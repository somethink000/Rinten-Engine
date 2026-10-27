using Rinten.UI;

namespace Editor;

/// <summary>Top layer of a window for menus, tooltips, drag previews and dialogs; ignores the pointer itself</summary>
public sealed class EditorOverlay : Panel
{
	/// <summary>Above everything else in the window, and out of the pointer's way.</summary>
	public EditorOverlay()
	{
		AddClass( "editor-overlay" );
	}

	/// <summary>The overlay already on this panel's window, or null.</summary>
	public static EditorOverlay Find( Panel panel )
	{
		EnsurePopupHooks();

		var root = panel?.FindRootPanel();

		if ( root is null ) return null;

		foreach ( var child in root.Children )
		{
			if ( child is EditorOverlay found ) return found;
		}

		return null;
	}

	/// <summary>The layer of the window this panel is in, making one if there is not one yet. Null where the panel is not in a window at all.</summary>
	public static EditorOverlay For( Panel panel )
	{
		EnsurePopupHooks();

		var found = Find( panel );

		if ( found is not null ) return found;

		var root = panel?.FindRootPanel();

		if ( root is null ) return null;

		var layer = new EditorOverlay();

		root.AddChild( layer );

		return layer;
	}

	static bool popupHooksRegistered;

	static EditorOverlay()
	{
		EnsurePopupHooks();
	}

	static void EnsurePopupHooks()
	{
		if ( popupHooksRegistered ) return;

		popupHooksRegistered = true;
		Panel.ResolveOverlayPanelHandler = ResolveOverlayForPanel;
		Panel.OverlayAnchorRectHandler = GetOverlayAnchorRect;
		Panel.OverlayPlaceHandler = PlaceOnOverlay;
		Panel.PopupPressedHandler = Menu.PressedOnPanel;
	}

	static Panel ResolveOverlayForPanel( Panel panel )
	{
		for ( var p = panel; p is not null; p = p.Parent )
		{
			if ( p is WidgetSurface surface && surface.Widget.IsValid() )
				return For( surface.Widget );
		}

		return For( panel );
	}

	static Vector2 PlaceOnOverlay( Panel source, Vector2 wanted, Vector2 size )
	{
		var layer = ResolveOverlayForPanel( source );

		if ( layer is EditorOverlay overlay )
			return overlay.Place( wanted, size );

		return wanted;
	}

	//Overlay pixels for a panel, widget surfaces included: one window, one pixel space.
	static Rect GetOverlayAnchorRect( Panel source ) => source.GetScreenPixelRect();

	/// <summary>The overlay already on this widget's window, or null.</summary>
	public static EditorOverlay Find( Widget widget )
	{
		for ( var w = widget; w is not null; w = w.Parent )
		{
			if ( w.Surface is null ) continue;

			var layer = Find( (Panel)w.Surface );

			if ( layer is not null ) return layer;
		}

		return null;
	}

	/// <summary>Overlay of the window under the pointer, else the first open one; for static callers with no panel</summary>
	public static EditorOverlay ForActiveWindow()
	{
		if ( Window.UnderPointer is { Shell: { } pointed } )
		{
			if ( For( pointed.Root ) is { } layer ) return layer;
		}

		foreach ( var window in Window.All )
		{
			if ( window?.Shell?.Root is not { } root ) continue;

			if ( For( root ) is { } layer ) return layer;
		}

		return null;
	}

	/// <summary>The layer of the window this widget is in.</summary>
	public static EditorOverlay For( Widget widget )
	{
		for ( var w = widget; w is not null; w = w.Parent )
		{
			if ( w.Surface is null ) continue;

			var layer = For( (Panel)w.Surface );

			if ( layer is not null ) return layer;
		}

		return null;
	}

	/// <summary>How big the layer is, in the units a widget is placed in.</summary>
	public Vector2 Bounds => Box.Rect.Size * ScaleFromScreen;

	/// <summary>Position that keeps a box of this size inside bounds; pinned top-left when it can't fit</summary>
	public static Vector2 Place( Vector2 wanted, Vector2 size, Vector2 bounds )
	{
		return new Vector2(
			wanted.x.Clamp( 0.0f, System.MathF.Max( bounds.x - size.x, 0.0f ) ),
			wanted.y.Clamp( 0.0f, System.MathF.Max( bounds.y - size.y, 0.0f ) ) );
	}

	/// <summary>Where something of this size goes on this layer.</summary>
	public Vector2 Place( Vector2 wanted, Vector2 size ) => Place( wanted, size, Bounds );

	//Tooltips. Every widget in the editor has had a ToolTip on it for years and none of them were ever shown - the string was
	//kept and nothing read it. The layer reads it: it is the one thing that is over everything and ticks every frame anyway.

	//How long the pointer rests on something before it is explained.
	private const float Delay = 0.5f;

	//What the pointer is resting on - a Widget or a Panel.
	private object resting;
	private string restingText;
	private RealTimeSince sinceRested;
	private Panel tip;

	public override void Tick()
	{
		base.Tick();

		EditorDrag.Tick( this );

		Tooltips();
	}

	private void Tooltips()
	{
		var (hovered, text) = Resting();

		if ( !ReferenceEquals( hovered, resting ) )
		{
			resting = hovered;
			restingText = text;
			sinceRested = 0;

			Hide();
			return;
		}

		if ( resting is null ) return;
		if ( sinceRested < Delay ) return;

		if ( tip is null ) Show( restingText );

		if ( tip is null ) return;

		//It follows the pointer rather than the widget: on a row of small buttons the widget is barely
		//bigger than the cursor, and a tooltip pinned to the widget lands under the pointer and flickers.
		var size = tip.Box.Rect.Size * ScaleFromScreen;
		var at = Application.CursorPosition + new Vector2( 0, 22 );

		var placed = Place( at, size );

		tip.Style.Left = placed.x;
		tip.Style.Top = placed.y;
	}

	//What the pointer rests on and its tooltip; panels first, since they are deeper than widgets
	private (object Hovered, string Text) Resting()
	{
		for ( var panel = DeepestHovered(); panel is not null; panel = panel.Parent )
		{
			if ( !string.IsNullOrWhiteSpace( panel.Tooltip ) )
				return (panel, panel.Tooltip);
		}

		for ( var widget = WidgetSurface.Hovered; widget is not null; widget = widget.Parent )
		{
			if ( !string.IsNullOrWhiteSpace( widget.ToolTip ) )
				return (widget, widget.ToolTip);
		}

		return (null, null);
	}

	//Deepest hovered panel, from the hovered surface since other windows keep stale hover
	private static Panel DeepestHovered()
	{
		Panel current = WidgetSurface.HoveredSurface;

		//A loop rather than FirstOrDefault: this walks a level per frame and the predicate closes over
		//nothing, but reading Children through IEnumerable still boxes an enumerator at every level.
		while ( current is not null )
		{
			Panel next = null;

			foreach ( var child in current.Children )
			{
				if ( !child.HasHovered ) continue;

				next = child;
				break;
			}

			if ( next is null ) break;

			current = next;
		}

		return current;
	}

	//Puts the explanation on the layer.
	private void Show( string text )
	{
		var markup = LooksLikeMarkup( text );

		if ( !markup ) text = PlainText( text );

		if ( string.IsNullOrWhiteSpace( text ) ) return;

		tip = new Panel();
		tip.AddClass( "tooltip" );

		//Rinten.UI's, not the editor's widget of the same name
		var label = tip.AddChild<Rinten.UI.Label>();
		label.Tokenize = false;
		label.IsRich = markup;
		label.Text = text;

		AddChild( tip );
	}

	//Whether a tooltip is markup: a closing tag or line break, not just a stray less-than
	internal static bool LooksLikeMarkup( string text )
	{
		if ( string.IsNullOrEmpty( text ) ) return false;

		return text.Contains( "</", System.StringComparison.Ordinal )
			|| text.Contains( "<br", System.StringComparison.OrdinalIgnoreCase );
	}

	private void Hide()
	{
		tip?.Delete( true );
		tip = null;
	}

	//A tooltip without its markup. Half of them are written as a scrap of html - <b>name</b><br>what it does - because the toolkit that used to
	//draw them read that. A label here does not, and would show the angle brackets, so the line break is kept and the rest of the tags are dropped.
	internal static string PlainText( string text )
	{
		if ( string.IsNullOrEmpty( text ) ) return text;

		if ( text.IndexOf( '<' ) < 0 ) return text;

		var builder = new System.Text.StringBuilder( text.Length );

		for ( var i = 0; i < text.Length; i++ )
		{
			if ( text[i] != '<' )
			{
				builder.Append( text[i] );
				continue;
			}

			var close = text.IndexOf( '>', i );

			//A stray bracket that opens nothing is a bracket somebody meant.
			if ( close < 0 )
			{
				builder.Append( text[i] );
				continue;
			}

			var tag = text[(i + 1)..close].Trim().ToLowerInvariant();

			if ( tag is "br" or "br/" or "/p" ) builder.Append( '\n' );

			i = close;
		}

		return builder.ToString().Trim();
	}
}
