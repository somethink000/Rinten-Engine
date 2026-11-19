using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Rinten.Porting.SBox;

//Their terrain grid runs +x forward, +y left from the corner; ours runs +x, +z from it. Our basis takes their
//cell (i, j) to (-j, -i) from the corner, a mirror, so the maps are transposed and the terrain turned half about up.
internal static class SBoxTerrains
{
	//The blob table their _d files start with: version, count, then 32 bytes per entry - see SBoxSceneBlobs
	private const int HeaderSize = 8;
	private const int TocEntrySize = 32;

	/// <summary>Inlines a .terrain's maps from the source _d, transposed; true when the file changed.</summary>
	public static bool ConvertStorage( string terrain, string source, string named, PortReport report )
	{
		var root = JsonNode.Parse( File.ReadAllText( terrain ) ) as JsonObject;

		if ( root?["Maps"] is not JsonObject maps || maps["$blob"]?.GetValue<string>() is not { } id
			|| !Guid.TryParse( id, out var guid ) )
			return false;

		var side = root["Resolution"]?.GetValue<int>() ?? 0;

		if ( !Blobs( source + "_d" ).TryGetValue( guid, out var payload ) )
		{
			report.Skipped.Add( $"{named}: its heightmap is in a blob the source has no _d for - the ground comes across flat" );
			return false;
		}

		var transposed = Transposed( payload, side );

		if ( transposed is null )
		{
			report.Skipped.Add( $"{named}: its heightmap blob could not be read - the ground comes across flat" );
			return false;
		}

		root["Maps"] = new JsonObject { ["$data"] = Convert.ToBase64String( transposed ) };
		File.WriteAllText( terrain, root.ToJsonString() );

		report.Notes.Add( $"{named}: heightmap and paint written into the file, turned onto this engine's axes" );

		return true;
	}

	/// <summary>Turns every terrain object in a scene or prefab half a turn about up, to go with its transposed maps.</summary>
	public static int TurnObjects( string scene )
	{
		var root = JsonNode.Parse( File.ReadAllText( scene ) );
		var turned = 0;

		Walk( root, ref turned );

		if ( turned > 0 ) File.WriteAllText( scene, root.ToJsonString() );

		return turned;
	}

	private static void Walk( JsonNode node, ref int turned )
	{
		if ( node is JsonArray array )
		{
			foreach ( var item in array ) Walk( item, ref turned );
			return;
		}

		if ( node is not JsonObject obj ) return;

		if ( obj["Components"] is JsonArray components
			&& components.Any( x => x?["__type"]?.GetValue<string>() == "Rinten.Terrain" ) )
		{
			var rotation = Rotation.Parse( obj["Rotation"]?.GetValue<string>() ?? "0,0,0,1" );
			var half = new Rotation( 0.0f, 1.0f, 0.0f, 0.0f );
			var r = rotation * half;

			obj["Rotation"] = $"{r.x:R},{r.y:R},{r.z:R},{r.w:R}";
			++turned;
		}

		foreach ( var (_, value) in obj ) Walk( value, ref turned );
	}

	//Their map blob (version, then named deflated maps) with every map swapped across its diagonal
	private static byte[] Transposed( byte[] payload, int side )
	{
		if ( side < 2 ) return null;

		try
		{
			using var from = new BinaryReader( new MemoryStream( payload ) );
			using var output = new MemoryStream();
			using var to = new BinaryWriter( output );

			to.Write( from.ReadInt32() );

			var count = from.ReadInt32();
			to.Write( count );

			for ( var m = 0; m < count; m++ )
			{
				var name = System.Text.Encoding.UTF8.GetString( from.ReadBytes( from.ReadInt32() ) );
				var size = from.ReadByte();
				var elements = from.ReadInt32();
				var data = Inflate( from.ReadBytes( from.ReadInt32() ) );

				if ( elements == side * side && data.Length == elements * size )
					data = Transpose( data, side, size );

				var packed = Deflate( data );
				var nameBytes = System.Text.Encoding.UTF8.GetBytes( name );

				to.Write( nameBytes.Length );
				to.Write( nameBytes );
				to.Write( size );
				to.Write( elements );
				to.Write( packed.Length );
				to.Write( packed );
			}

			to.Flush();
			return output.ToArray();
		}
		catch ( Exception e ) when ( e is IOException or InvalidDataException or ArgumentException )
		{
			return null;
		}
	}

	private static byte[] Transpose( byte[] data, int side, int size )
	{
		var result = new byte[data.Length];

		for ( var j = 0; j < side; j++ )
		{
			for ( var i = 0; i < side; i++ )
				Buffer.BlockCopy( data, ( ( j * side ) + i ) * size, result, ( ( i * side ) + j ) * size, size );
		}

		return result;
	}

	private static byte[] Inflate( byte[] packed )
	{
		using var input = new DeflateStream( new MemoryStream( packed ), CompressionMode.Decompress );
		using var output = new MemoryStream();
		input.CopyTo( output );
		return output.ToArray();
	}

	private static byte[] Deflate( byte[] data )
	{
		using var output = new MemoryStream();
		using ( var into = new DeflateStream( output, CompressionLevel.Optimal, leaveOpen: true ) ) into.Write( data );
		return output.ToArray();
	}

	//Guid to payload from their blob file; empty where there is none
	private static Dictionary<Guid, byte[]> Blobs( string path )
	{
		var found = new Dictionary<Guid, byte[]>();

		if ( !File.Exists( path ) ) return found;

		var bytes = File.ReadAllBytes( path );
		if ( bytes.Length < HeaderSize || BitConverter.ToInt32( bytes, 0 ) != 1 ) return found;

		var count = BitConverter.ToInt32( bytes, 4 );

		for ( var i = 0; i < count; i++ )
		{
			var entry = HeaderSize + ( i * TocEntrySize );
			if ( entry + TocEntrySize > bytes.Length ) break;

			var offset = BitConverter.ToInt64( bytes, entry + 20 );
			var size = BitConverter.ToInt32( bytes, entry + 28 );
			if ( offset < 0 || size < 0 || offset + size > bytes.Length ) break;

			found[new Guid( bytes.AsSpan( entry, 16 ) )] = bytes.AsSpan( (int)offset, size ).ToArray();
		}

		return found;
	}
}
