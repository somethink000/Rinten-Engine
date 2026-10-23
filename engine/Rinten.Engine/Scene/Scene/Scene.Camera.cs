namespace Rinten;

public partial class Scene : GameObject
{
	/// <summary>The camera this scene is seen through, or null.</summary>
	[ActionGraphInclude]
	public CameraComponent Camera
	{
		get => _camera.IsValid() ? _camera : null;
		private set => _camera = value;
	}

	private CameraComponent _camera;

	//Editor camera for this scene, or null; cached here since distance-based systems read it every frame
	internal CameraComponent EditorCamera => _editorCamera.IsValid() ? _editorCamera : null;

	private CameraComponent _editorCamera;

	internal void UpdateMainCamera()
	{
		//Get main cameras first, then order by priority after that. So that if we don't have a main camera, it'll fallback to a shitter one
		Camera = GetAllComponents<CameraComponent>()
			.Where( x => x.IsValid() && x.IsSceneEditorCamera == false && x.RenderTarget is null )
			.OrderBy( x => x.IsMainCamera ? 0 : 1 )
			.ThenBy( x => x.Priority )
			.FirstOrDefault();

		//And the editor's own - not a hidden stand-in a scene camera is drawn through, which is never enabled.
		_editorCamera = GetAllComponents<CameraComponent>()
			.FirstOrDefault( x => x.IsValid() && x.IsSceneEditorCamera && x.Active );
	}
}
