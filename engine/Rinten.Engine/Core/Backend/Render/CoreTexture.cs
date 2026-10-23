using NativeEngine;

namespace Rinten;

//A texture whose pixels live in process memory and are copied to our device when drawn with
//Layout: mips from largest down, each holding its slices back to back, tightly packed
internal sealed class CoreTexture
{
	//What it is: size, format, and the flags it was asked for.
	internal CTextureDesc Desc;

	//What it is called, for logs and for the asset it came from.
	internal string Name;

	//Every mip, packed.
	internal byte[] Data;

	//Bumped by every write, so a copy of this texture on the device can tell whether it is out of date without comparing the pixels.
	internal int Version;

	private ulong device;
	private int deviceVersion = -1;

	//Rectangle of level 0 written since the last upload, so only the changed part is sent
	private int writtenLeft, writtenTop, writtenRight, writtenBottom;

	//Whether something since the last upload wrote more than a rectangle of the first level
	//- the whole chain, another level, a clear - so only a whole upload is a true copy.
	private bool wholeWritten = true;

	//Version the device refused and when; retried after RefusedFrames instead of every frame
	private int refusedVersion = -1;
	private long refusedAt;

	//The loop frame the UI renderer last drew with this - live walk or a replayed
	//panel alike. What the UI's generated pictures (gradient ramps) are aged by.
	internal long UiDrawnAt;

	//How long a refused picture is left alone before it is offered again.
	private const long RefusedFrames = 60;

	//The device picture belongs to someone else (e.g. a drawn viewport); nothing to copy or read back
	private bool borrowed;

	//How many times a borrowed texture has been pointed at a picture - each one drawn afresh.
	private int borrowedVersion;

	//Whether whoever made the picture wrote the levels past the first one themselves. Where they
	//did not, those levels are built from the first on the way to the device - see UploadChain.
	private bool levelsGiven;

	//Changes whenever the shown content changes; render targets count as changing every frame
	internal int Content =>
		borrowed ? borrowedVersion
		: ( ( Desc.m_nFlags & RuntimeTextureSpecificationFlags.TSPEC_RENDER_TARGET ) == RuntimeTextureSpecificationFlags.TSPEC_RENDER_TARGET ) ? unchecked( (int)EngineLoop.LoopFrames )
		: Version;

	internal int Width => Desc.m_nWidth;
	internal int Height => Desc.m_nHeight;
	internal int Depth => Desc.IsCube ? 6 : Desc.m_nDepth;
	internal int Mips => Math.Max( (int)Desc.m_nNumMipLevels, 1 );
	internal ImageFormat Format => Desc.m_nImageFormat;

	//Makes one, with room for every mip it says it has.
	internal static CoreTexture Create( CTextureDesc desc, string name )
	{
		desc.m_nWidth = Math.Max( desc.m_nWidth, (short)1 );
		desc.m_nHeight = Math.Max( desc.m_nHeight, (short)1 );
		desc.m_nDepth = Math.Max( desc.m_nDepth, (short)1 );
		desc.m_nNumMipLevels = Math.Max( desc.m_nNumMipLevels, (short)1 );

		if ( desc.m_nImageFormat == ImageFormat.Default )
			desc.m_nImageFormat = ImageFormat.RGBA8888;

		var texture = new CoreTexture { Desc = desc, Name = name };

		var size = ImageFormatSize.Total( desc.m_nWidth, desc.m_nHeight,
			texture.Depth, desc.m_nNumMipLevels, desc.m_nImageFormat );

		texture.Data = new byte[Math.Max( size, 1 )];

		Watch( texture );

		return texture;
	}

	//Makes one from what a builder was asked for. The first fields of a creation config are a description of
	//the picture and nothing else; the rest is about the device, which has no say in what the pixels are.
	internal static CoreTexture Create( TextureCreationConfig_t config, string name )
	{
		return Create( new CTextureDesc
		{
			m_nWidth = config.m_nWidth,
			m_nHeight = config.m_nHeight,
			m_nDepth = config.m_nDepth,
			m_nNumMipLevels = config.m_nNumMipLevels,
			m_nDecodeFlags = config.m_nDecodeFlags,
			m_nImageFormat = config.m_nImageFormat,
			m_nFlags = config.m_nFlags,
			m_nDisplayRectWidth = config.m_nDisplayRectWidth,
			m_nDisplayRectHeight = config.m_nDisplayRectHeight,
			m_nMotionVectorsMaxDistanceInPixels = config.m_nMotionVectorsMaxDistanceInPixels,
		}, name );
	}

	//How big a mip is, in pixels.
	internal (int Width, int Height, int Depth) MipSize( int mip )
	{
		var width = Width;
		var height = Height;
		var depth = Depth;

		for ( var level = 0; level < mip; level++ )
		{
			width = Math.Max( 1, width / 2 );
			height = Math.Max( 1, height / 2 );

			//Slices of an array or a cube stay; a volume texture's depth halves with everything else.
			if ( !Desc.IsCube && !Desc.IsArray ) depth = Math.Max( 1, depth / 2 );
		}

		return (width, height, depth);
	}

	//Where a mip starts in the data.
	internal int MipOffset( int mip )
	{
		var offset = 0;

		for ( var level = 0; level < mip; level++ )
		{
			var (width, height, depth) = MipSize( level );
			offset += ImageFormatSize.Level( width, height, depth, Format );
		}

		return offset;
	}

	//How many bytes one mip takes.
	internal int MipBytes( int mip )
	{
		var (width, height, depth) = MipSize( mip );
		return ImageFormatSize.Level( width, height, depth, Format );
	}

	//One mip's bytes, to read or to write.
	internal Span<byte> MipData( int mip )
	{
		if ( Data is null ) return default;

		var offset = MipOffset( mip );
		var length = MipBytes( mip );

		if ( offset < 0 || length <= 0 || offset + length > Data.Length ) return default;

		return Data.AsSpan( offset, length );
	}

	//Fills the whole texture with one colour - what a texture made without data starts as.
	internal void Clear( Color colour )
	{
		var value = new Vector4( colour.r, colour.g, colour.b, colour.a );
		var pixelBytes = CoreImage.PixelBytes( Format );

		if ( pixelBytes == 0 )
		{
			//Not a format we can write a colour into. Zeroed is what it is.
			Array.Clear( Data );
			wholeWritten = true;
			Version++;
			return;
		}

		//One pixel written and then repeated: the formats are small and the repeat is a memory copy rather than a conversion per pixel.
		Span<byte> pixel = stackalloc byte[pixelBytes];
		CoreImage.Write( pixel, Format, value );

		var data = Data.AsSpan();

		for ( var offset = 0; offset + pixelBytes <= data.Length; offset += pixelBytes )
			pixel.CopyTo( data[offset..] );

		wholeWritten = true;
		Version++;
	}

	//Writes bytes in the texture's own format. Either one mip's worth, or the whole chain.
	internal void Write( ReadOnlySpan<byte> data, int mip = 0 )
	{
		if ( Data is null || data.IsEmpty ) return;

		var whole = mip == 0 && data.Length == Data.Length;
		var target = whole ? Data.AsSpan() : MipData( mip );
		if ( target.IsEmpty ) return;

		data[..Math.Min( data.Length, target.Length )].CopyTo( target );

		//The smaller levels are the writer's own once they have been written.
		if ( mip > 0 || ( whole && Mips > 1 ) ) levelsGiven = true;

		wholeWritten = true;
		Version++;
	}

	//Writes a rectangle of pixels into one mip of one slice, in the texture's own format - what an update of part of a texture does.
	internal void WriteRegion( ReadOnlySpan<byte> data, int x, int y, int width, int height, int mip, int slice )
	{
		var pixelBytes = CoreImage.PixelBytes( Format );
		if ( pixelBytes == 0 ) return;

		if ( mip > 0 ) levelsGiven = true;

		var mipData = MipData( mip );
		if ( mipData.IsEmpty ) return;

		var (mipWidth, mipHeight, _) = MipSize( mip );

		var sliceBytes = ImageFormatSize.Level( mipWidth, mipHeight, 1, Format );
		var sliceStart = slice * sliceBytes;

		if ( sliceStart < 0 || sliceStart + sliceBytes > mipData.Length ) return;

		var target = mipData.Slice( sliceStart, sliceBytes );
		var sourceStride = width * pixelBytes;

		for ( var row = 0; row < height; row++ )
		{
			var destinationY = y + row;
			if ( destinationY < 0 || destinationY >= mipHeight ) continue;

			var source = data[(row * sourceStride)..];
			var destination = target[(((destinationY * mipWidth) + x) * pixelBytes)..];

			var count = Math.Min( sourceStride, Math.Min( source.Length, destination.Length ) );
			if ( count <= 0 ) continue;

			source[..count].CopyTo( destination );
		}

		if ( mip == 0 && slice == 0 ) Written( x, y, width, height );
		else wholeWritten = true;

		Version++;
	}

	//Says that a rectangle of the first level was written straight into Data by whoever holds it, so the device is given that and no more. What a writer
	//that fills the pixels itself calls instead of WriteRegion - a web page, whose pixels arrive in another order and are put right on the way in.
	internal void MarkWritten( int x, int y, int width, int height )
	{
		Written( x, y, width, height );
		Version++;
	}

	//Grows the written rectangle to take in another, clipped to the first level.
	private void Written( int x, int y, int width, int height )
	{
		var left = Math.Max( x, 0 );
		var top = Math.Max( y, 0 );
		var right = Math.Min( x + width, Width );
		var bottom = Math.Min( y + height, Height );

		if ( right <= left || bottom <= top ) return;

		if ( writtenRight <= writtenLeft || writtenBottom <= writtenTop )
		{
			(writtenLeft, writtenTop, writtenRight, writtenBottom) = (left, top, right, bottom);
			return;
		}

		writtenLeft = Math.Min( writtenLeft, left );
		writtenTop = Math.Min( writtenTop, top );
		writtenRight = Math.Max( writtenRight, right );
		writtenBottom = Math.Max( writtenBottom, bottom );
	}

	//Forgets what was written: the device has all of it now.
	private void Uploaded()
	{
		writtenLeft = writtenTop = writtenRight = writtenBottom = 0;
		wholeWritten = false;
	}

	//Uploads only the written rectangle (single-level RGBA8 only); false means send the whole picture
	private bool UploadWritten( int width, int height )
	{
		if ( wholeWritten || device == 0 || Format != ImageFormat.RGBA8888 || Mips != 1 || Depth != 1 ) return false;

		var regionWidth = writtenRight - writtenLeft;
		var regionHeight = writtenBottom - writtenTop;

		if ( regionWidth <= 0 || regionHeight <= 0 ) return false;

		//All of it anyway - the ordinary path is the same copy with fewer steps.
		if ( regionWidth == width && regionHeight == height ) return false;

		var level = MipData( 0 );
		if ( level.Length < width * height * 4 ) return false;

		var stride = width * 4;
		var start = ( writtenTop * stride ) + ( writtenLeft * 4 );

		if ( !CoreRenderDevice.UpdateTextureRgbaRegion( device, level[start..], stride,
			writtenLeft, writtenTop, regionWidth, regionHeight ) )
			return false;

		Uploads++;
		UploadedPixels += regionWidth * regionHeight;

		return true;
	}

	//Reads a rectangle out, converting to whatever format is asked for. The destination rectangle may be a different size to the source, in
	//which case it is sampled rather than scaled - nearest pixel, which is what a thumbnail of a texture wants and all their device did.
	internal bool Read( (int X, int Y, int Width, int Height) source, int slice, int mip,
		Span<byte> destination, ImageFormat destinationFormat,
		(int X, int Y, int Width, int Height) destinationRect, int destinationStride )
	{
		var sourcePixel = CoreImage.PixelBytes( Format );
		var destinationPixel = CoreImage.PixelBytes( destinationFormat );

		if ( sourcePixel == 0 || destinationPixel == 0 ) return false;

		var mipData = MipData( mip );
		if ( mipData.IsEmpty ) return false;

		var (mipWidth, mipHeight, _) = MipSize( mip );

		var sliceBytes = ImageFormatSize.Level( mipWidth, mipHeight, 1, Format );
		var sliceStart = slice * sliceBytes;

		if ( sliceStart < 0 || sliceStart + sliceBytes > mipData.Length ) return false;

		var pixels = mipData.Slice( sliceStart, sliceBytes );

		if ( source.Width <= 0 ) source.Width = mipWidth;
		if ( source.Height <= 0 ) source.Height = mipHeight;

		if ( destinationRect.Width <= 0 ) destinationRect.Width = source.Width;
		if ( destinationRect.Height <= 0 ) destinationRect.Height = source.Height;

		if ( destinationStride <= 0 ) destinationStride = destinationRect.Width * destinationPixel;

		for ( var y = 0; y < destinationRect.Height; y++ )
		{
			//Which row of the source this row of the destination came from - the same one where they are the same size.
			var sourceY = source.Y + (destinationRect.Height == source.Height
				? y
				: y * source.Height / destinationRect.Height);

			if ( sourceY < 0 || sourceY >= mipHeight ) continue;

			var destinationRow = (destinationRect.Y + y) * destinationStride;

			for ( var x = 0; x < destinationRect.Width; x++ )
			{
				var sourceX = source.X + (destinationRect.Width == source.Width
					? x
					: x * source.Width / destinationRect.Width);

				if ( sourceX < 0 || sourceX >= mipWidth ) continue;

				var from = ((sourceY * mipWidth) + sourceX) * sourcePixel;
				var to = destinationRow + ((destinationRect.X + x) * destinationPixel);

				if ( from + sourcePixel > pixels.Length ) continue;
				if ( to < 0 || to + destinationPixel > destination.Length ) continue;

				CoreImage.Write( destination[to..], destinationFormat,
					CoreImage.Read( pixels[from..], Format ) );
			}
		}

		return true;
	}

	//One mip as colours, which is what most callers want.
	internal Color32[] Pixels( int mip )
	{
		var (width, height, _) = MipSize( mip );

		var output = new Color32[width * height];

		if ( !CoreImage.Handles( Format ) ) return output;

		Read( (0, 0, width, height), 0, mip,
			System.Runtime.InteropServices.MemoryMarshal.AsBytes( output.AsSpan() ),
			ImageFormat.RGBA8888, (0, 0, width, height), width * 4 );

		return output;
	}

	[ThreadStatic] static byte[] converting;

	//The first level as RGBA8, in the thread's kept buffer - valid until the next call on this thread
	private ReadOnlySpan<byte> Converted( int width, int height )
	{
		var count = width * height * 4;
		if ( converting is null || converting.Length < count ) converting = new byte[System.Math.Max( count, ( converting?.Length ?? 0 ) * 2 )];

		var output = converting.AsSpan( 0, count );
		output.Clear();

		if ( CoreImage.Handles( Format ) )
			Read( (0, 0, width, height), 0, 0, output, ImageFormat.RGBA8888, (0, 0, width, height), width * 4 );

		return output;
	}

	//Uploads since last read and their pixel count; a count climbing with frame rate means slow UI
	internal static int Uploads;
	internal static long UploadedPixels;

	//Uploads that had to create a new device texture, which is far costlier than overwriting one
	internal static int Created;

	//A texture that stands for something already on the device, which nothing here owns or copies.
	internal static CoreTexture Borrowing( ulong handle, int width, int height )
	{
		var desc = new CTextureDesc
		{
			m_nWidth = (short)width,
			m_nHeight = (short)height,
			m_nDepth = 1,
			m_nNumMipLevels = 1,
			m_nImageFormat = ImageFormat.RGBA8888,
		};

		return new CoreTexture
		{
			Desc = desc,
			Name = "viewport",
			Data = Array.Empty<byte>(),
			device = handle,
			borrowed = true,
		};
	}

	//Wraps a device texture this one owns (e.g. loaded from compiled blocks); released with it
	internal static CoreTexture OnDevice( ulong handle, int width, int height, string name )
	{
		var texture = Borrowing( handle, width, height );

		texture.Name = name;
		texture.borrowed = false;
		texture.deviceVersion = texture.Version;

		return texture;
	}

	//Set by the creator when every pixel is opaque, for UI occlusion culling; never queried from device
	internal bool Opaque;

	//Points a borrowed texture at a different picture.
	internal void Borrow( ulong handle, int width, int height )
	{
		if ( !borrowed ) return;

		device = handle;
		borrowedVersion++;

		Desc.m_nWidth = (short)width;
		Desc.m_nHeight = (short)height;
	}

	//Logs once that a picture can't be uploaded, since failing textures retry every frame
	private static void Missing( string why )
	{
		if ( saidMissing ) return;

		saidMissing = true;

		Log.Warning( $"a picture could not be drawn: {why}. Anything drawn from one is missing." );
	}

	private static bool saidMissing;

	internal ulong Device()
	{
		if ( borrowed ) return device;

		if ( Data is null ) return 0;

		if ( deviceVersion == Version && device != 0 ) return device;

		//Already offered and already refused, and nothing about it has changed since - so neither would the answer. See refusedVersion.
		if ( refusedVersion == Version && EngineLoop.LoopFrames - refusedAt < RefusedFrames )
			return device;

		var (width, height, _) = MipSize( 0 );
		if ( width <= 0 || height <= 0 ) return 0;

		//Only a rectangle of it changed since the device's copy was made, and only that rectangle goes. See writtenLeft.
		if ( UploadWritten( width, height ) )
		{
			deviceVersion = Version;
			Uploaded();
			return device;
		}

		//Asked for with mips, it goes as the whole chain. What will not go that way - a
		//device with no path for it - goes as one level, below, as everything used to.
		if ( Chained && UploadChain( width, height ) is var chained && chained != 0 ) return chained;

		//RGBA8 goes as the bytes it already is. Converting a format to itself through Pixels() walks every pixel through a read
		//and a write, and on an interface repainting a window's worth of widgets that walk cost more than the upload it fed.
		//Any other format is turned into RGBA8 in one buffer a thread keeps, not a new array of colours for every upload
		ReadOnlySpan<byte> bytes = Format == ImageFormat.RGBA8888
			? MipData( 0 )
			: Converted( width, height );

		if ( bytes.Length < width * height * 4 )
		{
			Refused();
			Missing( $"{width}x{height} {Format} has {bytes.Length} bytes of pixels" );
			return 0;
		}

		Uploads++;
		UploadedPixels += width * height;

		//A texture's shape never changes, so new pixels go into the image the device already has. Only
		//a texture that cannot take them in place - it has mips, or there is none yet - gets a new one.
		if ( device != 0 && CoreRenderDevice.UpdateTextureRgba( device, bytes, width, height ) )
		{
			deviceVersion = Version;
			Uploaded();
			return device;
		}

		Created++;

		var replacement = CoreRenderDevice.CreateTextureRgba( bytes, width, height, IsSrgb );

		if ( replacement == 0 )
		{
			Refused();
			Missing( $"the device would not take a {width}x{height} picture" );
			return device;
		}

		//Only after the new one is made: a frame drawing with the old one is still drawing with something.
		Release();

		device = replacement;
		deviceVersion = Version;
		Uploaded();

		return device;
	}

	//Upload with mips instead of level 0 only, so downscaled pictures aren't jagged; RGBA8 2D only
	private bool Chained => Mips > 1 && Format == ImageFormat.RGBA8888 && Depth == 1
		&& !Desc.IsCube && !Desc.IsArray
		&& !( ( Desc.m_nFlags & RuntimeTextureSpecificationFlags.TSPEC_RENDER_TARGET ) == RuntimeTextureSpecificationFlags.TSPEC_RENDER_TARGET );

	//The picture onto the device with every level, built here from the first one where nobody wrote them. Zero where the device would not take it.
	private ulong UploadChain( int width, int height )
	{
		if ( !levelsGiven ) BuildLevels();

		var mips = Mips;
		Span<int> levels = mips * 3 <= 96 ? stackalloc int[mips * 3] : new int[mips * 3];
		var total = 0;

		for ( var mip = 0; mip < mips; mip++ )
		{
			var (w, h, _) = MipSize( mip );
			var bytes = MipBytes( mip );

			levels[mip * 3] = w;
			levels[( mip * 3 ) + 1] = h;
			levels[( mip * 3 ) + 2] = bytes;

			total += bytes;
		}

		if ( total > Data.Length ) return 0;

		var made = CoreRenderDevice.CreateTextureMips( Data.AsSpan( 0, total ), levels, width, height, IsSrgb );
		if ( made == 0 ) return 0;

		Uploads++;
		UploadedPixels += width * height;
		Created++;

		//Only after the new one is made: a frame drawing with the old one is still drawing with something.
		Release();

		device = made;
		deviceVersion = Version;
		Uploaded();

		return device;
	}

	//Builds lower mips by alpha-weighted 2x2 averages, avoiding dark fringes on transparent edges
	private void BuildLevels()
	{
		for ( var mip = 1; mip < Mips; mip++ )
		{
			var (sourceWidth, sourceHeight, _) = MipSize( mip - 1 );
			var (width, height, _) = MipSize( mip );

			ReadOnlySpan<byte> source = MipData( mip - 1 );
			var target = MipData( mip );

			if ( source.IsEmpty || target.IsEmpty ) return;

			for ( var y = 0; y < height; y++ )
			{
				var top = Math.Min( y * 2, sourceHeight - 1 ) * sourceWidth;
				var bottom = Math.Min( ( y * 2 ) + 1, sourceHeight - 1 ) * sourceWidth;

				for ( var x = 0; x < width; x++ )
				{
					var left = Math.Min( x * 2, sourceWidth - 1 );
					var right = Math.Min( ( x * 2 ) + 1, sourceWidth - 1 );

					int r = 0, g = 0, b = 0, a = 0;

					//Already multiplied by its own alpha, as text is: a plain average is the right one, and weighing it again would push colour past alpha.
					if ( RawPixels )
					{
						var at0 = ( ( y * width ) + x ) * 4;

						for ( var c = 0; c < 4; c++ )
						{
							var sum = source[( ( top + left ) * 4 ) + c] + source[( ( top + right ) * 4 ) + c]
								+ source[( ( bottom + left ) * 4 ) + c] + source[( ( bottom + right ) * 4 ) + c];

							target[at0 + c] = (byte)( ( sum + 2 ) / 4 );
						}

						continue;
					}

					Weigh( source, ( top + left ) * 4, ref r, ref g, ref b, ref a );
					Weigh( source, ( top + right ) * 4, ref r, ref g, ref b, ref a );
					Weigh( source, ( bottom + left ) * 4, ref r, ref g, ref b, ref a );
					Weigh( source, ( bottom + right ) * 4, ref r, ref g, ref b, ref a );

					var at = ( ( y * width ) + x ) * 4;

					if ( a == 0 )
					{
						target.Slice( at, 4 ).Clear();
						continue;
					}

					target[at] = (byte)( ( r + ( a / 2 ) ) / a );
					target[at + 1] = (byte)( ( g + ( a / 2 ) ) / a );
					target[at + 2] = (byte)( ( b + ( a / 2 ) ) / a );
					target[at + 3] = (byte)( ( a + 2 ) / 4 );
				}
			}
		}
	}

	//One pixel into a sum: its colour by its alpha, and the alpha.
	private static void Weigh( ReadOnlySpan<byte> pixels, int at, ref int r, ref int g, ref int b, ref int a )
	{
		int alpha = pixels[at + 3];

		r += pixels[at] * alpha;
		g += pixels[at + 1] * alpha;
		b += pixels[at + 2] * alpha;
		a += alpha;
	}

	//Notes that this version of the picture was refused, so it is left alone for a while.
	private void Refused()
	{
		refusedVersion = Version;
		refusedAt = EngineLoop.LoopFrames;
	}

	//Pass pixel values to the shader as-is (text, normal maps), not as colour; changing it re-uploads
	internal bool RawPixels
	{
		get => rawPixels;
		set
		{
			if ( rawPixels == value ) return;

			rawPixels = value;

			if ( device != 0 ) Version++;
		}
	}

	private bool rawPixels;

	//Whether the pixels are meant to be read as sRGB, which everything drawn by hand is unless it is data rather than a picture.
	private bool IsSrgb => !RawPixels && Format switch
	{
		ImageFormat.RGBA8888 or ImageFormat.BGRA8888 or ImageFormat.ARGB8888 or
		ImageFormat.ABGR8888 or ImageFormat.RGB888 or ImageFormat.BGR888 or
		ImageFormat.RGBX8888 or ImageFormat.BGRX8888 => true,

		_ => false,
	};

	//Whether the copy on the device was made as sRGB, for a picture that has no pixels here to
	//decide it by - one loaded straight from a build's blocks, see ImageLoader.FromShippedBlocks.
	internal bool DeviceSrgb { get; set; }

	//Whether sampling decodes sRGB; shaders reading data from such a texture must re-encode it
	internal bool DecodesSrgb => Data is null ? DeviceSrgb : IsSrgb;

	//Weakly tracks textures with CPU pixels, so memory reports count only live ones
	private static readonly List<WeakReference<CoreTexture>> watched = new();

	private static void Watch( CoreTexture texture )
	{
		lock ( watched ) watched.Add( new WeakReference<CoreTexture>( texture ) );
	}

	//How many bytes of pixels are held on the processor, and by how many pictures. Drops the ones that have been collected on the way past.
	internal static (long Bytes, int Count) HeldPixels()
	{
		var bytes = 0L;
		var count = 0;

		lock ( watched )
		{
			var to = 0;

			for ( var from = 0; from < watched.Count; from++ )
			{
				if ( !watched[from].TryGetTarget( out var texture ) ) continue;

				watched[to++] = watched[from];

				if ( texture.Data is null ) continue;

				bytes += texture.Data.Length;
				count++;
			}

			watched.RemoveRange( to, watched.Count - to );
		}

		return ( bytes, count );
	}

	//The pictures holding the most pixels on the processor, heaviest first - for the report.
	internal static List<(string Name, long Bytes)> HeaviestHeld( int count )
	{
		var held = new List<(string Name, long Bytes)>();

		lock ( watched )
		{
			foreach ( var weak in watched )
				if ( weak.TryGetTarget( out var texture ) && texture.Data is { Length: > 0 } data )
					held.Add( (texture.Name ?? "(unnamed)", data.Length) );
		}

		held.Sort( ( a, b ) => b.Bytes.CompareTo( a.Bytes ) );

		if ( held.Count > count ) held.RemoveRange( count, held.Count - count );

		return held;
	}

	//Lets go of the copy on the device. The pixels stay.
	internal void Release()
	{
		if ( device == 0 ) return;

		//A borrowed picture is the viewport's, and the viewport is still using it. Letting go of it here means only forgetting where it was.
		if ( !borrowed ) CoreRenderDevice.DestroyTexture( device );

		device = 0;
		deviceVersion = -1;
	}
}
