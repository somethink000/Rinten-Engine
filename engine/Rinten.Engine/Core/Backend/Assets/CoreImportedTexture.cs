using System.IO;

namespace Rinten;

//Reads a compiled .vtex_c as top-mip RGBA8, for content shipped only in compiled form
internal static class CoreImportedTexture
{
	private static readonly Logger log = new( "Core" );

	//The formats their compiler writes, by the number it writes.
	private enum Format
	{
		Unknown = 0,
		Dxt1 = 1,
		Dxt5 = 2,
		I8 = 3,
		Rgba8888 = 4,
		R16 = 5,
		Rg1616 = 6,
		Rgba16161616 = 7,
		R16F = 8,
		Rg1616F = 9,
		Rgba16161616F = 10,
		R32F = 11,
		Rg3232F = 12,
		Rgb323232F = 13,
		Rgba32323232F = 14,
		JpegRgba8888 = 15,
		PngRgba8888 = 16,
		JpegDxt5 = 17,
		PngDxt5 = 18,
		Bc6H = 19,
		Bc7 = 20,
		Ati2N = 21,
		Ia88 = 22,
		Etc2 = 23,
		Etc2Eac = 24,
		R11Eac = 25,
		Rg11Eac = 26,
		Ati1N = 27,
		Bgra8888 = 28,
	}

	/// <summary>Whether this path names one of their compiled textures.</summary>
	public static bool IsTheirs( string path )
		=> !string.IsNullOrWhiteSpace( path )
		&& path.EndsWith( ".vtex_c", StringComparison.OrdinalIgnoreCase );

	/// <summary>Reads the texture at this path, or null where the file is not one of theirs or is in a format this does not unpack.</summary>
	public static Bitmap Load( string path )
	{
		var resource = CoreImportedResource.Open( path );
		if ( resource is null ) return null;

		if ( resource.Find( "DATA" ) is not { } data ) return null;

		var bytes = resource.Bytes;
		var at = data.Offset;

		if ( at + 40 > bytes.Length ) return null;

		var width = BitConverter.ToUInt16( bytes, at + 20 );
		var height = BitConverter.ToUInt16( bytes, at + 22 );
		var format = (Format)bytes[at + 26];
		var mipCount = bytes[at + 27];

		if ( width == 0 || height == 0 ) return null;

		var extraOffset = at + 32 + (int)BitConverter.ToUInt32( bytes, at + 32 );
		var extraCount = (int)BitConverter.ToUInt32( bytes, at + 36 );

		//How long each mip is stored, where it was packed at all: LZ4 or zstd, when that comes out smaller
		var compressed = CompressedMipSizes( bytes, extraOffset, extraCount, mipCount, out var zstd );

		//The pixels start where the block ends, smallest mip first.
		var payload = data.Offset + data.Size;

		var wanted = Math.Max( 1, (int)mipCount ) - 1; //mip zero, the big one

		for ( var mip = mipCount - 1; mip >= 0; mip-- )
		{
			var mipWidth = Math.Max( 1, width >> mip );
			var mipHeight = Math.Max( 1, height >> mip );

			var size = Size( format, mipWidth, mipHeight );
			if ( size <= 0 ) return null;

			var stored = compressed is null ? size : compressed[mip];

			if ( payload + stored > bytes.Length ) return null;

			if ( mip != 0 )
			{
				payload += stored;
				continue;
			}

			var mipBytes = new ReadOnlySpan<byte>( bytes, payload, stored );

			//Unpacked where it was packed. A mip whose stored length matches its real length was written as it is.
			if ( stored != size )
			{
				var unpacked = new byte[size];

				if ( !Unpack( mipBytes, unpacked, zstd ) )
				{
					log.Warning( $"{Path.GetFileName( path )}: mip {mip} did not unpack" );
					return null;
				}

				mipBytes = unpacked;
			}

			return Decode( format, mipBytes, mipWidth, mipHeight, path );
		}

		return null;
	}

	//Which of our stored formats one of theirs is, or zero where it is not one we can keep as it is.
	private static int BlockFormat( Format format ) => format switch
	{
		Format.Dxt1 => 1,
		Format.Dxt5 => 2,
		Format.Ati1N => 3,
		Format.Ati2N => 4,
		Format.Bc7 => 5,
		_ => 0,
	};

	/// <summary>Compiles a block-compressed .vtex_c texture into ours without decoding; false means unpack instead</summary>
	public static bool CompileAsBlocks( string fullPath, string output, bool srgb )
	{
		if ( !CoreNative.Available || CoreRender.SupportsBc() == 0 ) return false;
		if ( string.IsNullOrWhiteSpace( fullPath ) || !File.Exists( fullPath ) ) return false;

		var resource = CoreImportedResource.Open( fullPath );
		if ( resource is null ) return false;

		if ( resource.Find( "DATA" ) is not { } data ) return false;

		var bytes = resource.Bytes;
		var at = data.Offset;

		if ( at + 40 > bytes.Length ) return false;

		var width = BitConverter.ToUInt16( bytes, at + 20 );
		var height = BitConverter.ToUInt16( bytes, at + 22 );
		var format = (Format)bytes[at + 26];
		var mipCount = Math.Max( 1, (int)bytes[at + 27] );

		if ( width == 0 || height == 0 ) return false;

		var stored = BlockFormat( format );
		if ( stored == 0 ) return false;

		var extraOffset = at + 32 + (int)BitConverter.ToUInt32( bytes, at + 32 );
		var extraCount = (int)BitConverter.ToUInt32( bytes, at + 36 );

		var compressed = CompressedMipSizes( bytes, extraOffset, extraCount, mipCount, out var zstd );

		//Theirs are stored smallest first and ours largest first, so the levels are gathered as they come and turned round at the end.
		var levels = new byte[mipCount][];
		var payload = data.Offset + data.Size;

		for ( var mip = mipCount - 1; mip >= 0; mip-- )
		{
			var mipWidth = Math.Max( 1, width >> mip );
			var mipHeight = Math.Max( 1, height >> mip );

			var size = Size( format, mipWidth, mipHeight );
			if ( size <= 0 ) return false;

			var length = compressed is null ? size : compressed[mip];
			if ( payload + length > bytes.Length ) return false;

			var block = new byte[size];

			if ( length == size )
			{
				Array.Copy( bytes, payload, block, 0, size );
			}
			else if ( !Unpack( new ReadOnlySpan<byte>( bytes, payload, length ), block, zstd ) )
			{
				log.Warning( $"{Path.GetFileName( fullPath )}: mip {mip} did not unpack" );
				return false;
			}

			levels[mip] = block;
			payload += length;
		}

		var total = 0;
		var sizes = new int[mipCount];

		for ( var mip = 0; mip < mipCount; mip++ )
		{
			if ( levels[mip] is null ) return false;

			sizes[mip] = levels[mip].Length;
			total += sizes[mip];
		}

		var all = new byte[total];
		var written = 0;

		for ( var mip = 0; mip < mipCount; mip++ )
		{
			Array.Copy( levels[mip], 0, all, written, sizes[mip] );
			written += sizes[mip];
		}

		return CoreTextureFile.Write( output, width, height, (CoreTextureFile.TextureFormat)stored, srgb, sizes, all );
	}

	/// <summary>Device texture keeping the original blocks, using a newer compiled file if present; 0 means unpack</summary>
	public static ulong BlockTexture( string fullPath, bool srgb )
	{
		if ( string.IsNullOrWhiteSpace( fullPath ) || !File.Exists( fullPath ) ) return 0;

		var blocks = CompiledPaths.TextureBlocks( fullPath, srgb );

		if ( File.Exists( blocks ) &&
			File.GetLastWriteTimeUtc( blocks ) >= File.GetLastWriteTimeUtc( fullPath ) )
		{
			var kept = CoreRenderDevice.CreateTexture( blocks );
			if ( kept != 0 ) return kept;
		}

		if ( !CompileAsBlocks( fullPath, blocks, srgb ) ) return 0;

		return CoreRenderDevice.CreateTexture( blocks );
	}

	//One of theirs is unpacked once and written out beside it as a png. The file ends in _c.png , which the ported projects' gitignore already covers, and it is rewritten whenever the texture it came from is newer.
	/// <summary>A path to an image file for this texture, which is what our own texture compiler takes.</summary>
	public static string AsImageFile( string fullPath )
	{
		if ( string.IsNullOrWhiteSpace( fullPath ) || !File.Exists( fullPath ) ) return null;

		var unpacked = fullPath + ".png";

		if ( File.Exists( unpacked ) &&
			File.GetLastWriteTimeUtc( unpacked ) >= File.GetLastWriteTimeUtc( fullPath ) )
		{
			return unpacked;
		}

		using var bitmap = Load( fullPath );
		if ( bitmap is null ) return null;

		try
		{
			File.WriteAllBytes( unpacked, bitmap.ToPng() );
		}
		catch ( IOException e )
		{
			log.Warning( $"could not write {Path.GetFileName( unpacked )}: {e.Message}" );
			return null;
		}

		return unpacked;
	}

	//One packed mip into its full length
	private static bool Unpack( ReadOnlySpan<byte> packed, byte[] into, bool zstd )
	{
		if ( !zstd ) return Rinten.Compression.LZ4.DecompressBlock( packed, into ) == into.Length;

		try
		{
			using var decompressor = new ZstdSharp.Decompressor();
			return decompressor.Unwrap( packed, into ) == into.Length;
		}
		catch ( ZstdSharp.ZstdException )
		{
			return false;
		}
	}

	//The per-mip lengths, where the file says its mips are compressed, or null where they are stored as they are.
	private static int[] CompressedMipSizes( byte[] bytes, int extraOffset, int extraCount, int mipCount, out bool zstd )
	{
		const int compressedMipSize = 4;

		zstd = false;

		for ( var i = 0; i < extraCount; i++ )
		{
			var entry = extraOffset + i * 12;
			if ( entry + 12 > bytes.Length ) return null;

			var type = BitConverter.ToUInt32( bytes, entry );
			if ( type != compressedMipSize ) continue;

			//The offset is from the field holding it, as everywhere else here.
			var at = entry + 4 + (int)BitConverter.ToUInt32( bytes, entry + 4 );

			if ( at + 12 > bytes.Length ) return null;

			//Method first: 1 LZ4, 2 zstd
			zstd = BitConverter.ToUInt32( bytes, at ) == 2;

			var mips = (int)BitConverter.ToUInt32( bytes, at + 8 );
			if ( mips <= 0 || mips > 32 ) return null;

			at += 12;

			if ( at + mips * 4 > bytes.Length ) return null;

			var sizes = new int[Math.Max( mips, mipCount )];

			for ( var mip = 0; mip < mips; mip++ )
			{
				sizes[mip] = (int)BitConverter.ToUInt32( bytes, at + mip * 4 );
			}

			return sizes;
		}

		return null;
	}

	//How many bytes one mip of this size takes in this format.
	private static int Size( Format format, int width, int height )
	{
		var blocks = ( ( width + 3 ) / 4 ) * ( ( height + 3 ) / 4 );

		return format switch
		{
			Format.Dxt1 or Format.Ati1N => blocks * 8,
			Format.Dxt5 or Format.Ati2N or Format.Bc7 or Format.Bc6H => blocks * 16,
			Format.I8 => width * height,
			Format.Ia88 => width * height * 2,
			Format.Rgba8888 or Format.Bgra8888 => width * height * 4,
			Format.R16 => width * height * 2,
			Format.Rg1616 => width * height * 4,
			Format.Rgba16161616 => width * height * 8,
			Format.R16F => width * height * 2,
			Format.Rg1616F => width * height * 4,
			Format.Rgba16161616F => width * height * 8,
			Format.R32F => width * height * 4,
			Format.Rg3232F => width * height * 8,
			Format.Rgb323232F => width * height * 12,
			Format.Rgba32323232F => width * height * 16,
			_ => -1,
		};
	}

	//The mip's bytes, as a picture.
	private static Bitmap Decode( Format format, ReadOnlySpan<byte> data, int width, int height, string path )
	{
		byte[] pixels = format switch
		{
			Format.Dxt1 => CoreBlockCompression.Decode( CoreBlockCompression.Kind.Bc1, data, width, height ),
			Format.Dxt5 => CoreBlockCompression.Decode( CoreBlockCompression.Kind.Bc3, data, width, height ),
			Format.Ati1N => CoreBlockCompression.Decode( CoreBlockCompression.Kind.Bc4, data, width, height ),
			Format.Ati2N => CoreBlockCompression.Decode( CoreBlockCompression.Kind.Bc5, data, width, height ),
			Format.Bc7 => CoreBlockCompression.Decode( CoreBlockCompression.Kind.Bc7, data, width, height ),
			Format.Rgba8888 => data.ToArray(),
			Format.Bgra8888 => Swizzle( data ),
			Format.I8 => Grey( data ),
			Format.Ia88 => GreyAlpha( data ),
			_ => null,
		};

		if ( pixels is null )
		{
			log.Warning( $"{Path.GetFileName( path )} is {format}, which is not read yet" );
			return null;
		}

		var bitmap = new Bitmap( width, height );
		bitmap.SetPixelsRgba( pixels );

		return bitmap;
	}

	private static byte[] Swizzle( ReadOnlySpan<byte> data )
	{
		var pixels = data.ToArray();

		for ( var i = 0; i + 3 < pixels.Length; i += 4 )
		{
			( pixels[i], pixels[i + 2] ) = ( pixels[i + 2], pixels[i] );
		}

		return pixels;
	}

	private static byte[] Grey( ReadOnlySpan<byte> data )
	{
		var pixels = new byte[data.Length * 4];

		for ( var i = 0; i < data.Length; i++ )
		{
			pixels[i * 4 + 0] = data[i];
			pixels[i * 4 + 1] = data[i];
			pixels[i * 4 + 2] = data[i];
			pixels[i * 4 + 3] = 255;
		}

		return pixels;
	}

	private static byte[] GreyAlpha( ReadOnlySpan<byte> data )
	{
		var count = data.Length / 2;
		var pixels = new byte[count * 4];

		for ( var i = 0; i < count; i++ )
		{
			pixels[i * 4 + 0] = data[i * 2];
			pixels[i * 4 + 1] = data[i * 2];
			pixels[i * 4 + 2] = data[i * 2];
			pixels[i * 4 + 3] = data[i * 2 + 1];
		}

		return pixels;
	}
}
