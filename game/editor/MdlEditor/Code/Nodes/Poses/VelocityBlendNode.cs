using Editor.NodeEditor;
using System.Text.Json.Serialization;

namespace Editor.MdlEditor.Nodes;

/// <summary>Blends Animation Clips along an axis derived from their root-motion speeds</summary>
[Title( "Blend 1D (Velocity)" ), Group( "Blend" ), Icon( "speed" )]
public class VelocityBlendNode : Node, IReorderableInputNode
{
	public class Option
	{
		[Hide]
		public NodeInput Pose { get; set; }
	}

	[Input( typeof( FloatLink ) )]
	public NodeInput Parameter { get; set; }

	[Property, Hide]
	public List<Option> Options { get; set; } =
	[
		new(),
		new(),
	];

	[Output( typeof( PoseLink ) )]
	[JsonIgnore]
	public object Result => default;

	public override Color PrimaryColor => Theme.Blue;

	private readonly List<IPlugIn> _inputPlugs = new();

	protected override IEnumerable<IPlugIn> DynamicInputs
	{
		get
		{
			while ( _inputPlugs.Count > Options.Count )
				_inputPlugs.RemoveAt( _inputPlugs.Count - 1 );

			while ( _inputPlugs.Count < Options.Count )
			{
				var i = _inputPlugs.Count;
				var info = new AnimPlugInfo( "Input", i, index => $"Input {index + 1}",
					( _, index ) => index < Options.Count ? Options[index].Pose : default,
					( _, index, value ) => { if ( index < Options.Count ) Options[index].Pose = value; } );

				_inputPlugs.Add( new AnimPlugIn( this, info, typeof( PoseLink ) ) );
			}

			return _inputPlugs;
		}
	}

	bool IReorderableInputNode.CanReorderInput( IPlugIn input ) => _inputPlugs.Count > 1 && _inputPlugs.Contains( input );
	void IReorderableInputNode.ReorderInput( IPlugIn input, IPlugIn target ) => ReorderInput( input, target, Options, _inputPlugs );

	public override GraphMenu CreateContextMenu( NodeUI node )
	{
		var menu = new GraphMenu( "Blend 1D (Velocity)" );
		menu.AddOption( "Add Input", "add", () => { Options.Add( new Option() ); Update(); } );

		if ( Options.Count > 2 )
			menu.AddOption( "Remove Last Input", "remove", () => { Options.RemoveAt( Options.Count - 1 ); Update(); } );

		return menu;
	}

	internal override AnimGraphBuilder.Node Compile( Compiler compiler )
	{
		if ( Options.Count < 2 )
		{
			compiler.Warn( "Blend 1D (Velocity) needs at least 2 inputs" );
			return compiler.ReferencePose();
		}

		var parameter = compiler.EmitValue( this, Parameter );
		if ( parameter is null )
			parameter = compiler.ConstFloat( 0 );

		//The runtime derives the parameterization from clip speeds, so every input must be a clip
		var sources = new List<AnimGraphBuilder.Node>();
		var allClips = true;

		foreach ( var input in Options )
		{
			var index = compiler.EmitPose( this, input.Pose, "Blend 1D (Velocity)", synchronized: true );
			sources.Add( index );
			allClips &= index.IsClip();
		}

		if ( !allClips )
		{
			compiler.Warn( "Blend 1D (Velocity) inputs must be animation clips" );
			return compiler.ReferencePose();
		}

		return compiler.Builder.VelocityBlend( parameter, [.. sources] );
	}
}
