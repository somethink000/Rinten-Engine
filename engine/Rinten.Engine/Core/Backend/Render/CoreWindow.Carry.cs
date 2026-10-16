using System.Runtime.InteropServices;

namespace Rinten;

//A window carried by its caption so another window of ours can take it - see CoreWaylandDrag. SDL's drop events
//say which of our windows the carried one is over and which it was put down on
internal sealed unsafe partial class CoreWindow
{
	//How far a caption press is pulled before it is a carry, in window units
	private const float CarryStarts = 4.0f;

	private const uint SDL_EVENT_DROP_FILE = 0x1000;
	private const uint SDL_EVENT_DROP_TEXT = 0x1001;
	private const uint SDL_EVENT_DROP_COMPLETE = 0x1003;
	private const uint SDL_EVENT_DROP_POSITION = 0x1004;

	/// <summary>Whether pulling this window's caption carries it as a drag another of our windows can take, where the desktop can.</summary>
	public bool CarriesItself
	{
		get => carriesItself;
		set
		{
			carriesItself = value;

			//What a carry needs, bound on SDL's own connection the first time a window wants it
			if ( value && WindowKind == Kind.Wayland )
				CoreWaylandDrag.Start( (IntPtr)NativeDisplay, SDL_GetPointerProperty( SDL_GetGlobalProperties(), "SDL.video.wayland.wl_data_device", IntPtr.Zero ) );
		}
	}

	private bool carriesItself;

	/// <summary>The window being carried, if one is.</summary>
	public static CoreWindow Carried { get; private set; }

	/// <summary>The window of ours the carried one is over, or null.</summary>
	public static CoreWindow CarriedOver { get; private set; }

	/// <summary>Where in that window, in its own units.</summary>
	public static Vector2 CarriedAt { get; private set; }

	/// <summary>A window has started being carried.</summary>
	public static System.Action<CoreWindow> CarryStarted;

	/// <summary>A carried window was put down: the window, the one of ours it was dropped on or null, and where in that one.</summary>
	public static System.Action<CoreWindow, CoreWindow, Vector2> CarryEnded;

	private static CoreWindow droppedOn;
	private static Vector2 droppedAt;

	private Vector2? carryPress;

	private bool CarriesNow => carriesItself && CoreWaylandDrag.Available;

	//The caption pressed and pulled, out of this window's own events
	private void FollowCaption( uint type, byte* buffer )
	{
		if ( !CarriesNow ) return;

		var at = new Vector2( *(float*)(buffer + 28), *(float*)(buffer + 32) );

		switch ( type )
		{
			case SDL_EVENT_MOUSE_BUTTON_DOWN:
				carryPress = *(buffer + 24) == 1 && InStrip( (int)at.x, (int)at.y ) ? at : null;
				break;

			case SDL_EVENT_MOUSE_BUTTON_UP:
				carryPress = null;
				break;

			case SDL_EVENT_MOUSE_MOTION:
				if ( carryPress is not { } pressed || at.Distance( pressed ) < CarryStarts ) break;

				carryPress = null;
				StartCarry( pressed );
				break;
		}
	}

	private void StartCarry( Vector2 grab )
	{
		if ( Carried is not null || WindowKind != Kind.Wayland ) return;

		var toplevel = SDL_GetPointerProperty( SDL_GetWindowProperties( Handle ), "SDL.window.wayland.xdg_toplevel", IntPtr.Zero );

		if ( !CoreWaylandDrag.Carry( (IntPtr)NativeWindow, toplevel, (int)grab.x, (int)grab.y ) ) return;

		Carried = this;
		CarriedOver = null;
		droppedOn = null;

		CarryStarted?.Invoke( this );
	}

	//The drag ends after the events that said where it was put down
	private static void PumpCarry()
	{
		if ( CoreWaylandDrag.Ended() ) EndCarry();
	}

	//SDL's drop events while a window is carried: over a window of ours, off it, or put down on it
	private static bool HearDrop( uint type, byte* buffer )
	{
		if ( Carried is null ) return false;

		var window = Find( *(uint*)(buffer + 16) );
		var at = new Vector2( *(float*)(buffer + 20), *(float*)(buffer + 24) );

		switch ( type )
		{
			case SDL_EVENT_DROP_POSITION:
				CarriedOver = window;
				CarriedAt = at;
				break;

			case SDL_EVENT_DROP_TEXT when Marshal.PtrToStringUTF8( *(IntPtr*)(buffer + 40) ) == CoreWaylandDrag.Mime:
				droppedOn = window;
				droppedAt = CarriedAt;
				break;

			case SDL_EVENT_DROP_COMPLETE:
				CarriedOver = null;
				break;
		}

		return true;
	}

	private static void EndCarry()
	{
		var carried = Carried;
		var on = droppedOn;

		Carried = null;
		CarriedOver = null;
		droppedOn = null;

		if ( carried is not null ) CarryEnded?.Invoke( carried, on, droppedAt );
	}
}
