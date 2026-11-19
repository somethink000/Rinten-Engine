using System.Text.Json.Serialization;

namespace Editor.MdlEditor.Nodes;

/// <summary>Plays a clip whose time is driven by a float input (percentage through)</summary>
[Title( "Time Controlled Animation Clip" ), Group( "Animation" ), Icon( "history_toggle_off" )]
public class TimeControlledAnimationClipNode : VariationDataNode<TimeControlledAnimationClipNode.Data>
{
	/// <summary>The parts of a time controlled animation clip node that can differ per variation.</summary>
	public sealed class Data : VariationDataNode.VariationData
	{
		[Property, Title( "Animation" ), ModelClip]
		public string AnimationName { get; set; } = "";

		public override VisualState GetVisualState()
		{
			return string.IsNullOrWhiteSpace( AnimationName ) ? VisualState.HasUnsetData : VisualState.None;
		}
	}

	/// <summary>The animation of the variation being edited.</summary>
	[Hide, JsonIgnore]
	public string AnimationName
	{
		get => SelectedData.AnimationName;
		set => SelectedData.AnimationName = value;
	}

	[Hide, JsonPropertyName( "AnimationName" )]
	public string LegacyAnimationName
	{
		set => DefaultVariationData.AnimationName = value;
	}

	[Property]
	public bool SampleRootMotion { get; set; } = true;

	/// <summary>Graph event IDs fired while this clip plays.</summary>
	[Property]
	public List<string> GraphEvents { get; set; } = [];

	[Input( typeof( FloatLink ) ), Title( "Time" )]
	public NodeInput Time { get; set; }

	[Input( typeof( BoolLink ) ), Title( "Play In Reverse" )]
	public NodeInput PlayInReverse { get; set; }

	[Output( typeof( PoseLink ) )]
	[JsonIgnore]
	public object Result => default;

	public override Color PrimaryColor => Theme.Green;

	public override NodeDisplay DisplayInfo
	{
		get
		{
			var info = Rinten.DisplayInfo.For( this );
			if ( !string.IsNullOrWhiteSpace( AnimationName ) )
				info.Name = AnimationName;
			return info;
		}
	}

	internal override AnimGraphBuilder.Node Compile( Compiler compiler )
	{
		var data = (Data)GetResolvedVariationData( compiler.VariationHierarchy, compiler.VariationID );

		if ( string.IsNullOrWhiteSpace( data.AnimationName ) )
		{
			compiler.Warn( "Time Controlled Animation Clip has no animation selected" );
			return compiler.ReferencePose();
		}

		if ( GetConnectedNode( Time ) is null )
		{
			compiler.Warn( "Time Controlled Animation Clip requires a Time input", this );
			return compiler.ReferencePose();
		}

		var time = compiler.EmitValue( this, Time );
		var playInReverse = compiler.EmitValue( this, PlayInReverse );
		var graphEvents = GraphEvents?.Where( id => !string.IsNullOrWhiteSpace( id ) ) ?? [];

		return compiler.Builder.TimeControlledClip(
			data.AnimationName,
			time,
			new AnimGraphBuilder.TimeControlledClipSettings
			{
				PlayInReverse = playInReverse,
				SampleRootMotion = SampleRootMotion,
				GraphEvents = graphEvents,
			} );
	}
}
