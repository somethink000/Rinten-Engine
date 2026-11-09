using System;

namespace Editor;

public static class EditorPreferences
{
	public static bool NotificationPopups
	{
		get => EditorCookie.Get<bool>( "NotificationPopups", true );
		set => EditorCookie.Set( "NotificationPopups", value );
	}

	public static bool NotificationSounds
	{
		get => EditorCookie.Get<bool>( "NotificationSounds", true );
		set => EditorCookie.Set( "NotificationSounds", value );
	}

	public static bool ClearConsoleOnPlay
	{
		get => EditorCookie.Get<bool>( "ClearConsoleOnPlay", false );
		set => EditorCookie.Set( "ClearConsoleOnPlay", value );
	}

	public static bool FullScreenOnPlay
	{
		get => EditorCookie.Get<bool>( "FullScreenOnPlay", false );
		set => EditorCookie.Set( "FullScreenOnPlay", value );
	}

	[Description( "Use a much faster way of loading code changes if you only change method bodies. Will currently break stack traces of hotloaded methods." )]
	public static bool FastHotload
	{
		get => bool.TryParse( ConsoleSystem.GetValue( "hotload_fast" ), out var val ) ? val : true;
		set => ConVarSystem.SetValue( "hotload_fast", value.ToString(), true );
	}

	/// <summary>Whether a model dropped into the scene comes with its collision - a collider, or physics for a jointed one. There is no Prop component here; the collision is what makes a dropped model one - see ModelDropObject.Build.</summary>
	[Description( "Automatically give models placed in the scene their collision, as a prop. Works only if the model has a valid collider." )]
	public static bool PlaceModelsAsProp
	{
		get => EditorCookie.Get( "PlaceModelsAsProp", true );
		set => EditorCookie.Set( "PlaceModelsAsProp", value );
	}

	/// <summary>Serve the Model Context Protocol from inside the editor, letting AI agents like Claude Code read and drive the open project. Only ever reachable from this machine.</summary>
	public static bool McpServerEnabled
	{
		get => EditorCookie.Get<bool>( "McpServerEnabled", true );
		set
		{
			EditorCookie.Set( "McpServerEnabled", value );
			Mcp.McpServer.Restart();
		}
	}

	/// <summary>The local port the MCP server listens on.</summary>
	public static int McpServerPort
	{
		get => EditorCookie.Get<int>( "McpServerPort", 7269 );
		set
		{
			EditorCookie.Set( "McpServerPort", value );
			Mcp.McpServer.Restart();
		}
	}

	public enum NotificationLevel
	{
		ShowAlways,
		ShowOnError,
		NeverShow
	}

	public static NotificationLevel CompileNotifications
	{
		get => EditorCookie.Get( "CompileNotifications", NotificationLevel.ShowAlways );
		set => EditorCookie.Set( "CompileNotifications", value );
	}

	/// <summary>The amount of seconds to keep a notification open if it's an error</summary>
	[Title( "Error Timeout" )]
	public static float ErrorNotificationTimeout
	{
		get => EditorCookie.Get( "ErrorNotificationTimeout", 30.0f );
		set => EditorCookie.Set( "ErrorNotificationTimeout", value );
	}


	/// <summary>Camera field of view</summary>
	[Title( "Field Of View" )]
	[Range( 1.0f, 180.0f )]
	public static float CameraFieldOfView
	{
		get => EditorCookie.Get( "SceneView.CameraFOV", 80.0f );
		set => EditorCookie.Set( "SceneView.CameraFOV", value );
	}

	//Linear, as the world is shaded: ACES at exposure 1 brings it to #1b1d20 on screen, the panels' own dark
	static readonly Color DefaultCameraBackground = new( 0.0206f, 0.0221f, 0.0246f );

	/// <summary>What every editor scene view shows where its scene has no sky of its own.</summary>
	[Title( "Background Color" )]
	public static Color CameraBackgroundColor
	{
		get => EditorCookie.Get( "SceneView.CameraBgColor", DefaultCameraBackground );
		set => EditorCookie.Set( "SceneView.CameraBgColor", value );
	}

	/// <summary>The closest thing to render, in metres.</summary>
	[Title( "ZNear" )]
	[Range( 0.001f, 10.0f )]
	public static float CameraZNear
	{
		get => EditorCookie.Get( "SceneView.CameraZNear.m", 0.025f );
		set => EditorCookie.Set( "SceneView.CameraZNear.m", value );
	}

	/// <summary>The furthest thing to render, in metres.</summary>
	[Title( "ZFar" )]
	[Range( 10.0f, 20000.0f )]
	public static float CameraZFar
	{
		get => EditorCookie.Get( "SceneView.CameraZFar.m", 5000.0f );
		set => EditorCookie.Set( "SceneView.CameraZFar.m", value );
	}

	/// <summary>How fast should the camera move, in metres a second.</summary>
	[Title( "Movement Speed" )]
	[Range( 0.1f, 100.0f )]
	public static float CameraSpeed
	{
		get => EditorCookie.Get( "SceneView.CameraSpeed.m", 2.5f );
		set => EditorCookie.Set( "SceneView.CameraSpeed.m", value );
	}

	[Title( "Sensitivity" )]
	[Range( 0.01f, 5.0f )]
	public static float CameraSensitivity
	{
		get => EditorCookie.Get( "SceneView.CameraSensitivity", 1.0f );
		set => EditorCookie.Set( "SceneView.CameraSensitivity", value );
	}

	[Title( "Create Objects at Origin" )]
	public static bool CreateObjectsAtOrigin
	{
		get => EditorCookie.Get( "SceneView.CreateObjectsAtOrigin", false );
		set => EditorCookie.Set( "SceneView.CreateObjectsAtOrigin", value );
	}

	/// <summary>Should the orbit camera zoom be inverted? <list type="bullet"> <item>Inverted: mouse up/left zooms in, mouse down/right zooms out</item> <item>Standard: mouse down/right zooms in, mouse up/left zooms out</item> </list></summary>
	[Title( "Invert Orbit Zoom" )]
	public static bool InvertOrbitZoom
	{
		get => EditorCookie.Get( "SceneView.InvertOrbitZoom", false );
		set => EditorCookie.Set( "SceneView.InvertOrbitZoom", value );
	}

	/// <summary>How fast should the orbit camera zoom? Metres of dolly per tenth of a pixel dragged, so the default walks a quarter of a metre per hundred pixels.</summary>
	[Title( "Orbit Zoom Speed" )]
	[Range( 0.0f, 1.0f )]
	public static float OrbitZoomSpeed
	{
		get => EditorCookie.Get( "SceneView.OrbitZoomSpeed.m", 0.025f );
		set => EditorCookie.Set( "SceneView.OrbitZoomSpeed.m", value );
	}

	/// <summary>Should the camera panning be inverted?</summary>
	[Title( "Invert Pan" )]
	public static bool CameraInvertPan
	{
		get => EditorCookie.Get( "SceneView.CameraInvertPan", false );
		set => EditorCookie.Set( "SceneView.CameraInvertPan", value );
	}

	/// <summary>Should we hide the eye cursor when rotating the scene camera?</summary>
	[Title( "Hide Cursor on Rotate" )]
	public static bool HideRotateCursor
	{
		get => EditorCookie.Get( "SceneView.HideRotateCursor", false );
		set => EditorCookie.Set( "SceneView.HideRotateCursor", value );
	}

	/// <summary>Should we hide the eye cursor when panning scene camera?</summary>
	[Title( "Hide Cursor on Pan" )]
	public static bool HidePanCursor
	{
		get => EditorCookie.Get( "SceneView.HidePanCursor", false );
		set => EditorCookie.Set( "SceneView.HidePanCursor", value );
	}

	/// <summary>Should we hide the eye cursor when orbiting scene camera?</summary>
	[Title( "Hide Cursor on Orbit" )]
	public static bool HideOrbitCursor
	{
		get => EditorCookie.Get( "SceneView.HideOrbitCursor", false );
		set => EditorCookie.Set( "SceneView.HideOrbitCursor", value );
	}

	/// <summary>Use bounds when dragging in objects</summary>
	[Title( "Place Using Bounds" )]
	public static bool BoundsPlacement
	{
		get => EditorCookie.Get( "SceneView.BoundsPlacement", true );
		set => EditorCookie.Set( "SceneView.BoundsPlacement", value );
	}

	/// <summary>When enabled, pasted or duplicated objects are placed under the cursor and aligned to the hit surface.</summary>
	[Title( "Paste At Cursor" )]
	public static bool PasteAtCursor
	{
		get => EditorCookie.Get( "SceneView.PasteAtCursor", true );
		set => EditorCookie.Set( "SceneView.PasteAtCursor", value );
	}

	/// <summary>When enabled, component gizmo handles are drawn at a fixed world size instead of maintaining a constant screen size regardless of distance.</summary>
	[Title( "World Space Gizmos" )]
	public static bool WorldSpaceGizmos
	{
		get => EditorScene.GizmoSettings.WorldSpaceGizmos;
		set => EditorScene.GizmoSettings.WorldSpaceGizmos = value;
	}

	/// <summary>When enabled, component gizmo handles are depth tested against scene geometry.</summary>
	[Title( "Gizmo Depth Test" )]
	public static bool GizmoDepthTest
	{
		get => EditorScene.GizmoSettings.GizmoDepthTest;
		set => EditorScene.GizmoSettings.GizmoDepthTest = value;
	}

	/// <summary>How big to show component gizmo handles.</summary>
	[Title( "Gizmo Scale" ), Range( 0.1f, 2f )]
	public static float GizmoScale
	{
		get => EditorScene.GizmoSettings.GizmoScale;
		set => EditorScene.GizmoSettings.GizmoScale = value;
	}

	/// <summary>Maximum distance in metres at which component gizmo handles are visible. 0 for unlimited.</summary>
	[Title( "Gizmo Render Distance" ), Range( 0, 1000, slider: false ), Step( 5 )]
	public static float GizmoRenderDistance
	{
		get => EditorScene.GizmoSettings.GizmoRenderDistance;
		set => EditorScene.GizmoSettings.GizmoRenderDistance = value;
	}

	/// <summary>Controls whether a sound is played for any undo/redo operation (success or failure)</summary>
	[Title( "Undo/Redo Sounds" )]
	public static bool UndoSounds
	{
		get => EditorCookie.Get( "UndoSounds", true );
		set => EditorCookie.Set( "UndoSounds", value );
	}

	/// <summary>Overrides for any Editor shortcuts.</summary>
	public static Dictionary<string, string> ShortcutOverrides
	{
		get
		{
			if ( _shortcutOverrides is null )
			{
				var json = EditorCookie.GetString( "KeybindOverrides", null );
				if ( string.IsNullOrEmpty( json ) )
					_shortcutOverrides = new Dictionary<string, string>();
				else
				{
					_shortcutOverrides = Json.Deserialize<Dictionary<string, string>>( json );
					if ( _shortcutOverrides is null )
						_shortcutOverrides = new Dictionary<string, string>();
				}
			}
			return _shortcutOverrides;
		}
		set
		{
			_shortcutOverrides = value;
			EditorCookie.SetString( "KeybindOverrides", Json.Serialize( value ) );
		}
	}

	static Dictionary<string, string> _shortcutOverrides;

	/// <summary>Whether new game instances spawned by the editor are in windowed mode.</summary>
	[Title( "Windowed Local Instances" )]
	[Description( "Whether new game instances spawned by the editor are in windowed mode." )]
	public static bool WindowedLocalInstances
	{
		get => ProjectCookie.Get( "NewInstance.Windowed", true );
		set => ProjectCookie.Set( "NewInstance.Windowed", value );
	}

	/// <summary>Command-line arguments for new game instances spawned by the editor.</summary>
	[Title( "Command Line Args" )]
	[Description( "Command-line arguments for new game instances spawned by the editor." )]
	public static string NewInstanceCommandLineArgs
	{
		get => ProjectCookie.GetString( "NewInstance.Args" );
		set => ProjectCookie.SetString( "NewInstance.Args", value );
	}

	/// <summary>Command-line arguments for new game instances spawned by the editor.</summary>
	[Title( "Server Command Line Args" )]
	[Description( "Command-line arguments for the dedicated server spawned by the editor." )]
	public static string DedicatedServerCommandLineArgs
	{
		get => ProjectCookie.GetString( "DedicatedServerInstance.Args" );
		set => ProjectCookie.SetString( "DedicatedServerInstance.Args", value );
	}

	/// <summary>Whether SuspendUpdates paints the whole rebuilt subtree before showing it.</summary>
	[Title( "SuspendUpdates Burst Paint" )]
	[Description( "When enabled, rebuild scopes paint all new widgets synchronously on dispose instead of spreading across frames." )]
	public static bool UiSuspendBurstPaintEnabled
	{
		get => EditorCookie.Get( "EditorUi.SuspendBurstPaintEnabled", true );
		set => EditorCookie.Set( "EditorUi.SuspendBurstPaintEnabled", value );
	}

	/// <summary>Milliseconds of painting allowed during one SuspendUpdates dispose; zero is unlimited.</summary>
	[Title( "SuspendUpdates Paint Budget (ms)" )]
	[Description( "Caps synchronous painting during rebuild. Zero paints every widget in the scope before show." )]
	[Range( 0.0f, 2000.0f )]
	public static float UiSuspendPaintBudgetMs
	{
		get => EditorCookie.Get( "EditorUi.SuspendPaintBudgetMs", 0.0f );
		set => EditorCookie.Set( "EditorUi.SuspendPaintBudgetMs", value );
	}

	/// <summary>Whether SuspendUpdates runs layout before show and burst paint.</summary>
	[Title( "SuspendUpdates Arrange" )]
	[Description( "Layout the rebuild scope before showing it. Off leaves arrange to the next frame tick." )]
	public static bool UiSuspendArrangeEnabled
	{
		get => EditorCookie.Get( "EditorUi.SuspendArrangeEnabled", true );
		set => EditorCookie.Set( "EditorUi.SuspendArrangeEnabled", value );
	}

	/// <summary>Whether new widgets fade in over a few frames after their first paint.</summary>
	[Title( "Widget Fade In" )]
	public static bool UiWidgetFadeEnabled
	{
		get => EditorCookie.Get( "EditorUi.WidgetFadeEnabled", !Rinten.Utility.CommandLine.HasSwitch( "-nowidgetfade" ) );
		set => EditorCookie.Set( "EditorUi.WidgetFadeEnabled", value );
	}

	/// <summary>Logs SuspendUpdates rebuild timing to the console.</summary>
	[Title( "Log Rebuild Timing" )]
	[Description( "Writes total, arrange, paint, and geometry milliseconds for each outer SuspendUpdates scope. Arrange includes converge pass count." )]
	public static bool UiSuspendProfilerLog
	{
		get => EditorCookie.Get( "EditorUi.SuspendProfilerLog", false );
		set => EditorCookie.Set( "EditorUi.SuspendProfilerLog", value );
	}
}
