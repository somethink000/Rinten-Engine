// Rinten's own resource layer for FSR 3's upscaler - in place of AMD's
// ffx_fsr3upscaler_callbacks_hlsl.h, whose functions it provides one for one,
// so the passes themselves (the other files beside this one, AMD's, MIT) are
// unchanged. See engine/fsr3.slang for the passes and CoreFsr3 for the engine
// side.
//
// AMD's file binds every resource by register in a root signature. The engine
// has no such thing; what it has is a compute pass over its own pictures - see
// VkFrame.PassDispatch - which gives a shader:
//
//   set 2   the pictures it stores into, in the order it was handed them, then
//           the buffers it reads (at VkDevice.PassImages onwards)
//   set 3   the table of every picture on the device, read by place
//   push    128 bytes of its own
//
// So a resource FSR reads is a place in the table, handed in the push block at
// the slot the pass's own FSR3UPSCALER_BIND_SRV_ number names; a resource it
// writes is a stored picture at the binding its FSR3UPSCALER_BIND_UAV_ number
// names, in a format the engine's picture of it is made in; and the constant
// buffers - 148 bytes of FSR's alone, past what a push block holds - are one
// buffer of float4s CoreFsr3 writes once a frame, in the frame's own order.
//
// Formats differ from AMD's in three places, all wider: the accumulation, the
// shading change and the new locks are made in half floats rather than eight
// bits, because the engine's pictures have no single eight-bit channel. The
// values are the same.

#include "ffx_fsr3upscaler_resources.h"

#if defined(FFX_GPU)
#include "../ffx_core.h"
#endif

#if defined(FFX_GPU)

// ---- the constants -----------------------------------------------------------
//
// Laid out by CoreFsr3.Constants, a float4 a line:
//
//   0  render size, last frame's render size
//   1  upscale size, last frame's upscale size
//   2  most render size, most upscale size
//   3  device depth to view depth
//   4  jitter, last frame's jitter
//   5  motion vector scale, downscale factor
//   6  jitter cancellation, tan of half the field, the jitter's phase count
//   7  frame length, pre-exposure change, view-to-metres, frame index
//   8  velocity factor, reactiveness scale, shading change scale, accumulation a frame
//   9  least accumulation after a disocclusion, the exposure, spare, spare
//  10  SPD: mips, work groups, work group offset
//  11  SPD: render size, spare
//  12  RCAS: its configuration, as the bits FsrRcasCon wrote

[[vk::binding(FFX_RINTEN_CONSTANTS_BINDING, 2)]] StructuredBuffer<float4> g_ffxConstants;

float4 FfxConstant( uint line ) { return g_ffxConstants[line]; }

FfxInt32x2 RenderSize() { return FfxInt32x2( FfxConstant( 0 ).xy ); }
FfxInt32x2 PreviousFrameRenderSize() { return FfxInt32x2( FfxConstant( 0 ).zw ); }
FfxInt32x2 UpscaleSize() { return FfxInt32x2( FfxConstant( 1 ).xy ); }
FfxInt32x2 PreviousFrameUpscaleSize() { return FfxInt32x2( FfxConstant( 1 ).zw ); }
FfxInt32x2 MaxRenderSize() { return FfxInt32x2( FfxConstant( 2 ).xy ); }
FfxInt32x2 MaxUpscaleSize() { return FfxInt32x2( FfxConstant( 2 ).zw ); }
FfxFloat32x4 DeviceToViewSpaceTransformFactors() { return FfxConstant( 3 ); }
FfxFloat32x2 Jitter() { return FfxConstant( 4 ).xy; }
FfxFloat32x2 PreviousFrameJitter() { return FfxConstant( 4 ).zw; }
FfxFloat32x2 MotionVectorScale() { return FfxConstant( 5 ).xy; }
FfxFloat32x2 DownscaleFactor() { return FfxConstant( 5 ).zw; }
FfxFloat32x2 MotionVectorJitterCancellation() { return FfxConstant( 6 ).xy; }
FfxFloat32 TanHalfFoV() { return FfxConstant( 6 ).z; }
FfxFloat32 JitterSequenceLength() { return FfxConstant( 6 ).w; }
FfxFloat32 DeltaTime() { return FfxConstant( 7 ).x; }
FfxFloat32 DeltaPreExposure() { return FfxConstant( 7 ).y; }
FfxFloat32 ViewSpaceToMetersFactor() { return FfxConstant( 7 ).z; }
FfxFloat32 FrameIndex() { return FfxConstant( 7 ).w; }
FfxFloat32 VelocityFactor() { return FfxConstant( 8 ).x; }
FfxFloat32 ReactivenessScale() { return FfxConstant( 8 ).y; }
FfxFloat32 ShadingChangeScale() { return FfxConstant( 8 ).z; }
FfxFloat32 AccumulationAddedPerFrame() { return FfxConstant( 8 ).w; }
FfxFloat32 MinDisocclusionAccumulation() { return FfxConstant( 9 ).x; }

/// The exposure the engine's picture is shown at - what AMD reads out of the
/// exposure texture the application hands it. Nought is one, as there.
FfxFloat32 Exposure()
{
	FfxFloat32 exposure = FfxConstant( 9 ).y;
	return exposure == 0.0f ? 1.0f : exposure;
}

FfxUInt32 MipCount() { return FfxUInt32( FfxConstant( 10 ).x ); }
FfxUInt32 NumWorkGroups() { return FfxUInt32( FfxConstant( 10 ).y ); }
FfxUInt32x2 WorkGroupOffset() { return FfxUInt32x2( FfxConstant( 10 ).zw ); }
FfxUInt32x2 SPD_RenderSize() { return FfxUInt32x2( FfxConstant( 11 ).xy ); }
FfxUInt32x4 RCASConfig() { return asuint( FfxConstant( 12 ) ); }

// ---- what is read ------------------------------------------------------------

// The table of every picture on the device - the same binding frame.slang names
// g_textures, under a name of its own so this file needs nothing of the frame's.
[[vk::binding(0, 3)]] Sampler2D g_ffxTable[];

// Which place in the table each of the pass's inputs is, at the slot its
// FSR3UPSCALER_BIND_SRV_ number names - see CoreFsr3.Pass.
[[vk::push_constant]]
cbuffer FfxPush
{
	uint4 g_ffxSlots[4];
};

uint FfxSlot( uint binding ) { return g_ffxSlots[binding >> 2][binding & 3]; }

#define FFX_SRV( binding ) g_ffxTable[FfxSlot( binding )]

float4 FfxLoad( uint binding, FfxInt32x2 at ) { return FFX_SRV( binding ).Load( int3( at, 0 ) ); }
float4 FfxSample( uint binding, FfxFloat32x2 uv ) { return FFX_SRV( binding ).SampleLevel( uv, 0.0 ); }

FfxInt32x2 FfxSize( uint binding )
{
	FfxUInt32 width;
	FfxUInt32 height;
	FFX_SRV( binding ).GetDimensions( width, height );
	return FfxInt32x2( width, height );
}

#if defined(FSR3UPSCALER_BIND_SRV_SPD_MIPS)
FfxInt32x2 GetSPDMipDimensions(FfxUInt32 uMipLevel)
{
	FfxUInt32 uWidth;
	FfxUInt32 uHeight;
	FfxUInt32 uLevels;
	FFX_SRV( FSR3UPSCALER_BIND_SRV_SPD_MIPS ).GetDimensions( uMipLevel, uWidth, uHeight, uLevels );
	return FfxInt32x2( uWidth, uHeight );
}

FfxFloat32x2 SampleSPDMipLevel(FfxFloat32x2 fUV, FfxUInt32 mipLevel)
{
	return FFX_SRV( FSR3UPSCALER_BIND_SRV_SPD_MIPS ).SampleLevel( fUV, mipLevel ).xy;
}
#endif

#if defined(FSR3UPSCALER_BIND_SRV_INPUT_DEPTH)
FfxFloat32 LoadInputDepth(FfxUInt32x2 iPxPos) { return FfxLoad( FSR3UPSCALER_BIND_SRV_INPUT_DEPTH, iPxPos ).x; }
FfxFloat32 SampleInputDepth(FfxFloat32x2 fUV) { return FfxSample( FSR3UPSCALER_BIND_SRV_INPUT_DEPTH, fUV ).x; }
#endif

// The reactive mask where the frame laid glass or sprites over the opaque world
// (ReactiveCs in upscale.slang), nothing where it did not - slot nought. No
// transparency-and-composition mask is handed in, so that one reads as nothing,
// which is what AMD's default one-texel mask holds.
#if defined(FSR3UPSCALER_BIND_SRV_REACTIVE_MASK)
FfxFloat32 LoadReactiveMask(FfxUInt32x2 iPxPos) { return FfxSlot( FSR3UPSCALER_BIND_SRV_REACTIVE_MASK ) == 0u ? 0.0f : FfxLoad( FSR3UPSCALER_BIND_SRV_REACTIVE_MASK, iPxPos ).x; }
FfxInt32x2 GetReactiveMaskResourceDimensions() { return RenderSize(); }
FfxFloat32 SampleReactiveMask(FfxFloat32x2 fUV) { return FfxSlot( FSR3UPSCALER_BIND_SRV_REACTIVE_MASK ) == 0u ? 0.0f : FfxSample( FSR3UPSCALER_BIND_SRV_REACTIVE_MASK, fUV ).x; }
#endif

#if defined(FSR3UPSCALER_BIND_SRV_TRANSPARENCY_AND_COMPOSITION_MASK)
FfxFloat32 LoadTransparencyAndCompositionMask(FfxUInt32x2 iPxPos) { return 0.0f; }
FfxInt32x2 GetTransparencyAndCompositionMaskResourceDimensions() { return RenderSize(); }
FfxFloat32 SampleTransparencyAndCompositionMask(FfxFloat32x2 fUV) { return 0.0f; }
#endif

#if defined(FSR3UPSCALER_BIND_SRV_INPUT_COLOR)
FfxFloat32x3 LoadInputColor(FfxUInt32x2 iPxPos) { return FfxLoad( FSR3UPSCALER_BIND_SRV_INPUT_COLOR, iPxPos ).rgb; }
FfxFloat32x3 SampleInputColor(FfxFloat32x2 fUV) { return FfxSample( FSR3UPSCALER_BIND_SRV_INPUT_COLOR, fUV ).rgb; }
#endif

#if defined(FSR3UPSCALER_BIND_SRV_INPUT_MOTION_VECTORS)
FfxFloat32x2 LoadInputMotionVector(FfxUInt32x2 iPxDilatedMotionVectorPos)
{
	FfxFloat32x2 fSrcMotionVector = FfxLoad( FSR3UPSCALER_BIND_SRV_INPUT_MOTION_VECTORS, iPxDilatedMotionVectorPos ).xy;
	FfxFloat32x2 fUvMotionVector = fSrcMotionVector * MotionVectorScale();
#if FFX_FSR3UPSCALER_OPTION_JITTERED_MOTION_VECTORS
	fUvMotionVector -= MotionVectorJitterCancellation();
#endif
	return fUvMotionVector;
}
#endif

#if defined(FSR3UPSCALER_BIND_SRV_INTERNAL_UPSCALED)
FfxFloat32x4 LoadHistory(FfxUInt32x2 iPxHistory) { return FfxLoad( FSR3UPSCALER_BIND_SRV_INTERNAL_UPSCALED, iPxHistory ); }
FfxFloat32x4 SampleHistory(FfxFloat32x2 fUV) { return FfxSample( FSR3UPSCALER_BIND_SRV_INTERNAL_UPSCALED, fUV ); }
#endif

#if defined(FSR3UPSCALER_BIND_SRV_LUMA_HISTORY)
FfxFloat32x4 LoadLumaHistory(FfxInt32x2 iPxPos) { return FfxLoad( FSR3UPSCALER_BIND_SRV_LUMA_HISTORY, iPxPos ); }
FfxFloat32x4 SampleLumaHistory(FfxFloat32x2 fUV) { return FfxSample( FSR3UPSCALER_BIND_SRV_LUMA_HISTORY, fUV ); }
#endif

#if defined(FSR3UPSCALER_BIND_SRV_RCAS_INPUT)
FfxFloat32x4 LoadRCAS_Input(FfxInt32x2 iPxPos) { return FfxLoad( FSR3UPSCALER_BIND_SRV_RCAS_INPUT, iPxPos ); }
FfxFloat32x3 SampleRCAS_Input(FfxFloat32x2 fUV) { return FfxSample( FSR3UPSCALER_BIND_SRV_RCAS_INPUT, fUV ).rgb; }
#endif

#if defined(FSR3UPSCALER_BIND_SRV_ACCUMULATION)
FfxFloat32 SampleAccumulation(FfxFloat32x2 fUV) { return FfxSample( FSR3UPSCALER_BIND_SRV_ACCUMULATION, fUV ).x; }
#endif

#if defined(FSR3UPSCALER_BIND_SRV_SHADING_CHANGE)
FfxFloat32 LoadShadingChange(FfxUInt32x2 iPxPos) { return FfxLoad( FSR3UPSCALER_BIND_SRV_SHADING_CHANGE, iPxPos ).x * ShadingChangeScale(); }
FfxFloat32 SampleShadingChange(FfxFloat32x2 fUV) { return FfxSample( FSR3UPSCALER_BIND_SRV_SHADING_CHANGE, fUV ).x * ShadingChangeScale(); }
#endif

#if defined(FSR3UPSCALER_BIND_SRV_FARTHEST_DEPTH)
FfxInt32x2 GetFarthestDepthResourceDimensions() { return FfxSize( FSR3UPSCALER_BIND_SRV_FARTHEST_DEPTH ); }
FfxFloat32 LoadFarthestDepth(FfxUInt32x2 iPxPos) { return FfxLoad( FSR3UPSCALER_BIND_SRV_FARTHEST_DEPTH, iPxPos ).x; }
FfxFloat32 SampleFarthestDepth(FfxFloat32x2 fUV) { return FfxSample( FSR3UPSCALER_BIND_SRV_FARTHEST_DEPTH, fUV ).x; }
#endif

#if defined(FSR3UPSCALER_BIND_SRV_FARTHEST_DEPTH_MIP1)
FfxInt32x2 GetFarthestDepthMip1ResourceDimensions() { return FfxSize( FSR3UPSCALER_BIND_SRV_FARTHEST_DEPTH_MIP1 ); }
FfxFloat32 LoadFarthestDepthMip1(FfxUInt32x2 iPxPos) { return FfxLoad( FSR3UPSCALER_BIND_SRV_FARTHEST_DEPTH_MIP1, iPxPos ).x; }
FfxFloat32 SampleFarthestDepthMip1(FfxFloat32x2 fUV) { return FfxSample( FSR3UPSCALER_BIND_SRV_FARTHEST_DEPTH_MIP1, fUV ).x; }
#endif

#if defined(FSR3UPSCALER_BIND_SRV_CURRENT_LUMA)
FfxFloat32 LoadCurrentLuma(FfxUInt32x2 iPxPos) { return FfxLoad( FSR3UPSCALER_BIND_SRV_CURRENT_LUMA, iPxPos ).x; }
FfxFloat32 SampleCurrentLuma(FfxFloat32x2 uv) { return FfxSample( FSR3UPSCALER_BIND_SRV_CURRENT_LUMA, uv ).x; }
#endif

#if defined(FSR3UPSCALER_BIND_SRV_LUMA_INSTABILITY)
FfxFloat32 SampleLumaInstability(FfxFloat32x2 uv) { return FfxSample( FSR3UPSCALER_BIND_SRV_LUMA_INSTABILITY, uv ).x; }
#endif

#if defined(FSR3UPSCALER_BIND_SRV_PREVIOUS_LUMA)
FfxFloat32 LoadPreviousLuma(FfxUInt32x2 iPxPos) { return FfxLoad( FSR3UPSCALER_BIND_SRV_PREVIOUS_LUMA, iPxPos ).x; }
FfxFloat32 SamplePreviousLuma(FfxFloat32x2 uv) { return FfxSample( FSR3UPSCALER_BIND_SRV_PREVIOUS_LUMA, uv ).x; }
#endif

#if defined(FSR3UPSCALER_BIND_SRV_NEW_LOCKS)
FfxFloat32 LoadNewLocks(FfxUInt32x2 iPxPos) { return FfxLoad( FSR3UPSCALER_BIND_SRV_NEW_LOCKS, iPxPos ).x; }
#endif

// The reconstructed depth is thirty-two bits of unsigned integer, which the
// table - samplers of floats - cannot read. A pass that reads it is handed it
// as a stored picture as well, at the binding this names; see CoreFsr3.
#if defined(FSR3UPSCALER_BIND_SRV_RECONSTRUCTED_PREV_NEAREST_DEPTH)
[[vk::binding(FFX_RINTEN_RECONSTRUCTED_READ_BINDING, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> r_reconstructed_previous_nearest_depth;
FfxFloat32 LoadReconstructedPrevDepth(FfxUInt32x2 iPxPos)
{
	return asfloat( r_reconstructed_previous_nearest_depth[iPxPos] );
}
#endif

#if defined(FSR3UPSCALER_BIND_SRV_DILATED_MOTION_VECTORS)
FfxFloat32x2 LoadDilatedMotionVector(FfxUInt32x2 iPxInput) { return FfxLoad( FSR3UPSCALER_BIND_SRV_DILATED_MOTION_VECTORS, iPxInput ).xy; }
FfxFloat32x2 SampleDilatedMotionVector(FfxFloat32x2 fUV) { return FfxSample( FSR3UPSCALER_BIND_SRV_DILATED_MOTION_VECTORS, fUV ).xy; }
#endif

#if defined(FSR3UPSCALER_BIND_SRV_DILATED_DEPTH)
FfxFloat32 LoadDilatedDepth(FfxUInt32x2 iPxInput) { return FfxLoad( FSR3UPSCALER_BIND_SRV_DILATED_DEPTH, iPxInput ).x; }
FfxFloat32 SampleDilatedDepth(FfxFloat32x2 fUV) { return FfxSample( FSR3UPSCALER_BIND_SRV_DILATED_DEPTH, fUV ).x; }
#endif

// The Lanczos kernel is worked out rather than read out of a table - see
// FFX_FSR3UPSCALER_OPTION_UPSAMPLE_USE_LANCZOS_TYPE in engine/fsr3.slang.
FfxFloat32 SampleLanczos2Weight(FfxFloat32 x) { return 0.0f; }

#if defined(FSR3UPSCALER_BIND_SRV_DILATED_REACTIVE_MASKS)
FfxFloat32x4 SampleDilatedReactiveMasks(FfxFloat32x2 fUV) { return FfxSample( FSR3UPSCALER_BIND_SRV_DILATED_REACTIVE_MASKS, fUV ); }
#endif

#if defined(FSR3UPSCALER_BIND_SRV_FRAME_INFO)
FfxFloat32x4 FrameInfo() { return FfxLoad( FSR3UPSCALER_BIND_SRV_FRAME_INFO, FfxInt32x2( 0, 0 ) ); }
#endif

// ---- what is written ---------------------------------------------------------

#if defined(FSR3UPSCALER_BIND_UAV_LUMA_HISTORY)
[[vk::binding(FSR3UPSCALER_BIND_UAV_LUMA_HISTORY, 2)]] [format("rgba16f")] RWTexture2D<FfxFloat32x4> rw_luma_history;
void StoreLumaHistory(FfxUInt32x2 iPxPos, FfxFloat32x4 fLumaHistory) { rw_luma_history[iPxPos] = fLumaHistory; }
#endif

#if defined(FSR3UPSCALER_BIND_UAV_INTERNAL_UPSCALED)
[[vk::binding(FSR3UPSCALER_BIND_UAV_INTERNAL_UPSCALED, 2)]] [format("rgba16f")] RWTexture2D<FfxFloat32x4> rw_internal_upscaled_color;
void StoreReprojectedHistory(FfxUInt32x2 iPxHistory, FfxFloat32x4 fHistory) { rw_internal_upscaled_color[iPxHistory] = fHistory; }
void StoreInternalColorAndWeight(FfxUInt32x2 iPxPos, FfxFloat32x4 fColorAndWeight) { rw_internal_upscaled_color[iPxPos] = fColorAndWeight; }
#endif

#if defined(FSR3UPSCALER_BIND_UAV_UPSCALED_OUTPUT)
[[vk::binding(FSR3UPSCALER_BIND_UAV_UPSCALED_OUTPUT, 2)]] [format("rgba16f")] RWTexture2D<FfxFloat32x4> rw_upscaled_output;
void StoreUpscaledOutput(FfxUInt32x2 iPxPos, FfxFloat32x3 fColor) { rw_upscaled_output[iPxPos] = FfxFloat32x4( fColor, 1.0f ); }
#endif

#if defined(FSR3UPSCALER_BIND_UAV_ACCUMULATION)
[[vk::binding(FSR3UPSCALER_BIND_UAV_ACCUMULATION, 2)]] [format("r16f")] RWTexture2D<FfxFloat32> rw_accumulation;
void StoreAccumulation(FfxUInt32x2 iPxPos, FfxFloat32 fAccumulation) { rw_accumulation[iPxPos] = fAccumulation; }
#endif

#if defined(FSR3UPSCALER_BIND_UAV_SHADING_CHANGE)
[[vk::binding(FSR3UPSCALER_BIND_UAV_SHADING_CHANGE, 2)]] [format("r16f")] RWTexture2D<FfxFloat32> rw_shading_change;
void StoreShadingChange(FfxUInt32x2 iPxPos, FfxFloat32 fShadingChange) { rw_shading_change[iPxPos] = fShadingChange; }
#endif

#if defined(FSR3UPSCALER_BIND_UAV_FARTHEST_DEPTH)
[[vk::binding(FSR3UPSCALER_BIND_UAV_FARTHEST_DEPTH, 2)]] [format("r16f")] RWTexture2D<FfxFloat32> rw_farthest_depth;
void StoreFarthestDepth(FfxUInt32x2 iPxPos, FfxFloat32 fDepth) { rw_farthest_depth[iPxPos] = fDepth; }
#endif

#if defined(FSR3UPSCALER_BIND_UAV_FARTHEST_DEPTH_MIP1)
[[vk::binding(FSR3UPSCALER_BIND_UAV_FARTHEST_DEPTH_MIP1, 2)]] [format("r16f")] RWTexture2D<FfxFloat32> rw_farthest_depth_mip1;
void StoreFarthestDepthMip1(FfxUInt32x2 iPxPos, FfxFloat32 fDepth) { rw_farthest_depth_mip1[iPxPos] = fDepth; }
#endif

#if defined(FSR3UPSCALER_BIND_UAV_CURRENT_LUMA)
[[vk::binding(FSR3UPSCALER_BIND_UAV_CURRENT_LUMA, 2)]] [format("r16f")] RWTexture2D<FfxFloat32> rw_current_luma;
void StoreCurrentLuma(FfxUInt32x2 iPxPos, FfxFloat32 fLuma) { rw_current_luma[iPxPos] = fLuma; }
#endif

#if defined(FSR3UPSCALER_BIND_UAV_LUMA_INSTABILITY)
[[vk::binding(FSR3UPSCALER_BIND_UAV_LUMA_INSTABILITY, 2)]] [format("r16f")] RWTexture2D<FfxFloat32> rw_luma_instability;
void StoreLumaInstability(FfxUInt32x2 iPxPos, FfxFloat32 fLumaInstability) { rw_luma_instability[iPxPos] = fLumaInstability; }
#endif

#if defined(FSR3UPSCALER_BIND_UAV_NEW_LOCKS)
[[vk::binding(FSR3UPSCALER_BIND_UAV_NEW_LOCKS, 2)]] [format("r16f")] RWTexture2D<FfxFloat32> rw_new_locks;
FfxFloat32 LoadRwNewLocks(FfxUInt32x2 iPxPos) { return rw_new_locks[iPxPos]; }
void StoreNewLocks(FfxUInt32x2 iPxPos, FfxFloat32 newLock) { rw_new_locks[iPxPos] = newLock; }
#endif

#if defined(FSR3UPSCALER_BIND_UAV_RECONSTRUCTED_PREV_NEAREST_DEPTH)
[[vk::binding(FSR3UPSCALER_BIND_UAV_RECONSTRUCTED_PREV_NEAREST_DEPTH, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> rw_reconstructed_previous_nearest_depth;
void StoreReconstructedDepth(FfxUInt32x2 iPxSample, FfxFloat32 fDepth)
{
	FfxUInt32 uDepth = asuint( fDepth );
#if FFX_FSR3UPSCALER_OPTION_INVERTED_DEPTH
	InterlockedMax( rw_reconstructed_previous_nearest_depth[iPxSample], uDepth );
#else
	InterlockedMin( rw_reconstructed_previous_nearest_depth[iPxSample], uDepth );
#endif
}
void SetReconstructedDepth(FfxUInt32x2 iPxSample, const FfxUInt32 uValue) { rw_reconstructed_previous_nearest_depth[iPxSample] = uValue; }
#endif

#if defined(FSR3UPSCALER_BIND_UAV_DILATED_DEPTH)
[[vk::binding(FSR3UPSCALER_BIND_UAV_DILATED_DEPTH, 2)]] [format("r32f")] RWTexture2D<FfxFloat32> rw_dilated_depth;
void StoreDilatedDepth(FFX_PARAMETER_IN FfxUInt32x2 iPxPos, FFX_PARAMETER_IN FfxFloat32 fDepth) { rw_dilated_depth[iPxPos] = fDepth; }
#endif

#if defined(FSR3UPSCALER_BIND_UAV_DILATED_MOTION_VECTORS)
[[vk::binding(FSR3UPSCALER_BIND_UAV_DILATED_MOTION_VECTORS, 2)]] [format("rg16f")] RWTexture2D<FfxFloat32x2> rw_dilated_motion_vectors;
void StoreDilatedMotionVector(FFX_PARAMETER_IN FfxUInt32x2 iPxPos, FFX_PARAMETER_IN FfxFloat32x2 fMotionVector) { rw_dilated_motion_vectors[iPxPos] = fMotionVector; }
#endif

#if defined(FSR3UPSCALER_BIND_UAV_DILATED_REACTIVE_MASKS)
[[vk::binding(FSR3UPSCALER_BIND_UAV_DILATED_REACTIVE_MASKS, 2)]] [format("rgba8")] RWTexture2D<FfxFloat32x4> rw_dilated_reactive_masks;
void StoreDilatedReactiveMasks(FFX_PARAMETER_IN FfxUInt32x2 iPxPos, FFX_PARAMETER_IN FfxFloat32x4 fDilatedReactiveMasks) { rw_dilated_reactive_masks[iPxPos] = fDilatedReactiveMasks; }
#endif

#if defined(FSR3UPSCALER_BIND_UAV_FRAME_INFO)
[[vk::binding(FSR3UPSCALER_BIND_UAV_FRAME_INFO, 2)]] [format("rgba32f")] RWTexture2D<FfxFloat32x4> rw_frame_info;
FfxFloat32x4 LoadFrameInfo() { return rw_frame_info[FfxInt32x2( 0, 0 )]; }
void StoreFrameInfo(FfxFloat32x4 fInfo) { rw_frame_info[FfxInt32x2( 0, 0 )] = fInfo; }
#endif

#if defined(FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_0)
[[vk::binding(FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_0, 2)]] [format("rg16f")] RWTexture2D<FfxFloat32x2> rw_spd_mip0;
[[vk::binding(FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_1, 2)]] [format("rg16f")] RWTexture2D<FfxFloat32x2> rw_spd_mip1;
[[vk::binding(FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_2, 2)]] [format("rg16f")] RWTexture2D<FfxFloat32x2> rw_spd_mip2;
[[vk::binding(FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_3, 2)]] [format("rg16f")] RWTexture2D<FfxFloat32x2> rw_spd_mip3;
[[vk::binding(FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_4, 2)]] [format("rg16f")] RWTexture2D<FfxFloat32x2> rw_spd_mip4;
[[vk::binding(FSR3UPSCALER_BIND_UAV_SPD_MIPS_LEVEL_5, 2)]] [format("rg16f")] globallycoherent RWTexture2D<FfxFloat32x2> rw_spd_mip5;

FfxFloat32x2 RWLoadPyramid(FFX_PARAMETER_IN FfxInt32x2 iPxPos, FFX_PARAMETER_IN FfxUInt32 index)
{
	if ( index == 0 ) return rw_spd_mip0[iPxPos];
	if ( index == 1 ) return rw_spd_mip1[iPxPos];
	if ( index == 2 ) return rw_spd_mip2[iPxPos];
	if ( index == 3 ) return rw_spd_mip3[iPxPos];
	if ( index == 4 ) return rw_spd_mip4[iPxPos];
	if ( index == 5 ) return rw_spd_mip5[iPxPos];
	return 0;
}

void StorePyramid(FFX_PARAMETER_IN FfxInt32x2 iPxPos, FFX_PARAMETER_IN FfxFloat32x2 outValue, FFX_PARAMETER_IN FfxUInt32 index)
{
	if ( index == 0 ) rw_spd_mip0[iPxPos] = outValue;
	if ( index == 1 ) rw_spd_mip1[iPxPos] = outValue;
	if ( index == 2 ) rw_spd_mip2[iPxPos] = outValue;
	if ( index == 3 ) rw_spd_mip3[iPxPos] = outValue;
	if ( index == 4 ) rw_spd_mip4[iPxPos] = outValue;
	if ( index == 5 ) rw_spd_mip5[iPxPos] = outValue;
}
#endif

#if defined(FSR3UPSCALER_BIND_UAV_SPD_GLOBAL_ATOMIC)
[[vk::binding(FSR3UPSCALER_BIND_UAV_SPD_GLOBAL_ATOMIC, 2)]] [format("r32ui")] globallycoherent RWTexture2D<FfxUInt32> rw_spd_global_atomic;
void SPD_IncreaseAtomicCounter(inout FfxUInt32 spdCounter) { InterlockedAdd( rw_spd_global_atomic[FfxInt32x2( 0, 0 )], 1, spdCounter ); }
void SPD_ResetAtomicCounter() { rw_spd_global_atomic[FfxInt32x2( 0, 0 )] = 0; }
#endif

#endif // #if defined(FFX_GPU)
