using System.IO;
using Rinten.UI;

namespace Editor.UiEditor;

/// <summary>The window's furniture: the docks around the canvas. Nothing sits above them - new, open, reload, back, undo and redo are shortcuts, and the files behind the open document are on the right-click menu of the tree and the canvas.</summary>
public sealed partial class UiEditorWindow
{
	void BuildUI()
	{
		BuildWorkspace();

		//Each with the edge it belongs on: a registered dock is made at once and put on its area closed.
		DockManager.RegisterDock( new() { Title = "Hierarchy", Icon = "account_tree", Area = DockArea.Left, CreateAction = () => _hierarchy } );
		DockManager.RegisterDock( new() { Title = "Palette", Icon = "dashboard_customize", Area = DockArea.Left, CreateAction = () => _palette } );
		DockManager.RegisterDock( new() { Title = "Inspector", Icon = "tune", Area = DockArea.Right, CreateAction = () => _inspector } );
		DockManager.RegisterDock( new() { Title = "Data", Icon = "data_object", Area = DockArea.Right, CreateAction = () => _data } );
		DockManager.RegisterDock( new() { Title = "Animation", Icon = "animation", Area = DockArea.Bottom, CreateAction = () => _animation } );
	}

	//The tree, the inspector and the canvas, made together and bound to each other. Made again on hotload, which is
	//why it is one method: a rebuilt panel is a different widget, and the docks showing the old ones are re-seated.
	void BuildWorkspace()
	{
		var oldHierarchy = _hierarchy;
		var oldPalette = _palette;
		var oldInspector = _inspector;
		var oldData = _data;
		var oldAnimation = _animation;
		var oldCanvas = _canvas;

		//Where the selection was, before the canvas that holds it is remade.
		var selectionPath = SelectionPath;

		_hierarchy = new Hierarchy( this );
		_palette = new Palette( this );
		_inspector = new Inspector( this );
		_data = new Data( this );
		_animation = new AnimationDock( this );
		_canvas = new DesignCanvas( this );

		DockManager.SetCentralWidget( _canvas );

		Reseat( "Hierarchy", _hierarchy );
		Reseat( "Palette", _palette );
		Reseat( "Inspector", _inspector );
		Reseat( "Data", _data );
		Reseat( "Animation", _animation );

		if ( oldHierarchy.IsValid() && oldHierarchy != _hierarchy ) oldHierarchy.Destroy();
		if ( oldData.IsValid() && oldData != _data ) oldData.Destroy();
		if ( oldAnimation.IsValid() && oldAnimation != _animation ) oldAnimation.Destroy();
		if ( oldPalette.IsValid() && oldPalette != _palette ) oldPalette.Destroy();
		if ( oldInspector.IsValid() && oldInspector != _inspector ) oldInspector.Destroy();
		if ( oldCanvas.IsValid() && oldCanvas != _canvas ) oldCanvas.Destroy();

		//The type is the new assembly's now; the old one is code that no longer runs.
		_document?.Resolve();

		//A document whose code throws as it is built - a @foreach over a list nobody set - is the document's
		//trouble, not the window's: said, and the docks stand with nothing in them until it is put right.
		try
		{
			_canvas.SetDocument( _document );
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"UI editor: {_document?.Name} could not be built: {e.Message}" );
		}

		_data.SetDocument( _document );
		_animation.SetDocument( _document );
		OnRootChanged( selectionPath );
	}

	void Reseat( string title, Widget widget )
	{
		if ( DockManager.FindDockWidget( title ) is not { } dock ) return;

		dock.Widget = widget;
		widget.Parent = dock;
	}

	[EditorEvent.Hotload]
	public void OnHotload()
	{
		BuildWorkspace();
		ApplyLayout();
	}

	//Where the docks sit when nobody has moved them.
	protected override void BuildDefaultLayout()
	{
		var hierarchy = DockManager.OpenDock( "Hierarchy", DockArea.Left );
		var inspector = DockManager.OpenDock( "Inspector", DockArea.Right );

		//Under the inspector: what the document shows, beside how it looks.
		DockManager.OpenDock( "Data", DockArea.Bottom, inspector );

		//Along the bottom; a short strip until there are keyframes to show.
		DockManager.OpenDock( "Animation", DockArea.Bottom );

		//Under the tree, in the same column: what can be added, beside what is there.
		DockManager.OpenDock( "Palette", DockArea.Bottom, hierarchy );

		ApplyLayout();
	}

	//Narrow tree, a wider inspector, and the canvas gets the rest.
	internal void ApplyLayout()
	{
		Widths( "Hierarchy", 0.2f );
		Widths( "Inspector", 0.72f );

		DockManager.RaiseDock( "Hierarchy" );
		DockManager.RaiseDock( "Palette" );
		DockManager.RaiseDock( "Inspector" );
		DockManager.RaiseDock( "Data" );
	}


	void Widths( string title, float first )
	{
		if ( DockManager.FindDockWidget( title ) is { } dock )
			DockManager.SetSplitterProportions( dock, first, 1.0f - first );
	}

	//A new .razor and its .razor.scss , the least a panel is: a component with an empty root that
	//fills the screen. The compiler picks the files up and the canvas shows them when it has.
	[Shortcut( "editor.new", "CTRL+N", ShortcutType.Window )]
	//The new-panel card: a name, a folder, a base, a shape to start as, and a place in the scene.
	internal void NewFile()
	{
		var layer = ( _canvas?.Canvas is { } canvas ? EditorOverlay.For( canvas ) : null ) ?? EditorOverlay.ForActiveWindow();

		if ( layer is null )
		{
			Log.Warning( "UI editor: no window to ask in" );
			return;
		}

		//Beside the open document, else Code/UI.
		var root = Project.Current?.GetRootPath() ?? "";
		var folder = _document is not null ? Path.GetDirectoryName( _document.RazorPath ) : null;
		if ( string.IsNullOrEmpty( folder ) || string.Equals( folder, root, StringComparison.OrdinalIgnoreCase ) ) folder = Path.Combine( root, "Code", "UI" );
		if ( folder.StartsWith( root, StringComparison.OrdinalIgnoreCase ) ) folder = folder.Substring( root.Length ).TrimStart( '/', '\\' );

		layer.AddChild( new NewPanelModal( this, folder ) );
	}

	//Take the new-panel card away, if it is up; whether it was.
	internal bool CloseNewPanelCard()
	{
		var layer = ( _canvas?.Canvas is { } canvas ? EditorOverlay.For( canvas ) : null ) ?? EditorOverlay.ForActiveWindow();
		if ( layer?.Children.OfType<NewPanelModal>().FirstOrDefault() is not { } open ) return false;

		open.Delete();
		return true;
	}

	//Into the scene

	//A panel type waited for: once the compiler has it, a game object in the open scene gets it.
	(string name, RealTimeSince since)? _sceneWait;

	//Put a game object with a ScreenPanel and the named component in the open scene, once the razor has compiled.
	internal void PutInSceneWhenCompiled( string name )
	{
		_sceneWait = ( name, 0 );
	}

	[EditorEvent.Frame]
	void PutInScene()
	{
		if ( _sceneWait is not { } wait ) return;

		if ( wait.since > 90 )
		{
			_sceneWait = null;
			Log.Warning( $"UI editor: {wait.name} did not compile in time to be put in the scene" );
			return;
		}

		var type = TypeLibrary.GetType( wait.name );
		if ( type is null || !type.TargetType.IsAssignableTo( typeof( Rinten.Component ) ) ) return;

		_sceneWait = null;

		if ( SceneEditorSession.Active is not { } session || session.Scene is null )
		{
			Log.Warning( $"UI editor: no scene open to put {wait.name} in" );
			return;
		}

		using var scope = session.Scene.Push();

		using ( session.UndoScope( $"Add {wait.name}" ).WithGameObjectCreations().Push() )
		{
			var go = session.Scene.CreateObject();
			go.Name = wait.name;
			go.Components.Create<ScreenPanel>();
			go.Components.Create( type );

			EditorScene.Selection.Set( go );
		}

		Log.Info( $"UI editor: {wait.name} is in the scene, on a ScreenPanel" );
	}

	//Write the two files of a new panel, unless they are there already.
	internal static void CreateRazor( string path )
	{
		var name = Path.GetFileNameWithoutExtension( path );
		var element = name.ToLowerInvariant();

		if ( !File.Exists( path ) )
		{
			File.WriteAllText( path,
				"@using Rinten;\n" +
				"@using Rinten.UI;\n" +
				"@inherits PanelComponent\n" +
				"\n" +
				"<root>\n" +
				"</root>\n" +
				"\n" +
				"@code\n" +
				"{\n" +
				"}\n" );
		}

		var scss = path + ".scss";

		if ( !File.Exists( scss ) )
		{
			File.WriteAllText( scss,
				$"{element}\n" +
				"{\n" +
				"\tposition: absolute;\n" +
				"\tleft: 0;\n" +
				"\ttop: 0;\n" +
				"\tright: 0;\n" +
				"\tbottom: 0;\n" +
				"}\n" );
		}
	}

	//Build the panel again from its type - for when something outside the editor changed it.
	[Shortcut( "editor.reload", "F5", ShortcutType.Window )]
	void Reload()
	{
		var selectionPath = SelectionPath;

		_document?.Resolve();
		_canvas?.SetDocument( _document );

		OnRootChanged( selectionPath );
	}

	[Shortcut( "ui.back", "ALT+LEFT", ShortcutType.Window )]
	void GoBackShortcut() => GoBack();

	[Shortcut( "ui.fit", "CTRL+0", ShortcutType.Window )]
	void FitShortcut() => CanvasPanel?.ResetView();

	[Shortcut( "ui.actual", "CTRL+1", ShortcutType.Window )]
	void ActualShortcut() => CanvasPanel?.ActualSize();

	static void OpenInCode( string path )
	{
		if ( string.IsNullOrEmpty( path ) || !File.Exists( path ) ) return;

		CodeEditor.OpenFile( path );
	}

	//Reveal the open file in the OS file browser.
	void ShowInExplorer()
	{
		if ( _document?.RazorPath is not { Length: > 0 } path )
			return;

		EditorUtility.OpenFileFolder( path );
	}
}

//Where the editor is opened from: a right click on a .razor in the asset browser.
internal static class UiEditorMenu
{
	[Event( "asset.contextmenu", Priority = 40 )]
	public static void OnAssetContext( AssetContextMenu e )
	{
		if ( e.SelectedList.Count != 1 ) return;

		var path = e.SelectedList[0].FileInfo?.FullName;

		if ( path is null || !path.EndsWith( ".razor", StringComparison.OrdinalIgnoreCase ) ) return;

		e.Menu.AddOption( "Open in UI Editor", "web", () => UiEditorWindow.Open( path ) );
	}

	/// <summary>Open a <c>.razor</c> in the UI editor from the console: <c>ui_editor code/UI/Hud.razor</c>, relative to the project or absolute.</summary>
	[ConCmd( "ui_editor" )]
	public static void OpenFromConsole( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
		{
			Log.Info( "ui_editor <path to .razor>" );
			return;
		}

		if ( !System.IO.Path.IsPathRooted( path ) && Project.Current?.GetRootPath() is { } root )
		{
			var fromProject = System.IO.Path.Combine( root, path );
			if ( System.IO.File.Exists( fromProject ) ) path = fromProject;
		}

		if ( !System.IO.File.Exists( path ) )
		{
			Log.Warning( $"ui_editor: no file at {path}" );
			return;
		}

		UiEditorWindow.Open( path );
	}

	/// <summary>Make a new razor panel and open it: <c>ui_editor_new Code/UI/Hud.razor</c>, relative to the project or absolute.</summary>
	[ConCmd( "ui_editor_new" )]
	public static void NewFromConsole( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return;

		if ( !System.IO.Path.IsPathRooted( path ) && Project.Current?.GetRootPath() is { } root )
			path = System.IO.Path.Combine( root, path );

		if ( !path.EndsWith( ".razor", StringComparison.OrdinalIgnoreCase ) ) path += ".razor";

		System.IO.Directory.CreateDirectory( System.IO.Path.GetDirectoryName( path ) );
		UiEditorWindow.CreateRazor( path );
		UiEditorWindow.Open( path );
	}

	/// <summary>Select a panel in the newest UI editor by its path of child indices from the root: <c>ui_editor_select 0/2/1</c>. Empty selects the root.</summary>
	[ConCmd( "ui_editor_select" )]
	public static void SelectFromConsole( string path = "" )
	{
		if ( UiEditorWindow.Editors.FirstOrDefault( x => x.IsValid() ) is not { } window || window.Root is null )
		{
			Log.Warning( "ui_editor_select: no UI editor with a panel open" );
			return;
		}

		var indices = ( path ?? "" ).Split( '/', StringSplitOptions.RemoveEmptyEntries ).Select( int.Parse ).ToList();
		var found = PanelPath.Follow( window.Root, indices );

		window.Select( found );
		Log.Info( $"ui_editor_select: {found?.ElementName} .{found?.Classes}" );
	}

	/// <summary>Add a panel to the selection, or take it out, by its path of child indices: <c>ui_editor_select_also 0/3</c>.</summary>
	[ConCmd( "ui_editor_select_also" )]
	public static void SelectAlsoFromConsole( string path = "" )
	{
		if ( UiEditorWindow.Editors.FirstOrDefault( x => x.IsValid() ) is not { } window || window.Root is null )
		{
			Log.Warning( "ui_editor_select_also: no UI editor with a panel open" );
			return;
		}

		var indices = ( path ?? "" ).Split( '/', StringSplitOptions.RemoveEmptyEntries ).Select( int.Parse ).ToList();
		var found = PanelPath.Follow( window.Root, indices );

		if ( found is not null ) window.ToggleSelect( found );
		Log.Info( $"ui_editor_select_also: {window.Selected.Count()} selected, primary {window.Selection?.ElementName} .{window.Selection?.Classes}" );
	}

	/// <summary>Select what a marquee over the stage would, in stage pixels: <c>ui_editor_marquee x y w h</c>.</summary>
	[ConCmd( "ui_editor_marquee" )]
	public static void MarqueeFromConsole( float x, float y, float w, float h )
	{
		if ( UiEditorWindow.Editors.FirstOrDefault( x => x.IsValid() ) is not { } window || window.Root is null )
		{
			Log.Warning( "ui_editor_marquee: no UI editor with a panel open" );
			return;
		}

		window.CanvasPanel?.SelectInside( new Rect( x, y, w, h ) );
		Log.Info( $"ui_editor_marquee: {window.Selected.Count()} selected: {string.Join( ", ", window.Selected.Select( p => $"{p.ElementName}.{p.Classes}" ) )}" );
	}

	/// <summary>Scroll the selected panel's nearest scrolling ancestor - or itself - to a point: <c>ui_editor_scroll 0 400</c>.</summary>
	[ConCmd( "ui_editor_scroll" )]
	public static void ScrollFromConsole( float x, float y )
	{
		if ( WithSelection( "ui_editor_scroll", out var panel ) is null ) return;

		var scroller = panel;
		while ( scroller is not null && !scroller.HasScrollX && !scroller.HasScrollY ) scroller = scroller.Parent;

		if ( scroller is null )
		{
			Log.Warning( "ui_editor_scroll: nothing scrolls above the selection" );
			return;
		}

		scroller.ScrollOffset = new Vector2( x, y );
		Log.Info( $"ui_editor_scroll: {scroller.ElementName}.{scroller.Classes} to {scroller.ScrollOffset}" );
	}

	/// <summary>The clipboard, as the editor's copy and paste use it: <c>ui_editor_clip copy|cut|paste|copystyle|pastestyle|show</c>.</summary>
	[ConCmd( "ui_editor_clip" )]
	public static void ClipFromConsole( string what )
	{
		if ( UiEditorWindow.Editors.FirstOrDefault( x => x.IsValid() ) is not { } window ) return;

		switch ( what )
		{
			case "copy": window.CopySelection(); break;
			case "cut": window.CutSelection(); break;
			case "paste": window.PasteMarkup(); break;
			case "copystyle": window.CopyStyle(); break;
			case "pastestyle": window.PasteStyle(); break;
		}

		Log.Info( $"ui_editor_clip {what}: clipboard = {EditorUtility.Clipboard.Paste()?.Replace( "\n", " ⏎ " )}" );
	}

	/// <summary>Edit the selection's words as a double click on the canvas would: open the field, put these words in, submit: <c>ui_editor_text_inplace "new words"</c>.</summary>
	[ConCmd( "ui_editor_text_inplace" )]
	public static void TextInPlaceFromConsole( string words = null )
	{
		if ( WithSelection( "ui_editor_text_inplace", out var panel ) is not { } window || window.CanvasPanel is not { } canvas ) return;

		canvas.BeginTextEdit( panel );
		Log.Info( $"ui_editor_text_inplace: field open = {canvas.IsEditingText}" );

		//No words: the field is left open, to be seen.
		if ( string.IsNullOrEmpty( words ) ) return;

		canvas.SubmitTextEdit( words );
		Log.Info( $"ui_editor_text_inplace: field open after = {canvas.IsEditingText}" );
	}

	/// <summary>Print the undo stack's top entries: <c>ui_editor_history</c>.</summary>
	[ConCmd( "ui_editor_history" )]
	public static void HistoryFromConsole()
	{
		if ( UiEditorWindow.Editors.FirstOrDefault( x => x.IsValid() ) is not { } window ) return;

		Log.Info( $"ui_editor_history: {string.Join( " | ", window.HistoryTitles().Take( 6 ) )}" );
	}

	/// <summary>Anchor the selection as the inspector's block would: <c>ui_editor_anchor 0 0</c> (x, y each 0 near, 1 stretched, 2 far), <c>ui_editor_anchor fill</c>, <c>ui_editor_anchor flow</c>.</summary>
	[ConCmd( "ui_editor_anchor" )]
	public static void AnchorFromConsole( string x, string y = null )
	{
		if ( WithSelection( "ui_editor_anchor", out var panel ) is not { } window ) return;

		if ( x == "fill" ) AnchorBlock.Fill( window, panel );
		else if ( x == "flow" ) AnchorBlock.Flow( window, panel );
		else AnchorBlock.Anchor( window, panel, int.Parse( x ), int.Parse( y ?? "0" ) );

		Log.Info( $"ui_editor_anchor: {x} {y}" );
	}

	/// <summary>Draw a tag into the selection as a drag on the canvas would, in the selection's pixels: <c>ui_editor_draw div 10 10 200 100 [classes]</c>.</summary>
	[ConCmd( "ui_editor_draw" )]
	public static void DrawFromConsole( string tag, float x, float y, float w, float h, string classes = null )
	{
		if ( WithSelection( "ui_editor_draw", out var panel ) is not { } window ) return;

		var made = window.AddDrawn( panel, tag, classes, null, new Rect( x, y, w, h ) );
		if ( made is not null ) window.Select( made );

		Log.Info( $"ui_editor_draw: {( made is null ? "did not draw" : "drew" )} <{tag}> .{made?.Classes}" );
	}

	/// <summary>Group the selection in a div, or let it out: <c>ui_editor_group on|off</c>.</summary>
	[ConCmd( "ui_editor_group" )]
	public static void GroupFromConsole( string what )
	{
		if ( UiEditorWindow.Editors.FirstOrDefault( x => x.IsValid() ) is not { } window ) return;

		if ( what == "off" ) window.UnwrapSelection();
		else window.WrapSelection();

		Log.Info( $"ui_editor_group: {what}" );
	}

	/// <summary>Make a component of the selection's tag, or fold one back in: <c>ui_editor_component Name</c>, <c>ui_editor_component inline</c>.</summary>
	[ConCmd( "ui_editor_component" )]
	public static void ComponentFromConsole( string name )
	{
		if ( WithSelection( "ui_editor_component", out var panel ) is not { } window ) return;

		var done = name == "inline" ? window.InlineComponent( panel ) : window.ExtractComponent( panel, name );
		Log.Info( $"ui_editor_component: {name} {( done ? "done" : "not done" )}" );
	}

	/// <summary>Make a new panel from a template, as the card would: <c>ui_editor_new_panel Name Code/UI HUD PanelComponent [scene]</c>.</summary>
	[ConCmd( "ui_editor_new_panel" )]
	public static void NewPanelFromConsole( string name, string folder = "Code/UI", string template = "Empty", string baseType = "PanelComponent", string scene = "" )
	{
		var chosen = PanelTemplates.All.FirstOrDefault( x => string.Equals( x.Name, template, StringComparison.OrdinalIgnoreCase ) ) ?? PanelTemplates.All[0];
		var root = Project.Current?.GetRootPath() ?? "";
		var path = Path.Combine( root, folder, name + ".razor" );

		if ( File.Exists( path ) ) { Log.Warning( $"ui_editor_new_panel: {path} is there already" ); return; }

		var (razor, scss) = PanelTemplates.Fill( chosen, name, baseType );
		Directory.CreateDirectory( Path.GetDirectoryName( path ) );
		File.WriteAllText( path, razor );
		File.WriteAllText( path + ".scss", scss );

		var window = UiEditorWindow.Open( path );
		if ( scene == "scene" ) window?.PutInSceneWhenCompiled( name );

		Log.Info( $"ui_editor_new_panel: {path} from {chosen.Name}" );
	}

	/// <summary>Open the new-panel card, as File > New does.</summary>
	[ConCmd( "ui_editor_new_dialog" )]
	public static void NewDialogFromConsole()
	{
		if ( UiEditorWindow.Editors.FirstOrDefault( x => x.IsValid() ) is not { } window ) return;

		//Open already: this closes it.
		if ( window.CloseNewPanelCard() ) return;

		window.NewFile();
	}

	/// <summary>An item of the list the selection's @foreach walks: <c>ui_editor_item add|dup|remove|up|down</c>; <c>ui_editor_item set Field value</c> sets a field of it.</summary>
	[ConCmd( "ui_editor_item" )]
	public static void ItemFromConsole( string what, string field = null, string value = null )
	{
		if ( WithSelection( "ui_editor_item", out var panel ) is not { } window ) return;

		if ( window.LocateTag( panel ) is not { Control: "foreach" } tag || window.ListForForeach( tag ) is not { } member )
		{
			Log.Warning( "ui_editor_item: the selection is not inside a @foreach over a member" );
			return;
		}

		var index = window.ItemIndexOf( panel );
		var done = what switch
		{
			"add" => window.InsertItem( member, index + 1 ),
			"dup" => window.DuplicateItem( member, index ),
			"remove" => window.RemoveItem( member, index ),
			"up" => window.MoveItem( member, index, -1 ),
			"down" => window.MoveItem( member, index, 1 ),
			"set" => SetItemField( window, member, index, field, value ),
			_ => false,
		};

		Log.Info( $"ui_editor_item: {what} on {member}[{index}] {( done ? "done" : "not done" )}; now {window.ItemsOf( member )?.Count} items" );
	}

	/// <summary>An item of a list member by name, as the Data dock does it: <c>ui_editor_list Scenes add</c>, <c>ui_editor_list Scenes set 0 Title Showcase</c>, <c>ui_editor_list Scenes remove 0</c>.</summary>
	[ConCmd( "ui_editor_list" )]
	public static void ListFromConsole( string member, string what, int index = -1, string field = null, string value = null )
	{
		if ( Newest( "ui_editor_list" ) is not { } window ) return;

		var count = window.ItemsOf( member )?.Count ?? 0;
		if ( index < 0 ) index = Math.Max( 0, count - 1 );

		var done = what switch
		{
			"add" => window.InsertItem( member, count ),
			"dup" => window.DuplicateItem( member, index ),
			"remove" => window.RemoveItem( member, index ),
			"up" => window.MoveItem( member, index, -1 ),
			"down" => window.MoveItem( member, index, 1 ),
			"set" => SetItemField( window, member, index, field, value ),
			_ => false,
		};

		Log.Info( $"ui_editor_list: {what} on {member}[{index}] {( done ? "done" : "not done" )}; now {window.ItemsOf( member )?.Count} items" );
	}

	static bool SetItemField( UiEditorWindow window, string member, int index, string field, string value )
	{
		if ( window.ListMember( member ) is not { } list || TypeLibrary.GetType( list.item ) is not { } type ) return false;

		var target = DesignData.Members( type ).FirstOrDefault( x => string.Equals( x.name, field, StringComparison.OrdinalIgnoreCase ) );
		if ( target.name is null ) return false;

		var parsed = DataRow.ParseValue( value, target.type );
		window.SetItemField( member, index, item => target.set( item, parsed ) );
		return true;
	}

	/// <summary>Open or close a list's items in the Data dock: <c>ui_editor_items Items</c>.</summary>
	[ConCmd( "ui_editor_items" )]
	public static void ItemsFromConsole( string member )
	{
		if ( UiEditorWindow.Editors.FirstOrDefault( x => x.IsValid() ) is { } window ) window._data?.ToggleItems( member );
	}

	/// <summary>Print the selected panel's computed and inline position values, for seeing what a drag would start from.</summary>
	[ConCmd( "ui_editor_box" )]
	public static void BoxFromConsole()
	{
		if ( WithSelection( "ui_editor_box", out var panel ) is null ) return;

		var c = panel.ComputedStyle;
		var i = panel.Style;
		Log.Info( $"ui_editor_box: computed position={c?.Position} left={c?.Left} top={c?.Top} right={c?.Right} bottom={c?.Bottom} width={c?.Width} height={c?.Height}" );
		Log.Info( $"ui_editor_box: inline position={i.Position} left={i.Left} top={i.Top} right={i.Right} bottom={i.Bottom} width={i.Width} height={i.Height}" );
		Log.Info( $"ui_editor_box: rect={panel.Box.Rect} parent scroll={panel.Parent?.ScrollOffset}" );
	}

	static UiEditorWindow WithSelection( string command, out Panel panel )
	{
		panel = null;

		if ( UiEditorWindow.Editors.FirstOrDefault( x => x.IsValid() ) is not { } window || window.Selection is not { } selected )
		{
			Log.Warning( $"{command}: nothing selected in a UI editor" );
			return null;
		}

		panel = selected;
		return window;
	}

	/// <summary>Add a tag inside the selected panel's: <c>ui_editor_add label "some words" "class names"</c>.</summary>
	[ConCmd( "ui_editor_add" )]
	public static void AddFromConsole( string tag, string text = null, string classes = null )
	{
		if ( WithSelection( "ui_editor_add", out var panel ) is not { } window ) return;

		var made = window.AddChild( panel, tag, classes, text );
		if ( made is not null ) window.Select( made );

		Log.Info( $"ui_editor_add: {( made is null ? "did not add" : "added" )} <{tag}>" );
	}

	/// <summary>Set an attribute on the selected panel's tag: <c>ui_editor_attr class "a b"</c>; an empty value takes it off.</summary>
	[ConCmd( "ui_editor_attr" )]
	public static void AttributeFromConsole( string name, string value = "" )
	{
		if ( WithSelection( "ui_editor_attr", out var panel ) is not { } window ) return;

		Log.Info( $"ui_editor_attr: {( window.SetAttribute( panel, name, value ) ? "wrote" : "did not write" )} {name}" );
	}

	/// <summary>Set the words in the selected panel's tag: <c>ui_editor_text "some words"</c>.</summary>
	[ConCmd( "ui_editor_text" )]
	public static void TextFromConsole( string text )
	{
		if ( WithSelection( "ui_editor_text", out var panel ) is not { } window ) return;

		Log.Info( $"ui_editor_text: {( window.SetText( panel, text ) ? "wrote" : "did not write" )}" );
	}

	/// <summary>Take the selected panel's tag out of the file.</summary>
	[ConCmd( "ui_editor_delete" )]
	public static void DeleteFromConsole()
	{
		if ( WithSelection( "ui_editor_delete", out var panel ) is not { } window ) return;

		Log.Info( $"ui_editor_delete: {( window.Delete( panel ) ? "deleted" : "did not delete" )}" );
	}

	/// <summary>Move the selected panel's tag among its siblings: <c>ui_editor_move -1</c> up, <c>1</c> down.</summary>
	[ConCmd( "ui_editor_move" )]
	public static void MoveFromConsole( int direction )
	{
		if ( WithSelection( "ui_editor_move", out var panel ) is not { } window ) return;

		Log.Info( $"ui_editor_move: {( window.Move( panel, direction ) ? "moved" : "did not move" )}" );
	}

	/// <summary>Say where the selected panel and everything under it were written, and which tag each is found as.</summary>
	[ConCmd( "ui_editor_where" )]
	public static void WhereFromConsole()
	{
		if ( WithSelection( "ui_editor_where", out var panel ) is not { } window ) return;

		foreach ( var each in panel.Descendants.Prepend( panel ) )
		{
			var tag = window.LocateTag( each );
			Log.Info( $"ui_editor_where: {each.ElementName}.{each.Classes} at {each.SourceFile}:{each.SourceLine}:{each.SourceColumn} -> {( tag is null ? "nothing" : tag.ToString() + " ." + tag.Classes )}" );
		}
	}

	/// <summary>Drag the selection on the canvas by so many stage pixels, as the pointer would: <c>ui_editor_drag 40 0</c>; with a grip name first, pull that edge: <c>ui_editor_drag se 40 20</c>.</summary>
	[ConCmd( "ui_editor_drag" )]
	public static void DragFromConsole( string a, string b, string c = null )
	{
		if ( WithSelection( "ui_editor_drag", out var panel ) is not { } window || window.CanvasPanel is not { } canvas ) return;

		string grip = c is null ? null : a;
		var dx = float.Parse( c is null ? a : b, System.Globalization.CultureInfo.InvariantCulture );
		var dy = float.Parse( c is null ? b : c, System.Globalization.CultureInfo.InvariantCulture );

		//A move takes the whole selection along, the way a drag on the canvas does.
		var moving = grip is null ? window.Selected.Where( CanvasDrag.CanMove ).ToList() : new List<Panel> { panel };
		var drags = moving.Select( p => grip is null ? CanvasDrag.Move( canvas, p, Vector2.Zero ) : CanvasDrag.Resize( canvas, p, grip, Vector2.Zero ) ).ToList();

		foreach ( var drag in drags ) drag.MoveTo( new Vector2( dx, dy ) * canvas.Fit );

		using ( window.BeginGesture( "drag" ) )
			foreach ( var drag in drags ) drag.Finish();

		Log.Info( $"ui_editor_drag: {( grip ?? "move" )} by {dx},{dy}, {drags.Count} panel(s)" );
	}

	static UiEditorWindow Newest( string command )
	{
		if ( UiEditorWindow.Editors.FirstOrDefault( x => x.IsValid() ) is { } window && window.Document is not null ) return window;

		Log.Warning( $"{command}: no UI editor with a file open" );
		return null;
	}

	/// <summary>Add a [Parameter] to the document's @code: <c>ui_editor_property Health float</c>.</summary>
	[ConCmd( "ui_editor_property" )]
	public static void PropertyFromConsole( string name, string type = "string" )
	{
		if ( Newest( "ui_editor_property" ) is { } window )
			Log.Info( $"ui_editor_property: {( window.AddProperty( name, type ) ? "added" : "did not add" )} {name}" );
	}

	/// <summary>Set a design value on the document's instance and keep it: <c>ui_editor_data Title "Hello"</c>.</summary>
	[ConCmd( "ui_editor_data" )]
	public static void DataFromConsole( string name, string value )
	{
		if ( Newest( "ui_editor_data" ) is not { } window || window.Document?.Type is not { } type || window.Instance is not { } instance ) return;

		var member = DesignData.Members( type ).FirstOrDefault( x => string.Equals( x.name, name, StringComparison.OrdinalIgnoreCase ) );

		if ( member.name is null )
		{
			Log.Warning( $"ui_editor_data: {type.Name} has no member {name}" );
			return;
		}

		object parsed = member.type == typeof( string ) ? value : Json.Deserialize( value, member.type );
		member.set( instance, parsed );
		window.SetDesignValue( member.name, parsed, member.type );
		window.CanvasPanel?.Rerender();

		Log.Info( $"ui_editor_data: {member.name} = {value}" );
	}

	/// <summary>Give the selected tag a handler: <c>ui_editor_event onclick</c>, or <c>ui_editor_event onclick parameter</c> for an Action parameter.</summary>
	[ConCmd( "ui_editor_event" )]
	public static void EventFromConsole( string eventName, string kind = "method" )
	{
		if ( WithSelection( "ui_editor_event", out var panel ) is { } window )
			Log.Info( $"ui_editor_event: {( window.AddEventHandler( panel, eventName, kind == "parameter" ) ? "added" : "did not add" )} {eventName}" );
	}

	/// <summary>Keyframes in the document's sheet: <c>ui_editor_keyframes fade</c> makes one; <c>ui_editor_keyframes fade 50%</c> adds a stop; <c>ui_editor_keyframes fade to opacity 1</c> writes a value.</summary>
	[ConCmd( "ui_editor_keyframes" )]
	public static void KeyframesFromConsole( string name, string stop = null, string property = null, string value = null )
	{
		if ( Newest( "ui_editor_keyframes" ) is not { } window ) return;

		bool done;

		if ( stop is null ) done = window.AddKeyframes( name );
		else if ( property is null ) done = window.AddKeyframeStop( name, stop );
		else done = window.WriteKeyframe( name, stop, property, value ?? "" );

		Log.Info( $"ui_editor_keyframes: {( done ? "wrote" : "did not write" )} {name} {stop} {property} {value}" );
	}

	/// <summary>Hold or let go of a state on the selection: <c>ui_editor_state hover on</c>, <c>ui_editor_state open off</c>.</summary>
	[ConCmd( "ui_editor_state" )]
	public static void StateFromConsole( string state, string on = "on" )
	{
		if ( WithSelection( "ui_editor_state", out _ ) is not { } window || window.States is not { } states ) return;

		var pseudo = PanelStates.Pseudo.FirstOrDefault( x => x.name == state.TrimStart( ':' ) );

		if ( pseudo.name is not null ) states.Force( pseudo.flag, on == "on" );
		else states.Force( state.TrimStart( '.' ), on == "on" );

		Log.Info( $"ui_editor_state: {state} {on}" );
	}

	/// <summary>Wrap the selected tag: <c>ui_editor_repeat Items</c> in a @foreach, <c>ui_editor_repeat Open if</c> in an @if.</summary>
	[ConCmd( "ui_editor_repeat" )]
	public static void RepeatFromConsole( string member, string kind = "foreach" )
	{
		if ( WithSelection( "ui_editor_repeat", out var panel ) is { } window )
			Log.Info( $"ui_editor_repeat: {( ( kind == "if" ? window.ShowWhen( panel, member ) : window.RepeatFor( panel, member ) ) ? "wrapped" : "did not wrap" )}" );
	}

	/// <summary>Print the rules that dress the selection, with the file each is from and whether it is the document's own.</summary>
	[ConCmd( "ui_editor_rules" )]
	public static void RulesFromConsole()
	{
		if ( WithSelection( "ui_editor_rules", out var panel ) is not { } window ) return;

		var cascade = StyleCascade.For( panel, window.Document?.ScssPath );
		var own = window.Document?.ScssPath;

		foreach ( var rule in cascade.Rules )
			Log.Info( $"ui_editor_rules: {rule.SelectorText}  file={rule.FileName}  path={rule.Path ?? "(none)"}  own={own}" );

		Log.Info( $"ui_editor_rules: edit target = {cascade.EditTarget( null, own )?.SelectorText ?? "(none)"}" );
	}

	/// <summary>Zoom the canvas: <c>ui_editor_zoom 8</c>.</summary>
	[ConCmd( "ui_editor_zoom" )]
	public static void ZoomFromConsole( float zoom )
	{
		if ( Newest( "ui_editor_zoom" ) is { } window ) window.CanvasPanel?.ZoomTo( zoom, null );
	}

	/// <summary>Open or close a dock of the newest UI editor: <c>ui_editor_dock Animation on</c>.</summary>
	[ConCmd( "ui_editor_dock" )]
	public static void DockFromConsole( string title, string on = "on" )
	{
		if ( UiEditorWindow.Editors.FirstOrDefault( x => x.IsValid() ) is { } window )
		{
			window.DockManager.SetDockState( title, on == "on" );
			if ( on == "on" ) window.DockManager.RaiseDock( title );
		}
	}

	/// <summary>Select the first panel wearing a class: <c>ui_editor_find tile</c>.</summary>
	[ConCmd( "ui_editor_find" )]
	public static void FindFromConsole( string cls )
	{
		if ( Newest( "ui_editor_find" ) is not { } window || window.Root is not { } root ) return;

		var found = root.Descendants.FirstOrDefault( x => x.HasClass( cls ) );
		if ( found is not null ) window.Select( found );

		Log.Info( $"ui_editor_find: {( found is null ? "nothing" : found.ElementName + "." + found.Classes )}" );
	}

	/// <summary>Undo the last change in the newest UI editor.</summary>
	[ConCmd( "ui_editor_undo" )]
	public static void UndoFromConsole()
	{
		if ( UiEditorWindow.Editors.FirstOrDefault( x => x.IsValid() ) is { } window )
			Log.Info( $"ui_editor_undo: {( window.Undo() ? "undone" : "nothing to undo" )}" );
	}

	/// <summary>Say where the open document's styles are, and move a <c>&lt;style&gt;</c> into the sheet beside it: <c>ui_editor_styles</c>, <c>ui_editor_styles move</c>.</summary>
	[ConCmd( "ui_editor_styles" )]
	public static void StylesFromConsole( string what = "" )
	{
		if ( Newest( "ui_editor_styles" ) is not { } window || window.Document is not { } document ) return;

		if ( what == "move" )
		{
			Log.Info( $"ui_editor_styles: {( window.MoveStylesToSheet() ? "moved" : "did not move" )}" );
			return;
		}

		Log.Info( $"ui_editor_styles: {document.StyleTags().Count} <style> tag(s), sheet {System.IO.Path.GetFileName( document.ScssPath )} {( document.HasScss ? "there" : "not there" )}" );
	}

	/// <summary>Write a style property for the selected panel into the rule the inspector would edit: <c>ui_editor_set left 40px</c>.</summary>
	[ConCmd( "ui_editor_set" )]
	public static void SetFromConsole( string property, string value )
	{
		if ( UiEditorWindow.Editors.FirstOrDefault( x => x.IsValid() ) is not { } window || window.Selection is not { } panel )
		{
			Log.Warning( "ui_editor_set: nothing selected in a UI editor" );
			return;
		}

		var written = window.WriteStyles( panel, new List<(string, string)> { ( property, value ) } );
		Log.Info( $"ui_editor_set: {( written ? "wrote" : "did not write" )} {property}: {value}" );
	}
}
