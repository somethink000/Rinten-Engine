using System.Text.RegularExpressions;

namespace Editor;

/// <summary>Hierarchy dock: toolbar in markup, tree on panels.</summary>
[Dock( "Editor", "Hierarchy", "list", DockArea.Right )]
public partial class SceneTreeWidget : PanelWidget
{
	readonly HierarchyPanel panel;
	readonly HierarchyTreePanel tree;

	public static SceneTreeWidget Current { get; private set; }

	public HierarchyTreePanel Tree => tree;

	public string SearchQuery { get; private set; } = string.Empty;

	public SceneTreeWidget( Widget parent ) : base( parent )
	{
		HorizontalSizeMode = SizeMode.Flexible;
		VerticalSizeMode = SizeMode.Flexible;

		Current = this;

		tree = new HierarchyTreePanel { Host = this };

		panel = new HierarchyPanel { Host = this };
		panel.SetTree( tree );
		SetPanel( panel );

		_lastScene.SetTarget( null );
		CheckForChanges();

		EditorUtility.OnInspect += OnInspect;
	}

	public void SetSearchQuery( string query )
	{
		SearchQuery = query;
		queryDirty = true;
	}

	public void ClearSearch()
	{
		SearchQuery = string.Empty;
		queryDirty = true;
		panel.SyncSearch();
		CheckForChanges();

		foreach ( var item in SceneEditorSession.Current?.Selection ?? Enumerable.Empty<object>() )
			tree.ExpandPathTo( item );

		var scrollTarget = SceneEditorSession.Current?.Selection.FirstOrDefault();
		if ( scrollTarget is not null )
			tree.ScrollTo( scrollTarget );
	}

	public void CreateGameObjectMenu()
	{
		var m = new ContextMenu( this );

		using var scope = SceneEditorSession.Scope();
		var selected = EditorScene.Selection.FirstOrDefault() as GameObject;

		GameObjectNode.CreateObjectMenu( m, selected, go =>
		{
			tree.Open( SceneEditorSession.Current?.Scene );
			tree.SelectItems( new[] { go }, skipEvents: true );
			tree.BeginRename();
		} );

		m.OpenAtCursor( false );
	}

	WeakReference<Scene> _lastScene = new( null );
	bool queryDirty = false;

	[EditorEvent.Frame]
	public void CheckForChanges()
	{
		var session = SceneEditorSession.Current;
		if ( session is null )
			return;

		_lastScene.TryGetTarget( out var last );

		var sceneChanged = !ReferenceEquals( last, session.Scene );

		if ( !queryDirty && !sceneChanged )
			return;

		_lastScene.SetTarget( session.Scene );

		if ( sceneChanged )
		{
			CloseItemsFromClosedScenes();
			tree.ClearOpenState();
			RestoreSelection( session );
			tree.SyncSelectionFromSession();
		}

		queryDirty = false;
		panel.SyncSearch();
		tree.Bump();
	}

	void RestoreSelection( SceneEditorSession session )
	{
		var selection = session.Selection.Select( x => x as GameObject ).ToList();
		session.Selection.Clear();

		foreach ( var go in session.Scene.GetAllObjects( false ) )
		{
			if ( selection.FirstOrDefault( x => x.IsValid() && x.Id == go.Id ).IsValid() )
				session.Selection.Add( go );
		}
	}

	void CloseItemsFromClosedScenes()
	{
		foreach ( var item in tree.OpenItems.ToArray() )
		{
			if ( item is GameObject go && !IsSceneOpen( go.Scene ) )
				tree.Close( item );
		}
	}

	static bool IsSceneOpen( Scene scene )
	{
		if ( scene is null )
			return false;

		foreach ( var session in SceneEditorSession.All )
		{
			if ( ReferenceEquals( session.Scene, scene ) )
				return true;

			if ( ReferenceEquals( session.GameSession?.Scene, scene ) )
				return true;
		}

		return false;
	}

	public void OnInspect( EditorUtility.OnInspectArgs args )
	{
		var selection = SceneEditorSession.Current?.Selection;
		if ( selection is not null && tree.ChosenHere == (selection, selection.GetHashCode()) ) return;

		foreach ( var item in selection ?? Enumerable.Empty<object>() )
			tree.ExpandPathTo( item );

		var scrollTarget = selection?.LastOrDefault();
		if ( scrollTarget is not null )
			tree.ScrollTo( scrollTarget );
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();
		EditorUtility.OnInspect -= OnInspect;
	}

	[Shortcut( "editor.rename", "F2", ShortcutType.Window )]
	void OnRenameShortcut()
	{
		tree?.BeginRename();
	}
}
