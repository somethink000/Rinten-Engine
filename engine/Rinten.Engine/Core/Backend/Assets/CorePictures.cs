using System.IO;

namespace Rinten;

//Reads a picture via its compiled blocks, falling back to shipped blocks when the source is absent
internal static class CorePictures
{
	//Whether a picture's blocks are missing or older than the picture - which is when they are compiled again, by the renderer and by an export alike.
	internal static bool IsStale( string image, string blocks, string cutBy = null )
		=> !File.Exists( blocks ) || File.GetLastWriteTimeUtc( blocks ) < File.GetLastWriteTimeUtc( image )
		|| ( cutBy is not null && File.Exists( cutBy ) && File.GetLastWriteTimeUtc( blocks ) < File.GetLastWriteTimeUtc( cutBy ) );

	//The blocks a build ships for a picture in the requested colour space and device form; null if none
	internal static string ShippedBlocks( string picture, bool srgb, string cutBy = null )
	{
		if ( string.IsNullOrWhiteSpace( picture ) ) return null;

		var blocks = CompiledPaths.TextureBlocks( picture.Replace( '\\', '/' ), srgb, cutBy );

		//A web build's: Basis blocks, for the page to turn into what it reads.
		if ( CoreRender.TakesUniversal && CoreModel.ResolveOnDisk( CompiledPaths.Universal( blocks ) ) is { } universal )
			return universal;

		if ( CoreNative.Available && CoreRender.SupportsBc() != 0
			&& CoreModel.ResolveOnDisk( CompiledPaths.Compressed( blocks ) ) is { } compressed )
		{
			return compressed;
		}

		if ( CoreModel.ResolveOnDisk( blocks ) is { } plain ) return plain;

		//An Android build carries Basis blocks only, and a phone with BC reads them too
		return CoreRender.ReadsUniversal ? CoreModel.ResolveOnDisk( CompiledPaths.Universal( blocks ) ) : null;
	}

	//A picture's carried blocks on the device, or nought where it has none.
	internal static ulong Shipped( string picture, bool srgb, string cutBy = null )
		=> ShippedBlocks( picture, srgb, cutBy ) is { } path ? CoreRenderDevice.CreateTexture( path ) : 0;
}
