using System;
using System.Runtime.InteropServices;

namespace Rinten;

//Frame lights plus a clustered tile/slice grid of light lists, then decals, in one buffer (set 1 b7)
internal static class CoreLights
{
	//How many lights one frame may carry.
	internal const int MostLights = 128;

	//Floats a light takes: five float4s, as frame.slang has it; the fifth holds only the falloff's shape, as the shader
	//reads float4s - two kilobytes a frame across 128 lights
	internal const int LightFloats = 20;

	//What the buffer says about itself before the lights.
	internal const int HeaderFloats = 16;

	//Grid cell size in pixels; 64 would overflow PerCell, so 32
	internal const int TileSize = 32;

	//Depth slices of the light grid; fewer makes the cull cheaper with no visible change
	internal const int Slices = 16;

	//Max lights per cell, and the light count above which a grid is built at all
	internal const int PerCell = 16;

	//Grid depth, the distance lamps are dropped past; the last slice runs to infinity
	internal const float Deep = 200.0f;

	private static ulong buffer;
	private static long bufferWords;
	private static uint[] scratch = Array.Empty<uint>();
	private static ulong cullPipeline;
	private static bool cullTried;
	private static readonly ulong[] bound = new ulong[1];
	private static readonly byte[] push = new byte[128];

	//How many lights the last frame put on the card.
	internal static int Count { get; private set; }

	//And how many cells the grid had, or nought where it built none.
	internal static int Cells { get; private set; }

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
	//grid: whether to build cells - a bake has no screen and a flat view no depth to slice; both read every light
	internal static bool Frame( ReadOnlySpan<float> lights, int count, (int Width, int Height) pixels,
		in System.Numerics.Matrix4x4 worldToView, float projX, float projY, float near, float far, bool grid )
	{
		Count = 0;
		Cells = 0;
		Lamps = 0;
		Decals = 0;

		count = Math.Max( 0, Math.Min( count, lights.Length / LightFloats ) );

		//The portable frame reads its sixteen from the block, but its pipelines still take kLampsOn and kManyLights
		if ( CoreRenderDevice.PortableFrame )
		{
			Count = Math.Min( count, CoreSceneRenderer.MaxLights );
			for ( var i = 0; i < Count; i++ )
				if ( lights[( i * LightFloats ) + 7] >= 0.5f ) Lamps++;

			return false;
		}

		if ( !CoreNative.Available ) return false;

		//No grid where a cell could not narrow anything: with no more lights than a cell holds, every cell would name
		//all of them and the cull would be paid for nothing. Which is every scene that has a sun and a lamp or two.
		grid = grid && count > PerCell && pixels.Width > 0 && pixels.Height > 0 &&
			MathF.Abs( projX ) > 0.0001f && MathF.Abs( projY ) > 0.0001f &&
			far > near && near > 0.0f && EnsurePipeline();

		var cellsX = grid ? ( pixels.Width + TileSize - 1 ) / TileSize : 0;
		var cellsY = grid ? ( pixels.Height + TileSize - 1 ) / TileSize : 0;
		var cellsZ = grid ? Slices : 0;
		var cells = cellsX * cellsY * cellsZ;

		var lightWords = HeaderFloats + ( MostLights * LightFloats );

		//The decals go after the cells, and the cells after the lights: the cull writes its lists
		//at lightWords and the decals must not be under them. See CoreDecals for what one is.
		var decalCount = Math.Min( CoreDecals.Count, CoreDecals.MostDecals );
		var decalWords = lightWords + ( cells * ( PerCell + 1 ) );
		var words = decalWords + ( decalCount * CoreDecals.DecalFloats );

		if ( buffer == 0 || bufferWords < words )
		{
			if ( buffer != 0 ) CoreRenderDevice.DestroyBuffer( buffer );

			bufferWords = Math.Max( words, Math.Max( bufferWords * 2, lightWords ) );
			buffer = CoreRenderDevice.ReserveBuffer( bufferWords * 4 );

			if ( buffer == 0 ) { bufferWords = 0; return false; }

			//A new buffer is a new descriptor: every set 1 already written names the one that went.
			CoreRenderDevice.SetLightBuffer( buffer );
		}

		if ( scratch.Length < lightWords ) scratch = new uint[lightWords];

		//Upload only this frame's lights, not the whole buffer; nothing reads past the count
		var written = HeaderFloats + ( count * LightFloats );

		Array.Clear( scratch, 0, written );

		//Near enough to matter, and no further: see Deep.
		var gridNear = MathF.Max( near, 0.05f );
		var gridFar = MathF.Min( MathF.Max( far, gridNear * 2.0f ), Deep );
		var scale = grid ? Slices / MathF.Log( gridFar / gridNear ) : 0.0f;

		scratch[0] = (uint)count;
		scratch[1] = (uint)cellsX;
		scratch[2] = (uint)cellsY;
		scratch[3] = (uint)cellsZ;
		scratch[4] = PerCell;
		scratch[5] = (uint)lightWords;
		scratch[6] = BitConverter.SingleToUInt32Bits( gridNear );
		scratch[7] = BitConverter.SingleToUInt32Bits( scale );
		scratch[8] = (uint)decalCount;
		scratch[9] = (uint)decalWords;

		//Tile size goes in the header so the shader never hardcodes its own copy
		scratch[10] = (uint)TileSize;

		MemoryMarshal.Cast<float, uint>( lights[..( count * LightFloats )] )
			.CopyTo( scratch.AsSpan( HeaderFloats ) );

		if ( !CoreRenderDevice.FrameWrite( buffer, 0, MemoryMarshal.AsBytes( scratch.AsSpan( 0, written ) ) ) )
			return false;

		if ( decalCount > 0 && !CoreRenderDevice.FrameWrite( buffer, decalWords * 4L,
			MemoryMarshal.AsBytes( CoreDecals.Block.AsSpan( 0, decalCount * CoreDecals.DecalFloats ) ) ) )
		{
			return false;
		}

		Count = count;
		Decals = decalCount;

		//The kind is the w of the second float4: nought the sun, anything else a lamp - frame.slang's test
		for ( var i = 0; i < count; i++ )
			if ( lights[( i * LightFloats ) + 7] >= 0.5f ) Lamps++;

		if ( !grid ) return true;

		Array.Clear( push );
		var floats = MemoryMarshal.Cast<byte, float>( push.AsSpan() );

		var m = worldToView;
		floats[0] = m.M11; floats[1] = m.M12; floats[2] = m.M13; floats[3] = m.M14;
		floats[4] = m.M21; floats[5] = m.M22; floats[6] = m.M23; floats[7] = m.M24;
		floats[8] = m.M31; floats[9] = m.M32; floats[10] = m.M33; floats[11] = m.M34;
		floats[12] = m.M41; floats[13] = m.M42; floats[14] = m.M43; floats[15] = m.M44;

		//Signed: the projection turns the picture over on its y, and the cell's own box has to be turned over with it.
		floats[16] = 1.0f / projX;
		floats[17] = 1.0f / projY;
		floats[18] = gridNear;
		floats[19] = scale;

		floats[20] = cellsX;
		floats[21] = cellsY;
		floats[22] = cellsZ;
		floats[23] = count;

		floats[24] = PerCell;
		floats[25] = lightWords;

		bound[0] = buffer;

		CoreRenderDevice.ZoneBegin( "light cull" );
		var ok = CoreRenderDevice.Dispatch( cullPipeline, bound, cellsX * cellsY, 1, 1, push );
		CoreRenderDevice.ZoneEnd();

		if ( ok ) Cells = cells;


		if ( !ok ) Log.Warning( "the light cull would not dispatch" );

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
		Cells = 0;
		Decals = 0;
		CoreRenderDevice.SetLightBuffer( 0 );
	}
}
