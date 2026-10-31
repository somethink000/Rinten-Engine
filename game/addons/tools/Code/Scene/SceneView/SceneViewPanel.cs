using Rinten.UI;

namespace Editor;

//The bar across the top, the viewports under it, and the tool strip that lies over them. How the viewports are divided up is an IViewportArrangement rather than a switch in here, so this only has to say which one and hand it somewhere to build.
/// <summary>One scene, shown as one picture or as four.</summary>
[StyleSheet( "SceneViewPanel.cs.scss" )]
public partial class SceneViewPanel : EditorPanel, IViewportSource, IShortcutFallback
{
	public static SceneViewPanel Current { get; private set; }

	//Every scene view there is - one per open session. A workspace lays them all out
	//at once, so switching scenes inside it doesn't drop you back to a single view.
	internal static List<SceneViewPanel> All { get; } = new();

	private ViewportLayoutMode viewportLayout;

	public ViewportLayoutMode ViewportLayout
	{
		get => viewportLayout;
		set => SetLayout( value );
	}

	/// <summary>The viewport that was last worked in, which is where framing happens.</summary>
	public SceneViewportPanel LastSelectedViewport { get; set; }

	public EditorToolManager Tools { get; private set; }

	/// <summary>The currently active session for this scene view. The game session if playing, otherwise the editor session.</summary>
	public SceneEditorSession Session => editorSession.GameSession ?? editorSession;

	/// <summary>Whose tab this view sits in, which is not always what it is looking at.</summary>
	public SceneEditorSession TabSession => editorSession;

	private SceneEditorSession editorSession;

	private readonly List<SplitterPanel> splitters = new();
	private readonly Dictionary<int, SceneViewportPanel> viewports = new();

	private ViewportToolsPanel viewportTools;
	private ToolSidebarPanel toolSidebar;
	private Panel viewportArea;

	public SceneViewPanel( SceneEditorSession session )
	{
		editorSession = session;
		Tools = new EditorToolManager();

		AddClass( "scene-view" );

		All.Add( this );

		RestoreState();

		//A game session's view is the game view, from the moment it is built
		TakeGameView();

		//Opened while mapping is the workspace, it comes up split like the ones already open rather than as a single view
		MappingWorkspaceTools.ApplyTo( this );
	}

	public override void OnDeleted()
	{
		base.OnDeleted();

		SaveState();

		All.Remove( this );

		DestroyViewports();

		if ( Current == this )
			Current = null;

		if ( externalChangesDialog.IsValid() )
			externalChangesDialog.Close();

		externalChangesDialog = null;

		Tools.DisposeAll();
	}

	private static int selectionHash = 0;

	[EditorEvent.Frame]
	public void Frame()
	{
		var session = Session;
		if ( session is null ) return;
		if ( !session.Scene.IsValid() ) return;

		using var scope = session.Scene.Push();

		//Whose tab is current, not what the view is showing. While a game runs there are two views whose Session is that game - its own and the scene it was
		//started from - and both would call themselves active, then take turns each frame claiming Current and rewriting the inspector off a hash they share.
		bool isActive = TabSession == SceneEditorSession.Active;

		//Update inspector with current selection, if changed
		if ( isActive && selectionHash != session.Selection.GetHashCode() && !( ( Tools?.CurrentSubTool ?? Tools?.CurrentTool )?.IsDragSelecting ?? false ) )
		{
			//todo - multiselect
			EditorUtility.InspectorObject = session.Selection.ToArray();
			selectionHash = session.Selection.GetHashCode();
		}

		if ( isActive )
		{
			Current = this;

			//Which tab a tool should hang its widgets on, said here because the pointer never reaches the dock - everything in it is markup.
			(HostWidget as SceneDock)?.MakeActiveDock();
		}

		//All this shit below is scene specific
		if ( CurrentView != SceneViewMode.Game )
		{
			//Ticks the deferred undo buffer, periodically saves camera state
			using ( Rinten.Diagnostics.Performance.Scope( "view session tick" ) )
			{
				session.Tick();
			}

			bool shouldUpdate = IsVisible && session.ShouldUpdate; //Update if the scene window is visible

			if ( !shouldUpdate )
				return;

			//Named because this handler alone is ninety-two per cent of "Tool Frame Event" - measured with
			//-frametrace - and the zone above it said only that the editor's frame events were expensive.
			using ( Rinten.Diagnostics.Performance.Scope( "view scene tick" ) )
			{
				session.Scene.EditorTick( RealTime.Now, RealTime.Delta );
			}
		}

		if ( lastView != CurrentView )
		{
			viewportTools.Refresh();
			lastView = CurrentView;
		}

		//Detect scene swaps here, not via play/stop events, to avoid rendering a destroyed scene
		if ( lastScene != Session?.Scene )
		{
			lastScene = Session?.Scene;
			OnViewModeChanged();
		}
	}

	/// <summary>Every viewport this view is showing, in no particular order.</summary>
	public IEnumerable<SceneViewportPanel> Viewports => viewports.Values;

	public bool TryGetViewport( int id, out SceneViewportPanel viewport )
	{
		return viewports.TryGetValue( id, out viewport );
	}

	Panel IViewportSource.CreateViewport( int id )
	{
		var viewport = new SceneViewportPanel( this, id == -1 ? viewports.Count : id );
		viewports.Add( viewport.ViewportIndex, viewport );
		return viewport;
	}

	SplitterPanel IViewportSource.CreateSplitter( bool vertical )
	{
		var splitter = new SplitterPanel( vertical );
		splitters.Add( splitter );
		return splitter;
	}

	//Lay the viewports out because of the workspace rather than because anybody asked - mapping splits them
	//four ways, General leaves them as one. Not saved as the layout that was chosen, because it wasn't.
	internal void SetWorkspaceLayout( ViewportLayoutMode preset )
	{
		if ( viewportLayout == preset ) return;

		//Rebuilding the viewports out from under a running game takes the game view with them
		if ( Session?.IsPlaying ?? false ) return;

		viewportLayout = preset;

		RebuildLayout();
	}

	public void SetLayout( ViewportLayoutMode preset )
	{
		bool isRefresh = viewportLayout == preset;

		//Asked for, so it is remembered - unlike the split mapping puts on every view while it is open.
		chosenLayout = preset;
		viewportLayout = preset;

		RebuildLayout();

		if ( isRefresh )
		{
			RestoreSplitterState();
		}

		SaveState();
	}

	//Get rid of the viewports and the splitters holding them.
	private void DestroyViewports()
	{
		foreach ( var viewport in viewports.Values )
		{
			viewport.Delete( true );
		}

		foreach ( var splitter in splitters )
		{
			splitter.Delete( true );
		}

		viewports.Clear();
		splitters.Clear();
	}

	public void RebuildLayout()
	{
		DestroyViewports();

		toolSidebar?.Delete( true );
		toolSidebar = null;

		viewportArea?.Delete( true );
		viewportArea = null;

		if ( viewportTools is null )
		{
			viewportTools = new ViewportToolsPanel( this );
			AddChild( viewportTools );
		}

		viewportArea = new Panel();
		viewportArea.AddClass( "viewport-area" );
		AddChild( viewportArea );

		viewportArea.AddChild( ViewportArrangements.For( ViewportLayout ).Build( this ) );

		//The tool strip lies over the picture rather than beside it. As a cell of the row it took a column of its own and squeezed every viewport
		//by its width; over the viewports it is drawn on top of what the layout placed, and lets the mouse through where it has nothing to click.
		toolSidebar = new ToolSidebarPanel( this );
		viewportArea.AddChild( toolSidebar );

		toolSidebar.SetClass( "hidden", CurrentView == SceneViewMode.Game );

		viewportTools.Rebuild();
	}
}
