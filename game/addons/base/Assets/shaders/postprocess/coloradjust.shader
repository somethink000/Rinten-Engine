//Brightness, contrast, saturation and hue as a point effect, applied after exposure

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

FEATURES
{
}

PARAMETERS
{
	float g_flBlend < Attribute( "Blend" ); Default( 1.0 ); >;
	float g_flSaturation < Attribute( "Saturation" ); Default( 1.0 ); >;

	//In radians.
	float g_flHue < Attribute( "Hue" ); Default( 0.0 ); >;

	float g_flBrightness < Attribute( "Brightness" ); Default( 1.0 ); >;
	float g_flContrast < Attribute( "Contrast" ); Default( 1.0 ); >;
}

#include "postprocess.slang"

//The colour turned about the grey axis, which is what rotating a hue is: Rodrigues' rotation about (1,1,1), written out.
float3 Turned( float3 colour, float radians )
{
	float c = cos( radians );
	float s = sin( radians );

	float one = ( 1.0 - c ) / 3.0;
	float side = sqrt( 1.0 / 3.0 ) * s;

	float3x3 turn = float3x3(
		c + one,        one - side,     one + side,
		one + side,     c + one,        one - side,
		one - side,     one + side,     c + one );

	return mul( colour, turn );
}

float3 Apply( float3 colour, float2 uv )
{
	float blend = saturate( g_flBlend );

	if ( blend <= 0.0 ) return colour;

	float exposure = max( Exposure(), 1e-8 );
	float3 was = colour * exposure;

	float3 adjusted = was * g_flBrightness;

	//About the middle grey, so raising the contrast darkens what is dark as much as it brightens what is bright.
	adjusted = ( ( adjusted - 0.5 ) * g_flContrast ) + 0.5;

	float luma = dot( adjusted, float3( 0.2126, 0.7152, 0.0722 ) );
	adjusted = lerp( float3( luma, luma, luma ), adjusted, g_flSaturation );

	if ( abs( g_flHue ) > 0.001 ) adjusted = Turned( adjusted, g_flHue );

	return max( lerp( was, adjusted, blend ), 0.0 ) / exposure;
}
