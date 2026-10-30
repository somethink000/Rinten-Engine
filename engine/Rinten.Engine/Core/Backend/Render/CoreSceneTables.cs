using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Rinten;

//What places a run of copies on the card - see CoreSceneTables.AddRun.
internal interface IInstanceRun
{
	//Writes a run's slots into the instance buffer: each copy the template where it stands, an empty one undrawn
	//Between targets, after the tables went up; false where it could not yet, asked again next frame
	bool Place( ulong instances, int head, int count, in CoreSceneTables.InstanceRecord template, System.Numerics.Vector4 bound );
}

//GPU tables for one scene: a record per instance plus materials; Sync uploads only what changed
internal sealed class CoreSceneTables
{
	//One instance as the card reads it - see shaders/engine/mesh_tables.slang.
	[StructLayout( LayoutKind.Sequential )]
	internal struct InstanceRecord
	{
		public System.Numerics.Matrix4x4 World;
		public System.Numerics.Matrix4x4 Prev;
		public System.Numerics.Vector4 Sphere;
		public uint Mesh;
		public uint MaterialFirst;
		public uint Flags;
		public float CullGrow;
		public System.Numerics.Vector4 Numbers;
		public System.Numerics.Vector4 Tint;
		public System.Numerics.Vector4 Said0;
		public System.Numerics.Vector4 Said1;
		public uint PosedFirst;
		public uint PosedSource;
		public uint PaletteFirst;
		public uint PaletteCount;

		/// <summary>How many times the instance has been put somewhere else - what tells a shadow page baked with it where it used to stand from one baked where it stands now, even when the sphere came out the same. See CoreCasters.</summary>
		public uint MoveCount;

		/// <summary>One while moving, nought once still a while: whether it goes in the maps drawn every frame.</summary>
		public uint Moving;

		/// <summary>What its clusters' error is multiplied by in every view's cut - see ModelRenderer.LodBias.</summary>
		public float LodScale;

		/// <summary>The level-of-detail group scaling its error with distance: row plus one, nought for none.</summary>
		public uint LodGroup;
	}

	internal const int RecordBytes = 256;

	//Which pass of the frame an instance is drawn in - see RenderOptions. The game's world is most of them; the
	//other two are drawn the classic way, after the world: over it with depth of their own, or over the interface.
	internal enum DrawLayer
	{
		Game,
		Overlay,
		AfterUi,
	}

	//The managed side of an instance: what it was resolved from, and what it came to.
	internal sealed class Entry
	{
		/// <summary>Which pass it is drawn in - see <see cref="DrawLayer"/>.</summary>
		public DrawLayer Layer;

		public ModelRenderer Renderer;
		public SceneObject Object;

		/// <summary>Whether it said it is here for a moment - see SceneObjectFlags.NeverStill.</summary>
		public bool NeverStill;

		/// <summary>Metres it may stray from the surface it stands in for; nought where it is no stand-in - see SceneObject.StandInError.</summary>
		public float StandInError;

		/// <summary>Stands for nothing in the scene: a model at a place, handed over once - see AddStatic.</summary>
		public bool Static;
		public Transform StaticTransform;
		public bool StaticShadows;
		public float StaticLodScale = 1.0f;

		/// <summary>The detail group it was added with, or null for its model's own - see LodGroupOf.</summary>
		public string StaticLodGroup;

		/// <summary>Slots from this one in a run the card places, nought for one instance - see AddRun.</summary>
		public int Run;

		/// <summary>The flags a run's copies are drawn with; its record lacks InstanceDrawn until the card placed them.</summary>
		public uint RunFlags;

		/// <summary>Where a run's copies stand, all of them inside it, and what places them.</summary>
		public System.Numerics.Vector4 RunSphere;
		public IInstanceRun Placer;

		public CoreSceneRenderer.SceneMesh Mesh;
		public Model Model;
		public ulong Groups;
		public int MeshIndex = -1;
		public Color Tint;
		public string MaterialGroup;
		public ModelRenderer.ShadowRenderType RenderType;
		public Material Override;
		public int ObjectVersion;

		/// <summary>And its count of everything but where it stands and its colour - see SinceSeen.</summary>
		public int ObjectDrawVersion;

		/// <summary>A renderer's count of the same - see ModelRenderer.DrawVersion.</summary>
		public int DrawVersion;
		public int Resources;

		/// <summary>Where its parts' materials sit in the instance-material words, and how many.</summary>
		public int MaterialFirst;
		public int MaterialCount;

		/// <summary>Most finest-level triangles per camera list (one-sided, two-sided, cut-out).</summary>
		public int OneSided, TwoSided, CutOut;

		/// <summary>And the triangles of its parts that move their own vertices, kept apart from the three above.</summary>
		public int Moving;

		/// <summary>What Count added to the table sums, so Uncount removes exactly that and the sums can't drift</summary>
		public int CountedChunks, CountedFinest, CountedOneSided, CountedTwoSided, CountedCutOut, CountedMoving;

		/// <summary>How many writes its object's bag of numbers had when they were last read - see SinceSeen.</summary>
		public int ObjectAttributes;

		/// <summary>Where the materials of its parts that code has set values on stood when it was last resolved - see <see cref="EditedMark"/>.</summary>
		public int EditedSeen;

		/// <summary>The shader's declared draw numbers from the last resolve; see SaysKnown for what null means</summary>
		public CoreShaderParams.Parameter[] Says;

		/// <summary>Whether the resolve got as far as reading a bag of numbers - see <see cref="Says"/>.</summary>
		public bool SaysKnown;

		/// <summary>Where each of those numbers goes - see CoreShaderParams.PlanOf.</summary>
		public CoreShaderParams.DrawPlan SaysPlan;

		/// <summary>Whether it is drawn the classic way - posed, morphed, pulled - rather than from clusters.</summary>
		public bool Loose;

		public bool Skinned;
		public bool Live;

		/// <summary>Which sync of the scene's renderers last saw this one - see SyncRenderers.</summary>
		public int SeenBy;

		/// <summary>Whether anything drives its blend shapes; with none set it draws its base shape.</summary>
		public bool Morphed;

		/// <summary>A skinned or shaped thing through its clusters: its vertices posed into the pool each frame.</summary>
		public bool Posed;
		public long PosedAt = -1;
		public int PosedCount;
		public long PaletteAt = -1;
		public int PaletteCount;

		/// <summary>Where its blend shapes are summed, a vertex long range of ShapeSums; -1 for none.</summary>
		public long ShapesAt = -1;
		public int ShapesCount;

		/// <summary>This frame's weights, kept so asking makes nothing - read by CoreMeshRender.Skin.</summary>
		public float[] ShapeWeights;

		/// <summary>A hash of the bone matrices and the blend shape weights this one was last posed with.</summary>
		public ulong PoseHash;

		/// <summary>What its pool range was last posed with: hash, range, pool generation - see Skin.</summary>
		public ulong SkinnedHash;
		public long SkinnedAt = -1;
		public int SkinnedGeneration = -1;

		/// <summary>Its ModelDeformer stack and rigid anchors, a range of Deformations; -1 for none.</summary>
		public long DeformAt = -1;
		public int DeformCount;
		public int DeformVolumes;
		public int DeformAnchors;

		/// <summary>Its positions as posed before the last posing, a PosedCount range of Was; -1 for none.</summary>
		public long WasAt = -1;

		/// <summary>The loop turn whose posing moved it from what <see cref="WasAt"/> holds - see CoreMeshRender.Skin.</summary>
		public long PoseMovedTurn = -1;

		/// <summary>Back to what a new one would be, without being a new one - see Give.</summary>
		public void Wipe()
		{
			Renderer = null;
			Object = null;
			SeenBy = 0;
			ObjectAttributes = 0;
			EditedSeen = 0;
			Says = null;
			SaysPlan = null;
			SaysKnown = false;
			Static = false;
			StaticTransform = default;
			StaticShadows = false;
			StaticLodScale = 1.0f;
			StaticLodGroup = null;
			Run = 0;
			RunFlags = 0;
			RunSphere = default;
			Placer = null;
			Mesh = null;
			Model = null;
			Groups = default;
			Tint = default;
			MaterialGroup = null;
			RenderType = default;
			Override = null;
			ObjectVersion = 0;
			ObjectDrawVersion = 0;
			DrawVersion = 0;
			Resources = 0;
			MaterialFirst = 0;
			MaterialCount = 0;
			CountedChunks = CountedFinest = CountedOneSided = CountedTwoSided = CountedCutOut = CountedMoving = 0;
			Loose = false;
			Skinned = false;
			Live = false;
			Morphed = false;
			Posed = false;
			PosedAt = -1;
			PosedCount = 0;
			PaletteAt = -1;
			PaletteCount = 0;
			ShapesAt = -1;
			ShapesCount = 0;
			ShapeWeights = null;
			PoseHash = 0;
			SkinnedHash = 0;
			SkinnedAt = -1;
			SkinnedGeneration = -1;
			WasAt = -1;
			PoseMovedTurn = -1;
			DeformAt = -1;
			DeformCount = 0;
			DeformVolumes = 0;
			DeformAnchors = 0;
		}
	}

	//RINTEN_LOOSE_WHY=1 logs why instances fall off the cluster path, a common hidden slowdown
	private static readonly bool looseWhy = Environment.GetEnvironmentVariable( "RINTEN_LOOSE_WHY" ) == "1";
	private static readonly int[] looseCounts = new int[8];
	private static int looseSaid;

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Scene, CoreSceneTables> tables = new();

	//Every posed instance's bone matrices, a range each - what the skin pass reads.
	internal static readonly CoreGeometryPool.Pool Palettes = new( 64, 1 << 12 );

	//The most bones a posed instance carries - the frame's own binding covers the same.
	internal const int MostBones = 128;

	//This frame's poses, waiting to go into the palette pool.
	private readonly List<(long At, System.Numerics.Matrix4x4[] Pose)> paletteQueue = new();

	//What every shaped instance's blend shapes add to each of its vertices - a float3 of position and one of
	//normal a vertex, a range each. Written by mesh_shapes.slang, read by the pose pass beside the palettes.
	internal static readonly CoreGeometryPool.Pool ShapeSums = new( 24, 1 << 16 );

	//Every deformed instance's ModelDeformer volumes, then its rigid anchors - a float4 each, written each frame it is posed
	internal static readonly CoreGeometryPool.Pool Deformations = new( 16, 1 << 10 );

	//This frame's stacks, waiting to go into that pool.
	private readonly List<(long At, System.Numerics.Vector4[] Words, int Count)> deformQueue = new();

	//Every posed instance's positions before its last posing, a float3 a vertex - what the velocity pass moves them from
	internal static readonly CoreGeometryPool.Pool Was = new( 12, 1 << 16 );

	internal static CoreSceneTables Of( Scene scene ) => tables.GetOrCreateValue( scene );

	private InstanceRecord[] records = new InstanceRecord[256];
	private Entry[] entries = new Entry[256];
	private int used;
	private readonly List<int> free = new();
	private readonly Dictionary<ModelRenderer, int> byRenderer = new();
	private readonly Dictionary<SceneObject, int> byObject = new();

	//Hand-placed object walk stats: every one is asked each frame, so a big Asked with small rest = waste
	internal static long ObjectsAsked;
	internal static long ObjectsPlaced;
	internal static long ObjectsNumbered;
	internal static long ObjectsResolved;
	internal static long Syncs;

	//The slots whose model has blend shapes - the only ones worth asking about; see the poll in Sync
	private readonly List<int> morphable = new();

	//Slots posed each frame, so PoseAll needn't walk the whole table; compacted as it walks
	private readonly List<int> posedSlots = new();

	//Which slots are posed - for the passes that walk them rather than every instance.
	//The list itself: walked through the interface, every walk boxed its enumerator.
	internal List<int> PosedSlots => posedSlots;
	private readonly List<int> dirty = new();
	private bool allDirty = true;

	//Instances drawn the classic way; a set so add is idempotent and remove is unconditional and O(1)
	internal readonly HashSet<int> Loose = new();

	//Instance-material words: one material index a part, per instance.
	private uint[] instanceMaterials = new uint[1024];
	private int instanceMaterialsUsed;
	private readonly List<(int At, int Count)> materialHoles = new();
	private bool materialsDirty = true;

	//Which version of the shared material table this scene's buffer holds.
	private int materialsGeneration = -1;

	//The material buffer as it goes to the card, kept rather than made a frame.
	private uint[] packed = Array.Empty<uint>();

	private ulong instanceBuffer;
	private int instanceBufferRecords;
	private ulong materialBuffer;
	private int materialBufferWords;
	private int materialsSeen = -1;

	private int directoryVersion = -1;

	//The world's membership count when its objects were last taken in - see SceneWorld.Membership.
	private int worldMembership = -1;

	//Scratch for taking the scene's renderers in - see SyncRenderers.
	private readonly List<ModelRenderer> foundRenderers = new();
	private readonly List<ModelRenderer> goneRenderers = new();

	//And the same for the objects put in the world by hand - see SyncObjects.
	private readonly HashSet<SceneObject> seenObjects = new();
	private readonly List<SceneObject> goneObjects = new();

	//The slots of the hand-placed objects, flat - see the walk in Sync.
	private readonly List<int> objectSlots = new();
	private int resources = -1;
	private int pictures = -1;
	private int forgotten = -1;
	private int paths = -1;

	//How many instance slots are in use, including freed ones the card still holds.
	internal int InstanceCount => used;

	//The sum over drawn instances of their clusters in chunks of sixty-four - what the cull's work list must hold per view.
	internal int ChunkCount { get; private set; }

	//The sum over drawn instances of their finest level's clusters - what one view's visible list must hold.
	internal int FinestClusters { get; private set; }

	internal ulong InstanceBuffer => instanceBuffer;
	internal ulong MaterialBuffer => materialBuffer;

	//Where the instance-material words start in the material buffer, in words.
	internal int MaterialsAt => CoreMaterialTable.Count * 4;

	internal Entry EntryAt( int index ) => index >= 0 && index < used ? entries[index] : null;

	//A renderer's slot in these tables, or -1; for passes that draw a specific object (outlines, masks)
	internal int SlotOf( ModelRenderer renderer )
		=> renderer is not null && byRenderer.TryGetValue( renderer, out var slot ) ? slot : -1;

	internal int SlotOf( SceneObject scene )
		=> scene is not null && byObject.TryGetValue( scene, out var slot ) ? slot : -1;
	internal ref InstanceRecord RecordAt( int index ) => ref records[index];

	//Brings the tables up to date with the scene.
	/// <summary>Whether the scene has a bake, so all that is not static casts as moving - see CoreCasters.</summary>
	internal bool Movable { get; private set; }

	internal void Sync( Scene scene )
	{
		var rebuiltAll = false;

		//A bake came or went: every record's flags say again what of it moves
		var baked = CoreBakedLighting.Baked( scene );

		if ( baked != Movable )
		{
			Movable = baked;
			rebuiltAll = true;
		}

		//Collect moved renderers once per loop turn: CullMoved is drained, so later drawers would see stops
		var turn = EngineLoop.LoopFrames;

		if ( syncedAt != turn )
		{
			syncedAt = turn;

			//Before Moved is emptied: it holds last turn's movers, which ClearMoving tells from this turn's
			ClearMoving();

			Moved.Clear();
		}

		var meshesGone = false;

		if ( resources != CoreResources.Generation ||
			paths != Rinten.Rendering.DrawSettings.PathGeneration )
		{
			//Texture swaps need no rebuild, as records hold no picture numbers; meshes given back - a terrain chunk
			//streamed out - only the slots that drew them, or every caster changes and every shadow page bakes again
			var swaps = CoreResources.PictureGeneration - pictures;
			var gone = CoreResources.MeshesForgotten - forgotten;
			var narrow = resources >= 0 && paths == Rinten.Rendering.DrawSettings.PathGeneration &&
				CoreResources.Generation - resources == swaps + gone;

			resources = CoreResources.Generation;
			pictures = CoreResources.PictureGeneration;
			forgotten = CoreResources.MeshesForgotten;
			paths = Rinten.Rendering.DrawSettings.PathGeneration;
			rebuiltAll = rebuiltAll || !narrow;
			meshesGone = narrow && gone > 0;
		}

		if ( lodGroups != Rinten.Rendering.LodGroups.Generation )
		{
			lodGroups = Rinten.Rendering.LodGroups.Generation;
			Regroup();
		}

		CoreRenderDevice.ZoneBegin( "tables register" );

		//Renderers come and go every frame with particles, so this rescan really is needed each time
		if ( scene.DirectoryVersion != directoryVersion )
		{
			directoryVersion = scene.DirectoryVersion;
			SyncRenderers( scene );
		}

		var world = scene.SceneWorld;

		//On the membership count, not the version: particles change the version every frame, and this walked
		//every object each time to find the same ones - see SceneWorld.Membership
		if ( world is not null && world.Membership != worldMembership )
		{
			worldMembership = world.Membership;
			SyncObjects( world );
		}

		CoreRenderDevice.ZoneEnd();

		//The ones that moved say so through the renderer's list, which is everybody's: another scene's go back
		var moved = ModelRenderer.CullMoved;
		var kept = 0;

		for ( var i = 0; i < moved.Count; i++ )
		{
			var renderer = moved[i];
			if ( renderer is null ) continue;

			if ( !byRenderer.TryGetValue( renderer, out var slot ) )
			{
				if ( renderer.IsValid() ) moved[kept++] = renderer;
				else renderer.CullSettled();
				continue;
			}

			renderer.CullSettled();
			Place( slot );
		}

		moved.RemoveRange( kept, moved.Count - kept );

		if ( moved.Count > 8192 )
		{
			foreach ( var one in moved ) one?.CullSettled();
			moved.Clear();
		}

		//Changed renderers report themselves (CullChanged) instead of sweeping the table; objects are asked
		CoreRenderDevice.ZoneBegin( "tables resolve" );

		Syncs++;

		if ( rebuiltAll )
		{
			for ( var i = 0; i < used; i++ )
			{
				var entry = entries[i];
				if ( entry is null || !entry.Live ) continue;

				Resolve( i );
			}
		}
		else
		{
			if ( meshesGone )
			{
				for ( var i = 0; i < used; i++ )
					if ( entries[i] is { Live: true, Mesh.Released: true } ) Resolve( i );
			}

			var changed = ModelRenderer.CullChanged;
			var held = 0;

			for ( var i = 0; i < changed.Count; i++ )
			{
				var renderer = changed[i];
				if ( renderer is null ) continue;

				//An entry of another scene is put back, the same way the moved list does it.
				if ( !byRenderer.TryGetValue( renderer, out var slot ) )
				{
					if ( renderer.IsValid() ) changed[held++] = renderer;
					else renderer.CullResolved();
					continue;
				}

				renderer.CullResolved();

				var entry = entries[slot];
				if ( entry is null || !entry.Live ) continue;

				entry.DrawVersion = renderer.DrawVersion;
				Resolve( slot );
			}

			changed.RemoveRange( held, changed.Count - held );

			if ( changed.Count > 8192 )
			{
				foreach ( var one in changed ) one?.CullResolved();
				changed.Clear();
			}

			//Hand-placed objects are asked in parallel past a threshold; Changed slots resolve serially after
			if ( objectSlots.Count < ParallelObjects || Environment.ProcessorCount < 2 )
			{
				var marks = SerialMarks();

				for ( var i = 0; i < objectSlots.Count; i++ ) Ask( objectSlots[i], marks );

				Drain( marks );
			}
			else
			{
				AskInParallel();
			}

			//And what a renderer cannot say itself: whether its blend shapes began or stopped being driven;
			//only models with shapes, which are rare, are asked
			var shaped = 0;

			for ( var i = 0; i < morphable.Count; i++ )
			{
				var slot = morphable[i];
				var entry = slot >= 0 && slot < used ? entries[slot] : null;

				if ( entry is null || !entry.Live || ( entry.Renderer is null && entry.Object is null ) ||
					entry.Mesh is null || entry.Mesh.MorphCount == 0 ) continue;

				morphable[shaped++] = slot;

				if ( entry.Morphed != DrivesMorphs( entry ) ) Resolve( slot );
			}

			morphable.RemoveRange( shaped, morphable.Count - shaped );
		}

		//Re-resolve entries wearing a material edited at runtime, or only its first value would be drawn
		if ( Rinten.Material.EditCount != editsSeen )
		{
			editsSeen = Rinten.Material.EditCount;

			for ( var i = 0; i < used; i++ )
			{
				var entry = entries[i];
				if ( entry is null || !entry.Live || entry.Mesh is null ) continue;

				if ( EditedMark( entry ) != entry.EditedSeen ) Resolve( i );
			}
		}

		CoreRenderDevice.ZoneEnd();
		CoreRenderDevice.ZoneBegin( "tables pose" );

		PoseAll();

		CoreRenderDevice.ZoneEnd();

		if ( CoreMaterialTable.Count != materialsSeen )
		{
			materialsSeen = CoreMaterialTable.Count;
			materialsDirty = true;
		}
	}

	private void SyncRenderers( Scene scene )
	{
		//Reused collections and a per-entry stamp instead of a set, since this runs nearly every frame
		var stamp = ++syncStamp;

		foundRenderers.Clear();

		var found = foundRenderers;
		scene.GetAll( found );

		foreach ( var renderer in found )
		{
			if ( !renderer.IsValid() ) continue;

			//Reuse the renderer's cached slot only if it is this table's entry; each drawer has its own tables
			var slot = renderer.CullSlot;

			if ( (uint)slot < (uint)used && entries[slot] is { } stood && ReferenceEquals( stood.Renderer, renderer ) )
			{
				stood.SeenBy = stamp;
				continue;
			}

			if ( byRenderer.TryGetValue( renderer, out slot ) )
			{
				if ( entries[slot] is { } held ) held.SeenBy = stamp;
				continue;
			}

			slot = Take();
			var entry = entries[slot];
			entry.Renderer = renderer;
			entry.Object = null;
			entry.SeenBy = stamp;
			renderer.CullSlot = slot;
			byRenderer[renderer] = slot;
			Resolve( slot );
		}

		goneRenderers.Clear();

		for ( var i = 0; i < used; i++ )
		{
			var entry = entries[i];

			if ( entry is null || !entry.Live || entry.Renderer is null ) continue;
			if ( entry.SeenBy == stamp ) continue;

			goneRenderers.Add( entry.Renderer );
		}

		foreach ( var renderer in goneRenderers )
		{
			if ( !byRenderer.Remove( renderer, out var slot ) ) continue;

			if ( renderer.IsValid() && renderer.CullSlot == slot ) renderer.CullSlot = -1;

			Give( slot );
		}
	}

	//Which sync last saw each entry - see SyncRenderers.
	private int syncStamp;

	private void SyncObjects( SceneWorld world )
	{
		//Kept, for the reason SyncRenderers gives.
		seenObjects.Clear();

		var seen = seenObjects;

		lock ( world.InternalSceneObjects )
		{
			foreach ( var found in world.InternalSceneObjects )
			{
				if ( found is SceneCustomObject or SceneDynamicObject ) continue;
				if ( found.Component is ModelRenderer ) continue;
				if ( found.Model is null ) continue;

				seen.Add( found );
				if ( byObject.ContainsKey( found ) ) continue;

				var slot = Take();
				var entry = entries[slot];
				entry.Renderer = null;
				entry.Object = found;
				found.CullSlot = slot;
				byObject[found] = slot;
				Resolve( slot );
			}
		}

		goneObjects.Clear();

		foreach ( var (found, slot) in byObject )
		{
			if ( seen.Contains( found ) ) continue;
			goneObjects.Add( found );
		}

		foreach ( var found in goneObjects )
		{
			var slot = byObject[found];
			byObject.Remove( found );
			found.CullSlot = -1;
			Give( slot );
		}

		//The flat list the frame walks, built here as the only place membership changes
		objectSlots.Clear();

		foreach ( var (_, slot) in byObject ) objectSlots.Add( slot );
	}

	//Object count above which the walk goes to the thread pool (fork ~2.5 us vs ~150 ns per object)
	private const int ParallelObjects = 384;

	//Objects per worker run: small enough to spread a few hundred, large enough to amortise overhead
	private const int ObjectRun = 128;

	//What one walker of the hand-placed objects wrote down. The two lists are the table's own where
	//the walk is single - there is then nothing to join - and the walker's own where it is not.
	private sealed class Marks
	{
		internal List<int> Dirty;
		internal List<int> Moved;
		internal readonly List<int> Changed = new();
		internal int Asked;
		internal int Placed;
		internal int Numbered;
	}

	private Marks serialMarks;
	private readonly Stack<Marks> spareMarks = new();
	private readonly List<Marks> heldMarks = new();
	private List<int> askingSlots;
	private Func<Marks> takeMarks;
	private Func<int, System.Threading.Tasks.ParallelLoopState, Marks, Marks> askOne;
	private Action<Marks> giveMarks;

	private Marks SerialMarks()
	{
		serialMarks ??= new Marks();
		serialMarks.Dirty = dirty;
		serialMarks.Moved = Moved;
		return serialMarks;
	}

	//Asks one hand-placed object what has become of it since the last frame.
	private void Ask( int slot, Marks marks )
	{
		var entry = entries[slot];

		//Live, and still an object's. A slot given back can be taken again by a renderer
		//before the membership is walked again, and a renderer answers a different question.
		if ( entry is null || !entry.Live || entry.Object is null ) return;

		marks.Asked++;

		var since = SinceSeen( entry );

		if ( ( since & Since.Changed ) != 0 ) { marks.Changed.Add( slot ); return; }

		var moved = ( since & Since.Moved ) != 0;

		if ( moved ) { marks.Placed++; Place( slot, entry, marks.Dirty, marks.Moved ); }

		if ( ( since & Since.Numbers ) != 0 )
		{
			marks.Numbered++;

			//Written down once. A particle that slid and wrote a number is one record to upload, not two,
			//and the list of what to upload is also the log every drawer reads - see the change log below.
			if ( Numbers( slot, entry, moved ? null : marks.Dirty ) ) marks.Changed.Add( slot );
		}
	}

	//Takes in what one walker wrote down, and does the part only this thread may do.
	private void Drain( Marks marks )
	{
		ObjectsAsked += marks.Asked;
		ObjectsPlaced += marks.Placed;
		ObjectsNumbered += marks.Numbered;
		marks.Asked = marks.Placed = marks.Numbered = 0;

		if ( !ReferenceEquals( marks.Dirty, dirty ) ) { dirty.AddRange( marks.Dirty ); marks.Dirty.Clear(); }
		if ( !ReferenceEquals( marks.Moved, Moved ) ) { Moved.AddRange( marks.Moved ); marks.Moved.Clear(); }

		for ( var i = 0; i < marks.Changed.Count; i++ )
		{
			ObjectsResolved++;
			Resolve( marks.Changed[i] );
		}

		marks.Changed.Clear();
	}

	private void AskInParallel()
	{
		//Kept delegates: making three and their closure each frame is four allocations for nothing
		if ( askOne is null )
		{
			takeMarks = () =>
			{
				lock ( spareMarks )
				{
					var marks = spareMarks.Count > 0 ? spareMarks.Pop() : new Marks();
					marks.Dirty ??= new List<int>();
					marks.Moved ??= new List<int>();
					return marks;
				}
			};

			//A run at a time, not a slot at a time: a worker walking contiguous slots reads the table as laid out,
			//where one walking every eighth reads a cache line each
			askOne = ( c, _, marks ) =>
			{
				var slots = askingSlots;
				var from = c * ObjectRun;
				var to = Math.Min( from + ObjectRun, slots.Count );

				for ( var i = from; i < to; i++ ) Ask( slots[i], marks );

				return marks;
			};

			giveMarks = marks => { lock ( heldMarks ) heldMarks.Add( marks ); };
		}

		askingSlots = objectSlots;

		var runs = ( objectSlots.Count + ObjectRun - 1 ) / ObjectRun;

		//On a page's one thread the runs go in order with one set of marks
		Rinten.Utility.Parallel.For( 0, runs, takeMarks, askOne, giveMarks );

		for ( var i = 0; i < heldMarks.Count; i++ )
		{
			var marks = heldMarks[i];
			Drain( marks );
			spareMarks.Push( marks );
		}

		heldMarks.Clear();
	}

	//Adds an instance with no scene object (grass, clutter): resolved once, never polled per frame
	internal int AddStatic( Model model, in Transform transform, Material over, bool castShadows, float lodScale = 1.0f, string lodGroup = null )
	{
		if ( model is null ) return -1;

		var slot = Take();
		var entry = entries[slot];

		entry.Renderer = null;
		entry.Object = null;
		entry.Static = true;
		entry.StaticTransform = transform;
		entry.StaticShadows = castShadows;
		entry.StaticLodScale = lodScale;
		entry.StaticLodGroup = lodGroup;
		entry.Model = model;
		entry.Override = over;
		entry.Tint = Color.White;

		Resolve( slot );

		return slot;
	}

	//Takes one of those out again.
	internal void RemoveStatic( int slot )
	{
		if ( slot < 0 || slot >= used ) return;
		if ( entries[slot] is not { Live: true, Static: true, Run: 0 } ) return;

		Give( slot );
	}

	//Each slot's run as its first slot plus one, nought for none; gaps runs left; runs the card must place again
	private int[] runOf = Array.Empty<int>();
	private readonly List<(int At, int Count)> runHoles = new();
	private readonly HashSet<int> runs = new();
	private readonly HashSet<int> unplaced = new();
	private readonly List<int> placing = new();

	//Adds copies of one model in adjacent slots inside a sphere, placed by the card from one record (PlaceRuns)
	//The first slot, or -1 where the model is not drawn from clusters
	internal int AddRun( Model model, int count, System.Numerics.Vector4 sphere, in Transform frame, IInstanceRun placer, Material over, bool castShadows, float lodScale, string lodGroup )
	{
		if ( model is null || placer is null || count < 1 || !Rinten.Rendering.DrawSettings.Clusters ) return -1;

		var head = TakeRun( count );
		var entry = entries[head] ??= new Entry();

		entry.Live = true;
		entry.Renderer = null;
		entry.Object = null;
		entry.Static = true;
		entry.StaticTransform = frame;
		entry.StaticShadows = castShadows;
		entry.StaticLodScale = lodScale;
		entry.StaticLodGroup = lodGroup;
		entry.Model = model;
		entry.Override = over;
		entry.Tint = Color.White;
		entry.Run = count;
		entry.RunSphere = sphere;
		entry.Placer = placer;

		for ( var slot = head; slot < head + count; slot++ ) runOf[slot] = head + 1;

		runs.Add( head );
		SlotGeneration++;

		Resolve( head );

		return head;
	}

	//Whether a run is drawn from clusters: null while its model is not there yet.
	internal bool? RunDrawn( int head )
	{
		if ( head < 0 || head >= used || entries[head] is not { Live: true, Run: > 0 } entry ) return false;
		if ( entry.Mesh is null ) return null;

		return ( entry.RunFlags & CoreMeshShaders.InstanceDrawn ) != 0;
	}

	//Takes a run out again, every slot of it.
	internal void RemoveRun( int head )
	{
		if ( head < 0 || head >= used || entries[head] is not { Live: true, Run: > 0 } entry ) return;

		var count = entry.Run;

		runs.Remove( head );
		unplaced.Remove( head );

		Give( head, toFree: false );

		//What the card placed in them is wiped too, and they leave their cell.
		for ( var slot = head; slot < head + count; slot++ )
		{
			runOf[slot] = 0;
			records[slot] = default;
			dirty.Add( slot );
		}

		GiveRun( head, count );
	}

	//Slots side by side: out of a gap a run let go of, else past the last.
	private int TakeRun( int count )
	{
		for ( var i = 0; i < runHoles.Count; i++ )
		{
			var (at, room) = runHoles[i];
			if ( room < count ) continue;

			if ( room == count ) runHoles.RemoveAt( i );
			else runHoles[i] = ( at + count, room - count );

			return at;
		}

		var head = used;
		used += count;

		if ( used > records.Length )
		{
			var size = Math.Max( records.Length * 2, used );

			Array.Resize( ref records, size );
			Array.Resize( ref entries, size );
		}

		if ( runOf.Length < records.Length ) Array.Resize( ref runOf, records.Length );

		return head;
	}

	//A run's slots back as a gap, joined to the gaps either side of it.
	private void GiveRun( int at, int count )
	{
		var i = 0;
		while ( i < runHoles.Count && runHoles[i].At < at ) i++;

		runHoles.Insert( i, ( at, count ) );

		if ( i + 1 < runHoles.Count && runHoles[i].At + runHoles[i].Count == runHoles[i + 1].At )
		{
			runHoles[i] = ( runHoles[i].At, runHoles[i].Count + runHoles[i + 1].Count );
			runHoles.RemoveAt( i + 1 );
		}

		if ( i > 0 && runHoles[i - 1].At + runHoles[i - 1].Count == runHoles[i].At )
		{
			runHoles[i - 1] = ( runHoles[i - 1].At, runHoles[i - 1].Count + runHoles[i].Count );
			runHoles.RemoveAt( i );
		}
	}

	//Has the card place every run written since it last did - after Upload, before the cull reads them
	internal void PlaceRuns()
	{
		if ( unplaced.Count == 0 || instanceBuffer == 0 ) return;

		placing.Clear();
		placing.AddRange( unplaced );

		foreach ( var head in placing )
		{
			var entry = head < used ? entries[head] : null;

			if ( entry is not { Live: true, Run: > 0 } || entry.Mesh is null || ( entry.RunFlags & CoreMeshShaders.InstanceDrawn ) == 0 )
			{
				unplaced.Remove( head );
				continue;
			}

			var template = records[head];
			template.Flags = entry.RunFlags;

			var bound = new System.Numerics.Vector4( entry.Mesh.BoundCentre.x, entry.Mesh.BoundCentre.y, entry.Mesh.BoundCentre.z, entry.Mesh.BoundRadius );

			if ( entry.Placer.Place( instanceBuffer, head, entry.Run, in template, bound ) ) unplaced.Remove( head );
		}
	}

	//Where a slot is filed for the cull: a run's first files all of its slots, the rest are left where it put them.
	private void NoteCell( int slot )
	{
		var head = slot < runOf.Length ? runOf[slot] - 1 : -1;

		if ( head < 0 )
		{
			Cells.Note( slot, records[slot], slot < used ? entries[slot] : null );
			return;
		}

		if ( head != slot || entries[slot] is not { } entry ) return;

		Cells.NoteRun( slot, entry.Run, entry.RunSphere, ( entry.RunFlags & CoreMeshShaders.InstanceDrawn ) != 0, entry.CountedChunks / Math.Max( 1, entry.Run ) );
	}

	private int Take()
	{
		int slot;

		if ( free.Count > 0 )
		{
			slot = free[^1];
			free.RemoveAt( free.Count - 1 );
		}
		else
		{
			if ( used >= records.Length )
			{
				Array.Resize( ref records, records.Length * 2 );
				Array.Resize( ref entries, entries.Length * 2 );
			}

			slot = used++;
		}

		entries[slot] ??= new Entry();
		entries[slot].Live = true;
		SlotGeneration++;
		return slot;
	}

	//Which pass something is drawn in by its layers - see RenderOptions. Drawn in none of them,
	//it is left to the shadows: the camera does not draw it and every light still sees it.
	private static DrawLayer LayerOf( bool game, bool overlay, bool afterUi, ref ModelRenderer.ShadowRenderType renderType )
	{
		if ( overlay ) return DrawLayer.Overlay;
		if ( game ) return DrawLayer.Game;
		if ( afterUi ) return DrawLayer.AfterUi;

		if ( renderType != ModelRenderer.ShadowRenderType.Off ) renderType = ModelRenderer.ShadowRenderType.ShadowsOnly;

		return DrawLayer.Game;
	}

	//Counts every slot taken - whether a slot is still the thing it was.
	internal int SlotGeneration { get; private set; }

	//Camera visibility bits by tag; untagged instances are kept unless include is set; lights ignore it
	internal void Hidden( IReadOnlySet<uint> include, IReadOnlySet<uint> exclude, Span<uint> bits )
	{
		bits.Clear();

		for ( var slot = 0; slot < used; ++slot )
		{
			var entry = entries[slot];
			if ( entry is null || !entry.Live ) continue;

			var tags = ( entry.Renderer?.SceneObject ?? entry.Object )?.Tags?.GetTokens();

			var hide = false;

			if ( include.Count > 0 ) hide = tags is null || !include.Overlaps( tags );
			if ( !hide && exclude.Count > 0 && tags is not null ) hide = exclude.Overlaps( tags );

			if ( hide ) bits[slot >> 5] |= 1u << ( slot & 31 );
		}
	}

	private void Give( int slot, bool toFree = true )
	{
		var entry = entries[slot];
		if ( entry is null ) return;

		Loose.Remove( slot );

		if ( entry.MaterialCount > 0 )
		{
			ReleaseMaterials( entry );
			GiveMaterials( entry.MaterialFirst, entry.MaterialCount );
		}

		Uncount( entry );
		ReleasePosed( entry );

		//Wiped, not replaced: Take reuses this object for the slot, avoiding an allocation per removal
		entry.Wipe();
		records[slot] = default;
		dirty.Add( slot );

		if ( toFree ) free.Add( slot );
	}

	//Holes are an unordered set, so a used one is swapped with the last instead of RemoveAt
	private int TakeMaterials( int count )
	{
		for ( var i = 0; i < materialHoles.Count; i++ )
		{
			var (at, room) = materialHoles[i];
			if ( room < count ) continue;

			if ( room == count )
			{
				materialHoles[i] = materialHoles[^1];
				materialHoles.RemoveAt( materialHoles.Count - 1 );
			}
			else materialHoles[i] = ( at + count, room - count );

			return at;
		}

		if ( instanceMaterialsUsed + count > instanceMaterials.Length )
			Array.Resize( ref instanceMaterials, Math.Max( instanceMaterials.Length * 2, instanceMaterialsUsed + count ) );

		var start = instanceMaterialsUsed;
		instanceMaterialsUsed += count;
		return start;
	}

	private void GiveMaterials( int at, int count )
	{
		materialHoles.Add( ( at, count ) );
	}

	//Lets go of the materials an instance's words named, before the words go.
	private void ReleaseMaterials( Entry entry )
	{
		for ( var i = 0; i < entry.MaterialCount; i++ )
			CoreMaterialTable.Release( (int)instanceMaterials[entry.MaterialFirst + i] );
	}

	//What has happened to an instance since the tables last looked at it.
	[Flags]
	internal enum Since
	{
		/// <summary>Nothing worth writing down.</summary>
		Nothing = 0,

		/// <summary>It moved, and that is all - its record needs the new matrix and nothing else.</summary>
		Moved = 1,

		/// <summary>Only its draw numbers changed; they are rewritten in the record without a full resolve.</summary>
		Numbers = 2,

		/// <summary>Something it was resolved from is different; it has to be worked out again.</summary>
		Changed = 4,
	}

	private static Since SinceSeen( Entry entry )
	{
		if ( entry.Renderer is { } renderer )
		{
			if ( !renderer.IsValid() ) return Since.Nothing;

			//One integer, not seven properties - and it is only asked of a renderer that
			//has said it changed, or of one being swept in a rebuild. See the drain in Sync.
			if ( entry.DrawVersion != renderer.DrawVersion )
			{
				entry.DrawVersion = renderer.DrawVersion;
				return Since.Changed;
			}

			if ( entry.Morphed != DrivesMorphs( entry ) ) return Since.Changed;

			//A renderer that moved says so through CullMoved, which Sync has walked
			return Since.Nothing;
		}

		if ( entry.Object is { } found )
		{
			var version = found.Version;
			var attributes = found.AttributesVersion;

			if ( entry.ObjectVersion == version && entry.ObjectAttributes == attributes ) return Since.Nothing;

			//Separate counts tell a move or a numbers change from a real change, so particles skip full resolves
			var cheap = found.DrawVersion == entry.ObjectDrawVersion;
			var wrote = entry.ObjectAttributes != attributes;
			var moved = entry.ObjectVersion != version;

			entry.ObjectVersion = version;
			entry.ObjectDrawVersion = found.DrawVersion;
			entry.ObjectAttributes = attributes;

			if ( !cheap ) return Since.Changed;

			return ( moved ? Since.Moved : Since.Nothing ) | ( wrote ? Since.Numbers : Since.Nothing );
		}

		return Since.Nothing;
	}

	//Whether any blend shape weight is actually set; unused shapes shouldn't force the per-vertex path
	private static bool DrivesMorphs( Entry entry )
	{
		if ( entry.Mesh is null || entry.Mesh.MorphCount == 0 || !CoreRender.DrawsMorphs ) return false;

		return ( ( entry.Renderer?.SceneObject ?? entry.Object ) as SceneModel )?.Morphs is SceneObjectMorphCollection held && held.Any;
	}

	//How many material edits the tables had caught up with - see Material.EditCount.
	private int editsSeen;

	//Where the materials of an entry's parts that code has set values on stand now: which material
	//and how many times it has been set on, run together. Nought where none of them has been.
	private static int EditedMark( Entry entry )
	{
		if ( Rinten.Material.EditCount == 0 || entry.Mesh is null ) return 0;

		var mark = 0;
		var parts = entry.Mesh.Meshes;

		for ( var i = 0; i < parts.Count; i++ )
		{
			if ( Rinten.Material.Edited( parts[i].Material ) is not { core: { } core } edited ) continue;

			mark = HashCode.Combine( mark, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode( edited ), core.Revision );
		}

		return mark;
	}

	//Works an instance out again from what it stands for, and writes its record.
	private void Resolve( int slot )
	{
		var entry = entries[slot];
		ref var record = ref records[slot];

		Uncount( entry );
		Loose.Remove( slot );

		if ( entry.MaterialCount > 0 )
		{
			ReleaseMaterials( entry );
			GiveMaterials( entry.MaterialFirst, entry.MaterialCount );
		}

		entry.MaterialCount = 0;
		entry.Loose = false;
		entry.Skinned = false;
		entry.Morphed = false;

		Model model;
		Transform transform;
		Color tint;
		ulong groups;
		var meshIndex = -1;
		string materialGroup = null;
		Material over;
		var renderType = ModelRenderer.ShadowRenderType.On;
		RenderAttributes attributes = null;
		Guid id = default;

		if ( entry.Renderer is { } renderer )
		{
			if ( !renderer.IsValid() ) { record = default; dirty.Add( slot ); return; }
			//Error marker while the field is empty; the table reads the renderer, not its scene object
			model = renderer.ModelOrError;
			transform = renderer.WorldTransform;
			tint = renderer.Tint;
			groups = renderer.BodyGroups;
			materialGroup = renderer.MaterialGroup;
			over = renderer.MaterialOverride;
			renderType = renderer.RenderType;
			attributes = renderer.SceneObject is { HasAttributes: true } held ? held.AttributesIfAny : null;
			id = renderer.GameObject?.Id ?? default;
			entry.Skinned = renderer is SkinnedModelRenderer;

			var options = renderer.RenderOptions;
			entry.Layer = LayerOf( options.Game, options.Overlay, options.AfterUI, ref renderType );
			entry.RenderType = renderType;
		}
		else if ( entry.Object is { } found )
		{
			model = found.RenderingEnabled ? found.Model : null;
			transform = found.Transform;
			//The colour with alpha forced whole: a tint is a colouring, and its alpha would make the instance
			//translucent - off the cluster path, a draw of its own; see ColorTint
			tint = found.ColorTint.WithAlpha( 1.0f );
			groups = found.MeshGroupMask;
			meshIndex = found.MeshIndex;
			over = found.MaterialOverride;
			attributes = found.HasAttributes ? found.AttributesIfAny : null;
			if ( !found.Flags.CastShadows ) renderType = ModelRenderer.ShadowRenderType.Off;
			entry.NeverStill = found.Flags.NeverStill;
			entry.StandInError = found.StandInError;
			entry.Layer = LayerOf( !found.Flags.ExcludeGameLayer, found.Flags.OverlayLayer,
				found.Flags.HasFlag( Rendering.SceneObjectFlags.UIOverlayLayer ), ref renderType );
			entry.ObjectVersion = found.Version;
			entry.ObjectDrawVersion = found.DrawVersion;
		}
		else if ( entry.Static )
		{
			//Everything it is was said when it was added, and is kept on the entry so a rebuild of the whole table can say it again.
			model = entry.Model;
			transform = entry.StaticTransform;
			tint = entry.Tint;
			groups = ulong.MaxValue;
			over = entry.Override;
			if ( !entry.StaticShadows ) renderType = ModelRenderer.ShadowRenderType.Off;
		}
		else
		{
			record = default;
			dirty.Add( slot );
			return;
		}

		entry.Model = model;
		entry.Tint = tint;
		entry.Groups = groups;
		entry.MeshIndex = meshIndex;
		entry.MaterialGroup = materialGroup;
		entry.Override = over;
		entry.Resources = CoreResources.Generation;


		var mesh = model is null ? null : CoreResources.Mesh( model );
		entry.Mesh = mesh;


		if ( mesh is null )
		{
			record = default;
			dirty.Add( slot );
			return;
		}

		var world = CoreSceneRenderer.WorldOf( transform );
		var scale = transform.Scale;
		var maxScale = MathF.Max( MathF.Abs( scale.x ), MathF.Max( MathF.Abs( scale.y ), MathF.Abs( scale.z ) ) );
		var centre = transform.PointToWorld( mesh.BoundCentre );
		var radius = mesh.BoundRadius * maxScale;

		var chosen = mesh.Chosen( groups );

		//The materials being driven from code as they stand now - see the sweep in Sync, which asks this again to know when that has moved.
		entry.EditedSeen = EditedMark( entry );
		var groupIndex = CoreSceneRenderer.MaterialGroupIndex( mesh, materialGroup );

		var overTexture = 0ul;
		bool? overTwoSided = null;
		bool? overTranslucent = null;
		bool? overMasked = null;
		float? overCutoff = null;
		string overShader = null;
		string overCombo = null;
		string overPath = null;
		var overTint = Color.White;

		if ( over?.core is not null )
		{
			overTexture = CoreResources.Live( over );

			if ( overTexture != 0 )
			{
				//What its shader is built with rather than fields of its own - see CoreResources.LiveFlags.
				var liveFlags = CoreResources.LiveFlags( over );
				overTwoSided = liveFlags.TwoSided;
				overTranslucent = liveFlags.Translucent;

				//Override cut-out flag too, or the cluster depth pre-pass writes solid depth and leaves a black hole
				overMasked = liveFlags.Masked;
				overCutoff = CoreResources.LiveCutoff( over );
				var named = over.ShaderName;
				if ( !string.IsNullOrWhiteSpace( named ) && named != "invalid" ) overShader = named;
				overCombo = CoreResources.LiveCombo( over );
				overPath = over.ResourcePath;

				//An override replaces the material, so its tint too - otherwise a flat-coloured override would draw
				//in whatever the model wore, or white
				overTint = CoreResources.LiveTint( over );
			}
		}

		//A mesh the shader reads for itself is drawn the classic way; a posed skeleton or a
		//driven face is posed on the card and drawn out of its clusters like everything else.
		var animating = entry.Skinned && mesh.IsSkinned;

		//A skinned thing goes through the clusters where it can: its vertices are posed each frame
		entry.Morphed = DrivesMorphs( entry );

		//On the short list of things worth asking about shapes, once; the poll takes it off when it has none
		if ( ( entry.Renderer is not null || entry.Object is not null ) && mesh.MorphCount > 0 && !morphable.Contains( slot ) )
			morphable.Add( slot );

		//Skinned models use clusters too; only references or combos in their own bag force the classic path
		var ownAttributes = attributes is not null &&
			( attributes.HoldsReference || attributes.Combos.Count > 0 );

		//Blend shapes are posed in the skin pass before bones, with or without a skeleton
		var bonesPose = animating && entry.Renderer is SkinnedModelRenderer;
		var shapesPose = entry.Morphed && mesh.ShapeDeltas != 0 && mesh.ShapeRuns is not null;

		//A ModelDeformer reshapes it in the same pass, skeleton or not
		var deformPose = entry.Renderer is { DeformationVolumes: > 0 };

		var canPose = Rinten.Rendering.DrawSettings.Clusters
			&& ( bonesPose || shapesPose || deformPose ) && mesh.ClusterCount > 0 && mesh.MeshSlot >= 0 && !ownAttributes
			&& mesh.PoolVertexCount > 0 && mesh.Bones.Length <= MostBones;

		var posedNow = canPose && EnsurePosed( entry, mesh, bonesPose, shapesPose );
		if ( !posedNow ) ReleasePosed( entry );

		//On the short list of things PoseAll walks, once. Taken off again by the walk when it stops being posed.
		if ( posedNow && !posedSlots.Contains( slot ) ) posedSlots.Add( slot );


		if ( posedNow && entry.Renderer is { } posedRenderer )
		{
			posedRenderer.CullSphere( out var posedCentre, out var posedRadius );
			if ( posedRadius > 0.0f ) { centre = posedCentre; radius = posedRadius; }
		}

		//A layer drawn after the world goes the classic way, from the frame's queue; the cull never takes it
		var loose = !Rinten.Rendering.DrawSettings.Clusters || ( animating && !posedNow ) ||
			mesh.MeshSlot < 0 || mesh.Pulled || ( entry.Morphed && !posedNow ) || ownAttributes ||
			entry.Layer != DrawLayer.Game;

		var cullGrow = 0.0f;
		var count = mesh.Meshes.Count;
		var first = count > 0 ? TakeMaterials( count ) : 0;
		var anyLoose = false;
		var anyDrawn = false;
		var oneSided = 0;
		var twoSided = 0;
		var cutOut = 0;
		var moving = 0;

		for ( var i = 0; i < count; i++ )
		{
			var part = mesh.Meshes[i];
			var shows = meshIndex >= 0 ? i == meshIndex : CoreSceneRenderer.SceneMesh.Shows( part, chosen );

			var drawn = groupIndex < 0 ? part : CoreResources.WithGroupMaterial( part, groupIndex );

			if ( overTexture != 0 )
			{
				drawn = drawn with
				{
					Texture = overTexture,
					TwoSided = overTwoSided ?? drawn.TwoSided,
					Translucent = overTranslucent ?? drawn.Translucent,
					Masked = overMasked ?? drawn.Masked,
					Cutoff = overCutoff ?? drawn.Cutoff,
					Shader = overShader ?? drawn.Shader,
					Combo = overCombo,
					Material = overPath ?? drawn.Material,
					Tint = overTint,
				};
			}
			else if ( overShader is not null )
			{
				drawn = drawn with { Shader = overShader, Combo = overCombo };
			}
			else if ( Rinten.Material.Edited( drawn.Material ) is { } edited && CoreResources.Live( edited ) is var live && live != 0 )
			{
				//The material as code has set it, rather than as the file was read when the mesh was
				//built. The same material otherwise: its shader, its features and its tint are the file's.
				drawn = drawn with { Texture = live };
			}

			var partTint = drawn.Tint * tint;
			if ( partTint.a < 1.0f && !drawn.Translucent ) drawn = drawn with { Translucent = true };


			var material = CoreMaterialTable.Index( in drawn, shows );
			instanceMaterials[first + i] = (uint)material;


			cullGrow = MathF.Max( cullGrow, drawn.Cull.x );

			if ( !shows ) continue;
			anyDrawn = true;

			//Ask live: the real drawing shader is only known after compiling (see CoreResources.StandsIn)
			if ( drawn.Translucent || !CoreResources.Shades( drawn.Shader ) )
			{
				anyLoose = true;
				continue;
			}

			//Into the list the cull puts this part's clusters in - alpha before two-sided, see CullCluster; a part
			//moving its own vertices is counted apart and writes nothing into the runs those size
			var triangles = part.IndexCount / 3;
			if ( CoreSceneRenderer.MovesVertices( drawn.Shader, drawn.Combo ) ) moving += triangles;
			else if ( drawn.Masked ) cutOut += triangles;
			else if ( drawn.TwoSided ) twoSided += triangles;
			else oneSided += triangles;
		}

		if ( looseWhy )
		{
			if ( entry.Loose || anyLoose )
			{
				if ( !Rinten.Rendering.DrawSettings.Clusters ) looseCounts[0]++;
				else if ( animating && !posedNow ) looseCounts[1]++;
				else if ( mesh.MeshSlot < 0 ) looseCounts[2]++;
				else if ( mesh.Pulled ) looseCounts[3]++;
				else if ( entry.Morphed && !posedNow ) looseCounts[4]++;
				else if ( ownAttributes ) looseCounts[5]++;
				else looseCounts[6]++;
			}
			else looseCounts[7]++;

			if ( ++looseSaid % 200 == 0 )
				Log.Info( $"loose why (since the start): settings {looseCounts[0]}, animating {looseCounts[1]}, no slot {looseCounts[2]}, pulled {looseCounts[3]}, morphed {looseCounts[4]}, attributes {looseCounts[5]}, a part of it {looseCounts[6]}, clustered {looseCounts[7]}" );
		}


		entry.MaterialFirst = first;
		entry.MaterialCount = count;
		entry.OneSided = oneSided;
		entry.TwoSided = twoSided;
		entry.CutOut = cutOut;
		entry.Moving = moving;
		entry.Loose = loose || ( anyLoose && mesh.ClusterCount == 0 );
		materialsDirty = true;

		if ( entry.Loose || anyLoose ) Loose.Add( slot );

		var square = MathF.Abs( scale.x - scale.y ) < 1e-3f * MathF.Abs( scale.x ) &&
			MathF.Abs( scale.x - scale.z ) < 1e-3f * MathF.Abs( scale.x ) && scale.x > 0.0f && scale.y > 0.0f && scale.z > 0.0f;

		var flags = 0u;
		if ( anyDrawn && !entry.Loose && mesh.ClusterCount > 0 && mesh.MeshSlot >= 0 ) flags |= CoreMeshShaders.InstanceDrawn;
		if ( posedNow && !entry.Loose ) flags |= CoreMeshShaders.InstancePosed;
		if ( renderType == ModelRenderer.ShadowRenderType.Off ) flags |= CoreMeshShaders.InstanceNoShadow;
		if ( renderType == ModelRenderer.ShadowRenderType.ShadowsOnly ) flags |= CoreMeshShaders.InstanceShadowOnly;
		if ( square ) flags |= CoreMeshShaders.InstanceSquare;
		//With a bake, what is not static is never still: the bake holds only the static, so it casts into the movers' maps
		if ( entry.NeverStill || ( Movable && entry.Renderer is { } baked && baked.GameObject is { IsStatic: false } ) )
			flags |= CoreMeshShaders.InstanceNeverStill;

		//Its place in the baked atlas, read as g_object: a renderer's (ModelRenderer.LightmapRect), else its object's
		//(a MeshComponent's and its cells'); an object's own numbers go in the said ones below
		var numbers = entry.Renderer is { } lit ? lit.LightmapRect : entry.Object?.LightmapRect ?? default;

		Span<float> said = stackalloc float[CoreShaderParams.DrawSlots * 4];
		said.Clear();

		entry.Says = null;
		entry.SaysPlan = null;
		entry.SaysKnown = false;

		if ( attributes is not null && count > 0 )
		{
			var says = CoreResources.Declared( overShader ?? mesh.Meshes[0].Shader );

			entry.SaysKnown = true;

			if ( CoreShaderParams.AnyAttribute( says ) )
			{
				CoreShaderParams.Draw( said, says, attributes );

				//Kept, so that the next frame's write of one of those numbers needs nothing of the resolve
				//above - see Numbers. The plan beside them, so that write is not a weak-table lookup either.
				entry.Says = says;
				entry.SaysPlan = CoreShaderParams.PlanOf( says );
			}
		}
		else if ( count > 0 && CoreResources.Declared( overShader ?? mesh.Meshes[0].Shader ) is { } declared && CoreShaderParams.AnyAttribute( declared ) )
		{
			//Nothing sets them, so they are what the shader declares: nought would be a particle's emission off, drawing it black
			CoreShaderParams.PlanOf( declared ).Defaults.AsSpan( 0, said.Length ).CopyTo( said );
		}

		var previous = record.World;
		if ( previous == default ) previous = world;

		//Resolved again counts as moved for a shadow page baked with it - its shape may differ; see CoreCasters
		var moves = record.MoveCount + 1;

		record = new InstanceRecord
		{
			MoveCount = moves,
			World = world,
			Prev = previous,
			Sphere = new System.Numerics.Vector4( centre.x, centre.y, centre.z, radius ),
			Mesh = (uint)Math.Max( 0, mesh.MeshSlot ),
			MaterialFirst = (uint)first,
			Flags = flags,
			CullGrow = cullGrow,
			Numbers = numbers,
			Tint = new System.Numerics.Vector4( tint.r, tint.g, tint.b, tint.a ),
			Said0 = new System.Numerics.Vector4( said[0], said[1], said[2], said[3] ),
			Said1 = new System.Numerics.Vector4( said[4], said[5], said[6], said[7] ),
			PosedFirst = posedNow ? (uint)entry.PosedAt : 0,
			PosedSource = (uint)Math.Max( 0, mesh.PoolVertexAt ),
			PaletteFirst = posedNow ? (uint)Math.Max( 0, entry.PaletteAt ) : 0,
			PaletteCount = posedNow ? (uint)entry.PaletteCount : 0,
			LodScale = entry.Static ? entry.StaticLodScale : entry.Renderer?.LodBias ?? 1.0f,
			LodGroup = LodGroupOf( entry ),
		};

		//A run is drawn from clusters or not at all, once the card placed it: each copy's record starts from its own
		if ( entry.Run > 0 )
		{
			Loose.Remove( slot );
			entry.RunFlags = flags;
			record.Flags = flags & ~CoreMeshShaders.InstanceDrawn;
			record.Sphere = entry.RunSphere;
		}


		Count( entry );
		dirty.Add( slot );
	}

	//Rewrites only an instance's draw numbers from its bag, using the shader declarations kept at resolve
	private void Numbers( int slot )
	{
		var entry = entries[slot];

		if ( entry is not null && Numbers( slot, entry, dirty ) ) Resolve( slot );
	}

	private bool Numbers( int slot, Entry entry, List<int> dirtyInto )
	{
		if ( !entry.Live ) return false;

		//A bag that was not there when this was last resolved: resolve it, so the shader's
		//declarations are known, and the frame after this one takes the cheap path.
		if ( !entry.SaysKnown ) return true;

		if ( entry.SaysPlan is null ) return false;
		if ( entry.Object is not { } found || !found.HasAttributes ) return false;
		if ( found.AttributesIfAny is not { } attributes ) return false;

		Span<float> said = stackalloc float[CoreShaderParams.DrawSlots * 4];

		CoreShaderParams.Draw( said, entry.SaysPlan, attributes );

		ref var record = ref records[slot];

		record.Said0 = new System.Numerics.Vector4( said[0], said[1], said[2], said[3] );
		record.Said1 = new System.Numerics.Vector4( said[4], said[5], said[6], said[7] );

		dirtyInto?.Add( slot );
		return false;
	}

	private void Place( int slot )
	{
		var entry = entries[slot];

		if ( entry is not null ) Place( slot, entry, dirty, Moved );
	}

	//The two lists are handed in so that several of these can run at once - a walk of a scene's hand-placed objects is one
	//of the few things in a frame with nothing shared between its items but where it writes down what it touched. See Sync.
	private void Place( int slot, Entry entry, List<int> dirtyInto, List<int> movedInto )
	{
		if ( !entry.Live || entry.Mesh is null ) return;

		Transform transform;

		if ( entry.Renderer is { } renderer )
		{
			if ( !renderer.IsValid() ) return;
			transform = renderer.WorldTransform;
		}
		else if ( entry.Object is { } found )
		{
			transform = found.Transform;

			//The colour rides along: for a hand-placed object it is on the cheap side of the line with the transform,
			//and its alpha is forced whole so it cannot decide which material this is - see SceneObject.DrawVersion.
			var tint = found.ColorTint;
			records[slot].Tint = new System.Numerics.Vector4( tint.r, tint.g, tint.b, 1.0f );
			entry.Tint = tint;
		}
		else return;

		ref var record = ref records[slot];
		var scale = transform.Scale;
		var maxScale = MathF.Max( MathF.Abs( scale.x ), MathF.Max( MathF.Abs( scale.y ), MathF.Abs( scale.z ) ) );
		var centre = transform.PointToWorld( entry.Mesh.BoundCentre );

		record.Prev = record.World;
		record.World = CoreSceneRenderer.WorldOf( transform );
		record.Sphere = new System.Numerics.Vector4( centre.x, centre.y, centre.z, entry.Mesh.BoundRadius * maxScale );
		record.MoveCount++;

		//Moving until it has held still a while (ClearMoving): it goes into the maps drawn every frame, never
		//a page, and the pages it left are baked again - see CoreCasters
		record.Moving = 1;

		if ( entry.Posed ) PosedSphere( slot );
		dirtyInto.Add( slot );
		movedInto.Add( slot );
	}

	//Which turn of the loop the movers were last worked out for - see Sync.
	private long syncedAt = -1;

	//Which baking of the project's level-of-detail groups the records name rows of.
	private int lodGroups = -1;

	//The instance's LOD group word: its renderer's or scatter's group, else its model's; 0 if unknown
	private static uint LodGroupOf( Entry entry )
	{
		var named = entry.Static ? entry.StaticLodGroup : entry.Renderer?.LodGroup;
		if ( string.IsNullOrWhiteSpace( named ) ) named = entry.Model?.LodGroup;

		return Rinten.Rendering.LodGroups.Find( named );
	}

	private void Regroup()
	{
		for ( var slot = 0; slot < used; slot++ )
		{
			if ( entries[slot] is not { Live: true } entry ) continue;

			var group = LodGroupOf( entry );
			if ( records[slot].LodGroup == group ) continue;

			records[slot].LodGroup = group;
			dirty.Add( slot );
		}
	}

	//The instances that moved this frame, for the passes that keep what they drew.
	internal readonly List<int> Moved = new();

	//When each thing still marked as moving was last seen to move.
	private readonly Dictionary<int, double> movingSince = new();
	private readonly List<int> cameToRest = new();

	//Seconds still before a mover counts as stopped; per-frame checks misread gaps between its updates
	private const double Rest = 0.1;

	//Takes the moving mark off whatever has held still long enough. First thing in a gather, before anything sets it again.
	private void ClearMoving()
	{
		//Moved still holds the previous turn's movers - Sync empties it after this - so this is where they are stamped.
		var now = RealTime.NowDouble;

		foreach ( var slot in Moved ) movingSince[slot] = now;

		if ( movingSince.Count == 0 ) return;

		cameToRest.Clear();

		foreach ( var (slot, when) in movingSince )
			if ( now - when >= Rest ) cameToRest.Add( slot );

		foreach ( var slot in cameToRest )
		{
			movingSince.Remove( slot );

			if ( slot < 0 || slot >= records.Length ) continue;
			if ( records[slot].Moving == 0 ) continue;

			records[slot].Moving = 0;
			dirty.Add( slot );
		}
	}

	internal uint MaterialWordAt( int at ) => at >= 0 && at < instanceMaterialsUsed ? instanceMaterials[at] : 0;

	//Adds an instance drawn from its clusters to the sums the cull's lists are sized by, and remembers what it added.
	private void Count( Entry entry )
	{
		if ( entry is null ) return;

		//Never twice: whatever it added before goes first.
		Uncount( entry );

		if ( entry.Mesh is null || entry.Loose || entry.Mesh.ClusterCount == 0 ) return;

		var copies = Math.Max( 1, entry.Run );

		entry.CountedChunks = ChunksOf( entry.Mesh ) * copies;
		entry.CountedFinest = entry.Mesh.ClusterCount * copies;
		entry.CountedOneSided = entry.OneSided * copies;
		entry.CountedTwoSided = entry.TwoSided * copies;
		entry.CountedCutOut = entry.CutOut * copies;
		entry.CountedMoving = entry.Moving * copies;

		ChunkCount += entry.CountedChunks;
		FinestClusters += entry.CountedFinest;
		kindTriangles[0] += entry.CountedOneSided;
		kindTriangles[1] += entry.CountedTwoSided;
		kindTriangles[2] += entry.CountedCutOut;
		movingTriangles += entry.CountedMoving;
	}

	//The work items an instance of a mesh can hand the cluster stage in one view: its largest level, the
	//one level a chain hands over - or all of its clusters for a model cut on load, which is one level.
	private static int ChunksOf( CoreSceneRenderer.SceneMesh mesh )
	{
		if ( !mesh.Chain || mesh.LevelTallies is null ) return ( mesh.ClusterCount + 63 ) / 64;

		var most = 0L;
		foreach ( var level in mesh.LevelTallies ) most = Math.Max( most, level.Clusters );

		//Each part's run of a level ends in a part-filled item.
		return (int)( ( most + 63 ) / 64 ) + mesh.Meshes.Count;
	}

	//Takes back what Count added for an instance - see Entry.CountedChunks.
	private void Uncount( Entry entry )
	{
		if ( entry is null ) return;

		ChunkCount -= entry.CountedChunks;
		FinestClusters -= entry.CountedFinest;
		kindTriangles[0] -= entry.CountedOneSided;
		kindTriangles[1] -= entry.CountedTwoSided;
		kindTriangles[2] -= entry.CountedCutOut;
		movingTriangles -= entry.CountedMoving;

		entry.CountedChunks = entry.CountedFinest = entry.CountedOneSided = entry.CountedTwoSided =
			entry.CountedCutOut = entry.CountedMoving = 0;
	}

	private readonly long[] kindTriangles = new long[3];
	private long movingTriangles;

	//The sum over drawn instances of the triangles each of the camera's three lists can be handed at most -
	//one-sided, two-sided, cut out. What the lists' runs of the index buffer are sized by; see Entry.OneSided.
	internal long KindTriangles( int kind ) => kind >= 0 && kind < 3 ? Math.Max( 0L, kindTriangles[kind] ) : 0L;

	//And the moving parts' triangles, in those lists but not their runs - what the bins' run needs on top
	internal long MovingTriangles => Math.Max( 0L, movingTriangles );

	//Keeps a posed entry's pool and palette ranges for bones and/or shapes, freeing what's not needed
	private static bool EnsurePosed( Entry entry, CoreSceneRenderer.SceneMesh mesh, bool withBones, bool withShapes )
	{
		var count = mesh.PoolVertexCount;
		var bones = withBones ? Math.Min( MostBones, mesh.Bones.Length ) : 0;
		var shapes = withShapes ? mesh.MorphCount : 0;
		var sums = withShapes ? count : 0;

		//A pool dropped under it: the range is nobody's to give back.
		if ( entry.PosedAt >= 0 && entry.PosedAt + entry.PosedCount > CoreGeometryPool.Vertices.Used ) entry.PosedAt = -1;
		if ( entry.PaletteAt >= 0 && entry.PaletteAt + entry.PaletteCount > Palettes.Used ) entry.PaletteAt = -1;
		if ( entry.ShapesAt >= 0 && entry.ShapesAt + entry.ShapesCount > ShapeSums.Used ) entry.ShapesAt = -1;
		if ( entry.WasAt >= 0 && entry.WasAt + entry.PosedCount > Was.Used ) entry.WasAt = -1;

		if ( entry.PosedAt >= 0 && entry.PosedCount != count )
		{
			CoreGeometryPool.Vertices.Give( entry.PosedAt, entry.PosedCount );
			if ( entry.WasAt >= 0 ) Was.Give( entry.WasAt, entry.PosedCount );
			entry.PosedAt = entry.WasAt = -1;
		}
		if ( entry.PaletteAt >= 0 && entry.PaletteCount != bones ) { Palettes.Give( entry.PaletteAt, entry.PaletteCount ); entry.PaletteAt = -1; entry.PaletteCount = 0; entry.PoseHash = 0; }
		if ( entry.ShapesAt >= 0 && entry.ShapesCount != sums ) { ShapeSums.Give( entry.ShapesAt, entry.ShapesCount ); entry.ShapesAt = -1; entry.ShapesCount = 0; entry.PoseHash = 0; }

		//The same for where the posed vertices go: a range that moved holds nothing posed yet, so the
		//skin pass has to pose into it whether or not the skeleton did anything - see Entry.PoseHash.
		if ( entry.PosedAt < 0 ) { entry.PosedAt = CoreGeometryPool.Vertices.Take( count ); entry.PosedCount = count; entry.PoseHash = 0; entry.SkinnedAt = -1; }

		//Without room it only loses its motion, never its pose
		if ( entry.PosedAt >= 0 && entry.WasAt < 0 ) entry.WasAt = Was.Take( count );

		//A new range holds whatever was in it, so the old one's pose says nothing - see Entry.PoseHash
		if ( bones > 0 && entry.PaletteAt < 0 )
		{
			entry.PaletteAt = Palettes.Take( bones );
			entry.PaletteCount = bones;
			entry.PoseHash = 0;
		}

		if ( sums > 0 && entry.ShapesAt < 0 )
		{
			entry.ShapesAt = ShapeSums.Take( sums );
			entry.ShapesCount = sums;
			entry.PoseHash = 0;
		}

		if ( shapes > 0 && ( entry.ShapeWeights is null || entry.ShapeWeights.Length != shapes ) ) entry.ShapeWeights = new float[shapes];
		if ( shapes == 0 ) entry.ShapeWeights = null;

		entry.Posed = entry.PosedAt >= 0 && ( bones == 0 || entry.PaletteAt >= 0 ) && ( sums == 0 || entry.ShapesAt >= 0 );
		if ( !entry.Posed ) ReleasePosed( entry );
		return entry.Posed;
	}

	private static void ReleasePosed( Entry entry )
	{
		if ( entry is null ) return;
		if ( entry.PosedAt >= 0 && entry.PosedAt + entry.PosedCount <= CoreGeometryPool.Vertices.Used ) CoreGeometryPool.Vertices.Give( entry.PosedAt, entry.PosedCount );
		if ( entry.PaletteAt >= 0 && entry.PaletteAt + entry.PaletteCount <= Palettes.Used ) Palettes.Give( entry.PaletteAt, entry.PaletteCount );
		if ( entry.ShapesAt >= 0 && entry.ShapesAt + entry.ShapesCount <= ShapeSums.Used ) ShapeSums.Give( entry.ShapesAt, entry.ShapesCount );
		if ( entry.WasAt >= 0 && entry.WasAt + entry.PosedCount <= Was.Used ) Was.Give( entry.WasAt, entry.PosedCount );
		if ( entry.DeformAt >= 0 && entry.DeformAt + entry.DeformCount <= Deformations.Used ) Deformations.Give( entry.DeformAt, entry.DeformCount );
		entry.DeformAt = -1;
		entry.DeformCount = entry.DeformVolumes = entry.DeformAnchors = 0;
		entry.PosedAt = -1;
		entry.WasAt = -1;
		entry.PoseHash = 0;
		entry.PaletteAt = -1;
		entry.PosedCount = 0;
		entry.PaletteCount = 0;
		entry.ShapesAt = -1;
		entry.ShapesCount = 0;
		entry.ShapeWeights = null;
		entry.Posed = false;
	}

	//The renderer's own sphere for a posed thing - the mesh's is the bind pose.
	private void PosedSphere( int slot )
	{
		var entry = entries[slot];
		if ( entry?.Renderer is not { } renderer || !renderer.IsValid() ) return;

		renderer.CullSphere( out var centre, out var radius );
		if ( radius <= 0.0f ) return;

		ref var record = ref records[slot];
		var was = record.Sphere;
		record.Sphere = new System.Numerics.Vector4( centre.x, centre.y, centre.z, radius );
		if ( record.Sphere != was ) dirty.Add( slot );
	}

	//Whether anything in the scene is posed on the card this frame.
	internal bool AnyPosed { get; private set; }

	//Posed instances and how many are still in bind pose, which usually means a failed bone-name match
	internal int Posed, PosedAtRest;

	//Hashes pose words to tell a moved pose from a still one (see Entry.PoseHash)
	private static ulong PoseHashOf( ReadOnlySpan<ulong> words )
	{
		//FNV-1a over words; nought means nothing posed, so a hash landing there is nudged off
		var hash = 14695981039346656037ul;

		for ( var i = 0; i < words.Length; i++ )
		{
			hash ^= words[i];
			hash *= 1099511628211ul;
		}

		return hash == 0 ? 1ul : hash;
	}

	//Its ModelDeformer stack into the pool for the skin pass, and a hash of it for the pose: nought where it has none
	private ulong Deform( Entry entry )
	{
		var renderer = entry.Renderer;
		var volumes = renderer is null ? 0 : renderer.DeformationVolumes;
		var anchors = volumes > 0 && entry.PaletteAt >= 0 && renderer is SkinnedModelRenderer skinned
			? skinned.DeformationAnchors( entry.Mesh.Bones ) : [];
		var count = ( volumes * ModelDeformer.Words ) + anchors.Length;

		if ( entry.DeformAt >= 0 && entry.DeformAt + entry.DeformCount > Deformations.Used ) entry.DeformAt = -1;

		if ( entry.DeformAt >= 0 && entry.DeformCount != count )
		{
			Deformations.Give( entry.DeformAt, entry.DeformCount );
			entry.DeformAt = -1;
		}

		if ( count > 0 && entry.DeformAt < 0 ) entry.DeformAt = Deformations.Take( count );

		entry.DeformCount = entry.DeformAt >= 0 ? count : 0;
		entry.DeformVolumes = entry.DeformAt >= 0 ? volumes : 0;
		entry.DeformAnchors = entry.DeformAt >= 0 ? anchors.Length : 0;
		if ( entry.DeformCount == 0 ) return 0;

		var stack = renderer.DeformationWords;
		deformQueue.Add( ( entry.DeformAt, stack, volumes * ModelDeformer.Words ) );
		if ( anchors.Length > 0 ) deformQueue.Add( ( entry.DeformAt + ( volumes * ModelDeformer.Words ), anchors, anchors.Length ) );

		return PoseHashOf( MemoryMarshal.Cast<System.Numerics.Vector4, ulong>( stack.AsSpan( 0, volumes * ModelDeformer.Words ) ) );
	}

	private void PoseAll()
	{
		paletteQueue.Clear();
		deformQueue.Clear();
		AnyPosed = false;
		Posed = 0;
		PosedAtRest = 0;

		var kept = 0;

		for ( var p = 0; p < posedSlots.Count; p++ )
		{
			var i = posedSlots[p];
			var entry = i >= 0 && i < used ? entries[i] : null;

			if ( entry is null || !entry.Live || !entry.Posed || entry.Mesh is null ) continue;

			var withBones = entry.PaletteAt >= 0;
			var skin = entry.Renderer as SkinnedModelRenderer;
			if ( withBones && ( skin is null || !skin.IsValid() ) ) { ReleasePosed( entry ); continue; }

			posedSlots[kept++] = i;

			var hash = 0ul;

			if ( withBones )
			{
				var pose = CoreSceneRenderer.PoseBones( entry.Mesh, skin.SceneModel, skin.WorldTransform );
				if ( pose is null || pose.Length == 0 ) continue;

				paletteQueue.Add( ( entry.PaletteAt, pose ) );

				//And whether this skeleton is where it was last frame. The palette is written either way - it is a few kilobytes and the
				//pool under it may have moved - but the skin pass does not pose again a pose that did not move. See CoreMeshRender.Skin.
				hash = PoseHashOf( MemoryMarshal.Cast<System.Numerics.Matrix4x4, ulong>( pose.AsSpan() ) );

				Posed++;
				if ( CoreSceneRenderer.AtBindPose( pose ) ) PosedAtRest++;
			}

			//The shapes the same way: posed again only when a weight moved. Nothing set is every weight
			//nought - the model as it was modelled - which is what a face nobody drives any more is.
			if ( entry.ShapesAt >= 0 && entry.ShapeWeights is { } weights )
			{
				var held = ( entry.Renderer?.SceneObject ?? entry.Object ) as SceneModel;
				if ( held?.Morphs is not SceneObjectMorphCollection shapes || !shapes.WeightsInto( weights ) ) Array.Clear( weights );

				var shaped = PoseHashOf( MemoryMarshal.Cast<float, ulong>( weights.AsSpan( 0, weights.Length & ~1 ) ) );
				if ( ( weights.Length & 1 ) != 0 ) shaped ^= BitConverter.SingleToUInt32Bits( weights[^1] ) * 0x9E3779B97F4A7C15ul;

				hash = ( hash * 1099511628211ul ) ^ shaped;
			}

			hash = ( hash * 1099511628211ul ) ^ Deform( entry );

			PosedSphere( i );
			AnyPosed = true;

			entry.PoseHash = hash == 0 ? 1ul : hash;
		}

		posedSlots.RemoveRange( kept, posedSlots.Count - kept );
	}

	//Log of changed slots; each drawer keeps its own position, and one that falls behind rescans all
	private readonly List<int> changeLog = new();
	private long changeLogStart;
	private const int ChangeLogMost = 1 << 16;

	//The slots changed since a reader's place, and the place moved on; false where it cannot be told - first
	//time, fell out of the log, or a whole rewrite - and should look at every slot
	internal bool ChangesSince( ref long seen, List<int> into )
	{
		into.Clear();

		var end = changeLogStart + changeLog.Count;

		if ( seen < changeLogStart )
		{
			seen = end;
			return false;
		}

		for ( var i = (int)( seen - changeLogStart ); i < changeLog.Count; i++ ) into.Add( changeLog[i] );

		seen = end;
		return true;
	}

	internal void Upload()
	{
		if ( used == 0 ) return;

		if ( instanceBuffer == 0 || instanceBufferRecords < used )
		{
			if ( instanceBuffer != 0 ) CoreRenderDevice.DestroyBuffer( instanceBuffer );
			instanceBufferRecords = Math.Max( 256, records.Length );
			instanceBuffer = CoreRenderDevice.ReserveBuffer( (long)instanceBufferRecords * RecordBytes );
			allDirty = true;
		}

		if ( instanceBuffer != 0 )
		{
			var bytes = MemoryMarshal.AsBytes( records.AsSpan( 0, used ) );

			//What is written here over a run's slots wipes what the card placed in them.
			if ( allDirty || dirty.Count * 2 >= used ) unplaced.UnionWith( runs );
			else foreach ( var slot in dirty ) if ( runs.Contains( slot ) ) unplaced.Add( slot );

			if ( allDirty )
			{
				CoreRenderDevice.FrameWrite( instanceBuffer, 0, bytes );
			}
			else if ( dirty.Count * 2 >= used )
			{
				//Most of the table changed: write it all at once rather than as many small copies with barriers
				CoreRenderDevice.FrameWrite( instanceBuffer, 0, bytes );
			}
			else if ( dirty.Count > 0 )
			{
				dirty.Sort();

				var at = 0;
				while ( at < dirty.Count )
				{
					var first = dirty[at];
					var last = first;
					at++;

					//Merge only adjacent slots; copying gaps cost more than the (now shared) barriers saved
					while ( at < dirty.Count && dirty[at] <= last + 1 )
					{
						last = dirty[at];
						at++;
					}

					CoreRenderDevice.FrameWrite( instanceBuffer, (long)first * RecordBytes,
						bytes.Slice( first * RecordBytes, ( ( last - first ) + 1 ) * RecordBytes ) );
				}
			}
		}

		//What changed, into the log the drawers read - see ChangesSince. The whole table written again is a change nobody
		//can be told slot by slot, so the log starts again past every reader's place and each looks at everything.
		if ( allDirty )
		{
			changeLogStart += changeLog.Count + 1;
			changeLog.Clear();
		}
		else if ( dirty.Count > 0 )
		{
			changeLog.AddRange( dirty );

			if ( changeLog.Count > ChangeLogMost )
			{
				var drop = changeLog.Count - ( ChangeLogMost / 2 );
				changeLog.RemoveRange( 0, drop );
				changeLogStart += drop;
			}
		}

		//The cells the cull walks, from what changed - see CoreInstanceCells.
		if ( allDirty )
		{
			Cells.Reset();
			for ( var slot = 0; slot < used; slot++ ) NoteCell( slot );
		}
		else
		{
			foreach ( var slot in dirty ) NoteCell( slot );
		}

		Cells.Update( used );

		allDirty = false;
		dirty.Clear();

		foreach ( var (at, pose) in paletteQueue )
		{
			if ( Palettes.Buffer == 0 ) break;
			CoreRenderDevice.FrameWrite( Palettes.Buffer, at * 64, MemoryMarshal.AsBytes( pose.AsSpan() ) );
		}

		paletteQueue.Clear();

		foreach ( var (at, stack, count) in deformQueue )
		{
			if ( Deformations.Buffer == 0 ) break;
			CoreRenderDevice.FrameWrite( Deformations.Buffer, at * 16, MemoryMarshal.AsBytes( stack.AsSpan( 0, count ) ) );
		}

		deformQueue.Clear();


		var words = MaterialsAt + Math.Max( 1, instanceMaterialsUsed );

		if ( materialBuffer == 0 || materialBufferWords < words )
		{
			if ( materialBuffer != 0 ) CoreRenderDevice.DestroyBuffer( materialBuffer );
			materialBufferWords = Math.Max( 4096, words * 2 );
			materialBuffer = CoreRenderDevice.ReserveBuffer( (long)materialBufferWords * 4 );
			materialsDirty = true;
		}

		if ( materialBuffer != 0 && ( materialsDirty || materialsGeneration != CoreMaterialTable.Generation ) )
		{
			if ( packed.Length < words ) packed = new uint[Math.Max( 4096, words * 2 )];

			var into = packed.AsSpan( 0, words );
			CoreMaterialTable.Write( into );
			instanceMaterials.AsSpan( 0, instanceMaterialsUsed ).CopyTo( into[MaterialsAt..] );
			CoreRenderDevice.FrameWrite( materialBuffer, 0, MemoryMarshal.AsBytes( into ) );

			materialsDirty = false;
			materialsGeneration = CoreMaterialTable.Generation;
		}
	}

	//Everything again, next frame - what a device going or a table moving wants.
	internal void Invalidate()
	{
		allDirty = true;
		materialsDirty = true;
		resources = -1;
	}

	//The instances by the world's grid, for the cull - see CoreInstanceCells.
	internal readonly CoreInstanceCells Cells = new();

	internal void Release()
	{
		Cells.Release();
		if ( instanceBuffer != 0 ) CoreRenderDevice.DestroyBuffer( instanceBuffer );
		if ( materialBuffer != 0 ) CoreRenderDevice.DestroyBuffer( materialBuffer );
		instanceBuffer = 0;
		materialBuffer = 0;
		instanceBufferRecords = 0;
		materialBufferWords = 0;
		Invalidate();
	}
}

//Every material anything in any scene is drawn with, by number: what the card needs of it - its flags, the
//texture it cuts out by - and what the material passes need, which is the pipeline and the set to draw it with.
internal static class CoreMaterialTable
{
	internal sealed class Info
	{
		public ulong Texture;
		public string Shader;
		public string Combo;
		public bool TwoSided;
		public bool Translucent;
		public bool Masked;
		public float Cutoff;
		public bool Loose;

		/// <summary>Material tint, its Tint and any flat colour slot; part of the key.</summary>
		public Color Tint = Color.White;

		public uint ColourSlot;

		/// <summary>The picture ColourSlot numbers, looked up again when streaming moves it - see Renumber.</summary>
		public ulong ColourPicture;

		public ulong ShadePipeline;
		public bool AskedToShade;

		/// <summary>The depth-only pass for a material moving its own vertices; nought for still surfaces.</summary>
		public ulong DepthPipeline;
		public bool AskedToDepth;

		/// <summary>And its motion on the screen, for the velocity picture; nought where it has no velocity build.</summary>
		public ulong VelocityPipeline;
		public bool AskedToVelocity;

		/// <summary>Whether it has already been said that this surface has no depth build - said once.</summary>
		public bool SaidNoDepth;

		/// <summary>Whether the shading pass was baked with the mover pre-pass, which decides if it writes depth.</summary>
		public bool ShadePrepass;

		/// <summary>Whether its cluster pass was baked writing its own depth - see CoreMeshRender.DrawMaterials.</summary>
		public bool ShadeCarries;

		/// <summary>And its pass for the far calls - see CoreMeshRender.DrawMaterials.</summary>
		public bool ShadeFarCarries;


		/// <summary>Whether the pair targets the finished window or the world picture; true is the default.</summary>
		public bool ShadeFinishes = true;

		/// <summary>Specialization constants the baked pair carries; -1 means nothing baked yet</summary>
		public int ShadeSpecialize = -1;

		/// <summary>Which run of point effects the finishing pass carries; null for none.</summary>
		public string ShadeChain;

		/// <summary>The pass baked for its clusters' far calls (kFarShading); nought for none.</summary>
		public ulong ShadeFarPipeline;

		public string Path;

		/// <summary>How many instance-material words name this one; nought gives the slot back.</summary>
		public int Uses;

		/// <summary>What it was found by, so releasing it can take it out of the map.</summary>
		public Key Key;

		/// <summary>Whether it moves its own vertices: not pre-passed, its own pass writes the depth it shades.</summary>
		public bool Moves;

		/// <summary>The pictures it wears - colour, normal, third, emission - for CoreTextureStreaming.</summary>
		public readonly ulong[] Pictures = new ulong[4];

		/// <summary>Most metres a unit of its parts' first coordinates covers, for streaming; nought if untold.</summary>
		public float MetresPerUv;

		public uint Flags =>
			( TwoSided ? CoreMeshShaders.MaterialTwoSided : 0 ) |
			( Masked ? CoreMeshShaders.MaterialAlphaTest : 0 ) |
			( Translucent ? CoreMeshShaders.MaterialTranslucent : 0 ) |
			( Loose ? CoreMeshShaders.MaterialLoose : 0 ) |
			( Moves ? CoreMeshShaders.MaterialMoves : 0 );
	}

	//Material identity; five fields so ValueTuple has no Rest, whose hashing boxed on every lookup
	internal readonly record struct Key( ulong Texture, string Shader, string Combo, int Flags, Color Tint );

	//The switches a key packs, so that it is one number and not four.
	[Flags]
	private enum Drawn
	{
		None = 0,
		TwoSided = 1,
		Translucent = 2,
		Masked = 4,
		Loose = 8,
		Moves = 16,
	}

	private static readonly List<Info> materials = new();
	private static readonly Dictionary<Key, int> byKey = new();

	//Free slots for reuse; slots are GPU indices, so the list is never compacted
	private static readonly List<int> free = new();

	internal static int Count => materials.Count;

	//Bumped on any change; a counter, so every scene's copy can notice it independently
	internal static int Generation { get; private set; }

	internal static Info At( int index ) => index >= 0 && index < materials.Count ? materials[index] : null;

	//The material a part draws with, added if new; a part not shown draws nothing - loose, which the raster skips
	internal static int Index( in CoreSceneRenderer.MeshDraw part, bool shows )
	{
		var loose = !shows || !CoreResources.Shades( part.Shader );
		var moves = CoreSceneRenderer.MovesVertices( part.Shader, part.Combo );

		var drawn = ( part.TwoSided ? Drawn.TwoSided : 0 ) | ( part.Translucent ? Drawn.Translucent : 0 ) |
			( part.Masked ? Drawn.Masked : 0 ) | ( loose ? Drawn.Loose : 0 ) | ( moves ? Drawn.Moves : 0 );

		var key = new Key( part.Texture, part.Shader ?? "", part.Combo ?? "", (int)drawn, part.Tint );

		if ( byKey.TryGetValue( key, out var found ) )
		{
			materials[found].Uses++;
			materials[found].MetresPerUv = MathF.Max( materials[found].MetresPerUv, part.MetresPerUv );
			return found;
		}

		var info = new Info
		{
			Key = key,
			Uses = 1,
			Texture = part.Texture,
			Shader = part.Shader,
			Combo = part.Combo,
			TwoSided = part.TwoSided,
			Translucent = part.Translucent,
			Masked = part.Masked && part.Cutoff > 0.0f,
			Cutoff = part.Cutoff,
			Loose = loose,
			Moves = moves,
			Tint = part.Tint,
			Path = part.Material,
			MetresPerUv = part.MetresPerUv,
		};

		CoreRender.MaterialParts( part.Texture, info.Pictures );

		if ( info.Masked )
		{
			//The live material's own picture first: one made in code has no file - see CoreResources.LiveColour
			var colour = CoreResources.LiveColour( part.Texture );
			if ( colour == 0 ) colour = CoreResources.ColourTexture( part.Material );
			info.ColourPicture = colour;
			info.ColourSlot = colour != 0 ? CoreRender.TextureIndex( colour ) : 0;
		}

		int at;

		if ( free.Count > 0 )
		{
			at = free[^1];
			free.RemoveAt( free.Count - 1 );
			materials[at] = info;
		}
		else
		{
			materials.Add( info );
			at = materials.Count - 1;
		}

		byKey[key] = at;
		Generation++;

		return at;
	}

	//One fewer thing names this material; the last one gives the slot back.
	internal static void Release( int index )
	{
		var info = At( index );
		if ( info is null || --info.Uses > 0 ) return;

		byKey.Remove( info.Key );
		materials[index] = null;
		free.Add( index );
		Generation++;
	}

	//Forgets every material's pipeline after a shader reload, since those handles were destroyed
	internal static void ForgetPipelines()
	{
		foreach ( var one in materials )
		{
			if ( one is null ) continue;

			one.ShadePipeline = 0;
			one.AskedToShade = false;
			one.DepthPipeline = 0;
			one.AskedToDepth = false;
			one.VelocityPipeline = 0;
			one.AskedToVelocity = false;
			one.ShadePrepass = false;
			one.ShadeCarries = false;
			one.ShadeFarCarries = false;
			one.ShadeFinishes = true;
			one.ShadeSpecialize = -1;
			one.ShadeChain = null;
		}
	}

	//The records as the card reads them, four words each, at the start of a scene's material buffer.
	internal static void Write( Span<uint> into )
	{
		for ( var i = 0; i < materials.Count && ( i * 4 ) + 3 < into.Length; i++ )
		{
			var one = materials[i];

			//A slot nobody names reads as a material with no flags: nothing is drawn by it, because nothing points at it.
			if ( one is null )
			{
				into.Slice( i * 4, 4 ).Clear();
				continue;
			}

			into[i * 4] = one.Flags;
			into[( i * 4 ) + 1] = one.ColourSlot;
			into[( i * 4 ) + 2] = BitConverter.SingleToUInt32Bits( one.Cutoff );
			into[( i * 4 ) + 3] = 0;
		}
	}

	//Re-reads cut-out colour indices after streaming moved textures; bumps the generation
	internal static void Renumber()
	{
		var changed = false;

		//By index: a picture made whole out of turn renumbers from whichever thread asked - see KeepWhole
		for ( var i = 0; i < materials.Count; i++ )
		{
			var one = i < materials.Count ? materials[i] : null;
			if ( one is null || one.ColourPicture == 0 ) continue;

			var slot = CoreRender.TextureIndex( one.ColourPicture );
			if ( slot == one.ColourSlot ) continue;

			one.ColourSlot = slot;
			changed = true;
		}

		if ( changed ) Generation++;
	}

	//Forgets every material, which is what the device going takes with it.
	internal static void Clear()
	{
		materials.Clear();
		byKey.Clear();
		free.Clear();
		Generation++;
	}
}
