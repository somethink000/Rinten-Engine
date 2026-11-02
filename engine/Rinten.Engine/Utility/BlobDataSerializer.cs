using System.Buffers.Binary;
using System.IO;
using System.Text.Json.Nodes;
using Rinten.Hashing;

namespace Rinten;

//Manages binary blobs during JSON serialization with support for nested contexts.
internal static class BlobDataSerializer
{
	public const string CompiledBlobName = "DBLOB";

	private const int DefaultStreamSize = 4096;
	private const int HeaderSize = 8;
	private const int TocEntrySize = 32;

	internal readonly record struct RegisteredBlob( BlobData Blob, byte[] Data );

	private static BlobContext _current;

	//Indicates whether a blob context is currently active.
	internal static bool IsActive => _current != null;

	//Registers a BlobData instance for serialization in the current blob context. Returns a Guid that can be used to reference the blob in
	//JSON. If no blob context is active, returns Guid.Empty. Called automatically by BinaryBlobJsonConverter during JSON serialization.
	internal static Guid RegisterBlob( BlobData blob )
	{
		if ( _current == null )
			return Guid.Empty;

		//The id is derived from the serialized content, so unchanged data always produces
		//the same id - repeated saves stay byte-identical and identical blobs deduplicate.
		var data = SerializeBlob( blob );
		var guid = DeriveContentGuid( data );

		_current.Blobs[guid] = new RegisteredBlob( blob, data );
		return guid;
	}

	//Serialize a blob to its binary payload: version prefix followed by the blob data.
	private static byte[] SerializeBlob( BlobData blob )
	{
		if ( blob.Kept is { } kept ) return kept;

		var stream = ByteStream.Create( DefaultStreamSize );
		try
		{
			stream.Write( blob.Version );
			var writer = new BlobData.Writer { Stream = stream };
			blob.Serialize( ref writer );
			stream = writer.Stream;
			return stream.ToArray();
		}
		finally
		{
			stream.Dispose();
		}
	}

	//Deterministically derive a blob id from its content, marked as an RFC 4122 version 8 (custom) guid so derived ids are well formed.
	private static Guid DeriveContentGuid( ReadOnlySpan<byte> data )
	{
		Span<byte> derived = stackalloc byte[16];
		BinaryPrimitives.WriteUInt64LittleEndian( derived[..8], XxHash3.HashToUInt64( data ) );
		BinaryPrimitives.WriteUInt64LittleEndian( derived[8..], XxHash3.HashToUInt64( data, seed: 0x5bd1e9955bd1e995 ) );

		derived[7] = (byte)((derived[7] & 0x0F) | 0x80);
		derived[8] = (byte)((derived[8] & 0x3F) | 0x80);

		return new Guid( derived );
	}

	internal static BlobData ReadBlob( Guid guid, Type expectedType )
	{
		if ( _current == null )
			return null;

		if ( _current.Blobs.TryGetValue( guid, out var registeredBlob ) )
			return registeredBlob.Blob;

		//Fall back to pre-existing binary data loaded from file
		var binaryData = _current.BinaryData;
		if ( binaryData == null || !binaryData.TryGetValue( guid, out var blobData ) )
			return null;

		return FromBytes( blobData, expectedType );
	}

	//A blob's payload - version first, then its data - for writing inline where no blob context is there to hold it. See BinaryConvert.
	internal static byte[] ToBytes( BlobData blob ) => SerializeBlob( blob );

	//A blob read back from its payload, upgraded where it is older than its type.
	internal static BlobData FromBytes( byte[] blobData, Type expectedType )
	{
		if ( blobData is null || blobData.Length < sizeof( int ) )
			return null;

		if ( Activator.CreateInstance( expectedType ) is not BlobData instance )
			return null;

		var stream = ByteStream.CreateReader( blobData );
		try
		{
			int dataVersion = stream.Read<int>();
			var reader = new BlobData.Reader { Stream = stream, DataVersion = dataVersion };

			if ( dataVersion < instance.Version )
				instance.Upgrade( ref reader, dataVersion );
			else
				instance.Deserialize( ref reader );

			if ( instance.KeepsBytes && dataVersion == instance.Version ) instance.Kept = blobData;
		}
		finally
		{
			stream.Dispose();
		}

		return instance;
	}

	private static byte[] GetBlobData( Dictionary<Guid, RegisteredBlob> blobs )
	{
		if ( blobs == null || blobs.Count == 0 )
			return null;

		//Canonical order so unchanged content always produces byte-identical output, independent of registration order or dictionary enumeration details
		var ordered = blobs.OrderBy( x => x.Key ).ToArray();

		long offset = HeaderSize + blobs.Count * TocEntrySize;

		long totalSize = offset;
		foreach ( var kvp in ordered )
			totalSize += kvp.Value.Data.Length;

		var outputStream = ByteStream.Create( (int)totalSize );
		try
		{
			outputStream.Write( 1 );
			outputStream.Write( blobs.Count );

			foreach ( var kvp in ordered )
			{
				outputStream.Write( kvp.Key.ToByteArray() );
				outputStream.Write( kvp.Value.Blob.Version );
				outputStream.Write( offset );
				outputStream.Write( kvp.Value.Data.Length );
				offset += kvp.Value.Data.Length;
			}

			foreach ( var kvp in ordered )
			{
				outputStream.Write( kvp.Value.Data );
			}

			return outputStream.ToArray();
		}
		finally
		{
			outputStream.Dispose();
		}
	}

	private static Dictionary<Guid, byte[]> ParseFile( ReadOnlySpan<byte> data )
	{
		var result = new Dictionary<Guid, byte[]>();
		if ( data.Length == 0 ) return result;

		try
		{
			var stream = ByteStream.CreateReader( data );
			try
			{
				int version = stream.Read<int>();
				if ( version != 1 ) return result;

				int count = stream.Read<int>();
				var toc = new List<(Guid guid, long offset, int size)>( count );

				for ( int i = 0; i < count; i++ )
				{
					var guid = stream.Read<Guid>();
					stream.Read<int>();
					long blobOffset = stream.Read<long>();
					int size = stream.Read<int>();
					toc.Add( (guid, blobOffset, size) );
				}

				foreach ( var (guid, blobOffset, size) in toc )
				{
					stream.Position = (int)blobOffset;
					var buffer = new byte[size];
					stream.Read( buffer, 0, size );
					result[guid] = buffer;
				}
			}
			finally
			{
				stream.Dispose();
			}
		}
		catch ( Exception e )
		{
			Log.Warning( e, "Failed to parse blob data" );
		}

		return result;
	}

	/// <summary>Create a blob serialization context for capturing blobs.</summary>
	public static BlobContext Capture()
	{
		var context = new BlobContext( _current );
		_current = context;
		return context;
	}

	//Suppresses the blob context so nested serialization writes inline data, for JSON that outlives it
	internal static IDisposable Suppress()
	{
		return new SuppressScope( _current );
	}

	private sealed class SuppressScope : IDisposable
	{
		private readonly BlobContext _previous;

		public SuppressScope( BlobContext previous )
		{
			_previous = previous;
			_current = null;
		}

		public void Dispose() => _current = _previous;
	}

	/// <summary>Load blob data from memory if available, otherwise from file path.</summary>
	public static BlobContext Load( byte[] data, string filePath )
	{
		return data != null ? LoadFromMemory( data ) : LoadFrom( filePath );
	}

	/// <summary>Create a blob deserialization context from in-memory data.</summary>
	public static BlobContext LoadFromMemory( ReadOnlySpan<byte> data )
	{
		var binaryData = data.Length > 0 ? ParseFile( data ) : null;
		var context = new BlobContext( _current, binaryData );
		_current = context;
		return context;
	}

	//The blob table of a resource, parsed: from data where it is in memory, otherwise from the resource's files. For
	//something that loads the same resource again and again and wants to read the files once - see PrefabCacheScene.
	internal static Dictionary<Guid, byte[]> ReadTable( byte[] data, string filePath )
	{
		return data != null ? ParseFile( data ) : ResolveBlobData( filePath );
	}

	//Create a blob deserialization context from a table ReadTable made. The table is copied, so the context can be added to without changing it.
	internal static BlobContext LoadTable( Dictionary<Guid, byte[]> table )
	{
		var context = new BlobContext( _current, table is null ? null : new Dictionary<Guid, byte[]>( table ) );
		_current = context;
		return context;
	}

	//Create a blob deserialization context from blob data stored on a json object by BlobContext.SaveTo.
	internal static BlobContext LoadFrom( JsonNode json )
	{
		byte[] data = null;

		if ( json?["__blobdata"]?.GetValue<string>() is string base64 )
			data = Convert.FromBase64String( base64 );

		return LoadFromMemory( data );
	}

	/// <summary>Create a blob deserialization context from a file.</summary>
	public static BlobContext LoadFrom( string filePath )
	{
		var binaryData = ResolveBlobData( filePath );

		var context = new BlobContext( _current, binaryData );
		_current = context;
		return context;
	}

	//Finds and parses the blob table for a resource, searching all relevant filesystems and file variations.
	//Checks both "_d" sidecar and compiled "_c" files, with disk fallback if needed (for published resources).
	private static Dictionary<Guid, byte[]> ResolveBlobData( string filePath )
	{
		if ( string.IsNullOrEmpty( filePath ) )
			return null;

		if ( filePath.EndsWith( "_c" ) )
			filePath = filePath[..^2];

		var sidecarPath = CompiledPaths.BlobSidecar( filePath );
		var compiledPath = filePath + "_c";

		ReadOnlySpan<BaseFileSystem> filesystems =
		[
			FileSystem.Mounted,
			PackageManager.MountedFileSystem,
		];

		foreach ( var fs in filesystems )
		{
			if ( fs is null )
				continue;

			//The "_d" sidecar is already stored in the blob-file format.
			if ( fs.FileExists( sidecarPath ) )
				return ParseFile( fs.ReadAllBytes( sidecarPath ) );

			//Otherwise pull the compiled blob block out of the compiled resource.
			if ( fs.FileExists( compiledPath ) )
			{
				var blockData = Game.Resources.ReadCompiledResourceBlock( CompiledBlobName, fs.ReadAllBytes( compiledPath ) );
				if ( blockData != null )
					return ParseFile( blockData );
			}
		}

		//Final fallback: a loose sidecar on raw disk (e.g. freshly saved in the editor and not yet visible through a mounted filesystem).
		if ( File.Exists( sidecarPath ) )
			return ParseFile( SealedFile.ReadAllBytes( sidecarPath ) );

		return null;
	}

	/// <summary>A disposable context for blob serialization/deserialization.</summary>
	public sealed class BlobContext : IDisposable
	{
		internal readonly Dictionary<Guid, RegisteredBlob> Blobs;
		internal readonly Dictionary<Guid, byte[]> BinaryData;
		private readonly BlobContext _previous;

		internal BlobContext( BlobContext previous, Dictionary<Guid, byte[]> binaryData = null )
		{
			Blobs = new();
			BinaryData = binaryData ?? new();
			_previous = previous;
		}

		public byte[] ToByteArray() => GetBlobData( Blobs );

		//Merge extra serialized blob data into this open context, so separately-captured blobs stay readable through one deferred deserialize pass.
		internal void Load( ReadOnlySpan<byte> data )
		{
			if ( data.Length == 0 ) return;

			foreach ( var kvp in ParseFile( data ) )
				BinaryData[kvp.Key] = kvp.Value;
		}

		//Merge blob data stored on a json object by SaveTo into this open context.
		internal void LoadFrom( JsonNode json )
		{
			if ( json?["__blobdata"]?.GetValue<string>() is string base64 )
				Load( Convert.FromBase64String( base64 ) );
		}

		//Store the captured blob data on a json object, so it travels with it.
		internal bool SaveTo( JsonNode json )
		{
			if ( json is not JsonObject jso ) return false;

			var data = GetBlobData( Blobs );
			if ( data == null || data.Length == 0 ) return false;

			jso["__blobdata"] = Convert.ToBase64String( data );
			return true;
		}

		public bool SaveTo( string filePath )
		{
			var data = GetBlobData( Blobs );
			if ( data == null || data.Length == 0 ) return false;

			try
			{
				File.WriteAllBytes( CompiledPaths.BlobSidecar( filePath ), data );
				return true;
			}
			catch ( Exception e )
			{
				Log.Warning( e, "Failed to write blob file" );
				return false;
			}
		}

		public void Dispose() => _current = _previous;
	}
}
