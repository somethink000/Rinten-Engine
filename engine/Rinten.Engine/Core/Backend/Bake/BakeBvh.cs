using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vec3 = System.Numerics.Vector3;

namespace Rinten.Bake;

//What a ray of the bake found: how far along, which triangle, where on it, and whether it came at it from behind.
internal struct BakeHit
{
	public float Distance;
	public int Triangle;
	public float U, V;
	public bool Back;
}

//CPU BVH over all static triangles for bake rays: binned SAH, 32-byte nodes, leaf triangle runs
internal sealed class BakeBvh
{
	private struct Node
	{
		public Vec3 Min;

		/// <summary>The first child - the second is the one after it - or a leaf's first triangle.</summary>
		public int First;

		public Vec3 Max;

		/// <summary>How many triangles a leaf holds; nought for a node with children.</summary>
		public int Count;
	}

	private const int Bins = 16;
	private const int LeafMost = 4;

	//Below this a subtree is built where it is rather than handed to another core.
	private const int ParallelLeast = 1 << 14;

	private Node[] nodes = Array.Empty<Node>();
	private int nodesUsed;

	//A triangle as the intersection reads it, in the tree's order.
	private Vec3[] corner = Array.Empty<Vec3>();
	private Vec3[] edge1 = Array.Empty<Vec3>();
	private Vec3[] edge2 = Array.Empty<Vec3>();

	//Which triangle of the world each of the tree's is.
	private int[] source = Array.Empty<int>();

	//Whether a triangle of the world lets a ray through in places - see Through.
	private bool[] tested = Array.Empty<bool>();

	//Asked of a triangle that is not solid all over - a leaf cut out of a square - whether the ray goes through it here.
	internal delegate bool Through( int triangle, float u, float v );

	//How much of a ray gets by a world triangle it crosses, by whether it is solid all over - see BakeScene.Veil.
	internal delegate Vec3 Veil( int triangle, float u, float v, bool tested, Vec3 direction );

	private Through passes;

	public int TriangleCount => source.Length;

	//How many nodes the tree has - see PackNodes.
	internal int NodeCount => source.Length == 0 ? 0 : nodesUsed;

	//The nodes as the card reads them - see bake_trace.slang: eight words each, the low corner and the first child or
	//triangle, the high corner and how many triangles. The layout they have here, which is why it is thirty-two bytes.
	internal void PackNodes( Span<uint> into )
	{
		var words = System.Runtime.InteropServices.MemoryMarshal.Cast<Node, uint>( nodes.AsSpan( 0, NodeCount ) );
		words.CopyTo( into );
	}

	//The triangles in the tree's order, as the card reads them: a corner and two edges, then which triangle
	//of the world it is with the top bit set where it is not solid all over - see Through. Ten words each.
	internal void PackTriangles( Span<uint> into )
	{
		for ( var t = 0; t < source.Length; ++t )
		{
			var at = t * 10;

			Put( into, at, corner[t] );
			Put( into, at + 3, edge1[t] );
			Put( into, at + 6, edge2[t] );

			into[at + 9] = (uint)source[t] | ( tested[t] ? 0x80000000u : 0u );
		}
	}

	private static void Put( Span<uint> into, int at, Vec3 v )
	{
		into[at] = BitConverter.SingleToUInt32Bits( v.X );
		into[at + 1] = BitConverter.SingleToUInt32Bits( v.Y );
		into[at + 2] = BitConverter.SingleToUInt32Bits( v.Z );
	}

	/// <summary>Builds the tree over a soup of triangles: three positions each, in order.</summary>
	public void Build( Vec3[] positions, bool[] alphaTested, Through through )
	{
		var count = positions.Length / 3;

		passes = through;

		source = new int[count];
		var centres = new Vec3[count];
		var lows = new Vec3[count];
		var highs = new Vec3[count];

		for ( var t = 0; t < count; ++t )
		{
			var a = positions[t * 3];
			var b = positions[( t * 3 ) + 1];
			var c = positions[( t * 3 ) + 2];

			source[t] = t;
			lows[t] = Vec3.Min( a, Vec3.Min( b, c ) );
			highs[t] = Vec3.Max( a, Vec3.Max( b, c ) );
			centres[t] = ( lows[t] + highs[t] ) * 0.5f;
		}

		nodes = new Node[Math.Max( count * 2, 2 )];
		nodesUsed = 1;

		if ( count > 0 ) Split( 0, 0, count, centres, lows, highs );
		else nodes[0] = new Node { Min = Vec3.Zero, Max = Vec3.Zero, First = 0, Count = 0 };

		corner = new Vec3[count];
		edge1 = new Vec3[count];
		edge2 = new Vec3[count];
		tested = new bool[count];

		for ( var t = 0; t < count; ++t )
		{
			var from = source[t] * 3;

			corner[t] = positions[from];
			edge1[t] = positions[from + 1] - positions[from];
			edge2[t] = positions[from + 2] - positions[from];
			tested[t] = alphaTested is not null && alphaTested[source[t]];
		}
	}

	private void Split( int node, int first, int count, Vec3[] centres, Vec3[] lows, Vec3[] highs )
	{
		var min = new Vec3( float.MaxValue );
		var max = new Vec3( float.MinValue );
		var centreMin = new Vec3( float.MaxValue );
		var centreMax = new Vec3( float.MinValue );

		for ( var i = first; i < first + count; ++i )
		{
			var t = source[i];

			min = Vec3.Min( min, lows[t] );
			max = Vec3.Max( max, highs[t] );
			centreMin = Vec3.Min( centreMin, centres[t] );
			centreMax = Vec3.Max( centreMax, centres[t] );
		}

		nodes[node].Min = min;
		nodes[node].Max = max;

		if ( count <= LeafMost )
		{
			nodes[node].First = first;
			nodes[node].Count = count;
			return;
		}

		//The cheapest of fifteen planes on each of three axes, by what a ray through
		//this box would then cost: each side's area times how many triangles are in it.
		var bestAxis = -1;
		var bestPlane = 0;
		var bestCost = float.MaxValue;

		Span<int> binCount = stackalloc int[Bins];
		Span<Vec3> binMin = stackalloc Vec3[Bins];
		Span<Vec3> binMax = stackalloc Vec3[Bins];
		Span<float> leftArea = stackalloc float[Bins];
		Span<int> leftCount = stackalloc int[Bins];

		for ( var axis = 0; axis < 3; ++axis )
		{
			var low = Axis( centreMin, axis );
			var span = Axis( centreMax, axis ) - low;

			if ( span < 1e-9f ) continue;

			var scale = Bins / span;

			for ( var b = 0; b < Bins; ++b )
			{
				binCount[b] = 0;
				binMin[b] = new Vec3( float.MaxValue );
				binMax[b] = new Vec3( float.MinValue );
			}

			for ( var i = first; i < first + count; ++i )
			{
				var t = source[i];
				var b = Math.Min( (int)( ( Axis( centres[t], axis ) - low ) * scale ), Bins - 1 );

				binCount[b]++;
				binMin[b] = Vec3.Min( binMin[b], lows[t] );
				binMax[b] = Vec3.Max( binMax[b], highs[t] );
			}

			var runMin = new Vec3( float.MaxValue );
			var runMax = new Vec3( float.MinValue );
			var run = 0;

			for ( var b = 0; b < Bins - 1; ++b )
			{
				run += binCount[b];
				runMin = Vec3.Min( runMin, binMin[b] );
				runMax = Vec3.Max( runMax, binMax[b] );

				leftCount[b] = run;
				leftArea[b] = run > 0 ? Area( runMin, runMax ) : 0.0f;
			}

			runMin = new Vec3( float.MaxValue );
			runMax = new Vec3( float.MinValue );
			run = 0;

			for ( var b = Bins - 1; b > 0; --b )
			{
				run += binCount[b];
				runMin = Vec3.Min( runMin, binMin[b] );
				runMax = Vec3.Max( runMax, binMax[b] );

				if ( run == 0 || leftCount[b - 1] == 0 ) continue;

				var cost = ( leftArea[b - 1] * leftCount[b - 1] ) + ( Area( runMin, runMax ) * run );

				if ( cost < bestCost )
				{
					bestCost = cost;
					bestAxis = axis;
					bestPlane = b;
				}
			}
		}

		int middle;

		if ( bestAxis < 0 )
		{
			//Every centre in one place: nothing to choose between, so halves.
			middle = first + ( count / 2 );
		}
		else
		{
			var low = Axis( centreMin, bestAxis );
			var scale = Bins / ( Axis( centreMax, bestAxis ) - low );

			var i = first;
			var j = first + count - 1;

			while ( i <= j )
			{
				var b = Math.Min( (int)( ( Axis( centres[source[i]], bestAxis ) - low ) * scale ), Bins - 1 );

				if ( b < bestPlane )
				{
					++i;
				}
				else
				{
					( source[i], source[j] ) = ( source[j], source[i] );
					--j;
				}
			}

			middle = i;

			if ( middle == first || middle == first + count ) middle = first + ( count / 2 );
		}

		var children = Interlocked.Add( ref nodesUsed, 2 ) - 2;

		nodes[node].First = children;
		nodes[node].Count = 0;

		var leftSize = middle - first;
		var rightSize = count - leftSize;

		if ( count >= ParallelLeast )
		{
			Parallel.Invoke(
				() => Split( children, first, leftSize, centres, lows, highs ),
				() => Split( children + 1, middle, rightSize, centres, lows, highs ) );
		}
		else
		{
			Split( children, first, leftSize, centres, lows, highs );
			Split( children + 1, middle, rightSize, centres, lows, highs );
		}
	}

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	private static float Axis( Vec3 v, int axis ) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	private static float Area( Vec3 min, Vec3 max )
	{
		var d = max - min;

		return ( d.X * d.Y ) + ( d.Y * d.Z ) + ( d.Z * d.X );
	}

	//Where a ray enters a box, or a distance past furthest where it misses.
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	private static float Enter( in Node node, Vec3 origin, Vec3 inverse, float furthest )
	{
		var one = ( node.Min - origin ) * inverse;
		var two = ( node.Max - origin ) * inverse;

		var near = Vec3.Min( one, two );
		var far = Vec3.Max( one, two );

		var enter = MathF.Max( MathF.Max( near.X, near.Y ), MathF.Max( near.Z, 0.0f ) );
		var leave = MathF.Min( MathF.Min( far.X, far.Y ), MathF.Min( far.Z, furthest ) );

		return enter <= leave ? enter : float.MaxValue;
	}

	//One over a direction, with a nought made merely very small so the boxes still sort.
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	private static Vec3 Inverse( Vec3 direction )
	{
		const float least = 1e-20f;

		return new Vec3(
			1.0f / ( MathF.Abs( direction.X ) < least ? MathF.CopySign( least, direction.X ) : direction.X ),
			1.0f / ( MathF.Abs( direction.Y ) < least ? MathF.CopySign( least, direction.Y ) : direction.Y ),
			1.0f / ( MathF.Abs( direction.Z ) < least ? MathF.CopySign( least, direction.Z ) : direction.Z ) );
	}

	//Möller and Trumbore's, against the tree's own triangle.
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	private bool Meets( int t, Vec3 origin, Vec3 direction, float furthest, out float distance, out float u, out float v, out bool back )
	{
		if ( !Crosses( t, origin, direction, furthest, out distance, out u, out v, out back ) ) return false;

		return !( tested[t] && passes is not null && passes( source[t], u, v ) );
	}

	//Whether a ray crosses a tree triangle at all, however clear it is
	private bool Crosses( int t, Vec3 origin, Vec3 direction, float furthest, out float distance, out float u, out float v, out bool back )
	{
		distance = 0.0f;
		u = 0.0f;
		v = 0.0f;
		back = false;

		var e1 = edge1[t];
		var e2 = edge2[t];

		var p = Vec3.Cross( direction, e2 );
		var det = Vec3.Dot( e1, p );

		if ( det > -1e-12f && det < 1e-12f ) return false;

		var inv = 1.0f / det;
		var s = origin - corner[t];

		u = Vec3.Dot( s, p ) * inv;
		if ( u < 0.0f || u > 1.0f ) return false;

		var q = Vec3.Cross( s, e1 );

		v = Vec3.Dot( direction, q ) * inv;
		if ( v < 0.0f || u + v > 1.0f ) return false;

		distance = Vec3.Dot( e2, q ) * inv;
		if ( distance <= 0.0f || distance >= furthest ) return false;

		//The determinant's sign is which way round the triangle is wound as the ray sees it: below nought, the ray is looking at its back.
		back = det < 0.0f;

		return true;
	}

	/// <summary>How much of a ray gets through to <paramref name="furthest"/>, triangle by triangle.</summary>
	public Vec3 Transmittance( Vec3 origin, Vec3 direction, float furthest, Veil veil )
	{
		if ( source.Length == 0 ) return Vec3.One;

		var inverse = Inverse( direction );

		Span<int> stack = stackalloc int[64];
		var depth = 0;
		var at = 0;
		var through = Vec3.One;

		if ( Enter( nodes[0], origin, inverse, furthest ) == float.MaxValue ) return Vec3.One;

		while ( true )
		{
			ref readonly var node = ref nodes[at];

			if ( node.Count > 0 )
			{
				for ( var t = node.First; t < node.First + node.Count; ++t )
				{
					if ( !Crosses( t, origin, direction, furthest, out _, out var u, out var v, out _ ) ) continue;

					through *= veil( source[t], u, v, tested[t], direction );
					if ( through.X <= 1e-4f && through.Y <= 1e-4f && through.Z <= 1e-4f ) return Vec3.Zero;
				}
			}
			else
			{
				var left = node.First;

				var leftHit = Enter( nodes[left], origin, inverse, furthest ) != float.MaxValue;
				var rightHit = Enter( nodes[left + 1], origin, inverse, furthest ) != float.MaxValue;

				if ( leftHit )
				{
					if ( rightHit && depth < stack.Length ) stack[depth++] = left + 1;

					at = left;
					continue;
				}

				if ( rightHit )
				{
					at = left + 1;
					continue;
				}
			}

			if ( depth == 0 ) break;

			at = stack[--depth];
		}

		return through;
	}

	/// <summary>Whether a ray crosses, however clear, a triangle <paramref name="which"/> picks by its world index.</summary>
	public bool Touches( Vec3 origin, Vec3 direction, float furthest, Predicate<int> which )
	{
		if ( source.Length == 0 ) return false;

		var inverse = Inverse( direction );

		Span<int> stack = stackalloc int[64];
		var depth = 0;
		var at = 0;

		if ( Enter( nodes[0], origin, inverse, furthest ) == float.MaxValue ) return false;

		while ( true )
		{
			ref readonly var node = ref nodes[at];

			if ( node.Count > 0 )
			{
				for ( var t = node.First; t < node.First + node.Count; ++t )
					if ( which( source[t] ) && Crosses( t, origin, direction, furthest, out _, out _, out _, out _ ) ) return true;
			}
			else
			{
				var left = node.First;

				var leftHit = Enter( nodes[left], origin, inverse, furthest ) != float.MaxValue;
				var rightHit = Enter( nodes[left + 1], origin, inverse, furthest ) != float.MaxValue;

				if ( leftHit )
				{
					if ( rightHit && depth < stack.Length ) stack[depth++] = left + 1;

					at = left;
					continue;
				}

				if ( rightHit )
				{
					at = left + 1;
					continue;
				}
			}

			if ( depth == 0 ) break;

			at = stack[--depth];
		}

		return false;
	}

	/// <summary>The nearest thing along a ray, within <paramref name="furthest"/>.</summary>
	public bool Nearest( Vec3 origin, Vec3 direction, float furthest, out BakeHit hit )
	{
		hit = default;
		hit.Triangle = -1;

		if ( source.Length == 0 ) return false;

		var inverse = Inverse( direction );

		Span<int> stack = stackalloc int[64];
		var depth = 0;
		var at = 0;
		var found = false;

		if ( Enter( nodes[0], origin, inverse, furthest ) == float.MaxValue ) return false;

		while ( true )
		{
			ref readonly var node = ref nodes[at];

			if ( node.Count > 0 )
			{
				for ( var t = node.First; t < node.First + node.Count; ++t )
				{
					if ( !Meets( t, origin, direction, furthest, out var distance, out var u, out var v, out var back ) ) continue;

					furthest = distance;
					found = true;

					hit.Distance = distance;
					hit.Triangle = source[t];
					hit.U = u;
					hit.V = v;
					hit.Back = back;
				}
			}
			else
			{
				var left = node.First;
				var right = left + 1;

				var leftAt = Enter( nodes[left], origin, inverse, furthest );
				var rightAt = Enter( nodes[right], origin, inverse, furthest );

				//The nearer first, the further kept for later - by which time something nearer than it has usually been found.
				if ( leftAt > rightAt )
				{
					( left, right ) = ( right, left );
					( leftAt, rightAt ) = ( rightAt, leftAt );
				}

				if ( leftAt != float.MaxValue )
				{
					if ( rightAt != float.MaxValue && depth < stack.Length ) stack[depth++] = right;

					at = left;
					continue;
				}
			}

			if ( depth == 0 ) break;

			at = stack[--depth];
		}

		return found;
	}

	/// <summary>Whether anything at all stands along a ray before <paramref name="furthest"/>.</summary>
	public bool Blocked( Vec3 origin, Vec3 direction, float furthest )
	{
		if ( source.Length == 0 ) return false;

		var inverse = Inverse( direction );

		Span<int> stack = stackalloc int[64];
		var depth = 0;
		var at = 0;

		if ( Enter( nodes[0], origin, inverse, furthest ) == float.MaxValue ) return false;

		while ( true )
		{
			ref readonly var node = ref nodes[at];

			if ( node.Count > 0 )
			{
				for ( var t = node.First; t < node.First + node.Count; ++t )
				{
					if ( Meets( t, origin, direction, furthest, out _, out _, out _, out _ ) ) return true;
				}
			}
			else
			{
				var left = node.First;

				var leftHit = Enter( nodes[left], origin, inverse, furthest ) != float.MaxValue;
				var rightHit = Enter( nodes[left + 1], origin, inverse, furthest ) != float.MaxValue;

				if ( leftHit )
				{
					if ( rightHit && depth < stack.Length ) stack[depth++] = left + 1;

					at = left;
					continue;
				}

				if ( rightHit )
				{
					at = left + 1;
					continue;
				}
			}

			if ( depth == 0 ) break;

			at = stack[--depth];
		}

		return false;
	}
}
