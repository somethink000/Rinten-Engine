namespace Editor;

//Sets a distance from the camera - a depth of field's focus - from a click on the scene: the clicked point's depth along
//the view, previewed under the cursor. Right click or Escape leaves it as it was
[EditorTool( "tools.focus-pick-tool", Hidden = true )]
public class FocusPickTool : EditorTool
{
	static string lastTool;
	static Action finished;

	internal static SerializedProperty TargetProperty;

	public static void Begin( SerializedProperty property, Action onFinished )
	{
		if ( EditorToolManager.CurrentModeName == nameof( FocusPickTool ) )
		{
			End();
			return;
		}

		lastTool = EditorToolManager.CurrentModeName;
		TargetProperty = property;
		finished = onFinished;
		EditorToolManager.SetTool( nameof( FocusPickTool ) );
	}

	public override void OnEnabled()
	{
		base.OnEnabled();
		AllowGameObjectSelection = false;
	}

	public override void OnUpdate()
	{
		base.OnUpdate();

		if ( TargetProperty is null || !TargetProperty.Parent.Contains( TargetProperty ) || Gizmo.WasRightMouseReleased
			|| Application.IsKeyDown( KeyCode.Escape ) )
		{
			End();
			return;
		}

		var tr = MeshTrace.Run();
		if ( !tr.Hit ) return;

		var depth = Vector3.Dot( tr.HitPosition - Gizmo.Camera.Position, Gizmo.Camera.Rotation.Forward );

		using ( Gizmo.Scope( "Focus Pick" ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.Color = Theme.Green;
			Gizmo.Draw.LineCircle( tr.HitPosition, tr.Normal, ScreenSize( tr.HitPosition, 10 ) );
			Gizmo.Draw.ScreenText( $"{depth:0.##} m", tr.HitPosition, new Vector2( 12, -12 ), size: 13, flags: TextFlag.LeftBottom );
		}

		if ( !Gizmo.WasLeftMouseReleased ) return;

		if ( depth > 0 ) TargetProperty.SetValue( depth );
		End();
	}

	//Metres that look the same size at any distance
	static float ScreenSize( Vector3 at, float scale ) => scale * Gizmo.Camera.Position.Distance( at ) / 1000.0f;

	static void End()
	{
		var tool = lastTool;
		var done = finished;

		lastTool = null;
		finished = null;
		TargetProperty = null;

		if ( !string.IsNullOrEmpty( tool ) ) EditorToolManager.SetTool( tool );
		done?.Invoke();
	}
}
