using Editor.NodeEditor;
using Rinten.UI.NodeGraph;
using Facepunch.ActionGraphs;
using Rinten;
using Rinten.ActionGraphs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Editor.ActionGraphs;

public partial class MainWindow : DockWindow, EditorEvent.ISceneView, IActionGraphEvents
{
	public event Action<object> SelectionChanged;
	public event Action<Node.Input, int?> FocusedOnInput;

	internal static List<MainWindow> AllWindows { get; } = new List<MainWindow>();

	private static string FormatMessageContext( IMessageContext context )
	{
		return context switch
		{
			ActionGraph graph => graph.Title,
			Node node => node.GetDisplayInfo().Name,
			Link link => FormatMessageContext( link.Target ),
			Node.IParameter parameter => FormatMessageContext( parameter.Node ),
			Variable variable => variable.Name,
			_ => $"{context}"
		};
	}

	private static string GetTargetName( ActionGraph graph )
	{
		switch ( graph.GetEmbeddedTarget() )
		{
			case GameObject go:
				return go.Name;

			default:
				return null;
		}
	}

	private static string GetSourceName( ISourceLocation source )
	{
		return source switch
		{
			GameResourceSourceLocation { Resource: SceneFile { ResourcePath: null } } => "Play Mode",
			GameResourceSourceLocation { Resource: SceneFile { ResourceName: { } name } } => name,
			_ => source.ToString()
		};
	}

	public static string GetFullPath( ActionGraph graph )
	{
		if ( graph.SourceLocation is null )
		{
			return null;
		}

		return GetTargetName( graph ) is { } targetName
			? $"{GetSourceName( graph.SourceLocation )} - {targetName} - {graph.Title}"
			: $"{GetSourceName( graph.SourceLocation )} - {graph.Title}";
	}

	[StackLineHandler( @"^at (.+?) in (.+/Facepunch.ActionGraphs/.+):line (.+)$", Order = 910 )]
	public static StackRow ActionGraphLibraryStackLineHandler( Match match )
	{
		var row = new StackRow( match.Value, null ) { IsFromAction = true };

		return row;
	}

	[StackLineHandler( @"at ActionGraph\.(?<guid>[a-fA-F0-9-]+)(?:\.Node(?<nodeid>[0-9]+))?(?:\.[^(]+)?(?:\(.*\))?", Order = 900 )]
	public static StackRow ActionGraphStackLineHandler( Match match )
	{
		if ( !Guid.TryParse( match.Groups["guid"].Value, out var guid ) )
		{
			return null;
		}

		var graph = EditorNodeLibrary.GetGraphs( guid ).FirstOrDefault();

		if ( graph is null )
		{
			return new StackRow( $"Unknown Action Graph ({guid})", null );
		}

		IMessageContext context = graph;

		if ( match.Groups["nodeid"].Success && int.TryParse( match.Groups["nodeid"].Value, out var nodeId ) && graph.Nodes.TryGetValue( nodeId, out var node ) )
		{
			context = node;
		}

		var row = new StackRow( FormatMessageContext( context ), GetFullPath( graph ) )
		{
			IsFromEngine = false,
			IsFromAction = true
		};

		if ( graph.SourceLocation is not null )
		{
			row.MouseClick += () =>
			{
				var view = ActionGraphView.Open( graph );

				switch ( context )
				{
					case Link l:
						view.SelectLink( l );
						break;

					case Node n:
						view.SelectNode( n );
						break;
				}

				view.CenterOnSelection();
			};
		}

		return row;
	}

	private List<ActionGraphView> Views { get; } = new();

	public IReadOnlyList<ActionGraphView> OpenViews => Views;

	public ActionGraphView FocusedView => Views.LastOrDefault();

	public bool CanOpenMultipleAssets => true;

	public MainWindow( Window parent )
	{
		DeleteOnClose = true;

		Size = new Vector2( 1280, 720 );

		//Make this window stay on top of the editor, by making it a dialog
		Parent = parent;
		WindowFlags = WindowFlags.Dialog | WindowFlags.Customized | WindowFlags.CloseButton | WindowFlags.WindowSystemMenuHint | WindowFlags.WindowTitle | WindowFlags.MaximizeButton;

		SetWindowIcon( "electrical_services" );

		AllWindows.Add( this );

		BuildDefaultLayout();
	}

	protected override void OnClosed()
	{
		base.OnClosed();

		AllWindows.Remove( this );
	}

	protected override void OnFocus( FocusChangeReason reason )
	{
		base.OnFocus( reason );

		//Move this window to the end of the list, so it has priority when opening a new graph

		AllWindows.Remove( this );
		AllWindows.Add( this );
	}

	public ActionGraphView Open( ActionGraph actionGraph )
	{
		var view = new ActionGraphView( actionGraph ) { Window = this };
		var sibling = Views.LastOrDefault();

		Views.Add( view );

		var icon = view.ActionGraph.Icon ?? "account_tree";

		view.Dock = new GraphDock( view, view.Name, icon );

		if ( DockManager.FindDockWidget( view.Name ) is { } existing )
		{
			var previous = existing.Widget;

			existing.Widget = view.Dock;

			if ( previous != view.Dock ) previous?.Destroy();

			return view;
		}

		var dock = DockManager.CreateDockWidget( view.Name, icon, view.Dock );

		if ( sibling.IsValid() && DockManager.FindDockWidget( sibling.Name ) is { } siblingDock )
			DockManager.AddDock( dock, siblingDock );
		else
			DockManager.AddDock( dock, DockArea.Center );

		return view;
	}

	internal void UpdateTitle( ActionGraphView view )
	{
		Title = $"{view.ActionGraph.Title} - Action Graph";
	}

	protected override void BuildDefaultLayout()
	{
		var openViews = Views
			.Select( x => x.ActionGraph )
			.ToArray();

		DockManager.RegisterDock( new() { Title = "Inspector", Icon = "edit", Area = DockArea.Left, CreateAction = () => new PropertiesDock( this ) } );
		DockManager.RegisterDock( new() { Title = "ErrorList", Icon = "error", Area = DockArea.Bottom, CreateAction = () => new ErrorList( null, this ) } );

		var inspector = DockManager.OpenDock( "Inspector", DockArea.Left );
		DockManager.OpenDock( "ErrorList", DockArea.Bottom, inspector );

		//gather the graph views back into a single tab group in the middle
		DockWidget viewArea = null;
		foreach ( var graph in openViews )
		{
			var view = Open( graph );
			var dock = DockManager.OpenDock( view.Name, DockArea.Center, viewArea );
			viewArea ??= dock;
		}

		DockManager.SetSplitterProportions( inspector, 0.25f, 0.75f );
	}

	[Shortcut( "editor.quit", "CTRL+Q", ShortcutType.Window )]
	private void Quit()
	{
		Close();
	}

	[Shortcut( "editor.save", "Ctrl+S", ShortcutType.Window )]
	private void Save()
	{
		FocusedView?.Save();
	}

	[Shortcut( "editor.undo", "Ctrl+Z", ShortcutType.Window )]
	private void Undo()
	{
		FocusedView?.Undo();
	}

	[Shortcut( "editor.redo", "Ctrl+Y", ShortcutType.Window )]
	private void Redo()
	{
		FocusedView?.Redo();
	}

	[Shortcut( "editor.cut", "Ctrl+X", ShortcutType.Window )]
	private void CutSelection()
	{
		FocusedView?.CutSelection();
	}

	[Shortcut( "editor.copy", "Ctrl+C", ShortcutType.Window )]
	private void CopySelection()
	{
		FocusedView?.CopySelection();
	}

	[Shortcut( "editor.paste", "Ctrl+V", ShortcutType.Window )]
	private void PasteSelection()
	{
		FocusedView?.PasteSelection();
	}

	[Shortcut( "editor.duplicate", "CTRL+D", ShortcutType.Window )]
	private void DuplicateSelection()
	{
		FocusedView?.DuplicateSelection();
	}

	[Shortcut( "editor.select-all", "Ctrl+A", ShortcutType.Window )]
	private void SelectAll()
	{
		FocusedView?.SelectAll();
	}

	[Shortcut( "editor.clear-selection", "ESC", ShortcutType.Window )]
	private void ClearSelection()
	{
		FocusedView?.ClearSelection();
	}

	[Shortcut( "gameObject.frame", "F", ShortcutType.Window )]
	private void CenterOnSelection()
	{
		FocusedView?.CenterOnSelection();
	}

	private void CleanUp()
	{
		FocusedView?.CleanUp();
	}

	private void LogExpression()
	{
		FocusedView?.LogExpression();
	}

	private void LogInstances()
	{
		FocusedView?.LogInstances();
	}

	internal void OnFocusView( ActionGraphView view )
	{
		Views.Remove( view );
		Views.Add( view );

		DispatchSelectionChanged( view );

		UpdateTitle( view );
	}

	internal void OnRemoveView( ActionGraphView view )
	{
		Views.Remove( view );
	}

	internal void DispatchFocusedOnInput( Node.Input input, int? index )
	{
		FocusedOnInput?.Invoke( input, index );
	}

	internal void DispatchSelectionChanged( ActionGraphView view )
	{
		var node = view.SelectedItems
			.OfType<NodeUI>()
			.MaxBy( n => n is CommentUI );

		SelectionChanged?.Invoke( (object)node ?? view.EditorGraph );
	}

	public void SelectNode( Node node )
	{
		if ( Views.FirstOrDefault( x => x.ActionGraph == node.ActionGraph ) is not { } view )
		{
			return;
		}

		DockManager.RaiseDock( view.Name );

		view.SelectNode( node );
	}

	public void SelectLinks( IEnumerable<Link> links )
	{
		var linkArray = links.ToArray();

		if ( linkArray.FirstOrDefault()?.Target.Node.ActionGraph is not { } graph )
		{
			return;
		}

		if ( Views.FirstOrDefault( x => x.ActionGraph == graph ) is not { } view )
		{
			return;
		}

		DockManager.RaiseDock( view.Name );

		view.SelectLinks( linkArray );
	}

	public void SelectLink( Link link )
	{
		SelectLinks( new[] { link } );
	}

	void EditorEvent.ISceneView.DrawGizmos( Scene scene )
	{
		SceneRefGizmo.Draw( scene );
	}

	void IActionGraphEvents.SceneReferenceTriggered( SceneReferenceTriggeredEvent ev )
	{
		SceneRefGizmo.Trigger( ev );
	}
}
