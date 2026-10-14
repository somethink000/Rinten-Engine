using Rinten.Engine;

namespace Rinten;

public static partial class Game
{
	/// <summary>Indicates whether the game is currently running and actively playing a scene.</summary>
	public static bool IsPlaying { get; internal set; } = true;

	/// <summary>Indicates whether the game is currently paused.</summary>
	public static bool IsPaused { get; set; }

	//Frames the game still runs while paused - a step. Counted down a frame at a time,
	//whatever scenes tick in it. An agent's play_step sets it; see Editor.Mcp.PlayTools.
	internal static int StepFrames { get; set; }

	//The loop frame the current step was granted in.
	private static long steppingFrame = -1;

	//Whether the paused game ticks this frame anyway, because a step was asked for. Once
	//per frame the step is spent; every scene ticking in that frame gets the same answer.
	internal static bool Stepping()
	{
		if ( steppingFrame == EngineLoop.LoopFrames ) return true;
		if ( StepFrames <= 0 ) return false;

		StepFrames--;
		steppingFrame = EngineLoop.LoopFrames;
		return true;
	}

	/// <summary>The current scene that is being played.</summary>
	public static Scene ActiveScene
	{
		get => GlobalContext.Current.ActiveScene;
		internal set => GlobalContext.Current.ActiveScene = value;
	}

	/// <summary>Change the active scene and optionally bring all connected clients to the new scene (broadcast the scene change.) If we're in a networking session, then only the host can change the scene.</summary>
	public static bool ChangeScene( SceneLoadOptions options )
	{
		if ( !Networking.IsHost )
			return false;

		//We don't want to send any networked messages to do with deletion or creation of GameObjects here. Because the
		//client will destroy their scene locally anyway. This saves us sending a message for potentially 100s of objects.
		using ( SceneNetworkSystem.SuppressSpawnMessages() )
		{
			using ( SceneNetworkSystem.SuppressDestroyMessages() )
			{
				if ( !ActiveScene.Load( options ) )
					return false;
			}
		}

		//Conna: We want to send a new snapshot to every client.
		SceneNetworkSystem.Instance?.LoadSceneBroadcast( options );
		return true;
	}

	internal static void Render()
	{
		//IToolsDll.OnRender handles the case where game is not playing (render from editor scene)
		if ( !IsPlaying )
			return;

		//Our renderer draws the game, from the same scene, on its own window - and in the editor, through the surface on the widget being played in.
		if ( Application.IsEditor || CoreGameRenderer.Render( ActiveScene ) )
			return;

		//Could be loading still
		if ( ActiveScene is null )
			return;

		if ( ActiveScene.IsLoading || LoadingScreen.IsVisible || Networking.IsConnecting )
		{
			//Make sure overlays are rendered even when we are loading
			if ( ActiveScene.Camera is not null )
			{
				ActiveScene.Camera.SceneCamera.EnableEngineOverlays = true;
				ActiveScene.Camera.AddToRenderList( default );
			}

			return;
		}

		ActiveScene.Camera?.SceneCamera.EnableEngineOverlays = true;
		SceneCamera.RecordingCamera = ActiveScene.Camera?.SceneCamera;

		ActiveScene.Render( default );
	}

	internal static void Shutdown()
	{
		IsClosing = true;
		IsPlaying = false;

		ActiveScene?.Destroy();
		ActiveScene = null;

		IsClosing = false;
	}
}
