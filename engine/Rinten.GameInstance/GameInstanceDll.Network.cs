using Rinten.Network;
using Rinten.Tasks;
using System;
using System.Collections.Generic;
using System.IO;

namespace Rinten;

internal partial class GameInstanceDll
{
	readonly StringTable CodeArchiveTable = new( "CodeArchive", true );

	internal readonly ServerPackages ServerPackages = new();

	//The config table is used to send config (like physics config, input config) to the client. This isn't always
	//needed, because the config is loaded from the package. But if we're operating without a package, it is needed.
	readonly StringTable ConfigTable = new( "Config", true );

	//Hold and network any small files such as StyleSheets and compiled prefab assets.
	readonly SmallNetworkFiles NetworkedSmallFiles = new( "SmallFiles" );

	//Hold and network any files from Project Settings (.config files.)
	readonly SmallNetworkFiles NetworkedConfigFiles = new( "ConfigFiles" );

	//Hold and network any localization files.
	readonly SmallNetworkFiles NetworkedLangFiles = new( "LangFiles" );

	//Hold and network any small files such as StyleSheets and compiled prefab assets.
	readonly LargeNetworkFiles NetworkedLargeFiles = new( "LargeFiles" );

	//Hold and network any small files such as StyleSheets and compiled prefab assets.
	readonly ReplicatedConvars ReplicatedConvars = new( "ReplicatedConvars" );

	private List<FileWatch> FileWatchers { get; set; } = new();
	private bool DidMountNetworkedFiles { get; set; }

	//What we enumerate for files to offer joining clients: the game's content plus any local
	//libraries. We own this so it needs disposing, the filesystems mounted into it don't.
	internal AggregateFileSystem NetworkedFileSystem { get; private set; }

	public GameNetworkSystem CreateGameNetworking( NetworkSystem system )
	{
		var instance = new SceneNetworkSystem( TypeLibrary, system );

		NetworkedLargeFiles.NetworkInitialize( instance );
		Platform.Chat.NetworkInitialize( instance );

		if ( Networking.IsHost )
		{
			AddFilesToNetwork( NetworkedConfigFiles, EngineFileSystem.ProjectSettings, [".config"] );
			AddFilesToNetwork( NetworkedLangFiles, Game.Language.FileSystem, [".json"] );
			BuildNetworkedFiles();
		}
		else if ( !DidMountNetworkedFiles )
		{
			EngineFileSystem.ProjectSettings.Mount( NetworkedConfigFiles.Files );
			Game.Language.FileSystem.Mount( NetworkedLangFiles.Files );
			Game.Language.Refresh();

			FileSystem.Mounted.Mount( NetworkedLargeFiles.Files );
			FileSystem.Mounted.Mount( NetworkedSmallFiles.Files );

			NetworkedSmallFiles.Refresh();
			NetworkedConfigFiles.Refresh();
			NetworkedLangFiles.Refresh();

			ResourceLoader.LoadAllGameResource( FileSystem.Mounted, reloadExisting: true );
			FontManager.Instance.LoadAll( FileSystem.Mounted );

			DidMountNetworkedFiles = true;
		}

		return instance;
	}

	public async Task<GameNetworkSystem> CreateGameNetworkingAsync( NetworkSystem system )
	{
		var instance = new SceneNetworkSystem( TypeLibrary, system );

		NetworkedLargeFiles.NetworkInitialize( instance );
		Platform.Chat.NetworkInitialize( instance );

		if ( Networking.IsHost )
		{
			AddFilesToNetwork( NetworkedConfigFiles, EngineFileSystem.ProjectSettings, [".config"] );
			AddFilesToNetwork( NetworkedLangFiles, Game.Language.FileSystem, [".json"] );
			BuildNetworkedFiles();
		}
		else if ( !DidMountNetworkedFiles )
		{
			EngineFileSystem.ProjectSettings.Mount( NetworkedConfigFiles.Files );
			Game.Language.FileSystem.Mount( NetworkedLangFiles.Files );
			Game.Language.Refresh();

			FileSystem.Mounted.Mount( NetworkedLargeFiles.Files );
			FileSystem.Mounted.Mount( NetworkedSmallFiles.Files );

			NetworkedSmallFiles.Refresh();
			NetworkedConfigFiles.Refresh();
			NetworkedLangFiles.Refresh();

			LoadingScreen.Title = "Loading Resources";
			await ResourceLoader.LoadAllGameResourceAsync( FileSystem.Mounted, reloadExisting: true );
			FontManager.Instance.LoadAll( FileSystem.Mounted );

			DidMountNetworkedFiles = true;
		}

		return instance;
	}

	void AddFilesToNetwork( SmallNetworkFiles target, BaseFileSystem fs, HashSet<string> validExtensions )
	{
		var files = fs.FindFile( "/", "*", true );

		foreach ( var fileName in files )
		{
			var extension = Path.GetExtension( fileName );
			if ( !validExtensions.Contains( extension ) )
				continue;

			var text = fs.ReadAllBytes( fileName );
			target.AddFile( fs, fileName, text.ToArray() );
		}

		var watcher = fs.Watch();
		watcher.OnChanges += w =>
		{
			foreach ( var fileName in w.Changes )
			{
				var extension = Path.GetExtension( fileName );
				if ( !validExtensions.Contains( extension ) )
					continue;

				if ( fs.FileExists( fileName ) )
				{
					var text = fs.ReadAllBytes( fileName );
					target.AddFile( fs, fileName, text.ToArray() );
				}
				else
				{
					target.RemoveFile( fileName );
				}
			}
		};

		FileWatchers.Add( watcher );
	}

	//The code archives that come in from the network, compiled - see ServerCode.
	IServerCode serverCode;

	public void InstallNetworkTables( NetworkSystem system )
	{
		system.InstallTable( CodeArchiveTable );
		system.InstallTable( ServerPackages.StringTable );
		system.InstallTable( NetworkedSmallFiles.StringTable );
		system.InstallTable( NetworkedConfigFiles.StringTable );
		system.InstallTable( NetworkedLangFiles.StringTable );
		system.InstallTable( NetworkedLargeFiles.StringTable );
		system.InstallTable( ReplicatedConvars.StringTable );

		CodeArchiveTable.OnChangeOrAdd = ( entry ) =>
		{
			serverCode?.Update( entry.Data );

			//A page that joined with the server's code is left behind by a change to it.
			if ( serverCode is null && OperatingSystem.IsBrowser() && WebCode.Refuses( [(entry.Name, entry.Data)] ) is { } refused )
				Disconnect( refused );
		};

		CodeArchiveTable.PostNetworkUpdate = () => FinishLoadingCodeArchives();

		//Config
		system.InstallTable( ConfigTable );
		ConfigTable.PostNetworkUpdate = UpdateConfigFromNetworkTable;
	}

	bool FinishLoadingCodeArchives()
	{
		if ( serverCode is { NeedsBuild: true } && !serverCode.BuildAndLoad( AssemblyEnroller ) )
			return false;

		//Do the hotload and stuff
		FinishLoadingAssemblies();

		return true;
	}

	public async Task<bool> LoadNetworkTables( NetworkSystem system )
	{
		//Where this build has a compiler. One without - a web page - can join a server that sends no code, or one whose code
		//is the code the page was compiled from (its module holds it already - see WebCode.Refuses); never a different one.
		serverCode = ProjectCompiling.Current?.NewServerCode();

		if ( serverCode is null && CodeArchiveTable.Entries.Any() )
		{
			var refused = OperatingSystem.IsBrowser()
				? WebCode.Refuses( CodeArchiveTable.Entries.Select( x => (x.Key, x.Value.Data) ) )
				: "This server sends its code to be compiled, and this build of the game has no compiler.";

			if ( refused is not null )
			{
				Disconnect( refused );
				return false;
			}

			//The page's own code is the server's: nothing to compile, nothing to load - the loop below hands the archives to no one.
		}

		//Don't hotload while we're downloading stuff!
		using var pauseAsmLoadScope = PauseLoadingAssemblies();

		//Any assemblies come our way?
		foreach ( var entry in CodeArchiveTable.Entries )
		{
			serverCode?.Update( entry.Value.Data );
		}

		//We might have loaded new assemblies, here's a safe time to hotload before we start downloading again.
		if ( !FinishLoadingCodeArchives() )
		{
			Disconnect( "Failed to compile code archives. Check log for details." );
			return false;
		}

		//Load configs from network tables
		UpdateConfigFromNetworkTable();

		await ServerPackages.InstallAll();

		//Prevent a blank title between the last package install and the download queue start.
		if ( string.IsNullOrWhiteSpace( LoadingScreen.Title ) )
			LoadingScreen.Title = "Loading..";

		await NetworkedLargeFiles.RunDownloadQueue( system, default );

		return true;
	}

	static string[] _interestingExtensions = new[] { "_c", ".scss", ".ttf" };
	static string[] _engineAssets = new[] { "vtex_c", "vmat_c", "vsnd_c", "vmdl_c", "vpk", "vanmgrph_c", "shader_c" }; //anything the native engine loads from disk has to be a LARGE download
	List<string> _netIncludePaths = new(); //wildcard-supported paths we also want to include content of

	//Small files only live in an in-memory filesystem, which native loaders can't read - engine assets must be a real file on disk.
	internal static bool ShouldUseLargeDownload( string filename, long size )
		=> size >= 1024 * 64 || _engineAssets.Any( x => filename.EndsWith( x ) );

	bool ShouldNetworkFile( string filename )
	{
		filename = filename.NormalizeFilename();

		if ( !AssetDownloadCache.IsLegalDownload( filename ) )
			return false;

		if ( _netIncludePaths.Any( x => filename.WildcardMatch( x ) ) )
			return true;

		return _interestingExtensions.Any( x => filename.EndsWith( x ) );
	}

	void UpdateNetworkFile( BaseFileSystem fs, string filename )
	{
		// ignore code junk
		if ( filename.Contains( "\\code\\obj\\", System.StringComparison.OrdinalIgnoreCase ) )
			return;

		if ( !ShouldNetworkFile( filename ) )
		{
			return;
		}

		AddNetworkFile( fs, EngineFileSystem.Mounted, filename, NetworkedSmallFiles, NetworkedLargeFiles );
	}

	internal static void AddNetworkFile( BaseFileSystem source, BaseFileSystem mounted, string filename,
		SmallNetworkFiles smallFiles, LargeNetworkFiles largeFiles )
	{
		// Native formats always go large. Only check that the candidate exists in the source;
		// its size and checksum must come from the mount that actually serves client requests.
		if ( ShouldUseLargeDownload( filename, 0 ) )
		{
			if ( !source.FileExists( filename ) ) return;
			AddLargeFile();
			return;
		}

		Stream stream;
		try
		{
			stream = source.OpenRead( filename );
		}
		catch ( FileNotFoundException )
		{
			return;
		}
		catch ( DirectoryNotFoundException )
		{
			return;
		}

		if ( stream is null ) return;
		using ( stream )
		{
			var size = stream.Length;
			if ( !ShouldUseLargeDownload( filename, size ) )
			{
				var bytes = new byte[size];
				stream.ReadExactly( bytes );
				smallFiles.AddFile( filename, bytes );

				if ( AssetDownloadCache.DebugNetworkFiles )
					Log.Info( $"Adding Small File {filename} ({size.FormatBytes()})" );
				return;
			}
		}

		AddLargeFile();

		void AddLargeFile()
		{
			if ( !largeFiles.AddFile( mounted, filename ) )
				Log.Warning( $"File '{filename}' ('{source.GetFullPath( filename )}') doesn't exist - skipping" );
		}
	}

	//Go through our mounted files and make them available to joining clients for download
	void BuildNetworkedFiles()
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();

		var gameInstance = IGameInstance.Current as GameInstance;
		if ( gameInstance is null )
		{
			Log.Warning( "Game Instance was null when building network files" );
			return;
		}

		//No network files needed for package based games
		if ( gameInstance.IsRemote ) return;

		if ( AssetDownloadCache.DebugNetworkFiles )
			Log.Info( "Building network files.." );

		//include anything on resource paths
		var project = Project.Current;
		if ( project is not null && !string.IsNullOrWhiteSpace( project.Config.Resources ) )
		{
			var resourcePaths = project.Config.Resources.Split( "\n", StringSplitOptions.RemoveEmptyEntries )
			.Select( x => x.Trim() )
			.Where( x => !x.StartsWith( "//" ) )
			.Select( x => x.NormalizeFilename( true, false ) );

			_netIncludePaths.AddRange( resourcePaths );
		}

		//a library is its own package with its own filesystem, so its assets aren't in the game's filesystem and joining clients are never told about them
		var libraries = Project.Libraries
			.Where( x => x.Active && x.RootDirectory is not null )
			.ToArray();

		var fs = new AggregateFileSystem();
		fs.Mount( gameInstance.GameFileSystem );

		foreach ( var library in libraries )
		{
			if ( PackageManager.Find( library.Package.FullIdent, true ) is not { } libraryPackage )
				continue;

			fs.Mount( libraryPackage.FileSystem );
		}

		NetworkedFileSystem = fs;

		var files = fs.FindFile( "/", "*", true );

		foreach ( var file in files )
		{
			UpdateNetworkFile( fs, file );
		}

		var watcher = fs.Watch();
		watcher.OnChanges += w =>
		{
			foreach ( var fileName in w.Changes )
			{
				UpdateNetworkFile( fs, fileName );
			}
		};

		FileWatchers.Add( watcher );

		NetworkTransientGeneratedFiles( project );

		//library transients can't be networked - large files resolve through
		//EngineFileSystem.Mounted, and only the main project's .rinten/transient is ever mounted there

		if ( AssetDownloadCache.DebugNetworkFiles )
			Log.Info( $"..done in {sw.Elapsed.TotalSeconds:0.00}s" );
	}

	//Make runtime-generated assets in the project's .rinten/transient/ folder available to
	//joining clients This is necessary for connected clients to see things like TextureGenerators.
	void NetworkTransientGeneratedFiles( Project project )
	{
		if ( project is null )
			return;

		var transientFolder = Path.Combine( project.GetRootPath(), Project.CacheFolder, "transient" );
		if ( !Directory.Exists( transientFolder ) )
			return;

		var transientFs = new LocalFileSystem( transientFolder );

		foreach ( var file in transientFs.FindFile( "/", "*", true ) )
		{
			UpdateNetworkFile( transientFs, file );
		}

		var watcher = transientFs.Watch();
		watcher.OnChanges += w =>
		{
			foreach ( var fileName in w.Changes )
			{
				UpdateNetworkFile( transientFs, fileName );
			}
		};

		FileWatchers.Add( watcher );
	}
}
