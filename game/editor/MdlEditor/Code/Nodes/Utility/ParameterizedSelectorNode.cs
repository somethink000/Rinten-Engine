using Editor.NodeEditor;
using System.Text.Json.Serialization;

namespace Editor.MdlEditor.Nodes;

/// <summary>Picks a pose from a floored input value over weighted options, on entry only</summary>
[Title( "Parameterized Selector" ), Group( "Flow" ), Icon( "shuffle" )]
public class ParameterizedSelectorNode : VariationDataNode<ParameterizedSelectorNode.Data>
{
	public override void BuildContent( Rinten.UI.Panel body ) => EntrySelectionHint.Add( body );

	/// <summary>The parts of a parameterized selector that can differ per variation.</summary>
	public sealed class Data : VariationDataNode.VariationData
	{
		/// <summary>Relative weight of each option within the selection range, one per option.</summary>
		[Property, Range( 0, 255 )]
		public List<int> OptionWeights { get; set; } = [];
	}

	public class Option
	{
		/// <summary>Name shown on this option's input pin.</summary>
		[Property, KeyProperty]
		public string Label { get; set; } = "";

		[Hide]
		public NodeInput Pose { get; set; }
	}

	[Input( typeof( FloatLink ) )]
	public NodeInput Parameter { get; set; }

	[Property]
	public List<Option> Options { get; set; } =
	[
		new(),
		new(),
	];

	/// <summary>Skip options whose pose failed to compile instead of failing the whole node.</summary>
	[Property]
	public bool IgnoreInvalidOptions { get; set; } = false;

	[Output( typeof( PoseLink ) )]
	[JsonIgnore]
	public object Result => default;

	public override Color PrimaryColor => Color.Parse( "#e5c07b" ).Value;

	private readonly List<IPlugIn> _optionPlugs = new();

	protected override IEnumerable<IPlugIn> DynamicInputs
	{
		get
		{
			SyncOptionWeights();

			while ( _optionPlugs.Count > Options.Count )
				_optionPlugs.RemoveAt( _optionPlugs.Count - 1 );

			while ( _optionPlugs.Count < Options.Count )
			{
				var i = _optionPlugs.Count;
				var info = new AnimPlugInfo( $"Option{i}", () => OptionLabel( i ),
					_ => i < Options.Count ? Options[i].Pose : default,
					( _, value ) => { if ( i < Options.Count ) Options[i].Pose = value; } );

				_optionPlugs.Add( new AnimPlugIn( this, info, typeof( PoseLink ) ) );
			}

			return _optionPlugs;
		}
	}

	private string OptionLabel( int index )
	{
		if ( index >= Options.Count )
			return $"Option {index + 1}";

		return string.IsNullOrWhiteSpace( Options[index].Label ) ? $"Option {index + 1}" : Options[index].Label;
	}

	//Keep every variation's weight list the same length as the option list. Esoterica does this
	//from its pin creation and destruction hooks, we have no equivalent so we reconcile lazily.
	private void SyncOptionWeights()
	{
		foreach ( var data in AllVariationData )
		{
			data.OptionWeights ??= [];

			while ( data.OptionWeights.Count > Options.Count )
				data.OptionWeights.RemoveAt( data.OptionWeights.Count - 1 );

			while ( data.OptionWeights.Count < Options.Count )
				data.OptionWeights.Add( 0 );
		}
	}

	public override GraphMenu CreateContextMenu( NodeUI node )
	{
		var menu = new GraphMenu( "Parameterized Selector" );
		menu.AddOption( "Add Option", "add", () => { Options.Add( new Option() ); Update(); } );

		if ( Options.Count > 0 )
			menu.AddOption( "Remove Last Option", "remove", () => { Options.RemoveAt( Options.Count - 1 ); Update(); } );

		return menu;
	}

	internal override AnimGraphBuilder.Node Compile( Compiler compiler )
	{
		var title = this is ParameterizedAnimationClipSelectorNode ? "Parameterized Clip Selector" : "Parameterized Selector";

		if ( Options.Count == 0 )
		{
			compiler.Warn( $"{title} has no options" );
			return compiler.ReferencePose();
		}

		var parameter = compiler.EmitValue( this, Parameter );
		if ( parameter is null )
		{
			compiler.Warn( $"{title}'s parameter input isn't connected" );
			parameter = compiler.ConstFloat( 0.0f );
		}

		var isClip = this is ParameterizedAnimationClipSelectorNode;
		var options = new List<AnimGraphBuilder.Node>();
		foreach ( var option in Options )
		{
			var index = compiler.EmitPose( this, option.Pose, title );
			options.Add( index );
			isClip &= index.IsClip();
		}

		if ( this is ParameterizedAnimationClipSelectorNode && !isClip )
			compiler.Warn( "Parameterized Clip Selector options must be animation clips" );

		var hasWeights = false;
		var weights = new List<int>();

		SyncOptionWeights();
		var data = (Data)GetResolvedVariationData( compiler.VariationHierarchy, compiler.VariationID );

		for ( var i = 1; i < data.OptionWeights.Count; i++ )
			hasWeights |= data.OptionWeights[i] != data.OptionWeights[0];

		if ( hasWeights )
			weights = [.. data.OptionWeights.Select( weight => Math.Clamp( weight, 0, 255 ) )];

		return compiler.Builder.ParameterizedSelector(
			parameter,
			options,
			new AnimGraphBuilder.ParameterizedSelectorSettings
			{
				Weights = weights,
				IgnoreInvalidOptions = IgnoreInvalidOptions,
				HasWeights = hasWeights,
				ClipsOnly = isClip,
			} );
	}
}
