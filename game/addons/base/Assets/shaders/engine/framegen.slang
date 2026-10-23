//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//AMD FSR 3.1 frame generation - optical flow and frame interpolation - as the engine's compute passes; see CoreFrameGeneration
//AMD's passes are unchanged in engine/ffx/gpu (MIT); each pass is a build of this file with its FG_ name defined

#define FFX_GPU 1
#define FFX_HLSL 1
#define FFX_HALF 0

//Nought is the near plane and one the far.
#define FFX_FRAMEINTERPOLATION_OPTION_INVERTED_DEPTH 0

//The motion is at the world's size and carries no jitter - see MotionCs in upscale.slang.
#define FFX_FRAMEINTERPOLATION_OPTION_LOW_RES_MOTION_VECTORS 1
#define FFX_FRAMEINTERPOLATION_OPTION_JITTERED_MOTION_VECTORS 0

//The constants, then the frame interpolation's counters: after the stored pictures - see VkDevice.PassImages.
#define FFX_RINTEN_CONSTANTS_BINDING 12
#define FFX_RINTEN_COUNTERS_BINDING 13

//-- the optical flow --

#if defined( FG_PREPARE_LUMA )

#define FFX_OPTICALFLOW_BIND_SRV_INPUT_COLOR                0
#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT         0
#define FFX_OPTICALFLOW_BIND_CB_COMMON                      0
#define FFX_RINTEN_SRV_BASE                                 1

#include "ffx/gpu/opticalflow/ffx_opticalflow_callbacks_rinten.h"

#define FFX_OPTICALFLOW_THREAD_GROUP_WIDTH 16
#define FFX_OPTICALFLOW_THREAD_GROUP_HEIGHT 16
#define FFX_OPTICALFLOW_THREAD_GROUP_DEPTH 1

#include "ffx/gpu/opticalflow/ffx_opticalflow_prepare_luma.h"

[numthreads(16, 16, 1)]
void main( int2 iGlobalId : SV_DispatchThreadID, int iLocalIndex : SV_GroupIndex )
{
	PrepareLuma( iGlobalId, iLocalIndex );
}

#elif defined( FG_LUMA_PYRAMID )

#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT                0
#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_1        1
#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_2        2
#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_3        3
#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_4        4
#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_5        5
#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_6        6
#define FFX_OPTICALFLOW_BIND_CB_COMMON                             0
#define FFX_OPTICALFLOW_BIND_CB_SPD                                1
#define FFX_RINTEN_SRV_BASE                                        7

#include "ffx/gpu/opticalflow/ffx_opticalflow_callbacks_rinten.h"
#include "ffx/gpu/opticalflow/ffx_opticalflow_compute_luminance_pyramid.h"

[numthreads(256, 1, 1)]
void main( int2 iGlobalId : SV_DispatchThreadID, int2 iGroupId : SV_GroupID, int iLocalIndex : SV_GroupIndex )
{
	ComputeOpticalFlowInputPyramid( iGroupId, iLocalIndex );
}

#elif defined( FG_SCD_HISTOGRAM )

#define FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW_INPUT            0
#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_SCD_HISTOGRAM    0
#define FFX_OPTICALFLOW_BIND_CB_COMMON                         0
#define FFX_RINTEN_SRV_BASE                                    1

#include "ffx/gpu/opticalflow/ffx_opticalflow_callbacks_rinten.h"
#include "ffx/gpu/opticalflow/ffx_opticalflow_generate_scd_histogram.h"

[numthreads(32, 8, 1)]
void main( int3 iGlobalId : SV_DispatchThreadID, int2 iLocalId : SV_GroupThreadID, int2 iGroupId : SV_GroupID, int iLocalIndex : SV_GroupIndex )
{
	GenerateSceneChangeDetectionHistogram( iGlobalId, iLocalId, iLocalIndex, iGroupId, int2( 32, 8 ) );
}

#elif defined( FG_SCD_DIVERGENCE )

#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_SCD_HISTOGRAM            0
#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_SCD_PREVIOUS_HISTOGRAM   1
#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_SCD_TEMP                 2
#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_SCD_OUTPUT               3
#define FFX_OPTICALFLOW_BIND_CB_COMMON                                 0
#define FFX_RINTEN_SRV_BASE                                            4

#include "ffx/gpu/opticalflow/ffx_opticalflow_callbacks_rinten.h"
#include "ffx/gpu/opticalflow/ffx_opticalflow_compute_scd_divergence.h"

[numthreads(256, 1, 1)]
void main( int3 iGlobalId : SV_DispatchThreadID, int2 iLocalId : SV_GroupThreadID, int2 iGroupId : SV_GroupID, int iLocalIndex : SV_GroupIndex )
{
	ComputeSCDHistogramsDivergence( iGlobalId, iLocalId, iLocalIndex, iGroupId, int2( 256, 1 ) );
}

#elif defined( FG_SEARCH )

#define FFX_WAVE 1

#define FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW_INPUT                0
#define FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW_PREVIOUS_INPUT       1
#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW                      0
#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_SCD_OUTPUT           1
#define FFX_OPTICALFLOW_BIND_CB_COMMON                             0
#define FFX_RINTEN_SRV_BASE                                        2

#include "ffx/gpu/opticalflow/ffx_opticalflow_callbacks_rinten.h"

#define FFX_OPTICALFLOW_THREAD_GROUP_WIDTH 64
#define FFX_OPTICALFLOW_THREAD_GROUP_HEIGHT 1
#define FFX_OPTICALFLOW_THREAD_GROUP_DEPTH 1

#include "ffx/gpu/opticalflow/ffx_opticalflow_compute_optical_flow_v5.h"

[numthreads(64, 1, 1)]
void main( int2 iGlobalId : SV_DispatchThreadID, int2 iLocalId : SV_GroupThreadID, int2 iGroupId : SV_GroupID, int iLocalIndex : SV_GroupIndex )
{
	ComputeOpticalFlowAdvanced( iGlobalId, iLocalId, iGroupId, iLocalIndex );
}

#elif defined( FG_FILTER )

#define FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW_PREVIOUS     0
#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW              0
#define FFX_OPTICALFLOW_BIND_CB_COMMON                     0
#define FFX_RINTEN_SRV_BASE                                1

#include "ffx/gpu/opticalflow/ffx_opticalflow_callbacks_rinten.h"

#define FFX_OPTICALFLOW_THREAD_GROUP_WIDTH 16
#define FFX_OPTICALFLOW_THREAD_GROUP_HEIGHT 4
#define FFX_OPTICALFLOW_THREAD_GROUP_DEPTH 1

#include "ffx/gpu/opticalflow/ffx_opticalflow_filter_optical_flow_v5.h"

[numthreads(16, 4, 1)]
void main( int2 iGlobalId : SV_DispatchThreadID, int2 iLocalId : SV_GroupThreadID, int2 iGroupId : SV_GroupID, int iLocalIndex : SV_GroupIndex )
{
	FilterOpticalFlow( iGlobalId, iLocalId, iGroupId, iLocalIndex );
}

#elif defined( FG_SCALE )

#define FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW_INPUT            0
#define FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW_PREVIOUS_INPUT   1
#define FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW                  2
#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_NEXT_LEVEL       0
#define FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_SCD_OUTPUT       1
#define FFX_OPTICALFLOW_BIND_CB_COMMON                         0
#define FFX_RINTEN_SRV_BASE                                    2

#include "ffx/gpu/opticalflow/ffx_opticalflow_callbacks_rinten.h"

#define FFX_OPTICALFLOW_THREAD_GROUP_WIDTH 4
#define FFX_OPTICALFLOW_THREAD_GROUP_HEIGHT 4
#define FFX_OPTICALFLOW_THREAD_GROUP_DEPTH 4

#include "ffx/gpu/opticalflow/ffx_opticalflow_scale_optical_flow_advanced_v5.h"

[numthreads(4, 4, 4)]
void main( int3 iGlobalId : SV_DispatchThreadID, int3 iLocalId : SV_GroupThreadID )
{
	ScaleOpticalFlowAdvanced( iGlobalId, iLocalId );
}

//-- the frame interpolation --

#elif defined( FG_RECONSTRUCT_AND_DILATE )

#define FFX_FRAMEINTERPOLATION_BIND_SRV_INPUT_MOTION_VECTORS                0
#define FFX_FRAMEINTERPOLATION_BIND_SRV_INPUT_DEPTH                         1
#define FFX_FRAMEINTERPOLATION_BIND_UAV_RECONSTRUCTED_DEPTH_PREVIOUS_FRAME  0
#define FFX_FRAMEINTERPOLATION_BIND_UAV_DILATED_MOTION_VECTORS              1
#define FFX_FRAMEINTERPOLATION_BIND_UAV_DILATED_DEPTH                       2
#define FFX_FRAMEINTERPOLATION_BIND_CB_FRAMEINTERPOLATION                   0

#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_callbacks_rinten.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_common.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_reconstruct_dilated_velocity_and_previous_depth.h"

[numthreads(8, 8, 1)]
void main( int2 iDispatchThreadId : SV_DispatchThreadID )
{
	ReconstructAndDilate( iDispatchThreadId );
}

#elif defined( FG_SETUP )

#define FFX_FRAMEINTERPOLATION_BIND_SRV_OPTICAL_FLOW_SCENE_CHANGE_DETECTION     0
#define FFX_FRAMEINTERPOLATION_BIND_UAV_GAME_MOTION_VECTOR_FIELD_X              0
#define FFX_FRAMEINTERPOLATION_BIND_UAV_GAME_MOTION_VECTOR_FIELD_Y              1
#define FFX_FRAMEINTERPOLATION_BIND_UAV_OPTICAL_FLOW_MOTION_VECTOR_FIELD_X      2
#define FFX_FRAMEINTERPOLATION_BIND_UAV_OPTICAL_FLOW_MOTION_VECTOR_FIELD_Y      3
#define FFX_FRAMEINTERPOLATION_BIND_UAV_DISOCCLUSION_MASK                       4
#define FFX_FRAMEINTERPOLATION_BIND_UAV_COUNTERS                                5
#define FFX_FRAMEINTERPOLATION_BIND_CB_FRAMEINTERPOLATION                       0
#define FFX_RINTEN_READ_OPTICAL_FLOW_SCENE_CHANGE_DETECTION                     6

#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_callbacks_rinten.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_common.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_setup.h"

[numthreads(8, 8, 1)]
void main( int2 iPxPos : SV_DispatchThreadID )
{
	setupFrameinterpolationResources( iPxPos );
}

#elif defined( FG_RECONSTRUCT_PREVIOUS_DEPTH )

#define FFX_FRAMEINTERPOLATION_BIND_SRV_DILATED_MOTION_VECTORS                  0
#define FFX_FRAMEINTERPOLATION_BIND_SRV_DILATED_DEPTH                           1
#define FFX_FRAMEINTERPOLATION_BIND_SRV_CURRENT_INTERPOLATION_SOURCE            2
#define FFX_FRAMEINTERPOLATION_BIND_SRV_DISTORTION_FIELD                        3
#define FFX_FRAMEINTERPOLATION_BIND_UAV_RECONSTRUCTED_DEPTH_INTERPOLATED_FRAME  0
#define FFX_FRAMEINTERPOLATION_BIND_CB_FRAMEINTERPOLATION                       0

#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_callbacks_rinten.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_common.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_reconstruct_previous_depth.h"

[numthreads(8, 8, 1)]
void main( int2 iPxPos : SV_DispatchThreadID )
{
	reconstructPreviousDepth( iPxPos );
}

#elif defined( FG_GAME_MOTION_VECTOR_FIELD )

#define FFX_FRAMEINTERPOLATION_BIND_SRV_DILATED_MOTION_VECTORS                  0
#define FFX_FRAMEINTERPOLATION_BIND_SRV_DILATED_DEPTH                           1
#define FFX_FRAMEINTERPOLATION_BIND_SRV_PREVIOUS_INTERPOLATION_SOURCE           2
#define FFX_FRAMEINTERPOLATION_BIND_SRV_CURRENT_INTERPOLATION_SOURCE            3
#define FFX_FRAMEINTERPOLATION_BIND_SRV_DISTORTION_FIELD                        4
#define FFX_FRAMEINTERPOLATION_BIND_UAV_GAME_MOTION_VECTOR_FIELD_X              0
#define FFX_FRAMEINTERPOLATION_BIND_UAV_GAME_MOTION_VECTOR_FIELD_Y              1
#define FFX_FRAMEINTERPOLATION_BIND_CB_FRAMEINTERPOLATION                       0

#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_callbacks_rinten.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_common.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_game_motion_vector_field.h"

[numthreads(8, 8, 1)]
void main( int2 iPxPos : SV_DispatchThreadID )
{
	computeGameFieldMvs( iPxPos );
}

#elif defined( FG_GAME_VECTOR_FIELD_PYRAMID ) || defined( FG_INPAINTING_PYRAMID )

#if defined( FG_GAME_VECTOR_FIELD_PYRAMID )
#define FFX_FRAMEINTERPOLATION_BIND_SRV_GAME_MOTION_VECTOR_FIELD_X              0
#define FFX_FRAMEINTERPOLATION_BIND_SRV_GAME_MOTION_VECTOR_FIELD_Y              1
#define FFX_RINTEN_READ_GAME_MOTION_VECTOR_FIELD                                0
#else
#define FFX_FRAMEINTERPOLATION_BIND_SRV_OUTPUT                                  0
#define FFX_FRAMEINTERPOLATION_BIND_SRV_INPAINTING_MASK                         1
#endif

#define FFX_FRAMEINTERPOLATION_BIND_UAV_COUNTERS                                0
#define FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_0             1
#define FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_1             2
#define FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_2             3
#define FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_3             4
#define FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_4             5
#define FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_5             6
#define FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_6             7
#define FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_7             8
#define FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_8             9
#define FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_9             10
#define FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_10            11
#define FFX_FRAMEINTERPOLATION_BIND_CB_FRAMEINTERPOLATION                       0
#define FFX_FRAMEINTERPOLATION_BIND_CB_INPAINTING_PYRAMID                       1

#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_callbacks_rinten.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_common.h"

#if defined( FG_GAME_VECTOR_FIELD_PYRAMID )
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_compute_game_vector_field_inpainting_pyramid.h"
#else
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_compute_inpainting_pyramid.h"
#endif

[numthreads(256, 1, 1)]
void main( uint3 WorkGroupId : SV_GroupID, uint LocalThreadIndex : SV_GroupIndex )
{
#if defined( FG_GAME_VECTOR_FIELD_PYRAMID )
	computeFrameinterpolationGameVectorFieldInpaintingPyramid( WorkGroupId, LocalThreadIndex );
#else
	computeFrameinterpolationInpaintingPyramid( WorkGroupId, LocalThreadIndex );
#endif
}

#elif defined( FG_OPTICAL_FLOW_VECTOR_FIELD )

#define FFX_FRAMEINTERPOLATION_BIND_SRV_OPTICAL_FLOW                            0
#define FFX_FRAMEINTERPOLATION_BIND_SRV_OPTICAL_FLOW_CONFIDENCE                 1
#define FFX_FRAMEINTERPOLATION_BIND_SRV_DILATED_DEPTH                           2
#define FFX_FRAMEINTERPOLATION_BIND_SRV_PREVIOUS_INTERPOLATION_SOURCE           3
#define FFX_FRAMEINTERPOLATION_BIND_SRV_CURRENT_INTERPOLATION_SOURCE            4
#define FFX_FRAMEINTERPOLATION_BIND_UAV_OPTICAL_FLOW_MOTION_VECTOR_FIELD_X      0
#define FFX_FRAMEINTERPOLATION_BIND_UAV_OPTICAL_FLOW_MOTION_VECTOR_FIELD_Y      1
#define FFX_FRAMEINTERPOLATION_BIND_CB_FRAMEINTERPOLATION                       0
#define FFX_RINTEN_READ_OPTICAL_FLOW                                            2

#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_callbacks_rinten.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_common.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_optical_flow_vector_field.h"

[numthreads(8, 8, 1)]
void main( int2 iPxPos : SV_DispatchThreadID )
{
	computeOpticalFlowVectorField( iPxPos );
}

#elif defined( FG_DISOCCLUSION_MASK )

#define FFX_FRAMEINTERPOLATION_BIND_SRV_GAME_MOTION_VECTOR_FIELD_X              0
#define FFX_FRAMEINTERPOLATION_BIND_SRV_GAME_MOTION_VECTOR_FIELD_Y              1
#define FFX_FRAMEINTERPOLATION_BIND_SRV_RECONSTRUCTED_DEPTH_PREVIOUS_FRAME      2
#define FFX_FRAMEINTERPOLATION_BIND_SRV_DILATED_DEPTH                           3
#define FFX_FRAMEINTERPOLATION_BIND_SRV_RECONSTRUCTED_DEPTH_INTERPOLATED_FRAME  4
#define FFX_FRAMEINTERPOLATION_BIND_SRV_INPAINTING_PYRAMID                      5
#define FFX_FRAMEINTERPOLATION_BIND_SRV_DISTORTION_FIELD                        6
#define FFX_FRAMEINTERPOLATION_BIND_UAV_DISOCCLUSION_MASK                       0
#define FFX_FRAMEINTERPOLATION_BIND_CB_FRAMEINTERPOLATION                       0
#define FFX_RINTEN_READ_GAME_MOTION_VECTOR_FIELD                                1
#define FFX_RINTEN_READ_RECONSTRUCTED_DEPTH_PREVIOUS_FRAME                      2
#define FFX_RINTEN_READ_RECONSTRUCTED_DEPTH_INTERPOLATED_FRAME                  3

#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_callbacks_rinten.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_common.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_disocclusion_mask.h"

[numthreads(8, 8, 1)]
void main( int2 iPxPos : SV_DispatchThreadID )
{
	computeDisocclusionMask( iPxPos );
}

#elif defined( FG_INTERPOLATE )

#define FFX_FRAMEINTERPOLATION_BIND_SRV_GAME_MOTION_VECTOR_FIELD_X              0
#define FFX_FRAMEINTERPOLATION_BIND_SRV_GAME_MOTION_VECTOR_FIELD_Y              1
#define FFX_FRAMEINTERPOLATION_BIND_SRV_OPTICAL_FLOW_MOTION_VECTOR_FIELD_X      2
#define FFX_FRAMEINTERPOLATION_BIND_SRV_OPTICAL_FLOW_MOTION_VECTOR_FIELD_Y      3
#define FFX_FRAMEINTERPOLATION_BIND_SRV_PREVIOUS_INTERPOLATION_SOURCE           4
#define FFX_FRAMEINTERPOLATION_BIND_SRV_CURRENT_INTERPOLATION_SOURCE            5
#define FFX_FRAMEINTERPOLATION_BIND_SRV_DISOCCLUSION_MASK                       6
#define FFX_FRAMEINTERPOLATION_BIND_SRV_INPAINTING_PYRAMID                      7
#define FFX_FRAMEINTERPOLATION_BIND_SRV_COUNTERS                                8
#define FFX_FRAMEINTERPOLATION_BIND_UAV_OUTPUT                                  0
#define FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_MASK                         1
#define FFX_FRAMEINTERPOLATION_BIND_CB_FRAMEINTERPOLATION                       0
#define FFX_RINTEN_READ_GAME_MOTION_VECTOR_FIELD                                2
#define FFX_RINTEN_READ_OPTICAL_FLOW_MOTION_VECTOR_FIELD                        3

#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_callbacks_rinten.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_common.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation.h"

[numthreads(8, 8, 1)]
void main( int2 iPxPos : SV_DispatchThreadID )
{
	computeFrameinterpolation( iPxPos );
}

#elif defined( FG_INPAINTING )

#define FFX_FRAMEINTERPOLATION_BIND_SRV_OPTICAL_FLOW_SCENE_CHANGE_DETECTION     0
#define FFX_FRAMEINTERPOLATION_BIND_SRV_INPAINTING_PYRAMID                      1
#define FFX_FRAMEINTERPOLATION_BIND_SRV_PRESENT_BACKBUFFER                      2
#define FFX_FRAMEINTERPOLATION_BIND_SRV_CURRENT_INTERPOLATION_SOURCE            3
#define FFX_FRAMEINTERPOLATION_BIND_UAV_OUTPUT                                  0
#define FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_MASK                         1
#define FFX_FRAMEINTERPOLATION_BIND_CB_FRAMEINTERPOLATION                       0
#define FFX_RINTEN_READ_OPTICAL_FLOW_SCENE_CHANGE_DETECTION                     2

#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_callbacks_rinten.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_common.h"
#include "ffx/gpu/frameinterpolation/ffx_frameinterpolation_inpainting.h"

[numthreads(8, 8, 1)]
void main( int2 iPxPos : SV_DispatchThreadID )
{
	computeInpainting( iPxPos );
}

#elif defined( FG_CLEAR )

//What AMD's backend clears with a command, cleared by a pass - see CoreFrameGeneration.Clear.

#if FG_CLEAR == 1
[[vk::binding(0, 2)]] [format("r32ui")] RWTexture2D<uint> g_clear;
#else
[[vk::binding(0, 2)]] [format("r32f")] RWTexture2D<float> g_clear;
#endif

[[vk::push_constant]]
cbuffer ClearPush
{
	float4 g_value;
	uint4 g_size;
};

[numthreads(8, 8, 1)]
void main( uint2 id : SV_DispatchThreadID )
{
	if ( any( id >= g_size.xy ) ) return;

#if FG_CLEAR == 1
	g_clear[id] = asuint( g_value.x );
#else
	g_clear[id] = g_value.x;
#endif
}

#endif
