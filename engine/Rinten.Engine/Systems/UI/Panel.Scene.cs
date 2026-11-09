namespace Rinten.UI;

//The engine's side of a panel: the scene and game object it belongs to, and its place in the scene's index
public partial class Panel
{
	/// <summary>Returns the scene that this panel belongs to</summary>
	public Scene Scene
	{
		get => GameObject?.Scene;

		[Obsolete( "Setting Scene is no longer supported. This should be done automatically, internally" )]
		set => GameObject = value;
	}

	/// <summary>Returns the GameObject that this panel belongs to</summary>
	public GameObject GameObject
	{
		get
		{
			if ( field is not null )
				return field;

			//Resolved once per reshape of the tree, not once per read. This is the first question a panel's tick asks - UpdateSceneIndex reads Scene, and
			//Scene reads this - so walking to the root here cost the depth of the tree for every panel every frame, to be told what it was told last frame.
			if ( inheritedAt != StructureVersion )
			{
				inherited = Parent?.GameObject;
				inheritedAt = StructureVersion;
			}

			return inherited;
		}

		internal set
		{
			field = value;
			NoteStructureChanged();
			RequestTick();
		}
	}

	private GameObject inherited;
	private int inheritedAt = -1;

	//The scene we're currently registered in (if any), so we know what to remove ourselves from.
	Scene _indexedScene;

	//Keep this panel registered in its scene's object index, so it can be found by Scene.GetAll<T> / Scene.RunEvent<T>
	// / ISceneEvent<T> exactly like a component. Idempotent and cheap - a no-op while our scene hasn't changed.
	void UpdateSceneIndex()
	{
		var scene = Scene;
		if ( scene == _indexedScene )
			return;

		_indexedScene?.RemoveObjectFromDirectory( this );
		_indexedScene = scene;
		_indexedScene?.AddObjectToDirectory( this );
	}

	//Drop ourselves from the scene object index. Called when the panel is deleted.
	void RemoveFromSceneIndex()
	{
		if ( _indexedScene is null )
			return;

		_indexedScene.RemoveObjectFromDirectory( this );
		_indexedScene = null;
	}

	partial void OnSceneTick() => UpdateSceneIndex();

	partial void OnListed() => Rinten.Event.Register( this );

	partial void OnUnlisted() => Rinten.Event.Unregister( this );

	partial void OnSceneDeleted()
	{
		RemoveFromSceneIndex();
		GameObject = null;
	}
}
