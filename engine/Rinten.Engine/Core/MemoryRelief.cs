namespace Rinten;

//One compacting collection after a big piece of work - a world's terrain baked, a scene loaded - threw away gigabytes of
//large arrays. The large-object heap is otherwise never compacted, and its holes stay held as memory the game uses
internal static class MemoryRelief
{
	//At most this often, whoever asks
	private const double Seconds = 20.0;

	private static double lastAt = double.NegativeInfinity;

	/// <summary>Compacts every generation and the large-object heap now, unless it was done in the last few seconds.</summary>
	internal static void AfterBigWork( string why )
	{
		var now = RealTime.NowDouble;
		if ( now - lastAt < Seconds ) return;

		lastAt = now;

		var before = GC.GetTotalMemory( false );
		var clock = System.Diagnostics.Stopwatch.StartNew();

		System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
		GC.Collect( 2, GCCollectionMode.Forced, true, true );

		Log.Info( $"Memory: compacted after {why} - {before >> 20} MB to {GC.GetTotalMemory( false ) >> 20} MB in {clock.Elapsed.TotalMilliseconds:0} ms" );
	}
}
