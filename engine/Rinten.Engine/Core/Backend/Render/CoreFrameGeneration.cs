using System.Runtime.InteropServices;

namespace Rinten;

//FSR 3.1 frame generation host side as engine compute passes - AMD's optical flow and frame interpolation dispatches; see
//framegen.slang. The world is kept as it is before the interface is drawn (Capture); at the frame's end the frame between the
//last one and this one is made from the two, and VkFrame shows it half a frame before this one
internal static class CoreFrameGeneration
{
	//-- whether it runs ----------------------------------------------------------

	internal static bool Available => CoreNative.Available && !CoreRenderDevice.PortableFrame && EnsurePipelines() && formatsRight;

	/// <summary>Whether a frame is generated between every two the game draws: the player's setting, where this device runs it.</summary>
	internal static bool Wanted => Rinten.Rendering.DrawSettings.FrameGeneration && Available;

	//-- the passes ---------------------------------------------------------------

	private enum Pass
	{
		PrepareLuma,
		LumaPyramid,
		ScdHistogram,
		ScdDivergence,
		Search,
		Filter,
		Scale,
		ReconstructAndDilate,
		Setup,
		ReconstructPreviousDepth,
		GameMotionVectorField,
		GameVectorFieldPyramid,
		OpticalFlowVectorField,
		DisocclusionMask,
		Interpolate,
		InpaintingPyramid,
		Inpainting,
		ClearWhole,
		ClearSingleFull,
	}

	private static readonly string[] passZones =
	[
		"fg luma", "fg luma pyramid", "fg scene change", "fg scene change", "fg flow search", "fg flow filter", "fg flow scale",
		"fg dilate", "fg setup", "fg previous depth", "fg game motion", "fg game pyramid", "fg flow field", "fg disocclusion",
		"fg interpolate", "fg inpainting pyramid", "fg inpainting", "fg clear", "fg clear",
	];

	private static readonly string[] passDefines =
	[
		"#define FG_PREPARE_LUMA 1\n",
		"#define FG_LUMA_PYRAMID 1\n",
		"#define FG_SCD_HISTOGRAM 1\n",
		"#define FG_SCD_DIVERGENCE 1\n",
		"#define FG_SEARCH 1\n",
		"#define FG_FILTER 1\n",
		"#define FG_SCALE 1\n",
		"#define FG_RECONSTRUCT_AND_DILATE 1\n",
		"#define FG_SETUP 1\n",
		"#define FG_RECONSTRUCT_PREVIOUS_DEPTH 1\n",
		"#define FG_GAME_MOTION_VECTOR_FIELD 1\n",
		"#define FG_GAME_VECTOR_FIELD_PYRAMID 1\n",
		"#define FG_OPTICAL_FLOW_VECTOR_FIELD 1\n",
		"#define FG_DISOCCLUSION_MASK 1\n",
		"#define FG_INTERPOLATE 1\n",
		"#define FG_INPAINTING_PYRAMID 1\n",
		"#define FG_INPAINTING 1\n",
		"#define FG_CLEAR 1\n",
		"#define FG_CLEAR 2\n",
	];

	//Every build of engine/framegen.slang, for the bake - see EngineShaders.All.
	internal static IEnumerable<string> Defines => passDefines;

	private static readonly ulong[] pipelines = new ulong[passDefines.Length];
	private static int pipelinesBuilt = -1;
	private static bool pipelinesFailed;
	private static bool formatsRight = true;

	private static bool EnsurePipelines()
	{
		if ( pipelinesBuilt == CoreShaderCache.Revision ) return !pipelinesFailed;

		DestroyPipelines();

		pipelinesBuilt = CoreShaderCache.Revision;
		pipelinesFailed = false;

		for ( var i = 0; i < passDefines.Length; i++ )
		{
			var code = CoreShaderCache.Pass( EngineShaders.FrameGeneration, "main", CoreShaderCompiler.Stage.Compute, passDefines[i] );
			pipelines[i] = code is null ? 0 : CoreRenderDevice.CreatePassPipeline( code );

			if ( pipelines[i] != 0 ) continue;

			pipelinesFailed = true;
			Log.Warning( $"frame generation's {(Pass)i} pass did not build: {CoreRenderDevice.ShaderError}" );
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

	//-- the pictures -------------------------------------------------------------

	//Levels of the optical flow's pyramid - OpticalFlowMaxPyramidLevels in AMD's host code.
	private const int FlowLevels = 7;

	//Pixels a flow vector stands for, a side.
	private const int FlowBlock = 8;

	//SPD's levels the pyramid passes can store - see the callbacks.
	private const int PyramidLevels = 11;

	//AMD's scene change histograms: 256 bins, three by three of them.
	private const int ScdWidth = 256 * 9;

	private static readonly ulong[] luma = new ulong[2];
	private static readonly ulong[][] flow = [new ulong[FlowLevels], new ulong[FlowLevels]];
	private static ulong flowOut;
	private static ulong scdHistogram, scdPrevious, scdTemp, scdOutput;

	private static ulong dilatedDepth, dilatedMotion, reconstructedPrevious, reconstructedInterpolated;
	private static ulong gameField, flowField, disocclusion, inpaintingMask, pyramid;
	private static ulong output;
	private static readonly ulong[] hudless = new ulong[2];

	/// <summary>The window's picture with its interface, copied in at the frame's end - see VkFrame.Generation.</summary>
	internal static ulong PresentPicture { get; private set; }

	private static ulong constants, counters;
	private static int pyramidHas;

	private static (int Width, int Height) madeRender, madeDisplay;
	private static readonly (int Width, int Height)[] flowSizes = new (int, int)[FlowLevels];

	internal static void Release()
	{
		foreach ( var one in new[] { flowOut, scdHistogram, scdPrevious, scdTemp, scdOutput, dilatedDepth, dilatedMotion,
			reconstructedPrevious, reconstructedInterpolated, gameField, flowField, disocclusion, inpaintingMask, pyramid, output, PresentPicture } )
			CoreRenderDevice.ReleasePicture( one );

		foreach ( var set in new[] { luma, hudless, flow[0], flow[1] } )
		{
			for ( var i = 0; i < set.Length; i++ )
			{
				CoreRenderDevice.ReleasePicture( set[i] );
				set[i] = 0;
			}
		}

		flowOut = scdHistogram = scdPrevious = scdTemp = scdOutput = 0;
		dilatedDepth = dilatedMotion = reconstructedPrevious = reconstructedInterpolated = 0;
		gameField = flowField = disocclusion = inpaintingMask = pyramid = output = 0;
		PresentPicture = 0;

		if ( constants != 0 ) CoreRenderDevice.DestroyBuffer( constants );
		if ( counters != 0 ) CoreRenderDevice.DestroyBuffer( counters );
		constants = counters = 0;

		madeRender = madeDisplay = default;
		first = true;
	}

	//What ffxFrameInterpolationContextCreate and ffxOpticalflowContextCreate make, at these sizes.
	private static bool Make( (int Width, int Height) render, (int Width, int Height) display )
	{
		if ( madeRender == render && madeDisplay == display && constants != 0 ) return true;

		Release();

		var (rw, rh) = render;
		var (dw, dh) = display;

		static ulong Make( CoreRender.PictureKind kind, int width, int height, int levels = 1 )
			=> CoreRenderDevice.AcquirePicture( kind, Math.Max( width, 1 ), Math.Max( height, 1 ), levels, storage: true );

		for ( var i = 0; i < 2; i++ )
		{
			luma[i] = Make( CoreRender.PictureKind.Whole, dw, dh, FlowLevels );
			hudless[i] = Make( CoreRender.PictureKind.Colour, dw, dh );
		}

		flowSizes[0] = (( dw + FlowBlock - 1 ) / FlowBlock, ( dh + FlowBlock - 1 ) / FlowBlock);

		for ( var level = 1; level < FlowLevels; level++ )
			flowSizes[level] = (( flowSizes[level - 1].Width + 1 ) / 2, ( flowSizes[level - 1].Height + 1 ) / 2);

		for ( var level = 0; level < FlowLevels; level++ )
		{
			flow[0][level] = Make( CoreRender.PictureKind.WholePair, flowSizes[level].Width, flowSizes[level].Height );
			flow[1][level] = Make( CoreRender.PictureKind.WholePair, flowSizes[level].Width, flowSizes[level].Height );
		}

		flowOut = Make( CoreRender.PictureKind.WholePair, flowSizes[0].Width, flowSizes[0].Height );
		scdHistogram = Make( CoreRender.PictureKind.Whole, ScdWidth, 1 );
		scdPrevious = Make( CoreRender.PictureKind.SingleFull, ScdWidth, 1 );
		scdTemp = Make( CoreRender.PictureKind.Whole, 3, 1 );
		scdOutput = Make( CoreRender.PictureKind.Whole, 3, 1 );

		dilatedDepth = Make( CoreRender.PictureKind.SingleFull, rw, rh );
		dilatedMotion = Make( CoreRender.PictureKind.Pair, rw, rh );
		reconstructedPrevious = Make( CoreRender.PictureKind.Whole, rw, rh );
		reconstructedInterpolated = Make( CoreRender.PictureKind.Whole, rw, rh );

		//Each field's X and Y side by side - see the callbacks. The flow's field is read at the flow's size, which may pass the world's.
		gameField = Make( CoreRender.PictureKind.Whole, rw * 2, rh );
		flowField = Make( CoreRender.PictureKind.Whole, Math.Max( rw, flowSizes[0].Width ) * 2, Math.Max( rh, flowSizes[0].Height ) );

		disocclusion = Make( CoreRender.PictureKind.Pair, rw, rh );
		inpaintingMask = Make( CoreRender.PictureKind.Single, dw, dh );
		pyramid = Make( CoreRender.PictureKind.Hdr, dw / 2, dh / 2, 16 );
		pyramidHas = CoreRenderDevice.PictureShape( pyramid, out _, out _, out var levels, out _ ) ? levels : 0;

		output = Make( CoreRender.PictureKind.Colour, dw, dh );
		PresentPicture = Make( CoreRender.PictureKind.Colour, dw, dh );

		constants = CoreRenderDevice.ReserveBuffer( ConstantLines * 16 );
		counters = CoreRenderDevice.ReserveBuffer( 32 );

		madeRender = render;
		madeDisplay = display;

		//In the formats the passes declare: one the device made in half floats instead would be written as garbage
		formatsRight = Is( luma[0], R32Uint, FlowLevels ) && Is( luma[1], R32Uint, FlowLevels ) &&
			Is( hudless[0], R8G8B8A8Unorm ) && Is( hudless[1], R8G8B8A8Unorm ) && Is( flowOut, R32G32Sint ) &&
			Is( scdHistogram, R32Uint ) && Is( scdPrevious, R32Sfloat ) && Is( scdTemp, R32Uint ) && Is( scdOutput, R32Uint ) &&
			Is( dilatedDepth, R32Sfloat ) && Is( dilatedMotion, R16G16Sfloat ) && Is( reconstructedPrevious, R32Uint ) &&
			Is( reconstructedInterpolated, R32Uint ) && Is( gameField, R32Uint ) && Is( flowField, R32Uint ) &&
			Is( disocclusion, R16G16Sfloat ) && Is( inpaintingMask, R16Sfloat ) && Is( pyramid, R16G16B16A16Sfloat ) &&
			pyramidHas > 0 && Is( output, R8G8B8A8Unorm ) && Is( PresentPicture, R8G8B8A8Unorm ) && constants != 0 && counters != 0;

		for ( var level = 0; level < FlowLevels; level++ )
			formatsRight &= Is( flow[0][level], R32G32Sint ) && Is( flow[1][level], R32G32Sint );

		if ( !formatsRight )
		{
			Log.Warning( "frame generation could not have its pictures in the formats its passes store - frames are not generated" );
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
	private const int R32G32Sint = 102;
	private const int R8G8B8A8Unorm = 37;
	private const int R16G16B16A16Sfloat = 97;

	private static bool Is( ulong picture, int format, int levels = 1 )
		=> picture != 0 && CoreRenderDevice.PictureShape( picture, out _, out _, out var has, out var made ) && made == format && has >= levels;

	//-- the frame ----------------------------------------------------------------

	private static bool first = true;
	private static int current;
	private static int flowFrame;
	private static float flowFrameIndex;
	private static bool reset;
	private static long lastCaptured = -2;

	/// <summary>Keeps this frame's world as it is before the interface is drawn over it, and readies its depth and motion - AMD's prepare. The window's picture must hold the finished world.</summary>
	internal static void Capture( ulong depth, ulong motion, (int Width, int Height) render, (int Width, int Height) display,
		Vector2 jitter, float near, float far, float field, float delta, bool restart )
	{
		if ( !Wanted || depth == 0 || motion == 0 ) return;
		if ( !Make( render, display ) ) return;

		//Taken, and the frame's end told to make a frame from it - false where this is not a window or one was taken already
		if ( !CoreRenderDevice.CaptureForGeneration( hudless[current] ) ) return;

		//A frame skipped since the last one taken leaves nothing to interpolate from
		reset = restart || first || EngineLoop.LoopFrames != lastCaptured + 1;
		lastCaptured = EngineLoop.LoopFrames;
		flowFrameIndex = reset ? 0.0f : flowFrameIndex + 1.0f;

		var (rw, rh) = render;
		var (dw, dh) = display;

		Span<float> c = stackalloc float[ConstantLines * 4];
		c.Clear();

		var aspect = rw / (float)rh;
		var horizontal = 2.0f * MathF.Atan( MathF.Tan( field * 0.5f ) * aspect );
		var cotHalf = MathF.Cos( 0.5f * field ) / MathF.Sin( 0.5f * field );
		var fQ = far / ( near - far );

		SetLine( c, 0, dw, dh, flowFrameIndex, 0.0f );
		SetLine( c, 1, 0.0f, 0.0f, 0.0f, 0.0f );
		SetLine( c, 2, rw, rh, dw, dh );
		SetLine( c, 3, 1.0f / dw, 1.0f / dh, near, far );
		SetLine( c, 4, 0.0f, 0.0f, dw, dh );
		SetLine( c, 5, -fQ, fQ * near, 1.0f / ( cotHalf / aspect ), 1.0f / cotHalf );
		SetLine( c, 6, Math.Clamp( delta, 0.0f, 1.0f ), reset ? 1.0f : 0.0f, 1.0f, 0.0f );
		SetLine( c, 7, 1.0f / dw, 1.0f / dh, FlowBlock, MathF.Tan( horizontal * 0.5f ) );

		//The engine's motion is already in uv, so a scale of one
		SetLine( c, 8, jitter.x, jitter.y, 1.0f, 1.0f );

		if ( !CoreRenderDevice.FrameWrite( constants, 0, MemoryMarshal.AsBytes( c ) ) ) return;

		if ( first ) CoreRenderDevice.FrameFill( counters, 0, 32, 0 );

		first = false;

		//The previous frame's nearest depth, kept by an atomic minimum from the far plane down
		Clear( reconstructedPrevious, Pass.ClearWhole, 1.0f );

		Dispatch( Pass.ReconstructAndDilate, [motion, depth], [reconstructedPrevious, dilatedMotion, dilatedDepth], null,
			( rw + 7 ) / 8, ( rh + 7 ) / 8 );
	}

	/// <summary>The frame between the last one and this, made from what Capture kept and PresentPicture - recorded at the frame's end, and nought where none was made.</summary>
	internal static ulong Generate()
	{
		if ( !Available || constants == 0 ) return 0;

		var (rw, rh) = madeRender;
		var (dw, dh) = madeDisplay;

		var cur = hudless[current];
		var prev = hudless[1 - current];

		var ok = OpticalFlow( cur, dw, dh ) && Interpolate( cur, prev, rw, rh, dw, dh );

		current = 1 - current;

		return ok ? output : 0;
	}

	//AMD's optical flow dispatch: this frame's luma and its pyramid, the scene change, then the flow level by level from the coarsest.
	private static bool OpticalFlow( ulong colour, int dw, int dh )
	{
		var odd = ( flowFrame & 1 ) != 0;
		var lumaNow = luma[odd ? 1 : 0];
		var lumaBefore = luma[odd ? 0 : 1];

		flowFrame++;

		if ( reset )
		{
			Clear( scdTemp, Pass.ClearWhole, 0.0f );
			Clear( scdOutput, Pass.ClearWhole, 0.0f );
			Clear( scdHistogram, Pass.ClearWhole, 0.0f );
			Clear( scdPrevious, Pass.ClearSingleFull, 0.0f );

			for ( var level = 0; level < FlowLevels; level++ )
			{
				Clear( luma[0], Pass.ClearWhole, 0.0f, level );
				Clear( luma[1], Pass.ClearWhole, 0.0f, level );
			}
		}

		Span<ulong> pyramidOut = stackalloc ulong[FlowLevels];
		Span<int> pyramidAt = stackalloc int[FlowLevels];

		for ( var level = 0; level < FlowLevels; level++ )
		{
			pyramidOut[level] = lumaNow;
			pyramidAt[level] = level;
		}

		var lumaGroups = ( ( dw - 1 ) / 64 + 1 ) * ( ( dh - 1 ) / 64 + 1 );
		var strata = dw / 4 / 3;

		var ok =
			Dispatch( Pass.PrepareLuma, [Texture( colour )], [lumaNow], null,
				( ( dw + 1 ) / 2 + 15 ) / 16, ( ( dh + 1 ) / 2 + 15 ) / 16, steps: (0, FlowLevels, 0, 0) ) &&

			Dispatch( Pass.LumaPyramid, [], pyramidOut, pyramidAt,
				( dw - 1 ) / 64 + 1, ( dh - 1 ) / 64 + 1, steps: (0, (uint)lumaGroups, 0, 0) ) &&

			Dispatch( Pass.ScdHistogram, [], [scdHistogram, lumaNow], null,
				( strata + 31 ) / 32, 16, 9, steps: (0, FlowLevels, 0, 0) ) &&

			Dispatch( Pass.ScdDivergence, [], [scdHistogram, scdPrevious, scdTemp, scdOutput], null, 9, 3,
				steps: (0, FlowLevels, 0, 0) );

		Span<int> four = stackalloc int[4];
		Span<int> five = stackalloc int[5];

		for ( var level = FlowLevels - 1; ok && level >= 0; level-- )
		{
			var oddLevel = ( level & 1 ) != 0;
			var a = flow[odd != oddLevel ? 1 : 0];
			var b = flow[odd != oddLevel ? 0 : 1];

			var lw = Math.Max( dw >> level, 1 );
			var lh = Math.Max( dh >> level, 1 );
			var steps = ((uint)level, (uint)FlowLevels, 0u, 0u);

			four[2] = four[3] = level;

			ok = Dispatch( Pass.Search, [], [a[level], scdOutput, lumaNow, lumaBefore], four,
				( ( lw + 3 ) / 4 * 16 + 63 ) / 64, ( lh + 15 ) / 16, steps: steps );

			//The finest level's flow goes where the interpolation reads it
			var filtered = level == 0 ? flowOut : b[level];

			ok = ok && Dispatch( Pass.Filter, [], [filtered, a[level]], null,
				( flowSizes[level].Width + 15 ) / 16, ( flowSizes[level].Height + 3 ) / 4, steps: steps );

			if ( !ok || level == 0 ) continue;

			five[2] = five[3] = level;

			ok = Dispatch( Pass.Scale, [], [b[level - 1], scdOutput, lumaNow, lumaBefore, b[level]], five,
				( flowSizes[level - 1].Width + 3 ) / 4, ( flowSizes[level - 1].Height + 3 ) / 4, steps: steps );
		}

		return ok;
	}

	//AMD's frame interpolation dispatch, after its setup: the game's motion and the flow's as fields, what was hidden, the frame, and its holes filled.
	private static bool Interpolate( ulong cur, ulong prev, int rw, int rh, int dw, int dh )
	{
		var renderX = ( rw + 7 ) / 8;
		var renderY = ( rh + 7 ) / 8;
		var displayX = ( dw + 7 ) / 8;
		var displayY = ( dh + 7 ) / 8;

		var ok = Dispatch( Pass.Setup, [], [gameField, gameField, flowField, flowField, disocclusion, disocclusion, scdOutput], null,
			renderX, renderY, counted: true );

		Span<ulong> pyramidOut = stackalloc ulong[1 + PyramidLevels];
		Span<int> pyramidAt = stackalloc int[1 + PyramidLevels];

		for ( var i = 0; i < PyramidLevels; i++ )
		{
			pyramidOut[1 + i] = pyramid;
			pyramidAt[1 + i] = Math.Min( i, pyramidHas - 1 );
		}

		if ( ok && !reset )
		{
			Clear( reconstructedInterpolated, Pass.ClearWhole, 1.0f );

			pyramidOut[0] = gameField;
			pyramidAt[0] = 0;

			ok = Dispatch( Pass.ReconstructPreviousDepth, [Texture( dilatedMotion ), Texture( dilatedDepth ), Texture( cur )],
					[reconstructedInterpolated], null, renderX, renderY ) &&

				Dispatch( Pass.GameMotionVectorField,
					[Texture( dilatedMotion ), Texture( dilatedDepth ), Texture( prev ), Texture( cur )],
					[gameField, gameField], null, renderX, renderY ) &&

				Dispatch( Pass.GameVectorFieldPyramid, [], pyramidOut, pyramidAt,
					( rw - 1 ) / 64 + 1, ( rh - 1 ) / 64 + 1, steps: Spd( rw, rh ), counted: true ) &&

				Dispatch( Pass.OpticalFlowVectorField, [0, 0, Texture( dilatedDepth ), Texture( prev ), Texture( cur )],
					[flowField, flowField, flowOut], null,
					( (int)( dw / (float)FlowBlock ) + 7 ) / 8, ( (int)( dh / (float)FlowBlock ) + 7 ) / 8 ) &&

				Dispatch( Pass.DisocclusionMask, [0, 0, 0, Texture( dilatedDepth ), 0, Texture( pyramid )],
					[disocclusion, gameField, reconstructedPrevious, reconstructedInterpolated], null, renderX, renderY );
		}

		pyramidOut[0] = pyramid;
		pyramidAt[0] = 0;

		return ok &&
			Dispatch( Pass.Interpolate, [0, 0, 0, 0, Texture( prev ), Texture( cur ), Texture( disocclusion ), Texture( pyramid )],
				[output, inpaintingMask, gameField, flowField], null, displayX, displayY, counted: true ) &&

			Dispatch( Pass.InpaintingPyramid, [Texture( output ), Texture( inpaintingMask )], pyramidOut, pyramidAt,
				( dw - 1 ) / 64 + 1, ( dh - 1 ) / 64 + 1, steps: Spd( dw, dh ), counted: true ) &&

			Dispatch( Pass.Inpainting, [0, Texture( pyramid ), Texture( PresentPicture ), Texture( cur )],
				[output, inpaintingMask, scdOutput], null, displayX, displayY );
	}

	//ffxSpdSetup over a whole picture: its mips - no more than the pyramid's passes store - and its work groups.
	private static (uint, uint, uint, uint) Spd( int width, int height )
	{
		var groups = ( ( width - 1 ) / 64 + 1 ) * ( ( height - 1 ) / 64 + 1 );
		var mips = Math.Min( (int)MathF.Floor( MathF.Log2( Math.Max( width, height ) ) ), Math.Min( PyramidLevels, pyramidHas ) );

		return ((uint)mips, (uint)groups, 0u, 0u);
	}

	//The lines of float4s the constants are - see the callbacks' lists.
	private const int ConstantLines = 10;

	private static void SetLine( Span<float> into, int line, float x, float y, float z, float w )
	{
		into[( line * 4 ) + 0] = x;
		into[( line * 4 ) + 1] = y;
		into[( line * 4 ) + 2] = z;
		into[( line * 4 ) + 3] = w;
	}

	private static ulong Texture( ulong picture ) => picture == 0 ? 0 : CoreRenderDevice.PictureTexture( picture );

	//One pass: reads by texture at the slots its SRV numbers name, outputs in binding order, the pass's own numbers in the push block's last line.
	private static bool Dispatch( Pass pass, ReadOnlySpan<ulong> reads, ReadOnlySpan<ulong> writes, ReadOnlySpan<int> levels,
		int groupsX, int groupsY, int groupsZ = 1, (uint, uint, uint, uint) steps = default, bool counted = false )
	{
		var pipeline = pipelines[(int)pass];
		if ( pipeline == 0 ) return false;

		Span<byte> push = stackalloc byte[128];
		push.Clear();

		var slots = MemoryMarshal.Cast<byte, uint>( push );

		for ( var i = 0; i < reads.Length && i < 12; i++ )
		{
			if ( reads[i] == 0 ) continue;

			CoreRenderDevice.ReadyToRead( reads[i] );
			slots[i] = (uint)CoreSceneDrawer.Indexed( reads[i] );
		}

		slots[12] = steps.Item1;
		slots[13] = steps.Item2;
		slots[14] = steps.Item3;
		slots[15] = steps.Item4;

		Span<int> at = stackalloc int[writes.Length];

		for ( var i = 0; i < writes.Length; i++ )
			at[i] = levels.IsEmpty ? 0 : levels[i];

		Span<ulong> buffers = stackalloc ulong[2] { constants, counters };

		CoreRenderDevice.ZoneBegin( passZones[(int)pass] );
		var ran = CoreRenderDevice.DispatchPass( pipeline, CoreSceneDrawer.WhiteTexture, writes, at,
			counted ? buffers : buffers[..1], push, groupsX, groupsY, groupsZ );
		CoreRenderDevice.ZoneEnd();

		return ran;
	}

	//One level of a picture set to a value - an integer one to the value's bits.
	private static void Clear( ulong picture, Pass build, float value, int level = 0 )
	{
		if ( picture == 0 || pipelines[(int)build] == 0 ) return;
		if ( !CoreRenderDevice.PictureShape( picture, out var width, out var height, out _, out _ ) ) return;

		width = Math.Max( width >> level, 1 );
		height = Math.Max( height >> level, 1 );

		Span<byte> push = stackalloc byte[128];
		push.Clear();

		MemoryMarshal.Cast<byte, float>( push )[0] = value;

		var words = MemoryMarshal.Cast<byte, uint>( push );
		words[4] = (uint)width;
		words[5] = (uint)height;

		Span<ulong> outputs = stackalloc ulong[1] { picture };
		Span<int> levels = stackalloc int[1] { level };

		CoreRenderDevice.DispatchPass( pipelines[(int)build], CoreSceneDrawer.WhiteTexture, outputs, levels,
			ReadOnlySpan<ulong>.Empty, push, ( width + 7 ) / 8, ( height + 7 ) / 8 );
	}
}
