using System.Text.Json.Nodes;

using Rinten.CoreAnimation;

namespace Rinten.Animation;

//Posing nodes; each returns a pose the caller owns. Time advances only when a pose is asked for
internal abstract class PoseNode : GraphNode
{
	//Frames to keep retrying a missing clip, since clips may mount after the graph starts
	protected const int Attempts = 120;

	private int ticked = -1;

	//Move this node's own time along, once a frame, the first time anything asks it for anything.
	protected void Tick( GraphContext context )
	{
		if ( ticked == context.Frame ) return;

		ticked = context.Frame;
		OnTick( context );
	}

	protected virtual void OnTick( GraphContext context ) { }

	protected void Untick() => ticked = -1;
}

internal sealed class ReferencePoseNode : PoseNode
{
	protected override int OnPose( GraphContext context ) => context.RestPose();
}

internal sealed class ZeroPoseNode : PoseNode
{
	protected override int OnPose( GraphContext context ) => context.ZeroPose();
}

//A clip, playing at its own speed.
internal sealed class ClipNode : PoseNode
{
	private readonly string named;
	private readonly bool looping;
	private readonly float speed;
	private readonly int reverse, reset;

	private GraphNode reverseInput, resetInput;

	private AnimClipData clip;
	private int tries;

	private float time;
	private bool wasReset;

	public ClipNode( JsonObject node )
	{
		named = GraphFields.Text( node, "Clip" );
		looping = GraphFields.Flag( node, "Looping" );
		speed = GraphFields.Number( node, "Speed", 1.0f );

		reverse = GraphFields.Reference( node, "PlayInReverse" );
		reset = GraphFields.Reference( node, "ResetTime" );
	}

	public override void Link( GraphNode[] nodes )
	{
		reverseInput = ValueNodes.Get( nodes, reverse );
		resetInput = ValueNodes.Get( nodes, reset );
	}

	public override void Reset( GraphContext context )
	{
		time = 0.0f;
		Untick();
	}

	//The clip this plays, found once - by the name the bound model calls it, because that is what
	//a graph authored against a model says. Nothing the model does not list is a clip this plays.
	private AnimClipData Clip( GraphContext context )
	{
		//Retry a missing clip until found (it may not be mounted yet), up to Attempts tries
		if ( clip is not null || tries >= Attempts ) return clip;

		++tries;

		clip = CoreSkeletons.Clip( context.Model, named );

		//By the name the bound model calls it, or - a graph built in code against no model at all, which every test that
		//exercises a graph's own mechanics rather than a character's is - by the literal path of an already-compiled clip.
		clip ??= CoreClips.Clip( named );

		return clip;
	}

	protected override void OnTick( GraphContext context )
	{
		if ( resetInput?.Bool( context ) ?? false )
		{
			//Held rather than edge-triggered: a reset that is true for a frame and a reset that
			//stays true both mean "start again", and the second should not stop it playing.
			if ( !wasReset ) time = 0.0f;

			wasReset = true;
		}
		else
		{
			wasReset = false;
		}

		var direction = ( reverseInput?.Bool( context ) ?? false ) ? -1.0f : 1.0f;

		time += context.Delta * speed * direction;

		var duration = Duration( context );
		if ( duration <= 0.0f ) return;

		if ( looping ) time = Wrapped( time, duration );
		else time = Math.Clamp( time, 0.0f, duration );
	}

	//Wraps time into the clip from frame zero, not LoopStart (their clips close at zero, not there)
	private static float Wrapped( float at, float duration )
	{
		if ( duration <= 0.0f ) return 0.0f;

		at %= duration;

		return at < 0.0f ? at + duration : at;
	}

	protected override int OnPose( GraphContext context )
	{
		Tick( context );

		var clip = Clip( context );

		if ( clip is null )
		{
			//Missing clip yields no pose (warned once), not the rest pose, which breaks additive layers
			Owner.MissingClip( named );
			context.Debug?.Invalid( this );

			return -1;
		}

		var pose = context.Alloc();
		if ( pose < 0 ) return pose;

		context.Sample( pose, clip, time, looping );

		RaiseEvents( context, clip );

		return pose;
	}

	//Raises the clip's events at the playhead, with progress and this clip's blend weight
	private void RaiseEvents( GraphContext context, AnimClipData clip )
	{
		var events = clip.Events;

		for ( var i = 0; i < events.Length; i++ )
		{
			var marked = events[i];

			if ( time < marked.Start || time > marked.End ) continue;

			var length = marked.End - marked.Start;
			var fraction = length <= 0.0f ? 0.0f : ( time - marked.Start ) / length;

			context.Raise( new GraphContext.Sampled(
				marked.Id,
				(GraphContext.SampledKind)marked.Kind,
				marked.Data,
				fraction,
				context.Weight,
				clip,
				i,
				context.Machine,
				context.State ) );
		}
	}

	public override float Duration( GraphContext context )
	{
		var clip = Clip( context );
		return clip?.Duration ?? 0.0f;
	}

	public override float Progress( GraphContext context )
	{
		var duration = Duration( context );
		return duration <= 0.0f ? 0.0f : Math.Clamp( time / duration, 0.0f, 1.0f );
	}

	/// <summary>Where this clip is, for a transition that matches times.</summary>
	public float Time
	{
		get => time;
		set => time = value;
	}

	public override void Seek( GraphContext context, float at )
	{
		var duration = Duration( context );

		if ( duration <= 0.0f )
		{
			time = 0.0f;
			return;
		}

		if ( looping ) at = Wrapped( at, duration );

		time = Math.Clamp( at, 0.0f, duration );
	}

	public override int SyncTrack( GraphContext context, ref AnimGraphSyncTrackEvent[] into )
	{
		var clip = Clip( context );
		if ( clip is null ) return 0;

		var duration = Duration( context );
		if ( duration <= 0.0f ) return 0;

		var events = clip.Events;
		var written = 0;

		for ( var i = 0; i < events.Length; i++ )
		{
			var marked = events[i];
			if ( (GraphContext.SampledKind)marked.Kind != GraphContext.SampledKind.Sync ) continue;

			if ( into is null || into.Length <= written ) Array.Resize( ref into, Math.Max( written + 4, 8 ) );

			context.Debug?.Remember( marked.Id );

			//As fractions of the clip, because that is what a bar drawn across its whole length is measured in.
			into[written] = new AnimGraphSyncTrackEvent
			{
				ID = marked.Id,
				StartTime = marked.Start / duration,
				Duration = ( marked.End - marked.Start ) / duration,
			};

			written++;
		}

		return written;
	}

	//Measured off the root once and kept: it is a property of the clip, not of the frame, and a blend spread along it asks every frame.
	/// <summary>How far this clip carries the character a second, on average.</summary>
	public override float Speed( GraphContext context )
	{
		if ( measured ) return travel;

		measured = true;

		var clip = Clip( context );
		if ( clip is null ) return travel;

		var duration = clip.Duration;
		var bones = clip.Bones.Length;

		if ( duration <= 0.0f || bones <= 0 ) return travel;

		var root = -1;

		for ( var bone = 0; bone < bones; bone++ )
		{
			if ( clip.Bones[bone].Parent >= 0 ) continue;

			root = bone;
			break;
		}

		if ( root < 0 ) return travel;

		Span<AnimTrs> pose = bones <= 128 ? stackalloc AnimTrs[bones] : new AnimTrs[bones];

		//Where the root starts and where it ends up: the straight-line distance between them over the clip's
		//length. Not the path it took - a walk that wanders is still a walk at the speed it arrives at.
		if ( clip.Sample( 0.0f, false, pose ) <= root ) return travel;

		var from = pose[root].Position;

		if ( clip.Sample( duration, false, pose ) <= root ) return travel;

		travel = pose[root].Position.Distance( from ) / duration;

		return travel;
	}

	private float travel;
	private bool measured;

	public override bool CurrentSync( GraphContext context, out SyncPoint point )
	{
		point = default;

		var clip = Clip( context );
		if ( clip is null ) return false;

		var events = clip.Events;
		var ordinal = 0;

		for ( var i = 0; i < events.Length; i++ )
		{
			var marked = events[i];
			if ( (GraphContext.SampledKind)marked.Kind != GraphContext.SampledKind.Sync ) continue;

			if ( time >= marked.Start && time <= marked.End )
			{
				var length = marked.End - marked.Start;

				point = new SyncPoint(
					marked.Id,
					ordinal,
					length <= 0.0f ? 0.0f : ( time - marked.Start ) / length );

				return true;
			}

			ordinal++;
		}

		return false;
	}

	public override bool SeekToSync( GraphContext context, in SyncPoint point,
		AnimGraphBuilder.TransitionSyncMatch match, bool preferClosestId )
	{
		var clip = Clip( context );
		if ( clip is null || match == AnimGraphBuilder.TransitionSyncMatch.None ) return false;

		var events = clip.Events;

		var chosen = -1;
		var start = 0.0f;
		var length = 0.0f;

		var ordinal = 0;

		for ( var i = 0; i < events.Length; i++ )
		{
			var marked = events[i];
			if ( (GraphContext.SampledKind)marked.Kind != GraphContext.SampledKind.Sync ) continue;

			var id = marked.Id;

			var byId = ( ( match & AnimGraphBuilder.TransitionSyncMatch.EventId ) == AnimGraphBuilder.TransitionSyncMatch.EventId )
				&& string.Equals( id, point.Id, StringComparison.OrdinalIgnoreCase );

			var byIndex = ( ( match & AnimGraphBuilder.TransitionSyncMatch.EventIndex ) == AnimGraphBuilder.TransitionSyncMatch.EventIndex )
				&& ordinal == point.Index;

			//Which mark to land on: the one called the same, or the one in the same place in the list. Matching by name first when both are
			//asked for is what "prefer the closest by id" means - a clip whose marks are in a different order is still stepping on the same foot.
			if ( byId || ( byIndex && ( chosen < 0 || !preferClosestId ) ) )
			{
				chosen = ordinal;
				start = marked.Start;
				length = marked.End - marked.Start;

				if ( byId ) break;
			}

			ordinal++;
		}

		if ( chosen < 0 ) return false;

		//How far into the mark, if that was asked for: a foot halfway through coming down goes to halfway through coming down, not to the start of it.
		var fraction = ( ( match & AnimGraphBuilder.TransitionSyncMatch.EventPercentage ) == AnimGraphBuilder.TransitionSyncMatch.EventPercentage ) ? point.Fraction : 0.0f;

		Seek( context, start + ( length * fraction ) );

		return true;
	}
}

//A clip whose time comes from the graph rather than from the clock: a pose to be scrubbed rather than an animation to be played.
internal sealed class TimedClipNode : PoseNode
{
	private readonly string named;
	private readonly int time;

	private GraphNode input;

	private AnimClipData clip;
	private int tries;

	public TimedClipNode( JsonObject node )
	{
		named = GraphFields.Text( node, "Clip" );
		time = GraphFields.Reference( node, "Time" );
	}

	public override void Link( GraphNode[] nodes ) => input = ValueNodes.Get( nodes, time );

	private AnimClipData Clip( GraphContext context )
	{
		//Retry a missing clip until found (it may not be mounted yet), up to Attempts tries
		if ( clip is not null || tries >= Attempts ) return clip;

		++tries;

		clip = CoreSkeletons.Clip( context.Model, named );

		//By the name the bound model calls it, or - a graph built in code against no model at all, which every test that
		//exercises a graph's own mechanics rather than a character's is - by the literal path of an already-compiled clip.
		clip ??= CoreClips.Clip( named );

		return clip;
	}

	protected override int OnPose( GraphContext context )
	{
		var clip = Clip( context );

		if ( clip is null )
		{
			//Nothing rather than the rest - see AnimationClipNode.
			Owner.MissingClip( named );
			return -1;
		}

		var pose = context.Alloc();
		if ( pose < 0 ) return pose;

		var duration = clip.Duration;
		var at = Math.Clamp( input?.Float( context ) ?? 0.0f, 0.0f, 1.0f ) * duration;

		context.Sample( pose, clip, at, false );

		return pose;
	}

	public override float Duration( GraphContext context )
	{
		var clip = Clip( context );
		return clip?.Duration ?? 0.0f;
	}
}

//One frame of a clip, chosen by a value.
internal sealed class PoseFromClipNode : PoseNode
{
	private readonly string named;
	private readonly int time;
	private readonly float remapStart, remapEnd, fixedTime;
	private readonly bool frames;

	private GraphNode input;

	private AnimClipData clip;
	private int tries;

	public PoseFromClipNode( JsonObject node )
	{
		named = GraphFields.Text( node, "Clip" );
		time = GraphFields.Reference( node, "Time" );

		remapStart = GraphFields.Number( node, "RemapStart" );
		remapEnd = GraphFields.Number( node, "RemapEnd", 1.0f );
		fixedTime = GraphFields.Number( node, "FixedTime" );
		frames = GraphFields.Flag( node, "Frames" );
	}

	public override void Link( GraphNode[] nodes ) => input = ValueNodes.Get( nodes, time );

	private AnimClipData Clip( GraphContext context )
	{
		//Retry a missing clip until found (it may not be mounted yet), up to Attempts tries
		if ( clip is not null || tries >= Attempts ) return clip;

		++tries;

		clip = CoreSkeletons.Clip( context.Model, named );

		//By the name the bound model calls it, or - a graph built in code against no model at all, which every test that
		//exercises a graph's own mechanics rather than a character's is - by the literal path of an already-compiled clip.
		clip ??= CoreClips.Clip( named );

		return clip;
	}

	protected override int OnPose( GraphContext context )
	{
		var clip = Clip( context );

		if ( clip is null )
		{
			//Nothing rather than the rest - see AnimationClipNode.
			Owner.MissingClip( named );
			return -1;
		}

		var pose = context.Alloc();
		if ( pose < 0 ) return pose;

		var duration = clip.Duration;
		var rate = clip.FrameRate;

		//Fixed time uses the input's units: frames if the node takes frames, else seconds
		var at = frames && rate > 0.0f ? fixedTime / rate : fixedTime;

		if ( input is not null )
		{
			var value = input.Float( context );

			if ( frames )
			{
				//The value is a frame number, which is a place in the clip rather than a fraction of it.
				at = rate > 0.0f ? value / rate : 0.0f;
			}
			else
			{
				var span = remapEnd - remapStart;
				var alpha = MathF.Abs( span ) < 1e-8f ? 0.0f : ( value - remapStart ) / span;

				at = Math.Clamp( alpha, 0.0f, 1.0f ) * duration;
			}
		}

		context.Sample( pose, clip, Math.Clamp( at, 0.0f, duration ), false );

		return pose;
	}
}

internal sealed class BlendPoseNode : PoseNode
{
	private readonly int a, b, weight;

	private GraphNode first, second, amount;

	public BlendPoseNode( JsonObject node )
	{
		a = GraphFields.Reference( node, "A" );
		b = GraphFields.Reference( node, "B" );
		weight = GraphFields.Reference( node, "Weight" );
	}

	public override void Link( GraphNode[] nodes )
	{
		first = ValueNodes.Get( nodes, a );
		second = ValueNodes.Get( nodes, b );
		amount = ValueNodes.Get( nodes, weight );
	}

	protected override int OnPose( GraphContext context )
	{
		var at = Math.Clamp( amount?.Float( context ) ?? 0.0f, 0.0f, 1.0f );

		//One side entirely: the other is not asked at all, so a blend that has settled costs what one pose costs.
		if ( at <= 0.0f ) return first?.Pose( context ) ?? context.RestPose();
		if ( at >= 1.0f ) return second?.Pose( context ) ?? context.RestPose();

		var was = context.PushWeight( 1.0f - at );
		var left = first?.Pose( context ) ?? context.RestPose();
		context.PopWeight( was );

		was = context.PushWeight( at );
		var right = second?.Pose( context ) ?? context.RestPose();
		context.PopWeight( was );

		return context.Blend( left, right, at );
	}

	public override float Duration( GraphContext context )
		=> MathF.Max( first?.Duration( context ) ?? 0.0f, second?.Duration( context ) ?? 0.0f );

	//Both sides, because both are playing: lining a blend up with something else and leaving half of it where it was is what a blend is for undoing.
	public override void Seek( GraphContext context, float time )
	{
		first?.Seek( context, time );
		second?.Seek( context, time );
	}

	public override bool CurrentSync( GraphContext context, out SyncPoint point )
	{
		point = default;

		return ( first?.CurrentSync( context, out point ) ?? false )
			|| ( second?.CurrentSync( context, out point ) ?? false );
	}

	public override bool SeekToSync( GraphContext context, in SyncPoint point,
		AnimGraphBuilder.TransitionSyncMatch match, bool preferClosestId )
	{
		var left = first?.SeekToSync( context, point, match, preferClosestId ) ?? false;
		var right = second?.SeekToSync( context, point, match, preferClosestId ) ?? false;

		return left || right;
	}
}

internal sealed class PassthroughNode : PoseNode
{
	private readonly int pose;
	private GraphNode input;

	public PassthroughNode( JsonObject node ) => pose = GraphFields.Reference( node, "Pose" );

	public override void Link( GraphNode[] nodes ) => input = ValueNodes.Get( nodes, pose );

	protected override int OnPose( GraphContext context ) => input?.Pose( context ) ?? context.RestPose();
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

//The first option whose condition is true.
internal sealed class SelectPoseNode : PoseNode
{
	private readonly int[] poses;
	private readonly int[] conditions;

	private GraphNode[] options = Array.Empty<GraphNode>();
	private GraphNode[] tests = Array.Empty<GraphNode>();

	public SelectPoseNode( JsonObject node )
	{
		var list = GraphFields.Array( node, "Options" );
		var count = list?.Count ?? 0;

		poses = new int[count];
		conditions = new int[count];

		for ( var i = 0; i < count; i++ )
		{
			if ( list[i] is not JsonObject entry ) continue;

			poses[i] = GraphFields.Reference( entry, "Pose" );
			conditions[i] = GraphFields.Reference( entry, "Condition" );
		}
	}

	public override void Link( GraphNode[] nodes )
	{
		options = new GraphNode[poses.Length];
		tests = new GraphNode[conditions.Length];

		for ( var i = 0; i < poses.Length; i++ )
		{
			options[i] = ValueNodes.Get( nodes, poses[i] );
			tests[i] = ValueNodes.Get( nodes, conditions[i] );
		}
	}

	private GraphNode Chosen( GraphContext context )
	{
		for ( var i = 0; i < options.Length; i++ )
		{
			//A case with nothing connected to its condition is the last resort, which is how the editor writes a default option.
			if ( tests[i] is null || tests[i].Bool( context ) ) return options[i];
		}

		return null;
	}

	protected override int OnPose( GraphContext context ) => Chosen( context )?.Pose( context ) ?? context.RestPose();
	public override float Duration( GraphContext context ) => Chosen( context )?.Duration( context ) ?? 0.0f;
	public override float Progress( GraphContext context ) => Chosen( context )?.Progress( context ) ?? 0.0f;
}

//The option whose name matches.
internal sealed class IdSelectPoseNode : PoseNode
{
	private readonly int id, fallback;
	private readonly string[] ids;
	private readonly int[] poses;

	private GraphNode input, spare;
	private GraphNode[] options = Array.Empty<GraphNode>();

	public IdSelectPoseNode( JsonObject node )
	{
		id = GraphFields.Reference( node, "Id" );
		fallback = GraphFields.Reference( node, "Fallback" );

		var list = GraphFields.Array( node, "Options" );
		var count = list?.Count ?? 0;

		ids = new string[count];
		poses = new int[count];

		for ( var i = 0; i < count; i++ )
		{
			if ( list[i] is not JsonObject entry ) continue;

			ids[i] = GraphFields.Text( entry, "Id" );
			poses[i] = GraphFields.Reference( entry, "Pose" );
		}
	}

	public override void Link( GraphNode[] nodes )
	{
		input = ValueNodes.Get( nodes, id );
		spare = ValueNodes.Get( nodes, fallback );

		options = new GraphNode[poses.Length];
		for ( var i = 0; i < poses.Length; i++ ) options[i] = ValueNodes.Get( nodes, poses[i] );
	}

	//Which option was picked last, so a change can be noticed.
	private int picked = -2;

	private GraphNode Chosen( GraphContext context )
	{
		var wanted = input?.Id( context ) ?? "";

		var found = -1;

		for ( var i = 0; i < ids.Length; i++ )
		{
			if ( !string.Equals( ids[i], wanted, StringComparison.OrdinalIgnoreCase ) ) continue;

			found = i;
			break;
		}

		//Changing its mind is a reset, and the reset goes up the graph - see AnimGraphInstance.ResetSignals. A state machine above this is
		//how their graphs notice that a choice was made at all, and the first pick is not a change: the graph is being started, not switched.
		if ( picked != found )
		{
			var was = picked;
			picked = found;

			if ( was != -2 )
			{
				context.Graph?.SignalReset();
				( found < 0 ? spare : options[found] )?.Reset( context );
			}
		}

		return found < 0 ? spare : options[found];
	}

	protected override int OnPose( GraphContext context ) => Chosen( context )?.Pose( context ) ?? context.RestPose();
	public override float Duration( GraphContext context ) => Chosen( context )?.Duration( context ) ?? 0.0f;
	public override float Progress( GraphContext context ) => Chosen( context )?.Progress( context ) ?? 0.0f;
}

//Poses spread along a line, blended by where a value falls between them.
internal sealed class BlendSpaceNode : PoseNode
{
	private readonly int parameter;
	private readonly int[] sources;
	private readonly float[] positions;
	private readonly bool looping;
	private readonly bool synchronized;

	private GraphNode input;
	private GraphNode[] poses = Array.Empty<GraphNode>();

	public BlendSpaceNode( JsonObject node )
	{
		parameter = GraphFields.Reference( node, "Parameter" );
		looping = GraphFields.Flag( node, "Looping" );
		synchronized = GraphFields.Flag( node, "Synchronized" );

		var list = GraphFields.Array( node, "Sources" );
		var places = GraphFields.Array( node, "Positions" );

		var count = list?.Count ?? 0;

		sources = new int[count];
		positions = new float[count];

		for ( var i = 0; i < count; i++ )
		{
			sources[i] = list[i]?.GetValue<int>() ?? -1;
			positions[i] = places is not null && i < places.Count ? places[i]?.GetValue<float>() ?? 0.0f : i;
		}
	}

	public override void Link( GraphNode[] nodes )
	{
		input = ValueNodes.Get( nodes, parameter );

		poses = new GraphNode[sources.Length];
		for ( var i = 0; i < sources.Length; i++ ) poses[i] = ValueNodes.Get( nodes, sources[i] );
	}

	protected override int OnPose( GraphContext context )
	{
		if ( poses.Length == 0 ) return context.RestPose();
		if ( poses.Length == 1 ) return poses[0]?.Pose( context ) ?? context.RestPose();

		var at = input?.Float( context ) ?? 0.0f;

		//Outside the ends is the end, not a blend with nothing - and on a looping space the two ends are neighbours.
		if ( !looping )
		{
			if ( at <= positions[0] ) return poses[0]?.Pose( context ) ?? context.RestPose();
			if ( at >= positions[^1] ) return poses[^1]?.Pose( context ) ?? context.RestPose();
		}

		for ( var i = 0; i + 1 < positions.Length; i++ )
		{
			if ( at < positions[i] || at > positions[i + 1] ) continue;

			var span = positions[i + 1] - positions[i];
			var alpha = MathF.Abs( span ) < 1e-8f ? 0.0f : ( at - positions[i] ) / span;

			return Between( context, i, i + 1, alpha );
		}

		if ( looping )
		{
			//Past the last, back towards the first.
			var span = ( positions[^1] - positions[0] );
			if ( MathF.Abs( span ) < 1e-8f ) return poses[0]?.Pose( context ) ?? context.RestPose();

			var wrapped = ( at - positions[0] ) % span;
			if ( wrapped < 0.0f ) wrapped += span;

			return Between( context, poses.Length - 1, 0, wrapped / span );
		}

		return poses[0]?.Pose( context ) ?? context.RestPose();
	}

	private int Between( GraphContext context, int a, int b, float alpha )
	{
		//Which two of the poses are being blended, for anything drawing the space as it plays
		//- it is the one thing about a blend space that cannot be worked out from the outside.
		context.Debug?.Blend( this, poses[a]?.Index ?? -1, poses[b]?.Index ?? -1, alpha );

		if ( alpha <= 0.0f ) return poses[a]?.Pose( context ) ?? context.RestPose();
		if ( alpha >= 1.0f ) return poses[b]?.Pose( context ) ?? context.RestPose();

		var was = context.PushWeight( 1.0f - alpha );
		var left = poses[a]?.Pose( context ) ?? context.RestPose();
		context.PopWeight( was );

		//The first has moved for this frame; the second is put where it is before it is
		//asked, so the two play the same stride rather than each running on its own clock.
		if ( synchronized ) InStep( context, poses[a], poses[b] );

		was = context.PushWeight( alpha );
		var right = poses[b]?.Pose( context ) ?? context.RestPose();
		context.PopWeight( was );

		return context.Blend( left, right, alpha );
	}

	//Syncs follower's cycle phase to the leader's (most weighted), minus the follower's pending step
	internal static void InStep( GraphContext context, GraphNode leader, GraphNode follower )
	{
		if ( leader is null || follower is null || ReferenceEquals( leader, follower ) ) return;

		var length = follower.Duration( context );
		if ( length <= 0.0f ) return;

		follower.Seek( context, ( leader.Progress( context ) * length ) - context.Delta );
	}

	public override float Duration( GraphContext context )
	{
		var longest = 0.0f;
		foreach ( var pose in poses ) longest = MathF.Max( longest, pose?.Duration( context ) ?? 0.0f );

		return longest;
	}
}

//The same across a plane: poses at points, blended by which triangle a pair of values lands in.
internal sealed class BlendSpace2DNode : PoseNode
{
	private readonly int x, y;
	private readonly int[] sources;
	private readonly Vector2[] positions;
	private readonly int[] triangles;
	private readonly bool synchronized;

	private GraphNode xInput, yInput;
	private GraphNode[] poses = Array.Empty<GraphNode>();

	public BlendSpace2DNode( JsonObject node )
	{
		x = GraphFields.Reference( node, "X" );
		y = GraphFields.Reference( node, "Y" );
		synchronized = GraphFields.Flag( node, "Synchronized" );

		var list = GraphFields.Array( node, "Sources" );
		var places = GraphFields.Array( node, "Positions" );
		var indices = GraphFields.Array( node, "Indices" );

		var count = list?.Count ?? 0;

		sources = new int[count];
		positions = new Vector2[count];

		for ( var i = 0; i < count; i++ )
		{
			sources[i] = list[i]?.GetValue<int>() ?? -1;

			if ( places is null || i >= places.Count || places[i] is not JsonArray point ) continue;

			positions[i] = new Vector2(
				point.Count > 0 ? point[0]?.GetValue<float>() ?? 0.0f : 0.0f,
				point.Count > 1 ? point[1]?.GetValue<float>() ?? 0.0f : 0.0f );
		}

		triangles = new int[indices?.Count ?? 0];
		for ( var i = 0; i < triangles.Length; i++ ) triangles[i] = indices[i]?.GetValue<int>() ?? 0;
	}

	public override void Link( GraphNode[] nodes )
	{
		xInput = ValueNodes.Get( nodes, x );
		yInput = ValueNodes.Get( nodes, y );

		poses = new GraphNode[sources.Length];
		for ( var i = 0; i < sources.Length; i++ ) poses[i] = ValueNodes.Get( nodes, sources[i] );
	}

	protected override int OnPose( GraphContext context )
	{
		if ( poses.Length == 0 ) return context.RestPose();
		if ( poses.Length == 1 || triangles.Length < 3 ) return poses[0]?.Pose( context ) ?? context.RestPose();

		var at = new Vector2( xInput?.Float( context ) ?? 0.0f, yInput?.Float( context ) ?? 0.0f );

		//Where in the space it is, for anything drawing the space: the point is two parameters that were read here, and nothing else can see it.
		context.Debug?.Blend2D( this, at );

		//Whichever triangle it is in, by barycentric weights - which is what makes three poses blend smoothly rather than in two steps.
		var best = -1;
		var bestDistance = float.MaxValue;

		Vector3 weights = default;

		for ( var t = 0; t + 2 < triangles.Length; t += 3 )
		{
			var found = Barycentric( at, positions[triangles[t]], positions[triangles[t + 1]], positions[triangles[t + 2]] );

			//Inside is all three weights non-negative. Outside, the one that misses by least is the
			//nearest - a value off the edge of the space should land on the edge and not on nothing.
			var miss = MathF.Max( 0.0f, -MathF.Min( found.x, MathF.Min( found.y, found.z ) ) );

			if ( miss >= bestDistance ) continue;

			bestDistance = miss;
			best = t;
			weights = found;

			if ( miss <= 0.0f ) break;
		}

		if ( best < 0 ) return poses[0]?.Pose( context ) ?? context.RestPose();

		//Clamped and renormalised, so a point outside is the nearest point on the triangle rather than an extrapolation past it.
		var a = MathF.Max( weights.x, 0.0f );
		var b = MathF.Max( weights.y, 0.0f );
		var c = MathF.Max( weights.z, 0.0f );

		var total = a + b + c;
		if ( total <= 0.0f ) return poses[triangles[best]]?.Pose( context ) ?? context.RestPose();

		a /= total;
		b /= total;
		c /= total;

		if ( synchronized )
		{
			//Whichever corner the character is walking most of keeps time, and the other
			//two are put into its stride before either is asked - see BlendSpaceNode.InStep.
			var lead = a >= b && a >= c ? poses[triangles[best]]
				: b >= c ? poses[triangles[best + 1]]
				: poses[triangles[best + 2]];

			//Asked and handed straight back: what this is for is the step it takes when it is asked, after which the other two can be
			//put where it is. It is asked again below as part of the blend, which for a node with one parent is one extra sample a frame.
			var moved = lead?.Pose( context ) ?? -1;
			if ( moved >= 0 ) context.Free( moved );

			BlendSpaceNode.InStep( context, lead, poses[triangles[best]] );
			BlendSpaceNode.InStep( context, lead, poses[triangles[best + 1]] );
			BlendSpaceNode.InStep( context, lead, poses[triangles[best + 2]] );
		}

		var pose = poses[triangles[best]]?.Pose( context ) ?? context.RestPose();

		//Two blends rather than three weights: the second is weighted against what the first already holds.
		var second = poses[triangles[best + 1]]?.Pose( context ) ?? context.RestPose();
		pose = context.Blend( pose, second, a + b <= 0.0f ? 0.0f : b / ( a + b ) );

		var third = poses[triangles[best + 2]]?.Pose( context ) ?? context.RestPose();

		return context.Blend( pose, third, c );
	}

	//Where a point sits in a triangle, as one weight per corner.
	private static Vector3 Barycentric( Vector2 point, Vector2 a, Vector2 b, Vector2 c )
	{
		var v0 = b - a;
		var v1 = c - a;
		var v2 = point - a;

		var d00 = Vector2.Dot( v0, v0 );
		var d01 = Vector2.Dot( v0, v1 );
		var d11 = Vector2.Dot( v1, v1 );
		var d20 = Vector2.Dot( v2, v0 );
		var d21 = Vector2.Dot( v2, v1 );

		var denominator = ( d00 * d11 ) - ( d01 * d01 );

		//A triangle with no area has no inside; the first corner is as good an answer as any and better than a division by nothing.
		if ( MathF.Abs( denominator ) < 1e-12f ) return new Vector3( 1.0f, 0.0f, 0.0f );

		var v = ( ( d11 * d20 ) - ( d01 * d21 ) ) / denominator;
		var w = ( ( d00 * d21 ) - ( d01 * d20 ) ) / denominator;

		return new Vector3( 1.0f - v - w, v, w );
	}

	public override float Duration( GraphContext context )
	{
		var longest = 0.0f;
		foreach ( var pose in poses ) longest = MathF.Max( longest, pose?.Duration( context ) ?? 0.0f );

		return longest;
	}
}

//A base pose with more laid on top of it.
internal sealed class LayersNode : PoseNode
{
	private sealed class Layer
	{
		public int Pose, Weight, Mask;
		public AnimGraphBuilder.LayerBlendMode BlendMode;

		/// <summary>Only which way the bones point, not how far apart they sit.</summary>
		public bool RotationOnly;

		/// <summary>A difference laid on a channel at a time.</summary>
		public bool ChannelsApart;

		/// <summary>Authored against the model rather than each parent.</summary>
		public bool ModelSpace;

		/// <summary>Whether what this layer weighs and what it is masked by come from the states inside it rather than from here.</summary>
		public bool FromStates;

		/// <summary>How much of the movement in the layer the character takes.</summary>
		public int RootMotionWeight = -1;

		public GraphNode PoseNode, WeightNode, MaskNode, RootMotionNode;
	}

	private readonly int basePose;
	private readonly Layer[] layers;

	//Whether the layers move the character as well as pose it. Off unless the graph says otherwise: an upper body layer laid over a walk carries
	//the walk's root along with it, and a character moved once by the base and again by the layer walks twice as fast as it is animated to.
	private readonly bool rootMotion;

	private GraphNode under;

	public LayersNode( JsonObject node )
	{
		basePose = GraphFields.Reference( node, "Base" );
		rootMotion = GraphFields.Flag( node, "RootMotion" );

		var list = GraphFields.Array( node, "Layers" );
		layers = new Layer[list?.Count ?? 0];

		for ( var i = 0; i < layers.Length; i++ )
		{
			var entry = list[i] as JsonObject;

			layers[i] = new Layer
			{
				Pose = GraphFields.Reference( entry, "Pose" ),
				Weight = GraphFields.Reference( entry, "Weight" ),
				Mask = GraphFields.Reference( entry, "Mask" ),
				BlendMode = GraphFields.Enum( entry, "BlendMode", AnimGraphBuilder.LayerBlendMode.Overlay ),
				RotationOnly = GraphFields.Flag( entry, "RotationOnly" ),
				ChannelsApart = GraphFields.Flag( entry, "ChannelsApart" ),
				ModelSpace = GraphFields.Flag( entry, "ModelSpace" ),
				FromStates = GraphFields.Flag( entry, "FromStates" ),
				RootMotionWeight = GraphFields.Reference( entry, "RootMotionWeight" ),
			};
		}
	}

	public override void Link( GraphNode[] nodes )
	{
		under = ValueNodes.Get( nodes, basePose );

		foreach ( var layer in layers )
		{
			layer.PoseNode = ValueNodes.Get( nodes, layer.Pose );
			layer.WeightNode = ValueNodes.Get( nodes, layer.Weight );
			layer.MaskNode = ValueNodes.Get( nodes, layer.Mask );
			layer.RootMotionNode = ValueNodes.Get( nodes, layer.RootMotionWeight );
		}
	}

	protected override int OnPose( GraphContext context )
	{
		var pose = under?.Pose( context ) ?? context.RestPose();

		var index = -1;

		foreach ( var layer in layers )
		{
			index++;

			if ( layer.PoseNode is null ) continue;

			//A layer weighed by the states inside it has to run before what it weighs is known - the state machine decides, and
			//it cannot decide without being asked. Every other layer is worked out first, so one weighing nothing costs nothing.
			var machine = layer.FromStates ? layer.PoseNode as StateMachineNode : null;

			//A layer with no weight connected is fully on, which is what an author means by adding one and not wiring anything to it.
			var weight = layer.WeightNode is null ? 1.0f : Math.Clamp( layer.WeightNode.Float( context ), 0.0f, 1.0f );
			if ( weight <= 0.0f && machine is null ) continue;

			var was = context.PushWeight( weight );
			var over = layer.PoseNode.Pose( context );
			context.PopWeight( was );

			if ( machine is not null ) weight *= machine.LayerWeight( context );

			var mask = layer.MaskNode?.Mask( context ) ?? -1;
			if ( mask < 0 && machine is not null ) mask = machine.LayerMask( context );

			mask = HoldTheRoot( context, layer, machine, mask );

			if ( weight <= 0.0f )
			{
				context.Debug?.Layer( this, index, 0.0f );

				//Run, and then found to be worth nothing: what it produced is dropped rather than blended in at zero.
				context.Free( over );

				if ( mask >= 0 ) context.FreeMask( mask );

				continue;
			}

			//What the layer ended up weighing, which for one weighed by its own states is not something anything outside it knows.
			context.Debug?.Layer( this, index, weight );

			Owner?.Note( $"#{Index} layer {index} in, w={weight:0.00}", pose );
			Owner?.Note( $"#{Index} layer {index} over", over );

			pose = layer.BlendMode == AnimGraphBuilder.LayerBlendMode.Additive
				? ( layer.ModelSpace
					? context.AdditiveModel( pose, over, weight, mask )
					: context.Additive( pose, over, weight, mask, layer.ChannelsApart ) )
				: layer.ModelSpace
					? context.BlendModel( pose, over, weight, mask, layer.RotationOnly )
					: layer.RotationOnly
						? context.BlendRotation( pose, over, weight, mask )
						: context.Blend( pose, over, weight, mask );

			Owner?.Note( $"#{Index} layer {index} out" +
				$" ({layer.BlendMode}{( layer.ModelSpace ? "+model" : "" )}" +
				$"{( layer.RotationOnly ? "+rotonly" : "" )}{( layer.ChannelsApart ? "+apart" : "" )})", pose );

			if ( mask >= 0 ) context.FreeMask( mask );
		}

		return pose;
	}

	//Keep a layer from moving the character, unless it is meant to. Done to the mask rather than to the pose, because that is what a mask is for: the root
	//bone taking none of the layer is the same thing as the layer not moving the character, and everything hanging off it still gets the layer in full.
	private int HoldTheRoot( GraphContext context, Layer layer, StateMachineNode machine, int mask )
	{
		var wanted = rootMotion ? 1.0f : 0.0f;

		if ( layer.RootMotionNode is not null ) wanted = Math.Clamp( layer.RootMotionNode.Float( context ), 0.0f, 1.0f );
		if ( machine is not null ) wanted *= machine.LayerRootMotionWeight( context );

		if ( wanted >= 1.0f ) return mask;

		var root = Effects.RootBone( context );
		if ( root < 0 ) return mask;

		if ( mask < 0 )
		{
			mask = context.AllocMask();
			if ( mask < 0 ) return mask;

			context.FillMask( mask, 1.0f );
		}

		context.SetMaskBone( mask, root, wanted, false );

		return mask;
	}

	public override float Duration( GraphContext context ) => under?.Duration( context ) ?? 0.0f;
	public override float Progress( GraphContext context ) => under?.Progress( context ) ?? 0.0f;

	//What is underneath, because that is what the layers are laid on: lining a layered pose up means lining up what it is layered over.
	public override void Seek( GraphContext context, float time ) => under?.Seek( context, time );

	public override bool CurrentSync( GraphContext context, out SyncPoint point )
	{
		point = default;
		return under?.CurrentSync( context, out point ) ?? false;
	}

	public override bool SeekToSync( GraphContext context, in SyncPoint point,
		AnimGraphBuilder.TransitionSyncMatch match, bool preferClosestId )
		=> under?.SeekToSync( context, point, match, preferClosestId ) ?? false;
}

//Difference between two poses, for additive layers; computed per bone since core can't subtract
internal sealed class SubtractPoseNode : PoseNode
{
	private readonly int minuend, subtrahend;

	private GraphNode from, take;

	public SubtractPoseNode( JsonObject node )
	{
		minuend = GraphFields.Reference( node, "Pose" );
		subtrahend = GraphFields.Reference( node, "Base" );
	}

	public override void Link( GraphNode[] nodes )
	{
		from = ValueNodes.Get( nodes, minuend );
		take = ValueNodes.Get( nodes, subtrahend );
	}

	protected override int OnPose( GraphContext context )
	{
		var result = from?.Pose( context ) ?? context.RestPose();
		if ( take is null ) return result;

		var reference = take.Pose( context );

		try
		{
			var instance = context.Instance;
			var bones = context.Skeleton?.BoneCount ?? 0;

			for ( var bone = 0; bone < bones; bone++ )
			{
				if ( !instance.ReadBone( result, bone, out var mine ) ) continue;
				if ( !instance.ReadBone( reference, bone, out var theirs ) ) continue;

				var a = mine.ToTransform();
				var b = theirs.ToTransform();

				//What has to be added to the reference to arrive at this pose, in the reference's own
				//frame - the same way the core reads a difference back when it lays one over something.
				var delta = new Transform(
					a.Position - b.Position,
					b.Rotation.Inverse * a.Rotation,
					b.Scale == 0.0f ? 1.0f : a.Scale / b.Scale );

				instance.WriteBone( result, bone, AnimTrs.FromTransform( delta ) );
			}
		}
		finally
		{
			context.Free( reference );
		}

		return result;
	}

	public override float Duration( GraphContext context ) => from?.Duration( context ) ?? 0.0f;
	public override float Progress( GraphContext context ) => from?.Progress( context ) ?? 0.0f;

	public override void Seek( GraphContext context, float time )
	{
		from?.Seek( context, time );
		take?.Seek( context, time );
	}
}

//Scrubs what is under it to a 0-1 fraction from a value instead of the clock
internal sealed class CycleControlNode : PoseNode
{
	private readonly int pose, cycle;
	private readonly bool seconds;

	private GraphNode input, value;

	public CycleControlNode( JsonObject node )
	{
		pose = GraphFields.Reference( node, "Pose" );
		cycle = GraphFields.Reference( node, "Cycle" );
		seconds = GraphFields.Flag( node, "InSeconds" );
	}

	public override void Link( GraphNode[] nodes )
	{
		input = ValueNodes.Get( nodes, pose );
		value = ValueNodes.Get( nodes, cycle );
	}

	protected override int OnPose( GraphContext context )
	{
		if ( input is null ) return context.RestPose();

		if ( value is not null )
		{
			var at = value.Float( context );

			//A fraction unless the graph said otherwise, turned into the time the pose below actually measures itself in.
			if ( !seconds ) at *= input.Duration( context );

			input.Seek( context, at );
		}

		return input.Pose( context );
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
}

internal sealed class SpeedScaleNode : PoseNode
{
	private readonly int pose, scale;

	private GraphNode input, amount;

	public SpeedScaleNode( JsonObject node )
	{
		pose = GraphFields.Reference( node, "Pose" );
		scale = GraphFields.Reference( node, "Scale" );
	}

	public override void Link( GraphNode[] nodes )
	{
		input = ValueNodes.Get( nodes, pose );
		amount = ValueNodes.Get( nodes, scale );
	}

	protected override int OnPose( GraphContext context )
	{
		//The scale is applied by shortening the frame rather than by telling the clip below how fast to
		//go: it works for anything under it, including a blend of several clips at different lengths.
		var was = context.Delta;
		context.Scale( amount?.Float( context ) ?? 1.0f );

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

//Turn a pose down rather than off: bones move less far from where they rest.
internal sealed class PoseScaleNode : PoseNode
{
	private readonly int pose, mask, enable;

	private GraphNode input, maskNode, enabled;

	public PoseScaleNode( JsonObject node )
	{
		pose = GraphFields.Reference( node, "Pose" );
		mask = GraphFields.Reference( node, "Mask" );
		enable = GraphFields.Reference( node, "Enable" );
	}

	public override void Link( GraphNode[] nodes )
	{
		input = ValueNodes.Get( nodes, pose );
		maskNode = ValueNodes.Get( nodes, mask );
		enabled = ValueNodes.Get( nodes, enable );
	}

	protected override int OnPose( GraphContext context )
	{
		var source = input?.Pose( context ) ?? context.RestPose();

		if ( enabled is not null && !enabled.Bool( context ) ) return source;

		var weights = maskNode?.Mask( context ) ?? -1;
		if ( weights < 0 ) return source;

		//Towards the rest pose by however much the mask holds back.
		var rest = context.RestPose();
		var blended = context.Blend( rest, source, 1.0f, weights );

		context.FreeMask( weights );

		return blended;
	}

	public override float Duration( GraphContext context ) => input?.Duration( context ) ?? 0.0f;
}
