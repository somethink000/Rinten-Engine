using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Rinten;

//The renderer facade: Vulkan or WebGPU, picked by a static readonly bool so the JIT folds the branch
internal static unsafe class CoreRender
{
	//-- the renderer's shapes: callers name them, and the shader compiler and pipeline baker agree on the numbers --

	/// <summary>Matches sbc_vertex_layout.</summary>
	public enum VertexLayoutKind
	{
		None = 0,
		Engine = 1,
		Line = 2,
		Ui = 3,
		Skinned = 4,
		Morphed = 5,
		MorphedSkinned = 6,

		/// <summary>No vertices at all: the first stage is a mesh stage, which makes its own.</summary>
		MeshShader = 7,
	}

	/// <summary>Matches sbc_topology.</summary>
	public enum Topology
	{
		Triangles = 0,
		Lines = 1,
	}

	//Which picture a pass draws into, as sbc_render_target; a format the device cannot use as asked is made
	//in half floats instead, and the picture says which - see CoreRenderDevice.AcquirePicture
	internal enum PictureKind
	{
		/// <summary>Four half floats: light past white, what the world is shaded in.</summary>
		Hdr = 0,

		/// <summary>Light past white in thirty-two bits where the device stores into it - no alpha.</summary>
		HdrCompact = 1,

		/// <summary>Eight bits a channel: a mask, a colour between nought and one.</summary>
		Colour = 2,

		/// <summary>One half float.</summary>
		Single = 3,

		/// <summary>Two half floats: a direction, a movement.</summary>
		Pair = 4,

		/// <summary>Four whole floats.</summary>
		Full = 5,

		/// <summary>One whole float: a distance.</summary>
		SingleFull = 6,

		/// <summary>Ten bits a colour channel, two of alpha.</summary>
		Ten = 7,

		/// <summary>One unsigned integer: a counter or an atomic-min depth, read as stored, not as float.</summary>
		Whole = 8,

		/// <summary>Two signed integers: a movement in whole pixels, read as stored, not as float.</summary>
		WholePair = 9,
	}

	internal enum TargetKind
	{
		Screen = 0,
		Scene = 1,
		Depth = 4,

		/// <summary>Where what moved says how far - see TargetVelocity.</summary>
		Velocity = 5,

		/// <summary>The other full-size picture a post chain alternates with - see CorePostBlit.</summary>
		Post = 6,

		/// <summary>The world's picture with only its colour attached, the other half of that alternation.</summary>
		SceneColour = 7,

		//After tonemap and upscale at window size, for effects on what the player sees; made on first use
		/// <summary>The finished picture at window size and its partner, for passes after the compose.</summary>
		Final = 11,
		FinalSecond = 12,

		/// <summary>World and partner at window size, pre-curve, after a temporal upscale; full frame only.</summary>
		Display = 13,
		DisplaySecond = 14,



		/// <summary>Sky colour by elevation and sun angle, drawn once a frame - see VkDevice.TargetSkyTable.</summary>
		SkyTable = 17,


		/// <summary>A layer of a drawer's shadow map, opened with BeginShadow - see CoreSunPages.</summary>
		Shadow = 19,

		/// <summary>A level of a frame picture; its pipeline is baked for the picture's format.</summary>
		Picture = 20,
	}

	//A string the core owns. Valid until whatever it belongs to is destroyed, so it is copied out here
	//rather than held. Which half of this file runs. Constant for the process - see the remarks above.
	private static readonly bool web = OperatingSystem.IsBrowser();

	//Frame tier: portable in a browser, else what the Vulkan driver supports; full-only calls return 0
	internal static RenderTier Tier => web ? RenderTier.Portable : VkDevice.Tier;

	//Whether this is the portable frame. Read where a call belongs to the full one alone.
	private static bool Portable => web || VkDevice.Tier == RenderTier.Portable;

	//The device's shader language: a browser's own, or SPIR-V of the frame the Vulkan device holds
	internal static CoreShaderCompiler.Format ShaderFormat
		=> web ? BrowserDevice.ShaderFormat
			: VkDevice.Tier == RenderTier.Portable ? CoreShaderCompiler.Format.SpirvPortable
			: CoreShaderCompiler.Format.Spirv;

	//Routes each call to a backend; a 0 on the web means the browser renderer lacks that path

	//WebGPU opens through promises a page cannot block on, so the launcher awaits BrowserDevice.Init first
	internal static int Init( int enableValidation )
		=> web ? ( BrowserDevice.Ready ? 1 : 0 ) : ( VkDevice.Init( enableValidation != 0 ) ? 1 : 0 );

	internal static void Shutdown()
	{
		if ( web ) BrowserDevice.Shutdown();
		else VkDevice.Shutdown();
	}

	internal static int Ready()
		=> web ? ( BrowserDevice.Ready ? 1 : 0 ) : ( VkDevice.Ready ? 1 : 0 );

	internal static IntPtr Info()
		=> PinText( ref infoUtf8, ref infoPin, web ? BrowserDevice.infoText : VkDevice.infoText );

	internal static IntPtr DeviceName()
		=> PinText( ref nameUtf8, ref namePin, web ? BrowserDevice.deviceNameText : VkDevice.deviceNameText );

	internal static void DeviceInfo( int* outInfo )
	{
		if ( web ) BrowserDevice.DeviceInfo( outInfo );
		else VkDevice.DeviceInfo( outInfo );
	}

	internal static ulong SwapchainCreate( int kind, ulong display, ulong window,
		int width, int height, int transparent )
		=> web ? BrowserSwapchain.SwapchainCreate( kind, window, width, height )
			: VkSwapchain.Create( kind, display, window, width, height, transparent );

	internal static int SwapchainResize( ulong swapchain, int width, int height )
		=> web ? BrowserSwapchain.SwapchainResize( swapchain, width, height )
			: VkSwapchain.Resize( swapchain, width, height );

	internal static int SwapchainVsync( ulong swapchain, int enabled )
		=> web ? BrowserSwapchain.SwapchainVsync( swapchain, enabled ) : VkSwapchain.Vsync( swapchain, enabled );

	//The web canvas is the page's size with no pictures behind it to shrink, so its scale is one
	internal static int SwapchainScale( ulong swapchain, float scale )
		=> Portable ? 0 : VkSwapchain.Scale( swapchain, scale );

	//A browser has no library to hand a driver's compiled pipelines to, and no driver of its own
	//to hand them back - the page's own caches are the only thing between a run and the next one.
	internal static unsafe int PipelineCacheLoad( byte* data, int size )
		=> web ? 0 : VkDevice.PipelineCacheLoad( data, size );

	internal static unsafe int PipelineCacheSave( byte* outData, int capacity )
		=> web ? 0 : VkDevice.PipelineCacheSave( outData, capacity );

	internal static void SwapchainDestroy( ulong swapchain )
	{
		if ( web ) BrowserSwapchain.SwapchainDestroy( swapchain );
		else VkSwapchain.Destroy( swapchain );
	}

	//On the web, the canvas size from the page, which resizes it and is the size a frame draws at
	internal static void SwapchainInfo( ulong swapchain, int* outInfo )
	{
		if ( !web ) { VkSwapchain.Info( swapchain, outInfo ); return; }
		if ( outInfo is null ) return;
		outInfo[0] = WebPage.CanvasWidth?.Invoke() ?? 0;
		outInfo[1] = WebPage.CanvasHeight?.Invoke() ?? 0;
		outInfo[2] = 1;
		outInfo[3] = 1;
	}

	internal static int FrameBegin( ulong swapchain, float r, float g, float b, float a )
		=> web ? BrowserSwapchain.FrameBegin( swapchain, r, g, b, a )
			: VkFrame.FrameBegin( swapchain, r, g, b, a );

	internal static int FrameSetup( float* data, int floatCount )
		=> web ? BrowserSwapchain.FrameSetup( data, floatCount ) : VkFrame.FrameSetup( data, floatCount );

	//The second eye's frame block - see VkFrame.FrameSetupSecondEye and BrowserSwapchain's.
	internal static int FrameSetupSecondEye( float* data, int floatCount )
		=> web
			? BrowserSwapchain.FrameSetupSecondEye( data, floatCount )
			: VkFrame.FrameSetupSecondEye( data, floatCount );

	//A run of the frame block written again part way through the frame - see VkFrame.FrameBlockPatch.
	internal static int FrameBlockPatch( int floatOffset, float* data, int floatCount )
		=> web ? BrowserSwapchain.FramePatch( floatOffset, data, floatCount ) : VkFrame.FrameBlockPatch( floatOffset, data, floatCount );

	//A second frame block and which one is read - see VkFrame.FrameBlockPut: a light's view, drawn into its map.
	internal static ulong FrameBlockPut( float* data, int floatCount )
		=> web ? BrowserSwapchain.FramePut( data, floatCount ) : VkFrame.FrameBlockPut( data, floatCount );

	internal static ulong FrameBlockUse( ulong offset )
		=> web ? BrowserSwapchain.FrameUse( offset ) : VkFrame.FrameBlockUse( offset );

	//The lights' buffer, which the browser backend has no binding for.
	internal static int SetLightBuffer( ulong buffer )
		=> Portable ? 0 : VkFrame.SetLightBuffer( buffer );

	internal static int FrameEnd()
		=> web ? BrowserSwapchain.FrameEnd() : VkFrame.FrameEnd();

	//Every frame handed to the submit thread gone to the queue and the display - see VkSubmitter.
	internal static void FramesHandedOver()
	{
		if ( !web ) VkSubmitter.Drain();
	}

	internal static ulong FrameCount( ulong swapchain )
		=> web ? BrowserSwapchain.FrameCount( swapchain ) : VkSwapchain.FrameCount( swapchain );

	//A page has no timestamp queries, so there is nothing to time
	[System.Runtime.CompilerServices.MethodImpl( System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining )]
	internal static int ZoneBegin( string name )
		=> web ? 0 : VkFrame.ZoneBegin( name );

	[System.Runtime.CompilerServices.MethodImpl( System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining )]
	internal static void ZoneEnd()
	{
		if ( !web ) VkFrame.ZoneEnd();
	}

	internal static int ProfileStats( ulong viewport, float* outValues, int count )
		=> web ? 0 : VkFrame.ProfileStats( viewport, outValues, count );

	//WebGPU has no equivalent of the memory budget - a page is not told what the card is doing - so
	//nothing is written and the caller sees "the driver did not answer" rather than a made-up number.
	internal static int Memory( long* outValues, int count )
		=> web ? 0 : VkDevice.Memory( outValues, count );

	//WebGPU reads back through a promise and cannot answer inside a frame at all, so the capture records
	//nothing and the take says it has nothing - which the occlusion path already handles by trusting no depth.
	internal static int BufferCapture( ulong buffer )
		=> web ? 0 : VkFrame.BufferCapture( buffer );

	internal static int BufferTake( ulong buffer, byte* outBytes, int capacity )
		=> web ? 0 : VkFrame.BufferTake( buffer, outBytes, capacity );

	internal static int ProfileZone( ulong viewport, int index,
		byte* name, int nameCapacity, float* outValues, int count )
		=> web ? 0 : VkFrame.ProfileZone( viewport, index, name, nameCapacity, outValues, count );

	//specialize: which specialization constants the pipeline is baked with, or -1 for the shader's own defaults
	//- see VkPipelines.Create and BrowserPipelines.Create, which set them the same way, one bit an id.
	internal static ulong PipelineCreate(
		byte* vertexSpirv, int vertexSize, string vertexEntry,
		byte* pixelSpirv, int pixelSize, string pixelEntry,
		int vertexLayout, int depthTest, int topology, int blend, int depthWrite, int cullBack, int depthOnly,
		int target, int fillLines, int specialize = -1, int pictureFormat = -1 )
		=> web
			? BrowserPipelines.Create( vertexSpirv, vertexSize, vertexEntry,
				pixelSpirv, pixelSize, pixelEntry, vertexLayout, depthTest, topology, blend,
				depthWrite, cullBack, depthOnly, target, specialize, pictureFormat )
			: VkPipelines.Create( vertexSpirv, vertexSize, vertexEntry,
				pixelSpirv, pixelSize, pixelEntry, vertexLayout, depthTest, topology, blend,
				depthWrite, cullBack, depthOnly, target, fillLines, specialize, pictureFormat );

	internal static ulong ViewCreate( int width, int height, int layers = 1 )
		=> web ? BrowserSwapchain.ViewCreate( width, height, layers ) : VkSwapchain.ViewCreate( width, height, layers );

	//Whether both headset eyes draw in one pass; needs the extension plus stereo surfaces and pipelines
	internal static bool Multiview => web
		? StereoPossible && CoreResources.StereoBaked && CoreSceneDrawer.StereoReady
			&& !CoreSceneRenderer.StereoIncomplete
		: VkDevice.multiview && VkDevice.Tier == RenderTier.Portable;

	//Whether the work a headset's two eyes have in common is done once a frame
	[ConVar( "r_share_views", Help = "Do the work a headset's two eyes share once a frame instead of twice." )]
	internal static bool ShareViews { get; set; } = true;

	//-noshareviews: same as the convar, from a page; read lazily since a browser parses its args late
	private static bool? noShareViews;

	//Whether the eyes' shared work is shared - the convar and the switch together.
	internal static bool SharingViews =>
		ShareViews && !( noShareViews ??= Rinten.Utility.CommandLine.HasSwitch( "-noshareviews" ) );

	//Whether two-eye passes need pipelines built for two views; only WebGL2, where GLSL sets num_views
	internal static bool StereoPipelines => web && Multiview;

	//Whether a two-view build is worth trying at all; asked before Multiview, which needs the pipelines
	internal static bool StereoPossible => web && BrowserNative.XrStereo() != 0;

	//An image a layer of the view's next frame is copied into as it closes - see VkSwapchain.ViewCopyTo.
	internal static int ViewCopyTo( ulong view, ulong image, int width, int height, int layer = 0 )
		=> web
			? BrowserSwapchain.ViewCopyTo( view, image, width, height, layer )
			: VkSwapchain.ViewCopyTo( view, image, width, height, layer );

	//How the view's world is foveated - see VkSwapchain.ViewFovea. A page has no way to.
	internal static int ViewFovea( ulong view, float strength, bool tracked, float u0, float v0, float u1, float v1 )
		=> web ? 0 : VkSwapchain.ViewFovea( view, strength, tracked, u0, v0, u1, v1 );

	//Whether this device shades a headset's eyes coarser away from where they look - see VkFovea.
	internal static bool Foveates => !web && VkFovea.Kind != VkFovea.Kinds.None;

	//A texture the view's next frame is copied into as it closes - see VkSwapchain.ViewCopyToTexture.
	internal static int ViewCopyToTexture( ulong view, ulong texture )
		=> web ? 0 : VkSwapchain.ViewCopyToTexture( view, texture );

	internal static void ViewDestroy( ulong view )
	{
		if ( web ) BrowserSwapchain.ViewDestroy( view );
		else VkSwapchain.ViewDestroy( view );
	}

	internal static int ViewResize( ulong view, int width, int height )
		=> web ? BrowserSwapchain.ViewResize( view, width, height ) : VkSwapchain.ViewResize( view, width, height );

	internal static int ViewScale( ulong view, float scale )
		=> web ? 0 : VkSwapchain.ViewScale( view, scale );

	internal static int ViewBegin( ulong view, float r, float g, float b, float a )
		=> web ? BrowserSwapchain.ViewBegin( view, r, g, b, a ) : VkSwapchain.ViewBegin( view, r, g, b, a );

	internal static void ViewEnd()
	{
		if ( web ) BrowserSwapchain.ViewEnd();
		else VkSwapchain.ViewEnd();
	}

	internal static int ViewFrameBegin( ulong view, float r, float g, float b, float a )
		=> web ? BrowserSwapchain.ViewFrameBegin( view, r, g, b, a ) : VkSwapchain.ViewFrameBegin( view, r, g, b, a );

	internal static int ViewFrameEnd( ulong view )
		=> web ? BrowserSwapchain.ViewFrameEnd( view ) : VkSwapchain.ViewFrameEnd( view );

	internal static int ViewFrameEndAsync( ulong view )
		=> web ? BrowserSwapchain.ViewFrameEndAsync( view ) : VkSwapchain.ViewFrameEndAsync( view );

	//On the web nothing is ever waited for, so nothing is ever busy.
	internal static int ViewFrameReady( ulong view )
		=> web ? 1 : VkSwapchain.ViewFrameReady( view );

	internal static ulong ViewTexture( ulong view )
		=> web ? BrowserSwapchain.ViewTexture( view ) : VkSwapchain.ViewTexture( view );

	internal static int ViewRead( ulong view, byte* outPixels, int capacity, int layer = 0 )
		=> web ? 0 : VkSwapchain.ViewRead( view, outPixels, capacity, layer );

	internal static int ViewReadLayered( ulong view, byte* outPixels, int capacity )
		=> web ? 0 : VkSwapchain.ViewReadLayered( view, outPixels, capacity );

	//Opens a frame picture for the next draws; the portable frame only has the sky table
	internal static int TargetBegin( int target, float r, float g, float b, float a, int clear )
		=> Portable && target != VkDevice.TargetSkyTable ? 0
			: web ? BrowserSwapchain.TargetBegin( target, r, g, b, a, clear )
			: VkFrame.TargetBegin( target, r, g, b, a, clear );

	//-- the frame's pictures - see VkPictures; the browser's are one level, drawn into without depth --------

	//The portable frame has no compute to store into a picture, so it is never handed one to store into
	internal static ulong PictureAcquire( int kind, int width, int height, int levels, int storage )
		=> Portable && storage != 0 ? 0
			: web ? BrowserSwapchain.PictureAcquire( kind, width, height, levels )
			: VkPictures.Acquire( kind, width, height, levels, storage != 0 );

	internal static void PictureRelease( ulong picture )
	{
		if ( web ) BrowserSwapchain.PictureRelease( picture );
		else VkPictures.Release( picture );
	}

	internal static int PictureShape( ulong picture, out int width, out int height, out int levels, out int format )
	{
		width = height = levels = format = 0;
		return web ? BrowserSwapchain.PictureShape( picture, out width, out height, out levels, out format )
			: VkPictures.Shape( picture, out width, out height, out levels, out format );
	}

	internal static ulong PictureTexture( ulong picture )
		=> web ? BrowserSwapchain.PictureTexture( picture ) : VkPictures.TextureOf( picture );

	internal static int PictureBegin( ulong picture, int level, float r, float g, float b, float a, int clear, int depth )
		=> web ? BrowserSwapchain.PictureBegin( picture, level, r, g, b, a, clear, depth )
			: VkFrame.PictureBegin( picture, level, r, g, b, a, clear, depth );

	//The world's pass into a picture of the screen's own format and size, or back onto the screen - its texture and size, or nought
	internal static ulong WorldAside( int on, out int width, out int height )
		=> web ? BrowserSwapchain.WorldAside( on, out width, out height ) : VkFrame.WorldAside( on, out width, out height );

	internal static int PictureChain( ulong picture, ulong fold )
		=> Portable ? 0 : VkFrame.PictureChain( picture, fold );

	//How many pipelines the driver baked this run, and the milliseconds it spent - see VkPipelines.Baked.
	internal static (int Count, double Ms) PipelinesBaked => web ? ( 0, 0.0 ) : ( VkPipelines.Baked, VkPipelines.BakedMs );

	internal static ulong PassCreatePipeline( byte* code, int byteCount )
		=> Portable ? 0 : VkPipelines.PassCreatePipeline( code, byteCount );

	internal static int PassDispatch( ulong pipeline, ulong material, ulong* outputs, int* levels, int outputCount,
		ulong* buffers, int bufferCount, byte* push, int pushSize, int groupsX, int groupsY, int groupsZ )
		=> Portable ? 0 : VkFrame.PassDispatch( pipeline, material, outputs, levels, outputCount,
			buffers, bufferCount, push, pushSize, groupsX, groupsY, groupsZ );

	internal static void TargetEnd()
	{
		if ( web ) BrowserSwapchain.TargetEnd();
		else VkFrame.TargetEnd();
	}

	//A drawer's shadow map - see VkTextures.ShadowMapCreate and BrowserSwapchain.ShadowMapCreate.
	internal static ulong ShadowMapCreate( int side, int layers, bool readDepth )
		=> web ? BrowserSwapchain.ShadowMapCreate( side, layers ) : VkTextures.ShadowMapCreate( side, layers, readDepth );

	internal static void ShadowMapDestroy( ulong map )
	{
		if ( web ) BrowserSwapchain.ShadowMapDestroy( map );
		else VkTextures.ShadowMapDestroy( map );
	}

	//The portable frame's shadow maps, which its frame set binds in their own places - see CoreRenderDevice.PortableShadowMap
	internal static void PortableShadowMap( int slot, ulong map )
	{
		if ( web ) BrowserSwapchain.PortableShadowMap( slot, map );
		else VkDevice.PortableShadowMap( slot, map );
	}

	internal static uint ShadowMapIndex( ulong map, bool depth )
		=> Portable ? 0 : VkTextures.ShadowMapIndex( map, depth );

	//One shadow map layer as the target - see VkFrame.ShadowBegin; the portable frame has only the sun's
	internal static int ShadowBegin( ulong map, int layer )
		=> web ? BrowserSwapchain.ShadowBegin( map, layer ) : VkFrame.ShadowBegin( map, layer );

	//Narrows the open shadow area's draws, clearing first where asked - see VkFrame.ShadowScissor
	internal static void ShadowScissor( int x, int y, int width, int height, int clear )
	{
		if ( web ) BrowserSwapchain.ShadowScissor( x, y, width, height, clear );
		else VkFrame.ShadowScissor( x, y, width, height, clear );
	}

	internal static void ShadowArea( int x, int y, int width, int height, int clear )
	{
		if ( web ) BrowserSwapchain.ShadowArea( x, y, width, height, clear );
		else VkFrame.ShadowArea( x, y, width, height, clear );
	}


	//Draws left out so far because their pipeline was not ready yet - a page's are baked by the browser
	//off the frame; a desktop's never wait. Only ever grows: compare it before and after a pass.
	internal static long DrawsWaiting => web ? BrowserSwapchain.DrawsWaiting : 0;

	internal static int DepthReadable()
		=> Portable ? 0 : VkFrame.DepthReadable();

	//One of the frame's pictures into the layout a shader reads it in - see VkFrame.ReadyToRead.
	internal static int ReadyToRead( ulong texture )
		=> Portable ? 0 : VkFrame.ReadyToRead( texture );

	internal static int PictureMips( int target )
		=> Portable ? 0 : VkFrame.PictureMips( target );

	internal static ulong TargetTexture( ulong swapchain, int target, int which )
		=> Portable ? 0 : VkSwapchain.TargetTexture( swapchain, target, which );

	internal static int BackdropCapture()
		=> Portable ? 0 : VkFrame.BackdropCapture();

	internal static int GenerationCapture( ulong picture )
		=> Portable ? 0 : VkFrame.GenerationCapture( picture );

	internal static int ScreenInterrupt()
		=> Portable ? 0 : VkFrame.ScreenInterrupt();

	internal static int ScreenResume()
		=> Portable ? 0 : VkFrame.ScreenResume();

	internal static void WorldShades( int surfaces )
	{
		if ( !Portable ) VkFrame.WorldShades( surfaces != 0 );
	}

	internal static void WorldBack( in System.Numerics.Matrix4x4 clipToPrevious )
	{
		if ( !Portable ) VkFrame.AdaptiveBack = clipToPrevious;
	}

	//The near end of the depth range the next draws are squeezed into - see VkFrame.SetDepthCeiling.
	internal static void SetDepthCeiling( float ceiling )
	{
		if ( !Portable ) VkFrame.SetDepthCeiling( ceiling );
	}


	internal static int SwapchainWorldRect( ulong swapchain, int x, int y, int width, int height )
		=> Portable ? 0 : VkSwapchain.WorldRect( swapchain, x, y, width, height );

	internal static ulong BackdropTexture()
		=> Portable ? 0 : VkFrame.BackdropTexture();

	//A page reads BC where its device does and unpacks where not - see BrowserTextures.CreateMips
	internal static int SupportsBc()
		=> web ? ( BrowserDevice.BlockCompression ? 1 : 0 ) : VkDevice.SupportsBc();

	internal static int BindlessSlots()
		=> web ? 0 : VkDevice.BindlessSlotCount();

	internal static int DrawIndirectCount()
		=> Portable ? 0 : VkDevice.DrawIndirectCountAllowed();

	internal static ulong ComputeCreate( byte* code, int byteCount )
		=> Portable ? 0 : VkPipelines.ComputeCreate( code, byteCount );

	internal static ulong PyramidCreate( int width, int height )
		=> Portable ? 0 : VkTextures.PyramidCreate( width, height );

	internal static void PyramidDestroy( ulong pyramid )
	{
		if ( !Portable ) VkTextures.PyramidDestroy( pyramid );
	}

	internal static ulong PyramidCreatePipeline( byte* code, int byteCount )
		=> Portable ? 0 : VkPipelines.PyramidCreatePipeline( code, byteCount );

	//The fold a picture's chain of halvings is filled by - see VkFrame.PictureChain.
	internal static ulong FoldCreatePipeline( byte* code, int byteCount )
		=> Portable ? 0 : VkPipelines.FoldCreatePipeline( code, byteCount );

	//The smaller format for light past white, as a shader names it - see VkDevice.CompactFormat.
	internal static string CompactStorageName() => web ? "rgba16f" : VkDevice.CompactStorageName;

	//The same as the Vulkan format's number.
	internal static int CompactFormat() => web ? 97 : (int)VkDevice.CompactFormat;


	internal static ulong SkyTableTexture()
		=> Portable ? 0 : VkDevice.SkyTableTexture();

	//A panorama's table in place of the drawn one; the page copies it over the table its frame groups hold
	internal static void UseSkyTable( ulong texture )
	{
		if ( web ) BrowserSwapchain.UseSkyTable( texture );
		else VkTextures.portableSkyTable = texture;
	}

	internal static int PyramidBuild( ulong pipeline, ulong pyramid, int lod, int count,
		ulong source, byte* pushData, int pushSize, ulong argsBuffer = 0, int argsOffset = 0 )
		=> Portable ? 0 : VkFrame.PyramidBuild( pipeline, pyramid, lod, count, source, pushData, pushSize, argsBuffer, argsOffset );

	internal static int ComputeDispatch( ulong pipeline, ulong* buffers, int bufferCount,
		ulong texture, byte* pushData, int pushSize, int groupsX, int groupsY, int groupsZ )
		=> Portable ? 0 : VkFrame.ComputeDispatch( pipeline, buffers, bufferCount, texture,
			pushData, pushSize, groupsX, groupsY, groupsZ );

	internal static int DrawObjects( ulong pipeline, int vertexCount, byte* push, int pushSize,
		ulong texture, ulong* buffers, int bufferCount )
		=> Portable ? 0 : VkFrame.DrawObjects( pipeline, vertexCount, push, pushSize, texture, buffers, bufferCount );

	internal static int DrawObjectsIndexed( ulong pipeline, ulong indexBuffer, int indexCount,
		int instanceCount, bool wideIndices, byte* push, int pushSize,
		ulong texture, ulong* buffers, int bufferCount )
		=> Portable ? 0 : VkFrame.DrawObjectsIndexed( pipeline, indexBuffer, indexCount, instanceCount,
			wideIndices, push, pushSize, texture, buffers, bufferCount );

	//Instances out of a slot of the frame's bone ring - see VkFrame.DrawBlockInstanced.
	//Both frames have the ring, so this is who draws and not what the frame holds.
	internal static int DrawBlockInstanced( ulong pipeline, ulong indexBuffer, int indexCount,
		int instanceCount, bool wideIndices, ulong texture, uint blockOffset, byte* pushData, int pushSize )
		=> web
			? BrowserSwapchain.DrawBlockInstanced( pipeline, indexBuffer, indexCount, instanceCount,
				wideIndices, texture, blockOffset, pushData, pushSize )
			: VkFrame.DrawBlockInstanced( pipeline, indexBuffer, indexCount, instanceCount,
				wideIndices, texture, blockOffset, pushData, pushSize );

	internal static int FrameWrite( ulong buffer, long offset, byte* data, long size )
		=> Portable ? 0 : VkFrame.FrameWrite( buffer, offset, data, size );

	//New numbers into materials already made, in the frame being recorded - see
	//CoreRenderDevice.WriteMaterialNumbers. Nought in written for each one that could not be written in place.
	internal static void MaterialNumbers( ulong* materials, float* numbers, int count, byte* written )
	{
		if ( web ) BrowserTextures.MaterialNumbers( materials, numbers, count, written );
		else if ( Portable ) new Span<byte>( written, count ).Clear();
		else VkFrame.MaterialNumbers( materials, numbers, count, written );
	}

	internal static int DrawPulledIndirect( ulong pipeline, ulong argsBuffer, int argsOffset,
		int drawCount, ulong texture, byte* push, int pushSize, ulong* buffers, int bufferCount )
		=> Portable ? 0 : VkFrame.DrawPulledIndirect( pipeline, argsBuffer, argsOffset, drawCount, texture,
			push, pushSize, buffers, bufferCount );

	internal static int DrawMeshIndirect( ulong pipeline, ulong argsBuffer, int argsOffset,
		ulong texture, byte* push, int pushSize, ulong* buffers, int bufferCount )
		=> Portable ? 0 : VkFrame.DrawMeshIndirect( pipeline, argsBuffer, argsOffset, texture,
			push, pushSize, buffers, bufferCount );

	//Whether the device draws mesh stages - see VkDevice.meshShaders.
	internal static bool MeshShaders => !Portable && VkDevice.meshShaders;

	internal static int DrawPulledIndexedIndirect( ulong pipeline, ulong argsBuffer, int argsOffset, ulong indexBuffer,
		ulong texture, byte* push, int pushSize, ulong* buffers, int bufferCount )
		=> Portable ? 0 : VkFrame.DrawPulledIndexedIndirect( pipeline, argsBuffer, argsOffset, indexBuffer, texture,
			push, pushSize, buffers, bufferCount );

	//Whether an index may be any 32-bit number - see VkDevice.fullIndex.
	internal static int FullIndex()
		=> web ? 0 : VkDevice.FullIndexAllowed();

	//Whether an indirect draw may name where its instances start.
	internal static int FirstInstance()
		=> Portable ? 0 : VkDevice.FirstInstanceAllowed();

	internal static int ComputeDispatchIndirect( ulong pipeline, ulong* buffers, int bufferCount,
		ulong texture, byte* pushData, int pushSize, ulong argsBuffer, int argsOffset )
		=> Portable ? 0 : VkFrame.ComputeDispatchIndirect( pipeline, buffers, bufferCount, texture,
			pushData, pushSize, argsBuffer, argsOffset );

	internal static int BufferRead( ulong buffer, byte* outBytes, int capacity )
		=> web ? 0 : VkBuffers.Read( buffer, outBytes, capacity );

	//A page reads a buffer back through a promise, and a frame of its is never waited for - so nothing on this
	//side of it reads, fills or copies a buffer that is not an upload. See CoreRenderDevice.ReadBufferAsync.
	internal static int BufferReadAt( ulong buffer, long offset, byte* outBytes, int capacity )
		=> web ? 0 : VkBuffers.Read( buffer, offset, outBytes, capacity );

	internal static int BufferFill( ulong buffer, long offset, long size, uint value )
		=> web ? 0 : VkBuffers.Fill( buffer, offset, size, value );

	internal static int FrameFill( ulong buffer, long offset, long size, uint value )
		=> Portable ? 0 : VkFrame.FrameFill( buffer, offset, size, value );

	internal static int FrameCopy( ulong from, long fromOffset, ulong into, long intoOffset, long size )
		=> Portable ? 0 : VkFrame.FrameCopy( from, fromOffset, into, intoOffset, size );

	internal static int ReadbackAsk( ulong buffer, long offset, long size )
		=> web ? 0 : VkReadbacks.Ask( buffer, offset, size );

	internal static int ReadbackPoll( int id, out byte* data, out int size, out bool recorded )
	{
		data = null;
		size = 0;
		recorded = false;

		return web ? -1 : VkReadbacks.Poll( id, out data, out size, out recorded );
	}

	internal static void ReadbackRelease( int id )
	{
		if ( !web ) VkReadbacks.Release( id );
	}

	//A draw out of a project's own buffers - see VkFrame.DrawBuffers. The portable frame draws the ones that bind their
	//vertices; a draw whose shader reads a buffer of its own, or takes its counts from one, belongs to the full frame.
	internal static int DrawBuffers( CoreBufferDraw* draw, byte* push, int pushSize, ulong* buffers, int bufferCount )
		=> web ? BrowserSwapchain.DrawBuffers( draw, push, pushSize, buffers, bufferCount )
			: Portable && ( bufferCount > 0 || draw->Arguments != 0 ) ? 0
			: VkFrame.DrawBuffers( draw, push, pushSize, buffers, bufferCount );

	internal static int TextureCopy( ulong from, int fromLevel, ulong into, int intoLevel )
		=> web ? 0 : VkTextures.CopyTexture( from, fromLevel, into, intoLevel );

	internal static int TextureMips( ulong texture, int first, int count )
		=> web ? 0 : VkTextures.GenerateMips( texture, first, count );

	internal static int TextureClear( ulong texture, float r, float g, float b, float a )
		=> web ? 0 : VkTextures.ClearTexture( texture, r, g, b, a );

	internal static int PictureToTexture( ulong picture, ulong texture )
		=> Portable ? 0 : VkFrame.PictureToTexture( picture, texture );

	//Whether a frame is being recorded right now, a window's or a view's.
	internal static int Recording()
		=> web ? 0 : VkSwapchain.Current is { Recording: true } ? 1 : 0;

	internal static int WaitIdle()
		=> web ? 0 : VkDevice.WaitIdle();

	internal static void WorldSize( int* outInfo )
	{
		if ( Portable )
		{
			outInfo[0] = 0;
			outInfo[1] = 0;
			return;
		}

		VkSwapchain.WorldSize( outInfo );
	}

	//The web backend uploads through its own path, which has no limit to set.
	internal static void TextureLimit( int maxEdge )
	{
		if ( !web ) VkDevice.TextureLimit( maxEdge );
	}

	//And makes its own samplers, which this cannot reach.
	internal static int Anisotropy( float taps )
		=> web ? 0 : VkDevice.Anisotropy( taps );

	internal static int DrawDynamic( ulong pipeline,
		byte* vertexData, int vertexCount, int vertexStride,
		ulong texture, byte* pushData, int pushSize )
		=> web
			? BrowserSwapchain.DrawDynamic( pipeline, vertexData, vertexCount, vertexStride,
				texture, pushData, pushSize )
			: VkFrame.DrawDynamic( pipeline, vertexData, vertexCount, vertexStride,
				texture, pushData, pushSize );

	internal static void PipelineDestroy( ulong pipeline )
	{
		if ( web ) BrowserPipelines.Destroy( pipeline );
		else VkPipelines.Destroy( pipeline );
	}

	internal static int Draw( ulong pipeline, int vertexCount, void* push, int pushSize, ulong texture )
		=> web ? BrowserSwapchain.Draw( pipeline, vertexCount, push, pushSize, texture )
			: VkFrame.Draw( pipeline, vertexCount, push, pushSize, texture );

	internal static ulong BufferCreate( byte* data, long size )
		=> web ? BrowserBuffers.Create( data, size ) : VkBuffers.Create( data, size );

	internal static void BufferDestroy( ulong buffer )
	{
		if ( web ) BrowserBuffers.Destroy( buffer );
		else VkBuffers.Destroy( buffer );
	}

	internal static int BufferWrite( ulong buffer, long offset, byte* data, long size )
		=> web ? BrowserBuffers.Write( buffer, offset, data, size ) : VkBuffers.Write( buffer, offset, data, size );

	internal static ulong BufferReserve( long size )
		=> web ? BrowserBuffers.Reserve( size ) : VkBuffers.Reserve( size );

	internal static int BufferCopy( ulong from, long fromOffset, ulong into, long intoOffset, long size )
		=> web ? BrowserBuffers.Copy( from, fromOffset, into, intoOffset, size )
			: VkBuffers.Copy( from, fromOffset, into, intoOffset, size );

	//A run of copies of one mesh, their numbers in a ring slot - see VkFrame.DrawIndexedInstancedBlock.
	internal static int DrawIndexedInstancedBlock( ulong pipeline, ulong vertexBuffer, ulong indexBuffer,
		int indexCount, int instanceCount, uint block, int firstIndex, int firstVertex,
		ulong texture, byte* pushData, int pushSize )
		=> web
			? BrowserSwapchain.DrawIndexedInstancedBlock( pipeline, vertexBuffer, indexBuffer,
				indexCount, instanceCount, block, firstIndex, firstVertex, texture, pushData, pushSize )
			: VkFrame.DrawIndexedInstancedBlock( pipeline, vertexBuffer, indexBuffer,
				indexCount, instanceCount, block, firstIndex, firstVertex, texture, pushData, pushSize );

	internal static int DrawIndexed( ulong pipeline,
		ulong vertexBuffer, ulong indexBuffer, int firstIndex, int indexCount, int firstVertex,
		ulong texture, byte* pushData, int pushSize )
		=> web
			? BrowserSwapchain.DrawIndexed( pipeline, vertexBuffer, indexBuffer,
				firstIndex, indexCount, firstVertex, texture, pushData, pushSize )
			: VkFrame.DrawIndexed( pipeline, vertexBuffer, indexBuffer,
				firstIndex, indexCount, firstVertex, texture, pushData, pushSize );

	//What a window last presented, red first - see VkSwapchain.WindowRead.
	internal static int WindowRead( ulong handle, byte* outPixels, int capacity )
		=> web ? 0 : VkSwapchain.WindowRead( handle, outPixels, capacity );

	internal static (int Width, int Height) WindowReadSize( ulong handle )
		=> web ? (0, 0) : VkSwapchain.WindowReadSize( handle );

	//Whether the window keeps a host copy of what it presents.
	internal static bool KeepsWindowCopy
	{
		get => !web && VkSwapchain.Swapchain.WantsRead;
		set
		{
			if ( web ) return;

			var was = VkSwapchain.Swapchain.WantsRead;
			VkSwapchain.Swapchain.WantsRead = value;

			//And made now rather than at the next resize - see FitWindowReads.
			if ( was != value ) VkSwapchain.FitWindowReads();
		}
	}

	//Whether every window keeps copies of what it presents for a recording of the screen - see
	//VkSwapchain.Swapchain.Captures. Never on the web, which has no way to read its canvas back that does not stall it.
	internal static bool KeepsWindowCaptures
	{
		get => !web && VkSwapchain.Swapchain.WantsCapture;
		set
		{
			if ( web ) return;

			var was = VkSwapchain.Swapchain.WantsCapture;
			VkSwapchain.Swapchain.WantsCapture = value;

			if ( was != value ) VkSwapchain.FitAllCaptures();
		}
	}

	//The newest finished copy of what a window presented, red or blue first as outInfo[2]
	//says; nought where there is nothing new. See VkSwapchain.WindowCaptureTake.
	internal static long WindowCaptureTake( ulong handle, byte* outPixels, int capacity, int* outInfo )
		=> web ? 0 : VkSwapchain.WindowCaptureTake( handle, outPixels, capacity, outInfo );

	internal static (int Width, int Height) WindowCaptureSize( ulong handle )
		=> web ? (0, 0) : VkSwapchain.WindowCaptureSize( handle );

	internal static int DepthOnScreenPossible( ulong viewport )
		=> Portable ? 0 : VkSwapchain.DepthOnScreenPossible( viewport );

	internal static void DepthOnScreen( ulong viewport, bool wanted )
	{
		if ( !Portable ) VkSwapchain.DepthOnScreen( viewport, wanted );
	}

	internal static (int X, int Y, int Wide, int Tall) DepthReadRect( ulong viewport )
		=> Portable ? (0, 0, 0, 0) : VkSwapchain.DepthReadRect( viewport );

	internal static uint TextureIndex( ulong texture )
		=> web ? BrowserTextures.IndexGet( texture ) : VkTextures.TextureIndex( texture );


	internal static int DrawPulled( ulong pipeline,
		ulong geometryBuffer, ulong dataBuffer, int vertexCount,
		ulong texture, byte* pushData, int pushSize )
		=> web
			? BrowserSwapchain.DrawPulled( pipeline, geometryBuffer, dataBuffer, vertexCount,
				texture, pushData, pushSize )
			: VkFrame.DrawPulled( pipeline, geometryBuffer, dataBuffer, vertexCount,
				texture, pushData, pushSize );

	internal static int DrawSkinned( ulong pipeline,
		ulong vertexBuffer, ulong skinBuffer, ulong indexBuffer,
		int firstIndex, int indexCount, int firstVertex,
		ulong texture, float* bones, int boneCount,
		byte* pushData, int pushSize )
		=> web
			? BrowserSwapchain.DrawSkinned( pipeline, vertexBuffer, skinBuffer, indexBuffer,
				firstIndex, indexCount, firstVertex, texture, bones, boneCount, pushData, pushSize )
			: VkFrame.DrawSkinned( pipeline, vertexBuffer, skinBuffer, indexBuffer,
				firstIndex, indexCount, firstVertex, texture, bones, boneCount, pushData, pushSize );

	internal static uint Bones( float* bones, int boneCount )
		=> web ? BrowserSwapchain.Bones( bones, boneCount ) : VkFrame.Bones( bones, boneCount );

	internal static int DrawSkinnedAt( ulong pipeline,
		ulong vertexBuffer, ulong skinBuffer, ulong indexBuffer,
		int firstIndex, int indexCount, int firstVertex,
		ulong texture, uint boneOffset,
		byte* pushData, int pushSize )
		=> web
			? BrowserSwapchain.DrawSkinnedAt( pipeline, vertexBuffer, skinBuffer, indexBuffer,
				firstIndex, indexCount, firstVertex, texture, boneOffset, pushData, pushSize )
			: VkFrame.DrawSkinnedAt( pipeline, vertexBuffer, skinBuffer, indexBuffer,
				firstIndex, indexCount, firstVertex, texture, boneOffset, pushData, pushSize );

	internal static ulong TextureCreateMips( int width, int height, int format,
		int srgb, int mipCount, int* mipInfo, byte* pixels, int byteCount )
		=> web ? BrowserTextures.CreateMips( width, height, format, srgb, mipCount, mipInfo, pixels, byteCount )
			: VkTextures.CreateMips( width, height, format, srgb, mipCount, mipInfo, pixels, byteCount );

	//Whether this device takes Basis textures: a page, or Vulkan without BC; RINTEN_KTX2=1 forces it
	internal static bool TakesUniversal => web || NativeUniversal;

	private static readonly bool universalTried = Environment.GetEnvironmentVariable( "RINTEN_KTX2" ) == "1";

	private static bool NativeUniversal
		=> !web && ( universalTried || !VkDevice.blockCompression ) && CoreBasisTranscoder.Available;

	internal static ulong TextureCreateUniversal( CoreUniversalTexture picture )
		=> web ? BrowserTextures.CreateUniversal( picture ) : NativeUniversal ? VkTextures.CreateUniversal( picture ) : 0;

	internal static ulong TextureCreateRgba( byte* pixels, int width, int height, int srgb )
		=> web ? BrowserTextures.CreateRgba( pixels, width, height, srgb )
			: VkTextures.CreateRgba( pixels, width, height, srgb );

	internal static int TextureUpdateRgba( ulong texture, byte* pixels, int width, int height )
		=> web ? BrowserTextures.UpdateRgba( texture, pixels, width, height )
			: VkTextures.UpdateRgba( texture, pixels, width, height );

	//A rectangle of a texture, from rows stride bytes apart. The browser's device has
	//no such path and answers nought, which sends the caller back to the whole picture.
	internal static int TextureUpdateRgbaRegion( ulong texture, byte* pixels, int stride,
		int x, int y, int width, int height )
		=> web ? 0 : VkTextures.UpdateRgbaRegion( texture, pixels, stride, x, y, width, height, false );

	//A rectangle of one level of a texture, in the texture's own format - see VkTextures.WriteLevel.
	internal static int TextureWriteLevel( ulong texture, int level, int x, int y, int width, int height, byte* data, int stride )
		=> web ? BrowserTextures.WriteLevel( texture, level, x, y, width, height, data, stride )
			: VkTextures.WriteLevel( texture, level, x, y, width, height, data, stride );

	//A texture of nothing, every level there - see VkTextures.CreateBlank.
	internal static ulong TextureCreateBlank( int width, int height, int format, int srgb, int mipCount )
		=> web ? BrowserTextures.CreateBlank( width, height, format, srgb, mipCount )
			: VkTextures.CreateBlank( width, height, format, srgb, mipCount );

	//The pictures a material was made of, colour, normal, third and emission.
	internal static void MaterialParts( ulong material, Span<ulong> into )
	{
		if ( web ) BrowserTextures.MaterialParts( material, into );
		else VkTextures.MaterialParts( material, into );
	}

	//How many levels a picture has on the device and their bytes.
	internal static bool TextureHeld( ulong texture, out int levels, out long bytes )
		=> web ? BrowserTextures.Held( texture, out levels, out bytes ) : VkTextures.Held( texture, out levels, out bytes );

	//Whether a picture's levels are on the device yet: one once they are, minus one where they never will be,
	//nought while they are on their way - a page's transcode, see BrowserTextures.Ready. Always there on Vulkan.
	internal static int TextureReady( ulong texture ) => web ? BrowserTextures.Ready( texture ) : 1;

	//Each target given the source's image - see VkTextures.Swap and BrowserTextures.Swap.
	internal static int TextureSwap( ReadOnlySpan<(ulong Target, ulong Source)> swaps )
		=> web ? BrowserTextures.Swap( swaps ) : VkTextures.Swap( swaps );

	internal static void TextureDestroy( ulong texture )
	{
		ForgetPortableSlot( texture );

		if ( web ) BrowserTextures.Destroy( texture );
		else VkTextures.Destroy( texture );
	}

	//A texture number for the portable frame, which has no table; numbers are never reused
	internal static uint PortableSlot( ulong texture )
	{
		if ( texture == 0 ) return 0;

		lock ( portableSlots )
		{
			if ( portableSlots.TryGetValue( texture, out var slot ) ) return slot;

			slot = (uint)portableCount;

			if ( portableCount == portablePictures.Length )
			{
				var room = new ulong[portablePictures.Length * 2];
				Array.Copy( portablePictures, room, portableCount );

				//Published only once filled, so a reader sees the old array or the full new one
				System.Threading.Volatile.Write( ref portablePictures, room );
			}

			//The number is handed out after the picture is in, so nothing can name a slot this has not written yet.
			portablePictures[portableCount] = texture;
			portableCount++;

			portableSlots[texture] = slot;
			return slot;
		}
	}

	//Texture for a PortableSlot number, or 0; lock-free since the table only grows and is swapped whole
	internal static ulong PortableTexture( int slot )
	{
		var pictures = System.Threading.Volatile.Read( ref portablePictures );

		return slot > 0 && slot < pictures.Length ? pictures[slot] : 0;
	}

	private static void ForgetPortableSlot( ulong texture )
	{
		lock ( portableSlots )
		{
			if ( portableSlots.Remove( texture, out var slot ) ) portablePictures[(int)slot] = 0;
		}
	}

	private static readonly Dictionary<ulong, uint> portableSlots = new();

	//The pictures the numbers name - slot nought is no picture, as it is in the table. Grown
	//by copying rather than in place, so that PortableTexture may read it without the lock.
	private static ulong[] portablePictures = new ulong[16];

	//How many of them are filled. Written under the lock, read there alone.
	private static int portableCount = 1;

	internal static ulong MaterialCreate( ulong colour, ulong normal,
		ulong third, ulong emission, float* parameters )
		=> web ? BrowserTextures.MaterialCreate( colour, normal, third, emission, parameters )
			: VkTextures.MaterialCreate( colour, normal, third, emission, parameters );

	//Whether the device shades the world coarser where the last frame was smooth - see Rendering.AdaptiveShading.
	internal static bool AdaptiveShadingPossible => !web && !Portable && VkFovea.Adaptive;

	//A compute pipeline reading the picture table at set 1 - see VkPipelines.ComputeCreateOverTable.
	internal static unsafe ulong ComputeCreateOverTable( byte* code, int byteCount )
		=> Portable ? 0 : VkPipelines.ComputeCreateOverTable( code, byteCount );

	//The compute pipeline that lays the rates - see VkFrame.Adaptive. Nought takes it away.
	internal static void AdaptiveShadingPipeline( ulong pipeline )
	{
		if ( !web && !Portable ) VkFrame.AdaptivePipeline = pipeline;
	}

	//Whether this device draws blend shapes; WebGL2 lacks storage buffers, so a page draws the rest shape
	internal static bool DrawsMorphs => !web;

	internal static unsafe uint MorphWeights( float* weights, int count )
		=> web ? 0 : VkFrame.MorphWeights( weights, count );

	internal static unsafe int DrawMorphedIndexed( ulong pipeline,
		ulong vertexBuffer, ulong runBuffer, ulong deltaBuffer, ulong indexBuffer,
		int firstIndex, int indexCount, int firstVertex, int firstRun,
		ulong texture, uint morphOffset,
		byte* pushData, int pushSize )
		=> web ? 0 : VkFrame.DrawMorphedIndexed( pipeline, vertexBuffer, runBuffer,
			deltaBuffer, indexBuffer, firstIndex, indexCount, firstVertex, firstRun,
			texture, morphOffset, pushData, pushSize );

	internal static unsafe int DrawMorphedAt( ulong pipeline,
		ulong vertexBuffer, ulong skinBuffer, ulong runBuffer,
		ulong deltaBuffer, ulong indexBuffer,
		int firstIndex, int indexCount, int firstVertex, int firstRun,
		ulong texture, uint boneOffset, uint morphOffset,
		byte* pushData, int pushSize )
		=> web ? 0 : VkFrame.DrawMorphedAt( pipeline, vertexBuffer, skinBuffer, runBuffer,
			deltaBuffer, indexBuffer, firstIndex, indexCount, firstVertex, firstRun,
			texture, boneOffset, morphOffset, pushData, pushSize );

	static byte[] infoUtf8 = [0];
	static byte[] nameUtf8 = [0];
	static GCHandle infoPin, namePin;

	static IntPtr PinText( ref byte[] buffer, ref GCHandle pin, string text )
	{
		var bytes = System.Text.Encoding.UTF8.GetBytes( (text ?? "") + "\0" );
		if ( pin.IsAllocated ) pin.Free();
		buffer = bytes;
		pin = GCHandle.Alloc( buffer, GCHandleType.Pinned );
		return pin.AddrOfPinnedObject();
	}
}
