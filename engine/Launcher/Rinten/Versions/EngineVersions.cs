using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace Rinten;

//Engine versions under versions/; a project pins one via .rinten-engine, else the bundled engine
internal static class EngineVersions
{
	static readonly Rinten.Diagnostics.Logger Log = new( "versions" );

	const string PinFileName = ".rinten-engine";

	//Where installed versions live: versions/ next to the launcher.
	public static string Root => Path.Combine( AppContext.BaseDirectory, "versions" );

	public sealed record Installed( string Version, string Path, string Exe );

	//Everything currently installed, newest-looking first. A folder without a Rinten.exe is a broken install and isn't offered.
	public static List<Installed> ListInstalled()
	{
		var result = new List<Installed>();

		if ( !Directory.Exists( Root ) )
			return result;

		foreach ( var dir in Directory.GetDirectories( Root ) )
		{
			var name = Path.GetFileName( dir );

			if ( name.StartsWith( '.' ) )
				continue;

			var exe = Path.Combine( dir, EngineExecutables.EditorFile );

			if ( !File.Exists( exe ) )
				continue;

			result.Add( new Installed( name, dir, exe ) );
		}

		//By number, so 0.10 comes after 0.9; a name that is not a number goes last
		return result.OrderByDescending( x => System.Version.TryParse( x.Version, out var v ) ? v : new System.Version() ).ToList();
	}

	public static bool IsInstalled( string version ) =>
		ListInstalled().Any( x => x.Version == version );

	//Download, verify and unpack a release. The version folder appears atomically - everything happens in a dot-prefixed staging dir that ListInstalled
	//ignores, and the finished tree is moved into place as the last step, so a crash mid-install can't leave something that looks installed but isn't.
	public static async Task InstallAsync( ReleaseApi api, EngineReleaseInfo release, IProgress<DownloadProgress> progress = null )
	{
		var artifact = release.Artifacts?.FirstOrDefault( x => x.Platform == ReleaseApi.CurrentPlatform && x.Available )
			?? throw new InvalidOperationException( $"{release.Version} has no build for {ReleaseApi.CurrentPlatform}" );

		var staging = Path.Combine( Root, ".staging" );

		if ( Directory.Exists( staging ) )
			Directory.Delete( staging, true );

		var zipPath = await api.DownloadAsync( artifact, staging, progress );

		var extractDir = Path.Combine( staging, "extract" );
		Extract( zipPath, extractDir );
		File.Delete( zipPath );

		//Zips often wrap everything in a single top folder; the install shouldn't care which way the archive was rolled
		var tree = extractDir;
		var entries = Directory.GetFileSystemEntries( tree );

		if ( entries.Length == 1 && Directory.Exists( entries[0] ) )
			tree = entries[0];

		if ( !File.Exists( Path.Combine( tree, EngineExecutables.EditorFile ) ) )
		{
			Directory.Delete( staging, true );
			throw new InvalidDataException( $"{release.Version} unpacked without a {EngineExecutables.EditorFile} - not an engine build" );
		}

		var finalDir = Path.Combine( Root, release.Version );

		if ( Directory.Exists( finalDir ) )
			Directory.Delete( finalDir, true );

		Directory.Move( tree, finalDir );

		if ( Directory.Exists( staging ) )
			Directory.Delete( staging, true );
	}

	//ZipFile.ExtractToDirectory writes a link entry as a file holding the target's name, and a
	//libSkiaSharp.so that is 23 bytes of text fails FontManager's type initializer at startup
	static void Extract( string zipPath, string into )
	{
		var root = Path.GetFullPath( into ) + Path.DirectorySeparatorChar;

		using var archive = ZipFile.OpenRead( zipPath );

		foreach ( var entry in archive.Entries )
		{
			var path = Path.GetFullPath( Path.Combine( into, entry.FullName ) );

			if ( !path.StartsWith( root, StringComparison.Ordinal ) )
				throw new InvalidDataException( $"{entry.FullName} unpacks outside the install" );

			if ( entry.FullName.EndsWith( '/' ) )
			{
				Directory.CreateDirectory( path );
				continue;
			}

			Directory.CreateDirectory( Path.GetDirectoryName( path ) );

			//S_IFLNK in the upper half of the external attributes; the entry's content is the target, a file beside it
			if ( !OperatingSystem.IsWindows() && ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 )
			{
				using var reader = new StreamReader( entry.Open() );
				var target = reader.ReadToEnd();

				if ( target.Contains( '/' ) || target.Contains( '\\' ) )
					throw new InvalidDataException( $"{entry.FullName} links outside its folder ({target})" );

				File.CreateSymbolicLink( path, target );
				continue;
			}

			entry.ExtractToFile( path, overwrite: true );
		}
	}

	public static void Uninstall( string version )
	{
		var dir = Path.Combine( Root, version );

		if ( Directory.Exists( dir ) )
			Directory.Delete( dir, true );
	}

	//The version a project asks for, or null for the bundled engine.
	public static string GetProjectPin( Project project )
	{
		try
		{
			var file = PinPath( project );

			if ( file is null || !File.Exists( file ) )
				return null;

			var pin = File.ReadAllText( file ).Trim();
			return string.IsNullOrEmpty( pin ) ? null : pin;
		}
		catch
		{
			return null;
		}
	}

	public static void SetProjectPin( Project project, string version )
	{
		var file = PinPath( project );

		if ( file is null )
			return;

		if ( string.IsNullOrEmpty( version ) )
		{
			File.Delete( file );
			return;
		}

		File.WriteAllText( file, version );
	}

	//The editor exe this project should launch with. Null means the bundled one - either nothing is pinned, or
	//the pinned version isn't installed here, which gets logged rather than silently launching something else.
	public static string ResolveExeForProject( Project project )
	{
		var pin = GetProjectPin( project );

		if ( pin is null )
			return null;

		var installed = ListInstalled().FirstOrDefault( x => x.Version == pin );

		if ( installed is null )
		{
			Log.Warning( $"{project.Config.Title} wants engine {pin}, which isn't installed - launching the bundled engine instead" );
			return null;
		}

		return installed.Exe;
	}

	static string PinPath( Project project )
	{
		var dir = project?.Config?.Directory?.FullName;
		return string.IsNullOrEmpty( dir ) ? null : Path.Combine( dir, PinFileName );
	}
}
