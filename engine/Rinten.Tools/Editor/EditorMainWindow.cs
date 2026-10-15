using NativeEngine;
using Rinten.Helpers;
using System;

namespace Editor;

public class EditorMainWindow : DockWindow, IUndoSystemProvider
{
	internal static EditorMainWindow Current;

	Menu FileMenu { get; init; }
	public Menu ViewsMenu { get; init; }
	public Menu GameMenu { get; init; }
	Menu EditMenu { get; init; }

	internal ConsoleDock Console => ConsoleDock.Instance;

	//Glass: the UI rides a per-pixel alpha overlay, so the viewport stays solid and only gaps blur
	protected override bool IsGlass => true;

	//The first time the editor is opened on a machine there is nothing saved about where its window goes, and it fills the screen.
	protected override bool OpensMaximized => true;

	//The question on the screen while the cross waits for an answer.
	EditorModal closeQuestion;

	//Set once the question has been answered with Quit, so the close that follows goes rather than asking again.
	bool closeConfirmed;

	protected override bool OnClose()
	{
		//Answered already - this is the close that answer asked for. First, so a workspace that happens to be up is not closed in the editor's place.
		if ( closeConfirmed ) return true;

		//The cross closes what is in front of it. On any workspace but General that is the workspace: its layout goes, General comes
		//back up, and the editor stays where it is - the whole editor is not what somebody on the model editor's tab meant to close.
		if ( Workspaces.IsValid() && Workspaces.CloseCurrent() )
		{
			if ( _general.IsValid() )
				Workspaces.MakeCurrent( _general );

			return false;
		}

		//With no project open there is nothing to lose by going: the editor is the projects over an empty window, and one press is enough.
		if ( Project.Current is null ) return true;

		//In General only the editor is left to close, so ask first; a second click leaves the question up
		if ( !closeQuestion.IsValid() )
		{
			closeQuestion = EditorModal.Confirm( Shell?.Root, "Quit the editor?",
				"Everything not saved will be lost.", QuitAnswered,
				confirm: "Quit", cancel: "Cancel", destructive: true );
		}

		return false;
	}

	//Quit, said to the question: write everything out, then close for real.
	void QuitAnswered()
	{
		closeQuestion = null;
		closeConfirmed = true;

		Quitting();

		if ( Shell is { } shell ) shell.RequestClose();
		else Close();
	}

	//Writes out everything the editor is still holding, on the way out. The cross gets here once its question is answered with Quit - see above. File's
	//own Quit comes straight here: a menu item picked by name is already deliberate, and it means the editor whichever workspace somebody is looking at.
	void Quitting()
	{
		//Nothing here is a question. A resource is written as it is edited and a scene shortly after it
		//stops being edited, so closing the editor writes out whatever the last moments changed and goes.
		SaveResources();
		SceneEditorSession.CheckpointAll();

		//And the bytes those two queued. Writing happens off the frame while the editor is up -
		//see ResourceWrites - and this is the one moment there is no next frame to finish it in.
		GameResource.FlushWrites();

		//A scene that has never been saved has nowhere on the disk to be written to. It is not asked about either: the checkpoint above has it, and
		//standing between somebody and the door to say so is the thing this editor stopped doing. Said in the log, where it costs nobody a click.
		foreach ( var session in GetUnsavedScenes() )
		{
			Log.Info( $"quit: '{session.Scene?.Name}' has never been saved - it is in its checkpoint and nowhere else" );
		}

		ProjectCookie?.Set( EditorScene.GizmoSettingsCookie, EditorScene.GizmoSettings );
	}

	private static List<SceneEditorSession> GetUnsavedScenes()
	{
		return SceneEditorSession.All.Where( x => x.HasUnsavedChanges ).ToList();
	}

	private static List<GameResource> GetUnsavedResources()
	{
		return ResourceLibrary.GetAll<GameResource>().Where( x =>
		{
			var asset = AssetSystem.FindByPath( x.ResourcePath );
			var compiledFile = asset?.GetCompiledFile( true );
			var isCloud = compiledFile != null && compiledFile.Contains( Project.CloudPath );
			return x.HasUnsavedChanges && !isCloud;
		}
		).ToList();
	}

	//Backstop that saves any edited resource not yet written; the inspector normally saves instantly
	private static void SaveResources()
	{
		foreach ( var resource in GetUnsavedResources() )
		{
			var asset = AssetSystem.FindByPath( resource.ResourcePath );

			if ( asset is null ) continue;

			asset.SaveToDisk( resource );
			asset.Compile( false );
		}
	}

	static bool isEngineLoggingVerbose;

	internal SceneTabWidget SceneTabs { get; }
	internal WorkspaceArea Workspaces { get; }
	readonly DockWidget _centralDock;

	EditorWorkspace _general;
	EditorWorkspace _mappingTab;

	//The scene-centred workspaces that are open - mapping, the movie maker - each with its tab.
	readonly List<(EditorWorkspace Tab, SceneWorkspace Content)> _sceneWorkspaces = new();

	private Option undoOption;
	private Option redoOption;

	internal EditorMainWindow()
	{
		Current = this;
		Visible = false;
		Enabled = false;
		WindowTitle = "Rinten editor";
		DeleteOnClose = true;
		FullScreenManager = new();

		//A workspace is a whole layout rather than a panel inside one. General is this window's own docks, with the scene in the middle of them; a big
		//editor opened from Tools brings its layout along and swaps this one out. The tabs are up in the menu bar, from the middle of the window across.
		Workspaces = new WorkspaceArea( this, new WorkspaceTabBar( MenuBar ) );
		Canvas = Workspaces;

		SceneTabs = new SceneTabWidget( DockManager );
		_centralDock = DockManager.SetCentralWidget( SceneTabs );

		_general = Workspaces.AddPermanent( "General", "grid_4x4", DockManager );

		{
			FileMenu = MenuBar.AddMenu( "File" );
			FileMenu.AboutToShow += BuildFileMenu;
			BuildFileMenu();
		}

		{
			EditMenu = MenuBar.AddMenu( "Edit" );

			undoOption = EditMenu.AddOption( "Undo", "undo", Undo, "editor.undo" );
			redoOption = EditMenu.AddOption( "Redo", "redo", Redo, "editor.redo" );

			EditMenu.AddSeparator();
			EditMenu.AddOption( "Cut", "cut", EditorScene.Cut, "editor.cut" );
			EditMenu.AddOption( "Copy", "copy", EditorScene.Copy, "editor.copy" );
			EditMenu.AddOption( "Paste", "paste", EditorScene.Paste, "editor.paste" );
			EditMenu.AddOption( "Paste Special", "content_paste_go", EditorScene.PasteSpecial, "editor.paste-special" );
			EditMenu.AddOption( "Paste As Child", null, EditorScene.PasteAsChild, "editor.paste-as-child" );
			EditMenu.AboutToShow += OnEditMenuAboutToShow;
		}

		{
			ViewsMenu = MenuBar.AddMenu( "View" );
			ViewsMenu.AboutToShow += OnViewsMenuAboutToShow;
		}

		{
			var gameMenu = MenuBar.AddMenu( "Game" );
			gameMenu.AddOption( "Play", "play_arrow", EditorScene.TogglePlay, "editor.toggle-play" );

			gameMenu.AddOption( new Option()
			{
				Checkable = true,
				Checked = EditorScene.PlayMode,
				Toggled = ( b ) => EditorScene.PlayMode = b,
				Text = "Play in Game Mode",
				Icon = "sports_esports"
			} );

			gameMenu.AddSeparator();
		}

		{
			MenuBar.AddMenu( "Scene" );
		}

		{
			MenuBar.AddMenu( "Settings" );
			var debug = MenuBar.AddMenu( "Debug" );


			debug.AddSeparator();

			var help = MenuBar.AddMenu( "Help" );

			help.AddOption( "Open Log Folder", "source", () => EditorUtility.OpenFolder( FileSystem.Root.GetFullPath( "/logs/" ) ) );

			help.AddSeparator();
			help.AddOption( "About Rinten editor", "info", () =>
			{
				var aboutWidget = new AboutWidget();
				aboutWidget.SetModal( true, true );
				aboutWidget.Show();
			} );

			help.AddSeparator();

			var di = DisplayInfo.ForEnumValues<LogLevel>();
			var rootRule = Logging.GetDefaultLevel();

			{
				var o = help.AddOption( "Trace Logging" );
				o.Checkable = true;
				o.FetchCheckedState = () => Logging.GetDefaultLevel() == LogLevel.Trace;
				o.Toggled = ( b ) =>
				{
					Logging.SetRule( "*", b ? LogLevel.Trace : LogLevel.Info );
					EditorCookie.Set( "DefaultLoggingLevel", b ? LogLevel.Trace : LogLevel.Info );
				};
			}

			{
				var o = help.AddOption( "Hotload Logging" );
				o.Checkable = true;
				o.FetchCheckedState = () => HotloadManager.hotload_log == 2;
				o.Toggled = ( b ) =>
				{
					HotloadManager.hotload_log = b ? 2 : 0;
				};
			}

			{
				var o = help.AddOption( "Verbose Engine Logging" );
				o.Checkable = true;
				o.FetchCheckedState = () => isEngineLoggingVerbose;
				o.Toggled = ( b ) =>
				{
					//The engine logs through the same listeners the editor already shows.
					isEngineLoggingVerbose = b;
				};
			}
		}

		EditorWindow = this;
	}

	/// <summary>Whether the mapping workspace has an open tab, even when it is not active.</summary>
	public bool IsMappingOpen => _mappingTab.IsValid();

	/// <summary>Open the mapping workspace, or switch to it if it is already open.</summary>
	public void OpenMapping()
	{
		_mappingTab = OpenSceneWorkspace( "Mapping", "hardware", () => new MappingWorkspace( Workspaces ), "mapping", out _ );
	}

	/// <summary>Open a workspace laid out around the scene, or switch to it if one of this title is open. The scene area moves into it while it is in front and back to General when it is left; <paramref name="events"/>, when given, is run as "<c>{events}.entered</c>" and "<c>{events}.left</c>" after each move.</summary>
	public void OpenSceneWorkspace( string title, string icon, Func<SceneWorkspace> build, string events = null )
		=> OpenSceneWorkspace( title, icon, build, events, out _ );

	internal EditorWorkspace OpenSceneWorkspace( string title, string icon, Func<SceneWorkspace> build, string events, out SceneWorkspace content )
	{
		_sceneWorkspaces.RemoveAll( x => !x.Tab.IsValid() || !x.Content.IsValid() );

		var open = _sceneWorkspaces.FirstOrDefault( x => x.Tab.Title == title );

		if ( open.Tab is not null )
		{
			Workspaces.MakeCurrent( open.Tab );
			content = open.Content;
			return open.Tab;
		}

		SceneWorkspace built = null;

		var tab = Workspaces.Open( title, icon, () => built = build(),
			() => EnterScene( built, events ), () => LeaveScene( built, events ) );

		content = built;

		if ( tab.IsValid() && built.IsValid() )
			_sceneWorkspaces.Add( (tab, built) );

		return tab;
	}

	//Focus the workspace currently holding the scene area, not mapping just because it's open
	internal void FocusSceneWorkspace()
	{
		var holding = _sceneWorkspaces.FirstOrDefault( x => x.Tab.IsValid() && x.Content.IsValid() && x.Content.ContainsScene( SceneTabs ) );

		var workspace = holding.Tab ?? _general;

		if ( workspace.IsValid() )
			Workspaces.MakeCurrent( workspace );
	}

	//The scene area moves into a scene workspace rather than being built twice - it is where the open sessions live, and there is only one of it.
	void EnterScene( SceneWorkspace workspace, string events )
	{
		if ( !workspace.IsValid() ) return;

		_centralDock.Widget = null;
		workspace.TakeScene( SceneTabs );

		if ( events is not null )
			EditorEvent.Run( events + ".entered" );
	}

	void LeaveScene( SceneWorkspace workspace, string events )
	{
		if ( !workspace.IsValid() ) return;

		workspace.ReleaseScene();
		_centralDock.Widget = SceneTabs;

		if ( events is not null )
			EditorEvent.Run( events + ".left" );
	}

	[Shortcut( "editor.open-solution", "CTRL+P", ShortcutType.Window )]
	void OpenSolution()
	{
		CodeEditor.OpenSolution();
	}

	IUndoSystem IUndoSystemProvider.UndoSystem => SceneEditorSession.Current?.UndoSystem;

	//Undoes in whichever visible undo system changed last - the scene's, or a movie editor's when the last edit wrote a keyframe - see IUndoSystem.
	[Shortcut( "editor.undo", "CTRL+Z", ShortcutType.Window )]
	static void Undo()
	{
		if ( SceneEditorSession.Current is not { } session ) return;

		if ( session.IsUndoScopeOpen )
		{
			session.FailUndoRedo();
			return;
		}

		using var scope = SceneEditorSession.Scope();

		if ( IUndoSystem.Undo( EditorWindow ) )
		{
			session.SucceedUndoRedo();
		}
	}

	[Shortcut( "editor.redo", "CTRL+Y", ShortcutType.Window )]
	static void Redo()
	{
		if ( SceneEditorSession.Current is not { } session ) return;

		if ( session.IsUndoScopeOpen )
		{
			session.FailUndoRedo();
			return;
		}

		using var scope = SceneEditorSession.Scope();

		if ( IUndoSystem.Redo( EditorWindow ) )
		{
			session.SucceedUndoRedo();
		}
	}

	//Quits, from File's own menu item - and from an agent's editor_quit, which is the same deliberate ask. No key of its own:
	//Ctrl+Q sat on the editor for anybody whose hand slipped one key left of W, and nothing in the editor said it was there.
	internal static void Quit()
	{
		if ( Current is not { } window ) return;

		//Everything the cross does bar the workspace it closes first: File's Quit is about the editor, not about whichever tab is up.
		window.Quitting();
		window.Close();
	}

	[Shortcut( "editor.video", "F6", ShortcutType.Window )]
	static void ToggleVideo()
	{
		if ( Game.IsPlaying ) return;
		ConVarSystem.Run( "video" );
	}

	protected override void OnPaint()
	{
		if ( Game.IsPlaying )
		{
			Paint.ClearPen();
			Paint.SetBrush( Theme.Overlay );
			Paint.DrawRect( LocalRect );
			return;
		}

		base.OnPaint();
	}

	//Whether the built-in projects are in, and with them the docks the default layout is made of.
	bool docksReady;

	//The editor, up with no project in it: the window and its menus now, the docks
	//once the built-in projects that define them are in - see OnBuiltInsReady.
	internal void StartEmpty()
	{
		PrepareSize();

		//Register our menu bar, doesn't open anything
		using ( var _ = Rinten.Engine.Bootstrap.StartupTiming?.ScopeTimer( "Start Editor: Menus" ) )
			MenuBar.RegisterNamed( "Editor", MenuBar );

		//The workspace tabs are a project's - General is its scenes - so the bar has none until there is one. The title bar's buttons wait the same way.
		Workspaces.TabBar.Visible = false;
		MenuBar.TitlesHidden = true;

		using ( var _ = Rinten.Engine.Bootstrap.StartupTiming?.ScopeTimer( "Start Editor: Open Window" ) )
			SetVisible( true );

		//Where the window was, or filling the screen the first time. The layout that goes with it waits for the docks - see BuildDefaultLayout.
		using ( var _ = Rinten.Engine.Bootstrap.StartupTiming?.ScopeTimer( "Start Editor: Restore Geometry" ) )
			StateCookie = "RintenSceneEditor";
	}

	//The built-in projects are in: their docks and menus join the window, laid out the default way until a project brings the layout it was left in.
	internal void OnBuiltInsReady()
	{
		docksReady = true;

		MenuBar.RegisterNamed( "Editor", MenuBar );
		DockAttribute.RegisterWindow( "Editor", this );

		RestoreLayout();

		EditorEvent.Run( "editor.created", this );

		RebuildApps();
	}

	//A project is open in the editor: what it had open, laid out the way it was.
	internal void OnProjectOpened()
	{
		//Load gizmo settings
		EditorScene.RestoreState();

		//The project's own editor code may bring menus, docks and shortcuts of its own
		MenuBar.RegisterNamed( "Editor", MenuBar );
		DockAttribute.RegisterWindow( "Editor", this );
		EditorShortcuts.RegisterShortcuts();

		//Reopen last session's scenes so their docks exist before the layout is restored
		SceneEditorSession.RestoreOpenSessions();

		//The project's layout, out of its own cookie. The one up to now was the default, kept in memory while there was no project to keep it in.
		RestoreLayout();

		RebuildApps();

		//What waited for a project in the bar along the top: the workspace tabs, and the buttons, which are built on the next refresh.
		Workspaces.TabBar.Visible = true;
		MenuBar.TitlesHidden = false;
		ManagedTools.AssembliesDirty = true;

		EditorEvent.Run( "project.opened", this );
	}

	//What the window says while a project is being opened in it.
	internal void ShowOpening( string path )
	{
		WindowTitle = $"Opening {System.IO.Path.GetFileNameWithoutExtension( path )} - Rinten editor";
	}

	public override void SaveToStateCookie()
	{
		//Measured runs must not save state; they exit mid-frame and would persist a broken layout
		if ( PerfScenario.Measuring ) return;

		base.SaveToStateCookie();

		SceneEditorSession.SaveOpenSessions();
	}

	protected override void BuildDefaultLayout()
	{
		//Not before the docks exist: the layout is made of them, and a dock nobody has registered yet opens as nothing.
		if ( !docksReady ) return;

		//classic layout: hierarchy left, inspector right, asset browser + console under the scene
		var hierarchy = DockManager.OpenDock( "Hierarchy", DockArea.Left );
		DockManager.OpenDock( "Inspector", DockArea.Right );
		var browser = DockManager.OpenDock( "Asset Browser", DockArea.Bottom, _centralDock );
		DockManager.OpenDock( "Console", DockArea.Center, browser );

		DockManager.SetSplitterProportions( hierarchy, 0.2f, 0.6f, 0.2f );

		//A little under a third. The browser is a grid of thumbnails and a tree beside it, and a quarter of the window left both
		//of them showing one row - the panel you pick things out of should be tall enough to pick from without scrolling first.
		DockManager.SetSplitterProportions( browser, 0.68f, 0.32f );
	}

	//Called when the console key is pressed while the game is focused. Should do everything possible to switch to the actual console.
	internal void ConsoleFocus()
	{
		//focus the editor window instead of the game window
		EditorWindow.Blur();
		EditorWindow.Focus( true );

		//focus the console input if it's visible. The console key is used to switch between game
		//and editor too, so don't be heavy handed by forcing the console to be visible etc.
		if ( Console?.Visible ?? false )
		{
			Console.FocusInput();
		}
	}

	internal static bool showLauncherOnExit = false;
	public override void OnDestroyed()
	{
		//Unsubscribe from events
		if ( FileMenu != null ) FileMenu.AboutToShow -= BuildFileMenu;
		if ( ViewsMenu != null ) ViewsMenu.AboutToShow -= OnViewsMenuAboutToShow;
		if ( EditMenu != null ) EditMenu.AboutToShow -= OnEditMenuAboutToShow;

		base.OnDestroyed();

		if ( Rinten.Internal.GlobalToolsNamespace.EditorWindow != this )
			return;

		Rinten.Internal.GlobalToolsNamespace.EditorWindow = null;
		EditorUtility.Quit( showLauncherOnExit );
	}

	//Un-maximized size for a first run, fitted to the usable desktop instead of a fixed 1920x1080
	internal void PrepareSize()
	{
		var (_, _, width, height) = Rinten.CoreWindow.UsableDesktop();

		Size = width > 64 && height > 64
			? new Vector2( width * 0.8f, height * 0.8f )
			: new Vector2( 1280, 800 );
	}

	//Apps live on the tool strip (WorkspaceTabBar), rebuilt on code reload
	[Event( "refresh" )]
	void RebuildApps()
	{
		Workspaces?.TabBar?.Rebuild();
	}

	FullScreenManager FullScreenManager { get; set; }

	/// <summary>Is a widget currently the fullscreen widget</summary>
	public bool IsFullscreen( Widget widget )
	{
		return FullScreenManager.Widget == widget;
	}

	/// <summary>Sets a widget as the fullscreen widget</summary>
	public bool SetFullscreen( Widget widget )
	{
		if ( FullScreenManager.Widget == widget || !widget.IsValid() )
		{
			FullScreenManager.Clear();
			return false;
		}

		FullScreenManager.SetWidget( widget );

		return FullScreenManager.Widget == widget;
	}

	[Event( "asset.selected" )]
	public void OnAssetSelected( Asset asset )
	{
		//maybe an option for this if people bitch about it
		EditorUtility.PlayAssetSound( asset );
	}

	public void SetVisible( bool visible )
	{
		if ( visible )
		{
			EditorWindow.Enabled = true;
			EditorWindow.Visible = true;
			EditorWindow.Focus();
		}
		else
		{
			EditorWindow.Enabled = false;
			EditorWindow.Visible = false;
		}

		Update();
	}

	//The app whose tab is in front, or null on General and the workspaces built around the scene.
	EditorAppAttribute CurrentApp => EditorAppAttribute.For( Workspaces?.CurrentWorkspace?.EditorType );

	//The editor in front first - its name and what it opens - then what the whole editor does. No save entries:
	//what is edited is written as it changes, and the asset browser makes and finds scenes.
	void BuildFileMenu()
	{
		FileMenu.Clear();

		var app = CurrentApp;
		var open = app?.OpenLabel();

		FileMenu.AddHeading( open is null ? "Scene" : app.Title );
		FileMenu.AddOption( open ?? "Open Scene..", "file_open", OpenInCurrentWorkspace, "editor.open" );
		FileMenu.AddSeparator();
		//The build page lives in the tools addon, which this cannot name - so it is asked for by event. See ProjectPage.OpenPage.
		FileMenu.AddOption( "Build Game..", "publish", () => EditorEvent.Run( "project.build.open" ) );
		FileMenu.AddSeparator();
		FileMenu.AddOption( "Open Launcher", "rocket_launch", StartupLoadProject.ShowLauncher );
		FileMenu.AddOption( "Quit", "logout", Quit );
	}

	//File > Open: the asset picker, showing only what the tab in front opens - models on the model editor's, a razor
	//panel under Code on the UI editor's, and a scene or a prefab on General and the workspaces built around the scene.
	internal void OpenInCurrentWorkspace()
	{
		if ( CurrentApp?.AskForFile() == true ) return;

		EditorAppAttribute.PickFile( "Open Scene", new[] { "scene", "prefab" }, path =>
		{
			if ( AssetSystem.FindByPath( path ) is { } asset )
				asset.OpenInEditor();
		} );
	}

	private void OnEditMenuAboutToShow()
	{
		UpdateEditMenu( undoOption, redoOption );
	}

	private void OnViewsMenuAboutToShow()
	{
		CreateDynamicViewMenu( ViewsMenu );
	}

	public void UpdateEditorTitle( string title )
	{
		WindowTitle = $"{title} - Rinten editor{(Global.IsApiConnected ? "" : " - offline")}";
	}

	//Updates Undo/Redo states and text
	void UpdateEditMenu( Option undoOption, Option redoOption )
	{
		if ( SceneEditorSession.Active?.UndoSystem.Back.TryPeek( out var undoEntry ) ?? false )
		{
			undoOption.Enabled = true;
			undoOption.Text = $"Undo {undoEntry.Name}";
		}
		else
		{
			undoOption.Enabled = false;
			undoOption.Text = "Undo";
		}

		if ( SceneEditorSession.Active?.UndoSystem.Forward.TryPeek( out var redoEntry ) ?? false )
		{
			redoOption.Enabled = true;
			redoOption.Text = $"Redo {redoEntry.Name}";
		}
		else
		{
			redoOption.Enabled = false;
			redoOption.Text = "Redo";
		}
	}
}
