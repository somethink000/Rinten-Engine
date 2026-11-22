using System;
using System.IO;
using System.Text.Json.Serialization;

namespace Editor;

/// <summary>Dialog for creating models from mesh files (.fbx, .obj, .dmx): collision type and output path</summary>
public class CreateModelFromMeshDialog : Dialog
{
	public enum CollisionMode
	{
		[Icon( "change_history" ), Title( "Convex Hull" )]
		Hull,
		[Icon( "grid_on" ), Title( "Exact Mesh" )]
		Mesh,
		[Icon( "block" )]
		None,
	}

	/// <summary>Import options, remembered between uses. Add a property here to add a setting to the dialog.</summary>
	public class ImportOptions
	{
		[Property, Title( "Collision" )]
		public CollisionMode Collision { get; set; } = CollisionMode.Hull;

		//No shader to pick, where theirs had one: a material made from pictures is the standard surface here, and the suffixes are the ones it reads - see MaterialGenerator.
		/// <summary>Try generating materials for each material slot in the model. Textures must be named after the name of a material slot, otherwise it will be left empty and material won't be generated. Texture name suffix must say which map it is (_color, _normal, _rough, etc..)</summary>
		[Property, Title( "Try Generating Materials" )]
		public bool GenerateMaterials { get; set; } = false;

		//Off unless asked for. Theirs was on by default because their model documents always laid a default material over everything; a model here wears the art's own materials and colours unless it names one - see ModelDefinition.Material.
		/// <summary>Put one material over every surface of the model, instead of the art's own.</summary>
		[Property, Title( "Globally Override Materials" )]
		public bool GlobalMaterialOverride { get; set; } = false;

		/// <summary>Material to override every material on the model with. Leave empty to use the plain white material.</summary>
		[Property, Title( "Override Material" ), ResourceType( "mat" )]
		public string GlobalMaterial { get; set; }
	}

	//The override, where one was asked for: the chosen material, or the plain one where none was.
	internal static void ApplyMaterialOverride( ImportOptions options, ModelDefinition definition )
	{
		//generated materials are laid on by name, which one material over everything would beat
		if ( options.GenerateMaterials )
			return;

		if ( !options.GlobalMaterialOverride )
			return;

		definition.Material = string.IsNullOrWhiteSpace( options.GlobalMaterial )
			? ModelDefinition.PlainMaterial
			: options.GlobalMaterial;
	}

	//The suffix map to generate materials with, plus cached discovered texture sets
	internal sealed class MaterialGenerationPlan
	{
		public Dictionary<string, MaterialGenerator.Slot> SuffixMap { get; init; }

		readonly Dictionary<string, List<MaterialGenerator.TextureSet>> _setsByRoot = new( StringComparer.OrdinalIgnoreCase );

		//returns null if material generation is disabled
		public static MaterialGenerationPlan Create( ImportOptions options )
		{
			if ( !options.GenerateMaterials )
				return null;

			return new MaterialGenerationPlan { SuffixMap = MaterialGenerator.GetSuffixMap() };
		}

		public List<MaterialGenerator.TextureSet> SetsFor( string searchRoot )
		{
			if ( _setsByRoot.TryGetValue( searchRoot, out var sets ) )
				return sets;

			sets = MaterialGenerator.DiscoverTextureSets( searchRoot, SuffixMap.Keys );
			_setsByRoot[searchRoot] = sets;
			return sets;
		}
	}

	//Generate a material for each of the model's material slots that has matching textures, and remap
	//the slot to it. Slots with no match are left to the art's own, user should fix them themselves
	internal static void ApplyGeneratedMaterials( MaterialGenerationPlan plan, ModelDefinition definition, Asset mesh, string outputPath )
	{
		if ( plan is null )
			return;

		var slots = ModelImportCatalog.MaterialNames( mesh.AbsolutePath );

		if ( slots is null || slots.Count == 0 )
			return;

		//explicit remaps and a global override are mutually exclusive
		definition.Material = "";

		var sets = plan.SetsFor( Path.GetDirectoryName( outputPath ) );

		foreach ( var slot in slots.Where( x => !string.IsNullOrWhiteSpace( x ) ).Distinct( StringComparer.OrdinalIgnoreCase ) )
		{
			if ( definition.MaterialRemaps.Any( x => string.Equals( x?.Surface, slot, StringComparison.OrdinalIgnoreCase ) ) )
				continue;

			var set = MaterialGenerator.FindSetForSlot( sets, slot );
			if ( set is null )
				continue;

			var materialPath = MaterialGenerator.GetOrCreateMaterial( set, plan.SuffixMap );
			if ( materialPath is null )
				continue;

			definition.MaterialRemaps.Add( new ModelDefinition.SurfaceMaterial { Surface = slot, Material = materialPath } );
		}
	}

	const string OptionsCookie = "CreateModelFromMeshDialog.ImportOptions";

	//Where the window remembers how big it was and where.
	const string WindowCookie = "CreateModelFromMesh";

	readonly List<Asset> _meshFiles;
	readonly ImportOptions _options;
	readonly LineEdit _fileEdit;
	readonly FolderEdit _folderEdit;

	//The animations each export holds, listed once when the dialog opens.
	readonly Dictionary<Asset, List<ModelTakes.Take>> _takes = new();

	//Whether they go on the model as clips - only there when some export has any.
	readonly Checkbox _animations;

	public CreateModelFromMeshDialog( List<Asset> meshFiles )
	{
		_meshFiles = meshFiles;

		Window.WindowTitle = meshFiles.Count == 1
			? $"Create Model from {Path.GetFileName( meshFiles[0].AbsolutePath )}"
			: $"Create {meshFiles.Count} Models from Mesh Files";
		Window.SetWindowIcon( "view_in_ar" );
		Window.MinimumWidth = 420;

		Layout = Layout.Column();
		Layout.Margin = 16;
		Layout.Spacing = 8;

		if ( meshFiles.Count <= 6 )
		{
			foreach ( var mesh in meshFiles )
			{
				var fileName = Path.GetFileName( mesh.AbsolutePath );
				Layout.Add( new Label( $"  📄 {fileName}" ) { Color = Theme.TextControl.WithAlpha( 0.7f ) } );
			}
		}
		else
		{
			Layout.Add( new Label( $"{meshFiles.Count} mesh files selected" ) { Color = Theme.TextControl.WithAlpha( 0.7f ) } );
		}

		Layout.AddSpacingCell( 4 );

		_options = EditorCookie.Get( OptionsCookie, new ImportOptions() );

		var serializedOptions = _options.GetSerialized();
		var optionRows = 0;
		var conditionalRows = new List<(Widget Row, Func<bool> IsEnabled)>();

		foreach ( var prop in serializedOptions )
		{
			var row = AddRow( prop.DisplayName, ControlWidget.Create( prop ) );
			optionRows++;

			switch ( prop.Name )
			{
				case nameof( ImportOptions.GlobalMaterialOverride ):
					conditionalRows.Add( (row, () => !_options.GenerateMaterials) );
					break;

				case nameof( ImportOptions.GlobalMaterial ):
					conditionalRows.Add( (row, () => !_options.GenerateMaterials && _options.GlobalMaterialOverride) );
					break;
			}
		}

		//Greyed rather than hidden where they don't apply: a hidden widget still takes its share of the column on this backend - see the Save To row below.
		void SyncConditionalRows()
		{
			foreach ( var (row, isEnabled) in conditionalRows )
				row.Enabled = isEnabled();
		}

		serializedOptions.OnPropertyChanged += _ => SyncConditionalRows();
		SyncConditionalRows();

		//Import animations as clips by default; not remembered between uses, hidden when there are none
		foreach ( var mesh in meshFiles )
			_takes[mesh] = ModelTakes.In( mesh.AbsolutePath );

		var animationCount = _takes.Values.Sum( x => x.Count );

		if ( animationCount > 0 )
		{
			_animations = new Checkbox( this )
			{
				Text = animationCount == 1 ? "Add its animation as a clip" : $"Add its {animationCount} animations as clips",
				Value = true,
			};

			AddRow( "Animations", _animations );
		}

		var defaultDir = Path.GetDirectoryName( meshFiles[0].AbsolutePath );
		var defaultFile = Path.ChangeExtension( meshFiles[0].AbsolutePath, ".mdl" );

		//One row, the one this is: both were built and one hidden, and a hidden widget still asks for its share
		//of the column on this backend - so the dialog carried a blank gap where the other one would have been.
		if ( meshFiles.Count == 1 )
		{
			_fileEdit = new LineEdit( this );
			_fileEdit.Text = defaultFile;
			_fileEdit.AddOptionToEnd( new Option( "Browse", "folder", BrowseFile ) );

			AddRow( "Save To", new PathStepper( this, _fileEdit, keepsFileName: true ) );
		}
		else
		{
			_folderEdit = new FolderEdit( this );
			_folderEdit.Text = defaultDir;

			AddRow( "Save To", new PathStepper( this, _folderEdit, keepsFileName: false ) );
		}

		var footer = Layout.AddRow();
		footer.Margin = new Rinten.UI.Margin( 0, 8, 0, 0 );
		footer.AddStretchCell();

		var cancelButton = new Button( "Cancel", "close" );
		cancelButton.Clicked = Close;
		footer.Add( cancelButton );

		footer.AddSpacingCell( 8 );

		var createButton = new Button.Primary( "Create", "add" );
		createButton.Clicked = OnCreate;
		footer.Add( createButton );

		//Explicit size, since AdjustSize reads a size hint nothing fills on this backend
		var rows = System.Math.Min( meshFiles.Count, 7 ) + 3 + optionRows + ( _animations is null ? 0 : 1 );

		Window.Size = new Vector2( 480, 96 + ( rows * ( Theme.RowHeight + 8 ) ) );

		//How big it was and where, kept by the window itself rather than by a cookie of our own - the same one every
		//other window in the editor uses, which also puts it back on the screen when the monitor it was last on has gone.
		Window.StateCookie = WindowCookie;

		Show();
	}

	//Path box with up/down arrows to leave a folder or step back into the one just left
	private sealed class PathStepper : Widget
	{
		private readonly LineEdit _path;
		private readonly bool _keepsFileName;
		private readonly IconButton _up;
		private readonly IconButton _down;

		//Folders stepped out of, newest last - what down walks back into.
		private readonly Stack<string> _left = new();

		public PathStepper( Widget parent, LineEdit path, bool keepsFileName ) : base( parent )
		{
			_path = path;
			_keepsFileName = keepsFileName;

			//Right-aligned so the end of a long path (folder and name) stays visible
			_path.Alignment = TextFlag.RightCenter;

			HorizontalSizeMode = SizeMode.Flexible;
			VerticalSizeMode = SizeMode.CanGrow;

			Layout = Layout.Column();
			Layout.Spacing = 2;

			_up = new IconButton( "arrow_upward" ) { ToolTip = "Out of this folder", FixedHeight = 20 };
			_up.MouseLeftPress = Up;

			_down = new IconButton( "arrow_downward" ) { ToolTip = "Back into the folder", FixedHeight = 20 };
			_down.MouseLeftPress = Down;

			Layout.Add( _up );
			Layout.Add( _path );
			Layout.Add( _down );

			Note();
		}

		//What is written, whole.
		private string Written
		{
			get => _path.Text ?? "";
			set
			{
				_path.Text = value;

				//At the end, which is where the box is looking and where anybody typing would carry on from.
				_path.CursorPosition = value?.Length ?? 0;
			}
		}

		//The folder of it, whether or not a file name is on the end.
		private string Folder => _keepsFileName
			? Path.GetDirectoryName( Written ) ?? ""
			: Written;

		private string Rebuilt( string folder ) => _keepsFileName
			? Path.Combine( folder, Path.GetFileName( Written ) )
			: folder;

		private void Up()
		{
			var folder = Folder;

			if ( Path.GetDirectoryName( folder ) is not { Length: > 0 } parent ) return;

			//Not out of the project. Above the assets folder is somebody else's disk, and a model written there is not in the project at all.
			if ( !Inside( parent ) ) return;

			_left.Push( Path.GetFileName( folder ) );

			Written = Rebuilt( parent );

			Note();
		}

		private void Down()
		{
			if ( _left.Count == 0 ) return;

			Written = Rebuilt( Path.Combine( Folder, _left.Pop() ) );

			Note();
		}

		//Whether a folder is still under the project's own content.
		private static bool Inside( string folder )
		{
			var root = Project.Current?.GetAssetsPath();

			if ( string.IsNullOrWhiteSpace( root ) ) return true;

			try
			{
				return Path.GetFullPath( folder )
					.StartsWith( Path.GetFullPath( root ), System.StringComparison.OrdinalIgnoreCase );
			}
			catch ( System.Exception )
			{
				return false;
			}
		}

		//An arrow with nowhere to go says so rather than doing nothing.
		private void Note()
		{
			_up.Enabled = Path.GetDirectoryName( Folder ) is { Length: > 0 } parent && Inside( parent );
			_down.Enabled = _left.Count > 0;
		}
	}

	Widget AddRow( string label, Widget control )
	{
		var row = new Widget( this );
		row.Layout = Layout.Row();
		row.Layout.Spacing = 8;
		row.Layout.Add( new Label( label ) { MinimumWidth = 90 } );
		row.Layout.Add( control, 1 );
		Layout.Add( row );
		return row;
	}

	void BrowseFile()
	{
		var result = EditorUtility.SaveFileDialog( "Save Model As..", "mdl", _fileEdit.Text );
		if ( result is not null )
			_fileEdit.Text = result;
	}

	void OnCreate()
	{
		EditorCookie.Set( OptionsCookie, _options );

		Close();

		//shared across every model in this batch so the asset scan and any materials we create is reused
		var plan = MaterialGenerationPlan.Create( _options );

		if ( _meshFiles.Count == 1 )
		{
			var outputPath = _fileEdit.Text;
			if ( string.IsNullOrWhiteSpace( outputPath ) )
				return;

			CreateModel( _meshFiles[0], outputPath, plan );
		}
		else
		{
			var outputFolder = _folderEdit.Text;
			if ( string.IsNullOrWhiteSpace( outputFolder ) )
				return;

			foreach ( var mesh in _meshFiles )
			{
				var outputPath = Path.Combine( outputFolder, Path.ChangeExtension( Path.GetFileName( mesh.AbsolutePath ), ".mdl" ) );
				if ( File.Exists( outputPath ) )
				{
					Log.Warning( $"Skipping {Path.GetFileName( outputPath )} - already exists" );
					continue;
				}

				CreateModel( mesh, outputPath, plan );
			}
		}
	}

	//Writes the chosen collision: convex hull for props, exact triangles only for static objects
	void Collide( ModelDefinition definition )
	{
		switch ( _options.Collision )
		{
			case CollisionMode.Hull:
				definition.Hulls.Add( new ModelDefinition.RenderHull() );
				break;

			case CollisionMode.Mesh:
				definition.Hulls.Add( new ModelDefinition.RenderMesh() );
				break;
		}
	}

	void CreateModel( Asset mesh, string outputPath, MaterialGenerationPlan plan )
	{
		//Their model editor wrote the file and their compiler built it. Both are gone; what writes a model from a
		//mesh now is the importer, and this dialog's options - collision, the animations - are what it will be told.
		var made = EditorUtility.CreateModelFromMeshFile( mesh, outputPath, definition =>
		{
			Collide( definition );
			ApplyMaterialOverride( _options, definition );

			if ( GltfMaterials.Handles( mesh.AbsolutePath ) )
				GltfMaterials.Apply( definition, mesh, outputPath );

			ApplyGeneratedMaterials( plan, definition, mesh, outputPath );

			//Named the way the model editor names them when the same file is dropped on a mesh part there - see ModelTakes.
			if ( _animations?.Value == true && _takes.TryGetValue( mesh, out var takes ) )
				ModelTakes.AddClips( definition, mesh.RelativePath, takes );
		} );

		if ( made is null ) return;

		//Where somebody making one wants to end up: looking at it. The browser refreshes
		//itself - a file appearing is an asset changing, and it is listening for that.
		EditorUtility.InspectorObject = made;
	}
}
