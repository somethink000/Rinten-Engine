using System.Buffers.Binary;
using System.IO;
using Rinten.Native;

namespace Rinten;

//Compiled texture file: compile from images, write from blocks, read back; flat offset-based layout
internal sealed partial class CoreTextureFile
{
	/// <summary>"ESST", little-endian.</summary>
	public const uint Magic = 0x54535345;

	/// <summary>File version; bumped to force recompiling older files even when the layout did not change</summary>
	public const uint Version = 3;

	/// <summary>How the pixels are stored.</summary>
	public enum TextureFormat
	{
		/// <summary>Eight bits a channel, four channels: what images decode to and every GPU takes.</summary>
		Rgba8 = 0,

		/// <summary>Colour, one bit of alpha if any. Eight bytes a block.</summary>
		Bc1 = 1,

		/// <summary>Colour with a full alpha channel. Sixteen bytes a block.</summary>
		Bc3 = 2,

		/// <summary>One channel. Eight bytes a block.</summary>
		Bc4 = 3,

		/// <summary>Two channels, a normal map's; the third is rebuilt in the shader. Sixteen bytes a block.</summary>
		Bc5 = 4,

		/// <summary>Colour and alpha at the highest quality of the family. Sixteen bytes a block.</summary>
		Bc7 = 5,

		//6 to 8 are the device's own stored formats
		/// <summary>HDR light as RGB9E5, four bytes a texel, no alpha.</summary>
		Rgb9e5 = 9,

		/// <summary>HDR light as unsigned BC6H blocks, for a device that takes BC.</summary>
		Bc6h = 10,
	}

	/// <summary>One level of the mip chain: its size, and how many bytes it takes.</summary>
	public readonly record struct Level( int Width, int Height, int ByteCount );

	//The header: twelve uint32s, which is 48 bytes and so needs no padding after it. Then an array
	//of mips, four uint32s each, then the pixels, each piece starting on an eight byte boundary.
	private const int HeaderSize = 48;
	private const int MipSize = 16;

	private const int HeaderMagic = 0;
	private const int HeaderVersion = 4;

	//Checked against what was read, so a truncated file does not load with garbage for its last mip
	private const int HeaderByteCount = 8;
	private const int HeaderWidth = 16;
	private const int HeaderHeight = 20;
	private const int HeaderFormat = 24;

	//Whether the values are gamma-encoded and want converting on read. Colour is; normals, roughness
	//and masks are not, and treating them as though they were is the single most common texture mistake.
	private const int HeaderSrgb = 28;
	private const int HeaderMipCount = 32;
	private const int HeaderMipOffset = 36;
	private const int HeaderPixelOffset = 40;
	private const int HeaderPixelByteCount = 44;

	private readonly byte[] bytes;
	private readonly int mipOffset;
	private readonly int pixelOffset;

	public int Width { get; }
	public int Height { get; }
	public int MipCount { get; }
	public bool IsSrgb { get; }
	public TextureFormat Format { get; }

	private CoreTextureFile( byte[] bytes, int mipOffset, int pixelOffset )
	{
		this.bytes = bytes;
		this.mipOffset = mipOffset;
		this.pixelOffset = pixelOffset;

		Width = (int)ReadU32( bytes, HeaderWidth );
		Height = (int)ReadU32( bytes, HeaderHeight );
		Format = (TextureFormat)ReadU32( bytes, HeaderFormat );
		IsSrgb = ReadU32( bytes, HeaderSrgb ) != 0;
		MipCount = (int)ReadU32( bytes, HeaderMipCount );
	}

	/// <summary>The size and byte count of one level, or an empty one for a level that is not there.</summary>
	public Level MipInfo( int mip )
	{
		if ( mip < 0 || mip >= MipCount ) return default;

		var at = mipOffset + ( mip * MipSize );

		return new Level( (int)ReadU32( bytes, at ), (int)ReadU32( bytes, at + 4 ), (int)ReadU32( bytes, at + 12 ) );
	}

	/// <summary>The bytes of one level, as they lie in the file. Empty for a level that is not there.</summary>
	public ReadOnlySpan<byte> Mip( int mip )
	{
		if ( mip < 0 || mip >= MipCount ) return default;

		var at = mipOffset + ( mip * MipSize );
		var offset = (int)ReadU32( bytes, at + 8 );
		var byteCount = (int)ReadU32( bytes, at + 12 );

		return new ReadOnlySpan<byte>( bytes, pixelOffset + offset, byteCount );
	}

	/// <summary>Each level's width, height and byte count, and all levels' bytes, largest first.</summary>
	public void Levels( Span<int> info, out ReadOnlySpan<byte> pixels )
	{
		for ( var mip = 0; mip < MipCount; ++mip )
		{
			var level = MipInfo( mip );

			info[( mip * 3 ) + 0] = level.Width;
			info[( mip * 3 ) + 1] = level.Height;
			info[( mip * 3 ) + 2] = level.ByteCount;
		}

		pixels = new ReadOnlySpan<byte>( bytes, pixelOffset, (int)ReadU32( bytes, HeaderPixelByteCount ) );
	}

	/// <summary>Reads a compiled texture; null if missing, of another version, or truncated.</summary>
	public static CoreTextureFile Load( string path )
	{
		if ( string.IsNullOrEmpty( path ) ) return null;

		byte[] bytes;

		try
		{
			if ( !File.Exists( path ) ) return null;

			bytes = SealedFile.ReadAllBytes( path );
		}
		catch ( IOException )
		{
			return null;
		}
		catch ( UnauthorizedAccessException )
		{
			return null;
		}

		return FromBytes( bytes );
	}

	/// <summary>A compiled texture from bytes in memory, e.g. a model's impostor; null if not one.</summary>
	public static CoreTextureFile FromBytes( byte[] bytes )
	{
		if ( bytes is null || bytes.Length < HeaderSize ) return null;

		if ( ReadU32( bytes, HeaderMagic ) != Magic ) return null;
		if ( ReadU32( bytes, HeaderVersion ) != Version ) return null;
		if ( ReadU32( bytes, HeaderByteCount ) != (uint)bytes.Length ) return null;

		var mipCount = ReadU32( bytes, HeaderMipCount );
		var mipOffset = ReadU32( bytes, HeaderMipOffset );
		var pixelOffset = ReadU32( bytes, HeaderPixelOffset );
		var pixelByteCount = ReadU32( bytes, HeaderPixelByteCount );

		//Every block has to lie inside the file, or an accessor reads whatever followed it in memory.
		var mipEnd = (long)mipOffset + ( (long)MipSize * mipCount );
		var pixelEnd = (long)pixelOffset + pixelByteCount;

		if ( mipEnd > bytes.Length ) return null;
		if ( pixelEnd > bytes.Length ) return null;

		//And every mip has to lie inside the pixel block.
		for ( var i = 0; i < mipCount; ++i )
		{
			var at = (int)mipOffset + ( i * MipSize );
			var end = (long)ReadU32( bytes, at + 8 ) + ReadU32( bytes, at + 12 );

			if ( end > pixelByteCount ) return null;
		}

		return new CoreTextureFile( bytes, (int)mipOffset, (int)pixelOffset );
	}

	//A level of the chain while it is being built.
	private struct Building
	{
		public int Width;
		public int Height;
		public byte[] Pixels; //RGBA8, or blocks once packed
	}

	//Halve an image, averaging each two by two block. A box filter, which is the honest default: sharper filters are a
	//choice about how a particular texture should look, and belong in the descriptor rather than baked into everything.
	private static Building Halve( in Building from )
	{
		var to = new Building
		{
			Width = from.Width > 1 ? from.Width / 2 : 1,
			Height = from.Height > 1 ? from.Height / 2 : 1,
		};

		to.Pixels = new byte[to.Width * to.Height * 4];

		var source = from.Pixels;
		var fromWidth = from.Width;

		for ( var y = 0; y < to.Height; ++y )
		{
			//An odd edge has no partner row or column; sample the last one twice rather than read past it
			var y0 = y * 2;
			var y1 = ( y0 + 1 < from.Height ) ? y0 + 1 : y0;

			for ( var x = 0; x < to.Width; ++x )
			{
				var x0 = x * 2;
				var x1 = ( x0 + 1 < fromWidth ) ? x0 + 1 : x0;

				for ( var c = 0; c < 4; ++c )
				{
					var sum =
						source[( ( ( y0 * fromWidth ) + x0 ) * 4 ) + c] +
						source[( ( ( y0 * fromWidth ) + x1 ) * 4 ) + c] +
						source[( ( ( y1 * fromWidth ) + x0 ) * 4 ) + c] +
						source[( ( ( y1 * fromWidth ) + x1 ) * 4 ) + c];

					to.Pixels[( ( ( y * to.Width ) + x ) * 4 ) + c] = (byte)( ( sum + 2 ) / 4 );
				}
			}
		}

		return to;
	}

	//An image's pixels, four channels whatever it holds - or null where it is not one.
	private static unsafe byte[] Decode( byte[] encoded, out int width, out int height )
	{
		width = height = 0;
		if ( encoded is null || encoded.Length == 0 ) return null;

		int w, h, channels;

		fixed ( byte* bytes = encoded )
		{
			//Always four channels: a predictable stride is worth more to the GPU than the bytes saved
			var decoded = StbImage.stbi_load_from_memory( bytes, encoded.Length, &w, &h, &channels, 4 );

			if ( decoded == null ) return null;

			if ( w <= 0 || h <= 0 )
			{
				StbImage.stbi_image_free( decoded );
				return null;
			}

			var rgba = new ReadOnlySpan<byte>( decoded, w * h * 4 ).ToArray();
			StbImage.stbi_image_free( decoded );

			width = w;
			height = h;
			return rgba;
		}
	}

	//Multiplies a mask's grey into the pixels' alpha - read where each pixel falls on
	//it, so a mask of another size still lines up. False where the mask cannot be read.
	private static bool CutBy( byte[] rgba, int width, int height, string mask )
	{
		byte[] cut;
		int cutWidth, cutHeight;

		try
		{
			if ( !File.Exists( mask ) ) return false;

			cut = Decode( SealedFile.ReadAllBytes( mask ), out cutWidth, out cutHeight );
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			return false;
		}

		if ( cut is null ) return false;

		for ( var y = 0; y < height; y++ )
		{
			var row = (int)( ( y + 0.5f ) * cutHeight / height ) * cutWidth;

			for ( var x = 0; x < width; x++ )
			{
				var at = ( row + (int)( ( x + 0.5f ) * cutWidth / width ) ) * 4;
				var to = ( ( y * width ) + x ) * 4 + 3;

				rgba[to] = (byte)( ( rgba[to] * cut[at] + 127 ) / 255 );
			}
		}

		return true;
	}

	//The alpha a cut-out is judged at where nobody said: half.
	private const int MaskCutoff = 128;

	//Whether alpha is a cutout mask rather than a gradient, deciding if coverage is kept across mips
	private static bool IsMask( byte[] pixels )
	{
		var texels = pixels.Length / 4;
		int holes = 0, solid = 0, between = 0;

		for ( var i = 3; i < pixels.Length; i += 4 )
		{
			var a = pixels[i];

			if ( a < 16 ) holes++;
			else if ( a > 239 ) solid++;
			else between++;
		}

		//Both there, each more than a rounding, and the soft edge a minority.
		return holes > texels / 256 && solid > texels / 256 && between < texels / 4;
	}

	//Scales each mip's alpha to keep the same coverage at the cutoff as mip 0 (Castaño 2010)
	private static void KeepCoverage( List<Building> levels )
	{
		var wanted = Coverage( levels[0].Pixels, 1.0f );

		if ( wanted <= 0.0f || wanted >= 1.0f ) return;

		for ( var i = 1; i < levels.Count; ++i )
		{
			var pixels = levels[i].Pixels;

			//By halving: the share over the cut only grows with the scale.
			float low = 0.25f, high = 8.0f;

			for ( var step = 0; step < 12; ++step )
			{
				var middle = ( low + high ) * 0.5f;

				if ( Coverage( pixels, middle ) < wanted ) low = middle;
				else high = middle;
			}

			var scale = ( low + high ) * 0.5f;

			if ( MathF.Abs( scale - 1.0f ) < 0.01f ) continue;

			for ( var p = 3; p < pixels.Length; p += 4 )
				pixels[p] = (byte)Math.Min( 255, (int)MathF.Round( pixels[p] * scale ) );
		}
	}

	//The share of a level's texels whose alpha, scaled, is at or over the cut.
	private static float Coverage( byte[] pixels, float scale )
	{
		var over = 0;
		var least = MaskCutoff / scale;

		for ( var i = 3; i < pixels.Length; i += 4 )
			if ( pixels[i] >= least ) over++;

		return over / (float)( pixels.Length / 4 );
	}

	//What was set for one picture: no mip chain, no blocks, a change of brightness.
	internal readonly record struct PictureSettings( bool NoMips, bool NoCompress, float Brightness );

	//Per-picture settings from the editor's .meta, baked into blocks by any compile; null if none
	internal static Func<string, PictureSettings?> SettingsFor { get; set; }

	/// <summary>Compiles an image file into a texture file.</summary>
	public static unsafe bool Compile( string source, string output, bool srgb, bool generateMips, bool compress, string cutBy = null )
	{
		if ( string.IsNullOrEmpty( output ) ) return false;

		if ( IsHdr( source ) ) return CompileHdr( source, output, generateMips, compress );

		var rgba = Prepared( source, cutBy, ref generateMips, ref compress, out var width, out var height );
		if ( rgba is null ) return false;

		var file = Build( rgba, width, height, srgb, generateMips, compress );
		if ( file is null ) return false;

		try
		{
			File.WriteAllBytes( output, file );
		}
		catch ( IOException )
		{
			return false;
		}
		catch ( UnauthorizedAccessException )
		{
			return false;
		}

		return true;
	}

	//A picture's top level as decoded, cut by its mask and brightened per its settings, which also fold into the
	//mips and blocks asked for. Null where the picture could not be read
	private static byte[] Prepared( string source, string cutBy, ref bool generateMips, ref bool compress, out int width, out int height )
	{
		width = 0;
		height = 0;

		PictureSettings? settings = null;

		try
		{
			settings = SettingsFor?.Invoke( source );
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"{source}: its settings could not be read ({e.Message})" );
		}

		if ( settings is { } set )
		{
			generateMips &= !set.NoMips;
			compress &= !set.NoCompress;
		}

		if ( string.IsNullOrEmpty( source ) ) return null;

		byte[] encoded;

		try
		{
			if ( !File.Exists( source ) ) return null;

			encoded = SealedFile.ReadAllBytes( source );
		}
		catch ( IOException )
		{
			return null;
		}
		catch ( UnauthorizedAccessException )
		{
			return null;
		}

		var rgba = Decode( encoded, out width, out height );
		if ( rgba is null ) return null;

		//Cut before anything is made from the alpha, so the mips keep its coverage as a picture's own
		if ( !string.IsNullOrEmpty( cutBy ) && !CutBy( rgba, width, height, cutBy ) ) return null;

		//Brightness scales colour, not alpha, before the mips so every level agrees
		if ( settings is { } bright && MathF.Abs( bright.Brightness - 1.0f ) > 0.001f )
		{
			var scale = MathF.Max( bright.Brightness, 0.0f );

			for ( var i = 0; i < rgba.Length; i += 4 )
			{
				rgba[i] = (byte)Math.Min( 255, (int)MathF.Round( rgba[i] * scale ) );
				rgba[i + 1] = (byte)Math.Min( 255, (int)MathF.Round( rgba[i + 1] * scale ) );
				rgba[i + 2] = (byte)Math.Min( 255, (int)MathF.Round( rgba[i + 2] * scale ) );
			}
		}

		return rgba;
	}

	/// <summary>The mip chain Compile builds, for other block formats (web KTX2); empty if kept uncompressed.</summary>
	public static IReadOnlyList<(byte[] Pixels, int Width, int Height)> CompileLevels( string source, string cutBy = null )
	{
		var generateMips = true;
		var compress = true;

		var rgba = Prepared( source, cutBy, ref generateMips, ref compress, out var width, out var height );
		if ( rgba is null ) return null;
		if ( !compress ) return Array.Empty<(byte[], int, int)>();

		return Chain( rgba, width, height, generateMips, alphaIsCut: true ).ConvertAll( level => (level.Pixels, level.Width, level.Height) );
	}

	/// <summary>Texture file bytes from RGBA8 pixels, built as Compile builds a picture; null on failure.</summary>
	public static byte[] Build( byte[] rgba, int width, int height, bool srgb, bool generateMips, bool compress, bool alphaIsCut = true )
	{
		if ( rgba is null || width <= 0 || height <= 0 || rgba.Length < width * height * 4 ) return null;

		return Pack( Chain( rgba, width, height, generateMips, alphaIsCut ), width, height, srgb, compress );
	}

	//The top level and, where asked, every level under it down to one pixel.
	private static List<Building> Chain( byte[] rgba, int width, int height, bool generateMips, bool alphaIsCut )
	{
		var levels = new List<Building> { new() { Width = width, Height = height, Pixels = rgba } };

		if ( generateMips )
		{
			//Down to one pixel: dropping the last levels makes distant surfaces flicker
			while ( levels[^1].Width > 1 || levels[^1].Height > 1 )
				levels.Add( Halve( levels[^1] ) );

			//A picture something is cut out by keeps what it covers, level to level - after
			//every level is made, so none is halved from a level that was already lifted.
			if ( alphaIsCut && IsMask( levels[0].Pixels ) ) KeepCoverage( levels );
		}

		return levels;
	}

	/// <summary>Texture bytes from a caller-built mip chain (impostors); keepAlpha forces an alpha format.</summary>
	public static byte[] BuildLevels( IReadOnlyList<(byte[] Pixels, int Width, int Height)> chain, bool srgb, bool compress, bool keepAlpha = false )
	{
		if ( chain is null || chain.Count == 0 ) return null;

		var levels = new List<Building>( chain.Count );

		for ( var i = 0; i < chain.Count; i++ )
		{
			var (pixels, width, height) = chain[i];
			if ( pixels is null || width <= 0 || height <= 0 || pixels.Length < width * height * 4 ) return null;

			if ( i > 0 && ( width != Math.Max( 1, levels[i - 1].Width / 2 ) || height != Math.Max( 1, levels[i - 1].Height / 2 ) ) )
				return null;

			levels.Add( new Building { Width = width, Height = height, Pixels = pixels } );
		}

		return Pack( levels, chain[0].Width, chain[0].Height, srgb, compress, keepAlpha );
	}

	//The levels into blocks, where asked, and into the file.
	private static byte[] Pack( List<Building> levels, int width, int height, bool srgb, bool compress, bool keepAlpha = false )
	{
		//Compress if at least 4x4: BC1 when fully opaque, BC3 when alpha is used
		var blocks = compress && width >= 4 && height >= 4;
		var opaque = blocks && !keepAlpha && CoreBlockEncoder.AllOpaque( levels[0].Pixels, width * height );

		if ( blocks )
		{
			for ( var i = 0; i < levels.Count; ++i )
			{
				var level = levels[i];
				var packed = new byte[CoreBlockEncoder.BlockByteCount( level.Width, level.Height, !opaque )];

				if ( opaque ) CoreBlockEncoder.EncodeBc1( level.Pixels, level.Width, level.Height, packed );
				else CoreBlockEncoder.EncodeBc3( level.Pixels, level.Width, level.Height, packed );

				level.Pixels = packed;
				levels[i] = level;
			}
		}

		var format = blocks
			? ( opaque ? TextureFormat.Bc1 : TextureFormat.Bc3 )
			: TextureFormat.Rgba8;

		Span<int> sizes = levels.Count <= 32 ? stackalloc int[levels.Count] : new int[levels.Count];
		var total = 0;

		for ( var i = 0; i < levels.Count; ++i )
		{
			sizes[i] = levels[i].Pixels.Length;
			total += sizes[i];
		}

		//One buffer for the pixels of every level, largest first - the shape the file holds them in.
		var pixels = new byte[total];
		var at = 0;

		foreach ( var level in levels )
		{
			level.Pixels.CopyTo( pixels, at );
			at += level.Pixels.Length;
		}

		return FileBytes( width, height, format, srgb, sizes, pixels );
	}

	//For content that arrives compressed: its blocks are written through unchanged
	/// <summary>Writes a compiled texture from bytes already in their final form.</summary>
	public static bool Write( string output, int width, int height, TextureFormat format, bool srgb,
		ReadOnlySpan<int> mipSizes, ReadOnlySpan<byte> data )
	{
		if ( string.IsNullOrEmpty( output ) || data.IsEmpty || mipSizes.IsEmpty ) return false;
		if ( width <= 0 || height <= 0 ) return false;
		if ( format < TextureFormat.Rgba8 || format > TextureFormat.Bc7 ) return false;

		var file = FileBytes( width, height, format, srgb, mipSizes, data );
		if ( file is null ) return false;

		try
		{
			File.WriteAllBytes( output, file );
		}
		catch ( IOException )
		{
			return false;
		}
		catch ( UnauthorizedAccessException )
		{
			return false;
		}

		return true;
	}

	//The file's bytes, or null where the sizes do not fit the data.
	private static byte[] FileBytes( int width, int height, TextureFormat format, bool srgb,
		ReadOnlySpan<int> mipSizes, ReadOnlySpan<byte> data )
	{
		var mipCount = mipSizes.Length;

		//The header, the mip table on the next eight byte boundary, the pixels on the one after that. The
		//header is 48 bytes so the table follows it directly; the pixels are padded up to a multiple of eight.
		var mipAt = HeaderSize;
		var mipBytes = mipCount * MipSize;
		var pixelAt = Align8( mipAt + mipBytes );
		var fileSize = pixelAt + data.Length;

		var file = new byte[fileSize];

		var offset = 0u;
		var levelWidth = (uint)width;
		var levelHeight = (uint)height;

		for ( var level = 0; level < mipCount; ++level )
		{
			if ( mipSizes[level] <= 0 ) return null;

			//The caller's sizes, checked so a wrong one cannot run the last mip off the end of the bytes
			if ( offset + (uint)mipSizes[level] > (uint)data.Length ) return null;

			var at = mipAt + ( level * MipSize );

			WriteU32( file, at, levelWidth );
			WriteU32( file, at + 4, levelHeight );
			WriteU32( file, at + 8, offset );
			WriteU32( file, at + 12, (uint)mipSizes[level] );

			offset += (uint)mipSizes[level];

			levelWidth = levelWidth > 1 ? levelWidth / 2 : 1;
			levelHeight = levelHeight > 1 ? levelHeight / 2 : 1;
		}

		data.CopyTo( file.AsSpan( pixelAt ) );

		WriteU32( file, HeaderMagic, Magic );
		WriteU32( file, HeaderVersion, Version );
		WriteU32( file, HeaderByteCount, (uint)fileSize );
		WriteU32( file, HeaderWidth, (uint)width );
		WriteU32( file, HeaderHeight, (uint)height );
		WriteU32( file, HeaderFormat, (uint)format );
		WriteU32( file, HeaderSrgb, srgb ? 1u : 0u );
		WriteU32( file, HeaderMipCount, (uint)mipCount );
		WriteU32( file, HeaderMipOffset, (uint)mipAt );
		WriteU32( file, HeaderPixelOffset, (uint)pixelAt );
		WriteU32( file, HeaderPixelByteCount, (uint)data.Length );

		return file;
	}

	private static int Align8( int at ) => ( at + 7 ) & ~7;

	private static uint ReadU32( byte[] bytes, int at ) => BinaryPrimitives.ReadUInt32LittleEndian( bytes.AsSpan( at, 4 ) );

	private static void WriteU32( byte[] bytes, int at, uint value ) => BinaryPrimitives.WriteUInt32LittleEndian( bytes.AsSpan( at, 4 ), value );
}
