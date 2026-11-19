namespace Rinten;

//Ground built from a height function rather than a grid: fine where it is asked to be and where the shape needs it,
//coarse everywhere else, and without cracks - a balanced quadtree whose leaves are fanned against finer neighbours
public sealed partial class TerrainMesh
{
	/// <summary>A square ground this many metres a side from a height function, its edges no longer than edge says and finer where the shape needs.</summary>
	//A cell splits where its corners miss the function by more than tolerance times its edge; minimum stops either. Paint picks each vertex's layers
	public static TerrainMesh FromField( float size, Func<float, float, float> height, Func<float, float, float> edge, float tolerance = 0.1f,
		float minimum = 0.5f, Func<Vector3, Vector3, TerrainPaint> paint = null )
	{
		size = MathF.Max( size, 1.0f );
		minimum = MathF.Max( minimum, 0.05f );

		//The finest level whose cells are no smaller than the minimum
		var deepest = 0;
		while ( deepest < 20 && size / ( 1 << ( deepest + 1 ) ) >= minimum ) deepest++;

		var leaves = new HashSet<(int Level, int X, int Z)>();
		var pending = new Stack<(int Level, int X, int Z)>();

		pending.Push( (0, 0, 0) );

		//Split where the edge length or the shape asks for it
		while ( pending.Count > 0 )
		{
			var cell = pending.Pop();

			if ( cell.Level < deepest && WantsSplit( cell, size, height, edge, tolerance ) )
			{
				foreach ( var child in Children( cell ) ) pending.Push( child );
				continue;
			}

			leaves.Add( cell );
		}

		Balance( leaves, deepest );

		return Triangulate( leaves, deepest, size, height, paint );
	}

	private static IEnumerable<(int Level, int X, int Z)> Children( (int Level, int X, int Z) cell )
	{
		var level = cell.Level + 1;
		var x = cell.X * 2;
		var z = cell.Z * 2;

		yield return (level, x, z);
		yield return (level, x + 1, z);
		yield return (level, x, z + 1);
		yield return (level, x + 1, z + 1);
	}

	private static bool WantsSplit( (int Level, int X, int Z) cell, float size, Func<float, float, float> height, Func<float, float, float> edge, float tolerance )
	{
		var side = size / ( 1 << cell.Level );
		var x0 = cell.X * side;
		var z0 = cell.Z * side;

		var centreX = x0 + side * 0.5f;
		var centreZ = z0 + side * 0.5f;

		var wanted = edge( centreX, centreZ );
		if ( side > wanted ) return true;

		//How far the two triangles over the corners miss the function at the middle and the middle of each side
		var h00 = height( x0, z0 );
		var h10 = height( x0 + side, z0 );
		var h01 = height( x0, z0 + side );
		var h11 = height( x0 + side, z0 + side );

		static float Miss( float sampled, float expected ) => MathF.Abs( sampled - expected );

		var worst = Miss( height( centreX, centreZ ), ( h00 + h10 + h01 + h11 ) * 0.25f );
		worst = MathF.Max( worst, Miss( height( centreX, z0 ), ( h00 + h10 ) * 0.5f ) );
		worst = MathF.Max( worst, Miss( height( centreX, z0 + side ), ( h01 + h11 ) * 0.5f ) );
		worst = MathF.Max( worst, Miss( height( x0, centreZ ), ( h00 + h01 ) * 0.5f ) );
		worst = MathF.Max( worst, Miss( height( x0 + side, centreZ ), ( h10 + h11 ) * 0.5f ) );

		//Where coarse edges are asked for, a coarse miss is too
		return worst > tolerance * wanted;
	}

	//The leaf over a cell's place, at its level or coarser; null where the place is finer than it or outside
	private static (int Level, int X, int Z)? Covering( HashSet<(int Level, int X, int Z)> leaves, int level, int x, int z )
	{
		if ( x < 0 || z < 0 || x >= ( 1 << level ) || z >= ( 1 << level ) ) return null;

		for ( var l = level; l >= 0; l-- )
		{
			var shift = level - l;
			var key = (l, x >> shift, z >> shift);

			if ( leaves.Contains( key ) ) return key;
		}

		return null;
	}

	//The four neighbours of a cell, as steps across the grid
	private static readonly (int X, int Z)[] Sides = [(-1, 0), (1, 0), (0, -1), (0, 1)];

	//No leaf beside one more than a level finer than itself, so every side of a leaf has at most its own middle to fan to
	private static void Balance( HashSet<(int Level, int X, int Z)> leaves, int deepest )
	{
		var queue = new Queue<(int Level, int X, int Z)>( leaves );

		while ( queue.Count > 0 )
		{
			var cell = queue.Dequeue();
			if ( !leaves.Contains( cell ) || cell.Level < 2 ) continue;

			foreach ( var (dx, dz) in Sides )
			{
				var neighbour = Covering( leaves, cell.Level, cell.X + dx, cell.Z + dz );
				if ( neighbour is not { } coarse || coarse.Level >= cell.Level - 1 ) continue;

				leaves.Remove( coarse );

				foreach ( var child in Children( coarse ) )
				{
					leaves.Add( child );
					queue.Enqueue( child );
				}

				queue.Enqueue( cell );
			}
		}
	}

	private static TerrainMesh Triangulate( HashSet<(int Level, int X, int Z)> leaves, int deepest, float size, Func<float, float, float> height,
		Func<Vector3, Vector3, TerrainPaint> paint )
	{
		var mesh = new TerrainMesh( Vector2.Zero, new Vector2( size, size ), MathF.Max( size / 2048.0f, 4.0f ) );
		mesh.Reserve( leaves.Count * 2, leaves.Count * 4 );

		var unit = size / ( 1 << deepest );
		var ids = new Dictionary<long, int>( leaves.Count * 2 );

		Span<float> first = stackalloc float[Terrain.MostLayers];
		first[0] = 1.0f;
		var plain = TerrainPaint.Strongest( first );

		//A vertex on the finest grid, made once however many leaves share it
		int Vertex( int gx, int gz )
		{
			var key = ( (long)gx << 32 ) | (uint)gz;
			if ( ids.TryGetValue( key, out var id ) ) return id;

			var x = gx * unit;
			var z = gz * unit;
			var y = height( x, z );

			//The slope over a step a little finer than the finest cell
			var step = MathF.Max( unit * 0.5f, 0.05f );
			var dx = ( height( x + step, z ) - height( x - step, z ) ) / ( step * 2.0f );
			var dz = ( height( x, z + step ) - height( x, z - step ) ) / ( step * 2.0f );

			var position = new Vector3( x, y, z );
			var normal = new Vector3( -dx, 1.0f, -dz ).Normal;

			id = mesh.AddVertex( new Vertex { Position = position, Normal = normal, Paint = paint?.Invoke( position, normal ) ?? plain } );
			ids[key] = id;

			return id;
		}

		var ring = new List<(int X, int Z)>( 8 );

		foreach ( var cell in leaves )
		{
			var span = 1 << ( deepest - cell.Level );
			var x0 = cell.X * span;
			var z0 = cell.Z * span;
			var x1 = x0 + span;
			var z1 = z0 + span;
			var half = span / 2;

			//A side has its middle where the leaf beside it is finer
			bool Finer( int dx, int dz ) => span > 1 && Covering( leaves, cell.Level, cell.X + dx, cell.Z + dz ) is null
				&& cell.X + dx >= 0 && cell.Z + dz >= 0 && cell.X + dx < ( 1 << cell.Level ) && cell.Z + dz < ( 1 << cell.Level );

			var south = Finer( 0, -1 );
			var east = Finer( 1, 0 );
			var north = Finer( 0, 1 );
			var west = Finer( -1, 0 );

			if ( !south && !east && !north && !west )
			{
				//Split along alternating diagonals, so no direction is favoured over a wide flat
				if ( ( ( cell.X + cell.Z ) & 1 ) == 0 )
				{
					Add( mesh, Vertex( x0, z0 ), Vertex( x1, z1 ), Vertex( x1, z0 ) );
					Add( mesh, Vertex( x1, z1 ), Vertex( x0, z0 ), Vertex( x0, z1 ) );
				}
				else
				{
					Add( mesh, Vertex( x1, z0 ), Vertex( x0, z1 ), Vertex( x0, z0 ) );
					Add( mesh, Vertex( x0, z1 ), Vertex( x1, z0 ), Vertex( x1, z1 ) );
				}

				continue;
			}

			ring.Clear();
			ring.Add( (x0, z0) );
			if ( south ) ring.Add( (x0 + half, z0) );
			ring.Add( (x1, z0) );
			if ( east ) ring.Add( (x1, z0 + half) );
			ring.Add( (x1, z1) );
			if ( north ) ring.Add( (x0 + half, z1) );
			ring.Add( (x0, z1) );
			if ( west ) ring.Add( (x0, z0 + half) );

			var centre = Vertex( x0 + half, z0 + half );

			for ( var i = 0; i < ring.Count; i++ )
			{
				var a = ring[i];
				var b = ring[( i + 1 ) % ring.Count];

				Add( mesh, centre, Vertex( a.X, a.Z ), Vertex( b.X, b.Z ) );
			}
		}

		mesh.anyTouched = false;
		mesh.longestEdgeGrew = false;

		return mesh;
	}

	//Wound the way the rest of the ground is: clockwise on the x, z plane, which is counter-clockwise seen from above with y up
	private static void Add( TerrainMesh mesh, int a, int b, int c )
	{
		var pa = mesh.vPosition[a];
		var pb = mesh.vPosition[b];
		var pc = mesh.vPosition[c];

		var turn = ( pb.x - pa.x ) * ( pc.z - pa.z ) - ( pb.z - pa.z ) * ( pc.x - pa.x );

		if ( turn > 0.0f ) mesh.AddTriangle( a, c, b, 0 );
		else mesh.AddTriangle( a, b, c, 0 );
	}
}
