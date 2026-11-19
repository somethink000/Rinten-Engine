using Editor.NodeEditor;
using System.Text.Json.Serialization;

namespace Editor.MdlEditor.Nodes;

/// <summary>Plays one of several weighted poses picked at random, held until Reset</summary>
[Title( "Variation" ), Group( "Poses" ), Icon( "casino" )]
public class PoseVariationNode : Node, IReorderableInputNode
{
	/// <summary>One of the poses this picks between, and how often it comes up.</summary>
	public class Option
	{
		[Hide]
		public NodeInput Pose { get; set; }

		/// <summary>How often this one is picked, against the others. Zero never comes up.</summary>
		[Property]
		public int Weight { get; set; } = 1;
	}

	/// <summary>Rolls a new option while it is true. Nothing connected picks once and keeps that pick for the life of the graph.</summary>
	[Input( typeof( BoolLink ) )]
	public NodeInput Reset { get; set; }

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

	private readonly List<IPlugIn> _optionPlugs = new();

	protected override IEnumerable<IPlugIn> DynamicInputs
	{
		get
		{
			while ( _optionPlugs.Count > Options.Count )
				_optionPlugs.RemoveAt( _optionPlugs.Count - 1 );

			while ( _optionPlugs.Count < Options.Count )
			{
				var i = _optionPlugs.Count;
				var info = new AnimPlugInfo( "Option", i, index => $"Option {index + 1}",
					( _, index ) => index < Options.Count ? Options[index].Pose : default,
					( _, index, value ) => { if ( index < Options.Count ) Options[index].Pose = value; } );

				_optionPlugs.Add( new AnimPlugIn( this, info, typeof( PoseLink ) ) );
			}

			return _optionPlugs;
		}
	}

	bool IReorderableInputNode.CanReorderInput( IPlugIn input ) => _optionPlugs.Count > 1 && _optionPlugs.Contains( input );
	void IReorderableInputNode.ReorderInput( IPlugIn input, IPlugIn target ) => ReorderInput( input, target, Options, _optionPlugs );

	public override GraphMenu CreateContextMenu( NodeUI node )
	{
		var menu = new GraphMenu( "Variation" );
		menu.AddOption( "Add Option", "add", () => { Options.Add( new Option() ); Update(); } );

		if ( Options.Count > 1 )
			menu.AddOption( "Remove Last Option", "remove", () => { Options.RemoveAt( Options.Count - 1 ); Update(); } );

		return menu;
	}

	internal override AnimGraphBuilder.Node Compile( Compiler compiler )
	{
		var variation = compiler.Builder.Variation( compiler.EmitValue( this, Reset ) );
		var added = 0;

		foreach ( var option in Options )
		{
			//Skipped rather than given the reference pose. An option nobody wired is not "stand in the bind pose one time in three" - it is an
			//option that is not there, and drawing it as one is a character who snaps into a T every so often for no reason anybody can see.
			if ( GetConnectedNode( option.Pose ) is null ) continue;

			variation.Add( compiler.EmitPose( this, option.Pose, "Variation" ), Math.Max( option.Weight, 0 ) );
			++added;
		}

		if ( added != 0 ) return variation;

		compiler.Warn( "Variation has no options connected", this );
		return compiler.ReferencePose();
	}
}
