using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using static Editor.ProjectPublisher;

namespace Editor;

public partial class StandaloneExporter
{
	static Logger Logger = new Logger( "Exporter" );

	public Project Project => _exportConfig.Project;
	public IReadOnlyList<QueuedFile> Files => _files.AsReadOnly();

	public PackageManifest PackageManifest { get; protected set; }
	public StandaloneManifest StandaloneManifest { get; protected set; }

	public Action<ExportProgress> OnProgressChanged { get; set; }

	/// <summary>What is wrong with what was written, where anything is.</summary>
	public List<string> Problems { get; } = new();

	//Says what will be wrong with the build, here and in the log.
	private void Problem( string message )
	{
		Problems.Add( message );
		Logger.Warning( message );
	}

	private List<QueuedFile> _files = new();
	private ExportConfig _exportConfig;

	StandaloneExporter( ExportConfig config )
	{
		StandaloneManifest = new StandaloneManifest();
		PackageManifest = new PackageManifest();
		_exportConfig = config;
	}

	async Task GenerateAssetManifest( IProgress progress = null, CancellationToken cancel = default )
	{
		await PackageManifest.BuildFromAssets( _exportConfig.Project, progress, cancel );
	}

	/// <summary>Exporter for a project onto a platform by name or id; the one place a build is made from a name</summary>
	public static Task<StandaloneExporter> ForProject( Project project, string platform, string folder )
	{
		ArgumentNullException.ThrowIfNull( project );

		//What the build page offers, not every platform the exporter knows: see
		//ExportPlatform.Offered, which is where macOS is left out until there is a core for it.
		var target = ExportPlatform.Offered.FirstOrDefault( x => string.Equals( x.Folder, platform, StringComparison.OrdinalIgnoreCase )
				|| string.Equals( x.Id, platform, StringComparison.OrdinalIgnoreCase ) )
			?? throw new Exception( $"No platform '{platform}' - one of {string.Join( ", ", ExportPlatform.Offered.Select( x => x.Folder ) )}" );

		if ( string.IsNullOrWhiteSpace( folder ) ) throw new Exception( "Say which folder to write the build into" );

		var config = project.Config;

		return FromConfig( new ExportConfig
		{
			Project = project,
			Platform = target,
			TargetDir = folder,
			ExecutableName = config.GetMetaOrDefault( "Build.ExecutableName", project.Package?.Ident ?? config.Ident ?? "game" ),
			CreateZip = false,
			//The Build page's tick, or -exportdebug on a command line that wants one build for debugging without changing what the project saves.
			ExportDebug = config.GetMetaOrDefault( "Build.Debug", false )
				|| Rinten.Utility.CommandLine.HasSwitch( "-exportdebug" ),
			LoadingScreen = config.GetMetaOrDefault( "Build.LoadingScreen", LoadingScreenKind.Engine ),
			StartupImage = config.GetMetaOrDefault<string>( "Build.SplashPicture", null ),
			BuildDate = DateTime.Now,
		} );
	}

	public static async Task<StandaloneExporter> FromConfig( ExportConfig config )
	{
		var p = new StandaloneExporter( config );
		await p.GenerateAssetManifest();
		return p;
	}

	private void QueueCopyFile( string targetDir, ProjectFile file, BuildStep type )
	{
		var fileName = file.Name;

		//What the editor keeps beside an asset does not travel with the game - see ForGames.
		if ( !ForGames( fileName ) )
		{
			file.Skip = true;
			return;
		}

		var targetPath = Path.Combine( targetDir, fileName );

		//Create directory if it doesn't exist
		{
			var targetSubDir = Path.GetDirectoryName( targetPath );
			Directory.CreateDirectory( targetSubDir );
		}

		if ( file.Contents is not null )
		{
			if ( File.Exists( targetPath ) ) File.Delete( targetPath );
			File.WriteAllBytes( targetPath, file.Contents );
		}
		else if ( file.AbsolutePath is not null )
		{
			var sourcePath = file.AbsolutePath;
			QueueCopy( sourcePath, targetPath, type );
		}
		else
		{
			Log.Warning( $"Unable to copy {file.Name} - has no content defined!" );
		}

		file.Skip = true;
	}

	public async Task Run()
	{
		Logger.Info( $"Building {Project.Config.Title} to {_exportConfig.TargetDir}" );

		Result = _exportConfig.TargetDir;

		Logger.Info( $"Compiling assemblies.." );
		await Compile();

		//A page is not a program with content beside it, so it is not built the way the three desktops are. See StandaloneExporter.Web.
		if ( Platform.IsWeb )
		{
			await RunWeb();
			return;
		}

		Logger.Info( $"Compiling pictures.." );
		await CompilePictures();

		//The engine compiled ahead of time for the platform this is for - twenty seconds of another program,
		//on a thread of its own so the editor is not sitting still while it runs. See AheadOfTimeEngine.
		Logger.Info( $"Compiling the engine ahead of time.." );
		publishedManaged = await Task.Run( () => AheadOfTimeEngine( Platform ) );

		Logger.Info( $"Building manifest.." );
		await BuildManifest();

		Logger.Info( $"Copying files.." );
		await CopyAllFiles();

		//After the copy, which put the engine's own cache in place: this writes it again with the project's shaders in it. See BakeShaders.
		Logger.Info( $"Baking shaders.." );
		await OnMainThread( BakeShaders );

		OnFinish();

		//A phone runs a package rather than a folder - see StandaloneExporter.Android.
		if ( Platform.IsAndroid )
			await PackageAndroid();

		Logger.Info( $"Build complete!" );
	}

	private async Task BuildManifest()
	{
		if ( _exportConfig.AssemblyFiles is not null )
		{
			foreach ( var f in _exportConfig.AssemblyFiles )
			{
				if ( f.Value is byte[] bytes )
				{
					await AddFile( bytes, f.Key );
				}

				if ( f.Value is string json )
				{
					await AddFile( json, f.Key );
				}
			}
		}

		//Add assets from Cloud.Model( .. ) etc
		foreach ( var package in _exportConfig.CodePackages )
		{
			await AddCodePackageReference( package );
		}

		//Make sure we're copying into a directory that exists
		Directory.CreateDirectory( _exportConfig.TargetDir );

		//Create standalone properties
		StandaloneManifest = CreateStandaloneManifest( _exportConfig.TargetDir );
		WriteStandaloneManifest( _exportConfig.TargetDir );

		//Build queue
		DropModelDefinitions();
		QueueAddonFiles( _exportConfig.TargetDir, BuildStep.CopyProjectAssets );
		QueueBaseFiles( _exportConfig.TargetDir );
	}

	/// <summary>Which platform this export is for.</summary>
	public ExportPlatform Platform => _exportConfig.Platform ?? ExportPlatform.Host;

	private void OnFinish()
	{
		//The program's icon is the project's picture - the one set in the project's settings - unless the export was given an .ico of its own.
		var icon = _exportConfig.TargetIcon;

		if ( string.IsNullOrEmpty( icon ) )
			icon = _exportConfig.Project?.GetThumbPath();

		if ( string.IsNullOrEmpty( icon ) ) return;

		//Only Windows keeps a program's icon inside the program. A Linux build carries one in the desktop entry beside it and a Mac in its bundle
		//- neither of which is a resource written into the executable, so writing one there would corrupt the file rather than decorate it.
		if ( !Platform.HasExecutableIcon )
		{
			Logger.Info( $"{Platform.Title} keeps a program's icon outside the program - not writing one in" );
			return;
		}

		//Icon writing uses kernel32's resource API, so it only works when exporting from Windows
		if ( !OperatingSystem.IsWindows() )
		{
			Logger.Info( "the icon goes into the executable through the Windows resource API - exporting from here, the build keeps its default one" );
			return;
		}

		//An icon picked out of the project is named the way everything else in the project is named - from the
		//assets folder down. A rooted path is somebody's own file, or the project's picture, and is left alone.
		if ( !Path.IsPathRooted( icon ) )
			icon = Path.Combine( _exportConfig.Project.GetAssetsPath(), icon );

		if ( !File.Exists( icon ) )
		{
			Logger.Warning( $"the icon {icon} is not there - the build keeps its default one" );
			return;
		}

		var exe = Path.Combine( _exportConfig.TargetDir, Platform.Executable( StandaloneManifest.ExecutableName ) );

		if ( icon.EndsWith( ".ico", StringComparison.OrdinalIgnoreCase ) )
		{
			IconUpdater.UpdateExeIcon( exe, icon );
			return;
		}

		try
		{
			IconUpdater.UpdateExeIcon( exe, IconUpdater.IcoFromPicture( File.ReadAllBytes( icon ) ) );
		}
		catch ( Exception e )
		{
			Logger.Warning( $"the picture {icon} could not be made into an icon ({e.Message}) - the build keeps its default one" );
		}
	}

	private async Task CopyAllFiles()
	{
		var orderedFiles = _files.OrderBy( x => x.Step );
		foreach ( var f in orderedFiles )
		{
			await CopyFileAsync( f );
		}
	}

	private void WriteStandaloneManifest( string targetDir )
	{
		var gameDataPath = Path.Combine( targetDir, Standalone.GamePath );
		Directory.CreateDirectory( gameDataPath );

		var serializedProperties = JsonSerializer.Serialize( StandaloneManifest );
		var manifestPath = Path.Combine( targetDir, Standalone.GamePath, Standalone.ManifestName );
		File.WriteAllText( manifestPath, serializedProperties );
	}

	private StandaloneManifest CreateStandaloneManifest( string baseDir )
	{
		if ( !_exportConfig.Project.Config.TryGetMeta( "ControlModes", out ControlModeSettings controlSettings ) )
			controlSettings = new();

		return new StandaloneManifest()
		{
			BuildDate = _exportConfig.BuildDate,
			ExecutableName = _exportConfig.ExecutableName,
			Ident = _exportConfig.Project.Package.Ident,
			Name = _exportConfig.Project.Package.Title,

			IsVRProject = controlSettings.IsVROnly,
			Debug = _exportConfig.ExportDebug,
			Splash = SplashInBuild() is { } splash ? "/" + splash : null,
		};
	}

	//Drops .mdl definitions whose compiled model ships, unless the game reads them at runtime
	private void DropModelDefinitions()
	{
		var compiled = new HashSet<string>(
			PackageManifest.Assets.Select( asset => asset.Name ), StringComparer.OrdinalIgnoreCase );

		var dropped = PackageManifest.Assets.RemoveAll( asset =>
			asset.Name.EndsWith( ".mdl", StringComparison.OrdinalIgnoreCase ) &&
			compiled.Contains( asset.Name + "_c" ) &&
			!ReadAtRuntime( asset ) );

		if ( dropped > 0 ) Logger.Info( $"{dropped} model definition(s) left behind - the build carries the compiled models" );
	}

	//Whether the game reads this model's definition at runtime; parses the file, unparsable means keep
	private static bool ReadAtRuntime( ProjectFile asset )
	{
		var path = asset?.AbsolutePath;

		if ( string.IsNullOrWhiteSpace( path ) || !File.Exists( path ) ) return true;

		try
		{
			var definition = JsonSerializer.Deserialize<Rinten.ModelDefinition>( File.ReadAllText( path ), definitionOptions );

			return definition?.ReadAtRuntime ?? true;
		}
		catch ( Exception e ) when ( e is JsonException or IOException )
		{
			Logger.Warning( $"{asset.Name}: {e.Message} - the definition travels with the build" );
			return true;
		}
	}

	//As deep as the editor writes a model, and with the enums it writes as their names - the same reading CoreSkeletons gives one.
	private static readonly JsonSerializerOptions definitionOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		MaxDepth = 512,
		Converters = { new EnumConvert() },
	};

	private void QueueAddonFiles( string baseDir, BuildStep type )
	{
		var targetDir = Path.Combine( baseDir, Standalone.GamePath );
		Directory.CreateDirectory( targetDir );

		var targets = PackageManifest.Assets.Where( x => !x.Skip ).ToArray();
		var tasks = new List<Task>();

		foreach ( var t in targets )
		{
			QueueCopyFile( targetDir, t, type );
		}
	}

	private void QueueBaseFiles( string baseDir )
	{
		var engineDir = Environment.CurrentDirectory;

		void QueueFrom( IEnumerable<string> paths, bool aheadOfTime = false )
		{
			foreach ( var file in paths )
			{
				//Where it goes is where it sits under the engine folder - always, and including the assemblies that are copied from somewhere else.
				var relativePath = Path.GetRelativePath( engineDir, file );
				var targetPath = Path.Combine( baseDir, relativePath );

				QueueCopy( aheadOfTime ? AheadOfTime( file ) : file, targetPath, BuildStep.CopyCoreAssets );
			}
		}

		//The program first: the engine's assemblies a game gets are the ones it reaches, and it is where that walk starts - see GetManagedFiles.
		var launcher = Platform.IsAndroid ? null : BuildLauncher( Platform );



		//The engine's own assemblies, and the natives of the platform this is for.
		QueueFrom( GetDllFiles( engineDir, launcher?.Assembly ), aheadOfTime: true );

		//FFmpeg, for a game that plays or writes video. See StandaloneExporter.Video.
		QueueFrom( GetVideoFiles( engineDir ) );

		//The engine's content, where the engine mounts it: core/, and the base addon's Assets - capitalised, because that is
		//what the folder is called and Linux is not going to guess; see EngineFileSystem, which mounts it by the same name.
		QueueFrom( GetCoreFiles( engineDir ) );
		QueueFrom( BaseContent( engineDir ).SelectMany( ShippedAs ) );

		if ( OwnSplash() is { } own )
			QueueCopy( own, Path.Combine( baseDir, "addons", "base", "Assets", SplashInBuild() ), BuildStep.CopyCoreAssets );

		//Fetch code resources (resource manifest stuff from native)
		{
			var codeResources = GetCodeResources( engineDir );

			foreach ( var resource in codeResources )
			{
				var sourcePath = resource.AbsolutePath;
				var targetPath = Path.Combine( baseDir, $"core/{resource.RelativePath}" );

				//Only copy compiled resources
				if ( !sourcePath.EndsWith( "_c" ) )
					sourcePath += "_c";

				if ( !targetPath.EndsWith( "_c" ) )
					targetPath += "_c";

				QueueCopy( sourcePath, targetPath, BuildStep.CopyCode );
			}
		}

		//Copy the program, built for whichever platform this export is for.
		if ( !Platform.IsAndroid )
		{
			var standalone = EngineExecutables.Standalone;

			if ( launcher is not null )
			{
				QueueCopy( launcher.Program,
					$"{baseDir}/{Platform.Executable( StandaloneManifest.ExecutableName )}",
					BuildStep.FinalizeExecutable );

				QueueCopy( launcher.Assembly, $"{baseDir}/{standalone}.dll", BuildStep.FinalizeExecutable );
				QueueCopy( launcher.RuntimeConfig, $"{baseDir}/{standalone}.runtimeconfig.json", BuildStep.FinalizeExecutable );

				//The runtime itself, where the platform has none of its own.
				foreach ( var file in RuntimeFiles( Platform, launcher ) )
					QueueCopy( file, $"{baseDir}/{Path.GetFileName( file )}", BuildStep.FinalizeExecutable );
			}
			else
			{
				Logger.Warning( $"this export has no program to start it - everything else in it is written, " +
					$"but there is no {Platform.Title} launcher to put in front of it" );
			}
		}

		//Copy the project files for base + addon - ideally we should store these in an embedded resource inside the exe
		{
			var projectPath = Path.Combine( baseDir, Standalone.GamePath, Project.Extension );
			QueueCopy( $"{_exportConfig.Project.ConfigFilePath}", projectPath, BuildStep.CopyMisc );
			QueueCopy( $"{engineDir}/addons/base/{Project.Extension}", $"{baseDir}/addons/base/{Project.Extension}", BuildStep.CopyMisc );
		}
	}

	private void QueueCopy( string source, string dest, BuildStep type )
	{
		_files.Add( new QueuedFile( source, dest, type ) );
	}

	//Asset kinds sealed into the build: every JSON-sourced resource type except models
	private static HashSet<string> packedKinds;

	private static HashSet<string> PackedKinds() => packedKinds ??= new HashSet<string>(
		Rinten.Game.TypeLibrary.GetAttributes<Rinten.AssetTypeAttribute>()
			.Select( type => "." + type.Extension )
			.Where( extension => extension != ".mdl" ),
		StringComparer.OrdinalIgnoreCase );

	private bool Packs( QueuedFile file )
	{
		if ( !Seals( file ) ) return false;

		var extension = Path.GetExtension( file.Destination );

		//A shader's text is not a resource, but a game reads it for the same kind of reason - what a material's numbers are called, which of its features
		//exist, whether it is a point effect. The words it compiles to are in the cache; this is the description beside them, and it packs like the rest.
		if ( extension.Equals( ".shader", StringComparison.OrdinalIgnoreCase ) ||
			extension.Equals( ".slang", StringComparison.OrdinalIgnoreCase ) ) return true;

		return PackedKinds().Contains( extension );
	}

	private bool Seals( QueuedFile file )
	{
		//By where it lands, not by which step put it there: content is what goes under the game's own assets folder and under the base
		//addon's, and everything else in a build - the program, its libraries, core/ - is read by something that knows nothing of this.
		var relative = Path.GetRelativePath( _exportConfig.TargetDir, file.Destination ).Replace( '\\', '/' );

		var content = relative.StartsWith( Standalone.GamePath, StringComparison.OrdinalIgnoreCase ) ||
			relative.StartsWith( "addons/base/Assets/", StringComparison.OrdinalIgnoreCase );

		if ( !content ) return false;

		if ( Path.GetFileName( file.Destination ).Equals( Standalone.ManifestName, StringComparison.OrdinalIgnoreCase ) )
			return false;

		return Path.GetExtension( file.Destination ).ToLowerInvariant() switch
		{
			//A font is handed to the text shaper as a path, and the project file is read before there is a filesystem to read it through.
			".ttf" or ".otf" or ".ttc" or ".woff" or ".woff2" or ".rinproj" => false,

			//The game's own assemblies, under assets/.bin: the runtime loads those, and it knows nothing of any of this.
			".dll" or ".cll" or ".pdb" or ".xml" => false,

			_ => true,
		};
	}

	private async Task CopyFileAsync( QueuedFile file )
	{
		//Make sure we have a directory to copy into
		{
			var targetDir = Path.GetDirectoryName( file.Destination );
			if ( !Directory.Exists( targetDir ) ) Directory.CreateDirectory( targetDir );
		}

		//Attempt to copy the file
		try
		{
			//Check if the file exists first - there's no point in spinning up a task if it doesn't.
			if ( !File.Exists( file.Source ) )
			{
				file.State = QueuedFileState.FileNotFound;
				return;
			}

			await Task.Run( () =>
			{
				try
				{
					//A link stays a link. A library's other names are links to it in the engine's folder, and copying each one wrote the library out
					//again under every name - slang twice, SDL three times. Not from Windows, where making one needs rights an export does not have.
					if ( new FileInfo( file.Source ).LinkTarget is { } link && !OperatingSystem.IsWindows() )
					{
						if ( new FileInfo( file.Destination ) is { Exists: true } or { LinkTarget: not null } )
							File.Delete( file.Destination );

						File.CreateSymbolicLink( file.Destination, link );
					}
					else if ( Packs( file ) )
					{
						File.WriteAllBytes( file.Destination + "_c",
							Rinten.CompiledFile.Pack( File.ReadAllText( file.Source ) ) );
					}
					else if ( Seals( file ) )
					{
						File.WriteAllBytes( file.Destination, Rinten.SealedFile.Seal( File.ReadAllBytes( file.Source ) ) );
					}
					else
					{
						File.Copy( file.Source, file.Destination, true );
					}

					file.State = QueuedFileState.Copied;
				}
				catch ( Exception ex )
				{
					Log.Error( ex );
					file.State = QueuedFileState.FailedToCopy;
				}
			} );
		}
		finally
		{
			TriggerProgessChanged( file );
		}
	}

	/// <summary>Manually add a file to the manifest</summary>
	public Task AddFile( byte[] contents, string relativePath )
	{
		return PackageManifest.AddFile( contents, relativePath );
	}

	/// <summary>Manually add a file to the manifest</summary>
	public Task AddFile( string contents, string relativePath )
	{
		return PackageManifest.AddTextFile( contents, relativePath );
	}

	/// <summary>If the code is referencing a package - we can add it to the manifest using this.</summary>
	public Task AddCodePackageReference( string package )
	{
		return PackageManifest.AddCodePackageReference( package );
	}

	private void TriggerProgessChanged( QueuedFile file )
	{
		if ( OnProgressChanged is not null )
		{
			var extension = Path.GetExtension( file.Source );
			var assetType = AssetType.FromExtension( extension );

			var filesTotal = Files.Count;
			var filesDone = Files.Count( x => x.State != QueuedFileState.Waiting );
			var filesLeft = Files.Count( x => x.State == QueuedFileState.Waiting );

			var progressFraction = (float)(filesTotal - filesLeft) / filesTotal;
			var actionText = $"Copying files ({(progressFraction * 100f).CeilToInt()}%)";
			var stepString = file.Step.GetDescription();

			var buildIssues = new string[Files.Count( x => x.State != QueuedFileState.Waiting && x.State != QueuedFileState.Copied )];
			var issueIdx = 0;

			for ( int fileIdx = 0; fileIdx < Files.Count; fileIdx++ )
			{
				var processedFile = Files[fileIdx];

				if ( processedFile.State == QueuedFileState.Waiting )
					continue;

				if ( processedFile.State == QueuedFileState.Copied )
					continue;

				if ( processedFile.State == QueuedFileState.FailedToCopy )
					buildIssues[issueIdx] = $"Failed to copy '{processedFile.Source}' during step '{processedFile.Step.GetDescription()}'";

				if ( processedFile.State == QueuedFileState.FileNotFound )
					buildIssues[issueIdx] = $"File '{processedFile.Source}' not found during step '{processedFile.Step.GetDescription()}'";

				issueIdx++;
			}

			var progress = new ExportProgress()
			{
				ProgressFraction = progressFraction,
				CurrentOperation = stepString,
				FilesDone = filesDone,
				FilesTotal = filesTotal,
				BuildIssues = buildIssues
			};

			UpdateProgress( progress );
		}
	}
}
