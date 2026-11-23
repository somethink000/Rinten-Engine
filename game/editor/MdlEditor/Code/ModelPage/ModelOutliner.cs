using Rinten.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using UILabel = Rinten.UI.Label;

namespace Editor.MdlEditor;

//The model's parts tree dock; handles document actions and leaves drawing to ModelOutlinerPanel
sealed class ModelOutliner : PanelDockHost
{
	readonly IModelDocHost _host;
	readonly ModelOutlinerPanel _panel;

	//Every part, in the order the tree shows them - folded or not.
	readonly List<ModelDocItem> _items = new();
	readonly List<ModelDocItem> _selection = new();
	readonly HashSet<ModelDocKind> _collapsed = new();

	//Folders somebody made and then shut, by kind and name.
	readonly HashSet<string> _collapsedFolders = new( StringComparer.OrdinalIgnoreCase );

	Asset _asset;
	ModelDefinition _definition;

	public ModelDocItem Selected { get; private set; }

	/// <summary>Everything picked. The last one clicked is also in Selected.</summary>
	public IReadOnlyList<ModelDocItem> SelectedItems => _selection;

	public Action<ModelDocItem> ItemSelected { get; set; }

	/// <summary>Dock list, or a compact column without title, save, or its own scroller.</summary>
	public ModelOutliner( IModelDocHost host, bool compact = false ) : base( null )
	{
		_host = host;

		Name = "Model";
		WindowTitle = "Model";
		SetWindowIcon( "account_tree" );
		MinimumWidth = compact ? 0 : 140;

		_panel = new ModelOutlinerPanel( this, compact );

		SetContent( _panel );
	}

	//Puts the keyboard on the list, so Delete and Ctrl+C reach it.
	internal void FocusList() => _panel?.Focus();

	//What the tree is being built from, and how it is folded.
	internal ModelDocContext Context()
		=> new() { Host = _host, IsCollapsed = IsCollapsed, IsFolderCollapsed = IsFolderCollapsed };

	//Every part, folded or not: the tree folds them itself - see ModelPartsTree - so its arrows say what a shut list holds.
	ModelDocContext Unfolded()
		=> new() { Host = _host, IsCollapsed = static _ => false, IsFolderCollapsed = static ( _, _ ) => false };

	internal IModelDocHost Host => _host;

	internal ModelDefinition Definition => _definition;

	internal Asset Asset => _asset;

	//---- what the panel asks of the list ---------------------------------

	//A key was pressed while the list had focus.
	internal bool OnListKey( string button, bool ctrl, bool alt )
	{
		if ( button is "delete" or "backspace" )
		{
			if ( Selected is not null && CanDelete( Selected ) )
			{
				Delete( Selected );
				return true;
			}

			return false;
		}

		if ( !ctrl || alt ) return false;

		switch ( button )
		{
			case "c" when CanCopy( Selected ):
				Copy( Selected );
				return true;

			case "v" when CanPaste():
				Paste();
				return true;

			case "d" when CanCopy( Selected ):
				Duplicate( Selected );
				return true;
		}

		return false;
	}

	//A right-click that landed on the list rather than on a row. A row stops its own right-click from travelling, so nothing has to be
	//remembered between the two - the widget list kept a flag for that, because both it and the row were told about the same click.
	internal void OnListContextMenu()
	{
		if ( _definition is null ) return;

		OpenAddMenu();
	}

	//-- copy, paste, duplicate ------------------------------------------------ One set of these for
	//every kind that is a list entry, driven off the handler's own ListFor - see ModelDocHandler.ListFor.

	System.Collections.IList ListOf( ModelDocItem item )
		=> item is { IsFolder: false, Index: >= 0 } && _definition is not null
			? ModelDoc.Of( item.Kind )?.ListFor( Context(), item.Kind )
			: null;

	internal bool CanCopy( ModelDocItem item ) => ListOf( item ) is not null;

	internal void Copy( ModelDocItem item )
	{
		if ( ListOf( item ) is not { } list ) return;
		if ( ModelDocClipboard.Write( list, item.Index, item.Kind ) is not { } json ) return;

		EditorUtility.Clipboard.Copy( json );
	}

	//The kind on the clipboard, and the list it would go into.
	bool PasteTarget( out ModelDocKind kind, out System.Collections.IList list )
	{
		kind = default;
		list = null;

		if ( _definition is null ) return false;
		if ( ModelDocClipboard.KindOn( EditorUtility.Clipboard.Paste() ) is not { } copied ) return false;

		kind = copied;
		list = ModelDoc.Of( copied )?.ListFor( Context(), copied );

		return list is not null;
	}

	internal bool CanPaste() => PasteTarget( out _, out _ );

	internal void Paste()
	{
		if ( !PasteTarget( out var kind, out var list ) ) return;
		if ( ModelDocClipboard.Read( EditorUtility.Clipboard.Paste(), list ) is not { } value ) return;

		//After the selection when it is one of the same kind, so pasting a hull while a hull
		//is picked drops it next to that one rather than at the far end of a list of thirty.
		var at = Selected is { IsFolder: false, Index: >= 0 } && Selected.Kind == kind
			? Selected.Index + 1
			: list.Count;

		Insert( kind, list, at, value, "Paste" );
	}

	internal void Duplicate( ModelDocItem item )
	{
		if ( ListOf( item ) is not { } list ) return;
		if ( ModelDocClipboard.Clone( list, item.Index ) is not { } copy ) return;

		Insert( item.Kind, list, item.Index + 1, copy, "Duplicate" );
	}

	void Insert( ModelDocKind kind, System.Collections.IList list, int at, object value, string what )
	{
		_host.BeginDefinitionEdit( what );

		at = Math.Clamp( at, 0, list.Count );
		list.Insert( at, value );

		ModelDoc.Of( kind )?.OnInserted( Context(), kind, at );

		OnStructureChanged( kind, at );
		_host.EndDefinitionEdit( what );
	}

	internal void SetModel( Asset asset, ModelDefinition definition )
	{
		if ( !ReferenceEquals( _definition, definition ) )
			ClearSelection();

		_asset = asset;
		_definition = definition;
		Rebuild();
	}

	//No-op, kept for callers; the window autosaves so there is no save state to show
	internal void UpdateSaveState( bool dirty )
	{
	}

	internal bool IsSelected( ModelDocItem item )
		=> item is not null && _selection.Any( s => SameId( item, s ) );

	void ClearSelection()
	{
		_selection.Clear();
		Selected = null;
	}

	internal static bool SameId( ModelDocItem a, ModelDocItem b )
		=> a.Kind == b.Kind && a.Index == b.Index && a.ParentIndex == b.ParentIndex
			&& string.Equals( a.SubName, b.SubName, StringComparison.Ordinal );

	internal bool IsCollapsed( ModelDocKind kind ) => _collapsed.Contains( kind );

	internal bool IsFolderCollapsed( ModelDocKind kind, string folder )
		=> _collapsedFolders.Contains( $"{kind}/{folder}" );

	//Whether a list or a folder somebody made is shut.
	internal bool IsShut( ModelDocItem item )
		=> item.Kind == ModelDocKind.Folder
			? _collapsedFolders.Contains( Key( item ) )
			: _collapsed.Contains( item.Kind );

	//The tree opened or shut a list or a folder: remembered, so it stays so across rebuilds.
	internal void NoteShut( ModelDocItem item, bool shut )
	{
		if ( item.Kind == ModelDocKind.Folder )
		{
			if ( shut ) _collapsedFolders.Add( Key( item ) );
			else _collapsedFolders.Remove( Key( item ) );
			return;
		}

		if ( shut ) _collapsed.Add( item.Kind );
		else _collapsed.Remove( item.Kind );
	}

	//Which folder a row belongs to, as a key. The kind is in it because two lists may each
	//have an "arms": shutting the colliders' one has no business shutting the joints'.
	static string Key( ModelDocItem item ) => $"{OwnerOf( item )}/{ModelDocFolders.Named( item.SubName )}";

	//The list a folder row is sorting, which is the kind its children are.
	static ModelDocKind OwnerOf( ModelDocItem item ) => item.Owner;

	//Picks one part, as a click on its row would.
	internal void Select( ModelDocItem item )
	{
		_selection.Clear();
		if ( item is not null )
			_selection.Add( item );

		_panel.Tree.ShowSelection( _selection );
		Commit( item );
	}

	//The tree's selection moved: a click, a ctrl-click, a shift-click. Folders are not things to pick.
	internal void TookSelection( IEnumerable<ModelDocItem> items )
	{
		_selection.Clear();
		_selection.AddRange( items.Where( x => x is { IsFolder: false } ) );

		Commit( _selection.Count > 0 ? _selection[^1] : null );
	}

	//Says which one is now the lead.
	void Commit( ModelDocItem primary )
	{
		Selected = primary;

		ItemSelected?.Invoke( primary );
	}


	internal void Rebuild()
	{
		_panel.SetTitle( Path.GetFileNameWithoutExtension( _asset?.Path ?? "No model" ) );

		RebuildHeader();
		RebuildTree();
	}

	//The bar's fold button: every group and every folder somebody made shut, then the list read again - a group
	//that is shut is one whose handler stops after its heading, so the tree is the headings alone.
	internal void CollapseAll()
	{
		foreach ( var handler in ModelDoc.All )
		{
			_collapsed.Add( handler.Kind );

			foreach ( var kind in handler.AlsoHandles )
				_collapsed.Add( kind );
		}

		//Only the folders the list currently knows: one inside a shut group is not read again until the group opens,
		//and it starts open anyway.
		foreach ( var item in _items.Where( IsFolderItem ) )
			_collapsedFolders.Add( Key( item ) );

		RefreshTree();
	}

	//The bar's unfold button: no group or folder shut, then the list read again - every group reads its contents, and
	//the tree opens every node that has some.
	internal void ExpandAll()
	{
		_collapsed.Clear();
		_collapsedFolders.Clear();

		RefreshTree();
	}

	static bool IsFolderItem( ModelDocItem item ) => item.IsFolder && item.Kind == ModelDocKind.Folder;

	//The graph row: what pressing it will do, said before it is pressed.
	void RebuildHeader()
	{
		if ( _asset is null || _definition is null )
		{
			_panel.ShowGraph( false, null );
			return;
		}

		var said = _definition.Graph is not null
			? "Opens the graph this model carries."
			: "Starts a graph in this model. It is written into the model once there is something in it.";

		_panel.ShowGraph( true, said );
	}

	//The model's own graph, in the graph editor - the header's button. Kept in the definition rather than
	//named by it, so there is no file to pick: the button stands where the field that picked one used to.
	internal void OpenGraph()
	{
		if ( _asset is null || _definition is null ) return;

		global::Editor.MdlEditor.Window.OpenGraphOf( _asset );
	}

	internal void OnStructureChanged( ModelDocKind kind, int index, int parentIndex = -1 )
	{
		_host.BeginDefinitionEdit( "Add" );
		Changed();
		SelectKind( kind, index, parentIndex );
		_host.EndDefinitionEdit( "Add" );
	}

	void RebuildTree() => RefreshTree();

	//Read the parts again and hand them to the tree, keeping what is picked. The tree keeps a node for each part by what the part is, not by the item
	//the definition was read into - see ModelPartsTree - so an edit that renames a row or switches it off redraws that row and builds nothing else.
	internal void RefreshTree()
	{
		var keep = _selection.ToList();
		var lead = Selected;

		_items.Clear();
		_selection.Clear();
		Selected = null;

		if ( _definition is null )
		{
			_panel.Tree.SetItems( _items );
			_panel.SetNote( _asset is null
				? "Nothing is open."
				: "This model has no definition to edit." );
			return;
		}

		_panel.SetNote( null );
		_items.AddRange( ModelDoc.Collect( Unfolded() ) );

		foreach ( var was in keep )
		{
			if ( _items.FirstOrDefault( i => !i.IsFolder && SameId( i, was ) ) is { } match )
				_selection.Add( match );
		}

		Selected = lead is null ? null : _items.FirstOrDefault( i => !i.IsFolder && SameId( i, lead ) );

		_panel.Tree.SetItems( _items );
		_panel.Tree.ShowSelection( _selection );
	}

	//The add menu at the cursor - what a right click on the empty list gets.
	internal void OpenAddMenu()
	{
		if ( _definition is null )
			return;

		var menu = new Menu( this );
		FillAddGroups( menu, AllAdds() );
		if ( !menu.HasOptions && !menu.HasMenus )
			return;

		menu.OpenAtCursor();
	}

	//The list the "+ Add" button opens, while it is up.
	ModelAddPopup _addPopup;

	//Toggles the add popup that hangs under the button
	internal void ToggleAddPopup( Panel anchor )
	{
		if ( _addPopup.IsValid() )
		{
			_addPopup.Delete();
			_addPopup = null;
			return;
		}

		if ( _definition is null || anchor is not { IsValid: true } )
			return;

		var groups = GroupAdds( AllAdds() );
		if ( groups.Count == 0 )
			return;

		_addPopup = ModelAddPopup.Open( anchor, groups, RunAdd );
	}

	IEnumerable<ModelDocAdd> AllAdds() => ModelDoc.All.SelectMany( h => h.AddActions( Context() ) );

	//Does one add, as one step of undo.
	void RunAdd( ModelDocAdd action )
	{
		_host.BeginDefinitionEdit( action.Title );
		action.Run();
		_host.EndDefinitionEdit( action.Title );
	}

	internal void OpenRowMenu( ModelDocItem item )
	{
		if ( _definition is null || item is null )
			return;

		var menu = new Menu( this );

		if ( CanToggle( item ) )
		{
			var on = item.Enabled;
			menu.AddOption( on ? "Disable" : "Enable", on ? "visibility_off" : "visibility",
				() => SetEnabled( item, !on ) );
		}

		if ( CanCopy( item ) )
		{
			if ( menu.HasOptions )
				menu.AddSeparator();

			menu.AddOption( "Duplicate", "content_copy", () => Duplicate( item ) );
			menu.AddOption( "Copy", "file_copy", () => Copy( item ) );
		}

		if ( CanPaste() )
			menu.AddOption( "Paste", "content_paste", Paste );

		if ( CanDelete( item ) )
		{
			if ( menu.HasOptions )
				menu.AddSeparator();

			menu.AddOption( "Delete", "delete", () => Delete( item ) );
		}

		var adds = AddsFor( item ).ToList();
		if ( adds.Count > 0 )
		{
			if ( menu.HasOptions )
				menu.AddSeparator();

			FillAddGroups( menu, adds );
		}

		AddFolderOptions( menu, item );

		ModelDoc.Of( item.Kind )?.AddContextOptions( menu, Context(), item );

		if ( !menu.HasOptions && !menu.HasMenus )
			return;

		menu.OpenAtCursor();
	}

	//Putting a node in a folder, and what can be done to a folder itself. A menu rather than dragging: the list is rebuilt from the definition every
	//time anything changes, so there is nothing on screen stable enough to drop onto - and naming the folder is the whole of making one anyway.
	void AddFolderOptions( Menu menu, ModelDocItem item )
	{
		if ( _definition is null ) return;

		//The head of a list - "Colliders", "Animations" - which is where a folder with nothing in it yet has to be made from.
		if ( item.IsFolder && item.Kind != ModelDocKind.Folder
			&& ModelDocFolders.Sortable( _definition, item.Kind ) is not null )
		{
			if ( menu.HasOptions ) menu.AddSeparator();

			menu.AddOption( "New Folder", "create_new_folder", () =>
				AskName( this, "", "What do you want to call it?", name =>
				{
					_host.BeginDefinitionEdit( "New Folder" );
					ModelDocFolders.Declare( _definition, item.Kind, name );
					_host.EndDefinitionEdit( "New Folder" );
					_host.NotifyDefinitionChanged();
					RebuildTree();
				} ) );

			return;
		}

		if ( item.Kind == ModelDocKind.Folder )
		{
			if ( menu.HasOptions ) menu.AddSeparator();

			menu.AddOption( "Rename Folder", "drive_file_rename_outline", () =>
				AskName( this, ModelDocFolders.Named( item.SubName ), "Rename this folder to?", renamed =>
				{
					_host.BeginDefinitionEdit( "Rename Folder" );
					ModelDocFolders.Rename( _definition, item, renamed );
					_host.EndDefinitionEdit( "Rename Folder" );
					_host.NotifyDefinitionChanged();
					RebuildTree();
				} ) );

			menu.AddOption( "Ungroup", "folder_off", () =>
			{
				_host.BeginDefinitionEdit( "Ungroup" );
				ModelDocFolders.Ungroup( _definition, item );
				_host.EndDefinitionEdit( "Ungroup" );
				_host.NotifyDefinitionChanged();
				RebuildTree();
			} );

			return;
		}

		if ( ModelDocFolders.Node( _definition, item ) is not { } node ) return;

		if ( menu.HasOptions ) menu.AddSeparator();

		var folders = menu.AddMenu( "Folder", "folder" );
		var current = ModelDocFolders.FolderOf( node );

		foreach ( var name in ModelDocFolders.Names( _definition, item.Kind ) )
		{
			var into = name;
			var option = folders.AddOption( name, "folder", () => MoveTo( item, node, into ) );

			option.Checkable = true;
			option.Checked = name.Equals( current, StringComparison.OrdinalIgnoreCase );
		}

		if ( current.Length > 0 )
			folders.AddOption( "Take Out", "folder_off", () => MoveTo( item, node, "" ) );

		if ( folders.HasOptions ) folders.AddSeparator();

		folders.AddOption( "New Folder", "create_new_folder", () =>
			AskName( this, "", "What do you want to call it?", name => MoveTo( item, node, name ),
				"create_new_folder" ) );
	}

	void MoveTo( ModelDocItem item, object node, string folder )
	{
		_host.BeginDefinitionEdit( "Move to Folder" );
		ModelDocFolders.SetFolder( node, folder );
		ModelDocFolders.Declare( _definition, item.Kind, folder );
		_host.EndDefinitionEdit( "Move to Folder" );
		_host.NotifyDefinitionChanged();

		RebuildTree();
	}

	//Asks for a name, in the box the asset browser asks for one in.
	internal static void AskName( Widget owner, string value, string message, Action<string> onSubmit,
		string icon = "drive_file_rename_outline" )
	{
		EditorModal.Prompt( EditorOverlay.For( owner ), message, null, text =>
		{
			var name = ModelDocFolders.Named( text );

			if ( name.Length > 0 ) onSubmit?.Invoke( name );
		}, okay: "Confirm", initial: value, icon: icon );
	}

	//The file being dragged, whatever it is being dragged out of. An asset out of the browser, a file off the desktop, or a path as text
	//- the three shapes a drag carries a file in. The same three the inspector's own file field reads; see FilePathStringControlWidget.
	static string Dragged( DragData data )
	{
		if ( data is null ) return null;

		if ( data.Object is Asset asset )
			return asset.RelativePath ?? asset.AbsolutePath;

		if ( data.Object is Asset[] many && many.Length > 0 )
			return many[0].RelativePath ?? many[0].AbsolutePath;

		if ( data.HasFileOrFolder )
			return data.FileOrFolder;

		return string.IsNullOrWhiteSpace( data.Text ) ? null : data.Text;
	}

	//Whether what is being dragged is something this row would take.
	internal bool CanTake( ModelDocItem item, DragData e )
	{
		if ( _definition is null || item is null ) return false;
		if ( Dragged( e ) is not { Length: > 0 } path ) return false;

		return ModelDoc.Of( item.Kind )?.CanTake( Context(), item, path ) ?? false;
	}

	//Gives it to the row it was let go over.
	internal void Take( ModelDocItem item, DragData e )
	{
		if ( !CanTake( item, e ) ) return;
		if ( ModelDoc.Of( item.Kind ) is not { } handler ) return;

		_host.BeginDefinitionEdit( "Set File" );
		handler.Take( Context(), item, Dragged( e ) );
		_host.EndDefinitionEdit( "Set File" );

		//The inspector as well as the tree: what was dropped is the field being looked at, and
		//a row that renamed itself over a file field still saying nothing is the drop half done.
		_host.NotifyDefinitionChanged( true );

		RebuildTree();
	}

	IEnumerable<ModelDocAdd> AddsFor( ModelDocItem item )
	{
		var ctx = Context();
		return item.Kind switch
		{
			ModelDocKind.Mesh => ActionsOf( ModelDocKind.Mesh, ctx ),
			ModelDocKind.Hull or ModelDocKind.Hulls => ActionsOf( ModelDocKind.Hull, ctx ),
			ModelDocKind.Joint or ModelDocKind.Joints => ActionsOf( ModelDocKind.Joint, ctx ),
			ModelDocKind.Animation or ModelDocKind.Animations => ActionsOf( ModelDocKind.Animation, ctx ),
			ModelDocKind.LookChain or ModelDocKind.LookAt => ActionsOf( ModelDocKind.LookChain, ctx ),
			ModelDocKind.Attachment or ModelDocKind.Attachments or ModelDocKind.AttachmentInfluence
				=> ActionsOf( ModelDocKind.Attachment, ctx ),
			ModelDocKind.Constraint or ModelDocKind.Constraints or ModelDocKind.ConstraintDriver
				=> ActionsOf( ModelDocKind.Constraint, ctx ),
			ModelDocKind.WeightList or ModelDocKind.WeightLists or ModelDocKind.BoneWeight
				=> ActionsOf( ModelDocKind.WeightList, ctx ),
			ModelDocKind.IkChain or ModelDocKind.IkChains => ActionsOf( ModelDocKind.IkChain, ctx ),
			ModelDocKind.Morph or ModelDocKind.Morphs => ActionsOf( ModelDocKind.Morph, ctx ),
			ModelDocKind.Step or ModelDocKind.Steps or ModelDocKind.StepPart
				=> ActionsOf( ModelDocKind.Step, ctx ),
			ModelDocKind.MaterialGroup or ModelDocKind.DefaultMaterials or ModelDocKind.MaterialGroups
				or ModelDocKind.BodyGroup or ModelDocKind.BodyGroups or ModelDocKind.BodyGroupChoice
				=> ModelDoc.All.SelectMany( h => h.AddActions( ctx ) ).Where( a => a.Group == "Appearance" ),
			_ => []
		};
	}

	static IEnumerable<ModelDocAdd> ActionsOf( ModelDocKind kind, ModelDocContext ctx )
		=> ModelDoc.Of( kind )?.AddActions( ctx ) ?? [];

	void FillAddGroups( Menu menu, IEnumerable<ModelDocAdd> actions )
	{
		foreach ( var (group, icon, entries) in GroupAdds( actions ) )
		{
			var sub = menu.AddMenu( group, icon );
			foreach ( var action in entries )
			{
				var add = action;
				sub.AddOption( add.Title, add.Icon, () => RunAdd( add ) );
			}
		}
	}

	//The adds by group, in the order the groups were first named.
	internal static List<(string Group, string Icon, List<ModelDocAdd> Entries)> GroupAdds( IEnumerable<ModelDocAdd> actions )
	{
		var groups = new List<(string Group, string Icon, List<ModelDocAdd> Entries)>();
		var byGroup = new Dictionary<string, List<ModelDocAdd>>();
		foreach ( var action in actions )
		{
			if ( !byGroup.TryGetValue( action.Group, out var list ) )
			{
				list = new List<ModelDocAdd>();
				byGroup[action.Group] = list;
				groups.Add( (action.Group, action.GroupIcon, list) );
			}

			list.Add( action );
		}

		return groups;
	}

	internal static bool CanToggleRow( ModelDocItem item ) => CanToggle( item );

	//The eye on a row: switches the part on or off, as one undo step.
	internal void ToggleEnabled( ModelDocItem item ) => SetEnabled( item, !item.Enabled );

	internal static bool CanDelete( ModelDocItem item )
		=> item is not null && !item.IsFolder && (ModelDoc.Of( item.Kind )?.CanDelete( item ) ?? false);

	static bool CanToggle( ModelDocItem item )
		=> item is not null && (ModelDoc.Of( item.Kind )?.CanToggle( item ) ?? false);

	void SetEnabled( ModelDocItem item, bool enabled )
	{
		var name = enabled ? "Enable" : "Disable";
		_host.BeginDefinitionEdit( name );
		if ( ModelDoc.Of( item.Kind )?.TrySetEnabled( Context(), item, enabled ) != true )
		{
			_host.EndDefinitionEdit( name );
			return;
		}

		Changed();
		_host.EndDefinitionEdit( name );
	}

	internal void Delete( ModelDocItem item )
	{
		_host.BeginDefinitionEdit( "Delete" );
		if ( ModelDoc.Of( item.Kind )?.TryDelete( Context(), item ) != true )
		{
			_host.EndDefinitionEdit( "Delete" );
			return;
		}

		var next = NeighborAfterDelete( item );
		_selection.Clear();
		if ( next is not null )
			_selection.Add( next );

		Selected = next;
		Changed();
		ItemSelected?.Invoke( Selected );
		_host.EndDefinitionEdit( "Delete" );
	}

	ModelDocItem NeighborAfterDelete( ModelDocItem item )
	{
		var siblings = _items
			.Where( i => i is not null && !i.IsFolder && i.Kind == item.Kind && i.ParentIndex == item.ParentIndex )
			.ToList();

		var pos = siblings.FindIndex( i => SameId( i, item ) );
		if ( pos < 0 || siblings.Count <= 1 )
			return null;

		var next = pos + 1 < siblings.Count ? siblings[pos + 1] : siblings[pos - 1];
		var index = next.Index > item.Index ? next.Index - 1 : next.Index;
		return new ModelDocItem
		{
			Kind = next.Kind,
			Index = index,
			ParentIndex = next.ParentIndex,
			SubName = next.SubName
		};
	}

	void SelectKind( ModelDocKind kind, int index = -1, int parentIndex = -1 )
	{
		var match = _items.FirstOrDefault( i =>
			i.Kind == kind && i.Index == index && i.ParentIndex == parentIndex );
		if ( match is not null )
			Select( match );
	}

	void Changed()
	{
		_host.NotifyDefinitionChanged();
		RefreshTree();
	}
}

//The bar - add, unfold, fold, the model's graph - and the tree under it. The bar is the one the inspector's tab opens into, and the tree
//stands in a frame under it - design.scss's .tree-dock. In the inspector's card, where the card is the frame already, the bar lies flat on it.
[StyleSheet( "ModelOutliner.cs.scss" )]
sealed class ModelOutlinerPanel : EditorPanel
{
	readonly ModelOutliner _list;

	//The model's own animation graph: opens it, or starts one.
	readonly ToolbarButton _graph;

	//The parts, as the editor's one tree.
	internal ModelPartsTree Tree { get; }

	//What is said instead of parts, while there are none to show.
	readonly UILabel _note;

	public ModelOutlinerPanel( ModelOutliner list, bool compact )
	{
		_list = list;

		AddClass( "model-outliner tree-dock" );
		SetClass( "compact", compact );

		//Delete and Ctrl+C are the list's, and a panel is only offered keys once something in it holds the focus.
		AcceptsFocus = true;

		var bar = AddChild<Panel>( "dock-bar" );

		var add = bar.AddChild<ToolbarButton>();
		add.Icon = "add";
		add.ToolTip = "Add";
		//A square you can hit: its own minimum, so the first button in the bar cannot be squeezed by
		//the dock's edge - the add list used to open at whatever width was left.
		add.Style.MinWidth = 28.0f;
		add.Style.MinHeight = 28.0f;
		add.OnPressed = () => _list.ToggleAddPopup( add );

		var unfold = bar.AddChild<ToolbarButton>();
		unfold.Icon = "unfold_more";
		unfold.ToolTip = "Expand all";
		unfold.OnPressed = () => _list.ExpandAll();

		var fold = bar.AddChild<ToolbarButton>();
		fold.Icon = "unfold_less";
		fold.ToolTip = "Collapse all";
		fold.OnPressed = () => _list.CollapseAll();

		bar.AddChild<Panel>( "spacer" );

		_graph = bar.AddChild<ToolbarButton>();
		_graph.Icon = "account_tree";
		_graph.OnPressed = () => _list.OpenGraph();
		_graph.AddClass( "hidden" );

		//Not "well": design.scss's .well is a list's sunk plate, with margins - the tree came out grey on the inspector's card, in a box of its own.
		var well = AddChild<Panel>( compact ? "parts" : "parts tree-frame" );
		Tree = well.AddChild( new ModelPartsTree( list ) );

		_note = well.AddChild<UILabel>( "note hidden" );
	}

	//Shows the graph's button, or hides it where there is no model of ours to keep a graph in.
	internal void ShowGraph( bool shown, string tooltip )
	{
		_graph.SetClass( "hidden", !shown );
		_graph.ToolTip = $"Animation Graph\n{tooltip}";
	}

	//The model's name is the window's title; kept for the outliner to call.
	internal void SetTitle( string text )
	{
	}

	//Says something instead of the parts, or nothing.
	internal void SetNote( string text )
	{
		_note.Text = text ?? "";
		_note.SetClass( "hidden", string.IsNullOrEmpty( text ) );
	}

	//Asked of the list rather than answered here: what Delete and Ctrl+C mean is the document's business, and the panel only knows which key it was.
	/// <summary>The keys the list answers, while it has focus.</summary>
	public override void OnButtonTyped( ButtonEvent e )
	{
		if ( e.Pressed && _list.OnListKey( e.Button, e.HasCtrl, e.HasAlt ) )
		{
			e.StopPropagation = true;
			return;
		}

		base.OnButtonTyped( e );
	}

	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );

		Focus();

		if ( e.MouseButton != MouseButtons.Right ) return;

		_list.OnListContextMenu();
		e.StopPropagation();
	}
}

//Stable tree node for one model part, so rows keep state when items are re-read on edit
sealed class ModelPartNode
{
	public ModelDocItem Item { get; set; }

	public List<ModelPartNode> Children { get; } = new();

	public ModelPartNode( ModelDocItem item ) => Item = item;
}

//Draws the model's parts as a tree; ModelOutliner handles every action on them
sealed class ModelPartsTree : TreePanel<ModelPartNode>
{
	readonly ModelOutliner _list;

	//Nodes by what the part is, kept across reads - see ModelPartNode.
	readonly Dictionary<string, ModelPartNode> _nodes = new();
	readonly List<ModelPartNode> _roots = new();

	int _revision;

	//Set while the outliner's folding is being put back on the tree, so it is not told back what it said.
	bool _seeding;

	public ModelPartsTree( ModelOutliner list )
	{
		_list = list;

		AddClass( "model-parts" );
		SelectionMode = TreeSelectionMode.Multi;
	}

	protected override int Revision => _revision;

	protected override IEnumerable<ModelPartNode> Roots => _roots;

	protected override bool HasRoots() => _roots.Count > 0;

	protected override IEnumerable<ModelPartNode> GetChildren( ModelPartNode node ) => node.Children;

	//Which part a node is, whatever it is called now.
	static string Key( ModelDocItem item )
		=> $"{item.Kind}/{item.Owner}/{item.Index}/{item.ParentIndex}/{item.IsFolder}/{item.SubName}";

	//Takes the parts as the outliner read them, every one in order with how deep it sits, and makes them a tree - each hung off the last one a level up.
	internal void SetItems( IReadOnlyList<ModelDocItem> items )
	{
		var seen = new HashSet<string>();
		var stack = new List<ModelPartNode>();

		_roots.Clear();

		foreach ( var item in items )
		{
			var key = Key( item );
			seen.Add( key );

			if ( !_nodes.TryGetValue( key, out var node ) )
				_nodes[key] = node = new ModelPartNode( item );

			node.Item = item;
			node.Children.Clear();

			var depth = Math.Clamp( item.Depth, 0, stack.Count );
			stack.RemoveRange( depth, stack.Count - depth );

			if ( depth == 0 ) _roots.Add( node );
			else stack[depth - 1].Children.Add( node );

			stack.Add( node );
		}

		foreach ( var gone in _nodes.Keys.Where( k => !seen.Contains( k ) ).ToList() )
			_nodes.Remove( gone );

		//The outliner's folding is what is kept across rebuilds and what the model page asks; the tree is told it.
		_seeding = true;

		foreach ( var node in _nodes.Values )
		{
			if ( node.Children.Count == 0 ) continue;

			if ( _list.IsShut( node.Item ) ) Close( node );
			else Open( node );
		}

		_seeding = false;

		_revision++;
		Bump();
	}

	//Lights the rows of what the outliner has picked, without telling it back.
	internal void ShowSelection( IEnumerable<ModelDocItem> items )
	{
		var picked = new List<ModelPartNode>();

		foreach ( var item in items )
		{
			if ( _nodes.TryGetValue( Key( item ), out var node ) )
				picked.Add( node );
		}

		Select( picked, skipEvents: true );
	}

	protected override TreeItemInfo GetInfo( ModelPartNode node )
	{
		var item = node.Item;
		var handler = ModelDoc.Of( item.Kind );

		//A list of one kind - "Meshes", "Hulls" - is a heading over what it holds; a folder somebody made is a folder.
		var heading = item.IsFolder && item.Kind != ModelDocKind.Folder;
		var dim = !item.Enabled && !item.IsFolder;
		var tint = handler?.Tint is { } hue ? Color.Lerp( Theme.TextControl, hue, heading ? 1.0f : 0.58f ) : (Color?)null;

		return new TreeItemInfo( item.Title, handler?.IconFor( item ) ?? (item.IsFolder ? "folder" : "circle"), node.Children.Count > 0 )
		{
			IsHeading = heading,
			Count = heading ? node.Children.Count : null,
			IconColor = tint,
			TextColor = heading ? tint : null,
			Dim = dim,
			Tooltip = item.Kind.ToString(),
			Actions = !item.IsFolder && ModelOutliner.CanToggleRow( item )
				? item.Enabled ? TreeRowAction.EyeOn : TreeRowAction.EyeOff
				: null,
		};
	}

	protected override void SeedOpenRoots( IEnumerable<ModelPartNode> roots )
	{
		//Opened from the outliner's folding - see SetItems.
	}

	protected override void OnExpandedChanged( ModelPartNode node, bool expanded )
	{
		if ( !_seeding ) _list.NoteShut( node.Item, !expanded );
	}

	protected override void OnSelectionChanged( IReadOnlyList<ModelPartNode> selected )
		=> _list.TookSelection( selected.Select( x => x.Item ) );

	//A press puts the keyboard on the list; a press on a folder somebody made folds it, the way a heading folds.
	protected override bool OnRowPressed( ModelPartNode node, MousePanelEvent e )
	{
		_list.FocusList();

		if ( e.MouseButton != MouseButtons.Left || !node.Item.IsFolder || node.Children.Count == 0 ) return false;

		ToggleOpen( node );
		return true;
	}

	protected override void OnContextMenu( ModelPartNode node )
	{
		if ( !node.Item.IsFolder )
			_list.Select( node.Item );

		_list.OpenRowMenu( node.Item );
	}

	protected override void OnBodyContextMenu() => _list.OnListContextMenu();

	protected override void OnRowAction( ModelPartNode node, int index ) => _list.ToggleEnabled( node.Item );

	//A mesh made from the "+ Add" menu is an empty one with a name on it, and this is where the export goes into
	//it: the row it lands on is the row it belongs to. Rows are not dragged themselves - folders are a menu.
	public override bool SupportsDragDrop => true;

	protected override bool CanDrag( ModelPartNode node ) => false;

	protected override DropAction OnDragDrop( ModelPartNode target, DragData data, TreeDropEdge edge, bool isDrop )
	{
		if ( !_list.CanTake( target.Item, data ) ) return DropAction.Ignore;

		if ( isDrop )
			_list.Take( target.Item, data );

		return DropAction.Link;
	}
}
