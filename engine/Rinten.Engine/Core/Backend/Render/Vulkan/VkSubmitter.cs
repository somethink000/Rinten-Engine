using System.Threading;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Rinten;

//Runs a window's present on a worker thread; queue users wait for idle; results read at Settle
internal static unsafe class VkSubmitter
{
	//One frame's present, as the recording thread handed it over.
	internal struct Job
	{
		public VkSemaphore Signal;
		public SwapchainKHR Handle;
		public uint Image;

		/// <summary>The swapchain it belongs to - read and written by <see cref="Settle"/> alone.</summary>
		public VkSwapchain.Swapchain Owner;

		/// <summary>Its present id, nought for none - see CorePresent.</summary>
		public ulong Id;

		public Result Presented;

		/// <summary>A generated frame shown first, and how long after it this one is - see VkFrame.Generation.</summary>
		public bool Generated;
		public VkSemaphore GeneratedSignal;
		public uint GeneratedImage;
		public long Spacing;
		public Result GeneratedPresented;
	}

	private static readonly object gate = new();

	//A job handed over and not yet taken back by the worker.
	private static bool handed;
	private static Job job;

	//The worker's answers, waiting for the recording thread - see Settle.
	private static readonly List<Job> answered = new();

	private static Thread thread;

	//Whether frames are handed over at all - see r_submit_thread.
	internal static bool Enabled => Rinten.Rendering.DrawSettings.SubmitThread;

	//Whether anything is in the worker's hands - cheap to ask, and the whole of what most callers need.
	internal static bool Busy => Volatile.Read( ref handed );

	//The worker between a generated frame's present and its own: what is left waits for its time and the render lock, so the
	//recording thread, which holds that lock, may use the queue meanwhile - see Yield. Each hand-over is numbered, so a job whose
	//present was made by another thread first is known to be over - see TakeOver.
	private static bool parked;
	private static long handing;

	//Hands a closed frame over. Waits for the one before it, if that is somehow still going - or, where it is only waiting to be
	//shown, shows it now: the frame after it is already here.
	internal static void Hand( in Job frame )
	{
		lock ( gate )
		{
			if ( parked ) TakeOver();

			while ( handed ) Monitor.Wait( gate );

			if ( thread is null )
			{
				thread = new Thread( Run )
				{
					Name = "vk submit",
					IsBackground = true,

					//Above the frame's own thread's peers: what it holds up is the card.
					Priority = ThreadPriority.AboveNormal,
				};

				thread.Start();
			}

			job = frame;
			handing++;
			Volatile.Write( ref handed, true );
			Monitor.PulseAll( gate );
		}
	}

	//Until nothing is in the worker's hands: what every other user of the queue and of the swapchain has to do first. Any thread; nothing if it is idle.
	internal static void Drain()
	{
		if ( !Volatile.Read( ref handed ) ) return;

		lock ( gate )
		{
			if ( parked ) TakeOver();

			while ( handed ) Monitor.Wait( gate );
		}
	}

	/// <summary>Until the worker is not inside a call to the queue - a job left waiting to be shown stays waiting. Only under the render lock, which that job takes before it touches the queue.</summary>
	internal static void Yield()
	{
		if ( !Volatile.Read( ref handed ) ) return;

		lock ( gate )
		{
			while ( handed && !parked ) Monitor.Wait( gate );
		}
	}

	//The parked job's own present made here and now, and the job over. Under the gate.
	private static void TakeOver()
	{
		parked = false;

		CoreLatency.Presenting( job.Id );
		job.Presented = Present( job.Signal, job.Handle, job.Image, job.Id );
		CoreLatency.Presented( job.Id );

		answered.Add( job );
		handing++;
		Volatile.Write( ref handed, false );
		Monitor.PulseAll( gate );
	}

	//What the worker's calls answered, acted on the way the frame end used to act on them. The recording thread, under the render lock, after a Drain.
	internal static void Settle()
	{
		lock ( gate )
		{
			if ( answered.Count == 0 ) return;

			foreach ( var one in answered ) Act( one );
			answered.Clear();
		}
	}

	private static void Act( in Job one )
	{
		var owner = one.Owner;
		if ( owner is null ) return;

		if ( one.Generated && one.GeneratedPresented != Result.Success )
		{
			var generated = one;
			generated.Generated = false;
			generated.Presented = one.GeneratedPresented;
			Act( generated );
		}

		switch ( one.Presented )
		{
			case Result.Success:
				owner.Presented++;
				break;

			case Result.ErrorOutOfDateKhr:
				owner.OutOfDate = true;
				break;

			case Result.SuboptimalKhr:
				VkSwapchain.MarkSuboptimal( owner );
				break;

			case Result.ErrorSurfaceLostKhr:
				//Rebuilt when this swapchain next begins a frame - the rebuild works on whichever is current. See VkFrame.FrameBegin.
				owner.SurfaceLost = true;
				break;

			default:
				VkDevice.SetText( "the frame would not present" );
				VkDevice.MarkLostIf( one.Presented );
				break;
		}
	}

	//The device idle, after whatever the worker holds - what every device-wide wait
	//in the backend goes through, because Vulkan wants every queue kept apart for it.
	internal static Result DeviceWaitIdle()
	{
		Drain();
		return VkDevice.vk.DeviceWaitIdle( VkDevice.device );
	}

	private static void Run()
	{
		while ( true )
		{
			Job taken;
			long number;

			lock ( gate )
			{
				while ( !handed ) Monitor.Wait( gate );
				taken = job;
				number = handing;
			}

			//Whatever happens in the driver, the hand-over is over: a worker that died holding it would leave every Drain waiting for ever under the render lock.
			try
			{
				if ( !Perform( ref taken, number ) ) continue;
			}
			catch ( System.Exception e )
			{
				taken.Presented = Result.ErrorUnknown;
				Log.Warning( e, "the present thread" );
			}

			lock ( gate )
			{
				parked = false;
				answered.Add( taken );
				Volatile.Write( ref handed, false );
				Monitor.PulseAll( gate );
			}
		}
	}

	//The call the frame end used to make itself. False where another thread made the frame's own present first - see TakeOver.
	private static bool Perform( ref Job one, long number )
	{
		if ( one.Generated )
		{
			var began = System.Diagnostics.Stopwatch.GetTimestamp();

			one.GeneratedPresented = Present( one.GeneratedSignal, one.Handle, one.GeneratedImage, 0 );

			if ( one.Spacing > 0 )
			{
				lock ( gate )
				{
					job.GeneratedPresented = one.GeneratedPresented;
					parked = true;
					Monitor.PulseAll( gate );

					//Asleep on the gate rather than the clock, so a take-over ends it at once
					while ( handing == number )
					{
						var left = began + one.Spacing - System.Diagnostics.Stopwatch.GetTimestamp();
						if ( left <= 0 ) break;

						Monitor.Wait( gate, TimeSpan.FromTicks( Math.Max( left * TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency, 1 ) ) );
					}

					if ( handing != number ) return false;
				}

				//The queue only with the render lock: the recording thread may be using it, having yielded
				lock ( VkDevice.renderLock )
				{
					lock ( gate )
					{
						if ( handing != number ) return false;
						parked = false;
					}

					CoreLatency.Presenting( one.Id );
					one.Presented = Present( one.Signal, one.Handle, one.Image, one.Id );
					CoreLatency.Presented( one.Id );
				}

				return true;
			}
		}

		CoreLatency.Presenting( one.Id );
		one.Presented = Present( one.Signal, one.Handle, one.Image, one.Id );
		CoreLatency.Presented( one.Id );

		return true;
	}

	/// <summary>One image of a swapchain to the display once its signal is, with its present id where it has one.</summary>
	internal static Result Present( VkSemaphore finished, SwapchainKHR swapchain, uint index, ulong id )
	{
		var present = new PresentInfoKHR
		{
			SType = StructureType.PresentInfoKhr,
			WaitSemaphoreCount = 1,
			PWaitSemaphores = &finished,
			SwapchainCount = 1,
			PSwapchains = &swapchain,
			PImageIndices = &index,
		};

		PresentIdKHR told;
		CorePresent.Stamp( &present, &told, &id );

		return VkDevice.khrSwapchain.QueuePresent( VkDevice.graphicsQueue, &present );
	}

	/// <summary>Until a stopwatch time: asleep for most of it, spinning the last millisecond, which a sleep cannot hit.</summary>
	internal static void Hold( long until )
	{
		while ( true )
		{
			var left = until - System.Diagnostics.Stopwatch.GetTimestamp();
			if ( left <= 0 ) return;

			var ms = left * 1000 / System.Diagnostics.Stopwatch.Frequency;

			if ( ms > 1 ) Thread.Sleep( (int)( ms - 1 ) );
			else Thread.SpinWait( 64 );
		}
	}
}
