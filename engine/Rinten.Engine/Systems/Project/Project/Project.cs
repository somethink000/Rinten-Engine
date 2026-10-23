using Rinten.Engine;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rinten;

/// <summary>Represents an on-disk project.</summary>
[Expose]
public sealed partial class Project
{
	//If this is a single asset project, this will be the asset object
	internal object ProjectSourceObject { get; set; }

	/// <summary>Absolute path to the .addon file</summary>
	[JsonPropertyName( "Path" )]
	public string ConfigFilePath { get; set; }

	/// <summary>Root directory of this project</summary>
	[JsonIgnore]
	public DirectoryInfo RootDirectory
	{
		get => rootDirectory;
		set
		{
			rootDirectory = value;
			isBuilt = null;
		}
	}

	private DirectoryInfo rootDirectory;

	/// <summary>True if this project is active</summary>
	public bool Active { get; set; }

	/// <summary>True if this project is pinned, we'll prioritise it when sorting</summary>
	public bool Pinned { get; set; }

	/// <summary>When did the user last open this project?</summary>
	public DateTimeOffset LastOpened { get; set; }

	/// <summary>True if this project failed to load properly for some reason</summary>
	[JsonIgnore]
	public bool Broken { get; set; }

	/// <summary>Configuration of the project.</summary>
	[JsonIgnore]
	public DataModel.ProjectConfig Config { get; set; }

	/// <summary>A temporary project, usually created to inspect a single asset.</summary>
	[JsonIgnore]
	public bool IsTransient { get; internal set; }

	/// <summary>Shipped with the engine rather than created by the user.</summary>
	[JsonIgnore]
	public bool IsBuiltIn { get; internal set; }

	//Called when the project is about to save
	internal Action OnSaveProject { get; set; }

	//A filesystem into which compiled assemblies are written
	[JsonIgnore]
	internal MemoryFileSystem AssemblyFileSystem { get; }

	/// <summary>Project file suffix: <c>.rinproj</c>.</summary>
	public const string Extension = ".rinproj";

	/// <summary>Everything that ends that way, for a directory search.</summary>
	public const string ExtensionSearch = "*" + Extension;

	/// <summary>Engine-owned folder inside a project: <c>.rinten</c>.</summary>
	public const string CacheFolder = ".rinten";

	/// <summary>Where downloaded packages land, as a path reads inside an asset's own path.</summary>
	public const string CloudPath = "/" + CacheFolder + "/cloud/";

	/// <summary>Where assets made at runtime land, the same way.</summary>
	public const string TransientPath = "/" + CacheFolder + "/transient/";

	public Project()
	{
		AssemblyFileSystem = new MemoryFileSystem();
	}

	internal void Dispose()
	{
		ForgetCompilers();

		AssemblyFileSystem?.Dispose();
	}

	internal bool LoadMinimal()
	{
		if ( IsTransient )
			return false;

		try
		{
			RootDirectory = new DirectoryInfo( System.IO.Path.GetDirectoryName( ConfigFilePath ) );
			Assert.True( RootDirectory.Exists, $"{RootDirectory} does not exist" );

			if ( !File.Exists( ConfigFilePath ) && !ConfigFilePath.EndsWith( Extension ) )
			{
				ConfigFilePath = System.IO.Path.Combine( RootDirectory.FullName, Extension );
			}

			var text = File.ReadAllText( ConfigFilePath );
			Config = JsonSerializer.Deserialize<DataModel.ProjectConfig>( text, EnumConvert.Plain );
			Config.Init( ConfigFilePath );

			UpdateMockPackage();
			return true;
		}
		catch ( System.Exception e )
		{
			Log.Warning( e, $"Project config error ({e.Message}) - deactivating project" );
			Broken = true;
			Active = false;
			return false;
		}
	}

	internal void Load()
	{
		if ( !LoadMinimal() )
			return;

		try
		{
			UpdateCompiler();
		}
		catch ( System.Exception e )
		{
			Log.Warning( e, $"Project config error ({e.Message}) - deactivating project" );
			Broken = true;
			Active = false;
		}
	}

	/// <summary>Absolute path to the location of the <c>.rinproj</c> file of the project.</summary>
	public string GetRootPath() => RootDirectory.FullName;

	/// <summary>Gets the .rinproj file for this project</summary>
	public string GetProjectPath() => System.IO.Directory.EnumerateFiles( GetRootPath(), ExtensionSearch ).FirstOrDefault();

	/// <summary>Absolute path to the Code folder of the project.</summary>
	public string GetCodePath() => DirectoryNamed( "Code" );

	//A folder of the project by name, matching case-insensitively on disk.
	private string DirectoryNamed( string wanted )
	{
		var root = RootDirectory?.FullName;

		if ( string.IsNullOrEmpty( root ) ) return null;

		var asked = System.IO.Path.Combine( root, wanted );

		if ( System.IO.Directory.Exists( asked ) ) return asked;
		if ( !System.IO.Directory.Exists( root ) ) return asked;

		foreach ( var directory in System.IO.Directory.EnumerateDirectories( root ) )
		{
			if ( string.Equals( System.IO.Path.GetFileName( directory ), wanted, StringComparison.OrdinalIgnoreCase ) )
				return directory;
		}

		return asked;
	}

	/// <summary>Returns true if the Code path exists</summary>
	public bool HasCodePath() => RootDirectory is not null && System.IO.Directory.Exists( GetCodePath() );

	/// <summary>Absolute path to the Editor folder of the project.</summary>
	public string GetEditorPath() => DirectoryNamed( "Editor" );

	/// <summary>Returns true if the Editor path exists</summary>
	public bool HasEditorPath() => RootDirectory is not null && System.IO.Directory.Exists( GetEditorPath() );

	/// <summary>Absolute path to the project's content: Assets, or the root itself for a built game</summary>
	public string GetAssetsPath() => IsBuilt ? RootDirectory.FullName : System.IO.Path.Combine( RootDirectory.FullName, "Assets" );

	//Whether this project is a built game - see GetAssetsPath. Asked of the disk once a root, since the asset browsers ask for the path a lot.
	private bool IsBuilt => isBuilt ??= System.IO.File.Exists( System.IO.Path.Combine( RootDirectory.FullName, Standalone.ManifestName ) );

	private bool? isBuilt;

	/// <summary>Absolute path to the Localization folder of the project, or <see langword="null"/> if not set.</summary>
	public string GetLocalizationPath() => System.IO.Path.Combine( RootDirectory.FullName, "Localization" );

	/// <summary>Returns true if the Assets path exists</summary>
	public bool HasAssetsPath() => RootDirectory is not null && System.IO.Directory.Exists( GetAssetsPath() );

	internal void Save()
	{
		OnSaveProject?.Invoke();

		if ( Config == null )
			return;

		if ( IsTransient )
			return;

		if ( !ConfigFilePath.EndsWith( Extension ) ) return;

		var json = Config.ToJson();

		//Check if we need to do this first..
		try
		{
			if ( File.Exists( ConfigFilePath ) )
			{
				var existingContents = File.ReadAllText( ConfigFilePath );
				if ( json == existingContents ) return;
			}
		}
		catch ( System.Exception ) { }

		File.WriteAllText( ConfigFilePath, json );

		//update the package with new details
		UpdateMockPackage();
		UpdateCompiler();

		if ( Config.Type == "game" )
		{
			IGameInstanceDll.Current?.OnProjectConfigChanged( mockPackage );
		}
	}

	LocalPackage mockPackage;

	/// <summary>The package for this project. This is a mock up of the actual package.</summary>
	[JsonIgnore]
	public Package Package => UpdateMockPackage();

	LocalPackage UpdateMockPackage()
	{
		mockPackage ??= new LocalPackage( this );
		mockPackage.TypeName = Config.Type;
		mockPackage.Ident = Config.Ident;
		mockPackage.Title = Config.Title;
		mockPackage.PackageReferences = Config.PackageReferences?.ToArray() ?? Array.Empty<string>();
		mockPackage.EditorReferences = Config.EditorReferences?.ToArray() ?? Array.Empty<string>();

		mockPackage.Org = new Package.Organization
		{
			Ident = Config.Org,
			Title = Config.Org
		};

		mockPackage.Tags = Array.Empty<string>();
		mockPackage.Thumb = GetThumbPath();

		return mockPackage;
	}
}
