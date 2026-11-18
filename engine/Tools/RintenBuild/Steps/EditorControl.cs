using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Rinten.Build.Constants;

namespace Rinten.Build.Steps;

//Starts, stops and waits for the editor via its MCP server (port 7269 by default)
internal class EditorControl
{
	//The editor's process name - what EngineExecutables.Editor says, spelled here because this tool doesn't load the engine.
	private const string ProcessName = "Rinten";

	private const int DefaultPort = 7269;

	private readonly int port;
	private readonly TimeSpan timeout;

	internal EditorControl( int port = DefaultPort, int timeoutSeconds = 180 )
	{
		this.port = port;
		timeout = TimeSpan.FromSeconds( timeoutSeconds );
	}

	//── Commands ──────────────────────────────────────────────────────────────

	//Start the editor on a project - or the launcher, with none - and wait until it answers and its code has compiled.
	internal ExitCode Start( string project, bool wait = true )
	{
		if ( Status() is { } running )
			return Failed( $"An editor is already answering on port {port} (process {running.ProcessId}, {running.Describe()}). Stop it first, or use another port." );

		//One that exists but doesn't answer here is either on another port - two editors on two projects is a fine way to work - or still starting
		//on this one, in which case the two fight for the port and this one loses. Said, not refused: which it is can't be told from here.
		if ( Running() is { Length: > 0 } other )
			Log.Warning( $"An editor process is already running ({string.Join( ", ", other.Select( x => x.Id ) )}) and not answering on port {port}. If it's still starting on that port, 'editor wait' is what you want instead." );

		var executable = Executable();

		if ( executable is null )
			return Failed( $"No editor at game/{ProcessName}{ExeSuffix} - build first." );

		if ( project is not null )
		{
			project = Path.GetFullPath( project );

			if ( !File.Exists( project ) )
				return Failed( $"No project file at {project}" );
		}

		var pid = Launch( executable, project );

		Log.Info( project is null
			? $"Started the editor (process {pid}) with no project - it opens the launcher."
			: $"Started the editor (process {pid}) on {project}." );

		return wait ? Wait() : ExitCode.Success;
	}

	//Ask the running editor to quit and wait for its process to go. A process that is not answering is only killed when asked with force.
	internal ExitCode Stop( bool force = false )
	{
		var status = Status();

		if ( status is not null )
		{
			Log.Info( $"Asking the editor (process {status.ProcessId}, {status.Describe()}) to quit..." );

			var reply = Call( "editor_quit" );

			if ( reply is null )
				return Failed( "The editor stopped answering before it could be asked to quit." );

			if ( WaitForExit( status.ProcessId ) )
			{
				Log.Info( "The editor has quit." );
				return ExitCode.Success;
			}

			if ( !force )
				return Failed( $"The editor (process {status.ProcessId}) was asked to quit but is still running after {timeout.TotalSeconds:0} seconds. --force kills it." );

			return Kill( status.ProcessId );
		}

		var processes = Running();

		if ( processes.Length == 0 )
		{
			Log.Info( "No editor is running." );
			return ExitCode.Success;
		}

		//Not answering. Which of the editor processes is this port's is only known from the pid the editor wrote when it
		//took the port - never guessed from the process list, where another editor on another port looks exactly the same.
		var pids = string.Join( ", ", processes.Select( x => x.Id ) );
		var owner = PortOwner();

		if ( owner is null )
			return Failed( $"An editor is running (process {pids}) but not answering on port {port}, and no live process is named in {PidFile} - it never got as far as serving that port, or it's another editor's. Which one is this port's can't be told from here; kill it yourself by pid if it's stuck, or 'editor wait' if it's starting." );

		if ( !force )
			return Failed( $"The editor on port {port} (process {owner}) is running but not answering - still starting, or stuck. --force kills it; whatever it had open and unsaved is lost." );

		return Kill( owner.Value );
	}

	//The process that took this port, from the pid file the editor writes beside its
	//log when it does - null when there is none or it names a process that is gone.
	private int? PortOwner()
	{
		int pid;

		try
		{
			if ( !File.Exists( PidFile ) || !int.TryParse( File.ReadAllText( PidFile ).Trim(), out pid ) )
				return null;
		}
		catch ( IOException )
		{
			return null;
		}

		try
		{
			//The file outlives a crash; the id in it may by now be anybody's.
			using var process = Process.GetProcessById( pid );

			if ( process.ProcessName == ProcessName )
				return pid;
		}
		catch ( ArgumentException )
		{
			//Gone
		}

		//Stale, then - a crash left it. Cleared so it can't mislead anyone later.
		try { File.Delete( PidFile ); } catch ( IOException ) { }

		return null;
	}

	private string PidFile => Path.Combine( Directory.GetCurrentDirectory(), "game", "logs", $"mcp-{port}.pid" );

	//Stop, then start again on the same project - or on the one given.
	internal ExitCode Restart( string project, bool force = false )
	{
		project ??= Status()?.ProjectFile;

		if ( Stop( force ) != ExitCode.Success )
			return ExitCode.Failure;

		return Start( project );
	}

	//Wait until the editor answers on its port and its code has compiled.
	internal ExitCode Wait()
	{
		var started = Stopwatch.StartNew();
		var lastWord = TimeSpan.Zero;
		EditorStatus status = null;

		while ( started.Elapsed < timeout )
		{
			status = Status();

			if ( status is { IsCompiling: false, IsOpening: false } )
				break;

			//Every so often, not every poll: a line a second is noise, silence is a hang.
			if ( started.Elapsed - lastWord > TimeSpan.FromSeconds( 5 ) )
			{
				lastWord = started.Elapsed;
				Log.Info( status is null
					? $"Waiting for the editor on port {port}... {started.Elapsed.TotalSeconds:0}s"
					: status.IsCompiling
						? $"The editor is up, compiling its code... {started.Elapsed.TotalSeconds:0}s"
						: $"The editor is up, opening the project... {started.Elapsed.TotalSeconds:0}s" );
			}

			Thread.Sleep( 500 );
		}

		if ( status is null )
			return Failed( $"The editor didn't answer on port {port} within {timeout.TotalSeconds:0} seconds. game/logs/Rinten.log says what it did instead." );

		if ( status.IsCompiling || status.IsOpening )
			return Failed( $"The editor is up but still {(status.IsCompiling ? "compiling" : "opening the project")} after {timeout.TotalSeconds:0} seconds." );

		Log.Info( $"The editor is ready: {status.Describe()}" );

		if ( status.LastCompileSucceeded is false )
			Log.Warning( $"Its last compile failed with {status.LastCompileErrors} error(s) - compile_status over MCP, or game/logs/Rinten.log, says where." );

		return ExitCode.Success;
	}

	//Say what is running and what it is doing.
	internal ExitCode Report()
	{
		var processes = Running();
		var status = Status();

		if ( processes.Length == 0 && status is null )
		{
			Log.Info( "No editor is running." );
			return ExitCode.Success;
		}

		if ( processes.Length > 0 )
			Log.Info( $"Editor process{(processes.Length == 1 ? "" : "es")}: {string.Join( ", ", processes.Select( x => x.Id ) )}" );

		if ( status is null )
		{
			Log.Info( $"Not answering on port {port} - still starting, another port, or stuck." );
			return ExitCode.Success;
		}

		Log.Info( $"Answering on port {port} as process {status.ProcessId}: {status.Describe()}" );

		if ( status.ProjectFile is not null )
			Log.Info( $"Project file: {status.ProjectFile}" );

		return ExitCode.Success;
	}

	//── The process ───────────────────────────────────────────────────────────

	private static string ExeSuffix => OperatingSystem.IsWindows() ? ".exe" : "";

	//The editor beside the repository's game folder, or null when it hasn't been built.
	private static string Executable()
	{
		var path = Path.Combine( Directory.GetCurrentDirectory(), "game", ProcessName + ExeSuffix );

		return File.Exists( path ) ? path : null;
	}

	//Start the editor and let go of it. It must outlive this tool and whatever shell ran this tool - an agent's shell
	//kills the process group it started when the command is done, so on Unix the editor is put in a session of its own.
	private static int Launch( string executable, string project )
	{
		var arguments = new List<string>();

		if ( project is not null )
		{
			arguments.Add( "-project" );
			arguments.Add( project );
		}

		var directory = Path.GetDirectoryName( executable )!;

		ProcessStartInfo info;

		if ( OperatingSystem.IsWindows() )
		{
			//A window of its own and no console: the editor is a windowed program and leaves nothing on this one's stdout.
			info = new ProcessStartInfo( executable )
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = directory,
			};

			foreach ( var a in arguments )
				info.ArgumentList.Add( a );
		}
		else
		{
			//Launch via sh with setsid/nohup so it outlives our shell; stdio detached, stdout kept in logs
			var logs = Path.Combine( directory, "logs" );
			Directory.CreateDirectory( logs );
			var stdout = Path.Combine( logs, $"{ProcessName}.stdout.log" );

			const string script = """
				if command -v setsid >/dev/null 2>&1; then
					exec setsid "$0" "$@" </dev/null >"$RINTEN_STDOUT" 2>&1
				else
					exec nohup "$0" "$@" </dev/null >"$RINTEN_STDOUT" 2>&1
				fi
				""";

			info = new ProcessStartInfo( "/bin/sh" )
			{
				UseShellExecute = false,
				WorkingDirectory = directory,
			};

			info.Environment["RINTEN_STDOUT"] = stdout;
			info.ArgumentList.Add( "-c" );
			info.ArgumentList.Add( script );
			info.ArgumentList.Add( executable );

			foreach ( var a in arguments )
				info.ArgumentList.Add( a );
		}

		using var process = Process.Start( info )
			?? throw new InvalidOperationException( $"{executable} didn't start" );

		//Through sh the id is the shell's, which exec replaces with the editor - so it is the editor's too, unless setsid
		//forked. Whatever it is, it's for the message: the editor is found again through its port, never by this number.
		return process.Id;
	}

	private static Process[] Running()
	{
		return Process.GetProcessesByName( ProcessName );
	}

	private bool WaitForExit( int pid )
	{
		try
		{
			using var process = Process.GetProcessById( pid );
			return process.WaitForExit( (int)timeout.TotalMilliseconds );
		}
		catch ( ArgumentException )
		{
			//Already gone
			return true;
		}
	}

	private ExitCode Kill( int pid )
	{
		try
		{
			using var process = Process.GetProcessById( pid );

			Log.Warning( $"Killing the editor (process {pid}) - whatever it had unsaved is lost." );
			process.Kill( entireProcessTree: true );

			if ( !process.WaitForExit( (int)timeout.TotalMilliseconds ) )
				return Failed( $"Process {pid} didn't die." );

			return ExitCode.Success;
		}
		catch ( ArgumentException )
		{
			return ExitCode.Success;
		}
		catch ( Exception e )
		{
			return Failed( $"Couldn't kill process {pid}: {e.Message}" );
		}
	}

	//── The editor's MCP server ───────────────────────────────────────────────

	private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds( 10 ) };

	//What editor_status says, or null when nothing answers on the port.
	private EditorStatus Status()
	{
		var reply = Call( "editor_status" );

		if ( reply?["structuredContent"] is not JsonObject content )
			return null;

		try
		{
			return content.Deserialize( BuildJson.Default.EditorStatus );
		}
		catch ( JsonException )
		{
			return null;
		}
	}

	//Call one of the editor's tools. The result object of the tool call, or null when the editor isn't there. A tool error is a failure, said in the log.
	private JsonObject Call( string tool, JsonObject arguments = null )
	{
		var request = new JsonObject
		{
			["jsonrpc"] = "2.0",
			["id"] = 1,
			["method"] = "tools/call",
			["params"] = new JsonObject
			{
				["name"] = tool,
				["arguments"] = arguments ?? new JsonObject()
			}
		};

		try
		{
			using var content = new StringContent( request.ToJsonString(), Encoding.UTF8, "application/json" );
			using var message = new HttpRequestMessage( HttpMethod.Post, $"http://127.0.0.1:{port}/mcp" ) { Content = content };
			using var response = http.Send( message );

			if ( !response.IsSuccessStatusCode )
				return null;

			using var stream = response.Content.ReadAsStream();
			var body = JsonNode.Parse( stream )?.AsObject();

			if ( body?["error"] is JsonObject error )
			{
				Log.Error( $"{tool}: {error["message"]}" );
				return null;
			}

			if ( body?["result"] is not JsonObject result )
				return null;

			if ( result["isError"]?.GetValue<bool>() == true )
			{
				Log.Error( $"{tool}: {result["content"]?[0]?["text"]}" );
				return null;
			}

			return result;
		}
		catch ( Exception e ) when ( e is HttpRequestException or TaskCanceledException or IOException or JsonException )
		{
			return null;
		}
	}

	//The part of editor_status this tool reads. Named as the editor writes it.
	internal sealed class EditorStatus
	{
		public int ProcessId { get; set; }
		public string Project { get; set; }
		public string ProjectFile { get; set; }
		public string ActiveScene { get; set; }
		public bool IsPlaying { get; set; }
		public bool IsOpening { get; set; }
		public bool IsCompiling { get; set; }
		public bool? LastCompileSucceeded { get; set; }
		public int LastCompileErrors { get; set; }

		public string Describe()
		{
			var builder = new StringBuilder();

			builder.Append( Project is null ? "no project" : $"project {Project}" );

			if ( ActiveScene is not null )
				builder.Append( $", scene {ActiveScene}" );

			if ( IsPlaying )
				builder.Append( ", playing" );

			if ( IsOpening )
				builder.Append( ", opening" );

			builder.Append( IsCompiling ? ", compiling" : LastCompileSucceeded switch
			{
				true => ", code compiled",
				false => $", last compile failed ({LastCompileErrors} errors)",
				null => ", nothing compiled yet"
			} );

			return builder.ToString();
		}
	}

	private static ExitCode Failed( string why )
	{
		Log.Error( why );
		return ExitCode.Failure;
	}
}
