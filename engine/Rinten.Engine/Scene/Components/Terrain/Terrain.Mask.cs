using NativeEngine;

namespace Rinten;

public partial class Terrain
{
	//The paint mask on the card: the mesh's tiles in an atlas and a table of where each is, read by MaskInto
	//in terrain_layers.slang. The portable frame reads no mask
	private Texture maskAtlas, maskTable;
	private TerrainMesh maskOf;

	//Which slot of the atlas each tile is in, and the slots let go of.
	private readonly Dictionary<long, int> maskSlots = new();
	private readonly Stack<int> maskFree = new();
	private int maskNextSlot;
	private int maskAtlasRows;

	//The tile at the table's corner, and how many it is across and deep.
	private int maskOriginX, maskOriginZ, maskWide, maskDeep;
	private byte[] maskTableBytes;

	private readonly List<long> maskChanged = new();
	private byte[] maskTexels;

	//How many tiles stand side by side in the atlas - the shader's MaskAtlasSlots.
	private const int MaskAtlasSlots = 32;

	//Texels a mask point takes: its paint in 32 bits (PackPoint) and its top's float as four bytes
	//Bytes, as a picture made here is 8-bit or half a channel on the card, neither exact for a layer or a height
	private const int MaskPointTexels = 2;

	//Tiles of room left round what is painted when the table is laid out, so a stroke past its edge does not lay it out again.
	private const int MaskTableRoom = 4;

	//What is painted on the mask since the last frame, onto the card.
	private void PumpMask()
	{
		if ( Application.IsHeadless || CoreRenderDevice.PortableFrame || !CoreNative.Available ) return;

		if ( Storage?.Mesh is not { } mesh )
		{
			DropMask();
			return;
		}

		maskChanged.Clear();

		if ( !ReferenceEquals( maskOf, mesh ) )
		{
			DropMask();
			maskOf = mesh;
			maskChanged.AddRange( mesh.MaskKeys );
		}

		mesh.TakeMaskChanged( maskChanged );

		if ( maskChanged.Count == 0 )
		{
			SayMask();
			return;
		}

		var tableStale = false;
		var atlasStale = false;

		foreach ( var key in maskChanged )
		{
			var there = mesh.MaskTile( key ) is not null;
			var had = maskSlots.TryGetValue( key, out var slot );

			if ( there == had ) continue;

			tableStale = true;

			if ( had )
			{
				maskSlots.Remove( key );
				maskFree.Push( slot );
				continue;
			}

			slot = maskFree.Count > 0 ? maskFree.Pop() : maskNextSlot++;
			maskSlots[key] = slot;

			if ( slot >= MaskAtlasSlots * maskAtlasRows ) atlasStale = true;
		}

		//A table that does not reach every tile is laid out again round them all.
		var outside = false;

		foreach ( var key in maskSlots.Keys )
		{
			var x = TerrainMesh.MaskKeyX( key ) - maskOriginX;
			var z = TerrainMesh.MaskKeyZ( key ) - maskOriginZ;

			if ( x >= 0 && z >= 0 && x < maskWide && z < maskDeep ) continue;

			outside = true;
			break;
		}

		if ( outside || ( maskTable is null && maskSlots.Count > 0 ) ) LayOutMaskTable();

		if ( atlasStale || ( maskAtlas is null && maskSlots.Count > 0 ) )
		{
			GrowMaskAtlas();

			//A new atlas holds nothing yet: every tile goes onto it.
			maskChanged.Clear();
			maskChanged.AddRange( maskSlots.Keys );
		}

		if ( maskAtlas is null || maskTable is null ) return;

		foreach ( var key in maskChanged )
		{
			if ( !maskSlots.TryGetValue( key, out var slot ) || mesh.MaskTile( key ) is not { } tile ) continue;

			UploadMaskTile( slot, tile );
		}

		if ( tableStale || outside ) WriteMaskTable();

		SayMask();
	}

	//The mask's two pictures and where its table starts and how far it runs, as the ground's draws were last told them.
	private Vector2 maskPictures;
	private Vector4 maskPlace;

	//Tells every draw of the ground where the mask is, where that changed - see g_vPalette and g_vMask in the terrain shader.
	private void SayMask()
	{
		var pictures = maskAtlas is not null && maskTable is not null && maskSlots.Count > 0
			? new Vector2( maskTable.Index, maskAtlas.Index )
			: Vector2.Zero;

		var place = new Vector4( maskOriginX, maskOriginZ, maskWide, maskDeep );

		if ( pictures == maskPictures && place == maskPlace ) return;

		maskPictures = pictures;
		maskPlace = place;

		foreach ( var chunk in chunks )
		{
			if ( chunk.Ground.IsValid() ) SayPaint( chunk.Ground, chunk.Palette );
			if ( chunk.FarGround.IsValid() ) SayPaint( chunk.FarGround, chunk.Palette );
		}

		if ( relief is not null ) SayPaint( relief.Attributes, default );
	}

	//A draw of the ground's palette, and where the mask is.
	private void SayPaint( SceneObject ground, Vector4 palette ) => SayPaint( ground.Attributes, palette );

	private void SayPaint( RenderAttributes attributes, Vector4 palette )
	{
		attributes.Set( "TerrainPalette", new Vector4( palette.x, palette.y, maskPictures.x, maskPictures.y ) );
		attributes.Set( "TerrainMask", maskPlace );
	}

	//Round every tile there is, with room to spare.
	private void LayOutMaskTable()
	{
		int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;

		foreach ( var key in maskSlots.Keys )
		{
			x0 = Math.Min( x0, TerrainMesh.MaskKeyX( key ) );
			z0 = Math.Min( z0, TerrainMesh.MaskKeyZ( key ) );
			x1 = Math.Max( x1, TerrainMesh.MaskKeyX( key ) );
			z1 = Math.Max( z1, TerrainMesh.MaskKeyZ( key ) );
		}

		if ( maskSlots.Count == 0 ) return;

		maskOriginX = x0 - MaskTableRoom;
		maskOriginZ = z0 - MaskTableRoom;
		maskWide = Math.Min( x1 - x0 + 1 + ( MaskTableRoom * 2 ), 4096 );
		maskDeep = Math.Min( z1 - z0 + 1 + ( MaskTableRoom * 2 ), 4096 );

		maskTable?.Dispose();
		maskTable = Texture.Create( maskWide, maskDeep, ImageFormat.LINEAR_RGBA8888 )
			.WithName( "terrain_mask_table" )
			.Finish();

		maskTableBytes = new byte[maskWide * maskDeep * 4];
	}

	//Room for every slot handed out, in rows of tiles a power of two deep.
	private void GrowMaskAtlas()
	{
		var rows = Math.Max( maskAtlasRows, 4 );
		while ( rows * MaskAtlasSlots < maskNextSlot ) rows *= 2;

		if ( rows == maskAtlasRows && maskAtlas is not null ) return;

		maskAtlasRows = rows;

		maskAtlas?.Dispose();
		maskAtlas = Texture.Create( MaskAtlasSlots * TerrainMaskTile.Side * MaskPointTexels, rows * TerrainMaskTile.Side, ImageFormat.LINEAR_RGBA8888 )
			.WithName( "terrain_mask_atlas" )
			.Finish();
	}

	//A tile's points as the shader reads them - see MaskPointTexels.
	private void UploadMaskTile( int slot, TerrainMaskTile tile )
	{
		const int side = TerrainMaskTile.Side;

		maskTexels ??= new byte[side * side * MaskPointTexels * 4];

		var bytes = maskTexels.AsSpan();

		for ( var at = 0; at < side * side; at++ )
		{
			var point = bytes.Slice( at * MaskPointTexels * 4, MaskPointTexels * 4 );

			System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian( point, PackPoint( tile.Paint[at] ) );
			System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian( point[4..], tile.Top[at] );
		}

		maskAtlas.Update( new ReadOnlySpan<byte>( maskTexels ), ( slot % MaskAtlasSlots ) * side * MaskPointTexels, ( slot / MaskAtlasSlots ) * side,
			side * MaskPointTexels, side );
	}

	//Three heaviest layers in five bits each, two weights in a byte each (the third's the rest), top bit where painted
	//A rare fourth layer is dropped on the card
	private static uint PackPoint( TerrainPaint paint )
	{
		if ( !paint.Painted ) return 0u;

		var lightest = 0;
		for ( var s = 1; s < TerrainPaint.Most; s++ ) if ( paint.WeightAt( s ) < paint.WeightAt( lightest ) ) lightest = s;

		Span<int> layers = stackalloc int[3];
		Span<float> weights = stackalloc float[3];

		var kept = 0;
		var total = 0.0f;

		for ( var s = 0; s < TerrainPaint.Most; s++ )
		{
			if ( s == lightest ) continue;

			layers[kept] = paint.LayerAt( s ) & 31;
			weights[kept] = paint.WeightAt( s );
			total += weights[kept];
			kept++;
		}

		if ( total <= 0.0f ) return 0u;

		var first = (uint)Math.Clamp( MathF.Round( weights[0] / total * 255.0f ), 0.0f, 255.0f );
		var second = (uint)Math.Clamp( MathF.Round( weights[1] / total * 255.0f ), 0.0f, 255.0f - first );

		return ( 1u << 31 ) | (uint)layers[0] | ( (uint)layers[1] << 5 ) | ( (uint)layers[2] << 10 ) | ( first << 15 ) | ( second << 23 );
	}

	private void WriteMaskTable()
	{
		Array.Clear( maskTableBytes );

		foreach ( var (key, slot) in maskSlots )
		{
			var x = TerrainMesh.MaskKeyX( key ) - maskOriginX;
			var z = TerrainMesh.MaskKeyZ( key ) - maskOriginZ;

			if ( x < 0 || z < 0 || x >= maskWide || z >= maskDeep ) continue;

			var at = ( ( z * maskWide ) + x ) * 4;
			var held = slot + 1;

			maskTableBytes[at] = (byte)( held & 0xFF );
			maskTableBytes[at + 1] = (byte)( ( held >> 8 ) & 0xFF );
		}

		maskTable.Update( new ReadOnlySpan<byte>( maskTableBytes ) );
	}

	private void DropMask()
	{
		maskAtlas?.Dispose();
		maskTable?.Dispose();
		maskAtlas = null;
		maskTable = null;
		maskOf = null;

		maskSlots.Clear();
		maskFree.Clear();
		maskNextSlot = 0;
		maskAtlasRows = 0;
		maskWide = maskDeep = 0;
		maskTableBytes = null;

		//What the draws were told stays as it is, so the next frame sees it differs from nothing and tells them again.
	}

	//Where row 0 says the mask is - see MaskInto. Nought pictures where there is no mask.
	private void FillMask()
	{
		if ( maskAtlas is null || maskTable is null || maskSlots.Count == 0 ) return;

		WriteShorts( 7, RowTerrain, maskTable.Index, maskAtlas.Index );
		WriteBits( 8, RowTerrain, unchecked((uint)maskOriginX) );
		WriteBits( 9, RowTerrain, unchecked((uint)maskOriginZ) );
		WriteShorts( 10, RowTerrain, maskWide, maskDeep );
	}
}
