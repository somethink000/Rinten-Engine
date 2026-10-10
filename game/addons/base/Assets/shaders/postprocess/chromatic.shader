//The colours pulled apart towards the corners, the way a cheap lens does it - see ChromaticAberration. A pass
//of its own over the finished picture: each channel is read somewhere else, which no point effect may do.

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

FEATURES
{
}

PARAMETERS
{
	//How far out each channel is read, as a share of the way to the edge.
	float3 g_vShift < Attribute( "Shift" ); Default3( 0.0, 0.0, 0.0 ); >;
}

#include "postprocess.slang"

[shader("fragment")]
float4 MainPs( PostV2F i ) : SV_Target0
{
	float2 away = i.uv - 0.5;

	return float4(
		SceneColour( i.uv + ( away * g_vShift.x ) ).r,
		SceneColour( i.uv + ( away * g_vShift.y ) ).g,
		SceneColour( i.uv + ( away * g_vShift.z ) ).b, 1.0 );
}
