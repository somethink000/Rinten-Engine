using System;
using System.Runtime.InteropServices;

namespace Rinten;

//Frame lights, then per screen tile a bitmask of the lamps reaching it and per depth bin its run of lamps, then decals,
//in one buffer (set 1 b7) - the lamps go up sorted by view depth so that a bin's lamps are one run; see light_cull.slang
internal static class CoreLights
{
	//How many lights one frame may carry; light_cull.slang's MostWords is this over 32
	internal const int MostLights = 4096;

	//Floats a light takes: five float4s, as frame.slang has it; the fifth holds only the falloff's shape
	internal const int LightFloats = 20;

	//What the buffer says about itself before the lights.
	internal const int HeaderFloats = 16;

	//Tile size in pixels
	internal const int TileSize = 32;

	//Depth bins, even in the logarithm of depth from the near plane to Deep; the last runs to infinity
	internal const int Bins = 128;

	//Lights above which the grid is built; with fewer every pixel walks them all for less than the cull costs
	internal const int GridAbove = 16;

	//Metres the bins span
	internal const float Deep = 200.0f;

	private static ulong buffer;
	private static long bufferWords;
	private static uint[] scratch = Array.Empty<uint>();
	private static readonly float[] depths = new float[MostLights];
	private static readonly int[] order = new int[MostLights];
	private static ulong cullPipeline;
	private static bool cullTried;
	private static readonly ulong[] bound = new ulong[1];
	private static readonly byte[] push = new byte[128];

	//How many lights the last frame put on the card.
	internal static int Count { get; private set; }

	//And how many tiles the grid had, or nought where it built none.
	internal static int Tiles { get; private set; }

	//Non-directional light count as uploaded; 0 selects pipelines without the lamp branch (kLampsOn)
	internal static int Lamps { get; private set; }

	//How many decals went up with the lights - see kDecalsOn.
	internal static int Decals { get; private set; }

	private static bool EnsurePipeline()
	{
		if ( cullPipeline != 0 ) return true;
		if ( cullTried ) return false;

		cullTried = true;

		if ( !CoreNative.Available || CoreRenderDevice.PortableFrame ) return false;

		var spirv = CoreShaderCache.Pass( EngineShaders.LightCull, "main", CoreShaderCompiler.Stage.Compute );
		cullPipeline = spirv is null ? 0 : CoreRenderDevice.CreateCompute( spirv );

		if ( cullPipeline == 0 ) Log.Warning( $"the light cull did not compile: {CoreRenderDevice.ShaderError}" );

		return cullPipeline != 0;
	}

	//Sends the frame's lights and builds the grid over them, in the frame after the block is set up, before shading.
	//grid: whether to build one - a bake has no screen and a flat view no depth to bin; both read every light
	internal static bool Frame( ReadOnlySpan<float> lights, int count, (int Width, int Height) pixels,
		in System.Numerics.Matrix4x4 worldToView, float projX, float projY, float near, float far, bool grid )
	{
		Count = 0;
		Tiles = 0;
		Lamps = 0;
		Decals = 0;

		count = Math.Max( 0, Math.Min( count, Math.Min( MostLights, lights.Length / LightFloats ) ) );

		//The portable frame reads its sixteen from the block, but its pipelines still take kLampsOn and kManyLights
		if ( CoreRenderDevice.PortableFrame )
		{
			Count = Math.Min( count, CoreSceneRenderer.MaxLights );
			for ( var i = 0; i < Count; i++ )
				if ( lights[( i * LightFloats ) + 7] >= 0.5f ) Lamps++;

			return false;
		}

		if ( !CoreNative.Available ) return false;

		grid = grid && count > GridAbove && pixels.Width > 0 && pixels.Height > 0 &&
			MathF.Abs( projX ) > 0.0001f && MathF.Abs( projY ) > 0.0001f &&
			far > near && near > 0.0f && EnsurePipeline();

		//The suns first in their own order, then the lamps nearest the eye first: the kind is the w of the second float4
		var suns = 0;
		for ( var i = 0; i < count; i++ )
		{
			if ( lights[( i * LightFloats ) + 7] < 0.5f ) order[suns++] = i;
		}

		Lamps = count - suns;

		var lamp = suns;
		for ( var i = 0; i < count; i++ )
		{
			var at = i * LightFloats;
			if ( lights[at + 7] < 0.5f ) continue;

			depths[lamp] = -( ( lights[at] * worldToView.M13 ) + ( lights[at + 1] * worldToView.M23 ) +
				( lights[at + 2] * worldToView.M33 ) + worldToView.M43 );
			order[lamp++] = i;
		}

		if ( grid ) Array.Sort( depths, order, suns, Lamps );

		var tilesX = grid ? ( pixels.Width + TileSize - 1 ) / TileSize : 0;
		var tilesY = grid ? ( pixels.Height + TileSize - 1 ) / TileSize : 0;
		var maskWords = grid ? ( count + 31 ) / 32 : 0;
		var bins = grid ? Bins : 0;

		//The masks and bins after the lights, the decals after them; the cull writes what is between
		var lightWords = HeaderFloats + ( count * LightFloats );
		var masksAt = lightWords;
		var binsAt = masksAt + ( tilesX * tilesY * maskWords );
		var decalCount = Math.Min( CoreDecals.Count, CoreDecals.MostDecals );
		var decalWords = binsAt + bins;
		var words = decalWords + ( decalCount * CoreDecals.DecalFloats );

		if ( buffer == 0 || bufferWords < words )
		{
			if ( buffer != 0 ) CoreRenderDevice.DestroyBuffer( buffer );

			bufferWords = Math.Max( words, bufferWords * 2 );
			buffer = CoreRenderDevice.ReserveBuffer( bufferWords * 4 );

			if ( buffer == 0 ) { bufferWords = 0; return false; }

			//A new buffer is a new descriptor: every set 1 already written names the one that went.
			CoreRenderDevice.SetLightBuffer( buffer );
		}

		if ( scratch.Length < lightWords ) scratch = new uint[lightWords];

		Array.Clear( scratch, 0, HeaderFloats );

		var gridNear = MathF.Max( near, 0.05f );
		var gridFar = MathF.Min( MathF.Max( far, gridNear * 2.0f ), Deep );
		var scale = grid ? Bins / MathF.Log( gridFar / gridNear ) : 0.0f;

		scratch[0] = (uint)count;
		scratch[1] = (uint)tilesX;
		scratch[2] = (uint)tilesY;
		scratch[3] = (uint)bins;
		scratch[4] = (uint)suns;
		scratch[5] = (uint)masksAt;
		scratch[6] = BitConverter.SingleToUInt32Bits( gridNear );
		scratch[7] = BitConverter.SingleToUInt32Bits( scale );
		scratch[8] = (uint)decalCount;
		scratch[9] = (uint)decalWords;
		scratch[11] = (uint)binsAt;
		scratch[12] = (uint)maskWords;

		var source = MemoryMarshal.Cast<float, uint>( lights );
		for ( var i = 0; i < count; i++ )
			source.Slice( order[i] * LightFloats, LightFloats ).CopyTo( scratch.AsSpan( HeaderFloats + ( i * LightFloats ) ) );

		if ( !CoreRenderDevice.FrameWrite( buffer, 0, MemoryMarshal.AsBytes( scratch.AsSpan( 0, lightWords ) ) ) )
			return false;

		if ( decalCount > 0 && !CoreRenderDevice.FrameWrite( buffer, decalWords * 4L,
			MemoryMarshal.AsBytes( CoreDecals.Block.AsSpan( 0, decalCount * CoreDecals.DecalFloats ) ) ) )
		{
			return false;
		}

		Count = count;
		Decals = decalCount;

		if ( !grid ) return true;

		Array.Clear( push );
		var floats = MemoryMarshal.Cast<byte, float>( push.AsSpan() );

		var m = worldToView;
		floats[0] = m.M11; floats[1] = m.M12; floats[2] = m.M13; floats[3] = m.M14;
		floats[4] = m.M21; floats[5] = m.M22; floats[6] = m.M23; floats[7] = m.M24;
		floats[8] = m.M31; floats[9] = m.M32; floats[10] = m.M33; floats[11] = m.M34;
		floats[12] = m.M41; floats[13] = m.M42; floats[14] = m.M43; floats[15] = m.M44;

		//Signed: the projection turns the picture over on its y, and the tile's planes have to be turned over with it.
		floats[16] = 1.0f / projX;
		floats[17] = 1.0f / projY;
		floats[18] = gridNear;
		floats[19] = scale;

		floats[20] = tilesX;
		floats[21] = tilesY;
		floats[22] = bins;
		floats[23] = count;

		floats[24] = suns;
		floats[25] = masksAt;
		floats[26] = binsAt;
		floats[27] = maskWords;

		bound[0] = buffer;

		//A group a tile, then the bins 64 to a group
		CoreRenderDevice.ZoneBegin( "light cull" );
		var ok = CoreRenderDevice.Dispatch( cullPipeline, bound, ( tilesX * tilesY ) + ( ( bins + 63 ) / 64 ), 1, 1, push );
		CoreRenderDevice.ZoneEnd();

		if ( ok ) Tiles = tilesX * tilesY;
		else Log.Warning( "the light cull would not dispatch" );

		return ok;
	}

	//Forgets the buffer, which is what a device going takes with it.
	internal static void Drop()
	{
		if ( buffer != 0 ) CoreRenderDevice.DestroyBuffer( buffer );
		if ( cullPipeline != 0 ) CoreRenderDevice.DestroyPipeline( cullPipeline );

		buffer = 0;
		bufferWords = 0;
		cullPipeline = 0;
		cullTried = false;
		Count = 0;
		Tiles = 0;
		Decals = 0;
		CoreRenderDevice.SetLightBuffer( 0 );
	}
}
