using Rinten.Engine;
using Rinten.Physics;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Editor;

//Editor startup and project opening; a project opens in one go between two frames
static partial class StartupLoadProject
{
	/// <summary>Whether the editor or a project in it is still coming up.</summary>
	public static bool IsLoading => warming || opening;

	//The built-in projects are coming in.
	static bool warming;

	//A project is being opened.
	static bool opening;

	public static Logger Log = new( "Startup" );

	/// <summary>What opening the project is doing right now, or null when nothing is being opened.</summary>
	public static string Message { get; private set; }

	/// <summary>How far through opening it is, nought to one.</summary>
	public static float Progress { get; private set; }

	static int CurrentStep;
	static int TotalSteps;

	//Makes the panel to pick a project with, and says which project file was picked. The launcher, which lives in the program that starts the editor
	//rather than in here - so it hands itself over before the editor starts, and is asked only when nothing on the command line said which project.
	internal static Func<Action<string>, Rinten.UI.Panel> ProjectPicker { get; set; }

	//The built-in projects coming in, once they have been started.
	static Task builtIns;

	//A project waiting for the next gap between frames to be opened in.
	static string requested;

	//The launcher, while it is held over the window.
	static ModalSheet picker;

	//Where opening the project spent its time. The editor's own start is Bootstrap.StartupTiming, which ends once the window is up.
	static Rinten.Api.Events.EventRecord timing;

	//Advance the progress by one step. And a chance to draw: an open holds the main thread, and the windows are only
	//drawn when somebody pumps them - the launcher's card over the window says how far it has got from what is set here.
	static void Step( string message )
	{
		Message = message;

		//Before the count is known there is no fraction to report.
		if ( TotalSteps > 0 ) Progress = (float)CurrentStep / TotalSteps;

		CurrentStep++;

		Report( force: true );

		Application.Spin();
	}

	//Progress within the current step, for the steps that count their own.
	static void StepProgress( float subFraction )
	{
		if ( TotalSteps <= 0 ) return;

		float stepSize = 1.0f / TotalSteps;
		float baseProgress = (float)(CurrentStep - 1) / TotalSteps;
		Progress = baseProgress + stepSize * Math.Clamp( subFraction, 0f, 1f );

		Report();
	}

	//The bar in the corner, while the editor or a project is coming up.
	static EditorProgress.Job job;

	//Between two of the bar's own paints, measured off the clock rather than off frames - a start does not run any.
	static readonly Stopwatch painted = Stopwatch.StartNew();

	//Puts what the start is doing on the progress bar. Held to about thirty a second unless the caller says otherwise: a step is
	//somebody deciding this is worth saying, and the asset compile calls the other one once per asset, which is a few thousand of them.
	static void Report( bool force = false )
	{
		if ( job is null ) return;

		if ( !force && painted.ElapsedMilliseconds < 33 ) return;

		painted.Restart();

		job.Subtitle = Message;
		job.Fraction = TotalSteps > 0 ? Progress : -1f;

		EditorProgress.Tick();
	}

	static int lastPortTenth = -1;

	//What the port says it is doing, in the log every tenth of the way. A port is a process of its own with no
	//window: the launcher that started it lights the project's card while it runs, and this is its own account.
	static void PortStep( string message, float progress )
	{
		Message = message;
		Progress = progress;

		Report();

		var tenth = (int)( progress * 10 );

		if ( tenth == lastPortTenth ) return;

		lastPortTenth = tenth;

		Log.Info( $"port: {tenth * 10}% - {message}" );
	}

	/// <summary>The editor's start, once, from bootstrap: the window, and what goes over it.</summary>
	public static async Task Run()
	{
		var path = Rinten.Utility.CommandLine.GetSwitch( "-project", "" ).TrimQuoted();
		var portTo = Rinten.Utility.CommandLine.GetSwitch( "-port", "" ).TrimQuoted();

		if ( !string.IsNullOrWhiteSpace( portTo ) )
		{
			await Port( path, portTo );
			return;
		}

		EditorMainWindow window;

		using ( var _ = Bootstrap.StartupTiming?.ScopeTimer( "Start Editor: Main Window" ) )
			window = new EditorMainWindow();

		using ( var _ = Bootstrap.StartupTiming?.ScopeTimer( "Start Editor: Show" ) )
			window.StartEmpty();

		if ( !string.IsNullOrWhiteSpace( path ) )
		{
			requested = path;
			return;
		}

		ShowPicker();
	}

	//Between two frames: the built-in projects are started once the first one has been drawn, and a project asked for is opened.
	internal static void BetweenFrames()
	{
		framesRunning = true;

		//Whatever was waiting for this gap - the built-in projects come in a piece a frame. See NextFrame.
		ReleaseFrame();

		if ( builtIns is null )
		{
			builtIns = LoadBuiltIns();

			//And a frame of their own before anything is opened: the window has had one to show itself in, and this is the next.
			return;
		}

		if ( requested is null ) return;

		var path = requested;
		requested = null;

		//One project per process: "Close Project" starts another one.
		if ( Project.Current is not null || opening ) return;

		Rinten.Tasks.SyncContext.RunBlocking( Open( path ) );
	}

	//The editor's own projects - base, tools and the rest built in - which are where its docks, inspectors and tools are. None of it is the project's.
	static async Task LoadBuiltIns()
	{
		warming = true;

		var sw = Stopwatch.StartNew();

		//The longest wait in the editor and the one nothing ever said anything about: the
		//first start after the engine is rebuilt compiles every one of its own projects.
		job ??= EditorProgress.Start( "Starting the editor", "rocket_launch" );

		Message = "Compiling the editor's projects";
		Report( force: true );

		try
		{
			//Compiled first: a package's assemblies are read out of what its compiler wrote. Here and not before the window: a cached build is a tenth of a second,
			//but the first start after the engine is rebuilt compiles every one of them from nothing, which was twenty seconds of a window that was not there yet.
			Log.Info( "Compiling base projects" );

			if ( !await Project.CompileAsync() )
			{
				Log.Error( "the editor's own projects did not compile - see above. What is in them will be missing." );

				//Said over the window too: the console is under the launcher's blur.
				ShowBuiltInsFailed();
			}

			Message = "Loading the editor's projects";
			Report( force: true );

			//A frame after the compile and one between each package. A cached compile finishes without ever waiting, so all of this used
			//to run inside the one gap between two frames - a second and more of a window that did not move, just after it appeared.
			await NextFrame();

			await PackageManager.InstallProjects( Project.All.Where( x => x.IsBuiltIn ).ToArray(), between: NextFrame );
		}
		catch ( Exception e )
		{
			Log.Error( e, "the editor's own projects could not be loaded" );

			ShowBuiltInsCrashed( e );
		}
		finally
		{
			warming = false;

			//Unless a project is being opened, which carries on with the same job - what it says changes, the bar does not come and go.
			if ( !opening )
			{
				Message = null;
				job?.Finish( "ready" );
				job = null;
			}
		}

		Log.Info( $"the editor's own projects are in, {sw.ElapsedMilliseconds} ms after they were started on" );

		//And the docks in a frame of their own.
		await NextFrame();

		if ( EditorMainWindow.Current.IsValid() ) EditorMainWindow.Current.OnBuiltInsReady();
	}

	//The next gap between frames, while something is waiting for it.
	static TaskCompletionSource nextFrame;

	//Whether frames are being run at all - before the first, there is no next one.
	static bool framesRunning;

	//Yields to the next frame between work pieces; runs straight through while opening or before frames run
	static Task NextFrame()
	{
		if ( opening || !framesRunning ) return Task.CompletedTask;

		return ( nextFrame ??= new TaskCompletionSource() ).Task;
	}

	//Lets whatever was waiting for the next frame go on - see NextFrame.
	static void ReleaseFrame()
	{
		var waiting = nextFrame;
		nextFrame = null;
		waiting?.TrySetResult();
	}

	//Holds the launcher over the window until a project from it is open.
	static void ShowPicker()
	{
		if ( ProjectPicker is null )
		{
			Log.Warning( "no project was asked for, and there is nothing here to pick one with" );
			return;
		}

		var content = ProjectPicker( chosen =>
		{
			//The first pick is the one: its card is lit until the project is open, and a second press is somebody impatient.
			if ( requested is null && !opening ) requested = chosen;
		} );

		picker = ModalSheet.Open( EditorMainWindow.Current, content );
	}

	//The launcher over the editor while a project is open - File > Open Launcher. Picking another project restarts the editor
	//on it (a project is not swapped in-process - see Open); picking this one, a press beside the card or Escape leave it.
	internal static void ShowLauncher()
	{
		if ( ProjectPicker is null || picker.IsValid() ) return;

		var current = Project.Current?.ConfigFilePath;

		var content = ProjectPicker( chosen =>
		{
			picker?.Close();
			picker = null;

			if ( string.Equals( System.IO.Path.GetFullPath( chosen ), current is null ? null : System.IO.Path.GetFullPath( current ), StringComparison.Ordinal ) )
				return;

			EditorUtility.RestartEditor( chosen );
		} );

		picker = ModalSheet.Open( EditorMainWindow.Current, content );

		if ( picker is not null )
			picker.Dismissable = true;
	}

	//Opens a project into the editor that is up.
	static async Task Open( string path )
	{
		opening = true;
		CurrentStep = 0;

		//What a restart from a problem card opens again.
		startProject = path;

		job ??= EditorProgress.Start( "Opening project", "folder_open" );
		job.Title = $"Opening {System.IO.Path.GetFileNameWithoutExtension( path )}";
		job.Icon = "folder_open";

		//The built-in projects, if they are waiting on a frame between pieces, go on now: this is waited on until it is in, and it waits for them.
		ReleaseFrame();

		timing = new Rinten.Api.Events.EventRecord( "ProjectTiming" );
		timing.StartTimer( "Time" );

		EditorMainWindow.Current?.ShowOpening( path );

		Step( "Opening project" );

		bool success;
		Exception failure = null;

		try
		{
			//The project's code is built against the editor's own projects, and its settings pages and asset types
			//are theirs. Waited for here and not before the window, where they would hold up only the window.
			using ( var _ = timing.ScopeTimer( "Built-in Projects" ) )
			{
				await ( builtIns ??= LoadBuiltIns() );
			}

			success = await OpenProject( path, false, default );
		}
		catch ( Exception e )
		{
			Log.Error( e, "could not open the project" );
			success = false;
			failure = e;
		}

		if ( !success )
		{
			job?.Fail( "the project could not be opened", 4f );
			job = null;

			//Half a project is nothing to go on editing, and there is no taking one back out of a process - so
			//it starts again, with the launcher or with the same project, once whoever is there has read why.
			opening = false;
			ShowOpenFailed( path, failure );
			return;
		}

		using ( var _list = timing.ScopeTimer( "Project List" ) )
		{
			var projectList = new ProjectList();
			var entry = projectList.TryAddFromFile( path );
			if ( entry != null )
				entry.LastOpened = DateTime.Now;
			projectList.SaveList();

			TaskbarJumpList.Refresh();
		}

		using ( var _window = timing.ScopeTimer( "Editor Window" ) )
		{
			EditorMainWindow.Current?.OnProjectOpened();
		}

		picker?.Close();
		picker = null;

		//Show build notifications by default for any compilers from now on
		CompileGroup.SuppressBuildNotifications = false;

		Message = null;
		opening = false;

		job?.Finish( "open" );
		job = null;

		//After that, so the notification it raises is one of the ones that show.
		ReportFailedCompile();

		timing.FinishTimer( "Time" );
		timing.WriteTo( Log );
	}

	//Reads a project to write it out as another, and quits: an editor started for nothing else, with no window of its own.
	static async Task Port( string path, string portTo )
	{
		opening = true;

		bool success;

		try
		{
			await ( builtIns = LoadBuiltIns() );

			success = await OpenProject( path, true, default );
		}
		catch ( Exception e )
		{
			Log.Error( e, "could not open the project to port from" );
			success = false;
		}

		if ( success )
		{
			//Nothing is said here about where the game's own content is read from: the porter finds the installed game itself - see SBoxBaseContent.Find.
			try
			{
				Rinten.Porting.SBox.SBoxPorter.PortCurrentProject( portTo, PortStep );
			}
			catch ( Exception e )
			{
				Log.Error( e, "port failed" );
			}
		}

		EditorUtility.Quit( false );
	}

	//Opens the project once per process; when porting, skips everything that writes into it
	static async Task<bool> OpenProject( string path, bool porting, CancellationToken ct )
	{
		if ( string.IsNullOrEmpty( path ) ) throw new ArgumentException( nameof( path ) );
		Assert.IsNull( Project.Current );

		//This kinda sucks but better than overcomplicating everything.. make sure to update the steps..
		CurrentStep = 0;
		TotalSteps = 15;

		//should never be one existing once we remove all this shit
		Project project;

		using ( var _added = timing?.ScopeTimer( $"Add From File" ) )
		{
			project = Project.AddFromFile( path, false );
		}
		var parentPackage = project.Config.GetMetaOrDefault<string>( "ParentPackage", null );

		Step( "Initializing filesystem" );
		using ( var _ = timing?.ScopeTimer( $"Init FileSystem" ) )
		{
			FileSystem.InitializeFromProject( project );
		}

		ProjectCookie?.Dispose();
		ProjectCookie = new CookieContainer( "project", fileSystem: FileSystem.ProjectTemporary );

		//Set the project as active and sync with the package manager to install any dependant packages the compiler depends on
		Project.Current = project;
		Project.Current.LastOpened = DateTime.Now;
		project.Active = true;

		Step( "Initializing asset system" );
		using ( var _ = timing?.ScopeTimer( $"Init AssetSystem" ) )
		{
			AssetSystem.InitializeFromProject( project );
		}

		//Scan the project for libraries
		Step( "Scanning libraries" );
		using ( var _ = timing?.ScopeTimer( $"Init Libraries" ) )
		{
			LibrarySystem.InitializeFromProject( project );
		}

		//The built-in projects are in already - the editor loads them as it comes up, see LoadBuiltIns.
		//They are the baseline: a "parentpackage" with a "base" package of its own hotloads over it below.

		//Load the dlls etc from our parent package
		if ( project.Config.Type == "addon" && !string.IsNullOrWhiteSpace( parentPackage ) )
		{
			Step( $"Loading parent package ({parentPackage})" );
			using ( var _ = timing?.ScopeTimer( $"ParentPackage" ) )
			{
				await PackageManager.InstallAsync( new PackageLoadOptions( parentPackage, "tools" ) );
			}
		}

		//This should really only load our current project, that we're editing right now
		Step( "Syncing package manager" );
		using ( var _ = timing?.ScopeTimer( $"Sync PackageManager" ) )
		{
			await Project.SyncWithPackageManager();
		}

		//This double Load shit is stupid, creates the compilers properly now that we've installed any dependant packages
		using ( var _loaded = timing?.ScopeTimer( $"Project Load" ) )
		{
			project.Load();
		}

		if ( !porting )
		{
			using ( var _settings = timing?.ScopeTimer( $"Export Settings" ) )
			{
				ExportSettings( project );
			}

			Step( "Generating solution" );
			using var _ = timing?.ScopeTimer( $"Generate Solution" );

			await Project.GenerateSolution();
		}

		Step( "Creating filesystem" );
		using ( var _ = timing?.ScopeTimer( $"Update ProjectFilesystem" ) )
		{
			UpdateProjectFilesystem( project );
		}

		if ( porting )
		{
			Step( "Finding assets" );
			CoreAssetScan.ScanAll();
			return true;
		}

		//Compiles and waits for the project in a bullshit way - this already starts happening way sooner
		Step( "Compiling projects" );

		//Timed, because it was not: everything either side of this had a scope timer on it and this did not, so fifteen seconds of a
		//start sat inside "Load Project" with nothing claiming it and the search for where a start goes was a search among the log's gaps.
		bool updated;

		using ( var _updated = timing?.ScopeTimer( $"Wait For Project Code" ) )
		{
			updated = await EditorUtility.Projects.Updated( project );
		}

		//Code failed to build: open anyway and report later; the code is picked up once it compiles
		failedToCompile = updated == false;

		if ( project.Config.Type == "addon" && !string.IsNullOrWhiteSpace( parentPackage ) )
		{
			Step( "Loading addon" );
			using var _ = timing?.ScopeTimer( $"Load Addon" );

			Log.Info( $"LoadGamePackageAsync" );
			//Install it as the active game
			await GameInstanceDll.Current.LoadGamePackageAsync( parentPackage, GameLoadingFlags.Host | GameLoadingFlags.Reload, ct );

			Log.Info( $"AssetSystem.InstallAsync" );
			//Install into asset system so we can use prefabs, gameresources, etc.
			await AssetSystem.InstallAsync( parentPackage, false );

			Log.Info( $"MountAsync" );
			//Mount our current project, and load the source!
			await project.Package.MountAsync( true );

			//Mount our current project into the filesystem and make sure to load all assets
			FileSystem.Mounted.CreateAndMount( project.GetAssetsPath() );
			await ResourceLoader.LoadAllGameResourceAsync( FileSystem.Mounted, ct, true );
		}
		else
		{
			Step( "Loading game" );
			using var _ = timing?.ScopeTimer( $"Load Game" );

			//It'd be nice to do a full end to end test, but this is as far as it'll go
			if ( Rinten.Application.IsUnitTest )
				return true;

			await GameInstanceDll.Current.LoadGamePackageAsync( project.Package.FullIdent, GameLoadingFlags.Host | GameLoadingFlags.Reload, ct );
		}


		Step( "Initializing mounts" );
		//Register custom asset types before mounting, so that assets from mounts (like .prefab files) can be resolved by the asset system.
		Log.Info( "Importing custom assets" );
		using ( var _ = timing?.ScopeTimer( $"Register CustomAssetTypes" ) )
		{
			AssetType.UpdateCustomTypes();
		}

		using ( var _ = timing?.ScopeTimer( $"Init Mounts" ) )
		{
			//Mount any mounts that are required
			await EditorUtility.Mounting.InitMountsFromConfig( project );
		}

		//Everything in the project, found by looking. Without it the editor knows about no assets at all - no browser, no pickers, nothing to open.
		{
			Step( "Finding assets" );

			using var _scan = timing?.ScopeTimer( $"ScanAssets" );

			CoreAssetScan.ScanAll();
		}

		//Import custom asset type files from content directories (including mounted ones)
		Step( "Importing custom assets" );
		using ( var _ = timing?.ScopeTimer( $"Register CustomAssetTypes" ) )
		{
			AssetType.ImportCustomTypeFiles();
		}

		//Download cloud assets afterwards, otherwise we don't have references
		Step( "Refreshing cloud assets" );
		using ( var _ = timing?.ScopeTimer( $"DownloadCloudAssets" ) )
		{
			await RefreshCloudAssets( ct );
		}

		//Ticking the asset system resolves any pendeing types and dependencies, establishing parent links for
		//transient child assets (e.g. vtex generated by a GameResource) after that we can finally prune orphans.
		using ( var _ = timing?.ScopeTimer( $"PruneOrphans" ) )
		{
			AssetSystem.Tick();
			AssetSystem.DeleteOrphans();
		}

		//Go through and compile all assets
		using ( var _ = timing?.ScopeTimer( $"CompileAllAssets" ) )
		{
			Step( "Compiling assets" );
			CompileAllAssets( porting );

			FileWatch.Tick();

			//do we even need this anymore?
		}

		return true;
	}

	//Whether the project's code failed to build as it was opened.
	static bool failedToCompile;

	//Re-run compile events once the editor is up, so the console and notification show the failure
	static void ReportFailedCompile()
	{
		if ( !failedToCompile ) return;

		failedToCompile = false;

		var errors = Project.GetCompileDiagnostics()
			.Count( x => x.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error );

		Log.Error( $"the project's code did not build - {errors} error(s). It is open without it: " +
			"the console has them, and it will pick the code up by itself once they are fixed." );

		EditorEvent.Run( "compile.started", Project.CompileGroup );
		EditorEvent.Run( "compile.complete", Project.CompileGroup );

		ShowProjectFailed();
	}

	static void UpdateProjectFilesystem( Project project )
	{
		var assetsPath = project.GetAssetsPath();
		if ( !System.IO.Directory.Exists( assetsPath ) )
			return;

		Rinten.EngineFiles.AddProjectPath( project.Config.FullIdent, project.GetAssetsPath() );

		var cloudFolder = System.IO.Path.Combine( project.GetRootPath(), Project.CacheFolder, "cloud" );
		Rinten.EngineFiles.AddCloudPath( "mod_cloud", cloudFolder );

		var transientFolder = System.IO.Path.Combine( project.GetRootPath(), Project.CacheFolder, "transient" );
		Rinten.EngineFiles.AddCloudPath( "mod_transient", transientFolder );

		//The engine ships a bunch of transient files, like image generations from the addon base,
		//and cloud assets that the menu scene uses. Mount them last, but no need in the menu project.
		if ( project.Config.Ident != "menu" )
		{
			var engineTransient = EngineFileSystem.Root.GetFullPath( "addons/menu/transients" );
			Rinten.EngineFiles.AddCloudPath( "mod_engtrans", engineTransient );
		}

		Editor.FileSystem.RebuildContentPath();

	}

	static void CompileAllAssets( bool porting )
	{
		var sw = Stopwatch.StartNew();
		var gr = AssetSystem.All
			.Where( x => !x.IsTrivialChild && x.CanRecompile && !x.IsCompiledAndUpToDate )
			.ToArray();
		if ( gr.Length == 0 ) return;

		//A port has no editor frames after this, so models compile here too. Opening the editor leaves them
		//for the frames after the window is up: one jungle FBX is minutes on the thread that should be opening.
		var now = porting
			? gr
			: gr.Where( x => !CompileOnOpen.IsModel( x.Path ) ).ToArray();
		var later = porting
			? Array.Empty<Asset>()
			: gr.Where( x => CompileOnOpen.IsModel( x.Path ) ).ToArray();

		if ( now.Length > 0 )
			Log.Info( $"Compiling {now.Length} assets" );

		var failed = CompileNow( now );

		if ( later.Length > 0 )
		{
			CompileOnOpen.Enqueue( later );
			Log.Info( $"{later.Length} models will compile after the project is open" );
			Logging.Flush();
		}

		if ( failed > 0 )
			Log.Warning( $"Compiling assets: {failed} of {now.Length} failed - the project is open anyway" );

		if ( sw.Elapsed.TotalSeconds > 2 )
			Log.Info( $"Compiling assets took {sw.Elapsed.TotalSeconds:0.000}s" );
	}

	static int CompileNow( Asset[] assets )
	{
		var failed = 0;

		for ( int i = 0; i < assets.Length; i++ )
		{
			Message = $"Compiling asset {i + 1}/{assets.Length}";
			StepProgress( (float)i / assets.Length );

			IToolsDll.Current?.Spin();

			if ( !CompileOnOpen.One( assets[i].Path, () => assets[i].Compile( false ) ) )
				failed++;
		}

		return failed;
	}

	//Download all required cloud resources for each asset in the asset system, and remove any we don't need anymore
	static async Task RefreshCloudAssets( CancellationToken token )
	{
		var sw = Stopwatch.StartNew();

		packagesToDownload.AddRange( CloudAsset.GetAssetReferences( true ) );

		//1. remove any installed packages we no longer need
		var required = new HashSet<string>();
		if ( IGameInstance.Current?.Package is RemotePackage remotePackage )
		{
			//may require a game package, don't uninstall that
			required.Add( remotePackage.FullIdent );
		}

		foreach ( var ident in packagesToDownload )
		{
			//normalise into org.ident, without version, so we can easily check below
			if ( Package.TryParseIdent( ident, out var p ) )
				required.Add( Package.FormatIdent( p.org, p.package ) );
		}

		foreach ( var package in AssetSystem.GetInstalledPackages() )
		{
			if ( required.Contains( package.FullIdent ) ) continue;

			Log.Info( $"'{package.FullIdent}' is unused, cleaning up.." );
			AssetSystem.UninstallPackage( package );
		}

		//2. make sure everything we need is installed
		await CloudAsset.Install( packagesToDownload.Distinct(), token );

		//Only when it took long enough to be worth knowing about.
		if ( sw.Elapsed.TotalSeconds > 1.0 )
			Log.Info( $"refreshing the assets took {sw.Elapsed.TotalSeconds:0.000}s" );
	}

	private static void ExportSettings( Project project )
	{
		if ( !FileSystem.ProjectSettings.FileExists( "/Input.config" ) )
		{
			if ( !project.Config.TryGetMeta<InputSettings>( "InputSettings", out var meta ) )
			{
				meta = new InputSettings();
			}

			FileSystem.ProjectSettings.WriteJson( "/Input.config", meta.Serialize() );
		}

		if ( project.Config.SetMeta( "InputSettings", null ) )
		{
			project.Save();
		}

		if ( !FileSystem.ProjectSettings.FileExists( "/Collision.config" ) )
		{
			if ( !project.Config.TryGetMeta<CollisionRules>( "Collision", out var meta ) )
			{
				meta = new CollisionRules();
			}

			EditorUtility.SaveProjectSettings( meta, "/Collision.config" );
		}

		if ( project.Config.SetMeta( "Collision", null ) )
		{
			project.Save();
		}

		if ( !FileSystem.ProjectSettings.FileExists( "/Platform.config" ) )
		{
			EditorUtility.SaveProjectSettings( new PlatformSettings(), "Platform.config" );
		}
	}

	static List<string> packagesToDownload = new();

	//Adds a package to packagesToDownload - which will be downloaded
	internal static void QueuePackageDownload( string packageName )
	{
		if ( !IsLoading )
			throw new System.Exception( "QueuePackageDownload shouldn't get called after startup" );

		packagesToDownload.Add( packageName );
	}
}
