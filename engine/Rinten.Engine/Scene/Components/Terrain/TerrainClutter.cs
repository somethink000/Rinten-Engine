using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

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
	[Property, Range( 8.0f, 2048.0f )]
	public float Distance { get; set; } = 96.0f;

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

/// <summary>Models scattered over a terrain by paint and slope, read from baked chunks, GPU culled.</summary>
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
	}

	private sealed class Scattered
	{
		public long Key;
		public int Version;
		public List<Transform> Places;
	}

	private Terrain terrain;
	private readonly Dictionary<long, Patch> patches = new();
	private readonly ConcurrentQueue<Scattered> scattered = new();
	private readonly List<long> standing = new();
	private readonly List<long> gone = new();

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
		foreach ( var patch in patches.Values ) patch.Set?.Dispose();

		patches.Clear();
		standing.Clear();
		version++;
		lookAgain = true;
	}

	private void OnChunkBaked( int x, int z )
	{
		for ( var layer = 0; layer < Layers.Count; layer++ )
		{
			if ( !patches.TryGetValue( KeyOf( layer, x, z ), out var patch ) ) continue;

			//Left standing until the new scatter is ready to take its place.
			patch.Stale = true;
			lookAgain = true;
		}
	}

	private static long KeyOf( int layer, int x, int z ) => ( (long)layer << 40 ) | ( (long)z << 20 ) | (uint)x;

	protected override void OnUpdate()
	{
		if ( Application.IsHeadless ) return;

		if ( !terrain.IsValid() ) { Find(); if ( !terrain.IsValid() ) return; }
		if ( terrain.Storage is null || terrain.ChunksWide == 0 ) return;

		var now = Seed;
		foreach ( var layer in Layers ) now = HashCode.Combine( now, layer?.Settings() ?? 0 );
		now = HashCode.Combine( now, Layers.Count, terrain.ChunksWide, terrain.ChunkSpan, terrain.ChunkOrigin );

		if ( now != settings )
		{
			settings = now;
			Again();
		}

		Stand();

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
	}

	//Which patches should stand for an eye here: the missing are sent to be scattered, the far are taken away.
	private void Look( Vector3 eye, float span )
	{
		var reach = span * 0.7072f;

		for ( var l = 0; l < Layers.Count; l++ )
		{
			var layer = Layers[l];
			if ( layer?.Model is null || layer.Density <= 0.0f ) continue;

			var x0 = Math.Clamp( (int)MathF.Floor( ( eye.x - layer.Distance ) / span ), 0, terrain.ChunksWide - 1 );
			var x1 = Math.Clamp( (int)MathF.Floor( ( eye.x + layer.Distance ) / span ), 0, terrain.ChunksWide - 1 );
			var z0 = Math.Clamp( (int)MathF.Floor( ( eye.z - layer.Distance ) / span ), 0, terrain.ChunksDeep - 1 );
			var z1 = Math.Clamp( (int)MathF.Floor( ( eye.z + layer.Distance ) / span ), 0, terrain.ChunksDeep - 1 );

			for ( var z = z0; z <= z1; z++ )
			{
				for ( var x = x0; x <= x1; x++ )
				{
					if ( Away( eye, x, z, span ) > layer.Distance + reach ) continue;

					var key = KeyOf( l, x, z );

					if ( !patches.TryGetValue( key, out var patch ) )
					{
						patch = new Patch { Stale = true };
						patches[key] = patch;
						standing.Add( key );
					}

					if ( patch.Stale && !patch.Scattering ) Scatter( key, patch, l, x, z );
				}
			}
		}

		//Taken away a little further out than they are put up, so an eye pacing a boundary does not scatter the same patch over and over.
		gone.Clear();

		foreach ( var key in standing )
		{
			var l = (int)( key >> 40 );
			var z = (int)( ( key >> 20 ) & 0xFFFFF );
			var x = (int)( key & 0xFFFFF );

			if ( l < Layers.Count && Layers[l] is { } layer && Away( eye, x, z, span ) <= ( layer.Distance * 1.15f ) + reach ) continue;

			gone.Add( key );
		}

		foreach ( var key in gone )
		{
			if ( patches.Remove( key, out var patch ) ) patch.Set?.Dispose();
			standing.Remove( key );
		}
	}

	private static float Away( Vector3 eye, int x, int z, float span )
	{
		var dx = ( ( x + 0.5f ) * span ) - eye.x;
		var dz = ( ( z + 0.5f ) * span ) - eye.z;

		return MathF.Sqrt( ( dx * dx ) + ( dz * dz ) );
	}

	//Sends one patch to be worked out on a worker.
	private void Scatter( long key, Patch patch, int layerIndex, int x, int z )
	{
		var layer = Layers[layerIndex];

		patch.Scattering = true;
		patch.Stale = false;

		//The chunk's ground as it was last baked: never changed once made, so a worker reads it as it is. None
		//yet is nothing to grow on yet - the chunk says when it has baked, and the patch is scattered then.
		var ground = terrain.BakedGround( x, z );

		List<TerrainModifier> near = null;
		Dictionary<long, TerrainMaskTile> mask = null;

		if ( ground is not null )
		{
			var min = terrain.ChunkOrigin + ( new Vector2( x, z ) * terrain.ChunkSpan );
			var max = min + new Vector2( terrain.ChunkSpan, terrain.ChunkSpan );

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
			Ground = ground,
			Modifiers = near?.ToArray() ?? [],
			Mask = mask,
			Seed = HashCode.Combine( Seed, layerIndex ),
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
		};

		var stamped = version;

		System.Threading.Tasks.Task.Run( () =>
		{
			List<Transform> places = null;

			try
			{
				places = Work( ask );
			}
			catch ( Exception e )
			{
				Log.Warning( e, "A patch of terrain clutter could not be scattered" );
			}

			scattered.Enqueue( new Scattered { Key = key, Version = stamped, Places = places ?? new() } );
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

			//What stood here before goes now; the new ones follow in this frame and the next few.
			patch.Set?.Dispose();
			patch.Set = new StaticInstanceSet( Scene );

			if ( patch.Stale ) lookAgain = true;
		}

		var clock = Stopwatch.StartNew();

		foreach ( var key in standing )
		{
			if ( !patches.TryGetValue( key, out var patch ) || patch.Waiting is null ) continue;

			var layer = (int)( key >> 40 );
			if ( layer >= Layers.Count || Layers[layer]?.Model is not { } model ) { patch.Waiting = null; continue; }

			var shadows = Layers[layer].CastShadows;
			var lod = Layers[layer].LodScale;
			var lodGroup = Layers[layer].LodGroup;

			while ( patch.Stood < patch.Waiting.Count )
			{
				patch.Set.Add( model, patch.Waiting[patch.Stood++], shadows, null, lod, lodGroup );

				if ( ( patch.Stood & 63 ) == 0 && clock.Elapsed.TotalMilliseconds > StandBudget ) return;
			}

			patch.Waiting = null;
		}
	}

	//Everything a worker needs to scatter one patch, and nothing that changes under it.
	private sealed class Ask
	{
		public TerrainSoup Ground;
		public TerrainModifier[] Modifiers;
		public Dictionary<long, TerrainMaskTile> Mask;
		public int Seed;
		public float Density;
		public uint OnLayers;
		public float ScaleMin, ScaleMax;
		public float SteepLeast, SteepMost;
		public bool Align, Yaw;
		public float Sink;
		public Transform World;
	}

	//One patch: every triangle of the chunk's ground grows what its area and the density come to, placed by numbers drawn from where the triangle
	//is - so a patch comes out the same whenever it is asked for, and sculpting one corner of a chunk does not reshuffle the rest of it.
	private static List<Transform> Work( Ask ask )
	{
		var places = new List<Transform>();
		var ground = ask.Ground;

		Span<float> layers = stackalloc float[Terrain.MostLayers];

		if ( ground is null ) return places;

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

			var wanted = area * ask.Density;
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

		return places;
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
