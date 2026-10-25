//Contact shadows: a short march from each pixel towards the sun over the depth, nought where something covers the way -
//the small shadows the sun's maps are too coarse to hold. SunShadow reads it - see CoreContactShadows

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

FEATURES
{
}

PARAMETERS
{
	//How far towards the sun to look, as a share of the pixel's distance, and the least and most of it in metres.
	float g_flShare < Attribute( "Share" ); Default( 0.02 ); >;
	float g_flLeast < Attribute( "Least" ); Default( 0.15 ); >;
	float g_flMost < Attribute( "Most" ); Default( 1.5 ); >;

	//Reads along the way.
	float g_flSteps < Attribute( "Steps" ); Default( 12.0 ); >;
}

#include "postprocess.slang"

//Where a pixel of the depth is, in the world.
float3 PlaceOf( float2 uv, float depth )
{
	float4 clip = float4( ( uv * 2.0 ) - 1.0, depth, 1.0 );
	float4 world = mul( clip, EYE_FRAME( clipToWorld ) );

	return world.xyz / max( world.w, 0.000001 );
}

//A depth as written made metres ahead of the eye.
float Ahead( float raw )
{
	float near = g_vFrameRange.x;
	float far = g_vFrameRange.y;

	return near * far / max( far - raw * ( far - near ), 1e-6 );
}

[shader("fragment")]
float4 MainPs( PostV2F i ) : SV_Target0
{
	float raw = SceneDepthRaw( i.uv );

	//The sky, or no sun to cast: nothing covers it.
	if ( raw >= 1.0 || !HasSun() ) return float4( 1.0, 1.0, 1.0, 1.0 );

	float3 place = PlaceOf( i.uv, raw );
	float3 eye = EYE_FRAME( camera ).xyz;
	float away = distance( place, eye );
	float3 sun = normalize( g_frame[0].sunDirection.xyz );

	//Longer with distance; started a pixel towards the eye so it does not meet its own surface, and at a turn of its
	//own a pixel so neighbours share the steps between them
	float reach = clamp( away * g_flShare, g_flLeast, g_flMost );
	float pixel = away * 2.0 / max( g_vFrameSize.y, 1.0 );
	float3 from = place + ( normalize( eye - place ) * pixel );

	uint2 cell = uint2( i.position.xy ) & 3u;
	float jitter = ( float( ( cell.x * 4u + cell.y * 11u ) & 15u ) + 0.5 ) / 16.0;

	int steps = clamp( int( g_flSteps ), 4, 32 );

	//How deep something in front may be and still cast: thin, as a stone or a blade is, and growing with the way
	float thick = max( reach * 0.5, pixel * 4.0 );

	for ( int k = 0; k < steps; k++ )
	{
		float t = ( float( k ) + jitter ) / float( steps );
		float3 at = from + ( sun * ( reach * t ) );

		float4 clip = mul( float4( at, 1.0 ), EYE_FRAME( worldToClip ) );
		if ( clip.w <= 0.0 ) break;

		float2 uv = ( ( clip.xy / clip.w ) * 0.5 ) + 0.5;
		if ( any( uv < 0.0 ) || any( uv > 1.0 ) ) break;

		float ray = Ahead( clip.z / clip.w );
		float there = Ahead( SceneDepthRaw( uv ) );
		float front = ray - there;

		//Something nearer the eye than the way, by more than a step's slack and less than the depth a caster may have
		if ( front > pixel * 1.5 && front < thick )
		{
			//Fading over the last of the way, so where the march gives out is not a hard line
			return saturate( t * 3.0 - 2.0 ).xxxx;
		}
	}

	return float4( 1.0, 1.0, 1.0, 1.0 );
}
