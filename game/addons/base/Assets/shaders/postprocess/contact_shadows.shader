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
	float g_flShare < Attribute( "Share" ); Default( 0.04 ); >;
	float g_flLeast < Attribute( "Least" ); Default( 0.3 ); >;
	float g_flMost < Attribute( "Most" ); Default( 3.0 ); >;

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

//The surface's own plane from how the place moves across one pixel, turned to the eye - see ambient_occlusion.shader
float3 PlaneOf( float3 place, float3 view )
{
	float3 normal = cross( ddx( place ), ddy( place ) );
	float size = dot( normal, normal );

	normal = size > 1e-20 && size < 1e20 ? normal * rsqrt( size ) : view;
	return dot( normal, view ) < 0.0 ? -normal : normal;
}

//The depth at a texel's middle: a read between texels blends a near edge with what is behind it, a caster that is not there
float DepthAt( float2 uv, float2 size )
{
	return SceneDepthRaw( ( floor( uv * size ) + 0.5 ) / size );
}

[shader("fragment")]
float4 MainPs( PostV2F i ) : SV_Target0
{
	float raw = SceneDepthRaw( i.uv );
	float2 size = max( g_vFrameSize.xy, 1.0 );

	float3 place = PlaceOf( i.uv, raw );
	float3 eye = EYE_FRAME( camera ).xyz;
	float3 view = normalize( eye - place );
	float3 normal = PlaneOf( place, view );

	//With how far it is, so a surface drawn over it later - water, glass - is not shaded by what lies under it
	float away = distance( place, eye );

	//The sky, no sun, or a surface turned from it, which its light leaves in shade already
	float3 sun = normalize( g_frame[0].sunDirection.xyz );
	if ( raw >= 1.0 || !HasSun() || dot( normal, sun ) < 0.05 ) return float4( 1.0, away, 0.0, 0.0 );

	//A pixel's width there, across the view
	float pixel = distance( PlaceOf( i.uv + float2( 1.0 / size.x, 0.0 ), raw ), place );
	float reach = clamp( away * g_flShare, g_flLeast, g_flMost );

	//Off the surface along its own normal, so the way never meets the plane it starts on
	float3 from = place + ( normal * ( pixel * 2.0 ) );

	int steps = clamp( int( g_flSteps ), 4, 32 );

	//How deep something in front may be and still cast: thin, as a stone or a blade is, and growing with the way
	float thick = max( reach * 0.5, pixel * 4.0 );

	for ( int k = 0; k < steps; k++ )
	{
		float t = ( float( k ) + 1.0 ) / float( steps );
		float3 at = from + ( sun * ( reach * t ) );

		float4 clip = mul( float4( at, 1.0 ), EYE_FRAME( worldToClip ) );
		if ( clip.w <= 0.0 ) break;

		float2 uv = ( ( clip.xy / clip.w ) * 0.5 ) + 0.5;
		if ( any( uv < 0.0 ) || any( uv > 1.0 ) ) break;

		float front = Ahead( clip.z / clip.w ) - Ahead( DepthAt( uv, size ) );

		//In front of the way by more than the slack a step's depth carries, and less than a caster may be thick
		if ( front > ( pixel * 2.0 ) + ( reach * t * 0.03 ) && front < thick )
		{
			//Fading over the last of the way, so where the march gives out is not a hard line
			return float4( saturate( t * 3.0 - 2.0 ), away, 0.0, 0.0 );
		}
	}

	return float4( 1.0, away, 0.0, 0.0 );
}
