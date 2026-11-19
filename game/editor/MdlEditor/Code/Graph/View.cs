using Rinten.UI;

namespace Editor.MdlEditor;

/// <summary>Graph view for the AnimGraph editor.</summary>
[StyleSheet( "View.cs.scss" )]
public class View : EditorGraphView, Nodes.IInternalGraphEditorActions
{
	protected override string ClipboardIdent => "animgraph";

	protected override INodeType CommentNodeType { get; } = new NodeType( EditorTypeLibrary.GetType<Nodes.CommentNode>() );

	private static bool? _cachedConnectionStyle;

	public static bool EnableGridAlignedWires
	{
		get => _cachedConnectionStyle ??= EditorCookie.Get( "animgraph.gridwires", false );
		set => EditorCookie.Set( "animgraph.gridwires", _cachedConnectionStyle = value );
	}

	public override ConnectionStyle ConnectionStyle => EnableGridAlignedWires
		? GridConnectionStyle.Instance
		: ConnectionStyle.Default;

	protected override bool CanDisconnectByDroppingConnection => false;

	protected override bool CanConnect( PlugOut output, PlugIn input )
		=> output.PropertyType == input.PropertyType;

	//Right-clicking a wire in an anim graph takes it out.
	protected override void OnRightClick( MousePanelEvent e )
	{
		if ( ConnectionAt( ToScene( MousePosition ) ) is not { } connection )
		{
			base.OnRightClick( e );
			return;
		}

		e.StopPropagation();

		PushUndo( "Disconnect" );
		connection.Disconnect();
		connection.Destroy();
		PushRedo();
	}

	//True once live debug state has been wiped while no preview model is running, letting the per-frame debug walk skip this view entirely.
	internal bool LiveDebugCleared { get; set; }

	bool _pendingFit;

	public override void Tick()
	{
		base.Tick();

		if ( !_pendingFit || Box.Rect.Width < 200 || Box.Rect.Height < 200 ) return;

		_pendingFit = false;

		FitView();
	}

	private readonly Dictionary<string, INodeType> _availableNodes = new( StringComparer.OrdinalIgnoreCase );
	private readonly Window _window;

	void Nodes.IInternalGraphEditorActions.ReflectChildParametersToParent( Nodes.InternalReferencedGraphNode node )
	{
		_window.ReflectChildParametersToParent( node );
	}

	public new Data Graph
	{
		get => (Data)base.Graph;
		set => base.Graph = value;
	}

	public View( Window window )
	{
		_window = window;

		//Discover all Node types and register them
		var types = EditorTypeLibrary.GetTypes<Node>()
			.Where( x => !x.IsAbstract && !x.HasAttribute<HideAttribute>() )
			.OrderBy( x => x.Name );

		foreach ( var type in types )
		{
			var nodeType = new NodeType( type );
			_availableNodes.TryAdd( nodeType.Identifier, nodeType );
		}

		OnSelectionChanged += SelectionChanged;
	}

	private bool _suppressUndo;
	private bool _suppressSelection;

	private IDisposable SuppressUndo()
	{
		_suppressUndo = true;
		return new Rinten.Utility.DisposeAction( () => _suppressUndo = false );
	}

	internal IDisposable SuppressSelectionForwarding()
	{
		_suppressSelection = true;
		return new Rinten.Utility.DisposeAction( () => _suppressSelection = false );
	}

	public override void PushUndo( string name )
	{
		if ( !_suppressUndo )
			_window.PushGraphUndo( name );
	}

	public override void PushLayoutUndo( string name )
	{
		if ( !_suppressUndo )
			_window.PushGraphUndo( name, GraphChangeScope.EditorOnly );
	}

	public override void PushRedo()
	{
		if ( !_suppressUndo )
			_window.PushGraphRedo();
	}

	protected override void OnDoubleClick( MousePanelEvent e )
	{
		if ( e.MouseButton != MouseButtons.Left ) return;
		if ( NodeAt( ToScene( MousePosition ) )?.Node is not Node graphNode ) return;
		if ( !_window.NavigateInto( graphNode ) ) return;

		e.StopPropagation();
	}

	private void SelectionChanged()
	{
		if ( _suppressSelection )
			return;

		var item = SelectedItems
			.OfType<NodeUI>()
			.OrderByDescending( n => n is CommentUI )
			.FirstOrDefault();

		if ( !item.IsValid() )
		{
			_window.OnNodeSelected( this, null );
			return;
		}

		_window.OnNodeSelected( this, (Node)item.Node );
	}

	internal void NotifySelection() => SelectionChanged();

	protected override INodeType NodeTypeFromDragEvent( Widget.DragEvent ev )
	{
		if ( ev.Data.Object is ClipDragData clipData )
		{
			return new AnimClipDropNodeType( ModelClipCatalog.Key( clipData.AnimationNames[0] ) );
		}

		if ( ev.Data.Object is ParameterDragData paramData )
		{
			return new ParameterDropNodeType( paramData.Parameter );
		}

		return base.NodeTypeFromDragEvent( ev );
	}

	private readonly List<NodeUI> _clipPreviews = new();

	public override DropAction OnEditorDragHover( Widget.DragEvent ev )
	{
		var action = base.OnEditorDragHover( ev );

		if ( ev.Data.Object is not ClipDragData { AnimationNames.Count: > 1 } clipData )
			return action;

		if ( _clipPreviews.Count == 0 )
		{
			foreach ( var name in clipData.AnimationNames.Skip( 1 ) )
			{
				var node = new AnimClipDropNodeType( ModelClipCatalog.Key( name ) ).CreateNode( Graph );
				_clipPreviews.Add( CreateNodeUI( node ) );
			}
		}

		var position = ToScene( MousePosition ).SnapToGrid( GridSize );

		for ( var i = 0; i < _clipPreviews.Count; i++ )
			_clipPreviews[i].Position = position + new Vector2( 0, ClipStackStep * (i + 1) );

		return action;
	}

	public override void OnEditorDragLeave()
	{
		base.OnEditorDragLeave();
		ClearClipPreviews();
	}

	//Grid-aligned so per-node position snapping doesn't make the gaps uneven
	private float ClipStackStep => _clipPreviews.FirstOrDefault().IsValid()
		? MathF.Ceiling( (_clipPreviews[0].Size.y + 8f) / GridSize ) * GridSize
		: 128f;

	private void ClearClipPreviews()
	{
		foreach ( var ui in _clipPreviews.Where( ui => ui.IsValid() ) )
		{
			Graph.RemoveNode( ui.Node );
			ui.Destroy();
		}

		_clipPreviews.Clear();
	}

	public override void OnEditorDragDrop( Widget.DragEvent ev )
	{
		if ( TryAssignDroppedClip( ev ) )
			return;

		var extra = ev.Data.Object is ClipDragData { AnimationNames.Count: > 1 } clipData
			? clipData.AnimationNames.Skip( 1 ).Select( ModelClipCatalog.Key ).ToArray()
			: null;

		var step = ClipStackStep;
		ClearClipPreviews();

		if ( extra is null )
		{
			base.OnEditorDragDrop( ev );
			return;
		}

		//One undo entry for the whole stack
		using ( SuppressUndo() )
		{
			base.OnEditorDragDrop( ev );

			var position = ToScene( MousePosition ).SnapToGrid( GridSize );
			for ( var i = 0; i < extra.Length; i++ )
				CreateNewNode( new AnimClipDropNodeType( extra[i] ), position + new Vector2( 0, step * (i + 1) ), null );
		}

		_window.CommitChange( "Add Nodes" );
	}

	bool TryAssignDroppedClip( Widget.DragEvent ev )
	{
		if ( ev.Data.Object is not ClipDragData { AnimationNames.Count: > 0 } data )
			return false;

		var ui = NodeAt( ToScene( MousePosition ) );
		if ( ui?.Node is not Node node || !CanTakeClip( node ) )
			return false;

		OnEditorDragLeave();
		PushUndo( "Set Animation" );
		SetClip( node, ModelClipCatalog.Key( data.AnimationNames[0] ) );
		PushRedo();
		ui.Update();
		_window.OnNodeSelected( this, node );
		return true;
	}

	static bool CanTakeClip( Node node ) => node is Nodes.AnimationClipNode
		or Nodes.AnimationPoseNode
		or Nodes.TimeControlledAnimationClipNode;

	static void SetClip( Node node, string name )
	{
		switch ( node )
		{
			case Nodes.AnimationClipNode clip:
				clip.AnimationName = name;
				break;
			case Nodes.AnimationPoseNode pose:
				pose.AnimationName = name;
				break;
			case Nodes.TimeControlledAnimationClipNode timed:
				timed.AnimationName = name;
				break;
		}
	}

	protected override IEnumerable<INodeType> GetRelevantNodes( NodeQuery query )
	{
		return _availableNodes.Values
			.Where( t => t is not NodeType nodeType || nodeType.IsAvailableIn( Graph ) )
			.Concat( _window.Graph.AllParameters.Select( parameter => new ParameterDropNodeType( parameter ) ) )
			.Filter( query );
	}

	protected override void OnOpenContextMenu( GraphMenu menu, Plug targetPlug )
	{
		base.OnOpenContextMenu( menu, targetPlug );

		if ( targetPlug.IsValid() )
			return;

		menu.AddOption( "Fit View", "fit_screen", FitView );
	}

	internal void FitView()
	{
		if ( Box.Rect.Width < 200 || Box.Rect.Height < 200 )
		{
			_pendingFit = true;
			return;
		}

		var nodes = Items.OfType<NodeUI>().ToArray();
		if ( nodes.Length == 0 )
			return;

		var bounds = nodes[0].SceneRect;
		foreach ( var node in nodes.Skip( 1 ) )
			bounds.Add( node.SceneRect );

		var viewport = new Vector2( Math.Max( Box.Rect.Width - 96, 1 ), Math.Max( Box.Rect.Height - 96, 1 ) );
		var span = Vector2.Max( bounds.Size, new Vector2( 1 ) );
		var scale = Math.Min( MathF.Min( viewport.x / span.x, viewport.y / span.y ), 1 );
		SetView( bounds.Center, scale );
	}

	//A parameter node fresh from the palette references nothing - bind it to the first parameter
	//of its type, or define a new one, so the node and the parameters dock always agree.
	protected override void OnNodeCreated( INode node )
	{
		if ( node is not Nodes.ParameterNode parameterNode || !string.IsNullOrWhiteSpace( parameterNode.ParameterName ) )
			return;

		var root = _window.Graph;
		var parameter = root.Parameters.FirstOrDefault( p => p.Type == parameterNode.ParameterType );

		if ( parameter is null )
		{
			parameter = new Parameter
			{
				Name = root.UniqueParameterName( parameterNode.ParameterType.ToString() ),
				Type = parameterNode.ParameterType
			};
			root.Parameters.Add( parameter );
		}

		parameterNode.ParameterName = parameter.Name;
	}

	protected override HandleConfig OnGetHandleConfig( Type type )
	{
		if ( HandleConfigs.TryGetValue( type, out var config ) )
			return config;

		return base.OnGetHandleConfig( type );
	}

	private static readonly Dictionary<Type, HandleConfig> HandleConfigs = new()
	{
		{ typeof( PoseLink ), new HandleConfig( "Pose", Color.Parse( "#4fc3f7" ).Value ) },
		{ typeof( FloatLink ), new HandleConfig( "Float", Color.Parse( "#8ec07c" ).Value ) },
		{ typeof( BoolLink ), new HandleConfig( "Bool", Color.Parse( "#e06c75" ).Value ) },
		{ typeof( IDLink ), new HandleConfig( "ID", Color.Parse( "#c678dd" ).Value ) },
		{ typeof( VectorLink ), new HandleConfig( "Vector", Color.Parse( "#e0d867" ).Value ) },
		{ typeof( BoneMaskLink ), new HandleConfig( "Bone Mask", Color.Parse( "#d19a66" ).Value ) },
		{ typeof( LayerLink ), new HandleConfig( "Layer", Color.Parse( "#ff6347" ).Value ) },
	};
}

//Temporary INodeType used when dropping an animation clip from the clip list. Creates an AnimationClipNode with the animation name pre-filled.
file class AnimClipDropNodeType : INodeType
{
	private readonly string _animationName;

	public AnimClipDropNodeType( string animationName )
	{
		_animationName = ModelClipCatalog.Key( animationName );
	}

	public bool IsCommon => false;

	public GraphMenu.PathElement[] Path => [new() { Name = "Animation", Icon = "movie" }, new() { Name = _animationName }];

	public bool TryGetInput( Type valueType, [System.Diagnostics.CodeAnalysis.NotNullWhen( true )] out string name )
	{
		name = null;
		return false;
	}

	public bool TryGetOutput( Type valueType, [System.Diagnostics.CodeAnalysis.NotNullWhen( true )] out string name )
	{
		if ( valueType.IsAssignableFrom( typeof( PoseLink ) ) )
		{
			name = "Result";
			return true;
		}
		name = null;
		return false;
	}

	public INode CreateNode( IGraph graph )
	{
		var node = new Nodes.AnimationClipNode { AnimationName = _animationName };
		return node;
	}
}

//Temporary INodeType used when dropping a parameter from the parameter list. Creates the matching parameter node with the name pre-filled.
file class ParameterDropNodeType : INodeType
{
	private readonly IParameter _parameter;

	public ParameterDropNodeType( IParameter parameter )
	{
		_parameter = parameter;
	}

	public bool IsCommon => true;

	public GraphMenu.PathElement[] Path =>
	[
		new() { Name = "Parameters", Icon = "tune" },
		new() { Name = "Existing Parameters", Icon = "list", Order = -100 },
		new() { Name = $"{_parameter.Name} ({_parameter.Type})" }
	];

	public bool TryGetInput( Type valueType, [System.Diagnostics.CodeAnalysis.NotNullWhen( true )] out string name )
	{
		name = null;
		return false;
	}

	public bool TryGetOutput( Type valueType, [System.Diagnostics.CodeAnalysis.NotNullWhen( true )] out string name )
	{
		var linkType = _parameter.Type switch
		{
			AnimParameterType.Float => typeof( FloatLink ),
			AnimParameterType.Bool => typeof( BoolLink ),
			AnimParameterType.ID => typeof( IDLink ),
			AnimParameterType.Target => typeof( TargetLink ),
			AnimParameterType.BoneMask => typeof( BoneMaskLink ),
			_ => typeof( VectorLink ),
		};

		if ( valueType.IsAssignableFrom( linkType ) )
		{
			name = "Value";
			return true;
		}

		name = null;
		return false;
	}

	public INode CreateNode( IGraph graph )
	{
		Nodes.ParameterNode node = _parameter.Type switch
		{
			AnimParameterType.Float => new Nodes.FloatParameterReferenceNode(),
			AnimParameterType.Bool => new Nodes.BoolParameterReferenceNode(),
			AnimParameterType.ID => new Nodes.IDParameterReferenceNode(),
			AnimParameterType.Target => new Nodes.TargetParameterReferenceNode(),
			AnimParameterType.BoneMask => new Nodes.BoneMaskParameterReferenceNode(),
			_ => new Nodes.VectorParameterReferenceNode(),
		};

		node.ParameterIdentifier = _parameter.Identifier;
		node.ParameterName = _parameter.Name;
		return node;
	}
}
