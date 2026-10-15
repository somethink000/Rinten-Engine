using System;

namespace Editor;

//The editor's central widget. Every open scene and prefab session lives here as a browser style
//tab - sessions are never dock widgets, so they can't be floated, split or lost by layout changes.
sealed class SceneTabWidget : Widget
{
	readonly Widget _tabBar;
	readonly ContentHost _content;
	readonly IconButton _newTab;
	readonly List<SceneTab> _tabs = new();

	SceneTab _currentTab;

	//The tab widgets alive, for the sweep below. There is normally one.
	static readonly List<SceneTabWidget> Living = new();

	//Each frame, hide scene views covered by the current one; hidden ones would still render fully
	[EditorEvent.Frame]
	static void HideCoveredViews()
	{
		for ( var i = Living.Count - 1; i >= 0; i-- )
		{
			var one = Living[i];

			if ( !one.IsValid() ) { Living.RemoveAt( i ); continue; }

			one._content?.HideTheRest();
		}
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		Living.Remove( this );
	}

	public SceneTabWidget( Widget parent ) : base( parent )
	{
		Living.Add( this );

		Name = "SceneTabs";
		Layout = Layout.Column();

		_tabBar = Layout.Add( new TabBarWidget( this ) );
		_content = Layout.Add( new ContentHost( this ), 1 );

		_newTab = new IconButton( "add", EditorScene.NewScene, _tabBar )
		{
			ToolTip = "New scene",
			Background = Color.Transparent,
			FixedWidth = 20,
			FixedHeight = 20
		};

		RebuildTabBar();
	}

	public void Open( SceneEditorSession session )
	{
		if ( _tabs.Any( x => x.Session == session ) ) return;

		var tab = new SceneTab( this, session );
		_tabs.Add( tab );

		session.SceneDock.Visible = false;
		session.SceneDock.Parent = _content;

		RebuildTabBar();
		MakeCurrent( tab );
	}

	public void Remove( SceneEditorSession session )
	{
		var tab = _tabs.FirstOrDefault( x => x.Session == session );
		if ( tab is null ) return;

		CloseTab( tab );
	}

	void CloseTab( SceneTab tab )
	{
		var index = _tabs.IndexOf( tab );
		if ( index < 0 ) return;

		_tabs.Remove( tab );
		tab.Destroy();

		if ( !_tabBar.IsValid() )
			return;

		if ( _tabs.Count == 0 )
		{
			_currentTab = null;
			_content.Clear();
			RebuildTabBar();
			return;
		}

		if ( _currentTab == tab )
		{
			_currentTab = null;

			var neighbour = _tabs[Math.Min( index, _tabs.Count - 1 )];
			neighbour.Session.MakeActive();
		}

		RebuildTabBar();
	}

	public void MakeCurrent( SceneEditorSession session )
	{
		if ( _tabs.FirstOrDefault( x => x.Session == session ) is { } tab )
			MakeCurrent( tab );
	}

	void MakeCurrent( SceneTab tab )
	{
		if ( _currentTab == tab ) return;

		_currentTab = tab;
		_content.Show( tab.Session.SceneDock );

		//Each tab paints itself, so the one left and the one picked both repaint
		foreach ( var each in _tabs )
			each.Update();
	}

	public void UpdateTitle( SceneEditorSession session )
	{
		if ( _tabs.FirstOrDefault( x => x.Session == session ) is not { } tab ) return;

		tab.UpdateGeometry();
		tab.Update();
	}

	[Event( "scene.play" )]
	[Event( "scene.stop" )]
	void OnPlayStateChanged()
	{
		foreach ( var tab in _tabs )
			tab.Update();
	}

	internal bool IsCurrent( SceneTab tab ) => _currentTab == tab;

	internal bool IsCurrent( SceneEditorSession session ) => _currentTab?.Session == session;

	internal List<SceneTab> Tabs => _tabs;

	internal void Reorder( SceneTab tab, float centerX )
	{
		var oldIndex = _tabs.IndexOf( tab );
		if ( oldIndex < 0 ) return;

		var newIndex = _tabs.Count( x => x != tab && centerX > x.ScreenPosition.x + x.Width * 0.5f );
		if ( newIndex == oldIndex ) return;

		_tabs.RemoveAt( oldIndex );
		_tabs.Insert( newIndex, tab );
		RebuildTabBar();
	}

	void RebuildTabBar()
	{
		_tabBar.Layout.Clear( false );

		foreach ( var tab in _tabs )
		{
			_tabBar.Layout.Add( tab );
		}

		_tabBar.Layout.AddSpacingCell( 2 );
		_tabBar.Layout.Add( _newTab );
		_tabBar.Layout.AddStretchCell();
		_tabBar.Layout.AddSpacingCell( 4 );

		_tabBar.Update();
	}

	sealed class TabBarWidget : Widget
	{
		public TabBarWidget( Widget parent ) : base( parent )
		{
			Layout = Layout.Row();
			FixedHeight = 26;
		}

		protected override void OnPaint()
		{
			//Nothing behind the tabs. The strip is part of the window, and the window is glass.
		}
	}

	//Hosts the scene views stacked on top of each other, so switching doesn't relayout anything. The outgoing
	//widget stays on top until the end of the frame, by which point the incoming viewport has presented.
	sealed class ContentHost : Widget
	{
		Widget _current;

		public ContentHost( Widget parent ) : base( parent )
		{
		}

		public void Clear()
		{
			_current = null;
			HideTheRest();
			Update();
		}

		public void Show( Widget widget )
		{
			if ( _current == widget ) return;

			var outgoing = _current;
			_current = widget;

			widget.Position = 0;
			widget.Size = Size;
			widget.Visible = true;

			//Everything that is not the one being shown, now - not just the one this host happens to remember handing over from.
			HideTheRest( except: outgoing );

			if ( !outgoing.IsValid() || !outgoing.Visible )
				return;

			widget.Lower();

			EngineLoop.DisposeAtFrameEnd( new Rinten.Utility.DisposeAction( () =>
			{
				if ( outgoing.IsValid() && outgoing != _current )
					outgoing.Visible = false;

				if ( _current.IsValid() )
					_current.Raise();
			} ) );
		}

		//Hide every view except current and outgoing, since covered views still render at full cost
		internal void HideTheRest( Widget except = null )
		{
			if ( !StackedTabsHidden ) return;

			//The list itself, not Children, which copies into a new array - this runs for every scene tab,
			//every frame, to find nearly always nothing to hide, and hiding moves nobody in the list.
			foreach ( var child in childWidgets )
			{
				if ( child == _current || child == except ) continue;
				if ( !child.IsValid() || !child.Visible ) continue;

				child.Visible = false;

				Log.Info( $"scene tabs: hid {child.Name} - it was drawing behind the one on screen" );
			}
		}

		//Off with -nohidetabs , to measure what the hiding is worth.
		private static readonly bool StackedTabsHidden =
			!System.Environment.GetCommandLineArgs().Contains( "-nohidetabs" );

		protected override void OnPaint()
		{
			//Nothing: the scene views stacked in here cover it, and where they do not, what
			//should show is the window rather than a second grey behind the strip of open scenes.
		}

		protected override void OnResize()
		{
			foreach ( var child in Children )
			{
				if ( !child.Visible ) continue;

				child.Position = 0;
				child.Size = Size;
			}
		}
	}
}

//A single scene/prefab tab in the SceneTabWidget tab bar.
sealed class SceneTab : Widget
{
	public SceneEditorSession Session { get; }

	readonly SceneTabWidget _owner;
	readonly IconButton _close;
	Vector2 _dragStart;
	float _dragOffset;
	bool _dragPressed;

	bool IsCurrent => _owner.IsCurrent( this );
	bool IsPlaying => Session.IsPlaying;
	string Icon => IsPlaying ? "play_arrow" : Session.IsPrefabSession ? "home_repair_service" : "grid_4x4";
	string Title => Session.SceneDock?.WindowTitle ?? "Untitled";

	public override bool UsesCanvasDraw => true;

	public SceneTab( SceneTabWidget owner, SceneEditorSession session ) : base( owner )
	{
		_owner = owner;
		Session = session;
		Cursor = CursorShape.Finger;

		_close = new IconButton( "close", CloseSession, this )
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
		return new Vector2( Paint.MeasureText( Title ).x + 58, 26 );
	}

	protected override void OnPaint()
	{
		var hovered = Paint.HasMouseOver;
		var background = IsCurrent ? Theme.TabBackground : hovered ? Theme.Slab : Theme.TabInactiveBackground;

		var round = Theme.TabRadius;

		//Round along the top and square along the bottom, said outright. This used to draw a taller rectangle and let the
		//bottom corners fall off the end of the widget, which is the same picture and only while the numbers happen to agree.
		Paint.ClearPen();
		Paint.SetBrush( background );
		Paint.DrawRect( new Rect( 0, 0, Width, Height ), round, round, 0.0f, 0.0f );

		if ( IsCurrent || IsPlaying )
		{
			//Inside the corners, so the line does not cut across them.
			Paint.SetBrush( IsPlaying ? Theme.Good : Theme.EdgeLit );
			Paint.DrawRect( new Rect( round, 0, Width - (round * 2.0f), 2 ) );
		}

		var color = IsCurrent || hovered ? Theme.Text : Theme.TextLight;

		Paint.SetPen( IsPlaying ? Theme.Green : color );
		Paint.DrawIcon( new Rect( 8, 0, 16, Height ), Icon, 13 );

		Paint.SetPen( color );
		Paint.SetDefaultFont( weight: 500 );
		Paint.DrawText( new Rect( 29, 0, Width - 52, Height ), Title, TextFlag.LeftCenter );
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
			CloseSession();
			return;
		}

		//The close button acts on release, so its press falls through to here and would open the scene being closed
		if ( _close.IsValid() && new Rect( _close.Position, _close.Size ).IsInside( e.LocalPosition ) )
			return;

		if ( e.LeftMouseButton )
		{
			_dragStart = e.ScreenPosition;
			_dragOffset = e.LocalPosition.x;
			_dragPressed = true;
			Session.MakeActive();
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
		var tabs = _owner.Tabs;
		var index = tabs.IndexOf( this );

		menu.AddOption( "Close", null, CloseSession );
		AddCloseOption( menu, "Close Others", tabs.Where( x => x != this ) );
		AddCloseOption( menu, "Close Tabs to the Left", tabs.Take( index ) );
		AddCloseOption( menu, "Close Tabs to the Right", tabs.Skip( index + 1 ) );

		menu.OpenAtCursor();
		e.Accepted = true;
	}

	static void AddCloseOption( ContextMenu menu, string title, IEnumerable<SceneTab> tabs )
	{
		var closable = tabs.ToArray();
		if ( closable.Length == 0 ) return;

		menu.AddOption( title, null, () =>
		{
			foreach ( var tab in closable )
				tab.CloseSession();
		} );
	}

	void CloseSession()
	{
		//routes through SceneDock.OnClose so the unsaved changes prompt still applies
		Session.SceneDock?.Close();
	}
}
