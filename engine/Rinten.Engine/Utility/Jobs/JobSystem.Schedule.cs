using System.Threading;

namespace Rinten.Jobs;

/// <summary>A scheduled job, to wait for or to make another wait for. The default handle is a job long done.</summary>
internal readonly struct JobHandle
{
	internal readonly JobSystem.Job Job;

	internal JobHandle( JobSystem.Job job ) => Job = job;

	/// <summary>Whether it has run.</summary>
	public bool IsDone => Job is null || Job.IsDone;

	/// <summary>Waits until it has run, running queued work meanwhile; throws what it threw.</summary>
	public void Complete() => Job?.Complete();
}

internal static partial class JobSystem
{
	/// <summary>One scheduled piece of work: runs once, once what it waits on has, and then lets go of what waits on it.</summary>
	internal abstract class Job : Work
	{
		private int started, finished, waitingOn;
		private List<Job> after;
		private Exception failed;

		public bool IsDone => Volatile.Read( ref finished ) != 0;

		protected abstract void Execute();

		//Counted as one more thing to wait for until scheduling is over, so a dependency finishing meanwhile cannot start it early.
		internal void Arm() => waitingOn = 1;

		//Waits on another job: false where that one is already done.
		internal bool Follow( Job before )
		{
			lock ( before )
			{
				if ( before.IsDone ) return false;

				( before.after ??= new() ).Add( this );
				Interlocked.Increment( ref waitingOn );

				return true;
			}
		}

		//One of what it waits on is done, or scheduling is: posted once nothing is left.
		internal void Release()
		{
			if ( Interlocked.Decrement( ref waitingOn ) != 0 ) return;

			if ( Workers == 0 ) Run();
			else Post( this, 1 );
		}

		public override void Run()
		{
			if ( Interlocked.Exchange( ref started, 1 ) != 0 ) return;

			try
			{
				Execute();
			}
			catch ( Exception e )
			{
				failed = e;
			}

			List<Job> next;

			lock ( this )
			{
				Volatile.Write( ref finished, 1 );
				next = after;
				after = null;
			}

			if ( next is null ) return;

			foreach ( var job in next ) job.Release();
		}

		public void Complete()
		{
			var spin = new SpinWait();

			while ( !IsDone )
			{
				if ( !RunOne() ) spin.SpinOnce( -1 );
			}

			if ( failed is not null ) throw new AggregateException( failed );
		}
	}

	private sealed class Job<TState> : Job
	{
		public TState State;
		public Action<TState> Body;

		protected override void Execute() => Body( State );
	}

	/// <summary>Runs body on a worker once what it waits on has run; on a page, with no workers, on this thread.</summary>
	internal static JobHandle Schedule<TState>( TState state, Action<TState> body, JobHandle after = default, JobHandle andAfter = default )
	{
		var job = new Job<TState> { State = state, Body = body };

		job.Arm();

		if ( after.Job is { } first ) job.Follow( first );
		if ( andAfter.Job is { } second ) job.Follow( second );

		job.Release();

		return new JobHandle( job );
	}
}
