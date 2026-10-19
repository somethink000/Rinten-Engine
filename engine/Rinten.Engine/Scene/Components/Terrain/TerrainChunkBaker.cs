using System.Collections.Generic;

namespace Rinten;

//What one chunk of the ground came to, once every modifier has had its say.
internal sealed class TerrainChunkBake
{
	/// <summary>One material's worth of geometry, as the engine's own vertices.</summary>
	public sealed class Part
	{
		public Material Material;
		public readonly List<Vertex> Vertices = new();
		public readonly List<int> Indices = new();
	}

	/// <summary>What the terrain's own material draws. Let go of once the models are written - see <see cref="Drop"/>.</summary>
	public readonly List<Vertex> Vertices = new();
	public readonly List<int> Indices = new();

	/// <summary>And what was set in, by what it wears.</summary>
	public readonly List<Part> Parts = new();

	/// <summary>All of it as the simulation wants it: points and triangles, nothing else.</summary>
	public readonly List<Vector3> CollisionPoints = new();
	public readonly List<int> CollisionIndices = new();

	/// <summary>The ground alone, with what it is painted with - what is scattered over a chunk is scattered over this.</summary>
	public TerrainSoup Ground;

	/// <summary>Which of the terrain's layers each of the drawn vertex's weights is for - see TerrainChunkBaker.Palette.</summary>
	public byte[] Palette;

	/// <summary>The two models as compiled bytes, written on the worker that baked the chunk. The main thread turns them into models and lets them go - see Terrain.Rendering.Stand.</summary>
	public byte[] GroundBytes;
	public byte[] SetInBytes;

	/// <summary>What those models are called.</summary>
	public string GroundName, SetInName;

	/// <summary>Lets go of the geometry the models were written from.</summary>
	public void Drop()
	{
		Vertices.Clear();
		Vertices.TrimExcess();

		Indices.Clear();
		Indices.TrimExcess();

		Parts.Clear();
		Parts.TrimExcess();
	}
}

//Everything the baking of one chunk reads, copied out on the main thread so a worker can read it while the mesh is sculpted.
internal sealed class TerrainChunkJob
{
	public int Chunk;
	public int Version;

	/// <summary>The chunk's own triangles and the ground around it that a modifier reaches. A triangle that is some other chunk's says so by <see cref="NotOurs"/>.</summary>
	public TerrainSoup Ground;

	public TerrainModifier[] Modifiers = [];

	/// <summary>The chunk's rectangle of the ground plane: which of a solid's triangles are this chunk's to keep.</summary>
	public Vector2 Min, Max;

	/// <summary>What a material number means in this chunk: nought is the terrain's paint, then the mesh's own inset materials, then each modifier's.</summary>
	public List<Material> Materials = new();

	/// <summary>Where each modifier's materials start in that list, less one.</summary>
	public int[] ModifierMaterialsAt = [];

	public TerrainChunkBake Result;

	/// <summary>What holds the terrain this chunk is of - its own resource, or the scene it is set in - whose shipped pack is looked in first. See TerrainBakeCache.</summary>
	public string Shipped;

	/// <summary>Whether anybody is going to draw this - false where the game runs without a window.</summary>
	public bool Draws;

	/// <summary>Skip coarse LOD levels while sculpting; the chunk is rebaked properly when the edit ends</summary>
	public bool Coarse;

	/// <summary>What the two models are called. Named on the main thread, where the count is kept.</summary>
	public string GroundName, SetInName;

	/// <summary>The material of a triangle that takes part in the cut and belongs to another chunk.</summary>
	public const ushort NotOurs = ushort.MaxValue;
}

//Bakes one chunk's render and collision triangles; pure and safe on a worker thread
//Neighbour triangles join the union cut, but only pieces owned by this chunk are kept
internal static class TerrainChunkBaker
{
	public static void Build( TerrainChunkJob job )
	{
		var ground = job.Ground;

		foreach ( var modifier in job.Modifiers )
			modifier.Shape( ground );

		//What this chunk baked to last, shipped or on this machine; almost all the cost below is the model - see TerrainBakeCache
		var key = TerrainBakeCache.Key( job );

		TerrainBakeCache.Note( job, key );

		if ( key is not null && TerrainBakeCache.Read( key, job.Shipped ) is { } kept )
		{
			kept.GroundName = job.GroundName;
			kept.SetInName = job.SetInName;

			//The models were written with it, and it comes out of the ground they were written from the same way again.
			kept.Palette = Palette( kept.Ground.Vertices );

			job.Result = kept;
			return;
		}

		for ( var m = 0; m < job.Modifiers.Length; m++ )
		{
			var modifier = job.Modifiers[m];
			if ( modifier.Solid is not { } solid || solid.Indices.Count < 3 ) continue;

			var marked = Marked( solid, job, job.ModifierMaterialsAt[m] );
			var joined = new TerrainSoup();

			TerrainMeshUnion.Union( ground, marked, joined, modifier.Carve );

			ground = joined;
		}

		var bake = Split( ground, job );

		Write( job, bake );
		bake.Drop();

		if ( key is not null ) TerrainBakeCache.Write( key, bake );

		job.Result = bake;
	}

	//Compiles the chunk's models to bytes on the worker; main thread only does Model.FromBytes
	private static void Write( TerrainChunkJob job, TerrainChunkBake bake )
	{
		if ( !job.Draws ) return;

		bake.GroundName = job.GroundName;
		bake.SetInName = job.SetInName;

		if ( bake.Indices.Count >= 3 )
		{
			var mesh = new Mesh();
			mesh.CreateVertexBuffer( bake.Vertices.Count, bake.Vertices );
			mesh.CreateIndexBuffer( bake.Indices.Count, bake.Indices );

			bake.GroundBytes = Levels( Model.Builder.WithName( job.GroundName ).WithoutCollision().AddMesh( mesh ), job ).ToBytes();
		}

		if ( bake.Parts.Count == 0 ) return;

		var builder = Model.Builder.WithName( job.SetInName ).WithoutCollision();
		var any = false;

		foreach ( var part in bake.Parts )
		{
			if ( part.Indices.Count < 3 ) continue;

			var mesh = new Mesh( part.Material );
			mesh.CreateVertexBuffer( part.Vertices.Count, part.Vertices );
			mesh.CreateIndexBuffer( part.Indices.Count, part.Indices );

			builder.AddMesh( mesh );
			any = true;
		}

		if ( any ) bake.SetInBytes = Levels( builder, job ).ToBytes();
	}

	private static ModelBuilder Levels( ModelBuilder builder, TerrainChunkJob job )
		=> job.Coarse ? builder.WithoutLevelsOfDetail().WithLockedBorder() : builder.WithLockedBorder();

	//A solid as this chunk sees it: its materials as the chunk numbers them, and the triangles that are another chunk's marked so.
	private static TerrainSoup Marked( TerrainSoup solid, TerrainChunkJob job, int materialsAt )
	{
		var marked = new TerrainSoup();

		marked.Vertices.AddRange( solid.Vertices );
		marked.Indices.AddRange( solid.Indices );

		for ( var t = 0; t < solid.Materials.Count; t++ )
		{
			var a = solid.Vertices[solid.Indices[t * 3]].Position;
			var b = solid.Vertices[solid.Indices[( t * 3 ) + 1]].Position;
			var c = solid.Vertices[solid.Indices[( t * 3 ) + 2]].Position;

			var x = ( a.x + b.x + c.x ) / 3.0f;
			var z = ( a.z + b.z + c.z ) / 3.0f;

			var ours = x >= job.Min.x && z >= job.Min.y && x < job.Max.x && z < job.Max.y;
			var material = solid.Materials[t];

			marked.Materials.Add( !ours ? TerrainChunkJob.NotOurs : material == 0 ? (ushort)0 : (ushort)( materialsAt + material ) );
		}

		return marked;
	}

	//The finished soup, material by material, as the engine's vertices.
	private static TerrainChunkBake Split( TerrainSoup soup, TerrainChunkJob job )
	{
		var bake = new TerrainChunkBake { Ground = new TerrainSoup() };

		var groundAt = new Dictionary<int, int>();
		var collisionAt = new Dictionary<int, int>();
		var partAt = new Dictionary<long, int>();
		var parts = new Dictionary<ushort, TerrainChunkBake.Part>();

		for ( var t = 0; t < soup.Materials.Count; t++ )
		{
			var material = soup.Materials[t];
			if ( material == TerrainChunkJob.NotOurs ) continue;

			for ( var k = 0; k < 3; k++ )
			{
				var source = soup.Indices[( t * 3 ) + k];
				var vertex = soup.Vertices[source];

				if ( !collisionAt.TryGetValue( source, out var solidAt ) )
				{
					solidAt = bake.CollisionPoints.Count;
					bake.CollisionPoints.Add( vertex.Position );
					collisionAt[source] = solidAt;
				}

				bake.CollisionIndices.Add( solidAt );

				if ( material == 0 )
				{
					if ( !groundAt.TryGetValue( source, out var at ) )
					{
						at = bake.Vertices.Count;
						groundAt[source] = at;

						//Painted below, once the chunk's palette is known.
						bake.Vertices.Add( new Vertex
						{
							Position = vertex.Position,
							Normal = vertex.Normal,
							Tangent = new Vector4( 1.0f, 0.0f, 0.0f, -1.0f ),
						} );

						bake.Ground.Vertices.Add( vertex );
					}

					bake.Indices.Add( at );
					bake.Ground.Indices.Add( at );
					if ( k == 2 ) bake.Ground.Materials.Add( 0 );

					continue;
				}

				if ( !parts.TryGetValue( material, out var part ) )
				{
					part = new TerrainChunkBake.Part { Material = material <= job.Materials.Count ? job.Materials[material - 1] : null };
					parts[material] = part;
					bake.Parts.Add( part );
				}

				var key = ( (long)material << 32 ) | (uint)source;

				if ( !partAt.TryGetValue( key, out var placed ) )
				{
					placed = part.Vertices.Count;
					partAt[key] = placed;

					part.Vertices.Add( new Vertex
					{
						Position = vertex.Position,
						Normal = vertex.Normal,
						Tangent = vertex.Tangent,
						Color = Color32.White,
						TexCoord0 = new Vector4( vertex.Uv.x, vertex.Uv.y, 0.0f, 0.0f ),
					} );
				}

				part.Indices.Add( placed );
			}
		}

		bake.Palette = Palette( bake.Ground.Vertices );

		Span<float> slots = stackalloc float[Terrain.MaxLayers];

		for ( var i = 0; i < bake.Vertices.Count; i++ )
		{
			Slots( bake.Ground.Vertices[i].Paint, bake.Palette, slots );

			//Spread over colour and UVs, since the simplifier keeps UVs but not colour. Slots 6 and 7 go in the colour's spare two: the cluster
			//vertex has no more coordinates to give, and the last two wandering a little across a collapse is the price.
			var drawn = bake.Vertices[i];
			drawn.Color = new Color32( Byte01( slots[0] ), Byte01( slots[1] ), Byte01( slots[6] ), Byte01( slots[7] ) );
			drawn.TexCoord0 = new Vector4( slots[2], slots[3], 0.0f, 0.0f );
			drawn.TexCoord1 = new Vector4( slots[4], slots[5], 0.0f, 0.0f );
			bake.Vertices[i] = drawn;
		}

		return bake;
	}

	//What its drawn vertices weigh; a ninth layer and on are not drawn in this chunk.
	/// <summary>The eight layers a chunk's ground wears most, in the order of their numbers.</summary>
	public static byte[] Palette( List<TerrainSoup.Vertex> ground )
	{
		Span<float> totals = stackalloc float[Terrain.MostLayers];
		totals.Clear();

		foreach ( var vertex in ground ) vertex.Paint.AddTo( totals, 1.0f );

		var palette = new byte[Terrain.MaxLayers];
		Span<bool> taken = stackalloc bool[Terrain.MostLayers];
		taken.Clear();

		var count = 0;

		for ( ; count < Terrain.MaxLayers; count++ )
		{
			var best = -1;

			for ( var l = 0; l < totals.Length; l++ )
				if ( !taken[l] && totals[l] > 0.0f && ( best < 0 || totals[l] > totals[best] ) ) best = l;

			if ( best < 0 ) break;

			taken[best] = true;
		}

		//In the order of their numbers, the slots past the last left at nought and weighing nothing.
		var at = 0;
		for ( var l = 0; l < taken.Length; l++ ) if ( taken[l] ) palette[at++] = (byte)l;

		if ( at == 0 ) palette[0] = 0;

		return palette;
	}

	/// <summary>How much of each palette slot's layer a paint holds. A slot past the palette's last, which repeats layer nought, weighs nothing.</summary>
	public static void Slots( in TerrainPaint paint, byte[] palette, Span<float> into )
	{
		into.Clear();

		for ( var s = 0; s < palette.Length && s < into.Length; s++ )
		{
			if ( s > 0 && palette[s] <= palette[s - 1] ) break;

			into[s] = paint.Of( palette[s] );
		}
	}

	/// <summary>A palette as the two numbers the terrain shader reads it from, six bits a slot - exact in a float.</summary>
	public static Vector4 PaletteNumbers( byte[] palette )
	{
		uint low = 0, high = 0;

		for ( var s = 0; s < 4; s++ )
		{
			low |= (uint)( palette[s] & 63 ) << ( s * 6 );
			high |= (uint)( palette[s + 4] & 63 ) << ( s * 6 );
		}

		return new Vector4( low, high, 0.0f, 0.0f );
	}

	//Nought to one as a colour byte.
	private static byte Byte01( float value ) => (byte)Math.Clamp( MathF.Round( value * 255.0f ), 0.0f, 255.0f );
}
