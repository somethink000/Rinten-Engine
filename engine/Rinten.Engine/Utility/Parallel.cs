using System.Threading;

namespace Rinten.Utility;

/// <summary>Wrappers of the parallel class.</summary>
public static class Parallel
{
	//This is all wrapped so that if it turns out to be a bad idea to expose it, we can do an update where this just does a regular foreach.

	//A page has one thread: there System.Threading's loops are only their machinery around the same loop run in order.
	//The job system's loops need no such test - with no workers they run in order on their own
	internal static bool InOrder => OperatingSystem.IsBrowser();

	public static bool ForEach<T>( IEnumerable<T> source, Action<T> body )
	{
		if ( source is IList<T> items )
		{
			Rinten.Jobs.JobSystem.For( items.Count, (items, body), static ( s, i ) => s.body( s.items[i] ) );
			return true;
		}

		if ( InOrder )
		{
			foreach ( var item in source ) body( item );
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
		Rinten.Jobs.JobSystem.For( toExclusive - fromInclusive, (fromInclusive, body), static ( s, i ) => s.body( s.fromInclusive + i ) );
		return true;
	}

	//A list split as it is worked through rather than in fixed ranges - for work per item that is very uneven
	internal static void ForEach<T>( IList<T> list, ParallelOptions options, Action<T> body )
	{
		Rinten.Jobs.JobSystem.For( list.Count, (list, body), static ( s, i ) => s.body( s.list[i] ), options?.MaxDegreeOfParallelism ?? -1 );
	}

	//With a state each worker starts, carries through its items and hands back at the end - one state for the lot without workers
	internal static void For<TLocal>( int fromInclusive, int toExclusive, Func<TLocal> localInit,
		Func<int, ParallelLoopState, TLocal, TLocal> body, Action<TLocal> localFinally )
	{
		//A slice a thread, each with its own state throughout as Parallel gives a task one; nothing reads the loop state
		var count = toExclusive - fromInclusive;
		var slices = Math.Min( Rinten.Jobs.JobSystem.Workers + 1, Math.Max( count, 1 ) );

		Rinten.Jobs.JobSystem.For( slices, (fromInclusive, count, slices, localInit, body, localFinally), static ( s, slice ) =>
		{
			var start = s.fromInclusive + (int)( (long)s.count * slice / s.slices );
			var end = s.fromInclusive + (int)( (long)s.count * ( slice + 1 ) / s.slices );

			var local = s.localInit();
			for ( var i = start; i < end; i++ ) local = s.body( i, null, local );
			s.localFinally( local );
		} );
	}

	public static async Task ForAsync( int fromInclusive, int toExclusive, CancellationToken token, Func<int, CancellationToken, ValueTask> body )
	{
		await System.Threading.Tasks.Parallel.ForAsync( fromInclusive, toExclusive, new ParallelOptions { CancellationToken = token }, body );
	}
}
