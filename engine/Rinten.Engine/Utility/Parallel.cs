using System.Threading;

namespace Rinten.Utility;

/// <summary>Wrappers of the parallel class.</summary>
public static class Parallel
{
	//This is all wrapped so that if it turns out to be a bad idea to expose it, we can do an update where this just does a regular foreach.

	//Whether there are threads to fan out to. A page has one (plan В5): there a parallel loop is only its machinery -
	//partitioners, tasks, closures, the most of a particle scene's garbage at 1.8 MB a frame - around the same loop run in order.
	internal static bool InOrder => OperatingSystem.IsBrowser();

	public static bool ForEach<T>( IEnumerable<T> source, Action<T> body )
	{
		if ( InOrder )
		{
			if ( source is IList<T> list )
			{
				for ( int i = 0; i < list.Count; i++ ) body( list[i] );
			}
			else
			{
				foreach ( var item in source ) body( item );
			}

			return true;
		}

		var r = System.Threading.Tasks.Parallel.ForEach( source, body );
		return r.IsCompleted;
	}

	public static bool ForEach<T>( IEnumerable<T> source, CancellationToken token, Action<T> body )
	{
		if ( InOrder )
		{
			foreach ( var item in source )
			{
				if ( token.IsCancellationRequested ) return false;
				body( item );
			}

			return true;
		}

		var r = System.Threading.Tasks.Parallel.ForEach( source, new ParallelOptions { CancellationToken = token }, body );
		return r.IsCompleted;
	}

	public static bool For( int fromInclusive, int toExclusive, Action<int> body )
	{
		if ( InOrder )
		{
			for ( int i = fromInclusive; i < toExclusive; i++ ) body( i );
			return true;
		}

		var r = System.Threading.Tasks.Parallel.For( fromInclusive, toExclusive, body );
		return r.IsCompleted;
	}

	//A list split as it is worked through rather than in fixed ranges - for work per item that is very uneven
	internal static void ForEach<T>( IList<T> list, ParallelOptions options, Action<T> body )
	{
		if ( InOrder )
		{
			for ( int i = 0; i < list.Count; i++ ) body( list[i] );
			return;
		}

		System.Threading.Tasks.Parallel.ForEach( System.Collections.Concurrent.Partitioner.Create( list, loadBalance: true ), options ?? DefaultOptions, body );
	}

	private static readonly ParallelOptions DefaultOptions = new();

	//With a state each worker starts, carries through its items and hands back at the end - one state for the lot where it runs in order
	internal static void For<TLocal>( int fromInclusive, int toExclusive, Func<TLocal> localInit,
		Func<int, ParallelLoopState, TLocal, TLocal> body, Action<TLocal> localFinally )
	{
		if ( InOrder )
		{
			var local = localInit();
			for ( int i = fromInclusive; i < toExclusive; i++ ) local = body( i, null, local );
			localFinally( local );
			return;
		}

		System.Threading.Tasks.Parallel.For( fromInclusive, toExclusive, localInit, body, localFinally );
	}

	public static async Task ForAsync( int fromInclusive, int toExclusive, CancellationToken token, Func<int, CancellationToken, ValueTask> body )
	{
		await System.Threading.Tasks.Parallel.ForAsync( fromInclusive, toExclusive, new ParallelOptions { CancellationToken = token }, body );
	}
}
