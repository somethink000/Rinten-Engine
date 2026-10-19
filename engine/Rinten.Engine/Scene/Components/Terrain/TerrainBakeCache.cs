using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Rinten.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rinten;

//On-disk cache of baked chunk models, keyed by everything the bake reads, so it never goes stale; and a pack of them beside the
//terrain or scene, which a game is shipped with and reads first. Flat brush bakes and headless bakes are not cached
internal static class TerrainBakeCache
{
	private static readonly Logger log = new( "Terrain" );

	//Bumped whenever what a bake comes to changes - the model format, the cluster builder, what a
	//vertex carries. Everything written before is then a different key and is swept up by the budget.
	private const uint Version = 4;   //4: far models and the palette first, what a far chunk reads alone

	//The file's own magic, so half a file or somebody else's is a miss rather than a crash.
	private const uint Magic = 0x4B425452; //"RTBK"

	//Past this the oldest go. A default terrain is some tens of megabytes.
	private const long Budget = 1024L * 1024 * 1024;

	private static readonly bool off = Environment.GetEnvironmentVariable( "RINTEN_NO_TERRAIN_CACHE" ) == "1";

	//Beside the shader cache, for the same reason: it belongs to the machine, not to the project.
	private static readonly string folder = Where();

	private static string Where()
	{
		try
		{
			var root = Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData );
			if ( string.IsNullOrEmpty( root ) ) root = Path.GetTempPath();

			return Path.Combine( root, "Rinten", "terrain" );
		}
		catch ( Exception )
		{
			return null;
		}
	}

	/// <summary>What this job would be filed under, or null where it is not to be cached at all. Asked once its modifiers have shaped the ground.</summary>
	public static string Key( TerrainChunkJob job )
	{
		if ( off || folder is null || job is null ) return null;
		if ( !job.Draws || job.Coarse ) return null;
		if ( job.Ground is not { } soup || soup.Indices.Count < 3 ) return null;

		//Two hashes of the same bytes under different seeds - a hundred and twenty-eight
		//bits, because a collision here is not a slow frame but the wrong ground.
		var one = new XxHash3();
		var two = new XxHash3( unchecked((long)0x5bd1e9955bd1e995) );

		void Feed( ReadOnlySpan<byte> bytes )
		{
			one.Append( bytes );
			two.Append( bytes );
		}

		Span<byte> head = stackalloc byte[28];

		BinaryPrimitives.WriteUInt32LittleEndian( head[..4], Version );
		BinaryPrimitives.WriteSingleLittleEndian( head[4..8], job.Min.x );
		BinaryPrimitives.WriteSingleLittleEndian( head[8..12], job.Min.y );
		BinaryPrimitives.WriteSingleLittleEndian( head[12..16], job.Max.x );
		BinaryPrimitives.WriteSingleLittleEndian( head[16..20], job.Max.y );
		BinaryPrimitives.WriteUInt32LittleEndian( head[20..24], (uint)Unsafe.SizeOf<TerrainSoup.Vertex>() );

		//And the model builder's generation: a chunk is a model, and one built by yesterday's builder was found under today's key and drawn for ever.
		BinaryPrimitives.WriteUInt32LittleEndian( head[24..28], ModelFormat.ImportGeneration );

		Feed( head );

		Feed( MemoryMarshal.AsBytes( CollectionsMarshal.AsSpan( soup.Vertices ) ) );
		Feed( MemoryMarshal.AsBytes( CollectionsMarshal.AsSpan( soup.Indices ) ) );
		Feed( MemoryMarshal.AsBytes( CollectionsMarshal.AsSpan( soup.Materials ) ) );

		//What a material number in this chunk means. Only the name matters: that is all the model file keeps of a material.
		foreach ( var material in job.Materials )
			Feed( System.Text.Encoding.UTF8.GetBytes( ( material?.ResourcePath ?? material?.Name ?? "" ) + "\n" ) );

		//A modifier's shaping is in the ground already; what it sets in is its solid, carved or joined, its materials from ModifierMaterialsAt
		var told = new byte[12];

		for ( var m = 0; m < job.Modifiers.Length; m++ )
		{
			var solid = job.Modifiers[m].Solid;

			BinaryPrimitives.WriteInt32LittleEndian( told.AsSpan( 0, 4 ), job.ModifierMaterialsAt[m] );
			BinaryPrimitives.WriteInt32LittleEndian( told.AsSpan( 4, 4 ), job.Modifiers[m].Carve ? 1 : 0 );
			BinaryPrimitives.WriteInt32LittleEndian( told.AsSpan( 8, 4 ), solid?.Indices.Count ?? -1 );
			Feed( told );

			if ( solid is null ) continue;

			Feed( MemoryMarshal.AsBytes( CollectionsMarshal.AsSpan( solid.Vertices ) ) );
			Feed( MemoryMarshal.AsBytes( CollectionsMarshal.AsSpan( solid.Indices ) ) );
			Feed( MemoryMarshal.AsBytes( CollectionsMarshal.AsSpan( solid.Materials ) ) );
		}

		Span<byte> key = stackalloc byte[16];

		BinaryPrimitives.WriteUInt64LittleEndian( key[..8], one.GetCurrentHashAsUInt64() );
		BinaryPrimitives.WriteUInt64LittleEndian( key[8..], two.GetCurrentHashAsUInt64() );

		return Convert.ToHexString( key );
	}

	private static string PathOf( string key ) => Path.Combine( folder, key + ".bake" );

	/// <summary>How many chunks this run read back and how many it had to bake - what the log says after a layout.</summary>
	public static int Hits => hits;
	public static int Misses => misses;

	private static int hits, misses;

	/// <summary>What was filed under that key - shipped, else on this machine - or null; not <paramref name="whole"/>, a far chunk's part.</summary>
	public static TerrainChunkBake Read( string key, string shipped, bool whole )
	{
		if ( key is null ) return null;

		if ( Packed( shipped, key, whole ) is { } packed && Parse( packed, whole ) is { } fromPack )
		{
			System.Threading.Interlocked.Increment( ref hits );
			return fromPack;
		}

		byte[] bytes;

		try
		{
			using var file = File.OpenRead( PathOf( key ) );
			bytes = Front( file, (int)file.Length, whole );
		}
		catch ( Exception )
		{
			//Not there, or somebody is writing it this moment. Either way it is a miss, and a miss costs a bake.
			System.Threading.Interlocked.Increment( ref misses );
			return null;
		}

		var bake = Parse( bytes, whole );

		if ( bake is null ) System.Threading.Interlocked.Increment( ref misses );
		else System.Threading.Interlocked.Increment( ref hits );

		return bake;
	}

	//A filed bake's bytes back into a bake, or null where they are not one of this version's
	private static TerrainChunkBake Parse( ReadOnlySpan<byte> bytes, bool whole )
	{
		try
		{
			var read = new Reader( bytes );

			if ( read.U32() != Magic || read.U32() != Version ) return null;
			if ( read.U32() != (uint)Unsafe.SizeOf<TerrainSoup.Vertex>() ) return null;

			var bake = new TerrainChunkBake { HasFar = true, Whole = whole };

			bake.Palette = read.Blob();
			bake.FarGroundBytes = read.Blob();
			bake.FarSetInBytes = read.Blob();

			read.Into( bake.CollisionPoints );
			read.Into( bake.CollisionIndices );

			if ( bake.Palette is null ) return null;
			if ( !whole ) return bake;

			bake.Ground = new TerrainSoup();
			bake.GroundBytes = read.Blob();
			bake.SetInBytes = read.Blob();

			read.Into( bake.Ground.Vertices );
			read.Into( bake.Ground.Indices );
			read.Into( bake.Ground.Materials );

			return read.Whole ? bake : null;
		}
		catch ( Exception )
		{
			return null;
		}
	}

	//What a far chunk reads of an entry, in order, by the size of one of each: palette, far models, collision.
	private static readonly int[] farParts = [1, 1, 1, 12, 4];

	//The front of an entry, as far as what a far chunk keeps reaches - or all of it.
	private static byte[] Front( Stream from, int length, bool whole )
	{
		if ( whole )
		{
			var all = new byte[length];
			from.ReadExactly( all );

			return all;
		}

		using var into = new MemoryStream();
		var four = new byte[4];

		void Take( int count )
		{
			var some = new byte[count];
			from.ReadExactly( some );
			into.Write( some );
		}

		Take( 12 );

		foreach ( var size in farParts )
		{
			from.ReadExactly( four );
			into.Write( four );
			Take( (int)BinaryPrimitives.ReadUInt32LittleEndian( four ) * size );
		}

		return into.ToArray();
	}

	private const uint PackMagic = 0x50425452; //"RTBP"

	//Each holder's pack by its resource path: where it is and each key's place in it. The index is read the first time a chunk
	//of it is baked, an entry out of the file when asked for, so a large world's pack is never held whole.
	private sealed class Pack
	{
		public string Path;
		public readonly Dictionary<string, (long At, int Length)> Entries = new();
	}

	private static readonly Dictionary<string, Pack> packs = new( StringComparer.OrdinalIgnoreCase );

	private static byte[] Packed( string shipped, string key, bool whole )
	{
		if ( string.IsNullOrEmpty( shipped ) ) return null;

		Pack pack;

		lock ( packs )
		{
			if ( !packs.TryGetValue( shipped, out pack ) ) packs[shipped] = pack = Load( CompiledPaths.TerrainChunks( shipped ) );
		}

		if ( pack is null || !pack.Entries.TryGetValue( key, out var entry ) ) return null;

		try
		{
			using var file = FileSystem.Mounted.OpenRead( pack.Path );
			Skip( file, entry.At );

			return Front( file, entry.Length, whole );
		}
		catch ( Exception )
		{
			return null;
		}
	}

	//Moves a stream on, by reading where it cannot seek.
	private static void Skip( Stream file, long count )
	{
		if ( file.CanSeek )
		{
			file.Seek( count, SeekOrigin.Current );
			return;
		}

		var spare = new byte[64 * 1024];

		while ( count > 0 )
		{
			var read = file.Read( spare, 0, (int)Math.Min( spare.Length, count ) );
			if ( read <= 0 ) throw new EndOfStreamException();

			count -= read;
		}
	}

	private static Pack Load( string path )
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( path ) ) return null;

			using var file = FileSystem.Mounted.OpenRead( path );

			var pack = new Pack { Path = path };
			var head = new byte[20];

			file.ReadExactly( head.AsSpan( 0, 12 ) );

			if ( BinaryPrimitives.ReadUInt32LittleEndian( head ) != PackMagic ) return null;
			if ( BinaryPrimitives.ReadUInt32LittleEndian( head.AsSpan( 4 ) ) != Version ) return null;

			var count = BinaryPrimitives.ReadInt32LittleEndian( head.AsSpan( 8 ) );
			long at = 12;

			for ( var i = 0; i < count; i++ )
			{
				file.ReadExactly( head );

				var key = Convert.ToHexString( head.AsSpan( 0, 16 ) );
				var length = BinaryPrimitives.ReadInt32LittleEndian( head.AsSpan( 16 ) );

				pack.Entries[key] = (at + 20, length);
				at += 20 + length;

				Skip( file, length );
			}

			return pack;
		}
		catch ( Exception e )
		{
			log.Info( $"{path}: the terrain's baked chunks could not be read, so they are baked here - {e.Message}" );
			return null;
		}
	}

	//What each holder's terrain chunks are baked under now, by where the chunk is - written as its pack when it is saved. The editor's alone
	private static readonly Dictionary<string, Dictionary<Vector2, string>> current = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Whether a chunk of anything this resource holds was baked here - what saving it packs.</summary>
	public static bool Holds( string resource )
	{
		if ( string.IsNullOrEmpty( resource ) ) return false;

		lock ( current ) return current.TryGetValue( resource, out var chunks ) && chunks.Count > 0;
	}

	/// <summary>Says which key a chunk of a terrain is baked under now, or that it has none any more.</summary>
	public static void Note( TerrainChunkJob job, string key )
	{
		if ( !Application.IsEditor || job is null || string.IsNullOrEmpty( job.Shipped ) || job.Coarse ) return;

		lock ( current )
		{
			if ( !current.TryGetValue( job.Shipped, out var chunks ) ) current[job.Shipped] = chunks = new();

			if ( key is null ) chunks.Remove( job.Min );
			else chunks[job.Min] = key;
		}
	}

	/// <summary>Writes a holder's pack out of this machine's cache, every chunk as it now stands; nothing where none was baked here.</summary>
	public static void WritePack( string shipped, string path )
	{
		if ( string.IsNullOrEmpty( shipped ) || string.IsNullOrEmpty( path ) ) return;

		string[] keys;

		lock ( current )
		{
			if ( !current.TryGetValue( shipped, out var chunks ) || chunks.Count == 0 ) return;
			keys = chunks.Values.Distinct().ToArray();
		}

		try
		{
			using var into = new MemoryStream();
			using var write = new BinaryWriter( into );

			write.Write( PackMagic );
			write.Write( Version );

			var counted = into.Position;
			write.Write( 0 );

			var count = 0;

			foreach ( var key in keys )
			{
				byte[] bytes;

				try { bytes = File.ReadAllBytes( PathOf( key ) ); }
				catch ( Exception ) { continue; }

				write.Write( Convert.FromHexString( key ) );
				write.Write( bytes.Length );
				write.Write( bytes );
				count++;
			}

			into.Position = counted;
			write.Write( count );
			write.Flush();

			var temporary = $"{path}.{Environment.ProcessId}.part";

			File.WriteAllBytes( temporary, into.ToArray() );
			File.Move( temporary, path, true );

			lock ( packs ) packs.Remove( shipped );

			log.Info( $"{shipped}: {count} of {keys.Length} baked chunks packed beside it for the game, {into.Length / 1024} KB" );
		}
		catch ( Exception e )
		{
			log.Info( $"{shipped}: its baked chunks were not packed - {e.Message}" );
		}
	}

	/// <summary>Files a finished bake, having already let go of the geometry it was written from.</summary>
	public static void Write( string key, TerrainChunkBake bake )
	{
		if ( key is null || bake?.Ground is null ) return;

		try
		{
			Directory.CreateDirectory( folder );

			var write = new Writer();

			write.U32( Magic );
			write.U32( Version );
			write.U32( (uint)Unsafe.SizeOf<TerrainSoup.Vertex>() );

			//What a far chunk reads, first - see Front.
			write.Blob( bake.Palette );
			write.Blob( bake.FarGroundBytes );
			write.Blob( bake.FarSetInBytes );

			write.From( bake.CollisionPoints );
			write.From( bake.CollisionIndices );

			write.Blob( bake.GroundBytes );
			write.Blob( bake.SetInBytes );

			write.From( bake.Ground.Vertices );
			write.From( bake.Ground.Indices );
			write.From( bake.Ground.Materials );

			//Named for whoever is writing it, so two of them baking the same chunk do not write over
			//each other half way, and moved into place in one go so a reader sees a whole file or none.
			var temporary = $"{PathOf( key )}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.part";

			File.WriteAllBytes( temporary, write.Bytes() );
			File.Move( temporary, PathOf( key ), true );
		}
		catch ( Exception e )
		{
			//A cache that cannot be written is a cache that is not used. Said once, because the next chunk will fail the same way.
			if ( System.Threading.Interlocked.Exchange( ref complained, 1 ) == 0 )
				log.Info( $"the terrain bake cache is not being written - {e.Message}" );
		}

		Sweep();
	}

	private static int complained;

	//-- keeping it to a size -------------------------------------------------

	private static int swept;

	//Once a run, in the background: the oldest go until what is left is well under the budget. By when a file was last written, which a read does
	//not change - so this drops what nothing has baked in a long time, which after a sculpt is every key the old shape of the ground was under.
	private static void Sweep()
	{
		if ( System.Threading.Interlocked.Exchange( ref swept, 1 ) != 0 ) return;

		System.Threading.Tasks.Task.Run( () =>
		{
			try
			{
				var files = new DirectoryInfo( folder ).GetFiles( "*.bake" );

				long held = 0;
				foreach ( var file in files ) held += file.Length;

				if ( held <= Budget ) return;

				System.Array.Sort( files, ( x, y ) => x.LastWriteTimeUtc.CompareTo( y.LastWriteTimeUtc ) );

				foreach ( var file in files )
				{
					if ( held <= ( Budget * 4 ) / 5 ) break;

					held -= file.Length;

					try { file.Delete(); }
					catch ( Exception ) { }
				}
			}
			catch ( Exception )
			{
			}
		} );
	}

	//-- the bytes ------------------------------------------------------------

	private sealed class Writer
	{
		private readonly List<byte> bytes = new();

		public void U32( uint value )
		{
			Span<byte> four = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian( four, value );
			bytes.AddRange( four );
		}

		/// <summary>A run of bytes, or nothing at all - which reads back as null.</summary>
		public void Blob( byte[] blob )
		{
			U32( blob is null ? 0u : (uint)blob.Length );
			if ( blob is not null ) bytes.AddRange( blob );
		}

		/// <summary>A list of something flat, as the bytes it is made of.</summary>
		public void From<T>( List<T> list ) where T : unmanaged
		{
			U32( (uint)list.Count );
			bytes.AddRange( MemoryMarshal.AsBytes( CollectionsMarshal.AsSpan( list ) ) );
		}

		public byte[] Bytes() => bytes.ToArray();
	}

	private ref struct Reader
	{
		private readonly ReadOnlySpan<byte> bytes;
		private int at;

		public Reader( ReadOnlySpan<byte> bytes )
		{
			this.bytes = bytes;
			at = 0;
		}

		/// <summary>Whether the file ended exactly where the reading did.</summary>
		public readonly bool Whole => at == bytes.Length;

		public uint U32()
		{
			var value = BinaryPrimitives.ReadUInt32LittleEndian( bytes.Slice( at, 4 ) );
			at += 4;

			return value;
		}

		public byte[] Blob()
		{
			var count = (int)U32();
			if ( count == 0 ) return null;

			var blob = bytes.Slice( at, count ).ToArray();
			at += count;

			return blob;
		}

		public void Into<T>( List<T> list ) where T : unmanaged
		{
			var count = (int)U32();
			var size = count * Unsafe.SizeOf<T>();

			list.Clear();
			list.AddRange( MemoryMarshal.Cast<byte, T>( bytes.Slice( at, size ) ) );

			at += size;
		}
	}
}
