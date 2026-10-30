using Editor.TreeBrowser;
using System.Text;
using static Editor.BaseItemWidget;
namespace Editor;

//Hierarchy behaviour per object: menu, drop, rename; rows are drawn by HierarchyTreePanel
partial class GameObjectNode
{
	protected static Widget MenuHost => SceneTreeWidget.Current;

	/// <summary>The object this is about.</summary>
	public GameObject Value { get; }

	public GameObjectNode( GameObject o )
	{
		Value = o;
	}

	/// <summary>Renames this and every other node in the batch, as one undo step; several get unique names.</summary>
	public void ApplyRename( string text, List<GameObjectNode> selection )
	{
		var undoName = selection != null && selection.Count > 1 ? $"Rename {selection.Count} Objects" : "Rename Object";
		var gos = (selection ?? [this]).Select( x => x.Value ).Where( x => x is not null ).ToArray();
		using ( SceneEditorSession.Current.UndoScope( undoName ).WithGameObjectChanges( gos, GameObjectUndoFlags.Properties ).Push() )
		{
			foreach ( var go in gos )
			{
				go.Name = text;
			}
			var goCount = gos.Count();
			if ( goCount > 1 ) //Only make unique if we renamed multiple objects
			{
				for ( int i = 0; i < goCount; i++ )
				{
					var go = gos.ElementAt( (i + 1) % goCount ); //Offset by one so we run the first one LAST, letting it keep its name if possible
					go.MakeNameUnique();
				}
			}
		}
	}

	public virtual bool OnDragStart() => true;


	private async void Drop( string text, ItemEdge dropEdge = default )
	{
		//Capture the target scene and session before any awaits
		var targetScene = Value.Scene;
		var session = targetScene.Editor;
		var targetGo = Value;

		var drop = await BaseDropObject.CreateDropFor( text );

		if ( drop is null )
			return;

		await drop.StartInitialize( text );

		using ( var sc = targetScene.Push() )
		{
			await drop.OnDrop();
			var go = drop.GameObject;
			if ( go.IsValid() )
			{
				//Insert above/below the target if dropping on an edge, otherwise parent to target.
				if ( targetGo is not Scene && dropEdge.HasFlag( ItemEdge.Top | ItemEdge.Bottom ) )
				{
					targetGo.AddSibling( go, before: dropEdge.HasFlag( ItemEdge.Top ) );
				}
				else
				{
					go.Parent = targetGo;
				}

				go.LocalTransform = new Transform();

				session?.Selection.Set( go );
			}
		}
		drop.Delete();
	}

	public virtual DropAction OnPanelDragDrop( DragData data, ItemEdge dropEdge, bool isDrop )
	{
		using var scope = Value.Scene.Push();

		if ( data.OfType<Component>().FirstOrDefault() is Component draggedComp )
		{
			if ( Value is Scene )
				return DropAction.Ignore;

			if ( isDrop && EditorTypeLibrary.TryGetType( draggedComp.GetType(), out TypeDescription typeDesc ) )
			{
				Menu m = new ContextMenu( MenuHost );

				var targetGo = Value;

				m.AddOption( $"Copy {draggedComp.GetType().Name} here", action: () =>
				{
					using var scene = SceneEditorSession.Scope();

					using ( SceneEditorSession.Current.UndoScope( $"Copy Component" ).WithComponentCreations().Push() )
					{
						var newComp = targetGo.Components.Create( typeDesc );
						var json = draggedComp.Serialize().AsObject();
						json.Remove( "__guid" );
						newComp.DeserializeImmediately( json );

						SceneEditorSession.Current.Selection.Clear();
						SceneEditorSession.Current.Selection.Add( targetGo );
					}
				} );
				m.AddOption( $"Move {draggedComp.GetType().Name} here", action: () =>
				{
					using var scene = SceneEditorSession.Scope();

					using ( SceneEditorSession.Current.UndoScope( $"Move Component" ).WithComponentDestructions( draggedComp ).WithComponentCreations().Push() )
					{
						var newComp = targetGo.Components.Create( typeDesc );

						var json = draggedComp.Serialize().AsObject();
						json.Remove( "__guid" );
						newComp.DeserializeImmediately( json );

						draggedComp.Destroy();

						SceneEditorSession.Current.Selection.Clear();
						SceneEditorSession.Current.Selection.Add( targetGo );
					}
				} );

				m.OpenAtCursor();
			}
			return DropAction.Move;
		}

		if ( data.Object is GameObject[] draggedGos )
		{
			var targetGo = Value;

			if ( draggedGos.Any( go => go == targetGo || targetGo.IsAncestor( go ) ) )
			{
				return DropAction.Ignore;
			}

			using var scene = SceneEditorSession.Scope();

			if ( isDrop )
			{
				using ( SceneEditorSession.Current.UndoScope( "Change Game Object Order" ).WithGameObjectChanges( draggedGos, GameObjectUndoFlags.Properties ).Push() )
				{
					foreach ( var draggedGo in draggedGos )
					{
						if ( Value is not Scene && dropEdge.HasFlag( ItemEdge.Top ) )
						{
							targetGo.AddSibling( draggedGo, true );
						}
						else if ( Value is not Scene && dropEdge.HasFlag( ItemEdge.Bottom ) )
						{
							targetGo.AddSibling( draggedGo, false );
						}
						else
						{
							draggedGo.SetParent( targetGo, true );
						}
					}
				}
			}
			return DropAction.Move;
		}

		var asset = AssetSystem.FindByPath( data.FileOrFolder );
		if ( asset is not null && asset.AssetType.FileExtension == "prefab" )
		{
			var pf = asset.LoadResource<PrefabFile>();
			if ( pf is null ) return DropAction.Ignore;

			if ( isDrop )
			{
				using var scene = SceneEditorSession.Scope();

				using ( SceneEditorSession.Current.UndoScope( "Instantiate Prefab" ).WithGameObjectCreations().Push() )
				{
					var instantiated = SceneUtility.GetPrefabScene( pf )?.Clone();

					if ( Value is not Scene && dropEdge.HasFlag( ItemEdge.Top ) )
					{
						Value.AddSibling( instantiated, true );
					}
					else if ( Value is not Scene && dropEdge.HasFlag( ItemEdge.Bottom ) )
					{
						Value.AddSibling( instantiated, false );
					}
					else
					{
						instantiated.SetParent( Value, true );
					}

					SceneEditorSession.Current.Selection.Set( instantiated );
				}
			}

			return DropAction.Move;
		}

		//For local file paths, only accept the drop if the extension is supported by a registered DropObject
		if ( !string.IsNullOrEmpty( data.FileOrFolder ) && BaseDropObject.CanCreateDropFor( data.FileOrFolder ) )
		{
			if ( isDrop )
				Drop( data.FileOrFolder, dropEdge );

			return DropAction.Move;
		}

		if ( data.Url is not null )
		{
			if ( isDrop )
				Drop( data.Url.ToString(), dropEdge );

			return DropAction.Move;
		}

		return DropAction.Ignore;
	}

	public virtual bool OnContextMenu()
	{
		var m = new ContextMenu( MenuHost ) { Searchable = true };
		AddGameObjectMenuItems( m, this );

		m.OpenAtCursor( false );

		return true;
	}

	public static void AddGameObjectMenuItems( Menu m, GameObjectNode treeNode )
	{
		GameObject gameObject = treeNode.Value as GameObject;
		if ( gameObject is Scene ) gameObject = null;

		bool isObjectMenu = gameObject.IsValid();
		bool isPrefabRoot = isObjectMenu && treeNode is PrefabNode;

		//For object creation, we need to determine the parent at click time, not menu creation time (because
		//right-clicking on empty space deselects nodes, and we want root-level creation in that case)
		Func<IEnumerable<GameObject>> getParentsForCreation = () =>
		{
			var currentSelection = EditorScene.Selection.OfType<GameObject>();

			if ( currentSelection.Any() )
			{
				//Use current selection as parents
				var validParents = currentSelection.Where( x => x != null && x.IsValid() );
				return validParents;
			}
			else
			{
				//No current selection - create at root level
				return [null];
			}
		};

		m.AddOption( "Cut", "content_cut", EditorScene.Cut, "editor.cut" ).Enabled = isObjectMenu && !isPrefabRoot;
		m.AddOption( "Copy", "content_copy", EditorScene.Copy, "editor.copy" ).Enabled = isObjectMenu;
		m.AddOption( "Paste", "content_paste", EditorScene.Paste, "editor.paste" );
		m.AddOption( "Paste As Child", null, EditorScene.PasteAsChild, "editor.paste-as-child" ).Enabled = isObjectMenu;
		m.AddOption( "Paste Special", "content_paste_go", EditorScene.PasteSpecial, "editor.paste-special" );
		m.AddOption( "Create Group", "file_copy", SceneEditorMenus.Group, "editor.group" ).Enabled = isObjectMenu && !isPrefabRoot;
		m.AddSeparator();
		m.AddOption( "Rename", "label", () => SceneTreeWidget.Current?.Tree?.BeginRename( treeNode.Value ), "editor.rename" ).Enabled = isObjectMenu;
		m.AddOption( "Duplicate", "file_copy", SceneEditorMenus.Duplicate, "editor.duplicate" ).Enabled = isObjectMenu && !isPrefabRoot;
		m.AddOption( "Delete", "delete", SceneEditorMenus.Delete, "editor.delete" ).Enabled = isObjectMenu && !isPrefabRoot;
		m.AddOption( "Hide/Show", "visibility_off", SceneEditorMenus.ToggleVisibility, "editor.toggle-visibility" ).Enabled = isObjectMenu;
		m.AddOption( "Isolate Selection", "filter_center_focus", SceneEditorMenus.IsolateSelection, "editor.isolate-selection" ).Enabled = isObjectMenu;

		m.AddSeparator();

		Menu addMenu = m.AddMenu( isObjectMenu ? "Create Child" : "Create", "add" );
		CreateObjectMenu( addMenu, _ =>
		{
			SceneTreeWidget.Current?.Tree?.OpenTo( treeNode.Value );
		}, createdGos =>
		{
			SceneTreeWidget.Current?.Tree?.SelectItems( createdGos, skipEvents: true );
			SceneTreeWidget.Current?.Tree?.BeginRename();
		} );

		if ( gameObject.IsValid() ) //has selection
		{
			var selectedGos = EditorScene.Selection.OfType<GameObject>().ToArray();

			m.AddSeparator();

			//prefabs
			if ( selectedGos.All( x => x.IsPrefabInstance ) )
			{
				var sources = selectedGos.Select( x => x.PrefabInstanceSource ).Distinct();
				bool multipleSources = sources.Count() > 1;

				var prefabPath = gameObject.PrefabInstanceSource;
				var rootPrefabName = System.IO.Path.GetFileNameWithoutExtension( prefabPath ).ToTitleCase();

				var prefabMenu = m.AddMenu( multipleSources ? "Prefabs (multiple)" : $"Prefab '{rootPrefabName}'", "dataset" );

				//Only locate the prefab if we have a single prefab selected (or all instances share the same prefab)
				if ( !multipleSources )
				{
					var prefabAsset = AssetSystem.FindByPath( prefabPath );
					if ( prefabAsset is not null )
					{
						prefabMenu.AddOption( "Open in Editor", "edit", () =>
						{
							if ( prefabAsset.TryLoadResource<PrefabFile>( out var prefab ) && prefab.IsValid )
							{
								EditorScene.OpenPrefab( prefab );
							}
						} ).Enabled = !prefabAsset.IsProcedural;

						prefabMenu.AddOption( "Find in Asset Browser", "search", () =>
						{
							TreeAssetBrowser.OpenTo( prefabAsset );
						} );
					}
				}

				prefabMenu.AddSeparator();

				var unlinkFromPrefabActionName = multipleSources ? "Unlink From Prefabs" : "Unlink From Prefab";
				prefabMenu.AddOption( unlinkFromPrefabActionName, "link_off", () =>
				{
					using ( SceneEditorSession.Current.UndoScope( unlinkFromPrefabActionName ).WithGameObjectChanges( selectedGos, GameObjectUndoFlags.All ).Push() )
					{
						EditorScene.Selection.Clear();
						foreach ( var go in selectedGos )
						{
							go.BreakFromPrefab();
							EditorScene.Selection.Add( go );
						}
					}
				} );

				prefabMenu.AddSeparator();

				var outermostPrefabName = EditorUtility.Prefabs.GetOuterMostPrefabName( gameObject );
				var isModified = selectedGos.Any( x => (x.IsPrefabInstanceRoot && EditorUtility.Prefabs.IsInstanceModified( x ))
													|| (x.IsPrefabInstance && EditorUtility.Prefabs.IsGameObjectInstanceModified( gameObject )) );

				var revertChangesActionName = "Revert Changes";
				prefabMenu.AddOption( revertChangesActionName, "history", () =>
				{
					using var scene = SceneEditorSession.Scope();

					using ( SceneEditorSession.Current.UndoScope( revertChangesActionName ).WithGameObjectChanges( selectedGos, GameObjectUndoFlags.Properties ).Push() )
					{
						foreach ( var go in selectedGos )
						{
							EditorUtility.Prefabs.RevertGameObjectInstanceChanges( go );
						}
					}
				} ).Enabled = isModified;

				var isAdded = selectedGos.All( x => x.IsPrefabInstance && EditorUtility.Prefabs.IsGameObjectAddedToInstance( x ) );
				if ( isAdded )
				{
					var applyAddActionName = multipleSources ? "Add Objects to Prefabs" : "Add Object to Prefab";
					if ( EditorUtility.Prefabs.IsOuterMostPrefabRoot( gameObject ) )
					{
						var parentPrefabName = EditorUtility.Prefabs.GetOuterMostPrefabName( gameObject.Parent );
						applyAddActionName += $" '{parentPrefabName ?? "Invalid"}'";
					}
					prefabMenu.AddOption( applyAddActionName, "save", () =>
					{
						using var scene = SceneEditorSession.Scope();

						//Undo for this is not really possible, as it doesn't modify this scene but rather the
						//prefab asset. But flag as unsaved to make sure user gets prompted after using this action.
						SceneEditorSession.Current.HasUnsavedChanges = true;

						foreach ( var go in selectedGos )
						{
							if ( go.IsPrefabInstance )
							{
								EditorUtility.Prefabs.AddInstanceAddedGameObjectToPrefab( go );
							}
						}
					} );
				}
				else
				{
					var applyChangesActionName = multipleSources ? "Apply to Prefabs" : "Apply to Prefab";
					prefabMenu.AddOption( applyChangesActionName, "save", () =>
					{
						using var scene = SceneEditorSession.Scope();

						//Undo for this is not really possible, as it doesn't modify this scene but rather the
						//prefab asset. But flag as unsaved to make sure user gets prompted after using this action.
						SceneEditorSession.Current.HasUnsavedChanges = true;

						foreach ( var go in selectedGos )
						{
							//Nested roots must go through ApplyGameObjectInstanceChangesToPrefab, not WriteInstanceToPrefab.
							if ( EditorUtility.Prefabs.IsOuterMostPrefabRoot( go ) )
							{
								EditorUtility.Prefabs.WriteInstanceToPrefab( go );
							}
							else if ( go.IsPrefabInstance )
							{
								EditorUtility.Prefabs.ApplyGameObjectInstanceChangesToPrefab( go );
							}
						}
					} ).Enabled = isModified;
				}
			}

			if ( !isPrefabRoot )
			{
				m.AddOption( "Create New Prefab", "note_add", () => ConvertToPrefab( selectedGos ) );
			}

			m.AddOption( "Replace with Prefab", "change_circle", SceneEditorMenus.ReplaceWithPrefab ).Enabled = isObjectMenu && !isPrefabRoot;
		}
	}

	static void ConvertToPrefab( IEnumerable<GameObject> gameObjects )
	{
		using var scene = SceneEditorSession.Scope();

		using ( SceneEditorSession.Current.UndoScope( "Convert to Prefab" ).WithGameObjectChanges( gameObjects, GameObjectUndoFlags.All ).WithGameObjectCreations().Push() )
		{
			var selection = gameObjects.ToArray();
			var first = selection.FirstOrDefault();

			if ( first is null ) return;

			var saveLocation = "";

			var fd = new FileDialog( null );
			fd.Title = $"Save {first.Name} as Prefab..";
			fd.Directory = Project.Current.GetAssetsPath();
			fd.DefaultSuffix = $".prefab";
			fd.SelectFile( first.Name );
			fd.SetFindFile();
			fd.SetModeSave();
			fd.SetNameFilter( $"Prefab File (*.prefab)" );

			if ( !fd.Execute() ) return;

			saveLocation = fd.SelectedFile;

			GameObject prefabRoot;

			if ( selection.Count() == 1 )
			{
				prefabRoot = first;
			}
			else
			{
				prefabRoot = new GameObject();
				prefabRoot.WorldTransform = first.WorldTransform;

				for ( var i = 0; i < selection.Length; i++ )
				{
					selection[i].SetParent( prefabRoot, true );
				}
			}
			prefabRoot.Name = System.IO.Path.GetFileNameWithoutExtension( saveLocation );

			prefabRoot = EditorUtility.Prefabs.ConvertGameObjectToPrefab( prefabRoot, saveLocation );
			if ( prefabRoot is null ) return;

			EditorUtility.InspectorObject = prefabRoot;
			EditorScene.Selection.Set( prefabRoot );
		}
	}

	public static void CreateObjectMenu( Menu menu, GameObject parent, Action<GameObject> afterCreate )
	{
		void PostCreate( GameObject go, GameObject parent )
		{
			go.Parent = parent;

			if ( !EditorPreferences.CreateObjectsAtOrigin && !parent.IsValid() && SceneViewPanel.Current?.LastSelectedViewport?.IsValid() == true )
			{
				//I wonder if we should be tracing and placing it on the surface?
				go.LocalPosition = SceneViewPanel.Current.LastSelectedViewport.State.CameraPosition + SceneViewPanel.Current.LastSelectedViewport.State.CameraRotation.Forward * (7.62f);
			}

			afterCreate?.Invoke( go );
		}

		menu.AddOption( "Empty", "dataset", () =>
		{
			using var scope = SceneEditorSession.Scope();

			using ( SceneEditorSession.Current.UndoScope( "Create Empty" ).WithGameObjectCreations().Push() )
			{
				var createdObjects = new List<GameObject>();
				var go = new GameObject( true, "Object" );
				PostCreate( go, parent );
				createdObjects.Add( go );
			}
		} );

		foreach ( var entry in EditorUtility.Prefabs.GetTemplates() )
		{
			var menuPath = string.IsNullOrEmpty( entry.MenuPath ) ? entry.ResourceName : entry.MenuPath;
			menu.AddOption( menuPath.Split( '/' ), entry.MenuIcon, () =>
			{
				using var scope = SceneEditorSession.Scope();

				using ( SceneEditorSession.Current.UndoScope( "Create Prefab" ).WithGameObjectCreations().Push() )
				{
					var createdObjects = new List<GameObject>();
					var goName = menuPath.Split( '/' ).Last();
					var go = SceneUtility.GetPrefabScene( entry )?.Clone();
					if ( !entry.DontBreakAsTemplate ) go.BreakFromPrefab();
					go.Name = goName;
					PostCreate( go, parent );
					createdObjects.Add( go );
				}
			} );
		}

		menu.AddOption( "3D Object/Terrain".Split( '/' ), "landscape", () =>
		{
			using var scope = SceneEditorSession.Scope();

			using ( SceneEditorSession.Current.UndoScope( "Create Terrain" ).WithGameObjectCreations().Push() )
			{
				var createdObjects = new List<GameObject>();
				var go = new GameObject( true, "Terrain" );
				go.Components.Create<Terrain>();
				PostCreate( go, parent );
				createdObjects.Add( go );
			}
		} );
	}

	public static void CreateObjectMenu( Menu menu, Action<GameObject> afterCreate, Action<IEnumerable<GameObject>> afterComplete )
	{
		void PostCreate( GameObject go, GameObject parent )
		{
			go.Parent = parent;

			if ( !EditorPreferences.CreateObjectsAtOrigin && !parent.IsValid() && SceneViewPanel.Current?.LastSelectedViewport?.IsValid() == true )
			{
				//I wonder if we should be tracing and placing it on the surface?
				go.LocalPosition = SceneViewPanel.Current.LastSelectedViewport.State.CameraPosition + SceneViewPanel.Current.LastSelectedViewport.State.CameraRotation.Forward * (7.62f);
			}

			afterCreate?.Invoke( go );
		}

		menu.AddOption( "Empty", "dataset", () =>
		{
			using var scope = SceneEditorSession.Scope();

			using ( SceneEditorSession.Current.UndoScope( "Create Empty" ).WithGameObjectCreations().Push() )
			{
				var createdObjects = new List<GameObject>();
				var parents = GetParentsForCreation();
				foreach ( var parent in parents )
				{
					var go = new GameObject( true, "Object" );
					PostCreate( go, parent );
					createdObjects.Add( go );
				}
				afterComplete?.Invoke( createdObjects );
			}
		} );

		foreach ( var entry in EditorUtility.Prefabs.GetTemplates() )
		{
			var menuPath = string.IsNullOrEmpty( entry.MenuPath ) ? entry.ResourceName : entry.MenuPath;
			menu.AddOption( menuPath.Split( '/' ), entry.MenuIcon, () =>
			{
				using var scope = SceneEditorSession.Scope();

				using ( SceneEditorSession.Current.UndoScope( "Create Prefab" ).WithGameObjectCreations().Push() )
				{
					var createdObjects = new List<GameObject>();
					var goName = menuPath.Split( '/' ).Last();
					var parents = GetParentsForCreation();
					foreach ( var parent in parents )
					{
						var go = SceneUtility.GetPrefabScene( entry )?.Clone();
						if ( !entry.DontBreakAsTemplate ) go.BreakFromPrefab();
						go.Name = goName;
						PostCreate( go, parent );
						createdObjects.Add( go );
					}
					afterComplete?.Invoke( createdObjects );
				}
			} );
		}

		menu.AddOption( "3D Object/Terrain".Split( '/' ), "landscape", () =>
		{
			using var scope = SceneEditorSession.Scope();

			using ( SceneEditorSession.Current.UndoScope( "Create Terrain" ).WithGameObjectCreations().Push() )
			{
				var createdObjects = new List<GameObject>();
				var parents = GetParentsForCreation();
				foreach ( var parent in parents )
				{
					var go = new GameObject( true, "Terrain" );
					go.Components.Create<Terrain>();
					PostCreate( go, parent );
					createdObjects.Add( go );
				}
				afterComplete?.Invoke( createdObjects );
			}
		} );
	}

	public static void CreateObjectCategoryMenu( string category, Menu menu, GameObject parent, Action<GameObject> afterCreate )
	{
		void PostCreate( GameObject go )
		{
			go.Parent = parent;

			if ( !EditorPreferences.CreateObjectsAtOrigin && !parent.IsValid() )
			{
				//I wonder if we should be tracing and placing it on the surface?
				go.LocalPosition = SceneViewPanel.Current.LastSelectedViewport.State.CameraPosition + SceneViewPanel.Current.LastSelectedViewport.State.CameraRotation.Forward * (7.62f);
			}

			afterCreate?.Invoke( go );
		}

		foreach ( var entry in EditorUtility.Prefabs.GetTemplates() )
		{
			var menuPath = string.IsNullOrEmpty( entry.MenuPath ) ? entry.ResourceName : entry.MenuPath;

			if ( !menuPath.Contains( '/' ) )
				continue;

			if ( menuPath.Split( '/' )[0] != category )
				continue;

			menu.AddOption( menuPath.Split( '/' )[1], entry.MenuIcon, () =>
			{
				using var scope = SceneEditorSession.Scope();

				using ( SceneEditorSession.Current.UndoScope( "Create Prefab" ).WithGameObjectCreations().Push() )
				{
					var go = SceneUtility.GetPrefabScene( entry )?.Clone();
					if ( !entry.DontBreakAsTemplate ) go.BreakFromPrefab();
					go.Name = menuPath.Split( '/' ).Last();
					PostCreate( go );
				}
			} );
		}
	}

	//Determines the parents for context-menu GameObject creation (at the time the menu option is selected, not when the menu is built).
	private static IEnumerable<GameObject> GetParentsForCreation()
	{
		var currentSelection = EditorScene.Selection.OfType<GameObject>();

		if ( currentSelection.Any() )
		{
			//Use current selection as parents
			var validParents = currentSelection.Where( x => x != null && x.IsValid() );
			return validParents;
		}
		else
		{
			//No current selection, create at root
			return [null];
		}
	}
}
