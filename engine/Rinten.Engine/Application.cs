using Editor;
using Rinten.Engine;
using Rinten.Engine.Settings;
using Rinten.Utility;
using Rinten.VR;
using System.Globalization;

namespace Rinten;

public static class Application
{
	//Prevent double initialization
	internal static bool IsInitialized { get; private set; }

	/// <summary>True if we're running the engine as part of a unit test</summary>
	public static bool IsUnitTest { get; internal set; }

	/// <summary>True if running without a graphics window, such as in a terminal.</summary>
	public static bool IsHeadless { get; private set; }

	/// <summary>True if running in a terminal like console, instead of a game window or editor.</summary>
	public static bool IsConsoleApp => IsHeadless;

	/// <summary>True if this is a dedicated server</summary>
	public static bool IsDedicatedServer { get; private set; }

	//True if running a benchmark

	/// <summary>True if running with the tools or editor attached</summary>
	public static bool IsEditor { get; private set; }

	//True if running with -joinlocal. This is an instance that launches and joins an in process editor session.
	internal static bool IsJoinLocal { get; private set; }

	//A local instance Id assigned when the instance is spawned with -joinlocal. We can use this to give each local instance its own player id.
	internal static int LocalInstanceId { get; private set; }

	/// <summary>The engine's version string</summary>
	public static string Version { get; internal set; }

	//True if this is a published build rather than a development one
	internal static bool IsRetail { get; private set; }

	/// <summary>The date of this version, as a UTC datetime.</summary>
	public static DateTime VersionDate { get; internal set; }

	//Number of exceptions we've had. Resets on game exit.
	internal static int ExceptionCount { get; set; }

	/// <summary>True if the game is running in standalone mode</summary>
	public static bool IsStandalone { get; internal set; }

	/// <summary>A desktop program rather than a game: it sleeps while nobody uses it, and its builds leave out what only games need</summary>
	public static bool IsApp { get; internal set; }

	//Set by the browser player before anything else touches the engine.
	internal static bool IsWeb { get; set; }

	/// <summary>Whether a game may show what it shows its developer - its frame rate, the log, whatever else it keeps for them. Always in the editor; in an exported build only where it was exported for that - see ExportConfig.ExportDebug - so the same game ships to its players without any of it.</summary>
	public static bool IsDebugBuild => IsEditor || DebugExport;

	//The build's manifest said it was exported for debugging.
	internal static bool DebugExport { get; set; }

	internal static bool OnWeb => IsWeb || OperatingSystem.IsBrowser();

	/// <summary>The language code for the current language</summary>
	[ConVar( "language", ConVarFlags.Saved | ConVarFlags.Protected, Name = "Language" )]
	public static string LanguageCode { get; internal set; } = "en";

	//True if the game is running in VR mode
	public static bool IsVR => VRSystem.IsActive; //Possibly this should be set once at startup and never change

	internal static void Initialize( bool dedicated, bool headless, bool toolsMode, bool testMode, bool isRetail )
	{
		if ( IsInitialized )
			throw new InvalidOperationException( "Already Initialized" );

		IsInitialized = true;

		IsDedicatedServer = dedicated;
		IsRetail = isRetail;
		IsUnitTest = testMode;
		IsHeadless = headless;
		IsEditor = toolsMode;
		IsJoinLocal = CommandLine.HasSwitch( "-joinlocal" );
		LocalInstanceId = CommandLine.GetSwitchInt( "+instanceid", 1 );
	}

	internal static void Shutdown()
	{
		Network.NetworkProviders.Shutdown();
		IsInitialized = false;
	}

	internal static void TryLoadVersionInfo( string gameFolder )
	{
		Version = "0000000";
		VersionDate = DateTime.Now;

		var versionPath = System.IO.Path.Combine( gameFolder, ".version" );

		if ( System.IO.File.Exists( versionPath ) )
		{
			var text = System.IO.File.ReadAllText( versionPath );
			var split = text.Split( "\n" );

			Version = split[0].Trim();
			VersionDate = DateTime.ParseExact( split[4], "dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal );
		}
	}

	//The currently loaded game package. May be null if no game loaded. Controlled by GameMenuDll.
	internal static Package GamePackage { get; set; }

	//The currently loaded map package
	internal static Package MapPackage { get; set; }

	//The currently loaded game package's ident - if applicable.
	internal static string GameIdent { get; set; }

	//The currently loaded map, can be an ident or a path.
	internal static string Map { get; set; }

#if DEBUG
	public static bool IsDebug => true;
#endif

#if !DEBUG
	public static bool IsDebug => false;
#endif

	/// <summary>Returns true if the microphone is currently listening</summary>
	public static bool IsMicrophoneListening => VoiceManager.IsListening;

	/// <summary>Returns true if the microphone is currently listening and actually hearing/capturing sounds</summary>
	public static bool IsMicrophoneRecording => VoiceManager.IsRecording;

	/// <summary>Is the game window in focus?</summary>
	public static bool IsFocused  => false;

	internal static bool WantsExit { get; set; }

	//Exits the application if we're running in standalone mode or we are a Dedicated Server.
	internal static void Exit()
	{
		WantsExit = true;
	}

	internal static void ClearGame()
	{
		GameIdent = default;
		GamePackage = default;
		ExceptionCount = default;
		MapPackage = default;

		//Cheats, and anything they allowed, shouldn't carry over into the next game
		ConVarSystem.ResetCheats();
	}

	public static bool CheatsEnabled
	{
		get => ConVarSystem.GetValue( ConVarSystem.CheatsVariableName, "false", true ).ToBool();
	}

	/// <summary>Allows access to the RenderSettings singleton, which contains settings related to rendering in the game. You're only able to access this when in standalone mode. When accessing in the editor it will return null.</summary>
	public static RenderSettings RenderSettings
	{
		get
		{
			if ( !IsStandalone ) return null;
			return RenderSettings.Instance;
		}
	}

	//Gets the active scene. This could be in the menu system, or in the game. This is
	//provided for internal engine, and should never be accessible to the user code.
	internal static Scene GetActiveScene()
	{
		if ( IGameInstance.Current?.Scene is Scene gameScene && gameScene.IsValid() )
		{
			return gameScene;
		}

		if ( IMenuDll.Current?.Scene is Scene menuScene && menuScene.IsValid() )
		{
			return menuScene;
		}

		//And what the editor has open, which is the only scene there is when nothing is running. Left out, this answered null in the editor - and
		//the console commands built on it registered twice, because the editor had to declare its own copy of each to reach the scene it was editing.
		if ( Editor?.Scene is Scene editorScene && editorScene.IsValid() )
		{
			return editorScene;
		}

		return null;
	}

	/// <summary>Get the current editor if any. Will return null if we're not in the editor, or there is no active editor session.</summary>
	public static EditorSystem Editor => IToolsDll.Current?.ActiveEditor;


	[ConCmd( "version", Help = "Print info about the game version" )]
	internal static void PrintVersion()
	{
		Log.Info( $"Rinten version {Version} ({VersionDate})" );

		if ( GamePackage is { } game )
		{
			Log.Info( $"Game: {game.GetIdent( false, true )} ({game.Revision?.Created})" );
		}

		if ( MapPackage is { } map )
		{
			Log.Info( $"Map: {map.GetIdent( false, true )} ({map.Revision?.Created})" );
		}
	}
}
