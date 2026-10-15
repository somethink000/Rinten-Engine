using Editor.TreeBrowser;
using Rinten.Helpers;

namespace Editor.MdlEditor;

/// <summary>The animation graph editor; graphs live in the model definition, so this opens a model</summary>
[EditorApp( "Animation Graph", "account_tree", "Animation graphs: nodes, state machines, parameters and variations", Opens = "mdl" )]
public partial class Window : DockWindow, IAssetEditor, IPreviewHost, ILinkedEditor
{
	private Session _session;
	private View _graphView;

	//Whether the main graph is the page; kept rather than measured, set only by SetGraphShown
	internal bool IsGraphShown { get; private set; }

	private Properties _properties;
	private object _selectedTarget;
	private ClipList _clipList;
	private ParameterList _parameterList;
	private PreviewColumn _previewColumn;
	private VariationEditor _variationEditor;
	private Preview _preview;
	private CompileLog _compileLog;
	private AnimationClipInfo _clipInfo;

	private Data _graph => _session.Graph;

	//The model whose graph this is - see Session.ForModel. Null before one is opened.
	private Asset _model => _session.Model;

	internal StateMachineNode SelectedStateMachine => _selectedStateMachine;
	internal StateMachineCanvas StateMachineCanvas => (_pageCanvas as StateMachinePage)?.Canvas;

	public Data Graph => _graph;
	public Scene PreviewScene => _preview?.PreviewScene;
	internal SceneModel PreviewModel => _preview?.ModelRenderer?.SceneModel;
	internal bool IsPreviewPaused => _preview?.IsPaused ?? false;
	public bool CanOpenMultipleAssets => true;

	internal static readonly List<Window> OpenEditors = new();

	internal static Window For( Widget from, Data graph )
	{
		if ( graph is not null )
		{
			foreach ( var editor in OpenEditors )
			{
				if ( editor.IsValid() && editor.Graph?.Root == graph )
					return editor;
			}
		}

		for ( Widget widget = from; widget is not null; widget = widget.Parent )
		{
			if ( widget is Window window )
				return window;
		}

		foreach ( var editor in OpenEditors )
		{
			if ( editor.IsValid() )
				return editor;
		}

		return null;
	}

	internal UndoSystem UndoSystem => _undoSystem;

	//Warnings from the most recent compile, shown in the Compile Log dock.
	internal IReadOnlyList<Compiler.Warning> CompileWarnings { get; private set; } = [];

	//Duration of the most recent compile in milliseconds, or null if nothing compiled yet.
	internal double? CompileTime { get; private set; }

	public Window()
	{
		DeleteOnClose = true;
		Size = new Vector2( 1400, 900 );

		SetWindowIcon( "view_in_ar" );

		_session = Session.Create();
		_undoSystem = new UndoSystem();
		_undoSystem.Initialize();

		OpenEditors.Add( this );

		BuildUI();
		UpdateTitle();

		//Must come after the docks are registered; set here since embedded tabs never call Show
		StateCookie = LayoutCookie;

		EditorLink.Register( this );
	}

	public void AssetOpen( Asset asset )
	{
		//A model, whose graph is what opens. A double click on one still goes to the model editor, which is what a .mdl is registered to.
		if ( asset is null || !asset.HasSourceFile || !IsModelExtension( asset.AssetType?.FileExtension ) )
			return;

		//What is open goes to the disk before what is being opened takes its place. Nothing is asked, because nothing is at risk.
		Checkpoint();

		OpenAsset( asset );
		Show();
	}

	//Brings the graph editor up on the graph a model carries, in a tab of its own: the model page's graph button, and the model
	//editor's. Through the app rather than the asset: a .mdl opened as an asset is the model editor's
	internal static void OpenGraphOf( Asset model )
	{
		if ( model?.AbsolutePath is not { } path ) return;

		EditorAppAttribute.For( typeof( global::Editor.MdlEditor.Window ) )?.OpenFile( path );
	}

	//Show the graph a model carries, unless that is what is up already.
	internal void ShowModelGraph( Asset model )
	{
		if ( model is null ) return;

		if ( ModelGraphResolver.SamePath( _model?.Path, model.Path ) ) return;

		Checkpoint();
		OpenAsset( model );
	}

	//Whether a file of this extension is a model.
	private static bool IsModelExtension( string extension ) => ModelGraphResolver.IsModelExtension( extension );

	//Open the graph a model carries here.
	private void OpenAsset( Asset model )
	{
		_session = Session.ForModel( model );

		EditorCookie.Set( LastGraphCookie, model.Path );
		AddRecentFile( model.Path );
		SetGraph( _session.Graph );

		_undoSystem.Initialize();

		//Written straight away where what is open is not what is on the disk: a graph repaired on the way in.
		_dirty = _session.HasRepairs;
		_previewCompileStale = _session.HasRepairs;
		UpdateTitle();
	}

	public override void Show()
	{
		//Don't clobber an unsaved graph with the remembered one
		if ( _model is null && !_dirty )
			RestoreLastGraph();

		if ( !IsEmbedded )
			base.Show();

		StateCookie = LayoutCookie;

		ApplyLayout();
		FitCurrentView();
	}

	//True when docked as a workspace tab: the dock manager was reparented out of this window
	internal bool IsEmbedded => DockManager.IsValid() && DockManager.Parent != this;

	public override void OnDestroyed()
	{
		OpenEditors.Remove( this );
		EditorLink.Unregister( this );
		InvalidatePreviewCompile();
		_preview?.ClearRecording();
		SetPreviewGraph( null );
		base.OnDestroyed();
	}

	public void SetRecordingReviewMode( bool reviewing, bool paused ) => _preview?.SetRecordingReviewMode( reviewing, paused );
	public bool ApplyRecordedUpdate( int updateIndex ) => _preview?.ApplyRecordedUpdate( updateIndex ) ?? false;

	public void SelectMember( string memberName ) { }

	//The preview's host - see IPreviewHost. The model in the picture is the graph's target model;
	//its definition is read off the disk, or off the model editor where that model is open there.

	Data IPreviewHost.Graph => _graph;
	bool IPreviewHost.IsModelPreview => false;
	Model IPreviewHost.OpenModel => null;
	SceneModel IPreviewHost.PreviewModel => PreviewModel;
	bool IPreviewHost.IsPreviewPaused => IsPreviewPaused;
	void IPreviewHost.CommitChange( string name ) => CommitChange( name );
	void IPreviewHost.NotifyModelDocChanged() { }
	void IPreviewHost.MarkDirty( bool updatePreview ) => MarkDirty( updatePreview );
	void IPreviewHost.ApplyPreviewParameters() => ApplyPreviewParameters();
	void IPreviewHost.ConsumeAutoResetPreviews() => ConsumeAutoResetPreviews();
	void IPreviewHost.CollectActiveAnimations( SceneModel model, List<(string Name, float Cycle, float Duration)> animations )
		=> CollectActiveAnimations( model, animations );

	/// <summary>The definition of the graph's target model, where it has one on disk.</summary>
	public ModelDefinition ModelDefinition => ModelGraphResolver.DefinitionOf( _graph?.TargetModel );

	/// <summary>The graph's model's own asset - the <c>.mdl</c> - where there is one: the model it is kept in, or failing that the one it targets.</summary>
	public Asset ModelAsset => _model ?? ModelGraphResolver.ModelFor( _graph );

	//The link to the model editor - see EditorLink.

	LinkedKind ILinkedEditor.Kind => LinkedKind.Graph;

	//The model whose graph is open.
	Asset ILinkedEditor.CurrentAsset => _model;
	Asset ILinkedEditor.CounterpartAsset => ModelAsset;

	bool ILinkedEditor.IsOnScreen => IsEmbedded
		? EditorWorkspaces.IsCurrent( GetType() ) && ModelEditorWindow.ShownInTree( DockManager )
		: Visible;

	//The model the other editor has up: show the graph it carries - an empty one
	//on it where it carries none yet, which becomes its graph from the first node.
	void ILinkedEditor.AdoptCounterpart( Asset counterpart ) => ShowModelGraph( counterpart );

	private void SetGraph( Data graph )
	{
		_session.ReplaceGraph( graph );
		_committedState = graph.Serialize();
		SetSelectedTarget( graph );
		_clipInfo = null;
		_clipList?.Rebuild();
		_parameterList?.Rebuild();
		_variationEditor?.Rebuild();
		_preview?.ClearRecording();
		_preview?.RebuildModel();

		CompileWarnings = [];
		CompileTime = null;
		_compileLog?.Refresh();

		NavigateToRoot();
		_previewCompileStale = false;
		InvalidatePreviewCompile();
		UpdateSavedPreviewGraph();

		if ( _autoCompilePreviewEnabled )
			CompilePreview();
	}

	internal void ReflectChildParametersToParent( InternalReferencedGraphNode node )
	{
		if ( string.IsNullOrWhiteSpace( node.GraphPath ) )
		{
			Log.Warning( "AnimGraph: Cannot reflect parameters from an internal graph with no graph set" );
			return;
		}

		var asset = ModelGraphResolver.ModelFor( node.GraphPath );
		if ( asset is null )
		{
			Log.Warning( $"AnimGraph: Cannot find the model '{node.GraphPath}' an internal graph runs the graph of" );
			return;
		}

		try
		{
			//Read, and nothing written: the child's graph is only looked at.
			var childSession = Session.ForModel( asset );

			if ( !childSession.Carried )
			{
				Log.Warning( $"AnimGraph: '{asset.Path}' carries no graph" );
				return;
			}

			var childGraph = childSession.Graph;
			var outputLog = new List<string>();
			PushGraphUndo( "Reflect Child Parameters" );
			var changed = _graph.ReflectParameters( childGraph, false, outputLog );
			PushGraphRedo();

			if ( changed )
				_parameterList.Rebuild();

			foreach ( var message in outputLog )
			{
				if ( message.Contains( "Incorrect", StringComparison.Ordinal ) )
					Log.Warning( $"AnimGraph: {message}" );
				else
					Log.Info( $"AnimGraph: {message}" );
			}
		}
		catch ( Exception exception )
		{
			Log.Warning( $"AnimGraph: Could not read parameters from '{asset.Path}': {exception.Message}" );
		}
	}

	private const string RecentFilesCookie = "animgraph.recentfiles";
	private const string LastGraphCookie = "animgraph.lastgraph";
	private const int MaxRecentFiles = 10;

	protected void RestoreLastGraph()
	{
		var path = EditorCookie.Get( LastGraphCookie, "" );
		if ( string.IsNullOrWhiteSpace( path ) )
			return;

		if ( AssetSystem.FindByPath( path ) is { HasSourceFile: true } asset )
		{
			OpenAsset( asset );
			return;
		}

		EditorCookie.Set( LastGraphCookie, "" );
	}

	private static void AddRecentFile( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return;

		var recent = EditorCookie.Get<List<string>>( RecentFilesCookie, [] );
		recent.RemoveAll( p => string.Equals( p, path, StringComparison.OrdinalIgnoreCase ) );
		recent.Insert( 0, path );

		if ( recent.Count > MaxRecentFiles )
			recent.RemoveRange( MaxRecentFiles, recent.Count - MaxRecentFiles );

		EditorCookie.Set( RecentFilesCookie, recent );
	}

	//Ctrl+S writes now; autosave would do it shortly anyway, but the shortcut should do something
	[Shortcut( "editor.save", "CTRL+S" )]
	private void Save() => Checkpoint();

	protected override bool OnClose()
	{
		//Written rather than asked about. A window that closes with the work in it is a window nobody has to think about closing.
		Checkpoint();

		return true;
	}

	//Edits that aren't on disk yet.
	private bool HasUnsavedChanges => _dirty;

	//Reveal the model the graph is kept in, in the OS file browser.
	private void ShowInExplorer()
	{
		if ( _model?.GetSourceFile( true ) is not { Length: > 0 } path )
			return;

		EditorUtility.OpenFileFolder( path );
	}
}
