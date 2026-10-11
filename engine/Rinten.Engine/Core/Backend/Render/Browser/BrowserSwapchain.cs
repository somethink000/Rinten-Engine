using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Rinten;

//WebGPU surfaces, frames, views and draws; per-viewport buffers are made once, no per-frame allocs
internal static unsafe class BrowserSwapchain
{
	//A picture the renderer owns, and the handle a later pass names it by.
	private struct Picture
	{
		public int Image;
		public int View;
		public int Width;
		public int Height;
		public string Format;
		public ulong Adopted;

		/// <summary>Layers: two for a view drawing both headset eyes in one pass, one for every other picture.</summary>
		public int Layers;

		/// <summary>One, or four for a picture only drawn into and resolved - see Viewport.Samples.</summary>
		public int Samples;
	}

	private static void DropPicture( ref Picture picture )
	{
		if ( picture.Adopted != 0 ) BrowserTextures.Release( picture.Adopted );
		if ( picture.View != 0 ) BrowserNative.Release( picture.View );
		if ( picture.Image != 0 ) BrowserNative.Release( picture.Image );
		picture = default;
	}

	private static bool MakePicture( ref Picture picture, int width, int height, string format,
		int usage, string aspect = BrowserDevice.AspectAll, int layers = 1, int samples = 1 )
	{
		DropPicture( ref picture );

		picture.Image = BrowserDevice.MakeTexture( width, height, format, usage, 1, layers, samples );
		if ( picture.Image == 0 ) return false;

		picture.View = BrowserDevice.MakeView( picture.Image, format, aspect );
		if ( picture.View == 0 ) return false;

		picture.Width = width;
		picture.Height = height;
		picture.Format = format;
		picture.Layers = layers;
		picture.Samples = samples;
		return true;
	}

	//What one frame in flight writes into; two a viewport, so the frame the browser draws is not the one filled
	private sealed class FrameScratch
	{
		public int Frame;
		public int Bones;
		public int Dynamic;
		public int BonesGroup;
		public int DynamicUsed;
		public int PushUsed;
		public int BonesUsed;
		public byte[] FrameHost;

		/// <summary>The sun map's view the group was made with - see FrameGroup.</summary>
		public int GroupSun;

		/// <summary>The bake's sheet and volume views the group holds - see FrameGroup.</summary>
		public int GroupSheet, GroupVolume;

		/// <summary>The second eye's frame block (SecondFrameBinding); holds the first eye's copy until written.</summary>
		public int Second;

		/// <summary>A frame block and group for draws into the sun's map, the light's view in the camera's place.</summary>
		public int Aside;
		public int AsideGroup;
	}

	private sealed class Viewport
	{
		public int Width;
		public int Height;
		public bool Recording;
		public bool WorldBegun;
		public int CurrentTarget = BrowserDevice.Target.Screen;
		public ulong Presented;
		public readonly float[] Clear = [0, 0, 0, 1];

		/// <summary>The canvas context, which is what a surface is in a page.</summary>
		public int Surface;

		public int CurrentColor;
		public int CurrentColorView;
		public int RetiredColor;
		public int RetiredColorView;
		public int Encoder;
		public int Pass;

		public Picture Depth;
		public Picture Scene;
		public Picture SceneNormal;
		public Picture Screen;
		public ulong ScreenHandle;

		/// <summary>Samples the world's pass draws with: one for views, WantedSamples for the canvas.</summary>
		public int Samples = 1;
		public Picture Multisampled;

		/// <summary>Samples of the pass being recorded, which picks a pipeline's bake.</summary>
		public int PassSamples = 1;

		public readonly FrameScratch[] Scratch =
			[new FrameScratch(), new FrameScratch()];

		public int Frame;

		public ulong BoundTexture;
		public int BoundPipeline;

		/// <summary>What the pass was last told, so a draw does not say it again.</summary>
		public int BoundFrameGroup;
		public int BoundFrameOffset;
		public int BoundObjectGroup;
		public int BoundVertices;
		public int BoundIndices;
		public string BoundIndexFormat;

		/// <summary>The push group and the ring slot bound with it - see WritePush.</summary>
		public int BoundPushSet;
		public int BoundPushOffset = -1;

		/// <summary>The push bytes last written and their slot, for a draw with the same; -1 for none.</summary>
		public readonly byte[] LastPush = new byte[BrowserDevice.PushConstantSize];
		public int LastPushOffset = -1;

		/// <summary>Whether the draws read the light's frame block - see FrameUse.</summary>
		public bool UsingAside;
	}

	private static Viewport placeholder = new();
	private static Viewport active = placeholder;
	private static readonly Dictionary<ulong, Viewport> swapchains = new();
	private static readonly Dictionary<ulong, Viewport> views = new();
	private static ulong nextHandle = 1;

	private static Viewport Current => active;

	//Draws left out because their pipeline was still baking - see DrawCommon; only grows, callers compare
	internal static long DrawsWaiting;

	private static void EndPass()
	{
		if ( Current.Pass == 0 ) return;

		BrowserNative.EndPass( Current.Pass );
		Current.Pass = 0;
	}

	private static void DropView( ref int view )
	{
		if ( view != 0 ) BrowserNative.Release( view );
		view = 0;
	}

	//Lets a canvas texture go: forgotten, not destroyed - the page hands out a new one each frame
	private static void DropCanvasTexture( ref int image, ref int view )
	{
		DropView( ref view );
		if ( image != 0 ) BrowserNative.Forget( image );
		image = 0;
	}

	private static void DropSurfaceTexture( Viewport viewport )
	{
		DropCanvasTexture( ref viewport.CurrentColor, ref viewport.CurrentColorView );
		DropCanvasTexture( ref viewport.RetiredColor, ref viewport.RetiredColorView );
	}

	private static void RetireSurfaceTexture( Viewport viewport )
	{
		DropCanvasTexture( ref viewport.RetiredColor, ref viewport.RetiredColorView );
		viewport.RetiredColor = viewport.CurrentColor;
		viewport.RetiredColorView = viewport.CurrentColorView;
		viewport.CurrentColor = 0;
		viewport.CurrentColorView = 0;
	}

	private static void DropEncoder()
	{
		EndPass();
		DropSurfaceTexture( Current );

		if ( Current.Encoder != 0 )
		{
			BrowserNative.Release( Current.Encoder );
			Current.Encoder = 0;
		}
	}

	private static bool EnsureScratch( Viewport viewport )
	{
		var scratch = viewport.Scratch[viewport.Frame];
		if ( scratch.Frame != 0 ) return true;

		scratch.Frame = BrowserDevice.MakeBuffer( BrowserDevice.FrameBlockSize,
			BrowserDevice.BufferUse.Storage | BrowserDevice.BufferUse.CopyDst );
		scratch.Bones = BrowserDevice.MakeBuffer( (long)BrowserDevice.BoneBlockSize * BrowserDevice.BoneSlotsPerFrame,
			BrowserDevice.BufferUse.Storage | BrowserDevice.BufferUse.Uniform | BrowserDevice.BufferUse.CopyDst );
		scratch.Dynamic = BrowserDevice.MakeBuffer( BrowserDevice.DynamicScratch,
			BrowserDevice.BufferUse.Vertex | BrowserDevice.BufferUse.CopyDst );
		scratch.Aside = BrowserDevice.MakeBuffer( BrowserDevice.FrameBlockSize,
			BrowserDevice.BufferUse.Storage | BrowserDevice.BufferUse.CopyDst );
		scratch.Second = BrowserDevice.MakeBuffer( BrowserDevice.FrameBlockSize,
			BrowserDevice.BufferUse.Storage | BrowserDevice.BufferUse.CopyDst );
		scratch.FrameHost = new byte[BrowserDevice.FrameBlockSize];

		if ( scratch.Frame == 0 || scratch.Bones == 0 || scratch.Dynamic == 0
			|| scratch.Aside == 0 || scratch.Second == 0 ) return false;

		scratch.GroupSun = BrowserDevice.shadowView;
		scratch.GroupSheet = scratch.GroupVolume = BrowserDevice.noBakeView;
		scratch.BonesGroup = MakeBonesGroup( scratch, scratch.Frame, BrowserDevice.shadowView, BrowserDevice.noBakeView, BrowserDevice.noBakeView );
		scratch.AsideGroup = MakeBonesGroup( scratch, scratch.Aside, BrowserDevice.shadowView, BrowserDevice.noBakeView, BrowserDevice.noBakeView );
		return scratch.BonesGroup != 0 && scratch.AsideGroup != 0;
	}

	private static int MakeBonesGroup( FrameScratch scratch, int frame, int sun, int sheet, int volume )
		=> BrowserDevice.MakeBindGroup( BrowserDevice.boneSetLayout,
			BrowserDevice.Bind.Buffer( 0, scratch.Bones, BrowserDevice.BoneBlockSize ),
			BrowserDevice.Bind.Buffer( 1, frame, BrowserDevice.FrameBlockSize ),
			BrowserDevice.Bind.View( 2, sun ),
			BrowserDevice.Bind.Sampler( BrowserDevice.ShadowSamplerBinding, BrowserDevice.shadowSampler ),
			BrowserDevice.Bind.Sampler( BrowserDevice.FilterSamplerBinding, BrowserDevice.clampSampler ),
			BrowserDevice.Bind.View( 5, BrowserDevice.skyTableView ),
			BrowserDevice.Bind.View( BrowserDevice.BakedSheetBinding, sheet ),
			BrowserDevice.Bind.View( BrowserDevice.BakedVolumeBinding, volume ),

			//The second eye's block, read only by a pass drawing both; bound always so one layout serves both kinds,
			//holding this frame's own block until SetupSecondEye writes the other eye's
			BrowserDevice.Bind.Buffer( BrowserDevice.SecondFrameBinding,
				scratch.Second != 0 ? scratch.Second : frame, BrowserDevice.FrameBlockSize ) );

	private static void DropScratch( Viewport viewport )
	{
		viewport.LastPushOffset = -1;

		foreach ( var scratch in viewport.Scratch )
		{
			if ( scratch.BonesGroup != 0 ) BrowserNative.Release( scratch.BonesGroup );
			if ( scratch.AsideGroup != 0 ) BrowserNative.Release( scratch.AsideGroup );
			if ( scratch.Aside != 0 ) BrowserNative.Release( scratch.Aside );
			if ( scratch.Second != 0 ) BrowserNative.Release( scratch.Second );
			if ( scratch.Frame != 0 ) BrowserNative.Release( scratch.Frame );
			if ( scratch.Bones != 0 ) BrowserNative.Release( scratch.Bones );
			if ( scratch.Dynamic != 0 ) BrowserNative.Release( scratch.Dynamic );

			scratch.Frame = 0;
			scratch.Bones = 0;
			scratch.Dynamic = 0;
			scratch.BonesGroup = 0;
			scratch.AsideGroup = 0;
			scratch.Aside = 0;
			scratch.Second = 0;
			scratch.GroupSun = 0;
			scratch.GroupSheet = scratch.GroupVolume = 0;
			scratch.DynamicUsed = 0;
			scratch.PushUsed = 0;
			scratch.BonesUsed = 0;
			scratch.FrameHost = null;
		}
	}

	private static bool MakeTargets( Viewport viewport, int width, int height, bool colour, int layers = 1 )
	{
		const int colourUsage = BrowserDevice.TextureUse.RenderAttachment |
			BrowserDevice.TextureUse.TextureBinding | BrowserDevice.TextureUse.CopySrc;
		const int depthUsage = colourUsage;

		//A canvas's world drawn a sample at a time is a depth that is never read - on a page nothing reads the world's
		//depth, see CoreRender.DepthReadable - and a picture of the samples that is resolved into the screen's.
		var samples = colour ? 1 : viewport.Samples;

		if ( samples > 1 )
		{
			if ( !MakePicture( ref viewport.Depth, width, height, BrowserDevice.FormatDepth32,
					BrowserDevice.TextureUse.RenderAttachment, BrowserDevice.AspectDepth, 1, samples ) )
				return false;

			return MakePicture( ref viewport.Multisampled, width, height, BrowserDevice.ScreenFormat,
				BrowserDevice.TextureUse.RenderAttachment, BrowserDevice.AspectAll, 1, samples );
		}

		DropPicture( ref viewport.Multisampled );

		if ( !MakePicture( ref viewport.Depth, width, height, BrowserDevice.FormatDepth32, depthUsage,
				BrowserDevice.AspectDepth, layers ) )
			return false;

		viewport.Depth.Adopted = BrowserTextures.Adopt( viewport.Depth.View, true );

		if ( !colour ) return true;

		if ( !MakePicture( ref viewport.Scene, width, height, BrowserDevice.FormatRgba16Float, colourUsage,
				BrowserDevice.AspectAll, layers ) )
			return false;
		if ( !MakePicture( ref viewport.SceneNormal, width, height, BrowserDevice.FormatRgba8, colourUsage,
				BrowserDevice.AspectAll, layers ) )
			return false;

		viewport.Scene.Adopted = BrowserTextures.Adopt( viewport.Scene.View );
		viewport.SceneNormal.Adopted = BrowserTextures.Adopt( viewport.SceneNormal.View );
		return true;
	}

	private static void DropTargets( Viewport viewport )
	{
		DropPicture( ref viewport.Depth );
		DropPicture( ref viewport.Scene );
		DropPicture( ref viewport.SceneNormal );
		DropPicture( ref viewport.Screen );
		DropPicture( ref viewport.Multisampled );
		viewport.ScreenHandle = 0;
	}

	private static void DropViewport( Viewport viewport )
	{
		DropEncoder();
		DropSurfaceTexture( viewport );
		DropTargets( viewport );
		DropScratch( viewport );

		if ( viewport.Surface != 0 )
		{
			BrowserNative.UnconfigureContext( viewport.Surface );
			BrowserNative.Release( viewport.Surface );
		}

		viewport.Surface = 0;
	}

	//-- the canvas ------------------------------------------------------------------

	//The canvas the page put the game in. The window handle names the element where a C API
	//would take a CSS selector; zero is the one the launcher's page calls "canvas".
	private static int MakeCanvasSurface( Viewport viewport, string canvasId )
		=> BrowserNative.CanvasContext( string.IsNullOrEmpty( canvasId ) ? "canvas" : canvasId,
			viewport.Width, viewport.Height );

	private static void ConfigureSurface( Viewport viewport )
	{
		BrowserNative.ConfigureContext( viewport.Surface, viewport.Width, viewport.Height,
			BrowserDevice.CanvasFormat,
			BrowserDevice.TextureUse.RenderAttachment | BrowserDevice.TextureUse.CopyDst );
	}

	//WebGPU draws straight into the sRGB canvas view; WebGL2 keeps its picture plus a present pass
	private static bool DrawsOnCanvas => BrowserDevice.ShaderFormat != CoreShaderCompiler.Format.Glsl;

	private static bool MakeCanvasColor( Viewport viewport )
	{
		if ( DrawsOnCanvas )
		{
			DropPicture( ref viewport.Screen );
			return true;
		}

		return MakePicture( ref viewport.Screen, viewport.Width, viewport.Height, BrowserDevice.ScreenFormat,
			BrowserDevice.TextureUse.RenderAttachment | BrowserDevice.TextureUse.CopySrc );
	}

	private static bool AcquireSurface( Viewport viewport )
	{
		var texture = BrowserNative.CurrentTexture( viewport.Surface );
		if ( texture == 0 ) return false;

		viewport.CurrentColor = texture;
		viewport.CurrentColorView = BrowserNative.CreateView( texture, DrawsOnCanvas ? BrowserDevice.ScreenFormat : null,
			BrowserDevice.AspectAll, 0, 0 );
		return viewport.CurrentColorView != 0;
	}

	//The canvas's texture for this frame, which the world's pass then draws into - see DrawsOnCanvas.
	private static bool AcquireCanvas( Viewport viewport )
	{
		RetireSurfaceTexture( viewport );
		if ( AcquireSurface( viewport ) ) return true;

		ConfigureSurface( viewport );
		return AcquireSurface( viewport );
	}

	private static void PresentSurface( Viewport viewport )
	{
		EndPass();

		//Drawn where the browser shows it already.
		if ( DrawsOnCanvas ) return;

		if ( viewport.Surface == 0 || viewport.Encoder == 0 || viewport.Screen.Image == 0 ) return;

		//Chrome expires a canvas texture at the end of the JS call that acquired it if Submit is later.
		RetireSurfaceTexture( viewport );
		if ( !AcquireSurface( viewport ) )
		{
			ConfigureSurface( viewport );
			if ( !AcquireSurface( viewport ) ) return;
		}

		var width = Math.Min( viewport.Screen.Width, viewport.Width );
		var height = Math.Min( viewport.Screen.Height, viewport.Height );
		BrowserNative.CopyTextureToTexture( viewport.Encoder, viewport.Screen.Image, 0,
			viewport.CurrentColor, 0, width, height );
	}

	//-- passes ------------------------------------------------------------------------

	//A pass description the page has been told about, and the number it was told with - see PassToken.
	private readonly record struct PassShape( int Colour, int Colour2, int Depth, int Resolve,
		bool ClearColour, bool ClearDepth, float R, float G, float B, float A );

	//Pass descriptions the page already knows, small and walked linearly; avoids JSON per pass
	private static readonly PassShape[] passShapes = new PassShape[64];
	private static int passCount;

	private static void Number( StringBuilder json, float value )
		=> json.Append( value.ToString( "R", CultureInfo.InvariantCulture ) );

	//The number the page knows this pass description by, describing it first where it has not been described yet.
	private static int PassToken( int colour, int colour2, int depth, int resolve,
		ReadOnlySpan<float> clear, bool clearColour, bool clearDepth )
	{
		var shape = new PassShape( colour, colour2, depth, resolve, clearColour, clearDepth,
			clear[0], clear[1], clear[2], clear[3] );

		for ( var i = 0; i < passCount; i++ )
			if ( passShapes[i] == shape ) return i;

		//Full: everything is forgotten on both sides. It only fills when the views were remade - a resize, a lost
		//device - and those in use are described again on their next pass
		if ( passCount == passShapes.Length )
		{
			BrowserNative.ForgetPasses();
			passCount = 0;
		}

		var token = passCount++;
		passShapes[token] = shape;
		BrowserNative.DefinePass( token, PassJson( colour, colour2, depth, resolve, clear, clearColour, clearDepth ) );

		return token;
	}

	private static string PassJson( int colour, int colour2, int depth, int resolve,
		ReadOnlySpan<float> clear, bool clearColour, bool clearDepth )
	{
		var json = new StringBuilder( 320 );
		json.Append( "{\"colors\":[" );

		if ( colour != 0 )
		{
			json.Append( "{\"view\":" ).Append( colour )
				.Append( ",\"load\":\"" ).Append( clearColour ? "clear" : "load" )
				.Append( "\",\"store\":\"store\",\"clear\":[" );
			Number( json, clear[0] );
			json.Append( ',' );
			Number( json, clear[1] );
			json.Append( ',' );
			Number( json, clear[2] );
			json.Append( ',' );
			Number( json, clear[3] );
			json.Append( ']' );

			//Where the samples are averaged into when the pass closes. The samples themselves are kept: the
			//world's pass is opened again after a pass of another kind, and carries on from what it drew.
			if ( resolve != 0 ) json.Append( ",\"resolve\":" ).Append( resolve );
			json.Append( '}' );
		}

		if ( colour2 != 0 )
		{
			json.Append( colour != 0 ? "," : "" )
				.Append( "{\"view\":" ).Append( colour2 )
				.Append( ",\"load\":\"" ).Append( clearColour ? "clear" : "load" )
				.Append( "\",\"store\":\"store\",\"clear\":[0,0,0,0]}" );
		}

		json.Append( ']' );

		if ( depth != 0 )
		{
			json.Append( ",\"depth\":{\"view\":" ).Append( depth )
				.Append( ",\"depthLoad\":\"" ).Append( clearDepth ? "clear" : "load" )
				.Append( "\",\"depthStore\":\"store\",\"depthClear\":1}" );
		}

		json.Append( '}' );

		return json.ToString();
	}

	private static bool BeginPass( int colour, int colour2, int depth,
		ReadOnlySpan<float> clear, bool clearColour, bool clearDepth, int resolve = 0, int samples = 1 )
	{
		EndPass();

		if ( Current.Encoder == 0 ) return false;

		Current.PassSamples = samples;
		Current.Pass = BrowserNative.BeginRenderPass( Current.Encoder,
			PassToken( colour, colour2, depth, resolve, clear, clearColour, clearDepth ) );
		Current.BoundPipeline = 0;
		Current.BoundTexture = 0;
		ForgetBindings();
		return Current.Pass != 0;
	}

	//The pass a draw with nowhere else to go lands in: the canvas and its depth, cleared on a frame's first draw
	private static bool EnsureWorldPass()
	{
		if ( Current.Pass != 0 ) return true;
		if ( Current.Encoder == 0 ) return false;

		//Opened again after the sun's map, it keeps what the world drew rather than clearing it
		var first = !Current.WorldBegun;

		Current.WorldBegun = true;
		Current.CurrentTarget = BrowserDevice.Target.Screen;

		var colour = Current.Screen.View;
		if ( colour == 0 ) colour = Current.CurrentColorView;

		//Drawn into the samples and averaged into the screen's picture - see Viewport.Samples.
		if ( Current.Multisampled.View != 0 )
			return BeginPass( Current.Multisampled.View, 0, Current.Depth.View, Current.Clear, first, first,
				colour, Current.Multisampled.Samples );

		return BeginPass( colour, 0, Current.Depth.View, Current.Clear, first, first );
	}

	private static void Submit()
	{
		EndPass();
		if ( Current.Encoder == 0 ) return;

		BrowserNative.FinishAndSubmit( Current.Encoder );
		Current.Encoder = 0;

		//There is no present call in a page: the browser puts the canvas up itself when the task that drew into it ends.
		Current.Frame = ( Current.Frame + 1 ) % BrowserDevice.FramesInFlight;
	}

	private static Viewport Find( ulong handle )
	{
		if ( swapchains.TryGetValue( handle, out var found ) ) return found;
		if ( views.TryGetValue( handle, out found ) ) return found;
		return null;
	}

	private static int EndViewFrame( ulong handle )
	{
		if ( !views.TryGetValue( handle, out var viewport ) || !viewport.Recording ) return 0;

		if ( viewport.Pass == 0 && viewport.Encoder != 0 )
		{
			active = viewport;
			EnsureWorldPass();
		}

		Submit();
		viewport.Recording = false;
		viewport.Presented += 1;
		active = placeholder;
		return 1;
	}

	//-- what a draw does before it draws -----------------------------------------------

	//What a new pass has been told, which is nothing - see Viewport.BoundFrameGroup.
	private static void ForgetBindings()
	{
		Current.BoundFrameGroup = 0;
		Current.BoundFrameOffset = -1;
		Current.BoundObjectGroup = 0;
		Current.BoundVertices = 0;
		Current.BoundIndices = 0;
		Current.BoundIndexFormat = null;
		Current.BoundPushSet = 0;
		Current.BoundPushOffset = -1;
	}

	//The index buffer a draw wants, where the pass has not got it already.
	private static void SetIndices( int indices, string format, long bytes )
	{
		if ( indices == Current.BoundIndices && format == Current.BoundIndexFormat ) return;

		BrowserNative.SetIndexBuffer( Current.Pass, indices, format, 0, bytes );
		Current.BoundIndices = indices;
		Current.BoundIndexFormat = format;
	}

	internal static void BindMaterial( ulong texture ) => Current.BoundTexture = texture;

	internal static void BindObject( ulong geometry, ulong data )
	{
		var set = BrowserBuffers.ObjectSetGet( geometry, data );
		if ( set == 0 || Current.Pass == 0 ) return;

		//A pulled draw's own vertices, not what the last draw left at group two, so that memory stops being true
		BrowserNative.SetBindGroup( Current.Pass, 2, set, -1 );
		Current.BoundObjectGroup = set;
	}

	//The one slot the push constants of a draw are copied into before it.
	private static readonly byte[] pushSlot = new byte[BrowserDevice.PushConstantSize];

	//Per-draw constants via a ring of dynamic uniform offsets (no push constants); stops when full
	internal static void WritePush( byte* data, int size )
	{
		var set = BrowserTextures.SetGet( Current.BoundTexture );
		if ( set == 0 || Current.Pass == 0 || BrowserDevice.pushRing == 0 ) return;

		var slot = pushSlot.AsSpan();
		slot.Clear();

		if ( data is not null && size > 0 )
			new ReadOnlySpan<byte>( data, Math.Min( size, BrowserDevice.PushConstantSize ) ).CopyTo( slot );

		int offset;

		if ( Current.LastPushOffset >= 0 && slot.SequenceEqual( Current.LastPush ) )
		{
			//The last draw's bytes, still in their slot: nothing is overwritten within a frame, so this draw reads them
			offset = Current.LastPushOffset;
		}
		else
		{
			var scratch = Current.Scratch[Current.Frame];
			if ( scratch.PushUsed / BrowserDevice.PushAlign >= BrowserDevice.PushSlotsPerFrame ) return;

			offset = ( Current.Frame * BrowserDevice.PushAlign * BrowserDevice.PushSlotsPerFrame ) + scratch.PushUsed;
			scratch.PushUsed += BrowserDevice.PushAlign;

			BrowserNative.WriteBuffer( BrowserDevice.pushRing, offset, slot );
			slot.CopyTo( Current.LastPush );
			Current.LastPushOffset = offset;
		}

		if ( set != Current.BoundPushSet || offset != Current.BoundPushOffset )
		{
			BrowserNative.SetBindGroup( Current.Pass, 0, set, offset );
			Current.BoundPushSet = set;
			Current.BoundPushOffset = offset;
		}
	}

	//Writes a pose to its own slot in the frame's ring; returns slot + 1, 0 means none (as on Vulkan)
	internal static uint Bones( float* bones, int count )
	{
		if ( !Current.Recording || bones is null || count <= 0 ) return 0;

		if ( count > BrowserDevice.MaxBones )
		{
			BrowserDevice.infoText = "more bones than a skinned draw can carry";
			return 0;
		}

		var scratch = Current.Scratch[Current.Frame];
		if ( scratch.BonesUsed / BrowserDevice.BoneBlockSize >= BrowserDevice.BoneSlotsPerFrame ) return 0;

		var offset = scratch.BonesUsed;
		scratch.BonesUsed += BrowserDevice.BoneBlockSize;

		BrowserNative.WriteBuffer( scratch.Bones, offset, new Span<byte>( bones, count * 64 ) );
		return (uint)offset + 1;
	}

	private static int DrawCommon( ulong pipeline, ulong texture, byte* push, int pushSize, int bonesAt = 0 )
	{
		if ( !Current.Recording || BrowserDevice.Lost ) return 0;

		if ( !EnsureWorldPass() ) return 0;

		var baked = BrowserPipelines.Get( pipeline, Current.PassSamples );
		if ( baked == 0 )
		{
			//Not drawn because the browser has not finished it yet - which a pass whose result is kept
			//has to know, or it keeps a picture missing the draw for good. See CorePortableSun.Draw.
			if ( BrowserPipelines.Baking( pipeline, Current.PassSamples ) ) DrawsWaiting++;
			return 0;
		}

		if ( Current.BoundPipeline != baked )
		{
			BrowserNative.SetPipeline( Current.Pass, baked );
			Current.BoundPipeline = baked;
		}

		var scratch = Current.Scratch[Current.Frame];
		var frameGroup = Current.UsingAside ? scratch.AsideGroup : FrameGroup( scratch );

		if ( frameGroup != Current.BoundFrameGroup || bonesAt != Current.BoundFrameOffset )
		{
			BrowserNative.SetBindGroup( Current.Pass, 1, frameGroup, bonesAt );
			Current.BoundFrameGroup = frameGroup;
			Current.BoundFrameOffset = bonesAt;
		}

		if ( BrowserDevice.defaultObject != 0 && BrowserDevice.defaultObject != Current.BoundObjectGroup )
		{
			BrowserNative.SetBindGroup( Current.Pass, 2, BrowserDevice.defaultObject, -1 );
			Current.BoundObjectGroup = BrowserDevice.defaultObject;
		}

		//Every group a pipeline declares is bound for every draw, a picture or not: WebGPU refuses a draw with one missing,
		//and the push block rides in group nought. A draw that names no picture - a full-screen pass - gets the white one.
		BindMaterial( texture != 0 ? texture : BrowserTextures.Plain() );
		WritePush( push, pushSize );

		return 1;
	}

	//The frame's group, made again when the sun's map or the bake's pictures it holds are not the ones there are now -
	//the stand-ins until the portable frame first asks for a map, and while there is no bake
	private static int FrameGroup( FrameScratch scratch )
	{
		var sun = sunMap.View != 0 ? sunMap.View : BrowserDevice.shadowView;
		var sheet = ViewOr( CoreBakedLighting.PortableSheet );
		var volume = ViewOr( CoreBakedLighting.PortableVolume );

		if ( scratch.GroupSun == sun && scratch.GroupSheet == sheet && scratch.GroupVolume == volume ) return scratch.BonesGroup;

		var made = MakeBonesGroup( scratch, scratch.Frame, sun, sheet, volume );
		if ( made == 0 ) return scratch.BonesGroup;

		if ( scratch.BonesGroup != 0 ) BrowserNative.Release( scratch.BonesGroup );

		scratch.BonesGroup = made;
		scratch.GroupSun = sun;
		scratch.GroupSheet = sheet;
		scratch.GroupVolume = volume;
		return made;
	}

	private static int ViewOr( ulong texture )
	{
		var view = texture != 0 ? BrowserTextures.ViewOf( texture ) : 0;
		return view != 0 ? view : BrowserDevice.noBakeView;
	}

	//-- the portable frame's sun ----------------------------------------------------------

	//Texels across the sun's map - see CorePortableSun.
	internal const int PortableSunSide = 2048;

	//The one sun map, the device's own and in every frame group - the same shape as VkDevice.portableSunMap,
	//for the same reason: the portable frame has no table of pictures to find a drawer's own in by number.
	private static Picture sunMap;
	private static ulong sunMapHandle;

	//The portable frame's sky table onto the device (CoreSkyTable, g_skyTable); the picture is already in
	//every frame group, so a send is only the write
	internal static void SendSkyTable( byte* texels, int side )
	{
		if ( !BrowserDevice.Ready || BrowserDevice.skyTableImage == 0 ) return;
		if ( side != BrowserDevice.SkyTableSide ) return;

		BrowserNative.WriteTexture( BrowserDevice.skyTableImage, 0, 0, 0, side, side, side * 8,
			new Span<byte>( texels, side * side * 8 ) );
	}

	internal static ulong PortableSunMap()
	{
		if ( sunMapHandle != 0 ) return sunMapHandle;
		if ( !BrowserDevice.Ready ) return 0;

		if ( !MakePicture( ref sunMap, PortableSunSide, PortableSunSide, BrowserDevice.FormatDepth32,
				BrowserDevice.TextureUse.RenderAttachment | BrowserDevice.TextureUse.TextureBinding, BrowserDevice.AspectDepth ) )
			return 0;

		sunMapHandle = nextHandle++;
		return sunMapHandle;
	}

	//Begins drawing into the sun's map, cleared - the whole of it, every time.
	internal static int ShadowBegin( ulong handle, int layer )
	{
		if ( !Current.Recording || handle == 0 || handle != sunMapHandle || layer != 0 ) return 0;

		Span<float> none = [0, 0, 0, 0];
		return BeginPass( 0, 0, sunMap.View, none, false, true ) ? 1 : 0;
	}

	//A second frame block the draws read once FrameUse says so - see VkFrame.FrameBlockPut. One a frame: a page
	//writes a buffer before anything recorded runs, so a second would be the first again
	internal static ulong FramePut( float* data, int floatCount )
	{
		if ( !Current.Recording || data is null || floatCount <= 0 ) return 0;

		var scratch = Current.Scratch[Current.Frame];
		if ( scratch.Aside == 0 ) return 0;

		var bytes = Math.Min( floatCount * sizeof( float ), BrowserDevice.FrameBlockSize );
		BrowserNative.WriteBuffer( scratch.Aside, 0, new Span<byte>( data, bytes ) );
		return 1;
	}

	//Which block the draws read from here on - nought the frame's own - and which they read before.
	internal static ulong FrameUse( ulong which )
	{
		var was = Current.UsingAside ? 1ul : 0ul;
		Current.UsingAside = which != 0;
		return was;
	}

	//-- the seam ------------------------------------------------------------------------

	internal static void DestroySwapchain()
	{
		foreach ( var pair in swapchains ) DropViewport( pair.Value );
		foreach ( var pair in views ) DropViewport( pair.Value );

		DropPicture( ref sunMap );
		sunMapHandle = 0;

		swapchains.Clear();
		views.Clear();
		active = placeholder = new Viewport();

		//Every view the descriptions named is gone, so the page is told to forget them - see PassToken.
		if ( passCount > 0 )
		{
			BrowserNative.ForgetPasses();
			passCount = 0;
		}
	}

	internal static ulong SwapchainCreate( int kind, ulong window, int width, int height )
	{
		if ( !BrowserDevice.Ready )
		{
			BrowserDevice.infoText = "there is no device to make a swapchain on";
			return 0;
		}

		if ( kind != WindowCanvas )
		{
			BrowserDevice.infoText = "that kind of window is not supported on this platform";
			return 0;
		}

		if ( width <= 0 || height <= 0 )
		{
			BrowserDevice.infoText = "a swapchain needs a size";
			return 0;
		}

		var made = new Viewport { Width = width, Height = height, Samples = WantedSamples };
		made.Surface = MakeCanvasSurface( made, null );

		if ( made.Surface == 0 )
		{
			BrowserDevice.infoText = "the canvas would not become a surface";
			return 0;
		}

		ConfigureSurface( made );
		if ( !MakeTargets( made, made.Width, made.Height, false ) || !MakeCanvasColor( made )
			|| !EnsureScratch( made ) )
		{
			DropViewport( made );
			BrowserDevice.infoText = "the per-frame objects would not be made";
			return 0;
		}

		BrowserDevice.infoText = $"{BrowserDevice.deviceNameText}, {( BrowserDevice.ShaderFormat == CoreShaderCompiler.Format.Glsl ? "webgl2" : "webgpu" )}, {made.Width}x{made.Height}";

		var handle = nextHandle++;
		swapchains[handle] = made;
		return handle;
	}

	//The canvas's anti-aliasing (RenderSettings.AntiAliasQuality → DrawSettings.Msaa), none by default; four samples,
	//the one count WebGPU promises besides one, or WebGL2's most up to that
	private static int WantedSamples => Rinten.Rendering.DrawSettings.Msaa >= 2 ? 4 : 1;

	//Matches sbc_window_kind: the browser's canvas.
	private const int WindowCanvas = 5;

	internal static int SwapchainResize( ulong handle, int width, int height )
	{
		var viewport = Find( handle );
		if ( viewport is null || width <= 0 || height <= 0 ) return 0;
		if ( viewport.Recording ) return 0;

		//The canvas texture from the size that was is not one this frame may draw into, whatever else changes.
		DropSurfaceTexture( viewport );
		if ( viewport.Width == width && viewport.Height == height ) return 1;

		viewport.Width = width;
		viewport.Height = height;

		if ( viewport.Surface != 0 )
		{
			ConfigureSurface( viewport );
			return MakeTargets( viewport, width, height, false ) && MakeCanvasColor( viewport ) ? 1 : 0;
		}

		return MakeTargets( viewport, width, height, true ) ? 1 : 0;
	}

	//There is one way a page presents and no choice about it, so this only says yes.
	internal static int SwapchainVsync( ulong handle, int enabled ) => Find( handle ) is not null ? 1 : 0;

	internal static void SwapchainDestroy( ulong handle )
	{
		if ( !swapchains.TryGetValue( handle, out var viewport ) ) return;

		if ( ReferenceEquals( active, viewport ) ) active = placeholder;
		DropViewport( viewport );
		swapchains.Remove( handle );
	}

	internal static void SwapchainInfo( ulong handle, int* outInfo )
	{
		if ( outInfo is null ) return;

		var viewport = Find( handle );
		outInfo[0] = viewport?.Width ?? 0;
		outInfo[1] = viewport?.Height ?? 0;
		outInfo[2] = viewport is not null ? 2 : 0;
		outInfo[3] = 1;
	}

	internal static int FrameBegin( ulong handle, float r, float g, float b, float a )
	{
		if ( !swapchains.TryGetValue( handle, out var viewport ) ) return 0;
		if ( placeholder.Recording || viewport.Recording ) return 0;

		active = viewport;
		if ( !EnsureScratch( viewport ) )
		{
			active = placeholder;
			return 0;
		}

		//The setting changed since the last frame: the pictures it shapes are made again, as a resize makes them.
		if ( viewport.Samples != WantedSamples )
		{
			viewport.Samples = WantedSamples;
			if ( !MakeTargets( viewport, viewport.Width, viewport.Height, false ) ) return 0;
		}

		BrowserDevice.RefreshLost();

		if ( DrawsOnCanvas && viewport.Surface != 0 && !AcquireCanvas( viewport ) )
		{
			active = placeholder;
			return 0;
		}

		viewport.Encoder = BrowserNative.CreateCommandEncoder();
		viewport.Recording = true;
		viewport.WorldBegun = false;
		viewport.UsingAside = false;
		viewport.CurrentTarget = BrowserDevice.Target.Screen;
		viewport.Clear[0] = r;
		viewport.Clear[1] = g;
		viewport.Clear[2] = b;
		viewport.Clear[3] = a;
		viewport.Scratch[viewport.Frame].DynamicUsed = 0;
		viewport.Scratch[viewport.Frame].PushUsed = 0;
		viewport.LastPushOffset = -1;
		viewport.Scratch[viewport.Frame].BonesUsed = 0;
		return viewport.Encoder != 0 ? 1 : 0;
	}

	//The frame block: the camera, the sun, the cascades, the lights. Clamped to the block rather than growing it.
	internal static int FrameSetup( float* data, int floatCount )
	{
		if ( !Current.Recording || data is null || floatCount <= 0 ) return 0;

		var scratch = Current.Scratch[Current.Frame];
		if ( scratch.FrameHost is null ) return 0;

		var bytes = Math.Min( floatCount * sizeof( float ), BrowserDevice.FrameBlockSize );
		new ReadOnlySpan<byte>( data, bytes ).CopyTo( scratch.FrameHost );
		BrowserNative.WriteBuffer( scratch.Frame, 0, scratch.FrameHost );
		return 1;
	}

	//Whole frame block for the second eye in stereo passes; see BrowserDevice.SecondFrameBinding
	internal static int FrameSetupSecondEye( float* data, int floatCount )
	{
		if ( !Current.Recording || data is null || floatCount <= 0 ) return 0;

		var scratch = Current.Scratch[Current.Frame];
		if ( scratch.FrameHost is null || scratch.Second == 0 ) return 0;

		var bytes = Math.Min( floatCount * sizeof( float ), BrowserDevice.FrameBlockSize );

		//Into the same host array: a staging copy, and FrameSetup has already sent the first eye's block from it
		new ReadOnlySpan<byte>( data, bytes ).CopyTo( scratch.FrameHost );
		BrowserNative.WriteBuffer( scratch.Second, 0, scratch.FrameHost );
		return 1;
	}

	//A run of the frame block written again part way through the frame - see VkFrame.FrameBlockPatch.
	internal static int FramePatch( int floatOffset, float* data, int floatCount )
	{
		if ( !Current.Recording || data is null || floatCount <= 0 || floatOffset < 0 ) return 0;

		var scratch = Current.Scratch[Current.Frame];
		if ( scratch.FrameHost is null ) return 0;

		var at = floatOffset * sizeof( float );
		var bytes = floatCount * sizeof( float );

		if ( at + bytes > scratch.FrameHost.Length ) return 0;

		new ReadOnlySpan<byte>( data, bytes ).CopyTo( scratch.FrameHost.AsSpan( at ) );

		//Only the run that changed: the rest of the block on the device is what FrameSetup
		//sent, and the whole four and a half kilobytes went again for a patch of a few floats.
		BrowserNative.WriteBuffer( scratch.Frame, at, scratch.FrameHost.AsSpan( at, bytes ) );
		return 1;
	}

	internal static int TargetBegin( int target, float r, float g, float b, float a, int clear )
	{
		if ( !Current.Recording || Current.WorldBegun ) return 0;

		Current.CurrentTarget = target;
		Span<float> colour = [r, g, b, a];

		if ( target == BrowserDevice.Target.Scene )
			return BeginPass( Current.Scene.View, Current.SceneNormal.View, Current.Depth.View,
				colour, clear != 0, clear != 0 ) ? 1 : 0;
		if ( target == BrowserDevice.Target.Depth )
			return BeginPass( 0, 0, Current.Depth.View, colour, false, clear != 0 ) ? 1 : 0;

		return 0;
	}

	internal static void TargetEnd() => EndPass();

	//Ends the pass so the depth it wrote can be read; this backend needs no barrier for that
	internal static int DepthReadable()
	{
		EndPass();
		return Current.Recording ? 1 : 0;
	}

	internal static ulong TargetTexture( ulong viewport, int target, int which )
	{
		var one = Find( viewport );
		if ( one is null ) return 0;

		if ( target == BrowserDevice.Target.Scene )
		{
			if ( which == 1 ) return one.SceneNormal.Adopted;
			if ( which == 2 ) return one.Depth.Adopted;
			return one.Scene.Adopted;
		}

		if ( target == BrowserDevice.Target.Depth ) return one.Depth.Adopted;
		return one.ScreenHandle != 0 ? one.ScreenHandle : one.Scene.Adopted;
	}

	internal static int FrameEnd()
	{
		if ( !Current.Recording || ReferenceEquals( active, placeholder ) ) return 0;

		if ( Current.Pass == 0 && Current.Encoder != 0 )
			EnsureWorldPass();

		PresentSurface( Current );
		Submit();
		Current.Recording = false;
		Current.Presented += 1;
		active = placeholder;
		return 1;
	}

	internal static ulong FrameCount( ulong handle ) => Find( handle )?.Presented ?? 0;

	//What a frame cost, as far as a page can say: nothing of the GPU, and what the renderer holds
	internal static int ProfileStats( ulong viewport, float* outValues, int count )
	{
		if ( outValues is null || count <= 0 ) return 0;
		if ( Find( viewport ) is null ) return 0;

		Span<float> values =
		[
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			BrowserDevice.trackedTextureBytes, BrowserDevice.trackedBufferBytes,
			BrowserDevice.trackedTextureCount, BrowserDevice.trackedBufferCount,
		];

		var n = Math.Min( count, values.Length );
		for ( var i = 0; i < n; i++ ) outValues[i] = values[i];
		return n;
	}

	//-- views -----------------------------------------------------------------------------

	//A view of one or two layers; two uses OVR_multiview2 to draw both eyes, refused if unsupported
	internal static ulong ViewCreate( int width, int height, int layers = 1 )
	{
		if ( !BrowserDevice.Ready || width <= 0 || height <= 0 || layers < 1 || layers > 2 ) return 0;

		//Asked for two and the browser cannot: told no, rather than one eye's picture drawn into both
		if ( layers > 1 && BrowserNative.XrStereo() == 0 ) return 0;

		var made = new Viewport { Width = width, Height = height };
		if ( !MakeTargets( made, width, height, true, layers ) ) return 0;
		if ( !MakePicture( ref made.Screen, width, height, BrowserDevice.ScreenFormat,
				BrowserDevice.TextureUse.RenderAttachment | BrowserDevice.TextureUse.TextureBinding |
				BrowserDevice.TextureUse.CopySrc, BrowserDevice.AspectAll, layers ) )
			return 0;

		made.ScreenHandle = BrowserTextures.Adopt( made.Screen.View );
		if ( !EnsureScratch( made ) ) return 0;

		var handle = nextHandle++;
		views[handle] = made;
		return handle;
	}

	internal static void ViewDestroy( ulong handle )
	{
		if ( !views.TryGetValue( handle, out var viewport ) ) return;

		if ( ReferenceEquals( active, viewport ) ) active = placeholder;
		DropViewport( viewport );
		views.Remove( handle );
	}

	internal static int ViewResize( ulong handle, int width, int height )
	{
		if ( !views.TryGetValue( handle, out var viewport ) || width <= 0 || height <= 0 ) return 0;

		//However many layers it was made with: a view that draws both eyes keeps drawing both after a resize.
		var layers = Math.Max( 1, viewport.Screen.Layers );

		viewport.Width = width;
		viewport.Height = height;
		if ( !MakeTargets( viewport, width, height, true, layers ) ) return 0;
		if ( !MakePicture( ref viewport.Screen, width, height, BrowserDevice.ScreenFormat,
				BrowserDevice.TextureUse.RenderAttachment | BrowserDevice.TextureUse.TextureBinding |
				BrowserDevice.TextureUse.CopySrc, BrowserDevice.AspectAll, layers ) )
			return 0;

		viewport.ScreenHandle = BrowserTextures.Adopt( viewport.Screen.View );
		return 1;
	}

	//A view drawn inside somebody else's frame, on their encoder.
	internal static int ViewBegin( ulong handle, float r, float g, float b, float a )
	{
		if ( !views.TryGetValue( handle, out var viewport ) || !Current.Recording ) return 0;

		active = viewport;
		viewport.Recording = true;
		viewport.Clear[0] = r;
		viewport.Clear[1] = g;
		viewport.Clear[2] = b;
		viewport.Clear[3] = a;
		viewport.WorldBegun = false;
		return 1;
	}

	internal static void ViewEnd()
	{
		EndPass();
		if ( !ReferenceEquals( active, placeholder ) ) active.Recording = false;
		active = placeholder;
	}

	internal static int ViewFrameBegin( ulong handle, float r, float g, float b, float a )
	{
		if ( placeholder.Recording ) return 0;
		if ( !views.TryGetValue( handle, out var viewport ) ) return 0;

		active = viewport;
		if ( !EnsureScratch( viewport ) )
		{
			active = placeholder;
			return 0;
		}

		BrowserDevice.RefreshLost();
		viewport.Encoder = BrowserNative.CreateCommandEncoder();
		viewport.Recording = true;
		viewport.WorldBegun = false;
		viewport.Clear[0] = r;
		viewport.Clear[1] = g;
		viewport.Clear[2] = b;
		viewport.Clear[3] = a;
		viewport.Scratch[viewport.Frame].DynamicUsed = 0;
		viewport.Scratch[viewport.Frame].PushUsed = 0;
		viewport.LastPushOffset = -1;
		viewport.Scratch[viewport.Frame].BonesUsed = 0;
		return viewport.Encoder != 0 ? 1 : 0;
	}

	//Copies the view into one headset eye (token = eye + 1); ends the current pass first
	internal static int ViewCopyTo( ulong handle, ulong image, int width, int height, int layer = 0 )
	{
		if ( image == 0 ) return 0;
		if ( !views.TryGetValue( handle, out var viewport ) || viewport.Screen.Image == 0 ) return 0;
		if ( layer < 0 || layer >= Math.Max( 1, viewport.Screen.Layers ) ) return 0;

		EndPass();

		return BrowserNative.XrEyeCopy( viewport.Screen.Image, (int)image - 1, layer );
	}

	internal static int ViewFrameEnd( ulong handle ) => EndViewFrame( handle );

	internal static int ViewFrameEndAsync( ulong handle ) => EndViewFrame( handle );

	//Nothing here waits for a fence, so nothing here is ever busy.
	internal static int ViewFrameReady( ulong handle ) => 1;

	internal static ulong ViewTexture( ulong handle )
		=> views.TryGetValue( handle, out var viewport ) ? viewport.ScreenHandle : 0;

	//A readback is a promise in a page and cannot answer inside a frame: nothing is read, and the caller is told
	internal static int ViewRead( ulong handle, byte* outPixels, int capacity ) => 0;

	internal static int ViewReadLayered( ulong handle, byte* outPixels, int capacity ) => 0;

	//-- draws --------------------------------------------------------------------------------

	internal static int Draw( ulong pipeline, int vertexCount, void* push, int pushSize, ulong texture )
	{
		if ( DrawCommon( pipeline, texture, (byte*)push, pushSize ) == 0 ) return 0;

		BrowserNative.Draw( Current.Pass, vertexCount, 1, 0, 0 );
		return 1;
	}

	//One-draw vertices in the frame's ring; wraps to 0 when full instead of growing
	internal static int DrawDynamic( ulong pipeline, byte* vertexData, int vertexCount, int vertexStride,
		ulong texture, byte* pushData, int pushSize )
	{
		if ( vertexData is null || vertexCount <= 0 || vertexStride <= 0 ) return 0;
		if ( DrawCommon( pipeline, texture, pushData, pushSize ) == 0 ) return 0;

		var bytes = vertexCount * vertexStride;
		var scratch = Current.Scratch[Current.Frame];
		var aligned = ( bytes + 3 ) & ~3;
		if ( scratch.DynamicUsed + aligned > BrowserDevice.DynamicScratch ) scratch.DynamicUsed = 0;

		var offset = scratch.DynamicUsed;
		BrowserNative.WriteBuffer( scratch.Dynamic, offset, new Span<byte>( vertexData, bytes ) );
		scratch.DynamicUsed += aligned;

		BrowserNative.SetVertexBuffer( Current.Pass, 0, scratch.Dynamic, offset, bytes );

		//Slot nought holds the ring now, not the buffer remembered there: a mesh drawn next out of that buffer must bind it again, not read the ring.
		Current.BoundVertices = 0;

		BrowserNative.Draw( Current.Pass, vertexCount, 1, 0, 0 );
		return 1;
	}

	//A draw out of buffers that already exist - see CoreBufferDraw. On a page, the vertices bound and drawn in order
	//or by indices; not arguments the card wrote, nor buffers a shader reads at set 2, which a page has no way to.
	internal static int DrawBuffers( CoreBufferDraw* draw, byte* pushData, int pushSize, ulong* buffers, int bufferCount )
	{
		if ( draw is null || draw->Arguments != 0 || bufferCount > 0 || draw->Geometry != 0 ) return 0;

		var vertices = BrowserBuffers.Get( draw->Vertices );
		if ( vertices == 0 || draw->Count <= 0 ) return 0;

		var indices = draw->Indices != 0 ? BrowserBuffers.Get( draw->Indices ) : 0;
		if ( draw->Indices != 0 && indices == 0 ) return 0;

		if ( DrawCommon( draw->Pipeline, draw->Texture, pushData, pushSize ) == 0 ) return 0;

		if ( vertices != Current.BoundVertices )
		{
			BrowserNative.SetVertexBuffer( Current.Pass, 0, vertices, 0, BrowserBuffers.BytesOf( draw->Vertices ) );
			Current.BoundVertices = vertices;
		}

		var instances = Math.Max( draw->Instances, 1 );

		if ( indices != 0 )
		{
			SetIndices( indices, draw->WideIndices ? "uint32" : "uint16", BrowserBuffers.BytesOf( draw->Indices ) );
			BrowserNative.DrawIndexed( Current.Pass, draw->Count, instances, draw->First, draw->BaseVertex, 0 );
		}
		else
		{
			BrowserNative.Draw( Current.Pass, draw->Count, instances, draw->First, 0 );
		}

		return 1;
	}

	internal static int DrawIndexed( ulong pipeline, ulong vertexBuffer, ulong indexBuffer,
		int firstIndex, int indexCount, int firstVertex, ulong texture, byte* pushData, int pushSize )
	{
		var vertices = BrowserBuffers.Get( vertexBuffer );
		var indices = BrowserBuffers.Get( indexBuffer );
		if ( vertices == 0 || indices == 0 ) return 0;
		if ( DrawCommon( pipeline, texture, pushData, pushSize ) == 0 ) return 0;

		if ( vertices != Current.BoundVertices )
		{
			BrowserNative.SetVertexBuffer( Current.Pass, 0, vertices, 0, BrowserBuffers.BytesOf( vertexBuffer ) );
			Current.BoundVertices = vertices;
		}

		SetIndices( indices, "uint32", BrowserBuffers.BytesOf( indexBuffer ) );

		BrowserNative.DrawIndexed( Current.Pass, indexCount, 1, firstIndex, firstVertex, 0 );
		return 1;
	}

	internal static int DrawPulled( ulong pipeline, ulong geometryBuffer, ulong dataBuffer, int vertexCount,
		ulong texture, byte* pushData, int pushSize )
	{
		if ( DrawCommon( pipeline, texture, pushData, pushSize ) == 0 ) return 0;

		BindObject( geometryBuffer, dataBuffer );
		BrowserNative.Draw( Current.Pass, vertexCount, 1, 0, 0 );
		return 1;
	}

	//A run of copies of one mesh, drawn once: their numbers in the ring slot the frame's group is bound at
	internal static int DrawIndexedInstancedBlock( ulong pipeline, ulong vertexBuffer, ulong indexBuffer,
		int indexCount, int instanceCount, uint block, int firstIndex, int firstVertex,
		ulong texture, byte* pushData, int pushSize )
	{
		var vertices = BrowserBuffers.Get( vertexBuffer );
		var indices = BrowserBuffers.Get( indexBuffer );
		if ( vertices == 0 || indices == 0 || indexCount <= 0 || instanceCount <= 0 ) return 0;
		if ( block == 0 || block - 1 >= (uint)Current.Scratch[Current.Frame].BonesUsed ) return 0;

		if ( DrawCommon( pipeline, texture, pushData, pushSize, (int)( block - 1 ) ) == 0 ) return 0;

		if ( vertices != Current.BoundVertices )
		{
			BrowserNative.SetVertexBuffer( Current.Pass, 0, vertices, 0, BrowserBuffers.BytesOf( vertexBuffer ) );
			Current.BoundVertices = vertices;
		}

		SetIndices( indices, "uint32", BrowserBuffers.BytesOf( indexBuffer ) );

		BrowserNative.DrawIndexed( Current.Pass, indexCount, instanceCount, firstIndex, firstVertex, 0 );
		return 1;
	}

	//An instanced draw whose instances are read out of a slot Bones filled this frame - see VkFrame.DrawBlockInstanced.
	internal static int DrawBlockInstanced( ulong pipeline, ulong indexBuffer, int indexCount, int instanceCount,
		bool wideIndices, ulong texture, uint block, byte* pushData, int pushSize )
	{
		var indices = BrowserBuffers.Get( indexBuffer );
		if ( indices == 0 || indexCount <= 0 || instanceCount <= 0 ) return 0;
		if ( block == 0 || block - 1 >= (uint)Current.Scratch[Current.Frame].BonesUsed ) return 0;

		if ( DrawCommon( pipeline, texture, pushData, pushSize, (int)( block - 1 ) ) == 0 ) return 0;

		SetIndices( indices, wideIndices ? "uint32" : "uint16", BrowserBuffers.BytesOf( indexBuffer ) );
		BrowserNative.DrawIndexed( Current.Pass, indexCount, instanceCount, 0, 0, 0 );
		return 1;
	}

	internal static int DrawSkinned( ulong pipeline, ulong vertexBuffer, ulong skinBuffer, ulong indexBuffer,
		int firstIndex, int indexCount, int firstVertex, ulong texture, float* bones, int boneCount,
		byte* pushData, int pushSize )
	{
		var pose = Bones( bones, boneCount );
		if ( pose == 0 ) return 0;

		return DrawSkinnedAt( pipeline, vertexBuffer, skinBuffer, indexBuffer, firstIndex, indexCount, firstVertex,
			texture, pose, pushData, pushSize );
	}

	//A skinned draw in a pose Bones already put in this frame's ring.
	internal static int DrawSkinnedAt( ulong pipeline, ulong vertexBuffer, ulong skinBuffer, ulong indexBuffer,
		int firstIndex, int indexCount, int firstVertex, ulong texture, uint pose,
		byte* pushData, int pushSize )
	{
		var vertices = BrowserBuffers.Get( vertexBuffer );
		var skin = BrowserBuffers.Get( skinBuffer );
		var indices = BrowserBuffers.Get( indexBuffer );
		if ( vertices == 0 || indices == 0 ) return 0;

		//It has to name a pose this frame wrote - see VkFrame.DrawSkinnedAt.
		if ( pose == 0 || pose - 1 >= (uint)Current.Scratch[Current.Frame].BonesUsed ) return 0;

		if ( DrawCommon( pipeline, texture, pushData, pushSize, (int)( pose - 1 ) ) == 0 ) return 0;

		BrowserNative.SetVertexBuffer( Current.Pass, 0, vertices, 0, BrowserBuffers.BytesOf( vertexBuffer ) );

		if ( skin != 0 )
			BrowserNative.SetVertexBuffer( Current.Pass, 1, skin, 0, BrowserBuffers.BytesOf( skinBuffer ) );

		BrowserNative.SetIndexBuffer( Current.Pass, indices, "uint32", 0, BrowserBuffers.BytesOf( indexBuffer ) );
		BrowserNative.DrawIndexed( Current.Pass, indexCount, 1, firstIndex, firstVertex, 0 );
		return 1;
	}
}
