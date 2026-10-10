//Draws outline shapes into the mask target in the draw's colour, with and without depth test

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

FEATURES
{
}

PARAMETERS
{
	//The colour to mark this shape in, and in its alpha how much of it this pass is claiming to have seen - one
	//where the depth test let it through, less where the draw ignored the depth and the shape may be behind a wall.
	float4 g_vMarkColour < Attribute( "MarkColour" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;
}

struct V2F
{
	float4 position : SV_Position;
};

//The push block every world draw carries is the engine's, not this shader's.
#define ENGINE_WORLD_PUSH 1

#include "frame.slang"

[shader("vertex")]
V2F MainVs( VIn i )
{
	float3 position = i.position;

#ifdef SKINNED
	float4x4 skin =
		g_bones[i.bones.x] * i.weights.x +
		g_bones[i.bones.y] * i.weights.y +
		g_bones[i.bones.z] * i.weights.z +
		g_bones[i.bones.w] * i.weights.w;

	position = mul( float4( position, 1.0 ), skin ).xyz;
#endif

	float3 world = mul( float4( position, 1.0 ), g_modelToWorld ).xyz;

	V2F o;
	o.position = mul( float4( world, 1.0 ), EYE_FRAME( worldToClip ) );

	return o;
}

[shader("fragment")]
float4 MainPs( V2F i ) : SV_Target0
{
	return g_vMarkColour;
}
