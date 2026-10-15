using Rinten.Engine;
using System;

namespace Editor;

//Everything below the menu bar; shows one workspace (a whole dock layout) at a time
internal sealed class WorkspaceArea : StackedHost
{
	internal static WorkspaceArea Current { get; private set; }

	readonly List<EditorWorkspace> _workspaces = new();

	EditorWorkspace _currentWorkspace;

	//The strip the tabs are drawn in, which is not in here at all.
	internal WorkspaceTabBar TabBar { get; }

	public WorkspaceArea( Widget parent, WorkspaceTabBar tabBar ) : base( parent )
	{
		Current = this;
		TabBar = tabBar;

		Name = "Workspaces";
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		if ( Current == this )
			Current = null;
	}

	//Whether a widget is in the on-screen layout, so one workspace's shortcuts don't fire in another
	internal static bool InCurrentLayout( Widget widget )
	{
		if ( Current is not { } area || !area.IsValid() ) return true;
		if ( area.Shown is not { } current || !current.IsValid() ) return true;

		for ( var w = widget; w.IsValid(); w = w.Parent )
		{
			if ( ReferenceEquals( w, current ) ) return true;

			//Reached the area without passing through what it is showing, so this is in one of the layouts it is not showing.
			if ( ReferenceEquals( w, area ) ) return false;
		}

		return true;
	}

	/// <summary>Add the workspace the editor starts in and can never close - General, whose layout is the window's own docks.</summary>
	public EditorWorkspace AddPermanent( string title, string icon, Widget content )
	{
		var workspace = new EditorWorkspace( this, title, icon, content ) { CanClose = false };

		_workspaces.Add( workspace );
		content.Parent = this;
		content.Visible = false;

		RebuildTabBar();
		MakeCurrent( workspace );

		return workspace;
	}

	/// <summary>Open a workspace the editor builds itself - the mapping one - and bring it up. Already open, it is only brought up.</summary>
	public EditorWorkspace Open( string title, string icon, Func<Widget> build, Action entered = null, Action left = null )
	{
		if ( Find( title ) is { } existing )
		{
			MakeCurrent( existing );
			return existing;
		}

		Widget content;

		try
		{
			content = build();
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"Workspace '{title}' would not open" );
			return null;
		}

		if ( !content.IsValid() )
			return null;

		var workspace = new EditorWorkspace( this, title, icon, content ) { Entered = entered, Left = left };

		_workspaces.Add( workspace );
		content.Parent = this;
		content.Visible = false;

		RebuildTabBar();
		MakeCurrent( workspace );

		return workspace;
	}

	//The workspace an editor of this type is open in, if it is.
	internal EditorWorkspace FindEditor( Type type ) => _workspaces.FirstOrDefault( x => x.EditorType == type );

	/// <summary>The workspace of this name, if it is open.</summary>
	public EditorWorkspace Find( string title ) => _workspaces.FirstOrDefault( x => x.Title == title );

	/// <summary>Bring up the editor of this type, opening it if it isn't already here.</summary>
	public EditorWorkspace OpenEditor( Type type, string title, string icon )
	{
		//Already open - editors are one per type, the documents inside them are what you have several of
		if ( _workspaces.FirstOrDefault( x => x.EditorType == type ) is { } existing )
		{
			MakeCurrent( existing );
			return existing;
		}

		//An editor of files opens as documents, starting with a blank one that the first file it is given goes into
		if ( IsDocumentEditor( type ) )
		{
			var documents = OpenDocuments( type, title, icon );
			documents.Documents.Open( null, null, () => EditorTypeLibrary.Create<Widget>( type ), null );

			return documents;
		}

		var editor = EditorTypeLibrary.Create<Widget>( type );
		if ( !editor.IsValid() )
			return null;

		var content = Embed( editor );
		if ( !content.IsValid() )
		{
			editor.Destroy();
			return null;
		}

		var workspace = new EditorWorkspace( this, title, icon, content ) { EditorType = type, OwnEditor = editor };

		_workspaces.Add( workspace );
		content.Parent = this;
		content.Visible = false;

		RebuildTabBar();
		MakeCurrent( workspace );

		return workspace;
	}

	//Whether an editor works on files, and so opens as tabs of documents
	static bool IsDocumentEditor( Type type ) => type.IsAssignableTo( typeof( IAssetEditor ) ) || type.IsAssignableTo( typeof( IFileEditor ) );

	//Open an asset in its workspace editor - the double-click path - as a document tab of the editor registered for it
	internal static bool OpenAsset( Asset asset, string extension, out IAssetEditor editor )
	{
		editor = null;

		if ( !Current.IsValid() )
			return false;

		var found = EditorTypeLibrary.GetTypesWithAttribute<EditorForAssetTypeAttribute>()
			.Where( x => x.Attribute.Extension == extension )
			.Select( x => x.Type )
			.FirstOrDefault();

		if ( found is null )
			return false;

		//Only editors that declared themselves whole-workspace apps route here; everything else keeps opening the way it always has
		var app = EditorAppAttribute.For( found.TargetType );

		if ( app is not { Workspace: true } )
			return false;

		editor = Current.OpenDocument( found.TargetType, app.Title, app.Icon, asset.AbsolutePath, asset.Name,
			opened => ( opened as IAssetEditor )?.AssetOpen( asset ) ) as IAssetEditor;

		return editor is not null;
	}

	/// <summary>Brings up a file in its editor's workspace, as a document tab of its own, and gives back the editor holding it.</summary>
	internal Widget OpenDocument( Type type, string title, string icon, string path, string name, Action<Widget> load )
	{
		var workspace = OpenDocuments( type, title, icon );

		return workspace.Documents.Open( path, name, () => EditorTypeLibrary.Create<Widget>( type ), load );
	}

	//The documents workspace of this editor type, made if it is not open, and brought up
	EditorWorkspace OpenDocuments( Type type, string title, string icon )
	{
		var workspace = _workspaces.FirstOrDefault( x => x.EditorType == type );

		if ( workspace is null )
		{
			var documents = new DocumentTabWidget( this, title );

			workspace = new EditorWorkspace( this, title, icon, documents ) { EditorType = type, Documents = documents };
			documents.OnEmpty = () => Close( workspace );

			_workspaces.Add( workspace );
			documents.Visible = false;

			RebuildTabBar();
		}

		MakeCurrent( workspace );

		return workspace;
	}

	//Takes an editor window and returns just its docks for a workspace; the window itself is never shown
	internal static Widget Embed( Widget editor )
	{
		if ( editor is not DockWindow window )
		{
			//Not window-shaped - it can go in as it is
			return editor;
		}

		if ( !window.DockManager.IsValid() )
			return null;

		var host = new Widget( null ) { Name = "WorkspaceContent" };
		host.Layout = Layout.Column();

		window.DockManager.Parent = host;
		host.Layout.Add( window.DockManager, 1 );

		//Detach the canvas, or the hidden window keeps re-laying out the same widget the workspace placed
		window.Canvas = null;

		return host;
	}

	public void Close( EditorWorkspace workspace )
	{
		if ( !workspace.CanClose ) return;

		if ( _workspaces.IndexOf( workspace ) < 0 ) return;

		//Each document through its editor's own close, which writes what is unsaved; one that asks first keeps the workspace open
		if ( workspace.Documents is { } documents && !documents.CloseAll() )
			return;

		if ( workspace.OwnEditor is { } own && own.IsValid() && !own.AskClose() )
			return;

		var index = _workspaces.IndexOf( workspace );
		if ( index < 0 ) return;

		_workspaces.Remove( workspace );

		if ( _currentWorkspace == workspace )
		{
			//Whatever it took over while it was current goes back before it dies - the mapping workspace is holding the scene area
			workspace.Left?.Invoke();

			_currentWorkspace = null;
			MakeCurrent( _workspaces[Math.Min( index, _workspaces.Count - 1 )] );
		}

		//The tab is removed even if the editor throws while tearing down; the error is only logged
		try
		{
			workspace.Content?.Destroy();
			workspace.OwnEditor?.Destroy();
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"Workspace '{workspace.Title}' threw on the way out" );
		}
		finally
		{
			workspace.Destroy();

			RebuildTabBar();
		}
	}

	/// <summary>Close the workspace on screen, if it is one that can be closed at all. Says whether it did - General cannot, and is the answer of no.</summary>
	public bool CloseCurrent()
	{
		if ( _currentWorkspace is not { CanClose: true } workspace )
			return false;

		Close( workspace );

		return true;
	}

	public void MakeCurrent( EditorWorkspace workspace )
	{
		if ( _currentWorkspace == workspace ) return;
		if ( !workspace.Content.IsValid() ) return;

		//Timed under -uitrace. Switching workspaces is one of the things this editor is visibly slow at, and how slow has to be
		//written down somewhere before it can be worked on - but not in every run's log, where it is a number nobody asked for.
		var started = tracing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

		//A workspace can be holding something that belongs to the window rather than
		//to itself - the scene area - so it is told when it stops being the one shown
		_currentWorkspace?.Left?.Invoke();

		_currentWorkspace = workspace;
		Show( workspace.Content );

		workspace.Entered?.Invoke();

		TabBar.UpdateTabs();

		//And anyone outside who asked to hear about it - see EditorWorkspaces.
		EditorWorkspaces.RaiseEntered( workspace.EditorType );

		if ( tracing )
		{
			var ms = ( System.Diagnostics.Stopwatch.GetTimestamp() - started ) * 1000.0 /
				System.Diagnostics.Stopwatch.Frequency;

			Log.Info( $"workspace: {workspace.Title} in {ms:F0} ms" );
		}
	}

	private static readonly bool tracing = Rinten.Utility.CommandLine.HasSwitch( "-uitrace" );

	internal bool IsCurrent( EditorWorkspace workspace ) => _currentWorkspace == workspace;

	//The workspace on screen.
	internal EditorWorkspace CurrentWorkspace => _currentWorkspace;

	//The editor type the workspace on screen was opened for, or null for General and the built-in ones.
	internal Type CurrentEditorType => _currentWorkspace?.EditorType;


	//The window whose docks the user is actually looking at, or null when the workspace up is not a window at all.
	internal DockWindow CurrentWindow => CurrentDocks?.Window;

	//Dock manager of the workspace on screen, found by visibility as workspaces hold layouts differently
	internal DockManager CurrentDocks
	{
		get
		{
			if ( _currentWorkspace?.Editor is DockWindow window && window.DockManager.IsValid() )
				return window.DockManager;

			return FindDocks( Shown );
		}
	}

	//The outermost dock manager on screen under this widget.
	static DockManager FindDocks( Widget widget )
	{
		if ( !widget.IsValid() || !widget.Visible )
			return null;

		if ( widget is DockManager docks )
			return docks;

		foreach ( var child in widget.Children )
		{
			if ( FindDocks( child ) is { } found )
				return found;
		}

		return null;
	}

	void RebuildTabBar() => TabBar.SetTabs( _workspaces );
}

//Host for WorkspaceStrip in the right half of the menu bar
internal sealed class WorkspaceTabBar : PanelWidget
{
	readonly WorkspaceStrip _strip;

	public WorkspaceTabBar( MenuBar menuBar ) : base( menuBar )
	{
		Name = "WorkspaceTabs";

		menuBar.TrailingWidget = this;

		//The window drags by what the strip leaves free, so its width going changes the drag strips
		SetPanel( _strip = new WorkspaceStrip { Host = this, Resized = () => menuBar.TitlesMoved?.Invoke() } );
	}

	/// <summary>Show these workspaces, in this order.</summary>
	public void SetTabs( IEnumerable<EditorWorkspace> workspaces ) => _strip.SetTabs( workspaces );

	/// <summary>The squares worked out again - the pins or the apps changed.</summary>
	public void Rebuild() => _strip.Rebuild();

	/// <summary>Draw the strip again - which workspace is in front, or how many documents one holds, moved.</summary>
	public void UpdateTabs() => _strip.StateHasChanged();

	/// <summary>How far along this widget the squares reach.</summary>
	public float ContentWidth => _strip.ContentWidth;
}

//An open workspace and its editor; never drawn, parented to the strip only for lifetime
internal sealed class EditorWorkspace : Widget
{
	public string Title { get; }
	public string Icon { get; }

	/// <summary>The layout filling the window while this workspace is current.</summary>
	public Widget Content { get; }

	/// <summary>The editor on screen in this workspace: the document in front, or the one editor it was built from.</summary>
	public Widget Editor => Documents?.CurrentEditor ?? OwnEditor;

	/// <summary>The editor a workspace that is not documents was built from.</summary>
	public Widget OwnEditor { get; init; }

	/// <summary>Type of that editor, so we don't open a second of the same kind.</summary>
	public Type EditorType { get; init; }

	/// <summary>The document tabs, when this workspace holds per-asset editors.</summary>
	public DocumentTabWidget Documents { get; init; }

	/// <summary>Run when this workspace becomes the one being shown.</summary>
	public Action Entered { get; init; }

	/// <summary>Run when it stops being it - closing counts.</summary>
	public Action Left { get; init; }

	public bool CanClose { get; init; } = true;

	public EditorWorkspace( WorkspaceArea owner, string title, string icon, Widget content ) : base( owner.TabBar )
	{
		Title = title;
		Icon = icon;
		Content = content;
		Visible = false;
	}
}
