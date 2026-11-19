using Rinten.UI.NodeGraph;

namespace Editor.NodeEditor;

/// <summary>Rinten UI's graph as the editor shows it: in a dock, with the editor's own menus, taking assets dropped from it.</summary>
[StyleSheet( "EditorGraphView.cs.scss" )]
public class EditorGraphView : GraphView, IEditorDropTarget
{
	/// <summary>The widget this view is shown in, where it has been put in one.</summary>
	public PanelDockHost Host { get; internal set; }

	/// <summary>Whether the view is one somebody can see: its widget is up, and nothing has hidden the panel itself.</summary>
	public override bool IsShown => Host is not null ? Host.Visible : IsVisible;

	//The graph's menus as the editor's popups: nested, with their fields as line edits, refilled in place as a filter is typed
	public override void OpenMenu( GraphMenu menu, Vector2 viewPosition )
	{
		var shown = new ContextMenu( null );

		Fill( shown, menu );

		shown.AboutToHide += menu.Hidden;
		menu.Showing();
		shown.OpenAt( Application.CursorPosition, this, false );
	}

	private static void Fill( Menu into, GraphMenu menu )
	{
		//Only a menu with a field in it changes while open; refilling a parent would rebuild the field and drop its focus
		if ( menu.Items.Any( x => x is GraphMenu.Field ) )
		{
			menu.Changed += () =>
			{
				into.RemoveMenus();
				into.RemoveOptions();
				Entries( into, menu, fields: false );

				if ( into.Visible )
				{
					into.AdjustSize();
					into.Update();
				}
			};
		}

		Entries( into, menu, fields: true );
	}

	private static void Entries( Menu into, GraphMenu menu, bool fields )
	{
		foreach ( var entry in menu.Items )
		{
			switch ( entry )
			{
				case GraphMenu.Field field when fields:
					into.AddLineEdit( field.Label, field.Value, field.Placeholder, field.AutoFocus, field.OnChange, field.OnSubmit );
					break;

				case GraphMenu.Separator:
					into.AddSeparator();
					break;

				case GraphMenu.Option option:
					into.AddOption( option.Text, option.Icon, option.Action ).Enabled = option.Enabled;
					break;

				case GraphMenu.Sub sub when sub.Menu.Title is null:
					sub.Menu.Showing();
					Entries( into, sub.Menu, fields );
					break;

				case GraphMenu.Sub sub:
					var nested = into.AddMenu( sub.Menu.Title, sub.Menu.Icon );
					nested.AboutToShow += sub.Menu.Showing;
					nested.AboutToShow += () => { nested.RemoveMenus(); nested.RemoveOptions(); Entries( nested, sub.Menu, fields: false ); };
					Fill( nested, sub.Menu );
					break;
			}
		}
	}

	//Assets dropped onto the canvas

	public bool AcceptEditorDrops => true;

	private NodeUI _nodePreview;

	protected virtual INodeType NodeTypeFromDragEvent( Widget.DragEvent ev ) => null;

	public virtual DropAction OnEditorDragHover( Widget.DragEvent ev )
	{
		var position = ToScene( MousePosition ).SnapToGrid( GridSize );

		if ( _nodePreview.IsValid() )
		{
			_nodePreview.Position = position;
			return DropAction.Link;
		}

		if ( NodeTypeFromDragEvent( ev ) is not { } type ) return DropAction.Ignore;

		var node = type.CreateNode( Graph );
		node.Position = position;

		_nodePreview = CreateNodeUI( node );

		return DropAction.Link;
	}

	public virtual void OnEditorDragLeave() => ClearNodePreview();

	public virtual void OnEditorDragDrop( Widget.DragEvent ev )
	{
		Focus();

		ClearNodePreview();

		if ( NodeTypeFromDragEvent( ev ) is not { } type ) return;

		CreateNewNode( type, ToScene( MousePosition ), null );
	}

	private void ClearNodePreview()
	{
		if ( !_nodePreview.IsValid() ) return;

		Graph.RemoveNode( _nodePreview.Node );
		_nodePreview.Destroy();
		_nodePreview = null;
	}
}
