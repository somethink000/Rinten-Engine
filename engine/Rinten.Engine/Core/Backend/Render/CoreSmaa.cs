using System;

namespace Rinten;

//The edge filters over a drawer's finished picture: SMAA (T2x in the full frame, 1x in the portable one), CMAA2 or FXAA,
//into where the frame goes - see engine/smaa.slang and engine/cmaa2.slang. S2x runs at the world's MSAA resolve instead
internal sealed class CoreSmaa
{
	/// <summary>Whether the player asked for SMAA; the portable frame has no MSAA and takes SMAA for it.</summary>
	internal static bool Smaa => Rinten.Rendering.DrawSettings.Smaa ||
		( CoreRenderDevice.PortableFrame && Rinten.Rendering.DrawSettings.Msaa >= 2 );

	/// <summary>Whether SMAA also blends this frame with the last, the two drawn half a pixel apart (SMAA T2x): the full frame's SMAA.</summary>
	internal static bool T2x => Rinten.Rendering.DrawSettings.Smaa && !CoreRenderDevice.PortableFrame;

	//Chosen against MSAA 8x on the edges scene (error per edge pixel): T2x on SMAA.h's diagonal and with no area offsets
	//4.4 (the other diagonal 4.9, offsets 4.7-4.9); 4x an eighth of a pixel across MSAA 2x's samples, 3.2
	/// <summary>Where T2x draws this frame from, in pixels: a quarter (an eighth over S2x, as 4x) along a diagonal, in turn.</summary>
	internal static Vector2 T2xJitter( int frame )
	{
		if ( S2x ) return frame % 2 == 0 ? new Vector2( 0.125f, -0.125f ) : new Vector2( -0.125f, 0.125f );
		return frame % 2 == 0 ? new Vector2( 0.25f, -0.25f ) : new Vector2( -0.25f, 0.25f );
	}

	/// <summary>Whether SMAA smooths MSAA 2x's two samples apart before they are averaged (SMAA S2x; with T2x, SMAA 4x); full frame only.</summary>
	internal static bool S2x => Rinten.Rendering.DrawSettings.SmaaS2x && Rinten.Rendering.DrawSettings.Msaa == 2 && !CoreRenderDevice.PortableFrame;

	/// <summary>Whether CMAA2 runs in SMAA's place; full frame only, as its passes are compute - see cmaa2.slang.</summary>
	internal static bool Cmaa => Rinten.Rendering.DrawSettings.Cmaa && !Smaa && !CoreRenderDevice.PortableFrame;

	/// <summary>Whether FXAA runs in SMAA's place; full frame only, as it reads the luma the compose leaves in alpha.</summary>
	internal static bool Fxaa => Rinten.Rendering.DrawSettings.Fxaa && !Smaa && !Cmaa && !CoreRenderDevice.PortableFrame;

	/// <summary>Whether any of them smooths the finished picture.</summary>
	internal static bool Asked => Smaa || Cmaa || Fxaa;

	private ulong edges, weights;

	//The portable frame's finished picture, which its finish draws into instead of the screen
	private ulong finished;
	private int width, height;

	//What each pass reads, made again when the picture read changes
	private ulong edgesReads, weightsReads, blendReads;
	private ulong edgesFrom;

	private readonly byte[] push = new byte[80];

	//T2x: this frame's blended picture and last frame's, written in turn, and the resolve's reads of them with the depth
	private readonly ulong[] frames = new ulong[2];
	private readonly ulong[] resolveReads = new ulong[2];
	private ulong resolveDepth;
	private int frameNow;
	private bool frameHeld;

	//S2x: the second sample's edges and weights, what its passes read, and 4x's blend of a picture with no weights
	private ulong secondEdges, secondWeights, s2xWeightsReads, s2xBlendReads, unweighedReads, unweighedFrom;

	//CMAA2: what it stores, and the apply's read of it; its buffers in binding order - the fifth its group counts, the last two
	//its edges and lists' heads - and the sizes of those two, its push block
	private ulong cmaaOut, cmaaReads, cmaaReadsFrom;
	private readonly ulong[] cmaaBuffers = new ulong[7];
	private readonly byte[] cmaaPush = new byte[16];

	/// <summary>The portable frame's picture to finish into, made the first time a finish is drawn into it.</summary>
	internal ulong Finished => finished != 0 || edges == 0
		? finished
		: finished = CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.Srgb, width, height );

	/// <summary>Readies the passes for a picture this size, and SMAA's edges and weights where pictures asks for them - CMAA2
	/// and FXAA read neither; false where nothing can run.</summary>
	internal bool Ready( int pictureWidth, int pictureHeight, bool pictures = true )
	{
		if ( pictureWidth < 2 || pictureHeight < 2 || !EnsureShared() ) return false;

		var portable = CoreRenderDevice.PortableFrame;
		pictures |= portable;

		if ( pictureWidth != width || pictureHeight != height )
		{
			Release();
			width = pictureWidth;
			height = pictureHeight;
		}

		if ( !pictures || edges != 0 ) return !portable || EnsurePortable();

		edges = CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.Colour, width, height, 1, storage: !portable );
		weights = CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.Colour, width, height, 1, storage: !portable );

		weightsReads = edges == 0 || weights == 0 ? 0 : CoreRenderDevice.CreateMaterial(
			Texture( edges ), area, search, Texture( edges ), 1, 0, 0, 0 );

		if ( weightsReads == 0 || portable && !EnsurePortable() )
		{
			Release();
			return false;
		}

		//The blend's read names the weights, so it is made again now there are some
		edgesFrom = 0;
		return true;
	}

	/// <summary>Smooths the edges of source into the screen or, in the full frame, into Final; T2x also blends in last frame,
	/// found through depth and toBefore (this frame's clip space into last frame's, unjittered).</summary>
	internal bool Run( ulong source, CoreRender.TargetKind into, ulong depth = 0, System.Numerics.Matrix4x4 toBefore = default )
	{
		if ( source == 0 || width == 0 ) return false;

		var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( push.AsSpan() );
		floats[0] = 1.0f / width;
		floats[1] = 1.0f / height;
		floats[2] = width;
		floats[3] = height;

		if ( edgesFrom != source )
		{
			if ( edgesReads != 0 ) CoreRenderDevice.DestroyTexture( edgesReads );
			if ( blendReads != 0 ) CoreRenderDevice.DestroyTexture( blendReads );

			edgesReads = CoreRenderDevice.CreateMaterial( source, source, source, source, 1, 0, 0, 0 );
			blendReads = weights != 0 ? CoreRenderDevice.CreateMaterial( source, Texture( weights ), source, source, 1, 0, 0, 0 ) : 0;

			if ( unweighedReads != 0 ) CoreRenderDevice.DestroyTexture( unweighedReads );
			unweighedReads = 0;
			edgesFrom = edgesReads != 0 && ( weights == 0 || blendReads != 0 ) ? source : 0;
		}

		if ( edgesFrom == 0 ) return false;
		if ( Cmaa ) return RunCmaa( source, into );
		if ( !Fxaa && edges == 0 ) return false;

		var fxaa = Fxaa;
		var t2x = !fxaa && T2x && depth != 0 && EnsureT2x( depth );
		if ( !t2x ) frameHeld = false;

		//SMAA 4x: S2x already smoothed this frame's edges, so the frame goes into T2x's blend with no weights at all. The
		//flag is this frame's alone: taken here, so a resolve that no longer runs cannot leave it set
		var smoothed = t2x && S2x && CoreWorldResolve.S2xDone && EnsureUnweighed( source );
		CoreWorldResolve.S2xDone = false;

		CoreRenderDevice.ZoneBegin( fxaa ? "fxaa" : "smaa" );

		try
		{
			if ( fxaa || smoothed ) { }
			else if ( CoreRenderDevice.PortableFrame )
			{
				if ( !Drawn( edgesPicturePipeline, edges, edgesReads ) ) return false;
				if ( !Drawn( weightsPicturePipeline, weights, weightsReads ) ) return false;
			}
			else
			{
				CoreRenderDevice.ReadyToRead( source );

				CoreRenderDevice.ZoneBegin( "smaa edges" );
				var found = Dispatch( edgesPipeline, edgesReads, edges );
				CoreRenderDevice.ZoneEnd();
				if ( !found ) return false;

				CoreRenderDevice.ZoneBegin( "smaa weights" );
				var weighed = Dispatch( weightsPipeline, weightsReads, weights );
				CoreRenderDevice.ZoneEnd();
				if ( !weighed ) return false;
			}

			if ( t2x ) return Resolved( into, toBefore, smoothed ? unweighedReads : blendReads );

			var blend = fxaa
				? ( into == CoreRender.TargetKind.Screen ? fxaaScreenPipeline : fxaaFinalPipeline )
				: ( into == CoreRender.TargetKind.Screen ? blendScreenPipeline : blendFinalPipeline );
			if ( blend == 0 ) return false;

			//Every pixel is written, so nothing of what the target held is read in first
			var opened = into != CoreRender.TargetKind.Screen;

			if ( opened && !CoreRenderDevice.BeginTarget( into, Color.Transparent, clearFirst: false, discard: true ) ) return false;

			CoreRenderDevice.Draw( blend, 3, push, fxaa ? edgesReads : blendReads );

			if ( opened ) CoreRenderDevice.EndTarget();

			return true;
		}
		finally
		{
			CoreRenderDevice.ZoneEnd();
		}
	}

	//CMAA2's passes as Intel's host runs them: edges, the shapes' group count, the shapes, the blends' count, the blends - into a
	//picture of its own, cleared so CmaaPs knows what changed, then laid over the finished picture into where the frame goes
	private bool RunCmaa( ulong source, CoreRender.TargetKind into )
	{
		if ( !EnsureCmaa( source ) ) return false;

		var apply = into == CoreRender.TargetKind.Screen ? cmaaScreenPipeline : cmaaFinalPipeline;
		if ( apply == 0 ) return false;

		CoreRenderDevice.ZoneBegin( "cmaa" );

		try
		{
			if ( !CoreRenderDevice.BeginPicture( cmaaOut, 0, Color.Transparent, clearFirst: true ) ) return false;
			CoreRenderDevice.EndTarget();
			CoreRenderDevice.ReadyToRead( source );

			//Only the blends store into a picture: the rest of its state is buffers, which no pass has to move between layouts
			Span<ulong> stored = stackalloc ulong[1] { cmaaOut };
			Span<int> level = stackalloc int[1];
			var args = cmaaBuffers[4];

			//A group a 28-pixel square: its 16x16 threads each a 2x2 quad, less the one-quad apron
			if ( !CoreRenderDevice.DispatchPass( cmaaEdgesPipeline, edgesReads, default, default, cmaaBuffers, cmaaPush,
				( width + 27 ) / 28, ( height + 27 ) / 28 ) ) return false;

			if ( !CoreRenderDevice.DispatchPass( cmaaArgsPipeline, 0, default, default, cmaaBuffers, cmaaPush, 2, 1 ) ) return false;
			if ( !CoreRenderDevice.DispatchPassIndirect( cmaaShapesPipeline, edgesReads, default, default, cmaaBuffers, cmaaPush, args ) ) return false;
			if ( !CoreRenderDevice.DispatchPass( cmaaArgsPipeline, 0, default, default, cmaaBuffers, cmaaPush, 1, 2 ) ) return false;
			if ( !CoreRenderDevice.DispatchPassIndirect( cmaaBlendsPipeline, edgesReads, stored, level, cmaaBuffers, cmaaPush, args ) ) return false;

			var opened = into != CoreRender.TargetKind.Screen;
			if ( opened && !CoreRenderDevice.BeginTarget( into, Color.Transparent, clearFirst: false, discard: true ) ) return false;

			CoreRenderDevice.Draw( apply, 3, push, cmaaReads );

			if ( opened ) CoreRenderDevice.EndTarget();

			return true;
		}
		finally
		{
			CoreRenderDevice.ZoneEnd();
		}
	}

	//CMAA2's pictures and buffers at this size - sized as Intel's host does, for all but extreme noise - and its passes
	private bool EnsureCmaa( ulong source )
	{
		if ( cmaaEdgesPipeline == 0 )
		{
			cmaaEdgesPipeline = CmaaPass( "EdgesColor2x2CS" );
			cmaaArgsPipeline = CmaaPass( "ComputeDispatchArgsCS" );
			cmaaShapesPipeline = CmaaPass( "ProcessCandidatesCS" );
			cmaaBlendsPipeline = CmaaPass( "DeferredColorApply2x2CS" );

			var pixel = CoreShaderCache.Pass( EngineShaders.Smaa, "CmaaPs", CoreShaderCompiler.Stage.Pixel, ComputeDefines );
			cmaaScreenPipeline = Screen( pixel, CoreRender.TargetKind.Screen );
			cmaaFinalPipeline = Screen( pixel, CoreRender.TargetKind.Final );
		}

		if ( cmaaEdgesPipeline == 0 || cmaaArgsPipeline == 0 || cmaaShapesPipeline == 0 || cmaaBlendsPipeline == 0 ) return false;

		if ( cmaaOut == 0 )
		{
			cmaaOut = CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.Colour, width, height, 1, storage: true );

			//Edges two pixels a word, packed as Intel's half-width R8 is; a head a 2x2 quad
			var pixels = width * height;
			var half = ( width + 1 ) / 2;
			cmaaBuffers[0] = CoreRenderDevice.CreateBuffer( new byte[pixels / 4 * 4] );
			cmaaBuffers[1] = CoreRenderDevice.CreateBuffer( new byte[( pixels + 3 ) / 6 * 4] );
			cmaaBuffers[2] = CoreRenderDevice.CreateBuffer( new byte[pixels / 2 * 8] );
			cmaaBuffers[3] = CoreRenderDevice.CreateBuffer( new byte[16 * 4] );
			cmaaBuffers[4] = CoreRenderDevice.CreateBuffer( new byte[4 * 4] );
			cmaaBuffers[5] = CoreRenderDevice.CreateBuffer( new byte[half * height * 4] );
			cmaaBuffers[6] = CoreRenderDevice.CreateBuffer( new byte[half * ( ( height + 1 ) / 2 ) * 4] );

			var grid = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>( cmaaPush.AsSpan() );
			grid[0] = (uint)half;
			grid[1] = (uint)height;
			grid[2] = (uint)half;
		}

		if ( cmaaOut == 0 || Array.IndexOf( cmaaBuffers, 0ul ) >= 0 ) return false;

		if ( cmaaReadsFrom != source )
		{
			if ( cmaaReads != 0 ) CoreRenderDevice.DestroyTexture( cmaaReads );
			cmaaReads = CoreRenderDevice.CreateMaterial( source, Texture( cmaaOut ), source, source, 1, 0, 0, 0 );
			cmaaReadsFrom = cmaaReads != 0 ? source : 0;
		}

		return cmaaReads != 0;
	}

	private void ForgetCmaa()
	{
		if ( cmaaReads != 0 ) CoreRenderDevice.DestroyTexture( cmaaReads );

		CoreRenderDevice.ReleasePicture( cmaaOut );

		for ( var k = 0; k < cmaaBuffers.Length; k++ )
		{
			if ( cmaaBuffers[k] != 0 ) CoreRenderDevice.DestroyBuffer( cmaaBuffers[k] );
			cmaaBuffers[k] = 0;
		}

		cmaaOut = cmaaReads = cmaaReadsFrom = 0;
	}

	private static ulong CmaaPass( string entry )
	{
		var code = CoreShaderCache.Pass( EngineShaders.Cmaa, entry, CoreShaderCompiler.Stage.Compute );
		return code is null ? 0 : CoreRenderDevice.CreatePassPipeline( code );
	}

	//T2x: the blend into this frame's picture, then the two frames resolved into where the frame goes
	private bool Resolved( CoreRender.TargetKind into, System.Numerics.Matrix4x4 toBefore, ulong frameReads )
	{
		var now = frameNow;
		var resolve = into == CoreRender.TargetKind.Screen ? resolveScreenPipeline : resolveFinalPipeline;
		if ( resolve == 0 || blendFramePipeline == 0 ) return false;

		CoreRenderDevice.ZoneBegin( "smaa resolve" );

		try
		{
			if ( !CoreRenderDevice.BeginPicture( frames[now], 0, Color.Transparent, clearFirst: false ) ) return false;
			CoreRenderDevice.Draw( blendFramePipeline, 3, push, frameReads );
			CoreRenderDevice.EndTarget();

			//The reprojection's x, y and w rows, turned so a point in front lands with w above nought - as CoreUpscaler's
			var m = toBefore;
			var sign = ( ( 0.5f * m.M34 ) + m.M44 ) < 0.0f ? -1.0f : 1.0f;
			var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( push.AsSpan() );

			floats[4] = m.M11 * sign; floats[5] = m.M21 * sign; floats[6] = m.M31 * sign; floats[7] = m.M41 * sign;
			floats[8] = m.M12 * sign; floats[9] = m.M22 * sign; floats[10] = m.M32 * sign; floats[11] = m.M42 * sign;
			floats[12] = m.M14 * sign; floats[13] = m.M24 * sign; floats[14] = m.M34 * sign; floats[15] = m.M44 * sign;

			//Two samples a pixel, evenly; the first frame has no last one
			floats[16] = frameHeld ? 0.5f : 0.0f;

			var opened = into != CoreRender.TargetKind.Screen;
			if ( opened && !CoreRenderDevice.BeginTarget( into, Color.Transparent, clearFirst: false, discard: true ) ) return false;

			CoreRenderDevice.Draw( resolve, 3, push, resolveReads[now] );

			if ( opened ) CoreRenderDevice.EndTarget();

			frameNow = 1 - now;
			frameHeld = true;
			return true;
		}
		finally
		{
			CoreRenderDevice.ZoneEnd();
		}
	}

	//T2x's two pictures, the blend into them, and the resolve's reads, made again where the depth picture changes
	private bool EnsureT2x( ulong depth )
	{
		for ( var k = 0; k < 2; k++ )
		{
			if ( frames[k] == 0 ) frames[k] = CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.Srgb, width, height );
			if ( frames[k] == 0 ) return false;
		}

		if ( blendFramePipeline == 0 )
		{
			if ( !CoreRenderDevice.PictureShape( frames[0], out _, out _, out _, out var format ) ) return false;
			blendFramePipeline = Picture( "BlendPs", format );
			if ( blendFramePipeline == 0 ) return false;
		}

		if ( resolveDepth != depth || resolveReads[0] == 0 )
		{
			ForgetResolveReads();

			for ( var k = 0; k < 2; k++ )
				resolveReads[k] = CoreRenderDevice.CreateMaterial( Texture( frames[k] ), Texture( frames[1 - k] ), depth, Texture( frames[k] ), 1, 0, 0, 0 );

			if ( resolveReads[0] == 0 || resolveReads[1] == 0 ) return false;

			resolveDepth = depth;
			frameHeld = false;
		}

		return true;
	}

	private void ForgetResolveReads()
	{
		for ( var k = 0; k < 2; k++ )
		{
			if ( resolveReads[k] != 0 ) CoreRenderDevice.DestroyTexture( resolveReads[k] );
			resolveReads[k] = 0;
		}

		resolveDepth = 0;
	}

	//4x's read of the frame with a weights picture that is nought everywhere
	private bool EnsureUnweighed( ulong source )
	{
		if ( unweighedReads != 0 && unweighedFrom == source ) return true;
		if ( unweighedReads != 0 ) CoreRenderDevice.DestroyTexture( unweighedReads );

		noWeights = noWeights != 0 ? noWeights : CoreRenderDevice.CreateTextureRgba( new byte[4], 1, 1, srgb: false );
		unweighedReads = noWeights == 0 ? 0 : CoreRenderDevice.CreateMaterial( source, noWeights, source, source, 1, 0, 0, 0 );
		unweighedFrom = source;
		return unweighedReads != 0;
	}

	/// <summary>SMAA S2x: SMAA's edges and weights on each of MSAA 2x's samples, read in place, each with its own offset in the
	/// area table, and the two blended and averaged into the world's picture, opened here. False before anything is opened.</summary>
	internal bool RunS2x( ulong samples, int pictureWidth, int pictureHeight, float exposure )
	{
		if ( !Ready( pictureWidth, pictureHeight ) || !EnsureS2x() ) return false;

		CoreRenderDevice.ZoneBegin( "smaa s2x" );

		try
		{
			var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( push.AsSpan() );
			floats[0] = 1.0f / width;
			floats[1] = 1.0f / height;
			floats[2] = width;
			floats[3] = height;
			floats[17] = Math.Max( exposure, 1e-4f );
			floats[18] = CoreRender.TextureIndex( samples );

			if ( !Dispatch( edgesS2xPipeline, weightsReads, edges, secondEdges ) ) return false;
			if ( !Dispatch( weightsS2xPipeline, s2xWeightsReads, weights, secondWeights ) ) return false;

			floats[17] = 0.0f;

			if ( !VkFrame.WorldResolveOpen() ) return false;

			CoreRenderDevice.Draw( s2xBlendPipeline, 3, push, s2xBlendReads );
			CoreRenderDevice.EndTarget();
			return true;
		}
		finally
		{
			System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( push.AsSpan() )[18] = 0.0f;
			CoreRenderDevice.ZoneEnd();
		}
	}

	//S2x's second edges and weights and the reads of them, at this size; and its two passes
	private bool EnsureS2x()
	{
		if ( edgesS2xPipeline == 0 ) edgesS2xPipeline = Compute( "EdgesS2xCs" );
		if ( weightsS2xPipeline == 0 ) weightsS2xPipeline = Compute( "WeightsS2xCs" );
		if ( s2xBlendPipeline == 0 )
			s2xBlendPipeline = Screen( CoreShaderCache.Pass( EngineShaders.Smaa, "S2xBlendPs", CoreShaderCompiler.Stage.Pixel, ComputeDefines ),
				CoreRender.TargetKind.SceneColour );

		if ( edgesS2xPipeline == 0 || weightsS2xPipeline == 0 || s2xBlendPipeline == 0 ) return false;
		if ( s2xBlendReads != 0 ) return true;

		secondEdges = CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.Colour, width, height, 1, storage: true );
		secondWeights = CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.Colour, width, height, 1, storage: true );
		if ( secondEdges == 0 || secondWeights == 0 ) return false;

		s2xWeightsReads = CoreRenderDevice.CreateMaterial( Texture( edges ), area, search, Texture( secondEdges ), 1, 0, 0, 0 );
		s2xBlendReads = CoreRenderDevice.CreateMaterial( Texture( weights ), Texture( secondWeights ), Texture( weights ), Texture( weights ), 1, 0, 0, 0 );
		return s2xWeightsReads != 0 && s2xBlendReads != 0;
	}

	private void ForgetS2x()
	{
		foreach ( var material in new[] { s2xWeightsReads, s2xBlendReads, unweighedReads } )
			if ( material != 0 ) CoreRenderDevice.DestroyTexture( material );

		CoreRenderDevice.ReleasePicture( secondEdges );
		CoreRenderDevice.ReleasePicture( secondWeights );

		secondEdges = secondWeights = s2xWeightsReads = s2xBlendReads = unweighedReads = unweighedFrom = 0;
	}

	//A portable pass into its picture, cleared first: the shader writes only where it found something
	private bool Drawn( ulong pipeline, ulong into, ulong reads )
	{
		if ( pipeline == 0 || !CoreRenderDevice.BeginPicture( into, 0, Color.Transparent, clearFirst: true ) ) return false;

		CoreRenderDevice.Draw( pipeline, 3, push, reads );
		CoreRenderDevice.EndTarget();
		return true;
	}

	//Second: S2x's passes store the second sample's picture beside the first
	private bool Dispatch( ulong pipeline, ulong reads, ulong into, ulong second = 0 )
	{
		Span<ulong> outputs = stackalloc ulong[2] { into, second };
		Span<int> levels = stackalloc int[2];
		if ( second == 0 ) { outputs = outputs[..1]; levels = levels[..1]; }

		return CoreRenderDevice.DispatchPass( pipeline, reads, outputs, levels, ReadOnlySpan<ulong>.Empty, push,
			( width + 7 ) / 8, ( height + 7 ) / 8 );
	}

	private static ulong Texture( ulong picture ) => CoreRenderDevice.PictureTexture( picture );

	/// <summary>Gives the pictures back and drops the materials naming them.</summary>
	internal void Release()
	{
		foreach ( var material in new[] { edgesReads, weightsReads, blendReads } )
			if ( material != 0 ) CoreRenderDevice.DestroyTexture( material );

		ForgetResolveReads();
		ForgetS2x();
		ForgetCmaa();

		CoreRenderDevice.ReleasePicture( edges );
		CoreRenderDevice.ReleasePicture( weights );
		CoreRenderDevice.ReleasePicture( finished );

		for ( var k = 0; k < 2; k++ )
		{
			CoreRenderDevice.ReleasePicture( frames[k] );
			frames[k] = 0;
		}

		frameHeld = false;

		edges = weights = finished = 0;
		edgesReads = weightsReads = blendReads = 0;
		edgesFrom = 0;
		width = height = 0;
	}

	//-- shared by every drawer: the two tables and the passes -----------------------------------

	private static ulong area, search;
	private static ulong edgesPipeline, weightsPipeline, blendScreenPipeline, blendFinalPipeline;
	private static ulong fxaaScreenPipeline, fxaaFinalPipeline;
	private static ulong resolveScreenPipeline, resolveFinalPipeline, blendFramePipeline;
	private static ulong edgesS2xPipeline, weightsS2xPipeline, s2xBlendPipeline, noWeights;
	private static ulong cmaaEdgesPipeline, cmaaArgsPipeline, cmaaShapesPipeline, cmaaBlendsPipeline, cmaaScreenPipeline, cmaaFinalPipeline;
	private static ulong edgesPicturePipeline, weightsPicturePipeline;
	private static byte[] vertex;
	private static int picturesFormat;
	private static int builtRevision = -1, builtDevice = -1;
	private static bool failed;

	//The compute build, which declares the picture it stores into - see smaa.slang
	internal const string ComputeDefines = "#define SMAA_COMPUTE 1\n";

	//The reference's tables: area 160x560 in two bytes a texel, search 64x16 in one
	private const int AreaWidth = 160, AreaHeight = 560, SearchWidth = 64, SearchHeight = 16;

	private static bool EnsureShared()
	{
		//The old device's handles went with it, and an edited shader asks for its passes again
		if ( builtDevice != CoreResources.Cleared )
		{
			ForgetPipelines();
			area = search = noWeights = 0;
			builtDevice = CoreResources.Cleared;
		}
		else if ( builtRevision != CoreShaderCache.Revision )
		{
			DestroyPipelines();
		}

		if ( builtRevision == CoreShaderCache.Revision ) return !failed;

		builtRevision = CoreShaderCache.Revision;
		failed = !CoreNative.Available || !EnsureTables() || !Build();

		return !failed;
	}

	private static bool EnsureTables()
	{
		if ( area != 0 && search != 0 ) return true;

		var areaBytes = Embedded( "smaa_area.bin", AreaWidth * AreaHeight * 2 );
		var searchBytes = Embedded( "smaa_search.bin", SearchWidth * SearchHeight );

		if ( areaBytes is null || searchBytes is null )
		{
			Log.Warning( "SMAA's tables are not in the engine - edges are left as drawn" );
			return false;
		}

		//Eight bits a channel is what every device takes; the tables' own two and one go in red and green
		var texels = new byte[AreaWidth * AreaHeight * 4];

		for ( var i = 0; i < AreaWidth * AreaHeight; i++ )
		{
			texels[i * 4] = areaBytes[i * 2];
			texels[( i * 4 ) + 1] = areaBytes[( i * 2 ) + 1];
		}

		area = CoreRenderDevice.CreateTextureRgba( texels, AreaWidth, AreaHeight, srgb: false );

		texels = new byte[SearchWidth * SearchHeight * 4];

		for ( var i = 0; i < SearchWidth * SearchHeight; i++ ) texels[i * 4] = searchBytes[i];

		search = CoreRenderDevice.CreateTextureRgba( texels, SearchWidth, SearchHeight, srgb: false );

		return area != 0 && search != 0;
	}

	private static byte[] Embedded( string name, int length )
	{
		using var stream = typeof( CoreSmaa ).Assembly.GetManifestResourceStream( name );
		if ( stream is null ) return null;

		var bytes = new byte[length];
		return stream.ReadAtLeast( bytes, length, throwOnEndOfStream: false ) < length ? null : bytes;
	}

	//The blend onto the screen everywhere; the full frame's compute passes and blend into Final, or the portable
	//frame's passes into pictures, which wait for a picture to learn its format - see EnsurePortable
	private static bool Build()
	{
		vertex = CoreShaderCache.Pass( EngineShaders.Smaa, "MainVs", CoreShaderCompiler.Stage.Vertex );
		var blend = CoreShaderCache.Pass( EngineShaders.Smaa, "BlendPs", CoreShaderCompiler.Stage.Pixel );
		var fxaa = CoreShaderCache.Pass( EngineShaders.Smaa, "FxaaPs", CoreShaderCompiler.Stage.Pixel );
		var resolve = CoreShaderCache.Pass( EngineShaders.Smaa, "ResolvePs", CoreShaderCompiler.Stage.Pixel );

		blendScreenPipeline = Screen( blend, CoreRender.TargetKind.Screen );
		fxaaScreenPipeline = Screen( fxaa, CoreRender.TargetKind.Screen );

		if ( !CoreRenderDevice.PortableFrame )
		{
			blendFinalPipeline = Screen( blend, CoreRender.TargetKind.Final );
			fxaaFinalPipeline = Screen( fxaa, CoreRender.TargetKind.Final );
			resolveScreenPipeline = Screen( resolve, CoreRender.TargetKind.Screen );
			resolveFinalPipeline = Screen( resolve, CoreRender.TargetKind.Final );
			edgesPipeline = Compute( "EdgesCs" );
			weightsPipeline = Compute( "WeightsCs" );
		}

		var built = blendScreenPipeline != 0 &&
			( CoreRenderDevice.PortableFrame || blendFinalPipeline != 0 && edgesPipeline != 0 && weightsPipeline != 0 );

		if ( !built ) Log.Warning( $"SMAA's passes did not build: {CoreRenderDevice.ShaderError}" );

		return built;
	}

	//The portable passes for the pictures' format, made again where it changes
	private bool EnsurePortable()
	{
		if ( !CoreRenderDevice.PictureShape( edges, out _, out _, out _, out var format ) ) return false;
		if ( format == picturesFormat && edgesPicturePipeline != 0 && weightsPicturePipeline != 0 ) return true;

		if ( edgesPicturePipeline != 0 ) CoreRenderDevice.DestroyPipeline( edgesPicturePipeline );
		if ( weightsPicturePipeline != 0 ) CoreRenderDevice.DestroyPipeline( weightsPicturePipeline );

		picturesFormat = format;
		edgesPicturePipeline = Picture( "EdgesPs", format );
		weightsPicturePipeline = Picture( "WeightsPs", format );

		if ( edgesPicturePipeline == 0 || weightsPicturePipeline == 0 )
		{
			Log.Warning( $"SMAA's passes did not build: {CoreRenderDevice.ShaderError}" );
			return false;
		}

		return true;
	}

	private static ulong Screen( byte[] pixel, CoreRender.TargetKind target )
		=> vertex is null || pixel is null ? 0 : CoreRenderDevice.CreatePipeline( vertex, pixel, CoreRender.VertexLayoutKind.None,
			depthTest: false, CoreRender.Topology.Triangles, blend: false, depthWrite: false, target: target );

	private static ulong Picture( string entry, int format )
	{
		var pixel = CoreShaderCache.Pass( EngineShaders.Smaa, entry, CoreShaderCompiler.Stage.Pixel );

		return vertex is null || pixel is null ? 0 : CoreRenderDevice.CreatePipeline( vertex, pixel, CoreRender.VertexLayoutKind.None,
			depthTest: false, CoreRender.Topology.Triangles, blend: false, depthWrite: false,
			target: CoreRender.TargetKind.Picture, pictureFormat: format );
	}

	private static ulong Compute( string entry )
	{
		var code = CoreShaderCache.Pass( EngineShaders.Smaa, entry, CoreShaderCompiler.Stage.Compute, ComputeDefines );
		return code is null ? 0 : CoreRenderDevice.CreatePassPipeline( code );
	}

	private static void DestroyPipelines()
	{
		foreach ( var pipeline in new[] { edgesPipeline, weightsPipeline, blendScreenPipeline, blendFinalPipeline,
			edgesPicturePipeline, weightsPicturePipeline, fxaaScreenPipeline, fxaaFinalPipeline,
			resolveScreenPipeline, resolveFinalPipeline, blendFramePipeline, edgesS2xPipeline, weightsS2xPipeline, s2xBlendPipeline,
			cmaaEdgesPipeline, cmaaArgsPipeline, cmaaShapesPipeline, cmaaBlendsPipeline, cmaaScreenPipeline, cmaaFinalPipeline } )
			if ( pipeline != 0 ) CoreRenderDevice.DestroyPipeline( pipeline );

		ForgetPipelines();
	}

	/// <summary>Drops the passes with the device that made them.</summary>
	internal static void ForgetPipelines()
	{
		edgesPipeline = weightsPipeline = blendScreenPipeline = blendFinalPipeline = 0;
		fxaaScreenPipeline = fxaaFinalPipeline = 0;
		resolveScreenPipeline = resolveFinalPipeline = blendFramePipeline = 0;
		edgesS2xPipeline = weightsS2xPipeline = s2xBlendPipeline = 0;
		cmaaEdgesPipeline = cmaaArgsPipeline = cmaaShapesPipeline = cmaaBlendsPipeline = cmaaScreenPipeline = cmaaFinalPipeline = 0;
		edgesPicturePipeline = weightsPicturePipeline = 0;
		picturesFormat = 0;
		vertex = null;
		builtRevision = -1;
		failed = false;
	}
}
