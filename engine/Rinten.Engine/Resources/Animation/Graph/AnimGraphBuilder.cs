using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rinten;

/// <summary>Builds a graph as a flat, index-linked node list; the API is shaped for the editor's calls</summary>
public sealed partial class AnimGraphBuilder
{
	//Bumped when the definition below changes in a way an older reader would misread. A graph is
	//compiled from its source, so this only has to outlive the compiled files sitting in a project.
	private const int DefinitionVersion = 1;

	//A handle, not the node: the graph is the list, and this says where in it. The index is stored one higher than it is, so that a default handle - which is what a caller passes for "nothing connected here" - is not mistaken for the first node.
	/// <summary>A node in the graph being built.</summary>
	public sealed class Node : IEquatable<Node>
	{
		private readonly AnimGraphBuilder owner;
		private readonly int index;

		internal Node( AnimGraphBuilder owner, int index, NodeKind kind )
		{
			this.owner = owner;
			this.index = index;

			Kind = kind;
		}

		/// <summary>What this node produces.</summary>
		public NodeKind Kind { get; }

		//Nothing connected is a null handle rather than an invalid one, which is what the editor passes and what a graph that is half drawn is full of. This is here for the reading of it.
		/// <summary>Whether anything is connected here at all.</summary>
		public bool IsValid => index >= 0;

		/// <summary>Where this node sits in the compiled definition. What a debugger points at, and what a saved graph's node map is keyed by.</summary>
		public int GetRuntimeIndex() => index;

		//Asked by the things that only work on one: warping a path, scaling a speed to a velocity. A blend of two clips has no single path to warp, which is a thing to tell an author rather than to work around.
		/// <summary>Whether this is a clip, or something that is one underneath.</summary>
		public bool IsClip() => owner is not null && owner.IsClipNode( index, 0 );

		public bool Equals( Node other ) => other is not null && index == other.index;
		public override bool Equals( object obj ) => Equals( obj as Node );
		public override int GetHashCode() => index;

		public static bool operator ==( Node a, Node b ) => a is null ? b is null : a.Equals( b );
		public static bool operator !=( Node a, Node b ) => !( a == b );

		public override string ToString() => IsValid ? $"{Kind} #{GetRuntimeIndex()}" : "nothing";
	}

	//Whether the node at an index is a clip once whatever wraps it is looked through. Bounded, because
	//a graph can be drawn with a loop in it and a compiler that follows one does not come back.
	private bool IsClipNode( int index, int depth )
	{
		if ( depth > 16 ) return false;
		if ( index < 0 || index >= nodes.Count ) return false;
		if ( nodes[index] is not JsonObject node ) return false;

		var kind = node["Kind"]?.GetValue<string>() ?? "";

		switch ( kind )
		{
			case "Clip":
			case "TimedClip":
			case "Pose":
				return true;

			//Things that wrap one clip and leave it a clip.
			case "Passthrough":
			case "SpeedScale":
			case "DurationScale":
			case "VelocityScale":
			case "PoseScale":
				return IsClipNode( Index( node, "Pose" ), depth + 1 );

			default:
				return false;
		}
	}

	private static int Index( JsonObject node, string field )
		=> node[field] is { } value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<int>() : -1;

	private readonly JsonArray nodes = new();
	private readonly JsonArray parameters = new();
	private readonly JsonObject virtualParameters = new();

	//A path rather than a loaded model, because graphs are often built off the main thread, where loading one is not allowed.
	/// <summary>The skeleton the graph's clips are compiled against.</summary>
	public string Skeleton { get; set; }

	//Add a node to the graph and hand back a handle to it.
	private Node Add( NodeKind kind, string type, JsonObject fields = null )
	{
		var node = fields ?? new JsonObject();
		node["Kind"] = type;

		nodes.Add( node );

		return new Node( this, nodes.Count - 1, kind );
	}

	//A node reference as the definition stores it: an index, or -1 for nothing connected.
	private static JsonNode Ref( Node node ) => node is { IsValid: true } ? node.GetRuntimeIndex() : -1;

	//Several of them, in order.
	private static JsonArray Refs( IEnumerable<Node> list )
	{
		var array = new JsonArray();
		if ( list is null ) return array;

		foreach ( var node in list ) array.Add( Ref( node ) );

		return array;
	}

	private static JsonArray Text( IEnumerable<string> list )
	{
		var array = new JsonArray();
		if ( list is null ) return array;

		foreach ( var value in list ) array.Add( value ?? "" );

		return array;
	}

	private static JsonArray Numbers( IEnumerable<float> list )
	{
		var array = new JsonArray();
		if ( list is null ) return array;

		foreach ( var value in list ) array.Add( value );

		return array;
	}

	//-- parameters -----------------------------------------------------------

	//Both at once because they are the same thing seen from two sides: a game sets it by name, and the graph reads it as a value. A parameter nothing reads still has to exist, because the game may still set it.
	/// <summary>Declare a parameter the graph is driven by, and hand back the node that reads it.</summary>
	public Node AddControlParameter( string name, NodeKind kind, ControlParameterSettings settings = null )
	{
		settings ??= new ControlParameterSettings();

		var index = parameters.Count;

		parameters.Add( new JsonObject
		{
			["Name"] = name ?? "",
			["Kind"] = kind.ToString(),
			["Describe"] = settings.Describe,
			["Min"] = settings.Min,
			["Max"] = settings.Max,
			["Default"] = settings.Default,
			["AutoReset"] = settings.AutoReset,
			["Options"] = Text( settings.Options ),
		} );

		return Add( kind, "Parameter", new JsonObject
		{
			["Parameter"] = index,
			["Name"] = name ?? "",
		} );
	}

	public Node FloatParameter( string name, float min = 0.0f, float max = 0.0f )
		=> AddControlParameter( name, NodeKind.Float, new ControlParameterSettings { Describe = true, Min = min, Max = max } );

	public Node BoolParameter( string name )
		=> AddControlParameter( name, NodeKind.Bool, new ControlParameterSettings { Describe = true } );

	public Node IdParameter( string name, params string[] options )
		=> AddControlParameter( name, NodeKind.Id, new ControlParameterSettings { Describe = true, Options = options } );

	public Node VectorParameter( string name )
		=> AddControlParameter( name, NodeKind.Vector, new ControlParameterSettings { Describe = true } );

	public Node TargetParameter( string name )
		=> AddControlParameter( name, NodeKind.Target, new ControlParameterSettings { Describe = true } );

	/// <summary>Give a name to something the graph works out for itself, so the rest of the graph can read it as though it were set from outside.</summary>
	public void BindVirtualParameter( string name, Node node )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return;

		virtualParameters[name] = Ref( node );
	}

	public Node VirtualFloatParameter( string name, Node value )
	{
		BindVirtualParameter( name, value );
		return value;
	}

	public Node VirtualBoolParameter( string name, Node value )
	{
		BindVirtualParameter( name, value );
		return value;
	}

	public Node VirtualIdParameter( string name, Node value )
	{
		BindVirtualParameter( name, value );
		return value;
	}

	public Node VirtualVectorParameter( string name, Node value )
	{
		BindVirtualParameter( name, value );
		return value;
	}

	public Node VirtualTargetParameter( string name, Node value )
	{
		BindVirtualParameter( name, value );
		return value;
	}

	public Node VirtualBoneMaskParameter( string name, Node value )
	{
		BindVirtualParameter( name, value );
		return value;
	}

	//-- checking -------------------------------------------------------------

	//Throwing rather than warning, because the caller is a compiler with a node in hand: it can say which node and which input, and a graph that compiles into something meaningless is worse than one that does not compile.
	/// <summary>Insist that something is connected here, and that it produces what is wanted.</summary>
	public Node Require( Node node, NodeKind kind, string what = null )
	{
		if ( node is not { IsValid: true } )
			throw new ArgumentException( $"{what ?? "input"} has nothing connected to it" );

		if ( node.Kind != kind )
			throw new ArgumentException( $"{what ?? "input"} wants a {kind} and was given a {node.Kind}" );

		return node;
	}

	/// <summary>The same, for something that may be left unconnected. Nothing connected is fine; the wrong thing connected is not.</summary>
	public Node Optional( Node node, NodeKind kind, string what = null )
	{
		if ( node is not { IsValid: true } ) return default;

		return Require( node, kind, what );
	}

	//-- what comes out -------------------------------------------------------

	/// <summary>The graph, as the definition a compiled resource holds.</summary>
	public string Build( Node root )
	{
		var definition = new JsonObject
		{
			["Version"] = DefinitionVersion,
			["Skeleton"] = Skeleton ?? "",
			["Root"] = Ref( root ),
			["Parameters"] = parameters.DeepClone(),
			["Virtual"] = virtualParameters.DeepClone(),
			["Nodes"] = nodes.DeepClone(),
		};

		return definition.ToJsonString( new JsonSerializerOptions { WriteIndented = false } );
	}

	/// <summary>Build the graph and make a resource of it, for code that puts one together as it runs rather than in the editor.</summary>
	public AnimGraph Create( Node root, string name = null )
		=> AnimGraph.FromDefinition( Build( root ), name ?? "procedural" );
}
