namespace Editor.NodeEditor;

//The only widget the node editor has. Docks are arranged by the editor's own layouts, which know about widgets and nothing else; everything inside this one is panels.
/// <summary>The dock slot a graph is shown in.</summary>
public class GraphDock : PanelDockHost
{
	public EditorGraphView View { get; }

	public GraphDock( EditorGraphView view, string name, string icon = null ) : base( null )
	{
		View = view;

		Name = name;
		WindowTitle = name;

		if ( !string.IsNullOrEmpty( icon ) ) SetWindowIcon( icon );

		view.Host = this;

		SetContent( view );
	}

	/// <summary>Puts a new name and icon on the tab.</summary>
	public void Retitle( string title, string icon )
	{
		WindowTitle = title;

		if ( !string.IsNullOrEmpty( icon ) ) SetWindowIcon( icon );
	}
}
