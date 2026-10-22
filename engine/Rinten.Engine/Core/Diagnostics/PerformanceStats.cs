using System.Collections.Concurrent;
using System.Diagnostics;

namespace Rinten.Diagnostics;

public static partial class PerformanceStats
{
	public struct Block
	{
		public float FrameAvg;
		public float FrameMin;
		public float FrameMax;

		public long ByteAlloc;
		public int Gc0;
		public int Gc1;
		public int Gc2;
		public long GcPause;
	}

	/// <summary>Get the time taken, in seconds, that were required to process the previous frame.</summary>
	public static double FrameTime { get; internal set; }

	//On a page only while MeasureGpu is set, and only where the browser has a GPU timer (EXT_disjoint_timer_query_webgl2); nought where it has none.
	/// <summary>Latest available GPU frametime, in ms.</summary>
	public static float GpuFrametime { get; internal set; }

	/// <summary>Ms the loop worked last frame, excluding waits for display, cap or headset; FrameTime is the whole</summary>
	public static float FrameWork { get; internal set; }

	/// <summary>How many draws the last frame issued, where the renderer counts them - the web's.</summary>
	public static int FrameDraws { get; internal set; }

	/// <summary>Triangles the GPU was handed in the last counted frame, every pass of it; below nought where nothing counted them - see MeasureTriangles.</summary>
	public static long FrameTriangles { get; internal set; } = -1;

	/// <summary>Ask the renderer to count the triangles each frame hands the GPU, for something that is showing them. A query over the whole frame, kept off while nobody looks; desktop only.</summary>
	public static bool MeasureTriangles { get; set; }

	/// <summary>Ask the renderer to time the GPU and count draws, for something that is showing them. A page times the GPU with a query per frame and reads it back through the page, which is not free, so it is done only while this is set.</summary>
	public static bool MeasureGpu { get; set; }

	private static bool measuringGpu;

	/// <summary>Frame number of the last reported <see cref="GpuFrametime"/>.</summary>
	public static uint GpuFrameNumber { get; internal set; }

	/// <summary>What our renderer is holding on the graphics device - its textures and its buffers, in bytes. What we put there, not what the card is using: another process's memory is its own business and we cannot see it.</summary>
	public static ulong GpuMemoryUsage { get; internal set; }

	/// <summary>This process's share of the machine's processors over the last moment, as a percentage - a hundred is every core busy with us.</summary>
	public static float CpuUsage { get; internal set; }

	/// <summary>Main thread busy share of the last second, %: frame minus idle sleep and display/view waits</summary>
	public static float MainThreadUsage { get; internal set; }

	/// <summary>The same reading in milliseconds: how long the main thread was busy in an average frame of the last second, to sit beside the GPU's own figure.</summary>
	public static float MainThreadTime { get; internal set; }

	/// <summary>The frame rate cap the loop is pacing to right now, zero for none.</summary>
	public static float FrameRateCap { get; internal set; }

	/// <summary>Which setting <see cref="FrameRateCap"/> came from: fps_max, fps_max_inactive, fps_max_menu or vsync.</summary>
	public static string FrameRateCapSource { get; internal set; } = "";

	//This may not include allocations from threads other than the game thread.
	/// <summary>The number of bytes that were allocated on the managed heap in the last frame. <remarks>This may not include allocations from threads other than the game thread.</remarks></summary>
	public static long BytesAllocated { get; internal set; }

	/// <summary>Number of generation 0 (fastest) garbage collections were done in the last frame.</summary>
	public static int Gen0Collections { get; internal set; }

	/// <summary>Number of generation 1 (fast) garbage collections were done in the last frame.</summary>
	public static int Gen1Collections { get; internal set; }

	/// <summary>Number of generation 2 (slow) garbage collections were done in the last frame.</summary>
	public static int Gen2Collections { get; internal set; }

	/// <summary>How many ticks we paused in the last frame</summary>
	public static long GcPause { get; internal set; }

	/// <summary>Number of exceptions in the last frame.</summary>
	public static int Exceptions { get; internal set; }

	/// <summary>Approximate working set of this process.</summary>
	public static ulong ApproximateProcessMemoryUsage { get; internal set; }

	/// <summary>Performance statistics over the last period, which is dictated by "perf_time" console command.</summary>
	public static Block LastSecond { get; internal set; }

	private static Stopwatch frameTimer;
	private static Stopwatch secondTimer;
	private static List<Block> _history = new List<Block>( 1024 );
	private static long _prevAllocatedBytes;
	private static long _prevPauseTime;
	private static int _prevGen0, _prevGen1, _prevGen2;
	private static int _exceptions;
	private static int _lastSecond; //the actual rounded second of RealTime.Now when we last captured

	internal static bool Frame()
	{
		frameTimer ??= Stopwatch.StartNew();
		secondTimer ??= Stopwatch.StartNew();

		float frameMs = (float)frameTimer.Elapsed.TotalMilliseconds;

		PerformanceStats.FrameTime = frameTimer.Elapsed.TotalSeconds;
		frameTimer.Restart();

		UpdateGpuStats();
		UpdateProcessorUsage();

		var allocatedBytes = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
		PerformanceStats.BytesAllocated = allocatedBytes - _prevAllocatedBytes;
		_prevAllocatedBytes = allocatedBytes;

		var pauseTicks = GC.GetTotalPauseDuration().Ticks;
		GcPause = pauseTicks - _prevPauseTime;
		_prevPauseTime = pauseTicks;

		Timings.GcPause.AddMilliseconds( TimeSpan.FromTicks( GcPause ).TotalMilliseconds );

		var gen0 = GC.CollectionCount( 0 );
		var gen1 = GC.CollectionCount( 1 );
		var gen2 = GC.CollectionCount( 2 );
		PerformanceStats.Gen0Collections = gen0 - _prevGen0;
		PerformanceStats.Gen1Collections = gen1 - _prevGen1;
		PerformanceStats.Gen2Collections = gen2 - _prevGen2;
		_prevGen0 = gen0;
		_prevGen1 = gen1;
		_prevGen2 = gen2;

		Timings.FlipAll();
		FrameProfiler.Flip();

		//After the flip, so the idle this reads is the one that belongs to the frame just measured: the sleep happens
		//at the end of the loop and the frame timer above spans it, so the two cover the same stretch of clock.
		UpdateMainThreadUsage( frameMs );

		//The frame, said out loud. Everything else measured about this frame - our zones, PresentMon's presents, the
		//GPU's own count - is joined to it by this number, and nothing joins to a number that was never written down.
		EngineEventSource.Instance.Frame( (int)EngineLoop.LoopFrames, frameMs, BytesAllocated, (int)GpuFrameNumber );

		//how many exceptions happened between now and the last one
		Exceptions = Application.ExceptionCount - _exceptions;
		if ( Exceptions < 0 ) Exceptions = 0;
		_exceptions = Application.ExceptionCount;


		_history.Add( new Block
		{
			FrameAvg = frameMs,
			ByteAlloc = BytesAllocated,
			Gc0 = PerformanceStats.Gen0Collections,
			Gc1 = PerformanceStats.Gen1Collections,
			Gc2 = PerformanceStats.Gen2Collections,
			GcPause = GcPause
		} );

		var second = RealTime.Now.FloorToInt();
		if ( _lastSecond == second )
			return false;

		_lastSecond = second;

		//Part III's last row, in every run whether anybody asked or not: the simulation clock, the loop's clock and the wall, together. Two
		//of them agreeing is the only reason to believe the third, and a run where they disagree has measured a frame nobody lived through.
		EngineEventSource.Instance.Clocks( Time.NowDouble, RealTime.NowDouble,
			( DateTime.UtcNow - ProcessStartUtc ).TotalSeconds );

		var ls = new Block();
		ls.FrameAvg = _history.Average( x => x.FrameAvg );
		ls.FrameMin = _history.Min( x => x.FrameAvg );
		ls.FrameMax = _history.Max( x => x.FrameAvg );
		ls.ByteAlloc = _history.Sum( x => x.ByteAlloc );
		ls.Gc0 = _history.Sum( x => x.Gc0 );
		ls.Gc1 = _history.Sum( x => x.Gc1 );
		ls.Gc2 = _history.Sum( x => x.Gc2 );
		ls.GcPause = _history.Sum( x => x.GcPause );

		LastSecond = ls;

		//Main thread share over the same second as the row above, so it can't exceed the frame time
		if ( frameSum > 0.0 )
		{
			MainThreadTime = (float)( busySum / Math.Max( 1, _history.Count ) );
			MainThreadUsage = (float)Math.Clamp( busySum / frameSum * 100.0, 0.0, 100.0 );
		}

		busySum = 0.0;
		frameSum = 0.0;

		//Once a second, with the rest of this block, and not once a frame: on Linux Environment.WorkingSet opens and parses /proc/self/status,
		//and read every frame that was a syscall and a kilobyte of strings a frame for a number nothing reads faster than the report does.
		ApproximateProcessMemoryUsage = (ulong)Environment.WorkingSet;

		_history.Clear();
		secondTimer.Restart();

		//Everything in a frame's stats is counted by the native scene system, resource system and render device.
		//None of them are here to be counted where it is not loaded, and the block keeps its last values.

		return true;
	}

	//GPU stats from the viewport being looked at; read only after its fence passed, so a frame or two old
	private static void UpdateGpuStats()
	{
		//A page's frames are timed only while somebody is looking - see MeasureGpu. The last ask with the wish off tells the page to stop.
		if ( OperatingSystem.IsBrowser() )
		{
			//What the backend knows it made, which it counts whether or not anyone asks.
			GpuMemoryUsage = (ulong)Math.Max( 0, BrowserDevice.trackedTextureBytes + BrowserDevice.trackedBufferBytes );

			if ( !MeasureGpu && !measuringGpu ) return;

			measuringGpu = MeasureGpu;

			var ms = BrowserNative.GpuTake( MeasureGpu );
			FrameDraws = BrowserNative.DrawsTake();

			//Nought is "nothing finished since the last ask" - a query lands a frame or two late and sometimes two at once - so the last reading stands.
			if ( ms > 0 )
			{
				GpuFrametime = (float)ms;
				GpuFrameNumber++;
			}

			return;
		}

		if ( CoreRenderDevice.ReadFrameSummary( CoreRenderProfiler.LastViewport ) is not { } frame )
			return;

		//The same measurement read twice is the same frame, and anything keeping a history of them would otherwise count it twice
		if ( frame.GpuMs != GpuFrametime )
		{
			GpuFrametime = frame.GpuMs;
			GpuFrameNumber++;
		}

		GpuMemoryUsage = (ulong)Math.Max( 0, frame.TextureBytes + frame.BufferBytes );
		FrameTriangles = MeasureTriangles ? frame.Triangles : -1;
	}

	//How much of the machine's processors this process is using, from how much processor time it got between two readings. Sampled a couple of times a
	//second rather than every frame: it is a ratio between two clocks, and over a frame neither has moved far enough for the answer to mean anything.
	private static void UpdateProcessorUsage()
	{
		if ( thisProcess is null ) return;

		if ( cpuTimer is null )
		{
			cpuTimer = Stopwatch.StartNew();
			cpuTimeAtLastSample = thisProcess.TotalProcessorTime;
			return;
		}

		var elapsed = cpuTimer.Elapsed.TotalSeconds;
		if ( elapsed < CpuSampleInterval ) return;

		var processorTime = thisProcess.TotalProcessorTime;
		var used = (processorTime - cpuTimeAtLastSample).TotalSeconds;

		cpuTimeAtLastSample = processorTime;
		cpuTimer.Restart();

		var cores = Environment.ProcessorCount;
		if ( cores <= 0 ) return;

		CpuUsage = (float)Math.Clamp( used / (elapsed * cores) * 100.0, 0.0, 100.0 );
	}

	private const double CpuSampleInterval = 0.5;

	//Main thread busy share and ms, smoothed over a quarter second by time, not by frame count
	private static void UpdateMainThreadUsage( float frameMs )
	{
		if ( frameMs <= 0.0f ) return;

		//Parked time: cap sleep, wait for a display image, and wait on a view's fence
		var parkedMs = Latest( Timings.Idle ) + Latest( waitingForDisplay ) + Latest( waitingForView );

		var busyMs = Math.Clamp( frameMs - parkedMs, 0.0f, frameMs );

		busySum += busyMs;
		frameSum += frameMs;
	}

	//The frame just flipped, out of one of the timings the loop keeps.
	private static float Latest( Timings timing ) =>
		timing.History.Size > 0 ? timing.History[0].TotalMs : 0.0f;

	//Wait timings by scope name; ui wait display is inside ui acquire, so it is not counted again
	private static readonly Timings waitingForDisplay = Timings.Get( "ui acquire" );
	private static readonly Timings waitingForView = Timings.Get( "view begin" );

	//What the frames of the second being counted came to, busy and whole.
	private static double busySum;
	private static double frameSum;

	//When this process started, by the wall clock - the third, independent witness the other two clocks are checked against.
	internal static readonly DateTime ProcessStartUtc =
		OperatingSystem.IsBrowser() ? DateTime.UtcNow : Process.GetCurrentProcess().StartTime.ToUniversalTime();

	private static readonly Process thisProcess = OperatingSystem.IsBrowser() ? null : Process.GetCurrentProcess();
	private static Stopwatch cpuTimer;
	private static TimeSpan cpuTimeAtLastSample;

	public record struct PeriodMetric( float Min, float Max, float Avg, int Calls );
}


internal static class ObjectPool<T> where T : class, new()
{
	public static T Get()
	{
		if ( _pool.TryDequeue( out var o ) )
			return o;

		return new T();
	}

	public static void Return( T obj )
	{
		_pool.Enqueue( obj );
	}

	static ConcurrentQueue<T> _pool = new();
}
