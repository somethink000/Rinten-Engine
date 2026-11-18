using System;
using System.CommandLine;
using Rinten.Build.Steps;
using static Rinten.Build.Constants;

namespace Rinten.Build;

//Main entry point for the build tool
internal class Program
{
	static int Main( string[] args )
	{
		var rootCommand = new RootCommand( "rintenbuild - build and deployment tool for Rinten\n\nRun this from the repository root." );

		//Compound commands (handle multiple related steps with flags)
		AddBuildCommand( rootCommand );
		AddFormatCommand( rootCommand );

		//Individual step commands
		AddTestCommand( rootCommand );
		AddFetchNativesCommand( rootCommand );
		AddVerifyNativesCommand( rootCommand );
		AddBuildShadersCommand( rootCommand );
		AddBuildModelsCommand( rootCommand );
		AddWriteVersionCommand( rootCommand );
		AddNvPatchCommand( rootCommand );
		AddSignBinariesCommand( rootCommand );
		AddBuildAddonsCommand( rootCommand );
		AddGameCacheCommand( rootCommand );
		AddCheckNativeTouchedCommand( rootCommand );
		AddBuildExportKitsCommand( rootCommand );
		AddPackageReleaseCommand( rootCommand );
		AddSteamUploadCommand( rootCommand );
		AddDesktopEntryCommand( rootCommand );
		AddEditorCommand( rootCommand );

		rootCommand.Invoke( args );
		return Environment.ExitCode;
	}

	//── Compound commands ─────────────────────────────────────────────────────

	private static void AddBuildCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "build", "Build the native core and the managed engine" );

		var cleanOption = new Option<bool>( "--clean", getDefaultValue: () => false );
		var skipNativeOption = new Option<bool>( "--skip-native", getDefaultValue: () => false );
		var skipManagedOption = new Option<bool>( "--skip-managed", getDefaultValue: () => false );
		var noAotOption = new Option<bool>( "--no-aot", getDefaultValue: () => false );

		cmd.AddOption( cleanOption );
		cmd.AddOption( skipNativeOption );
		cmd.AddOption( skipManagedOption );
		cmd.AddOption( noAotOption );

		cmd.SetHandler( ( bool clean, bool skipNative, bool skipManaged, bool noAot ) =>
		{
			//Qualified: this tool's namespace is Rinten.Build, so a bare `Build` binds to the namespace rather than to the step class in it.
			Environment.ExitCode = (int)Steps.Build.Run( clean, skipNative, skipManaged, !noAot );
		}, cleanOption, skipNativeOption, skipManagedOption, noAotOption );

		rootCommand.Add( cmd );
	}

	private static void AddFormatCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "format", "Format all code" );
		var verifyOption = new Option<bool>( "--verify",
			description: "Verify compliance without changes",
			getDefaultValue: () => false );
		cmd.AddOption( verifyOption );
		cmd.SetHandler( ( bool verify ) =>
		{
			Environment.ExitCode = (int)FormatAll.Run( verify );
		}, verifyOption );
		rootCommand.Add( cmd );
	}

	//── Individual step commands ──────────────────────────────────────────────

	private static void AddTestCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "test", "Run tests" );
		var noBuildOption = new Option<bool>( "--no-build",
			description: "Skip building before running tests (assumes projects are already built)",
			getDefaultValue: () => false );
		var filterOption = new Option<string>( "--filter",
			description: "dotnet test filter expression, e.g. TestCategory!=LiveBackend",
			getDefaultValue: () => null );
		cmd.AddOption( noBuildOption );
		cmd.AddOption( filterOption );
		cmd.SetHandler( ( bool noBuild, string filter ) =>
		{
			Environment.ExitCode = (int)new Test( noBuild, filter ).Run();
		}, noBuildOption, filterOption );
		rootCommand.Add( cmd );
	}

	private static void AddFetchNativesCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "fetch-natives", "Download the third-party native libraries into game/bin" );

		var forceOption = new Option<bool>( "--force",
			description: "Fetch again even when the installed version already matches",
			getDefaultValue: () => false );
		var platformOption = new Option<string>( "--platform",
			description: "Platform to fetch for, e.g. win64 or linuxsteamrt64. Defaults to this machine's.",
			getDefaultValue: () => null );

		cmd.AddOption( forceOption );
		cmd.AddOption( platformOption );

		cmd.SetHandler( ( bool force, string platform ) =>
		{
			Environment.ExitCode = (int)new FetchNatives( force, platform ).Run();
		}, forceOption, platformOption );

		rootCommand.Add( cmd );
	}

	private static void AddVerifyNativesCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "verify-natives",
			"Check that every native the engine opens by name is in game/bin" );

		var platformOption = new Option<string>( "--platform",
			description: "Platform to check, e.g. win64 or osxarm64. Defaults to this machine's.",
			getDefaultValue: () => null );

		cmd.AddOption( platformOption );

		cmd.SetHandler( ( string platform ) =>
		{
			Environment.ExitCode = (int)new VerifyNatives( platform ).Run();
		}, platformOption );

		rootCommand.Add( cmd );
	}

	private static void AddBuildShadersCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "build-shaders", "Build shaders" );
		var forcedOption = new Option<bool>( "--forced",
			description: "Force rebuild all shaders",
			getDefaultValue: () => false );
		cmd.AddOption( forcedOption );
		cmd.SetHandler( ( bool forced ) =>
		{
			Environment.ExitCode = (int)new BuildShaders( forced ).Run();
		}, forcedOption );
		rootCommand.Add( cmd );
	}

	private static void AddBuildModelsCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "build-models", "Build the models the engine ships, for a release" );
		cmd.SetHandler( () => { Environment.ExitCode = (int)new BuildModels().Run(); } );
		rootCommand.Add( cmd );
	}

	private static void AddWriteVersionCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "write-version", "Write version information to game/.version" );
		cmd.SetHandler( () => { Environment.ExitCode = (int)new WriteVersion().Run(); } );
		rootCommand.Add( cmd );
	}

	private static void AddNvPatchCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "nvpatch", "Apply NvPatch to game executables" );
		cmd.SetHandler( () => { Environment.ExitCode = (int)new NvPatch().Run(); } );
		rootCommand.Add( cmd );
	}

	private static void AddSignBinariesCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "sign-binaries", "Sign game binaries using Azure Trusted Signing" );
		cmd.SetHandler( () => { Environment.ExitCode = (int)new SignBinaries().Run(); } );
		rootCommand.Add( cmd );
	}

	private static void AddBuildAddonsCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "build-addons", "Build addons and menu" );
		cmd.SetHandler( () => { Environment.ExitCode = (int)new BuildAddons().Run(); } );
		rootCommand.Add( cmd );
	}

	private static void AddGameCacheCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "game-cache", "Create game cache" );
		cmd.SetHandler( () => { Environment.ExitCode = (int)new GameCache().Run(); } );
		rootCommand.Add( cmd );
	}

	private static void AddCheckNativeTouchedCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "check-native-touched", "Check if the PR touches native code; writes native_touched output" );
		cmd.SetHandler( () =>
		{
			Environment.ExitCode = (int)CheckNativeTouched.Run();
		} );
		rootCommand.Add( cmd );
	}

	private static void AddDesktopEntryCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "desktop-entry",
			"Install this engine's menu entry, icon and .rinproj file type on this machine" );

		var removeOption = new Option<bool>( "--remove",
			description: "Take the entry, the icons and the file type out again",
			getDefaultValue: () => false );
		var statusOption = new Option<bool>( "--status",
			description: "Say what is installed and whether it points at this engine",
			getDefaultValue: () => false );

		cmd.AddOption( removeOption );
		cmd.AddOption( statusOption );

		cmd.SetHandler( ( bool remove, bool status ) =>
		{
			Environment.ExitCode = (int)new DesktopEntry( remove, status ).Run();
		}, removeOption, statusOption );

		rootCommand.Add( cmd );
	}

	private static void AddEditorCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "editor",
			"Start, stop and wait for the editor from a shell, through the MCP server it runs - for the rebuild loop, where the editor has to be gone before a build and back, answering, after it" );

		var portOption = new Option<int>( "--port",
			description: "The editor's MCP port, as set in Editor -> Preferences",
			getDefaultValue: () => 7269 );
		var timeoutOption = new Option<int>( "--timeout",
			description: "Seconds to wait for the editor to answer, compile, or quit",
			getDefaultValue: () => 180 );
		var forceOption = new Option<bool>( "--force",
			description: "Kill an editor that doesn't answer or won't quit; whatever it had unsaved is lost",
			getDefaultValue: () => false );
		var noWaitOption = new Option<bool>( "--no-wait",
			description: "Return as soon as the process is started, without waiting for it to answer",
			getDefaultValue: () => false );
		var projectArgument = new Argument<string>( "project",
			description: "The .rinproj to open; the launcher with none" )
		{ Arity = ArgumentArity.ZeroOrOne };

		cmd.AddGlobalOption( portOption );
		cmd.AddGlobalOption( timeoutOption );

		var start = new Command( "start", "Start the editor on a project and wait until it answers with its code compiled" );
		start.AddArgument( projectArgument );
		start.AddOption( noWaitOption );
		start.SetHandler( ( string project, int port, int timeout, bool noWait ) =>
		{
			Environment.ExitCode = (int)new EditorControl( port, timeout ).Start( project, wait: !noWait );
		}, projectArgument, portOption, timeoutOption, noWaitOption );

		var stop = new Command( "stop", "Ask the editor to quit - it writes everything out - and wait for its process to go" );
		stop.AddOption( forceOption );
		stop.SetHandler( ( int port, int timeout, bool force ) =>
		{
			Environment.ExitCode = (int)new EditorControl( port, timeout ).Stop( force );
		}, portOption, timeoutOption, forceOption );

		var restart = new Command( "restart", "Stop the editor and start it again on the same project, or on the one given" );
		restart.AddArgument( projectArgument );
		restart.AddOption( forceOption );
		restart.SetHandler( ( string project, int port, int timeout, bool force ) =>
		{
			Environment.ExitCode = (int)new EditorControl( port, timeout ).Restart( project, force );
		}, projectArgument, portOption, timeoutOption, forceOption );

		var wait = new Command( "wait", "Wait until the editor answers and its code has compiled" );
		wait.SetHandler( ( int port, int timeout ) =>
		{
			Environment.ExitCode = (int)new EditorControl( port, timeout ).Wait();
		}, portOption, timeoutOption );

		var status = new Command( "status", "Say whether an editor is running and what it is doing" );
		status.SetHandler( ( int port, int timeout ) =>
		{
			Environment.ExitCode = (int)new EditorControl( port, timeout ).Report();
		}, portOption, timeoutOption );

		cmd.AddCommand( start );
		cmd.AddCommand( stop );
		cmd.AddCommand( restart );
		cmd.AddCommand( wait );
		cmd.AddCommand( status );

		rootCommand.Add( cmd );
	}

	private static void AddSteamUploadCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "steam-upload", "Stage a release build and push it to the Steam depot with steamcmd" );

		var appOption = new Option<string>( "--app", description: "Steam app id", getDefaultValue: () => SteamUpload.DefaultAppId );
		var depotOption = new Option<string>( "--depot", description: "Steam depot id", getDefaultValue: () => SteamUpload.DefaultDepotId );
		var branchOption = new Option<string>( "--branch",
			description: "Branch to set the build live on. Defaults to $RINTEN_STEAM_BRANCH; with neither, the build is uploaded but not set live.",
			getDefaultValue: () => null );
		var userOption = new Option<string>( "--user",
			description: "Steam build account. Defaults to $RINTEN_STEAM_USER.",
			getDefaultValue: () => null );

		cmd.AddOption( appOption );
		cmd.AddOption( depotOption );
		cmd.AddOption( branchOption );
		cmd.AddOption( userOption );

		cmd.SetHandler( ( string app, string depot, string branch, string user ) =>
		{
			Environment.ExitCode = (int)new SteamUpload( app, depot, branch, user ).Run();
		}, appOption, depotOption, branchOption, userOption );

		rootCommand.Add( cmd );
	}

	private static void AddBuildExportKitsCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "build-export-kits", "Compile the web and Android hosts for a release, which ships no sources" );

		var outputOption = new Option<string>( "--output",
			description: "Directory the kits land in, relative to the repo root; package-release reads it",
			getDefaultValue: () => "dist/kits" );

		cmd.AddOption( outputOption );

		cmd.SetHandler( ( string output ) =>
		{
			Environment.ExitCode = (int)new BuildExportKits( output ).Run();
		}, outputOption );

		rootCommand.Add( cmd );
	}

	private static void AddPackageReleaseCommand( RootCommand rootCommand )
	{
		var cmd = new Command( "package-release", "Assemble a distributable engine build: binaries at the top, everything else in one engine/ folder, zipped for the release API" );

		var versionOption = new Option<string>( "--version",
			description: "Version stamped into the folder and zip name",
			getDefaultValue: () => "0.0.0-dev" );

		var outputOption = new Option<string>( "--output",
			description: "Directory the staged folder and zip land in, relative to the repo root",
			getDefaultValue: () => "dist" );

		cmd.AddOption( versionOption );
		cmd.AddOption( outputOption );

		cmd.SetHandler( ( string version, string output ) =>
		{
			Environment.ExitCode = (int)new PackageRelease().Run( version, output );
		}, versionOption, outputOption );

		rootCommand.Add( cmd );
	}

}

