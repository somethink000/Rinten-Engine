using Rinten.Engine;
using Rinten.Internal;
using Rinten.Modals;
using Rinten.UI;

namespace Rinten;

//The engine's side of the interface's input: who gets the keyboard and mouse, the game or the panels, and the panels in the world
internal static class UiGameInput
{
	internal static void Route( UISystem ui )
	{
		//Set mouse delta to 0 so it doesn't repeat the last frame's delta on the next frame
		Mouse.Frame();

		bool inGame = InputRouter.GameRunning;

		var mouseState = Rinten.Engine.InputContext.InputState.Ignore;
		var buttonState = Rinten.Engine.InputContext.InputState.Ignore;

		if ( Game.IsMenu )
		{
			if ( !inGame )
			{
				mouseState = InputContext.InputState.UI;
				buttonState = Rinten.Engine.InputContext.InputState.Game;

				if ( ui.CurrentFocus is not null )
					buttonState = ui.CurrentFocus.ButtonInput == PanelInputType.Game ? InputContext.InputState.Game : InputContext.InputState.UI;
			}

			//A modal is open
			if ( (IModalSystem.Current?.HasModalsOpen() ?? false) )
			{
				mouseState = Rinten.Engine.InputContext.InputState.UI;
				buttonState = Rinten.Engine.InputContext.InputState.Game;

				if ( ui.CurrentFocus is not null )
					buttonState = ui.CurrentFocus.ButtonInput == PanelInputType.Game ? InputContext.InputState.Game : InputContext.InputState.UI;
			}

			//The loading screen is visible
			if ( Rinten.LoadingScreen.IsVisible )
			{
				mouseState = Rinten.Engine.InputContext.InputState.UI;
				buttonState = Rinten.Engine.InputContext.InputState.UI;
			}

			//The developer console / chat is open
			if ( IMenuSystem.Current?.ForceCursorVisible ?? false )
			{
				mouseState = Rinten.Engine.InputContext.InputState.UI;
				buttonState = Rinten.Engine.InputContext.InputState.UI;
			}
		}

		//If we're a game menu, and there is no client - then treat all input as game input by default.
		if ( !Game.IsMenu )
		{
			mouseState = Rinten.Engine.InputContext.InputState.Game;
			buttonState = Rinten.Engine.InputContext.InputState.Game;

			if ( ui.DoAnyPanelsWantMouseVisible() )
				mouseState = InputContext.InputState.UI;

			if ( ui.CurrentFocus is not null )
				buttonState = ui.CurrentFocus.ButtonInput == PanelInputType.Game ? InputContext.InputState.Game : InputContext.InputState.UI;

			//No input if we're not playing
			if ( !inGame )
			{
				mouseState = InputContext.InputState.UI;
				buttonState = InputContext.InputState.UI;
			}

			if ( Application.IsEditor && !Game.IsPlaying )
			{
				mouseState = InputContext.InputState.Ignore;
				buttonState = InputContext.InputState.Ignore;
			}

			//The player asked an editor for the game - clicked into the view it is being played in. Then it is the game's keyboard and mouse
			//whatever else is loaded: a scene played on its own has no game instance, and would otherwise be a scene played with no input.
			if ( Application.IsEditor && Game.IsPlaying && InputRouter.GameHasInput )
			{
				mouseState = InputContext.InputState.Game;
				buttonState = InputContext.InputState.Game;

				if ( ui.DoAnyPanelsWantMouseVisible() )
					mouseState = InputContext.InputState.UI;

				//Only text-input panels take the keyboard; otherwise keys still go to the game
				if ( ui.CurrentFocus is { AcceptsImeInput: true } )
					buttonState = InputContext.InputState.UI;
				else if ( ui.CurrentFocus is not null && ui.CurrentFocus.ButtonInput != PanelInputType.Game )
					buttonState = InputContext.InputState.Game;
			}
		}

		Game.InputContext.UpdateInputFromUI( mouseState, ui.Input.Hovered, Panel.MouseCapture is not null, buttonState, ui.CurrentFocus );
	}

	//What the pointers in the world are tested against this frame, kept between frames
	private static readonly List<RootPanel> pointedAt = new();

	internal static void TickWorld( UISystem ui )
	{
		var scene = Game.ActiveScene;
		if ( !scene.IsValid() ) return;

		//The world's panels, and in a headset the screen's too: there they are sheets in front of the head, pointed at the same way - see ScreenPanel.
		pointedAt.Clear();

		foreach ( var worldPanel in scene.GetAllComponents<WorldPanel>() )
		{
			if ( worldPanel.GetPanel() is RootPanel root ) pointedAt.Add( root );
		}

		foreach ( var screen in scene.GetAllComponents<ScreenPanel>() )
		{
			if ( screen.GetPanel() is GameRootPanel { InHeadset: true } root && root.IsVisible ) pointedAt.Add( root );
		}

		var gameHasPointers = false;

		foreach ( var worldInput in scene.GetAllComponents<WorldInput>() )
		{
			gameHasPointers = true;
			worldInput.WorldPanelInput.Tick( pointedAt, true );
		}

		//A headset with nothing of the game's pointing: the controllers do it.
		HeadsetPointers.Tick( scene, pointedAt, gameHasPointers );
	}
}
