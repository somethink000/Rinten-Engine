using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Rinten;

//Browser graphics setup over WebGPU or WebGL2 (see BrowserNative), shaped like the Vulkan side
internal static class BrowserDevice
{
	//-- WebGPU flags as numbers: GPUBufferUsage, GPUTextureUsage and GPUShaderStage bits, kept in one place --

	internal static class BufferUse
	{
		internal const int CopySrc = 4;
		internal const int CopyDst = 8;
		internal const int Index = 16;
		internal const int Vertex = 32;
		internal const int Uniform = 64;
		internal const int Storage = 128;
	}

	internal static class TextureUse
	{
		internal const int CopySrc = 1;
		internal const int CopyDst = 2;
		internal const int TextureBinding = 4;
		internal const int RenderAttachment = 16;

		/// <summary>Not WebGPU's: read as layers even with one, as a shadow map is - see webgl2.js.</summary>
		internal const int Layered = 1 << 20;
	}

	private static class Stage
	{
		internal const int Vertex = 1;
		internal const int Fragment = 2;
		internal const int VertexFragment = Vertex | Fragment;
	}

	//-- the formats this backend names ------------------------------------------

	internal const string FormatRgba8 = "rgba8unorm";
	internal const string FormatRgba8Srgb = "rgba8unorm-srgb";
	internal const string FormatRgba16Float = "rgba16float";

	//An HDR picture's light; core WebGPU, filterable - see CoreTextureFile.TextureFormat.Rgb9e5
	internal const string FormatRgb9e5 = "rgb9e5ufloat";
	internal const string FormatDepth32 = "depth32float";

	internal const string AspectAll = "all";
	internal const string AspectDepth = "depth-only";

	//-- the sizes the whole backend is cut to -----------------------------------

	internal const int PushConstantSize = 128;
	internal const int PushAlign = 256;
	internal const int PushSlotsPerFrame = 4096;
	internal const int MaxBones = 128;
	internal const int BoneBlockSize = MaxBones * 64;

	//How many skinned poses one frame holds - each a whole block, bound at its own offset - and runs
	//of sprites, which ride in the same slots. A frame that runs out draws the rest in no pose at all.
	internal const int BoneSlotsPerFrame = 256;
	internal const int MaxLights = 16;

	//Frame block size, taken from CoreSceneRenderer's layout so it always matches the shaders
	internal const int FrameBlockSize = CoreSceneRenderer.FrameFloats * sizeof( float );

	internal const int MaterialTextures = 4;

	//Where the push ring and the two samplers sit in the groups below. The shader translators say the same -
	//see CoreShaderCompiler.Wgsl and .Glsl - since neither WGSL nor GLSL from Slang puts them there on its own.
	internal const int PushBinding = 6;
	internal const int MaterialSamplerBinding = 5;
	internal const int ShadowSamplerBinding = 3;

	//Binding of the plain filtering sampler in the frame group; see OneSamplerAGroup
	internal const int FilterSamplerBinding = 4;
	//A material's numbers: every slot a PARAMETERS block can ask for - see CoreRenderDevice.MaterialParamSlots
	internal const int MaterialParamSize = CoreRenderDevice.MaterialParamFloats * sizeof( float );
	internal const int FramesInFlight = 2;
	internal const int DynamicScratch = 4 * 1024 * 1024;

	//Which set of pictures a pass draws into. Matches SBC_TARGET_*.
	internal static class Target
	{
		internal const int Screen = 0;
		internal const int Scene = 1;
		internal const int Depth = 4;

		/// <summary>A frame picture: one colour attachment of its own format, no depth.</summary>
		internal const int Picture = 20;
	}

	//-- the device ---------------------------------------------------------------

	internal static bool validation;

	internal static int sampler;
	internal static int clampSampler;
	internal static int pointSampler;
	internal static int shadowSampler;

	internal static int textureSetLayout;
	internal static int boneSetLayout;
	internal static int objectSetLayout;

	internal static int shadowImage;
	internal static int shadowView;
	//The sky's table and the picture it lives in - see CoreSkyTable.
	internal static int skyTableImage;
	internal static int skyTableView;

	//How many texels across it is - see CoreSkyTable.Side, which fills it.
	internal const int SkyTableSide = 64;

	//Binding for the second eye's frame block in stereo passes; must match webgl2.js SecondFrameBinding
	internal const int SecondFrameBinding = 6;

	//The portable frame's bake, its sheet and its volume - see g_bakedSheet in frame.slang and CoreBakedLighting.Portable
	internal const int BakedSheetBinding = 8;
	internal const int BakedVolumeBinding = 9;

	//The portable frame's shadow maps by slot: sun pages twice, sun movers, lamps, lamp movers - see g_portablePages
	internal static readonly int[] ShadowBindings = [2, 10, 11, 12, 13];

	//What those two hold where there is no bake: one texel of nothing
	internal static int noBakeImage, noBakeView;

	internal static int defaultParams;
	internal static int pushRing;
	internal static int defaultMaterial;
	internal static int defaultBones;
	internal static int defaultObject;

	//Kept as long as the default groups: WebGPU destroys a released buffer, failing every submit bound to it
	private static int defaultBonesBuffer;
	private static int defaultFrameBuffer;
	private static int defaultObjectBuffer;

	//The world finishes into sRGB eight-bit, like a desktop window, since screen stages write linear light
	internal const string ScreenFormat = FormatRgba8Srgb;

	//The canvas's format: the same bytes without the curve, copied in as they are; every browser offers it
	internal const string CanvasFormat = FormatRgba8;

	//The language this device's shaders are in: WGSL on WebGPU, GLSL where the page had only WebGL2 - see webgl2.js.
	//Everything above the handle table is the same either way; only the words handed to a shader module differ.
	internal static CoreShaderCompiler.Format ShaderFormat { get; private set; } = CoreShaderCompiler.Format.Wgsl;
	internal static int maxImageSize = 8192;

	//Whether the device reads the BC formats a compiled texture is stored in. A desktop's
	//browser does; a phone's does not, and gets them unpacked - see BrowserTextures.CreateMips.
	internal static bool BlockCompression;

	//Whether the device reads ASTC 4x4 (headsets, phones) and ETC2 (every phone WebGL2), what a web build's
	//pictures become without BC - see BrowserTextures.CreateUniversal
	internal static bool AstcCompression;
	internal static bool Etc2Compression;

	internal static string infoText = "not initialised";
	internal static string deviceNameText = "";

	internal static long trackedBufferBytes;
	internal static long trackedTextureBytes;
	internal static int trackedBufferCount;
	internal static int trackedTextureCount;

	//Whether the device came up and has not been taken down again.
	internal static bool Ready => ready;

	private static bool ready;

	//Whether the browser said the device is gone; asked once a frame, so a draw reads a field, not JavaScript
	internal static bool Lost => lost;

	private static bool lost;

	//Asks the page whether the device is still there. Once a frame, at its start.
	internal static void RefreshLost()
	{
		if ( ready ) lost = BrowserNative.Lost() != 0;
	}

	//-- making things ------------------------------------------------------------

	//A buffer with bytes in it, or empty for an empty span; sized up to whole words, as WebGPU maps only those,
	//the tail of a mapped range being zero
	internal static int MakeBuffer( Span<byte> data, int usage )
	{
		if ( data.Length == 0 ) return 0;

		return BrowserNative.CreateBufferWithData( data, usage, null );
	}

	//A buffer of a size with nothing in it yet.
	internal static int MakeBuffer( long size, int usage )
	{
		if ( size <= 0 ) return 0;

		return BrowserNative.CreateBuffer( ( size + 3 ) & ~3L, usage, null );
	}

	internal static int MakeTexture( int width, int height, string format, int usage, int mips = 1, int layers = 1,
		int samples = 1 )
	{
		if ( width <= 0 || height <= 0 ) return 0;

		return BrowserNative.CreateTexture( width, height, mips <= 0 ? 1 : mips, format, usage, null,
			layers <= 0 ? 1 : layers, samples <= 1 ? 1 : samples );
	}

	internal static int MakeView( int texture, string format, string aspect = AspectAll )
	{
		if ( texture == 0 ) return 0;

		return BrowserNative.CreateView( texture, format, aspect, 0, 0 );
	}

	//What one entry of a bind group names, and by which kind of handle.
	internal readonly struct Bind
	{
		private readonly int binding;
		private readonly string kind;
		private readonly int handle;
		private readonly long size;

		private Bind( int binding, string kind, int handle, long size )
		{
			this.binding = binding;
			this.kind = kind;
			this.handle = handle;
			this.size = size;
		}

		internal static Bind Buffer( int binding, int buffer, long size ) => new( binding, "buffer", buffer, size );
		internal static Bind View( int binding, int view ) => new( binding, "textureView", view, 0 );
		internal static Bind Sampler( int binding, int sampler ) => new( binding, "sampler", sampler, 0 );

		internal void Write( StringBuilder json )
		{
			json.Append( "{\"binding\":" ).Append( binding ).Append( ",\"" ).Append( kind ).Append( "\":" )
				.Append( handle );
			if ( size > 0 ) json.Append( ",\"size\":" ).Append( size );
			json.Append( '}' );
		}
	}

	//A bind group naming resources by handle. Made when a texture, a material or a frame's
	//scratch is made and kept for as long as they are - nothing on the per-draw path builds one.
	internal static int MakeBindGroup( int layout, params Bind[] entries )
	{
		if ( layout == 0 ) return 0;

		var json = new StringBuilder( 256 );
		json.Append( "{\"layout\":" ).Append( layout ).Append( ",\"entries\":[" );

		for ( var i = 0; i < entries.Length; i++ )
		{
			if ( i > 0 ) json.Append( ',' );
			entries[i].Write( json );
		}

		json.Append( "]}" );
		return BrowserNative.CreateBindGroup( json.ToString() );
	}

	private static int MakeSampler( string address, string filter, bool compare )
	{
		var json = new StringBuilder( 192 );
		json.Append( "{\"addressModeU\":\"" ).Append( address )
			.Append( "\",\"addressModeV\":\"" ).Append( address )
			.Append( "\",\"addressModeW\":\"" ).Append( address )
			.Append( "\",\"maxAnisotropy\":1" );

		if ( compare )
		{
			//Linear, as WebGPU and WebGL2 both allow on a comparison: each tap is the four texels' answers blended,
			//which the shadow lookup's few taps need - nearest gave them a staircase a texel a step
			json.Append( ",\"compare\":\"less-equal\",\"magFilter\":\"linear\",\"minFilter\":\"linear\"" )
				.Append( ",\"mipmapFilter\":\"nearest\"" );
		}
		else
		{
			json.Append( ",\"magFilter\":\"" ).Append( filter )
				.Append( "\",\"minFilter\":\"" ).Append( filter )
				.Append( "\",\"mipmapFilter\":\"linear\"" );
		}

		json.Append( '}' );
		return BrowserNative.CreateSampler( json.ToString() );
	}

	//-- bringing it up -----------------------------------------------------------

	//Group 0 entries, all visible to the vertex stage too, since foliage reads material data there
	private static readonly string TextureSetJson = $$$"""
		{"entries":[
		{"binding":0,"visibility":{{{Stage.VertexFragment}}},"texture":{"sampleType":"float","viewDimension":"2d"}},
		{"binding":1,"visibility":{{{Stage.VertexFragment}}},"texture":{"sampleType":"float","viewDimension":"2d"}},
		{"binding":2,"visibility":{{{Stage.VertexFragment}}},"texture":{"sampleType":"float","viewDimension":"2d"}},
		{"binding":3,"visibility":{{{Stage.VertexFragment}}},"texture":{"sampleType":"float","viewDimension":"2d"}},
		{"binding":4,"visibility":{{{Stage.VertexFragment}}},"buffer":{"type":"uniform","minBindingSize":{{{MaterialParamSize}}}}},
		{"binding":{{{MaterialSamplerBinding}}},"visibility":{{{Stage.VertexFragment}}},"sampler":{"type":"filtering"}},
		{"binding":{{{PushBinding}}},"visibility":{{{Stage.VertexFragment}}},"buffer":{"type":"uniform","minBindingSize":{{{PushConstantSize}}},"hasDynamicOffset":true}}
		]}
		""";

	//Group 1: bones or a sprite run, the frame block, the sun map and its compare sampler, a filtering sampler,
	//the sky table. Group 0 is the push ring, the one thing rebound between draws
	private static readonly string BoneSetJson = $$$"""
		{"entries":[
		{"binding":0,"visibility":{{{Stage.VertexFragment}}},"buffer":{"type":"read-only-storage","minBindingSize":{{{BoneBlockSize}}},"hasDynamicOffset":true}},
		{"binding":1,"visibility":{{{Stage.VertexFragment}}},"buffer":{"type":"read-only-storage","minBindingSize":{{{FrameBlockSize}}}}},
		{"binding":2,"visibility":{{{Stage.Fragment}}},"texture":{"sampleType":"depth","viewDimension":"2d-array"}},
		{"binding":10,"visibility":{{{Stage.Fragment}}},"texture":{"sampleType":"depth","viewDimension":"2d-array"}},
		{"binding":11,"visibility":{{{Stage.Fragment}}},"texture":{"sampleType":"depth","viewDimension":"2d-array"}},
		{"binding":12,"visibility":{{{Stage.Fragment}}},"texture":{"sampleType":"depth","viewDimension":"2d-array"}},
		{"binding":13,"visibility":{{{Stage.Fragment}}},"texture":{"sampleType":"depth","viewDimension":"2d-array"}},
		{"binding":{{{ShadowSamplerBinding}}},"visibility":{{{Stage.Fragment}}},"sampler":{"type":"comparison"}},
		{"binding":{{{FilterSamplerBinding}}},"visibility":{{{Stage.Fragment}}},"sampler":{"type":"filtering"}},
		{"binding":5,"visibility":{{{Stage.Fragment}}},"texture":{"sampleType":"float","viewDimension":"2d"}},
		{"binding":{{{SecondFrameBinding}}},"visibility":{{{Stage.VertexFragment}}},"buffer":{"type":"read-only-storage","minBindingSize":{{{FrameBlockSize}}}}},
		{"binding":{{{BakedSheetBinding}}},"visibility":{{{Stage.Fragment}}},"texture":{"sampleType":"float","viewDimension":"2d"}},
		{"binding":{{{BakedVolumeBinding}}},"visibility":{{{Stage.Fragment}}},"texture":{"sampleType":"float","viewDimension":"2d"}}
		]}
		""";

	//Group 2: an object's two buffers, with no minimum size, so world chunks and instance palettes share it
	private static readonly string ObjectSetJson = $$$"""
		{"entries":[
		{"binding":0,"visibility":{{{Stage.VertexFragment}}},"buffer":{"type":"read-only-storage"}},
		{"binding":1,"visibility":{{{Stage.VertexFragment}}},"buffer":{"type":"read-only-storage"}}
		]}
		""";

	private static bool CreateLayouts()
	{
		textureSetLayout = BrowserNative.CreateBindGroupLayout( TextureSetJson );
		boneSetLayout = BrowserNative.CreateBindGroupLayout( BoneSetJson );
		objectSetLayout = BrowserNative.CreateBindGroupLayout( ObjectSetJson );

		return textureSetLayout != 0 && boneSetLayout != 0 && objectSetLayout != 0;
	}

	private static bool CreateShadows()
	{
		const int usage = TextureUse.RenderAttachment | TextureUse.TextureBinding | TextureUse.Layered;

		//What the sun's map slots hold until there are maps - one texel, never read, as the frame block says nothing is whole
		shadowImage = MakeTexture( 1, 1, FormatDepth32, usage );

		//Cleared: alpha stays nought, meaning no table, until CoreSkyTable sends one - see SkyTableAt in frame.slang
		skyTableImage = MakeTexture( SkyTableSide, SkyTableSide, FormatRgba16Float,
			TextureUse.TextureBinding | TextureUse.CopyDst );

		if ( shadowImage == 0 || skyTableImage == 0 ) return false;

		shadowView = BrowserNative.CreateView( shadowImage, FormatDepth32, AspectDepth, 0, 0, 0, "2d-array" );
		skyTableView = MakeView( skyTableImage, FormatRgba16Float );

		var empty = new byte[SkyTableSide * SkyTableSide * 8];
		BrowserNative.WriteTexture( skyTableImage, 0, 0, 0, SkyTableSide, SkyTableSide, SkyTableSide * 8, empty );

		noBakeImage = MakeTexture( 1, 1, FormatRgba16Float, TextureUse.TextureBinding | TextureUse.CopyDst );
		noBakeView = noBakeImage != 0 ? MakeView( noBakeImage, FormatRgba16Float ) : 0;

		return shadowView != 0 && skyTableView != 0 && noBakeView != 0;
	}

	private static bool CreateDefaults()
	{
		Span<float> parameters = stackalloc float[CoreRenderDevice.MaterialParamFloats];
		parameters.Clear();
		parameters[0] = 0.5f;
		parameters[2] = 1.0f;
		defaultParams = MakeBuffer( MemoryMarshal.AsBytes( parameters ), BufferUse.Uniform );
		pushRing = MakeBuffer( (long)PushAlign * PushSlotsPerFrame * FramesInFlight,
			BufferUse.Uniform | BufferUse.CopyDst );

		var identity = new byte[BoneBlockSize];
		defaultBonesBuffer = MakeBuffer( identity, BufferUse.Storage );
		defaultFrameBuffer = MakeBuffer( FrameBlockSize, BufferUse.Storage | BufferUse.CopyDst );

		if ( defaultParams == 0 || pushRing == 0 || defaultBonesBuffer == 0 || defaultFrameBuffer == 0 ) return false;

		defaultBones = MakeBindGroup( boneSetLayout,
			Bind.Buffer( 0, defaultBonesBuffer, BoneBlockSize ),
			Bind.Buffer( 1, defaultFrameBuffer, FrameBlockSize ),
			Bind.View( 2, shadowView ),
			Bind.View( 10, shadowView ),
			Bind.View( 11, shadowView ),
			Bind.View( 12, shadowView ),
			Bind.View( 13, shadowView ),
			Bind.Sampler( ShadowSamplerBinding, shadowSampler ),
			Bind.Sampler( FilterSamplerBinding, clampSampler ),
			Bind.View( 5, skyTableView ),
			Bind.View( BakedSheetBinding, noBakeView ),
			Bind.View( BakedVolumeBinding, noBakeView ),

			//Every entry the layout has, the second eye's block included - with six the page
			//refused the group on every start ("did not match the expected number of entries").
			Bind.Buffer( SecondFrameBinding, defaultFrameBuffer, FrameBlockSize ) );


		Span<byte> zeros = stackalloc byte[64];
		zeros.Clear();
		defaultObjectBuffer = MakeBuffer( zeros, BufferUse.Storage );
		if ( defaultObjectBuffer == 0 ) return false;

		defaultObject = MakeBindGroup( objectSetLayout,
			Bind.Buffer( 0, defaultObjectBuffer, 64 ),
			Bind.Buffer( 1, defaultObjectBuffer, 64 ) );

		return defaultBones != 0 && defaultObject != 0;
	}

	private static void ReleaseTexture( ref int texture, ref int view )
	{
		if ( view != 0 ) BrowserNative.Release( view );
		if ( texture != 0 ) BrowserNative.Release( texture );
		view = 0;
		texture = 0;
	}

	private static void TearDown()
	{
		BrowserSwapchain.DestroySwapchain();
		BrowserPipelines.DestroyPipelines();
		BrowserBuffers.DestroyObjectSets();
		BrowserTextures.DestroyTextures();
		BrowserBuffers.DestroyBuffers();

		if ( defaultBones != 0 ) BrowserNative.Release( defaultBones );
		if ( defaultObject != 0 ) BrowserNative.Release( defaultObject );
		if ( defaultMaterial != 0 ) BrowserNative.Release( defaultMaterial );
		if ( defaultParams != 0 ) BrowserNative.Release( defaultParams );
		if ( pushRing != 0 ) BrowserNative.Release( pushRing );
		if ( defaultBonesBuffer != 0 ) BrowserNative.Release( defaultBonesBuffer );
		if ( defaultFrameBuffer != 0 ) BrowserNative.Release( defaultFrameBuffer );
		if ( defaultObjectBuffer != 0 ) BrowserNative.Release( defaultObjectBuffer );
		defaultBones = defaultObject = defaultMaterial = defaultParams = pushRing = 0;
		defaultBonesBuffer = defaultFrameBuffer = defaultObjectBuffer = 0;

		ReleaseTexture( ref shadowImage, ref shadowView );
		ReleaseTexture( ref skyTableImage, ref skyTableView );
		ReleaseTexture( ref noBakeImage, ref noBakeView );

		//CoreSkyTable sends the table again rather than leave the next device a cleared one
		CoreSkyTable.Forget();

		if ( textureSetLayout != 0 ) BrowserNative.Release( textureSetLayout );
		if ( boneSetLayout != 0 ) BrowserNative.Release( boneSetLayout );
		if ( objectSetLayout != 0 ) BrowserNative.Release( objectSetLayout );
		textureSetLayout = boneSetLayout = objectSetLayout = 0;

		if ( sampler != 0 ) BrowserNative.Release( sampler );
		if ( clampSampler != 0 ) BrowserNative.Release( clampSampler );
		if ( pointSampler != 0 ) BrowserNative.Release( pointSampler );
		if ( shadowSampler != 0 ) BrowserNative.Release( shadowSampler );
		sampler = clampSampler = pointSampler = shadowSampler = 0;

		BlockCompression = false;
		AstcCompression = false;
		Etc2Compression = false;
		validation = false;
		ready = false;
		lost = false;
	}

	//Async device and adapter setup, awaited once by the launcher before the engine starts
	internal static async Task<bool> Init( int enableValidation )
	{
		if ( ready ) return true;

		if ( await BrowserNative.Init( enableValidation ) == 0 )
		{
			infoText = BrowserNative.Info();
			TearDown();
			return false;
		}

		validation = enableValidation != 0;
		deviceNameText = BrowserNative.DeviceName();
		ShaderFormat = BrowserNative.Language() == "glsl" ? CoreShaderCompiler.Format.Glsl : CoreShaderCompiler.Format.Wgsl;
		BlockCompression = BrowserNative.HasFeature( "texture-compression-bc" ) != 0;
		AstcCompression = BrowserNative.HasFeature( "texture-compression-astc" ) != 0;
		Etc2Compression = BrowserNative.HasFeature( "texture-compression-etc2" ) != 0;

		var largest = (int)BrowserNative.Limit( "maxTextureDimension2D" );
		if ( largest > 0 ) maxImageSize = largest;

		sampler = MakeSampler( "repeat", "linear", false );
		clampSampler = MakeSampler( "clamp-to-edge", "linear", false );
		pointSampler = MakeSampler( "clamp-to-edge", "nearest", false );
		//Wrapped, as the sun's pages are a torus whose levels are read past their edges - see SunLevel in frame.slang
		shadowSampler = MakeSampler( "repeat", "linear", true );

		if ( !CreateLayouts() || !CreateShadows() || !CreateDefaults() )
		{
			infoText = "WebGPU layouts or shadow maps would not be made";
			TearDown();
			return false;
		}

		infoText = BrowserNative.Info();
		ready = true;
		return true;
	}

	internal static void Shutdown()
	{
		TearDown();
		infoText = "not initialised";
		deviceNameText = "";
	}

	//What the device is, for the seam's sbc_render_device_info : one device, no
	//second queue, no bindless, and whether errors are being reported to the console.
	internal static unsafe void DeviceInfo( int* outInfo )
	{
		if ( outInfo is null ) return;

		outInfo[0] = 1;
		outInfo[1] = 0;
		outInfo[2] = 0;
		outInfo[3] = validation ? 1 : 0;
	}
}
