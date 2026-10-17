//Adds SSR into the world picture by blend; upsamples weighting each sample by plane match

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

STATE
{
	Blend( Additive );
}

FEATURES
{
}

PARAMETERS
{
	//What reflection.shader resolved, by its place in the table.
	float g_flReflection < Attribute( "Reflection" ); >;

	//How much of a mirror a mirror is.
	float g_flStrength < Attribute( "Strength" ); Default( 1.0 ); >;
}

#include "postprocess.slang"

//Where a pixel of the depth is, in the world.
float3 PlaceOf( float2 uv, float depth )
{
	float4 world = mul( float4( ( uv * 2.0 ) - 1.0, depth, 1.0 ), EYE_FRAME( clipToWorld ) );

	return world.xyz / max( world.w, 0.000001 );
}

[shader("fragment")]
float4 MainPs( PostV2F i ) : SV_Target0
{
	//What is no mirror is left alone rather than added nought to: the blend reads and writes every pixel it is handed, and most of a frame is not a mirror.
	float4 surface = SceneSurface( i.uv );
	float strength = surface.a * g_flStrength;
	if ( strength <= 0.01 || g_flReflection <= 0.0 ) discard;

	float depth = SceneDepthRaw( i.uv );
	if ( depth >= 1.0 ) discard;

	float3 camera = EYE_FRAME( camera ).xyz;
	float3 place = PlaceOf( i.uv, depth );
	float3 normal = normalize( ( surface.rgb * 2.0 ) - 1.0 );
	float plane = dot( camera - place, normal );

	//How far off this pixel's plane the one a reflection was traced from may lie and still
	//count: a few centimetres near, more with the distance, as a texel of the depth grows.
	float within = 0.02 + ( 0.01 * length( camera - place ) );

	uint wide, tall, levels;
	g_textures[uint( g_flReflection )].GetDimensions( 0u, wide, tall, levels );
	float2 size = float2( float( wide ), float( tall ) );

	float2 at = ( i.uv * size ) - 0.5;
	float2 base = floor( at );
	float2 f = at - base;

	//The four neighbours' planes in one GatherAlpha (x lower left, then clockwise)
	float2 corner = ( base + 1.0 ) / size;
	float4 planes = g_textures[uint( g_flReflection )].GatherAlpha( corner );
	float4 off = abs( planes - plane );
	float4 share = float4( ( 1.0 - f.x ) * f.y, f.x * f.y, f.x * ( 1.0 - f.y ), ( 1.0 - f.x ) * ( 1.0 - f.y ) );
	float4 w = share * saturate( 1.0 - ( off / within ) ) * float4( planes >= 0.0 );

	float3 reflection;

	if ( all( off < within * 0.05 ) && all( planes >= 0.0 ) )
	{
		reflection = FramePicture( g_flReflection, i.uv ).rgb;
	}
	else
	{
		float3 sum = 0.0;
		sum += FramePicture( g_flReflection, corner + ( float2( -0.5, 0.5 ) / size ) ).rgb * w.x;
		sum += FramePicture( g_flReflection, corner + ( float2( 0.5, 0.5 ) / size ) ).rgb * w.y;
		sum += FramePicture( g_flReflection, corner + ( float2( 0.5, -0.5 ) / size ) ).rgb * w.z;
		sum += FramePicture( g_flReflection, corner + ( float2( -0.5, -0.5 ) / size ) ).rgb * w.w;

		float weight = dot( w, 1.0.xxxx );

		//None of the four in this plane by its share - a strip of mirror narrower than a pixel of the reflection - takes the one nearest it, if near at all.
		float4 near = off + ( float4( planes < 0.0 ) * 1e9 );
		float least = min( min( near.x, near.y ), min( near.z, near.w ) );
		float2 pick = least == near.x ? float2( -0.5, 0.5 ) : least == near.y ? float2( 0.5, 0.5 )
			: least == near.z ? float2( 0.5, -0.5 ) : float2( -0.5, -0.5 );

		reflection = weight > 1e-4 ? sum / weight
			: least < within * 4.0 ? FramePicture( g_flReflection, corner + ( pick / size ) ).rgb : 0.0;
	}

	return float4( reflection * strength, 0.0 );
}
