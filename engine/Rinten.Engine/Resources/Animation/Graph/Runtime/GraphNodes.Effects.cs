using System.Text.Json.Nodes;
using Rinten.CoreAnimation;

namespace Rinten.Animation;

//Nodes that change a pose after it has been made: reaching for something, going
//somewhere other than where the animation goes, playing at a speed the game asked for.
internal static class Effects
{
	//The first bone with nothing above it. Everything that moves a character rather than posing one works on this: the rest of the skeleton hangs off it and comes along.
	/// <summary>A pose's root bone, which is where an animation carries the character.</summary>
	public static int RootBone( GraphContext context )
	{
		if ( context.Skeleton is not { } skeleton ) return -1;

		for ( var bone = 0; bone < skeleton.BoneCount; bone++ )
		{
			if ( skeleton.BoneParent( bone ) < 0 ) return bone;
		}

		return -1;
	}

	/// <summary>Two-bone IK onto a target, using a model-defined chain or a bare bone with its two parents</summary>
	public static unsafe bool Reach( GraphContext context, int pose, string chain, string bone,
		in Transform where, float weight, float twist,
		AnimGraphBuilder.IKBlendMode blend, out string missing )
	{
		missing = null;

		var limb = string.IsNullOrWhiteSpace( chain )
			? default
			: CoreSkeletons.IkChainOf( context.Model, chain );

		var index = -1;

		//No chain named, or none the model has: the bone field is what is left, and a bone of that name wins over a chain of it.
		if ( !limb.IsValid )
		{
			index = context.FindBone( bone );

			//Their graphs name the effector by an IK chain rather than by a bone, and a ported one arrives with the name in the bone field.
			if ( index < 0 ) limb = CoreSkeletons.IkChainOf( context.Model, bone );
		}

		if ( index < 0 && !limb.IsValid )
		{
			missing = string.IsNullOrWhiteSpace( chain ) ? bone : chain;
			return false;
		}

		var target = where;
		var solve = weight;

		//Partial weight: Pose blends solved bones; Effector blends the target, then solves fully
		if ( blend == AnimGraphBuilder.IKBlendMode.Effector && weight < 1.0f )
		{
			if ( TipOf( context, pose, limb.IsValid ? limb.Tip : index, out var tip ) )
			{
				target = Transform.Lerp( tip, where, weight, true );
				solve = 1.0f;
			}
		}

		var reach = AnimTrs.FromTransform( target );

		if ( !limb.IsValid )
		{
			CoreAnimRuntime.PoseTwoBoneIk( context.Instance, pose, index, reach, solve, twist );
			return true;
		}

		//The chain's twist unless the node asked for one of its own, which is what makes a chain a default rather than a rule.
		CoreAnimRuntime.PoseIkChain( context.Instance, pose,
			limb.Bones, reach, solve,
			twist > 0.0f ? twist : limb.Twist,
			limb.UseKnee, limb.Knee );

		return true;
	}

	//Chain tip position in this branch's pose (not last frame's), the start of an effector blend
	private static bool TipOf( GraphContext context, int pose, int tip, out Transform where )
	{
		where = Transform.Zero;

		if ( tip < 0 || pose < 0 ) return false;

		var live = context.Instance;

		var local = live?.Pose( pose );
		if ( local is null || tip >= local.Length ) return false;

		var found = CoreAnimRuntime.ModelOf( local, live.Skeleton, tip );

		where = new Transform( found.Position, found.Rotation, found.Scale.x );

		return true;
	}
}

//Bend a limb so its tip lands on a target.
internal sealed class IkNode : PoseNode
{
	private readonly int pose, target, enabled;
	private readonly string chain, bone;
	private readonly float blendTime, twist;
	private readonly bool worldSpace;
	private readonly AnimGraphBuilder.IKBlendMode blend;

	private GraphNode input, targetNode, enabledNode;

	private float weight;
	private int frame = -1;

	public IkNode( JsonObject node )
	{
		pose = GraphFields.Reference( node, "Pose" );
		target = GraphFields.Reference( node, "Target" );
		enabled = GraphFields.Reference( node, "Enabled" );

		//The limb the model names. Empty on a graph written before there were chains, which named a bone and let the skeleton say the rest.
		chain = GraphFields.Text( node, "Chain" );

		bone = GraphFields.Text( node, "Bone" );
		blendTime = GraphFields.Number( node, "BlendTime" );
		twist = GraphFields.Number( node, "Twist" );
		worldSpace = GraphFields.Flag( node, "WorldSpace" );

		blend = GraphFields.Enum( node, "BlendMode", AnimGraphBuilder.IKBlendMode.Effector );
	}

	public override void Link( GraphNode[] nodes )
	{
		input = ValueNodes.Get( nodes, pose );
		targetNode = ValueNodes.Get( nodes, target );
		enabledNode = ValueNodes.Get( nodes, enabled );
	}

	public override void Reset( GraphContext context ) => weight = 0.0f;

	protected override int OnPose( GraphContext context )
	{
		var result = input?.Pose( context ) ?? context.RestPose();

		//On and off are blended rather than switched: an arm that snaps to a target is worse than one that takes a moment to reach it.
		if ( frame != context.Frame )
		{
			frame = context.Frame;

			var wanted = ( enabledNode?.Bool( context ) ?? true ) && ( targetNode?.HasTarget( context ) ?? false )
				? 1.0f
				: 0.0f;

			weight = blendTime <= 0.0f
				? wanted
				: Math.Clamp( weight + ( ( wanted > weight ? 1.0f : -1.0f ) * ( context.Delta / blendTime ) ), 0.0f, 1.0f );
		}

		if ( weight <= 0.0f ) return result;

		//A bone target is read out of the pose being solved rather than out of the one the character is standing
		//in: the rule bones their clips carry are animated, and the value wanted is this frame's, on this branch.
		var where = targetNode is BoneTargetNode from && from.ReadFrom( context, result, out var at )
			? at
			: targetNode?.Target( context ) ?? Transform.Zero;

		//The solver works in the model's own space, because that is the space a pose is
		//in. A target given in the world comes back through the object it belongs to.
		if ( worldSpace ) where = context.ToModel( where );

		if ( !Effects.Reach( context, result, chain, bone, where, weight, twist, blend, out var missing ) )
			Owner.MissingBone( missing );

		return result;
	}

	public override float Duration( GraphContext context ) => input?.Duration( context ) ?? 0.0f;
	public override float Progress( GraphContext context ) => input?.Progress( context ) ?? 0.0f;

	//Lining up reaches through to what is wrapped: a transition matching times means the clip's time, whatever is sitting on top of it.
	public override void Seek( GraphContext context, float time ) => input?.Seek( context, time );

	public override bool CurrentSync( GraphContext context, out SyncPoint point )
	{
		point = default;
		return input?.CurrentSync( context, out point ) ?? false;
	}

	public override bool SeekToSync( GraphContext context, in SyncPoint point,
		AnimGraphBuilder.TransitionSyncMatch match, bool preferClosestId )
		=> input?.SeekToSync( context, point, match, preferClosestId ) ?? false;

	public override int SyncTrack( GraphContext context, ref AnimGraphSyncTrackEvent[] into )
		=> input?.SyncTrack( context, ref into ) ?? 0;

	public override float Speed( GraphContext context ) => input?.Speed( context ) ?? 0.0f;
}

//Aims part of a character at a target, spreading the turn down a model-named chain
internal sealed class LookAtNode : PoseNode
{
	private readonly int pose, direction, weight;
	private readonly string chain, attachment;
	private readonly float limit, yawLimit, pitchLimit;

	private GraphNode input, directionNode, weightNode;

	//Which bones, worked out once the model is known.
	private int[] bones;
	private float[] weights;
	private bool looked;

	//What actually points, and which way it faces on its bone.
	private int aimBone = -1;
	private Vector3 aimAxis = Vector3.Forward;

	//Whether that way is written against the model rather than a bone.
	private bool aimInModel;

	public LookAtNode( JsonObject node )
	{
		pose = GraphFields.Reference( node, "Pose" );
		direction = GraphFields.Reference( node, "Direction" );
		weight = GraphFields.Reference( node, "Weight" );

		chain = GraphFields.Text( node, "Chain" );

		//What does the pointing: the eyes, a hold point, a marker on the head. Empty aims the chain's first bone by its own forward.
		attachment = GraphFields.Text( node, "Attachment" );

		limit = GraphFields.Number( node, "Limit" );

		//A graph written before the two were told apart has neither, and a half turn each way leaves only the cone above doing any limiting.
		yawLimit = GraphFields.Number( node, "YawLimit", 180.0f );
		pitchLimit = GraphFields.Number( node, "PitchLimit", 180.0f );
	}

	public override void Link( GraphNode[] nodes )
	{
		input = ValueNodes.Get( nodes, pose );
		directionNode = ValueNodes.Get( nodes, direction );
		weightNode = ValueNodes.Get( nodes, weight );
	}

	protected override int OnPose( GraphContext context )
	{
		var result = input?.Pose( context ) ?? context.RestPose();

		if ( directionNode is null ) return result;

		//The chain is a fact about the model and does not change under us, so it is looked up the first time and kept.
		if ( !looked )
		{
			looked = true;

			var found = CoreSkeletons.Chain( context.Model, chain );

			bones = found.Bones;
			weights = found.Weights;

			if ( bones.Length == 0 ) Owner.MissingBone( chain );

			if ( !string.IsNullOrEmpty( attachment ) )
			{
				var aim = CoreSkeletons.Aim( context.Model, attachment );

				aimBone = aim.Bone;
				aimAxis = aim.Forward;
				aimInModel = aim.InModel;

				//A model without it still aims, by the head of its chain. One that faces one way whatever its bones do has no bone to name, and is not missing.
				if ( aimBone < 0 && !aimInModel ) Owner.MissingBone( attachment );
			}
		}

		if ( bones is not { Length: > 0 } ) return result;

		//No weight given is all of it: their graphs leave it unconnected on the chains that always aim.
		var much = weightNode?.Float( context ) ?? 1.0f;

		if ( much <= 0.0f ) return result;

		CoreAnimRuntime.PoseLookAt( context.Instance, result, bones, weights,
			directionNode.Vector( context ), much, limit, yawLimit, pitchLimit,
			aimBone, aimAxis, aimInModel );

		return result;
	}

	public override float Duration( GraphContext context ) => input?.Duration( context ) ?? 0.0f;
	public override float Progress( GraphContext context ) => input?.Progress( context ) ?? 0.0f;

	public override void Seek( GraphContext context, float time ) => input?.Seek( context, time );

	public override bool CurrentSync( GraphContext context, out SyncPoint point )
	{
		point = default;
		return input?.CurrentSync( context, out point ) ?? false;
	}

	public override bool SeekToSync( GraphContext context, in SyncPoint point,
		AnimGraphBuilder.TransitionSyncMatch match, bool preferClosestId )
		=> input?.SeekToSync( context, point, match, preferClosestId ) ?? false;

	public override int SyncTrack( GraphContext context, ref AnimGraphSyncTrackEvent[] into )
		=> input?.SyncTrack( context, ref into ) ?? 0;

	public override float Speed( GraphContext context ) => input?.Speed( context ) ?? 0.0f;
}

//The same for both feet, which is how ground is stood on.
internal sealed class FootIKNode : PoseNode
{
	private readonly int pose, leftTarget, rightTarget, enabled;
	private readonly string leftChain, rightChain, left, right;
	private readonly float blendTime;
	private readonly bool worldSpace;
	private readonly AnimGraphBuilder.IKBlendMode blend;

	private GraphNode input, leftNode, rightNode, enabledNode;

	private float weight;
	private int frame = -1;

	public FootIKNode( JsonObject node )
	{
		pose = GraphFields.Reference( node, "Pose" );
		leftTarget = GraphFields.Reference( node, "LeftTarget" );
		rightTarget = GraphFields.Reference( node, "RightTarget" );
		enabled = GraphFields.Reference( node, "Enabled" );

		leftChain = GraphFields.Text( node, "LeftChain" );
		rightChain = GraphFields.Text( node, "RightChain" );

		left = GraphFields.Text( node, "Left" );
		right = GraphFields.Text( node, "Right" );

		blendTime = GraphFields.Number( node, "BlendTime" );
		worldSpace = GraphFields.Flag( node, "WorldSpace" );

		blend = GraphFields.Enum( node, "BlendMode", AnimGraphBuilder.IKBlendMode.Effector );
	}

	public override void Link( GraphNode[] nodes )
	{
		input = ValueNodes.Get( nodes, pose );
		leftNode = ValueNodes.Get( nodes, leftTarget );
		rightNode = ValueNodes.Get( nodes, rightTarget );
		enabledNode = ValueNodes.Get( nodes, enabled );
	}

	public override void Reset( GraphContext context ) => weight = 0.0f;

	protected override int OnPose( GraphContext context )
	{
		var result = input?.Pose( context ) ?? context.RestPose();

		if ( frame != context.Frame )
		{
			frame = context.Frame;

			var wanted = enabledNode?.Bool( context ) ?? true ? 1.0f : 0.0f;

			weight = blendTime <= 0.0f
				? wanted
				: Math.Clamp( weight + ( ( wanted > weight ? 1.0f : -1.0f ) * ( context.Delta / blendTime ) ), 0.0f, 1.0f );
		}

		if ( weight <= 0.0f ) return result;

		Plant( context, result, leftChain, left, leftNode );
		Plant( context, result, rightChain, right, rightNode );

		return result;
	}

	private void Plant( GraphContext context, int pose, string chain, string bone, GraphNode target )
	{
		if ( target is null || !target.HasTarget( context ) ) return;

		var where = target.Target( context );
		if ( worldSpace ) where = context.ToModel( where );

		if ( !Effects.Reach( context, pose, chain, bone, where, weight, 0.0f, blend, out var missing ) )
			Owner.MissingBone( missing );
	}

	public override float Duration( GraphContext context ) => input?.Duration( context ) ?? 0.0f;
	public override float Progress( GraphContext context ) => input?.Progress( context ) ?? 0.0f;

	//Lining up reaches through to what is wrapped: a transition matching times means the clip's time, whatever is sitting on top of it.
	public override void Seek( GraphContext context, float time ) => input?.Seek( context, time );

	public override bool CurrentSync( GraphContext context, out SyncPoint point )
	{
		point = default;
		return input?.CurrentSync( context, out point ) ?? false;
	}

	public override bool SeekToSync( GraphContext context, in SyncPoint point,
		AnimGraphBuilder.TransitionSyncMatch match, bool preferClosestId )
		=> input?.SeekToSync( context, point, match, preferClosestId ) ?? false;

	public override int SyncTrack( GraphContext context, ref AnimGraphSyncTrackEvent[] into )
		=> input?.SyncTrack( context, ref into ) ?? 0;

	public override float Speed( GraphContext context ) => input?.Speed( context ) ?? 0.0f;
}

//Take the movement out of the animation and put the game's in. What makes a walk cycle go where the character is actually
//going rather than where it was animated to go. The root bone carries a character; this rewrites what it carries it by.
internal sealed class RootMotionOverrideNode : PoseNode
{
	private readonly int pose, velocity, facing, linearLimit, angularLimit;
	private readonly float maxLinear, maxAngular;
	private readonly AnimGraphBuilder.RootMotionOverrideFlags flags;

	private GraphNode input, velocityNode, facingNode, linearNode, angularNode;

	private Transform carried = Transform.Zero;
	private bool started;
	private int frame = -1;

	public RootMotionOverrideNode( JsonObject node )
	{
		pose = GraphFields.Reference( node, "Pose" );
		velocity = GraphFields.Reference( node, "Velocity" );
		facing = GraphFields.Reference( node, "Facing" );
		linearLimit = GraphFields.Reference( node, "LinearLimit" );
		angularLimit = GraphFields.Reference( node, "AngularLimit" );

		maxLinear = GraphFields.Number( node, "MaxLinear" );
		maxAngular = GraphFields.Number( node, "MaxAngular" );

		flags = (AnimGraphBuilder.RootMotionOverrideFlags)GraphFields.Integer( node, "Flags" );
	}

	public override void Link( GraphNode[] nodes )
	{
		input = ValueNodes.Get( nodes, pose );
		velocityNode = ValueNodes.Get( nodes, velocity );
		facingNode = ValueNodes.Get( nodes, facing );
		linearNode = ValueNodes.Get( nodes, linearLimit );
		angularNode = ValueNodes.Get( nodes, angularLimit );
	}

	public override void Reset( GraphContext context )
	{
		started = false;
		carried = Transform.Zero;
	}

	protected override unsafe int OnPose( GraphContext context )
	{
		var result = input?.Pose( context ) ?? context.RestPose();

		var root = Effects.RootBone( context );
		if ( root < 0 ) return result;

		if ( frame != context.Frame )
		{
			frame = context.Frame;

			if ( !started )
			{
				started = true;

				if ( context.Instance.ReadBone( result, root, out var trs ) )
					carried = trs.ToTransform();
			}

			Carry( context );
		}

		context.Instance.WriteBone( result, root, AnimTrs.FromTransform( carried ) );

		return result;
	}

	//Move the root by what the game says, rather than by the clip.
	private void Carry( GraphContext context )
	{
		var speed = velocityNode?.Vector( context ) ?? Vector3.Zero;

		var limit = linearNode is not null ? linearNode.Float( context ) : maxLinear;
		if ( limit > 0.0f && speed.Length > limit ) speed = speed.Normal * limit;

		//Only the axes the graph said it drives: a character that is walked along the
		//ground by the game but still bobs with the animation is two decisions, not one.
		var step = speed * context.Delta;

		var position = carried.Position;

		if ( ( ( flags & AnimGraphBuilder.RootMotionOverrideFlags.MoveX ) == AnimGraphBuilder.RootMotionOverrideFlags.MoveX ) ) position.x += step.x;
		if ( ( ( flags & AnimGraphBuilder.RootMotionOverrideFlags.MoveY ) == AnimGraphBuilder.RootMotionOverrideFlags.MoveY ) ) position.y += step.y;
		if ( ( ( flags & AnimGraphBuilder.RootMotionOverrideFlags.MoveZ ) == AnimGraphBuilder.RootMotionOverrideFlags.MoveZ ) ) position.z += step.z;

		carried.Position = position;

		var direction = facingNode?.Vector( context ) ?? Vector3.Zero;

		//A character told to face something above it looks up only if the graph said it should - otherwise it turns towards it and stays upright.
		if ( !( ( flags & AnimGraphBuilder.RootMotionOverrideFlags.FacingPitch ) == AnimGraphBuilder.RootMotionOverrideFlags.FacingPitch ) ) direction.y = 0.0f;

		if ( direction.Length <= 0.001f ) return;

		var wanted = Rotation.LookAt( direction.Normal );

		var turn = angularNode is not null ? angularNode.Float( context ) : maxAngular;

		//A turn rate of nothing means as fast as it likes; anything else is degrees a second, which is what stops a character spinning on the spot.
		carried.Rotation = turn <= 0.0f
			? wanted
			: Rotation.Lerp( carried.Rotation, wanted,
				Math.Clamp( ( turn * context.Delta ) / MathF.Max( Rotation.Difference( carried.Rotation, wanted ).Angle(), 0.001f ), 0.0f, 1.0f ) );
	}

	public override float Duration( GraphContext context ) => input?.Duration( context ) ?? 0.0f;
	public override float Progress( GraphContext context ) => input?.Progress( context ) ?? 0.0f;

	//Lining up reaches through to what is wrapped: a transition matching times means the clip's time, whatever is sitting on top of it.
	public override void Seek( GraphContext context, float time ) => input?.Seek( context, time );

	public override bool CurrentSync( GraphContext context, out SyncPoint point )
	{
		point = default;
		return input?.CurrentSync( context, out point ) ?? false;
	}

	public override bool SeekToSync( GraphContext context, in SyncPoint point,
		AnimGraphBuilder.TransitionSyncMatch match, bool preferClosestId )
		=> input?.SeekToSync( context, point, match, preferClosestId ) ?? false;

	public override int SyncTrack( GraphContext context, ref AnimGraphSyncTrackEvent[] into )
		=> input?.SyncTrack( context, ref into ) ?? 0;

	public override float Speed( GraphContext context ) => input?.Speed( context ) ?? 0.0f;
}

//Warps the animation's root path to face a wanted direction, spread evenly over the clip
internal sealed class OrientationWarpNode : PoseNode
{
	private readonly int pose, direction;
	private readonly bool relative;

	private GraphNode input, directionNode;

	public OrientationWarpNode( JsonObject node )
	{
		pose = GraphFields.Reference( node, "Pose" );
		direction = GraphFields.Reference( node, "Direction" );
		relative = GraphFields.Flag( node, "Relative" );
	}

	public override void Link( GraphNode[] nodes )
	{
		input = ValueNodes.Get( nodes, pose );
		directionNode = ValueNodes.Get( nodes, direction );
	}

	protected override unsafe int OnPose( GraphContext context )
	{
		var result = input?.Pose( context ) ?? context.RestPose();

		var wanted = directionNode?.Vector( context ) ?? Vector3.Zero;
		if ( wanted.Length <= 0.001f ) return result;

		var root = Effects.RootBone( context );
		if ( root < 0 ) return result;

		if ( !context.Instance.ReadBone( result, root, out var trs ) ) return result;

		var current = trs.ToTransform();

		var facing = relative ? Rotation.LookAt( wanted.Normal ) * current.Rotation : Rotation.LookAt( wanted.Normal );

		//How far through the animation is decides how much of the turn has happened, so it arrives with the clip rather than before it.
		var progress = Math.Clamp( input?.Progress( context ) ?? 1.0f, 0.0f, 1.0f );

		current.Rotation = Rotation.Lerp( current.Rotation, facing, progress );

		context.Instance.WriteBone( result, root, AnimTrs.FromTransform( current ) );

		return result;
	}

	public override float Duration( GraphContext context ) => input?.Duration( context ) ?? 0.0f;
	public override float Progress( GraphContext context ) => input?.Progress( context ) ?? 0.0f;

	//Lining up reaches through to what is wrapped: a transition matching times means the clip's time, whatever is sitting on top of it.
	public override void Seek( GraphContext context, float time ) => input?.Seek( context, time );

	public override bool CurrentSync( GraphContext context, out SyncPoint point )
	{
		point = default;
		return input?.CurrentSync( context, out point ) ?? false;
	}

	public override bool SeekToSync( GraphContext context, in SyncPoint point,
		AnimGraphBuilder.TransitionSyncMatch match, bool preferClosestId )
		=> input?.SeekToSync( context, point, match, preferClosestId ) ?? false;

	public override int SyncTrack( GraphContext context, ref AnimGraphSyncTrackEvent[] into )
		=> input?.SyncTrack( context, ref into ) ?? 0;

	public override float Speed( GraphContext context ) => input?.Speed( context ) ?? 0.0f;
}

//The same, towards a place rather than a direction.
internal sealed class TargetWarpNode : PoseNode
{
	private readonly int pose, target;
	private readonly float lerpFallback;

	private GraphNode input, targetNode;

	public TargetWarpNode( JsonObject node )
	{
		pose = GraphFields.Reference( node, "Pose" );
		target = GraphFields.Reference( node, "Target" );
		lerpFallback = GraphFields.Number( node, "LerpFallback" );
	}

	public override void Link( GraphNode[] nodes )
	{
		input = ValueNodes.Get( nodes, pose );
		targetNode = ValueNodes.Get( nodes, target );
	}

	protected override unsafe int OnPose( GraphContext context )
	{
		var result = input?.Pose( context ) ?? context.RestPose();

		if ( targetNode is null || !targetNode.HasTarget( context ) ) return result;

		var root = Effects.RootBone( context );
		if ( root < 0 ) return result;

		if ( !context.Instance.ReadBone( result, root, out var trs ) ) return result;

		var current = trs.ToTransform();
		var wanted = targetNode.Target( context );

		var progress = Math.Clamp( input?.Progress( context ) ?? 1.0f, 0.0f, 1.0f );

		//Beyond a distance the warp gives up and simply moves towards the target: bending a step animation across a room is not a step.
		if ( lerpFallback > 0.0f && current.Position.Distance( wanted.Position ) > lerpFallback ) progress = 1.0f;

		current.Position = Vector3.Lerp( current.Position, wanted.Position, progress );
		current.Rotation = Rotation.Lerp( current.Rotation, wanted.Rotation, progress );

		context.Instance.WriteBone( result, root, AnimTrs.FromTransform( current ) );

		return result;
	}

	public override float Duration( GraphContext context ) => input?.Duration( context ) ?? 0.0f;
	public override float Progress( GraphContext context ) => input?.Progress( context ) ?? 0.0f;

	//Lining up reaches through to what is wrapped: a transition matching times means the clip's time, whatever is sitting on top of it.
	public override void Seek( GraphContext context, float time ) => input?.Seek( context, time );

	public override bool CurrentSync( GraphContext context, out SyncPoint point )
	{
		point = default;
		return input?.CurrentSync( context, out point ) ?? false;
	}

	public override bool SeekToSync( GraphContext context, in SyncPoint point,
		AnimGraphBuilder.TransitionSyncMatch match, bool preferClosestId )
		=> input?.SeekToSync( context, point, match, preferClosestId ) ?? false;

	public override int SyncTrack( GraphContext context, ref AnimGraphSyncTrackEvent[] into )
		=> input?.SyncTrack( context, ref into ) ?? 0;

	public override float Speed( GraphContext context ) => input?.Speed( context ) ?? 0.0f;
}

//Play what is under this in the time asked for.
internal sealed class DurationScaleNode : PoseNode
{
	private readonly int pose, duration;
	private readonly float fallback;

	private GraphNode input, wanted;

	public DurationScaleNode( JsonObject node )
	{
		pose = GraphFields.Reference( node, "Pose" );
		duration = GraphFields.Reference( node, "Duration" );
		fallback = GraphFields.Number( node, "Default" );
	}

	public override void Link( GraphNode[] nodes )
	{
		input = ValueNodes.Get( nodes, pose );
		wanted = ValueNodes.Get( nodes, duration );
	}

	protected override int OnPose( GraphContext context )
	{
		var asked = wanted is null ? fallback : wanted.Float( context );
		var own = input?.Duration( context ) ?? 0.0f;

		//Nothing to stretch, or nowhere to stretch it to: played as it is, rather than at a speed worked out by dividing by zero.
		var scale = asked > 0.0f && own > 0.0f ? own / asked : 1.0f;

		var was = context.Delta;
		context.Scale( scale );

		try
		{
			return input?.Pose( context ) ?? context.RestPose();
		}
		finally
		{
			context.Restore( was );
		}
	}

	public override float Duration( GraphContext context ) => input?.Duration( context ) ?? 0.0f;
	public override float Progress( GraphContext context ) => input?.Progress( context ) ?? 0.0f;

	//Lining up reaches through to what is wrapped: a transition matching times means the clip's time, whatever is sitting on top of it.
	public override void Seek( GraphContext context, float time ) => input?.Seek( context, time );

	public override bool CurrentSync( GraphContext context, out SyncPoint point )
	{
		point = default;
		return input?.CurrentSync( context, out point ) ?? false;
	}

	public override bool SeekToSync( GraphContext context, in SyncPoint point,
		AnimGraphBuilder.TransitionSyncMatch match, bool preferClosestId )
		=> input?.SeekToSync( context, point, match, preferClosestId ) ?? false;

	public override int SyncTrack( GraphContext context, ref AnimGraphSyncTrackEvent[] into )
		=> input?.SyncTrack( context, ref into ) ?? 0;

	public override float Speed( GraphContext context ) => input?.Speed( context ) ?? 0.0f;
}

//Play at whatever speed makes the animation's own movement match a velocity - what stops a walk cycle sliding along the floor.
internal sealed class VelocityScaleNode : PoseNode
{
	private readonly int pose, velocity;
	private readonly float authored;

	private GraphNode input, wanted;

	public VelocityScaleNode( JsonObject node )
	{
		pose = GraphFields.Reference( node, "Pose" );
		velocity = GraphFields.Reference( node, "Velocity" );
		authored = GraphFields.Number( node, "Default" );
	}

	public override void Link( GraphNode[] nodes )
	{
		input = ValueNodes.Get( nodes, pose );
		wanted = ValueNodes.Get( nodes, velocity );
	}

	protected override int OnPose( GraphContext context )
	{
		//The speed the animation was made at is what the author wrote down - measuring
		//it from the pose would be measuring something this node is already changing.
		var asked = wanted?.Float( context ) ?? 0.0f;
		var scale = authored > 0.0f ? MathF.Max( asked, 0.0f ) / authored : 1.0f;

		var was = context.Delta;
		context.Scale( scale );

		try
		{
			return input?.Pose( context ) ?? context.RestPose();
		}
		finally
		{
			context.Restore( was );
		}
	}

	public override float Duration( GraphContext context ) => input?.Duration( context ) ?? 0.0f;
	public override float Progress( GraphContext context ) => input?.Progress( context ) ?? 0.0f;

	//Lining up reaches through to what is wrapped: a transition matching times means the clip's time, whatever is sitting on top of it.
	public override void Seek( GraphContext context, float time ) => input?.Seek( context, time );

	public override bool CurrentSync( GraphContext context, out SyncPoint point )
	{
		point = default;
		return input?.CurrentSync( context, out point ) ?? false;
	}

	public override bool SeekToSync( GraphContext context, in SyncPoint point,
		AnimGraphBuilder.TransitionSyncMatch match, bool preferClosestId )
		=> input?.SeekToSync( context, point, match, preferClosestId ) ?? false;

	public override int SyncTrack( GraphContext context, ref AnimGraphSyncTrackEvent[] into )
		=> input?.SyncTrack( context, ref into ) ?? 0;

	public override float Speed( GraphContext context ) => input?.Speed( context ) ?? 0.0f;
}

//Velocity scale measuring the clip's speed off a bone (foot) from the previous frame
internal sealed class VelocityScaleBoneNode : PoseNode
{
	private readonly int pose, velocity;
	private readonly string bone;
	private readonly bool worldSpace;

	private GraphNode input, wanted;

	private Vector3 last;
	private bool measured;
	private float scale = 1.0f;

	public VelocityScaleBoneNode( JsonObject node )
	{
		pose = GraphFields.Reference( node, "Pose" );
		velocity = GraphFields.Reference( node, "Velocity" );

		bone = GraphFields.Text( node, "Bone" );
		worldSpace = GraphFields.Flag( node, "WorldSpace" );
	}

	public override void Link( GraphNode[] nodes )
	{
		input = ValueNodes.Get( nodes, pose );
		wanted = ValueNodes.Get( nodes, velocity );
	}

	public override void Reset( GraphContext context )
	{
		measured = false;
		scale = 1.0f;
	}

	protected override unsafe int OnPose( GraphContext context )
	{
		var was = context.Delta;
		context.Scale( scale );

		int result;

		try
		{
			result = input?.Pose( context ) ?? context.RestPose();
		}
		finally
		{
			context.Restore( was );
		}

		var index = context.FindBone( bone );

		if ( index < 0 )
		{
			Owner.MissingBone( bone );
			return result;
		}

		if ( !context.Instance.BoneModel( result, index, out var trs ) ) return result;

		var at = trs.Position;
		if ( worldSpace ) at = context.World.PointToWorld( at );

		Measure( context, at, was );

		last = at;
		measured = true;

		return result;
	}

	//What to play the next frame at, from what this one moved by.
	private void Measure( GraphContext context, Vector3 at, float delta )
	{
		if ( !measured || delta <= 0.0f ) return;

		//The distance covered is at the speed it was already being played, so the animation's own speed is that
		//divided by the scale that produced it - measuring without dividing would settle on the wrong number.
		var moved = at.Distance( last ) / delta;
		var own = scale > 0.0001f ? moved / scale : 0.0f;

		var asked = MathF.Max( wanted?.Float( context ) ?? 0.0f, 0.0f );

		//An animation that does not move cannot be made to move by playing it faster,
		//and dividing by how much it does not move would send the scale to infinity.
		if ( own <= 0.0001f ) return;

		scale = Math.Clamp( asked / own, 0.0f, 10.0f );
	}

	public override float Duration( GraphContext context ) => input?.Duration( context ) ?? 0.0f;
	public override float Progress( GraphContext context ) => input?.Progress( context ) ?? 0.0f;

	//Lining up reaches through to what is wrapped: a transition matching times means the clip's time, whatever is sitting on top of it.
	public override void Seek( GraphContext context, float time ) => input?.Seek( context, time );

	public override bool CurrentSync( GraphContext context, out SyncPoint point )
	{
		point = default;
		return input?.CurrentSync( context, out point ) ?? false;
	}

	public override bool SeekToSync( GraphContext context, in SyncPoint point,
		AnimGraphBuilder.TransitionSyncMatch match, bool preferClosestId )
		=> input?.SeekToSync( context, point, match, preferClosestId ) ?? false;

	public override int SyncTrack( GraphContext context, ref AnimGraphSyncTrackEvent[] into )
		=> input?.SyncTrack( context, ref into ) ?? 0;

	public override float Speed( GraphContext context ) => input?.Speed( context ) ?? 0.0f;
}

//One of several poses, picked at random and weighted - the same idle played a different way each time.
internal sealed class VariationNode : PoseNode
{
	private readonly int reset;
	private readonly int[] poses;
	private readonly int[] weights;

	private GraphNode resetNode;
	private GraphNode[] options = Array.Empty<GraphNode>();

	private int chosen = -1;
	private bool wasReset;

	public VariationNode( JsonObject node )
	{
		reset = GraphFields.Reference( node, "Reset" );

		var list = GraphFields.Array( node, "Options" );
		var count = list?.Count ?? 0;

		poses = new int[count];
		weights = new int[count];

		for ( var i = 0; i < count; i++ )
		{
			if ( list[i] is not JsonObject entry ) continue;

			poses[i] = GraphFields.Reference( entry, "Pose" );
			weights[i] = Math.Max( GraphFields.Integer( entry, "Weight", 1 ), 0 );
		}
	}

	public override void Link( GraphNode[] nodes )
	{
		resetNode = ValueNodes.Get( nodes, reset );

		options = new GraphNode[poses.Length];
		for ( var i = 0; i < poses.Length; i++ ) options[i] = ValueNodes.Get( nodes, poses[i] );
	}

	public override void Reset( GraphContext context )
	{
		chosen = -1;
		wasReset = false;
	}

	protected override int OnPose( GraphContext context )
	{
		if ( options.Length == 0 ) return context.RestPose();

		if ( resetNode?.Bool( context ) ?? false )
		{
			//Held rather than edge-triggered, like a clip's reset: a signal that stays true should not roll a new variation every frame.
			if ( !wasReset ) chosen = -1;

			wasReset = true;
		}
		else
		{
			wasReset = false;
		}

		if ( chosen < 0 )
		{
			chosen = Pick();

			//Reset only the newly picked option; resetting every pose would pin it to its first frame
			options[chosen]?.Reset( context );
		}

		var picked = options[chosen];

		return picked?.Pose( context ) ?? context.RestPose();
	}

	private int Pick()
	{
		var total = 0;
		foreach ( var weight in weights ) total += weight;

		if ( total <= 0 ) return 0;

		var at = System.Random.Shared.Next( total );

		for ( var i = 0; i < weights.Length; i++ )
		{
			at -= weights[i];
			if ( at < 0 ) return i;
		}

		return weights.Length - 1;
	}

	public override float Duration( GraphContext context )
		=> chosen >= 0 && chosen < options.Length ? options[chosen]?.Duration( context ) ?? 0.0f : 0.0f;

	public override float Progress( GraphContext context )
		=> chosen >= 0 && chosen < options.Length ? options[chosen]?.Progress( context ) ?? 0.0f : 0.0f;
}

//Procedural hit reaction: impulse at a bone, spread up the chain, springing back to the animation
internal sealed class HitReactNode : PoseNode
{
	private readonly int pose, trigger, hitBone, hitOffset, hitDirection, hitStrength;
	private readonly int reach;
	private readonly float least, most, spring, counter, spread, wait;

	private GraphNode input, triggerNode, boneNode, offsetNode, directionNode, strengthNode;

	//Where each shoved bone is now, and how fast it is moving back.
	private readonly Dictionary<int, Shove> shoved = new();

	private bool wasTriggered;
	private float since;
	private int frame = -1;

	private struct Shove
	{
		public Vector3 Turn;
		public Vector3 Speed;
	}

	public HitReactNode( JsonObject node )
	{
		pose = GraphFields.Reference( node, "Pose" );
		trigger = GraphFields.Reference( node, "Trigger" );
		hitBone = GraphFields.Reference( node, "HitBone" );
		hitOffset = GraphFields.Reference( node, "HitOffset" );
		hitDirection = GraphFields.Reference( node, "HitDirection" );
		hitStrength = GraphFields.Reference( node, "HitStrength" );

		reach = Math.Max( GraphFields.Integer( node, "BoneCount", 3 ), 1 );
		least = GraphFields.Number( node, "MinImpactForce", 50.0f );
		most = GraphFields.Number( node, "MaxImpactForce", 250.0f );
		spring = Math.Max( GraphFields.Number( node, "SpringStrength", 10.0f ), 0.01f );
		counter = GraphFields.Number( node, "CounterRotationScale", 0.6f );
		spread = GraphFields.Number( node, "PropagationScale", 1.0f );
		wait = GraphFields.Number( node, "MinDelayBetweenHits", 0.0f );
	}

	public override void Link( GraphNode[] nodes )
	{
		input = ValueNodes.Get( nodes, pose );
		triggerNode = ValueNodes.Get( nodes, trigger );
		boneNode = ValueNodes.Get( nodes, hitBone );
		offsetNode = ValueNodes.Get( nodes, hitOffset );
		directionNode = ValueNodes.Get( nodes, hitDirection );
		strengthNode = ValueNodes.Get( nodes, hitStrength );
	}

	public override void Reset( GraphContext context )
	{
		shoved.Clear();
		wasTriggered = false;
		since = 0.0f;
	}

	protected override unsafe int OnPose( GraphContext context )
	{
		var result = input?.Pose( context ) ?? context.RestPose();

		//Once a frame however many times the pose is asked for: a spring stepped per ask
		//moves at a speed that depends on how many things happen to be looking at it.
		if ( frame != context.Frame )
		{
			frame = context.Frame;
			since += context.Delta;

			Struck( context );
			Settle( context.Delta );
		}

		if ( shoved.Count == 0 ) return result;

		foreach ( var (bone, shove) in shoved )
		{
			if ( shove.Turn.Length < 0.0001f ) continue;
			if ( !context.Instance.ReadBone( result, bone, out var trs ) ) continue;

			var local = trs.ToTransform();

			//The shove is kept as a turn about an axis - its direction is which way, its length is how far - which is what an impulse spends itself as.
			var turn = Rotation.FromAxis( shove.Turn.Normal, shove.Turn.Length.RadianToDegree() );

			context.Instance.WriteBone( result, bone,
				AnimTrs.FromTransform( local.WithRotation( turn * local.Rotation ) ) );
		}

		return result;
	}

	//Take a hit, where the game has just said there was one.
	private void Struck( GraphContext context )
	{
		var hit = triggerNode?.Bool( context ) ?? false;

		//On the edge, not while held: a flag the game leaves up would other- wise be a hit every frame for as long as it stayed up.
		if ( !hit )
		{
			wasTriggered = false;
			return;
		}

		if ( wasTriggered ) return;
		wasTriggered = true;

		if ( since < wait ) return;
		since = 0.0f;

		var bone = (int)( boneNode?.Float( context ) ?? 0.0f );
		if ( bone < 0 ) return;

		var direction = directionNode?.Vector( context ) ?? Vector3.Zero;
		if ( direction.Length < 0.0001f ) return;

		//Their force is a range, and what arrives is somewhere in it. Below the least
		//it is not worth moving for; above the most it would throw the character about.
		var strength = Math.Clamp( ( strengthNode?.Float( context ) ?? 0.0f ) * most, least, most );

		var push = direction.Normal * ( strength / most );

		//Up the chain, weaker each step, and the bone above the one hit turns back the other way - a body absorbing a blow rather than folding.
		var at = bone;

		for ( var step = 0; step < reach && at >= 0; step++ )
		{
			var scale = MathF.Pow( spread, step ) * ( step == 1 ? -counter : 1.0f );

			var shove = shoved.TryGetValue( at, out var found ) ? found : default;
			shove.Speed += push * scale;
			shoved[at] = shove;

			at = CoreSkeletons.BoneParent( context.Model, at );
		}
	}

	//Let the springs carry every shoved bone back to rest.
	private void Settle( float delta )
	{
		if ( shoved.Count == 0 || delta <= 0.0f ) return;

		//Critically damped, so a bone comes back once rather than wobbling - a hit is a blow absorbed, not a struck bell.
		var damping = 2.0f * MathF.Sqrt( spring );

		var done = new List<int>();

		foreach ( var (bone, was) in shoved )
		{
			var shove = was;

			shove.Speed += ( ( shove.Turn * -spring ) - ( shove.Speed * damping ) ) * delta;
			shove.Turn += shove.Speed * delta;

			if ( shove.Turn.Length < 0.0005f && shove.Speed.Length < 0.0005f )
			{
				done.Add( bone );
				continue;
			}

			shoved[bone] = shove;
		}

		foreach ( var bone in done ) shoved.Remove( bone );
	}

	public override float Duration( GraphContext context ) => input?.Duration( context ) ?? 0.0f;
	public override float Progress( GraphContext context ) => input?.Progress( context ) ?? 0.0f;

	public override void Seek( GraphContext context, float time ) => input?.Seek( context, time );

	public override bool CurrentSync( GraphContext context, out SyncPoint point )
	{
		point = default;
		return input?.CurrentSync( context, out point ) ?? false;
	}

	public override bool SeekToSync( GraphContext context, in SyncPoint point,
		AnimGraphBuilder.TransitionSyncMatch match, bool preferClosestId )
		=> input?.SeekToSync( context, point, match, preferClosestId ) ?? false;
}

//A pose the game puts in from outside, by slot name - a ragdoll, a recorded pose, anything the graph does not produce itself.
internal sealed class ExternalPoseNode : PoseNode
{
	private readonly string slot;

	public ExternalPoseNode( JsonObject node ) => slot = GraphFields.Text( node, "Slot" );

	public string Slot => slot;

	protected override int OnPose( GraphContext context )
	{
		var given = Owner.ExternalPose( slot );

		if ( given < 0 ) return context.RestPose();

		//Copied rather than handed over: the slot holds it for as long as the game wants, and whoever asked for it here will free what they are given.
		var pose = context.Alloc();
		if ( pose < 0 ) return pose;

		context.Copy( pose, given );

		return pose;
	}

	protected override bool OnBool( GraphContext context ) => Owner.ExternalPose( slot ) >= 0;
}

//Whether the game has put anything in that slot.
internal sealed class ExternalPoseSetNode : GraphNode
{
	private readonly int slot;
	private GraphNode input;

	public ExternalPoseSetNode( JsonObject node ) => slot = GraphFields.Reference( node, "Slot" );

	public override void Link( GraphNode[] nodes ) => input = ValueNodes.Get( nodes, slot );

	protected override bool OnBool( GraphContext context ) => input?.Bool( context ) ?? false;
}

//A whole graph the game plugs in by slot name, with something to play while nothing is plugged in.
internal sealed class ExternalGraphNode : PoseNode
{
	private readonly string slot;
	private readonly int fallback;

	private GraphNode spare;

	public ExternalGraphNode( JsonObject node )
	{
		slot = GraphFields.Text( node, "Slot" );
		fallback = GraphFields.Reference( node, "Fallback" );
	}

	public string Slot => slot;

	public override void Link( GraphNode[] nodes ) => spare = ValueNodes.Get( nodes, fallback );

	protected override int OnPose( GraphContext context )
	{
		var filled = Owner.ExternalGraph( slot );

		if ( filled is null ) return spare?.Pose( context ) ?? context.RestPose();

		return filled.PoseInto( context );
	}

	protected override bool OnBool( GraphContext context ) => Owner.ExternalGraph( slot ) is not null;

	public override float Duration( GraphContext context ) => spare?.Duration( context ) ?? 0.0f;
}

internal sealed class ExternalGraphFilledNode : GraphNode
{
	private readonly int slot;
	private GraphNode input;

	public ExternalGraphFilledNode( JsonObject node ) => slot = GraphFields.Reference( node, "Slot" );

	public override void Link( GraphNode[] nodes ) => input = ValueNodes.Get( nodes, slot );

	protected override bool OnBool( GraphContext context ) => input?.Bool( context ) ?? false;
}

//Another graph, named and played inside this one.
internal sealed class ReferencedGraphNode : PoseNode
{
	private readonly string path;
	private readonly int fallback;

	private GraphNode spare;

	private AnimGraphInstance running;
	private bool looked;

	public ReferencedGraphNode( JsonObject node )
	{
		path = GraphFields.Text( node, "Graph" );
		fallback = GraphFields.Reference( node, "Fallback" );
	}

	public override void Link( GraphNode[] nodes ) => spare = ValueNodes.Get( nodes, fallback );

	protected override int OnPose( GraphContext context )
	{
		if ( !looked )
		{
			looked = true;

			var graph = AnimGraph.Load( path );

			//Its own instance, because a referenced graph has its own states and its own
			//place in them - two graphs playing the same one are not in the same part of it.
			if ( graph is not null ) running = Owner.Referenced( graph );
			else Owner.MissingGraph( path );
		}

		if ( running is null ) return spare?.Pose( context ) ?? context.RestPose();

		return running.PoseInto( context );
	}

	public override float Duration( GraphContext context ) => spare?.Duration( context ) ?? 0.0f;
}
