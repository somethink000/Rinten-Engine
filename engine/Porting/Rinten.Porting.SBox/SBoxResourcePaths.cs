using System.IO;
using System.Text.RegularExpressions;

namespace Rinten.Porting.SBox;

//Repoints resource paths (.vmdl, .vmat...) at files the port wrote; names with no ported file stay
internal static class SBoxResourcePaths
{
	private static readonly Logger log = new( "Porter" );

	//Theirs and ours, for the kinds the port writes a file of its own for. Deliberately short. A picture named by its compiled .vtex
	//name is pointed at the image beside it (see Pictures); sounds are left alone, their names resolved by machinery that knows both.
	private static readonly Dictionary<string, string> Ours = new( StringComparer.OrdinalIgnoreCase )
	{
		[".vmdl"] = ".mdl",
		[".vmat"] = ".mat",

		//A terrain layer, which is a material on the terrain layer shader here
		[".tmat"] = ".mat",

		//A level. There is no map format on this side - SBoxMaps writes one out as a prefab - so every name that asked for the map has to ask for the
		//prefab. What named one through their map component is not left as a name at all: SBoxMapPlacements puts the prefab itself into the scene.
		[".vmap"] = ".prefab",
	};

	//What a picture named by its compiled name may be saved as here, likeliest first
	private static readonly string[] Pictures = [".png", ".tga", ".jpg", ".jpeg", ".psd"];

	//A quoted resource path ending in one of their extensions.
	private static readonly Regex Named = new(
		@"(?<=""|')(?<path>[A-Za-z0-9_@\-./\\ ]+)(?<kind>\.vmdl|\.vmat|\.tmat|\.vanmgrph|\.vmap|\.vtex)(?=""|')",
		RegexOptions.Compiled | RegexOptions.IgnoreCase );

	//Files whose bytes are not text and are never searched.
	private static readonly HashSet<string> Binary = new( StringComparer.OrdinalIgnoreCase )
	{
		".png", ".jpg", ".jpeg", ".tga", ".bmp", ".gif", ".webp", ".psd", ".dds",
		".mp3", ".wav", ".ogg", ".flac", ".fbx", ".obj", ".dmx", ".ttf", ".otf",
		".dll", ".pdb", ".exe", ".zip", ".7z", ".vox", ".bin", ".ico",
	};

	/// <summary>Rewrite every reference in a ported project. Returns how many changed.</summary>
	public static int Rewrite( string assets, string destination, PortReport report )
	{
		var changed = 0;
		var files = 0;

		var roots = Roots( assets, destination );

		foreach ( var file in PortPaths.Walk( destination ) )
		{
			var extension = Path.GetExtension( file );

			//Their compiled files keep their own names inside them, and rewriting a name in a file this port does not write is a file broken for nothing.
			if ( PortPaths.IsCompiled( file ) || PortPaths.IsBlobSidecar( file ) ) continue;
			if ( Binary.Contains( extension ) ) continue;

			var count = RewriteFile( file, roots, report );

			if ( count == 0 ) continue;

			changed += count;
			++files;
		}

		if ( changed > 0 )
		{
			report.Notes.Add( $"{changed} resource path(s) in {files} file(s) were pointed at " +
				"the files the port wrote, rather than at the names s&box knew them by." );
		}

		log.Info( $"resource paths: {changed} in {files} file(s)" );

		return changed;
	}

	//Every Assets folder in the ported project, its own first; libraries share one resource namespace
	private static List<string> Roots( string assets, string destination )
	{
		var roots = new List<string> { assets };

		try
		{
			foreach ( var found in Directory.EnumerateDirectories( destination, "Assets",
				SearchOption.AllDirectories ) )
			{
				if ( PortPaths.Local( destination, found ) is not { } local ) continue;
				if ( local.Split( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar )
					.Any( part => part is "obj" or "bin" ) )
				{
					continue;
				}

				if ( !roots.Contains( found, StringComparer.OrdinalIgnoreCase ) ) roots.Add( found );
			}
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			//Whatever was reachable is what gets looked in.
		}

		return roots;
	}

	//The name the port wrote this file under in any of them, as spelt on disk, or null. Their names are case-blind - a material
	//asked for as planks012 sits in Planks012 - and on a case-sensitive disk only the disk's spelling finds the file again
	private static string Wrote( List<string> roots, string wanted )
	{
		var parts = wanted.Replace( '\\', '/' ).TrimStart( '/' ).Split( '/', StringSplitOptions.RemoveEmptyEntries );

		foreach ( var root in roots )
		{
			var at = root;
			var spelt = new List<string>( parts.Length );

			foreach ( var part in parts )
			{
				var exact = Path.Combine( at, part );
				string found = Directory.Exists( exact ) || File.Exists( exact ) ? part : null;

				if ( found is null && Directory.Exists( at ) )
				{
					found = Directory.EnumerateFileSystemEntries( at )
						.Select( Path.GetFileName )
						.FirstOrDefault( x => string.Equals( x, part, StringComparison.OrdinalIgnoreCase ) );
				}

				if ( found is null ) { spelt = null; break; }

				spelt.Add( found );
				at = Path.Combine( at, found );
			}

			if ( spelt is not null && File.Exists( at ) ) return string.Join( '/', spelt );
		}

		return null;
	}

	//Rewrite one file in place. Returns how many names changed.
	private static int RewriteFile( string file, List<string> roots, PortReport report )
	{
		string text;

		try
		{
			//Cheap first: most files name nothing, and reading a whole project twice over to find the few that do is the slow way round.
			if ( new FileInfo( file ).Length > 32 * 1024 * 1024 ) return 0;

			text = File.ReadAllText( file );
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException or ArgumentException )
		{
			return 0;
		}

		//A file of bytes that happened to have a text extension. Reading one is harmless; writing it back is not.
		if ( text.Contains( '\0' ) ) return 0;

		var changed = 0;

		var rewritten = Named.Replace( text, match =>
		{
			var path = match.Groups["path"].Value;
			var kind = match.Groups["kind"].Value;

			//Not a file here: a graph came across inside the model that named it, and a name that asked for the graph asks for that model's.
			if ( kind.Equals( ".vanmgrph", StringComparison.OrdinalIgnoreCase ) )
			{
				var named = ( path + kind ).Replace( '\\', '/' ).TrimStart( '/' );

				if ( !report.GraphOwners.TryGetValue( named, out var owner ) || Wrote( roots, owner ) is null )
					return match.Value;

				++changed;

				return AnimGraph.ModelGraphName( owner );
			}

			//A picture by its compiled name - a particle's sprite: the image the port wrote under that name, whatever it was saved as.
			//Nothing here reads a .vtex, so left as it was the sprite drew with no picture at all
			if ( kind.Equals( ".vtex", StringComparison.OrdinalIgnoreCase ) )
			{
				foreach ( var image in Pictures )
				{
					if ( Wrote( roots, path + image ) is not { } picture ) continue;

					++changed;
					return picture;
				}

				return match.Value;
			}

			if ( !Ours.TryGetValue( kind, out var ours ) ) return match.Value;

			var wanted = path + ours;

			//Only where the port wrote it. Their engine's own content stays under their names and keeps working the way it does now.
			if ( Wrote( roots, wanted ) is not { } onDisk ) return match.Value;

			++changed;

			return onDisk;
		} );

		if ( changed == 0 ) return 0;

		try
		{
			File.WriteAllText( file, rewritten );
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			report.Skipped.Add( $"{Path.GetFileName( file )}: could not have its resource paths rewritten - {e.Message}" );
			return 0;
		}

		return changed;
	}
}
