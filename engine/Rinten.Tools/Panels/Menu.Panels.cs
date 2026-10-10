using Rinten;
using Rinten.UI;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Editor;

/// <summary>A menu that is panels rather than a window of the platform's.</summary>
[FramesOnDemand]
public partial class Menu
{
	//Every menu of ours that is open, newest last.
	private static readonly List<Menu> openMenus = new();

	static readonly Func<Panel, bool> PreviousPopupDismissal = Panel.SuppressPopupDismissal;

	static Menu()
	{
		Panel.SuppressPopupDismissal = ShouldSuppressPopupDismissal;
		BasePopup.SparePopupsForInteraction = SparePopupsHostingOpenMenus;
	}

	static void SparePopupsHostingOpenMenus( HashSet<BasePopup> spared )
	{
		openMenus.RemoveAll( x => !x.IsValid );

		foreach ( var menu in openMenus )
		{
			for ( var current = menu; current is not null; current = current.ParentMenu )
			{
				var anchor = current.OverlayAnchor;

				if ( anchor is not { IsValid: true } )
					continue;

				BasePopup.SparePopupsContaining( anchor, spared );
			}
		}
	}

	static bool ShouldSuppressPopupDismissal( Panel panel )
	{
		if ( PreviousPopupDismissal?.Invoke( panel ) == true )
			return true;

		if ( panel is null )
			return false;

		openMenus.RemoveAll( x => !x.IsValid );

		foreach ( var menu in openMenus )
		{
			var surface = menu.Surface;

			if ( surface is null || !surface.IsValid )
				continue;

			if ( IsPanelInside( panel, surface ) )
				return true;
		}

		return false;
	}

	private EditorOverlay host;

	//How wide a menu is unless its own rows want more.
	private const float Narrowest = 120.0f;

	//Whether this menu is one of the ones on the screen.
	internal bool IsOpen => openMenus.Contains( this );

	//The state a menu arrives from and leaves to, worn as a class so that the sheet says what it looks like and this says when.
	private const string Away = "away";

	//How long a menu takes to leave. The sheet moves it; this only has to outlast that, because
	//what it decides is when the panel comes off the layer rather than what it does on the way out.
	private const float Leaving = 0.15f;

	//Frames before the arrive class is removed; two, so the start style is laid out and the transition runs
	private int arriving;

	private bool leaving;
	private RealTimeSince sinceLeaving;

	//Panel the menu was opened from, so the overlay is the right window.
	private Panel overlayAnchor;

	internal Panel OverlayAnchor => overlayAnchor;

	//Puts the menu on the screen at a point in the window it belongs to.
	private void OpenPanel( Vector2 screenPosition, Rect? belowAnchor = null, float gap = 0f, Panel anchor = null, bool alignRight = true )
	{
		overlayAnchor = anchor;

		var layer = HostLayer();

		if ( layer is null ) return;

		//Everything open that this menu does not hang off goes first. Without it a second right click left the
		//first menu where it was and put another one on top - nothing here ever looked at what was already up.
		CloseOthers();

		//And a menu that is already open moves rather than being opened a second time, which would list it twice and leak the earlier one.
		openMenus.Remove( this );

		leaving = false;

		//Opened again after leaving, it hears the pointer again. Its rows are made afresh below; what somebody put in the menu themselves is not.
		TransparentForMouseEvents = false;

		//Before the rows: a searchable menu puts its field in here, and anything listening can still add options that then become rows.
		OnAboutToShowInternal();

		foreach ( var widget in Widgets )
		{
			if ( widget.IsValid ) widget.TransparentForMouseEvents = false;
		}

		Build();

		if ( Owner is null && Parent is not null ) Owner = Parent;

		SetPanelParent( null );

		host = layer;

		//Re-added rather than left where it is: what is drawn over what on the layer is the order they
		//were added in, so a menu opened while another is still on its way out has to be the newer child.
		if ( Surface.Parent is not null ) Surface.Parent = null;

		host.AddChild( Surface );

		//Kept inside the window: a menu that hangs off the edge of ours is a menu with half of itself
		//missing, because there is no second window to draw it in. The layer works out where that is.
		if ( belowAnchor.HasValue )
		{
			var anchorRect = belowAnchor.Value;
			var left = alignRight ? anchorRect.Right - Size.x : anchorRect.Left;

			screenPosition = new Vector2( left, anchorRect.Bottom + gap );
		}

		Position = layer.Place( screenPosition, Size );

		Visible = true;

		foreach ( var widget in Widgets )
		{
			if ( !widget.IsValid || widget is not LineEdit field ) continue;

			field.Focus();
			break;
		}

		//It arrives: a frame in the state it comes from - and that one snapped to, not moved to, or the menu
		//would be seen leaving before it ever got here - and then the class comes off and the sheet moves it in.
		Surface.AddClass( Away );
		Surface.SkipTransitions();

		arriving = 2;
		RequestFrame();

		openMenus.Add( this );
	}

	//Closes everything open that this menu is not opened from - the menu it hangs off, and that one's own parents, stay where they are.
	private void CloseOthers()
	{
		foreach ( var other in openMenus.ToArray() )
		{
			if ( Under( other ) ) continue;

			other.ClosePanel();
		}
	}

	//Closes every submenu of this one that is open, except that one.
	internal void CloseSubmenusExcept( Menu keep )
	{
		foreach ( var other in Menus )
		{
			if ( other != keep && other.IsValid && other.IsOpen ) other.ClosePanel();
		}
	}

	//Whether this menu is that one, or hangs off it.
	private bool Under( Menu other )
	{
		for ( var menu = this; menu is not null; menu = menu.ParentMenu )
		{
			if ( menu == other ) return true;
		}

		return false;
	}

	//A press closes open menus it wasn't inside, without eating it, so a new right-click menu opens at once
	internal static void PressedOn( Widget widget )
	{
		if ( widget?.Surface is Panel panel && Panel.SuppressPopupDismissal?.Invoke( panel ) == true )
			return;

		dismissed.Clear();

		PopupWidget.PressedOn( widget );

		openMenus.RemoveAll( x => !x.IsValid );

		if ( openMenus.Count == 0 ) return;

		for ( var w = widget; w is not null; w = w.Parent )
		{
			if ( w is PopupWidget ) return;
			if ( w is Menu menu && menu.IsOpen ) return;
		}

		foreach ( var menu in openMenus )
		{
			if ( menu.Owner.IsValid() ) dismissed.Add( menu.Owner );
		}

		CloseAllPanels();
	}

	//A press landed on a panel: overlay menus go unless the press was inside one.
	internal static void PressedOnPanel( Panel panel )
	{
		if ( panel is null ) return;

		if ( Panel.SuppressPopupDismissal?.Invoke( panel ) == true )
			return;

		dismissed.Clear();

		openMenus.RemoveAll( x => !x.IsValid );

		foreach ( var menu in openMenus )
		{
			var surface = menu.Surface;

			if ( surface is null || !surface.IsValid() ) continue;

			if ( IsPanelInside( panel, surface ) ) return;
		}

		Widget widget = null;

		for ( var p = panel; p is not null; p = p.Parent )
		{
			if ( p is WidgetSurface { Widget.IsValid: true } surface )
			{
				widget = surface.Widget;
				break;
			}
		}

		if ( widget is not null )
			PopupWidget.PressedOn( widget );

		if ( openMenus.Count == 0 ) return;

		foreach ( var menu in openMenus )
		{
			if ( menu.Owner.IsValid() ) dismissed.Add( menu.Owner );
		}

		CloseAllPanels();
	}

	static bool IsPanelInside( Panel panel, Panel surface )
	{
		foreach ( var hit in panel.AncestorsAndSelf )
		{
			if ( hit == surface ) return true;
		}

		return false;
	}

	//What the menus that the press now down closed were opened from.
	private static readonly HashSet<Widget> dismissed = new();

	//Whether this press closed this widget's menu, so the release doesn't reopen it
	internal static bool ClosedByPress( Widget widget )
	{
		return widget is not null && dismissed.Contains( widget );
	}

	//Overlay of the window this menu belongs to; uses the opener, since closed menus lose their parent
	private EditorOverlay HostLayer()
	{
		if ( overlayAnchor is not null )
		{
			for ( var panel = overlayAnchor; panel is not null; panel = panel.Parent )
			{
				if ( EditorOverlay.For( panel ) is { } fromAnchor )
					return fromAnchor;
			}
		}

		for ( var widget = Owner ?? ParentMenu?.Owner ?? Parent; widget is not null; widget = widget.Parent )
		{
			if ( EditorOverlay.For( widget ) is { } found ) return found;
		}

		//Opened from nothing in particular - a shortcut, a context menu a panel asked for without naming itself.
		//The pointer says which window that is, and a menu opened at the cursor belongs in the window under it.
		if ( Window.UnderPointer is { Shell: { } pointed } && EditorOverlay.For( pointed.Root ) is { } top ) return top;

		var at = Application.CursorPosition;

		foreach ( var window in Window.All )
		{
			if ( !window.ScreenRect.IsInside( at ) ) continue;

			if ( EditorOverlay.For( window.Shell?.Root ) is { } under ) return under;
		}

		//Failing that, whichever window is in front - and past any that has no shell to hang the menu on, which is a window that is not open yet.
		for ( var index = Window.All.Count - 1; index >= 0; index-- )
		{
			if ( EditorOverlay.For( Window.All[index].Shell?.Root ) is { } found ) return found;
		}

		return null;
	}

	//The widget this menu was opened from, if any.
	internal Widget Owner { get; set; }

	//Makes the rows: one per option, one per submenu, and whatever widgets were added straight to the menu.
	internal void Build()
	{
		if ( Surface is null ) MakeSurface( null );

		//Widgets are usually added before the menu has a surface.
		foreach ( var widget in Widgets )
		{
			if ( widget.IsValid() ) widget.SetPanelParent( this );
		}

		foreach ( var row in rows ) row.Delete( true );

		rows.Clear();

		//A destroyed option is not a row. It takes itself out of this list when it goes; anything that
		//got dropped some other way goes here rather than being drawn as a row that does nothing.
		Options.RemoveAll( x => !x.IsValid );

		var widest = Narrowest;
		var height = Padding;

		//Widgets first: a search field belongs above the buttons, not under them, and a heading is the same.
		foreach ( var widget in Widgets )
		{
			if ( !widget.IsValid ) continue;

			widest = MathF.Max( widest, WidgetWidth( widget ) );
			height += WidgetHeight( widget );
		}

		//In the order they were added, rather than every option and then every submenu - which put "Open Recent"
		//at the bottom of the File menu and every separator somewhere other than the gap it was written for.
		var entries = new List<(int Order, Option Option, Menu Menu)>();

		foreach ( var option in Options ) entries.Add( (option.Order, option, null) );

		foreach ( var menu in Menus )
		{
			if ( menu.IsValid ) entries.Add( (menu.Order, null, menu) );
		}

		entries.Sort( ( a, b ) => a.Order.CompareTo( b.Order ) );

		//Whoever builds a menu adds its lines blind, so an option that turned out not to apply
		//leaves one behind: runs of them, and a line at either end, are dropped rather than drawn.
		var shown = new List<(int Order, Option Option, Menu Menu)>( entries.Count );

		foreach ( var entry in entries )
		{
			if ( !IsSeparatorEntry( entry ) )
			{
				shown.Add( entry );
				continue;
			}

			if ( shown.Count == 0 || IsSeparatorEntry( shown[^1] ) ) continue;

			shown.Add( entry );
		}

		while ( shown.Count > 0 && IsSeparatorEntry( shown[^1] ) )
		{
			shown.RemoveAt( shown.Count - 1 );
		}

		foreach ( var entry in shown )
		{
			var row = Surface.AddChild( new MenuRow( this, entry.Option, entry.Menu ) );

			rows.Add( row );

			widest = MathF.Max( widest, row.WantedWidth );
			height += row.RowHeight;
		}

		Size = new Vector2( widest, height + Padding );

		var at = Padding;
		var inner = widest - Padding * 2.0f;

		foreach ( var widget in Widgets )
		{
			if ( !widget.IsValid ) continue;

			var tall = WidgetHeight( widget );

			widget.Position = new Vector2( Padding, at );
			widget.Size = new Vector2( inner, tall );

			ArrangeEmbedded( widget );

			at += tall;
		}

		foreach ( var row in rows )
		{
			row.Place( at, widest );

			at += row.RowHeight;
		}
	}

	//A line between groups, rather than something to choose or open.
	private static bool IsSeparatorEntry( (int Order, Option Option, Menu Menu) entry )
	{
		return entry.Menu is null && entry.Option is { IsSeparator: true };
	}

	private static float WidgetWidth( Widget widget )
	{
		var cell = new Layout.Cell { Widget = widget };
		var fromLayout = Layout.WantsOf( cell, vertical: false );
		var wide = MathF.Max( fromLayout, widget.WantedSize().x );

		return MathF.Max( wide, widget.MinimumWidth ) + Padding * 2.0f;
	}

	private static float WidgetHeight( Widget widget )
	{
		var cell = new Layout.Cell { Widget = widget };
		var fromLayout = Layout.WantsOf( cell, vertical: true );
		var tall = MathF.Max( fromLayout, widget.WantedSize().y );

		return tall > 0.0f ? tall : widget.Size.y;
	}

	private static void ArrangeEmbedded( Widget widget )
	{
		widget.ArrangeIfNeeded();

		foreach ( var child in widget.childWidgets )
			ArrangeEmbedded( child );
	}

	/// <summary>Widgets somebody put in the menu themselves - a search field at the top, a colour picker, a heading. They are rows like any other, except they draw themselves.</summary>
	public List<Widget> Widgets { get; } = new();

	//The room above the first row and below the last - Menu's own padding, 5px 0.
	internal const float Padding = 5.0f;

	private readonly List<MenuRow> rows = new();

	//The rows the menu was last built from, top to bottom.
	internal IReadOnlyList<MenuRow> Rows => rows;

	/// <summary>Closes the panel-backed menu without destroying the widget.</summary>
	public override void Close()
	{
		if ( OnPanels )
			ClosePanel();
		else
			base.Close();
	}

	//Takes the menu off the screen.
	internal void ClosePanel()
	{
		if ( !openMenus.Remove( this ) ) return;

		//Whatever hangs off this one goes with it: a submenu is only on the screen because the menu it belongs to is.
		foreach ( var below in openMenus.ToArray() )
		{
			if ( below.Under( this ) ) below.ClosePanel();
		}

		OnAboutToHideInternal();

		if ( !IsValid || Surface is null || Surface.Parent is null )
		{
			Gone();
			return;
		}

		//It leaves rather than vanishes. The panel stays on the layer, wearing the state it arrived from again, and comes off once it has gone.
		Surface.AddClass( Away );

		//Closed before it finished arriving - a menu opened and dismissed inside two frames
		//- it stays where it is rather than arriving at a menu that is already going.
		arriving = 0;

		leaving = true;
		sinceLeaving = 0;

		RequestFrame();

		//Nothing on a menu on its way out is worth pointing at, and it is over whatever is underneath until it is gone.
		TransparentForMouseEvents = true;

		foreach ( var row in rows )
		{
			if ( row.IsValid() ) row.MakeInert();
		}

		foreach ( var widget in Widgets )
		{
			if ( widget.IsValid ) widget.TransparentForMouseEvents = true;
		}
	}

	//The menu has left: off the layer, and deleted if it was to be.
	private void Gone()
	{
		leaving = false;

		if ( !IsValid ) return;

		Visible = false;

		SetPanelParent( null );

		host = null;

		if ( DeleteOnClose ) Destroy();
	}

	//While it arrives and while it leaves - the frames it asks for then, see FramesOnDemandAttribute:
	//letting go of where the menu arrived from, and taking one that has left off the layer.
	private protected override void OnWidgetFrame()
	{
		base.OnWidgetFrame();

		if ( !OnPanels ) return;

		if ( arriving > 0 )
		{
			arriving--;

			if ( arriving == 0 ) Surface?.RemoveClass( Away );
			else RequestFrame();

			return;
		}

		if ( !leaving ) return;

		if ( sinceLeaving < Leaving )
		{
			RequestFrame();
			return;
		}

		Gone();
	}

	//Closes every open menu, as clicking away from one does. Over a copy: closing a menu closes the ones hanging off it
	//too, so the list is shorter by the time the loop comes round again. Closing one that has already gone does nothing.
	internal static void CloseAllPanels()
	{
		foreach ( var menu in openMenus.ToArray() )
		{
			menu.ClosePanel();
		}
	}

	//Nothing: the menu is a floating thing, and what those look like is said once in the editor's own sheet under .menu - glass over whatever it
	//covers, a lit edge along the top, and it arrives rather than appears. Painting a background here would be a picture drawn over all of that.
	protected override void OnPaint()
	{
		if ( !OnPanels ) { base.OnPaint(); return; }
	}

	internal void OnAboutToShowInternal() => OnAboutToShow();
	internal void OnAboutToHideInternal() => OnAboutToHide();
}


//A menu row (option, submenu or separator); width computed here must match the .menu-row sheet
internal sealed class MenuRow : Panel
{
	//A texture per picture rather than one per opening. ToTexture uploads a new one every call and nothing here would ever
	//let it go: an asset type's icon in the create menu would leave a texture on the device each time the menu was opened.
	private static readonly ConditionalWeakTable<Bitmap, Texture> pictures = new();

	private readonly Menu menu;
	private readonly Option option;
	private readonly Menu submenu;

	private Rinten.UI.Label glyph;

	//Where the row was put, so a submenu opens beside it.
	private float top;
	private float wide;

	//Whether the menu this row is in has been closed.
	private bool inert;

	//.menu-row .body margin: 0 5px.
	private const float Inset = 5.0f;

	//.menu-row .icon width, and the room left after the words.
	private const float Gutter = 20.0f;

	//.menu-row.separator height.
	private const float SeparatorHeight = 11.0f;

	//.menu-row .shortcut margin-left.
	private const float ShortcutGap = 24.0f;

	//.menu-row .arrow width.
	private const float ArrowWidth = 16.0f;

	public MenuRow( Menu menu, Option option, Menu submenu )
	{
		this.menu = menu;
		this.option = option;
		this.submenu = submenu;

		AddClass( "menu-row" );

		if ( IsSeparator )
		{
			AddClass( "separator" );
			AddChild<Panel>( "line" );

			return;
		}

		var body = AddChild<Panel>( "body" );

		if ( Picture() is { } picture )
		{
			var image = body.AddChild<Panel>( "picture" );
			image.Style.BackgroundImage = picture;
		}
		else
		{
			glyph = body.AddChild<Rinten.UI.Label>( "icon" );
			glyph.AddClass( "material-icon" );
			glyph.Tokenize = false;
		}

		var text = body.AddChild<Rinten.UI.Label>( "text" );
		text.Tokenize = false;
		text.Text = Text;

		if ( !string.IsNullOrEmpty( Shortcut ) )
		{
			var shortcut = body.AddChild<Rinten.UI.Label>( "shortcut" );
			shortcut.Tokenize = false;
			shortcut.Text = Shortcut;
		}

		if ( submenu is not null )
		{
			AddClass( "has-submenu" );

			var arrow = body.AddChild<Rinten.UI.Label>( "arrow" );
			arrow.AddClass( "material-icon" );
			arrow.Tokenize = false;
			arrow.Text = "chevron_right";
		}

		Sync();
	}

	private bool IsSeparator => option is { IsSeparator: true } && submenu is null;

	private bool IsHeading => option is { IsHeading: true };

	private bool IsChecked => option is { Checkable: true, Checked: true };

	private string Text => option?.RowText ?? submenu?.Title ?? string.Empty;
	private string Shortcut => option?.RowShortcut;
	private string Icon => option?.RowIcon ?? submenu?.Icon;

	//How tall the row is. The sheet is told this, rather than asked.
	internal float RowHeight => IsSeparator ? SeparatorHeight : Theme.RowHeight;

	//How wide the row wants to be, from the words in it.
	internal float WantedWidth
	{
		get
		{
			if ( IsSeparator ) return 0.0f;

			//At the weight it may be drawn at: a checked row is bold, and one measured light comes out a few pixels short of its own words.
			var weight = option is { Checkable: true } ? 700 : 400;

			var width = EditorText.Measure( Text, EditorText.Size, null, weight ).x + (Inset + Gutter) * 2.0f;

			if ( !string.IsNullOrEmpty( Shortcut ) ) width += EditorText.Measure( Shortcut ).x + ShortcutGap;
			if ( submenu is not null ) width += ArrowWidth;

			return width;
		}
	}

	//Where the row was put in the menu, and how wide it came out.
	internal float Top => top;

	internal float Wide => wide;

	//Puts the row where the menu worked out it goes.
	internal void Place( float at, float width )
	{
		top = at;
		wide = width;

		Style.Left = 0.0f;
		Style.Top = at;
		Style.Width = width;
		Style.Height = RowHeight;
	}

	//The state the sheet dresses, and the check that replaces the icon.
	private void Sync()
	{
		if ( IsSeparator ) return;

		var enabled = option?.Enabled ?? true;
		var checkedNow = IsChecked;

		SetClass( "heading", IsHeading );
		SetClass( "checked", checkedNow );
		SetClass( "disabled", !enabled );

		//Here rather than in the sheet: a row that is placed by hand carries styles of its own, and those beat anything the sheet has to say.
		var pointer = enabled ? PointerEvents.All : PointerEvents.None;

		if ( !inert && Style.PointerEvents != pointer )
			Style.PointerEvents = pointer;

		if ( glyph.IsValid() )
			glyph.Text = checkedNow ? "check" : Icon ?? string.Empty;
	}

	//Nothing on a menu on its way out is worth pointing at.
	internal void MakeInert()
	{
		inert = true;

		Style.PointerEvents = PointerEvents.None;
	}

	/// <summary>An option whose state lives somewhere else can change while the menu is open, and the row that was painted every frame kept up on its own.</summary>
	public override void Tick()
	{
		base.Tick();

		Sync();
	}

	//Hover opens the row's submenu and closes siblings'; fires repeatedly, so an open one is left alone
	protected override void OnMouseOver( MousePanelEvent e )
	{
		base.OnMouseOver( e );

		if ( inert ) return;

		menu.CloseSubmenusExcept( submenu );

		OpenSubmenu();
	}

	//Opens the submenu beside the row, unless it is already there.
	private void OpenSubmenu()
	{
		if ( submenu is null || submenu.IsOpen ) return;

		submenu.Owner = menu;
		submenu.OpenAt( menu.Position + new Vector2( wide, top ) );
	}

	protected override void OnClick( MousePanelEvent e )
	{
		base.OnClick( e );

		e.StopPropagation();

		if ( IsSeparator || IsHeading ) return;

		if ( submenu is not null )
		{
			OpenSubmenu();

			return;
		}

		if ( option is null ) return;
		if ( !option.Enabled ) return;

		option.Choose();

		Sync();

		Menu.CloseAllPanels();
	}

	//The option's picture as something a panel can wear, or null.
	private Texture Picture()
	{
		if ( option?.RowIconImage?.picture is not { IsValid: true } bitmap ) return null;
		if ( option is { Checkable: true } ) return null;

		if ( pictures.TryGetValue( bitmap, out var known ) && known is { IsValid: true } )
			return known;

		var made = bitmap.ToTexture( mips: false );

		pictures.Remove( bitmap );
		pictures.Add( bitmap, made );

		return made;
	}
}
