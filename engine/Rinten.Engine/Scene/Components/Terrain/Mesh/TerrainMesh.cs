using System;
using System.Collections.Generic;
using System.IO;

namespace Rinten;

/// <summary>The ground as it is: one triangle mesh for a whole terrain, which is what is stored, what the brushes move and what the render chunks are baked from.</summary>
public sealed partial class TerrainMesh
{
	/// <summary>Everything a vertex of the ground carries.</summary>
	public struct Vertex
	{
		public Vector3 Position, Normal;

		/// <summary>Which of the terrain's layers are painted here, and how much of each.</summary>
		public TerrainPaint Paint;
	}

	/// <summary>Per-corner UV and tangent, so texture seams don't split vertices and crack under brushes</summary>
	public struct Corner
	{
		public Vector2 Uv;
		public Vector4 Tangent;
	}

	//storage

	private Vector3[] vPosition = [];
	private Vector3[] vNormal = [];
	private TerrainPaint[] vPaint = [];
	private bool[] vAlive = [];

	//The first corner at each vertex, or less than nought.
	private int[] vCorner = [];

	//The grid: which square a vertex is in, and its neighbours in that square's list.
	private int[] vCell = [];
	private int[] vCellNext = [];
	private int[] vCellPrev = [];

	private int[] tVertex = [];
	private ushort[] tMaterial = [];

	//A triangle's corners' own numbers - see Corner. Three to a triangle, beside tVertex .
	private Vector2[] cUv = [];
	private Vector4[] cTangent = [];
	private bool[] tAlive = [];

	//The next corner at the same vertex, per corner.
	private int[] cNext = [];

	private int vertexSlots, triangleSlots, vertexCount, triangleCount;

	//Slots that died. An entry may be stale - the slot brought back by an undo since - so whoever pops one checks it is still dead.
	private readonly Stack<int> freeVertices = new();
	private readonly Stack<int> freeTriangles = new();

	private Vector2 gridMin;
	private float gridCell;
	private int gridWide, gridDeep;
	private int[] gridFirst;

	//No edge in the mesh is longer than this. Grows as things move; measured again when an edit closes.
	private float longestEdge;
	private bool longestEdgeGrew;

	//No vertex stands further outside the grid's rectangle than this.
	private float overhang;

	private BBox bounds;
	private bool anyBounds;

	/// <summary>A mesh with nothing in it, over a rectangle of the ground plane. What stands outside the rectangle is still found, only slower.</summary>
	public TerrainMesh( Vector2 min, Vector2 max, float cell = 4.0f )
	{
		var wide = MathF.Max( max.x - min.x, 1.0f );
		var deep = MathF.Max( max.y - min.y, 1.0f );

		cell = MathF.Max( cell, 0.25f );
		cell = MathF.Max( cell, MathF.Max( wide, deep ) / 2048.0f );

		gridMin = min;
		gridCell = cell;
		gridWide = Math.Max( 1, (int)MathF.Ceiling( wide / cell ) );
		gridDeep = Math.Max( 1, (int)MathF.Ceiling( deep / cell ) );
		gridFirst = new int[gridWide * gridDeep];

		Array.Fill( gridFirst, -1 );
	}

	/// <summary>How many vertex ids there are, the dead among them.</summary>
	public int VertexSlots => vertexSlots;
	public int TriangleSlots => triangleSlots;

	/// <summary>How many are alive.</summary>
	public int VertexCount => vertexCount;
	public int TriangleCount => triangleCount;

	public bool IsVertexAlive( int v ) => (uint)v < (uint)vertexSlots && vAlive[v];
	public bool IsTriangleAlive( int t ) => (uint)t < (uint)triangleSlots && tAlive[t];

	public Vector3 PositionOf( int v ) => vPosition[v];
	public Vector3 NormalOf( int v ) => vNormal[v];
	public ushort MaterialOf( int t ) => tMaterial[t];

	public void GetTriangle( int t, out int a, out int b, out int c )
	{
		a = tVertex[t * 3];
		b = tVertex[( t * 3 ) + 1];
		c = tVertex[( t * 3 ) + 2];
	}

	public Vertex GetVertex( int v ) => new()
	{
		Position = vPosition[v],
		Normal = vNormal[v],
		Paint = vPaint[v],
	};

	/// <summary>One corner of a triangle, k from nought to two in the order <see cref="GetTriangle"/> gives them.</summary>
	public Corner CornerOf( int t, int k ) => new() { Uv = cUv[( t * 3 ) + k], Tangent = cTangent[( t * 3 ) + k] };

	/// <summary>Everything there is, as a box. Only ever grows between loads.</summary>
	public BBox Bounds => anyBounds ? bounds : new BBox( Vector3.Zero, Vector3.Zero );

	/// <summary>The longest edge there is, or a little over - how far from a triangle's owner the triangle can reach.</summary>
	public float LongestEdge => longestEdge;

	/// <summary>Told that the mesh changed and where, as a box around everything that moved - where it was and where it is. Once a brush call, and once for an edit closed, undone or done again.</summary>
	public event Action<BBox> Changed;

	//the primitive changes - everything else is made of these, and these are what an edit records

	private void Reserve( int vertices, int triangles )
	{
		if ( vertices > vPosition.Length )
		{
			var room = Math.Max( vertices, Math.Max( 256, vPosition.Length * 2 ) );

			Array.Resize( ref vPosition, room );
			Array.Resize( ref vNormal, room );
			Array.Resize( ref vPaint, room );
			Array.Resize( ref vAlive, room );
			Array.Resize( ref vCorner, room );
			Array.Resize( ref vCell, room );
			Array.Resize( ref vCellNext, room );
			Array.Resize( ref vCellPrev, room );
		}

		if ( triangles > tAlive.Length )
		{
			var room = Math.Max( triangles, Math.Max( 256, tAlive.Length * 2 ) );

			Array.Resize( ref tVertex, room * 3 );
			Array.Resize( ref cNext, room * 3 );
			Array.Resize( ref cUv, room * 3 );
			Array.Resize( ref cTangent, room * 3 );
			Array.Resize( ref tMaterial, room );
			Array.Resize( ref tAlive, room );
		}
	}

	//Which column or row of the grid a coordinate falls in, before it is held to the grid -
	//and held to something an integer can say first, since a rectangle may run to infinity.
	private int Along( float coordinate, float origin )
		=> (int)MathF.Floor( Math.Clamp( ( coordinate - origin ) / gridCell, -1e6f, 1e6f ) );

	private int CellOf( Vector3 position )
	{
		var x = Along( position.x, gridMin.x );
		var z = Along( position.z, gridMin.y );

		return ( Math.Clamp( z, 0, gridDeep - 1 ) * gridWide ) + Math.Clamp( x, 0, gridWide - 1 );
	}

	private void GridInsert( int v )
	{
		var position = vPosition[v];
		var cell = CellOf( position );
		var first = gridFirst[cell];

		vCell[v] = cell;
		vCellPrev[v] = -1;
		vCellNext[v] = first;

		if ( first >= 0 ) vCellPrev[first] = v;
		gridFirst[cell] = v;

		//How far outside the grid it stands, which a ray has to allow for.
		var outside = MathF.Max(
			MathF.Max( gridMin.x - position.x, position.x - ( gridMin.x + ( gridWide * gridCell ) ) ),
			MathF.Max( gridMin.y - position.z, position.z - ( gridMin.y + ( gridDeep * gridCell ) ) ) );

		if ( outside > overhang ) overhang = outside;
	}

	private void GridRemove( int v )
	{
		var next = vCellNext[v];
		var previous = vCellPrev[v];

		if ( previous >= 0 ) vCellNext[previous] = next;
		else gridFirst[vCell[v]] = next;

		if ( next >= 0 ) vCellPrev[next] = previous;
	}

	private void Grow( Vector3 point )
	{
		bounds = anyBounds ? bounds.AddPoint( point ) : new BBox( point, point );
		anyBounds = true;
	}

	//A vertex into a slot that is known to be dead.
	private void RawAddVertexAt( int v, in Vertex value )
	{
		Reserve( v + 1, 0 );

		if ( v >= vertexSlots ) vertexSlots = v + 1;

		vPosition[v] = value.Position;
		vNormal[v] = value.Normal;
		vPaint[v] = value.Paint;
		vAlive[v] = true;
		vCorner[v] = -1;
		vertexCount++;

		GridInsert( v );
		Grow( value.Position );
		Touch( value.Position );
	}

	private void RawRemoveVertex( int v )
	{
		Touch( vPosition[v] );
		GridRemove( v );

		vAlive[v] = false;
		vertexCount--;
		freeVertices.Push( v );
	}

	private void RawSetVertex( int v, in Vertex value )
	{
		var was = vPosition[v];

		vNormal[v] = value.Normal;
		vPaint[v] = value.Paint;

		if ( was == value.Position )
		{
			Touch( was );
			return;
		}

		Touch( was );
		Touch( value.Position );

		vPosition[v] = value.Position;

		if ( CellOf( value.Position ) != vCell[v] )
		{
			GridRemove( v );
			GridInsert( v );
		}

		Grow( value.Position );

		//The edges at it are as long as they now are.
		for ( var c = vCorner[v]; c >= 0; c = cNext[c] )
		{
			var t = c - ( c % 3 );

			for ( var k = 0; k < 3; k++ )
			{
				var other = tVertex[t + k];
				if ( other == v ) continue;

				var length = ( vPosition[other] - value.Position ).Length;

				if ( length > longestEdge )
				{
					longestEdge = length;
					longestEdgeGrew = true;
				}
			}
		}
	}

	private void RawAddTriangleAt( int t, int a, int b, int c, ushort material, in Corner ca, in Corner cb, in Corner cc )
	{
		Reserve( 0, t + 1 );

		if ( t >= triangleSlots ) triangleSlots = t + 1;

		tVertex[t * 3] = a;
		tVertex[( t * 3 ) + 1] = b;
		tVertex[( t * 3 ) + 2] = c;
		tMaterial[t] = material;

		cUv[t * 3] = ca.Uv; cTangent[t * 3] = ca.Tangent;
		cUv[( t * 3 ) + 1] = cb.Uv; cTangent[( t * 3 ) + 1] = cb.Tangent;
		cUv[( t * 3 ) + 2] = cc.Uv; cTangent[( t * 3 ) + 2] = cc.Tangent;
		tAlive[t] = true;
		triangleCount++;

		for ( var k = 0; k < 3; k++ )
		{
			var v = tVertex[( t * 3 ) + k];

			cNext[( t * 3 ) + k] = vCorner[v];
			vCorner[v] = ( t * 3 ) + k;
		}

		var pa = vPosition[a];
		var pb = vPosition[b];
		var pc = vPosition[c];

		var longest = MathF.Max( ( pb - pa ).Length, MathF.Max( ( pc - pb ).Length, ( pa - pc ).Length ) );

		if ( longest > longestEdge )
		{
			longestEdge = longest;
			longestEdgeGrew = true;
		}

		Touch( pa );
		Touch( pb );
		Touch( pc );
	}

	private void RawRemoveTriangle( int t )
	{
		for ( var k = 0; k < 3; k++ )
		{
			var corner = ( t * 3 ) + k;
			var v = tVertex[corner];

			Touch( vPosition[v] );

			//Out of its vertex's list, which is as long as the vertex has triangles.
			if ( vCorner[v] == corner )
			{
				vCorner[v] = cNext[corner];
				continue;
			}

			for ( var c = vCorner[v]; c >= 0; c = cNext[c] )
			{
				if ( cNext[c] != corner ) continue;

				cNext[c] = cNext[corner];
				break;
			}
		}

		tAlive[t] = false;
		triangleCount--;
		freeTriangles.Push( t );
	}

	//the public changes: the primitives, recorded

	public int AddVertex( in Vertex value )
	{
		var v = -1;

		while ( freeVertices.Count > 0 && v < 0 )
		{
			var slot = freeVertices.Pop();
			if ( !vAlive[slot] ) v = slot;
		}

		if ( v < 0 ) v = vertexSlots;

		RawAddVertexAt( v, value );
		open?.AddedVertex( v, value );

		return v;
	}

	/// <summary>Changes a vertex - where it is, or anything else about it.</summary>
	public void SetVertex( int v, in Vertex value )
	{
		if ( !IsVertexAlive( v ) ) return;

		open?.SetVertex( v, GetVertex( v ), value );
		RawSetVertex( v, value );
	}

	/// <summary>A triangle, or less than nought for one that is not: two corners the same, or a corner that is dead.</summary>
	public int AddTriangle( int a, int b, int c, ushort material )
		=> AddTriangle( a, b, c, material, default, default, default );

	/// <summary>The same, with what each corner says for itself - see <see cref="Corner"/>.</summary>
	public int AddTriangle( int a, int b, int c, ushort material, in Corner ca, in Corner cb, in Corner cc )
	{
		if ( a == b || b == c || a == c ) return -1;
		if ( !IsVertexAlive( a ) || !IsVertexAlive( b ) || !IsVertexAlive( c ) ) return -1;

		var t = -1;

		while ( freeTriangles.Count > 0 && t < 0 )
		{
			var slot = freeTriangles.Pop();
			if ( !tAlive[slot] ) t = slot;
		}

		if ( t < 0 ) t = triangleSlots;

		RawAddTriangleAt( t, a, b, c, material, ca, cb, cc );
		open?.AddedTriangle( t, a, b, c, material, ca, cb, cc );

		return t;
	}

	/// <summary>Takes a triangle out. A vertex whose last triangle this was goes with it.</summary>
	public void RemoveTriangle( int t )
	{
		if ( !IsTriangleAlive( t ) ) return;

		GetTriangle( t, out var a, out var b, out var c );

		open?.RemovedTriangle( t, a, b, c, tMaterial[t], CornerOf( t, 0 ), CornerOf( t, 1 ), CornerOf( t, 2 ) );
		RawRemoveTriangle( t );

		Orphaned( a );
		Orphaned( b );
		Orphaned( c );
	}

	private void Orphaned( int v )
	{
		if ( !vAlive[v] || vCorner[v] >= 0 ) return;

		open?.RemovedVertex( v, GetVertex( v ) );
		RawRemoveVertex( v );
	}

	//what changed, and where

	private BBox touched;
	private bool anyTouched;

	private void Touch( Vector3 point )
	{
		touched = anyTouched ? touched.AddPoint( point ) : new BBox( point, point );
		anyTouched = true;
	}

	//Says what a brush call touched, and folds it into the open edit.
	private void Report()
	{
		if ( !anyTouched ) return;

		var box = touched;
		anyTouched = false;

		open?.Grow( box );
		Announce( box );
	}

	//Tells whoever listens, the mask's tops over the box first.
	private void Announce( BBox box )
	{
		StaleMask( box );
		Changed?.Invoke( box );
	}

	private TerrainMeshEdit open;

	/// <summary>Whether an edit is open - a brush stroke, from the button going down to it coming up.</summary>
	public bool IsEditing => open is not null;

	/// <summary>Everything from here to <see cref="EndEdit"/> is one change that can be undone.</summary>
	public void BeginEdit()
	{
		if ( open is not null ) return;

		anyTouched = false;
		open = new TerrainMeshEdit();
	}

	/// <summary>Closes the edit and hands it over, or null where nothing changed.</summary>
	public TerrainMeshEdit EndEdit()
	{
		var edit = open;
		open = null;

		if ( edit is null ) return null;

		if ( anyTouched ) edit.Grow( touched );
		anyTouched = false;

		Remeasure();

		if ( edit.IsEmpty ) return null;

		edit.Close();
		Announce( edit.Bounds );

		return edit;
	}

	/// <summary>Puts the mesh back as it was before an edit. Edits are undone in the reverse of the order they were made.</summary>
	public void Undo( TerrainMeshEdit edit )
	{
		if ( edit is null ) return;

		var was = open;
		open = null;

		for ( var i = edit.Ops.Count - 1; i >= 0; i-- )
		{
			var op = edit.Ops[i];

			switch ( op.Kind )
			{
				case TerrainMeshEdit.Kind.SetVertex: RawSetVertex( op.Id, edit.Vertices[op.Data] ); break;
				case TerrainMeshEdit.Kind.AddVertex: RawRemoveVertex( op.Id ); break;
				case TerrainMeshEdit.Kind.RemoveVertex: RawAddVertexAt( op.Id, edit.Vertices[op.Data] ); break;
				case TerrainMeshEdit.Kind.AddTriangle: RawRemoveTriangle( op.Id ); break;

				case TerrainMeshEdit.Kind.RemoveTriangle:
					var gone = edit.Triangles[op.Data];
					RawAddTriangleAt( op.Id, gone.A, gone.B, gone.C, gone.Material, gone.CA, gone.CB, gone.CC );
					break;

				case TerrainMeshEdit.Kind.SetTexel: RawSetTexel( edit.Texels[op.Data].Key, op.Id, edit.Texels[op.Data].Before ); break;
				case TerrainMeshEdit.Kind.AddTile: RawRemoveTile( edit.Tiles[op.Data].Key ); break;
				case TerrainMeshEdit.Kind.RemoveTile: RawAddTile( edit.Tiles[op.Data].Key, edit.Tiles[op.Data].Paint ); break;
			}
		}

		PublishMask();

		open = was;
		anyTouched = false;

		Remeasure();
		Announce( edit.Bounds );
	}

	/// <summary>Makes an undone edit again.</summary>
	public void Redo( TerrainMeshEdit edit )
	{
		if ( edit is null ) return;

		var was = open;
		open = null;

		foreach ( var op in edit.Ops )
		{
			switch ( op.Kind )
			{
				case TerrainMeshEdit.Kind.SetVertex: RawSetVertex( op.Id, edit.Vertices[op.Data + 1] ); break;
				case TerrainMeshEdit.Kind.AddVertex: RawAddVertexAt( op.Id, edit.Vertices[op.Data] ); break;
				case TerrainMeshEdit.Kind.RemoveVertex: RawRemoveVertex( op.Id ); break;
				case TerrainMeshEdit.Kind.RemoveTriangle: RawRemoveTriangle( op.Id ); break;

				case TerrainMeshEdit.Kind.AddTriangle:
					var made = edit.Triangles[op.Data];
					RawAddTriangleAt( op.Id, made.A, made.B, made.C, made.Material, made.CA, made.CB, made.CC );
					break;

				case TerrainMeshEdit.Kind.SetTexel: RawSetTexel( edit.Texels[op.Data].Key, op.Id, edit.Texels[op.Data].After ); break;
				case TerrainMeshEdit.Kind.AddTile: RawAddTile( edit.Tiles[op.Data].Key, edit.Tiles[op.Data].Paint ); break;
				case TerrainMeshEdit.Kind.RemoveTile: RawRemoveTile( edit.Tiles[op.Data].Key ); break;
			}
		}

		PublishMask();

		open = was;
		anyTouched = false;

		Remeasure();
		Changed?.Invoke( edit.Bounds );
	}

	//The longest edge, measured rather than remembered. It only ever grows while things move, and a bound that
	//has grown past the truth costs every question a wider search - so it is put right when an edit is over.
	private void Remeasure()
	{
		if ( !longestEdgeGrew ) return;

		longestEdgeGrew = false;

		var longest = 0.0f;

		for ( var t = 0; t < triangleSlots; t++ )
		{
			if ( !tAlive[t] ) continue;

			var a = vPosition[tVertex[t * 3]];
			var b = vPosition[tVertex[( t * 3 ) + 1]];
			var c = vPosition[tVertex[( t * 3 ) + 2]];

			var ab = ( b - a ).LengthSquared;
			var bc = ( c - b ).LengthSquared;
			var ca = ( a - c ).LengthSquared;

			longest = MathF.Max( longest, MathF.Max( ab, MathF.Max( bc, ca ) ) );
		}

		longestEdge = MathF.Sqrt( longest );
	}

	//questions

	//Whether a corner's vertex is the one its triangle is found through: the lowest id of the three.
	private bool Owns( int corner )
	{
		var t = corner - ( corner % 3 );
		var v = tVertex[corner];

		return v <= tVertex[t] && v <= tVertex[t + 1] && v <= tVertex[t + 2];
	}

	private void CellRange( float minX, float minZ, float maxX, float maxZ, out int x0, out int x1, out int z0, out int z1 )
	{
		x0 = Math.Clamp( Along( minX, gridMin.x ), 0, gridWide - 1 );
		x1 = Math.Clamp( Along( maxX, gridMin.x ), 0, gridWide - 1 );
		z0 = Math.Clamp( Along( minZ, gridMin.y ), 0, gridDeep - 1 );
		z1 = Math.Clamp( Along( maxZ, gridMin.y ), 0, gridDeep - 1 );
	}

	/// <summary>Every triangle whose middle stands over a rectangle of the ground plane - the low edges in, the high edges out, so rectangles side by side share nothing. What a render chunk is baked from.</summary>
	public void TrianglesInRect( Vector2 min, Vector2 max, List<int> into )
	{
		//A triangle's owner is within an edge's length of its middle.
		var reach = longestEdge + 1e-3f;

		CellRange( MathF.Max( min.x, -1e18f ) - reach, MathF.Max( min.y, -1e18f ) - reach,
			MathF.Min( max.x, 1e18f ) + reach, MathF.Min( max.y, 1e18f ) + reach,
			out var x0, out var x1, out var z0, out var z1 );

		for ( var z = z0; z <= z1; z++ )
		{
			for ( var x = x0; x <= x1; x++ )
			{
				for ( var v = gridFirst[( z * gridWide ) + x]; v >= 0; v = vCellNext[v] )
				{
					for ( var c = vCorner[v]; c >= 0; c = cNext[c] )
					{
						if ( !Owns( c ) ) continue;

						var t = c - ( c % 3 );

						var a = vPosition[tVertex[t]];
						var b = vPosition[tVertex[t + 1]];
						var d = vPosition[tVertex[t + 2]];

						var mx = ( a.x + b.x + d.x ) * ( 1.0f / 3.0f );
						var mz = ( a.z + b.z + d.z ) * ( 1.0f / 3.0f );

						if ( mx < min.x || mz < min.y || mx >= max.x || mz >= max.y ) continue;

						into.Add( t / 3 );
					}
				}
			}
		}
	}

	public void VerticesInSphere( Vector3 centre, float radius, List<int> into )
	{
		CellRange( centre.x - radius, centre.z - radius, centre.x + radius, centre.z + radius,
			out var x0, out var x1, out var z0, out var z1 );

		var reach = radius * radius;

		for ( var z = z0; z <= z1; z++ )
		{
			for ( var x = x0; x <= x1; x++ )
			{
				for ( var v = gridFirst[( z * gridWide ) + x]; v >= 0; v = vCellNext[v] )
				{
					if ( ( vPosition[v] - centre ).LengthSquared <= reach ) into.Add( v );
				}
			}
		}
	}

	/// <summary>Where a ray first meets the ground, from either side of it - a cave is looked at from inside. Walks the grid along the ray rather than the mesh.</summary>
	public bool Trace( Vector3 from, Vector3 direction, float distance, out float travelled, out int triangle, out Vector3 normal )
	{
		travelled = distance;
		triangle = -1;
		normal = Vector3.Up;

		if ( triangleCount == 0 || distance <= 0.0f ) return false;

		direction = direction.Normal;

		//Only the stretch of it over the grid, grown by what stands outside the grid and by how far a triangle reaches from its owner.
		var pad = overhang + longestEdge + gridCell;
		var near = 0.0f;
		var far = distance;

		if ( !ClipAxis( from.x, direction.x, gridMin.x - pad, gridMin.x + ( gridWide * gridCell ) + pad, ref near, ref far ) ) return false;
		if ( !ClipAxis( from.z, direction.z, gridMin.y - pad, gridMin.y + ( gridDeep * gridCell ) + pad, ref near, ref far ) ) return false;

		var around = (int)MathF.Ceiling( ( longestEdge + 1e-3f ) / gridCell );
		var best = distance;

		var start = from + ( direction * near );
		var cx = Along( start.x, gridMin.x );
		var cz = Along( start.z, gridMin.y );

		var stepX = direction.x > 0.0f ? 1 : -1;
		var stepZ = direction.z > 0.0f ? 1 : -1;

		var nextX = MathF.Abs( direction.x ) < 1e-9f ? float.MaxValue
			: near + ( ( gridMin.x + ( ( cx + ( stepX > 0 ? 1 : 0 ) ) * gridCell ) - start.x ) / direction.x );

		var nextZ = MathF.Abs( direction.z ) < 1e-9f ? float.MaxValue
			: near + ( ( gridMin.y + ( ( cz + ( stepZ > 0 ? 1 : 0 ) ) * gridCell ) - start.z ) / direction.z );

		var deltaX = MathF.Abs( direction.x ) < 1e-9f ? float.MaxValue : gridCell / MathF.Abs( direction.x );
		var deltaZ = MathF.Abs( direction.z ) < 1e-9f ? float.MaxValue : gridCell / MathF.Abs( direction.z );

		//The squares looked at on the step before, which the next step's overlap.
		int px0 = 1, px1 = 0, pz0 = 1, pz1 = 0;
		var entered = near;

		while ( entered <= far && entered <= best )
		{
			var x0 = Math.Clamp( cx - around, 0, gridWide - 1 );
			var x1 = Math.Clamp( cx + around, 0, gridWide - 1 );
			var z0 = Math.Clamp( cz - around, 0, gridDeep - 1 );
			var z1 = Math.Clamp( cz + around, 0, gridDeep - 1 );

			//Held to the grid: what stands outside it is kept in its edge squares, so a ray passing outside looks at those.
			{
				for ( var z = z0; z <= z1; z++ )
				{
					for ( var x = x0; x <= x1; x++ )
					{
						if ( x >= px0 && x <= px1 && z >= pz0 && z <= pz1 ) continue;

						for ( var v = gridFirst[( z * gridWide ) + x]; v >= 0; v = vCellNext[v] )
						{
							for ( var c = vCorner[v]; c >= 0; c = cNext[c] )
							{
								if ( !Owns( c ) ) continue;

								var t = c - ( c % 3 );

								if ( !Hits( from, direction, t, out var along ) || along >= best ) continue;

								best = along;
								triangle = t / 3;
							}
						}
					}
				}

				px0 = x0; px1 = x1; pz0 = z0; pz1 = z1;
			}

			if ( nextX < nextZ )
			{
				entered = nextX;
				nextX += deltaX;
				cx += stepX;
			}
			else
			{
				if ( nextZ == float.MaxValue ) break;

				entered = nextZ;
				nextZ += deltaZ;
				cz += stepZ;
			}
		}

		if ( triangle < 0 ) return false;

		travelled = best;
		normal = FaceNormal( triangle );

		return true;
	}

	private static bool ClipAxis( float from, float direction, float low, float high, ref float near, ref float far )
	{
		if ( MathF.Abs( direction ) < 1e-9f ) return from >= low && from <= high;

		var first = ( low - from ) / direction;
		var second = ( high - from ) / direction;

		if ( first > second ) ( first, second ) = ( second, first );

		near = MathF.Max( near, first );
		far = MathF.Min( far, second );

		return near <= far;
	}

	//A ray against one triangle, from either side. t is its first corner.
	private bool Hits( Vector3 from, Vector3 direction, int t, out float along )
	{
		along = 0.0f;

		var a = vPosition[tVertex[t]];
		var edge1 = vPosition[tVertex[t + 1]] - a;
		var edge2 = vPosition[tVertex[t + 2]] - a;

		var p = Vector3.Cross( direction, edge2 );
		var det = Vector3.Dot( edge1, p );

		if ( MathF.Abs( det ) < 1e-12f ) return false;

		var inv = 1.0f / det;
		var s = from - a;
		var u = Vector3.Dot( s, p ) * inv;

		if ( u < 0.0f || u > 1.0f ) return false;

		var q = Vector3.Cross( s, edge1 );
		var v = Vector3.Dot( direction, q ) * inv;

		if ( v < 0.0f || u + v > 1.0f ) return false;

		along = Vector3.Dot( edge2, q ) * inv;

		return along >= 0.0f;
	}

	/// <summary>Which way a triangle faces.</summary>
	public Vector3 FaceNormal( int t )
	{
		var a = vPosition[tVertex[t * 3]];
		var b = vPosition[tVertex[( t * 3 ) + 1]];
		var c = vPosition[tVertex[( t * 3 ) + 2]];

		return Vector3.Cross( b - a, c - a ).Normal;
	}

	//brushes

	//Kept between calls, so a stroke of a thousand dabs is not a thousand lists. The brushes are the main thread's; the questions above use none of it.
	private readonly List<int> brushed = new();
	private readonly List<int> ring = new();
	private readonly List<int> ringB = new();
	private readonly List<int> around = new();
	private readonly List<int> aroundB = new();
	private readonly List<Vector3> targets = new();
	//The edges one remesh pass has looked at. Keyed by the pair, not packed into a long: a long hashes as its halves
	//exclusive-or'd, and neighbouring vertex numbers are close, so a dab's edges would share a handful of buckets.
	private readonly HashSet<(int, int)> seenEdges = new();
	private readonly List<(float Length, int A, int B)> edges = new();
	private readonly HashSet<int> renormal = new();
	private readonly List<int> renormalList = new();

	private static float Falloff( float t )
	{
		if ( t >= 1.0f ) return 0.0f;

		var inside = 1.0f - ( t * t );
		return inside * inside;
	}

	//The vertices a brush reaches, and how hard it reaches each.
	private float Weight( int v, Vector3 centre, float radius )
		=> Falloff( ( vPosition[v] - centre ).Length / MathF.Max( radius, 1e-6f ) );

	private void Move( int v, Vector3 to )
	{
		var value = GetVertex( v );
		value.Position = to;

		SetVertex( v, value );
	}

	/// <summary>Scales all ground about the terrain corner; refused while a stroke is open, undone by inverse</summary>
	public bool Stretch( float across, float up )
	{
		if ( open is not null ) return false;
		if ( !( across > 0.0f ) || !( up > 0.0f ) ) return false;
		if ( across == 1.0f && up == 1.0f ) return false;

		var was = Bounds;
		var factor = new Vector3( across, up, across );

		//A normal goes by the inverse of the stretch and a tangent by the stretch - the one is across the surface and the other along it.
		var inverse = new Vector3( 1.0f / across, 1.0f / up, 1.0f / across );

		anyBounds = false;
		gridCell *= across;
		gridMin *= across;
		overhang *= across;
		Array.Fill( gridFirst, -1 );

		for ( var v = 0; v < vertexSlots; v++ )
		{
			if ( !vAlive[v] ) continue;

			vPosition[v] *= factor;
			vNormal[v] = ( vNormal[v] * inverse ).Normal;

			GridInsert( v );
			Grow( vPosition[v] );
		}

		for ( var t = 0; t < triangleSlots; t++ )
		{
			if ( !tAlive[t] ) continue;

			for ( var k = 0; k < 3; k++ )
			{
				var at = ( t * 3 ) + k;
				var along = new Vector3( cTangent[at].x, cTangent[at].y, cTangent[at].z ) * factor;
				along = along.IsNearZeroLength ? along : along.Normal;
				cTangent[at] = new Vector4( along.x, along.y, along.z, cTangent[at].w );
			}
		}

		longestEdge *= MathF.Max( across, up );

		StretchMask( across );

		Touch( was.Mins );
		Touch( was.Maxs );
		Touch( Bounds.Mins );
		Touch( Bounds.Maxs );
		Report();

		return true;
	}

	/// <summary>Moves the ground along a direction - up to raise it, along the view to carve into a cliff.</summary>
	public void Displace( Vector3 centre, float radius, Vector3 direction, float amount )
	{
		direction = direction.Normal;

		brushed.Clear();
		VerticesInSphere( centre, radius, brushed );

		foreach ( var v in brushed )
			Move( v, vPosition[v] + ( direction * ( amount * Weight( v, centre, radius ) ) ) );

		Renormal( brushed );
		Report();
	}

	/// <summary>Moves the ground along a direction by as much as <paramref name="amountAt"/> says for where each vertex stands - a field of bumps rather than one - times how hard the brush reaches it. Asked of each vertex once, where it stood before the dab.</summary>
	public void Displace( Vector3 centre, float radius, Vector3 direction, Func<Vector3, float> amountAt )
	{
		direction = direction.Normal;

		brushed.Clear();
		VerticesInSphere( centre, radius, brushed );

		foreach ( var v in brushed )
		{
			var weight = Weight( v, centre, radius );
			if ( weight <= 0.0f ) continue;

			Move( v, vPosition[v] + ( direction * ( amountAt( vPosition[v] ) * weight ) ) );
		}

		Renormal( brushed );
		Report();
	}

	/// <summary>Moves each vertex along its own normal: swells what is there, or with less than nought hollows it.</summary>
	public void Inflate( Vector3 centre, float radius, float amount )
	{
		brushed.Clear();
		VerticesInSphere( centre, radius, brushed );

		foreach ( var v in brushed )
			Move( v, vPosition[v] + ( vNormal[v] * ( amount * Weight( v, centre, radius ) ) ) );

		Renormal( brushed );
		Report();
	}

	/// <summary>Smooths at brush scale by moving vertices along their normal toward a cell-averaged surface</summary>
	public void Smooth( Vector3 centre, float radius, float strength, float edge = 0.0f )
	{
		strength = Math.Clamp( strength, 0.0f, 1.0f );
		if ( strength <= 0.0f ) return;

		brushed.Clear();
		VerticesInSphere( centre, radius, brushed );
		if ( brushed.Count == 0 ) return;

		var cell = MathF.Max( MathF.Max( radius * SmoothCell, edge * 2.0f ), 1e-3f );
		var origin = centre - new Vector3( radius + cell );

		smoothCells.Clear();

		foreach ( var v in brushed )
		{
			var key = CellOf( vPosition[v], origin, cell );

			smoothCells.TryGetValue( key, out var had );
			smoothCells[key] = (had.Sum + vPosition[v], had.Count + 1);
		}

		//Worked out from where everything is before any of it moves.
		targets.Clear();

		foreach ( var v in brushed )
		{
			var at = vPosition[v];

			//Which eight cells the vertex stands between, and how far across them.
			var local = ( ( at - origin ) / cell ) - new Vector3( 0.5f );
			var x0 = (int)MathF.Floor( local.x );
			var y0 = (int)MathF.Floor( local.y );
			var z0 = (int)MathF.Floor( local.z );
			var fx = local.x - x0;
			var fy = local.y - y0;
			var fz = local.z - z0;

			var sum = Vector3.Zero;
			var total = 0.0f;

			for ( var k = 0; k < 8; k++ )
			{
				var dx = k & 1;
				var dy = ( k >> 1 ) & 1;
				var dz = ( k >> 2 ) & 1;

				if ( !smoothCells.TryGetValue( (x0 + dx, y0 + dy, z0 + dz), out var one ) ) continue;

				var weight = ( dx == 1 ? fx : 1.0f - fx ) * ( dy == 1 ? fy : 1.0f - fy ) * ( dz == 1 ? fz : 1.0f - fz );

				sum += ( one.Sum / one.Count ) * weight;
				total += weight;
			}

			if ( total <= 1e-6f )
			{
				targets.Add( at );
				continue;
			}

			var normal = vNormal[v];
			var off = Vector3.Dot( ( sum / total ) - at, normal );

			targets.Add( at + ( normal * off ) );
		}

		for ( var i = 0; i < brushed.Count; i++ )
		{
			var v = brushed[i];
			Move( v, Vector3.Lerp( vPosition[v], targets[i], strength * Weight( v, centre, radius ), false ) );
		}

		Renormal( brushed );
		Report();
	}

	//How large a cell the smooth brush averages over, as a part of the brush - see Smooth.
	private const float SmoothCell = 0.25f;

	private readonly Dictionary<(int X, int Y, int Z), (Vector3 Sum, int Count)> smoothCells = new();

	private static (int X, int Y, int Z) CellOf( Vector3 at, Vector3 origin, float cell )
	{
		var local = ( at - origin ) / cell;
		return ((int)MathF.Floor( local.x ), (int)MathF.Floor( local.y ), (int)MathF.Floor( local.z ));
	}

	/// <summary>Draws the ground onto a plane.</summary>
	public void Flatten( Vector3 centre, float radius, Vector3 planePoint, Vector3 planeNormal, float strength )
	{
		planeNormal = planeNormal.Normal;
		strength = Math.Clamp( strength, 0.0f, 1.0f );

		brushed.Clear();
		VerticesInSphere( centre, radius, brushed );

		foreach ( var v in brushed )
		{
			var off = Vector3.Dot( vPosition[v] - planePoint, planeNormal );
			Move( v, vPosition[v] - ( planeNormal * ( off * strength * Weight( v, centre, radius ) ) ) );
		}

		Renormal( brushed );
		Report();
	}

	//On the vertices, and on the mask over the top of the ground wherever the brush reaches that.
	/// <summary>Paints a layer: more of it under the brush, and the rest scaled back so the whole still comes to one.</summary>
	public void Paint( Vector3 centre, float radius, int layer, float strength )
	{
		if ( layer < 0 || layer >= Terrain.MostLayers ) return;

		//The mask first: a tile it makes is filled from the vertices as they were before this dab.
		PaintMask( centre, radius, layer, strength );

		brushed.Clear();
		VerticesInSphere( centre, radius, brushed );

		foreach ( var v in brushed )
		{
			var add = strength * Weight( v, centre, radius );
			if ( add == 0.0f ) continue;

			var value = GetVertex( v );
			value.Paint = value.Paint.With( layer, add );

			SetVertex( v, value );
		}

		Report();
	}

	/// <summary>A hole: every triangle whose middle is under the brush goes.</summary>
	public void DeleteTriangles( Vector3 centre, float radius )
	{
		brushed.Clear();
		VerticesInSphere( centre, radius + longestEdge, brushed );

		ring.Clear();

		foreach ( var v in brushed )
		{
			for ( var c = vCorner[v]; c >= 0; c = cNext[c] )
			{
				if ( !Owns( c ) ) continue;

				var t = c - ( c % 3 );
				var middle = ( vPosition[tVertex[t]] + vPosition[tVertex[t + 1]] + vPosition[tVertex[t + 2]] ) / 3.0f;

				if ( ( middle - centre ).LengthSquared <= radius * radius ) ring.Add( t / 3 );
			}
		}

		renormal.Clear();

		foreach ( var t in ring )
		{
			GetTriangle( t, out var a, out var b, out var c );

			renormal.Add( a ); renormal.Add( b ); renormal.Add( c );
			RemoveTriangle( t );
		}

		FlushRenormal();
		Report();
	}

	/// <summary>Closes one corner of a hole's rim per dab, so holding the brush paints the hole shut</summary>
	public void FillHole( Vector3 centre, float radius )
	{
		if ( radius <= 0.0f ) return;

		var reach = radius * radius;
		renormal.Clear();
		var added = 0;

		for ( var n = 0; n < 48 && CloseOne( centre, reach ); n++ ) added++;

		if ( added == 0 ) return;

		FlushRenormal();
		Report();
	}

	/// <summary>Where a ray crosses a hole. There is no triangle there to meet, so the place is the ray against the plane of the nearest rim the brush can reach.</summary>
	public bool TraceOpening( Vector3 from, Vector3 direction, float distance, float radius, out Vector3 position, out Vector3 normal )
	{
		position = default;
		normal = Vector3.Up;

		if ( radius <= 0.0f || distance <= 0.0f ) return false;

		direction = direction.Normal;

		//Only the stretch over the ground. A camera far off would otherwise step so wide that the rim of the hole falls between the samples.
		var pad = overhang + longestEdge + gridCell + radius;
		var near = 0.0f;
		var far = distance;

		if ( !ClipAxis( from.x, direction.x, gridMin.x - pad, gridMin.x + ( gridWide * gridCell ) + pad, ref near, ref far ) ) return false;
		if ( !ClipAxis( from.z, direction.z, gridMin.y - pad, gridMin.y + ( gridDeep * gridCell ) + pad, ref near, ref far ) ) return false;

		var span = MathF.Max( far - near, 0.0f );
		var step = MathF.Max( radius * 0.5f, 0.25f );
		var steps = (int)MathF.Ceiling( span / step );

		if ( steps > 160 )
		{
			steps = 160;
			step = span / steps;
		}

		var query = radius + ( step * 0.5f );
		var bestAlong = float.MaxValue;
		var best = -1;

		for ( var i = 0; i <= steps; i++ )
		{
			var along = near + ( span * ( i / (float)Math.Max( steps, 1 ) ) );
			var at = from + ( direction * along );

			brushed.Clear();
			VerticesInSphere( at, query, brushed );

			foreach ( var v in brushed )
			{
				if ( !IsBorder( v ) ) continue;

				var rel = vPosition[v] - from;
				var rayAlong = Vector3.Dot( rel, direction );

				if ( rayAlong < 0.0f || rayAlong > distance ) continue;
				if ( ( rel - ( direction * rayAlong ) ).Length > radius ) continue;
				if ( rayAlong >= bestAlong ) continue;

				bestAlong = rayAlong;
				best = v;
			}
		}

		if ( best < 0 ) return false;

		var count = 0;
		var acc = Vector3.Zero;
		var accN = Vector3.Zero;

		brushed.Clear();
		VerticesInSphere( vPosition[best], radius, brushed );

		foreach ( var v in brushed )
		{
			if ( !IsBorder( v ) ) continue;

			acc += vPosition[v];
			accN += vNormal[v];
			count++;
		}

		if ( count == 0 ) return false;

		acc /= count;
		normal = accN.IsNearZeroLength ? Vector3.Up : accN.Normal;

		var plane = new Plane( acc, normal );
		var ray = new Ray( from, direction );

		if ( !plane.TryTrace( ray, out position, true, distance ) )
			position = plane.SnapToPlane( from + ( direction * bestAlong ) );

		return ( position - acc ).Length <= radius * 2.0f;
	}

	//Open edges under the brush, before a loop is known to be a hole.
	private readonly List<(int From, int To)> seam = new();

	private readonly List<(int From, int To, int Triangle)> loopEdges = new();
	private readonly HashSet<(int, int)> seamSeen = new();
	private readonly List<int> loopVerts = new();

	private bool CloseOne( Vector3 centre, float reach )
	{
		seam.Clear();
		seamSeen.Clear();

		brushed.Clear();
		VerticesInSphere( centre, MathF.Sqrt( reach ) + longestEdge, brushed );

		foreach ( var v in brushed )
		{
			for ( var c = vCorner[v]; c >= 0; c = cNext[c] )
			{
				if ( !Owns( c ) ) continue;

				var t = c - ( c % 3 );

				for ( var k = 0; k < 3; k++ )
				{
					var a = tVertex[t + k];
					var b = tVertex[t + ( ( k + 1 ) % 3 )];

					if ( EdgeUses( a, b ) != 1 ) continue;

					var mid = ( vPosition[a] + vPosition[b] ) * 0.5f;
					if ( ( mid - centre ).LengthSquared > reach ) continue;

					seam.Add( ( a, b ) );
				}
			}
		}

		var bestArea = float.MaxValue;
		var found = false;
		int from = 0, to = 0, tip = 0, tri = 0;

		foreach ( var edge in seam )
		{
			if ( !seamSeen.Add( ( edge.From, edge.To ) ) ) continue;
			if ( !WalkRim( edge.From, edge.To ) ) continue;

			for ( var i = 0; i < loopEdges.Count; i++ )
				seamSeen.Add( ( loopEdges[i].From, loopEdges[i].To ) );

			if ( !LoopIsHole( loopEdges[0].Triangle ) ) continue;

			//The ear is judged while this loop is the one in hand. A later loop replaces it, and the corner already chosen does not need it again.
			for ( var i = 0; i < loopEdges.Count; i++ )
			{
				var open = loopEdges[i];
				var mid = ( vPosition[open.From] + vPosition[open.To] ) * 0.5f;

				if ( ( mid - centre ).LengthSquared > reach ) continue;
				if ( !Outgoing( open.To, out var next, out _ ) || next == open.From ) continue;
				if ( !Ear( open.From, open.To, next, open.Triangle, centre, reach, out var area ) ) continue;
				if ( area >= bestArea ) continue;

				bestArea = area;
				from = open.To;
				to = open.From;
				tip = next;
				tri = open.Triangle;
				found = true;
			}
		}

		if ( !found ) return false;

		//from is the far end of the open edge and to is its near end, so the new face runs the edge backwards and the opening is on its left.
		var material = tMaterial[tri];
		var made = AddTriangle( from, to, tip, material, CornerAt( tri, from ), CornerAt( tri, to ), CornerAtAny( tip ) );

		if ( made < 0 ) return false;

		renormal.Add( from );
		renormal.Add( to );
		renormal.Add( tip );

		return true;
	}

	//The rim walked with the ground on the left. False when it does not close.
	private bool WalkRim( int from, int to )
	{
		loopVerts.Clear();
		loopEdges.Clear();

		var a = from;
		var b = to;

		for ( var guard = 0; guard < 10000; guard++ )
		{
			if ( !Outgoing( a, out var next, out var triangle ) || next != b ) return false;

			loopVerts.Add( a );
			loopEdges.Add( ( a, b, triangle ) );

			if ( !Outgoing( b, out var after, out _ ) ) return false;

			a = b;
			b = after;

			if ( a == from && b == to ) return loopVerts.Count >= 3;
		}

		return false;
	}

	//A hole winds clockwise about itself, so the area of the rim points against the ground. The terrain's outline winds the other way.
	private bool LoopIsHole( int triangle )
	{
		GetTriangle( triangle, out var a, out var b, out var c );

		var face = Vector3.Cross( vPosition[b] - vPosition[a], vPosition[c] - vPosition[a] );
		if ( face.IsNearZeroLength ) return false;

		var origin = vPosition[loopVerts[0]];
		var area = Vector3.Zero;

		for ( var i = 0; i < loopVerts.Count; i++ )
		{
			var p = vPosition[loopVerts[i]] - origin;
			var q = vPosition[loopVerts[( i + 1 ) % loopVerts.Count]] - origin;
			area += Vector3.Cross( p, q );
		}

		return Vector3.Dot( area, face ) < 0.0f;
	}

	//Whether a triangle already runs from a to b.
	private bool Runs( int a, int b )
	{
		for ( var c = vCorner[a]; c >= 0; c = cNext[c] )
		{
			var t = c - ( c % 3 );
			if ( tVertex[t + ( ( c % 3 + 1 ) % 3 )] == b ) return true;
		}

		return false;
	}

	//The one open edge leaving a vertex, in the triangle's own order.
	private bool Outgoing( int from, out int to, out int triangle )
	{
		to = -1;
		triangle = -1;

		var found = 0;

		for ( var c = vCorner[from]; c >= 0; c = cNext[c] )
		{
			var t = c - ( c % 3 );
			var k = c % 3;
			var next = tVertex[t + ( ( k + 1 ) % 3 )];

			if ( EdgeUses( from, next ) != 1 ) continue;

			found++;
			to = next;
			triangle = t / 3;
		}

		return found == 1;
	}

	//Whether tip closes the open edge without crossing the ground or the hole.
	private bool Ear( int from, int to, int tip, int triangle, Vector3 centre, float reach, out float area )
	{
		area = 0.0f;

		if ( tip == from || tip == to ) return false;
		if ( !IsVertexAlive( tip ) ) return false;
		if ( EdgeUses( to, tip ) >= 2 || EdgeUses( from, tip ) >= 2 ) return false;

		//The new face runs to->from, from->tip and tip->to. An edge already run that way would put two triangles on the same side of it.
		if ( Runs( to, from ) || Runs( from, tip ) || Runs( tip, to ) ) return false;

		GetTriangle( triangle, out var a, out var b, out var c );

		var face = Vector3.Cross( vPosition[b] - vPosition[a], vPosition[c] - vPosition[a] );
		if ( face.IsNearZeroLength ) return false;

		var faceN = face.Normal;

		//On the right of from->to: the opening, not another copy of the ground.
		if ( Vector3.Dot( faceN, Vector3.Cross( vPosition[to] - vPosition[from], vPosition[tip] - vPosition[from] ) ) >= -1e-5f )
			return false;

		var span = Vector3.Cross( vPosition[from] - vPosition[to], vPosition[tip] - vPosition[to] );
		area = span.Length;

		if ( area < 1e-6f ) return false;
		if ( Vector3.Dot( faceN, span ) <= 0.0f ) return false;

		var mid = ( vPosition[from] + vPosition[to] + vPosition[tip] ) * ( 1.0f / 3.0f );
		if ( ( mid - centre ).LengthSquared > reach ) return false;

		//A corner that still has another rim vertex inside it is not a corner.
		for ( var i = 0; i < loopVerts.Count; i++ )
		{
			var v = loopVerts[i];
			if ( v == from || v == to || v == tip ) continue;
			if ( StrictlyInside( vPosition[to], vPosition[from], vPosition[tip], vPosition[v], faceN ) ) return false;
		}

		return true;
	}

	private static bool StrictlyInside( Vector3 a, Vector3 b, Vector3 c, Vector3 p, Vector3 normal )
	{
		var ab = Vector3.Dot( normal, Vector3.Cross( b - a, p - a ) );
		var bc = Vector3.Dot( normal, Vector3.Cross( c - b, p - b ) );
		var ca = Vector3.Dot( normal, Vector3.Cross( a - c, p - c ) );
		const float slack = 1e-4f;

		return ab > slack && bc > slack && ca > slack;
	}

	//A corner's own numbers, from any triangle still at the vertex.
	private Corner CornerAtAny( int v )
	{
		var c = vCorner[v];
		if ( c < 0 ) return default;

		return CornerOf( c / 3, c % 3 );
	}

	/// <summary>Which way the ground under a brush faces, on the whole.</summary>
	public Vector3 AreaNormal( Vector3 centre, float radius )
	{
		var found = new List<int>();
		VerticesInSphere( centre, radius, found );

		var sum = Vector3.Zero;

		foreach ( var v in found ) sum += vNormal[v] * Weight( v, centre, radius );

		return sum.IsNearZeroLength ? Vector3.Up : sum.Normal;
	}

	//normals

	/// <summary>Works the normals of some vertices out again from the triangles round them, each counting for its area.</summary>
	public void RecomputeNormals( List<int> vertices )
	{
		foreach ( var v in vertices )
		{
			if ( !IsVertexAlive( v ) ) continue;

			var sum = Vector3.Zero;

			for ( var c = vCorner[v]; c >= 0; c = cNext[c] )
			{
				var t = c - ( c % 3 );
				var a = vPosition[tVertex[t]];

				//Twice the triangle's area long, which is the weighting wanted.
				sum += Vector3.Cross( vPosition[tVertex[t + 1]] - a, vPosition[tVertex[t + 2]] - a );
			}

			if ( sum.IsNearZeroLength ) continue;

			var normal = sum.Normal;
			if ( normal == vNormal[v] ) continue;

			var value = GetVertex( v );
			value.Normal = normal;

			SetVertex( v, value );
		}
	}

	//The normals of what moved and of what stands next to it.
	private void Renormal( List<int> moved )
	{
		renormal.Clear();

		foreach ( var v in moved )
		{
			if ( !vAlive[v] ) continue;

			renormal.Add( v );

			for ( var c = vCorner[v]; c >= 0; c = cNext[c] )
			{
				var t = c - ( c % 3 );

				renormal.Add( tVertex[t] );
				renormal.Add( tVertex[t + 1] );
				renormal.Add( tVertex[t + 2] );
			}
		}

		FlushRenormal();
	}

	private void FlushRenormal()
	{
		renormalList.Clear();
		renormalList.AddRange( renormal );
		renormal.Clear();

		RecomputeNormals( renormalList );
	}

	//what is round a vertex

	/// <summary>The triangles at a vertex. The triangles that have a vertex as a corner.</summary>
	public void TrianglesAround( int v, List<int> into )
	{
		into.Clear();
		if ( !IsVertexAlive( v ) ) return;

		for ( var c = vCorner[v]; c >= 0; c = cNext[c] ) into.Add( c / 3 );
	}

	private void Ring( int v, List<int> into )
	{
		into.Clear();

		for ( var c = vCorner[v]; c >= 0; c = cNext[c] ) into.Add( c / 3 );
	}

	//The vertices an edge away from one.
	private void Neighbours( int v, List<int> into )
	{
		into.Clear();

		for ( var c = vCorner[v]; c >= 0; c = cNext[c] )
		{
			var t = c - ( c % 3 );

			for ( var k = 0; k < 3; k++ )
			{
				var other = tVertex[t + k];
				if ( other != v && !into.Contains( other ) ) into.Add( other );
			}
		}
	}

	//How many triangles an edge has.
	private int EdgeUses( int a, int b )
	{
		var uses = 0;

		for ( var c = vCorner[a]; c >= 0; c = cNext[c] )
		{
			var t = c - ( c % 3 );
			if ( tVertex[t] == b || tVertex[t + 1] == b || tVertex[t + 2] == b ) uses++;
		}

		return uses;
	}

	private void EdgeTriangles( int a, int b, List<int> into )
	{
		into.Clear();

		for ( var c = vCorner[a]; c >= 0; c = cNext[c] )
		{
			var t = c - ( c % 3 );
			if ( tVertex[t] == b || tVertex[t + 1] == b || tVertex[t + 2] == b ) into.Add( t / 3 );
		}
	}

	//Whether a vertex is on an open edge.
	private bool IsBorder( int v )
	{
		for ( var c = vCorner[v]; c >= 0; c = cNext[c] )
		{
			var t = c - ( c % 3 );

			for ( var k = 0; k < 3; k++ )
			{
				var other = tVertex[t + k];
				if ( other != v && EdgeUses( v, other ) == 1 ) return true;
			}
		}

		return false;
	}

	//Whether a vertex is somewhere the shape turns on: an open edge, or where two materials meet.
	private bool IsFeature( int v )
	{
		var first = -1;

		for ( var c = vCorner[v]; c >= 0; c = cNext[c] )
		{
			var material = tMaterial[c / 3];

			if ( first < 0 ) first = material;
			else if ( first != material ) return true;
		}

		return IsBorder( v );
	}

	//Whether a triangle goes from one vertex straight to another, among the triangles at the first that are not leaving.
	private bool HasDirected( int from, int to, List<int> leaving )
	{
		for ( var c = vCorner[from]; c >= 0; c = cNext[c] )
		{
			var t = c - ( c % 3 );
			var k = c % 3;

			if ( tVertex[t + ( ( k + 1 ) % 3 )] != to ) continue;
			if ( leaving.Contains( t / 3 ) ) continue;

			return true;
		}

		return false;
	}

	//dynamic topology

	/// <summary>Splits edges over 4/3 of target and collapses under 4/5, so brushes don't make slivers</summary>
	public void Remesh( Vector3 centre, float radius, float targetEdge )
	{
		targetEdge = MathF.Max( targetEdge, 1e-3f );

		var tooLong = targetEdge * ( 4.0f / 3.0f );
		var tooShort = targetEdge * ( 4.0f / 5.0f );

		renormal.Clear();

		for ( var pass = 0; pass < 6; pass++ )
		{
			var changed = 0;

			GatherEdges( centre, radius );
			edges.Sort( static ( l, r ) => r.Length.CompareTo( l.Length ) );

			foreach ( var (_, a, b) in edges )
			{
				if ( !vAlive[a] || !vAlive[b] ) continue;
				if ( ( vPosition[a] - vPosition[b] ).Length <= tooLong ) continue;
				if ( Split( a, b ) ) changed++;
			}

			GatherEdges( centre, radius );
			edges.Sort( static ( l, r ) => l.Length.CompareTo( r.Length ) );

			foreach ( var (_, a, b) in edges )
			{
				if ( !vAlive[a] || !vAlive[b] ) continue;
				if ( ( vPosition[a] - vPosition[b] ).Length >= tooShort ) continue;
				if ( Collapse( a, b, tooLong ) ) changed++;
			}

			if ( changed == 0 ) break;
		}

		FlushRenormal();
		Report();
	}

	private void GatherEdges( Vector3 centre, float radius )
	{
		brushed.Clear();
		VerticesInSphere( centre, radius, brushed );

		seenEdges.Clear();
		edges.Clear();

		var reach = radius * radius;

		foreach ( var v in brushed )
		{
			for ( var c = vCorner[v]; c >= 0; c = cNext[c] )
			{
				var t = c - ( c % 3 );

				for ( var k = 0; k < 3; k++ )
				{
					var other = tVertex[t + k];
					if ( other == v ) continue;
					if ( ( vPosition[other] - centre ).LengthSquared > reach ) continue;

					var low = Math.Min( v, other );
					var high = Math.Max( v, other );

					if ( !seenEdges.Add( ( low, high ) ) ) continue;

					edges.Add( ( ( vPosition[v] - vPosition[other] ).Length, low, high ) );
				}
			}
		}
	}

	private static Vertex Between( in Vertex a, in Vertex b )
	{
		return new Vertex
		{
			Position = ( a.Position + b.Position ) * 0.5f,
			Normal = ( a.Normal + b.Normal ).Normal,
			Paint = TerrainPaint.Between( a.Paint, b.Paint ),
		};
	}

	private static Corner Between( in Corner a, in Corner b ) => new()
	{
		Uv = ( a.Uv + b.Uv ) * 0.5f,
		Tangent = new Vector4( ( a.Tangent.x + b.Tangent.x ) * 0.5f, ( a.Tangent.y + b.Tangent.y ) * 0.5f,
			( a.Tangent.z + b.Tangent.z ) * 0.5f, a.Tangent.w ),
	};

	//A triangle's own corner at a vertex of it.
	private Corner CornerAt( int t, int v )
	{
		for ( var k = 0; k < 3; k++ )
			if ( tVertex[( t * 3 ) + k] == v ) return CornerOf( t, k );

		return default;
	}

	//Splits an edge at its midpoint, blending vertex and per-triangle corner data into it
	private bool Split( int a, int b )
	{
		EdgeTriangles( a, b, ring );

		if ( ring.Count is 0 or > 2 ) return false;

		var middle = AddVertex( Between( GetVertex( a ), GetVertex( b ) ) );

		foreach ( var t in ring )
		{
			GetTriangle( t, out var x, out var y, out var z );
			var material = tMaterial[t];

			var cx = CornerOf( t, 0 ); var cy = CornerOf( t, 1 ); var cz = CornerOf( t, 2 );
			var half = Between( CornerAt( t, a ), CornerAt( t, b ) );

			//The same triangle twice over, once with each end moved to the middle: both halves wind the way the whole did.
			AddTriangle( x == b ? middle : x, y == b ? middle : y, z == b ? middle : z, material,
				x == b ? half : cx, y == b ? half : cy, z == b ? half : cz );
			AddTriangle( x == a ? middle : x, y == a ? middle : y, z == a ? middle : z, material,
				x == a ? half : cx, y == a ? half : cy, z == a ? half : cz );

			renormal.Add( x ); renormal.Add( y ); renormal.Add( z );
		}

		foreach ( var t in ring ) RemoveTriangle( t );

		renormal.Add( middle );
		return true;
	}

	//Two ends of an edge made one vertex, where that leaves the mesh a surface: refused where it would pinch two sheets together, fold a triangle over,
	//stretch an edge past what would only be split again, pull an open edge or a seam between two materials out of line, or join two materials' triangles.
	private bool Collapse( int a, int b, float tooLong )
	{
		EdgeTriangles( a, b, ring );

		if ( ring.Count is 0 or > 2 ) return false;
		if ( ring.Count == 2 && tMaterial[ring[0]] != tMaterial[ring[1]] ) return false;

		//Nowhere near what was set in. Its triangles are a model's, with its texture laid over them corner by corner; folding one of its edges away
		//would drag a corner's coordinate somewhere it does not belong. A rock sculpted is split finer where the brush stretches it, never coarsened.
		if ( WearsAnything( a ) || WearsAnything( b ) ) return false;

		var openEdge = ring.Count == 1;
		var featureA = IsFeature( a );
		var featureB = IsFeature( b );

		Vertex target;

		if ( featureA && featureB )
		{
			//Two ends that both matter: only along an open edge that runs straight, where the middle is on the line both were on.
			if ( !openEdge || !RunsStraight( a, b ) || !RunsStraight( b, a ) ) return false;

			target = Between( GetVertex( a ), GetVertex( b ) );
		}
		else if ( featureA )
		{
			target = GetVertex( a );
		}
		else if ( featureB )
		{
			( a, b ) = ( b, a );
			target = GetVertex( a );
		}
		else
		{
			target = Between( GetVertex( a ), GetVertex( b ) );
		}

		//The link condition: what stands next to both ends is exactly what stands across the
		//edge from it. Anything more and the collapse would press two sheets into one edge.
		Neighbours( a, around );
		Neighbours( b, aroundB );

		var shared = 0;

		foreach ( var n in around )
		{
			if ( !aroundB.Contains( n ) ) continue;

			shared++;

			var across = false;

			foreach ( var t in ring )
			{
				GetTriangle( t, out var x, out var y, out var z );
				if ( x == n || y == n || z == n ) across = true;
			}

			if ( !across ) return false;
		}

		if ( shared != ring.Count ) return false;

		//What is left of the triangles at either end, with that end moved: none may fold over, flatten to nothing or come out too long.
		Ring( b, ringB );

		for ( var side = 0; side < 2; side++ )
		{
			var end = side == 0 ? a : b;

			for ( var c = vCorner[end]; c >= 0; c = cNext[c] )
			{
				var t = c / 3;
				if ( ring.Contains( t ) ) continue;

				GetTriangle( t, out var x, out var y, out var z );

				var px = vPosition[x]; var py = vPosition[y]; var pz = vPosition[z];
				var before = Vector3.Cross( py - px, pz - px );

				if ( x == end ) px = target.Position;
				if ( y == end ) py = target.Position;
				if ( z == end ) pz = target.Position;

				var after = Vector3.Cross( py - px, pz - px );

				if ( after.LengthSquared < 1e-12f ) return false;
				if ( Vector3.Dot( before.Normal, after.Normal ) <= 0.2f ) return false;

				if ( ( py - px ).Length > tooLong || ( pz - py ).Length > tooLong || ( px - pz ).Length > tooLong ) return false;
			}
		}

		//And none may run along an edge a triangle staying at the kept end already runs along, the same way.
		foreach ( var t in ringB )
		{
			if ( ring.Contains( t ) ) continue;

			GetTriangle( t, out var x, out var y, out var z );

			if ( x == b ) x = a; if ( y == b ) y = a; if ( z == b ) z = a;

			if ( x == a && ( HasDirected( a, y, ring ) || HasDirected( z, a, ring ) ) ) return false;
			if ( y == a && ( HasDirected( a, z, ring ) || HasDirected( x, a, ring ) ) ) return false;
			if ( z == a && ( HasDirected( a, x, ring ) || HasDirected( y, a, ring ) ) ) return false;
		}

		//Made first and taken after, so the kept end is never without a triangle on the way and does not die of it.
		foreach ( var t in ringB )
		{
			if ( ring.Contains( t ) ) continue;

			GetTriangle( t, out var x, out var y, out var z );
			AddTriangle( x == b ? a : x, y == b ? a : y, z == b ? a : z, tMaterial[t], CornerOf( t, 0 ), CornerOf( t, 1 ), CornerOf( t, 2 ) );

			renormal.Add( x == b ? a : x ); renormal.Add( y == b ? a : y ); renormal.Add( z == b ? a : z );
		}

		foreach ( var t in ringB ) RemoveTriangle( t );
		foreach ( var t in ring ) RemoveTriangle( t );

		if ( vAlive[a] )
		{
			SetVertex( a, target );
			renormal.Add( a );
		}

		return true;
	}

	//Whether any triangle at a vertex wears a material of its own.
	private bool WearsAnything( int v )
	{
		for ( var c = vCorner[v]; c >= 0; c = cNext[c] )
			if ( tMaterial[c / 3] != 0 ) return true;

		return false;
	}

	//Whether the open edge through a vertex carries straight on past it, coming from another vertex.
	private bool RunsStraight( int v, int from )
	{
		Neighbours( v, around );

		var onward = -1;
		var open = 0;

		foreach ( var n in around )
		{
			if ( EdgeUses( v, n ) != 1 ) continue;

			open++;
			if ( n != from ) onward = n;
		}

		if ( open != 2 || onward < 0 ) return false;

		var arriving = ( vPosition[v] - vPosition[from] ).Normal;
		var leaving = ( vPosition[onward] - vPosition[v] ).Normal;

		return Vector3.Dot( arriving, leaving ) > 0.966f;
	}

	//storage

	private const uint Magic = 0x534D5452; //"RTMS"
	private const int Version = 4;   //4: four layers by number a vertex, and the mask after the triangles

	/// <summary>The mesh as bytes, with the dead slots dropped - so what is read back has other ids, and an edit made before does not belong to it.</summary>
	public void Write( BinaryWriter into )
	{
		into.Write( Magic );
		into.Write( Version );

		into.Write( gridMin.x ); into.Write( gridMin.y );
		into.Write( gridMin.x + ( gridWide * gridCell ) ); into.Write( gridMin.y + ( gridDeep * gridCell ) );
		into.Write( gridCell );

		into.Write( vertexCount );
		into.Write( triangleCount );

		var renamed = new int[vertexSlots];
		var next = 0;

		for ( var v = 0; v < vertexSlots; v++ )
		{
			if ( !vAlive[v] ) { renamed[v] = -1; continue; }

			renamed[v] = next++;

			into.Write( vPosition[v].x ); into.Write( vPosition[v].y ); into.Write( vPosition[v].z );
			into.Write( vNormal[v].x ); into.Write( vNormal[v].y ); into.Write( vNormal[v].z );
			into.Write( vPaint[v].Layers ); into.Write( vPaint[v].Weights );
		}

		for ( var t = 0; t < triangleSlots; t++ )
		{
			if ( !tAlive[t] ) continue;

			into.Write( renamed[tVertex[t * 3]] );
			into.Write( renamed[tVertex[( t * 3 ) + 1]] );
			into.Write( renamed[tVertex[( t * 3 ) + 2]] );
			into.Write( tMaterial[t] );

			//The ground's own triangles have nothing to say for their corners - nearly all of any terrain - and say nothing.
			if ( tMaterial[t] == 0 ) continue;

			for ( var k = 0; k < 3; k++ )
			{
				var c = ( t * 3 ) + k;
				into.Write( cUv[c].x ); into.Write( cUv[c].y );
				into.Write( cTangent[c].x ); into.Write( cTangent[c].y ); into.Write( cTangent[c].z ); into.Write( cTangent[c].w );
			}
		}

		WriteMask( into );
	}

	/// <summary>A mesh from what <see cref="Write"/> wrote, or null for anything else.</summary>
	public static TerrainMesh Read( BinaryReader from )
	{
		if ( from.ReadUInt32() != Magic ) return null;

		//One: the coordinates on the vertex, before they were on the corners.
		var version = from.ReadInt32();
		if ( version is not ( 1 or 2 or 3 or Version ) ) return null;

		var min = new Vector2( from.ReadSingle(), from.ReadSingle() );
		var max = new Vector2( from.ReadSingle(), from.ReadSingle() );
		var cell = from.ReadSingle();

		var vertices = from.ReadInt32();
		var triangles = from.ReadInt32();

		if ( vertices < 0 || triangles < 0 ) return null;

		var mesh = new TerrainMesh( min, max, cell );
		mesh.Reserve( vertices, triangles );

		var oldCorners = version == 1 ? new Corner[vertices] : null;

		for ( var v = 0; v < vertices; v++ )
		{
			var value = new Vertex
			{
				Position = new Vector3( from.ReadSingle(), from.ReadSingle(), from.ReadSingle() ),
				Normal = new Vector3( from.ReadSingle(), from.ReadSingle(), from.ReadSingle() ),
			};

			//Before four: eight weights, one for each of the first eight layers.
			if ( version >= 4 )
			{
				value.Paint = new TerrainPaint { Layers = from.ReadUInt32(), Weights = from.ReadUInt32() };
			}
			else
			{
				var first = new Color32( from.ReadByte(), from.ReadByte(), from.ReadByte(), from.ReadByte() );
				var last = version >= 3
					? new Vector4( from.ReadSingle(), from.ReadSingle(), from.ReadSingle(), from.ReadSingle() )
					: new Vector4( from.ReadSingle(), from.ReadSingle(), 0.0f, 0.0f );

				value.Paint = TerrainPaint.FromEight( first, last );
			}

			if ( oldCorners is not null )
			{
				oldCorners[v] = new Corner
				{
					Uv = new Vector2( from.ReadSingle(), from.ReadSingle() ),
					Tangent = new Vector4( from.ReadSingle(), from.ReadSingle(), from.ReadSingle(), from.ReadSingle() ),
				};
			}

			mesh.RawAddVertexAt( v, value );
		}

		for ( var t = 0; t < triangles; t++ )
		{
			var a = from.ReadInt32();
			var b = from.ReadInt32();
			var c = from.ReadInt32();
			var material = from.ReadUInt16();

			if ( (uint)a >= (uint)vertices || (uint)b >= (uint)vertices || (uint)c >= (uint)vertices ) return null;

			Corner ca = default, cb = default, cc = default;

			if ( oldCorners is not null )
			{
				ca = oldCorners[a]; cb = oldCorners[b]; cc = oldCorners[c];
			}
			else if ( material != 0 )
			{
				ca = ReadCorner( from ); cb = ReadCorner( from ); cc = ReadCorner( from );
			}

			mesh.RawAddTriangleAt( t, a, b, c, material, ca, cb, cc );
		}

		if ( version >= 4 && !mesh.ReadMask( from ) ) return null;

		mesh.anyTouched = false;
		mesh.longestEdgeGrew = false;

		return mesh;
	}

	private static Corner ReadCorner( BinaryReader from ) => new()
	{
		Uv = new Vector2( from.ReadSingle(), from.ReadSingle() ),
		Tangent = new Vector4( from.ReadSingle(), from.ReadSingle(), from.ReadSingle(), from.ReadSingle() ),
	};

	/// <summary>Builds a mesh from a legacy height map; vertices at texel centres, holed cells skipped</summary>
	public static TerrainMesh FromHeights( ushort[] heights, uint[] control, int side, float size, float height )
	{
		var unit = size / Math.Max( 1, side );
		var mesh = new TerrainMesh( Vector2.Zero, new Vector2( size, size ) );

		if ( heights is null || side < 2 || heights.Length < side * side ) return mesh;

		mesh.Reserve( side * side, ( side - 1 ) * ( side - 1 ) * 2 );

		var toMetres = height / 65535.0f;

		float At( int x, int z ) => heights[( Math.Clamp( z, 0, side - 1 ) * side ) + Math.Clamp( x, 0, side - 1 )] * toMetres;

		var ids = new int[side * side];
		var holes = new bool[side * side];

		Span<float> weights = stackalloc float[Terrain.MostLayers];

		for ( var z = 0; z < side; z++ )
		{
			for ( var x = 0; x < side; x++ )
			{
				var at = ( z * side ) + x;
				var packed = control is not null && control.Length > at ? control[at] : 0u;

				holes[at] = ( ( packed >> 18 ) & 1u ) != 0u;

				var under = (int)Math.Min( packed & 0x1Fu, (uint)Terrain.MostLayers - 1 );
				var over = (int)Math.Min( ( packed >> 5 ) & 0x1Fu, (uint)Terrain.MostLayers - 1 );
				var blend = ( ( packed >> 10 ) & 0xFFu ) / 255.0f;

				weights.Clear();
				weights[under] += 1.0f - blend;
				weights[over] += blend;

				var dx = ( At( x + 1, z ) - At( x - 1, z ) ) / ( unit * 2.0f );
				var dz = ( At( x, z + 1 ) - At( x, z - 1 ) ) / ( unit * 2.0f );

				ids[at] = mesh.AddVertex( new Vertex
				{
					Position = new Vector3( ( x + 0.5f ) * unit, At( x, z ), ( z + 0.5f ) * unit ),
					Normal = new Vector3( -dx, 1.0f, -dz ).Normal,
					Paint = TerrainPaint.Strongest( weights ),
				} );
			}
		}

		for ( var z = 0; z + 1 < side; z++ )
		{
			for ( var x = 0; x + 1 < side; x++ )
			{
				var a = ( z * side ) + x;
				var b = a + 1;
				var c = a + side + 1;
				var d = a + side;

				if ( holes[a] || holes[b] || holes[c] || holes[d] ) continue;

				//Counter-clockwise seen from above, which with Y up is x then z backwards.
				mesh.AddTriangle( ids[a], ids[c], ids[b], 0 );
				mesh.AddTriangle( ids[c], ids[a], ids[d], 0 );
			}
		}

		//A vertex that only a hole stood on belongs to nothing.
		for ( var v = 0; v < mesh.vertexSlots; v++ )
			if ( mesh.vAlive[v] && mesh.vCorner[v] < 0 ) mesh.RawRemoveVertex( v );

		mesh.anyTouched = false;
		mesh.longestEdgeGrew = false;

		return mesh;
	}

	//the mesh checked against itself

	//What is wrong with the mesh, or null. Walks all of it; for a test, not for a frame.
	internal string Validate()
	{
		var alive = 0;
		//Keyed by the pair itself rather than packed into a long: a long's hash is its two halves exclusive-or'd,
		//and neighbouring vertices' numbers are near each other, so a whole lattice lands on a few dozen buckets.
		var directed = new HashSet<(int, int)>();
		var triples = new HashSet<(int, int, int)>();

		for ( var t = 0; t < triangleSlots; t++ )
		{
			if ( !tAlive[t] ) continue;

			alive++;

			GetTriangle( t, out var a, out var b, out var c );

			if ( a == b || b == c || a == c ) return $"triangle {t} names a vertex twice";
			if ( !IsVertexAlive( a ) || !IsVertexAlive( b ) || !IsVertexAlive( c ) ) return $"triangle {t} names a dead vertex";

			var low = Math.Min( a, Math.Min( b, c ) );
			var high = Math.Max( a, Math.Max( b, c ) );

			if ( !triples.Add( ( low, a + b + c - low - high, high ) ) ) return $"triangle {t} is there twice";

			for ( var k = 0; k < 3; k++ )
			{
				var from = tVertex[( t * 3 ) + k];
				var to = tVertex[( t * 3 ) + ( ( k + 1 ) % 3 )];

				if ( !directed.Add( ( from, to ) ) ) return $"edge {from}->{to} is run along twice the same way (triangle {t})";

				//And the corner is in its vertex's list, once.
				var found = 0;
				var walked = 0;
				for ( var corner = vCorner[from]; corner >= 0; corner = cNext[corner] )
				{
					if ( corner == ( t * 3 ) + k ) found++;
					if ( ++walked > 100000 ) return $"vertex {from}'s corner list does not end";
				}

				if ( found != 1 ) return $"corner {k} of triangle {t} is in its vertex's list {found} times";
			}
		}

		if ( alive != triangleCount ) return $"{alive} triangles alive, {triangleCount} counted";

		//An edge with three triangles cannot have them all run opposite ways, so the check above has already
		//refused it. What is left: every corner in a list belongs to a live triangle that names that vertex.
		var vertices = 0;

		for ( var v = 0; v < vertexSlots; v++ )
		{
			if ( !vAlive[v] ) continue;

			vertices++;

			var steps = 0;

			for ( var c = vCorner[v]; c >= 0; c = cNext[c] )
			{
				if ( !tAlive[c / 3] ) return $"vertex {v} lists a corner of dead triangle {c / 3}";
				if ( tVertex[c] != v ) return $"vertex {v} lists a corner that names {tVertex[c]}";
				if ( ++steps > 100000 ) return $"vertex {v}'s corner list does not end";
			}

			if ( steps == 0 ) return $"vertex {v} is alive with no triangle";
			if ( vCell[v] != CellOf( vPosition[v] ) ) return $"vertex {v} is in the wrong square of the grid";
		}

		if ( vertices != vertexCount ) return $"{vertices} vertices alive, {vertexCount} counted";

		var inGrid = 0;

		for ( var cell = 0; cell < gridFirst.Length; cell++ )
		{
			var previous = -1;

			for ( var v = gridFirst[cell]; v >= 0; v = vCellNext[v] )
			{
				if ( !vAlive[v] ) return $"the grid holds dead vertex {v}";
				if ( vCell[v] != cell ) return $"vertex {v} is in square {cell} and thinks it is in {vCell[v]}";
				if ( vCellPrev[v] != previous ) return $"vertex {v}'s place in its square's list is broken";

				previous = v;

				if ( ++inGrid > vertexCount ) return "the grid holds more than there is";
			}
		}

		if ( inGrid != vertexCount ) return $"the grid holds {inGrid} of {vertexCount} vertices";

		return null;
	}
}

/// <summary>Undo journal of primitive mesh changes for one edit, replayed backwards to restore ids too</summary>
public sealed class TerrainMeshEdit
{
	internal enum Kind : byte { SetVertex, AddVertex, RemoveVertex, AddTriangle, RemoveTriangle, SetTexel, AddTile, RemoveTile }

	internal struct Op
	{
		public Kind Kind;
		public int Id;

		/// <summary>Where its vertex or triangle is in the lists below. A set vertex has two: before, then after.</summary>
		public int Data;
	}

	internal struct Triangle
	{
		public int A, B, C;
		public ushort Material;
		public TerrainMesh.Corner CA, CB, CC;
	}

	internal readonly List<Op> Ops = new();
	internal readonly List<TerrainMesh.Vertex> Vertices = new();
	internal readonly List<Triangle> Triangles = new();

	//A point of the mask: its tile, and what it was and came to; the op's id is where in the tile it is.
	internal struct Texel
	{
		public long Key;
		public TerrainPaint Before, After;
	}

	//A tile made or taken away, with its paint as it was made or as it was when it went.
	internal struct Tile
	{
		public long Key;
		public TerrainPaint[] Paint;
	}

	internal readonly List<Texel> Texels = new();
	internal readonly List<Tile> Tiles = new();

	//Which op last set each point of the mask, as for the vertices.
	private Dictionary<(long, int), int> lastTexel = new();

	//Which op last set each vertex, while the edit is open - so the next set of it lands there too.
	private Dictionary<int, int> lastSet = new();

	private bool any;

	/// <summary>Everywhere the edit touched, before and after.</summary>
	public BBox Bounds { get; private set; }

	/// <summary>About how much memory it holds on to.</summary>
	public long Bytes => ( Ops.Count * 12L ) + ( Vertices.Count * 40L ) + ( Triangles.Count * 88L ) + ( Texels.Count * 24L )
		+ ( Tiles.Count * TerrainMaskTile.Side * TerrainMaskTile.Side * 8L );

	internal bool IsEmpty => Ops.Count == 0;

	internal void Grow( BBox box )
	{
		Bounds = any ? Bounds.AddBBox( box ) : box;
		any = true;
	}

	internal void Close()
	{
		lastSet = null;
		lastTexel = null;
	}

	internal void SetVertex( int v, in TerrainMesh.Vertex before, in TerrainMesh.Vertex after )
	{
		if ( lastSet.TryGetValue( v, out var at ) )
		{
			Vertices[Ops[at].Data + 1] = after;
			return;
		}

		lastSet[v] = Ops.Count;
		Ops.Add( new Op { Kind = Kind.SetVertex, Id = v, Data = Vertices.Count } );

		Vertices.Add( before );
		Vertices.Add( after );
	}

	internal void AddedVertex( int v, in TerrainMesh.Vertex value )
	{
		lastSet.Remove( v );
		Ops.Add( new Op { Kind = Kind.AddVertex, Id = v, Data = Vertices.Count } );
		Vertices.Add( value );
	}

	internal void RemovedVertex( int v, in TerrainMesh.Vertex value )
	{
		lastSet.Remove( v );
		Ops.Add( new Op { Kind = Kind.RemoveVertex, Id = v, Data = Vertices.Count } );
		Vertices.Add( value );
	}

	internal void AddedTriangle( int t, int a, int b, int c, ushort material,
		in TerrainMesh.Corner ca, in TerrainMesh.Corner cb, in TerrainMesh.Corner cc )
	{
		Ops.Add( new Op { Kind = Kind.AddTriangle, Id = t, Data = Triangles.Count } );
		Triangles.Add( new Triangle { A = a, B = b, C = c, Material = material, CA = ca, CB = cb, CC = cc } );
	}

	internal void RemovedTriangle( int t, int a, int b, int c, ushort material,
		in TerrainMesh.Corner ca, in TerrainMesh.Corner cb, in TerrainMesh.Corner cc )
	{
		Ops.Add( new Op { Kind = Kind.RemoveTriangle, Id = t, Data = Triangles.Count } );
		Triangles.Add( new Triangle { A = a, B = b, C = c, Material = material, CA = ca, CB = cb, CC = cc } );
	}

	internal void SetTexel( long key, int at, TerrainPaint before, TerrainPaint after )
	{
		if ( lastTexel.TryGetValue( (key, at), out var op ) )
		{
			var texel = Texels[Ops[op].Data];
			texel.After = after;
			Texels[Ops[op].Data] = texel;
			return;
		}

		lastTexel[(key, at)] = Ops.Count;
		Ops.Add( new Op { Kind = Kind.SetTexel, Id = at, Data = Texels.Count } );
		Texels.Add( new Texel { Key = key, Before = before, After = after } );
	}

	//A tile coming or going starts its points' history again.
	internal void AddedTile( long key, TerrainPaint[] paint )
	{
		lastTexel.Clear();
		Ops.Add( new Op { Kind = Kind.AddTile, Data = Tiles.Count } );
		Tiles.Add( new Tile { Key = key, Paint = paint } );
	}

	internal void RemovedTile( long key, TerrainPaint[] paint )
	{
		lastTexel.Clear();
		Ops.Add( new Op { Kind = Kind.RemoveTile, Data = Tiles.Count } );
		Tiles.Add( new Tile { Key = key, Paint = paint } );
	}
}
