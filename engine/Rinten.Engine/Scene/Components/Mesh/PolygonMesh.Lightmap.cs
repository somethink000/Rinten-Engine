using HalfEdgeMesh;

namespace Rinten;

//A lightmap sheet for the mesh, as an import's (ModelBuilder.GenerateLightmapUv): touching faces in one plane share an
//island, so a wall cut round a door is one lightmap with no seam; each laid in its plane and shelf-packed
public sealed partial class PolygonMesh
{
	//Each face's vertices as TriangulateFace laid them: its submesh, where they start, and the face
	private readonly List<(Submesh Submesh, int First, FaceHandle Face)> _lightmapFaces = new();

	private void LayLightmap()
	{
		var count = _lightmapFaces.Count;
		if ( count == 0 ) return;

		var normals = new Vector3[count];
		var planes = new float[count];

		for ( var f = 0; f < count; ++f )
		{
			var (submesh, first, face) = _lightmapFaces[f];
			normals[f] = _faceNormalCache.TryGetValue( face, out var n ) ? n : Vector3.Up;
			planes[f] = Next( f ) > first ? Vector3.Dot( submesh.Vertices[first].Position, normals[f] ) : 0.0f;
		}

		var chartOf = Charts( count, normals, planes );
		var charts = chartOf.Max() + 1;

		var low = new Vector2[charts];
		var size = new Vector2[charts];
		var right = new Vector3[charts];
		var up = new Vector3[charts];
		var least = new Vector2[charts];
		var most = new Vector2[charts];
		var area = 0.0f;

		for ( var c = 0; c < charts; ++c )
		{
			least[c] = new Vector2( float.MaxValue, float.MaxValue );
			most[c] = new Vector2( float.MinValue, float.MinValue );
		}

		for ( var f = 0; f < count; ++f )
		{
			var c = chartOf[f];

			//The chart's own axes, from its first face: an axis it does not lie along, the one it leans on least
			if ( right[c] == Vector3.Zero )
			{
				var normal = normals[f];
				var axis = MathF.Abs( normal.y ) < 0.9f ? new Vector3( 0.0f, 1.0f, 0.0f ) : new Vector3( 1.0f, 0.0f, 0.0f );
				right[c] = Vector3.Cross( axis, normal ).Normal;
				up[c] = Vector3.Cross( normal, right[c] ).Normal;
			}

			var (submesh, first, _) = _lightmapFaces[f];

			for ( var v = first; v < Next( f ); ++v )
			{
				var at = submesh.Vertices[v].Position;
				var flat = new Vector2( Vector3.Dot( at, right[c] ), Vector3.Dot( at, up[c] ) );
				least[c] = Vector2.Min( least[c], flat );
				most[c] = Vector2.Max( most[c], flat );
			}
		}

		for ( var c = 0; c < charts; ++c )
		{
			if ( least[c].x > most[c].x ) least[c] = most[c] = Vector2.Zero;

			low[c] = least[c];
			size[c] = most[c] - least[c];
			area += size[c].x * size[c].y;
		}

		//Tallest first, in shelves, in a square grown until all fit; a 48th of the side apart where islands are few, so
		//a square the bake gives 96 texels has two between charts for its filters - less as they multiply, as on import
		var order = Enumerable.Range( 0, charts ).OrderByDescending( c => size[c].y ).ToArray();
		var widest = size.Max( s => MathF.Max( s.x, s.y ) );
		var side = MathF.Max( MathF.Sqrt( area ) * 1.4f, widest * 1.1f );
		var placed = new Vector2[charts];

		for ( var attempt = 0; attempt < 24; ++attempt, side *= 1.2f )
		{
			var gap = side / MathF.Max( 48.0f, 6.0f * MathF.Sqrt( charts ) );
			if ( Shelve( order, size, side, gap, placed ) ) break;
		}

		for ( var f = 0; f < count; ++f )
		{
			var c = chartOf[f];
			var (submesh, first, _) = _lightmapFaces[f];
			var end = Next( f );

			for ( var v = first; v < end; ++v )
			{
				var vertex = submesh.Vertices[v];
				var flat = new Vector2( Vector3.Dot( vertex.Position, right[c] ), Vector3.Dot( vertex.Position, up[c] ) );
				vertex.Lightmap = ( flat - low[c] + placed[c] ) / side;
				submesh.Vertices[v] = vertex;
			}
		}
	}

	//Each face's chart, numbered from nought: faces in one plane that share a corner, and what those share corners with
	private int[] Charts( int count, Vector3[] normals, float[] planes )
	{
		var parent = Enumerable.Range( 0, count ).ToArray();

		int Root( int f )
		{
			while ( parent[f] != f ) f = parent[f] = parent[parent[f]];
			return f;
		}

		//Faces by the corners they have, a tenth of a millimetre apart counted the same
		var byCorner = new Dictionary<(int, int, int), List<int>>();

		for ( var f = 0; f < count; ++f )
		{
			var (submesh, first, _) = _lightmapFaces[f];

			for ( var v = first; v < Next( f ); ++v )
			{
				var at = submesh.Vertices[v].Position;
				var key = ( (int)MathF.Round( at.x * 10000.0f ), (int)MathF.Round( at.y * 10000.0f ), (int)MathF.Round( at.z * 10000.0f ) );

				if ( !byCorner.TryGetValue( key, out var faces ) ) byCorner[key] = faces = new List<int>();
				if ( faces.Count == 0 || faces[^1] != f ) faces.Add( f );
			}
		}

		foreach ( var faces in byCorner.Values )
		{
			for ( var i = 1; i < faces.Count; ++i )
			{
				for ( var j = 0; j < i; ++j )
				{
					var a = faces[i];
					var b = faces[j];

					if ( Vector3.Dot( normals[a], normals[b] ) < 0.9995f || MathF.Abs( planes[a] - planes[b] ) > 0.001f ) continue;

					var ra = Root( a );
					var rb = Root( b );
					if ( ra != rb ) parent[ra] = rb;
				}
			}
		}

		var number = new Dictionary<int, int>();
		var chartOf = new int[count];

		for ( var f = 0; f < count; ++f )
		{
			var root = Root( f );
			if ( !number.TryGetValue( root, out var c ) ) number[root] = c = number.Count;
			chartOf[f] = c;
		}

		return chartOf;
	}

	//Where a face's vertices end: the next face's start in the same submesh, else the submesh's end
	private int Next( int f )
	{
		var submesh = _lightmapFaces[f].Submesh;

		for ( var g = f + 1; g < _lightmapFaces.Count; ++g )
			if ( _lightmapFaces[g].Submesh == submesh ) return _lightmapFaces[g].First;

		return submesh.Vertices.Count;
	}

	private static bool Shelve( int[] order, Vector2[] size, float side, float gap, Vector2[] placed )
	{
		var x = 0.0f;
		var y = 0.0f;
		var row = 0.0f;

		foreach ( var f in order )
		{
			var width = size[f].x + ( gap * 2.0f );
			var height = size[f].y + ( gap * 2.0f );

			if ( width > side || height > side ) return false;

			if ( x + width > side )
			{
				x = 0.0f;
				y += row;
				row = 0.0f;
			}

			if ( y + height > side ) return false;

			placed[f] = new Vector2( x + gap, y + gap );
			x += width;
			row = MathF.Max( row, height );
		}

		return true;
	}
}
