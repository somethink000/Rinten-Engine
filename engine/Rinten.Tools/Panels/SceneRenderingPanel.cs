using Rinten;
using Rinten.UI;
using System;

namespace Editor;

//By SceneRenderCore; surrounding UI is markup and flexbox
/// <summary>A scene drawn into a panel.</summary>
public class SceneRenderingPanel : EditorPanel, ISceneRenderHost, IPlaySurface, ISceneCanvas
{
	bool ISceneCanvas.IsCanvasValid => IsValid;
	Vector2 ISceneCanvas.CanvasSize => ScreenSize;
	Vector2 ISceneCanvas.CanvasScreenPosition => ScreenPosition;
	void ISceneCanvas.FocusCanvas() => Focus();
	void ISceneCanvas.SetCanvasCursor( CursorShape shape ) => Cursor = shape;
	void ISceneCanvas.SetCanvasCursor( Pixmap pixmap ) { if ( HostWidget is { } host && host.IsValid() ) host.PixmapCursor = pixmap; }

	//Not the one a panel belongs to, which is what Panel.Scene answers - this panel belongs to the editor's window and draws a scene it is otherwise nothing to do with.
	/// <summary>The active scene that we're rendering.</summary>
	public new Scene Scene { get; set; }

	/// <summary>The camera to render from. We fall back to Scene.Camera if this is null.</summary>
	public CameraComponent Camera { get; set; }

	/// <summary>This panel manages its own gizmo instance.</summary>
	public Gizmo.Instance GizmoInstance { get; private set; } = new();

	/// <summary>True when a handle was under the cursor last frame, or is held now.</summary>
	public bool GizmoHasPointer =>
		!string.IsNullOrEmpty( GizmoInstance?.builder.HoveredPath )
		|| !string.IsNullOrEmpty( GizmoInstance?.current.HoveredPath )
		|| !string.IsNullOrEmpty( GizmoInstance?.current.PressedPath );

	/// <summary>Whether the game's own interface and overlays are drawn - the game view.</summary>
	public bool EnableEngineOverlays { get; set; }

	/// <summary>Editor grid, light/camera icons, and selection. The scene view turns this on.</summary>
	public bool ShowEditorOverlays { get; set; }

	/// <summary>Whether the grid on the ground is drawn. A 2D view turns it off and draws its own.</summary>
	public bool ShowGroundGrid { get; set; } = true;

	/// <summary>Whether the grid is cut into the faces of the editable meshes.</summary>
	public bool ShowSurfaceGrid { get; set; } = true;

	public event Action OnPreFrame;

	private readonly SceneRenderCore core;

	private Texture worn;

	public SceneRenderingPanel()
	{
		AddClass( "scene-rendering" );

		//Clicking it is how a tool is used and how the game is asked for the mouse, so it takes the pointer and the keyboard.
		Style.PointerEvents = PointerEvents.All;
		AcceptsFocus = true;

		core = new SceneRenderCore( this );
	}

	public override void OnDeleted()
	{
		base.OnDeleted();

		//The panel the game was played in is going away, and the mouse with it - a cursor
		//left hidden in a surface that no longer exists is one nothing gives back.
		if ( GameMode.IsPlaySurface( this ) ) GameMode.ClearPlayMode();

		core.Dispose();

		GizmoInstance?.Dispose();
		GizmoInstance = default;
	}

	/// <summary>Create a hidden scene editor camera, post processing will be copied from a main camera in the scene.</summary>
	public CameraComponent CreateSceneEditorCamera() => SceneSurface.CreateEditorCamera( Scene );

	//Called just before rendering.
	protected virtual void PreFrame() => OnPreFrame?.Invoke();

	/// <summary>Update common inputs for gizmo.</summary>
	public void UpdateGizmoInputs( bool hasMouseFocus = true )
	{
		var camera = SceneSurface.SceneCameraFor( Scene, Camera );
		if ( camera is null ) return;

		SceneSurface.UpdateGizmoInputs( ref GizmoInstance.Input, camera, hasMouseFocus, ScreenPosition, ScreenSize );
	}

	/// <summary>Return a ray for the current cursor position.</summary>
	public Ray CursorRay => GetRay( Application.CursorPosition - ScreenPosition );

	/// <summary>Given a local panel position, return a Ray.</summary>
	public Ray GetRay( Vector2 localPosition )
	{
		var camera = SceneSurface.SceneCameraFor( Scene, Camera );
		if ( camera is null )
			return default;

		return camera.GetRay( localPosition, ScreenSize );
	}

	/// <summary>The pointer's shape while it is over the scene, said on the widget that owns the window's cursor.</summary>
	public CursorShape Cursor
	{
		get => HostWidget is { } host && host.IsValid() ? host.Cursor : CursorShape.None;
		set { if ( HostWidget is { } host && host.IsValid() ) host.Cursor = value; }
	}

	Scene ISceneRenderHost.Scene => Scene;
	CameraComponent ISceneRenderHost.Camera => Camera;
	Gizmo.Instance ISceneRenderHost.GizmoInstance => GizmoInstance;
	bool ISceneRenderHost.EnableEngineOverlays => EnableEngineOverlays;
	bool ISceneRenderHost.ShowEditorOverlays => ShowEditorOverlays;
	bool ISceneRenderHost.ShowGroundGrid => ShowGroundGrid;
	bool ISceneRenderHost.ShowSurfaceGrid => ShowSurfaceGrid;
	bool ISceneRenderHost.IsSurfaceVisible => IsValid && IsVisible;
	Vector2 ISceneRenderHost.SurfaceSize => ScreenSize;
	Vector2 ISceneRenderHost.SurfaceScreenPosition => ScreenPosition;
	float ISceneRenderHost.SurfaceScale => ScaleToScreen;
	bool ISceneRenderHost.IsUnderMouse => IsUnderMouse;

	string ISceneRenderHost.HostDescription =>
		$"panel pointer {Style?.PointerEvents}, hovered {HasHovered}, visible {IsVisible}";

	//This panel while the game plays fullscreen (asked of the widget chain), so the world skips a texture
	Rinten.UI.Panel ISceneRenderHost.ViewSurface => this;

	Rinten.UI.Panel ISceneRenderHost.WorldSurface
	{
		get
		{
			if ( !GameMode.IsPlaySurface( this ) ) return null;

			var editor = Rinten.Internal.GlobalToolsNamespace.EditorWindow;
			if ( !editor.IsValid() ) return null;

			for ( var up = HostWidget; up is not null; up = up.Parent )
			{
				if ( editor.IsFullscreen( up ) ) return this;
			}

			return null;
		}
	}

	void ISceneRenderHost.PreFrame() => PreFrame();

	//The scene, straight off the device. Written only when it changes: setting a style
	//marks the panel dirty whether or not the value is new, and this runs every frame.
	void ISceneRenderHost.WearTexture( Texture texture )
	{
		if ( ReferenceEquals( worn, texture ) ) return;

		worn = texture;

		Style.BackgroundImage = texture;
		Style.BackgroundSizeX = Length.Percent( 100 );
		Style.BackgroundSizeY = Length.Percent( 100 );
	}

	bool IPlaySurface.IsShowing => IsVisible;
	Panel IPlaySurface.InputSurface => this;
	void IPlaySurface.TakeFocus() => Focus();
	void IPlaySurface.ReleaseFocus() { if ( IsValid ) Blur(); }
	void IPlaySurface.Redraw() { if ( IsValid ) StateHasChanged(); }

	public event Action<bool> FocusChanged;

	protected override void OnFocus( PanelEvent e )
	{
		base.OnFocus( e );

		FocusChanged?.Invoke( true );
	}

	protected override void OnBlur( PanelEvent e )
	{
		base.OnBlur( e );

		FocusChanged?.Invoke( false );
	}

	//Clicking the game view is how the player asks for the mouse.
	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );

		Focus();

		if ( !GameMode.IsPlaySurface( this ) ) return;

		GameMode.TakeInput();
	}

	/// <summary>Wheel goes to the game in game view, else the camera via MouseWheelDelta, as notches</summary>
	public override void OnMouseWheel( Vector2 value )
	{
		var notches = -value.y;

		if ( EnableEngineOverlays )
		{
			core.AddWheel( -notches );
			return;
		}

		Application.TakeWheel( notches );
	}
}
