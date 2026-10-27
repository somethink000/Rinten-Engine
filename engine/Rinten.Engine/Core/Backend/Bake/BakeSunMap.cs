using System;
using System.Threading;
using System.Threading.Tasks;
using Vec3 = System.Numerics.Vector3;
using Vec4 = System.Numerics.Vector4;

namespace Rinten.Bake;

//The still world's depth down the baked sun, as UE's stationary sun keeps one: what the frame reads for the still world's
//shadow wherever no lightmap holds it - the ground, and all that moves - so a still thing's shadow is the bake's
internal sealed class BakeSunMap
{
	//Texels a side at most, and the finest a texel is in metres
	private const int MostSide = 2048;
	private const float Finest = 0.1f;

	/// <summary>Depths a side; nought where there is no map.</summary>
	public int Side;

	/// <summary>World to the map: x across and y down it nought to one, z the depth down the sun over its range - a row
	/// each, its xyz dotted with the point and w added.</summary>
	public Vec4 Across, Down, Deep;

	/// <summary>A texel's width over the depth range: how far down the sun a texel's own slope may reach.</summary>
	public float TexelDeep;

	/// <summary>Two depths a texel of four bytes, high byte first; 65535 where nothing still stands - see Depth.</summary>
	public byte[] Packed = [];

	/// <summary>The sun it is of.</summary>
	public Guid Sun;

	/// <summary>Traces the map down the sun with this id, where it is in the bake and casts; false where it is not.</summary>
	public bool Trace( BakeScene scene, Guid sun, BakeProgress progress, CancellationToken cancel )
	{
		var light = scene.Lights.Find( x => x.Kind == 0 && x.Shadows && x.Id == sun );
		if ( light is null || scene.Corners.Length == 0 ) return false;

		var forward = Vec3.Normalize( light.Forward );
		var helper = MathF.Abs( forward.Y ) < 0.9f ? Vec3.UnitY : Vec3.UnitX;
		var right = Vec3.Normalize( Vec3.Cross( forward, helper ) );
		var up = Vec3.Cross( right, forward );

		//The bounds' corners on the sun's axes
		var low = new Vec3( float.MaxValue );
		var high = new Vec3( float.MinValue );
		var box = scene.Bounds;

		for ( var k = 0; k < 8; ++k )
		{
			var corner = new Vec3( ( k & 1 ) != 0 ? box.Maxs.x : box.Mins.x, ( k & 2 ) != 0 ? box.Maxs.y : box.Mins.y, ( k & 4 ) != 0 ? box.Maxs.z : box.Mins.z );
			var on = new Vec3( Vec3.Dot( corner, right ), Vec3.Dot( corner, up ), Vec3.Dot( corner, forward ) );

			low = Vec3.Min( low, on );
			high = Vec3.Max( high, on );
		}

		//Square, an even number of texels a side for two to a texel, the depth a metre past either end
		var extent = MathF.Max( MathF.Max( high.X - low.X, high.Y - low.Y ), 1.0f );
		Side = Math.Clamp( (int)MathF.Ceiling( extent / Finest ), 64, MostSide ) & ~1;

		var texel = extent / Side;
		var near = low.Z - 1.0f;
		var range = ( high.Z + 1.0f ) - near;

		Across = new Vec4( right / extent, -low.X / extent );
		Down = new Vec4( up / extent, -low.Y / extent );
		Deep = new Vec4( forward / range, -near / range );
		TexelDeep = texel / range;
		Sun = sun;

		var depth = new ushort[Side * Side];
		progress.Begin( "Tracing the sun's still shadow", Side );

		Parallel.For( 0, Side, new ParallelOptions { CancellationToken = cancel }, y =>
		{
			for ( var x = 0; x < Side; ++x )
			{
				var origin = ( right * ( low.X + ( ( x + 0.5f ) * texel ) ) ) + ( up * ( low.Y + ( ( y + 0.5f ) * texel ) ) ) + ( forward * near );

				depth[( y * Side ) + x] = scene.Bvh.Nearest( origin, forward, range, out var hit )
					? (ushort)Math.Min( MathF.Round( hit.Distance / range * 65534.0f ), 65534.0f )
					: ushort.MaxValue;
			}

			progress.Step();
		} );

		Packed = new byte[Side * Side * 2];

		for ( var i = 0; i < depth.Length; ++i )
		{
			Packed[i * 2] = (byte)( depth[i] >> 8 );
			Packed[( i * 2 ) + 1] = (byte)depth[i];
		}

		return true;
	}
}
