using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Editor;

partial class StandaloneExporter
{
	//Editor-only files in a platform's bin folder to leave out of games (a deny list, not an allow list)
	private static readonly string[] NotForGames = [
		//Finding and opening Visual Studio: the editor's business.
		"vsopen.exe",
		"vswhere.exe",

		//The link library for our own core. Nothing loads a .lib at runtime; it is there to build against.
		"rintencore.lib",
		"rintencore.a",
		"librintencore.a",

		//SPIRV-Cross, which writes a web export's GLSL. A game compiles nothing.
		"spirv-cross.dll",
		"libspirv-cross.so",
		"libspirv-cross.dylib",
	];

	//Whether a file in a bin folder travels with a game.
	private static bool ShipsWithGames( string file )
	{
		var name = Path.GetFileName( file );

		if ( NotForGames.Contains( name, StringComparer.OrdinalIgnoreCase ) ) return false;

		//Symbols. Megabytes each, read by nothing at runtime, and a crash dump from a shipped game is
		//symbolised against the build rather than against whatever the player happens to have on disk.
		return !name.EndsWith( ".pdb", StringComparison.OrdinalIgnoreCase );
	}

	private IEnumerable<string> GetDllFiles( string engineDir, string program )
	{
		var platform = _exportConfig.Platform ?? ExportPlatform.Host;

		//Android will not open a library out of the folder these are unpacked into - what an apk loads is what
		//is in its own lib folder, put there when the package is built. See engine/Launcher/RintenAndroid.
		if ( platform.IsAndroid ) return GetManagedFiles( engineDir, null );

		var nativeDir = Path.Combine( engineDir, platform.BinFolder );

		if ( !Directory.Exists( nativeDir ) )
		{
			//Named rather than thrown: everything else about the export is platform-independent and will be written correctly, so what
			//comes out is a game missing its native half - which is worth saying exactly once, with the folder that was not there.
			Problem( $"no native libraries for {platform.Title} - {nativeDir} is not there. " +
				"Build the core for it first (engine/Core/build-*)." );

			return GetManagedFiles( engineDir, program );
		}

		var native = Directory.GetFiles( nativeDir ).Where( ShipsWithGames ).ToList();

		//Never ship the shader compiler; games use shaders baked at export
		if ( NativePackages( engineDir, platform ).TryGetValue( "slang", out var slang ) )
		{
			native.RemoveAll( file => slang.Contains( Path.GetFileName( file ), StringComparer.OrdinalIgnoreCase ) );
		}

		if ( MissingNatives( engineDir, platform ) is { Count: > 0 } missing )
		{
			Problem( $"{platform.Title} libraries missing from {platform.BinFolder}: {string.Join( ", ", missing )} - " +
				"the game will not start without them. Fetch them with: " +
				$"dotnet run --project engine/Tools/RintenBuild -- fetch-natives --platform {platform.Id}" );
		}

		return native.Concat( GetManagedFiles( engineDir, program ) );
	}

	//Third-party natives from engine/natives.json missing from the target's bin folder
	private static List<string> MissingNatives( string engineDir, ExportPlatform platform )
	{
		var missing = new List<string>();

		foreach ( var (_, files) in NativePackages( engineDir, platform ) )
		{
			foreach ( var name in files )
			{
				if ( !File.Exists( Path.Combine( engineDir, platform.BinFolder, name ) ) ) missing.Add( name );
			}
		}

		return missing;
	}

	//What engine/natives.json says this platform's third-party libraries are, by the package they
	//come from. Empty where the manifest is not there - an engine without its source beside it.
	private static Dictionary<string, List<string>> NativePackages( string engineDir, ExportPlatform platform )
	{
		var packages = new Dictionary<string, List<string>>( StringComparer.OrdinalIgnoreCase );
		var manifest = Path.GetFullPath( Path.Combine( engineDir, "..", "engine", "natives.json" ) );

		if ( !File.Exists( manifest ) ) return packages;

		try
		{
			var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
			using var json = JsonDocument.Parse( File.ReadAllText( manifest ), options );

			if ( !json.RootElement.TryGetProperty( platform.Id, out var named ) ) return packages;

			foreach ( var package in named.EnumerateArray() )
			{
				if ( !package.TryGetProperty( "files", out var files ) ) continue;

				//What the engine runs without - FFmpeg, say - is not a library a game is missing; whether it goes is that feature's own step to say.
				if ( package.TryGetProperty( "optional", out var optional ) && optional.ValueKind == JsonValueKind.True ) continue;

				var name = package.TryGetProperty( "name", out var said ) ? said.GetString() : null;
				var into = packages.TryGetValue( name ?? "", out var list ) ? list : packages[name ?? ""] = new List<string>();

				foreach ( var file in files.EnumerateObject() )
				{
					if ( file.Value.GetString() is { Length: > 0 } carried ) into.Add( carried );
				}
			}
		}
		catch ( Exception e ) when ( e is IOException or JsonException )
		{
			//A manifest that will not read says nothing about the folder.
		}

		return packages;
	}

	//Managed assemblies reachable from the program, minus editor-only ones; all of them for Android
	private IEnumerable<string> GetManagedFiles( string engineDir, string program )
	{
		var folder = Path.Combine( engineDir, "bin", "managed" );
		var all = GetBlacklistedFiles( [], folder )
			.Where( x => x.EndsWith( ".dll", StringComparison.OrdinalIgnoreCase ) )
			.ToList();

		if ( program is null || !File.Exists( program ) ) return all;

		var pending = new Queue<byte[]>();
		pending.Enqueue( File.ReadAllBytes( program ) );

		if ( _exportConfig.AssemblyFiles is not null )
		{
			foreach ( var file in _exportConfig.AssemblyFiles )
			{
				if ( file.Key.EndsWith( ".dll", StringComparison.OrdinalIgnoreCase ) && file.Value is byte[] bytes )
					pending.Enqueue( bytes );
			}
		}

		var reached = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		while ( pending.Count > 0 )
		{
			foreach ( var name in ReferencesOf( pending.Dequeue() ) )
			{
				if ( Rinten.Diagnostics.ShippedBuild.IsEditorOnly( name ) ) continue;

				var file = Path.Combine( folder, name + ".dll" );
				if ( !File.Exists( file ) || !reached.Add( name ) ) continue;

				pending.Enqueue( File.ReadAllBytes( file ) );
			}
		}

		return all.Where( file => reached.Contains( Path.GetFileNameWithoutExtension( file ) ) );
	}

	//Swaps in the AOT-compiled assembly at copy time if not older; the destination path is unchanged
	internal string AheadOfTime( string file )
	{
		if ( publishedManaged is null ) return file;

		var compiled = Path.Combine( publishedManaged, Path.GetFileName( file ) );

		try
		{
			if ( !File.Exists( compiled ) ) return file;
			if ( File.GetLastWriteTimeUtc( compiled ) < File.GetLastWriteTimeUtc( file ) ) return file;
		}
		catch ( IOException )
		{
			return file;
		}

		return compiled;
	}

	//The simple names of the assemblies an assembly references.
	private static List<string> ReferencesOf( byte[] image )
	{
		var names = new List<string>();

		using var reader = new PEReader( new MemoryStream( image ) );
		if ( !reader.HasMetadata ) return names;

		var metadata = reader.GetMetadataReader();

		foreach ( var handle in metadata.AssemblyReferences )
			names.Add( metadata.GetString( metadata.GetAssemblyReference( handle ).Name ) );

		return names;
	}

	//Core files a game needs, by pattern under core/: precompiled shaders and config
	private static readonly string[] CoreWhitelist = [
		//The engine's own shaders, compiled ahead of time. Without this a game compiles them on its first frame
		//- which costs a second on a desktop and cannot happen at all where there is no compiler to do it with.
		"shaders/builtin.shadercache",

		//The one config file a game reads (see RenderQualityProfiles)
		"cfg/quality_profiles.json",
	];

	//What the editor keeps beside an asset that a game has no use for. A panel's .razor.design.json is what the UI editor's canvas
	//lays out with - the only thing in the engine that reads one - and it travels with the panel into every build otherwise.
	private static readonly string[] EditorOnlyContent = [ ".design.json" ];

	//Whether a content file belongs in a game at all.
	internal static bool ForGames( string relativePath )
	{
		if ( string.IsNullOrEmpty( relativePath ) ) return true;

		foreach ( var ending in EditorOnlyContent )
		{
			if ( relativePath.EndsWith( ending, StringComparison.OrdinalIgnoreCase ) ) return false;
		}

		return true;
	}

	private IEnumerable<string> GetCoreFiles( string engineDir )
	{
		return GetWhitelistedFiles( CoreWhitelist, Path.Combine( engineDir, "core" ) );
	}

	//Whether the project can be played in a headset at all - VR among its control modes.
	private bool PlaysInHeadset()
		=> _exportConfig.Project.Config.TryGetMeta( "ControlModes", out ControlModeSettings modes ) && modes.VR;

	//Where the base addon's content is, under the engine.
	private static string BaseAssetsFolder( string engineDir ) => Path.Combine( engineDir, "addons", "base", "Assets" );

	//Folders of the base addon's content that are only for the editor: what it offers when something new is made - a GameObject, a map.
	private static readonly string[] BaseEditorOnly = [ "templates" ];

	//Folders of the base addon's content only a game played in a headset has any use for: the controllers a hand
	//is drawn holding - see XrControllerModels. A few megabytes, and a page downloads everything a build ships.
	private static readonly string[] BaseVrOnly = [ "models/vr" ];

	//The engine's splash picture under the base content - see EngineSplash
	private const string EngineSplashPicture = "splash/splash.png";

	//The game's own startup picture on disk, or null where the export shows the engine's or none
	private string OwnSplash()
	{
		if ( _exportConfig.LoadingScreen != LoadingScreenKind.Own || string.IsNullOrWhiteSpace( _exportConfig.StartupImage ) ) return null;

		var path = Path.IsPathRooted( _exportConfig.StartupImage )
			? _exportConfig.StartupImage
			: Path.Combine( _exportConfig.Project.GetAssetsPath(), _exportConfig.StartupImage );

		if ( File.Exists( path ) ) return path;

		Logger.Warning( $"the startup picture {path} is not there - the build starts on black" );
		return null;
	}

	//Where the build's startup picture sits under the base content, or null for none; beside the engine's, which is mounted from the first frame
	private string SplashInBuild()
	{
		if ( _exportConfig.LoadingScreen == LoadingScreenKind.Engine ) return EngineSplashPicture;

		return OwnSplash() is { } own ? "splash/own" + Path.GetExtension( own ).ToLowerInvariant() : null;
	}

	//All base addon content except the editor's, since games reference it by path
	private IEnumerable<string> BaseContent( string engineDir )
	{
		var root = BaseAssetsFolder( engineDir );

		if ( !Directory.Exists( root ) ) yield break;

		var headset = PlaysInHeadset();
		var unnamed = unnamedBase ??= UnnamedBaseFiles( root );

		foreach ( var file in Directory.EnumerateFiles( root, "*", SearchOption.AllDirectories ) )
		{
			var relative = Path.GetRelativePath( root, file ).Replace( '\\', '/' );

			if ( BaseEditorOnly.Any( x => relative.StartsWith( x + "/", StringComparison.OrdinalIgnoreCase ) ) ) continue;
			if ( !headset && BaseVrOnly.Any( x => relative.StartsWith( x + "/", StringComparison.OrdinalIgnoreCase ) ) ) continue;
			if ( _exportConfig.LoadingScreen != LoadingScreenKind.Engine && relative.Equals( EngineSplashPicture, StringComparison.OrdinalIgnoreCase ) ) continue;
			if ( unnamed.Contains( Path.GetFullPath( file ) ) ) continue;

			//Dot folders and files are the editor's own bookkeeping - caches, thumbnails.
			if ( relative.Split( '/' ).Any( x => x.StartsWith( '.' ) ) ) continue;

			//What a picture compiles to rather than content: which of those a build carries is the pictures' to say - see CompilePictures.
			if ( CompiledPaths.IsTextureBlocks( relative ) ) continue;

			if ( IsMeshSource( relative ) && CompiledBeside( file ) ) continue;

			yield return file;
		}
	}

	//What a model is compiled from, which a build has no use for once the compiled model ships beside it.
	private static bool IsMeshSource( string relative )
		=> relative.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) || relative.EndsWith( ".obj", StringComparison.OrdinalIgnoreCase );

	//Whether every model definition in the mesh file's folder is compiled; one that is not compiles from the file at load
	private static bool CompiledBeside( string meshSource )
	{
		var definitions = Directory.GetFiles( Path.GetDirectoryName( meshSource ), "*.mdl" );

		return definitions.Length > 0 && definitions.All( x => File.Exists( x + "_c" ) );
	}

	//Base files a game reaches only by naming them, once per export; full paths
	private HashSet<string> unnamedBase;

	//Base models the engine loads by path at runtime, named in a game or not
	private static readonly string[] EngineModels = [ "models/dev/error.mdl" ];

	//Families the engine draws with itself: its defaults, and the frame graph's and debug overlays' mono
	private static readonly string[] EngineFonts = [ "Roboto", "Inter", "Roboto Mono" ];

	//Files the game names whatever it is: a scene's models, a style's fonts
	private static readonly HashSet<string> NamingFiles = new( StringComparer.OrdinalIgnoreCase )
	{
		".cs", ".razor", ".scss", ".css", ".json", ".mdl", ".scene", ".prefab", ".mat",
	};

	//Base models and font faces nothing in the game or the base code names: the skeleton and its clips, Poppins
	private HashSet<string> UnnamedBaseFiles( string root )
	{
		var unnamed = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var named = NamesInGame( root );
		var kept = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		var models = Path.Combine( root, "models" );

		if ( Directory.Exists( models ) )
		{
			foreach ( var definition in Directory.EnumerateFiles( models, "*.mdl", SearchOption.AllDirectories ) )
			{
				var relative = Path.GetRelativePath( root, definition ).Replace( '\\', '/' );

				//The headset's own rule says whether those go - see BaseVrOnly
				if ( BaseVrOnly.Any( x => relative.StartsWith( x + "/", StringComparison.OrdinalIgnoreCase ) ) ) continue;

				var used = EngineModels.Contains( relative, StringComparer.OrdinalIgnoreCase )
					|| named.Contains( relative[..^".mdl".Length].ToLowerInvariant(), StringComparison.Ordinal );

				( used ? kept : unnamed ).UnionWith( ModelFiles( root, definition, relative ) );
			}
		}

		//A clip two models share stays with the one that ships
		unnamed.ExceptWith( kept );

		var fonts = Path.Combine( root, "fonts" );

		if ( Directory.Exists( fonts ) )
		{
			foreach ( var file in Directory.EnumerateFiles( fonts, "*", SearchOption.AllDirectories ) )
			{
				var extension = Path.GetExtension( file );
				if ( !extension.Equals( ".ttf", StringComparison.OrdinalIgnoreCase ) && !extension.Equals( ".otf", StringComparison.OrdinalIgnoreCase ) ) continue;

				if ( FontIndex.FamilyOf( file ) is not { Length: > 0 } family ) continue;
				if ( EngineFonts.Contains( family, StringComparer.OrdinalIgnoreCase ) ) continue;
				if ( named.Contains( family.ToLowerInvariant(), StringComparison.Ordinal ) ) continue;

				unnamed.Add( Path.GetFullPath( file ) );
			}
		}

		if ( unnamed.Count > 0 )
		{
			var bytes = unnamed.Sum( x => new FileInfo( x ).Length );
			Logger.Info( $"left out {unnamed.Count} base files nothing in the game names ({bytes / 1048576.0:F1} MB)" );
		}

		return unnamed;
	}

	//A base model's definition, what it compiles to, its graphs and its clips, as full paths
	private static IEnumerable<string> ModelFiles( string root, string definition, string relative )
	{
		yield return Path.GetFullPath( definition );
		yield return Path.GetFullPath( Path.Combine( root, CompiledPaths.Model( relative ) ) );

		//The graph, and any variation of it: thing.mdl_ag_c, thing+name.mdl_ag_c
		var stem = Path.GetFileNameWithoutExtension( definition );

		foreach ( var graph in Directory.EnumerateFiles( Path.GetDirectoryName( definition ), stem + "*.mdl_ag_c" ) )
		{
			var name = Path.GetFileName( graph );
			if ( name.StartsWith( stem + ".", StringComparison.OrdinalIgnoreCase ) || name.StartsWith( stem + "+", StringComparison.OrdinalIgnoreCase ) )
				yield return Path.GetFullPath( graph );
		}

		if ( Rinten.ModelDefinitionCompiler.ReadDefinitionOnly( definition ) is not { } read ) yield break;

		foreach ( var clip in Rinten.CoreSkeletons.ClipsOf( relative, read ).Values )
		{
			if ( string.IsNullOrWhiteSpace( clip ) || Path.IsPathRooted( clip ) ) continue;
			yield return Path.GetFullPath( Path.Combine( root, clip.TrimStart( '/' ) ) );
		}
	}

	//Everything the game and the base code could name a file in, as one lowercase text to search
	private string NamesInGame( string root )
	{
		var text = new System.Text.StringBuilder();

		void Read( string folder, Func<string, bool> skip = null )
		{
			if ( string.IsNullOrEmpty( folder ) || !Directory.Exists( folder ) ) return;

			foreach ( var file in Directory.EnumerateFiles( folder, "*", SearchOption.AllDirectories ) )
			{
				var relative = Path.GetRelativePath( folder, file ).Replace( '\\', '/' );
				var parts = relative.Split( '/' );

				//Dot folders and build output: caches and copies, not what the game names
				if ( parts.Any( x => x.StartsWith( '.' ) || x.Equals( "obj", StringComparison.OrdinalIgnoreCase ) || x.Equals( "bin", StringComparison.OrdinalIgnoreCase ) ) ) continue;
				if ( skip?.Invoke( relative ) == true ) continue;

				var extension = Path.GetExtension( file );
				if ( !NamingFiles.Contains( extension ) && !PackedKinds().Contains( extension ) ) continue;

				text.Append( File.ReadAllText( file ) ).Append( '\n' );
			}
		}

		var project = _exportConfig.Project;

		if ( project.HasAssetsPath() ) Read( project.GetAssetsPath() );
		if ( project.HasCodePath() ) Read( project.GetCodePath() );

		foreach ( var library in LibrarySystem.All )
		{
			if ( library.Project.HasAssetsPath() ) Read( library.Project.GetAssetsPath() );
			if ( library.Project.HasCodePath() ) Read( library.Project.GetCodePath() );
		}

		//The base code ships with every game, and the base content that always does: its prefabs, its materials
		Read( Path.Combine( root, "..", "code" ) );
		Read( root, relative => relative.StartsWith( "models/", StringComparison.OrdinalIgnoreCase )
			|| BaseEditorOnly.Any( x => relative.StartsWith( x + "/", StringComparison.OrdinalIgnoreCase ) ) );

		return text.ToString().ToLowerInvariant();
	}

	private static IEnumerable<string> GetBlacklistedFiles( string[] blacklist, string absoluteDirectory )
	{
		var allFiles = new List<string>();

		foreach ( var file in Directory.GetFiles( absoluteDirectory ) )
		{
			var fileName = Path.GetFileName( file );

			if ( blacklist != null )
				if ( blacklist.Contains( fileName ) )
					continue;

			allFiles.Add( file );
		}

		return allFiles;
	}

	private IEnumerable<string> GetWhitelistedFiles( string[] whitelist, string absoluteDirectory )
	{
		var allFiles = new List<string>();
		foreach ( var entry in whitelist )
		{
			var normalizedEntry = entry.Replace( '/', Path.DirectorySeparatorChar );

			if ( entry.Contains( "**" ) )
			{
				//Handle recursive directory patterns
				var parts = normalizedEntry.Split( new[] { "**" }, 2, StringSplitOptions.RemoveEmptyEntries );
				var baseDirectory = parts[0].TrimEnd( Path.DirectorySeparatorChar );
				var filePattern = parts[1].TrimStart( Path.DirectorySeparatorChar );

				var fullPath = Path.Combine( absoluteDirectory, baseDirectory );
				try
				{
					if ( Directory.Exists( fullPath ) )
					{
						allFiles.AddRange( Directory.GetFiles( fullPath, filePattern, SearchOption.AllDirectories ) );
					}
					else
					{
						Log.Warning( $"Directory not found: {fullPath}" );
					}
				}
				catch ( DirectoryNotFoundException )
				{
					Log.Warning( $"{entry} doesn't exist" );
				}
			}
			else
			{
				//Handle non-recursive patterns
				var directory = Path.GetDirectoryName( normalizedEntry ) ?? ".";
				var filePattern = Path.GetFileName( normalizedEntry );

				var fullPath = Path.Combine( absoluteDirectory, directory );
				try
				{
					if ( Directory.Exists( fullPath ) )
					{
						allFiles.AddRange( Directory.GetFiles( fullPath, filePattern, SearchOption.TopDirectoryOnly ) );
					}
					else
					{
						Logger.Warning( $"Directory not found: {fullPath}" );
					}
				}
				catch ( DirectoryNotFoundException )
				{
					Logger.Warning( $"{entry} doesn't exist" );
				}
			}
		}

		return allFiles;
	}
}
