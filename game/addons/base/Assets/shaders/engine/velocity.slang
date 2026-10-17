//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//Screen motion for moved and posed objects only; elsewhere motion is derived from depth and the camera

#include "frame_data.slang"

struct VIn
{
	[[vk::location(0)]] float3 position;
};

struct V2F
{
	float4 position : SV_Position;

	//Where this vertex is now and where it was, both in clip space, so that the division by w happens per
	//pixel rather than per vertex - a triangle across the screen is not linear in the thing being divided.
	float4 now : TEXCOORD0;
	float4 before : TEXCOORD1;
};

//MainVs: model to world now and before, a row each. PosedVs: the two as three columns each, then where the
//part's vertices start in the posed positions and in the ones before
[[vk::push_constant]]
cbuffer Push
{
	float4 g_push[8];
};

//A posed thing's positions now and as posed before, a float3 a vertex - see CoreSceneTables.Was
[[vk::binding(0,2)]] StructuredBuffer<float> g_posed;
[[vk::binding(1,2)]] StructuredBuffer<float> g_was;

V2F Moved( float3 world, float3 was )
{
	V2F o;

	o.now = mul( float4( world, 1.0 ), EYE_FRAME( worldToClip ) );
	o.before = mul( float4( was, 1.0 ), EYE_FRAME( previousWorldToClip ) );
	o.position = o.now;

	return o;
}

[shader("vertex")]
V2F MainVs( VIn i )
{
	float4x4 now = float4x4( g_push[0], g_push[1], g_push[2], g_push[3] );
	float4x4 before = float4x4( g_push[4], g_push[5], g_push[6], g_push[7] );

	return Moved( mul( float4( i.position, 1.0 ), now ).xyz, mul( float4( i.position, 1.0 ), before ).xyz );
}

float3 Affine( float3 position, uint first )
{
	float4 p = float4( position, 1.0 );
	return float3( dot( p, g_push[first] ), dot( p, g_push[first + 1] ), dot( p, g_push[first + 2] ) );
}

//Indices are the part's own; the draw adds no base, the push says where the part starts
[shader("vertex")]
V2F PosedVs( uint vid : SV_VertexID )
{
	uint2 from = asuint( g_push[6].xy );
	uint now = ( from.x + vid ) * 3;
	uint was = ( from.y + vid ) * 3;

	float3 posed = float3( g_posed[now], g_posed[now + 1], g_posed[now + 2] );
	float3 before = float3( g_was[was], g_was[was + 1], g_was[was + 2] );

	return Moved( Affine( posed, 0 ), Affine( before, 3 ) );
}

[shader("fragment")]
float4 MainPs( V2F i ) : SV_Target
{
	if ( i.now.w <= 0.0001 || i.before.w <= 0.0001 ) return float4( 0.0, 0.0, 0.0, 0.0 );

	float2 here = ( ( i.now.xy / i.now.w ) * 0.5 ) + 0.5;
	float2 there = ( ( i.before.xy / i.before.w ) * 0.5 ) + 0.5;

	//Screen delta, view depth (clip w), and 1 to mark the pixel as written by this pass
	return float4( here - there, i.now.w, 1.0 );
}
