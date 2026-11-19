using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rinten;

/// <summary>One thing a terrain is scattered with: a model, how thickly, and where it will and will not grow.</summary>
[Expose]
public sealed class TerrainClutterLayer
{
	/// <summary>What is scattered.</summary>
	[Property]
	public Model Model { get; set; }

	/// <summary>How many to a square metre, where everything else lets them stand.</summary>
	[Property, Range( 0.001f, 32.0f )]
	public float Density { get; set; } = 0.5f;

	/// <summary>How far from the eye they are kept, in metres. The card culls and coarsens what is nearer than this; beyond it there is nothing to cull.</summary>
	[Property, Range( 8.0f, 16384.0f )]
	public float Distance { get; set; } = 96.0f;

	/// <summary>Metres out to which they stand as thick as asked; past it they thin with the square of the distance, so the screen holds as many. Nought never thins.</summary>
	[Property, Range( 0.0f, 4096.0f )]
	public float ThinFrom { get; set; }

	/// <summary>Which of the terrain's painted layers they grow on, as places in its list of materials. Where two layers blend they thin out with the blend. Empty for everywhere.</summary>
	[Property]
	public List<int> OnLayers { get; set; } = new();

	[Property]
	public RangedFloat Scale { get; set; } = new RangedFloat( 0.8f, 1.2f );

	/// <summary>The gentlest and the steepest ground they stand on, in degrees off level.</summary>
	[Property, Range( 0, 90 ), Group( "Placement" )]
	public float MinSlope { get; set; } = 0.0f;

	[Property, Range( 0, 90 ), Group( "Placement" )]
	public float MaxSlope { get; set; } = 35.0f;

	/// <summary>Stood square to the ground rather than to the world - stones, not trees.</summary>
	[Property, Group( "Placement" )]
	public bool AlignToGround { get; set; }

	[Property, Group( "Placement" )]
	public bool RandomYaw { get; set; } = true;

	/// <summary>How far they are let into the ground, in metres, so a slope does not show under one side.</summary>
	[Property, Range( 0.0f, 2.0f ), Group( "Placement" )]
	public float Sink { get; set; } = 0.02f;

	/// <summary>Off by default: a field of grass in the shadow maps is most of the shadow maps.</summary>
	[Property, Group( "Rendering" )]
	public bool CastShadows { get; set; }

	/// <summary>Above one they coarsen sooner with distance, below one later.</summary>
	[Property, Range( 0.1f, 8.0f ), Group( "Rendering" )]
	public float LodScale { get; set; } = 1.0f;

	/// <summary>Which of the project's detail groups these keep their detail by with distance. None wears the model's own, from its definition.</summary>
	[Property, Title( "Detail Group" ), LodGroupName, Group( "Rendering" )]
	public string LodGroup { get; set; } = "";

	internal int Settings()
	{
		var hash = new HashCode();

		hash.Add( Model );
		hash.Add( Density );
		hash.Add( Distance );
		hash.Add( ThinFrom );
		hash.Add( Scale.Min );
		hash.Add( Scale.Max );
		hash.Add( MinSlope );
		hash.Add( MaxSlope );
		hash.Add( AlignToGround );
		hash.Add( RandomYaw );
		hash.Add( Sink );
		hash.Add( CastShadows );
		hash.Add( LodScale );
		hash.Add( LodGroup );

		foreach ( var layer in OnLayers ) hash.Add( layer );

		return hash.ToHashCode();
	}
}

/// <summary>Models scattered on a terrain by paint and slope from baked chunks, placed and culled on the card.</summary>
[Expose]
[Title( "Terrain Clutter" )]
[Category( "World" )]
[Icon( "grass" )]
public sealed class TerrainClutter : Component, Component.ExecuteInEditor
{
	[Property]
	public List<TerrainClutterLayer> Layers { get; set; } = new();

	[Property]
	public int Seed { get; set; }

	//How many milliseconds of a frame go on standing new copies up.
	private const double StandBudget = 2.0;

	//The most copies one patch of one layer holds, whatever its density asks for.
	private const int MostPerPatch = 1 << 16;

	private sealed class Patch
	{
		public StaticInstanceSet Set;
		public bool Scattering;
		public bool Stale;

		/// <summary>Worked out and waiting to stand, and how far through them the frames have got.</summary>
		public List<Transform> Waiting;
		public int Stood;

		/// <summary>What the card places it from, and whether the tables have yet said they draw it.</summary>
		public Placed Placed;
		public bool Checked;

		/// <summary>How far its middle was from the eye when scattered, and since when it has not been wanted.</summary>
		public float Away;
		public double UnwantedSince;

		public void Drop()
		{
			Set?.Dispose();
			Placed?.Dispose();
			Set = null;
			Placed = null;
			Checked = false;
		}
	}

	private sealed class Scattered
	{
		public long Key;
		public int Version;
		public List<Transform> Places;
		public Laid Laid;
	}

	//Whether patches are placed on the card: full frame clusters only, none portable or headless
	private static bool OnCard => !Application.IsHeadless && CoreNative.Available && !CoreRenderDevice.PortableFrame && Rendering.DrawSettings.Clusters;

	//Layers whose model the tables would not draw from clusters - translucent, say - placed here instead.
	private readonly HashSet<int> byHand = new();

	private Terrain terrain;
	private readonly Dictionary<long, Patch> patches = new();
	private readonly ConcurrentQueue<Scattered> scattered = new();
	private readonly HashSet<long> standing = new();
	private readonly List<long> gone = new();

	//Patches each look asks for, by layer; the standing ones it no longer asks for wait in retiring until what replaces them stands
	private readonly List<HashSet<long>> wanted = new();
	private readonly HashSet<long> retiring = new();

	//The few patches with copies still to stand up or a run still to check, so a frame does not walk every standing patch
	private readonly HashSet<long> toStand = new();
	private readonly HashSet<long> toCheck = new();
	private readonly List<long> finished = new();

	private int version;
	private int settings;
	private Vector3 lookedFrom = new( float.MaxValue, 0.0f, 0.0f );
	private bool lookAgain = true;

	protected override void OnEnabled()
	{
		Find();
	}

	protected override void OnDisabled() => Release();
	protected override void OnDestroy() => Release();

	private void Find()
	{
		if ( terrain.IsValid() ) return;

		terrain = Components.GetInAncestorsOrSelf<Terrain>();

		if ( !terrain.IsValid() && Scene is not null )
		{
			foreach ( var one in Scene.GetAll<Terrain>() )
			{
				if ( !one.IsValid() ) continue;
				terrain = one;
				break;
			}
		}

		if ( !terrain.IsValid() ) return;

		terrain.ChunkBaked += OnChunkBaked;
		terrain.Transform.OnTransformChanged += Again;
	}

	private void Release()
	{
		if ( terrain.IsValid() )
		{
			terrain.ChunkBaked -= OnChunkBaked;
			terrain.Transform.OnTransformChanged -= Again;
		}

		terrain = null;
		Again();
	}

	/// <summary>Everything is scattered again from nothing.</summary>
	public void Again()
	{
		foreach ( var patch in patches.Values ) patch.Drop();

		patches.Clear();
		byHand.Clear();
		standing.Clear();
		toStand.Clear();
		toCheck.Clear();
		retiring.Clear();
		version++;
		lookAgain = true;
	}

	private void OnChunkBaked( int x, int z )
	{
		var top = TopLevel();

		for ( var layer = 0; layer < Layers.Count; layer++ )
		{
			for ( var level = 0; level <= top; level++ )
			{
				if ( !patches.TryGetValue( KeyOf( layer, level, ( x >> level ) << level, ( z >> level ) << level ), out var patch ) ) continue;

				//Left standing until the new scatter is ready to take its place.
				patch.Stale = true;
				lookAgain = true;
			}
		}
	}

	//A patch is a square of chunks 2^level a side, keyed by its first chunk
	private static long KeyOf( int layer, int level, int x, int z ) => ( (long)layer << 40 ) | ( (long)level << 36 ) | ( (long)z << 18 ) | (uint)x;

	private static (int Layer, int Level, int X, int Z) Unkey( long key ) =>
		( (int)( key >> 40 ), (int)( ( key >> 36 ) & 0xF ), (int)( ( key >> 18 ) & 0x3FFFF ), (int)( key & 0x3FFFF ) );

	//The level whose one patch covers the whole terrain
	private int TopLevel()
	{
		var most = Math.Max( terrain?.ChunksWide ?? 1, terrain?.ChunksDeep ?? 1 );
		var level = 0;
		while ( ( 1 << level ) < most && level < 15 ) level++;
		return level;
	}

	protected override void OnUpdate()
	{
		if ( Application.IsHeadless ) return;

		if ( !terrain.IsValid() ) { Find(); if ( !terrain.IsValid() ) return; }
		if ( terrain.Storage is null || terrain.ChunksWide == 0 ) return;

		var now = Seed;
		foreach ( var layer in Layers ) now = HashCode.Combine( now, layer?.Settings() ?? 0 );
		now = HashCode.Combine( now, Layers.Count, terrain.ChunksWide, terrain.ChunkSpan, terrain.ChunkOrigin, OnCard );

		if ( now != settings )
		{
			settings = now;
			Again();
		}

		Stand();
		Check();

		//The terrain can go while patches are stood up, when its scene is being unloaded
		if ( !terrain.IsValid() ) return;

		Retire();

		//Where the frame is being looked at from - see CoreDetail, which is told once a tick and is
		//the one place that knows whether it is the scene's camera, the editor's or a headset's.
		if ( Scene is not { Detail.Looking: true } ) return;

		var seen = Scene.Detail.Eye;

		var local = terrain.WorldTransform.PointToLocal( seen );
		var span = terrain.ChunkSpan;

		//Chunks are counted from the corner the terrain laid them out from.
		local -= new Vector3( terrain.ChunkOrigin.x, 0.0f, terrain.ChunkOrigin.y );

		//Which patches exist is a question about where the eye is to within a patch, so it is asked when the eye has gone a quarter of one.
		if ( !lookAgain && ( local - lookedFrom ).Length < span * 0.25f ) return;

		lookedFrom = local;
		lookAgain = false;

		Look( local, span );

		//Not held past the look: the workers took what they need
		groundRead.Clear();
	}

	//Each chunk's ground read once for a look, however many layers scatter on it - see Terrain.GroundOf
	private readonly Dictionary<long, TerrainSoup> groundRead = new();

	private TerrainSoup GroundOf( int x, int z )
	{
		var key = ( (long)z << 20 ) | (uint)x;
		if ( groundRead.TryGetValue( key, out var soup ) ) return soup;

		return groundRead[key] = terrain.GroundOf( x, z );
	}

	//Which patches should stand for an eye here: a tree of squares, each as coarse as its distance lets a thinning layer be,
	//the missing sent to be scattered; what is no longer asked for retires once its replacements stand.
	private void Look( Vector3 eye, float span )
	{
		groundRead.Clear();

		var top = TopLevel();

		while ( wanted.Count < Layers.Count ) wanted.Add( new() );

		for ( var l = 0; l < Layers.Count; l++ )
		{
			wanted[l].Clear();

			var layer = Layers[l];
			if ( layer?.Model is null || layer.Density <= 0.0f ) continue;

			Walk( l, layer, top, 0, 0, eye, span );
		}

		foreach ( var key in standing )
		{
			var (l, _, _, _) = Unkey( key );
			if ( l < wanted.Count && wanted[l].Contains( key ) ) { retiring.Remove( key ); continue; }

			if ( retiring.Add( key ) && patches.TryGetValue( key, out var patch ) ) patch.UnwantedSince = RealTime.Now;
		}

		Retire();
	}

	private void Walk( int l, TerrainClutterLayer layer, int level, int x, int z, Vector3 eye, float span )
	{
		if ( x >= terrain.ChunksWide || z >= terrain.ChunksDeep ) return;

		var size = 1 << level;
		var near = BoxAway( eye, x, z, size, span );

		if ( near > layer.Distance ) return;

		//Coarse enough where the nearest of it is far enough that its density has fallen a quarter for each level
		var thin = layer.ThinFrom;
		var leaf = level == 0 || ( thin > 0.0f && near >= thin * ( 1 << ( level - 1 ) ) );

		if ( !leaf )
		{
			var half = size >> 1;
			Walk( l, layer, level - 1, x, z, eye, span );
			Walk( l, layer, level - 1, x + half, z, eye, span );
			Walk( l, layer, level - 1, x, z + half, eye, span );
			Walk( l, layer, level - 1, x + half, z + half, eye, span );
			return;
		}

		var key = KeyOf( l, level, x, z );
		wanted[l].Add( key );

		var away = Away( eye, x, z, size, span );

		if ( !patches.TryGetValue( key, out var patch ) )
		{
			patch = new Patch { Stale = true };
			patches[key] = patch;
			standing.Add( key );
		}
		//Thinned for where the eye was: scattered again once it has come a quarter nearer
		else if ( thin > 0.0f && patch.Away > thin && away < patch.Away * 0.75f ) patch.Stale = true;

		if ( patch.Stale && !patch.Scattering ) Scatter( key, patch, l, level, x, z, eye, away );
	}

	//Drops what no look asks for once every patch overlapping it stands, or after a few seconds; a patch just outside the
	//distance with nothing to replace it stays until the eye is a little further, so pacing the edge does not churn it
	private void Retire()
	{
		if ( retiring.Count == 0 ) return;

		gone.Clear();

		var eye = lookedFrom;
		var span = terrain.ChunkSpan;

		foreach ( var key in retiring )
		{
			if ( !patches.TryGetValue( key, out var patch ) ) { gone.Add( key ); continue; }

			var (l, level, x, z) = Unkey( key );
			if ( l >= Layers.Count || Layers[l] is not { } layer || l >= wanted.Count ) { gone.Add( key ); continue; }

			var size = 1 << level;
			var overlapped = false;
			var ready = true;

			foreach ( var other in wanted[l] )
			{
				var (_, ol, ox, oz) = Unkey( other );
				var osize = 1 << ol;
				if ( ox >= x + size || ox + osize <= x || oz >= z + size || oz + osize <= z ) continue;

				overlapped = true;

				if ( !patches.TryGetValue( other, out var p ) || p.Scattering || p.Stale || toStand.Contains( other ) ) { ready = false; break; }
			}

			if ( !overlapped )
			{
				if ( BoxAway( eye, x, z, size, span ) <= layer.Distance * 1.15f ) continue;
			}
			else if ( !ready && RealTime.Now - patch.UnwantedSince < 3.0 ) continue;

			gone.Add( key );
		}

		foreach ( var key in gone )
		{
			if ( patches.Remove( key, out var patch ) ) patch.Drop();
			standing.Remove( key );
			toStand.Remove( key );
			toCheck.Remove( key );
			retiring.Remove( key );
		}
	}

	//From the eye to a square's middle, and to its nearest point, across the ground
	private static float Away( Vector3 eye, int x, int z, int size, float span )
	{
		var dx = ( ( x + ( size * 0.5f ) ) * span ) - eye.x;
		var dz = ( ( z + ( size * 0.5f ) ) * span ) - eye.z;

		return MathF.Sqrt( ( dx * dx ) + ( dz * dz ) );
	}

	private static float BoxAway( Vector3 eye, int x, int z, int size, float span )
	{
		var dx = MathF.Max( MathF.Max( ( x * span ) - eye.x, eye.x - ( ( x + size ) * span ) ), 0.0f );
		var dz = MathF.Max( MathF.Max( ( z * span ) - eye.z, eye.z - ( ( z + size ) * span ) ), 0.0f );

		return MathF.Sqrt( ( dx * dx ) + ( dz * dz ) );
	}

	//Sends one patch to be worked out on a worker.
	private void Scatter( long key, Patch patch, int layerIndex, int level, int x, int z, Vector3 eye, float away )
	{
		var layer = Layers[layerIndex];

		patch.Scattering = true;
		patch.Stale = false;
		patch.Away = away;

		//The chunks' own triangles, read from the mesh now and copied, so a worker reads them as they are; a bake of a chunk
		//says when its ground changed, and the patch is scattered again then
		var size = 1 << level;
		var grounds = new List<TerrainSoup>();

		for ( var cz = z; cz < Math.Min( z + size, terrain.ChunksDeep ); cz++ )
			for ( var cx = x; cx < Math.Min( x + size, terrain.ChunksWide ); cx++ )
				if ( GroundOf( cx, cz ) is { } one ) grounds.Add( one );

		List<TerrainModifier> near = null;
		Dictionary<long, TerrainMaskTile> mask = null;

		if ( grounds.Count > 0 )
		{
			var min = terrain.ChunkOrigin + ( new Vector2( x, z ) * terrain.ChunkSpan );
			var max = min + new Vector2( terrain.ChunkSpan * size, terrain.ChunkSpan * size );

			foreach ( var modifier in terrain.Modifiers )
			{
				var b = modifier.Bounds;
				if ( b.Maxs.x < min.x || b.Maxs.z < min.y || b.Mins.x > max.x || b.Mins.z > max.y ) continue;

				( near ??= new() ).Add( modifier );
			}

			//The paint mask's tiles over the chunk, as they are now: a worker reads them while the mesh is painted on.
			if ( layer.OnLayers.Count > 0 ) mask = terrain.Mesh?.MaskAround( min, max );
		}

		var ask = new Ask
		{
			Grounds = grounds,
			Modifiers = near?.ToArray() ?? [],
			Eye = new Vector2( eye.x + terrain.ChunkOrigin.x, eye.z + terrain.ChunkOrigin.y ),
			ThinFrom = layer.ThinFrom,
			Mask = mask,
			//Not HashCode, whose seed differs every run, scattering the editor's clutter elsewhere in the game
			Seed = (int)Mix( (uint)Seed, (uint)layerIndex, 0u ),
			Density = MathF.Max( layer.Density, 1e-4f ),
			OnLayers = layer.OnLayers.Count > 0 ? OnMask( layer.OnLayers ) : 0u,
			ScaleMin = layer.Scale.Min,
			ScaleMax = layer.Scale.Max,
			SteepLeast = MathF.Cos( Math.Clamp( layer.MaxSlope, 0.0f, 90.0f ).DegreeToRadian() ),
			SteepMost = MathF.Cos( Math.Clamp( layer.MinSlope, 0.0f, 90.0f ).DegreeToRadian() ),
			Align = layer.AlignToGround,
			Yaw = layer.RandomYaw,
			Sink = layer.Sink,
			World = terrain.WorldTransform,
			OnCard = OnCard && !byHand.Contains( layerIndex ),
			Reach = Reach( layer ),
		};

		var stamped = version;

		System.Threading.Tasks.Task.Run( () =>
		{
			List<Transform> places = null;
			Laid laid = null;

			try
			{
				if ( ask.OnCard ) laid = Lay( ask );
				else places = Work( ask );
			}
			catch ( Exception e )
			{
				Log.Warning( e, "A patch of terrain clutter could not be scattered" );
			}

			scattered.Enqueue( new Scattered { Key = key, Version = stamped, Places = places ?? ( laid is null ? new() : null ), Laid = laid } );
		} );
	}

	private static uint OnMask( List<int> layers )
	{
		var mask = 0u;

		foreach ( var layer in layers )
			if ( layer >= 0 && layer < 32 ) mask |= 1u << layer;

		return mask;
	}

	//Stands up what the workers finished, a couple of milliseconds a frame.
	private void Stand()
	{
		while ( scattered.TryDequeue( out var done ) )
		{
			if ( done.Version != version || !patches.TryGetValue( done.Key, out var patch ) ) continue;

			patch.Scattering = false;
			patch.Waiting = done.Places;
			patch.Stood = 0;

			if ( patch.Waiting is { Count: > 0 } ) toStand.Add( done.Key );

			//What stood here before goes now; the new ones follow in this frame and the next few - or all at once, by the card.
			patch.Drop();
			patch.Set = new StaticInstanceSet( Scene );

			if ( done.Laid is { } laid ) Hand( done.Key, patch, laid );

			if ( patch.Stale ) lookAgain = true;
		}

		if ( toStand.Count == 0 ) return;

		var clock = Stopwatch.StartNew();
		finished.Clear();

		foreach ( var key in toStand )
		{
			if ( !patches.TryGetValue( key, out var patch ) || patch.Waiting is null ) { finished.Add( key ); continue; }

			var layer = (int)( key >> 40 );
			if ( layer >= Layers.Count || Layers[layer]?.Model is not { } model ) { patch.Waiting = null; finished.Add( key ); continue; }

			var shadows = Layers[layer].CastShadows;
			var lod = Layers[layer].LodScale;
			var lodGroup = Layers[layer].LodGroup;
			var over = false;

			while ( patch.Stood < patch.Waiting.Count )
			{
				patch.Set.Add( model, patch.Waiting[patch.Stood++], shadows, null, lod, lodGroup );

				if ( ( patch.Stood & 63 ) == 0 && clock.Elapsed.TotalMilliseconds > StandBudget ) { over = true; break; }
			}

			if ( over ) break;

			patch.Waiting = null;
			finished.Add( key );
		}

		foreach ( var key in finished ) toStand.Remove( key );
	}

	//A patch's copies to the tables as one run the card places; a layer whose run they refuse is scattered here from then on.
	private void Hand( long key, Patch patch, Laid laid )
	{
		var layer = (int)( key >> 40 );
		if ( laid.Copies == 0 || layer >= Layers.Count || Layers[layer]?.Model is not { } model ) return;

		var placed = new Placed( terrain, laid );
		var rules = Layers[layer];

		if ( !patch.Set.AddRun( model, laid.Copies, laid.Sphere, terrain.WorldTransform, placed, rules.CastShadows, rules.LodScale, rules.LodGroup ) )
		{
			placed.Dispose();
			Refuse( layer );
			return;
		}

		patch.Placed = placed;
		toCheck.Add( key );
	}

	//Whether the tables draw the runs handed them since the last look - which they know only once the model is there.
	private void Check()
	{
		if ( toCheck.Count == 0 ) return;

		finished.Clear();

		foreach ( var key in toCheck )
		{
			if ( !patches.TryGetValue( key, out var patch ) || patch.Placed is null || patch.Checked ) { finished.Add( key ); continue; }

			var drawn = patch.Set?.RunsDrawn;
			if ( drawn is null ) continue;

			patch.Checked = true;
			finished.Add( key );

			if ( drawn == false ) Refuse( (int)( key >> 40 ) );
		}

		foreach ( var key in finished ) toCheck.Remove( key );
	}

	private void Refuse( int layer )
	{
		if ( !byHand.Add( layer ) ) return;

		foreach ( var (key, patch) in patches )
			if ( (int)( key >> 40 ) == layer ) patch.Stale = true;

		lookAgain = true;
	}

	//How far a copy reaches past where it stands, in the terrain's metres: the model's sphere at its largest, and the sink.
	private static float Reach( TerrainClutterLayer layer )
	{
		var bounds = layer.Model?.Bounds ?? default;

		return ( ( bounds.Center.Length + ( bounds.Size.Length * 0.5f ) ) * MathF.Max( layer.Scale.Min, layer.Scale.Max ) ) + layer.Sink;
	}

	//Everything a worker needs to scatter one patch, and nothing that changes under it.
	private sealed class Ask
	{
		public List<TerrainSoup> Grounds;
		public TerrainModifier[] Modifiers;
		public Vector2 Eye;
		public float ThinFrom;
		public Dictionary<long, TerrainMaskTile> Mask;
		public int Seed;
		public float Density;
		public uint OnLayers;
		public float ScaleMin, ScaleMax;
		public float SteepLeast, SteepMost;
		public bool Align, Yaw;
		public float Sink;
		public Transform World;
		public bool OnCard;
		public float Reach;
	}

	//What of the density is left at a triangle's middle: all of it out to ThinFrom, then less with the square of the distance
	private static float Thinned( Ask ask, Vector3 middle )
	{
		if ( ask.ThinFrom <= 0.0f ) return 1.0f;

		var dx = middle.x - ask.Eye.x;
		var dz = middle.z - ask.Eye.y;
		var far = ( dx * dx ) + ( dz * dz );
		var near = ask.ThinFrom * ask.ThinFrom;

		return far <= near ? 1.0f : near / far;
	}

	//One patch: every triangle of the chunk's ground grows what its area and the density come to, placed by numbers drawn from where the triangle
	//is - so a patch comes out the same whenever it is asked for, and sculpting one corner of a chunk does not reshuffle the rest of it.
	private static List<Transform> Work( Ask ask )
	{
		var places = new List<Transform>();
		Span<float> layers = stackalloc float[Terrain.MostLayers];

		foreach ( var ground in ask.Grounds )
		{
			for ( var t = 0; t + 2 < ground.Indices.Count; t += 3 )
			{
				var a = ground.Vertices[ground.Indices[t]];
				var b = ground.Vertices[ground.Indices[t + 1]];
				var c = ground.Vertices[ground.Indices[t + 2]];

				var cross = Vector3.Cross( b.Position - a.Position, c.Position - a.Position );
				var area = cross.Length * 0.5f;

				if ( area < 1e-6f ) continue;

				//Which way the triangle faces, by the normals its corners carry - the winding's cross can point either way depending on who is asked.
				var facing = ( a.Normal + b.Normal + c.Normal ).Normal;

				if ( facing.y < ask.SteepLeast || facing.y > ask.SteepMost + 1e-5f ) continue;

				var middle = ( a.Position + b.Position + c.Position ) / 3.0f;

				var bits = Mix( (uint)ask.Seed, (uint)(int)MathF.Round( middle.x * 64.0f ),
					(uint)(int)MathF.Round( middle.z * 64.0f ) ^ ( (uint)(int)MathF.Round( middle.y * 64.0f ) * 0x27D4EB2Fu ) );

				var wanted = area * ask.Density * Thinned( ask, middle );
				var count = (int)wanted;

				if ( Unit( ref bits ) < wanted - count ) count++;

				for ( var n = 0; n < count; n++ )
				{
					//A point of the triangle, evenly: two numbers folded back across its diagonal.
					var u = Unit( ref bits );
					var v = Unit( ref bits );

					if ( u + v > 1.0f ) { u = 1.0f - u; v = 1.0f - v; }

					var w = 1.0f - u - v;
					var chance = Unit( ref bits );
					var sized = Unit( ref bits );
					var turned = Unit( ref bits );

					var point = ( a.Position * w ) + ( b.Position * u ) + ( c.Position * v );

					//What is painted there: the mask over the top of the ground, the corners' paint for the rest.
					if ( ask.OnLayers != 0u )
					{
						layers.Clear();

						var share = 1.0f - TerrainMesh.MaskAt( ask.Mask, point, facing.y, layers );

						a.Paint.AddTo( layers, w * share );
						b.Paint.AddTo( layers, u * share );
						c.Paint.AddTo( layers, v * share );

						var grows = 0.0f;

						for ( var l = 0; l < layers.Length; l++ )
							if ( ( ask.OnLayers & ( 1u << l ) ) != 0u ) grows += layers[l];

						if ( chance >= grows ) continue;
					}

					var clear = true;

					foreach ( var modifier in ask.Modifiers )
					{
						if ( !modifier.Bounds.Contains( point ) || !modifier.Clears( point ) ) continue;

						clear = false;
						break;
					}

					if ( !clear ) continue;

					var scale = ask.ScaleMin + ( ( ask.ScaleMax - ask.ScaleMin ) * sized );
					var turn = ask.Yaw ? Rotation.FromAxis( Vector3.Up, turned * 360.0f ) : Rotation.Identity;

					if ( ask.Align ) turn = Rotation.FromToRotation( Vector3.Up, facing ) * turn;

					places.Add( ask.World.ToWorld( new Transform( point - ( Vector3.Up * ask.Sink ), turn, scale ) ) );

					if ( places.Count >= MostPerPatch ) return places;
				}
			}
		}

		return places;
	}

	//What the card places a patch from, in fours of words as terrain_clutter.shader reads them: the record and sphere
	//it writes as it places, the run, the rules, six a triangle
	private const int TemplateAt = 0;
	private const int BoundAt = 16;
	private const int RunAt = 17;
	private const int RulesAt = 18;
	private const int TrianglesAt = 23;
	private const int TriangleWords = 6;

	//How many of a triangle's copies a modifier's clearing is said for, a bit each; one under a modifier gets no more than this.
	private const int MostCleared = 32;

	private sealed class Laid
	{
		public uint[] Words;
		public int Triangles;
		public int Copies;

		/// <summary>Round every copy, in the world.</summary>
		public System.Numerics.Vector4 Sphere;
	}

	//One patch as the card places it: each triangle's corners, facing, paint, copies, first slot and numbers
	//The same as Work's, so a patch is the same whichever places it
	private static Laid Lay( Ask ask )
	{
		var words = new List<uint>( 4096 );

		for ( var i = 0; i < TrianglesAt * 4; i++ ) words.Add( 0u );

		var copies = 0;
		var triangles = 0;
		var low = new Vector3( float.MaxValue );
		var high = new Vector3( float.MinValue );

		foreach ( var ground in ask.Grounds )
		{
			for ( var t = 0; t + 2 < ground.Indices.Count; t += 3 )
			{
				var a = ground.Vertices[ground.Indices[t]];
				var b = ground.Vertices[ground.Indices[t + 1]];
				var c = ground.Vertices[ground.Indices[t + 2]];

				var cross = Vector3.Cross( b.Position - a.Position, c.Position - a.Position );
				var area = cross.Length * 0.5f;

				if ( area < 1e-6f ) continue;

				var facing = ( a.Normal + b.Normal + c.Normal ).Normal;

				if ( facing.y < ask.SteepLeast || facing.y > ask.SteepMost + 1e-5f ) continue;

				var middle = ( a.Position + b.Position + c.Position ) / 3.0f;

				var bits = Mix( (uint)ask.Seed, (uint)(int)MathF.Round( middle.x * 64.0f ),
					(uint)(int)MathF.Round( middle.z * 64.0f ) ^ ( (uint)(int)MathF.Round( middle.y * 64.0f ) * 0x27D4EB2Fu ) );

				var wanted = area * ask.Density * Thinned( ask, middle );
				var count = (int)wanted;

				if ( Unit( ref bits ) < wanted - count ) count++;
				if ( count == 0 ) continue;

				var min = Vector3.Min( a.Position, Vector3.Min( b.Position, c.Position ) );
				var max = Vector3.Max( a.Position, Vector3.Max( b.Position, c.Position ) );

				//Nothing it grows on in its corners' paint nor a tile of the mask over it: none of its copies would be kept.
				if ( ask.OnLayers != 0u && !Grows( a.Paint, ask.OnLayers ) && !Grows( b.Paint, ask.OnLayers ) && !Grows( c.Paint, ask.OnLayers ) &&
					!Masked( ask.Mask, min, max ) )
					continue;

				var keep = Cleared( ask, a.Position, b.Position, c.Position, min, max, bits, ref count );

				if ( copies + count > MostPerPatch ) break;

				Add( words, a.Position, keep );
				Add( words, b.Position, (uint)copies );
				Add( words, c.Position, (uint)count );
				Add( words, facing, bits );

				words.Add( a.Paint.Layers );
				words.Add( a.Paint.Weights );
				words.Add( b.Paint.Layers );
				words.Add( b.Paint.Weights );
				words.Add( c.Paint.Layers );
				words.Add( c.Paint.Weights );
				words.Add( 0u );
				words.Add( 0u );

				copies += count;
				triangles++;
				low = Vector3.Min( low, min );
				high = Vector3.Max( high, max );
			}
		}

		var laid = new Laid { Triangles = triangles, Copies = copies };

		if ( copies == 0 ) return laid;

		//The rules - see RulesAt in the shader.
		var at = RulesAt * 4;
		var turn = ask.World.Rotation;

		words[at + 0] = ask.OnLayers;
		words[at + 1] = ( ask.Yaw ? 1u : 0u ) | ( ask.Align ? 2u : 0u );
		words[at + 2] = BitConverter.SingleToUInt32Bits( ask.ScaleMin );
		words[at + 3] = BitConverter.SingleToUInt32Bits( ask.ScaleMax );
		words[at + 4] = BitConverter.SingleToUInt32Bits( ask.Sink );
		Set( words, at + 8, ask.World.Position, 0u );
		Set( words, at + 12, new Vector3( turn.x, turn.y, turn.z ), BitConverter.SingleToUInt32Bits( turn.w ) );
		Set( words, at + 16, ask.World.Scale, 0u );

		//The sphere every copy stands inside, in the world: round the triangles, and as far again as a copy reaches.
		var middleOf = ( low + high ) * 0.5f;
		var scale = ask.World.Scale;
		var most = MathF.Max( MathF.Abs( scale.x ), MathF.Max( MathF.Abs( scale.y ), MathF.Abs( scale.z ) ) );
		var centre = ask.World.PointToWorld( middleOf );

		laid.Sphere = new System.Numerics.Vector4( centre.x, centre.y, centre.z, ( ( ( high - low ).Length * 0.5f ) + ask.Reach ) * most );
		laid.Words = words.ToArray();

		return laid;
	}

	private static void Add( List<uint> words, Vector3 v, uint w )
	{
		words.Add( BitConverter.SingleToUInt32Bits( v.x ) );
		words.Add( BitConverter.SingleToUInt32Bits( v.y ) );
		words.Add( BitConverter.SingleToUInt32Bits( v.z ) );
		words.Add( w );
	}

	private static void Set( List<uint> words, int at, Vector3 v, uint w )
	{
		words[at] = BitConverter.SingleToUInt32Bits( v.x );
		words[at + 1] = BitConverter.SingleToUInt32Bits( v.y );
		words[at + 2] = BitConverter.SingleToUInt32Bits( v.z );
		words[at + 3] = w;
	}

	private static bool Grows( in TerrainPaint paint, uint layers )
	{
		for ( var slot = 0; slot < TerrainPaint.Most; slot++ )
		{
			var layer = paint.LayerAt( slot );
			if ( layer < 32 && ( layers & ( 1u << layer ) ) != 0u && paint.WeightAt( slot ) > 0.0f ) return true;
		}

		return false;
	}

	//Whether a tile of the paint mask lies under any of a box.
	private static bool Masked( Dictionary<long, TerrainMaskTile> mask, Vector3 min, Vector3 max )
	{
		if ( mask is null || mask.Count == 0 ) return false;

		var x0 = (int)MathF.Floor( min.x / TerrainMesh.MaskSpan );
		var x1 = (int)MathF.Floor( max.x / TerrainMesh.MaskSpan );
		var z0 = (int)MathF.Floor( min.z / TerrainMesh.MaskSpan );
		var z1 = (int)MathF.Floor( max.z / TerrainMesh.MaskSpan );

		for ( var z = z0; z <= z1; z++ )
			for ( var x = x0; x <= x1; x++ )
				if ( mask.ContainsKey( TerrainMesh.MaskKey( x, z ) ) ) return true;

		return false;
	}

	//A bit a copy, set where no modifier clears its ground - here, as the card cannot ask a modifier
	private static uint Cleared( Ask ask, Vector3 a, Vector3 b, Vector3 c, Vector3 min, Vector3 max, uint bits, ref int count )
	{
		TerrainModifier[] near = null;
		var box = new BBox( min, max );

		foreach ( var modifier in ask.Modifiers )
		{
			if ( !modifier.Bounds.Overlaps( box ) ) continue;

			near = ask.Modifiers;
			break;
		}

		if ( near is null ) return uint.MaxValue;

		count = Math.Min( count, MostCleared );

		var keep = 0u;

		for ( var n = 0; n < count; n++ )
		{
			var u = Unit( ref bits );
			var v = Unit( ref bits );

			if ( u + v > 1.0f ) { u = 1.0f - u; v = 1.0f - v; }

			var w = 1.0f - u - v;

			Unit( ref bits );
			Unit( ref bits );
			Unit( ref bits );

			var point = ( a * w ) + ( b * u ) + ( c * v );
			var clear = true;

			foreach ( var modifier in near )
			{
				if ( !modifier.Bounds.Contains( point ) || !modifier.Clears( point ) ) continue;

				clear = false;
				break;
			}

			if ( clear ) keep |= 1u << n;
		}

		return keep;
	}

	//A patch on the card: the buffer the shader reads, filled here but for the record it writes as it places
	//Patches' words side by side in a few big buffers: standing a patch up while the eye moves takes a slice of one,
	//not a buffer - and a device allocation - of its own. Counted in fours of words, as the shader reads them
	private static class Pages
	{
		private const int PageQuads = 1 << 18;

		private sealed class Page
		{
			public GpuBuffer<uint> Buffer;
			public int Size;
			public readonly List<(int At, int Count)> Holes = new();
		}

		private static readonly List<Page> pages = new();

		public static (GpuBuffer<uint> Buffer, int At) Take( int quads )
		{
			foreach ( var page in pages )
			{
				for ( var i = 0; i < page.Holes.Count; i++ )
				{
					var (at, room) = page.Holes[i];
					if ( room < quads ) continue;

					if ( room == quads ) page.Holes.RemoveAt( i );
					else page.Holes[i] = ( at + quads, room - quads );

					return ( page.Buffer, at );
				}
			}

			var size = Math.Max( PageQuads, quads );
			var made = new Page { Buffer = new GpuBuffer<uint>( size * 4, GpuBuffer.UsageFlags.Structured, "terrain clutter" ), Size = size };

			if ( size > quads ) made.Holes.Add( ( quads, size - quads ) );
			pages.Add( made );

			return ( made.Buffer, 0 );
		}

		//A slice back, joined to the holes either side of it; a page left empty is freed, but for one ordinary page kept
		//spare, so an eye pacing a patch's edge does not make and free a buffer each time
		public static void Give( GpuBuffer<uint> buffer, int at, int quads )
		{
			var index = -1;

			for ( var p = 0; p < pages.Count; p++ )
			{
				if ( ReferenceEquals( pages[p].Buffer, buffer ) ) { index = p; break; }
			}

			if ( index < 0 ) return;

			var page = pages[index];

			var holes = page.Holes;
			var i = 0;
			while ( i < holes.Count && holes[i].At < at ) i++;

			holes.Insert( i, ( at, quads ) );

			if ( i + 1 < holes.Count && holes[i].At + holes[i].Count == holes[i + 1].At )
			{
				holes[i] = ( holes[i].At, holes[i].Count + holes[i + 1].Count );
				holes.RemoveAt( i + 1 );
			}

			if ( i > 0 && holes[i - 1].At + holes[i - 1].Count == holes[i].At )
			{
				holes[i - 1] = ( holes[i - 1].At, holes[i - 1].Count + holes[i].Count );
				holes.RemoveAt( i );
			}

			if ( holes.Count != 1 || holes[0].Count != page.Size ) return;
			if ( page.Size == PageQuads && pages.Count == 1 ) return;

			page.Buffer.Dispose();
			pages.RemoveAt( index );
		}
	}

	private sealed class Placed : IInstanceRun, IDisposable
	{
		private readonly Terrain terrain;
		private readonly int triangles;
		private GpuBuffer<uint> buffer;
		private readonly int at, quads;

		private static ComputeShader Shader => field ??= new ComputeShader( "shaders/engine/terrain_clutter.shader", "MainCs" );

		public Placed( Terrain terrain, Laid laid )
		{
			this.terrain = terrain;
			triangles = laid.Triangles;
			quads = ( laid.Words.Length + 3 ) / 4;

			( buffer, at ) = Pages.Take( quads );
			buffer.SetData( laid.Words.AsSpan(), at * 4 );
		}

		public bool Place( ulong instances, int head, int count, in CoreSceneTables.InstanceRecord template, System.Numerics.Vector4 bound )
		{
			if ( buffer is null || triangles == 0 ) return true;

			Span<uint> header = stackalloc uint[RulesAt * 4];
			header.Clear();

			MemoryMarshal.Write( MemoryMarshal.AsBytes( header[( TemplateAt * 4 )..] ), in template );
			MemoryMarshal.Write( MemoryMarshal.AsBytes( header[( BoundAt * 4 )..] ), in bound );

			header[( RunAt * 4 ) + 0] = (uint)head;
			header[( RunAt * 4 ) + 1] = (uint)count;
			header[( RunAt * 4 ) + 2] = (uint)triangles;

			if ( !CoreRenderDevice.FrameWrite( buffer.Handle, (long)at * 16, MemoryMarshal.AsBytes( header ) ) ) return false;

			//The numbers picture is where the paint mask is said to be; without one only the corners' paint counts. Then where the patch starts in its page.
			Shader.Attributes.Set( "Clutter", new Vector4( terrain.IsValid() ? terrain.ParamsMap?.Index ?? 0 : 0, at, 0.0f, 0.0f ) );

			Span<ulong> buffers = stackalloc ulong[2] { buffer.Handle, instances };

			return Shader.RunApartOver( buffers, triangles, 1, 1 );
		}

		public void Dispose()
		{
			if ( buffer is not null ) Pages.Give( buffer, at, quads );
			buffer = null;
		}
	}

	private static uint Mix( uint seed, uint x, uint z )
	{
		var h = seed ^ ( x * 0x9E3779B1u ) ^ ( z * 0x85EBCA77u );

		h ^= h >> 16; h *= 0x7FEB352Du;
		h ^= h >> 15; h *= 0x846CA68Bu;
		h ^= h >> 16;

		//Nought is the one state the run below never leaves.
		return h == 0u ? 1u : h;
	}

	//The next number from nought up to one out of a run of bits.
	private static float Unit( ref uint bits )
	{
		bits ^= bits << 13;
		bits ^= bits >> 17;
		bits ^= bits << 5;

		return ( bits >> 8 ) * ( 1.0f / 16777216.0f );
	}
}
