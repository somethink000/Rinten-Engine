using NativeEngine;
using Rinten.Internal;
using Rinten.UI;

namespace Rinten.Engine;

//This is where input is sent to from the engine. This is the first place input is routed to. From here it tries to route it to the
//menu, game menu and client - in that order. That should really be abstracted out though, so we can use this properly in Standalone.
internal static partial class InputRouter
{
	/// <summary>True if the cursor is visible</summary>
	public static bool MouseCursorVisible { get; private set; }

	/// <summary>The mouse cursor position. Or the last position if it's now invisible.</summary>
	public static Vector2 MouseCursorPosition { get; private set; }

	/// <summary>The mouse cursor delta</summary>
	public static Vector2 MouseCursorDelta { get; private set; }

	/// <summary>The panel we're keyboard focusing on</summary>
	public static IPanel KeyboardFocusPanel { get; set; }

	//The position in which we entered capture/relative mode
	static Vector2? mouseCapturePosition;

	//Whether the mouse is captured - hidden, and heard as movement rather than as a place. For input with no relative mode of its own to ask, a finger.
	internal static bool MouseCaptured => mouseCapturePosition is not null;

	//The window a game played inside an editor has been given the keyboard and the mouse in, or zero while the editor has them.
	//The keyboard is the game's until escape (see CoreInput.Key); the mouse only while it hides the cursor (see GameHasMouse).
	internal static IntPtr GameWindow { get; private set; }

	//Whether a game inside an editor has been given the input.
	internal static bool GameHasInput => GameWindow != IntPtr.Zero;

	//Whether the game holds the mouse itself - hiding the cursor to fly something - rather than sharing it with the editor.
	internal static bool GameHasMouse { get; private set; }

	//Whether the pointer is over the view a game is played in, as the view itself reports it.
	internal static bool PointerInGameView { get; set; }

	//Game screen origin within the editor window when docked, else null; offsets pointer position
	internal static Vector2? ScreenOrigin { get; set; }

	//Told when the input goes back to the editor without it asking - escape, or the window it was taken in losing focus.
	internal static Action GameInputReleased { get; set; }

	//The window input was being read for before the game took it.
	static IntPtr windowBeforeGame;

	//Hands the keyboard and the mouse to a game being played inside an editor, in the window it is being played in.
	internal static void TakeGameInput( IntPtr window )
	{
		if ( window == IntPtr.Zero || GameWindow == window ) return;

		GameWindow = window;

		//Relative mouse mode, the cursor and composed text are all asked of a window in SDL3, and an editor has never named
		//one - it has no window of the game's own to name. Without this the mouse is never taken and the look never moves.
		windowBeforeGame = CoreInput.Window;
		CoreInput.Window = window;

		//The press that asked for the game is released into the game; the window would hold it down for the whole
		//run. Where the pointer is stays: a game with a cursor on screen is still pointed at through this window.
		CoreWindow.ForgetButtons( window );
	}

	//Gives the keyboard and the mouse back to the editor.
	internal static void ReleaseGameInput()
	{
		if ( !GameHasInput ) return;

		SetGameHasMouse( false );
		PointerInGameView = false;

		GameWindow = IntPtr.Zero;

		//While the window is still the game's, because that is the window both of these are asked of.
		EngineInput.SetRelativeMouseMode( false );
		EngineInput.SetIMEAllowed( false );

		CoreInput.Window = windowBeforeGame;
		windowBeforeGame = IntPtr.Zero;

		//The cursor by hand: with the input given back, Frame leaves it to the editor and would never put back the one it hid.
		mouseCapturePosition = null;
		CursorName = null;
		EngineInput.SetCursorStandard( InputStandardCursor_t.Arrow );

		//Nothing held survives the handover - a key that went down in the game and comes up in the editor is a key the game runs on forever.
		OnWindowActive( false );

		GameInputReleased?.Invoke();
	}

	//The game taking the mouse off the window it is played in, or giving it back.
	static void SetGameHasMouse( bool taken )
	{
		if ( GameHasMouse == taken ) return;

		GameHasMouse = taken;

		//No event moves the window's pointer while the game has the mouse, so a widget under it would stay hovered and pressed for the whole look.
		if ( taken ) CoreWindow.ForgetPointer( GameWindow );
	}

	/// <summary>True if an "exit game" button is pressed, escape on keyboard</summary>
	public static bool EscapeIsDown { get; private set; }

	/// <summary>The escape button was pressed this frame. The game is allowed to consume this. Then it will go to the menu. This is distinct from EscapeIsDown, because that is used to close the game when held down.</summary>
	public static bool EscapeWasPressed { get; set; }

	//Time since escape was pressed
	static RealTimeSince TimeSinceEscapePressed { get; set; }

	//Buttons that are currently pressed
	static HashSet<ButtonCode> PressedButtons = new HashSet<ButtonCode>();

	//Controller buttons that are currently pressed
	static HashSet<GamepadCode> PressedControllerButtons = new HashSet<GamepadCode>();

	/// <summary>Returns the number of seconds escape has been held down</summary>
	public static float EscapeTime => EscapeIsDown ? TimeSinceEscapePressed.Relative : 0;

	//Whether a game is running to take input: one loaded as a package, or a scene being played with none - in an editor, and in a page or on a phone,
	//which start their scene directly and have no game instance at all. Asked only of the first, a shipped web game drew and never heard a key.
	internal static bool GameRunning => IGameInstance.Current is not null || Game.IsPlaying;

	//Return the input contexts of each context, in order of priority
	static IEnumerable<InputContext> Contexts
	{
		get
		{
			if ( IMenuDll.Current is not null )
			{
				var menu = IMenuDll.Current.InputContext;
				if ( menu is not null ) yield return menu;
			}

			//if we even have a game menu! Or a scene being played with no package loaded - see GameRunning:
			//every key and every mouse move went nowhere at all, there was no context here to send them to.
			if ( GameRunning )
			{
				var gamemenu = IGameInstanceDll.Current.InputContext;
				if ( gamemenu is not null ) yield return gamemenu;
			}
		}
	}

	//The first of Contexts that passes the test, or null. The same two contexts in the same order, asked directly: Contexts is an iterator,
	//and with a Where and a FirstOrDefault on it the router's frame made four objects twice over to look at one field of two contexts.
	static InputContext FirstContext( Func<InputContext, bool> test )
	{
		if ( IMenuDll.Current?.InputContext is { } menu && test( menu ) ) return menu;

		if ( GameRunning )
		{
			if ( IGameInstanceDll.Current.InputContext is { } game && test( game ) ) return game;
		}

		return null;
	}

	public static void Frame()
	{
		var activeMouse = FirstContext( static x => x.MouseState != InputContext.InputState.Ignore );
		var activeKeyboard = FirstContext( static x => x.KeyboardState != InputContext.InputState.Ignore );

		//A game played inside an editor shares its window with the interface, and the pointer is the editor's until the player asks for it. Nothing
		//below may hide the cursor or take the mouse off the window system until they do, or pressing play leaves an editor nobody can click.
		var editorHasInput = Application.IsEditor && !GameHasInput;

		//Capture mode could either come from being in game (in which case input is sent to
		//the game) or from a Panel.CaptureMode - in which case input is sent to the panel/ui
		bool mouseCaptureMode = activeMouse is not null && activeMouse.MouseState == InputContext.InputState.Game;
		mouseCaptureMode = mouseCaptureMode || (activeMouse?.MouseCapture ?? false);
		mouseCaptureMode = mouseCaptureMode && !editorHasInput;

		//Said here rather than worked out again in the window loop: this is where capture is decided.
		SetGameHasMouse( GameHasInput && mouseCaptureMode );

		MouseCursorVisible = !mouseCaptureMode && (activeMouse is not null && activeMouse.MouseState == InputContext.InputState.UI);
		if ( editorHasInput ) MouseCursorVisible = true;
		if ( !EngineInput.HasMouseFocus() ) MouseCursorVisible = true;

		if ( editorHasInput )
		{
			//The editor's own windows set their cursor and ask for their own text input; both of those are below, and both would fight them.
			if ( activeKeyboard is not null )
			{
				KeyboardFocusPanel = activeKeyboard.KeyboardFocusPanel;
			}

			MouseCursorDelta = 0;
			EscapeWasPressed = false;

			TooltipSystem.SetHovered( activeMouse?.MouseFocusPanel ?? null );
			return;
		}

		if ( mouseCaptureMode )
		{
			//save the cursor position
			if ( mouseCapturePosition is null )
			{
				mouseCapturePosition = MouseCursorPosition;
			}

			EngineInput.SetRelativeMouseMode( true );
		}
		else
		{
			EngineInput.SetRelativeMouseMode( false );

			//restore cursor position
			if ( mouseCapturePosition is not null )
			{
				SetCursorPosition( mouseCapturePosition.Value );
				mouseCapturePosition = null;
			}
		}

		if ( activeMouse is not null )
		{
			SetCursorType( activeMouse.MouseCursor );
		}

		if ( activeKeyboard is not null )
		{
			KeyboardFocusPanel = activeKeyboard.KeyboardFocusPanel;
		}

		if ( KeyboardFocusPanel is null )
		{
			EngineInput.SetIMEAllowed( false );
		}
		else
		{
			EngineInput.SetIMEAllowed( true );
			//The window system is told in device pixels; an editor panel is laid out in units
			var rect = KeyboardFocusPanel.Rect;
			var device = KeyboardFocusPanel is Rinten.UI.Panel { DeviceScale: var scale } ? scale : 1.0f;
			EngineInput.SetIMETextLocation( (int)( rect.Left * device ), (int)( rect.Top * device ), (int)MathF.Ceiling( rect.Width * device ), (int)MathF.Ceiling( rect.Height * device ) );
		}

		MouseCursorDelta = 0;
		EscapeWasPressed = false;

		TooltipSystem.SetHovered( activeMouse?.MouseFocusPanel ?? null );
	}

	static void SetCursorPosition( Vector2 pos )
	{
		if ( !EngineInput.IsAppActive() ) return;
		if ( !EngineInput.HasMouseFocus() ) return;

		//Back out of the screen into the window it is in.
		pos += ScreenOrigin ?? default;

		EngineInput.SetCursorPosition( (int)pos.x, (int)pos.y );
	}

	static string CursorName { get; set; }

	static readonly CaseInsensitiveDictionary<InputStandardCursor_t> CursorLookup = new()
	{
		{ "none", InputStandardCursor_t.None },
		{ "arrow", InputStandardCursor_t.Arrow },
		{ "ibeam", InputStandardCursor_t.IBeam },
		{ "text", InputStandardCursor_t.IBeam },
		{ "crosshair", InputStandardCursor_t.Crosshair },
		{ "pointer", InputStandardCursor_t.Hand },
		{ "hand", InputStandardCursor_t.Hand },
		{ "progress", InputStandardCursor_t.WaitArrow },
		{ "wait", InputStandardCursor_t.HourGlass },
		{ "hourglass", InputStandardCursor_t.HourGlass },
		{ "move", InputStandardCursor_t.SizeALL },
		{ "sizenesw", InputStandardCursor_t.SizeNESW },
		{ "nesw-resize", InputStandardCursor_t.SizeNESW },
		{ "sizenwse", InputStandardCursor_t.SizeNWSE },
		{ "nwse-resize", InputStandardCursor_t.SizeNWSE },
		{ "sizewe", InputStandardCursor_t.SizeWE },
		{ "ew-resize", InputStandardCursor_t.SizeWE },
		{ "sizens", InputStandardCursor_t.SizeNS },
		{ "ns-resize", InputStandardCursor_t.SizeNS },
		{ "not-allowed", InputStandardCursor_t.No },
	};

	static readonly HashSet<string> UserCursors = new();

	static readonly CaseInsensitiveDictionary<string> CursorAliases = new()
	{
		{ "text", "ibeam" },
		{ "pointer", "hand" },
		{ "hourglass", "wait" },
		{ "nesw-resize", "sizenesw" },
		{ "nwse-resize", "sizenwse" },
		{ "ew-resize", "sizewe" },
		{ "ns-resize", "sizens" },
	};

	static void SetCursorType( string name )
	{
		name = MouseCursorVisible ? string.IsNullOrWhiteSpace( name ) ? "arrow" : name.ToLower() : "none";

		if ( CursorAliases.TryGetValue( name, out var canonical ) )
			name = canonical;

		if ( name == CursorName )
			return;

		if ( name == "none" )
		{
			EngineInput.SetCursorStandard( InputStandardCursor_t.None );
		}
		else if ( UserCursors.Contains( name ) )
		{
			EngineInput.SetCursorUser( name );
		}
		else if ( CursorLookup.TryGetValue( name, out var found ) )
		{
			EngineInput.SetCursorStandard( found );
		}
		else
		{
			name = "arrow";
			if ( name == CursorName )
				return;

			EngineInput.SetCursorStandard( InputStandardCursor_t.Arrow );
		}

		CursorName = name;
	}

	internal static void Shutdown()
	{
		KeyboardFocusPanel = null;
	}

	internal static void ShutdownUserCursors()
	{
		if ( Application.IsHeadless )
			return;

		UserCursors.Clear();
		EngineInput.ShutdownUserCursors();
	}

	internal static void CreateUserCursor( BaseFileSystem filesystem, string name, string filepath, int hotX, int hotY )
	{
		Assert.False( Application.IsHeadless );

		if ( string.IsNullOrWhiteSpace( name ) )
			return;

		if ( string.IsNullOrWhiteSpace( filepath ) )
			return;

		if ( UserCursors.Contains( name ) )
			return;

		if ( !filesystem.FileExists( filepath ) )
			return;

		if ( !EngineInput.LoadCursorFromFile( filepath, name, hotX, hotY ) )
			return;

		UserCursors.Add( name.ToLower() );
	}

	//An input context wants to set the cursor position
	internal static void SetCursorPosition( InputContext inputContext, Vector2 vector2 )
	{
		var activeMouse = Contexts.Where( x => x.MouseState != InputContext.InputState.Ignore )
							.FirstOrDefault();

		if ( activeMouse != inputContext )
			return;

		//if this is set, we're in capture mode - so just update the position which will update the position of the cursor when we come out of it
		if ( mouseCapturePosition is not null )
		{
			mouseCapturePosition = vector2;
			return;
		}

		SetCursorPosition( vector2 );
	}

	/// <summary>Return true if button is pressed</summary>
	public static bool IsButtonDown( ButtonCode code )
	{
		return PressedButtons.Contains( code );
	}

	//Return true if button is pressed
	private static void SetButtonState( ButtonCode code, bool state )
	{
		if ( state ) PressedButtons.Add( code );
		else PressedButtons.Remove( code );
	}

	/// <summary>Return true if button is pressed</summary>
	public static bool IsButtonDown( GamepadCode code )
	{
		return PressedControllerButtons.Contains( code );
	}

	//Return true if button is pressed
	private static void SetButtonState( GamepadCode code, bool state )
	{
		if ( state ) PressedControllerButtons.Add( code );
		else PressedControllerButtons.Remove( code );
	}

	//A console command from the engine.
	internal static void OnConsoleCommand( string v )
	{
		ConVarSystem.Run( v );
	}

	internal static void CloseApplication()
	{
		Application.Exit();
	}
}
