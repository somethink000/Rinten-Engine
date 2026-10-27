using Rinten.UI.Construct;
namespace Rinten.UI;

public partial class Popup : BasePopup
{
	/// <summary>Which panel triggered this popup.</summary>
	public Panel PopupSource { get; set; }

	/// <summary>Currently selected option in the popup.</summary>
	public Panel SelectedChild { get; set; }

	/// <summary>Positioning mode for this popup.</summary>
	public PositionMode Position { get; set; }

	/// <summary>Offset away from <see cref="PopupSource"/> based on <see cref="Position"/>.</summary>
	public float PopupSourceOffset { get; set; }

	/// <summary>Closes when <see cref="PopupSource"/> is hidden.</summary>
	public bool CloseWhenParentIsHidden { get; set; } = false;

	Rect SourceScreenRect() => PopupSource.GetScreenPixelRect();

	Vector2 WantedBelowScreen()
	{
		var sourcePixel = SourceScreenRect();
		return new Vector2( sourcePixel.Left, sourcePixel.Bottom + PopupSourceOffset );
	}

	Rect ClampBounds()
	{
		var root = FindRootPanel();
		if ( root is not null )
			return root.PanelBounds;

		return new Rect( 0, 0, Screen.Width, Screen.Height );
	}

	void ClampLayoutRect( ref Rect layoutRect )
	{
		var bounds = ClampBounds();
		var padding = 10;
		var maxX = bounds.Right - padding;
		var maxY = bounds.Bottom - padding;

		if ( layoutRect.Bottom > maxY )
			layoutRect.Top -= layoutRect.Bottom - maxY;

		if ( layoutRect.Right > maxX )
			layoutRect.Left -= layoutRect.Right - maxX;

		if ( layoutRect.Left < bounds.Left )
			layoutRect.Left = bounds.Left;
	}

	void StyleBelowStretch()
	{
		if ( PopupSource is null || !PopupSource.IsValid() ) return;

		//All in style units, which is what the overlay places in and the style is written in
		var source = PopupSource.GetScreenStyleRect();
		var parent = Parent?.GetScreenStyleRect() ?? default;
		var wanted = new Vector2( source.Left, source.Bottom + PopupSourceOffset );
		var popupSize = new Vector2( source.Width, Box.Rect.Height > 0 ? Box.Rect.Height * ScaleFromScreen : 1 );
		var placed = PopupSource.PlaceOverlayPopup( wanted, popupSize );

		Style.Right = null;
		Style.Bottom = null;
		Style.Left = placed.x - parent.Left;
		Style.Top = placed.y - parent.Top;
		Style.Width = source.Width;
	}

	public enum PositionMode
	{
		Left,
		Right,
		LeftBottom,
		RightBottom,
		AboveLeft,
		AboveRight,
		BelowLeft,
		BelowCenter,
		BelowRight,
		BelowStretch,
		AboveCenter,
		UnderMouse
	}

	public Popup()
	{

	}

	public Popup( Panel sourcePanel, PositionMode position, float offset )
	{
		SetPositioning( sourcePanel, position, offset );
	}

	public void SetPositioning( Panel sourcePanel, PositionMode position, float offset )
	{
		var scrollMount = sourcePanel.FindScrollPopupMount();
		var overlay = sourcePanel.FindOverlayPanel();
		var useOverlay = overlay.IsValid() && overlay.HasClass( "editor-overlay" );
		Parent = scrollMount ?? (useOverlay ? overlay : sourcePanel.FindOverlayPanel());

		if ( scrollMount is not null && Parent == scrollMount || useOverlay && Parent == overlay )
		{
			Style.ZIndex = 3000;
			Parent?.SetChildIndex( this, Parent.ChildrenCount - 1 );
		}

		PopupSource = sourcePanel;
		AnchorPanel = sourcePanel;
		Position = position;
		PopupSourceOffset = offset;

		AddClass( "popup-panel" );

		PositionMe( true );

		switch ( Position )
		{
			case PositionMode.Left:
				AddClass( "left" );
				break;

			case PositionMode.Right:
				AddClass( "right" );
				break;

			case PositionMode.LeftBottom:
				AddClass( "left-bottom" );
				break;

			case PositionMode.RightBottom:
				AddClass( "right-bottom" );
				break;

			case PositionMode.AboveLeft:
				AddClass( "above-left" );
				break;

			case PositionMode.AboveCenter:
				AddClass( "above-center" );
				break;

			case PositionMode.AboveRight:
				AddClass( "above-right" );
				break;

			case PositionMode.BelowLeft:
				AddClass( "below-left" );
				break;

			case PositionMode.BelowCenter:
				AddClass( "below-center" );
				break;

			case PositionMode.BelowRight:
				AddClass( "below-right" );
				break;

			case PositionMode.BelowStretch:
				AddClass( "below-stretch" );
				break;
		}
	}

	protected Panel Header;
	protected Label TitleLabel;
	protected IconPanel IconPanel;

	void CreateHeader()
	{
		if ( Header.IsValid() ) return;

		Header = Add.Panel( "header" );

		IconPanel = Header.Add.Icon( null );
		TitleLabel = Header.Add.Label( null, "title" );
	}

	public string Title
	{
		get => TitleLabel?.Text;
		set
		{
			CreateHeader();
			TitleLabel.Text = value;
		}
	}

	public string Icon
	{
		get => IconPanel?.Text;
		set
		{
			CreateHeader();
			IconPanel.Text = value;
		}
	}

	public void Success()
	{
		AddClass( "success" );
		Popup.CloseAll();
	}

	public void Failure()
	{
		AddClass( "failure" );
		Popup.CloseAll();
	}

	public Panel AddOption( string text, Action action = null )
	{
		return AddChild( new Button( text, () =>
		{
			Delete();
			action?.Invoke();
		} ) );
	}

	public Panel AddOption( string text, string icon, Action action = null )
	{
		return AddChild( new Button( text, icon, () =>
		{
			Delete();
			action?.Invoke();
		} ) );
	}

	public void MoveSelection( int dir )
	{
		var currentIndex = GetChildIndex( SelectedChild );

		if ( currentIndex >= 0 ) currentIndex += dir;
		else if ( currentIndex < 0 ) currentIndex = dir == 1 ? 0 : -1;

		SelectedChild?.SetClass( "active", false );
		SelectedChild = GetChild( currentIndex, true );
		SelectedChild?.SetClass( "active", true );
	}

	public override void Tick()
	{
		base.Tick();

		if ( CloseWhenParentIsHidden && !PopupSource.IsValid() )
		{
			Delete();
			return;
		}

		PositionMe( false );
	}

	public override void OnLayout( ref Rect layoutRect )
	{
		if ( Position == PositionMode.BelowStretch && PopupSource?.IsValid() == true )
			layoutRect.Width = SourceScreenRect().Width;

		ClampLayoutRect( ref layoutRect );
	}

	void PositionMe( bool isInitial )
	{
		var onOverlay = Parent.IsValid() && Parent.HasClass( "editor-overlay" );

		if ( Position == PositionMode.BelowStretch )
		{
			//The style setters dirty on change, so a popup that did not move dirties nothing.
			StyleBelowStretch();
			return;
		}

		var layoutScale = onOverlay && Parent.IsValid() ? Parent.ScaleFromScreen : 1.0f;
		var rect = AnchorInParent( PopupSource, Parent, onOverlay );
		var parentRect = ParentBounds( Parent );

		//Right and Bottom are measured from the parent's edges, so the parent is what they have to be worked
		//out against - the screen is only the same thing when the popup happens to hang off the root panel.
		var w = parentRect.Width;
		var h = parentRect.Height;

		Style.MaxHeight = Screen.Height - 50;

		switch ( Position )
		{
			case PositionMode.Left:
				{
					Style.Left = null;
					Style.Right = ((w - rect.Left * layoutScale) + PopupSourceOffset);
					Style.Top = (rect.Top + rect.Height * 0.5f) * layoutScale;
					break;
				}
			case PositionMode.Right:
				{
					Style.Right = null;
					Style.Left = (rect.Right + PopupSourceOffset) * layoutScale;
					Style.Top = (rect.Top + rect.Height * 0.5f) * layoutScale;
					break;
				}
			case PositionMode.RightBottom:
				{
					Style.Right = null;
					Style.Left = (rect.Right + PopupSourceOffset) * layoutScale;
					Style.Top = null;
					Style.Bottom = (h - rect.Bottom * layoutScale);
					break;
				}
			case PositionMode.LeftBottom:
				{
					Style.Left = null;
					Style.Right = ((w - rect.Left * layoutScale) + PopupSourceOffset);
					Style.Top = null;
					Style.Bottom = (h - rect.Bottom * layoutScale);
					break;
				}

			case PositionMode.AboveLeft:
				{
					Style.Left = rect.Left * layoutScale;
					Style.Bottom = parentRect.Height - rect.Top * layoutScale + PopupSourceOffset;
					break;
				}

			case PositionMode.AboveCenter:
				{
					Style.Left = (rect.Left + rect.Width * 0.5f) * layoutScale;
					Style.Bottom = parentRect.Height - rect.Top * layoutScale + PopupSourceOffset;
					break;
				}

			case PositionMode.AboveRight:
				{
					Style.Left = null;
					Style.Right = parentRect.Width - rect.Right * layoutScale;
					Style.Bottom = parentRect.Height - rect.Top * layoutScale + PopupSourceOffset;
					break;
				}

			case PositionMode.BelowLeft:
				{
					Style.Left = rect.Left * layoutScale;
					Style.Top = (rect.Bottom + PopupSourceOffset) * layoutScale;
					break;
				}

			case PositionMode.BelowCenter:
				{
					Style.Left = rect.Center.x * layoutScale;
					Style.Top = (rect.Bottom + PopupSourceOffset) * layoutScale;
					break;
				}

			case PositionMode.BelowRight:
				{
					Style.Left = null;
					Style.Right = parentRect.Width - rect.Right * layoutScale;
					Style.Top = (rect.Bottom + PopupSourceOffset) * layoutScale;
					break;
				}

			case PositionMode.UnderMouse:
				{
					if ( isInitial )
					{
						//Left and Top are the parent's, and the cursor is the window's - a popup hung
						//off a panel partway down the tree lands offset by wherever that panel sits.
						var scale = onOverlay ? layoutScale : PopupSource.ScaleFromScreen;
						var at = ( Mouse.Position - (Parent.IsValid() ? Parent.Box.Rect.Position : Vector2.Zero) ) * scale;

						Style.Left = at.x;
						Style.Top = at.y + PopupSourceOffset;
					}
					break;
				}
		}

		//Left, Right, Top, Bottom and MaxHeight are setters that dirty the style only when the value changed.
	}

	/// <summary>Re-runs overlay positioning after content was added.</summary>
	public void SyncAnchor()
	{
		PositionMe( true );
	}

	static Rect AnchorInParent( Panel source, Panel parent, bool overlayCoords )
	{
		if ( !parent.IsValid() )
		{
			var anchor = overlayCoords ? source.GetOverlayAnchorRect() : source.Box.Rect;
			return overlayCoords ? anchor : anchor * source.ScaleFromScreen;
		}

		if ( overlayCoords )
		{
			var anchor = source.GetOverlayAnchorRect();
			var origin = parent.Box.Rect.Position;
			return new Rect(
				anchor.Left - origin.x,
				anchor.Top - origin.y,
				anchor.Width,
				anchor.Height );
		}

		var sourcePixel = source.GetScreenPixelRect();
		var parentPixel = parent.GetScreenPixelRect();
		var scale = source.ScaleFromScreen;

		return new Rect(
			( sourcePixel.Left - parentPixel.Left ) * scale,
			( sourcePixel.Top - parentPixel.Top ) * scale,
			sourcePixel.Width * scale,
			sourcePixel.Height * scale );
	}

	//In style units: the anchor's pixels are scaled into them where they are used
	static Rect ParentBounds( Panel parent )
	{
		if ( !parent.IsValid() )
			return default;

		var box = parent.Box.Rect;
		var scale = parent.ScaleFromScreen;
		return new Rect( 0, 0, box.Width * scale, box.Height * scale );
	}
}
