using Rinten.UI;

namespace Editor.NodeEditor;

partial class GraphView
{
	//What the pointer is in the middle of doing, if anything.
	private IGraphInteraction _interaction;
	private MouseButtons _interactionButton;

	private float _zoomTarget;
	private Vector2 _zoomAnchor;

	private Connection _hoveredConnection;

	//Whether a wire dropped in empty space should leave a reroute behind.
	private bool _createReroute;

	private Vector2 _lastScenePosition;

	//Where the pointer is, in both spaces at once.
	internal GraphPointer CurrentPointer => Pointer();

	private GraphPointer Pointer()
	{
		var view = MousePosition;

		return new GraphPointer( view, ToScene( view ) );
	}

	/// <summary>Hands the pointer to something until the button comes up.</summary>
	public void Begin( IGraphInteraction interaction, MousePanelEvent e )
	{
		_interaction?.Cancel();

		//Anything done to the graph is done to this view, which is what the window reads to know which graph the menus belong to.
		Focus();

		_interaction = interaction;
		_interactionButton = e.MouseButton;
	}

	/// <summary>Gives up on whatever the pointer was doing.</summary>
	public void CancelInteraction()
	{
		var interaction = _interaction;

		_interaction = null;

		interaction?.Cancel();
	}

	//Follows the pointer for whatever it is doing, and notices when it stops. Read each frame rather than from move events: a drag
	//that leaves the panel stops sending them, and a button released over another window is a button nothing tells this about.
	private void DriveInteraction()
	{
		if ( _interaction is null ) return;

		var pointer = Pointer();

		_lastScenePosition = pointer.Scene;

		if ( (Application.MouseButtons & _interactionButton) == 0 )
		{
			var finishing = _interaction;

			_interaction = null;

			finishing.Finish( pointer );
			return;
		}

		_interaction.Move( pointer );
	}

	//Zooming

	public override void OnMouseWheel( Vector2 value )
	{
		if ( value.y.AlmostEqual( 0.0f ) ) return;

		if ( _zoomTarget <= 0.0f ) _zoomTarget = Viewport.Zoom;

		_zoomTarget = Math.Clamp( _zoomTarget * (value.y < 0.0f ? 1.15f : 1.0f / 1.15f),
			GraphViewport.MinZoom, GraphViewport.MaxZoom );

		_zoomAnchor = MousePosition;
	}

	//Eases towards the zoom the wheel asked for, about the point it was over.
	private void SmoothZoom()
	{
		if ( _zoomTarget <= 0.0f ) return;

		var zoom = Viewport.Zoom;

		if ( MathF.Abs( MathF.Log( _zoomTarget / zoom ) ) < 0.002f )
		{
			_zoomTarget = 0.0f;

			SaveViewToCookie();
			return;
		}

		var t = 1.0f - MathF.Exp( -RealTime.Delta * 20.0f );

		Viewport.ZoomTo( MathF.Exp( MathX.Lerp( MathF.Log( zoom ), MathF.Log( _zoomTarget ), t ) ), _zoomAnchor );
	}

	//Pointer

	protected override void OnMouseDown( MousePanelEvent e )
	{
		Focus();

		if ( e.MouseButton == MouseButtons.Middle )
		{
			Begin( new PanInteraction( Viewport, Pointer() ), e );

			e.StopPropagation();
			return;
		}

		if ( e.MouseButton != MouseButtons.Left ) return;

		//Only presses that reached the canvas itself: one on a node was dealt with there,
		//and telling it apart from one on the canvas behind it is what the target says.
		if ( e.Target != this ) return;

		var pointer = Pointer();

		if ( ConnectionAt( pointer.Scene ) is { } connection )
		{
			PressConnection( connection, pointer, e );
			return;
		}

		Begin( new MarqueeInteraction( this, pointer, HasCtrl || HasShift ), e );

		e.StopPropagation();
	}

	protected override void OnMouseMove( MousePanelEvent e )
	{
		var pointer = Pointer();

		_lastScenePosition = pointer.Scene;

		if ( _interaction is not null ) return;

		SetHoveredConnection( e.Target == this ? ConnectionAt( pointer.Scene ) : null );
	}

	protected override void OnMouseOut( MousePanelEvent e )
	{
		if ( _interaction is null ) SetHoveredConnection( null );
	}

	internal static bool HasCtrl => Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Ctrl );
	internal static bool HasShift => Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Shift );

	//Wires, which are not panels and so are found by hand

	/// <summary>The wire under a point of the graph, if the pointer is on one.</summary>
	public Connection ConnectionAt( Vector2 scenePosition )
	{
		//In graph units, so the reach shrinks with everything else as the view pulls back -
		//and a wire you cannot put the pointer on is worse than one that is a little generous.
		var reach = MathF.Max( Connection.HitWidth, 5.0f / MathF.Max( Viewport.Zoom, 0.01f ) );

		for ( var i = _connections.Count - 1; i >= 0; i-- )
		{
			var connection = _connections[i];

			if ( !connection.IsValid() ) continue;
			if ( !connection.SceneBounds.IsInside( scenePosition ) ) continue;
			if ( !connection.IsNear( scenePosition, reach ) ) continue;

			return connection;
		}

		return null;
	}

	private void SetHoveredConnection( Connection connection )
	{
		if ( _hoveredConnection == connection ) return;

		if ( _hoveredConnection.IsValid() ) _hoveredConnection.Hovered = false;

		_hoveredConnection = connection;

		if ( _hoveredConnection.IsValid() ) _hoveredConnection.Hovered = true;

		Tooltip = connection?.ToolTip;
		SetClass( "over-connection", connection is not null );
	}

	private void PressConnection( Connection connection, GraphPointer pointer, MousePanelEvent e )
	{
		e.StopPropagation();

		//Shift drops a reroute where the wire was clicked, which is how a wire that has to go round something gets a corner to go round it by.
		if ( HasShift )
		{
			RerouteConnection( connection, pointer.Scene );
			return;
		}

		Select( connection, alone: !HasCtrl );

		//Taking hold of the wire is taking hold of the end that feeds it: drag and it comes off the input and follows the pointer.
		if ( connection.Output.IsValid() )
		{
			Begin( new WireInteraction( this, connection, pointer ), e );
		}
	}

	//Keys

	public override void OnButtonEvent( ButtonEvent e )
	{
		base.OnButtonEvent( e );

		if ( e.Pressed )
		{
			switch ( e.Button )
			{
				case "delete":
					DeleteSelection();
					e.StopPropagation = true;
					break;

				case "r":
					_createReroute = true;
					break;

				case "space":
					OpenContextMenu( Application.CursorPosition, _lastScenePosition );
					e.StopPropagation = true;
					break;
			}

			return;
		}

		if ( e.Button == "r" ) _createReroute = false;
	}

	//Menus

	//Canvas menu, which is also the node menu; contents depend on what was under the press
	protected override void OnRightClick( MousePanelEvent e )
	{
		e.StopPropagation();

		OpenContextMenu( Application.CursorPosition, ToScene( MousePosition ) );
	}

	protected virtual void OpenContextMenu( Vector2 screenPosition, Vector2 clickPos, Plug targetPlug = null, Action onClose = null )
	{
		var menu = new ContextMenu( null );
		var anySelected = false;

		if ( !targetPlug.IsValid() )
		{
			var selectedNodes = SelectedItems.OfType<NodeUI>().ToArray();

			if ( selectedNodes.Any() )
			{
				anySelected = true;

				menu.AddOption( $"Cut {selectedNodes.Length} nodes", "content_cut", CutSelection );
				menu.AddOption( $"Copy {selectedNodes.Length} nodes", "content_copy", CopySelection );
				menu.AddOption( $"Delete {selectedNodes.Length} nodes", "delete", DeleteSelection );
				menu.AddSeparator();

				menu.AddOption( $"Add Comment for {selectedNodes.Length} nodes", "notes", () =>
				{
					Vector2 min = float.MaxValue;
					Vector2 max = float.MinValue;

					foreach ( var node in selectedNodes )
					{
						min = node.SceneRect.TopLeft.ComponentMin( min );
						max = node.SceneRect.BottomRight.ComponentMax( max );
					}

					min -= new Vector2( 32, 40 + 32 );
					max += 32;

					CreateNewComment( "Untitled", CommentColor.Green, min, max - min );
				} );
			}

			if ( NodeAt( clickPos ) is { } node )
			{
				if ( node.Node.GoToDefinition is { } goToDef )
				{
					anySelected = true;
					menu.AddOption( "Go to Definition", "read_more", goToDef );
				}

				if ( node.Node.CreateContextMenu( node ) is { } nodeMenu )
				{
					anySelected = true;
					menu.AddMenu( nodeMenu );
				}
			}
		}

		OnOpenContextMenu( menu, targetPlug );

		if ( !anySelected )
		{
			var nodeMenu = menu.OptionCount == 0 && menu.MenuCount == 0 ? menu : menu.AddMenu( "Create Node" );

			CreateNodeMenu( nodeMenu, screenPosition, clickPos, targetPlug );
		}

		if ( onClose is not null ) menu.AboutToHide += onClose;

		menu.OpenAt( screenPosition, this, false );
	}

	protected virtual void OnOpenContextMenu( Menu menu, Plug targetPlug )
	{
	}

	protected virtual void CreateNodeMenu( Menu menu, Vector2 screenPosition, Vector2 clickPos, Plug targetPlug = null )
	{
		menu.AboutToShow += () => PopulateNodeMenu( menu, clickPos, targetPlug );

		menu.AddLineEdit( "Filter",
			placeholder: "Filter Nodes..",
			autoFocus: true,
			onChange: s => PopulateNodeMenu( menu, clickPos, targetPlug, s ) );
	}

	protected virtual IEnumerable<INodeType> GetRelevantNodes( NodeQuery query ) => Enumerable.Empty<INodeType>();

	protected virtual void OnPopulateNodeMenuSpecialOptions( Menu menu, Vector2 clickPos, Plug targetPlug, string filter )
	{
		if ( CanPasteSelection() )
		{
			menu.AddOption( "Paste Node(s)", "content_paste", PasteSelection );
		}

		if ( !targetPlug.IsValid() )
		{
			menu.AddOption( "Add Comment", "notes", () =>
			{
				CreateNewComment( "Untitled", CommentColor.Green, clickPos, 300 );
			} );
		}
		else if ( CanCreateReroute )
		{
			menu.AddOption( "Add Reroute", "route", () =>
			{
				CreateNewNode( RerouteNodeType, clickPos, targetPlug );
			} );
		}
	}

	protected virtual void OnPopulateNodeMenu( Menu menu, Vector2 clickPos, Plug targetPlug, string filter )
	{
		var query = new NodeQuery( Graph, targetPlug is { IsValid: true, Inner: { } inner } ? inner : null, filter );
		var nodes = GetRelevantNodes( query ).ToArray();

		var useFilter = query.Filter.Count > 0;

		const int maxFilteredResults = 20;

		if ( useFilter && nodes.Length > maxFilteredResults )
		{
			nodes = nodes.Take( maxFilteredResults ).ToArray();
		}

		PopulateNodeMenu( menu, nodes, useFilter ? query : null, type => CreateNewNode( type, clickPos, targetPlug ) );
	}

	protected virtual void PopulateNodeMenu( Menu menu, Vector2 clickPos, Plug targetPlug = null, string filter = null )
	{
		menu.RemoveMenus();
		menu.RemoveOptions();

		if ( string.IsNullOrWhiteSpace( filter ) )
		{
			OnPopulateNodeMenuSpecialOptions( menu, clickPos, targetPlug, filter );
		}

		OnPopulateNodeMenu( menu, clickPos, targetPlug, filter );

		if ( menu.Visible )
		{
			menu.AdjustSize();
			menu.Update();
		}
	}

	private static Menu.PathElement[] WithScore( Menu.PathElement[] path, NodeQuery query )
	{
		var copy = new Menu.PathElement[path.Length];

		Array.Copy( path, copy, path.Length );

		copy[^1] = copy[^1] with { Order = -query.GetScore( path ) };

		return copy;
	}

	public static void PopulateNodeMenu( Menu menu, IEnumerable<INodeType> nodes, NodeQuery? query, Action<INodeType> selectedAction )
	{
		menu.AddOptions( nodes, query is { } q ? x => WithScore( x.Path, q ) : x => x.Path,
			action: selectedAction,
			flat: query is not null,
			reduce: true );
	}

	//Finding things by where they are

	//Where a panel of the graph sits, in graph units.
	internal Rect SceneRectOf( Panel panel )
	{
		var box = panel.Box.Rect;

		return new Rect( box.Position - _world.Box.Rect.Position, box.Size );
	}

	/// <summary>The node at a point of the graph, the topmost one if they overlap.</summary>
	public NodeUI NodeAt( Vector2 scenePosition )
	{
		NodeUI best = null;

		foreach ( var item in _items )
		{
			if ( item is not NodeUI node ) continue;
			if ( !node.SceneRect.IsInside( scenePosition ) ) continue;
			if ( best is not null && node.ZIndex < best.ZIndex ) continue;

			best = node;
		}

		return best;
	}

	//In that order, because it is the order they lie in - the editor is inside the node, and the wires are on the layer beneath both.
	/// <summary>What is under a point of the graph: a socket's editor, a wire, or a node.</summary>
	public object ItemAt( Vector2 scenePosition )
	{
		if ( PlugAt( scenePosition )?.Editor is { } editor ) return editor;
		if ( ConnectionAt( scenePosition ) is { } connection ) return connection;

		return NodeAt( scenePosition );
	}

	/// <summary>The plug at a point of the graph.</summary>
	public Plug PlugAt( Vector2 scenePosition )
	{
		foreach ( var item in _items )
		{
			if ( item is not NodeUI node ) continue;
			if ( !node.SceneRect.Grow( 12.0f ).IsInside( scenePosition ) ) continue;

			foreach ( var plug in node.AllPlugs )
			{
				if ( plug.SceneRect.IsInside( scenePosition ) ) return plug;
			}
		}

		return null;
	}

	//Assets dropped onto the canvas

	public bool AcceptEditorDrops => true;

	private NodeUI _nodePreview;

	protected virtual INodeType NodeTypeFromDragEvent( Widget.DragEvent ev ) => null;

	public virtual DropAction OnEditorDragHover( Widget.DragEvent ev )
	{
		var position = ToScene( MousePosition ).SnapToGrid( GridSize );

		if ( _nodePreview.IsValid() )
		{
			_nodePreview.Position = position;
			return DropAction.Link;
		}

		if ( NodeTypeFromDragEvent( ev ) is not { } type ) return DropAction.Ignore;

		var node = type.CreateNode( Graph );
		node.Position = position;

		_nodePreview = CreateNodeUI( node );

		return DropAction.Link;
	}

	public virtual void OnEditorDragLeave() => ClearNodePreview();

	public virtual void OnEditorDragDrop( Widget.DragEvent ev )
	{
		Focus();

		ClearNodePreview();

		if ( NodeTypeFromDragEvent( ev ) is not { } type ) return;

		CreateNewNode( type, ToScene( MousePosition ), null );
	}

	private void ClearNodePreview()
	{
		if ( !_nodePreview.IsValid() ) return;

		Graph.RemoveNode( _nodePreview.Node );
		_nodePreview.Destroy();
		_nodePreview = null;
	}
}
