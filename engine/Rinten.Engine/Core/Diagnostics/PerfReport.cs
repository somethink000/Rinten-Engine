using System.Text;

namespace Rinten.Diagnostics;

//-perfreport N: measures a live editor for N seconds, writes logs/perfreport.txt and exits
internal static class PerfReport
{
	private static readonly int seconds = Rinten.Utility.CommandLine.GetSwitchInt( "-perfreport", 0 );

	//-perfframes: measure a frame count instead of seconds, so A/B runs compare equal samples
	private static readonly int wantedFrames = Rinten.Utility.CommandLine.GetSwitchInt( "-perfframes", 0 );

	//Whether this run was asked to measure anything at all.
	private static bool Asked => seconds > 0 || wantedFrames > 0;

	//-perfwarmup: seconds to wait after the first frame before measuring (default 30, past JIT/compiler)
	private static readonly int warmup = Rinten.Utility.CommandLine.GetSwitchInt( "-perfwarmup", 30 );

	//-perfwindowshot: path to save the actual presented window image (PPM plus alpha PGM)
	private static string WindowShot =>
		Rinten.Utility.CommandLine.GetSwitch( "-perfwindowshot", "" ).TrimQuoted();

	//-perfwindowscan: counts opaque window pixels per frame, keeps best/worst; diagnosis only
	private static readonly bool WindowScan = Rinten.Utility.CommandLine.HasSwitch( "-perfwindowscan" ) ||
		Rinten.Utility.CommandLine.HasSwitch( "-perfwindowflicker" ) ||
		Rinten.Utility.CommandLine.HasSwitch( "-perfwindowseq" );

	//-perfwindowseq: folder for half-size window frames (first SequenceFrames only); diagnosis only
	private static readonly string Sequence = Rinten.Utility.CommandLine.GetSwitch( "-perfwindowseq", "" ).TrimQuoted();

	//How many frames Sequence keeps.
	private const int SequenceFrames = 150;

	private static int sequenceWritten;

	//-perfwindowflicker: keeps each pixel's min/max over the run to show flicker; diagnosis only
	private static readonly bool WindowFlicker = Rinten.Utility.CommandLine.HasSwitch( "-perfwindowflicker" );

	//-perfwindowflickerevery: read back one frame in N for the flicker span (default 3; reads are slow)
	private static readonly int FlickerEvery =
		Math.Max( 1, Rinten.Utility.CommandLine.GetSwitchInt( "-perfwindowflickerevery", 3 ) );

	private static long flickerSkip;

	private static byte[] flickerLow, flickerHigh;
	private static int flickerWide, flickerTall;
	private static long flickerFrames;

	private static byte[] scanPixels;

	//The frame that came out least opaque, kept whole so that -perfwindowshot photographs the fault rather than whichever frame the run ended on.
	private static byte[] scanWorstPixels;
	private static int scanWorstWide;
	private static int scanWorstTall;

	private static long scanOpaqueBest = -1;
	private static long scanOpaqueWorst = -1;
	private static long scanFrames;

	//When the first window frame went up, so the warm-up can be waited out.
	private static double firstFrameAt;

	//Whether the switch has been reported to the log yet.
	private static bool announced;

	private static double armedAt;
	private static long framesAtArm;
	private static long loopsAtArm;
	private static long attemptsAtArm;
	private static long refusalsAtArm;
	private static bool done;

	//The three clocks as they stood when measuring began.
	private static double gameAtArm;
	private static DateTime wallAtArm;

	//Every frame time in the window, kept whole so the report can give typical and worst frames
	private static readonly List<double> frameMs = new( 8192 );

	//What was capping the frame rate when the window began, since measuring lifts the cap and the answer at the end is always "perfreport".
	private static double capAtArm;
	private static string capSourceAtArm = "";

	//Per-category totals accumulated over the window; the categories only keep a short rolling history
	private static readonly Dictionary<string, (double Ms, int Calls)> categories = new();

	//Profiler tree totals accumulated over the window by stable id, not read smoothed at the end
	private readonly record struct TreeTotal( string Name, int Depth, double Ms, double SelfMs,
		long Calls, long AllocBytes, long SelfAllocBytes, int Frames );

	private static readonly Dictionary<int, TreeTotal> tree = new();

	//Record the profiler tree: RINTEN_PERF_TREE=1 or -perftree (pages have no env); read when arming
	private static bool WantsTree => System.Environment.GetEnvironmentVariable( "RINTEN_PERF_TREE" ) == "1"
		|| Rinten.Utility.CommandLine.HasSwitch( "-perftree" );

	//The tree's zones in the order they were first seen - see Write.
	private static readonly List<int> treeOrder = new();

	//Sampled allocations by type over the window; proportions are sound, totals are an estimate
	private static Allocations.Scope allocations;

	/// <summary>What the one collection after the project compiled gave back, in words.</summary>
	public static string Reclaimed { get; set; }

	//What the viewports did with requested frames: drawn, skipped for budget, or GPU behind
	private static void WriteViewports( System.Text.StringBuilder report, double seconds )
	{
		var budgeted = CoreScenePreview.skippedDraws - viewsSkippedAt;
		var busy = CoreScenePreview.skippedBusy - viewsBusyAt;
		var drew = CoreScenePreview.drawnViews - viewsDrewAt;

		if ( budgeted <= 0 && busy <= 0 && drew <= 0 ) return;

		seconds = Math.Max( 0.001, seconds );

		report.AppendLine();
		report.AppendLine( "-- viewports ------------------------------------------------------------" );
		report.AppendLine( $"  {drew / seconds:F0} views drawn a second, of {( drew + budgeted + busy ) / seconds:F0} asked" );
		report.AppendLine( $"  {budgeted / seconds:F0} draws a second left for later by the budget, " +
			$"{busy / seconds:F0} because the card had not finished the last one" );
	}

	//What the viewports had counted when the window opened.
	private static long viewsSkippedAt;
	private static long viewsBusyAt;
	private static long viewsDrewAt;

	//And what the mesh passes had counted, and the editor's labels.
	private static long meshCullsAtArm, meshListDrawsAtArm, meshMaterialDrawsAtArm;
	private static long particleTicksAtArm, particleRunsAtArm;
	private static long particleAskedAtArm, particleHeldAtArm;
	private static long textLabelsAtArm, textMadeAtArm, textGoneAtArm;

	//And what the interface had counted of its shortcuts: editor frames put out again and
	//walked, why the rest were not, hover tests run and left out, binds polled and put off.
	private static long framesReusedAtArm, framesWalkedAtArm, refusedGenerationAtArm, refusedLiveAtArm,
		refusedResourcesAtArm, refusedTexturesAtArm, refusedSlotsAtArm, refusedPicturesAtArm;
	private static long hoverWalksAtArm, hoverSkippedAtArm, bindPollsAtArm, bindDeferralsAtArm;

	//Device buffer bytes at arming, to spot levels still building geometry during measurement
	private static long bufferBytesAtArm;

	//What the particle step had counted when the window opened - see ParticleGameSystem.Stepped.
	private static long particlesAtArm, particleEffectsAtArm, particleStepsAtArm;

	//How many charts of the sun had been traced as the window opened, and how often the world was built for the rays.
	private static long bakesAtArm;

	//The text counters as the window opened - see the text lines of the report
	private static Rinten.UI.TextStats.Snapshot textAtArm;
	private static long pagesAtArm;
	private static long lampFacesAtArm;

	//The collector's counts as the window opened.
	private static long allocatedAtArm;
	private static int gen0AtArm, gen1AtArm, gen2AtArm;
	private static TimeSpan gcPauseAtArm;

	//Whether a measurement is running; the loop ignores the frame cap while it is
	internal static bool Measuring { get; private set; }

	//Frame cap off for the whole measured run including warm-up; the original cap is still reported
	internal static bool Uncapped { get; private set; }

	//What the window presented, into a buffer of ours.
	private static unsafe int ReadWindowPixels( ulong handle, byte[] pixels )
	{
		fixed ( byte* data = pixels ) return CoreRender.WindowRead( handle, data, pixels.Length );
	}

	//The world's layer, where there is one, into a window's pixels at its place - see CoreUiWindow.MainLayer.
	private static void PasteLayer( byte[] pixels, int wide, int tall )
	{
		var (layer, left, top) = CoreUiWindow.MainLayer;
		if ( layer == 0 ) return;

		var (across, down) = CoreRender.WindowReadSize( layer );
		if ( across <= 0 || down <= 0 ) return;

		var over = new byte[across * down * 4];
		if ( ReadWindowPixels( layer, over ) != over.Length ) return;

		for ( var y = 0; y < down; y++ )
		{
			var row = top + y;
			if ( row < 0 || row >= tall ) continue;

			for ( var x = 0; x < across; x++ )
			{
				var column = left + x;
				if ( column < 0 || column >= wide ) continue;

				var from = ( ( y * across ) + x ) * 4;
				var to = ( ( row * wide ) + column ) * 4;

				pixels[to] = over[from];
				pixels[to + 1] = over[from + 1];
				pixels[to + 2] = over[from + 2];
				pixels[to + 3] = 255;
			}
		}
	}

	//Counts opaque pixels in this frame's window, tracking the run's best and worst (see WindowScan)
	private static void ScanWindow()
	{
		//The span's own stride: the read below is the expensive part of this, and there is no reason to pay it on a frame nothing will look
		//at. Only where the span is the whole reason the scan is running - a run asking for the least opaque frame wants every one of them.
		if ( WindowFlicker && FlickerEvery > 1 && ( flickerSkip++ % FlickerEvery ) != 0 ) return;

		var handle = CoreUiWindow.MainSwapchain;
		var (wide, tall) = CoreRender.WindowReadSize( handle );

		if ( wide <= 0 || tall <= 0 ) return;

		var need = wide * tall * 4;

		if ( scanPixels is null || scanPixels.Length != need ) scanPixels = new byte[need];

		if ( ReadWindowPixels( handle, scanPixels ) != need ) return;

		long opaque = 0;

		for ( var i = 3; i < need; i += 4 )
		{
			if ( scanPixels[i] == 255 ) opaque++;
		}

		scanFrames++;

		if ( WindowFlicker && Measuring ) Flicker( wide, tall );

		if ( Measuring && !string.IsNullOrWhiteSpace( Sequence ) && sequenceWritten < SequenceFrames ) WriteSequenceFrame( wide, tall );

		if ( scanOpaqueBest < 0 || opaque > scanOpaqueBest ) scanOpaqueBest = opaque;

		if ( scanOpaqueWorst < 0 || opaque < scanOpaqueWorst )
		{
			scanOpaqueWorst = opaque;

			//Kept, because the shape of what was left unwritten is the whole diagnosis and the last frame of a run is rarely the worst one.
			if ( scanWorstPixels is null || scanWorstPixels.Length != need ) scanWorstPixels = new byte[need];

			System.Array.Copy( scanPixels, scanWorstPixels, need );

			scanWorstWide = wide;
			scanWorstTall = tall;
		}
	}

	//The window as it was this frame, at half size, into the next file of Sequence.
	private static void WriteSequenceFrame( int wide, int tall )
	{
		var halfWide = wide / 2;
		var halfTall = tall / 2;
		var rgb = new byte[halfWide * halfTall * 3];

		for ( var y = 0; y < halfTall; y++ )
		{
			for ( var x = 0; x < halfWide; x++ )
			{
				var from = ( ( y * 2 * wide ) + ( x * 2 ) ) * 4;
				var to = ( ( y * halfWide ) + x ) * 3;

				rgb[to] = scanPixels[from];
				rgb[to + 1] = scanPixels[from + 1];
				rgb[to + 2] = scanPixels[from + 2];
			}
		}

		System.IO.Directory.CreateDirectory( Sequence );

		using var file = System.IO.File.Create( System.IO.Path.Combine( Sequence, $"{sequenceWritten:D4}.ppm" ) );
		file.Write( System.Text.Encoding.ASCII.GetBytes( $"P6\n{halfWide} {halfTall}\n255\n" ) );
		file.Write( rgb );

		sequenceWritten++;
	}

	//The darkest and the brightest each pixel has been over the measured window - see WindowFlicker.
	private static void Flicker( int wide, int tall )
	{
		var many = wide * tall;

		if ( flickerLow is null || flickerLow.Length != many || flickerWide != wide || flickerTall != tall )
		{
			flickerLow = new byte[many];
			flickerHigh = new byte[many];
			flickerWide = wide;
			flickerTall = tall;
			flickerFrames = 0;

			for ( var i = 0; i < many; i++ ) flickerLow[i] = 255;
		}

		for ( var i = 0; i < many; i++ )
		{
			//Green alone. It carries most of what an eye calls brightness, and a patch that blinks black blinks in all three anyway.
			var v = scanPixels[( i * 4 ) + 1];

			if ( v < flickerLow[i] ) flickerLow[i] = v;
			if ( v > flickerHigh[i] ) flickerHigh[i] = v;
		}

		flickerFrames++;
	}

	/// <summary>Once a frame, from the engine loop. Nothing until the switch asks.</summary>
	public static void Frame()
	{
		//Announced once from here, not a static ctor, since the boot log rotation would drop it
		if ( !announced && Asked )
		{
			announced = true;

			//Asked for before the window has settled on a size, so the copy is made when the pictures are - see VkSwapchain.FitWindowRead.
			//It costs a screen of host memory and a screen copied a frame, so it is off unless the switch names a file.
			if ( !string.IsNullOrWhiteSpace( WindowShot ) || WindowScan ) CoreRender.KeepsWindowCopy = true;

			Log.Info( $"perfreport: switch is {Length}, warm-up {warmup}s - " +
				$"pid {System.Environment.ProcessId}, command line: {Rinten.Utility.CommandLine.Full}" );

			//Read before the cap comes off, because that is the cap the run was launched under and the only
			//one worth reporting - and then off it comes, for the warm-up as well as for the measurement.
			capAtArm = EngineLoop.EffectiveMaxFrameRate;
			capSourceAtArm = EngineLoop.MaxFrameRateSource;

			Uncapped = true;
		}

		if ( !Asked || done ) return;

		//Booting is not what is being measured - wait until a window has actually put a frame on the screen. Either
		//window: the editor's, or the game's own, which is the only one a browser or a standalone build has.
		if ( CoreUiWindow.FramesPresented == 0 && CoreGameRenderer.FramesPresented == 0 ) return;

		//Nor a scene still coming down a page: its frames until then draw a scene that is not there.
		if ( WebContent.ScenesWaiting > 0 ) return;

		if ( firstFrameAt == 0 )
		{
			firstFrameAt = RealTime.NowDouble;

			if ( warmup > 0 )
				Log.Info( $"perfreport: first frame up, settling for {warmup}s" );
		}

		//And then let it settle: the frames right after a window appears are still compiling shaders and still loading.
		if ( RealTime.NowDouble - firstFrameAt < warmup ) return;

		if ( armedAt == 0 )
		{
			armedAt = RealTime.NowDouble;
			framesAtArm = CoreUiWindow.FramesPresented;
			loopsAtArm = EngineLoop.LoopFrames;
			attemptsAtArm = CoreUiWindow.FramesAttempted;
			refusalsAtArm = CoreUiWindow.FramesRefused;

			gameAtArm = Time.NowDouble;
			wallAtArm = DateTime.UtcNow;

			allocatedAtArm = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
			gen0AtArm = GC.CollectionCount( 0 );
			gen1AtArm = GC.CollectionCount( 1 );
			gen2AtArm = GC.CollectionCount( 2 );
			gcPauseAtArm = GC.GetTotalPauseDuration();
			VkFrame.CardIdleReset();

			viewsSkippedAt = CoreScenePreview.skippedDraws;
			viewsBusyAt = CoreScenePreview.skippedBusy;
			viewsDrewAt = CoreScenePreview.drawnViews;

			bufferBytesAtArm = CoreRenderDevice.ReadMemory().Buffers;

			meshCullsAtArm = CoreMeshRender.Culls;
			meshListDrawsAtArm = CoreMeshRender.ListDraws;
			meshMaterialDrawsAtArm = CoreMeshRender.MaterialDraws;
			particleTicksAtArm = ParticleGameSystem.StepTicks;
			ParticleGameSystem.CountedFrom++;
			particleRunsAtArm = ParticleGameSystem.StepRuns;
			particleAskedAtArm = ParticleGameSystem.Asked;
			particleHeldAtArm = ParticleGameSystem.Held;
			textLabelsAtArm = CoreSceneDrawer.textLabels;
			textMadeAtArm = CoreSceneDrawer.textMade;
			textGoneAtArm = CoreSceneDrawer.textGone;

			framesReusedAtArm = CoreUiRenderer.FramesReused;
			framesWalkedAtArm = CoreUiRenderer.FramesWalked;
			refusedGenerationAtArm = CoreUiRenderer.RefusedGeneration;
			refusedLiveAtArm = CoreUiRenderer.RefusedLive;
			refusedResourcesAtArm = CoreUiRenderer.RefusedResources;
			refusedTexturesAtArm = CoreUiRenderer.RefusedTextures;
			refusedSlotsAtArm = CoreUiRenderer.RefusedSlots;
			refusedPicturesAtArm = CoreUiRenderer.RefusedPictures;

			hoverWalksAtArm = UiWindowInput.HoverWalks;
			hoverSkippedAtArm = UiWindowInput.HoverSkipped;

			bindPollsAtArm = Rinten.UI.Panel.TreeBindPolls;
			bindDeferralsAtArm = Rinten.UI.Panel.TreeBindDeferrals;
			particlesAtArm = ParticleGameSystem.Stepped;
			particleEffectsAtArm = ParticleGameSystem.Effects;
			particleStepsAtArm = ParticleGameSystem.Steps;

			bakesAtArm = CoreSunPages.Bakes;
			pagesAtArm = CoreSunPages.PagesBaked;
			lampFacesAtArm = CoreLampMaps.FacesDrawn;

			lock ( Scene.EditorTicks ) Scene.EditorTicks.Clear();

			allocations = new Allocations.Scope();
			allocations.Start();

			textAtArm = Rinten.UI.TextStats.Take();

			//Profiler tree records only with RINTEN_PERF_TREE=1; it slows the frame, so it's a map, not a timing
			FrameProfiler.Reporting = WantsTree;
			Measuring = true;

			Log.Info( $"perfreport: measuring for {Length}" );
			return;
		}

		//Every frame of the window, from the window rather than from the last second of it.
		var thisFrameMs = PerformanceStats.FrameTime * 1000.0;

		if ( thisFrameMs > 0 ) frameMs.Add( thisFrameMs );

		//Keeps the worst frame's zones whole, since averages hide hitches
		if ( thisFrameMs > worstFrameMs )
		{
			worstFrameMs = thisFrameMs;
			worstFrame.Clear();

			foreach ( var entry in FrameProfiler.Frame )
			{
				if ( entry.LastMs < 0.01 ) continue;

				worstFrame.Add( (entry.Name, entry.Depth, entry.LastMs, entry.LastSelfMs,
					entry.Calls, entry.AllocBytes) );
			}
		}

		foreach ( var entry in FrameProfiler.Frame )
		{
			if ( !tree.TryGetValue( entry.Id, out var running ) ) treeOrder.Add( entry.Id );

			tree[entry.Id] = new TreeTotal( entry.Name, entry.Depth,
				running.Ms + entry.LastMs,
				running.SelfMs + entry.LastSelfMs,
				running.Calls + entry.Calls,
				running.AllocBytes + entry.AllocBytes,
				running.SelfAllocBytes + entry.SelfAllocBytes,
				running.Frames + 1 );
		}

		//The categories are flipped by PerformanceStats.Frame just above this, so index zero is the frame that has only now finished.
		foreach ( var timing in PerformanceStats.Timings.Listed )
		{
			if ( timing.History.Size == 0 ) continue;

			var last = timing.History[0];
			if ( last.TotalMs <= 0 && last.Calls == 0 ) continue;

			categories.TryGetValue( timing.Name, out var running );
			categories[timing.Name] = ( running.Ms + last.TotalMs, running.Calls + last.Calls );
		}

		if ( WindowScan ) ScanWindow();

		//Whichever limit was asked for arrives first. A run given both is over at the earlier of them, which is
		//what makes a frame count safe to ask for: a window that stopped drawing would otherwise never reach it.
		var ranOutOfTime = seconds > 0 && RealTime.NowDouble - armedAt >= seconds;
		var ranOutOfFrames = wantedFrames > 0 && frameMs.Count >= wantedFrames;

		if ( !ranOutOfTime && !ranOutOfFrames ) return;

		Measuring = false;
		FrameProfiler.Reporting = false;
		Uncapped = false;

		done = true;

		try
		{
			Write();
		}
		catch ( System.Exception e )
		{
			Log.Error( e );
		}

		//The run existed to produce the report, and it has.
		System.Environment.Exit( 0 );
	}

	//What the run was asked for, as a person would say it.
	private static string Length => ( seconds, wantedFrames ) switch
	{
		( > 0, > 0 ) => $"{seconds}s or {wantedFrames} frames, whichever comes first",
		( > 0, _ ) => $"{seconds}s",
		_ => $"{wantedFrames} frames",
	};

	//The slowest frame of the window, and what it was doing.
	private static readonly List<(string Name, int Depth, double Ms, double SelfMs, int Calls, long AllocBytes)> worstFrame = new();

	private static double worstFrameMs;

	private static void Write()
	{
		var report = new StringBuilder();
		var elapsed = RealTime.NowDouble - armedAt;
		var frames = CoreUiWindow.FramesPresented - framesAtArm;

		var loops = EngineLoop.LoopFrames - loopsAtArm;
		var attempts = CoreUiWindow.FramesAttempted - attemptsAtArm;
		var refusals = CoreUiWindow.FramesRefused - refusalsAtArm;

		var workspace = Rinten.Utility.CommandLine.GetSwitch( "-perfworkspace", "" ).TrimQuoted();

		report.AppendLine( $"perfreport - {elapsed:F1}s measured, {frames} window frames, {frames / elapsed:F0} fps" +
			( string.IsNullOrWhiteSpace( workspace ) ? " - whatever the editor was last left in" : $" - workspace {workspace}" ) );
		report.AppendLine( $"  loop {loops / elapsed:F0}/s, window frames tried {attempts / elapsed:F0}/s, refused by the swapchain {refusals / elapsed:F0}/s" );
		report.AppendLine();

		//Before any measurement, what it was measured under. A number without its conditions is not a measurement,
		//and the conditions here are not visible anywhere else - Debug and Release deploy to the same folder.
		RunConditions.Write( report, new RunConditions.ClockWindow(
			Time.NowDouble - gameAtArm,
			elapsed,
			( DateTime.UtcNow - wallAtArm ).TotalSeconds ) );

		//The interface's switches, in the conditions rather than beside each count: a run with one turned off reads exactly like a default run everywhere else.
		report.AppendLine( $"  ui_tick_skip {Rinten.UI.Panel.TickSkip}, ui_geometry_cache {CoreUiRenderer.GeometryCache}, " +
			$"ui_reuse_frame {CoreUiRenderer.ReuseFrame}, ui_hover_skip {UiWindowInput.HoverSkip}, " +
			$"ui_bind_poll {Rinten.UI.Panel.BindPollInterval}, ui_scroll_marks_layout {Rinten.UI.Panel.ScrollWriteMarksLayout}" );

		//Since the process started rather than over the window: what goes wrong goes wrong while things are made, which is before anybody
		//measures. The log carries the messages without their severity, so this is the count a run that is meant to be clean is checked against.
		report.AppendLine( $"  validation {( VkDevice.validation ? "on" : "off" )}, {CoreErrors.Count} errors, {CoreErrors.Warnings} warnings" );
		report.AppendLine();

		report.AppendLine( "-- pacing ---------------------------------------------------------------" );
		report.AppendLine( $"  cap {capAtArm:0} by {capSourceAtArm} - lifted for the whole run, " +
			$"display {EngineLoop.DisplayRefreshRate:0} Hz, vsync {Engine.Settings.RenderSettings.Instance.VSync}" );

		frameMs.Sort();

		report.AppendLine( $"  frame typical {Percentile( 0.50 ):F2} ms, mean {Mean():F2} ms " +
			$"(best {Percentile( 0 ):F2}, 99th {Percentile( 0.99 ):F2}, worst {Percentile( 1 ):F2}) over {frameMs.Count} frames" );

		//A mean far above the typical frame is a run with hitches in it, and a hitch is a different
		//fault to a slow frame - it is fixed by finding what stalled, not by making the frame cheaper.
		var typical = Percentile( 0.50 );

		if ( typical > 0 && Mean() > typical * 1.5 )
			report.AppendLine( "  the mean is well above the typical frame - this run hitched, read the worst frames first" );

		WriteWaiting( report, typical );

		var allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - allocatedAtArm;
		var pause = GC.GetTotalPauseDuration() - gcPauseAtArm;

		report.AppendLine( $"  allocated {allocated / ( 1024.0 * 1024.0 ):F0} MB over the window " +
			$"({( frameMs.Count > 0 ? allocated / (double)frameMs.Count / 1024.0 : 0 ):F0} KB a frame), " +
			$"gc {GC.CollectionCount( 0 ) - gen0AtArm}/{GC.CollectionCount( 1 ) - gen1AtArm}/{GC.CollectionCount( 2 ) - gen2AtArm} gen0/1/2, " +
			$"paused {pause.TotalMilliseconds:F0} ms total ({pause.TotalMilliseconds / Math.Max( 1, elapsed ):F1} ms a second)" );
		report.AppendLine( $"  cpu {PerformanceStats.CpuUsage:F1} % of {System.Environment.ProcessorCount} cores, " +
			$"gpu frame {PerformanceStats.GpuFrametime:F2} ms, " +
			$"working set {PerformanceStats.ApproximateProcessMemoryUsage / (1024.0 * 1024.0):F0} MB" );

		//Whether the card is the wall. The gpu frame is first stamp to last of one recording; the gap between recordings is what the card spent waiting on
		//the processor, the submission and the display. Nought is a frame bound by the card; half the frame is a card idle half the time. See CardIdleMs.
		if ( VkFrame.CardIdleRecordings > 1 && frameMs.Count > 0 )
			report.AppendLine( $"  card idle between recordings {VkFrame.CardIdleMs / frameMs.Count:F3} ms a frame " +
				$"({VkFrame.CardIdleRecordings / (double)frameMs.Count:F1} recordings a frame)" );

		//The one cost a baked shader cache does not remove: SPIR-V is not what the card runs, so the driver compiles every pipeline the first
		//time it is made. A player's first run pays for all of them; every run after finds them in the driver's own cache. See VkPipelines.Baked.
		var ( baked, bakedMs ) = CoreRender.PipelinesBaked;

		if ( baked > 0 )
			report.AppendLine( $"  {baked} pipelines baked by the driver this run, {bakedMs:F0} ms of it" );

		WriteGpuMemory( report );
		WriteGpuMemoryByUse( report );
		report.AppendLine();

		report.AppendLine( "-- where the frame goes (over the whole window, ms/frame) ----------------" );

		var sampled = Math.Max( 1, frameMs.Count );

		foreach ( var category in categories
			.Select( c => (Name: c.Key, Ms: c.Value.Ms / sampled, Calls: c.Value.Calls / (double)sampled) )
			.Where( c => c.Ms > 0.005 )
			.OrderByDescending( c => c.Ms ) )
		{
			report.AppendLine( $"  {category.Name,-24} {category.Ms,8:F3} ms  {category.Calls,7:F1} calls" );
		}

		report.AppendLine();
		report.AppendLine( "-- frame profiler (per frame, over the whole window) ---------------------" );
		report.AppendLine( $"  {"",-40} {"ms",8} {"self",8} {"calls",6} {"KB",9} {"self KB",9}" );

		//Zones in first-seen order (parents first), capped at 400; cap must exceed a real frame's zone count
		foreach ( var id in treeOrder.Take( 400 ) )
		{
			if ( !tree.TryGetValue( id, out var total ) || total.Frames == 0 ) continue;

			var over = (double)total.Frames;

			//The bytes beside the milliseconds, because in a managed editor they are the same story told twice and the second telling is often the readable one: the
			//Mapping workspace allocated seven megabytes a frame against General's six hundred kilobytes, and no timing in the tree said which zone was doing it.
			report.AppendLine( $"  {new string( ' ', total.Depth * 2 )}{total.Name,-40} " +
				$"{total.Ms / over,8:F3} {total.SelfMs / over,8:F3} {total.Calls / over,6:F1} " +
				$"{total.AllocBytes / 1024.0 / over,9:F1} {total.SelfAllocBytes / 1024.0 / over,9:F1}" );
		}

		if ( worstFrame.Count > 0 )
		{
			report.AppendLine();
			report.AppendLine( $"-- the worst frame ({worstFrameMs:F1} ms), zone by zone ---------------------------" );
			report.AppendLine( $"  {"",-40} {"ms",8} {"self",8} {"calls",6} {"KB",9}" );

			foreach ( var entry in worstFrame.Take( 40 ) )
			{
				report.AppendLine( $"  {new string( ' ', entry.Depth * 2 )}{entry.Name,-40} " +
					$"{entry.Ms,8:F3} {entry.SelfMs,8:F3} {entry.Calls,6} {entry.AllocBytes / 1024.0,9:F1}" );
			}
		}

		report.AppendLine();
		report.AppendLine( "-- the renderer's last frame ---------------------------------------------" );

		if ( CoreRenderProfiler.LastViewport != 0 &&
			CoreRenderDevice.ReadProfile( CoreRenderProfiler.LastViewport ) is { } profile )
		{
			report.AppendLine( $"  gpu {profile.GpuMs:F2} ms, cpu record {profile.CpuMs:F2} ms, " +
				$"{profile.Draws} draws, {profile.Triangles:N0} tris" );

			//Triangles drawn without clusters (classic draws); moving-vertex surfaces always land here
			if ( CoreSceneDrawer.LooseTriangles > 0 )
			{
				report.AppendLine( $"  {CoreSceneDrawer.LooseDraws} of those draws are the classic path, " +
					$"{CoreSceneDrawer.LooseTriangles:N0} tris a pass - {CoreSceneDrawer.LooseMovingTriangles:N0} of them " +
					$"moving, which is why they carry no level" );
			}

			//How much of that a single call already covers, which is what decides whether deciding it on the card is worth anything:
			//an indirect call spans one mesh and one material, so what it can take over is instanced copies, not draws that differ.
			report.AppendLine( $"  {profile.SkinnedDraws} skinned, {profile.DynamicDraws} dynamic, " +
				$"{profile.Instances} copies from instanced calls, {profile.SkippedBinds} redundant binds skipped" );
			report.AppendLine( $"  textures {profile.TextureBytes / (1024.0 * 1024.0):F0} MB in {profile.Textures}, " +
				$"buffers {profile.BufferBytes / (1024.0 * 1024.0):F0} MB in {profile.Buffers}" );

			WriteGeometryDensity( report, profile );

			foreach ( var zone in profile.Zones )
			{
				report.AppendLine( $"  {new string( ' ', ( zone.Depth + 1 ) * 2 )}{zone.Name,-24} gpu {zone.GpuMs,7:F3} ms   cpu {zone.CpuMs,7:F3} ms" );
			}
		}
		else
		{
			report.AppendLine( "  no finished frame to read - no scene viewport was drawn" );
		}

		WriteAllocations( report );

		WriteLabels( report );
		WriteMeshPasses( report );
		WriteParticles( report );

		WriteShadows( report );

		WriteViewports( report, elapsed );
		WriteEditorTicks( report );
		WriteTypeLibrary( report );

		//What the one collection after the compile handed back, if it ran. The working set is what
		//a person sees in a task manager, so it is the half worth printing next to the frame.
		if ( !string.IsNullOrEmpty( Reclaimed ) )
		{
			report.AppendLine();
			report.AppendLine( "-- heap reclaim ---------------------------------------------------------" );
			report.AppendLine( $"  after the compile: {Reclaimed}" );
		}

		if ( Rinten.UI.TickTypes.Counting )
		{
			report.AppendLine();
			report.AppendLine( "-- which panels spend the tick (-ticktypes, over the whole run) ----------" );

			var tickFrames = System.Math.Max( 1, frameMs.Count );

			foreach ( var (name, ms, calls) in Rinten.UI.TickTypes.Worst( 15 ) )
			{
				report.AppendLine( $"  {ms / tickFrames,8:F4} ms a frame  {(double)calls / tickFrames,7:F1} calls  {name}" );
			}

			report.AppendLine();
			report.AppendLine( $"-- why panels were ticked (-ticktypes; {(double)Rinten.UI.TickReasons.Visited / tickFrames:F1} visited a frame, own turn only) --" );

			foreach ( var (name, ms, calls) in Rinten.UI.TickReasons.All() )
			{
				report.AppendLine( $"  {ms / tickFrames,8:F4} ms a frame  {(double)calls / tickFrames,7:F1} panels  {name}" );
			}

			report.AppendLine();
			report.AppendLine( "-- which panels asked for their turn (-ticktypes) -------------------------" );

			var askerList = Rinten.UI.TickReasons.Askers( 15 ).ToList();

			foreach ( var (name, ms, calls) in askerList )
			{
				report.AppendLine( $"  {ms / tickFrames,8:F4} ms a frame  {(double)calls / tickFrames,7:F1} calls  {name}" );
			}

			report.AppendLine();
			report.AppendLine( "-- razor render trees rebuilt, by type (-ticktypes) ------------------------" );

			foreach ( var (name, count) in Rinten.UI.TickReasons.Renders( 12 ) )
				report.AppendLine( $"  {(double)count / tickFrames,8:F2} a frame  {name}" );

			report.AppendLine();
			report.AppendLine( "-- still moving, so asking again: which answer, frames over the run --------" );

			foreach ( var (name, transition, animation, scroll, events) in Rinten.UI.TickReasons.Motion( 10 ) )
				report.AppendLine( $"  transition {transition,6}  animation {animation,6}  scroll {scroll,6}  events {events,6}  {name}" );

			report.AppendLine();
			report.AppendLine( "-- where the busiest askers ask from (their 500th ask) ---------------------" );

			foreach ( var (name, _, _) in askerList.Take( 3 ) )
			{
				var stack = Rinten.UI.TickReasons.AskStack( name );
				report.AppendLine( $"  {name}:" );
				report.AppendLine( stack is null ? "    (asked fewer than 500 times)" : "    " + stack.Replace( "\n", "\n    " ) );
			}
		}

		report.AppendLine();
		report.AppendLine( "-- the interface, in counts ---------------------------------------------" );

		var walk = CoreUiRenderer.LastWindowWalk;

		report.AppendLine( $"  panels visited {walk.Visited}, skipped whole {walk.Skipped}, " +
			$"boxes {walk.Boxes} covering {walk.Area / 1.0e6:F2} MP, of which {walk.Covered} under something solid ({walk.CoveredArea / 1.0e6:F2} MP) were dropped, " +
			$"text rasterised {walk.TextRasterised}, texture uploads {walk.TextureUploads}" );

		{
			var ts = Rinten.UI.TextStats.Take();
			report.AppendLine( $"  text over the run ({ts.Frames} frames): " + Rinten.UI.TextStats.Describe( ts ) );

			//What text cost in the window alone, past the run's first shaping and loading of each face
			var rasters = ts.ScopeRasters - textAtArm.ScopeRasters;
			var changes = ts.Restyles - textAtArm.Restyles;
			report.AppendLine( $"  text in the window: {rasters} rasters, {( ts.ScopeGarbage - textAtArm.ScopeGarbage ) / 1024.0:F1} KB garbage " +
				$"({( rasters > 0 ? ( ts.ScopeGarbage - textAtArm.ScopeGarbage ) / rasters : 0 )} bytes each); {changes} label changes, " +
				$"{( ts.LabelGarbage - textAtArm.LabelGarbage ) / 1024.0:F1} KB garbage ({( changes > 0 ? ( ts.LabelGarbage - textAtArm.LabelGarbage ) / changes : 0 )} bytes each)" );
		}

		if ( walk.Biggest is { } biggest )
		{
			report.Append( "  biggest boxes left:" );

			foreach ( var (area, kind, textured, alpha) in biggest )
			{
				if ( area <= 0 ) continue;
				report.Append( $" {area / 1.0e6:F2} MP {kind}{( textured ? " picture" : "" )} alpha {alpha};" );
			}

			report.AppendLine();
		}

		if ( walk.BiggestEmitted is { } emitted )
		{
			report.Append( $"  biggest boxes emitted (the world came {walk.WorldOrder}th):" );

			foreach ( var (area, panel, order) in emitted )
			{
				if ( area <= 0 ) continue;
				report.Append( $" {area / 1.0e6:F2} MP {panel} {order}th;" );
			}

			report.AppendLine();
		}

		var perBatch = walk.Batches > 0 ? walk.Boxes / (double)walk.Batches : 0;

		report.AppendLine( $"  batched into {walk.Batches} ({perBatch:F1} boxes a batch, " +
			$"{walk.TextureCuts} cut by a texture change, {walk.DistinctTextures} distinct textures)" );

		//Two pictures on one slot is the only way index lookup goes wrong; counted on both sides
		if ( walk.Slotted > 0 )
		{
			report.AppendLine( $"  {walk.Slotted} textures in {walk.DistinctSlots} slots of the picture table" +
				( walk.Unslotted > 0 ? $", {walk.Unslotted} with no slot (drawn the bound way)" : "" ) );

			if ( walk.Slotted > walk.DistinctSlots )
			{
				report.AppendLine( "  TWO PICTURES SHARE A SLOT - some boxes are drawing somebody else's texture" );
			}
		}

		//The last walk's split between boxes put out from the previous frame and boxes made afresh.
		if ( walk.Replayed > 0 || walk.Recorded > 0 )
		{
			report.AppendLine( $"  geometry cache: {walk.Replayed} panels replayed, {walk.Recorded} made afresh" +
				( CoreUiRenderer.GeometryCache ? "" : " (ui_geometry_cache off)" ) );
		}

		//Editor frames handed to the device again without a walk, against frames walked, and why the rest could not
		//be - each refusal counted under the first check that failed. Over the window, like the occlusion above.
		var reused = CoreUiRenderer.FramesReused - framesReusedAtArm;
		var walked = CoreUiRenderer.FramesWalked - framesWalkedAtArm;

		if ( reused > 0 || walked > 0 )
		{
			report.AppendLine( $"  frame reuse: {reused} of {reused + walked} window frames resubmitted last frame's buffer " +
				$"(refused: generation {CoreUiRenderer.RefusedGeneration - refusedGenerationAtArm}, " +
				$"live {CoreUiRenderer.RefusedLive - refusedLiveAtArm}, " +
				$"resources or units {CoreUiRenderer.RefusedResources - refusedResourcesAtArm}, " +
				$"textures {CoreUiRenderer.RefusedTextures - refusedTexturesAtArm}, " +
				$"slots {CoreUiRenderer.RefusedSlots - refusedSlotsAtArm}, " +
				$"pictures {CoreUiRenderer.RefusedPictures - refusedPicturesAtArm})" +
				( CoreUiRenderer.ReuseFrame ? "" : " (ui_reuse_frame off)" ) +
				( walk.Reused > 0 ? $"; the counts above are a frame reused {walk.Reused} times in a row" : "" ) );
		}

		//The hit test over an editor window's panels: the frames it ran on against the frames it was left out, nothing in its key having moved.
		var hoverWalks = UiWindowInput.HoverWalks - hoverWalksAtArm;
		var hoverSkipped = UiWindowInput.HoverSkipped - hoverSkippedAtArm;

		if ( hoverWalks > 0 || hoverSkipped > 0 )
		{
			report.AppendLine( $"  hover: {hoverWalks} walks, {hoverSkipped} skipped" +
				( UiWindowInput.HoverSkip ? "" : " (ui_hover_skip off)" ) );
		}

		//Razor binds polled against polls put off to a later frame. Panels a frame rather than the totals, which run to hundreds of thousands over a window.
		var bindPolls = Rinten.UI.Panel.TreeBindPolls - bindPollsAtArm;
		var bindDeferrals = Rinten.UI.Panel.TreeBindDeferrals - bindDeferralsAtArm;

		if ( bindPolls > 0 || bindDeferrals > 0 )
		{
			var perFrame = Math.Max( 1, frames );

			report.AppendLine( $"  bind poll: {bindPolls / perFrame} panels polled a frame, {bindDeferrals / perFrame} deferred " +
				$"(ui_bind_poll {Rinten.UI.Panel.BindPollInterval})" );
		}

		if ( walk.Backdrops > 0 )
		{
			report.AppendLine( $"  {walk.Backdrops} backdrop panels - each copies the whole window " +
				"before it draws" );
		}

		//One box a batch is one draw a box, and the interface is then paying a pipeline bind and a descriptor bind for every rectangle on the screen.
		if ( walk.Batches > 0 && perBatch < 2 )
			report.AppendLine( "  THE INTERFACE IS NOT BATCHING - a draw per box, cut by the texture each one names" );

		report.AppendLine();
		report.AppendLine( "-- windows ---------------------------------------------------------------" );
		CoreUiWindow.Report( report );

		//Whether any frame of the run left window unwritten - see WindowScan.
		if ( WindowScan && scanFrames > 0 )
		{
			report.AppendLine();
			report.AppendLine( "-- the window's alpha ---------------------------------------------------" );

			if ( scanOpaqueWorst == scanOpaqueBest )
			{
				report.AppendLine( $"  {scanOpaqueBest:n0} pixels opaque in every one of {scanFrames:n0} frames - no frame left a hole" );
			}
			else
			{
				report.AppendLine( $"  {scanOpaqueBest:n0} pixels opaque at best, {scanOpaqueWorst:n0} at worst over {scanFrames:n0} frames" );
				report.AppendLine( $"  the worst frame left {scanOpaqueBest - scanOpaqueWorst:n0} pixels of window unwritten - " +
					"the desktop shows through those" );
			}
		}

		var text = report.ToString();

		Log.Info( text );

		System.IO.Directory.CreateDirectory( "logs" );
		//Somewhere of the caller's choosing with -perfreportfile. Two runs on one machine - two sessions measuring
		//at once - otherwise write the same file, and whichever finished last is the report both of them read back.
		var reportFile = Rinten.Utility.CommandLine.GetSwitch( "-perfreportfile", "logs/perfreport.txt" );

		System.IO.File.WriteAllText( reportFile, text );

		//And the picture itself, where asked: -perfshot <file.ppm>, the last view drawn, as bytes - so two runs can be compared pixel by pixel.
		var shot = Rinten.Utility.CommandLine.GetSwitch( "-perfshot", "" ).TrimQuoted();

		if ( !string.IsNullOrWhiteSpace( shot ) && CoreSceneDrawer.LastDrawn.View != 0 )
		{
			var (view, width, height) = CoreSceneDrawer.LastDrawn;
			var pixels = new byte[width * height * 4];

			var read = CoreRenderDevice.ReadView( view, pixels );

			//A scene drawn straight into a window has no view to read: drawn once more into one, the same way, at the same size.
			if ( !read )
			{
				using var scope = Engine.GlobalContext.GameScope();

				var scene = Game.ActiveScene;
				var camera = scene.IsValid() ? scene.Camera : null;

				if ( camera.IsValid() && new CoreScenePreview().Shoot( scene, camera, width, height, out var again ) )
				{
					pixels = again;
					read = true;
				}
			}

			if ( read )
			{
				using var file = System.IO.File.Create( shot );
				var header = System.Text.Encoding.ASCII.GetBytes( $"P6\n{width} {height}\n255\n" );
				file.Write( header );
				var rgb = new byte[width * height * 3];
				for ( var i = 0; i < width * height; i++ )
				{
					rgb[i * 3] = pixels[i * 4];
					rgb[( i * 3 ) + 1] = pixels[( i * 4 ) + 1];
					rgb[( i * 3 ) + 2] = pixels[( i * 4 ) + 2];
				}
				file.Write( rgb );
				Log.Info( $"perfreport: picture written to {shot} ({width}x{height})" );
			}
			else
			{
				Log.Info( $"perfreport: no picture - {CoreRenderDevice.Description}" );
			}
		}

		//And what the window actually presented, where asked.
		var windowShot = WindowShot;

		if ( !string.IsNullOrWhiteSpace( windowShot ) )
		{
			var handle = CoreUiWindow.MainSwapchain;
			var (wide, tall) = CoreRender.WindowReadSize( handle );

			//The worst frame the scan saw, where it saw one - a photograph of the last frame says nothing about a fault that comes and goes.
			var worst = scanWorstPixels is not null;

			if ( worst )
			{
				wide = scanWorstWide;
				tall = scanWorstTall;
			}

			if ( wide > 0 && tall > 0 )
			{
				var pixels = worst ? scanWorstPixels : new byte[wide * tall * 4];

				var got = worst ? pixels.Length : ReadWindowPixels( handle, pixels );

				//A world on a layer of its own is not in the window's picture: the compositor puts
				//the two together, and so does this - the layer's last picture, at its place.
				if ( !worst && got == pixels.Length ) PasteLayer( pixels, wide, tall );

				if ( got == pixels.Length )
				{
					using ( var file = System.IO.File.Create( windowShot ) )
					{
						file.Write( System.Text.Encoding.ASCII.GetBytes( $"P6\n{wide} {tall}\n255\n" ) );

						var rgb = new byte[wide * tall * 3];

						for ( var i = 0; i < wide * tall; i++ )
						{
							rgb[i * 3] = pixels[i * 4];
							rgb[( i * 3 ) + 1] = pixels[( i * 4 ) + 1];
							rgb[( i * 3 ) + 2] = pixels[( i * 4 ) + 2];
						}

						file.Write( rgb );
					}

					using ( var file = System.IO.File.Create( windowShot + ".alpha.pgm" ) )
					{
						file.Write( System.Text.Encoding.ASCII.GetBytes( $"P5\n{wide} {tall}\n255\n" ) );

						var alpha = new byte[wide * tall];
						for ( var i = 0; i < wide * tall; i++ ) alpha[i] = pixels[( i * 4 ) + 3];

						file.Write( alpha );
					}

					Log.Info( $"perfreport: the window itself written to {windowShot} ({wide}x{tall}), alpha beside it" +
						( worst ? $" - the least opaque frame of the run, {scanOpaqueWorst:n0} of {scanOpaqueBest:n0}" : "" ) );
				}
				else
				{
					Log.Info( "perfreport: the window's own copy would not read" );
				}
			}
			else
			{
				Log.Info( "perfreport: no host copy of the window - was -perfwindowshot given before it opened?" );
			}
		}

		//And how much every pixel moved over the run - see WindowFlicker.
		if ( WindowFlicker && flickerFrames > 1 && flickerLow is not null )
		{
			var many = flickerWide * flickerTall;
			var span = new byte[many];
			long moved = 0, worst = 0, sum = 0;

			for ( var i = 0; i < many; i++ )
			{
				var by = flickerHigh[i] - flickerLow[i];
				span[i] = (byte)by;
				if ( by > 24 ) moved++;
				if ( by > worst ) worst = by;
				sum += by;
			}

			var where = string.IsNullOrWhiteSpace( windowShot ) ? "logs/flicker" : windowShot;

			using ( var file = System.IO.File.Create( where + ".flicker.pgm" ) )
			{
				file.Write( System.Text.Encoding.ASCII.GetBytes( $"P5\n{flickerWide} {flickerTall}\n255\n" ) );
				file.Write( span );
			}

			//And the darkest each pixel ever was, because the fault being chased is a pixel that goes to **exactly
			//nought** for a frame - one nobody wrote - and a span alone cannot tell that from one that merely got dark.
			long everBlack = 0;

			for ( var i = 0; i < many; i++ )
				if ( flickerLow[i] == 0 && flickerHigh[i] > 24 ) everBlack++;

			using ( var file = System.IO.File.Create( where + ".low.pgm" ) )
			{
				file.Write( System.Text.Encoding.ASCII.GetBytes( $"P5\n{flickerWide} {flickerTall}\n255\n" ) );
				file.Write( flickerLow );
			}

			if ( everBlack > 0 )
				Log.Warning( $"perfreport: {everBlack:n0} pixels were exactly nought at some frame and lit at another - " +
					$"nobody wrote them; the darkest each ever was is in {where}.low.pgm" );

			Log.Info( $"perfreport: {moved:n0} of {many:n0} pixels moved by more than 24 over {flickerFrames:n0} frames " +
				$"(worst {worst}, mean {(double)sum / many:F2}) - the span written to {where}.flicker.pgm" );
		}

		Log.Info( $"perfreport: written to {Rinten.Utility.CommandLine.GetSwitch( "-perfreportfile", "logs/perfreport.txt" )}" );
	}

	//Cull pass feed cost: candidate bytes uploaded per frame versus what it decides
	private static void WriteMeshPasses( System.Text.StringBuilder report )
	{
		var culls = CoreMeshRender.Culls - meshCullsAtArm;
		if ( culls <= 0 ) return;

		var over = Math.Max( 1, frameMs.Count );
		var lists = CoreMeshRender.ListDraws - meshListDrawsAtArm;
		var materials = CoreMeshRender.MaterialDraws - meshMaterialDrawsAtArm;

		report.AppendLine();
		report.AppendLine( "-- the mesh passes ------------------------------------------------------" );
		report.AppendLine( $"  {culls / over} culls a frame, {lists / over} list draws, {materials / over} material passes, clusters drawn indexed from the runs the cull writes" );
		report.AppendLine( "  every instance and cluster is decided on the card; the processor writes what moved and the views" );

		//Per-frame walk over hand-placed objects; high asked, low moved/changed means the walk is the cost
		if ( CoreSceneTables.Syncs > 0 && CoreSceneTables.ObjectsAsked > 0 )
		{
			var syncs = CoreSceneTables.Syncs;

			report.AppendLine( $"  hand-placed objects: {CoreSceneTables.ObjectsAsked / syncs} asked a sync, " +
				$"{CoreSceneTables.ObjectsPlaced / syncs} had moved, {CoreSceneTables.ObjectsNumbered / syncs} wrote their numbers, " +
				$"{CoreSceneTables.ObjectsResolved / syncs} had changed" );
		}
	}

	//Particle step stats and cost per particle, comparable across runs with different counts
	private static void WriteParticles( System.Text.StringBuilder report )
	{
		var steps = ParticleGameSystem.Steps - particleStepsAtArm;

		if ( steps <= 0 ) return;

		var stepped = ParticleGameSystem.Stepped - particlesAtArm;
		var effects = ParticleGameSystem.Effects - particleEffectsAtArm;

		//The same accumulation the "where the frame goes" table reads, so the two lines cannot disagree about what the step cost.
		categories.TryGetValue( PerformanceStats.Timings.Particles.Name, out var timing );

		var ms = timing.Ms / Math.Max( 1, frameMs.Count );

		report.AppendLine();
		report.AppendLine( "-- particles ------------------------------------------------------------" );

		if ( stepped <= 0 )
		{
			report.AppendLine( $"  {effects / steps} effects, no particles alive" );
			return;
		}

		report.AppendLine( $"  {stepped / steps} particles a frame over {effects / steps} effects, " +
			$"{ms:0.000} ms a frame, {ms * 1_000_000.0 * steps / stepped:0.0} ns a particle" );

		//And how much of that was one thread. The zone above is wall clock; this is the thread time the workers actually spent and how many of them
		//there were. A step whose thread time is barely more than its wall clock is a step being done by one thread whatever it was handed to.
		var ticks = ParticleGameSystem.StepTicks - particleTicksAtArm;
		var runs = ParticleGameSystem.StepRuns - particleRunsAtArm;

		if ( ticks > 0 )
		{
			var threadMs = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / steps;

			report.AppendLine( $"  {threadMs:0.000} ms of processor a frame - {( ms > 0.0 ? threadMs / ms : 0.0 ):0.00}x of the wall clock over " +
				$"{runs / (double)steps:0.0} workers, {threadMs * 1_000_000.0 * steps / stepped:0.0} ns of processor a particle" );
		}

		//And how many effects were stepped at all. An effect far from the eye steps once in
		//several frames with the time since rolled into one step - see ParticleGameSystem.RateFor.
		var asked = ParticleGameSystem.Asked - particleAskedAtArm;
		var held = ParticleGameSystem.Held - particleHeldAtArm;

		if ( asked > 0 && held > 0 )
			report.AppendLine( $"  {held * 100 / asked} % of the effects asked were held back this frame - too far to step every one" );

		//Which effects that processor went to, the dearest first
		if ( ticks > 0 && Game.ActiveScene is { } stepping )
		{
			foreach ( var (name, effectTicks, effectParticles) in ParticleGameSystem.Costliest( stepping, 10 ) )
			{
				var share = effectTicks * 100.0 / ticks;
				var each = effectTicks * 1_000_000_000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max( effectParticles, 1 );

				report.AppendLine( $"    {share,5:0.0} %  {effectParticles / steps,6} a frame  {each,7:0} ns each  {name}" );
			}
		}

		Anchors( report, steps );
	}

	//Anchor pose traffic; only the last write per name is kept, so excess writes are waste
	private static void Anchors( System.Text.StringBuilder report, long steps )
	{
		if ( steps <= 0 || Game.ActiveScene is not { } scene ) return;

		var writes = 0L;
		var names = 0;
		var players = 0;

		foreach ( var player in scene.GetAllComponents<ParticlePlayer>() )
		{
			if ( !player.IsValid() ) continue;

			var (wrote, named) = player.AnchorTraffic();

			if ( named == 0 ) continue;

			writes += wrote;
			names += named;
			players++;
		}

		if ( writes <= 0 ) return;

		report.AppendLine( $"  anchors: {writes / steps} poses a frame to {names} names over {players} players" +
			$" - {(double)writes / Math.Max( names, 1 ) / steps:0.0} writes a name a frame" );

		if ( EventModule.Fired > 0 )
		{
			report.AppendLine( $"  events: {EventModule.Fired / steps} fired a frame, " +
				$"{EventModule.Anchored / steps} set an anchor, {EventModule.Queued / steps} queued a spawn" );
		}
	}

	//How many renderers a walk over the scene looks at. The one part of a frame whose cost grows with the size of the scene rather than with
	//what is on the screen, which is what makes it the number to read when a frame gets slower after a level got bigger rather than busier.
	private static void WriteLabels( System.Text.StringBuilder report )
	{
		var labels = CoreSceneDrawer.textLabels - textLabelsAtArm;
		if ( labels <= 0 ) return;

		var made = CoreSceneDrawer.textMade - textMadeAtArm;
		var retired = CoreSceneDrawer.textGone - textGoneAtArm;
		var over = Math.Max( 1, frameMs.Count );

		report.AppendLine();
		report.AppendLine( "-- the editor's labels --------------------------------------------------" );
		report.AppendLine( $"  {labels / over} asked for a frame, {made / over} rasterised into a texture of their own, {retired / over} textures handed back" );
		report.AppendLine( "  both of those happen inside the open frame, and this is the zone that spikes to eight milliseconds" );
	}

	//Shadow cost: sun page bakes and lamp faces drawn over the window
	private static void WriteShadows( System.Text.StringBuilder report )
	{
		report.AppendLine( "-- shadows --------------------------------------------------------------" );
		report.AppendLine( $"  {CoreSunPages.Bakes - bakesAtArm} bakes of the sun's pages in the window, " +
			$"{CoreSunPages.PagesBaked - pagesAtArm} pages" );
		report.AppendLine( $"  {CoreLampMaps.FacesDrawn - lampFacesAtArm} lamp faces drawn in the window" );
		report.AppendLine();
	}

	//Triangles per pixel; above about one, a depth pre-pass costs more than it saves
	private static void WriteGeometryDensity( System.Text.StringBuilder report,
		CoreRenderDevice.FrameProfile profile )
	{
		var pixels = (double)LastViewportPixels;

		if ( pixels <= 0 || profile.Triangles <= 0 ) return;

		var perPixel = profile.Triangles / pixels;

		report.AppendLine( $"  {perPixel:F2} triangles a pixel over {pixels / 1e6:F2} M pixels " +
			"- every pass of the frame over the camera's own pixels, so shadows are in the top half " +
			"and not in the bottom" );
	}

	//How many pixels the viewport the profile came from covers.
	internal static long LastViewportPixels;

	//CPU memory still held by assets after upload: compiled models and texture pixels
	private static void WriteCpuAssets( System.Text.StringBuilder report )
	{
		const double mb = 1024.0 * 1024.0;

		var pixels = CoreTexture.HeldPixels();
		var models = ( Bytes: CoreAssets.ModelFile.HeldBytes, Count: CoreAssets.ModelFile.HeldFiles );

		if ( pixels.Bytes <= 0 && models.Bytes <= 0 ) return;

		report.AppendLine( $"  still on the processor: models {models.Bytes / mb:F0} MB in {models.Count}, " +
			$"texture pixels {pixels.Bytes / mb:F0} MB in {pixels.Count} - " +
			$"copies of what the card already has" );

		//The heaviest of the pictures, by name - what to look at first.
		foreach ( var (name, bytes) in CoreTexture.HeaviestHeld( 8 ) )
			report.AppendLine( $"    {bytes / mb,6:F1} MB  {name}" );
	}

	//The device memory by what it is for: the pictures the engine loads and the ones it draws into, the shadows, and the
	//geometry - the pools by what they hold and the chain by level. What a change to the geometry's format is weighed against.
	private static void WriteGpuMemoryByUse( System.Text.StringBuilder report )
	{
		if ( CoreRenderDevice.PortableFrame ) return;

		const double mb = 1024.0 * 1024.0;

		var memory = CoreRenderDevice.ReadMemory();
		var impostors = CoreImpostor.PageBytes;

		report.AppendLine( $"  by use: textures {( memory.Textures - impostors ) / mb:F1} MB, impostor pages {impostors / mb:F1}, " +
			$"targets {memory.Targets / mb:F1}, shadow maps {memory.ShadowMaps / mb:F1}, depth pyramids {memory.Pyramids / mb:F1}" );

		//Which upscaler drew the last frame, and at what size - a scale that never reached the world's pictures reads here as two sizes the same.
		var up = CoreUpscaler.Last;
		report.AppendLine( $"  upscaler {up.Kind}: world drawn at {up.Render.Width}x{up.Render.Height}, shown at {up.Display.Width}x{up.Display.Height}" );


		static long Held( CoreGeometryPool.Pool pool ) => pool.Buffer == 0 ? 0 : pool.Capacity * pool.ElementBytes;
		static long Used( CoreGeometryPool.Pool pool ) => pool.Used * pool.ElementBytes;

		var pools = new (string Name, CoreGeometryPool.Pool Pool)[]
		{
			( "vertices", CoreGeometryPool.Vertices ),
			( "indices", CoreGeometryPool.Indices ),
			( "clusters", CoreClusters.Clusters ),
			( "cluster vertices", CoreClusters.ClusterVertices ),
			( "cluster triangles", CoreClusters.ClusterTriangles ),
			( "mesh records", CoreClusters.Meshes ),
		};

		var held = 0L;
		var line = new System.Text.StringBuilder( "  geometry pools, used of held MB:" );

		foreach ( var (name, pool) in pools )
		{
			held += Held( pool );
			line.Append( $" {name} {Used( pool ) / mb:F1}/{Held( pool ) / mb:F1}," );
		}

		line.Append( $" other buffers {( memory.Buffers - held ) / mb:F1}" );
		report.AppendLine( line.ToString() );

		if ( PolygonMesh.CutMeshes > 0 )
			report.AppendLine( $"  scene meshes cut by the world's grid: {PolygonMesh.CutMeshes} builds into {PolygonMesh.CutCells} cells" );

		var total = new CoreClusters.LevelTally();

		for ( var level = 0; level < CoreClusters.MostLevels; level++ )
		{
			var one = CoreClusters.Levels[level];
			if ( one.Clusters == 0 ) continue;

			report.AppendLine( $"    level {level}: {one.Clusters,9:n0} clusters {one.Triangles,11:n0} triangles " +
				$"{one.Vertices,10:n0} vertices {one.References,11:n0} references  {one.Bytes / mb,7:F1} MB" );

			total.Clusters += one.Clusters;
			total.Triangles += one.Triangles;
			total.Vertices += one.Vertices;
			total.References += one.References;
		}

		if ( total.Clusters > 0 )
		{
			var finest = CoreClusters.Levels[0];
			report.AppendLine( $"    all levels: {total.Clusters:n0} clusters, {total.Triangles:n0} triangles, {total.Vertices:n0} vertices, " +
				$"{total.Bytes / mb:F1} MB - {( finest.Bytes > 0 ? total.Bytes / (double)finest.Bytes : 0 ):F2}x level 0; " +
				$"{CoreClusters.UnnamedVertices:n0} pool vertices no cluster names, " +
				$"{CoreGeometryPool.Indices.Used * 4 / mb:F1} MB of loose indices" );
		}
	}

	private static void WriteGpuMemory( System.Text.StringBuilder report )
	{
		const double mb = 1024.0 * 1024.0;

		var memory = CoreRenderDevice.ReadMemory();

		//What streaming holds - see CoreTextureStreaming. Here, where every frame says it, a page's included.
		if ( CoreTextureStreaming.Count > 0 )
			report.AppendLine( $"  texture streaming: {CoreTextureStreaming.Count} streamed, holding {CoreTextureStreaming.HeldBytes / mb:F1} MB, {CoreTextureStreaming.Swaps} level changes put in place" );

		if ( !memory.DriverAnswered )
		{
			report.AppendLine( $"  gpu memory ours {memory.Ours / mb:F0} MB " +
				$"(textures {memory.Textures / mb:F0}, buffers {memory.Buffers / mb:F0}) - " +
				$"the driver would not say what it thinks, so there is nothing to check this against" );
			return;
		}

		report.AppendLine( $"  gpu memory ours {memory.Ours / mb:F0} MB " +
			$"(textures {memory.Textures / mb:F0}, buffers {memory.Buffers / mb:F0}), " +
			$"driver says {memory.DriverUsed / mb:F0} MB of {memory.DriverBudget / mb:F0} MB " +
			$"allowed on {memory.HeapSize / mb:F0} MB of device heap" );

		report.AppendLine( $"  unaccounted {memory.Unaccounted / mb:F0} MB - swapchain, descriptor pools, " +
			$"staging, alignment and the driver's own" );

		WriteCpuAssets( report );

		//Whether the level was still being built while it was being measured.
		var made = memory.Buffers - bufferBytesAtArm;

		if ( made > 1024 * 1024 )
		{
			report.AppendLine( $"  THE LEVEL WAS STILL BEING BUILT - {made / mb:F0} MB more geometry exists " +
				"than when the window opened. A scene that meshes itself as it goes was doing that during " +
				"this run, and those frames are not frames. Warm up for longer." );
		}
	}

	//What the window allocated, by type, heaviest first.
	private static void WriteAllocations( System.Text.StringBuilder report )
	{
		if ( allocations is null ) return;

		var entries = allocations.Entries
			.Where( e => e.TotalBytes > 0 )
			.OrderByDescending( e => e.TotalBytes )
			.Take( 20 )
			.ToList();

		allocations.Stop();

		if ( entries.Count == 0 ) return;

		var frames = Math.Max( 1, frameMs.Count );

		report.AppendLine();
		report.AppendLine( "-- what was allocated (sampled by the runtime, so proportions not totals) -" );
		report.AppendLine( $"  {"KB a frame",11}  {"allocations",12}  type" );

		foreach ( var entry in entries )
		{
			report.AppendLine( $"  {entry.TotalBytes / 1024.0 / frames,11:F1}  {entry.Count,12:N0}  {entry.Name}" );
		}
	}

	//One frame time out of the sorted window, by rank.
	private static double Percentile( double at )
	{
		if ( frameMs.Count == 0 ) return 0;

		var index = (int)Math.Round( at * ( frameMs.Count - 1 ) );

		return frameMs[Math.Clamp( index, 0, frameMs.Count - 1 )];
	}

	private static double Mean()
	{
		if ( frameMs.Count == 0 ) return 0;

		var sum = 0.0;
		foreach ( var one in frameMs ) sum += one;

		return sum / frameMs.Count;
	}

	//Which scenes ticked and how often, since profiler zones can't tell scenes apart
	private static void WriteEditorTicks( StringBuilder report )
	{
		KeyValuePair<string, int>[] ticks;

		lock ( Scene.EditorTicks ) ticks = Scene.EditorTicks.ToArray();

		if ( ticks.Length == 0 ) return;

		var frames = Math.Max( 1, frameMs.Count );

		report.AppendLine();
		report.AppendLine( "-- editor ticks ---------------------------------------------------------" );

		foreach ( var one in ticks.OrderByDescending( x => x.Value ) )
			report.AppendLine( $"  {one.Key}: {one.Value / (double) frames:F2} a frame" );

		if ( ticks.Length > 1 )
			report.AppendLine( $"  {ticks.Length} scenes ticking - only the one on screen is being looked at" );
	}

	//Says in words if the frame is mostly waiting on the display (acquire), which looks like idle load
	private static void WriteWaiting( StringBuilder report, double typical )
	{
		if ( typical <= 0 ) return;

		var frames = Math.Max( 1, frameMs.Count );
		var waiting = 0.0;

		foreach ( var total in tree.Values )
		{
			if ( total.Frames == 0 ) continue;
			if ( total.Name is not ( "ui acquire" or "view begin" ) ) continue;

			waiting += total.Ms / frames;
		}

		if ( waiting < typical * 0.25 ) return;

		//Waiting is a fault only if the rate missed the refresh; otherwise it's normal vsync pacing
		var refresh = EngineLoop.DisplayRefreshRate;
		var reached = typical > 0 ? 1000.0 / typical : 0;
		var keepingUp = refresh <= 0 || reached >= refresh * 0.9;

		if ( keepingUp )
		{
			report.AppendLine( $"  {waiting:F2} ms of the {typical:F2} ms frame is spent waiting for the display, " +
				$"and the rate is at the refresh ({reached:F0} of {refresh:F0} Hz) - that is headroom, not a fault." );
			return;
		}

		report.AppendLine( $"  THE FRAME IS WAITING ON THE DISPLAY - {waiting:F2} ms of {typical:F2} " +
			$"is spent acquiring a picture to draw into, not drawing, and the rate is {reached:F0} against a {refresh:F0} Hz display." );

		if ( Engine.Settings.RenderSettings.Instance.VSync )
		{
			report.AppendLine( "    The wait is on. Under it the acquire blocks until the display lets an image go, " +
				"so a frame a hair over the refresh waits a whole extra one - which is how a frame rate lands on " +
				"the refresh over a whole number, at a few per cent of the machine, with nothing to see in any " +
				"profiler that measures work." );
			report.AppendLine( "    Try -vsync 0 to confirm it. If that fixes it, the frame is over budget rather than the engine being idle." );
		}
		else
		{
			report.AppendLine( "    The wait is off, so this is the graphics card being behind rather than the display - " +
				"the frame is queued faster than it can be drawn." );
		}
	}

	//Ratio of type library members actually read versus described, to judge eager reflection's cost
	private static void WriteTypeLibrary( StringBuilder report )
	{
		var known = Rinten.TypeDescription.TypesKnown;
		var asked = Rinten.TypeDescription.TypesAsked;

		if ( known == 0 ) return;

		report.AppendLine();
		report.AppendLine( "-- type library ---------------------------------------------------------" );
		report.AppendLine( $"  {known} types described, {asked} asked for their members ({asked * 100.0 / known:F0} %)" );
	}
}
