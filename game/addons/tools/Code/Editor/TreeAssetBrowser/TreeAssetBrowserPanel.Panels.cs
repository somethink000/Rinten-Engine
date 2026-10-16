using Rinten.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Editor.TreeBrowser;

public partial class TreeAssetBrowserPanel
{
	const float DefaultTreeWidth = 220f;

	readonly List<IBrowserPanel> _panels = new();
	readonly List<BrowserPanelSplitter> _splitters = new();

	AssetTypeFilter _typeFilter;
	IAssetFilter _filter;
	AssetTreeScope? _scope;
	AssetTreeSection? _section;

	int _activeTabIndex;
	bool _isTabMode;
	bool _panelsMounted;
	int _placeSeen;
	BrowserSettingsPopup _settingsPopup;

	/// <summary>The asset types the user has narrowed this browser down to.</summary>
	public AssetTypeFilter TypeFilter => _typeFilter ??= CreateTypeFilter();

	/// <summary>What the browser's own limits and the user's chosen types allow through, together.</summary>
	public IAssetFilter Filter => _filter ??= new CompositeAssetFilter( Options, TypeFilter );

	/// <summary>Project content or mounted games.</summary>
	public AssetTreeScope Scope => _scope ??= AssetTreeScope.Project;

	AssetTypeFilter CreateTypeFilter()
	{
		var filter = new AssetTypeFilter();
		filter.Changed += OnTypeFilterChanged;
		return filter;
	}

	void OnTypeFilterChanged()
	{
		foreach ( var panel in _panels )
			panel.FilterChanged();

		StateHasChanged();
	}

	/// <summary>Switches every panel over to the other body of content.</summary>
	public void SetScope( AssetTreeScope scope )
	{
		if ( Scope == scope ) return;

		_scope = scope;

		foreach ( var panel in _panels )
			panel.SetScope( scope );

		StateHasChanged();
	}

	/// <summary>Which section the strip has picked. See AssetTreeSection.</summary>
	public AssetTreeSection Section => Project.Current is null ? SharedSection() : _section ??= SharedSection();

	//The section the other browsers are on; a picker starts on the default
	AssetTreeSection SharedSection()
		=> AssetTreeSections.Resolve( Options.IsPickerMode ? null : AssetBrowserPlace.Section, Options );

	/// <summary>The sections this project has, which is what the strip draws.</summary>
	public IReadOnlyList<AssetTreeSections.Entry> Sections => AssetTreeSections.For( Options );

	/// <summary>Points every panel at another section of the content.</summary>
	public void SetSection( AssetTreeSection section )
	{
		if ( Section == section ) return;

		_section = section;

		if ( !Options.IsPickerMode )
			AssetBrowserPlace.MoveTo( section );

		foreach ( var panel in _panels )
			panel.SetSection( section );

		StateHasChanged();
	}

	public void CreateDefaultPanels()
	{
		AddTreePanel();
		AddIconGridPanel();
		PinDefaultTreeWidth();
	}

	public void AddTreePanel()
	{
		var panel = new AssetBrowserPanel { Options = Options, Filter = Filter };
		panel.SetScope( Scope );
		panel.SetSection( Section );
		WireTreePanel( panel );
		AddPanelToContainer( panel );

		_activeTabIndex = _panels.Count - 1;
		UpdatePanelButtons();

		if ( _isTabMode )
			SelectTab( _activeTabIndex );
	}

	public void AddIconGridPanel()
	{
		var panel = new IconGridPanel { Options = Options, Filter = Filter };
		WireGridPanel( panel );

		AddPanelToContainer( panel );

		_activeTabIndex = _panels.Count - 1;
		UpdatePanelButtons();

		if ( _isTabMode )
			SelectTab( _activeTabIndex );
	}

	void WireTreePanel( AssetBrowserPanel panel )
	{
		panel.OnCloseRequested = () => RemovePanel( panel );
		panel.OnMoveLeftRequested = () => MovePanelLeft( panel );
		panel.OnMoveRightRequested = () => MovePanelRight( panel );
		panel.OnAssetSelected = asset =>
		{
			OnAssetSelected?.Invoke( asset );
			TreeAssetBrowser.RaiseAssetHighlighted( asset );
		};
		panel.OnAssetHighlighted = HighlightAsset;
		panel.OnAssetActivated = ActivateAsset;
		panel.OnFileSelected = path => OnFileSelected?.Invoke( path );
		panel.OnFolderSelected = path => OnFolderSelected?.Invoke( path );
		panel.OnFolderClicked = OnFolderSelectedInTree;
		panel.OnSectionSelected = OnSectionSelectedInTree;
		panel.OnBrowseLocationSelected = OnBrowseLocationSelectedInTree;
	}

	void WireGridPanel( IconGridPanel panel )
	{
		panel.OnCloseRequested = () => RemovePanel( panel );
		panel.OnMoveLeftRequested = () => MovePanelLeft( panel );
		panel.OnMoveRightRequested = () => MovePanelRight( panel );
		panel.OnAssetHighlighted = HighlightAsset;
		panel.OnAssetActivated = ActivateAsset;
		panel.OnFileActivated = path => OnFileSelected?.Invoke( path );
		panel.OnSelectionChanged = assets => OnSelectionChanged?.Invoke( assets );
		panel.HasSectionAbove = path => FindSectionAbove( path ) is not null;
		panel.OnLeaveRoot = path =>
		{
			if ( FindSectionAbove( path ) is { } section )
				OnSectionSelectedInTree( section );
		};
	}

	AssetTreeItem FindSectionAbove( string path ) =>
		_panels.OfType<AssetBrowserPanel>().FirstOrDefault()?.Tree?.FindSectionContaining( path );

	void HighlightAsset( Asset asset ) => OnAssetHighlighted?.Invoke( asset );

	void ActivateAsset( Asset asset )
	{
		OnAssetActivated?.Invoke( asset );
		Options.Activation.Activate( asset );
	}

	void AddPanelToContainer( IBrowserPanel panel )
	{
		_panels.Add( panel );
		SyncLayoutToggle();

		if ( panelsHost is not null )
		{
			_panelsMounted = false;
			TryMountPanels();
		}
	}

	public void EnsurePanelsMounted() => TryMountPanels();

	void TryMountPanels()
	{
		if ( _panels.Count == 0 || panelsHost is null )
			return;

		//A hotload hands us a fresh, empty host while we still think we are mounted.
		if ( _panelsMounted && !panelsHost.HasChildren )
			_panelsMounted = false;

		if ( !_panelsMounted )
		{
			RebuildPanelsLayout();

			if ( !panelsHost.HasChildren )
				return;

			_panelsMounted = true;
		}
	}

	//Every panel but the last keeps the width it was given; the last takes the rest.
	static void ConfigurePanelLayout( Panel panel, bool flex )
	{
		if ( panel is null || !panel.IsValid() ) return;

		panel.SetClass( "hidden", false );
		panel.SetClass( "fixed", !flex );
	}

	public void RemovePanel( IBrowserPanel panel )
	{
		if ( _panels.Count <= 1 ) return;

		var index = _panels.IndexOf( panel );
		if ( index < 0 ) return;

		_panels.RemoveAt( index );
		panel.Root.Delete( true );

		RebuildPanelsLayout();

		if ( _panels.Count == 1 )
			ClearFixedWidth( _panels[0].Root );

		if ( _activeTabIndex >= _panels.Count )
			_activeTabIndex = _panels.Count - 1;

		if ( _isTabMode )
		{
			for ( var i = 0; i < _panels.Count; i++ )
				SetPanelVisible( _panels[i].Root, i == _activeTabIndex );

			foreach ( var splitter in _splitters )
				splitter.SetClass( "hidden", true );

			RebuildTabBar();

			if ( _panels.Count <= 1 )
				SwitchToNormalMode();
		}

		UpdatePanelButtons();
	}

	public void MovePanelLeft( IBrowserPanel panel ) => SwapAdjacent( panel, -1 );

	public void MovePanelRight( IBrowserPanel panel ) => SwapAdjacent( panel, 1 );

	void SwapAdjacent( IBrowserPanel panel, int direction )
	{
		var index = _panels.IndexOf( panel );
		var other = index + direction;
		if ( index < 0 || other < 0 || other >= _panels.Count ) return;

		(_panels[index], _panels[other]) = (_panels[other], _panels[index]);
		RebuildPanelsLayout();
		UpdatePanelButtons();

		if ( _isTabMode )
			RebuildTabBar();
	}

	void RebuildPanelsLayout()
	{
		if ( panelsHost is null )
			return;

		foreach ( var splitter in _splitters )
			splitter.Delete( true );
		_splitters.Clear();

		foreach ( var child in panelsHost.Children.ToArray() )
			child.Parent = null;

		for ( var i = 0; i < _panels.Count; i++ )
		{
			var root = _panels[i].Root;
			if ( root is null || !root.IsValid() )
				continue;

			if ( i > 0 )
			{
				var left = _panels[i - 1].Root;
				if ( left is null || !left.IsValid() )
					continue;

				var splitter = new BrowserPanelSplitter();
				splitter.Init( left, root );
				panelsHost.AddChild( splitter );
				_splitters.Add( splitter );

				if ( _isTabMode )
					splitter.SetClass( "hidden", true );
			}

			ConfigurePanelLayout( root, flex: i == _panels.Count - 1 );
			panelsHost.AddChild( root );
		}

		UpdatePanelButtons();
	}

	void UpdatePanelButtons()
	{
		var showClose = ShowPanelControls && _panels.Count > 1;

		for ( var i = 0; i < _panels.Count; i++ )
		{
			var browserPanel = _panels[i];
			if ( _isTabMode )
			{
				browserPanel.ShowCloseButton = false;
				browserPanel.ShowMoveLeftButton = false;
				browserPanel.ShowMoveRightButton = false;
			}
			else
			{
				browserPanel.ShowCloseButton = showClose;
				browserPanel.ShowMoveLeftButton = showClose && i > 0;
				browserPanel.ShowMoveRightButton = showClose && i < _panels.Count - 1;
			}
		}
	}

	void OnFolderSelectedInTree( string folderPath )
	{
		if ( !Options.IsPickerMode )
			AssetBrowserPlace.MoveTo( folderPath );

		foreach ( var panel in _panels.OfType<IconGridPanel>() )
			panel.ShowFolder( folderPath );
	}

	void OnBrowseLocationSelectedInTree( AssetBrowser.Location location )
	{
		if ( !Options.IsPickerMode )
			AssetBrowserPlace.MoveTo( location );

		foreach ( var panel in _panels.OfType<IconGridPanel>() )
			panel.ShowBrowseLocation( location );
	}

	void OnSectionSelectedInTree( AssetTreeItem section )
	{
		var treePanel = _panels.OfType<AssetBrowserPanel>().FirstOrDefault()?.Tree;
		if ( treePanel is null || section is null ) return;

		var items = treePanel.GetBrowsableChildren( section );
		foreach ( var panel in _panels.OfType<IconGridPanel>() )
			panel.ShowTreeItems( section.Label, items );
	}

	public void NavigateToFile( string path )
	{
		_panels.OfType<AssetBrowserPanel>().FirstOrDefault()?.Reveal( path );
	}

	/// <summary>Points the tree and every grid at a folder.</summary>
	public void NavigateToFolder( string path )
	{
		if ( string.IsNullOrEmpty( path ) ) return;

		_panels.OfType<AssetBrowserPanel>().FirstOrDefault()?.Reveal( path );
		OnFolderSelectedInTree( path );
	}

	/// <summary>Opens the folder an asset lives in and picks the asset out of it.</summary>
	public void FocusOnAsset( Asset asset )
	{
		if ( asset is null ) return;

		var path = asset.GetSourceFile( true ) ?? asset.AbsolutePath;
		if ( string.IsNullOrEmpty( path ) ) return;

		foreach ( var panel in _panels )
			panel.Reveal( path );
	}

	/// <summary>Assets picked out in the grids, or the tree when no grid is showing.</summary>
	public IReadOnlyList<Asset> GetSelectedAssets()
	{
		foreach ( var panel in _panels.OfType<IconGridPanel>() )
		{
			var assets = panel.GetSelectedAssets();
			if ( assets.Count > 0 )
				return assets;
		}

		return [];
	}

	public void RefreshAll()
	{
		SyncScopeTabs();
		SyncSection();

		foreach ( var panel in _panels )
			panel.Refresh();
	}

	//Points the panels at the shared section, where they were built before the project was open and so were given the fallback
	void SyncSection()
	{
		if ( Project.Current is null ) return;

		var section = SharedSection();
		_section = section;

		foreach ( var panel in _panels )
			panel.SetSection( section );

		StateHasChanged();
	}

	public void ShowAddPanelMenu()
	{
		if ( !ShowPanelControls ) return;

		var menu = new ContextMenu( Host );
		menu.AddOption( "Tree View", "account_tree", () => AddTreePanel() );
		menu.AddOption( "Icon Grid", "grid_view", () => AddIconGridPanel() );
		menu.OpenAtCursor( this );
	}

	public void ShowSettingsPopup()
	{
		if ( _settingsPopup is { IsValid: true } )
		{
			_settingsPopup.Delete();
			return;
		}

		_settingsPopup = BrowserSettingsPopup.Open( settingsButton );
	}

	public void ToggleLayoutMode()
	{
		if ( !ShowPanelControls || _panels.Count <= 1 ) return;

		if ( _isTabMode )
			SwitchToNormalMode();
		else
			SwitchToTabMode();
	}

	void SwitchToTabMode()
	{
		_isTabMode = true;
		if ( tabsHost.IsValid() )
			tabsHost.SetClass( "hidden", false );

		foreach ( var splitter in _splitters )
			splitter.SetClass( "hidden", true );

		for ( var i = 0; i < _panels.Count; i++ )
		{
			ClearFixedWidth( _panels[i].Root );
			SetPanelVisible( _panels[i].Root, i == _activeTabIndex );
		}

		RebuildTabBar();
		UpdatePanelButtons();
		SyncLayoutToggle();
	}

	void SwitchToNormalMode()
	{
		_isTabMode = false;
		if ( tabsHost.IsValid() )
			tabsHost.SetClass( "hidden", true );

		foreach ( var splitter in _splitters )
			splitter.SetClass( "hidden", false );

		foreach ( var panel in _panels )
			SetPanelVisible( panel.Root, true );

		UpdatePanelButtons();
		SyncLayoutToggle();
	}

	void RebuildTabBar()
	{
		if ( !tabsHost.IsValid() ) return;

		tabsHost.DeleteChildren( true );

		for ( var i = 0; i < _panels.Count; i++ )
		{
			var index = i;
			var panel = _panels[i];
			var tab = new BrowserTab
			{
				Icon = GetPanelTabIcon( panel ),
				Title = GetPanelTabName( panel ),
				Active = index == _activeTabIndex,
				ShowClose = _panels.Count > 1,
				OnSelected = () => SelectTab( index ),
				OnCloseRequested = () => RemovePanel( panel )
			};
			tabsHost.AddChild( tab );
		}
	}

	void SelectTab( int index )
	{
		if ( index < 0 || index >= _panels.Count ) return;

		_activeTabIndex = index;

		for ( var i = 0; i < _panels.Count; i++ )
			SetPanelVisible( _panels[i].Root, i == _activeTabIndex );

		RebuildTabBar();
	}

	static void SetPanelVisible( Panel panel, bool visible ) => panel.SetClass( "hidden", !visible );

	//Drops the width a splitter wrote, so the panel goes back to sharing the row.
	static void ClearFixedWidth( Panel panel )
	{
		panel.Style.Width = Length.Auto;
		panel.SetClass( "fixed", false );
	}

	void PinDefaultTreeWidth()
	{
		if ( _panels.Count == 0 ) return;

		var root = _panels[0].Root;
		root.Style.Width = Length.Pixels( DefaultTreeWidth );
		root.SetClass( "fixed", true );
	}

	static string GetPanelTabName( IBrowserPanel panel ) => panel switch
	{
		AssetBrowserPanel => "Tree",
		IconGridPanel => "Grid",
		_ => "Panel"
	};

	static string GetPanelTabIcon( IBrowserPanel panel ) => panel switch
	{
		AssetBrowserPanel => "account_tree",
		IconGridPanel => "grid_view",
		_ => "tab"
	};

	/// <summary>Brings this browser to where the others are - see AssetBrowserPlace.</summary>
	public void FollowPlace()
	{
		if ( Options.IsPickerMode || _placeSeen == AssetBrowserPlace.Version ) return;

		_placeSeen = AssetBrowserPlace.Version;
		AssetBrowserPlace.Following = true;

		try
		{
			if ( AssetBrowserPlace.Section is { } section && Project.Current is not null )
				SetSection( AssetTreeSections.Resolve( section, Options ) );

			if ( AssetBrowserPlace.Folder is { } folder )
			{
				foreach ( var tree in _panels.OfType<AssetBrowserPanel>() )
					tree.Follow( folder );

				foreach ( var grid in _panels.OfType<IconGridPanel>() )
				{
					if ( !AssetBrowserPlace.Same( grid.Folder, folder ) )
						grid.ShowFolder( folder );
				}
			}
			else if ( AssetBrowserPlace.Location is { } location )
			{
				foreach ( var grid in _panels.OfType<IconGridPanel>() )
				{
					if ( !Equals( grid.Location, location ) )
						grid.ShowBrowseLocation( location );
				}
			}
		}
		finally
		{
			AssetBrowserPlace.Following = false;
		}
	}

	public IEnumerable<IBrowserPanel> Panels => _panels;
}
