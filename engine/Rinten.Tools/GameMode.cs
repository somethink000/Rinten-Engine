using Rinten;
using Rinten.Engine;

namespace Editor;

/// <summary>Who owns keyboard and mouse during in-editor play: the game view or the editor (Escape returns it)</summary>
public static class GameMode
{
	static IPlaySurface _inPlay;

	//Whether the way back out has been said this run.
	static bool saidHow;

	//Is this surface the one the game is being played in
	internal static bool IsPlaySurface( IPlaySurface surface ) => surface == _inPlay;

	/// <summary>Whether the game has the keyboard and the mouse rather than the editor.</summary>
	public static bool HasInput => InputRouter.GameHasInput;

	/// <summary>Whether the game holds the mouse itself, rather than sharing it with the editor around it.</summary>
	public static bool HasMouse => InputRouter.GameHasMouse;

	/// <summary>Given a surface, register it as the one the game is played in, and give the game the input to play it with.</summary>
	public static void SetPlaySurface( IPlaySurface surface )
	{
		if ( _inPlay == surface ) return;

		ClearPlayMode();

		if ( surface is null ) return;

		surface.FocusChanged += OnFocusChanged;

		_inPlay = surface;

		surface.TakeFocus();

		//Screen otherwise refreshes only in SimulateUI, after the first OnStart and OnUpdate have run
		Screen.UpdateFromEngine();

		//Straight into the game, because that is what pressing play is for. Escape is the way back out, and clicking the view the way back in.
		TakeInput();
	}

	public static void ClearPlayMode()
	{
		ReleaseInput();

		if ( _inPlay is null )
			return;

		_inPlay.FocusChanged -= OnFocusChanged;
		_inPlay.ReleaseFocus();

		_inPlay = null;
	}

	/// <summary>Hands the keyboard and the mouse to the game. Called when play starts and whenever the player clicks into the game view afterwards.</summary>
	public static void TakeInput()
	{
		if ( _inPlay is null || InputRouter.GameHasInput ) return;

		//Only while the game is the thing on screen. It has its own tab now, so it can be playing away behind a scene somebody is editing -
		//and a click meant for that scene must not hand the mouse to a game that is not even visible, nor drag its tab to the front to do it.
		if ( !_inPlay.IsShowing ) return;

		//The window the view is in, rather than whichever one has the keyboard: an editor has several open -
		//a console, a tool torn off into its own - and the mouse has to come off the one the game is drawn in.
		var window = CoreUiWindow.HandleOf( _inPlay.InputSurface );

		if ( window == System.IntPtr.Zero ) window = CoreInput.KeyboardWindow();
		if ( window == System.IntPtr.Zero ) return;

		InputRouter.GameInputReleased = OnInputReleased;
		InputRouter.TakeGameInput( window );

		_inPlay.TakeFocus();

		//Once a session: with the cursor gone and the editor not answering, the way back has to be written down somewhere.
		if ( !saidHow )
		{
			saidHow = true;
			Log.Info( "playing - escape gives the mouse back to the editor" );
		}
	}

	/// <summary>Gives the keyboard and the mouse back to the editor.</summary>
	public static void ReleaseInput() => InputRouter.ReleaseGameInput();

	//The input went back to the editor on its own - escape, or the window losing focus. Nothing
	//to undo here; the surface keeps the focus so clicking the view is enough to go back in.
	private static void OnInputReleased()
	{
		_inPlay?.Redraw();
	}

	//The play surface losing the keyboard is a way of asking for the mouse back, the same as escape - a dock, a menu, the hierarchy.
	private static void OnFocusChanged( bool focused )
	{
		if ( !focused ) ReleaseInput();
	}
}
