using System.Runtime.InteropServices;
using NativeEngine;

namespace Rinten;

//Keyboard and mouse on SDL3; events are read once a frame and passed straight to InputRouter
internal static unsafe partial class CoreInput
{
	private const string SDL = "SDL3";

	[LibraryImport( SDL, EntryPoint = "SDL_GetMouseFocus" )]
	private static partial IntPtr SDL_GetMouseFocus();

	[LibraryImport( SDL, EntryPoint = "SDL_GetKeyboardFocus" )]
	private static partial IntPtr SDL_GetKeyboardFocus();

	[LibraryImport( SDL, EntryPoint = "SDL_SetWindowRelativeMouseMode" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_SetWindowRelativeMouseMode( IntPtr window, [MarshalAs( UnmanagedType.U1 )] bool enabled );

	[LibraryImport( SDL, EntryPoint = "SDL_GetWindowRelativeMouseMode" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_GetWindowRelativeMouseMode( IntPtr window );

	[LibraryImport( SDL, EntryPoint = "SDL_WarpMouseInWindow" )]
	private static partial void SDL_WarpMouseInWindow( IntPtr window, float x, float y );

	[LibraryImport( SDL, EntryPoint = "SDL_ShowCursor" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_ShowCursor();

	[LibraryImport( SDL, EntryPoint = "SDL_HideCursor" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_HideCursor();

	[LibraryImport( SDL, EntryPoint = "SDL_CreateSystemCursor" )]
	private static partial IntPtr SDL_CreateSystemCursor( int id );

	[LibraryImport( SDL, EntryPoint = "SDL_SetCursor" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_SetCursor( IntPtr cursor );

	[LibraryImport( SDL, EntryPoint = "SDL_StartTextInput" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_StartTextInput( IntPtr window );

	[LibraryImport( SDL, EntryPoint = "SDL_StopTextInput" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_StopTextInput( IntPtr window );

	[LibraryImport( SDL, EntryPoint = "SDL_SetTextInputArea" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_SetTextInputArea( IntPtr window, int* rect, int cursor );

	//SDL's event numbers, which are grouped by what they are about.
	private const uint SDL_EVENT_WINDOW_FOCUS_GAINED = 0x20E;
	private const uint SDL_EVENT_WINDOW_FOCUS_LOST = 0x20F;
	private const uint SDL_EVENT_KEY_DOWN = 0x300;
	private const uint SDL_EVENT_KEY_UP = 0x301;
	private const uint SDL_EVENT_TEXT_INPUT = 0x303;
	private const uint SDL_EVENT_MOUSE_MOTION = 0x400;
	private const uint SDL_EVENT_MOUSE_BUTTON_DOWN = 0x401;
	private const uint SDL_EVENT_MOUSE_BUTTON_UP = 0x402;
	private const uint SDL_EVENT_MOUSE_WHEEL = 0x403;

	//A wheel that reports the other way around.
	private const uint SDL_MOUSEWHEEL_FLIPPED = 1;

	//The window input is being read for. Set by whoever made it. Cursors, relative mouse mode and text input are all asked of a window
	//in SDL3 rather than of the process, so there has to be one to point at. Zero in a headless run, where none of this is asked for.
	internal static IntPtr Window { get; set; }

	static bool OnWeb => OperatingSystem.IsBrowser();

	//Whether what follows can be asked of SDL - see CoreWindow.HasSdl. Not the same question as OnWeb: a browser has its own answers to some
	//of these, and a phone has neither SDL nor those - it has no cursor to hide, no pointer to lock and no focus to lose to another window.
	static bool NoSdl => !CoreWindow.HasSdl;

	//Lets a window receive typed text, which is asked of a window rather than of the process.
	//For a window of ours that is an interface of its own and reads its own keyboard.
	internal static void StartTextInput( IntPtr window )
	{
		if ( NoSdl )
		{
			ShowHostKeyboard( true );
			return;
		}

		if ( window != IntPtr.Zero ) SDL_StartTextInput( window );
	}

	//Stops text input; on Wayland the IME path can swallow key-downs the editor camera needs
	internal static void StopTextInput( IntPtr window )
	{
		if ( NoSdl )
		{
			ShowHostKeyboard( false );
			return;
		}

		if ( window != IntPtr.Zero ) SDL_StopTextInput( window );
	}

	//The window the window system says has the keyboard.
	internal static IntPtr KeyboardWindow() => NoSdl ? Window : SDL_GetKeyboardFocus();

	//Whether the application has the window system's attention.
	internal static bool IsAppActive() => NoSdl || SDL_GetKeyboardFocus() != IntPtr.Zero;

	//Whether to say the pointer is over one of our windows when it is not: an agent playing the game over MCP is at the window as far as the game is
	//concerned, whatever the desktop's pointer is doing. Without it the look is thrown away - see InputRouter.Frame - and a player nobody can see never turns.
	internal static bool MouseFocusPretended { get; set; }

	//Whether the pointer is over one of our windows.
	internal static bool HasMouseFocus() => NoSdl || MouseFocusPretended || SDL_GetMouseFocus() != IntPtr.Zero;

	//Takes the mouse away from the window system, so it can be moved without a cursor and without stopping at the edge of the screen.
	internal static void SetRelativeMouseMode( bool state )
	{
		if ( OnWeb )
		{
			//A browser will only lock the pointer during a gesture, so the page takes this as what the game wants and locks on the next click -
			//and keeps wanting it, so it is said when it changes, as SDL is below. It was said every frame, a crossing into the page for nothing.
			if ( webWantsLock == state ) return;
			webWantsLock = state;

			WebPage.SetPointerLock?.Invoke( state ? 1 : 0 );
			return;
		}

		if ( NoSdl || Window == IntPtr.Zero ) return;
		if ( SDL_GetWindowRelativeMouseMode( Window ) == state ) return;

		SDL_SetWindowRelativeMouseMode( Window, state );
	}

	//What the page was last told the game wants of the pointer - see SetRelativeMouseMode.
	static bool? webWantsLock;

	internal static bool GetRelativeMouseMode()
		=> OnWeb
			? (WebPage.PointerLocked?.Invoke() ?? 0) != 0
			: !NoSdl && Window != IntPtr.Zero && SDL_GetWindowRelativeMouseMode( Window );

	internal static Vector2 WebLook { get; private set; }

	internal static void PumpWeb()
	{
		if ( WebPage.PointerX is null ) return;

		var dx = WebPage.PointerDeltaX?.Invoke() ?? 0;
		var dy = WebPage.PointerDeltaY?.Invoke() ?? 0;
		WebLook = new Vector2( dx, dy );

		var at = new Vector2( WebPage.PointerX(), WebPage.PointerY() );
		var jumped = false;

		if ( GetRelativeMouseMode() )
		{
			Engine.InputRouter.OnMouseMotion( dx, dy );
		}
		else
		{
			//A finger lands where it lands; a mouse travels there first. Moved further than a hand moves a mouse in a frame, the pointer jumped.
			jumped = ( at - webPointer ).Length > 8;
			Engine.InputRouter.OnMousePositionChange( at.x, at.y, dx, dy );
		}

		webPointer = at;

		//Last frame's presses first, and their buttons are not let go this frame - see PumpWebButton.
		var turned = webPending;
		webPending = 0;

		for ( var bit = 0; bit < 3; bit++ )
		{
			if ( ( turned & ( 1 << bit ) ) == 0 ) continue;

			webButtons |= 1 << bit;
			Engine.InputRouter.OnMouseButton( WebButton( bit ), true, 0 );
		}

		//Held, or pressed since the last frame - see native.js, which keeps a press until it has been read, so a tap between two frames is not lost.
		var buttons = WebPage.Buttons?.Invoke() ?? 0;

		for ( var bit = 0; bit < 3; bit++ )
			PumpWebButton( bit, buttons, turned, jumped );

		//Every key asked only when one has moved: forty-nine crossings into the page a frame, to learn
		//nothing, nearly every frame. A page without the counter asks every key, as it always did.
		var version = WebPage.KeyVersion?.Invoke() ?? -1;
		if ( version >= 0 && version == webKeyVersion ) return;
		webKeyVersion = version;

		for ( var i = 0; i < WebKeys.Length; i++ )
		{
			var down = (WebPage.KeyDown?.Invoke( WebKeys[i].Code ) ?? 0) != 0;
			if ( down == webKeyDown[i] ) continue;
			webKeyDown[i] = down;
			Engine.InputRouter.OnKey( WebKeys[i].Button, WebKeys[i].Button, down, false, 0 );
		}
	}

	//One web button: presses after a pointer jump wait a frame, and a press lasts at least one frame
	static void PumpWebButton( int bit, int buttons, int turned, bool jumped )
	{
		var mask = 1 << bit;
		var down = ( buttons & mask ) != 0;
		var was = ( webButtons & mask ) != 0;

		if ( down == was ) return;

		if ( down )
		{
			if ( jumped )
			{
				webPending |= mask;
				return;
			}

			webButtons |= mask;
			Engine.InputRouter.OnMouseButton( WebButton( bit ), true, 0 );
			return;
		}

		if ( ( turned & mask ) != 0 ) return;

		webButtons &= ~mask;
		Engine.InputRouter.OnMouseButton( WebButton( bit ), false, 0 );
	}

	static ButtonCode WebButton( int bit ) => bit switch
	{
		1 => ButtonCode.MouseMiddle,
		2 => ButtonCode.MouseRight,
		_ => ButtonCode.MouseLeft,
	};

	//The buttons the engine has been told are down.
	static int webButtons;

	//Buttons pressed where the pointer jumped, to be pressed next frame - see PumpWebButton.
	static int webPending;

	//Where the pointer was last frame, in the canvas's pixels.
	static Vector2 webPointer;

	//The page's key counter as the keys were last read - see PumpWeb.
	static int webKeyVersion = -1;
	static readonly (string Code, ButtonCode Button)[] WebKeys =
	[
		("KeyA", ButtonCode.KEY_A), ("KeyB", ButtonCode.KEY_B), ("KeyC", ButtonCode.KEY_C),
		("KeyD", ButtonCode.KEY_D), ("KeyE", ButtonCode.KEY_E), ("KeyF", ButtonCode.KEY_F),
		("KeyG", ButtonCode.KEY_G), ("KeyH", ButtonCode.KEY_H), ("KeyI", ButtonCode.KEY_I),
		("KeyJ", ButtonCode.KEY_J), ("KeyK", ButtonCode.KEY_K), ("KeyL", ButtonCode.KEY_L),
		("KeyM", ButtonCode.KEY_M), ("KeyN", ButtonCode.KEY_N), ("KeyO", ButtonCode.KEY_O),
		("KeyP", ButtonCode.KEY_P), ("KeyQ", ButtonCode.KEY_Q), ("KeyR", ButtonCode.KEY_R),
		("KeyS", ButtonCode.KEY_S), ("KeyT", ButtonCode.KEY_T), ("KeyU", ButtonCode.KEY_U),
		("KeyV", ButtonCode.KEY_V), ("KeyW", ButtonCode.KEY_W), ("KeyX", ButtonCode.KEY_X),
		("KeyY", ButtonCode.KEY_Y), ("KeyZ", ButtonCode.KEY_Z),
		("Digit0", ButtonCode.KEY_0), ("Digit1", ButtonCode.KEY_1), ("Digit2", ButtonCode.KEY_2),
		("Digit3", ButtonCode.KEY_3), ("Digit4", ButtonCode.KEY_4), ("Digit5", ButtonCode.KEY_5),
		("Digit6", ButtonCode.KEY_6), ("Digit7", ButtonCode.KEY_7), ("Digit8", ButtonCode.KEY_8),
		("Digit9", ButtonCode.KEY_9),
		("Space", ButtonCode.KEY_SPACE), ("Tab", ButtonCode.KEY_TAB), ("Escape", ButtonCode.KEY_ESCAPE),
		("ShiftLeft", ButtonCode.KEY_LSHIFT), ("ShiftRight", ButtonCode.KEY_RSHIFT),
		("ControlLeft", ButtonCode.KEY_LCONTROL), ("ControlRight", ButtonCode.KEY_RCONTROL),
		("AltLeft", ButtonCode.KEY_LALT), ("AltRight", ButtonCode.KEY_RALT),
		("ArrowUp", ButtonCode.KEY_UP), ("ArrowDown", ButtonCode.KEY_DOWN),
		("ArrowLeft", ButtonCode.KEY_LEFT), ("ArrowRight", ButtonCode.KEY_RIGHT),
	];
	static readonly bool[] webKeyDown = new bool[WebKeys.Length];

	//Puts the cursor somewhere, in the window's pixels.
	internal static void SetCursorPosition( int x, int y )
	{
		if ( NoSdl || Window == IntPtr.Zero ) return;

		var density = CoreWindow.DensityOf( Window );

		SDL_WarpMouseInWindow( Window, x / density, y / density );
	}

	//Whether a window may put text into a panel - which is what turns composed text, and the on-screen keyboard on the platforms that have one, on and off.
	internal static void SetIMEAllowed( bool allowed )
	{
		//A phone's keyboard is the host's - see CoreInput.Host.
		if ( NoSdl )
		{
			ShowHostKeyboard( allowed );
			return;
		}

		if ( Window == IntPtr.Zero ) return;
		if ( imeAllowed == allowed ) return;

		imeAllowed = allowed;

		if ( allowed ) SDL_StartTextInput( Window );
		else SDL_StopTextInput( Window );
	}

	private static bool imeAllowed;

	//Where the text being typed is, in the window's pixels, so the platform can put its candidate window somewhere that does not cover it.
	internal static void SetIMETextLocation( int x, int y, int width, int height )
	{
		if ( NoSdl || Window == IntPtr.Zero ) return;

		var density = CoreWindow.DensityOf( Window );

		//SDL_Rect is four ints and the cursor offset is measured from the start of the text, which we do not track - the whole box will do.
		var rect = stackalloc int[4] { (int)( x / density ), (int)( y / density ), (int)MathF.Ceiling( width / density ), (int)MathF.Ceiling( height / density ) };
		SDL_SetTextInputArea( Window, rect, 0 );
	}

	private static readonly Dictionary<InputStandardCursor_t, IntPtr> cursors = new();

	//SDL's own cursors, in the order it declares them, for the ones the UI asks for.
	private static int SystemCursor( InputStandardCursor_t cursor ) => cursor switch
	{
		InputStandardCursor_t.Arrow => 0,
		InputStandardCursor_t.IBeam => 1,
		InputStandardCursor_t.HourGlass => 2,
		InputStandardCursor_t.Crosshair => 3,
		InputStandardCursor_t.WaitArrow => 4,
		InputStandardCursor_t.SizeNWSE => 5,
		InputStandardCursor_t.SizeNESW => 6,
		InputStandardCursor_t.SizeWE => 7,
		InputStandardCursor_t.SizeNS => 8,
		InputStandardCursor_t.SizeALL => 9,
		InputStandardCursor_t.No => 10,
		InputStandardCursor_t.Hand or InputStandardCursor_t.HandClosed => 11,

		//Up has no counterpart in SDL and nothing asks for it.
		_ => 0,
	};

	//Shows one of the window system's own cursors.
	internal static void SetCursorStandard( InputStandardCursor_t cursor )
	{
		if ( NoSdl ) return;

		if ( cursor == InputStandardCursor_t.None )
		{
			SDL_HideCursor();
			return;
		}

		if ( !cursors.TryGetValue( cursor, out var handle ) )
		{
			handle = SDL_CreateSystemCursor( SystemCursor( cursor ) );
			cursors[cursor] = handle;
		}

		if ( handle != IntPtr.Zero ) SDL_SetCursor( handle );

		SDL_ShowCursor();
	}

	//Reads an event SDL has just given us and passes it on. The event is a union, so this reads fields out of it by where they are rather than
	//through a type: every event begins with its number, four bytes of padding and a timestamp, and the rest is laid out by what sort of event it is.
	internal static void Dispatch( byte* raw )
	{
		var type = *(uint*)raw;

		switch ( type )
		{
			case SDL_EVENT_KEY_DOWN:
			case SDL_EVENT_KEY_UP:
				Key( raw, type == SDL_EVENT_KEY_DOWN );
				break;

			case SDL_EVENT_TEXT_INPUT:
				Text( raw );
				break;

			case SDL_EVENT_MOUSE_MOTION:
				Motion( raw );
				break;

			case SDL_EVENT_MOUSE_BUTTON_DOWN:
			case SDL_EVENT_MOUSE_BUTTON_UP:
				MouseButton( raw, type == SDL_EVENT_MOUSE_BUTTON_DOWN );
				break;

			case SDL_EVENT_MOUSE_WHEEL:
				Wheel( raw );
				break;

			case SDL_EVENT_WINDOW_FOCUS_GAINED:
				Engine.InputRouter.OnWindowActive( true );
				break;

			case SDL_EVENT_WINDOW_FOCUS_LOST:
				Engine.InputRouter.OnWindowActive( false );
				break;

			case SDL_EVENT_GAMEPAD_ADDED:
			case SDL_EVENT_GAMEPAD_REMOVED:
			case SDL_EVENT_GAMEPAD_BUTTON_DOWN:
			case SDL_EVENT_GAMEPAD_BUTTON_UP:
			case SDL_EVENT_GAMEPAD_AXIS_MOTION:
				Gamepad( raw, type );
				break;
		}
	}

	private static void Key( byte* raw, bool down )
	{
		var scancode = *(uint*)(raw + 24);
		var keycode = *(uint*)(raw + 28);
		var repeat = *(raw + 37) != 0;

		var scanButton = FromScancode( scancode );
		var keyButton = FromKeycode( keycode );

		//A key we have no name for is a key nothing can be bound to.
		if ( scanButton == ButtonCode.BUTTON_CODE_INVALID ) return;
		if ( keyButton == ButtonCode.BUTTON_CODE_INVALID ) keyButton = scanButton;

		//Escape gives the mouse back to the editor a game is being played inside of, and never reaches the game. Handed on instead,
		//a game that swallows it for its own menu - which most do - would be a game with no way out of it but killing the editor.
		if ( down && !repeat && scanButton == ButtonCode.KEY_ESCAPE && Engine.InputRouter.GameHasInput )
		{
			Engine.InputRouter.ReleaseGameInput();
			return;
		}

		Engine.InputRouter.OnKey( scanButton, keyButton, down, repeat, 0 );
	}

	private static void Text( byte* raw )
	{
		var text = Marshal.PtrToStringUTF8( *(IntPtr*)(raw + 24) );
		if ( string.IsNullOrEmpty( text ) ) return;

		foreach ( var character in text )
			Engine.InputRouter.OnText( character );
	}

	private static void Motion( byte* raw )
	{
		var x = *(float*)(raw + 28);
		var y = *(float*)(raw + 32);
		var dx = *(float*)(raw + 36);
		var dy = *(float*)(raw + 40);

		//With the mouse taken from the window system there is no position to speak of, only movement - and the cursor is not where the pointer is.
		if ( GetRelativeMouseMode() )
		{
			Engine.InputRouter.OnMouseMotion( dx, dy );
			return;
		}

		Engine.InputRouter.OnMousePositionChange( x, y, dx, dy );
	}

	private static void MouseButton( byte* raw, bool down )
	{
		var button = FromMouseButton( *(raw + 24) );
		if ( button == ButtonCode.BUTTON_CODE_INVALID ) return;

		Engine.InputRouter.OnMouseButton( button, down, 0 );
	}

	private static void Wheel( byte* raw )
	{
		var x = *(float*)(raw + 24);
		var y = *(float*)(raw + 28);
		var direction = *(uint*)(raw + 32);

		if ( direction == SDL_MOUSEWHEEL_FLIPPED )
		{
			x = -x;
			y = -y;
		}

		if ( x == 0 && y == 0 ) return;

		//Fractional wheel deltas are kept, so trackpads do not scroll too far
		Engine.InputRouter.OnMouseWheel( x, y, 0 );
	}
}
