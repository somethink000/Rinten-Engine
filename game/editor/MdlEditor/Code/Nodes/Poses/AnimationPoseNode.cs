using System.Text.Json.Serialization;

namespace Editor.MdlEditor.Nodes;

/// <summary>Samples one static pose from an animation at a fixed or driven time</summary>
[Title( "Animation Pose" ), Group( "Animation" ), Icon( "person" )]
public class AnimationPoseNode : VariationDataNode<AnimationPoseNode.Data>
{
	/// <summary>The parts of an animation pose node that can differ per variation.</summary>
	public sealed class Data : VariationDataNode.VariationData
	{
		[Property, Title( "Animation" ), ModelClip]
		public string AnimationName { get; set; } = "";

		public override VisualState GetVisualState()
		{
			return string.IsNullOrWhiteSpace( AnimationName ) ? VisualState.HasUnsetData : VisualState.None;
		}
	}

	[Input( typeof( FloatLink ) )]
	public NodeInput Time { get; set; }

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

	/// <summary>Remaps the input time value from this range into 0-1.</summary>
	[Property]
	public float RemapRangeStart { get; set; } = 0.0f;

	[Property]
	public float RemapRangeEnd { get; set; } = 1.0f;

	/// <summary>Time to sample at when the input is not connected.</summary>
	[Property]
	public float FixedTime { get; set; } = 0.0f;

	/// <summary>Interpret the input time in frames instead of a 0-1 percentage.</summary>
	[Property]
	public bool UseFramesAsInput { get; set; } = false;

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
				info.Name = $"Pose: {AnimationName}";
			return info;
		}
	}

	internal override AnimGraphBuilder.Node Compile( Compiler compiler )
	{
		var data = (Data)GetResolvedVariationData( compiler.VariationHierarchy, compiler.VariationID );

		if ( string.IsNullOrWhiteSpace( data.AnimationName ) )
		{
			compiler.Warn( "Animation Pose has no animation selected" );
			return compiler.ReferencePose();
		}

		var time = compiler.EmitValue( this, Time );
		return compiler.Builder.Pose(
			data.AnimationName,
			new AnimGraphBuilder.PoseSettings
			{
				Time = time,
				RemapRangeStart = RemapRangeStart,
				RemapRangeEnd = RemapRangeEnd,
				FixedTime = FixedTime,
				UseFramesAsInput = UseFramesAsInput,
			} );
	}
}
