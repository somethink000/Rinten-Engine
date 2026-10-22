using System.Numerics;
using System.Runtime.CompilerServices;

namespace Rinten.CoreAnimation;

//Managed skeletons and pose instances (port of pose.cpp); a skeleton is shared by all its instances
//Working poses and masks inside an instance are graph scratch buffers referenced by index
internal static class CoreAnimRuntime
{
	//-- skeletons ---------------------------------------------------------------

	internal sealed class Skeleton
	{
		public struct BoneEntry
		{
			public string Name;

			/// <summary>The same name folded down, which is what lookups use. Rigs are matched by name across files nobody built together, and a capital letter is not a different bone.</summary>
			public string Key;

			public int Parent;

			/// <summary>Where the bone rests, relative to the one above it.</summary>
			public AnimTrs Rest;
		}

		public BoneEntry[] Bones = Array.Empty<BoneEntry>();

		/// <summary>Bone indices with every parent before its children, so a pose is composed in one pass.</summary>
		public int[] Order = Array.Empty<int>();

		public readonly Dictionary<string, int> ByName = new( StringComparer.Ordinal );

		/// <summary>Per clip: which of our bones each clip bone maps to, or -1; weak-keyed and thread-safe</summary>
		public readonly ConditionalWeakTable<AnimClipData, int[]> ClipBindings = new();

		/// <summary>The same for another skeleton, which is what a bone merge needs.</summary>
		public readonly ConditionalWeakTable<Skeleton, int[]> SkeletonBindings = new();

		public int BoneCount => Bones.Length;

		public string BoneName( int bone )
			=> bone >= 0 && bone < Bones.Length ? Bones[bone].Name : "";

		public int BoneParent( int bone )
			=> bone >= 0 && bone < Bones.Length ? Bones[bone].Parent : -1;

		/// <summary>Where a bone rests, relative to the one above it.</summary>
		public AnimTrs Rest( int bone )
			=> bone >= 0 && bone < Bones.Length ? Bones[bone].Rest : AnimTrs.Identity;

		/// <summary>A bone by name, in any case, or -1.</summary>
		public int FindBone( string name )
			=> string.IsNullOrEmpty( name ) ? -1 : Find( Lowered( name ) );

		public int Find( string loweredName )
			=> ByName.TryGetValue( loweredName, out var bone ) ? bone : -1;
	}

	private static string Lowered( string name ) => ( name ?? "" ).ToLowerInvariant();

	//Bone indices with every parent before its children. Worked out here rather than trusted from the file: a model's bones are written in whatever order
	//the exporter listed them, and composing a pose against a parent that has not been composed yet is a frame of lag that looks like a rig problem.
	private static int[] EvaluationOrder( Skeleton.BoneEntry[] bones )
	{
		var order = new List<int>( bones.Length );
		var placed = new bool[bones.Length];

		var progress = true;

		while ( order.Count < bones.Length && progress )
		{
			progress = false;

			for ( var bone = 0; bone < bones.Length; ++bone )
			{
				if ( placed[bone] ) continue;

				var parent = bones[bone].Parent;
				if ( parent >= 0 && !placed[parent] ) continue;

				order.Add( bone );
				placed[bone] = true;
				progress = true;
			}
		}

		//A cycle, or a parent that does not exist. Whatever is left is treated as a root rather than dropped: a bone
		//missing from the pose is a limb collapsed at the origin, which is worse than one that does not follow its parent.
		for ( var bone = 0; bone < bones.Length; ++bone )
		{
			if ( !placed[bone] ) order.Add( bone );
		}

		return order.ToArray();
	}

	/// <summary>Builds a skeleton from a compiled model (names, parents, inverse binds); null if no bones</summary>
	public static unsafe Skeleton CreateSkeleton( CoreAssets.ModelFile model )
	{
		var count = model?.BoneCount ?? 0;
		if ( count <= 0 ) return null;

		var skeleton = new Skeleton { Bones = new Skeleton.BoneEntry[count] };

		//Where each bone rests, in model space, from the inverse bind - which is the only thing a model file says about a bone's place.
		var bindPose = new AnimTrs[count];
		var matrix = stackalloc float[16];

		for ( var bone = 0; bone < count; ++bone )
		{
			model.BoneInverseBind( bone, matrix );

			bindPose[bone] = AnimTrs.Invert( AnimTrs.FromMatrix( new ReadOnlySpan<float>( matrix, 16 ) ) );

			var name = model.BoneName( bone );

			ref var entry = ref skeleton.Bones[bone];
			entry.Name = name;
			entry.Key = Lowered( name );
			entry.Parent = model.BoneParent( bone );

			if ( entry.Parent >= count || entry.Parent == bone ) entry.Parent = -1;

			skeleton.ByName.TryAdd( entry.Key, bone );
		}

		for ( var bone = 0; bone < count; ++bone )
		{
			var parent = skeleton.Bones[bone].Parent;

			skeleton.Bones[bone].Rest = parent >= 0
				? AnimTrs.Then( bindPose[bone], AnimTrs.Invert( bindPose[parent] ) )
				: bindPose[bone];
		}

		skeleton.Order = EvaluationOrder( skeleton.Bones );

		return skeleton;
	}

	//Which of the skeleton's bones each of the clip's bones is.
	private static int[] ClipBinding( Skeleton skeleton, AnimClipData clip )
	{
		if ( skeleton.ClipBindings.TryGetValue( clip, out var cached ) ) return cached;

		var binding = new int[clip.Bones.Length];

		for ( var bone = 0; bone < binding.Length; ++bone )
			binding[bone] = skeleton.Find( Lowered( clip.Bones[bone].Name ) );

		skeleton.ClipBindings.AddOrUpdate( clip, binding );
		return binding;
	}

	//Which of another skeleton's bones each of ours is.
	private static int[] SkeletonBinding( Skeleton skeleton, Skeleton other )
	{
		if ( skeleton.SkeletonBindings.TryGetValue( other, out var cached ) ) return cached;

		var binding = new int[skeleton.Bones.Length];

		for ( var bone = 0; bone < binding.Length; ++bone )
		{
			binding[bone] = other.ByName.TryGetValue( skeleton.Bones[bone].Key, out var match )
				? match
				: -1;
		}

		skeleton.SkeletonBindings.AddOrUpdate( other, binding );
		return binding;
	}

	//-- standing in one -----------------------------------------------------------

	internal sealed class Instance
	{
		public Skeleton Skeleton;

		public AnimTrs[] Local;

		/// <summary>Room for a model space blend, kept rather than made each time.</summary>
		public AnimTrs[] ModelScratch = Array.Empty<AnimTrs>();
		public AnimTrs[] LaidScratch = Array.Empty<AnimTrs>();
		public AnimTrs[] Model;
		public AnimTrs[] Previous;

		/// <summary>Counts every change to the model space pose, for what is worked out from it to know when to work it out again.</summary>
		public int Stamp;

		/// <summary>Bones driven from outside - physics, or a game object moving one by hand. They survive a pose being rebuilt, which is the point of them.</summary>
		public bool[] Overridden;
		public AnimTrs[] Overrides;
		public int OverrideCount;

		/// <summary>What is playing on its own, or null under a graph.</summary>
		public AnimClipData Clip;
		public float Time;
		public float Rate = 1.0f;
		public bool Looping;
		public bool Finished;

		public AnimTrs[] Sampled = Array.Empty<AnimTrs>();

		/// <summary>Room for a second reading of the clip - the root at some other time - kept rather than made each update.</summary>
		public AnimTrs[] RootScratch = Array.Empty<AnimTrs>();

		/// <summary>What the root bone did during the last update, in its own frame.</summary>
		public AnimTrs RootMotion = AnimTrs.Identity;
		public AnimTrs LastRoot = AnimTrs.Identity;
		public bool HasLastRoot;
		public int RootBone = -1;

		/// <summary>Somewhere for a graph to work: poses handed out by index, because they are only ever named by the thing that allocated them, an index is what a caller can hold cheaply, and a stale one lands inside the list rather than anywhere.</summary>
		public readonly List<AnimTrs[]> Poses = new();
		public readonly List<bool> PoseUsed = new();

		/// <summary>The same for bone masks, which a graph also blends.</summary>
		public readonly List<float[]> Masks = new();
		public readonly List<bool> MaskUsed = new();

		public AnimTrs[] Pose( int pose )
			=> pose >= 0 && pose < Poses.Count ? Poses[pose] : null;

		/// <summary>A negative index means "no mask", which is how a blend says it applies to every bone.</summary>
		public float[] Mask( int mask )
			=> mask >= 0 && mask < Masks.Count ? Masks[mask] : null;

		public int BoneCount => Local.Length;

		//The bounds checks every caller used to write for itself around a ten-float buffer. They are here so
		//that reading one bone is one call that cannot be got wrong, rather than a stackalloc and a stride.

		/// <summary>A bone of a working pose, or false where either is not there.</summary>
		public bool ReadBone( int pose, int bone, out AnimTrs trs )
		{
			var target = Pose( pose );

			if ( target is null || bone < 0 || bone >= target.Length )
			{
				trs = AnimTrs.Identity;
				return false;
			}

			trs = target[bone];
			return true;
		}

		public bool WriteBone( int pose, int bone, in AnimTrs trs )
		{
			var target = Pose( pose );
			if ( target is null || bone < 0 || bone >= target.Length ) return false;

			target[bone] = trs;
			return true;
		}

		/// <summary>A bone of a working pose in model space, composed on the spot.</summary>
		public bool BoneModel( int pose, int bone, out AnimTrs trs )
		{
			var target = Pose( pose );

			if ( target is null || bone < 0 || bone >= target.Length )
			{
				trs = AnimTrs.Identity;
				return false;
			}

			trs = ModelOf( target, Skeleton, bone );
			return true;
		}

		/// <summary>A bone of the pose being played, in its parent's space.</summary>
		public bool LocalBone( int bone, out AnimTrs trs )
		{
			if ( bone < 0 || bone >= Local.Length )
			{
				trs = AnimTrs.Identity;
				return false;
			}

			trs = Local[bone];
			return true;
		}

		/// <summary>A bone of the pose being played, in model space.</summary>
		public bool ModelBone( int bone, out AnimTrs trs )
		{
			if ( bone < 0 || bone >= Model.Length )
			{
				trs = AnimTrs.Identity;
				return false;
			}

			trs = Model[bone];
			return true;
		}
	}

	public static Instance CreateInstance( Skeleton skeleton )
	{
		if ( skeleton is null ) return null;

		var count = skeleton.Bones.Length;

		var instance = new Instance
		{
			Skeleton = skeleton,
			Local = new AnimTrs[count],
			Model = new AnimTrs[count],
			Previous = new AnimTrs[count],
			Overridden = new bool[count],
			Overrides = new AnimTrs[count],
		};

		for ( var bone = 0; bone < count; ++bone )
			instance.Local[bone] = skeleton.Bones[bone].Rest;

		Compose( instance );

		//The previous pose starts as this one, so nothing has moved on the first frame - a bone
		//velocity taken from an empty previous pose is the whole skeleton flying in from the origin.
		instance.Model.CopyTo( instance.Previous, 0 );

		return instance;
	}

	//Local pose to model space, parents first.
	private static void Compose( Instance instance )
	{
		var skeleton = instance.Skeleton;

		var swap = instance.Previous;
		instance.Model.CopyTo( swap, 0 );
		instance.Previous = swap;
		++instance.Stamp;

		foreach ( var bone in skeleton.Order )
		{
			var parent = skeleton.Bones[bone].Parent;

			instance.Model[bone] = parent >= 0
				? AnimTrs.Then( instance.Local[bone], instance.Model[parent] )
				: instance.Local[bone];
		}
	}

	private static void ApplyOverrides( Instance instance )
	{
		if ( instance.OverrideCount == 0 ) return;

		for ( var bone = 0; bone < instance.Local.Length; ++bone )
		{
			if ( instance.Overridden[bone] ) instance.Local[bone] = instance.Overrides[bone];
		}
	}

	//The clip's pose at a time, written over the rest pose.
	private static void SampleInto( Instance instance, float time )
	{
		var clip = instance.Clip;
		if ( clip is null || clip.Bones.Length == 0 ) return;

		if ( instance.Sampled.Length < clip.Bones.Length )
			instance.Sampled = new AnimTrs[clip.Bones.Length];

		clip.Sample( time, instance.Looping, instance.Sampled );

		var binding = ClipBinding( instance.Skeleton, clip );

		for ( var bone = 0; bone < clip.Bones.Length; ++bone )
		{
			var target = binding[bone];
			if ( target < 0 ) continue;

			instance.Local[target] = instance.Sampled[bone];
		}
	}

	//The root bone's local transform at a time, without disturbing the pose.
	private static AnimTrs RootAt( Instance instance, float time )
	{
		if ( instance.RootBone < 0 ) return AnimTrs.Identity;

		var clip = instance.Clip;
		if ( clip is null || clip.Bones.Length == 0 ) return AnimTrs.Identity;

		if ( instance.RootScratch.Length < clip.Bones.Length )
			instance.RootScratch = new AnimTrs[clip.Bones.Length];

		var scratch = instance.RootScratch;
		clip.Sample( time, false, scratch );

		var binding = ClipBinding( instance.Skeleton, clip );

		for ( var bone = 0; bone < clip.Bones.Length; ++bone )
		{
			if ( binding[bone] != instance.RootBone ) continue;

			return scratch[bone];
		}

		return AnimTrs.Identity;
	}

	/// <summary>Play a clip from its beginning, or nothing at all. Looping left unsaid is whatever the clip was authored as.</summary>
	public static void SetClip( Instance instance, AnimClipData clip, bool? looping = null )
	{
		instance.Clip = clip;
		instance.Time = 0.0f;
		instance.Finished = false;
		instance.HasLastRoot = false;

		instance.Looping = looping ?? clip?.Looping ?? false;

		//Which of our bones the clip calls its root, so root motion has something to be about.
		instance.RootBone = -1;

		if ( clip is null ) return;

		var binding = ClipBinding( instance.Skeleton, clip );

		for ( var bone = 0; bone < binding.Length; ++bone )
		{
			if ( binding[bone] < 0 ) continue;
			if ( clip.Bones[bone].Parent >= 0 ) continue;

			instance.RootBone = binding[bone];
			break;
		}
	}

	/// <summary>Put the playhead somewhere. Whether it had finished and where the root was last frame are both about where it was, so they go with it.</summary>
	public static void SetTime( Instance instance, float seconds )
	{
		instance.Time = seconds;
		instance.Finished = false;
		instance.HasLastRoot = false;
	}

	public static void Update( Instance instance, float delta )
	{
		var skeleton = instance.Skeleton;
		var clip = instance.Clip;

		var wrapped = false;

		if ( clip is not null )
		{
			var duration = clip.Duration;

			instance.Time += delta * instance.Rate;

			if ( duration > 0.0f )
			{
				if ( instance.Looping )
				{
					while ( instance.Time > duration ) { instance.Time -= duration; wrapped = true; }
					while ( instance.Time < 0.0f ) { instance.Time += duration; wrapped = true; }
				}
				else if ( instance.Time >= duration )
				{
					instance.Time = duration;
					instance.Finished = true;
				}
				else if ( instance.Time <= 0.0f )
				{
					instance.Time = 0.0f;
					instance.Finished = instance.Rate < 0.0f;
				}
			}
			else
			{
				instance.Time = 0.0f;
				instance.Finished = true;
			}
		}

		for ( var bone = 0; bone < instance.Local.Length; ++bone )
			instance.Local[bone] = skeleton.Bones[bone].Rest;

		SampleInto( instance, instance.Time );

		//What the root did, before anything overrides it. A wrapped loop is two pieces - up to the end and on
		//from the beginning - because the straight difference would be a leap backwards across the whole clip.
		if ( instance.RootBone >= 0 && clip is not null )
		{
			var root = instance.Local[instance.RootBone];

			if ( !instance.HasLastRoot )
			{
				instance.RootMotion = AnimTrs.Identity;
			}
			else if ( !wrapped )
			{
				instance.RootMotion = AnimTrs.Then( AnimTrs.Invert( instance.LastRoot ), root );
			}
			else
			{
				var end = RootAt( instance, clip.Duration );
				var start = RootAt( instance, 0.0f );

				instance.RootMotion = AnimTrs.Then(
					AnimTrs.Then( AnimTrs.Invert( instance.LastRoot ), end ),
					AnimTrs.Then( AnimTrs.Invert( start ), root ) );
			}

			instance.LastRoot = root;
			instance.HasLastRoot = true;
		}
		else
		{
			instance.RootMotion = AnimTrs.Identity;
		}

		ApplyOverrides( instance );
		Compose( instance );
	}

	public static void BindPose( Instance instance )
	{
		var skeleton = instance.Skeleton;

		for ( var bone = 0; bone < instance.Local.Length; ++bone )
			instance.Local[bone] = skeleton.Bones[bone].Rest;

		instance.RootMotion = AnimTrs.Identity;
		instance.HasLastRoot = false;

		ApplyOverrides( instance );
		Compose( instance );
	}

	public static void SetBone( Instance instance, int bone, in AnimTrs trs )
	{
		if ( bone < 0 || bone >= instance.Local.Length ) return;

		if ( !instance.Overridden[bone] )
		{
			instance.Overridden[bone] = true;
			++instance.OverrideCount;
		}

		instance.Overrides[bone] = trs;
		instance.Local[bone] = trs;

		//Refresh this bone's model pose now, so a child override set later this frame reads the new parent
		var parent = instance.Skeleton.Bones[bone].Parent;
		instance.Model[bone] = parent >= 0 ? AnimTrs.Then( trs, instance.Model[parent] ) : trs;
		++instance.Stamp;
	}

	public static void ClearBones( Instance instance )
	{
		Array.Clear( instance.Overridden );
		instance.OverrideCount = 0;
	}

	public static void ComposeNow( Instance instance ) => Compose( instance );

	//Where a bone rests in its model's own space, composed up its parents. A skeleton keeps each bone's rest relative to whatever it hangs off, so
	//the model-space one is walked out here. Only the merge asks, and only for the few bones that had no counterpart, so it is not worth keeping.
	private static AnimTrs RestModel( Skeleton skeleton, int bone )
	{
		var rest = skeleton.Bones[bone].Rest;

		for ( var at = skeleton.Bones[bone].Parent; at >= 0; at = skeleton.Bones[at].Parent )
			rest = AnimTrs.Then( rest, skeleton.Bones[at].Rest );

		return rest;
	}

	public static int Merge( Instance mine, Instance theirs )
	{
		if ( ReferenceEquals( mine, theirs ) ) return 0;

		var skeleton = mine.Skeleton;
		var binding = SkeletonBinding( skeleton, theirs.Skeleton );

		mine.Model.CopyTo( mine.Previous, 0 );
		++mine.Stamp;

		var merged = 0;

		//Where the merge moved the first bone the two rigs share, as a rigid step from where that bone rests. See below for what it carries.
		var carried = AnimTrs.Identity;
		var carries = false;

		foreach ( var bone in skeleton.Order )
		{
			var match = binding[bone];
			if ( match < 0 ) continue;

			var parent = skeleton.Bones[bone].Parent;

			//Taken in model space rather than local: the two rigs need not hang the same way,
			//and what has to agree is where the bone ends up, not what it is relative to.
			mine.Model[bone] = theirs.Model[match];

			mine.Local[bone] = parent >= 0
				? AnimTrs.Then( mine.Model[bone], AnimTrs.Invert( mine.Model[parent] ) )
				: mine.Model[bone];

			if ( !carries )
			{
				carried = AnimTrs.Then( AnimTrs.Invert( RestModel( skeleton, bone ) ), mine.Model[bone] );
				carries = true;
			}

			++merged;
		}

		//The rest, in the same order, so a bone is placed after whatever it hangs off - which may be one of the bones just merged or one filled in here.
		foreach ( var bone in skeleton.Order )
		{
			if ( binding[bone] >= 0 ) continue;

			var parent = skeleton.Bones[bone].Parent;

			if ( parent >= 0 )
			{
				//A bone the other rig does not have keeps its own pose, carried by whichever of its parents did match.
				mine.Model[bone] = AnimTrs.Then( mine.Local[bone], mine.Model[parent] );
				continue;
			}

			//Unmatched roots follow the shared bones' rigid step, so the rig keeps its shape instead of dropping
			mine.Model[bone] = carries
				? AnimTrs.Then( RestModel( skeleton, bone ), carried )
				: mine.Local[bone];
		}

		return merged;
	}

	//-- somewhere for a graph to work ---------------------------------------------

	public static int PoseAlloc( Instance instance )
	{
		for ( var at = 0; at < instance.PoseUsed.Count; ++at )
		{
			if ( instance.PoseUsed[at] ) continue;

			instance.PoseUsed[at] = true;
			return at;
		}

		instance.Poses.Add( new AnimTrs[instance.Skeleton.Bones.Length] );
		instance.PoseUsed.Add( true );

		return instance.Poses.Count - 1;
	}

	public static void PoseFree( Instance instance, int pose )
	{
		//Kept rather than shrunk: a graph allocates the same poses every frame, and the memory is the point of holding them.
		if ( pose >= 0 && pose < instance.PoseUsed.Count ) instance.PoseUsed[pose] = false;
	}

	public static void PoseRest( Instance instance, int pose )
	{
		var target = instance.Pose( pose );
		if ( target is null ) return;

		var bones = instance.Skeleton.Bones;

		for ( var bone = 0; bone < target.Length; ++bone ) target[bone] = bones[bone].Rest;
	}

	public static void PoseZero( Instance instance, int pose )
	{
		var target = instance.Pose( pose );
		if ( target is null ) return;

		//Not the rest pose: nothing at all, which is what an additive difference is measured from.
		for ( var bone = 0; bone < target.Length; ++bone ) target[bone] = AnimTrs.Identity;
	}

	public static int PoseSample( Instance instance, int pose, AnimClipData clip, float time, bool loop )
	{
		var target = instance.Pose( pose );

		if ( target is null || clip is null ) return 0;

		//Bones a clip omits default to rest pose, or to nothing for additive (difference) clips
		var bones = instance.Skeleton.Bones;

		for ( var bone = 0; bone < target.Length; ++bone )
			target[bone] = clip.Additive ? AnimTrs.Identity : bones[bone].Rest;

		if ( clip.Bones.Length == 0 ) return 0;

		if ( instance.Sampled.Length < clip.Bones.Length )
			instance.Sampled = new AnimTrs[clip.Bones.Length];

		clip.Sample( time, loop, instance.Sampled );

		var binding = ClipBinding( instance.Skeleton, clip );

		var written = 0;

		for ( var bone = 0; bone < clip.Bones.Length; ++bone )
		{
			var at = binding[bone];
			if ( at < 0 ) continue;

			//Per channel: unauthored channels keep the model's rest, so bone lengths come from the model
			var sampled = instance.Sampled[bone];
			var rest = target[at];
			var track = clip.Tracks[bone];

			target[at] = new AnimTrs
			{
				Position = track.Translation.Count > 0 ? sampled.Position : rest.Position,
				Rotation = track.Rotation.Count > 0 ? sampled.Rotation : rest.Rotation,
				Scale = track.Scale.Count > 0 ? sampled.Scale : rest.Scale,
			};

			++written;
		}

		return written;
	}

	public static void PoseCopy( Instance instance, int destination, int source )
	{
		var to = instance.Pose( destination );
		var from = instance.Pose( source );

		if ( to is null || from is null || ReferenceEquals( to, from ) ) return;

		from.CopyTo( to, 0 );
	}

	public static void PoseBlend( Instance instance, int destination, int a, int b, float weight, int mask )
	{
		var to = instance.Pose( destination );
		var from = instance.Pose( a );
		var towards = instance.Pose( b );

		if ( to is null || from is null || towards is null ) return;

		var weights = instance.Mask( mask );

		for ( var bone = 0; bone < to.Length; ++bone )
		{
			var amount = weight;
			if ( weights is not null ) amount *= weights[bone];

			to[bone] = AnimTrs.Blend( from[bone], towards[bone], amount );
		}
	}

	/// <summary>Blends rotations and leaves everything else where the base had it.</summary>
	public static void PoseBlendRotation( Instance instance, int destination, int a, int b,
		float weight, int mask )
	{
		var to = instance.Pose( destination );
		var from = instance.Pose( a );
		var towards = instance.Pose( b );

		if ( to is null || from is null || towards is null ) return;

		var weights = instance.Mask( mask );

		for ( var bone = 0; bone < to.Length; ++bone )
		{
			var amount = weight;
			if ( weights is not null ) amount *= weights[bone];

			var blended = AnimTrs.Blend( from[bone], towards[bone], amount );

			to[bone].Rotation = blended.Rotation;
			to[bone].Position = from[bone].Position;
			to[bone].Scale = from[bone].Scale;
		}
	}

	/// <summary>Two poses blended where the bones stand rather than where their parents put them.</summary>
	public static void PoseBlendModel( Instance instance, int destination, int a, int b,
		float weight, int mask, bool rotationOnly )
	{
		var to = instance.Pose( destination );
		var from = instance.Pose( a );
		var towards = instance.Pose( b );

		if ( to is null || from is null || towards is null ) return;

		var skeleton = instance.Skeleton;
		var bones = skeleton.Bones;
		var weights = instance.Mask( mask );

		if ( instance.ModelScratch.Length < to.Length ) instance.ModelScratch = new AnimTrs[to.Length];
		if ( instance.LaidScratch.Length < to.Length ) instance.LaidScratch = new AnimTrs[to.Length];

		var model = instance.ModelScratch;
		var other = instance.LaidScratch;

		//Down the hierarchy, both of them: where each bone stands now and where the pose coming over it would have it stand.
		foreach ( var bone in skeleton.Order )
		{
			if ( bone >= to.Length ) continue;

			var parent = bones[bone].Parent;

			model[bone] = parent >= 0 ? AnimTrs.Then( from[bone], model[parent] ) : from[bone];
			other[bone] = parent >= 0 ? AnimTrs.Then( towards[bone], other[parent] ) : towards[bone];
		}

		//Blended there, each bone on its own - which is the whole point, since a bone's share no longer carries down to its children.
		for ( var bone = 0; bone < to.Length; ++bone )
		{
			var amount = weight;
			if ( weights is not null ) amount *= weights[bone];

			var blended = AnimTrs.Blend( model[bone], other[bone], amount );

			model[bone] = rotationOnly
				? model[bone] with { Rotation = blended.Rotation }
				: blended;
		}

		//And back out of it. Rotation only keeps each bone where it sat under its parent and takes only the turn, so nothing stretches.
		foreach ( var bone in skeleton.Order )
		{
			if ( bone >= to.Length ) continue;

			var parent = bones[bone].Parent;

			if ( rotationOnly )
			{
				var above = parent >= 0 ? model[parent].Rotation : Quaternion.Identity;

				var local = from[bone];

				local.Rotation = AnimTrs.NormaliseQuat(
					Quaternion.Inverse( AnimTrs.NormaliseQuat( above ) ) * AnimTrs.NormaliseQuat( model[bone].Rotation ) );

				to[bone] = local;
				continue;
			}

			to[bone] = parent >= 0
				? AnimTrs.Then( model[bone], AnimTrs.Invert( model[parent] ) )
				: model[bone];
		}
	}

	public static void PoseAdditive( Instance instance, int destination, int basePose, int additive,
		float weight, int mask, bool separately = false )
	{
		var to = instance.Pose( destination );
		var under = instance.Pose( basePose );
		var over = instance.Pose( additive );

		if ( to is null || under is null || over is null ) return;

		var weights = instance.Mask( mask );

		for ( var bone = 0; bone < to.Length; ++bone )
		{
			var amount = weight;
			if ( weights is not null ) amount *= weights[bone];

			//The difference applied on top of what is under it, then blended in by weight - so a weight of zero is the base pose exactly.
			var applied = separately
				? AnimTrs.Beside( under[bone], over[bone] )
				: AnimTrs.Then( over[bone], under[bone] );

			to[bone] = AnimTrs.Blend( under[bone], applied, amount );
		}
	}

	/// <summary>Adds a model-space difference (deltams clips): compose, apply, then convert back to parent space</summary>
	public static void PoseAdditiveModel( Instance instance, int destination, int basePose,
		int additive, float weight, int mask )
	{
		var to = instance.Pose( destination );
		var under = instance.Pose( basePose );
		var over = instance.Pose( additive );

		if ( to is null || under is null || over is null ) return;

		var skeleton = instance.Skeleton;
		var bones = skeleton.Bones;
		var weights = instance.Mask( mask );

		if ( instance.ModelScratch.Length < to.Length ) instance.ModelScratch = new AnimTrs[to.Length];
		if ( instance.LaidScratch.Length < to.Length ) instance.LaidScratch = new AnimTrs[to.Length];

		var model = instance.ModelScratch;
		var laid = instance.LaidScratch;

		//Down the hierarchy: where each bone stands, and where the difference asks it to stand.
		foreach ( var bone in skeleton.Order )
		{
			if ( bone >= to.Length ) continue;

			var parent = bones[bone].Parent;

			model[bone] = parent >= 0 ? AnimTrs.Then( under[bone], model[parent] ) : under[bone];

			var amount = weight;
			if ( weights is not null ) amount *= weights[bone];

			//Apply the model-space difference after the bone's current rotation, not before
			AnimTrs wanted;

			wanted.Position = model[bone].Position + over[bone].Position;
			wanted.Rotation = model[bone].Rotation * over[bone].Rotation;
			wanted.Scale = model[bone].Scale * over[bone].Scale;

			laid[bone] = AnimTrs.Blend( model[bone], wanted, amount );
		}

		//And back out of it, each bone read against where its parent ended up.
		foreach ( var bone in skeleton.Order )
		{
			if ( bone >= to.Length ) continue;

			var parent = bones[bone].Parent;

			to[bone] = parent >= 0
				? AnimTrs.Then( laid[bone], AnimTrs.Invert( laid[parent] ) )
				: laid[bone];
		}
	}

	public static void PoseApply( Instance instance, int pose )
	{
		var from = instance.Pose( pose );
		if ( from is null ) return;

		from.CopyTo( instance.Local, 0 );

		ApplyOverrides( instance );
		Compose( instance );
	}

	//Where one bone of a pose stands in the model's own space. Walked up from the bone rather than composed for the whole skeleton: the
	//callers of this want three bones of a limb, and composing everything to find three is most of a pose's cost for none of its use.
	internal static AnimTrs ModelOf( AnimTrs[] local, Skeleton skeleton, int bone )
	{
		if ( bone < 0 || bone >= local.Length ) return AnimTrs.Identity;

		var result = local[bone];

		for ( var at = skeleton.Bones[bone].Parent; at >= 0; at = skeleton.Bones[at].Parent )
			result = AnimTrs.Then( result, local[at] );

		return result;
	}

	//Clamps a direction to yaw and pitch limits separately, relative to the model's forward
	private static Vector3 Bounded( Vector3 direction, float yawLimit, float pitchLimit )
	{
		if ( yawLimit >= 180.0f && pitchLimit >= 180.0f ) return direction;

		//Heading is measured about up, which is y, from the way the model faces, which is -z; the dip is what is left over on y.
		var yaw = MathF.Atan2( -direction.x, -direction.z );
		var pitch = MathF.Atan2( direction.y, new Vector2( direction.x, direction.z ).Length );

		var mostYaw = Math.Clamp( yawLimit, 0.0f, 180.0f ) * MathF.PI / 180.0f;
		var mostPitch = Math.Clamp( pitchLimit, 0.0f, 90.0f ) * MathF.PI / 180.0f;

		yaw = Math.Clamp( yaw, -mostYaw, mostYaw );
		pitch = Math.Clamp( pitch, -mostPitch, mostPitch );

		var flat = MathF.Cos( pitch );

		return new Vector3( -flat * MathF.Sin( yaw ), MathF.Sin( pitch ), -flat * MathF.Cos( yaw ) );
	}

	/// <summary>Turn a run of bones so that something on the model points where it is asked to.</summary>
	public static bool PoseLookAt( Instance instance, int pose, ReadOnlySpan<int> bones,
		ReadOnlySpan<float> weights, Vector3 direction, float weight, float limit,
		float yawLimit = 180.0f, float pitchLimit = 180.0f,
		int aimBone = -1, Vector3 aimAxis = default, bool aimInModel = false )
	{
		if ( weight <= 0.0f || bones.Length == 0 ) return false;

		var local = instance.Pose( pose );
		if ( local is null ) return false;

		var skeleton = instance.Skeleton;

		if ( direction.Length < 1e-6f ) return false;

		var wanted = Bounded( AnimTrs.NormaliseVec( direction ), yawLimit, pitchLimit );

		//Where the thing that does the pointing is pointing already. Which bone carries it and which way it faces on that bone is the model's answer,
		//not the chain's: a citizen's head bone runs up the skull, so aiming its own forward at the target lays the character's face on the floor.
		var along = aimAxis.Length > 1e-6f ? AnimTrs.NormaliseVec( aimAxis ) : Vector3.Forward;

		Vector3 facing;

		if ( aimInModel )
		{
			//Written against the model, so nothing turns it: the chain is measured from where
			//the body faces rather than from where the last animation left the bone it hangs off.
			facing = along;
		}
		else
		{
			var aim = aimBone >= 0 ? aimBone : bones[0];
			if ( aim < 0 || aim >= local.Length ) return false;

			facing = (Rotation)AnimTrs.NormaliseQuat( ModelOf( local, skeleton, aim ).Rotation ) * along;
		}

		if ( facing.Length < 1e-6f ) return false;

		var turn = AnimTrs.Between( AnimTrs.NormaliseVec( facing ), wanted );

		//Further round than it is allowed to go: turn as far as it may and stop, which is what a neck does.
		if ( limit > 0.0f && limit < 180.0f )
		{
			var angle = 2.0f * MathF.Acos( Math.Clamp( MathF.Abs( turn.W ), -1.0f, 1.0f ) );
			var most = limit * MathF.PI / 180.0f;

			if ( angle > most && angle > 1e-6f )
				turn = Quaternion.Slerp( Quaternion.Identity, turn, most / angle );
		}

		if ( weight > 1.0f ) weight = 1.0f;

		//Where each bone stood before any of them turned, so a weight reads as its share of the whole turn rather
		//than of what its parents left: a spine written 1, 0.7, 0.45, 0.2 compounds to 2.35 turns otherwise.
		Span<Quaternion> stood = bones.Length <= 16
			? stackalloc Quaternion[bones.Length]
			: new Quaternion[bones.Length];

		for ( var i = 0; i < bones.Length; i++ )
		{
			var at = bones[i];

			stood[i] = at >= 0 && at < local.Length
				? AnimTrs.NormaliseQuat( ModelOf( local, skeleton, at ).Rotation )
				: Quaternion.Identity;
		}

		//From the far end back, so a bone's parents are already where they are going by the time it is placed against them.
		for ( var i = bones.Length - 1; i >= 0; i-- )
		{
			var bone = bones[i];
			if ( bone < 0 || bone >= local.Length ) continue;

			var share = ( i < weights.Length ? weights[i] : 1.0f ) * weight;
			if ( share <= 0.0f ) continue;

			var part = Quaternion.Slerp( Quaternion.Identity, turn, Math.Clamp( share, 0.0f, 1.0f ) );

			var model = stood[i];
			var parent = skeleton.Bones[bone].Parent;

			var above = parent >= 0 ? ModelOf( local, skeleton, parent ).Rotation : Quaternion.Identity;

			//The bone turns where it stands, so only its rotation changes - which is also why its own length and its children's places are left alone.
			var turned = AnimTrs.NormaliseQuat( part * model );

			local[bone].Rotation = AnimTrs.NormaliseQuat(
				Quaternion.Inverse( AnimTrs.NormaliseQuat( above ) ) * turned );
		}

		return true;
	}

	public static bool PoseTwoBoneIk( Instance instance, int pose, int bone, in AnimTrs target,
		float weight, float twist )
	{
		var skeleton = instance.Skeleton;
		var local = instance.Pose( pose );

		if ( local is null ) return false;

		//Three bones make a limb: the one that reaches, the one that bends, and the one
		//that arrives. Walked up from the end, which is the limb on nearly every skeleton.
		if ( bone < 0 || bone >= local.Length ) return false;

		var middle = skeleton.Bones[bone].Parent;
		if ( middle < 0 ) return false;

		var upper = skeleton.Bones[middle].Parent;
		if ( upper < 0 ) return false;

		return PoseTwoBoneIk( instance, pose, upper, middle, bone, target, weight, twist, false, default );
	}

	/// <summary>Two-bone IK on a named limb; joints needn't be adjacent, bones between keep their pose</summary>
	public static bool PoseTwoBoneIk( Instance instance, int pose, int upper, int middle, int bone,
		in AnimTrs target, float weight, float twist, bool useKnee, Vector3 knee )
	{
		var skeleton = instance.Skeleton;
		var local = instance.Pose( pose );

		if ( local is null ) return false;

		if ( weight <= 0.0f ) return false;
		if ( weight > 1.0f ) weight = 1.0f;

		if ( upper < 0 || middle < 0 || bone < 0 ) return false;
		if ( upper >= local.Length || middle >= local.Length || bone >= local.Length ) return false;

		var upperWas = ModelOf( local, skeleton, upper );
		var middleWas = ModelOf( local, skeleton, middle );
		var endWas = ModelOf( local, skeleton, bone );

		var a = upperWas.Position;
		var b = middleWas.Position;
		var c = endWas.Position;

		var upperLength = ( b - a ).Length;
		var lowerLength = ( c - b ).Length;

		if ( upperLength < 1e-6f || lowerLength < 1e-6f ) return false;

		var toTarget = target.Position - a;
		if ( toTarget.Length < 1e-6f ) return false;

		var direction = AnimTrs.NormaliseVec( toTarget );

		//A target further away than the limb is long is reached towards rather than snapped to: the limb
		//straightens and points at it. Nearer than it can fold is the same thing the other way round.
		var longest = ( upperLength + lowerLength ) * 0.999f;
		var shortest = ( MathF.Abs( upperLength - lowerLength ) * 1.001f ) + 1e-5f;

		//Degenerate limb with no solvable triangle: fail instead of throwing inside the graph update
		if ( shortest > longest ) return false;

		var distance = Math.Clamp( toTarget.Length, shortest, longest );

		//Which way the joint bends. The model's own answer where its chain gave one - a knee is forward whatever the animation
		//was doing - and otherwise kept from where it already bends, because an IK that decides for itself is an elbow that flips.
		var bend = useKnee && knee.Length > 1e-5f ? knee : b - a;

		var pole = bend - ( direction * Vector3.Dot( bend, direction ) );

		//A limb that is already straight has no bend to keep, and any direction square to it
		//will do - one derived from the limb itself at least gives the same answer every frame.
		pole = pole.Length < 1e-5f ? AnimTrs.Perpendicular( direction ) : AnimTrs.NormaliseVec( pole );

		//The triangle the limb makes to reach: how far round the upper bone has to swing from the straight line to the target.
		var cosine = Math.Clamp(
			( ( upperLength * upperLength ) + ( distance * distance ) - ( lowerLength * lowerLength ) ) /
			( 2.0f * upperLength * distance ), -1.0f, 1.0f );

		var angle = MathF.Acos( cosine );

		var wantedMiddle = a +
			( direction * ( MathF.Cos( angle ) * upperLength ) ) +
			( pole * ( MathF.Sin( angle ) * upperLength ) );

		var wantedEnd = a + ( direction * distance );

		//Bones are moved by turning the one above them, which is what keeps whatever hangs off them coming along.
		var swingUpper = AnimTrs.Between( b - a, wantedMiddle - a );
		var swingLower = AnimTrs.Between( c - b, wantedEnd - wantedMiddle );

		var upperOut = upperWas;
		upperOut.Rotation = swingUpper * upperWas.Rotation;

		var middleOut = middleWas;
		middleOut.Position = wantedMiddle;
		middleOut.Rotation = swingLower * ( swingUpper * middleWas.Rotation );

		var endOut = endWas;
		endOut.Position = wantedEnd;
		endOut.Rotation = swingLower * ( swingUpper * endWas.Rotation );

		//The end takes the target's own turn - a hand lands facing where the target faces, not where the arm happened to leave it.
		var wanted = AnimTrs.Blend( endOut, target, 1.0f ).Rotation;

		//And part of that roll is spread back into the bone above, which is what stops a wrist turning while the forearm stays where it was.
		if ( twist > 0.0f )
		{
			var along = AnimTrs.NormaliseVec( wantedEnd - wantedMiddle );
			var roll = AnimTrs.TwistAngle( wanted * Quaternion.Conjugate( endOut.Rotation ), along );

			var spread = AnimTrs.FromAxisAngle( along, roll * Math.Clamp( twist, 0.0f, 1.0f ) );

			middleOut.Rotation = spread * middleOut.Rotation;
		}

		endOut.Rotation = wanted;

		//Blended in rather than snapped to, so an IK that switches on does not arrive in a single frame.
		upperOut = AnimTrs.Blend( upperWas, upperOut, weight );
		middleOut = AnimTrs.Blend( middleWas, middleOut, weight );
		endOut = AnimTrs.Blend( endWas, endOut, weight );

		//Back into each bone's own space, which is what a pose holds. Down the limb in order, so a bone whose parent is one of the two
		//above it - or a twist bone hanging off one - is measured against where that parent has just been put rather than where it started.
		local[upper] = AnimTrs.Then( upperOut, AnimTrs.Invert( ParentOf( local, skeleton, upper ) ) );
		local[middle] = AnimTrs.Then( middleOut, AnimTrs.Invert( ParentOf( local, skeleton, middle ) ) );
		local[bone] = AnimTrs.Then( endOut, AnimTrs.Invert( ParentOf( local, skeleton, bone ) ) );

		return true;
	}

	/// <summary>IK for a chain of any length: two segments use the exact solve, longer chains use FABRIK</summary>
	public static bool PoseIkChain( Instance instance, int pose, int[] bones, in AnimTrs target,
		float weight, float twist, bool useKnee, Vector3 knee )
	{
		if ( bones is not { Length: >= 2 } ) return false;

		if ( bones.Length == 3 )
		{
			return PoseTwoBoneIk( instance, pose, bones[0], bones[1], bones[2],
				target, weight, twist, useKnee, knee );
		}

		var skeleton = instance.Skeleton;
		var local = instance.Pose( pose );

		if ( local is null ) return false;

		if ( weight <= 0.0f ) return false;
		if ( weight > 1.0f ) weight = 1.0f;

		var count = bones.Length;

		foreach ( var bone in bones )
		{
			if ( bone < 0 || bone >= local.Length ) return false;
		}

		var was = new AnimTrs[count];
		var from = new Vector3[count];

		for ( var i = 0; i < count; i++ )
		{
			was[i] = ModelOf( local, skeleton, bones[i] );
			from[i] = was[i].Position;
		}

		var lengths = new float[count - 1];
		var reach = 0.0f;

		for ( var i = 0; i < count - 1; i++ )
		{
			lengths[i] = ( from[i + 1] - from[i] ).Length;

			//A segment of no length has no direction to turn, and the whole run would divide by it.
			if ( lengths[i] < 1e-6f ) return false;

			reach += lengths[i];
		}

		var to = Fabrik( from, lengths, reach, target.Position, useKnee, knee );
		if ( to is null ) return false;

		//Each joint swings from where it pointed to where it now points, and carries
		//everything below it round, the same way the two bone solve composes its two swings.
		var out_ = new AnimTrs[count];
		var carried = Quaternion.Identity;

		for ( var i = 0; i < count - 1; i++ )
		{
			carried = AnimTrs.Between( from[i + 1] - from[i], to[i + 1] - to[i] ) * carried;

			out_[i] = was[i];
			out_[i].Position = to[i];
			out_[i].Rotation = carried * was[i].Rotation;
		}

		out_[count - 1] = was[count - 1];
		out_[count - 1].Position = to[count - 1];
		out_[count - 1].Rotation = carried * was[count - 1].Rotation;

		//The tip takes the target's own turn - a hand lands facing where the target faces, not where the limb happened to leave it.
		var wanted = AnimTrs.Blend( out_[count - 1], target, 1.0f ).Rotation;

		if ( twist > 0.0f )
		{
			var along = AnimTrs.NormaliseVec( to[count - 1] - to[count - 2] );
			var roll = AnimTrs.TwistAngle( wanted * Quaternion.Conjugate( out_[count - 1].Rotation ), along );

			out_[count - 2].Rotation =
				AnimTrs.FromAxisAngle( along, roll * Math.Clamp( twist, 0.0f, 1.0f ) ) * out_[count - 2].Rotation;
		}

		out_[count - 1].Rotation = wanted;

		for ( var i = 0; i < count; i++ ) out_[i] = AnimTrs.Blend( was[i], out_[i], weight );

		//Back into each bone's own space, down the run in order, so a bone is measured against where its parent has just been put.
		for ( var i = 0; i < count; i++ )
			local[bones[i]] = AnimTrs.Then( out_[i], AnimTrs.Invert( ParentOf( local, skeleton, bones[i] ) ) );

		return true;
	}

	//FABRIK onto a target keeping segment lengths; null when there's nothing to solve
	private static Vector3[] Fabrik( Vector3[] from, float[] lengths, float reach,
		Vector3 target, bool useKnee, Vector3 knee )
	{
		var count = from.Length;
		var root = from[0];

		var toTarget = target - root;
		if ( toTarget.Length < 1e-6f ) return null;

		var to = (Vector3[])from.Clone();

		//Further away than the run is long is reached towards rather than snapped to: it straightens and points at the target.
		if ( toTarget.Length >= reach * 0.999f )
		{
			var straight = AnimTrs.NormaliseVec( toTarget );

			for ( var i = 1; i < count; i++ ) to[i] = to[i - 1] + ( straight * lengths[i - 1] );

			return to;
		}

		//Which way the run is meant to fold. Kept flat in that plane, because a chain free to bend anywhere picks somewhere different every frame.
		var direction = AnimTrs.NormaliseVec( toTarget );
		var pole = Vector3.Zero;

		if ( useKnee && knee.Length > 1e-5f )
		{
			pole = knee - ( direction * Vector3.Dot( knee, direction ) );
			pole = pole.Length < 1e-5f ? Vector3.Zero : AnimTrs.NormaliseVec( pole );
		}

		//Told nothing, it folds the way it is already bent - and a run lying dead along the line to
		//the target is not bent at all, so any side will do as long as it is the same one every frame.
		var hint = pole;

		if ( hint == Vector3.Zero )
		{
			for ( var i = 1; i < count - 1; i++ )
			{
				var offset = from[i] - root;
				hint += offset - ( direction * Vector3.Dot( offset, direction ) );
			}

			hint = hint.Length < 1e-5f ? AnimTrs.Perpendicular( direction ) : AnimTrs.NormaliseVec( hint );
		}

		//Nudged off the line before the first pass: reaching moves joints along the run,
		//never across it, so one that starts straight has nothing to fold and stays that way.
		for ( var i = 1; i < count - 1; i++ ) to[i] += hint * ( reach * 0.01f );

		for ( var pass = 0; pass < 12; pass++ )
		{
			to[count - 1] = target;

			for ( var i = count - 2; i >= 0; i-- )
				to[i] = to[i + 1] + ( AnimTrs.NormaliseVec( to[i] - to[i + 1] ) * lengths[i] );

			to[0] = root;

			for ( var i = 1; i < count; i++ )
				to[i] = to[i - 1] + ( AnimTrs.NormaliseVec( to[i] - to[i - 1] ) * lengths[i - 1] );

			if ( pole != Vector3.Zero )
			{
				for ( var i = 1; i < count - 1; i++ )
				{
					var offset = to[i] - root;

					to[i] = root
						+ ( direction * Vector3.Dot( offset, direction ) )
						+ ( pole * MathF.Max( Vector3.Dot( offset, pole ), 0.0f ) );
				}
			}

			if ( ( to[count - 1] - target ).Length < 1e-4f ) break;
		}

		return to;
	}

	//Where a bone's parent is in the model, as the pose stands now.
	private static AnimTrs ParentOf( AnimTrs[] local, Skeleton skeleton, int bone )
	{
		var parent = skeleton.Bones[bone].Parent;
		return parent >= 0 ? ModelOf( local, skeleton, parent ) : AnimTrs.Identity;
	}

	//-- bone masks ------------------------------------------------------------------

	public static int MaskAlloc( Instance instance )
	{
		for ( var at = 0; at < instance.MaskUsed.Count; ++at )
		{
			if ( instance.MaskUsed[at] ) continue;

			instance.MaskUsed[at] = true;
			return at;
		}

		var mask = new float[instance.Skeleton.Bones.Length];
		Array.Fill( mask, 1.0f );

		instance.Masks.Add( mask );
		instance.MaskUsed.Add( true );

		return instance.Masks.Count - 1;
	}

	public static void MaskFree( Instance instance, int mask )
	{
		if ( mask >= 0 && mask < instance.MaskUsed.Count ) instance.MaskUsed[mask] = false;
	}

	public static void MaskFill( Instance instance, int mask, float weight )
	{
		var to = instance.Mask( mask );
		if ( to is null ) return;

		Array.Fill( to, weight );
	}

	public static void MaskSetBone( Instance instance, int mask, int bone, float weight, bool withChildren )
	{
		var skeleton = instance.Skeleton;

		var to = instance.Mask( mask );
		if ( to is null || bone < 0 || bone >= to.Length ) return;

		to[bone] = weight;

		if ( !withChildren ) return;

		//Down the tree in evaluation order, so a bone's parent has already been decided by the time it is reached.
		foreach ( var at in skeleton.Order )
		{
			var parent = skeleton.Bones[at].Parent;
			if ( parent < 0 ) continue;

			if ( parent == bone || to[parent] == weight ) to[at] = weight;
		}
	}

	public static void MaskBlend( Instance instance, int destination, int a, int b, float weight )
	{
		var to = instance.Mask( destination );
		var from = instance.Mask( a );
		var towards = instance.Mask( b );

		if ( to is null || from is null || towards is null ) return;

		for ( var bone = 0; bone < to.Length; ++bone )
			to[bone] = from[bone] + ( ( towards[bone] - from[bone] ) * weight );
	}
}
