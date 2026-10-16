namespace Editor.SpriteEditor;

/// <summary>Sprite editor: owns document, selection, preview and commands; a workspace tab when possible</summary>
[EditorForAssetType( "sprite" )]
[EditorApp( "Sprite Editor", "emoji_emotions", "Edit 2D Sprites" )]
public sealed partial class SpriteEditorWindow : DockWindow, IAssetEditor
{
	//Which saved dock layout this window's arrangement belongs to.
	internal const string LayoutCookie = "SpriteEditor";

	private readonly SpriteDocument _document = new();
	private readonly AnimationSelection _selection;
	private readonly SpritePreviewScene _scene;
	private readonly SpritePlayback _playback;
	private readonly AnimationCommands _animations;
	private readonly FrameCommands _frames;
	private readonly FrameImporter _importer;

	private readonly List<SpriteDock> _docks = new();

	public bool CanOpenMultipleAssets => true;

	/// <summary>Edits not yet saved - what the document tab's star says.</summary>
	public bool HasUnsavedChanges => _document.IsModified;

	public SpriteEditorWindow()
	{
		DeleteOnClose = true;
		Size = new Vector2( 1280, 720 );

		SetWindowIcon( "emoji_emotions" );

		_selection = new AnimationSelection( _document );
		_scene = new SpritePreviewScene();
		_playback = new SpritePlayback( _scene.Renderer, _document, _selection );
		_animations = new AnimationCommands( _document, _selection );
		_frames = new FrameCommands( _document, _selection, _playback );
		_importer = new FrameImporter( _document, _selection, _frames, () => _scene.Filter );

		Antialiasing = EditorCookie.Get( TextureFilterLevels.Cookie, TextureFilterLevels.Default );

		_document.Loaded += UpdateTitle;
		_document.Restored += UpdateTitle;
		_document.Changed += _ => UpdateTitle();

		CreateDocks();
		UpdateTitle();

		//After the docks and not before: setting this is what restores the saved layout, and
		//the default it falls back to opens docks that have to have been registered already.
		StateCookie = LayoutCookie;
	}

	public void AssetOpen( Asset asset )
	{
		PromptSave( () => OpenAsset( asset ) );

		//A workspace tab is this editor with the window left hidden behind it; showing the husk would put an empty frame on the desktop.
		if ( !IsEmbedded )
			Show();
	}

	public void SelectMember( string memberName ) { }

	//Whether this editor is a tab in the main window rather than a window of its own - the workspace
	//takes the docks out of the frame and leaves the window itself an empty husk that never shows.
	internal bool IsEmbedded => DockManager.IsValid() && DockManager.Parent != this;

	public override void OnDestroyed()
	{
		_playback.Dispose();
		_selection.Dispose();
		_scene.Dispose();

		base.OnDestroyed();
	}

	//The docks

	private void CreateDocks()
	{
		var preview = Register( new PreviewDock( _scene, _playback, _selection ) );
		var inspector = Register( new InspectorDock( _document, _selection ) );
		var animations = Register( new AnimationListDock( _document, _selection, _animations ) );
		var timeline = Register( new TimelineDock( _document, _selection, _playback, _frames, _importer ) );

		var previewSlot = DockManager.AddDock( preview.Name, "emoji_emotions", preview, DockArea.Right );
		var inspectorSlot = DockManager.AddDock( inspector.Name, "edit", inspector, DockArea.Left );

		DockManager.AddDock( animations.Name, "directions_walk", animations, DockArea.Bottom, relativeTo: inspectorSlot );
		DockManager.AddDock( timeline.Name, "view_column", timeline, DockArea.Bottom, relativeTo: previewSlot );
	}

	private T Register<T>( T dock ) where T : SpriteDock
	{
		_docks.Add( dock );
		return dock;
	}

	//Where the docks sit when nobody has moved them.
	protected override void BuildDefaultLayout()
	{
		var preview = DockManager.OpenDock( "Preview", DockArea.Right );
		var inspector = DockManager.OpenDock( "Inspector", DockArea.Left );
		var animations = DockManager.OpenDock( "Animations", DockArea.Bottom, inspector );
		var timeline = DockManager.OpenDock( "Timeline", DockArea.Bottom, preview );

		DockManager.SetSplitterProportions( preview, 0.25f, 0.75f );
		DockManager.SetSplitterProportions( animations, 0.68f, 0.32f );
		DockManager.SetSplitterProportions( timeline, 0.75f, 0.25f );
	}

	//The panel types are new after a hotload; every dock is given a fresh panel of the new kind. The docks themselves, the scene and the document stay.
	[EditorEvent.Hotload]
	private void OnHotload()
	{
		foreach ( var dock in _docks )
		{
			if ( dock.IsValid() ) dock.Rebuild();
		}
	}
}
