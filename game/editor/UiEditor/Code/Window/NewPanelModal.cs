using System.IO;
using Rinten.UI;

using UILabel = Rinten.UI.Label;

namespace Editor.UiEditor;

//The card a new panel is made from: a name, a folder, what it inherits, the shape it starts as, and whether
//a game object in the open scene shows it. Over the window, the way the editor asks everything else.
[StyleSheet( "NewPanelModal.cs.scss" )]
internal sealed class NewPanelModal : EditorPanel
{
	readonly UiEditorWindow _window;
	readonly TextField _name;
	readonly TextField _folder;
	readonly UILabel _preview;
	readonly UILabel _sceneCheck;
	readonly UILabel _sceneNote;

	string _base = "PanelComponent";
	PanelTemplates.Template _template = PanelTemplates.All[0];
	bool _toScene;

	readonly List<(UILabel label, string value)> _bases = new();
	readonly List<(Panel row, PanelTemplates.Template template)> _templates = new();

	public NewPanelModal( UiEditorWindow window, string folder )
	{
		_window = window;

		AddClass( "new-panel-modal" );
		AcceptsFocus = true;

		var card = AddChild<Panel>( "np-card" );

		var head = card.AddChild<Panel>( "np-head" );
		var icon = head.AddChild<UILabel>( "np-icon np-badge" );
		icon.Tokenize = false;
		icon.Text = "note_add";
		var title = head.AddChild<UILabel>( "np-title" );
		title.Tokenize = false;
		title.Text = "New Panel";

		//Name
		var nameRow = card.AddChild<Panel>( "np-row" );
		Label( nameRow, "Name" );
		_name = nameRow.AddChild<TextField>( "np-field" );
		_name.Placeholder = "MainMenu";
		_name.ValueChanged = _ => Refresh();
		_name.Submitted = _ => Create();

		//Folder
		var folderRow = card.AddChild<Panel>( "np-row" );
		Label( folderRow, "Folder" );
		_folder = folderRow.AddChild<TextField>( "np-field" );
		_folder.Value = folder;
		_folder.ValueChanged = _ => Refresh();

		//Base
		var baseRow = card.AddChild<Panel>( "np-row" );
		Label( baseRow, "Inherits" );
		var bases = baseRow.AddChild<Panel>( "np-choices" );

		foreach ( var (value, what) in new[] { ( "PanelComponent", "a component of a game object" ), ( "Panel", "a panel other razors put in their markup" ) } )
		{
			var choice = bases.AddChild<UILabel>( "np-choice" );
			choice.Tokenize = false;
			choice.Text = value;
			choice.Tooltip = what;
			choice.AddEventListener( "onclick", () => { _base = value; Refresh(); } );
			_bases.Add( ( choice, value ) );
		}

		//Template
		var templateBlock = card.AddChild<Panel>( "np-row np-column" );
		Label( templateBlock, "Starts as" );
		var templates = templateBlock.AddChild<Panel>( "np-templates" );

		foreach ( var template in PanelTemplates.All )
		{
			var row = templates.AddChild<Panel>( "np-template" );

			var ticon = row.AddChild<UILabel>( "np-icon np-ticon" );
			ticon.Tokenize = false;
			ticon.Text = template.Icon;

			var words = row.AddChild<Panel>( "np-words" );
			var tname = words.AddChild<UILabel>( "np-name" );
			tname.Tokenize = false;
			tname.Text = template.Name;
			var what = words.AddChild<UILabel>( "np-what" );
			what.Tokenize = false;
			what.Text = template.What;

			var chosen = template;
			row.AddEventListener( "onclick", () => { _template = chosen; Refresh(); } );
			_templates.Add( ( row, template ) );
		}

		//Scene
		var sceneRow = card.AddChild<Panel>( "np-row np-check-row" );
		_sceneCheck = sceneRow.AddChild<UILabel>( "np-icon np-check" );
		_sceneCheck.Tokenize = false;
		var sceneLabel = sceneRow.AddChild<UILabel>( "np-check-label" );
		sceneLabel.Tokenize = false;
		sceneLabel.Text = "Put it in the open scene";
		sceneRow.AddEventListener( "onclick", () => { _toScene = !_toScene; Refresh(); } );
		_sceneNote = sceneRow.AddChild<UILabel>( "np-note" );
		_sceneNote.Tokenize = false;

		_preview = card.AddChild<UILabel>( "np-preview" );
		_preview.Tokenize = false;

		var actions = card.AddChild<Panel>( "np-actions" );
		var cancel = actions.AddChild<UILabel>( "np-btn" );
		cancel.Tokenize = false;
		cancel.Text = "Cancel";
		cancel.AddEventListener( "onclick", () => Delete() );
		var create = actions.AddChild<UILabel>( "np-btn np-primary" );
		create.Tokenize = false;
		create.Text = "Create";
		create.AddEventListener( "onclick", Create );

		_toScene = SceneEditorSession.Active is not null;

		Refresh();
	}

	static void Label( Panel row, string text )
	{
		var label = row.AddChild<UILabel>( "np-label" );
		label.Tokenize = false;
		label.Text = text;
	}

	protected override void OnAfterTreeRender( bool firstTime )
	{
		base.OnAfterTreeRender( firstTime );
		if ( firstTime ) _name.Focus();
	}

	//What the files will be called, from the name and the folder as typed.
	string RazorPath
	{
		get
		{
			var name = Sanitize( _name.Value );
			if ( name.Length == 0 ) return null;

			var folder = ( _folder.Value ?? "" ).Trim();
			if ( !Path.IsPathRooted( folder ) && Project.Current?.GetRootPath() is { } root ) folder = Path.Combine( root, folder );

			return Path.Combine( folder, name + ".razor" );
		}
	}

	static string Sanitize( string typed )
	{
		var sb = new System.Text.StringBuilder();
		var up = true;

		foreach ( var c in typed ?? "" )
		{
			if ( char.IsLetterOrDigit( c ) ) { sb.Append( up ? char.ToUpperInvariant( c ) : c ); up = false; }
			else up = true;
		}

		return sb.ToString();
	}

	void Refresh()
	{
		foreach ( var (label, value) in _bases ) label.SetClass( "on", value == _base );
		foreach ( var (row, template) in _templates ) row.SetClass( "on", ReferenceEquals( template, _template ) );

		var sceneOpen = SceneEditorSession.Active is not null;
		var canScene = sceneOpen && _base == "PanelComponent";
		if ( !canScene ) _toScene = false;

		_sceneCheck.Text = _toScene ? "check_box" : "check_box_outline_blank";
		_sceneCheck.SetClass( "off", !canScene );
		_sceneNote.Text = !sceneOpen ? "no scene is open" : _base != "PanelComponent" ? "only a PanelComponent goes on a game object" : "a game object with a ScreenPanel and this";

		var path = RazorPath;
		_preview.Text = path is null ? "Type a name." : File.Exists( path ) ? $"{Path.GetFileName( path )} is already there." : $"{Relative( path )} and its .scss";
		_preview.SetClass( "bad", path is not null && File.Exists( path ) );
	}

	static string Relative( string path )
	{
		var root = Project.Current?.GetRootPath();
		if ( root is not null && path.StartsWith( root, StringComparison.OrdinalIgnoreCase ) ) return path.Substring( root.Length ).TrimStart( '/', '\\' );
		return path;
	}

	void Create()
	{
		var path = RazorPath;
		if ( path is null || File.Exists( path ) ) { Refresh(); return; }

		var name = Path.GetFileNameWithoutExtension( path );
		var (razor, scss) = PanelTemplates.Fill( _template, name, _base );

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( path ) );
			File.WriteAllText( path, razor );
			File.WriteAllText( path + ".scss", scss );
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"UI editor: could not write {path}: {e.Message}" );
			return;
		}

		Delete();

		//Through the documents, so it gets a tab: the blank one it was asked in, or a new one
		var window = UiEditorWindow.Open( path ) ?? _window;

		if ( _toScene ) window.PutInSceneWhenCompiled( name );
	}

	//Clicking beside the card is the same as leaving.
	protected override void OnClick( MousePanelEvent e )
	{
		base.OnClick( e );

		if ( e.Target != this ) return;

		Delete();
		e.StopPropagation();
	}

	public override void OnButtonTyped( ButtonEvent e )
	{
		base.OnButtonTyped( e );

		if ( !e.Pressed ) return;

		if ( e.Button == "escape" ) Delete();
	}
}
