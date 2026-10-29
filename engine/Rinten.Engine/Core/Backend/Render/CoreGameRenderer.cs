using NativeEngine;

namespace Rinten;

//Draws the standalone game into its window; returns false so the caller can fall back to the legacy renderer
internal static class CoreGameRenderer
{
	//The game window, set by the app system; null in the editor and in headless runs
	internal static CoreWindow Window { get; set; }

	private static ulong swapchain;

	//Frames presented by the game window or headset; non-editor builds wait on this, not CoreUiWindow
	internal static long FramesPresented { get; private set; }
	private static (int Width, int Height) attached;
	private static bool drawn;

	//Last vsync setting sent to the swapchain; null until first sent, so it is always applied once
	private static bool? vsyncIs;

	//Whether the tick's frame is open; the first scene to draw opens it, Present closes it
	private static bool framing;

	//Picture size: the window's before the first frame, the swapchain's after, so early UI layout works
	internal static (int Width, int Height) Size
		=> attached.Width > 0 ? attached : WindowSize();

	//Told it is drawing a game rather than an editor viewport, which is what decides whether the furniture of the editor - the grid, the icons over
	//components, the selection outline - is drawn, and whether the game's own panels are. A window with no editor around it is always the game.
	private static readonly CoreSceneDrawer drawer = new() { GameViewSource = () => true };

	//Draws the scene from its own camera, into this tick's frame. False means it did not - and then whoever asked falls back to the legacy renderer.
	internal static bool Render( Scene scene )
	{
		//The editor has surfaces of its own on every window it draws, and the game view is one of them. Nothing
		//here has a window to draw into there, and drawing into the main one would be drawing over the editor.
		if ( Application.IsEditor ) return false;

		if ( !scene.IsValid() ) return false;
		if ( Window is null ) return false;

		var camera = scene.Camera;
		if ( !camera.IsValid() ) return false;

		if ( !CoreNative.Available || !CoreRenderDevice.Initialize() ) return false;

		var (width, height) = WindowSize();

		if ( width <= 0 || height <= 0 ) return false;

		if ( !Attach( width, height ) ) return false;

		//The cameras drawing into textures first, each a view of its own sent before the window's frame opens - so the frame samples what they
		//saw this frame rather than last. These are drawn either way: a render texture may be what a panel in front of the player is showing.
		CoreRenderDevice.ZoneBegin( "camera targets" );
		CoreCameraTargets.Draw( scene );
		CoreRenderDevice.ZoneEnd();

		//No window frame while a headset has the picture, unless VRSystem.MirrorToScreen asks for one
		if ( CoreHeadset.HasSession && !Rinten.VR.VRSystem.MirrorToScreen )
		{
			FramesPresented++;
			return true;
		}

		//The first scene of the tick opens the frame and says what colour the window starts as; anything after it draws on top of what is there.
		if ( !framing )
		{
			CoreRenderDevice.ZoneBegin( "begin frame" );
			var begun = CoreRenderDevice.BeginFrame( swapchain, camera.BackgroundColor );
			CoreRenderDevice.ZoneEnd();

			if ( !begun ) return false;

			framing = true;
		}

		//The world. A session that is being mirrored draws it here as well as in the headset; one that is not never reaches this line - see above.
		drawer.Draw( scene, camera, swapchain, width, height );

		//Once, on the first frame that made it all the way through. Whether the game is being drawn by us or by
		//them is the first thing anybody asks of a log, and it is otherwise invisible - both look like a picture.
		if ( !drawn )
		{
			drawn = true;
			Log.Info( $"drawing the game: {width}x{height}" );
		}

		return true;
	}

	//Draws a panel tree over this tick's frame, opening one on black where nothing drew yet - see EngineSplash
	internal static bool DrawOver( Rinten.UI.RootPanel root )
	{
		if ( Application.IsEditor || Window is null ) return false;

		if ( !CoreNative.Available || !CoreRenderDevice.Initialize() ) return false;

		var (width, height) = WindowSize();

		if ( width <= 0 || height <= 0 ) return false;

		if ( !Attach( width, height ) ) return false;

		if ( CoreHeadset.HasSession && !Rinten.VR.VRSystem.MirrorToScreen ) return false;

		if ( !framing )
		{
			if ( !CoreRenderDevice.BeginFrame( swapchain, Color.Black ) ) return false;

			framing = true;
		}

		CoreSceneDrawer.DrawUi( root, width, height );

		return true;
	}

	//Puts this tick's frame on the screen, if anything drew into one. Called at the end of the tick's drawing, after every scene that
	//wanted into it has had its turn. Nothing to do where nobody drew - a tick with no scene presents nothing rather than a blank window.
	internal static void Present()
	{
		if ( !framing ) return;

		framing = false;
		FramesPresented++;
		CoreRenderDevice.EndFrame();
	}

	//Our swapchain on the engine's window, made once and resized after that.
	private static bool Attach( int width, int height )
	{
		if ( swapchain == 0 )
		{
			swapchain = CoreRenderDevice.AttachWindow( Window, widthPixels: width, heightPixels: height );

			if ( swapchain == 0 )
			{
				//Once, not every frame: a window we cannot draw into does not become one we can, and there is nothing else to draw with.
				Window = null;

				Log.Warning( "the game's window could not be drawn into - nothing will be drawn" );
				return false;
			}

			attached = WindowSize();

			//The furniture every viewport draws with. Baked here rather than on the first draw so a failure is one message rather than one a frame.
			if ( !CoreSceneDrawer.EnsureResources() )
			{
				Log.Warning( "the renderer has nothing to draw the game with" );
				return false;
			}
		}

		if ( attached != (width, height) )
		{
			CoreRenderDevice.Resize( swapchain, width, height );
			attached = (width, height);
		}

		//What the display is allowed to do to the pace. Asked here once a frame rather than pushed from the setting, and
		//acted on only when it moves - the mode is baked into the swapchain, so asking for the other thing rebuilds it.
		var wantsVsync = Engine.Settings.RenderSettings.Instance.VSync;

		if ( vsyncIs != wantsVsync )
		{
			vsyncIs = wantsVsync;

			CoreRenderDevice.SetVSync( swapchain, wantsVsync );
		}

		return true;
	}

	//How big the window is now, in pixels. Asked of the window itself rather than of an
	//engine: it is the one thing that knows, and it changes when somebody drags a corner.
	private static (int Width, int Height) WindowSize()
	{
		if ( Window is null ) return (0, 0);

		if ( Application.OnWeb )
		{
			var width = WebPage.CanvasCssWidth?.Invoke() ?? 0;
			var height = WebPage.CanvasCssHeight?.Invoke() ?? 0;
			if ( width > 0 && height > 0 ) return (width, height);
		}

		return Window.SizeInPixels;
	}

	//Releases the window and swapchain but keeps the rest, for mobile; next Render uses the new window
	internal static void LetGoOfWindow()
	{
		Present();

		CoreRenderDevice.DetachWindow( swapchain );
		swapchain = 0;

		attached = default;
		vsyncIs = null;
		Window = null;
	}

	//Lets the window go. For shutdown.
	internal static void Shutdown()
	{
		drawer.Release();

		Present();

		CoreRenderDevice.DetachWindow( swapchain );
		swapchain = 0;

		//The next one is a new swapchain on the core's own default, and what we told this one is not true of it.
		vsyncIs = null;
		Window = null;
		drawn = false;
	}
}
