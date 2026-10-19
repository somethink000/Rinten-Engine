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

	//Hands a closed frame over. Waits for the one before it, if that is somehow still going.
	internal static void Hand( in Job frame )
	{
		lock ( gate )
		{
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
			while ( handed ) Monitor.Wait( gate );
		}
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

			lock ( gate )
			{
				while ( !handed ) Monitor.Wait( gate );
				taken = job;
			}

			//Whatever happens in the driver, the hand-over is over: a worker that died holding it would leave every Drain waiting for ever under the render lock.
			try
			{
				Perform( ref taken );
			}
			catch ( System.Exception e )
			{
				taken.Presented = Result.ErrorUnknown;
				Log.Warning( e, "the present thread" );
			}

			lock ( gate )
			{
				answered.Add( taken );
				Volatile.Write( ref handed, false );
				Monitor.PulseAll( gate );
			}
		}
	}

	//The call the frame end used to make itself.
	private static void Perform( ref Job one )
	{
		var finished = one.Signal;
		var swapchain = one.Handle;
		var index = one.Image;
		var id = one.Id;

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

		CoreLatency.Presenting( id );
		one.Presented = VkDevice.khrSwapchain.QueuePresent( VkDevice.graphicsQueue, &present );
		CoreLatency.Presented( id );
	}
}
