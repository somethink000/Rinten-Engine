using System;
using System.Threading.Tasks;

namespace Rinten;

//WebGPU-shaped calls into the page's handle table (WebGPU or WebGL2); handle 0 is null
//Delegates, not [JSImport]: that attribute throws off the browser at startup
internal static class BrowserNative
{
	//-- the device --------------------------------------------------------------

	//Asks the page for an adapter and a device. A promise, as the browser has it.
	internal static Func<int, Task<int>> InitFn;

	internal static Func<int> ReadyFn;
	internal static Func<int> LostFn;
	internal static Func<string> InfoFn;
	internal static Func<string> DeviceNameFn;

	//Which shading language the page's device takes: wgsl where it is WebGPU, glsl where
	//the browser had only WebGL2 and the page is speaking WebGPU over it - see webgl2.js.
	internal static Func<string> LanguageFn;

	//The last error the device reported, taken off the shim as it is read.
	internal static Func<string> PopErrorFn;

	//How many uncaptured errors the device has reported, ever - the browser's half of CoreErrors. Counted on the page rather
	//than here because the browser reports them through onuncapturederror , on its own schedule and not inside any call of ours.
	internal static Func<int> ErrorCountFn;

	internal static Func<string, double> LimitFn;
	internal static Func<string, int> HasFeatureFn;

	//Destroys what the handle names, where it has a destroy, and frees the slot.
	internal static Action<int> ReleaseFn;

	//Frees the slot without destroying it: for what the page owns and we borrowed.
	internal static Action<int> ForgetFn;

	//-- the canvas --------------------------------------------------------------

	internal static Func<string, int, int, int> CanvasContextFn;
	internal static Action<int, int, int, string, int> ConfigureContextFn;
	internal static Action<int> UnconfigureContextFn;

	//The canvas texture for this frame, or zero where the page would not give one up. It is the browser's, and
	//it presents it itself when the frame's task ends - there is no present call and nothing here destroys it.
	internal static Func<int, int> CurrentTextureFn;

	//-- buffers -----------------------------------------------------------------

	internal static Func<double, int, string, int> CreateBufferFn;

	//A span rather than an array on both of these: the byte view the .NET runtime hands JavaScript is valid while the call is on the stack, which is
	//as long as a queue write needs it, and copying the geometry of a frame into an array to hand it over would be the one allocation on this path.
	internal delegate int CreateBufferWithDataDelegate( Span<byte> bytes, int usage, string label );
	internal delegate void WriteBufferDelegate( int buffer, double offset, Span<byte> bytes );
	internal delegate void WriteTextureDelegate( int texture, int mip, int x, int y,
		int width, int height, int bytesPerRow, Span<byte> bytes );

	internal static CreateBufferWithDataDelegate CreateBufferWithDataFn;
	internal static WriteBufferDelegate WriteBufferFn;

	//-- textures ----------------------------------------------------------------

	internal static Func<int, int, int, string, int, string, int, int, int> CreateTextureFn;
	internal static Func<int, string, string, int, int, int, string, int> CreateViewFn;
	internal static WriteTextureDelegate WriteTextureFn;

	//A picture's Basis blocks, the format to turn them into and its block size - see TranscodeInto.
	internal delegate void TranscodeIntoDelegate( int texture, Span<byte> bytes, int target, int blockBytes );

	internal static TranscodeIntoDelegate TranscodeIntoFn;
	internal static Func<int, int> TranscodeStateFn;
	internal static Func<string, int> CreateSamplerFn;

	//-- layouts and groups ------------------------------------------------------

	internal static Func<string, int> CreateBindGroupLayoutFn;
	internal static Func<string, int> CreatePipelineLayoutFn;
	internal static Func<string, int> CreateBindGroupFn;

	//-- shaders and pipelines ---------------------------------------------------

	internal static Func<string, string, int> CreateShaderModuleFn;

	//What the browser made of the WGSL, once it has looked. Empty where it compiled.
	internal static Func<int, Task<string>> ShaderMessagesFn;

	//Bakes a pipeline. A promise: the browser compiles on another thread and resolves when the
	//thing can be bound, which is why nothing above this waits on a pipeline it has just asked for.
	internal static Func<string, Task<int>> CreateRenderPipelineFn;

	//-- recording ---------------------------------------------------------------

	internal static Func<int> CreateCommandEncoderFn;
	internal static Func<int, int, int> BeginRenderPassFn;
	internal static Action<int, string> DefinePassFn;
	internal static Action ForgetPassesFn;
	//A pass's commands, played by the page in one call - see EndPass. The pass, the words, how many, and whether the pass ends with them.
	internal delegate void RunPassDelegate( int pass, Span<byte> words, int count, int end );

	internal static RunPassDelegate RunPassFn;
	internal static Action<int, int, int, int, int, int, int> CopyTextureToTextureFn;

	//Bytes from one buffer into another, in the device's order - outside any pass.
	internal static Action<int, double, int, double, double> CopyBufferFn;

	//Finishes the encoder and submits in one, which is the only way this ever used the two.
	internal static Action<int> FinishAndSubmitFn;

	//-- what the backend calls --------------------------------------------------- Named as the shim's own functions are, so a line of the backend
	//reads the same as the JavaScript it ends up in. Each is one delegate call, which the JIT inlines away to the [JSImport] stub behind it.

	internal static Task<int> Init( int validation ) => InitFn( validation );
	internal static int Ready() => ReadyFn();
	internal static int Lost() => LostFn();
	internal static string Info() => InfoFn();
	internal static string DeviceName() => DeviceNameFn();
	internal static string Language() => LanguageFn();
	internal static string PopError() => PopErrorFn();
	internal static int ErrorCount() => ErrorCountFn is null ? 0 : ErrorCountFn();
	internal static double Limit( string name ) => LimitFn( name );
	internal static int HasFeature( string name ) => HasFeatureFn( name );
	internal static void Release( int handle )
	{
		Flush();
		ReleaseFn( handle );
	}

	internal static void Forget( int handle )
	{
		Flush();
		ForgetFn( handle );
	}

	internal static int CanvasContext( string canvasId, int width, int height )
		=> CanvasContextFn( canvasId, width, height );


	internal static void ConfigureContext( int context, int width, int height, string format, int usage )
		=> ConfigureContextFn( context, width, height, format, usage );

	internal static void UnconfigureContext( int context ) => UnconfigureContextFn( context );
	internal static int CurrentTexture( int context )
	{
		Flush();
		return CurrentTextureFn( context );
	}

	internal static int CreateBuffer( double size, int usage, string label )
		=> CreateBufferFn( size, usage, label );

	internal static int CreateBufferWithData( Span<byte> bytes, int usage, string label )
		=> CreateBufferWithDataFn( bytes, usage, label );

	//Bytes into a buffer, in their place among the commands - see the head of the
	//stream below. A large write goes on its own, after what was written before it.
	internal static void WriteBuffer( int buffer, double offset, Span<byte> bytes )
	{
		if ( bytes.Length > LargestWritten )
		{
			Flush();
			WriteBufferFn( buffer, offset, bytes );
			return;
		}

		var w = Words( 4 + ( ( bytes.Length + 3 ) >> 2 ) );
		w[0] = OpWrite;
		w[1] = buffer;
		w[2] = Bytes( offset );
		w[3] = bytes.Length;
		bytes.CopyTo( System.Runtime.InteropServices.MemoryMarshal.AsBytes( w[4..] ) );
	}

	internal static int CreateTexture( int width, int height, int mips, string format, int usage, string label,
		int layers = 1, int samples = 1 )
		=> CreateTextureFn( width, height, mips, format, usage, label, layers, samples );

	//A dimension of "2d" is one layer, baseLayer, of an array; "2d-array" every layer as one; empty the texture's own
	internal static int CreateView( int texture, string format, string aspect, int baseMip, int mipCount,
		int baseLayer = 0, string dimension = "" )
		=> CreateViewFn( texture, format, aspect, baseMip, mipCount, baseLayer, dimension );

	internal static void WriteTexture( int texture, int mip, int x, int y,
		int width, int height, int bytesPerRow, Span<byte> bytes )
	{
		Flush();
		WriteTextureFn( texture, mip, x, y, width, height, bytesPerRow, bytes );
	}

	//A KTX2 of Basis blocks into a texture the page made: copied before this returns, turned into target
	//by the page's transcoder in a worker, and written level by level when it is done - see basis.js.
	internal static void TranscodeInto( int texture, Span<byte> bytes, int target, int blockBytes )
		=> TranscodeIntoFn?.Invoke( texture, bytes, target, blockBytes );

	//Whether a texture's transcoded levels have been written: one once they are, minus one where they never
	//will be, nought while they are on their way. One for a texture nothing was transcoded into. See basis.js.
	internal static int TranscodeState( int texture ) => TranscodeStateFn?.Invoke( texture ) ?? 1;

	internal static int CreateSampler( string json ) => CreateSamplerFn( json );

	internal static int CreateBindGroupLayout( string json ) => CreateBindGroupLayoutFn( json );
	internal static int CreatePipelineLayout( string json ) => CreatePipelineLayoutFn( json );
	internal static int CreateBindGroup( string json ) => CreateBindGroupFn( json );

	internal static int CreateShaderModule( string wgsl, string label ) => CreateShaderModuleFn( wgsl, label );
	internal static Task<string> ShaderMessages( int module ) => ShaderMessagesFn( module );
	internal static Task<int> CreateRenderPipeline( string json ) => CreateRenderPipelineFn( json );

	internal static int CreateCommandEncoder()
	{
		Flush();
		return CreateCommandEncoderFn();
	}
	//A pass description the page keeps, under a number - see BrowserSwapchain.PassToken.
	internal static void DefinePass( int token, string json ) => DefinePassFn( token, json );

	//Every description forgotten, when the backend's table starts again.
	internal static void ForgetPasses() => ForgetPassesFn();

	internal static int BeginRenderPass( int encoder, int token )
	{
		Flush();
		return BeginRenderPassFn( encoder, token );
	}
	//-- pass commands as words -- opcodes and operands played by the page in one call (see runPass)

	private const int OpPipeline = 1;
	private const int OpBindGroup = 2;
	private const int OpVertexBuffer = 3;
	private const int OpIndexBuffer = 4;
	private const int OpViewport = 5;
	private const int OpScissor = 6;
	private const int OpDraw = 7;
	private const int OpDrawIndexed = 8;
	private const int OpWrite = 9;
	private const int OpClearDepth = 10;

	//The largest write carried in the words; anything larger goes on its own.
	private const int LargestWritten = 64 * 1024;

	private static int[] words = new int[16384];
	private static int wordCount;

	//The pass the pass commands in the words belong to: the one open, or nought.
	private static int wordPass;

	//Room for count words, at the end of what is written.
	private static Span<int> Words( int count )
	{
		if ( wordCount + count > words.Length ) Array.Resize( ref words, Math.Max( words.Length * 2, wordCount + count ) );

		var span = words.AsSpan( wordCount, count );
		wordCount += count;
		return span;
	}

	//Room for a pass command: the pass is the one the words are for.
	private static Span<int> Words( int pass, int count )
	{
		wordPass = pass;
		return Words( count );
	}

	//What is written, played by the page now.
	private static void Flush()
	{
		if ( wordCount == 0 ) return;
		Play( false );
	}

	private static void Play( bool end )
	{
		var count = wordCount;
		RunPassFn( wordPass, System.Runtime.InteropServices.MemoryMarshal.AsBytes( words.AsSpan( 0, count ) ), count, end ? 1 : 0 );
		wordCount = 0;
	}

	//Offsets and sizes of buffers, as the page wants them: whole numbers, and no buffer here reaches two gigabytes. The page reads them unsigned.
	private static int Bytes( double value ) => (int)(uint)value;

	internal static void SetPipeline( int pass, int pipeline )
	{
		var w = Words( pass, 2 );
		w[0] = OpPipeline;
		w[1] = pipeline;
	}

	//A negative offset is "this group has no dynamic offset", not "offset zero".
	internal static void SetBindGroup( int pass, int index, int group, int dynamicOffset )
	{
		var w = Words( pass, 4 );
		w[0] = OpBindGroup;
		w[1] = index;
		w[2] = group;
		w[3] = dynamicOffset;
	}

	internal static void SetVertexBuffer( int pass, int slot, int buffer, double offset, double size )
	{
		var w = Words( pass, 5 );
		w[0] = OpVertexBuffer;
		w[1] = slot;
		w[2] = buffer;
		w[3] = Bytes( offset );
		w[4] = Bytes( size );
	}

	//The index buffer; format is uint16 or uint32 , as WebGPU names them.
	internal static void SetIndexBuffer( int pass, int buffer, string format, double offset, double size )
	{
		var w = Words( pass, 5 );
		w[0] = OpIndexBuffer;
		w[1] = buffer;
		w[2] = format == "uint32" ? 1 : 0;
		w[3] = Bytes( offset );
		w[4] = Bytes( size );
	}

	internal static void SetViewport( int pass, float x, float y, float width, float height,
		float minDepth, float maxDepth )
	{
		var w = Words( pass, 7 );
		w[0] = OpViewport;
		w[1] = BitConverter.SingleToInt32Bits( x );
		w[2] = BitConverter.SingleToInt32Bits( y );
		w[3] = BitConverter.SingleToInt32Bits( width );
		w[4] = BitConverter.SingleToInt32Bits( height );
		w[5] = BitConverter.SingleToInt32Bits( minDepth );
		w[6] = BitConverter.SingleToInt32Bits( maxDepth );
	}

	//The depth under the scissor back to the far plane - see BrowserSwapchain.ShadowArea
	internal static void ClearDepth( int pass )
	{
		var w = Words( pass, 1 );
		w[0] = OpClearDepth;
	}

	internal static void SetScissorRect( int pass, int x, int y, int width, int height )
	{
		var w = Words( pass, 5 );
		w[0] = OpScissor;
		w[1] = x;
		w[2] = y;
		w[3] = width;
		w[4] = height;
	}

	internal static void Draw( int pass, int vertexCount, int instanceCount, int firstVertex, int firstInstance )
	{
		var w = Words( pass, 5 );
		w[0] = OpDraw;
		w[1] = vertexCount;
		w[2] = instanceCount;
		w[3] = firstVertex;
		w[4] = firstInstance;
	}

	internal static void DrawIndexed( int pass, int indexCount, int instanceCount,
		int firstIndex, int baseVertex, int firstInstance )
	{
		var w = Words( pass, 6 );
		w[0] = OpDrawIndexed;
		w[1] = indexCount;
		w[2] = instanceCount;
		w[3] = firstIndex;
		w[4] = baseVertex;
		w[5] = firstInstance;
	}

	//The words played and the pass ended, in one call into the page.
	internal static void EndPass( int pass )
	{
		wordPass = pass;
		Play( true );
		wordPass = 0;
	}

	internal static void CopyBuffer( int from, double fromOffset, int into, double intoOffset, double size )
	{
		Flush();
		CopyBufferFn( from, fromOffset, into, intoOffset, size );
	}

	//Headset eye output and tuning (size and foveation); see presentInto and xrTune in webgl2.js
	internal static Action<double, double> XrTuneFn;

	internal static void XrTune( double scale, double foveation ) => XrTuneFn?.Invoke( scale, foveation );

	//What RenderScale would have to be for an eye to be drawn at the headset's own
	//resolution - see xrNative in webgl2.js. Nought where the browser does not say.
	internal static Func<double> XrNativeFn;

	internal static double XrNative() => XrNativeFn?.Invoke() ?? 0.0;

	internal static Func<int, int, int, int> XrEyeCopyFn;

	internal static int XrEyeCopy( int source, int eye, int layer = 0 )
	{
		Flush();
		return XrEyeCopyFn is null ? 0 : XrEyeCopyFn( source, eye, layer );
	}

	//Whether this browser can draw both of a headset's eyes in one pass - OVR_multiview2. Nought before a session, and on every browser without it.
	internal static Func<int> XrStereoFn;

	//GPU milliseconds finished since the last ask, -1 with no timer; the argument keeps timing on or off.
	internal static Func<int, double> GpuTakeFn;

	//Draws issued since the last ask.
	internal static Func<int> DrawsTakeFn;

	internal static double GpuTake( bool want ) => GpuTakeFn is null ? -1 : GpuTakeFn( want ? 1 : 0 );

	internal static int DrawsTake()
	{
		Flush();
		return DrawsTakeFn?.Invoke() ?? 0;
	}

	//Whether the eyes are drawn straight into a WebXR projection layer.
	internal static Func<int> XrLayeredFn;

	internal static bool XrLayered() => ( XrLayeredFn?.Invoke() ?? 0 ) != 0;

	//Whether the context has the extension, which it does or does not for as long as it lives - so asked
	//once the device is up and remembered. It was asked seven times a frame, each a crossing into the page.
	internal static int XrStereo()
	{
		if ( stereoKnown ) return stereo;

		var answer = XrStereoFn?.Invoke() ?? 0;

		if ( BrowserDevice.Ready )
		{
			stereo = answer;
			stereoKnown = true;
		}

		return answer;
	}

	private static int stereo;
	private static bool stereoKnown;

	internal static void CopyTextureToTexture( int encoder, int source, int sourceMip,
		int destination, int destinationMip, int width, int height )
	{
		Flush();
		CopyTextureToTextureFn( encoder, source, sourceMip, destination, destinationMip, width, height );
	}

	internal static void FinishAndSubmit( int encoder )
	{
		Flush();
		FinishAndSubmitFn( encoder );
	}
}
