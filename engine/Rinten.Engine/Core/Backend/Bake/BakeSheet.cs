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

	public int Width, Height;

	/// <summary>Four halves a texel: the light, and one where the texel is a surface's.</summary>
	public Half[] Light;

	/// <summary>Four halves a texel: which way the light came from, and how much it leans.</summary>
	public Half[] Ways;

	/// <summary>Four halves a texel: which way the sky is open xyz, how much is seen w - see SkyLit.</summary>
	public Half[] Sky;

	//One entry an atlas texel while baking.
	private Vec3[] position;
	private Vec3[] normal;
	private Vec3[] geometric;
	private float[] owned;        //Distance from the texel's centre to its triangle, in texels; +inf where none
	private int[] ownerOf;        //Receiver whose square it is in, -1 where none

	private Vec3[] light;
	private Vec3[] towards;
	private float[] variance;
	private bool[] valid;

	//The sky straight from the texel: the escaping rays' direction summed xyz, their share w
	private Vec4[] sky;

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

			for ( var t = receiver.FirstTriangle; t < receiver.FirstTriangle + receiver.TriangleCount; ++t )
			{
				var a = scene.Corners[t * 3];
				var b = scene.Corners[( t * 3 ) + 1];
				var c = scene.Corners[( t * 3 ) + 2];

				world += Vec3.Cross( scene.Positions[b] - scene.Positions[a], scene.Positions[c] - scene.Positions[a] ).Length() * 0.5f;

				var ua = scene.Uv1[a];
				var ub = scene.Uv1[b];
				var uc = scene.Uv1[c];

				sheet += MathF.Abs( ( ( ub.X - ua.X ) * ( uc.Y - ua.Y ) ) - ( ( uc.X - ua.X ) * ( ub.Y - ua.Y ) ) ) * 0.5f;
			}

			//The unwrap fills some part of its square; the square is as big as makes that part carry the density.
			wanted[r] = sheet > 1e-6f ? MathF.Sqrt( world / sheet ) : MathF.Sqrt( world );
		}

		for ( var attempt = 0; attempt < 48; ++attempt )
		{
			for ( var r = 0; r < receivers.Count; ++r )
				receivers[r].Side = Math.Clamp( (int)MathF.Ceiling( wanted[r] * density ), 8, Math.Max( most / 2, 8 ) );

			//The smallest power of two it all goes into, up to the most.
			for ( var size = 128; size <= most; size *= 2 )
			{
				if ( Shelve( receivers, size ) )
				{
					side = size;
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
		owned = new float[count];
		ownerOf = new int[count];

		Array.Fill( owned, float.PositiveInfinity );
		Array.Fill( ownerOf, -1 );

		for ( var r = 0; r < scene.Receivers.Count; ++r )
		{
			var receiver = scene.Receivers[r];

			for ( var y = receiver.Y - Gutter; y < receiver.Y + receiver.Side + Gutter; ++y )
				for ( var x = receiver.X - Gutter; x < receiver.X + receiver.Side + Gutter; ++x )
					if ( x >= 0 && y >= 0 && x < side && y < side ) ownerOf[( y * side ) + x] = r;

			for ( var t = receiver.FirstTriangle; t < receiver.FirstTriangle + receiver.TriangleCount; ++t )
				Triangle( scene, receiver, t, side );
		}
	}

	private void Triangle( BakeScene scene, BakeReceiver receiver, int t, int side )
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
			}
		}
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
	public bool TraceOnCard( BakeScene scene, int samples, BakeProgress progress, CancellationToken cancel )
	{
		var count = Width * Height;

		var texels = new List<int>();
		for ( var i = 0; i < count; ++i ) if ( !float.IsPositiveInfinity( owned[i] ) ) texels.Add( i );

		if ( !BakeGpu.Trace( scene, position, normal, geometric, texels, samples, progress, cancel, out var sums ) )
			return false;

		light = new Vec3[count];
		towards = new Vec3[count];
		variance = new float[count];
		valid = new bool[count];
		sky = new Vec4[count];

		for ( var i = 0; i < texels.Count; ++i )
		{
			var at = texels[i];
			var o = i * BakeGpu.SheetFloats;

			var sum = new Vec3( sums[o], sums[o + 1], sums[o + 2] );
			var squares = sums[o + 3];
			var way = new Vec3( sums[o + 4], sums[o + 5], sums[o + 6] );
			var weight = sums[o + 7];
			var inside = sums[o + 8];

			var mean = sum / samples;
			var meanLuma = BakeMath.Luma( mean );

			light[at] = mean;
			towards[at] = weight > 1e-8f ? way / weight : Vec3.Zero;
			variance[at] = MathF.Max( ( squares / samples ) - ( meanLuma * meanLuma ), 0.0f ) / samples;
			valid[at] = inside < samples * 0.35f;

			sky[at] = SkyOf( new Vec4( sums[o + 12], sums[o + 13], sums[o + 14], sums[o + 15] ), samples, scene.SkyStrength );
		}

		return true;
	}

	/// <summary>Traces every texel, on every thread; <paramref name="progress"/> counts texels as they finish.</summary>
	public void Trace( BakeScene scene, int samples, BakeProgress progress, CancellationToken cancel )
	{
		var count = Width * Height;

		light = new Vec3[count];
		towards = new Vec3[count];
		variance = new float[count];
		valid = new bool[count];
		sky = new Vec4[count];

		var texels = new List<int>();
		for ( var i = 0; i < count; ++i ) if ( !float.IsPositiveInfinity( owned[i] ) ) texels.Add( i );

		progress.Begin( "Tracing the surfaces", texels.Count );

		Parallel.For( 0, texels.Count, new ParallelOptions { CancellationToken = cancel }, i =>
		{
			var at = texels[i];
			var random = new BakeRandom( (ulong)at * 0x9E3779B97F4A7C15ul, (ulong)at );

			var p = position[at];
			var n = normal[at];
			var g = geometric[at];

			var sum = Vec3.Zero;
			var way = Vec3.Zero;
			var weight = 0.0f;
			var squares = 0.0f;
			var inside = 0;
			var open = Vec4.Zero;

			for ( var s = 0; s < samples; ++s )
			{
				var lean = Vec3.Zero;
				var direct = scene.Direct( p, n, g, ref random, true, ref lean ) + scene.GlowDirect( p, n, g, ref random, ref lean );

				var axis = n;
				var direction = BakeMath.CosineAround( axis, random.Float(), random.Float() );

				if ( Vec3.Dot( direction, g ) <= 0.0f )
				{
					axis = g;
					direction = BakeMath.CosineAround( axis, random.Float(), random.Float() );
				}

				var density = scene.Emitters.Length > 0 ? Vec3.Dot( axis, direction ) * BakeMath.InvPi : 0.0f;
				var arriving = scene.Arriving( BakeScene.Offset( p, g, direction ), direction, density, ref random, out var behind, out var escaped );
				if ( behind ) inside++;
				if ( escaped ) open += new Vec4( direction, 1.0f );

				var one = direct + arriving;
				var luma = BakeMath.Luma( one );

				sum += one;
				squares += luma * luma;

				way += lean + ( direction * BakeMath.Luma( arriving ) );
				weight += BakeMath.Luma( direct ) + BakeMath.Luma( arriving );
			}

			var mean = sum / samples;
			var meanLuma = BakeMath.Luma( mean );

			light[at] = mean;
			towards[at] = weight > 1e-8f ? way / weight : Vec3.Zero;

			//How far this texel's mean can be trusted - what the filter below is allowed to smooth over.
			variance[at] = MathF.Max( ( squares / samples ) - ( meanLuma * meanLuma ), 0.0f ) / samples;

			//A texel whose rays mostly came out of the back of something is under a wall or inside a pillar,
			//and what it measured is the dark inside of that. It is filled from its neighbours instead.
			valid[at] = inside < samples * 0.35f;

			sky[at] = SkyOf( open, samples, scene.SkyStrength );

			progress.Step();
		} );
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
		var fromWays = towards;
		var fromVariance = variance;

		for ( var pass = 0; pass < 4; ++pass )
		{
			var step = 1 << pass;
			var to = new Vec3[count];
			var toWays = new Vec3[count];
			var toVariance = new float[count];

			var src = from;
			var srcWays = fromWays;
			var srcVariance = fromVariance;

			Parallel.For( 0, Height, y =>
			{
				for ( var x = 0; x < Width; ++x )
				{
					var at = ( y * Width ) + x;

					if ( !valid[at] || float.IsPositiveInfinity( owned[at] ) )
					{
						to[at] = src[at];
						toWays[at] = srcWays[at];
						toVariance[at] = srcVariance[at];
						continue;
					}

					var owner = ownerOf[at];
					var texel = metres[owner];
					var centre = BakeMath.Luma( src[at] );
					var sigma = ( strength * MathF.Sqrt( srcVariance[at] ) ) + 1e-4f;

					var sum = Vec3.Zero;
					var ways = Vec3.Zero;
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
							ways += srcWays[near] * w;
							noise += srcVariance[near] * w * w;
							total += w;
						}
					}

					if ( total > 1e-8f )
					{
						to[at] = sum / total;
						toWays[at] = ways / total;
						toVariance[at] = noise / ( total * total );
					}
					else
					{
						to[at] = src[at];
						toWays[at] = srcWays[at];
						toVariance[at] = srcVariance[at];
					}
				}
			} );

			from = to;
			fromWays = toWays;
			fromVariance = toVariance;
		}

		light = from;
		towards = fromWays;
		variance = fromVariance;
	}

	/// <summary>Fills unanswered texels from answered ones beside them, a ring at a time, for clean edges.</summary>
	public void Dilate()
	{
		var count = Width * Height;
		var known = new bool[count];

		for ( var i = 0; i < count; ++i ) known[i] = valid[i] && !float.IsPositiveInfinity( owned[i] );

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
					var ways = Vec3.Zero;
					var skies = Vec4.Zero;
					var n = 0;

					for ( var dy = -1; dy <= 1; ++dy )
					{
						for ( var dx = -1; dx <= 1; ++dx )
						{
							var xx = x + dx;
							var yy = y + dy;

							if ( xx < 0 || yy < 0 || xx >= Width || yy >= Height ) continue;

							var near = ( yy * Width ) + xx;
							if ( !known[near] || ownerOf[near] != ownerOf[at] ) continue;

							sum += light[near];
							ways += towards[near];
							skies += sky[near];
							n++;
						}
					}

					if ( n == 0 ) continue;

					light[at] = sum / n;
					towards[at] = ways / n;
					sky[at] = skies / n;

					//Its normal from the same neighbours, for the directions below.
					if ( float.IsPositiveInfinity( owned[at] ) ) normal[at] = Vec3.Zero;

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
		Ways = new Half[count * 4];
		Sky = new Half[count * 4];

		for ( var i = 0; i < count; ++i )
		{
			var l = Vec3.Max( light[i], Vec3.Zero );

			Light[( i * 4 ) + 0] = (Half)Math.Min( l.X, 60000.0f );
			Light[( i * 4 ) + 1] = (Half)Math.Min( l.Y, 60000.0f );
			Light[( i * 4 ) + 2] = (Half)Math.Min( l.Z, 60000.0f );
			Light[( i * 4 ) + 3] = (Half)( ownerOf[i] >= 0 ? 1.0f : 0.0f );

			var way = towards[i];
			if ( way.LengthSquared() > 1.0f ) way = Vec3.Normalize( way );

			//How much light the unbent normal gets by the lean below - what it is divided back to
			var n = normal[i];
			var lean = n.LengthSquared() > 0.5f ? ( Vec3.Dot( n, way ) * 0.5f ) + 0.5f : 0.5f + ( way.Length() * 0.5f );

			Ways[( i * 4 ) + 0] = (Half)way.X;
			Ways[( i * 4 ) + 1] = (Half)way.Y;
			Ways[( i * 4 ) + 2] = (Half)way.Z;
			Ways[( i * 4 ) + 3] = (Half)lean;

			var open = sky[i];
			var openWay = new Vec3( open.X, open.Y, open.Z );
			if ( openWay.LengthSquared() > 1e-8f ) openWay = Vec3.Normalize( openWay );

			Sky[( i * 4 ) + 0] = (Half)openWay.X;
			Sky[( i * 4 ) + 1] = (Half)openWay.Y;
			Sky[( i * 4 ) + 2] = (Half)openWay.Z;
			Sky[( i * 4 ) + 3] = (Half)Math.Min( open.W, 60000.0f );
		}
	}

	/// <summary>Where a receiver's square is, as the surface reads it: scale in xy, offset in zw.</summary>
	public System.Numerics.Vector4 Rect( BakeReceiver receiver )
		=> new( receiver.Side / (float)Width, receiver.Side / (float)Height, receiver.X / (float)Width, receiver.Y / (float)Height );
}
