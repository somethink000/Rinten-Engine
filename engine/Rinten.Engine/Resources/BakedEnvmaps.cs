using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Serialization;

namespace Rinten;

/// <summary>A scene's baked envmap probes as an asset: each Baked probe's picture, taken by the scene's bake - see
/// BakedLighting.Envmaps.</summary>
[Expose]
[AssetType( Name = "Envmaps", Extension = "envmaps", Category = "Rendering", Icon = "panorama_photosphere" )]
public sealed class BakedEnvmaps : GameResource
{
	/// <summary>What the last bake into it took; empty until one has.</summary>
	[JsonInclude, Hide]
	public BakedEnvmapsData Data { get; set; } = new();
}

/// <summary>Each baked probe's picture: an octahedral square with its blurred bands stacked under it, RGBM - see
/// CoreEnvmap.Fold.</summary>
public sealed class BakedEnvmapsData : BlobData
{
	/// <summary>One probe's picture.</summary>
	public struct Picture
	{
		/// <summary>The probe, by its GameObject's id.</summary>
		public Guid Probe;

		/// <summary>Four bytes a texel, Side wide and Side times Bands tall.</summary>
		public byte[] Pixels;
	}

	/// <summary>A band's width and height in texels.</summary>
	public int Side { get; set; }

	/// <summary>How many bands each picture stacks, sharpest first.</summary>
	public int Bands { get; set; }

	public List<Picture> Pictures { get; set; } = new();

	/// <summary>The picture taken for a probe, or null.</summary>
	public byte[] PixelsOf( Guid probe )
	{
		foreach ( var picture in Pictures )
			if ( picture.Probe == probe ) return picture.Pixels;

		return null;
	}

	public override void Serialize( ref Writer writer )
	{
		var s = writer.Stream;

		s.Write( Side );
		s.Write( Bands );
		s.Write( Pictures.Count );

		foreach ( var picture in Pictures )
		{
			s.Write( picture.Probe );

			using var memory = new MemoryStream();

			using ( var deflate = new DeflateStream( memory, CompressionLevel.Optimal, leaveOpen: true ) )
				deflate.Write( picture.Pixels ?? [] );

			var bytes = memory.ToArray();

			s.Write( picture.Pixels?.Length ?? 0 );
			s.Write( bytes.Length );
			s.Write( bytes );
		}

		writer.Stream = s;
	}

	public override void Deserialize( ref Reader reader )
	{
		var s = reader.Stream;

		Side = s.Read<int>();
		Bands = s.Read<int>();

		var count = s.Read<int>();
		Pictures = new List<Picture>( count );

		for ( var i = 0; i < count; ++i )
		{
			var probe = s.Read<Guid>();
			var length = s.Read<int>();
			var packed = new byte[s.Read<int>()];
			s.Read( packed, 0, packed.Length );

			var pixels = new byte[length];

			using ( var deflate = new DeflateStream( new MemoryStream( packed ), CompressionMode.Decompress ) )
				deflate.ReadExactly( pixels );

			Pictures.Add( new Picture { Probe = probe, Pixels = pixels } );
		}

		reader.Stream = s;
	}
}
