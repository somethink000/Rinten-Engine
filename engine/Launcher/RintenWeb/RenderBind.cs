using System;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

namespace Rinten;

//JSImport bindings for the page renderer; JSImport attributes in Rinten.Engine break desktop startup
static partial class RenderBind
{
	//-- the device --------------------------------------------------------------

	[JSImport( "globalThis.RintenRender.init" )]
	internal static partial Task<int> Init( int validation );

	[JSImport( "globalThis.RintenRender.ready" )]
	internal static partial int Ready();

	[JSImport( "globalThis.RintenRender.lost" )]
	internal static partial int Lost();

	[JSImport( "globalThis.RintenRender.info" )]
	internal static partial string Info();

	[JSImport( "globalThis.RintenRender.deviceName" )]
	internal static partial string DeviceName();

	[JSImport( "globalThis.RintenRender.language" )]
	internal static partial string Language();

	[JSImport( "globalThis.RintenRender.popError" )]
	internal static partial string PopError();

	[JSImport( "globalThis.RintenRender.errorCount" )]
	internal static partial int ErrorCount();

	[JSImport( "globalThis.RintenRender.limit" )]
	internal static partial double Limit( string name );

	[JSImport( "globalThis.RintenRender.hasFeature" )]
	internal static partial int HasFeature( string name );

	[JSImport( "globalThis.RintenRender.release" )]
	internal static partial void Release( int handle );

	[JSImport( "globalThis.RintenRender.forget" )]
	internal static partial void Forget( int handle );

	//-- the canvas --------------------------------------------------------------

	[JSImport( "globalThis.RintenRender.canvasContext" )]
	internal static partial int CanvasContext( string canvasId, int width, int height );

	[JSImport( "globalThis.RintenRender.configureContext" )]
	internal static partial void ConfigureContext( int context, int width, int height, string format, int usage );

	[JSImport( "globalThis.RintenRender.unconfigureContext" )]
	internal static partial void UnconfigureContext( int context );

	[JSImport( "globalThis.RintenRender.currentTexture" )]
	internal static partial int CurrentTexture( int context );

	//-- buffers -----------------------------------------------------------------

	[JSImport( "globalThis.RintenRender.createBuffer" )]
	internal static partial int CreateBuffer( double size, int usage, string label );

	[JSImport( "globalThis.RintenRender.createBufferWithData" )]
	internal static partial int CreateBufferWithData(
		[JSMarshalAs<JSType.MemoryView>] Span<byte> bytes, int usage, string label );

	[JSImport( "globalThis.RintenRender.writeBuffer" )]
	internal static partial void WriteBuffer( int buffer, double offset,
		[JSMarshalAs<JSType.MemoryView>] Span<byte> bytes );

	//-- textures ----------------------------------------------------------------

	[JSImport( "globalThis.RintenRender.createTexture" )]
	internal static partial int CreateTexture( int width, int height, int mips, string format, int usage, string label,
		int layers, int samples );

	[JSImport( "globalThis.RintenRender.createView" )]
	internal static partial int CreateView( int texture, string format, string aspect, int baseMip, int mipCount,
		int baseLayer, string dimension );

	[JSImport( "globalThis.RintenRender.writeTexture" )]
	internal static partial void WriteTexture( int texture, int mip, int x, int y,
		int width, int height, int bytesPerRow, [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes );

	[JSImport( "globalThis.RintenRender.createSampler" )]
	internal static partial int CreateSampler( string json );

	//-- layouts and groups ------------------------------------------------------

	[JSImport( "globalThis.RintenRender.createBindGroupLayout" )]
	internal static partial int CreateBindGroupLayout( string json );

	[JSImport( "globalThis.RintenRender.createPipelineLayout" )]
	internal static partial int CreatePipelineLayout( string json );

	[JSImport( "globalThis.RintenRender.createBindGroup" )]
	internal static partial int CreateBindGroup( string json );

	//-- shaders and pipelines ---------------------------------------------------

	[JSImport( "globalThis.RintenRender.createShaderModule" )]
	internal static partial int CreateShaderModule( string wgsl, string label );

	[JSImport( "globalThis.RintenRender.shaderMessages" )]
	internal static partial Task<string> ShaderMessages( int module );

	[JSImport( "globalThis.RintenRender.createRenderPipeline" )]
	internal static partial Task<int> CreateRenderPipeline( string json );

	//-- recording ---------------------------------------------------------------

	[JSImport( "globalThis.RintenRender.createCommandEncoder" )]
	internal static partial int CreateCommandEncoder();

	[JSImport( "globalThis.RintenRender.definePass" )]
	internal static partial void DefinePass( int token, string json );

	[JSImport( "globalThis.RintenRender.forgetPasses" )]
	internal static partial void ForgetPasses();

	[JSImport( "globalThis.RintenRender.beginRenderPass" )]
	internal static partial int BeginRenderPass( int encoder, int token );

	[JSImport( "globalThis.RintenRender.transcodeInto" )]
	internal static partial void TranscodeInto( int texture,
		[JSMarshalAs<JSType.MemoryView>] Span<byte> bytes, int target, int blockBytes );

	[JSImport( "globalThis.RintenRender.transcodeState" )]
	internal static partial int TranscodeState( int texture );

	[JSImport( "globalThis.RintenRender.runPass" )]
	internal static partial void RunPass( int pass,
		[JSMarshalAs<JSType.MemoryView>] Span<byte> words, int count, int end );

	[JSImport( "globalThis.RintenRender.copyBuffer" )]
	internal static partial void CopyBuffer( int from, double fromOffset, int into, double intoOffset, double size );

	[JSImport( "globalThis.RintenRender.copyTextureToTexture" )]
	internal static partial void CopyTextureToTexture( int encoder, int source, int sourceMip,
		int destination, int destinationMip, int width, int height );

	//A finished eye into the headset's framebuffer - see webgl2.js.
	[JSImport( "globalThis.RintenRender.xrEyeCopy" )]
	internal static partial int XrEyeCopy( int source, int eye, int layer );

	//Whether this browser can draw both of a headset's eyes in one pass.
	[JSImport( "globalThis.RintenRender.xrStereo" )]
	internal static partial int XrStereo();

	//The GPU's time on the frames since the last ask - see gpuTake in webgl2.js.
	[JSImport( "globalThis.RintenRender.gpuTake" )]
	internal static partial double GpuTake( int want );

	//Draws issued since the last ask.
	[JSImport( "globalThis.RintenRender.drawsTake" )]
	internal static partial int DrawsTake();

	//Whether the eyes go straight into a projection layer - see xrLayered in webgl2.js.
	[JSImport( "globalThis.RintenRender.xrLayered" )]
	internal static partial int XrLayered();

	//How big an eye is drawn and how far its edge is foveated.
	[JSImport( "globalThis.RintenRender.xrTune" )]
	internal static partial void XrTune( double scale, double foveation );

	//What RenderScale would have to be for an eye to be drawn one-to-one.
	[JSImport( "globalThis.RintenRender.xrNative" )]
	internal static partial double XrNative();

	[JSImport( "globalThis.RintenRender.finishAndSubmit" )]
	internal static partial void FinishAndSubmit( int encoder );

	//Hands every one of these to the engine's backend. Called once, before the engine starts.
	internal static void Attach()
	{
		BrowserNative.InitFn = Init;
		BrowserNative.ReadyFn = Ready;
		BrowserNative.LostFn = Lost;
		BrowserNative.InfoFn = Info;
		BrowserNative.DeviceNameFn = DeviceName;
		BrowserNative.LanguageFn = Language;
		BrowserNative.PopErrorFn = PopError;
		BrowserNative.ErrorCountFn = ErrorCount;
		BrowserNative.LimitFn = Limit;
		BrowserNative.HasFeatureFn = HasFeature;
		BrowserNative.ReleaseFn = Release;
		BrowserNative.ForgetFn = Forget;

		BrowserNative.CanvasContextFn = CanvasContext;
		BrowserNative.ConfigureContextFn = ConfigureContext;
		BrowserNative.UnconfigureContextFn = UnconfigureContext;
		BrowserNative.CurrentTextureFn = CurrentTexture;

		BrowserNative.CreateBufferFn = CreateBuffer;
		BrowserNative.CreateBufferWithDataFn = CreateBufferWithData;
		BrowserNative.WriteBufferFn = WriteBuffer;

		BrowserNative.CreateTextureFn = CreateTexture;
		BrowserNative.CreateViewFn = CreateView;
		BrowserNative.WriteTextureFn = WriteTexture;
		BrowserNative.CreateSamplerFn = CreateSampler;

		BrowserNative.CreateBindGroupLayoutFn = CreateBindGroupLayout;
		BrowserNative.CreatePipelineLayoutFn = CreatePipelineLayout;
		BrowserNative.CreateBindGroupFn = CreateBindGroup;

		BrowserNative.CreateShaderModuleFn = CreateShaderModule;
		BrowserNative.ShaderMessagesFn = ShaderMessages;
		BrowserNative.CreateRenderPipelineFn = CreateRenderPipeline;

		BrowserNative.CreateCommandEncoderFn = CreateCommandEncoder;
		BrowserNative.DefinePassFn = DefinePass;
		BrowserNative.ForgetPassesFn = ForgetPasses;
		BrowserNative.BeginRenderPassFn = BeginRenderPass;
		BrowserNative.RunPassFn = RunPass;
		BrowserNative.TranscodeIntoFn = TranscodeInto;
		BrowserNative.TranscodeStateFn = TranscodeState;
		BrowserNative.CopyTextureToTextureFn = CopyTextureToTexture;
		BrowserNative.XrEyeCopyFn = XrEyeCopy;
		BrowserNative.XrStereoFn = XrStereo;
		BrowserNative.GpuTakeFn = GpuTake;
		BrowserNative.DrawsTakeFn = DrawsTake;
		BrowserNative.XrLayeredFn = XrLayered;
		BrowserNative.XrTuneFn = XrTune;
		BrowserNative.XrNativeFn = XrNative;
		BrowserNative.CopyBufferFn = CopyBuffer;
		BrowserNative.FinishAndSubmitFn = FinishAndSubmit;
	}
}
