using System;

namespace Editor;

//What one tab of an EditorTabStrip shows and does: an open scene, or a document in an editor
internal interface IEditorTab
{
	string Title { get; }

	//A material icon, or null for none
	string Icon { get; }

	//The line along the top and the icon, where not the usual colour - a playing scene's green
	Color? Accent { get; }

	bool Unsaved { get; }

	void Select();

	//Asks to close; the owner takes the tab away once it really has
	void Close();
}

//The row of tabs over a central view, shared by the scene tabs and every editor's documents
internal sealed class EditorTabStrip : Widget
{
	readonly List<TabWidget> _tabs = new();
	IEditorTab _current;
	Widget _trailing;

	/// <summary>Run with the new order after the user drags a tab along.</summary>
	public Action<IReadOnlyList<IEditorTab>> Reordered;

	public EditorTabStrip( Widget parent ) : base( parent )
	{
		Layout = Layout.Row();
		FixedHeight = 26;
	}

	/// <summary>What stands after the tabs, such as the scene's new-scene button.</summary>
	public Widget Trailing
	{
		get => _trailing;
		set
		{
			_trailing = value;
			Rebuild();
		}
	}

	/// <summary>The tabs shown, in order.</summary>
	public IReadOnlyList<IEditorTab> Items => _tabs.Select( x => x.Item ).ToList();

	/// <summary>Shows these tabs in this order, keeping the widgets of the ones already shown.</summary>
	public void SetItems( IEnumerable<IEditorTab> items )
	{
		var wanted = items.ToList();

		foreach ( var gone in _tabs.Where( x => !wanted.Contains( x.Item ) ).ToList() )
		{
			_tabs.Remove( gone );
			gone.Destroy();
		}

		var next = wanted.Select( item => _tabs.FirstOrDefault( x => x.Item == item ) ?? new TabWidget( this, item ) ).ToList();

		_tabs.Clear();
		_tabs.AddRange( next );

		Rebuild();
	}

	/// <summary>Marks the tab in front; every tab repaints, since each paints itself.</summary>
	public void SetCurrent( IEditorTab item )
	{
		_current = item;

		foreach ( var tab in _tabs )
			tab.Update();
	}

	internal bool IsCurrent( IEditorTab item ) => _current == item;

	void Rebuild()
	{
		Layout.Clear( false );

		foreach ( var tab in _tabs )
			Layout.Add( tab );

		if ( _trailing.IsValid() )
		{
			Layout.AddSpacingCell( 2 );
			Layout.Add( _trailing );
		}

		Layout.AddStretchCell();
		Layout.AddSpacingCell( 4 );

		Update();
	}

	//Moves a dragged tab to where its middle is now
	void Reorder( TabWidget tab, float centerX )
	{
		var from = _tabs.IndexOf( tab );
		if ( from < 0 ) return;

		var to = _tabs.Count( x => x != tab && centerX > x.ScreenPosition.x + x.Width * 0.5f );
		if ( to == from ) return;

		_tabs.RemoveAt( from );
		_tabs.Insert( to, tab );

		Rebuild();
		Reordered?.Invoke( Items );
	}

	protected override void OnPaint()
	{
		//Nothing behind the tabs: the strip is part of the window, and the window is glass
	}

	sealed class TabWidget : Widget
	{
		public IEditorTab Item { get; }

		readonly EditorTabStrip _owner;
		readonly IconButton _close;
		Vector2 _dragStart;
		float _dragOffset;
		bool _dragPressed;
		(string Text, string Icon, Color? Accent) _painted;

		bool IsCurrent => _owner.IsCurrent( Item );
		string Shown => Item.Unsaved ? $"{Item.Title} *" : Item.Title;
		float TextLeft => Item.Icon is null ? 10 : 29;

		public override bool UsesCanvasDraw => true;

		public TabWidget( EditorTabStrip owner, IEditorTab item ) : base( owner )
		{
			_owner = owner;
			Item = item;
			Cursor = CursorShape.Finger;

			_close = new IconButton( "close", Item.Close, this )
			{
				ToolTip = "Close",
				Background = Color.Transparent,
				Foreground = Theme.TextLight,
				IconSize = 8,
				FixedWidth = 16,
				FixedHeight = 16
			};
		}

		protected override Vector2 SizeHint()
		{
			Paint.SetDefaultFont( weight: 500 );
			return new Vector2( Paint.MeasureText( Shown ).x + TextLeft + 29, 26 );
		}

		protected override void OnPaint()
		{
			var hovered = Paint.HasMouseOver;
			var accent = Item.Accent;
			var background = IsCurrent ? Theme.TabBackground : hovered ? Theme.Slab : Theme.TabInactiveBackground;
			var round = Theme.TabRadius;

			//Round along the top, square where it meets the view under it
			Paint.ClearPen();
			Paint.SetBrush( background );
			Paint.DrawRect( new Rect( 0, 0, Width, Height ), round, round, 0.0f, 0.0f );

			if ( IsCurrent || accent is not null )
			{
				//Inside the corners, so the line does not cut across them
				Paint.SetBrush( accent ?? Theme.EdgeLit );
				Paint.DrawRect( new Rect( round, 0, Width - (round * 2.0f), 2 ) );
			}

			var color = IsCurrent || hovered ? Theme.Text : Theme.TextLight;

			if ( Item.Icon is { } icon )
			{
				Paint.SetPen( accent ?? color );
				Paint.DrawIcon( new Rect( 8, 0, 16, Height ), icon, 13 );
			}

			_painted = (Shown, Item.Icon, accent);

			Paint.SetPen( color );
			Paint.SetDefaultFont( weight: 500 );
			Paint.DrawText( new Rect( TextLeft, 0, Width - TextLeft - 23, Height ), _painted.Text, TextFlag.LeftCenter );
		}

		//Drawn and measured again when the title, the star or the play state moves
		[EditorEvent.Frame]
		void WatchItem()
		{
			if ( _painted.Text is null || _painted == (Shown, Item.Icon, Item.Accent) ) return;

			UpdateGeometry();
			Update();
		}

		protected override void OnResize()
		{
			_close.Position = new Vector2( Width - 20, (Height - _close.Height) * 0.5f + 1 );
		}

		protected override void OnMouseEnter()
		{
			base.OnMouseEnter();
			Update();
		}

		protected override void OnMouseLeave()
		{
			base.OnMouseLeave();
			Update();
		}

		protected override void OnMousePress( MouseEvent e )
		{
			base.OnMousePress( e );

			if ( e.MiddleMouseButton )
			{
				e.Accepted = true;
				Item.Close();
				return;
			}

			//The close button acts on release, so its press falls through to here and would bring up what is being closed
			if ( _close.IsValid() && new Rect( _close.Position, _close.Size ).IsInside( e.LocalPosition ) )
				return;

			if ( e.LeftMouseButton )
			{
				_dragStart = e.ScreenPosition;
				_dragOffset = e.LocalPosition.x;
				_dragPressed = true;
				Item.Select();
			}
		}

		protected override void OnMouseMove( MouseEvent e )
		{
			base.OnMouseMove( e );

			if ( !_dragPressed || (e.ButtonState & MouseButtons.Left) == 0 ) return;
			if ( _dragStart.Distance( e.ScreenPosition ) <= 6.0f ) return;

			_owner.Reorder( this, e.ScreenPosition.x - _dragOffset + Width * 0.5f );
		}

		protected override void OnMouseReleased( MouseEvent e )
		{
			base.OnMouseReleased( e );

			if ( e.LeftMouseButton )
				_dragPressed = false;
		}

		protected override void OnContextMenu( ContextMenuEvent e )
		{
			var menu = new ContextMenu( this );
			var items = _owner.Items;
			var index = items.ToList().IndexOf( Item );

			menu.AddOption( "Close", null, Item.Close );
			AddCloseOption( menu, "Close Others", items.Where( x => x != Item ) );
			AddCloseOption( menu, "Close Tabs to the Left", items.Take( index ) );
			AddCloseOption( menu, "Close Tabs to the Right", items.Skip( index + 1 ) );

			menu.OpenAtCursor();
			e.Accepted = true;
		}

		static void AddCloseOption( ContextMenu menu, string title, IEnumerable<IEditorTab> items )
		{
			var closable = items.ToArray();
			if ( closable.Length == 0 ) return;

			menu.AddOption( title, null, () =>
			{
				foreach ( var item in closable )
					item.Close();
			} );
		}
	}
}
