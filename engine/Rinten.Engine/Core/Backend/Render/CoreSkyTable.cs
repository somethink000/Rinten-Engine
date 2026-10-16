using System;
using Vec3 = System.Numerics.Vector3;

namespace Rinten;

//The sky table's shape, and the engine's default sky on the CPU for the light bake; devices draw theirs
internal static class CoreSkyTable
{
	//Samples per axis; must match SkyTableSide in frame.slang, VkDevice and BrowserDevice
	internal const int Side = 64;

	//Everything the colour depends on besides the direction asked about
	internal readonly struct Air
	{
		public readonly Vec3 Zenith, Horizon, Ground, SunDirection, SunLight;
		public readonly float Night;
		public readonly bool HasSun;

		public Air( Vec3 zenith, Vec3 horizon, Vec3 ground,
			Vec3 sunDirection, Vec3 sunLight, float night, bool hasSun )
		{
			Zenith = zenith; Horizon = horizon; Ground = ground;
			SunDirection = sunDirection; SunLight = sunLight;
			Night = night; HasSun = hasSun;
		}
	}

	private static float Saturate( float v ) => v < 0.0f ? 0.0f : v > 1.0f ? 1.0f : v;

	private static Vec3 Lerp( Vec3 a, Vec3 b, float t ) => a + ( ( b - a ) * t );

	private static float Smoothstep( float edge0, float edge1, float v )
	{
		var t = Saturate( ( v - edge0 ) / ( edge1 - edge0 ) );
		return t * t * ( 3.0f - ( 2.0f * t ) );
	}

	//The engine's default sky from the scene's colours and sun, for a bake without the sky shader
	internal static Vec3 ColourOf( in Air air, float height, float mu )
	{
		if ( !air.HasSun )
		{
			var upper = Lerp( air.Horizon, air.Zenith, Saturate( height ) );
			var lower = Lerp( air.Horizon, air.Ground, Saturate( -height ) );
			return Lerp( lower, upper, Saturate( ( height * 0.5f ) + 0.5f ) );
		}

		var luma = new Vec3( 0.2126f, 0.7152f, 0.0722f );

		var strength = MathF.Max( System.Numerics.Vector3.Dot( air.SunLight, luma ), 1e-4f );
		var hue = air.SunLight / strength;
		var warmth = Saturate( ( hue.X - hue.Z ) * 0.6f );
		var low = Saturate( 1.0f - ( air.SunDirection.Y * 2.2f ) );
		var up = Saturate( ( height * 0.5f ) + 0.5f );

		var sky = System.Numerics.Vector3.Zero;

		if ( air.Night < 1.0f )
		{
			var logUp = MathF.Log2( MathF.Max( up, 1e-4f ) );
			var phase = 0.5f + ( 0.5f * mu * mu );

			var warmed = hue * System.Numerics.Vector3.Dot( air.Horizon, luma );
			var edge = Lerp( air.Horizon, warmed, low * ( 0.45f + ( warmth * 0.55f ) ) );
			var middle = Lerp( edge, air.Zenith, 0.55f );

			var day = Lerp( edge, Lerp( middle, air.Zenith, MathF.Pow( 2.0f, logUp * 0.75f ) ),
				MathF.Pow( 2.0f, logUp * 0.30f ) );

			day *= strength * phase * ( 0.40f + ( up * 0.60f ) );
			day = Lerp( day, day * hue, Saturate( mu * MathF.Sqrt( mu ) * low * warmth ) );

			var band = 1.0f - up;
			var haze = band * band * MathF.Sqrt( band ) * ( 0.12f + ( low * 0.30f ) );

			//The glow round the sun, as sky.shader's SkyLight has it, so a page reflects the same day as a desktop
			var mu2 = mu * mu;
			haze += mu2 * mu2 * mu2 * ( 0.20f + ( low * 0.50f ) );

			sky = day + ( air.SunLight * haze );

		}

		if ( air.Night > 0.0f )
		{
			var evening = air.Zenith * 0.05f * ( 0.35f + ( MathF.Sqrt( up ) * 0.65f ) );
			sky = air.Night >= 1.0f ? evening : Lerp( sky, evening, air.Night );
		}

		var earth = air.Ground * ( ( ( 0.40f + ( strength * 0.60f ) ) * ( 1.0f - air.Night ) ) + ( 0.10f * air.Night ) );

		return Lerp( sky, earth, Smoothstep( 0.0f, 0.35f, -height ) );
	}
}
