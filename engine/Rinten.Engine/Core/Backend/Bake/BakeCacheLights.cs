using System;
using System.Collections.Generic;
using Vec2 = System.Numerics.Vector2;
using Vec3 = System.Numerics.Vector3;

namespace Rinten.Bake;

//The last gather pass's lit triangles as lights the next one samples by power (bake_trace.slang's TowardsCache): a
//lampshade's inside lit from a few centimetres is a sliver of the view that random rays find too seldom to agree on
internal sealed class CacheLights
{
	//Floats a picked triangle takes: the triangle, power summed so far, a corner and two edges
	private const int RecordFloats = 11;

	/// <summary>How many triangles are lights.</summary>
	public int Count { get; private init; }

	/// <summary>Each world triangle's chance per square metre, then the lights' records, as g_lights.</summary>
	public float[] Floats { get; private init; } = [];

	/// <summary>Each lightmapped triangle by the light it leaves in the cache, four floats a texel.</summary>
	public static CacheLights Of( BakeScene scene, float[] cache, int side )
	{
		var triangles = scene.MaterialOf.Length;
		var power = new float[triangles];
		var area = new float[triangles];
		var lit = new List<int>();
		var sum = 0.0;

		foreach ( var receiver in scene.Receivers )
		{
			for ( var t = receiver.FirstTriangle; t < receiver.FirstTriangle + receiver.TriangleCount; ++t )
			{
				var a = scene.Positions[scene.Corners[t * 3]];
				var b = scene.Positions[scene.Corners[( t * 3 ) + 1]];
				var c = scene.Positions[scene.Corners[( t * 3 ) + 2]];

				area[t] = Vec3.Cross( b - a, c - a ).Length() * 0.5f;
				if ( area[t] <= 1e-10f ) continue;

				//The light leaving it, as read at its middle and halfway to each corner
				var leaving = Vec3.Zero;

				for ( var k = 0; k < 4; ++k )
				{
					var spot = k == 3 ? Atlas( scene, receiver, t, 1.0f / 3.0f, 1.0f / 3.0f )
						: Atlas( scene, receiver, t, k == 1 ? 0.5f : 0.25f, k == 2 ? 0.5f : 0.25f );

					var x = Math.Clamp( (int)spot.X, 0, side - 1 );
					var y = Math.Clamp( (int)spot.Y, 0, side - 1 );
					var at = ( ( y * side ) + x ) * 4;

					leaving += new Vec3( cache[at], cache[at + 1], cache[at + 2] );
				}

				power[t] = area[t] * BakeMath.Luma( scene.AlbedoAt( t, 1.0f / 3.0f, 1.0f / 3.0f ) * leaving * 0.25f );
				if ( power[t] <= 0.0f ) continue;

				lit.Add( t );
				sum += power[t];
			}
		}

		if ( lit.Count == 0 || sum <= 0.0 ) return new CacheLights();

		var floats = new float[triangles + ( lit.Count * RecordFloats )];
		var summed = 0.0;

		for ( var i = 0; i < lit.Count; ++i )
		{
			var t = lit[i];
			var share = power[t] / sum;
			summed += share;

			floats[t] = (float)( share / area[t] );

			var a = scene.Positions[scene.Corners[t * 3]];
			var o = triangles + ( i * RecordFloats );

			floats[o] = BitConverter.Int32BitsToSingle( t );
			floats[o + 1] = (float)summed;
			Put( floats, o + 2, a );
			Put( floats, o + 5, scene.Positions[scene.Corners[( t * 3 ) + 1]] - a );
			Put( floats, o + 8, scene.Positions[scene.Corners[( t * 3 ) + 2]] - a );
		}

		return new CacheLights { Count = lit.Count, Floats = floats };
	}

	//Where a spot of a triangle, by its weights of the second and third corner, lands on the sheet, in texels
	private static Vec2 Atlas( BakeScene scene, BakeReceiver receiver, int t, float u, float v )
	{
		Vec2 Corner( int k ) => ( Vec2.Clamp( scene.Uv1[scene.Corners[( t * 3 ) + k]], Vec2.Zero, Vec2.One ) * receiver.Side )
			+ new Vec2( receiver.X, receiver.Y );

		return ( Corner( 0 ) * ( 1.0f - u - v ) ) + ( Corner( 1 ) * u ) + ( Corner( 2 ) * v );
	}

	private static void Put( float[] to, int at, Vec3 v )
	{
		to[at] = v.X;
		to[at + 1] = v.Y;
		to[at + 2] = v.Z;
	}
}
