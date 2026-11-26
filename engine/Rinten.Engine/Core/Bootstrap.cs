using NativeEngine;
using Rinten.Network;
using Rinten.Utility;
using Rinten.VR;
using System.Diagnostics;
using System.Threading;

namespace Rinten.Engine;

[SkipHotload]
internal static class Bootstrap
{
	//The github SHA of the current build
	internal static string VersionSha { get; private set; }

	private static readonly Logger log = Logging.GetLogger();

	internal static Api.Events.EventRecord StartupTiming;

	/// <summary>The same startup, for a process with no native runtime in it.</summary>
	public static void PreInitCore( string gameRoot, bool headless, bool standalone = true, bool dedicated = false, bool editor = false )
	{
		Application.Initialize( dedicated, headless, toolsMode: editor, testMode: false, isRetail: false );
		Application.IsStandalone = standalone;

		PreInitCommon( gameRoot );
	}

	internal static void PreInitWebFilesystem( string gameRoot )
	{
		Application.Initialize( false, false, toolsMode: false, testMode: false, isRetail: false );
		Application.IsStandalone = true;

		InitMinimal( gameRoot );
		DLLImportResolver.SetupResolvers();
		SyncContext.Init();

		Logging.Enabled = true;
		Logging.PrintToConsole = true;

		EngineFileSystem.InitializeAddonsFolder();
		EngineFileSystem.InitializeDataFolder();
	}

	internal static void PreInitWebHost()
	{
		IGameInstanceDll.Current?.Bootstrap();
	}

	private static void PreInitCommon( string gameRoot )
	{
		try
		{
			InitMinimal( gameRoot );

			DLLImportResolver.SetupResolvers();

			StartupTiming = new Api.Events.EventRecord( $"StartupTiming.{(Application.IsEditor ? "Editor" : (Application.IsHeadless ? "Server" : "Game"))}" );
			StartupTiming.StartTimer( "Time" );

			//This also calls SetSynchronizationContext
			SyncContext.Init();

			if ( Thread.CurrentThread.CurrentCulture.Name != "en-US" )
			{
				var culture = System.Globalization.CultureInfo.CreateSpecificCulture( "en-US" );
				Thread.CurrentThread.CurrentCulture = culture;
				Thread.CurrentThread.CurrentUICulture = culture;
			}

			Logging.Enabled = true;
			Logging.OnException = _ => Application.ExceptionCount++;
			Logging.PrintToConsole = Application.IsHeadless;

			//As soon as there is a log to say it in: what a shipped game loads that only the editor should, and who asked for it.
			Diagnostics.ShippedBuild.Watch();

			{
				using var timerFs = StartupTiming?.ScopeTimer( "FilesystemInit" );

				EngineFileSystem.InitializeAddonsFolder();
				EngineFileSystem.InitializeDataFolder();

				if ( !Application.IsStandalone )
				{
					EngineFileSystem.InitializeDownloadsFolder();

					string assetdownloadFolder = "/assets";
					EngineFileSystem.DownloadedFiles.CreateDirectory( assetdownloadFolder );

					AssetDownloadCache.Initialize( EngineFileSystem.DownloadedFiles.GetFullPath( assetdownloadFolder ) );
				}
			}

			Api.Init();

			if ( Application.IsStandalone )
			{
				IGameInstanceDll.Current?.Bootstrap();
			}
			else
			{
				using ( var _ = StartupTiming?.ScopeTimer( "Menu PreInit Bootstrap" ) )
				{
					IMenuDll.Current?.Bootstrap();
				}
				using ( var _ = StartupTiming?.ScopeTimer( "Game PreInit Bootstrap" ) )
				{
					IGameInstanceDll.Current?.Bootstrap();
				}
				using ( var _ = StartupTiming?.ScopeTimer( "Tools PreInit Bootstrap" ) )
				{
					IToolsDll.Current?.Bootstrap();
				}

				Mounting.Directory.LoadAssemblies();
			}

			//Network modules, if the build ships any (net/x/x.dll). A game for itch.io or the web ships none, and this finds nothing and loads nothing.
			global::Rinten.Network.NetworkProviders.LoadAssemblies();
		}
		catch ( Exception ex )
		{
			if ( Application.OnWeb )
				throw;

			Log.Error( ex );
			SayEach( ex );

			//An aggregate says nothing about where it went wrong; what is inside it does, and this is the one place anybody sees it.
			for ( var inner = ex.InnerException; inner is not null; inner = inner.InnerException )
			{
				Log.Error( $"caused by: {inner}" );
			}
			Die( "Bootstrap::PreInit Error", $"Failed to bootstrap engine: {ex.Message}\n\n{ex.StackTrace}" );
		}
	}

	//Ends the process. A native runtime must be torn down by its own exit, not .NET's: its global
	//destructors do not survive being run. With no native runtime there is nothing to be careful of.
	private static void Exit( int code )
	{
		CoreUiWindow.DropDisplay();
		Environment.Exit( code );
	}

	//A fatal error: let the GPU go first, then write it down, then leave. The box is the editor's, as it is in AppSystem.CrashShutdown: a player handed
	//the engine's own words about why it would not start can do nothing with them. The log has all of it either way, which is what gets sent on.
	private static void Die( string title, string message )
	{
		if ( Application.OnWeb )
			throw new InvalidOperationException( $"{title}: {message}" );

		try { log.Error( $"{title}: {message}" ); } catch { }

		CoreUiWindow.DropDisplay();

		if ( Application.IsEditor && !Application.IsHeadless )
		{
			try { EnginePlatform.MessageBox( title, message ); } catch { }
		}

		try { NLog.LogManager.Shutdown(); } catch { }

		Environment.Exit( 1 );
	}

	//Let's native exit the C# app so AppDomain.ProcessExit gets called
	internal static void EnvironmentExit( int nCode )
	{
		//Whatever is still in the log's buffer, on its way out.
		try { NLog.LogManager.Shutdown(); } catch { }

		//Environment.Exit would run C++ global destructors, which break the native runtime;
		//it relies on the process being terminated abruptly with no cleanup.
		//Environment.Exit( nCode );
	}

	//Called on exceptions from a task (delayed, because it'll only get called when the exception gets collected) TODO: Move this somewhere else
	private static void TaskScheduler_UnobservedTaskException( object sender, UnobservedTaskExceptionEventArgs e )
	{
		var exception = e.Exception.Flatten();

		log.Error( exception );

		foreach ( var ex in exception.InnerExceptions )
		{
			log.Error( ex );
		}
	}

	//Called to initialize the engine.
	internal static void Init()
	{
		try
		{
			IToolsDll.Current?.Spin();

			if ( !Application.OnWeb )
			{
				using ( var _ = StartupTiming?.ScopeTimer( $"Static Constructors" ) )
				{
					ReflectionUtility.RunAllStaticConstructors( "Rinten.System" );
					ReflectionUtility.RunAllStaticConstructors( "Rinten.Engine" );
				}

				using ( var _ = StartupTiming?.ScopeTimer( $"BuiltIn Projects Init" ) )
					SyncContext.RunBlocking( Project.InitializeBuiltIn() );
			}

			if ( !Application.OnWeb )
			{
				using var _ = StartupTiming?.ScopeTimer( $"Engine ConVars" );
				InitEngineConVars();
			}

			if ( IToolsDll.Current is not null )
			{
				using var x = StartupTiming?.ScopeTimer( $"IToolsDll Bootstrap Init" );
				SyncContext.RunBlocking( IToolsDll.Current.Initialize() );
			}

			VRSystem.Init();

			Screen.UpdateFromEngine();

			if ( IMenuDll.Current is not null )
			{
				using var x = StartupTiming?.ScopeTimer( $"MenuBootstrap" );
				SyncContext.RunBlocking( IMenuDll.Current.Initialize() );
			}

			if ( IGameInstanceDll.Current is not null && !Application.OnWeb )
			{
				using var x = StartupTiming?.ScopeTimer( $"IGameMenuDll Bootstrap" );
				SyncContext.RunBlocking( IGameInstanceDll.Current.Initialize() );
			}

			Internal.TypeLibrary.OnClassName = x => StringToken.FindOrCreate( x );

			if ( IToolsDll.Current is not null )
			{
				using var x = StartupTiming?.ScopeTimer( $"Start Editor" );
				SyncContext.RunBlocking( IToolsDll.Current.LoadProject() );
			}

			if ( !Application.IsHeadless )
			{
				LoadingFinished();
			}

			//Run any commands
			foreach ( var sw in CommandLine.GetSwitches() )
			{
				var cmd = ConVarSystem.Find( sw.Key );
				if ( cmd is null || !cmd.IsConCommand ) continue;

				//A shipped game loads its own package first, which would drop a session; it hosts or connects after - see CoreAppSystem
				if ( Standalone.Manifest is not null && sw.Key is "connect" or "host" ) continue;

				cmd.Run( sw.Value );
			}

			if ( Application.IsEditor )
			{
				Log.Info( "Bootstrap Init Done" );
			}

			//Networking bootstrap
			Networking.Bootstrap();

			if ( Application.IsJoinLocal )
			{
				NetworkConsoleCommands.ConnectToServer( "local" );
			}
		}
		catch ( Exception ex )
		{
			if ( Application.OnWeb )
				throw;

			Log.Error( ex );
			SayEach( ex );

			var diagnostics = Project.CompileErrorsText();

			Die( "Bootstrap::Init Error", $"""
				Failed to bootstrap engine.
				
				{(string.IsNullOrEmpty( diagnostics ) ? ex : diagnostics)}

				This either means that we've messed something up, or you've edited a base addon - in that case, verify your game files.
				Take a look at your Log files if you're still having problems.
				""" );
		}
	}

	//Logs each inner exception of a failure by name and cause, since aggregate messages hide the why
	private static void SayEach( Exception ex )
	{
		if ( ex is not AggregateException several ) return;

		foreach ( var one in several.Flatten().InnerExceptions )
		{
			var cause = one;
			while ( cause.InnerException is not null ) cause = cause.InnerException;

			log.Error( $"{one.Message} - {cause.GetType().Name}: {cause.Message}" );
		}
	}

	internal static void InitMinimal( string rootFolder )
	{
		if ( !Application.OnWeb )
			Environment.CurrentDirectory = rootFolder;

		ThreadSafe.MarkMainThread();

		if ( !Application.OnWeb )
			ThreadPool.SetMinThreads( Environment.ProcessorCount, Environment.ProcessorCount );

		TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
		AppDomain.CurrentDomain.UnhandledException += ( _, args ) =>
		{
			Log.Error( args.ExceptionObject as Exception, "AppDomain unhandled exception" );

			//Written out before the process is gone. A log kept open buffers, and a run that ends here
			//ended mid-sentence - which is exactly the log somebody is about to be asked to send.
			if ( args.IsTerminating ) try { NLog.LogManager.Flush(); } catch { }

			//The GPU let go of before the process goes - and only then. A page is not ended by one: an exception out of
			//a task was said here and the runtime went on, with the device shut and every frame after it asking for one.
			if ( args.IsTerminating ) CoreUiWindow.DropDisplay();
		};

		//System.Net.ServicePointManager.ServerCertificateValidationCallback += ( sender, cert, chain, sslPolicyErrors ) => true;

		EngineFileSystem.Initialize( Application.OnWeb ? "." : Environment.CurrentDirectory );
		EngineFileSystem.InitializeConfigFolder();
	}

	//Should be called when startup has finished. If we have a client, this is when the menu is first entered.
	internal static void LoadingFinished()
	{
		if ( StartupTiming != null )
		{
			StartupTiming.FinishTimer( "Time" );
			StartupTiming.SetValue( "package.ident", Application.GameIdent );

			//Startup timings written to the log too, not just sent to telemetry
			StartupTiming.WriteTo( Log );

			StartupTiming.Submit( true );
		}

	}

	static void InitEngineConVars()
	{
		ConVarSystem.AddAssembly( typeof( Bootstrap ).Assembly, "engine" );

		//The quality profiles ran before this and left their values behind by name. Whatever is still unclaimed is a name nothing registers.
		ConVarSystem.ReportUnclaimed();
	}
}
