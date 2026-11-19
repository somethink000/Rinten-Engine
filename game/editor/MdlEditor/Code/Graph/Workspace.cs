using Editor.MdlEditor.Nodes;
using Rinten.UI;

using UILabel = Rinten.UI.Label;

namespace Editor.MdlEditor;

internal readonly record struct SecondaryGraphInfo(
	Data Graph,
	string Title,
	string Kind,
	string Icon,
	Color Tint,
	string Key );

//The graph with an overlaid strip at its foot showing the selection's second graph
[StyleSheet( "Workspace.cs.scss" )]
internal sealed class Workspace : EditorPanel
{
	//The grab strip between the graph and the secondary view.
	private sealed class Handle : Panel
	{
		private readonly Workspace _workspace;
		private bool _dragging;
		private bool _startedCollapsed;
		private float _startY;
		private float _startHeight;

		public Handle( Workspace workspace )
		{
			_workspace = workspace;

			AddClass( "handle" );
		}

		protected override void OnMouseDown( MousePanelEvent e )
		{
			base.OnMouseDown( e );

			if ( e.MouseButton != MouseButtons.Left )
				return;

			_dragging = true;
			_startY = _workspace.LocalMouseY;
			_startHeight = _workspace.AppliedHeight;
			_startedCollapsed = _workspace.IsCollapsed;
			_workspace.BeginResize();

			e.StopPropagation();
		}

		/// <summary>The drag is read here rather than from mouse moves: the pointer is off this four pixel strip the moment it starts moving, and a panel hears neither a move nor a button coming up somewhere else.</summary>
		public override void Tick()
		{
			base.Tick();

			if ( !_dragging )
				return;

			//Measured against the workspace, which is the one thing in this that does not move while it is being dragged.
			var height = _startHeight - (_workspace.LocalMouseY - _startY);

			//Dragged up off the header it opens properly rather than as a sliver; dragged below what an open strip is worth it shuts.
			if ( _startedCollapsed && height > SecondaryHeaderHeight )
				height = Math.Max( height, MinimumExpandedHeight );
			else if ( !_startedCollapsed && height < MinimumExpandedHeight )
				height = SecondaryHeaderHeight;

			_workspace.SecondaryHeight = height;

			if ( Application.MouseButtons.HasFlag( MouseButtons.Left ) )
				return;

			_dragging = false;
			_workspace.EndResize();
		}
	}

	//How tall the grab strip is: the theme's one separator width, which .handle in the sheet also reads.
	private static float HandleHeight => Theme.SeparatorWidth;

	private const float MinimumPrimaryHeight = 100;
	private const float MinimumExpandedHeight = 96;

	//The shut height of the strip, which is its header and nothing else. Must agree with .secondary-header
	//in the sheet - the drag works in the units the sheet is written in and has to know where the bottom is.
	private static float SecondaryHeaderHeight => Theme.RowHeight + 2;

	private readonly Window _window;
	private readonly Handle _handle;
	private readonly Panel _secondaryPanel;
	private readonly View _secondaryView;
	private readonly CrumbButtonPanel _secondaryOwner;
	private readonly UILabel _secondaryChevron;
	private readonly CrumbButtonPanel _secondaryGraphType;
	private readonly PanelToolbarButton _secondaryFit;
	private readonly PanelToolbarButton _secondaryOpen;
	private readonly PanelToolbarButton _secondaryToggle;
	private readonly Panel _empty;
	private readonly Dictionary<string, (Vector2 Center, float Scale)> _viewMemory = new();

	private SecondaryGraphInfo? _secondary;
	private string _shownGraph;
	private string _pendingFrameGraph;
	private bool _rebuilding;
	private bool _collapsed;
	private bool _preserveCenter;
	private bool _finishResize;
	private Vector2 _secondaryCenter;
	private float _lastExpandedHeight;
	private float _appliedHeight = -1;
	private bool _secondaryViewEnabled = true;

	//Where the main graph and the node pages go, under the strip.
	internal Panel PrimaryHost { get; }

	internal View SecondaryView => _secondaryView;

	//Whether the strip is shut down to its header. Told rather than measured. Nothing writes a height
	//back onto a panel, so asking how tall the strip came out is asking the answer of a frame ago.
	internal bool IsCollapsed => _collapsed;

	//Whether the secondary graph is a thing anybody can see.
	internal bool IsSecondaryShown => _secondaryViewEnabled && !_collapsed && _shownGraph is not null;

	internal bool SecondaryViewEnabled
	{
		get => _secondaryViewEnabled;
		set
		{
			if ( _secondaryViewEnabled == value )
				return;

			_secondaryViewEnabled = value;
			_handle.SetClass( "hidden", !value );
			_secondaryPanel.SetClass( "hidden", !value );

			UpdateVisibility();
		}
	}

	//This panel's height in the units the sheet is written in.
	private float Height => Box.Rect.Height * ScaleFromScreen;

	//Where the pointer is down this panel, in those same units.
	internal float LocalMouseY => MousePosition.y * ScaleFromScreen;

	//How tall the strip actually is, which is not always what it asked for. A drag has to start from this and not from the request: in a workspace
	//too short for the height that was asked for, starting from the request leaves the handle dead until the pointer has travelled the difference.
	internal float AppliedHeight => _appliedHeight < 0 ? SecondaryHeight : _appliedHeight;

	//How tall the strip is asked to be, shut or open.
	private float SecondaryHeight
	{
		get => _collapsed ? SecondaryHeaderHeight : _lastExpandedHeight;
		set
		{
			_collapsed = value <= SecondaryHeaderHeight + 1;

			if ( !_collapsed )
				_lastExpandedHeight = Math.Max( value, MinimumExpandedHeight );

			UpdateCollapsedState();
			Apply();
		}
	}

	internal Workspace( Window window )
	{
		_window = window;

		AddClass( "graph-workspace" );

		PrimaryHost = AddChild<Panel>( "page-host" );

		_handle = AddChild( new Handle( this ) );

		_secondaryPanel = AddChild<Panel>( "secondary" );

		var header = _secondaryPanel.AddChild<Panel>( "secondary-header" );

		_secondaryOwner = header.AddChild( new CrumbButtonPanel( "", "", null, null, false ) );

		_secondaryChevron = header.AddChild<UILabel>( "chevron" );
		_secondaryChevron.Text = "›";

		_secondaryGraphType = header.AddChild( new CrumbButtonPanel( "", "", null, null, true ) );

		var actions = header.AddChild<PanelToolbar>();
		_secondaryFit = actions.AddIcon( "fit_screen", "Fit View", () => _secondaryView.FitView() );
		_secondaryOpen = actions.AddIcon( "open_in_full", "Open Full Graph", OpenSecondaryGraph );
		_secondaryToggle = actions.AddIcon( "expand_more", "Collapse Secondary View", ToggleSecondaryView );

		_secondaryView = _secondaryPanel.AddChild( new View( window ) );
		_secondaryView.OnSelectionChanged += RestorePrimarySelection;

		//What the strip says while nothing with a second graph is selected.
		_empty = _secondaryPanel.AddChild<Panel>( "empty" );
		_empty.AddChild<UILabel>( "material-icon glyph" ).Text = "ads_click";
		_empty.AddChild<UILabel>( "title" ).Text = "Nothing Selected";
		_empty.AddChild<UILabel>( "hint" ).Text = "Select something with a secondary graph to edit it here";

		_lastExpandedHeight = ProjectCookie.Get(
			"AnimGraph.SecondaryHeight",
			ProjectCookie.Get( "AnimGraph.StateMachineSecondaryHeight", 160f ) );
		_collapsed = ProjectCookie.Get(
			"AnimGraph.SecondaryCollapsed",
			ProjectCookie.Get( "AnimGraph.StateMachineSecondaryCollapsed", false ) );

		UpdateCollapsedState();
		Apply();
	}

	internal void Show( object context, object selected )
	{
		if ( _window.ResolveSecondaryGraph( context, selected ) is not { } secondary )
		{
			RememberView();
			_secondary = null;
			_shownGraph = null;
			_pendingFrameGraph = null;
			_secondaryOwner.SetContent( "Secondary View", "account_tree" );
			UpdateHeader();
			UpdateVisibility();
			return;
		}

		_secondary = secondary;
		_secondaryOwner.SetContent( secondary.Title, secondary.Icon, secondary.Tint );
		_secondaryGraphType.SetContent( secondary.Kind, "" );
		UpdateHeader();

		if ( _shownGraph != secondary.Key || !ReferenceEquals( _secondaryView.Graph, secondary.Graph ) )
		{
			RememberView();
			_shownGraph = secondary.Key;

			_rebuilding = true;
			try
			{
				using ( _secondaryView.SuppressSelectionForwarding() )
					_secondaryView.Graph = secondary.Graph;
			}
			finally
			{
				_rebuilding = false;
			}

			if ( _viewMemory.TryGetValue( secondary.Key, out var view ) )
			{
				_pendingFrameGraph = null;
				_secondaryView.SetView( view.Center, view.Scale );
			}
			else
			{
				_pendingFrameGraph = secondary.Key;
			}
		}

		UpdateVisibility();
		TryFrameSecondaryView();
	}

	private void OpenSecondaryGraph()
	{
		if ( _secondary is not { } secondary )
			return;

		_window.NavigateInto( secondary.Graph, secondary.Title, secondary.Icon );
	}

	private void ToggleSecondaryView()
	{
		PreserveCenter();

		SecondaryHeight = _collapsed ? _lastExpandedHeight : SecondaryHeaderHeight;
		FinishResize();
	}

	private void UpdateCollapsedState()
	{
		_secondaryToggle.SetIcon( _collapsed ? "expand_less" : "expand_more" );
		_secondaryToggle.Tooltip = _collapsed ? "Expand Secondary View" : "Collapse Secondary View";

		UpdateHeader();
		UpdateVisibility();
	}

	//Which parts of the header have anything to say.
	private void UpdateHeader()
	{
		var has = _secondary is not null;

		_secondaryChevron.SetClass( "hidden", !has );
		_secondaryGraphType.SetClass( "hidden", !has );
		_secondaryOpen.SetClass( "hidden", !has );

		//Nothing to fit while the graph it would fit is shut away.
		_secondaryFit.SetClass( "hidden", !has || _collapsed );
	}

	private void UpdateVisibility()
	{
		_secondaryView.SetClass( "hidden", !IsSecondaryShown );
		_empty.SetClass( "hidden", _shownGraph is not null || _collapsed );
	}

	//Gives the strip its height and lifts the handle onto it. Written only when it changes. The
	//strip is placed by the sheet otherwise, and a style set every frame is a layout every frame.
	private void Apply()
	{
		var height = SecondaryHeight;

		if ( Height > 0 )
		{
			//Never so tall that there is no graph left above it.
			var maximum = Math.Max( SecondaryHeaderHeight, Height - HandleHeight - MinimumPrimaryHeight );

			//In a workspace this short an open strip would be a sliver, so it shows its header and waits for the window to be given some room.
			height = maximum < MinimumExpandedHeight
				? SecondaryHeaderHeight
				: Math.Clamp( height, SecondaryHeaderHeight, maximum );
		}

		if ( MathF.Abs( height - _appliedHeight ) < 0.5f )
			return;

		_appliedHeight = height;
		_secondaryPanel.Style.Height = height;
		_handle.Style.Bottom = height;
	}

	public override void Tick()
	{
		base.Tick();

		//The room the strip is allowed changes with the window, and a panel is not told that it was resized.
		Apply();
	}

	private void RestorePrimarySelection()
	{
		if ( !_rebuilding && !_secondaryView.SelectedItems.OfType<Rinten.UI.NodeGraph.NodeUI>().Any() )
			_window.RestorePrimarySelection();
	}

	private void RememberView()
	{
		if ( _shownGraph is not null && _secondaryView.Scale > 0 )
			_viewMemory[_shownGraph] = (_secondaryView.Center, _secondaryView.Scale);
	}

	private void PreserveCenter()
	{
		if ( !IsSecondaryShown || _secondaryView.Graph is null )
			return;

		_secondaryCenter = _secondaryView.Center;
		_preserveCenter = true;
	}

	private void BeginResize() => PreserveCenter();

	private void EndResize() => FinishResize();

	private void FinishResize()
	{
		_finishResize = _preserveCenter;
		SaveState();
	}

	private void SaveState()
	{
		ProjectCookie.Set( "AnimGraph.SecondaryHeight", _lastExpandedHeight );
		ProjectCookie.Set( "AnimGraph.SecondaryCollapsed", _collapsed );
	}

	public override void OnDeleted()
	{
		SaveState();

		base.OnDeleted();
	}

	[EditorEvent.Frame]
	private void TryFrameSecondaryView()
	{
		if ( _preserveCenter )
		{
			_secondaryView.CenterOn( _secondaryCenter );

			if ( _finishResize )
			{
				_preserveCenter = false;
				_finishResize = false;
			}
		}

		if ( _pendingFrameGraph is null
			|| _pendingFrameGraph != _shownGraph
			|| _collapsed
			|| _secondaryView.Box.Rect.Width <= 0
			|| _secondaryView.Box.Rect.Height <= 0 )
			return;

		var nodes = _secondaryView.Items.OfType<Rinten.UI.NodeGraph.NodeUI>().ToArray();
		if ( nodes.Length == 0 )
		{
			_pendingFrameGraph = null;
			return;
		}

		var bounds = nodes[0].SceneRect;
		foreach ( var node in nodes.Skip( 1 ) )
			bounds.Add( node.SceneRect );

		var viewport = Vector2.Max( _secondaryView.Box.Rect.Size - 32, new Vector2( 1 ) );
		var span = Vector2.Max( bounds.Size, new Vector2( 1 ) );
		var scale = Math.Clamp( MathF.Min( viewport.x / span.x, viewport.y / span.y ), 0.65f, 1 );

		_secondaryView.SetView( bounds.Center, scale );
		_pendingFrameGraph = null;
	}
}
