namespace Editor.MdlEditor.Nodes;

/// <summary>One conditional route through a conduit, with blend timing and options</summary>
[Title( "Transition" ), Group( "State Machine" ), Icon( "route" )]
[AvailableIn( GraphKind.TransitionConduit )]
public class TransitionNode : Node
{
	/// <summary>Fires the transition when true. Unconnected disables the transition.</summary>
	[Input( typeof( BoolLink ) )]
	public NodeInput Condition { get; set; }

	/// <summary>Optional float overriding the blend duration in seconds.</summary>
	[Input( typeof( FloatLink ) ), Title( "Duration Override" )]
	public NodeInput DurationOverride { get; set; }

	/// <summary>Optional float overriding the time offset.</summary>
	[Input( typeof( FloatLink ) ), Title( "Time Offset Override" )]
	public NodeInput TimeOffsetOverride { get; set; }

	/// <summary>Optional bone mask blended in over the transition, letting parts of the body transition before others.</summary>
	[Input( typeof( BoneMaskLink ) ), Title( "Start Bone Mask" )]
	public NodeInput StartBoneMask { get; set; }

	/// <summary>Optional sync event ID to match in the target, for the sync-ID time match modes.</summary>
	[Input( typeof( IDLink ) ), Title( "Target Sync ID" )]
	public NodeInput TargetSyncID { get; set; }

	[Property]
	public string TransitionName { get; set; } = "Transition";

	/// <summary>Blend duration in seconds.</summary>
	[Property]
	public float Duration { get; set; } = 0.2f;

	/// <summary>How the target state's playback time relates to the source's during the blend.</summary>
	[Property]
	public TimeMatchMode TimeMatch { get; set; } = TimeMatchMode.None;

	/// <summary>Sync event offset, or seconds when time matching in seconds.</summary>
	[Property, HideIf( nameof( TimeMatch ), TimeMatchMode.None )]
	public float TimeOffset { get; set; }

	[Property]
	public AnimGraphBuilder.Easing BlendWeightEasing { get; set; } = AnimGraphBuilder.Easing.Linear;

	/// <summary>How root motion combines while blending.</summary>
	[Property]
	public AnimGraphBuilder.RootMotionBlend RootMotionBlend { get; set; } = AnimGraphBuilder.RootMotionBlend.Blend;

	/// <summary>Clamp the transition length to the remaining duration of the source state.</summary>
	[Property]
	public bool ClampDurationToSource { get; set; }

	/// <summary>Allow this transition to interrupt an in-progress transition to its source state.</summary>
	[Property]
	public bool CanBeForced { get; set; }

	/// <summary>How far through the transition the start bone mask blends out, as a percentage.</summary>
	[Property, Title( "Bone Mask Blend In %" ), Range( 0, 1 )]
	public float BoneMaskBlendInTimePercentage { get; set; } = 0.33f;

	public override Color PrimaryColor => Theme.Pink;

	public override NodeDisplay DisplayInfo
	{
		get
		{
			var info = Rinten.DisplayInfo.For( this );
			if ( !string.IsNullOrWhiteSpace( TransitionName ) )
				info.Name = TransitionName;
			return info;
		}
	}

}
