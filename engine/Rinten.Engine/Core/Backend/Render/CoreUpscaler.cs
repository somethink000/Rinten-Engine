namespace Rinten;

//Per-drawer upscaler: turns the effects' output into a window-sized picture for the compose
//Runs after effects, before tonemapping; owns the temporal history that outlives a frame
internal sealed class CoreUpscaler
{
	//The upscaler this frame really has - see CoreUpscale.Effective.
	internal CoreUpscale.Kind Kind { get; private set; }

	internal bool Temporal => CoreUpscale.IsTemporal( Kind );

	//Where this frame is drawn from, in the world's pixels - nought where it is not temporal.
	internal Vector2 Jitter { get; private set; }

	//The translation after the projection that draws the world there - identity where not temporal.
	internal System.Numerics.Matrix4x4 JitterMatrix { get; private set; } = System.Numerics.Matrix4x4.Identity;

	//This frame's clip space straight into last frame's, as the frame block's two matrices make it - the temporal pass's reprojection
	internal System.Numerics.Matrix4x4 ClipToPrevious { get; set; } = System.Numerics.Matrix4x4.Identity;

	//How much sharper textures are read this frame - see CoreUpscale.MipBias.
	internal float MipBias { get; private set; }

	//Whether the frames gathered so far are to be thrown away this frame.
	internal bool Reset { get; private set; } = true;

	//The world's size and the window's this frame, as the upscaler was told them.
	internal (int Width, int Height) RenderSize { get; private set; }
	internal (int Width, int Height) DisplaySize { get; private set; }

	private int jitterIndex;
	private CoreUpscale.Kind lastKind = CoreUpscale.Kind.Native;
	private Vector3 lastEye;
	private Rotation lastTurn;
	private float lastField;
	private bool hasLast;

	//Picks this frame's upscaler and source, and drops the history on a camera cut to avoid ghosting
	internal void Begin( bool headset, int renderWidth, int renderHeight, int displayWidth, int displayHeight,
		Vector3 eye, Rotation turn, float field )
	{
		Kind = CoreUpscale.Effective( headset );

		//Another upscaler keeps other pictures, and none keeps none: what the last one held
		//goes back rather than sitting a window's size in memory for a setting that is off.
		if ( Kind != lastKind ) Release();

		RenderSize = (Math.Max( renderWidth, 1 ), Math.Max( renderHeight, 1 ));
		DisplaySize = (Math.Max( displayWidth, 1 ), Math.Max( displayHeight, 1 ));

		if ( !headset ) Last = (Kind, RenderSize, DisplaySize);

		var cut = !hasLast ||
			( eye - lastEye ).Length > CutDistance ||
			Vector3.Dot( turn.Forward, lastTurn.Forward ) < CutTurn ||
			MathF.Abs( field - lastField ) > 1.0f;

		Reset = cut || Kind != lastKind || !Temporal;

		lastKind = Kind;
		lastEye = eye;
		lastTurn = turn;
		lastField = field;
		hasLast = true;

		MipBias = CoreUpscale.MipBias( Kind, RenderSize.Width, DisplaySize.Width );

		if ( !Temporal )
		{
			Jitter = default;
			JitterMatrix = System.Numerics.Matrix4x4.Identity;
			return;
		}

		var phases = CoreUpscale.PhaseCount( RenderSize.Width, DisplaySize.Width );

		jitterIndex = ( jitterIndex + 1 ) % phases;
		Jitter = CoreUpscale.Jitter( jitterIndex, phases );
		JitterMatrix = CoreUpscale.JitterMatrix( Jitter, RenderSize.Width, RenderSize.Height );
	}

	//Further than this in one frame, in metres, is a cut rather than a move.
	private const float CutDistance = 8.0f;

	//A turn past this - the cosine of about forty-five degrees - in one frame is a cut.
	private const float CutTurn = 0.7f;

	//-- the pictures it keeps ------------------------------------------------

	//The two histories, written in turn: last frame's is read while this frame's is written.
	private readonly ulong[] history = new ulong[2];
	private int historyNow;
	private bool historyWritten;

	//What an edge pass or FSR 3 wrote.
	private ulong carried;

	//The world before glass and sprites were laid over it this frame, whether it was kept, and how much they changed each texel - see ReactiveCs
	private ulong opaque;
	private bool opaqueKept;
	private ulong reactive;

	//Every pixel's motion, for the upscalers that are handed one - see MotionCs.
	private ulong motion;

	private (int Width, int Height) keptDisplay;
	private (int Width, int Height) keptRender;

	//The materials the compose reads a picture of these through, by the picture's texture.
	private readonly Dictionary<ulong, ulong> readers = new();

	//Lets go of everything kept, for a drawer going away or a size that changed.
	internal void Release()
	{
		foreach ( var reader in readers.Values )
			if ( reader != 0 ) CoreRenderDevice.DestroyTexture( reader );

		readers.Clear();

		for ( var i = 0; i < history.Length; i++ )
		{
			CoreRenderDevice.ReleasePicture( history[i] );
			history[i] = 0;
		}

		CoreRenderDevice.ReleasePicture( carried );
		CoreRenderDevice.ReleasePicture( motion );
		CoreRenderDevice.ReleasePicture( opaque );
		CoreRenderDevice.ReleasePicture( reactive );

		carried = motion = opaque = reactive = 0;
		opaqueKept = false;
		historyWritten = false;
		keptDisplay = default;
		keptRender = default;

		fsr3?.Release();
		dlss?.Release();
	}

	//The last frame's upscaler, the size the world was drawn at and the size shown - for the report.
	internal static (CoreUpscale.Kind Kind, (int Width, int Height) Render, (int Width, int Height) Display) Last;

	private CoreFsr3 fsr3;
	private CoreDlss dlss;

	//Carries this frame's world up, and answers the material the compose is to read in its place - nought where
	//there is nothing to carry, or it could not be done and the compose carries the world itself as it always has.
	internal ulong Run( ulong colour, ulong depth, ulong velocity, float near, float far, float field, float delta, float exposure )
	{
		if ( Kind is CoreUpscale.Kind.Native or CoreUpscale.Kind.Stretch ) return 0;
		if ( colour == 0 || depth == 0 ) return 0;

		var carrying = RenderSize != DisplaySize;

		//FSR1 has nothing to do at a scale of one; the temporal ones always do.
		if ( Kind == CoreUpscale.Kind.Fsr1 && !carrying ) return 0;

		if ( !EnsurePipelines() ) return 0;

		if ( keptDisplay != DisplaySize || keptRender != RenderSize )
		{
			Release();
			keptDisplay = DisplaySize;
			keptRender = RenderSize;
			Reset = true;
		}

		CoreRenderDevice.ZoneBegin( "upscale" );
		ComposeSharpening = 0.0f;

		try
		{
			CoreRenderDevice.ReadyToRead( colour );
			CoreRenderDevice.ReadyToRead( depth );
			if ( velocity != 0 ) CoreRenderDevice.ReadyToRead( velocity );

			var result = Kind switch
			{
				CoreUpscale.Kind.Fsr1 => RunFsr1( colour ),
				CoreUpscale.Kind.Temporal => RunTemporal( colour, depth, velocity, near, far ),
				CoreUpscale.Kind.Fsr3 => RunFsr3( colour, depth, velocity, near, far, field, delta, exposure ),
				CoreUpscale.Kind.Dlss => RunDlss( colour, depth, velocity, near, far, delta ),
				_ => 0ul,
			};

			LastTexture = result == 0 ? 0 : CoreRenderDevice.PictureTexture( result );
			return result == 0 ? 0 : ReaderOf( result );
		}
		finally
		{
			opaqueKept = false;
			CoreRenderDevice.ZoneEnd();
		}
	}

	//Keeps the world's picture as it stands before glass and sprites are laid over it, for the reactive mask - see Reactivity
	internal void KeepOpaque( ulong colour )
	{
		if ( Kind is not ( CoreUpscale.Kind.Temporal or CoreUpscale.Kind.Fsr3 ) || colour == 0 || !EnsurePipelines() ) return;

		var (width, height) = RenderSize;
		if ( Keep( ref opaque, CoreRender.PictureKind.Hdr, width, height ) == 0 ) return;

		CoreRenderDevice.ReadyToRead( colour );

		Span<byte> push = stackalloc byte[128];
		Push( push, RenderSize, RenderSize, colour, 0, 0, 0, 0, 0.0f, 0.0f );

		CoreRenderDevice.ZoneBegin( "opaque kept" );
		opaqueKept = Dispatch( copyPipeline, opaque, push, width, height );
		CoreRenderDevice.ZoneEnd();
	}

	//How much what was laid over the opaque world changed each texel this frame, as a texture; nought where nothing was
	private ulong Reactivity( ulong colour )
	{
		if ( !opaqueKept ) return 0;

		var (width, height) = RenderSize;
		if ( Keep( ref reactive, CoreRender.PictureKind.Single, width, height ) == 0 ) return 0;

		Span<byte> push = stackalloc byte[128];
		Push( push, RenderSize, RenderSize, colour, CoreRenderDevice.PictureTexture( opaque ), 0, 0, 0, 0.0f, 0.0f );

		CoreRenderDevice.ZoneBegin( "reactive" );
		var ran = Dispatch( reactivePipeline, reactive, push, width, height );
		CoreRenderDevice.ZoneEnd();

		return ran ? CoreRenderDevice.PictureTexture( reactive ) : 0;
	}

	//The last Run's picture as a texture, for a pass that reads it by its place in the table - see CoreSceneDrawer.
	internal ulong LastTexture { get; private set; }

	//How far the compose sharpens the last Run's picture by RCAS, nought none - it reads every pixel anyway, so no pass of its own
	internal float ComposeSharpening { get; private set; }

	//A picture this keeps, acquired the first time it is wanted at this size.
	private static ulong Keep( ref ulong picture, CoreRender.PictureKind kind, int width, int height )
	{
		if ( picture == 0 ) picture = CoreRenderDevice.AcquirePicture( kind, width, height, 1, storage: true );
		return picture;
	}

	//The material the compose reads this picture through, made once.
	private ulong ReaderOf( ulong picture )
	{
		var texture = CoreRenderDevice.PictureTexture( picture );
		if ( texture == 0 ) return 0;

		if ( readers.TryGetValue( texture, out var reader ) ) return reader;

		reader = CoreRenderDevice.CreateMaterial( texture, texture, texture, texture, 1, 0, 0, 0 );
		readers[texture] = reader;

		return reader;
	}

	private ulong RunFsr1( ulong colour )
	{
		var (width, height) = DisplaySize;

		if ( Keep( ref carried, CoreRender.PictureKind.Hdr, width, height ) == 0 ) return 0;

		Span<byte> push = stackalloc byte[128];

		Push( push, DisplaySize, RenderSize, colour, 0, 0, 0, 0, 0.0f, 0.0f );

		if ( !Dispatch( easuPipeline, carried, push, width, height ) ) return 0;

		ComposeSharpening = CoreUpscale.RcasStrength( Rinten.Rendering.DrawSettings.Fsr1Sharpness );
		return carried;
	}

	private ulong RunTemporal( ulong colour, ulong depth, ulong velocity, float near, float far )
	{
		var (width, height) = DisplaySize;

		for ( var i = 0; i < history.Length; i++ )
			if ( Keep( ref history[i], CoreRender.PictureKind.Hdr, width, height ) == 0 ) return 0;

		var reading = history[historyNow];
		var writing = history[1 - historyNow];

		var reset = Reset || !historyWritten;

		var flags = ( reset ? 1 : 0 ) | ( velocity != 0 ? 2 : 0 );

		Span<byte> push = stackalloc byte[128];

		Push( push, DisplaySize, RenderSize, colour,
			reset ? 0 : CoreRenderDevice.PictureTexture( reading ), depth, velocity, flags, near, far );

		var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( push );

		//Where glass and sprites changed the world - see ReactiveCs
		var reacting = Reactivity( colour );
		floats[15] = reacting == 0 ? 0.0f : CoreSceneDrawer.Indexed( reacting );

		//How much a pixel may have gathered: more the more it is upscaled, since each frame then lands fewer samples near any one pixel.
		var upscale = DisplaySize.Width / (float)RenderSize.Width;

		floats[16] = Math.Clamp( 8.0f * upscale, 8.0f, 24.0f );

		//How far outside the spread of its neighbourhood a history may stay: a little wider
		//at native, where the neighbourhood is the same pixels the history was made of.
		floats[17] = upscale > 1.01f ? 1.0f : 1.25f;

		//How narrow a sample's reach is, in the world's pixels squared.
		floats[18] = 1.0f;

		//The reprojection's x, y and w columns, turned so a point in front lands with w above nought - see Reprojected
		var m = ClipToPrevious;
		var sign = ( ( 0.5f * m.M34 ) + m.M44 ) < 0.0f ? -1.0f : 1.0f;

		floats[20] = m.M11 * sign; floats[21] = m.M21 * sign; floats[22] = m.M31 * sign; floats[23] = m.M41 * sign;
		floats[24] = m.M12 * sign; floats[25] = m.M22 * sign; floats[26] = m.M32 * sign; floats[27] = m.M42 * sign;
		floats[28] = m.M14 * sign; floats[29] = m.M24 * sign; floats[30] = m.M34 * sign; floats[31] = m.M44 * sign;

		if ( !Dispatch( temporalPipeline, writing, push, width, height ) ) return 0;

		historyNow = 1 - historyNow;
		historyWritten = true;

		var sharpness = Rinten.Rendering.DrawSettings.TemporalSharpness;
		ComposeSharpening = sharpness > 0.001f ? CoreUpscale.RcasStrength( sharpness ) : 0.0f;

		return writing;
	}

	private ulong RunFsr3( ulong colour, ulong depth, ulong velocity, float near, float far, float field, float delta, float exposure )
	{
		var output = Keep( ref carried, CoreRender.PictureKind.Hdr, DisplaySize.Width, DisplaySize.Height );
		if ( output == 0 || !Motion( depth, velocity, near, far ) ) return 0;

		fsr3 ??= new CoreFsr3();

		//Its RCAS in the compose, as the engine's own upscaler's - a pass of its own wrote and read the window's picture again
		if ( !fsr3.Run( colour, depth, CoreRenderDevice.PictureTexture( motion ), Reactivity( colour ), output,
			RenderSize, DisplaySize, Jitter, near, far, field, delta, Reset, exposure ) ) return 0;

		var sharpness = Rinten.Rendering.DrawSettings.Fsr3Sharpness;
		ComposeSharpening = sharpness > 0.001f ? CoreUpscale.RcasStrength( sharpness ) : 0.0f;

		return output;
	}

	private ulong RunDlss( ulong colour, ulong depth, ulong velocity, float near, float far, float delta )
	{
		var output = Keep( ref carried, CoreRender.PictureKind.Hdr, DisplaySize.Width, DisplaySize.Height );
		if ( output == 0 || !Motion( depth, velocity, near, far ) ) return 0;

		dlss ??= new CoreDlss();

		return dlss.Run( colour, depth, motion, output, RenderSize, DisplaySize, Jitter, Reset, delta, copyPipeline ) ? output : 0;
	}

	//Every pixel's motion into motion, at the world's size - see MotionCs.
	private bool Motion( ulong depth, ulong velocity, float near, float far )
	{
		var (width, height) = RenderSize;

		if ( Keep( ref motion, CoreRender.PictureKind.Pair, width, height ) == 0 ) return false;

		Span<byte> push = stackalloc byte[128];

		Push( push, RenderSize, RenderSize, 0, 0, depth, velocity, velocity != 0 ? 2 : 0, near, far );

		CoreRenderDevice.ZoneBegin( "motion" );
		var ran = Dispatch( motionPipeline, motion, push, width, height );
		CoreRenderDevice.ZoneEnd();

		return ran;
	}

	//The push block every pass here reads - see upscale.slang for which lane is
	//what. Pictures are handed as textures and written as their places in the table.
	private void Push( Span<byte> into, (int Width, int Height) stored, (int Width, int Height) read,
		ulong colour, ulong history, ulong depth, ulong velocity, int flags, float near, float far )
	{
		into.Clear();

		var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( into );

		floats[0] = stored.Width;
		floats[1] = stored.Height;
		floats[2] = 1.0f / stored.Width;
		floats[3] = 1.0f / stored.Height;

		floats[4] = near;
		floats[5] = far;
		floats[6] = Jitter.x;
		floats[7] = Jitter.y;

		floats[8] = CoreSceneDrawer.Indexed( colour );
		floats[9] = CoreSceneDrawer.Indexed( history );
		floats[10] = CoreSceneDrawer.Indexed( depth );
		floats[11] = CoreSceneDrawer.Indexed( velocity );

		floats[12] = read.Width;
		floats[13] = read.Height;
		floats[14] = flags;
	}

	private static bool Dispatch( ulong pipeline, ulong output, ReadOnlySpan<byte> push, int width, int height )
	{
		Span<ulong> outputs = stackalloc ulong[1] { output };
		Span<int> levels = stackalloc int[1] { 0 };

		return CoreRenderDevice.DispatchPass( pipeline, CoreSceneDrawer.WhiteTexture, outputs, levels,
			ReadOnlySpan<ulong>.Empty, push, ( width + 7 ) / 8, ( height + 7 ) / 8 );
	}

	//-- the passes -------------------------------------------------------------

	private static ulong temporalPipeline;
	private static ulong easuPipeline;
	private static ulong motionPipeline;
	private static ulong reactivePipeline;
	private static ulong copyPipeline;
	private static int pipelinesBuilt = -1;
	private static bool pipelinesFailed;

	//The motion pass's build: it stores into two half floats - see upscale.slang.
	internal const string MotionDefines = "#define UPSCALE_MOTION 1\n";

	//And the reactive mask's: one half float - see ReactiveCs.
	internal const string SingleDefines = "#define UPSCALE_SINGLE 1\n";

	//The four passes, built the first time a frame wants one and again when the shaders change.
	private static bool EnsurePipelines()
	{
		if ( pipelinesBuilt == CoreShaderCache.Revision ) return !pipelinesFailed;

		DestroyPipelines();

		pipelinesBuilt = CoreShaderCache.Revision;

		temporalPipeline = Build( "TemporalCs", null );
		easuPipeline = Build( "EasuCs", null );
		motionPipeline = Build( "MotionCs", MotionDefines );
		reactivePipeline = Build( "ReactiveCs", SingleDefines );
		copyPipeline = Build( "CopyCs", null );

		pipelinesFailed = temporalPipeline == 0 || easuPipeline == 0 || motionPipeline == 0 || copyPipeline == 0 || reactivePipeline == 0;

		if ( pipelinesFailed )
			Log.Warning( $"the upscaler's passes did not build: {CoreRenderDevice.ShaderError}" );

		return !pipelinesFailed;
	}

	private static ulong Build( string entry, string defines )
	{
		var code = CoreShaderCache.Pass( EngineShaders.Upscale, entry, CoreShaderCompiler.Stage.Compute, defines );
		return code is null ? 0 : CoreRenderDevice.CreatePassPipeline( code );
	}

	//Every pass let go of, for a device going away.
	internal static void DestroyPipelines()
	{
		foreach ( var pipeline in new[] { temporalPipeline, easuPipeline, motionPipeline, copyPipeline, reactivePipeline } )
			if ( pipeline != 0 ) CoreRenderDevice.DestroyPipeline( pipeline );

		temporalPipeline = easuPipeline = motionPipeline = copyPipeline = reactivePipeline = 0;
		pipelinesBuilt = -1;
		pipelinesFailed = false;
	}
}
