//A film's grain, as a point effect - see FilmGrain. Laid last, over the finished picture, a different
//pattern every frame - or it is a texture stuck to the screen rather than something happening on it.

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

FEATURES
{
}

PARAMETERS
{
	float g_flIntensity < Attribute( "Intensity" ); Default( 0.0 ); >;

	//How much the grain keeps to the shadows: a bright frame shows almost none.
	float g_flResponse < Attribute( "Response" ); Default( 0.5 ); >;

	//Seconds, kept small so the hash below does not run out of precision.
	float g_flTime < Attribute( "Time" ); Default( 0.0 ); >;
}

#include "postprocess.slang"

float3 Apply( float3 colour, float2 uv )
{
	if ( g_flIntensity <= 0.0 ) return colour;

	float2 at = ( uv * g_vFrameSize.xy ) + g_flTime;

	float noise = frac( sin( dot( at, float2( 12.9898, 78.233 ) ) ) * 43758.5453 );

	float luma = dot( colour, float3( 0.299, 0.587, 0.114 ) );
	float shadow = lerp( 1.0, 1.0 - saturate( luma ), saturate( g_flResponse ) );

	return max( colour + ( ( noise - 0.5 ) * g_flIntensity * shadow ), 0.0 );
}
