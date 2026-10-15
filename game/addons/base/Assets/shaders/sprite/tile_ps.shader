//Tile map pixel stage: sprite records, animated via BlendSheetUV (X frames, Y fps, Z step)

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

#include "sprite/sprite.slang"
#include "picture_size.slang"

[shader("fragment")]
Out MainPs( V2F i )
{
	STEREO_EYE_IN( i );

	SpriteData tile = SpriteAt( i.sprite );

	float2 uv = i.uv;

	if ( tile.SheetX > 1.0 )
	{
		//The frame now, and everything read moved along the row to it - the region the read is held inside too, or the frame is clamped back to the first.
		float frame = floor( fmod( EYE_FRAME( time ).z * tile.SheetY, tile.SheetX ) );
		float step = tile.SheetZ * frame;

		uv.x += step;
		tile.RegionX += step;
	}

	float2 dx = ddx( uv ), dy = ddy( uv );
	float2 size;

#ifdef PORTABLE
	size = float2( PictureSize( g_color ) );
	float4 texel = g_color.SampleGrad( SpriteReadAt( tile, uv, size ), dx, dy );
#else
	Sampler2D picture = g_textures[NonUniformResourceIndex( uint( max( tile.TextureHandle, 0 ) ) )];
	picture.GetDimensions( size.x, size.y );
	float4 texel = picture.SampleGrad( SpriteReadAt( tile, uv, size ), dx, dy );
#endif

	//Cut on the picture's own alpha: the cut-out is the tile's shape. The tint's alpha is how see-through
	//the whole layer is, and cut with it a layer tinted half see-through was cut away entirely.
	float cutoff = float( ( tile.FogStrengthCutout >> 16 ) & 0xFFFF ) / 65535.0;

	if ( texel.a < cutoff ) discard;

	//The tile's own tint and its layer's, multiplied together by the host into four plain bytes
	//- or, past white, into RGBE with the exponent in the top half of Lighting, as a sprite's is.
	float4 tint = UnpackRgba8( tile.TintColor );
	uint exponent = ( tile.Lighting >> 16 ) & 0xFF;

	if ( exponent != 0 )
		tint.rgb = ( ( tint.rgb * 255.0 ) - 128.0 ) * exp2( float( exponent ) - 128.0 - 7.0 );

	texel *= tint;

	//Lit by the scene's 2D lighting, where the layer asks to be - see TileMapLayer.Lit2D
	if ( ( tile.Lighting & Lit2DFlag ) != 0 )
		texel.rgb *= Light2DAt( i.world );

#ifdef DEPTH_ONLY
	Out nothing = (Out)0;
	return nothing;
#endif

	Out o;
	o.colour = texel;

#ifdef TARGET_SCENE
	o.surface = float4( 0.5, 0.5, 1.0, 0.0 );
#endif

#ifdef TARGET_SCREEN
	o.colour = float4( FinishToScreen( o.colour.rgb ), o.colour.a );
#endif

	return o;
}
