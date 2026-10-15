using System;
using System.IO;

namespace Rinten.LtcFit;

//Compares a table's polygon integral, read the way the shader reads it, with GGX integrated by brute force
internal static class Check
{
	private const int Size = 64;

	public static int Run( string path )
	{
		var bytes = File.ReadAllBytes( path );
		var table = new Half[bytes.Length / 2];
		System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>( bytes ).CopyTo( table );

		//A quad above the surface, off to the side of the mirror direction and on it
		Vec3[][] quads =
		[
			[new( -0.5, -0.5, 1.0 ), new( 0.5, -0.5, 1.0 ), new( 0.5, 0.5, 1.0 ), new( -0.5, 0.5, 1.0 )],
			[new( 0.4, -0.3, 0.6 ), new( 1.2, -0.3, 0.6 ), new( 1.2, 0.3, 0.9 ), new( 0.4, 0.3, 0.9 )],
			[new( -1.5, -0.2, 0.4 ), new( -0.7, -0.2, 0.4 ), new( -0.7, 0.2, 0.7 ), new( -1.5, 0.2, 0.7 )],
		];

		var worst = 0.0;

		foreach ( var roughness in new[] { 0.15, 0.3, 0.5, 0.8, 1.0 } )
		{
			foreach ( var cosTheta in new[] { 0.95, 0.7, 0.4 } )
			{
				var theta = Math.Acos( cosTheta );
				var v = new Vec3( Math.Sin( theta ), 0, cosTheta );
				var alpha = roughness * roughness;

				var x = Math.Round( roughness * ( Size - 1 ) );
				var y = Math.Round( Math.Sqrt( 1.0 - cosTheta ) * ( Size - 1 ) );
				var i = (int)( x + ( y * Size ) );

				var a = (double)table[( i * 4 ) + 0];
				var b = (double)table[( i * 4 ) + 1];
				var c = (double)table[( i * 4 ) + 2];
				var d = (double)table[( i * 4 ) + 3];
				var magnitude = (double)table[( Size * Size * 4 ) + ( i * 4 )];

				foreach ( var quad in quads )
				{
					//The shader's sums: corners through the inverse, normalised, edges integrated
					var p = new Vec3[4];
					for ( var k = 0; k < 4; ++k )
						p[k] = new Vec3( ( a * quad[k].X ) + ( b * quad[k].Z ), quad[k].Y, ( c * quad[k].X ) + ( d * quad[k].Z ) ).Normalized();

					var sum = 0.0;
					for ( var k = 0; k < 4; ++k ) sum += Edge( p[k], p[( k + 1 ) % 4] );

					var fitted = magnitude * Math.Abs( sum ) / ( 2.0 * Math.PI );
					var truth = Brute( quad, v, alpha );
					var error = Math.Abs( fitted - truth ) / Math.Max( truth, 1e-4 );

					worst = Math.Max( worst, truth > 0.01 ? error : 0.0 );
					Console.WriteLine( $"r {roughness:0.00} cos {cosTheta:0.00}: ltc {fitted:0.0000} ggx {truth:0.0000} ({error * 100:0.0}%)" );
				}
			}
		}

		Console.WriteLine( $"worst relative error where the quad holds more than 1%: {worst * 100:0.0}%" );
		return 0;
	}

	private static double Edge( Vec3 a, Vec3 b )
	{
		var cos = Math.Clamp( Vec3.Dot( a, b ), -1.0, 1.0 );
		var theta = Math.Acos( cos );
		var z = ( a.X * b.Y ) - ( a.Y * b.X );

		return z * ( theta > 1e-6 ? theta / Math.Sin( theta ) : 1.0 );
	}

	//GGX times cosine over the quad by sampling its surface
	private static double Brute( Vec3[] quad, Vec3 v, double alpha )
	{
		const int n = 1500;
		var sum = 0.0;
		var e1 = quad[1] - quad[0];
		var e2 = quad[3] - quad[0];
		var normal = Cross( e1, e2 );
		var area = normal.Length;
		normal = normal * ( 1.0 / area );

		for ( var j = 0; j < n; ++j )
		{
			for ( var i = 0; i < n; ++i )
			{
				var u = ( i + 0.5 ) / n;
				var w = ( j + 0.5 ) / n;

				//Bilinear over the quad, which is planar here
				var point = quad[0] + ( e1 * u ) + ( e2 * w ) + ( ( quad[2] - quad[1] - quad[3] + quad[0] ) * ( u * w ) );
				var length = point.Length;
				var l = point * ( 1.0 / length );

				var solid = area * Math.Abs( Vec3.Dot( l, normal ) ) / ( length * length ) / ( n * n );
				sum += Ggx.Eval( v, l, alpha, out _ ) * solid;
			}
		}

		return sum;
	}

	private static Vec3 Cross( Vec3 a, Vec3 b ) => new( ( a.Y * b.Z ) - ( a.Z * b.Y ), ( a.Z * b.X ) - ( a.X * b.Z ), ( a.X * b.Y ) - ( a.Y * b.X ) );
}
