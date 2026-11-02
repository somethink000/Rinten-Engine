using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Editor;

//When saving a game resource, we inject information about packages used into it. We do that here
//in this special callback because GameResource is in engine.dll, so we can't do it in Serialize()
internal static class GameResourceProcessor
{
	public static void Initialize()
	{
		GameResource.ProcessSerializedObject += ProcessGameResource;
	}

	//Read as text: walking the nodes makes every object build its dictionary, gigabytes that a big scene then keeps
	internal static IEnumerable<string> GetCloudReferences( JsonObject jso )
	{
		var cloudPackages = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var text = new ArrayBufferWriter<byte>( 1024 * 1024 );

		using ( var writer = new Utf8JsonWriter( text, new JsonWriterOptions { SkipValidation = true } ) )
		{
			jso.WriteTo( writer );
		}

		var reader = new Utf8JsonReader( text.WrittenSpan, new JsonReaderOptions { MaxDepth = 4096 } );
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		while ( reader.Read() )
		{
			if ( reader.TokenType != JsonTokenType.String ) continue;

			//Most strings have no extension, and their bytes say so without making a string of each
			var raw = reader.ValueSpan;
			var period = raw.LastIndexOf( (byte)'.' );
			if ( period < 0 || period < raw.Length - 9 ) continue;

			var path = reader.GetString();
			if ( !seen.Add( path ) ) continue;

			//not a path if it has any of these characters in it
			if ( path.Contains( ',' ) || path.Contains( ':' ) || path.Contains( '\"' ) || path.Contains( '\'' ) ) continue;

			var asset = AssetSystem.FindByPath( path );
			if ( asset?.Package is null ) continue;

			cloudPackages.Add( asset.Package.GetIdent( false, true ) );
		}

		return cloudPackages.OrderBy( x => x );
	}

	private static void ProcessGameResource( object target, JsonObject jso )
	{
		jso["__references"] = JsonValue.Create( GetCloudReferences( jso ) );
	}
}
