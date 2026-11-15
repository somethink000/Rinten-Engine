using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using static Rinten.Build.Constants;

namespace Rinten.Build.Steps;

//Downloads pinned third-party natives (Slang, SDL...) from engine/natives.json into game/bin
internal class FetchNatives( bool force = false, string platformOverride = null )
{
	private readonly Platform platform = Platform.Create();

	//One archive somebody else publishes, and what to take out of it.
	internal sealed class Package
	{
		public string Name { get; set; } = "";
		public string Version { get; set; } = "";
		public string Url { get; set; } = "";

		//Of the archive, not of what is in it. Null skips the check.
		public string Sha256 { get; set; }

		//Path inside the archive to name in the bin folder.
		public Dictionary<string, string> Files { get; set; } = new();

		//The same for whole directories, copied recursively.
		public Dictionary<string, string> Folders { get; set; } = new();

		//File names inside those folders that are not taken - a link library, a sandbox helper nothing runs.
		public string[] Skip { get; set; } = [];

		//Whether the engine runs without it. One that will not download or unpack is said and passed over rather than failing the build: FFmpeg is three
		//hundred megabytes that a build with no video in it has no use for, and without it a video simply does not play rather than breaking anything.
		public bool Optional { get; set; }

		//Whether the Linux libraries it installs have their symbols stripped - some of these arrive many times their own size in symbols alone.
		public bool Strip { get; set; }
	}

	internal ExitCode Run()
	{
		var root = Directory.GetCurrentDirectory();
		var manifestPath = Path.Combine( root, "engine", "natives.json" );

		if ( !File.Exists( manifestPath ) )
		{
			Log.Error( $"No native manifest at {manifestPath}." );
			return ExitCode.Failure;
		}

		Dictionary<string, Package[]> manifest;

		try
		{
			manifest = JsonSerializer.Deserialize( File.ReadAllText( manifestPath ), BuildJson.Default.NativeManifest );
		}
		catch ( JsonException ex )
		{
			Log.Error( $"engine/natives.json does not read as JSON: {ex.Message}" );
			return ExitCode.Failure;
		}

		var platformId = platformOverride ?? platform.PlatformID;

		if ( manifest is null || !manifest.TryGetValue( platformId, out var packages ) || packages.Length == 0 )
		{
			//Said rather than failed: a platform with nothing to fetch is a platform whose libraries are all built or restored, not a mistake.
			Log.Info( $"Nothing to fetch for {platformId}." );
			return ExitCode.Success;
		}

		var binDir = Path.Combine( root, "game", "bin", platformId );
		var cacheDir = Path.Combine( root, "engine", "Natives" );

		Directory.CreateDirectory( binDir );
		Directory.CreateDirectory( cacheDir );

		var stampPath = Path.Combine( binDir, ".natives" );
		var stamp = ReadStamp( stampPath );

		foreach ( var package in packages )
		{
			if ( !force && stamp.TryGetValue( package.Name, out var installed )
				&& installed == package.Version && IsInstalled( binDir, package ) )
			{
				Log.Info( $"{package.Name} {package.Version} is already in {ShortPath( binDir )}." );
				continue;
			}

			var archive = Path.Combine( cacheDir, NameFromUrl( package.Url ) );

			if ( !Download( package, archive ) || !Extract( package, archive, binDir ) )
			{
				if ( !package.Optional )
					return ExitCode.Failure;

				Log.Warning( $"{package.Name} {package.Version} is not installed - it is optional, and the build goes on without it." );
				continue;
			}

			if ( package.Strip )
				StripSymbols( package, binDir );

			stamp[package.Name] = package.Version;
			WriteStamp( stampPath, stamp );

			Log.Info( $"{package.Name} {package.Version} -> {ShortPath( binDir )}" );
		}

		return ExitCode.Success;
	}

	//Whether everything the package installs is on disk. The stamp alone is not enough: somebody deletes a DLL far more often than they edit a stamp.
	private static bool IsInstalled( string binDir, Package package )
	{
		foreach ( var destination in package.Files.Values )
		{
			if ( !File.Exists( Path.Combine( binDir, destination ) ) )
				return false;
		}

		foreach ( var destination in package.Folders.Values )
		{
			if ( !Directory.Exists( Path.Combine( binDir, destination ) ) )
				return false;
		}

		return true;
	}

	private static bool Download( Package package, string archive )
	{
		if ( File.Exists( archive ) && Verify( archive, package.Sha256, quiet: true ) )
		{
			Log.Info( $"Using the cached {Path.GetFileName( archive )}." );
			return true;
		}

		Log.Info( $"Downloading {package.Name} {package.Version} from {package.Url}" );

		var partial = archive + ".part";

		try
		{
			using var http = new HttpClient { Timeout = TimeSpan.FromMinutes( 30 ) };
			using var response = http.GetAsync( package.Url, HttpCompletionOption.ResponseHeadersRead ).GetAwaiter().GetResult();

			if ( !response.IsSuccessStatusCode )
			{
				Log.Error( $"{package.Url} answered {(int)response.StatusCode} {response.StatusCode}." );
				return false;
			}

			var total = response.Content.Headers.ContentLength ?? 0;

			using ( var from = response.Content.ReadAsStream() )
			using ( var to = File.Create( partial ) )
			{
				var buffer = new byte[1 << 16];
				long done = 0, said = 0;

				int read;
				while ( (read = from.Read( buffer, 0, buffer.Length )) > 0 )
				{
					to.Write( buffer, 0, read );
					done += read;

					//Every eight megabytes. These are tens of megabytes over a link nobody chose, and a
					//bootstrap that prints one line and then sits there for ten minutes reads as a hang.
					if ( done - said < 8 << 20 )
						continue;

					said = done;
					Log.Info( total > 0
						? $"  {done >> 20} of {total >> 20} MB"
						: $"  {done >> 20} MB" );
				}
			}

			if ( !Verify( partial, package.Sha256, quiet: false ) )
			{
				File.Delete( partial );
				return false;
			}

			File.Move( partial, archive, overwrite: true );
			return true;
		}
		catch ( Exception ex )
		{
			Log.Error( $"Downloading {package.Name} failed: {ex.Message}" );

			if ( File.Exists( partial ) )
				File.Delete( partial );

			return false;
		}
	}

	//Checks a downloaded archive against the hash the manifest pins it to.
	private static bool Verify( string path, string expected, bool quiet )
	{
		if ( string.IsNullOrEmpty( expected ) )
		{
			if ( !quiet )
				Log.Warning( $"{Path.GetFileName( path )} has no sha256 in the manifest - taking it as it came." );

			return true;
		}

		using var stream = File.OpenRead( path );
		var actual = Convert.ToHexString( SHA256.HashData( stream ) ).ToLowerInvariant();

		if ( string.Equals( actual, expected, StringComparison.OrdinalIgnoreCase ) )
			return true;

		if ( !quiet )
			Log.Error( $"{Path.GetFileName( path )} hashes to {actual}, and the manifest says {expected}." );

		return false;
	}

	private static bool Extract( Package package, string archive, string binDir )
	{
		try
		{
			var wanted = new Dictionary<string, string>( package.Files );
			var folders = new Dictionary<string, string>( package.Folders );

			var skip = new HashSet<string>( package.Skip ?? [], StringComparer.OrdinalIgnoreCase );

			//An Android library comes as an .aar, which is a zip under another name.
			if ( archive.EndsWith( ".zip", StringComparison.OrdinalIgnoreCase ) ||
				archive.EndsWith( ".aar", StringComparison.OrdinalIgnoreCase ) )
				ExtractZip( archive, binDir, wanted, folders, skip );
			else if ( archive.EndsWith( ".tar.bz2", StringComparison.OrdinalIgnoreCase ) )
				ExtractWithTar( archive, "-xjf", binDir, wanted, folders, skip );
			else if ( archive.EndsWith( ".tar.xz", StringComparison.OrdinalIgnoreCase ) )
				ExtractWithTar( archive, "-xJf", binDir, wanted, folders, skip );
			else
				ExtractTar( archive, binDir, wanted, folders, skip );

			//Whatever is left was named in the manifest and is not in the archive - a version bump that moved a
			//file, usually. Worth failing on: the alternative is a build that runs and cannot compile a shader.
			if ( wanted.Count > 0 )
			{
				Log.Error( $"{Path.GetFileName( archive )} has no {string.Join( ", ", wanted.Keys )}." );
				return false;
			}

			return true;
		}
		catch ( Exception ex )
		{
			Log.Error( $"Unpacking {Path.GetFileName( archive )} failed: {ex.Message}" );
			return false;
		}
	}

	private static void ExtractZip( string archive, string binDir, Dictionary<string, string> files, Dictionary<string, string> folders, HashSet<string> skip )
	{
		using var zip = ZipFile.OpenRead( archive );

		foreach ( var entry in zip.Entries )
		{
			if ( entry.Length == 0 && entry.FullName.EndsWith( '/' ) )
				continue;

			var destination = Destination( entry.FullName, binDir, files, folders, skip );
			if ( destination is null )
				continue;

			Directory.CreateDirectory( Path.GetDirectoryName( destination ) );

			using var from = entry.Open();
			using var to = File.Create( destination );
			from.CopyTo( to );
		}
	}

	private static void ExtractTar( string archive, string binDir, Dictionary<string, string> files, Dictionary<string, string> folders, HashSet<string> skip )
	{
		using var stream = File.OpenRead( archive );
		using var gzip = new GZipStream( stream, CompressionMode.Decompress );
		using var tar = new TarReader( gzip );

		//Links are settled after the pass: the one that names the library everything opens - libslang.so - comes past before the versioned file it points at.
		var links = new List<(string Destination, string Target)>();

		while ( tar.GetNextEntry() is { } entry )
		{
			var link = entry.EntryType == TarEntryType.SymbolicLink;

			if ( !link && entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) )
				continue;

			var destination = Destination( entry.Name, binDir, files, folders, skip );
			if ( destination is null )
				continue;

			Directory.CreateDirectory( Path.GetDirectoryName( destination ) );

			if ( link )
			{
				links.Add( (destination, entry.LinkName) );
				continue;
			}

			entry.ExtractToFile( destination, overwrite: true );
		}

		//A link is what these should stay - a second copy of a thirty megabyte .so is thirty megabytes - but a
		//Windows machine unpacking a Linux archive is not allowed to make one, and there a copy is all there is.
		foreach ( var (destination, target) in links )
		{
			var name = Path.GetFileName( target );

			if ( File.Exists( destination ) )
				File.Delete( destination );

			try
			{
				File.CreateSymbolicLink( destination, name );
			}
			catch ( Exception )
			{
				var beside = Path.Combine( Path.GetDirectoryName( destination ), name );

				if ( File.Exists( beside ) )
					File.Copy( beside, destination, overwrite: true );
				else
					Log.Warning( $"{Path.GetFileName( destination )} points at {name}, which is not beside it." );
			}
		}
	}

	//Unpacks .tar.bz2/.tar.xz with the system tar (.NET can't), then walks it like a zip
	private static void ExtractWithTar( string archive, string flags, string binDir, Dictionary<string, string> files, Dictionary<string, string> folders, HashSet<string> skip )
	{
		var unpacked = archive + ".unpacked";

		if ( Directory.Exists( unpacked ) )
			Directory.Delete( unpacked, true );

		Directory.CreateDirectory( unpacked );

		try
		{
			Log.Info( $"Unpacking {Path.GetFileName( archive )}" );

			if ( !Utility.RunProcess( "tar", $"{flags} \"{archive}\" -C \"{unpacked}\"" ) )
				throw new InvalidOperationException( "tar could not unpack it - are bzip2 and xz installed?" );

			foreach ( var file in Directory.EnumerateFiles( unpacked, "*", SearchOption.AllDirectories ) )
			{
				var relative = Path.GetRelativePath( unpacked, file );

				var destination = Destination( relative, binDir, files, folders, skip );
				if ( destination is null )
					continue;

				Directory.CreateDirectory( Path.GetDirectoryName( destination ) );
				File.Move( file, destination, overwrite: true );
			}
		}
		finally
		{
			try
			{
				Directory.Delete( unpacked, true );
			}
			catch ( IOException )
			{
				Log.Warning( $"{unpacked} could not be removed - it can be deleted by hand." );
			}
		}
	}

	//Strips the symbols out of the Linux libraries a package installed. Only on a machine with strip - which a Linux
	//one that builds the core has - and never fatal: an unstripped library is a large library, not a broken one.
	private static void StripSymbols( Package package, string binDir )
	{
		if ( OperatingSystem.IsWindows() )
			return;

		var libraries = package.Files.Values
			.Select( name => Path.Combine( binDir, name ) )
			.Concat( package.Folders.Values
				.Select( name => Path.Combine( binDir, name ) )
				.Where( Directory.Exists )
				.SelectMany( folder => Directory.EnumerateFiles( folder, "*.so*", SearchOption.AllDirectories ) ) )
			.Where( file => File.Exists( file ) && Path.GetFileName( file ).Contains( ".so" ) )
			.Distinct()
			.ToList();

		foreach ( var library in libraries )
		{
			var before = new FileInfo( library ).Length;

			if ( !Utility.RunProcess( "strip", $"--strip-unneeded \"{library}\"" ) )
			{
				Log.Warning( $"{Path.GetFileName( library )} could not be stripped - left as it came." );
				continue;
			}

			var after = new FileInfo( library ).Length;

			if ( after < before )
				Log.Info( $"  {Path.GetFileName( library )}: {before >> 20} MB -> {after >> 20} MB" );
		}
	}

	//Where one entry of an archive lands, or null for the ones we do not want. Matched on the tail rather than the whole path, because half of
	//these archives unpack into a folder named after their own version and the other half do not, and the manifest should not have to know which.
	private static string Destination( string entryPath, string binDir, Dictionary<string, string> files, Dictionary<string, string> folders, HashSet<string> skip )
	{
		var path = entryPath.Replace( '\\', '/' ).TrimStart( '.', '/' );

		if ( skip.Contains( Path.GetFileName( path ) ) )
			return null;

		foreach ( var (from, to) in files )
		{
			if ( !Tail( path, from ) )
				continue;

			files.Remove( from );
			return Path.Combine( binDir, to );
		}

		foreach ( var (from, to) in folders )
		{
			var at = path.IndexOf( from + "/", StringComparison.OrdinalIgnoreCase );
			if ( at < 0 )
				continue;

			var inside = path[(at + from.Length + 1)..];
			return Path.Combine( binDir, to, inside.Replace( '/', Path.DirectorySeparatorChar ) );
		}

		return null;
	}

	private static bool Tail( string path, string wanted )
	{
		wanted = wanted.Replace( '\\', '/' );

		return string.Equals( path, wanted, StringComparison.OrdinalIgnoreCase )
			|| path.EndsWith( "/" + wanted, StringComparison.OrdinalIgnoreCase );
	}

	private static Dictionary<string, string> ReadStamp( string path )
	{
		var stamp = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

		if ( !File.Exists( path ) )
			return stamp;

		foreach ( var line in File.ReadAllLines( path ) )
		{
			var parts = line.Split( ' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries );

			if ( parts.Length == 2 )
				stamp[parts[0]] = parts[1];
		}

		return stamp;
	}

	private static void WriteStamp( string path, Dictionary<string, string> stamp )
	{
		File.WriteAllLines( path, stamp.Select( pair => $"{pair.Key} {pair.Value}" ).Order() );
	}

	private static string NameFromUrl( string url )
	{
		var name = url[(url.LastIndexOf( '/' ) + 1)..];
		return string.IsNullOrEmpty( name ) ? "download" : name;
	}

	private static string ShortPath( string path )
	{
		var root = Directory.GetCurrentDirectory();
		return path.StartsWith( root, StringComparison.OrdinalIgnoreCase )
			? path[(root.Length + 1)..].Replace( '\\', '/' )
			: path;
	}
}
