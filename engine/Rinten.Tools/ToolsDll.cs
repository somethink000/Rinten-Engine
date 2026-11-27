using NativeEngine;
using Rinten.Audio;
using Rinten.Engine;
using Rinten.Internal;
using System;

namespace Rinten;

internal class ToolsDll : IToolsDll
{
	static Logger log = new Logger( "ToolsDll" );

	PackageLoader.Enroller AssemblyEnroller { get; set; }

	public ToolsDll()
	{
		Global.Assembly = GetType().Assembly;
	}

	public void Bootstrap()
	{
		EditorEvent.Init();
		EditorEvent.RegisterAssembly( Global.Assembly );

		Game.IsPlaying = false;

		using ( var _ = Rinten.Engine.Bootstrap.StartupTiming?.ScopeTimer( "Tools PreInit: Type Library" ) )
		{
			EditorTypeLibrary = new Rinten.Internal.TypeLibrary();
			EditorTypeLibrary.AddIntrinsicTypes();
			EditorTypeLibrary.AddAssembly( typeof( Vector3 ).Assembly, false );
			EditorTypeLibrary.AddAssembly( Global.Assembly, true ); //editor can access everything anyway
			EditorTypeLibrary.AddAssembly( typeof( EngineLoop ).Assembly, false );
			EditorTypeLibrary.AddAssembly( typeof( Facepunch.ActionGraphs.ActionGraph ).Assembly, false );
		}

		Rinten.Internal.TypeLibrary.Editor = EditorTypeLibrary;

		Rinten.Generator.Processor.DefaultPackageAssetResolver = ResolvePackageAsset;

		EditorCookie = new CookieContainer( "tools" );

		//make sure mixer is initialized at least once
		using ( var _ = Rinten.Engine.Bootstrap.StartupTiming?.ScopeTimer( "Tools PreInit: Audio" ) )
			Mixer.ResetToDefault();

		using ( var _ = Rinten.Engine.Bootstrap.StartupTiming?.ScopeTimer( "Tools PreInit: Shortcuts" ) )
			EditorShortcuts.RegisterShortcuts();

		//Every key any of our windows hears, whatever has the focus. A shortcut is the window's rather than the focused panel's, and
		//most of the editor's widgets never take the focus at all - handed on through the focus alone, almost none of them ever fired.
		CoreUiWindow.KeyHeard = OnKeyHeard;
	}

	//A key one of the editor's windows heard.
	private static void OnKeyHeard( CoreWindow.Key key )
	{
		EditorShortcuts.Typed( key.Button, key.Down, key.Repeat, key.Modifiers );
	}

	public void Exiting()
	{
		EditorEvent.Run( "app.exit" );
		EditorCookie?.Save();
		ProjectCookie?.Save();

		//Lets go of the port, and of the pid file that says this process holds it
		Editor.Mcp.McpServer.Stop();
	}

	public void RunEvent( string name ) => EditorEvent.Run( name );
	public void RunEvent( string name, object argument ) => EditorEvent.Run( name, argument );
	public void RunEvent( string name, object arg0, object arg1 ) => EditorEvent.Run( name, arg0, arg1 );
	public void RunEvent<T>( Action<T> action ) => EditorEvent.RunInterface<T>( action );

	/// <summary>Called from <see cref="EngineLoop.OnClientOutput"/>, this method exists because we can't access the scene editor session from GameMenuDll to render cameras in the game viewport FROM the editor scene and we probably don't want to either</summary>
	public void OnRender()
	{
		SceneRenderCore.RenderAll();
	}

	public bool ConsoleFocus()
	{
		EditorWindow.ConsoleFocus();
		return true;
	}

	public void ExitPlaymode()
	{
		if ( !Game.IsPlaying )
			return;

		EditorScene.Stop();
	}

	public void SetPlaying()
	{
		if ( Game.ActiveScene is null || !Game.ActiveScene.IsValid() )
		{
			return;
		}

		//A host's snapshot made this scene: the editor plays it as it is, loading nothing of its own over it
		var session = SceneEditorSession.All.FirstOrDefault( x => x.GameSession is not null )
			?? ( SceneEditorSession.Active is GameEditorSession game ? game.Parent : SceneEditorSession.Active );

		if ( session is null || session.GameSession?.Scene == Game.ActiveScene ) return;

		session.StopPlaying();

		Game.IsPlaying = true;
		session.SetPlaying( Game.ActiveScene );
		EditorEvent.Run( "scene.play" );
	}

	/// <summary>Bootstrapping has finished. Close the splash screen and show the editor.</summary>
	public Task Initialize()
	{
		//The editor's own start, where their tool framework is not the one starting it. Before
		//anything else: the rest of this asks the bind system and the filesystem for things.
		if ( EditorBackend.Panels )
		{
			using var _ = Rinten.Engine.Bootstrap.StartupTiming?.ScopeTimer( "Tools Init: Managed Start" );
			ManagedTools.InitStartManaged();
		}

		//Register the editor sheet theme before any dock, even without a project, or sheets parse broken
		EditorSheetPreamble.Register();

		//Associate the project file with the editor
		FileAssociations.Create();

		//The editor's own projects are compiled after its window is up, not before - see StartupLoadProject.LoadBuiltIns.

		//Rebuild content path now because some projects are added during bootstrap
		using ( var _ = Rinten.Engine.Bootstrap.StartupTiming?.ScopeTimer( "Tools Init: Styles" ) )
		{
			Editor.FileSystem.RebuildContentPath();
			ManagedTools.WatchEditorStyles();
			Editor.Application.ReloadStyles();
		}

		Assert.NotNull( GameInstanceDll.PackageLoader );
		Assert.NotNull( GlobalGameNamespace.TypeLibrary );

		Rinten.GameInstanceDll.PackageLoader.ToolsMode = true;

		AssemblyEnroller = Rinten.GameInstanceDll.PackageLoader.CreateEnroller( "tools" );

		Rinten.GameInstanceDll.PackageLoader.HotloadWatch( GetType().Assembly );
		Rinten.GameInstanceDll.PackageLoader.OnAfterHotload = () =>
		{
			//here we're overriding what the GameMenu set, so we're gonna do that stuff too.
			GlobalContext.Current.OnHotload();

			foreach ( var session in SceneEditorSession.All )
			{
				session.Scene.OnHotload();
			}

			Event.Run( "hotloaded" );

			//editor stuff
			EditorEvent.Run( "hotloaded" );
		};

		AssemblyEnroller.OnAssemblyAdded = ( assembly ) =>
		{
			Log.Trace( $"Tools - Loading Assembly {assembly.Assembly} ({assembly.IsEditorAssembly})" );

			if ( assembly.IsEditorAssembly )
			{
				EditorEvent.RegisterAssembly( assembly.Assembly );
				ConVarSystem.AddAssembly( assembly.Assembly, $"editor.{assembly.Name.Replace( ".editor", "" )}" );
				Rinten.Mounting.Directory.AddAssembly( assembly.Assembly );
			}

			EditorTypeLibrary.AddAssembly( assembly.Assembly, true );

			if ( !StartupLoadProject.IsLoading )
			{
				//Can't do this during bootstrapping in case a resource references a type that hasn't been loaded yet.
				if ( AssetType.UpdateCustomTypes() )
				{
					//If the types changed, re-import all the custom types from disk
					AssetType.ImportCustomTypeFiles();
				}
			}
		};

		AssemblyEnroller.OnAssemblyRemoved = ( outgoing ) =>
		{
			if ( outgoing.IsEditorAssembly )
			{
				EditorEvent.UnregisterAssembly( outgoing.Assembly );
				ConVarSystem.RemoveAssembly( outgoing.Assembly );
				Rinten.Mounting.Directory.RemoveAssembly( outgoing.Assembly );
			}

			EditorTypeLibrary.RemoveAssembly( outgoing.Assembly );
			ManagedTools.AssembliesDirty = true;
		};

		using ( var _ = Rinten.Engine.Bootstrap.StartupTiming?.ScopeTimer( "Tools Init: Resource Processor" ) )
			GameResourceProcessor.Initialize();
		PackageManager.OnPackageInstalledToContext += OnPackageInstalled;

		//Add all game addons to be compiled in tools mode, making them accessible for the map editor, Asset Editor, etc.
		ManagedTools.AssembliesDirty = true;

		using ( var _ = Rinten.Engine.Bootstrap.StartupTiming?.ScopeTimer( "Tools Init: MCP" ) )
			Editor.Mcp.McpServer.Start();

		return Task.CompletedTask;
	}

	/// <summary>Load the startup project for the first time</summary>
	public async Task LoadProject()
	{
		await StartupLoadProject.Run();
	}

	public void BetweenFrames() => StartupLoadProject.BetweenFrames();

	//Called from the code generator, the job of this function is: 1. Make sure the
	//package is downloaded and installed 2. Return the relative path to the primary asset
	string ResolvePackageAsset( string packageName )
	{
		Package package = Package.FetchAsync( packageName, false ).Result;
		if ( package == null )
		{
			return null;
		}

		if ( StartupLoadProject.IsLoading )
		{
			StartupLoadProject.QueuePackageDownload( packageName );
		}
		else
		{
			MainThread.Queue( () => AssetSystem.InstallAsync( package ).Wait() );
		}

		return package?.PrimaryAsset;
	}


	private void OnPackageInstalled( PackageManager.ActivePackage package, string context )
	{
		log.Trace( $"OnPackageInstalled: {package.Package.FullIdent} {context}" );

		//only load if a tools context
		if ( context != "tools" && context != "hammer" && package.Package is not LocalPackage ) return;

		//The menu addon should never leak into other projects, its loading is handled in MenuDll.
		if ( package.Package is LocalPackage { Project.Config.Ident: "menu" } && Project.Current?.Config?.Ident != "menu" ) return;

		log.Trace( $" - Loading: {package.Package.FullIdent}" );

		//make sure it's loaded into our context
		AssemblyEnroller.LoadPackage( package.Package.FullIdent );
	}

	public void OnFunctionKey( ButtonCode key, KeyboardModifiers modifiers )
	{
		//While the game has the keyboard the window's keys go to it alone and the shortcuts never hear them (see CoreWindow) - and F5
		//is the way to stop the game, F3 the way out of fullscreen. With the editor holding the input it has heard the key already.
		if ( !GameMode.HasInput ) return;

		EditorShortcuts.FunctionKey( key, modifiers );
	}

	/// <summary>Everything the editor's windows want doing, done once - so that they stay answering while something slow blocks the thread they run on.</summary>
	public void Spin()
	{
		BindSystem?.Tick();

		if ( EditorBackend.Panels )
		{
			//Their tool framework had a pump of its own. Ours is the windows that are open: their events, their layout and their picture.
			Rinten.CoreUiWindow.FrameAll();

			MainThread.RunQueues();
			Logging.PushQueuedMessages();

			EngineLoop.RunAsyncTasks();
			return;
		}

		EngineLoop.RunAsyncTasks();
		//No-op: nothing else to do here.
	}

	public void Tick()
	{
		//close progress windows if compile has finished
		CompileStatus.CloseProgress();

		//A game that has stopped cannot be left holding the keyboard and the mouse,
		//whichever way it stopped - there would be no cursor left to press play with again.
		if ( !Game.IsPlaying && GameMode.HasInput )
		{
			GameMode.ClearPlayMode();
		}

		//Make sure we agree about Time.Now during editor events while in play mode
		if ( Game.IsPlaying && Game.ActiveScene is { } scene )
		{
			Time.Update( scene.TimeNow, scene.TimeDelta );
		}

		//Escape was pressed in game and wasn't swallowed so lets change focus from the game window
		//to the main editor window, which is going to free the mouse cursor from being captured
		if ( Game.IsPlaying && Input.EscapePressed )
		{
			EditorWindow.Focus();
			Input.EscapePressed = false;
		}
	}

	/// <summary>Registers exclusive Rinten.Tools <see cref="Rinten.IHandle"/> types</summary>
	public int RegisterHandle( IntPtr ptr, uint type ) => HandleTypes.RegisterHandle( ptr, type );

	public object InspectedObject
	{
		get => EditorUtility.InspectorObject;
		set => EditorUtility.InspectorObject = value;
	}

	/// <summary>Return true if the GameFrame is visible</summary>
	[Obsolete]
	public bool IsGameViewVisible => false;

	public async Task OnInitializeHost()
	{
		await CloudAsset.AddNewServerPackages();
	}

	void IToolsDll.RunFrame()
	{
		using ( Rinten.Diagnostics.PerformanceStats.Timings.Editor.Scope() )
		{
			if ( EditorBackend.Panels )
			{
				//Their tool framework called this back through interop once a frame. It is the editor's own frame - the item lists
				//rebuild in it, the tools run in it, the thumbnails come in on it - so with no framework to call it, it is called here.
				ManagedTools.RunFrame();
				return;
			}

		}
	}

	/// <summary>Return a thumbnail for this asset. This is used by the ThumbLoader, which allows people to load thumbs using thumb:resource/path.mdl etc.</summary>
	public async Task<Bitmap> GetThumbnail( string filename )
	{
		var asset = AssetSystem.FindByPath( filename );
		if ( asset is null ) return null;

		var thumb = await AssetThumbnail.GetAssetThumbAsync( asset );
		if ( thumb is null ) return null;

		//Sorry - we have no fast GetPixels
		var pixels = thumb.GetPng();
		return Bitmap.CreateFromBytes( pixels );
	}

	/// <summary>Public access to the editor system, from Game-side</summary>
	public EditorSystem ActiveEditor { get; } = new EditorSystemPublic();

}
