using System.Collections.Generic;

namespace Rinten;

//Copies mesh triangles into a TerrainSoup, splitting vertices per corner data, with mesh-wide normals
internal static class TerrainMeshCopy
{
	//Faces whose normals turn by more than this, sixty degrees, meet at a hard edge.
	private const float Smooth = 0.5f;

	public static void Triangles( TerrainMesh mesh, List<int> triangles, TerrainSoup into, Func<int, ushort> materialOf, List<int> sourceOf = null )
	{
		var placed = new Dictionary<(int, Vector2, Vector4, Vector3), int>( triangles.Count * 2 );
		var groundNormal = new Dictionary<int, Vector3>();
		var around = new List<int>();

		foreach ( var t in triangles )
		{
			mesh.GetTriangle( t, out var a, out var b, out var c );

			var worn = mesh.MaterialOf( t );
			var face = FaceNormal( mesh, t );

			into.Indices.Add( Copy( a, 0 ) );
			into.Indices.Add( Copy( b, 1 ) );
			into.Indices.Add( Copy( c, 2 ) );
			into.Materials.Add( materialOf( t ) );

			int Copy( int vertex, int k )
			{
				var corner = worn == 0 ? default : mesh.CornerOf( t, k );
				var normal = worn == 0 ? GroundNormal( vertex ) : WornNormal( vertex, worn, face );

				//The ground's vertex is one soup vertex whatever the corner; what was set in is one a coordinate and a shading.
				var key = ( vertex, corner.Uv, corner.Tangent, worn == 0 ? default : normal );

				if ( placed.TryGetValue( key, out var at ) ) return at;

				var v = mesh.GetVertex( vertex );

				into.Vertices.Add( new TerrainSoup.Vertex
				{
					Position = v.Position,
					Normal = normal,
					Paint = v.Paint,
					Uv = corner.Uv,
					Tangent = corner.Tangent,
				} );

				sourceOf?.Add( vertex );

				return placed[key] = into.Vertices.Count - 1;
			}
		}

		Vector3 GroundNormal( int vertex )
		{
			if ( groundNormal.TryGetValue( vertex, out var known ) ) return known;

			mesh.TrianglesAround( vertex, around );

			var sum = Vector3.Zero;

			foreach ( var t in around )
				if ( mesh.MaterialOf( t ) == 0 ) sum += FaceArea( mesh, t );

			//Ground with no ground round it is set-in geometry's; it has a normal of its own.
			var normal = sum.LengthSquared > 1e-20f ? sum.Normal : mesh.NormalOf( vertex );

			return groundNormal[vertex] = normal;
		}

		Vector3 WornNormal( int vertex, ushort material, Vector3 face )
		{
			mesh.TrianglesAround( vertex, around );

			var sum = Vector3.Zero;

			foreach ( var t in around )
			{
				if ( mesh.MaterialOf( t ) != material ) continue;

				var weighted = FaceArea( mesh, t );
				if ( weighted.LengthSquared <= 1e-20f ) continue;

				if ( Vector3.Dot( weighted.Normal, face ) >= Smooth ) sum += weighted;
			}

			var normal = sum.LengthSquared > 1e-20f ? sum.Normal : face;

			//Quantised a little, so two faces of one smooth group that arrive at the same normal by a different sum share a vertex rather than making two.
			return new Vector3( MathF.Round( normal.x * 4096.0f ) / 4096.0f, MathF.Round( normal.y * 4096.0f ) / 4096.0f,
				MathF.Round( normal.z * 4096.0f ) / 4096.0f );
		}
	}

	//A triangle's normal times twice its area, which is what an area-weighted sum wants.
	private static Vector3 FaceArea( TerrainMesh mesh, int t )
	{
		mesh.GetTriangle( t, out var a, out var b, out var c );

		var pa = mesh.PositionOf( a );

		return Vector3.Cross( mesh.PositionOf( b ) - pa, mesh.PositionOf( c ) - pa );
	}

	private static Vector3 FaceNormal( TerrainMesh mesh, int t )
	{
		var area = FaceArea( mesh, t );
		return area.LengthSquared > 1e-20f ? area.Normal : Vector3.Up;
	}

	/// <summary>A solid joined to the mesh for good: the ground near it copied out, cut against it, and put back with the solid as one surface - see Terrain.Embed(TerrainModifier), which numbers the solid's materials first.</summary>
	public static TerrainMeshEdit Embed( TerrainMesh mesh, TerrainSoup solid, BBox bounds, float margin, bool carve,
		Action<TerrainSoup> shape, out TerrainMeshUnion.Report report )
	{
		report = null;

		//The ground under it and a margin round that, so the cut never reaches the edge of what was copied out.
		var min = new Vector2( bounds.Mins.x - margin, bounds.Mins.z - margin );
		var max = new Vector2( bounds.Maxs.x + margin, bounds.Maxs.z + margin );

		var taken = new List<int>();
		mesh.TrianglesInRect( min, max, taken );

		if ( taken.Count == 0 ) return null;

		taken.Sort();

		var ground = new TerrainSoup();
		var back = new List<int>();

		Triangles( mesh, taken, ground, t => mesh.MaterialOf( t ), back );

		shape?.Invoke( ground );

		var joined = new TerrainSoup();
		report = TerrainMeshUnion.Union( ground, solid, joined, carve );

		//A vertex the triangles round the outside also name has to stay the vertex it was, or the ground tears
		//along the edge of what was taken. Those are found by looking a little wider and seeing who else names them.
		var takenSet = new HashSet<int>( taken );
		var wider = new List<int>();
		var outside = new HashSet<int>();

		mesh.TrianglesInRect( new Vector2( min.x - ( margin * 4.0f ), min.y - ( margin * 4.0f ) ),
			new Vector2( max.x + ( margin * 4.0f ), max.y + ( margin * 4.0f ) ), wider );

		foreach ( var t in wider )
		{
			if ( takenSet.Contains( t ) ) continue;

			mesh.GetTriangle( t, out var a, out var b, out var c );
			outside.Add( a ); outside.Add( b ); outside.Add( c );
		}

		//Every place once: the outside's own vertices where they are, the rest made new, with the
		//ground's paint where the ground is at that place and the normals of all that is there added up.
		var vertexAt = new Dictionary<Vector3, int>();

		for ( var i = 0; i < ground.Vertices.Count; i++ )
			if ( outside.Contains( back[i] ) ) vertexAt[ground.Vertices[i].Position] = back[i];

		var paintAt = new Dictionary<Vector3, TerrainSoup.Vertex>();
		var normalAt = new Dictionary<Vector3, Vector3>();

		for ( var t = 0; t < joined.Materials.Count; t++ )
		{
			var fromGround = t < report.FromGround.Count && report.FromGround[t];

			for ( var k = 0; k < 3; k++ )
			{
				var record = joined.Vertices[joined.Indices[( t * 3 ) + k]];

				normalAt[record.Position] = normalAt.GetValueOrDefault( record.Position ) + record.Normal;

				if ( fromGround || !paintAt.ContainsKey( record.Position ) ) paintAt[record.Position] = record;
			}
		}

		var made = new List<int>();

		mesh.BeginEdit();

		//Taken out first. The outside's vertices live on through their own triangles outside;
		//every other vertex goes with its last triangle and is made again below, one a place.
		foreach ( var t in taken ) mesh.RemoveTriangle( t );

		for ( var t = 0; t < joined.Materials.Count; t++ )
		{
			var ra = joined.Vertices[joined.Indices[t * 3]];
			var rb = joined.Vertices[joined.Indices[( t * 3 ) + 1]];
			var rc = joined.Vertices[joined.Indices[( t * 3 ) + 2]];

			var a = Place( ra.Position );
			var b = Place( rb.Position );
			var c = Place( rc.Position );

			if ( a == b || b == c || c == a ) continue;

			var material = joined.Materials[t];

			if ( material == 0 )
			{
				mesh.AddTriangle( a, b, c, 0 );
				continue;
			}
			mesh.AddTriangle( a, b, c, material,
				new TerrainMesh.Corner { Uv = ra.Uv, Tangent = ra.Tangent },
				new TerrainMesh.Corner { Uv = rb.Uv, Tangent = rb.Tangent },
				new TerrainMesh.Corner { Uv = rc.Uv, Tangent = rc.Tangent } );
		}

		//Which way each new vertex faces, from its faces - what the brushes push along.
		mesh.RecomputeNormals( made );

		return mesh.EndEdit();

		int Place( Vector3 position )
		{
			if ( vertexAt.TryGetValue( position, out var known ) && mesh.IsVertexAlive( known ) ) return known;

			var paint = paintAt[position];
			var normal = normalAt.GetValueOrDefault( position );

			var v = mesh.AddVertex( new TerrainMesh.Vertex
			{
				Position = position,
				Normal = normal.LengthSquared > 1e-20f ? normal.Normal : Vector3.Up,
				Paint = paint.Paint,
			} );

			made.Add( v );
			return vertexAt[position] = v;
		}
	}
}
