using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Rinten;

//Swapchains and viewport pictures; uses dynamic rendering, so no render pass objects up front
internal static unsafe partial class VkSwapchain
{
	//How many frames the CPU may be ahead of the GPU. Two: one being drawn while the next is being recorded. Three would hide
	//a little more jitter at the cost of a frame of latency, which is the wrong trade for anything the player is aiming with.
	internal const uint FramesInFlight = 2;

	//How long a frame will wait for an image before deciding the display is not taking any. Long enough that a screen pacing us at
	//its own refresh never reaches it, short enough that a window nobody can see costs one of these and not a loop that never returns.
	internal const ulong AcquireTimeoutNs = 100_000_000ul;

	//Window-independent viewport pictures; a swapchain has one, and so does a view that never presents
	internal sealed class Pictures
	{
		/// <summary>The picture the world's pass draws into instead of the screen, kept across frames - see VkFrame.WorldAside.</summary>
		public ulong Aside;

		/// <summary>Whether the world's pass goes into it now, and whether it has been cleared this frame.</summary>
		public bool AsideOn, AsideBegun;

		/// <summary>How many pictures deep the depth is: one, or one for each of a headset's eyes where a view draws them at once - see ViewCreate. Said before the pictures are made and kept across a remake.</summary>
		public uint Layers = 1;

		/// <summary>How big the world is drawn. The screen's size times the render scale, and the same thing where the scale is one.</summary>
		public Extent2D Extent;

		/// <summary>Final picture size; the world may draw smaller, but UI, text and gizmos draw at this size</summary>
		public Extent2D ScreenExtent;

		/// <summary>Screen-sized depth when the world draws smaller; world depth is blitted up before the screen pass</summary>
		public VkImage ScreenDepthImage;
		public DeviceMemory ScreenDepthMemory;
		public ImageView ScreenDepthView;
		public ImageLayout ScreenDepthLayout;

		/// <summary>And it by name, for the fold that reads it - see Swapchain.DepthOnScreen.</summary>
		public ulong ScreenDepthHandle;

		/// <summary>Whether this frame's pre-pass wrote it - see Swapchain.DepthOnScreen.</summary>
		public bool ScreenDepthFilled;

		/// <summary>The world, drawn in half floats, before the compose takes it to the screen.</summary>
		public VkImage SceneImage;
		public DeviceMemory SceneMemory;
		public ImageView SceneView;
		public ImageView SceneTop;
		public ulong SceneHandle;

		/// <summary>Mip levels under the world picture's top, sized for bloom; filled only when a pass asks</summary>
		public uint SceneLevels;

		/// <summary>Whether all levels left the initial layout, since the descriptor names every level; done once</summary>
		public bool SceneMipsReady;

		/// <summary>The normals and roughness beside it, so the passes after the world can ask about surfaces they never saw drawn.</summary>
		public VkImage NormalImage;
		public DeviceMemory NormalMemory;
		public ImageView NormalView;
		public ulong NormalHandle;

		/// <summary>The other full-size picture - see TargetPost. Made on first use.</summary>
		public VkImage PostImage;
		public DeviceMemory PostMemory;
		public ImageView PostView;
		public ulong PostHandle;

		/// <summary>Its top level on its own, because a pass writes one level and the view above is the whole image - and the whole image is a chain now.</summary>
		public ImageView PostTop;

		//The same chain the world's picture has, and for the same reader: a bloom blurs what it has already kept, and what it has already kept is written here. See VkFrame.PictureMips.
		/// <summary>How many halvings it carries, and whether the ones under the top have been moved out of the layout an image is made in.</summary>
		public uint PostLevels;
		public bool PostMipsReady;

		/// <summary>How far what moved has moved - see TargetVelocity.</summary>
		public VkImage VelocityImage;
		public DeviceMemory VelocityMemory;
		public ImageView VelocityView;
		public ulong VelocityHandle;

		/// <summary>The screen pass's multisampled colour and depth, resolved into the screen and DepthImage.</summary>
		public VkImage SampledColourImage, SampledDepthImage;
		public DeviceMemory SampledColourMemory, SampledDepthMemory;
		public ImageView SampledColourView, SampledDepthView;

		/// <summary>What they were made for, and whether they have left the layout an image is made in.</summary>
		public uint Samples;
		public Format SampledFormat;
		public Extent2D SampledExtent;
		public bool SampledReady;

		/// <summary>The depth buffer: written by the world, read by the passes that ask about its shape.</summary>
		public VkImage DepthImage;
		public DeviceMemory DepthMemory;
		public ImageView DepthView;
		public ulong DepthHandle;

		/// <summary>Two pictures the size of the window, for the passes after the compose - see TargetFinal.</summary>
		public readonly VkImage[] FinalImage = new VkImage[2];
		public readonly DeviceMemory[] FinalMemory = new DeviceMemory[2];
		public readonly ImageView[] FinalView = new ImageView[2];
		public readonly ulong[] FinalHandle = new ulong[2];
		public readonly ImageLayout[] FinalLayout = new ImageLayout[2];

		/// <summary>Which size they were made for, so a resize remakes them.</summary>
		public Extent2D FinalExtent;

		/// <summary>The world's two pictures at the window's size - see TargetDisplay: each with its chain of halvings, a view over the chain and one over the top.</summary>
		public readonly VkImage[] DisplayImage = new VkImage[2];
		public readonly DeviceMemory[] DisplayMemory = new DeviceMemory[2];
		public readonly ImageView[] DisplayView = new ImageView[2];
		public readonly ImageView[] DisplayTop = new ImageView[2];
		public readonly ulong[] DisplayHandle = new ulong[2];
		public readonly ImageLayout[] DisplayLayout = new ImageLayout[2];
		public readonly bool[] DisplayMipsReady = new bool[2];
		public uint DisplayLevels;
		public Extent2D DisplayExtent;

		/// <summary>Where each of them stands right now. Tracked rather than assumed: a pass may be opened, closed and opened again in one frame, and every one of those is a different layout.</summary>
		public ImageLayout SceneLayout;
		public ImageLayout NormalLayout;
		public ImageLayout VelocityLayout;
		public ImageLayout PostLayout;

		/// <summary>Whether this frame has made the velocities mean something - either by drawing into them or by clearing them to nothing.</summary>
		public bool VelocityReady;
		public ImageLayout DepthLayout;

		/// <summary>Whether the world has been drawn into them this frame, and whether anything has written the depth - the screen's pass must not clear the depth the world left behind, or the gizmos lose it.</summary>
		public bool SceneDrawn;
		public bool DepthFilled;

		/// <summary>The headset's foveation map for these pictures, and where its middle is - see VkFovea. Null for everything that is not an eye. Not forgotten with the pictures: its settings are said again every frame, and its GPU half goes with the rest of them in DestroyPictures.</summary>
		public VkFovea.Map Fovea;

		/// <summary>Everything back to nothing, which is what <c>p = Pictures{}</c> was.</summary>
		public void Forget()
		{
			Extent = default;
			ScreenExtent = default;

			ScreenDepthImage = default;
			ScreenDepthMemory = default;
			ScreenDepthView = default;
			ScreenDepthLayout = ImageLayout.Undefined;
			ScreenDepthHandle = 0;
			ScreenDepthFilled = false;

			for ( var i = 0; i < 2; ++i )
			{
				FinalImage[i] = default;
				FinalMemory[i] = default;
				FinalView[i] = default;
				FinalHandle[i] = 0;
				FinalLayout[i] = ImageLayout.Undefined;
			}

			FinalExtent = default;

			for ( var i = 0; i < 2; ++i )
			{
				DisplayImage[i] = default;
				DisplayMemory[i] = default;
				DisplayView[i] = default;
				DisplayTop[i] = default;
				DisplayHandle[i] = 0;
				DisplayLayout[i] = ImageLayout.Undefined;
				DisplayMipsReady[i] = false;
			}

			DisplayLevels = 0;
			DisplayExtent = default;

			SceneImage = default;
			SceneMemory = default;
			SceneView = default;
			SceneTop = default;
			SceneHandle = 0;
			SceneLevels = 0;
			SceneMipsReady = false;

			NormalImage = default;
			NormalMemory = default;
			NormalView = default;
			NormalHandle = 0;

			PostImage = default;
			PostTop = default;
			PostLevels = 0;
			PostMipsReady = false;
			PostMemory = default;
			PostView = default;
			PostHandle = 0;

			VelocityImage = default;
			VelocityMemory = default;
			VelocityView = default;
			VelocityHandle = 0;

			DepthImage = default;
			DepthMemory = default;
			DepthView = default;
			DepthHandle = 0;

			SceneLayout = ImageLayout.Undefined;
			NormalLayout = ImageLayout.Undefined;
			VelocityLayout = ImageLayout.Undefined;
			PostLayout = ImageLayout.Undefined;
			VelocityReady = false;
			DepthLayout = ImageLayout.Undefined;

			SceneDrawn = false;
			DepthFilled = false;
		}
	}

	internal sealed class Swapchain
	{
		public SurfaceKHR Surface;

		/// <summary>What the surface was made from, so it can be recreated if lost while the device survives</summary>
		public int SurfaceKind;
		public ulong SurfaceDisplay;
		public ulong SurfaceWindow;

		/// <summary>The pictures this window's viewport is drawn through.</summary>
		public readonly Pictures Pics = new();

		/// <summary>What the window last said it was. Kept because a rebuild happens a frame or two after the resize that caused it, by which time the only record of the new size is this one.</summary>
		public int RequestedWidth;
		public int RequestedHeight;
		public SwapchainKHR Handle;

		public Format Format = Format.Undefined;
		public Extent2D Extent;

		/// <summary>Which ways the desktop is willing to composite this window, out of the surface itself. Asked for rather than assumed: whether a window can be see-through per pixel is the platform's answer, not ours, and on some of them the only answer is "opaque".</summary>
		public uint SupportedAlpha;

		/// <summary>Whether whoever made it asked for the desktop to show through.</summary>
		public bool WantsAlpha;

		/// <summary>How much of the window's resolution the world is drawn at. One by default, which is the arrangement this renderer had before there was a number here at all: every picture the size of the window. Below one, the world and every pass over it shrink and the interface does not.</summary>
		public float RenderScale = 1.0f;

		/// <summary>Where the world is drawn inside the window (e.g. editor viewport); world pictures are this size</summary>
		public Extent2D WorldWanted;
		public int WorldX;
		public int WorldY;

		/// <summary>Whether the world was drawn after the screen's pass had already begun - interrupted for it, see ScreenInterrupt - so the shape it drew has still to reach the screen's depth when the pass reopens.</summary>
		public bool LiftPending;

		/// <summary>True between ScreenInterrupt and ScreenResume, so only world draws use the world rectangle</summary>
		public bool WorldOnScreen;

		/// <summary>Whether the world being drawn has surfaces to shade, which an adaptive rate map is worth reading the picture for</summary>
		public bool WorldShades = true;

		/// <summary>Depth pre-pass writes the screen depth at the world offset, skipping a copy; see DepthGoesToScreen</summary>
		public bool DepthOnScreen;

		/// <summary>Host copy of the last presented image, alpha included; made only on request (see WantsRead)</summary>
		public VkBuffer ReadBuffer;
		public DeviceMemory ReadMemory;
		public byte* ReadMapped;
		public bool ReadPending;
		public Extent2D ReadExtent;
		public static bool WantsRead;

		/// <summary>Per-frame-in-flight copies of presented images for screen recording; read without waiting</summary>
		public WindowCapture[] Captures;
		public long CaptureCount;
		public static bool WantsCapture;

		/// <summary>Whether frames wait for the display. On until somebody says otherwise, because that is the one mode every driver has.</summary>
		public bool WantsVsync = true;

		/// <summary>Whether the display has stopped handing images back, so that the next frame asks for one rather than waits for one.</summary>
		public bool DisplayStalled;

		public VkImage[] Images = [];
		public ImageView[] Views = [];

		/// <summary>Whether the images may be read by a shader - asked for only where the world is shaded coarser off the last picture, see VkFrame.Adaptive - and each one's place in the picture table once it has been read.</summary>
		public bool Sampled;
		public uint[] ViewSlots = [];

		/// <summary>A copy of what the world looked like, taken mid-frame, for the panels that blur or tint whatever is behind them. Nothing writes it unless somebody asks, and the handle is a texture like any other.</summary>
		public VkImage BackdropImage;
		public DeviceMemory BackdropMemory;
		public ImageView BackdropView;
		public ulong BackdropHandle;
		public bool BackdropTaken;

		/// <summary>How many levels the backdrop has - see BackdropLevelsFor.</summary>
		public uint BackdropLevels;

		/// <summary>One per image, not per frame: present gives no signal when it is done with a semaphore</summary>
		public VkSemaphore[] RenderFinished = [];

		public readonly VkFrame.Frame[] Frames = [new VkFrame.Frame(), new VkFrame.Frame()];

		/// <summary>Which frame we are on, and which image it took.</summary>
		public uint Frame;
		public uint ImageIndex;

		/// <summary>How this one presents, kept so it can be said out loud.</summary>
		public PresentModeKHR PresentMode = PresentModeKHR.FifoKhr;
		public bool Recording;

		/// <summary>Whether the pass that draws the world has begun. It is put off until something actually draws, so a shadow pass can go in front of it: dynamic rendering does not nest, and the sun has to have finished looking before anything asks what it saw.</summary>
		public bool MainPass;

		/// <summary>Whether the world's pass has already been begun once this frame, and so has to be resumed rather than started - loading what is there instead of clearing over it. Taking a backdrop copy is the only thing that interrupts it.</summary>
		public bool MainBegun;

		/// <summary>Samples a pixel the world's pass takes this frame - see VkFrame.MainSamples.</summary>
		public uint MainSamples = 1;

		/// <summary>Whether the world's pass was resumed this frame, and the last - see the depth's store in VkFrame.EnsureMainPass, which keeps the depth only where it may be.</summary>
		public bool ResumedThisFrame, ResumedLastFrame;

		/// <summary>Which of the pictures a pass is filling right now, if any.</summary>
		public bool TargetPass;
		public int Target;

		/// <summary>How big the picture being filled is - the area the pass that opened it drew, and so the viewport every draw in it gets. See VkFrame.SetViewport.</summary>
		public Extent2D TargetExtent;

		/// <summary>The far end of the depth range the draws that follow are squeezed into - one is the whole of it. A layer drawn over the world with depth of its own is drawn under a tiny one: nearer than everything the world wrote, and still in order among its own parts. See VkFrame.SetDepthCeiling.</summary>
		public float DepthCeiling = 1.0f;

		/// <summary>Which picture and level a picture pass is filling - see VkFrame.PictureBegin.</summary>
		public ulong PictureId;
		public int PictureLevel;
		public bool PictureDepth;

		/// <summary>Which shadow map a shadow target is drawing into, and where in its layer the draws land - one bake's rectangle. See VkFrame.ShadowBegin and ShadowArea.</summary>
		public ulong ShadowMap;
		public int ShadowLayer;
		public Rect2D ShadowArea;

		/// <summary>Where within that rectangle the draws may land - all of it unless narrowed, see VkFrame.ShadowScissor.</summary>
		public Rect2D ShadowScissor;

		/// <summary>What the world is cleared to, remembered from begin until the pass actually starts.</summary>
		public readonly float[] Clear = new float[4];

		/// <summary>Set when a present or an acquire says the window has moved on. The next frame rebuilds rather than trying to draw into an image that no longer matches the window.</summary>
		public bool OutOfDate;

		/// <summary>Image still works but mismatches the window (drags); waited out and rebuilt once resizing stops</summary>
		public bool Suboptimal;
		public long SuboptimalSince;

		/// <summary>Set when a present made on the submit thread found the surface gone; the next frame begin of this swapchain builds it again. See VkSubmitter.</summary>
		public bool SurfaceLost;

		public ulong Presented;

		/// <summary>The last present id handed out for it - see CorePresent.</summary>
		public ulong PresentIds;

		/// <summary>The last finished frame, measured.</summary>
		public readonly VkFrame.Profile Profile = new();
	}

	//Every open swapchain by handle, one per window; they share the device, shadows and pipelines
	private static readonly Dictionary<ulong, Swapchain> swapchains = new();

	//One number space for swapchains and views together: both are viewports, and everything
	//that reads a viewport's pictures should not have to know which kind it was handed.
	private static ulong nextHandle = 1;

	//Swapchain recorded into now; the placeholder when no frame is open, so calls quietly do nothing
	internal static readonly Swapchain placeholder = new();

	internal static Swapchain active = placeholder;

	internal static Swapchain Current => active;

	//The pictures the passes are working on: a view's while one is open, and the window's the rest of the time. Views are drawn inside the window's
	//frame, before the window's own pass - so they cost no command buffer, no fence and no present of their own. This is the one thing that says which.
	internal static Pictures currentPictures;

	internal static Pictures Pics => currentPictures ?? Current.Pics;

	//True only when the pre-pass writes the window's screen depth (DepthOnScreen), never a view's
	internal static bool DepthGoesToScreen
	{
		get
		{
			if ( currentPictures is not null ) return false;

			var one = Current;
			if ( one is null || !one.DepthOnScreen || one.WorldWanted.Width == 0 ) return false;

			var p = one.Pics;

			if ( p is null || p.ScreenDepthImage.Handle == 0 || p.ScreenDepthView.Handle == 0 ) return false;
			if ( !OneToOne( one, p ) ) return false;

			return one.WorldX >= 0 && one.WorldY >= 0 &&
				one.WorldX + p.Extent.Width <= p.ScreenExtent.Width &&
				one.WorldY + p.Extent.Height <= p.ScreenExtent.Height;
		}
	}

	//The frame being recorded, whoever it belongs to. A window's frame while one is open, and a view's own when a view
	//is drawn on its own - which is what a thumbnail is: no window, no present, one recording submitted and waited for.
	internal static VkFrame.Frame activeFrame;

	internal static VkFrame.Frame CurrentFrame => activeFrame ?? Current.Frames[Current.Frame];

	//The view being drawn, if it is a view rather than a window.
	internal static View currentView;

	//What the window was going to be cleared to, while a view borrows the field to say what it is cleared to.
	private static readonly float[] windowClear = new float[4];

	private static Swapchain Find( ulong handle )
		=> swapchains.TryGetValue( handle, out var found ) ? found : null;

	//The swapchain a handle names, for the frame path.
	internal static Swapchain FindSwapchain( ulong handle ) => Find( handle );

	//Every set 1 in every viewport marked to be written again before anything records against it - not only whichever is drawing when this is called.
	internal static void MarkEveryFrameSetStale()
	{
		foreach ( var pair in swapchains )
		{
			for ( var i = 0; i < pair.Value.Frames.Length; ++i ) pair.Value.Frames[i].FrameSetStale = true;
		}
	}

	//The pictures a handle names, whether it is a window's or a view's.
	private static Pictures PicturesFor( ulong handle )
	{
		var one = Find( handle );
		if ( one is not null ) return one.Pics;

		var view = FindView( handle );
		return view?.Pics;
	}

	//-- where a rebuild's time goes ----------------------------------------

	private static long swapchainMadeNs;
	private static long picturesMadeNs;
	private static long backdropMadeNs;

	private static readonly double NanosecondsPerTick = 1.0e9 / System.Diagnostics.Stopwatch.Frequency;

	//The CPU's clock, in nanoseconds, for the profiler's other half.
	internal static long NowNs() => (long)( System.Diagnostics.Stopwatch.GetTimestamp() * NanosecondsPerTick );

	//Whether to say where a rebuild's time went. RINTEN_SWAPCHAIN_TIMING=1.
	internal static readonly bool timingRebuilds = TimingRebuildsAsked();

	private static bool TimingRebuildsAsked()
	{
		var said = Environment.GetEnvironmentVariable( "RINTEN_SWAPCHAIN_TIMING" );
		return !string.IsNullOrEmpty( said ) && said[0] != '0';
	}

	//RINTEN_SWAPCHAIN_LEGACY: old pacing (min+1 images, plain FIFO) for comparison
	private static readonly bool legacySwapchain =
		Environment.GetEnvironmentVariable( "RINTEN_SWAPCHAIN_LEGACY" ) is not null;

	//Ns a suboptimal swapchain waits before self-rebuild; never by default (RINTEN_SWAPCHAIN_COALESCE_MS)
	private static readonly long coalesceNs = CoalesceAsked();

	private static long CoalesceAsked()
	{
		var said = Environment.GetEnvironmentVariable( "RINTEN_SWAPCHAIN_COALESCE_MS" );

		//Nothing said means never - see above.
		if ( string.IsNullOrEmpty( said ) ) return long.MaxValue;

		if ( !long.TryParse( said, System.Globalization.CultureInfo.InvariantCulture, out var ms ) ) ms = 0;

		return ( ms < 0 ? 0 : ms ) * 1000000L;
	}

	//-- the surface --------------------------------------------------------

	//Creates a surface for a window handle via whichever surface extension the instance loaded
	private static bool CreateSurface( int kind, ulong display, ulong window )
	{
		SurfaceKHR made = default;

		switch ( kind )
		{
			case VkDevice.WindowWin32:
			{
				if ( VkDevice.khrWin32Surface is null )
				{
					VkDevice.SetText( "the loader has no VK_KHR_win32_surface" );
					return false;
				}

				var hwnd = (nint)window;

				var info = new Win32SurfaceCreateInfoKHR
				{
					SType = StructureType.Win32SurfaceCreateInfoKhr,
					Hwnd = hwnd,

					//The module the window belongs to, rather than ours: they are the same for a game and different for a plugin inside an editor.
					Hinstance = WindowModule( hwnd ),
				};

				if ( VkDevice.khrWin32Surface.CreateWin32Surface( VkDevice.instance, &info, null, &made ) != Result.Success )
					return false;

				Current.Surface = made;
				return true;
			}

			case VkDevice.WindowAndroid:
			{
				if ( VkDevice.khrAndroidSurface is null )
				{
					VkDevice.SetText( "the loader has no VK_KHR_android_surface" );
					return false;
				}

				//The ANativeWindow the activity handed over, straight through.
				var info = new AndroidSurfaceCreateInfoKHR
				{
					SType = StructureType.AndroidSurfaceCreateInfoKhr,
					Window = (nint*)window,
				};

				if ( VkDevice.khrAndroidSurface.CreateAndroidSurface( VkDevice.instance, &info, null, &made ) != Result.Success )
					return false;

				Current.Surface = made;
				return true;
			}

			case VkDevice.WindowX11:
			{
				if ( VkDevice.khrXlibSurface is null )
				{
					VkDevice.SetText( "the loader has no VK_KHR_xlib_surface" );
					return false;
				}

				var info = new XlibSurfaceCreateInfoKHR
				{
					SType = StructureType.XlibSurfaceCreateInfoKhr,
					Dpy = (nint*)display,
					Window = (nint)window,
				};

				if ( VkDevice.khrXlibSurface.CreateXlibSurface( VkDevice.instance, &info, null, &made ) != Result.Success )
					return false;

				Current.Surface = made;
				return true;
			}

			case VkDevice.WindowWayland:
			{
				if ( VkDevice.khrWaylandSurface is null )
				{
					VkDevice.SetText( "the loader has no VK_KHR_wayland_surface" );
					return false;
				}

				var info = new WaylandSurfaceCreateInfoKHR
				{
					SType = StructureType.WaylandSurfaceCreateInfoKhr,
					Display = (nint*)display,
					Surface = (nint*)window,
				};

				if ( VkDevice.khrWaylandSurface.CreateWaylandSurface( VkDevice.instance, &info, null, &made ) != Result.Success )
					return false;

				Current.Surface = made;
				return true;
			}

			case VkDevice.WindowMetal:
			{
				//macOS has Vulkan only over Metal, so the surface comes from SDL's CAMetalLayer
				if ( VkDevice.extMetalSurface is null )
				{
					VkDevice.SetText( "the loader has no VK_EXT_metal_surface" );
					return false;
				}

				var info = new MetalSurfaceCreateInfoEXT
				{
					SType = StructureType.MetalSurfaceCreateInfoExt,
					PLayer = (nint*)window,
				};

				if ( VkDevice.extMetalSurface.CreateMetalSurface( VkDevice.instance, &info, null, &made ) != Result.Success )
					return false;

				Current.Surface = made;
				return true;
			}

			default:
				VkDevice.SetText( "that kind of window is not supported on this platform" );
				return false;
		}
	}

	//The module a window belongs to. Win32 only; nothing else has the notion, and the import is only reached on the branch that made a Win32 surface.
	private static nint WindowModule( nint hwnd )
	{
		if ( !OperatingSystem.IsWindows() ) return 0;

		//GWLP_HINSTANCE.
		return GetWindowLongPtrW( hwnd, -6 );
	}

	[System.Runtime.InteropServices.LibraryImport( "user32.dll" )]
	private static partial nint GetWindowLongPtrW( nint window, int index );

	//Prefers an sRGB format so hardware converts from linear; else the first offered
	private static SurfaceFormatKHR ChooseFormat()
	{
		uint count = 0;
		VkDevice.khrSurface.GetPhysicalDeviceSurfaceFormats( VkDevice.physicalDevice, Current.Surface, ref count, null );

		if ( count == 0 )
			return new SurfaceFormatKHR( Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr );

		var formats = new SurfaceFormatKHR[count];

		fixed ( SurfaceFormatKHR* into = formats )
		{
			VkDevice.khrSurface.GetPhysicalDeviceSurfaceFormats( VkDevice.physicalDevice, Current.Surface, ref count, into );

			for ( var i = 0u; i < count; ++i )
			{
				var srgb = into[i].Format == Format.B8G8R8A8Srgb || into[i].Format == Format.R8G8B8A8Srgb;

				if ( srgb && into[i].ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr ) return into[i];
			}

			return into[0];
		}
	}

	//FIFO (vsync) unless the window disabled the wait; then immediate, else mailbox, else FIFO
	private static PresentModeKHR ChoosePresentMode()
	{
		uint count = 0;
		VkDevice.khrSurface.GetPhysicalDeviceSurfacePresentModes( VkDevice.physicalDevice, Current.Surface, ref count, null );

		var immediate = false;
		var relaxed = false;
		var mailbox = false;

		if ( count > 0 )
		{
			var modes = new PresentModeKHR[count];

			fixed ( PresentModeKHR* into = modes )
			{
				VkDevice.khrSurface.GetPhysicalDeviceSurfacePresentModes( VkDevice.physicalDevice, Current.Surface, ref count, into );

				for ( var i = 0u; i < count; ++i )
				{
					if ( into[i] == PresentModeKHR.ImmediateKhr ) immediate = true;
					if ( into[i] == PresentModeKHR.FifoRelaxedKhr ) relaxed = true;
					if ( into[i] == PresentModeKHR.MailboxKhr ) mailbox = true;
				}
			}
		}

		if ( Current.WantsVsync )
		{
			if ( legacySwapchain ) return PresentModeKHR.FifoKhr;

			//FIFO relaxed when available: a late frame shows at once (with tearing) instead of halving the rate
			if ( relaxed ) return PresentModeKHR.FifoRelaxedKhr;

			return PresentModeKHR.FifoKhr;
		}

		//Immediate, not mailbox: compositors pace mailbox to refresh, so fps_max would be ignored
		if ( immediate ) return PresentModeKHR.ImmediateKhr;

		return mailbox ? PresentModeKHR.MailboxKhr : PresentModeKHR.FifoKhr;
	}

	//Premultiplied alpha first, as transparent windows hold; else opaque. Usage adds backdrop reads
	private static ImageUsageFlags ChooseUsage( ImageUsageFlags supported )
	{
		var usage = ImageUsageFlags.ColorAttachmentBit;

		if ( ( supported & ImageUsageFlags.TransferSrcBit ) != 0 )
			usage |= ImageUsageFlags.TransferSrcBit;

		//Read by the pass that lays the next frame's shading rates - see VkFrame.Adaptive. Only on a device that shades by them.
		if ( VkFovea.Adaptive && ( supported & ImageUsageFlags.SampledBit ) != 0 )
			usage |= ImageUsageFlags.SampledBit;

		return usage;
	}

	//One the surface says it takes, always. A desktop takes opaque and that is the end of it; a phone's window commonly takes nothing but
	//"whatever the system does with it", and asked for opaque it refuses the swapchain - so opaque is the first choice and not the only one.
	private static CompositeAlphaFlagsKHR ChooseCompositeAlpha( CompositeAlphaFlagsKHR supported, bool wanted )
	{
		static bool Has( CompositeAlphaFlagsKHR supported, CompositeAlphaFlagsKHR one ) => ( supported & one ) != 0;

		if ( wanted )
		{
			if ( Has( supported, CompositeAlphaFlagsKHR.PreMultipliedBitKhr ) ) return CompositeAlphaFlagsKHR.PreMultipliedBitKhr;
			if ( Has( supported, CompositeAlphaFlagsKHR.PostMultipliedBitKhr ) ) return CompositeAlphaFlagsKHR.PostMultipliedBitKhr;
		}

		if ( Has( supported, CompositeAlphaFlagsKHR.OpaqueBitKhr ) || supported == 0 ) return CompositeAlphaFlagsKHR.OpaqueBitKhr;
		if ( Has( supported, CompositeAlphaFlagsKHR.InheritBitKhr ) ) return CompositeAlphaFlagsKHR.InheritBitKhr;
		if ( Has( supported, CompositeAlphaFlagsKHR.PreMultipliedBitKhr ) ) return CompositeAlphaFlagsKHR.PreMultipliedBitKhr;

		return CompositeAlphaFlagsKHR.PostMultipliedBitKhr;
	}

	//Identity transform when allowed, so the system rotates for us instead of the game coming up sideways
	private static SurfaceTransformFlagsKHR ChooseTransform( in SurfaceCapabilitiesKHR capabilities )
		=> ( capabilities.SupportedTransforms & SurfaceTransformFlagsKHR.IdentityBitKhr ) != 0
			? SurfaceTransformFlagsKHR.IdentityBitKhr
			: capabilities.CurrentTransform;

	//How many halvings a picture this size has in it, at most this many. Never nought: a picture always has its top level,
	//however small the window has been dragged, and an image asked for with no levels at all is not one Vulkan will make.
	internal static uint Levels( Extent2D extent, uint most )
	{
		var side = Math.Max( extent.Width, extent.Height );

		uint levels = 1;

		while ( side > 1 && levels < most )
		{
			side >>= 1;
			levels++;
		}

		return levels;
	}

	//Frees the window-size pair of pictures (see TargetDisplay)
	internal static void DropDisplay( Pictures p, bool deferred )
	{
		for ( var i = 0; i < 2; ++i )
		{
			DropHandle( p.DisplayHandle[i], deferred );
			DropImage( default, default, p.DisplayTop[i], deferred );
			DropImage( p.DisplayImage[i], p.DisplayMemory[i], p.DisplayView[i], deferred );

			p.DisplayHandle[i] = 0;
			p.DisplayImage[i] = default;
			p.DisplayMemory[i] = default;
			p.DisplayView[i] = default;
			p.DisplayTop[i] = default;
			p.DisplayLayout[i] = ImageLayout.Undefined;
			p.DisplayMipsReady[i] = false;
		}

		p.DisplayLevels = 0;
		p.DisplayExtent = default;
	}

	internal static uint LevelsForBloom( Extent2D extent )
	{
		//Fifteen texels across at the widest, which is where 1920 lands on eight - see the constant in bloom.shader, which is this one.
		var widest = Math.Log2( Math.Max( extent.Width, 1u ) / 15.0 );

		var most = (uint)Math.Max( 8.0, Math.Ceiling( widest ) + 1.0 );

		return Levels( extent, most );
	}

	//Backdrop levels: full size plus three halvings, so the UI blur reads the level its radius needs
	internal static uint BackdropLevelsFor( Extent2D extent )
	{
		var levels = 1u;
		var side = Math.Min( extent.Width, extent.Height );

		while ( levels < 4 && ( side >> (int)levels ) >= 8 ) levels++;

		return levels;
	}

	internal static bool MakePicture( Format format, Extent2D extent, uint mips, ImageUsageFlags usage,
		out VkImage outImage, out DeviceMemory outMemory, out ImageView outView, uint layers = 1, uint samples = 1 )
	{
		outImage = default;
		outMemory = default;
		outView = default;

		var vk = VkDevice.vk;
		var device = VkDevice.device;

		var info = new ImageCreateInfo
		{
			SType = StructureType.ImageCreateInfo,
			ImageType = ImageType.Type2D,
			Format = format,
			Extent = new Extent3D( extent.Width, extent.Height, 1 ),
			MipLevels = mips,
			ArrayLayers = layers,
			Samples = samples > 1 ? (SampleCountFlags)samples : SampleCountFlags.Count1Bit,
			Tiling = ImageTiling.Optimal,
			Usage = usage,
			InitialLayout = ImageLayout.Undefined,
		};

		VkImage image;
		if ( vk.CreateImage( device, &info, null, &image ) != Result.Success ) return false;

		//A picture only ever drawn in and resolved need never leave the tile, where the device has memory for that
		var transient = ( usage & ImageUsageFlags.TransientAttachmentBit ) != 0;

		outImage = image;

		MemoryRequirements requirements;
		vk.GetImageMemoryRequirements( device, image, &requirements );

		var allocate = new MemoryAllocateInfo
		{
			SType = StructureType.MemoryAllocateInfo,
			AllocationSize = requirements.Size,
			MemoryTypeIndex = transient
				? VkDevice.FindMemoryType( requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.LazilyAllocatedBit )
				: uint.MaxValue,
		};

		if ( allocate.MemoryTypeIndex == uint.MaxValue )
			allocate.MemoryTypeIndex = VkDevice.FindMemoryType( requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit );

		DeviceMemory memory;

		if ( allocate.MemoryTypeIndex == uint.MaxValue ||
			vk.AllocateMemory( device, &allocate, null, &memory ) != Result.Success )
		{
			return false;
		}

		outMemory = memory;
		VkDevice.CountTarget( memory, requirements.Size );

		if ( vk.BindImageMemory( device, image, memory, 0 ) != Result.Success ) return false;

		var viewInfo = new ImageViewCreateInfo
		{
			SType = StructureType.ImageViewCreateInfo,
			Image = image,
			ViewType = layers > 1 ? ImageViewType.Type2DArray : ImageViewType.Type2D,
			Format = format,
			SubresourceRange = new ImageSubresourceRange( format == VkDevice.DepthFormat ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit,
				0, mips, 0, layers ),
		};

		ImageView view;
		if ( vk.CreateImageView( device, &viewInfo, null, &view ) != Result.Success ) return false;

		outView = view;
		return true;
	}

	//Frees one viewport's pictures; deferred sends them to the graveyard since frames may be in flight
	internal static void DestroyPictures( Pictures p, bool deferred = false )
	{
		//The handles go first. A texture naming a dead image view is worse than no texture at all.
		DropHandle( p.SceneHandle, deferred );
		DropHandle( p.NormalHandle, deferred );
		DropHandle( p.VelocityHandle, deferred );
		DropHandle( p.PostHandle, deferred );
		DropHandle( p.DepthHandle, deferred );

		//Back to the pool, which lets it go once nothing of its size is asked for
		if ( p.Aside != 0 ) VkPictures.Release( p.Aside );
		p.Aside = 0;

		for ( var i = 0; i < 2; ++i )
		{
			DropHandle( p.FinalHandle[i], deferred );
			DropImage( p.FinalImage[i], p.FinalMemory[i], p.FinalView[i], deferred );

			p.FinalHandle[i] = 0;
			p.FinalImage[i] = default;
			p.FinalMemory[i] = default;
			p.FinalView[i] = default;
			p.FinalLayout[i] = ImageLayout.Undefined;
		}

		p.FinalExtent = default;

		DropDisplay( p, deferred );

		//Two views on the one picture: the whole chain and the top of it.
		DropImage( default, default, p.SceneTop, deferred );
		DropImage( p.SceneImage, p.SceneMemory, p.SceneView, deferred );

		DropImage( p.NormalImage, p.NormalMemory, p.NormalView, deferred );
		DropImage( p.VelocityImage, p.VelocityMemory, p.VelocityView, deferred );
		DropImage( default, default, p.PostTop, deferred );
		DropImage( p.PostImage, p.PostMemory, p.PostView, deferred );
		DropImage( p.DepthImage, p.DepthMemory, p.DepthView, deferred );
		DropSampled( p, deferred );
		DropHandle( p.ScreenDepthHandle, deferred );
		DropImage( p.ScreenDepthImage, p.ScreenDepthMemory, p.ScreenDepthView, deferred );

		VkFovea.Release( p.Fovea, deferred );

		p.Forget();
	}

	//The multisampled pair let go, to be made again at the next pass that wants them
	internal static void DropSampled( Pictures p, bool deferred )
	{
		DropImage( p.SampledColourImage, p.SampledColourMemory, p.SampledColourView, deferred );
		DropImage( p.SampledDepthImage, p.SampledDepthMemory, p.SampledDepthView, deferred );

		p.SampledColourImage = p.SampledDepthImage = default;
		p.SampledColourMemory = p.SampledDepthMemory = default;
		p.SampledColourView = p.SampledDepthView = default;
		p.Samples = 0;
		p.SampledFormat = Format.Undefined;
		p.SampledExtent = default;
		p.SampledReady = false;
	}

	//The multisampled pair for a pass of this colour, at the screen's size and layers: kept while all of that holds
	internal static bool EnsureSampled( Pictures p, Format colour, uint samples )
	{
		if ( p.Samples == samples && p.SampledFormat == colour && p.SampledExtent.Width == p.ScreenExtent.Width &&
			p.SampledExtent.Height == p.ScreenExtent.Height && p.SampledColourView.Handle != 0 ) return true;

		DropSampled( p, deferred: true );

		const ImageUsageFlags drawn = ImageUsageFlags.TransientAttachmentBit;

		if ( !MakePicture( colour, p.ScreenExtent, 1, ImageUsageFlags.ColorAttachmentBit | drawn,
				out p.SampledColourImage, out p.SampledColourMemory, out p.SampledColourView, p.Layers, samples ) ||
			!MakePicture( VkDevice.DepthFormat, p.ScreenExtent, 1, ImageUsageFlags.DepthStencilAttachmentBit | drawn,
				out p.SampledDepthImage, out p.SampledDepthMemory, out p.SampledDepthView, p.Layers, samples ) )
		{
			DropSampled( p, deferred: true );
			return false;
		}

		p.Samples = samples;
		p.SampledFormat = colour;
		p.SampledExtent = p.ScreenExtent;

		return true;
	}

	internal static void DropImage( VkImage image, DeviceMemory memory, ImageView view, bool deferred )
	{
		if ( deferred )
		{
			VkDevice.RetireImage( image, memory, view );
			return;
		}

		VkDevice.DestroyView( view );
		if ( image.Handle != 0 ) VkDevice.vk.DestroyImage( VkDevice.device, image, null );
		VkDevice.UncountTarget( memory );
		if ( memory.Handle != 0 ) VkDevice.vk.FreeMemory( VkDevice.device, memory, null );
	}

	internal static void DropHandle( ulong handle, bool deferred )
	{
		if ( handle == 0 ) return;

		if ( deferred ) VkTextures.Retire( handle );
		else VkTextures.Release( handle );
	}

	//Everything one viewport draws through, at a given size: the world's picture, the normals, the depth, the IDs. A window's
	//viewport and a view without a window are made the same way - which is the point of the two being the same thing.
	internal static bool CreatePictures( Pictures p, Extent2D screen, float scale, Extent2D wanted = default )
	{
		var vk = VkDevice.vk;
		var device = VkDevice.device;

		//The world's size, which is the screen's below a scale of one. Rounded rather than truncated,
		//and never to nothing: a zero-sized attachment is not a small picture, it is an invalid one.
		var extent = Scaled( wanted.Width > 0 && wanted.Height > 0 ? WorldExtent( screen, wanted ) : screen, scale );

		p.Extent = extent;
		p.ScreenExtent = screen;

		var depthInfo = new ImageCreateInfo
		{
			SType = StructureType.ImageCreateInfo,
			ImageType = ImageType.Type2D,
			Format = VkDevice.DepthFormat,
			Extent = new Extent3D( extent.Width, extent.Height, 1 ),
			MipLevels = 1,
			ArrayLayers = p.Layers,
			Samples = SampleCountFlags.Count1Bit,
			Tiling = ImageTiling.Optimal,

			//Read as well as written: what the world was shaped like is what the occlusion and the reflections are worked out from.
			//And copied from, where the world is smaller than the screen and its shape has to reach the screen's own depth buffer.
			Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit |
				ImageUsageFlags.TransferSrcBit,
			InitialLayout = ImageLayout.Undefined,
		};

		{
			VkImage image;

			if ( vk.CreateImage( device, &depthInfo, null, &image ) != Result.Success )
			{
				VkDevice.SetText( "the depth buffer would not be made" );
				return false;
			}

			p.DepthImage = image;

			MemoryRequirements requirements;
			vk.GetImageMemoryRequirements( device, image, &requirements );

			var allocate = new MemoryAllocateInfo
			{
				SType = StructureType.MemoryAllocateInfo,
				AllocationSize = requirements.Size,
				MemoryTypeIndex = VkDevice.FindMemoryType( requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit ),
			};

			DeviceMemory memory;

			if ( allocate.MemoryTypeIndex == uint.MaxValue ||
				vk.AllocateMemory( device, &allocate, null, &memory ) != Result.Success )
			{
				VkDevice.SetText( "the depth buffer's memory would not be had" );
				return false;
			}

			p.DepthMemory = memory;
			VkDevice.CountTarget( memory, requirements.Size );

			if ( vk.BindImageMemory( device, image, memory, 0 ) != Result.Success )
			{
				VkDevice.SetText( "the depth buffer's memory would not be had" );
				return false;
			}
		}

		var depthViewInfo = new ImageViewCreateInfo
		{
			SType = StructureType.ImageViewCreateInfo,
			Image = p.DepthImage,
			ViewType = p.Layers > 1 ? ImageViewType.Type2DArray : ImageViewType.Type2D,
			Format = VkDevice.DepthFormat,
			SubresourceRange = new ImageSubresourceRange( ImageAspectFlags.DepthBit, 0, 1, 0, p.Layers ),
		};

		{
			ImageView view;

			if ( vk.CreateImageView( device, &depthViewInfo, null, &view ) != Result.Success )
			{
				VkDevice.SetText( "the depth buffer's view would not be made" );
				return false;
			}

			p.DepthView = view;
		}

		//Portable tier draws straight to the window, so no intermediate pictures are made
		if ( VkDevice.Tier == RenderTier.Portable )
		{
			if ( p.Layers == 1 ) p.DepthHandle = VkTextures.Adopt( p.DepthView, VkDevice.pointSampler );
			return true;
		}

		//World picture with enough mip levels for bloom; see LevelsForBloom
		var sceneLevels = LevelsForBloom( extent );

		if ( MakePicture( VkDevice.WorldFormat, extent, sceneLevels,
			ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit |
			ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
			out var sceneImage, out var sceneMemory, out var sceneView ) )
		{
			p.SceneImage = sceneImage;
			p.SceneMemory = sceneMemory;
			p.SceneView = sceneView;
			p.SceneLevels = sceneLevels;

			//The attachment names one level of its own, because a pass writes a level and the view above is the whole image.
			var topInfo = new ImageViewCreateInfo
			{
				SType = StructureType.ImageViewCreateInfo,
				Image = sceneImage,
				ViewType = ImageViewType.Type2D,
				Format = VkDevice.WorldFormat,
				SubresourceRange = new ImageSubresourceRange( ImageAspectFlags.ColorBit, 0, 1, 0, 1 ),
			};

			ImageView top;

			if ( vk.CreateImageView( device, &topInfo, null, &top ) == Result.Success )
			{
				p.SceneTop = top;
				p.SceneHandle = VkTextures.Adopt( sceneView, VkDevice.clampSampler );
			}
		}

		if ( MakePicture( VkDevice.NormalFormat, extent, 1,
			ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
			out var normalImage, out var normalMemory, out var normalView ) )
		{
			p.NormalImage = normalImage;
			p.NormalMemory = normalMemory;
			p.NormalView = normalView;
			p.NormalHandle = VkTextures.Adopt( normalView, VkDevice.pointSampler );

			//The velocities are not made here. Nothing but a motion blur reads them, most scenes have none, and at the world's size in four half floats they are
			//sixteen megabytes of a 1080p viewport that would never be written - see VkFrame.MakeVelocity, which makes them the first time something draws into them.
		}

		p.DepthHandle = VkTextures.Adopt( p.DepthView, VkDevice.pointSampler );
		//Screen-sized depth when the world is smaller; dynamic rendering needs attachments >= render area
		if ( extent.Width != screen.Width || extent.Height != screen.Height )
		{
			var screenDepth = new ImageCreateInfo
			{
				SType = StructureType.ImageCreateInfo,
				ImageType = ImageType.Type2D,
				Format = VkDevice.DepthFormat,
				Extent = new Extent3D( screen.Width, screen.Height, 1 ),
				MipLevels = 1,
				ArrayLayers = 1,
				Samples = SampleCountFlags.Count1Bit,
				Tiling = ImageTiling.Optimal,
				//Read as well as written: where the world's pre-pass draws straight into this
				//one, this is what the pyramid is folded from - see Swapchain.DepthOnScreen.
				Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.TransferDstBit |
					ImageUsageFlags.SampledBit,
				InitialLayout = ImageLayout.Undefined,
			};

			VkImage image;

			if ( vk.CreateImage( device, &screenDepth, null, &image ) != Result.Success )
			{
				VkDevice.SetText( "the screen's depth buffer would not be made" );
				return false;
			}

			p.ScreenDepthImage = image;

			MemoryRequirements requirements;
			vk.GetImageMemoryRequirements( device, image, &requirements );

			var allocate = new MemoryAllocateInfo
			{
				SType = StructureType.MemoryAllocateInfo,
				AllocationSize = requirements.Size,
				MemoryTypeIndex = VkDevice.FindMemoryType( requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit ),
			};

			DeviceMemory memory;

			if ( allocate.MemoryTypeIndex == uint.MaxValue ||
				vk.AllocateMemory( device, &allocate, null, &memory ) != Result.Success )
			{
				VkDevice.SetText( "the screen's depth buffer would not be given memory" );
				return false;
			}

			p.ScreenDepthMemory = memory;
			VkDevice.CountTarget( memory, requirements.Size );

			if ( vk.BindImageMemory( device, image, memory, 0 ) != Result.Success )
			{
				VkDevice.SetText( "the screen's depth buffer would not be given memory" );
				return false;
			}

			var viewInfo = new ImageViewCreateInfo
			{
				SType = StructureType.ImageViewCreateInfo,
				Image = image,
				ViewType = ImageViewType.Type2D,
				Format = VkDevice.DepthFormat,
				SubresourceRange = new ImageSubresourceRange( ImageAspectFlags.DepthBit, 0, 1, 0, 1 ),
			};

			ImageView view;

			if ( vk.CreateImageView( device, &viewInfo, null, &view ) != Result.Success )
			{
				VkDevice.SetText( "the screen's depth view would not be made" );
				return false;
			}

			p.ScreenDepthView = view;
			p.ScreenDepthHandle = VkTextures.Adopt( view, VkDevice.pointSampler );
		}

		//Everything above, named. Where a hazard is reported between two images, these are the two ends of it, and a handle says nothing about which.
		VkDevice.NameHandle( ObjectType.Image, p.SceneImage.Handle, "world picture" );
		VkDevice.NameHandle( ObjectType.ImageView, p.SceneView.Handle, "world picture view" );

		VkDevice.NameHandle( ObjectType.Image, p.NormalImage.Handle, "world normals" );
		VkDevice.NameHandle( ObjectType.ImageView, p.NormalView.Handle, "world normals view" );
		VkDevice.NameHandle( ObjectType.Image, p.DepthImage.Handle, "world depth" );
		VkDevice.NameHandle( ObjectType.ImageView, p.DepthView.Handle, "world depth view" );
		VkDevice.NameHandle( ObjectType.Image, p.ScreenDepthImage.Handle, "screen depth" );
		VkDevice.NameHandle( ObjectType.ImageView, p.ScreenDepthView.Handle, "screen depth view" );

		return true;
	}

	//Destroys swapchain children, optionally keeping the swapchain so a rebuild can hand it over
	private static void DestroyImages( bool keepSwapchain = false, bool deferred = false )
	{
		var vk = VkDevice.vk;
		var device = VkDevice.device;

		for ( var i = 0; i < Current.RenderFinished.Length; ++i )
		{
			var semaphore = Current.RenderFinished[i];
			if ( semaphore.Handle == 0 ) continue;

			if ( deferred ) VkDevice.RetireSemaphore( semaphore, FramesInFlight );
			else vk.DestroySemaphore( device, semaphore, null );
		}

		Current.RenderFinished = [];

		//Out of the picture table before the views go - after the frames that may still read them, as the views are.
		foreach ( var slot in Current.ViewSlots ) if ( slot != 0 ) VkDevice.RetireBindless( slot );
		Current.ViewSlots = [];

		for ( var i = 0; i < Current.Views.Length; ++i )
		{
			var view = Current.Views[i];
			if ( view.Handle == 0 ) continue;

			if ( deferred ) VkDevice.RetireImage( default, default, view );
			else VkDevice.DestroyView( view );
		}

		Current.Views = [];

		//The images belong to the swapchain and are not destroyed by us.
		Current.Images = [];

		//Backdrop goes with the window images; retired via graveyard since in-flight frames use its set
		if ( Current.BackdropHandle != 0 )
		{
			if ( deferred ) VkTextures.Retire( Current.BackdropHandle );
			else VkTextures.Release( Current.BackdropHandle );
		}

		if ( deferred )
		{
			VkDevice.RetireImage( Current.BackdropImage, Current.BackdropMemory, Current.BackdropView );
		}
		else
		{
			VkDevice.DestroyView( Current.BackdropView );
			if ( Current.BackdropImage.Handle != 0 ) vk.DestroyImage( device, Current.BackdropImage, null );
			VkDevice.UncountTarget( Current.BackdropMemory );
			if ( Current.BackdropMemory.Handle != 0 ) vk.FreeMemory( device, Current.BackdropMemory, null );
		}

		Current.BackdropHandle = 0;
		Current.BackdropLevels = 0;
		Current.BackdropView = default;
		Current.BackdropImage = default;
		Current.BackdropMemory = default;
		Current.BackdropTaken = false;

		DestroyPictures( Current.Pics, deferred );

		if ( !keepSwapchain && Current.Handle.Handle != 0 )
		{
			if ( deferred ) VkDevice.RetireSwapchain( Current.Handle, FramesInFlight );
			else
			{
				CorePresent.Forget( Current.Handle );
				VkDevice.khrSwapchain.DestroySwapchain( device, Current.Handle, null );
			}

			Current.Handle = default;
		}
	}

	//Make the swapchain and everything that hangs off it. The surface must already exist.
	private static bool CreateImages( int width, int height )
	{
		var vk = VkDevice.vk;
		var device = VkDevice.device;

		SurfaceCapabilitiesKHR capabilities;

		if ( VkDevice.khrSurface.GetPhysicalDeviceSurfaceCapabilities(
			VkDevice.physicalDevice, Current.Surface, &capabilities ) != Result.Success )
		{
			VkDevice.SetText( "the surface would not say how big it can be" );
			return false;
		}

		var extent = capabilities.CurrentExtent;

		//0xFFFFFFFF means the window manager is letting us choose, which is mostly Wayland.
		//Everywhere else the window's own size wins over what the caller thinks it is.
		if ( extent.Width == uint.MaxValue )
		{
			extent.Width = (uint)width;
			extent.Height = (uint)height;
		}

		if ( extent.Width < capabilities.MinImageExtent.Width ) extent.Width = capabilities.MinImageExtent.Width;
		if ( extent.Height < capabilities.MinImageExtent.Height ) extent.Height = capabilities.MinImageExtent.Height;
		if ( extent.Width > capabilities.MaxImageExtent.Width ) extent.Width = capabilities.MaxImageExtent.Width;
		if ( extent.Height > capabilities.MaxImageExtent.Height ) extent.Height = capabilities.MaxImageExtent.Height;

		//A minimised window is zero by zero, and a swapchain of that size is not allowed. Say so plainly rather than failing inside the driver.
		if ( extent.Width == 0 || extent.Height == 0 )
		{
			VkDevice.SetText( "the window has no size - it is probably minimised" );
			return false;
		}

		//Driver minimum + 2, at least 3, so the CPU can run a frame ahead instead of blocking on acquire
		var imageCount = legacySwapchain ? capabilities.MinImageCount + 1 : capabilities.MinImageCount + 2;

		if ( !legacySwapchain && imageCount < 3 ) imageCount = 3;

		if ( capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount )
			imageCount = capabilities.MaxImageCount;

		var format = ChooseFormat();

		//What the desktop is willing to do with this window's alpha, kept so that
		//whoever asked for a see-through window can be told whether it actually got one.
		Current.SupportedAlpha = (uint)capabilities.SupportedCompositeAlpha;

		//Previous swapchain is handed to the new one so a resize reuses images instead of flashing black
		var previous = Current.Handle;

		var usage = ChooseUsage( capabilities.SupportedUsageFlags );
		Current.Sampled = ( usage & ImageUsageFlags.SampledBit ) != 0;

		var info = new SwapchainCreateInfoKHR
		{
			SType = StructureType.SwapchainCreateInfoKhr,
			Surface = Current.Surface,
			MinImageCount = imageCount,
			ImageFormat = format.Format,
			ImageColorSpace = format.ColorSpace,
			ImageExtent = extent,
			ImageArrayLayers = 1,

			//Drawn into and copied from for the backdrop; request only used flags so compression stays on
			ImageUsage = usage,

			//One queue, so no sharing to arrange.
			ImageSharingMode = SharingMode.Exclusive,
			PreTransform = ChooseTransform( capabilities ),
			CompositeAlpha = ChooseCompositeAlpha( capabilities.SupportedCompositeAlpha, Current.WantsAlpha ),
			PresentMode = ChoosePresentMode(),

			//Pixels hidden by another window need not be drawn correctly. They are never read back, and saying so is free performance.
			Clipped = true,
			OldSwapchain = previous,
		};

		//Made for NVIDIA's low-latency sleep where the device has it - see CoreLatency.
		info.PNext = CoreLatency.SwapchainChain( null );

		Current.PresentMode = info.PresentMode;

		SwapchainKHR made;
		var created = VkDevice.khrSwapchain.CreateSwapchain( device, &info, null, &made );

		if ( created == Result.Success ) CoreLatency.SwapchainMade( made );

		//Retired to the graveyard, not destroyed: in-flight frames may still use its images
		if ( previous.Handle != 0 )
		{
			VkDevice.RetireSwapchain( previous, FramesInFlight );
			Current.Handle = default;
		}

		if ( created != Result.Success )
		{
			VkDevice.SetText( "the swapchain would not be made" );
			return false;
		}

		Current.Handle = made;

		Current.Format = format.Format;
		Current.Extent = extent;

		uint count = 0;
		VkDevice.khrSwapchain.GetSwapchainImages( device, made, ref count, null );

		Current.Images = new VkImage[count];

		fixed ( VkImage* into = Current.Images )
		{
			VkDevice.khrSwapchain.GetSwapchainImages( device, made, ref count, into );
		}

		Current.Views = new ImageView[count];
		Current.RenderFinished = new VkSemaphore[count];

		for ( var i = 0u; i < count; ++i )
		{
			var viewInfo = new ImageViewCreateInfo
			{
				SType = StructureType.ImageViewCreateInfo,
				Image = Current.Images[i],
				ViewType = ImageViewType.Type2D,
				Format = Current.Format,
				SubresourceRange = new ImageSubresourceRange( ImageAspectFlags.ColorBit, 0, 1, 0, 1 ),
			};

			ImageView view;

			if ( vk.CreateImageView( device, &viewInfo, null, &view ) != Result.Success )
			{
				VkDevice.SetText( "a swapchain image view would not be made" );
				return false;
			}

			Current.Views[i] = view;

			var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };

			VkSemaphore semaphore;

			if ( vk.CreateSemaphore( device, &semaphoreInfo, null, &semaphore ) != Result.Success )
			{
				VkDevice.SetText( "a semaphore would not be made" );
				return false;
			}

			Current.RenderFinished[i] = semaphore;

			//One semaphore per image rather than per frame, which is right and is also the thing a reader of a present hazard has to keep straight.
			VkDevice.NameHandle( ObjectType.Image, Current.Images[i].Handle, "swapchain image", i );
			VkDevice.NameHandle( ObjectType.ImageView, view.Handle, "swapchain image view", i );
			VkDevice.NameHandle( ObjectType.Semaphore, semaphore.Handle, "render finished", i );
		}

		swapchainMadeNs = NowNs();

		//And the host copy of what is presented, at the window's size, where somebody asked to keep one - see FitWindowRead.
		FitWindowRead( Current, extent );
		FitCaptures( Current, extent );

		if ( !CreatePictures( Current.Pics, extent, Current.RenderScale, Current.WorldWanted ) ) return false;

		picturesMadeNs = NowNs();

		//And the backdrop: the same shape as the window, written by a copy and read by a
		//shader. A failure here is not fatal - the panels that wanted it simply draw without it.
		{
			var backdropLevels = BackdropLevelsFor( extent );

			var backdropInfo = new ImageCreateInfo
			{
				SType = StructureType.ImageCreateInfo,
				ImageType = ImageType.Type2D,
				Format = Current.Format,
				Extent = new Extent3D( extent.Width, extent.Height, 1 ),
				MipLevels = backdropLevels,
				ArrayLayers = 1,
				Samples = SampleCountFlags.Count1Bit,
				Tiling = ImageTiling.Optimal,
				Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit,
				InitialLayout = ImageLayout.Undefined,
			};

			VkImage image;

			if ( vk.CreateImage( device, &backdropInfo, null, &image ) == Result.Success )
			{
				Current.BackdropImage = image;

				MemoryRequirements requirements;
				vk.GetImageMemoryRequirements( device, image, &requirements );

				var allocate = new MemoryAllocateInfo
				{
					SType = StructureType.MemoryAllocateInfo,
					AllocationSize = requirements.Size,
					MemoryTypeIndex = VkDevice.FindMemoryType( requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit ),
				};

				DeviceMemory memory = default;

				if ( allocate.MemoryTypeIndex != uint.MaxValue &&
					vk.AllocateMemory( device, &allocate, null, &memory ) == Result.Success )
				{
					Current.BackdropMemory = memory;
					VkDevice.CountTarget( memory, requirements.Size );

					if ( vk.BindImageMemory( device, image, memory, 0 ) == Result.Success )
					{
						var backdropViewInfo = new ImageViewCreateInfo
						{
							SType = StructureType.ImageViewCreateInfo,
							Image = image,
							ViewType = ImageViewType.Type2D,
							Format = Current.Format,
							SubresourceRange = new ImageSubresourceRange( ImageAspectFlags.ColorBit, 0, backdropLevels, 0, 1 ),
						};

						ImageView view;

						if ( vk.CreateImageView( device, &backdropViewInfo, null, &view ) == Result.Success )
						{
							Current.BackdropView = view;
							Current.BackdropLevels = backdropLevels;
							Current.BackdropHandle = VkTextures.Adopt( view );
						}
					}
				}
			}
		}

		Current.OutOfDate = false;
		Current.Suboptimal = false;

		DescribeCurrent();

		return true;
	}

	//Describes the device and swapchain; called on every rebuild since mode and count can change
	private static void DescribeCurrent()
	{
		//The present mode and the image count together, because neither alone says how the frame is paced: plain FIFO with two images
		//parks the processor in the acquire at the top of every frame, which reads from outside as a slow engine that loads nothing.
		var modeText = Current.PresentMode switch
		{
			PresentModeKHR.MailboxKhr => "mailbox",
			PresentModeKHR.ImmediateKhr => "immediate",
			PresentModeKHR.FifoRelaxedKhr => "fifo relaxed",
			_ => "fifo",
		};

		var version = (Silk.NET.Core.Version32)VkDevice.apiVersion;

		VkDevice.SetText( $"{VkDevice.deviceNameText}, vulkan {version.Major}.{version.Minor}.{version.Patch}" +
			$"{( VkDevice.validation ? " (validation on)" : "" )}, " +
			$"{Current.Extent.Width}x{Current.Extent.Height}, {Current.Images.Length} images, {modeText}" );
	}

	//Recreates a lost surface and everything on it; the old surface can't even be queried
	internal static bool RebuildTheSurface()
	{
		VkSubmitter.Drain();
		return RebuildSurface();
	}

	private static bool RebuildSurface()
	{
		if ( Current.SurfaceKind == 0 && Current.SurfaceWindow == 0 ) return false;

		VkSubmitter.DeviceWaitIdle();
		VkDevice.AllTicketsDone();

		//The images first: they belong to a swapchain that belongs to the surface, and the surface may not be destroyed while it holds them.
		DestroyImages();

		if ( Current.Surface.Handle != 0 )
		{
			VkDevice.khrSurface.DestroySurface( VkDevice.instance, Current.Surface, null );
			Current.Surface = default;
		}

		if ( !CreateSurface( Current.SurfaceKind, Current.SurfaceDisplay, Current.SurfaceWindow ) ||
			Current.Surface.Handle == 0 )
		{
			VkDevice.SetText( "the window would not become a surface again" );
			return false;
		}

		//The same question the surface was asked when it was first made. A new surface is a new answer,
		//and on a laptop that has just handed the panel to the other GPU the answer may have changed.
		Silk.NET.Core.Bool32 supported = false;
		VkDevice.khrSurface.GetPhysicalDeviceSurfaceSupport( VkDevice.physicalDevice, VkDevice.graphicsFamily,
			Current.Surface, &supported );

		if ( !supported )
		{
			VkDevice.SetText( "the drawing queue cannot present to this window any more" );
			return false;
		}

		if ( !CreateImages( Current.RequestedWidth, Current.RequestedHeight ) )
		{
			DestroyImages();
			return false;
		}

		return true;
	}

	//The window has changed shape under us. Whether that is acted on now or waited out is the coalesce window's business.
	internal static void MarkSuboptimal() => MarkSuboptimal( Current );

	//The same, for a swapchain that is not the current one - a present the submit thread made.
	internal static void MarkSuboptimal( Swapchain swapchain )
	{
		if ( coalesceNs == 0 )
		{
			swapchain.OutOfDate = true;
			return;
		}

		if ( swapchain.Suboptimal ) return;

		swapchain.Suboptimal = true;
		swapchain.SuboptimalSince = NowNs();
	}

	//Whether a suboptimal swapchain has stood still long enough to rebuild.
	internal static bool SuboptimalSettled()
		=> Current.Suboptimal && NowNs() - Current.SuboptimalSince >= coalesceNs;

	//Rebuild the images at whatever size the window is now.
	internal static bool Rebuild()
	{
		//The images and the swapchain are about to be replaced, so none of them may be in the submit thread's hands - see VkSubmitter.
		VkSubmitter.Drain();

		//In-flight resources go to the graveyard instead of stopping the device; old swapchain handed over
		var started = NowNs();

		DestroyImages( true, true );

		var destroyed = NowNs();

		if ( !CreateImages( Current.RequestedWidth, Current.RequestedHeight ) )
		{
			DestroyImages();
			return false;
		}

		if ( timingRebuilds )
		{
			backdropMadeNs = NowNs();

			VkDevice.Say( $"swapchain {Current.RequestedWidth}x{Current.RequestedHeight} rebuilt: " +
				$"destroy {( destroyed - started ) / 1.0e6:F2}, swapchain {( swapchainMadeNs - destroyed ) / 1.0e6:F2}, " +
				$"pictures {( picturesMadeNs - swapchainMadeNs ) / 1.0e6:F2}, backdrop {( backdropMadeNs - picturesMadeNs ) / 1.0e6:F2} ms" );
		}

		return true;
	}

	//Tears down whichever one is current.
	private static void DestroyCurrent()
	{
		if ( VkDevice.device.Handle == 0 ) return;

		//Anything still running is using the images.
		VkSubmitter.DeviceWaitIdle();
		VkDevice.AllTicketsDone();

		DestroyImages();
		VkFrame.DestroyFrames( Current );
		DropCaptures( Current );

		//Forget does not touch it, so a window closed while somebody kept its picture used to leave the buffer and its memory behind.
		DropWindowRead( Current );

		if ( Current.Surface.Handle != 0 )
		{
			VkDevice.khrSurface.DestroySurface( VkDevice.instance, Current.Surface, null );
			Current.Surface = default;
		}

		Forget( Current );
	}

	//Everything a swapchain held, back to nothing.
	private static void Forget( Swapchain one )
	{
		one.Surface = default;
		one.SurfaceKind = 0;
		one.SurfaceDisplay = 0;
		one.SurfaceWindow = 0;
		one.Pics.Forget();
		one.RequestedWidth = 0;
		one.RequestedHeight = 0;
		one.Handle = default;
		one.Format = Format.Undefined;
		one.Extent = default;
		one.SupportedAlpha = 0;
		one.WantsAlpha = false;
		one.RenderScale = 1.0f;
		one.WorldWanted = default;
		one.WorldX = 0;
		one.WorldY = 0;
		one.LiftPending = false;
		one.WorldOnScreen = false;
		one.WorldShades = true;
		one.DepthOnScreen = false;
		one.WantsVsync = true;
		one.DisplayStalled = false;
		one.Images = [];
		one.Views = [];
		one.ViewSlots = [];
		one.Sampled = false;
		one.BackdropImage = default;
		one.BackdropMemory = default;
		one.BackdropView = default;
		one.BackdropHandle = 0;
		one.BackdropTaken = false;
		one.BackdropLevels = 0;
		one.RenderFinished = [];
		one.Frame = 0;
		one.ImageIndex = 0;
		one.PresentMode = PresentModeKHR.FifoKhr;
		one.Recording = false;
		one.MainPass = false;
		one.MainBegun = false;
		one.TargetPass = false;
		one.Target = 0;
		one.OutOfDate = false;
		one.Suboptimal = false;
		one.SuboptimalSince = 0;
		one.Presented = 0;

		for ( var i = 0; i < 4; ++i ) one.Clear[i] = 0.0f;
	}

	//Tears down every swapchain: the device is going, and every window that was drawing through it is going with it.
	internal static void DestroyEverySwapchain()
	{
		//Nothing of this swapchain may be in the submit thread's hands - see VkSubmitter.
		VkSubmitter.Drain();

		foreach ( var pair in swapchains )
		{
			active = pair.Value;
			DestroyCurrent();
		}

		active = placeholder;
		swapchains.Clear();

		DestroyEveryView();
	}

	//The swapchain's colour format, or Undefined without one.
	internal static Format SwapchainFormat()
	{
		//Whichever one is current, or any of them - a pipeline is baked between frames, when
		//none is, and every window on one device is offered the same surface formats anyway.
		if ( Current.Format != Format.Undefined ) return Current.Format;

		foreach ( var pair in swapchains )
		{
			if ( pair.Value.Format != Format.Undefined ) return pair.Value.Format;
		}

		return Format.Undefined;
	}

	//What a finished picture is in - a window's image or a view's own. The same for both on purpose: a pipeline
	//bakes against one format, and the pass that draws the screen is the same pass wherever the screen is.
	internal static Format ScreenFormat()
	{
		var swapchain = SwapchainFormat();
		if ( swapchain != Format.Undefined ) return swapchain;

		//No window has been attached, so nothing has told us what a screen looks like here. Eight bits
		//a channel with the curve, which is what every swapchain we are offered turns out to be anyway.
		return Format.R8G8B8A8Srgb;
	}

	//-- what the managed side calls ----------------------------------------

	internal static ulong Create( int kind, ulong display, ulong window, int width, int height, int transparent )
	{
		//Nothing of this swapchain may be in the submit thread's hands - see VkSubmitter.
		VkSubmitter.Drain();

		lock ( VkDevice.renderLock )
		{
			if ( !VkDevice.Ready )
			{
				VkDevice.SetText( "there is no device to make a swapchain on" );
				return 0;
			}

			//Built in place and made current, because everything below works on whichever one is - and it
			//is not in the table until it is whole, so a failure halfway leaves nothing behind to find.
			var made = new Swapchain();
			active = made;

			Current.RequestedWidth = width;
			Current.RequestedHeight = height;
			Current.WantsAlpha = transparent != 0;

			//Kept for the day the surface is lost and has to be made again from the same window.
			Current.SurfaceKind = kind;
			Current.SurfaceDisplay = display;
			Current.SurfaceWindow = window;

			if ( !CreateSurface( kind, display, window ) || Current.Surface.Handle == 0 )
			{
				//CreateSurface says why when it knows; this covers the driver refusing a handle that looked fine.
				if ( Current.Surface.Handle == 0 )
					VkDevice.SetText( "the window would not become a surface" );

				DestroyCurrent();
				active = placeholder;
				return 0;
			}

			//A queue family that can draw but not present is possible in the standard and unheard of on a
			//desktop. Check anyway: the alternative is presenting to a queue that silently does nothing.
			Silk.NET.Core.Bool32 supported = false;
			VkDevice.khrSurface.GetPhysicalDeviceSurfaceSupport( VkDevice.physicalDevice, VkDevice.graphicsFamily,
				Current.Surface, &supported );

			if ( !supported )
			{
				VkDevice.SetText( "the drawing queue cannot present to this window" );
				DestroyCurrent();
				active = placeholder;
				return 0;
			}

			if ( !VkFrame.CreateFrames( Current ) )
			{
				VkDevice.SetText( "the per-frame objects would not be made" );
				DestroyCurrent();
				active = placeholder;
				return 0;
			}

			if ( !CreateImages( width, height ) )
			{
				DestroyCurrent();
				active = placeholder;
				return 0;
			}

			//What it came out as is CreateImages' to say - see DescribeCurrent.

			var handle = nextHandle++;

			swapchains[handle] = made;
			active = placeholder;

			return handle;
		}
	}

	internal static int Resize( ulong handle, int width, int height )
	{

		lock ( VkDevice.renderLock )
		{
			var found = Find( handle );
			if ( found is null || found.Surface.Handle == 0 ) return 0;

			//Made current for the duration: rebuilding is all the same work as making one, and that work knows only about whichever is current.
			active = found;

			Current.RequestedWidth = width;
			Current.RequestedHeight = height;

			var result = Rebuild() ? 1 : 0;

			active = placeholder;
			return result;
		}
	}

	internal static int Vsync( ulong handle, int enabled )
	{

		lock ( VkDevice.renderLock )
		{
			var found = Find( handle );
			if ( found is null || found.Surface.Handle == 0 ) return 0;

			var wanted = enabled != 0;

			//Asking for what it already is costs nothing, which is what lets the caller ask every frame rather than remembering.
			if ( found.WantsVsync == wanted ) return 1;

			found.WantsVsync = wanted;

			//The mode is baked into the swapchain, so changing it is the same work as a resize - made current for the duration, same as one.
			active = found;

			var result = Rebuild() ? 1 : 0;

			//Traced only when the present mode changes, since that decides how frames are paced
			if ( result != 0 ) VkDevice.SayTrace( VkDevice.infoText );

			active = placeholder;
			return result;
		}
	}

	//World size for an area at a render scale: scaled, rounded, never zero, never past the window
	private static Extent2D Scaled( Extent2D area, float scale )
	{
		if ( !( scale > 0.0f ) || scale >= 1.0f ) return area;

		return new Extent2D(
			System.Math.Max( 1u, (uint)( area.Width * scale + 0.5f ) ),
			System.Math.Max( 1u, (uint)( area.Height * scale + 0.5f ) ) );
	}

	//The part of the screen the world is put down on, in pixels - all of it, or what the window said.
	internal static Extent2D WorldArea( Swapchain one, Pictures p )
		=> one.WorldWanted.Width > 0 ? WorldExtent( p.ScreenExtent, one.WorldWanted ) : p.ScreenExtent;

	//Whether the world's pictures are the size of the area they are put down on - nothing to carry up.
	private static bool OneToOne( Swapchain one, Pictures p )
	{
		var area = one.WorldWanted.Width > 0 ? WorldExtent( p.ScreenExtent, one.WorldWanted ) : p.ScreenExtent;
		return p.Extent.Width == area.Width && p.Extent.Height == area.Height;
	}

	private static Extent2D WorldExtent( Extent2D screen, Extent2D wanted )
	{
		var width = System.Math.Min( wanted.Width, screen.Width );
		var height = System.Math.Min( wanted.Height, screen.Height );

		return new Extent2D( System.Math.Max( 1u, width ), System.Math.Max( 1u, height ) );
	}

	//Sets where the world goes on screen; offset is free, a size change rebuilds world pictures
	internal static int WorldRect( ulong handle, int x, int y, int width, int height )
	{

		lock ( VkDevice.renderLock )
		{
			var found = Find( handle );
			if ( found is null || found.Surface.Handle == 0 ) return 0;

			found.WorldX = System.Math.Max( 0, x );
			found.WorldY = System.Math.Max( 0, y );

			var wanted = width > 0 && height > 0 ? new Extent2D( (uint)width, (uint)height ) : default;

			if ( found.WorldWanted.Width == wanted.Width && found.WorldWanted.Height == wanted.Height ) return 1;

			found.WorldWanted = wanted;

			//Only where the pictures would come out a different size. Asking for the whole window on
			//a swapchain already drawn at the whole window is the ordinary case and costs nothing.
			var target = Scaled( wanted.Width > 0 ? WorldExtent( found.Extent, wanted ) : found.Extent, found.RenderScale );

			if ( found.Pics.Extent.Width == target.Width && found.Pics.Extent.Height == target.Height ) return 1;

			active = found;

			var result = Rebuild() ? 1 : 0;

			active = placeholder;
			return result;
		}
	}

	internal static int Scale( ulong handle, float scale )
	{

		lock ( VkDevice.renderLock )
		{
			var found = Find( handle );
			if ( found is null || found.Surface.Handle == 0 ) return 0;

			//A quarter is as far down as this goes. Past that the world is fewer pixels than the interface drawn over it
			//by a factor that no amount of stretching hides, and the frame is no longer bound by the thing being shrunk.
			if ( scale > 1.0f ) scale = 1.0f;
			if ( scale < 0.25f ) scale = 0.25f;

			//The world's shape has to reach the screen's depth buffer, and on a device that cannot blit its depth format there is no way to get it there.
			//Refused rather than drawn without it: gizmos and world text sinking through walls is a stranger bug than a scale that would not change.
			if ( scale < 1.0f && !VkDevice.depthBlit )
			{
				VkDevice.SetText( "this device cannot blit depth, so the world cannot be drawn smaller than the window" );
				return 0;
			}

			//Asking for what it already is costs nothing. Compared loosely: the number
			//comes from a slider by way of a convar and will not arrive bit-identical twice.
			var difference = found.RenderScale > scale ? found.RenderScale - scale : scale - found.RenderScale;

			if ( difference < 0.001f ) return 1;

			found.RenderScale = scale;

			//Every picture the world is drawn through changes size, which is the same work as a resize - made current for the duration, same as one.
			active = found;

			var result = Rebuild() ? 1 : 0;

			active = placeholder;
			return result;
		}
	}

	internal static void Destroy( ulong handle )
	{
		//Nothing of this swapchain may be in the submit thread's hands - see VkSubmitter.
		VkSubmitter.Drain();

		lock ( VkDevice.renderLock )
		{
			var found = Find( handle );
			if ( found is null ) return;

			active = found;
			DestroyCurrent();
			active = placeholder;

			swapchains.Remove( handle );
		}
	}

	//Writes the swapchain's width, height, image count and alpha modes.
	internal static void Info( ulong handle, int* outInfo )
	{
		if ( outInfo is null ) return;

		lock ( VkDevice.renderLock )
		{
			var found = Find( handle );

			outInfo[0] = found is not null ? (int)found.Extent.Width : 0;
			outInfo[1] = found is not null ? (int)found.Extent.Height : 0;
			outInfo[2] = found is not null ? found.Images.Length : 0;

			//Which ways the desktop said it would composite this window, so that a window asking to be see-through can find out whether it is.
			outInfo[3] = found is not null ? (int)found.SupportedAlpha : 0;
		}
	}

	//The pictures the world is drawn through, which below a render scale of one are smaller than the window they end up on.
	internal static void WorldSize( int* outInfo )
	{
		if ( outInfo is null ) return;

		lock ( VkDevice.renderLock )
		{
			var pictures = Pics;

			outInfo[0] = (int)pictures.Extent.Width;
			outInfo[1] = (int)pictures.Extent.Height;
		}
	}

	//How many frames this window has actually shown. Makes - or lets go of - the host copy of what
	//is presented, for the size the window is now. Nothing at all unless somebody asked for it.
	internal static void FitWindowRead( Swapchain one, Extent2D extent )
	{
		if ( !Swapchain.WantsRead )
		{
			DropWindowRead( one );
			return;
		}

		if ( one.ReadBuffer.Handle != 0 &&
			one.ReadExtent.Width == extent.Width && one.ReadExtent.Height == extent.Height ) return;

		DropWindowRead( one );

		var vk = VkDevice.vk;
		var device = VkDevice.device;

		var info = new BufferCreateInfo
		{
			SType = StructureType.BufferCreateInfo,
			Size = (ulong)extent.Width * extent.Height * 4,
			Usage = BufferUsageFlags.TransferDstBit,
			SharingMode = SharingMode.Exclusive,
		};

		VkBuffer buffer;
		if ( vk.CreateBuffer( device, &info, null, &buffer ) != Result.Success ) return;

		one.ReadBuffer = buffer;

		MemoryRequirements need;
		vk.GetBufferMemoryRequirements( device, buffer, &need );

		var type = VkDevice.FindMemoryType( need.MemoryTypeBits,
			MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit );

		if ( type == uint.MaxValue ) { DropWindowRead( one ); return; }

		var allocate = new MemoryAllocateInfo
		{
			SType = StructureType.MemoryAllocateInfo,
			AllocationSize = need.Size,
			MemoryTypeIndex = type,
		};

		DeviceMemory memory;
		if ( vk.AllocateMemory( device, &allocate, null, &memory ) != Result.Success ) { DropWindowRead( one ); return; }

		one.ReadMemory = memory;

		if ( vk.BindBufferMemory( device, buffer, memory, 0 ) != Result.Success ) { DropWindowRead( one ); return; }

		void* mapped;
		if ( vk.MapMemory( device, memory, 0, info.Size, 0, &mapped ) != Result.Success ) { DropWindowRead( one ); return; }

		one.ReadMapped = (byte*)mapped;
		one.ReadExtent = extent;
	}

	//Frees the host copy of the presented image via the graveyard; in-flight frames may still use it
	internal static void DropWindowRead( Swapchain one )
	{
		var vk = VkDevice.vk;
		var device = VkDevice.device;

		if ( one.ReadMapped is not null && one.ReadMemory.Handle != 0 ) vk.UnmapMemory( device, one.ReadMemory );
		if ( one.ReadBuffer.Handle != 0 || one.ReadMemory.Handle != 0 ) VkDevice.RetireBuffer( one.ReadBuffer, one.ReadMemory );

		one.ReadMapped = null;
		one.ReadBuffer = default;
		one.ReadMemory = default;
		one.ReadExtent = default;
		one.ReadPending = false;
	}

	//What the window last presented, as RGBA, red first - see the read buffer on Swapchain. Nought where nobody asked for it to be kept.
	internal static int WindowRead( ulong handle, byte* outPixels, int capacity )
	{
		//Nothing of this swapchain may be in the submit thread's hands - see VkSubmitter.
		VkSubmitter.Drain();

		lock ( VkDevice.renderLock )
		{
			if ( !swapchains.TryGetValue( handle, out var one ) ) return 0;
			if ( outPixels is null || one.ReadMapped is null ) return 0;

			var bytes = (int)one.ReadExtent.Width * (int)one.ReadExtent.Height * 4;
			if ( capacity < bytes ) return 0;

			if ( VkDevice.device.Handle != 0 ) VkSubmitter.DeviceWaitIdle();

			one.ReadPending = false;

			System.Buffer.MemoryCopy( one.ReadMapped, outPixels, bytes, bytes );

			//Red first, whatever the surface wanted - everything on this side counts pixels as RGBA.
			if ( one.Format == Format.B8G8R8A8Srgb || one.Format == Format.B8G8R8A8Unorm )
			{
				for ( var at = 0; at < bytes; at += 4 )
				{
					var blue = outPixels[at];

					outPixels[at] = outPixels[at + 2];
					outPixels[at + 2] = blue;
				}
			}

			return bytes;
		}
	}

	//Creates or frees each window's host copy after WantsRead changes, since resizes may not happen
	internal static void FitWindowReads()
	{
		lock ( VkDevice.renderLock )
		{
			foreach ( var one in swapchains.Values ) FitWindowRead( one, one.Extent );
		}
	}

	//One frame's copy of what a window presented, for a recording - see Swapchain.Captures.
	internal sealed class WindowCapture
	{
		public VkBuffer Buffer;
		public DeviceMemory Memory;
		public byte* Mapped;
		public Extent2D Extent;

		/// <summary>Which present this is a copy of, counted per window.</summary>
		public long Stamp;

		/// <summary>Recorded and not yet taken.</summary>
		public bool Pending;
	}

	//Makes - or lets go of - a window's recording copies, for the size it is now. Nothing unless a recording asked for them.
	internal static void FitCaptures( Swapchain one, Extent2D extent )
	{
		if ( !Swapchain.WantsCapture || extent.Width == 0 || extent.Height == 0 )
		{
			DropCaptures( one );
			return;
		}

		if ( one.Captures is { } kept && kept[0].Extent.Width == extent.Width && kept[0].Extent.Height == extent.Height ) return;

		DropCaptures( one );

		var vk = VkDevice.vk;
		var device = VkDevice.device;
		var made = new WindowCapture[one.Frames.Length];

		for ( var i = 0; i < made.Length; i++ )
		{
			var capture = made[i] = new WindowCapture();

			var info = new BufferCreateInfo
			{
				SType = StructureType.BufferCreateInfo,
				Size = (ulong)extent.Width * extent.Height * 4,
				Usage = BufferUsageFlags.TransferDstBit,
				SharingMode = SharingMode.Exclusive,
			};

			VkBuffer buffer;
			if ( vk.CreateBuffer( device, &info, null, &buffer ) != Result.Success ) { one.Captures = made; DropCaptures( one ); return; }

			capture.Buffer = buffer;

			MemoryRequirements need;
			vk.GetBufferMemoryRequirements( device, buffer, &need );

			//Cached where the card offers it: this is read back a whole screen at a time by the processor, and uncached memory reads at a fraction of the speed.
			var type = VkDevice.FindMemoryType( need.MemoryTypeBits,
				MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.HostCachedBit );

			if ( type == uint.MaxValue )
				type = VkDevice.FindMemoryType( need.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit );

			if ( type == uint.MaxValue ) { one.Captures = made; DropCaptures( one ); return; }

			var allocate = new MemoryAllocateInfo
			{
				SType = StructureType.MemoryAllocateInfo,
				AllocationSize = need.Size,
				MemoryTypeIndex = type,
			};

			DeviceMemory memory;
			if ( vk.AllocateMemory( device, &allocate, null, &memory ) != Result.Success ) { one.Captures = made; DropCaptures( one ); return; }

			capture.Memory = memory;

			void* mapped;
			if ( vk.BindBufferMemory( device, buffer, memory, 0 ) != Result.Success ||
				vk.MapMemory( device, memory, 0, info.Size, 0, &mapped ) != Result.Success )
			{
				one.Captures = made;
				DropCaptures( one );
				return;
			}

			capture.Mapped = (byte*)mapped;
			capture.Extent = extent;
		}

		one.Captures = made;
	}

	internal static void DropCaptures( Swapchain one )
	{
		if ( one.Captures is not { } captures ) return;

		one.Captures = null;

		var vk = VkDevice.vk;
		var device = VkDevice.device;

		if ( device.Handle == 0 ) return;

		//A frame in flight may still be copying into one of these.
		VkSubmitter.DeviceWaitIdle();

		foreach ( var capture in captures )
		{
			if ( capture is null ) continue;

			if ( capture.Mapped is not null && capture.Memory.Handle != 0 ) vk.UnmapMemory( device, capture.Memory );
			if ( capture.Buffer.Handle != 0 ) vk.DestroyBuffer( device, capture.Buffer, null );
			if ( capture.Memory.Handle != 0 ) vk.FreeMemory( device, capture.Memory, null );
		}
	}

	//Every window's recording copies made or let go of, now that a recording started or stopped.
	internal static void FitAllCaptures()
	{
		lock ( VkDevice.renderLock )
		{
			foreach ( var one in swapchains.Values ) FitCaptures( one, one.Extent );
		}
	}

	//The newest copy of what a window presented that the card has finished and nobody has taken yet, into
	//outPixels. Never waits: nought where there is nothing new, and the stamp of the present otherwise.
	internal static long WindowCaptureTake( ulong handle, byte* outPixels, int capacity, int* outInfo )
	{
		//Nothing of this swapchain may be in the submit thread's hands - see VkSubmitter.
		VkSubmitter.Drain();

		lock ( VkDevice.renderLock )
		{
			if ( !swapchains.TryGetValue( handle, out var one ) || one.Captures is not { } captures || outPixels is null ) return 0;

			var vk = VkDevice.vk;
			var device = VkDevice.device;

			WindowCapture newest = null;

			for ( var i = 0; i < captures.Length && i < one.Frames.Length; i++ )
			{
				var capture = captures[i];
				if ( !capture.Pending || ( newest is not null && capture.Stamp <= newest.Stamp ) ) continue;

				var frame = one.Frames[i];

				//Not submitted is waited on and reset already, which is finished too.
				var done = !frame.Submitted || vk.GetFenceStatus( device, frame.Finished ) == Result.Success;
				if ( done ) newest = capture;
			}

			if ( newest is null ) return 0;

			var bytes = (int)newest.Extent.Width * (int)newest.Extent.Height * 4;
			if ( capacity < bytes ) return 0;

			System.Buffer.MemoryCopy( newest.Mapped, outPixels, bytes, bytes );

			//Anything older is older than what was just taken, and nobody wants it.
			foreach ( var capture in captures )
			{
				if ( capture.Pending && capture.Stamp <= newest.Stamp ) capture.Pending = false;
			}

			if ( outInfo is not null )
			{
				outInfo[0] = (int)newest.Extent.Width;
				outInfo[1] = (int)newest.Extent.Height;
				outInfo[2] = one.Format == Format.B8G8R8A8Srgb || one.Format == Format.B8G8R8A8Unorm ? 1 : 0;
				outInfo[3] = one.Format is Format.B8G8R8A8Srgb or Format.B8G8R8A8Unorm or Format.R8G8B8A8Srgb or Format.R8G8B8A8Unorm ? 1 : 0;
			}

			return newest.Stamp;
		}
	}

	//How big a window's recording copies are, nought where it has none.
	internal static (int Width, int Height) WindowCaptureSize( ulong handle )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !swapchains.TryGetValue( handle, out var one ) || one.Captures is not { } captures ) return (0, 0);

			return ((int)captures[0].Extent.Width, (int)captures[0].Extent.Height);
		}
	}

	//How big that copy is, so a caller can size its buffer.
	internal static (int Width, int Height) WindowReadSize( ulong handle )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !swapchains.TryGetValue( handle, out var one ) || one.ReadMapped is null ) return (0, 0);

			return ((int)one.ReadExtent.Width, (int)one.ReadExtent.Height);
		}
	}

	//Whether this window could have its depth pre-pass write the screen's own depth buffer. False where there is no such buffer - the world is already
	//the whole window and there is no copy to avoid - or where the world is drawn smaller than the place it goes, which needs a stretch and not this.
	internal static int DepthOnScreenPossible( ulong handle )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !swapchains.TryGetValue( handle, out var one ) ) return 0;

			var p = one.Pics;

			if ( p is null || p.ScreenDepthImage.Handle == 0 || p.ScreenDepthHandle == 0 ) return 0;
			if ( one.WorldWanted.Width == 0 || !OneToOne( one, p ) ) return 0;

			return one.WorldX >= 0 && one.WorldY >= 0 &&
				one.WorldX + p.Extent.Width <= p.ScreenExtent.Width &&
				one.WorldY + p.Extent.Height <= p.ScreenExtent.Height ? 1 : 0;
		}
	}

	//Asks for it, for this frame - see Swapchain.DepthOnScreen.
	internal static void DepthOnScreen( ulong handle, bool wanted )
	{
		lock ( VkDevice.renderLock )
		{
			if ( swapchains.TryGetValue( handle, out var one ) ) one.DepthOnScreen = wanted;
		}
	}

	//Where the depth is read from and how big the picture holding it is: the world's own corner
	//and size ordinarily, its place in the window where the pre-pass wrote the screen's depth.
	internal static (int X, int Y, int Wide, int Tall) DepthReadRect( ulong handle )
	{
		lock ( VkDevice.renderLock )
		{
			var p = PicturesFor( handle );
			if ( p is null ) return (0, 0, 0, 0);

			var onScreen = DepthGoesToScreen && ReferenceEquals( p, Current.Pics ) &&
				p.ScreenDepthHandle != 0;

			return onScreen
				? (Current.WorldX, Current.WorldY, (int)p.ScreenExtent.Width, (int)p.ScreenExtent.Height)
				: (0, 0, (int)p.Extent.Width, (int)p.Extent.Height);
		}
	}

	internal static ulong FrameCount( ulong handle )
	{
		lock ( VkDevice.renderLock )
		{
			var one = Find( handle );
			return one?.Presented ?? 0;
		}
	}

	//The measured last frame a handle names, whether it is a window's or a view's.
	internal static VkFrame.Profile ProfileFor( ulong handle )
	{
		var one = Find( handle );
		if ( one is not null ) return one.Profile;

		var view = FindView( handle );
		return view?.Profile;
	}

	//The picture behind one of a viewport's targets, by handle rather than by whichever is current: these are
	//asked for between frames as often as during one, and between frames there is no current viewport to ask.
	internal static ulong TargetTexture( ulong handle, int target, int which )
	{
		lock ( VkDevice.renderLock )
		{
			var p = PicturesFor( handle );
			if ( p is null ) return 0;

			if ( target == VkDevice.TargetScene )
			{
				if ( which == 1 ) return p.NormalHandle;
				if ( which == 2 ) return p.DepthHandle;

				return p.SceneHandle;
			}

			//Depth by name too; uses the screen depth when this frame's pre-pass drew there (DepthOnScreen)
			if ( target == VkDevice.TargetDepth )
			{
				return DepthGoesToScreen && ReferenceEquals( p, Current.Pics ) && p.ScreenDepthHandle != 0
					? p.ScreenDepthHandle
					: p.DepthHandle;
			}

			if ( target == VkDevice.TargetVelocity ) return p.VelocityHandle;
			if ( target == VkDevice.TargetPost ) return p.PostHandle;
			if ( target == VkDevice.TargetFinal ) return p.FinalHandle[0];
			if ( target == VkDevice.TargetFinalSecond ) return p.FinalHandle[1];
			if ( target == VkDevice.TargetDisplay ) return p.DisplayHandle[0];
			if ( target == VkDevice.TargetDisplaySecond ) return p.DisplayHandle[1];
			if ( target == VkDevice.TargetSceneColour ) return p.SceneHandle;

			return 0;
		}
	}
}
