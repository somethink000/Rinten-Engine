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

/// <summary>Models scattered over a terrain by paint and slope, read from baked chunks, placed and culled on the card.</summary>
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

	//Whether patches are placed on the card: the full frame's clusters, which the portable frame and a headless run have none of.
	private static bool OnCard => !Application.IsHeadless && CoreNative.Available && !CoreRenderDevice.PortableFrame && Rendering.DrawSettings.Clusters;

	//Layers whose model the tables would not draw from clusters - translucent, say - placed here instead.
	private readonly HashSet<int> byHand = new();

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
			terrain.KeepWhole( this, 0.0f );
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
		now = HashCode.Combine( now, Layers.Count, terrain.ChunksWide, terrain.ChunkSpan, terrain.ChunkOrigin, OnCard );

		if ( now != settings )
		{
			settings = now;
			Again();
		}

		Stand();
		Check();

		//Patches stand on the ground of whole chunks only: kept whole as far out as one is taken away - see Look.
		var most = 0.0f;
		foreach ( var layer in Layers ) if ( layer?.Model is not null && layer.Density > 0.0f ) most = MathF.Max( most, layer.Distance );

		terrain.KeepWhole( this, most > 0.0f ? ( most * 1.15f ) + ( terrain.ChunkSpan * 0.7072f ) : 0.0f );

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
			if ( patches.Remove( key, out var patch ) ) patch.Drop();
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

			//What stood here before goes now; the new ones follow in this frame and the next few - or all at once, by the card.
			patch.Drop();
			patch.Set = new StaticInstanceSet( Scene );

			if ( done.Laid is { } laid ) Hand( done.Key, patch, laid );

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
	}

	//Whether the tables draw the runs handed them since the last look - which they know only once the model is there.
	private void Check()
	{
		foreach ( var key in standing )
		{
			if ( !patches.TryGetValue( key, out var patch ) || patch.Placed is null || patch.Checked ) continue;

			var drawn = patch.Set?.RunsDrawn;
			if ( drawn is null ) continue;

			patch.Checked = true;

			if ( drawn == false ) Refuse( (int)( key >> 40 ) );
		}
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
		public bool OnCard;
		public float Reach;
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

	//What the card places one patch from - see terrain_clutter.shader, whose layout this is in fours of words: the record every copy starts from
	//and the model's sphere, written when it places them; the run; the rules; six a triangle.
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

	//One patch as the card places it: each triangle its corners, which way it faces, its corners' paint, how many copies, where their
	//slots start and the numbers they are drawn from - the same as Work's, so a patch is the same whichever places it.
	private static Laid Lay( Ask ask )
	{
		var ground = ask.Ground;
		var words = new List<uint>( 4096 );

		for ( var i = 0; i < TrianglesAt * 4; i++ ) words.Add( 0u );

		var copies = 0;
		var triangles = 0;
		var low = new Vector3( float.MaxValue );
		var high = new Vector3( float.MinValue );

		if ( ground is not null )
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

				var wanted = area * ask.Density;
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

	//A bit a copy, set where no modifier clears the ground it would stand on - worked out here, as the card cannot ask a modifier.
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

	//A patch on the card: the buffer the shader reads it from, filled here but for the record every copy starts from, which is written as it places them.
	private sealed class Placed : IInstanceRun, IDisposable
	{
		private readonly Terrain terrain;
		private readonly int triangles;
		private GpuBuffer<uint> buffer;

		private static ComputeShader Shader => field ??= new ComputeShader( "shaders/engine/terrain_clutter.shader", "MainCs" );

		public Placed( Terrain terrain, Laid laid )
		{
			this.terrain = terrain;
			triangles = laid.Triangles;
			buffer = new GpuBuffer<uint>( laid.Words.Length, GpuBuffer.UsageFlags.Structured, "terrain clutter" );
			buffer.SetData( laid.Words.AsSpan() );
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

			if ( !CoreRenderDevice.FrameWrite( buffer.Handle, 0, MemoryMarshal.AsBytes( header ) ) ) return false;

			//The numbers picture is where the paint mask is said to be; without one only the corners' paint counts.
			Shader.Attributes.Set( "Clutter", new Vector4( terrain.IsValid() ? terrain.ParamsMap?.Index ?? 0 : 0, 0.0f, 0.0f, 0.0f ) );

			Span<ulong> buffers = stackalloc ulong[2] { buffer.Handle, instances };

			return Shader.RunApartOver( buffers, triangles, 1, 1 );
		}

		public void Dispose()
		{
			buffer?.Dispose();
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
