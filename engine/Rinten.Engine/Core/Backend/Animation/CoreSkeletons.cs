using System.IO;
using System.Text.Json;
using Rinten.CoreAnimation;

namespace Rinten;

//Loaded skeletons and clips, one shared copy per path for the process; locked for parallel updates
internal static class CoreSkeletons
{
	private static readonly Logger log = new( "Core" );

	private static readonly object padlock = new();

	private static readonly Dictionary<string, CoreAnimRuntime.Skeleton> skeletons = new( StringComparer.OrdinalIgnoreCase );

	//What each model's definition said, read once and kept.
	private static readonly Dictionary<string, Read> definitions = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>The skeleton of a model our core loaded, or null if it has no bones or did not come from our own format.</summary>
	public static CoreAnimRuntime.Skeleton Skeleton( Model model )
	{
		if ( model?.core is not { IsValid: true } core ) return null;

		lock ( padlock )
		{
			if ( skeletons.TryGetValue( core.Name, out var cached ) ) return cached;

			var skeleton = CoreAnimRuntime.CreateSkeleton( core.Loaded );
			skeletons[core.Name] = skeleton;

			return skeleton;
		}
	}

	private static readonly Dictionary<string, CorePoseReach> reaches = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>How far a model's vertices reach from its bones, read once - see CorePoseReach. Null where a pose's bounds can't come from them.</summary>
	public static CorePoseReach Reach( Model model )
	{
		if ( model?.core is not { IsValid: true } core ) return null;

		lock ( padlock )
		{
			if ( reaches.TryGetValue( core.Name, out var cached ) ) return cached;

			var reach = CorePoseReach.Of( core.Loaded );
			reaches[core.Name] = reach;

			return reach;
		}
	}

	/// <summary>Drop what was remembered about a model, so the next thing to ask reads it again.</summary>
	public static void Forget( string modelPath )
	{
		if ( string.IsNullOrWhiteSpace( modelPath ) ) return;

		//However it was named when it was loaded: the compiled file, the definition beside it, either slash.
		var name = Path.ChangeExtension( modelPath.Replace( '\\', '/' ), ".mdl" );

		lock ( padlock )
		{
			Drop( skeletons, name );
			Drop( definitions, name );
			Drop( reaches, name );
		}
	}

	//Drop every key of a cache that is the model being forgotten.
	private static void Drop<T>( Dictionary<string, T> cache, string definition )
	{
		foreach ( var key in cache.Keys.ToList() )
		{
			if ( Matches( key, definition ) ) cache.Remove( key );
		}
	}

	//Whether a remembered model is the one being forgotten. Compared as definitions, since that is the one name both halves of a model share.
	private static bool Matches( string key, string definition )
	{
		var held = Path.ChangeExtension( key.Replace( '\\', '/' ), ".mdl" );

		return held.Equals( definition, StringComparison.OrdinalIgnoreCase )
			|| held.EndsWith( "/" + definition, StringComparison.OrdinalIgnoreCase )
			|| definition.EndsWith( "/" + held, StringComparison.OrdinalIgnoreCase );
	}

	//-- model-level bone queries, answered from the skeleton since our models have no native peer --

	public static int BoneCount( Model model ) => Skeleton( model )?.BoneCount ?? 0;

	public static string BoneName( Model model, int bone ) => Skeleton( model )?.BoneName( bone ) ?? "";

	public static int BoneParent( Model model, int bone ) => Skeleton( model )?.BoneParent( bone ) ?? -1;

	/// <summary>Where a bone rests, relative to the one above it.</summary>
	public static Transform BoneRest( Model model, int bone )
		=> Skeleton( model ) is { } skeleton ? skeleton.Rest( bone ).ToTransform() : Transform.Zero;

	/// <summary>What a model can be asked to play, in the order its definition lists.</summary>
	public static IReadOnlyList<string> SequenceNames( Model model )
	{
		var named = Sequences( model );
		return named is null ? Array.Empty<string>() : named.Keys.ToList();
	}

	/// <summary>Resource path of the clip behind a sequence name, or null.</summary>
	public static string ClipPath( Model model, string sequence )
	{
		if ( string.IsNullOrWhiteSpace( sequence ) ) return null;

		var named = Sequences( model );
		if ( named is null || !named.TryGetValue( sequence, out var path ) ) return null;

		return path;
	}

	/// <summary>The clip a model plays under a given sequence name. Null when the model names no such sequence - a graph asks the model, and nowhere else.</summary>
	public static AnimClipData Clip( Model model, string sequence )
	{
		if ( string.IsNullOrWhiteSpace( sequence ) ) return null;

		var named = Sequences( model );
		if ( named is null ) return null;

		if ( !named.TryGetValue( sequence, out var path ) )
		{
			var key = Path.GetFileNameWithoutExtension( sequence );
			if ( string.IsNullOrWhiteSpace( key ) || !named.TryGetValue( key, out path ) )
				return null;
		}

		return CoreClips.Clip( path );
	}

	//Sequence names from a model's definition; null means no file yet (retried later), empty is kept
	private static Dictionary<string, string> Sequences( Model model )
	{
		if ( model?.core is not { IsValid: true } core ) return null;

		lock ( padlock )
		{
			var read = Of( core.Name, reread: true );

			return read.OnDisk ? read.Clips() : null;
		}
	}

	//On the model because a graph is written against a skeleton. A character put into a scene should animate because it is that character, not because whoever placed it remembered to fill in a field.
	/// <summary>The graph a model is animated by unless something says otherwise, or null where it names none.</summary>
	public static AnimGraph Graph( Model model )
	{
		if ( model?.core is not { IsValid: true } core ) return null;

		lock ( padlock ) return Of( core.Name ).Graph();
	}

	//Resolved to positions here rather than left as names, because a look is solved every frame and looking a name up is not.
	/// <summary>A run of bones that turns together to point at something, as positions in the model's own skeleton. Empty where the model has no such chain.</summary>
	public static (int[] Bones, float[] Weights) Chain( Model model, string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return (Array.Empty<int>(), Array.Empty<float>());
		if ( model?.core is not { IsValid: true } core ) return (Array.Empty<int>(), Array.Empty<float>());

		lock ( padlock )
		{
			return Of( core.Name ).Chains( model ).TryGetValue( name, out var chain )
				? chain
				: (Array.Empty<int>(), Array.Empty<float>());
		}
	}

	/// <summary>What one of a model's IK chains resolved to on its skeleton.</summary>
	public readonly record struct IkChain(
		int[] Bones, bool UseKnee, Vector3 Knee, float Twist )
	{
		/// <summary>Two bones is the shortest thing that can bend, and an unset chain has none.</summary>
		public bool IsValid => Bones is { Length: >= 2 };

		/// <summary>The bone the reach swings from.</summary>
		public int Root => IsValid ? Bones[0] : -1;

		/// <summary>The bone that arrives, and the one a target is a target for.</summary>
		public int Tip => IsValid ? Bones[^1] : -1;

		/// <summary>How many segments there are to fold, which is what picks the solver.</summary>
		public int Segments => IsValid ? Bones.Length - 1 : 0;
	}

	/// <summary>Resolves a model's named IK chain to skeleton bones once; invalid if the model lacks the chain</summary>
	public static IkChain IkChainOf( Model model, string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return default;
		if ( model?.core is not { IsValid: true } core ) return default;

		lock ( padlock )
		{
			return Of( core.Name ).Ik( model ).TryGetValue( name, out var chain ) ? chain : default;
		}
	}

	/// <summary>The bone an IK chain reaches with. -1 where there is no such chain.</summary>
	public static int IkChainBone( Model model, string name )
	{
		var chain = IkChainOf( model, name );
		return chain.IsValid ? chain.Tip : -1;
	}

	//Named per-bone weights, ordered parents first so a child's weight overrides its parent's
	internal sealed class BoneWeights
	{
		public float BaseWeight;
		public (int Bone, float Weight, bool Descendants)[] Bones = System.Array.Empty<(int, float, bool)>();
	}

	/// <summary>The weights a model's definition keeps under <paramref name="name"/>. Null where it names no such list.</summary>
	public static BoneWeights Weights( Model model, string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return null;
		if ( model?.core is not { IsValid: true } core ) return null;

		lock ( padlock )
		{
			return Of( core.Name ).Masks( model ).TryGetValue( name, out var list ) ? list : null;
		}
	}

	/// <summary>What a model's definition calls the weight lists it carries.</summary>
	public static IReadOnlyList<string> WeightListNames( Model model )
	{
		if ( model?.core is not { IsValid: true } core ) return System.Array.Empty<string>();

		lock ( padlock ) return Of( core.Name ).Masks( model ).Keys.ToList();
	}

	/// <summary>An attachment's bone and forward axis in bone space, so look-at nodes aim along it; -1 if missing</summary>
	public static (int Bone, Vector3 Forward, bool InModel) Aim( Model model, string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return (-1, Vector3.Forward, false);
		if ( model?.core is not { IsValid: true } core ) return (-1, Vector3.Forward, false);

		lock ( padlock )
		{
			return Of( core.Name ).Aims( model ).TryGetValue( name, out var aim )
				? aim
				: (-1, Vector3.Forward, false);
		}
	}

	/// <summary>What a model works out for itself every frame, with its bones already found. Empty for the models that drive nothing, which is most of them.</summary>
	public static CoreAnimConstraints.Bound[] Constraints( Model model )
	{
		if ( model?.core is not { IsValid: true } core ) return System.Array.Empty<CoreAnimConstraints.Bound>();

		lock ( padlock ) return Of( core.Name ).Bounds( model );
	}

	/// <summary>A model's jiggle bones, with their bones found and parents first. Empty for the models that have none, which is most of them.</summary>
	public static CoreJiggle.Set Jiggles( Model model )
	{
		if ( model?.core is not { IsValid: true } core ) return CoreJiggle.Set.Empty;

		lock ( padlock ) return Of( core.Name ).Jiggles( model );
	}

	//Jiggle tip rest position in bone space and swing length; aims at the child unless an axis is named
	private static (Vector3 Tip, float Length) TipOf( CoreAnimRuntime.Skeleton skeleton, int bone, ModelDefinition.JiggleBone entry )
	{
		var child = ChildOf( skeleton, bone );
		var toChild = child >= 0 ? skeleton.Bones[child].Rest.Position : Vector3.Zero;
		var measured = toChild.Length > 1e-6f;

		var axis = entry.Axis switch
		{
			ModelDefinition.JiggleAxis.X => new Vector3( 1, 0, 0 ),
			ModelDefinition.JiggleAxis.Y => new Vector3( 0, 1, 0 ),
			ModelDefinition.JiggleAxis.Z => new Vector3( 0, 0, 1 ),
			ModelDefinition.JiggleAxis.NegativeX => new Vector3( -1, 0, 0 ),
			ModelDefinition.JiggleAxis.NegativeY => new Vector3( 0, -1, 0 ),
			ModelDefinition.JiggleAxis.NegativeZ => new Vector3( 0, 0, -1 ),
			_ => measured ? toChild : new Vector3( 1, 0, 0 ),
		};

		if ( entry.Axis != ModelDefinition.JiggleAxis.Auto && measured )
			axis = AnimTrs.NormaliseVec( axis ) * toChild.Length;

		//Nothing to measure and nothing said: a tenth of a metre, which is a length a bone can swing at without looking like a mistake.
		var length = entry.Length > 0.0f ? entry.Length : measured ? 0.0f : 0.1f;

		return (axis, length);
	}

	//The child a jiggle bone points at: the one named after it where there is such a child - an export's end bone - and the first one otherwise.
	private static int ChildOf( CoreAnimRuntime.Skeleton skeleton, int bone )
	{
		var name = skeleton.Bones[bone].Name ?? "";
		var first = -1;

		for ( var i = 0; i < skeleton.Bones.Length; i++ )
		{
			if ( skeleton.Bones[i].Parent != bone ) continue;

			if ( name.Length > 0 && ( skeleton.Bones[i].Name ?? "" ).StartsWith( name, StringComparison.OrdinalIgnoreCase ) )
				return i;

			if ( first < 0 ) first = i;
		}

		return first;
	}

	//-- the definition beside the model ----------------------------------------

	//Lenient .mdl JSON options matching the tools' compiler; graph is skipped, depth matches the editor
	private static readonly JsonSerializerOptions Options = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		IncludeFields = true,
		MaxDepth = 512,
		Converters = { new EnumConvert(), new ModelDefinition.ModelGraph.Skim() },
	};

	//What a model's definition said, read the first time anything asks for any part of it and kept until the model is forgotten.
	//Rereading looks for the file again where it was not there last time, for a caller that cannot take a missing definition for "none".
	private static Read Of( string modelPath, bool reread = false )
	{
		if ( definitions.TryGetValue( modelPath, out var read ) && ( read.OnDisk || !reread ) ) return read;

		read = Parse( modelPath );
		definitions[modelPath] = read;

		return read;
	}

	//Parses a .mdl once into ModelDefinition, instead of each section re-reading the JSON
	private static Read Parse( string modelPath )
	{
		//The definition is the source half of the pair - models/thing.mdl for models/thing.mdl_c
		//- and a model compiled without one has no clips rather than no model.
		var source = Path.ChangeExtension( modelPath, ".mdl" ).Replace( '\\', '/' );

		var read = new Read { Source = source };

		var full = CoreModel.ResolveOnDisk( source );
		if ( full is null ) return read;

		read.OnDisk = true;

		try
		{
			read.Definition = JsonSerializer.Deserialize<ModelDefinition>( CompiledFile.TextOf( full ), Options );
		}
		catch ( JsonException e )
		{
			log.Warning( $"{source}: {e.Message}" );
		}

		return read;
	}

	//One model's definition, with each section of it worked out against that model's skeleton the first time it is asked for.
	private sealed class Read
	{
		/// <summary>The definition's own resource path, which is what its clips are named against.</summary>
		public string Source;

		/// <summary>Whether the file was there, which is not the same as it having anything in it.</summary>
		public bool OnDisk;

		/// <summary>Null where there was nothing to read, or where it would not parse.</summary>
		public ModelDefinition Definition;

		private Dictionary<string, int> bones;
		private Dictionary<string, string> clips;
		private Dictionary<string, (int[] Bones, float[] Weights)> chains;
		private Dictionary<string, IkChain> ik;
		private Dictionary<string, BoneWeights> masks;
		private Dictionary<string, (int Bone, Vector3 Forward, bool InModel)> aims;
		private CoreAnimConstraints.Bound[] bounds;
		private CoreJiggle.Set jiggles;

		private AnimGraph graph;

		//The AnimGraph.Generation the graph was loaded at, or -1 before it was.
		private int graphRead = -1;

		//Bone names to where they are, which is what the solvers want.
		private Dictionary<string, int> Bones( Model model )
		{
			if ( bones is not null ) return bones;

			bones = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );

			for ( var i = 0; i < BoneCount( model ); i++ ) bones.TryAdd( BoneName( model, i ), i );

			return bones;
		}

		//Asked again once any graph has been compiled since the last time, which is a number compared on every ask and a load that finds the same graph already loaded on every other.
		/// <summary>The graph this definition carries, loaded.</summary>
		public AnimGraph Graph()
		{
			var generation = AnimGraph.Generation;

			if ( graphRead == generation ) return graph;

			graphRead = generation;

			//Compiled beside it, which is all a game ever reads of it - the document in the definition is the graph editor's.
			graph = Definition?.Graph is not null ? AnimGraph.Load( Source ) : null;

			return graph;
		}

		/// <summary>Sequence name to the clip resource that plays it.</summary>
		public Dictionary<string, string> Clips() => clips ??= ClipsOf( Source, Definition );

		/// <summary>Sequence name to the clip resource that plays it, for the definition at <paramref name="source"/>. Static so a build can ask what a model will load without loading it - see <see cref="CoreSkeletons.ClipsOf"/>.</summary>
		public static Dictionary<string, string> ClipsOf( string source, ModelDefinition definition )
		{
			var clips = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

			if ( definition?.Clips is not { Count: > 0 } listed ) return clips;

			var folder = Path.GetDirectoryName( source )?.Replace( '\\', '/' ) ?? "";

			foreach ( var entry in listed )
			{
				if ( entry is null || !entry.Enabled ) continue;

				var file = entry.File ?? "";

				//What the model calls it, worked out by the clip itself - the same answer the compiler wrote the file under. Worked out here
				//as well, from two of the three things it looks at, it stopped agreeing the day a clip could be named apart from its take.
				var sequence = entry.SequenceName;

				if ( string.IsNullOrWhiteSpace( sequence ) ) continue;

				//A clip whose File already is the compiled clip - a character with no source art - names where it actually sits, which
				//is not always beside the model: SBoxEmbeddedAnimations puts a character's hundreds of them in a folder of their own.
				string clipPath;

				if ( file.EndsWith( ".anim_c", StringComparison.OrdinalIgnoreCase ) )
				{
					//As the definition spells it, which is a resource path - what it is when the project it belongs to is mounted.
					clipPath = file;

					//And when it is not, because the model was opened by a real path rather than a mounted one:
					//every other branch here inherits the model's own rooting, and this is the one that does not.
					if ( CoreModel.ResolveOnDisk( clipPath ) is null && Anchored( folder, file ) is { } found )
						clipPath = found;
				}
				else
				{
					//The usual place: beside the model, which is where the editor compiles a clip it authored.
					//Tried first, because it is where almost everything lives and costs nothing extra to check.
					var besideModel = string.IsNullOrWhiteSpace( folder ) ? sequence + ".anim_c" : $"{folder}/{sequence}.anim_c";

					//A port keeps a clip beside the export it was built from instead, which is not always the
					//model's own folder. Tried second, and only once the first guess is confirmed missing.
					var exportFolder = Path.GetDirectoryName( file )?.Replace( '\\', '/' ) ?? "";
					var besideExport = string.IsNullOrWhiteSpace( exportFolder ) ? sequence + ".anim_c" : $"{exportFolder}/{sequence}.anim_c";

					clipPath = besideExport != besideModel && CoreModel.ResolveOnDisk( besideModel ) is null
						&& CoreModel.ResolveOnDisk( besideExport ) is not null
						? besideExport
						: besideModel;
				}

				clips[sequence] = clipPath.Replace( '\\', '/' );
			}

			return clips;
		}

		/// <summary>The look-at chains, as positions in this model's skeleton.</summary>
		public Dictionary<string, (int[] Bones, float[] Weights)> Chains( Model model )
		{
			if ( chains is not null ) return chains;

			chains = new Dictionary<string, (int[], float[])>( StringComparer.OrdinalIgnoreCase );

			if ( Definition?.LookAtChains is not { Count: > 0 } listed ) return chains;

			var at = Bones( model );

			foreach ( var entry in listed )
			{
				if ( entry is null || string.IsNullOrWhiteSpace( entry.Name ) ) continue;
				if ( entry.Bones is not { Count: > 0 } links ) continue;

				var order = new List<int>();
				var weights = new List<float>();

				foreach ( var link in links )
				{
					//A bone the model does not have is a chain written for another skeleton, and turning the wrong bone is worse than turning none.
					if ( link?.Bone is null || !at.TryGetValue( link.Bone, out var index ) ) continue;

					order.Add( index );
					weights.Add( link.Weight );
				}

				if ( order.Count > 0 ) chains[entry.Name] = (order.ToArray(), weights.ToArray());
			}

			return chains;
		}

		/// <summary>The IK chains, as runs of bones in this model's skeleton.</summary>
		public Dictionary<string, IkChain> Ik( Model model )
		{
			if ( ik is not null ) return ik;

			ik = new Dictionary<string, IkChain>( StringComparer.OrdinalIgnoreCase );

			if ( Definition?.IkChains is not { Count: > 0 } listed ) return ik;

			var at = Bones( model );

			int Index( string bone )
				=> bone is not null && at.TryGetValue( bone, out var found ) ? found : -1;

			foreach ( var entry in listed )
			{
				if ( entry is null || string.IsNullOrWhiteSpace( entry.Name ) ) continue;
				if ( !entry.Enabled ) continue;

				//A bone the model does not have is a chain written for another skeleton, and reaching with the wrong bone is worse than not reaching at all.
				var tip = Index( entry.Bone );
				if ( tip < 0 ) continue;

				var root = Index( entry.StartBone );

				//A middle bone is what a chain used to be written with, and naming one still pins
				//the run to those three - a rig whose limb wants a joint skipped says so that way.
				var middle = Index( entry.MiddleBone );

				if ( root < 0 ) root = middle >= 0 ? BoneParent( model, middle ) : Up( model, tip, 2 );
				if ( root < 0 ) continue;

				int[] run = middle >= 0
					? IsAbove( model, root, middle ) && IsAbove( model, middle, tip )
						? [root, middle, tip]
						: null
					: RunBetween( model, root, tip );

				//Bones that are not one above the other are not a limb, and solving them would move a hand by turning somebody else's shoulder.
				if ( run is null )
				{
					log.Warning( $"{Source}: ik chain '{entry.Name}' is not a run of bones" );
					continue;
				}

				ik[entry.Name] = new IkChain( run, entry.UseKneeDirection, entry.KneeDirection, entry.TwistWeight );
			}

			return ik;
		}

		/// <summary>The weight lists, as positions in this model's skeleton, parents first.</summary>
		public Dictionary<string, BoneWeights> Masks( Model model )
		{
			if ( masks is not null ) return masks;

			masks = new Dictionary<string, BoneWeights>( StringComparer.OrdinalIgnoreCase );

			if ( Definition?.WeightLists is not { Count: > 0 } listed ) return masks;

			var at = Bones( model );

			foreach ( var entry in listed )
			{
				if ( entry is null || string.IsNullOrWhiteSpace( entry.Name ) ) continue;

				//Switched off in the editor is switched off here: a mask that somebody turned off should behave as one nobody wrote.
				if ( !entry.Enabled ) continue;

				var weighted = new List<(int Bone, float Weight, bool Descendants)>();

				if ( entry.Bones is { Count: > 0 } listedBones )
				{
					foreach ( var bone in listedBones )
					{
						//A bone this skeleton has not got is a list written for another body of the same family, which is ordinary.
						if ( bone?.Bone is null || !at.TryGetValue( bone.Bone, out var index ) ) continue;

						weighted.Add( (index, Math.Clamp( bone.Weight, 0.0f, 1.0f ), bone.Descendants) );
					}
				}

				//Parents first, so a child that sets its own weight is written after the parent whose entry reaches down over it.
				weighted.Sort( ( a, b ) => Depth( model, a.Bone ).CompareTo( Depth( model, b.Bone ) ) );

				masks[entry.Name] = new BoneWeights
				{
					BaseWeight = Math.Clamp( entry.BaseWeight, 0.0f, 1.0f ),
					Bones = weighted.ToArray(),
				};
			}

			return masks;
		}

		/// <summary>Where each attachment sits, and which way it faces, on this model's bones.</summary>
		public Dictionary<string, (int Bone, Vector3 Forward, bool InModel)> Aims( Model model )
		{
			if ( aims is not null ) return aims;

			aims = new Dictionary<string, (int, Vector3, bool)>( StringComparer.OrdinalIgnoreCase );

			if ( Definition?.Attachments is not { Count: > 0 } listed ) return aims;

			var at = Bones( model );

			//Bones a definition adds are not in the skeleton and an attachment is allowed to hang off one, so they are kept to walk through.
			var added = new Dictionary<string, (string Parent, Rotation Local)>( StringComparer.OrdinalIgnoreCase );

			if ( Definition.Bones is { Count: > 0 } extras )
			{
				foreach ( var extra in extras )
				{
					if ( extra is null || string.IsNullOrWhiteSpace( extra.Name ) ) continue;

					added[extra.Name] = (extra.Parent ?? "", extra.Rotation.ToRotation());
				}
			}

			foreach ( var entry in listed )
			{
				if ( entry is null || string.IsNullOrWhiteSpace( entry.Name ) ) continue;

				var turn = entry.Rotation.ToRotation();
				var parent = entry.Parent ?? "";

				//Down to the first real bone, carrying the turns along. Bounded by how many added bones
				//there are, so a definition that names its own parent is a miss rather than a hang.
				for ( var step = 0; step <= added.Count && !at.ContainsKey( parent ); step++ )
				{
					if ( !added.TryGetValue( parent, out var above ) ) break;

					turn = above.Local * turn;
					parent = above.Parent;
				}

				//An attachment on a bone this skeleton does not have belongs to another model, and pointing the wrong bone is worse than leaving the look where it was.
				if ( !at.TryGetValue( parent, out var bone ) ) continue;

				//A fixed-direction attachment is a model-space direction, not tied to any one bone
				if ( entry.FacesOneWay )
				{
					aims[entry.Name] = (-1, ( model.GetBoneTransform( bone ).Rotation * turn ).Forward, true);
					continue;
				}

				aims[entry.Name] = (bone, turn.Forward, false);
			}

			return aims;
		}

		/// <summary>The bones this model swings on springs, parents first so each swings from where its parent ended up, and the spheres they are kept out of.</summary>
		public CoreJiggle.Set Jiggles( Model model )
		{
			if ( jiggles is not null ) return jiggles;

			jiggles = CoreJiggle.Set.Empty;

			if ( Definition?.JiggleBones is not { Count: > 0 } listed ) return jiggles;
			if ( Skeleton( model ) is not { } skeleton ) return jiggles;

			var at = Bones( model );
			var bound = new List<(int Depth, CoreJiggle.Bound Bound)>();

			foreach ( var entry in listed )
			{
				//One somebody turned off in the editor to see the pose without it.
				if ( entry is null || !entry.Enabled ) continue;

				if ( string.IsNullOrEmpty( entry.Bone ) || !at.TryGetValue( entry.Bone, out var bone ) ) continue;

				var (tip, length) = TipOf( skeleton, bone, entry );

				bound.Add( (Depth( model, bone ), new CoreJiggle.Bound
				{
					Bone = bone,
					Tip = tip,
					Length = length,
					Omega = 2.0f * MathF.PI * MathF.Max( entry.Stiffness, 0.01f ),
					Damping = MathF.Max( entry.Damping, 0.0f ),
					Gravity = entry.Gravity,
					WorldMotion = entry.WorldMotion,
					MaxAngle = Math.Clamp( entry.MaxAngle, 0.0f, 180.0f ) * MathF.PI / 180.0f,
					Weight = entry.Weight,
					Radius = MathF.Max( entry.Radius, 0.0f ),
				}) );
			}

			if ( bound.Count == 0 ) return jiggles;

			var colliders = new List<CoreJiggle.Collider>();

			foreach ( var entry in Definition.JiggleColliders ?? new() )
			{
				if ( entry is null || !entry.Enabled || entry.Radius <= 0.0f ) continue;
				if ( string.IsNullOrEmpty( entry.Bone ) || !at.TryGetValue( entry.Bone, out var bone ) ) continue;

				colliders.Add( new CoreJiggle.Collider { Bone = bone, Offset = entry.Position, Radius = entry.Radius } );
			}

			jiggles = new CoreJiggle.Set
			{
				Bones = bound.OrderBy( x => x.Depth ).Select( x => x.Bound ).ToArray(),
				Colliders = colliders.ToArray(),
			};

			return jiggles;
		}

		/// <summary>The bones this model drives itself, with their drivers found.</summary>
		public CoreAnimConstraints.Bound[] Bounds( Model model )
		{
			if ( bounds is not null ) return bounds;

			bounds = System.Array.Empty<CoreAnimConstraints.Bound>();

			if ( Definition?.Constraints is not { Count: > 0 } listed ) return bounds;

			var at = Bones( model );

			var made = new List<CoreAnimConstraints.Bound>();

			foreach ( var entry in listed )
			{
				//One somebody turned off in the editor to see what it had been doing.
				if ( entry is null || !entry.Enabled ) continue;

				//A constraint driving a bone this skeleton has not got belongs to another body of the same
				//family, which is ordinary: the citizen's list is written once for every shape of citizen.
				if ( entry.Bone is null || !at.TryGetValue( entry.Bone, out var bone ) ) continue;

				var drivers = new List<int>();
				var shares = new List<float>();
				var offsets = new List<AnimTrs>();

				if ( entry.Drivers is { Count: > 0 } driving )
				{
					foreach ( var driver in driving )
					{
						if ( driver is null ) continue;

						AnimTrs offset;

						if ( string.IsNullOrEmpty( driver.Bone ) )
						{
							//An attachment rather than a bone, which is a definition written before those were spread over
							//the bones they are pulled between. One bone of the several, which is what it used to be worth.
							var (rides, _, _) = Aim( model, driver.Attachment );
							if ( rides < 0 ) continue;

							offset = Offset( model, driver.Attachment );
							drivers.Add( rides );
						}
						else
						{
							if ( !at.TryGetValue( driver.Bone, out var index ) ) continue;

							offset = Sits( driver );
							drivers.Add( index );
						}

						shares.Add( driver.Weight );
						offsets.Add( offset );
					}
				}

				if ( drivers.Count == 0 ) continue;

				made.Add( new CoreAnimConstraints.Bound
				{
					Kind = entry.Kind,
					Bone = bone,
					Weight = entry.Weight,
					Drivers = drivers.ToArray(),
					Shares = shares.ToArray(),
					Offsets = offsets.ToArray(),
					DriverAxis = (int)entry.DriverAxis,
					BoneAxis = (int)entry.BoneAxis,
				} );
			}

			if ( made.Count > 0 ) bounds = made.ToArray();

			return bounds;
		}
	}

	//How far down the skeleton a bone sits, counted from a root.
	private static int Depth( Model model, int bone )
	{
		var depth = 0;

		//Bounded by the bone count: a skeleton that somehow loops is a hang otherwise, and this runs while a model is being read.
		for ( var at = bone; at >= 0 && depth <= BoneCount( model ); depth++ )
			at = BoneParent( model, at );

		return depth;
	}

	//Whether one bone is somewhere above another in the skeleton.
	private static bool IsAbove( Model model, int above, int below )
	{
		for ( var at = BoneParent( model, below ); at >= 0; at = BoneParent( model, at ) )
		{
			if ( at == above ) return true;
		}

		return false;
	}

	//How many parents up from a bone, or -1 where the skeleton runs out first.
	private static int Up( Model model, int bone, int steps )
	{
		var at = bone;

		for ( var i = 0; i < steps && at >= 0; i++ ) at = BoneParent( model, at );

		return at;
	}

	//Bones from root down to tip inclusive, walked from the tip; null if one is not above the other
	private static int[] RunBetween( Model model, int root, int tip )
	{
		if ( root < 0 || tip < 0 || root == tip ) return null;

		var run = new List<int> { tip };

		//Bounded by the bone count: a skeleton that somehow loops would hang here otherwise, and this runs while a model is being read.
		for ( var at = BoneParent( model, tip ); at >= 0; at = BoneParent( model, at ) )
		{
			run.Add( at );

			if ( at == root )
			{
				run.Reverse();
				return run.ToArray();
			}

			if ( run.Count > BoneCount( model ) ) break;
		}

		return null;
	}

	//Where a driver reads on the bone it names.
	private static AnimTrs Sits( ModelDefinition.Constraint.Driver driver )
		=> new()
		{
			Position = driver.Position,
			Rotation = driver.Rotation.ToRotation(),
			Scale = Vector3.One,
		};

	//Where an attachment sits on its bone, as a transform.
	private static AnimTrs Offset( Model model, string attachment )
	{
		if ( model?.Attachments?.Get( attachment ) is not { } found ) return AnimTrs.Identity;

		var local = found.LocalTransform;

		return new AnimTrs
		{
			Position = local.Position,
			Rotation = local.Rotation,
			Scale = local.Scale,
		};
	}

	//The clip resources a model definition plays, by the same rule a loaded model resolves them with. For a build, which has to ship them and
	//must not load the model to find out which: nothing compiles in a game, so a clip left behind is a clip the game asks for and does not have.
	internal static Dictionary<string, string> ClipsOf( string source, ModelDefinition definition ) => Read.ClipsOf( source, definition );

	//Resolves a resource path against the folders above folder; null if none has it
	private static string Anchored( string folder, string file )
	{
		if ( string.IsNullOrWhiteSpace( folder ) || string.IsNullOrWhiteSpace( file ) ) return null;

		var root = folder.Replace( '/', Path.DirectorySeparatorChar );
		if ( !Path.IsPathRooted( root ) ) return null;

		var relative = file.Replace( '/', Path.DirectorySeparatorChar );

		while ( !string.IsNullOrEmpty( root ) )
		{
			var candidate = Path.Combine( root, relative );

			if ( File.Exists( candidate ) ) return candidate.Replace( '\\', '/' );

			root = Path.GetDirectoryName( root );
		}

		return null;
	}
}
