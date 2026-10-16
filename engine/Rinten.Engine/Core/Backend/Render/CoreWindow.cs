using NativeEngine;
using System.Runtime.InteropServices;

namespace Rinten;

//A native window of our own on SDL3, which the core makes a Vulkan surface for
internal sealed unsafe partial class CoreWindow : IDisposable
{
	private const string SDL = "SDL3";

	private const uint SDL_INIT_VIDEO = 0x00000020u;

	private const ulong SDL_WINDOW_HIDDEN = 0x0000000000000008ul;
	private const ulong SDL_WINDOW_BORDERLESS = 0x0000000000000010ul;
	private const ulong SDL_WINDOW_RESIZABLE = 0x0000000000000020ul;
	private const ulong SDL_WINDOW_ALWAYS_ON_TOP = 0x0000000000010000ul;

	//A window that is not a place of work: no taskbar button, no alt-tab.
	private const ulong SDL_WINDOW_UTILITY = 0x0000000000020000ul;

	private const ulong SDL_WINDOW_TRANSPARENT = 0x0000000040000000ul;
	private const ulong SDL_WINDOW_NOT_FOCUSABLE = 0x0000000080000000ul;
	private const ulong SDL_WINDOW_VULKAN = 0x0000000010000000ul;

	[LibraryImport( SDL, EntryPoint = "SDL_InitSubSystem" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_InitSubSystem( uint flags );

	[LibraryImport( SDL, EntryPoint = "SDL_WasInit" )]
	private static partial uint SDL_WasInit( uint flags );

	[LibraryImport( SDL, EntryPoint = "SDL_SetHint", StringMarshalling = StringMarshalling.Utf8 )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_SetHint( string name, string value );

	[LibraryImport( SDL, EntryPoint = "SDL_GetCurrentVideoDriver" )]
	private static partial IntPtr SDL_GetCurrentVideoDriver();

	[LibraryImport( SDL, EntryPoint = "SDL_SetWindowHitTest" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_SetWindowHitTest( IntPtr window, IntPtr callback, IntPtr data );

	[LibraryImport( SDL, EntryPoint = "SDL_SetWindowBordered" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_SetWindowBordered( IntPtr window, [MarshalAs( UnmanagedType.U1 )] bool bordered );

	[LibraryImport( SDL, EntryPoint = "SDL_RaiseWindow" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_RaiseWindow( IntPtr window );

	[LibraryImport( SDL, EntryPoint = "SDL_CreateWindow", StringMarshalling = StringMarshalling.Utf8 )]
	private static partial IntPtr SDL_CreateWindow( string title, int width, int height, ulong flags );

	[LibraryImport( SDL, EntryPoint = "SDL_WarpMouseInWindow" )]
	private static partial void SDL_WarpMouseInWindow( IntPtr window, float x, float y );

	[LibraryImport( SDL, EntryPoint = "SDL_DestroyWindow" )]
	private static partial void SDL_DestroyWindow( IntPtr window );

	[LibraryImport( SDL, EntryPoint = "SDL_ShowWindow" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_ShowWindow( IntPtr window );

	[LibraryImport( SDL, EntryPoint = "SDL_HideWindow" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_HideWindow( IntPtr window );

	[LibraryImport( SDL, EntryPoint = "SDL_SetWindowTitle", StringMarshalling = StringMarshalling.Utf8 )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_SetWindowTitle( IntPtr window, string title );

	[LibraryImport( SDL, EntryPoint = "SDL_GetWindowSize" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_GetWindowSize( IntPtr window, out int width, out int height );

	[LibraryImport( SDL, EntryPoint = "SDL_GetWindowSizeInPixels" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_GetWindowSizeInPixels( IntPtr window, out int width, out int height );

	[LibraryImport( SDL, EntryPoint = "SDL_GetWindowPosition" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_GetWindowPosition( IntPtr window, out int x, out int y );

	[LibraryImport( SDL, EntryPoint = "SDL_SetWindowPosition" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_SetWindowPosition( IntPtr window, int x, int y );

	[LibraryImport( SDL, EntryPoint = "SDL_MaximizeWindow" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_MaximizeWindow( IntPtr window );

	[LibraryImport( SDL, EntryPoint = "SDL_MinimizeWindow" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_MinimizeWindow( IntPtr window );

	[LibraryImport( SDL, EntryPoint = "SDL_RestoreWindow" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_RestoreWindow( IntPtr window );

	[LibraryImport( SDL, EntryPoint = "SDL_SetWindowFullscreen" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_SetWindowFullscreen( IntPtr window,
		[MarshalAs( UnmanagedType.U1 )] bool fullscreen );


	[LibraryImport( SDL, EntryPoint = "SDL_GetWindowFlags" )]
	private static partial ulong SDL_GetWindowFlags( IntPtr window );

	[LibraryImport( SDL, EntryPoint = "SDL_SetWindowSize" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_SetWindowSize( IntPtr window, int width, int height );

	[LibraryImport( SDL, EntryPoint = "SDL_SyncWindow" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_SyncWindow( IntPtr window );

	[LibraryImport( SDL, EntryPoint = "SDL_GetWindowProperties" )]
	private static partial uint SDL_GetWindowProperties( IntPtr window );

	[LibraryImport( SDL, EntryPoint = "SDL_GetGlobalProperties" )]
	private static partial uint SDL_GetGlobalProperties();

	[LibraryImport( SDL, EntryPoint = "SDL_GetPointerProperty", StringMarshalling = StringMarshalling.Utf8 )]
	private static partial IntPtr SDL_GetPointerProperty( uint properties, string name, IntPtr fallback );

	[LibraryImport( SDL, EntryPoint = "SDL_GetNumberProperty", StringMarshalling = StringMarshalling.Utf8 )]
	private static partial long SDL_GetNumberProperty( uint properties, string name, long fallback );

	[LibraryImport( SDL, EntryPoint = "SDL_PumpEvents" )]
	private static partial void SDL_PumpEvents();

	[LibraryImport( SDL, EntryPoint = "SDL_GetPrimaryDisplay" )]
	private static partial uint SDL_GetPrimaryDisplay();

	[System.Runtime.InteropServices.StructLayout( System.Runtime.InteropServices.LayoutKind.Sequential )]
	private struct SdlRect
	{
		public int X, Y, Width, Height;
	}

	[LibraryImport( SDL, EntryPoint = "SDL_GetDisplayUsableBounds" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_GetDisplayUsableBounds( uint display, out SdlRect bounds );

	/// <summary>The part of the main screen a window may sit in - the desktop minus the taskbar. All zeros when there is no screen to ask.</summary>
	public static (int X, int Y, int Width, int Height) UsableDesktop()
	{
		var display = SDL_GetPrimaryDisplay();
		if ( display == 0 ) return (0, 0, 0, 0);

		return SDL_GetDisplayUsableBounds( display, out var bounds )
			? (bounds.X, bounds.Y, bounds.Width, bounds.Height)
			: (0, 0, 0, 0);
	}

	[LibraryImport( SDL, EntryPoint = "SDL_GetDisplayForWindow" )]
	private static partial uint SDL_GetDisplayForWindow( IntPtr window );

	[LibraryImport( SDL, EntryPoint = "SDL_GetDisplayBounds" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_GetDisplayBounds( uint display, out SdlRect bounds );

	[LibraryImport( SDL, EntryPoint = "SDL_GetFullscreenDisplayModes" )]
	private static unsafe partial SdlDisplayMode** SDL_GetFullscreenDisplayModes( uint display, out int count );

	[LibraryImport( SDL, EntryPoint = "SDL_free" )]
	private static unsafe partial void SDL_free( void* memory );

	//SDL3's SDL_DisplayMode, field for field.
	[System.Runtime.InteropServices.StructLayout( System.Runtime.InteropServices.LayoutKind.Sequential )]
	private struct SdlDisplayMode
	{
		public uint DisplayId;
		public uint Format;
		public int Width, Height;
		public float PixelDensity;
		public float RefreshRate;
		public int RefreshNumerator, RefreshDenominator;
		public IntPtr Internal;
	}

	//The screen this window is on, or the main one where it is on none yet.
	private uint Display => Handle != IntPtr.Zero && SDL_GetDisplayForWindow( Handle ) is var shown and not 0
		? shown
		: SDL_GetPrimaryDisplay();

	/// <summary>The modes this window's screen can be driven at - width, height and refresh - best first, as the screen lists them. Empty where there is no screen to ask.</summary>
	public unsafe (int Width, int Height, float RefreshRate)[] DisplayModes()
	{
		if ( OperatingSystem.IsBrowser() ) return Array.Empty<(int, int, float)>();

		var display = Display;
		if ( display == 0 ) return Array.Empty<(int, int, float)>();

		var modes = SDL_GetFullscreenDisplayModes( display, out var count );
		if ( modes is null ) return Array.Empty<(int, int, float)>();

		try
		{
			var found = new (int, int, float)[count];

			for ( var i = 0; i < count; i++ )
				found[i] = (modes[i]->Width, modes[i]->Height, modes[i]->RefreshRate);

			return found;
		}
		finally
		{
			SDL_free( modes );
		}
	}

	/// <summary>The window system's frame on the window, or none. A window of ours that draws its own caption has none already; this is for a game's.</summary>
	public void SetBordered( bool bordered )
	{
		if ( OperatingSystem.IsBrowser() || Handle == IntPtr.Zero || Adopted ) return;

		SDL_SetWindowBordered( Handle, bordered );
	}

	/// <summary>Covers the whole of the screen the window is on, as a window - no frame, the desktop's bars under it, the screen's own resolution. What a game calls borderless: full screen to look at, a window to the desktop, so switching away is instant.</summary>
	public void CoverDisplay()
	{
		if ( OperatingSystem.IsBrowser() || Handle == IntPtr.Zero || Adopted ) return;

		if ( !SDL_GetDisplayBounds( Display, out var bounds ) ) return;

		SDL_SetWindowFullscreen( Handle, false );
		SDL_SetWindowBordered( Handle, false );
		SDL_SetWindowPosition( Handle, bounds.X, bounds.Y );
		SDL_SetWindowSize( Handle, bounds.Width, bounds.Height );
		SDL_SyncWindow( Handle );
	}

	[LibraryImport( SDL, EntryPoint = "SDL_PollEvent" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static unsafe partial bool SDL_PollEvent( void* eventOut );

	//The application has been asked to quit.
	private const uint SDL_EVENT_QUIT = 0x100;

	//A window's close box. SDL numbers its window events from 0x202; this is the fifteenth of them.
	private const uint SDL_EVENT_WINDOW_CLOSE_REQUESTED = 0x210;

	//The window system took the keyboard away from a window.
	private const uint SDL_EVENT_WINDOW_FOCUS_LOST = 0x20F;

	[LibraryImport( SDL, EntryPoint = "SDL_GetError" )]
	private static partial IntPtr SDL_GetError();

	[LibraryImport( SDL, EntryPoint = "SDL_GetWindowID" )]
	private static partial uint SDL_GetWindowID( IntPtr window );

	//The mouse and keyboard events, which a window keeps for itself rather than handing to the game: an
	//editor window has its own pointer, in its own pixels, and its own idea of what is being typed into it.
	private const uint SDL_EVENT_KEY_DOWN = 0x300;
	private const uint SDL_EVENT_KEY_UP = 0x301;
	private const uint SDL_EVENT_TEXT_INPUT = 0x303;
	private const uint SDL_EVENT_MOUSE_MOTION = 0x400;
	private const uint SDL_EVENT_MOUSE_BUTTON_DOWN = 0x401;
	private const uint SDL_EVENT_MOUSE_BUTTON_UP = 0x402;
	private const uint SDL_EVENT_MOUSE_WHEEL = 0x403;

	private Vector2 pointer;
	private MouseButtons pointerButtons;

	//Buttons pressed since last read, latched so a click shorter than a tick isn't missed
	private MouseButtons pointerPressed;

	private Vector2 wheel;

	//Both ways round, because a key that is only ever seen going down is a key nothing can hold: the editor flies its camera on W being held, and asked whether it was, the answer was always no.
	/// <summary>A key going down or coming up in this window, with what was held with it.</summary>
	public readonly record struct Key( ButtonCode Button, KeyboardModifiers Modifiers, bool Repeat, bool Down );

	private readonly Queue<Key> keys = new();
	private readonly Queue<char> typed = new();

	//SDL mouse button numbers; differ from our flags and from SDL's global mask (middle/right swapped)

	private const byte SdlButtonLeft = 1;
	private const byte SdlButtonMiddle = 2;
	private const byte SdlButtonRight = 3;
	private const byte SdlButtonBack = 4;
	private const byte SdlButtonForward = 5;

	//The button SDL means by a number, or none for one nobody named - a mouse with more buttons than five, which is not a mouse this reads.
	private static MouseButtons ButtonOf( byte button ) => button switch
	{
		SdlButtonLeft => MouseButtons.Left,
		SdlButtonRight => MouseButtons.Right,
		SdlButtonMiddle => MouseButtons.Middle,
		SdlButtonBack => MouseButtons.Back,
		SdlButtonForward => MouseButtons.Forward,
		_ => MouseButtons.None,
	};

	/// <summary>Where the pointer is inside this window, and which of its buttons are down.</summary>
	public (float X, float Y, MouseButtons Buttons) Pointer()
	{
		var buttons = pointerButtons | pointerPressed;
		pointerPressed = MouseButtons.None;

		return (pointer.x, pointer.y, buttons);
	}

	//The same, without taking it: for saying in a log what the window thinks the pointer is doing, which reading it would otherwise change.
	internal (float X, float Y, MouseButtons Buttons) LastPointer => (pointer.x, pointer.y, pointerButtons);

	//The window SDL says owns the pointer (hovered, or dragged from); kept after the pointer leaves
	internal static CoreWindow Pointed { get; private set; }

	//The window with keyboard focus, or null when another app has it; asked of SDL each time
	internal static CoreWindow Keyboard => FindHandle( CoreInput.KeyboardWindow() );

	//The pointer is here, and it is in this window.
	private void PointerAt( Vector2 at )
	{
		pointer = at;
		Pointed = this;
	}

	//Rather than working out where that is on the desktop and warping there: the number came from a widget in this window, so this is the window it means, and the window system knows where that is better than we do.
	/// <summary>Puts the pointer somewhere inside this window, in this window's own coordinates.</summary>
	public void WarpPointer( Vector2 at )
	{
		if ( Handle == IntPtr.Zero ) return;

		SDL_WarpMouseInWindow( Handle, at.x, at.y );
	}

	//Clears pointer position and held buttons when the window's input is handed elsewhere
	internal static void ForgetPointer( IntPtr handle )
	{
		var window = FindHandle( handle );

		if ( window is null ) return;

		window.pointer = new Vector2( -1, -1 );

		ForgetHeld( window );

		if ( ReferenceEquals( Pointed, window ) ) Pointed = null;
	}

	//Forgets what is held down in a window, keeping where the pointer is. For a window that handed
	//the keyboard away and kept the mouse: the press that asked for it is released elsewhere.
	internal static void ForgetButtons( IntPtr handle )
	{
		var window = FindHandle( handle );

		if ( window is null ) return;

		ForgetHeld( window );
	}

	private static void ForgetHeld( CoreWindow window )
	{
		window.pointerButtons = MouseButtons.None;
		window.pointerPressed = MouseButtons.None;
		window.keys.Clear();
		window.typed.Clear();
	}

	//Puts the pointer somewhere and holds a button, as though the window system had said so. For
	//testing the way a click travels from a window to a widget without a person to do the clicking.
	internal void PretendPointer( Vector2 at, bool leftDown )
	{
		PretendPointer( at, leftDown ? MouseButtons.Left : MouseButtons.None, leftDown ? MouseButtons.None : MouseButtons.Left );
	}

	//Puts the pointer somewhere, presses some buttons and releases others, as though the window system had said so - exactly what its own events
	//do, so whatever is read next reads them the same way. An agent driving the editor over MCP comes in here; see Editor.Mcp.UiInputTools.
	internal void PretendPointer( Vector2 at, MouseButtons press, MouseButtons release )
	{
		PointerAt( at );

		pointerButtons |= press;
		pointerPressed |= press;
		pointerButtons &= ~release;
	}

	//The wheel turned, as though the window system had said so.
	internal void PretendWheel( Vector2 turned )
	{
		wheel += turned;
	}

	//A key went down or came up, as though the window system had said so.
	internal void PretendKey( ButtonCode button, KeyboardModifiers modifiers, bool down )
	{
		keys.Enqueue( new Key( button, modifiers, Repeat: false, down ) );
	}

	//Text was composed, as though the window system had said so.
	internal void PretendTyped( string text )
	{
		if ( string.IsNullOrEmpty( text ) ) return;

		foreach ( var letter in text ) typed.Enqueue( letter );
	}

	/// <summary>How far the wheel has turned since this was last asked, and clears it.</summary>
	public Vector2 TakeWheel()
	{
		var turned = wheel;
		wheel = 0;

		return turned;
	}

	/// <summary>The keys pressed in this window since this was last asked, and clears them. Presses rather than states: what a text field wants to know is that backspace was hit, not that it is being held.</summary>
	public List<Key> TakeKeys()
	{
		if ( keys.Count == 0 ) return null;

		var pressed = new List<Key>( keys );
		keys.Clear();

		return pressed;
	}

	/// <summary>The text typed into this window since this was last asked, and clears it. Letters as the platform composed them - which is not the same as the keys that were pressed to get them, and is what goes into a text field.</summary>
	public List<char> TakeTyped()
	{
		if ( typed.Count == 0 ) return null;

		var letters = new List<char>( typed );
		typed.Clear();

		return letters;
	}

	/// <summary>Lets this window receive typed text at all - and the platform put its candidate window somewhere sensible while it is being composed.</summary>
	public void StartTextInput() => SetTextInput( true );

	/// <summary>Turns composed text on or off for this window. Off unless a field is actually being typed into: a window that always asks for text is one whose printable keys may never arrive as keys, which is what flies the editor camera.</summary>
	public void SetTextInput( bool enabled )
	{
		if ( Handle == IntPtr.Zero || textInput == enabled ) return;

		textInput = enabled;

		if ( enabled ) CoreInput.StartTextInput( Handle );
		else CoreInput.StopTextInput( Handle );
	}

	private bool textInput;

	//Keeps this window's own story straight - where its pointer is and what is being typed into it - out of one event that is known to be its own.
	private void Read( uint type, byte* buffer )
	{
		if ( type is SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EVENT_MOUSE_BUTTON_UP or SDL_EVENT_MOUSE_MOTION )
			FollowCaption( type, buffer );

		switch ( type )
		{
			case SDL_EVENT_KEY_DOWN:
			case SDL_EVENT_KEY_UP:
			{
				//The scancode is where the key is on the keyboard and the keycode is what it means with this layout; a name
				//for the second is better where there is one, because that is the key somebody would say they pressed.
				var button = CoreInput.FromKeycode( *(uint*)(buffer + 28) );

				if ( button == ButtonCode.BUTTON_CODE_INVALID )
					button = CoreInput.FromScancode( *(uint*)(buffer + 24) );

				if ( button == ButtonCode.BUTTON_CODE_INVALID ) break;

				keys.Enqueue( new Key( button, Modifiers( *(ushort*)(buffer + 32) ),
					*(buffer + 37) != 0, type == SDL_EVENT_KEY_DOWN ) );
				break;
			}

			case SDL_EVENT_TEXT_INPUT:
			{
				var text = Marshal.PtrToStringUTF8( *(IntPtr*)(buffer + 24) );
				if ( string.IsNullOrEmpty( text ) ) break;

				foreach ( var letter in text ) typed.Enqueue( letter );
				break;
			}
			case SDL_EVENT_MOUSE_MOTION:
				PointerAt( new Vector2( *(float*)(buffer + 28), *(float*)(buffer + 32) ) );
				break;

			case SDL_EVENT_MOUSE_BUTTON_DOWN:
			case SDL_EVENT_MOUSE_BUTTON_UP:
			{
				var button = *(buffer + 24);
				var down = type == SDL_EVENT_MOUSE_BUTTON_DOWN;

				var pressed = ButtonOf( button );

				if ( down )
				{
					pointerButtons |= pressed;

					//Held until it has been read, so a click quicker than a tick is still a click
					pointerPressed |= pressed;
				}
				else
				{
					pointerButtons &= ~pressed;
				}

				PointerAt( new Vector2( *(float*)(buffer + 28), *(float*)(buffer + 32) ) );
				break;
			}

			case SDL_EVENT_MOUSE_WHEEL:
				wheel += new Vector2( *(float*)(buffer + 24), *(float*)(buffer + 28) );
				break;

			//The window changed shape - resized by anybody, or maximised, minimised or put back. Noted here and handed on by the interface's own
			//frame (see CoreUiWindow.Resized), on the main thread: these arrive while the queue is pumped, not from the watch -liveresize adds.
			case SDL_EVENT_WINDOW_RESIZED:
			case SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
			case SDL_EVENT_WINDOW_MINIMIZED:
			case SDL_EVENT_WINDOW_MAXIMIZED:
			case SDL_EVENT_WINDOW_RESTORED:
				shapeChanged = true;
				break;
		}
	}

	//Whether the window changed shape since this was last asked.
	private bool shapeChanged;

	//Whether the window changed shape since anybody last asked - asking clears it.
	internal bool TakeShapeChanged()
	{
		var changed = shapeChanged;
		shapeChanged = false;

		return changed;
	}

	//What was held down with a key, out of SDL's own bits.
	private static KeyboardModifiers Modifiers( ushort mod )
	{
		const ushort shift = 0x0003;
		const ushort ctrl = 0x00C0;
		const ushort alt = 0x0300;

		var modifiers = KeyboardModifiers.None;

		if ( (mod & shift) != 0 ) modifiers |= KeyboardModifiers.Shift;
		if ( (mod & ctrl) != 0 ) modifiers |= KeyboardModifiers.Ctrl;
		if ( (mod & alt) != 0 ) modifiers |= KeyboardModifiers.Alt;

		return modifiers;
	}

	/// <summary>Matches sbc_window_kind.</summary>
	public enum Kind
	{
		Win32 = 0,
		X11 = 1,
		Wayland = 2,
		Android = 3,
		Metal = 4,
		Canvas = 5,
	}

	/// <summary>SDL's window. Zero once disposed.</summary>
	public IntPtr Handle { get; private set; }

	/// <summary>Whether the platform handed us this window rather than SDL making it.</summary>
	public bool Adopted { get; private set; }

	/// <summary>What sort of window the platform gave us.</summary>
	public Kind WindowKind { get; private set; }

	/// <summary>The native window, as the core wants it.</summary>
	public ulong NativeWindow { get; private set; }

	/// <summary>The native display or connection, where the platform has one.</summary>
	public ulong NativeDisplay { get; private set; }

	/// <summary>Why the last call failed, from SDL.</summary>
	public static string LastError
		=> HasSdl ? Marshal.PtrToStringUTF8( SDL_GetError() ) ?? string.Empty : string.Empty;

	//Whether SDL is available at all; browser and Android builds don't ship it
	internal static bool HasSdl => !OperatingSystem.IsBrowser() && !OperatingSystem.IsAndroid();

	/// <summary>The size to draw at, in pixels rather than in the units the window was asked for - they differ on a display with scaling, and the swapchain wants pixels.</summary>
	public (int Width, int Height) SizeInPixels
	{
		get
		{
			if ( Handle == IntPtr.Zero ) return (0, 0);
			if ( Adopted ) return CanvasSize();

			return SDL_GetWindowSizeInPixels( Handle, out var width, out var height )
				? (width, height)
				: (0, 0);
		}
	}

	/// <summary>Window size in window-system units (as SetSize and Position use), not pixels; differs when scaled</summary>
	public (int Width, int Height) Size
	{
		get
		{
			if ( Handle == IntPtr.Zero ) return (0, 0);
			if ( Adopted ) return CanvasSize();

			return SDL_GetWindowSize( Handle, out var width, out var height )
				? (width, height)
				: (0, 0);
		}
	}

	private (int Width, int Height) canvasPixels;

	(int Width, int Height) CanvasSize()
	{
		var width = WebPage.CanvasWidth?.Invoke() ?? canvasPixels.Width;
		var height = WebPage.CanvasHeight?.Invoke() ?? canvasPixels.Height;
		if ( width > 0 && height > 0 ) canvasPixels = (width, height);
		return canvasPixels;
	}

	private CoreWindow() { }

	private static bool saidDriver;

	//Whether this process runs under a Wayland compositor; SDL is told to prefer it over XWayland
	internal static bool OnWayland =>
		OperatingSystem.IsLinux()
		&& !string.IsNullOrEmpty( Environment.GetEnvironmentVariable( "WAYLAND_DISPLAY" ) );

	private static void PreferWayland()
	{
		if ( !OperatingSystem.IsLinux() ) return;

		if ( string.IsNullOrEmpty( Environment.GetEnvironmentVariable( "WAYLAND_DISPLAY" ) ) ) return;
		if ( !string.IsNullOrEmpty( Environment.GetEnvironmentVariable( "SDL_VIDEO_DRIVER" ) ) ) return;

		SDL_SetHint( "SDL_VIDEO_DRIVER", "wayland,x11" );
	}

	//Sets the WM_CLASS / app_id so desktops match windows to our .desktop entry; respects SDL_APP_ID
	private static void AppId()
	{
		if ( !OperatingSystem.IsLinux() ) return;

		if ( !string.IsNullOrEmpty( Environment.GetEnvironmentVariable( "SDL_APP_ID" ) ) ) return;

		var name = System.IO.Path.GetFileNameWithoutExtension( Environment.ProcessPath );

		if ( string.IsNullOrEmpty( name ) ) return;

		SDL_SetHint( "SDL_APP_ID", name.ToLowerInvariant() );
	}

	//What a point in a frameless window means to the window system. SDL's own numbering, from SDL_HitTestResult.
	private const int HitNormal = 0;
	private const int HitDraggable = 1;
	private const int HitResizeTopLeft = 2;
	private const int HitResizeTop = 3;
	private const int HitResizeTopRight = 4;
	private const int HitResizeRight = 5;
	private const int HitResizeBottomRight = 6;
	private const int HitResizeBottom = 7;
	private const int HitResizeBottomLeft = 8;
	private const int HitResizeLeft = 9;

	[StructLayout( LayoutKind.Sequential )]
	private struct SdlPoint
	{
		public int X, Y;
	}

	//Windows that answer a hit test, by the handle SDL passes back.
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, CoreWindow> framed = new();

	/// <summary>Caption area that drags the window, in window units; set by the caption drawer, excluding buttons</summary>
	public (int X, int Y, int Width, int Height) DragStrip
	{
		get => strips.Length > 0 ? strips[0] : default;
		set => strips = value.Width > 0 && value.Height > 0
			? new[] { value }
			: NoStrips;
	}

	/// <summary>Every such strip, for a bar with more than one gap in it - swapped whole, because the window system reads it on its own thread.</summary>
	public (int X, int Y, int Width, int Height)[] DragStrips
	{
		get => strips;
		set => strips = value ?? NoStrips;
	}

	private static readonly (int X, int Y, int Width, int Height)[] NoStrips = Array.Empty<(int, int, int, int)>();

	private (int X, int Y, int Width, int Height)[] strips = NoStrips;

	/// <summary>How near an edge counts as a pull on that edge, in window units.</summary>
	public int ResizeBorder { get; set; } = 5;

	/// <summary>Restores the system window frame, for when our own caption couldn't be built</summary>
	public void GiveBackTheFrame()
	{
		if ( Handle == IntPtr.Zero ) return;

		DragStrip = default;

		SDL_SetWindowHitTest( Handle, IntPtr.Zero, IntPtr.Zero );
		framed.TryRemove( Handle, out _ );

		SDL_SetWindowBordered( Handle, true );
	}

	private unsafe void InstallHitTest()
	{
		framed[Handle] = this;

		SDL_SetWindowHitTest( Handle,
			(IntPtr)(delegate* unmanaged[Cdecl]< IntPtr, SdlPoint*, IntPtr, int >)&HitTest,
			IntPtr.Zero );
	}

	[UnmanagedCallersOnly( CallConvs = new[] { typeof( System.Runtime.CompilerServices.CallConvCdecl ) } )]
	private static unsafe int HitTest( IntPtr handle, SdlPoint* area, IntPtr data )
	{
		//Nothing here may throw: it is called from inside SDL, and an exception crossing back out of managed code there takes the process with it.
		try
		{
			if ( area == null ) return HitNormal;
			if ( !framed.TryGetValue( handle, out var window ) ) return HitNormal;

			return window.WhatIsAt( area->X, area->Y );
		}
		catch
		{
			return HitNormal;
		}
	}

	private int WhatIsAt( int x, int y )
	{
		var (width, height) = Size;

		//A maximized window has no edges to pull - the ones it appears to have are the screen's - and dragging it is the window system's own gesture.
		if ( width > 0 && height > 0 && ResizeBorder > 0 && !IsMaximized )
		{
			var left = x <= ResizeBorder;
			var right = x >= width - ResizeBorder;
			var top = y <= ResizeBorder;
			var bottom = y >= height - ResizeBorder;

			if ( top && left ) return HitResizeTopLeft;
			if ( top && right ) return HitResizeTopRight;
			if ( bottom && left ) return HitResizeBottomLeft;
			if ( bottom && right ) return HitResizeBottomRight;

			if ( left ) return HitResizeLeft;
			if ( right ) return HitResizeRight;
			if ( top ) return HitResizeTop;
			if ( bottom ) return HitResizeBottom;
		}

		//A window that carries itself takes the caption's pull as its own - see CoreWindow.Carry
		if ( CarriesNow ) return HitNormal;

		return InStrip( x, y ) ? HitDraggable : HitNormal;
	}

	//Whether a point is on the caption's free strip
	private bool InStrip( int x, int y )
	{
		//Read whole: the setter swaps the array rather than editing it.
		var here = strips;

		foreach ( var strip in here )
		{
			if ( strip.Width <= 0 || strip.Height <= 0 ) continue;

			if ( x < strip.X || x >= strip.X + strip.Width ) continue;
			if ( y < strip.Y || y >= strip.Y + strip.Height ) continue;

			return true;
		}

		return false;
	}

	private static void SaidDriver()
	{
		if ( saidDriver ) return;
		saidDriver = true;

		new Rinten.Diagnostics.Logger( "Ui" ).Info( $"window system: {Driver ?? "none"}" );
	}

	/// <summary>Window system name (wayland, x11, windows, cocoa), for features some desktops refuse</summary>
	public static string WindowSystem => Driver;

	//What the window system is called, once there is one.
	private static string Driver
	{
		get
		{
			var name = SDL_GetCurrentVideoDriver();

			return name == IntPtr.Zero ? null : Marshal.PtrToStringUTF8( name );
		}
	}

	//Whether new windows show a platform fill before first draw; Wayland shows nothing until a commit
	private static bool PaintsEmptyWindows => Driver != "wayland";

	/// <summary>Whether a window may say where it goes. Wayland places its own.</summary>
	public static bool PlacesWindows => Driver != "wayland";

	/// <summary>Opens a window, or returns null and leaves the reason in <see cref="LastError"/>.</summary>
	public static CoreWindow Create( string title, int width, int height, bool hidden = false,
		bool utility = false, bool noRedirection = false, bool borderless = false,
		bool deferShow = false, bool transparent = false )
	{
		if ( OperatingSystem.IsBrowser() )
		{
			var canvas = new CoreWindow
			{
				Handle = new IntPtr( 1 ),
				WindowKind = Kind.Canvas,
				NativeWindow = 0,
				Adopted = true,
				canvasPixels = ( Math.Max( width, 1 ), Math.Max( height, 1 ) ),
			};
			live.Add( canvas );
			return canvas;
		}

		//The click that brings a window to the front is a click the window should still get. Without this the first press on an unfocused
		//window is eaten by the window system, and every editor window starts unfocused - so the first press on each of them was going nowhere.
		SDL_SetHint( "SDL_MOUSE_FOCUS_CLICKTHROUGH", "1" );

		//Disable libdecor: our windows draw their own captions, and its GTK plugin often fails noisily
		SDL_SetHint( "SDL_VIDEO_WAYLAND_ALLOW_LIBDECOR", "0" );

		PreferWayland();
		AppId();

		//SDL_InitSubSystem rather than SDL_Init: something else may already have SDL up, and this counts a reference rather than starting again.
		if ( !SDL_InitSubSystem( SDL_INIT_VIDEO ) ) return null;

		SaidDriver();

		//Here because this is where SDL is known to be up, and because a pad plugged in before the window exists has
		//to be found as well as one plugged in after. Costs nothing and answers nothing on a machine with no pads.
		CoreInput.StartGamepads();

		//A Vulkan utility window on Wayland has taken mutter down with it, and the session goes with the compositor.
		//Here rather than at the callers: this is the first moment the driver is known rather than guessed at.
		if ( utility && Driver == "wayland" )
		{
			utility = false;
		}

		var flags = ( OperatingSystem.IsBrowser() ? 0ul : SDL_WINDOW_VULKAN ) | (utility
			? SDL_WINDOW_BORDERLESS | SDL_WINDOW_ALWAYS_ON_TOP | SDL_WINDOW_UTILITY | SDL_WINDOW_NOT_FOCUSABLE
			: SDL_WINDOW_RESIZABLE | ( borderless ? SDL_WINDOW_BORDERLESS : 0ul ));

		if ( hidden || ( deferShow && PaintsEmptyWindows ) ) flags |= SDL_WINDOW_HIDDEN;

		if ( transparent ) flags |= SDL_WINDOW_TRANSPARENT;

		//A window that will be a sheet is made by hand first, so it can be made without a picture of its client
		//area - see WithoutRedirection. SDL is then handed the finished window and goes on as if it had made it.
		var handle = noRedirection
			? WithoutRedirection( title, width, height, flags )
			: IntPtr.Zero;

		//Whether the platform window belongs to us or to SDL, which is what says who has to take it away again.
		var ours = handle != IntPtr.Zero;

		handle = ours ? handle : SDL_CreateWindow( title, width, height, flags );

		if ( handle == IntPtr.Zero ) return null;

		var window = new CoreWindow { Handle = handle, madeByUs = ours };

		if ( !window.ReadNativeHandles() )
		{
			SDL_DestroyWindow( handle );
			return null;
		}

		//A window with no frame of its own has nothing to be dragged or resized by until we say which of its pixels do that.
		if ( borderless ) window.InstallHitTest();

		//On the list of windows the pump sorts events between.
		live.Add( window );

		return window;
	}

	//Creates a Win32 window with WS_EX_NOREDIRECTIONBITMAP for sheets and hands it to SDL
	//The style only works at creation; returns zero on failure so the caller makes a normal window
	private static IntPtr WithoutRedirection( string title, int width, int height, ulong flags )
	{
		if ( !OperatingSystem.IsWindows() ) return IntPtr.Zero;

		try
		{
			if ( !RegisterHostClass() ) return IntPtr.Zero;

			//The same window SDL would have made, in the same shape. Which shape that is comes from the flags it was asked for: a bar with no frame is a popup,
			//and one made overlapped instead wears a title bar and square corners it never asked for - and is that much taller than the size it was given.
			var borderless = ( flags & SDL_WINDOW_BORDERLESS ) != 0;

			var style = borderless ? WS_POPUP : WS_OVERLAPPEDWINDOW;

			var exStyle = (uint)WS_EX_NOREDIRECTIONBITMAP;

			if ( ( flags & SDL_WINDOW_ALWAYS_ON_TOP ) != 0 ) exStyle |= WS_EX_TOPMOST;
			if ( ( flags & SDL_WINDOW_UTILITY ) != 0 ) exStyle |= WS_EX_TOOLWINDOW;
			if ( ( flags & SDL_WINDOW_NOT_FOCUSABLE ) != 0 ) exStyle |= WS_EX_NOACTIVATE;

			//The size asked for is the inside of the window; the window itself is that plus whatever frame the style carries, which for a popup is nothing at all.
			var rect = new WindowRect { Left = 0, Top = 0, Right = width, Bottom = height };

			AdjustWindowRectEx( ref rect, style, false, exStyle );

			var hwnd = CreateWindowExW( exStyle, HostClass, title, style,
				CW_USEDEFAULT, CW_USEDEFAULT, rect.Right - rect.Left, rect.Bottom - rect.Top,
				IntPtr.Zero, IntPtr.Zero, GetModuleHandleW( null ), IntPtr.Zero );

			if ( hwnd == IntPtr.Zero )
			{
				log.Warning( "a window without a client picture could not be made - the ordinary one it is" );
				return IntPtr.Zero;
			}

			//And into SDL, which from here on treats it as any other window of its own: events, sizing, the Vulkan surface, all of it.
			var properties = SDL_CreateProperties();

			if ( properties == 0 )
			{
				DestroyWindow( hwnd );
				return IntPtr.Zero;
			}

			SDL_SetPointerProperty( properties, "SDL.window.create.win32.hwnd", hwnd );
			SDL_SetNumberProperty( properties, "SDL.window.create.flags", (long)flags );
			SDL_SetStringProperty( properties, "SDL.window.create.title", title );

			var handle = SDL_CreateWindowWithProperties( properties );

			SDL_DestroyProperties( properties );

			if ( handle == IntPtr.Zero )
			{
				DestroyWindow( hwnd );

				log.Warning( $"the window system would not adopt a window of ours: {LastError}" );

				return IntPtr.Zero;
			}

			log.Info( $"window '{title}': made without a client picture on {hwnd:X}" );

			return handle;
		}
		catch ( Exception e )
		{
			log.Warning( $"a window without a client picture could not be made: {e.Message}" );

			return IntPtr.Zero;
		}
	}

	//What this file says out loud.
	private static readonly Rinten.Diagnostics.Logger log = new( "Ui" );

	//Whether the class those windows are made from is registered.
	private static bool hostClassRegistered;

	private const string HostClass = "RintenSheetWindow";

	//Registers our window class: no background brush, DefWindowProcW as the procedure (SDL wraps it)
	private static bool RegisterHostClass()
	{
		if ( hostClassRegistered ) return true;

		var user32 = GetModuleHandleW( "user32.dll" );

		if ( user32 == IntPtr.Zero ) return false;

		var procedure = GetProcAddress( user32, "DefWindowProcW" );

		if ( procedure == IntPtr.Zero ) return false;

		var wanted = new WindowClass
		{
			Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WindowClass>(),
			Style = CS_HREDRAW | CS_VREDRAW,
			Procedure = procedure,
			Instance = GetModuleHandleW( null ),
			Cursor = LoadCursorW( IntPtr.Zero, (IntPtr)IDC_ARROW ),

			//None. A window that fills its own client area is a window with a slab of colour over the blur.
			Background = IntPtr.Zero,

			ClassName = HostClass,
		};

		hostClassRegistered = RegisterClassExW( ref wanted ) != 0;

		if ( !hostClassRegistered )
			log.Warning( "the class for a window without a client picture could not be registered" );

		return hostClassRegistered;
	}

	//A rectangle as the window system lays one out: four edges.
	[System.Runtime.InteropServices.StructLayout( System.Runtime.InteropServices.LayoutKind.Sequential )]
	private struct WindowRect
	{
		public int Left, Top, Right, Bottom;
	}

	private const int CW_USEDEFAULT = unchecked( (int)0x80000000 );
	private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
	private const uint WS_POPUP = 0x80000000;
	private const uint WS_EX_TOPMOST = 0x00000008;
	private const uint WS_EX_TOOLWINDOW = 0x00000080;
	private const uint WS_EX_NOACTIVATE = 0x08000000;
	private const uint CS_HREDRAW = 0x0002;
	private const uint CS_VREDRAW = 0x0001;
	private const int IDC_ARROW = 32512;

	[System.Runtime.InteropServices.StructLayout( System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode )]
	private struct WindowClass
	{
		public uint Size;
		public uint Style;
		public IntPtr Procedure;
		public int ClassExtra;
		public int WindowExtra;
		public IntPtr Instance;
		public IntPtr Icon;
		public IntPtr Cursor;
		public IntPtr Background;
		public string MenuName;
		public string ClassName;
		public IntPtr SmallIcon;
	}

	[System.Runtime.InteropServices.DllImport( "user32.dll", EntryPoint = "RegisterClassExW", CharSet = System.Runtime.InteropServices.CharSet.Unicode )]
	private static extern ushort RegisterClassExW( ref WindowClass wanted );

	[System.Runtime.InteropServices.DllImport( "user32.dll", EntryPoint = "CreateWindowExW", CharSet = System.Runtime.InteropServices.CharSet.Unicode )]
	private static extern IntPtr CreateWindowExW( uint exStyle, string className, string title, uint style,
		int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param );

	[System.Runtime.InteropServices.DllImport( "user32.dll" )]
	private static extern bool DestroyWindow( IntPtr window );

	[System.Runtime.InteropServices.DllImport( "user32.dll" )]
	private static extern bool AdjustWindowRectEx( ref WindowRect rect, uint style, bool menu, uint exStyle );

	[System.Runtime.InteropServices.DllImport( "user32.dll", EntryPoint = "LoadCursorW" )]
	private static extern IntPtr LoadCursorW( IntPtr instance, IntPtr name );

	[System.Runtime.InteropServices.DllImport( "kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = System.Runtime.InteropServices.CharSet.Unicode )]
	private static extern IntPtr GetModuleHandleW( string name );

	[System.Runtime.InteropServices.DllImport( "kernel32.dll" )]
	private static extern IntPtr GetProcAddress( IntPtr module, [System.Runtime.InteropServices.MarshalAs( System.Runtime.InteropServices.UnmanagedType.LPStr )] string name );

	[LibraryImport( SDL, EntryPoint = "SDL_CreateProperties" )]
	private static partial uint SDL_CreateProperties();

	[LibraryImport( SDL, EntryPoint = "SDL_DestroyProperties" )]
	private static partial void SDL_DestroyProperties( uint properties );

	[LibraryImport( SDL, EntryPoint = "SDL_SetPointerProperty", StringMarshalling = StringMarshalling.Utf8 )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_SetPointerProperty( uint properties, string name, IntPtr value );

	[LibraryImport( SDL, EntryPoint = "SDL_SetNumberProperty", StringMarshalling = StringMarshalling.Utf8 )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_SetNumberProperty( uint properties, string name, long value );

	[LibraryImport( SDL, EntryPoint = "SDL_SetStringProperty", StringMarshalling = StringMarshalling.Utf8 )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_SetStringProperty( uint properties, string name, string value );

	[LibraryImport( SDL, EntryPoint = "SDL_CreateWindowWithProperties" )]
	private static partial IntPtr SDL_CreateWindowWithProperties( uint properties );

	//Digs the platform's own handles out of SDL, which is the only part of this that has to know which platform it is on.
	private bool ReadNativeHandles()
	{
		var properties = SDL_GetWindowProperties( Handle );
		if ( properties == 0 ) return false;

		if ( OperatingSystem.IsWindows() )
		{
			var hwnd = SDL_GetPointerProperty( properties, "SDL.window.win32.hwnd", IntPtr.Zero );
			if ( hwnd == IntPtr.Zero ) return false;

			WindowKind = Kind.Win32;
			NativeWindow = (ulong)hwnd;
			return true;
		}

		if ( OperatingSystem.IsAndroid() )
		{
			var native = SDL_GetPointerProperty( properties, "SDL.window.android.window", IntPtr.Zero );
			if ( native == IntPtr.Zero ) return false;

			WindowKind = Kind.Android;
			NativeWindow = (ulong)native;
			return true;
		}

		if ( OperatingSystem.IsMacOS() )
		{
			var layer = SDL_GetPointerProperty( properties, "SDL.window.cocoa.metal_layer", IntPtr.Zero );
			if ( layer == IntPtr.Zero ) return false;

			WindowKind = Kind.Metal;
			NativeWindow = (ulong)layer;
			return true;
		}

		if ( OperatingSystem.IsBrowser() )
		{
			WindowKind = Kind.Canvas;
			NativeWindow = 0;
			return true;
		}

		//Linux, where the same build may be running under either. Which one is not a compile-time question, so ask the window that exists.
		var waylandSurface = SDL_GetPointerProperty( properties, "SDL.window.wayland.surface", IntPtr.Zero );

		if ( waylandSurface != IntPtr.Zero )
		{
			WindowKind = Kind.Wayland;
			NativeWindow = (ulong)waylandSurface;
			NativeDisplay = (ulong)SDL_GetPointerProperty( properties, "SDL.window.wayland.display", IntPtr.Zero );
			return true;
		}

		//X11's window is an integer rather than a pointer - an XID, which is a server-side name and not an address in this process at all.
		var x11Window = SDL_GetNumberProperty( properties, "SDL.window.x11.window", 0 );

		if ( x11Window != 0 )
		{
			WindowKind = Kind.X11;
			NativeWindow = (ulong)x11Window;
			NativeDisplay = (ulong)SDL_GetPointerProperty( properties, "SDL.window.x11.display", IntPtr.Zero );
			return true;
		}

		return false;
	}

	/// <summary>Takes a window the platform already made, which is how a phone gives one.</summary>
	public static CoreWindow Adopt( Kind kind, ulong window, ulong display, int width, int height )
	{
		if ( window == 0 ) return null;

		var adopted = new CoreWindow
		{
			Handle = (IntPtr)window,
			WindowKind = kind,
			NativeWindow = window,
			NativeDisplay = display,
			Adopted = true,
			canvasPixels = ( Math.Max( width, 1 ), Math.Max( height, 1 ) ),
		};

		live.Add( adopted );

		return adopted;
	}

	/// <summary>Window size in pixels from just its handle</summary>
	public static (int Width, int Height) SizeOf( IntPtr window )
	{
		if ( window == IntPtr.Zero ) return (0, 0);

		foreach ( var one in live )
		{
			if ( one.Adopted && one.Handle == window ) return one.CanvasSize();
		}

		if ( OperatingSystem.IsBrowser() ) return (0, 0);

		return SDL_GetWindowSizeInPixels( window, out var width, out var height )
			? (width, height)
			: (0, 0);
	}

	public bool SetSize( int width, int height )
	{
		if ( Handle == IntPtr.Zero ) return false;
		if ( Adopted )
		{
			canvasPixels = ( Math.Max( width, 1 ), Math.Max( height, 1 ) );
			return true;
		}
		if ( !SDL_SetWindowSize( Handle, width, height ) ) return false;

		//A window manager may decline - a tiling one will - so this says the request has been dealt with, not that the size is what was asked for.
		SDL_SyncWindow( Handle );
		return true;
	}

	//The corners. Also the platform's: a window is a rectangle of pixels the desktop
	//composes, and the only thing that can take the corners off it is the desktop.

	//Which corners a window wants - see RoundCorners. The import itself is further down, with the
	//other window attributes the caption sets - one declaration of it, since it is the same call.
	private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

	private const int DWMWCP_DONOTROUND = 1;
	private const int DWMWCP_ROUND = 2;

	/// <summary>Asks the desktop to round the window's corners.</summary>
	public bool RoundCorners( bool round = true )
	{
		if ( !OperatingSystem.IsWindows() ) return false;
		if ( NativeWindow == 0 ) return false;

		var preference = round ? DWMWCP_ROUND : DWMWCP_DONOTROUND;

		try
		{
			return DwmSetWindowAttribute( (IntPtr)NativeWindow, DWMWA_WINDOW_CORNER_PREFERENCE,
				ref preference, sizeof( int ) ) >= 0;
		}
		catch ( System.Exception )
		{
			//No dwmapi, which is a Windows without a desktop compositor. The window is square and that is the end of it.
			return false;
		}
	}

	//A window made of glass. The platform's, not ours: what is behind a window belongs to the desktop, and no amount of drawing inside one can see it.

	[System.Runtime.InteropServices.DllImport( "gdi32.dll" )]
	private static extern IntPtr CreateRoundRectRgn( int left, int top, int right, int bottom, int wide, int tall );

	[System.Runtime.InteropServices.DllImport( "user32.dll" )]
	private static extern int SetWindowRgn( IntPtr window, IntPtr region, [MarshalAs( UnmanagedType.Bool )] bool redraw );

	[System.Runtime.InteropServices.DllImport( "user32.dll" )]
	private static extern int GetWindowLongW( IntPtr window, int index );

	[System.Runtime.InteropServices.DllImport( "user32.dll" )]
	private static extern int SetWindowLongW( IntPtr window, int index, int value );

	[System.Runtime.InteropServices.DllImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static extern bool SetLayeredWindowAttributes( IntPtr window, uint key, byte alpha, uint flags );

	private const int GWL_EXSTYLE = -20;
	private const int WS_EX_LAYERED = 0x00080000;
	private const uint LWA_ALPHA = 0x00000002;

	/// <summary>Asks the desktop to blur behind the window, without thinning the window itself.</summary>
	public void MakeSheet( float radius )
	{
		if ( Handle == IntPtr.Zero ) return;
		if ( !OperatingSystem.IsWindows() ) return;
		if ( NativeWindow == 0 ) return;

		var hwnd = (IntPtr)NativeWindow;

		var (width, height) = SizeInPixels;

		if ( width > 0 && height > 0 && radius > 0.0f )
		{
			var corners = (int)( radius * 2.0f );
			var region = CreateRoundRectRgn( 0, 0, width + 1, height + 1, corners, corners );

			if ( region != IntPtr.Zero ) SetWindowRgn( hwnd, region, true );
		}

		//The caption can't be glass and DWM paints a colourless one black, so give it dark mode instead
		var on = 1;
		DwmSetWindowAttribute( hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof( int ) );

		//COLORREF: blue first, so this is rgb(45, 45, 50).
		var caption = 0x00322D2D;
		DwmSetWindowAttribute( hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof( int ) );
		DwmSetWindowAttribute( hwnd, DWMWA_BORDER_COLOR, ref caption, sizeof( int ) );

		var text = 0x00D0D0D0;
		DwmSetWindowAttribute( hwnd, DWMWA_TEXT_COLOR, ref text, sizeof( int ) );

		//Accent policy blur, not a backdrop material, which drops to a flat fill when inactive
		Blur( hwnd );

		//Remove the class background brush, or Windows paints a white fill over the sheet's blur
		var had = SetClassLongPtr( hwnd, GCLP_HBRBACKGROUND, IntPtr.Zero );

		new Rinten.Diagnostics.Logger( "Ui" ).Info(
			$"sheet {hwnd:X}: window class brush was {had:X}" );

		//A fill that arrived before any of this ran is cleared by asking for the frame again. A window made by us
		//has no such fill to begin with - it has no picture of its client area at all - and this costs nothing there.
		SetWindowPos( hwnd, IntPtr.Zero, 0, 0, 0, 0,
			SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE );
	}

	//A window with no picture of its client area for the desktop to keep.
	private const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;

	//The brush a window's class fills its client area with.
	private const int GCLP_HBRBACKGROUND = -10;

	private const uint SWP_NOSIZE = 0x0001;
	private const uint SWP_NOMOVE = 0x0002;
	private const uint SWP_NOZORDER = 0x0004;
	private const uint SWP_NOACTIVATE = 0x0010;
	private const uint SWP_FRAMECHANGED = 0x0020;

	[System.Runtime.InteropServices.DllImport( "user32.dll", EntryPoint = "SetClassLongPtrW" )]
	private static extern IntPtr SetClassLongPtr( IntPtr window, int index, IntPtr value );

	[System.Runtime.InteropServices.DllImport( "user32.dll" )]
	private static extern bool SetWindowPos( IntPtr window, IntPtr after,
		int x, int y, int width, int height, uint flags );

	//Draw the caption dark rather than light.
	private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

	//The line round the window.
	private const int DWMWA_BORDER_COLOR = 34;

	//The bar along the top.
	private const int DWMWA_CAPTION_COLOR = 35;

	//The title written on it.
	private const int DWMWA_TEXT_COLOR = 36;


	[System.Runtime.InteropServices.DllImport( "dwmapi.dll" )]
	private static extern int DwmSetWindowAttribute( IntPtr window, int attribute,
		ref int value, int size );

	/// <summary>Rounds the window's corners and lets what is behind it show through.</summary>
	public void MakeGlass( float opacity, float radius )
	{
		if ( Handle == IntPtr.Zero ) return;
		if ( !OperatingSystem.IsWindows() ) return;
		if ( NativeWindow == 0 ) return;

		var hwnd = (IntPtr)NativeWindow;

		var (width, height) = SizeInPixels;

		if ( width > 0 && height > 0 && radius > 0.0f )
		{
			//The region is the window's now; it is not ours to free.
			var corners = (int)( radius * 2.0f );
			var region = CreateRoundRectRgn( 0, 0, width + 1, height + 1, corners, corners );

			if ( region != IntPtr.Zero ) SetWindowRgn( hwnd, region, true );
		}

		//See-through is disabled (GlassEnabled); the code below is kept as the only working recipe
		if ( !CoreUiWindow.GlassEnabled ) return;

		if ( opacity >= 1.0f ) return;

		var style = GetWindowLongW( hwnd, GWL_EXSTYLE );

		SetWindowLongW( hwnd, GWL_EXSTYLE, style | WS_EX_LAYERED );
		SetLayeredWindowAttributes( hwnd, 0, (byte)( Math.Clamp( opacity, 0.0f, 1.0f ) * 255.0f ), LWA_ALPHA );

		//A colour key doesn't work with the accent blur (the key is ignored); don't retry without dropping blur
		Blur( hwnd );
	}

	[System.Runtime.InteropServices.StructLayout( System.Runtime.InteropServices.LayoutKind.Sequential )]
	private struct AccentPolicy
	{
		public int AccentState;
		public int AccentFlags;
		public int GradientColour;
		public int AnimationId;
	}

	[System.Runtime.InteropServices.StructLayout( System.Runtime.InteropServices.LayoutKind.Sequential )]
	private struct CompositionAttributeData
	{
		public int Attribute;
		public IntPtr Data;
		public uint Size;
	}

	[System.Runtime.InteropServices.DllImport( "user32.dll" )]
	private static extern int SetWindowCompositionAttribute( IntPtr window, ref CompositionAttributeData data );

	//Compositor blur behind the window via the undocumented accent policy; no-op where unsupported
	private static void Blur( IntPtr hwnd )
	{
		const int AccentPolicyAttribute = 19;
		const int EnableBlurBehind = 3;
		const int EnableAcrylic = 4;

		//Windows 11 stopped honouring the plain blur: a window that asks for it is given nothing, and the desktop shows through sharp
		//- see-through without the glass. The acrylic one took its place and is the only blur behind a window that still works there.
		var acrylic = OperatingSystem.IsWindowsVersionAtLeast( 10, 0, 22000 );

		var policy = new AccentPolicy
		{
			AccentState = acrylic ? EnableAcrylic : EnableBlurBehind,

			//Acrylic mixes this over what it blurred, and without one it mixes black. Written the way the desktop
			//wants it - alpha, blue, green, red - and kept faint, because the window is nearly solid already.
			GradientColour = acrylic ? unchecked( (int)0x30201C18 ) : 0,
		};

		var size = System.Runtime.InteropServices.Marshal.SizeOf<AccentPolicy>();
		var memory = System.Runtime.InteropServices.Marshal.AllocHGlobal( size );

		try
		{
			System.Runtime.InteropServices.Marshal.StructureToPtr( policy, memory, false );

			var data = new CompositionAttributeData
			{
				Attribute = AccentPolicyAttribute,
				Data = memory,
				Size = (uint)size,
			};

			//Said, because a window that did not get its blur looks exactly like one that got it
			//and had something opaque drawn over it, and the two are fixed in different places.
			var took = SetWindowCompositionAttribute( hwnd, ref data );

			new Rinten.Diagnostics.Logger( "Ui" ).Info(
				$"blur on {hwnd:X}: {( took != 0 ? "taken" : "refused" )}" +
				$" ({( acrylic ? "acrylic" : "plain" )})" );
		}
		catch ( EntryPointNotFoundException )
		{
			//An older Windows than the one this was written for.
		}
		catch ( DllNotFoundException )
		{
		}
		finally
		{
			System.Runtime.InteropServices.Marshal.FreeHGlobal( memory );
		}
	}

	/// <summary>Puts a hidden window on the screen, in front, and focused.</summary>
	public void Show()
	{
		if ( Handle == IntPtr.Zero || Adopted ) return;

		SDL_ShowWindow( Handle );

		//In front and taking the keyboard: a window that opens behind everything else and never takes the focus is one the user has to click twice to use.
		SDL_RaiseWindow( Handle );
	}

	/// <summary>Takes it off the screen without closing it.</summary>
	public void Hide()
	{
		if ( Handle != IntPtr.Zero && !Adopted ) SDL_HideWindow( Handle );
	}

	//Where the window is on the desktop, and whether it is filling the screen or folded away. The
	//editor saves both when it closes and puts the window back where it was when it opens again.

	private const ulong FlagMinimized = 0x40;
	private const ulong FlagFullscreen = 0x1;
	private const ulong FlagMaximized = 0x80;

	/// <summary>The top left corner of the window, in desktop pixels.</summary>
	public (int X, int Y) Position
	{
		get
		{
			if ( Handle == IntPtr.Zero ) return (0, 0);

			return SDL_GetWindowPosition( Handle, out var x, out var y ) ? (x, y) : (0, 0);
		}

		set
		{
			if ( Handle == IntPtr.Zero ) return;

			SDL_SetWindowPosition( Handle, value.X, value.Y );
		}
	}

	public bool IsMaximized => Handle != IntPtr.Zero && (SDL_GetWindowFlags( Handle ) & FlagMaximized) != 0;
	public bool IsMinimized => Handle != IntPtr.Zero && (SDL_GetWindowFlags( Handle ) & FlagMinimized) != 0;

	/// <summary>Fills the screen it is on.</summary>
	public void Maximize()
	{
		if ( Handle != IntPtr.Zero ) SDL_MaximizeWindow( Handle );
	}

	/// <summary>Folds away to the taskbar.</summary>
	public void Minimize()
	{
		if ( Handle != IntPtr.Zero ) SDL_MinimizeWindow( Handle );
	}

	/// <summary>Back to the size and place it had before either of those.</summary>
	public void Restore()
	{
		if ( Handle != IntPtr.Zero ) SDL_RestoreWindow( Handle );
	}

	//A page has no window of ours to ask, and the browser's own full screen is the whole of it there - see WebPage, and native.js which performs it.
	/// <summary>Whether the window has the screen to itself, bars and all.</summary>
	public bool IsFullscreen => OperatingSystem.IsBrowser()
		? ( WebPage.Fullscreen?.Invoke() ?? 0 ) != 0
		: Handle != IntPtr.Zero && ( SDL_GetWindowFlags( Handle ) & FlagFullscreen ) != 0;

	/// <summary>True fullscreen (unlike maximise, covers desktop bars); syncs so callers see the new size</summary>
	public void SetFullscreen( bool fullscreen )
	{
		//Browsers only allow fullscreen from a gesture, so this may take effect on the next click
		if ( OperatingSystem.IsBrowser() )
		{
			WebPage.SetFullscreen?.Invoke( fullscreen ? 1 : 0 );
			return;
		}

		if ( Handle == IntPtr.Zero ) return;

		SDL_SetWindowFullscreen( Handle, fullscreen );
		SDL_SyncWindow( Handle );
	}

	/// <summary>What the window's own bar says.</summary>
	public void SetTitle( string title )
	{
		if ( Handle != IntPtr.Zero && !Adopted ) SDL_SetWindowTitle( Handle, title ?? string.Empty );
	}

	//A window that is never pumped stops responding, and on Windows the compositor eventually draws it grey. This is not the engine's event loop - it throws the events away - only enough to keep the window alive while something else is driving.
	/// <summary>Lets the window system get on with it.</summary>
	public static void Pump()
	{
		if ( HasSdl ) SDL_PumpEvents();
	}

	/// <summary>Whether the window system has asked this window to close - the button in the corner, or the application being quit.</summary>
	public bool CloseRequested { get; private set; }

	//A window that draws its own caption has no close box for the window system to tell us about - the button is a panel of ours, and this is how it says the same thing.
	/// <summary>Asks the window to close, exactly as its close box does.</summary>
	public void RequestClose() => CloseRequested = true;

	/// <summary>Takes the events waiting and gives each window its own.</summary>
	public void PollEvents( bool toGame = true )
	{
		RoutesToGame = toGame;

		PollAll();
	}

	/// <summary>Whether what happens in this window drives the game as well.</summary>
	public bool RoutesToGame { get; set; } = true;

	//Every window of ours that is open, in the order they opened.
	private static readonly List<CoreWindow> live = new();

	//Drains the window system's queue and hands every event to whichever of our windows it happened in.


	public static void PollAll()
	{
		if ( OperatingSystem.IsBrowser() )
		{
			CoreInput.PumpWeb();
			return;
		}

		//Android draws into a surface the activity owns, and every event it would answer comes
		//from Java - put on a queue of the engine's by the activity, and read here once a frame.
		if ( !HasSdl )
		{
			CoreInput.PumpHost();
			return;
		}

		//SDL may commit the surface while handling events, so wait for any present in flight first
		CoreRenderDevice.FramesHandedOver();

		//The union is large and only its first field matters here. Sized generously on purpose: SDL writes the whole event whatever we read.
		Span<byte> raw = stackalloc byte[256];

		unsafe
		{
			fixed ( byte* buffer = raw )
			{
				while ( SDL_PollEvent( buffer ) )
				{
					Route( *(uint*)buffer, buffer );
				}
			}
		}

		PumpCarry();
	}

	//What the window system actually said, counted by kind. A window that does not answer the mouse is either not
	//being told about it or not recognising what it is told, and those look identical from anywhere further along.
	internal static int MotionEvents;
	internal static int ButtonEvents;
	internal static int UnownedEvents;

	//Timestamp of the last user input, so heavy work like full GCs can wait until the user is idle
	private static long lastInput = System.Diagnostics.Stopwatch.GetTimestamp();

	//Says the person at the machine just did something, for input that does not come through SDL - see lastInput.
	internal static void MarkInput()
		=> System.Threading.Volatile.Write( ref lastInput, System.Diagnostics.Stopwatch.GetTimestamp() );

	/// <summary>How long it has been since the person at the machine last touched it, in seconds - see lastInput.</summary>
	public static double SecondsSinceInput
		=> ( System.Diagnostics.Stopwatch.GetTimestamp() - System.Threading.Volatile.Read( ref lastInput ) ) /
			(double)System.Diagnostics.Stopwatch.Frequency;

	//One event, to whoever it is about.
	private static void Route( uint type, byte* buffer )
	{
		if ( type is >= SDL_EVENT_DROP_FILE and <= SDL_EVENT_DROP_POSITION && HearDrop( type, buffer ) ) return;

		if ( type == SDL_EVENT_MOUSE_MOTION ) MotionEvents++;
		if ( type is SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EVENT_MOUSE_BUTTON_UP ) ButtonEvents++;

		if ( type is SDL_EVENT_MOUSE_MOTION or SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EVENT_MOUSE_BUTTON_UP or
			SDL_EVENT_MOUSE_WHEEL or SDL_EVENT_KEY_DOWN or SDL_EVENT_KEY_UP or SDL_EVENT_TEXT_INPUT )
		{
			System.Threading.Volatile.Write( ref lastInput, System.Diagnostics.Stopwatch.GetTimestamp() );
		}

		//The application is going away, and every window with it.
		if ( type == SDL_EVENT_QUIT )
		{
			foreach ( var window in live ) window.CloseRequested = true;

			CoreInput.Dispatch( buffer );
			return;
		}

		//Anything that is not about a window - a gamepad being plugged in, the clipboard changing - is the application's, and the application is
		//the game. The number at sixteen bytes in is a window only for the events that are about one; on the rest it means something else entirely.
		if ( !IsWindowEvent( type ) )
		{
			CoreInput.Dispatch( buffer );
			return;
		}

		var owner = Find( *(uint*)(buffer + 16) );

		//Wayland in particular will send a key with no window on it when the compositor has not named one. The
		//keyboard still belongs to whoever has it, and dropping the event is how WASD never reached the editor.
		if ( owner is null && type is SDL_EVENT_KEY_DOWN or SDL_EVENT_KEY_UP or SDL_EVENT_TEXT_INPUT )
			owner = FindHandle( CoreInput.KeyboardWindow() );

		if ( owner is null ) UnownedEvents++;

		if ( type == SDL_EVENT_WINDOW_CLOSE_REQUESTED && owner is not null )
			owner.CloseRequested = true;

		//A game played inside an editor was given this window's keyboard and mouse. The window system taking the
		//window away takes them with it - otherwise alt-tabbing out of a game leaves an editor with no cursor.
		if ( type == SDL_EVENT_WINDOW_FOCUS_LOST && owner is not null
			&& owner.Handle == Engine.InputRouter.GameWindow )
		{
			Engine.InputRouter.ReleaseGameInput();
		}

		//The keyboard is the game's while it has the input; the mouse only while it hides the cursor. With a cursor on screen the two share the mouse.
		if ( owner is not null && owner.Handle == Engine.InputRouter.GameWindow
			&& IsPlayerEvent( type ) )
		{
			if ( !IsMouseEvent( type ) || Engine.InputRouter.GameHasMouse )
			{
				CoreInput.Dispatch( buffer );
				return;
			}

			owner.Read( type, buffer );

			//A shared mouse reaches the game only over the view it is played in.
			if ( Engine.InputRouter.PointerInGameView ) CoreInput.Dispatch( buffer );

			return;
		}

		owner?.Read( type, buffer );

		//A window we do not know about is the engine's own - a mixed build still makes one - and its events belong to the game.
		if ( owner is null || owner.RoutesToGame ) CoreInput.Dispatch( buffer );
	}

	//Whether an event is about a window, and so carries one's number where the rest carry a device or nothing. Whether an event is somebody playing
	//rather than the window system talking about the window: the keyboard and the mouse, which are the two a game and an interface have to share.
	private static bool IsPlayerEvent( uint type )
	{
		//Keyboard is the three hundreds, mouse the four hundreds.
		return type is >= 0x300 and < 0x500;
	}

	//Whether an event is the mouse rather than the keyboard.
	private static bool IsMouseEvent( uint type )
	{
		//Mouse is the four hundreds.
		return type is >= 0x400 and < 0x500;
	}

	private static bool IsWindowEvent( uint type )
	{
		//SDL groups its events by the hundred: display, window, keyboard, mouse, and so on upwards.
		return type is >= 0x200 and < 0x500;
	}

	//Live resize: Windows blocks our loop during an edge drag, so draw from an SDL event watch instead

	//Whether to draw from the watch while a window is being dragged.
	internal static readonly bool LiveResize = Rinten.Utility.CommandLine.HasSwitch( "-liveresize" );

	//Told which window the window system is busy resizing, as it happens.
	internal static Action<CoreWindow> Resizing;

	//Kept in a field of its own so the collector leaves it alone.
	private static EventWatch watch;

	private static bool watching;

	//Starts listening for the size events that arrive from inside a drag. Called once, by whoever opens the first window.
	internal static void WatchForLiveResize()
	{
		if ( watching || !LiveResize ) return;

		watching = true;
		watch = Watched;

		if ( !SDL_AddEventWatch( watch, IntPtr.Zero ) )
			log.Warning( $"-liveresize: the window system would not take a watch ({LastError})" );
		else
			log.Info( "-liveresize: drawing while a window is dragged" );
	}

	private static unsafe bool Watched( IntPtr userData, byte* buffer )
	{
		var type = *(uint*)buffer;

		//Exposed as well as resized: a window uncovered mid-drag has the same problem and
		//the same answer, and on some desktops it is the only one of the two that arrives.
		if ( type is not ( SDL_EVENT_WINDOW_EXPOSED or SDL_EVENT_WINDOW_RESIZED
			or SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED ) )
			return true;

		if ( Find( *(uint*)( buffer + 16 ) ) is { } owner )
			Resizing?.Invoke( owner );

		//The watch's answer is not read - it is a watch, not a filter.
		return true;
	}

	private const uint SDL_EVENT_WINDOW_EXPOSED = 0x204;
	private const uint SDL_EVENT_WINDOW_RESIZED = 0x206;
	private const uint SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED = 0x207;
	private const uint SDL_EVENT_WINDOW_MINIMIZED = 0x209;
	private const uint SDL_EVENT_WINDOW_MAXIMIZED = 0x20A;
	private const uint SDL_EVENT_WINDOW_RESTORED = 0x20B;

	[System.Runtime.InteropServices.UnmanagedFunctionPointer( System.Runtime.InteropServices.CallingConvention.Cdecl )]
	private unsafe delegate bool EventWatch( IntPtr userData, byte* buffer );

	[System.Runtime.InteropServices.DllImport( SDL, EntryPoint = "SDL_AddEventWatch" )]
	[return: System.Runtime.InteropServices.MarshalAs( System.Runtime.InteropServices.UnmanagedType.U1 )]
	private static extern bool SDL_AddEventWatch( EventWatch watch, IntPtr userData );

	private static CoreWindow Find( uint id )
	{
		if ( id == 0 ) return null;

		foreach ( var window in live )
		{
			if ( window.Handle != IntPtr.Zero && SDL_GetWindowID( window.Handle ) == id )
				return window;
		}

		return null;
	}

	private static CoreWindow FindHandle( IntPtr handle )
	{
		if ( handle == IntPtr.Zero ) return null;

		foreach ( var window in live )
		{
			if ( window.Handle == handle ) return window;
		}

		return null;
	}

	public void Dispose()
	{
		if ( Handle == IntPtr.Zero ) return;

		if ( ReferenceEquals( Pointed, this ) ) Pointed = null;

		live.Remove( this );
		framed.TryRemove( Handle, out _ );

		if ( !Adopted )
		{
			var ours = madeByUs ? (IntPtr)NativeWindow : IntPtr.Zero;

			SDL_DestroyWindow( Handle );

			if ( ours != IntPtr.Zero ) DestroyWindow( ours );
		}

		madeByUs = false;

		Handle = IntPtr.Zero;
		NativeWindow = 0;
		NativeDisplay = 0;
	}

	//Whether the platform window under SDL is one of ours to destroy.
	private bool madeByUs;
}
