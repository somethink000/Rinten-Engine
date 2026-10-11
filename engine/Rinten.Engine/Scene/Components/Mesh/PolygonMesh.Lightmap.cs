using HalfEdgeMesh;

namespace Rinten;

//A lightmap sheet for the mesh, as an import is given one (ModelBuilder.GenerateLightmapUv): each face is flat, so each is
//its own island, laid in its own plane and shelf-packed with room between them - the cells of RenderCells share it
public sealed partial class PolygonMesh
{
	//Each face's vertices as TriangulateFace laid them: its submesh, where they start, and the face
	private readonly List<(Submesh Submesh, int First, FaceHandle Face)> _lightmapFaces = new();

	private void LayLightmap()
	{
		var count = _lightmapFaces.Count;
		if ( count == 0 ) return;

		var low = new Vector2[count];
		var size = new Vector2[count];
		var right = new Vector3[count];
		var up = new Vector3[count];
		var area = 0.0f;

		for ( var f = 0; f < count; ++f )
		{
			var (submesh, first, face) = _lightmapFaces[f];
			var end = Next( f );
			var normal = _faceNormalCache.TryGetValue( face, out var n ) ? n : Vector3.Up;

			//Any axis the face does not lie along: the one it leans on least
			var axis = MathF.Abs( normal.y ) < 0.9f ? new Vector3( 0.0f, 1.0f, 0.0f ) : new Vector3( 1.0f, 0.0f, 0.0f );
			right[f] = Vector3.Cross( axis, normal ).Normal;
			up[f] = Vector3.Cross( normal, right[f] ).Normal;

			var least = new Vector2( float.MaxValue, float.MaxValue );
			var most = new Vector2( float.MinValue, float.MinValue );

			for ( var v = first; v < end; ++v )
			{
				var at = submesh.Vertices[v].Position;
				var flat = new Vector2( Vector3.Dot( at, right[f] ), Vector3.Dot( at, up[f] ) );
				least = Vector2.Min( least, flat );
				most = Vector2.Max( most, flat );
			}

			if ( end <= first ) least = most = Vector2.Zero;

			low[f] = least;
			size[f] = most - least;
			area += size[f].x * size[f].y;
		}

		//Tallest first, in shelves, in a square grown until all fit; a 48th of the side apart where islands are few, so
		//a square the bake gives 96 texels has two between faces for its filters - less as they multiply, as on import
		var order = Enumerable.Range( 0, count ).OrderByDescending( f => size[f].y ).ToArray();
		var widest = size.Max( s => MathF.Max( s.x, s.y ) );
		var side = MathF.Max( MathF.Sqrt( area ) * 1.4f, widest * 1.1f );
		var placed = new Vector2[count];

		for ( var attempt = 0; attempt < 24; ++attempt, side *= 1.2f )
		{
			var gap = side / MathF.Max( 48.0f, 6.0f * MathF.Sqrt( count ) );
			if ( Shelve( order, size, side, gap, placed ) ) break;
		}

		for ( var f = 0; f < count; ++f )
		{
			var (submesh, first, _) = _lightmapFaces[f];
			var end = Next( f );

			for ( var v = first; v < end; ++v )
			{
				var vertex = submesh.Vertices[v];
				var flat = new Vector2( Vector3.Dot( vertex.Position, right[f] ), Vector3.Dot( vertex.Position, up[f] ) );
				vertex.Lightmap = ( flat - low[f] + placed[f] ) / side;
				submesh.Vertices[v] = vertex;
			}
		}
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
