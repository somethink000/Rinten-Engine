//The frame's sprites and tiles redrawn into the 2D light's scene (CoreLight2D): what blocks writes its coverage, what
//glows its own colour times its strength, and everything else nothing. Straight alpha over what is there, so an
//occluder in front of a glow hides it. LIGHT2D_TILE steps a tile map's animation the way tile_ps.shader does

#define LIGHT2D_SCENE 1

#include "sprite/sprite.slang"

//The bits of SpriteData.Lighting this reads - SpriteData.Occluder2D and Emission2DShift
static const uint Occluder2DFlag = 1u << 9;
static const uint Emission2DShift = 24u;

[shader("fragment")]
float4 MainPs( V2F i ) : SV_Target0
{
	SpriteData sprite = SpriteAt( i.sprite );

	bool occludes = ( sprite.Lighting & Occluder2DFlag ) != 0;
	float emission = float( ( sprite.Lighting >> Emission2DShift ) & 0xFFu ) / 16.0;

	if ( !occludes && emission <= 0.0 ) discard;

	float2 uv = i.uv;

#ifdef LIGHT2D_TILE
	if ( sprite.SheetX > 1.0 )
	{
		float frame = floor( fmod( EYE_FRAME( time ).z * sprite.SheetY, sprite.SheetX ) );
		float step = sprite.SheetZ * frame;

		uv.x += step;
		sprite.RegionX += step;
	}
#endif

	float2 dx = ddx( uv ), dy = ddy( uv );
	float2 size;

	Sampler2D picture = g_textures[NonUniformResourceIndex( uint( max( sprite.TextureHandle, 0 ) ) )];
	picture.GetDimensions( size.x, size.y );
	float4 texel = picture.SampleGrad( SpriteReadAt( sprite, uv, size ), dx, dy );

	float cutoff = float( ( sprite.FogStrengthCutout >> 16 ) & 0xFFFF ) / 65535.0;

	if ( texel.a < cutoff ) discard;

	//The tint as the draw's own stage reads it: a sprite's always RGBE, a tile's only past white
	float4 tint = UnpackRgba8( sprite.TintColor );
	uint exponent = ( sprite.Lighting >> 16 ) & 0xFF;

#ifdef LIGHT2D_TILE
	if ( exponent != 0 )
		tint.rgb = ( ( tint.rgb * 255.0 ) - 128.0 ) * exp2( float( exponent ) - 128.0 - 7.0 );
#else
	tint.rgb = ( exponent == 0 )
		? float3( 0, 0, 0 )
		: ( ( tint.rgb * 255.0 ) - 128.0 ) * exp2( float( exponent ) - 128.0 - 7.0 );
#endif

	float coverage = saturate( texel.a * tint.a );

	if ( coverage <= 0.0 ) discard;

	return float4( texel.rgb * tint.rgb * emission, coverage );
}
