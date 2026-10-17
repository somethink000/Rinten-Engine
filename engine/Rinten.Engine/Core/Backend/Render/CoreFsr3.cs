using System.Runtime.InteropServices;

namespace Rinten;

//FSR 3.1 upscaler host side as engine compute passes, mirroring the SDK dispatch; see fsr3.slang
internal sealed class CoreFsr3
{
	//-- whether it can run -------------------------------------------------------

	//Whether this device runs it: the full frame, every pass built, and pictures in the formats the passes declare.
	internal static bool Available => CoreNative.Available && !CoreRenderDevice.PortableFrame && EnsurePipelines() && formatsRight;

	internal static string WhyNot => !CoreNative.Available || CoreRenderDevice.PortableFrame
		? "the portable frame has no compute"
		: pipelinesFailed ? $"its passes did not build: {buildError}"
		: !formatsRight ? "this device cannot store into the formats its passes need"
		: "";

	//-- the passes ---------------------------------------------------------------

	private enum Pass
	{
		PrepareInputs,
		LumaPyramid,
		ShadingChangePyramid,
		ShadingChange,
		PrepareReactivity,
		LumaInstability,
		Accumulate,
		ClearWhole,
		ClearSingle,
		ClearPair,
		ClearFull,
		ClearHdr,
	}

	//Each pass's zone in the frame's report, by Pass
	private static readonly string[] passZones =
	[
		"fsr3 prepare inputs", "fsr3 luma pyramid", "fsr3 shading change pyramid", "fsr3 shading change",
		"fsr3 prepare reactivity", "fsr3 luma instability", "fsr3 accumulate", "fsr3 clear", "fsr3 clear", "fsr3 clear", "fsr3 clear", "fsr3 clear",
	];

	private static readonly string[] passDefines =
	[
		"#define FSR3_PREPARE_INPUTS 1\n",
		"#define FSR3_LUMA_PYRAMID 1\n",
		"#define FSR3_SHADING_CHANGE_PYRAMID 1\n",
		"#define FSR3_SHADING_CHANGE 1\n",
		"#define FSR3_PREPARE_REACTIVITY 1\n",
		"#define FSR3_LUMA_INSTABILITY 1\n",
		"#define FSR3_ACCUMULATE 1\n",
		"#define FSR3_CLEAR 1\n",
		"#define FSR3_CLEAR 2\n",
		"#define FSR3_CLEAR 3\n",
		"#define FSR3_CLEAR 4\n",
		"#define FSR3_CLEAR 5\n",
	];

	//Every build of engine/fsr3.slang, for the bake - see EngineShaders.All.
	internal static IEnumerable<string> Defines => passDefines;

	private static readonly ulong[] pipelines = new ulong[passDefines.Length];
	private static int pipelinesBuilt = -1;
	private static bool pipelinesFailed;
	private static bool formatsRight = true;
	private static string buildError = "";

	private static bool EnsurePipelines()
	{
		if ( pipelinesBuilt == CoreShaderCache.Revision ) return !pipelinesFailed;

		DestroyPipelines();

		pipelinesBuilt = CoreShaderCache.Revision;
		pipelinesFailed = false;

		for ( var i = 0; i < passDefines.Length; i++ )
		{
			var code = CoreShaderCache.Pass( EngineShaders.Fsr3, "main", CoreShaderCompiler.Stage.Compute, passDefines[i] );
			pipelines[i] = code is null ? 0 : CoreRenderDevice.CreatePassPipeline( code );

			if ( pipelines[i] != 0 ) continue;

			pipelinesFailed = true;
			buildError = $"{(Pass)i}: {CoreRenderDevice.ShaderError}";
			Log.Warning( $"FSR 3's {(Pass)i} pass did not build: {CoreRenderDevice.ShaderError}" );
			break;
		}

		return !pipelinesFailed;
	}

	internal static void DestroyPipelines()
	{
		for ( var i = 0; i < pipelines.Length; i++ )
		{
			if ( pipelines[i] != 0 ) CoreRenderDevice.DestroyPipeline( pipelines[i] );
			pipelines[i] = 0;
		}

		pipelinesBuilt = -1;
	}

	//-- what one context keeps ---------------------------------------------------

	//The pictures AMD's context creates, by the names it gives them.
	private ulong dilatedDepth;
	private ulong dilatedMotion;
	private ulong reconstructedDepth;
	private ulong intermediate;
	private ulong spdMips;
	private ulong farthestMip1;
	private ulong shadingChange;
	private ulong dilatedReactive;
	private ulong newLocks;
	private ulong spdAtomic;
	private ulong frameInfo;
	private readonly ulong[] luma = new ulong[2];
	private readonly ulong[] accumulation = new ulong[2];
	private readonly ulong[] upscaled = new ulong[2];
	private readonly ulong[] lumaHistory = new ulong[2];

	//The constants, a buffer the passes read at the binding after their pictures.
	private ulong constants;

	//How many levels of the SPD chain the passes store into - its six UAVs.
	private int spdLevels;

	private (int Width, int Height) madeRender;
	private (int Width, int Height) madeDisplay;

	private bool firstExecution = true;
	private int resourceFrameIndex;
	private float frameIndex;
	private float jitterPhaseCount;
	private Vector2 previousJitter;
	private (int Width, int Height) previousRender;
	private (int Width, int Height) previousDisplay;

	internal void Release()
	{
		foreach ( var one in new[] { dilatedDepth, dilatedMotion, reconstructedDepth, intermediate, spdMips, farthestMip1,
			shadingChange, dilatedReactive, newLocks, spdAtomic, frameInfo } )
			CoreRenderDevice.ReleasePicture( one );

		foreach ( var pair in new[] { luma, accumulation, upscaled, lumaHistory } )
		{
			for ( var i = 0; i < pair.Length; i++ )
			{
				CoreRenderDevice.ReleasePicture( pair[i] );
				pair[i] = 0;
			}
		}

		dilatedDepth = dilatedMotion = reconstructedDepth = intermediate = spdMips = farthestMip1 = 0;
		shadingChange = dilatedReactive = newLocks = spdAtomic = frameInfo = 0;

		if ( constants != 0 ) CoreRenderDevice.DestroyBuffer( constants );
		constants = 0;

		madeRender = default;
		madeDisplay = default;
		firstExecution = true;
	}

	//Every picture of the context, made for this render and display size - what fsr3upscalerCreate makes, at the sizes it is given as the most.
	private bool Make( (int Width, int Height) render, (int Width, int Height) display )
	{
		if ( madeRender == render && madeDisplay == display && constants != 0 ) return true;

		Release();

		var (rw, rh) = render;
		var (dw, dh) = display;
		var (hw, hh) = (Math.Max( rw / 2, 1 ), Math.Max( rh / 2, 1 ));

		ulong Make( CoreRender.PictureKind kind, int width, int height, int levels = 1 )
			=> CoreRenderDevice.AcquirePicture( kind, width, height, levels, storage: true );

		dilatedDepth = Make( CoreRender.PictureKind.SingleFull, rw, rh );
		dilatedMotion = Make( CoreRender.PictureKind.Pair, rw, rh );
		reconstructedDepth = Make( CoreRender.PictureKind.Whole, rw, rh );
		intermediate = Make( CoreRender.PictureKind.Single, rw, rh );
		farthestMip1 = Make( CoreRender.PictureKind.Single, hw, hh );
		shadingChange = Make( CoreRender.PictureKind.Single, hw, hh );
		dilatedReactive = Make( CoreRender.PictureKind.Colour, rw, rh );
		newLocks = Make( CoreRender.PictureKind.Single, dw, dh );
		spdAtomic = Make( CoreRender.PictureKind.Whole, 1, 1 );
		frameInfo = Make( CoreRender.PictureKind.Full, 1, 1 );

		//The chain SPD folds the luma into: six levels, what its passes can store, or as many as a small world has.
		spdMips = Make( CoreRender.PictureKind.Pair, hw, hh, 6 );
		spdLevels = CoreRenderDevice.PictureShape( spdMips, out _, out _, out var levels, out _ ) ? Math.Min( levels, 6 ) : 0;

		for ( var i = 0; i < 2; i++ )
		{
			luma[i] = Make( CoreRender.PictureKind.Single, rw, rh );
			accumulation[i] = Make( CoreRender.PictureKind.Single, rw, rh );
			upscaled[i] = Make( CoreRender.PictureKind.Hdr, dw, dh );
			lumaHistory[i] = Make( CoreRender.PictureKind.Hdr, rw, rh );
		}

		constants = CoreRenderDevice.ReserveBuffer( ConstantLines * 16 );

		madeRender = render;
		madeDisplay = display;

		//Everything there, and in the format its pass declares it in: a picture the device made in half floats instead -
		//it could not store into the smaller format - would be written as garbage, and FSR is then not for this device.
		formatsRight = Is( dilatedDepth, R32Sfloat ) && Is( dilatedMotion, R16G16Sfloat ) && Is( reconstructedDepth, R32Uint ) &&
			Is( intermediate, R16Sfloat ) && Is( farthestMip1, R16Sfloat ) && Is( shadingChange, R16Sfloat ) &&
			Is( dilatedReactive, R8G8B8A8Unorm ) && Is( newLocks, R16Sfloat ) && Is( spdAtomic, R32Uint ) &&
			Is( frameInfo, R32G32B32A32Sfloat ) && Is( spdMips, R16G16Sfloat ) && spdLevels > 0 &&
			Is( luma[0], R16Sfloat ) && Is( luma[1], R16Sfloat ) && Is( accumulation[0], R16Sfloat ) &&
			Is( accumulation[1], R16Sfloat ) && Is( upscaled[0], R16G16B16A16Sfloat ) && Is( upscaled[1], R16G16B16A16Sfloat ) &&
			Is( lumaHistory[0], R16G16B16A16Sfloat ) && Is( lumaHistory[1], R16G16B16A16Sfloat ) && constants != 0;

		if ( !formatsRight )
		{
			Log.Warning( "FSR 3 could not have its pictures in the formats its passes store - the engine's own temporal upscaler instead" );
			Release();
			return false;
		}

		return true;
	}

	//The Vulkan formats the passes declare - see the callbacks.
	private const int R16Sfloat = 76;
	private const int R16G16Sfloat = 83;
	private const int R32Sfloat = 100;
	private const int R32Uint = 98;
	private const int R8G8B8A8Unorm = 37;
	private const int R32G32B32A32Sfloat = 109;
	private const int R16G16B16A16Sfloat = 97;

	private static bool Is( ulong picture, int format )
		=> picture != 0 && CoreRenderDevice.PictureShape( picture, out _, out _, out _, out var made ) && made == format;

	//-- the frame ----------------------------------------------------------------

	//One frame of it: the world's picture, depth and motion in, the window's picture out. False where it could not, and the caller shows something else.
	internal bool Run( ulong colour, ulong depth, ulong motion, ulong reactive, ulong output,
		(int Width, int Height) render, (int Width, int Height) display, Vector2 jitter,
		float near, float far, float field, float delta, bool reset, float exposure )
	{
		if ( !Available || output == 0 ) return false;
		if ( !Make( render, display ) ) return false;

		var restart = reset || firstExecution;

		if ( firstExecution )
		{
			Clear( accumulation[0], Pass.ClearSingle, default );
			Clear( accumulation[1], Pass.ClearSingle, default );
			Clear( luma[0], Pass.ClearSingle, default );
			Clear( luma[1], Pass.ClearSingle, default );
			Clear( lumaHistory[0], Pass.ClearHdr, default );
			Clear( lumaHistory[1], Pass.ClearHdr, default );
			Clear( upscaled[0], Pass.ClearHdr, default );
			Clear( upscaled[1], Pass.ClearHdr, default );
			Clear( newLocks, Pass.ClearSingle, default );
		}

		firstExecution = false;

		var odd = ( resourceFrameIndex & 1 ) != 0;

		var accumulationRead = accumulation[odd ? 1 : 0];
		var accumulationWrite = accumulation[odd ? 0 : 1];
		var upscaledRead = upscaled[odd ? 1 : 0];
		var upscaledWrite = upscaled[odd ? 0 : 1];
		var historyRead = lumaHistory[odd ? 1 : 0];
		var historyWrite = lumaHistory[odd ? 0 : 1];
		var currentLuma = luma[odd ? 1 : 0];
		var previousLuma = luma[odd ? 0 : 1];

		//-- the constants, as fsr3upscalerDispatch works them out --

		Span<float> c = stackalloc float[ConstantLines * 4];
		c.Clear();

		var phases = CoreUpscale.PhaseCount( render.Width, display.Width );

		if ( restart || jitterPhaseCount == 0 ) jitterPhaseCount = phases;
		else if ( phases > jitterPhaseCount ) jitterPhaseCount++;
		else if ( phases < jitterPhaseCount ) jitterPhaseCount--;

		frameIndex = restart ? 0.0f : frameIndex + 1.0f;

		var aspect = render.Width / (float)render.Height;
		var horizontal = 2.0f * MathF.Atan( MathF.Tan( field * 0.5f ) * aspect );
		var cotHalf = MathF.Cos( 0.5f * field ) / MathF.Sin( 0.5f * field );

		//Device depth to view depth, non-inverted and finite: near at nought.
		var fQ = far / ( near - far );

		SetLine( c, 0, render.Width, render.Height, previousRender.Width, previousRender.Height );
		SetLine( c, 1, display.Width, display.Height, previousDisplay.Width, previousDisplay.Height );
		SetLine( c, 2, render.Width, render.Height, display.Width, display.Height );
		SetLine( c, 3, -fQ, fQ * near, 1.0f / ( cotHalf / aspect ), 1.0f / cotHalf );
		SetLine( c, 4, jitter.x, jitter.y, previousJitter.x, previousJitter.y );

		//Already uv, so a scale of one; and the downscale is render over display.
		SetLine( c, 5, 1.0f, 1.0f, render.Width / (float)display.Width, render.Height / (float)display.Height );
		SetLine( c, 6, 0.0f, 0.0f, MathF.Tan( horizontal * 0.5f ), jitterPhaseCount );
		SetLine( c, 7, Math.Clamp( delta, 0.0f, 1.0f ), 1.0f, 1.0f, frameIndex );
		SetLine( c, 8, 1.0f, 1.0f, 1.0f, 1.0f / 3.0f );
		SetLine( c, 9, -1.0f / 3.0f, exposure, 0.0f, 0.0f );

		//SPD over the whole world - ffxSpdSetup.
		var groupsX = ( ( render.Width - 1 ) / 64 ) + 1;
		var groupsY = ( ( render.Height - 1 ) / 64 ) + 1;
		var mips = Math.Min( MathF.Floor( MathF.Log2( Math.Max( render.Width, render.Height ) ) ), 12.0f );

		SetLine( c, 10, mips, groupsX * groupsY, 0.0f, 0.0f );
		SetLine( c, 11, render.Width, render.Height, 0.0f, 0.0f );

		if ( !CoreRenderDevice.FrameWrite( constants, 0, MemoryMarshal.AsBytes( c ) ) ) return false;

		previousJitter = jitter;
		previousRender = render;
		previousDisplay = display;
		resourceFrameIndex++;

		//-- the clears --

		if ( restart )
		{
			Clear( accumulationRead, Pass.ClearSingle, default );
			Clear( frameInfo, Pass.ClearFull, new Vector4( -1.0f, 1.0f, 0.0f, 0.0f ) );
		}

		//The nearest depth each texel was last frame, kept by an atomic minimum from the far plane down; and SPD's counter and chain, every frame.
		Clear( reconstructedDepth, Pass.ClearWhole, new Vector4( 1.0f, 0.0f, 0.0f, 0.0f ) );
		Clear( spdAtomic, Pass.ClearWhole, default );

		for ( var level = 0; level < spdLevels; level++ )
			Clear( spdMips, Pass.ClearPair, default, level );

		//-- the passes --

		var srcGroupsX = ( render.Width + 7 ) / 8;
		var srcGroupsY = ( render.Height + 7 ) / 8;
		var dstGroupsX = ( display.Width + 7 ) / 8;
		var dstGroupsY = ( display.Height + 7 ) / 8;

		//The two SPD passes store into the counter, the chain's six levels, and - the luma pyramid's - the frame's numbers and the farthest depth's half.
		lumaPyramidOut[0] = spdAtomic;
		lumaPyramidOut[1] = frameInfo;
		lumaPyramidOut[8] = farthestMip1;
		shadingPyramidOut[0] = spdAtomic;

		for ( var i = 0; i < 6; i++ )
		{
			var level = Math.Min( i, Math.Max( spdLevels - 1, 0 ) );

			lumaPyramidOut[2 + i] = spdMips;
			lumaPyramidLevels[2 + i] = level;
			shadingPyramidOut[1 + i] = spdMips;
			shadingPyramidLevels[1 + i] = level;
		}

		var ok =
			Dispatch( Pass.PrepareInputs, [motion, depth, colour],
				[dilatedMotion, dilatedDepth, reconstructedDepth, intermediate, currentLuma], null,
				srcGroupsX, srcGroupsY ) &&

			Dispatch( Pass.LumaPyramid, [Texture( currentLuma ), Texture( intermediate )],
				lumaPyramidOut, lumaPyramidLevels,
				groupsX, groupsY ) &&

			Dispatch( Pass.ShadingChangePyramid, [Texture( currentLuma ), Texture( previousLuma ), Texture( dilatedMotion ), 0],
				shadingPyramidOut, shadingPyramidLevels,
				groupsX, groupsY ) &&

			Dispatch( Pass.ShadingChange, [Texture( spdMips )],
				[shadingChange], null,
				( ( render.Width / 2 ) + 7 ) / 8, ( ( render.Height / 2 ) + 7 ) / 8 ) &&

			Dispatch( Pass.PrepareReactivity,
				[0, Texture( dilatedMotion ), Texture( dilatedDepth ), reactive, 0, Texture( accumulationRead ),
					Texture( shadingChange ), Texture( currentLuma ), 0],
				[dilatedReactive, newLocks, accumulationWrite, reconstructedDepth], null,
				srcGroupsX, srcGroupsY ) &&

			Dispatch( Pass.LumaInstability,
				[0, Texture( dilatedReactive ), Texture( dilatedMotion ), Texture( frameInfo ), Texture( historyRead ),
					Texture( farthestMip1 ), Texture( currentLuma )],
				[historyWrite, intermediate], null,
				srcGroupsX, srcGroupsY );

		//Its RCAS is the compose's - see CoreUpscaler.ComposeSharpening
		return ok && Dispatch( Pass.Accumulate,
			[0, Texture( dilatedReactive ), Texture( dilatedMotion ), Texture( upscaledRead ), 0,
				Texture( farthestMip1 ), Texture( currentLuma ), Texture( intermediate ), colour],
			[upscaledWrite, output, newLocks], null,
			dstGroupsX, dstGroupsY );
	}

	//The lines of float4s the constants are - see the callbacks' list.
	private const int ConstantLines = 13;

	private static void SetLine( Span<float> into, int line, float x, float y, float z, float w )
	{
		into[( line * 4 ) + 0] = x;
		into[( line * 4 ) + 1] = y;
		into[( line * 4 ) + 2] = z;
		into[( line * 4 ) + 3] = w;
	}

	private static ulong Texture( ulong picture ) => picture == 0 ? 0 : CoreRenderDevice.PictureTexture( picture );

	//What the two SPD passes store into, and at which level - the chain's six in turn,
	//the last one again where a small world has fewer. Kept rather than made a frame.
	private readonly ulong[] lumaPyramidOut = new ulong[9];
	private readonly int[] lumaPyramidLevels = new int[9];
	private readonly ulong[] shadingPyramidOut = new ulong[7];
	private readonly int[] shadingPyramidLevels = new int[7];

	//One pass: its inputs by texture at the slots its SRV numbers name, its outputs in its UAV order, and the constants after them.
	private bool Dispatch( Pass pass, ReadOnlySpan<ulong> reads, ReadOnlySpan<ulong> writes, int[] levels, int groupsX, int groupsY )
	{
		var pipeline = pipelines[(int)pass];
		if ( pipeline == 0 ) return false;

		Span<byte> push = stackalloc byte[128];
		push.Clear();

		var slots = MemoryMarshal.Cast<byte, uint>( push );

		for ( var i = 0; i < reads.Length && i < 16; i++ )
		{
			if ( reads[i] == 0 ) continue;

			CoreRenderDevice.ReadyToRead( reads[i] );
			slots[i] = (uint)CoreSceneDrawer.Indexed( reads[i] );
		}

		Span<int> at = stackalloc int[writes.Length];

		for ( var i = 0; i < writes.Length; i++ )
			at[i] = levels is null ? 0 : levels[i];

		Span<ulong> buffers = stackalloc ulong[1] { constants };

		CoreRenderDevice.ZoneBegin( passZones[(int)pass] );
		var ran = CoreRenderDevice.DispatchPass( pipeline, CoreSceneDrawer.WhiteTexture, writes, at, buffers, push, groupsX, groupsY );
		CoreRenderDevice.ZoneEnd();

		return ran;
	}

	//One level of a picture set to a value, by the clear build for its format.
	private void Clear( ulong picture, Pass build, Vector4 value, int level = 0 )
	{
		if ( picture == 0 || pipelines[(int)build] == 0 ) return;
		if ( !CoreRenderDevice.PictureShape( picture, out var width, out var height, out _, out _ ) ) return;

		width = Math.Max( width >> level, 1 );
		height = Math.Max( height >> level, 1 );

		Span<byte> push = stackalloc byte[128];
		push.Clear();

		var floats = MemoryMarshal.Cast<byte, float>( push );
		floats[0] = value.x;
		floats[1] = value.y;
		floats[2] = value.z;
		floats[3] = value.w;

		var words = MemoryMarshal.Cast<byte, uint>( push );
		words[4] = (uint)width;
		words[5] = (uint)height;

		Span<ulong> outputs = stackalloc ulong[1] { picture };
		Span<int> levels = stackalloc int[1] { level };

		CoreRenderDevice.DispatchPass( pipelines[(int)build], CoreSceneDrawer.WhiteTexture, outputs, levels,
			ReadOnlySpan<ulong>.Empty, push, ( width + 7 ) / 8, ( height + 7 ) / 8 );
	}
}
