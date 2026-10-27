using Rinten;
using System;

namespace Editor;

//Everything about drawing a scene is SceneRenderCore, which a panel-native viewport uses just the same. What is left here is the widget half: the mouse, the keyboard, and the picture being worn as this widget's surface texture.
/// <summary>The scene, drawn into a widget that is a panel.</summary>
public partial class SceneRenderingWidget : ISceneRenderHost, IPlaySurface, ISceneCanvas
{
	bool ISceneCanvas.IsCanvasValid => this.IsValid();
	Vector2 ISceneCanvas.CanvasSize => Size;
	Vector2 ISceneCanvas.CanvasScreenPosition => ScreenPosition;
	void ISceneCanvas.FocusCanvas() => Focus();
	void ISceneCanvas.SetCanvasCursor( CursorShape shape ) => Cursor = shape;
	void ISceneCanvas.SetCanvasCursor( Pixmap pixmap ) => PixmapCursor = pixmap;

	private SceneRenderCore core;

	//Create the render core eagerly: its constructor registers it in the list of scenes to draw
	private protected void CreateRenderCore() => core ??= new SceneRenderCore( this );

	private SceneRenderCore Core => core ??= new SceneRenderCore( this );

	//The scene, as our renderer left it on the device. The panel wears this rather than a painting of it.
	internal override Texture SurfaceTexture => core?.Frame;

	Scene ISceneRenderHost.Scene => Scene;
	CameraComponent ISceneRenderHost.Camera => Camera;
	Gizmo.Instance ISceneRenderHost.GizmoInstance => GizmoInstance;
	bool ISceneRenderHost.EnableEngineOverlays => EnableEngineOverlays;
	bool ISceneRenderHost.ShowEditorOverlays => ShowEditorOverlays;
	bool ISceneRenderHost.ShowGroundGrid => ShowGroundGrid;
	bool ISceneRenderHost.ShowSurfaceGrid => ShowSurfaceGrid;
	bool ISceneRenderHost.IsSurfaceVisible => this.IsValid() && Visible;
	Vector2 ISceneRenderHost.SurfaceSize => Size;
	Vector2 ISceneRenderHost.SurfaceScreenPosition => ScreenPosition;
	float ISceneRenderHost.SurfaceScale => DpiScale;
	bool ISceneRenderHost.IsUnderMouse => IsUnderMouse;

	string ISceneRenderHost.HostDescription => Surface is null
		? "none"
		: $"pointer {Surface.Style?.PointerEvents}, hovered {Surface.HasHovered}, visible {Visible}";

	//This widget's panel while the game plays in it fullscreen (asked of the widget chain), else null
	Rinten.UI.Panel ISceneRenderHost.ViewSurface => Surface;

	Rinten.UI.Panel ISceneRenderHost.WorldSurface
	{
		get
		{
			if ( Surface is null ) return null;
			if ( !GameMode.IsPlaySurface( this ) ) return null;

			var editor = Rinten.Internal.GlobalToolsNamespace.EditorWindow;
			if ( !editor.IsValid() ) return null;

			for ( var up = (Widget)this; up is not null; up = up.Parent )
			{
				if ( editor.IsFullscreen( up ) ) return Surface;
			}

			return null;
		}
	}

	void ISceneRenderHost.PreFrame() => PreFrame();

	//The widget's surface reads SurfaceTexture on its own tick, so there is nothing to hand it here.
	void ISceneRenderHost.WearTexture( Texture texture ) { }

	bool IPlaySurface.IsShowing => Visible;
	Rinten.UI.Panel IPlaySurface.InputSurface => Surface;
	void IPlaySurface.TakeFocus() => Focus();
	void IPlaySurface.ReleaseFocus() { if ( this.IsValid() ) Blur(); }
	void IPlaySurface.Redraw() { if ( this.IsValid() ) Update(); }

	event Action<bool> IPlaySurface.FocusChanged
	{
		add
		{
			focusChanged += value;
			HookFocusEvents();
		}
		remove => focusChanged -= value;
	}

	private Action<bool> focusChanged;
	private bool focusHooked;

	private void HookFocusEvents()
	{
		if ( focusHooked ) return;

		focusHooked = true;

		Focused += OnSurfaceFocused;
		Blurred += OnSurfaceBlurred;
	}

	private void OnSurfaceFocused( FocusChangeReason reason ) => focusChanged?.Invoke( true );
	private void OnSurfaceBlurred( FocusChangeReason reason ) => focusChanged?.Invoke( false );

	private void ReleaseManaged()
	{
		core?.Dispose();
		core = null;
	}

	//What is drawn where there is no scene on the device to wear. With one, the panel wears it and this is never asked for.
	protected override void OnPaint()
	{
		if ( !OnPanels ) { base.OnPaint(); return; }

		Paint.ClearPen();
		Paint.SetBrush( Theme.WindowBackground );
		Paint.DrawRect( LocalRect );

		Paint.SetPen( Theme.TextDisabled );
		Paint.SetDefaultFont();
		Paint.DrawText( LocalRect, "no scene", TextFlag.Center );
	}

	//Clicking the game view gives the game the mouse; Escape is the only way to get it back
	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );

		if ( !GameMode.IsPlaySurface( this ) ) return;

		GameMode.TakeInput();
	}

	//Wheel goes to the game in game view, else the camera via MouseWheelDelta (120 per notch)
	protected override void OnMouseWheel( WheelEvent e )
	{
		if ( EnableEngineOverlays )
		{
			Core.AddWheel( -e.Delta / 120.0f );
			return;
		}

		Application.TakeWheel( e.Delta / 120.0f );

		//Ours, so a scroller this happens to be inside does not also take it.
		e.Accept();
	}
}
