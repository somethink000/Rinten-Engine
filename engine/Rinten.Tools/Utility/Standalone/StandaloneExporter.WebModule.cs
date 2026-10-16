using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Editor;

//Compiles engine, base and game code ahead of time into one WebAssembly module via NativeAOT-LLVM
partial class StandaloneExporter
{
	//The launcher project the module is compiled from.
	private static string WebModuleProject
		=> Path.Combine( EngineSource(), "Launcher", "RintenWeb", "RintenWeb.csproj" );

	//What the module's build leaves for the page: the runtime's scripts and the module.
	private static readonly string[] ModuleFiles = ["dotnet.js", "dotnet.native.js", "dotnet.runtime.js", "dotnet.diagnostics.js", "dotnet.native.wasm"];

	//The module packed, which PackModule writes and keeps beside it - placed where it was kept.
	private static readonly string[] PackedModuleFiles = ["dotnet.native.wasm.gz", "dotnet.native.wasm.br"];

	//Where this export's module is kept, which its packed copies join (PackModule).
	private string keptModule;

	//Compiles the module with the game's code in it and puts it in _framework . False, having said why, where it would not build.
	private async Task<bool> CompileWebModule( string target )
	{
		var project = WebModuleProject;
		var gameBin = Path.Combine( target, WebBuild.AssemblyFolder );
		var work = Path.Combine( Path.GetDirectoryName( project ), "obj", "export" );
		var output = Path.Combine( work, "out" );
		var native = Path.Combine( work, "native" );

		//Fresh each time: the compiler's own up-to-date check looks at the project, not at
		//the game's code or the directives beside it, and called a new game's module done.
		foreach ( var folder in new[] { output, native } )
			if ( Directory.Exists( folder ) ) Directory.Delete( folder, true );

		Directory.CreateDirectory( work );

		//What the serializer makes generic over the game's values, which the compiler has to be told - see WebDirectives. Asked of the game's own library.
		var directives = Path.Combine( work, "rd.xml" );
		var descriptor = Path.Combine( work, "kept.xml" );
		HashSet<string> inModule = null;

		await OnMainThread( () =>
		{
			//Only what the module will hold: the editor's library has its tools in it too.
			inModule = ModuleAssemblies( gameBin );
			var types = Rinten.Game.TypeLibrary.GetTypes().Select( x => x.TargetType )
				.Where( x => x is not null && inModule.Contains( x.Assembly.GetName().Name ) ).ToList();

			//And what the page itself reads: its manifest.
			types.Add( typeof( Rinten.WebManifest ) );

			var made = Rinten.Json.MadeGenerics( types, out var described );
			//Keep library types and serializer-described types whole; the rest is trimmed to what is called
			static bool Ours( string name ) => !name.StartsWith( "System", StringComparison.Ordinal ) && !name.StartsWith( "Microsoft", StringComparison.Ordinal ) && name != "netstandard" && name != "mscorlib";
			var kept = types.Concat( described )
				.Where( x => !x.IsGenericType && !x.IsArray && inModule.Contains( x.Assembly.GetName().Name ) && Ours( x.Assembly.GetName().Name ) )
				.Distinct().ToList();
			File.WriteAllText( directives, WebDirectives.For( made, inModule ) );
			File.WriteAllText( descriptor, WebDirectives.Descriptor( kept, inModule ) );
			Logger.Info( $"web module: {made.Count} serializer generics from {types.Count} types" );
		} );

		//The same inputs make the same module, and making it is most of an export: a game exported again with only its content changed takes the last one.
		var key = ModuleKey( project, gameBin, [directives, descriptor], inModule );
		var last = Path.Combine( work, "kept", key );

		if ( ModuleFiles.All( x => File.Exists( Path.Combine( last, x ) ) ) )
		{
			Logger.Info( "web module: the engine, the game's code and the directives are as they were - the last module is reused" );
			keptModule = last;
			return PlaceModule( last, target, gameBin );
		}

		var emscripten = EmscriptenTools();

		//The ceiling is what is free, within reason; the LLVM compiles after the compiler share it, as many as fit
		var ceiling = Math.Clamp( (int)( ( AvailableMemoryGB() ?? MaxCeilingGB ) - 0.5 ), ModuleMemoryGB, MaxCeilingGB );
		var compiles = Math.Clamp( ( ceiling - 1 ) / LlvmCompileGB, 1, Math.Max( 1, Environment.ProcessorCount / 2 ) );

		var arguments = $"publish \"{project}\" -r browser-wasm -c Release -m:{compiles} -p:IlcSingleThreaded=true " +
			"-p:BuildProjectReferences=false " +
			$"-p:RintenGameBin=\"{gameBin}\" -p:RintenRdXml=\"{directives}\" -p:RintenDescriptor=\"{descriptor}\" " +
			$"-p:RintenWasmBin=\"{WasmBin}{Path.DirectorySeparatorChar}\" " +
			$"-p:NativeIntermediateOutputPath=\"{native}{Path.DirectorySeparatorChar}\" -p:NativeOutputPath=\"{output}{Path.DirectorySeparatorChar}\"";

		var clock = Stopwatch.StartNew();
		Logger.Info( "web module: compiling the engine and the game ahead of time (several minutes).." );

		var built = await Task.Run( () => RunDotnet( arguments, emscripten, ceiling, out var log ) || Failed( log ) );

		if ( !built ) return false;

		//A compiler stopped at the memory ceiling leaves the SDK saying it succeeded, with nothing written
		if ( !ModuleFiles.All( x => File.Exists( Path.Combine( output, x ) ) ) )
		{
			Problem( $"no web module: the build ended without writing it - most often the compiler was stopped past the {ceiling} GB " +
				"of memory it was given; close what else is running and export again" );
			return false;
		}

		Logger.Info( $"web module: compiled in {clock.Elapsed.TotalMinutes:F1} min" );

		if ( !PlaceModule( output, target, gameBin ) ) return false;

		//Only the last one is kept: a module is a hundred megabytes.
		var keptRoot = Path.Combine( work, "kept" );
		if ( Directory.Exists( keptRoot ) ) Directory.Delete( keptRoot, true );
		Directory.CreateDirectory( last );
		foreach ( var name in ModuleFiles ) File.Copy( Path.Combine( output, name ), Path.Combine( last, name ) );
		keptModule = last;

		return true;

		bool Failed( string log )
		{
			Problem( "no web module: it would not compile - see the log" );
			Logger.Warning( $"the web module did not build:\n{log}" );
			return false;
		}
	}

	//Whether the module can be compiled here at all; says why not. A module kept from an earlier export needs no memory
	private async Task<bool> WebModuleCanBuild()
	{
		var project = WebModuleProject;

		if ( !File.Exists( project ) )
		{
			Problem( $"no web module: {project} is not here" );
			return false;
		}

		if ( EmscriptenTools() is null )
		{
			Problem( "no web module: Emscripten is not installed - install the web tools on the Build page" );
			return false;
		}

		var kept = Path.Combine( Path.GetDirectoryName( project ), "obj", "export", "kept" );
		if ( Directory.Exists( kept ) && Directory.EnumerateDirectories( kept ).Any() ) return true;

		await OnMainThread( IdleReclaim.Now );

		if ( AvailableMemoryGB() is { } gb && gb < ModuleMemoryGB )
		{
			Problem( $"no web module: {gb:F1} GB of memory free and compiling it takes {ModuleMemoryGB} - close something and export again" );
			return false;
		}

		return true;
	}

	//Puts a compiled module into the build, from from.
	private bool PlaceModule( string from, string target, string gameBin )
	{
		var into = Path.Combine( target, "_framework" );
		if ( Directory.Exists( into ) ) Directory.Delete( into, true );
		Directory.CreateDirectory( into );

		foreach ( var name in ModuleFiles )
		{
			var source = Path.Combine( from, name );

			if ( !File.Exists( source ) )
			{
				Problem( $"no web module: the build wrote no {name}" );
				return false;
			}

			File.Copy( source, Path.Combine( into, name ), true );
		}

		foreach ( var name in PackedModuleFiles )
			if ( File.Exists( Path.Combine( from, name ) ) )
				File.Copy( Path.Combine( from, name ), Path.Combine( into, name ), true );

		//The game's assemblies are inside the module now; the page has nothing to load them with, and the IL would only be a download nobody reads.
		foreach ( var dll in Directory.GetFiles( gameBin, "*.dll" ) ) File.Delete( dll );

		return true;
	}

	//The core's archive and the player's scripts, under the engine.
	private static string WasmBin => Path.Combine( Environment.CurrentDirectory, ExportPlatform.Web.BinFolder );

	//One hash over everything the module is compiled from, to skip rebuilding an unchanged module
	private static string ModuleKey( string project, string gameBin, string[] directives, HashSet<string> inModule )
	{
		var player = Path.GetDirectoryName( project );
		var managed = Path.GetDirectoryName( typeof( Rinten.Scene ).Assembly.Location );

		var files = new List<string>();
		files.AddRange( Directory.GetFiles( player, "*.cs" ) );
		files.AddRange( Directory.GetFiles( player, "*.csproj" ) );
		files.AddRange( Directory.GetFiles( player, "*.props" ) );
		if ( Directory.Exists( Path.Combine( player, "prebuilt" ) ) ) files.AddRange( Directory.GetFiles( Path.Combine( player, "prebuilt" ), "*.dll" ) );

		//A release kit's references that stayed in bin/managed
		var shared = Path.Combine( player, "prebuilt", "Shared.props" );
		if ( File.Exists( shared ) )
			files.AddRange( System.Text.RegularExpressions.Regex.Matches( File.ReadAllText( shared ), @"bin/managed/([^""]+\.dll)" ).Select( m => Path.Combine( managed, m.Groups[1].Value ) ) );
		files.AddRange( directives );
		files.AddRange( Directory.GetFiles( gameBin, "*.dll" ) );
		files.AddRange( inModule.Select( x => Path.Combine( managed, x + ".dll" ) ).Where( File.Exists ) );
		files.Add( Path.Combine( WasmBin, "rintencore.a" ) );

		using var hash = System.Security.Cryptography.IncrementalHash.CreateHash( System.Security.Cryptography.HashAlgorithmName.SHA256 );

		foreach ( var file in files.Where( File.Exists ).Select( Path.GetFullPath ).Distinct().Order( StringComparer.Ordinal ) )
		{
			hash.AppendData( System.Text.Encoding.UTF8.GetBytes( Path.GetFileName( file ) + "\n" ) );
			hash.AppendData( File.ReadAllBytes( file ) );
		}

		return Convert.ToHexString( hash.GetHashAndReset() )[..16].ToLowerInvariant();
	}

	//The assemblies the module is made of, by name: the engine and everything it references, the game's own, and .NET's - which a directive
	//may name and the compiler will find. Anything else the editor has loaded (its tools, the tool addons) is not there to be compiled.
	private static HashSet<string> ModuleAssemblies( string gameBin )
	{
		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var walk = new Queue<System.Reflection.Assembly>();

		void Take( System.Reflection.Assembly assembly )
		{
			if ( assembly is not null && names.Add( assembly.GetName().Name ) ) walk.Enqueue( assembly );
		}

		Take( typeof( Rinten.Scene ).Assembly );
		Take( AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault( x => x.GetName().Name == "Rinten.GameInstance" ) );

		foreach ( var dll in Directory.GetFiles( gameBin, "*.dll" ) )
		{
			var name = Path.GetFileNameWithoutExtension( dll );
			Take( AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault( x => x.GetName().Name == name ) );
			names.Add( name );
		}

		while ( walk.Count > 0 )
		{
			foreach ( var reference in walk.Dequeue().GetReferencedAssemblies() )
			{
				if ( names.Contains( reference.Name ) ) continue;

				System.Reflection.Assembly loaded = null;
				try { loaded = System.Reflection.Assembly.Load( reference ); } catch { }

				if ( loaded is null ) names.Add( reference.Name );
				else Take( loaded );
			}
		}

		return names;
	}

	//The SDK's Emscripten pack, as the module's build reads it: the tools folder, and the
	//environment the pack's own config expects. Null where the workload is not installed.
	private static Dictionary<string, string> EmscriptenTools()
	{
		var roots = ExportToolchain.PackRoots().ToArray();
		var host = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsMacOS() ? "osx-arm64" : "linux-x64";

		string Pack( string kind )
		{
			foreach ( var root in roots.Where( Directory.Exists ) )
			{
				var newest = Directory.GetDirectories( root, $"Microsoft.NET.Runtime.Emscripten.*.{kind}.{host}" )
					.SelectMany( Directory.GetDirectories )
					.OrderByDescending( x => x, StringComparer.Ordinal )
					.FirstOrDefault();

				if ( newest is not null ) return newest;
			}

			return null;
		}

		var sdk = Pack( "Sdk" );
		var node = Pack( "Node" );
		var cache = Pack( "Cache" );

		if ( sdk is null || node is null || cache is null ) return null;

		var tools = Path.Combine( sdk, "tools" );

		return new Dictionary<string, string>
		{
			["EmscriptenSdkToolsPath"] = tools + Path.DirectorySeparatorChar,
			["DOTNET_EMSCRIPTEN_LLVM_ROOT"] = Path.Combine( tools, "bin" ),
			["DOTNET_EMSCRIPTEN_BINARYEN_ROOT"] = tools,
			["DOTNET_EMSCRIPTEN_NODE_JS"] = Path.Combine( node, "tools", "bin", "node" ),
			["EM_CONFIG"] = Path.Combine( tools, "emscripten", ".emscripten" ),
			["EM_CACHE"] = Path.Combine( cache, "tools", "emscripten", "cache" ),
			["PATH"] = string.Join( Path.PathSeparator, Path.Combine( tools, "emscripten" ), Path.Combine( tools, "bin" ),
				Path.Combine( node, "tools", "bin" ), Environment.GetEnvironmentVariable( "PATH" ) ?? "" ),
		};
	}

	//What the module's build needs, in GB: its peak is the single-threaded compiler, 4.95 untrimmed (2026-10-10, /usr/bin/time -v)
	private const int ModuleMemoryGB = 5;

	//The most the build is given even with more free, in GB.
	private const int MaxCeilingGB = 9;

	//What one LLVM compile of the module holds at its peak, in gigabytes: the largest of the sixteen parts measured 1.96 (2026-10-05, /usr/bin/time -v).
	private const int LlvmCompileGB = 2;

	//What the system says is available, in gigabytes; null where it will not say.
	private static double? AvailableMemoryGB()
	{
		if ( OperatingSystem.IsWindows() )
		{
			var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
			return GlobalMemoryStatusEx( ref status ) ? status.AvailablePhysical / 1073741824.0 : null;
		}

		try
		{
			foreach ( var line in File.ReadLines( "/proc/meminfo" ) )
			{
				if ( !line.StartsWith( "MemAvailable:", StringComparison.Ordinal ) ) continue;

				var kb = long.Parse( line.Split( ' ', StringSplitOptions.RemoveEmptyEntries )[1] );
				return kb / 1048576.0;
			}
		}
		catch
		{
			//No /proc: nothing to check by.
		}

		return null;
	}

	//MEMORYSTATUSEX
	[StructLayout( LayoutKind.Sequential )]
	private struct MemoryStatus
	{
		public uint Length;
		public uint MemoryLoad;
		public ulong TotalPhysical;
		public ulong AvailablePhysical;
		public ulong TotalPageFile;
		public ulong AvailablePageFile;
		public ulong TotalVirtual;
		public ulong AvailableVirtual;
		public ulong AvailableExtendedVirtual;
	}

	[DllImport( "kernel32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static extern bool GlobalMemoryStatusEx( ref MemoryStatus status );

	//Runs the SDK with these arguments and this environment; true where it finished well. On Linux inside a scope of its own
	//with a ceiling on memory, so the desktop keeps what it needs; past the ceiling the build swaps and is slower rather than killed
	private static bool RunDotnet( string arguments, Dictionary<string, string> environment, int ceilingGB, out string log )
	{
		var capped = OperatingSystem.IsLinux() && File.Exists( "/usr/bin/systemd-run" );
		var dotnet = ExportToolchain.Dotnet ?? "dotnet";
		var start = new ProcessStartInfo( capped ? "/usr/bin/systemd-run" : dotnet,
			capped ? $"--user --scope --quiet -p MemoryMax={ceilingGB}G \"{dotnet}\" {arguments}" : arguments )
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};

		ExportToolchain.Apply( start );

		foreach ( var (key, value) in environment ) start.Environment[key] = value;

		//An emsdk elsewhere on the machine would be taken over the workload's.
		start.Environment.Remove( "EMSDK" );

		using var process = Process.Start( start );

		if ( process is null )
		{
			log = "the .NET SDK is not on the path";
			return false;
		}

		var errors = process.StandardError.ReadToEndAsync();
		var output = process.StandardOutput.ReadToEnd();
		process.WaitForExit();

		log = output + errors.Result;
		return process.ExitCode == 0;
	}
}
