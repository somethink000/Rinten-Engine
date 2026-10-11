using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Rinten.CoreAssets;

//Generates a lightmap UV set with unique, non-overlapping texels; correct but not tightly packed
internal sealed unsafe partial class ModelBuilder
{
	//Which way a triangle faces, and how big it is in the world.
	private struct Face
	{
		public uint A, B, C;
		public Vector3 Normal;
		public float Area;
		public int Island;
	}

	//Everything a run of connected, similarly-facing triangles needs.
	private sealed class Island
	{
		public readonly List<uint> Faces = new();
		public Vector3 Normal = new( 0.0f, 0.0f, 1.0f );

		/// <summary>Its own two axes on the plane it is flattened onto.</summary>
		public Vector3 Right = new( 1.0f, 0.0f, 0.0f );
		public Vector3 Up = new( 0.0f, 1.0f, 0.0f );

		/// <summary>Where it sits once flattened, in metres, and where it was packed to.</summary>
		public float LowX, LowY;
		public float SizeX, SizeY;
		public float PlacedX, PlacedY;
	}

	//Union-find over triangles, so connected ones end up in one island.
	private sealed class Groups
	{
		private uint[] parent = Array.Empty<uint>();

		public void Reset( int count )
		{
			if ( parent.Length < count ) parent = new uint[count];

			for ( var i = 0; i < count; ++i ) parent[i] = (uint)i;
		}

		public uint Find( uint of )
		{
			while ( parent[of] != of )
			{
				parent[of] = parent[parent[of]];
				of = parent[of];
			}

			return of;
		}

		public void Join( uint a, uint b )
		{
			a = Find( a );
			b = Find( b );

			if ( a != b ) parent[b] = a;
		}
	}

	//Triangles past which an import keeps no sheet of its own - see GenerateLightmapUv
	private const int MostLightmapTriangles = 65536;

	//Normal dot threshold for joining triangles into one island (about 48 degrees)
	private const float SameWay = 0.66f;

	//The same direction at unit length, or +Z where there was none.
	private static Vector3 Normalised( Vector3 v )
	{
		var length = MathF.Sqrt( ( v.x * v.x ) + ( v.y * v.y ) + ( v.z * v.z ) );

		if ( length < 1e-8f ) return new Vector3( 0.0f, 0.0f, 1.0f );

		return new Vector3( v.x / length, v.y / length, v.z / length );
	}

	private static Vector3 CrossOf( Vector3 a, Vector3 b )
		=> new( ( a.y * b.z ) - ( a.z * b.y ),
			( a.z * b.x ) - ( a.x * b.z ),
			( a.x * b.y ) - ( a.y * b.x ) );

	private static float DotOf( Vector3 a, Vector3 b ) => ( a.x * b.x ) + ( a.y * b.y ) + ( a.z * b.z );

	//A pair of vertices, in the order that makes an edge the same edge from either triangle.
	private static ulong EdgeKey( uint a, uint b )
	{
		var low = a < b ? a : b;
		var high = a < b ? b : a;

		return ( (ulong)low << 32 ) | high;
	}

	//Edge key hash using MurmurHash3's finaliser, since the default hash clusters sequential pairs
	private sealed class EdgeHash : IEqualityComparer<ulong>
	{
		public static readonly EdgeHash Instance = new();

		public bool Equals( ulong a, ulong b ) => a == b;

		public int GetHashCode( ulong key )
		{
			key ^= key >> 33;
			key *= 0xff51afd7ed558ccdUL;
			key ^= key >> 33;
			key *= 0xc4ceb9fe1a85ec53UL;
			key ^= key >> 33;

			return (int)key;
		}
	}

	//The two axes of the plane a normal faces, chosen so that neither is nearly parallel to it.
	private static void BasisFor( Vector3 normal, out Vector3 right, out Vector3 up )
	{
		var away = MathF.Abs( normal.z ) < 0.9f
			? new Vector3( 0.0f, 0.0f, 1.0f )
			: new Vector3( 1.0f, 0.0f, 0.0f );

		right = Normalised( CrossOf( away, normal ) );
		up = Normalised( CrossOf( normal, right ) );
	}

	//Shelf packing, tallest first, into a square of the given side - the same shape as CoreLightmap.Pack one level up,
	//as it is the packing that can be written in twenty lines and read in five
	private static bool Fits( List<Island> islands, List<uint> order, float side, float padding )
	{
		var x = 0.0f;
		var y = 0.0f;
		var row = 0.0f;

		foreach ( var at in order )
		{
			var island = islands[(int)at];

			var width = island.SizeX + ( padding * 2.0f );
			var height = island.SizeY + ( padding * 2.0f );

			if ( width > side || height > side ) return false;

			if ( x + width > side )
			{
				x = 0.0f;
				y += row;
				row = 0.0f;
			}

			if ( y + height > side ) return false;

			island.PlacedX = x + padding;
			island.PlacedY = y + padding;

			x += width;
			row = MathF.Max( row, height );
		}

		return true;
	}

	//Whether any vertex has a second UV set
	private bool HasSecondSet()
	{
		foreach ( var vertex in vertices )
		{
			if ( vertex.TexCoord1.x != 0.0f || vertex.TexCoord1.y != 0.0f ) return true;
		}

		return false;
	}

	//Whether the second UV set is our old per-triangle unwrap, detected by how much welding it blocks
	private bool SplitByItsOwnUnwrap()
	{
		var count = vertices.Count;
		if ( count == 0 ) return false;

		//Hashes rather than the keys: this is a ratio against a factor of two, and a collision cannot move it
		var whole = new HashSet<int>( count );
		var without = new HashSet<int>( count );

		foreach ( var v in vertices )
		{
			var colour = ( (uint)v.Color.r << 24 ) | ( (uint)v.Color.g << 16 ) | ( (uint)v.Color.b << 8 ) | v.Color.a;

			var shared = System.HashCode.Combine( v.Position.x, v.Position.y, v.Position.z,
				v.Normal.x, v.Normal.y, v.Normal.z, v.TexCoord0.x, v.TexCoord0.y );

			without.Add( System.HashCode.Combine( shared, colour ) );
			whole.Add( System.HashCode.Combine( shared, colour, v.TexCoord1.x, v.TexCoord1.y ) );
		}

		return without.Count * 2 <= whole.Count;
	}

	public int GenerateLightmapUv( int overwrite )
	{
		if ( vertices.Count == 0 || indices.Count == 0 ) return 0;

		//A dense mesh - foliage, terrain - is lit from the volume, as UE advises: its split corners would cost every frame
		if ( overwrite == 0 && indices.Count / 3 > MostLightmapTriangles ) return 0;

		//Skip skinned or morphed models: they are not lightmapped and morphs index vertices by number
		if ( bones.Count > 0 || morphs.Count > 0 ) return 0;

		//Keep an artist-made lightmap set unless it is our own old per-triangle unwrap
		if ( overwrite == 0 && HasSecondSet() && !SplitByItsOwnUnwrap() ) return 0;

		//Weld corners first, or every triangle becomes its own island and can never be welded again
		WeldVertices();

		var madeVertices = new List<Vertex>( vertices.Count );
		var madeSkin = new List<byte>();

		var hasSkin = skin.Count == vertices.Count * SkinStride;

		//The ranges the meshes end up with, applied only if anything came of this: a run that produced nothing must leave its mesh where it was.
		var ranges = new (uint FirstVertex, uint VertexCount)[meshes.Count];

		for ( var m = 0; m < meshes.Count; m++ ) ranges[m] = ( meshes[m].FirstVertex, meshes[m].VertexCount );

		var groups = new Groups();
		var edges = new Dictionary<ulong, uint>( EdgeHash.Instance );
		var byRoot = new Dictionary<uint, uint>();
		var order = new List<uint>();
		var made = new Dictionary<ulong, uint>( EdgeHash.Instance );
		var places = new Dictionary<(float, float, float), uint>();
		var placeOf = Array.Empty<uint>();

		var flatIndices = CollectionsMarshal.AsSpan( indices );

		//One sheet for the whole model: an instance has one place in the atlas (BakedLighting), so meshes that each filled
		//a square of their own would be baked over each other. Every mesh's islands are found first, then packed together.
		var facesOf = new List<Face>[meshes.Count];
		var islands = new List<Island>();

		Span<uint> corners = stackalloc uint[6];

		for ( var m = 0; m < meshes.Count; m++ )
		{
			var mesh = meshes[m];

			if ( mesh.IndexCount < 3 || mesh.VertexCount == 0 ) continue;

			var firstIndex = (int)mesh.FirstIndex;
			var firstVertex = (int)mesh.FirstVertex;

			if ( firstIndex + mesh.IndexCount > indices.Count ) continue;
			if ( firstVertex + mesh.VertexCount > vertices.Count ) continue;

			//The triangles, one entry each in the mesh's order, so a face's place in the list is its place in the indices;
			//one that cannot be unwrapped stays with no island and keeps its indices
			var faces = new List<Face>( (int)( mesh.IndexCount / 3 ) );
			facesOf[m] = faces;

			for ( var at = 0u; at + 2 < mesh.IndexCount; at += 3 )
			{
				var face = default( Face );
				face.Island = -1;
				face.A = flatIndices[firstIndex + (int)at + 0];
				face.B = flatIndices[firstIndex + (int)at + 1];
				face.C = flatIndices[firstIndex + (int)at + 2];

				if ( face.A < mesh.VertexCount && face.B < mesh.VertexCount && face.C < mesh.VertexCount )
				{
					var a = vertices[firstVertex + (int)face.A].Position;
					var bb = vertices[firstVertex + (int)face.B].Position;
					var c = vertices[firstVertex + (int)face.C].Position;

					var cross = CrossOf( bb - a, c - a );

					face.Area = MathF.Sqrt( DotOf( cross, cross ) ) * 0.5f;
					face.Normal = Normalised( cross );
					face.Island = 0;
				}

				faces.Add( face );
			}

			//-- islands: join faces by edge position, not vertex number, so flat-shaded faces connect --
			places.Clear();

			if ( placeOf.Length < mesh.VertexCount ) placeOf = new uint[mesh.VertexCount];

			for ( var v = 0; v < mesh.VertexCount; ++v )
			{
				var at = vertices[firstVertex + v].Position;
				var key = ( at.x, at.y, at.z );

				if ( !places.TryGetValue( key, out var place ) )
				{
					place = (uint)places.Count;
					places[key] = place;
				}

				placeOf[v] = place;
			}

			groups.Reset( faces.Count );

			edges.Clear();
			edges.EnsureCapacity( faces.Count * 3 );

			for ( var at = 0u; at < faces.Count; ++at )
			{
				var face = faces[(int)at];

				if ( face.Island < 0 ) continue;

				corners[0] = face.A;
				corners[1] = face.B;
				corners[2] = face.B;
				corners[3] = face.C;
				corners[4] = face.C;
				corners[5] = face.A;

				for ( var e = 0; e < 3; e++ )
				{
					var key = EdgeKey( placeOf[corners[e * 2]], placeOf[corners[( e * 2 ) + 1]] );

					if ( !edges.TryGetValue( key, out var found ) )
					{
						edges[key] = at;
						continue;
					}

					if ( DotOf( faces[(int)found].Normal, face.Normal ) >= SameWay ) groups.Join( found, at );
				}
			}

			byRoot.Clear();

			var firstIsland = islands.Count;

			for ( var at = 0u; at < faces.Count; ++at )
			{
				var face = faces[(int)at];

				if ( face.Island < 0 ) continue;

				var root = groups.Find( at );

				if ( !byRoot.TryGetValue( root, out var which ) )
				{
					which = (uint)islands.Count;
					byRoot[root] = which;
					islands.Add( new Island() );
				}

				face.Island = (int)which;
				faces[(int)at] = face;

				islands[(int)which].Faces.Add( at );
			}

			//------------------------------------------------ flatten each island
			for ( var which = firstIsland; which < islands.Count; ++which )
			{
				var island = islands[which];
				var normal = new Vector3( 0.0f, 0.0f, 0.0f );

				//Weighted by area, so a hundred slivers do not outvote the face they are chamfering.
				foreach ( var at in island.Faces )
				{
					normal.x += faces[(int)at].Normal.x * faces[(int)at].Area;
					normal.y += faces[(int)at].Normal.y * faces[(int)at].Area;
					normal.z += faces[(int)at].Normal.z * faces[(int)at].Area;
				}

				island.Normal = Normalised( normal );
				BasisFor( island.Normal, out var right, out var up );

				island.Right = right;
				island.Up = up;

				float lowX = 1e30f, lowY = 1e30f, highX = -1e30f, highY = -1e30f;

				foreach ( var at in island.Faces )
				{
					var face = faces[(int)at];
					corners[0] = face.A;
					corners[1] = face.B;
					corners[2] = face.C;

					for ( var k = 0; k < 3; k++ )
					{
						var position = vertices[firstVertex + (int)corners[k]].Position;

						var x = DotOf( position, island.Right );
						var y = DotOf( position, island.Up );

						lowX = MathF.Min( lowX, x );
						lowY = MathF.Min( lowY, y );
						highX = MathF.Max( highX, x );
						highY = MathF.Max( highY, y );
					}
				}

				island.LowX = lowX;
				island.LowY = lowY;
				island.SizeX = MathF.Max( highX - lowX, 1e-4f );
				island.SizeY = MathF.Max( highY - lowY, 1e-4f );
			}
		}

		if ( islands.Count == 0 ) return 0;

		//------------------------------------------------ pack them, all together
		order.Clear();
		for ( var at = 0u; at < islands.Count; ++at ) order.Add( at );

		order.Sort( ( left, right ) => islands[(int)right].SizeY.CompareTo( islands[(int)left].SizeY ) );

		var area = 0.0f;
		var widest = 0.0f;

		foreach ( var island in islands )
		{
			area += island.SizeX * island.SizeY;
			widest = MathF.Max( widest, MathF.Max( island.SizeX, island.SizeY ) );
		}

		//Padding scaled to island size, so packing succeeds - and a 48th of the side where islands are few, so a square
		//the bake gives 96 texels has two between them for its filters; less as they multiply
		var least = MathF.Sqrt( area / islands.Count ) * 0.02f;
		var padding = least;

		var padded = 0.0f;

		foreach ( var island in islands )
			padded += ( island.SizeX + ( padding * 2.0f ) ) * ( island.SizeY + ( padding * 2.0f ) );

		var side = MathF.Max( MathF.Sqrt( padded ) * 1.3f, ( widest + ( padding * 2.0f ) ) * 1.05f );

		//Grown until they fit rather than solved for: the shelf packer wastes an unpredictable amount, and ten cheap tries
		//beat one expensive one. With the gap fixed the area to fit is too, so growing the side gets there.
		var packed = false;

		for ( var attempt = 0; attempt < 16 && !packed; ++attempt )
		{
			padding = MathF.Max( least, side / MathF.Max( 48.0f, 6.0f * MathF.Sqrt( islands.Count ) ) );
			packed = Fits( islands, order, side, padding );

			if ( !packed ) side *= 1.25f;
		}

		//No sheet: the model keeps the coordinates it had, which is none. Writing what a failed pass left behind
		//is a sheet with every island on the same texel, and nothing downstream can tell that from a good one.
		if ( !packed )
		{
			log.Warning( $"lightmap sheet: {islands.Count} islands would not pack in sixteen tries - the model gets no sheet" );
			return 0;
		}

		//-- write out: a vertex shared across a seam is split, inside an island it stays one --
		for ( var m = 0; m < meshes.Count; m++ )
		{
			var faces = facesOf[m];

			var mesh = meshes[m];
			var firstIndex = (int)mesh.FirstIndex;
			var firstVertex = (int)mesh.FirstVertex;

			if ( faces is null )
			{
				//Nothing of this mesh was unwrapped, but the vertices are about to be replaced by the ones made
				//here - so it takes its own across as they are, and its indices go on meaning what they did.
				var kept = (int)Math.Min( mesh.VertexCount, (uint)Math.Max( vertices.Count - firstVertex, 0 ) );

				ranges[m] = ( (uint)madeVertices.Count, (uint)kept );

				for ( var v = 0; v < kept; ++v )
				{
					madeVertices.Add( vertices[firstVertex + v] );

					if ( hasSkin )
					{
						madeSkin.AddRange( CollectionsMarshal.AsSpan( skin )
							.Slice( ( firstVertex + v ) * SkinStride, SkinStride ) );
					}
				}

				continue;
			}

			made.Clear();
			made.EnsureCapacity( (int)mesh.VertexCount * 2 );

			var newFirstVertex = (uint)madeVertices.Count;

			for ( var at = 0; at < faces.Count; ++at )
			{
				var face = faces[at];
				corners[0] = face.A;
				corners[1] = face.B;
				corners[2] = face.C;

				for ( var corner = 0; corner < 3; ++corner )
				{
					var local = corners[corner];

					//A triangle naming a vertex the mesh lacks keeps the first one: nothing can be read for it either way
					if ( local >= mesh.VertexCount ) local = 0;

					//A triangle with no island shares the unmoved vertices: its island half of the key is one no island has
					var key = ( (ulong)(uint)face.Island << 32 ) | local;

					if ( !made.TryGetValue( key, out var found ) )
					{
						var vertex = vertices[firstVertex + (int)local];

						if ( face.Island >= 0 )
						{
							var island = islands[face.Island];
							var position = vertex.Position;

							var x = DotOf( position, island.Right ) - island.LowX;
							var y = DotOf( position, island.Up ) - island.LowY;

							vertex.TexCoord1 = new Vector4( ( island.PlacedX + x ) / side,
								( island.PlacedY + y ) / side, 0.0f, 0.0f );
						}

						found = (uint)( madeVertices.Count - newFirstVertex );

						madeVertices.Add( vertex );

						if ( hasSkin )
						{
							madeSkin.AddRange( CollectionsMarshal.AsSpan( skin )
								.Slice( ( firstVertex + (int)local ) * SkinStride, SkinStride ) );
						}

						made[key] = found;
					}

					flatIndices[firstIndex + ( at * 3 ) + corner] = found;
				}
			}

			ranges[m] = ( newFirstVertex, (uint)madeVertices.Count - newFirstVertex );
		}

		if ( madeVertices.Count == 0 ) return 0;

		vertices.Clear();
		vertices.AddRange( madeVertices );

		for ( var m = 0; m < meshes.Count; m++ )
		{
			meshes[m].FirstVertex = ranges[m].FirstVertex;
			meshes[m].VertexCount = ranges[m].VertexCount;
		}

		skin.Clear();

		if ( hasSkin ) skin.AddRange( madeSkin );

		return 1;
	}
}
