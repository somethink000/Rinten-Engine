using Rinten.Internal;
using System;

namespace Editor;


internal static class ManagedTools
{
	/// <summary>This gets set when the assemblies are changed, which means we may have new tools and menu options. This ultimately triggers a tools.refresh on the next frame.</summary>
	public static bool AssembliesDirty { get; internal set; }

	//On startup we start logging in. We can continue to log in during startup, in the background.
	//But we really want to have been logged in by the time we start accessing the API properly.
	internal static Task AccountLoginTask;

	/// <summary>Editor startup that does not need a window: filesystem, logging, binds, fonts.</summary>
	public static void InitStartManaged()
	{
		InitFilesystem();

		Logging.SetRule( "*", EditorCookie.Get<LogLevel>( "DefaultLoggingLevel", LogLevel.Info ) );

		ConVarSystem.AddAssembly( Global.Assembly, "editor" );

		BindSystem = new Rinten.Bind.BindSystem( "Tools" );
		BindSystem.ThrottleUpdates = true;
		BindSystem.CatchExceptions = true;

		FileSystem.RebuildContentPath();

		FontManager.Instance.LoadAll( FileSystem.Content );
	}

	public static void InitStart()
	{
		Log.Info( $"Editor Startup version {Rinten.Application.Version}" );
		//Log into the api, if one is configured
		AccountLoginTask = AccountInformation.Update();

		InitFilesystem();

		Logging.SetRule( "*", EditorCookie.Get<LogLevel>( "DefaultLoggingLevel", LogLevel.Info ) );

		ConVarSystem.AddAssembly( Global.Assembly, "editor" );

		BindSystem = new Rinten.Bind.BindSystem( "Tools" );
		BindSystem.ThrottleUpdates = true;
		BindSystem.CatchExceptions = true;

		//Makes sure we have a Filesystem.Content early, even if projects aren't added yet
		FileSystem.RebuildContentPath();
	}

	public static void InitFilesystem()
	{
		FileSystem.Root.CreateDirectory( "/.rinten/http" );
		FileSystem.WebCache = new LocalFileSystem( FileSystem.Root.GetFullPath( "/.rinten/http" ) );

		FileSystem.Temporary = new LocalFileSystem( FileSystem.Root.GetFullPath( "/.rinten" ) );
	}

	private static FileWatch stylesWatcher, kitWatcher;

	//Watches the UI kit's theme and design, and the editor's theme.json, for live reload.
	internal static void WatchEditorStyles()
	{
		if ( stylesWatcher is not null )
			return;

		if ( FileSystem.Root is null )
			return;

		stylesWatcher = FileSystem.Root.Watch( "/addons/tools/assets/styles/*" );
		stylesWatcher.OnChangedFile += _ => Application.ReloadStyles();

		kitWatcher = FileSystem.Root.Watch( "/addons/base/code/Styles/kit/*" );
		kitWatcher.OnChangedFile += _ => Application.ReloadStyles();
	}

	/// <summary>The last of the editor's start: the styles are watched so a change to them shows up without a restart, and the tools' own addons are given time to finish compiling before anything asks them for a window.</summary>
	public static void InitFinish()
	{
		WatchEditorStyles();

		//Wait for tools addons to finish compiling
		Log.Info( "Compiling Tools" );
		RunFrame();

		while ( !AccountLoginTask.IsCompleted )
		{
			Rinten.Engine.IToolsDll.Current?.Spin();
			System.Threading.Thread.Sleep( 16 );
		}

		//If the avatar was found on the backend, replace the cookie one
		if ( !string.IsNullOrWhiteSpace( AccountInformation.AvatarJson ) )
		{
			Avatar.AvatarJson = AccountInformation.AvatarJson;
		}
	}

	static bool _wasActiveWindow;

	public static void RunFrame()
	{
		Application.StartFrame();

		//Release all shortcut key states when the editor loses focus (alt-tab)
		var isActive = EditorMainWindow.Current?.IsActiveWindow ?? false;
		if ( _wasActiveWindow && !isActive )
		{
			EditorShortcuts.ReleaseAll();
		}
		_wasActiveWindow = isActive;

		if ( AssembliesDirty )
		{
			AssembliesDirty = false;
			EditorEvent.Run( "refresh" );
		}

		EngineLoop.RunAsyncTasks();

		using ( Performance.Scope( "Tool Binds" ) )
		{
			BindSystem.Tick();
		}

		//Every [EditorEvent.Frame] in the editor and its addons runs in here - scene ticks, toolbars,
		//overlays. When the editor is slow, this is where it usually is, and it was unlabelled.
		FrameEventTrace.Arm();

		using ( Performance.Scope( "Tool Frame Event" ) )
		{
			EditorEvent.Run( "tool.frame" );
		}

		FrameEventTrace.Frame();

		using ( Performance.Scope( "Thumbnails" ) )
		{
			AssetThumbnail.Frame();
		}
	}

	public static void Shutdown()
	{
		stylesWatcher?.Dispose();

		AssetSystem.Shutdown();
	}

	internal static void NativeHook( string name, IntPtr arg0, IntPtr arg1, IntPtr arg2 )
	{
		var method = typeof( ManagedTools ).GetMethod( name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic );
		if ( method == null )
		{
			Log.Warning( $"Unknown tool hook: {name}" );
			return;
		}

		var parameters = method.GetParameters();

		if ( parameters.Length == 0 )
		{
			method?.Invoke( null, null );
			return;
		}

		if ( parameters.Length == 1 )
		{
			return;
		}
	}

	internal static void GlobalMousePressed( int button )
	{
		EditorEvent.Run( "editor.mousepressed" );
	}

	internal static void GlobalMouseReleased( int button )
	{
		if ( button == 1 )
		{
			Widget.CurrentlyPressedWidget = null;
		}
	}

	internal static void GlobalMouseWheel( int x, int y, int modifiers )
	{
		//Wheel turn from whichever axis carries it (shift-wheel is horizontal); kept fractional for trackpads
		var turn = y != 0 ? y : x;

		if ( EditorShortcuts.InvokeWheel( turn, KeyModifierMap.Translate( (NativeKeyModifiers)modifiers ) ) )
			return;

		Application.accumulatedWheelDelta += new Vector2( x / 120.0f, turn / 120.0f );
	}

	internal static bool GlobalShortcutPressed()
	{
		return !EditorShortcuts.AllowShortcuts;
	}

	internal static void OnToolCommand( string v )
	{
		var parts = v.SplitQuotesStrings();
		if ( parts.Length == 0 )
			return;

		EditorEvent.Run( $"command {parts[0]}" );
	}

	/// <summary>Pumps the main loop while something else is blocking.</summary>
	public static void OnUiHeartbeat()
	{
		BlockingLoopPumper.Pump();
	}
}
