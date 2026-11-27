using NativeEngine;
using Rinten.Engine;
using Rinten.Network;
using Rinten.VR;

namespace Rinten;

/// <summary>Provides global access to core game state, utilities, and operations. The <see cref="Game"/> class exposes static properties and methods to query and control the running game, such as checking if the game is running, getting your player id, taking screenshots, and managing game sessions.</summary>
public static partial class Game
{
	//The input context for this context (menu, gamemenu, client)
	internal static InputContext InputContext
	{
		get => GlobalContext.Current.InputContext;
		set => GlobalContext.Current.InputContext = value;
	}

	/// <summary>Return true if we're in a game (ie, not in the main menu)</summary>
	public static bool InGame => IGameInstance.Current is not null;

	/// <summary>Returns true if the game is running with the editor enabled</summary>
	public static bool IsEditor => Application.IsEditor;

	/// <summary>Returns the current game's ident - ie org.sandbox</summary>
	public static string Ident => Application.GameIdent;

	/// <summary>Returns true if the main menu is visible. Note that this will work serverside too but will only return the state of the host.</summary>
	public static bool IsMainMenuVisible => !InGame;

	/// <summary>True if we're currently recording a video (using the video command, or F6)</summary>
	public static bool IsRecordingVideo => ScreenRecorder.IsRecording();

	/// <summary>Set to true when the game is closing</summary>
	public static bool IsClosing { get; internal set; } = false;

	/// <summary>Return true if we're running in VR</summary>
	public static bool IsRunningInVR => VRSystem.IsActive;

	/// <summary>A shared random that is automatically seeded on tick</summary>
	public static System.Random Random => RintenSystem.Random;

	/// <summary>Set the seed for Game.Random</summary>
	public static void SetRandomSeed( int seed )
	{
		RintenSystem.SetRandomSeed( seed );
	}

	/// <summary>Your player id</summary>
	public static SteamId SteamId => Utility.LocalUser.SteamId;

	/// <summary>Disconnect from the current game session</summary>
	public static void Disconnect()
	{
		ConVarSystem.Run( "disconnect end game" );
	}

	/// <summary>Trace against the physics in the current scene</summary>
	public static PhysicsTraceBuilder PhysicsTrace => Game.ActiveScene.PhysicsWorld.Trace;

	/// <summary>Trace against the physics and hitboxes in the current scene</summary>
	public static SceneTrace SceneTrace => Game.ActiveScene.Trace;

	/// <summary>Close the current game.</summary>
	public static void Close()
	{
		//An editor's -joinlocal instance exists only to join it: it goes with the session
		if ( Application.IsJoinLocal )
		{
			Networking.Disconnect();
			Application.Exit();
			return;
		}

		//Editor only: just exit playmode
		if ( IToolsDll.Current is not null )
		{
			IToolsDll.Current.ExitPlaymode();
			return;
		}

		//Might want to queue this up. do it the next frame? Be aware that this could be called
		//from the GameDll or the MenuDll So anything here needs to be safe to call from either

		if ( IGameInstance.Current is not null )
		{
			IGameInstance.Current.Close();
		}
		else
		{
			//Conna: game instance will call disconnect. If we don't have a game instance then we need to call it ourselves.
			Networking.Disconnect();

			Application.ClearGame();
		}

		LaunchArguments.Reset();

		if ( Application.IsStandalone || Application.IsDedicatedServer )
		{
			//exit whole app
			Application.Exit();
		}
		else
		{
			//return to menu
			IMenuDll.Current?.OnGameExited();
		}
	}

	/// <summary>Load a game. You can configure the new game with LaunchArguments before calling this.</summary>
	public static void Load( string gameIdent, bool keepClients )
	{
		Log.Info( $"Game Load: {gameIdent}" );

		_ = LoadAsync( gameIdent, keepClients );
	}

	static async Task LoadAsync( string gameIdent, bool keepClients )
	{
		if ( IToolsDll.Current is not null )
		{
			IToolsDll.Current.ExitPlaymode();
			Log.Warning( $"Called Game.Load( {gameIdent} ) but we're in the editor, so just stopping." );
			return;
		}

		if ( Networking.IsActive && Networking.IsHost )
		{
			if ( keepClients )
			{
				//Send a reconnect message but with no delay - this must be sent right now!
				var msg = new ReconnectMsg { Game = gameIdent, Map = LaunchArguments.Map };
				SceneNetworkSystem.Instance.Broadcast( msg, default, NetFlags.Reliable | NetFlags.SendImmediate );
			}

			//Allow for a 1 second grace period for clients to receive the message
			await Task.Delay( 1000 );

			Networking.Disconnect();
		}

		//close old game
		if ( IGameInstance.Current is not null )
		{
			Log.Info( $"Closing {IGameInstance.Current.Package?.Title}" );
			IGameInstanceDll.Current.CloseGame();
		}

		Application.ClearGame();

		LoadingScreen.IsVisible = true;
		LoadingScreen.Media = null;
		LoadingScreen.Title = null;

		await IGameInstanceDll.Current.LoadGamePackageAsync( gameIdent, GameLoadingFlags.Host, default );
	}

	/// <summary>Capture a screenshot and save it to the screenshots folder.</summary>
	[ConCmd( "screenshot", Help = "Take a screenshot and save it to the screenshots folder" )]
	public static void TakeScreenshot()
	{
		ScreenshotService.RequestCapture();
	}

	/// <summary>Capture a high resolution screenshot using the active scene camera.</summary>
	[ConCmd( "screenshot_highres", Help = "Take a high resolution screenshot you specify the width and height" )]
	public static void TakeHighResScreenshot( int width, int height )
	{
		ScreenshotService.TakeHighResScreenshot( Application.GetActiveScene(), width, height );
	}

	/// <summary>This has to be in Game.dll so the codegen will get generated for it</summary>
	[ConVar( "sv_cheats", ConVarFlags.Replicated | ConVarFlags.Protected, Help = "Enable cheats on the server" )]
	public static bool CheatsEnabled { get; set; }
}
