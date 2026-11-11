//CMAA2 over the finished picture: edges, their shapes, the blends - see CoreSmaa. Intel's reference (Apache-2.0, see
//cmaa2/LICENSE.txt) with only its bindings set for the engine's pass layout and its stores marking what they changed

//High: an edge at 0.07 of luma, read from the colour itself; stores into an 8-bit picture in sRGB, as the frame reads it
#define CMAA2_STATIC_QUALITY_PRESET 2
#define CMAA2_EDGE_DETECTION_LUMA_PATH 1
#define CMAA2_UAV_STORE_TYPED 1
#define CMAA2_UAV_STORE_TYPED_UNORM_FLOAT 1
#define CMAA2_UAV_STORE_CONVERT_TO_SRGB 1

//The packed edges' width and height, then the lists' heads' width (a 2x2 quad each)
[[vk::push_constant]]
cbuffer CmaaPush
{
	uint4 g_cmaaGrid;
};

#include "cmaa2/CMAA2.hlsl"
