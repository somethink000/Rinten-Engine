namespace Editor.Inspectors;

[StyleSheet( "ListControl.cs.scss" )]
[CustomEditor( typeof( List<> ) )]
[CustomEditor( typeof( Array ) )]
[CustomEditor( typeof( NetList<> ) )]
public class ListControl : BaseControl
{
	Panel body;
	Panel addRow;
	ToolbarButton addButton;
	SerializedCollection collection;
	int buildHash;
	bool preventRebuild;
	int? dragIndex;
	ListEntryPanel dragHover;

	public override bool SupportsMultiEdit => true;

	public ListControl()
	{
		ShowBackground = false;
		MultipleRows = true;

		body = AddChild<Panel>( "body" );

		addRow = AddChild<Panel>( "add-row" );

		addButton = addRow.AddChild<ToolbarButton>();
		addButton.AddClass( "list-action" );
		addButton.Icon = "add";
		addButton.ToolTip = "Add entry";
		addButton.OnPressed = () => Edit( () => collection.Add( null ) );
	}

	protected override void OnReadOnlyChanged()
	{
		base.OnReadOnlyChanged();
		if ( addRow.IsValid() )
			addRow.SetClass( "hidden", IsReadOnly );
	}

	public override void Rebuild()
	{
		if ( preventRebuild ) return;
		if ( Property is null ) return;

		if ( !Property.TryGetAsObject( out var so ) || so is not SerializedCollection sc )
			return;

		collection = sc;
		collection.OnEntryAdded = Rebuild;
		collection.OnEntryRemoved = Rebuild;

		var hash = HashCode.Combine( collection.ToList().Count );
		if ( hash == buildHash ) return;
		buildHash = hash;

		ClearDrag();
		body.DeleteChildren();

		var index = 0;
		foreach ( var entry in collection )
		{
			body.AddChild( new ListEntryPanel( this, entry, index ) );
			index++;
		}

		addRow.SetClass( "hidden", IsReadOnly );
	}

	public override void Tick()
	{
		base.Tick();

		if ( dragIndex is null ) return;

		if ( (Application.MouseButtons & MouseButtons.Left) == 0 )
		{
			FinishDrag();
			return;
		}

		UpdateDragHover();
	}

	void StartDrag( int index ) => dragIndex = index;

	void FinishDrag()
	{
		if ( dragIndex is not null && dragHover is not null )
		{
			var delta = dragIndex.Value - dragHover.Index;
			if ( delta != 0 )
				MoveEntry( dragHover.Index, delta );
		}

		ClearDrag();
	}

	void ClearDrag()
	{
		dragIndex = null;
		dragHover?.ClearDropIndicator();
		dragHover = null;
	}

	void UpdateDragHover()
	{
		if ( dragIndex is null ) return;

		var pos = Application.CursorPosition;
		ListEntryPanel found = null;

		foreach ( var child in body.Children )
		{
			if ( child is not ListEntryPanel entry ) continue;
			if ( !entry.GetScreenStyleRect().IsInside( pos ) ) continue;

			found = entry;
			break;
		}

		if ( found == dragHover ) return;

		dragHover?.ClearDropIndicator();
		dragHover = found;

		if ( found is null ) return;

		var delta = dragIndex.Value - found.Index;
		found.SetDropIndicator( delta > 0, delta < 0 );
	}

	//Wraps add/remove/move as one property edit so it gets undo, dirty state and prefab overrides
	void Edit( Action change )
	{
		if ( collection is null || Property is null ) return;

		Property.Parent?.NoteStartEdit( Property );

		try
		{
			change();
		}
		finally
		{
			Property.Parent?.NoteFinishEdit( Property );
		}
	}

	void RemoveAt( int index ) => Edit( () => collection.RemoveAt( index ) );

	void MoveEntry( int index, int delta )
	{
		if ( collection is null ) return;

		var movingIndex = index + delta;
		if ( movingIndex < 0 || movingIndex >= collection.Count() ) return;

		preventRebuild = true;
		Edit( () => Move( collection, index, delta ) );
		preventRebuild = false;
		buildHash = 0;
		Rebuild();
	}

	static void Move( SerializedCollection collection, int index, int delta )
	{
		var list = new List<object>();
		var movingIndex = index + delta;

		foreach ( var item in collection )
			list.Add( item.GetValue<object>() );

		var value = list[movingIndex];
		list.RemoveAt( movingIndex );
		list.Insert( index, value );

		while ( collection.Count() > 0 )
			collection.RemoveAt( 0 );

		foreach ( var item in list )
			collection.Add( item );
	}

	sealed class ListEntryPanel : Panel
	{
		public int Index { get; }

		Panel dropAbove;
		Panel dropBelow;

		public ListEntryPanel( ListControl parent, SerializedProperty entry, int index )
		{
			Index = index;

			AddClass( "list-entry" );

			dropAbove = AddChild<Panel>( "drop-above" );
			dropAbove.AddClass( "list-drop-line" );
			dropAbove.AddClass( "hidden" );

			dropBelow = AddChild<Panel>( "drop-below" );
			dropBelow.AddClass( "list-drop-line" );
			dropBelow.AddClass( "bottom" );
			dropBelow.AddClass( "hidden" );

			if ( !parent.IsReadOnly )
			{
				var handle = AddChild<DragHandlePanel>();
				handle.Tooltip = $"Element {index}";
				handle.DragStarted = () => parent.StartDrag( index );
			}

			var editor = PanelControlFactory.CreateFor( entry );
			if ( editor is not null )
			{
				AddChild( editor );
			}

			if ( parent.IsReadOnly ) return;

			var remove = AddChild<ToolbarButton>();
			remove.AddClass( "list-action" );
			remove.Icon = "remove";
			remove.ToolTip = "Remove entry";
			remove.OnPressed = () => parent.RemoveAt( index );
		}

		public void SetDropIndicator( bool above, bool below )
		{
			dropAbove.SetClass( "hidden", !above );
			dropBelow.SetClass( "hidden", !below );
		}

		public void ClearDropIndicator()
		{
			dropAbove.SetClass( "hidden", true );
			dropBelow.SetClass( "hidden", true );
		}
	}

	sealed class DragHandlePanel : Panel
	{
		public Action DragStarted;

		public DragHandlePanel()
		{
			AddClass( "list-drag-handle" );

			var icon = AddChild<IconPanel>();
			icon.Text = "drag_handle";
		}

		protected override void OnMouseDown( MousePanelEvent e )
		{
			if ( e.MouseButton != MouseButtons.Left ) return;

			DragStarted?.Invoke();
			e.StopPropagation();
		}
	}
}
