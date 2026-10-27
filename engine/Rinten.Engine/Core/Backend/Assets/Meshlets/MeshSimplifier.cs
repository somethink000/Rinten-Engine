namespace Rinten.Meshlets;

//Edge-collapse simplifier ported from meshoptimizer's simplifyEdge, with attribute quadrics
internal static class MeshSimplifier
{
	[Flags]
	public enum Options
	{
		None = 0,

		/// <summary>Vertices on an open edge do not move. A terrain chunk's chain needs this so its levels still meet its neighbours'.</summary>
		LockBorder = 1 << 0,

		/// <summary>The target error and the reported one are in the mesh's own units rather than fractions of its extent.</summary>
		ErrorAbsolute = 1 << 2,

		/// <summary>A disconnected piece whose whole size is under the error so far is dropped outright.</summary>
		Prune = 1 << 3,

		/// <summary>Clamps attribute error to roughly the edge length around the vertex</summary>
		ErrorClamped = 1 << 4,

		/// <summary>An edge the surface doubles back over - its two triangles facing nearly opposite ways, the rim of a thin sheet - is held like a border. Without it a sheet's rim is cheap to collapse inwards, the planes on both sides agreeing, and thin things shrink.</summary>
		PreserveFolds = 1 << 5,

		/// <summary>Lets vertices whose copies differ only in unprotected attributes collapse together</summary>
		Permissive = 1 << 6,
	}

	private const int MaxAttributes = 32;

	//-- edge adjacency: every half-edge out of each vertex --------------------

	private struct Edge
	{
		public uint Next;
		public uint Prev;
	}

	private struct Adjacency
	{
		/// <summary>vertexCount + 1 entries; a vertex's edges are [Offsets[v], Offsets[v + 1]).</summary>
		public uint[] Offsets;
		public Edge[] Data;
	}

	private static void UpdateAdjacency( ref Adjacency adjacency, ReadOnlySpan<uint> indices, int vertexCount, uint[] remap )
	{
		var faceCount = indices.Length / 3;
		var offsets = adjacency.Offsets;
		var data = adjacency.Data;

		System.Array.Clear( offsets, 1, vertexCount );

		foreach ( var index in indices )
		{
			var v = remap is null ? index : remap[index];
			offsets[v + 1]++;
		}

		var offset = 0u;

		for ( var i = 0; i < vertexCount; ++i )
		{
			var count = offsets[i + 1];
			offsets[i + 1] = offset;
			offset += count;
		}

		for ( var i = 0; i < faceCount; ++i )
		{
			uint a = indices[( i * 3 ) + 0], b = indices[( i * 3 ) + 1], c = indices[( i * 3 ) + 2];

			if ( remap is not null )
			{
				a = remap[a];
				b = remap[b];
				c = remap[c];
			}

			data[offsets[a + 1]].Next = b;
			data[offsets[a + 1]].Prev = c;
			offsets[a + 1]++;

			data[offsets[b + 1]].Next = c;
			data[offsets[b + 1]].Prev = a;
			offsets[b + 1]++;

			data[offsets[c + 1]].Next = a;
			data[offsets[c + 1]].Prev = b;
			offsets[c + 1]++;
		}

		offsets[0] = 0;
	}

	private static bool HasEdge( in Adjacency adjacency, uint a, uint b )
	{
		var start = (int)adjacency.Offsets[a];
		var end = (int)adjacency.Offsets[a + 1];

		for ( var i = start; i < end; ++i )
			if ( adjacency.Data[i].Next == b )
				return true;

		return false;
	}

	//Whether any copy of a at its point has an edge to the point b stands at.
	private static bool HasEdge( in Adjacency adjacency, uint a, uint b, uint[] remap, uint[] wedge )
	{
		var v = a;

		do
		{
			var start = (int)adjacency.Offsets[v];
			var end = (int)adjacency.Offsets[v + 1];

			for ( var i = start; i < end; ++i )
				if ( remap[adjacency.Data[i].Next] == remap[b] )
					return true;

			v = wedge[v];
		}
		while ( v != a );

		return false;
	}

	//-- welding by position, and the wedge ring through a seam --------------------

	private struct PositionHasher : MeshHash.IIndexHasher
	{
		public Vector3[] Positions;

		public uint Hash( uint index ) => MeshHash.Position( Positions[index] );

		public bool Equal( uint a, uint b )
		{
			ref readonly var l = ref Positions[a];
			ref readonly var r = ref Positions[b];

			return l.x == r.x && l.y == r.y && l.z == r.z;
		}
	}

	private static void BuildPositionRemap( uint[] remap, uint[] wedge, Vector3[] positions )
	{
		var vertexCount = positions.Length;
		var hasher = new PositionHasher { Positions = positions };

		var table = new uint[MeshHash.Buckets( vertexCount )];
		table.AsSpan().Fill( MeshHash.Empty );

		//Forward: each vertex to the first one at its position.
		for ( var i = 0; i < vertexCount; ++i )
		{
			ref var entry = ref MeshHash.Lookup( table, in hasher, (uint)i );

			if ( entry == MeshHash.Empty ) entry = (uint)i;

			remap[i] = entry;
		}

		//The wedge ring: every vertex at a position, cyclically. Manifold vertices point at themselves.
		for ( var i = 0; i < vertexCount; ++i ) wedge[i] = (uint)i;

		for ( var i = 0; i < vertexCount; ++i )
		{
			if ( remap[i] != i )
			{
				var r = remap[i];

				wedge[i] = wedge[r];
				wedge[r] = (uint)i;
			}
		}
	}

	//-- what kind of vertex each one is ----------------------------------------------

	private const byte KindManifold = 0;
	private const byte KindBorder = 1;
	private const byte KindSeam = 2;

	//Several copies at the point, none protected: moves as long as every copy goes to the target.
	private const byte KindComplex = 3;

	//A complex vertex on the mesh's open edge: moves only onto another of its kind.
	private const byte KindFringe = 4;
	private const byte KindLocked = 5;
	private const int KindCount = 6;

	//Collapsing kind A into kind B keeps B what it was: a manifold vertex can go anywhere, a border or seam only
	//along itself or into a lock, a complex one onto complex, fringe or a lock, a fringe one only along the fringe.
	private static readonly byte[,] canCollapse =
	{
		{ 1, 1, 1, 1, 1, 1 },
		{ 0, 1, 0, 0, 1, 1 },
		{ 0, 0, 1, 0, 0, 1 },
		{ 0, 0, 0, 1, 1, 1 },
		{ 0, 0, 0, 0, 1, 0 },
		{ 0, 0, 0, 0, 0, 0 },
	};

	//Whether an edge between two kinds has its opposite half-edge, so the pair is not entered twice. Complex
	//edges are kept apart even where they have one: each copy has its own target, and both are worth weighing.
	private static readonly byte[,] hasOpposite =
	{
		{ 1, 1, 1, 1, 1, 1 },
		{ 1, 0, 1, 0, 0, 0 },
		{ 1, 1, 1, 0, 0, 1 },
		{ 1, 0, 0, 0, 0, 0 },
		{ 1, 0, 0, 0, 0, 0 },
		{ 1, 0, 1, 0, 0, 0 },
	};

	private static void ClassifyVertices( byte[] result, uint[] loop, uint[] loopback, int vertexCount, in Adjacency adjacency,
		uint[] remap, uint[] wedge, byte[] protect, ReadOnlySpan<byte> locks, Options options )
	{
		loop.AsSpan( 0, vertexCount ).Fill( MeshHash.Empty );
		loopback.AsSpan( 0, vertexCount ).Fill( MeshHash.Empty );

		//Open half-edges in and out of each vertex: Empty for none, the vertex itself for more
		//than one. Kept in loop/loopback, which is what they become for border and seam vertices.
		var openinc = loopback;
		var openout = loop;

		for ( var i = 0; i < vertexCount; ++i )
		{
			var vertex = (uint)i;

			var start = (int)adjacency.Offsets[vertex];
			var end = (int)adjacency.Offsets[vertex + 1];

			for ( var j = start; j < end; ++j )
			{
				var target = adjacency.Data[j].Next;

				if ( target == vertex )
				{
					//A degenerate triangle's self edge is open both ways by definition and would
					//close another triangle's open edge by mistake; marked as many open edges instead.
					openinc[vertex] = openout[vertex] = vertex;
				}
				else if ( !HasEdge( in adjacency, target, vertex ) )
				{
					openinc[target] = openinc[target] == MeshHash.Empty ? vertex : target;
					openout[vertex] = openout[vertex] == MeshHash.Empty ? target : vertex;
				}
			}
		}

		for ( var i = 0; i < vertexCount; ++i )
		{
			if ( remap[i] == i )
			{
				if ( wedge[i] == i )
				{
					uint openi = openinc[i], openo = openout[i];

					if ( openi == MeshHash.Empty && openo == MeshHash.Empty )
					{
						result[i] = KindManifold;
					}
					else if ( openi != MeshHash.Empty && openo != MeshHash.Empty && remap[openi] == remap[openo] && openi != i )
					{
						//A half-seam: one vertex meeting both sides of a seam. A seam, so the full seam vertex may fold onto it.
						result[i] = KindSeam;
					}
					else if ( openi != i && openo != i )
					{
						result[i] = KindBorder;
					}
					else
					{
						result[i] = KindLocked;
					}
				}
				else if ( wedge[wedge[i]] == i )
				{
					//Two vertices at the point: a seam, if each has one open half-edge each way and they connect to the same places.
					var w = wedge[i];
					uint openiv = openinc[i], openov = openout[i];
					uint openiw = openinc[w], openow = openout[w];

					if ( openiv != MeshHash.Empty && openiv != i && openov != MeshHash.Empty && openov != i &&
						openiw != MeshHash.Empty && openiw != w && openow != MeshHash.Empty && openow != w )
					{
						if ( remap[openiv] == remap[openow] && remap[openov] == remap[openiw] && remap[openiv] != remap[openov] )
							result[i] = KindSeam;
						else
							result[i] = KindLocked;
					}
					else
					{
						result[i] = KindLocked;
					}
				}
				else
				{
					//Three or more at one point: nothing is said about those.
					result[i] = KindLocked;
				}
			}
			else
			{
				result[i] = result[remap[i]];
			}
		}

		//Permissive: what was held as a seam or a lock only for standing in copies becomes free
		//to move with all its copies - unless one of the copies differs in something protected.
		if ( ( options & Options.Permissive ) != 0 )
		{
			for ( var i = 0; i < vertexCount; ++i )
			{
				if ( result[i] != KindSeam && result[i] != KindLocked ) continue;

				//The first of each point decides; the rest follow it.
				if ( remap[i] != i )
				{
					result[i] = result[remap[i]];
					continue;
				}

				var v = (uint)i;
				var guarded = false;

				do
				{
					guarded |= protect is not null && protect[v] != 0;
					v = wedge[v];
				}
				while ( v != i );

				//On the open edge of the mesh: an edge out of any copy with no edge back into any copy of its end.
				var border = false;

				do
				{
					var start = (int)adjacency.Offsets[v];
					var end = (int)adjacency.Offsets[v + 1];

					for ( var j = start; j < end; ++j )
						border |= !HasEdge( in adjacency, adjacency.Data[j].Next, v, remap, wedge );

					v = wedge[v];
				}
				while ( v != i );

				if ( !guarded ) result[i] = border ? KindFringe : KindComplex;
			}
		}

		//The caller's own locks: a locked copy locks its point, and every copy at a locked point is locked with it.
		if ( !locks.IsEmpty )
		{
			for ( var i = 0; i < vertexCount; ++i )
				if ( locks[i] != 0 )
					result[remap[i]] = KindLocked;

			for ( var i = 0; i < vertexCount; ++i )
				if ( result[remap[i]] == KindLocked )
					result[i] = KindLocked;
		}

		if ( ( options & Options.LockBorder ) != 0 )
			for ( var i = 0; i < vertexCount; ++i )
				if ( result[i] == KindBorder || result[i] == KindFringe )
					result[i] = KindLocked;
	}

	//-- the mesh in a unit cube --------------------------------------------------

	//Rescales positions into a unit cube in place and returns the extent; done after welding
	private static float RescalePositions( Vector3[] positions, out Vector3 corner )
	{
		float minx = float.MaxValue, miny = float.MaxValue, minz = float.MaxValue;
		float maxx = -float.MaxValue, maxy = -float.MaxValue, maxz = -float.MaxValue;

		for ( var i = 0; i < positions.Length; ++i )
		{
			ref readonly var v = ref positions[i];

			minx = minx > v.x ? v.x : minx;
			miny = miny > v.y ? v.y : miny;
			minz = minz > v.z ? v.z : minz;
			maxx = maxx < v.x ? v.x : maxx;
			maxy = maxy < v.y ? v.y : maxy;
			maxz = maxz < v.z ? v.z : maxz;
		}

		var extent = 0.0f;

		extent = ( maxx - minx ) < extent ? extent : ( maxx - minx );
		extent = ( maxy - miny ) < extent ? extent : ( maxy - miny );
		extent = ( maxz - minz ) < extent ? extent : ( maxz - minz );

		var scale = extent == 0 ? 0.0f : 1.0f / extent;

		for ( var i = 0; i < positions.Length; ++i )
		{
			ref var r = ref positions[i];
			r = new Vector3( ( r.x - minx ) * scale, ( r.y - miny ) * scale, ( r.z - minz ) * scale );
		}

		corner = new Vector3( minx, miny, minz );

		return extent;
	}

	private static void RescaleAttributes( float[] result, ReadOnlySpan<float> attributes, int vertexCount, int attributeStride,
		ReadOnlySpan<float> weights, ReadOnlySpan<uint> attributeRemap )
	{
		var count = attributeRemap.Length;

		for ( var i = 0; i < vertexCount; ++i )
		{
			for ( var k = 0; k < count; ++k )
			{
				var rk = (int)attributeRemap[k];
				var a = attributes[( i * attributeStride ) + rk];

				result[( i * count ) + k] = a * weights[rk];
			}
		}
	}

	//-- quadrics ----------------------------------------------------------------------

	private struct Quadric
	{
		//a00*x^2 + a11*y^2 + a22*z^2 + 2*a10*xy + 2*a20*xz + 2*a21*yz + 2*b0*x + 2*b1*y + 2*b2*z + c
		public float a00, a11, a22;
		public float a10, a20, a21;
		public float b0, b1, b2, c;
		public float w;
	}

	private struct QuadricGrad
	{
		//gx*x + gy*y + gz*z + gw
		public float gx, gy, gz, gw;
	}

	private struct Collapse
	{
		public uint V0;
		public uint V1;
		public float Error;
		public bool Bidi;
	}

	private static float Normalize( ref float x, ref float y, ref float z )
	{
		var length = MathF.Sqrt( ( x * x ) + ( y * y ) + ( z * z ) );

		if ( length > 0 )
		{
			x /= length;
			y /= length;
			z /= length;
		}

		return length;
	}

	private static void QuadricAdd( ref Quadric Q, in Quadric R )
	{
		Q.a00 += R.a00;
		Q.a11 += R.a11;
		Q.a22 += R.a22;
		Q.a10 += R.a10;
		Q.a20 += R.a20;
		Q.a21 += R.a21;
		Q.b0 += R.b0;
		Q.b1 += R.b1;
		Q.b2 += R.b2;
		Q.c += R.c;
		Q.w += R.w;
	}

	private static void QuadricAdd( ref QuadricGrad G, in QuadricGrad R )
	{
		G.gx += R.gx;
		G.gy += R.gy;
		G.gz += R.gz;
		G.gw += R.gw;
	}

	private static void QuadricAdd( Span<QuadricGrad> G, ReadOnlySpan<QuadricGrad> R )
	{
		for ( var k = 0; k < G.Length; ++k )
		{
			G[k].gx += R[k].gx;
			G[k].gy += R[k].gy;
			G[k].gz += R[k].gz;
			G[k].gw += R[k].gw;
		}
	}

	private static float QuadricEval( in Quadric Q, in Vector3 v )
	{
		var rx = Q.b0;
		var ry = Q.b1;
		var rz = Q.b2;

		rx += Q.a10 * v.y;
		ry += Q.a21 * v.z;
		rz += Q.a20 * v.x;

		rx *= 2;
		ry *= 2;
		rz *= 2;

		rx += Q.a00 * v.x;
		ry += Q.a11 * v.y;
		rz += Q.a22 * v.z;

		var r = Q.c;
		r += rx * v.x;
		r += ry * v.y;
		r += rz * v.z;

		return r;
	}

	private static float QuadricError( in Quadric Q, in Vector3 v )
	{
		var r = QuadricEval( in Q, in v );
		var s = Q.w == 0.0f ? 0.0f : 1.0f / Q.w;

		return MathF.Abs( r ) * s;
	}

	private static float QuadricError( in Quadric Q, ReadOnlySpan<QuadricGrad> G, in Vector3 v, ReadOnlySpan<float> va, bool clamped )
	{
		var r = QuadricEval( in Q, in v );

		//The parts of (eval(pos) - attr)^2 that depend on attr; see QuadricFromAttributes.
		for ( var k = 0; k < G.Length; ++k )
		{
			var a = va[k];
			var g = ( v.x * G[k].gx ) + ( v.y * G[k].gy ) + ( v.z * G[k].gz ) + G[k].gw;

			r += a * ( ( a * Q.w ) - ( 2 * g ) );
		}

		//Not divided by Q.w, to keep the edge scaling, unlike the position error. Clamped,
		//it is held to the weight - the area folded in - see Options.ErrorClamped.
		var e = MathF.Abs( r );

		return clamped ? ( e < Q.w ? e : Q.w ) : e;
	}

	private static void QuadricFromPlane( out Quadric Q, float a, float b, float c, float d, float w )
	{
		var aw = a * w;
		var bw = b * w;
		var cw = c * w;
		var dw = d * w;

		Q.a00 = a * aw;
		Q.a11 = b * bw;
		Q.a22 = c * cw;
		Q.a10 = a * bw;
		Q.a20 = a * cw;
		Q.a21 = b * cw;
		Q.b0 = a * dw;
		Q.b1 = b * dw;
		Q.b2 = c * dw;
		Q.c = d * dw;
		Q.w = w;
	}

	private static void QuadricFromPoint( out Quadric Q, float x, float y, float z, float w )
	{
		Q.a00 = Q.a11 = Q.a22 = w;
		Q.a10 = Q.a20 = Q.a21 = 0;
		Q.b0 = -x * w;
		Q.b1 = -y * w;
		Q.b2 = -z * w;
		Q.c = ( ( x * x ) + ( y * y ) + ( z * z ) ) * w;
		Q.w = w;
	}

	private static void QuadricFromTriangle( out Quadric Q, in Vector3 p0, in Vector3 p1, in Vector3 p2, float weight )
	{
		float p10x = p1.x - p0.x, p10y = p1.y - p0.y, p10z = p1.z - p0.z;
		float p20x = p2.x - p0.x, p20y = p2.y - p0.y, p20z = p2.z - p0.z;

		var nx = ( p10y * p20z ) - ( p10z * p20y );
		var ny = ( p10z * p20x ) - ( p10x * p20z );
		var nz = ( p10x * p20y ) - ( p10y * p20x );
		var area = Normalize( ref nx, ref ny, ref nz );

		var distance = ( nx * p0.x ) + ( ny * p0.y ) + ( nz * p0.z );

		//The root of the area, so the error scales linearly; better silhouettes.
		QuadricFromPlane( out Q, nx, ny, nz, -distance, MathF.Sqrt( area ) * weight );
	}

	private static void QuadricFromTriangleEdge( out Quadric Q, in Vector3 p0, in Vector3 p1, in Vector3 p2, float weight )
	{
		float p10x = p1.x - p0.x, p10y = p1.y - p0.y, p10z = p1.z - p0.z;

		var lengthsq = ( p10x * p10x ) + ( p10y * p10y ) + ( p10z * p10z );
		var length = MathF.Sqrt( lengthsq );

		//The perpendicular from p2 to the edge, through the plane that holds the edge and stands square to the triangle.
		float p20x = p2.x - p0.x, p20y = p2.y - p0.y, p20z = p2.z - p0.z;
		var p20p = ( p20x * p10x ) + ( p20y * p10y ) + ( p20z * p10z );

		var perpx = ( p20x * lengthsq ) - ( p10x * p20p );
		var perpy = ( p20y * lengthsq ) - ( p10y * p20p );
		var perpz = ( p20z * lengthsq ) - ( p10z * p20p );
		Normalize( ref perpx, ref perpy, ref perpz );

		var distance = ( perpx * p0.x ) + ( perpy * p0.y ) + ( perpz * p0.z );

		//Weighted by edge length, to match the triangle weight.
		QuadricFromPlane( out Q, perpx, perpy, perpz, -distance, length * weight );
	}

	private static void QuadricFromAttributes( out Quadric Q, Span<QuadricGrad> G, in Vector3 p0, in Vector3 p1, in Vector3 p2,
		ReadOnlySpan<float> va0, ReadOnlySpan<float> va1, ReadOnlySpan<float> va2 )
	{
		//Each attribute is encoded as (eval(pos) - attr)^2, where eval interpolates the attribute across the triangle from its gradients.
		float p10x = p1.x - p0.x, p10y = p1.y - p0.y, p10z = p1.z - p0.z;
		float p20x = p2.x - p0.x, p20y = p2.y - p0.y, p20z = p2.z - p0.z;

		var nx = ( p10y * p20z ) - ( p10z * p20y );
		var ny = ( p10z * p20x ) - ( p10x * p20z );
		var nz = ( p10x * p20y ) - ( p10y * p20x );
		var area = MathF.Sqrt( ( nx * nx ) + ( ny * ny ) + ( nz * nz ) ) * 0.5f;

		//Weighted by the area, the square of edge length, so the units match the positional error: a
		//weighted attribute moving by one along a distance d costs about what a position moving d does.
		var w = area;

		//Gradients from barycentrics; v0, v1 are the edges, dij their dots.
		var d00 = ( p10x * p10x ) + ( p10y * p10y ) + ( p10z * p10z );
		var d01 = ( p10x * p20x ) + ( p10y * p20y ) + ( p10z * p20z );
		var d11 = ( p20x * p20x ) + ( p20y * p20y ) + ( p20z * p20z );
		var denom = ( d00 * d11 ) - ( d01 * d01 );
		var denomr = denom == 0 ? 0.0f : 1.0f / denom;

		var gx1 = ( ( d11 * p10x ) - ( d01 * p20x ) ) * denomr;
		var gx2 = ( ( d00 * p20x ) - ( d01 * p10x ) ) * denomr;
		var gy1 = ( ( d11 * p10y ) - ( d01 * p20y ) ) * denomr;
		var gy2 = ( ( d00 * p20y ) - ( d01 * p10y ) ) * denomr;
		var gz1 = ( ( d11 * p10z ) - ( d01 * p20z ) ) * denomr;
		var gz2 = ( ( d00 * p20z ) - ( d01 * p10z ) ) * denomr;

		Q = default;
		Q.w = w;

		for ( var k = 0; k < G.Length; ++k )
		{
			float a0 = va0[k], a1 = va1[k], a2 = va2[k];

			var gx = ( gx1 * ( a1 - a0 ) ) + ( gx2 * ( a2 - a0 ) );
			var gy = ( gy1 * ( a1 - a0 ) ) + ( gy2 * ( a2 - a0 ) );
			var gz = ( gz1 * ( a1 - a0 ) ) + ( gz2 * ( a2 - a0 ) );
			var gw = a0 - ( p0.x * gx ) - ( p0.y * gy ) - ( p0.z * gz );

			Q.a00 += w * ( gx * gx );
			Q.a11 += w * ( gy * gy );
			Q.a22 += w * ( gz * gz );

			Q.a10 += w * ( gy * gx );
			Q.a20 += w * ( gz * gx );
			Q.a21 += w * ( gz * gy );

			Q.b0 += w * ( gx * gw );
			Q.b1 += w * ( gy * gw );
			Q.b2 += w * ( gz * gw );

			Q.c += w * ( gw * gw );

			//What depends on the attribute value is added at evaluation.
			G[k].gx = w * gx;
			G[k].gy = w * gy;
			G[k].gz = w * gz;
			G[k].gw = w * gw;
		}
	}

	//The plane of a triangle weighted by its area: what moving a corner along it does to the volume the surface encloses.
	private static void QuadricVolumeGradient( out QuadricGrad G, in Vector3 p0, in Vector3 p1, in Vector3 p2 )
	{
		float p10x = p1.x - p0.x, p10y = p1.y - p0.y, p10z = p1.z - p0.z;
		float p20x = p2.x - p0.x, p20y = p2.y - p0.y, p20z = p2.z - p0.z;

		var nx = ( p10y * p20z ) - ( p10z * p20y );
		var ny = ( p10z * p20x ) - ( p10x * p20z );
		var nz = ( p10x * p20y ) - ( p10y * p20x );
		var area = Normalize( ref nx, ref ny, ref nz ) * 0.5f;

		G.gx = nx * area;
		G.gy = ny * area;
		G.gz = nz * area;
		G.gw = ( ( -p0.x * nx ) - ( p0.y * ny ) - ( p0.z * nz ) ) * area;
	}

	//The point a quadric is least at, with the volume held: A p = -b, and the plane GV as a
	//constraint through a Lagrange multiplier. False where the quadric does not pin a point down.
	private static bool QuadricSolve( out Vector3 p, in Quadric Q, in QuadricGrad GV )
	{
		float a00 = Q.a00, a11 = Q.a11, a22 = Q.a22;
		float a10 = Q.a10, a20 = Q.a20, a21 = Q.a21;
		float x0 = -Q.b0, x1 = -Q.b1, x2 = -Q.b2;

		var eps = 1e-6f * Q.w;

		//A = L D L^T.
		var d0 = a00;
		var l10 = a10 / d0;
		var l20 = a20 / d0;

		var d1 = a11 - ( a10 * l10 );
		var dl21 = a21 - ( a20 * l10 );
		var l21 = dl21 / d1;

		var d2 = a22 - ( a20 * l20 ) - ( dl21 * l21 );

		//L y = x.
		var y0 = x0;
		var y1 = x1 - ( l10 * y0 );
		var y2 = x2 - ( l20 * y0 ) - ( l21 * y1 );

		//D z = y.
		var z0 = y0 / d0;
		var z1 = y1 / d1;
		var z2 = y2 / d2;

		//The constraint as a fourth row.
		float a30 = GV.gx, a31 = GV.gy, a32 = GV.gz;
		var x3 = -GV.gw;

		var l30 = a30 / d0;
		var dl31 = a31 - ( a30 * l10 );
		var l31 = dl31 / d1;
		var dl32 = a32 - ( a30 * l20 ) - ( dl31 * l21 );
		var l32 = dl32 / d2;
		var d3 = 0.0f - ( a30 * l30 ) - ( dl31 * l31 ) - ( dl32 * l32 );

		var y3 = x3 - ( l30 * y0 ) - ( l31 * y1 ) - ( l32 * y2 );

		//A constraint that says nothing is left out.
		var z3 = MathF.Abs( d3 ) > eps ? y3 / d3 : 0.0f;

		//L^T p = z.
		var lambda = z3;
		var pz = z2 - ( l32 * lambda );
		var py = z1 - ( l21 * pz ) - ( l31 * lambda );
		var px = z0 - ( l10 * py ) - ( l20 * pz ) - ( l30 * lambda );

		p = new Vector3( px, py, pz );

		return MathF.Abs( d0 ) > eps && MathF.Abs( d1 ) > eps && MathF.Abs( d2 ) > eps;
	}

	//Folds an attribute quadric into a vertex's, with the attribute values themselves taken out: what
	//is left is least where the attributes as a whole would be closest, whatever they come to there.
	private static void QuadricReduceAttributes( ref Quadric Q, in Quadric A, ReadOnlySpan<QuadricGrad> G )
	{
		//Times the vertex's weight, so the error minimised is the normalised one.
		Q.a00 += A.a00 * Q.w;
		Q.a11 += A.a11 * Q.w;
		Q.a22 += A.a22 * Q.w;
		Q.a10 += A.a10 * Q.w;
		Q.a20 += A.a20 * Q.w;
		Q.a21 += A.a21 * Q.w;
		Q.b0 += A.b0 * Q.w;
		Q.b1 += A.b1 * Q.w;
		Q.b2 += A.b2 * Q.w;

		var iaw = A.w == 0 ? 0.0f : Q.w / A.w;

		//B B^T / a, from the gradients.
		for ( var k = 0; k < G.Length; ++k )
		{
			ref readonly var g = ref G[k];

			Q.a00 -= ( g.gx * g.gx ) * iaw;
			Q.a11 -= ( g.gy * g.gy ) * iaw;
			Q.a22 -= ( g.gz * g.gz ) * iaw;
			Q.a10 -= ( g.gx * g.gy ) * iaw;
			Q.a20 -= ( g.gx * g.gz ) * iaw;
			Q.a21 -= ( g.gy * g.gz ) * iaw;

			Q.b0 -= ( g.gx * g.gw ) * iaw;
			Q.b1 -= ( g.gy * g.gw ) * iaw;
			Q.b2 -= ( g.gz * g.gw ) * iaw;
		}
	}

	private static void FillFaceQuadrics( Quadric[] vertexQuadrics, QuadricGrad[] volumeGradients, ReadOnlySpan<uint> indices, Vector3[] positions, uint[] remap )
	{
		for ( var i = 0; i < indices.Length; i += 3 )
		{
			var i0 = indices[i + 0];
			var i1 = indices[i + 1];
			var i2 = indices[i + 2];

			QuadricFromTriangle( out var Q, in positions[i0], in positions[i1], in positions[i2], 1.0f );

			QuadricAdd( ref vertexQuadrics[remap[i0]], in Q );
			QuadricAdd( ref vertexQuadrics[remap[i1]], in Q );
			QuadricAdd( ref vertexQuadrics[remap[i2]], in Q );

			if ( volumeGradients is not null )
			{
				QuadricVolumeGradient( out var GV, in positions[i0], in positions[i1], in positions[i2] );

				QuadricAdd( ref volumeGradients[remap[i0]], in GV );
				QuadricAdd( ref volumeGradients[remap[i1]], in GV );
				QuadricAdd( ref volumeGradients[remap[i2]], in GV );
			}
		}
	}

	private static void FillVertexQuadrics( Quadric[] vertexQuadrics, Vector3[] positions, int vertexCount, uint[] remap )
	{
		//A very small point quadric per vertex: better triangulation and numerical stability, without touching the shape or the error.
		const float factor = 1e-7f;

		for ( var i = 0; i < vertexCount; ++i )
		{
			if ( remap[i] != i ) continue;

			ref readonly var p = ref positions[i];
			var w = vertexQuadrics[i].w * factor;

			QuadricFromPoint( out var Q, p.x, p.y, p.z, w );

			QuadricAdd( ref vertexQuadrics[i], in Q );
		}
	}

	private static readonly int[] nextCorner = { 1, 2, 0, 1 };

	private static void FillEdgeQuadrics( Quadric[] vertexQuadrics, ReadOnlySpan<uint> indices, Vector3[] positions, uint[] remap,
		byte[] vertexKind, uint[] loop, uint[] loopback )
	{
		for ( var i = 0; i < indices.Length; i += 3 )
		{
			for ( var e = 0; e < 3; ++e )
			{
				var i0 = indices[i + e];
				var i1 = indices[i + nextCorner[e]];

				var k0 = vertexKind[i0];
				var k1 = vertexKind[i1];

				//A manifold vertex is never on a border.
				if ( k0 == KindManifold || k1 == KindManifold ) continue;

				//One end has to be border, seam or fringe; an edge from a border to a lock still counts, or corners lose their error.
				if ( k0 != KindBorder && k0 != KindSeam && k0 != KindFringe && k1 != KindBorder && k1 != KindSeam && k1 != KindFringe ) continue;

				//A border or a seam on the same loop.
				if ( ( k0 == KindBorder || k0 == KindSeam ) && loop[i0] != i1 ) continue;
				if ( ( k1 == KindBorder || k1 == KindSeam ) && loopback[i1] != i0 ) continue;

				//A fringe vertex has no loop of its own: its edges to other fringe vertices and to locks count, those into the surface do not.
				if ( ( k0 == KindFringe || k1 == KindFringe ) && ( k0 == KindComplex || k1 == KindComplex ) ) continue;

				var i2 = indices[i + nextCorner[e + 1]];

				//A border's geometry is held hard; a seam may move more freely.
				const float edgeWeightSeam = 0.5f;
				const float edgeWeightBorder = 10.0f;

				var edgeWeight = ( k0 == KindSeam || k1 == KindSeam ) ? edgeWeightSeam : edgeWeightBorder;

				QuadricFromTriangleEdge( out var Q, in positions[i0], in positions[i1], in positions[i2], edgeWeight );
				QuadricFromTriangle( out var QT, in positions[i0], in positions[i1], in positions[i2], edgeWeight );

				//Mixed with the triangle's plane, so a collapse either way is stable.
				QT.w = 0;
				QuadricAdd( ref Q, in QT );

				QuadricAdd( ref vertexQuadrics[remap[i0]], in Q );
				QuadricAdd( ref vertexQuadrics[remap[i1]], in Q );
			}
		}
	}

	//The third corner of the other triangle on edge a-b, through every copy of a at its point; Empty where there is none or more than one.
	private static uint OppositeVertex( in Adjacency adjacency, uint a, uint b, uint[] remap, uint[] wedge )
	{
		var v = a;
		var r = MeshHash.Empty;

		do
		{
			var start = (int)adjacency.Offsets[v];
			var end = (int)adjacency.Offsets[v + 1];

			for ( var i = start; i < end; ++i )
				if ( remap[adjacency.Data[i].Next] == remap[b] )
					r = r == MeshHash.Empty ? adjacency.Data[i].Prev : a;

			v = wedge[v];
		}
		while ( v != a );

		return r == a ? MeshHash.Empty : r;
	}

	//An edge quadric on every edge whose two triangles face nearly opposite ways - see Options.PreserveFolds.
	private static void FillFoldQuadrics( Quadric[] vertexQuadrics, in Adjacency adjacency, Vector3[] positions, int vertexCount, uint[] remap, uint[] wedge )
	{
		for ( var i0 = 0u; i0 < (uint)vertexCount; ++i0 )
		{
			var start = (int)adjacency.Offsets[i0];
			var end = (int)adjacency.Offsets[i0 + 1];

			for ( var i = start; i < end; ++i )
			{
				var i1 = adjacency.Data[i].Next;
				var i2 = adjacency.Data[i].Prev;

				//A paired edge comes twice; once is enough.
				if ( remap[i1] > remap[i0] ) continue;

				var i3 = OppositeVertex( in adjacency, i1, i0, remap, wedge );

				if ( i3 == MeshHash.Empty ) continue;

				ref readonly var p0 = ref positions[i0];
				ref readonly var p1 = ref positions[i1];
				ref readonly var p2 = ref positions[i2];
				ref readonly var p3 = ref positions[i3];

				float p10x = p1.x - p0.x, p10y = p1.y - p0.y, p10z = p1.z - p0.z;
				float p20x = p2.x - p0.x, p20y = p2.y - p0.y, p20z = p2.z - p0.z;
				float p30x = p3.x - p0.x, p30y = p3.y - p0.y, p30z = p3.z - p0.z;

				var nx = ( p10y * p20z ) - ( p10z * p20y );
				var ny = ( p10z * p20x ) - ( p10x * p20z );
				var nz = ( p10x * p20y ) - ( p10y * p20x );

				var ox = ( p30y * p10z ) - ( p30z * p10y );
				var oy = ( p30z * p10x ) - ( p30x * p10z );
				var oz = ( p30x * p10y ) - ( p30y * p10x );

				var nl = MathF.Sqrt( ( nx * nx ) + ( ny * ny ) + ( nz * nz ) );
				var ol = MathF.Sqrt( ( ox * ox ) + ( oy * oy ) + ( oz * oz ) );

				//A fold is the two within about thirty degrees of back to back.
				if ( ( nx * ox ) + ( ny * oy ) + ( nz * oz ) >= -0.85f * nl * ol ) continue;

				//Both triangles, for symmetry: the edge is not visited again.
				QuadricFromTriangleEdge( out var Q, in p0, in p1, in p2, 1.0f );
				QuadricFromTriangleEdge( out var QO, in p0, in p1, in p3, 1.0f );
				QuadricAdd( ref Q, in QO );

				QuadricAdd( ref vertexQuadrics[remap[i0]], in Q );
				QuadricAdd( ref vertexQuadrics[remap[i1]], in Q );
			}
		}
	}

	private static void FillAttributeQuadrics( Quadric[] attributeQuadrics, QuadricGrad[] attributeGradients, ReadOnlySpan<uint> indices,
		Vector3[] positions, float[] attributes, int attributeCount )
	{
		Span<QuadricGrad> G = stackalloc QuadricGrad[MaxAttributes];
		G = G[..attributeCount];

		for ( var i = 0; i < indices.Length; i += 3 )
		{
			var i0 = (int)indices[i + 0];
			var i1 = (int)indices[i + 1];
			var i2 = (int)indices[i + 2];

			QuadricFromAttributes( out var QA, G, in positions[i0], in positions[i1], in positions[i2],
				attributes.AsSpan( i0 * attributeCount, attributeCount ),
				attributes.AsSpan( i1 * attributeCount, attributeCount ),
				attributes.AsSpan( i2 * attributeCount, attributeCount ) );

			QuadricAdd( ref attributeQuadrics[i0], in QA );
			QuadricAdd( ref attributeQuadrics[i1], in QA );
			QuadricAdd( ref attributeQuadrics[i2], in QA );

			QuadricAdd( attributeGradients.AsSpan( i0 * attributeCount, attributeCount ), G );
			QuadricAdd( attributeGradients.AsSpan( i1 * attributeCount, attributeCount ), G );
			QuadricAdd( attributeGradients.AsSpan( i2 * attributeCount, attributeCount ), G );
		}
	}

	//-- what a collapse may not do -------------------------------------------------

	//Does triangle abc turn over when c becomes d?
	private static bool HasTriangleFlip( in Vector3 a, in Vector3 b, in Vector3 c, in Vector3 d )
	{
		float ebx = b.x - a.x, eby = b.y - a.y, ebz = b.z - a.z;
		float ecx = c.x - a.x, ecy = c.y - a.y, ecz = c.z - a.z;
		float edx = d.x - a.x, edy = d.y - a.y, edz = d.z - a.z;

		var nbcx = ( eby * ecz ) - ( ebz * ecy );
		var nbcy = ( ebz * ecx ) - ( ebx * ecz );
		var nbcz = ( ebx * ecy ) - ( eby * ecx );

		var nbdx = ( eby * edz ) - ( ebz * edy );
		var nbdy = ( ebz * edx ) - ( ebx * edz );
		var nbdz = ( ebx * edy ) - ( eby * edx );

		var ndp = ( nbcx * nbdx ) + ( nbcy * nbdy ) + ( nbcz * nbdz );
		var abc = ( nbcx * nbcx ) + ( nbcy * nbcy ) + ( nbcz * nbcz );
		var abd = ( nbdx * nbdx ) + ( nbdy * nbdy ) + ( nbdz * nbdz );

		//About seventy-five degrees rather than ninety, or a run of nearly square collapses turns a triangle over in steps.
		return ndp <= 0.25f * MathF.Sqrt( abc * abd );
	}

	private static bool HasTriangleFlips( in Adjacency adjacency, Vector3[] positions, uint[] collapseRemap, uint i0, uint i1 )
	{
		ref readonly var v0 = ref positions[i0];
		ref readonly var v1 = ref positions[i1];

		var start = (int)adjacency.Offsets[i0];
		var end = (int)adjacency.Offsets[i0 + 1];

		for ( var i = start; i < end; ++i )
		{
			var a = collapseRemap[adjacency.Data[i].Next];
			var b = collapseRemap[adjacency.Data[i].Prev];

			//Triangles this collapse removes, or an earlier one already did.
			if ( a == i1 || b == i1 || a == b ) continue;

			if ( HasTriangleFlip( in positions[a], in positions[b], in v0, in v1 ) ) return true;
		}

		return false;
	}

	//Does any triangle round a vertex turn over when the vertex goes to v1?
	private static bool HasTriangleFlips( in Adjacency adjacency, Vector3[] positions, uint i0, in Vector3 v1 )
	{
		ref readonly var v0 = ref positions[i0];

		var start = (int)adjacency.Offsets[i0];
		var end = (int)adjacency.Offsets[i0 + 1];

		for ( var i = start; i < end; ++i )
		{
			var a = adjacency.Data[i].Next;
			var b = adjacency.Data[i].Prev;

			if ( HasTriangleFlip( in positions[a], in positions[b], in v0, in v1 ) ) return true;
		}

		return false;
	}

	//How far the furthest corner of the triangles round a vertex stands from it.
	private static float NeighbourhoodRadius( in Adjacency adjacency, Vector3[] positions, uint i0 )
	{
		ref readonly var v0 = ref positions[i0];

		var start = (int)adjacency.Offsets[i0];
		var end = (int)adjacency.Offsets[i0 + 1];

		var result = 0.0f;

		for ( var i = start; i < end; ++i )
		{
			ref readonly var va = ref positions[adjacency.Data[i].Next];
			ref readonly var vb = ref positions[adjacency.Data[i].Prev];

			var da = ( ( va.x - v0.x ) * ( va.x - v0.x ) ) + ( ( va.y - v0.y ) * ( va.y - v0.y ) ) + ( ( va.z - v0.z ) * ( va.z - v0.z ) );
			var db = ( ( vb.x - v0.x ) * ( vb.x - v0.x ) ) + ( ( vb.y - v0.y ) * ( vb.y - v0.y ) ) + ( ( vb.z - v0.z ) * ( vb.z - v0.z ) );

			result = result < da ? da : result;
			result = result < db ? db : result;
		}

		return MathF.Sqrt( result );
	}

	//-- the collapses of one pass ---------------------------------------------------

	//Which copy of the target a copy of a complex vertex goes to: the one its own open half-edge already runs to, where it has
	//one - the loops track the half-edge with no pair across an attribute break, as for a seam - and the target itself otherwise.
	private static uint ComplexTarget( uint v, uint target, uint[] remap, uint[] loop, uint[] loopback )
	{
		var r = remap[target];

		if ( loop[v] != MeshHash.Empty && remap[loop[v]] == r ) return loop[v];
		if ( loopback[v] != MeshHash.Empty && remap[loopback[v]] == r ) return loopback[v];

		return target;
	}

	private static int BoundEdgeCollapses( in Adjacency adjacency, int vertexCount, int indexCount, byte[] vertexKind )
	{
		var dualCount = 0;

		for ( var i = 0; i < vertexCount; ++i )
		{
			var k = vertexKind[i];
			var e = (int)( adjacency.Offsets[i + 1] - adjacency.Offsets[i] );

			dualCount += ( k == KindManifold || k == KindSeam ) ? e : 0;
		}

		//Three spare, so the overflow check is once a triangle rather than once an edge.
		return ( indexCount - ( dualCount / 2 ) ) + 3;
	}

	private static int PickEdgeCollapses( Collapse[] collapses, ReadOnlySpan<uint> indices, uint[] remap, byte[] vertexKind, uint[] loop, uint[] loopback )
	{
		var collapseCount = 0;

		for ( var i = 0; i < indices.Length; i += 3 )
		{
			if ( collapseCount + 3 > collapses.Length ) break;

			for ( var e = 0; e < 3; ++e )
			{
				var i0 = indices[i + e];
				var i1 = indices[i + nextCorner[e]];

				//A zero-length edge, or a manifold vertex folded onto one of a seam's two: left alone, it may be holding the mesh together.
				if ( remap[i0] == remap[i1] ) continue;

				var k0 = vertexKind[i0];
				var k1 = vertexKind[i1];

				if ( ( canCollapse[k0, k1] | canCollapse[k1, k0] ) == 0 ) continue;

				//Manifold and seam edges come twice; the second is skipped.
				if ( hasOpposite[k0, k1] != 0 && remap[i1] > remap[i0] ) continue;

				//Two border or seam vertices with no edge between them are on different loops and must not be joined.
				if ( ( k0 == KindBorder || k0 == KindSeam ) && k1 != KindManifold && loop[i0] != i1 ) continue;
				if ( ( k1 == KindBorder || k1 == KindSeam ) && k0 != KindManifold && loopback[i1] != i0 ) continue;

				if ( ( canCollapse[k0, k1] & canCollapse[k1, k0] ) != 0 )
				{
					collapses[collapseCount++] = new Collapse { V0 = i0, V1 = i1, Bidi = true };
				}
				else
				{
					var e0 = canCollapse[k0, k1] != 0 ? i0 : i1;
					var e1 = canCollapse[k0, k1] != 0 ? i1 : i0;

					collapses[collapseCount++] = new Collapse { V0 = e0, V1 = e1, Bidi = false };
				}
			}
		}

		return collapseCount;
	}

	private static void RankEdgeCollapses( Span<Collapse> collapses, Vector3[] positions, float[] attributes, Quadric[] vertexQuadrics,
		Quadric[] attributeQuadrics, QuadricGrad[] attributeGradients, int attributeCount, uint[] remap, uint[] wedge,
		byte[] vertexKind, uint[] loop, uint[] loopback, bool clamped )
	{
		for ( var i = 0; i < collapses.Length; ++i )
		{
			ref var c = ref collapses[i];

			var i0 = c.V0;
			var i1 = c.V1;
			var bidi = c.Bidi;

			var ei = QuadricError( in vertexQuadrics[remap[i0]], in positions[i1] );
			var ej = bidi ? QuadricError( in vertexQuadrics[remap[i1]], in positions[i0] ) : float.MaxValue;

			if ( attributeCount != 0 )
			{
				ei += QuadricError( in attributeQuadrics[i0], attributeGradients.AsSpan( (int)i0 * attributeCount, attributeCount ),
					in positions[i1], attributes.AsSpan( (int)i1 * attributeCount, attributeCount ), clamped );
				ej += bidi ? QuadricError( in attributeQuadrics[i1], attributeGradients.AsSpan( (int)i1 * attributeCount, attributeCount ),
					in positions[i0], attributes.AsSpan( (int)i0 * attributeCount, attributeCount ), clamped ) : 0;

				//A seam's two edges have separate attribute quadrics; both count.
				if ( vertexKind[i0] == KindSeam )
				{
					var s0 = wedge[i0];
					var s1 = loop[i0] == i1 ? loopback[s0] : loop[s0];

					s1 = s1 != MeshHash.Empty ? s1 : wedge[i1];

					ei += QuadricError( in attributeQuadrics[s0], attributeGradients.AsSpan( (int)s0 * attributeCount, attributeCount ),
						in positions[s1], attributes.AsSpan( (int)s1 * attributeCount, attributeCount ), clamped );
					ej += bidi ? QuadricError( in attributeQuadrics[s1], attributeGradients.AsSpan( (int)s1 * attributeCount, attributeCount ),
						in positions[s0], attributes.AsSpan( (int)s0 * attributeCount, attributeCount ), clamped ) : 0;
				}
				else
				{
					//A complex vertex has a quadric a copy; each copy's error is against the copy of the target it would go to.
					if ( vertexKind[i0] == KindComplex || vertexKind[i0] == KindFringe )
					{
						for ( var v = wedge[i0]; v != i0; v = wedge[v] )
						{
							var t = ComplexTarget( v, i1, remap, loop, loopback );

							ei += QuadricError( in attributeQuadrics[v], attributeGradients.AsSpan( (int)v * attributeCount, attributeCount ),
								in positions[t], attributes.AsSpan( (int)t * attributeCount, attributeCount ), clamped );
						}
					}

					if ( ( vertexKind[i1] == KindComplex || vertexKind[i1] == KindFringe ) && bidi )
					{
						for ( var v = wedge[i1]; v != i1; v = wedge[v] )
						{
							var t = ComplexTarget( v, i0, remap, loop, loopback );

							ej += QuadricError( in attributeQuadrics[v], attributeGradients.AsSpan( (int)v * attributeCount, attributeCount ),
								in positions[t], attributes.AsSpan( (int)t * attributeCount, attributeCount ), clamped );
						}
					}
				}
			}

			var rev = bidi & ( ej < ei );

			c.V0 = rev ? i1 : i0;
			c.V1 = rev ? i0 : i1;
			c.Error = ej < ei ? ej : ei;
		}
	}

	private static void SortEdgeCollapses( uint[] sortOrder, ReadOnlySpan<Collapse> collapses )
	{
		//A counting sort on the top twelve bits of the error - eight of exponent, four of mantissa - which
		//is order enough. The exponent range is clamped; an error far above one is not worth ranking.
		const int sortBits = 12;
		const int sortBins = 2048 + 512;

		Span<uint> histogram = stackalloc uint[sortBins];
		histogram.Clear();

		for ( var i = 0; i < collapses.Length; ++i )
		{
			var error = BitConverter.SingleToUInt32Bits( collapses[i].Error );
			var key = ( error << 1 ) >> ( 32 - sortBits );
			key = key < sortBins ? key : sortBins - 1;

			histogram[(int)key]++;
		}

		var sum = 0u;

		for ( var i = 0; i < sortBins; ++i )
		{
			var count = histogram[i];
			histogram[i] = sum;
			sum += count;
		}

		for ( var i = 0; i < collapses.Length; ++i )
		{
			var error = BitConverter.SingleToUInt32Bits( collapses[i].Error );
			var key = ( error << 1 ) >> ( 32 - sortBits );
			key = key < sortBins ? key : sortBins - 1;

			sortOrder[histogram[(int)key]++] = (uint)i;
		}
	}

	private static int PerformEdgeCollapses( uint[] collapseRemap, byte[] collapseLocked, ReadOnlySpan<Collapse> collapses, uint[] collapseOrder,
		uint[] remap, uint[] wedge, byte[] vertexKind, uint[] loop, uint[] loopback, Vector3[] positions, in Adjacency adjacency,
		int triangleCollapseGoal, float errorLimit, ref float resultError )
	{
		var edgeCollapses = 0;
		var triangleCollapses = 0;

		//Most collapses take two triangles; the edge goal is an estimate, the triangle goal is what stops the pass.
		var edgeCollapseGoal = triangleCollapseGoal / 2;

		for ( var i = 0; i < collapses.Length; ++i )
		{
			ref readonly var c = ref collapses[(int)collapseOrder[i]];

			if ( c.Error > errorLimit ) break;

			if ( triangleCollapses >= triangleCollapseGoal ) break;

			//The pass's error is bounded by the error of the last collapse it would ideally make, with room for the ones that get locked out.
			var errorGoal = edgeCollapseGoal < collapses.Length ? 1.5f * collapses[(int)collapseOrder[edgeCollapseGoal]].Error : float.MaxValue;

			//Each collapse locks about six others; the pass gives up on the goal only once it has done a sixth of what it meant to.
			if ( c.Error > errorGoal && c.Error > resultError && triangleCollapses > triangleCollapseGoal / 6 ) break;

			var i0 = c.V0;
			var i1 = c.V1;

			var r0 = remap[i0];
			var r1 = remap[i1];

			var kind = vertexKind[i0];

			//Nothing moves twice in a pass, and nothing moves towards what moved: the ranking is not redone mid-pass.
			if ( ( collapseLocked[r0] | collapseLocked[r1] ) != 0 ) continue;

			if ( HasTriangleFlips( in adjacency, positions, collapseRemap, r0, r1 ) )
			{
				//Not a real collapse, so it should not count against the goal.
				edgeCollapseGoal++;
				continue;
			}

			if ( kind == KindComplex || kind == KindFringe )
			{
				collapseRemap[i0] = i1;

				//Every copy goes, each to the copy the ranking weighed it against.
				for ( var v = wedge[i0]; v != i0; v = wedge[v] )
					collapseRemap[v] = ComplexTarget( v, i1, remap, loop, loopback );
			}
			else if ( kind == KindSeam )
			{
				//The seam pair moves together, found through the loops since the target may be locked and have more than two wedges.
				var s0 = wedge[i0];
				var s1 = loop[i0] == i1 ? loopback[s0] : loop[s0];

				s1 = s1 != MeshHash.Empty ? s1 : wedge[i1];

				collapseRemap[i0] = i1;
				collapseRemap[s0] = s1;
			}
			else
			{
				collapseRemap[i0] = i1;
			}

			collapseLocked[r0] = 1;
			collapseLocked[r1] = 1;

			//A border edge takes one triangle, any other two.
			triangleCollapses += kind == KindBorder ? 1 : 2;
			edgeCollapses++;

			resultError = resultError < c.Error ? c.Error : resultError;
		}

		return edgeCollapses;
	}

	private static void UpdateQuadrics( uint[] collapseRemap, int vertexCount, Quadric[] vertexQuadrics, QuadricGrad[] volumeGradients,
		Quadric[] attributeQuadrics, QuadricGrad[] attributeGradients, int attributeCount, Vector3[] positions, uint[] remap, ref float vertexError )
	{
		for ( var i = 0; i < vertexCount; ++i )
		{
			if ( collapseRemap[i] == i ) continue;

			var i0 = (uint)i;
			var i1 = collapseRemap[i];

			var r0 = remap[i0];
			var r1 = remap[i1];

			//Once per position: the primary vertex moves if any wedge does.
			if ( i0 == r0 )
			{
				QuadricAdd( ref vertexQuadrics[r1], in vertexQuadrics[r0] );

				if ( volumeGradients is not null ) QuadricAdd( ref volumeGradients[r1], in volumeGradients[r0] );
			}

			if ( attributeCount != 0 )
			{
				QuadricAdd( ref attributeQuadrics[i1], in attributeQuadrics[i0] );
				QuadricAdd( attributeGradients.AsSpan( (int)i1 * attributeCount, attributeCount ),
					attributeGradients.AsSpan( (int)i0 * attributeCount, attributeCount ) );

				//With attributes the distance error is not what the collapse tracked, so it is measured again here.
				if ( i0 == r0 )
				{
					var derr = QuadricError( in vertexQuadrics[r0], in positions[r1] );
					vertexError = vertexError < derr ? derr : vertexError;
				}
			}
		}
	}

	private static int RemapIndexBuffer( Span<uint> indices, uint[] collapseRemap, uint[] remap )
	{
		var write = 0;

		for ( var i = 0; i < indices.Length; i += 3 )
		{
			var v0 = collapseRemap[indices[i + 0]];
			var v1 = collapseRemap[indices[i + 1]];
			var v2 = collapseRemap[indices[i + 2]];

			//Zero-area triangles go even when not topologically degenerate: a manifold vertex folded onto a seam pair leaves them behind.
			var r0 = remap[v0];
			var r1 = remap[v1];
			var r2 = remap[v2];

			if ( r0 != r1 && r0 != r2 && r1 != r2 )
			{
				indices[write + 0] = v0;
				indices[write + 1] = v1;
				indices[write + 2] = v2;
				write += 3;
			}
		}

		return write;
	}

	private static void RemapEdgeLoops( uint[] loop, int vertexCount, uint[] collapseRemap )
	{
		for ( var i = 0; i < vertexCount; ++i )
		{
			if ( loop[i] == MeshHash.Empty ) continue;

			var l = loop[i];
			var r = collapseRemap[l];

			//i == r is a seam edge collapsed against the way its loop runs.
			if ( i == r )
				loop[i] = loop[l] != MeshHash.Empty ? collapseRemap[loop[l]] : MeshHash.Empty;
			else
				loop[i] = r;
		}
	}

	//-- solving what is left ---------------------------------------------------------

	//Moves each free vertex to its quadric's minimum, preserving volume; seam and border vertices stay
	private static void SolvePositions( Vector3[] positions, int vertexCount, Quadric[] vertexQuadrics, QuadricGrad[] volumeGradients,
		Quadric[] attributeQuadrics, QuadricGrad[] attributeGradients, int attributeCount, uint[] remap, uint[] wedge,
		in Adjacency adjacency, byte[] vertexKind, byte[] vertexUpdate, byte[] moved )
	{
		for ( var i = 0; i < vertexCount; ++i )
		{
			if ( vertexUpdate[i] == 0 ) continue;
			if ( vertexKind[i] != KindManifold && vertexKind[i] != KindComplex ) continue;

			//A copy stands where the first of its point was put - which has been solved by now, the first being the lower number.
			if ( remap[i] != i )
			{
				positions[i] = positions[remap[i]];
				moved[i] = moved[remap[i]];
				continue;
			}

			var vp = positions[i];

			var Q = vertexQuadrics[i];
			QuadricGrad GV = default;

			//A little of the point itself, which steadies a quadric that a flat neighbourhood leaves free to slide.
			QuadricFromPoint( out var R, vp.x, vp.y, vp.z, Q.w * 1e-4f );
			QuadricAdd( ref Q, in R );

			if ( attributeCount != 0 )
			{
				//Least for every wedge's attributes at once.
				var v = (uint)i;

				do
				{
					QuadricReduceAttributes( ref Q, in attributeQuadrics[v], attributeGradients.AsSpan( (int)v * attributeCount, attributeCount ) );
					v = wedge[v];
				}
				while ( v != i );

				//Which on its own loses volume, so the volume is held.
				if ( volumeGradients is not null ) GV = volumeGradients[i];
			}

			if ( !QuadricSolve( out var p, in Q, in GV ) ) continue;
			if ( float.IsNaN( p.x ) || float.IsNaN( p.y ) || float.IsNaN( p.z ) ) continue;

			//Not out of its own neighbourhood - a quadric that is not well defined sends the point anywhere.
			var nr = NeighbourhoodRadius( in adjacency, positions, (uint)i );
			var dp = ( ( p.x - vp.x ) * ( p.x - vp.x ) ) + ( ( p.y - vp.y ) * ( p.y - vp.y ) ) + ( ( p.z - vp.z ) * ( p.z - vp.z ) );

			if ( dp > nr * nr ) continue;

			//Not through a neighbour, as for a collapse.
			if ( HasTriangleFlips( in adjacency, positions, (uint)i, in p ) ) continue;

			//And not much further from the surface than it was: some, for the attributes' sake.
			if ( QuadricError( in vertexQuadrics[i], in p ) > ( QuadricError( in vertexQuadrics[i], in vp ) * 1.5f ) + 1e-6f ) continue;

			positions[i] = p;
			moved[i] = 1;
		}
	}

	//The attributes of each vertex that is left, read off its quadric at the place the vertex now stands:
	//the mean of what the folded triangles carried there, not what one of their corners happened to.
	private static void SolveAttributes( Vector3[] positions, float[] attributes, int vertexCount, Quadric[] attributeQuadrics,
		QuadricGrad[] attributeGradients, int attributeCount, uint[] remap, uint[] wedge, byte[] vertexKind, byte[] vertexUpdate )
	{
		for ( var i = 0; i < vertexCount; ++i )
		{
			if ( vertexUpdate[i] == 0 ) continue;
			if ( remap[i] != i ) continue;
			if ( vertexKind[i] == KindLocked ) continue;

			//The same place for every wedge.
			ref readonly var p = ref positions[i];

			var complex = vertexKind[i] == KindComplex || vertexKind[i] == KindFringe;

			for ( var k = 0; k < attributeCount; ++k )
			{
				//Where the copies of a complex vertex agreed on an attribute they go on agreeing: all take the answer of the heaviest of them.
				var shared = MeshHash.Empty;

				if ( complex )
				{
					shared = (uint)i;

					for ( var w = wedge[i]; w != i; w = wedge[w] )
					{
						if ( attributes[( (int)w * attributeCount ) + k] != attributes[( i * attributeCount ) + k] )
							shared = MeshHash.Empty;
						else if ( shared != MeshHash.Empty && attributeQuadrics[w].w > attributeQuadrics[shared].w )
							shared = w;
					}
				}

				var v = (uint)i;

				do
				{
					var r = shared == MeshHash.Empty ? v : shared;

					ref readonly var A = ref attributeQuadrics[r];

					//Nothing was folded into a wedge with no area; it keeps what it had.
					if ( A.w != 0 )
					{
						ref readonly var G = ref attributeGradients[( (int)r * attributeCount ) + k];

						attributes[( (int)v * attributeCount ) + k] = ( ( G.gx * p.x ) + ( G.gy * p.y ) + ( G.gz * p.z ) + G.gw ) / A.w;
					}

					v = wedge[v];
				}
				while ( v != i );
			}
		}
	}

	//-- pruning: whole pieces small enough to disappear -----------------------------

	private static uint Follow( uint[] parents, uint index )
	{
		while ( index != parents[index] )
		{
			var parent = parents[index];
			parents[index] = parents[parent];
			index = parent;
		}

		return index;
	}

	private static int BuildComponents( uint[] components, int vertexCount, ReadOnlySpan<uint> indices, uint[] remap )
	{
		for ( var i = 0; i < vertexCount; ++i ) components[i] = (uint)i;

		//Union-find, always into the smaller index, so a root is the least of its component.
		for ( var i = 0; i < indices.Length; i += 3 )
		{
			for ( var e = 0; e < 3; ++e )
			{
				var i0 = indices[i + e];
				var i1 = indices[i + nextCorner[e]];

				var r0 = Follow( components, remap[i0] );
				var r1 = Follow( components, remap[i1] );

				if ( r0 != r1 ) components[r0 < r1 ? r1 : r0] = r0 < r1 ? r0 : r1;
			}
		}

		for ( var i = 0; i < vertexCount; ++i )
			if ( remap[i] == i )
				components[i] = Follow( components, (uint)i );

		var nextComponent = 0u;

		//Renumbered in one pass, which works because a root has the least index.
		for ( var i = 0; i < vertexCount; ++i )
		{
			if ( remap[i] == i )
			{
				var root = components[i];
				components[i] = root == i ? nextComponent++ : components[root];
			}
			else
			{
				components[i] = components[remap[i]];
			}
		}

		return (int)nextComponent;
	}

	private static void MeasureComponents( float[] componentErrors, int componentCount, uint[] components, Vector3[] positions, int vertexCount )
	{
		System.Array.Clear( componentErrors, 0, componentCount * 4 );

		for ( var i = 0; i < vertexCount; ++i )
		{
			var c = (int)components[i];
			var v = positions[i];

			componentErrors[( c * 4 ) + 0] += v.x;
			componentErrors[( c * 4 ) + 1] += v.y;
			componentErrors[( c * 4 ) + 2] += v.z;
			componentErrors[( c * 4 ) + 3] += 1;
		}

		for ( var i = 0; i < componentCount; ++i )
		{
			var w = componentErrors[( i * 4 ) + 3];
			var iw = w == 0.0f ? 0.0f : 1.0f / w;

			componentErrors[( i * 4 ) + 0] *= iw;
			componentErrors[( i * 4 ) + 1] *= iw;
			componentErrors[( i * 4 ) + 2] *= iw;
			componentErrors[( i * 4 ) + 3] = 0;
		}

		for ( var i = 0; i < vertexCount; ++i )
		{
			var c = (int)components[i];

			var dx = positions[i].x - componentErrors[( c * 4 ) + 0];
			var dy = positions[i].y - componentErrors[( c * 4 ) + 1];
			var dz = positions[i].z - componentErrors[( c * 4 ) + 2];
			var r = ( dx * dx ) + ( dy * dy ) + ( dz * dz );

			componentErrors[( c * 4 ) + 3] = componentErrors[( c * 4 ) + 3] < r ? r : componentErrors[( c * 4 ) + 3];
		}

		//Squared, to match the quadric error.
		for ( var i = 0; i < componentCount; ++i )
			componentErrors[i] = componentErrors[( i * 4 ) + 3];
	}

	//Drops the triangles of every component no bigger than the cutoff; the biggest dropped goes into prunedError, as a piece
	//taken away is that much error, or a level that lost whole clumps would say it was off by less than a pixel
	private static int PruneComponents( Span<uint> indices, uint[] components, float[] componentErrors, float errorCutoff, ref float nextError,
		ref float prunedError )
	{
		var write = 0;
		var minError = float.MaxValue;

		for ( var i = 0; i < indices.Length; i += 3 )
		{
			uint v0 = indices[i + 0], v1 = indices[i + 1], v2 = indices[i + 2];
			var c = (int)components[v0];

			if ( componentErrors[c] <= errorCutoff )
			{
				prunedError = prunedError < componentErrors[c] ? componentErrors[c] : prunedError;
				continue;
			}

			minError = minError > componentErrors[c] ? componentErrors[c] : minError;

			indices[write + 0] = v0;
			indices[write + 1] = v1;
			indices[write + 2] = v2;
			write += 3;
		}

		nextError = minError;
		return write;
	}

	//-- the whole ------------------------------------------------------------------------

	/// <summary>Simplifies indices into destination (may alias); returns index count and the achieved error</summary>
	public static int Simplify( Span<uint> destination, ReadOnlySpan<uint> indices, ReadOnlySpan<Vector3> positionsIn,
		ReadOnlySpan<float> attributes, int attributeStride, ReadOnlySpan<float> attributeWeights,
		int targetIndexCount, float targetError, Options options, out float resultError, uint protectMask = 0 )
		=> Run( destination, indices, positionsIn, attributes, attributeStride, attributeWeights,
			targetIndexCount, targetError, options, protectMask, default, default, default, default, out resultError );

	/// <summary>Simplify, then solve remaining vertices' positions and attributes in place and flag them in changed</summary>
	public static int SimplifyWithUpdate( Span<uint> indices, Span<Vector3> positions, Span<float> attributes, int attributeStride,
		ReadOnlySpan<float> attributeWeights, int targetIndexCount, float targetError, Options options,
		Span<byte> changed, out float resultError, uint protectMask = 0, ReadOnlySpan<byte> locks = default )
	{
		changed.Clear();

		return Run( indices, indices, positions, attributes, attributeStride, attributeWeights,
			targetIndexCount, targetError, options, protectMask, locks, positions, attributes, changed, out resultError );
	}

	private static int Run( Span<uint> destination, ReadOnlySpan<uint> indices, ReadOnlySpan<Vector3> positionsIn,
		ReadOnlySpan<float> attributes, int attributeStride, ReadOnlySpan<float> attributeWeights,
		int targetIndexCount, float targetError, Options options, uint protectMask, ReadOnlySpan<byte> locks,
		Span<Vector3> positionsOut, Span<float> attributesOut, Span<byte> changed, out float resultError )
	{
		var solve = !changed.IsEmpty;
		var indexCount = indices.Length;
		var vertexCount = positionsIn.Length;

		var result = destination[..indexCount];
		if ( !indices.Overlaps( result ) ) indices.CopyTo( result );

		var adjacency = new Adjacency { Offsets = new uint[vertexCount + 1], Data = new Edge[indexCount] };
		UpdateAdjacency( ref adjacency, result, vertexCount, null );

		var remap = new uint[vertexCount];
		var wedge = new uint[vertexCount];

		var positions = new Vector3[vertexCount];
		positionsIn.CopyTo( positions );

		BuildPositionRemap( remap, wedge, positions );

		var vertexKind = new byte[vertexCount];
		var loop = new uint[vertexCount];
		var loopback = new uint[vertexCount];
		//Which copies differ from the first at their point in something protected.
		byte[] protect = null;

		if ( ( options & Options.Permissive ) != 0 && protectMask != 0 )
		{
			protect = new byte[vertexCount];

			for ( var i = 0; i < vertexCount; ++i )
			{
				var r = (int)remap[i];
				if ( r == i ) continue;

				for ( var k = 0; k < attributeStride && k < 32; ++k )
				{
					if ( ( protectMask & ( 1u << k ) ) != 0 && attributes[( i * attributeStride ) + k] != attributes[( r * attributeStride ) + k] )
					{
						protect[i] = 1;
						break;
					}
				}
			}
		}

		ClassifyVertices( vertexKind, loop, loopback, vertexCount, in adjacency, remap, wedge, protect, locks, options );

		var vertexScale = RescalePositions( positions, out var corner );

		//Only attributes with weight; the rest cost quadric space for nothing.
		Span<uint> attributeRemap = stackalloc uint[MaxAttributes];
		var attributeCount = 0;

		for ( var i = 0; i < attributeWeights.Length; ++i )
			if ( attributeWeights[i] > 0 )
				attributeRemap[attributeCount++] = (uint)i;

		float[] scaledAttributes = null;

		if ( attributeCount != 0 )
		{
			scaledAttributes = new float[vertexCount * attributeCount];
			RescaleAttributes( scaledAttributes, attributes, vertexCount, attributeStride, attributeWeights, attributeRemap[..attributeCount] );
		}

		var vertexQuadrics = new Quadric[vertexCount];

		Quadric[] attributeQuadrics = null;
		QuadricGrad[] attributeGradients = null;

		if ( attributeCount != 0 )
		{
			attributeQuadrics = new Quadric[vertexCount];
			attributeGradients = new QuadricGrad[vertexCount * attributeCount];
		}

		//The volume round each vertex, where they are to be solved with their attributes - which pull a vertex in off the surface.
		var volumeGradients = solve && attributeCount != 0 ? new QuadricGrad[vertexCount] : null;

		FillFaceQuadrics( vertexQuadrics, volumeGradients, result, positions, remap );
		FillVertexQuadrics( vertexQuadrics, positions, vertexCount, remap );
		FillEdgeQuadrics( vertexQuadrics, result, positions, remap, vertexKind, loop, loopback );

		if ( ( options & Options.PreserveFolds ) != 0 )
			FillFoldQuadrics( vertexQuadrics, in adjacency, positions, vertexCount, remap, wedge );

		if ( attributeCount != 0 )
			FillAttributeQuadrics( attributeQuadrics, attributeGradients, result, positions, scaledAttributes, attributeCount );

		uint[] components = null;
		float[] componentErrors = null;
		var componentCount = 0;
		var componentNextError = 0.0f;

		if ( ( options & Options.Prune ) != 0 )
		{
			components = new uint[vertexCount];
			componentCount = BuildComponents( components, vertexCount, result, remap );

			componentErrors = new float[componentCount * 4];
			MeasureComponents( componentErrors, componentCount, components, positions, vertexCount );

			componentNextError = float.MaxValue;
			for ( var i = 0; i < componentCount; ++i )
				componentNextError = componentNextError > componentErrors[i] ? componentErrors[i] : componentNextError;
		}

		var collapseCapacity = BoundEdgeCollapses( in adjacency, vertexCount, indexCount, vertexKind );

		var edgeCollapses = new Collapse[collapseCapacity];
		var collapseOrder = new uint[collapseCapacity];
		var collapseRemap = new uint[vertexCount];
		var collapseLocked = new byte[vertexCount];

		var resultCount = indexCount;
		var error = 0.0f;
		var vertexError = 0.0f;

		//The target is linear; the quadric's is squared.
		var errorScale = ( options & Options.ErrorAbsolute ) != 0 ? vertexScale : 1.0f;
		var errorLimit = ( targetError * targetError ) / ( errorScale * errorScale );

		while ( resultCount > targetIndexCount )
		{
			//The adjacency follows the welded topology of the result so far.
			UpdateAdjacency( ref adjacency, result[..resultCount], vertexCount, remap );

			var edgeCollapseCount = PickEdgeCollapses( edgeCollapses, result[..resultCount], remap, vertexKind, loop, loopback );

			if ( edgeCollapseCount == 0 ) break;

			var collapses = edgeCollapses.AsSpan( 0, edgeCollapseCount );

			RankEdgeCollapses( collapses, positions, scaledAttributes, vertexQuadrics, attributeQuadrics, attributeGradients,
				attributeCount, remap, wedge, vertexKind, loop, loopback, ( options & Options.ErrorClamped ) != 0 );

			SortEdgeCollapses( collapseOrder, collapses );

			var triangleCollapseGoal = ( resultCount - targetIndexCount ) / 3;

			for ( var i = 0; i < vertexCount; ++i ) collapseRemap[i] = (uint)i;
			System.Array.Clear( collapseLocked, 0, vertexCount );

			var made = PerformEdgeCollapses( collapseRemap, collapseLocked, collapses, collapseOrder, remap, wedge, vertexKind,
				loop, loopback, positions, in adjacency, triangleCollapseGoal, errorLimit, ref error );

			if ( made == 0 ) break;

			UpdateQuadrics( collapseRemap, vertexCount, vertexQuadrics, volumeGradients, attributeQuadrics, attributeGradients, attributeCount,
				positions, remap, ref vertexError );

			vertexError = attributeCount == 0 ? error : vertexError;

			//Loops go stale where a loop edge's triangle was taken along a non-loop edge,
			//which is harmless: a loop into a vertex nothing connects to decides nothing.
			RemapEdgeLoops( loop, vertexCount, collapseRemap );
			RemapEdgeLoops( loopback, vertexCount, collapseRemap );

			resultCount = RemapIndexBuffer( result[..resultCount], collapseRemap, remap );

			if ( ( options & Options.Prune ) != 0 && resultCount > targetIndexCount && componentNextError <= vertexError )
			{
				var pruned = 0.0f;
				resultCount = PruneComponents( result[..resultCount], components, componentErrors, vertexError, ref componentNextError, ref pruned );
				error = error < pruned ? pruned : error;
			}
		}

		//Short of the target with nothing left to collapse: prune harder, up to the error limit.
		var componentNextStale = true;

		while ( ( options & Options.Prune ) != 0 && resultCount > targetIndexCount && componentNextError <= errorLimit )
		{
			var componentCutoff = componentNextError * 1.5f < errorLimit ? componentNextError * 1.5f : errorLimit;

			var componentMaxError = 0.0f;
			var newCount = PruneComponents( result[..resultCount], components, componentErrors, componentCutoff, ref componentNextError,
				ref componentMaxError );
			if ( newCount == resultCount && !componentNextStale ) break;

			componentNextStale = false;
			resultCount = newCount;
			error = error < componentMaxError ? componentMaxError : error;
			vertexError = vertexError < componentMaxError ? componentMaxError : vertexError;
		}

		if ( solve )
		{
			//Only what the result still uses, and the first of each position for the solving.
			var vertexUpdate = collapseLocked;
			System.Array.Clear( vertexUpdate, 0, vertexCount );

			for ( var i = 0; i < resultCount; ++i )
			{
				var v = result[i];
				vertexUpdate[remap[v]] = vertexUpdate[v] = 1;
			}

			//Stale after the last pass of collapses.
			UpdateAdjacency( ref adjacency, result[..resultCount], vertexCount, remap );

			var moved = new byte[vertexCount];

			SolvePositions( positions, vertexCount, vertexQuadrics, volumeGradients, attributeQuadrics, attributeGradients, attributeCount,
				remap, wedge, in adjacency, vertexKind, vertexUpdate, moved );

			if ( attributeCount != 0 )
				SolveAttributes( positions, scaledAttributes, vertexCount, attributeQuadrics, attributeGradients, attributeCount,
					remap, wedge, vertexKind, vertexUpdate );

			//Back to the caller's numbers. A position only where it moved: the way into the unit
			//cube and out again is not exact, and a vertex that stayed should stay to the bit.
			for ( var i = 0; i < vertexCount; ++i )
			{
				if ( vertexUpdate[i] == 0 || vertexKind[i] == KindLocked ) continue;

				if ( moved[i] != 0 )
				{
					ref readonly var at = ref positions[i];

					positionsOut[i] = new Vector3( ( at.x * vertexScale ) + corner.x, ( at.y * vertexScale ) + corner.y, ( at.z * vertexScale ) + corner.z );
					changed[i] = 1;
				}

				if ( attributeCount != 0 )
				{
					for ( var k = 0; k < attributeCount; ++k )
					{
						var rk = (int)attributeRemap[k];

						attributesOut[( i * attributeStride ) + rk] = scaledAttributes[( i * attributeCount ) + k] / attributeWeights[rk];
					}

					changed[i] = 1;
				}
			}
		}

		//Back from squared to linear, and into the caller's units.
		resultError = MathF.Sqrt( error ) * errorScale;

		return resultCount;
	}
}
