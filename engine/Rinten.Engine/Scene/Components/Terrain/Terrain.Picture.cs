namespace Rinten;

public partial class Terrain
{
	//The ground picture on the card: each chunk's (TerrainChunkBaker.Picture) in an atlas laid out by chunk number, read by
	//PictureInto in terrain_layers.slang. The mask's base: the mask paints over it, and the portable frame reads neither
	private Texture pictureAtlas;
	private int picturePerRow, pictureRows;

	//The widest a picture may be, in texels; past it the chunks wrap onto more rows
	private const int MostPictureTexels = 16384;

	//Texels a point takes: its paint, then its top and normal
	private const int PicturePointTexels = TerrainChunkBaker.PicturePointBytes / 4;

	//An atlas for the chunks as they are laid out now, holding nothing until each chunk stands.
	private void LayOutPicture()
	{
		DropPicture();

		if ( Application.IsHeadless || CoreRenderDevice.PortableFrame || !CoreNative.Available || chunks.Length == 0 ) return;

		const int side = TerrainChunkBaker.PictureSide;

		picturePerRow = Math.Max( 1, Math.Min( chunks.Length, MostPictureTexels / ( side * PicturePointTexels ) ) );
		pictureRows = ( chunks.Length + picturePerRow - 1 ) / picturePerRow;

		if ( pictureRows * side > MostPictureTexels ) return;

		var wide = picturePerRow * side * PicturePointTexels;
		var tall = pictureRows * side;

		pictureAtlas = Texture.Create( wide, tall, ImageFormat.LINEAR_RGBA8888 )
			.WithName( "terrain_picture_atlas" )
			.Finish();

		//No ground anywhere until a chunk says otherwise - a nought point would say ground at nought, faced every way
		var empty = new byte[wide * tall * 4];
		TerrainChunkBaker.NoGroundPoint( empty );
		pictureAtlas?.Update( new ReadOnlySpan<byte>( empty ) );

		//The chunks' pictures go straight onto the card from here on, so the copy of a whole map's worth here goes once it is there
		pictureShed = false;
		pictureCleared = CoreResources.Cleared;

		UpdateParamsMap();
	}

	//Whether the atlas has let go of its pixels here, and the device it was laid out on - see UploadPicture
	private bool pictureShed;
	private int pictureCleared;

	//A chunk's picture onto the card, where its shape is new to it; the bake lets go of it then.
	private void UploadPicture( Chunk chunk, int index, TerrainChunkBake result )
	{
		if ( result.Picture is not { } bytes ) return;

		result.Picture = null;

		if ( pictureAtlas is null || index < 0 || chunk.PictureShaped == result.Shaped ) return;

		//The device went and took the atlas with it: laid out again, and every chunk's picture is made again by its next bake
		if ( pictureShed && pictureCleared != CoreResources.Cleared )
		{
			LayOutPicture();
			foreach ( var each in chunks ) each.PictureShaped = -1;
			if ( pictureAtlas is null ) return;
		}

		const int side = TerrainChunkBaker.PictureSide;

		var x = ( index % picturePerRow ) * side * PicturePointTexels;
		var y = ( index / picturePerRow ) * side;
		var width = side * PicturePointTexels;

		if ( !pictureShed )
		{
			pictureAtlas.core.ShedData();
			pictureShed = pictureAtlas.core.Data is { Length: 0 };
		}

		if ( pictureShed )
		{
			if ( !pictureAtlas.core.WriteThrough( bytes, width * 4, x, y, width, side ) ) return;
		}
		else
		{
			pictureAtlas.Update( new ReadOnlySpan<byte>( bytes ), x, y, width, side );
		}

		chunk.PictureShaped = result.Shaped;
	}

	private void DropPicture()
	{
		pictureAtlas?.Dispose();
		pictureAtlas = null;
		picturePerRow = pictureRows = 0;

		foreach ( var chunk in chunks ) chunk.PictureShaped = -1;
	}

	//Where row 0 says the picture is, from column 11 - see GroundPictureOfParams.
	private void FillPicture()
	{
		var told = PictureTold();

		for ( var k = 0; k < 4; k++ ) WriteFloat( 11 + k, RowTerrain, told[k] );
	}

	//Where the picture is as four whole numbers, each exact in a float - see GroundPictureOf: the atlas and chunks a row,
	//chunks across and deep, their side, the first chunk's corner in chunks. Nought where there is none
	private Vector4 PictureTold()
	{
		if ( pictureAtlas is null || chunkSpan <= 0.0f ) return Vector4.Zero;

		var x = (int)MathF.Round( chunkOrigin.x / chunkSpan ) + 2048;
		var z = (int)MathF.Round( chunkOrigin.y / chunkSpan ) + 2048;

		if ( pictureAtlas.Index > 65535 || chunksWide > 4095 || chunksDeep > 4095 || x is < 0 or > 4095 || z is < 0 or > 4095 ) return Vector4.Zero;

		return new Vector4( ( pictureAtlas.Index << 8 ) | picturePerRow, ( chunksWide << 12 ) | chunksDeep, chunkSpan, ( x << 12 ) | z );
	}
}
