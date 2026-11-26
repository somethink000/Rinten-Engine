using System.Reflection;
using System.Text.Json;

namespace Rinten;

//What code a shipped build was compiled from, so it joins only servers running it; and a web build's baked-in game types
internal static class WebCode
{
	private static readonly Logger log = new( "Web" );

	//What code this page was compiled from, by assembly: the fingerprints of its sources (see Fingerprint) as the web export
	//wrote them - one as an editor hosting the game sends them, one as a dedicated server does, without its server code.
	private static Dictionary<string, List<string>> compiledFrom = new( StringComparer.OrdinalIgnoreCase );

	//The fingerprints the export wrote - see WebManifest.Code.
	internal static void CompiledFrom( Dictionary<string, List<string>> code )
		=> compiledFrom = code is null ? new( StringComparer.OrdinalIgnoreCase ) : new( code, StringComparer.OrdinalIgnoreCase );

	//Whether the code a server sends is the code this page was compiled from. A page has no compiler, so a server's code can
	//only be taken where it already is the page's: the same sources, archive for archive. Null where it is; otherwise why not.
	internal static string Refuses( IEnumerable<(string Name, byte[] Archive)> sent )
	{
		foreach ( var (name, archive) in sent )
		{
			if ( !compiledFrom.TryGetValue( name, out var known ) )
				return $"This server runs code ({name}) that this build was not made with.";

			string mine;

			try
			{
				mine = Fingerprint( archive );
			}
			catch ( Exception e )
			{
				return $"This server sent code ({name}) this build cannot read: {e.Message}";
			}

			if ( !known.Contains( mine, StringComparer.OrdinalIgnoreCase ) )
				return $"This server runs a different version of {name} than this build was made from - export it again from the server's project.";
		}

		return null;
	}

	//SHA-256 over a code archive's source texts, in hash order: paths differ between a server and an export
	//(absolute or relative), and generated files under /.obj/ follow the build configuration, not the code
	internal static string Fingerprint( byte[] archive )
	{
		using var compressed = ByteStream.CreateReader( archive );
		using var stream = compressed.Decompress();

		if ( stream.Read<string>() != "GMCA" ) throw new System.IO.InvalidDataException( "not a code archive" );

		_ = stream.Read<long>();
		_ = stream.Read<string>(); //the compiler's name
		_ = stream.Read<string>(); //its configuration
		_ = stream.Read<string>(); //the references

		var texts = new List<string>();

		foreach ( var list in new[] { stream.Read<string>(), stream.Read<string>() } )
		{
			using var document = JsonDocument.Parse( list );

			foreach ( var file in document.RootElement.EnumerateArray() )
			{
				var path = ( file.GetProperty( "LocalPath" ).GetString() ?? "" ).Replace( '\\', '/' );
				if ( path.StartsWith( "/.obj/", StringComparison.Ordinal ) ) continue;

				var text = ( file.GetProperty( "Text" ).GetString() ?? "" ).Replace( "\r\n", "\n" );
				texts.Add( Convert.ToHexString( System.Security.Cryptography.SHA256.HashData( System.Text.Encoding.UTF8.GetBytes( text ) ) ) );
			}
		}

		texts.Sort( StringComparer.Ordinal );

		return Convert.ToHexString( System.Security.Cryptography.SHA256.HashData( System.Text.Encoding.UTF8.GetBytes( string.Join( "\n", texts ) ) ) );
	}

	//One of the game's assemblies, compiled into the module with the engine, into the library. Throws where the module has no such assembly.
	internal static Assembly Baked( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return null;

		var assembly = Assembly.Load( new AssemblyName( name ) );

		Game.TypeLibrary.AddAssembly( assembly, true );

		log.Info( $"compiled in: {assembly.GetName().Name}" );

		return assembly;
	}
}
