namespace Editor;

/// <summary>Editor tab hosting the scene view; switches active session on hover/focus, ends it on close</summary>
public partial class SceneDock : PanelWidget
{
	/// <summary>The dock of the scene being worked in, for anything that needs a widget.</summary>
	public static SceneDock Active { get; private set; }

	public SceneEditorSession Session => _editorSession.GameSession ?? _editorSession;

	/// <summary>The scene view inside this tab.</summary>
	public SceneViewPanel SceneView { get; private set; }

	private SceneEditorSession _editorSession;

	public SceneDock( SceneEditorSession session ) : base( null )
	{
		_editorSession = session;

		SceneView = new SceneViewPanel( session );

		SetPanel( SceneView );

		DeleteOnClose = true;

		Name = session.Scene.Source?.ResourcePath;

		Active = this;
	}

	protected override bool OnClose()
	{
		if ( _editorSession.HasUnsavedChanges )
		{
			this.ShowUnsavedChangesDialog(
				assetName: _editorSession.Scene.Name,
				assetType: _editorSession.IsPrefabSession ? "prefab" : "scene",
				onSave: () => _editorSession.Save( false ) );

			return false;
		}

		return true;
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		if ( Active == this )
			Active = null;

		SceneView = null;

		_editorSession.Destroy();
		_editorSession = null;
	}

	protected override void OnVisibilityChanged( bool visible )
	{
		base.OnVisibilityChanged( visible );

		//A view behind the front tab is shown too - while a switch finishes, or when the scene area moves into
		//another workspace - and taking the session from that would leave the strip on one scene and the editor on another
		if ( !visible || !_editorSession.IsFrontTab ) return;

		Active = this;

		_editorSession.MakeActive();

		//Focus the viewport so keybinds work right away after switching tabs
		SceneView?.LastSelectedViewport?.Renderer?.Focus();
	}

	protected override void OnFocus( FocusChangeReason reason )
	{
		base.OnFocus( reason );

		Active = this;

		_editorSession.MakeActive();
	}

	//The scene view says which tab is being worked in - the pointer never reaches this widget, because everything in it is markup.
	internal void MakeActiveDock() => Active = this;
}
