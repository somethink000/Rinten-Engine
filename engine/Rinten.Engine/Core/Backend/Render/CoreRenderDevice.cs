using System.IO;
using System.Runtime.InteropServices;

namespace Rinten;

//The graphics device from the managed side: what it is and whether it opened; everything else goes
//through the render seam rather than the API
internal static unsafe class CoreRenderDevice
{
	private static readonly Logger log = new( "Render" );

	//Whether -vkvalidate was passed; read once, as whoever opens the device first decides for all
	private static readonly bool Asked = Rinten.Utility.CommandLine.HasSwitch( "-vkvalidate" );

	/// <summary>Whether a device is open.</summary>
	public static bool IsReady => CoreNative.Available && CoreRender.Ready() != 0;

	/// <summary>The device we are on, or empty if none opened.</summary>
	public static string DeviceName
		=> CoreNative.Available ? Marshal.PtrToStringUTF8( CoreRender.DeviceName() ) ?? string.Empty : string.Empty;

	/// <summary>The device, or why there isn't one.</summary>
	public static string Description
		=> CoreNative.Available ? Marshal.PtrToStringUTF8( CoreRender.Info() ) ?? string.Empty : "rintencore is not loaded";

	/// <summary>The Vulkan version the driver supports.</summary>
	public static Version ApiVersion
	{
		get
		{
			if ( !CoreNative.Available ) return new Version( 0, 0, 0 );

			var info = stackalloc int[4];
			CoreRender.DeviceInfo( info );

			return new Version( info[0], info[1], info[2] );
		}
	}

	/// <summary>What is on the graphics device, counted from both sides.</summary>
	public readonly record struct GpuMemory( long Textures, long Buffers,
		long DriverUsed, long DriverBudget, long HeapSize, bool DriverAnswered,
		long Targets = 0, long ShadowMaps = 0, long Pyramids = 0 )
	{
		/// <summary>What we asked for, textures and buffers together.</summary>
		public long Ours => Textures + Buffers;

		//Rising while Ours holds still means the driver is holding something we did not know we asked for
		/// <summary>Driver memory we do not count: swapchain, pools, staging, alignment, overhead.</summary>
		public long Unaccounted => DriverAnswered ? Math.Max( 0, DriverUsed - Ours - Targets - ShadowMaps - Pyramids ) : 0;
	}

	/// <summary>Asks the device what it holds; cheap, and fresh each time, as others draw on this machine.</summary>
	public static GpuMemory ReadMemory()
	{
		if ( !CoreNative.Available ) return default;

		var values = stackalloc long[10];

		var given = CoreRender.Memory( values, 10 );
		if ( given < 7 ) return default;

		return new GpuMemory( values[0], values[1], values[2], values[3], values[4], values[6] != 0,
			given > 7 ? values[7] : 0, given > 8 ? values[8] : 0, given > 9 ? values[9] : 0 );
	}

	/// <summary>Size of the bindless picture table, or 0 without one (every vertex picture index reads 0).</summary>
	public static int BindlessSlots => CoreNative.Available ? CoreRender.BindlessSlots() : 0;

	/// <summary>Whether an indirect draw may name where in the instance buffer its copies start.</summary>
	public static bool FirstInstance
		=> CoreNative.Available && CoreRender.FirstInstance() != 0;

	/// <summary>Whether this device draws the portable frame (browser, Vulkan before 1.3).</summary>
	public static bool PortableFrame => CoreRender.Tier == RenderTier.Portable;

	/// <summary>Whether the validation layers are running.</summary>
	public static bool ValidationEnabled
	{
		get
		{
			if ( !CoreNative.Available ) return false;

			var info = stackalloc int[4];
			CoreRender.DeviceInfo( info );

			return info[3] != 0;
		}
	}

	/// <summary>Opens a device, or says why it could not.</summary>
	public static bool Initialize( bool validation = false )
	{
		if ( !CoreNative.Available )
		{
			log.Warning( $"no graphics device: {CoreNative.BuildInfo}" );
			return false;
		}

		if ( CoreRender.Init( validation || Asked ? 1 : 0 ) == 0 )
		{
			log.Warning( $"no graphics device: {Description}" );
			return false;
		}

		if ( !opened )
		{
			opened = true;
			log.Info( Description );

			//Before any pipeline bakes, so the driver need not compile what is cached
			LoadPipelineCache();
		}

		return true;
	}

	//-- the pipeline cache ------------------------------------------------------
	//Saves the driver's compiled pipelines between runs; the core validates it, this side owns the file

	//The driver's compiled pipelines between runs, in the user's data: they belong to this card and driver
	private static string PipelineCachePath
	{
		get
		{
			var root = Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData );

			//A platform that will not say where local data goes still has a temporary directory,
			//and a cache thrown away between sessions is better than none within one.
			if ( string.IsNullOrEmpty( root ) ) root = Path.GetTempPath();

			return Path.Combine( root, "Rinten", "pipeline.cache" );
		}
	}

	//Hands the core what was saved last run, if any. A file from another card, another driver or another
	//version of one is refused by the core rather than trusted, so a stale file costs only the read.
	private static void LoadPipelineCache()
	{
		try
		{
			var path = PipelineCachePath;
			if ( !File.Exists( path ) ) return;

			var bytes = File.ReadAllBytes( path );
			if ( bytes.Length == 0 ) return;

			fixed ( byte* data = bytes )
			{
				if ( CoreRender.PipelineCacheLoad( data, bytes.Length ) != 0 )
					log.Info( $"{bytes.Length / 1024} KB of pipeline cache from {path}" );
			}
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			//A cache that will not load is a slower start, not a failure.
			log.Info( $"no pipeline cache: {e.Message}" );
		}
	}

	//How many pipelines had been baked when the cache was last written out.
	private static int savedPipelines;

	//When the count last moved, in Stopwatch ticks.
	private static long bakedAt;

	//Saves the driver's pipeline cache a few seconds after the last bake, not only on a tidy shutdown
	private static void KeepPipelineCache()
	{
		var baked = CoreRender.PipelinesBaked.Count;

		if ( baked == savedPipelines ) return;

		var now = System.Diagnostics.Stopwatch.GetTimestamp();

		//Still baking: wait for it to settle rather than writing the cache out between one pipeline and the next.
		if ( baked != bakedPipelines )
		{
			bakedPipelines = baked;
			bakedAt = now;
			return;
		}

		if ( now - bakedAt < 3 * System.Diagnostics.Stopwatch.Frequency ) return;

		savedPipelines = baked;

		SavePipelineCache();
	}

	//The count this saw last frame, to tell "still baking" from "settled".
	private static int bakedPipelines;

	//Writes out what the driver compiled this run, while the device is still open
	private static void SavePipelineCache()
	{
		try
		{
			//Asked how much room it wants before being given any.
			var size = CoreRender.PipelineCacheSave( null, 0 );
			if ( size <= 0 ) return;

			var bytes = new byte[size];
			int written;

			fixed ( byte* data = bytes )
			{
				written = CoreRender.PipelineCacheSave( data, size );
			}

			if ( written <= 0 ) return;

			var path = PipelineCachePath;
			Directory.CreateDirectory( Path.GetDirectoryName( path ) );

			//Written beside and moved into place, under this process's own name, so a dying run or two closing together
			//leave the last good cache - see CoreShaderCache.SaveLocal
			var temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.new";

			try
			{
				File.WriteAllBytes( temporary, bytes.AsSpan( 0, written ).ToArray() );
				File.Move( temporary, path, overwrite: true );
			}
			catch ( Exception ) when ( File.Exists( temporary ) )
			{
				try { File.Delete( temporary ); } catch ( Exception ) { }
				throw;
			}
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			log.Info( $"the pipeline cache was not saved: {e.Message}" );
		}
	}

	//Whether a device has been opened and not shut down.
	private static bool opened;

	/// <summary>Closes the device, waiting for the GPU first, so in-flight work is fine.</summary>
	public static void Shutdown()
	{
		if ( !CoreNative.Available ) return;

		//What was loaded onto the device goes with it. Before the shutdown, so that the handles are
		//still handles when they are let go of. Needs the device still open - the cache lives on it.
		if ( opened ) SavePipelineCache();

		//And the shader stages this run compiled, if the moment after the last compile has not come round yet.
		CoreShaderCache.SaveLocal();

		CoreResources.Clear();

		CoreRender.Shutdown();

		opened = false;
	}

	//-- the window ------------------------------------------------------------

	/// <summary>Gives the device a window and returns its swapchain; zero on failure.</summary>
	public static ulong AttachWindow( CoreWindow window, bool transparent = false,
		int widthPixels = 0, int heightPixels = 0 )
	{
		ArgumentNullException.ThrowIfNull( window );

		if ( !IsReady ) return 0;

		if ( widthPixels <= 0 || heightPixels <= 0 )
			(widthPixels, heightPixels) = window.SizeInPixels;

		//Window kind and handles come from the window itself, so one call covers every platform
		var swapchain = CoreRender.SwapchainCreate( (int)window.WindowKind,
			window.NativeDisplay, window.NativeWindow, widthPixels, heightPixels,
			transparent ? 1 : 0 );

		if ( swapchain == 0 )
		{
			log.Warning( $"no swapchain: {Description}" );
			return 0;
		}

		log.Info( Description );

		//Whatever the graphics settings already say. A window that opens after the user has turned the resolution
		//down should open turned down, not at full size until something happens to reapply the setting.
		ApplyRenderScale( swapchain );

		return swapchain;
	}

	/// <summary>An opaque swapchain on a surface not a window's own (see WaylandLayer), as big as told.</summary>
	public static ulong AttachSurface( int kind, ulong display, ulong surface, int widthPixels, int heightPixels )
	{
		if ( !IsReady || surface == 0 || widthPixels <= 0 || heightPixels <= 0 ) return 0;

		var swapchain = CoreRender.SwapchainCreate( kind, display, surface, widthPixels, heightPixels, 0 );

		if ( swapchain == 0 )
		{
			log.Warning( $"no swapchain on a layer: {Description}" );
			return 0;
		}

		ApplyRenderScale( swapchain );

		return swapchain;
	}

	/// <summary>Rebuilds the swapchain at a new size, waiting for in-flight work; not per frame.</summary>
	public static bool Resize( ulong swapchain, int width, int height )
		=> CoreNative.Available && CoreRender.SwapchainResize( swapchain, width, height ) != 0;

	/// <summary>Whether this window waits for the display before a frame is shown.</summary>
	public static bool SetVSync( ulong swapchain, bool enabled )
	{
		if ( !CoreNative.Available || swapchain == 0 ) return false;

		return CoreRender.SwapchainVsync( swapchain, enabled ? 1 : 0 ) != 0;
	}

	/// <summary>Where on the window the world is drawn; no size means whole window. Resizes remake pictures.</summary>
	public static bool SetWorldRect( ulong swapchain, int x, int y, int width, int height )
	{
		if ( !CoreNative.Available || swapchain == 0 ) return false;

		return CoreRender.SwapchainWorldRect( swapchain, x, y, width, height ) != 0;
	}

	/// <summary>Closes the screen pass so world passes can run mid-draw; the next screen draw reopens it.</summary>
	public static bool InterruptScreen()
		=> CoreNative.Available && CoreRender.ScreenInterrupt() != 0;

	/// <summary>Whether the world drawn next has surfaces to shade; without any it is shaded at every pixel's rate.</summary>
	public static void WorldShades( bool surfaces )
	{
		if ( CoreNative.Available ) CoreRender.WorldShades( surfaces ? 1 : 0 );
	}

	/// <summary>The world drawn next's clip space into its frame before's, which its adaptive rates follow the view by.</summary>
	public static void WorldBack( in System.Numerics.Matrix4x4 clipToPrevious )
	{
		if ( CoreNative.Available ) CoreRender.WorldBack( clipToPrevious );
	}

	/// <summary>Says the world's drawing on the screen is over - see <see cref="InterruptScreen"/>.</summary>
	public static void ResumeScreen()
	{
		if ( CoreNative.Available ) CoreRender.ScreenResume();
	}

	/// <summary>How much of the window's resolution the world is drawn at, from a quarter to all of it.</summary>
	public static bool SetRenderScale( ulong swapchain, float scale )
	{
		if ( !CoreNative.Available || swapchain == 0 ) return false;

		return CoreRender.SwapchainScale( swapchain, scale ) != 0;
	}

	/// <summary>Puts the graphics settings' render scale onto a window.</summary>
	public static bool ApplyRenderScale( ulong swapchain )
	{
		if ( !CoreNative.Available || swapchain == 0 ) return false;

		return SetRenderScale( swapchain, CoreUpscale.WindowScale );
	}

	/// <summary>Lets go of one swapchain. The device stays open.</summary>
	public static void DetachWindow( ulong swapchain )
	{
		if ( !CoreNative.Available || swapchain == 0 ) return;

		CoreRender.SwapchainDestroy( swapchain );
	}

	/// <summary>The swapchain's actual size, which may differ from the ask, and its image count.</summary>
	public static (int Width, int Height, int Images) SwapchainInfo( ulong swapchain )
	{
		if ( !CoreNative.Available ) return (0, 0, 0);

		var info = stackalloc int[4];
		CoreRender.SwapchainInfo( swapchain, info );

		return (info[0], info[1], info[2]);
	}

	/// <summary>Whether the desktop will let what is behind this window show through it.</summary>
	public static bool IsComposited( ulong swapchain )
	{
		if ( !CoreNative.Available ) return false;

		var info = stackalloc int[4];
		CoreRender.SwapchainInfo( swapchain, info );

		//Anything past the opaque bit means the alpha is looked at.
		const int Opaque = 1;

		return ( info[3] & ~Opaque ) != 0;
	}

	//-- a frame ---------------------------------------------------------------

	/// <summary>Acquires and clears the next image; false means skip the frame. One open at a time.</summary>
	public static bool BeginFrame( ulong swapchain, Color clear )
	{
		poses.Clear();
		morphs.Clear();

		KeepPipelineCache();

		if ( !CoreNative.Available || CoreRender.FrameBegin( swapchain, clear.r, clear.g, clear.b, clear.a ) == 0 ) return false;

		//Numbers set on live materials since the last frame, ahead of anything drawn with them.
		CoreResources.WriteNumbers();

		return true;
	}

	/// <summary>Presents the frame; false if the swapchain went out of date in flight, resized next frame.</summary>
	public static bool EndFrame()
		=> CoreNative.Available && CoreRender.FrameEnd() != 0;

	/// <summary>Waits until ended frames are presented, for window calls that must precede the next present.</summary>
	public static void FramesHandedOver()
	{
		if ( CoreNative.Available ) CoreRender.FramesHandedOver();
	}

	/// <summary>The frame's view, camera and lights, written once after BeginFrame for every draw to read.</summary>
	public static bool SetupFrame( ReadOnlySpan<float> frame )
	{
		if ( !CoreNative.Available || frame.IsEmpty ) return false;

		fixed ( float* data = frame )
		{
			return CoreRender.FrameSetup( data, frame.Length ) != 0;
		}
	}

	//The second eye's block for a two-eye pass, read at the second layer - see CreateEyesView
	internal static bool SetupSecondEye( ReadOnlySpan<float> frame )
	{
		if ( !CoreNative.Available || frame.IsEmpty ) return false;

		fixed ( float* data = frame )
		{
			return CoreRender.FrameSetupSecondEye( data, frame.Length ) != 0;
		}
	}

	/// <summary>Rewrites a run of this frame's block from <paramref name="at"/> floats in.</summary>
	public static bool PatchFrame( int at, ReadOnlySpan<float> values )
	{
		if ( !CoreNative.Available || values.IsEmpty ) return false;

		fixed ( float* data = values )
		{
			return CoreRender.FrameBlockPatch( at, data, values.Length ) != 0;
		}
	}

	//A second frame block for draws seen from elsewhere, like a light's view into a shadow map
	//Returns where it went, or nought where it could not; UseFrame makes the draws read it
	internal static ulong PutFrame( ReadOnlySpan<float> frame )
	{
		if ( !CoreNative.Available || frame.IsEmpty ) return 0;

		fixed ( float* data = frame )
		{
			return CoreRender.FrameBlockPut( data, frame.Length );
		}
	}

	//Which frame block the draws from here on read - one PutFrame gave, or the one this returned to put back.
	internal static ulong UseFrame( ulong offset )
		=> CoreNative.Available ? CoreRender.FrameBlockUse( offset ) : 0;

	/// <summary>The buffer world shaders read lights from (CoreLights); nought restores the frame scratch.</summary>
	public static bool SetLightBuffer( ulong buffer )
		=> CoreNative.Available && CoreRender.SetLightBuffer( buffer ) != 0;

	/// <summary>Copies the frame for panels that blur or tint behind them; false if nothing or nowhere.</summary>
	public static bool CaptureBackdrop()
		=> CoreNative.Available && CoreRender.BackdropCapture() != 0;

	/// <summary>Copies the window's picture as it stands into a picture, for a frame generated before this one - see CoreFrameGeneration.</summary>
	public static bool CaptureForGeneration( ulong picture )
		=> CoreNative.Available && picture != 0 && CoreRender.GenerationCapture( picture ) != 0;

	/// <summary>That copy as a texture, or zero until it has been taken this frame.</summary>
	public static ulong BackdropTexture()
		=> CoreNative.Available ? CoreRender.BackdropTexture() : 0;

	//-- a view ----------------------------------------------------------------

	/// <summary>A viewport with no window, for previews and thumbnails: same pictures and passes, no present.</summary>
	public static ulong CreateView( int width, int height )
		=> CoreNative.Available ? CoreRender.ViewCreate( width, height ) : 0;

	//A view of both of a headset's eyes, a layer each, drawn in one pass - see Multiview. Nought where that cannot be.
	internal static ulong CreateEyesView( int width, int height )
		=> CoreNative.Available && Multiview ? CoreRender.ViewCreate( width, height, 2 ) : 0;

	//Two eyes in one pass: the portable frame on a multiview device; the full frame draws an eye at a time
	internal static bool Multiview => CoreNative.Available && CoreRender.Multiview;

	/// <summary>Lets one go. Waits for the device: it may still be being drawn.</summary>
	public static void DestroyView( ulong view )
	{
		if ( CoreNative.Available && view != 0 ) CoreRender.ViewDestroy( view );
	}

	/// <summary>Remakes its pictures at a new size, or leaves them if that is the size they already are.</summary>
	public static bool ResizeView( ulong view, int width, int height )
		=> CoreNative.Available && CoreRender.ViewResize( view, width, height ) != 0;

	/// <summary>Aims the passes at a view: inside an open frame, before anything drew the window.</summary>
	public static bool BeginView( ulong view, Color clear = default )
		=> CoreNative.Available &&
			CoreRender.ViewBegin( view, clear.r, clear.g, clear.b, clear.a ) != 0;

	/// <summary>Ends it, and makes what it drew readable.</summary>
	public static void EndView()
	{
		if ( CoreNative.Available ) CoreRender.ViewEnd();
	}

	/// <summary>Begins a standalone view frame outside the window loop, e.g. a thumbnail.</summary>
	public static bool BeginViewFrame( ulong view, Color clear = default )
	{
		//Last frame's poses live in last frame's scratch, which this one is about to write over.
		poses.Clear();
		morphs.Clear();

		if ( !CoreNative.Available || CoreRender.ViewFrameBegin( view, clear.r, clear.g, clear.b, clear.a ) == 0 ) return false;

		CoreResources.WriteNumbers();

		return true;
	}

	/// <summary>Ends it, submits it and waits for it: what it drew is finished by the time this returns.</summary>
	public static bool EndViewFrame( ulong view )
		=> CoreNative.Available && CoreRender.ViewFrameEnd( view ) != 0;

	//Copies the view's picture into another image (e.g. a headset's) as its frame ends
	internal static bool CopyViewTo( ulong view, ulong image, int width, int height, int layer = 0 )
		=> CoreNative.Available && CoreRender.ViewCopyTo( view, image, width, height, layer ) != 0;

	//A headset eye view's foveation: strength nought to one, and each layer's centre in nought-to-one - see CoreFovea
	internal static bool SetViewFovea( ulong view, float strength, bool tracked, Vector2 first, Vector2 second )
		=> CoreNative.Available && view != 0 &&
			CoreRender.ViewFovea( view, strength, tracked, first.x, first.y, second.x, second.y ) != 0;

	//Whether the device shades an eye coarser away from where it looks.
	internal static bool Foveates => CoreNative.Available && CoreRender.Foveates;

	//The texture the view's picture copies into as its frame ends, every level, ready to sample
	internal static bool CopyViewToTexture( ulong view, ulong texture )
		=> CoreNative.Available && texture != 0 && CoreRender.ViewCopyToTexture( view, texture ) != 0;

	//Draws after this one put their depth between nought and ceiling - nearer than anything
	//drawn with the whole range, and in order among themselves. One puts the whole range back.
	internal static void SetDepthCeiling( float ceiling )
	{
		if ( CoreNative.Available ) CoreRender.SetDepthCeiling( ceiling );
	}

	public static bool EndViewFrameAsync( ulong view )
		=> CoreNative.Available && CoreRender.ViewFrameEndAsync( view ) != 0;

	/// <summary>Whether the card has finished this view's last frame, asked without waiting for it.</summary>
	public static bool ViewFrameReady( ulong view )
		=> !CoreNative.Available || CoreRender.ViewFrameReady( view ) != 0;

	/// <summary>The finished picture as a texture, for a window to draw without going through memory.</summary>
	public static ulong ViewTexture( ulong view )
		=> CoreNative.Available ? CoreRender.ViewTexture( view ) : 0;

	/// <summary>Whether this viewport's depth pre-pass may write the screen's depth buffer instead of its own</summary>
	public static bool DepthOnScreenPossible( ulong viewport )
		=> CoreNative.Available && CoreRender.DepthOnScreenPossible( viewport ) != 0;

	/// <summary>Asks for it, for this frame.</summary>
	public static void DepthOnScreen( ulong viewport, bool wanted )
	{
		if ( CoreNative.Available ) CoreRender.DepthOnScreen( viewport, wanted );
	}

	/// <summary>Where depth is read from and that picture's size.</summary>
	public static (int X, int Y, int Wide, int Tall) DepthReadRect( ulong viewport )
		=> CoreNative.Available ? CoreRender.DepthReadRect( viewport ) : (0, 0, 0, 0);

	/// <summary>Reads a view's pixels; layer is the eye for a view of both - see CreateEyesView.</summary>
	public static bool ReadView( ulong view, Span<byte> pixels, int layer = 0 )
	{
		if ( !CoreNative.Available || pixels.IsEmpty ) return false;

		fixed ( byte* data = pixels )
		{
			return CoreRender.ViewRead( view, data, pixels.Length, layer ) == pixels.Length;
		}
	}

	/// <summary>Pixels as premultiplied BGRA for a layered window; swizzled in the core.</summary>
	public static bool ReadViewLayered( ulong view, Span<byte> pixels )
	{
		if ( !CoreNative.Available || pixels.IsEmpty ) return false;

		fixed ( byte* data = pixels )
		{
			return CoreRender.ViewReadLayered( view, data, pixels.Length ) == pixels.Length;
		}
	}

	//-- the pictures a pass draws into ----------------------------------------

	/// <summary>Begins drawing into an off-screen picture (world, depth, post), before the screen is drawn.</summary>
	public static bool BeginTarget( CoreRender.TargetKind target, Color clear = default, bool clearFirst = true,
		bool discard = false )
		=> CoreNative.Available && CoreRender.TargetBegin( (int)target,
			clear.r, clear.g, clear.b, clear.a, clearFirst ? 1 : discard ? 2 : 0 ) != 0;

	/// <summary>Ends it, and makes what was drawn readable.</summary>
	public static void EndTarget()
	{
		if ( CoreNative.Available ) CoreRender.TargetEnd();
	}

	/// <summary>A frame picture of this shape until given back - see VkPictures; nought on failure.</summary>
	public static ulong AcquirePicture( CoreRender.PictureKind kind, int width, int height, int levels = 1, bool storage = false )
		=> CoreNative.Available ? CoreRender.PictureAcquire( (int)kind, width, height, levels, storage ? 1 : 0 ) : 0;

	/// <summary>Given back, for the next request of the same shape.</summary>
	public static void ReleasePicture( ulong picture )
	{
		if ( CoreNative.Available && picture != 0 ) CoreRender.PictureRelease( picture );
	}

	/// <summary>Its size, levels and actual format, which a pipeline for it is baked against.</summary>
	public static bool PictureShape( ulong picture, out int width, out int height, out int levels, out int format )
	{
		width = height = levels = format = 0;
		return CoreNative.Available && CoreRender.PictureShape( picture, out width, out height, out levels, out format ) != 0;
	}

	/// <summary>The texture a shader reads it through - index it with CoreRender.TextureIndex.</summary>
	public static ulong PictureTexture( ulong picture )
		=> CoreNative.Available ? CoreRender.PictureTexture( picture ) : 0;

	/// <summary>One picture level as the draw target, with the world's depth read-only where asked.</summary>
	public static bool BeginPicture( ulong picture, int level = 0, Color clear = default, bool clearFirst = false,
		bool discard = false, bool withDepth = false )
		=> CoreNative.Available && CoreRender.PictureBegin( picture, level,
			clear.r, clear.g, clear.b, clear.a, clearFirst ? 1 : discard ? 2 : 0, withDepth ? 1 : 0 ) != 0;

	/// <summary>Sends the world's pass into a picture of the screen's format, or back; that picture's texture, or nought.</summary>
	public static ulong WorldAside( bool on, out int width, out int height )
	{
		width = height = 0;
		return CoreNative.Available ? CoreRender.WorldAside( on ? 1 : 0, out width, out height ) : 0;
	}

	/// <summary>The Vulkan format of a stored picture name (rgba16f, rgba8, r16f...), or nought.</summary>
	public static int StorageFormat( string name ) => name switch
	{
		"rgba16f" => 97,
		"rgba8" => 37,
		"r16f" => 76,
		"rg16f" => 83,
		"rgba32f" => 109,
		"r32f" => 100,
		"rgb10_a2" => 64,
		"r11f_g11f_b10f" => 122,
		"compact" => CoreNative.Available ? CoreRender.CompactFormat() : 0,
		_ => 0,
	};

	/// <summary>A stored picture format's shader name, or null - the reverse of StorageFormat.</summary>
	public static string StorageName( int format ) => format switch
	{
		97 => "rgba16f",
		37 => "rgba8",
		76 => "r16f",
		83 => "rg16f",
		109 => "rgba32f",
		100 => "r32f",
		64 => "rgb10_a2",
		122 => "r11f_g11f_b10f",
		_ => null,
	};

	/// <summary>Fills a picture's mip chain from its top, by the fold where given and storable, else blits.</summary>
	public static bool PictureChain( ulong picture, ulong fold = 0 )
		=> CoreNative.Available && CoreRender.PictureChain( picture, fold ) != 0;

	/// <summary>A compute pass over the frame's pictures - see VkFrame.PassDispatch.</summary>
	public static ulong CreatePassPipeline( byte[] spirv )
	{
		if ( !CoreNative.Available || spirv is null || spirv.Length == 0 ) return 0;

		fixed ( byte* code = spirv )
		{
			return CoreRender.PassCreatePipeline( code, spirv.Length );
		}
	}

	/// <summary>Runs a pass with a material, stored picture levels, written buffers and a push block.</summary>
	public static bool DispatchPass( ulong pipeline, ulong material, ReadOnlySpan<ulong> outputs, ReadOnlySpan<int> levels,
		ReadOnlySpan<ulong> buffers, ReadOnlySpan<byte> push, int groupsX, int groupsY, int groupsZ = 1 )
	{
		if ( !CoreNative.Available || pipeline == 0 || outputs.Length != levels.Length ) return false;

		fixed ( ulong* outputPtr = outputs )
		fixed ( int* levelPtr = levels )
		fixed ( ulong* bufferPtr = buffers )
		fixed ( byte* pushPtr = push )
		{
			return CoreRender.PassDispatch( pipeline, material, outputPtr, levelPtr, outputs.Length,
				bufferPtr, buffers.Length, pushPtr, push.Length, groupsX, groupsY, groupsZ ) != 0;
		}
	}

	/// <summary>A layered depth square for sun pages or a movers map - see CoreSunPages; nought on failure.</summary>
	public static ulong CreateShadowMap( int side, int layers, bool readDepth = false )
		=> CoreNative.Available ? CoreRender.ShadowMapCreate( side, layers, readDepth ) : 0;

	public static void DestroyShadowMap( ulong map )
	{
		if ( CoreNative.Available && map != 0 ) CoreRender.ShadowMapDestroy( map );
	}

	/// <summary>The shadow map the portable frame binds in one of its places, having no picture table: the sun's pages twice, its movers', a lamps' and their movers' - see ShadowCompare in frame.slang.</summary>
	public static void PortableShadowMap( int slot, ulong map )
	{
		if ( CoreNative.Available ) CoreRender.PortableShadowMap( slot, map );
	}

	/// <summary>Where a shadow map sits in the picture table - what a shader is told to read it by.</summary>
	public static uint ShadowMapIndex( ulong map, bool depth = false )
		=> CoreNative.Available ? CoreRender.ShadowMapIndex( map, depth ) : 0;

	/// <summary>Begins drawing a shadow map layer without clearing; EndTarget leaves the map readable.</summary>
	public static bool BeginShadow( ulong map, int layer )
		=> CoreNative.Available && CoreRender.ShadowBegin( map, layer ) != 0;

	/// <summary>Limits the open shadow area's next draws to a rectangle, cleared first where asked.</summary>
	public static void ShadowScissor( int x, int y, int width, int height, bool clear = false )
	{
		if ( CoreNative.Available ) CoreRender.ShadowScissor( x, y, width, height, clear ? 1 : 0 );
	}

	/// <summary>Where in the open shadow layer the next draws land, cleared first where asked.</summary>
	public static void ShadowArea( int x, int y, int width, int height, bool clear )
	{
		if ( CoreNative.Available ) CoreRender.ShadowArea( x, y, width, height, clear ? 1 : 0 );
	}

	/// <summary>Makes the depth buffer readable, between the pre-pass and what reads the shape.</summary>
	public static bool DepthReadable()
		=> CoreNative.Available && CoreRender.DepthReadable() != 0;

	/// <summary>Makes a frame picture readable by table index with no pass open; others left alone.</summary>
	public static bool ReadyToRead( ulong texture )
		=> CoreNative.Available && texture != 0 && CoreRender.ReadyToRead( texture ) != 0;

	/// <summary>Fills a full-size picture's mip chain from its contents (bloom), only when asked.</summary>
	public static bool PictureMips( CoreRender.TargetKind target )
		=> CoreNative.Available && CoreRender.PictureMips( (int)target ) != 0;

	/// <summary>One of those pictures as a texture: 0 colour, 1 normals, 2 depth (the last two the world's).</summary>
	public static ulong TargetTexture( ulong swapchain, CoreRender.TargetKind target, int which = 0 )
		=> CoreNative.Available ? CoreRender.TargetTexture( swapchain, (int)target, which ) : 0;


	/// <summary>Puts the texture quality setting on the device, when it has changed.</summary>
	public static void ApplyTextureQuality()
	{
		if ( !CoreNative.Available ) return;

		var wanted = (int)Rendering.TextureSettings.MaxResolution;

		if ( wanted != textureLimit )
		{
			textureLimit = wanted;
			CoreRender.TextureLimit( wanted );
		}

		//The sampler, which unlike the limit reaches what is already loaded - so it is asked for only
		//when the number moved, and the core does nothing when it is asked for what it already is.
		var taps = Rendering.TextureSettings.Anisotropy;

		if ( Math.Abs( taps - anisotropy ) > 0.01f )
		{
			anisotropy = taps;
			CoreRender.Anisotropy( taps );
		}
	}

	//What the device was last told about anisotropy.
	private static float anisotropy = -1.0f;

	//What the device was last told the texture limit is.
	private static int textureLimit = -1;

	/// <summary>How many frames have reached the screen since the window was attached.</summary>
	public static ulong FrameCount( ulong swapchain )
		=> CoreNative.Available ? CoreRender.FrameCount( swapchain ) : 0;

	//-- the profiler ------------------------------------------------------------

	/// <summary>Opens a GPU+CPU timing zone, read back after the fence; nests, and feeds the CPU profiler.</summary>
	public static void ZoneBegin( string name )
	{
		//Ask the profiler first: nobody is recording nearly always, and a push would be sixty calls a frame for nothing
		if ( zoneDepth < zoneSpans.Length )
			zoneSpans[zoneDepth++] = Rinten.Diagnostics.FrameProfiler.Enabled ? Rinten.Diagnostics.FrameProfiler.Push( name ) : default;

		if ( CoreNative.Available ) CoreRender.ZoneBegin( name );
	}

	/// <summary>Closes the innermost open zone.</summary>
	public static void ZoneEnd()
	{
		if ( CoreNative.Available ) CoreRender.ZoneEnd();

		if ( zoneDepth > 0 ) zoneSpans[--zoneDepth].Dispose();
	}

	private static Rinten.Diagnostics.FrameProfiler.Scope[] zoneSpans = new Rinten.Diagnostics.FrameProfiler.Scope[8];
	private static int zoneDepth;

	/// <summary>What one frame measured about itself.</summary>
	public readonly record struct FrameProfile(
		float GpuMs, float CpuMs, int Draws, long Triangles, int DynamicDraws, int SkinnedDraws,
		long ScratchBytes, int SkippedBinds, int ZonesDropped,
		long TextureBytes, long BufferBytes, int Textures, int Buffers, int Instances,
		int Dispatches, int Indirect,
		(string Name, float GpuMs, float CpuMs, int Depth)[] Zones );

	/// <summary>The viewport's last finished frame, measured, or null; never waits, so a frame or two old.</summary>
	public static FrameProfile? ReadProfile( ulong viewport )
	{
		if ( !CoreNative.Available || viewport == 0 ) return null;

		var stats = stackalloc float[17];
		if ( CoreRender.ProfileStats( viewport, stats, 17 ) < 17 ) return null;

		var zoneCount = (int)stats[8];
		var zones = new (string, float, float, int)[zoneCount];

		var name = stackalloc byte[64];
		var values = stackalloc float[3];

		var kept = 0;

		for ( var index = 0; index < zoneCount; index++ )
		{
			if ( CoreRender.ProfileZone( viewport, index, name, 64, values, 3 ) == 0 ) continue;

			zones[kept++] = (Marshal.PtrToStringUTF8( (nint)name ) ?? string.Empty, values[0], values[1], (int)values[2]);
		}

		if ( kept != zoneCount ) System.Array.Resize( ref zones, kept );

		return new FrameProfile(
			stats[0], stats[1], (int)stats[2], (long)stats[3], (int)stats[4], (int)stats[5],
			(long)stats[6], (int)stats[7], (int)stats[9],
			(long)stats[10], (long)stats[11], (int)stats[12], (int)stats[13], (int)stats[14],
			(int)stats[15], (int)stats[16],
			zones );
	}

	/// <summary>What a frame cost and what our resources hold, without the zones.</summary>
	public readonly record struct FrameSummary( float GpuMs, float CpuMs, long TextureBytes, long BufferBytes );

	/// <summary>GPU/CPU time and memory of the viewport's last finished frame, without allocating</summary>
	public static FrameSummary? ReadFrameSummary( ulong viewport )
	{
		if ( !CoreNative.Available || viewport == 0 ) return null;

		var stats = stackalloc float[14];
		if ( CoreRender.ProfileStats( viewport, stats, 14 ) < 14 ) return null;

		return new FrameSummary( stats[0], stats[1], (long)stats[10], (long)stats[11] );
	}

	//-- pipelines -------------------------------------------------------------

	/// <summary>Whether the device draws by a GPU-written count; else every record draws, unused kept empty.</summary>
	public static bool DrawIndirectCount
	{
		get
		{
			drawIndirectCount ??= CoreNative.Available && CoreRender.DrawIndirectCount() != 0;
			return drawIndirectCount.Value;
		}
	}

	private static bool? drawIndirectCount;

	//A compute pipeline that reads the picture table at set 1; set 0 holds the dispatch's storage buffers
	internal static ulong CreateComputeOverTable( byte[] spirv )
	{
		if ( !CoreNative.Available || spirv is null || spirv.Length == 0 ) return 0;

		fixed ( byte* code = spirv )
		{
			return CoreRender.ComputeCreateOverTable( code, spirv.Length );
		}
	}

	public static ulong CreateCompute( byte[] spirv )
	{
		if ( !CoreNative.Available || spirv is null || spirv.Length == 0 ) return 0;

		fixed ( byte* code = spirv )
		{
			return CoreRender.ComputeCreate( code, spirv.Length );
		}
	}

	//A picture, so a sampler takes a quad's farthest in one fetch and a level is just a number
	/// <summary>A picture to reduce the frame's depth into, with every level.</summary>
	public static ulong CreatePyramid( int width, int height )
		=> CoreNative.Available ? CoreRender.PyramidCreate( width, height ) : 0;

	public static void DestroyPyramid( ulong pyramid )
	{
		if ( CoreNative.Available ) CoreRender.PyramidDestroy( pyramid );
	}

	//Its own maker, as its second set is a read and store level rather than a material
	/// <summary>A compute pipeline for building one level of the pyramid.</summary>
	public static ulong CreatePyramidPipeline( byte[] spirv )
	{
		if ( !CoreNative.Available || spirv is null || spirv.Length == 0 ) return 0;

		fixed ( byte* code = spirv )
		{
			return CoreRender.PyramidCreatePipeline( code, spirv.Length );
		}
	}


	/// <summary>The fold a picture's chain of halvings is filled by - see CorePasses.Chain.</summary>
	public static ulong CreateFoldPipeline( byte[] spirv )
	{
		if ( !CoreNative.Available || spirv is null || spirv.Length == 0 ) return 0;

		fixed ( byte* code = spirv )
		{
			return CoreRender.FoldCreatePipeline( code, spirv.Length );
		}
	}

	/// <summary>The compact format for light past white, as a shader names it.</summary>
	public static string CompactStorageName => CoreNative.Available ? CoreRender.CompactStorageName() : "rgba16f";

	/// <summary>The sky table by elevation and sun angle, as an indexed texture - see TargetKind.SkyTable.</summary>
	public static ulong SkyTableTexture => CoreNative.Available ? CoreRender.SkyTableTexture() : 0;

	/// <summary>A panorama's sky table to bind instead of the drawn one; nought for the drawn one.</summary>
	public static void UseSkyTable( ulong texture )
	{
		if ( CoreNative.Available ) CoreRender.UseSkyTable( texture );
	}

	/// <summary>Records one depth pyramid level; with an args buffer the group count comes from the GPU</summary>
	public static bool BuildPyramid( ulong pipeline, ulong pyramid, int lod, int count, ulong source,
		ReadOnlySpan<byte> push = default, ulong argsBuffer = 0, int argsOffset = 0 )
	{
		if ( !CoreNative.Available || pipeline == 0 || pyramid == 0 ) return false;

		fixed ( byte* pushBytes = push )
		{
			return CoreRender.PyramidBuild( pipeline, pyramid, lod, count, source,
				pushBytes, push.Length, argsBuffer, argsOffset ) != 0;
		}
	}

	/// <summary>Runs one over a grid of groups, inside a begun frame.</summary>
	public static bool Dispatch( ulong pipeline, ReadOnlySpan<ulong> buffers,
		int groupsX, int groupsY = 1, int groupsZ = 1, ReadOnlySpan<byte> push = default,
		ulong texture = 0 )
	{
		if ( !CoreNative.Available || pipeline == 0 || buffers.IsEmpty ) return false;

		fixed ( ulong* handles = buffers )
		fixed ( byte* pushBytes = push )
		{
			return CoreRender.ComputeDispatch( pipeline, handles, buffers.Length,
				texture, pushBytes, push.Length, groupsX, groupsY, groupsZ ) != 0;
		}
	}

	/// <summary>A full-screen draw with buffers of its own at set 2.</summary>
	public static bool DrawObjects( ulong pipeline, int vertexCount, ReadOnlySpan<byte> push,
		ulong texture, ReadOnlySpan<ulong> buffers )
	{
		if ( !CoreNative.Available || pipeline == 0 || buffers.IsEmpty ) return false;

		fixed ( byte* pushBytes = push )
		fixed ( ulong* handles = buffers )
		{
			return CoreRender.DrawObjects( pipeline, vertexCount, pushBytes, push.Length,
				texture, handles, buffers.Length ) != 0;
		}
	}

	//No vertex input (VkFrame.DrawObjectsIndexed): the shape is the index buffer and what the shader pulls
	/// <summary>One shape drawn many times, each instance reading its buffers at set 2.</summary>
	public static bool DrawObjectsIndexed( ulong pipeline, ulong indexBuffer, int indexCount,
		int instanceCount, bool wideIndices, ReadOnlySpan<byte> push,
		ulong texture, ReadOnlySpan<ulong> buffers )
	{
		if ( !CoreNative.Available || pipeline == 0 || indexBuffer == 0 || buffers.IsEmpty ) return false;

		fixed ( byte* pushBytes = push )
		fixed ( ulong* handles = buffers )
		{
			return CoreRender.DrawObjectsIndexed( pipeline, indexBuffer, indexCount, instanceCount,
				wideIndices, pushBytes, push.Length, texture, handles, buffers.Length ) != 0;
		}
	}

	/// <summary>Bytes of instances one DrawBlockInstanced carries: a bone ring slot, alike on every backend.</summary>
	public const int BlockBytes = 128 * 64;

	/// <summary>A mesh drawn as copies whose data sits in a bone-ring slot; the portable instanced draw.</summary>
	public static bool DrawIndexedInstancedBlock( ulong pipeline, ulong vertexBuffer, ulong indexBuffer,
		int indexCount, int instanceCount, ReadOnlySpan<byte> block, ReadOnlySpan<byte> push,
		int firstIndex, int firstVertex, ulong texture )
	{
		if ( !CoreNative.Available || pipeline == 0 || vertexBuffer == 0 || indexBuffer == 0 ) return false;
		if ( indexCount <= 0 || instanceCount <= 0 ) return false;
		if ( block.IsEmpty || block.Length > BlockBytes || block.Length % 64 != 0 ) return false;

		//As in DrawBlockInstanced: the ring clears the rest of the slot itself, so what goes over is the copies
		//this draw has. A push block is a hundred and twenty-eight bytes, so a run of them is whole bones.
		uint where;

		fixed ( byte* data = block )
		{
			where = CoreRender.Bones( (float*)data, block.Length / 64 );
		}

		if ( where == 0 ) return false;

		fixed ( byte* pushBytes = push )
		{
			return CoreRender.DrawIndexedInstancedBlock( pipeline, vertexBuffer, indexBuffer,
				indexCount, instanceCount, where, firstIndex, firstVertex, texture,
				pushBytes, push.Length ) != 0;
		}
	}

	/// <summary>A run of copies whose numbers are in a slot of the frame's bone ring.</summary>
	public static bool DrawBlockInstanced( ulong pipeline, ulong indexBuffer, int indexCount,
		int instanceCount, bool wideIndices, ReadOnlySpan<byte> block, int blockBytes,
		ReadOnlySpan<byte> push, ulong texture )
	{
		if ( !CoreNative.Available || pipeline == 0 || indexBuffer == 0 ) return false;
		if ( blockBytes <= 0 || blockBytes > BlockBytes || block.Length < BlockBytes ) return false;

		uint where;

		fixed ( byte* data = block )
		{
			where = CoreRender.Bones( (float*)data, ( blockBytes + 63 ) / 64 );
		}

		if ( where == 0 ) return false;

		fixed ( byte* pushBytes = push )
		{
			return CoreRender.DrawBlockInstanced( pipeline, indexBuffer, indexCount, instanceCount,
				wideIndices, texture, where, pushBytes, push.Length ) != 0;
		}
	}

	/// <summary>A dispatch whose group counts an earlier dispatch wrote: three words at argsOffset bytes.</summary>
	public static bool DispatchIndirect( ulong pipeline, ReadOnlySpan<ulong> buffers,
		ulong argsBuffer, int argsOffset, ReadOnlySpan<byte> push = default, ulong texture = 0 )
	{
		if ( !CoreNative.Available || pipeline == 0 || buffers.IsEmpty || argsBuffer == 0 ) return false;

		fixed ( ulong* handles = buffers )
		fixed ( byte* pushBytes = push )
		{
			return CoreRender.ComputeDispatchIndirect( pipeline, handles, buffers.Length,
				texture, pushBytes, push.Length, argsBuffer, argsOffset ) != 0;
		}
	}

	//Waits for the device both sides of the copy, so between frames only
	/// <summary>A buffer's contents copied back to the host.</summary>
	public static bool ReadBuffer( ulong buffer, Span<byte> into )
	{
		if ( !CoreNative.Available || buffer == 0 || into.IsEmpty ) return false;

		fixed ( byte* bytes = into )
		{
			return CoreRender.BufferRead( buffer, bytes, into.Length ) == into.Length;
		}
	}

	//A frame being recorded has not run yet; AskReadback reads after it
	/// <summary>Part of a buffer from <paramref name="offset"/> bytes, waiting as ReadBuffer does.</summary>
	public static bool ReadBuffer( ulong buffer, long offset, Span<byte> into )
	{
		if ( !CoreNative.Available || buffer == 0 || into.IsEmpty || offset < 0 ) return false;

		fixed ( byte* bytes = into )
		{
			return CoreRender.BufferReadAt( buffer, offset, bytes, into.Length ) == into.Length;
		}
	}

	//-- what a project's own buffers are given --------------------------------

	/// <summary>Whether a frame is being recorded - a window's, or a view's of its own.</summary>
	public static bool Recording => CoreNative.Available && CoreRender.Recording() != 0;

	/// <summary>Fills a buffer run with one word before the next frame; offset and length in whole words.</summary>
	public static bool FillBuffer( ulong buffer, long offset, long size, uint value )
		=> CoreNative.Available && buffer != 0 && CoreRender.BufferFill( buffer, offset, size, value ) != 0;

	/// <summary>The same where the frame being recorded is, in its order - see VkFrame.FrameFill.</summary>
	public static bool FrameFill( ulong buffer, long offset, long size, uint value )
		=> CoreNative.Available && buffer != 0 && CoreRender.FrameFill( buffer, offset, size, value ) != 0;

	/// <summary>Bytes between two buffers in the recording frame's order - see VkFrame.FrameCopy.</summary>
	public static bool FrameCopy( ulong from, long fromOffset, ulong into, long intoOffset, long bytes )
		=> CoreNative.Available && from != 0 && into != 0 && bytes > 0 &&
			CoreRender.FrameCopy( from, fromOffset, into, intoOffset, bytes ) != 0;

	/// <summary>A draw from a project's own buffers, bound at set 2 in declared order - see CoreBufferDraw.</summary>
	public static bool DrawBuffers( in CoreBufferDraw draw, ReadOnlySpan<byte> push, ReadOnlySpan<ulong> buffers = default )
	{
		if ( !CoreNative.Available || draw.Pipeline == 0 ) return false;

		var copy = draw;

		fixed ( byte* pushBytes = push )
		fixed ( ulong* handles = buffers )
		{
			return CoreRender.DrawBuffers( &copy, pushBytes, push.Length, handles, buffers.Length ) != 0;
		}
	}

	/// <summary>Asks for a buffer run back without waiting (VkReadbacks); the request id, or nought.</summary>
	public static int AskReadback( ulong buffer, long offset, long bytes )
		=> CoreNative.Available && buffer != 0 ? CoreRender.ReadbackAsk( buffer, offset, bytes ) : 0;

	/// <summary>Whether a readback arrived: one with bytes (valid until released), nought pending, -1 never.</summary>
	public static int PollReadback( int id, out ReadOnlySpan<byte> bytes, out bool recorded )
	{
		bytes = default;
		recorded = false;

		if ( !CoreNative.Available || id == 0 ) return -1;

		var state = CoreRender.ReadbackPoll( id, out var data, out var size, out recorded );

		if ( state == 1 ) bytes = new ReadOnlySpan<byte>( data, size );

		return state;
	}

	/// <summary>Done with a request, arrived or not.</summary>
	public static void ReleaseReadback( int id )
	{
		if ( CoreNative.Available && id != 0 ) CoreRender.ReadbackRelease( id );
	}

	/// <summary>One texture level into another: a copy where alike, a converting blit where not.</summary>
	public static bool CopyTexture( ulong from, int fromLevel, ulong into, int intoLevel )
		=> CoreNative.Available && from != 0 && into != 0 && CoreRender.TextureCopy( from, fromLevel, into, intoLevel ) != 0;

	/// <summary>Every level of a texture cleared to a colour, on the device - see VkTextures.ClearTexture.</summary>
	public static bool ClearTexture( ulong texture, Color colour )
		=> CoreNative.Available && texture != 0 && CoreRender.TextureClear( texture, colour.r, colour.g, colour.b, colour.a ) != 0;

	/// <summary>A frame picture into every level of a texture, in frame order.</summary>
	public static bool PictureToTexture( ulong picture, ulong texture )
		=> CoreNative.Available && picture != 0 && texture != 0 && CoreRender.PictureToTexture( picture, texture ) != 0;

	/// <summary>A texture's levels below <paramref name="first"/> blitted from it.</summary>
	public static bool GenerateTextureMips( ulong texture, int first = 0, int count = -1 )
		=> CoreNative.Available && texture != 0 && CoreRender.TextureMips( texture, first, count ) != 0;

	/// <summary>Waits for everything handed to the device so far.</summary>
	public static bool WaitIdle()
		=> CoreNative.Available && CoreRender.WaitIdle() != 0;

	/// <summary>How a depth-tested pipeline compares - the values sbc_render_pipeline_create reads.</summary>
	public enum DepthCompare
	{
		LessOrEqual = 1,
		Equal = 2,
		Always = 3,

	}

	//Max specialization constants per pipeline; ids below CoreResources.FirstShaderFold are the frame's
	internal const int SpecializationCount = 21;

	public static ulong CreatePipeline( byte[] vertexSpirv, byte[] pixelSpirv,
		CoreRender.VertexLayoutKind vertexLayout = CoreRender.VertexLayoutKind.None, bool depthTest = false,
		CoreRender.Topology topology = CoreRender.Topology.Triangles, bool blend = false,
		bool? depthWrite = null, bool cullBack = false, bool depthOnly = false,
		CoreRender.TargetKind target = CoreRender.TargetKind.Screen, bool lines = false,
		bool additive = false, DepthCompare compare = DepthCompare.LessOrEqual,
		bool screen = false, int specialize = -1, bool colourAlone = false, int pictureFormat = -1,
		bool addByAlpha = false, bool multiply = false )
	{
		if ( !CoreNative.Available ) return 0;
		if ( vertexSpirv is not { Length: > 0 } || pixelSpirv is not { Length: > 0 } ) return 0;

		fixed ( byte* vertex = vertexSpirv )
		fixed ( byte* pixel = pixelSpirv )
		{
			var handle = CoreRender.PipelineCreate(
				vertex, vertexSpirv.Length, "main",
				pixel, pixelSpirv.Length, "main",
				//Two means additive, which is what a pass gathering over several draws wants; three screens,
				//which is adding without going past white; five adds by the source's own alpha - see VkPipelines.
				(int)vertexLayout, depthTest ? (int)compare : 0, (int)topology,
				screen ? 3 : additive ? 2 : addByAlpha ? 5 : multiply ? 6 : blend ? 1 : 0,
				( depthWrite ?? depthTest ) ? 1 : 0, cullBack ? 1 : 0, depthOnly ? 1 : 0, (int)target,
				//Raster flags: bit 0 draws edges, bit 1 leaves the world's second picture alone - see VkPipelines
				( lines ? 1 : 0 ) | ( colourAlone ? 2 : 0 ),
				specialize, pictureFormat );

			if ( handle == 0 ) log.Warning( $"no pipeline: {Description}" );

			return handle;
		}
	}

	/// <summary>Draws per-frame vertices (gizmos, lines) via scratch; Vertex is packed here into PackedVertex</summary>
	public static bool DrawDynamic( ulong pipeline, ReadOnlySpan<byte> vertices, int vertexCount,
		int vertexStride, ReadOnlySpan<byte> push = default, ulong texture = 0 )
	{
		if ( !CoreNative.Available || vertices.IsEmpty ) return false;

		if ( vertexStride == sizeof( Vertex ) && vertexCount > 0 )
		{
			var count = Math.Min( vertexCount, vertices.Length / sizeof( Vertex ) );
			var from = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Vertex>( vertices[..( count * sizeof( Vertex ) )] );

			if ( packScratch is null || packScratch.Length < count )
				packScratch = new PackedVertex[Math.Max( count, Math.Max( 1024, ( packScratch?.Length ?? 0 ) * 2 ) )];

			PackedVertex.Pack( from, packScratch );

			vertices = System.Runtime.InteropServices.MemoryMarshal.AsBytes( packScratch.AsSpan( 0, count ) );
			vertexCount = count;
			vertexStride = sizeof( PackedVertex );
		}

		fixed ( byte* data = vertices )
		fixed ( byte* pushBytes = push )
		{
			return CoreRender.DrawDynamic( pipeline, data, vertexCount, vertexStride,
				texture, pushBytes, push.Length ) != 0;
		}
	}

	//Where DrawDynamic packs the vertices it is handed - one a thread, grown and kept.
	[ThreadStatic] private static PackedVertex[] packScratch;

	//-- buffers ---------------------------------------------------------------

	/// <summary>Puts bytes in a device buffer for vertices or indices; zero on failure, see Description.</summary>
	public static ulong CreateBuffer( ReadOnlySpan<byte> data )
	{
		if ( !CoreNative.Available || data.IsEmpty ) return 0;

		fixed ( byte* bytes = data )
		{
			var handle = CoreRender.BufferCreate( bytes, data.Length );

			if ( handle == 0 ) log.Warning( $"no buffer: {Description}" );

			return handle;
		}
	}

	/// <summary>A buffer of that many bytes, rewritten each frame without reallocating; 0 if refused</summary>
	public static ulong ReserveBuffer( long bytes )
	{
		if ( !CoreNative.Available || bytes <= 0 ) return 0;

		var handle = CoreRender.BufferReserve( bytes );

		if ( handle == 0 ) log.Warning( $"no buffer of {bytes} bytes: {Description}" );

		return handle;
	}

	/// <summary>Copies bytes between two buffers on the device.</summary>
	public static bool CopyBuffer( ulong from, long fromOffset, ulong into, long intoOffset, long bytes )
	{
		if ( !CoreNative.Available || from == 0 || into == 0 || bytes <= 0 ) return false;

		if ( CoreRender.BufferCopy( from, fromOffset, into, intoOffset, bytes ) != 0 ) return true;

		log.Warning( $"buffer copy refused: {Description}" );
		return false;
	}

	/// <summary>Writes where the frame is, not ahead of it - see sbc_render_frame_write.</summary>
	public static bool FrameWrite( ulong buffer, long offset, ReadOnlySpan<byte> data )
	{
		if ( !CoreNative.Available || buffer == 0 || data.IsEmpty ) return false;

		fixed ( byte* bytes = data )
		{
			if ( CoreRender.FrameWrite( buffer, offset, bytes, data.Length ) != 0 ) return true;
		}

		log.Warning( $"frame write refused: {Description}" );

		return false;
	}

	/// <summary>A draw with no vertex buffer, its counts from a buffer - see sbc_render_draw_pulled_indirect.</summary>
	public static bool DrawPulledIndirect( ulong pipeline, ulong argsBuffer, int argsOffset, int drawCount,
		ReadOnlySpan<byte> push, ulong texture, ReadOnlySpan<ulong> buffers )
	{
		if ( !CoreNative.Available || pipeline == 0 || argsBuffer == 0 || buffers.IsEmpty ) return false;

		fixed ( byte* pushBytes = push )
		fixed ( ulong* handles = buffers )
		{
			return CoreRender.DrawPulledIndirect( pipeline, argsBuffer, argsOffset, drawCount,
				texture, pushBytes, push.Length, handles, buffers.Length ) != 0;
		}
	}

	/// <summary>The same with a card-written index buffer, five words at argsOffset; how clusters draw.</summary>
	public static bool DrawPulledIndexedIndirect( ulong pipeline, ulong argsBuffer, int argsOffset, ulong indexBuffer,
		ReadOnlySpan<byte> push, ulong texture, ReadOnlySpan<ulong> buffers )
	{
		if ( !CoreNative.Available || pipeline == 0 || argsBuffer == 0 || indexBuffer == 0 || buffers.IsEmpty ) return false;

		fixed ( byte* pushBytes = push )
		fixed ( ulong* handles = buffers )
		{
			return CoreRender.DrawPulledIndexedIndirect( pipeline, argsBuffer, argsOffset, indexBuffer,
				texture, pushBytes, push.Length, handles, buffers.Length ) != 0;
		}
	}

	/// <summary>A list's clusters as workgroups through a mesh-stage pipeline: counts at argsOffset.</summary>
	public static bool DrawMeshIndirect( ulong pipeline, ulong argsBuffer, int argsOffset,
		ReadOnlySpan<byte> push, ulong texture, ReadOnlySpan<ulong> buffers )
	{
		if ( !CoreNative.Available || pipeline == 0 || argsBuffer == 0 || buffers.IsEmpty ) return false;

		fixed ( byte* pushBytes = push )
		fixed ( ulong* handles = buffers )
		{
			return CoreRender.DrawMeshIndirect( pipeline, argsBuffer, argsOffset,
				texture, pushBytes, push.Length, handles, buffers.Length ) != 0;
		}
	}

	/// <summary>Whether the device draws mesh stages.</summary>
	public static bool MeshShaders => CoreNative.Available && CoreRender.MeshShaders;

	/// <summary>Whether an index may be any 32-bit number rather than one under 2^24.</summary>
	public static bool FullIndex
		=> CoreNative.Available && CoreRender.FullIndex() != 0;

	public static bool WriteBuffer( ulong buffer, long offset, ReadOnlySpan<byte> data )
	{
		if ( !CoreNative.Available || buffer == 0 || data.IsEmpty ) return false;

		fixed ( byte* bytes = data )
		{
			if ( CoreRender.BufferWrite( buffer, offset, bytes, data.Length ) != 0 )
				return true;
		}

		log.Warning( $"buffer write refused: {Description}" );

		return false;
	}

	/// <summary>The most instances one call carries, what the bone binding covers; callers split beyond.</summary>
	public const int MaxInstances = 128;

	/// <summary>Destroys a buffer; waits for the GPU first</summary>
	public static void DestroyBuffer( ulong buffer )
	{
		if ( !CoreNative.Available || buffer == 0 ) return;

		CoreRender.BufferDestroy( buffer );
	}

	/// <summary>Draws an indexed mesh in a begun frame; up to 128 push bytes reach both stages.</summary>
	public static bool DrawIndexed( ulong pipeline, ulong vertexBuffer, ulong indexBuffer,
		int indexCount, ReadOnlySpan<byte> push = default, int firstIndex = 0, int firstVertex = 0,
		ulong texture = 0 )
	{
		if ( !CoreNative.Available ) return false;

		fixed ( byte* pushBytes = push )
		{
			return CoreRender.DrawIndexed( pipeline, vertexBuffer, indexBuffer,
				firstIndex, indexCount, firstVertex, texture, pushBytes, push.Length ) != 0;
		}
	}

	/// <summary>Draws a mesh whose shader reads its own vertices out of a buffer.</summary>
	public static bool DrawPulled( ulong pipeline, ulong geometry, ulong data, int vertexCount,
		ReadOnlySpan<byte> push = default, ulong texture = 0 )
	{
		if ( !CoreNative.Available || geometry == 0 || vertexCount <= 0 ) return false;

		fixed ( byte* pushBytes = push )
		{
			return CoreRender.DrawPulled( pipeline, geometry, data, vertexCount,
				texture, pushBytes, push.Length ) != 0;
		}
	}

	/// <summary>Draws a skinned mesh; bones are row-major skin matrices, copied into the frame.</summary>
	public static bool DrawSkinned( ulong pipeline, ulong vertexBuffer, ulong skinBuffer, ulong indexBuffer,
		int indexCount, ReadOnlySpan<System.Numerics.Matrix4x4> bones, ReadOnlySpan<byte> push = default,
		int firstIndex = 0, int firstVertex = 0, ulong texture = 0 )
	{
		if ( !CoreNative.Available || bones.IsEmpty ) return false;

		fixed ( System.Numerics.Matrix4x4* boneData = bones )
		fixed ( byte* pushBytes = push )
		{
			return CoreRender.DrawSkinned( pipeline, vertexBuffer, skinBuffer, indexBuffer,
				firstIndex, indexCount, firstVertex, texture,
				(float*)boneData, bones.Length, pushBytes, push.Length ) != 0;
		}
	}

	/// <summary>Draws a view's world at a quarter to one of its size, and the compose carries it up.</summary>
	public static bool SetViewScale( ulong view, float scale )
		=> CoreNative.Available && view != 0 &&
			CoreRender.ViewScale( view, scale ) != 0;

	//The window's size unless the render scale is below one; the difference is what an upscaler carries
	/// <summary>The size of the picture the current viewport's world is drawn into.</summary>
	public static Vector2 WorldSize()
	{
		if ( !CoreNative.Available ) return default;

		var info = stackalloc int[2];

		CoreRender.WorldSize( info );

		return new Vector2( info[0], info[1] );
	}

	/// <summary>Uploads a pose once per frame and returns its offset for all that frame's draws of it</summary>
	public static uint Bones( System.Numerics.Matrix4x4[] bones )
	{
		if ( !CoreNative.Available || bones is null || bones.Length == 0 ) return 0;

		if ( poses.TryGetValue( bones, out var found ) ) return found;

		uint where;

		fixed ( System.Numerics.Matrix4x4* data = bones )
		{
			where = CoreRender.Bones( (float*)data, bones.Length );
		}

		poses[bones] = where;
		return where;
	}

	/// <summary>Draws a skinned mesh against a pose already in the frame.</summary>
	public static bool DrawSkinnedAt( ulong pipeline, ulong vertexBuffer, ulong skinBuffer, ulong indexBuffer,
		int indexCount, uint boneOffset, ReadOnlySpan<byte> push = default,
		int firstIndex = 0, int firstVertex = 0, ulong texture = 0 )
	{
		if ( !CoreNative.Available || boneOffset == 0 ) return false;

		fixed ( byte* pushBytes = push )
		{
			return CoreRender.DrawSkinnedAt( pipeline, vertexBuffer, skinBuffer, indexBuffer,
				firstIndex, indexCount, firstVertex, texture, boneOffset,
				pushBytes, push.Length ) != 0;
		}
	}

	//Remembered by array like a pose, as one character draws into the world, shadows and other views
	/// <summary>Hands one object's blend shape weights to the frame and returns where, or nought.</summary>
	public static uint MorphWeights( float[] weights )
	{
		if ( !CoreNative.Available || weights is null || weights.Length == 0 ) return 0;

		if ( morphs.TryGetValue( weights, out var found ) ) return found;

		uint where;

		fixed ( float* data = weights )
		{
			where = CoreRender.MorphWeights( data, weights.Length );
		}

		morphs[weights] = where;
		return where;
	}

	/// <summary>Draws a rigid mesh moved by its blend shapes, against weights already in the frame.</summary>
	public static bool DrawMorphedIndexed( ulong pipeline, ulong vertexBuffer,
		ulong runBuffer, ulong deltaBuffer, ulong indexBuffer,
		int indexCount, uint morphOffset, ReadOnlySpan<byte> push = default,
		int firstIndex = 0, int firstVertex = 0, ulong texture = 0, int firstRun = 0 )
	{
		if ( !CoreNative.Available || morphOffset == 0 ) return false;

		fixed ( byte* pushBytes = push )
		{
			return CoreRender.DrawMorphedIndexed( pipeline, vertexBuffer, runBuffer,
				deltaBuffer, indexBuffer, firstIndex, indexCount, firstVertex, firstRun, texture,
				morphOffset, pushBytes, push.Length ) != 0;
		}
	}

	/// <summary>Draws a skinned mesh moved by its blend shapes, against a pose and weights in the frame.</summary>
	public static bool DrawMorphedAt( ulong pipeline, ulong vertexBuffer, ulong skinBuffer,
		ulong runBuffer, ulong deltaBuffer, ulong indexBuffer,
		int indexCount, uint boneOffset, uint morphOffset, ReadOnlySpan<byte> push = default,
		int firstIndex = 0, int firstVertex = 0, ulong texture = 0, int firstRun = 0 )
	{
		if ( !CoreNative.Available || boneOffset == 0 || morphOffset == 0 ) return false;

		fixed ( byte* pushBytes = push )
		{
			return CoreRender.DrawMorphedAt( pipeline, vertexBuffer, skinBuffer, runBuffer,
				deltaBuffer, indexBuffer, firstIndex, indexCount, firstVertex, firstRun, texture,
				boneOffset, morphOffset, pushBytes, push.Length ) != 0;
		}
	}

	//Where each pose handed over this frame was put.
	private static readonly Dictionary<System.Numerics.Matrix4x4[], uint> poses =
		new( ReferenceEqualityComparer.Instance as IEqualityComparer<System.Numerics.Matrix4x4[]> );

	//And the same for the blend shape weights beside them.
	private static readonly Dictionary<float[], uint> morphs =
		new( ReferenceEqualityComparer.Instance as IEqualityComparer<float[]> );

	//-- textures --------------------------------------------------------------

	/// <summary>Loads a compiled .tex_c onto the device, small levels first where streamed; zero on failure.</summary>
	public static ulong CreateTexture( string path )
	{
		if ( !CoreNative.Available ) return 0;

		//A web build's picture, which the page turns into what its device reads.
		if ( CoreUniversalTexture.IsOne( path ) )
		{
			var universal = CoreUniversalTexture.Load( path );

			if ( universal is null )
			{
				log.Warning( $"no texture - it would not load: {path}" );
				return 0;
			}

			//Its small levels first where it is streamed - see CoreTextureStreaming.
			var made = CoreTextureStreaming.Create( path, universal );
			if ( made == 0 ) NoTexture();
			return made;
		}

		var asset = CoreTextureFile.Load( path );

		if ( asset is null )
		{
			log.Warning( $"no texture - it would not load: {path}" );
			return 0;
		}

		//Up as its small levels, finer ones fetched while something near wears it - see CoreTextureStreaming
		return CoreTextureStreaming.Create( path, asset );
	}

	//A compiled picture's size, from a .tex_c or a web build's KTX2; false where it will not load
	internal static bool TextureSize( string path, out int width, out int height )
	{
		width = height = 0;

		if ( CoreUniversalTexture.IsOne( path ) )
		{
			if ( CoreUniversalTexture.Load( path ) is not { } universal ) return false;

			width = universal.Width;
			height = universal.Height;
			return true;
		}

		if ( CoreTextureFile.Load( path ) is not { } file ) return false;

		width = file.Width;
		height = file.Height;
		return true;
	}

	//A compiled texture already read, onto the device; zero on failure
	internal static ulong CreateTexture( CoreTextureFile asset )
	{
		if ( !CoreNative.Available || asset is null ) return 0;

		//Every level's shape and bytes as in the file; copied to the device, then the file is let go
		var mipCount = asset.MipCount;
		Span<int> info = mipCount * 3 <= 96 ? stackalloc int[mipCount * 3] : new int[mipCount * 3];

		asset.Levels( info, out var pixels );

		fixed ( int* mipInfo = info )
		fixed ( byte* bytes = pixels )
		{
			var handle = CoreRender.TextureCreateMips( asset.Width, asset.Height, (int)asset.Format,
				asset.IsSrgb ? 1 : 0, mipCount, mipInfo, bytes, pixels.Length );

			if ( handle == 0 ) NoTexture();

			return handle;
		}
	}

	//Logs a failed texture creation once, with a count of repeats, instead of once a frame
	private static string saidNoTexture;
	private static long noTextureSince;

	private static void NoTexture()
	{
		var why = Description;

		if ( why == saidNoTexture )
		{
			noTextureSince++;
			return;
		}

		//The run of the last one, before the new one takes its place: a reason that changes
		//is worth a line, and how many the old one swallowed is worth knowing beside it.
		if ( noTextureSince > 0 )
			log.Warning( $"no texture: and {noTextureSince} more of \"{saidNoTexture}\"" );

		saidNoTexture = why;
		noTextureSince = 0;

		log.Warning( $"no texture: {why}" );
	}

	/// <summary>Uploads RGBA8 pixels with all mips (width, height, bytes per level); 0 where unsupported</summary>
	public static ulong CreateTextureMips( ReadOnlySpan<byte> pixels, ReadOnlySpan<int> levels, int width, int height, bool srgb )
	{
		if ( !CoreNative.Available || pixels.IsEmpty || levels.Length < 3 ) return 0;

		fixed ( byte* bytes = pixels )
		fixed ( int* info = levels )
		{
			//Format 0 is plain RGBA8; the numbers after it are the compressed formats
			return CoreRender.TextureCreateMips( width, height, 0, srgb ? 1 : 0,
				levels.Length / 3, info, bytes, pixels.Length );
		}
	}

	/// <summary>Four half floats a texel, one level, in the picture table: light, such as baked lighting.</summary>
	public static ulong CreateTextureHalf( ReadOnlySpan<Half> texels, int width, int height )
	{
		if ( !CoreNative.Available || width <= 0 || height <= 0 || texels.Length < width * height * 4 ) return 0;

		var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes( texels[..( width * height * 4 )] );
		var level = stackalloc int[3] { width, height, bytes.Length };

		fixed ( byte* data = bytes )
		{
			return CoreRender.TextureCreateMips( width, height, VkTextures.HalfFormat, 0, 1, level, data, bytes.Length );
		}
	}

	/// <summary>Raw RGBA8 pixels onto the device, one mip level.</summary>
	public static ulong CreateTextureRgba( ReadOnlySpan<byte> pixels, int width, int height, bool srgb )
	{
		if ( !CoreNative.Available || pixels.Length < width * height * 4 ) return 0;

		fixed ( byte* bytes = pixels )
		{
			var handle = CoreRender.TextureCreateRgba( bytes, width, height, srgb ? 1 : 0 );

			if ( handle == 0 ) NoTexture();

			return handle;
		}
	}

	/// <summary>A material; maps but colour may be 0 and fall back to it. Destroy it before its textures.</summary>
	public static ulong CreateMaterial( ulong colour, ulong normal, ulong third, ulong emission,
		float roughness, float metalness, float emissionStrength, float alphaCutoff )
	{
		Span<float> numbers = stackalloc float[MaterialParamFloats];

		numbers.Clear();

		//The first four are what a material carried before a shader could declare its own,
		//and they share the first slot; a number a shader invents gets a slot to itself.
		numbers[0] = roughness;
		numbers[1] = metalness;
		numbers[2] = emissionStrength;
		numbers[3] = alphaCutoff;

		return CreateMaterial( colour, normal, third, emission, numbers );
	}

	/// <summary>Rewrites materials' numbers in the current frame; written says which need remaking.</summary>
	public static void WriteMaterialNumbers( ReadOnlySpan<ulong> materials, ReadOnlySpan<float> numbers, Span<byte> written )
	{
		var count = Math.Min( materials.Length, Math.Min( written.Length, numbers.Length / MaterialParamFloats ) );

		written.Clear();

		if ( !CoreNative.Available || count == 0 ) return;

		fixed ( ulong* handles = materials )
		fixed ( float* values = numbers )
		fixed ( byte* done = written )
		{
			CoreRender.MaterialNumbers( handles, values, count, done );
		}
	}

	/// <summary>How many floats a material's parameters are, four to a slot.</summary>
	public const int MaterialParamSlots = 32;
	public const int MaterialParamFloats = MaterialParamSlots * 4;

	/// <summary>The same, with the numbers the shader asked for.</summary>
	public static ulong CreateMaterial( ulong colour, ulong normal, ulong third, ulong emission,
		ReadOnlySpan<float> parameters )
	{
		if ( !CoreNative.Available || colour == 0 ) return 0;

		Span<float> numbers = stackalloc float[MaterialParamFloats];

		numbers.Clear();

		parameters[..Math.Min( parameters.Length, MaterialParamFloats )].CopyTo( numbers );

		fixed ( float* pointer = numbers )
		{
			return Made( colour, normal, third, emission, pointer );
		}
	}

	private static ulong Made( ulong colour, ulong normal, ulong third, ulong emission,
		float* parameters )
	{
		var handle = CoreRender.MaterialCreate( colour, normal, third, emission,
			parameters );

		if ( handle == 0 ) log.Warning( $"no material: {Description}" );

		return handle;
	}

	/// <summary>New RGBA8 pixels into an existing texture in place; false where it cannot be, make a new one.</summary>
	public static bool UpdateTextureRgba( ulong texture, ReadOnlySpan<byte> pixels, int width, int height )
	{
		if ( !CoreNative.Available || texture == 0 || pixels.Length < width * height * 4 ) return false;

		fixed ( byte* bytes = pixels )
		{
			return CoreRender.TextureUpdateRgba( texture, bytes, width, height ) != 0;
		}
	}

	/// <summary>Writes an RGBA8 rectangle into an existing texture; false means send it whole.</summary>
	public static bool UpdateTextureRgbaRegion( ulong texture, ReadOnlySpan<byte> pixels, int stride,
		int x, int y, int width, int height )
	{
		if ( !CoreNative.Available || texture == 0 || width <= 0 || height <= 0 ) return false;
		if ( pixels.Length < ( ( height - 1 ) * stride ) + ( width * 4 ) ) return false;

		fixed ( byte* bytes = pixels )
		{
			return CoreRender.TextureUpdateRgbaRegion( texture, bytes, stride, x, y, width, height ) != 0;
		}
	}

	//A texture of nothing in format, with levels levels, every one of them there whatever the
	//texture quality - for filling in pieces with WriteTextureLevel. Zero where it would not be made.
	internal static ulong CreateTextureBlank( int width, int height, CoreTextureFile.TextureFormat format, bool srgb, int levels )
		=> CoreNative.Available ? CoreRender.TextureCreateBlank( width, height, (int)format, srgb ? 1 : 0, levels ) : 0;

	//A rectangle of one level of a texture already on the device, in the texture's own format: rows of four-by-four
	//blocks for a block format, stride bytes apart, starting on a block. False where it could not be written.
	internal static bool WriteTextureLevel( ulong texture, int level, int x, int y, int width, int height, ReadOnlySpan<byte> data, int stride )
	{
		if ( !CoreNative.Available || texture == 0 || data.IsEmpty ) return false;

		fixed ( byte* bytes = data )
			return CoreRender.TextureWriteLevel( texture, level, x, y, width, height, bytes, stride ) != 0;
	}

	/// <summary>Destroys a texture. Waits for the GPU first.</summary>
	public static void DestroyTexture( ulong texture )
	{
		if ( !CoreNative.Available || texture == 0 ) return;

		CoreTextureStreaming.Forget( texture );
		CoreRender.TextureDestroy( texture );
	}

	/// <summary>Destroys a pipeline, waiting for the GPU first: safe, but slow, with frames in flight.</summary>
	public static void DestroyPipeline( ulong pipeline )
	{
		if ( !CoreNative.Available || pipeline == 0 ) return;

		CoreRender.PipelineDestroy( pipeline );
	}

	/// <summary>Draws vertices with no buffers in a begun frame, as a full-screen pass wants.</summary>
	public static bool Draw( ulong pipeline, int vertexCount,
		ReadOnlySpan<byte> push = default, ulong texture = 0 )
	{
		if ( !CoreNative.Available ) return false;

		fixed ( byte* data = push )
		{
			return CoreRender.Draw( pipeline, vertexCount, data, push.Length, texture ) != 0;
		}
	}

	//-- shaders ---------------------------------------------------------------

	//Kept for the renderer's callers; CoreShaderCompiler does the work
	/// <summary>Compiles a Slang entry point to SPIR-V, or null with the reason in ShaderError.</summary>
	public static byte[] CompileShader( string path, string entryPoint, CoreShaderCompiler.Stage stage,
		CoreShaderCompiler.Format format = CoreShaderCompiler.Format.Spirv )
	{
		if ( !CoreShaderCompiler.Available ) return null;

		return CoreShaderCompiler.Compile( path, entryPoint, stage, format );
	}

	/// <summary>Compiles an entry point from source text, so variants need no temp files in watched folders.</summary>
	public static byte[] CompileShaderSource( string source, string directory,
		string entryPoint, CoreShaderCompiler.Stage stage,
		CoreShaderCompiler.Format format = CoreShaderCompiler.Format.Spirv )
	{
		if ( !CoreShaderCompiler.Available ) return null;
		if ( string.IsNullOrEmpty( source ) ) return null;

		return CoreShaderCompiler.CompileSource( source, directory ?? ".", entryPoint, stage, format );
	}

	/// <summary>Why the last compile failed.</summary>
	public static string ShaderError => CoreShaderCompiler.LastError;
}
