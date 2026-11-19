using System.IO;
using System.Text.RegularExpressions;

namespace Rinten.Porting.SBox;

/// <summary>The content a ported project uses without containing.</summary>
public static class SBoxBaseContent
{
	private static readonly Logger log = new( "Porter" );

	/// <summary>Finds the installed game in Steam's default and other library folders, or null</summary>
	public static string Find()
	{
		var suffix = Path.Combine( "steamapps", "common", "sbox" );

		foreach ( var steam in SteamRoots() )
		{
			var here = Path.Combine( steam, suffix );

			if ( Directory.Exists( Path.Combine( here, "addons" ) ) ) return here;

			foreach ( var library in Libraries( steam ) )
			{
				var there = Path.Combine( library, suffix );

				if ( Directory.Exists( Path.Combine( there, "addons" ) ) ) return there;
			}
		}

		return null;
	}

	/// <summary>The install a folder is, or null where it is not one.</summary>
	public static string Holds( string folder )
	{
		if ( string.IsNullOrWhiteSpace( folder ) ) return null;

		string full;

		try
		{
			full = Path.GetFullPath( folder.Trim() );
		}
		catch ( Exception )
		{
			return null;
		}

		//The install itself.
		if ( Directory.Exists( Path.Combine( full, "addons" ) ) ) return full;

		//The shelf inside it, which is what a folder picker lands on when somebody opens the install to check it is the right one.
		if ( string.Equals( Path.GetFileName( full ), "addons", StringComparison.OrdinalIgnoreCase ) &&
			Path.GetDirectoryName( full ) is { Length: > 0 } above )
		{
			return above;
		}

		//The folder above it, or a whole Steam library: both are somewhere the game is rather than the game, and both are one lookup away.
		foreach ( var inside in new[]
		{
			Path.Combine( full, "sbox" ),
			Path.Combine( full, "common", "sbox" ),
			Path.Combine( full, "steamapps", "common", "sbox" ),
		} )
		{
			if ( Directory.Exists( Path.Combine( inside, "addons" ) ) ) return inside;
		}

		return null;
	}

	//Everywhere Steam itself is normally installed.
	private static IEnumerable<string> SteamRoots()
	{
		if ( OperatingSystem.IsWindows() )
		{
			yield return Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ProgramFilesX86 ), "Steam" );
			yield return Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ProgramFiles ), "Steam" );

			foreach ( var drive in "CDEFG" )
			{
				yield return Path.Combine( $"{drive}:\\", "Steam" );
				yield return Path.Combine( $"{drive}:\\", "SteamLibrary" );
			}

			yield break;
		}

		var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );

		//~/.steam/steam is the symlink Steam has always kept; where the files themselves are depends on
		//how it was installed - a distribution package, the Flatpak, or the Snap, none of which agree.
		yield return Path.Combine( home, ".steam", "steam" );
		yield return Path.Combine( home, ".steam", "root" );
		yield return Path.Combine( home, ".steam", "debian-installation" );
		yield return Path.Combine( home, ".local", "share", "Steam" );
		yield return Path.Combine( home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam" );
		yield return Path.Combine( home, "snap", "steam", "common", ".local", "share", "Steam" );

		if ( Environment.GetEnvironmentVariable( "XDG_DATA_HOME" ) is { Length: > 0 } data )
			yield return Path.Combine( data, "Steam" );
	}

	//All Steam library folders from the install's list, needed for libraries on other disks
	private static IEnumerable<string> Libraries( string steam )
	{
		foreach ( var file in new[]
		{
			Path.Combine( steam, "steamapps", "libraryfolders.vdf" ),
			Path.Combine( steam, "config", "libraryfolders.vdf" ),
		} )
		{
			string text;

			try
			{
				if ( !File.Exists( file ) ) continue;

				text = File.ReadAllText( file );
			}
			catch ( IOException )
			{
				continue;
			}

			foreach ( Match match in LibraryPath.Matches( text ) )
			{
				//A quoted string in their format, so its separators are escaped.
				yield return match.Groups["path"].Value.Replace( "\\\\", "\\" );
			}
		}
	}

	//A library's line in Steam's list of them.
	private static readonly Regex LibraryPath = new(
		"\"path\"\\s*\"(?<path>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase );

	//Content roots: each addon's assets; core is skipped on purpose (see below)
	private static List<string> Roots( string install )
	{
		var roots = new List<string>();

		foreach ( var addon in PortPaths.Directories( Path.Combine( install, "addons" ) ) )
		{
			var assets = PortPaths.FindDirectory( addon, "Assets" );
			if ( Directory.Exists( assets ) ) roots.Add( assets );
		}

		//Skip core: its dev materials reference generated texture names we can't resolve yet

		return roots;
	}

	//Any quoted path with a folder and an extension; @ is allowed for clip names like Citizen@Run_N.fbx
	private static readonly Regex Named = new(
		"\"(?<path>[A-Za-z0-9_@\\-.]+(?:/[A-Za-z0-9_@\\-.]+)+\\.[A-Za-z0-9_]{2,12})\"",
		RegexOptions.Compiled | RegexOptions.IgnoreCase );

	//Files that hold no names a reader could find: pictures, meshes, sounds,
	//binaries. Everything else is read, because everything else might name something.
	private static readonly HashSet<string> Opaque = new( StringComparer.OrdinalIgnoreCase )
	{
		".png", ".jpg", ".jpeg", ".tga", ".psd", ".dds", ".exr", ".hdr", ".bmp", ".gif",
		".fbx", ".obj", ".dae", ".gltf", ".glb", ".blend", ".stl", ".ply",
		".wav", ".mp3", ".ogg", ".flac", ".mp4", ".webm",
		".dll", ".exe", ".pdb", ".zip", ".7z", ".rar", ".bin", ".pak",
		".ttf", ".otf", ".woff", ".woff2", ".ico",
	};

	//Whether a file is worth reading for the names inside it. Compiled files are not read at all - see
	//PortPaths.IsCompiled - and nothing carries one any more, so this pass is over documents and the art beside them.
	private static bool Readable( string file )
	{
		if ( PortPaths.IsCompiled( file ) ) return false;

		return !Opaque.Contains( Path.GetExtension( file ).ToLowerInvariant() );
	}

	//Asset names scanned from compiled files as plain text; vmdl_prefab must precede vmdl
	private static readonly Regex Embedded = new(
		"(?<path>[A-Za-z0-9_@\\-./]+\\.(?:vmdl_prefab|vmdl|vmat|vtex|vanmgrph|vsnd|vphys" +
		"|fbx|gltf|glb|smd|obj|dae|dmx))",
		RegexOptions.Compiled | RegexOptions.IgnoreCase );

	/// <summary>Brings across everything the ported project names and does not have. Returns how many files were copied.</summary>
	public static int Carry( string assets, PortReport report )
	{
		var install = Find();

		if ( install is null )
		{
			report.Notes.Add( "s&box is not installed here, so nothing its content provides could be " +
				"brought across - a model merged onto the citizen skeleton will have nothing to merge onto." );

			return 0;
		}

		var roots = Roots( install );
		if ( roots.Count == 0 ) return 0;

		//What the project asks for, gathered from what it says about itself: its scenes, its prefabs, and the definitions the port has
		//just written. A path named in code is not here, because a name built at runtime is not a name anybody can read off the page.
		var wanted = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var file in PortPaths.Walk( assets ) )
		{
			if ( !Readable( file ) ) continue;

			try
			{
				foreach ( Match match in Named.Matches( File.ReadAllText( file ) ) )
					wanted.Add( match.Groups["path"].Value.Replace( '\\', '/' ) );
			}
			catch ( IOException )
			{
				//Unreadable here is unreadable everywhere; whoever else touches it will say so.
			}
		}

		var copied = 0;
		var carried = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		//Their shaders, left where they were - see TheirShader.
		var shadowed = new List<string>();

		//Breadth first in sorted order, so every platform carries the same set of files
		var queue = new Queue<string>( wanted.OrderBy( x => x, StringComparer.Ordinal ) );

		while ( queue.Count > 0 )
		{
			var path = queue.Dequeue();

			if ( !carried.Add( path ) ) continue;

			//Already in the project, in ours or in theirs. Either way it is not missing and not the base content's to provide.
			if ( Exists( assets, path ) ) continue;

			//Never carry their shaders: a file would shadow our alias for that shader name
			if ( TheirShader( path ) )
			{
				shadowed.Add( path );
				continue;
			}

			var (from, source) = Find( roots, path );

			//What an install ships of a material is often only the compiled file: ported from that, its generated pictures unpacked beside it
			if ( source is null && path.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) )
			{
				var (compiledFrom, compiled) = FindCompiled( roots, path + "_c" );

				if ( compiled is not null )
				{
					try
					{
						if ( SBoxPorter.PortCompiledMaterial( compiled, path, compiledFrom, assets, report ) ) ++copied;
					}
					catch ( Exception e )
					{
						report.Skipped.Add( $"{path}: came over from s&box compiled and would not port - {e.Message}" );
					}
				}

				continue;
			}

			//A picture the install ships only compiled - core's particle sprites - is unpacked to the picture it was made from,
			//under the name asked for: a .vtex name finds its .png here
			if ( source is null && path.EndsWith( ".vtex", StringComparison.OrdinalIgnoreCase ) )
			{
				//Core too, for pictures only - its materials stay skipped (see Roots)
				var (_, compiledPicture) = FindCompiled( [.. roots, Path.Combine( install, "core" )], path + "_c" );

				if ( compiledPicture is not null && CoreImportedTexture.Load( compiledPicture ) is { } bitmap )
				{
					try
					{
						using ( bitmap )
						{
							var unpacked = Path.Combine( assets, Path.ChangeExtension( path, ".png" ) );
							Directory.CreateDirectory( Path.GetDirectoryName( unpacked ) );
							File.WriteAllBytes( unpacked, bitmap.ToPng() );
							++copied;
						}
					}
					catch ( Exception e )
					{
						report.Skipped.Add( $"{path}: came over from s&box compiled and would not unpack - {e.Message}" );
					}
				}

				continue;
			}

			if ( source is null ) continue;

			//Named as the file on the shelf is named, not as whatever asked for it spelled it. The port writes the resolved name into the
			//model it ports - see PortPaths.FindFile - so a copy landing under the caller's spelling is a file the model still cannot open.
			var to = Path.Combine( assets, Path.GetRelativePath( from, source ) );

			try
			{
				Directory.CreateDirectory( Path.GetDirectoryName( to ) );
				File.Copy( source, to, overwrite: true );

				++copied;
			}
			catch ( Exception e )
			{
				report.Skipped.Add( $"{path}: could not be brought over from s&box - {e.Message}" );
				continue;
			}

			//Follow references from anything that can name assets; pictures and anim graphs are skipped
			var kind = Path.GetExtension( path ).ToLowerInvariant();

			if ( !Opaque.Contains( kind ) && kind != ".vanmgrph" )
			{
				foreach ( var next in Names( source ) )
				{
					//A graph is named by every model that merges onto one, and it is not copied: it is translated below into one of ours. Copying it as well
					//would put a file in the project that nothing reads, and following what it names would drag in a world of subgraphs to sit beside it.
					if ( next.EndsWith( ".vanmgrph", StringComparison.OrdinalIgnoreCase ) ) continue;

					queue.Enqueue( next );
				}
			}

			//Port documents, not just copy them, since nothing reads their raw format
			try
			{
				//With the install's shelves to find its graph on: the graph a carried character animates itself with goes into it, translated,
				//and every character an install ships names the same one - which the port translates once, see PortReport.GraphDocuments.
				if ( kind is ".vmdl" ) SBoxPorter.PortModel( source, path, from, assets, report, roots );
				else if ( kind is ".vmat" ) SBoxPorter.PortMaterial( source, path, from, assets, report );
			}
			catch ( Exception e )
			{
				report.Skipped.Add( $"{path}: came over from s&box and would not port - {e.Message}" );
			}

			//Compiled data is no longer read; only the document and its art come across
			if ( kind is ".vmdl" ) Nothing( assets, path, report );
		}

		if ( copied > 0 )
		{
			report.Notes.Add( $"{copied} file(s) came from the s&box installation at {install} - " +
				"content the project names and does not contain." );
		}

		foreach ( var shader in shadowed.Distinct( StringComparer.OrdinalIgnoreCase )
			.OrderBy( x => x, StringComparer.Ordinal ) )
		{
			report.Notes.Add( $"{shader}: their shader, left in s&box - this engine draws the materials " +
				"that name it with a surface of its own, and a copy of theirs sitting under that name " +
				"would be found first and would not compile." );
		}

		log.Info( $"base content: {copied} file(s) from {install}" );

		return copied;
	}

	//Reports a carried model that produced nothing, e.g. a doc whose meshes aren't on disk
	private static void Nothing( string assets, string path, PortReport report )
	{
		var ours = Path.Combine( assets,
			Path.ChangeExtension( path, ".mdl" ).Replace( '/', Path.DirectorySeparatorChar ) );

		if ( File.Exists( ours ) ) return;

		report.Skipped.Add( $"{path}: came over from s&box and no model of ours could be built from it - " +
			"their document is there and the meshes it names are not. A checkout of their public content " +
			"holds documents only; the geometry is in an installed copy of the game." );
	}

	//Whether a path is one of their shader files, which are never carried. The includes beside them too: their common/*.hlsl is what a
	//carried shader would be reaching for, and on its own it is a folder of dead text under the one folder the editor offers surfaces from.
	private static bool TheirShader( string path )
	{
		var kind = Path.GetExtension( path ).ToLowerInvariant();

		return kind is ".shader" or ".hlsl" or ".fxc" or ".vfx";
	}

	//Whether the project already has this, under either engine's name.
	private static bool Exists( string assets, string path )
	{
		var local = Path.Combine( assets, path.Replace( '/', Path.DirectorySeparatorChar ) );

		//However it is spelled here too, or a file already carried under the name it really has is carried again under the name that asked for it.
		if ( PortPaths.FindFile( local ) is not null ) return true;

		//And under the name the port gives it: a .vmdl the project contains has become a .mdl by the time this runs.
		var ours = Path.GetExtension( local ).ToLowerInvariant() switch
		{
			".vmdl" => Path.ChangeExtension( local, ".mdl" ),
			".vmat" => Path.ChangeExtension( local, ".mat" ),
			_ => null,
		};

		return ours is not null && File.Exists( ours );
	}

	//The file behind a resource path, in whichever addon has it.
	private static string Locate( List<string> roots, string path ) => Find( roots, path ).File;

	//The same, and the assets root it was found under - which is what the paths inside it are
	//written against, so a document carried from there can be ported rather than only copied.
	private static (string Root, string File) Find( List<string> roots, string path )
	{
		var relative = path.Replace( '/', Path.DirectorySeparatorChar );

		foreach ( var root in roots )
		{
			//Case-insensitive lookup, since documents name files in whatever case was typed
			if ( PortPaths.FindFile( Path.Combine( root, relative ) ) is not { } candidate ) continue;

			//Only the source document; compiled files are old build output (see PortPaths.IsCompiled)
			if ( !PortPaths.IsCompiled( candidate ) ) return (root, candidate);
		}

		return (null, null);
	}

	//A compiled file and its root, for what an install ships only compiled - see PortCompiledMaterial.
	private static (string Root, string File) FindCompiled( List<string> roots, string path )
	{
		var relative = path.Replace( '/', Path.DirectorySeparatorChar );

		foreach ( var root in roots )
		{
			if ( PortPaths.FindFile( Path.Combine( root, relative ) ) is { } candidate ) return (root, candidate);
		}

		return (null, null);
	}

	//What a file names, so that it can be followed.
	internal static IEnumerable<string> Names( string file )
	{
		byte[] bytes;

		try
		{
			var length = new FileInfo( file ).Length;

			//A model can be a hundred megabytes of vertices with a dozen names in it. Reading the whole thing
			//to find them is the cost of not parsing the format, and it is paid once per file at port time.
			if ( length > 256 * 1024 * 1024 ) yield break;

			bytes = File.ReadAllBytes( file );
		}
		catch ( Exception )
		{
			yield break;
		}

		//Their strings are ASCII and null terminated, so the file reads as text well enough to
		//find them in - anything that is not a name comes out as something that matches nothing.
		var text = System.Text.Encoding.ASCII.GetString( bytes );

		foreach ( Match match in Embedded.Matches( text ) )
		{
			var found = match.Groups["path"].Value.Replace( '\\', '/' );

			//A name is a path from a content root. One with a drive letter or a leading slash is somebody's machine,
			//and one with no folder at all is a stray word that happened to end in four of the right letters.
			if ( found.Contains( ':' ) || found.StartsWith( '/' ) ) continue;
			if ( !found.Contains( '/' ) ) continue;

			yield return found;
		}
	}
}
