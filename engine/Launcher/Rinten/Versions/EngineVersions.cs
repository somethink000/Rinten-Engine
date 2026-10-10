using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace Rinten;

//Engine versions on this machine, shared by every engine; a project pins one via .rinten-engine, else the running engine
internal static class EngineVersions
{
	static readonly Rinten.Diagnostics.Logger Log = new( "versions" );

	const string PinFileName = ".rinten-engine";

	//What an engine unpacked by hand leaves in Root: <version>.where, holding the path of its editor
	const string WhereExtension = ".where";

	/// <summary>Where versions are installed, and where every engine on this machine looks for the others.</summary>
	public static string Root => Path.Combine(
		Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Rinten", "versions" );

	//Where engines up to 0.2 installed versions: inside the engine that installed them
	static string OwnFolder => Path.Combine( AppContext.BaseDirectory, "versions" );

	/// <summary>An engine that can be opened. Removable is one a launcher installed; Current is the one running.</summary>
	public sealed record Installed( string Version, string Path, string Exe, bool Removable, bool Current );

	//The running engine's version, or null for a build from source
	static string RunningVersion => System.Version.TryParse( Application.Version, out _ ) ? Application.Version : null;

	//The running editor's program, or null where it was started some other way (dotnet Rinten.dll)
	static string RunningExe => Environment.ProcessPath is { } path && Path.GetFileName( path ) == EngineExecutables.EditorFile ? path : null;

	/// <summary>Every engine on this machine, newest first: installed ones, ones unpacked by hand that have run once, and this one.</summary>
	public static List<Installed> ListInstalled()
	{
		var result = new List<Installed>();

		void Add( string version, string path, string exe, bool removable )
		{
			if ( result.Any( x => x.Version == version ) ) return;

			result.Add( new Installed( version, path, exe, removable, SameFile( exe, RunningExe ) ) );
		}

		foreach ( var folder in new[] { Root, OwnFolder } )
		{
			if ( !Directory.Exists( folder ) ) continue;

			foreach ( var dir in Directory.GetDirectories( folder ) )
			{
				var name = Path.GetFileName( dir );

				//A folder without the editor is an install that did not finish
				var exe = Path.Combine( dir, EngineExecutables.EditorFile );
				if ( name.StartsWith( '.' ) || !File.Exists( exe ) ) continue;

				Add( name, dir, exe, removable: true );
			}
		}

		if ( Directory.Exists( Root ) )
		{
			foreach ( var where in Directory.GetFiles( Root, "*" + WhereExtension ) )
			{
				var exe = ReadWhere( where );
				if ( exe is null || !File.Exists( exe ) ) continue;

				Add( Path.GetFileNameWithoutExtension( where ), Path.GetDirectoryName( exe ), exe, removable: false );
			}
		}

		if ( RunningVersion is { } running && RunningExe is { } self )
			Add( running, Path.GetDirectoryName( self ), self, removable: false );

		//By number, so 0.10 comes after 0.9; a name that is not a number goes last
		return result.OrderByDescending( x => System.Version.TryParse( x.Version, out var v ) ? v : new System.Version() ).ToList();
	}

	/// <summary>Writes down where the running engine is, so the other engines here can open it. Not for a build from source.</summary>
	public static void RegisterRunning()
	{
		if ( RunningVersion is not { } version || RunningExe is not { } exe ) return;

		//A launcher's install is found by its folder
		if ( SameFile( Path.GetDirectoryName( exe ), Path.Combine( Root, version ) ) ) return;

		var where = Path.Combine( Root, version + WhereExtension );

		try
		{
			if ( SameFile( ReadWhere( where ), exe ) ) return;

			Directory.CreateDirectory( Root );
			File.WriteAllText( where, exe );
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			Log.Warning( $"could not write down where engine {version} is - other versions will not see it: {e.Message}" );
		}
	}

	static string ReadWhere( string file )
	{
		try
		{
			return File.Exists( file ) ? File.ReadAllText( file ).Trim() : null;
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			return null;
		}
	}

	static bool SameFile( string a, string b )
	{
		if ( string.IsNullOrEmpty( a ) || string.IsNullOrEmpty( b ) ) return false;

		return string.Equals( Path.GetFullPath( a ).TrimEnd( Path.DirectorySeparatorChar ),
			Path.GetFullPath( b ).TrimEnd( Path.DirectorySeparatorChar ),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal );
	}

	/// <summary>Whether this version can be opened here.</summary>
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
		ZipFile.ExtractToDirectory( zipPath, extractDir );
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

		//The same version unpacked by hand is found by its folder from now on
		File.Delete( Path.Combine( Root, release.Version + WhereExtension ) );

		if ( Directory.Exists( staging ) )
			Directory.Delete( staging, true );
	}

	/// <summary>Deletes a version a launcher installed. One unpacked by hand, or the running one, is left alone.</summary>
	public static void Uninstall( string version )
	{
		var installed = ListInstalled().FirstOrDefault( x => x.Version == version );

		if ( installed is null || !installed.Removable || installed.Current ) return;

		Directory.Delete( installed.Path, true );
	}

	//The version a project asks for, or null for the running engine.
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

	//The editor exe this project should launch with. Null means the running one - either nothing is pinned, or
	//the pinned version isn't on this machine, which gets logged rather than silently launching something else.
	public static string ResolveExeForProject( Project project )
	{
		var pin = GetProjectPin( project );

		if ( pin is null )
			return null;

		var installed = ListInstalled().FirstOrDefault( x => x.Version == pin );

		if ( installed is null )
		{
			Log.Warning( $"{project.Config.Title} wants engine {pin}, which isn't on this machine - opening it in this one instead" );
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
