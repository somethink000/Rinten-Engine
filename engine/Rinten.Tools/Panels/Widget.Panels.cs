using Rinten;
using Rinten.UI;
using System.Collections.Generic;
using System.Linq;

namespace Editor;

/// <summary>A widget that is a panel of ours.</summary>
public partial class Widget
{
	internal WidgetSurface Surface { get; private set; }

	//Whether this widget is one of ours.
	internal bool OnPanels => Surface is not null;

	private Vector2 position;
	private Vector2 size = new( 100, 30 );
	private bool visible = true;
	private bool enabled = true;
	private bool hovered;

	private Widget parent;
	internal readonly List<Widget> childWidgets = new();

	private Margin contentMargins;
	private string widgetName;

	//How big this widget may get. A widget with nothing said about it may get as big as anything - which is what own default means.
	private Vector2 maximumSize = new( 1000000, 1000000 );

	//Makes the panel this widget is, and puts it under its parent's.
	internal void MakeSurface( Widget parent )
	{
		Rinten.Engine.GlobalContext.Current.UISystem ??= new Rinten.UISystem();

		var first = Surface is null;

		if ( Surface is not null )
		{
			Surface.Parent = null;
			Surface.Delete( true );
			Surface = null;
		}

		Surface = new WidgetSurface( this );

		//A new panel must re-learn whether it accepts focus, or the widget's shortcuts stop working
		Surface.AcceptsFocus = focusMode != FocusMode.None;

		if ( first )
			EditorEvent.Register( this );

		SetPanelParent( parent );

		OnSurfaceCreated();
	}

	//Panel tree root for child panels and CSS chrome.
	protected Panel ContentPanel => Surface;

	/// <summary>Syncs panel children after the surface exists; called from tick and burst paint.</summary>
	public virtual void SyncSurfaceContent()
	{
	}

	/// <summary>Whether this widget still bakes canvas paint: no children, or an assigned OnPaintOverride</summary>
	public virtual bool UsesCanvasDraw =>
		Surface is not null
		&& SurfaceTexture is not { IsValid: true }
		&& ( !Surface.HasChildren || OnPaintOverride is not null );

	//Panel-backed widgets create child panels when their surface is made.
	private protected virtual void OnSurfaceCreated()
	{
	}

	//Size was set by a layout, so it isn't read back as a request (which would ratchet)
	internal bool SizeFromLayout;

	//Whether this widget's layout has to be worked out again before it is next drawn.
	private bool needsArrange = true;

	//Asks for a layout pass, this frame - and of everything this is inside.
	internal void MarkNeedsArrange()
	{
		if ( FlexSettling > 0 ) return;

		for ( var widget = this; widget is not null; widget = widget.Parent )
		{
			widget.needsArrange = true;

			//And the tick that acts on it. A surface that has opted out of the per-frame poll - which is nearly all of
			//them, see WidgetSurface.TicksEveryFrameOfItsOwn - would otherwise carry the flag for ever and never arrange.
			widget.Surface?.RequestTick();
		}
	}

	//Puts everything inside this widget where it goes, if anything asked. Called once a frame for every widget, parents
	//before children - so a layout is always worked out against a rectangle its own widget has already been given.
	internal void ArrangeIfNeeded()
	{
		if ( !needsArrange ) return;

		needsArrange = false;

		OnArrange();
	}

	//Max layout passes while rebuilding before giving up.
	internal const int MaxArrangePasses = 8;

	//Panel settle passes used while flex layout converges.
	internal const int FlexPanelSettlePasses = 1;

	//Depth of SettleFlexSubtree; suppresses arrange feedback while settling.
	internal static int FlexSettling { get; private set; }

	//Flex layout: sync styles, settle the root panel, read geometry back.
	internal (int Passes, bool Stable) SettleFlexSubtree()
	{
		var settleRoot = Surface?.FindRootPanel();

		MarkSubtreeNeedsArrange();

		FlexSettling++;

		try
		{
			for ( int i = 0; i < MaxArrangePasses; i++ )
			{
				if ( i > 0 )
					MarkSubtreeNeedsArrange();

				ArrangeDirtySubtreePass();

				PushGeometryToPanelsSubtree();

				if ( settleRoot is not null && SubtreeUsesFlex() )
				{
					for ( int j = 0; j < FlexPanelSettlePasses; j++ )
						settleRoot.SettleLayout();
				}

				SyncFlexGeometrySubtree();

				if ( !SubtreeNeedsArrange() )
					return ( i + 1, true );
			}

			return ( MaxArrangePasses, !SubtreeNeedsArrange() );
		}
		finally
		{
			FlexSettling--;
		}
	}

	bool SubtreeUsesFlex()
	{
		if ( InFlex )
			return true;

		foreach ( var child in childWidgets )
		{
			if ( child.IsValid() && child.SubtreeUsesFlex() )
				return true;
		}

		return false;
	}

	void PushGeometryToPanelsSubtree()
	{
		if ( Surface is WidgetSurface surface )
			surface.PushGeometryToPanel();

		foreach ( var child in childWidgets )
		{
			if ( child.IsValid() )
				child.PushGeometryToPanelsSubtree();
		}
	}

	void SyncFlexGeometrySubtree()
	{
		SyncFlexGeometryFromSurface();

		foreach ( var child in childWidgets )
		{
			if ( child.IsValid() )
				child.SyncFlexGeometrySubtree();
		}
	}

	void SyncFlexGeometryFromSurface()
	{
		if ( !InFlex || Surface is not WidgetSurface surface || !surface.IsValid() ) return;

		var scale = surface.ScaleFromScreen;
		var mine = surface.Box.Rect;
		var around = surface.Parent?.Box.Rect.Position ?? Vector2.Zero;

		TakeGeometry( (mine.Position - around) * scale, mine.Size * scale );
	}

	void MarkSubtreeNeedsArrange()
	{
		needsArrange = true;

		foreach ( var child in childWidgets )
		{
			if ( child.IsValid() )
				child.MarkSubtreeNeedsArrange();
		}
	}

	void ArrangeDirtySubtreePass()
	{
		if ( needsArrange )
			ArrangeIfNeeded();

		foreach ( var child in childWidgets )
		{
			if ( child.IsValid() )
				child.ArrangeDirtySubtreePass();
		}
	}

	bool SubtreeNeedsArrange()
	{
		if ( needsArrange )
			return true;

		foreach ( var child in childWidgets )
		{
			if ( child.IsValid() && child.SubtreeNeedsArrange() )
				return true;
		}

		return false;
	}

	//Paints every visible descendant synchronously.
	internal void PaintSubtree()
	{
		if ( !IsValid || !Visible ) return;

		if ( Surface is WidgetSurface surface )
			surface.EnsurePainted();

		foreach ( var child in childWidgets )
		{
			if ( child.IsValid() )
				child.PaintSubtree();
		}
	}

	//Once a frame, for a widget that has something of its own to keep up - scrolling that is still settling, an animation, a timer.
	private protected virtual void OnWidgetFrame()
	{
	}

	//The frame, from the panel in front of the widget.
	internal void Frame() => OnWidgetFrame();

	//Asks for this widget's frame - its OnWidgetFrame and its SyncSurfaceContent - on the next tick, without asking for a paint. What a widget
	//whose frames are asked for calls when it has work, and again from its frame for as long as it still has. See FramesOnDemandAttribute.
	private protected void RequestFrame() => Surface?.RequestTick();

	//A GPU texture worn as-is instead of painting (scene views); null for most widgets
	internal virtual Texture SurfaceTexture => null;

	//Whether a layout put this widget in a flex container, and so whether it is where flex put it rather than where a layout said.
	internal bool InFlex;

	//Takes flex's rectangle as this widget's geometry, via fields since setters mean a size request
	internal void TakeGeometry( Vector2 at, Vector2 measured )
	{
		if ( position == at && size == measured ) return;

		var resized = size != measured;

		position = at;
		size = measured;

		//Leave SizeFromLayout alone here, or widgets that asked for a size ask for nothing next frame

		if ( FlexSettling == 0 )
			MarkNeedsArrange();

		if ( resized ) OnResize();

		UpdateGeometry();
		Update();
	}

	//Arranges contents inside the content margins rather than the whole box
	private protected virtual void OnArrange()
	{
		//Content margins are room around the whole widget, handed over as padding.
		Layout?.Flex( Surface, ContentMargins );

		//Widgets that place their own children (line edit, slider, axis square) still do that here, after the layout has given them a rectangle.
		DoLayout();
	}

	//Moves this widget - and the panel in front of it - under another.
	internal void SetPanelParent( Widget wanted )
	{
		if ( parent == wanted ) return;

		parent?.childWidgets.Remove( this );

		if ( wanted is not { IsValid: true } )
		{
			parent = null;

			if ( Surface is not null ) Surface.Parent = null;
			return;
		}

		parent = wanted;
		parent.childWidgets.Add( this );

		if ( Surface is null || parent.Surface is null ) return;
		if ( !Surface.IsValid || !parent.Surface.IsValid ) return;

		parent.Surface.AddChild( Surface );
	}

	//The panel goes with the widget it was the front of.
	internal void TakeDownSurface()
	{
		SetPanelParent( null );

		if ( Surface is null ) return;

		Surface.Delete( true );
		Surface = null;
	}

	//A property is a class on the panel, so a stylesheet can pick the widget out by it the way could.
	private protected override void OnPropertySet( string name )
	{
		var value = GetManagedProperty( name );

		if ( Surface is null || string.IsNullOrEmpty( value ) ) return;

		Surface.AddClass( $"{name}-{value}" );
	}

	//Draws the widget into the picture its panel wears. False where nothing was drawn at all, so the panel can wear nothing instead.
	internal bool PaintInto( Bitmap bitmap, Rect rect )
	{
		var surface = new PaintCanvas( bitmap );

		var flags = StateFlag.None;

		if ( enabled ) flags |= StateFlag.Enabled;
		if ( hovered ) flags |= StateFlag.MouseOver;
		if ( IsPressed ) flags |= StateFlag.Sunken;
		if ( IsFocused ) flags |= StateFlag.HasFocus;

		Paint.Snapshot = true;

		try
		{
			using ( Paint.Start( surface, flags, DpiScale ) )
			{
				Paint.LocalRect = rect;

				if ( OnPaintOverride is not null && OnPaintOverride() )
				{
					return surface.Drew;
				}

				OnPaint();
			}
		}
		finally
		{
			Paint.Snapshot = false;
		}

		return surface.Drew;
	}


	//Whether this widget saw the press that is still down.
	private bool panelPressed;

	internal void PanelMousePress( MouseEvent e )
	{
		panelPressed = true;

		//The innermost widget is the one that looks pressed - this is what draws a button sunken, and only one of them should be
		if ( !CurrentlyPressedWidget.IsValid() )
		{
			CurrentlyPressedWidget = this;
		}

		OnMousePress( e );

		Update();
	}

	internal void PanelMouseRelease( MouseEvent e )
	{
		var pressed = panelPressed;

		panelPressed = false;

		if ( CurrentlyPressedWidget == this ) CurrentlyPressedWidget = null;

		OnMouseReleased( e );

		if ( pressed && LocalRect.IsInside( e.LocalPosition ) )
		{
			if ( e.RightMouseButton )
			{
				OnMouseRightClick( e );

				var menu = new ContextMenuEvent
				{
					LocalPosition = e.LocalPosition,
					ScreenPosition = e.ScreenPosition
				};

				OnContextMenu( menu );
			}
			else if ( e.LeftMouseButton )
			{
				//The left button, rather than everything that is not the right one. A middle click used to arrive
				//here as a click, and so did any button the surface could not name - which was all of them but two.
				OnMouseClick( e );
			}
		}

		Update();
	}

	internal void PanelMouseMove( MouseEvent e )
	{
		OnMouseMove( e );
	}

	internal void PanelDoubleClick( MouseEvent e )
	{
		OnDoubleClick( e );
	}

	internal void PanelWheel( ref WheelEvent e )
	{
		OnMouseWheel( e );

		Update();
	}

	internal void PanelKeyPress( KeyEvent e )
	{
		OnKeyPress( e );

		Update();
	}

	//The key came up, when it actually did. The release used to be sent along with the press, because the window
	//system's key-ups were not being read at all. A key that is up as soon as it is down is a key nothing can hold.
	internal void PanelKeyRelease( KeyEvent e )
	{
		OnKeyRelease( e );

		Update();
	}

	internal void PanelMouseEnter()
	{
		hovered = true;

		OnMouseEnter();
	}

	internal void PanelMouseLeave()
	{
		hovered = false;

		OnMouseLeave();
	}
}
