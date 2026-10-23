using System;
using System.Collections.Generic;

namespace Rinten;

/// <summary>Triangles with everything a terrain vertex carries, as the union below takes them and gives them back: a list of vertices, three indices a triangle, and which material each triangle wears.</summary>
public sealed class TerrainSoup
{
	public struct Vertex
	{
		public Vector3 Position, Normal;

		/// <summary>Which painted layers the vertex wears, and how much of each.</summary>
		public TerrainPaint Paint;

		public Vector2 Uv;
		public Vector4 Tangent;
	}

	public readonly List<Vertex> Vertices = new();

	/// <summary>Three to a triangle, counter-clockwise seen from the front.</summary>
	public readonly List<int> Indices = new();

	/// <summary>One to a triangle.</summary>
	public readonly List<ushort> Materials = new();
}

/// <summary>Cuts a solid into the ground along their intersection curve, producing one welded mesh Pure and thread safe; inside/outside is flooded from the curve rather than ray-counted</summary>
public static class TerrainMeshUnion
{
	public sealed class Report
	{
		/// <summary>Triangles of each side that were cut along the curve and triangulated again.</summary>
		public int GroundCut, SolidCut;

		/// <summary>Triangles whose cut could not be made and were kept or dropped whole instead.</summary>
		public int Fallbacks;

		/// <summary>How many times the solid had to be nudged before the seam closed - see Union.</summary>
		public int Retries;

		/// <summary>Whether the seam is still open after every retry: the result is the best there was, with a gap in it.</summary>
		public bool Unclosed;

		//Which side each triangle written came from, in the order written. Not the material: a ground carries
		//materials of its own - what was set into it before, and a caller's marks - and a solid can wear the ground's.
		internal readonly List<bool> FromGround = new();

		/// <summary>Distinct points of the curve that are not a corner of any input triangle.</summary>
		public int CurveVertices;

		/// <summary>Whether what came out is anything but the ground as it went in.</summary>
		public bool Touched;
	}

	/// <summary>Told why a cut went the way it did, where anybody is listening - a test harness, nothing else.</summary>
	public static Action<string> Debug;

	/// <summary>The union of a ground and a solid set into it. Neither input is changed.</summary>
	public static Report Union( TerrainSoup ground, TerrainSoup solid, TerrainSoup into, bool carve = false )
	{
		var report = Once( ground, solid, into, carve );

		//If the seam didn't close, nudge the solid slightly and cut again
		if ( report.Touched && !carve && !Closed( ground, into, report ) )
		{
			var size = Size( solid );

			for ( var attempt = 1; attempt <= 3; attempt++ )
			{
				var nudge = new Vector3( 0.37f * attempt, 0.53f * attempt, 0.29f * attempt ) * ( size * 4e-5f );
				var moved = Nudged( solid, nudge );

				var again = Once( ground, moved, into, carve );
				again.Retries = attempt;

				if ( Closed( ground, into, again ) ) { Debug?.Invoke( $"retry {attempt} closed it" ); return again; }

				report = again;
			}

			report.Unclosed = true;
			Debug?.Invoke( "still open after every retry" );
		}

		return report;
	}

	private static Report Once( TerrainSoup ground, TerrainSoup solid, TerrainSoup into, bool carve )
	{
		into.Vertices.Clear();
		into.Indices.Clear();
		into.Materials.Clear();

		var report = new Report();
		new Job( ground, solid, into, carve, report ).Run();
		return report;
	}

	private static float Size( TerrainSoup soup )
	{
		if ( soup.Vertices.Count == 0 ) return 1.0f;

		var min = soup.Vertices[0].Position; var max = min;

		foreach ( var v in soup.Vertices )
		{
			min = new Vector3( MathF.Min( min.x, v.Position.x ), MathF.Min( min.y, v.Position.y ), MathF.Min( min.z, v.Position.z ) );
			max = new Vector3( MathF.Max( max.x, v.Position.x ), MathF.Max( max.y, v.Position.y ), MathF.Max( max.z, v.Position.z ) );
		}

		return MathF.Max( ( max - min ).Length, 1e-3f );
	}

	private static TerrainSoup Nudged( TerrainSoup soup, Vector3 by )
	{
		var moved = new TerrainSoup();

		foreach ( var v in soup.Vertices )
		{
			var copy = v;
			copy.Position += by;
			moved.Vertices.Add( copy );
		}

		moved.Indices.AddRange( soup.Indices );
		moved.Materials.AddRange( soup.Materials );

		return moved;
	}

	//Whether the seam closes: every edge that only one triangle of the result has - by where its ends are, since the two sides keep their own vertices -
	//is matched by one of the other side, except the ground's own outer edge. Which side a triangle is on is asked of the report, never of its material.
	private static bool Closed( TerrainSoup ground, TerrainSoup result, Report report )
	{
		var rim = new HashSet<(Vector3, Vector3)>();
		var uses = new Dictionary<(Vector3, Vector3), int>();

		for ( var t = 0; t + 2 < ground.Indices.Count; t += 3 )
			for ( var k = 0; k < 3; k++ )
			{
				var e = Edge( ground.Vertices[ground.Indices[t + k]].Position, ground.Vertices[ground.Indices[t + ( ( k + 1 ) % 3 )]].Position );
				uses[e] = uses.GetValueOrDefault( e ) + 1;
			}

		foreach ( var (e, n) in uses ) if ( n == 1 ) rim.Add( e );

		var groundOpen = new Dictionary<(Vector3, Vector3), int>();
		var solidOpen = new Dictionary<(Vector3, Vector3), int>();

		for ( var t = 0; t < result.Materials.Count; t++ )
		{
			var into = report.FromGround[t] ? groundOpen : solidOpen;

			for ( var k = 0; k < 3; k++ )
			{
				var e = Edge( result.Vertices[result.Indices[( t * 3 ) + k]].Position, result.Vertices[result.Indices[( t * 3 ) + ( ( k + 1 ) % 3 )]].Position );
				into[e] = into.GetValueOrDefault( e ) + 1;
			}
		}

		foreach ( var (e, n) in solidOpen )
			if ( n == 1 && groundOpen.GetValueOrDefault( e ) != 1 ) return false;

		foreach ( var (e, n) in groundOpen )
			if ( n == 1 && !rim.Contains( e ) && solidOpen.GetValueOrDefault( e ) != 1 ) return false;

		return true;

		static (Vector3, Vector3) Edge( Vector3 a, Vector3 b )
			=> a.x < b.x || ( a.x == b.x && ( a.y < b.y || ( a.y == b.y && a.z < b.z ) ) ) ? ( a, b ) : ( b, a );
	}

	//Arithmetic

	private readonly struct V3
	{
		public readonly double X, Y, Z;

		public V3( double x, double y, double z ) { X = x; Y = y; Z = z; }
		public V3( Vector3 v ) { X = v.x; Y = v.y; Z = v.z; }

		public static V3 operator +( V3 a, V3 b ) => new( a.X + b.X, a.Y + b.Y, a.Z + b.Z );
		public static V3 operator -( V3 a, V3 b ) => new( a.X - b.X, a.Y - b.Y, a.Z - b.Z );
		public static V3 operator *( V3 a, double s ) => new( a.X * s, a.Y * s, a.Z * s );

		public static double Dot( V3 a, V3 b ) => ( a.X * b.X ) + ( a.Y * b.Y ) + ( a.Z * b.Z );

		public static V3 Cross( V3 a, V3 b )
			=> new( ( a.Y * b.Z ) - ( a.Z * b.Y ), ( a.Z * b.X ) - ( a.X * b.Z ), ( a.X * b.Y ) - ( a.Y * b.X ) );

		public double LengthSquared => ( X * X ) + ( Y * Y ) + ( Z * Z );
		public double Length => Math.Sqrt( LengthSquared );

		public static V3 Min( V3 a, V3 b ) => new( Math.Min( a.X, b.X ), Math.Min( a.Y, b.Y ), Math.Min( a.Z, b.Z ) );
		public static V3 Max( V3 a, V3 b ) => new( Math.Max( a.X, b.X ), Math.Max( a.Y, b.Y ), Math.Max( a.Z, b.Z ) );

		public Vector3 ToVector() => new( (float)X, (float)Y, (float)Z );
	}

	//Order-independent edge key, multiplied by an odd constant so hash buckets spread well
	private static long Key( int a, int b )
		=> unchecked(( a < b ? ( (long)a << 32 ) | (uint)b : ( (long)b << 32 ) | (uint)a ) * (long)0x9E3779B97F4A7C15UL);

	//The weld: one number for one place

	//Every position that takes part, given a number - and the same number to anything that lands within the radius of one already there. No two numbers
	//are nearer each other than the radius, which is what the triangulation below leans on when it decides a point is on an edge and not at its end.
	private sealed class Welder
	{
		private readonly double cell, radiusSquared;
		private readonly Dictionary<long, int> heads = new();
		private readonly List<int> next = new();

		public readonly List<V3> Points = new();

		public Welder( double radius )
		{
			//Twice the radius to a cell, so a sphere of the radius touches two cells an axis at most: eight to look in, not twenty-seven.
			cell = radius * 2.0;
			radiusSquared = radius * radius;
		}

		private static long Hash( long x, long y, long z )
			=> unchecked(( x * (long)0x9E3779B97F4A7C15UL ) ^ ( y * (long)0xC2B2AE3D27D4EB4FUL ) ^ ( z * 0x165667B19E3779F9L ));

		public int Register( V3 p )
		{
			var fx = p.X / cell; var fy = p.Y / cell; var fz = p.Z / cell;
			var cx = (long)Math.Floor( fx ); var cy = (long)Math.Floor( fy ); var cz = (long)Math.Floor( fz );

			var sx = fx - cx < 0.5 ? -1 : 1;
			var sy = fy - cy < 0.5 ? -1 : 1;
			var sz = fz - cz < 0.5 ? -1 : 1;

			var found = -1;
			var nearest = double.MaxValue;

			for ( var k = 0; k < 8; k++ )
			{
				var key = Hash( cx + ( ( k & 1 ) != 0 ? sx : 0 ), cy + ( ( k & 2 ) != 0 ? sy : 0 ), cz + ( ( k & 4 ) != 0 ? sz : 0 ) );
				if ( !heads.TryGetValue( key, out var at ) ) continue;

				for ( ; at >= 0; at = next[at] )
				{
					var d = ( Points[at] - p ).LengthSquared;

					if ( d > radiusSquared || d > nearest || ( d == nearest && at > found ) ) continue;

					nearest = d;
					found = at;
				}
			}

			if ( found >= 0 ) return found;

			var own = Hash( cx, cy, cz );
			var head = heads.TryGetValue( own, out var first ) ? first : -1;

			Points.Add( p );
			next.Add( head );
			heads[own] = Points.Count - 1;

			return Points.Count - 1;
		}
	}

	//A box tree over triangles: which are near a box, which is nearest a point

	private sealed class Tree
	{
		private struct Node { public V3 Min, Max; public int Left, Right, Start, Count; }

		private readonly List<Node> nodes = new();
		private readonly int[] order;
		private readonly Side side;

		public bool Empty => order.Length == 0;

		public Tree( Side side, int[] triangles )
		{
			this.side = side;
			order = triangles;

			if ( order.Length == 0 ) return;

			var stack = new Stack<(int Node, int Start, int Count)>();
			nodes.Add( default );
			stack.Push( (0, 0, order.Length) );

			while ( stack.Count > 0 )
			{
				var (at, start, count) = stack.Pop();

				var min = new V3( double.MaxValue, double.MaxValue, double.MaxValue );
				var max = new V3( double.MinValue, double.MinValue, double.MinValue );
				var cmin = min; var cmax = max;

				for ( var i = start; i < start + count; i++ )
				{
					var t = order[i];
					min = V3.Min( min, side.BoxMin[t] ); max = V3.Max( max, side.BoxMax[t] );

					var c = ( side.BoxMin[t] + side.BoxMax[t] ) * 0.5;
					cmin = V3.Min( cmin, c ); cmax = V3.Max( cmax, c );
				}

				var node = new Node { Min = min, Max = max, Left = -1, Right = -1, Start = start, Count = count };

				if ( count > 4 )
				{
					var size = cmax - cmin;
					var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
					var middle = axis == 0 ? ( cmin.X + cmax.X ) * 0.5 : axis == 1 ? ( cmin.Y + cmax.Y ) * 0.5 : ( cmin.Z + cmax.Z ) * 0.5;

					var low = start; var high = start + count - 1;

					while ( low <= high )
					{
						var t = order[low];
						var c = axis == 0 ? side.BoxMin[t].X + side.BoxMax[t].X : axis == 1 ? side.BoxMin[t].Y + side.BoxMax[t].Y : side.BoxMin[t].Z + side.BoxMax[t].Z;

						if ( c * 0.5 < middle ) low++;
						else { ( order[low], order[high] ) = ( order[high], order[low] ); high--; }
					}

					var left = low - start;

					//All on one side - the same middle, a thousand times over. Halved by count instead; the order they are in is as good as any.
					if ( left == 0 || left == count ) left = count / 2;

					node.Left = nodes.Count; nodes.Add( default );
					node.Right = nodes.Count; nodes.Add( default );
					node.Count = 0;

					stack.Push( (node.Left, start, left) );
					stack.Push( (node.Right, start + left, count - left) );
				}

				nodes[at] = node;
			}
		}

		public void Overlapping( V3 min, V3 max, List<int> into )
		{
			into.Clear();
			if ( order.Length == 0 ) return;

			Span<int> stack = stackalloc int[64];
			var depth = 0;
			stack[depth++] = 0;

			while ( depth > 0 )
			{
				var node = nodes[stack[--depth]];

				if ( node.Min.X > max.X || node.Min.Y > max.Y || node.Min.Z > max.Z
					|| node.Max.X < min.X || node.Max.Y < min.Y || node.Max.Z < min.Z ) continue;

				if ( node.Left >= 0 )
				{
					stack[depth++] = node.Left;
					stack[depth++] = node.Right;
					continue;
				}

				for ( var i = node.Start; i < node.Start + node.Count; i++ )
				{
					var t = order[i];

					if ( side.BoxMin[t].X > max.X || side.BoxMin[t].Y > max.Y || side.BoxMin[t].Z > max.Z
						|| side.BoxMax[t].X < min.X || side.BoxMax[t].Y < min.Y || side.BoxMax[t].Z < min.Z ) continue;

					into.Add( t );
				}
			}

			//The tree's order is the order it happened to split in; the caller's should not be.
			into.Sort();
		}

		private static double BoxDistanceSquared( in Node node, V3 p )
		{
			var dx = Math.Max( Math.Max( node.Min.X - p.X, 0.0 ), p.X - node.Max.X );
			var dy = Math.Max( Math.Max( node.Min.Y - p.Y, 0.0 ), p.Y - node.Max.Y );
			var dz = Math.Max( Math.Max( node.Min.Z - p.Z, 0.0 ), p.Z - node.Max.Z );

			return ( dx * dx ) + ( dy * dy ) + ( dz * dz );
		}

		/// <summary>Nearest triangle, signed side and feature (0 face, 1-3 edge, 4-6 corner); ties pick most square-on</summary>
		public bool Nearest( V3 p, out int triangle, out double signed, out int feature )
		{
			triangle = -1; signed = 0.0; feature = 0;
			if ( order.Length == 0 ) return false;

			var best = double.MaxValue;
			var bestSquare = 0.0;

			Span<int> stack = stackalloc int[64];
			var depth = 0;
			stack[depth++] = 0;

			while ( depth > 0 )
			{
				var node = nodes[stack[--depth]];
				if ( BoxDistanceSquared( node, p ) > best * ( 1.0 + 1e-9 ) + 1e-30 ) continue;

				if ( node.Left >= 0 )
				{
					//The nearer child last, so it is looked in first.
					var dl = BoxDistanceSquared( nodes[node.Left], p );
					var dr = BoxDistanceSquared( nodes[node.Right], p );

					if ( dl < dr ) { stack[depth++] = node.Right; stack[depth++] = node.Left; }
					else { stack[depth++] = node.Left; stack[depth++] = node.Right; }
					continue;
				}

				for ( var i = node.Start; i < node.Start + node.Count; i++ )
				{
					var t = order[i];
					var c = Closest( p, side.A[t], side.B[t], side.C[t], out var part );
					var offset = p - c;
					var d = offset.LengthSquared;
					var facing = V3.Dot( side.Normal[t], offset );
					var square = Math.Abs( facing );

					var tied = Math.Abs( d - best ) <= ( best * 1e-9 ) + 1e-30;

					if ( d > best && !tied ) continue;
					if ( tied && ( square < bestSquare || ( square == bestSquare && t > triangle ) ) ) continue;

					if ( d < best ) best = d;
					bestSquare = square;
					triangle = t;
					signed = facing;
					feature = part;
				}
			}

			return triangle >= 0;
		}

		//The point of a triangle nearest a point - Ericson's, region by region.
		private static V3 Closest( V3 p, V3 a, V3 b, V3 c, out int part )
		{
			var ab = b - a; var ac = c - a; var ap = p - a;
			var d1 = V3.Dot( ab, ap ); var d2 = V3.Dot( ac, ap );
			if ( d1 <= 0.0 && d2 <= 0.0 ) { part = 4; return a; }

			var bp = p - b;
			var d3 = V3.Dot( ab, bp ); var d4 = V3.Dot( ac, bp );
			if ( d3 >= 0.0 && d4 <= d3 ) { part = 5; return b; }

			var vc = ( d1 * d4 ) - ( d3 * d2 );
			if ( vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0 ) { part = 1; return a + ( ab * ( d1 / ( d1 - d3 ) ) ); }

			var cp = p - c;
			var d5 = V3.Dot( ab, cp ); var d6 = V3.Dot( ac, cp );
			if ( d6 >= 0.0 && d5 <= d6 ) { part = 6; return c; }

			var vb = ( d5 * d2 ) - ( d1 * d6 );
			if ( vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0 ) { part = 3; return a + ( ac * ( d2 / ( d2 - d6 ) ) ); }

			var va = ( d3 * d6 ) - ( d5 * d4 );
			if ( va <= 0.0 && ( d4 - d3 ) >= 0.0 && ( d5 - d6 ) >= 0.0 )
			{
				part = 2;
				return b + ( ( c - b ) * ( ( d4 - d3 ) / ( ( d4 - d3 ) + ( d5 - d6 ) ) ) );
			}

			var denominator = 1.0 / ( va + vb + vc );
			part = 0;
			return a + ( ab * ( vb * denominator ) ) + ( ac * ( vc * denominator ) );
		}
	}

	//One of the two meshes, and what is known of each triangle of it

	//What the curve left on one triangle: the points that landed on it and the stretches of curve across it.
	private sealed class Cut
	{
		public readonly List<int> Points = new();
		public readonly List<int> SegA = new(), SegB = new(), SegPartner = new();

		//Once triangulated: every vertex in the triangle's own plane, the weld's number
		//for each, and the triangles between them. The first three are the corners.
		public double[] X, Y;
		public int[] Id;
		public int[] Triangles;

		public void AddPoint( int id, int c0, int c1, int c2 )
		{
			if ( id == c0 || id == c1 || id == c2 || Points.Contains( id ) ) return;
			Points.Add( id );
		}
	}

	private sealed class Side
	{
		public TerrainSoup Soup;
		public int Triangles;

		/// <summary>The weld's number for each vertex record, below nought where it takes no part.</summary>
		public int[] Weld;

		/// <summary>Per triangle: whether it takes part, its corners as the weld has them, its face, its box.</summary>
		public bool[] Live;
		public int[] W;
		public V3[] A, B, C, Normal, BoxMin, BoxMax;
		public Cut[] Cuts;

		public Tree Tree;

		/// <summary>Which live triangles meet at an edge, by the weld's numbers: a chain of corners through <see cref="EdgeNext"/>.</summary>
		public readonly Dictionary<long, int> EdgeHead = new();
		public int[] EdgeNext;

		/// <summary>Stretches of the curve, as they came out of the triangulation, and the triangles of the other mesh that made each.</summary>
		public readonly Dictionary<long, List<int>> Curve = new();

		//What comes out.
		public int[] OutOfRecord;
		public readonly Dictionary<(int, int, int), int> OutOfNew = new();

		public Cut CutOf( int triangle ) => Cuts[triangle] ??= new Cut();

		public int Uses( long key )
		{
			var count = 0;
			if ( !EdgeHead.TryGetValue( key, out var at ) ) return 0;
			for ( ; at >= 0; at = EdgeNext[at] ) count++;
			return count;
		}
	}

	//A triangle cut along the curve: a constrained triangulation in its plane

	//Small constrained Delaunay triangulation of one cut triangle; bounded loops, never throws
	private sealed class Triangulation
	{
		public readonly List<double> X = new(), Y = new();
		public readonly List<int> T = new();

		private readonly HashSet<long> held = new();
		private readonly List<(int, int)> pending = new();
		private readonly List<(double, int)> along = new();
		private double onEdge;
		public string Why = "";

		public void Begin( double bx, double cx, double cy, double onEdge )
		{
			X.Clear(); Y.Clear(); T.Clear(); held.Clear();
			X.Add( 0.0 ); Y.Add( 0.0 );
			X.Add( bx ); Y.Add( 0.0 );
			X.Add( cx ); Y.Add( cy );
			T.Add( 0 ); T.Add( 1 ); T.Add( 2 );
			this.onEdge = onEdge;
		}

		//The nearest point of the triangle the triangulation began as - corners 0, 1 and 2.
		private void ClampIntoFirst( ref double x, ref double y )
		{
			//Inside by all three edges: as it is.
			var bx = X[1]; var cx = X[2]; var cy = Y[2];

			var e0 = y;                                                        //above 0 -> 1, which lies on the x axis
			var e1 = ( ( cx - bx ) * y ) - ( cy * ( x - bx ) );                //left of 1 -> 2
			var e2 = ( ( 0.0 - cx ) * ( y - cy ) ) - ( ( 0.0 - cy ) * ( x - cx ) ); //left of 2 -> 0

			if ( e0 >= 0.0 && e1 >= 0.0 && e2 >= 0.0 ) return;

			//Otherwise the nearest point of the three edges.
			var px = x; var py = y;
			var best = double.MaxValue;

			NearestOnEdge( px, py, 0.0, 0.0, bx, 0.0, ref x, ref y, ref best );
			NearestOnEdge( px, py, bx, 0.0, cx, cy, ref x, ref y, ref best );
			NearestOnEdge( px, py, cx, cy, 0.0, 0.0, ref x, ref y, ref best );
		}

		private static void NearestOnEdge( double px, double py, double ax, double ay, double qx, double qy,
			ref double outX, ref double outY, ref double best )
		{
			var dx = qx - ax; var dy = qy - ay;
			var t = Math.Clamp( ( ( ( px - ax ) * dx ) + ( ( py - ay ) * dy ) ) / Math.Max( ( dx * dx ) + ( dy * dy ), 1e-300 ), 0.0, 1.0 );
			var nx = ax + ( dx * t ); var ny = ay + ( dy * t );
			var d = ( ( nx - px ) * ( nx - px ) ) + ( ( ny - py ) * ( ny - py ) );

			if ( d >= best ) return;

			best = d;
			outX = nx;
			outY = ny;
		}

		private double Orient( int a, int b, int c )
			=> ( ( X[b] - X[a] ) * ( Y[c] - Y[a] ) ) - ( ( Y[b] - Y[a] ) * ( X[c] - X[a] ) );

		//How far left of the line from a to b a point stands.
		private double Left( int a, int b, int p )
		{
			var length = Math.Sqrt( ( ( X[b] - X[a] ) * ( X[b] - X[a] ) ) + ( ( Y[b] - Y[a] ) * ( Y[b] - Y[a] ) ) );
			return length > 0.0 ? Orient( a, b, p ) / length : 0.0;
		}

		//The triangle that has a to b as an edge, in that direction, and which of its edges it is.
		private bool Find( int a, int b, out int triangle, out int edge )
		{
			for ( var t = 0; t < T.Count; t += 3 )
			{
				for ( var e = 0; e < 3; e++ )
				{
					if ( T[t + e] != a || T[t + ( ( e + 1 ) % 3 )] != b ) continue;
					triangle = t; edge = e;
					return true;
				}
			}

			triangle = -1; edge = -1;
			return false;
		}

		public bool Insert( double x, double y )
		{
			//Clamp into the first triangle, since rounding can put an on-edge point just outside it
			ClampIntoFirst( ref x, ref y );

			var v = X.Count;
			X.Add( x ); Y.Add( y );

			var best = -1; var bestEdge = 0; var bestLeast = double.MinValue;

			for ( var t = 0; t < T.Count; t += 3 )
			{
				var d0 = Left( T[t], T[t + 1], v );
				var d1 = Left( T[t + 1], T[t + 2], v );
				var d2 = Left( T[t + 2], T[t], v );

				var least = d0; var edge = 0;
				if ( d1 < least ) { least = d1; edge = 1; }
				if ( d2 < least ) { least = d2; edge = 2; }

				if ( least <= bestLeast ) continue;
				bestLeast = least; best = t; bestEdge = edge;
			}

			if ( best < 0 ) return false;

			pending.Clear();

			var a = T[best + bestEdge]; var b = T[best + ( ( bestEdge + 1 ) % 3 )]; var c = T[best + ( ( bestEdge + 2 ) % 3 )];

			if ( bestLeast > onEdge )
			{
				//Inside: three where there was one.
				T[best] = a; T[best + 1] = b; T[best + 2] = v;
				T.Add( b ); T.Add( c ); T.Add( v );
				T.Add( c ); T.Add( a ); T.Add( v );

				pending.Add( (a, b) ); pending.Add( (b, c) ); pending.Add( (c, a) );
			}
			else
			{
				//Point is on an edge: snap it exactly onto the edge, then split that edge on both sides
				var ex = X[b] - X[a];
				var ey = Y[b] - Y[a];
				var along = ( ( ( X[v] - X[a] ) * ex ) + ( ( Y[v] - Y[a] ) * ey ) ) / Math.Max( ( ex * ex ) + ( ey * ey ), 1e-300 );

				if ( along > 0.0 && along < 1.0 )
				{
					X[v] = X[a] + ( ex * along );
					Y[v] = Y[a] + ( ey * along );
				}

				if ( Orient( a, v, c ) <= 0.0 || Orient( v, b, c ) <= 0.0 ) { Why = $"edge split inverts: least={bestLeast:g4} o1={Orient( a, v, c ):g4} o2={Orient( v, b, c ):g4}"; return false; }

				var across = Find( b, a, out var other, out var otherEdge );
				var d = across ? T[other + ( ( otherEdge + 2 ) % 3 )] : -1;

				if ( across && ( Orient( b, v, d ) <= 0.0 || Orient( v, a, d ) <= 0.0 ) ) { Why = "edge split inverts across"; return false; }

				T[best] = a; T[best + 1] = v; T[best + 2] = c;
				T.Add( v ); T.Add( b ); T.Add( c );
				pending.Add( (c, a) ); pending.Add( (b, c) );

				if ( across )
				{
					T[other] = b; T[other + 1] = v; T[other + 2] = d;
					T.Add( v ); T.Add( a ); T.Add( d );
					pending.Add( (d, b) ); pending.Add( (a, d) );
				}
			}

			//Towards Delaunay's: an edge whose far corner is inside the circle through the near three is swapped for the other diagonal.
			var bound = ( T.Count * 4 ) + 64;

			while ( pending.Count > 0 && bound-- > 0 )
			{
				var (p, q) = pending[^1];
				pending.RemoveAt( pending.Count - 1 );

				if ( !Find( p, q, out var t, out var e ) ) continue;
				var apex = T[t + ( ( e + 2 ) % 3 )];
				if ( apex != v ) continue;

				if ( !Find( q, p, out var u, out var ue ) ) continue;
				var far = T[u + ( ( ue + 2 ) % 3 )];

				if ( !InCircle( p, q, v, far ) ) continue;
				if ( Orient( p, far, v ) <= 0.0 || Orient( far, q, v ) <= 0.0 ) continue;

				T[t] = p; T[t + 1] = far; T[t + 2] = v;
				T[u] = far; T[u + 1] = q; T[u + 2] = v;

				pending.Add( (p, far) ); pending.Add( (far, q) );
			}

			return true;
		}

		private bool InCircle( int a, int b, int c, int d )
		{
			var ax = X[a] - X[d]; var ay = Y[a] - Y[d];
			var bx = X[b] - X[d]; var by = Y[b] - Y[d];
			var cx = X[c] - X[d]; var cy = Y[c] - Y[d];

			var al = ( ax * ax ) + ( ay * ay ); var bl = ( bx * bx ) + ( by * by ); var cl = ( cx * cx ) + ( cy * cy );

			var det = ( ax * ( ( by * cl ) - ( bl * cy ) ) ) - ( ay * ( ( bx * cl ) - ( bl * cx ) ) ) + ( al * ( ( bx * cy ) - ( by * cx ) ) );
			var scale = ( al + bl + cl ) * ( Math.Abs( ax ) + Math.Abs( bx ) + Math.Abs( cx ) + Math.Abs( ay ) + Math.Abs( by ) + Math.Abs( cy ) );

			return det > scale * 1e-12;
		}

		/// <summary>Makes a stretch of the curve an edge - or several, where other points stand on it - and says which, two numbers a piece.</summary>
		public bool Constrain( int p, int q, double onLine, List<int> pieces )
		{
			if ( p == q ) return true;

			var dx = X[q] - X[p]; var dy = Y[q] - Y[p];
			var lengthSquared = ( dx * dx ) + ( dy * dy );
			if ( lengthSquared <= 0.0 ) return true;

			var length = Math.Sqrt( lengthSquared );

			along.Clear();

			for ( var v = 0; v < X.Count; v++ )
			{
				if ( v == p || v == q ) continue;

				var t = ( ( ( X[v] - X[p] ) * dx ) + ( ( Y[v] - Y[p] ) * dy ) ) / lengthSquared;
				if ( t <= 0.0 || t >= 1.0 ) continue;

				var off = Math.Abs( ( ( X[v] - X[p] ) * dy ) - ( ( Y[v] - Y[p] ) * dx ) ) / length;
				if ( off <= onLine ) along.Add( (t, v) );
			}

			along.Sort();

			var from = p;

			for ( var i = 0; i <= along.Count; i++ )
			{
				var to = i < along.Count ? along[i].Item2 : q;

				if ( !Force( from, to ) ) return false;

				pieces.Add( from ); pieces.Add( to );
				from = to;
			}

			return true;
		}

		private bool Crosses( int p, int q, int a, int b )
		{
			if ( a == p || a == q || b == p || b == q ) return false;

			var o1 = Orient( p, q, a ); var o2 = Orient( p, q, b );
			if ( !( ( o1 > 0.0 && o2 < 0.0 ) || ( o1 < 0.0 && o2 > 0.0 ) ) ) return false;

			var o3 = Orient( a, b, p ); var o4 = Orient( a, b, q );
			return ( o3 > 0.0 && o4 < 0.0 ) || ( o3 < 0.0 && o4 > 0.0 );
		}

		private bool Force( int p, int q )
		{
			if ( Find( p, q, out _, out _ ) || Find( q, p, out _, out _ ) )
			{
				held.Add( Key( p, q ) );
				return true;
			}

			pending.Clear();

			for ( var t = 0; t < T.Count; t += 3 )
			{
				for ( var e = 0; e < 3; e++ )
				{
					var a = T[t + e]; var b = T[t + ( ( e + 1 ) % 3 )];
					if ( a > b || !Crosses( p, q, a, b ) ) continue;

					//Two stretches of curve through each other, which the caller was to have given a point where they meet.
					if ( held.Contains( Key( a, b ) ) ) { Why = "crosses a held edge"; return false; }

					pending.Add( (a, b) );
				}
			}

			var bound = ( pending.Count * pending.Count * 4 ) + 64;
			var next = 0;

			while ( next < pending.Count && bound-- > 0 )
			{
				var (a, b) = pending[next++];

				if ( !Find( a, b, out var t, out var e ) || !Find( b, a, out var u, out var ue ) ) { Why = "lost an edge"; return false; }

				var c = T[t + ( ( e + 2 ) % 3 )];
				var d = T[u + ( ( ue + 2 ) % 3 )];

				//Only a quad that bulges everywhere can take the other diagonal; one that does not waits for a neighbour's flip to make it so.
				if ( Orient( a, d, c ) <= 0.0 || Orient( d, b, c ) <= 0.0 )
				{
					pending.Add( (a, b) );
					continue;
				}

				T[t] = a; T[t + 1] = d; T[t + 2] = c;
				T[u] = d; T[u + 1] = b; T[u + 2] = c;

				if ( Crosses( p, q, c, d ) ) pending.Add( (Math.Min( c, d ), Math.Max( c, d )) );
			}

			if ( !Find( p, q, out _, out _ ) && !Find( q, p, out _, out _ ) ) { Why = $"never became an edge ({pending.Count} waiting)"; return false; }

			held.Add( Key( p, q ) );
			return true;
		}
	}

	//The work

	private sealed class Job
	{
		private readonly TerrainSoup groundSoup, solidSoup, into;
		private readonly bool carve;
		private readonly Report report;

		private double eps;
		private Welder weld;
		private Side ground, solid;
		private int firstCurvePoint;

		private readonly List<int> near = new();
		private readonly List<int> pieces = new();
		private readonly Triangulation triangulation = new();

		public Job( TerrainSoup ground, TerrainSoup solid, TerrainSoup into, bool carve, Report report )
		{
			groundSoup = ground; solidSoup = solid; this.into = into; this.carve = carve; this.report = report;
		}

		public void Run()
		{
			//How big the solid is, which is what "near" is measured against.
			var min = new V3( double.MaxValue, double.MaxValue, double.MaxValue );
			var max = new V3( double.MinValue, double.MinValue, double.MinValue );

			foreach ( var index in solidSoup.Indices )
			{
				var p = new V3( solidSoup.Vertices[index].Position );
				min = V3.Min( min, p ); max = V3.Max( max, p );
			}

			if ( solidSoup.Indices.Count < 3 )
			{
				PassThrough();
				return;
			}

			var size = max - min;
			eps = Math.Max( Math.Max( size.X, Math.Max( size.Y, size.Z ) ) * 1e-5, 1e-9 );
			//Weld at the triangulation's on-edge distance, so points it treats as touching become one
			weld = new Welder( eps * 3.0 );

			var reach = new V3( eps * 4.0, eps * 4.0, eps * 4.0 );

			ground = Prepare( groundSoup, min - reach, max + reach, all: false );
			solid = Prepare( solidSoup, min - reach, max + reach, all: true );

			firstCurvePoint = weld.Points.Count;

			Build( ground );
			Build( solid );

			FindCurve();
			CrossCurve( ground, solid );
			CrossCurve( solid, ground );
			ShareEdgePoints( ground );
			ShareEdgePoints( solid );

			report.GroundCut = Triangulate( ground, solid );
			report.SolidCut = Triangulate( solid, ground );

			var used = new HashSet<int>();
			CountCurve( ground, used );
			CountCurve( solid, used );
			report.CurveVertices = used.Count;

			var droppedGround = Emit( ground, solid, isGround: true );
			var keptSolid = Emit( solid, ground, isGround: false );

			report.Touched = droppedGround > 0 || keptSolid > 0 || report.GroundCut > 0;
		}

		private void PassThrough()
		{
			into.Vertices.AddRange( groundSoup.Vertices );
			into.Indices.AddRange( groundSoup.Indices );
			into.Materials.AddRange( groundSoup.Materials );

			for ( var t = 0; t < groundSoup.Materials.Count; t++ ) report.FromGround.Add( true );
		}

		//Reads one mesh: which of its triangles take part - all of the solid, and the ground whose boxes touch the solid's - and
		//gives every corner of those the weld's number. The ground first, so where the two meshes share a place it is the ground's.
		private Side Prepare( TerrainSoup soup, V3 min, V3 max, bool all )
		{
			var count = soup.Indices.Count / 3;

			var side = new Side
			{
				Soup = soup,
				Triangles = count,
				Weld = new int[soup.Vertices.Count],
				Live = new bool[count],
				W = new int[count * 3],
				A = new V3[count], B = new V3[count], C = new V3[count],
				Normal = new V3[count], BoxMin = new V3[count], BoxMax = new V3[count],
				Cuts = new Cut[count],
				EdgeNext = new int[count * 3],
				OutOfRecord = new int[soup.Vertices.Count],
			};

			Array.Fill( side.Weld, -1 );
			Array.Fill( side.OutOfRecord, -1 );

			for ( var t = 0; t < count; t++ )
			{
				var a = new V3( soup.Vertices[soup.Indices[t * 3]].Position );
				var b = new V3( soup.Vertices[soup.Indices[( t * 3 ) + 1]].Position );
				var c = new V3( soup.Vertices[soup.Indices[( t * 3 ) + 2]].Position );

				var lo = V3.Min( a, V3.Min( b, c ) ); var hi = V3.Max( a, V3.Max( b, c ) );

				if ( !all && ( lo.X > max.X || lo.Y > max.Y || lo.Z > max.Z || hi.X < min.X || hi.Y < min.Y || hi.Z < min.Z ) ) continue;

				side.Live[t] = true;
			}

			//Numbered in the order of the records rather than of the triangles, so the numbers do not depend on how the triangles happen to be listed.
			var wanted = new bool[soup.Vertices.Count];

			for ( var t = 0; t < count; t++ )
			{
				if ( !side.Live[t] ) continue;
				for ( var k = 0; k < 3; k++ ) wanted[soup.Indices[( t * 3 ) + k]] = true;
			}

			for ( var r = 0; r < wanted.Length; r++ )
				if ( wanted[r] ) side.Weld[r] = weld.Register( new V3( soup.Vertices[r].Position ) );

			return side;
		}

		//The faces, boxes, edges and tree of what takes part, from the places the weld settled on.
		private void Build( Side side )
		{
			var live = new List<int>();

			for ( var t = 0; t < side.Triangles; t++ )
			{
				if ( !side.Live[t] ) continue;

				var w0 = side.Weld[side.Soup.Indices[t * 3]];
				var w1 = side.Weld[side.Soup.Indices[( t * 3 ) + 1]];
				var w2 = side.Weld[side.Soup.Indices[( t * 3 ) + 2]];

				var a = weld.Points[w0]; var b = weld.Points[w1]; var c = weld.Points[w2];
				var normal = V3.Cross( b - a, c - a );
				var length = normal.Length;

				//Nothing to cut and nothing to keep: a triangle the weld closed up, or one that never had any area.
				if ( w0 == w1 || w1 == w2 || w2 == w0 || length <= eps * eps * 0.01 )
				{
					side.Live[t] = false;
					side.W[t * 3] = -1;
					continue;
				}

				side.W[t * 3] = w0; side.W[( t * 3 ) + 1] = w1; side.W[( t * 3 ) + 2] = w2;
				side.A[t] = a; side.B[t] = b; side.C[t] = c;
				side.Normal[t] = normal * ( 1.0 / length );

				var pad = new V3( eps, eps, eps );
				side.BoxMin[t] = V3.Min( a, V3.Min( b, c ) ) - pad;
				side.BoxMax[t] = V3.Max( a, V3.Max( b, c ) ) + pad;

				for ( var k = 0; k < 3; k++ )
				{
					var key = Key( side.W[( t * 3 ) + k], side.W[( t * 3 ) + ( ( k + 1 ) % 3 )] );

					side.EdgeNext[( t * 3 ) + k] = side.EdgeHead.TryGetValue( key, out var head ) ? head : -1;
					side.EdgeHead[key] = ( t * 3 ) + k;
				}

				live.Add( t );
			}

			side.Tree = new Tree( side, live.ToArray() );
		}

		//-- the curve ---------------------------------------------------------

		private void FindCurve()
		{
			for ( var s = 0; s < solid.Triangles; s++ )
			{
				if ( !solid.Live[s] ) continue;

				ground.Tree.Overlapping( solid.BoxMin[s], solid.BoxMax[s], near );

				foreach ( var g in near ) Meet( g, s );
			}
		}

		private readonly int[] met = new int[16];
		private int metCount;

		private void Met( int id )
		{
			for ( var i = 0; i < metCount; i++ ) if ( met[i] == id ) return;
			if ( metCount < met.Length ) met[metCount++] = id;
		}

		//Curve segment where two triangles meet, from each edge crossing the other's face
		private void Meet( int g, int s )
		{
			Span<double> dg = stackalloc double[3];
			Span<double> ds = stackalloc double[3];

			if ( !Sides( ground, g, solid, s, dg ) || !Sides( solid, s, ground, g, ds ) ) return;

			metCount = 0;

			Pierce( ground, g, dg, solid, s );
			Pierce( solid, s, ds, ground, g );

			if ( metCount == 0 ) return;

			var gc = ground.CutOf( g ); var sc = solid.CutOf( s );
			var g0 = ground.W[g * 3]; var g1 = ground.W[( g * 3 ) + 1]; var g2 = ground.W[( g * 3 ) + 2];
			var s0 = solid.W[s * 3]; var s1 = solid.W[( s * 3 ) + 1]; var s2 = solid.W[( s * 3 ) + 2];

			for ( var i = 0; i < metCount; i++ )
			{
				gc.AddPoint( met[i], g0, g1, g2 );
				sc.AddPoint( met[i], s0, s1, s2 );
			}

			if ( metCount < 2 ) return;

			//The two furthest apart along the line the planes share; anything between them stands on the stretch and splits it later.
			var line = V3.Cross( ground.Normal[g], solid.Normal[s] );
			var low = 0; var high = 0;
			var least = double.MaxValue; var most = double.MinValue;

			for ( var i = 0; i < metCount; i++ )
			{
				var t = V3.Dot( weld.Points[met[i]], line );
				if ( t < least ) { least = t; low = i; }
				if ( t > most ) { most = t; high = i; }
			}

			if ( low == high ) return;

			gc.SegA.Add( met[low] ); gc.SegB.Add( met[high] ); gc.SegPartner.Add( s );
			sc.SegA.Add( met[low] ); sc.SegB.Add( met[high] ); sc.SegPartner.Add( g );
		}

		//How far each corner of one triangle stands from the other's plane - nought within
		//the weld - and whether the triangle reaches the plane at all without lying in it.
		private bool Sides( Side of, int t, Side against, int u, Span<double> d )
		{
			var n = against.Normal[u]; var origin = against.A[u];

			d[0] = V3.Dot( n, of.A[t] - origin );
			d[1] = V3.Dot( n, of.B[t] - origin );
			d[2] = V3.Dot( n, of.C[t] - origin );

			for ( var k = 0; k < 3; k++ ) if ( Math.Abs( d[k] ) < eps ) d[k] = 0.0;

			if ( d[0] > 0.0 && d[1] > 0.0 && d[2] > 0.0 ) return false;
			if ( d[0] < 0.0 && d[1] < 0.0 && d[2] < 0.0 ) return false;

			//In the plane: two sheets lying on each other have no curve between them, and which of them is kept is decided by what is around them.
			return !( d[0] == 0.0 && d[1] == 0.0 && d[2] == 0.0 );
		}

		private void Pierce( Side of, int t, Span<double> d, Side through, int u )
		{
			for ( var k = 0; k < 3; k++ )
			{
				var j = ( k + 1 ) % 3;

				if ( d[k] == 0.0 )
				{
					if ( Within( through, u, Corner( of, t, k ) ) ) Met( of.W[( t * 3 ) + k] );
					continue;
				}

				if ( d[j] == 0.0 || ( d[k] > 0.0 ) == ( d[j] > 0.0 ) ) continue;

				//From the corner with the lower number, whichever triangle asks, so the two triangles that share the edge do the same sum.
				var first = of.W[( t * 3 ) + k] < of.W[( t * 3 ) + j] ? k : j;
				var second = first == k ? j : k;

				var from = Corner( of, t, first ); var to = Corner( of, t, second );
				var point = from + ( ( to - from ) * ( d[first] / ( d[first] - d[second] ) ) );

				if ( Within( through, u, point ) ) Met( weld.Register( point ) );
			}
		}

		private static V3 Corner( Side side, int t, int k ) => k == 0 ? side.A[t] : k == 1 ? side.B[t] : side.C[t];

		//Whether a point of a triangle's plane is within the triangle, give or take the weld
		//- which errs towards a point found twice rather than a curve with a gap in it.
		private bool Within( Side side, int t, V3 p )
		{
			var n = side.Normal[t];

			for ( var k = 0; k < 3; k++ )
			{
				var a = Corner( side, t, k ); var b = Corner( side, t, ( k + 1 ) % 3 );
				var edge = b - a;

				if ( V3.Dot( V3.Cross( edge, p - a ), n ) < -eps * edge.Length ) return false;
			}

			return true;
		}

		//The triangle's own plane: a corner for the origin, an edge for x.
		private void Plane( Side side, int t, out V3 origin, out V3 u, out V3 v )
		{
			origin = side.A[t];
			var edge = side.B[t] - origin;
			u = edge * ( 1.0 / edge.Length );
			v = V3.Cross( side.Normal[t], u );
		}

		//Where curve segments cross on one triangle, add a point and cut both source segments there
		private void CrossCurve( Side side, Side other )
		{
			for ( var t = 0; t < side.Triangles; t++ )
			{
				var cut = side.Cuts[t];
				if ( cut is null || cut.SegA.Count < 2 ) continue;

				Plane( side, t, out var origin, out var u, out var v );

				var count = cut.SegA.Count;

				for ( var i = 0; i < count; i++ )
				{
					for ( var j = i + 1; j < count; j++ )
					{
						int p = cut.SegA[i], q = cut.SegB[i], a = cut.SegA[j], b = cut.SegB[j];
						if ( p == a || p == b || q == a || q == b ) continue;

						var P = weld.Points[p] - origin; var Q = weld.Points[q] - origin;
						var A = weld.Points[a] - origin; var B = weld.Points[b] - origin;

						double px = V3.Dot( P, u ), py = V3.Dot( P, v ), qx = V3.Dot( Q, u ), qy = V3.Dot( Q, v );
						double ax = V3.Dot( A, u ), ay = V3.Dot( A, v ), bx = V3.Dot( B, u ), by = V3.Dot( B, v );

						var pq = Math.Sqrt( ( ( qx - px ) * ( qx - px ) ) + ( ( qy - py ) * ( qy - py ) ) );
						var ab = Math.Sqrt( ( ( bx - ax ) * ( bx - ax ) ) + ( ( by - ay ) * ( by - ay ) ) );
						if ( pq <= 0.0 || ab <= 0.0 ) continue;

						var da = ( ( ( qx - px ) * ( ay - py ) ) - ( ( qy - py ) * ( ax - px ) ) ) / pq;
						var db = ( ( ( qx - px ) * ( by - py ) ) - ( ( qy - py ) * ( bx - px ) ) ) / pq;
						var dp = ( ( ( bx - ax ) * ( py - ay ) ) - ( ( by - ay ) * ( px - ax ) ) ) / ab;
						var dq = ( ( ( bx - ax ) * ( qy - ay ) ) - ( ( by - ay ) * ( qx - ax ) ) ) / ab;

						//An end standing on the other stretch is not a crossing: it splits that stretch when the triangle is triangulated.
						var clear = eps * 2.0;
						if ( Math.Abs( da ) <= clear || Math.Abs( db ) <= clear || Math.Abs( dp ) <= clear || Math.Abs( dq ) <= clear ) continue;
						if ( ( da > 0.0 ) == ( db > 0.0 ) || ( dp > 0.0 ) == ( dq > 0.0 ) ) continue;

						var at = weld.Points[p] + ( ( weld.Points[q] - weld.Points[p] ) * ( dp / ( dp - dq ) ) );
						var id = weld.Register( at );

						cut.AddPoint( id, side.W[t * 3], side.W[( t * 3 ) + 1], side.W[( t * 3 ) + 2] );

						foreach ( var partner in new[] { cut.SegPartner[i], cut.SegPartner[j] } )
							other.CutOf( partner ).AddPoint( id, other.W[partner * 3], other.W[( partner * 3 ) + 1], other.W[( partner * 3 ) + 2] );
					}
				}
			}
		}

		//A point of the curve standing on an edge is given to every triangle that has the edge. The triangle across it nearly always found the point for
		//itself; where it did not - it met the other mesh at that point and nowhere else - it would keep a whole edge beside a split one, and that is a crack.
		private void ShareEdgePoints( Side side )
		{
			var onEdge = eps * 3.0;

			for ( var t = 0; t < side.Triangles; t++ )
			{
				var cut = side.Cuts[t];
				if ( cut is null ) continue;

				for ( var i = 0; i < cut.Points.Count; i++ )
				{
					var id = cut.Points[i];
					var p = weld.Points[id];

					for ( var k = 0; k < 3; k++ )
					{
						var a = Corner( side, t, k ); var b = Corner( side, t, ( k + 1 ) % 3 );
						var edge = b - a;
						var along = V3.Dot( p - a, edge ) / edge.LengthSquared;

						if ( along <= 0.0 || along >= 1.0 ) continue;
						if ( ( p - ( a + ( edge * along ) ) ).Length > onEdge ) continue;

						var key = Key( side.W[( t * 3 ) + k], side.W[( t * 3 ) + ( ( k + 1 ) % 3 )] );
						if ( !side.EdgeHead.TryGetValue( key, out var at ) ) continue;

						for ( ; at >= 0; at = side.EdgeNext[at] )
						{
							var other = at / 3;
							if ( other == t ) continue;

							side.CutOf( other ).AddPoint( id, side.W[other * 3], side.W[( other * 3 ) + 1], side.W[( other * 3 ) + 2] );
						}
					}
				}
			}
		}

		//-- cutting -----------------------------------------------------------

		private int Triangulate( Side side, Side other )
		{
			var cutCount = 0;
			var local = new Dictionary<int, int>();

			for ( var t = 0; t < side.Triangles; t++ )
			{
				var cut = side.Cuts[t];
				if ( cut is null ) continue;

				//The solid of a cave is kept whole, but cut all the same: the ground's edge has to find its points on it.
				Plane( side, t, out var origin, out var u, out var v );

				var b = side.B[t] - origin; var c = side.C[t] - origin;
				triangulation.Begin( V3.Dot( b, u ), V3.Dot( c, u ), V3.Dot( c, v ), eps * 3.0 );

				local.Clear();
				local[side.W[t * 3]] = 0; local[side.W[( t * 3 ) + 1]] = 1; local[side.W[( t * 3 ) + 2]] = 2;

				var good = true;

				foreach ( var id in cut.Points )
				{
					var p = weld.Points[id] - origin;

					if ( !triangulation.Insert( V3.Dot( p, u ), V3.Dot( p, v ) ) ) { good = false; if ( Debug is not null ) { Debug( $"insert failed: point {id} in tri {t} ({cut.Points.Count} points) why={triangulation.Why} eps={eps:g3} side={( side == ground ? "ground" : "solid" )}" ); Debug( $"   corners B=({triangulation.X[1]:g6},0) C=({triangulation.X[2]:g6},{triangulation.Y[2]:g6})" ); foreach ( var other2 in cut.Points ) { var pp = weld.Points[other2] - origin; Debug( $"   pt {other2}{( other2 < firstCurvePoint ? "(vertex)" : "" )}: ({V3.Dot( pp, u ):g6},{V3.Dot( pp, v ):g6}) off-plane {V3.Dot( pp, side.Normal[t] ):g3}" ); } } break; }

					local[id] = triangulation.X.Count - 1;
				}

				pieces.Clear();
				var pieceOwner = new List<int>();

				for ( var i = 0; good && i < cut.SegA.Count; i++ )
				{
					if ( !local.TryGetValue( cut.SegA[i], out var p ) || !local.TryGetValue( cut.SegB[i], out var q ) ) { good = false; break; }

					var before = pieces.Count;
					if ( !triangulation.Constrain( p, q, eps * 2.0, pieces ) ) { good = false; Debug?.Invoke( $"constrain failed in tri {t} ({cut.Points.Count} points, {cut.SegA.Count} segs) why={triangulation.Why}" ); break; }

					for ( var k = before; k < pieces.Count; k += 2 ) pieceOwner.Add( cut.SegPartner[i] );
				}

				if ( !good )
				{
					//Left whole, and kept or dropped as its middle says. The curve has a gap here, which the flood below may leak through - it notices when it does.
					report.Fallbacks++;
					side.Cuts[t] = null;
					continue;
				}

				cut.X = triangulation.X.ToArray();
				cut.Y = triangulation.Y.ToArray();
				cut.Triangles = triangulation.T.ToArray();
				cut.Id = new int[cut.X.Length];

				foreach ( var (id, at) in local ) cut.Id[at] = id;

				for ( var k = 0; k < pieces.Count; k += 2 )
				{
					var key = Key( cut.Id[pieces[k]], cut.Id[pieces[k + 1]] );

					if ( !side.Curve.TryGetValue( key, out var makers ) ) side.Curve[key] = makers = new List<int>( 2 );
					if ( !makers.Contains( pieceOwner[k / 2] ) ) makers.Add( pieceOwner[k / 2] );
				}

				if ( cut.Points.Count > 0 ) cutCount++;
			}

			return cutCount;
		}

		private void CountCurve( Side side, HashSet<int> used )
		{
			for ( var t = 0; t < side.Triangles; t++ )
			{
				if ( side.Cuts[t] is not { Id: not null } cut ) continue;

				for ( var i = 3; i < cut.Id.Length; i++ )
					if ( cut.Id[i] >= firstCurvePoint ) used.Add( cut.Id[i] );
			}
		}

		//-- what is kept ------------------------------------------------------

		//Floods the pieces of one mesh from the curve outward, decides each flood as a whole, and writes
		//what is kept. Says how many of the ground's pieces went, or how many of the solid's stayed.
		private int Emit( Side side, Side other, bool isGround )
		{
			//Every piece: a cut triangle's pieces, or a whole triangle.
			var pieceTriangle = new List<int>();
			var pieceLocal = new List<int>();
			var corners = new List<int>();

			for ( var t = 0; t < side.Triangles; t++ )
			{
				if ( !side.Live[t] ) continue;

				if ( side.Cuts[t] is { Triangles: not null } cut )
				{
					for ( var i = 0; i < cut.Triangles.Length; i += 3 )
					{
						pieceTriangle.Add( t ); pieceLocal.Add( i );
						corners.Add( cut.Id[cut.Triangles[i]] ); corners.Add( cut.Id[cut.Triangles[i + 1]] ); corners.Add( cut.Id[cut.Triangles[i + 2]] );
					}
				}
				else
				{
					pieceTriangle.Add( t ); pieceLocal.Add( -1 );
					corners.Add( side.W[t * 3] ); corners.Add( side.W[( t * 3 ) + 1] ); corners.Add( side.W[( t * 3 ) + 2] );
				}
			}

			var count = pieceTriangle.Count;
			var heads = new Dictionary<long, int>( count * 2 );
			var next = new int[count * 3];

			for ( var h = 0; h < count * 3; h++ )
			{
				var key = Key( corners[h], corners[( h / 3 * 3 ) + ( ( ( h % 3 ) + 1 ) % 3 )] );

				next[h] = heads.TryGetValue( key, out var head ) ? head : -1;
				heads[key] = h;
			}

			var flood = new int[count];
			Array.Fill( flood, -1 );

			var keep = new bool[count];
			var queue = new List<int>();
			var floods = 0;

			for ( var start = 0; start < count; start++ )
			{
				if ( flood[start] >= 0 ) continue;

				queue.Clear();
				queue.Add( start );
				flood[start] = floods;

				var score = 0.0; var weight = 0.0;
				var largest = start; var largestArea = -1.0;

				for ( var at = 0; at < queue.Count; at++ )
				{
					var piece = queue[at];

					V3 a = weld.Points[corners[piece * 3]], b = weld.Points[corners[( piece * 3 ) + 1]], c = weld.Points[corners[( piece * 3 ) + 2]];
					var area = V3.Cross( b - a, c - a ).LengthSquared;
					if ( area > largestArea ) { largestArea = area; largest = piece; }

					for ( var k = 0; k < 3; k++ )
					{
						var i0 = corners[( piece * 3 ) + k]; var i1 = corners[( piece * 3 ) + ( ( k + 1 ) % 3 )];
						var key = Key( i0, i1 );

						if ( side.Curve.TryGetValue( key, out var makers ) )
						{
							//On the curve. Which way this piece leaves it - before the face of the other mesh
							//that made this stretch, or behind - is the one thing about it that is exact.
							var p = weld.Points[i0]; var q = weld.Points[i1];
							var apex = weld.Points[corners[( piece * 3 ) + ( ( k + 2 ) % 3 )]];
							var edge = q - p;
							var length = edge.Length;
							if ( length <= 0.0 ) continue;

							var away = ( apex - p ) - ( edge * ( V3.Dot( apex - p, edge ) / ( length * length ) ) );
							var reach = away.Length;
							if ( reach <= 0.0 ) continue;

							foreach ( var maker in makers )
							{
								var facing = V3.Dot( other.Normal[maker], away ) / reach;
								if ( Math.Abs( facing ) < 1e-7 ) continue;

								score += facing * length;
								weight += Math.Abs( facing ) * length;
							}

							continue;
						}

						for ( var h = heads[key]; h >= 0; h = next[h] )
						{
							var across = h / 3;
							if ( flood[across] >= 0 ) continue;

							flood[across] = floods;
							queue.Add( across );
						}
					}
				}

				floods++;

				//Before the other mesh's faces is outside it - unless those faces are a cave's, which look into the air the ground is to lose.
				var solidIsHollow = carve;
				var wholeSolid = carve && !isGround;

				if ( wholeSolid )
				{
					foreach ( var piece in queue ) keep[piece] = true;
				}
				else if ( weight > 0.0 && Math.Abs( score ) >= weight * 0.2 )
				{
					var outside = isGround && solidIsHollow ? score < 0.0 : score > 0.0;
					foreach ( var piece in queue ) keep[piece] = outside;
				}
				else if ( weight > 0.0 )
				{
					//As much of it says in as says out: the curve does not close - an arch with no floor, a
					//cut that could not be made - and the flood ran round the end of it. Piece by piece, then.
					foreach ( var piece in queue ) keep[piece] = !Inside( side, other, corners, piece, isGround );
				}
				else
				{
					var outside = !Inside( side, other, corners, largest, isGround );
					foreach ( var piece in queue ) keep[piece] = outside;
				}
			}

			//Written: first what took no part, as it was, then the pieces kept.
			var result = 0;

			if ( isGround )
			{
				for ( var t = 0; t < side.Triangles; t++ )
				{
					if ( side.Live[t] || side.W[t * 3] < 0 && WasClosed( side, t ) ) continue;

					var i0 = side.Soup.Indices[t * 3]; var i1 = side.Soup.Indices[( t * 3 ) + 1]; var i2 = side.Soup.Indices[( t * 3 ) + 2];

					var pa = side.Soup.Vertices[i0].Position; var pb = side.Soup.Vertices[i1].Position; var pc = side.Soup.Vertices[i2].Position;
					if ( Vector3.Cross( pb - pa, pc - pa ).LengthSquared <= 0.0f ) continue;

					Write( side, t, Record( side, i0 ), Record( side, i1 ), Record( side, i2 ) );
				}
			}

			for ( var piece = 0; piece < count; piece++ )
			{
				if ( !keep[piece] )
				{
					if ( isGround ) result++;
					continue;
				}

				if ( !isGround ) result++;

				var t = pieceTriangle[piece];
				var at = pieceLocal[piece];

				if ( at < 0 )
				{
					Write( side, t, Record( side, side.Soup.Indices[t * 3] ), Record( side, side.Soup.Indices[( t * 3 ) + 1] ), Record( side, side.Soup.Indices[( t * 3 ) + 2] ) );
					continue;
				}

				var cut = side.Cuts[t];

				Write( side, t, Made( side, t, cut, cut.Triangles[at] ), Made( side, t, cut, cut.Triangles[at + 1] ), Made( side, t, cut, cut.Triangles[at + 2] ) );
			}

			return result;
		}

		//Whether a triangle that takes no part was one the weld closed up, rather than one that was never near.
		private static bool WasClosed( Side side, int t )
			=> side.Weld[side.Soup.Indices[t * 3]] >= 0 && side.Weld[side.Soup.Indices[( t * 3 ) + 1]] >= 0 && side.Weld[side.Soup.Indices[( t * 3 ) + 2]] >= 0;

		//Is this piece's middle behind the other mesh's nearest triangle; a solid's open rim means outside
		private bool Inside( Side side, Side other, List<int> corners, int piece, bool isGround )
		{
			var middle = ( weld.Points[corners[piece * 3]] + weld.Points[corners[( piece * 3 ) + 1]] + weld.Points[corners[( piece * 3 ) + 2]] ) * ( 1.0 / 3.0 );

			if ( other.Tree.Empty )
			{
				//Nothing of the ground is near the solid at all: every triangle of it is asked, once, which is rare and is not many.
				return !isGround && BehindAnyGround( other, middle );
			}

			if ( !other.Tree.Nearest( middle, out var t, out var signed, out var feature ) ) return false;

			if ( isGround && feature != 0 && OnRim( other, t, feature ) ) return false;

			return isGround && carve ? signed > 0.0 : signed < 0.0;
		}

		private static bool OnRim( Side side, int t, int feature )
		{
			bool Open( int k ) => side.Uses( Key( side.W[( t * 3 ) + k], side.W[( t * 3 ) + ( ( k + 1 ) % 3 )] ) ) < 2;

			return feature switch
			{
				1 => Open( 0 ),
				2 => Open( 1 ),
				3 => Open( 2 ),
				4 => Open( 0 ) || Open( 2 ),
				5 => Open( 0 ) || Open( 1 ),
				_ => Open( 1 ) || Open( 2 ),
			};
		}

		private static bool BehindAnyGround( Side groundSide, V3 p )
		{
			var best = double.MaxValue;
			var behind = false;
			var soup = groundSide.Soup;

			for ( var t = 0; t < groundSide.Triangles; t++ )
			{
				var a = new V3( soup.Vertices[soup.Indices[t * 3]].Position );
				var b = new V3( soup.Vertices[soup.Indices[( t * 3 ) + 1]].Position );
				var c = new V3( soup.Vertices[soup.Indices[( t * 3 ) + 2]].Position );

				var middle = ( a + b + c ) * ( 1.0 / 3.0 );
				var d = ( middle - p ).LengthSquared;
				if ( d >= best ) continue;

				best = d;
				behind = V3.Dot( V3.Cross( b - a, c - a ), p - a ) < 0.0;
			}

			return behind;
		}

		//-- writing -----------------------------------------------------------

		private void Write( Side side, int t, int a, int b, int c )
		{
			if ( a == b || b == c || c == a ) return;

			//Exactly the same place, not nearly: the vector's own == is a nearly.
			var pa = into.Vertices[a].Position; var pb = into.Vertices[b].Position; var pc = into.Vertices[c].Position;
			if ( Same( pa, pb ) || Same( pb, pc ) || Same( pc, pa ) ) return;

			into.Indices.Add( a ); into.Indices.Add( b ); into.Indices.Add( c );
			into.Materials.Add( t < side.Soup.Materials.Count ? side.Soup.Materials[t] : (ushort)0 );
			report.FromGround.Add( side == ground );
		}

		private static bool Same( Vector3 a, Vector3 b ) => a.x == b.x && a.y == b.y && a.z == b.z;

		//A vertex that was there already, where the weld put it.
		private int Record( Side side, int record )
		{
			if ( side.OutOfRecord[record] >= 0 ) return side.OutOfRecord[record];

			var vertex = side.Soup.Vertices[record];
			if ( side.Weld[record] >= 0 ) vertex.Position = weld.Points[side.Weld[record]].ToVector();

			into.Vertices.Add( vertex );
			return side.OutOfRecord[record] = into.Vertices.Count - 1;
		}

		//Vertex of a cut triangle, shared only between triangles with the same corner records
		private int Made( Side side, int t, Cut cut, int local )
		{
			var r0 = side.Soup.Indices[t * 3]; var r1 = side.Soup.Indices[( t * 3 ) + 1]; var r2 = side.Soup.Indices[( t * 3 ) + 2];

			if ( local < 3 ) return Record( side, local == 0 ? r0 : local == 1 ? r1 : r2 );

			double x = cut.X[local], y = cut.Y[local];
			double bx = cut.X[1], cx = cut.X[2], cy = cut.Y[2];

			//How far from each edge, and along the nearest if it is on it.
			var w2 = y / cy;
			var w1 = ( x - ( cx * w2 ) ) / bx;
			var w0 = 1.0 - w1 - w2;

			var onEdge = eps * 3.0;
			var d0 = y;
			var e1 = Math.Sqrt( ( ( cx - bx ) * ( cx - bx ) ) + ( cy * cy ) );
			var d1 = ( ( ( cx - bx ) * y ) - ( cy * ( x - bx ) ) ) / e1;
			var e2 = Math.Sqrt( ( cx * cx ) + ( cy * cy ) );
			var d2 = ( ( cy * x ) - ( cx * y ) ) / e2;

			var edge = -1;
			var least = onEdge;
			if ( d0 <= least ) { least = d0; edge = 0; }
			if ( d1 <= least ) { least = d1; edge = 1; }
			if ( d2 <= least ) { edge = 2; }

			(int, int, int) key;
			int ra = 0, rb = 0; double along = 0.0;

			if ( edge >= 0 )
			{
				ra = edge == 0 ? r0 : edge == 1 ? r1 : r2;
				rb = edge == 0 ? r1 : edge == 1 ? r2 : r0;
				if ( ra > rb ) ( ra, rb ) = ( rb, ra );

				key = (cut.Id[local], ra, rb);
			}
			else key = (cut.Id[local], -1 - t, 0);

			if ( side.OutOfNew.TryGetValue( key, out var known ) ) return known;

			var position = weld.Points[cut.Id[local]];
			TerrainSoup.Vertex made;

			if ( edge >= 0 )
			{
				var from = new V3( side.Soup.Vertices[ra].Position ); var to = new V3( side.Soup.Vertices[rb].Position );
				var span = to - from;
				along = Math.Clamp( V3.Dot( position - from, span ) / Math.Max( span.LengthSquared, 1e-300 ), 0.0, 1.0 );

				made = Blend( side.Soup.Vertices[ra], side.Soup.Vertices[rb], side.Soup.Vertices[rb], 1.0 - along, along, 0.0, side.Normal[t] );
			}
			else
			{
				w0 = Math.Max( w0, 0.0 ); w1 = Math.Max( w1, 0.0 ); w2 = Math.Max( w2, 0.0 );
				var sum = w0 + w1 + w2;

				made = Blend( side.Soup.Vertices[r0], side.Soup.Vertices[r1], side.Soup.Vertices[r2], w0 / sum, w1 / sum, w2 / sum, side.Normal[t] );
			}

			//The one number, for both sides of the seam.
			made.Position = position.ToVector();

			into.Vertices.Add( made );
			return side.OutOfNew[key] = into.Vertices.Count - 1;
		}

		private static TerrainSoup.Vertex Blend( in TerrainSoup.Vertex a, in TerrainSoup.Vertex b, in TerrainSoup.Vertex c,
			double wa, double wb, double wc, V3 face )
		{
			float fa = (float)wa, fb = (float)wb, fc = (float)wc;

			var normal = ( a.Normal * fa ) + ( b.Normal * fb ) + ( c.Normal * fc );
			normal = normal.LengthSquared > 1e-12f ? normal.Normal : face.ToVector();

			var tangent = new Vector3(
				( a.Tangent.x * fa ) + ( b.Tangent.x * fb ) + ( c.Tangent.x * fc ),
				( a.Tangent.y * fa ) + ( b.Tangent.y * fb ) + ( c.Tangent.y * fc ),
				( a.Tangent.z * fa ) + ( b.Tangent.z * fb ) + ( c.Tangent.z * fc ) );

			if ( tangent.LengthSquared > 1e-12f ) tangent = tangent.Normal;

			var handed = wa >= wb && wa >= wc ? a.Tangent.w : wb >= wc ? b.Tangent.w : c.Tangent.w;

			return new TerrainSoup.Vertex
			{
				Normal = normal,
				Paint = TerrainPaint.Mix( a.Paint, b.Paint, c.Paint, fa, fb, fc ),
				Uv = ( a.Uv * fa ) + ( b.Uv * fb ) + ( c.Uv * fc ),
				Tangent = new Vector4( tangent.x, tangent.y, tangent.z, handed ),
			};
		}
	}
}
