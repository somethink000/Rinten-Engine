using Editor.AssetPickers;
using Editor.TreeBrowser;
using Rinten.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using UILabel = Rinten.UI.Label;

namespace Editor.Materials;

/// <summary>One material map: picture, path, and the numbers of the feature it belongs to</summary>
[StyleSheet( "MaterialMapPanel.cs.scss" )]
public sealed class MaterialMapPanel : EditorPanel
{
	public MaterialMapPicture Frame { get; }

	public MaterialMapPanel( string title, SerializedProperty path, IReadOnlyList<SerializedProperty> beside,
		bool colour = false )
	{
		AddClass( "material-map" );

		var heading = AddChild<UILabel>( "title" );
		heading.Text = title;
		heading.Tokenize = false;

		var row = AddChild<Panel>( "row" );

		Frame = row.AddChild( new MaterialMapPicture( path ) { Hint = $"Drop {title} here" } );

		var side = row.AddChild<Panel>( "beside" );
		side.AddChild( new MaterialMapPathPanel( path ) );
		side.AddChild( new MaterialMapColourButton( path, colour ) );

		if ( beside.Count == 0 ) return;

		var sheet = side.AddChild( new Inspectors.ControlSheet() );

		foreach ( var property in beside )
			sheet.AddPropertyRow( property );
	}
}

/// <summary>A material's map slot, drawn as the picture itself.</summary>
public sealed class MaterialMapPicture : Panel, IEditorDropTarget
{
	//Named after the slot rather than left as "drop an image": four empty squares in a column all saying the same thing is four squares somebody has to read the headings of to tell apart.
	/// <summary>What the empty square says.</summary>
	public string Hint { get; set; } = "Drop an image here";

	readonly SerializedProperty _property;
	readonly Rinten.UI.Image _picture;
	readonly UILabel _glyph;
	readonly UILabel _caption;

	int _shownHash;
	Texture _texture;
	string _textureFor;

	public MaterialMapPicture( SerializedProperty property )
	{
		_property = property;

		AddClass( "map-frame" );
		Tooltip = "Drop an image here, or click to pick one.";

		_picture = AddChild<Rinten.UI.Image>( "picture" );

		_glyph = AddChild<UILabel>( "glyph material-icon" );
		_glyph.Tokenize = false;

		_caption = AddChild<UILabel>( "caption" );
		_caption.Tokenize = false;
	}

	string Written => _property.GetValue<string>( null );

	public override void Tick()
	{
		base.Tick();

		if ( !EditorDrag.Active && HasClass( "drop-target" ) )
			SetClass( "drop-target", false );

		var hash = HashCode.Combine( Written, Hint );

		if ( hash == _shownHash ) return;

		_shownHash = hash;
		Show( Written );
	}

	//Put on the square whatever the path says: nothing, a stand-in, a picture, or a gap where one was.
	void Show( string written )
	{
		Style.BackgroundColor = null;
		_picture.Texture = null;

		SetClass( "empty", false );
		SetClass( "builtin", false );
		SetClass( "image", false );
		SetClass( "missing", false );
		SetClass( "light", false );

		if ( string.IsNullOrWhiteSpace( written ) )
		{
			SetClass( "empty", true );
			_glyph.Text = "add_photo_alternate";
			_caption.Text = Hint;
			return;
		}

		//Built-in default map: draw its flat colour with a caption in contrasting ink, not "Missing"
		if ( MaterialDefinition.BuiltInMapColour( written ) is { } flat )
		{
			SetClass( "builtin", true );
			SetClass( "light", flat.Luminance > 0.5f );
			Style.BackgroundColor = flat;
			_glyph.Text = "";

			//A colour somebody chose says so; the stand-in a slot nobody filled in carries still says nobody chose it.
			_caption.Text = MaterialDefinition.IsPickedMapColour( written ) ? "Colour" : "Default";
			return;
		}

		if ( Thumbnail( written ) is { } thumb )
		{
			SetClass( "image", true );
			_picture.Texture = TextureOf( thumb, written );
			return;
		}

		//A path with nothing behind it. Said rather than left blank: an empty square reads as a slot
		//nobody filled in, and this one is filled in with something that is not there any more.
		SetClass( "missing", true );
		_glyph.Text = "broken_image";
		_caption.Text = "Missing";
	}

	//The picture as a texture, made once per path rather than once per frame.
	Texture TextureOf( Pixmap pixmap, string path )
	{
		if ( _texture is { IsValid: true } && _textureFor == path ) return _texture;

		_texture = pixmap.picture?.ToTexture( mips: false );
		_textureFor = path;

		return _texture;
	}

	//The picture at a path, whether or not anything compiled it. A map is usually a png sitting in the project rather than an
	//asset the browser knows, so the asset's own thumbnail is only the first place to look and the file itself is the second.
	static Pixmap Thumbnail( string path )
	{
		var asset = AssetSystem.FindByPath( path );

		if ( asset is not null && !asset.IsDeleted && asset.GetAssetThumb( true ) is { } thumb )
			return thumb;

		var full = asset?.GetSourceFile( true ) ?? FileSystem.Content?.GetFullPath( path );

		if ( string.IsNullOrWhiteSpace( full ) && Path.IsPathRooted( path ) )
			full = path;

		if ( string.IsNullOrWhiteSpace( full ) || !File.Exists( full ) )
			return null;

		try
		{
			return Pixmap.FromFile( full );
		}
		catch ( Exception )
		{
			return null;
		}
	}

	protected override void OnClick( MousePanelEvent e )
	{
		base.OnClick( e );

		if ( e.MouseButton != MouseButtons.Left ) return;

		OpenPicker();
		e.StopPropagation();
	}

	//What can be done to the picture, asked for on the picture. The same menu the path line
	//beside it has. The square is what somebody points at, so it is what somebody right clicks.
	protected override void OnRightClick( MousePanelEvent e )
	{
		base.OnRightClick( e );

		var written = Written;
		var built = MaterialDefinition.IsBuiltInMap( written );
		var asset = string.IsNullOrWhiteSpace( written ) || built ? null : AssetSystem.FindByPath( written );

		var menu = new ContextMenu();

		//No "Pick.." here: a left click on the square already opens the picker.
		menu.AddOption( "Copy Path", "file_copy", () => EditorUtility.Clipboard.Copy( written ) )
			.Enabled = !string.IsNullOrWhiteSpace( written ) && !built;

		menu.AddOption( "Find in Asset Browser", "search", () => TreeAssetBrowser.OpenTo( asset ) )
			.Enabled = asset is not null;

		menu.AddSeparator();

		//Emptying it puts the engine's own picture back rather than nothing - see
		//MaterialDefinition.ColorMap, where a blank slot stopped being a thing that can exist.
		menu.AddOption( "Clear", "backspace", () => Assign( "" ) ).Enabled = !built;

		menu.OpenAtCursor( this );
		e.StopPropagation();
	}

	//The extensions the square takes, without their dots.
	static string[] Extensions()
		=> MaterialDefinition.ImageFiles.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );

	//The asset types behind those extensions, so the picker only offers those.
	static List<AssetType> AllowedAssetTypes()
		=> Extensions().Select( AssetType.FromExtension ).Where( x => x is not null ).Distinct().ToList();

	void OpenPicker()
	{
		AssetPicker.CloseOpenPickers();

		var picker = new GenericPicker( Rinten.Internal.GlobalToolsNamespace.EditorWindow, AllowedAssetTypes(), new AssetPicker.PickerOptions() );
		picker.Title = $"Select {_property.DisplayName}";
		picker.OnAssetPicked = assets => AssignObject( assets?.FirstOrDefault() );
		picker.SetSelection( Written );
		picker.Show();
	}

	public bool AcceptEditorDrops => true;

	public DropAction OnEditorDragHover( Widget.DragEvent ev )
	{
		var accept = TryGetDrop( ev, out _ );
		SetClass( "drop-target", accept );
		return accept ? DropAction.Link : DropAction.Ignore;
	}

	public void OnEditorDragLeave() => SetClass( "drop-target", false );

	public void OnEditorDragDrop( Widget.DragEvent ev )
	{
		SetClass( "drop-target", false );

		if ( !TryGetDrop( ev, out var dropped ) ) return;

		AssignObject( dropped );
		ev.Action = DropAction.Link;
	}

	bool TryGetDrop( Widget.DragEvent ev, out object value )
	{
		value = ev.Data.Object switch
		{
			Asset asset => asset,
			Asset[] many when many.Length > 0 => many[0],
			_ => null
		};

		value ??= ev.Data.HasFileOrFolder ? ev.Data.FileOrFolder : null;
		value ??= string.IsNullOrWhiteSpace( ev.Data.Text ) ? null : ev.Data.Text;

		return value is not null && CanAssign( value );
	}

	static bool CanAssign( object value ) => value switch
	{
		Asset asset => CanAssign( asset.RelativePath ?? asset.AbsolutePath ),
		string path => CanAssign( path ),
		_ => false
	};

	static bool CanAssign( string path )
	{
		path = Normalize( path );

		if ( string.IsNullOrWhiteSpace( path ) ) return false;

		var extension = Path.GetExtension( path ).TrimStart( '.' );

		return extension.Length > 0 && Extensions().Contains( extension, StringComparer.OrdinalIgnoreCase );
	}

	void AssignObject( object value )
	{
		switch ( value )
		{
			case Asset asset:
				Assign( asset.RelativePath );
				break;

			case string path:
				Assign( Relative( path ) );
				break;
		}
	}

	void Assign( string path )
	{
		_property.Parent?.NoteStartEdit( _property );
		_property.SetValue( path );
		_property.Parent?.NoteFinishEdit( _property );
	}

	//One line, no quotes, and a file: URL turned back into a path.
	static string Normalize( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return null;

		path = path.Trim().Split( '\n' )[0].Trim().Trim( '"' );

		if ( !path.StartsWith( "file:", StringComparison.OrdinalIgnoreCase ) )
			return path;

		if ( Uri.TryCreate( path, UriKind.Absolute, out var uri ) && uri.IsFile )
			return uri.LocalPath;

		return path;
	}

	//A picked or dropped path as the project sees it, relative where it can be.
	static string Relative( string path )
	{
		path = Normalize( path );

		if ( string.IsNullOrWhiteSpace( path ) ) return path;

		if ( AssetSystem.FindByPath( path ) is { } found )
			return found.RelativePath;

		if ( Path.IsPathRooted( path ) && Project.Current is not null )
			path = Path.GetRelativePath( Project.Current.GetAssetsPath(), path );

		return path.Replace( '\\', '/' );
	}
}

//Map path as plain text beside the picture, folder dimmed and file name bright
sealed class MaterialMapPathPanel : Panel
{
	readonly SerializedProperty _property;
	readonly UILabel _folder;
	readonly UILabel _name;

	string _shown;

	public MaterialMapPathPanel( SerializedProperty property )
	{
		_property = property;

		AddClass( "map-path" );

		_folder = AddChild<UILabel>( "folder" );
		_folder.Tokenize = false;

		_name = AddChild<UILabel>( "name" );
		_name.Tokenize = false;
	}

	public override void Tick()
	{
		base.Tick();

		var written = _property.GetValue<string>( null ) ?? "";

		if ( written == _shown ) return;

		_shown = written;
		Show( written );
	}

	void Show( string written )
	{
		if ( string.IsNullOrWhiteSpace( written ) )
		{
			SetClass( "empty", true );
			_folder.Text = "No image";
			_name.Text = "";
			Tooltip = null;
			return;
		}

		SetClass( "empty", false );

		//A slot holding a flat colour has a path with no file behind it, and showing it as a
		//folder and a filename invites somebody to go looking for one. It is a colour: say that.
		if ( MaterialDefinition.IsPickedMapColour( written ) )
		{
			_folder.Text = "Flat colour";
			_name.Text = "";
			Tooltip = "This slot is one colour rather than an image";
			return;
		}

		var path = written.Replace( '\\', '/' );
		var cut = path.LastIndexOf( '/' );

		_folder.Text = cut < 0 ? "" : path[..( cut + 1 )];
		_name.Text = cut < 0 ? path : path[( cut + 1 )..];
		Tooltip = path;
	}

	protected override void OnRightClick( MousePanelEvent e )
	{
		base.OnRightClick( e );

		var written = _property.GetValue<string>( null );
		var asset = string.IsNullOrWhiteSpace( written ) ? null : AssetSystem.FindByPath( written );

		var menu = new ContextMenu();

		menu.AddOption( "Copy Path", "file_copy", () => EditorUtility.Clipboard.Copy( written ) )
			.Enabled = !string.IsNullOrWhiteSpace( written );

		menu.AddOption( "Find in Asset Browser", "search", () => TreeAssetBrowser.OpenTo( asset ) )
			.Enabled = asset is not null;

		menu.OpenAtCursor( this );
		e.StopPropagation();
	}
}


//Fills a map slot with a flat colour path (like CoreDevTextures) instead of a texture file
sealed class MaterialMapColourButton : Panel
{
	readonly SerializedProperty _property;
	readonly bool _colour;
	readonly Panel _swatch;
	readonly UILabel _label;

	string _shown;

	public MaterialMapColourButton( SerializedProperty property, bool colour )
	{
		_property = property;
		_colour = colour;

		AddClass( "map-colour" );

		_swatch = AddChild<Panel>( "swatch" );

		_label = AddChild<UILabel>( "text" );
		_label.Tokenize = false;

		Tooltip = "Fill this slot with one colour instead of an image";

		AddEventListener( "onclick", Pick );
	}

	public override void Tick()
	{
		base.Tick();

		var written = _property.GetValue<string>( null ) ?? "";

		if ( written == _shown ) return;

		_shown = written;

		//The swatch shows what the slot holds where that is a flat colour, and stands empty where it holds a picture.
		var flat = MaterialDefinition.BuiltInMapColour( written );
		var picked = MaterialDefinition.IsPickedMapColour( written );

		SetClass( "unset", flat is null );
		SetClass( "on", picked );

		_label.Text = picked ? "Colour - click to change" : "Use a colour instead";

		if ( flat is { } known ) _swatch.Style.BackgroundColor = known;
	}

	void Pick()
	{
		var current = MaterialDefinition.BuiltInMapColour( _property.GetValue<string>( null ) ?? "" )
			?? Color.White;

		//Bytes in the end - see CoreDevTextures.PathFor - so nothing past one;
		//the alpha is kept, since a colour map's alpha is what a cutout reads. A drag across the picker is one edit of the slot: the
		//material is written, and so compiled, when the mouse is let go rather than for every colour on the way - see ResourceDocument.
		Inspectors.ColorPickerPopup.Open( this, current,
			picked => _property.SetValue( MaterialDefinition.FlatMapPath( picked, _colour ) ),
			hasAlpha: true, hdr: false,
			started: () => _property.Parent?.NoteStartEdit( _property ),
			finished: () => _property.Parent?.NoteFinishEdit( _property ) );
	}
}
