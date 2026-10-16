using Editor.TreeBrowser;

namespace Editor.Materials;

/// <summary>Material editor layout: controls dock, preview, and map browser around a leased MaterialDocument</summary>
[EditorForAssetType( "mat" )]
[EditorApp( "Material Editor", "format_paint", "Shader, features, parameters and maps for a material" )]
public sealed class MaterialEditorWindow : DockWindow, IAssetEditor
{
	//Which saved dock layout this window's arrangement belongs to.
	const string LayoutCookie = "MaterialEditor.Docks";

	const string MaterialDock = "Material";
	const string AssetsDock = "Asset Browser";

	/// <summary>One material per instance: the workspace gives each of them a document tab and a whole editor behind it, which is what tabs the materials.</summary>
	public bool CanOpenMultipleAssets => false;

	DocumentLease<MaterialDocument> _lease;

	MaterialStage _stage;
	MaterialSheetDock _sheet;
	DockWidget _centralDock;
	TreeAssetBrowser _assets;

	//The material open here, or the empty document while there is none.
	IMaterialDocument Document => _lease?.Document ?? MaterialDocument.Empty;

	//Whether this editor is a tab in the main window rather than a window of its own - the workspace
	//lifts the docks out of the frame and leaves the window an empty husk that never shows.
	bool IsEmbedded => DockManager.IsValid() && DockManager.Parent != this;

	public MaterialEditorWindow()
	{
		DeleteOnClose = true;
		Size = new Vector2( 1280, 860 );

		SetWindowIcon( "format_paint" );

		BuildUI();

		//After the docks and not before: setting this is what restores the saved layout, and
		//the default one it falls back to opens docks that have to have been registered already.
		StateCookie = LayoutCookie;
	}

	public void AssetOpen( Asset asset )
	{
		if ( asset is null )
			return;

		//The old file is let go of before the new one is taken up, and the panels are made
		//again over the new document: they bind to a document once, when they are made.
		_lease?.Dispose();
		_lease = ResourceDocuments.Acquire<MaterialDocument>( asset );

		BuildWorkspace();

		Title = $"Material - {asset.Path}";

		if ( asset.AssetType?.Icon128 is { } icon )
			SetWindowIcon( icon );

		//A workspace tab is this editor with the window left hidden behind it; showing the husk would put an empty frame on the desktop.
		if ( !IsEmbedded )
			Show();
	}

	/// <summary>Nothing on the sheet is reached by name; the material is one page.</summary>
	public void SelectMember( string memberName ) { }

	public override void OnDestroyed()
	{
		_lease?.Dispose();
		_lease = null;

		base.OnDestroyed();
	}

	void BuildUI()
	{
		BuildWorkspace();

		//The editor's own browser, the tree alone; it stays on the same folder as every other browser
		_assets = new TreeAssetBrowser( this, treeOnly: true )
		{
			Name = "Assets",
			WindowTitle = AssetsDock,
			MinimumWidth = 200,
		};

		_assets.SetWindowIcon( "folder_open" );

		//The preview fills the centre; it is the one part that is always there.
		_centralDock = DockManager.SetCentralWidget( _stage );

		DockManager.RegisterDock( new() { Title = MaterialDock, Icon = "tune", Area = DockArea.Left, CreateAction = () => _sheet } );
		DockManager.RegisterDock( new() { Title = AssetsDock, Icon = "folder_open", Area = DockArea.Right, CreateAction = () => _assets } );

		//Both columns ask for more room than they are useful at; each is meant to be draggable down to what its reader wants of it.
		foreach ( var name in new[] { MaterialDock, AssetsDock } )
		{
			if ( DockManager.FindDockWidget( name ) is { } dock )
				dock.MinimumSizeFromContent = false;
		}
	}

	//Builds sheet and preview over the document; rerun on hotload or file change, docks re-seated
	void BuildWorkspace()
	{
		var oldStage = _stage;
		var oldSheet = _sheet;

		_stage = new MaterialStage( Document );
		_sheet = new MaterialSheetDock( Document );

		Reseat( MaterialDock, _sheet );

		if ( _centralDock.IsValid() )
			_centralDock.Widget = _stage;

		if ( oldSheet.IsValid() && oldSheet != _sheet ) oldSheet.Destroy();
		if ( oldStage.IsValid() && oldStage != _stage ) oldStage.Destroy();
	}

	void Reseat( string title, Widget widget )
	{
		if ( DockManager.FindDockWidget( title ) is not { } dock ) return;

		dock.Widget = widget;
		widget.Parent = dock;
	}

	[EditorEvent.Hotload]
	void OnHotload()
	{
		BuildWorkspace();
	}

	//Where the docks sit when nobody has moved them.
	protected override void BuildDefaultLayout()
	{
		var material = DockManager.OpenDock( MaterialDock, DockArea.Left );
		var assets = DockManager.OpenDock( AssetsDock, DockArea.Right );

		//The controls take a good third, the tree a fifth, and the shape in the middle has the rest - the material is what the window is for.
		DockManager.SetSplitterProportions( material, 0.3f, 0.7f );
		DockManager.SetSplitterProportions( assets, 0.8f, 0.2f );
	}

	//Puts the material back the way it was before the last change - the way back a resource written on every edit has, see ResourceHistory.
	[Shortcut( "editor.undo", "CTRL+Z" )]
	void Undo() => Document.StepBack();
}
