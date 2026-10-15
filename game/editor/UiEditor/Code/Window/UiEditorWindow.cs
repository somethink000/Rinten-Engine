using System.IO;
using Rinten.Helpers;
using Rinten.UI;

namespace Editor.UiEditor;

/// <summary>The UI editor: edits write straight to the .razor/.scss; undo stores file text before and after</summary>
[EditorApp( "UI Editor", "web", "Interfaces: a razor panel drawn by the engine, its tree, and the rules that dress it", Opens = "razor" )]
public sealed partial class UiEditorWindow : DockWindow, IFileEditor
{
	//Which saved dock layout this window's arrangement belongs to. Bumped whenever the default layout changes shape.
	internal const string LayoutCookie = "UiEditor.6";

	//Every one of these that is open, newest first.
	internal static readonly List<UiEditorWindow> Editors = new();

	readonly UndoSystem _undo = new();

	RazorDocument _document;

	Hierarchy _hierarchy;
	Palette _palette;
	Inspector _inspector;
	internal Data _data;
	AnimationDock _animation;
	DesignCanvas _canvas;

	//One of the document's own panels, made again with the workspace on hotload - see DesignCanvas.
	[SkipHotload]
	Panel _selection;

	/// <summary>The file open here, if there is one.</summary>
	public RazorDocument Document => _document;

	DesignData _design;

	/// <summary>The values the document is shown with while it is edited.</summary>
	public DesignData Design => _design;

	//The object the document's members are on, on the canvas.
	internal object Instance => _canvas?.Canvas?.Instance;

	/// <summary>The panel selected on the canvas or in the tree, if any - the primary, when several are.</summary>
	public Panel Selection => _selection.IsValid() ? _selection : null;

	readonly List<Panel> _selected = new();

	/// <summary>Every selected panel, the primary first; empty when nothing is.</summary>
	public IReadOnlyList<Panel> Selected
	{
		get
		{
			_selected.RemoveAll( x => !x.IsValid() );
			return _selected;
		}
	}

	/// <summary>Whether several panels are selected.</summary>
	public bool HasMany => Selected.Count > 1;

	/// <summary>The instance of the document's type on the canvas, if there is one.</summary>
	public Panel Root => _canvas?.Root;

	//The canvas's panel, for a drag driven from the console.
	internal DesignCanvasPanel CanvasPanel => _canvas?.Canvas;

	public UiEditorWindow()
	{
		DeleteOnClose = true;
		Size = new Vector2( 1500, 950 );

		SetWindowIcon( "web" );

		BuildUI();
		UpdateTitle();

		//After the docks and not before: setting this is what restores the saved layout, and
		//the default it falls back to opens docks that have to have been registered already.
		StateCookie = LayoutCookie;

		Register();
	}

	/// <summary>Opens a .razor in the UI editor's workspace tab, replacing any file already open there</summary>
	public static UiEditorWindow Open( string razorPath )
	{
		if ( string.IsNullOrEmpty( razorPath ) ) return null;

		var full = Path.GetFullPath( razorPath );

		//Makes the workspace and the window in it, or brings the one there is to the front; either way the window registers itself first in Editors.
		EditorAppAttribute.For( typeof( UiEditorWindow ) )?.Open();

		var window = Editors.FirstOrDefault( x => x.IsValid() ) ?? new UiEditorWindow();

		if ( window._document is null || !string.Equals( window._document.RazorPath, full, StringComparison.OrdinalIgnoreCase ) )
			window.OpenDocument( new RazorDocument( full ) );

		window.Show();

		return window;
	}

	//A razor file chosen on File > Open - see EditorAppAttribute.OpenFile.
	void IFileEditor.OpenFile( string path ) => Open( path );

	void IFileEditor.NewFile() => NewFile();

	//Open a file here, in place of whatever was open.
	internal void OpenDocument( RazorDocument document )
	{
		_document = document;
		_selection = null;
		_design = document is null ? null : new DesignData( document.RazorPath );

		_undo.Initialize();

		_canvas?.SetDocument( _document );
		_hierarchy?.SetRoot( Root );
		_inspector?.Show( null );
		_data?.SetDocument( _document );
		_animation?.SetDocument( _document );
		_palette?.SetDocument( _document );

		if ( _document is not null && _document.Type is null )
			Log.Warning( $"UI editor: no compiled panel type for {_document.RazorPath} - is the file in a project that is loaded?" );

		Register();
		UpdateTitle();
	}

	public override void Show()
	{
		if ( !IsEmbedded )
			base.Show();

		StateCookie = LayoutCookie;

		Register();
		ApplyLayout();
	}

	//Whether this editor is a tab in the main window rather than a window of its own - the workspace takes the
	//menu bar, tool bars and docks out of the frame and leaves the window itself an empty husk that never shows.
	internal bool IsEmbedded => DockManager.IsValid() && DockManager.Parent != this;

	PanelStates _states;

	//The states forced on the selection, for seeing its rules for them.
	internal PanelStates States => _states;

	//Make a panel the selection - the only one - and tell every dock.
	internal void Select( Panel panel )
	{
		if ( ReferenceEquals( _selection, panel ) && _selected.Count <= 1 ) return;

		_selected.Clear();
		if ( panel.IsValid() ) _selected.Add( panel );

		SetPrimary( panel );
	}

	//Add a panel to the selection, or take it out when it is in; the primary is the last added.
	internal void ToggleSelect( Panel panel )
	{
		if ( !panel.IsValid() ) return;

		if ( _selected.Remove( panel ) )
		{
			SetPrimary( _selected.LastOrDefault() );
			return;
		}

		_selected.Add( panel );
		SetPrimary( panel );
	}

	//Select these and nothing else, the first as primary.
	internal void SelectMany( IEnumerable<Panel> panels )
	{
		_selected.Clear();
		_selected.AddRange( panels.Where( x => x.IsValid() ).Distinct() );

		SetPrimary( _selected.FirstOrDefault() );
	}

	//Whether a panel is in the selection, primary or not.
	internal bool IsInSelection( Panel panel ) => panel.IsValid() && _selected.Contains( panel );

	internal void SetPrimary( Panel panel )
	{
		//What was forced on the last selection goes with it.
		if ( !ReferenceEquals( _selection, panel ) )
		{
			_states?.Release();
			_states = panel.IsValid() ? new PanelStates( panel ) : null;
		}

		_selection = panel.IsValid() ? panel : null;

		//Kept while the panel is alive: a hotload builds the tree again and deletes the old
		//panels before the window hears of it, and by then the selection cannot say where it was.
		if ( _selection is not null ) _lastSelectionPath = PanelPath.Of( Root, _selection );

		_hierarchy?.NoteSelection();
		_canvas?.NoteSelection();
		_inspector?.Show( _selection );
	}

	//Whether this panel is the one selected.
	internal bool IsSelected( Panel panel ) => panel.IsValid() && ReferenceEquals( _selection, panel );

	//The canvas has a new instance of the document - after a hotload, a resolution change or
	//a reload - so the tree is rebuilt and the selection found again in it by the path it had.
	internal void OnRootChanged( IReadOnlyList<int> selectionPath )
	{
		_hierarchy?.SetRoot( Root );

		_selection = null;
		Select( Root );

		//A new instance has no children until its first render; the path is followed once they are there - see RestoreSelection.
		_pendingSelection = selectionPath is { Count: > 0 } ? selectionPath : null;
		_pendingSince = 0;
	}

	IReadOnlyList<int> _pendingSelection;
	RealTimeSince _pendingSince;

	//Follow the selection's old path into the new tree once the tree has been built - a razor panel makes its children
	//on its first tick, not when it is made - and give up after a moment when the path no longer leads anywhere.
	[EditorEvent.Frame]
	void RestoreSelection()
	{
		_states?.Hold();

		if ( _pendingSelection is null ) return;

		if ( PanelPath.TryFollow( Root, _pendingSelection, out var found ) )
		{
			_pendingSelection = null;
			Select( found );
			return;
		}

		if ( _pendingSince > 2.0f )
			_pendingSelection = null;
	}

	IReadOnlyList<int> _lastSelectionPath;

	//The selection changed place in the tree without changing; its path is read again.
	internal void NoteSelectionMoved()
	{
		if ( Selection is { } panel ) _lastSelectionPath = PanelPath.Of( Root, panel );
	}

	//Where the selection is in the tree, for finding it again in a new tree - or where
	//it was, when the tree has already gone - or where it is wanted, after a paste.
	internal IReadOnlyList<int> SelectionPath => _selectionAfterBuild ?? PanelPath.Of( Root, Selection ) ?? _lastSelectionPath;

	IReadOnlyList<int> _selectionAfterBuild;
	Panel _selectionAfterBuildWas;
	RealTimeSince _selectionAfterBuildSince;

	//A tag written but not yet built - a paste, a group - is where the selection goes once the tree has
	//it: when the panel at that path is another one than now, the hotload having made the tree again.
	internal void SelectAfterBuild( IReadOnlyList<int> path )
	{
		_selectionAfterBuild = path;
		_selectionAfterBuildWas = PanelPath.TryFollow( Root, path, out var now ) ? now : null;
		_selectionAfterBuildSince = 0;
	}

	[EditorEvent.Frame]
	void FollowSelectionAfterBuild()
	{
		if ( _selectionAfterBuild is not { } path ) return;

		if ( _selectionAfterBuildSince > 30 )
		{
			_selectionAfterBuild = null;
			return;
		}

		if ( !PanelPath.TryFollow( Root, path, out var found ) || ReferenceEquals( found, _selectionAfterBuildWas ) ) return;

		_selectionAfterBuild = null;
		_selectionAfterBuildWas = null;
		Select( found );
	}

	//Data

	//Build the document's instance again, with the design values as they are now.
	internal void ReloadInstance()
	{
		var selectionPath = SelectionPath;

		_canvas?.SetDocument( _document );
		OnRootChanged( selectionPath );
	}

	//The names a value in the markup can be bound to: the document's members.
	internal IEnumerable<string> BindableNames()
	{
		if ( _document?.Type is not { } type ) return Array.Empty<string>();

		return DesignData.Members( type ).Select( x => x.name ).OrderBy( x => x, StringComparer.OrdinalIgnoreCase ).ToList();
	}

	//Add a member to the document's @code : a parameter of the type given, to bind to.
	internal bool AddProperty( string name, string type = "string" )
	{
		if ( _document is null || string.IsNullOrWhiteSpace( name ) ) return false;

		name = name.Trim();

		if ( !char.IsLetter( name[0] ) && name[0] != '_' || name.Any( c => !char.IsLetterOrDigit( c ) && c != '_' ) )
		{
			Log.Warning( $"UI editor: '{name}' is not a name a property can have" );
			return false;
		}

		var markup = _document.Markup;
		var after = markup.WithCodeMember( $"[Parameter] public {type} {name} {{ get; set; }}" );

		return CommitMarkup( $"add {name}", markup.Text, after );
	}

	//Events

	//Give a tag a handler for an event: the attribute on the tag, and in @code either a method to fill in - opened in the
	//code editor - or a parameter of type Action the attribute invokes, for whoever puts the component in a scene to bind.
	internal bool AddEventHandler( Panel panel, string eventName, bool asParameter )
	{
		if ( TagOf( panel, $"add {eventName}" ) is not { } element ) return false;

		//A name from the tag: OnClick, or OnPlayButtonClick for <div class="play-button">.
		var subject = Pascal( element.Classes.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).FirstOrDefault() ?? element.Name );
		var verb = Pascal( eventName.Substring( 2 ) );
		var method = subject == "Div" || subject == "Root" ? $"On{verb}" : $"On{subject}{verb}";

		//One name, not two: a second handler for the same thing on another tag gets a number.
		var text = _document.Markup.Text;
		var name = method;
		for ( var i = 2; text.Contains( name + "(" ) || text.Contains( name + " " ); i++ ) name = method + i;

		var markup = _document.Markup;
		string after;

		if ( asParameter )
		{
			after = markup.WithAttribute( element, eventName, $"@( () => {name}?.Invoke() )" );
			after = RazorMarkup.Parse( after ).WithCodeMember( $"/// <summary>What pressing this does; bound by whoever uses the panel.</summary>\n[Parameter] public Action {name} {{ get; set; }}" );
		}
		else
		{
			after = markup.WithAttribute( element, eventName, "@" + name );
			after = RazorMarkup.Parse( after ).WithCodeMember( $"void {name}( PanelEvent e )\n{{\n}}" );
		}

		if ( !CommitMarkup( $"{eventName} on <{element.Name}>", markup.Text, after ) ) return false;

		if ( !asParameter ) OpenCodeMember( name );

		return true;
	}

	//Open the code editor at a member of the document's @code .
	internal void OpenCodeMember( string name )
	{
		if ( _document is null ) return;

		var text = _document.Markup.Text;
		var at = text.IndexOf( " " + name + "(", StringComparison.Ordinal );
		if ( at < 0 ) at = text.IndexOf( " " + name + " ", StringComparison.Ordinal );

		var line = at < 0 ? 0 : _document.Markup.LineOf( at ) + 1;

		InspectorPanel.OpenFile( _document.RazorPath, line );
	}

	static string Pascal( string s )
	{
		if ( string.IsNullOrEmpty( s ) ) return "";

		var parts = s.Split( new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries );
		return string.Concat( parts.Select( p => char.ToUpperInvariant( p[0] ) + p.Substring( 1 ) ) );
	}

	//Into a component and back

	readonly Stack<(string path, IReadOnlyList<int> selection)> _history = new();

	//Whether there is a document to go back to.
	internal bool CanGoBack => _history.Count > 0;

	//The razor a panel's own type was written in, when it is a component of the project's and not this file.
	internal string ComponentPathOf( Panel panel )
	{
		if ( !panel.IsValid() || _document is null ) return null;

		var type = TypeLibrary.GetType( panel.GetType() );
		if ( type?.SourceFile is not { Length: > 0 } source ) return null;
		if ( !source.EndsWith( ".razor", StringComparison.OrdinalIgnoreCase ) ) return null;

		var path = ResolveProjectPath( source );
		if ( path is null || string.Equals( path, _document.RazorPath, StringComparison.OrdinalIgnoreCase ) ) return null;

		return path;
	}

	//Open the razor a panel's type was written in, with the way back kept.
	internal void OpenComponent( Panel panel )
	{
		if ( ComponentPathOf( panel ) is not { } path ) return;

		_history.Push( ( _document.RazorPath, SelectionPath ) );
		OpenDocument( new RazorDocument( path ) );
	}

	//Back to the document this one was opened from, with its selection.
	internal void GoBack()
	{
		if ( _history.Count == 0 ) return;

		var (path, selection) = _history.Pop();

		OpenDocument( new RazorDocument( path ) );
		_pendingSelection = selection;
		_pendingSince = 0;
	}

	//A path a type recorded, relative to its project, made absolute.
	static string ResolveProjectPath( string relative )
	{
		if ( Path.IsPathRooted( relative ) ) return File.Exists( relative ) ? relative : null;

		//From a project's root, or from its code folder - a type compiled from Code/UI/X.razor says its source is UI/X.razor.
		foreach ( var project in EditorUtility.Projects.GetAll() )
		{
			var root = project.GetRootPath();

			foreach ( var candidate in new[] { Path.Combine( root, relative ), Path.Combine( root, "Code", relative ), Path.Combine( root, "code", relative ) } )
				if ( File.Exists( candidate ) ) return Path.GetFullPath( candidate );
		}

		return null;
	}

	void UpdateTitle()
	{
		var name = _document?.RazorPath;

		if ( name is not null && Project.Current?.GetRootPath() is { Length: > 0 } root )
		{
			var relative = Path.GetRelativePath( root, name );
			if ( !relative.StartsWith( ".." ) ) name = relative;
		}

		Title = $"UI - {name ?? "untitled"}";
	}

	protected override bool OnClose() => true;

	public override void OnDestroyed()
	{
		Editors.Remove( this );

		base.OnDestroyed();
	}

	void Register()
	{
		Editors.Remove( this );
		Editors.Insert( 0, this );
	}

	//Editing the stylesheet

	//Writes one property into a rule on disk, found by selector; refuses if the rule is gone
	internal bool WriteStyle( StyleCascade.Rule rule, string property, string value )
	{
		if ( rule is null ) return false;

		//A rule written in a <style> names the razor as its file. Reading that as a sheet would read markup as css, and saving it would
		//write css over markup. Pull the tag into the sheet first, then write there - the same selector, now in a file that can be spliced.
		if ( rule.IsInStyleTag || rule.Path is null || !rule.Path.EndsWith( ".scss", StringComparison.OrdinalIgnoreCase ) )
		{
			if ( _document is null || !_document.HasStyleTag || !MoveStylesToSheet() )
			{
				var into = _document is null ? ".razor.scss" : Path.GetFileName( _document.ScssPath );

				Log.Warning( $"UI editor: `{rule.SelectorText}` is not in a stylesheet - it was written in {Path.GetFileName( rule.Path )}, which is markup. Move the styles into {into} to edit them: File > Move <style> to .razor.scss." );
				return false;
			}

			var moved = ScssDocument.Load( _document.ScssPath );
			var there = moved?.Find( rule.Block );

			if ( there is null )
			{
				Log.Warning( $"UI editor: could not find `{rule.SelectorText}` in {_document.ScssPath} to write {property}" );
				return false;
			}

			var movedAfter = string.IsNullOrWhiteSpace( value )
				? moved.WithoutDeclaration( there, property )
				: moved.WithDeclaration( there, property, value.Trim() );

			return Commit( moved, $"{property} in {rule.SelectorText}", movedAfter );
		}

		var sheet = ScssDocument.Load( rule.Path );
		var block = sheet?.Find( rule.Block );

		if ( block is null )
		{
			Log.Warning( $"UI editor: could not find `{rule.SelectorText}` in {rule.Path} to write {property}" );
			return false;
		}

		var after = string.IsNullOrWhiteSpace( value )
			? sheet.WithoutDeclaration( block, property )
			: sheet.WithDeclaration( block, property, value.Trim() );

		return Commit( sheet, $"{property} in {rule.SelectorText}", after );
	}

	//Write several properties for a panel at once - what a drag on the canvas ends with - into the rule the inspector
	//would edit, or into a new rule for the panel when nothing of the document's own dresses it yet. One change to undo.
	internal bool WriteStyles( Panel panel, IReadOnlyList<(string name, string value)> declarations )
	{
		if ( _document is null || !panel.IsValid() || declarations.Count == 0 ) return false;

		//A <style> still wins over anything written beside it, so a drag would land and then
		//jump back. Pull the tag into the sheet first; the rest of the write already goes there.
		using ( _document.HasStyleTag ? BeginGesture( $"edit styles of {_document.Name}" ) : null )
		{
			if ( _document.HasStyleTag && !MoveStylesToSheet() ) return false;

		var cascade = StyleCascade.For( panel, _document?.ScssPath );
		var rule = cascade.EditTarget( declarations[0].name, _document.ScssPath );

		//A rule still pointing at the razor is leftover from before the tag was moved: treat it as missing so the write goes into the sheet.
		if ( rule is not null && rule.IsInStyleTag ) rule = null;

		//With a state held on the panel, what is written is for that state: into its rule, nested under the panel's own, made when it is not there.
		if ( rule is not null && HeldStateSuffix( panel ) is { } suffix && !rule.SelectorText.Contains( suffix, StringComparison.OrdinalIgnoreCase ) )
		{
			var stateSheet = ScssDocument.Load( rule.Path );
			var own = stateSheet?.Find( rule.Block );

			if ( own is not null )
			{
				var existing = own.Children.FirstOrDefault( x => string.Equals( x.Selector, "&" + suffix, StringComparison.OrdinalIgnoreCase ) );

				if ( existing is null )
					return Commit( stateSheet, $"Add &{suffix} in {rule.SelectorText}", stateSheet.WithBlock( own, "&" + suffix, declarations ) );

				var stateText = stateSheet.Text;

				foreach ( var (name, value) in declarations )
				{
					var working = ScssDocument.FromText( stateSheet.Path, stateText );

					//The same nested block, found again in the text as it stands: by its selector under a parent with the parent's.
					var at = working.Blocks.FirstOrDefault( x => x.Selector == existing.Selector && x.Parent?.Selector == own.Selector && x.Parent.Line == own.Line );

					if ( at is null ) break;

					stateText = string.IsNullOrWhiteSpace( value ) ? working.WithoutDeclaration( at, name ) : working.WithDeclaration( at, name, value.Trim() );
				}

				return Commit( stateSheet, $"{declarations.Count} properties in {rule.SelectorText}{suffix}", stateText );
			}
		}

		if ( rule is null )
		{
			var selector = InspectorPanel.SelectorFor( panel, LocateTag( panel ) );

			if ( string.IsNullOrEmpty( selector ) ) return false;

			//With a state held, the panel's new rule is empty and the state's rule inside it takes the values.
			var held = HeldStateSuffix( panel );
			var outer = held is null ? declarations : Array.Empty<(string, string)>();

			var ownSheet = ScssDocument.Load( _document.ScssPath ) ?? ScssDocument.FromText( _document.ScssPath, "" );

			//Under the document's own root rule when it has one - the way a sheet written by hand nests everything under its panel - else at the top of the sheet.
			var rootBlock = RootRule() is { } root ? ownSheet.Find( root.Block ) : null;

			//Written into the file a moment ago and not yet loaded by the engine: the rule is
			//there in the text, and that is the one to write into, not a second of the same name.
			if ( ( ownSheet.Blocks.LastOrDefault( x => x.Selector == selector && x.Parent?.Line == rootBlock?.Line )
				?? ownSheet.Blocks.LastOrDefault( x => x.Selector == selector ) ) is { } already )
			{
				var soFar = ownSheet.Text;

				foreach ( var (name, value) in declarations )
				{
					var working = ScssDocument.FromText( ownSheet.Path, soFar );
					var at = working.Blocks.LastOrDefault( x => x.Selector == selector && x.Parent?.Line == rootBlock?.Line )
						?? working.Blocks.LastOrDefault( x => x.Selector == selector );

					if ( at is null ) break;

					if ( held is not null )
					{
						var state = at.Children.FirstOrDefault( x => string.Equals( x.Selector, "&" + held, StringComparison.OrdinalIgnoreCase ) );

						if ( state is null )
						{
							soFar = working.WithBlock( at, "&" + held, declarations );
							break;
						}

						at = state;
					}

					soFar = string.IsNullOrWhiteSpace( value ) ? working.WithoutDeclaration( at, name ) : working.WithDeclaration( at, name, value.Trim() );
				}

				return Commit( ownSheet, $"{declarations.Count} properties in {selector}{held}", soFar );
			}

			var ownText = rootBlock is not null ? ownSheet.WithBlock( rootBlock, selector, outer ) : ownSheet.WithBlock( null, selector, outer );

			if ( held is not null )
			{
				var again = ScssDocument.FromText( ownSheet.Path, ownText );
				var made = again.Blocks.LastOrDefault( x => x.Selector == selector );

				if ( made is not null ) ownText = again.WithBlock( made, "&" + held, declarations );
			}

			return Commit( ownSheet, $"Add {selector}{held}", ownText );
		}

		var sheet = ScssDocument.Load( rule.Path );
		var block = sheet?.Find( rule.Block );

		if ( block is null )
		{
			Log.Warning( $"UI editor: could not find `{rule.SelectorText}` in {rule.Path} to write into" );
			return false;
		}

		//Each write moves the text under it, so the block is found again in the text as it stands after the one before.
		var before = sheet.Text;
		var text = before;

		foreach ( var (name, value) in declarations )
		{
			var working = ScssDocument.FromText( sheet.Path, text );
			var at = working.Find( rule.Block );

			if ( at is null ) break;

			text = string.IsNullOrWhiteSpace( value ) ? working.WithoutDeclaration( at, name ) : working.WithDeclaration( at, name, value.Trim() );
		}

		return Commit( sheet, $"{declarations.Count} properties in {rule.SelectorText}", text );
		}
	}

	//The state held on a panel, as a selector's tail: :hover , .open , or null when none is held.
	string HeldStateSuffix( Panel panel )
	{
		if ( _states is null || !ReferenceEquals( _states.Panel, panel ) ) return null;

		foreach ( var (flag, name) in PanelStates.Pseudo )
			if ( _states.IsForced( flag ) ) return ":" + name;

		foreach ( var cls in PanelStates.StateClasses( panel ).Concat( panel.Class ) )
			if ( _states.IsForced( cls ) ) return "." + cls;

		return null;
	}

	//The rule in the document's own sheet that dresses its root panel by element name, if there is one.
	StyleCascade.Rule RootRule()
	{
		if ( Root is not { } root || _document is null ) return null;

		var cascade = StyleCascade.For( root, _document?.ScssPath );

		foreach ( var rule in cascade.Rules )
		{
			if ( rule.Path is null ) continue;
			if ( !string.Equals( System.IO.Path.GetFullPath( rule.Path ), System.IO.Path.GetFullPath( _document.ScssPath ), StringComparison.OrdinalIgnoreCase ) ) continue;
			if ( string.Equals( rule.SelectorText, root.ElementName, StringComparison.OrdinalIgnoreCase ) ) return rule;
		}

		return null;
	}

	//Add a rule to the document's own stylesheet, making the file when there is none, and write the properties given into it.
	internal bool AddRule( string selector, IEnumerable<(string name, string value)> declarations )
	{
		if ( _document is null ) return false;

		var sheet = ScssDocument.Load( _document.ScssPath ) ?? ScssDocument.FromText( _document.ScssPath, "" );
		var after = sheet.WithBlock( null, selector, declarations );

		return Commit( sheet, $"Add {selector}", after );
	}

	//Add a rule nested inside another - &:hover under .button - in that rule's file.
	internal bool AddNestedRule( StyleCascade.Rule parent, string selector )
	{
		if ( parent?.Path is null ) return false;

		var sheet = ScssDocument.Load( parent.Path );
		var block = sheet?.Find( parent.Block );

		if ( block is null ) return false;

		//Already there: nothing to add.
		if ( block.Children.Any( x => string.Equals( x.Selector, selector, StringComparison.OrdinalIgnoreCase ) ) ) return false;

		var after = sheet.WithBlock( block, selector, Array.Empty<(string, string)>() );

		return Commit( sheet, $"Add {selector} in {parent.SelectorText}", after );
	}

	//Keyframes

	//A new @keyframes at the end of the document's sheet, with a from and a to .
	internal bool AddKeyframes( string name )
	{
		if ( _document is null ) return false;

		var sheet = ScssDocument.Load( _document.ScssPath ) ?? ScssDocument.FromText( _document.ScssPath, "" );

		if ( sheet.Blocks.Any( x => string.Equals( x.Selector, $"@keyframes {name}", StringComparison.OrdinalIgnoreCase ) ) ) return false;

		var text = sheet.Text.TrimEnd();
		var after = text + ( text.Length > 0 ? "\n\n" : "" ) + $"@keyframes {name}\n{{\n\tfrom\n\t{{\n\t}}\n\n\tto\n\t{{\n\t}}\n}}\n";

		return Commit( sheet, $"Add @keyframes {name}", after );
	}

	//A stop added to an animation - 50% - empty.
	internal bool AddKeyframeStop( string animation, string stop )
	{
		var sheet = ScssDocument.Load( _document?.ScssPath );
		var block = KeyframesBlock( sheet, animation );

		if ( block is null ) return false;
		if ( block.Children.Any( x => string.Equals( x.Selector, stop, StringComparison.OrdinalIgnoreCase ) ) ) return false;

		return Commit( sheet, $"Add {stop} to {animation}", sheet.WithBlock( block, stop, Array.Empty<(string, string)>() ) );
	}

	//A property at a stop of an animation; an empty value takes it out.
	internal bool WriteKeyframe( string animation, string stop, string property, string value )
	{
		var sheet = ScssDocument.Load( _document?.ScssPath );
		var block = KeyframesBlock( sheet, animation )?.Children.FirstOrDefault( x => string.Equals( x.Selector, stop, StringComparison.OrdinalIgnoreCase ) );

		if ( block is null ) return false;

		var after = string.IsNullOrWhiteSpace( value ) ? sheet.WithoutDeclaration( block, property ) : sheet.WithDeclaration( block, property, value.Trim() );

		return Commit( sheet, $"{property} at {stop} of {animation}", after );
	}

	static ScssDocument.Block KeyframesBlock( ScssDocument sheet, string animation )
		=> sheet?.Blocks.FirstOrDefault( x => string.Equals( x.Selector, $"@keyframes {animation}", StringComparison.OrdinalIgnoreCase ) );

	bool Commit( ScssDocument sheet, string title, string after )
	{
		var before = sheet.Text;

		if ( before == after ) return false;

		sheet.Save( after );

		var path = sheet.Path;

		Record( title, () => Restore( path, before ), () => Restore( path, after ) );

		OnSheetWritten( path );

		return true;
	}

	//One entry per gesture

	//The writes of a gesture in progress, to go into the stack as one; null between gestures.
	List<(string title, Action undo, Action redo)> _gesture;
	string _gestureTitle;

	//Every write until the scope ends is one entry, undone whole: a drag of several,
	//an align, a delete of several. Scopes nest; the outermost owns the entry.
	internal IDisposable BeginGesture( string title )
	{
		if ( _gesture is not null ) return null;

		_gesture = new();
		_gestureTitle = title;

		return new GestureScope( this );
	}

	sealed class GestureScope : IDisposable
	{
		readonly UiEditorWindow _window;
		public GestureScope( UiEditorWindow window ) { _window = window; }
		public void Dispose() => _window.EndGesture();
	}

	void EndGesture()
	{
		var writes = _gesture;
		_gesture = null;

		if ( writes is null || writes.Count == 0 ) return;

		if ( writes.Count == 1 )
		{
			_undo.Insert( writes[0].title, writes[0].undo, writes[0].redo );
			return;
		}

		var title = $"{_gestureTitle} ({writes.Count} writes)";

		_undo.Insert( title,
			() => { for ( var i = writes.Count - 1; i >= 0; i-- ) writes[i].undo(); },
			() => { foreach ( var write in writes ) write.redo(); } );
	}

	//A write into the stack - or into the gesture in progress, when there is one.
	void Record( string title, Action undo, Action redo )
	{
		if ( _gesture is not null ) _gesture.Add( ( title, undo, redo ) );
		else _undo.Insert( title, undo, redo );
	}

	//Design values, undone like the files

	//Keep a design value, so the next instance shows it, and remember what it replaced.
	internal void SetDesignValue( string name, object value, Type type )
	{
		if ( Design is not { } design ) return;

		var before = design.Has( name ) ? design.Get( name )?.DeepClone() : null;
		var had = design.Has( name );

		design.Set( name, value, type );

		var after = design.Has( name ) ? design.Get( name )?.DeepClone() : null;

		var said = value is System.Collections.ICollection items && value is not string ? $"{items.Count} items" : value?.ToString() ?? "null";

		Record( $"{name} = {said}",
			() => { if ( had ) Design?.SetNode( name, before ); else Design?.Clear( name ); ReloadInstance(); },
			() => { Design?.SetNode( name, after ); ReloadInstance(); } );
	}

	//Forget a design value, so the instance keeps its own default; undo brings it back.
	internal void ClearDesignValue( string name )
	{
		if ( Design is not { } design || !design.Has( name ) ) return;

		var before = design.Get( name )?.DeepClone();

		design.Clear( name );

		Record( $"reset {name}",
			() => { Design?.SetNode( name, before ); ReloadInstance(); },
			() => { Design?.Clear( name ); ReloadInstance(); } );
	}

	static void Restore( string path, string text )
	{
		try
		{
			File.WriteAllText( path, text );
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"UI editor: could not restore {path}: {e.Message}" );
		}
	}

	//A stylesheet changed under us; the docks reading it read again.
	void OnSheetWritten( string path )
	{
		_canvas?.NoteSheetWritten( path );
		_inspector?.NoteSheetWritten( path );
		_animation?.NoteSheetWritten();
	}

	//Files are written as each edit is made. This pushes the open sheet into every
	//loaded copy - the scene's among them - in case a watcher missed the write.
	[Shortcut( "editor.save", "CTRL+S", ShortcutType.Window )]
	void Save()
	{
		if ( _document is null ) return;

		OnSheetWritten( _document.ScssPath );
	}

	internal IEnumerable<string> HistoryTitles() => _undo.Back.Select( x => x.Name );

	internal bool CanUndo => _undo.Back.Count > 0;
	internal bool CanRedo => _undo.Forward.Count > 0;

	[Shortcut( "editor.undo", "CTRL+Z", ShortcutType.Window )]
	internal bool Undo()
	{
		if ( _gesture is not null || !_undo.Undo() ) return false;

		OnSheetWritten( null );
		return true;
	}

	[Shortcut( "editor.redo", "CTRL+Y", ShortcutType.Window )]
	internal bool Redo()
	{
		if ( _gesture is not null || !_undo.Redo() ) return false;

		OnSheetWritten( null );
		return true;
	}
}

//A panel's place in a tree as the child index at each step down from the root - what survives the tree being built again.
internal static class PanelPath
{
	public static IReadOnlyList<int> Of( Panel root, Panel panel )
	{
		if ( !root.IsValid() || !panel.IsValid() ) return null;

		var path = new List<int>();

		for ( var at = panel; at is not null && !ReferenceEquals( at, root ); at = at.Parent )
		{
			var parent = at.Parent;
			if ( parent is null ) return null;

			var index = IndexIn( parent, at );
			if ( index < 0 ) return null;

			path.Add( index );
		}

		path.Reverse();
		return path;
	}

	public static Panel Follow( Panel root, IReadOnlyList<int> path )
	{
		TryFollow( root, path, out var found );
		return found;
	}

	/// <summary>Follow a path down from the root; false, with the deepest panel reached, when a step has no such child.</summary>
	public static bool TryFollow( Panel root, IReadOnlyList<int> path, out Panel found )
	{
		found = root.IsValid() ? root : null;

		if ( found is null ) return false;
		if ( path is null ) return true;

		foreach ( var index in path )
		{
			var child = found.Children.ElementAtOrDefault( index );
			if ( child is null ) return false;

			found = child;
		}

		return true;
	}

	static int IndexIn( Panel parent, Panel child )
	{
		var i = 0;

		foreach ( var candidate in parent.Children )
		{
			if ( ReferenceEquals( candidate, child ) ) return i;
			i++;
		}

		return -1;
	}
}
