using System;
using System.IO;

namespace Rinten;

//File names derived from a source (compiled output, blob sidecar), shared by engine and build tools
internal static class CompiledPaths
{
	/// <summary>The model built from a definition: <c>thing.mdl</c> to <c>thing.mdl_c</c>.</summary>
	public static string Model( string definition ) => Path.ChangeExtension( definition, ".mdl_c" );

	/// <summary>What an asset builder makes of a source: the source's own name with <c>_c</c> on the end - see <c>BuildsAssetTypeAttribute</c>.</summary>
	public static string Built( string source ) => source + "_c";

	/// <summary>A picture's blocks, beside it: colour, or data that is not colour.</summary>
	public static string TextureBlocks( string image, bool srgb ) => image + ( srgb ? ".tex_c" : ".data.tex_c" );

	/// <summary>A picture's blocks cut by a mask - see CoreTextureFile.Compile - beside the picture, under a name that says which mask: one colour two materials cut differently is two sets of blocks. The mask by the name a material gives it, so the editor, an export and the game it made all find the same file.</summary>
	public static string TextureBlocks( string image, bool srgb, string cutBy )
	{
		if ( string.IsNullOrWhiteSpace( cutBy ) ) return TextureBlocks( image, srgb );

		var name = cutBy.Replace( '\\', '/' ).TrimStart( '/' ).ToLowerInvariant();
		var hash = (uint)Rinten.Hashing.XxHash3.HashToUInt64( System.Text.Encoding.UTF8.GetBytes( name ) );

		return image + $".cut{hash:x8}" + ( srgb ? ".tex_c" : ".data.tex_c" );
	}

	/// <summary>Blocks in their compressed form, for a device that takes BC.</summary>
	public static string Compressed( string blocks ) => blocks + ".bc";

	/// <summary>Blocks that travel as a KTX2 of Basis Universal's UASTC - what a web build carries of a picture, for the page to turn into what its device reads. See StandaloneExporter.Pictures and BrowserTextures.</summary>
	public static string Universal( string blocks ) => blocks + ".ktx2";

	/// <summary>A terrain's chunks as the editor baked them, shipped so a game starts without baking: <c>thing.terrain</c> to <c>thing.terrain_c.chunks</c>. See TerrainBakeCache.</summary>
	public static string TerrainChunks( string terrain ) => terrain + "_c.chunks";

	/// <summary>A resource's binary half: <c>thing.scene</c> to <c>thing.scene_d</c>.</summary>
	public static string BlobSidecar( string resource ) => resource + "_d";

	/// <summary>Whether this is a picture's blocks, in either reading and either form - what a picture compiles into rather than anything somebody wrote.</summary>
	public static bool IsTextureBlocks( string path )
		=> path.EndsWith( ".tex_c", StringComparison.OrdinalIgnoreCase )
		|| path.EndsWith( ".tex_c.bc", StringComparison.OrdinalIgnoreCase )
		|| path.EndsWith( ".tex_c.ktx2", StringComparison.OrdinalIgnoreCase );
}
