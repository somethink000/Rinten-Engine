using NativeEngine;
using System.Buffers;
using System.Runtime.InteropServices;

namespace Rinten;

public partial class Texture
{
	/// <summary>Clear this texture with a solid color</summary>
	public void Clear( Color color )
	{
			core.Clear( color );
	}

	/// <summary>Update this texture with given raw data.</summary>
	public void Update( ReadOnlySpan<byte> data, int x = 0, int y = 0, int width = 0, int height = 0 )
	{
		UpdateInternal( data, x, y, 0, width, height, 1 );
	}

	/// <summary>Update this texture with given raw data.</summary>
	public void Update<T>( ReadOnlySpan<T> data, int x = 0, int y = 0, int width = 0, int height = 0 ) where T : struct
	{
		UpdateInternal( MemoryMarshal.Cast<T, byte>( data ), x, y, 0, width, height, 1 );
	}

	/// <summary>Update this texture with given raw data.</summary>
	public void Update( ReadOnlySpan<Color32> data, int x = 0, int y = 0, int width = 0, int height = 0 )
	{
		UpdateInternal( MemoryMarshal.Cast<Color32, byte>( data ), x, y, 0, width, height, 1 );
	}

	/// <summary>Update this texture from the bitmap</summary>
	public unsafe void Update( Bitmap source )
	{
		UpdateInternal( source.GetBuffer(), 0, 0, 0, source.Width, source.Height, 1 );
	}

	/// <summary>Update this 3D texture with given raw data.</summary>
	public void Update3D( ReadOnlySpan<byte> data, int x = 0, int y = 0, int z = 0, int width = 0, int height = 0, int depth = 0 )
	{
		UpdateInternal( data, x, y, z, width, height, depth );
	}

	private void UpdateInternal( ReadOnlySpan<byte> data, int x, int y, int z, int width, int height, int depth )
	{
		//No rectangle given is the whole picture, which is what every overload here documents.
		//Left as nought it was a region nought wide, and the write went nowhere at all.
		if ( width <= 0 ) width = Width;
		if ( height <= 0 ) height = Height;
		if ( depth <= 0 ) depth = 1;

		if ( x < 0 || y < 0 || z < 0 )
			throw new ArgumentException( "Update region can't be negative" );

		if ( (long)x + width > Width || (long)y + height > Height || (long)z + depth > Math.Max( Depth, 1 ) )
			throw new ArgumentException( "Update region is outside the texture" );

		var memRequired = ImageFormatSize.Total( width, height, depth, 1, ImageFormat );
		if ( data.Length < memRequired )
		{
			throw new Exception( $"{data.Length} bytes is not enough data to update texture! {memRequired} required." );
		}

		//The whole picture may carry mips; a rectangle is copied row by row.
		if ( x == 0 && y == 0 && z == 0 && width >= Width && height >= Height )
			core.Write( data );
		else
			core.WriteRegion( data, x, y, width, height, 0, z );
	}

	/// <summary>Write a coloured rectangle to the texture</summary>
	public void Update( Color32 color, Rect rect )
	{
		rect.Left = rect.Left.Clamp( 0, Width ).FloorToInt();
		rect.Top = rect.Top.Clamp( 0, Height ).FloorToInt();
		rect.Right = rect.Right.Clamp( 0, Width ).FloorToInt();
		rect.Bottom = rect.Bottom.Clamp( 0, Height ).FloorToInt();

		if ( rect.Width < 1.0f || rect.Height < 1.0f )
			return;

		int pixels = (int)(rect.Width * rect.Height);
		var bytes = ArrayPool<Color32>.Shared.Rent( pixels );

		//I know there's a faster way than this
		for ( int i = 0; i < pixels; i++ )
		{
			bytes[i] = color;
		}

		Update( bytes.AsSpan().Slice( 0, pixels ), (int)rect.Left, (int)rect.Top, (int)rect.Width, (int)rect.Height );

		ArrayPool<Color32>.Shared.Return( bytes );
	}

	/// <summary>Write a coloured pixel to the texture</summary>
	public void Update( Color32 color, float x, float y )
	{
		x = x.FloorToInt();
		y = y.FloorToInt();

		if ( x < 0 ) return;
		if ( y < 0 ) return;
		if ( x > Width - 1 ) return;
		if ( y > Height - 1 ) return;

		var bytes = ArrayPool<Color32>.Shared.Rent( 1 );
		bytes[0] = color;

		Update( bytes.AsSpan().Slice( 0, 1 ), (int)x, (int)y, (int)1, (int)1 );

		ArrayPool<Color32>.Shared.Return( bytes );
	}

}
