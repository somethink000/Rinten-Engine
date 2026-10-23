using Rinten.CoreAnimation;

namespace Rinten;

//Core animation backend: skeleton, pose and clips in model space; one thread poses an instance
internal sealed class CoreAnimatableObject : IAnimatableObjectBackend
{
	private static readonly Logger log = new( "Core" );

	private readonly SceneModel owner;

	private CoreAnimRuntime.Skeleton skeleton;
	private CoreAnimRuntime.Instance instance;

	//What the skeleton was built from, so a changed model rebuilds it.
	private Model model;

	private string sequence = "";
	private float delta;

	//Where this object's jiggle bones are, made the first frame it has any.
	private CoreJiggle.State jiggle;

	public CoreAnimatableObject( SceneModel owner )
	{
		this.owner = owner;
		Rebuild();
	}

	/// <summary>Build the skeleton and the pose standing in it, from whatever model the scene object has now.</summary>
	public void Rebuild()
	{
		instance = null;
		jiggle = null;
		poseBoundsStamp = -1;

		model = owner.Model;
		skeleton = CoreSkeletons.Skeleton( model );

		if ( skeleton is null ) return;

		instance = CoreAnimRuntime.CreateInstance( skeleton );

		//A model can change under a scene object, and what it was playing is a name or a graph rather than a pose - so it is asked for again rather than lost.
		if ( graph is not null ) Graph = graph;
		else if ( !string.IsNullOrEmpty( sequence ) ) Sequence = sequence;
	}

	/// <summary>Let go of the pose. Nothing is freed by hand - an instance is an ordinary object and goes when the last reference to it does.</summary>
	public void Destroy() => instance = null;

	//Whether this can pose anything. A model that is not ours, or has no bones, has no skeleton - and there is nothing here for it to do.
	internal bool CanPose => instance is not null;

	private bool IsValid => instance is not null;

	//The scene object's transform, which is what takes the model's own space out into the world.
	private Transform ToWorld => owner.Transform;

	public void Update( float updateDelta )
	{
		delta = updateDelta;

		if ( !IsValid ) return;
		if ( model != owner.Model ) Rebuild();

		//A graph decides the whole pose, so the clip the instance would play on its own does not run as well
		//- it was cleared when the graph was set, and this is where the two would otherwise both write.
		if ( RunningGraph is not null )
		{
			running.Update( updateDelta * instance.Rate, ToWorld );
			Jiggle( updateDelta );
			return;
		}

		CoreAnimRuntime.Update( instance, updateDelta );
		Jiggle( updateDelta );
	}

	//Swing the model's jiggle bones on the pose just made. Here rather than in the graph, so a model swings whatever poses it -
	//a graph, a clip, or nothing at all - and on the frame's own time rather than the animation's: a paused clip still settles.
	private void Jiggle( float updateDelta )
	{
		var set = CoreSkeletons.Jiggles( model );

		if ( set.IsEmpty )
		{
			jiggle = null;
			return;
		}

		jiggle ??= new CoreJiggle.State();

		CoreJiggle.Apply( instance, set, jiggle, ToWorld, updateDelta );
	}

	public void SetBindPose()
	{
		if ( !IsValid ) return;

		CoreAnimRuntime.BindPose( instance );
	}

	public void FinishBoneUpdate()
	{
		if ( !IsValid ) return;

		//The local pose may have been written into since the update - a procedural bone read off a
		//game object - so the model-space pose is worked out again rather than assumed still true.
		CoreAnimRuntime.ComposeNow( instance );
	}

	/// <summary>Take this model's bones from another's, by name. Returns how many of ours the other rig answered for - nought where the two share no bone names.</summary>
	public int MergeFrom( SceneModel parent )
	{
		if ( !IsValid ) return 0;
		if ( parent?.Animation is not CoreAnimatableObject from || !from.IsValid ) return 0;

		return CoreAnimRuntime.Merge( instance, from.instance );
	}

	//-- bones ------------------------------------------------------------------

	public Transform GetParentSpaceBone( int bone )
	{
		if ( !IsValid ) return Transform.Zero;

		return instance.LocalBone( bone, out var trs ) ? trs.ToTransform() : Transform.Zero;
	}

	public void SetParentSpaceBone( int bone, in Transform transform )
	{
		if ( !IsValid ) return;
		if ( bone < 0 || bone >= instance.Local.Length ) return;

		instance.Local[bone] = AnimTrs.FromTransform( transform );
	}

	public void GetParentSpaceBones( Span<Transform> destination )
	{
		if ( !IsValid || destination.IsEmpty ) return;

		var local = instance.Local;
		var count = Math.Min( destination.Length, local.Length );

		for ( var bone = 0; bone < count; bone++ )
			destination[bone] = local[bone].ToTransform();
	}

	public Transform GetBoneWorldTransform( int bone )
	{
		if ( !IsValid ) return Transform.Zero;

		return instance.ModelBone( bone, out var trs ) ? ToWorld.ToWorld( trs.ToTransform() ) : Transform.Zero;
	}

	public Transform GetBoneWorldTransform( string bone ) => GetBoneWorldTransform( Find( bone ) );

	public void SetBoneWorldTransform( int bone, in Transform transform )
	{
		if ( !IsValid ) return;

		//The pose is kept relative to each bone's parent, so a world transform has to
		//come back through the object and then through whatever the bone hangs off.
		var local = ToWorld.ToLocal( transform );

		var parent = skeleton.BoneParent( bone );
		if ( parent >= 0 ) local = GetModelBone( parent ).ToLocal( local );

		SetParentSpaceBone( bone, local );
		FinishBoneUpdate();
	}

	public void GetBoneWorldTransforms( Span<Transform> destination )
	{
		if ( !IsValid || destination.IsEmpty ) return;

		var toWorld = ToWorld;
		var pose = instance.Model;
		var count = Math.Min( destination.Length, pose.Length );

		for ( var bone = 0; bone < count; bone++ )
			destination[bone] = toWorld.ToWorld( pose[bone].ToTransform() );
	}

	public void GetBoneWorldPreviousTransforms( Span<Transform> destination )
	{
		if ( !IsValid || destination.IsEmpty ) return;

		var toWorld = ToWorld;
		var pose = instance.Previous;
		var count = Math.Min( destination.Length, pose.Length );

		for ( var bone = 0; bone < count; bone++ )
			destination[bone] = toWorld.ToWorld( pose[bone].ToTransform() );
	}

	public Transform GetBoneWorldPreviousTransform( int bone )
	{
		if ( !IsValid ) return Transform.Zero;

		var pose = instance.Previous;
		if ( bone < 0 || bone >= pose.Length ) return Transform.Zero;

		return ToWorld.ToWorld( pose[bone].ToTransform() );
	}

	public Transform GetBoneLocalTransform( int bone )
	{
		if ( !IsValid ) return Transform.Zero;

		//Local to the scene object, which is the model's own space - not local to the parent bone, which is what GetParentSpaceBone is for.
		return GetModelBone( bone );
	}

	public Transform GetBoneLocalTransform( string bone ) => GetBoneLocalTransform( Find( bone ) );

	public int GetClosestBoneIndex( Vector3 worldPoint )
	{
		if ( !IsValid ) return -1;

		var point = ToWorld.PointToLocal( worldPoint );
		var pose = instance.Model;

		var closest = -1;
		var nearest = float.MaxValue;

		for ( var bone = 0; bone < pose.Length; bone++ )
		{
			var distance = pose[bone].Position.DistanceSquared( point );
			if ( distance >= nearest ) continue;

			nearest = distance;
			closest = bone;
		}

		return closest;
	}

	public float DeltaTime => delta;

	//-- bones driven from outside ----------------------------------------------

	/// <summary>Put a bone where something outside the animation says it goes, in the model's own space.</summary>
	public void SetBoneOverride( int bone, in Transform transform )
	{
		if ( !IsValid ) return;

		var parent = skeleton.BoneParent( bone );
		var local = parent >= 0 ? GetModelBone( parent ).ToLocal( transform ) : transform;

		CoreAnimRuntime.SetBone( instance, bone, AnimTrs.FromTransform( local ) );
	}

	public void ClearBoneOverrides()
	{
		if ( !IsValid ) return;

		CoreAnimRuntime.ClearBones( instance );
	}

	public bool HasBoneOverrides() => IsValid && instance.OverrideCount > 0;

	/// <summary>Null: the world box is the local one through the object's transform - see LocalBounds.</summary>
	public BBox? WorldBounds => null;

	/// <summary>The pose's bounds in the model's space, from its bones; null where the model's own must do - see CorePoseReach.</summary>
	public BBox? LocalBounds
	{
		get
		{
			if ( !IsValid || CoreSkeletons.Reach( model ) is not { } reach ) return null;

			//Shapes are weighed every time: they fade on their own, with nothing in the pose to say so
			var shapes = owner.Morphs as SceneObjectMorphCollection;
			var shaped = reach.Shapes.Length > 0 && shapes is { Any: true };

			if ( poseBoundsStamp == instance.Stamp && poseBoundsReach == reach && !shaped ) return poseBounds;

			System.Span<float> weights = shaped && reach.Shapes.Length <= 256 ? stackalloc float[reach.Shapes.Length] : default;
			if ( shaped && weights.IsEmpty ) weights = new float[reach.Shapes.Length];
			if ( shaped ) shapes.WeightsInto( weights );

			poseBounds = reach.Bounds( instance.Model, weights );
			poseBoundsStamp = instance.Stamp;
			poseBoundsReach = reach;

			return poseBounds;
		}
	}

	//The pose's bounds as last found, and the pose and reach they were found from
	private BBox? poseBounds;
	private int poseBoundsStamp = -1;
	private CorePoseReach poseBoundsReach;

	//-- attachments ------------------------------------------------------------

	/// <summary>Where an attachment is this frame.</summary>
	public Transform? GetAttachment( string name, bool worldSpace )
	{
		var attachment = model?.Attachments?.Get( name );

		if ( attachment is not null )
		{
			Transform Carrier( BoneCollection.Bone bone )
				=> IsValid ? GetModelBone( bone.Index ) : model.GetBoneTransform( bone.Index );

			//A place pulled between several bones is the weighted average of where each of them puts it. The
			//point between both hands moves with both; taking the first bone alone leaves it in one hand.
			if ( attachment.Pulled.Length > 1
				&& ModelAttachments.Attachment.Between( attachment.Pulled, Carrier ) is { } between )
			{
				between = attachment.Faced( between );

				return worldSpace ? ToWorld.ToWorld( between ) : between;
			}

			//Carrier bone in the current pose, or the model origin for boneless props
			var carrier = attachment.Bone is null ? Transform.Zero : Carrier( attachment.Bone );

			//Turned the one way it faces, where its bones do not decide that.
			var local = attachment.Faced( carrier.ToWorld( attachment.LocalTransform ) );

			return worldSpace ? ToWorld.ToWorld( local ) : local;
		}

		if ( !IsValid ) return null;

		var named = Find( name );
		if ( named < 0 ) return null;

		return worldSpace ? GetBoneWorldTransform( named ) : GetModelBone( named );
	}

	//-- playback ---------------------------------------------------------------

	public float PlaybackRate
	{
		get => IsValid ? instance.Rate : 1.0f;
		set { if ( IsValid ) instance.Rate = value; }
	}

	public Transform RootMotion => IsValid ? instance.RootMotion.ToTransform() : Transform.Zero;

	//-- graphs -----------------------------------------------------------------

	private Animation.AnimGraphInstance running;

	/// <summary>The graph driving this. Setting one starts it from the beginning, which is what swapping a character's whole way of moving means.</summary>
	public AnimGraph Graph
	{
		get => graph;
		set => Adopt( value, running );
	}

	//The instance a new one takes its values from, while it is made.
	private Animation.AnimGraphInstance replacing;

	//Play a graph, taking over from whatever was playing before.
	private void Adopt( AnimGraph playing, Animation.AnimGraphInstance was )
	{
		graph = playing;

		replacing = was;
		running = null;
		refused = false;

		Start();
	}

	//Carries state over from the replaced backend on model change, so the graph keeps running
	internal void TakeOver( IAnimatableObjectBackend backend )
	{
		if ( backend is not CoreAnimatableObject was || ReferenceEquals( was, this ) ) return;

		PlaybackRate = was.PlaybackRate;

		//Counted on from where the last one got to, so whoever watches this to know the graph is new is not fooled by a fresh backend starting at nought.
		GraphGeneration = was.GraphGeneration;

		if ( was.graph is not null )
		{
			Adopt( was.graph, was.running );
			return;
		}

		if ( !string.IsNullOrEmpty( was.sequence ) ) Sequence = was.sequence;
	}

	private AnimGraph graph;

	//Set once a graph has been tried and had nothing to play.
	private bool refused;

	/// <summary>Running graph, started lazily since it may be assigned before the skeleton exists</summary>
	public Animation.AnimGraphInstance RunningGraph => running ?? Start();

	public int GraphGeneration { get; private set; }

	private Animation.AnimGraphInstance Start()
	{
		if ( running is not null ) return running;
		if ( graph is null || refused || !IsValid ) return null;

		//What the game had set on the one this replaces. A character changes model when it draws a weapon, the model rebuilds the graph,
		//and without this everything set before that moment - the hold type most of all - is back at its default before anything reads it.
		var was = replacing;

		replacing = null;

		running = new Animation.AnimGraphInstance( graph, instance, skeleton, model );

		if ( !running.IsValid )
		{
			log.Warning( $"{graph.Name}: nothing to play - the graph has no output" );

			running = null;
			refused = true;

			return null;
		}

		running.Reset();
		running.CarryOver( was );

		//Everything the game set is kept where it was set from, and that is a fuller answer than what one instance can hand the next: a
		//target, an enum set by option name, a parameter set while there was no graph at all. Saying so here is what asks for all of it again.
		GraphGeneration++;

		//A graph and a sequence are two ways to drive the same bones, and the graph is the one that was just asked for.
		sequence = "";
		CoreAnimRuntime.SetClip( instance, null );

		return running;
	}

	public void SetParameter( string name, float value ) => RunningGraph?.SetFloat( name, value );
	public void SetParameter( string name, bool value ) => RunningGraph?.SetBool( name, value );
	public void SetParameter( string name, int value ) => RunningGraph?.SetInt( name, value );
	public void SetParameter( string name, Vector3 value ) => RunningGraph?.SetVector( name, value );

	/// <summary>A rotation, which our graphs have no parameter kind for - a direction does everything a graph actually asks of one.</summary>
	public void SetParameter( string name, Rotation value ) => RunningGraph?.SetVector( name, value.Forward );

	public bool SetParameter( string name, string option )
	{
		var graph = RunningGraph;
		if ( graph is null || !graph.Has( name ) ) return false;

		graph.SetId( name, option );

		return true;
	}

	public void SetParameter( string name, Transform value ) => RunningGraph?.SetTarget( name, value );

	public void SetTargetBone( string name, string bone ) => RunningGraph?.SetTargetBone( name, bone );

	public void ClearTarget( string name ) => RunningGraph?.ClearTarget( name );

	//The parameters, not the graph: restarting every state machine as well is not what putting the inputs back asks for, and it is what this did.
	public void ResetParameters() => RunningGraph?.ResetParameters();

	public void ResetParameter( string name ) => RunningGraph?.ResetParameter( name );

	//Reading a parameter back is what the game set, not what the graph made of it - a graph does not hand its working out back.
	//An id is the exception: the name it was set by is a number to whoever asks, and the graph is the only one who knows which.
	public int GetIntParameter( string name ) => running?.GetInt( name ) ?? 0;

	//-- sequences --------------------------------------------------------------

	public string Sequence
	{
		get => sequence;
		set
		{
			sequence = value ?? "";

			if ( !IsValid ) return;

			//A sequence and a graph both drive every bone, and the one asked for last wins
			//- two of them writing the same pose is not a blend, it is whichever ran second.
			if ( !string.IsNullOrEmpty( sequence ) )
			{
				running = null;
				graph = null;
			}

			if ( string.IsNullOrEmpty( sequence ) )
			{
				CoreAnimRuntime.SetClip( instance, null );
				return;
			}

			var clip = CoreSkeletons.Clip( model, sequence );

			if ( clip is null )
			{
				log.Warning( $"{model?.Name}: no sequence called '{sequence}'" );
				return;
			}

			CoreAnimRuntime.SetClip( instance, clip );
		}
	}

	public float SequenceCycle
	{
		get
		{
			var duration = SequenceDuration;
			return duration > 0.0f ? instance.Time / duration : 0.0f;
		}
		set
		{
			if ( !IsValid ) return;

			CoreAnimRuntime.SetTime( instance, value * SequenceDuration );
		}
	}

	public float SequenceDuration => IsValid ? instance.Clip?.Duration ?? 0.0f : 0.0f;

	public bool SequenceFinished => IsValid && instance.Finished;

	public bool SequenceLooping
	{
		set { if ( IsValid ) instance.Looping = value; }
	}

	/// <summary>Blending between one sequence and the next, which nothing here does yet.</summary>
	public bool SequenceBlending { set { } }

	public IReadOnlyList<string> SequenceNames => CoreSkeletons.SequenceNames( model );

	//-- shared -----------------------------------------------------------------

	private int Find( string bone ) => skeleton?.FindBone( bone ) ?? -1;

	private Transform GetModelBone( int bone )
		=> instance.ModelBone( bone, out var trs ) ? trs.ToTransform() : Transform.Zero;
}
