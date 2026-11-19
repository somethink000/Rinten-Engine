using Editor.NodeEditor;
using Rinten.UI;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Editor.MdlEditor;

internal interface IReorderableInputNode
{
	bool CanReorderInput( IPlugIn input );
	void ReorderInput( IPlugIn input, IPlugIn target );
}

/// <summary>Base class for all AnimGraph nodes. Uses [Input]/[Output] attributes on properties for automatic plug discovery via reflection.</summary>
public abstract class Node : INode, INodeHighlight, IAttachableNode
{
	private Vector2 _expandSize;

	[Hide, Browsable( false )]
	public string Identifier { get; set; }

	[Hide, Browsable( false )]
	public Vector2 Position { get; set; }

	/// <summary>Whether this node rides on the input it feeds instead of sitting on a wire beside it</summary>
	[Hide, Browsable( false )]
	public bool Attached { get; set; }

	public event Action Changed;

	[JsonIgnore, Hide, Browsable( false )]
	public virtual NodeDisplay DisplayInfo { get; }

	[JsonIgnore, Hide, Browsable( false )]
	public IGraph Graph { get; set; }

	/// <summary>The root graph that owns the model and parameters.</summary>
	[JsonIgnore, Hide, Browsable( false )]
	public Data Root => (Graph as Data)?.Root;

	[JsonIgnore, Hide, Browsable( false )]
	public IEnumerable<IPlugIn> Inputs => _propertyInputs.Concat( DynamicInputs );

	[JsonIgnore, Hide, Browsable( false )]
	public IEnumerable<IPlugOut> Outputs => _propertyOutputs.Concat( DynamicOutputs );

	//Extra inputs beyond the reflected [Input] properties, for nodes with a variable number of connections (e.g. blend space points).
	[JsonIgnore, Hide, Browsable( false )]
	protected virtual IEnumerable<IPlugIn> DynamicInputs => [];

	//Extra outputs beyond the reflected [Output] properties, for nodes with a variable number of outputs (e.g. groups).
	[JsonIgnore, Hide, Browsable( false )]
	protected virtual IEnumerable<IPlugOut> DynamicOutputs => [];

	/// <summary>Nested graphs owned by this node (e.g. state machine states and conditions).</summary>
	[JsonIgnore, Hide, Browsable( false )]
	public virtual IEnumerable<Data> SubGraphs => [];

	[JsonIgnore, Hide, Browsable( false )]
	public virtual Data SecondaryGraph => null;

	/// <summary>The node connected to one of this node's inputs, or null.</summary>
	public Node GetConnectedNode( NodeInput input )
	{
		var graph = Graph as Data;
		return input.IsValid ? graph?.FindNode( input.Identifier ) : null;
	}

	//Compiles this node into its runtime definition.
	internal virtual AnimGraphBuilder.Node Compile( Compiler compiler )
	{
		compiler.Warn( $"{DisplayInfo.Name} isn't supported yet", this );
		return compiler.ConstDefault( Outputs.OfType<AnimPlugOut>().FirstOrDefault()?.Type ?? typeof( PoseLink ) );
	}

	private List<IPlugIn> _propertyInputs;
	private List<IPlugOut> _propertyOutputs;
	private AnimGraphNodeValueType _outputValueType;

	[JsonIgnore, Hide, Browsable( false )]
	public virtual bool CanClone => true;

	[JsonIgnore, Hide, Browsable( false )]
	public virtual bool CanRemove => true;

	//True when the graph holds another node of the same type. Lets graph-managed singletons that normally can't be removed delete stray duplicates.
	protected bool HasDuplicate => Graph is { } graph && graph.Nodes.Count( node => node.GetType() == GetType() ) > 1;

	[JsonIgnore, Hide, Browsable( false )]
	public Vector2 ExpandSize
	{
		get => _expandSize;
		set => _expandSize = value;
	}

	[JsonIgnore, Hide, Browsable( false )]
	public virtual bool AutoSize => true;

	[JsonIgnore, Hide, Browsable( false )]
	public string ErrorMessage => null;

	[JsonIgnore, Hide, Browsable( false )]
	public bool IsReachable => true;

	[JsonIgnore, Hide, Browsable( false )]
	public bool HasTitleBar => true;

	[JsonIgnore, Hide, Browsable( false )]
	public virtual ITexture Thumbnail => null;

	[JsonIgnore, Hide, Browsable( false )]
	public virtual Color PrimaryColor { get; } = Color.Lerp( new Color( 0.7f, 0.7f, 0.7f ), Theme.Blue, 0.1f );

	[JsonIgnore, Hide, Browsable( false )]
	public bool HasLiveDebugInfo { get; private set; }

	[JsonIgnore, Hide, Browsable( false )]
	public AnimGraphNodeDebugInfo LiveDebugInfo { get; private set; }

	[JsonIgnore, Hide, Browsable( false )]
	public bool HasInvalidLiveHighlightOverride { get; private set; }

	[JsonIgnore, Hide, Browsable( false )]
	public bool IsLive => HasLiveDebugInfo && LiveDebugInfo.IsActive;

	[JsonIgnore, Hide, Browsable( false )]
	public AnimGraphNodeValueType OutputValueType => _outputValueType;

	[JsonIgnore, Hide, Browsable( false )]
	public bool HasPoseOutput => OutputValueType == AnimGraphNodeValueType.Pose;

	[JsonIgnore, Hide, Browsable( false )]
	public bool HasValueOutput => ValueDebug.IsSupported( OutputValueType );

	//True when this node has anything to show in the Properties Debug section. Nodes
	//adding their own debug widgets without a pose/value output should override this too.
	internal virtual bool HasDebugContent => HasPoseOutput || HasValueOutput;

	//Fills the Properties dock's Debug section. The base implementation shows the live pose or value readout; override to add
	//something the node itself has to say (a blend preview, say), calling base.BuildDebugWidgets to keep the default readout.
	internal virtual void BuildDebugWidgets( Rinten.UI.Panel parent )
	{
		if ( HasPoseOutput )
			PoseDebug.BuildDebugWidgets( parent, this );
		else if ( HasValueOutput )
			ValueDebug.BuildDebugWidgets( parent, this );
	}

	//A label to show in place of an output pin's name (e.g. a parameter node showing its live value), or null to keep the pin's normal name.
	internal virtual string LiveOutputLabel( string identifier ) => null;

	internal bool IsPoseLive => HasLiveDebugInfo
		&& LiveDebugInfo.IsActive
		&& LiveDebugInfo.ValueType == AnimGraphNodeValueType.Pose;

	[JsonIgnore, Hide, Browsable( false )]
	public AnimGraphSyncTrackEvent[] LiveSyncTrackEvents { get; private set; } = [];

	[JsonIgnore, Hide, Browsable( false )]
	public string LiveSyncEventID { get; private set; } = "";

	[JsonIgnore, Hide, Browsable( false )]
	public bool HasLiveDebugValue { get; private set; }

	[JsonIgnore, Hide, Browsable( false )]
	public ValueDebugInfo LiveDebugValue { get; private set; }

	[JsonIgnore, Hide, Browsable( false )]
	public Color HighlightOutlineColor
	{
		get
		{
			if ( HasInvalidLiveHighlightOverride )
				return Color.Red;

			if ( !HasLiveDebugInfo )
				return default;

			if ( LiveDebugInfo.IsInitialized && !LiveDebugInfo.IsValid )
				return Color.Red;

			//Nothing for a node that is merely running. Outlining every node the graph is currently using is outlining most of the
			//page, which says nothing and buries the red one that does. What is playing is read off the readout under the node.
			return default;
		}
	}

	[Hide, Browsable( false )]
	public Dictionary<string, float> HandleOffsets { get; set; } = new();

	public Node()
	{
		Identifier = Guid.NewGuid().ToString();
		DisplayInfo = Rinten.DisplayInfo.For( this );
		DiscoverPlugs();
	}

	public void Update() => Changed?.Invoke();
	public virtual NodeUI CreateUI( GraphView view ) => new GraphNodeUI( view, this );
	public Color GetPrimaryColor( GraphView view ) => PrimaryColor;

	public bool SetLiveDebugInfo(
		bool hasInfo,
		AnimGraphNodeDebugInfo info,
		AnimGraphSyncTrackEvent[] syncTrackEvents = null,
		string syncEventID = "",
		bool hasDebugValue = false,
		ValueDebugInfo debugValue = default,
		bool forceInvalidHighlight = false )
	{
		syncTrackEvents ??= [];

		if ( HasLiveDebugInfo == hasInfo
			&& LiveDebugInfo == info
			&& LiveSyncTrackEvents.SequenceEqual( syncTrackEvents )
			&& LiveSyncEventID == syncEventID
			&& HasLiveDebugValue == hasDebugValue
			&& LiveDebugValue == debugValue
			&& HasInvalidLiveHighlightOverride == forceInvalidHighlight )
			return false;

		HasLiveDebugInfo = hasInfo;
		LiveDebugInfo = info;
		LiveSyncTrackEvents = syncTrackEvents;
		LiveSyncEventID = syncEventID;
		HasLiveDebugValue = hasDebugValue;
		LiveDebugValue = debugValue;
		HasInvalidLiveHighlightOverride = forceInvalidHighlight;
		return true;
	}

	public virtual void BuildContent( Rinten.UI.Panel body ) { }

	public virtual void OnDoubleClick() { }
	public virtual void OnFrame() { }
	public virtual GraphMenu CreateContextMenu( NodeUI node ) => null;

	protected void ReorderInput<T>( IPlugIn input, IPlugIn target, List<T> values, List<IPlugIn> plugs )
	{
		var sourceIndex = plugs.IndexOf( input );
		var targetIndex = plugs.IndexOf( target );
		if ( sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex )
			return;

		var value = values[sourceIndex];
		values.RemoveAt( sourceIndex );
		values.Insert( targetIndex, value );

		var plug = plugs[sourceIndex];
		plugs.RemoveAt( sourceIndex );
		plugs.Insert( targetIndex, plug );

		for ( var i = 0; i < plugs.Count; i++ )
			((AnimPlugIn)plugs[i]).Info.Reindex( i );

		Update();
	}

	//An anim graph node, which may let its inputs be dragged into a different order.
	private sealed class GraphNodeUI : NodeUI
	{
		private PlugIn _reorderTarget;

		public GraphNodeUI( GraphView view, Node node ) : base( view, node )
		{
		}

		protected override PlugIn CreateInput( IPlugIn plug )
		{
			if ( Node is IReorderableInputNode reorderable && reorderable.CanReorderInput( plug ) )
			{
				return new ReorderablePlugIn( this, plug );
			}

			return base.CreateInput( plug );
		}

		//The row a dragged input would land on, lit while it is over it.
		private void SetReorderTarget( PlugIn target )
		{
			if ( ReferenceEquals( _reorderTarget, target ) ) return;

			_reorderTarget?.SetClass( "reorder-target", false );
			_reorderTarget = target;
			_reorderTarget?.SetClass( "reorder-target", true );
		}

		internal void BeginInputReorder( PlugIn source ) => SetReorderTarget( source );

		//The reorderable input nearest the pointer.
		internal void UpdateInputReorder( Vector2 scenePosition )
		{
			if ( Node is not IReorderableInputNode reorderable ) return;

			SetReorderTarget( Inputs
				.Where( x => reorderable.CanReorderInput( x.Inner ) )
				.MinBy( x => MathF.Abs( x.ConnectionPosition.y - scenePosition.y ) ) );
		}

		internal void EndInputReorder( PlugIn source )
		{
			var target = _reorderTarget;

			SetReorderTarget( null );

			if ( source is null || target is null || source == target ) return;
			if ( Node is not IReorderableInputNode reorderable ) return;

			Graph.PushUndo( "Reorder Inputs" );
			reorderable.ReorderInput( source.Inner, target.Inner );
			Graph.PushRedo();

			Update();
		}

		//An input with a grip on it, for dragging it up or down the node.
		private sealed class ReorderablePlugIn : PlugIn
		{
			private readonly GraphNodeUI _owner;

			public ReorderablePlugIn( GraphNodeUI owner, IPlug plug ) : base( owner, plug )
			{
				_owner = owner;

				AddClass( "reorderable" );

				var grip = AddChild<IconPanel>( "reorder-grip" );
				grip.Text = "drag_handle";
				grip.Tooltip = "Drag to reorder";
				grip.AddEventListener( "onmousedown", OnGripDown );
			}

			private void OnGripDown( PanelEvent e )
			{
				if ( e is not MousePanelEvent mouse || mouse.MouseButton != MouseButtons.Left ) return;

				e.StopPropagation();

				Graph.Begin( new ReorderInteraction( _owner, this ), mouse );
			}
		}

		//One input being dragged up or down the node it belongs to.
		private sealed class ReorderInteraction : IGraphInteraction
		{
			private readonly GraphNodeUI _node;
			private readonly PlugIn _source;

			public ReorderInteraction( GraphNodeUI node, PlugIn source )
			{
				_node = node;
				_source = source;

				node.BeginInputReorder( source );
			}

			public void Move( GraphPointer pointer ) => _node.UpdateInputReorder( pointer.Scene );

			public void Finish( GraphPointer pointer )
			{
				Move( pointer );

				_node.EndInputReorder( _source );
			}

			public void Cancel() => _node.EndInputReorder( null );
		}
	}

	//Discovers [Input] and [Output] properties via reflection and creates plugs.
	private void DiscoverPlugs()
	{
		var inputs = new List<IPlugIn>();
		var outputs = new List<IPlugOut>();

		foreach ( var prop in GetType().GetProperties() )
		{
			if ( prop.GetCustomAttribute<InputAttribute>() is { } inputAttr )
			{
				var info = new AnimPlugInfo( prop );
				inputs.Add( new AnimPlugIn( this, info, inputAttr.Type ?? typeof( object ) ) );
			}

			if ( prop.GetCustomAttribute<OutputAttribute>() is { } outputAttr )
			{
				var info = new AnimPlugInfo( prop );
				outputs.Add( new AnimPlugOut( this, info, outputAttr.Type ?? typeof( object ) ) );
			}
		}

		_propertyInputs = inputs;
		_propertyOutputs = outputs;
		_outputValueType = OutputValueTypeFor( Outputs.FirstOrDefault()?.Type );
	}

	private static AnimGraphNodeValueType OutputValueTypeFor( Type type ) => type switch
	{
		_ when type == typeof( BoolLink ) => AnimGraphNodeValueType.Bool,
		_ when type == typeof( IDLink ) => AnimGraphNodeValueType.ID,
		_ when type == typeof( FloatLink ) => AnimGraphNodeValueType.Float,
		_ when type == typeof( VectorLink ) => AnimGraphNodeValueType.Vector,
		_ when type == typeof( TargetLink ) => AnimGraphNodeValueType.Target,
		_ when type == typeof( PoseLink ) => AnimGraphNodeValueType.Pose,
		_ => AnimGraphNodeValueType.Unknown
	};

	[AttributeUsage( AttributeTargets.Property )]
	public class InputAttribute : Attribute
	{
		public Type Type { get; }
		public InputAttribute( Type type = null ) => Type = type;
	}

	[AttributeUsage( AttributeTargets.Property )]
	public class OutputAttribute : Attribute
	{
		public Type Type { get; }
		public OutputAttribute( Type type = null ) => Type = type;
	}
}

/// <summary>Metadata for a plug — either wrapping a NodeInput property, or backed by custom accessors for dynamic plugs.</summary>
public class AnimPlugInfo
{
	public string Name => _namePrefix is null ? _name : $"{_namePrefix}{Index}";
	public Type Type { get; }
	public PropertyInfo Property { get; }
	public int Index { get; private set; }

	private readonly string _name;
	private readonly string _namePrefix;
	private readonly Func<string> _title;
	private readonly Func<Node, NodeInput> _getInput;
	private readonly Action<Node, NodeInput> _setInput;

	public NodeDisplay DisplayInfo
	{
		get
		{
			var info = Rinten.DisplayInfo.ForMember( Type );
			info.Name = _title();
			return info;
		}
	}

	public AnimPlugInfo( PropertyInfo property )
	{
		Property = property;
		_name = property.Name;
		Type = property.PropertyType;

		var title = property.GetCustomAttribute<TitleAttribute>()?.Value ?? property.Name;
		_title = () => title;

		if ( property.PropertyType == typeof( NodeInput ) )
		{
			_getInput = node => (NodeInput)property.GetValue( node );
			_setInput = ( node, value ) => property.SetValue( node, value );
		}
	}

	public AnimPlugInfo( string name, Func<string> title, Func<Node, NodeInput> getInput, Action<Node, NodeInput> setInput )
	{
		_name = name;
		Type = typeof( NodeInput );
		_title = title;
		_getInput = getInput;
		_setInput = setInput;
	}

	public AnimPlugInfo( string namePrefix, int index, Func<int, string> title, Func<Node, int, NodeInput> getInput, Action<Node, int, NodeInput> setInput )
	{
		_namePrefix = namePrefix;
		Index = index;
		Type = typeof( NodeInput );
		_title = () => title( Index );
		_getInput = node => getInput( node, Index );
		_setInput = ( node, value ) => setInput( node, Index, value );
	}

	public void Reindex( int index ) => Index = index;

	public bool CanConnect => _getInput is not null;
	public NodeInput GetInput( Node node ) => _getInput?.Invoke( node ) ?? default;
	public void SetInput( Node node, NodeInput value ) => _setInput?.Invoke( node, value );
}

/// <summary>Base plug record shared by inputs and outputs.</summary>
public record AnimPlug( Node Node, AnimPlugInfo Info, Type Type ) : IPlug
{
	INode IPlug.Node => Node;
	public string Identifier => Info.Name;
	public virtual NodeDisplay DisplayInfo => Info.DisplayInfo;
	public virtual bool ShowLabel => true;
	public bool AllowStretch => true;

	//A node riding on the plug it feeds needs none: it is sitting on the thing it is connected to, and a wire between two touching boxes is a smudge.
	/// <summary>Whether the connection through this plug is drawn as a wire.</summary>
	public virtual bool ShowConnection => true;
	public virtual bool InTitleBar => false;
	public bool IsReachable => true;
	public string ErrorMessage => null;

	public ValueEditor CreateEditor( NodeUI node, Plug plug ) => null;
	public virtual GraphMenu CreateContextMenu( NodeUI node, Plug plug ) => null;
	public void OnDoubleClick( NodeUI node, Plug plug ) { }
}

/// <summary>Input plug that reads/writes NodeInput connections from node properties.</summary>
public record AnimPlugIn( Node Node, AnimPlugInfo Info, Type Type ) : AnimPlug( Node, Info, Type ), IPlugIn
{
	/// <summary>No wire when what feeds this plug is sitting on it.</summary>
	public override bool ShowConnection => ( (IPlugIn)this ).ConnectedOutput?.Node is not Node { Attached: true };

	/// <summary>Input right-click menu: create something that feeds this plug, attached directly to it</summary>
	public override GraphMenu CreateContextMenu( NodeUI node, Plug plug )
	{
		if ( !Info.CanConnect || Node.Graph is not Data graph ) return null;

		var answers = graph.GetRelevantNodes( graph )
			.OfType<NodeType>()
			.Where( x => Answers( x, Type ) )
			.OrderBy( x => x.DisplayInfo.Name )
			.ToList();

		if ( answers.Count == 0 ) return null;

		var menu = new GraphMenu( DisplayInfo.Name );

		foreach ( var answer in answers )
		{
			var made = answer;

			menu.AddOption( made.DisplayInfo.Name, made.DisplayInfo.Icon, () => Attach( made, plug ) );
		}

		return menu;
	}

	//Whether a node type has an output this plug could take.
	private static bool Answers( NodeType type, Type wanted )
	{
		foreach ( var property in type.TypeDescription.TargetType.GetProperties() )
		{
			if ( property.GetCustomAttribute<Node.OutputAttribute>() is not { } output ) continue;
			if ( output.Type == wanted ) return true;
		}

		return false;
	}

	//Makes one and puts it on the plug.
	private void Attach( NodeType type, Plug plug )
	{
		if ( Node.Graph is not Data graph ) return;

		var view = plug?.Node?.Graph;

		view?.PushUndo( $"Add {type.DisplayInfo.Name}" );

		var made = (Node)type.CreateNode( graph );
		made.Attached = true;
		made.Position = Node.Position;

		graph.AddNode( made );

		if ( made.Outputs.FirstOrDefault() is { } source )
			( (IPlugIn)this ).ConnectedOutput = source;

		view?.PushRedo();
		view?.RebuildFromGraph();
	}

	IPlugOut IPlugIn.ConnectedOutput
	{
		get
		{
			if ( !Info.CanConnect )
				return null;

			var value = Info.GetInput( Node );
			if ( !value.IsValid )
				return null;

			if ( Node.Graph is not Data graph )
				return null;

			var sourceNode = graph.FindNode( value.Identifier );
			return sourceNode?.Outputs.FirstOrDefault( x => x.Identifier == value.Output );
		}
		set
		{
			if ( !Info.CanConnect )
				return;

			if ( value is null )
			{
				Info.SetInput( Node, default );
				return;
			}

			if ( value is not AnimPlug source )
				return;

			Info.SetInput( Node, new NodeInput
			{
				Identifier = source.Node.Identifier,
				Output = source.Identifier
			} );
		}
	}

	public float? GetHandleOffset( string name )
	{
		return Node.HandleOffsets.TryGetValue( name, out var v ) ? v : null;
	}

	public void SetHandleOffset( string name, float? value )
	{
		if ( value is null ) Node.HandleOffsets.Remove( name );
		else Node.HandleOffsets[name] = value.Value;
	}
}

/// <summary>Output plug. Lets the owning node override its displayed label - parameter nodes substitute their live value for the pin name.</summary>
public record AnimPlugOut( Node Node, AnimPlugInfo Info, Type Type ) : AnimPlug( Node, Info, Type ), IPlugOut
{
	/// <summary>No wire out of a node that is sitting on the plug it feeds.</summary>
	public override bool ShowConnection => !Node.Attached;

	/// <summary>An attached node is just its title with the output plug in it, no body</summary>
	public override bool InTitleBar => Node.Attached;

	/// <summary>The label goes with it: the node's own name already says this.</summary>
	public override bool ShowLabel => !Node.Attached;

	public override NodeDisplay DisplayInfo
	{
		get
		{
			var info = base.DisplayInfo;
			if ( Node.LiveOutputLabel( Identifier ) is { } label )
				info.Name = label;
			return info;
		}
	}
}
