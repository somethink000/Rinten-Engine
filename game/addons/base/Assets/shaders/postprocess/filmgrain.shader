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

	//Seconds; the hash takes a new pattern each sixtieth
	float g_flTime < Attribute( "Time" ); Default( 0.0 ); >;
}

#include "postprocess.slang"

float3 Apply( float3 colour, float2 uv )
{
	if ( g_flIntensity <= 0.0 ) return colour;

	//An integer hash of pixel and frame: sin of pixel coordinates in the thousands lost its precision on the GPU and drew stripes
	uint3 v = uint3( uint2( uv * g_vFrameSize.xy ), uint( g_flTime * 60.0 ) );

	v = v * 1664525u + 1013904223u;
	v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
	v ^= v >> 16u;
	v.x += v.y * v.z;

	float noise = float( v.x >> 8u ) * ( 1.0 / 16777216.0 );

	float luma = dot( colour, float3( 0.299, 0.587, 0.114 ) );
	float shadow = lerp( 1.0, 1.0 - saturate( luma ), saturate( g_flResponse ) );

	return max( colour + ( ( noise - 0.5 ) * g_flIntensity * shadow ), 0.0 );
}
