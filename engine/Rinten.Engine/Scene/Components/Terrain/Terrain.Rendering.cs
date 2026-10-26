using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using static Rinten.ModelRenderer;

namespace Rinten;

public partial class Terrain
{
	//One baked square of ground: ground and set-in models, their scene objects and collision shape
	private sealed class Chunk
	{
		public SceneObject Ground;
		public SceneObject SetIn;
		public Model GroundModel;
		public Model SetInModel;
		public PhysicsShape Shape;

		/// <summary>What it is drawn with while it is not kept whole - see TerrainChunkBaker.Thinned.</summary>
		public SceneObject FarGround;
		public SceneObject FarSetIn;
		public Model FarGroundModel;
		public Model FarSetInModel;

		/// <summary>Whether it is kept whole: drawn finest, its ground kept for the scatter - see Keep.</summary>
		public bool Whole = true;

		/// <summary>Whether its ground changed since last gathered, not only wanted whole or far.</summary>
		public bool Changed;

		/// <summary>Whether the physics world holds its collision - see Solidify. Its triangles are kept either way.</summary>
		public bool Solid = true;

		/// <summary>What it last baked to: what it collides as, and what is scattered over it.</summary>
		public TerrainChunkBake Baked;

		/// <summary>Whether what is baked has been stood up in the scene yet.</summary>
		public bool Stood;

		/// <summary>Its palette as the shader reads it - see TerrainChunkBaker.PaletteNumbers.</summary>
		public Vector4 Palette;

		public bool Dirty;
		public bool Building;
	}

	private Chunk[] chunks = [];
	private int chunksWide, chunksDeep;
	private float chunkSpan;
	private Vector2 chunkOrigin;

	//Which layout a bake belongs to: a result from before the terrain was laid out again is dropped.
	private int layoutVersion;

	private int dirtyChunks;
	private int buildingChunks;
	private static int modelSerial;

	private readonly ConcurrentQueue<TerrainChunkJob> baked = new();
	private readonly Dictionary<object, TerrainModifier> modifiers = new();
	private TerrainMesh watched;

	/// <summary>The ground changed inside a box of the world - sculpted, painted, something laid over it moved. What stands on the ground by its own reckoning listens.</summary>
	public event Action<BBox> OnTerrainModified;

	//Told which chunk was just baked, in chunk coordinates - what is scattered over it wants to be scattered again.
	internal event Action<int, int> ChunkBaked;

	/// <summary>How many chunks the ground is baked in.</summary>
	public int ChunkCount => chunks.Length;

	/// <summary>How many of them are waiting to be baked or are being baked now - nought when the ground is as it should be.</summary>
	public int ChunksWaiting => dirtyChunks + buildingChunks;

	internal int ChunksWide => chunksWide;
	internal int ChunksDeep => chunksDeep;
	internal float ChunkSpan => chunkSpan;
	internal Vector2 ChunkOrigin => chunkOrigin;

	//The ground of one chunk as it was last baked, or null - what the scatter reads.
	internal TerrainSoup BakedGround( int x, int z )
		=> x < 0 || z < 0 || x >= chunksWide || z >= chunksDeep ? null : chunks[( z * chunksWide ) + x].Baked?.Ground;

	//Everything laid over the terrain now.
	internal IEnumerable<TerrainModifier> Modifiers => modifiers.Values;

	//What draws the ground, unless the terrain was given something else: the terrain shader, carrying the one number
	//it needs - which picture holds this terrain's own. A material each, because that number is each terrain's.
	private Material ownMaterial;

	//What the ground is actually drawn with.
	internal Material DrawnWith
	{
		get
		{
			if ( MaterialOverride is not null ) return MaterialOverride;

			return ownMaterial ??= Material.Create( $"terrain_{Id:N}", "shaders/engine/terrain.shader" );
		}
	}

	//Hands the material the picture its shader reads the terrain out of.
	private void NameParams()
	{
		if ( ParamsMap is null || DrawnWith is not { } wears ) return;

		if ( CoreRenderDevice.PortableFrame )
		{
			NamePortablePictures( wears );
			return;
		}

		//Said once a material and a picture: setting a number makes the material a new one as
		//far as the renderer is concerned, and every chunk wearing it would be resolved again.
		if ( ReferenceEquals( namedOn, wears ) && namedIndex == ParamsMap.Index ) return;

		namedOn = wears;
		namedIndex = ParamsMap.Index;

		wears.Set( "g_flParams", (float)ParamsMap.Index );
	}

	private Material namedOn;
	private int namedIndex = -1;

	//Portable path has no bindless table, so bind pictures on the material, only when they change
	private void NamePortablePictures( Material wears )
	{
		var pictures = portablePictures;

		pictures[0] = ParamsMap;

		for ( var i = 0; i < 3; i++ )
			pictures[i + 1] = Storage?.Materials.ElementAtOrDefault( i )?.GetTexture( "Color" ) ?? Texture.White;

		var same = ReferenceEquals( namedOn, wears );

		for ( var i = 0; i < pictures.Length; i++ )
		{
			var device = pictures[i]?.core?.Device() ?? 0;

			if ( portableNamed[i] != device ) same = false;
			portableNamed[i] = device;
		}

		if ( same ) return;

		namedOn = wears;

		wears.Set( "g_tParams", pictures[0] );
		wears.Set( "g_tColor", pictures[1] );
		wears.Set( "g_tNormal", pictures[2] );
		wears.Set( "g_tEmission", pictures[3] );
	}

	private readonly Texture[] portablePictures = new Texture[4];
	private readonly ulong[] portableNamed = new ulong[4];

	/// <summary>Sets or replaces (null removes) a modifier by key; only rebakes chunks under old and new bounds</summary>
	public void SetModifier( object key, TerrainModifier modifier )
	{
		if ( key is null ) return;

		modifiers.TryGetValue( key, out var was );

		if ( modifier is null ) modifiers.Remove( key );
		else modifiers[key] = modifier;

		if ( was is not null ) ChunksDirty( was.Bounds );
		if ( modifier is not null ) ChunksDirty( modifier.Bounds );
	}

	//Lays the chunks out over the mesh as it is, and bakes all of them.
	private void CreateChunks()
	{
		ReleaseChunks();

		if ( !Active || Storage?.Mesh is not { } mesh ) return;

		if ( !ReferenceEquals( watched, mesh ) )
		{
			if ( watched is not null ) watched.Changed -= MeshChanged;
			watched = mesh;
			watched.Changed += MeshChanged;
		}

		if ( !Application.IsHeadless )
		{
			UpdateParamsMap();
			NameParams();
		}

		//Over the ground as it stands, and a chunk either way: a brush pulls the ground
		//past its old edge, and a triangle past the last chunk would be nobody's.
		var bounds = mesh.Bounds;

		chunkSpan = Math.Clamp( ChunkSize, 8.0f, 256.0f );
		keptFrom = null;
		chunkOrigin = new Vector2( MathF.Floor( bounds.Mins.x / chunkSpan ) - 1.0f, MathF.Floor( bounds.Mins.z / chunkSpan ) - 1.0f ) * chunkSpan;
		chunksWide = Math.Max( 1, (int)MathF.Ceiling( ( bounds.Maxs.x - chunkOrigin.x ) / chunkSpan ) + 1 );
		chunksDeep = Math.Max( 1, (int)MathF.Ceiling( ( bounds.Maxs.z - chunkOrigin.y ) / chunkSpan ) + 1 );
		chunks = new Chunk[chunksWide * chunksDeep];

		for ( var i = 0; i < chunks.Length; i++ ) chunks[i] = new Chunk();

		layoutVersion++;

		//Which are kept whole for where the eye is now; the rest read back only what is drawn of them far off, and their collision.
		Keep();
		Solidify( true );

		//Bake every chunk now in parallel, so collision exists before the first frame. Gathered
		//on the workers too: nothing sculpts the mesh until this returns.
		var jobs = new TerrainChunkJob[chunks.Length];
		var readBefore = TerrainBakeCache.Hits;

		Rinten.Utility.Parallel.For( 0, jobs.Length, i =>
		{
			var job = jobs[i] = Gather( i, mesh, new List<int>() );

			try { TerrainChunkBaker.Build( job ); }
			catch ( Exception e ) { Log.Warning( e, $"Terrain chunk {job.Chunk} did not bake" ); }
		} );

		foreach ( var job in jobs )
		{
			chunks[job.Chunk].Baked = job.Result;
			chunks[job.Chunk].Stood = job.Result is null;
		}

		//Said once a layout, because the whole point of the cache is that this number is the whole terrain on the second
		//start and nought on the first - and a run where it is nought twice over is a run where something is wrong with it.
		var read = TerrainBakeCache.Hits - readBefore;
		if ( read > 0 ) Log.Info( $"Terrain: {read} of {jobs.Length} chunks read back rather than baked" );
	}

	private void ReleaseChunks()
	{
		foreach ( var chunk in chunks ) Release( chunk, true );

		chunks = [];
		chunksWide = chunksDeep = 0;
		dirtyChunks = 0;

		//Nothing of the new layout is standing yet. Left at the old count, PumpChunks took a layout of chunks read back from the bake cache - not
		//dirty, not building, only waiting to be stood - for one already stood, returned before standing any, and the ground was not drawn at all.
		standing = 0;

		//Whatever is still out on a worker comes back to a layout that has gone.
		layoutVersion++;
	}

	private void Release( Chunk chunk, bool shape )
	{
		if ( chunk is null ) return;

		ReleaseWhole( chunk );
		ReleaseFar( chunk );

		if ( shape && chunk.Shape.IsValid() )
		{
			Shapes.Remove( chunk.Shape );
			chunk.Shape.Remove();
			chunk.Shape = null;
		}
	}

	//The renderer keeps a mesh per model it has seen, and these are models nobody will ask for again.
	private static void ReleaseWhole( Chunk chunk )
	{
		chunk.Ground?.Delete();
		chunk.SetIn?.Delete();
		chunk.Ground = null;
		chunk.SetIn = null;

		if ( chunk.GroundModel is not null ) CoreResources.Forget( chunk.GroundModel );
		if ( chunk.SetInModel is not null ) CoreResources.Forget( chunk.SetInModel );

		chunk.GroundModel = null;
		chunk.SetInModel = null;
	}

	private static void ReleaseFar( Chunk chunk )
	{
		chunk.FarGround?.Delete();
		chunk.FarSetIn?.Delete();
		chunk.FarGround = null;
		chunk.FarSetIn = null;

		if ( chunk.FarGroundModel is not null ) CoreResources.Forget( chunk.FarGroundModel );
		if ( chunk.FarSetInModel is not null ) CoreResources.Forget( chunk.FarSetInModel );

		chunk.FarGroundModel = null;
		chunk.FarSetInModel = null;
	}

	//Mesh changed in a box: dirty chunks, and relayout if the ground grew past them (mesh only)
	private void MeshChanged( BBox box )
	{
		if ( chunks.Length == 0 ) return;

		groundStale = true;

		if ( box.Mins.x < chunkOrigin.x || box.Mins.z < chunkOrigin.y
			|| box.Maxs.x >= chunkOrigin.x + ( chunksWide * chunkSpan ) || box.Maxs.z >= chunkOrigin.y + ( chunksDeep * chunkSpan ) )
		{
			relayout = true;
		}

		ChunksDirty( box );
	}

	//Every chunk a box of the terrain's own space touches is to be baked again.
	private void ChunksDirty( BBox box )
	{
		if ( chunks.Length == 0 ) return;

		OnTerrainModified?.Invoke( box.Transform( WorldTransform ) );

		//Grow by a triangle's reach, not a whole chunk, since a moved corner can shift a triangle's chunk
		var reach = MathF.Max( Storage?.EdgeLength ?? 1.0f, 0.05f ) * 3.0f;
		var x0 = Math.Clamp( (int)MathF.Floor( ( box.Mins.x - reach - chunkOrigin.x ) / chunkSpan ), 0, chunksWide - 1 );
		var x1 = Math.Clamp( (int)MathF.Floor( ( box.Maxs.x + reach - chunkOrigin.x ) / chunkSpan ), 0, chunksWide - 1 );
		var z0 = Math.Clamp( (int)MathF.Floor( ( box.Mins.z - reach - chunkOrigin.y ) / chunkSpan ), 0, chunksDeep - 1 );
		var z1 = Math.Clamp( (int)MathF.Floor( ( box.Maxs.z + reach - chunkOrigin.y ) / chunkSpan ), 0, chunksDeep - 1 );

		for ( var z = z0; z <= z1; z++ )
		{
			for ( var x = x0; x <= x1; x++ )
			{
				var chunk = chunks[( z * chunksWide ) + x];

				chunk.Changed = true;
				Again( chunk );
			}
		}
	}

	private void Again( Chunk chunk )
	{
		if ( chunk.Dirty ) return;

		chunk.Dirty = true;
		dirtyChunks++;
	}

	//Pixels a metre covers a metre away, for a window as tall as most, where no camera has said yet.
	private const float UnseenPixelsAtAMetre = 1000.0f;

	//Where the ground is seen from in its metres, and how far chunks are kept whole; false where nobody looks yet
	private bool Looked( out Vector3 eye, out float reach )
	{
		eye = default;
		reach = 0.0f;

		if ( Scene is null || Application.IsHeadless ) return false;

		float pixels;

		if ( Scene.Detail.Looking )
		{
			eye = Scene.Detail.Eye;
			pixels = Scene.Detail.PixelsAtAMetre;
		}
		else if ( Scene.DetailCamera is { } camera && camera.IsValid() )
		{
			eye = camera.WorldPosition;
			pixels = UnseenPixelsAtAMetre;
		}
		else
		{
			return false;
		}

		eye = WorldTransform.PointToLocal( eye );

		//Where a far model's error reaches the pixels a model's chain may be off by, in the terrain's metres
		reach = chunkSpan * TerrainChunkBaker.FarShare * pixels / MathF.Max( Rendering.DrawSettings.MeshLodPixels, 0.1f );
		if ( Relieved ) reach = MathF.Max( reach, ReliefDistance );

		foreach ( var wanted in keptWhole.Values ) reach = MathF.Max( reach, wanted );

		return true;
	}

	//How far round the eye whatever reads a chunk's ground wants chunks kept whole, in the terrain's metres, by who asked.
	private readonly Dictionary<object, float> keptWhole = new();

	//Asks for the chunks within a reach of the eye to be kept whole, or with nought stops asking.
	internal void KeepWhole( object who, float reach )
	{
		if ( reach > 0.0f ) keptWhole[who] = reach;
		else keptWhole.Remove( who );
	}

	private Vector3? keptFrom;
	private float keptReach;

	//Chunks kept whole for an eye here: one in reach is read whole, one a chunk past drops to its far models
	//Asked again once the eye moves an eighth of a chunk
	private void Keep()
	{
		if ( chunks.Length == 0 || !Looked( out var eye, out var reach ) ) return;

		if ( keptFrom is { } was && ( eye - was ).Length < chunkSpan * 0.125f && reach == keptReach ) return;

		keptFrom = eye;
		keptReach = reach;

		for ( var i = 0; i < chunks.Length; i++ )
		{
			var chunk = chunks[i];

			var x0 = chunkOrigin.x + ( ( i % chunksWide ) * chunkSpan );
			var z0 = chunkOrigin.y + ( ( i / chunksWide ) * chunkSpan );
			var dx = MathF.Max( MathF.Max( x0 - eye.x, eye.x - ( x0 + chunkSpan ) ), 0.0f );
			var dz = MathF.Max( MathF.Max( z0 - eye.z, eye.z - ( z0 + chunkSpan ) ), 0.0f );
			var away = MathF.Sqrt( ( dx * dx ) + ( dz * dz ) );

			var whole = away <= ( chunk.Whole ? reach + chunkSpan : reach );
			if ( whole == chunk.Whole ) continue;

			chunk.Whole = whole;

			//Not laid out yet: the first bake reads it as it now is.
			if ( chunk.Baked is null ) continue;

			if ( whole )
			{
				Again( chunk );
				continue;
			}

			//Made without far models while being sculpted: baked again for them, and the whole ones stand until then.
			if ( !chunk.Baked.HasFar )
			{
				Again( chunk );
				continue;
			}

			ReleaseWhole( chunk );
			Forget( chunk.Baked );
			ShowFar( chunk );
		}
	}

	//What a bake holds that only a whole chunk keeps.
	private static void Forget( TerrainChunkBake bake )
	{
		bake.Whole = false;
		bake.Ground = null;
		bake.GroundBytes = null;
		bake.SetInBytes = null;
	}

	//The far models are drawn wherever the whole ones are not standing.
	private static void ShowFar( Chunk chunk )
	{
		var far = chunk.GroundModel is null && chunk.SetInModel is null;

		if ( chunk.FarGround.IsValid() ) chunk.FarGround.RenderingEnabled = far;
		if ( chunk.FarSetIn.IsValid() ) chunk.FarSetIn.RenderingEnabled = far;
	}

	private bool relayout;

	//Metres round the eye, and round everything that moves, the ground holds physics in play - what a shot or a step can reach.
	private const float SolidReach = 256.0f;

	//Seconds between looks at where what moves has gone.
	private const float SolidEvery = 0.1f;

	private RealTimeSince solidLooked;

	//Per chunk this look: 2 within the reach of something, 1 only within a chunk more of it - see Mark.
	private byte[] solidNear = [];

	//Chunks the physics world holds: all in the editor; in play those within SolidReach of the eye, every non-static body
	//and every character. Further ones keep triangles for the navigation mesh; a solid one stays so a chunk past reach
	private void Solidify( bool now )
	{
		if ( chunks.Length == 0 || Scene is null ) return;
		if ( !now && solidLooked < SolidEvery ) return;

		solidLooked = 0;

		var every = Scene.IsEditor;
		var scale = MathF.Max( WorldTransform.UniformScale, 1e-4f );
		var reach = MathF.Max( SolidReach / scale, chunkSpan * 2.0f );

		if ( solidNear.Length != chunks.Length ) solidNear = new byte[chunks.Length];
		Array.Clear( solidNear );

		if ( !every )
		{
			if ( Looked( out var eye, out var whole ) )
			{
				reach = MathF.Max( reach, whole );
				Mark( eye, reach );
			}

			if ( Scene.HasPhysicsWorld )
			{
				foreach ( var body in Scene.PhysicsWorld.BodySet )
					if ( body.IsValid() && body.BodyType != PhysicsBodyType.Static ) Mark( WorldTransform.PointToLocal( body.Position ), reach );
			}

			foreach ( var walker in Scene.GetAll<CharacterController>() )
				if ( walker.IsValid() ) Mark( WorldTransform.PointToLocal( walker.WorldPosition ), reach );
		}

		var changed = false;

		for ( var i = 0; i < chunks.Length; i++ )
		{
			var chunk = chunks[i];
			var solid = every || solidNear[i] == 2 || ( chunk.Solid && solidNear[i] == 1 );

			if ( solid == chunk.Solid ) continue;

			chunk.Solid = solid;

			//Laid out a moment ago: the rebuild after the layout gives it its shape.
			if ( chunk.Baked is null ) continue;

			Collide( chunk );
			changed = true;
		}

		if ( changed ) ConfigureShapes();
	}

	//The chunks round a point of the terrain's own across its plane: 2 within reach, 1 within a chunk more.
	private void Mark( Vector3 at, float reach )
	{
		var outer = reach + chunkSpan;

		var x0 = Math.Max( (int)MathF.Floor( ( at.x - outer - chunkOrigin.x ) / chunkSpan ), 0 );
		var x1 = Math.Min( (int)MathF.Floor( ( at.x + outer - chunkOrigin.x ) / chunkSpan ), chunksWide - 1 );
		var z0 = Math.Max( (int)MathF.Floor( ( at.z - outer - chunkOrigin.y ) / chunkSpan ), 0 );
		var z1 = Math.Min( (int)MathF.Floor( ( at.z + outer - chunkOrigin.y ) / chunkSpan ), chunksDeep - 1 );

		for ( var z = z0; z <= z1; z++ )
		{
			var cz = chunkOrigin.y + ( z * chunkSpan );
			var dz = MathF.Max( MathF.Max( cz - at.z, at.z - ( cz + chunkSpan ) ), 0.0f );

			for ( var x = x0; x <= x1; x++ )
			{
				var i = ( z * chunksWide ) + x;
				if ( solidNear[i] == 2 ) continue;

				var cx = chunkOrigin.x + ( x * chunkSpan );
				var dx = MathF.Max( MathF.Max( cx - at.x, at.x - ( cx + chunkSpan ) ), 0.0f );
				var apart = ( dx * dx ) + ( dz * dz );

				if ( apart <= reach * reach ) solidNear[i] = 2;
				else if ( apart <= outer * outer ) solidNear[i] = 1;
			}
		}
	}

	/// <summary>World triangles of chunks under a box the physics world does not hold, for the navigation mesh.</summary>
	internal void UnheldCollision( BBox world, List<Vector3> points, List<int> indices )
	{
		if ( chunks.Length == 0 ) return;

		//The box's corners in the terrain's own space, which may be turned.
		var local = new BBox( WorldTransform.PointToLocal( world.Mins ), WorldTransform.PointToLocal( world.Mins ) );

		for ( var corner = 1; corner < 8; corner++ )
		{
			var at = new Vector3( ( corner & 1 ) != 0 ? world.Maxs.x : world.Mins.x, ( corner & 2 ) != 0 ? world.Maxs.y : world.Mins.y,
				( corner & 4 ) != 0 ? world.Maxs.z : world.Mins.z );

			local = local.AddPoint( WorldTransform.PointToLocal( at ) );
		}

		var x0 = Math.Clamp( (int)MathF.Floor( ( local.Mins.x - chunkOrigin.x ) / chunkSpan ), 0, chunksWide - 1 );
		var x1 = Math.Clamp( (int)MathF.Floor( ( local.Maxs.x - chunkOrigin.x ) / chunkSpan ), 0, chunksWide - 1 );
		var z0 = Math.Clamp( (int)MathF.Floor( ( local.Mins.z - chunkOrigin.y ) / chunkSpan ), 0, chunksDeep - 1 );
		var z1 = Math.Clamp( (int)MathF.Floor( ( local.Maxs.z - chunkOrigin.y ) / chunkSpan ), 0, chunksDeep - 1 );

		var stands = WorldTransform;

		for ( var z = z0; z <= z1; z++ )
		{
			for ( var x = x0; x <= x1; x++ )
			{
				var chunk = chunks[( z * chunksWide ) + x];
				if ( chunk.Shape.IsValid() || chunk.Baked is not { } bake || bake.CollisionIndices.Count < 3 ) continue;

				var first = points.Count;

				foreach ( var point in bake.CollisionPoints ) points.Add( stands.PointToWorld( point ) );
				foreach ( var index in bake.CollisionIndices ) indices.Add( first + index );
			}
		}
	}

	//Moves the baking along: stands up what is baked within a few milliseconds of the frame, and hands the
	//workers what is waiting - nearest the eye first. Costs a comparison on a frame with nothing to do.
	private void PumpChunks()
	{
		if ( chunks.Length == 0 ) return;

		if ( relayout && buildingChunks == 0 )
		{
			relayout = false;
			CreateChunks();
			Rebuild();
		}

		Keep();
		Solidify( false );

		if ( dirtyChunks == 0 && buildingChunks == 0 && baked.IsEmpty && standing == chunks.Length ) return;

		var clock = Stopwatch.StartNew();

		//Cook finished chunk shapes: always one, then more while within budget; the rest wait in queue
		var cooked = false;
		var took = false;

		while ( ( !took || clock.Elapsed.TotalMilliseconds < BakeBudget ) && baked.TryDequeue( out var done ) )
		{
			took = true;
			buildingChunks--;

			if ( done.Version != layoutVersion || done.Chunk >= chunks.Length ) continue;

			var chunk = chunks[done.Chunk];
			chunk.Building = false;

			if ( done.Result is null ) continue;

			chunk.Baked = done.Result;
			chunk.Stood = false;

			//Read again only to be kept whole or far, it collides as it did.
			if ( done.Changed || !chunk.Shape.IsValid() )
			{
				Collide( chunk );
				cooked = true;
			}

			//Wanted whole again while a far one was being read: read once more.
			if ( chunk.Whole && !done.Result.Whole ) Again( chunk );
		}

		//Once, not once a chunk: this walks every shape the terrain has, and doing it inside Collide made a frame that swapped eight chunks walk eight hundred.
		if ( cooked ) ConfigureShapes();

		standing = 0;

		foreach ( var chunk in chunks )
		{
			if ( !chunk.Stood && clock.Elapsed.TotalMilliseconds < BakeBudget ) Stand( chunk );
			if ( chunk.Stood ) standing++;
		}

		if ( dirtyChunks == 0 || Storage?.Mesh is not { } mesh ) return;

		var most = Math.Max( 1, Environment.ProcessorCount - 2 );

		//The nearest chunk to the eye is built first, and where the eye is comes off CoreDetail - see LookForDetail.
		var local = Scene is { Detail.Looking: true } ? WorldTransform.PointToLocal( Scene.Detail.Eye ) : mesh.Bounds.Center;

		while ( buildingChunks < most && dirtyChunks > 0 )
		{
			var best = -1;
			var nearest = float.MaxValue;

			for ( var i = 0; i < chunks.Length; i++ )
			{
				if ( !chunks[i].Dirty || chunks[i].Building ) continue;

				var dx = chunkOrigin.x + ( ( ( i % chunksWide ) + 0.5f ) * chunkSpan ) - local.x;
				var dz = chunkOrigin.y + ( ( ( i / chunksWide ) + 0.5f ) * chunkSpan ) - local.z;
				var distance = ( dx * dx ) + ( dz * dz );

				if ( distance >= nearest ) continue;

				nearest = distance;
				best = i;
			}

			if ( best < 0 ) break;

			var chunk = chunks[best];

			chunk.Dirty = false;
			chunk.Building = true;
			dirtyChunks--;
			buildingChunks++;

			var job = Gather( best, mesh );
			chunk.Changed = false;

			System.Threading.Tasks.Task.Run( () =>
			{
				try { TerrainChunkBaker.Build( job ); }
				catch ( Exception e ) { Log.Warning( e, $"Terrain chunk {job.Chunk} did not bake" ); }

				baked.Enqueue( job );
			} );
		}
	}

	private int standing;

	//How many milliseconds of a frame go on turning finished bakes into models.
	private const double BakeBudget = 4.0;

	private readonly List<int> gathered = new();

	//Copies what a chunk's bake reads: its triangles, neighbours' under solids reaching it, the modifiers over it
	//On the main thread, so a worker never reads a mesh being sculpted - but for the layout's first bake
	private TerrainChunkJob Gather( int index, TerrainMesh mesh, List<int> gathered = null )
	{
		gathered ??= this.gathered;

		var x = index % chunksWide;
		var z = index / chunksWide;

		var job = new TerrainChunkJob
		{
			Chunk = index,
			Version = layoutVersion,
			Min = chunkOrigin + ( new Vector2( x, z ) * chunkSpan ),
			Max = chunkOrigin + ( new Vector2( x + 1, z + 1 ) * chunkSpan ),
			Ground = new TerrainSoup(),

			//What the worker needs to know to write the models as well as bake them, read here because it is all main-thread business:
			//whether there is anything to draw, whether the ground is being sculpted this moment, and what the two models are called.
			Draws = !Application.IsHeadless,
			Coarse = mesh.IsEditing,
			Whole = chunks[index].Whole,
			Changed = chunks[index].Changed,
			GroundName = $"terrain_ground_{System.Threading.Interlocked.Increment( ref modelSerial )}",
			SetInName = $"terrain_set_{System.Threading.Interlocked.Increment( ref modelSerial )}",
			Shipped = string.IsNullOrEmpty( Storage.ResourcePath ) ? Scene?.Source?.ResourcePath : Storage.ResourcePath,
		};

		job.Materials.AddRange( Storage.InsetMaterials );

		//What reaches this chunk, and how far past its edges the ground has to be read for them to be cut properly.
		var reachMin = job.Min;
		var reachMax = job.Max;

		List<TerrainModifier> over = null;
		List<int> materialsAt = null;

		foreach ( var modifier in modifiers.Values )
		{
			var b = modifier.Bounds;

			if ( b.Maxs.x < job.Min.x || b.Maxs.z < job.Min.y || b.Mins.x > job.Max.x || b.Mins.z > job.Max.y ) continue;

			( over ??= new() ).Add( modifier );
			( materialsAt ??= new() ).Add( job.Materials.Count );

			job.Materials.AddRange( modifier.Materials );

			if ( modifier.Solid is null ) continue;

			reachMin = new Vector2( MathF.Min( reachMin.x, b.Mins.x - 1.0f ), MathF.Min( reachMin.y, b.Mins.z - 1.0f ) );
			reachMax = new Vector2( MathF.Max( reachMax.x, b.Maxs.x + 1.0f ), MathF.Max( reachMax.y, b.Maxs.z + 1.0f ) );
		}

		if ( over is not null )
		{
			job.Modifiers = over.ToArray();
			job.ModifierMaterialsAt = materialsAt.ToArray();
		}

		gathered.Clear();
		mesh.TrianglesInRect( reachMin, reachMax, gathered );

		//In the mesh's own order, whichever rectangle asked. Two chunks either side of a rock both cut the ground under it, and they have to make the
		//same cut along their border: the union merges near points in the order it meets them, so the triangles they share must reach it in one order.
		gathered.Sort();

		TerrainMeshCopy.Triangles( mesh, gathered, job.Ground, t =>
		{
			mesh.GetTriangle( t, out var a, out var b, out var c );

			var pa = mesh.PositionOf( a );
			var pb = mesh.PositionOf( b );
			var pc = mesh.PositionOf( c );

			var cx = ( pa.x + pb.x + pc.x ) / 3.0f;
			var cz = ( pa.z + pb.z + pc.z ) / 3.0f;

			var ours = cx >= job.Min.x && cz >= job.Min.y && cx < job.Max.x && cz < job.Max.y;

			return ours ? mesh.MaterialOf( t ) : TerrainChunkJob.NotOurs;
		} );

		return job;
	}

	//Swaps a chunk's collision shape for its new bake; caller then settles all swapped shapes once
	private void Collide( Chunk chunk )
	{
		if ( chunk.Shape.IsValid() )
		{
			Shapes.Remove( chunk.Shape );
			chunk.Shape.Remove();
		}

		chunk.Shape = null;

		if ( !EnableCollision || !chunk.Solid || !PhysicsBody.IsValid() ) return;

		chunk.Shape = ShapeOf( chunk, PhysicsBody );

		if ( chunk.Shape.IsValid() ) Shapes.Add( chunk.Shape );
	}

	private PhysicsShape ShapeOf( Chunk chunk, PhysicsBody body )
	{
		if ( chunk.Baked is not { } bake || bake.CollisionIndices.Count < 3 ) return null;

		//The body stands where the terrain does and carries no scale; the mesh is in the terrain's own metres.
		var scale = WorldTransform.UniformScale;
		var points = bake.CollisionPoints;

		if ( MathF.Abs( scale - 1.0f ) > 1e-4f )
		{
			points = new List<Vector3>( points.Count );
			foreach ( var point in bake.CollisionPoints ) points.Add( point * scale );
		}

		var shape = body.AddMeshShape( points, bake.CollisionIndices );

		if ( shape.IsValid() )
		{
			shape.Collider = this;
			shape.Surface = Surface;
		}

		return shape;
	}

	//Turns a finished bake into scene models, replacing the old ones; models were built on the worker
	private void Stand( Chunk chunk )
	{
		chunk.Stood = true;

		if ( Application.IsHeadless || chunk.Baked is not { } result ) return;

		//The new ones stand before the old ones go, so there is no frame with a hole in the ground.
		var world = Scene.SceneWorld;
		var was = new Chunk();

		//Which layers its vertices' eight weights are for, and where the paint mask is.
		if ( result.Palette is { } palette ) chunk.Palette = TerrainChunkBaker.PaletteNumbers( palette );

		if ( result.HasFar )
		{
			( was.FarGround, was.FarSetIn, was.FarGroundModel, was.FarSetInModel ) = ( chunk.FarGround, chunk.FarSetIn, chunk.FarGroundModel, chunk.FarSetInModel );

			chunk.FarGroundModel = Load( result.FarGroundBytes, $"{result.GroundName}_far" );
			chunk.FarSetInModel = Load( result.FarSetInBytes, $"{result.SetInName}_far" );
			chunk.FarGround = chunk.FarGroundModel is null ? null : Place( new SceneObject( world, chunk.FarGroundModel, WorldTransform ), DrawnWith );
			chunk.FarSetIn = chunk.FarSetInModel is null ? null : Place( new SceneObject( world, chunk.FarSetInModel, WorldTransform ), null );

			if ( chunk.FarGround.IsValid() ) SayPaint( chunk.FarGround, chunk.Palette );

			result.FarGroundBytes = null;
			result.FarSetInBytes = null;
		}

		//Drawn far off by what it was baked with while being sculpted, having nothing else.
		var whole = chunk.Whole || !result.HasFar;

		if ( result.Whole && whole )
		{
			( was.Ground, was.SetIn, was.GroundModel, was.SetInModel ) = ( chunk.Ground, chunk.SetIn, chunk.GroundModel, chunk.SetInModel );

			chunk.GroundModel = Load( result.GroundBytes, result.GroundName );
			chunk.SetInModel = Load( result.SetInBytes, result.SetInName );
			chunk.Ground = chunk.GroundModel is null ? null : Place( new SceneObject( world, chunk.GroundModel, WorldTransform ), DrawnWith );
			chunk.SetIn = chunk.SetInModel is null ? null : Place( new SceneObject( world, chunk.SetInModel, WorldTransform ), null );

			if ( chunk.Ground.IsValid() ) SayPaint( chunk.Ground, chunk.Palette );

			//The models hold the geometry now; the bake keeps only what the simulation and the scatter read.
			result.GroundBytes = null;
			result.SetInBytes = null;
		}
		else if ( !whole )
		{
			( was.Ground, was.SetIn, was.GroundModel, was.SetInModel ) = ( chunk.Ground, chunk.SetIn, chunk.GroundModel, chunk.SetInModel );
			( chunk.Ground, chunk.SetIn, chunk.GroundModel, chunk.SetInModel ) = ( null, null, null, null );

			Forget( result );
		}

		ShowFar( chunk );
		Release( was, false );

		var index = Array.IndexOf( chunks, chunk );
		if ( index >= 0 ) ChunkBaked?.Invoke( index % chunksWide, index / chunksWide );

		reliefStale = true;
	}

	//A model out of a bake's bytes, or null where there were none.
	private static Model Load( byte[] bytes, string name )
	{
		if ( bytes is null ) return null;

		var model = Model.FromBytes( bytes, name );

		return model is not null && model != Model.Error ? model : null;
	}

	private SceneObject Place( SceneObject made, Material wears )
	{
		made.Component = this;
		made.Tags.SetFrom( GameObject.Tags );

		if ( wears is not null ) made.SetMaterialOverride( wears );

		Flag( made );
		return made;
	}

	private void Flag( SceneObject one )
	{
		if ( !one.IsValid() ) return;

		one.Flags.ExcludeGameLayer = RenderType == ShadowRenderType.ShadowsOnly;
		one.Flags.CastShadows = RenderType == ShadowRenderType.On || RenderType == ShadowRenderType.ShadowsOnly;
	}

	//Says something to every object the ground is drawn by.
	private void EachObject( Action<SceneObject, bool> say )
	{
		foreach ( var chunk in chunks )
		{
			if ( chunk.Ground.IsValid() ) say( chunk.Ground, true );
			if ( chunk.SetIn.IsValid() ) say( chunk.SetIn, false );
			if ( chunk.FarGround.IsValid() ) say( chunk.FarGround, true );
			if ( chunk.FarSetIn.IsValid() ) say( chunk.FarSetIn, false );
		}
	}

	//The terrain wears another material: the ground's objects are told, and the material is told which picture to read.
	private void MaterialChanged()
	{
		NameParams();

		var wears = DrawnWith;

		EachObject( ( one, isGround ) =>
		{
			if ( isGround ) one.SetMaterialOverride( wears );
		} );
	}

	//Rewrite the numbers picture after a change to the terrain itself, and stand the chunks where the terrain stands now.
	private void UpdateTerrainNumbers()
	{
		if ( Storage is null ) return;

		if ( !Application.IsHeadless )
		{
			UpdateParamsMap();
			NameParams();
		}

		var stands = WorldTransform;
		EachObject( ( one, _ ) => one.Transform = stands );
	}

	/// <summary>Rewrite the numbers picture after the painted layers change - one added, one removed, or one of their settings moved.</summary>
	public void UpdateMaterials()
	{
		if ( Application.IsHeadless || Storage is null )
			return;

		UpdateParamsMap();
		relief?.Again();
	}
}
