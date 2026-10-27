using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vec2 = System.Numerics.Vector2;
using Vec3 = System.Numerics.Vector3;
using Vec4 = System.Numerics.Vector4;

namespace Rinten.Bake;

//Lightmap atlas: irradiance over pi plus dominant direction per texel for static unwrapped surfaces
internal sealed class BakeSheet
{
	//Texels of gutter around each square, so filtering at its edge reads its own light.
	private const int Gutter = 2;

	//Texels a side the smallest receiver gets, so a small prop's contact shadow is more than a blur
	private const int SmallestSide = 16;

	public int Width, Height;

	/// <summary>Four halves a texel: the light, and one where the texel is a surface's.</summary>
	public Half[] Light;

	/// <summary>Four halves a texel: which way the sky is open xyz, how much is seen w - see SkyLit.</summary>
	public Half[] Sky;

	/// <summary>The light's first band over the light, half size, four bytes a texel in runs of red, green, blue.</summary>
	public byte[] Band;

	/// <summary>Four bytes a texel: how much of each shade channel's light the bake let through - see BakeLight.Shade.</summary>
	public byte[] Shade;

	//One entry an atlas texel while baking.
	private Vec3[] position;
	private Vec3[] normal;
	private Vec3[] geometric;

	//How far the surface moves across one texel and down one, in the world: what each sample spreads over, so a
	//texel is the mean over its area rather than its centre and a shadow's edge falls across it, not between texels
	private Vec3[] across;
	private Vec3[] down;

	//How far each way from its centre a texel's spread may go and stay on its own face: least and most across x y,
	//down z w, in texels - a sample past a box's edge is in the air beside it, and lights the edge as if it saw round
	private System.Numerics.Vector4[] reach;
	private float[] owned;        //Distance from the texel's centre to its triangle, in texels; +inf where none
	private int[] ownerOf;        //Receiver whose square it is in, -1 where none
	private int[] isle;           //Island of the unwrap the texel lies on, numbered across the sheet; -1 where none
	private int[] transmitter;    //One more than the material passing light through the texel, nought where none

	//What each texel's surface reflects, for the denoiser to tell one material from another - see BakeDenoise
	private Vec3[] albedo;

	//Texels whose centre was moved out of something (Unocclude): the denoiser takes nothing from them
	private bool[] pushed;

	//The light that bounced, which the filter smooths, and what came straight from the lights, which it leaves
	private Vec3[] light;
	private Vec3[] direct;

	//Their first bands, three a texel - red, green, blue - each the way the light came, by how much: the irradiance at a
	//normal n is the light plus the band's dot with n less the face's (L1 lightmaps)
	private Vec3[] bounceBand;
	private Vec3[] straightBand;
	private float[] variance;
	private bool[] valid;

	//The sky straight from the texel: the escaping rays' direction summed xyz, their share w
	private Vec4[] sky;

	//Each shade channel's light that arrived over what would have unshadowed, by luma; one where none would
	private Vec4[] shade;

	/// <summary>Gives each receiver a square at the density, within <paramref name="most"/>; too big gets less.</summary>
	public static bool Pack( BakeScene scene, float density, int most, out int side )
	{
		side = 0;

		var receivers = scene.Receivers;
		if ( receivers.Count == 0 ) return false;

		var wanted = new float[receivers.Count];

		for ( var r = 0; r < receivers.Count; ++r )
		{
			var receiver = receivers[r];

			var world = 0.0f;
			var sheet = 0.0f;

			//Metres a unit of the unwrap spans in its longer direction, weighed by area: a box stretched to a pillar
			//keeps its square unwrap, and the square must be big enough for the long way, not the mean
			var stretched = 0.0f;

			for ( var t = receiver.FirstTriangle; t < receiver.FirstTriangle + receiver.TriangleCount; ++t )
			{
				var a = scene.Corners[t * 3];
				var b = scene.Corners[( t * 3 ) + 1];
				var c = scene.Corners[( t * 3 ) + 2];

				world += Vec3.Cross( scene.Positions[b] - scene.Positions[a], scene.Positions[c] - scene.Positions[a] ).Length() * 0.5f;

				var ua = scene.Uv1[a];
				var ub = scene.Uv1[b];
				var uc = scene.Uv1[c];

				var e1 = scene.Positions[b] - scene.Positions[a];
				var e2 = scene.Positions[c] - scene.Positions[a];
				var d1 = ub - ua;
				var d2 = uc - ua;
				var turn = ( d1.X * d2.Y ) - ( d2.X * d1.Y );
				var area = Vec3.Cross( e1, e2 ).Length() * 0.5f;

				sheet += MathF.Abs( turn ) * 0.5f;

				if ( MathF.Abs( turn ) > 1e-10f )
				{
					var alongU = ( ( e1 * d2.Y ) - ( e2 * d1.Y ) ) / turn;
					var alongV = ( ( e2 * d1.X ) - ( e1 * d2.X ) ) / turn;
					stretched += MathF.Max( alongU.Length(), alongV.Length() ) * area;
				}
			}

			//The unwrap fills some part of its square; the square is as big as makes that part carry the density,
			//and its long way too - within four times what the area alone asks
			var even = sheet > 1e-6f ? MathF.Sqrt( world / sheet ) : MathF.Sqrt( world );
			var longest = world > 1e-8f ? stretched / world : even;
			wanted[r] = Math.Clamp( longest, even, even * 4.0f ) * receiver.Scale;
		}

		for ( var attempt = 0; attempt < 48; ++attempt )
		{
			for ( var r = 0; r < receivers.Count; ++r )
				receivers[r].Side = Math.Clamp( (int)MathF.Ceiling( wanted[r] * density ), SmallestSide, Math.Max( most / 2, SmallestSide ) );

			//The smallest power of two it all goes into, up to the most.
			for ( var size = 128; size <= most; size *= 2 )
			{
				if ( Shelve( receivers, size ) )
				{
					side = size;
					scene.AtlasSide = size;
					return true;
				}
			}

			density *= 0.85f;
		}

		return false;
	}

	//Tallest first, left to right, a row at a time.
	private static bool Shelve( List<BakeReceiver> receivers, int size )
	{
		var order = new List<BakeReceiver>( receivers );
		order.Sort( ( a, b ) => b.Side.CompareTo( a.Side ) );

		var x = 0;
		var y = 0;
		var row = 0;

		foreach ( var receiver in order )
		{
			var span = receiver.Side + ( Gutter * 2 );

			if ( x + span > size )
			{
				x = 0;
				y += row;
				row = 0;
			}

			if ( span > size || y + span > size ) return false;

			receiver.X = x + Gutter;
			receiver.Y = y + Gutter;

			x += span;
			row = Math.Max( row, span );
		}

		return true;
	}

	/// <summary>Keeps each texel's surface point under its centre, or the nearest if grazed, so edges are lit.</summary>
	public void Rasterise( BakeScene scene, int side )
	{
		Width = side;
		Height = side;

		var count = side * side;

		position = new Vec3[count];
		normal = new Vec3[count];
		geometric = new Vec3[count];
		across = new Vec3[count];
		down = new Vec3[count];
		reach = new System.Numerics.Vector4[count];
		albedo = new Vec3[count];
		owned = new float[count];
		ownerOf = new int[count];
		isle = new int[count];
		transmitter = new int[count];

		Array.Fill( owned, float.PositiveInfinity );
		Array.Fill( ownerOf, -1 );
		Array.Fill( isle, -1 );

		var islands = 0;

		for ( var r = 0; r < scene.Receivers.Count; ++r )
		{
			var receiver = scene.Receivers[r];

			for ( var y = receiver.Y - Gutter; y < receiver.Y + receiver.Side + Gutter; ++y )
				for ( var x = receiver.X - Gutter; x < receiver.X + receiver.Side + Gutter; ++x )
					if ( x >= 0 && y >= 0 && x < side && y < side ) ownerOf[( y * side ) + x] = r;

			var islandOf = Islands( scene, receiver, ref islands );

			for ( var t = receiver.FirstTriangle; t < receiver.FirstTriangle + receiver.TriangleCount; ++t )
				Triangle( scene, receiver, t, side, islandOf[t - receiver.FirstTriangle] );

			for ( var t = receiver.FirstTriangle; t < receiver.FirstTriangle + receiver.TriangleCount; ++t )
				Covers( scene, receiver, t, side );
		}

		Unocclude( scene );
	}

	//Unocclude: a texel whose centre is inside something (a wall on the floor) moves out past the back face its rays
	//find, but only onto its own face - a corner's overlap has a way out on each side, and the far one is outdoors
	private void Unocclude( BakeScene scene )
	{
		var count = Width * Height;
		pushed = new bool[count];

		//How far the rays look for the mark, in texels
		const float Look = 4.0f;

		Parallel.For( 0, count, at =>
		{
			if ( float.IsPositiveInfinity( owned[at] ) ) return;

			var face = geometric[at];
			if ( face == Vec3.Zero ) return;

			var size = MathF.Max( across[at].Length(), down[at].Length() );
			if ( size <= 1e-6f ) return;

			var from = position[at] + ( face * BakeScene.Lift );
			var u = Vec3.Normalize( across[at].LengthSquared() > 1e-12f ? across[at] : Vec3.Cross( face, Vec3.UnitY ) );
			var v = Vec3.Normalize( Vec3.Cross( face, u ) );

			Span<Vec3> ways = [u, v, -u, -v];
			var nearest = float.MaxValue;
			var moved = position[at];

			foreach ( var way in ways )
			{
				if ( !scene.Bvh.Nearest( from, way, size * Look, out var hit ) ) continue;

				var facing = scene.Facing[hit.Triangle];
				if ( Vec3.Dot( way, facing ) <= 0.0f ) continue;

				pushed[at] = true;

				if ( hit.Distance <= size && hit.Distance < nearest && OnOwnFace( at, way * ( hit.Distance + ( size * 0.5f ) ) ) )
				{
					nearest = hit.Distance;
					moved = from + ( way * hit.Distance ) + ( facing * BakeScene.Lift * 10.0f );
				}
			}

			position[at] = moved;
		} );
	}

	//Whether a step from a texel's centre lands on a texel of the same receiver and face
	private bool OnOwnFace( int at, Vec3 step )
	{
		var a = across[at];
		var d = down[at];
		if ( a.LengthSquared() <= 1e-12f || d.LengthSquared() <= 1e-12f ) return true;

		var x = (int)MathF.Floor( ( at % Width ) + 0.5f + ( Vec3.Dot( step, a ) / a.LengthSquared() ) );
		var y = (int)MathF.Floor( ( at / Width ) + 0.5f + ( Vec3.Dot( step, d ) / d.LengthSquared() ) );
		if ( x < 0 || y < 0 || x >= Width || y >= Height ) return false;

		var to = ( y * Width ) + x;

		return !float.IsPositiveInfinity( owned[to] ) && ownerOf[to] == ownerOf[at] && Vec3.Dot( geometric[to], geometric[at] ) > 0.99f;
	}

	//Widens the reach of the texels of a triangle's face to where this triangle covers them
	private void Covers( BakeScene scene, BakeReceiver receiver, int t, int side )
	{
		var scale = receiver.Side;
		var origin = new Vec2( receiver.X, receiver.Y );

		var a = ( Vec2.Clamp( scene.Uv1[scene.Corners[t * 3]], Vec2.Zero, Vec2.One ) * scale ) + origin;
		var b = ( Vec2.Clamp( scene.Uv1[scene.Corners[( t * 3 ) + 1]], Vec2.Zero, Vec2.One ) * scale ) + origin;
		var c = ( Vec2.Clamp( scene.Uv1[scene.Corners[( t * 3 ) + 2]], Vec2.Zero, Vec2.One ) * scale ) + origin;

		var x0 = Math.Max( (int)MathF.Floor( MathF.Min( a.X, MathF.Min( b.X, c.X ) ) ) - 1, 0 );
		var y0 = Math.Max( (int)MathF.Floor( MathF.Min( a.Y, MathF.Min( b.Y, c.Y ) ) ) - 1, 0 );
		var x1 = Math.Min( (int)MathF.Ceiling( MathF.Max( a.X, MathF.Max( b.X, c.X ) ) ) + 1, side - 1 );
		var y1 = Math.Min( (int)MathF.Ceiling( MathF.Max( a.Y, MathF.Max( b.Y, c.Y ) ) ) + 1, side - 1 );

		Span<float> steps = [-0.5f, -0.25f, 0.25f, 0.5f];

		for ( var y = y0; y <= y1; ++y )
		{
			for ( var x = x0; x <= x1; ++x )
			{
				var at = ( y * side ) + x;
				if ( float.IsPositiveInfinity( owned[at] ) || Vec3.Dot( geometric[at], scene.Facing[t] ) < 0.99f ) continue;

				var centre = new Vec2( x + 0.5f, y + 0.5f );
				var r = reach[at];

				foreach ( var s in steps )
				{
					if ( Inside( centre + new Vec2( s, 0.0f ), a, b, c ) ) { r.X = MathF.Min( r.X, s ); r.Y = MathF.Max( r.Y, s ); }
					if ( Inside( centre + new Vec2( 0.0f, s ), a, b, c ) ) { r.Z = MathF.Min( r.Z, s ); r.W = MathF.Max( r.W, s ); }
				}

				reach[at] = r;
			}
		}
	}

	//Whether a point is on a triangle, all in texels, with a hair's slack so a shared edge counts for both
	private static bool Inside( Vec2 p, Vec2 a, Vec2 b, Vec2 c )
	{
		var d1 = ( ( b.X - a.X ) * ( p.Y - a.Y ) ) - ( ( b.Y - a.Y ) * ( p.X - a.X ) );
		var d2 = ( ( c.X - b.X ) * ( p.Y - b.Y ) ) - ( ( c.Y - b.Y ) * ( p.X - b.X ) );
		var d3 = ( ( a.X - c.X ) * ( p.Y - c.Y ) ) - ( ( a.Y - c.Y ) * ( p.X - c.X ) );
		const float Slack = 1e-3f;

		return ( d1 >= -Slack && d2 >= -Slack && d3 >= -Slack ) || ( d1 <= Slack && d2 <= Slack && d3 <= Slack );
	}

	//Each of the receiver's triangles' island: those that share a corner of the unwrap across a crease gentler than 60
	//degrees are one, so a box unwrapped as a cross keeps its faces apart; numbered on from `next`
	private static int[] Islands( BakeScene scene, BakeReceiver receiver, ref int next )
	{
		var count = receiver.TriangleCount;
		var parent = new int[count];
		for ( var i = 0; i < count; ++i ) parent[i] = i;

		int Find( int i )
		{
			while ( parent[i] != i ) i = parent[i] = parent[parent[i]];
			return i;
		}

		var corners = new Dictionary<(int, int), List<int>>();

		for ( var i = 0; i < count; ++i )
		{
			var facing = scene.Facing[receiver.FirstTriangle + i];

			for ( var k = 0; k < 3; ++k )
			{
				var uv = scene.Uv1[scene.Corners[( ( receiver.FirstTriangle + i ) * 3 ) + k]];
				var spot = ( (int)MathF.Round( uv.X * 65536.0f ), (int)MathF.Round( uv.Y * 65536.0f ) );

				if ( !corners.TryGetValue( spot, out var here ) ) corners[spot] = here = new List<int>();

				foreach ( var other in here )
					if ( Vec3.Dot( facing, scene.Facing[receiver.FirstTriangle + other] ) > 0.5f ) parent[Find( i )] = Find( other );

				here.Add( i );
			}
		}

		var numbered = new Dictionary<int, int>();
		var island = new int[count];

		for ( var i = 0; i < count; ++i )
		{
			var root = Find( i );
			if ( !numbered.TryGetValue( root, out var number ) ) numbered[root] = number = next++;
			island[i] = number;
		}

		return island;
	}

	private void Triangle( BakeScene scene, BakeReceiver receiver, int t, int side, int island )
	{
		var ia = scene.Corners[t * 3];
		var ib = scene.Corners[( t * 3 ) + 1];
		var ic = scene.Corners[( t * 3 ) + 2];

		var scale = receiver.Side;
		var origin = new Vec2( receiver.X, receiver.Y );

		var a = ( Vec2.Clamp( scene.Uv1[ia], Vec2.Zero, Vec2.One ) * scale ) + origin;
		var b = ( Vec2.Clamp( scene.Uv1[ib], Vec2.Zero, Vec2.One ) * scale ) + origin;
		var c = ( Vec2.Clamp( scene.Uv1[ic], Vec2.Zero, Vec2.One ) * scale ) + origin;

		var low = Vec2.Min( a, Vec2.Min( b, c ) );
		var high = Vec2.Max( a, Vec2.Max( b, c ) );

		var x0 = Math.Max( (int)MathF.Floor( low.X ) - 1, receiver.X - Gutter );
		var y0 = Math.Max( (int)MathF.Floor( low.Y ) - 1, receiver.Y - Gutter );
		var x1 = Math.Min( (int)MathF.Ceiling( high.X ) + 1, receiver.X + receiver.Side + Gutter - 1 );
		var y1 = Math.Min( (int)MathF.Ceiling( high.Y ) + 1, receiver.Y + receiver.Side + Gutter - 1 );

		x0 = Math.Max( x0, 0 );
		y0 = Math.Max( y0, 0 );
		x1 = Math.Min( x1, side - 1 );
		y1 = Math.Min( y1, side - 1 );

		var area = ( ( b.X - a.X ) * ( c.Y - a.Y ) ) - ( ( c.X - a.X ) * ( b.Y - a.Y ) );
		if ( MathF.Abs( area ) < 1e-12f ) return;

		//The world's change a texel across and down the square, out of the triangle's corners
		var pa = scene.Interpolate( scene.Positions, t, 0.0f, 0.0f );
		var ab = scene.Interpolate( scene.Positions, t, 1.0f, 0.0f ) - pa;
		var ac = scene.Interpolate( scene.Positions, t, 0.0f, 1.0f ) - pa;
		var perX = Short( ( ( ab * ( c.Y - a.Y ) ) - ( ac * ( b.Y - a.Y ) ) ) / area );
		var perY = Short( ( ( ac * ( b.X - a.X ) ) - ( ab * ( c.X - a.X ) ) ) / area );

		for ( var y = y0; y <= y1; ++y )
		{
			for ( var x = x0; x <= x1; ++x )
			{
				var p = new Vec2( x + 0.5f, y + 0.5f );
				var closest = Closest( p, a, b, c );
				var distance = Vec2.Distance( p, closest );

				//Past a texel's half-diagonal the triangle does not touch it.
				if ( distance > 0.7072f ) continue;

				var at = ( y * side ) + x;
				if ( distance >= owned[at] ) continue;

				//Where it lands on the triangle, as weights of its corners.
				var wb = ( ( ( closest.X - a.X ) * ( c.Y - a.Y ) ) - ( ( c.X - a.X ) * ( closest.Y - a.Y ) ) ) / area;
				var wc = ( ( ( b.X - a.X ) * ( closest.Y - a.Y ) ) - ( ( closest.X - a.X ) * ( b.Y - a.Y ) ) ) / area;

				owned[at] = distance;
				position[at] = scene.Interpolate( scene.Positions, t, wb, wc );

				var n = scene.Interpolate( scene.Normals, t, wb, wc );
				var length = n.Length();
				normal[at] = length > 1e-8f ? n / length : scene.Facing[t];
				geometric[at] = scene.Facing[t];
				isle[at] = island;
				albedo[at] = scene.AlbedoAt( t, wb, wc );
				transmitter[at] = scene.Materials[scene.MaterialOf[t]].Transmits == Vec3.Zero ? 0 : scene.MaterialOf[t] + 1;
				across[at] = perX;
				down[at] = perY;
			}
		}
	}

	//An island as one of 63 marks over 64 - what the frame's filter keeps its taps to (SheetSpotAt), so it blends across
	//a curved model's triangles but not into the island packed beside it
	private static float IslandKey( int island ) => ( ( island % 63 ) + 1 ) / 64.0f;

	//Which way a texel's face looks, as one of 64 bins over the octahedron, over 64 - the mark where no island is known
	private static float FaceOf( Vec3 facing )
	{
		var sum = MathF.Abs( facing.X ) + MathF.Abs( facing.Y ) + MathF.Abs( facing.Z );
		if ( sum < 1e-6f ) return 1.0f / 64.0f;

		var x = facing.X / sum;
		var y = facing.Y / sum;

		if ( facing.Z < 0.0f )
		{
			var fx = ( 1.0f - MathF.Abs( y ) ) * ( x >= 0.0f ? 1.0f : -1.0f );
			var fy = ( 1.0f - MathF.Abs( x ) ) * ( y >= 0.0f ? 1.0f : -1.0f );
			x = fx;
			y = fy;
		}

		var bx = Math.Clamp( (int)( ( ( x * 0.5f ) + 0.5f ) * 8.0f ), 0, 7 );
		var by = Math.Clamp( (int)( ( ( y * 0.5f ) + 0.5f ) * 8.0f ), 0, 7 );

		return ( ( by * 8 ) + bx + 1 ) / 64.0f;
	}

	//The band at half the sheet's size, each colour over that colour's light, as L1 is packed - the way the light
	//leans is broad, and a byte of it is plenty: a quarter of the memory of a full one in halves, twice over
	private void EncodeBand()
	{
		var half = Math.Max( Width / 2, 1 );
		var rows = Math.Max( Height / 2, 1 );
		Band = new byte[half * rows * 12];

		Span<Vec3> bands = stackalloc Vec3[3];

		for ( var y = 0; y < rows; ++y )
		{
			for ( var x = 0; x < half; ++x )
			{
				bands.Clear();
				var lit = Vec3.Zero;

				for ( var k = 0; k < 4; ++k )
				{
					var xx = Math.Min( ( x * 2 ) + ( k & 1 ), Width - 1 );
					var yy = Math.Min( ( y * 2 ) + ( k >> 1 ), Height - 1 );
					var at = ( yy * Width ) + xx;
					if ( ownerOf[at] < 0 ) continue;

					lit += Vec3.Max( light[at] + direct[at], Vec3.Zero );
					for ( var c = 0; c < 3; ++c ) bands[c] += bounceBand[( at * 3 ) + c] + straightBand[( at * 3 ) + c];
				}

				ReadOnlySpan<float> each = [lit.X, lit.Y, lit.Z];

				for ( var c = 0; c < 3; ++c )
				{
					var over = bands[c] / MathF.Max( each[c], 1e-4f );
					var to = ( ( y * half * 3 ) + ( c * half ) + x ) * 4;

					Band[to] = Byte( over.X );
					Band[to + 1] = Byte( over.Y );
					Band[to + 2] = Byte( over.Z );
					Band[to + 3] = 128;
				}
			}
		}
	}

	//Minus two to two as a byte, nought at its middle - BakedSheet reads it back the same way
	private static byte Byte( float value ) => (byte)Math.Clamp( MathF.Round( ( ( value * 0.25f ) + 0.5f ) * 255.0f ), 0.0f, 255.0f );

	//A texel's step held to half a metre, so a sliver of a triangle stretched over its square does not spread far
	private static Vec3 Short( Vec3 step )
	{
		var length = step.Length();
		return length > 0.5f ? step * ( 0.5f / length ) : step;
	}

	//The point of a triangle nearest a point, all in texels.
	private static Vec2 Closest( Vec2 p, Vec2 a, Vec2 b, Vec2 c )
	{
		//Ericson's, in two dimensions.
		var ab = b - a;
		var ac = c - a;
		var ap = p - a;

		var d1 = Vec2.Dot( ab, ap );
		var d2 = Vec2.Dot( ac, ap );
		if ( d1 <= 0.0f && d2 <= 0.0f ) return a;

		var bp = p - b;
		var d3 = Vec2.Dot( ab, bp );
		var d4 = Vec2.Dot( ac, bp );
		if ( d3 >= 0.0f && d4 <= d3 ) return b;

		var vc = ( d1 * d4 ) - ( d3 * d2 );
		if ( vc <= 0.0f && d1 >= 0.0f && d3 <= 0.0f ) return a + ( ab * ( d1 / ( d1 - d3 ) ) );

		var cp = p - c;
		var d5 = Vec2.Dot( ab, cp );
		var d6 = Vec2.Dot( ac, cp );
		if ( d6 >= 0.0f && d5 <= d6 ) return c;

		var vb = ( d5 * d2 ) - ( d1 * d6 );
		if ( vb <= 0.0f && d2 >= 0.0f && d6 <= 0.0f ) return a + ( ac * ( d2 / ( d2 - d6 ) ) );

		var va = ( d3 * d6 ) - ( d5 * d4 );
		if ( va <= 0.0f && ( d4 - d3 ) >= 0.0f && ( d5 - d6 ) >= 0.0f )
			return b + ( ( c - b ) * ( ( d4 - d3 ) / ( ( d4 - d3 ) + ( d5 - d6 ) ) ) );

		//Inside: the point itself.
		return p;
	}

	/// <summary>Traces every texel on the card (BakeGpu), leaving what Trace would; false where it could not.</summary>
	//A final gather as Lightmass's: a pass a bounce, each ray reading the last pass's filtered light where it lands,
	//not a path that must find a room's few lit patches (lamps in shades) again at every step - which bakes spots
	public bool TraceOnCard( BakeScene scene, int samples, BakeProgress progress, CancellationToken cancel )
	{
		var count = Width * Height;

		var texels = new List<int>();
		for ( var i = 0; i < count; ++i ) if ( !float.IsPositiveInfinity( owned[i] ) ) texels.Add( i );

		//Whole paths with no bounce to gather, or where the world was laid out for another sheet than this one
		if ( scene.Bounces <= 0 || scene.AtlasSide != Width )
		{
			if ( !BakeGpu.Trace( scene, position, normal, geometric, across, down, reach, transmitter, texels, samples, 0, null, null,
				"Tracing the surfaces on the card", progress, cancel, out var whole ) ) return false;

			Take( scene, texels, whole, samples );
			return true;
		}

		//The passes before the last take a quarter of the samples: what they leave is read by many rays, each aiming at
		//the brightest of it, so it needs no filter of its own
		var early = Math.Max( samples / 4, 64 );
		var cache = new float[count * 4];
		Vec3[] lit = null;

		for ( var pass = 0; pass < scene.Bounces; ++pass )
		{
			//The first finds every lamp's straight light and the sky; the rest, each a bounce more of light and sky
			var mode = BakeGpu.ModeGather | BakeGpu.ModeSky | ( pass == 0 ? BakeGpu.ModeAllLights : BakeGpu.ModeNoDirect );

			//Past the first, the cache's lit triangles are lights the gather aims at as well
			var lights = pass == 0 ? null : CacheLights.Of( scene, cache, Width );

			if ( !BakeGpu.Trace( scene, position, normal, geometric, across, down, reach, transmitter, texels, early, mode, cache, lights,
				$"Gathering bounce {pass + 1} of {scene.Bounces} on the card", progress, cancel, out var sums ) ) return false;

			Take( scene, texels, sums, early );
			Dilate();

			lit ??= (Vec3[])direct.Clone();

			for ( var i = 0; i < count; ++i )
			{
				var l = Vec3.Max( lit[i] + light[i], Vec3.Zero );
				cache[i * 4] = l.X;
				cache[( i * 4 ) + 1] = l.Y;
				cache[( i * 4 ) + 2] = l.Z;
			}
		}

		if ( !BakeGpu.Trace( scene, position, normal, geometric, across, down, reach, transmitter, texels, samples, BakeGpu.ModeGather, cache,
			CacheLights.Of( scene, cache, Width ), "Tracing the surfaces on the card", progress, cancel, out var last ) ) return false;

		Take( scene, texels, last, samples );
		return true;
	}

	//A pass's sums into the texels' light, straight light, bands, noise and sky
	private void Take( BakeScene scene, List<int> texels, float[] sums, int samples )
	{
		var count = Width * Height;

		light = new Vec3[count];
		direct = new Vec3[count];
		bounceBand = new Vec3[count * 3];
		straightBand = new Vec3[count * 3];
		variance = new float[count];
		valid = new bool[count];
		sky = new Vec4[count];
		shade = new Vec4[count];

		Span<Vec3> bands = stackalloc Vec3[6];

		for ( var i = 0; i < texels.Count; ++i )
		{
			var at = texels[i];
			var o = i * BakeGpu.SheetFloats;

			var sum = new Vec3( sums[o], sums[o + 1], sums[o + 2] );
			var squares = sums[o + 3];
			var inside = (int)sums[o + 4];
			var straight = new Vec3( sums[o + 5], sums[o + 6], sums[o + 7] );

			for ( var k = 0; k < 6; ++k ) bands[k] = new Vec3( sums[o + 12 + ( k * 4 )], sums[o + 13 + ( k * 4 )], sums[o + 14 + ( k * 4 )] );

			var through = new Vec4( sums[o + 36], sums[o + 37], sums[o + 38], sums[o + 39] );
			var open = new Vec4( sums[o + 40], sums[o + 41], sums[o + 42], sums[o + 43] );

			End( at, sum, straight, squares, inside, samples, bands );
			shade[at] = ShadeOf( through, open );
			sky[at] = SkyOf( new Vec4( sums[o + 8], sums[o + 9], sums[o + 10], sums[o + 11] ), Math.Max( samples - inside, 1 ), scene.SkyStrength );
		}
	}

	/// <summary>Traces every texel, on every thread; <paramref name="progress"/> counts texels as they finish.</summary>
	//With bounces, TraceOnCard's final gather: a pass a bounce, each reading the last pass's light where it lands
	public void Trace( BakeScene scene, int samples, BakeProgress progress, CancellationToken cancel )
	{
		var count = Width * Height;

		try
		{
			if ( scene.Bounces <= 0 || scene.AtlasSide != Width )
			{
				scene.Gathering = null;
				TracePass( scene, samples, 0, "Tracing the surfaces", progress, cancel );
				return;
			}

			var early = Math.Max( samples / 4, 64 );
			var cache = new float[count * 4];
			Vec3[] lit = null;

			for ( var pass = 0; pass < scene.Bounces; ++pass )
			{
				var mode = BakeGpu.ModeGather | BakeGpu.ModeSky | ( pass == 0 ? BakeGpu.ModeAllLights : BakeGpu.ModeNoDirect );

				scene.Gathering = new BakeGather { Mode = mode, Cache = cache, Lights = pass == 0 ? null : CacheLights.Of( scene, cache, Width ) };
				TracePass( scene, early, mode, $"Gathering bounce {pass + 1} of {scene.Bounces}", progress, cancel );
				Dilate();

				lit ??= (Vec3[])direct.Clone();

				var next = new float[count * 4];

				for ( var i = 0; i < count; ++i )
				{
					var l = Vec3.Max( lit[i] + light[i], Vec3.Zero );
					next[i * 4] = l.X;
					next[( i * 4 ) + 1] = l.Y;
					next[( i * 4 ) + 2] = l.Z;
				}

				cache = next;
			}

			scene.Gathering = new BakeGather { Mode = BakeGpu.ModeGather, Cache = cache, Lights = CacheLights.Of( scene, cache, Width ) };
			TracePass( scene, samples, BakeGpu.ModeGather, "Tracing the surfaces", progress, cancel );
		}
		finally
		{
			scene.Gathering = null;
		}
	}

	//One pass over every texel, on every thread, sampling as mode says - bake_trace.slang's main
	private void TracePass( BakeScene scene, int samples, int mode, string stage, BakeProgress progress, CancellationToken cancel )
	{
		var count = Width * Height;

		light = new Vec3[count];
		direct = new Vec3[count];
		bounceBand = new Vec3[count * 3];
		straightBand = new Vec3[count * 3];
		variance = new float[count];
		valid = new bool[count];
		sky = new Vec4[count];
		shade = new Vec4[count];

		var texels = new List<int>();
		for ( var i = 0; i < count; ++i ) if ( !float.IsPositiveInfinity( owned[i] ) ) texels.Add( i );

		progress.Begin( stage, texels.Count );

		//Whether a ray's way must carry its chance, for the glows' and the cache's lights to weigh theirs against
		var aiming = scene.Gathering?.Lights is { Count: > 0 };

		Parallel.For( 0, texels.Count, new ParallelOptions { CancellationToken = cancel }, i =>
		{
			var at = texels[i];
			var random = new BakeRandom( (ulong)at * 0x9E3779B97F4A7C15ul, (ulong)at );

			var n = normal[at];
			var g = geometric[at];

			var sum = Vec3.Zero;
			var straight = Vec3.Zero;
			var squares = 0.0f;
			var inside = 0;
			var open = Vec4.Zero;
			var shadeLit = Vec4.Zero;
			var shadeWhole = Vec4.Zero;
			Span<Vec3> bands = stackalloc Vec3[6];

			for ( var s = 0; s < samples; ++s )
			{
				//Somewhere in the texel's area that is on its face
				var r = reach[at];
				var p = position[at] + ( across[at] * float.Lerp( r.X, r.Y, random.Float() ) ) + ( down[at] * float.Lerp( r.Z, r.W, random.Float() ) );

				var axis = n;
				var direction = BakeMath.CosineAround( axis, random.Float(), random.Float() );

				if ( Vec3.Dot( direction, g ) <= 0.0f )
				{
					axis = g;
					direction = BakeMath.CosineAround( axis, random.Float(), random.Float() );
				}

				var density = scene.Emitters.Length > 0 || aiming ? Vec3.Dot( axis, direction ) * BakeMath.InvPi : 0.0f;
				var arriving = scene.Arriving( BakeScene.Offset( p, g, direction ), direction, density, ref random, out var behind, out var escaped );

				//A spot of the texel inside something counts for nothing: the rest of the texel is what it shows
				if ( behind )
				{
					inside++;
					continue;
				}

				if ( escaped ) open += new Vec4( direction, 1.0f ) * BakeScene.FirstVeil;

				//And a lit spot of the cache aimed at by its power, weighed against the cosine ray's chance of that way
				var aimed = Vec3.Zero;
				var litLight = scene.TowardsCache( p, g, ref random, out var towardsLit, out var litChance );

				if ( litChance > 0.0f )
				{
					var facing = Vec3.Dot( n, towardsLit );
					if ( facing > 0.0f ) aimed = litLight * ( facing * BakeMath.InvPi * BakeScene.Weight( litChance, facing * BakeMath.InvPi ) / litChance );
				}

				var lean = Vec3.Zero;
				var lit = ( mode & BakeGpu.ModeNoDirect ) != 0 ? Vec3.Zero
					: scene.Direct( p, n, g, ref random, ( mode & BakeGpu.ModeAllLights ) == 0, ref lean, ref shadeLit, ref shadeWhole )
						+ scene.GlowDirect( p, n, g, ref random, ref lean );

				//And a leaf's back, through it: with the bounced light, which the denoiser smooths, and in no band
				var through = transmitter[at] == 0 ? Vec3.Zero
					: scene.Transmission( p, n, g, scene.Materials[transmitter[at] - 1].Transmits, mode, aiming, ref random );
				var luma = BakeMath.Luma( arriving + aimed + through );

				sum += lit + arriving + aimed + through;
				straight += lit;
				squares += luma * luma;

				bands[0] += ( direction * arriving.X ) + ( towardsLit * aimed.X );
				bands[1] += ( direction * arriving.Y ) + ( towardsLit * aimed.Y );
				bands[2] += ( direction * arriving.Z ) + ( towardsLit * aimed.Z );

				//The lights' way, and their light as it would fall square on - see bake_trace.slang's main
				var leaning = lean.Length();

				if ( leaning > 1e-8f )
				{
					var l = lean / leaning;
					var square = lit / MathF.Max( Vec3.Dot( n, l ), 0.1f );

					bands[3] += l * square.X;
					bands[4] += l * square.Y;
					bands[5] += l * square.Z;
				}
			}

			End( at, sum, straight, squares, inside, samples, bands );
			sky[at] = SkyOf( open, Math.Max( samples - inside, 1 ), scene.SkyStrength );
			shade[at] = ShadeOf( shadeLit, shadeWhole );

			progress.Step();
		} );
	}

	//A texel out of what its samples summed: the bounce apart from the straight light, how far the bounce's mean can be
	//trusted (what the filter may smooth over), and whether enough of the texel was outside things to keep
	private void End( int at, Vec3 sum, Vec3 straight, float squares, int inside, int samples, ReadOnlySpan<Vec3> bands )
	{
		var kept = Math.Max( samples - inside, 1 );

		for ( var c = 0; c < 3; ++c )
		{
			bounceBand[( at * 3 ) + c] = bands[c] / kept;
			straightBand[( at * 3 ) + c] = bands[3 + c] / kept;
		}

		direct[at] = straight / kept;
		light[at] = ( sum - straight ) / kept;

		var meanLuma = BakeMath.Luma( light[at] );
		variance[at] = MathF.Max( ( squares / kept ) - ( meanLuma * meanLuma ), 0.0f ) / kept;

		//Mostly under a wall or inside a pillar: filled from its neighbours instead
		valid[at] = inside < samples * 0.75f;
	}

	//A channel's share that arrived; a texel no light of it reaches has nothing to shadow and is whole
	private static Vec4 ShadeOf( Vec4 through, Vec4 open )
	{
		var shade = Vec4.One;

		for ( var c = 0; c < BakeScene.ShadeChannels; ++c )
			if ( open[c] > 1e-8f ) shade[c] = Math.Clamp( through[c] / open[c], 0.0f, 1.0f );

		return shade;
	}

	//Escaping rays' direction sum and count into the way the sky is open and how much of it, at the bake's sky strength
	private static Vec4 SkyOf( Vec4 open, int samples, float strength )
	{
		var way = new Vec3( open.X, open.Y, open.Z );
		var length = way.Length();

		return new Vec4( length > 1e-6f ? way / length : Vec3.Zero, open.W / samples * strength );
	}

	/// <summary>Edge-avoiding a-trous denoise that keeps differences the noise cannot explain, like shadow edges</summary>
	public void Denoise( BakeScene scene, float strength )
	{
		if ( strength <= 0.0f ) return;

		//Joint non-local means on the card, a square at a time; the filter below where it cannot
		if ( DenoiseOnCard( scene, strength ) ) return;

		var count = Width * Height;
		var metres = new float[scene.Receivers.Count];

		//How big a texel of each square is in the world: how near a neighbour stands to be the same patch
		for ( var r = 0; r < scene.Receivers.Count; ++r )
		{
			var receiver = scene.Receivers[r];
			var total = 0.0f;
			var n = 0;

			for ( var y = receiver.Y; y < receiver.Y + receiver.Side - 1; ++y )
			{
				for ( var x = receiver.X; x < receiver.X + receiver.Side - 1; ++x )
				{
					var at = ( y * Width ) + x;
					var right = at + 1;

					if ( float.IsPositiveInfinity( owned[at] ) || float.IsPositiveInfinity( owned[right] ) ) continue;

					total += Vec3.Distance( position[at], position[right] );
					n++;
				}
			}

			metres[r] = n > 0 ? MathF.Max( total / n, 1e-3f ) : 0.1f;
		}

		float[] kernel = [1.0f / 16.0f, 1.0f / 4.0f, 3.0f / 8.0f, 1.0f / 4.0f, 1.0f / 16.0f];

		var from = light;
		var fromVariance = variance;

		for ( var pass = 0; pass < 4; ++pass )
		{
			var step = 1 << pass;
			var to = new Vec3[count];
			var toVariance = new float[count];

			var src = from;
			var srcVariance = fromVariance;

			Parallel.For( 0, Height, y =>
			{
				for ( var x = 0; x < Width; ++x )
				{
					var at = ( y * Width ) + x;

					if ( !valid[at] || float.IsPositiveInfinity( owned[at] ) )
					{
						to[at] = src[at];
						toVariance[at] = srcVariance[at];
						continue;
					}

					var owner = ownerOf[at];
					var texel = metres[owner];
					var centre = BakeMath.Luma( src[at] );
					var sigma = ( strength * MathF.Sqrt( srcVariance[at] ) ) + 1e-4f;

					var sum = Vec3.Zero;
					var noise = 0.0f;
					var total = 0.0f;

					for ( var dy = -2; dy <= 2; ++dy )
					{
						var yy = y + ( dy * step );
						if ( yy < 0 || yy >= Height ) continue;

						for ( var dx = -2; dx <= 2; ++dx )
						{
							var xx = x + ( dx * step );
							if ( xx < 0 || xx >= Width ) continue;

							var near = ( yy * Width ) + xx;

							if ( ownerOf[near] != owner || !valid[near] || float.IsPositiveInfinity( owned[near] ) ) continue;

							var facing = MathF.Max( Vec3.Dot( normal[at], normal[near] ), 0.0f );
							facing *= facing;
							facing *= facing;
							facing *= facing;

							var apart = Vec3.Distance( position[at], position[near] ) / ( texel * step * 2.5f );
							var standing = MathF.Exp( -apart * apart );

							var differs = MathF.Abs( BakeMath.Luma( src[near] ) - centre ) / sigma;
							var alike = MathF.Exp( -differs );

							var w = kernel[dx + 2] * kernel[dy + 2] * facing * standing * alike;

							sum += src[near] * w;
							noise += srcVariance[near] * w * w;
							total += w;
						}
					}

					if ( total > 1e-8f )
					{
						to[at] = sum / total;
						toVariance[at] = noise / ( total * total );
					}
					else
					{
						to[at] = src[at];
						toVariance[at] = srcVariance[at];
					}
				}
			} );

			from = to;
			fromVariance = toVariance;
		}

		light = from;
		variance = fromVariance;
	}

	//Each receiver's square with its gutter through BakeDenoise; false where the card would not take one
	private bool DenoiseOnCard( BakeScene scene, float strength )
	{
		foreach ( var receiver in scene.Receivers )
		{
			var x0 = Math.Max( receiver.X - Gutter, 0 );
			var y0 = Math.Max( receiver.Y - Gutter, 0 );
			var w = Math.Min( receiver.X + receiver.Side + Gutter, Width ) - x0;
			var h = Math.Min( receiver.Y + receiver.Side + Gutter, Height ) - y0;

			var part = new Vec3[w * h];
			var partBands = new Vec3[w * h * 3];
			var guide = new System.Numerics.Vector4[w * h];
			var facing = new Vec3[w * h];
			var spread = new float[w * h];

			for ( var y = 0; y < h; ++y )
				for ( var x = 0; x < w; ++x )
				{
					var at = ( ( y0 + y ) * Width ) + x0 + x;
					var i = ( y * w ) + x;
					var mine = !float.IsPositiveInfinity( owned[at] ) && ownerOf[at] == scene.Receivers.IndexOf( receiver );

					part[i] = light[at];
					for ( var c = 0; c < 3; ++c ) partBands[( i * 3 ) + c] = bounceBand[( at * 3 ) + c];
					guide[i] = new System.Numerics.Vector4( albedo[at], valid[at] && !pushed[at] ? 0.0f : 1.0f );
					facing[i] = mine ? normal[at] : Vec3.Zero;
					spread[i] = variance[at];
				}

			if ( !BakeDenoise.Run( part, partBands, guide, facing, spread, w, h, strength, CancellationToken.None ) ) return false;

			for ( var y = 0; y < h; ++y )
			{
				for ( var x = 0; x < w; ++x )
				{
					var i = ( y * w ) + x;
					if ( facing[i] == Vec3.Zero ) continue;

					var at = ( ( y0 + y ) * Width ) + x0 + x;
					light[at] = part[i];
					for ( var c = 0; c < 3; ++c ) bounceBand[( at * 3 ) + c] = partBands[( i * 3 ) + c];
				}
			}
		}

		return true;
	}

	/// <summary>Fills unanswered texels from answered ones of the same face beside them, a ring at a time.</summary>
	public void Dilate()
	{
		var count = Width * Height;
		var known = new bool[count];

		for ( var i = 0; i < count; ++i ) known[i] = valid[i] && !float.IsPositiveInfinity( owned[i] );

		Span<Vec3> bands = stackalloc Vec3[6];

		for ( var ring = 0; ring < Gutter + 6; ++ring )
		{
			var grown = (bool[])known.Clone();
			var any = false;

			for ( var y = 0; y < Height; ++y )
			{
				for ( var x = 0; x < Width; ++x )
				{
					var at = ( y * Width ) + x;
					if ( known[at] || ownerOf[at] < 0 ) continue;

					var sum = Vec3.Zero;
					var straight = Vec3.Zero;
					var skies = Vec4.Zero;
					var shades = Vec4.Zero;
					bands.Clear();
					var n = 0;

					//Only from the face it carries on: a box's faces sit side by side in its square, and a lit face's
					//light filled into a dark one's border is a bright line down the edge once the frame filters it
					var face = geometric[at];
					var island = isle[at];

					if ( face == Vec3.Zero )
					{
						for ( var i = 0; i < 9 && face == Vec3.Zero; ++i )
						{
							var xx = x + ( i % 3 ) - 1;
							var yy = y + ( i / 3 ) - 1;
							if ( xx < 0 || yy < 0 || xx >= Width || yy >= Height ) continue;

							var near = ( yy * Width ) + xx;
							if ( known[near] && ownerOf[near] == ownerOf[at] )
							{
								face = geometric[near];
								island = isle[near];
							}
						}
					}

					for ( var dy = -1; dy <= 1; ++dy )
					{
						for ( var dx = -1; dx <= 1; ++dx )
						{
							var xx = x + dx;
							var yy = y + dy;

							if ( xx < 0 || yy < 0 || xx >= Width || yy >= Height ) continue;

							var near = ( yy * Width ) + xx;
							if ( !known[near] || ownerOf[near] != ownerOf[at] ) continue;
							if ( face != Vec3.Zero && Vec3.Dot( geometric[near], face ) < 0.9f ) continue;

							sum += light[near];
							straight += direct[near];

							for ( var c = 0; c < 3; ++c )
							{
								bands[c] += bounceBand[( near * 3 ) + c];
								bands[3 + c] += straightBand[( near * 3 ) + c];
							}
							skies += sky[near];
							shades += shade[near];
							n++;
						}
					}

					if ( n == 0 ) continue;

					light[at] = sum / n;
					direct[at] = straight / n;

					for ( var c = 0; c < 3; ++c )
					{
						bounceBand[( at * 3 ) + c] = bands[c] / n;
						straightBand[( at * 3 ) + c] = bands[3 + c] / n;
					}
					sky[at] = skies / n;
					shade[at] = shades / n;

					//Its normal from the same neighbours, for the directions below; its face for the next ring
					if ( float.IsPositiveInfinity( owned[at] ) ) normal[at] = Vec3.Zero;
					geometric[at] = face;
					isle[at] = island;

					grown[at] = true;
					any = true;
				}
			}

			known = grown;
			if ( !any ) break;
		}
	}

	/// <summary>Into the two pictures the frame reads - see BakedSheet in frame.slang.</summary>
	public void Encode()
	{
		var count = Width * Height;

		Light = new Half[count * 4];
		Sky = new Half[count * 4];
		Shade = new byte[count * 4];
		EncodeBand();

		for ( var i = 0; i < count; ++i )
		{
			var l = Vec3.Max( light[i] + direct[i], Vec3.Zero );

			Light[( i * 4 ) + 0] = (Half)Math.Min( l.X, 60000.0f );
			Light[( i * 4 ) + 1] = (Half)Math.Min( l.Y, 60000.0f );
			Light[( i * 4 ) + 2] = (Half)Math.Min( l.Z, 60000.0f );
			Light[( i * 4 ) + 3] = (Half)( ownerOf[i] < 0 ? 0.0f : isle[i] >= 0 ? IslandKey( isle[i] ) : FaceOf( geometric[i] ) );

			var open = sky[i];
			var openWay = new Vec3( open.X, open.Y, open.Z );
			if ( openWay.LengthSquared() > 1e-8f ) openWay = Vec3.Normalize( openWay );

			Sky[( i * 4 ) + 0] = (Half)openWay.X;
			Sky[( i * 4 ) + 1] = (Half)openWay.Y;
			Sky[( i * 4 ) + 2] = (Half)openWay.Z;
			Sky[( i * 4 ) + 3] = (Half)Math.Min( open.W, 60000.0f );

			//Where no surface is, whole: nothing reads it there but the frame's filter at a square's edge
			var lets = ownerOf[i] < 0 ? Vec4.One : shade[i];
			for ( var c = 0; c < 4; ++c ) Shade[( i * 4 ) + c] = (byte)MathF.Round( Math.Clamp( lets[c], 0.0f, 1.0f ) * 255.0f );
		}
	}

	//Each block's owned texel nearest its middle, lifted off its face, and its island as its key - as the frame's sheet
	/// <summary>Where the live bounce is gathered, a block of side texels each; none where no surface is.</summary>
	public void BounceBlocks( int side, out int width, out int height, out Vec3[] at, out Vec3[] facing, out int[] key )
	{
		width = ( Width + side - 1 ) / side;
		height = ( Height + side - 1 ) / side;
		at = new Vec3[width * height];
		facing = new Vec3[width * height];
		key = new int[width * height];

		for ( var by = 0; by < height; ++by )
		{
			for ( var bx = 0; bx < width; ++bx )
			{
				var best = -1;
				var nearest = float.MaxValue;
				var middle = ( side - 1 ) * 0.5f;

				for ( var y = by * side; y < Math.Min( ( by + 1 ) * side, Height ); ++y )
				{
					for ( var x = bx * side; x < Math.Min( ( bx + 1 ) * side, Width ); ++x )
					{
						var texel = ( y * Width ) + x;
						if ( float.IsPositiveInfinity( owned[texel] ) || geometric[texel] == Vec3.Zero ) continue;

						var dx = x - ( bx * side ) - middle;
						var dy = y - ( by * side ) - middle;
						var apart = ( dx * dx ) + ( dy * dy );
						if ( apart >= nearest ) continue;

						nearest = apart;
						best = texel;
					}
				}

				if ( best < 0 ) continue;

				var block = ( by * width ) + bx;
				facing[block] = geometric[best];
				key[block] = isle[best] >= 0 ? isle[best] : -1 - ( ( ownerOf[best] * 64 ) + (int)( FaceOf( geometric[best] ) * 64.0f ) );
				at[block] = position[best] + ( geometric[best] * ( BakeScene.Lift * 4.0f ) );
			}
		}
	}

	/// <summary>Where a receiver's square is, as the surface reads it: scale in xy, offset in zw.</summary>
	public System.Numerics.Vector4 Rect( BakeReceiver receiver )
		=> new( receiver.Side / (float)Width, receiver.Side / (float)Height, receiver.X / (float)Width, receiver.Y / (float)Height );
}
