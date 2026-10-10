//The detail brought back up, over the finished picture at the window's size - see Sharpen. After
//an upscale rather than before it: a sharpen that runs first is one the upscaler then softens.

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

FEATURES
{
}

PARAMETERS
{
	float g_flStrength < Attribute( "Strength" ); Default( 0.0 ); >;

	//How far the neighbours it compares with are, in pixels.
	float g_flTexelSize < Attribute( "TexelSize" ); Default( 1.0 ); >;
}

#include "postprocess.slang"

[shader("fragment")]
float4 MainPs( PostV2F i ) : SV_Target0
{
	float3 centre = SceneColour( i.uv );

	if ( g_flStrength <= 0.0 ) return float4( centre, 1.0 );

	float2 step = max( g_flTexelSize, 0.05 ) * g_vFrameSize.zw;

	float3 around =
		SceneColour( i.uv + float2( step.x, 0.0 ) ) +
		SceneColour( i.uv - float2( step.x, 0.0 ) ) +
		SceneColour( i.uv + float2( 0.0, step.y ) ) +
		SceneColour( i.uv - float2( 0.0, step.y ) );

	float3 edge = ( centre * 4.0 ) - around;

	return float4( max( centre + ( edge * g_flStrength * 0.25 ), 0.0 ), 1.0 );
}
