using Editor.Inspectors;
using UILabel = Rinten.UI.Label;
using Rinten.UI;

namespace Editor.MdlEditor;

/// <summary>Dock for graph variations: pick one, give it a model, choose which nodes override their data</summary>
public class VariationEditor : PanelDockHost
{
	private readonly VariationEditorPanel _panel;

	public VariationEditor( Window window ) : base( null )
	{
		Name = "Variations";
		WindowTitle = "Variations";
		SetWindowIcon( "call_split" );

		_panel = new VariationEditorPanel( window );

		SetContent( _panel );
	}

	/// <summary>Read the graph again - a variation, a model or an override changed.</summary>
	public void Rebuild() => _panel?.Read();

	internal void OnGraphEdited() => _panel?.Read();

	//The inspector is pointed at something else; the rows say which node that is.
	internal void UpdateSelection() => _panel?.UpdateSelection();

	public override void OnDestroyed()
	{
		_panel?.SaveSplitter();

		base.OnDestroyed();
	}

	//Rows are a touch taller than a standard control row, so the pills have some room to breathe.
	internal static float PillHeight => Theme.RowHeight + 1;
}

//The variations over the nodes, with a seam between them you can drag.
[StyleSheet( "VariationEditor.cs.scss" )]
internal sealed class VariationEditorPanel : EditorPanel
{
	private const string SplitterCookie = "AnimGraph.VariationSplitter";
	private const string CollapsedCookie = "AnimGraph.VariationsCollapsed";

	private readonly Window _window;
	private readonly SplitterPanel _splitter;

	private readonly VariationHeader _treeHeader;
	private readonly VariationHeader _nodeHeader;
	private readonly Panel _pickers;
	private readonly VariationTree _tree;
	private readonly VariationNodeList _nodes;
	private readonly UILabel _empty;

	private readonly PanelToolbarButton _rename;
	private readonly PanelToolbarButton _delete;
	private readonly PanelToolbarButton _overridesOnly;
	private readonly TextField _filter;

	private bool _showOverridesOnly;

	//Which variations the user has folded away, kept across sessions.
	internal HashSet<string> Collapsed { get; }

	public VariationEditorPanel( Window window )
	{
		_window = window;
		Collapsed = ProjectCookie.Get<List<string>>( CollapsedCookie, [] ).ToHashSet();

		AddClass( "variation-editor" );

		_splitter = AddChild( new SplitterPanel( vertical: true ) );

		//The variations, and what to do to them
		var variations = new Panel();
		variations.AddClass( "section" );

		_treeHeader = variations.AddChild( new VariationHeader( "Variations" ) );
		_treeHeader.Tooltip = "Each variation animates its own model and can override the data of any node. Click a variation to edit it, right-click for more options.";

		//Fills whatever the title and the buttons do not need.
		_pickers = _treeHeader.AddChild<Panel>( "pickers" );

		var treeButtons = _treeHeader.AddChild<PanelToolbar>();

		treeButtons.AddIcon( "add", "Create a new variation as a child of the selected one",
			() => _window.PromptCreateVariation( _window.SelectedVariationID ) ).AddClass( "add" );

		_rename = treeButtons.AddIcon( "edit", "Rename the selected variation",
			() => _window.PromptRenameVariation( _window.SelectedVariationID ) );

		_delete = treeButtons.AddIcon( "delete", "Delete the selected variation and all of its children",
			() => _window.PromptDeleteVariation( _window.SelectedVariationID ) );

		_delete.AddClass( "delete" );

		_tree = variations.AddChild( new VariationTree( window, this ) );
		//On the dock's ground like every other tree - design.scss's .tree-frame - not on a list's sunk plate.
		_tree.AddClass( "tree-frame" );

		_splitter.AddCell( variations, 0.4f );

		//The nodes that can hold data of their own
		var nodes = new Panel();
		nodes.AddClass( "section" );

		_nodeHeader = nodes.AddChild( new VariationHeader( "Nodes" ) );
		_nodeHeader.Tooltip = "Every node that can hold per variation data - clips, IK setups, referenced graphs. Add an override to give the selected variation its own data, otherwise it inherits.";

		//This bar carries the filter, so it is the part of the header that gives - the tree's bar is buttons only and keeps its width.
		var nodeButtons = _nodeHeader.AddChild<PanelToolbar>();
		nodeButtons.AddClass( "grow-shrink" );

		_filter = nodeButtons.AddFilter( "Filter nodes..", _ => Read() );

		_overridesOnly = nodeButtons.AddToggle( "filter_alt",
			"Only show nodes that override their data for this variation", false, on =>
			{
				_showOverridesOnly = on;
				Read();
			} );

		var well = nodes.AddChild<Panel>( "well" );

		_nodes = well.AddChild( new VariationNodeList( window, this ) );

		//Sits over the list rather than in it, so an empty list is a hint in the middle of the well rather than a row at the top of it.
		_empty = well.AddChild<UILabel>( "empty" );
		_empty.Tokenize = false;

		_splitter.AddCell( nodes, 0.6f );

		if ( ProjectCookie.Get<string>( SplitterCookie, null ) is { Length: > 0 } state )
			_splitter.RestoreState( state );

		Read();
	}

	/// <summary>Where the seam was left, for the next time the dock is opened.</summary>
	public void SaveSplitter() => ProjectCookie.Set( SplitterCookie, _splitter.SaveState() );

	/// <summary>Says which node the inspector is on, without reading the graph again: the rows are over the same nodes, only the mark on them has moved.</summary>
	public void UpdateSelection() => _nodes?.Invalidate();

	//Remembers a fold, so a tree of variations opens the way it was left.
	internal void Folded( string variationID, bool open )
	{
		if ( open ) Collapsed.Remove( variationID );
		else Collapsed.Add( variationID );

		ProjectCookie.Set( CollapsedCookie, Collapsed.Order().ToList() );
	}

	/// <summary>Reads the graph: the variations, the model pickers, and the node rows.</summary>
	public void Read()
	{
		var graph = _window.Graph;
		var variationID = _window.SelectedVariationID;
		var isDefault = variationID == Variation.DefaultVariationID;

		_treeHeader.Detail = $"{graph?.Variations?.All.Count ?? 0}";

		//The default variation is the one everything else inherits from: it cannot be renamed, cannot be deleted, and overrides nothing.
		_rename.Enabled = !isDefault;
		_delete.Enabled = !isDefault;
		_overridesOnly.SetClass( "hidden", isDefault );

		ReadPickers( graph );

		_tree.Read();
		_nodes.Read( graph, variationID, _filter?.Value?.Trim() ?? string.Empty,
			_showOverridesOnly && !isDefault );

		_nodeHeader.Detail = $"{_nodes.Count}";

		PanelText.Set( _empty, _nodes.Count > 0 ? string.Empty : EmptyHint );
		_empty.SetClass( "hidden", _nodes.Count > 0 );
	}

	//Why the node list is empty, which is three different things.
	private string EmptyHint
	{
		get
		{
			if ( !_nodes.HasAny ) return "No nodes with per variation data";

			return _showOverridesOnly
				? "This variation overrides nothing yet"
				: "No nodes match the filter";
		}
	}

	//The graph's target model and skeleton follow the selected variation, so these
	//edit whichever one is active - Esoterica's per variation skeleton picker.
	private void ReadPickers( Data graph )
	{
		_pickers.DeleteChildren( true );

		if ( graph is null ) return;

		var so = graph.GetSerialized();

		so.OnPropertyChanged += _ => _window.RebuildPreviewModel();
		so.OnPropertyFinishEdit += _ => _window.CommitChange( "Edit Variation Model" );

		AddPicker( so, nameof( Data.TargetModel ),
			$"The model the '{_window.SelectedVariationID}' variation animates" );

		AddPicker( so, nameof( Data.TargetSkeleton ),
			$"The skeleton the '{_window.SelectedVariationID}' variation's clips are compiled against" );
	}

	private void AddPicker( SerializedObject so, string property, string tooltip )
	{
		//The factory rather than BaseControl.CreateFor: that one answers with the "no editor found" fallback for anything it has no control of its own for,
		//and the factory is what then goes looking through the editor's type library and the generic controls. A model picker comes out of the second.
		if ( PanelControlFactory.CreateFor( so.GetProperty( property ) ) is not { } control ) return;

		control.Tooltip = tooltip;

		_pickers.AddChild( control );
	}
}

//A titled bar separating the sections, holding that section's own controls.
internal sealed class VariationHeader : Panel
{
	private readonly UILabel _title;
	private readonly UILabel _dot;
	private readonly UILabel _detail;

	public VariationHeader( string title )
	{
		AddClass( "variation-header" );

		_title = AddChild<UILabel>( "title" );
		_title.Tokenize = false;
		_title.Text = title;

		_dot = AddChild<UILabel>( "dot" );
		_dot.Tokenize = false;
		_dot.Text = "·";

		_detail = AddChild<UILabel>( "detail" );
		_detail.Tokenize = false;
	}

	/// <summary>The count after the title, dimmed - how many of whatever this names.</summary>
	public string Detail
	{
		set
		{
			PanelText.Set( _detail, value );

			var empty = string.IsNullOrEmpty( value );

			_dot.SetClass( "hidden", empty );
			_detail.SetClass( "hidden", empty );
		}
	}
}

//The variations, as the tree they inherit down.
internal sealed class VariationTree : TreePanel<string>
{
	private readonly Window _window;
	private readonly VariationEditorPanel _panel;

	private int _revision;

	public VariationTree( Window window, VariationEditorPanel panel )
	{
		_window = window;
		_panel = panel;

		AddClass( "variation-tree" );

		SelectionMode = TreeSelectionMode.Single;
	}

	protected override int Revision => _revision;

	protected override IEnumerable<string> Roots =>
		_window.Graph?.Variations is null ? [] : [Variation.DefaultVariationID];

	protected override IEnumerable<string> GetChildren( string variationID ) =>
		_window.VariationHierarchy?.GetChildVariations( variationID ) ?? [];

	//A variation: its name, the model it animates after it, much quieter, and how much it overrides as the count.
	protected override TreeItemInfo GetInfo( string variationID )
	{
		var overrides = _window.CountVariationOverrides( variationID );
		var model = _window.VariationHierarchy?.GetVariation( variationID )?.TargetModel?.Name;

		return new TreeItemInfo( variationID,
			variationID == Variation.DefaultVariationID ? "layers" : "subdirectory_arrow_right",
			GetChildren( variationID ).Any() )
		{
			Suffix = string.IsNullOrEmpty( model ) ? null : System.IO.Path.GetFileName( model ),
			Count = overrides > 0 ? overrides : null,
			Tooltip = $"{variationID}\nModel: {( string.IsNullOrEmpty( model ) ? "no model set" : model )}" +
				$"\nOverrides: {overrides}" +
				"\nClick to edit this variation, right-click for more options.",
		};
	}

	//A double-click renames a variation - all but the default one.
	protected override bool OnRowActivated( string variationID )
	{
		if ( variationID == Variation.DefaultVariationID ) return false;

		_window.PromptRenameVariation( variationID );
		return true;
	}

	/// <summary>The graph changed; the tree is over different variations now.</summary>
	public void Read()
	{
		_revision++;

		Bump();

		foreach ( var folded in _panel.Collapsed ) Close( folded );

		//Last, so selecting does not unfold a branch somebody folded away.
		Select( [_window.SelectedVariationID], skipEvents: true );
	}

	protected override void OnSelectionChanged( IReadOnlyList<string> selected )
	{
		if ( selected.FirstOrDefault() is not { } variationID ) return;

		_window.SetSelectedVariation( variationID );
	}

	protected override void OnExpandedChanged( string variationID, bool expanded ) =>
		_panel.Folded( variationID, expanded );

	protected override void OnContextMenu( string variationID )
	{
		var menu = new global::Editor.Menu();

		_window.BuildVariationContextMenu( menu, variationID );

		menu.OpenAtCursor();
	}
}

//The nodes that can hold data of their own, and whether this variation gives them any.
internal sealed class VariationNodeList : PooledRowList<VariationNodeRow>
{
	private readonly Window _window;
	private readonly VariationEditorPanel _panel;

	private readonly List<(VariationDataNode Node, string Path)> _rows = new();

	private string _variationID;
	private bool _isDefault;

	public VariationNodeList( Window window, VariationEditorPanel panel )
	{
		_window = window;
		_panel = panel;

		AddClass( "variation-nodes" );
	}

	protected override float RowHeight => VariationEditor.PillHeight;

	protected override int CellCount => _rows.Count;

	/// <summary>How many rows are listed, for the header's count.</summary>
	public int Count => _rows.Count;

	/// <summary>Whether the graph has any such node at all, filter or no filter.</summary>
	public bool HasAny { get; private set; }

	protected override VariationNodeRow CreateCell() => new( _window );

	protected override void BindCell( VariationNodeRow cell, int index )
	{
		var (node, path) = _rows[index];

		cell.Set( node, path, _variationID, _isDefault );
	}

	/// <summary>Reads the graph's nodes, filtered and sorted the way they are shown.</summary>
	public void Read( Data graph, string variationID, string filter, bool overridesOnly )
	{
		_variationID = variationID;
		_isDefault = variationID == Variation.DefaultVariationID;

		_rows.Clear();
		HasAny = false;

		if ( graph is null )
		{
			Invalidate();
			return;
		}

		var nodes = graph.AllNodes
			.OfType<VariationDataNode>()
			.Select( node => (Node: node, Path: _window.GetNodePath( node )) )
			.ToList();

		HasAny = nodes.Count > 0;

		//Sorted by something that cannot change while you edit: a clip node's display name is its
		//animation, so sorting by that would reshuffle the list every time you picked a new one.
		_rows.AddRange( nodes
			.Where( entry => !overridesOnly || entry.Node.HasVariationOverride( variationID ) )
			.Where( entry => string.IsNullOrEmpty( filter )
				|| entry.Node.DisplayInfo.Name.Contains( filter, StringComparison.OrdinalIgnoreCase )
				|| ( entry.Path?.Contains( filter, StringComparison.OrdinalIgnoreCase ) ?? false ) )
			.OrderBy( entry => entry.Path, StringComparer.OrdinalIgnoreCase )
			.ThenBy( entry => Rinten.DisplayInfo.ForType( entry.Node.GetType() ).Name, StringComparer.OrdinalIgnoreCase )
			.ThenBy( entry => entry.Node.Identifier, StringComparer.OrdinalIgnoreCase ) );

		Invalidate();
	}
}

//One node with per variation data: whether this variation overrides it or inherits it, and a button to switch between the two.
internal sealed class VariationNodeRow : PanelPooledRow
{
	private readonly Window _window;

	private readonly UILabel _icon;
	private readonly UILabel _name;
	private readonly UILabel _path;
	private readonly UILabel _sourceIcon;
	private readonly UILabel _source;
	private readonly PanelToolbarButton _toggle;

	private VariationDataNode _node;
	private string _variationID;
	private bool _hasOverride;

	public VariationNodeRow( Window window )
	{
		_window = window;

		AddClass( "variation-node-row" );

		_icon = AddChild<UILabel>( "icon material-icon" );

		_name = AddChild<UILabel>( "name" );
		_name.Tokenize = false;

		_path = AddChild<UILabel>( "path grow-shrink" );
		_path.Tokenize = false;

		//Where inherited data comes from is worth spelling out; an override is obvious from the row itself.
		_sourceIcon = AddChild<UILabel>( "source-icon material-icon" );

		_source = AddChild<UILabel>( "source" );
		_source.Tokenize = false;

		var buttons = AddChild<PanelToolbar>();

		_toggle = buttons.AddIcon( "edit_note", string.Empty, Toggle );
	}

	/// <summary>What this row is over, and how it stands against the variation.</summary>
	public void Set( VariationDataNode node, string path, string variationID, bool isDefault )
	{
		_node = node;
		_variationID = variationID;
		_hasOverride = node.HasVariationOverride( variationID );

		var source = isDefault ? null : _window.GetVariationDataSource( node, variationID );
		var inherited = !isDefault && !_hasOverride;

		var unset = node.GetResolvedVariationData( _window.Graph?.Variations, variationID )?.GetVisualState()
			== VariationDataNode.VariationData.VisualState.HasUnsetData;

		SetClass( "inherited", inherited );
		SetClass( "unset", unset );
		SetClass( "current", _window.IsSelected( node ) );

		//The node's own colour, which is what tells a clip from an IK setup at a glance. It is per node rather
		//than per class, so it cannot come from a sheet - this is the one thing here still written from code.
		Style.BackgroundColor = node.PrimaryColor.WithAlpha( _window.IsSelected( node ) ? 0.25f : 0.1f );

		PanelText.Set( _icon, node.DisplayInfo.Icon ?? Rinten.DisplayInfo.ForType( node.GetType() ).Icon ?? "account_tree" );
		PanelText.Set( _name, node.DisplayInfo.Name );
		PanelText.Set( _path, path );

		PanelText.Set( _sourceIcon, inherited ? "link" : string.Empty );
		PanelText.Set( _source, inherited ? source : string.Empty );

		_toggle.SetClass( "hidden", isDefault );
		_toggle.SetClass( "adds", !_hasOverride );
		_toggle.SetIcon( _hasOverride ? "undo" : "edit_note" );
		_toggle.Tooltip = _hasOverride
			? $"Remove the override so '{variationID}' inherits this data again"
			: $"Give '{variationID}' its own copy of this data";

		Tooltip = isDefault
			? $"{path} / {node.DisplayInfo.Name}\nClick to edit its data in the inspector, double-click to jump to the node"
			: _hasOverride
				? $"{path} / {node.DisplayInfo.Name}\nOverridden for '{variationID}' - click to edit its data in the inspector"
				: $"{path} / {node.DisplayInfo.Name}\nInherits its data from '{source}'";
	}

	private void Toggle()
	{
		if ( _node is null ) return;

		_window.SetVariationOverride( _node, _variationID, !_hasOverride );
	}

	protected override void OnClick( MousePanelEvent e )
	{
		base.OnClick( e );

		if ( _node is null ) return;

		_window.SelectVariationNode( _node );
	}

	protected override void OnDoubleClick( MousePanelEvent e )
	{
		base.OnDoubleClick( e );

		if ( _node is null ) return;

		_window.NavigateToNode( _node.Identifier );

		e.StopPropagation();
	}

	public override void Write( System.Text.StringBuilder into )
	{
		into.Append( _path.Text ).Append( " / " ).Append( _name.Text );
	}
}
