using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Rinten.CoreAssets;

//Compiled model reader: read once into one array, everything points into it
internal sealed unsafe class ModelFile : IDisposable
{
	private byte[] bytes;

	//The clusters cut on load, for a file written before the format carried them. Null when the file brought its own - see Own.
	private LevelChain.Build own;

	private float[] ownPositions;

	/// <summary>Unique per-model id from a counter, for caches keyed by model</summary>
	public ulong Id { get; }

	private static ulong nextId = 1;

	//Total compiled model bytes and files held in memory, for PerfReport
	internal static long HeldBytes;
	internal static int HeldFiles;

	//Who loaded it, for the perf report's breakdown of held bytes
	internal string Tag;

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ModelFile, object> live = new();

	private ModelFile( byte[] bytes )
	{
		this.bytes = bytes;

		Id = System.Threading.Interlocked.Increment( ref nextId );

		System.Threading.Interlocked.Add( ref HeldBytes, bytes.Length );
		System.Threading.Interlocked.Increment( ref HeldFiles );

		live.AddOrUpdate( this, null );
	}

	/// <summary>Held model bytes summed by tag kind (digits dropped) and by block, for the perf report.</summary>
	internal static (List<(string Kind, long Bytes, int Count)> Kinds, List<(string Block, long Bytes)> Blocks) HeldBreakdown()
	{
		var kinds = new Dictionary<string, (long Bytes, int Count)>();
		var blocks = new Dictionary<string, long>();

		foreach ( var (model, _) in live )
		{
			var held = model.bytes;
			if ( held is null ) continue;

			var kind = KindOf( model.Tag );
			var was = kinds.GetValueOrDefault( kind );
			kinds[kind] = ( was.Bytes + held.Length, was.Count + 1 );

			ref readonly var h = ref HeaderIn( held );
			var stride = ModelFormat.VertexSizeOf( h.version );

			void Add( string name, long bytes ) { if ( bytes > 0 ) blocks[name] = blocks.GetValueOrDefault( name ) + bytes; }

			Add( "vertices", (long)h.vertices.count * stride );
			Add( "indices", (long)h.indices.count * 4 );
			var tailShed = model.shedAt >= 0 && held.Length == model.shedFrom;
			if ( !tailShed ) Add( "positions", (long)h.positions.count * 4 );
			if ( tailShed ) Add( "shed to disk", model.shedLength );
			if ( !tailShed ) Add( "clusters", (long)h.clusters.count * sizeof( ModelFormat.Cluster ) );
			if ( !tailShed ) Add( "cluster vertices", (long)h.clusterVertices.count * 4 );
			if ( !tailShed ) Add( "cluster triangles", h.clusterTriangles.count );
			Add( "collision", ( (long)h.collisionPoints.count * 4 ) + ( (long)h.collisionIndices.count * 4 ) );
			Add( "skin", (long)h.skin.count * sizeof( ModelFormat.SkinVertex ) );
			Add( "morphs", ( (long)h.morphDeltas.count * sizeof( ModelFormat.MorphDelta ) ) + ( (long)h.morphRuns.count * sizeof( ModelFormat.MorphRun ) ) );
			if ( !tailShed ) Add( "impostor", (long)h.impostorColour.count + h.impostorShape.count + h.impostorPicture.count );
		}

		return ( kinds.Select( x => ( x.Key, x.Value.Bytes, x.Value.Count ) ).OrderByDescending( x => x.Bytes ).ToList(),
			blocks.Select( x => ( x.Key, x.Value ) ).OrderByDescending( x => x.Value ).ToList() );
	}

	//"models/foo/bar.mdl" -> "models/foo", "terrain chunk 12_4" -> "terrain chunk #_#"
	private static string KindOf( string tag )
	{
		if ( string.IsNullOrEmpty( tag ) ) return "(no name)";

		var slash = tag.LastIndexOf( '/' );
		if ( slash > 0 && tag.IndexOf( '.', slash ) > 0 ) return tag[..slash];

		var kind = new StringBuilder( tag.Length );
		foreach ( var c in tag ) if ( !char.IsAsciiDigit( c ) || kind.Length == 0 || kind[^1] != '#' ) kind.Append( char.IsAsciiDigit( c ) ? '#' : c );

		return kind.Length > 48 ? kind.ToString( 0, 48 ) : kind.ToString();
	}

	//-- the card-only tail ---------------------------------------------------

	//Clusters, positions and the impostor are written last and read only to upload; once up they wait in a spill file
	private int shedFrom;
	private int shedLength;
	private long shedAt = -1;

	//Below this a tail is not worth a write and a copy
	private const int MostKeptTail = 64 * 1024;

	/// <summary>Card-only bytes moved out to the spill file, for PerfReport.</summary>
	internal static long ShedBytes;

	//The header of a given array: tail readers take the array once, so a shed between two reads cannot split them
	private static ref readonly ModelFormat.Header HeaderIn( byte[] held )
		=> ref Unsafe.As<byte, ModelFormat.Header>( ref MemoryMarshal.GetArrayDataReference( held ) );

	//Where the card-only tail starts, or -1 where something the processor reads lies past it (a file written in another order)
	private static int TailStart( byte[] held )
	{
		ref readonly var h = ref HeaderIn( held );

		var from = long.MaxValue;

		void Tail( in ModelFormat.Block block ) { if ( block.count > 0 ) from = Math.Min( from, block.offset ); }

		Tail( h.clusters );
		Tail( h.clusterVertices );
		Tail( h.clusterTriangles );
		Tail( h.positions );
		Tail( h.impostor );
		Tail( h.impostorColour );
		Tail( h.impostorShape );
		Tail( h.impostorPicture );

		if ( from == long.MaxValue || from < ModelFormat.HeaderSize ) return -1;

		var stride = ModelFormat.VertexSizeOf( h.version );
		var past = false;

		void Kept( in ModelFormat.Block block, int size ) { if ( block.count > 0 && block.offset + ( (long)block.count * size ) > from ) past = true; }

		Kept( h.text, 1 );
		Kept( h.vertices, stride );
		Kept( h.indices, sizeof( uint ) );
		Kept( h.meshes, sizeof( ModelFormat.Mesh ) );
		Kept( h.bodyParts, sizeof( ModelFormat.BodyPart ) );
		Kept( h.shapes, sizeof( ModelFormat.Shape ) );
		Kept( h.collisionPoints, sizeof( float ) );
		Kept( h.collisionIndices, sizeof( uint ) );
		Kept( h.skin, sizeof( ModelFormat.SkinVertex ) );
		Kept( h.bones, sizeof( ModelFormat.Bone ) );
		Kept( h.renderParts, sizeof( ModelFormat.RenderPart ) );
		Kept( h.partChoices, sizeof( ModelFormat.PartChoice ) );
		Kept( h.materialGroups, sizeof( ModelFormat.MaterialGroup ) );
		Kept( h.groupMaterials, sizeof( uint ) );
		Kept( h.surfaces, sizeof( ModelFormat.Surface ) );
		Kept( h.morphs, sizeof( ModelFormat.Morph ) );
		Kept( h.morphDeltas, sizeof( ModelFormat.MorphDelta ) );
		Kept( h.morphRuns, sizeof( ModelFormat.MorphRun ) );

		return past ? -1 : (int)from;
	}

	/// <summary>Lets go of the card-only tail once the mesh is up; the first time it is written to the spill file. Read back by <see cref="Full"/>.</summary>
	internal void Shed()
	{
		lock ( this )
		{
			var held = bytes;
			if ( held is null || own is not null || cutLater ) return;
			if ( shedAt >= 0 && held.Length == shedFrom ) return;

			if ( shedAt < 0 )
			{
				var from = TailStart( held );
				if ( from < 0 || held.Length - from < MostKeptTail ) return;

				var at = ModelSpill.Write( held.AsSpan( from ) );
				if ( at < 0 ) return;

				shedAt = at;
				shedFrom = from;
				shedLength = held.Length - from;
			}

			//Every offset before the cut is the same in both arrays, so a reader of the old one reads the same
			var head = new byte[shedFrom];
			held.AsSpan( 0, shedFrom ).CopyTo( head );
			bytes = head;

			System.Threading.Interlocked.Add( ref HeldBytes, -shedLength );
			System.Threading.Interlocked.Add( ref ShedBytes, shedLength );
		}
	}

	//The whole file, the tail read back from the spill file where it was shed; what reads the tail asks for it through this
	private byte[] Full()
	{
		var held = bytes;
		if ( held is null || shedAt < 0 || held.Length != shedFrom ) return held;

		lock ( this )
		{
			held = bytes;
			if ( held is null || held.Length != shedFrom ) return held;

			var full = new byte[shedFrom + shedLength];
			held.CopyTo( full, 0 );

			//Lost tail: the readers below see no clusters and no impostor rather than garbage
			if ( !ModelSpill.Read( shedAt, full.AsSpan( shedFrom ) ) ) return held;

			bytes = full;

			System.Threading.Interlocked.Add( ref HeldBytes, shedLength );
			System.Threading.Interlocked.Add( ref ShedBytes, -shedLength );

			return full;
		}
	}

	//A tail block of the whole file, or empty where it could not be read back
	private ReadOnlySpan<T> TailSpan<T>( Func<ModelFormat.Header, ModelFormat.Block> pick ) where T : unmanaged
	{
		var full = Full();
		if ( full is null ) return ReadOnlySpan<T>.Empty;

		var block = pick( HeaderIn( full ) );
		if ( block.count == 0 || block.offset + ( (long)block.count * sizeof( T ) ) > full.Length ) return ReadOnlySpan<T>.Empty;

		return MemoryMarshal.Cast<byte, T>( full.AsSpan( (int)block.offset, (int)block.count * sizeof( T ) ) );
	}

	/// <summary>Whether this model still holds a file.</summary>
	public bool Valid => bytes is not null;

	private ReadOnlySpan<byte> All => bytes;

	//The head of the file, in place. A reference into the array rather than a copy of it - which is what the C++ held as well.
	private ref readonly ModelFormat.Header Header
		=> ref Unsafe.As<byte, ModelFormat.Header>( ref MemoryMarshal.GetArrayDataReference( bytes ) );

	//-- reading one in -----------------------------------------------------

	/// <summary>A compiled model out of bytes, or null where they are not one.</summary>
	public static ModelFile LoadMemory( ReadOnlySpan<byte> read )
	{
		if ( read.Length < ModelFormat.HeaderSizeBeforeClusters ) return null;

		//Room for the whole header even where the file was written with a shorter one, so the fields past its end read as nought rather than as the text.
		var held = new byte[read.Length < ModelFormat.HeaderSize ? ModelFormat.HeaderSize : read.Length];
		read.CopyTo( held );

		return Adopt( held, read.Length );
	}

	//A model out of bytes nobody else holds, read where they lie - see ModelBuilder.Build, whose fresh array would be copied for nothing
	internal static ModelFile LoadOwned( byte[] bytes )
	{
		if ( bytes is null || bytes.Length < ModelFormat.HeaderSizeBeforeClusters ) return null;

		return bytes.Length < ModelFormat.HeaderSize ? LoadMemory( bytes ) : Adopt( bytes, bytes.Length );
	}

	//Reads a model out of bytes it now owns and may rewrite; the file is the first fileLength of them
	private static ModelFile Adopt( byte[] held, int fileLength )
	{
		var model = new ModelFile( held );

		ref var header = ref MemoryMarshal.AsRef<ModelFormat.Header>( held.AsSpan() );

		//Three ways a file can be wrong, all of which would otherwise be read as a valid model made of whatever the bytes happened to be.
		if ( header.magic != ModelFormat.Magic ) return null;
		if ( header.version < ModelFormat.VersionOldest || header.version > ModelFormat.Version ) return null;
		if ( header.byteCount != (uint)fileLength ) return null;

		//A file from before the clusters: the words where they would be are the text's, and are cleared so the reads below see none.
		if ( header.version < ModelFormat.VersionClusters )
		{
			held.AsSpan( ModelFormat.HeaderSizeBeforeClusters,
				ModelFormat.HeaderSize - ModelFormat.HeaderSizeBeforeClusters ).Clear();
		}

		//And from before the impostor: the same for its three blocks.
		else if ( header.version < ModelFormat.VersionImpostor )
		{
			held.AsSpan( ModelFormat.HeaderSizeBeforeImpostor,
				ModelFormat.HeaderSize - ModelFormat.HeaderSizeBeforeImpostor ).Clear();
		}

		//And from before the impostor's picture of triangles: its one block.
		else if ( header.version < ModelFormat.VersionImpostorPicture )
		{
			held.AsSpan( ModelFormat.HeaderSizeBeforeImpostorPicture,
				ModelFormat.HeaderSize - ModelFormat.HeaderSizeBeforeImpostorPicture ).Clear();
		}

		//And from before the chains: clusters of another shape, which the reads below must not take for these - cut on load instead.
		if ( header.version < ModelFormat.VersionChains )
		{
			header.clusters = default;
			header.clusterVertices = default;
			header.clusterTriangles = default;
		}

		//A block is only usable if it lies inside the file.
		bool Within( in ModelFormat.Block block, int stride )
			=> block.count == 0 || ( (long)block.offset + ( (long)stride * block.count ) ) <= held.Length;

		if ( !Within( header.text, 1 ) ) return null;
		if ( !Within( header.vertices, ModelFormat.VertexSizeOf( header.version ) ) ) return null;
		if ( !Within( header.indices, sizeof( uint ) ) ) return null;
		if ( !Within( header.meshes, sizeof( ModelFormat.Mesh ) ) ) return null;
		if ( !Within( header.impostor, sizeof( ModelFormat.Impostor ) ) ) return null;
		if ( !Within( header.impostorColour, 1 ) ) return null;
		if ( !Within( header.impostorShape, 1 ) ) return null;
		if ( !Within( header.impostorPicture, 1 ) ) return null;
		if ( !Within( header.bodyParts, sizeof( ModelFormat.BodyPart ) ) ) return null;
		if ( !Within( header.shapes, sizeof( ModelFormat.Shape ) ) ) return null;
		if ( !Within( header.collisionPoints, sizeof( float ) ) ) return null;
		if ( !Within( header.collisionIndices, sizeof( uint ) ) ) return null;
		if ( !Within( header.skin, sizeof( ModelFormat.SkinVertex ) ) ) return null;
		if ( !Within( header.bones, sizeof( ModelFormat.Bone ) ) ) return null;
		if ( !Within( header.renderParts, sizeof( ModelFormat.RenderPart ) ) ) return null;
		if ( !Within( header.partChoices, sizeof( ModelFormat.PartChoice ) ) ) return null;
		if ( !Within( header.materialGroups, sizeof( ModelFormat.MaterialGroup ) ) ) return null;
		if ( !Within( header.groupMaterials, sizeof( uint ) ) ) return null;
		if ( !Within( header.morphs, sizeof( ModelFormat.Morph ) ) ) return null;
		if ( !Within( header.morphDeltas, sizeof( ModelFormat.MorphDelta ) ) ) return null;
		if ( !Within( header.morphRuns, sizeof( ModelFormat.MorphRun ) ) ) return null;
		if ( !Within( header.clusters, sizeof( ModelFormat.Cluster ) ) ) return null;
		if ( !Within( header.clusterVertices, sizeof( uint ) ) ) return null;
		if ( !Within( header.clusterTriangles, 1 ) ) return null;
		if ( !Within( header.positions, sizeof( float ) ) ) return null;

		//What the art said of each surface. Those eight bytes were something else once, so a file from before
		//they were this - or one whose count is not one per mesh - reads as a model whose art said nothing.
		if ( header.generation < ModelFormat.GenerationSurfaces
			|| header.surfaces.count != header.meshes.count
			|| !Within( header.surfaces, sizeof( ModelFormat.Surface ) ) )
		{
			header.surfaces = default;
		}

		if ( header.positions.count != 0 && header.positions.count != header.vertices.count * 3 ) return null;

		//A skin that does not cover every vertex is a file we refuse rather than read: the draw path would index off the end of it.
		if ( header.skin.count != 0 && header.skin.count != header.vertices.count ) return null;

		//The same for the morph runs, and for the same reason.
		if ( header.morphRuns.count != 0 && header.morphRuns.count != header.vertices.count ) return null;

		var carried = header.clusters.count > 0 && header.positions.count == header.vertices.count * 3;

		//Flagged NoClusters: something else draws it, so the cut waits for a first draw that may never come
		if ( !carried && header.vertices.count > 0 )
		{
			if ( ( header.flags & (uint)ModelFormat.ModelFlags.NoClusters ) != 0 ) model.cutLater = true;
			else model.CutOwn();
		}

		return model;
	}

	//Level nought cut here, for a file written without clusters. The first level and no more - the ones
	//above it make vertices of their own, and a file being read has nowhere to keep them.
	private void CutOwn()
	{
		var built = new LevelChain.Build();
		var all = Vertices;

		LevelChain.CutClusters( all, Indices, Meshes, built, most: 1 );

		var positions = new float[all.Length * 3];

		for ( var v = 0; v < all.Length; v++ )
		{
			positions[( v * 3 ) + 0] = all[v].Position.x;
			positions[( v * 3 ) + 1] = all[v].Position.y;
			positions[( v * 3 ) + 2] = all[v].Position.z;
		}

		ownPositions = positions;
		own = built;

		//The unpacked copy was for the cut, not to be held beside the file.
		unpacked = null;
	}

	private volatile bool cutLater;

	//The clusters cut on load or on first asking; null where the file carries its own
	private LevelChain.Build Own
	{
		get
		{
			if ( !cutLater ) return own;

			lock ( this )
			{
				if ( cutLater && bytes is not null ) CutOwn();
				cutLater = false;
			}

			return own;
		}
	}

	/// <summary>The same for a pointer and a length, which is what the ABI took.</summary>
	public static ModelFile LoadMemory( void* read, int size )
		=> read is null || size < 0 ? null : LoadMemory( new ReadOnlySpan<byte>( read, size ) );

	/// <summary>A compiled model from a path, or null.</summary>
	public static ModelFile Load( string path )
	{
		if ( path is null ) return null;

		byte[] read;

		try
		{
			read = SealedFile.ReadAllBytes( path );
		}
		catch ( IOException )
		{
			return null;
		}
		catch ( UnauthorizedAccessException )
		{
			return null;
		}

		if ( read.Length < ModelFormat.HeaderSize ) return null;

		//Read for this alone, so it is held as it is rather than copied a second time
		return Adopt( read, read.Length );
	}

	/// <summary>Which build of the importers this engine writes - see <see cref="ModelFormat.ImportGeneration"/>.</summary>
	public static uint ImportGeneration => ModelFormat.ImportGeneration;

	//Only as far as the field: the point of asking is to avoid reading a file that is about to be thrown away and built again.
	/// <summary>Which build of the importers filled a compiled model, without reading the rest of it.</summary>
	public static uint ImportGenerationOf( string path )
	{
		if ( path is null ) return 0;

		Span<byte> head = stackalloc byte[ModelFormat.HeaderSize];

		try
		{
			using var stream = SealedFile.OpenRead( path );

			if ( stream.ReadAtLeast( head, head.Length, throwOnEndOfStream: false ) != head.Length ) return 0;
		}
		catch ( IOException )
		{
			return 0;
		}
		catch ( UnauthorizedAccessException )
		{
			return 0;
		}

		ref readonly var header = ref MemoryMarshal.AsRef<ModelFormat.Header>( head );

		if ( header.magic != ModelFormat.Magic ) return 0;

		return header.generation;
	}

	public void Dispose()
	{
		//From the finalizer's thread too - see Model.Destroy
		lock ( this )
		{
			if ( bytes is null ) return;

			System.Threading.Interlocked.Add( ref HeldBytes, -bytes.Length );
			System.Threading.Interlocked.Decrement( ref HeldFiles );

			if ( shedAt >= 0 )
			{
				if ( bytes.Length == shedFrom ) System.Threading.Interlocked.Add( ref ShedBytes, -shedLength );
				ModelSpill.Give( shedAt, shedLength );
				shedAt = -1;
			}

			bytes = null;
		}
	}

	//-- what the file holds ------------------------------------------------

	//A string of the text block, by its offset. Nought is empty.
	private string Text( uint offset )
	{
		var text = ModelFormat.Span<byte>( All, in Header.text );

		if ( offset >= (uint)text.Length ) return "";

		var run = text[(int)offset..];
		var end = run.IndexOf( (byte)0 );

		return Encoding.UTF8.GetString( end < 0 ? run : run[..end] );
	}

	//Whether the vertices are the seventy-six-byte Vertex of a file from before they were packed.
	private bool Unpacked => Header.version < ModelFormat.VersionPackedVertices;

	/// <summary>The vertices as the file holds them, where it holds them packed.</summary>
	public ReadOnlySpan<PackedVertex> PackedVertices
		=> Unpacked ? default : ModelFormat.Span<PackedVertex>( All, in Header.vertices );

	/// <summary>The vertices unpacked - the file itself for one from before they were packed, and otherwise a copy made on first asking and kept. For what reads a model as a whole on the processor and wants it as <see cref="Vertex"/>; the renderer takes <see cref="CopyPackedVertices"/>, which does not copy.</summary>
	public ReadOnlySpan<Vertex> Vertices
	{
		get
		{
			if ( Unpacked ) return ModelFormat.Span<Vertex>( All, in Header.vertices );

			if ( unpacked is null )
			{
				var made = new Vertex[Header.vertices.count];
				PackedVertex.Unpack( PackedVertices, made );
				unpacked = made;
			}

			return unpacked;
		}
	}

	private Vertex[] unpacked;
	public ReadOnlySpan<uint> Indices => ModelFormat.Span<uint>( All, in Header.indices );
	public ReadOnlySpan<ModelFormat.Mesh> Meshes => ModelFormat.Span<ModelFormat.Mesh>( All, in Header.meshes );

	/// <summary>The model's impostor, where it has one - see ModelFormat.Impostor. Whether its skin follows its bones as dual quaternions - see ModelFormat.ModelFlags.</summary>
	public bool DualQuaternion => ( Header.flags & (uint)ModelFormat.ModelFlags.DualQuaternion ) != 0;

	/// <summary>Whether its levels are a chain - one level of the whole model at a time. See ModelFormat.ModelFlags.Chain.</summary>
	public bool IsChain => own is null && ( Header.flags & (uint)ModelFormat.ModelFlags.Chain ) != 0;

	/// <summary>How many levels coarser than the camera's its shadows are, for a chain.</summary>
	public int ShadowLevelOffset => IsChain ? (int)( ( Header.flags >> ModelFormat.ShadowOffsetShift ) & 3u ) : 0;

	public bool HasImpostor => Header.impostor.count == 1 && Header.impostorColour.count > 0 && Header.impostorShape.count > 0;

	/// <summary>What the impostor is: its grid, its sphere, its plane. Default where there is none.</summary>
	public ModelFormat.Impostor Impostor
		=> HasImpostor && TailSpan<ModelFormat.Impostor>( static h => h.impostor ) is { Length: 1 } one ? one[0] : default;

	/// <summary>The impostor's colour and cover, as a compiled texture file.</summary>
	public ReadOnlySpan<byte> ImpostorColour => TailSpan<byte>( static h => h.impostorColour );

	/// <summary>Its normal and depth, as a compiled texture file.</summary>
	public ReadOnlySpan<byte> ImpostorShape => TailSpan<byte>( static h => h.impostorShape );

	/// <summary>Whether its planes are shaded by the model's own surfaces, through the picture of which triangle each texel shows.</summary>
	public bool HasImpostorPicture => HasImpostor && Header.impostorPicture.count > 0;

	/// <summary>That picture, as a compiled texture file - see ModelBuilder.ImpostorPicture.</summary>
	public ReadOnlySpan<byte> ImpostorPicture => TailSpan<byte>( static h => h.impostorPicture );

	/// <summary>How many meshes the impostor's planes are, one a material, in a row from Impostor.mesh.</summary>
	public int ImpostorMeshes
	{
		get
		{
			if ( !HasImpostor ) return 0;

			var first = (int)Impostor.mesh;
			var count = 0;

			while ( first + count < MeshCount && StandsIn( first + count ) ) count++;

			return Math.Max( 1, count );
		}
	}

	/// <summary>Whether a mesh only ever stands in for the rest - see ModelFormat.MeshFlags.StandsIn.</summary>
	public bool StandsIn( int mesh )
		=> (uint)mesh < (uint)Meshes.Length && ( (ModelFormat.MeshFlags)Meshes[mesh].reserved & ModelFormat.MeshFlags.StandsIn ) != 0;
	private ReadOnlySpan<ModelFormat.BodyPart> Bodies => ModelFormat.Span<ModelFormat.BodyPart>( All, in Header.bodyParts );
	private ReadOnlySpan<ModelFormat.Shape> Shapes => ModelFormat.Span<ModelFormat.Shape>( All, in Header.shapes );
	private ReadOnlySpan<float> CollisionPoints => ModelFormat.Span<float>( All, in Header.collisionPoints );
	private ReadOnlySpan<uint> CollisionIndices => ModelFormat.Span<uint>( All, in Header.collisionIndices );
	private ReadOnlySpan<ModelFormat.Bone> Bones => ModelFormat.Span<ModelFormat.Bone>( All, in Header.bones );
	private ReadOnlySpan<ModelFormat.Morph> Morphs => ModelFormat.Span<ModelFormat.Morph>( All, in Header.morphs );
	private ReadOnlySpan<ModelFormat.RenderPart> Parts => ModelFormat.Span<ModelFormat.RenderPart>( All, in Header.renderParts );
	private ReadOnlySpan<ModelFormat.PartChoice> Choices => ModelFormat.Span<ModelFormat.PartChoice>( All, in Header.partChoices );
	private ReadOnlySpan<ModelFormat.MaterialGroup> MaterialGroups => ModelFormat.Span<ModelFormat.MaterialGroup>( All, in Header.materialGroups );
	private ReadOnlySpan<uint> GroupMaterials => ModelFormat.Span<uint>( All, in Header.groupMaterials );

	/// <summary>The skin as the bytes the renderer uploads, eight a vertex.</summary>
	public ReadOnlySpan<byte> Skin
		=> All.Slice( (int)Header.skin.offset, (int)Header.skin.count * sizeof( ModelFormat.SkinVertex ) );

	/// <summary>The deltas and the runs, as the bytes that go into a buffer.</summary>
	public ReadOnlySpan<byte> MorphDeltas
		=> All.Slice( (int)Header.morphDeltas.offset, (int)Header.morphDeltas.count * sizeof( ModelFormat.MorphDelta ) );

	public ReadOnlySpan<byte> MorphRuns
		=> All.Slice( (int)Header.morphRuns.offset, (int)Header.morphRuns.count * sizeof( ModelFormat.MorphRun ) );

	public string Name => Text( Header.name );

	/// <summary>The level-of-detail group the definition named, or empty - see <see cref="ModelFormat.Header.lodGroup"/>.</summary>
	public string LodGroup => Text( Header.lodGroup );

	public void GetRenderBounds( float* outMinMax )
	{
		if ( outMinMax is null ) return;

		//The header by value: a fixed-size buffer cannot be read through a reference into the heap, and the copy is one memcpy of the head.
		var header = Header;

		for ( var i = 0; i < 6; i++ ) outMinMax[i] = header.renderBounds[i];
	}

	public void GetPhysicsBounds( float* outMinMax )
	{
		if ( outMinMax is null ) return;

		var header = Header;

		for ( var i = 0; i < 6; i++ ) outMinMax[i] = header.physicsBounds[i];
	}

	public int VertexCount => (int)Header.vertices.count;
	public int IndexCount => (int)Header.indices.count;
	public int MeshCount => (int)Header.meshes.count;
	public int BoneCount => (int)Header.bones.count;
	public int SkinCount => (int)Header.skin.count;
	public int MorphCount => (int)Header.morphs.count;
	public int MorphDeltaCount => (int)Header.morphDeltas.count;
	public int BodyCount => (int)Header.bodyParts.count;
	public int PartCount => (int)Header.renderParts.count;
	public int MaterialGroupCount => (int)Header.materialGroups.count;
	public ulong DefaultMask => Header.defaultMask;

	/// <summary>The vertices unpacked into <see cref="Vertex"/> - exactly as written for a file from before they were packed.</summary>
	public int CopyVertices( Vertex* outVertices, int capacity )
	{
		if ( outVertices is null || capacity <= 0 ) return 0;

		var take = VertexCount < capacity ? VertexCount : capacity;

		if ( Unpacked ) ModelFormat.Span<Vertex>( All, in Header.vertices )[..take].CopyTo( new Span<Vertex>( outVertices, take ) );
		else if ( unpacked is not null ) unpacked.AsSpan( 0, take ).CopyTo( new Span<Vertex>( outVertices, take ) );
		else PackedVertex.Unpack( PackedVertices[..take], new Span<Vertex>( outVertices, take ) );

		return take;
	}

	/// <summary>The vertices as the card reads them - a copy of the file's own, or for a file from before they were packed, packed on the way.</summary>
	public int CopyPackedVertices( PackedVertex* outVertices, int capacity )
	{
		if ( outVertices is null || capacity <= 0 ) return 0;

		var take = VertexCount < capacity ? VertexCount : capacity;

		if ( Unpacked ) PackedVertex.Pack( ModelFormat.Span<Vertex>( All, in Header.vertices )[..take], new Span<PackedVertex>( outVertices, take ) );
		else PackedVertices[..take].CopyTo( new Span<PackedVertex>( outVertices, take ) );

		return take;
	}

	public int CopyIndices( uint* outIndices, int capacity )
	{
		if ( outIndices is null || capacity <= 0 ) return 0;

		var all = Indices;
		var take = all.Length < capacity ? all.Length : capacity;

		all[..take].CopyTo( new Span<uint>( outIndices, take ) );
		return take;
	}

	public int CopySkin( byte* outSkin, int capacity )
	{
		if ( outSkin is null || capacity <= 0 ) return 0;

		var available = SkinCount;
		var take = available < capacity ? available : capacity;

		if ( take > 0 )
			Skin[..( take * sizeof( ModelFormat.SkinVertex ) )]
				.CopyTo( new Span<byte>( outSkin, take * sizeof( ModelFormat.SkinVertex ) ) );

		return take;
	}

	public int CopyMorphDeltas( byte* outDeltas, int capacity )
	{
		if ( outDeltas is null || capacity <= 0 ) return 0;

		var available = MorphDeltaCount;
		var take = available < capacity ? available : capacity;

		if ( take > 0 )
			MorphDeltas[..( take * sizeof( ModelFormat.MorphDelta ) )]
				.CopyTo( new Span<byte>( outDeltas, take * sizeof( ModelFormat.MorphDelta ) ) );

		return take;
	}

	public int CopyMorphRuns( byte* outRuns, int capacity )
	{
		if ( outRuns is null || capacity <= 0 ) return 0;

		var available = (int)Header.morphRuns.count;
		var take = available < capacity ? available : capacity;

		if ( take > 0 )
			MorphRuns[..( take * sizeof( ModelFormat.MorphRun ) )]
				.CopyTo( new Span<byte>( outRuns, take * sizeof( ModelFormat.MorphRun ) ) );

		return take;
	}

	public string MorphName( int morph )
		=> morph >= 0 && morph < MorphCount ? Text( Morphs[morph].name ) : "";

	//-- meshes, body groups, material groups -------------------------------

	public string MeshName( int mesh ) => mesh >= 0 && mesh < MeshCount ? Text( Meshes[mesh].name ) : "";

	public string MeshMaterial( int mesh ) => mesh >= 0 && mesh < MeshCount ? Text( Meshes[mesh].material ) : "";

	public ulong MeshGroup( int mesh ) => mesh >= 0 && mesh < MeshCount ? Meshes[mesh].group : 0;

	/// <summary>What the art said a mesh's surface is, or nothing - see <see cref="ModelFormat.Surface"/>.</summary>
	public ModelFormat.Surface MeshSurface( int mesh )
	{
		var surfaces = ModelFormat.Span<ModelFormat.Surface>( All, in Header.surfaces );

		return mesh >= 0 && mesh < surfaces.Length ? surfaces[mesh] : default;
	}

	public void MeshRange( int mesh, int* outRange )
	{
		if ( outRange is null ) return;
		for ( var i = 0; i < 4; i++ ) outRange[i] = 0;

		if ( mesh < 0 || mesh >= MeshCount ) return;

		ref readonly var entry = ref Meshes[mesh];

		outRange[0] = (int)entry.firstIndex;
		outRange[1] = (int)entry.indexCount;
		outRange[2] = (int)entry.firstVertex;
		outRange[3] = (int)entry.vertexCount;
	}

	public string PartName( int part ) => part >= 0 && part < PartCount ? Text( Parts[part].name ) : "";

	public ulong PartMask( int part ) => part >= 0 && part < PartCount ? Parts[part].mask : 0;

	public int PartChoiceCount( int part ) => part >= 0 && part < PartCount ? (int)Parts[part].choices.count : 0;

	//Where one choice sits in the shared array, or -1 if there is no such choice.
	private int ChoiceAt( int part, int choice )
	{
		if ( part < 0 || part >= PartCount ) return -1;

		ref readonly var entry = ref Parts[part];
		if ( choice < 0 || choice >= (int)entry.choices.count ) return -1;

		return (int)entry.choices.offset + choice;
	}

	public string PartChoiceName( int part, int choice )
	{
		var at = ChoiceAt( part, choice );
		return at < 0 ? "" : Text( Choices[at].name );
	}

	public ulong PartChoiceMask( int part, int choice )
	{
		var at = ChoiceAt( part, choice );
		return at < 0 ? 0 : Choices[at].mask;
	}

	public string MaterialGroupName( int group )
		=> group >= 0 && group < MaterialGroupCount ? Text( MaterialGroups[group].name ) : "";

	/// <summary>What a group puts on a mesh - the mesh's own material where the group says nothing, so a caller never has to check whether it did.</summary>
	public string GroupMaterial( int group, int mesh )
	{
		if ( mesh < 0 || mesh >= MeshCount ) return "";

		//Past the end of the group, or no group at all: the material the mesh was built with, which is what a group that says nothing about it means.
		if ( group < 0 || group >= MaterialGroupCount ) return Text( Meshes[mesh].material );

		ref readonly var entry = ref MaterialGroups[group];

		if ( mesh >= (int)entry.materials.count ) return Text( Meshes[mesh].material );

		var named = GroupMaterials[(int)entry.materials.offset + mesh];

		return named != 0 ? Text( named ) : Text( Meshes[mesh].material );
	}

	//-- the skeleton -------------------------------------------------------

	public string BoneName( int bone ) => bone >= 0 && bone < BoneCount ? Text( Bones[bone].name ) : "";

	public int BoneParent( int bone ) => bone >= 0 && bone < BoneCount ? Bones[bone].parent : -1;

	/// <summary>Row vectors, translation in the last row - see ModelFormat.Bone. Files written before that was settled had it the other way round, and they are already refused: the version they carry is not this one.</summary>
	public void BoneInverseBind( int bone, float* outMatrix )
	{
		if ( outMatrix is null ) return;
		if ( bone < 0 || bone >= BoneCount ) return;

		var one = Bones[bone];

		for ( var i = 0; i < 16; i++ ) outMatrix[i] = one.inverseBind[i];
	}

	//-- collision ----------------------------------------------------------

	public string BodyName( int body ) => body >= 0 && body < BodyCount ? Text( Bodies[body].name ) : "";

	public string BodyBone( int body ) => body >= 0 && body < BodyCount ? Text( Bodies[body].boneName ) : "";

	public void BodyTransform( int body, float* outTransform )
	{
		if ( outTransform is null ) return;

		for ( var i = 0; i < 6; i++ ) outTransform[i] = 0.0f;
		outTransform[6] = 1.0f;

		if ( body < 0 || body >= BodyCount ) return;

		var part = Bodies[body];

		for ( var i = 0; i < 7; i++ ) outTransform[i] = part.transform[i];
	}

	public void BodyProperties( int body, float* outProperties )
	{
		if ( outProperties is null ) return;

		for ( var i = 0; i < 4; i++ ) outProperties[i] = 0.0f;

		if ( body < 0 || body >= BodyCount ) return;

		ref readonly var part = ref Bodies[body];

		outProperties[0] = part.mass;
		outProperties[1] = part.linearDamping;
		outProperties[2] = part.angularDamping;
		outProperties[3] = part.gravityScale;
	}

	public int BodyMassCenter( int body, float* outCenter )
	{
		if ( outCenter is null ) return 0;

		for ( var i = 0; i < 3; i++ ) outCenter[i] = 0.0f;

		if ( body < 0 || body >= BodyCount ) return 0;

		var part = Bodies[body];

		if ( part.overrideMassCenter == 0 ) return 0;

		for ( var i = 0; i < 3; i++ ) outCenter[i] = part.massCenter[i];

		return 1;
	}

	public int ShapeCount( int body ) => body >= 0 && body < BodyCount ? (int)Bodies[body].shapes.count : 0;

	//Where the shape a pair of indices names sits, or -1.
	private int ShapeAt( int body, int shape )
	{
		if ( body < 0 || body >= BodyCount ) return -1;

		ref readonly var part = ref Bodies[body];
		if ( shape < 0 || shape >= (int)part.shapes.count ) return -1;

		return (int)part.shapes.offset + shape;
	}

	public int ShapeKind( int body, int shape )
	{
		var at = ShapeAt( body, shape );
		return at < 0 ? -1 : (int)Shapes[at].kind;
	}

	public string ShapeSurface( int body, int shape )
	{
		var at = ShapeAt( body, shape );
		return at < 0 ? "" : Text( Shapes[at].surface );
	}

	public void ShapePrimitive( int body, int shape, float* outPrimitive )
	{
		if ( outPrimitive is null ) return;

		for ( var i = 0; i < 7; i++ ) outPrimitive[i] = 0.0f;

		var at = ShapeAt( body, shape );
		if ( at < 0 ) return;

		var one = Shapes[at];

		for ( var i = 0; i < 7; i++ ) outPrimitive[i] = one.primitive[i];
	}

	/// <summary>Stored as loose floats, reported as points, because that is how the caller thinks about them and how it will size its buffer.</summary>
	public int ShapePointCount( int body, int shape )
	{
		var at = ShapeAt( body, shape );
		return at < 0 ? 0 : (int)Shapes[at].points.count / 3;
	}

	public int ShapeIndexCount( int body, int shape )
	{
		var at = ShapeAt( body, shape );
		return at < 0 ? 0 : (int)Shapes[at].indices.count;
	}

	public int ShapeCopyPoints( int body, int shape, float* outPoints, int capacity )
	{
		var at = ShapeAt( body, shape );
		if ( at < 0 || outPoints is null || capacity <= 0 ) return 0;

		var count = (int)Shapes[at].points.count;
		var take = count < capacity ? count : capacity;

		CollisionPoints.Slice( (int)Shapes[at].points.offset, take )
			.CopyTo( new Span<float>( outPoints, take ) );

		return take;
	}

	public int ShapeCopyIndices( int body, int shape, uint* outIndices, int capacity )
	{
		var at = ShapeAt( body, shape );
		if ( at < 0 || outIndices is null || capacity <= 0 ) return 0;

		var count = (int)Shapes[at].indices.count;
		var take = count < capacity ? count : capacity;

		CollisionIndices.Slice( (int)Shapes[at].indices.offset, take )
			.CopyTo( new Span<uint>( outIndices, take ) );

		return take;
	}

	//-- the cluster DAG ---------------------------------------------------- Out of
	//the file where it carries them, or cut on load for a file written before it did.

	/// <summary>How many bytes one cluster is, which is what the renderer strides by.</summary>
	public static int ClusterStride => sizeof( ModelFormat.Cluster );

	public ReadOnlySpan<ModelFormat.Cluster> Clusters
		=> Own is { } cut
			? CollectionsMarshal.AsSpan( cut.Clusters )
			: TailSpan<ModelFormat.Cluster>( static h => h.clusters );

	public ReadOnlySpan<uint> ClusterVertices
		=> Own is { } cut
			? CollectionsMarshal.AsSpan( cut.Vertices )
			: TailSpan<uint>( static h => h.clusterVertices );

	public ReadOnlySpan<byte> ClusterTriangles
		=> Own is { } cut
			? CollectionsMarshal.AsSpan( cut.Triangles )
			: TailSpan<byte>( static h => h.clusterTriangles );

	public ReadOnlySpan<float> Positions
		=> Own is not null ? ownPositions : TailSpan<float>( static h => h.positions );

	public int ClusterCount => Clusters.Length;
	public int ClusterVertexCount => ClusterVertices.Length;
	public int ClusterTriangleCount => ClusterTriangles.Length / 3;
	public int ClusterLevels => Own is { } cut ? (int)cut.Levels : (int)Header.clusterLevels;

	public int CopyClusters( void* outClusters, int capacity )
	{
		if ( outClusters is null || capacity <= 0 ) return 0;

		var all = Clusters;
		var take = all.Length < capacity ? all.Length : capacity;

		all[..take].CopyTo( new Span<ModelFormat.Cluster>( outClusters, take ) );
		return take;
	}

	public int CopyClusterVertices( uint* outVertices, int capacity )
	{
		if ( outVertices is null || capacity <= 0 ) return 0;

		var all = ClusterVertices;
		var take = all.Length < capacity ? all.Length : capacity;

		all[..take].CopyTo( new Span<uint>( outVertices, take ) );
		return take;
	}

	public int CopyClusterTriangles( byte* outTriangles, int capacity )
	{
		if ( outTriangles is null || capacity <= 0 ) return 0;

		var count = ClusterTriangleCount;
		var take = count < capacity ? count : capacity;

		ClusterTriangles[..( take * 3 )].CopyTo( new Span<byte>( outTriangles, take * 3 ) );
		return take;
	}

	public int CopyPositions( float* outPositions, int capacity )
	{
		if ( outPositions is null || capacity <= 0 ) return 0;

		var all = Positions;
		if ( all.Length == 0 ) return 0;

		var count = VertexCount;
		var take = count < capacity ? count : capacity;

		all[..( take * 3 )].CopyTo( new Span<float>( outPositions, take * 3 ) );
		return take;
	}
}

//Where shed model tails wait: one file a process, unlinked as soon as it is open, so a crash leaves nothing behind
internal static class ModelSpill
{
	private static readonly object gate = new();
	private static Microsoft.Win32.SafeHandles.SafeFileHandle file;
	private static long end;
	private static bool failed;

	//Ranges models gave back, by offset, neighbours merged; a streamed terrain would otherwise grow the file all session
	private static readonly List<(long At, long Length)> holes = new();

	//The offset it went to, or -1 where there is no spill file
	public static long Write( ReadOnlySpan<byte> data )
	{
		lock ( gate )
		{
			if ( !Open() ) return -1;

			var at = end;
			var hole = -1;

			for ( var i = 0; i < holes.Count; i++ )
			{
				if ( holes[i].Length < data.Length ) continue;
				if ( hole < 0 || holes[i].Length < holes[hole].Length ) hole = i;
			}

			if ( hole >= 0 ) at = holes[hole].At;

			try
			{
				RandomAccess.Write( file, data, at );
			}
			catch ( IOException )
			{
				return -1;
			}

			if ( hole < 0 ) end += data.Length;
			else if ( holes[hole].Length == data.Length ) holes.RemoveAt( hole );
			else holes[hole] = ( at + data.Length, holes[hole].Length - data.Length );

			return at;
		}
	}

	public static void Give( long at, long length )
	{
		if ( at < 0 || length <= 0 ) return;

		lock ( gate )
		{
			var i = 0;
			while ( i < holes.Count && holes[i].At < at ) i++;

			holes.Insert( i, ( at, length ) );

			if ( i + 1 < holes.Count && holes[i].At + holes[i].Length == holes[i + 1].At )
			{
				holes[i] = ( holes[i].At, holes[i].Length + holes[i + 1].Length );
				holes.RemoveAt( i + 1 );
			}

			if ( i > 0 && holes[i - 1].At + holes[i - 1].Length == holes[i].At )
			{
				holes[i - 1] = ( holes[i - 1].At, holes[i - 1].Length + holes[i].Length );
				holes.RemoveAt( i );
			}
		}
	}

	public static bool Read( long at, Span<byte> into )
	{
		lock ( gate )
		{
			if ( file is null ) return false;

			try
			{
				var done = 0;

				while ( done < into.Length )
				{
					var read = RandomAccess.Read( file, into[done..], at + done );
					if ( read <= 0 ) return false;
					done += read;
				}

				return true;
			}
			catch ( IOException )
			{
				return false;
			}
		}
	}

	//On the home disk beside the terrain cache, never the temp folder: on this kind of machine that is memory
	private static bool Open()
	{
		if ( file is not null ) return true;
		if ( failed ) return false;

		//A browser's files are its memory: nothing to gain
		if ( OperatingSystem.IsBrowser() ) { failed = true; return false; }

		try
		{
			var root = Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData );
			if ( string.IsNullOrEmpty( root ) ) { failed = true; return false; }

			var folder = Path.Combine( root, "Rinten", "spill" );
			Directory.CreateDirectory( folder );

			var path = Path.Combine( folder, $"{Environment.ProcessId}.bin" );
			//Windows keeps an open file's name, so there it goes when the handle does
			var windows = OperatingSystem.IsWindows();
			file = File.OpenHandle( path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete,
				windows ? FileOptions.DeleteOnClose : FileOptions.None );

			if ( !windows ) File.Delete( path );

			return true;
		}
		catch ( Exception )
		{
			failed = true;
			return false;
		}
	}
}
