using Rinten.UI;

namespace Editor;

/// <summary>One picture of the scene, and everything that happens inside it.</summary>
public partial class SceneViewportPanel : EditorPanel, IOrientationGizmoHost
{
	public static Vector2 PointerPosition { get; private set; }

	public int ViewportIndex { get; private set; }
	public SceneViewPanel SceneView { get; private set; }
	public Gizmo.Instance GizmoInstance { get; private set; }
	public SceneOverlayPanel Overlay { get; private set; }

	/// <summary>The scene, drawn straight onto the device and worn by this panel.</summary>
	public SceneRenderingPanel Renderer { get; private set; }

	//The viewport whose tools are running their frame right now, or null
	internal static SceneViewportPanel Framing { get; private set; }

	protected virtual SceneEditorSession Session => SceneView.Session;
	private EditorToolManager Tools => SceneView.Tools;

	private readonly ViewportPointer pointer = new();

	/// <summary>Say why the viewport is or is not listening to the mouse.</summary>
	[ConVar( "tool_input_debug" )]
	public static bool InputDebug { get; set; }

	//The last line said, so a steady state is not repeated every frame.
	private string lastSaid;

	private void SayInput()
	{
		if ( !InputDebug )
		{
			lastSaid = null;
			return;
		}

		var line = $"modal {Window.ModalBlocksInput}, activeWindow {IsActiveWindow}, "
			+ $"underMouse {Renderer.IsUnderMouse}, hasMouseInput {pointer.HasMouseInput}, "
			+ $"gameHasInput {GameMode.HasInput}, buttons {Application.MouseButtons}, "
			+ $"gizmoHovered {GizmoInstance?.Input.IsHovered}, "
			+ $"tool {Tools?.CurrentTool?.GetType().Name ?? "none"}"
			+ $"/{Tools?.CurrentSubTool?.GetType().Name ?? "none"}";

		if ( line == lastSaid ) return;

		lastSaid = line;

		Log.Info( $"[viewinput] {line}" );
	}

	private ViewportCamera camera;

	//The view's camera, for a measured run to put into a flat view - see PerfCamera.
	internal ViewportCamera ViewCamera => camera;
	private OrientationGizmo orientation;
	private FlatViewPainter flatView;
	private SceneDecorations decorations;

	private CameraComponent editorCamera;

	internal RealTimeSince timeSinceCameraSpeedChange = 99;

	/// <summary>The camera this viewport is looking through right now.</summary>
	public CameraComponent ActiveCamera => camera?.Active;

	//If assigned, copy FoV / ZNear / ZFar etc from this camera - see ViewportCamera.SourceCamera.
	internal CameraComponent SourceCamera
	{
		get => camera?.SourceCamera;
		set
		{
			if ( camera is not null )
				camera.SourceCamera = value;
		}
	}

	CameraComponent IOrientationGizmoHost.Camera => ActiveCamera;
	Vector2 IOrientationGizmoHost.SurfaceSize => SurfaceSize;
	Vector2 IOrientationGizmoHost.MousePosition => PointerPosition;
	bool IOrientationGizmoHost.IsActiveWindow => IsActiveWindow;
	void IOrientationGizmoHost.SetCursor( CursorShape shape ) => Renderer.Cursor = shape;
	void IOrientationGizmoHost.FocusSurface() => Renderer.Focus();

	//How big the picture is, which is what the camera is sized against.
	private Vector2 SurfaceSize => Renderer.IsValid() ? Renderer.ScreenSize : ScreenSize;

	public SceneViewportPanel( SceneViewPanel sceneView, int id )
	{
		SceneView = sceneView;
		ViewportIndex = id;

		AddClass( "scene-viewport" );

		Session.OnFrameTo += FrameOn;

		if ( ViewportIndex == 0 )
		{
			SceneView.LastSelectedViewport = this;
		}

		if ( ProjectCookie.Get<ViewportState>( $"SceneView.Viewport{ViewportIndex}.Settings.{CookieVersion}", null ) is ViewportState savedSettings )
		{
			State = savedSettings;
		}
		else
		{
			State = new ViewportState();
			State.View = (ViewportViewMode)(ViewportIndex % typeof( ViewportViewMode ).GetFields().Length);
		}

		camera = new ViewportCamera( State, () => SurfaceSize );
		flatView = new FlatViewPainter( State, () => ActiveCamera, () => SurfaceSize );
		decorations = new SceneDecorations( State, () => SurfaceSize );
		orientation = new OrientationGizmo( this, camera, State );

		InitializeCamera();

		//The panel no longer renders the scene view - our core does, through the panel's own background. The
		//panel stays for what it still carries: the editor camera and the gizmo instance the tools talk to.
		Renderer = new SceneRenderingPanel
		{
			ShowEditorOverlays = true,
			Scene = Session.Scene,
		};

		AddChild( Renderer );

		editorCamera = Renderer.CreateSceneEditorCamera();
		camera.Active = editorCamera;

		Renderer.OnPreFrame += OnEditorPreFrame;
		Renderer.Camera = editorCamera;

		GizmoInstance = Renderer.GizmoInstance;
		GizmoInstance.Settings = EditorScene.GizmoSettings;
		GizmoInstance.Selection = Session.Selection;

		Overlay = new SceneOverlayPanel();
		AddChild( Overlay );

		//What the editor is costing, down the right of the picture under the orientation
		//gizmo. Hidden until Settings says otherwise, which it says to every view at once.
		Overlay.Side.AddChild( new SceneStatsPanel() );
	}

	public override void OnDeleted()
	{
		base.OnDeleted();

		Session.OnFrameTo -= FrameOn;

		if ( camera?.Active.IsValid() ?? false )
		{
			camera.Active.GameObject?.Destroy();
			camera.Active = null;
		}
	}

	protected override void OnDoubleClick( MousePanelEvent e )
	{
		base.OnDoubleClick( e );

		pointer.NoteDoubleClick( e.LocalPosition );
	}

	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );

		if ( SceneView.CurrentView == SceneViewMode.Game )
		{
			Renderer.Focus();
			return;
		}

		SceneView.TabSession.MakeActive();

		if ( e.MouseButton == MouseButtons.Right )
		{
			pointer.PressPosition = e.LocalPosition;
			pointer.PressCameraPosition = camera.TargetPosition ?? ActiveCamera.WorldPosition;
		}
	}

	protected override void OnMouseUp( MousePanelEvent e )
	{
		base.OnMouseUp( e );

		if ( SceneView.CurrentView == SceneViewMode.Game )
		{
			Renderer.Focus();
			return;
		}

		var tool = Tools?.CurrentTool;
		if ( tool is not null && !tool.AllowContextMenu )
			return;

		//A right click opens the context menu only within a 6 pixel deadzone
		if ( Application.KeyboardModifiers != KeyboardModifiers.None ) return;
		if ( e.MouseButton != MouseButtons.Right ) return;
		if ( Vector2.DistanceBetween( pointer.PressPosition, e.LocalPosition ) >= 6 ) return;

		var restingAt = camera.TargetPosition ?? GizmoInstance.GetValue<Vector3?>( "CameraTarget" ) ?? ActiveCamera.WorldPosition;
		if ( pointer.PressCameraPosition != restingAt ) return;

		OpenContextMenu();
	}

	private Ray ScreenPixelToRay( Vector2 localPixelPosition )
	{
		if ( !ActiveCamera.IsValid() )
			return default;

		return ActiveCamera.ScreenPixelToRay( localPixelPosition );
	}

	private SceneTraceResult? GetCursorTracePosition( Ray ray )
	{
		var tr = Session.Scene.Trace.Ray( ray, Gizmo.RayDepth )
			.UseRenderMeshes( true )
			.UsePhysicsWorld( false )
			.Run();

		if ( tr.Hit ) return tr;

		var plane = new Plane( Vector3.Up, 0.0f );
		if ( plane.TryTrace( ray, out var point, true, Gizmo.RayDepth ) )
		{
			tr = default;
			tr.Hit = true;
			tr.Normal = plane.Normal;
			tr.EndPosition = point;
			tr.HitPosition = point;
			return tr;
		}

		return null;
	}

	public bool TryGetCursorTracePosition( out SceneTraceResult tr )
	{
		if ( GetCursorTracePosition( Gizmo.CurrentRay ) is { } result )
		{
			tr = result;
			return true;
		}

		tr = default;
		return false;
	}

	[Shortcut( "editor.paste", "CTRL+V" )]
	private void Paste()
	{
		using ( GizmoInstance.Push() )
		{
			if ( EditorPreferences.PasteAtCursor && TryGetCursorTracePosition( out var tr ) )
			{
				EditorScene.PasteAt( tr );
			}
			else
			{
				EditorScene.Paste();
			}
		}
	}

	private void PasteAtCursor()
	{
		using ( GizmoInstance.Push() )
		{
			if ( GetCursorTracePosition( ScreenPixelToRay( pointer.PressPosition ) ) is { } trace )
			{
				EditorScene.PasteAt( trace );
			}
		}
	}

	//The editor's own frame for this viewport: the camera, the tools, and everything drawn over the scene.
	private void OnEditorPreFrame()
	{
		//don't do editor update if we're the play view
		if ( IsGameView )
		{
			ApplyViewToGame();
			return;
		}

		using ( Rinten.Diagnostics.Performance.Scope( "view input" ) )
		{
			pointer.Update( Window.ModalBlocksInput, IsActiveWindow, Renderer.IsUnderMouse );
		}

		SayInput();

		var isActive = Session == SceneEditorSession.Active;

		using ( Rinten.Diagnostics.Performance.Scope( "view camera" ) )
		{
			EnsureCamera();
			camera.Update();
			decorations.DressPrefabScene( Session.Scene );
		}

		//Inactive view not under the cursor: skip input but still draw, flat views draw here
		if ( !isActive && !pointer.HasMouseInput )
		{
			GizmoInstance.Input.IsHovered = false;
			Renderer.UpdateGizmoInputs( false );
			DrawScene();
			return;
		}

		PointerPosition = Application.CursorPosition - Renderer.ScreenPosition;

		//Play sounds from the point of view of the scene camera if we're not playing the game, and this window is focused
		if ( pointer.HasMouseInput )
		{
			Sound.Listener = ActiveCamera.WorldTransform;
		}

		var hasMouseFocus = pointer.HasMouseInput;
		if ( hasMouseFocus || HasFocus || Renderer.HasFocus )
		{
			SceneView.LastSelectedViewport = this;
		}

		GizmoInstance.Input.IsHovered = hasMouseFocus;

		//Orientation gizmo gets priority on the mouse. When it's interacting we suppress clicking anything in the scene
		bool gizmoInputUsed;

		using ( Rinten.Diagnostics.Performance.Scope( "view orientation" ) )
		{
			gizmoInputUsed = orientation.Update( hasMouseFocus );
		}

		if ( gizmoInputUsed )
			GizmoInstance.Input.IsHovered = false;

		UpdateCameraInput( gizmoInputUsed );

		if ( GizmoInstance.Input.IsHovered && hasMouseFocus )
			GizmoInstance.Input.DoubleClick = pointer.DoubleClick;

		pointer.DoubleClick = false;

		using ( Rinten.Diagnostics.Performance.Scope( "view gizmo inputs" ) )
		{
			Renderer.UpdateGizmoInputs( GizmoInstance.Input.IsHovered && hasMouseFocus );
		}

		DrawScene();
	}

	//The camera answers the wheel and the drag, unless a tool or the orientation gizmo has taken them.
	private void UpdateCameraInput( bool gizmoInputUsed )
	{
		//Wheel zooms any orthographic view, including 3D views snapped to an axis
		if ( camera.WantOrtho && Renderer.IsUnderMouse && !gizmoInputUsed && ActiveCamera.IsValid() )
		{
			if ( SceneEditorExtensions.ZoomOrthographic( ActiveCamera, Renderer ) )
			{
				camera.ClearTarget();
				State.CameraPosition = ActiveCamera.WorldPosition;

				//Straight onto the height being drawn, not lerped toward it: the transition blend is for going between flat
				//and perspective, and running a zoom through it turns one notch of the wheel into half a second of drift.
				camera.TakeOrthoHeight( ActiveCamera.OrthographicHeight );
			}
		}

		if ( IsActiveWindow ) //don't update camera input if the editor window isn't active
		{
			//How fast the camera flies is asked before anything below can block the camera, because
			//holding shift blocks it and holding shift is when you want to change the speed.
			if ( GizmoInstance.AdjustCameraSpeed( ActiveCamera ) )
				timeSinceCameraSpeedChange = 0;

			pointer.UpdateCameraBlocks( GizmoInstance.Input.IsHovered, GizmoInstance.IsHandleHovered, PointerPosition );

			bool shouldBlockOrbit = pointer.BlockCamera
				|| pointer.HandleHasPress
				|| (pointer.BlockForToolInput && GizmoInstance.Input.IsHovered)
				|| gizmoInputUsed
				|| Gizmo.Pressed.Any;

			ActiveCamera.OrthographicHeight = State.CameraOrthoHeight;

			if ( !shouldBlockOrbit )
			{
				var orbitDistance = camera.OrbitDistance;

				if ( GizmoInstance.OrbitCamera( ActiveCamera, Renderer, ref orbitDistance ) )
				{
					camera.OrbitDistance = orbitDistance;
					camera.ClearTarget();
					GizmoInstance.Input.IsHovered = false;
				}
				else if ( GizmoInstance.FirstPersonCamera( ActiveCamera, Renderer, State.View == ViewportViewMode.Perspective ) )
				{
					camera.ClearTarget();
					GizmoInstance.Input.IsHovered = false;
				}
			}
			else if ( pointer.BlockCamera )
			{
				Renderer.Cursor = CursorShape.None;
			}

			State.CameraPosition = ActiveCamera.WorldPosition;
			State.CameraRotation = ActiveCamera.WorldRotation;
			State.CameraOrthoHeight = ActiveCamera.OrthographicHeight;
		}

		camera.ApplyViewOffset();
	}

	//The call handed to every scene-view listener, made once - see where it is used.
	private System.Action<EditorEvent.ISceneView> drawSceneViewGizmos;

	//Everything the editor puts over the scene, in the order it goes on.
	private void DrawScene()
	{
		//Grid per view: perspective gets the ground grid, flat views (any ortho) draw their own plane
		var flat = camera.WantOrtho;

		Renderer.ShowGroundGrid = State.ShowGrid && !flat;
		Renderer.ShowSurfaceGrid = State.ShowGrid;

		if ( State.ShowGrid && flat )
		{
			using ( Rinten.Diagnostics.Performance.Scope( "view grid" ) )
			using ( Gizmo.Scope( "grid" ) )
			{
				flatView.DrawGrid();
			}
		}

		if ( State.Is2D )
		{
			using ( Rinten.Diagnostics.Performance.Scope( "view flat meshes" ) )
			using ( Gizmo.Scope( "flat-meshes" ) )
			{
				flatView.DrawMeshes( Session.Scene );
			}
		}

		if ( GizmoInstance.Input.IsHovered )
		{
			using ( Rinten.Diagnostics.Performance.Scope( "view hovered" ) )
			{
				decorations.DrawHovered( Session );
			}
		}

		using ( Rinten.Diagnostics.Performance.Scope( "view tools" ) )
		{
			Framing = this;

			try
			{
				Tools.Frame( ActiveCamera, Session, GizmoInstance.Input.IsHovered );
			}
			finally
			{
				Framing = null;
			}
		}

		using ( Rinten.Diagnostics.Performance.Scope( "view event gizmos" ) )
		{
			//Held, not written out fresh: this captures the panel, so writing it here is an allocation once a frame for every viewport open.
			EditorEvent.RunInterface( drawSceneViewGizmos ??= x => x.DrawGizmos( Session.Scene ) );
		}

		Session.Scene.EditorDraw();

		using ( Rinten.Diagnostics.Performance.Scope( "view selection" ) )
		{
			decorations.DrawSelection( Session );
		}

		using ( Rinten.Diagnostics.Performance.Scope( "view overlays" ) )
		{
			UpdateDragDrops();

			decorations.DrawCameraSpeed( timeSinceCameraSpeedChange );
			orientation.Draw();
		}
	}

	//Puts an editor camera back where one has been lost - the scene changed underneath it, or it was never made because there was no scene yet.
	private void EnsureCamera()
	{
		if ( ActiveCamera.IsValid() ) return;

		if ( editorCamera.IsValid() && editorCamera.Scene == Session.Scene )
		{
			camera.Active = editorCamera;
		}
		else
		{
			editorCamera = Renderer.CreateSceneEditorCamera();
			camera.Active = editorCamera;
		}

		Renderer.Camera = camera.Active;
	}

	//Points this viewport at a box, if it is the one being worked in.
	private void FrameOn( BBox target )
	{
		//The game view has no editor camera to frame with, and there is nothing to switch it into any more - framing belongs to the scene tab, which has one.
		if ( !ActiveCamera.IsValid() )
			return;

		//Only frame in the active viewport for this scene view.
		if ( SceneView.LastSelectedViewport is { } selected && selected != this )
			return;

		camera.FrameOn( target );
	}
}
