using Editor.TreeBrowser;
namespace Editor.MdlEditor;

public partial class Window
{
	//Dock layout key; bump when the default layout changes so stale saved layouts are ignored
	internal const string LayoutCookie = "AnimGraph.1";

	private readonly HashSet<string> _searchActiveTypes = new();
	private readonly HashSet<string> _searchExcludedTypes = new();
	private DockWidget _centralDock;

	[EditorEvent.Hotload]
	public void OnHotload()
	{
		Data.InvalidatePropertyCache();

		if ( _workspace is null )
		{
			SaveViewMemory();

			var previousCanvas = _centralDock.Widget;
			_centralDock.Widget = BuildGraphCanvas();
			previousCanvas?.Destroy();

			_pageCanvas = null;
			_shownPage = null;

			if ( _nav.Count == 0 )
				NavigateToRoot();
			else
				ShowPage();

			if ( _autoCompilePreviewEnabled )
				CompilePreview();

			return;
		}

		_pageCanvas?.Delete( true );
		_pageCanvas = null;
		ShowPage();

		if ( _autoCompilePreviewEnabled )
			CompilePreview();
	}

	//The animation half's document: the breadcrumb over the graph. A widget, and the only one this half has. The dock manager's
	//central slot is arranged by the editor's layouts, which know about widgets and nothing else; everything inside it is panels.
	private PanelDockHost BuildGraphCanvas()
	{
		var host = new PanelDockHost( this )
		{
			Name = "AnimGraph",
			WindowTitle = "AnimGraph",
		};

		_graphCanvas = new GraphCanvasPanel( this );
		_workspace = _graphCanvas.Workspace;
		_pageHost = _graphCanvas.PageHost;

		_graphView = _graphCanvas.GraphView;
		_graphView.Graph = _graph;

		host.SetContent( _graphCanvas );

		return host;
	}

	private void BuildUI()
	{
		var graphCanvas = BuildGraphCanvas();

		_properties = new Properties( this );
		_properties.Target = _graph;
		_properties.PropertyUpdated += OnPropertyUpdated;

		_clipList = new ClipList( this );
		_clipList.OnClipSelected = OnClipSelected;
		_clipList.Rebuild();

		_parameterList = new ParameterList( this );
		_parameterList.Rebuild();

		_variationEditor = new VariationEditor( this );

		_preview = new Preview( this );

		//The preview with the graph's parameters under it, as one panel: the parameters are what you drive the preview
		//with, and having them across the foot of the window put them a whole screen away from the model they move.
		_previewColumn = new PreviewColumn( this );
		_previewColumn.Fill( _preview, _parameterList );

		var history = new History( this );
		_compileLog = new CompileLog( this );
		//The editor's own asset browser, the tree alone; it stays on the same folder as every other browser
		var assets = new TreeAssetBrowser( this, treeOnly: true )
		{
			Name = "Assets",
			WindowTitle = "Assets",
			MinimumWidth = 160,
		};

		assets.SetWindowIcon( "folder_open" );

		_centralDock = DockManager.SetCentralWidget( graphCanvas );

		//Each with the edge it belongs on, not Hidden. A registered dock is made at once and put on its area
		//closed, and Hidden has no area - the dock manager reads it as the centre, which is where the graph is.
		DockManager.RegisterDock( new() { Title = "Preview", Icon = "visibility", Area = DockArea.Left, CreateAction = () => _previewColumn } );
		DockManager.RegisterDock( new() { Title = "Properties", Icon = "edit", Area = DockArea.Right, CreateAction = () => _properties } );
		DockManager.RegisterDock( new() { Title = "Assets", Icon = "folder_open", Area = DockArea.Right, CreateAction = () => assets } );
		DockManager.RegisterDock( new() { Title = "Animation Clips", Icon = "movie", Area = DockArea.Bottom, CreateAction = () => _clipList } );
		DockManager.RegisterDock( new() { Title = "Variations", Icon = "call_split", Area = DockArea.Bottom, CreateAction = () => _variationEditor } );
		DockManager.RegisterDock( new() { Title = "History", Icon = "history", Area = DockArea.Bottom, CreateAction = () => history } );
		DockManager.RegisterDock( new() { Title = "Compile Log", Icon = "rule", Area = DockArea.Bottom, CreateAction = () => _compileLog } );

		foreach ( var info in DockManager.DockTypes )
		{
			if ( DockManager.FindDockWidget( info.Title ) is { } dock )
				dock.MinimumSizeFromContent = info.Title != "Assets";
		}

		SetSelectedTarget( _graph );
		NavigateToRoot();
	}

	//Where the docks sit when nobody has moved them. The graph in the middle, the preview with the parameters under it
	//down the left, the properties and the asset browser down the right, and the log and the lists along the bottom.
	protected override void BuildDefaultLayout()
	{
		DockManager.OpenDock( "Preview", DockArea.Left );

		var properties = DockManager.OpenDock( "Properties", DockArea.Right );
		DockManager.OpenDock( "Assets", DockArea.Center, properties );

		var bottom = DockManager.OpenDock( "Compile Log", DockArea.Bottom, _centralDock );
		DockManager.OpenDock( "Animation Clips", DockArea.Center, bottom );
		DockManager.OpenDock( "Variations", DockArea.Center, bottom );
		DockManager.OpenDock( "History", DockArea.Center, bottom );

		ApplyLayout();
	}

	//The widths the columns start at, and which tabs are on top.
	internal void ApplyLayout()
	{
		//The centre is the document, so it is never the thing that gets hidden.
		if ( _centralDock.IsValid() )
			_centralDock.ToggleView( true );

		//Wide enough for the model and a column of parameters under it.
		Widths( "Preview", 0.26f );

		//Narrow. What is on that side is a properties sheet and a folder tree, and both of them
		//are lists of names - the graph is what the window is for and it should have the width.
		Widths( "Properties", 0.84f );
		Widths( "Compile Log", 0.78f );

		DockManager.RaiseDock( "Animation Clips" );
		DockManager.RaiseDock( "Properties" );
	}

	//Give a dock's splitter a share, where that dock is up at all.
	void Widths( string title, float first )
	{
		if ( DockManager.FindDockWidget( title ) is { } dock )
			DockManager.SetSplitterProportions( dock, first, 1.0f - first );
	}

	[Shortcut( "animgraph.find", "CTRL+F", ShortcutType.Window )]
	internal void OpenSearch()
	{
		if ( !_graphCanvas.IsValid() )
			return;

		//Where it opens is the popup's own business now - it hangs off the strip on the window's overlay, rather than being handed a place on the desktop.
		SearchPopup.Open( _graphCanvas.Breadcrumb, this, _searchActiveTypes, _searchExcludedTypes );
	}
}

//Left column dock: model preview above the graph parameters, split by a vertical splitter
internal sealed class PreviewColumn : PanelDockHost
{
	private SplitterPanel _split;

	public PreviewColumn( Widget parent ) : base( parent )
	{
		//A column narrower than this shows nothing of either half.
		MinimumWidth = 100;

		Fill( null, null );
	}

	/// <summary>Put them in, in this order.</summary>
	public void Fill( Rinten.UI.Panel top, Rinten.UI.Panel bottom )
	{
		if ( _split.IsValid() )
		{
			foreach ( var cell in _split.Cells.ToArray() )
			{
				if ( !cell.IsValid() ) continue;

				cell.RemoveClass( "splitter-cell" );
				cell.Style.FlexGrow = null;
				cell.Parent = null;
			}
		}

		_split = new SplitterPanel( vertical: true );

		SetContent( _split );

		//Three parts of the column to the picture, two to the parameters - what the widget splitter's stretches said.
		if ( top.IsValid() ) _split.AddCell( top, 3.0f );
		if ( bottom.IsValid() ) _split.AddCell( bottom, 2.0f );
	}
}
