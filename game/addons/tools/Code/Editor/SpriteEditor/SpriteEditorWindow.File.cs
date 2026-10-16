namespace Editor.SpriteEditor;

/// <summary>New, save, and the question asked before an edit is lost.</summary>
public sealed partial class SpriteEditorWindow
{
	[Shortcut( "editor.new", "CTRL+N", ShortcutType.Window )]
	private void New() => PromptSave( CreateNew );

	private void CreateNew()
	{
		var path = AskSavePath( "New Sprite" );
		if ( path is null ) return;

		_document.CreateNew( path );
	}

	private void OpenAsset( Asset asset )
	{
		if ( asset is null ) return;

		if ( asset == _document.Asset )
		{
			Focus();
			return;
		}

		_document.Open( asset );
	}

	[Shortcut( "editor.save", "CTRL+S", ShortcutType.Window )]
	private bool Save( bool saveAs = false )
	{
		if ( !saveAs && _document.Asset is not null )
		{
			_document.Save();
			UpdateTitle();
			return true;
		}

		var path = AskSavePath( "Save Sprite" );
		if ( path is null ) return false;

		_document.Save( AssetSystem.CreateResource( "sprite", path ) );
		UpdateTitle();

		return true;
	}

	[Shortcut( "editor.save-as", "CTRL+SHIFT+S", ShortcutType.Window )]
	private void SaveAs() => Save( saveAs: true );

	//Where to put a sprite, asked of the user. Null where they thought better of it.
	private static string AskSavePath( string title )
	{
		var dialog = new FileDialog( null )
		{
			Title = title,
			DefaultSuffix = ".sprite"
		};

		dialog.SelectFile( "untitled.sprite" );
		dialog.SetFindFile();
		dialog.SetModeSave();
		dialog.SetNameFilter( "Sprite (*.sprite)" );

		if ( !dialog.Execute() ) return null;

		return string.IsNullOrWhiteSpace( dialog.SelectedFile ) ? null : dialog.SelectedFile;
	}

	//Goes on at once where nothing is unsaved, and asks first where something is.
	private void PromptSave( Action then )
	{
		if ( !_document.IsModified )
		{
			then?.Invoke();
			return;
		}

		EditorModal.Choose( null, "Save Current Sprite", "The open sprite has unsaved changes. Would you like to save before continuing?", "Cancel",
		[
			new EditorModal.Choice( "Don't Save", () => then?.Invoke(), Destructive: true ),
			new EditorModal.Choice( "Save", () => { if ( Save() ) then?.Invoke(); } ),
		] );
	}

	private void UpdateTitle()
	{
		var star = _document.IsModified ? "*" : "";
		var name = _document.Asset?.Name ?? "untitled";

		Title = $"Sprite Editor - {name}{star}";
	}

	protected override bool OnClose()
	{
		EditorCookie.Set( TextureFilterLevels.Cookie, Antialiasing );

		if ( !_document.IsModified ) return true;

		EditorModal.Choose( null, "Save Current Sprite", "The open sprite has unsaved changes. Would you like to save now?", "Cancel",
		[
			new EditorModal.Choice( "Don't Save", () => { _document.RevertToSaved(); Close(); }, Destructive: true ),
			new EditorModal.Choice( "Save", () => { Save(); Close(); } ),
		] );

		return false;
	}

	[Shortcut( "editor.quit", "CTRL+Q" )]
	private void Quit() => Close();

	[Shortcut( "editor.undo", "CTRL+Z" )]
	private bool Undo() => _document.Undo();

	[Shortcut( "editor.redo", "CTRL+Y" )]
	private bool Redo() => _document.Redo();
}
