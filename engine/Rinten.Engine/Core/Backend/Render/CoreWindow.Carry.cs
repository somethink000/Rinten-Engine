namespace Rinten;

//A window carried by its caption so another window of ours can take it - see CoreWaylandDrag, whose drag says which
//of our windows the carried one is over and which it was put down on
internal sealed unsafe partial class CoreWindow
{
	//How far a caption press is pulled before it is a carry, in window units
	private const float CarryStarts = 4.0f;

	/// <summary>Whether pulling this window's caption carries it as a drag another of our windows can take, where the desktop can.</summary>
	public bool CarriesItself
	{
		get => carriesItself;
		set
		{
			carriesItself = value;

			//What a carry needs, bound on SDL's own connection the first time a window wants it
			if ( value && WindowKind == Kind.Wayland ) CoreWaylandDrag.Start( (IntPtr)NativeDisplay );
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

	//What the drag said since the last frame
	private static void PumpCarry()
	{
		while ( CoreWaylandDrag.Next( out var report ) )
		{
			switch ( report.What )
			{
				case CoreWaylandDrag.Heard.Over:
					CarriedOver = OfSurface( report.Surface );
					CarriedAt = new Vector2( report.X, report.Y );
					break;

				case CoreWaylandDrag.Heard.Off:
					CarriedOver = null;
					break;

				case CoreWaylandDrag.Heard.Dropped:
					droppedOn = OfSurface( report.Surface );
					droppedAt = new Vector2( report.X, report.Y );
					break;

				case CoreWaylandDrag.Heard.Ended:
					EndCarry();
					break;
			}
		}
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

	//The window of ours a Wayland surface is
	private static CoreWindow OfSurface( IntPtr surface )
	{
		if ( surface == IntPtr.Zero ) return null;

		foreach ( var window in live )
		{
			if ( window.WindowKind == Kind.Wayland && (IntPtr)window.NativeWindow == surface ) return window;
		}

		return null;
	}
}
