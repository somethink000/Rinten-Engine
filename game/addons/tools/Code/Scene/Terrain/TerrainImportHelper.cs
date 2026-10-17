using SkiaSharp;
using System.Runtime.InteropServices;

namespace Editor.TerrainEditor;

internal static class TerrainImportHelper
{
	internal static ushort[] ResampleHeightmap( Span<ushort> original, int originalSize, int newSize )
	{
		using var bitmap = new SKBitmap( originalSize, originalSize, SKColorType.Alpha16, SKAlphaType.Opaque );
		using ( var pixmap = bitmap.PeekPixels() )
		{
			var dataBytes = MemoryMarshal.AsBytes( original );
			unsafe
			{
				fixed ( byte* source = dataBytes )
				{
					Buffer.MemoryCopy( source, (void*)pixmap.GetPixels(), dataBytes.Length, dataBytes.Length );
				}
			}
		}

		using var newBitmap = bitmap.Resize( new SKSizeI( newSize, newSize ), new SKSamplingOptions( SKFilterMode.Linear, SKMipmapMode.None ) );
		using var newPixmap = newBitmap.PeekPixels();
		return newPixmap.GetPixelSpan<ushort>().ToArray();
	}

	internal static int RoundDownToPowerOfTwo( int value )
	{
		value = value | (value >> 1);
		value = value | (value >> 2);
		value = value | (value >> 4);
		value = value | (value >> 8);
		value = value | (value >> 16);
		return value - (value >> 1);
	}
}
