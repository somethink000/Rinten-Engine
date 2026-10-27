using System;
using Rinten;
using Rinten.UI;

namespace Editor;

internal sealed class WidgetSurface : Panel
{
	/// <summary>The widget this is the front of.</summary>
	public Widget Widget { get; }

	private bool paintedOnce;
	private bool dirty = true;

	//The picture this panel wears, and everything about keeping it.
	private readonly PanelBake bake = new();

	//Where the panel was last put, so it is only moved when it moves.
	private Rect placed = new( float.NaN, float.NaN, float.NaN, float.NaN );

	public WidgetSurface( Widget widget )
	{
		Widget = widget;

		//Panel's own constructor has already asked TicksEveryFrameOfItsOwn, and at that moment there was no widget to ask about. Now there is.
		RefreshTicksEveryFrame();

		Style.Position = PositionMode.Absolute;
		Style.Left = 0;
		Style.Top = 0;

		Style.PointerEvents = PointerEvents.All;

		AddClasses( ClassesFor( widget.GetType() ) );

		EditorStyles.Apply( this, "WidgetPanels.scss" );
		EditorStyles.Apply( this, "InspectorSheet.scss" );

		//Stamped here, because a RealTimeSince that nobody has set reads as having happened at the
		//beginning of time - which would make the wait below expire before the widget's first frame.
		sinceMade = 0;
	}

	//Class names for a widget type (its own and its bases, lowercased), computed once per type
	private static string[] ClassesFor( Type type )
	{
		return classesByType.GetOrAdd( type, static made =>
		{
			var names = new List<string>( 6 );

			for ( var type = made; type is not null && type != typeof( object ); type = type.BaseType )
			{
				names.Add( type.Name.ToLowerInvariant() );
			}

			return names.ToArray();
		} );
	}

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string[]> classesByType = new();

	/// <summary>A field composes text; everything else wants the keys themselves. Without this the window asks for IME all the time, and on Wayland WASD never arrives as key-down.</summary>
	public override bool AcceptsImeInput =>
		Widget is LineEdit or TextEdit || Widget.WantsRawKeys || FocusedFieldAcceptsIme();

	bool FocusedFieldAcceptsIme()
	{
		var focus = InputFocus.Current;
		if ( !focus.IsValid() || focus == this ) return false;

		for ( var panel = focus; panel is not null; panel = panel.Parent )
		{
			if ( panel == this ) return focus.AcceptsImeInput;
		}

		return false;
	}

	//Which widget the pointer is on and which one is typed into. The editor asks both of these of itself - a tool wants to know
	//whether the mouse is over its viewport before it acts on a key - so they are kept here, where the events that decide them arrive.

	private static WidgetSurface hovered;
	private static WidgetSurface focused;

	//First hover claim per bubbling event, so the innermost widget counts as hovered, not the window
	private static PanelEvent claimedHover;

	/// <summary>The widget the pointer is over, if it is over one.</summary>
	public static Widget Hovered => hovered?.Widget;

	//Hovered widget's panel; walk down from here, since other windows keep stale hover
	internal static Panel HoveredSurface => hovered;

	/// <summary>The widget being typed into, if anything is.</summary>
	public static Widget Focused => focused?.Widget;

	/// <summary>Ask for the widget to be painted again before the next frame. Somebody marked this widget: paint it on the next tick, and make sure there is one. Everything in the editor that changes how a widget looks arrives here through <c>Widget.Update</c>.</summary>
	public void Invalidate()
	{
		dirty = true;

		RequestTick();
	}

	//Under -uitrace, periodically names widgets that repaint every frame

	//Either switch opens the breakdown - see TracingTick in Panel.cs, which reads the
	//same pair, so one flag on the command line lights up both halves of a UI frame.
	private static readonly bool tracing =
		Rinten.Utility.CommandLine.HasSwitch( "-uitrace" ) ||
		Rinten.Utility.CommandLine.HasSwitch( "-ticktrace" );

	//Profiler zone opened only under -uitrace, since these zones are costly in the per-widget walk
	private readonly ref struct Zoned
	{
		private readonly Rinten.Diagnostics.Performance.ScopeSection section;
		private readonly bool open;

		public Zoned( string name )
		{
			open = tracing;
			section = open ? Rinten.Diagnostics.Performance.Scope( name ) : default;
		}

		public void Dispose()
		{
			if ( open ) section.Dispose();
		}
	}

	private static Zoned Zone( string name ) => new( name );

	/// <summary>Tick a widget's surface only when something asks, not every frame.</summary>
	[ConVar( "ui_widget_tick_on_demand", Help = "Tick a widget's panel only when something asks for it; 0 polls every widget every frame, as it always did" )]
	public static bool TickOnDemand { get; set; } = true;

	protected override bool TicksEveryFrameOfItsOwn =>
		!TickOnDemand || Widget is null || PollsEveryFrame( Widget.GetType() );

	//The widget behind it, and whether it is polled or asked - see Panel.TickTypeName.
	protected internal override string TickTypeName =>
		Widget is null ? "WidgetSurface"
		: $"{Widget.GetType().Name}{( PollsEveryFrame( Widget.GetType() ) ? " (polled)" : " (asked)" )}";

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> pollingTypes = new();

	private static bool PollsEveryFrame( Type type ) => pollingTypes.GetOrAdd( type, static t =>
		PollsThrough( t, nameof( Widget.SyncSurfaceContent ) ) ||
		PollsThrough( t, "OnWidgetFrame" ) ||
		PollsThrough( t, "get_SurfaceTexture" ) );

	//Whether a hook forces the every-frame tick: it is overridden, and the class that wrote the override has not said its frames are asked for - see
	//FramesOnDemandAttribute. Asked of the most derived override, so a subclass that overrides again without saying so is polled, as it always was.
	private static bool PollsThrough( Type type, string method )
	{
		var found = type.GetMethod( method,
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
			System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null );

		if ( found is null || found.DeclaringType == typeof( Widget ) ) return false;

		return !found.DeclaringType.IsDefined( typeof( FramesOnDemandAttribute ), inherit: false );
	}

	//The box the layout last gave this panel, so a widget in a flex container is told to read it back only when it moved.
	private Rect laidOut = new( float.NaN, float.NaN, float.NaN, float.NaN );

	/// <summary>A layout has just placed this panel. If it placed it somewhere new, the widget behind it has to be told - which is the one thing in the tick below that nobody pushes, because it comes from the layout rather than from anybody touching the widget.</summary>
	public override void FinalLayout( Vector2 offset )
	{
		base.FinalLayout( offset );

		if ( Box.Rect == laidOut ) return;

		laidOut = Box.Rect;

		RequestTick();
	}

	/// <summary>Where the widget says it is, and what it looks like.</summary>
	public override void Tick()
	{
		base.Tick();

		if ( Widget is not { IsValid: true } ) return;

		//Arrange children now that this widget has its rectangle; parents tick before children
		using ( Zone( "widget arrange" ) )
		{
			Widget.Frame();
			Widget.ArrangeIfNeeded();
		}

		//A widget's frame can destroy widgets - closing a window destroys them all, this one included - and a deleted panel has no style left.
		if ( Widget is not { IsValid: true } || Style is null ) return;

		//In a flex container the widget is not placed by anybody - it is where the container put
		//it, and what it got is read back so the rest of the editor still knows where everything is.
		using ( Zone( "widget geometry" ) )
		{
			if ( Widget.InFlex )
			{
				var scale = ScaleFromScreen;
				var mine = Box.Rect;
				var around = Parent?.Box.Rect.Position ?? Vector2.Zero;

				Widget.TakeGeometry( (mine.Position - around) * scale, mine.Size * scale );
			}
			else
			{
				PushGeometryToPanel();
			}
		}

		var rect = new Rect( Widget.Position, Widget.Size );

		//Write styles only when changed; any assignment dirties the panel and re-resolves its cascade
		if ( disabled != !Widget.Enabled )
		{
			disabled = !Widget.Enabled;

			SetClass( "disabled", disabled );
		}

		var pointing = Widget.TransparentForMouseEvents ? PointerEvents.None : PointerEvents.All;

		if ( pointed != pointing )
		{
			pointed = pointing;

			Style.PointerEvents = pointing;
		}

		if ( Widget.Visible != IsVisibleSelf ) SetClass( "hidden", !Widget.Visible );

		//Named by widget type while tracing. "tick self: WidgetSurface" says the surfaces cost the frame; it does
		//not say which widget, nor whether the cost is keeping the panel in step with the widget or repainting it.
		using ( Zone( tracing ? $"widget sync: {Widget.GetType().Name}" : "widget sync" ) )
		{
			Widget.SyncSurfaceContent();
		}

		//Clear dirty before painting, so a fade's repaint request from inside OnPaint is kept
		var asked = dirty;
		dirty = false;

		using ( Zone( tracing ? $"widget paint: {Widget.GetType().Name}" : "widget paint" ) )
		{
			//A widget the world is drawn under wears no picture at all: the window draws the world in this panel's place, and anything worn here would be
			//drawn over it. Before the surface texture, because a scene view has one and this is the same scene view drawn the other way. See Panel.WearsWorld.
			if ( WearsWorld )
				ClearBakedBackground();
			else if ( Widget.SurfaceTexture is { IsValid: true } ready )
				WearLiveTexture( ready );
			else if ( Widget.UsesCanvasDraw )
				PaintCanvas( rect, asked );
			else
				ClearBakedBackground();
		}

		paintedOnce = true;

		Reveal();

		//Still coming up: the first paint is on a budget and the reveal is a fade, and neither of those is anybody's to ask for.
		if ( !revealed || Style?.Opacity is not null )
			RequestTick();
	}

	//Writes widget geometry onto the panel when it is not a flex item.
	internal void PushGeometryToPanel()
	{
		if ( Widget is not { IsValid: true } || Style is null || Widget.InFlex ) return;

		var placing = new Rect( Widget.Position, Widget.Size );

		if ( placing == placed ) return;

		placed = placing;

		Style.Position = PositionMode.Absolute;
		Style.Left = placing.Left;
		Style.Top = placing.Top;
		Style.Width = placing.Width;
		Style.Height = placing.Height;
	}

	/// <summary>The picture goes with the panel. It never used to: the bitmap and the texture baked from it were held in fields nothing let go of, so every widget the editor closed left both behind.</summary>
	public override void OnDeleted()
	{
		base.OnDeleted();

		bake.Dispose();
	}

	private void WearLiveTexture( Texture ready ) => bake.Wear( this, ready );

	//Drops a baked widget texture so panel children are what shows.
	private void ClearBakedBackground() => bake.Clear( this );

	//Draws the widget into the picture its panel wears, where it asked to be drawn or where the room it has changed shape.
	private void PaintCanvas( Rect rect, bool asked )
		=> bake.Paint( this, rect, asked, paintedOnce, PaintDelegate(), ScaleToScreen );

	//Paint delegate cached, since a method group allocates a new Func every time
	private Func<Bitmap, Rect, bool> PaintDelegate() => paintDelegate ??= Widget.PaintInto;

	private Func<Bitmap, Rect, bool> paintDelegate;

	//Whether this widget has ever been shown.
	private bool revealed;

	//What the tick last wrote, so it does not write it again.
	private bool disabled;
	private PointerEvents? pointed;

	private RealTimeSince sinceRevealed;
	private RealTimeSince sinceMade;


	//Whether a widget appears rather than pops. -nowidgetfade is the control half of it.
	private static bool UseFades =>
		EditorPreferences.UiWidgetFadeEnabled && !Rinten.Utility.CommandLine.HasSwitch( "-nowidgetfade" );

	//Fade-in time for a new widget; just enough to hide popping, not a visible animation
	private const float FadeSeconds = 0.05f;

	//The longest a widget may be kept back waiting for its first picture. A widget that never paints anything -
	//a container that is only somewhere to put other widgets - would otherwise take its children down with it.
	private const float HiddenAtMost = 0.25f;

	//Hides a new widget until it has a picture at its real size, then fades it in; layout is unaffected
	private void Reveal()
	{
		if ( Style is null ) return;
		if ( revealed && Style.Opacity is null ) return;

		if ( !paintedOnce && sinceMade < HiddenAtMost )
		{
			Fade( 0.0f );
			return;
		}

		if ( !revealed )
		{
			revealed = true;
			sinceRevealed = 0;
		}

		if ( !UseFades )
		{
			Fade( null );
			return;
		}

		Fade( MathX.Clamp( (float)sinceRevealed / FadeSeconds, 0.0f, 1.0f ) );
	}

	//Paints now and shows at full opacity instead of waiting for the paint budget.
	internal void EnsurePainted()
	{
		if ( Style is null || Widget is not { IsValid: true } ) return;

		if ( burstPaintDepth > 0 && OverSuspendPaintBudget() )
			return;

		if ( Widget.Visible )
			SetClass( "hidden", false );

		var rect = new Rect( Widget.Position, Widget.Size );
		if ( rect.Width <= 0 || rect.Height <= 0 ) return;

		Widget.SyncSurfaceContent();

		//Before the paint, as in the tick: what the paint asks for is a request for the next one, and clearing after would be answering it by throwing it away.
		dirty = false;

		//As in the tick above: the world's own panel wears nothing of its own.
		if ( WearsWorld )
			ClearBakedBackground();
		else if ( Widget.SurfaceTexture is { IsValid: true } ready )
			WearLiveTexture( ready );
		else if ( Widget.UsesCanvasDraw )
			PaintCanvas( rect, asked: true );
		else
			ClearBakedBackground();

		paintedOnce = true;

		revealed = true;
		Fade( null );
	}

	//What this panel's opacity was last set to - null for the style's own nothing, which is what a panel starts with.
	private float? faded;

	//Sets opacity only when changed; fully opaque clears it to null so the panel carries nothing
	private void Fade( float? to )
	{
		var wanted = to >= 1.0f ? null : to;

		if ( wanted is null && faded is null ) return;
		if ( wanted is not null && faded is not null && MathF.Abs( wanted.Value - faded.Value ) < 0.001f ) return;

		faded = wanted;
		Style.Opacity = wanted;
	}

	private static int burstPaintDepth;
	private static double suspendPaintSpentMs;

	//Skips burst sync budget while a subtree is revealed in one go.
	internal static void BeginBurstPaint()
	{
		if ( burstPaintDepth++ == 0 )
			suspendPaintSpentMs = 0;
	}

	internal static void EndBurstPaint()
	{
		if ( burstPaintDepth > 0 )
			burstPaintDepth--;
	}

	static bool OverSuspendPaintBudget()
	{
		if ( !EditorPreferences.UiSuspendBurstPaintEnabled )
			return true;

		var limit = EditorPreferences.UiSuspendPaintBudgetMs;
		if ( limit <= 0.0f )
			return false;

		return suspendPaintSpentMs >= limit;
	}

	//The pointer. A panel's events are in its own pixels already, which is what a widget's local position means.

	protected override void OnMouseDown( MousePanelEvent e )
	{
		if ( Window.BlocksWidgetInput( Widget ) )
		{
			e.StopPropagation();
			return;
		}

		Said( "pressed", e.Button );

		//A press outside open menus closes them, once per press and before the widget handles it
		if ( ReferenceEquals( In( e.Target ), this ) )
		{
			if ( Panel.SuppressPopupDismissal?.Invoke( this ) != true )
			{
				Menu.PressedOn( Widget );

				//And whoever else watches for a press anywhere: an open completion list closes on one. The
				//window system used to say so through a hook into ManagedTools that nothing calls any more.
				EditorEvent.Run( "editor.mousepressed" );
			}
		}

		Widget.PanelMousePress( Event( e ) );
	}

	//The widget a panel is in - itself where it is one, and otherwise whatever widget it is drawn inside. Null for a panel that is in no widget at all.
	private static WidgetSurface In( Panel panel )
	{
		for ( var p = panel; p is not null; p = p.Parent )
		{
			if ( p is WidgetSurface surface ) return surface;
		}

		return null;
	}

	protected override void OnMouseUp( MousePanelEvent e )
	{
		if ( Window.BlocksWidgetInput( Widget ) )
		{
			e.StopPropagation();
			return;
		}

		Said( "released", e.Button );

		Widget.PanelMouseRelease( Event( e ) );
	}

	//Says which widget was pressed, under -uitrace . The one thing a log cannot otherwise tell you about an editor nobody can click: whether the press
	//reached a widget at all. Everything before this point is the window system's and the panels', and everything after is the widget's own business.
	private void Said( string what, string button )
	{
		if ( !tracing ) return;

		new Rinten.Diagnostics.Logger( "Ui" ).Info(
			$"{Widget.GetType().Name} {what} with {button} at {MousePosition}" );
	}

	protected override void OnMouseMove( MousePanelEvent e )
	{
		if ( Window.BlocksWidgetInput( Widget ) )
		{
			e.StopPropagation();
			return;
		}

		Widget.PanelMouseMove( Event( e ) );
	}

	protected override void OnDoubleClick( MousePanelEvent e )
	{
		if ( Window.BlocksWidgetInput( Widget ) )
		{
			e.StopPropagation();
			return;
		}

		Widget.PanelDoubleClick( Event( e ) );
	}

	protected override void OnMouseOver( MousePanelEvent e )
	{
		base.OnMouseOver( e );

		//The innermost widget the event passes through is the one being pointed at - see claimedHover.
		if ( !ReferenceEquals( claimedHover, e ) )
		{
			claimedHover = e;
			hovered = this;
		}

		Widget.PanelMouseEnter();
		Invalidate();
	}

	protected override void OnMouseOut( MousePanelEvent e )
	{
		base.OnMouseOut( e );

		if ( hovered == this ) hovered = null;

		Widget.PanelMouseLeave();
		Invalidate();
	}

	//The panel is what the keyboard is handed to; the widget hears about it here.
	protected override void OnFocus( PanelEvent e )
	{
		base.OnFocus( e );

		focused = this;

		if ( Widget is { IsValid: true } ) Widget.InternalFocusInEvent( FocusChangeReason.Other );
	}

	protected override void OnBlur( PanelEvent e )
	{
		base.OnBlur( e );

		if ( focused == this ) focused = null;

		if ( Widget is { IsValid: true } ) Widget.InternalFocusOutEvent( FocusChangeReason.Other );
	}


	public override void OnButtonTyped( ButtonEvent e )
	{
		e.StopPropagation = true;

		//The shortcut system is not told here: it hears every key the window hears, whether or not anything has the focus - see ToolsDll's
		//hook on CoreUiWindow.KeyHeard. Told here as well, a key pressed while a widget happens to be focused would fire its shortcut twice.
		var key = EditorKeys.FromName( e.Button );
		if ( key == 0 ) return;

		//The key's Windows virtual key as the native code, on every platform: it is the one number a widget can hand on
		//to something outside the editor - the web widget tells its browser this, and nought was a key it never heard.
		var typed = new KeyEvent( key, string.Empty, Modifiers( e ) ) { NativeKeyCode = (uint)e.VirtualKey };

		if ( e.Pressed ) Widget.PanelKeyPress( typed );
		else Widget.PanelKeyRelease( typed );
	}

	public override void OnKeyTyped( char key )
	{
		//A letter is its own key as far as a widget is concerned, and the text is what it is there for.
		Widget.PanelKeyPress( new KeyEvent( (KeyCode)char.ToUpperInvariant( key ), key.ToString(), KeyboardModifiers.None ) );
	}

	public override void OnMouseWheel( Vector2 value )
	{
		if ( Window.BlocksWidgetInput( Widget ) )
			return;

		var modifiers = Modifiers();
		var notches = -value.y * 120.0f;

		//Wheel shortcut binds first; at least one notch, since trackpads send fractions
		var turned = Math.Sign( notches ) * Math.Max( 1, (int)MathF.Abs( notches / 120.0f ) ) * 120;
		if ( EditorShortcuts.InvokeWheel( turned, modifiers ) ) return;

		//With whatever is held: shift over a scroll area scrolls sideways, and control zooms in most of the views.
		var wheel = new WheelEvent( notches, MousePosition, modifiers );

		Widget.PanelWheel( ref wheel );

		if ( !wheel.Accepted ) base.OnMouseWheel( value );
	}

	//What was held down with a key.
	private static KeyboardModifiers Modifiers( ButtonEvent e )
	{
		var modifiers = KeyboardModifiers.None;

		if ( e.HasShift ) modifiers |= KeyboardModifiers.Shift;
		if ( e.HasCtrl ) modifiers |= KeyboardModifiers.Ctrl;
		if ( e.HasAlt ) modifiers |= KeyboardModifiers.Alt;

		return modifiers;
	}

	//A panel's mouse event as a widget's.
	private MouseEvent Event( MousePanelEvent e )
	{
		var local = MousePosition;
		var screen = Box.Rect.Position + local;

		//Named, not guessed at. Anything this does not know is no button rather than the left one: the fallback used to be Left, so a button the
		//panels named anything else - a thumb button, or the "none" a move event carries - arrived at every widget in the editor as a left click.
		var button = e.Button switch
		{
			"mouseleft" => MouseButtons.Left,
			"mouseright" => MouseButtons.Right,
			"mousemiddle" => MouseButtons.Middle,
			"mouseback" => MouseButtons.Back,
			"mouseforward" => MouseButtons.Forward,
			_ => MouseButtons.None,
		};

		//Event button and currently held buttons are separate, so a plain move isn't read as a drag
		return new MouseEvent( local, screen, screen, button, Application.MouseButtons, Modifiers() );
	}

	//Held modifiers from the keyboard itself, not game input bindings
	private static KeyboardModifiers Modifiers() => Application.KeyboardModifiers;
}
