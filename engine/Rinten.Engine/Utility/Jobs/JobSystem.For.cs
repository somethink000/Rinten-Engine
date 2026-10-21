using System.Threading;

namespace Rinten.Jobs;

internal static partial class JobSystem
{
	//One loop: body, state, next index, how many done; pooled per state type, so a loop allocates nothing
	private sealed class ForWork<TState> : Work
	{
		public TState State;
		public Action<TState, int> Body;
		public int Count, Batch, Next, Done;
		public Exception Failed;

		private static readonly Stack<ForWork<TState>> pool = new();

		public static ForWork<TState> Rent()
		{
			lock ( pool ) return pool.Count > 0 ? pool.Pop() : new ForWork<TState>();
		}

		public void Return()
		{
			State = default;
			Body = null;
			Failed = null;

			lock ( pool ) pool.Push( this );
		}

		//Takes batches until there are none left.
		public override void Run()
		{
			while ( true )
			{
				var start = Interlocked.Add( ref Next, Batch ) - Batch;
				if ( start >= Count ) return;

				var end = Math.Min( start + Batch, Count );

				try
				{
					for ( var i = start; i < end; i++ ) Body( State, i );
				}
				catch ( Exception e )
				{
					Interlocked.CompareExchange( ref Failed, e, null );
				}

				Interlocked.Add( ref Done, end - start );
			}
		}
	}

	/// <summary>Runs a body that keeps its own state - a delegate made once and kept - for every index below count.</summary>
	internal static void For( int count, Action<int> body, int most = -1 ) => For( count, body, static ( run, i ) => run( i ), most );

	//Runs body for every index below count on the workers and this thread; a throw comes back wrapped as Parallel's
	internal static void For<TState>( int count, TState state, Action<TState, int> body, int most = -1 )
	{
		if ( count <= 0 ) return;

		var helpers = Math.Min( Workers, count - 1 );
		if ( most > 0 ) helpers = Math.Min( helpers, most - 1 );

		if ( helpers <= 0 )
		{
			for ( var i = 0; i < count; i++ ) body( state, i );
			return;
		}

		var work = ForWork<TState>.Rent();

		work.State = state;
		work.Body = body;
		work.Count = count;
		work.Next = 0;
		work.Done = 0;

		//Four batches a thread, so one slow index does not leave the rest waiting for it.
		work.Batch = Math.Max( 1, count / ( ( helpers + 1 ) * 4 ) );

		Interlocked.Increment( ref work.Generation );
		Post( work, helpers );

		work.Run();

		//Only batches already running are left: another loop's work taken up here would only make this one later
		var spin = new SpinWait();
		while ( Volatile.Read( ref work.Done ) < count ) spin.SpinOnce( -1 );

		//Nobody may still be inside it when it goes back to be handed out again.
		Interlocked.Increment( ref work.Generation );
		while ( Volatile.Read( ref work.Inside ) > 0 ) Thread.SpinWait( 8 );

		var failed = work.Failed;
		work.Return();

		if ( failed is not null ) throw new AggregateException( failed );
	}
}
