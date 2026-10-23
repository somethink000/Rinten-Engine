using NativeEngine;
using Rinten.Audio;
using Rinten.Engine;
using Rinten.Engine.Settings;
using Rinten.Network;
using Rinten.Rendering;
using Rinten.TextureLoader;
using Rinten.UI;
using Rinten.Utility;
using Rinten.VR;
using System.Threading.Channels;

namespace Rinten;

[SkipHotload]
internal static class EngineLoop
{
	static double previousTime;

	//Requests a 1ms Windows timer so frame cap sleeps aren't rounded up to the ~15ms default tick
	static EngineLoop()
	{
		if ( OperatingSystem.IsWindows() ) timeBeginPeriod( 1 );
	}

	[System.Runtime.InteropServices.DllImport( "winmm.dll" )]
	private static extern uint timeBeginPeriod( uint period );

	//Loop iterations vs frames that actually rendered. Native skips client output when it can't present.
	internal static long LoopFrames;
	internal static long RenderedFrames;

	static Superluminal _runFrame = new Superluminal( "RunFrame", "#4d5e73" );
	static Superluminal _frameStart = new Superluminal( "FrameStart", "#2c3541" );
	static Superluminal _frameEnd = new Superluminal( "FrameEnd", "#2c3541" );

	static Superluminal _sleepForFrameCap = new Superluminal( "Sleep For Max FPS", Color.Gray );

	static double GetMaxFrameRate()
	{
		if ( Application.IsHeadless ) return 60;

		//A page's frame is already paced by the browser (vsync or XR session), so it must not sleep here
		if ( OperatingSystem.IsBrowser() )
		{
			MaxFrameRateSource = Diagnostics.PerfReport.Uncapped ? "browser, uncapped" : "browser vsync";
			return 0;
		}

		//A measured run outranks every cap, warm-up included. Under one the loop spends the frame not working and the report is mostly Idle, which
		//answers "is there a cap" rather than "where does the frame go" - and a warm-up spent capped warms the machine at a rate nobody measures.
		if ( Diagnostics.PerfReport.Uncapped )
		{
			MaxFrameRateSource = "perfreport";
			return 0;
		}

		double effectiveFps = RenderSettings.Instance.MaxFrameRate;
		MaxFrameRateSource = "fps_max";

		//Menu and inactive caps tighten the active cap (and each other), applying even when it's uncapped.
		if ( Game.IsMainMenuVisible ) effectiveFps = TightenCap( effectiveFps, RenderSettings.Instance.MaxFrameRateMenu, "fps_max_menu" );

		//Whether the window is in front - a game nobody is looking at is drawn slowly on purpose.
		if ( !EngineInput.IsAppActive() )
			effectiveFps = TightenCap( effectiveFps, RenderSettings.Instance.MaxFrameRateInactive, "fps_max_inactive" );

		//Under vsync, a cap above the refresh lets the main thread race ahead of the present queue and stall on it
		//(bimodal frame delivery / judder). Clamp to the refresh instead. No-op when the cap is already at/below it.
		if ( RenderSettings.Instance.VSync )
		{
			double refresh = GetDisplayRefreshRate();
			if ( refresh > 0 && (effectiveFps <= 0 || refresh < effectiveFps) )
			{
				effectiveFps = refresh;
				MaxFrameRateSource = "vsync";
			}
		}

		return effectiveFps;
	}

	//Which cap won, for overlay_fps.
	internal static string MaxFrameRateSource { get; private set; } = "fps_max";

	//Lower of two fps caps, treating <= 0 as unlimited so a cap still applies when the other is uncapped.
	static double TightenCap( double current, int candidate, string source )
	{
		if ( candidate <= 0 ) return current;

		if ( current <= 0 || candidate < current )
		{
			MaxFrameRateSource = source;
			return candidate;
		}

		return current;
	}

	static double cachedRefreshRate;
	static FastTimer refreshRateTimer = FastTimer.StartNew();

	//Desktop refresh rate (Hz) of the monitor the game window is on, cached and re-queried every
	//couple of seconds so moving the window is picked up. Returns 0 if unknown (treat as "no clamp").
	static double GetDisplayRefreshRate()
	{
		if ( cachedRefreshRate <= 0 || refreshRateTimer.ElapsedSeconds > 2.0 )
		{
			int w = 0, h = 0;
			uint hz = 0;
			EnginePlatform.GetDesktopResolution( EnginePlatform.GetEngineWindowMonitorIndex(), ref w, ref h, ref hz );
			cachedRefreshRate = hz;
			refreshRateTimer = FastTimer.StartNew();
		}

		return cachedRefreshRate;
	}

	//For overlay_fps.
	internal static double DisplayRefreshRate => GetDisplayRefreshRate();
	internal static double EffectiveMaxFrameRate => GetMaxFrameRate();

	//Drift-compensated pacing: wake each frame on an absolute 1/fps grid rather than padding from the
	//frame's own start, so per-frame overhead doesn't accumulate into drift. Resyncs after a hitch.
	static FastTimer pacingClock = FastTimer.StartNew();
	static double nextFrameDeadlineMs;

	//RINTEN_PACING_LOG: logs the held rate and sleep slack every five seconds
	static readonly bool pacingLog = System.Environment.GetEnvironmentVariable( "RINTEN_PACING_LOG" ) is not null;
	static double pacingSaidAt;
	static long pacingFramesAt;

	static void SleepForFrameRateClamp( FastTimer frameTime )
	{
		double frameSleepMs = 0;
		double maxFps = GetMaxFrameRate();

		//Whoever shows a frame rate can show which cap it is under - a frame rate sitting far
		//below the chosen limit reads as a bug when it is the inactive cap or vsync winning.
		PerformanceStats.FrameRateCap = (float)maxFps;
		PerformanceStats.FrameRateCapSource = MaxFrameRateSource;

		double targetMilliseconds = maxFps > 0 ? 1000.0 / maxFps : 0;
		if ( targetMilliseconds > 100 ) targetMilliseconds = 100; //min is 10fps

		if ( targetMilliseconds <= 0 )
		{
			nextFrameDeadlineMs = 0; //uncapped — drop the grid
		}
		else
		{
			double nowMs = pacingClock.ElapsedMilliSeconds;

			//Next grid point is one period after the previous deadline (not after 'now') — the drift compensation. Seed from 'now' on the first frame.
			double deadlineMs = nextFrameDeadlineMs > 0 ? nextFrameDeadlineMs + targetMilliseconds : nowMs + targetMilliseconds;

			//More than a period behind (a hitch)? Resync to 'now' rather than catch up in a burst.
			if ( nowMs - deadlineMs > targetMilliseconds )
				deadlineMs = nowMs;

			if ( nowMs < deadlineMs )
			{
				using var inst = _sleepForFrameCap.Start();

				var left = deadlineMs - pacingClock.ElapsedMilliSeconds;
				PreciseSleep.Until( System.Diagnostics.Stopwatch.GetTimestamp() + (long)( left * System.Diagnostics.Stopwatch.Frequency / 1000.0 ) );

				//actual time parked this frame, including any oversleep
				frameSleepMs = pacingClock.ElapsedMilliSeconds - nowMs;
			}

			nextFrameDeadlineMs = deadlineMs;
		}

		PerformanceStats.Timings.Idle.AddMilliseconds( frameSleepMs );

		if ( pacingLog )
		{
			var atMs = pacingClock.ElapsedMilliSeconds;

			if ( pacingSaidAt == 0 ) { pacingSaidAt = atMs; pacingFramesAt = LoopFrames; }
			else if ( atMs - pacingSaidAt >= 5000 )
			{
				var frames = LoopFrames - pacingFramesAt;
				new Rinten.Diagnostics.Logger( "Perf" ).Info( $"pacing: {frames * 1000.0 / ( atMs - pacingSaidAt ):F1} fps held, cap {maxFps:F0}, sleep slack {PreciseSleep.SlackMs:F3} ms" );
				pacingSaidAt = atMs; pacingFramesAt = LoopFrames;
			}
		}

		//Feed the on-screen pacing overlay (no-op unless overlay_fps is on).
		DebugOverlay.FrameTimeGraph.Sample( frameTime.ElapsedMilliSeconds );
	}

	//Pumps the input system
	static void UpdateInput()
	{
		using var __ = PerformanceStats.Timings.Input.Scope();

		//No input service to pump here: a game on our own core reads its window's events itself, in the app system.
	}

	/// <summary>The top of a frame, before its input is read: where r_low_latency is on, the processor waits here until the card is nearly free - see CoreLatency.</summary>
	public static void BeforeInput()
	{
		try
		{
			CorePresent.Pace();
			CoreLatency.BeforeInput();
		}
		catch ( System.Exception e )
		{
			Log.Error( e );
		}
	}

	internal static void FrameStart()
	{
		ThreadSafe.AssertIsMainThread();

		//Before any frame is begun: putting a texture's new levels in place lets the device finish, and may not happen inside a recording.
		CoreTextureStreaming.Tick();

		//Update performance stats (should be called every frame)
		UpdatePerformance();

		//Whether the renderer's zones are recorded this loop - see CoreRenderProfiler.ZonesWanted.
		VkFrame.ZonesWanted = CoreRenderProfiler.ZonesWanted;
		DebugOverlay.Draw();
		UpdateInput();

		//What the window showed a frame or two ago, into a recording where one is running - taken now, once the card has finished it. See ScreenRecorder.
		ScreenRecorder.Frame();

		//Named the same way as the frame's end: the housekeeping between the stats flip and the contexts' ticks was invisible time.
		using ( Diagnostics.Performance.Scope( "Frame Start" ) )
		{
			//Each named, as the context ticks below are: one zone over the lot said a
			//browser frame spent a seventh of a millisecond here and nothing about on what.

			//Dispatch callbacks for any changed files
			using ( Diagnostics.Performance.Scope( "file watch" ) )
			{
				FileWatch.Tick();
			}

			//Update any animated textures
			using ( PerformanceStats.Timings.Video.Scope() )
			{
				Texture.Tick();
			}

			//Update VR
			using ( Diagnostics.Performance.Scope( "vr frame start" ) )
			{
				VRSystem.FrameStart();
			}

			//Expire any unused resources
			using ( Diagnostics.Performance.Scope( "resource expiry" ) )
			{
				NativeResourceCache.Tick();
				Game.Resources.PruneWeakIndex();
				Mounting.MountUtility.TickPreviewRenders();
			}

			//Run Tasks
			using ( Diagnostics.Performance.Scope( "async tasks" ) )
			{
				RunAsyncTasks();
			}
		}

		//Let the context's tick

		using ( Diagnostics.Performance.Scope( "Context Ticks" ) )
		{
			//Four calls, named one by one. Together they were forty milliseconds of self time and two and a half megabytes
			//a frame in an editor sitting still, and one zone over the lot says only that something in here is expensive.
			using ( Diagnostics.Performance.Scope( "menu tick" ) )
			{
				IMenuDll.Current?.Tick();
			}

			using ( Diagnostics.Performance.Scope( "game tick" ) )
			{
				IGameInstanceDll.Current?.Tick();
			}

			using ( Diagnostics.Performance.Scope( "menu late tick" ) )
			{
				IMenuDll.Current?.LateTick();
			}

			using ( Diagnostics.Performance.Scope( "tools tick" ) )
			{
				IToolsDll.Current?.Tick();
			}
		}


		//Run Tasks
		RunAsyncTasks();

		//Misc client systems
		if ( !Application.IsHeadless )
		{
			using ( IGameInstanceDll.Current?.PushScope() ?? default )
			{
				VoiceManager.Tick();
				Rinten.TextRendering.Tick();
			}
		}

		//If we have any queued console messages, we can print them now
		Logging.PushQueuedMessages();

		//Allow the events to push if they want
		Api.Events.TickEvents();
		Api.Stats.TickStats();

		//Simulate UI last. This works out all the styles and shit, so we want that to be reflected right BEFORE the frame is rendered.
		using ( PerformanceStats.Timings.Ui.Scope() )
		{
			SimulateUI();
		}

		//Give each sound handle an opportunity to for a frame think
		using ( PerformanceStats.Timings.Audio.Scope() )
		{
			MixingThread.UpdateGlobals();
		}

		//Update the mouse visibility status
		if ( !Application.IsHeadless )
		{
			Engine.InputRouter.Frame();
		}

		Audio.AudioEngine.Tick();
	}

	/// <summary>One frame of a game running on our own core.</summary>
	public static void RunFrameCore()
	{
		LoopFrames++;

		var time = RealTime.NowDouble;
		var frameTimer = FastTimer.StartNew();

		RealTime.Update( time );
		Time.Update( RealTime.Now, RealTime.Delta );

		DebugOverlay.Reset();

		try
		{
			FrameStart();
		}
		catch ( System.Exception e )
		{
			Log.Error( e );
		}

		try
		{
			using var _ = Diagnostics.Performance.Scope( "Client Output" );

			OnClientOutput();
		}
		catch ( System.Exception e )
		{
			Log.Error( e );
		}

		try
		{
			FrameEnd();
		}
		catch ( System.Exception e )
		{
			Log.Error( e );
		}

		//The editor's own frame, under a named zone so its allocations are attributed
		try
		{
			using var _ = Diagnostics.Performance.Scope( "Tools Frame" );

			IToolsDll.Current?.RunFrame();
		}
		catch ( System.Exception e )
		{
			Log.Error( e );
		}

		//The frame's own work, before any wait for the next one - see PerformanceStats.FrameWork. One clock read.
		PerformanceStats.FrameWork = (float)frameTimer.ElapsedMilliSeconds;

		SleepForFrameRateClamp( frameTimer );

		previousTime = time;
	}

	public static void RunAsyncTasks()
	{
		using ( PerformanceStats.Timings.Async.Scope() )
		{
			using var sceneScope = IGameInstanceDll.Current?.PushScope() ?? default;

			ThreadSafe.AssertIsMainThread();
			MainThread.RunQueues();
			SyncContext.MainThread?.ProcessQueue();
		}
	}

	internal static void FrameEnd()
	{
		ThreadSafe.AssertIsMainThread();

		//Named, because this used to be a millisecond nobody could see: the pieces of a frame that are neither simulation nor drawing
		//still cost, and a profiler that cannot say where sends people hunting in the renderer for time spent freeing strings.
		using var scope = Diagnostics.Performance.Scope( "Frame End" );

		//Run Tasks
		Engine.Streamer.CurrentService?.Tick();
		RunAsyncTasks();

		//Update VR Named because it is not a small thing hiding in here: on a headset this is where both
		//eyes are drawn, and a third of the frame used to be in "Frame End" with no zone under it saying so.
		using ( Diagnostics.Performance.Scope( "vr frame" ) )
		{
			VRSystem.FrameEnd();
		}

		//Free strings allocated by Interop shit, and let us know how many
		using ( Diagnostics.Performance.Scope( "interop free" ) )
		{
			int count = Interop.Free();
			if ( count > 10 )
			{
				//log.Trace( $"Interop Free: {count}" );
			}
		}

		//Run threaded stuff that needed to happen on the main thread
		using ( Diagnostics.Performance.Scope( "main queues" ) )
		{
			MainThread.RunQueues();
		}

		//Trigger recompile of Project
		using ( Diagnostics.Performance.Scope( "project tick" ) )
		{
			Project.Tick();
		}

		//Free anything that needs to be disposed of at end of frame
		using ( Diagnostics.Performance.Scope( "frame disposables" ) )
		{
			DrainFrameEndDisposables();

			//The buffers asked back without waiting, handed to whoever asked once they have arrived - see GpuBuffer.GetDataAsync.
			CoreReadbacks.Pump();

			//Free render targets
			RenderTarget.EndOfFrame();
		}
	}


	static unsafe void UpdatePerformance()
	{
		PerformanceStats.Frame();
		PerfReport.Frame();

		//Not Api.Performance.Frame: it gathered stats for a backend report that is never sent
	}


	static Superluminal _simulateUiGame = new Superluminal( "Simulate GameUI", "#2c3541" );
	static Superluminal _simulateUiMenu = new Superluminal( "Simulate GameUI", "#2c3541" );

	private static void SimulateUI()
	{
		ThreadSafe.AssertIsMainThread();
		VideoTextureLoader.TickVideoPlayers();
		VideoPlayer.TickAll();
		TooltipSystem.Frame();
		PanelRealTime.Update();

		using ( _simulateUiGame.Start() )
		{
			IGameInstanceDll.Current?.SimulateUI();
		}

		using ( _simulateUiMenu.Start() )
		{
			IMenuDll.Current?.SimulateUI();
		}
	}

	private static Logger nativeLogger = Logging.GetLogger( "Native" );

	static string partial = "";

	internal static void Print( int severity, string logger, string message )
	{
		partial += message;

		if ( !partial.Contains( "\n" ) )
			return;

		if ( partial.EndsWith( '\n' ) )
		{
			message = partial;
			partial = "";
		}
		else
		{
			var i = partial.LastIndexOf( '\n' );
			message = partial.Substring( 0, i );
			partial = partial.Substring( i );
		}

		message = message.TrimEnd( new[] { '\n', '\r' } );
		NLog.LogLevel level = severity switch
		{
			0 => NLog.LogLevel.Info,
			1 => NLog.LogLevel.Info,
			2 => NLog.LogLevel.Warn,
			3 => NLog.LogLevel.Warn,
			4 => NLog.LogLevel.Error,
			5 => NLog.LogLevel.Fatal,
			_ => NLog.LogLevel.Info,
		};

		var logName = $"engine/{logger}";
		nativeLogger.WriteToTargets( level, null, $"{message}", logName );
	}

	internal static void Print( bool debug, string message )
	{
		message = message.TrimEnd( new[] { '\n', '\r' } );

		if ( debug )
		{
			nativeLogger.Trace( message );
		}
		else
		{
			nativeLogger.Info( message );
		}
	}

	//A console command has arrived, or a convar has changed
	internal static void DispatchConsoleCommand( string name, string args, long flaglong )
	{
		var convar = ConVarSystem.Find( name );
		if ( convar is null )
		{
			Log.Warning( $"Unknown Command: {name}" );
			return;
		}

		convar.Run( args );
	}

	static Superluminal _clientOutput = new Superluminal( "OnClientOutput", "#3a6ea5" );
	static Superluminal _toolsRender = new Superluminal( "Tools Render", "#6e6e3a" );
	static Superluminal _gameRender = new Superluminal( "Game Render", "#3a6e4d" );
	static Superluminal _menuRender = new Superluminal( "Menu Render", "#6e3a6e" );

	internal static void OnClientOutput()
	{
		RenderedFrames++;
		CoreLatency.RenderStarts();

		using var _outputScope = _clientOutput.Start();

		//The editor renders it's own game scene
		if ( Application.IsEditor )
		{
			//A real profiler zone; the Superluminal marks here were no-ops in this process
			using ( Diagnostics.Performance.Scope( "Tools Render" ) )
			using ( _toolsRender.Start() )
				IToolsDll.Current?.OnRender();
			return;
		}

		using ( Diagnostics.Performance.Scope( "Game Render" ) )
		using ( _gameRender.Start() )
		{
			if ( IGameInstanceDll.Current is not null )
				IGameInstanceDll.Current.OnRender();
			else
				Game.Render();
		}

		using ( Diagnostics.Performance.Scope( "Menu Render" ) )
		using ( _menuRender.Start() )
			IMenuDll.Current?.OnRender();

		//Everything that was going to draw has drawn. Our renderer holds one frame open across all
		//of them - the game and the menu are one picture - so this is where it goes on the screen.
		using ( Diagnostics.Performance.Scope( "Present" ) )
			CoreGameRenderer.Present();
	}

	static Channel<IDisposable> FrameEndDisposables = Channel.CreateUnbounded<IDisposable>();

	//Queue something to be disposed of after the frame has ended and everything has finished rendering.
	internal static void DisposeAtFrameEnd( IDisposable disposable ) => FrameEndDisposables.Writer.TryWrite( disposable );

	//Drain all queued frame-end disposables immediately. Called during shutdown since no more frames will run to process them naturally.
	internal static void DrainFrameEndDisposables()
	{
		while ( FrameEndDisposables.Reader.TryRead( out var disposable ) )
		{
			disposable.Dispose();
		}
	}
}
