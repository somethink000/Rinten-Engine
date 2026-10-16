namespace Editor;

/// <summary>Tree view; subclasses supply data and behavior.</summary>
[StyleSheet( "TreePanel.cs.scss" )]
public abstract class TreePanel<T> : EditorPanel, IEditorDropTarget where T : class
{
	/// <summary>How left clicks update the selection.</summary>
	public TreeSelectionMode SelectionMode { get; set; } = TreeSelectionMode.Single;

	/// <summary>Currently selected nodes.</summary>
	public IReadOnlyList<T> Selected { get; private set; } = [];

	/// <summary>Nodes whose children are currently expanded.</summary>
	public IEnumerable<T> OpenItems => openItems;

	//Model revision; a change triggers a row rebuild.
	protected abstract int Revision { get; }

	//Top-level nodes shown as tree roots.
	protected abstract IEnumerable<T> Roots { get; }

	//Returns direct children of the node.
	protected abstract IEnumerable<T> GetChildren( T node );

	//Returns label, icon, and child presence for the node.
	protected abstract TreeItemInfo GetInfo( T node );

	//Whether inline rename is allowed for this tree.
	protected virtual bool SupportsRename => false;

	/// <summary>Whether rows can be dragged to reorder or reparent within the tree.</summary>
	public virtual bool SupportsDragDrop => false;

	/// <summary>Whether a tree drag is active.</summary>
	public bool IsDragging => SupportsDragDrop && EditorDrag.Active;

	/// <summary>Active drag event while hovering this tree.</summary>
	public Widget.DragEvent CurrentDrag => currentDrag;

	//Currently visible row panels.
	protected IEnumerable<TreeRowPanel<T>> Rows => body.Children.OfType<TreeRowPanel<T>>();

	//Scrollable container for tree rows.
	private readonly Panel body;

	//Expanded nodes; drives which children are visible.
	private readonly HashSet<T> openItems = [];

	//Backing list for Selected.
	private readonly List<T> selected = [];

	//Flattened visible rows from the last structure pass.
	private readonly List<(T Node, int Depth, TreeItemInfo Info, ulong Lines, bool Last)> desiredRows = [];

	//Last seen Revision from the model.
	private int lastRevision = -1;

	//Incremented when open state or structure should refresh rows.
	private int structureVersion;

	//structureVersion applied in the last row sync.
	private int lastStructureVersion = -1;

	//Whether root nodes were auto-expanded on first sync.
	private bool seededOpen;

	//Node currently being renamed inline.
	private T renameNode;

	//Selection batch passed to OnRenamed.
	private T[] renameSelection;

	//Row panel hosting the active rename field.
	private Panel renameHost;

	//Inline text field for rename.
	private TextField renameField;

	//Range-selection anchor for shift-click.
	private T selectionAnchor;

	//Row showing the current drop hint.
	private TreeRowPanel<T> dropRow;

	//Drop edge shown on dropRow.
	private TreeDropEdge dropEdge;

	//Drag event while hovering this tree.
	private Widget.DragEvent currentDrag;

	/// <summary>Creates the tree body panel.</summary>
	public TreePanel()
	{
		AddClass( "tree-panel" );
		body = AddChild<Panel>( "body" );
	}

	/// <summary>Syncs structure from the model and rebuilds visible rows.</summary>
	public override void Tick()
	{
		base.Tick();
		SyncStructure();
		SyncRowsIfNeeded();

		if ( renameNode is not null && renameHost is null )
			ShowRename();
	}

	/// <summary>Marks row layout stale so the next tick rebuilds rows.</summary>
	public void Bump() => structureVersion++;

	/// <summary>Sets the current selection, optionally without firing selection callbacks.</summary>
	public void Select( IEnumerable<T> items, bool skipEvents = false ) => ApplySelection( items, skipEvents: skipEvents );

	/// <summary>Returns whether the node is in the current selection.</summary>
	public bool IsSelected( T node ) => IndexOfSelected( node ) >= 0;

	//Where the node sits in the selection, by reference, or -1. A loop, not Any with a lambda: the lambda captured the node, so every row
	//asking every frame was a closure and a delegate - the largest allocator in the editor's idle frame, three hundred samples of its trace.
	private int IndexOfSelected( T node )
	{
		for ( var i = 0; i < selected.Count; i++ )
		{
			if ( ReferenceEquals( selected[i], node ) ) return i;
		}

		return -1;
	}

	/// <summary>Starts inline rename for the node, or the current selection.</summary>
	public virtual void BeginRename( T node = default )
	{
		if ( !SupportsRename ) return;

		node ??= Selected.LastOrDefault();
		if ( node is null ) return;

		OpenTo( node );
		renameNode = node;
		renameSelection = GetRenameSelection( node );
		ShowRename();
	}

	/// <summary>Returns whether the node's children are shown.</summary>
	public bool IsOpen( T node ) => openItems.Contains( node );

	/// <summary>Clears expanded nodes and re-seeds open state on the next sync.</summary>
	public void ClearOpenState()
	{
		openItems.Clear();
		seededOpen = false;
		Bump();
	}

	/// <summary>Expands the node, optionally including all descendants.</summary>
	public void Open( T node, bool recursive = false )
	{
		if ( !openItems.Add( node ) ) return;

		OnExpandedChanged( node, true );
		Bump();

		if ( !recursive ) return;

		OpenDescendants( node );
	}

	/// <summary>Collapses the node, optionally including all descendants.</summary>
	public void Close( T node, bool recursive = false )
	{
		if ( !openItems.Remove( node ) ) return;

		OnExpandedChanged( node, false );
		Bump();

		if ( !recursive ) return;

		foreach ( var child in GetChildren( node ) )
			Close( child, true );
	}

	/// <summary>Opens everything - the bar's unfold button.</summary>
	public void OpenAll()
	{
		foreach ( var root in Roots.ToList() )
		{
			Open( root );
			OpenDescendants( root );
		}

		Bump();
	}

	/// <summary>Shuts everything under the roots, which stay open so the tree is not one row - the bar's fold button.</summary>
	public void CloseAll()
	{
		foreach ( var root in Roots.ToList() )
		{
			foreach ( var child in GetChildren( root ).ToList() )
				CloseDeep( child );
		}
	}

	//Shuts a node and everything under it, open or not.
	void CloseDeep( T node )
	{
		foreach ( var child in GetChildren( node ).ToList() )
			CloseDeep( child );

		Close( node );
	}

	/// <summary>Toggles whether the node's children are shown.</summary>
	public void ToggleOpen( T node, bool recursive = false )
	{
		if ( IsOpen( node ) ) Close( node, recursive );
		else Open( node, recursive );
	}

	/// <summary>Expands ancestors so the node becomes visible.</summary>
	public void OpenTo( T node, bool recursive = false )
	{
		if ( !HasRoots() || node is null ) return;

		//The ancestors only: the path ends at the node, and opening that too unfolded every object in the hierarchy the moment it was picked.
		foreach ( var ancestor in EnumeratePathTo( node ) )
		{
			if ( ReferenceEquals( ancestor, node ) ) break;

			if ( openItems.Add( ancestor ) )
				OnExpandedChanged( ancestor, true );
		}

		if ( recursive )
			OpenDescendants( node );

		Bump();
	}

	/// <summary>How far the rows are scrolled.</summary>
	public float ScrollY => body.ScrollOffset.y;

	/// <summary>Whether the pointer is over the rows.</summary>
	public bool PointerOverRows => body.HasHovered;

	/// <summary>The node whose row is at the top of the view, and how far into that row the view starts.</summary>
	public (T Node, float Into)? TopRow()
	{
		var scroll = body.ScrollOffset.y;

		foreach ( var row in Rows )
		{
			var height = row.Box.Rect.Height;
			if ( height <= 0 ) return null;

			var top = RowTop( row );
			if ( top + height > scroll ) return (row.Node, scroll - top);
		}

		return null;
	}

	/// <summary>Scrolls a node's row that far above the top, as far as there is room. False until the row is laid out.</summary>
	public bool ScrollToRow( Func<T, bool> match, float into )
	{
		var row = Rows.FirstOrDefault( x => match( x.Node ) );
		if ( row is null || row.Box.Rect.Height <= 0 ) return false;

		body.ScrollOffset = new Vector2( 0, ( RowTop( row ) + into ).Clamp( 0, Math.Max( 0f, body.ScrollSize.y ) ) );
		return true;
	}

	//Where a row starts in the scrolled content
	float RowTop( Panel row ) => row.Box.Rect.Top - body.Box.Rect.Top + body.ScrollOffset.y;

	/// <summary>Scrolls the body so the node's row is in view.</summary>
	public void ScrollTo( T node )
	{
		var row = body.Children.OfType<TreeRowPanel<T>>().FirstOrDefault( x => ReferenceEquals( x.Node, node ) );
		row?.ScrollIntoView();
	}

	//Handles a left click on a row.
	internal void RowClicked( TreeRowPanel<T> row, MousePanelEvent e )
	{
		TryCancelRename( e.Target as Panel ?? row );

		if ( SelectionMode == TreeSelectionMode.Single )
			ApplySelection( [row.Node] );
		else
			HandleMultiSelectClick( row.Node, e );

		OnRowClicked( row.Node );
	}

	//A row clicked, after the selection took it - for a tree that does something on every click, even of what was selected already.
	protected internal virtual void OnRowClicked( T node ) { }

	//Handles a right click on a row.
	internal void RowContextMenu( TreeRowPanel<T> row )
	{
		TryCancelRename( row );

		if ( !IsSelected( row.Node ) )
			ApplySelection( [row.Node], skipEvents: true );

		OnContextMenu( row.Node );
	}

	//Creates a row panel for the given node.
	protected virtual TreeRowPanel<T> CreateRow( T node, TreeItemInfo info, int depth )
		=> new TreeRowPanel<T>( this, node, info, depth, 0 );

	//A press on a row, before the row does what a press does - for a tree that takes some presses itself: the eye-dropper picking an object.
	protected internal virtual bool OnRowPressed( T node, MousePanelEvent e ) => false;

	//A row double-clicked - an asset opened, the camera brought to an object.
	protected internal virtual bool OnRowActivated( T node ) => false;

	//The row's info as it is now, for a tree whose rows change look without its shape changing - an object switched off, a prefab edited. Asked every frame
	//for every row, so it is handed what the row has, to keep what does not move. Null, the default, is a tree whose info only changes with its Revision.
	protected internal virtual TreeItemInfo? LiveInfo( T node, TreeItemInfo current ) => null;

	//Whether the rows inside a folder stand on a plate. A tree that is all folders - the asset browser - says no, and every row lies on the ground.
	protected internal virtual bool PlatesInnerRows => true;

	//One of a row's buttons pressed, where the button carries nothing to run itself - see TreeRowAction.Pressed.
	protected internal virtual void OnRowAction( T node, int index ) { }

	//Called when the user opens a row context menu.
	protected virtual void OnContextMenu( T node ) { }

	//Called when the user right-clicks the empty tree body.
	protected virtual void OnBodyContextMenu() { }

	//Called when a node is expanded or collapsed.
	protected virtual void OnExpandedChanged( T node, bool expanded ) { }

	//Seeds which roots start expanded on first sync.
	protected virtual void SeedOpenRoots( IEnumerable<T> roots )
	{
		foreach ( var root in roots )
			openItems.Add( root );
	}

	//Called before the selection changes.
	protected virtual void OnBeforeSelectionChanged( IReadOnlyList<T> previous ) { }

	//Called after the selection changes.
	protected virtual void OnSelectionChanged( IReadOnlyList<T> selected ) { }

	//Called when inline rename is committed.
	protected virtual void OnRenamed( T node, string text, T[] selection ) { }

	//Returns whether the node can start a tree drag.
	protected virtual bool CanDrag( T node ) => true;

	//Creates drag data for the row being dragged.
	protected virtual DragData CreateDragData( TreeRowPanel<T> row ) => null;

	//Handles hover or drop on a row.
	protected virtual DropAction OnDragDrop( T target, DragData data, TreeDropEdge edge, bool isDrop ) => DropAction.Ignore;

	//Handles hover or drop on the empty tree body.
	protected virtual DropAction OnBodyDragDrop( DragData data, bool isDrop ) => DropAction.Ignore;

	//Returns the row and edge to highlight for a body drop.
	protected virtual bool TryGetBodyDropHint( DragData data, out TreeRowPanel<T> row, out TreeDropEdge edge )
	{
		row = null;
		edge = TreeDropEdge.None;
		return false;
	}

	//Starts inline rename for a node and a multi-selection batch.
	protected void BeginRenameSelection( T node, T[] selection )
	{
		if ( !SupportsRename || node is null ) return;

		OpenTo( node );
		renameNode = node;
		renameSelection = selection;
		ShowRename();
	}

	//Clears multi-selection on body click, or opens the body context menu.
	protected override void OnMouseDown( MousePanelEvent e )
	{
		TryCancelRename( e.Target as Panel ?? this );

		base.OnMouseDown( e );

		if ( e.MouseButton == MouseButtons.Right && e.Target == body )
		{
			OnBodyContextMenu();
			e.StopPropagation();
			return;
		}

		if ( SelectionMode != TreeSelectionMode.Multi ) return;
		if ( e.MouseButton != MouseButtons.Left || e.Target != body ) return;
		if ( Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Ctrl ) ) return;
		if ( Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Shift ) ) return;

		OnBeforeSelectionChanged( Selected );
		ApplySelection( Array.Empty<T>(), skipBefore: true );
	}

	/// <summary>Returns whether keyboard focus is inside this tree.</summary>
	public bool ContainsPanelFocus()
	{
		var focus = InputFocus.Current;
		if ( !focus.IsValid() ) return false;

		for ( var panel = focus; panel is not null; panel = panel.Parent )
		{
			if ( ReferenceEquals( panel, this ) )
				return true;
		}

		return false;
	}

	/// <summary>Starts rename when F2 is pressed while the tree has focus.</summary>
	public override void OnButtonTyped( ButtonEvent e )
	{
		if ( e.Pressed && SupportsRename && renameNode is null && e.Button == "f2" )
		{
			e.StopPropagation = true;
			BeginRename();
			return;
		}

		base.OnButtonTyped( e );
	}

	//Starts a drag from the given row.
	internal void StartRowDrag( TreeRowPanel<T> row )
	{
		if ( !SupportsDragDrop || !CanDrag( row.Node ) ) return;

		var data = CreateDragData( row );
		if ( data is null ) return;

		EditorDrag.Begin( row, data, null );
	}

	//Handles drag hover on a row.
	internal DropAction RowDragHover( TreeRowPanel<T> row, Widget.DragEvent ev )
	{
		currentDrag = ev;

		var edge = GetDropEdge( row, ev.LocalPosition );
		SetDropTarget( row, edge );

		return OnDragDrop( row.Node, ev.Data, edge, false );
	}

	//Commits a drop on a row.
	internal void RowDragDrop( TreeRowPanel<T> row, Widget.DragEvent ev )
	{
		var edge = row == dropRow && dropEdge != TreeDropEdge.None
			? dropEdge
			: GetDropEdge( row, ev.LocalPosition );

		ev.Action = OnDragDrop( row.Node, ev.Data, edge, true );
		ClearDropTarget();
		currentDrag = null;
		Bump();
	}

	//Clears row drop hints when the drag leaves a row.
	internal void RowDragLeave()
	{
		if ( dropRow is null ) return;

		ClearDropTarget();
		currentDrag = null;
	}

	bool IEditorDropTarget.AcceptEditorDrops => SupportsDragDrop;

	DropAction IEditorDropTarget.OnEditorDragHover( Widget.DragEvent ev )
	{
		currentDrag = ev;

		var action = OnBodyDragDrop( ev.Data, false );
		if ( action == DropAction.Ignore )
		{
			ClearDropTarget();
			return DropAction.Ignore;
		}

		if ( TryGetBodyDropHint( ev.Data, out var row, out var edge ) )
			SetDropTarget( row, edge );
		else
			ClearDropTarget();

		return action;
	}

	void IEditorDropTarget.OnEditorDragDrop( Widget.DragEvent ev )
	{
		ev.Action = OnBodyDragDrop( ev.Data, true );
		ClearDropTarget();
		currentDrag = null;
		Bump();
	}

	void IEditorDropTarget.OnEditorDragLeave()
	{
		ClearDropTarget();
		currentDrag = null;
	}

	//Updates the highlighted row for a drop target.
	private void SetDropTarget( TreeRowPanel<T> row, TreeDropEdge edge )
	{
		if ( dropRow == row && dropEdge == edge ) return;

		dropRow?.SetDropEdge( TreeDropEdge.None );
		dropRow = row;
		dropEdge = edge;
		row?.SetDropEdge( edge );
	}

	//Clears the active row drop hint.
	private void ClearDropTarget()
	{
		dropRow?.SetDropEdge( TreeDropEdge.None );
		dropRow = null;
		dropEdge = TreeDropEdge.None;
	}

	//Maps a pointer position on a row to a drop edge.
	private static TreeDropEdge GetDropEdge( Panel row, Vector2 local )
	{
		var height = row.Box.Rect.Height;
		if ( height <= 0 )
			height = Theme.RowHeight;

		if ( local.y < 5 )
			return TreeDropEdge.Top;

		if ( local.y > height - 5 )
			return TreeDropEdge.Bottom;

		return TreeDropEdge.Inside;
	}

	//Polls model revision and seeds initial open roots.
	private void SyncStructure()
	{
		if ( !HasRoots() )
		{
			if ( desiredRows.Count > 0 || body.HasChildren )
				ClearRows();

			lastRevision = 0;
			seededOpen = false;
			return;
		}

		var revision = Revision;
		if ( !seededOpen )
		{
			SeedOpenRoots( Roots );
			seededOpen = true;
			Bump();
		}

		if ( revision != lastRevision )
		{
			lastRevision = revision;
			Bump();
		}
	}

	//Applies shift-, ctrl-, or plain-click multi-selection rules.
	private void HandleMultiSelectClick( T node, MousePanelEvent e )
	{
		var shift = Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Shift );
		var ctrl = Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Ctrl );

		if ( shift )
		{
			SelectRange( node );
			return;
		}

		var next = selected.ToList();

		if ( ctrl )
		{
			if ( next.Any( x => ReferenceEquals( x, node ) ) )
			{
				OnBeforeSelectionChanged( Selected );
				next.RemoveAll( x => ReferenceEquals( x, node ) );
				ApplySelection( next, skipBefore: true );
			}
			else
			{
				OnBeforeSelectionChanged( Selected );
				next.Add( node );
				selectionAnchor = node;
				ApplySelection( next, skipBefore: true );
			}

			return;
		}

		OnBeforeSelectionChanged( Selected );
		selectionAnchor = node;
		ApplySelection( [node], skipBefore: true );
	}

	//Selects all visible rows between the anchor and the clicked node.
	private void SelectRange( T node )
	{
		var flat = body.Children.OfType<TreeRowPanel<T>>().Select( x => x.Node ).ToList();
		var indexB = flat.FindIndex( x => ReferenceEquals( x, node ) );
		if ( indexB < 0 ) return;

		var indexA = selectionAnchor is not null ? flat.FindIndex( x => ReferenceEquals( x, selectionAnchor ) ) : indexB;
		if ( indexA < 0 ) indexA = indexB;

		OnBeforeSelectionChanged( Selected );

		var next = new List<T>();
		var step = indexA <= indexB ? 1 : -1;
		for ( var i = indexA; ; i += step )
		{
			next.Add( flat[i] );
			if ( i == indexB ) break;
		}

		ApplySelection( next, skipBefore: true );
	}

	//Writes the selection list and raises selection callbacks.
	private void ApplySelection( IEnumerable<T> items, bool skipBefore = false, bool skipEvents = false )
	{
		var next = items as IReadOnlyList<T> ?? items.ToList();

		//Both or neither. The two are a pair - the hierarchy opens an undo scope in the first and closes it in the second - and a selection mirrored
		//in from the session used to raise the first alone: the scope stayed open, and every Ctrl+Z after it was refused as "undo scope open".
		if ( !skipBefore && !skipEvents )
			OnBeforeSelectionChanged( Selected );

		selected.Clear();
		selected.AddRange( next );
		Selected = selected;

		if ( !skipEvents )
			OnSelectionChanged( Selected );

		Bump();
	}

	//Returns all nodes that should be renamed together with the node.
	private T[] GetRenameSelection( T node )
	{
		if ( SelectionMode == TreeSelectionMode.Multi && IndexOfSelected( node ) >= 0 )
			return selected.ToArray();

		return [node];
	}

	//Recursively opens every descendant of the node.
	private void OpenDescendants( T node )
	{
		foreach ( var child in GetChildren( node ) )
		{
			if ( openItems.Add( child ) )
				OnExpandedChanged( child, true );

			OpenDescendants( child );
		}
	}

	//Yields ancestors from root down to the node's parent.
	private IEnumerable<T> EnumeratePathTo( T node )
	{
		if ( !HasRoots() || node is null )
			yield break;

		var path = new List<T>();
		if ( !TryBuildPath( node, path ) )
			yield break;

		foreach ( var item in path )
			yield return item;
	}

	//Builds the path from a root to the target node.
	private bool TryBuildPath( T target, List<T> path )
	{
		foreach ( var root in Roots )
		{
			if ( TryBuildPathFrom( root, target, path ) )
				return true;
		}

		return false;
	}

	//Depth-first search that fills path when target is found under current.
	private bool TryBuildPathFrom( T current, T target, List<T> path )
	{
		path.Add( current );

		if ( ReferenceEquals( current, target ) )
			return true;

		foreach ( var child in GetChildren( current ) )
		{
			if ( TryBuildPathFrom( child, target, path ) )
				return true;
		}

		path.RemoveAt( path.Count - 1 );
		return false;
	}

	//Returns whether the model currently exposes any roots. Asked by the sync every frame, and Roots is usually an
	//iterator - an object on the heap for every ask. A tree that can answer without walking its roots says so here.
	protected virtual bool HasRoots() => Roots.Any();

	//Shows the inline rename field on the node's row.
	private void ShowRename()
	{
		SyncRowsIfNeeded();

		var row = body.Children.OfType<TreeRowPanel<T>>().FirstOrDefault( x => ReferenceEquals( x.Node, renameNode ) );
		if ( row is null ) return;
		if ( renameHost == row && renameField.IsValid() ) return;

		renameHost = row;
		row.SetClass( "renaming", true );
		var field = row.AddChild<TreeRenameField>( "rename" );
		renameField = field;
		field.Value = GetInfo( renameNode ).Label ?? string.Empty;
		field.Submitted = CompleteRename;
		field.ValueChanged = x => field.Value = x;
		field.OnCancel = CancelRename;
		field.Focus();
	}

	//Cancels rename when the click is outside the rename row.
	internal void TryCancelRename( Panel target )
	{
		if ( renameNode is null || renameHost is null || target is null ) return;

		for ( var panel = target; panel is not null; panel = panel.Parent )
		{
			if ( ReferenceEquals( panel, renameHost ) )
				return;
		}

		CancelRename();
	}

	//Abandons the active inline rename without saving.
	private void CancelRename()
	{
		if ( renameNode is null ) return;

		renameField?.Delete( immediate: true );
		ClearRename();
		Bump();
	}

	//Commits or abandons the active inline rename.
	private void CompleteRename( string text )
	{
		if ( renameNode is null || !SupportsRename ) return;

		OnRenamed( renameNode, text, renameSelection ?? [renameNode] );
		renameField?.Delete( immediate: true );
		ClearRename();
		Bump();
	}

	//Clears inline rename state and removes the rename field.
	private void ClearRename()
	{
		renameHost?.SetClass( "renaming", false );
		renameNode = default;
		renameSelection = null;
		renameHost = null;
		renameField = null;
	}

	//Removes all row panels and pending rename UI.
	private void ClearRows()
	{
		body.DeleteChildren( true );
		desiredRows.Clear();
		ClearRename();
		Bump();
	}

	//Rebuilds row panels when structure or open state changed.
	private void SyncRowsIfNeeded()
	{
		if ( !HasRoots() ) return;
		if ( lastStructureVersion == structureVersion ) return;

		lastStructureVersion = structureVersion;

		desiredRows.Clear();

		var roots = Roots.ToList();
		for ( var i = 0; i < roots.Count; i++ )
			CollectVisibleRows( roots[i], 0, 0, i == roots.Count - 1 );

		var existing = body.Children.OfType<TreeRowPanel<T>>().ToList();
		var byNode = new Dictionary<T, TreeRowPanel<T>>( existing.Count );
		foreach ( var row in existing )
			byNode[row.Node] = row;

		var next = new List<TreeRowPanel<T>>( desiredRows.Count );

		for ( var i = 0; i < desiredRows.Count; i++ )
		{
			var (node, depth, info, lines, last) = desiredRows[i];

			if ( !byNode.TryGetValue( node, out var row ) )
			{
				row = CreateRow( node, info, depth );
				byNode[node] = row;
			}

			row.SetGuides( lines, last );
			row.UpdateLayout( depth, i, info );
			next.Add( row );
		}

		var keep = next.Count > 0 ? new HashSet<TreeRowPanel<T>>( next ) : null;
		foreach ( var row in existing )
		{
			if ( keep is not null && keep.Contains( row ) ) continue;
			row.Delete( immediate: true );
		}

		foreach ( var row in next )
		{
			if ( row.Parent != body )
				body.AddChild( row );
		}

		for ( var i = 0; i < next.Count; i++ )
		{
			if ( next[i].SiblingIndex != i )
				body.SetChildIndex( next[i], i );
		}

		if ( renameNode is not null )
			ShowRename();
	}

	//Appends visible rows for the node and its open descendants.
	private void CollectVisibleRows( T node, int depth, ulong lines, bool last )
	{
		var info = GetInfo( node );
		desiredRows.Add( (node, depth, info, lines, last) );

		if ( !info.HasChildren || !IsOpen( node ) ) return;

		//Under this node, its own level's line runs on while it has siblings after it - so its children draw it past them.
		var below = depth > 0 && !last ? lines | (1UL << (depth - 1)) : lines;

		var children = GetChildren( node ).ToList();
		for ( var i = 0; i < children.Count; i++ )
			CollectVisibleRows( children[i], depth + 1, below, i == children.Count - 1 );
	}
}
