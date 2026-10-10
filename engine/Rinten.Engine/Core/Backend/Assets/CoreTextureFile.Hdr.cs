using System.IO;
using Rinten.Native;

namespace Rinten;

//.exr and .hdr pictures: mipped in floats, then BC6H where the device takes BC, RGB9E5 where not
internal sealed partial class CoreTextureFile
{
	//Nine bits of mantissa over a shared five-bit exponent biased by fifteen - the Vulkan, WebGPU and GL layout
	private const int SharedBits = 9;
	private const int SharedBias = 15;

	//The brightest a channel can hold: 511/512 of two to the sixteenth
	private const float SharedMax = 65408.0f;

	//Reads an .exr into RGBA floats, rows from the top; set by the editor, as a game ships it compiled
	internal static Func<byte[], string, (float[] Rgba, int Width, int Height)?> DecodeExr { get; set; }

	//The brightest a BC6H block holds: the largest finite half
	private const float HalfMax = 65504.0f;

	//Packs sixteen RGB halves into a BC6H block; set by the editor, without it HDR compiles to RGB9E5
	internal static BlockEncoder EncodeBc6h { get; set; }

	internal delegate void BlockEncoder( ReadOnlySpan<ushort> rgb, Span<byte> block );

	/// <summary>Whether a picture is HDR light, which compiles to BC6H or RGB9E5.</summary>
	public static bool IsHdr( string path )
		=> path is not null && ( path.EndsWith( ".exr", StringComparison.OrdinalIgnoreCase )
			|| path.EndsWith( ".hdr", StringComparison.OrdinalIgnoreCase ) );

	//Colour space, compression and a cut do not apply: the file is linear light whatever the material reads it as
	private static bool CompileHdr( string source, string output, bool generateMips, bool compress )
	{
		PictureSettings? settings = null;

		try
		{
			settings = SettingsFor?.Invoke( source );
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"{source}: its settings could not be read ({e.Message})" );
		}

		if ( settings is { NoMips: true } ) generateMips = false;
		if ( settings is { NoCompress: true } ) compress = false;

		byte[] encoded;

		try
		{
			if ( !File.Exists( source ) ) return false;

			encoded = SealedFile.ReadAllBytes( source );
		}
		catch ( IOException )
		{
			return false;
		}
		catch ( UnauthorizedAccessException )
		{
			return false;
		}

		var exr = source.EndsWith( ".exr", StringComparison.OrdinalIgnoreCase );

		if ( exr && DecodeExr is null )
		{
			Log.Warning( $"{source}: only the editor reads .exr - a build carries it compiled" );
			return false;
		}

		var decoded = exr ? DecodeExr( encoded, source ) : DecodeRadiance( encoded );
		if ( decoded is not { } top ) return false;

		var scale = settings is { } bright ? MathF.Max( bright.Brightness, 0.0f ) : 1.0f;

		var levels = new List<(float[] Rgba, int Width, int Height)> { (top.Rgba, top.Width, top.Height) };

		while ( generateMips && ( levels[^1].Width > 1 || levels[^1].Height > 1 ) )
			levels.Add( HalveFloat( levels[^1] ) );

		var blocks = compress && EncodeBc6h is not null;

		Span<int> sizes = levels.Count <= 32 ? stackalloc int[levels.Count] : new int[levels.Count];
		var total = 0;

		for ( var i = 0; i < levels.Count; ++i )
		{
			var (_, width, height) = levels[i];

			sizes[i] = blocks ? ( ( width + 3 ) / 4 ) * ( ( height + 3 ) / 4 ) * CoreBc6h.BlockBytes : width * height * 4;
			total += sizes[i];
		}

		var pixels = new byte[total];
		var at = 0;

		for ( var i = 0; i < levels.Count; ++i )
		{
			var (rgba, width, height) = levels[i];
			var level = pixels.AsMemory( at, sizes[i] );

			if ( blocks ) PackBlocks( rgba, width, height, scale, level );
			else PackLevel( rgba, width * height, scale, level.Span );

			at += sizes[i];
		}

		var file = FileBytes( top.Width, top.Height, blocks ? TextureFormat.Bc6h : TextureFormat.Rgb9e5, srgb: false, sizes, pixels );
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

	//A level as RGB9E5 words
	private static void PackLevel( float[] rgba, int count, float scale, Span<byte> into )
	{
		var packed = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>( into );

		for ( var i = 0; i < count; ++i )
			packed[i] = PackShared( rgba[i * 4] * scale, rgba[( i * 4 ) + 1] * scale, rgba[( i * 4 ) + 2] * scale );
	}

	//A level as BC6H blocks, a row of blocks to a worker; an edge block repeats the last row and column
	private static void PackBlocks( float[] rgba, int width, int height, float scale, Memory<byte> into )
	{
		var across = ( width + 3 ) / 4;
		var down = ( height + 3 ) / 4;
		var encode = EncodeBc6h;

		System.Threading.Tasks.Parallel.For( 0, down, row =>
		{
			Span<ushort> texels = stackalloc ushort[48];

			for ( var column = 0; column < across; column++ )
			{
				for ( var t = 0; t < 16; t++ )
				{
					var x = Math.Min( ( column * 4 ) + ( t & 3 ), width - 1 );
					var y = Math.Min( ( row * 4 ) + ( t >> 2 ), height - 1 );
					var at = ( ( y * width ) + x ) * 4;

					for ( var c = 0; c < 3; c++ )
					{
						var value = rgba[at + c] * scale;
						value = float.IsFinite( value ) ? Math.Clamp( value, 0.0f, HalfMax ) : 0.0f;

						texels[( t * 3 ) + c] = BitConverter.HalfToUInt16Bits( (Half)value );
					}
				}

				encode( texels, into.Span.Slice( ( ( row * across ) + column ) * CoreBc6h.BlockBytes, CoreBc6h.BlockBytes ) );
			}
		} );
	}

	/// <summary>One level of an RGB9E5 or BC6H file as RGB floats, rows from the top; null for anything else.</summary>
	public float[] LightLevel( int mip )
	{
		var level = MipInfo( mip );
		if ( level.Width <= 0 || level.Height <= 0 ) return null;

		if ( Format == TextureFormat.Bc6h ) return LightBlocks( Mip( mip ), level.Width, level.Height );
		if ( Format != TextureFormat.Rgb9e5 ) return null;

		var texels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>( Mip( mip ) );
		var count = level.Width * level.Height;
		if ( texels.Length < count ) return null;

		var rgb = new float[count * 3];

		for ( var i = 0; i < count; ++i )
		{
			var word = texels[i];
			var scale = MathF.ScaleB( 1.0f, (int)( word >> 27 ) - SharedBias - SharedBits );

			rgb[i * 3] = ( word & 511 ) * scale;
			rgb[( i * 3 ) + 1] = ( ( word >> 9 ) & 511 ) * scale;
			rgb[( i * 3 ) + 2] = ( ( word >> 18 ) & 511 ) * scale;
		}

		return rgb;
	}

	/// <summary>A level of BC6H blocks as RGB floats, rows from the top; null where bytes are short.</summary>
	public static float[] LightBlocks( ReadOnlySpan<byte> blocks, int width, int height )
	{
		var across = ( width + 3 ) / 4;
		var down = ( height + 3 ) / 4;
		if ( blocks.Length < across * down * CoreBc6h.BlockBytes ) return null;

		var rgb = new float[width * height * 3];
		Span<float> block = stackalloc float[48];

		for ( var row = 0; row < down; row++ )
		{
			for ( var column = 0; column < across; column++ )
			{
				CoreBc6h.Decode( blocks.Slice( ( ( row * across ) + column ) * CoreBc6h.BlockBytes ), block );

				for ( var t = 0; t < 16; t++ )
				{
					var x = ( column * 4 ) + ( t & 3 );
					var y = ( row * 4 ) + ( t >> 2 );
					if ( x >= width || y >= height ) continue;

					block.Slice( t * 3, 3 ).CopyTo( rgb.AsSpan( ( ( y * width ) + x ) * 3 ) );
				}
			}
		}

		return rgb;
	}

	/// <summary>An RGB9E5 texture from RGB levels in memory, widest first, each half the one before.</summary>
	public static CoreTextureFile FromLight( IReadOnlyList<(float[] Rgb, int Width, int Height)> levels )
	{
		if ( levels is null || levels.Count == 0 ) return null;

		Span<int> sizes = levels.Count <= 32 ? stackalloc int[levels.Count] : new int[levels.Count];
		var total = 0;

		for ( var i = 0; i < levels.Count; i++ )
		{
			sizes[i] = levels[i].Width * levels[i].Height * 4;
			total += sizes[i];
		}

		var pixels = new byte[total];
		var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>( pixels.AsSpan() );
		var at = 0;

		foreach ( var (rgb, width, height) in levels )
		{
			PackShared( rgb, words.Slice( at, width * height ) );
			at += width * height;
		}

		return FromBytes( FileBytes( levels[0].Width, levels[0].Height, TextureFormat.Rgb9e5, srgb: false, sizes, pixels ) );
	}

	/// <summary>RGB floats as RGB9E5 words, for a device that takes no BC - see BrowserTextures.</summary>
	public static void PackShared( ReadOnlySpan<float> rgb, Span<uint> into )
	{
		for ( var i = 0; i < into.Length; i++ ) into[i] = PackShared( rgb[i * 3], rgb[( i * 3 ) + 1], rgb[( i * 3 ) + 2] );
	}

	//EXT_texture_shared_exponent's encoding; a NaN or a negative from the file reads as nothing
	private static uint PackShared( float r, float g, float b )
	{
		r = float.IsFinite( r ) ? Math.Clamp( r, 0.0f, SharedMax ) : 0.0f;
		g = float.IsFinite( g ) ? Math.Clamp( g, 0.0f, SharedMax ) : 0.0f;
		b = float.IsFinite( b ) ? Math.Clamp( b, 0.0f, SharedMax ) : 0.0f;

		var most = MathF.Max( r, MathF.Max( g, b ) );
		if ( most <= 0.0f ) return 0;

		var exponent = Math.Max( -SharedBias - 1, MathF.ILogB( most ) ) + 1 + SharedBias;

		//Rounding can carry the largest channel over nine bits, and then the exponent goes up one
		if ( (int)MathF.Floor( ( most / MathF.ScaleB( 1.0f, exponent - SharedBias - SharedBits ) ) + 0.5f ) == 1 << SharedBits )
			exponent++;

		var step = MathF.ScaleB( 1.0f, exponent - SharedBias - SharedBits );

		var rs = (uint)Math.Min( 511, (int)MathF.Floor( ( r / step ) + 0.5f ) );
		var gs = (uint)Math.Min( 511, (int)MathF.Floor( ( g / step ) + 0.5f ) );
		var bs = (uint)Math.Min( 511, (int)MathF.Floor( ( b / step ) + 0.5f ) );

		return rs | ( gs << 9 ) | ( bs << 18 ) | ( (uint)exponent << 27 );
	}

	//A Radiance .hdr through stb_image, which every build of rintencore carries
	private static unsafe (float[] Rgba, int Width, int Height)? DecodeRadiance( byte[] encoded )
	{
		if ( encoded is null || encoded.Length == 0 ) return null;

		int w, h, channels;

		fixed ( byte* bytes = encoded )
		{
			var decoded = StbImage.stbi_loadf_from_memory( bytes, encoded.Length, &w, &h, &channels, 4 );

			if ( decoded == null ) return null;

			if ( w <= 0 || h <= 0 )
			{
				StbImage.stbi_image_free( decoded );
				return null;
			}

			var rgba = new ReadOnlySpan<float>( decoded, w * h * 4 ).ToArray();
			StbImage.stbi_image_free( decoded );

			return (rgba, w, h);
		}
	}

	//Halve's box filter, in floats
	private static (float[] Rgba, int Width, int Height) HalveFloat( (float[] Rgba, int Width, int Height) from )
	{
		var width = from.Width > 1 ? from.Width / 2 : 1;
		var height = from.Height > 1 ? from.Height / 2 : 1;
		var to = new float[width * height * 4];
		var source = from.Rgba;

		for ( var y = 0; y < height; ++y )
		{
			var y0 = y * 2;
			var y1 = ( y0 + 1 < from.Height ) ? y0 + 1 : y0;

			for ( var x = 0; x < width; ++x )
			{
				var x0 = x * 2;
				var x1 = ( x0 + 1 < from.Width ) ? x0 + 1 : x0;

				for ( var c = 0; c < 4; ++c )
				{
					var sum =
						source[( ( ( y0 * from.Width ) + x0 ) * 4 ) + c] +
						source[( ( ( y0 * from.Width ) + x1 ) * 4 ) + c] +
						source[( ( ( y1 * from.Width ) + x0 ) * 4 ) + c] +
						source[( ( ( y1 * from.Width ) + x1 ) * 4 ) + c];

					to[( ( ( y * width ) + x ) * 4 ) + c] = sum * 0.25f;
				}
			}
		}

		return (to, width, height);
	}
}
