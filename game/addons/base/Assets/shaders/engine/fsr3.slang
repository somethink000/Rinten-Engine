//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

// AMD FidelityFX Super Resolution 3.1's temporal upscaler, run as the engine's
// own compute passes over its own pictures - see CoreFsr3, which records them.
//
// The passes are AMD's, unchanged, in engine/ffx/gpu (MIT - see LICENSE.md
// there). What is the engine's is this file, which is AMD's eight *_pass.hlsl
// entry files in one - each pass is a build of it with its FSR3_ name defined
// - and ffx_fsr3upscaler_callbacks_rinten.h, which reads and writes the
// engine's pictures where AMD's reads and writes a root signature's. The
// binding numbers below are AMD's own, pass by pass, so the passes can be read
// against the SDK side by side.
//
// Each pass's entry is main; CoreFsr3 builds eight pipelines out of the eight
// builds, and three more that clear what AMD's backend clears with a command.

#define FFX_GPU 1
#define FFX_HLSL 1

//No half floats: AMD's half build ran its accumulate slower on RADV (780M 1.24 -> 1.80 ms), 2026-10-17
#define FFX_HALF 0

//The world's picture is light past white, not a tonemapped picture.
#define FFX_FSR3UPSCALER_OPTION_HDR_COLOR_INPUT 1

//The motion is at the world's size, and carries no jitter - see MotionCs in upscale.slang: last frame's matrix was given this frame's jitter.
#define FFX_FSR3UPSCALER_OPTION_LOW_RESOLUTION_MOTION_VECTORS 1
#define FFX_FSR3UPSCALER_OPTION_JITTERED_MOTION_VECTORS 0

//Depth is reversed: one is the near plane and nought the far
#define FFX_FSR3UPSCALER_OPTION_INVERTED_DEPTH 1

//The Lanczos kernel worked out rather than read from a table - AMD's default, and one texture fewer.
#define FFX_FSR3UPSCALER_OPTION_UPSAMPLE_USE_LANCZOS_TYPE 2

//Where the constant buffer is in set 2: after the stored pictures - see VkDevice.PassImages.
#define FFX_RINTEN_CONSTANTS_BINDING 12

#if defined( FSR3_PREPARE_INPUTS )

#define FSR3UPSCALER_BIND_SRV_INPUT_MOTION_VECTORS                      0
#define FSR3UPSCALER_BIND_SRV_INPUT_DEPTH                               1
#define FSR3UPSCALER_BIND_SRV_INPUT_COLOR                               2
#define FSR3UPSCALER_BIND_UAV_DILATED_MOTION_VECTORS                    0
#define FSR3UPSCALER_BIND_UAV_DILATED_DEPTH                             1
#define FSR3UPSCALER_BIND_UAV_RECONSTRUCTED_PREV_NEAREST_DEPTH          2
#define FSR3UPSCALER_BIND_UAV_FARTHEST_DEPTH                            3
#define FSR3UPSCALER_BIND_UAV_CURRENT_LUMA                              4
#define FSR3UPSCALER_BIND_CB_FSR3UPSCALER                               0

#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_callbacks_rinten.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_common.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_prepare_inputs.h"

[numthreads(8, 8, 1)]
void main( int2 iDispatchThreadId : SV_DispatchThreadID )
{
	PrepareInputs( iDispatchThreadId );
}

#elif defined( FSR3_LUMA_PYRAMID )

#define FSR3UPSCALER_BIND_SRV_CURRENT_LUMA                    0
#define FSR3UPSCALER_BIND_SRV_FARTHEST_DEPTH                  1
#define FSR3UPSCALER_BIND_UAV_SPD_GLOBAL_ATOMIC               0
#define FSR3UPSCALER_BIND_UAV_FRAME_INFO                      1
#define FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_0                2
#define FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_1                3
#define FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_2                4
#define FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_3                5
#define FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_4                6
#define FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_5                7
#define FSR3UPSCALER_BIND_UAV_FARTHEST_DEPTH_MIP1             8
#define FSR3UPSCALER_BIND_CB_FSR3UPSCALER                     0
#define FSR3UPSCALER_BIND_CB_SPD                              1

#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_callbacks_rinten.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_common.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_luma_pyramid.h"

[numthreads(256, 1, 1)]
void main( uint3 WorkGroupId : SV_GroupID, uint LocalThreadIndex : SV_GroupIndex )
{
	ComputeAutoExposure( WorkGroupId, LocalThreadIndex );
}

#elif defined( FSR3_SHADING_CHANGE_PYRAMID )

#define FSR3UPSCALER_BIND_SRV_CURRENT_LUMA                          0
#define FSR3UPSCALER_BIND_SRV_PREVIOUS_LUMA                         1
#define FSR3UPSCALER_BIND_SRV_DILATED_MOTION_VECTORS                2
#define FSR3UPSCALER_BIND_SRV_INPUT_EXPOSURE                        3
#define FSR3UPSCALER_BIND_UAV_SPD_GLOBAL_ATOMIC                     0
#define FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_0                      1
#define FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_1                      2
#define FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_2                      3
#define FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_3                      4
#define FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_4                      5
#define FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_5                      6
#define FSR3UPSCALER_BIND_CB_FSR3UPSCALER                           0
#define FSR3UPSCALER_BIND_CB_SPD                                    1

#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_callbacks_rinten.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_common.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_shading_change_pyramid.h"

[numthreads(256, 1, 1)]
void main( uint3 WorkGroupId : SV_GroupID, uint LocalThreadIndex : SV_GroupIndex )
{
	ComputeShadingChangePyramid( WorkGroupId, LocalThreadIndex );
}

#elif defined( FSR3_SHADING_CHANGE )

#define FSR3UPSCALER_BIND_SRV_SPD_MIPS                              0
#define FSR3UPSCALER_BIND_UAV_SHADING_CHANGE                        0
#define FSR3UPSCALER_BIND_CB_FSR3UPSCALER                           0

#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_callbacks_rinten.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_common.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_shading_change.h"

[numthreads(8, 8, 1)]
void main( int2 iDispatchThreadId : SV_DispatchThreadID )
{
	ShadingChange( iDispatchThreadId );
}

#elif defined( FSR3_PREPARE_REACTIVITY )

#define FSR3UPSCALER_BIND_SRV_RECONSTRUCTED_PREV_NEAREST_DEPTH              0
#define FSR3UPSCALER_BIND_SRV_DILATED_MOTION_VECTORS                        1
#define FSR3UPSCALER_BIND_SRV_DILATED_DEPTH                                 2
#define FSR3UPSCALER_BIND_SRV_REACTIVE_MASK                                 3
#define FSR3UPSCALER_BIND_SRV_TRANSPARENCY_AND_COMPOSITION_MASK             4
#define FSR3UPSCALER_BIND_SRV_ACCUMULATION                                  5
#define FSR3UPSCALER_BIND_SRV_SHADING_CHANGE                                6
#define FSR3UPSCALER_BIND_SRV_CURRENT_LUMA                                  7
#define FSR3UPSCALER_BIND_SRV_INPUT_EXPOSURE                                8
#define FSR3UPSCALER_BIND_UAV_DILATED_REACTIVE_MASKS                        0
#define FSR3UPSCALER_BIND_UAV_NEW_LOCKS                                     1
#define FSR3UPSCALER_BIND_UAV_ACCUMULATION                                  2
#define FSR3UPSCALER_BIND_CB_FSR3UPSCALER                                   0

//The reconstructed depth, read as the stored picture it is - see the callbacks.
#define FFX_RINTEN_RECONSTRUCTED_READ_BINDING                               3

#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_callbacks_rinten.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_common.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_prepare_reactivity.h"

[numthreads(8, 8, 1)]
void main( int2 iDispatchThreadId : SV_DispatchThreadID )
{
	PrepareReactivity( iDispatchThreadId );
}

#elif defined( FSR3_LUMA_INSTABILITY )

#define FSR3UPSCALER_BIND_SRV_INPUT_EXPOSURE                        0
#define FSR3UPSCALER_BIND_SRV_DILATED_REACTIVE_MASKS                1
#define FSR3UPSCALER_BIND_SRV_DILATED_MOTION_VECTORS                2
#define FSR3UPSCALER_BIND_SRV_FRAME_INFO                            3
#define FSR3UPSCALER_BIND_SRV_LUMA_HISTORY                          4
#define FSR3UPSCALER_BIND_SRV_FARTHEST_DEPTH_MIP1                   5
#define FSR3UPSCALER_BIND_SRV_CURRENT_LUMA                          6
#define FSR3UPSCALER_BIND_UAV_LUMA_HISTORY                          0
#define FSR3UPSCALER_BIND_UAV_LUMA_INSTABILITY                      1
#define FSR3UPSCALER_BIND_CB_FSR3UPSCALER                           0

#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_callbacks_rinten.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_common.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_luma_instability.h"

[numthreads(8, 8, 1)]
void main( int2 iDispatchThreadId : SV_DispatchThreadID )
{
	LumaInstability( iDispatchThreadId );
}

#elif defined( FSR3_ACCUMULATE )

//Its RCAS is the compose's - see CoreUpscaler.ComposeSharpening
#define FFX_FSR3UPSCALER_OPTION_APPLY_SHARPENING 0

#define FSR3UPSCALER_BIND_SRV_INPUT_EXPOSURE                        0
#define FSR3UPSCALER_BIND_SRV_DILATED_REACTIVE_MASKS                1
#define FSR3UPSCALER_BIND_SRV_DILATED_MOTION_VECTORS                2
#define FSR3UPSCALER_BIND_SRV_INTERNAL_UPSCALED                     3
#define FSR3UPSCALER_BIND_SRV_FARTHEST_DEPTH_MIP1                   5
#define FSR3UPSCALER_BIND_SRV_CURRENT_LUMA                          6
#define FSR3UPSCALER_BIND_SRV_LUMA_INSTABILITY                      7
#define FSR3UPSCALER_BIND_SRV_INPUT_COLOR                           8
#define FSR3UPSCALER_BIND_UAV_INTERNAL_UPSCALED                     0
#define FSR3UPSCALER_BIND_UAV_UPSCALED_OUTPUT                       1
#define FSR3UPSCALER_BIND_UAV_NEW_LOCKS                             2
#define FSR3UPSCALER_BIND_CB_FSR3UPSCALER                           0

#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_callbacks_rinten.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_common.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_sample.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_upsample.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_reproject.h"
#include "ffx/gpu/fsr3upscaler/ffx_fsr3upscaler_accumulate.h"

[numthreads(8, 8, 1)]
void main( int2 iDispatchThreadId : SV_DispatchThreadID )
{
	Accumulate( iDispatchThreadId );
}

#elif defined( FSR3_CLEAR )

//What AMD's backend clears with a command, cleared by a pass: the engine's stored pictures are
//cleared where they are written. One build a format, the value in the push block - see CoreFsr3.Clear.

#if FSR3_CLEAR == 1
[[vk::binding(0, 2)]] [format("r32ui")] RWTexture2D<uint> g_clear;
#elif FSR3_CLEAR == 2
[[vk::binding(0, 2)]] [format("r16f")] RWTexture2D<float> g_clear;
#elif FSR3_CLEAR == 3
[[vk::binding(0, 2)]] [format("rg16f")] RWTexture2D<float2> g_clear;
#elif FSR3_CLEAR == 5
[[vk::binding(0, 2)]] [format("rgba16f")] RWTexture2D<float4> g_clear;
#else
[[vk::binding(0, 2)]] [format("rgba32f")] RWTexture2D<float4> g_clear;
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

#if FSR3_CLEAR == 1
	g_clear[id] = asuint( g_value.x );
#elif FSR3_CLEAR == 2
	g_clear[id] = g_value.x;
#elif FSR3_CLEAR == 3
	g_clear[id] = g_value.xy;
#else
	g_clear[id] = g_value;
#endif
}

#endif
