using System;
using System.Runtime.InteropServices;

namespace Editor;

//rintencore's hdr_shim for the texture compiler: .exr reading and BC6H packing, desktop editor only
internal static unsafe partial class HdrPictures
{
	private const string Library = "rintencore";

	[LibraryImport( Library )]
	private static partial float* rn_exr_load( byte* bytes, int byteCount, int* width, int* height, byte* error, int errorSize );

	[LibraryImport( Library )]
	private static partial void rn_exr_free( float* rgba );

	[LibraryImport( Library )]
	private static partial void rn_bc6h_encode( ushort* rgb, byte* block );

	//Tells the engine's texture compiler how to read an .exr and pack a picture's light into blocks
	internal static void Install()
	{
		Rinten.CoreTextureFile.DecodeExr = Decode;
		Rinten.CoreTextureFile.EncodeBc6h = HasEncoder() ? Encode : null;
	}

	//A rintencore built before the encoder came in has not got it, and its pictures compile to RGB9E5
	private static bool HasEncoder()
	{
		try
		{
			Encode( stackalloc ushort[48], stackalloc byte[16] );
			return true;
		}
		catch ( Exception e ) when ( e is EntryPointNotFoundException or DllNotFoundException )
		{
			return false;
		}
	}

	private static void Encode( ReadOnlySpan<ushort> rgb, Span<byte> block )
	{
		fixed ( ushort* texels = rgb )
		fixed ( byte* packed = block )
			rn_bc6h_encode( texels, packed );
	}

	private static (float[] Rgba, int Width, int Height)? Decode( byte[] encoded, string source )
	{
		if ( encoded is null || encoded.Length == 0 ) return null;

		int width, height;
		var error = stackalloc byte[256];
		float* rgba;

		try
		{
			fixed ( byte* bytes = encoded )
				rgba = rn_exr_load( bytes, encoded.Length, &width, &height, error, 256 );
		}
		catch ( Exception e ) when ( e is EntryPointNotFoundException or DllNotFoundException )
		{
			Log.Warning( $"{source}: this editor's rintencore has no EXR reader - rebuild the engine" );
			return null;
		}

		if ( rgba is null || width <= 0 || height <= 0 )
		{
			if ( rgba is not null ) rn_exr_free( rgba );

			Log.Warning( $"{source}: not a readable EXR ({Marshal.PtrToStringUTF8( (IntPtr)error )})" );
			return null;
		}

		var pixels = new ReadOnlySpan<float>( rgba, width * height * 4 ).ToArray();
		rn_exr_free( rgba );

		return (pixels, width, height);
	}
}
