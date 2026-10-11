using System;
using System.Runtime.CompilerServices;
using Vec3 = System.Numerics.Vector3;

namespace Rinten.Bake;

//The bake's own dice: one small generator a worker, seeded by what it is working on,
//so that a bake of the same scene is the same bake whichever core took which texel.
internal struct BakeRandom
{
	private ulong state;
	private readonly ulong stream;

	public BakeRandom( ulong seed, ulong sequence )
	{
		state = 0;
		stream = ( sequence << 1 ) | 1u;

		Next();
		state += seed;
		Next();
	}

	/// <summary>PCG's thirty-two bits.</summary>
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public uint Next()
	{
		var old = state;

		state = ( old * 6364136223846793005ul ) + stream;

		var shifted = (uint)( ( ( old >> 18 ) ^ old ) >> 27 );
		var turn = (int)( old >> 59 );

		return ( shifted >> turn ) | ( shifted << ( ( -turn ) & 31 ) );
	}

	/// <summary>Nought up to, and never, one.</summary>
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public float Float() => ( Next() >> 8 ) * ( 1.0f / 16777216.0f );
}

internal static class BakeMath
{
	public const float Pi = 3.14159265f;
	public const float InvPi = 1.0f / Pi;

	/// <summary>How bright a colour is to an eye.</summary>
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public static float Luma( Vec3 colour ) => ( colour.X * 0.2126f ) + ( colour.Y * 0.7152f ) + ( colour.Z * 0.0722f );

	/// <summary>Two axes square to a direction and each other - Duff's: no square root, never fails.</summary>
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public static void Basis( Vec3 normal, out Vec3 right, out Vec3 up )
	{
		var sign = MathF.CopySign( 1.0f, normal.Z );
		var a = -1.0f / ( sign + normal.Z );
		var b = normal.X * normal.Y * a;

		right = new Vec3( 1.0f + ( sign * normal.X * normal.X * a ), sign * b, -sign * normal.X );
		up = new Vec3( b, sign + ( normal.Y * normal.Y * a ), -normal.Y );
	}

	/// <summary>A direction off a surface by the cosine, so a sample is worth the light it finds.</summary>
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public static Vec3 CosineAround( Vec3 normal, float u, float v )
	{
		var radius = MathF.Sqrt( u );
		var angle = 2.0f * Pi * v;

		var x = radius * MathF.Cos( angle );
		var y = radius * MathF.Sin( angle );
		var z = MathF.Sqrt( MathF.Max( 0.0f, 1.0f - u ) );

		Basis( normal, out var right, out var up );

		return Vec3.Normalize( ( right * x ) + ( up * y ) + ( normal * z ) );
	}

	/// <summary>A direction out of all of them, every one as likely as any other.</summary>
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public static Vec3 AnyDirection( float u, float v )
	{
		var z = 1.0f - ( 2.0f * u );
		var radius = MathF.Sqrt( MathF.Max( 0.0f, 1.0f - ( z * z ) ) );
		var angle = 2.0f * Pi * v;

		return new Vec3( radius * MathF.Cos( angle ), z, radius * MathF.Sin( angle ) );
	}

	/// <summary>A direction inside a cone about an axis, all equally likely - where on the sun's disc a ray aims.</summary>
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public static Vec3 InCone( Vec3 axis, float cosine, float u, float v )
	{
		var z = 1.0f - ( u * ( 1.0f - cosine ) );
		var radius = MathF.Sqrt( MathF.Max( 0.0f, 1.0f - ( z * z ) ) );
		var angle = 2.0f * Pi * v;

		Basis( axis, out var right, out var up );

		return Vec3.Normalize( ( right * ( radius * MathF.Cos( angle ) ) ) + ( up * ( radius * MathF.Sin( angle ) ) ) + ( axis * z ) );
	}

	/// <summary>A direction on the unfolded octahedron, minus one to one, y the axis - as BakeGpu's sky.</summary>
	public static (float X, float Y) Octahedron( Vec3 direction )
	{
		var n = direction / MathF.Max( MathF.Abs( direction.X ) + MathF.Abs( direction.Y ) + MathF.Abs( direction.Z ), 1e-8f );

		if ( n.Y >= 0.0f ) return (n.X, n.Z);

		return (( 1.0f - MathF.Abs( n.Z ) ) * ( n.X >= 0.0f ? 1.0f : -1.0f ), ( 1.0f - MathF.Abs( n.X ) ) * ( n.Z >= 0.0f ? 1.0f : -1.0f ));
	}

	/// <summary>The direction at a point of the unfolded octahedron, Octahedron's inverse.</summary>
	public static Vec3 FromOctahedron( float x, float y )
	{
		var up = 1.0f - MathF.Abs( x ) - MathF.Abs( y );

		if ( up < 0.0f )
		{
			var fx = ( 1.0f - MathF.Abs( y ) ) * ( x >= 0.0f ? 1.0f : -1.0f );
			var fy = ( 1.0f - MathF.Abs( x ) ) * ( y >= 0.0f ? 1.0f : -1.0f );
			x = fx;
			y = fy;
		}

		return Vec3.Normalize( new Vec3( x, up, y ) );
	}

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public static float Saturate( float x ) => x < 0.0f ? 0.0f : x > 1.0f ? 1.0f : x;

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public static float Smoothstep( float low, float high, float x )
	{
		var t = Saturate( ( x - low ) / ( high - low ) );

		return t * t * ( 3.0f - ( 2.0f * t ) );
	}

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public static Vec3 Lerp( Vec3 a, Vec3 b, float t ) => a + ( ( b - a ) * t );

	/// <summary>An sRGB byte as the light it stands for.</summary>
	public static readonly float[] Linear = MakeLinear();

	private static float[] MakeLinear()
	{
		var table = new float[256];

		for ( var i = 0; i < 256; ++i )
		{
			var c = i / 255.0f;

			table[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow( ( c + 0.055f ) / 1.055f, 2.4f );
		}

		return table;
	}
}
