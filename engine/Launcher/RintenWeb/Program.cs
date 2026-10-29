using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Rinten.Engine;

namespace Rinten;

public static partial class Program
{
	//What the build says it is. Empty where it was not exported by the editor.
	static WebManifest game = new();

	static GameObject flyCamera;
	static Angles flyLook = new( 20, 0, 0 );

	public static async Task Main( string[] args )
	{
		try
		{
			//The page's switches arrive here and nowhere else: a module compiled whole has no Environment.CommandLine to fill from runMain, as Mono's runtime did.
			Rinten.Utility.CommandLine.CommandLineString = "Rinten-Web " + string.Join( " ", args ?? [] );
			Rinten.Utility.CommandLine.Parse();

			//One thread, finalizers inline - see SkiaLock.
			SkiaLock.Install();

			Application.IsWeb = true;

			//The build's content is written into the runtime's own filesystem, and the engine is
			//started in it, the way a desktop game is started in its folder - see WebContent.
			System.IO.Directory.CreateDirectory( WebContent.Root );
			System.IO.Directory.SetCurrentDirectory( WebContent.Root );

			//The engine's warnings and errors in the browser's console, where a page's are read. Its info
			//lines stay in the engine: a page that says everything the editor says is a console nobody reads.
			Rinten.Diagnostics.Logging.Tap += e =>
			{
				if ( e.Level < LogLevel.Warn ) return;
				Console.WriteLine( e.Exception is null
					? $"{e.Level} {e.Logger}: {e.Message}"
					: $"{e.Level} {e.Logger}: {e.Message}\n{e.Exception}" );
			};
			Console.WriteLine( $"rinten web browser={OperatingSystem.IsBrowser()}" );

			Phase( null );
			Native.Say( "Graphics…" );
			await Native.WhenReady();
			WebBind.Attach();

			//The renderer's own half of the page. Separate from WebBind because the WebGPU imports are a whole backend's worth and belong together.
			RenderBind.Attach();

			//The headset's, the same way - see XrPage. Bound whether or not this build wants one: what
			//it costs is a few delegates, and what asking later would cost is knowing when to ask.
			XrPage.Attach();

			//Opens the GPU device before the engine starts, since frames can't await promises
			if ( !await BrowserDevice.Init( 0 ) )
			{
				var info = BrowserDevice.infoText;
				Console.WriteLine( info );
				Native.Say( info );
				return;
			}

			Console.WriteLine( $"rinten web {BrowserDevice.infoText}" );

			game = await LoadManifest();
			//What code the page was compiled from - what a server's code is checked against.
			WebCode.CompiledFrom( game.Code );
			if ( !string.IsNullOrWhiteSpace( game.Name ) )
				Native.SetTitle( game.Name );

			//Exported for the one making it - see Application.IsDebugBuild.
			Application.DebugExport = game.Debug;
			//A page the editor did not export is the core's dev build: nothing to introduce
			Rinten.Splash.EngineSplash.Picture = string.IsNullOrWhiteSpace( game.Ident ) ? null : game.Splash;

			//Whether to look for a headset. index.html settled this before the graphics context was made - it had to, a
			//context that draws to one says so when it is created - so this reads its answer rather than working it out again.
			CoreHeadset.Wanted = XrPage.Asked() == 1;

			Phase( "device" );
			Native.Say( "Assets…" );
			await PreloadWebAssets();
			await Native.NextFrame();

			Phase( "assets" );
			Native.Say( "Game instance…" );
			GameInstanceDll.Create();
			await Native.NextFrame();

			Phase( "game instance" );
			Native.Say( "Filesystem…" );
			Bootstrap.PreInitWebFilesystem( "." );
			Bootstrap.PreInitWebHost();
			WebContent.Mount();
			await Native.NextFrame();

			Phase( "filesystem" );
			Native.Say( "Code…" );
			await LoadGameAssemblies();

			//The project's mixers, as a desktop game's instance reads them; after the code, whose types a mixer's processors may be.
			//Without a master mixer every buffer the device asks for is silence
			Rinten.Audio.Mixer.LoadFromSettings( ProjectSettings.Mixer, Game.TypeLibrary );
			await Native.NextFrame();

			CoreWindow window = null;
			try
			{
				Phase( "code" );
				Native.Say( "Window…" );
				window = CoreWindow.Create( game.Name ?? "Rinten", Native.CanvasWidth(), Native.CanvasHeight() );
				if ( window is null )
				{
					Native.Say( "no canvas" );
					return;
				}

				if ( !CoreRenderDevice.Initialize() )
				{
					Native.Say( CoreRenderDevice.Description );
					return;
				}

				Console.WriteLine( CoreRenderDevice.Description );

				CoreGameRenderer.Window = window;
				CoreInput.Window = window.Handle;

				Phase( "window" );
				Native.Say( "Bootstrap…" );
				await Native.NextFrame();
				Bootstrap.Init();
				Phase( "bootstrap" );

				Native.Say( "Resources…" );
				await ResourceLoader.LoadAllGameResourceAsync( EngineFileSystem.Mounted );
				await Native.NextFrame();
				Phase( "resources" );

				Native.Say( "Scene…" );
				await Native.NextFrame();

				//What only the first scene uses comes down now, so that it loads whole and its startup runs on what it loaded - see WebContent.SceneReady.
				if ( StartScenePath() is { } first )
					await WebContent.SceneReady( first );

				if ( !StartScene() )
					StartEmptyScene();

				Phase( "scene" );
				Console.WriteLine( $"web start {phases}" );

				//A headset, where this build asked for one and the browser has it. Nothing is opened here:
				//only a gesture may open a session, so what this does is let the page draw the way in.
				CoreHeadset.Begin();

				Native.Say( "" );

				//One frame's work; the loop calls it normally, the XR session's frame callback during VR
				void Tick()
				{
					window.PollEvents();
					SteerCamera();
					EngineLoop.RunFrameCore();
				}

				XrBind.SetFrame?.Invoke( Tick );

				//Run by the page, which calls Step once a turn - see runLoop in native.js for why it is not
				//an await here. What stops the loop is kept and thrown on this side, where it always was.
				Exception failed = null;

				int Step()
				{
					if ( Application.WantsExit || window.CloseRequested ) return 0;

					try
					{
						if ( !CoreHeadset.Driving ) Tick();
					}
					catch ( Exception e )
					{
						failed = e;
						return 0;
					}

					//The display's clock: a page shows one picture a refresh, and turns run past it starve the browser's
					//own drawing and input. Only a measured run goes as fast as the frame does.
					return Rinten.Diagnostics.PerfReport.Uncapped && !CoreHeadset.Driving ? 1 : 2;
				}

				//What only other scenes use comes down now, a file at a time between frames, so
				//a scene the player opens later has mostly arrived - see WebContent.FetchTheRest.
				WebContent.FetchTheRest();

				await Native.RunLoop( Step );

				if ( failed is not null ) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw( failed );
			}
			finally
			{
				CoreGameRenderer.Window = null;
				window?.Dispose();
			}
		}
		catch ( Exception e )
		{
			Console.WriteLine( e.ToString() );
			Native.Say( $"{e.GetType().Name}: {e.InnerException?.Message ?? e.Message}" );
		}
	}

	//One on-demand file fetched and written where the engine looks for it, through a buffer outside the collector's heap - see WebContent.Fetch.
	static async Task<bool> FetchInPlace( string path )
	{
		var length = await Native.FetchBytes( WebContent.Asked( path ) );
		if ( length <= 0 ) return false;

		WebContent.Place( path, length, Native.TakeFetchInto );
		return true;
	}

	static readonly System.Diagnostics.Stopwatch phaseClock = new();
	static readonly StringBuilder phases = new();

	//Closes the start-up step that was running and says how long it took, in one line once the scene is up - a page
	//has no StartupTiming , and the gaps between its log lines belong to whatever logged last. Null starts the clock.
	static void Phase( string finished )
	{
		if ( finished is not null )
			phases.Append( $"{finished} {phaseClock.Elapsed.TotalMilliseconds:0} ms, " );

		phaseClock.Restart();
	}

	//Loads the export manifest; a build without one is valid but has no game name or code
	static async Task<WebManifest> LoadManifest()
	{
		var text = await Native.FetchText( WebBuild.ManifestName );
		if ( string.IsNullOrWhiteSpace( text ) )
		{
			Console.WriteLine( $"no {WebBuild.ManifestName} - running whatever is here" );
			return new WebManifest();
		}

		try
		{
			var manifest = JsonSerializer.Deserialize<WebManifest>( text,
				new JsonSerializerOptions { PropertyNameCaseInsensitive = true } );

			if ( manifest is null ) return new WebManifest();

			Console.WriteLine( $"web build {manifest.Name} ({manifest.Ident}) scene={manifest.StartScene} " +
				$"assemblies={manifest.Assemblies?.Count ?? 0}" );

			return manifest;
		}
		catch ( Exception e )
		{
			Console.WriteLine( $"{WebBuild.ManifestName} did not parse: {e.GetType().Name}: {e.Message}" );
			return new WebManifest();
		}
	}

	//Prefetches every exported file, since the engine reads content synchronously
	static async Task PreloadWebAssets()
	{
		var manifest = await Native.AssetManifest();
		if ( string.IsNullOrWhiteSpace( manifest ) )
		{
			Console.WriteLine( $"{WebBuild.AssetListName} missing - the build has no content list" );
			return;
		}

		var lines = manifest
			.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
			.Where( x => !x.StartsWith( '#' ) )
			.Select( x => x.Replace( '\\', '/' ) )
			.ToArray();

		//What the game fetches when it first asks for it, not now - see WebBuild.OnDemand. Fetched one at a time through the page's own fetch.
		var later = lines.Where( x => x[0] == WebBuild.OnDemand ).Select( x => x[1..] ).ToArray();
		var paths = lines.Where( x => x[0] != WebBuild.OnDemand ).ToArray();

		//What the build carries packed: the page asks for path.gz and opens it - see WebBuild.Packed and native.js, which reads the mark.
		WebContent.Packed( later.Concat( paths ).Where( x => x[0] == WebBuild.Packed ).Select( x => x[1..] ) );
		later = later.Select( x => x.TrimStart( WebBuild.Packed ) ).ToArray();

		WebContent.OnDemand( later );
		WebContent.Fetch = FetchInPlace;

		//Which of those the scenes need - see WebBuild.SceneListName: a scene that names one waits for it, and the rest come down while the game runs.
		WebContent.ForScenes( await Native.FetchText( WebBuild.SceneListName ) );

		//Downloaded by the page rather than one at a time from here: a game is hundreds of files, and a round trip
		//each would be most of the wait. The bar moves from there too, because that is where the answers land.
		await Native.Prefetch( string.Join( '\n', paths ), 12 );

		var loaded = 0;
		var failed = 0;

		for ( var i = 0; i < paths.Length; i++ )
		{
			//Asked for as listed - its mark says how - and kept under its own name, copied straight into place
			//rather than through an array on the heap: the collector was the most of a page's start (plan В5).
			var asked = paths[i];
			paths[i] = paths[i].TrimStart( WebBuild.Packed );

			var length = Native.FileLength( asked );

			if ( length <= 0 )
			{
				failed++;
				Console.WriteLine( $"web asset missing {paths[i]}" );
				continue;
			}

			WebContent.Place( paths[i], length, into => Native.TakeFileInto( asked, into ) );
			loaded++;

			//Copying a build's worth of files out of the page is work, and a page that never yields while doing it is a page that has stopped.
			if ( (i & 63) == 0 ) await Native.NextFrame();
		}

		Native.Progress( paths.Length, paths.Length );
		Console.WriteLine( $"web assets loaded={loaded} failed={failed}" );
	}

	//Loads the game's exported assemblies in compile order and registers their types
	static Task LoadGameAssemblies()
	{
		//Compiled into the module with the engine - the export builds them in (plan, stage Н) - so there is nothing to fetch: only the library to fill.
		if ( game.Assemblies is not { Count: > 0 } ) return Task.CompletedTask;

		foreach ( var path in game.Assemblies )
		{
			if ( string.IsNullOrWhiteSpace( path ) ) continue;

			var name = System.IO.Path.GetFileNameWithoutExtension( path );

			try
			{
				WebCode.Baked( name );
			}
			catch ( Exception e )
			{
				Console.WriteLine( $"{name} is not in the module: {e.GetType().Name}: {e.Message}" );
			}
		}

		return Task.CompletedTask;
	}

	//Which scene this build starts in.
	static string StartScenePath()
	{
		if ( !string.IsNullOrWhiteSpace( game.StartScene ) ) return game.StartScene;
		if ( WebContent.Has( "web-start.scene" ) ) return "web-start.scene";

		return null;
	}

	//Starts the game, the same way a desktop build starts one. Additive onto an empty scene, then the system scene, then the startup events - that
	//order is what GameInstance.OpenStartupScene does, and a game whose code runs from ISceneStartup only runs at all if the page does it too.
	static bool StartScene()
	{
		var path = StartScenePath();
		if ( path is null )
		{
			Console.WriteLine( "this build names no scene to start" );
			return false;
		}

		using var scope = GlobalContext.GameScope();

		var scene = new Scene();
		Game.ActiveScene = scene;
		Game.IsPlaying = true;

		var options = new SceneLoadOptions { ShowLoadingScreen = false, IsAdditive = true };

		if ( !options.SetScene( path ) && !ReadSceneFile( options, path ) )
		{
			Console.WriteLine( $"{path} is not in this build" );
			return false;
		}

		scene.RunEvent<ISceneStartup>( x => x.OnHostPreInitialize( options.GetSceneFile() ) );

		if ( !scene.Load( options ) )
		{
			Console.WriteLine( $"{path} did not load" );
			return false;
		}

		scene.AddSystemScene();
		scene.RunEvent<ISceneStartup>( x => x.OnHostInitialize() );
		scene.RunEvent<ISceneStartup>( x => x.OnClientInitialize() );

		TakeCamera( scene );

		Console.WriteLine( $"started {path} with {scene.Children.Count()} objects" );
		return true;
	}

	//The scene read straight out of the build, for one the resource system never registered - a loose web-start.scene put beside the page.
	static bool ReadSceneFile( SceneLoadOptions options, string path )
	{
		var full = System.IO.Path.Combine( WebContent.Root, path );
		if ( !System.IO.File.Exists( full ) ) return false;

		var bytes = System.IO.File.ReadAllBytes( full );

		try
		{
			var root = Json.ParseToJsonObject( Encoding.UTF8.GetString( bytes ) );
			if ( root is null ) return false;

			var sceneFile = new SceneFile();
			sceneFile.Register( path );

			if ( Guid.TryParse( root["__guid"]?.ToString(), out var id ) )
				sceneFile.Id = id;

			sceneFile.GameObjects = root["GameObjects"] is JsonArray objects
				? objects.OfType<JsonObject>().ToArray()
				: [];
			sceneFile.SceneProperties = root["SceneProperties"] as JsonObject;

			return options.SetScene( sceneFile );
		}
		catch ( Exception e )
		{
			Console.WriteLine( $"{path} did not parse: {e.GetType().Name}: {e.Message}" );
			return false;
		}
	}

	//Something to look at when the build has no scene, so a page that got this far shows that it did rather than a black canvas.
	static void StartEmptyScene()
	{
		using var scope = GlobalContext.GameScope();

		var scene = new Scene();
		Game.ActiveScene = scene;
		Game.IsPlaying = true;

		var sun = scene.CreateObject();
		sun.Name = "sun";
		sun.WorldRotation = Rotation.From( 50, 40, 0 );
		sun.Components.Create<DirectionalLight>();

		var prop = scene.CreateObject();
		prop.Name = "cube";
		prop.Components.Create<ModelRenderer>().Model = Model.Cube;

		TakeCamera( scene, force: true );
	}

	//Gives the page a camera to fly where the game has none of its own. A shipped game has a camera and code that moves it, and flying it from
	//here would be two things steering one object. This is for the other case: a scene exported to be looked at, which has nothing to drive it.
	static void TakeCamera( Scene scene, bool force = false )
	{
		//Which camera the scene draws through is worked out once a frame, and no frame
		//has run yet - so ask now, or a scene with a camera gets a second one made for it.
		scene.UpdateMainCamera();

		var camera = scene.Camera;

		if ( camera is not null && !force && !game.FreeCamera )
			return;

		if ( camera is null )
		{
			var made = scene.CreateObject();
			made.Name = "camera";
			made.WorldPosition = new Vector3( -200, 0, 100 );
			var component = made.Components.Create<CameraComponent>();
			component.ZFar = 10000;
			camera = component;
		}

		flyCamera = camera.GameObject;
		flyLook = flyCamera.WorldRotation.Angles();

		//A request rather than a capture: a browser hands the pointer over during a gesture and at no other time, so this locks on the next click.
		Native.SetPointerLock( 1 );
	}

	static void SteerCamera()
	{
		if ( flyCamera is null || !flyCamera.IsValid() ) return;

		var look = CoreInput.WebLook;
		if ( Native.PointerLocked() != 0 )
		{
			flyLook.yaw -= look.x * 0.12f;
			flyLook.pitch = Math.Clamp( flyLook.pitch + look.y * 0.12f, -89f, 89f );
			flyCamera.WorldRotation = Rotation.From( flyLook );
		}

		var rot = flyCamera.WorldRotation;
		var move = Vector3.Zero;
		if ( Native.KeyDown( "KeyW" ) != 0 || Native.KeyDown( "ArrowUp" ) != 0 ) move += rot.Forward;
		if ( Native.KeyDown( "KeyS" ) != 0 || Native.KeyDown( "ArrowDown" ) != 0 ) move += rot.Backward;
		if ( Native.KeyDown( "KeyA" ) != 0 || Native.KeyDown( "ArrowLeft" ) != 0 ) move += rot.Left;
		if ( Native.KeyDown( "KeyD" ) != 0 || Native.KeyDown( "ArrowRight" ) != 0 ) move += rot.Right;
		if ( Native.KeyDown( "KeyE" ) != 0 || Native.KeyDown( "Space" ) != 0 ) move += Vector3.Up;
		if ( Native.KeyDown( "KeyQ" ) != 0 ) move += Vector3.Down;
		if ( move.IsNearZeroLength ) return;

		var speed = Native.KeyDown( "ShiftLeft" ) != 0 || Native.KeyDown( "ShiftRight" ) != 0 ? 400f : 160f;
		var dt = Time.Delta > 0 ? Time.Delta : 1f / 60f;
		flyCamera.WorldPosition += move.Normal * speed * dt;
	}
}
