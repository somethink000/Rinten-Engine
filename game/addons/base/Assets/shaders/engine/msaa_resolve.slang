//The world's samples into its picture, averaged as the screen will show them: a plain average of light keeps
//a stair where a dark edge meets a bright sky, since one bright sample outweighs the rest even after the curve

#include "postprocess.slang"

[[vk::binding(0,3)]] Sampler2DMS<float4> g_sampledWorlds[];

[shader("fragment")]
float4 MainPs( PostV2F i ) : SV_Target0
{
	Sampler2DMS<float4> samples = g_sampledWorlds[int( g_vPostSlots[0].x )];
	int count = int( g_vPostSlots[0].y );
	int2 at = int2( i.position.xy );

	float exposure = Exposure();
	float4 first = samples.Load( at, 0 );
	float4 sum = first;
	float weights = 1.0;
	bool flat = true;

	//The inside of a surface: every sample the same, so its colour goes through as it is
	for ( int s = 1; s < count; ++s )
	{
		if ( any( samples.Load( at, s ) != first ) )
		{
			flat = false;
			break;
		}
	}

	if ( flat ) return first;

	float weight = 1.0 / ( 1.0 + max( first.r, max( first.g, first.b ) ) * exposure );
	sum = first * weight;
	weights = weight;

	for ( int s = 1; s < count; ++s )
	{
		float4 one = samples.Load( at, s );
		weight = 1.0 / ( 1.0 + max( one.r, max( one.g, one.b ) ) * exposure );
		sum += one * weight;
		weights += weight;
	}

	return sum / weights;
}
