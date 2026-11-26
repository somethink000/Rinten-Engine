namespace Rinten;

public partial class Bitmap
{
	//The largest picture a TGA may hold, so a malformed header can't ask for an allocation the machine can't meet.
	internal const int MaxTgaPixels = 8192 * 8192;

	//Skia does not know this format, and the native float bitmap that read it before is gone. It is a small format: a header, then rows of pixels, optionally run-length encoded. The content this engine inherited is full of them, so it reads them itself.
	/// <summary>Reads a Targa image.</summary>
	public unsafe static Bitmap CreateFromTgaBytes( byte[] data )
	{
		if ( !IsTga( data ) )
			return default;

		var idLength = data[0];
		var colorMapType = data[1];
		var imageType = data[2];

		//A colour-mapped image is a palette plus indices into it. Nothing in the content uses one, and guessing at it would be worse than saying no.
		if ( colorMapType != 0 )
			return default;

		var width = data[12] | ( data[13] << 8 );
		var height = data[14] | ( data[15] << 8 );
		var bits = data[16];
		var descriptor = data[17];

		if ( width <= 0 || height <= 0 )
			return default;

		if ( (long)width * height > MaxTgaPixels )
			return default;

		if ( bits != 8 && bits != 24 && bits != 32 )
			return default;

		var bytesPerPixel = bits / 8;
		var at = 18 + idLength;

		var pixels = new byte[width * height * 4];
		var compressed = imageType is 9 or 10 or 11;

		//Bit 5 of the descriptor: set means the first row in the file is the top one. Unset - which is the usual - means the image is bottom-up.
		var topDown = ( descriptor & 0x20 ) != 0;

		var written = 0;
		var total = width * height;

		while ( written < total )
		{
			if ( at >= data.Length ) return default;

			var run = 1;
			var repeat = false;

			if ( compressed )
			{
				var packet = data[at++];

				run = ( packet & 0x7F ) + 1;
				repeat = ( packet & 0x80 ) != 0;
			}

			for ( var i = 0; i < run; i++ )
			{
				if ( written >= total ) break;

				//A repeated run holds one pixel that stands for all of them.
				var source = at + ( repeat ? 0 : i * bytesPerPixel );

				if ( source + bytesPerPixel > data.Length ) return default;

				byte b, g, r, a;

				if ( bits == 8 )
				{
					b = g = r = data[source];
					a = 255;
				}
				else
				{
					b = data[source + 0];
					g = data[source + 1];
					r = data[source + 2];
					a = bits == 32 ? data[source + 3] : (byte)255;
				}

				var x = written % width;
				var y = written / width;

				//Written into the buffer the way a picture is read: top row first, whichever way round the file stored it.
				var row = topDown ? y : height - 1 - y;
				var to = ( row * width + x ) * 4;

				pixels[to + 0] = r;
				pixels[to + 1] = g;
				pixels[to + 2] = b;
				pixels[to + 3] = a;

				written++;
			}

			at += ( repeat ? 1 : run ) * bytesPerPixel;
		}

		var bitmap = new Bitmap( width, height );
		bitmap.SetPixelsRgba( pixels );

		return bitmap;
	}

	/// <summary>Return true if this data is a Tga file</summary>
	public static bool IsTga( byte[] data )
	{
		if ( data is null || data.Length < 18 ) return false;
		int imageType = data[2];

		if ( imageType == 2 || imageType == 3 || imageType == 10 )
		{
			int width = data[12] | (data[13] << 8);
			int height = data[14] | (data[15] << 8);
			int bits = data[16];

			if ( width > 0 && height > 0 && (bits == 8 || bits == 24 || bits == 32) )
				return true;
		}

		return false;
	}
}
