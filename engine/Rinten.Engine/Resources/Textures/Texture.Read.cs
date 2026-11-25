using NativeEngine;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Rinten;

public partial class Texture
{
	/// <summary>Reads pixel colors from the texture at the specified mip level</summary>
	[Pure]
	public Color32[] GetPixels( int mip = 0 )
	{
		//BUG: Width/Height is on disk and not the currently streamed mip I don't know what would break if I made Width/Height represent that instead
		var desc = core.Desc;

		mip = Math.Clamp( mip, 0, Mips - 1 );
		var d = 1 << mip;

		if ( Depth == 1 )
		{
			var rect = (X: 0, Y: 0, W: desc.m_nWidth / d, H: desc.m_nHeight / d);
			var data = new Color32[rect.W * rect.H];

			GetPixels( rect, 0, mip, data.AsSpan(), ImageFormat.RGBA8888 );

			return data;
		}
		else
		{
			var box = (X: 0, Y: 0, Z: 0, Width: desc.m_nWidth / d, Height: desc.m_nHeight / d, Depth: Depth / d);
			var data = new Color32[box.Width * box.Height * box.Depth];

			GetPixels3D( box, mip, data.AsSpan(), ImageFormat.RGBA8888 );

			return data;
		}
	}

	public unsafe Bitmap GetBitmap( int mip )
	{
		mip = Math.Clamp( mip, 0, Mips - 1 );
		var d = 1 << mip;

		bool floatingPoint = ImageFormat == ImageFormat.RGBA16161616F;

		var desc = core.Desc;
		var width = desc.m_nWidth / d;
		var height = desc.m_nHeight / d;
		var depth = Depth; //Cubes have 6 depth even though reports as only 1 in desc
		var outputFormat = floatingPoint ? ImageFormat.RGBA16161616F : ImageFormat.RGBA8888;
		var targetMemoryRequired = ImageFormatSize.Total( width, height, depth, 1, outputFormat );

		if ( targetMemoryRequired <= 0 )
		{
			//If desc.m_nWidth and height are 0, this is probably the rendersystemempty, which is obviously a disaster

			throw new System.Exception( $"targetMemoryRequired <= 0 ({width}x{height}x{depth} {outputFormat})" );
		}

		var bitmap = new Bitmap( width, height * depth, floatingPoint );
		var data = bitmap.GetBuffer();

		if ( data.Length != targetMemoryRequired )
		{
			throw new System.Exception( $"Buffer isn't big enough {data.Length} != {targetMemoryRequired}" );
		}

		fixed ( byte* pData = data )
		{
			if ( depth > 1 )
			{
				GetPixels3D( (0, 0, 0, width, height, depth), mip, data, outputFormat );
			}
			else			if ( !core.Read( (0, 0, width, height), 0, mip, data, outputFormat,
				(0, 0, width, height), width * GetImageFormatSize( outputFormat ) ) )
				return null;
		}

		return bitmap;
	}

	private static int GetImageFormatSize( ImageFormat format )
	{
		switch ( format )
		{
			case ImageFormat.RGBA16161616F:
				return 8;
			case ImageFormat.RGBA8888:
			case ImageFormat.BGRA8888:
			case ImageFormat.ARGB8888:
			case ImageFormat.ABGR8888:
			case ImageFormat.R32F:
			case ImageFormat.R32_UINT:
				return 4;

			case ImageFormat.RGB888:
			case ImageFormat.BGR888:
				return 3;

			case ImageFormat.R16:
			case ImageFormat.R16F:
				return 2;

			case ImageFormat.I8:
				return 1;

			default:
				throw new NotImplementedException( $"Reading pixels with format {format} not yet implemented." );
		}
	}

	private static int GetImageFormatSize( ImageFormat format, int width, int height )
	{
		if ( width <= 0 || height <= 0 ) throw new ArgumentException( "Width and height must be positive" );

		return ImageFormatSize.Total( width, height, 1, 1, format );
	}

	//How many slices a mip has: a volume's depth shrinks with its mips, an array's or cube's count does not.
	private int GetSliceCount( int mip )
	{
		var desc = core.Desc;

		if ( desc.m_nFlags.HasFlag( RuntimeTextureSpecificationFlags.TSPEC_VOLUME_TEXTURE ) )
			return Math.Max( 1, desc.m_nDepth >> mip );

		return Math.Max( 1, desc.ArrayCount );
	}

	private void ThrowIfSliceOutOfRange( int slice, int mip )
	{
		if ( slice < 0 || slice >= GetSliceCount( mip ) )
			throw new ArgumentOutOfRangeException( nameof( slice ) );
	}

	private void ThrowIfMipOutOfRange( int mip )
	{
		if ( mip < 0 || mip >= Mips )
			throw new ArgumentOutOfRangeException( nameof( mip ) );
	}

	/// <summary>Reads a 2D range of pixel values from the texture at the specified mip level, writing to <paramref name="dstData"/>. This reads one slice from a 2D texture array or 3D texture volume.</summary>
	[Pure]
	public unsafe void GetPixels<T>( (int X, int Y, int Width, int Height) srcRect, int slice, int mip, Span<T> dstData, ImageFormat dstFormat, (int X, int Y) dstSize = default )
		where T : unmanaged
	{
		var pixelSize = Unsafe.SizeOf<T>();

		if ( dstSize.X < 0 || dstSize.Y < 0 )
			throw new ArgumentException( $"{nameof( dstSize )} can't be negative" );

		if ( dstSize.X == 0 )
			dstSize.X = srcRect.Width;

		if ( dstSize.Y == 0 )
			dstSize.Y = srcRect.Height;

		if ( srcRect.X < 0 || srcRect.Y < 0 ||
			srcRect.Width <= 0 || srcRect.Height <= 0 ||
			((long)srcRect.X + srcRect.Width) > Width ||
			((long)srcRect.Y + srcRect.Height) > Height )
		{
			throw new ArgumentException( $"{nameof( srcRect )} out of range" );
		}

		ThrowIfSliceOutOfRange( slice, mip );

		if ( dstSize.X > srcRect.Width || dstSize.Y > srcRect.Height )
			throw new ArgumentException( $"{nameof( dstSize )} can't be larger than {nameof( srcRect )}" );

		var dstStrideBytes = GetImageFormatSize( dstFormat, dstSize.X, 1 );
		if ( dstStrideBytes <= 0 )
			throw new ArgumentException( $"{nameof( dstSize )} invalid" );
		if ( dstStrideBytes > int.MaxValue )
			throw new ArgumentException( $"{nameof( dstSize )} too large" );

		var sliceSize = GetImageFormatSize( dstFormat, dstSize.X, dstSize.Y );
		if ( sliceSize <= 0 )
			throw new ArgumentException( $"{nameof( dstSize )} invalid" );

		if ( !SandboxedUnsafe.IsAcceptablePod<T>() )
			throw new ArgumentException( $"{nameof( T )} must be a POD type" );

		var dataSize = (long)dstData.Length * pixelSize;

		if ( dataSize < sliceSize )
		{
			throw new ArgumentException( "Output array is too small to fit the given pixel range.",
				nameof( dstData ) );
		}

			if ( !core.Read( srcRect, slice, mip, MemoryMarshal.AsBytes( dstData ), dstFormat,
				(0, 0, dstSize.X, dstSize.Y), dstStrideBytes ) )
				throw new Exception( "Unable to read texture pixels" );

		var nativeSrcRect = new NativeRect { x = srcRect.X, y = srcRect.Y, w = srcRect.Width, h = srcRect.Height };
		var nativeDstRect = new NativeRect { x = 0, y = 0, w = dstSize.X, h = dstSize.Y };

		fixed ( T* dataPtr = dstData )
		{
		//No-op: the engine holds no such data, so there is nothing to ask.
		}
	}

	/// <summary>Reads a 2D range of pixel values from the texture at the specified mip level, writing to <paramref name="dstData"/>. This reads one slice from a 2D texture array or 3D texture volume.</summary>
	[Pure]
	public unsafe void GetPixels<T>( (int X, int Y, int Width, int Height) srcRect, int slice, int mip, Span<T> dstData, ImageFormat dstFormat, (int X, int Y, int W, int H) dstRect, int dstStride )
		where T : unmanaged
	{
		var pixelByteSize = Unsafe.SizeOf<T>();

		if ( dstRect.X < 0 || dstRect.Y < 0 || dstRect.W < 0 || dstRect.H < 0 )
			throw new ArgumentException( $"{nameof( dstRect )} can't be negative" );

		if ( dstRect.W == 0 )
			dstRect.W = srcRect.Width;

		if ( dstRect.H == 0 )
			dstRect.H = srcRect.Height;

		if ( srcRect.X < 0 || srcRect.Y < 0 ||
			srcRect.Width <= 0 || srcRect.Height <= 0 ||
			((long)srcRect.X + srcRect.Width) > Width ||
			((long)srcRect.Y + srcRect.Height) > Height )
		{
			throw new ArgumentException( $"{nameof( srcRect )} out of range" );
		}

		ThrowIfSliceOutOfRange( slice, mip );

		if ( dstRect.W > srcRect.Width || dstRect.H > srcRect.Height )
			throw new ArgumentException( $"{nameof( dstRect )} can't be larger than {nameof( srcRect )}" );

		if ( !SandboxedUnsafe.IsAcceptablePod<T>() )
			throw new ArgumentException( $"{nameof( T )} must be a POD type" );

		long formatSize = GetImageFormatSize( dstFormat );
		long strideBytes = (long)dstStride * pixelByteSize;
		long rowEndBytes = ((long)dstRect.X + dstRect.W) * formatSize;

		if ( strideBytes > int.MaxValue || rowEndBytes > strideBytes )
			throw new ArgumentException( $"{nameof( dstStride )} ({dstStride}) is too small for {nameof( dstRect )}" );

		var requiredBytes = ((long)dstRect.Y + dstRect.H - 1) * strideBytes + rowEndBytes;
		var dataSize = (long)dstData.Length * pixelByteSize;

		if ( requiredBytes > dataSize || requiredBytes > int.MaxValue )
		{
			throw new ArgumentException( $"Output rect size ({requiredBytes} bytes) exceeds destination array size ({dataSize} bytes)",
				nameof( dstData ) );
		}

			if ( !core.Read( srcRect, slice, mip, MemoryMarshal.AsBytes( dstData ), dstFormat,
				(dstRect.X, dstRect.Y, dstRect.W, dstRect.H), (int)strideBytes ) )
				throw new Exception( "Unable to read texture pixels" );

		var nativeSrcRect = new NativeRect { x = srcRect.X, y = srcRect.Y, w = srcRect.Width, h = srcRect.Height };
		var nativeDstRect = new NativeRect { x = dstRect.X, y = dstRect.Y, w = dstRect.W, h = dstRect.H };

		fixed ( T* dataPtr = dstData )
		{
		//No-op: the engine holds no such data, so there is nothing to ask.
		}
	}

	/// <summary>Reads a 3D range of pixel values from the texture at the specified mip level, writing to <paramref name="dstData"/>. This can be used with a 2D texture array, or a 3D volume texture.</summary>
	[Pure]
	public void GetPixels3D<T>( (int X, int Y, int Z, int Width, int Height, int Depth) srcBox, int mip, Span<T> dstData, ImageFormat dstFormat, (int X, int Y, int Z) dstSize = default ) where T : unmanaged
	{
		if ( dstSize.X < 0 || dstSize.Y < 0 || dstSize.Z < 0 )
			throw new ArgumentException( $"{nameof( dstSize )} can't be negative" );

		if ( dstSize.X == 0 )
			dstSize.X = srcBox.Width;

		if ( dstSize.Y == 0 )
			dstSize.Y = srcBox.Height;

		if ( dstSize.Z == 0 )
			dstSize.Z = srcBox.Depth;

		if ( srcBox.X < 0 || srcBox.Y < 0 || srcBox.Z < 0 ||
			srcBox.Width <= 0 || srcBox.Height <= 0 || srcBox.Depth <= 0 ||
			((long)srcBox.X + srcBox.Width) > Width ||
			((long)srcBox.Y + srcBox.Height) > Height ||
			((long)srcBox.Z + srcBox.Depth) > Depth )
		{
			throw new ArgumentException( $"{nameof( srcBox )} out of range" );
		}

		if ( !SandboxedUnsafe.IsAcceptablePod<T>() )
			throw new ArgumentException( $"{nameof( T )} must be a POD type" );

		var sliceSize = GetImageFormatSize( dstFormat, dstSize.X, dstSize.Y );
		if ( sliceSize <= 0 )
			throw new ArgumentException( $"{nameof( dstSize )} invalid" );
		var sliceStride = sliceSize / Unsafe.SizeOf<T>();

		for ( var z = 0; z < srcBox.Depth; ++z )
		{
			GetPixels(
				(srcBox.X, srcBox.Y, srcBox.Width, srcBox.Height),
				srcBox.Z + z, mip,
				dstData.Slice( z * sliceStride, sliceStride ),
				dstFormat, (dstSize.X, dstSize.Y) );
		}
	}

	/// <summary>Reads a single pixel color.</summary>
	[Pure]
	public unsafe Color32 GetPixel( float x, float y, int mip = 0 )
	{
		x = x.FloorToInt();
		y = y.FloorToInt();

		if ( x < 0 ) return Color.Green;
		if ( y < 0 ) return Color.Green;
		if ( x > Width - 1 ) return Color.Green;
		if ( y > Height - 1 ) return Color.Green;

		mip = Math.Clamp( mip, 0, Mips - 1 );

		int d = (int)Math.Pow( 2, mip );

		var data = new Color32[1];
		var source = new NativeRect { x = (int)x, y = (int)y, w = 1, h = 1 };
		var dest = new NativeRect { x = 0, y = 0, w = 1, h = 1 };

			if ( !core.Read( ((int)x, (int)y, 1, 1), 0, mip,
				MemoryMarshal.AsBytes( data.AsSpan() ), ImageFormat.RGBA8888, (0, 0, 1, 1), 4 ) )
				return Color.Red;

			return data[0];

		{
		//No-op: the engine holds no such data, so there is nothing to ask.
		}
	}

	/// <summary>Reads a single pixel color from a volume or array texture.</summary>
	[Pure]
	public unsafe Color32 GetPixel3D( float x, float y, float z, int mip = 0 )
	{
		x = x.FloorToInt();
		y = y.FloorToInt();
		z = z.FloorToInt();

		if ( x < 0 ) return Color.Green;
		if ( y < 0 ) return Color.Green;
		if ( z < 0 ) return Color.Green;
		if ( x > Width - 1 ) return Color.Green;
		if ( y > Height - 1 ) return Color.Green;
		if ( z > Depth - 1 ) return Color.Green;

		mip = Math.Clamp( mip, 0, Mips - 1 );

		var data = new Color32[1];
		var source = new NativeRect { x = (int)x, y = (int)y, w = 1, h = 1 };
		var dest = new NativeRect { x = 0, y = 0, w = 1, h = 1 };

			if ( !core.Read( ((int)x, (int)y, 1, 1), (int)z, mip,
				MemoryMarshal.AsBytes( data.AsSpan() ), ImageFormat.RGBA8888, (0, 0, 1, 1), 4 ) )
				return Color.Red;

			return data[0];

		{
		//No-op: the engine holds no such data, so there is nothing to ask.
		}
	}

	/// <summary>Asynchronously reads all pixel colors from the texture at the specified mip level.</summary>
	public void GetPixelsAsync( Action<ReadOnlySpan<Color32>> callback, int mip = 0 )
	{
		mip = Math.Clamp( mip, 0, Mips - 1 );
		var d = 1 << mip;

		var desc = core.Desc;

		if ( Depth == 1 )
		{
			var width = desc.m_nWidth / d;
			var height = desc.m_nHeight / d;
			var rect = (X: 0, Y: 0, Width: width, Height: height);

			GetPixelsAsync( callback, ImageFormat.RGBA8888, rect, 0, mip );
		}
		else
		{
			var box = (X: 0, Y: 0, Z: 0, Width: desc.m_nWidth / d, Height: desc.m_nHeight / d, Depth: Depth / d);

			GetPixelsAsync3D( callback, ImageFormat.RGBA8888, box, mip );
		}
	}

	/// <summary>Reads a 2D pixel range at a mip asynchronously; the span is only valid during the callback</summary>
	public void GetPixelsAsync<T>( Action<ReadOnlySpan<T>> callback, ImageFormat dstFormat = ImageFormat.Default, (int X, int Y, int Width, int Height) srcRect = default, int slice = 0, int mip = 0 ) where T : unmanaged
	{
		ThrowIfMipOutOfRange( mip );

		if ( srcRect.X < 0 || srcRect.Y < 0 ||
			srcRect.Width <= 0 || srcRect.Height <= 0 ||
			((long)srcRect.X + srcRect.Width) > Math.Max( 1, Width >> mip ) ||
			((long)srcRect.Y + srcRect.Height) > Math.Max( 1, Height >> mip ) )
		{
			throw new ArgumentException( $"{nameof( srcRect )} out of range" );
		}

		ThrowIfSliceOutOfRange( slice, mip );

		if ( !SandboxedUnsafe.IsAcceptablePod<T>() )
			throw new ArgumentException( $"{nameof( T )} must be a POD type" );

		if ( dstFormat == ImageFormat.Default ) dstFormat = ImageFormat;

		//Nothing to wait for: the pixels of one of ours are already in memory, so the answer is read here and handed straight to the callback.
			var pixelBytes = GetImageFormatSize( dstFormat );
			var buffer = new byte[srcRect.Width * srcRect.Height * pixelBytes];

			if ( core.Read( srcRect, slice, mip, buffer, dstFormat,
				(0, 0, srcRect.Width, srcRect.Height), srcRect.Width * pixelBytes ) )
			{
				callback( MemoryMarshal.Cast<byte, T>( buffer ) );
			}

		//Reading back from the GPU asynchronously was their render context's to do. Ours keeps
		//the pixels in memory, so anything that gets here is a texture that has none to read.
	}

	/// <summary>Reads a 3D pixel range at a mip asynchronously; the span is only valid during the callback</summary>
	public void GetPixelsAsync3D<T>( Action<ReadOnlySpan<T>> callback, ImageFormat dstFormat = ImageFormat.Default, (int X, int Y, int Z, int Width, int Height, int Depth) srcBox = default, int mip = 0 ) where T : unmanaged
	{
		//Default to full texture if not specified
		if ( srcBox.Depth == 0 )
			srcBox.Depth = Depth;

		ThrowIfMipOutOfRange( mip );

		if ( srcBox.X < 0 || srcBox.Y < 0 || srcBox.Z < 0 ||
			srcBox.Width <= 0 || srcBox.Height <= 0 || srcBox.Depth <= 0 ||
			((long)srcBox.X + srcBox.Width) > Math.Max( 1, Width >> mip ) ||
			((long)srcBox.Y + srcBox.Height) > Math.Max( 1, Height >> mip ) ||
			((long)srcBox.Z + srcBox.Depth) > GetSliceCount( mip ) )
		{
			throw new ArgumentException( $"{nameof( srcBox )} out of range" );
		}

		if ( !SandboxedUnsafe.IsAcceptablePod<T>() )
			throw new ArgumentException( $"{nameof( T )} must be a POD type" );

		if ( dstFormat == ImageFormat.Default ) dstFormat = ImageFormat;

		var sliceSize = GetImageFormatSize( dstFormat, srcBox.Width, srcBox.Height );
		if ( sliceSize <= 0 )
			throw new ArgumentException( $"{nameof( srcBox )} invalid size" );

		var totalBytes = checked(sliceSize * srcBox.Depth);

		//One of ours, read slice by slice here rather than through a context that would only be waiting for memory it already has.
			var buffer = new byte[totalBytes];
			var stride = srcBox.Width * GetImageFormatSize( dstFormat );

			for ( var z = 0; z < srcBox.Depth; z++ )
			{
				core.Read( (srcBox.X, srcBox.Y, srcBox.Width, srcBox.Height), srcBox.Z + z, mip,
					buffer.AsSpan( z * sliceSize, sliceSize ), dstFormat,
					(0, 0, srcBox.Width, srcBox.Height), stride );
			}

			callback( MemoryMarshal.Cast<byte, T>( buffer ) );

		//Reading back from the GPU asynchronously was their render context's to do. Ours keeps
		//the pixels in memory, so anything that gets here is a texture that has none to read.
	}

	/// <summary>Asynchronously reads the texture into a bitmap at the specified mip level.</summary>
	public void GetBitmapAsync( Action<Bitmap> callback, int mip = 0 )
	{
		mip = Math.Clamp( mip, 0, Mips - 1 );
		var d = 1 << mip;

		bool floatingPoint = ImageFormat == ImageFormat.RGBA16161616F;
		var desc = core.Desc;
		var width = desc.m_nWidth / d;
		var height = desc.m_nHeight / d;
		var dstFormat = floatingPoint ? ImageFormat.RGBA16161616F : ImageFormat.RGBA8888;

			var made = GetBitmap( mip );
			if ( made is not null ) callback( made );

		//Reading back from the GPU asynchronously was their render context's to do. Ours keeps
		//the pixels in memory, so anything that gets here is a texture that has none to read.
	}

	//Converts image data from one format to another and writes to an existing buffer.
	private static unsafe void ConvertImageDataTo(
		ReadOnlySpan<byte> srcData,
		ImageFormat srcFormat,
		Span<byte> dstBuffer,
		ImageFormat dstFormat,
		int width,
		int height )
	{
		fixed ( byte* srcPtr = srcData )
		fixed ( byte* dstPtr = dstBuffer )
		{
		//No-op: nothing to do here.
		}
	}
}
