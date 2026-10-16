using Editor;
using Rinten.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Editor.TreeBrowser;

[StyleSheet( "IconGridCanvasPanel.cs.scss" )]
public sealed class IconGridCanvasPanel : EditorPanel, AssetSystem.IEventListener, IEditorDropTarget
{
	public bool AcceptEditorDrops => true;

	public Action<string> OnFolderNavigate;
	public Action<AssetBrowser.Location> OnLocationNavigate;

	/// <summary>Single click landed on an asset.</summary>
	public Action<Asset> OnAssetHighlighted;

	/// <summary>Double click landed on an asset.</summary>
	public Action<Asset> OnAssetActivated;

	/// <summary>Double click landed on a file that is no asset - a razor panel under Code. Its full path.</summary>
	public Action<string> OnFileActivated;

	/// <summary>The set of selected assets changed.</summary>
	public Action<IReadOnlyList<Asset>> OnSelectionChanged;

	readonly IconGridBodyPanel body;
	readonly Panel content;
	readonly Rinten.UI.Label emptyLabel;
	readonly List<IconGridItemPanel> _rows = new();

	bool[] _slotLive = [];
	float _contentHeight = -1f;

	TreeBrowserOptions _options = TreeBrowserOptions.Dock;
	IAssetFilter _filter = AllAssetsFilter.Instance;
	List<GridItem> _allItems = new();
	List<GridItem> _items = new();
	string _searchFilter = "";
	string _currentFolder;
	AssetBrowser.Location _currentLocation;

	int _dragHoveredIndex = -1;
	int _selectedIndex = -1;
	HashSet<int> _selectedIndices = new();
	int _selectionAnchor = -1;
	int _pendingInspectorIndex = -1;

	Vector2 _dragStartScreen;
	bool _isDragging;
	int _dragItemIndex = -1;

	public IconGridCanvasPanel()
	{
		AddClass( "icon-grid-canvas" );
		CanDragScroll = false;

		//Takes keyboard focus on presses in the grid so shortcuts like Delete land here, not the scene
		AcceptsFocus = true;

		body = AddChild<IconGridBodyPanel>( "body" );
		body.Grid = this;
		body.CanDragScroll = false;

		content = body.AddChild<Panel>( "content" );

		emptyLabel = AddChild<Rinten.UI.Label>( "empty" );
		emptyLabel.Text = "Select a folder or cloud category";
		emptyLabel.AddClass( "empty" );
	}

	/// <summary>Mode this grid runs in; re-filters what is on screen when it changes.</summary>
	public TreeBrowserOptions Options
	{
		get => _options;
		set
		{
			var next = value ?? TreeBrowserOptions.Dock;
			if ( ReferenceEquals( _options, next ) ) return;

			_options = next;
			ApplyFilter();
		}
	}

	/// <summary>Which files this grid may show; re-filters what is on screen when it changes.</summary>
	public IAssetFilter Filter
	{
		get => _filter;
		set
		{
			var next = value ?? AllAssetsFilter.Instance;
			if ( ReferenceEquals( _filter, next ) ) return;

			_filter = next;
			ApplyFilter();
		}
	}

	/// <summary>Re-filters after the chosen asset types changed under us.</summary>
	public void FilterChanged() => ApplyFilter();

	/// <summary>Empties the grid: nothing is being browsed.</summary>
	public void Clear()
	{
		_currentFolder = null;
		_currentLocation = null;
		_allItems.Clear();
		ApplyFilter();
	}

	//Ctrl and shift extend the selection.
	bool AllowMultiselect => !_options.IsPickerMode || _options.EnableMultiselect;

	/// <summary>Assets currently selected, in grid order.</summary>
	public IReadOnlyList<Asset> GetSelectedAssets() =>
		_selectedIndices
			.Where( i => i >= 0 && i < _items.Count )
			.OrderBy( i => i )
			.Select( i => _items[i].Asset )
			.Where( x => x is not null )
			.ToList();

	public override void OnMouseWheel( Vector2 value )
	{
		if ( (Application.KeyboardModifiers & KeyboardModifiers.Ctrl) == 0 )
		{
			base.OnMouseWheel( value );
			return;
		}

		var order = new[]
		{
			AssetListViewMode.List,
			AssetListViewMode.SmallIcons,
			AssetListViewMode.MediumIcons,
			AssetListViewMode.LargeIcons,
		};

		var at = Array.IndexOf( order, BrowserSettings.ViewMode );
		if ( at < 0 ) at = 2;

		var next = Math.Clamp( at + (value.y > 0 ? 1 : -1), 0, order.Length - 1 );

		if ( order[next] != BrowserSettings.ViewMode )
		{
			BrowserSettings.ViewMode = order[next];
			ViewModeChanged();
		}
	}

	public void ViewModeChanged()
	{
		foreach ( var row in _rows )
		{
			row.ApplyViewMode();
			row.RefreshLabels();
		}

		//Tile size drives the whole layout, so everything has to be placed again.
		_contentHeight = -1f;
		ParkAll();
		UpdateVisibleTiles();
	}

	//Widget-level, so it is asked only while the keyboard is in here and before the scene's own Delete, which is the window's: a file selected here is never answered by removing whatever is selected in the scene.
	/// <summary>Delete, while the grid has the keyboard - see the constructor, which is what lets it.</summary>
	[Shortcut( "editor.delete", "DEL" )]
	public void DeleteSelected()
	{
		var chosen = _selectedIndices
			.Where( i => i >= 0 && i < _items.Count )
			.OrderBy( i => i )
			.Select( i => _items[i] )
			.ToList();

		if ( chosen.Count == 0 && _selectedIndex >= 0 && _selectedIndex < _items.Count )
			chosen.Add( _items[_selectedIndex] );

		if ( chosen.Count == 0 ) return;

		//The same question and the same removal as the tree's Delete - see AssetContextMenuHelper.DeletePaths.
		AssetContextMenuHelper.DeletePaths( this, chosen.Select( x => x.Path ).ToList(), () =>
		{
			_selectedIndices.Clear();
			_selectedIndex = -1;
			Reload();
		} );
	}

	public void LoadFolder( string folderPath )
	{
		_currentFolder = folderPath;
		_currentLocation = null;

		var items = new List<GridItem>();

		try
		{
			foreach ( var dir in Directory.GetDirectories( folderPath ).OrderBy( d => Path.GetFileName( d ), StringComparer.OrdinalIgnoreCase ) )
			{
				var dirInfo = new DirectoryInfo( dir );
				if ( dirInfo.Attributes.HasFlag( FileAttributes.Hidden ) ) continue;
				if ( dirInfo.Name.Equals( "obj", StringComparison.OrdinalIgnoreCase ) ) continue;
				if ( dirInfo.Name.StartsWith( "." ) ) continue;

				items.Add( new GridItem
				{
					Path = dir,
					Name = dirInfo.Name,
					IsFolder = true,
				} );
			}

			foreach ( var file in Directory.GetFiles( folderPath ).OrderBy( f => Path.GetFileName( f ), StringComparer.OrdinalIgnoreCase ) )
			{
				var fileInfo = new FileInfo( file );
				if ( IsJunkFile( fileInfo ) ) continue;

				items.Add( FileGridItem( fileInfo ) );
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"Error loading folder: {ex.Message}" );
		}

		_allItems = KeepExisting( items );

		ApplyFilter();
	}

	/// <summary>Shows whatever a browse location holds: a disk folder, a mount, Recents or Everything.</summary>
	public void LoadBrowseLocation( AssetBrowser.Location location )
	{
		if ( location is null ) return;

		if ( location is DiskLocation disk )
		{
			LoadFolder( disk.Path );
			return;
		}

		_currentFolder = null;
		_currentLocation = location;
		_allItems = KeepExisting( BuildLocationItems( location ) );

		ApplyFilter();
	}

	//Reuses existing items for unchanged entries so tiles keep previews when a folder is re-read
	List<GridItem> KeepExisting( List<GridItem> fresh )
	{
		if ( _allItems.Count == 0 ) return fresh;

		var had = new Dictionary<string, GridItem>( StringComparer.OrdinalIgnoreCase );

		foreach ( var item in _allItems )
		{
			if ( item.Path is not null ) had.TryAdd( item.Path, item );
		}

		for ( var i = 0; i < fresh.Count; i++ )
		{
			var item = fresh[i];

			if ( item.Path is null || !had.TryGetValue( item.Path, out var kept ) ) continue;
			if ( kept.IsFolder != item.IsFolder || !ReferenceEquals( kept.Asset, item.Asset ) ) continue;

			kept.Name = item.Name;
			kept.Location = item.Location;
			fresh[i] = kept;
		}

		return fresh;
	}

	//Everything and Recents are lists of assets rather than a folder, so what changes them is the asset system, not
	//a folder watch: a file registered or deleted is in them or out of them now, not the next time they are opened.
	void AssetSystem.IEventListener.OnAssetSystemChanges()
	{
		if ( _currentLocation is not ( EverythingLocation or RecentsLocation ) ) return;

		LoadBrowseLocation( _currentLocation );
	}

	static List<GridItem> BuildLocationItems( AssetBrowser.Location location )
	{
		var items = new List<GridItem>();

		var directories = Safely( () => location.GetDirectories(), "directories" )
			.OrderBy( x => x.Name, StringComparer.OrdinalIgnoreCase );

		foreach ( var directory in directories )
		{
			items.Add( new GridItem
			{
				Path = directory.Path,
				Name = directory.Name,
				IsFolder = true,
				Location = directory
			} );
		}

		foreach ( var file in Safely( () => location.GetFiles(), "files" ) )
		{
			try
			{
				if ( IsJunkFile( file ) ) continue;

				items.Add( FileGridItem( file ) );
			}
			catch ( Exception ex )
			{
				Log.Warning( $"Skipping '{file?.Name}': {ex.Message}" );
			}
		}

		return items;
	}

	//Walks a location's listing without one bad entry costing every entry after it. Everything and Recents hand back
	//a FileInfo per asset in the project, and a cloud or package asset has a path the filesystem will not answer for.
	static IEnumerable<T> Safely<T>( Func<IEnumerable<T>> source, string what )
	{
		IEnumerator<T> walker;

		try
		{
			walker = source().GetEnumerator();
		}
		catch ( Exception ex )
		{
			Log.Warning( $"Error listing location {what}: {ex.Message}" );
			yield break;
		}

		while ( true )
		{
			T current;

			try
			{
				if ( !walker.MoveNext() ) break;
				current = walker.Current;
			}
			catch ( Exception ex )
			{
				Log.Warning( $"Stopped listing location {what}: {ex.Message}" );
				break;
			}

			yield return current;
		}
	}

	static GridItem FileGridItem( FileInfo file )
	{
		var path = file.ToString();

		return new GridItem
		{
			Path = path,
			Name = file.Name,
			IsFolder = false,
			Asset = AssetSystem.FindByPath( path ),
			Extension = file.Extension.ToLowerInvariant()
		};
	}

	static bool IsJunkFile( FileInfo file )
	{
		if ( file is null ) return true;

		var name = file.Name;

		if ( name.StartsWith( "." ) ) return true;
		if ( name.Contains( ".generated", StringComparison.OrdinalIgnoreCase ) ) return true;
		if ( name.EndsWith( ".meta", StringComparison.OrdinalIgnoreCase ) ) return true;

		try
		{
			if ( file.Exists && file.Attributes.HasFlag( FileAttributes.Hidden ) ) return true;
		}
		catch
		{
			//A virtual path the filesystem will not answer for is still worth showing.
		}

		var path = file.ToString();
		return name.EndsWith( "_c" ) && path.Length > 2 && File.Exists( path[..^2] );
	}

	/// <summary>Reloads whatever this grid is pointed at.</summary>
	public void Reload()
	{
		if ( _currentLocation is not null )
			LoadBrowseLocation( _currentLocation );
		else if ( !string.IsNullOrEmpty( _currentFolder ) )
			LoadFolder( _currentFolder );
	}

	/// <summary>Lists what a tree section holds: folders first, then any files pinned into it.</summary>
	public void LoadTreeItems( IReadOnlyList<AssetTreeItem> items )
	{
		_currentFolder = null;
		_currentLocation = null;
		_allItems = items
			.Where( x => !string.IsNullOrEmpty( x.FullPath ) )
			.OrderBy( x => x.Kind == AssetTreeItemKind.File )
			.ThenBy( x => x.Label, StringComparer.OrdinalIgnoreCase )
			.Select( ToGridItem )
			.ToList();

		ApplyFilter();
	}

	static GridItem ToGridItem( AssetTreeItem item )
	{
		if ( item.Kind != AssetTreeItemKind.File )
		{
			return new GridItem
			{
				Path = item.FullPath,
				Name = item.Label,
				IsFolder = true,
			};
		}

		return new GridItem
		{
			Path = item.FullPath,
			Name = Path.GetFileName( item.FullPath ),
			IsFolder = false,
			Asset = item.Asset ?? AssetSystem.FindByPath( item.FullPath ),
			Extension = item.Extension
		};
	}

	public void ApplySearch( string text )
	{
		var filter = text?.Trim().ToLowerInvariant() ?? "";

		if ( filter == _searchFilter ) return;

		_searchFilter = filter;
		ApplyFilter();
	}

	void ApplyFilter( bool resetSelection = true )
	{
		var nextItems = _allItems
			.Where( MatchesSearch )
			.Where( MatchesTypeFilter )
			.ToList();

		if ( ItemsMatch( _items, nextItems ) )
			return;

		_items = nextItems;

		if ( resetSelection )
		{
			_dragHoveredIndex = -1;
			_selectedIndex = -1;
			_selectedIndices.Clear();
			_selectionAnchor = -1;
		}

		SyncItems();
	}

	bool MatchesSearch( GridItem item ) =>
		string.IsNullOrEmpty( _searchFilter ) || item.Name.Contains( _searchFilter, StringComparison.OrdinalIgnoreCase );

	bool MatchesTypeFilter( GridItem item ) => item.IsFolder || ( _options.AcceptsFile( item.Path ) && _filter.Accepts( item.Asset ) );

	//Compares by object, not path, so an item that gained an asset is not treated as unchanged
	static bool ItemsMatch( IReadOnlyList<GridItem> a, IReadOnlyList<GridItem> b )
	{
		if ( a.Count != b.Count ) return false;

		for ( var i = 0; i < a.Count; i++ )
		{
			if ( !ReferenceEquals( a[i], b[i] ) )
				return false;
		}

		return true;
	}

	void SyncItems()
	{
		var hasItems = _items.Count > 0;
		body.SetClass( "hidden", !hasItems );
		emptyLabel.SetClass( "hidden", hasItems );

		UpdateVisibleTiles();
	}

	//How many rows of tiles are kept either side of the viewport.
	const int Overscan = 2;

	//How many previews may be fetched in one frame.
	const int ThumbsPerFrame = 6;

	//Builds only the tiles the viewport can reach, and moves them as it scrolls. Everything lists the whole project. A
	//panel per asset is tens of thousands of panels laid out every frame, so what exists is bounded by the window instead.
	void UpdateVisibleTiles()
	{
		if ( content is null || !content.IsValid() )
			return;

		var width = content.Box.Rect.Width;
		if ( width <= 1f )
			return;

		var metrics = GridMetrics.For( BrowserSettings.ViewMode, width );
		SetContentHeight( metrics.TotalHeight( _items.Count ) );

		if ( _items.Count == 0 )
		{
			ParkAll();
			return;
		}

		var step = metrics.Step;
		var firstRow = Math.Max( 0, (int)(body.ScrollOffset.y / step) - Overscan );
		var lastRow = Math.Min( metrics.RowCount( _items.Count ) - 1, (int)((body.ScrollOffset.y + body.Box.Rect.Height) / step) + Overscan );

		var first = firstRow * metrics.Columns;
		var last = Math.Min( _items.Count - 1, (lastRow + 1) * metrics.Columns - 1 );

		EnsurePool( last - first + 1, metrics.Columns );

		Array.Clear( _slotLive );

		for ( var index = first; index <= last; index++ )
		{
			var slot = index % _rows.Count;
			var row = _rows[slot];

			row.Show( _items[index], index );
			row.Place( metrics.LeftOf( index ), metrics.TopOf( index ) );

			_slotLive[slot] = true;
		}

		for ( var slot = 0; slot < _rows.Count; slot++ )
		{
			if ( !_slotLive[slot] )
				_rows[slot].Park();
		}

		RefreshRowVisuals();
	}

	//Grows the pool to cover the viewport, in whole rows so a tile keeps its slot while it stays on screen and does not reload its preview on every scroll.
	void EnsurePool( int needed, int columns )
	{
		var rows = Math.Max( 1, (needed + columns - 1) / columns );
		var wanted = rows * columns;

		while ( _rows.Count < wanted )
		{
			var row = new IconGridItemPanel( this );
			content.AddChild( row );
			_rows.Add( row );
		}

		if ( _slotLive.Length != _rows.Count )
			_slotLive = new bool[_rows.Count];
	}

	void ParkAll()
	{
		foreach ( var row in _rows )
			row.Park();
	}

	void SetContentHeight( float height )
	{
		if ( Math.Abs( _contentHeight - height ) < 0.5f )
			return;

		_contentHeight = height;
		content.Style.Height = Length.Pixels( height );
	}

	//Fetches previews for the tiles that exist, a few per frame.
	void PumpThumbnails()
	{
		var budget = ThumbsPerFrame;

		foreach ( var row in _rows )
		{
			if ( !row.Live || !row.NeedsThumb ) continue;

			row.LoadThumb();

			if ( --budget == 0 ) break;
		}
	}

	internal void RefreshRowVisuals()
	{
		foreach ( var row in _rows )
		{
			if ( !row.Live ) continue;

			row.Switch( PseudoClass.Focus, _selectedIndices.Contains( row.Index ) );
		}
	}

	//Attaches a newly registered asset to its on-screen item, which may predate the registration
	void AssetSystem.IEventListener.OnAssetChanged( Asset asset )
	{
		if ( asset is null || asset.IsDeleted || string.IsNullOrEmpty( asset.AbsolutePath ) ) return;

		var path = asset.AbsolutePath.Replace( '\\', '/' );
		var changed = false;

		foreach ( var item in _allItems )
		{
			if ( item.IsFolder || ReferenceEquals( item.Asset, asset ) ) continue;
			if ( !string.Equals( item.Path?.Replace( '\\', '/' ), path, StringComparison.OrdinalIgnoreCase ) ) continue;

			item.Asset = asset;
			changed = true;
		}

		if ( !changed ) return;

		foreach ( var row in _rows )
		{
			if ( row.Live && row.Item is { IsFolder: false } item && item.Asset == asset )
				row.Refresh();
		}

		//A type filter passes an item by its asset, so one that had none may pass now.
		ApplyFilter( resetSelection: false );
	}

	void AssetSystem.IEventListener.OnAssetThumbGenerated( Asset asset )
	{
		foreach ( var row in _rows )
		{
			if ( row.Live && row.Item is { IsFolder: false } item && item.Asset == asset )
				row.InvalidateThumb();
		}
	}

	int GetItemAtPosition( Vector2 screenPos )
	{
		foreach ( var row in _rows )
		{
			if ( !row.Live || !row.IsInside( screenPos ) )
				continue;

			//A tile keeps the index it was last shown with, so one still on screen from before a reload can name an item that is not there any more.
			return row.Index < _items.Count ? row.Index : -1;
		}

		return -1;
	}

	internal void ClearSelection()
	{
		_selectedIndices.Clear();
		_selectedIndex = -1;
		_selectionAnchor = -1;
		_pendingInspectorIndex = -1;
		RefreshRowVisuals();
		OnSelectionChanged?.Invoke( [] );
	}

	/// <summary>Selects the item at this path, if the grid is showing it.</summary>
	public bool SelectByPath( string fullPath )
	{
		if ( string.IsNullOrEmpty( fullPath ) ) return false;

		var index = _items.FindIndex( x => string.Equals( x.Path, fullPath, StringComparison.OrdinalIgnoreCase ) );
		if ( index < 0 ) return false;

		_selectedIndices.Clear();
		_selectedIndices.Add( index );
		_selectedIndex = index;
		_selectionAnchor = index;

		RefreshRowVisuals();
		ScrollToIndex( index );
		OnSelectionChanged?.Invoke( GetSelectedAssets() );
		return true;
	}

	//Made on first use: a grid already open across a hotload never runs a field's initializer
	ScrollFollower scroll;

	/// <summary>Keeps this grid scrolled with the other browsers' grids - see ScrollFollower.</summary>
	public void SyncScroll() => ( scroll ??= new( AssetBrowserPlace.GridScroll ) ).Tick( body.HasHovered, body.ScrollOffset.y, TopItem, ScrollToItem, () => body.ScrollOffset.y );

	//The file at the top of the view, and how far into its row the view starts
	(string Key, float Into)? TopItem()
	{
		var width = content?.Box.Rect.Width ?? 0f;
		if ( width <= 1f || _items.Count == 0 ) return null;

		var metrics = GridMetrics.For( BrowserSettings.ViewMode, width );
		var row = (int)( body.ScrollOffset.y / metrics.Step );
		var index = row * metrics.Columns;

		if ( index < 0 || index >= _items.Count ) return null;

		return (_items[index].Path, body.ScrollOffset.y - ( row * metrics.Step ));
	}

	//Puts a file's row that far above the top, as far as there is room. False until the grid has it and is laid out
	bool ScrollToItem( string path, float into )
	{
		var width = content?.Box.Rect.Width ?? 0f;
		if ( width <= 1f ) return false;

		var index = _items.FindIndex( x => string.Equals( x.Path, path, StringComparison.OrdinalIgnoreCase ) );
		if ( index < 0 ) return false;

		var metrics = GridMetrics.For( BrowserSettings.ViewMode, width );
		var top = metrics.TopOf( index ) + into;

		body.ScrollOffset = new Vector2( 0, top.Clamp( 0, Math.Max( 0f, body.ScrollSize.y ) ) );
		return true;
	}

	void ScrollToIndex( int index )
	{
		if ( index < 0 || index >= _items.Count || content is null ) return;

		var width = content.Box.Rect.Width;
		if ( width <= 1f ) return;

		var metrics = GridMetrics.For( BrowserSettings.ViewMode, width );
		var top = metrics.TopOf( index );

		body.ScrollOffset = new Vector2( 0, top.Clamp( 0, Math.Max( 0f, body.ScrollSize.y ) ) );
	}

	internal void BeginItemPress( int index, MousePanelEvent e )
	{
		_dragStartScreen = Application.CursorPosition;
		_isDragging = false;
		_dragItemIndex = index;
		_pendingInspectorIndex = -1;

		if ( index < 0 || index >= _items.Count )
			return;

		if ( AllowMultiselect && (Application.KeyboardModifiers & KeyboardModifiers.Shift) != 0 && _selectionAnchor >= 0 )
		{
			_selectedIndices.Clear();
			var from = Math.Min( _selectionAnchor, index );
			var to = Math.Max( _selectionAnchor, index );
			for ( var i = from; i <= to; i++ )
				_selectedIndices.Add( i );

			_selectedIndex = index;
		}
		else if ( AllowMultiselect && (Application.KeyboardModifiers & KeyboardModifiers.Ctrl) != 0 )
		{
			if ( _selectedIndices.Contains( index ) )
				_selectedIndices.Remove( index );
			else
				_selectedIndices.Add( index );

			_selectedIndex = index;
			_selectionAnchor = index;
		}
		else
		{
			_selectedIndices.Clear();
			_selectedIndices.Add( index );
			_selectedIndex = index;
			_selectionAnchor = index;
		}

		var pressed = _items[index];
		if ( !pressed.IsFolder && pressed.Asset is not null )
			_pendingInspectorIndex = index;

		NotifyHighlight( index );
		RefreshRowVisuals();
		OnSelectionChanged?.Invoke( GetSelectedAssets() );
	}

	internal void EndItemPress( int index, MousePanelEvent e )
	{
		if ( _dragItemIndex < 0 )
			return;

		//The inspector waits for the press to end as a click: pointed at the file on the
		//way down, it takes away whatever was being inspected the moment a drag begins.
		if ( !_isDragging && _pendingInspectorIndex == index )
			ShowInInspector( index );

		_isDragging = false;
		_pendingInspectorIndex = -1;
		_dragItemIndex = -1;
		RefreshRowVisuals();
	}

	//Points the inspector at an item, unless this grid is picking one for somebody.
	void ShowInInspector( int index )
	{
		if ( _options.IsPickerMode ) return;
		if ( index < 0 || index >= _items.Count ) return;

		var item = _items[index];
		if ( item.IsFolder || item.Asset is null ) return;

		EditorUtility.InspectorObject = item.Asset;
		EditorEvent.Run( "asset.highlighted", item.Asset );
	}

	void NotifyHighlight( int index )
	{
		if ( index < 0 || index >= _items.Count )
			return;

		var item = _items[index];
		if ( item.IsFolder || item.Asset is null )
			return;

		OnAssetHighlighted?.Invoke( item.Asset );
	}

	internal void TryStartItemDrag( int index )
	{
		if ( _isDragging || _dragItemIndex != index )
			return;

		_isDragging = true;
		_pendingInspectorIndex = -1;
		StartDrag( index );
	}

	internal void ActivateItem( int index )
	{
		if ( index < 0 || index >= _items.Count ) return;

		var item = _items[index];

		if ( item.IsFolder )
		{
			NavigateTo( item );
			return;
		}

		if ( item.Asset is not null )
			OnAssetActivated?.Invoke( item.Asset );
		else
			OnFileActivated?.Invoke( item.Path );
	}

	void NavigateTo( GridItem folder )
	{
		if ( folder.Location is not null )
			OnLocationNavigate?.Invoke( folder.Location );
		else
			OnFolderNavigate?.Invoke( folder.Path );
	}

	public override void Tick()
	{
		base.Tick();

		UpdateVisibleTiles();
		PumpThumbnails();

		if ( _dragItemIndex >= 0 )
		{
			if ( (Application.MouseButtons & MouseButtons.Left) != 0 )
			{
				if ( !_isDragging && Application.CursorPosition.Distance( _dragStartScreen ) > 4 )
					TryStartItemDrag( _dragItemIndex );
			}
			else
				EndItemPress( _dragItemIndex, null );
		}
	}

	void StartDrag( int index )
	{
		if ( index < 0 || index >= _items.Count ) return;

		var indices = _selectedIndices.Contains( index )
			? _selectedIndices.Where( i => i >= 0 && i < _items.Count ).OrderBy( i => i ).ToList()
			: new List<int> { index };

		var paths = indices.Select( i => _items[i].Path ).ToList();

		var assets = indices
			.Select( i => _items[i].Asset )
			.Where( x => x is not null && !x.IsDeleted )
			.ToArray();

		object payload = assets.Length switch
		{
			0 => null,
			1 => assets[0],
			_ => assets
		};

		AssetMover.StartDrag( this, paths, payload );
	}

	public DropAction OnEditorDragHover( Widget.DragEvent ev )
	{
		ev.Action = DropAction.Ignore;

		if ( string.IsNullOrEmpty( _currentFolder ) || !Directory.Exists( _currentFolder ) )
			return ev.Action;

		var screenPos = body.Box.Rect.Position + ev.LocalPosition;
		_dragHoveredIndex = -1;

		if ( ev.Data.Object is GameObject[] gos && gos.Length > 0 )
		{
			ev.Action = DropAction.Copy;

			var folderIdx = GetItemAtPosition( screenPos );
			if ( folderIdx >= 0 && _items[folderIdx].IsFolder )
				_dragHoveredIndex = folderIdx;

			return ev.Action;
		}

		var effect = DragEffectFor( ev );

		var index = GetItemAtPosition( screenPos );
		if ( index >= 0 && _items[index].IsFolder )
		{
			ev.Action = effect;
			_dragHoveredIndex = index;
		}
		else if ( ev.Data.HasFileOrFolder )
		{
			ev.Action = effect;
		}

		return ev.Action;
	}

	static DropAction DragEffectFor( Widget.DragEvent ev )
	{
		if ( ev.HasCtrl )
			return DropAction.Copy;

		foreach ( var file in ev.Data.Files )
		{
			if ( AssetContextMenuHelper.IsExternalSource( file ) )
				return DropAction.Copy;
		}

		return DropAction.Move;
	}

	public void OnEditorDragDrop( Widget.DragEvent ev )
	{
		if ( string.IsNullOrEmpty( _currentFolder ) || !Directory.Exists( _currentFolder ) )
			return;

		var screenPos = body.Box.Rect.Position + ev.LocalPosition;
		var index = GetItemAtPosition( screenPos );
		var targetFolder = _currentFolder;

		if ( index >= 0 && _items[index].IsFolder )
			targetFolder = _items[index].Path;

		if ( ev.Data.Object is GameObject[] gos && gos.Length > 0 )
		{
			ev.Action = DropAction.Copy;
			AssetContextMenuHelper.CreatePrefabFromGameObjects( gos, targetFolder, Reload );
			return;
		}

		if ( !ev.Data.HasFileOrFolder )
			return;

		var isCopy = DragEffectFor( ev ) == DropAction.Copy;
		ev.Action = isCopy ? DropAction.Copy : DropAction.Move;

		AssetMover.RunAndReport( ev.Data.Files, targetFolder, isCopy, Reload );
	}

	public void OnEditorDragLeave()
	{
		_dragHoveredIndex = -1;
	}

	internal void OpenFolderContextMenu()
	{
		if ( string.IsNullOrEmpty( _currentFolder ) ) return;

		var menu = new ContextMenu();
		AssetContextMenuHelper.BuildEmptySpaceMenu( menu, _currentFolder, Reload );
		menu.AddSeparator();
		AddPinOption( menu, _currentFolder );
		menu.OpenAtCursor( this );
	}

	//Right press anywhere not on a tile opens the folder menu, including on an empty folder
	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );

		if ( e.MouseButton != MouseButtons.Right ) return;

		OpenFolderContextMenu();
		e.StopPropagation();
	}

	internal void OpenItemContextMenu( int index )
	{
		if ( index < 0 || index >= _items.Count ) return;

		SelectForContextMenu( index );

		var menu = new ContextMenu();

		if ( _selectedIndices.Contains( index ) && _selectedIndices.Count > 1 )
		{
			var selectedFiles = _selectedIndices
				.Where( i => i >= 0 && i < _items.Count )
				.Select( i => _items[i] )
				.Where( i => !i.IsFolder )
				.Select( i => (i.Path, i.Asset) )
				.ToList();

			if ( selectedFiles.Count > 0 )
				AssetContextMenuHelper.BuildMultiFileMenu( menu, selectedFiles, Reload, owner: this );

			menu.OpenAtCursor( this );
			return;
		}

		var item = _items[index];

		if ( item.IsFolder )
		{
			AssetContextMenuHelper.BuildFolderMenu(
				menu,
				item.Path,
				item.Name,
				isRoot: false,
				onOpen: () => OnFolderNavigate?.Invoke( item.Path ),
				onRename: () => StartRename( index ),
				onRefresh: Reload,
				onDeleted: Reload,
				owner: this );

			menu.AddSeparator();
			AddPinOption( menu, item.Path );
		}
		else
		{
			AssetContextMenuHelper.BuildFileMenu(
				menu,
				item.Path,
				item.Asset,
				onRename: () => StartRename( index ),
				onChanged: Reload,
				owner: this );

			menu.AddSeparator();
			AddPinOption( menu, item.Path );
		}

		menu.OpenAtCursor( this );
	}

	//The tile a menu was opened on is picked out, unless it is already part of a selection.
	void SelectForContextMenu( int index )
	{
		if ( _selectedIndices.Contains( index ) ) return;

		_selectedIndices.Clear();
		_selectedIndices.Add( index );
		_selectedIndex = index;
		_selectionAnchor = index;

		RefreshRowVisuals();
	}

	//Pinning is offered wherever a file or folder is, not only in the tree.
	static void AddPinOption( ContextMenu menu, string path )
	{
		if ( string.IsNullOrEmpty( path ) )
			return;

		if ( AssetPins.Contains( path ) )
			menu.AddOption( "Unpin", "close", () => AssetPins.Remove( path ) );
		else
			menu.AddOption( "Pin", "push_pin", () => AssetPins.Add( path ) );
	}

	void StartRename( int index )
	{
		if ( index < 0 || index >= _items.Count ) return;

		var item = _items[index];
		EditorModal.Prompt( this, "Rename", null, newName =>
		{
			if ( string.IsNullOrWhiteSpace( newName ) )
				return;

			if ( !item.IsFolder && !Path.HasExtension( newName ) )
				newName += item.Extension;

			AssetMover.RenameAndReport( item.Path, newName, Reload );
		}, okay: "Rename", initial: item.Name, icon: item.IsFolder ? "folder" : "drive_file_rename_outline" );
	}

	sealed class IconGridBodyPanel : Panel
	{
		internal IconGridCanvasPanel Grid;

		protected override bool WantsDragScrolling => false;

		protected override void OnMouseDown( MousePanelEvent e )
		{
			if ( e.MouseButton == MouseButtons.Left && e.Target == this )
				Grid.ClearSelection();

			//A right press goes on up to the grid, which opens the folder's menu - see IconGridCanvasPanel.OnMouseDown.
		}
	}
}
