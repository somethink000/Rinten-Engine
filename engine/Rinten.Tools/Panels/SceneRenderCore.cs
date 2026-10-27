using Rinten;
using System;

namespace Editor;

/// <summary>Drawing a scene onto an editor surface, once per frame.</summary>
public sealed class SceneRenderCore : IDisposable
{
	private static readonly List<SceneRenderCore> All = new();

	private readonly ISceneRenderHost host;

	private CoreScenePreview preview;

	//Held for the same reason the delegate above is - see where it is used.
	private System.Func<Gizmo.Instance> gizmoSource;
	private Texture frame;

	//Whether this surface is the one telling the game how big its screen is.
	private bool holdsScreen;

	//How far the wheel has turned over this surface since the game read it.
	private Vector2 wheel;

	public SceneRenderCore( ISceneRenderHost host )
	{
		this.host = host;

		All.Add( this );
	}

	/// <summary>The scene, as our renderer left it on the device.</summary>
	public Texture Frame => frame;

	/// <summary>Notches of wheel the game has not been told about yet.</summary>
	public void AddWheel( float notches ) => wheel += new Vector2( 0, notches );

	public void Dispose()
	{
		All.Remove( this );

		//The console asks the surface itself what it looked like, so a surface that is going away stops being the one it asks.
		if ( ReferenceEquals( lastSurface, host ) ) lastSurface = null;

		Release();
	}

	/// <summary>Gives back the game's screen and drops the picture on the device.</summary>
	public void Release()
	{
		if ( holdsScreen )
		{
			Screen.Viewport = null;
			holdsScreen = false;
		}

		ReleaseUnderlay();

		preview?.Dispose();
		preview = null;

		frame = null;
	}

	//The views drawn this frame, copied out of All - see RenderAll.
	private static readonly List<SceneRenderCore> drawing = new();

	internal static void RenderAll()
	{
		var rendered = 0;

		//A copy, because drawing a scene can open or close a view - a tool that makes one, a session
		//that ends - and the list is this one. Into a list kept for it, not a new array every frame.
		drawing.Clear();
		drawing.AddRange( All );

		foreach ( var core in drawing )
		{
			if ( !core.host.IsSurfaceVisible ) continue;

			rendered++;
			core.Render();
		}

		drawing.Clear();

		Describe( rendered );
	}

	//Reads the pixel under a screen point from a visible scene surface.
	internal static bool TrySampleColor( Vector2 screenPos, out Color color )
	{
		color = default;

		foreach ( var core in All )
		{
			if ( !core.host.IsSurfaceVisible ) continue;
			if ( !new Rect( core.host.SurfaceScreenPosition, core.host.SurfaceSize ).IsInside( screenPos ) ) continue;

			if ( core.TryReadPixel( screenPos, out color ) )
				return true;
		}

		return false;
	}

	private bool TryReadPixel( Vector2 screenPos, out Color color )
	{
		color = default;

		if ( preview is null ) return false;

		var size = host.SurfaceSize;
		var local = screenPos - host.SurfaceScreenPosition;

		if ( size.x <= 0 || size.y <= 0 ) return false;

		//No readable picture when drawn into the window, so render the world again to read the pixel
		if ( worldPanel is not null )
		{
			if ( !worldScene.IsValid() || !worldCamera.IsValid() ) return false;

			var pixels = size * host.SurfaceScale;

			using ( worldScene.Push() )
			{
				if ( !preview.DrawOwn( worldScene, worldCamera, pixels.x.CeilToInt(), pixels.y.CeilToInt() ) ) return false;
			}
		}

		var (pw, ph) = preview.PictureSize;

		if ( pw <= 0 || ph <= 0 ) return false;

		var x = (int)(local.x / size.x * pw);
		var y = (int)(local.y / size.y * ph);

		return preview.TryReadPixel( x, y, out color );
	}

	/// <summary>Draws the scene into this surface's picture, once, for this frame.</summary>
	public void Render()
	{
		var scene = host.Scene;

		SampleInput();

		if ( !scene.IsValid() || !host.IsSurfaceVisible ) return;

		drawn++;

		var camera = host.Camera.IsValid() ? host.Camera : scene.Camera;
		var size = host.SurfaceSize;

		//The picture is the display's pixels. The editor's camera stays in interface units, as the pointer and the gizmos are;
		//the game's sees the pixels it is drawn at, as it would in a window of its own
		var pixels = size * host.SurfaceScale;

		//Set the camera's screen size to this surface before the tools' frame; game view leaves it alone
		if ( camera.IsValid() )
		{
			var screen = host.EnableEngineOverlays ? pixels : size;
			var viewport = new Vector2( screen.x < 1.0f ? 1.0f : screen.x, screen.y < 1.0f ? 1.0f : screen.y );

			if ( !host.EnableEngineOverlays ) camera.CustomSize = viewport;

			if ( camera.SceneCamera is { } sceneCamera )
			{
				//Update the camera transform now so the cursor ray and handle sizes use this frame's position
				camera.UpdateSceneCameraTransform( sceneCamera );

				//After it, because it works the size out from Screen and this surface is not the screen.
				sceneCamera.Size = viewport;
			}
		}

		//Tools frame runs even with no renderer; its draws are recorded into this instance's list
		using ( Rinten.Diagnostics.Performance.Scope( "Gizmo Frame" ) )
		using ( host.GizmoInstance.RecordDraws() )
		using ( scene.Push() )
		{
			using ( host.GizmoInstance.PushInternal() )
			{
				using ( Rinten.Diagnostics.Performance.Scope( "Gizmo PreFrame" ) )
				{
					host.PreFrame();
				}
			}
		}

		SampleGizmoInput();

		if ( !camera.IsValid() || !CoreScenePreview.Available ) return;

		var width = pixels.x.CeilToInt();
		var height = pixels.y.CeilToInt();

		if ( width < 8 || height < 8 ) return;

		preview ??= new CoreScenePreview();

		preview.GameView = host.EnableEngineOverlays;
		preview.EditorOverlays = host.ShowEditorOverlays && !host.EnableEngineOverlays;

		//Where this view wants the grid. Both were the drawer's own default until now, so a flat view
		//got the ground plane on top of the one it rules itself with and nothing could turn either off.
		preview.GroundGrid = host.ShowGroundGrid;
		preview.SurfaceGrid = host.ShowSurfaceGrid;

		//This surface draws only its own gizmos; delegate cached to avoid a per-frame allocation
		preview.GizmoSource = gizmoSource ??= () => host.GizmoInstance;

		ClaimScreen( width, height );

		//Don't push the gizmo instance here: a second push breaks hover; the renderer gets it via GizmoSource

		//The game, on the whole of the window: into the window's own frame, in this panel's place, with no picture of its own in between - see
		//ISceneRenderHost.WorldSurface. A window has one place for the world; a view that finds it held by another one on screen draws a picture instead.
		var surface = host.WorldSurface;

		//Docked, the same where the window can put the world on a layer of its own: then the window's picture is not written for the view at all. Where it
		//cannot, a docked view stays a picture - drawn into the window's own frame instead it measured the same, the whole window being written either way.
		if ( surface is null && host.ViewSurface is { IsValid: true } view
			&& CoreUiWindow.Holding( view ) is { LayersWorld: true } )
			surface = view;

		if ( WorldViews && surface is { IsValid: true }
			&& CoreUiWindow.Holding( surface ) is { } shell
			&& ( shell.WorldPanel is not { IsValid: true, IsVisible: true } holder || ReferenceEquals( holder, surface ) ) )
		{
			Underlay( shell, surface, scene, camera );
			return;
		}

		ReleaseUnderlay();

		using ( scene.Push() )
		{
			//Straight to a picture on the device: the surface wears it, and nothing about a moving scene view goes through memory.
			frame = preview.RenderToTexture( scene, camera, width, height );
		}

		host.WearTexture( frame );
	}

	//Window drawn straight into while fullscreen play; -noworldview forces a texture instead
	private static readonly bool WorldViews = !Rinten.Utility.CommandLine.HasSwitch( "-noworldview" );

	private CoreUiWindow worldWindow;
	private Rinten.UI.Panel worldPanel;
	private Scene worldScene;
	private CameraComponent worldCamera;

	//Hands the window this scene to draw where the panel sits, instead of wearing a picture.
	private void Underlay( CoreUiWindow shell, Rinten.UI.Panel surface, Scene scene, CameraComponent camera )
	{
		frame = null;
		worldScene = scene;
		worldCamera = camera;

		//A different window, or a different panel in the same one: whatever wore the world before
		//has to stop wearing it, or the one that lost keeps a hole where its picture used to be.
		if ( !ReferenceEquals( worldWindow, shell ) || !ReferenceEquals( worldPanel, surface ) )
		{
			ReleaseUnderlay();

			worldWindow = shell;
			worldPanel = surface;
		}

		surface.WearsWorld = true;
		shell.WorldPanel = surface;
		shell.WorldDraw = DrawWorld;
		shell.WorldShot = ShootWorld;

		host.WearTexture( null );
	}

	//What the window calls, inside its frame, where the panels reached this one. The world as the panel shows it,
	//drawn into a picture - what a screenshot of the window puts where the panel is. See CoreUiWindow.WorldShot.
	private Bitmap ShootWorld( int width, int height )
	{
		if ( !worldScene.IsValid() || !worldCamera.IsValid() ) return null;

		var bitmap = new Bitmap( width, height );

		using ( worldScene.Push() )
		{
			worldCamera.RenderToBitmap( bitmap, false );
		}

		return bitmap;
	}

	private void DrawWorld( ulong swapchain, int width, int height )
	{
		if ( !worldScene.IsValid() || !worldCamera.IsValid() ) return;

		preview ??= new CoreScenePreview();

		using ( worldScene.Push() )
		{
			preview.DrawInto( worldScene, worldCamera, swapchain, width, height );
		}
	}

	//Gives the window back its own frame, and the panel back its picture.
	private void ReleaseUnderlay()
	{
		if ( worldPanel is { IsValid: true } ) worldPanel.WearsWorld = false;

		if ( worldWindow is not null )
		{
			if ( ReferenceEquals( worldWindow.WorldPanel, worldPanel ) )
			{
				worldWindow.WorldPanel = null;
				worldWindow.WorldDraw = null;
				worldWindow.WorldShot = null;
			}

			worldWindow = null;
		}

		worldPanel = null;
		worldScene = null;
		worldCamera = null;
	}

	//The game view claims the game's screen size; only the claimer releases it
	private void ClaimScreen( int width, int height )
	{
		if ( host.EnableEngineOverlays )
		{
			Screen.Viewport = new Vector2( width, height );

			//And where that screen is in the window, so the pointer the window reports is put on it - see InputRouter.ScreenOrigin.
			Rinten.Engine.InputRouter.ScreenOrigin = host.SurfaceScreenPosition * host.SurfaceScale;

			ClaimPointer();

			holdsScreen = true;
			return;
		}

		if ( !holdsScreen ) return;

		Screen.Viewport = null;
		Rinten.UI.PanelInput.External = null;
		Rinten.Engine.InputRouter.ScreenOrigin = null;
		Rinten.Engine.InputRouter.PointerInGameView = false;
		holdsScreen = false;
	}

	//Pointer and buttons go to the game only inside this surface; outside, nowhere rather than clamped
	private void ClaimPointer()
	{
		//With the mouse taken off the window system there is no cursor to hand anybody: it stays where it was taken, and a panel under that point
		//would be hovered - and pressed by every shot fired - for as long as play lasts. A game that hid its cursor is not pointing at anything.
		if ( !Rinten.Engine.InputRouter.MouseCursorVisible )
		{
			Rinten.UI.PanelInput.External = new Rinten.UI.PanelInput.Pointer
			{
				Position = new Vector2( -1, -1 ),
				Wheel = wheel,
			};

			//A hidden cursor is the game's wherever the pointer would have been.
			Rinten.Engine.InputRouter.PointerInGameView = Rinten.Engine.InputRouter.GameHasInput;

			wheel = 0;
			return;
		}

		//In the game's pixels, which are the window's
		var size = host.SurfaceSize * host.SurfaceScale;
		var local = Application.CursorPixels - host.SurfaceScreenPosition * host.SurfaceScale;

		var inside = local.x >= 0 && local.y >= 0 && local.x < size.x && local.y < size.y;
		var buttons = Application.MouseButtons;

		Rinten.UI.PanelInput.External = new Rinten.UI.PanelInput.Pointer
		{
			Position = inside ? local : new Vector2( -1, -1 ),
			Left = inside && buttons.HasFlag( MouseButtons.Left ),
			Right = inside && buttons.HasFlag( MouseButtons.Right ),
			Middle = inside && buttons.HasFlag( MouseButtons.Middle ),
			Wheel = wheel,
		};

		//CoreWindow.Route only passes the mouse to the game while this is set
		Rinten.Engine.InputRouter.PointerInGameView = inside;

		wheel = 0;
	}

	//Which surface the pointer is on, and whether this one ever thinks it is. Sampled every
	//frame and remembered, because asking from a console asks with the pointer over the console.

	private static float everUnderMouse = -1;
	private static float everAnyHover = -1;
	private static string lastHoveredWidget;

	//Last drawn surface kept as a reference; its description is built only when asked
	private static ISceneRenderHost lastSurface;

	private static float everGizmoHovered = -1;
	private static float everGizmoRay = -1;
	private static float everLeftMouse = -1;
	private static float everPicked = -1;
	private static float everPressed = -1;
	private static string lastPickedPath;

	/// <summary>Where the pointer went. Hovering a viewport is the first link of picking anything in it, and when it never happens nothing downstream can.</summary>
	[ConCmd( "editor_input_status" )]
	public static void InputStatus()
	{
		static string Ago( float at ) => at < 0 ? "never" : $"{RealTime.Now - at:F1}s ago";

		Log.Info( $"scene view under mouse {Ago( everUnderMouse )}, anything hovered {Ago( everAnyHover )}" );
		Log.Info( $"last hovered widget: {lastHoveredWidget ?? "none"}" );
		Log.Info( $"scene view surface: {lastSurface?.HostDescription ?? "unknown"}" );

		//The rest of the chain, read straight off the instance the tools were given - after their frame, which is where they set it.
		Log.Info( $"gizmo input: hovered {Ago( everGizmoHovered )}, ray {Ago( everGizmoRay )}, " +
			$"left mouse {Ago( everLeftMouse )}" );

		Log.Info( $"gizmo picking: something under the pointer {Ago( everPicked )}, pressed {Ago( everPressed )}" );

		if ( lastPickedPath is not null )
			Log.Info( $"last gizmo under the pointer: {lastPickedPath}" );
	}

	//What the tools made of the pointer, sampled after their frame - which is the only moment it is set.
	private void SampleGizmoInput()
	{
		if ( host.GizmoInstance is not { } instance ) return;

		var now = RealTime.Now;

		if ( instance.Input.IsHovered ) everGizmoHovered = now;
		if ( instance.Input.LeftMouse ) everLeftMouse = now;
		if ( !instance.Input.CursorRay.Forward.IsNearZeroLength ) everGizmoRay = now;

		if ( !string.IsNullOrEmpty( instance.current.HoveredPath ) )
		{
			everPicked = now;
			lastPickedPath = instance.current.HoveredPath;
		}

		if ( !string.IsNullOrEmpty( instance.current.PressedPath ) ) everPressed = now;
	}

	private void SampleInput()
	{
		var now = RealTime.Now;

		if ( host.IsUnderMouse ) everUnderMouse = now;

		if ( WidgetSurface.Hovered is { } widget )
		{
			everAnyHover = now;
			lastHoveredWidget = widget.GetType().Name;
		}

		lastSurface = host;
	}

	private static readonly bool tracing = Rinten.Utility.CommandLine.HasSwitch( "-uitrace" );
	private static RealTimeSince sinceSaidDraws;
	private static int drawn;

	//Logs scene view draw counts under -uitrace; a workspace switch draws eight views
	private static void Describe( int rendered )
	{
		if ( !tracing || sinceSaidDraws <= 2.0f ) return;

		sinceSaidDraws = 0;

		new Rinten.Diagnostics.Logger( "Ui" ).Info(
			$"scene views: {drawn} drawn of {rendered} asked, {CoreScenePreview.skippedDraws} held back, over 2s" );

		drawn = 0;
		CoreScenePreview.skippedDraws = 0;
	}
}
