using System.Threading;

namespace Rinten.Jobs;

//Worker threads that run the engine's parallel loops and its scheduled jobs, made once and kept. The thread that asks always
//runs its share, so a loop inside a loop never waits on a worker that is waiting on it; and nothing is allocated once warm
internal static partial class JobSystem
{
	//One piece of queued work, as a worker sees it: run it if it is still the same piece - see Work.Generation.
	private struct Entry
	{
		public Work Work;
		public int Generation;
	}

	/// <summary>Something a worker can run, kept in a pool and handed out again; a run of it is told apart from the next by its generation.</summary>
	internal abstract class Work
	{
		public int Generation;

		//Workers inside Run now - a piece is given back to its pool only once none are.
		public int Inside;

		public abstract void Run();
	}

	private static readonly Lock queueLock = new();
	private static Entry[] queue = new Entry[256];
	private static int head, tail, queued;

	private static readonly SemaphoreSlim wake = new( 0 );
	private static Thread[] workers = [];
	private static int asleep;

	/// <summary>How many threads besides the caller's take a share of a loop - nought where there are none, as on a page.</summary>
	internal static int Workers => workers.Length;

	static JobSystem()
	{
		if ( OperatingSystem.IsBrowser() ) return;

		var count = Math.Clamp( Environment.ProcessorCount - 1, 1, 31 );
		workers = new Thread[count];

		for ( var i = 0; i < count; i++ )
		{
			workers[i] = new Thread( Loop ) { Name = $"Jobs {i}", IsBackground = true, Priority = ThreadPriority.AboveNormal };
			workers[i].Start();
		}
	}

	//Queues one piece for as many workers as are to help with it, and wakes that many.
	internal static void Post( Work work, int helpers )
	{
		if ( helpers <= 0 || workers.Length == 0 ) return;

		lock ( queueLock )
		{
			if ( queued + helpers > queue.Length )
			{
				var grown = new Entry[Math.Max( queue.Length * 2, queued + helpers )];
				for ( var i = 0; i < queued; i++ ) grown[i] = queue[( head + i ) % queue.Length];

				queue = grown;
				head = 0;
				tail = queued;
			}

			for ( var i = 0; i < helpers; i++ )
			{
				queue[tail] = new Entry { Work = work, Generation = work.Generation };
				tail = ( tail + 1 ) % queue.Length;
				queued++;
			}
		}

		var sleeping = Volatile.Read( ref asleep );
		if ( sleeping > 0 ) wake.Release( Math.Min( sleeping, helpers ) );
	}

	private static bool TryTake( out Entry entry )
	{
		lock ( queueLock )
		{
			if ( queued == 0 )
			{
				entry = default;
				return false;
			}

			entry = queue[head];
			queue[head] = default;
			head = ( head + 1 ) % queue.Length;
			queued--;

			return true;
		}
	}

	//Runs one queued piece on this thread, if there is one still current - what a worker does, and what a caller waiting on something does meanwhile.
	internal static bool RunOne()
	{
		if ( !TryTake( out var entry ) ) return false;

		var work = entry.Work;

		//In first, then checked: a piece given back and handed out again since it was queued is not this one any more.
		Interlocked.Increment( ref work.Inside );

		try
		{
			if ( Volatile.Read( ref work.Generation ) == entry.Generation ) work.Run();
		}
		finally
		{
			Interlocked.Decrement( ref work.Inside );
		}

		return true;
	}

	private static void Loop()
	{
		var spin = new SpinWait();

		while ( true )
		{
			if ( RunOne() )
			{
				spin.Reset();
				continue;
			}

			//A little spinning first: loops come in bursts a frame, and a thread put to sleep between two of them costs more than it saves.
			if ( !spin.NextSpinWillYield )
			{
				spin.SpinOnce();
				continue;
			}

			Interlocked.Increment( ref asleep );

			//Looked at again once counted as asleep, so a piece posted in between is not left until the next one.
			if ( Volatile.Read( ref queued ) == 0 ) wake.Wait( 50 );

			Interlocked.Decrement( ref asleep );
			spin.Reset();
		}
	}
}
