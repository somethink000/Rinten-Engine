using System;

namespace Rinten;

//Area lamps' LTC tables (engine/Tools/LtcFit) on the device, made the first frame a lamp has a shape - see AreaLamp
internal static class CoreAreaLights
{
	//Texels along each side: roughness across, sqrt( 1 - cos theta ) down
	private const int Side = 64;

	private static ulong inverse, scale;
	private static bool failed;
	private static int generation;

	//Writes the tables' indices into the block; false where they are not on the device, which leaves shapes as points
	internal static bool Write( float[] frame )
	{
		if ( !Ensure() ) return false;

		frame[CoreSceneRenderer.AreaAt + 0] = CoreRender.TextureIndex( inverse );
		frame[CoreSceneRenderer.AreaAt + 1] = CoreRender.TextureIndex( scale );

		return true;
	}

	private static bool Ensure()
	{
		//The old device's tables went with it; nothing to give back
		if ( generation != CoreResources.Cleared )
		{
			inverse = scale = 0;
			generation = CoreResources.Cleared;
		}

		if ( inverse != 0 && scale != 0 ) return true;
		if ( failed || !CoreNative.Available || CoreRenderDevice.PortableFrame ) return false;

		var assembly = typeof( CoreAreaLights ).Assembly;
		using var stream = assembly.GetManifestResourceStream( "ltc.bin" );

		var texels = Side * Side * 4;
		var table = new Half[texels * 2];
		var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes( table.AsSpan() );

		if ( stream is null || stream.ReadAtLeast( bytes, bytes.Length, throwOnEndOfStream: false ) < bytes.Length )
		{
			failed = true;
			Log.Warning( "the area lights' tables are not in the engine - shaped lamps light as points" );
			return false;
		}

		inverse = CoreRenderDevice.CreateTextureHalf( table.AsSpan( 0, texels ), Side, Side );
		scale = CoreRenderDevice.CreateTextureHalf( table.AsSpan( texels, texels ), Side, Side );

		failed = inverse == 0 || scale == 0;
		return !failed;
	}
}
