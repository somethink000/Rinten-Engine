using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Rinten;

//Pipelines on WebGPU: two WGSL shaders and the fixed-function state between them, baked. Same handle ABI as the Vulkan half.
internal static class BrowserPipelines
{
	//A pipeline as the renderer holds it: its handle from the moment it is asked for, and the
	//browser's once the bake lands - nought until then, and for good where it did not bake.
	private sealed class Pipeline
	{
		public int Handle;
		public Task<int> Baked;
		public ulong Key;

		/// <summary>The same pipeline for a pass of four samples, baked the first time one asks - see <see cref="Get"/>. WebGPU fixes the sample count in the pipeline; a canvas's world is drawn with four and a view's with one, and every surface can be drawn into either.</summary>
		public int Multisampled;
		public Task<int> BakedMultisampled;
		public bool AskedMultisampled;

		/// <summary>What the second bake is made from, kept only where it may be asked for: a pipeline that draws colour onto the screen, on WebGPU. Nought otherwise.</summary>
		public string Describe;
		public int VertexModule;
		public int PixelModule;
		public int Layout;
		public bool Stereo;
	}

	private static readonly Dictionary<ulong, Pipeline> pipelines = new();
	private static readonly Dictionary<ulong, ulong> byKey = new();
	private static readonly List<Pipeline> baking = new();
	private static ulong nextHandle = 1;

	//The browser's pipeline behind a handle, or nought while it is still baking.
	internal static int Get( ulong handle, int samples = 1 )
	{
		if ( !pipelines.TryGetValue( handle, out var found ) ) return 0;

		if ( samples > 1 && found.Describe is not null )
		{
			if ( !found.AskedMultisampled )
			{
				found.AskedMultisampled = true;
				found.BakedMultisampled = Bake( found.Describe.Replace( SingleSample, FourSamples ),
					found.VertexModule, found.PixelModule, found.Layout, found.Stereo, release: false );
				if ( !baking.Contains( found ) ) baking.Add( found );
			}

			if ( found.BakedMultisampled is not null ) Settle();
			return found.Multisampled;
		}

		if ( found.Baked is not null ) Settle();

		return found.Handle;
	}

	//Whether the pipeline behind a handle is still being baked by the browser - a draw with it draws nothing yet, and will once it lands.
	internal static bool Baking( ulong handle, int samples = 1 )
	{
		if ( !pipelines.TryGetValue( handle, out var found ) ) return false;

		return samples > 1 && found.Describe is not null
			? !found.AskedMultisampled || found.BakedMultisampled is not null
			: found.Baked is not null;
	}

	private const string SingleSample = "\"multisample\":{\"count\":1,";
	private const string FourSamples = "\"multisample\":{\"count\":4,";

	//Everything the browser holds for one pipeline.
	private static void ReleaseOne( Pipeline one )
	{
		if ( one.Handle != 0 ) BrowserNative.Release( one.Handle );
		if ( one.Multisampled != 0 ) BrowserNative.Release( one.Multisampled );
		if ( one.VertexModule != 0 ) BrowserNative.Release( one.VertexModule );
		if ( one.PixelModule != 0 ) BrowserNative.Release( one.PixelModule );
		if ( one.Layout != 0 ) BrowserNative.Release( one.Layout );
		one.Handle = one.Multisampled = one.VertexModule = one.PixelModule = one.Layout = 0;
	}

	internal static void DestroyPipelines()
	{
		//A bake that has not landed yet is let go of rather than waited for - there is nothing here that
		//can wait, and the browser drops the pipeline it was making as soon as nothing holds the promise.
		baking.Clear();
		byKey.Clear();

		foreach ( var pair in pipelines ) ReleaseOne( pair.Value );

		pipelines.Clear();
	}

	internal static void Destroy( ulong pipeline )
	{
		if ( !pipelines.TryGetValue( pipeline, out var found ) ) return;

		ReleaseOne( found );
		baking.Remove( found );
		byKey.Remove( found.Key );
		pipelines.Remove( pipeline );
	}

	//-- shader modules ------------------------------------------------------------

	//Shader text - WGSL, or GLSL on WebGL2 - into a shader module. A compiled shader carries SPIR-V beside the browser's languages, and SPIR-V means
	//nothing here - so a build that shipped the desktop half is told which half it shipped rather than left with a module that will not compile.
	private static unsafe int MakeModule( byte* code, int size, string label, int specialize = -1 )
	{
		if ( code is null || size <= 0 ) return 0;

		if ( size >= 4 )
		{
			var magic = *(uint*)code;
			if ( magic == 0x07230203u )
			{
				BrowserDevice.infoText = "that shader is SPIR-V - export it for the web again";
				return 0;
			}
		}

		//The trailing nulls and carriage returns a packed shader may carry are not part of the source, and neither language will have them.
		var end = size;
		while ( end > 0 && ( code[end - 1] == 0 || code[end - 1] == (byte)'\r' ) ) end--;

		var source = Encoding.UTF8.GetString( code, end );
		return BrowserNative.CreateShaderModule( Specialized( source, specialize ), label );
	}

	//-- specialization ---------------------------------------------------------------

	//The value a pipeline gives specialization constant `id`: one bit of `specialize` an id, as VkPipelines.Create gives it.
	private static int ConstantValue( int specialize, int id ) => ( specialize >> id ) & 1;

	private static readonly System.Text.RegularExpressions.Regex GlslConstant =
		new( @"#ifndef SPIRV_CROSS_CONSTANT_ID_(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled );

	private static readonly System.Text.RegularExpressions.Regex WgslConstant =
		new( @"@id\((\d+)\)\s*override", System.Text.RegularExpressions.RegexOptions.Compiled );

	//Defines SPIRV-Cross specialization constants in GLSL after the version line; WGSL uses overrides
	private static string Specialized( string source, int specialize )
	{
		if ( specialize < 0 || BrowserDevice.ShaderFormat != CoreShaderCompiler.Format.Glsl ) return source;

		var found = GlslConstant.Matches( source );
		if ( found.Count == 0 ) return source;

		var line = source.IndexOf( '\n' );
		if ( line < 0 ) return source;

		var defines = new StringBuilder();
		foreach ( System.Text.RegularExpressions.Match one in found )
		{
			var id = int.Parse( one.Groups[1].Value );
			defines.Append( "#define SPIRV_CROSS_CONSTANT_ID_" ).Append( id ).Append( ' ' )
				.Append( ConstantValue( specialize, id ) ).Append( '\n' );
		}

		return source.Substring( 0, line + 1 ) + defines + source.Substring( line + 1 );
	}

	//A WGSL stage's `constants`, as the pipeline descriptor takes them: every `@id(n) override` the module declares, and
	//only those - WebGPU refuses a pipeline that names one its module has not got. Empty where there is nothing to say.
	private static unsafe string Constants( byte* code, int size, int specialize )
	{
		if ( specialize < 0 || code is null || size <= 0 || BrowserDevice.ShaderFormat == CoreShaderCompiler.Format.Glsl )
			return "";

		var found = WgslConstant.Matches( Encoding.UTF8.GetString( code, size ) );
		if ( found.Count == 0 ) return "";

		var json = new StringBuilder( ",\"constants\":{" );
		var first = true;

		foreach ( System.Text.RegularExpressions.Match one in found )
		{
			var id = int.Parse( one.Groups[1].Value );
			if ( !first ) json.Append( ',' );
			json.Append( '"' ).Append( id ).Append( "\":" ).Append( ConstantValue( specialize, id ) );
			first = false;
		}

		return json.Append( '}' ).ToString();
	}

	//-- the descriptor -------------------------------------------------------------

	//What a pipeline's vertices look like. Matches sbc_vertex_layout.
	private const int VertexEngine = 1;
	private const int VertexLine = 2;
	private const int VertexUi = 3;
	private const int VertexSkinned = 4;

	//Matches sbc_topology: anything that is not lines is a triangle list.
	private const int TopologyLines = 1;

	private static void Attribute( StringBuilder json, string format, int offset, int location, bool first )
	{
		if ( !first ) json.Append( ',' );
		json.Append( "{\"format\":\"" ).Append( format ).Append( "\",\"offset\":" ).Append( offset )
			.Append( ",\"shaderLocation\":" ).Append( location ).Append( '}' );
	}

	//The engine vertex, exactly as .mdl_c and PackedVertex lay it out - 52 bytes, offsets fixed by
	//that format, the normal and tangent one packed word at location two (see vertex_frame.slang).
	private static void EngineAttributes( StringBuilder json )
	{
		Attribute( json, "float32x3", 0, 0, true );
		Attribute( json, "unorm8x4", 12, 1, false );
		Attribute( json, "uint32", PackedVertex.FrameOffset, 2, false );
		Attribute( json, "float32x4", PackedVertex.TexCoord0Offset, 3, false );
		Attribute( json, "float32x4", PackedVertex.TexCoord1Offset, 4, false );
	}

	//The engine vertex's stride, as the JSON says it.
	private const string EngineStride = "{\"arrayStride\":52,\"stepMode\":\"vertex\",\"attributes\":[";

	private static void VertexBuffers( StringBuilder json, int vertexLayout )
	{
		json.Append( ",\"buffers\":[" );

		switch ( vertexLayout )
		{
			case VertexEngine:
				json.Append( EngineStride );
				EngineAttributes( json );
				json.Append( "]}" );
				break;

			case VertexLine:
				//The line vertex: a position and a colour, nothing else.
				json.Append( "{\"arrayStride\":16,\"stepMode\":\"vertex\",\"attributes\":[" );
				Attribute( json, "float32x3", 0, 0, true );
				Attribute( json, "unorm8x4", 12, 1, false );
				json.Append( "]}" );
				break;

			case VertexSkinned:
				//The engine vertex as it always was, and the skin beside it in a second buffer - so one
				//mesh's vertices serve both paths and a skinned model needs no second copy of its geometry.
				json.Append( EngineStride );
				EngineAttributes( json );
				json.Append( "]},{\"arrayStride\":8,\"stepMode\":\"vertex\",\"attributes\":[" );
				Attribute( json, "uint8x4", 0, 6, true );
				Attribute( json, "unorm8x4", 4, 7, false );
				json.Append( "]}" );
				break;

			case VertexUi:
				json.Append( "{\"arrayStride\":168,\"stepMode\":\"vertex\",\"attributes\":[" );
				Attribute( json, "float32x2", 0, 0, true );
				Attribute( json, "float32x2", 8, 1, false );
				Attribute( json, "float32x4", 16, 2, false );
				Attribute( json, "float32x4", 32, 3, false );
				Attribute( json, "unorm8x4", 48, 4, false );
				Attribute( json, "uint32x4", 52, 5, false );
				Attribute( json, "float32x4", 68, 6, false );
				Attribute( json, "float32x4", 84, 7, false );
				Attribute( json, "float32x4", 100, 8, false );
				Attribute( json, "float32x2", 116, 9, false );
				Attribute( json, "float32x2", 124, 10, false );
				Attribute( json, "uint32", 132, 11, false );
				Attribute( json, "float32x4", 136, 12, false );
				Attribute( json, "uint32x4", 152, 13, false );
				json.Append( "]}" );
				break;
		}

		json.Append( ']' );
	}

	private const string BlendJson =
		"{\"color\":{\"operation\":\"add\",\"srcFactor\":\"src-alpha\",\"dstFactor\":\"one-minus-src-alpha\"}," +
		"\"alpha\":{\"operation\":\"add\",\"srcFactor\":\"one\",\"dstFactor\":\"one-minus-src-alpha\"}}";

	//A string as JSON. Entry point names are identifiers, but nothing here trusts that.
	private static void Quoted( StringBuilder json, string text )
	{
		json.Append( '"' );

		foreach ( var c in text ?? "" )
		{
			if ( c is '"' or '\\' ) json.Append( '\\' ).Append( c );
			else if ( c < ' ' ) json.Append( ' ' );
			else json.Append( c );
		}

		json.Append( '"' );
	}

	private static string Describe( int pipelineLayout, int vertexModule, string vertexEntry,
		int pixelModule, string pixelEntry,
		int vertexLayout, int depthTest, int topology, int blend,
		int depthWrite, int cullBack, int depthOnly, int target,
		string vertexConstants = "", string pixelConstants = "", int pictureFormat = -1 )
	{
		var json = new StringBuilder( 2048 );

		json.Append( "{\"layout\":" ).Append( pipelineLayout )
			.Append( ",\"vertexModule\":" ).Append( vertexModule )
			.Append( ",\"vertex\":{\"entryPoint\":" );
		Quoted( json, vertexEntry );
		VertexBuffers( json, vertexLayout );
		json.Append( vertexConstants ).Append( '}' );

		if ( depthOnly == 0 )
		{
			json.Append( ",\"fragmentModule\":" ).Append( pixelModule )
				.Append( ",\"fragment\":{\"entryPoint\":" );
			Quoted( json, pixelEntry );
			json.Append( ",\"targets\":[{\"format\":\"" );

			if ( target == BrowserDevice.Target.Picture )
			{
				json.Append( BrowserSwapchain.PictureFormatName( pictureFormat ) ).Append( "\",\"writeMask\":15" );
				if ( blend != 0 ) json.Append( ",\"blend\":" ).Append( BlendJson );
				json.Append( '}' );
			}
			else if ( target == BrowserDevice.Target.SkyTable )
				json.Append( BrowserDevice.FormatRgba16Float ).Append( "\",\"writeMask\":15}" );
			else if ( target == BrowserDevice.Target.Scene )
			{
				//The scene pass writes colour and the thin g-buffer beside it.
				json.Append( BrowserDevice.FormatRgba16Float ).Append( "\",\"writeMask\":15" );
				if ( blend != 0 ) json.Append( ",\"blend\":" ).Append( BlendJson );
				json.Append( "},{\"format\":\"" ).Append( BrowserDevice.FormatRgba8 )
					.Append( "\",\"writeMask\":15}" );
			}
			else
			{
				json.Append( BrowserDevice.ScreenFormat );

				json.Append( "\",\"writeMask\":15" );
				if ( blend != 0 ) json.Append( ",\"blend\":" ).Append( BlendJson );
				json.Append( '}' );
			}

			json.Append( ']' ).Append( pixelConstants ).Append( '}' );
		}

		json.Append( ",\"primitive\":{\"topology\":\"" )
			.Append( topology == TopologyLines ? "line-list" : "triangle-list" )
			.Append( "\",\"cullMode\":\"" ).Append( cullBack != 0 ? "back" : "none" )
			.Append( "\",\"frontFace\":\"ccw\"}" );

		//A picture's pass has no depth, and a pipeline naming one would not draw in it
		if ( target is not ( BrowserDevice.Target.Picture or BrowserDevice.Target.SkyTable ) )
		{
			//Stencil is left at the browser's defaults, which are always/keep.
			json.Append( ",\"depthStencil\":{\"format\":\"" ).Append( BrowserDevice.FormatDepth32 )
				.Append( "\",\"depthWriteEnabled\":" ).Append( depthWrite != 0 ? "true" : "false" )
				.Append( ",\"depthCompare\":\"" ).Append( depthTest switch
				{
					//As VkPipelines has them: a camera's depth is reversed, a light's map is not - see CoreRenderDevice.DepthCompare
					0 or 3 => "always",
					2 => "equal",
					4 => "less-equal",
					_ => "greater-equal",
				} ).Append( '"' );

			//A shadow map is drawn from the light and read from the camera, and the two disagree by a fraction
			//of a texel. The bias is what stops that disagreement from being a stripe across every lit surface.
			if ( depthOnly != 0 && depthTest == (int)CoreRenderDevice.DepthCompare.LightNearerOrSame ) json.Append( ",\"depthBias\":2,\"depthBiasSlopeScale\":2" );

			json.Append( '}' );
		}

		json.Append( ',' ).Append( SingleSample ).Append( "\"mask\":4294967295}}" );
		return json.ToString();
	}

	//-- baking ---------------------------------------------------------------------

	//Returns a pipeline handle at once; draws with it are skipped until the async compile lands
	internal static unsafe ulong Create(
		byte* vertexCode, int vertexSize, string vertexEntry,
		byte* pixelCode, int pixelSize, string pixelEntry,
		int vertexLayout, int depthTest, int topology, int blend,
		int depthWrite, int cullBack, int depthOnly, int target, int specialize = -1, int pictureFormat = -1 )
	{
		if ( !BrowserDevice.Ready )
		{
			BrowserDevice.infoText = "there is no device to bake a pipeline on";
			return 0;
		}

		if ( vertexEntry is null || pixelEntry is null )
		{
			BrowserDevice.infoText = "a pipeline needs both entry point names";
			return 0;
		}

		var key = Key( vertexCode, vertexSize, vertexEntry, pixelCode, pixelSize, pixelEntry,
			vertexLayout, depthTest, topology, blend, depthWrite, cullBack, depthOnly, target, specialize, pictureFormat );

		if ( byKey.TryGetValue( key, out var known ) ) return known;

		var vertexModule = MakeModule( vertexCode, vertexSize, vertexEntry, specialize );
		var pixelModule = MakeModule( pixelCode, pixelSize, pixelEntry, specialize );

		if ( vertexModule == 0 || pixelModule == 0 )
		{
			if ( !BrowserDevice.infoText.Contains( "SPIR-V" ) )
				BrowserDevice.infoText = "the shader text would not become a shader module";

			if ( vertexModule != 0 ) BrowserNative.Release( vertexModule );
			if ( pixelModule != 0 ) BrowserNative.Release( pixelModule );
			return 0;
		}

		//Every pipeline carries all three groups, used or not - identical layouts mean any texture, any bone block and any
		//object's buffers can bind against any pipeline, and a shader that ignores a group costs nothing by having it declared.
		var pipelineLayout = BrowserNative.CreatePipelineLayout(
			$"{{\"layouts\":[{BrowserDevice.textureSetLayout},{BrowserDevice.boneSetLayout},{BrowserDevice.objectSetLayout}]}}" );

		var describe = Describe( pipelineLayout, vertexModule, vertexEntry, pixelModule, pixelEntry,
			vertexLayout, depthTest, topology, blend, depthWrite, cullBack, depthOnly, target,
			Constants( vertexCode, vertexSize, specialize ), Constants( pixelCode, pixelSize, specialize ), pictureFormat );

		//A program built for both of a headset's eyes at once - see CoreRender.Multiview.
		//Only the vertex stage says so: the pixel stage is told its view, not asked.
		var stereo = new ReadOnlySpan<byte>( vertexCode, vertexSize ).IndexOf( "num_views"u8 ) >= 0;

		//Only what may be drawn into a canvas's world keeps what a second bake takes.
		var keep = target == BrowserDevice.Target.Screen && depthOnly == 0 &&
			BrowserDevice.ShaderFormat != CoreShaderCompiler.Format.Glsl;

		var made = new Pipeline
		{
			Key = key,
			Baked = Bake( describe, vertexModule, pixelModule, pipelineLayout, stereo, release: !keep ),
		};

		if ( keep )
		{
			made.Describe = describe;
			made.VertexModule = vertexModule;
			made.PixelModule = pixelModule;
			made.Layout = pipelineLayout;
			made.Stereo = stereo;
		}
		var handle = nextHandle++;

		pipelines[handle] = made;
		byKey[key] = handle;
		baking.Add( made );

		return handle;
	}

	private static async Task<int> Bake( string describe, int vertexModule, int pixelModule, int pipelineLayout, bool stereo,
		bool release )
	{
		try
		{
			var made = await BrowserNative.CreateRenderPipeline( describe );

			if ( made == 0 )
			{
				var why = BrowserNative.PopError();
				var vertexSaid = await BrowserNative.ShaderMessages( vertexModule );
				var pixelSaid = await BrowserNative.ShaderMessages( pixelModule );
				var said = string.Join( " ", why, vertexSaid, pixelSaid ).Trim();
				BrowserDevice.infoText = said.Length > 0 ? $"pipeline: {said}" : "the pipeline would not bake";
				Console.WriteLine( $"pipeline bake failed: {BrowserDevice.infoText}" );

				//A two-view program the browser would not link is a surface missing from both eyes, and nothing else would ever say so - the handle just
				//stays nought and the draw draws nothing. So it takes the headset back to an eye at a time, the same as a two-view build that was never baked.
				if ( stereo ) CoreSceneRenderer.StereoRefused( BrowserDevice.infoText );
			}

			return made;
		}
		finally
		{
			//The modules and the layout are the browser's to keep for as long as the pipeline needs them; these were only ever
			//handles onto them - unless another bake is to be made from them, and then the pipeline lets them go when it goes.
			if ( release ) BrowserNative.Release( vertexModule );
			if ( release ) BrowserNative.Release( pixelModule );
			if ( release && pipelineLayout != 0 ) BrowserNative.Release( pipelineLayout );
		}
	}

	//Puts whatever the browser has finished behind its handle.
	private static void Settle()
	{
		for ( var i = baking.Count - 1; i >= 0; i-- )
		{
			var one = baking[i];

			if ( one.Baked is { IsCompleted: true } )
			{
				one.Handle = one.Baked.IsCompletedSuccessfully ? one.Baked.Result : 0;
				one.Baked = null;
			}

			if ( one.BakedMultisampled is { IsCompleted: true } )
			{
				one.Multisampled = one.BakedMultisampled.IsCompletedSuccessfully ? one.BakedMultisampled.Result : 0;
				one.BakedMultisampled = null;
			}

			if ( one.Baked is null && one.BakedMultisampled is null ) baking.RemoveAt( i );
		}
	}

	//Pipeline cache key: hashes both shader sources and every state switch
	private static unsafe ulong Key(
		byte* vertexCode, int vertexSize, string vertexEntry,
		byte* pixelCode, int pixelSize, string pixelEntry,
		int vertexLayout, int depthTest, int topology, int blend,
		int depthWrite, int cullBack, int depthOnly, int target, int specialize, int pictureFormat )
	{
		var hash = 14695981039346656037ul;

		hash = Fold( hash, Fnv( vertexCode, vertexSize ) );
		hash = Fold( hash, Fnv( pixelCode, pixelSize ) );
		hash = Fold( hash, (uint)( vertexEntry?.GetHashCode() ?? 0 ) );
		hash = Fold( hash, (uint)( pixelEntry?.GetHashCode() ?? 0 ) );
		hash = Fold( hash, (uint)vertexLayout );
		hash = Fold( hash, (uint)depthTest );
		hash = Fold( hash, (uint)topology );
		hash = Fold( hash, (uint)blend );
		hash = Fold( hash, (uint)depthWrite );
		hash = Fold( hash, (uint)cullBack );
		hash = Fold( hash, (uint)depthOnly );
		hash = Fold( hash, (uint)target );
		hash = Fold( hash, (uint)specialize );
		hash = Fold( hash, (uint)pictureFormat );
		return hash;
	}

	private static ulong Fold( ulong hash, ulong value ) => ( hash ^ value ) * 1099511628211ul;

	private static unsafe ulong Fnv( byte* data, int size )
	{
		var hash = 14695981039346656037ul;
		if ( data is null ) return hash;

		for ( var i = 0; i < size; i++ )
			hash = ( hash ^ data[i] ) * 1099511628211ul;

		return hash;
	}
}
