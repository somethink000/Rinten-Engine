using Rinten.Engine;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rinten;

public partial class Project
{
	/// <summary>Current open project.</summary>
	public static Project Current { get; internal set; }

	internal static List<Project> All = new();
	internal static IEnumerable<Project> Libraries => All.Where( x => x.Config.Type == "library" );


	//Remove all local packages. Used by unit tests to reset state.
	internal static void Clear()
	{
		All = new();
		PackageManager.UnmountTagged( "local" );

		//Let go of it rather than make another: whatever compiles next makes one.
		ProjectCompiling.Current?.DropGroup();
	}

	internal static void RebuildCompilers()
	{
		ProjectCompiling.Current?.DropGroup();

		foreach ( var proj in All )
		{
			proj.ForgetCompilers();
			proj.UpdateCompiler();
		}
	}

	internal static void Remove( Project project )
	{
		project.Dispose();
		All.Remove( project );
	}

	//Check whether the group needs recompiling, and recompiles. Every frame, in every process. Where
	//nothing has made a compile group - a built game, whose code ships compiled - there is nothing to build.
	internal static void Tick()
	{
		if ( ProjectCompiling.Current is { HasGroup: true } compiling )
			compiling.BuildIfNeeded();
	}

	//Initializes all the base projects
	internal static async Task InitializeBuiltIn( bool syncPackageManager = true )
	{
		AddFromFileBuiltIn( $"addons/base/{Extension}" );

		if ( Application.IsEditor || Application.IsUnitTest )
		{
			AddFromFileBuiltIn( $"addons/tools/{Extension}" );
			AddFromFileBuiltIn( $"editor/ActionGraph/{Extension}" );
			AddFromFileBuiltIn( $"editor/MdlEditor/{Extension}" );
			AddFromFileBuiltIn( $"editor/FxEditor/{Extension}" );
			AddFromFileBuiltIn( $"editor/UiEditor/{Extension}" );
			AddFromFileBuiltIn( $"editor/MovieMaker/{Extension}" );
		}

		if ( syncPackageManager )
		{
			//Is PackageManager tools-only?
			await SyncWithPackageManager();
		}
	}

	//Takes all of the active projects and makes sure we're in sync with the package manager. Creates mock packages that
	//act like real ones. Removes packages that are no longer active. If nothing changed then this should do nothing.
	internal static Task SyncWithPackageManager()
	{
		return PackageManager.InstallProjects( All.Where( x => x.Active ).ToArray() );
	}

	private static readonly JsonSerializerOptions JsonWriteIndented = new() { WriteIndented = true };

	public class VSCodeExtensions
	{
		[JsonPropertyName( "recommendations" )]
		public string[] Recommendations { get; set; } = [];
	}

	class VSCodeSettings
	{
		[JsonPropertyName( "files.associations" )]
		public Dictionary<string, string> FilesAssociations { get; set; } = [];

		[JsonPropertyName( "slang.additionalSearchPaths" )]
		public string[] SlangIncludePaths { get; set; } = [];

		[JsonPropertyName( "slang.predefinedMacros" )]
		public string[] SlangDefines { get; set; } = [];

		[JsonPropertyName( "slang.workspaceFlavor" )]
		public string SlangWorkspaceFlavor { get; set; } = "vfx";
	}

	//Writes a .vscode workspace configuration
	internal static void WriteVsCodeWorkspace( Project project )
	{
		var projectPath = project.GetRootPath();

		var vscodePath = Path.Combine( projectPath, ".vscode" );

		Directory.CreateDirectory( vscodePath );

		//Recommend C# Dev Kit and Slang extensions
		var extensions = new VSCodeExtensions { Recommendations = ["ms-dotnettools.csdevkit", "shader-slang.slang-language-extension"] };
		File.WriteAllText( Path.Combine( vscodePath, "extensions.json" ), JsonSerializer.Serialize( extensions, JsonWriteIndented ) );

		//Associate file extensions (defaults to Unity ShaderLab) and set up Slang search paths
		var settings = new VSCodeSettings
		{
			FilesAssociations = new() { { "*.shader", "slang" }, { "*.hlsl", "slang" } }
		};

		var shaderSearchPaths = new List<string> { EngineFileSystem.Root.GetFullPath( "/core/shaders" ) };

		foreach ( var p in Project.All )
		{
			shaderSearchPaths.Add( Path.Combine( p.GetAssetsPath(), "shaders" ) );
		}

		settings.SlangIncludePaths = [.. shaderSearchPaths];
		settings.SlangWorkspaceFlavor = "vfx";

		File.WriteAllText( Path.Combine( vscodePath, "settings.json" ), JsonSerializer.Serialize( settings, JsonWriteIndented ) );
	}

	[Flags]
	internal enum ProjectLoadFlags
	{
		None = 0,

		/// <summary>Component of the engine, always automatically loaded and can't be unloaded</summary>
		BuiltIn = 1 << 0,
	}

	//Add a project that ships with the engine. These are named in code rather than discovered, so one that has been taken out of the build used to
	//throw out of startup - the exception named the assert, not the addon, and nothing ran afterwards. A missing built-in is now logged and skipped.
	internal static Project AddFromFileBuiltIn( string path )
	{
		try
		{
			return AddFromFile( path, flags: ProjectLoadFlags.BuiltIn );
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"Built-in project '{path}' didn't load, skipping it ({e.Message})" );
			return null;
		}
	}

	internal static Project AddFromFile( string path, bool active = true, ProjectLoadFlags flags = ProjectLoadFlags.None )
	{
		var cleanPath = System.IO.Path.GetFullPath( path );

		//A folder rather than a file: the project inside it. Named after the project where the creator wrote it, and
		//named nothing at all in the ones that ship with the engine - so it is looked for rather than spelled out.
		if ( System.IO.Directory.Exists( cleanPath ) )
		{
			cleanPath = System.IO.Directory.EnumerateFiles( cleanPath, ExtensionSearch ).FirstOrDefault()
				?? System.IO.Path.Combine( cleanPath, Extension );
		}
		else if ( !System.IO.File.Exists( cleanPath ) && !cleanPath.EndsWith( Extension ) )
		{
			//Neither a file nor a folder that is there: a folder that has not been made yet, which is what this used to assume of everything.
			cleanPath = System.IO.Path.Combine( cleanPath, Extension );
		}

		//An existing file is the project file whatever its extension, e.g. a project being imported

		//Don't add the same project twice
		if ( All.Where( a => a.ConfigFilePath == cleanPath ).FirstOrDefault() is Project lp )
			return lp;

		var project = new Project
		{
			ConfigFilePath = cleanPath,
			Active = active,
			IsBuiltIn = ( ( flags & ProjectLoadFlags.BuiltIn ) == ProjectLoadFlags.BuiltIn )
		};
		project.Load();

		//If it loaded broken, don't bother with it
		if ( project.Broken )
		{
			throw new System.Exception( $"Couldn't add project." );
		}

		//If the schema needs upgrading then upgrade it and save before the engine loads it, so it's up to date at that point.
		if ( project.Config.Upgrade() )
		{
			project.Save();
		}

		All.Add( project );

		return project;
	}

	internal static Project FindByIdent( string ident )
	{
		return All.FirstOrDefault( x => string.Equals( ident.Replace( "#local", "" ), x.Config.FullIdent, StringComparison.OrdinalIgnoreCase ) );
	}

	[ConCmd( "list_projects", ConVarFlags.Protected )]
	internal static void ListProjects()
	{
		Log.Info( $"Loaded projects:" );
		foreach ( var project in All.OrderBy( x => x.Config.Type ).ThenByDescending( x => x.Active ).ThenBy( x => x.Config.Title ) )
		{
			var sb = new StringBuilder();

			sb.Append( "\t- " );
			sb.Append( $"{project.Config.Type} " );
			sb.Append( $"{project.Config.FullIdent} " );
			sb.Append( $"({project.GetRootPath()}) " );

			if ( project.Active )
				sb.Append( "[ACTIVE] " );

			Log.Info( sb.ToString() );
		}
	}

	public static Project Load( string dir )
	{
		var cleanPath = System.IO.Path.GetFullPath( dir );

		var project = new Project { ConfigFilePath = cleanPath, Active = false };
		project.Load();

		//If it loaded broken, don't bother with it
		if ( project.Broken )
		{
			return null;
		}

		//If the schema needs upgrading then upgrade it and save before the engine loads it, so it's up to date at that point.
		if ( project.Config.Upgrade() )
		{
			project.Save();
		}

		return project;
	}
}

