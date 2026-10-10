using System;
using Vec3 = System.Numerics.Vector3;

namespace Rinten;

//Sky colour table baked on the CPU for the browser and the light bake; other devices use the GPU
//Indexed by sqrt(height) and sun angle; a filtered texture is cheaper than evaluating per pixel
internal static class CoreSkyTable
{
	//Samples per axis; must match SkyTableSide in frame.slang, VkDevice and BrowserDevice
	internal const int Side = 64;

	//How many bytes the picture is: four halves a texel.
	internal const int Bytes = Side * Side * 8;

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

	//The engine's default sky from the scene's colours and sun; fallback where sky shaders can't run
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

	//What the table was last filled for, so a still sky is filled once.
	private static Air last;
	private static bool filled;

	//Approximate equality so a slowly moving sun doesn't rebake the table every frame
	private static bool Same( in Air a, in Air b )
		=> a.HasSun == b.HasSun && Near( a.Night, b.Night )
			&& Near( a.Zenith, b.Zenith ) && Near( a.Horizon, b.Horizon ) && Near( a.Ground, b.Ground )
			&& Near( a.SunLight, b.SunLight )
			&& System.Numerics.Vector3.Dot( a.SunDirection, b.SunDirection ) > 0.99999f;

	private static bool Near( float a, float b ) => MathF.Abs( a - b ) < 0.001f;

	private static bool Near( Vec3 a, Vec3 b )
		=> Near( a.X, b.X ) && Near( a.Y, b.Y ) && Near( a.Z, b.Z );

	//Bakes and sends the table only when the sky changed since the last send
	internal static unsafe void Send( in Air air )
	{
		if ( filled && sent is null && Same( air, last ) ) return;

		Bake( air );
		last = air;
		filled = true;
		sent = null;

		fixed ( byte* bytes = texels ) CoreRenderDevice.SendSkyTable( bytes, Side );
	}

	//Sends a panorama's table in place of the baked one, once per table
	internal static unsafe void Send( Half[] table )
	{
		if ( table is null || table.Length < Side * Side * 4 || ReferenceEquals( table, sent ) ) return;

		sent = table;
		filled = false;

		fixed ( Half* texels = table ) CoreRenderDevice.SendSkyTable( (byte*)texels, Side );
	}

	//The table made elsewhere that the device holds, or null where it holds the baked one.
	private static Half[] sent;

	//Sent again after a device came back, whatever the sky is doing.
	internal static void Forget()
	{
		filled = false;
		sent = null;
	}

	private static readonly byte[] texels = new byte[Bytes];

	private static void Half( byte[] into, int at, float value )
	{
		var half = (ushort)BitConverter.HalfToUInt16Bits( (System.Half)value );

		into[at + 0] = (byte)( half & 0xFF );
		into[at + 1] = (byte)( half >> 8 );
	}

	private static void Bake( in Air air )
	{
		//The two numbers the sky depends on, each over the whole of its range with a sample at either end: the
		//vertical by its square root, and how near the sun straight. SkyTableAt in frame.slang undoes both.
		for ( var row = 0; row < Side; row++ )
		{
			var u = row / (float)( Side - 1 );
			var height = ( u * u * 2.0f ) - 1.0f;

			for ( var column = 0; column < Side; column++ )
			{
				var mu = column / (float)( Side - 1 );
				var colour = ColourOf( air, height, mu );

				var slot = ( ( row * Side ) + column ) * 8;

				//Half floats, which is what the picture holds: the sky is brighter than one and never
				//finer than a thousandth of itself, so eleven bits of mantissa are more than it has.
				Half( texels, slot + 0, colour.X );
				Half( texels, slot + 2, colour.Y );
				Half( texels, slot + 4, colour.Z );

				//One in the alpha says there is a table here at all. A device that was never sent one reads nought
				//out of a cleared picture, and the sky is worked out the old way - see SkyTableAt in frame.slang.
				Half( texels, slot + 6, 1.0f );
			}
		}
	}
}
