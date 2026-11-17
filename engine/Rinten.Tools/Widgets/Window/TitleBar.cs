using System;

namespace Editor;

//The one bar along the top of a window: icon, menus, whatever the window put there, and the three window buttons.
[FramesOnDemand]
internal class TitleBar : Widget
{
	//How tall the bar is, in the window's own pixels.
	internal const float BarHeight = 30.0f;

	//The window this bar is the top of.
	internal Widget Host { get; init; }

	//The window's icon at the left end of the bar. A picture and nothing else - it used to be a Button, which
	//is a thing that lights up under the pointer and can be pressed, and pressing it did nothing at all.
	private Widget IconWidget { get; set; }

	//The picture in it, at the size the bar draws it.
	private Pixmap icon;

	public TitleBarButtons TitleBarButtons { get; init; }

	//Minimise, maximise and close, at the right end of the row.
	internal WindowControls WindowControls { get; init; }

	public MenuBar MenuBar { get; private init; }

	private const int IconSize = 18;

	//The room either side of the window's name.
	private const float NamePadding = 12.0f;

	public Pixmap IconPixmap
	{
		set
		{
			icon = value?.Resize( IconSize );

			IconWidget.Visible = icon is not null;
			IconWidget.FixedSize = icon is null ? new Vector2( 0 ) : new Vector2( 32, 32 );

			Update();
		}
	}

	public TitleBar( Widget window, bool isMainWindow = false ) : base( window )
	{
		Host = window;

		Name = "TitleBar";
		MenuBar = new MenuBar( this );
		MenuBar.TitlesMoved = StripsMoved;

		//Bar height is the row's, since menu bar contents are absolutely placed and flex measures zero
		MenuBar.MinimumHeight = BarHeight;

		TitleBarButtons = new TitleBarButtons();
		WindowControls = new WindowControls( this );

		Layout = Layout.Row();
		//Across only: Left would pack the strip and throw away the stretch the menu needs.
		Layout.Alignment = TextFlag.CenterVertically;

		IconWidget = new Widget( this );
		IconWidget.Cursor = CursorShape.Arrow;
		IconWidget.OnPaintOverride = PaintIcon;

		//Nothing to press and nothing to light up: the pointer goes straight through it to the bar underneath.
		IconWidget.TransparentForMouseEvents = true;

		//Nothing until there is a picture to put in it. A window whose icon never loaded is a window with an empty square where one would be.
		IconWidget.Visible = false;
		IconWidget.FixedSize = new Vector2( 0 );

		Layout.Add( IconWidget, 0 );
		Layout.Add( MenuBar, 1 );
		Layout.Add( TitleBarButtons, 0 );
		Layout.Add( WindowControls, 0 );
	}

	//The bar's own height, so a window with no menus is capped the same as one with them.
	protected override Vector2 SizeHint()
	{
		if ( !OnPanels ) return base.SizeHint();

		return new Vector2( Size.x, BarHeight );
	}

	private bool PaintIcon()
	{
		Paint.ClearPen();
		Paint.ClearBrush();

		if ( icon is not null )
		{
			Paint.Draw( IconWidget.LocalRect.Contain( IconSize ), icon );
		}

		return true;
	}

	//The window's name, in the widest part of the bar nothing else is using; the main window has no room for one.
	protected override void OnPaint()
	{
		if ( !OnPanels ) return;

		//Not the main window: its bar is menus, tabs and buttons all the way across, and the name would be sat under them.
		if ( Host is EditorMainWindow ) return;

		var name = Host is Window window ? window.Title : WindowTitle;

		if ( string.IsNullOrWhiteSpace( name ) ) return;

		var free = WidestFreeArea();

		if ( free.Width <= 0 ) return;

		Paint.Antialiasing = true;
		Paint.SetDefaultFont();

		//Whole or not at all: a name cut off half way says less than no name.
		if ( EditorText.Measure( name ).x + (NamePadding * 2) > free.Width ) return;

		Paint.SetPen( Theme.InkFaint );
		Paint.DrawText( free.Shrink( NamePadding, 0 ), name, TextFlag.LeftCenter | TextFlag.SingleLine );
	}

	//The parts of the row nothing was drawn on, in this widget's own pixels.
	private void FreeAreas( Span<Rect> into, out int count )
	{
		count = 0;

		if ( !OnPanels || Size.x <= 0 || Size.y <= 0 ) return;

		var left = MenuBar.IsValid() ? MenuBar.Position.x + MenuBar.TitlesEnd : 0.0f;
		var right = TitleBarButtons.IsValid() ? TitleBarButtons.Position.x : WindowControls.Position.x;

		var trailing = MenuBar.IsValid() ? MenuBar.TrailingWidget : null;

		//Two gaps where something sits in the right half of the strip - the editor's workspace tabs - because one of them alone is a few pixels to drag by.
		if ( trailing.IsValid() && trailing.Visible )
		{
			var start = MenuBar.Position.x + trailing.Position.x;
			var used = start + ContentWidth( trailing );

			AddArea( into, ref count, left, MathF.Min( start, right ) );
			AddArea( into, ref count, MathF.Max( used, left ), right );

			return;
		}

		AddArea( into, ref count, left, right );
	}

	private void AddArea( Span<Rect> into, ref int count, float from, float to )
	{
		if ( count >= into.Length ) return;
		if ( to - from < 1.0f ) return;

		into[count++] = new Rect( from, 0, to - from, Size.y );
	}

	//How far along a widget its last child actually reaches.
	private static float ContentWidth( Widget widget )
	{
		//Markup has no child widgets to measure
		if ( widget is WorkspaceTabBar strip ) return strip.ContentWidth;

		var right = 0.0f;

		foreach ( var child in widget.childWidgets )
		{
			if ( !child.IsValid() || !child.Visible ) continue;

			right = MathF.Max( right, child.Position.x + child.Size.x );
		}

		return right;
	}

	//The gaps in the bar, in this widget's own pixels.
	internal Rect[] DragAreas()
	{
		Span<Rect> areas = stackalloc Rect[2];

		FreeAreas( areas, out var count );

		return areas[..count].ToArray();
	}

	//The biggest of them, which is where the window's name goes.
	private Rect WidestFreeArea()
	{
		Span<Rect> areas = stackalloc Rect[2];

		FreeAreas( areas, out var count );

		var widest = new Rect( 0, 0, 0, 0 );

		for ( var i = 0; i < count; i++ )
		{
			if ( areas[i].Width > widest.Width ) widest = areas[i];
		}

		return widest;
	}

	//What the window system was last told, so it is not told it again.
	private (int X, int Y, int Width, int Height)[] strips;

	//Whether what the bar holds has moved since its strips were last handed over.
	private bool stripsMoved = true;

	//Something in the bar moved; strips are recomputed next frame once children are arranged
	internal void StripsMoved()
	{
		stripsMoved = true;
		RequestFrame();
	}

	private protected override void OnArrange()
	{
		base.OnArrange();

		StripsMoved();
	}

	//The window changed shape: its buttons and its strips follow.
	internal void ShellChanged()
	{
		StripsMoved();
		WindowControls?.FollowWindow();
	}

	//Hands the window system the parts of the bar that drag the window.
	private protected override void OnWidgetFrame()
	{
		base.OnWidgetFrame();

		if ( !stripsMoved ) return;

		if ( Host is not Window window ) return;

		var shell = window.Shell;

		//Still moved: handed over once there is a window to hand them to.
		if ( shell is null ) return;

		stripsMoved = false;

		Span<Rect> areas = stackalloc Rect[2];

		FreeAreas( areas, out var count );

		Span<(int X, int Y, int Width, int Height)> next = stackalloc (int, int, int, int)[2];

		//The areas are in interface units; the window system hit-tests in window pixels. At anything above 100% leaving them
		//unscaled shrinks the strip and slides it left, which put it back over the workspace tabs - so a press there dragged
		//the window instead of pressing the tab.
		var scale = DpiScale;

		for ( var i = 0; i < count; i++ )
		{
			var at = ToScreen( new Vector2( areas[i].Left, areas[i].Top ) ) * scale;

			next[i] = (
				(int)MathF.Round( at.x ),
				(int)MathF.Round( at.y ),
				(int)MathF.Round( areas[i].Width * scale ),
				(int)MathF.Round( areas[i].Height * scale ) );
		}

		//On the frames something moved - everything under the bar arranging, a menu added by a tool, a workspace tab opened or closed, the
		//window changing shape. Compared on the stack: the array the window system is handed is made only when the strips actually moved.
		if ( Same( strips, next[..count] ) ) return;

		strips = next[..count].ToArray();
		shell.DragStrips = strips;
	}

	private static bool Same( (int X, int Y, int Width, int Height)[] a, ReadOnlySpan<(int X, int Y, int Width, int Height)> b )
	{
		if ( a is null ) return false;
		if ( a.Length != b.Length ) return false;

		for ( var i = 0; i < a.Length; i++ )
		{
			if ( a[i] != b[i] ) return false;
		}

		return true;
	}

	[Event( "refresh" )]
	[Event( "project.settings.saved" )]
	public void OnHotload()
	{
		if ( Host is EditorMainWindow )
		{
			TitleBarButtons.Layout.Clear( true );

			//Every one of them is about the project - playing it, building it, its settings - and the editor comes
			//up before it has one. The bar is built again once it does: see EditorMainWindow.OnProjectOpened.
			if ( Project.Current is null ) return;

			EditorEvent.Run( "editor.titlebar.buttons.build", TitleBarButtons );
			EditorEvent.Run( "tools.titlebar.build", TitleBarButtons.Layout );
		}
	}
}

/// <summary>A list of title bar buttons, at the top right of a window.</summary>
public class TitleBarButtons : Widget
{
	public TitleBarButtons()
	{
		Layout = Layout.Row();
	}

	/// <summary>Adds a button to the title bar.</summary>
	public Widget AddButton( string icon, Action onClick )
	{
		return Layout.Add( new TitleBarButton( icon, onClick ) );
	}

	/// <summary>Adds a toggle button to the title bar.</summary>
	public TitleBarToggle AddToggleButton( string icon, Action<bool> onSet, bool initialValue = false )
	{
		var b = Layout.Add( new TitleBarToggle( icon, onSet ) );
		b.Value = initialValue;

		return b;
	}

	public TitleBarToggle AddToggleButton( Pixmap icon, Action<bool> onSet, bool initialValue = false )
	{
		var b = Layout.Add( new TitleBarToggle( icon, onSet ) );
		b.Value = initialValue;

		return b;
	}

	/// <summary>Adds a custom widget to the title bar.</summary>
	public Widget Add( Widget widget )
	{
		return Layout.Add( widget );
	}
}

//Minimise, maximise and close, at the end of the bar.
internal sealed class WindowControls : Widget
{
	private readonly TitleBar bar;
	private readonly WindowControl minimize;
	private readonly WindowControl maximize;
	private readonly WindowControl close;

	internal WindowControls( TitleBar bar )
	{
		this.bar = bar;

		Layout = Layout.Row();

		minimize = Layout.Add( new WindowControl( "remove", Minimize ) );
		maximize = Layout.Add( new WindowControl( "crop_square", ToggleMaximized ) );
		close = Layout.Add( new WindowControl( "close", Shut ) { Shut = true } );

		if ( bar.Host is Window window ) window.WindowFlagsChanged += FlagsChanged;
	}

	//The window's flags changed, and with them which buttons it has.
	private void FlagsChanged()
	{
		FollowWindow();
		MarkNeedsArrange();
	}

	private Window Window => bar.IsValid() ? bar.Host as Window : null;

	private void Minimize() => Window?.Shell?.Minimize();

	private void ToggleMaximized()
	{
		var shell = Window?.Shell;

		if ( shell is null ) return;

		if ( shell.IsMaximized ) shell.Restore();
		else shell.Maximize();
	}

	//Asks the window to close; a dialog drawn inside another has no window system to ask.
	private void Shut()
	{
		var window = Window;

		if ( window is null ) return;

		//The window is asked before the window system is told. It may have something to do first, or something to ask about, and it may say no.
		if ( !window.AskClose() ) return;

		if ( window.Shell is { } shell ) shell.RequestClose();
		else window.Close();
	}

	//Shows only the buttons the window's flags ask for
	private void ShowWhatTheWindowHas( Window window )
	{
		minimize.Visible = window.HasWindowFlag( WindowFlags.MinimizeButton );
		maximize.Visible = window.HasWindowFlag( WindowFlags.MaximizeButton );
		close.Visible = window.HasWindowFlag( WindowFlags.CloseButton );
	}

	//The row is laid out, so what is in it is settled first.
	protected override void DoLayout()
	{
		if ( Window is { } window ) ShowWhatTheWindowHas( window );

		base.DoLayout();
	}

	//Syncs buttons and the maximize glyph with the window; called on shape and flag changes
	internal void FollowWindow()
	{
		if ( Window is not { } window ) return;

		ShowWhatTheWindowHas( window );

		if ( window.Shell is not { } shell ) return;

		var glyph = shell.IsMaximized ? "filter_none" : "crop_square";

		if ( maximize.Icon == glyph ) return;

		maximize.Icon = glyph;
		maximize.Update();
	}
}

//One of the three window buttons.
internal sealed class WindowControl : Widget
{
	private readonly Action pressed;

	public string Icon { get; set; }

	/// <summary>Whether this is the one that closes the window, which goes red.</summary>
	public bool Shut { get; init; }

	private static readonly Color Danger = new( 0xc4 / 255.0f, 0x2b / 255.0f, 0x1c / 255.0f );

	public WindowControl( string icon, Action pressed )
	{
		Icon = icon;

		this.pressed = pressed;

		FixedSize = new Vector2( 44, TitleBar.BarHeight );
	}

	protected override void OnMouseClick( MouseEvent e )
	{
		pressed?.Invoke();

		e.Accepted = true;
	}

	protected override void OnPaint()
	{
		var over = Paint.HasMouseOver && Enabled;

		if ( over )
		{
			Paint.ClearPen();
			Paint.SetBrush( Shut ? Danger : Theme.VeilHover );
			Paint.DrawRect( LocalRect );
		}

		Paint.ClearBrush();
		Paint.SetPen( Enabled ? (over ? Theme.Ink : Theme.InkDim) : Theme.InkOff );

		Paint.DrawIcon( LocalRect, Icon, 16.0f );
	}
}

//Rounded inset plate for title bar buttons when hovered or on
internal static class TitleBarPlate
{
	//How far in from the bar the plate sits.
	private const float Inset = 3.0f;

	/// <summary>The plate, at however much of it has arrived.</summary>
	public static void Draw( Rect bar, float over, float on )
	{
		if ( over <= 0.0f && on <= 0.0f ) return;

		var plate = bar.Shrink( 2.0f, Inset );

		Paint.Antialiasing = true;
		Paint.ClearPen();

		//On wins over hover, never both: on uses the selected plate, hover a light white
		Paint.SetBrush( on > 0.0f
			? Theme.SelectedBackground.WithAlphaMultiplied( on )
			: Theme.VeilHover.WithAlphaMultiplied( over ) );

		Paint.DrawRect( plate, Theme.ControlRadius );
	}
}

internal class TitleBarButton : Widget
{
	private Action _onClick;

	public string Icon { get; set; }
	public Color HighlightColor { get; set; } = Theme.Text.WithAlpha( 0.1f );

	//How far the plate under the pointer has arrived.
	private Theme.Lit litHover;

	public TitleBarButton( string icon, Action onClick = null )
	{
		_onClick = onClick;

		Icon = icon;
		Cursor = CursorShape.Finger;
		FixedSize = new Vector2( 40, 32 );
	}

	protected override void OnMouseClick( MouseEvent e )
	{
		_onClick?.Invoke();
	}

	protected override void OnPaint()
	{
		var over = litHover.Step( Paint.HasMouseOver && Enabled );

		TitleBarPlate.Draw( LocalRect, over, 0.0f );

		if ( litHover.Moving ) Update();

		Paint.ClearBrush();

		//The icon comes up with the plate rather than sitting at full white on a bar of icons nobody is pointing at.
		Paint.SetPen( Enabled ? Theme.InkDim.LerpTo( Theme.Ink, over ) : Theme.InkOff );

		Paint.DrawIcon( LocalRect, Icon, 13.0f );
	}
}


/// <summary>A button in the title bar that stays pressed, drawn like every other one there. Public because what turns one on is not always the click on it - the eyedropper puts itself away when a pick is made or cancelled, and has to be able to say so.</summary>
public class TitleBarToggle : Widget
{
	private Action<bool> _onSet;

	public string Icon { get; set; }
	public Pixmap PixmapIcon { get; set; }
	public Color HighlightColor { get; set; } = Theme.Text.WithAlpha( 0.1f );

	/// <summary>Whether it is on. Set from outside as readily as by the click, and the button repaints either way - it has a fade to run now, and a widget that is never invalidated never runs it.</summary>
	public bool Value
	{
		get => value;
		set
		{
			if ( this.value == value ) return;

			this.value = value;
			Update();
		}
	}

	private bool value;

	public TitleBarToggle( string icon, Action<bool> onSet )
	{
		Icon = icon;
		Init( onSet );
	}

	public TitleBarToggle( Pixmap icon, Action<bool> onSet )
	{
		PixmapIcon = icon;
		Init( onSet );
	}

	private void Init( Action<bool> onSet )
	{
		_onSet = onSet;
		Cursor = CursorShape.Finger;
		FixedSize = new Vector2( 40, 32 );
	}

	protected override void OnMouseClick( MouseEvent e )
	{
		Value = !Value;

		_onSet?.Invoke( Value );
	}

	protected override void OnPaint()
	{
		//Being on and being pointed at are both things this fades between - see Theme.Lit. A toggle in the bar is small and there are several
		//of them in a row, so what says which one took is the plate arriving under it rather than a state that changes between two frames.
		var on = litOn.Step( Value );
		var over = litHover.Step( Paint.HasMouseOver && Enabled && !Value );

		TitleBarPlate.Draw( LocalRect, over, on );

		if ( litOn.Moving || litHover.Moving ) Update();

		Paint.ClearBrush();
		Paint.SetPen( Enabled ? Theme.InkDim.LerpTo( Theme.Ink, MathF.Max( on, over ) ) : Theme.InkOff );

		if ( PixmapIcon is not null )
		{
			Paint.Draw( LocalRect, PixmapIcon );
		}
		else
		{
			Paint.DrawIcon( LocalRect, Icon, 13.0f );
		}
	}

	//How far it has faded into being on, and into being pointed at.
	private Theme.Lit litOn;
	private Theme.Lit litHover;
}
