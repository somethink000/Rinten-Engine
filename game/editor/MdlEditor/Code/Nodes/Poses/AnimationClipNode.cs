using System.Text.Json.Serialization;

namespace Editor.MdlEditor.Nodes;

/// <summary>Plays an animation clip, with looping, root motion, speed and sync options</summary>
[Title( "Animation Clip" ), Group( "Animation" ), Icon( "movie" )]
public class AnimationClipNode : VariationDataNode<AnimationClipNode.Data>
{
	/// <summary>The parts of an animation clip node that can differ per variation.</summary>
	public sealed class Data : VariationDataNode.VariationData
	{
		[Property, Title( "Animation" ), ModelClip]
		public string AnimationName { get; set; } = "";

		[Property, Range( 0.01f, 5.0f )]
		public float SpeedMultiplier { get; set; } = 1.0f;

		[Property]
		public int StartSyncEventOffset { get; set; }

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

	/// <summary>The playback speed of the variation being edited.</summary>
	[Hide, JsonIgnore]
	public float SpeedMultiplier
	{
		get => SelectedData.SpeedMultiplier;
		set => SelectedData.SpeedMultiplier = value;
	}

	[Hide, JsonPropertyName( "AnimationName" )]
	public string LegacyAnimationName
	{
		set => DefaultVariationData.AnimationName = value;
	}

	[Hide, JsonPropertyName( "SpeedMultiplier" )]
	public float LegacySpeedMultiplier
	{
		set => DefaultVariationData.SpeedMultiplier = value;
	}

	[Hide, JsonPropertyName( "StartSyncEventOffset" )]
	public int LegacyStartSyncEventOffset
	{
		set => DefaultVariationData.StartSyncEventOffset = value;
	}

	[Property]
	public bool Loop { get; set; } = true;

	[Property]
	public bool SampleRootMotion { get; set; } = true;

	/// <summary>Graph event IDs fired while this clip plays.</summary>
	[Property]
	public List<string> GraphEvents { get; set; } = [];

	//No inputs: reverse with a negative speed; graph-driven time uses Time Controlled Animation Clip

	[Output( typeof( PoseLink ) )]
	[JsonIgnore]
	public object Result => default;

	public override Color PrimaryColor => Theme.Green;

	public override NodeDisplay DisplayInfo
	{
		get
		{
			var info = Rinten.DisplayInfo.For( this );
			if ( !string.IsNullOrWhiteSpace( SelectedData.AnimationName ) )
				info.Name = SelectedData.AnimationName;
			return info;
		}
	}

	internal override AnimGraphBuilder.Node Compile( Compiler compiler )
	{
		var data = (Data)GetResolvedVariationData( compiler.VariationHierarchy, compiler.VariationID );

		if ( string.IsNullOrWhiteSpace( data.AnimationName ) )
		{
			compiler.Warn( "Animation Clip has no animation selected" );
			return compiler.ReferencePose();
		}

		var graphEvents = GraphEvents?.Where( id => !string.IsNullOrWhiteSpace( id ) ) ?? [];

		return compiler.Builder.Clip(
			data.AnimationName,
			new AnimGraphBuilder.ClipSettings
			{
				Looping = Loop,
				Speed = data.SpeedMultiplier,
				SyncEventOffset = data.StartSyncEventOffset,
				SampleRootMotion = SampleRootMotion,
				GraphEvents = graphEvents,
			} );
	}
}
