using Editor.TreeBrowser;
using System.IO;

namespace Editor;

/// <summary>The New menu of a folder in the asset browser - asset list and tree alike - and what each entry makes.</summary>
public static class CreateAsset
{
	public struct Entry
	{
		public string Name { get; init; }
		public string Icon { get; init; }
		public Pixmap IconImage { get; init; }
		public string Category { get; init; }

		//Should these show at the top outside of category views?
		public bool Pinned { get; init; }

		public Action<DirectoryInfo> Action { get; init; }
		public string Default { get; init; }

		public void Execute( DirectoryInfo folder )
		{
			if ( Action is not null )
			{
				Action( folder );
				return;
			}

			if ( !string.IsNullOrEmpty( Default ) )
				CreateFromTemplate( Name, Default, folder, Icon, IconImage );
		}
	}

	/// <summary>Fills a New menu with what can be made in this folder: a folder, and the files its kind of location holds.</summary>
	public static void AddOptions( Menu parent, string folderPath )
	{
		var folder = new DirectoryInfo( folderPath );
		var type = new DiskLocation( folder ).Type;

		parent.AddOption( "Folder", "folder", () => AskName( "New Folder", "new folder", "folder", null, name =>
		{
			if ( Directory.Exists( Path.Combine( folder.FullName, name ) ) )
			{
				EditorModal.Notice( null, "New Folder", $"{name} already exists." );
				return;
			}

			folder.CreateSubdirectory( name );
			EditorEvent.Run( "assetsystem.newfolder" );
		} ) );

		parent.AddSeparator();

		var entries = new List<Entry>();

		if ( type is AssetBrowser.LocationType.Localization )
		{
			entries.Add( new Entry { Name = "Empty JSON File", Icon = "data_object", Default = "localization.json", Pinned = true } );
		}
		else if ( type is AssetBrowser.LocationType.Code )
		{
			entries.Add( new Entry { Name = "Empty C# File", Icon = "description", Default = "default.cs", Category = "Code", Pinned = true } );
			entries.Add( new Entry { Name = "Component", Icon = "sports_esports", Default = "component.cs", Category = "Code", Pinned = true } );
			entries.Add( new Entry { Name = "Panel Component", Icon = "desktop_windows", Default = "default.razor", Category = "Razor" } );
			entries.Add( new Entry { Name = "Style Sheet", Icon = "brush", Default = "default.scss", Category = "Razor" } );
		}
		else if ( type is AssetBrowser.LocationType.Assets )
		{
			//Materials and models come from the type library below; a level is a prefab here
			entries.Add( new Entry { Name = "Standard Material Shader", Icon = "brush", Default = "material.shader", Category = "Shader" } );
			entries.Add( new Entry { Name = "Unlit Shader", Icon = "brush", Default = "unlit.shader", Category = "Shader" } );
			entries.Add( new Entry { Name = "Compute Shader", Icon = "brush", Default = "compute.shader", Category = "Shader" } );

			entries.AddRange( EditorTypeLibrary.GetAttributes<AssetTypeAttribute>().Select( x => new Entry
			{
				Name = x.Name,
				Category = x.Category,
				IconImage = AssetType.FromType( x.TargetType )?.Icon64,
				Action = d => CreateGameResource( x, d ),
				Pinned = x.Extension is "sound" or "prefab" or "scene" or "mat" or "mdl"
			} ) );
		}

		foreach ( var entry in entries.Where( x => x.Pinned ).OrderBy( x => x.Name ) )
			Add( parent, entry, folder );

		parent.AddSeparator();

		var groups = entries
			.OrderBy( x => x.Name )
			.GroupBy( x => NormalizeCategory( x.Category ), StringComparer.OrdinalIgnoreCase )
			.Where( x => x.Key is not null )
			.OrderBy( x => x.Key, StringComparer.OrdinalIgnoreCase );

		foreach ( var group in groups )
		{
			var menu = parent;

			foreach ( var segment in group.Key.Split( '/' ) )
				menu = menu.FindOrCreateMenu( segment );

			foreach ( var entry in group )
				Add( menu, entry, folder );
		}
	}

	static void Add( Menu menu, Entry entry, DirectoryInfo folder )
	{
		if ( entry.IconImage != null ) menu.AddOptionWithImage( entry.Name, entry.IconImage, () => entry.Execute( folder ) );
		else menu.AddOption( entry.Name, entry.Icon, () => entry.Execute( folder ) );
	}

	//"A/B" as a path of submenus; blank segments dropped
	static string NormalizeCategory( string category )
	{
		if ( string.IsNullOrWhiteSpace( category ) ) return null;

		var segments = category.Split( '/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		return segments.Length == 0 ? null : string.Join( '/', segments );
	}

	//"new material", or "new material 2" where that is taken
	static string NewName( DirectoryInfo folder, string typeName, string extension )
	{
		var name = $"new {typeName.ToLower()}";

		for ( var i = 1; File.Exists( Path.Combine( folder.FullName, $"{name}{extension}" ) ); i++ )
			name = $"new {typeName.ToLower()} {i}";

		return name;
	}

	//The name for what is made, on the editor's modal; the icon comes along because the menu it was picked from has closed
	static void AskName( string title, string initial, string icon, Pixmap image, Action<string> named )
	{
		EditorModal.Prompt( null, title, null, name =>
		{
			if ( !string.IsNullOrWhiteSpace( name ) ) named( name.Trim() );
		}, okay: "Create", initial: initial, icon: icon ?? "note_add", image: image );
	}

	//Asks the file's name in this folder and makes it, unless something is there already; then shows it in the browser
	static void AskFile( string name, string extension, DirectoryInfo folder, string icon, Pixmap image, Func<string, Asset> make )
	{
		AskName( $"New {name}", NewName( folder, name, extension ), icon, image, chosen =>
		{
			if ( !chosen.EndsWith( extension, StringComparison.OrdinalIgnoreCase ) ) chosen += extension;

			var path = Path.Combine( folder.FullName, chosen );

			if ( File.Exists( path ) )
			{
				EditorModal.Notice( null, $"New {name}", $"{chosen} already exists." );
				return;
			}

			var asset = make( path );

			TreeAssetBrowser.RefreshOpen();
			TreeAssetBrowser.Reveal( asset );
		} );
	}

	static void CreateFromTemplate( string name, string defaultFile, DirectoryInfo folder, string icon, Pixmap image )
	{
		var source = FileSystem.Root.GetFullPath( $"/templates/{defaultFile}" );

		if ( !File.Exists( source ) )
		{
			Log.Error( $"Can't create asset! Missing template: {defaultFile}" );
			return;
		}

		AskFile( name, Path.GetExtension( defaultFile ), folder, icon, image, path =>
		{
			File.Copy( source, path );
			return AssetSystem.RegisterFile( path );
		} );
	}

	/// <summary>Asks a name and makes an empty resource of this type in the folder.</summary>
	public static void CreateGameResource( AssetTypeAttribute gameResource, DirectoryInfo folder )
	{
		var slash = gameResource.Name.LastIndexOf( '/' );
		var name = slash == -1 ? gameResource.Name : gameResource.Name[( slash + 1 )..];

		AskFile( name, $".{gameResource.Extension}", folder, null, AssetType.FromType( gameResource.TargetType )?.Icon64,
			path => AssetSystem.CreateResource( gameResource.Extension, path ) );
	}
}
