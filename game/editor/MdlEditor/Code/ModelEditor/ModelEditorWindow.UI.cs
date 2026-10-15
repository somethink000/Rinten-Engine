using Editor.TreeBrowser;

namespace Editor.MdlEditor;

/// <summary>The window's furniture: the docks around the page. There is no menu bar or toolbar over them.</summary>
public sealed partial class ModelEditorWindow
{
	private DockWidget _centralDock;

	private void BuildUI()
	{
		BuildWorkspace();

		//The editor's own asset browser, the tree alone; it stays on the same folder as every other browser
		var assets = new TreeAssetBrowser( this, treeOnly: true )
		{
			Name = "Assets",
			WindowTitle = "Assets",
			MinimumWidth = 160,
		};

		assets.SetWindowIcon( "folder_open" );

		//The clip's timeline, which used to lie along the foot of the picture. Made once: the preview whose clip it shows is the window's for its whole life.
		_timelineDock = new PanelDockHost
		{
			Name = "Timeline",
			WindowTitle = "Timeline",
			MinimumHeight = 72,
		};

		_timelineDock.SetWindowIcon( "timeline" );
		_timelineDock.SetContent( new ModelTimeline { Preview = _preview } );

		_centralDock = DockManager.SetCentralWidget( _page );

		//Each with the edge it belongs on: a registered dock is made at once and put on its area closed.
		DockManager.RegisterDock( new() { Title = "Model", Icon = "account_tree", Area = DockArea.Left, CreateAction = () => _outliner } );
		DockManager.RegisterDock( new() { Title = "Inspector", Icon = "tune", Area = DockArea.Right, CreateAction = () => _inspector } );
		DockManager.RegisterDock( new() { Title = "Hierarchy", Icon = "device_hub", Area = DockArea.Right, CreateAction = () => _hierarchyDock } );
		DockManager.RegisterDock( new() { Title = "Assets", Icon = "folder_open", Area = DockArea.Right, CreateAction = () => assets } );
		DockManager.RegisterDock( new() { Title = "Timeline", Icon = "timeline", Area = DockArea.Bottom, CreateAction = () => _timelineDock } );

		foreach ( var info in DockManager.DockTypes )
		{
			if ( DockManager.FindDockWidget( info.Title ) is { } dock )
				dock.MinimumSizeFromContent = info.Title != "Assets";
		}
	}

	//Builds and binds page, tree, inspector and hierarchy; rerun on hotload to re-seat the docks
	private void BuildWorkspace()
	{
		var oldPage = _page;
		var oldOutliner = _outliner;
		var oldInspector = _inspector;
		var oldHierarchy = _hierarchyDock;

		oldPage?.UnbindPreview();

		_page = new ModelPage( this );
		_outliner = new ModelOutliner( _page );
		_inspector = new ModelNodeInspector( _page );
		_page.Bind( _outliner, _inspector );

		_hierarchyDock = new PanelDockHost
		{
			Name = "Hierarchy",
			WindowTitle = "Hierarchy",
			MinimumWidth = 140,
		};

		_hierarchyDock.SetWindowIcon( "device_hub" );
		_hierarchyDock.SetContent( _page.Hierarchy );

		if ( !_preview.IsValid() )
		{
			_preview = new Preview( this );
			_preview.SetModelMode( true );
			_preview.SetGraphToolsVisible( false );
		}

		Reseat( "Model", _outliner );
		Reseat( "Inspector", _inspector );
		Reseat( "Hierarchy", _hierarchyDock );

		if ( _centralDock.IsValid() )
			_centralDock.Widget = _page;

		_page.SetModel( _asset, _definition, _model );
		_page.BindPreview( _preview );

		if ( oldOutliner.IsValid() && oldOutliner != _outliner ) oldOutliner.Destroy();
		if ( oldInspector.IsValid() && oldInspector != _inspector ) oldInspector.Destroy();
		if ( oldHierarchy.IsValid() && oldHierarchy != _hierarchyDock ) oldHierarchy.Destroy();
		if ( oldPage.IsValid() && oldPage != _page ) oldPage.Destroy();
	}

	void Reseat( string title, Widget widget )
	{
		if ( DockManager.FindDockWidget( title ) is not { } dock ) return;

		dock.Widget = widget;
		widget.Parent = dock;
	}

	[EditorEvent.Hotload]
	public void OnHotload()
	{
		BuildWorkspace();
		ApplyLayout();
	}

	//Where the docks sit when nobody has moved them.
	protected override void BuildDefaultLayout()
	{
		//A narrow tree down the left; the inspector and the skeleton as two tabs on the preview's left; and
		//under both of those, the asset browser - narrow - with the clip's timeline taking the rest of the row.
		DockManager.OpenDock( "Model", DockArea.Left );

		var inspector = DockManager.OpenDock( "Inspector", DockArea.Left, _centralDock );
		DockManager.OpenDock( "Hierarchy", DockArea.Center, inspector );

		//The window's own bottom edge, which lies under the preview and the group beside
		//it both - where a split of the preview's group would lie under the preview alone.
		var assets = DockManager.OpenDock( "Assets", DockArea.Bottom );

		//How tall the row is - a timeline wants more than a strip - said now, while the split over the Assets
		//group is still that one: once the timeline is put beside it, the nearest split is the one between them.
		DockManager.SetSplitterProportions( assets, 0.68f, 0.32f );

		DockManager.OpenDock( "Timeline", DockArea.Right, assets );

		ApplyLayout();
	}

	//Narrow tree, narrow inspector, and the model itself gets the rest.
	internal void ApplyLayout()
	{
		if ( _centralDock.IsValid() )
			_centralDock.ToggleView( true );

		//Each the first half of the split over it: the tree against the rest of the
		//window, the inspector against the preview, the asset browser against the timeline.
		Widths( "Model", 0.12f );
		Widths( "Inspector", 0.26f );
		Widths( "Assets", 0.2f );

		DockManager.RaiseDock( "Model" );
		DockManager.RaiseDock( "Inspector" );
	}

	void Widths( string title, float first )
	{
		if ( DockManager.FindDockWidget( title ) is { } dock )
			DockManager.SetSplitterProportions( dock, first, 1.0f - first );
	}

	//The graph this model carries, in the graph editor - the same as the outliner's button.
	private void OpenGraph()
	{
		if ( _asset is null || _definition is null ) return;

		global::Editor.MdlEditor.Window.OpenGraphOf( _asset );
	}

	//Reveal the open file in the OS file browser.
	private void ShowInExplorer()
	{
		if ( _asset?.GetSourceFile( true ) is not { Length: > 0 } path )
			return;

		EditorUtility.OpenFileFolder( path );
	}
}
