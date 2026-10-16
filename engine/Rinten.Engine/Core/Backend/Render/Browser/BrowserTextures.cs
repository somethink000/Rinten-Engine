using System;
using System.Collections.Generic;

namespace Rinten;

//Browser textures and materials (bind groups of four), sharing one handle table
internal static unsafe class BrowserTextures
{
	private struct GpuTexture
	{
		public int Image;
		public int View;
		public int Set;
		public int Params;

		/// <summary>Which view the material group samples, which is not always the one it owns.</summary>
		public int BindView;

		public bool OwnsImage;
		public int Width;
		public int Height;
		public int MipCount;
		public long Bytes;

		/// <summary>Bytes per 4x4 block where held in blocks; nought where held as pixels.</summary>
		public int BlockBytes;

		/// <summary>Bytes a pixel where held as pixels: four, or eight for half floats.</summary>
		public int TexelBytes;

		/// <summary>The block format a picture held as pixels came in, for later writes - see WriteLevel.</summary>
		public CoreBlockCompression.Kind? Unpacked;

		/// <summary>A material's four pictures, so a re-levelled one can be rebound.</summary>
		public ulong[] Parts;
		public int[] SetViews;
	}

	//One level of a picture, where it starts in the blob and how big it is.
	private struct MipSlice
	{
		public int Width;
		public int Height;
		public int Offset;
		public int ByteCount;
	}

	private static readonly Dictionary<ulong, GpuTexture> textures = new();
	private static ulong nextHandle = 1;
	private static int whiteView;
	private static int whiteImage;
	private static ulong plain;

	//The group a draw binds to say which pictures it is using: four views, the sampler, the material's
	//own numbers, and the push ring it reads its per-draw constants out of at a dynamic offset.
	private static int WriteMaterialSet( ReadOnlySpan<int> views, int parameters )
	{
		if ( BrowserDevice.pushRing == 0 ) return 0;

		return BrowserDevice.MakeBindGroup( BrowserDevice.textureSetLayout,
			BrowserDevice.Bind.View( 0, views[0] ),
			BrowserDevice.Bind.View( 1, views[1] ),
			BrowserDevice.Bind.View( 2, views[2] ),
			BrowserDevice.Bind.View( 3, views[3] ),
			BrowserDevice.Bind.Buffer( 4, parameters, BrowserDevice.MaterialParamSize ),
			BrowserDevice.Bind.Sampler( BrowserDevice.MaterialSamplerBinding, BrowserDevice.sampler ),
			BrowserDevice.Bind.Buffer( BrowserDevice.PushBinding, BrowserDevice.pushRing, BrowserDevice.PushConstantSize ) );
	}

	//The white pixel as a texture a draw can name - for a draw that names none.
	internal static ulong Plain()
	{
		if ( plain == 0 && EnsureWhite() ) plain = Adopt( whiteView );
		return plain;
	}

	//One white pixel, which stands in for every texture a material did not name.
	private static bool EnsureWhite()
	{
		if ( whiteView != 0 ) return true;

		Span<byte> pixel = stackalloc byte[4] { 255, 255, 255, 255 };
		whiteImage = BrowserDevice.MakeTexture( 1, 1, BrowserDevice.FormatRgba8,
			BrowserDevice.TextureUse.TextureBinding | BrowserDevice.TextureUse.CopyDst );
		if ( whiteImage == 0 ) return false;

		whiteView = BrowserDevice.MakeView( whiteImage, BrowserDevice.FormatRgba8 );
		BrowserNative.WriteTexture( whiteImage, 0, 0, 0, 1, 1, 256, pixel );
		return whiteView != 0;
	}

	//A picture out of levels lying back to back.
	private static ulong CreateFromPixels( byte* pixels, ReadOnlySpan<MipSlice> mips, long byteCount, string format, int blockBytes = 0, int texelBytes = 4 )
	{
		if ( mips.Length == 0 ) return 0;

		var width = mips[0].Width;
		var height = mips[0].Height;

		if ( width > BrowserDevice.maxImageSize || height > BrowserDevice.maxImageSize )
		{
			BrowserDevice.infoText = "that texture is larger than the device allows";
			return 0;
		}

		var image = BrowserDevice.MakeTexture( width, height, format,
			BrowserDevice.TextureUse.TextureBinding | BrowserDevice.TextureUse.CopyDst |
			BrowserDevice.TextureUse.CopySrc, mips.Length );

		if ( image == 0 )
		{
			BrowserDevice.infoText = "the texture would not be made";
			return 0;
		}

		for ( var i = 0; i < mips.Length; i++ )
		{
			//A block format is written a whole block at a time, so a level is as wide as its blocks - a
			//level of two by two is one block, four by four - and a row of the upload is a row of blocks.
			var (w, h, row) = blockBytes == 0
				? (mips[i].Width, mips[i].Height, mips[i].Width * texelBytes)
				: (( mips[i].Width + 3 ) & ~3, ( mips[i].Height + 3 ) & ~3, ( ( mips[i].Width + 3 ) / 4 ) * blockBytes);

			BrowserNative.WriteTexture( image, i, 0, 0, w, h, row,
				new Span<byte>( pixels + mips[i].Offset, mips[i].ByteCount ) );
		}

		var made = new GpuTexture
		{
			Image = image,
			OwnsImage = true,
			Width = width,
			Height = height,
			MipCount = mips.Length,
			Bytes = byteCount,
			BlockBytes = blockBytes,
			TexelBytes = texelBytes,
		};

		made.View = BrowserDevice.MakeView( image, format );
		made.BindView = made.View;

		Span<int> views = [made.View, made.View, made.View, made.View];
		made.Set = WriteMaterialSet( views, BrowserDevice.defaultParams );

		if ( made.View == 0 || made.Set == 0 )
		{
			BrowserDevice.infoText = "the texture's bind group would not be made";
			if ( made.Set != 0 ) BrowserNative.Release( made.Set );
			if ( made.View != 0 ) BrowserNative.Release( made.View );
			BrowserNative.Release( image );
			return 0;
		}

		BrowserDevice.trackedTextureBytes += made.Bytes;
		BrowserDevice.trackedTextureCount += 1;

		var handle = nextHandle++;
		textures[handle] = made;
		return handle;
	}

	//What the page's transcoder turns UASTC into (transcoder_texture_format) - see basis_worker.js
	private const int TranscodeEtc1Rgb = 0;
	private const int TranscodeEtc2Rgba = 1;
	private const int TranscodeBc7 = 6;
	private const int TranscodeAstc4x4 = 10;
	private const int TranscodeRgba32 = 13;

	//UASTC KTX2 texture, transcoded by the page to the best supported format; empty until it lands
	internal static ulong CreateUniversal( CoreUniversalTexture picture )
	{
		if ( !BrowserDevice.Ready || picture is null ) return 0;

		var width = picture.Width;
		var height = picture.Height;

		if ( width > BrowserDevice.maxImageSize || height > BrowserDevice.maxImageSize )
		{
			BrowserDevice.infoText = "that texture is larger than the device allows";
			return 0;
		}

		var srgb = picture.IsSrgb ? "-srgb" : "";
		var whole = width % 4 == 0 && height % 4 == 0;

		//A block format is made only in whole blocks - see CreateMips.
		var (format, target, blockBytes) =
			whole && BrowserDevice.BlockCompression ? ("bc7-rgba-unorm" + srgb, TranscodeBc7, 16)
			: whole && BrowserDevice.AstcCompression ? ("astc-4x4-unorm" + srgb, TranscodeAstc4x4, 16)
			: whole && BrowserDevice.Etc2Compression && picture.HasAlpha ? ("etc2-rgba8unorm" + srgb, TranscodeEtc2Rgba, 16)
			: whole && BrowserDevice.Etc2Compression ? ("etc2-rgb8unorm" + srgb, TranscodeEtc1Rgb, 8)
			: ( picture.IsSrgb ? BrowserDevice.FormatRgba8Srgb : BrowserDevice.FormatRgba8, TranscodeRgba32, 0 );

		var levels = picture.Levels;

		var image = BrowserDevice.MakeTexture( width, height, format,
			BrowserDevice.TextureUse.TextureBinding | BrowserDevice.TextureUse.CopyDst |
			BrowserDevice.TextureUse.CopySrc, levels );

		if ( image == 0 )
		{
			BrowserDevice.infoText = "the texture would not be made";
			return 0;
		}

		long bytes = 0;

		for ( var i = 0; i < levels; i++ )
		{
			var w = Math.Max( 1, width >> i );
			var h = Math.Max( 1, height >> i );
			bytes += blockBytes == 0 ? (long)w * h * 4 : (long)( ( w + 3 ) / 4 ) * ( ( h + 3 ) / 4 ) * blockBytes;
		}

		var made = new GpuTexture
		{
			Image = image,
			OwnsImage = true,
			Width = width,
			Height = height,
			MipCount = levels,
			Bytes = bytes,
			BlockBytes = blockBytes,
		};

		made.View = BrowserDevice.MakeView( image, format );
		made.BindView = made.View;

		Span<int> views = [made.View, made.View, made.View, made.View];
		made.Set = WriteMaterialSet( views, BrowserDevice.defaultParams );

		if ( made.View == 0 || made.Set == 0 )
		{
			BrowserDevice.infoText = "the texture's bind group would not be made";
			if ( made.Set != 0 ) BrowserNative.Release( made.Set );
			if ( made.View != 0 ) BrowserNative.Release( made.View );
			BrowserNative.Release( image );
			return 0;
		}

		//The page copies the blocks before this returns and writes each level once its worker transcodes them
		BrowserNative.TranscodeInto( image, picture.Bytes, target, blockBytes );

		BrowserDevice.trackedTextureBytes += made.Bytes;
		BrowserDevice.trackedTextureCount += 1;

		var handle = nextHandle++;
		textures[handle] = made;
		return handle;
	}

	private static void DestroyOne( ref GpuTexture texture )
	{
		if ( texture.Set != 0 ) BrowserNative.Release( texture.Set );
		if ( texture.Params != 0 ) BrowserNative.Release( texture.Params );

		if ( !texture.OwnsImage ) return;

		if ( texture.View != 0 ) BrowserNative.Release( texture.View );
		if ( texture.Image != 0 ) BrowserNative.Release( texture.Image );
		BrowserDevice.trackedTextureBytes -= texture.Bytes;
		BrowserDevice.trackedTextureCount -= 1;
	}

	internal static int SetGet( ulong handle )
		=> textures.TryGetValue( handle, out var found ) ? found.Set : 0;

	internal static int ViewGet( ulong handle )
	{
		if ( !textures.TryGetValue( handle, out var found ) ) return 0;
		return found.BindView != 0 ? found.BindView : found.View;
	}

	//There is no bindless path on this backend, so every texture is slot nought.
	internal static uint IndexGet( ulong handle ) => 0;

	//Gives a view the renderer owns (a render target's) a handle, so a later pass can name it as a texture
	internal static ulong Adopt( int view, bool depth = false )
	{
		if ( view == 0 || !EnsureWhite() ) return 0;

		//Depth32Float is UnfilterableFloat|Depth, not Float. Group 0 is Float.
		var sampled = depth ? whiteView : view;

		var made = new GpuTexture { OwnsImage = false, View = view, BindView = sampled };

		Span<int> views = [sampled, sampled, sampled, sampled];
		made.Set = WriteMaterialSet( views, BrowserDevice.defaultParams );
		if ( made.Set == 0 )
		{
			DestroyOne( ref made );
			return 0;
		}

		var handle = nextHandle++;
		textures[handle] = made;
		return handle;
	}

	internal static void Release( ulong handle )
	{
		if ( !textures.TryGetValue( handle, out var found ) ) return;

		DestroyOne( ref found );
		textures.Remove( handle );
	}

	internal static void DestroyTextures()
	{
		foreach ( var pair in textures )
		{
			var one = pair.Value;
			DestroyOne( ref one );
		}

		textures.Clear();

		if ( whiteView != 0 ) BrowserNative.Release( whiteView );
		if ( whiteImage != 0 ) BrowserNative.Release( whiteImage );
		whiteView = 0;
		whiteImage = 0;
		plain = 0;
	}

	//Prebuilt mip chain upload; block formats unpacked to RGBA8 if unsupported, HDR light as it is
	internal static ulong CreateMips( int width, int height, int format, int srgb,
		int mipCount, int* mipInfo, byte* pixelData, int byteCount )
	{
		if ( !BrowserDevice.Ready )
		{
			BrowserDevice.infoText = "there is no device to put a texture on";
			return 0;
		}

		if ( width <= 0 || height <= 0 || mipCount <= 0 || mipInfo is null || pixelData is null || byteCount <= 0 )
		{
			BrowserDevice.infoText = "that texture asset is not loaded";
			return 0;
		}

		var stored = (CoreTextureFile.TextureFormat)format;

		if ( !Enum.IsDefined( stored ) && format != HalfFormat )
		{
			BrowserDevice.infoText = "the browser has no such picture format";
			return 0;
		}

		Span<MipSlice> mips = mipCount <= 16 ? stackalloc MipSlice[mipCount] : new MipSlice[mipCount];
		var taken = 0;
		var at = 0;

		for ( var mip = 0; mip < mipCount; mip++ )
		{
			var info = mipInfo + ( mip * 3 );
			if ( info[0] <= 0 || info[1] <= 0 || info[2] <= 0 ) break;
			if ( at + info[2] > byteCount ) break;

			mips[taken++] = new MipSlice
			{
				Width = info[0],
				Height = info[1],
				Offset = at,
				ByteCount = info[2],
			};

			at += info[2];
		}

		mips = mips[..taken];

		var rgba = srgb != 0 ? BrowserDevice.FormatRgba8Srgb : BrowserDevice.FormatRgba8;

		if ( stored == CoreTextureFile.TextureFormat.Rgba8 )
			return CreateFromPixels( pixelData, mips, at, rgba );

		//Half floats, the bake's light - see CoreRenderDevice.CreateTextureHalf
		if ( format == HalfFormat )
			return CreateFromPixels( pixelData, mips, at, BrowserDevice.FormatRgba16Float, texelBytes: 8 );

		if ( stored == CoreTextureFile.TextureFormat.Rgb9e5 )
			return CreateFromPixels( pixelData, mips, at, BrowserDevice.FormatRgb9e5 );

		if ( stored == CoreTextureFile.TextureFormat.Bc6h )
		{
			if ( BrowserDevice.BlockCompression && width % 4 == 0 && height % 4 == 0 )
				return CreateFromPixels( pixelData, mips, at, "bc6h-rgb-ufloat", CoreBc6h.BlockBytes );

			return CreateLightUnpacked( pixelData, mips );
		}

		var kind = stored switch
		{
			CoreTextureFile.TextureFormat.Bc1 => CoreBlockCompression.Kind.Bc1,
			CoreTextureFile.TextureFormat.Bc3 => CoreBlockCompression.Kind.Bc3,
			CoreTextureFile.TextureFormat.Bc4 => CoreBlockCompression.Kind.Bc4,
			CoreTextureFile.TextureFormat.Bc5 => CoreBlockCompression.Kind.Bc5,
			_ => CoreBlockCompression.Kind.Bc7,
		};

		//WebGPU makes block pictures only in whole blocks; other sizes are unpacked as on a device without blocks
		if ( BrowserDevice.BlockCompression && width % 4 == 0 && height % 4 == 0 )
		{
			var suffix = srgb != 0 ? "-srgb" : "";
			var name = kind switch
			{
				CoreBlockCompression.Kind.Bc1 => "bc1-rgba-unorm" + suffix,
				CoreBlockCompression.Kind.Bc3 => "bc3-rgba-unorm" + suffix,
				CoreBlockCompression.Kind.Bc4 => "bc4-r-unorm",
				CoreBlockCompression.Kind.Bc5 => "bc5-rg-unorm",
				_ => "bc7-rgba-unorm" + suffix,
			};

			return CreateFromPixels( pixelData, mips, at, name, CoreBlockCompression.BlockSize( kind ) );
		}

		var unpacked = CreateUnpacked( kind, pixelData, mips, rgba );

		if ( unpacked != 0 && textures.TryGetValue( unpacked, out var held ) )
		{
			held.Unpacked = kind;
			textures[unpacked] = held;
		}

		return unpacked;
	}

	//BC6H levels on a device without BC: decoded and packed again as RGB9E5, which every one samples
	private static ulong CreateLightUnpacked( byte* pixelData, ReadOnlySpan<MipSlice> mips )
	{
		var levels = new MipSlice[mips.Length];
		var total = 0;

		for ( var i = 0; i < mips.Length; i++ )
		{
			levels[i] = mips[i];
			levels[i].Offset = total;
			levels[i].ByteCount = mips[i].Width * mips[i].Height * 4;
			total += levels[i].ByteCount;
		}

		var words = new uint[total / 4];

		for ( var i = 0; i < mips.Length; i++ )
		{
			var light = CoreTextureFile.LightBlocks( new ReadOnlySpan<byte>( pixelData + mips[i].Offset, mips[i].ByteCount ), mips[i].Width, mips[i].Height );
			if ( light is null ) return 0;

			CoreTextureFile.PackShared( light, words.AsSpan( levels[i].Offset / 4, mips[i].Width * mips[i].Height ) );
		}

		fixed ( uint* unpacked = words )
			return CreateFromPixels( (byte*)unpacked, levels, total, BrowserDevice.FormatRgb9e5 );
	}

	//A texture of nothing, every level there - for filling in pieces with WriteLevel. See VkTextures.CreateBlank.
	internal static ulong CreateBlank( int width, int height, int format, int srgb, int mipCount )
	{
		if ( width <= 0 || height <= 0 || mipCount <= 0 || mipCount > 32 ) return 0;

		var stored = (CoreTextureFile.TextureFormat)format;
		var blockBytes = stored switch
		{
			CoreTextureFile.TextureFormat.Rgba8 => 0,
			CoreTextureFile.TextureFormat.Bc1 or CoreTextureFile.TextureFormat.Bc4 => 8,
			_ => 16,
		};

		Span<int> info = stackalloc int[mipCount * 3];
		var total = 0;

		for ( var mip = 0; mip < mipCount; mip++ )
		{
			var w = Math.Max( 1, width >> mip );
			var h = Math.Max( 1, height >> mip );
			var bytes = blockBytes > 0 ? ( ( w + 3 ) / 4 ) * ( ( h + 3 ) / 4 ) * blockBytes : w * h * 4;

			info[mip * 3] = w;
			info[( mip * 3 ) + 1] = h;
			info[( mip * 3 ) + 2] = bytes;
			total += bytes;
		}

		var nothing = new byte[total];

		fixed ( int* mipInfo = info )
		fixed ( byte* pixels = nothing )
			return CreateMips( width, height, format, srgb, mipCount, mipInfo, pixels, total );
	}

	//A rectangle of one level, in the texture's source format - see VkTextures.WriteLevel
	//Blocks into a picture held as pixels are unpacked on the way
	internal static int WriteLevel( ulong texture, int level, int x, int y, int width, int height, byte* data, int stride )
	{
		if ( !BrowserDevice.Ready || data is null || width <= 0 || height <= 0 || x < 0 || y < 0 || level < 0 ) return 0;
		if ( !textures.TryGetValue( texture, out var target ) ) return 0;
		if ( !target.OwnsImage || target.Image == 0 || level >= target.MipCount ) return 0;

		var levelWidth = Math.Max( 1, target.Width >> level );
		var levelHeight = Math.Max( 1, target.Height >> level );
		if ( x + width > levelWidth || y + height > levelHeight ) return 0;

		var rows = ( height + 3 ) / 4;

		if ( target.BlockBytes > 0 )
		{
			if ( ( x & 3 ) != 0 || ( y & 3 ) != 0 ) return 0;

			//Whole blocks, as the device takes them - see CreateFromPixels.
			var rowBytes = ( ( width + 3 ) / 4 ) * target.BlockBytes;
			BrowserNative.WriteTexture( target.Image, level, x, y, ( width + 3 ) & ~3, ( height + 3 ) & ~3, stride,
				new Span<byte>( data, ( ( rows - 1 ) * stride ) + rowBytes ) );
			return 1;
		}

		if ( target.Unpacked is { } kind )
		{
			//Blocks the device cannot read: unpacked here, packed tight first.
			var blockBytes = CoreBlockCompression.BlockSize( kind );
			var rowBytes = ( ( width + 3 ) / 4 ) * blockBytes;
			var packed = new byte[rows * rowBytes];

			for ( var row = 0; row < rows; row++ )
				new ReadOnlySpan<byte>( data + ( row * stride ), rowBytes ).CopyTo( packed.AsSpan( row * rowBytes ) );

			var pixels = CoreBlockCompression.Decode( kind, packed, width, height );
			if ( pixels is null ) return 0;

			BrowserNative.WriteTexture( target.Image, level, x, y, width, height, width * 4, pixels );
			return 1;
		}

		BrowserNative.WriteTexture( target.Image, level, x, y, width, height, stride,
			new Span<byte>( data, ( ( height - 1 ) * stride ) + ( width * Math.Max( target.TexelBytes, 4 ) ) ) );
		return 1;
	}

	//The device's own half float format number - VkTextures.HalfFormat, which this side must take the same
	private const int HalfFormat = 6;

	/// <summary>A picture's view, for binding it outside a material; nought for none.</summary>
	internal static int ViewOf( ulong texture )
		=> textures.TryGetValue( texture, out var found ) ? found.BindView : 0;

	internal static int ImageOf( ulong texture )
		=> textures.TryGetValue( texture, out var found ) ? found.Image : 0;

	//Every level unpacked to RGBA8 and put up as one chain.
	private static ulong CreateUnpacked( CoreBlockCompression.Kind kind, byte* pixelData, ReadOnlySpan<MipSlice> mips, string format )
	{
		var levels = new byte[mips.Length][];
		var unpacked = new MipSlice[mips.Length];
		var total = 0;

		for ( var i = 0; i < mips.Length; i++ )
		{
			levels[i] = CoreBlockCompression.Decode( kind,
				new ReadOnlySpan<byte>( pixelData + mips[i].Offset, mips[i].ByteCount ), mips[i].Width, mips[i].Height );

			//A level that will not unpack ends the chain there: the levels above it are a whole picture on their own.
			if ( levels[i] is null )
			{
				if ( i == 0 )
				{
					BrowserDevice.infoText = "the texture's blocks would not unpack";
					return 0;
				}

				Array.Resize( ref unpacked, i );
				break;
			}

			unpacked[i] = new MipSlice { Width = mips[i].Width, Height = mips[i].Height, Offset = total, ByteCount = levels[i].Length };
			total += levels[i].Length;
		}

		var chain = new byte[total];

		for ( var i = 0; i < unpacked.Length; i++ )
			levels[i].CopyTo( chain, unpacked[i].Offset );

		fixed ( byte* pixels = chain )
			return CreateFromPixels( pixels, unpacked, total, format );
	}

	//One level of RGBA, which is what an image loaded in the page is.
	internal static ulong CreateRgba( byte* pixels, int width, int height, int srgb )
	{
		if ( !BrowserDevice.Ready )
		{
			BrowserDevice.infoText = "there is no device to put a texture on";
			return 0;
		}

		if ( pixels is null || width <= 0 || height <= 0 )
		{
			BrowserDevice.infoText = "a texture needs some pixels";
			return 0;
		}

		var byteCount = (long)width * height * 4;
		Span<MipSlice> mips = stackalloc MipSlice[1];
		mips[0] = new MipSlice { Width = width, Height = height, Offset = 0, ByteCount = (int)byteCount };
		return CreateFromPixels( pixels, mips, byteCount, srgb != 0 ? BrowserDevice.FormatRgba8Srgb : BrowserDevice.FormatRgba8 );
	}

	//Fills a texture that already exists with new pixels of exactly its shape. Only for one this backend made
	//itself, with one level and the same size: anything else is a different texture and the caller should make one.
	internal static int UpdateRgba( ulong texture, byte* pixels, int width, int height )
	{
		if ( !BrowserDevice.Ready || pixels is null ) return 0;
		if ( !textures.TryGetValue( texture, out var target ) ) return 0;
		if ( !target.OwnsImage || target.Image == 0 ) return 0;
		if ( target.MipCount != 1 ) return 0;
		if ( target.Width != width || target.Height != height ) return 0;

		var byteCount = width * height * 4;
		BrowserNative.WriteTexture( target.Image, 0, 0, 0, width, height, width * 4,
			new Span<byte>( pixels, byteCount ) );
		return 1;
	}

	internal static void Destroy( ulong texture ) => Release( texture );

	//The pictures a material was made of, colour, normal, third and emission - nought where it is none.
	internal static void MaterialParts( ulong material, Span<ulong> into )
	{
		into.Clear();
		if ( !textures.TryGetValue( material, out var found ) ) return;

		if ( found.Parts is null )
		{
			if ( found.OwnsImage && into.Length > 0 ) into[0] = material;
			return;
		}

		for ( var i = 0; i < found.Parts.Length && i < into.Length; i++ ) into[i] = found.Parts[i];
	}

	//How many levels a picture has on the device and what they cost, or false where it is none of ours.
	internal static bool Held( ulong texture, out int levels, out long bytes )
	{
		levels = 0;
		bytes = 0;

		if ( !textures.TryGetValue( texture, out var found ) || !found.OwnsImage ) return false;

		levels = found.MipCount;
		bytes = found.Bytes;
		return true;
	}

	//Whether a picture's levels are on the device: one once its transcode has written them, minus one where
	//it never will, nought while it is on its way - see basis.js. A picture that was not transcoded is there.
	internal static int Ready( ulong texture )
		=> textures.TryGetValue( texture, out var found ) && found.OwnsImage ? BrowserNative.TranscodeState( found.Image ) : 1;

	//Swaps textures for resized copies and rebuilds material groups; only between frames
	internal static int Swap( ReadOnlySpan<(ulong Target, ulong Source)> swaps )
	{
		var moved = new Dictionary<int, int>();
		var done = 0;

		foreach ( var (target, source) in swaps )
		{
			if ( !textures.TryGetValue( target, out var into ) || !into.OwnsImage ) continue;
			if ( !textures.TryGetValue( source, out var from ) || !from.OwnsImage ) continue;

			var old = into.BindView != 0 ? into.BindView : into.View;

			if ( into.Set != 0 ) BrowserNative.Release( into.Set );
			if ( into.View != 0 ) BrowserNative.Release( into.View );
			if ( into.Image != 0 ) BrowserNative.Release( into.Image );
			BrowserDevice.trackedTextureBytes -= into.Bytes;
			BrowserDevice.trackedTextureCount -= 1;

			into.Image = from.Image;
			into.View = from.View;
			into.BindView = from.BindView;
			into.Set = from.Set;
			into.Width = from.Width;
			into.Height = from.Height;
			into.MipCount = from.MipCount;
			into.Bytes = from.Bytes;
			into.BlockBytes = from.BlockBytes;
			into.TexelBytes = from.TexelBytes;
			into.Unpacked = from.Unpacked;

			textures[target] = into;
			textures.Remove( source );

			//Moved twice in one batch: whatever was moved onto the view that has just gone moves on again.
			foreach ( var key in new List<int>( moved.Keys ) )
				if ( moved[key] == old ) moved[key] = into.BindView;

			moved[old] = into.BindView;
			done++;
		}

		if ( moved.Count == 0 ) return 0;

		//Every material naming an old view: a new group naming the new one.
		Span<int> named4 = stackalloc int[BrowserDevice.MaterialTextures];

		foreach ( var key in new List<ulong>( textures.Keys ) )
		{
			var material = textures[key];
			if ( material.SetViews is null || material.Set == 0 || material.SetViews.Length > named4.Length ) continue;

			var named = false;
			var views = named4[..material.SetViews.Length];

			for ( var i = 0; i < views.Length; i++ )
			{
				views[i] = moved.TryGetValue( material.SetViews[i], out var now ) ? now : material.SetViews[i];
				named |= views[i] != material.SetViews[i];
			}

			if ( !named ) continue;

			var set = WriteMaterialSet( views, material.Params != 0 ? material.Params : BrowserDevice.defaultParams );
			if ( set == 0 ) continue;

			BrowserNative.Release( material.Set );
			material.Set = set;
			material.SetViews = views.ToArray();
			textures[key] = material;
		}

		return done;
	}

	//New numbers into materials already made. The queue writes them in order with what it is given
	//to draw, so a frame already handed over keeps the numbers it had and the next reads these.
	internal static void MaterialNumbers( ulong* materials, float* numbers, int count, byte* written )
	{
		for ( var i = 0; i < count; i++ )
		{
			written[i] = 0;

			if ( !textures.TryGetValue( materials[i], out var found ) || found.Parts is null || found.Params == 0 ) continue;

			BrowserNative.WriteBuffer( found.Params, 0,
				new Span<byte>( numbers + ( (long)i * CoreRenderDevice.MaterialParamFloats ), BrowserDevice.MaterialParamSize ) );

			written[i] = 1;
		}
	}

	//Four pictures and the numbers as one bind group; a missing picture falls back to the colour map, then white
	internal static ulong MaterialCreate( ulong colour, ulong normal,
		ulong third, ulong emission, float* parameters )
	{
		if ( !BrowserDevice.Ready )
		{
			BrowserDevice.infoText = "there is no device to make a material on";
			return 0;
		}

		if ( !EnsureWhite() ) return 0;

		Span<ulong> wanted = [colour, normal, third, emission];
		var fallback = ViewGet( colour );
		if ( fallback == 0 ) fallback = whiteView;

		Span<int> views = stackalloc int[BrowserDevice.MaterialTextures];
		for ( var i = 0; i < BrowserDevice.MaterialTextures; i++ )
		{
			views[i] = ViewGet( wanted[i] );
			if ( views[i] == 0 ) views[i] = fallback;
		}

		var made = new GpuTexture { OwnsImage = false, Parts = [colour, normal, third, emission], SetViews = views.ToArray() };

		if ( parameters is not null )
		{
			//Copied into as well as read: its numbers are written in place when they change - see MaterialNumbers.
			made.Params = BrowserDevice.MakeBuffer(
				new Span<byte>( parameters, BrowserDevice.MaterialParamSize ),
				BrowserDevice.BufferUse.Uniform | BrowserDevice.BufferUse.CopyDst );

			if ( made.Params == 0 )
			{
				BrowserDevice.infoText = "the material's parameters would not fit anywhere";
				return 0;
			}
		}

		made.Set = WriteMaterialSet( views, made.Params != 0 ? made.Params : BrowserDevice.defaultParams );

		if ( made.Set == 0 )
		{
			BrowserDevice.infoText = "the material would not be made";
			DestroyOne( ref made );
			return 0;
		}

		var handle = nextHandle++;
		textures[handle] = made;
		return handle;
	}
}
