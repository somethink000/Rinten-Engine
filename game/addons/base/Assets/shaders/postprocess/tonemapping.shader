//Tonemap curve as a point effect; curves live in tonemap.slang, which a project can override

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

STATE
{
	//This file's Apply is the frame's curve: what comes before it is light, what comes after it is the picture the screen shows.
	Curve();
}

FEATURES
{
}

PARAMETERS
{
	//Which of tonemap.slang's curves - Tonemapping.TonemappingMode. Nought is the engine's own choice.
	float g_flMode < Attribute( "Mode" ); Default( 0.0 ); >;
}

#include "postprocess.slang"
#include "tonemap.slang"

float3 Apply( float3 colour, float2 uv )
{
	return EngineTonemap( colour * Exposure(), g_flMode );
}
