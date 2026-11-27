using Rinten.Diagnostics;
using Rinten.Engine;
using System;
using System.Linq;
using System.Runtime;
using System.Threading.Tasks;

namespace Rinten;

/// <summary>A game booted on the engine's own core, with no native runtime in the process.</summary>
public class CoreAppSystem : AppSystem
{
	private static readonly Logger Log = new( "CoreApp" );

	private CoreWindow window;

	//Whether this is a server rather than somebody's game: no window, and nobody to draw for. Said by whoever derives from this.
	protected virtual bool Dedicated => false;

	//Whether this process is the editor rather than a game. Said by whoever derives from this;
	//it decides what is mounted, what is loaded and which window the process comes up in.
	protected virtual bool IsEditorApp => false;

	//How many frames to run before quitting, for a boot that is only being tested. Zero runs until the window is closed.
	private int framesWanted;
	private int framesRun;

	public override void Init()
	{
		//Not on phones: low-latency GC holds more memory, and mobile runs Mono's collector anyway
		if ( !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS() )
		{
			GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
		}

		ClaimNormalPriority();

		//No native runtime is bound. Everything below has to hold without one; the flag tells the rest of the engine.

		var root = Environment.CurrentDirectory;

		//The game instance: the thing scenes belong to, and the thing the frame asks to draw. Made first because the engine's own start
		//is what bootstraps it - made after, it is never bootstrapped and has no type library, which the first scene then falls over.
		CreateGame();

		//The editor's own assembly, which registers the tools and the window they live in. Nothing native is bound: on panels there is nothing to bind it to.
		if ( IsEditorApp ) CreateEditor();

		//A shipped game says so with a manifest beside it. Read before anything else is set up,
		//because what it says - which game, which app id - decides how the rest of the start goes.
		manifest = ReadManifest();

		if ( manifest is not null )
		{
			Standalone.SetupFromManifest( manifest );
		}

		Bootstrap.PreInitCore( root,
			headless: Dedicated || Utility.CommandLine.HasSwitch( "-headless" ),
			//A server without a shipped build's manifest runs the project it is told to: not a standalone game
			standalone: !IsEditorApp && ( !Dedicated || manifest is not null ),
			dedicated: Dedicated,
			editor: IsEditorApp );

		//A server stopped by its host (docker stop, systemd, Ctrl+C) quits the normal way, so its players are told rather than timing out
		if ( Dedicated )
		{
			stopOnTerm = System.Runtime.InteropServices.PosixSignalRegistration.Create( System.Runtime.InteropServices.PosixSignal.SIGTERM, StopFromSignal );
			stopOnInterrupt = System.Runtime.InteropServices.PosixSignalRegistration.Create( System.Runtime.InteropServices.PosixSignal.SIGINT, StopFromSignal );
		}

		framesWanted = Utility.CommandLine.GetSwitchInt( "-frames", 0 );

		//-bakewebshaders <path>: builds the engine's web shaders to check they compile for browsers
		if ( Utility.CommandLine.GetSwitch( "-bakewebshaders", null ) is { Length: > 0 } webCache )
		{
			Application.WantsExit = true;
			ExitCode = CoreShaderCache.WriteForWeb( webCache ) ? 0 : 1;
			Environment.ExitCode = ExitCode;
			return;
		}

		//Compiling the engine's own shaders and writing them down, which is a build step rather than a run: no window, no project,
		//no frame. Here rather than earlier because the compiler is inside the core and the core is loaded by the bootstrap above.
		if ( Utility.CommandLine.HasSwitch( "-bakeshaders" ) )
		{
			Application.WantsExit = true;
			ExitCode = CoreShaderCache.BakeAll() ? 0 : 1;

			//Set on the process too, so a failed bake isn't reported as success after shutdown
			Environment.ExitCode = ExitCode;
			return;
		}

		//The engine's own models built beside their definitions, a build step like the shaders
		if ( Utility.CommandLine.HasSwitch( "-bakemodels" ) )
		{
			Application.WantsExit = true;
			ExitCode = BakeModels( root ) ? 0 : 1;
			Environment.ExitCode = ExitCode;
			return;
		}

		//A headset asked for at launch makes the graphics device. Settings/VR restarts the editor after a successful
		//probe and marks that one child process through its environment, rather than making the user type -vr.
		CoreHeadset.Wanted = !Application.IsHeadless && !Utility.CommandLine.HasSwitch( "-novr" ) &&
			( Utility.CommandLine.HasSwitch( "-vr" ) || manifest?.IsVRProject == true ||
				Environment.GetEnvironmentVariable( "RINTEN_ENTER_VR" ) == "1" );

		CoreHeadset.Begin();

		//A headless run must not open Vulkan next to the launcher.
		if ( IsEditorApp && !Application.IsHeadless )
		{
			if ( !CoreRenderDevice.Initialize() )
			{
				Log.Error( $"could not open a graphics device: {CoreRenderDevice.Description}" );
				Application.WantsExit = true;
			}
		}
		else if ( !Application.IsHeadless && !OpenWindow() )
		{
			Log.Error( "no window to draw into" );
			Application.WantsExit = true;
		}

		//The editor's project is loaded from inside here, by the tools, and the window it opens is the editor itself.
		Bootstrap.Init();

		if ( IsEditorApp ) return;

		if ( manifest is not null )
		{
			Log.Info( $"loading {manifest.Ident}" );

			loading = IGameInstanceDll.Current.LoadGamePackageAsync( manifest.Ident, GameLoadingFlags.Host, default );
			return;
		}

		//A window that is panels and nothing else - what the editor is being rebuilt as. Here while
		//it is being written, so it can be looked at and clicked on without an editor around it.
		if ( Utility.CommandLine.HasSwitch( "-uiwindow" ) )
		{
			OpenShell();
			return;
		}

		StartScene();
	}

	//Never inlined: it names Rinten.Tools, which a shipped game has not got
	[System.Runtime.CompilerServices.MethodImpl( System.Runtime.CompilerServices.MethodImplOptions.NoInlining )]
	private static bool BakeModels( string root ) => EngineModels.BakeAll( root );

	private CoreUiWindow shellWindow;

	//Opens the editor shell in a window of its own.
	private void OpenShell()
	{
		shellWindow = CoreUiWindow.Open( "rinten editor", 1100, 700 );

		if ( shellWindow is null ) return;

		//Said out loud: a window is made off the screen and goes on it when it has a frame to go on with. Nothing else asks this one to be shown.
		shellWindow.Shown = true;

		var shell = CoreUiShell.Build( shellWindow.Root, "rinten editor" );

		//The console first: while the engine is being debugged it is the tool that earns its place, and it is the
		//one that proves the rest works - text that arrives on its own, a field to type in, a button to press.
		shell.AddTool( "Console", () => new EditorConsole() );

		foreach ( var name in new[] { "Scene", "Assets", "Inspector", "Settings" } )
			shell.AddTool( name );
	}

	//What the shipped game is, or null where this is not one.
	private StandaloneManifest manifest;

	//The game package being brought up, while it is.
	private Task loading;

	//The manifest a shipped game carries, if this is one. The same file the ordinary standalone reads. Its absence is the ordinary
	//case in a checkout - there is no game here, only the engine - and then the window shows a scene or the menu instead.
	private static StandaloneManifest ReadManifest()
	{
		var path = System.IO.Path.Combine( Standalone.GamePath, Standalone.ManifestName );

		if ( !System.IO.File.Exists( path ) ) return null;

		try
		{
			return System.Text.Json.JsonSerializer.Deserialize<StandaloneManifest>( System.IO.File.ReadAllText( path ) );
		}
		catch ( Exception e )
		{
			Log.Warning( $"there is a game manifest here but it cannot be read: {e.Message}" );
			return null;
		}
	}

	//Loads the command-line scene (or an empty one) so the renderer draws a real scene
	private void StartScene()
	{
		if ( Application.IsHeadless ) return;

		//In the game's own context: there is one of these per side of the process - menu, game, editor - and
		//the active scene belongs to the context it was set in. Set outside, the game would never find it.
		using var scope = GlobalContext.GameScope();

		var scene = new Scene();
		Game.ActiveScene = scene;
		Game.IsPlaying = true;

		var path = Utility.CommandLine.GetSwitch( "-scene", null );

		if ( !string.IsNullOrWhiteSpace( path ) )
		{
			var file = ResourceLibrary.Get<SceneFile>( path );

			if ( file is null ) Log.Warning( $"no scene at {path}" );
			else scene.Load( file );
		}

		//Something has to be looking at it. A scene with no camera draws nothing at all, which looks exactly
		//like a renderer that does not work - so an empty scene gets one and the window shows the sky.
		if ( !scene.Camera.IsValid() )
		{
			var camera = scene.CreateObject();
			camera.Name = "camera";
			camera.WorldPosition = new Vector3( -200, 0, 100 );
			camera.WorldRotation = Rotation.From( 20, 0, 0 );
			camera.Components.Create<CameraComponent>();
		}
	}

	//The JavaVM and the activity, on Android, for a headset: the OpenXR loader and the runtime are both handed them
	//before anything else - see CoreXr. Set by the launcher before the app system runs; nought everywhere else.
	internal static (nint JavaVm, nint Activity) AndroidXr
	{
		set
		{
			CoreXr.AndroidVm = value.JavaVm;
			CoreXr.AndroidActivity = value.Activity;
		}
	}

	System.Runtime.InteropServices.PosixSignalRegistration stopOnTerm;
	System.Runtime.InteropServices.PosixSignalRegistration stopOnInterrupt;

	static void StopFromSignal( System.Runtime.InteropServices.PosixSignalContext context )
	{
		context.Cancel = true;
		Application.WantsExit = true;
	}

	//Platform-provided window source (e.g. Android surface); otherwise we open our own
	internal static Func<CoreWindow> WindowSource { get; set; }

	private bool OpenWindow()
	{
		if ( WindowSource is { } given )
		{
			window = given();
		}
		else if ( IsEditorApp )
		{
			window = CoreWindow.Create( EngineExecutables.Editor, 1280, 720 );
		}
		else
		{
			window = OpenGameWindow();
		}

		if ( window is null )
		{
			Log.Error( $"could not make a window: {CoreWindow.LastError}" );
			return false;
		}

		if ( !CoreRenderDevice.Initialize() )
		{
			Log.Error( $"could not open a graphics device: {CoreRenderDevice.Description}" );
			return false;
		}

		//The window to draw into. It answers both questions the renderer has of it - what to
		//make a surface from, and how big it is in pixels - on whichever window system this is.
		CoreGameRenderer.Window = window;

		CoreInput.Window = window.Handle;
		return true;
	}

	//Game window with its name, saved size and mode; fullscreen set before device creation
	private CoreWindow OpenGameWindow()
	{
		var settings = Rinten.Engine.Settings.RenderSettings.Instance;

		var title = manifest?.Name;
		if ( string.IsNullOrWhiteSpace( title ) ) title = manifest?.Ident;
		if ( string.IsNullOrWhiteSpace( title ) ) title = EngineExecutables.Editor;

		var width = Math.Max( settings.ResolutionWidth, 640 );
		var height = Math.Max( settings.ResolutionHeight, 480 );

		var made = CoreWindow.Create( title, width, height );

		//Full screen, a borderless window over the screen, or framed at the size - the same as a settings menu's Apply puts it in later.
		settings.ApplyVideoMode( made );

		return made;
	}

	protected override bool RunFrame()
	{
		if ( Application.WantsExit ) return false;

		//A headless tool run is done after Init; a dedicated server is headless and runs until told to quit
		if ( Application.IsHeadless && !Application.IsDedicatedServer ) return false;

		//A phone's window is lent: while the game is in the background, or the system has the surface
		//back, this is where the frame waits - and where the window it comes back to is a new one.
		if ( CoreLifecycle.Hold() is { } given ) window = given;

		if ( Application.WantsExit ) return false;

		//Before anything is read of the player: where the low-latency setting is on, the frame starts as late as the card allows - see CoreLatency.
		EngineLoop.BeforeInput();

		//The window's own messages: without pumping them it stops answering, and closing it is the only way out of this loop.
		if ( window is not null )
		{
			window.PollEvents();

			if ( window.CloseRequested ) return false;
		}

		//Every window that is panels: its own events, its own layout, its own picture. One of
		//them is the editor's shell, and a tool that opens a window of its own is another.
		CoreUiWindow.FrameAll();

		if ( (shellWindow is not null || IsEditorApp) && CoreUiWindow.OpenCount == 0 ) return false;

		//The editor's work that belongs between frames - opening a project - after the windows have had this one and before the engine's.
		IToolsDll.Current?.BetweenFrames();

		EngineLoop.RunFrameCore();

		//The game package, while it is coming up. Waited on here rather than awaited, because the
		//frame has to keep running for it to finish - it loads through the main thread's queues.
		if ( loading is not null && loading.IsCompleted )
		{
			//Throws where the load failed, which is the only place anybody would see why.
			loading.GetAwaiter().GetResult();
			loading = null;

			if ( Utility.CommandLine.HasSwitch( "host" ) )
				Networking.CreateLobby( new() );
			else if ( Utility.CommandLine.GetSwitch( "connect", null ) is { Length: > 0 } target )
				Networking.Connect( target.Trim( '"' ) );
		}

		framesRun++;

		return framesWanted <= 0 || framesRun < framesWanted;
	}

	public override void Shutdown()
	{
		stopOnTerm?.Dispose();
		stopOnInterrupt?.Dispose();

		CoreGameRenderer.Shutdown();

		shellWindow?.Dispose();
		shellWindow = null;

		window?.Dispose();
		window = null;

		//The scene goes before the base teardown, which counts what is left and warns about scenes nobody destroyed - this one is ours to destroy.
		using ( GlobalContext.GameScope() )
		{
			Game.ActiveScene?.Destroy();
			Game.ActiveScene?.ProcessDeletes();
			Game.ActiveScene = null;
		}

		CoreRenderDevice.Shutdown();

		base.Shutdown();
	}

	//Raises an inherited below-normal process priority to Normal; never lowers or overrides higher
	private static void ClaimNormalPriority()
	{
		//Nor on a phone, where what a process is worth is the system's business and not the game's: Android schedules an app by what
		//it is doing, and System.Diagnostics.Process there is a reader of /proc that a game has no reason to be opening at all.
		if ( OperatingSystem.IsBrowser() || OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() ) return;

		try
		{
			using var self = System.Diagnostics.Process.GetCurrentProcess();

			//Named rather than compared. ProcessPriorityClass carries the Win32 flag values, not an ordering: BelowNormal is 16384 and Normal
			//is 32, so "priority >= Normal" is true of a below-normal process and the first version of this quietly did nothing at all.
			var low = self.PriorityClass is System.Diagnostics.ProcessPriorityClass.Idle
				or System.Diagnostics.ProcessPriorityClass.BelowNormal;

			if ( !low ) return;

			var was = self.PriorityClass;
			self.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal;

			Log.Info( $"priority was {was}, taken back to Normal" );
		}
		catch ( Exception e )
		{
			//A process that may not set its own priority is a process that runs where somebody decided that, and it still has a frame to draw.
			Log.Info( $"could not take back a normal priority: {e.Message}" );
		}
	}

}
