using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Editor;

/// <summary>The .NET SDK and add-ons the web and Android exports compile with, found or installed per user.</summary>
public static class ExportToolchain
{
	static readonly Logger Log = new( "Toolchain" );

	//Where Microsoft's install script puts a per-user SDK; nothing there needs admin rights
	static string LocalRoot => OperatingSystem.IsWindows()
		? Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Microsoft", "dotnet" )
		: Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), ".dotnet" );

	static string Tools => Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Rinten" );

	/// <summary>Where the Android SDK goes when it is installed from here.</summary>
	public static string AndroidSdkInstall => Path.Combine( Tools, "android-sdk" );

	/// <summary>Where the JDK the Android build runs goes when it is installed from here.</summary>
	public static string JavaSdkInstall => Path.Combine( Tools, "jdk" );

	static string ExeName => OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

	//What was found, until an install changes it
	static string dotnet;

	//The folder the SDK found is installed in - see Look.
	static string sdkRoot;

	static HashSet<string> workloads;
	static bool looked;

	/// <summary>Whether an install is running.</summary>
	public static bool Installing { get; private set; }

	/// <summary>The dotnet with a .NET 10 SDK: the per-user one first, then the one on the path; null where there is none.</summary>
	public static string Dotnet
	{
		get
		{
			Look();
			return dotnet;
		}
	}

	static readonly object gate = new();

	static void Look()
	{
		lock ( gate )
		{
			if ( looked ) return;

			string listed = null;

			var found = new[] { Path.Combine( LocalRoot, ExeName ), "dotnet" }
				.FirstOrDefault( x => ( listed = Run( x, "--list-sdks" ) ) is { } sdks && sdks.Split( '\n' ).Any( l => l.StartsWith( "10." ) ) );

			//Where the SDK really is, which a dotnet behind a version manager's shim (mise, asdf) does not say by its own path: "10.0.100 [/root/sdk]"
			var sdkLine = found is null ? null : listed?.Split( '\n' ).FirstOrDefault( l => l.StartsWith( "10." ) );
			var open = sdkLine?.IndexOf( '[' ) ?? -1;
			sdkRoot = open < 0 ? null : Path.GetDirectoryName( sdkLine[( open + 1 )..].TrimEnd( ']', '\r', ' ' ) );

			var ids = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

			//Rows after the dashes: "<id>   <manifest version>   <source>"
			var rows = ( found is null ? "" : Run( found, "workload list" ) ?? "" )
				.Split( '\n' ).SkipWhile( x => !x.StartsWith( "---" ) ).Skip( 1 );

			foreach ( var row in rows )
			{
				var id = row.Trim().Split( ' ', StringSplitOptions.RemoveEmptyEntries ).FirstOrDefault();
				if ( !string.IsNullOrEmpty( id ) && !id.StartsWith( "Use" ) ) ids.Add( id );
			}

			dotnet = found;
			workloads = ids;
			looked = true;
		}
	}

	static Task checking;

	/// <summary>Whether the SDK has been looked for since the last install.</summary>
	public static bool Checked => looked;

	/// <summary>Looks for the SDK off the main thread; the page asks this before <see cref="Missing"/>.</summary>
	public static Task Check() => checking ??= Task.Run( Look ).ContinueWith( _ => checking = null );

	static string AndroidSdk => FirstDirectory( Environment.GetEnvironmentVariable( "ANDROID_HOME" ),
		Environment.GetEnvironmentVariable( "ANDROID_SDK_ROOT" ), AndroidSdkInstall,
		Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), "Android", "Sdk" ) );

	//JAVA_HOME, the one installed from here, then the newest JDK 17+ where the platform keeps them
	static string JavaSdk => new[] { Environment.GetEnvironmentVariable( "JAVA_HOME" ), JavaSdkInstall }
		.Concat( SystemJdks().OrderByDescending( JdkVersion ) )
		.FirstOrDefault( x => !string.IsNullOrWhiteSpace( x ) && JdkVersion( x ) >= 17 );

	static IEnumerable<string> SystemJdks()
	{
		var roots = OperatingSystem.IsWindows()
			? new[] { @"C:\Program Files\Microsoft", @"C:\Program Files\Eclipse Adoptium", @"C:\Program Files\Java" }
			: OperatingSystem.IsMacOS() ? new[] { "/Library/Java/JavaVirtualMachines" } : new[] { "/usr/lib/jvm" };

		foreach ( var root in roots.Where( Directory.Exists ) )
			foreach ( var jdk in Directory.GetDirectories( root ) )
				yield return OperatingSystem.IsMacOS() ? Path.Combine( jdk, "Contents", "Home" ) : jdk;
	}

	//The major version from the JDK's release file - JAVA_VERSION="17.0.12" - or 0 for no JDK there
	static int JdkVersion( string home )
	{
		try
		{
			var javac = Path.Combine( home, "bin", OperatingSystem.IsWindows() ? "javac.exe" : "javac" );
			var release = Path.Combine( home, "release" );

			if ( !File.Exists( javac ) || !File.Exists( release ) ) return 0;

			var line = File.ReadLines( release ).FirstOrDefault( x => x.StartsWith( "JAVA_VERSION=" ) );
			var version = line?.Split( '=' )[1].Trim( '"' ) ?? "";
			var major = version.StartsWith( "1." ) ? version[2..] : version;

			return int.TryParse( major.Split( '.' )[0], out var n ) ? n : 0;
		}
		catch ( IOException )
		{
			return 0;
		}
	}

	//Big downloads go here rather than the system temp folder, which can be small or have a quota
	static string Temp
	{
		get
		{
			var temp = Path.Combine( Tools, "tmp" );
			Directory.CreateDirectory( temp );
			return temp;
		}
	}

	static string FirstDirectory( params string[] paths )
		=> paths.FirstOrDefault( x => !string.IsNullOrWhiteSpace( x ) && Directory.Exists( x ) && Directory.EnumerateFileSystemEntries( x ).Any() );

	static string Workload( ExportPlatform platform ) => platform.IsWeb ? "wasm-tools" : platform.IsAndroid ? "android" : null;

	/// <summary>What this platform's export still needs installed, or null.</summary>
	public static string Missing( ExportPlatform platform )
	{
		if ( Workload( platform ) is not { } workload ) return null;

		Look();

		var missing = new List<string>();

		if ( dotnet is null ) missing.Add( "the .NET 10 SDK" );
		if ( dotnet is null || !workloads.Contains( workload ) ) missing.Add( $"its {workload} workload" );
		if ( platform.IsAndroid && ( AndroidSdk is null || JavaSdk is null ) ) missing.Add( "the Android SDK and a JDK" );

		return missing.Count == 0 ? null : string.Join( ", ", missing );
	}

	/// <summary>How an install is going: the step, what it is doing, and the whole install from nought to one.</summary>
	public readonly record struct InstallProgress( string Step, string Detail, float Fraction );

	//One step of an install: it reports its own fraction and a line about what it is doing, and says whether it worked
	delegate Task<bool> Step( Action<float, string> progress );

	/// <summary>Installs what <see cref="Missing"/> names, reporting on the main thread; false where a step failed.</summary>
	public static async Task<bool> Install( ExportPlatform platform, Action<InstallProgress> report )
	{
		if ( Installing ) return false;
		Installing = true;

		try
		{
			Look();

			var workload = Workload( platform );
			var steps = new List<(string Title, Step Run)>();

			//A workload goes into the SDK's own folder, which for a machine-wide SDK needs admin rights - so a per-user SDK is installed beside it
			var local = Path.Combine( LocalRoot, ExeName );

			if ( dotnet is null || ( !workloads.Contains( workload ) && dotnet != local ) )
				steps.Add( ("Downloading the .NET 10 SDK", InstallSdk) );

			if ( !workloads.Contains( workload ) )
				steps.Add( ($"Installing the {workload} workload", p => RunLines( File.Exists( local ) ? local : Dotnet, $"workload install {workload}", 1800, p )) );

			if ( platform.IsAndroid && ( AndroidSdk is null || JavaSdk is null ) )
			{
				//The engine's Android project, which knows which SDK packages its build asks for
				var project = Path.GetFullPath( Path.Combine( Environment.CurrentDirectory, "..", "engine", "Launcher", "RintenAndroid", "Rinten-Android.csproj" ) );
				var arguments = $"build \"{project}\" -t:InstallAndroidDependencies -f net10.0-android " +
					$"-p:AndroidSdkDirectory=\"{AndroidSdk ?? AndroidSdkInstall}\" -p:JavaSdkDirectory=\"{JavaSdk ?? JavaSdkInstall}\" " +
					"-p:AcceptAndroidSDKLicenses=True";

				steps.Add( ("Installing the Android SDK and a JDK", p => RunLines( Dotnet, arguments, 3600, p )) );
			}

			for ( var i = 0; i < steps.Count; i++ )
			{
				var (title, run) = steps[i];
				var at = i;

				void Progress( float fraction, string detail )
				{
					var whole = ( at + Math.Clamp( fraction, 0.0f, 1.0f ) ) / steps.Count;
					MainThread.Queue( () => report( new InstallProgress( title, detail, whole ) ) );
				}

				Progress( 0.0f, null );

				var worked = await Task.Run( () => run( Progress ) );

				Forget();

				if ( !worked )
				{
					report( new InstallProgress( $"{title} failed - see the log", null, ( at + 1.0f ) / steps.Count ) );
					return false;
				}
			}

			return true;
		}
		finally
		{
			Installing = false;
		}
	}

	static void Forget() => looked = false;

	//The SDK's own archive rather than the install script, so the download can say how far it has got
	static async Task<bool> InstallSdk( Action<float, string> progress )
	{
		const string Feed = "https://builds.dotnet.microsoft.com/dotnet/Sdk";

		var windows = OperatingSystem.IsWindows();
		var arm = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
		var rid = ( windows ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux" ) + ( arm ? "-arm64" : "-x64" );
		var extension = windows ? "zip" : "tar.gz";

		try
		{
			using var http = new HttpClient();

			var version = ( await http.GetStringAsync( $"{Feed}/10.0/latest.version" ) )
				.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ).Last();

			var url = $"{Feed}/{version}/dotnet-sdk-{version}-{rid}.{extension}";
			var expected = ( await http.GetStringAsync( url + ".sha512" ) ).Split( ' ' )[0].Trim();
			var archive = Path.Combine( Temp, Path.GetFileName( url ) );

			using ( var response = await http.GetAsync( url, HttpCompletionOption.ResponseHeadersRead ) )
			{
				response.EnsureSuccessStatusCode();

				var total = response.Content.Headers.ContentLength ?? 0;
				using var hash = System.Security.Cryptography.IncrementalHash.CreateHash( System.Security.Cryptography.HashAlgorithmName.SHA512 );
				await using var source = await response.Content.ReadAsStreamAsync();
				await using var target = File.Create( archive );

				var buffer = new byte[1 << 16];
				long read = 0;
				int chunk;

				while ( ( chunk = await source.ReadAsync( buffer ) ) > 0 )
				{
					hash.AppendData( buffer, 0, chunk );
					await target.WriteAsync( buffer.AsMemory( 0, chunk ) );

					var before = read * 100 / Math.Max( total, 1 );
					read += chunk;

					if ( total > 0 && read * 100 / total != before )
						progress( 0.9f * read / total, $"{read >> 20} of {total >> 20} MB" );
				}

				if ( !string.Equals( Convert.ToHexString( hash.GetHashAndReset() ), expected, StringComparison.OrdinalIgnoreCase ) )
				{
					Log.Warning( $"{url} does not match its published SHA-512" );
					return false;
				}
			}

			progress( 0.9f, "Unpacking" );
			Directory.CreateDirectory( LocalRoot );

			if ( windows )
			{
				System.IO.Compression.ZipFile.ExtractToDirectory( archive, LocalRoot, overwriteFiles: true );
			}
			else
			{
				await using var file = File.OpenRead( archive );
				await using var gzip = new System.IO.Compression.GZipStream( file, System.IO.Compression.CompressionMode.Decompress );
				await System.Formats.Tar.TarFile.ExtractToDirectoryAsync( gzip, LocalRoot, overwriteFiles: true );
			}

			File.Delete( archive );
			progress( 1.0f, null );

			return true;
		}
		catch ( Exception e )
		{
			Log.Warning( $"the .NET SDK did not install: {e.Message}" );
			return false;
		}
	}

	//Runs a long command, passing on its latest line; the whole output goes to the log if it fails
	static Task<bool> RunLines( string exe, string arguments, int timeoutSeconds, Action<float, string> progress )
	{
		var log = new System.Text.StringBuilder();
		var lines = 0;

		try
		{
			var info = new ProcessStartInfo( exe, arguments )
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};

			Apply( info );

			using var process = new Process { StartInfo = info };

			void Line( object sender, DataReceivedEventArgs e )
			{
				if ( string.IsNullOrWhiteSpace( e.Data ) ) return;

				lock ( log ) log.AppendLine( e.Data );

				//No count to go by: each line takes it a little further, never all the way
				var n = Interlocked.Increment( ref lines );
				var text = e.Data.Trim();
				progress( n / ( n + 25.0f ), text.Length > 90 ? text[..90] + "…" : text );
			}

			process.OutputDataReceived += Line;
			process.ErrorDataReceived += Line;

			process.Start();
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();

			if ( !process.WaitForExit( timeoutSeconds * 1000 ) )
			{
				process.Kill( true );
				Log.Warning( $"{exe} {arguments} did not finish in {timeoutSeconds} s" );
				return Task.FromResult( false );
			}

			process.WaitForExit();

			if ( process.ExitCode != 0 )
			{
				Log.Warning( $"{exe} {arguments} failed:\n{log}" );
				return Task.FromResult( false );
			}

			progress( 1.0f, null );
			return Task.FromResult( true );
		}
		catch ( Exception e )
		{
			Log.Warning( $"{exe} {arguments}: {e.Message}" );
			return Task.FromResult( false );
		}
	}

	/// <summary>A dotnet process with the SDK found here, and the Android SDK and JDK where they are known.</summary>
	public static ProcessStartInfo Start( string arguments )
	{
		var info = new ProcessStartInfo( Dotnet ?? "dotnet", arguments )
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};

		Apply( info );

		return info;
	}

	/// <summary>Points a process at the SDK found here: its root, and it first on the path.</summary>
	public static void Apply( ProcessStartInfo info )
	{
		info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
		info.Environment["DOTNET_NOLOGO"] = "1";
		info.Environment[OperatingSystem.IsWindows() ? "TEMP" : "TMPDIR"] = Temp;

		if ( Dotnet is { } exe && Path.IsPathRooted( exe ) )
		{
			var root = Path.GetDirectoryName( exe );
			info.Environment["DOTNET_ROOT"] = root;
			info.Environment["PATH"] = root + Path.PathSeparator + ( Environment.GetEnvironmentVariable( "PATH" ) ?? "" );
		}
	}

	/// <summary>The Android SDK and JDK as build properties, where they are known.</summary>
	public static string AndroidProperties()
		=> ( AndroidSdk is { } sdk ? $"-p:AndroidSdkDirectory=\"{sdk}\" " : "" )
			+ ( JavaSdk is { } jdk ? $"-p:JavaSdkDirectory=\"{jdk}\" " : "" );

	/// <summary>The folders workload packs are in, for the SDK found here.</summary>
	public static IEnumerable<string> PackRoots()
	{
		Look();

		if ( sdkRoot is not null ) yield return Path.Combine( sdkRoot, "packs" );
		if ( Environment.GetEnvironmentVariable( "DOTNET_ROOT" ) is { Length: > 0 } root ) yield return Path.Combine( root, "packs" );
		if ( Dotnet is { } exe && Path.IsPathRooted( exe ) ) yield return Path.Combine( Path.GetDirectoryName( exe ), "packs" );

		yield return Path.Combine( LocalRoot, "packs" );
		yield return "/usr/lib/dotnet/packs";
		yield return "/usr/share/dotnet/packs";
	}

	//Output on success, null on failure or timeout; failures go to the log whole
	static string Run( string exe, string arguments, int timeoutSeconds = 30 )
	{
		try
		{
			var info = new ProcessStartInfo( exe, arguments )
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};

			info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
			info.Environment["DOTNET_NOLOGO"] = "1";

			if ( Path.IsPathRooted( exe ) && Path.GetFileName( exe ) == ExeName )
				info.Environment["DOTNET_ROOT"] = Path.GetDirectoryName( exe );

			using var process = Process.Start( info );
			if ( process is null ) return null;

			var errors = process.StandardError.ReadToEndAsync();
			var output = process.StandardOutput.ReadToEndAsync();

			if ( !process.WaitForExit( timeoutSeconds * 1000 ) )
			{
				process.Kill( true );
				Log.Warning( $"{exe} {arguments} did not finish in {timeoutSeconds} s" );
				return null;
			}

			if ( process.ExitCode != 0 )
			{
				Log.Warning( $"{exe} {arguments} failed:\n{output.Result}\n{errors.Result}" );
				return null;
			}

			return output.Result;
		}
		catch ( Exception e )
		{
			//No such program is the common case: no SDK on the path
			if ( timeoutSeconds > 30 ) Log.Warning( $"{exe} {arguments}: {e.Message}" );
			return null;
		}
	}
}
