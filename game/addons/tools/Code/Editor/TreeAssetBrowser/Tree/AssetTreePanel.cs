using System.IO;

namespace Editor.TreeBrowser;

/// <summary>Tree asset browser backed by TreePanel.</summary>
[StyleSheet( "AssetTreePanel.cs.scss" )]
public sealed class AssetTreePanel : TreePanel<AssetTreeItem>, ITreePathSource, AssetSystem.IEventListener
{
	readonly AssetTreeModel model = new();

	bool rebuilding;
	bool suppressExpandSync;
	int seenVersion;
	TreeBrowserOptions options = TreeBrowserOptions.Dock;
	HashSet<string> pendingAutoExpand = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Mode this tree runs in; rebuilds the tree when it changes.</summary>
	public TreeBrowserOptions Options
	{
		get => options;
		set
		{
			var next = value ?? TreeBrowserOptions.Dock;
			if ( ReferenceEquals( options, next ) ) return;

			options = next;
			model.Options = next;
			RefreshTree();
		}
	}

	/// <summary>Which files this tree may list; rebuilds the tree when it changes.</summary>
	public IAssetFilter Filter
	{
		get => model.Filter;
		set
		{
			var next = value ?? AllAssetsFilter.Instance;
			if ( ReferenceEquals( model.Filter, next ) ) return;

			model.Filter = next;
			RefreshTree();
		}
	}

	/// <summary>Project content or mounted games.</summary>
	public AssetTreeScope Scope
	{
		get => model.Scope;
		set
		{
			if ( model.Scope == value ) return;

			model.Scope = value;
			RefreshTree();
		}
	}

	/// <summary>Which section of the content this is showing. See AssetTreeSection.</summary>
	public AssetTreeSection Section
	{
		get => model.Showing;
		set
		{
			if ( model.Showing == value ) return;

			model.Showing = value;
			RefreshTree( skipDefaultOpen: false );
		}
	}

	/// <summary>Reloads after the chosen asset types changed under us.</summary>
	public void FilterChanged() => RefreshTree();

	public AssetTreePanel()
	{
		AddClass( "asset-tree" );
		SelectionMode = TreeSelectionMode.Multi;

		//The keyboard comes here when a row is pressed, so Delete means the files selected here - see DeleteSelected.
		//The scene's tree does not take it: selecting there leaves the keys with the viewport's tools.
		AcceptsFocus = true;
		model.Refresh( skipDefaultOpen: false );
		seenVersion = AssetExpandedPaths.SharedVersion;
		RestoreExpandedFromPaths();
		BrowserSettings.OnChanged += OnBrowserSettingsChanged;
		AssetPins.Changed += OnPinsChanged;
	}

	public override void OnDeleted()
	{
		base.OnDeleted();
		BrowserSettings.OnChanged -= OnBrowserSettingsChanged;
		AssetPins.Changed -= OnPinsChanged;
	}

	void OnBrowserSettingsChanged() => Bump();

	//A pin added or dropped anywhere shows up here at once.
	void OnPinsChanged() => RefreshTree();

	protected override bool SupportsRename => true;

	public override bool SupportsDragDrop => true;

	protected override int Revision => HashCode.Combine( model.Revision, BrowserSettings.ShowTreeIcons );

	protected override IEnumerable<AssetTreeItem> Roots => model.VisibleRoots;

	protected override IEnumerable<AssetTreeItem> GetChildren( AssetTreeItem node ) => model.GetChildren( node );

	protected override TreeItemInfo GetInfo( AssetTreeItem node )
	{
		var info = model.GetInfo( node );
		var file = node.Kind == AssetTreeItemKind.File;
		var folder = node.Kind is AssetTreeItemKind.Folder or AssetTreeItemKind.PackageFolder or AssetTreeItemKind.PackageSubFolder;

		//A folder that is open says so; a file's extension is the quiet word after its name; a section names a whole branch, so it is a heading.
		return info with
		{
			Icon = node.Kind == AssetTreeItemKind.Folder && IsOpen( node ) ? "folder_open" : info.Icon,
			HasChildren = !file && info.HasChildren,
			IconColor = node.Kind == AssetTreeItemKind.PackageFolder ? Theme.Green
				: folder ? Theme.Yellow
				: file ? AssetFileIconColors.GetColor( node )
				: null,
			IsHeading = node.Kind == AssetTreeItemKind.Section,
			Suffix = file ? node.Extension : null,
		};
	}

	//Folders all the way down: no plate under the inner ones.
	protected internal override bool PlatesInnerRows => false;

	protected internal override void OnRowClicked( AssetTreeItem node ) => NotifyFolderClicked( node );

	protected internal override bool OnRowActivated( AssetTreeItem node )
	{
		if ( node.Kind != AssetTreeItemKind.File ) return false;

		Activate( node );
		return true;
	}

	protected override void SeedOpenRoots( IEnumerable<AssetTreeItem> roots )
	{
		//Expanded paths are restored explicitly.
	}

	protected override TreeRowPanel<AssetTreeItem> CreateRow( AssetTreeItem node, TreeItemInfo info, int depth )
		=> node.Kind == AssetTreeItemKind.Spacer
			? new AssetTreeSpacerRow( this, node, info, depth )
			: new AssetTreeRow( this, node, info, depth );

	protected override void OnExpandedChanged( AssetTreeItem node, bool expanded )
	{
		if ( suppressExpandSync || rebuilding || model.IsSearchActive )
			return;

		if ( AssetExpandedPaths.SetExpanded( node.Key, expanded ) )
			seenVersion = AssetExpandedPaths.SharedVersion;
	}

	protected override void OnSelectionChanged( IReadOnlyList<AssetTreeItem> selected )
	{
		base.OnSelectionChanged( selected );

		var item = selected.FirstOrDefault();
		if ( item is null || item.Kind == AssetTreeItemKind.Spacer ) return;

		if ( item.Kind == AssetTreeItemKind.File )
		{
			var asset = item.Asset ?? AssetSystem.FindByPath( item.FullPath );
			if ( asset is null ) return;

			OnAssetHighlighted?.Invoke( asset );

			if ( !options.IsPickerMode )
			{
				EditorUtility.InspectorObject = asset;
				EditorEvent.Run( "asset.highlighted", asset );
			}

			return;
		}

		if ( item.Kind == AssetTreeItemKind.Aggregate )
		{
			NotifyBrowseLocation( item );
			return;
		}

		if ( item.Kind == AssetTreeItemKind.Section )
		{
			OnSectionSelected?.Invoke( item );
			return;
		}

		var path = item.FullPath;
		if ( string.IsNullOrEmpty( path ) ) return;

		OnFolderSelected?.Invoke( path );
		NotifyFolderPath( path );
	}

	internal void NotifyFolderClicked( AssetTreeItem item )
	{
		if ( item is null || item.Kind == AssetTreeItemKind.Spacer ) return;

		if ( item.Kind == AssetTreeItemKind.Aggregate )
		{
			if ( !IsSelected( item ) )
				Select( [item] );
			else
				NotifyBrowseLocation( item );

			return;
		}

		if ( item.Kind == AssetTreeItemKind.Section )
		{
			if ( !IsSelected( item ) )
				Select( [item] );
			else
				OnSectionSelected?.Invoke( item );

			return;
		}

		if ( item.Kind != AssetTreeItemKind.Folder || string.IsNullOrEmpty( item.FullPath ) )
			return;

		if ( !IsSelected( item ) )
			Select( [item] );
		else
			NotifyFolderPath( item.FullPath );
	}

	/// <summary>The section a branch root hangs under, for walking back up out of it.</summary>
	public AssetTreeItem FindSectionContaining( string path ) => model.FindSectionContaining( path );

	/// <summary>The children of a section the grid can list: folders, pinned folders and pinned files.</summary>
	public IReadOnlyList<AssetTreeItem> GetBrowsableChildren( AssetTreeItem item ) =>
		GetChildren( item )
			.Where( c => c.Kind is AssetTreeItemKind.Folder or AssetTreeItemKind.Aggregate or AssetTreeItemKind.File
				&& !string.IsNullOrEmpty( c.FullPath ) )
			.ToList();

	void NotifyFolderPath( string path ) => OnFolderClicked?.Invoke( path );

	void NotifyBrowseLocation( AssetTreeItem item )
	{
		if ( item.BrowseLocation is null ) return;

		OnBrowseLocationSelected?.Invoke( item.BrowseLocation );
	}

	protected override void OnContextMenu( AssetTreeItem node )
	{
		var menu = new ContextMenu();

		switch ( node.Kind )
		{
			case AssetTreeItemKind.File:
				ShowFileMenu( menu, node );
				break;
			case AssetTreeItemKind.Folder:
				ShowFolderMenu( menu, node );
				break;
			case AssetTreeItemKind.PackageFolder:
				menu.AddOption( "Refresh", "refresh", () => model.Invalidate( node ) );
				break;
		}

		if ( node.IsPinned )
		{
			if ( menu.HasOptions )
				menu.AddSeparator();

			menu.AddOption( "Unpin", "close", () => Unpin( node.FullPath ) );
		}

		if ( !menu.HasOptions ) return;

		menu.OpenAtCursor( this );
	}

	/// <summary>Delete, while the tree has the keyboard: the selected files and folders, asked first.</summary>
	[Shortcut( "editor.delete", "DEL" )]
	public void DeleteSelected()
	{
		var paths = Selected
			.Where( x => x.Kind is AssetTreeItemKind.File or AssetTreeItemKind.Folder && !x.IsRoot )
			.Select( x => x.FullPath )
			.Where( x => !string.IsNullOrEmpty( x ) )
			.ToList();

		AssetContextMenuHelper.DeletePaths( this, paths, Refresh );
	}

	//Below the last row: the same menu the grid gives its empty space, for the folder that is selected - or the
	//one a selected file is in, or the project's own content where nothing is. There was no menu here at all.
	protected override void OnBodyContextMenu()
	{
		var folder = BodyFolder();
		if ( string.IsNullOrEmpty( folder ) ) return;

		var menu = new ContextMenu();
		AssetContextMenuHelper.BuildEmptySpaceMenu( menu, folder, Refresh );
		menu.OpenAtCursor( this );
	}

	string BodyFolder()
	{
		foreach ( var item in Selected )
		{
			if ( string.IsNullOrEmpty( item.FullPath ) ) continue;

			if ( item.Kind == AssetTreeItemKind.Folder && Directory.Exists( item.FullPath ) )
				return item.FullPath;

			if ( item.Kind == AssetTreeItemKind.File && Path.GetDirectoryName( item.FullPath ) is { } parent && Directory.Exists( parent ) )
				return parent;
		}

		var assets = Project.Current?.GetAssetsPath();
		return !string.IsNullOrEmpty( assets ) && Directory.Exists( assets ) ? assets : null;
	}

	static void Pin( string path ) => AssetPins.Add( path );

	static void Unpin( string path ) => AssetPins.Remove( path );

	protected override bool CanDrag( AssetTreeItem node ) => node.CanDrag;

	protected override DragData CreateDragData( TreeRowPanel<AssetTreeItem> row )
	{
		var node = row.Node;
		if ( node.Kind is not (AssetTreeItemKind.File or AssetTreeItemKind.Folder) )
			return null;

		var path = node.FullPath;
		var paths = AssetMover.GetDragPaths( GetSelectedPaths(), path );

		object payload = null;
		if ( node.Kind == AssetTreeItemKind.File )
		{
			var assets = paths
				.Select( AssetSystem.FindByPath )
				.Where( x => x is not null && !x.IsDeleted )
				.ToArray();

			payload = assets.Length switch
			{
				0 => null,
				1 => assets[0],
				_ => assets
			};
		}

		var data = new DragData();
		if ( payload is not null )
			data.Object = payload;

		data.Text = string.Join( "\n", paths.Select( AssetMover.FileUri ) );
		data.Url = new Uri( AssetMover.FileUri( paths[0] ) );
		return data;
	}

	protected override DropAction OnDragDrop( AssetTreeItem target, DragData data, TreeDropEdge edge, bool isDrop )
	{
		if ( target.Kind != AssetTreeItemKind.Folder || edge != TreeDropEdge.Inside )
			return DropAction.Ignore;

		if ( data.Object is GameObject[] gos && gos.Length > 0 )
		{
			if ( isDrop )
				AssetContextMenuHelper.CreatePrefabFromGameObjects( gos, target.FullPath, Refresh );

			return DropAction.Copy;
		}

		if ( !data.HasFileOrFolder )
			return DropAction.Ignore;

		var anyExternal = data.Files.Any( AssetContextMenuHelper.IsExternalSource );
		var dropAction = (Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Ctrl ) || anyExternal)
			? DropAction.Copy
			: DropAction.Move;

		if ( !isDrop )
			return dropAction;

		AssetMover.RunAndReport( data.Files, target.FullPath, dropAction == DropAction.Copy, Refresh );
		return dropAction;
	}

	protected override void OnRenamed( AssetTreeItem node, string text, AssetTreeItem[] selection )
	{
		if ( node.Kind == AssetTreeItemKind.File )
		{
			var newName = text;
			if ( !Path.HasExtension( newName ) )
				newName += node.Extension;

			AssetMover.RenameAndReport( node.FullPath, newName, Refresh );
			return;
		}

		if ( node.Kind == AssetTreeItemKind.Folder )
			AssetMover.RenameAndReport( node.FullPath, text, Refresh );
	}

	public IReadOnlyList<string> GetSelectedPaths()
	{
		return Selected
			.Where( x => x.Kind is AssetTreeItemKind.File or AssetTreeItemKind.Folder )
			.Select( x => x.FullPath )
			.Where( x => !string.IsNullOrEmpty( x ) )
			.ToList();
	}

	public void ApplySearch( string text )
	{
		if ( string.IsNullOrEmpty( text ) )
		{
			model.ClearSearch();
			Refresh();
			return;
		}

		model.ApplySearch( text );
		Bump();
		OpenSearchMatches();
	}

	public void RefreshTree( bool skipDefaultOpen = true )
	{
		rebuilding = true;

		try
		{
			suppressExpandSync = true;
			model.Refresh( skipDefaultOpen );
			RestoreExpandedFromPaths();
		}
		finally
		{
			suppressExpandSync = false;
			rebuilding = false;
		}

		seenVersion = AssetExpandedPaths.SharedVersion;
		Bump();
	}

	/// <summary>Opens the tree down to a file or folder and picks it. False where the tree has no such node yet - it is still being built.</summary>
	public bool NavigateToFile( string path )
	{
		var node = model.FindNode( path );
		if ( node is null ) return false;

		OpenTo( node );
		Select( [node] );
		ScrollTo( node );
		return true;
	}

	public void SyncSharedExpandState()
	{
		if ( seenVersion == AssetExpandedPaths.SharedVersion || model.IsSearchActive )
			return;

		RefreshTree( skipDefaultOpen: true );
	}

	/// <summary>Opens the tree down to a folder and picks it without raising the pick, as when following another browser.</summary>
	public void FollowFolder( string path )
	{
		var node = model.FindNode( path );
		if ( node is null || IsSelected( node ) ) return;

		OpenTo( node );
		Select( [node], skipEvents: true );
		ScrollTo( node );
	}

	void AssetSystem.IEventListener.OnAssetThumbGenerated( Asset asset ) => Bump();

	void AssetSystem.IEventListener.OnAssetChanged( Asset asset ) => model.AssetChanged( asset );

	public Action<Asset> OnAssetSelected;
	public Action<Asset> OnAssetHighlighted;
	public Action<Asset> OnAssetActivated;
	public Action<string> OnFileSelected;
	public Action<string> OnFolderSelected;
	public Action<string> OnFolderClicked;
	public Action<AssetTreeItem> OnSectionSelected;
	public Action<AssetBrowser.Location> OnBrowseLocationSelected;

	internal void Activate( AssetTreeItem node )
	{
		if ( node.Kind != AssetTreeItemKind.File )
			return;

		var asset = node.Asset ?? AssetSystem.FindByPath( node.FullPath );
		if ( asset is not null )
		{
			OnAssetSelected?.Invoke( asset );
			OnAssetActivated?.Invoke( asset );
			return;
		}

		OnFileSelected?.Invoke( node.FullPath );

		if ( !options.IsPickerMode )
			EditorUtility.OpenFolder( node.FullPath );
	}

	void Refresh() => RefreshTree( skipDefaultOpen: true );

	void RestoreExpandedFromPaths()
	{
		pendingAutoExpand.Clear();
		var paths = AssetExpandedPaths.Snapshot();

		foreach ( var path in paths )
			pendingAutoExpand.Add( path );

		suppressExpandSync = true;

		try
		{
			ApplyExpandedRecursive( model.Roots, paths );
		}
		finally
		{
			suppressExpandSync = false;
		}
	}

	void ApplyExpandedRecursive( IEnumerable<AssetTreeItem> items, IReadOnlyCollection<string> paths )
	{
		foreach ( var item in items )
		{
			var shouldExpand = paths.Contains( item.Key );
			var hasChildToExpand = !shouldExpand && paths.Any( p =>
				item.Kind == AssetTreeItemKind.Folder &&
				(p.StartsWith( item.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) ||
				 p.StartsWith( item.FullPath + "/", StringComparison.OrdinalIgnoreCase ) ) );

			if ( shouldExpand || hasChildToExpand )
			{
				Open( item );
				model.GetChildren( item );
				ApplyExpandedRecursive( item.Children, paths );
			}
		}
	}

	void OpenSearchMatches()
	{
		foreach ( var root in model.Roots )
			OpenSearchRecursive( root );
	}

	void OpenSearchRecursive( AssetTreeItem item )
	{
		if ( item.Kind is AssetTreeItemKind.Folder or AssetTreeItemKind.Section or AssetTreeItemKind.PackageFolder or AssetTreeItemKind.PackageSubFolder )
		{
			var children = model.GetChildren( item ).ToList();
			var anyVisible = children.Count > 0;

			if ( anyVisible )
				Open( item );

			foreach ( var child in children )
				OpenSearchRecursive( child );
		}
	}

	void ShowFileMenu( ContextMenu menu, AssetTreeItem node )
	{
		var selectedFiles = Selected
			.Where( x => x.Kind == AssetTreeItemKind.File )
			.ToList();

		if ( selectedFiles.Count > 1 && selectedFiles.Contains( node ) )
		{
			var items = selectedFiles.Select( n => (n.FullPath, n.Asset) ).ToList();
			AssetContextMenuHelper.BuildMultiFileMenu( menu, items, Refresh, owner: this );
			return;
		}

		AssetContextMenuHelper.BuildFileMenu(
			menu,
			node.FullPath,
			node.Asset,
			onRename: () => BeginRename( node ),
			onChanged: Refresh,
			owner: this );

		AddPinOption( menu, node );
	}

	//Pinning a node, unless it is already a pin - those are unpinned further up.
	void AddPinOption( ContextMenu menu, AssetTreeItem node )
	{
		if ( node.IsPinned || string.IsNullOrEmpty( node.FullPath ) )
			return;

		menu.AddSeparator();

		if ( AssetPins.Contains( node.FullPath ) )
			menu.AddOption( "Unpin", "close", () => Unpin( node.FullPath ) );
		else
			menu.AddOption( "Pin", "push_pin", () => Pin( node.FullPath ) );
	}

	void ShowFolderMenu( ContextMenu menu, AssetTreeItem node )
	{
		AssetContextMenuHelper.BuildFolderMenu(
			menu,
			node.FullPath,
			node.Label,
			node.IsRoot,
			onOpen: () => Open( node ),
			onRename: () => ShowFolderRenameDialog( node ),
			onRefresh: Refresh,
			onDeleted: Refresh,
			owner: this );

		AddPinOption( menu, node );
	}

	void ShowFolderRenameDialog( AssetTreeItem node )
	{
		EditorModal.Prompt( this, "Rename Folder", null,
			newName => AssetMover.RenameAndReport( node.FullPath, newName, Refresh ),
			okay: "Rename", initial: node.Label, icon: "folder" );
	}

	public void ExpandAll()
	{
		foreach ( var root in model.Roots )
			ExpandRecursive( root );

		Bump();
	}

	public void CollapseAll()
	{
		foreach ( var root in model.Roots )
			Close( root, recursive: true );

		Bump();
	}

	void ExpandRecursive( AssetTreeItem item )
	{
		if ( item.HasChildren )
		{
			Open( item );
			model.GetChildren( item );

			foreach ( var child in item.Children )
				ExpandRecursive( child );
		}
	}
}
