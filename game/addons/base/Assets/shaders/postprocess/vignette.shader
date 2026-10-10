//Darker towards the edges, as a point effect - see Vignette. Laid after the curve, on the picture as the screen will show it.

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

FEATURES
{
}

PARAMETERS
{
	float3 g_vColour < Attribute( "Colour" ); Default3( 0.0, 0.0, 0.0 ); >;
	float g_flIntensity < Attribute( "Intensity" ); Default( 0.0 ); >;

	//Where its middle is, on the screen.
	float2 g_vCenter < Attribute( "Center" ); Default2( 0.5, 0.5 ); >;

	float g_flSmoothness < Attribute( "Smoothness" ); Default( 1.0 ); >;
	float g_flRoundness < Attribute( "Roundness" ); Default( 1.0 ); >;
}

#include "postprocess.slang"

float3 Apply( float3 colour, float2 uv )
{
	if ( g_flIntensity <= 0.0 ) return colour;

	//Round, or the shape of the frame: at one it is a circle however wide the window is; at nothing it
	//follows the window, which is what a wide screen wants if the corners are all that should darken.
	float2 away = ( uv - g_vCenter ) * 2.0;

	float aspect = g_vFrameSize.x / max( g_vFrameSize.y, 1.0 );
	away.x *= lerp( 1.0, aspect, saturate( g_flRoundness ) );

	float reach = length( away );
	float lit = smoothstep( 0.0, max( g_flSmoothness, 0.02 ), 1.0 - ( reach * 0.75 ) );

	return lerp( g_vColour, colour, lerp( 1.0, lit, saturate( g_flIntensity ) ) );
}
