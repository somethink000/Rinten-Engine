using Rinten;
using Rinten.UI;
using System;
using System.Linq;

namespace Editor;

/// <summary>An editor window that is a window of ours full of panels.</summary>
[FramesOnDemand]
public partial class Window
{
	private CoreUiWindow shell;
	private string title = "rinten";

	//Whether a window wears a caption of its own instead of the window system's. One bar - TitleBar - drawn by our renderer, the same
	//on every platform: GNOME draws no caption for a Wayland window at all. Turn it off and the window system's own frame comes back.
	internal static bool OwnCaption { get; set; } = true;

	//The window this widget is the inside of, once it is open.
	internal CoreUiWindow Shell => shell;

	/// <summary>The editor window the pointer is in, or null where it is in none of ours.</summary>
	public static Window UnderPointer
	{
		get
		{
			if ( CoreUiWindow.Pointed is not { } pointed ) return null;

			foreach ( var window in All )
			{
				if ( window is null ) continue;
				if ( ReferenceEquals( window.shell, pointed ) ) return window;
			}

			return null;
		}
	}

	//Whether the keyboard is in this window, including dialogs drawn inside it (OpenInside)
	internal bool HasKeyboard
	{
		get
		{
			if ( CoreUiWindow.Focused is not { } focused ) return false;

			return ReferenceEquals( shell, focused ) || ReferenceEquals( inside, focused );
		}
	}

	//Whether this window is actually shown, not a DockWindow embedded as a workspace
	internal bool IsOnScreen => shell is not null || inside is not null;

	//Whether the keyboard is in any window of the editor's at all. False the moment the user goes to another
	//application, which is what makes an editor let go of the keys it thought were held - see ManagedTools.RunFrame .
	internal static bool AnyHasKeyboard => CoreUiWindow.Focused is not null;

	//Whether this window has been put where it opens, which happens once.
	private bool placed;

	//Centres the window on first show, against the desktop rather than the game's Screen.Size
	private void PlaceCentred()
	{
		if ( placed || shell is null ) return;

		placed = true;

		if ( !StartCentered ) return;

		CentreNow();
	}

	//Centres over the main editor window if any, else the desktop; also backs Widget.CenterWindow
	internal void CentreNow()
	{
		if ( shell is null ) return;

		var area = ScreenGeometry;

		//A dialog belongs in the middle of the editor rather than in the middle of a desktop the editor may only be half of.
		var main = Rinten.Internal.GlobalToolsNamespace.EditorWindow;

		if ( main is not null && !ReferenceEquals( main, this ) && main.Shell is not null )
		{
			var over = main.Shell.Placement;

			if ( over.Width > 1 && over.Height > 1 ) area = over;
		}

		//Placement, not Geometry, so both values share units and scaled displays don't grow the window
		var window = shell.Placement;

		if ( area.Width <= 1 || area.Height <= 1 ) return;
		if ( window.Width <= 1 || window.Height <= 1 ) return;

		//Never past the top or the left edge: a window taller than what it is centred in
		//puts its own caption - and the buttons to close it - above the top of the screen.
		var x = area.Left + MathF.Max( 0, MathF.Round( ( area.Width - window.Width ) * 0.5f ) );
		var y = area.Top + MathF.Max( 0, MathF.Round( ( area.Height - window.Height ) * 0.5f ) );

		shell.Placement = new Rect( x, y, window.Width, window.Height );
	}

	//Glass: whole-window alpha with the desktop blurred behind (no per-pixel alpha on Vulkan)
	protected virtual bool IsGlass => true;

	//Opens the window of the window system, and puts this widget's panel in it. Nothing happens until
	//something asks to be shown - a window that is made and never shown should not appear on somebody's screen.
	private bool OpenShell()
	{
		if ( shell is not null ) return true;
		if ( inside is not null ) return true;

		var size = Size;

		var width = size.x > 1 ? size.x.CeilToInt() : 1024;
		var height = size.y > 1 ? size.y.CeilToInt() : 720;

		//A dialog over a window that is one whole sheet of glass is drawn into that sheet rather than into a window of its own: two
		//sheets stacked come out flat white, because the lower one has no pixels for the upper one to blur. See CoreUiWindow.Inside.
		if ( IsDialog && CoreUiWindow.Inside is not null )
		{
			return OpenInside( CoreUiWindow.Inside, width, height );
		}

		//Always draw our own title bar and open without the window system's
		var caption = OwnCaption;

		//Whichever this window said it was. The main editor window says it is not glass, and that is the one that could not afford to be.
		var heading = string.IsNullOrWhiteSpace( WindowTitle ) ? title : WindowTitle;

		//The bar is TitleBar, one row with the menus and the window buttons in it.
		shell = CoreUiWindow.Open( heading, width, height, glass: IsGlass, captionByContent: caption );

		if ( shell is null ) return false;

		//The window going is this widget closing. Without it, pressing the close button asked the window system, the window system took the window away,
		//and this - the thing that owns the interface inside it, runs its close, and answers whoever is waiting on it - was never told and never went.
		shell.Closed = ShellClosed;

		//And its changes of shape: the widget follows them when told, rather than asking the window
		//its size once a frame for the rest of its life. Once now, for the shape it opened at.
		shell.Resized += ShellResized;
		ShellResized();

		//A sheet clears to nothing: what a pixel nobody painted shows is the desktop behind the window, blurred.
		//A window that is not one still needs a colour, because there its clear is what the whole window is.
		shell.Background = shell.Composited ? Color.Transparent : WindowCaption.Ground;

		//Every window of the editor, the same amount: one that is glass beside one that is solid reads as two applications.
		if ( IsGlass ) EditorGlass.Apply( shell );

		//The look, once, on the root - everything in the window is under it and wears it without asking
		EditorDesign.Apply( shell.Root );

		//And what kind of window it is, so the sheet can tell. A surface in a window that is glass has to paint nothing, or the ground it
		//would otherwise lay down covers the very blur the window was opened for - which is what made a see-through dialog come out black.
		shell.Root.SetClass( "glass", shell.Composited );

		//And the other way round, for the desktop that gave us nothing: there the window is solid, so
		//the panels inside it lay down their own glass rather than showing a blur that is not there.
		shell.Root.SetClass( "solid", !shell.Composited );

		//First composited non-dialog window hosts dialogs, since stacked sheets can't blur each other
		if ( shell.Composited && !IsDialog ) CoreUiWindow.Inside ??= shell;

		shell.Content.AddChild( Surface );

		//The cross the caption draws asks this window first. Nothing used to ask - OnClose was declared, overridden and called from
		//nowhere - so the editor went down without writing anything out, and a window that says it cannot be closed could be closed.
		shell.AskClose = AskClose;

		//Create the overlay now; it also watches for hover to show tooltips, so must be in the tree
		EditorOverlay.For( shell.Root );

		Size = new Vector2( width, height );

		return true;
	}

	//The window this widget is the inside of has gone, so the widget closes - the ordinary
	//way, through Window.Close, so a window that does something when it closes still does it.
	private void ShellClosed()
	{
		if ( shell is null ) return;
		if ( !IsValid ) return;

		Close();
	}

	//The window this one is drawn inside, where it is not one itself.
	private CoreUiWindow inside;

	//What pushes the window behind this one back while it is up.
	Panel scrim;

	static int _modalDepth;
	static readonly Stack<(Panel Scrim, Panel Surface)> _modals = new();

	/// <summary>True while an in-window dialog scrim is blocking the editor beneath it.</summary>
	public static bool ModalBlocksInput => _modalDepth > 0;

	/// <summary>The editor window the pointer is in, if it is in one of ours.</summary>
	public static Window GetPointed()
	{
		if ( CoreUiWindow.Pointed is not { } shell )
			return null;

		foreach ( var window in All )
		{
			if ( ReferenceEquals( window.Shell, shell ) )
				return window;
		}

		return null;
	}

	internal static bool IsUnderModal( Panel panel )
	{
		if ( _modalDepth <= 0 || panel is null )
			return false;

		foreach ( var (scrim, surface) in _modals )
		{
			for ( var p = panel; p is not null; p = p.Parent )
			{
				if ( p == surface || p == scrim )
					return true;
			}
		}

		return false;
	}

	public static bool BlocksWidgetInput( Widget widget )
	{
		if ( _modalDepth <= 0 )
			return false;

		if ( widget?.Surface is Panel surface && IsUnderModal( surface ) )
			return false;

		//The window's own bar and its buttons are never under a modal: a window that cannot be moved, minimised or
		//closed around one is not what anybody asked for. The bar itself only - what else is in it, the menus, is not.
		if ( widget is TitleBar ) return false;

		for ( var w = widget; w is not null; w = w.Parent )
		{
			if ( w is WindowControls ) return false;
		}

		return true;
	}

	static void SwallowMouse( PanelEvent e )
	{
		if ( e is MousePanelEvent mpe )
			mpe.StopPropagation();
	}

	void RegisterModal()
	{
		_modals.Push( (scrim, Surface) );
		_modalDepth = _modals.Count;
	}

	void UnregisterModal()
	{
		if ( _modals.Count == 0 )
			return;

		_modals.Pop();
		_modalDepth = _modals.Count;
	}

	//Holds a panel over the interface as its modal: a widget that is not inside it
	//stops hearing the pointer, the same as under an in-window dialog. See ModalSheet.
	internal static void PushModal( Panel over )
	{
		_modals.Push( (over, over) );
		_modalDepth = _modals.Count;
	}

	//Lets go of one - whichever it is, not only the one on top.
	internal static void PopModal( Panel over )
	{
		var kept = _modals.Where( x => x.Scrim != over ).Reverse().ToArray();

		_modals.Clear();

		foreach ( var modal in kept ) _modals.Push( modal );

		_modalDepth = _modals.Count;
	}

	//Opens as a sheet inside another window, which also avoids the system title bar
	private bool OpenInside( CoreUiWindow host, int width, int height )
	{
		inside = host;
		host.Resized += InsideResized;

		//Behind it: everything that was on the screen, still there and pushed back. It is also what swallows
		//presses meant for the window behind, which is what makes this modal without a loop of its own.
		scrim = new Panel();
		scrim.AddClass( "dialog-scrim" );
		scrim.AddEventListener( "onmousedown", SwallowMouse );
		scrim.AddEventListener( "onmouseup", SwallowMouse );
		scrim.AddEventListener( "onrightclick", SwallowMouse );
		scrim.AddEventListener( "onmiddleclick", SwallowMouse );

		host.Root.AddChild( scrim );
		host.Root.AddChild( Surface );

		//A card rather than a rectangle of interface: it is over the window rather than part of it,
		//and it has to say so on its own now that the window system is not drawing a frame round it.
		Surface.AddClass( "inside" );

		Size = new Vector2( width, height );
		Centre();
		RegisterModal();

		return true;
	}

	//Takes this window back out of the one it was drawn inside. The widget's own panel goes when the widget does; what has to be
	//undone here is everything put in somebody else's window - the layer behind it, and the standing interest in where that window is.
	private void CloseInside()
	{
		if ( inside is null ) return;

		UnregisterModal();
		scrim?.Delete();
		scrim = null;

		inside.Resized -= InsideResized;
		inside = null;
	}

	//Centres in the host window on its Resized; writes position only when it changed
	private void Centre()
	{
		if ( inside is null ) return;

		var room = inside.InterfaceSize;

		if ( room.x <= 0 || room.y <= 0 ) return;

		var middle = new Vector2(
			MathF.Round( ( room.x - Size.x ) * 0.5f ),
			MathF.Round( ( room.y - Size.y ) * 0.5f ) );

		if ( Position == middle ) return;

		Position = middle;
	}

	//Follows window resizes (CoreUiWindow.Resized) and lays out the parts in the same frame
	private void FollowShell()
	{
		if ( shell is null ) return;

		var room = shell.InterfaceSize;

		if ( room.x <= 0 || room.y <= 0 ) return;

		var top = 0;

		var wanted = new Vector2( room.x, MathF.Max( room.y - shell.CaptionHeight, 0 ) );

		//Against what the window last was rather than against the widget's own size: a widget with a fixed or a maximum size
		//never becomes what it was handed, and comparing the two would arrange it again every frame for the rest of its life.
		if ( followed == wanted && followedTop == top ) return;

		followed = wanted;
		followedTop = top;

		Position = new Vector2( 0, top );
		Size = wanted;

		//Size only arranges when the value it was given actually changed, and a window
		//clamped to a size of its own still has to place its three parts the first time round.
		ArrangeParts();
	}

	//How big the window was when the widget last followed it.
	private Vector2 followed = new( float.NaN, float.NaN );

	//How far down the caption pushed it, last time.
	private float followedTop = float.NaN;

	private protected override void OnArrange()
	{
		ArrangeParts();

		if ( OnPanels && Layout is not null )
		{
			Layout.Flex( Surface, ContentMargins + ChromeMargins() );

			DoLayout();
			return;
		}

		base.OnArrange();
	}

	//Margins for the title and status bars, which flex doesn't know about, so layouts avoid them
	private Margin ChromeMargins()
	{
		var client = ClientArea();

		return new Margin( 0.0f, client.Top - LocalRect.Top, 0.0f, LocalRect.Bottom - client.Bottom );
	}

	//Window interior below the title bar and above the status bar.
	Rect ClientArea()
	{
		var rect = LocalRect;

		if ( _menu is not null && _menu.Visible )
			rect.Top += _menu.WantedSize().y;

		if ( _status is not null && _status.Visible )
			rect.Bottom -= _status.WantedSize().y;

		return rect;
	}

	//The menu along the top, the status bar along the bottom, and whatever is left over for the canvas.
	private void ArrangeParts()
	{
		if ( !OnPanels ) return;

		if ( _menu is not null && _menu.Visible )
		{
			var wanted = _menu.WantedSize();

			_menu.Position = LocalRect.Position;
			_menu.Size = new Vector2( LocalRect.Width, wanted.y );
		}

		if ( _status is not null && _status.Visible )
		{
			var wanted = _status.WantedSize();

			_status.Position = new Vector2( LocalRect.Left, LocalRect.Bottom - wanted.y );
			_status.Size = new Vector2( LocalRect.Width, wanted.y );
		}

		var rect = ClientArea();

		if ( _canvas is not null )
		{
			_canvas.Position = rect.Position;
			_canvas.Size = rect.Size;
		}
	}

	//Whether this window has said where every widget in it ended up - for finding out which layout did not run, under -dumplayout.
	private bool dumped;

	//Follows the window and, for one drawn inside another, keeps it in the middle - on the frames asked for: the
	//window changed shape, the one it is inside did, or this widget was arranged. See FramesOnDemandAttribute.
	private protected override void OnWidgetFrame()
	{
		base.OnWidgetFrame();

		FollowShell();
		Centre();
	}

	//The window changed shape: follow it, and tell the caption - its buttons and drag strips depend on it.
	private void ShellResized()
	{
		RequestFrame();

		( MenuWidget as TitleBar )?.ShellChanged();
	}

	//The window this one is drawn inside changed shape, so this one is centred again.
	private void InsideResized() => RequestFrame();

	//Dumped a while after opening rather than at it, so what is written down is what the window settled into and not its first guess.
	private async Task DumpLayoutOnceSettled()
	{
		await Task.Delay( 8000 );

		if ( dumped || !IsValid ) return;

		dumped = true;

		DumpLayout();
	}

	internal void DumpLayout()
	{
		var log = new Rinten.Diagnostics.Logger( "Widgets" );

		void Walk( Widget widget, int depth )
		{
			var layout = widget.Layout is null
				? ""
				: $" [{(widget.Layout.Vertical ? "column" : "row")} {widget.Layout.Cells.Count} cells" +
					$" stretch {string.Join( ',', widget.Layout.Cells.Select( x => x.Stretch ) )}]";

			//Whether it is hidden, and whether the panel in front of it agrees. Half of what a layout dump is asked is "why can I see that" -
			//and a widget that says it is hidden while its panel is still on screen is a different fault from one that was never hidden at all.
			var hidden = widget.Hidden ? " hidden" : "";
			var showing = widget.Surface is { IsValid: true, IsVisibleSelf: true } && widget.Hidden
				? " PANEL-STILL-SHOWN"
				: "";

			//The panel in front of it, and the box that panel is a flex item in. A widget's size is read back off its panel, so "the widget
			//is nought wide" and "the panel it reads is nought wide" are two different faults, and the dump could not tell them apart.
			var surface = widget.Surface;
			var box = surface is { IsValid: true }
				? $" panel {surface.Box.Rect.Size}{(widget.InFlex ? "" : " NOT-IN-FLEX")}"
				: " no-panel";

			var holder = surface?.Parent is { } up
				? $" in {up.GetType().Name} {up.Box.Rect.Size}"
				: "";

			log.Info( $"{new string( ' ', depth * 2 )}{widget.GetType().Name} {widget.Position} {widget.Size}{layout}{hidden}{showing}{box}{holder}" );

			foreach ( var child in widget.childWidgets ) Walk( child, depth + 1 );
		}

		Walk( this, 0 );
	}

	//The startup dump answers what the editor settled into; most layout faults only appear after something has been opened, switched or dragged, and by then that one dump is eight seconds stale.
	/// <summary>Writes the widget tree of every window on screen to the log - what <c>-dumplayout</c> writes once at startup, whenever it is asked for.</summary>
	[ConCmd( "editor_dumplayout" )]
	public static void DumpLayoutCommand()
	{
		foreach ( var window in All.ToArray() )
		{
			if ( window.IsValid() && window.Visible ) window.DumpLayout();
		}
	}

	//Window background, skipped for glass windows and ones drawn inside another
	protected override void OnPaint()
	{
		if ( !OnPanels ) { base.OnPaint(); return; }

		if ( inside is not null ) return;
		if ( shell is { Composited: true } ) return;

		Paint.ClearPen();
		Paint.SetBrush( WindowCaption.Ground );
		Paint.DrawRect( LocalRect );
	}
}
