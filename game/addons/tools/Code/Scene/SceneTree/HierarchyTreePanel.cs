using System.Text.RegularExpressions;
using Rinten.UI;
using static Editor.BaseItemWidget;

namespace Editor;

/// <summary>Hierarchy tree backed by TreePanel.</summary>
[StyleSheet( "HierarchyTreePanel.cs.scss" )]
public class HierarchyTreePanel : TreePanel<object>
{
	IDisposable selectionUndoScope;
	int lastSelectionHash;

	public SceneTreeWidget Host { get; set; }

	public HierarchyTreePanel()
	{
		AddClass( "hierarchy-tree" );
		SelectionMode = TreeSelectionMode.Multi;
		EditorEvent.Register( this );
	}

	protected override bool SupportsRename => true;

	public override bool SupportsDragDrop => true;

	protected override int Revision
	{
		get
		{
			var session = SceneEditorSession.Current;
			if ( session?.Scene is null ) return 0;

			var hash = HashCode.Combine( session.Scene, Host?.SearchQuery );

			//A search shows rows for objects anywhere in the level, whether or not the tree is open at
			//them, so there the whole scene is what has to be watched. Without a search, only what is open.
			var searching = !string.IsNullOrEmpty( Host?.SearchQuery );

			return HashCode.Combine( hash, HashShown( session.Scene, searching ) );
		}
	}

	//A search watches the whole scene, however few rows it found
	protected override bool RevisionIsCostly => base.RevisionIsCostly || !string.IsNullOrEmpty( Host?.SearchQuery );

	protected override IEnumerable<object> Roots
	{
		get
		{
			var session = SceneEditorSession.Current;
			if ( session?.Scene is null )
				yield break;

			var scene = session.Scene;
			if ( !string.IsNullOrEmpty( Host?.SearchQuery ) )
			{
				foreach ( var go in Search( scene, Host.SearchQuery ) )
					yield return go;

				yield break;
			}

			yield return scene;
		}
	}

	//Asked every frame, so never runs the search: a search that finds nothing is an empty row list, which the sync handles
	protected override bool HasRoots() => SceneEditorSession.Current?.Scene is not null;

	//Up through the parents: a selection of thousands asks this of each, every frame of a box drag
	protected override bool TryBuildPath( object target, List<object> path )
	{
		if ( !string.IsNullOrEmpty( Host?.SearchQuery ) ) return base.TryBuildPath( target, path );

		var scene = SceneEditorSession.Current?.Scene;

		for ( var at = target as GameObject; at is not null; at = at.Parent )
		{
			if ( at is Scene )
			{
				if ( at != scene ) break;

				path.Add( at );
				path.Reverse();

				return true;
			}

			if ( !at.ShouldShowInHierarchy() ) break;

			path.Add( at );
		}

		path.Clear();
		return false;
	}

	protected override IEnumerable<object> GetChildren( object node )
	{
		if ( !string.IsNullOrEmpty( Host?.SearchQuery ) )
			yield break;

		if ( node is Scene scene )
		{
			foreach ( var child in scene.Children.Where( x => x.ShouldShowInHierarchy() ) )
				yield return child;
			yield break;
		}

		if ( node is GameObject go )
		{
			foreach ( var child in go.Children.Where( x => x.ShouldShowInHierarchy() ) )
				yield return child;
		}
	}

	protected override TreeItemInfo GetInfo( object node ) => Describe( node, GetChildren( node ).Any() );

	//A row's look follows its object between changes to the tree's shape - a prefab edited, an object dragged over its parent -
	//so it is asked again each frame. Whether it has children is what the row already knows: that only changes with the shape.
	protected internal override TreeItemInfo? LiveInfo( object node, TreeItemInfo current ) => Describe( node, current.HasChildren );

	//What one row says: its object's glyph and colours, its quiet word, and its eye.
	TreeItemInfo Describe( object node, bool hasChildren )
	{
		if ( node is PrefabScene prefab )
		{
			var look = HierarchyRowAppearance.ForPrefab( prefab );
			return new TreeItemInfo( node.ToString(), look.Icon, hasChildren ) { IconColor = look.IconColor, TextColor = look.TextColor };
		}

		if ( node is Scene scene )
		{
			var look = HierarchyRowAppearance.ForScene( scene );
			var name = string.IsNullOrWhiteSpace( scene.Name ) ? "Untitled Scene" : scene.Name;

			return new TreeItemInfo( name, look.Icon, hasChildren ) { IconColor = look.IconColor, TextColor = look.TextColor };
		}

		if ( node is GameObject go )
		{
			var look = HierarchyRowAppearance.For( go, this );
			var name = string.IsNullOrWhiteSpace( go.Name ) ? "Untitled GameObject" : go.Name;

			//The quiet word after the name: still loading, who owns it on the network, or that it is tagged hidden.
			string suffix = null;
			var suffixIsIcon = false;

			if ( look.ShowLoading ) (suffix, suffixIsIcon) = ("access_time_filled", true);
			else if ( !string.IsNullOrEmpty( look.NetworkOwner ) ) suffix = look.NetworkOwner;
			else if ( look.ShowHiddenTag ) (suffix, suffixIsIcon) = ("visibility_off", true);

			return new TreeItemInfo( name, look.Icon, hasChildren )
			{
				IconColor = look.IconColor,
				TextColor = look.TextColor,
				Overlay = look.OverlayIcon,
				Suffix = suffix,
				SuffixIsIcon = suffixIsIcon,
				Dim = look.Dimmed,
				Classes = look.PrefabModified ? "prefab-modified" : null,
				Actions = go.Enabled ? TreeRowAction.EyeOn : TreeRowAction.EyeOff,
			};
		}

		return new( node?.ToString() ?? string.Empty, "layers", false );
	}

	//The eye: switches the object on or off, as one undo step.
	protected internal override void OnRowAction( object node, int index )
	{
		if ( node is not GameObject go || go is Rinten.Scene || !go.IsValid() ) return;
		if ( SceneEditorSession.Resolve( go ) is not { } session ) return;

		using var scope = session.Scene.Push();
		using ( session.UndoScope( go.Enabled ? "Disable GameObject" : "Enable GameObject" ).WithGameObjectChanges( go, GameObjectUndoFlags.Properties ).Push() )
		{
			go.Enabled = !go.Enabled;
		}
	}

	//A press while the eye-dropper is out picks the object rather than the row.
	protected internal override bool OnRowPressed( object node, MousePanelEvent e )
	{
		if ( EyeDropperTool.TargetProperty is null || e.MouseButton != MouseButtons.Left || node is not GameObject go ) return false;

		EyeDropperTool.Select( go );
		return true;
	}

	//A double-click on an object brings the camera to it, as its node's OnActivated always did in the widget tree.
	protected internal override bool OnRowActivated( object node )
	{
		if ( node is not GameObject go || go is Rinten.Scene ) return false;

		var selection = SceneEditorSession.Current?.Selection;
		if ( selection is not null && !selection.Contains( go ) )
			selection.Set( go );

		SceneEditorMenus.Frame();
		return true;
	}

	public void ExpandPathTo( object target ) => OpenTo( target );

	public void SelectItems( IEnumerable<object> items, bool skipEvents = false )
	{
		var list = items.ToList();
		Select( list, skipEvents );

		//Skipped events mean OnSelectionChanged never ran, so push to the session ourselves or the inspector keeps showing the previous selection.
		if ( skipEvents && SceneEditorSession.Current is { } session )
		{
			session.Selection.Clear();
			foreach ( var item in list )
				session.Selection.Add( item );

			lastSelectionHash = session.Selection.GetHashCode();
		}
	}

	public void SyncSelectionFromSession()
	{
		var session = SceneEditorSession.Current;
		if ( session is null ) return;

		Select( session.Selection.ToList(), skipEvents: true );
		lastSelectionHash = session.Selection.GetHashCode();
	}

	public override void BeginRename( object node = default )
	{
		var items = GetRenameItems( node ).ToArray();
		if ( items.Length == 0 ) return;

		BeginRenameSelection( items[^1], items );
	}

	private IEnumerable<object> GetRenameItems( object node )
	{
		if ( node is GameObject explicitGo && explicitGo.IsValid() && !(explicitGo is Scene) )
			return new object[] { (object)explicitGo };

		var fromTree = Selected
			.OfType<GameObject>()
			.Where( x => x.IsValid() && !(x is Scene) )
			.Cast<object>();

		if ( fromTree.Any() )
			return fromTree;

		var session = SceneEditorSession.Current;
		if ( session is null )
			return Array.Empty<object>();

		return session.Selection
			.OfType<GameObject>()
			.Where( x => x.IsValid() && !(x is Scene) )
			.Cast<object>();
	}

	protected override void OnContextMenu( object item )
	{
		if ( HierarchyNodeBridge.ShowContextMenu( item ) )
			return;

		OnBodyContextMenu();
	}

	protected override void OnBodyContextMenu()
	{
		var scene = SceneEditorSession.Current?.Scene;
		if ( scene is not null )
			HierarchyNodeBridge.ShowContextMenu( scene );
	}

	protected override void OnBeforeSelectionChanged( IReadOnlyList<object> previous )
	{
		if ( SceneEditorSession.Current is not { } session ) return;

		//One left open would hold the session's undo shut - see IsUndoScopeOpen.
		selectionUndoScope?.Dispose();
		selectionUndoScope = session.UndoScope( "Select GameObject(s)" ).Push();
	}

	protected override void OnSelectionChanged( IReadOnlyList<object> selected )
	{
		selectionUndoScope?.Dispose();
		selectionUndoScope = null;

		var session = SceneEditorSession.Current;
		if ( session is null ) return;

		session.Selection.Clear();
		foreach ( var item in selected )
			session.Selection.Add( item );

		lastSelectionHash = session.Selection.GetHashCode();
		ChosenHere = (session.Selection, lastSelectionHash);
	}

	//The selection as a click in the tree last left it: those rows are in view already, so inspecting it scrolls nowhere
	internal (object Selection, int Hash) ChosenHere;

	protected override void OnRenamed( object node, string text, object[] selection )
		=> HierarchyNodeBridge.ApplyRename( node, text, selection );

	protected override bool CanDrag( object node ) => HierarchyNodeBridge.CanDrag( node );

	protected override DragData CreateDragData( TreeRowPanel<object> row )
	{
		var data = new DragData();

		if ( IsSelected( row.Node ) )
			data.Object = Selected.OfType<GameObject>().ToArray();
		else if ( row.Node is GameObject go )
			data.Object = new GameObject[] { go };

		return data;
	}

	protected override DropAction OnDragDrop( object target, DragData data, TreeDropEdge edge, bool isDrop )
		=> HierarchyNodeBridge.OnDragDrop( target, data, ToItemEdge( edge ), isDrop );

	protected override DropAction OnBodyDragDrop( DragData data, bool isDrop )
	{
		if ( !TryGetBodyDropObjects( data, out var draggedGos ) )
			return DropAction.Ignore;

		if ( !isDrop )
			return DropAction.Move;

		using var scope = SceneEditorSession.Scope();
		var session = SceneEditorSession.Current;
		if ( session is null ) return DropAction.Ignore;

		var scene = session.Scene;
		using ( session.UndoScope( "Change Game Object Order" ).WithGameObjectChanges( draggedGos, GameObjectUndoFlags.Properties ).Push() )
		{
			foreach ( var go in draggedGos )
				MoveToSceneBottom( go, scene );
		}

		return DropAction.Move;
	}

	protected override bool TryGetBodyDropHint( DragData data, out TreeRowPanel<object> row, out TreeDropEdge edge )
	{
		row = null;
		edge = TreeDropEdge.None;

		if ( !TryGetBodyDropObjects( data, out _ ) )
			return false;

		var scene = SceneEditorSession.Current?.Scene;
		if ( scene is null )
			return false;

		var rows = Rows.ToList();
		if ( rows.Count == 0 )
			return false;

		TreeRowPanel<object> lastRootChild = null;
		foreach ( var hierarchyRow in rows )
		{
			if ( hierarchyRow.Node is GameObject go && go.Parent == scene )
				lastRootChild = hierarchyRow;
		}

		if ( lastRootChild is not null )
		{
			row = lastRootChild;
			edge = TreeDropEdge.Bottom;
			return true;
		}

		var sceneRow = rows.FirstOrDefault( x => ReferenceEquals( x.Node, scene ) );
		if ( sceneRow is not null )
		{
			row = sceneRow;
			edge = TreeDropEdge.Inside;
			return true;
		}

		row = rows[^1];
		edge = TreeDropEdge.Bottom;
		return true;
	}

	[EditorEvent.Frame]
	private void SyncTreeSelection()
	{
		var session = SceneEditorSession.Current;
		if ( session is null ) return;

		var hash = session.Selection.GetHashCode();
		if ( hash == lastSelectionHash ) return;

		lastSelectionHash = hash;
		Select( session.Selection.ToList(), skipEvents: true );
	}

	private static ItemEdge ToItemEdge( TreeDropEdge edge )
	{
		if ( edge == TreeDropEdge.Top ) return ItemEdge.Top;
		if ( edge == TreeDropEdge.Bottom ) return ItemEdge.Bottom;
		return ItemEdge.None;
	}

	private static bool TryGetBodyDropObjects( DragData data, out GameObject[] draggedGos )
	{
		draggedGos = null;

		if ( data.Object is not GameObject[] gos || gos.Length == 0 )
			return false;

		if ( SceneEditorSession.Current?.Scene is null )
			return false;

		if ( gos.Any( go => !go.IsValid() || go is Rinten.Scene ) )
			return false;

		draggedGos = gos;
		return true;
	}

	private static void MoveToSceneBottom( GameObject go, Scene scene )
	{
		if ( !go.IsValid() || go is Rinten.Scene )
			return;

		if ( go.Parent != scene )
			go.SetParent( scene, true );

		if ( go.Parent != scene )
			return;

		scene.Children.Remove( go );
		scene.Children.Add( go );
	}

	//Hash of visible tree content; descends only into open nodes, counting children of closed ones
	int HashShown( object node, bool everywhere )
	{
		if ( node is not GameObject go )
			return 0;

		var hash = HashCode.Combine( go, go.Name, go.Flags, go.Active, go.NetworkMode, go.Network.Active, go.IsPrefabInstance );

		var open = everywhere || IsOpen( node );
		var shown = 0;

		foreach ( var child in go.Children )
		{
			if ( child is not GameObject childGo || !childGo.ShouldShowInHierarchy() )
				continue;

			shown++;

			//Only where somebody can see it. Closed, the children are counted for the arrow and left alone.
			if ( open )
				hash = HashCode.Combine( hash, HashShown( childGo, everywhere ) );
		}

		return HashCode.Combine( hash, shown );
	}

	static IEnumerable<GameObject> Search( Scene scene, string query )
	{
		var tokens = Regex.Matches( query, @"(\w+):(\S+)" )
			.ToDictionary( m => m.Groups[1].Value, m => m.Groups[2].Value );

		var search = Regex.Replace( query, @"\b\w+:\S+\b", "" ).Trim();

		IEnumerable<GameObject> objects = Enumerable.Empty<GameObject>();
		if ( tokens.TryGetValue( "id", out var idFilter ) )
		{
			if ( Guid.TryParse( idFilter, out var guid ) )
			{
				var obj = scene.Directory.FindByGuid( guid );
				objects = new[] { obj };
			}
		}
		else
		{
			objects = scene.Directory.GetAll();
		}

		foreach ( var go in objects )
		{
			if ( !go.IsValid() ) continue;
			if ( go.Parent is null || go.Flags.HasFlag( GameObjectFlags.Hidden ) ) continue;
			if ( !go.Name.Contains( search, StringComparison.OrdinalIgnoreCase ) ) continue;

			if ( tokens.TryGetValue( "t", out var typeFilter ) )
			{
				var types = go.Components.GetAll().Select( x => EditorTypeLibrary.GetType( x.GetType() ) );
				if ( types.FirstOrDefault( x => x.Name.Equals( typeFilter, StringComparison.OrdinalIgnoreCase ) ) is null )
					continue;
			}

			if ( tokens.TryGetValue( "tag", out var tagFilter ) )
			{
				if ( !go.Tags.Contains( tagFilter ) )
					continue;
			}

			yield return go;
		}
	}
}
