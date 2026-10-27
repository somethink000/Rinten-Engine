using Rinten;

namespace Editor;

/// <summary>What drawing a scene needs from whatever is showing it.</summary>
public interface ISceneRenderHost
{
	/// <summary>The scene being drawn, or nothing.</summary>
	Scene Scene { get; }

	/// <summary>The camera to draw it from, falling back to the scene's own.</summary>
	CameraComponent Camera { get; }

	/// <summary>The gizmo instance the tools on this surface talk to.</summary>
	Gizmo.Instance GizmoInstance { get; }

	/// <summary>Whether the game's own interface and overlays are drawn - the game view.</summary>
	bool EnableEngineOverlays { get; }

	/// <summary>Whether the editor's grid, icons and selection are drawn.</summary>
	bool ShowEditorOverlays { get; }

	/// <summary>Whether the grid on the world's ground plane is drawn.</summary>
	bool ShowGroundGrid { get; }

	/// <summary>Whether the grid is cut into the faces of the editable meshes.</summary>
	bool ShowSurfaceGrid { get; }

	/// <summary>Whether the surface is on screen at all.</summary>
	bool IsSurfaceVisible { get; }

	/// <summary>How big the surface is, in interface units.</summary>
	Vector2 SurfaceSize { get; }

	/// <summary>Where the surface is in its window, in interface units.</summary>
	Vector2 SurfaceScreenPosition { get; }

	/// <summary>Pixels per interface unit where the surface is shown, which is what the picture is drawn at.</summary>
	float SurfaceScale { get; }

	/// <summary>Whether the pointer is over the surface.</summary>
	bool IsUnderMouse { get; }

	/// <summary>What this surface is called when the editor talks about itself.</summary>
	string HostDescription { get; }

	/// <summary>Panel to draw the world straight into the window frame (fullscreen play only), or null for a texture</summary>
	Rinten.UI.Panel WorldSurface { get; }

	/// <summary>The panel this view is shown on, fullscreen or docked. A docked view is drawn onto a layer of its own over the window where the window has one, and stays a picture where it has not - see CoreUiWindow.LayersWorld.</summary>
	Rinten.UI.Panel ViewSurface { get; }

	/// <summary>Runs the editor's frame - tools, gizmos, everything the surface draws.</summary>
	void PreFrame();

	/// <summary>Puts the finished picture on the surface.</summary>
	void WearTexture( Texture texture );
}
