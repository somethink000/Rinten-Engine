using System.Collections.Generic;
using Rinten.Meshlets;

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

	/// <summary>Which terrain layer each weight of the drawn vertex is for - see TerrainChunkBaker.Palette.</summary>
	public byte[] Palette;

	/// <summary>The two models as compiled bytes, written on the worker that baked the chunk. The main thread turns them into models and lets them go - see Terrain.Rendering.Stand.</summary>
	public byte[] GroundBytes;
	public byte[] SetInBytes;

	/// <summary>What those models are called.</summary>
	public string GroundName, SetInName;

	/// <summary>The two drawn past where the chunk is kept whole (Thinned); null for none.</summary>
	public byte[] FarGroundBytes;
	public byte[] FarSetInBytes;

	/// <summary>Whether far models were made for it at all - not while the ground is being sculpted.</summary>
	public bool HasFar;

	/// <summary>Whether it holds the whole chunk, or only what a far one keeps: its far models, palette and collision.</summary>
	public bool Whole = true;

	/// <summary>The chunk's shape count it was gathered at - see Terrain.Chunk.Shaped.</summary>
	public int Shaped;

	/// <summary>The ground's top seen from above, for the shader to paint and light by place - see TerrainChunkBaker.Picture.</summary>
	public byte[] Picture;

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

	/// <summary>The terrain's resource or scene, whose shipped pack is looked in first - see TerrainBakeCache.</summary>
	public string Shipped;

	/// <summary>Whether anybody is going to draw this - false where the game runs without a window.</summary>
	public bool Draws;

	/// <summary>Skip coarse LOD levels while sculpting; the chunk is rebaked properly when the edit ends</summary>
	public bool Coarse;

	/// <summary>Whether the chunk is kept whole; a far one reads back only far models and collision.</summary>
	public bool Whole = true;

	/// <summary>Whether its ground changed since it was last baked, rather than it only being read again whole or far.</summary>
	public bool Changed;

	/// <summary>The chunk's shape count when gathered - see Terrain.Chunk.Shaped.</summary>
	public int Shaped;

	/// <summary>Whether the card lacks the chunk's picture at this shape, so the bake makes it - see Terrain.Picture.</summary>
	public bool Pictured;

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

		//Made rather than kept in the cache, which every shipped terrain would have to bake again for it - only for a shape the card lacks
		var picture = job.Draws && job.Pictured ? Picture( ground, job ) : null;

		if ( key is not null && TerrainBakeCache.Read( key, job.Shipped, job.Whole ) is { } kept )
		{
			kept.GroundName = job.GroundName;
			kept.SetInName = job.SetInName;
			kept.Picture = picture;

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

		bake.Picture = picture;
		job.Result = bake;
	}

	/// <summary>Steps along a side of a chunk's picture; its points are one more, the far edge shared with the next chunk.</summary>
	public const int PictureCells = 64;
	public const int PictureSide = PictureCells + 1;

	/// <summary>Bytes a point of the picture takes: its paint (Terrain.PackPoint), then its top as a half and its normal in two bytes.</summary>
	public const int PicturePointBytes = 8;

	//The ground's top over a grid of points seen from above, as terrain_layers.slang's PictureInto reads it. What the
	//shader paints and lights the top by, so a far model or a coarse level - whose corners moved - paints the same places
	private static byte[] Picture( TerrainSoup soup, TerrainChunkJob job )
	{
		if ( soup.Indices.Count < 3 ) return null;

		const int side = PictureSide;

		var step = ( job.Max.x - job.Min.x ) / PictureCells;
		var x0 = job.Min.x;
		var z0 = job.Min.y;

		var top = new float[side * side];
		var paint = new TerrainPaint[side * side];
		var normal = new Vector3[side * side];
		Array.Fill( top, TerrainMesh.NoGround );

		var vertices = soup.Vertices;
		var indices = soup.Indices;

		for ( var t = 0; t + 2 < indices.Count; t += 3 )
		{
			var a = vertices[indices[t]];
			var b = vertices[indices[t + 1]];
			var c = vertices[indices[t + 2]];

			var pa = a.Position;
			var pb = b.Position;
			var pc = c.Position;

			var area = ( ( pb.x - pa.x ) * ( pc.z - pa.z ) ) - ( ( pc.x - pa.x ) * ( pb.z - pa.z ) );
			if ( MathF.Abs( area ) < 1e-8f ) continue;

			var i0 = Math.Max( (int)MathF.Ceiling( ( MathF.Min( pa.x, MathF.Min( pb.x, pc.x ) ) - x0 ) / step ), 0 );
			var i1 = Math.Min( (int)MathF.Floor( ( MathF.Max( pa.x, MathF.Max( pb.x, pc.x ) ) - x0 ) / step ), PictureCells );
			var j0 = Math.Max( (int)MathF.Ceiling( ( MathF.Min( pa.z, MathF.Min( pb.z, pc.z ) ) - z0 ) / step ), 0 );
			var j1 = Math.Min( (int)MathF.Floor( ( MathF.Max( pa.z, MathF.Max( pb.z, pc.z ) ) - z0 ) / step ), PictureCells );

			for ( var j = j0; j <= j1; j++ )
			{
				var pz = z0 + ( j * step );

				for ( var i = i0; i <= i1; i++ )
				{
					var px = x0 + ( i * step );

					var wb = ( ( ( px - pa.x ) * ( pc.z - pa.z ) ) - ( ( pc.x - pa.x ) * ( pz - pa.z ) ) ) / area;
					var wc = ( ( ( pb.x - pa.x ) * ( pz - pa.z ) ) - ( ( px - pa.x ) * ( pb.z - pa.z ) ) ) / area;
					var wa = 1.0f - wb - wc;

					if ( wa < -1e-5f || wb < -1e-5f || wc < -1e-5f ) continue;

					var y = ( pa.y * wa ) + ( pb.y * wb ) + ( pc.y * wc );
					var at = ( j * side ) + i;

					if ( y <= top[at] ) continue;

					top[at] = y;
					paint[at] = TerrainPaint.Mix( a.Paint, b.Paint, c.Paint, wa, wb, wc );
					normal[at] = ( a.Normal * wa ) + ( b.Normal * wb ) + ( c.Normal * wc );
				}
			}
		}

		//A chunk holds the triangles whose middles are in it, so a sliver along its edge may be its neighbour's:
		//points there take a neighbour's, the way the next chunk over sees much the same ground
		var known = new bool[side * side];

		for ( var pass = 0; pass < PictureEdgeFill; pass++ )
		{
			for ( var at = 0; at < known.Length; at++ ) known[at] = top[at] != TerrainMesh.NoGround;

			var filled = false;

			for ( var j = 0; j < side; j++ )
			{
				for ( var i = 0; i < side; i++ )
				{
					var at = ( j * side ) + i;
					if ( known[at] ) continue;

					var from = i > 0 && known[at - 1] ? at - 1
						: i < side - 1 && known[at + 1] ? at + 1
						: j > 0 && known[at - side] ? at - side
						: j < side - 1 && known[at + side] ? at + side
						: -1;

					if ( from < 0 ) continue;

					top[at] = top[from];
					paint[at] = paint[from];
					normal[at] = normal[from];
					filled = true;
				}
			}

			if ( !filled ) break;
		}

		var bytes = new byte[side * side * PicturePointBytes];

		for ( var at = 0; at < side * side; at++ )
		{
			var point = bytes.AsSpan( at * PicturePointBytes, PicturePointBytes );
			var ground = top[at] != TerrainMesh.NoGround;

			System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian( point, ground ? Terrain.PackPoint( paint[at] ) : 0u );
			System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian( point[4..],
				BitConverter.HalfToUInt16Bits( ground ? (Half)Math.Clamp( top[at], -PictureLowest, PictureLowest ) : (Half)( -PictureNoGround ) ) );

			var (u, v) = Octahedral( normal[at] );
			point[6] = u;
			point[7] = v;
		}

		return bytes;
	}

	//Metres a top may be held at as a half, and the top a point with no ground says - the shader's PictureNoGround
	private const float PictureLowest = 60000.0f;
	private const float PictureNoGround = 65000.0f;

	//Points an edge's missing ground is filled in from, at most
	private const int PictureEdgeFill = 3;

	/// <summary>Fills a run of picture points with ones that say there is no ground there.</summary>
	public static void NoGroundPoint( Span<byte> points )
	{
		var none = BitConverter.HalfToUInt16Bits( (Half)( -PictureNoGround ) );

		for ( var at = 0; at + PicturePointBytes <= points.Length; at += PicturePointBytes )
		{
			System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian( points[( at + 4 )..], none );
			points[at + 6] = 128;
			points[at + 7] = 128;
		}
	}

	//A direction folded onto the square y faces, a byte each way - see OctahedralNormal in terrain_layers.slang
	private static (byte U, byte V) Octahedral( Vector3 n )
	{
		var sum = MathF.Abs( n.x ) + MathF.Abs( n.y ) + MathF.Abs( n.z );
		if ( sum < 1e-8f ) return (128, 128);

		var u = n.x / sum;
		var v = n.z / sum;

		if ( n.y < 0.0f )
		{
			var fu = ( 1.0f - MathF.Abs( v ) ) * ( u >= 0.0f ? 1.0f : -1.0f );
			var fv = ( 1.0f - MathF.Abs( u ) ) * ( v >= 0.0f ? 1.0f : -1.0f );
			u = fu;
			v = fv;
		}

		static byte Byte( float x ) => (byte)Math.Clamp( MathF.Round( ( ( x * 0.5f ) + 0.5f ) * 255.0f ), 0.0f, 255.0f );

		return (Byte( u ), Byte( v ));
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

		//Not while sculpting: a far chunk then stands its whole models until the edit ends and it is baked properly.
		if ( job.Coarse ) return;

		bake.HasFar = true;

		var error = ( job.Max.x - job.Min.x ) * FarShare;

		if ( Thinned( bake.Vertices, bake.Indices, null, error, true ) is { } ground )
			bake.FarGroundBytes = Model.Builder.WithName( $"{job.GroundName}_far" ).WithoutCollision().AddMesh( ground ).WithLockedBorder().ToBytes();

		var far = Model.Builder.WithName( $"{job.SetInName}_far" ).WithoutCollision();
		any = false;

		foreach ( var part in bake.Parts )
		{
			if ( Thinned( part.Vertices, part.Indices, part.Material, error, false ) is not { } mesh ) continue;

			far.AddMesh( mesh );
			any = true;
		}

		if ( any ) bake.FarSetInBytes = far.WithLockedBorder().ToBytes();
	}

	/// <summary>How far a far chunk's model may stray from the ground, a share of its side.</summary>
	public const float FarShare = 1.0f / 128.0f;

	//How much a whole change of paint or texture coordinate counts against the error, in metres.
	private const float FarAttributeWeight = 0.25f;

	//One material's geometry thinned to an error in metres, its border kept to meet a whole neighbour without a crack
	//Kept vertices are its own, paint and all; null where nothing is left
	private static Mesh Thinned( List<Vertex> vertices, List<int> indices, Material material, float error, bool painted )
	{
		if ( indices.Count < 3 ) return null;

		//The eight paint slots of the ground's vertex, or a set-in part's texture coordinates.
		var stride = painted ? 8 : 2;
		var positions = new Vector3[vertices.Count];
		var attributes = new float[vertices.Count * stride];

		for ( var i = 0; i < vertices.Count; i++ )
		{
			var vertex = vertices[i];
			var at = i * stride;

			positions[i] = vertex.Position;
			attributes[at] = vertex.TexCoord0.x;
			attributes[at + 1] = vertex.TexCoord0.y;

			if ( !painted ) continue;

			attributes[at + 2] = vertex.TexCoord1.x;
			attributes[at + 3] = vertex.TexCoord1.y;
			attributes[at + 4] = vertex.Color.r / 255.0f;
			attributes[at + 5] = vertex.Color.g / 255.0f;
			attributes[at + 6] = vertex.Color.b / 255.0f;
			attributes[at + 7] = vertex.Color.a / 255.0f;
		}

		var weights = new float[stride];
		Array.Fill( weights, FarAttributeWeight );

		var source = System.Runtime.InteropServices.MemoryMarshal.Cast<int, uint>( System.Runtime.InteropServices.CollectionsMarshal.AsSpan( indices ) );
		var kept = new uint[source.Length];
		var count = MeshSimplifier.Simplify( kept, source, positions, attributes, stride, weights, 0, error,
			MeshSimplifier.Options.LockBorder | MeshSimplifier.Options.ErrorAbsolute, out _ );

		if ( count < 3 ) return null;

		var newAt = new Dictionary<uint, int>();
		var thinVertices = new List<Vertex>();
		var thinIndices = new List<int>( count );

		for ( var i = 0; i < count; i++ )
		{
			if ( !newAt.TryGetValue( kept[i], out var at ) )
			{
				at = thinVertices.Count;
				newAt[kept[i]] = at;
				thinVertices.Add( vertices[(int)kept[i]] );
			}

			thinIndices.Add( at );
		}

		var mesh = material is null ? new Mesh() : new Mesh( material );
		mesh.CreateVertexBuffer( thinVertices.Count, thinVertices );
		mesh.CreateIndexBuffer( thinIndices.Count, thinIndices );

		return mesh;
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

			//Over colour and UVs, as the simplifier keeps UVs but not colour; slots 6 and 7 take the colour's spare two
			//and may wander a little across a collapse
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

	/// <summary>How much of each palette slot's layer a paint holds; slots past the palette weigh nothing.</summary>
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
