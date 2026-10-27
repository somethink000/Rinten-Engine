using System;
using Rinten.UI;

namespace Rinten;

//A window that is nothing but panels.
internal sealed class CoreUiWindow : IDisposable
{
	private static readonly Diagnostics.Logger log = new( "Ui" );

	private CoreWindow window;
	private ulong swapchain;

	//Sheet glass: the interface is drawn offscreen and laid over the window by a layered overlay with real per-pixel alpha, while the window itself
	//never presents - so its client area stays the desktop's blur, which is the look an opaque swapchain paints over and can never give back.
	private bool sheet;

	//Whether windows are drawn as glass sheets read back each frame; off means every window is solid
	internal static bool Sheets { get; set; }

	//The offscreen view the interface is drawn into, in sheet mode.
	private ulong sheetView;

	//The overlay that carries the picture, in sheet mode.
	private CoreSheetOverlay overlay;

	//Whether this window asked the desktop to show through it.
	private bool wantsGlass;

	/// <summary>Whether it got it. False until there is a swapchain to ask.</summary>
	public bool Composited { get; private set; }

	/// <summary>Whether the desktop took the corners off it for us.</summary>
	public bool Rounded { get; private set; }

	/// <summary>Whether it cut its own corners instead, out of a surface with an alpha.</summary>
	public bool RoundsItself { get; private set; }

	//Whether it asked for an alpha at all - for the corners, or for glass.
	private bool wantsAlpha;

	//Whether the ground under the panels is this window's to paint.
	private bool ownsGround;

	//Whether the content paints its own shape, corners and all - a notice does.
	private bool shapedByContent;

	//How round a window's corners are where they are ours to cut.
	private const float CornerRadius = 10.0f;

	//What they were last set to, so they are only written when they move.
	private float corners = -1.0f;

	/// <summary>Shortest gap between two frames of this window, in seconds. Nought draws on every pass.</summary>
	public float FrameGap { get; set; }

	//When this window last drew, on the monotonic clock.
	private long framedAt;

	//See-through windows are off: blur only exists on Windows, so every window is opaque everywhere
	//Not a const, or the compiler flags the glass path as unreachable code, an error in this build
	internal static readonly bool GlassEnabled;
	private (int Width, int Height) attached;
	private readonly UiWindowInput input = new();

	/// <summary>What is in the window. Fill it with panels.</summary>
	public RootPanel Root { get; private set; }

	/// <summary>The sheet dialogs draw inside of, since a sheet stacked on a sheet has nothing to blur. Usually null</summary>
	public static CoreUiWindow Inside { get; set; }

	//Native handle of the window a panel is in, by walking to its root; zero if none of ours
	internal static IntPtr HandleOf( Panel panel )
	{
		if ( panel is null ) return IntPtr.Zero;

		var root = panel;

		while ( root.Parent is not null ) root = root.Parent;

		foreach ( var window in open )
		{
			if ( ReferenceEquals( window.Root, root ) )
				return window.window?.Handle ?? IntPtr.Zero;
		}

		return IntPtr.Zero;
	}

	//Sees every key any of our windows hears before focus routing, so editor shortcuts always work
	internal static Action<CoreWindow.Key> KeyHeard { get; set; }

	/// <summary>The colour behind the panels.</summary>
	public Color Background { get; set; } = Color.Black;

	//Panel the world is drawn under, and its draw; the world renders into this window's frame in paint order
	internal Rinten.UI.Panel WorldPanel
	{
		get => worldPanel;
		set
		{
			worldPanel = value;
			worldClaimedAt = EngineLoop.LoopFrames;
		}
	}

	internal System.Action<ulong, int, int> WorldDraw { get; set; }

	private Rinten.UI.Panel worldPanel;

	//The loop frame the world's panel was last said to be it. Whoever draws the world says so every frame it wants it drawn, and a claim not
	//renewed is let go of: a view that stopped drawing - its dock behind another tab - leaves no world over whatever is in its place now.
	private long worldClaimedAt = long.MinValue;

	//Whether the last frame had a world in it, so the one after can say it has none.
	private bool worldWas;

	//The world's size this frame, or nothing.
	private (int Width, int Height) worldSize;

	//Last world panel size asked for and when; acted on only once it holds still, as resizes remake targets
	private (int Width, int Height) worldAsked;
	private long worldAskedAt;

	//The size the world's pictures were last made at, which a size still moving is drawn at.
	private (int Width, int Height) worldPictures;

	//Whether a world may go on a layer of its own over its window, where the window system
	//has them. -nolayers keeps it in the window's own frame: the control half of measuring it.
	internal static readonly bool Layers = !Rinten.Utility.CommandLine.HasSwitch( "-nolayers" );

	//The world's own surface over this window, while it has one - see WaylandLayer.
	private WaylandLayer layer;
	private ulong layerSwapchain;

	//The layer's size in pixels, and where on the window it lies.
	private (int Width, int Height) layerSize;
	private (int X, int Y) layerAt;

	//Whether the layer moved or changed size, and waits for the window's next frame to show it so.
	private bool layerMoved;

	//Whether the window system would not give this window a layer; it is not asked again.
	private bool layersRefused;

	//Whether the window's own picture has to go out again whatever its panels say: it
	//is new, or a new size, or its last frame was refused, or the layer over it moved.
	private bool windowStale = true;

	//Window frames under a layer since the start: drawn, and left on the screen as they were.
	internal static long WindowFramesUnderLayer, WindowFramesLeft;

	//Whether a world held by this window goes on a layer of its own. Where a window's pixels are not its surface's
	//units - a scaled display - the layer would have to be placed in the one and drawn in the other, and it is not made.
	internal bool LayersWorld =>
		Layers && !layersRefused && !sheet && window is { WindowKind: CoreWindow.Kind.Wayland, NativeDisplay: not 0 }
		&& Root is { IsEditorUi: true } && window.Size == window.SizeInPixels;

	//The window whose panels a panel belongs to, or null for one in no window of ours.
	internal static CoreUiWindow Holding( Rinten.UI.Panel panel )
	{
		if ( panel is null ) return null;

		var top = panel;
		while ( top.Parent is { } up ) top = up;

		foreach ( var window in open )
		{
			if ( ReferenceEquals( window.Root, top ) ) return window;
		}

		return null;
	}

	//Tells the swapchain where the world goes this frame, from where the layout put its
	//panel. Before the frame begins, because a change of size remakes the world's pictures.
	private void PlaceWorld( int width, int height )
	{
		worldSize = default;

		var panel = WorldPanel;

		//Held by whoever said so this frame or the last: a claim nobody renews is let go of.
		var claimed = worldClaimedAt >= EngineLoop.LoopFrames - 1;

		if ( claimed && panel is { IsValid: true, IsVisible: true } && WorldDraw is not null && panel.WearsWorld )
		{
			var box = panel.Box.Rect;

			var left = System.Math.Clamp( (int)System.MathF.Round( box.Left ), 0, width );
			var top = System.Math.Clamp( (int)System.MathF.Round( box.Top ), 0, height );
			var right = System.Math.Clamp( (int)System.MathF.Round( box.Right ), 0, width );
			var bottom = System.Math.Clamp( (int)System.MathF.Round( box.Bottom ), 0, height );

			if ( right - left >= 8 && bottom - top >= 8 )
			{
				(int Width, int Height) wanted = (right - left, bottom - top);
				var now = System.Diagnostics.Stopwatch.GetTimestamp();

				if ( wanted != worldAsked )
				{
					worldAsked = wanted;
					worldAskedAt = now;
				}

				var layered = LayersWorld;

				//A size still moving is drawn at the size the pictures already are, at the new place; the size follows once it has settled. On a layer only where the
				//compositor stretches it over its panel meanwhile: a layer held at its old size would otherwise stand over the panels beside it, or short of its edge.
				var holds = worldPictures.Width > 0 && ( !layered || layer is { Stretches: true } );

				var settled = !holds ||
					( now - worldAskedAt ) >= HoldSeconds * System.Diagnostics.Stopwatch.Frequency;

				var size = settled ? wanted : worldPictures;

				if ( layered && PlaceLayer( left, top, size, wanted ) )
				{
					//The window's own frame has no world in it while the layer has.
					if ( worldWas )
					{
						worldWas = false;
						CoreRenderDevice.SetWorldRect( swapchain, 0, 0, 0, 0 );
					}

					worldSize = worldPictures = size;
					return;
				}

				DropLayer();

				if ( CoreRenderDevice.SetWorldRect( swapchain, left, top, size.Width, size.Height ) )
				{
					worldSize = worldPictures = size;
					worldWas = true;
					return;
				}
			}
		}

		DropLayer();
		worldPictures = default;

		if ( !worldWas ) return;

		worldWas = false;
		CoreRenderDevice.SetWorldRect( swapchain, 0, 0, 0, 0 );
	}

	//The world into this frame, where the interface pass reached its panel.
	private void DrawWorld( Rect area )
	{
		if ( worldSize.Width <= 0 || WorldDraw is null ) return;

		using var scope = Diagnostics.Performance.Scope( "Scene View" );

		CoreRenderDevice.ZoneBegin( "scene" );
		CoreRenderDevice.InterruptScreen();

		try
		{
			WorldDraw( layer is not null ? layerSwapchain : swapchain, worldSize.Width, worldSize.Height );
		}
		finally
		{
			//What follows is the interface again, through the whole window.
			CoreRenderDevice.ResumeScreen();
		}

		CoreRenderDevice.ZoneEnd();
	}

	//Puts the world's layer over its panel: made the first time it is wanted, a new size once that has settled - shown stretched over the panel
	//meanwhile, where the compositor can - and moved wherever the panel went. False if this window cannot have one, which it is then not asked for again.
	private bool PlaceLayer( int left, int top, (int Width, int Height) size, (int Width, int Height) shown )
	{
		//Layer changes commit with the next surface commit, so wait until the last present is handed over
		CoreRenderDevice.FramesHandedOver();

		if ( layer is null )
		{
			layer = WaylandLayer.Create( (nint)window.NativeDisplay, (nint)window.NativeWindow );

			if ( layer is not null )
			{
				layerSwapchain = CoreRenderDevice.AttachSurface( (int)window.WindowKind, window.NativeDisplay,
					(ulong)layer.Surface, size.Width, size.Height );
			}

			if ( layerSwapchain == 0 )
			{
				layer?.Dispose();
				layer = null;
				layersRefused = true;

				log.Info( $"window '{named}': no layer for the world - it is drawn into the window's own frame" );
				return false;
			}

			if ( vsync is { } wanted ) CoreRenderDevice.SetVSync( layerSwapchain, wanted );

			layerSize = size;
			layerAt = (int.MinValue, int.MinValue);

			//Trace: it is placed again every time play starts or stops.
			log.Trace( $"window '{named}': the world on a layer of its own, {size.Width}x{size.Height}" );
		}
		else if ( layerSize != size )
		{
			CoreRenderDevice.Resize( layerSwapchain, size.Width, size.Height );

			layerSize = size;
			layerMoved = true;
		}

		layer.Drain();

		if ( layerAt != (left, top) )
		{
			layerAt = (left, top);
			layerMoved = true;
		}

		if ( layer.Show( shown.Width, shown.Height ) ) layerMoved = true;

		if ( layerMoved )
		{
			//Together with the window's next frame, which carries the place and the size: the layer's frames wait for it until it has gone out. See LayeredFrame.
			layer.Synchronise( true );
			layer.Place( left, top );
			windowStale = true;
		}

		CoreRenderDevice.SetWorldRect( layerSwapchain, 0, 0, size.Width, size.Height );
		return true;
	}

	//Takes the world's layer away, swapchain first; the window's own frame shows what is under it again.
	private void DropLayer()
	{
		if ( layer is null ) return;

		if ( layerSwapchain != 0 )
		{
			CoreRenderDevice.DetachWindow( layerSwapchain );
			layerSwapchain = 0;
		}

		layer.Dispose();
		layer = null;

		layerSize = default;
		layerMoved = false;
		windowStale = true;
	}

	//Forces every window to redraw next frame, e.g. when a recording starts on a still interface
	internal static void RepaintAll()
	{
		foreach ( var one in open )
		{
			if ( one is not null ) one.windowStale = true;
		}
	}

	//The world's layer over whichever window has one, and where on it it lies - nothing without one. For whoever reads
	//what the window system shows: the world is not in the window's own picture while it is on a layer. See MainSwapchain.
	internal static (ulong Swapchain, int X, int Y) MainLayer
	{
		get
		{
			foreach ( var one in open )
			{
				if ( one is not null && one.layerSwapchain != 0 ) return (one.layerSwapchain, one.layerAt.X, one.layerAt.Y);
			}

			return default;
		}
	}

	//Layered frame: the world layer every frame, the window picture only when it changed. False if refused
	private bool LayeredFrame( int width, int height )
	{
		var root = Root;

		using ( Diagnostics.Performance.Scope( "ui acquire" ) )
		{
			if ( !CoreRenderDevice.BeginFrame( layerSwapchain, Background ) )
			{
				Refused( CoreRenderDevice.Description );
				return false;
			}
		}

		CoreRenderDevice.ZoneBegin( "ui draw" );

		//Walked, or kept, inside the layer's frame: the walk uploads what it names, and the window's frame below is recorded after it on the same queue.
		var built = CoreSceneDrawer.BuildEditorUi( root, keepable: true );

		CoreSceneDrawer.DrawEditorUiOverWorld( root, new Vector2( layerAt.X, layerAt.Y ),
			layerSize.Width, layerSize.Height, DrawWorld );

		CoreRenderDevice.ZoneEnd();

		using ( Diagnostics.Performance.Scope( "ui submit" ) )
		{
			CoreRenderDevice.EndFrame();
		}

		if ( built == CoreSceneDrawer.UiBuilt.KeptSame && !windowStale )
		{
			WindowFramesLeft++;
			return true;
		}

		using ( Diagnostics.Performance.Scope( "ui acquire" ) )
		{
			if ( !CoreRenderDevice.BeginFrame( swapchain, Background ) )
			{
				//Tried again next frame, whatever the panels say then.
				windowStale = true;
				Refused( CoreRenderDevice.Description );
				return false;
			}
		}

		CoreRenderDevice.ZoneBegin( "ui draw" );
		CoreSceneDrawer.DrawEditorUi( root, width, height );
		CoreRenderDevice.ZoneEnd();

		using ( Diagnostics.Performance.Scope( "ui submit" ) )
		{
			CoreRenderDevice.EndFrame();
		}

		WindowFramesUnderLayer++;
		windowStale = false;

		//That frame took the layer's place and size to the screen with it; from here the two go at their own pace again.
		if ( layerMoved )
		{
			//After the present that carried the place and the size, not beside it.
			CoreRenderDevice.FramesHandedOver();

			layer.Synchronise( false );
			layerMoved = false;
		}

		return true;
	}

	/// <summary>Whether the window's close box has been pressed.</summary>
	public bool CloseRequested => window?.CloseRequested ?? true;

	/// <summary>Asks the window to close, as its close box would.</summary>
	public void RequestClose() => window?.RequestClose();

	//Asked before our caption's close button closes the window; false keeps it open
	internal Func<bool> AskClose { get; set; }

	/// <summary>How big the inside of the window is, in pixels.</summary>
	public (int Width, int Height) Size => window?.SizeInPixels ?? (0, 0);

	/// <summary>Area the window system may drag the window by: the caption bar minus its buttons, set by its drawer</summary>
	public (int X, int Y, int Width, int Height) DragStrip
	{
		get => window?.DragStrip ?? default;
		set { if ( window is not null ) window.DragStrip = value; }
	}

	/// <summary>Every such strip, for a bar with more than one gap in it.</summary>
	public (int X, int Y, int Width, int Height)[] DragStrips
	{
		get => window?.DragStrips ?? Array.Empty<(int, int, int, int)>();
		set { if ( window is not null ) window.DragStrips = value; }
	}

	/// <summary>Puts the window system's frame back on a window that was opened without one. For when the caption we meant to draw instead did not happen: a window with no frame and no caption cannot be moved or closed at all.</summary>
	public void GiveBackTheFrame() => window?.GiveBackTheFrame();

	//The bar along the top, where this window draws its own.
	private WindowCaption caption;

	/// <summary>Where the window's own interface goes: below the caption, or the root if there is not one.</summary>
	public Panel Content => body ?? Root;

	private Panel body;

	//How tall the caption is, or nothing if the window has none.
	internal int CaptionHeight => caption is not null ? WindowCaption.BarHeight : 0;

	private CoreUiWindow() { }

	//Opens one, or returns null and says why in the log. Every one of these that is open, in the order they opened.
	private static readonly List<CoreUiWindow> open = new();

	//Ticks every panel window once a frame (events, layout, draw) and disposes the ones asking to close
	private static bool framing;

	//Closes every UI window. Without telling anybody. This is the process going, not a window being closed: there is nothing left for an
	//interface to do about it, and every window running its own close on the way out is a great deal of work to arrive at the same dark screen.
	internal static void DisposeAll()
	{
		for ( var i = open.Count - 1; i >= 0; i-- )
		{
			try
			{
				open[i].Closed = null;
				open[i].Dispose();
			}
			catch { }
		}
	}

	//Destroys every UI window and the device before leaving the process.
	internal static void DropDisplay()
	{
		try { DisposeAll(); } catch { }
		try { CoreRenderDevice.Shutdown(); } catch { }
	}

	public static void FrameAll()
	{
		//The editor pumps its windows from inside blocking work, and some of that work happens during a frame. One at a time.
		if ( framing ) return;

		framing = true;

		try
		{
			FrameEach();
		}
		finally
		{
			framing = false;
		}
	}

	private static void FrameEach()
	{
		//Wind RealTime only when no game loop does; winding it per repaint shrinks the game's frame delta
		if ( EngineLoop.LoopFrames == 0 ) RealTime.Update( RealTime.NowDouble );

		PanelRealTime.Update();

		PretendIfAsked();

		//Once for the lot: the drain is global - every window's events come out of the one queue - and the pump
		//behind it is the expensive part. Each window used to drain for itself, which was N pumps for one queue.
		using ( Diagnostics.Performance.Scope( "Editor UI Events" ) )
		{
			CoreWindow.PollAll();
		}

		pumpedForPass = true;

		try
		{
			//Over a copy: a window's frame can close other windows - an editor tab closing takes its husk with it - and the list shrinks under
			//an index walked backwards, which went off its end when two went at once. A window gone from the list before its turn is skipped.
			var windows = open.ToArray();

			for ( var i = windows.Length - 1; i >= 0; i-- )
			{
				var window = windows[i];

				if ( !open.Contains( window ) ) continue;

				if ( window.Frame() ) continue;

				window.Dispose();
			}
		}
		finally
		{
			pumpedForPass = false;
		}

		//Run deferred panel deletion, which editors otherwise never do, so closed dialogs actually go away
		Engine.GlobalContext.Current?.UISystem?.RunDeferredDeletion();
	}

	//Whether the watch that draws during a drag is installed.
	private static bool watching;

	//Whether a frame is already being drawn from inside that watch.
	private static bool dragging;

	//Draws one window during a live resize from inside the OS callback; must not pump events or re-enter
	private static void DrawWhileDragged( CoreWindow which )
	{
		//The same events arrive during an ordinary frame as well, where the pass is already about to draw
		//this window - drawing it here as well is one frame's work thrown away for every event in the queue.
		if ( dragging || pumpedForPass ) return;

		dragging = true;
		pumpedForPass = true;

		try
		{
			for ( var i = open.Count - 1; i >= 0; i-- )
			{
				var window = open[i];

				if ( !ReferenceEquals( window.window, which ) ) continue;

				window.Frame();
				break;
			}
		}
		catch ( System.Exception e )
		{
			log.Warning( e, "drawing a window while it was being dragged" );
		}
		finally
		{
			pumpedForPass = false;
			dragging = false;
		}
	}

	//Whether FrameEach already drained the window system for this pass. A window framed on its
	//own - a click test, a window pumped from inside blocking work - still drains for itself.
	private static bool pumpedForPass;

	//Fakes a click in this window (-clicktest x,y), to test input routing without a real mouse
	internal void PretendClick( Vector2 at )
	{
		if ( window is null ) return;

		//The pointer arrives first: what is under it is decided by the frame that sees it there, not by the one that sees it pressed.
		window.PretendPointer( at, leftDown: false );
		Frame();

		log.Info( $"clicktest: {at} is over {Describe( input.Hovered )}" );

		window.PretendPointer( at, leftDown: true );
		Frame();

		window.PretendPointer( at, leftDown: false );
		Frame();

		//And a few more with nothing happening. A panel queues its events and runs them on the tick after the one that made them, so a click that
		//stops the moment the button comes up is a click nothing ever hears - which made this say the road was broken when it was only short.
		for ( var i = 0; i < 4; i++ ) Frame();

		log.Info( "clicktest: done" );
	}

	//The click a test asked for, once the window is up.
	private static void PretendIfAsked()
	{
		if ( !wantedClick.HasValue ) return;

		//Frames with a window in them. The editor spends the best part of a minute loading a project before it opens one, and every
		//one of those frames used to count - so the click was spent on an empty list long before there was anything to click on.
		if ( open.Count == 0 ) return;

		//A few frames in, so the window has laid itself out - but not so many that a window somebody closes first never gets clicked at all.
		if ( frames++ < 20 ) return;

		var at = wantedClick.Value;
		wantedClick = null;

		foreach ( var window in open.ToArray() ) window.PretendClick( at );
	}

	private static Vector2? wantedClick = ReadWantedClick();
	private static int frames;

	private static Vector2? ReadWantedClick()
	{
		var said = Rinten.Utility.CommandLine.GetSwitch( "-clicktest", null );

		if ( string.IsNullOrWhiteSpace( said ) ) return null;

		var parts = said.Split( ',' );

		if ( parts.Length != 2 ) return null;
		if ( !float.TryParse( parts[0], out var x ) || !float.TryParse( parts[1], out var y ) ) return null;

		return new Vector2( x, y );
	}

	/// <summary>How many are open. For a loop that ends when the last one does.</summary>
	public static int OpenCount => open.Count;

	//The window the pointer is in, or nothing before it has been in one.
	internal static CoreUiWindow Pointed
	{
		get
		{
			if ( CoreWindow.Pointed is not { } pointed ) return null;

			foreach ( var candidate in open )
			{
				if ( ReferenceEquals( candidate.window, pointed ) ) return candidate;
			}

			return null;
		}
	}

	//The one of these the keyboard is in, or null where it is in none of them. What tells an editor's windows apart when a key is pressed. A shortcut
	//is the window's rather than the focused panel's - see KeyHeard - so without this every window's shortcuts are every other window's too.
	internal static CoreUiWindow Focused
	{
		get
		{
			if ( CoreWindow.Keyboard is not { } keyboard ) return null;

			foreach ( var candidate in open )
			{
				if ( ReferenceEquals( candidate.window, keyboard ) ) return candidate;
			}

			return null;
		}
	}

	/// <summary>Whether pulling the caption carries this window as a drag another of ours can take - see CoreWindow.Carry.</summary>
	internal bool CarriesItself
	{
		set
		{
			if ( window is not null ) window.CarriesItself = value;
		}
	}

	//The open window around a native one, if it is one of these
	internal static CoreUiWindow Of( CoreWindow native )
	{
		if ( native is null ) return null;

		foreach ( var candidate in open )
		{
			if ( ReferenceEquals( candidate.window, native ) ) return candidate;
		}

		return null;
	}

	//Pointer in the pixels of the window it is in, the same value panels are hit-tested against
	internal static Vector2 Pointer
	{
		get
		{
			if ( Pointed is { } window ) return window.LastPointer;

			//Before the pointer has been in any window of ours there is nothing to be relative
			//to. The first window is the better guess than zero: it is the one being worked in.
			return open.Count > 0 ? open[0].LastPointer : default;
		}
	}

	//Where this window last saw the pointer, in its own pixels.
	internal Vector2 LastPointer
	{
		get
		{
			if ( window is null ) return default;

			var (x, y, _) = window.LastPointer;

			return new Vector2( x, y );
		}
	}

	//Puts the pointer somewhere in the window it is already in. The number came out of a widget, so the window that widget is in is the window
	//it means. Warping inside that window rather than working out where it lands on the desktop keeps the way back the same as the way there.
	internal static void PutPointer( Vector2 inWindow )
	{
		Pointed?.window?.WarpPointer( inWindow );
	}

	/// <summary>Opens a window of the engine's own, with a panel tree in it.</summary>
	public static CoreUiWindow Open( string title, int width, int height, bool utility = false, bool glass = false, bool ownCaption = false, bool captionByContent = false )
	{
		//Still frameless; only the bar is somebody else's to draw.
		if ( captionByContent ) ownCaption = true;

		if ( !CoreNative.Available )
		{
			log.Warning( "no window: the core is not loaded" );
			return null;
		}

		//Decided before creation: a sheet must be made without the desktop's cached client-area picture
		var asSheet = Sheets && glass && OperatingSystem.IsWindows();

		//Own-caption windows are borderless and made hidden until the first frame is drawn (ShowOnceDrawn)
		//Alpha must be requested at creation, or Wayland treats the window as opaque and corners turn black
		var wantsShowingThrough = !asSheet && !OperatingSystem.IsWindows();

		var window = CoreWindow.Create( title, width, height, utility: utility, noRedirection: asSheet,
			borderless: ownCaption && !utility, deferShow: true, transparent: wantsShowingThrough );

		if ( window is null )
		{
			log.Warning( $"no window: {CoreWindow.LastError}" );
			return null;
		}

		//Once, with the first window: from here on the window system tells us when it is dragging one, and a drag is drawn rather than smeared.
		if ( CoreWindow.LiveResize && !watching )
		{
			watching = true;

			CoreWindow.Resizing += DrawWhileDragged;
			CoreWindow.WatchForLiveResize();
		}

		if ( !CoreRenderDevice.Initialize() )
		{
			log.Warning( $"no window: {CoreRenderDevice.Description}" );
			window.Dispose();
			return null;
		}

		//Typed text is asked for only while a field has the focus - see UiWindowInput.Tick.
		//A window that always asks is one whose WASD may never arrive as keys on Wayland.

		//This window's events are an interface's, never a game's. Said here, once, because the shared drain in
		//FrameEach routes by this flag - it used to be re-said on every per-window poll, which no longer happens.
		window.RoutesToGame = false;

		//Panels keep their focus, their hover and their input queue in one of these. A game
		//makes one when it starts; a process that is only an editor never had a reason to.
		Engine.GlobalContext.Current.UISystem ??= new UISystem();

		//Vulkan surfaces on Windows have no alpha, so see-through windows there use a sheet instead

		var made = new CoreUiWindow
		{
			window = window,
			Root = new UiWindowRoot(),
			sheet = asSheet,
			named = title,

			//A notice always wants it; anything else has to ask. Whether it can have it is the desktop's answer - see Composited.
			wantsGlass = GlassEnabled && ( utility || ( glass && !asSheet ) ),

			//The startup bar draws a rounded plate over the whole of itself, so there is nothing for this window to lay down under it.
			shapedByContent = utility,
		};

		if ( asSheet )
		{
			made.overlay = new CoreSheetOverlay( (IntPtr)window.NativeWindow );
			made.Composited = made.overlay.IsValid;

			//The blur behind the window, without the whole-window thinning - the see-through is the overlay's, per pixel.
			window.MakeSheet( 0.0f );
		}

		//Round the corners wherever the desktop supports it; it squares them again while maximised
		made.Rounded = window.RoundCorners();

		//Ask for alpha wherever the desktop won't round corners for us, so we can cut them ourselves
		made.wantsAlpha = made.wantsGlass || ( !made.Rounded && !asSheet );

		open.Add( made );

		if ( captionByContent ) made.BareCaption();
		else if ( ownCaption ) made.DressCaption( title );

		return made;
	}

	//A window with no bar of ours: the ground and the column are still the window's, the caption is its content's.
	private void BareCaption()
	{
		Root.Style.FlexDirection = FlexDirection.Column;
		Root.Style.Width = Length.Percent( 100 );
		Root.Style.Height = Length.Percent( 100 );

		EditorTheme.Apply( Root );
		Root.AddClass( "captioned" );

		ownsGround = true;

		if ( !Composited ) Background = WindowCaption.Ground;
	}

	//Puts our own bar along the top, and a place under it for everything else.
	private void DressCaption( string heading )
	{
		Root.Style.FlexDirection = FlexDirection.Column;
		Root.Style.Width = Length.Percent( 100 );
		Root.Style.Height = Length.Percent( 100 );

		caption = WindowCaption.Attach( this, Root, heading );

		ownsGround = true;

		if ( !Composited ) Background = WindowCaption.Ground;

		body = Root.AddChild<Panel>( "window-body" );
		body.Style.FlexGrow = 1;
		body.Style.FlexShrink = 1;
		body.Style.Width = Length.Percent( 100 );
		body.Style.MinHeight = 0;
		body.Style.Position = PositionMode.Relative;
	}

	//Rounds the corners ourselves where the desktop won't: clear to nothing, draw the ground with a radius
	private void TakeCorners()
	{
		if ( Rounded || RoundsItself ) return;
		if ( !Composited || Root is null ) return;
		if ( !ownsGround && !shapedByContent ) return;

		RoundsItself = true;

		//Nothing in the clear: what lies outside the shape has to be the desktop, and it can only be that if this window puts nothing there.
		Background = Color.Transparent;

		//One that brought its own shape is left to it.
		if ( !ownsGround ) return;

		Root.Style.BackgroundColor = WindowCaption.Ground;
		Root.Style.OverflowX = OverflowMode.Hidden;
		Root.Style.OverflowY = OverflowMode.Hidden;

		SetCorners( CornerRadius );
	}

	private void SetCorners( float radius )
	{
		if ( corners == radius || Root is null ) return;

		corners = radius;

		Root.Style.BorderTopLeftRadius = radius;
		Root.Style.BorderTopRightRadius = radius;
		Root.Style.BorderBottomLeftRadius = radius;
		Root.Style.BorderBottomRightRadius = radius;
	}

	//Everything a tick of a window is, in the order it has to happen: what the window system has to say, then where the panels end up, then the drawing. False once there is no window left to draw into.
	/// <summary>Lays the panels out, draws them, and puts the picture on the screen.</summary>
	public bool Frame()
	{
		if ( window is null ) return false;

		//The window's own events only: this is an interface, not a game. Framed as part of the pass, the queue was already drained once for everybody.
		if ( !pumpedForPass )
		{
			using ( Diagnostics.Performance.Scope( "Editor UI Events" ) )
			{
				window.PollEvents( toGame: false );
			}
		}

		if ( window.CloseRequested ) return false;

		//Waiting for a frame to be shown with, but not forever.
		if ( wantsShowing && ++framesWaitedToShow > FramesToWaitToShow )
			ShowOnceDrawn();

		var (width, height) = window.SizeInPixels;

		if ( width <= 0 || height <= 0 ) return true;

		//A change of shape is told now, before the layout below, so a window resized this frame is laid out to its new shape this frame.
		//The widgets that follow the window used to ask it once a frame instead - every one of them, every frame, for the same answer.
		if ( window.TakeShapeChanged() ) Resized?.Invoke();

		//A window pumped from inside blocking work is asked to draw far more often than a screen can show
		//it. Its own clock, not the engine's: those are wound by whoever draws, and this decides whether to.
		if ( FrameGap > 0.0f )
		{
			var now = System.Diagnostics.Stopwatch.GetTimestamp();

			if ( framedAt != 0
				&& ( now - framedAt ) < FrameGap * System.Diagnostics.Stopwatch.Frequency )
				return true;

			framedAt = now;
		}

		//Named, because it was not: a window being dragged resizes its swapchain every frame and the cost of that sat
		//outside every zone in the tree - a measured frame of 60 ms had 27 ms of zones in it and no account of the rest.
		bool attached;

		using ( Diagnostics.Performance.Scope( "ui attach" ) )
		{
			attached = Attach( width, height );
		}

		if ( !attached ) return false;

		//A window filling the screen has no corners to round, and the desktop's own rounding takes itself off for exactly the same reason.
		if ( RoundsItself && ownsGround ) SetCorners( window.IsMaximized ? 0.0f : CornerRadius );

		//Lay out at the window's real size, not the swapchain's, unless FreezeOnResize holds it during a drag
		if ( FreezeOnResize ) (width, height) = this.attached;

		//The pointer, then the layout, then the drawing: a panel that grew under the cursor this tick is hovered this tick, not next.
		input.Read( window );

		//The three stages, named for the profiler as well as timed for the counters here - an
		//editor frame that is slow should say where in the same tree as everything else that is.
		var started = System.Diagnostics.Stopwatch.GetTimestamp();

		using ( Diagnostics.Performance.Scope( "Editor UI Layout" ) )
		{
			Layout( width, height );
		}

		var laidOut = System.Diagnostics.Stopwatch.GetTimestamp();

		using ( Diagnostics.Performance.Scope( "Editor UI Input" ) )
		{
			input.Tick( Root, window );

			//Input queues click handlers; run them before draw so focus does not flash the caret at its old index.
			if ( Root is not null && Root.IsValid )
				Root.RunPendingEventsDeep();
		}

		var pointed = System.Diagnostics.Stopwatch.GetTimestamp();

		//Where the world goes, now that the layout has said where its panel is - and before the frame, which is the only time its pictures may be remade.
		PlaceWorld( width, height );

		FramesAttempted++;

		//Draw carries the present in it, so under vsync this is also where the wait for the display shows up.
		using ( Diagnostics.Performance.Scope( "Editor UI Draw" ) )
		{
			if ( sheet )
			{
				if ( !SheetFrame( width, height ) )
				{
					Refused( "the sheet would not draw" );
					return true;
				}
			}
			else if ( layer is not null )
			{
				if ( !LayeredFrame( width, height ) ) return true;
			}
			else
			{
				//Named separately because they fail differently and are fixed differently: acquiring is waiting on the display, drawing is this process's
				//own work, and submitting is where a frame goes to wait for the last one. One zone over all three said only that the window was slow.
				using ( Diagnostics.Performance.Scope( "ui acquire" ) )
				{
					if ( !CoreRenderDevice.BeginFrame( swapchain, Background ) )
					{
						Refused( CoreRenderDevice.Description );
						return true;
					}
				}

				CoreRenderDevice.ZoneBegin( "ui draw" );
				CoreSceneDrawer.DrawUi( Root, width, height, worldSize.Width > 0 ? DrawWorld : null );
				CoreRenderDevice.ZoneEnd();

				using ( Diagnostics.Performance.Scope( "ui submit" ) )
				{
					CoreRenderDevice.EndFrame();
				}
			}
		}

		Accepted();

		Measure( started, laidOut, pointed );
		Trace();

		FramesPresented++;
		FramesDrawn++;

		//There is a frame in it now, so it may go on the screen.
		ShowOnceDrawn();

		return true;
	}

	//Window frames drawn since the process started, across every window - the cheapest possible answer to "is the editor actually up yet".
	internal static long FramesPresented;

	//The swapchain of the window the world is drawn in, for whoever wants to read what was actually presented - see PerfReport's -perfwindowshot.
	//The one that carries the world, not merely the first: an editor has several windows and only one of them has a viewport in it.
	internal static ulong MainSwapchain
	{
		get
		{
			foreach ( var one in open )
			{
				if ( one is not null && one.swapchain != 0 && one.WorldPanel is not null ) return one.swapchain;
			}

			foreach ( var one in open )
			{
				if ( one is not null && one.swapchain != 0 ) return one.swapchain;
			}

			return 0;
		}
	}

	//How many were tried, and how many the swapchain turned away - a gap between tried and presented is frames lost to rebuilds.
	internal static long FramesAttempted;
	internal static long FramesRefused;

	//Consecutive refused frames, and whether it was logged; logged once per run, not every frame
	private long refusedRunning;
	private bool saidRefused;

	//How long a run has to be before it is worth a line - about a second of them.
	private const long RefusedRun = 60;

	//One frame the window system would not take, counted and said if it is a run.
	private void Refused( string why )
	{
		FramesRefused++;
		refusedRunning++;

		if ( saidRefused || refusedRunning < RefusedRun ) return;

		saidRefused = true;

		log.Warning( $"the window has not taken a frame for {refusedRunning} tries ({why}) - " +
			"the loop is running and the picture is standing still" );
	}

	//And a frame that was taken, which ends any run.
	private void Accepted()
	{
		if ( saidRefused )
			log.Info( $"the window is taking frames again, after {refusedRunning}" );

		refusedRunning = 0;
		saidRefused = false;
	}

	//Adds every open window's stats to a report, including GPU time, as ui_perf logs them
	internal static void Report( System.Text.StringBuilder into )
	{
		foreach ( var window in open )
		{
			var (width, height) = window.Size;

			if ( window.swapchain != 0 && CoreRenderDevice.ReadProfile( window.swapchain ) is { } gpu )
			{
				into.AppendLine( $"  {width}x{height}: gpu {gpu.GpuMs:F2} ms, cpu record {gpu.CpuMs:F2} ms, " +
					$"{gpu.Draws} draws, {gpu.Triangles:N0} tris" );

				//And where it went: a window's frame is its own pass list, and a total alone cannot
				//say whether the panels, the picture of the world or a backdrop copy is the cost.
				foreach ( var zone in gpu.Zones )
				{
					into.AppendLine( $"    {new string( ' ', zone.Depth * 2 )}{zone.Name,-26} gpu {zone.GpuMs,7:F3} ms   cpu {zone.CpuMs,7:F3} ms" );
				}
			}

			//And the world's layer over it, which is a swapchain of its own.
			if ( window.layerSwapchain != 0 && CoreRenderDevice.ReadProfile( window.layerSwapchain ) is { } over )
			{
				into.AppendLine( $"  layer {window.layerSize.Width}x{window.layerSize.Height} at {window.layerAt.X},{window.layerAt.Y}: " +
					$"gpu {over.GpuMs:F2} ms, cpu record {over.CpuMs:F2} ms, {over.Draws} draws, {over.Triangles:N0} tris" );

				foreach ( var zone in over.Zones )
				{
					into.AppendLine( $"    {new string( ' ', zone.Depth * 2 )}{zone.Name,-26} gpu {zone.GpuMs,7:F3} ms   cpu {zone.CpuMs,7:F3} ms" );
				}
			}

			if ( window.perfFrames == 0 )
			{
				into.AppendLine( $"  {width}x{height}: no frames measured" );
				continue;
			}

			var seconds = MathF.Max( (float)window.sincePerfAsked.Relative, 0.001f );

			into.AppendLine( $"  {width}x{height}: {window.perfFrames / seconds:F0} fps over {window.perfFrames} frames - " +
				$"layout {window.perfLayoutMs / window.perfFrames:F2} ms, input {window.perfInputMs / window.perfFrames:F2} ms, " +
				$"draw+present {window.perfDrawMs / window.perfFrames:F2} ms - {CountPanels( window.Root )} panels" );
		}

		if ( WindowFramesUnderLayer + WindowFramesLeft > 0 )
		{
			into.AppendLine( $"  under a layer since the start: window frames drawn {WindowFramesUnderLayer:N0}, " +
				$"left on the screen as they were {WindowFramesLeft:N0}" );
		}

		//One line for the lot rather than one a window: the count is kept by the input class for every window at once, since the process started.
		var tested = UiWindowInput.HoverWalks + UiWindowInput.HoverSkipped;

		if ( tested > 0 )
		{
			into.AppendLine( $"  hover walks {UiWindowInput.HoverWalks} of {tested} window frames" +
				( UiWindowInput.HoverSkip ? "" : " (ui_hover_skip off)" ) );
		}
	}

	//What this frame's three parts cost, kept as a running total for the trace to report.
	private void Measure( long started, long laidOut, long pointed )
	{
		var done = System.Diagnostics.Stopwatch.GetTimestamp();
		var toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

		var layout = (float)( ( laidOut - started ) * toMs );
		var pointer = (float)( ( pointed - laidOut ) * toMs );
		var draw = (float)( ( done - pointed ) * toMs );

		//Always, cheaply - three additions a frame - so ui_perf has something to answer with whenever it is asked
		perfFrames++;
		perfLayoutMs += layout;
		perfInputMs += pointer;
		perfDrawMs += draw;

		//And on the root, smoothed, for anything in the tree that wants to show what its window costs - the Panel Gallery's readout, a stats overlay.
		var frames = lastMeasured == 0 ? 0f : (float)( 1000.0 / Math.Max( ( done - lastMeasured ) * toMs, 0.001 ) );
		lastMeasured = done;

		if ( Root is not null )
			Root.FrameCost = Root.FrameCost.Toward( new Rinten.UI.UiFrameCost( layout, pointer, draw, frames ), CostSmoothing );

		if ( !tracing ) return;

		layoutMs += layout;
		inputMs += pointer;
		drawMs += draw;
	}

	//How far a frame's reading moves the smoothed one: a tenth, so it settles in about a second at sixty frames.
	private const float CostSmoothing = 0.1f;

	private long lastMeasured;

	private float layoutMs;
	private float inputMs;
	private float drawMs;
	private RealTimeSince sinceTraced;

	private double perfLayoutMs;
	private double perfInputMs;
	private double perfDrawMs;
	private int perfFrames;
	private RealTimeSince sincePerfAsked = 0;

	//The window's own numbers rather than the profiler's: these are measured whether or not anything is recording, so the first ask after something felt slow already has the answer in it. Draw carries the present, which under vsync is where waiting for the display appears.
	/// <summary>What each open window's interface actually costs, to the log, averaged since the last time somebody asked.</summary>
	[ConCmd( "ui_perf", ConVarFlags.Protected,
		Help = "Print what each editor window's interface costs per frame, averaged since last asked" )]
	public static void DumpPerf()
	{
		if ( open.Count == 0 )
		{
			Log.Info( "ui_perf: no windows of ours are open" );
			return;
		}

		foreach ( var window in open ) window.ReportPerf();
	}

	private void ReportPerf()
	{
		var (width, height) = Size;

		if ( perfFrames == 0 )
		{
			log.Info( $"{width}x{height}: no frames since last asked" );
			return;
		}

		var seconds = MathF.Max( (float)sincePerfAsked.Relative, 0.001f );

		log.Info( $"{width}x{height}: {perfFrames / seconds:F0} fps over {perfFrames} frames - " +
			$"layout {perfLayoutMs / perfFrames:F2} ms, input {perfInputMs / perfFrames:F2} ms, " +
			$"draw+present {perfDrawMs / perfFrames:F2} ms - {CountPanels( Root )} panels" );

		perfLayoutMs = 0;
		perfInputMs = 0;
		perfDrawMs = 0;
		perfFrames = 0;
		sincePerfAsked = 0;
	}

	private static int CountPanels( Rinten.UI.Panel panel )
	{
		if ( panel is null ) return 0;

		var count = 1;

		foreach ( var child in panel.Children ) count += CountPanels( child );

		return count;
	}

	//Logs pointer input and what panels made of it a few times a second while -uitrace is on
	private void Trace()
	{
		if ( !tracing ) return;
		if ( traced++ % 30 != 0 ) return;

		var (x, y, buttons) = window.LastPointer;
		var (width, height) = window.SizeInPixels;

		//Per frame rather than per trace: the uploads are what the interface costs, and
		//thirty frames' worth averaged hides exactly the spike somebody is looking for.
		var uploads = CoreTexture.Uploads / 30.0f;
		var pixels = CoreTexture.UploadedPixels / 30.0f;
		var created = CoreTexture.Created / 30.0f;

		CoreTexture.Uploads = 0;
		CoreTexture.UploadedPixels = 0;
		CoreTexture.Created = 0;

		var since = (float)sinceTraced.Relative;
		sinceTraced = 0;

		log.Info( $"{30.0f / MathF.Max( since, 0.0001f ):F0} fps - layout {layoutMs / 30.0f:F1} ms, " +
			$"input {inputMs / 30.0f:F1} ms, draw {drawMs / 30.0f:F1} ms, " +
			$"uploads {uploads:F1}/frame ({created:F1} new, {pixels / 1024.0f:F0}k pixels) - " +
			$"events: {CoreWindow.MotionEvents} motion, {CoreWindow.ButtonEvents} button, " +
			$"{CoreWindow.UnownedEvents} for no window of ours - " +
			$"pointer {x:F0},{y:F0} buttons {buttons} over {Describe( input.Hovered )}" );

		CoreWindow.MotionEvents = 0;
		CoreWindow.ButtonEvents = 0;
		CoreWindow.UnownedEvents = 0;

		layoutMs = 0;
		inputMs = 0;
		drawMs = 0;
	}

	private static readonly bool tracing = Rinten.Utility.CommandLine.HasSwitch( "-uitrace" );
	private int traced;

	//A panel, as somebody reading a log would recognise it: what it is, how deep in the tree, and - for the panels an editor is made of
	//- the widget it is the front of. Every one of those is a WidgetSurface, so the type alone says nothing about which one was found.
	internal static string Describe( Rinten.UI.Panel panel )
	{
		if ( panel is null ) return "nothing";

		var depth = 0;
		for ( var up = panel.Parent; up is not null; up = up.Parent ) depth++;

		//The editor's panels wear their widget's own type as a class - it is how a stylesheet picks a Button
		//out - so the classes say which widget this is the front of, which the panel's own type does not.
		return $"{panel.GetType().Name} [{panel.Classes}] at depth {depth}, {panel.Box.Rect.Size} px";
	}

	//The panels, laid out to the window rather than to a screen.
	private void Layout( int width, int height )
	{
		if ( Root is null || !Root.IsValid ) return;

		try
		{
			if ( Root is null || !Root.IsValid ) return;

			using ( Diagnostics.Performance.Scope( "Panel Tick" ) )
			{
				Root.TickInternal();
			}

			if ( Root is null || !Root.IsValid ) return;

			using ( Diagnostics.Performance.Scope( "Panel PreLayout" ) )
			{
				Root.PreLayout( new Rect( 0, 0, width, height ) );
			}

			if ( Root is null || !Root.IsValid ) return;

			using ( Diagnostics.Performance.Scope( "Panel Yoga" ) )
			{
				Root.CalculateLayout();
			}

			using ( Diagnostics.Performance.Scope( "Panel PostLayout" ) )
			{
				Root.PostLayout();
			}

			caption?.Sync();
		}
		catch ( System.Exception e )
		{
			if ( !laidOutBadly )
			{
				laidOutBadly = true;

				log.Error( e, $"laying a window out ({width}x{height})" );
			}
		}

		try { Dump(); }
		catch { }
	}

	//Whether laying this window out has already thrown once - so it is said once and not sixty times a second.
	private bool laidOutBadly;

	//Logs the whole panel tree once, a few frames in, while -uitrace is on; earlier it is still empty
	private void Dump()
	{
		if ( !tracing ) return;
		if ( dumped ) return;
		if ( laidOutFrames++ < 3 ) return;

		dumped = true;

		//The summary first: how much is in the window, how much of that is text, and how much of the text
		//has a picture to be drawn from - which is what tells an empty window from one that did not draw.
		var panels = 0;
		var labels = 0;
		var pictured = 0;

		void Count( Rinten.UI.Panel panel )
		{
			if ( panel is null ) return;

			panels++;

			if ( panel is Rinten.UI.Label label && !string.IsNullOrEmpty( label.Text ) )
			{
				labels++;

				if ( label.HasPicture ) pictured++;
			}

			foreach ( var child in panel.Children ) Count( child );
		}

		Count( Root );

		log.Info( $"panels: {panels}, text: {pictured} of {labels} drawable" );

		log.Info( "panels in this window:" );

		Dump( Root, 0 );
	}

	private void Dump( Rinten.UI.Panel panel, int depth )
	{
		if ( panel is null ) return;

		var rect = panel.Box.Rect;
		var text = panel is Rinten.UI.Label label && !string.IsNullOrEmpty( label.Text )
			? $" \"{label.Text}\""
			: "";

		log.Info( $"{new string( ' ', depth * 2 )}{panel.ElementName ?? panel.GetType().Name} " +
			$"[{panel.Classes}]{text} at {rect.Left:F0},{rect.Top:F0} {rect.Width:F0}x{rect.Height:F0} " +
			$"{( panel.IsVisible ? "" : "hidden " )}" +
			$"pointer:{panel.ComputedStyle?.PointerEvents} font:{panel.ComputedStyle?.FontFamily}" );

		foreach ( var child in panel.Children ) Dump( child, depth + 1 );
	}

	private bool dumped;
	private int laidOutFrames;

	//Our swapchain on the window, made once and resized after that.
	private bool Attach( int width, int height )
	{
		//The sheet never attaches to the window at all - the window staying unpresented is what keeps its
		//client area the desktop's blur. The interface goes to an offscreen view of the same size instead.
		if ( sheet )
		{
			if ( sheetView == 0 )
			{
				sheetView = CoreRenderDevice.CreateView( width, height );

				if ( sheetView == 0 )
				{
					log.Warning( $"the sheet cannot be drawn into: {CoreRenderDevice.Description}" );
					return false;
				}

				attached = (width, height);
			}
			else if ( attached != (width, height) )
			{
				if ( !CoreRenderDevice.ResizeView( sheetView, width, height ) ) return true;

				attached = (width, height);
			}

			return true;
		}

		if ( swapchain == 0 )
		{
			swapchain = CoreRenderDevice.AttachWindow( window, wantsAlpha, width, height );

			if ( swapchain == 0 )
			{
				log.Warning( $"the window cannot be drawn into: {CoreRenderDevice.Description}" );
				return false;
			}

			attached = (width, height);
			Composited = wantsAlpha && CoreRenderDevice.IsComposited( swapchain );

			//The first moment there is an answer, and the only thing waiting on it: a window cannot
			//clear to nothing until the desktop has said it will look at what is in the alpha.
			TakeCorners();

			//Why a window came out square, which is otherwise a styling mistake to go looking for and is not one.
			if ( !Rounded && !RoundsItself && ( ownsGround || shapedByContent ) )
				log.Warning( $"window '{named}': square corners - the desktop will not look at its alpha" );

			//Said once, here, because this is the first moment there is an answer - the surface has to exist before anyone can ask whether its
			//alpha is looked at. A window that asked for glass and did not get it comes out solid, which reads as a styling mistake and is not one.
			if ( wantsGlass && !Composited )
				log.Warning( "this window asked to be glass and the desktop would not have it - it will be solid" );

			ApplyVSync();
			ApplyRenderScale();

			return true;
		}

		//Wait for the size to settle before rebuilding the swapchain; the desktop stretches the old frame meanwhile
		if ( attached != (width, height) && ( !SwapchainHold || Settled( width, height ) ) )
		{
			//Remembered only once it took, so a size the window system refused mid-change is asked for again
			if ( CoreRenderDevice.Resize( swapchain, width, height ) ) attached = (width, height);
			windowStale = true;
		}

		ApplyVSync();
		ApplyRenderScale();

		return true;
	}

	//Whether a window waits for its size to settle before its swapchain is remade at it. -noswapchainhold is the control half of measuring it.
	private static readonly bool SwapchainHold =
		!Rinten.Utility.CommandLine.HasSwitch( "-noswapchainhold" );

	//Whether the interface freezes during a window drag until images catch up; -noresizefreeze turns it off
	private static readonly bool FreezeOnResize =
		!Rinten.Utility.CommandLine.HasSwitch( "-noresizefreeze" );

	//How long a size has to stand still before the images are remade at it. Short, because everything it buys is bought at the cost of
	//the picture being that far behind the window. A tenth of a second was long enough to see; this is about three frames of a drag.
	private const float HoldSeconds = 0.05f;

	private (int Width, int Height) asked;
	private RealTimeSince sinceAsked;

	//Whether this size has been the one asked for long enough to be worth remaking a window's images at.
	private bool Settled( int width, int height )
	{
		if ( asked != (width, height) )
		{
			asked = (width, height);
			sinceAsked = 0;

			return false;
		}

		return sinceAsked > HoldSeconds;
	}

	//One frame of the sheet: the interface into the view, the pixels out of it, and the picture onto the overlay. Cleared to nothing at all - alpha zero
	//- so a pixel nothing painted is a pixel the overlay does not have, and what shows there is the window's own client area: the desktop, blurred.
	private bool SheetFrame( int width, int height )
	{
		//Sheets never present, so nothing paces them; cap them to the display rate
		var now = System.Diagnostics.Stopwatch.GetTimestamp();

		if ( sheetPresented != 0 && now - sheetPresented < System.Diagnostics.Stopwatch.Frequency / SheetHertz )
			return true;

		sheetPresented = now;

		if ( overlay is null ) return true;

		//Straight into the memory the window system reads the frame out of, so the window is not copied a second time on its way there.
		var pixels = overlay.Frame( width, height );

		if ( pixels.IsEmpty ) return false;

		if ( !CoreRenderDevice.BeginViewFrame( sheetView, Color.Transparent ) ) return false;

		CoreSceneDrawer.DrawUi( Root, width, height );

		var drawn = System.Diagnostics.Stopwatch.GetTimestamp();

		if ( !CoreRenderDevice.EndViewFrame( sheetView ) ) return false;

		var waited = System.Diagnostics.Stopwatch.GetTimestamp();

		if ( !CoreRenderDevice.ReadViewLayered( sheetView, pixels ) ) return false;

		var read = System.Diagnostics.Stopwatch.GetTimestamp();

		SaidWhatItPainted( pixels, width, height );

		//Hidden while the window is: an overlay is not part of the window as far as the window system cares, so it has to follow along by hand.
		if ( window.IsMinimized )
		{
			overlay.Hide();
			return true;
		}

		overlay.Present();

		if ( shownOnScreen ) overlay.Show();

		//Never make the desktop drop its client-area picture, or the sheet turns flat white

		SaidWhereTheFrameWent( now, drawn, waited, read );

		return true;
	}

	private long spentDrawing, spentWaiting, spentReading, spentShowing;

	//Logs where a sheet's frame time went: drawing, waiting on the GPU, readback, or handing to the desktop
	private void SaidWhereTheFrameWent( long started, long drawn, long waited, long read )
	{
		var done = System.Diagnostics.Stopwatch.GetTimestamp();

		spentDrawing += drawn - started;
		spentWaiting += waited - drawn;
		spentReading += read - waited;
		spentShowing += done - read;

		if ( sheetFrames % ( SheetHertz * 2 ) != 0 ) return;

		var perFrame = 1000.0 / System.Diagnostics.Stopwatch.Frequency / ( SheetHertz * 2 );
		var whole = ( spentDrawing + spentWaiting + spentReading + spentShowing ) * perFrame;

		//Only log when the frame is actually slow
		if ( whole <= 1000.0 / SheetHertz )
		{
			spentDrawing = spentWaiting = spentReading = spentShowing = 0;
			return;
		}

		log.Info( $"window '{named}': sheet frame {whole:F1}ms" +
			$" = draw {spentDrawing * perFrame:F1}" +
			$" + wait {spentWaiting * perFrame:F1}" +
			$" + read {spentReading * perFrame:F1}" +
			$" + show {spentShowing * perFrame:F1}" );

		spentDrawing = spentWaiting = spentReading = spentShowing = 0;
	}

	//What the window was opened as, for saying which one this is.
	private string named;

	//How often a sheet is allowed to put its pixels on the screen.
	private const int SheetHertz = 60;

	//When it last did, so it can be held to that.
	private long sheetPresented;

	//How many frames this sheet has presented.
	private int sheetFrames;

	//Logs the sheet's painted pixels for its first few frames, to tell broken-glass failure modes apart
	private void SaidWhatItPainted( ReadOnlySpan<byte> sheetPixels, int width, int height )
	{
		sheetFrames++;

		//The first frame, then a second in and two seconds in: a window is styled and laid out over its opening
		//frames, so what it painted at once and what it settled into are different answers and both are wanted.
		if ( sheetFrames != 1 && sheetFrames != SheetHertz && sheetFrames != SheetHertz * 2 ) return;

		//The whole picture rather than a few points in it: two samples that both land in a gap say "nothing was painted" about a
		//window that is full of interface. Every fourth pixel each way, which is a sixteenth of the work and tells the same story.
		long clear = 0, solid = 0, part = 0;
		long red = 0, green = 0, blue = 0, lit = 0;

		for ( var y = 0; y < height; y += 4 )
		{
			for ( var x = 0; x < width; x += 4 )
			{
				var i = (y * width + x) * 4;
				var a = sheetPixels[i + 3];

				if ( a == 0 ) { clear++; continue; }

				if ( a == 255 ) solid++; else part++;

				//Premultiplied, so back out the alpha to get the colour that was actually asked for rather than one dimmed by it.
				blue += sheetPixels[i] * 255 / a;
				green += sheetPixels[i + 1] * 255 / a;
				red += sheetPixels[i + 2] * 255 / a;
				lit++;
			}
		}

		var counted = clear + solid + part;
		if ( counted == 0 ) return;

		var colour = lit == 0 ? "-" : $"r{red / lit} g{green / lit} b{blue / lit}";

		log.Info( $"window '{named}': sheet {width}x{height} painted " +
			$"{solid * 100 / counted}% solid, {part * 100 / counted}% part, " +
			$"{clear * 100 / counted}% nothing, colour [{colour}]" );
	}

	//Last vsync value sent to the swapchain; null until first set so the real setting always gets applied
	private bool? vsync;

	//Applies the vsync setting when it changed; polled per frame so newly opened windows pick it up
	private void ApplyVSync()
	{
		if ( swapchain == 0 ) return;

		var wanted = Engine.Settings.RenderSettings.Instance.VSync;
		if ( wanted == vsync ) return;

		vsync = wanted;

		CoreRenderDevice.SetVSync( swapchain, wanted );

		if ( layerSwapchain != 0 ) CoreRenderDevice.SetVSync( layerSwapchain, wanted );
	}

	//What we last told the swapchain about how big to draw the world.
	private float renderScale = 1.0f;

	//Applies the render scale when it changed; polled per frame so newly opened windows pick it up
	private void ApplyRenderScale()
	{
		if ( swapchain == 0 ) return;

		//What the settings mean by the mode is CoreUpscale's business; all that is needed here is a number to compare against the last one.
		var wanted = CoreUpscale.WindowScale;

		if ( Math.Abs( wanted - renderScale ) < 0.001f ) return;

		renderScale = wanted;

		var applied = CoreRenderDevice.ApplyRenderScale( swapchain );
		log.Info( $"window: the world drawn at {wanted:0.##} of its size{( applied ? "" : $" - refused: {CoreRenderDevice.Description}" )}{( layerSwapchain != 0 ? ", and on its layer" : "" )}" );

		//The world is drawn on the layer while there is one.
		if ( layerSwapchain != 0 ) CoreRenderDevice.ApplyRenderScale( layerSwapchain );
	}

	/// <summary>What the pointer is over, or null.</summary>
	public Rinten.UI.Panel Hovered => input.Hovered;

	//Every window open, in the order they were opened.
	internal static IReadOnlyList<CoreUiWindow> AllOpen => open;

	//Frames this window has drawn. What to watch to know that something put in front of the window - a pretended click, a changed panel - has been seen.
	internal long FramesDrawn { get; private set; }

	//Renders the world panel's camera at a given size, pasted into window screenshots
	internal Func<int, int, Bitmap> WorldShot { get; set; }

	//The pointer put somewhere in this window with buttons pressed or released, as though the window system had said so. Seen by the next frame.
	internal void PretendPointer( Vector2 at, MouseButtons press, MouseButtons release ) => window?.PretendPointer( at, press, release );

	//The wheel turned in this window, as though the window system had said so.
	internal void PretendWheel( Vector2 turned ) => window?.PretendWheel( turned );

	//A key went down or came up in this window, as though the window system had said so.
	internal void PretendKey( NativeEngine.ButtonCode button, KeyboardModifiers modifiers, bool down ) => window?.PretendKey( button, modifiers, down );

	//Text composed in this window, as though the window system had said so.
	internal void PretendTyped( string text ) => window?.PretendTyped( text );

	//The last frame's cost, as the device measured it: the window's own, and the world's layer where it has one.
	internal (CoreRenderDevice.FrameProfile? Window, CoreRenderDevice.FrameProfile? Layer) Profiles()
	{
		return (
			swapchain != 0 ? CoreRenderDevice.ReadProfile( swapchain ) : null,
			layerSwapchain != 0 ? CoreRenderDevice.ReadProfile( layerSwapchain ) : null );
	}

	//Where the world panel lies in the window, or null with none.
	internal Rect? WorldRect
	{
		get
		{
			var panel = WorldPanel;
			if ( panel is not { IsValid: true, IsVisible: true } ) return null;
			return panel.Box.Rect;
		}
	}

	//Screenshot of the window: panels drawn offscreen, with the world drawn by its camera pasted in
	internal Bitmap Shoot()
	{
		var (width, height) = Size;

		if ( width <= 0 || height <= 0 ) return null;
		if ( Root is null || !Root.IsValid ) return null;

		var wanted = width * height * 4;

		if ( pickView == 0 || pickSize != (width, height) )
		{
			if ( pickView != 0 ) CoreRenderDevice.DestroyView( pickView );

			pickView = CoreRenderDevice.CreateView( width, height );

			if ( pickView == 0 ) return null;

			pickSize = (width, height);
		}

		if ( !CoreRenderDevice.BeginViewFrame( pickView, Background ) ) return null;

		CoreSceneDrawer.DrawUi( Root, width, height );

		if ( !CoreRenderDevice.EndViewFrame( pickView ) ) return null;

		var pixels = new byte[wanted];

		if ( !CoreRenderDevice.ReadView( pickView, pixels ) ) return null;

		var bitmap = new Bitmap( width, height );
		bitmap.SetPixelsRgba( pixels );

		if ( WorldRect is { } area && WorldShot is { } shoot && area.Width >= 1 && area.Height >= 1 )
		{
			using var world = shoot( (int)area.Width, (int)area.Height );

			if ( world is { IsValid: true } )
				PutWorld( bitmap, world, area, width, height, pixels );
		}

		return bitmap;
	}

	//Puts the world into a screenshot, redrawing any panels layered over it so they appear too
	private void PutWorld( Bitmap bitmap, Bitmap world, Rect area, int width, int height, byte[] pixels )
	{
		if ( Root is Rinten.UI.RootPanel { IsEditorUi: true } editor )
		{
			using var still = world.ToTexture( mips: false );
			var picture = still?.core?.Device() ?? 0;

			if ( picture != 0 && CoreRenderDevice.BeginViewFrame( pickView, Background ) )
			{
				CoreSceneDrawer.DrawEditorUiOverPicture( editor, width, height, picture );

				if ( CoreRenderDevice.EndViewFrame( pickView ) && CoreRenderDevice.ReadView( pickView, pixels ) )
				{
					using var layered = new Bitmap( width, height );
					layered.SetPixelsRgba( pixels );

					using var place = layered.Crop( area );
					bitmap.DrawBitmap( place, area );
					return;
				}
			}
		}

		bitmap.DrawBitmap( world, area );
	}

	/// <summary>What the window's own bar says.</summary>
	public string Title
	{
		get => named;
		set
		{
			named = value;
			window?.SetTitle( value );
			if ( caption is not null ) caption.Title = value;
		}
	}

	/// <summary>Puts it on the screen, or takes it off.</summary>
	public bool Shown
	{
		set
		{
			if ( !value )
			{
				wantsShowing = false;
				overlay?.Hide();
				window?.Hide();
				return;
			}

			//Already drawing, so there is something to show right now.
			if ( shownOnScreen )
			{
				window?.Show();
				overlay?.Show();
				return;
			}

			wantsShowing = true;
		}
	}

	//Asked to be on the screen, and waiting for a frame to be on it with.
	private bool wantsShowing;

	//Whether it has been put on the screen.
	private bool shownOnScreen;

	//Frames tried since it asked to be shown.
	private int framesWaitedToShow;

	//Frames to wait for a successful present before showing the window anyway
	private const int FramesToWaitToShow = 60;

	//Called once a frame has been presented: the window has something to show.
	private void ShowOnceDrawn()
	{
		if ( !wantsShowing || shownOnScreen ) return;

		shownOnScreen = true;
		wantsShowing = false;

		window?.Show();
		overlay?.Show();
	}

	/// <summary>Rounds the window's corners and lets the desktop show through it.</summary>
	public void MakeGlass( float opacity, float radius )
	{
		window?.MakeGlass( opacity, radius );
	}

	/// <summary>Makes the window a sheet of glass: what it clears to black is the blur behind it, and what it draws is itself.</summary>
	public void MakeSheet( float radius = 0.0f )
	{
		window?.MakeSheet( radius );
	}

	/// <summary>Asks the window system to make it this big.</summary>
	public void Resize( int width, int height )
	{
		window?.SetSize( width, height );
	}

	/// <summary>Where it is on the desktop, and how big.</summary>
	public Rect Geometry
	{
		get
		{
			if ( window is null ) return default;

			var (x, y) = window.Position;
			var (width, height) = window.SizeInPixels;

			return new Rect( x, y, width, height );
		}

		set
		{
			if ( window is null ) return;

			window.Position = (value.Left.FloorToInt(), value.Top.FloorToInt());
			window.SetSize( value.Width.CeilToInt(), value.Height.CeilToInt() );
		}
	}

	/// <summary>Where the window is and how big, in the window system's own units.</summary>
	public Rect Placement
	{
		get
		{
			if ( window is null ) return default;

			var (x, y) = window.Position;
			var (width, height) = window.Size;

			return new Rect( x, y, width, height );
		}

		set
		{
			if ( window is null ) return;

			window.Position = (value.Left.FloorToInt(), value.Top.FloorToInt());
			window.SetSize( value.Width.CeilToInt(), value.Height.CeilToInt() );
		}
	}

	public bool IsMaximized => window?.IsMaximized ?? false;
	public bool IsMinimized => window?.IsMinimized ?? false;

	public void Maximize() => window?.Maximize();
	public void Minimize() => window?.Minimize();
	public void Restore() => window?.Restore();

	/// <summary>Whether this window has the screen to itself - see CoreWindow.SetFullscreen.</summary>
	public bool IsFullscreen => window?.IsFullscreen ?? false;

	public void SetFullscreen( bool fullscreen ) => window?.SetFullscreen( fullscreen );

	static ulong pickView;
	static (int Width, int Height) pickSize;
	static byte[] pickPixels = Array.Empty<byte>();

	//Reads one pixel from what this window last drew, or redraws UI once to read it.
	internal bool TrySamplePixel( int x, int y, out Color color )
	{
		color = default;

		var (width, height) = Size;

		if ( width <= 0 || height <= 0 || x < 0 || y < 0 || x >= width || y >= height )
			return false;

		if ( Root is null || !Root.IsValid ) return false;

		var wanted = width * height * 4;

		if ( sheet && sheetView != 0 )
		{
			if ( pickPixels.Length < wanted ) pickPixels = new byte[wanted];

			if ( !CoreRenderDevice.ReadViewLayered( sheetView, pickPixels.AsSpan( 0, wanted ) ) )
				return false;

			return TryColorFromLayered( pickPixels, width, height, x, y, out color );
		}

		if ( pickView == 0 || pickSize != (width, height) )
		{
			if ( pickView != 0 ) CoreRenderDevice.DestroyView( pickView );

			pickView = CoreRenderDevice.CreateView( width, height );

			if ( pickView == 0 ) return false;

			pickSize = (width, height);
		}

		if ( !CoreRenderDevice.BeginViewFrame( pickView, Background ) ) return false;

		CoreSceneDrawer.DrawUi( Root, width, height );

		if ( !CoreRenderDevice.EndViewFrame( pickView ) ) return false;

		if ( pickPixels.Length < wanted ) pickPixels = new byte[wanted];

		if ( !CoreRenderDevice.ReadView( pickView, pickPixels.AsSpan( 0, wanted ) ) )
			return false;

		var i = (y * width + x) * 4;

		color = new Color(
			pickPixels[i] / 255f,
			pickPixels[i + 1] / 255f,
			pickPixels[i + 2] / 255f,
			pickPixels[i + 3] / 255f );

		return color.a > 0.001f;
	}

	static bool TryColorFromLayered( byte[] pixels, int width, int height, int x, int y, out Color color )
	{
		color = default;

		var i = (y * width + x) * 4;
		var a = pixels[i + 3];

		if ( a == 0 ) return false;

		var scale = 255f / a;

		color = new Color(
			pixels[i + 2] * scale / 255f,
			pixels[i + 1] * scale / 255f,
			pixels[i] * scale / 255f,
			a / 255f );

		return true;
	}

	/// <summary>Raised once when this window closes by any means (not on process exit), before it is torn down</summary>
	public Action Closed { get; set; }

	/// <summary>The window changed shape: resized, maximised, minimised or put back. Raised from the window's own frame, on the main thread, before it is laid out. An event rather than a single handler like <see cref="Closed"/>: a window drawn inside this one listens as well as the one that owns it.</summary>
	public event Action Resized;

	//So the handler closing us can dispose us again without looping.
	private bool disposing;

	public void Dispose()
	{
		if ( disposing ) return;
		disposing = true;

		var closed = Closed;
		Closed = null;
		closed?.Invoke();

		open.Remove( this );

		Root?.Delete( true );
		Root = null;

		//The layer first: it lies over the window's surface and has to go before it does.
		DropLayer();

		if ( swapchain != 0 )
		{
			CoreRenderDevice.DetachWindow( swapchain );
			swapchain = 0;
		}

		if ( sheetView != 0 )
		{
			CoreRenderDevice.DestroyView( sheetView );
			sheetView = 0;
		}

		overlay?.Dispose();
		overlay = null;

		window?.Dispose();
		window = null;
	}
}

//Per-window pointer state: each editor window keeps its own cursor in its own pixels
internal sealed class UiWindowInput : PanelInput
{
	private InputData state;
	private List<CoreWindow.Key> keys;
	private List<char> typed;

	/// <summary>Takes what the window system has to say about the pointer and the keyboard.</summary>
	public void Read( CoreWindow window )
	{
		var (x, y, buttons) = window.Pointer();

		state.MousePos = new Vector2( x, y );
		//Panel button order: 1 is middle, 2 is right, matching PanelInput, not the button names
		state.Mouse0 = ( ( buttons & MouseButtons.Left ) == MouseButtons.Left );
		state.Mouse1 = ( ( buttons & MouseButtons.Middle ) == MouseButtons.Middle );
		state.Mouse2 = ( ( buttons & MouseButtons.Right ) == MouseButtons.Right );
		state.Mouse3 = ( ( buttons & MouseButtons.Back ) == MouseButtons.Back );
		state.Mouse4 = ( ( buttons & MouseButtons.Forward ) == MouseButtons.Forward );
		state.MouseWheel = -window.TakeWheel();

		keys = window.TakeKeys();
		typed = window.TakeTyped();

	}

	//The window's root, so unfocused keys still reach its widgets.
	private RootPanel root;

	//Hover, press, click and typing, against this window's panels. The single-root list handed to the walk below - see where it is filled.
	private RootPanel[] justTheRoot;

	public void Tick( RootPanel root, CoreWindow window = null )
	{
		this.root = root;

		if ( root is null || !root.IsValid ) return;

		//Only decides game cursor visibility, which editor roots ignore; skipped when the hover test is too
		if ( !HoverSkip || !root.IsEditorUi )
			root.TickInputInternal();

		//The one root, in an array held rather than made: this is once a frame for every editor window open.
		( justTheRoot ??= new RootPanel[1] )[0] = root;

		Tick( justTheRoot, mouseIsActive: true );

		Moved();

		DoubleClicked();

		//After the pointer, because clicking a panel is how it takes the focus, and what is typed this tick belongs to what was clicked this tick.
		InputFocus.Tick();

		//Composed text only while a field wants it. Left on all the time, a Wayland
		//compositor swallows printable keys into IME and the editor camera never sees WASD.
		window?.SetTextInput( WantsText( InputFocus.Current ) );

		Typing();
	}

	//Whether the focused panel is a field that composes text.
	private static bool WantsText( Panel focused )
	{
		for ( var panel = focused; panel is not null; panel = panel.Parent )
		{
			if ( panel.AcceptsImeInput ) return true;
		}

		return false;
	}

	//Where the pointer was last tick, so a move is only sent when it moved.
	private Vector2 lastMousePos = new( float.NaN, float.NaN );

	//Sends mouse moves to the pressed panel, else the hovered one; the game's event queue skips windows
	private void Moved()
	{
		if ( state.MousePos == lastMousePos ) return;

		lastMousePos = state.MousePos;

		var target = Active ?? Hovered;

		if ( target is null ) return;

		target.CreateEvent( new MousePanelEvent( "onmousemove", target, "none" ) );
	}

	//Double-click detection for windows, which the game's input queue never sees

	//How far apart in time two presses may be and still be one click.
	private const double DoubleClickSeconds = 0.4;

	//And how far apart on the screen, in pixels.
	private const float DoubleClickSlack = 6.0f;

	private bool leftWasDown;
	private long lastPressed;
	private Vector2 lastPressedAt;

	private void DoubleClicked()
	{
		//The moment it goes down, and only that moment.
		if ( state.Mouse0 == leftWasDown ) return;

		leftWasDown = state.Mouse0;

		if ( !leftWasDown ) return;

		var now = System.Diagnostics.Stopwatch.GetTimestamp();

		var since = ( now - lastPressed ) / (double)System.Diagnostics.Stopwatch.Frequency;
		var moved = state.MousePos.Distance( lastPressedAt );

		var again = lastPressed != 0 && since <= DoubleClickSeconds && moved <= DoubleClickSlack;

		//A third press starts counting again rather than being a second double click - otherwise a rapid four clicks open something twice.
		lastPressed = again ? 0 : now;
		lastPressedAt = state.MousePos;

		if ( !again ) return;

		//To what is under the pointer, which is what the press itself went to.
		var target = Hovered;

		if ( target is null ) return;

		target.CreateEvent( new MousePanelEvent( "ondoubleclick", target, "mouseleft" ) );
	}

	//Sends typed keys then characters to the focused panel; keys still go out with nothing focused
	private void Typing()
	{
		var focused = InputFocus.Current;
		var target = focused.IsValid() ? focused : FallbackKeyTarget();

		//Whoever listens for every key, before the focus is consulted and whether or not there is one
		//- an editor's shortcuts are the window's, not the focused panel's. See CoreUiWindow.KeyHeard.
		if ( keys is not null && CoreUiWindow.KeyHeard is { } heard )
		{
			foreach ( var key in keys ) Guarded( () => heard( key ), "a shortcut" );
		}

		if ( !target.IsValid() )
		{
			keys = null;
			typed = null;
			return;
		}

		if ( keys is not null )
		{
			foreach ( var key in keys )
			{
				if ( focused.IsValid() ) Clipboard( focused, key );

				Guarded( () => target.OnButtonTyped( new ButtonEvent( key.Button, key.Down, key.Modifiers ) ), "a key" );
			}

			keys = null;
		}

		if ( typed is not null && focused.IsValid() )
		{
			//A key that produced a letter is that letter's business, not the panel's - the ones a text field acts on itself arrive above.
			foreach ( var letter in typed )
				if ( !char.IsControl( letter ) ) Guarded( () => focused.OnKeyTyped( letter ), "a letter" );

			typed = null;
		}
		else
		{
			typed = null;
		}
	}

	//Maps Ctrl+C/X/V to the panel copy, cut and paste events; the key still reaches the panel after
	private static void Clipboard( Panel focused, CoreWindow.Key key )
	{
		//Ctrl alone: with Shift or Alt it is a shortcut of somebody's, not a copy.
		if ( !key.Down || key.Modifiers != KeyboardModifiers.Ctrl ) return;

		PanelEvent e = key.Button switch
		{
			NativeEngine.ButtonCode.KEY_C => new CopyEvent(),
			NativeEngine.ButtonCode.KEY_X => new CutEvent(),
			NativeEngine.ButtonCode.KEY_V when EnginePlatform.HasClipboardText() => new PasteEvent( EnginePlatform.GetClipboardText() ),
			_ => null,
		};

		if ( e is null ) return;

		e.Target = focused;
		focused.CreateEvent( e );
	}

	//Runs a key handler and logs its exceptions so one bad handler can't crash the editor
	private static void Guarded( System.Action handle, string what )
	{
		try
		{
			handle();
		}
		catch ( System.Exception e )
		{
			Log.Error( e, $"handling {what} threw" );
		}
	}

	//The window's root, which hands unfocused keys to the widget tree.
	private Panel FallbackKeyTarget() => root;

	internal override InputData GetInputData() => state;

	//The hit test, only when something could have changed its answer. An editor window is a few thousand panels and the walk
	//over every visible one of them ran every frame, pointer moving or not, to find the same panel it found the frame before.

	/// <summary>Whether a window's hit test is left out while nothing that decides it has moved. <c>ui_hover_skip 0</c> tests every visible panel every frame, as before.</summary>
	[ConVar( "ui_hover_skip", Help = "Hit-test an editor window for hover only when the pointer, a button, a drag or the panels changed; 0 tests every frame" )]
	public static bool HoverSkip { get; set; } = true;

	//Frames the hit test ran, and frames it was left out, across every window since the process started.
	internal static long HoverWalks, HoverSkipped;

	//What the last hit test was made under: where the pointer was, which buttons were down, whether a drag was
	//on, and the panels' generation. Not a number until the first one has run, so the first frame always walks.
	private Vector2 testedAt = new( float.NaN, float.NaN );
	private byte testedButtons;
	private bool testedDragged;
	private uint testedGeneration;

	//Whether last hover result still holds: same tree generation, pointer, buttons and drag, panels alive
	private protected override bool HoverUnchanged( RootPanel root, InputData data )
	{
		//The editor's own windows only. A game's screen, in a build or played inside the editor, is hit-tested every frame as it always was.
		if ( !HoverSkip || !root.IsEditorUi ) return false;

		var buttons = (byte)( ( data.Mouse0 ? 1 : 0 ) | ( data.Mouse1 ? 2 : 0 ) | ( data.Mouse2 ? 4 : 0 )
			| ( data.Mouse3 ? 8 : 0 ) | ( data.Mouse4 ? 16 : 0 ) );
		var dragged = MouseStates[0].Dragged;
		var generation = root.TreeGeneration;

		var same = data.MousePos == testedAt
			&& buttons == testedButtons
			&& dragged == testedDragged
			&& generation == testedGeneration
			&& Stands( Hovered )
			&& Stands( DropTarget );

		testedAt = data.MousePos;
		testedButtons = buttons;
		testedDragged = dragged;
		testedGeneration = generation;

		if ( same ) HoverSkipped++;
		else HoverWalks++;

		return same;
	}

	//Whether a panel the last walk found can still be handed events: nothing found, or a live one not on its way out.
	private static bool Stands( Panel panel ) => panel is null || ( panel.IsValid && !panel.IsDeleting );

	//The window system's own cursors, by the same call a game makes. Without this the arrow never changes over anything in the editor - and the arrow staying an arrow over a button is how an interface says the button is not a button.
	/// <summary>The cursor a panel asked for, in the names a stylesheet uses.</summary>
	public override void SetCursor( string name )
	{
		if ( name == asked ) return;

		asked = name;

		CoreInput.SetCursorStandard( Standard( name ) );
	}

	//What was last asked for, so the same cursor is not set every frame.
	private string asked = "arrow";

	//A CSS cursor name as the window system counts cursors. Anything unknown is the arrow, which is what a name nobody implemented should look like.
	private static InputStandardCursor_t Standard( string name ) => name switch
	{
		null or "" or "default" or "auto" => InputStandardCursor_t.Arrow,
		"pointer" or "hand" => InputStandardCursor_t.Hand,
		"text" => InputStandardCursor_t.IBeam,
		"wait" or "progress" => InputStandardCursor_t.HourGlass,
		"crosshair" => InputStandardCursor_t.Crosshair,
		"move" or "grab" or "grabbing" or "all-scroll" => InputStandardCursor_t.SizeALL,
		"ew-resize" or "col-resize" or "e-resize" or "w-resize" => InputStandardCursor_t.SizeWE,
		"ns-resize" or "row-resize" or "n-resize" or "s-resize" => InputStandardCursor_t.SizeNS,
		"nwse-resize" or "nw-resize" or "se-resize" => InputStandardCursor_t.SizeNWSE,
		"nesw-resize" or "ne-resize" or "sw-resize" => InputStandardCursor_t.SizeNESW,
		"not-allowed" or "no-drop" => InputStandardCursor_t.No,
		"none" => InputStandardCursor_t.None,
		_ => InputStandardCursor_t.Arrow,
	};
}

//The root of a window's panels. Scaled one to one: a window is measured in pixels and its widgets are
//written in pixels, unlike a game's interface, which is scaled so that the same layout suits every screen.
internal sealed class UiWindowRoot : RootPanel
{
	//Baked into the type: the tests build one directly, and the only other maker is CoreUiWindow.Open.
	public UiWindowRoot()
	{
		IsEditorUi = true;
	}

	public override bool IsWorldPanel => false;

	//The window drives this tree; the game's UI system keeps off it.
	internal override bool ExternallyDriven => true;

	protected override void UpdateScale( Rect screenSize )
	{
		Scale = 1.0f;
	}

	bool handingOn;

	public override void OnButtonTyped( ButtonEvent e )
	{
		//A child that does not swallow the key bubbles it here. Sending it back down is how a lobby key became a stack overflow.
		if ( handingOn ) return;

		handingOn = true;
		try
		{
			foreach ( var child in Children )
			{
				child.OnButtonTyped( e );
				if ( e.StopPropagation ) break;
			}
		}
		finally
		{
			handingOn = false;
		}
	}
}
