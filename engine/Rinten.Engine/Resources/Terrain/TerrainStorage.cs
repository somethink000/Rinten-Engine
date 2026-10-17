using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rinten;

//Read off the layer's material - a .mat whose shader declares what a layer knows about itself, shaders/terrain_layer.shader being the engine's own - and written into the numbers picture the ground is drawn by. See Terrain.Params.
/// <summary>What a painted layer switched on for itself.</summary>
[Flags]
public enum TerrainFlags : uint
{
	None = 0,
	NoTile = 1 << 0
}

/// <summary>Compact terrain material encoding with base/overlay texture blending. Packed format (32-bit uint)</summary>
public record struct CompactTerrainMaterial
{
	//Helper struct for cleaner bit field manipulation
	private readonly record struct BitField( int shift, int bits )
	{
		private readonly uint _mask = (1u << bits) - 1;

		/// <summary>Extract value from packed data</summary>
		public uint Extract( uint packed ) => (packed >> shift) & _mask;

		/// <summary>Insert value into packed data</summary>
		public uint Insert( uint packed, uint value )
		{
			packed &= ~(_mask << shift);
			packed |= (value & _mask) << shift;
			return packed;
		}
	}

	private uint _packed;

	//Bit offset definitions
	private static readonly BitField BaseTexture = new( shift: 0, bits: 5 );
	private static readonly BitField OverlayTexture = new( shift: 5, bits: 5 );
	private static readonly BitField Blend = new( shift: 10, bits: 8 );
	private static readonly BitField Hole = new( shift: 18, bits: 1 );
	private static readonly BitField Reserved = new( shift: 19, bits: 13 );

	public CompactTerrainMaterial( uint packed )
	{
		_packed = packed;
	}

	public CompactTerrainMaterial(
		byte baseTextureId,
		byte overlayTextureId = 0,
		byte blendFactor = 0,
		bool isHole = false )
	{
		_packed = 0;
		BaseTextureId = baseTextureId;
		OverlayTextureId = overlayTextureId;
		BlendFactor = blendFactor;
		IsHole = isHole;
		ReservedValue = 0;
	}

	/// <summary>Base texture ID (0-31)</summary>
	public byte BaseTextureId
	{
		get => (byte)BaseTexture.Extract( _packed );
		set => _packed = BaseTexture.Insert( _packed, value );
	}

	/// <summary>Overlay texture ID (0-31)</summary>
	public byte OverlayTextureId
	{
		get => (byte)OverlayTexture.Extract( _packed );
		set => _packed = OverlayTexture.Insert( _packed, value );
	}

	/// <summary>Blend factor between base and overlay (0-255).</summary>
	public byte BlendFactor
	{
		get => (byte)Blend.Extract( _packed );
		set => _packed = Blend.Insert( _packed, value );
	}

	/// <summary>Whether this pixel is marked as a hole</summary>
	public bool IsHole
	{
		get => Hole.Extract( _packed ) != 0;
		set => _packed = Hole.Insert( _packed, value ? 1u : 0u );
	}

	//Reserved bits for future use (13 bits)
	internal ushort ReservedValue
	{
		get => (ushort)Reserved.Extract( _packed );
		set => _packed = Reserved.Insert( _packed, value );
	}

	/// <summary>Raw packed value</summary>
	public uint Packed => _packed;
}

//The mesh is the terrain; the old maps are only used to build a mesh and aren't written after
/// <summary>A terrain as it is kept: the mesh it is, what it is painted with, and the materials of whatever was set into it.</summary>
[Expose]
[AssetType( Name = "Terrain", Extension = "terrain", Category = "World", Icon = "terrain" )]
public partial class TerrainStorage : GameResource
{
	//The mesh, and in a file from before there was one the height and control maps it is made from once and never written again - see MakeMesh
	[JsonInclude, JsonPropertyName( "Maps" )] private TerrainMapBlob Maps { get; set; } = new();

	//How many cells a side such a file's maps are; nought once the mesh is made
	[JsonInclude, JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingDefault )] private int Resolution { get; set; }

	/// <summary>The ground itself. Made the first time it is asked for: from an old file's maps, or flat.</summary>
	[JsonIgnore, Hide]
	public TerrainMesh Mesh
	{
		get
		{
			if ( Maps.Mesh is null ) MakeMesh();
			return Maps.Mesh;
		}
		set => Maps.Mesh = value;
	}

	/// <summary>What the geometry set into the ground wears, by the number its triangles carry less one - nought is the terrain's own paint. See TerrainMesh.MaterialOf.</summary>
	public List<Material> InsetMaterials { get; set; } = new();

	/// <summary>How long an edge of the ground is kept under a brush, in metres: longer ones are split and shorter ones folded as the ground is pulled about, so a cave's walls are as finely made as the hill it was dug into.</summary>
	public float EdgeLength { get; set; } = 1.0f;

	/// <summary>The ground made again from a height map - <paramref name="side"/> by <paramref name="side"/> heights over the terrain's size, the full range its height - painted with its first layer. Whatever was sculpted is gone.</summary>
	public void MakeFromHeights( ushort[] heights, int side )
	{
		Maps.Mesh = TerrainMesh.FromHeights( heights, null, side, TerrainSize, TerrainHeight );
		EdgeLength = MathF.Max( TerrainSize / Math.Max( 1, side ), 0.05f );
	}

	//An old file's maps made into the mesh and let go of; anything else is a flat ground of edges the length the brush keeps
	private void MakeMesh()
	{
		var side = Resolution;
		var heights = Maps.HeightMap;
		var control = Maps.SplatMap;

		if ( side >= 2 && heights is not null && heights.Length >= side * side )
		{
			Maps.Mesh = TerrainMesh.FromHeights( heights, control, side, TerrainSize, TerrainHeight );
			EdgeLength = MathF.Max( TerrainSize / side, 0.05f );
		}
		else
		{
			side = Math.Clamp( (int)MathF.Round( TerrainSize / MathF.Max( EdgeLength, 0.05f ) ), 2, 2048 );
			Maps.Mesh = TerrainMesh.FromHeights( new ushort[side * side], null, side, TerrainSize, TerrainHeight );
		}

		Maps.HeightMap = Array.Empty<ushort>();
		Maps.SplatMap = Array.Empty<uint>();
		Resolution = 0;
	}

	/// <summary>Uniform world size of the width and length of the terrain.</summary>
	public float TerrainSize { get; set; }

	/// <summary>World size of the maximum height of the terrain.</summary>
	public float TerrainHeight { get; set; }

	//Layers are ordinary materials whose shader declares the layer schema (shaders/terrain_layer.shader)
	/// <summary>What the ground is painted with, in the order the control map indexes them.</summary>
	public List<Material> Materials { get; set; } = new();

	public class TerrainMaterialSettings
	{
		public event Action OnChanged;

		//Titled short: the group already says what they are, and the full names did not fit the inspector's label column.
		[Group( "Height Blend" ), Property, Title( "Enabled" )]
		public bool HeightBlendEnabled
		{
			get => field;
			set
			{
				if ( field == value ) return;
				field = value;
				OnChanged?.Invoke();
			}
		} = true;

		[Group( "Height Blend" ), Property, Range( 0, 1 ), Title( "Sharpness" )]
		public float HeightBlendSharpness
		{
			get => field;
			set
			{
				if ( field == value ) return;
				field = value;
				OnChanged?.Invoke();
			}
		} = 0.87f;

		/// <summary>Texture sampling state used for rendering the terrain.</summary>
		[Property, Advanced]
		public Rendering.SamplerState Sampler
		{
			get => field;
			set
			{
				if ( field == value ) return;
				field = value;
				OnChanged?.Invoke();
			}
		} = new() { Filter = Rendering.FilterMode.Anisotropic, MaxAnisotropy = 8 };
	}

	public TerrainMaterialSettings MaterialSettings { get; set; } = new();

	public TerrainStorage()
	{
		//Half a kilometre across and a quarter of one tall, in metres.
		TerrainSize = 500;
		TerrainHeight = 250;
	}

	//Stores heightmaps and control maps. Serializes as a binary blob for efficiency,
	//with fallback reading of the legacy base64+deflate JSON format for existing files.
	private class TerrainMapBlob : BlobData
	{
		public override int Version => 2;

		public ushort[] HeightMap { get; set; } = Array.Empty<ushort>();
		public uint[] SplatMap { get; set; } = Array.Empty<uint>();

		/// <summary>The ground, once there is one; the maps above are then only what it was made from.</summary>
		public TerrainMesh Mesh;

		//BlobData — binary serialization into the _d sidecar / scene_d file
		public override void Serialize( ref Writer writer )
		{
			//Nothing made yet: a flat ground, made again on load
			if ( Mesh is null && HeightMap.Length == 0 )
			{
				writer.Stream.Write( 0 );
				return;
			}

			//The mesh where there is one, and nothing else: the maps it was made from are not the
			//terrain any more, and a terrain sculpted into caves cannot be put back into them.
			if ( Mesh is not null )
			{
				using var packed = new MemoryStream();
				using ( var into = new BinaryWriter( packed, System.Text.Encoding.UTF8, leaveOpen: true ) ) Mesh.Write( into );

				var meshBytes = Compress<byte>( packed.ToArray() );

				writer.Stream.Write( 1 ); //map_count
				writer.Stream.Write( "mesh" );
				writer.Stream.Write( (byte)1 );
				writer.Stream.Write( (int)packed.Length );
				writer.Stream.Write( meshBytes.Length );
				writer.Stream.Write( meshBytes );
				return;
			}

			writer.Stream.Write( 2 ); //map_count

			//heightmap
			var heightmapBytes = Compress<ushort>( HeightMap );
			writer.Stream.Write( "heightmap" );
			writer.Stream.Write( (byte)sizeof( ushort ) );
			writer.Stream.Write( HeightMap.Length );
			writer.Stream.Write( heightmapBytes.Length );
			writer.Stream.Write( heightmapBytes );

			//splatmap
			var splatmapBytes = Compress<uint>( SplatMap );
			writer.Stream.Write( "splatmap" );
			writer.Stream.Write( (byte)sizeof( uint ) );
			writer.Stream.Write( SplatMap.Length );
			writer.Stream.Write( splatmapBytes.Length );
			writer.Stream.Write( splatmapBytes );
		}

		public override void Deserialize( ref Reader reader )
		{
			int mapCount = reader.Stream.Read<int>();

			for ( int i = 0; i < mapCount; i++ )
			{
				var name = reader.Stream.Read<string>();
				reader.Stream.Read<byte>(); //elementSize (informational)
				reader.Stream.Read<int>(); //elementCount (informational)
				int compressedLength = reader.Stream.Read<int>();
				var compressed = new byte[compressedLength];
				reader.Stream.Read( compressed, 0, compressedLength );

				switch ( name )
				{
					case "heightmap":
						HeightMap = Decompress<ushort>( compressed ).ToArray();
						break;
					case "splatmap":
						SplatMap = Decompress<uint>( compressed ).ToArray();
						break;
					case "mesh":
						using ( var from = new BinaryReader( new MemoryStream( Decompress<byte>( compressed ).ToArray() ) ) )
							Mesh = TerrainMesh.Read( from );
						break;
						//Unknown map type: skip (forward compatibility)
				}
			}
		}

		//Compression helpers — also used by JsonUpgrader methods in TerrainStorage.Upgrade.cs
		internal static Span<T> Decompress<T>( byte[] compressedData ) where T : unmanaged
		{
			using var compressedStream = new MemoryStream( compressedData );
			using var decompressedStream = new MemoryStream();
			using ( var deflateStream = new DeflateStream( compressedStream, CompressionMode.Decompress ) )
			{
				deflateStream.CopyTo( decompressedStream );
			}

			return MemoryMarshal.Cast<byte, T>( decompressedStream.ToArray().AsSpan() );
		}

		internal static byte[] Compress<T>( Span<T> data ) where T : struct
		{
			using var memoryStream = new MemoryStream();
			using ( var deflateStream = new DeflateStream( memoryStream, CompressionMode.Compress ) )
			{
				deflateStream.Write( MemoryMarshal.AsBytes( data ) );
			}

			return memoryStream.ToArray();
		}
	}
}
