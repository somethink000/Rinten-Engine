using System;

namespace Rinten;

//SMAA 1x over a drawer's finished picture: edges and blend weights into two pictures of its own, then the blend into
//where the frame goes. The full frame finds both in compute, the portable frame draws them - see engine/smaa.slang
internal sealed class CoreSmaa
{
	/// <summary>Whether the player asked for SMAA; the portable frame has no MSAA and takes SMAA for it.</summary>
	internal static bool Asked => Rinten.Rendering.DrawSettings.Smaa ||
		( CoreRenderDevice.PortableFrame && Rinten.Rendering.DrawSettings.Msaa >= 2 );

	private ulong edges, weights;

	//The portable frame's finished picture, which its finish draws into instead of the screen
	private ulong finished;
	private int width, height;

	//What each pass reads, made again when the picture read changes
	private ulong edgesReads, weightsReads, blendReads;
	private ulong edgesFrom;

	private readonly byte[] push = new byte[16];

	/// <summary>The portable frame's picture to finish into, made the first time a finish is drawn into it.</summary>
	internal ulong Finished => finished != 0 || edges == 0
		? finished
		: finished = CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.Srgb, width, height );

	/// <summary>Readies the pictures and passes for a picture this size; false where SMAA cannot run.</summary>
	internal bool Ready( int pictureWidth, int pictureHeight )
	{
		if ( pictureWidth < 2 || pictureHeight < 2 || !EnsureShared() ) return false;

		var portable = CoreRenderDevice.PortableFrame;

		if ( pictureWidth == width && pictureHeight == height && edges != 0 ) return !portable || EnsurePortable();

		Release();

		edges = CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.Colour, pictureWidth, pictureHeight, 1, storage: !portable );
		weights = CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.Colour, pictureWidth, pictureHeight, 1, storage: !portable );

		weightsReads = edges == 0 || weights == 0 ? 0 : CoreRenderDevice.CreateMaterial(
			Texture( edges ), area, search, Texture( edges ), 1, 0, 0, 0 );

		if ( weightsReads == 0 || portable && !EnsurePortable() )
		{
			Release();
			return false;
		}

		width = pictureWidth;
		height = pictureHeight;
		return true;
	}

	/// <summary>Smooths the edges of source into the screen or, in the full frame, into Final.</summary>
	internal bool Run( ulong source, CoreRender.TargetKind into )
	{
		if ( source == 0 || edges == 0 ) return false;

		var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( push.AsSpan() );
		floats[0] = 1.0f / width;
		floats[1] = 1.0f / height;
		floats[2] = width;
		floats[3] = height;

		if ( edgesFrom != source )
		{
			if ( edgesReads != 0 ) CoreRenderDevice.DestroyTexture( edgesReads );
			if ( blendReads != 0 ) CoreRenderDevice.DestroyTexture( blendReads );

			edgesReads = CoreRenderDevice.CreateMaterial( source, source, source, source, 1, 0, 0, 0 );
			blendReads = CoreRenderDevice.CreateMaterial( source, Texture( weights ), source, source, 1, 0, 0, 0 );
			edgesFrom = edgesReads != 0 && blendReads != 0 ? source : 0;
		}

		if ( edgesFrom == 0 ) return false;

		CoreRenderDevice.ZoneBegin( "smaa" );

		try
		{
			if ( CoreRenderDevice.PortableFrame )
			{
				if ( !Drawn( edgesPicturePipeline, edges, edgesReads ) ) return false;
				if ( !Drawn( weightsPicturePipeline, weights, weightsReads ) ) return false;
			}
			else
			{
				CoreRenderDevice.ReadyToRead( source );

				if ( !Dispatch( edgesPipeline, edgesReads, edges ) ) return false;
				if ( !Dispatch( weightsPipeline, weightsReads, weights ) ) return false;
			}

			var blend = into == CoreRender.TargetKind.Screen ? blendScreenPipeline : blendFinalPipeline;
			if ( blend == 0 ) return false;

			//Every pixel is written, so nothing of what the target held is read in first
			var opened = into != CoreRender.TargetKind.Screen;

			if ( opened && !CoreRenderDevice.BeginTarget( into, Color.Transparent, clearFirst: false, discard: true ) ) return false;

			CoreRenderDevice.Draw( blend, 3, push, blendReads );

			if ( opened ) CoreRenderDevice.EndTarget();

			return true;
		}
		finally
		{
			CoreRenderDevice.ZoneEnd();
		}
	}

	//A portable pass into its picture, cleared first: the shader writes only where it found something
	private bool Drawn( ulong pipeline, ulong into, ulong reads )
	{
		if ( pipeline == 0 || !CoreRenderDevice.BeginPicture( into, 0, Color.Transparent, clearFirst: true ) ) return false;

		CoreRenderDevice.Draw( pipeline, 3, push, reads );
		CoreRenderDevice.EndTarget();
		return true;
	}

	private bool Dispatch( ulong pipeline, ulong reads, ulong into )
	{
		Span<ulong> outputs = stackalloc ulong[1] { into };
		Span<int> levels = stackalloc int[1] { 0 };

		return CoreRenderDevice.DispatchPass( pipeline, reads, outputs, levels, ReadOnlySpan<ulong>.Empty, push,
			( width + 7 ) / 8, ( height + 7 ) / 8 );
	}

	private static ulong Texture( ulong picture ) => CoreRenderDevice.PictureTexture( picture );

	/// <summary>Gives the pictures back and drops the materials naming them.</summary>
	internal void Release()
	{
		foreach ( var material in new[] { edgesReads, weightsReads, blendReads } )
			if ( material != 0 ) CoreRenderDevice.DestroyTexture( material );

		CoreRenderDevice.ReleasePicture( edges );
		CoreRenderDevice.ReleasePicture( weights );
		CoreRenderDevice.ReleasePicture( finished );

		edges = weights = finished = 0;
		edgesReads = weightsReads = blendReads = 0;
		edgesFrom = 0;
		width = height = 0;
	}

	//-- shared by every drawer: the two tables and the passes -----------------------------------

	private static ulong area, search;
	private static ulong edgesPipeline, weightsPipeline, blendScreenPipeline, blendFinalPipeline;
	private static ulong edgesPicturePipeline, weightsPicturePipeline;
	private static byte[] vertex;
	private static int picturesFormat;
	private static int builtRevision = -1, builtDevice = -1;
	private static bool failed;

	//The compute build, which declares the picture it stores into - see smaa.slang
	internal const string ComputeDefines = "#define SMAA_COMPUTE 1\n";

	//The reference's tables: area 160x560 in two bytes a texel, search 64x16 in one
	private const int AreaWidth = 160, AreaHeight = 560, SearchWidth = 64, SearchHeight = 16;

	private static bool EnsureShared()
	{
		//The old device's handles went with it, and an edited shader asks for its passes again
		if ( builtDevice != CoreResources.Cleared )
		{
			ForgetPipelines();
			area = search = 0;
			builtDevice = CoreResources.Cleared;
		}
		else if ( builtRevision != CoreShaderCache.Revision )
		{
			DestroyPipelines();
		}

		if ( builtRevision == CoreShaderCache.Revision ) return !failed;

		builtRevision = CoreShaderCache.Revision;
		failed = !CoreNative.Available || !EnsureTables() || !Build();

		return !failed;
	}

	private static bool EnsureTables()
	{
		if ( area != 0 && search != 0 ) return true;

		var areaBytes = Embedded( "smaa_area.bin", AreaWidth * AreaHeight * 2 );
		var searchBytes = Embedded( "smaa_search.bin", SearchWidth * SearchHeight );

		if ( areaBytes is null || searchBytes is null )
		{
			Log.Warning( "SMAA's tables are not in the engine - edges are left as drawn" );
			return false;
		}

		//Eight bits a channel is what every device takes; the tables' own two and one go in red and green
		var texels = new byte[AreaWidth * AreaHeight * 4];

		for ( var i = 0; i < AreaWidth * AreaHeight; i++ )
		{
			texels[i * 4] = areaBytes[i * 2];
			texels[( i * 4 ) + 1] = areaBytes[( i * 2 ) + 1];
		}

		area = CoreRenderDevice.CreateTextureRgba( texels, AreaWidth, AreaHeight, srgb: false );

		texels = new byte[SearchWidth * SearchHeight * 4];

		for ( var i = 0; i < SearchWidth * SearchHeight; i++ ) texels[i * 4] = searchBytes[i];

		search = CoreRenderDevice.CreateTextureRgba( texels, SearchWidth, SearchHeight, srgb: false );

		return area != 0 && search != 0;
	}

	private static byte[] Embedded( string name, int length )
	{
		using var stream = typeof( CoreSmaa ).Assembly.GetManifestResourceStream( name );
		if ( stream is null ) return null;

		var bytes = new byte[length];
		return stream.ReadAtLeast( bytes, length, throwOnEndOfStream: false ) < length ? null : bytes;
	}

	//The blend onto the screen everywhere; the full frame's compute passes and blend into Final, or the portable
	//frame's passes into pictures, which wait for a picture to learn its format - see EnsurePortable
	private static bool Build()
	{
		vertex = CoreShaderCache.Pass( EngineShaders.Smaa, "MainVs", CoreShaderCompiler.Stage.Vertex );
		var blend = CoreShaderCache.Pass( EngineShaders.Smaa, "BlendPs", CoreShaderCompiler.Stage.Pixel );

		blendScreenPipeline = Screen( blend, CoreRender.TargetKind.Screen );

		if ( !CoreRenderDevice.PortableFrame )
		{
			blendFinalPipeline = Screen( blend, CoreRender.TargetKind.Final );
			edgesPipeline = Compute( "EdgesCs" );
			weightsPipeline = Compute( "WeightsCs" );
		}

		var built = blendScreenPipeline != 0 &&
			( CoreRenderDevice.PortableFrame || blendFinalPipeline != 0 && edgesPipeline != 0 && weightsPipeline != 0 );

		if ( !built ) Log.Warning( $"SMAA's passes did not build: {CoreRenderDevice.ShaderError}" );

		return built;
	}

	//The portable passes for the pictures' format, made again where it changes
	private bool EnsurePortable()
	{
		if ( !CoreRenderDevice.PictureShape( edges, out _, out _, out _, out var format ) ) return false;
		if ( format == picturesFormat && edgesPicturePipeline != 0 && weightsPicturePipeline != 0 ) return true;

		if ( edgesPicturePipeline != 0 ) CoreRenderDevice.DestroyPipeline( edgesPicturePipeline );
		if ( weightsPicturePipeline != 0 ) CoreRenderDevice.DestroyPipeline( weightsPicturePipeline );

		picturesFormat = format;
		edgesPicturePipeline = Picture( "EdgesPs", format );
		weightsPicturePipeline = Picture( "WeightsPs", format );

		if ( edgesPicturePipeline == 0 || weightsPicturePipeline == 0 )
		{
			Log.Warning( $"SMAA's passes did not build: {CoreRenderDevice.ShaderError}" );
			return false;
		}

		return true;
	}

	private static ulong Screen( byte[] pixel, CoreRender.TargetKind target )
		=> vertex is null || pixel is null ? 0 : CoreRenderDevice.CreatePipeline( vertex, pixel, CoreRender.VertexLayoutKind.None,
			depthTest: false, CoreRender.Topology.Triangles, blend: false, depthWrite: false, target: target );

	private static ulong Picture( string entry, int format )
	{
		var pixel = CoreShaderCache.Pass( EngineShaders.Smaa, entry, CoreShaderCompiler.Stage.Pixel );

		return vertex is null || pixel is null ? 0 : CoreRenderDevice.CreatePipeline( vertex, pixel, CoreRender.VertexLayoutKind.None,
			depthTest: false, CoreRender.Topology.Triangles, blend: false, depthWrite: false,
			target: CoreRender.TargetKind.Picture, pictureFormat: format );
	}

	private static ulong Compute( string entry )
	{
		var code = CoreShaderCache.Pass( EngineShaders.Smaa, entry, CoreShaderCompiler.Stage.Compute, ComputeDefines );
		return code is null ? 0 : CoreRenderDevice.CreatePassPipeline( code );
	}

	private static void DestroyPipelines()
	{
		foreach ( var pipeline in new[] { edgesPipeline, weightsPipeline, blendScreenPipeline, blendFinalPipeline,
			edgesPicturePipeline, weightsPicturePipeline } )
			if ( pipeline != 0 ) CoreRenderDevice.DestroyPipeline( pipeline );

		ForgetPipelines();
	}

	/// <summary>Drops the passes with the device that made them.</summary>
	internal static void ForgetPipelines()
	{
		edgesPipeline = weightsPipeline = blendScreenPipeline = blendFinalPipeline = 0;
		edgesPicturePipeline = weightsPicturePipeline = 0;
		picturesFormat = 0;
		vertex = null;
		builtRevision = -1;
		failed = false;
	}
}
