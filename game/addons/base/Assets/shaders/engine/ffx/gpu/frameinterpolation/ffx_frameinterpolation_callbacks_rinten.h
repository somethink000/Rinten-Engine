//AMD's frame interpolation callbacks over the engine's pictures, one for one - see framegen.slang and CoreFrameGeneration
//Integers are stored pictures at their FFX_RINTEN_READ_ binding; a field's X and Y share one picture, X left and Y right
//No distortion field and no flow confidence: both read nought, as AMD's default resources hold

#include "ffx_frameinterpolation_resources.h"

#if defined(FFX_GPU)
#include "../ffx_core.h"
#endif

#if defined(FFX_GPU)

#define COUNTER_SPD                          0
#define COUNTER_FRAME_INDEX_SINCE_LAST_RESET 1

//-- the constants --

//Laid out by CoreFrameGeneration.Constants, a float4 a line, after the optical
//flow's two:

//  2  render size, display size
//  3  display size's reciprocal, near, far
//  4  interpolation rect's base, its size
//  5  device depth to view depth
//  6  delta time, reset, HUD-less, dispatch flags
//  7  optical flow scale, optical flow block size, tan of half the field
//  8  jitter, motion vector scale
//  9  transfer function, least and most luminance, spare

//And what changes pass by pass - SPD's mips, work groups and offset - in the
//push block's last line.

[[vk::binding(FFX_RINTEN_CONSTANTS_BINDING, 2)]] StructuredBuffer<float4> g_ffxConstants;

float4 FfxConstant( uint line ) { return g_ffxConstants[line]; }

[[vk::push_constant]]
cbuffer FfxPush
{
	uint4 g_ffxSlots[4];
};

uint FfxSlot( uint binding ) { return g_ffxSlots[binding >> 2][binding & 3]; }
uint FfxStep( uint which ) { return g_ffxSlots[3][which]; }

const FfxInt32x2 RenderSize() { return FfxInt32x2( FfxConstant( 2 ).xy ); }
const FfxInt32x2 DisplaySize() { return FfxInt32x2( FfxConstant( 2 ).zw ); }
const FfxInt32x2 InterpolationRectBase() { return FfxInt32x2( FfxConstant( 4 ).xy ); }
const FfxInt32x2 InterpolationRectSize() { return FfxInt32x2( FfxConstant( 4 ).zw ); }
const FfxBoolean Reset() { return FfxConstant( 6 ).y != 0.0f; }
FfxFloat32x4 DeviceToViewSpaceTransformFactors() { return FfxConstant( 5 ); }
const FfxFloat32x2 Jitter() { return FfxConstant( 8 ).xy; }
const FfxFloat32x2 MotionVectorScale() { return FfxConstant( 8 ).zw; }

FfxFloat32x2 GetOpticalFlowScale() { return FfxConstant( 7 ).xy; }
FfxInt32 GetOpticalFlowBlockSize() { return FfxInt32( FfxConstant( 7 ).z ); }

FfxInt32x2 GetOpticalFlowSize()
{
	FfxInt32x2 iOpticalFlowSize = (1.0f / GetOpticalFlowScale()) / FfxFloat32x2(GetOpticalFlowBlockSize().xx);
	return iOpticalFlowSize;
}

FfxInt32x2 GetOpticalFlowSize2() { return GetOpticalFlowSize() * 1; }

FfxInt32 GetHUDLessAttachedFactor() { return FfxInt32( FfxConstant( 6 ).z ); }
FfxInt32x2 GetDistortionFieldSize() { return FfxInt32x2( 1, 1 ); }
FfxUInt32 GetDispatchFlags() { return FfxUInt32( FfxConstant( 6 ).w ); }

//The pictures are made at the render size, so the most it can be is what it is
FfxInt32x2 GetMaxRenderSize() { return RenderSize(); }

FfxInt32 GetOpticalFlowHalfResMode() { return 0; }
FfxFloat32x3 GetDebugBarColor() { return FfxFloat32x3( 0.0f, 0.0f, 0.0f ); }
FfxFloat32 TanHalfFoV() { return FfxConstant( 7 ).w; }
FfxUInt32 BackBufferTransferFunction() { return FfxUInt32( FfxConstant( 9 ).x ); }
FfxFloat32 MinLuminance() { return FfxConstant( 9 ).y; }
FfxFloat32 MaxLuminance() { return FfxConstant( 9 ).z; }

FfxUInt32 NumMips() { return FfxStep( 0 ); }
FfxUInt32 NumWorkGroups() { return FfxStep( 1 ); }
FfxUInt32x2 WorkGroupOffset() { return FfxUInt32x2( FfxStep( 2 ), FfxStep( 3 ) ); }

//-- what is read through the table --

[[vk::binding(0, 3)]] Sampler2D g_ffxTable[];

#define FFX_SRV( binding ) g_ffxTable[FfxSlot( binding )]

#ifdef FFX_FRAMEINTERPOLATION_BIND_SRV_PREVIOUS_INTERPOLATION_SOURCE
FfxFloat32x3 LoadPreviousBackbuffer(FFX_PARAMETER_IN FfxInt32x2 iPxPos)
{
	return FFX_SRV( FFX_FRAMEINTERPOLATION_BIND_SRV_PREVIOUS_INTERPOLATION_SOURCE ).Load( int3( iPxPos, 0 ) ).rgb;
}
FfxFloat32x3 SamplePreviousBackbuffer(FFX_PARAMETER_IN FfxFloat32x2 fUv)
{
	return FFX_SRV( FFX_FRAMEINTERPOLATION_BIND_SRV_PREVIOUS_INTERPOLATION_SOURCE ).SampleLevel( fUv, 0 ).xyz;
}
#endif

#ifdef FFX_FRAMEINTERPOLATION_BIND_SRV_CURRENT_INTERPOLATION_SOURCE
FfxFloat32x3 LoadCurrentBackbuffer(FFX_PARAMETER_IN FfxInt32x2 iPxPos)
{
	return FFX_SRV( FFX_FRAMEINTERPOLATION_BIND_SRV_CURRENT_INTERPOLATION_SOURCE ).Load( int3( iPxPos, 0 ) ).rgb;
}
FfxFloat32x3 SampleCurrentBackbuffer(FFX_PARAMETER_IN FfxFloat32x2 fUv)
{
	return FFX_SRV( FFX_FRAMEINTERPOLATION_BIND_SRV_CURRENT_INTERPOLATION_SOURCE ).SampleLevel( fUv, 0 ).xyz;
}
#endif

#ifdef FFX_FRAMEINTERPOLATION_BIND_SRV_DILATED_MOTION_VECTORS
FfxFloat32x2 LoadDilatedMotionVector(FFX_PARAMETER_IN FfxInt32x2 iPxPos)
{
	return FFX_SRV( FFX_FRAMEINTERPOLATION_BIND_SRV_DILATED_MOTION_VECTORS ).Load( int3( iPxPos, 0 ) ).xy;
}
#endif

#ifdef FFX_FRAMEINTERPOLATION_BIND_SRV_DILATED_DEPTH
FfxFloat32 LoadDilatedDepth(FFX_PARAMETER_IN FfxInt32x2 iPxPos)
{
	return FFX_SRV( FFX_FRAMEINTERPOLATION_BIND_SRV_DILATED_DEPTH ).Load( int3( iPxPos, 0 ) ).x;
}
#endif

#ifdef FFX_FRAMEINTERPOLATION_BIND_SRV_DISOCCLUSION_MASK
FfxFloat32x4 LoadDisocclusionMask(FFX_PARAMETER_IN FfxInt32x2 iPxPos)
{
	return FFX_SRV( FFX_FRAMEINTERPOLATION_BIND_SRV_DISOCCLUSION_MASK ).Load( int3( iPxPos, 0 ) );
}
FfxFloat32x4 SampleDisocclusionMask(FFX_PARAMETER_IN FfxFloat32x2 fUv)
{
	return FFX_SRV( FFX_FRAMEINTERPOLATION_BIND_SRV_DISOCCLUSION_MASK ).SampleLevel( fUv, 0 );
}
#endif

#ifdef FFX_FRAMEINTERPOLATION_BIND_SRV_OPTICAL_FLOW_CONFIDENCE
FfxFloat32 LoadOpticalFlowConfidence(FFX_PARAMETER_IN FfxInt32x2 iPxPos) { return 0.0f; }
#endif

#if defined(FFX_FRAMEINTERPOLATION_BIND_SRV_INPAINTING_MASK) && defined(FFX_FRAMEINTERPOLATION_BIND_SRV_OUTPUT)
FfxFloat32x4 LoadFrameInterpolationOutput(FFX_PARAMETER_IN FfxInt32x2 iPxInput)
{
	return FfxFloat32x4( FFX_SRV( FFX_FRAMEINTERPOLATION_BIND_SRV_OUTPUT ).Load( int3( iPxInput, 0 ) ).rgb,
		FFX_SRV( FFX_FRAMEINTERPOLATION_BIND_SRV_INPAINTING_MASK ).Load( int3( iPxInput, 0 ) ).x );
}
#endif

#ifdef FFX_FRAMEINTERPOLATION_BIND_SRV_INPAINTING_PYRAMID
FfxFloat32x4 LoadInpaintingPyramid(FFX_PARAMETER_IN FfxInt32 mipLevel, FFX_PARAMETER_IN FfxUInt32x2 iPxInput)
{
	return FFX_SRV( FFX_FRAMEINTERPOLATION_BIND_SRV_INPAINTING_PYRAMID ).Load( int3( iPxInput, mipLevel ) );
}
#endif

#ifdef FFX_FRAMEINTERPOLATION_BIND_SRV_PRESENT_BACKBUFFER
FfxFloat32x4 LoadPresentBackbuffer(FFX_PARAMETER_IN FfxInt32x2 iPxInput)
{
	return FFX_SRV( FFX_FRAMEINTERPOLATION_BIND_SRV_PRESENT_BACKBUFFER ).Load( int3( iPxInput, 0 ) );
}
FfxFloat32x4 SamplePresentBackbuffer(FFX_PARAMETER_IN FfxFloat32x2 fUv)
{
	return FFX_SRV( FFX_FRAMEINTERPOLATION_BIND_SRV_PRESENT_BACKBUFFER ).SampleLevel( fUv, 0 );
}
#endif

#if defined(FFX_FRAMEINTERPOLATION_BIND_SRV_INPUT_DEPTH)
FfxFloat32 LoadInputDepth(FfxInt32x2 iPxPos)
{
	return FFX_SRV( FFX_FRAMEINTERPOLATION_BIND_SRV_INPUT_DEPTH ).Load( int3( iPxPos, 0 ) ).x;
}
#endif

#if defined(FFX_FRAMEINTERPOLATION_BIND_SRV_INPUT_MOTION_VECTORS)
FfxFloat32x2 LoadInputMotionVector(FfxInt32x2 iPxDilatedMotionVectorPos)
{
	FfxFloat32x2 fSrcMotionVector = FFX_SRV( FFX_FRAMEINTERPOLATION_BIND_SRV_INPUT_MOTION_VECTORS ).Load( int3( iPxDilatedMotionVectorPos, 0 ) ).xy;
	return fSrcMotionVector * MotionVectorScale();
}
#endif

#if defined(FFX_FRAMEINTERPOLATION_BIND_SRV_DISTORTION_FIELD)
FfxFloat32x2 SampleDistortionField(FFX_PARAMETER_IN FfxFloat32x2 fUv) { return FfxFloat32x2( 0.0f, 0.0f ); }
#endif

//-- the counters --

#ifdef FFX_FRAMEINTERPOLATION_BIND_SRV_COUNTERS
[[vk::binding(FFX_RINTEN_COUNTERS_BINDING, 2)]] StructuredBuffer<FfxUInt32> r_counters;
FfxUInt32 LoadCounter(FFX_PARAMETER_IN FfxInt32 iPxPos) { return r_counters[iPxPos]; }
const FfxUInt32 FrameIndexSinceLastReset() { return LoadCounter(COUNTER_FRAME_INDEX_SINCE_LAST_RESET); }
#endif

#ifdef FFX_FRAMEINTERPOLATION_BIND_UAV_COUNTERS
[[vk::binding(FFX_RINTEN_COUNTERS_BINDING, 2)]] globallycoherent RWStructuredBuffer<FfxUInt32> rw_counters;
FfxUInt32 RWLoadCounter(FFX_PARAMETER_IN FfxInt32 iPxPos) { return rw_counters[iPxPos]; }
void StoreCounter(FFX_PARAMETER_IN FfxInt32 iPxPos, FFX_PARAMETER_IN FfxUInt32 counter) { rw_counters[iPxPos] = counter; }
void AtomicIncreaseCounter(FFX_PARAMETER_IN FfxInt32 iPxPos, FFX_PARAMETER_OUT FfxUInt32 oldVal) { InterlockedAdd(rw_counters[iPxPos], 1, oldVal); }
#endif

//-- the integers, read as stored pictures --

//Where a field picture's Y half starts: half its width
FfxInt32x2 FfxFieldY( RWTexture2D<FfxUInt32> field )
{
	FfxUInt32 width;
	FfxUInt32 height;
	field.GetDimensions( width, height );
	return FfxInt32x2( FfxInt32( width / 2 ), 0 );
}

//Inside the X half: past it is the Y half, where AMD's two pictures would have dropped the access
FfxBoolean FfxFieldInside( RWTexture2D<FfxUInt32> field, FfxInt32x2 at )
{
	FfxUInt32 width;
	FfxUInt32 height;
	field.GetDimensions( width, height );
	return all( at >= 0 ) && at.x < FfxInt32( width / 2 ) && at.y < FfxInt32( height );
}

#ifdef FFX_RINTEN_READ_RECONSTRUCTED_DEPTH_PREVIOUS_FRAME
[[vk::binding(FFX_RINTEN_READ_RECONSTRUCTED_DEPTH_PREVIOUS_FRAME, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> r_reconstructed_depth_previous_frame;
FfxFloat32 LoadReconstructedDepthPreviousFrame(FFX_PARAMETER_IN FfxInt32x2 iPxInput)
{
	return asfloat(r_reconstructed_depth_previous_frame[iPxInput]);
}
#endif

#ifdef FFX_RINTEN_READ_RECONSTRUCTED_DEPTH_INTERPOLATED_FRAME
[[vk::binding(FFX_RINTEN_READ_RECONSTRUCTED_DEPTH_INTERPOLATED_FRAME, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> r_reconstructed_depth_interpolated_frame;
FfxFloat32 LoadEstimatedInterpolationFrameDepth(FFX_PARAMETER_IN FfxInt32x2 iPxInput)
{
	return asfloat(r_reconstructed_depth_interpolated_frame[iPxInput]);
}
#endif

#ifdef FFX_RINTEN_READ_GAME_MOTION_VECTOR_FIELD
[[vk::binding(FFX_RINTEN_READ_GAME_MOTION_VECTOR_FIELD, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> r_game_motion_vector_field;
FfxUInt32x2 LoadGameFieldMv(FFX_PARAMETER_IN FfxInt32x2 iPxSample)
{
	if ( !FfxFieldInside( r_game_motion_vector_field, iPxSample ) ) return FfxUInt32x2( 0, 0 );

	FfxUInt32 packedX = r_game_motion_vector_field[iPxSample];
	FfxUInt32 packedY = r_game_motion_vector_field[iPxSample + FfxFieldY( r_game_motion_vector_field )];
	return FfxUInt32x2(packedX, packedY);
}
#endif

#ifdef FFX_RINTEN_READ_OPTICAL_FLOW_MOTION_VECTOR_FIELD
[[vk::binding(FFX_RINTEN_READ_OPTICAL_FLOW_MOTION_VECTOR_FIELD, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> r_optical_flow_motion_vector_field;
FfxUInt32x2 LoadOpticalFlowFieldMv(FFX_PARAMETER_IN FfxInt32x2 iPxSample)
{
	if ( !FfxFieldInside( r_optical_flow_motion_vector_field, iPxSample ) ) return FfxUInt32x2( 0, 0 );

	FfxUInt32 packedX = r_optical_flow_motion_vector_field[iPxSample];
	FfxUInt32 packedY = r_optical_flow_motion_vector_field[iPxSample + FfxFieldY( r_optical_flow_motion_vector_field )];
	return FfxUInt32x2(packedX, packedY);
}
#endif

#ifdef FFX_RINTEN_READ_OPTICAL_FLOW
[[vk::binding(FFX_RINTEN_READ_OPTICAL_FLOW, 2)]] [format("rg32i")] RWTexture2D<FfxInt32x2> r_optical_flow;
FfxFloat32x2 LoadOpticalFlow(FFX_PARAMETER_IN FfxInt32x2 iPxPos)
{
	FfxUInt32 width;
	FfxUInt32 height;
	r_optical_flow.GetDimensions( width, height );

	//A texture read past the edge comes back nought, and AMD's 3x3 filter counts on it
	if ( any( iPxPos < 0 ) || any( iPxPos >= FfxInt32x2( width, height ) ) ) return FfxFloat32x2( 0.0f, 0.0f );

	return r_optical_flow[iPxPos] * GetOpticalFlowScale();
}
#endif

#ifdef FFX_RINTEN_READ_OPTICAL_FLOW_SCENE_CHANGE_DETECTION
[[vk::binding(FFX_RINTEN_READ_OPTICAL_FLOW_SCENE_CHANGE_DETECTION, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> r_optical_flow_scd;
FfxUInt32 LoadOpticalFlowSceneChangeDetection(FFX_PARAMETER_IN FfxInt32x2 iPxPos) { return r_optical_flow_scd[iPxPos]; }
FfxBoolean HasSceneChanged()
{
	#define SCD_OUTPUT_HISTORY_BITS_SLOT 1

	//Changed where any of the four frames before saw a change
	return (r_optical_flow_scd[FfxInt32x2(SCD_OUTPUT_HISTORY_BITS_SLOT, 0)] & 0xfu) != 0;
}
#endif

//-- what is written --

#if defined(FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_MASK) && defined(FFX_FRAMEINTERPOLATION_BIND_UAV_OUTPUT)
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_OUTPUT, 2)]] [format("rgba8")] RWTexture2D<FfxFloat32x4> rw_output;
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_MASK, 2)]] [format("r16f")] RWTexture2D<FfxFloat32> rw_inpainting_mask;
FfxFloat32x4 RWLoadFrameinterpolationOutput(FFX_PARAMETER_IN FfxInt32x2 iPxPos)
{
	return FfxFloat32x4(rw_output[iPxPos].rgb, rw_inpainting_mask[iPxPos]);
}
void StoreFrameinterpolationOutput(FFX_PARAMETER_IN FfxInt32x2 iPxPos, FFX_PARAMETER_IN FfxFloat32x4 val)
{
	rw_output[iPxPos] = FfxFloat32x4(val.rgb, 1.0f);
	rw_inpainting_mask[iPxPos] = val.a;
}
#endif

#ifdef FFX_FRAMEINTERPOLATION_BIND_UAV_DILATED_MOTION_VECTORS
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_DILATED_MOTION_VECTORS, 2)]] [format("rg16f")] RWTexture2D<FfxFloat32x2> rw_dilated_motion_vectors;
FfxFloat32x2 RWLoadDilatedMotionVectors(FFX_PARAMETER_IN FfxInt32x2 iPxPos) { return rw_dilated_motion_vectors[iPxPos]; }
void StoreDilatedMotionVectors(FFX_PARAMETER_IN FfxInt32x2 iPxPos, FFX_PARAMETER_IN FfxFloat32x2 val) { rw_dilated_motion_vectors[iPxPos] = val; }
#endif

#ifdef FFX_FRAMEINTERPOLATION_BIND_UAV_DILATED_DEPTH
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_DILATED_DEPTH, 2)]] [format("r32f")] RWTexture2D<FfxFloat32> rw_dilated_depth;
FfxFloat32 RWLoadDilatedDepth(FFX_PARAMETER_IN FfxInt32x2 iPxPos) { return rw_dilated_depth[iPxPos]; }
void StoreDilatedDepth(FFX_PARAMETER_IN FfxInt32x2 iPxPos, FFX_PARAMETER_IN FfxFloat32 val) { rw_dilated_depth[iPxPos] = val; }
#endif

#ifdef FFX_FRAMEINTERPOLATION_BIND_UAV_RECONSTRUCTED_DEPTH_PREVIOUS_FRAME
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_RECONSTRUCTED_DEPTH_PREVIOUS_FRAME, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> rw_reconstructed_depth_previous_frame;
FfxFloat32 RWLoadReconstructedDepthPreviousFrame(FFX_PARAMETER_IN FfxInt32x2 iPxPos) { return ffxAsFloat(rw_reconstructed_depth_previous_frame[iPxPos]); }
void UpdateReconstructedDepthPreviousFrame(FfxInt32x2 iPxSample, FfxFloat32 fDepth)
{
	FfxUInt32 uDepth = ffxAsUInt32(fDepth);
#if FFX_FRAMEINTERPOLATION_OPTION_INVERTED_DEPTH
	InterlockedMax(rw_reconstructed_depth_previous_frame[iPxSample], uDepth);
#else
	InterlockedMin(rw_reconstructed_depth_previous_frame[iPxSample], uDepth);
#endif
}
#endif

#ifdef FFX_FRAMEINTERPOLATION_BIND_UAV_RECONSTRUCTED_DEPTH_INTERPOLATED_FRAME
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_RECONSTRUCTED_DEPTH_INTERPOLATED_FRAME, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> rw_reconstructed_depth_interpolated_frame;
FfxFloat32 RWLoadReconstructedDepthInterpolatedFrame(FFX_PARAMETER_IN FfxInt32x2 iPxPos) { return ffxAsFloat(rw_reconstructed_depth_interpolated_frame[iPxPos]); }
void StoreReconstructedDepthInterpolatedFrame(FFX_PARAMETER_IN FfxInt32x2 iPxPos, FFX_PARAMETER_IN FfxFloat32 value)
{
	rw_reconstructed_depth_interpolated_frame[iPxPos] = ffxAsUInt32(value);
}
void UpdateReconstructedDepthInterpolatedFrame(FfxInt32x2 iPxSample, FfxFloat32 fDepth)
{
	FfxUInt32 uDepth = ffxAsUInt32(fDepth);
#if FFX_FRAMEINTERPOLATION_OPTION_INVERTED_DEPTH
	InterlockedMax(rw_reconstructed_depth_interpolated_frame[iPxSample], uDepth);
#else
	InterlockedMin(rw_reconstructed_depth_interpolated_frame[iPxSample], uDepth);
#endif
}
#endif

#ifdef FFX_FRAMEINTERPOLATION_BIND_UAV_DISOCCLUSION_MASK
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_DISOCCLUSION_MASK, 2)]] [format("rg16f")] RWTexture2D<FfxFloat32x2> rw_disocclusion_mask;
FfxFloat32x2 RWLoadDisocclusionMask(FFX_PARAMETER_IN FfxInt32x2 iPxPos) { return rw_disocclusion_mask[iPxPos]; }
void StoreDisocclusionMask(FFX_PARAMETER_IN FfxInt32x2 iPxPos, FFX_PARAMETER_IN FfxFloat32x2 val) { rw_disocclusion_mask[iPxPos] = val; }
#endif

//The field's X and Y halves, at the binding AMD gives X - its Y binding is handed the same picture and not read
#ifdef FFX_FRAMEINTERPOLATION_BIND_UAV_GAME_MOTION_VECTOR_FIELD_X
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_GAME_MOTION_VECTOR_FIELD_X, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> rw_game_motion_vector_field;
FfxUInt32 RWLoadGameMotionVectorFieldX(FFX_PARAMETER_IN FfxInt32x2 iPxPos)
{
	return FfxFieldInside( rw_game_motion_vector_field, iPxPos ) ? rw_game_motion_vector_field[iPxPos] : 0;
}
void StoreGameMotionVectorFieldX(FFX_PARAMETER_IN FfxInt32x2 iPxPos, FFX_PARAMETER_IN FfxUInt32 val)
{
	if ( FfxFieldInside( rw_game_motion_vector_field, iPxPos ) ) rw_game_motion_vector_field[iPxPos] = val;
}
FfxUInt32 RWLoadGameMotionVectorFieldY(FFX_PARAMETER_IN FfxInt32x2 iPxPos)
{
	return FfxFieldInside( rw_game_motion_vector_field, iPxPos ) ? rw_game_motion_vector_field[iPxPos + FfxFieldY( rw_game_motion_vector_field )] : 0;
}
void StoreGameMotionVectorFieldY(FFX_PARAMETER_IN FfxInt32x2 iPxPos, FFX_PARAMETER_IN FfxUInt32 val)
{
	if ( FfxFieldInside( rw_game_motion_vector_field, iPxPos ) ) rw_game_motion_vector_field[iPxPos + FfxFieldY( rw_game_motion_vector_field )] = val;
}
void UpdateGameMotionVectorField(FFX_PARAMETER_IN FfxInt32x2 iPxPos, FFX_PARAMETER_IN FfxUInt32x2 packedVector)
{
	if ( !FfxFieldInside( rw_game_motion_vector_field, iPxPos ) ) return;

	InterlockedMax(rw_game_motion_vector_field[iPxPos], packedVector.x);
	InterlockedMax(rw_game_motion_vector_field[iPxPos + FfxFieldY( rw_game_motion_vector_field )], packedVector.y);
}
FfxUInt32 UpdateGameMotionVectorFieldEx(FFX_PARAMETER_IN FfxInt32x2 iPxPos, FFX_PARAMETER_IN FfxUInt32x2 packedVector)
{
	if ( !FfxFieldInside( rw_game_motion_vector_field, iPxPos ) ) return 0;

	FfxUInt32 uPreviousValueX = 0;
	FfxUInt32 uPreviousValueY = 0;
	InterlockedMax(rw_game_motion_vector_field[iPxPos], packedVector.x, uPreviousValueX);
	InterlockedMax(rw_game_motion_vector_field[iPxPos + FfxFieldY( rw_game_motion_vector_field )], packedVector.y, uPreviousValueY);
	return ffxMax(uPreviousValueX, uPreviousValueY);
}
#endif

#ifdef FFX_FRAMEINTERPOLATION_BIND_UAV_OPTICAL_FLOW_MOTION_VECTOR_FIELD_X
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_OPTICAL_FLOW_MOTION_VECTOR_FIELD_X, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> rw_optical_flow_motion_vector_field;
FfxUInt32 RWLoadOpticalflowMotionVectorFieldX(FFX_PARAMETER_IN FfxInt32x2 iPxPos)
{
	return FfxFieldInside( rw_optical_flow_motion_vector_field, iPxPos ) ? rw_optical_flow_motion_vector_field[iPxPos] : 0;
}
void StoreOpticalflowMotionVectorFieldX(FFX_PARAMETER_IN FfxInt32x2 iPxPos, FFX_PARAMETER_IN FfxUInt32 val)
{
	if ( FfxFieldInside( rw_optical_flow_motion_vector_field, iPxPos ) ) rw_optical_flow_motion_vector_field[iPxPos] = val;
}
FfxUInt32 RWLoadOpticalflowMotionVectorFieldY(FFX_PARAMETER_IN FfxInt32x2 iPxPos)
{
	return FfxFieldInside( rw_optical_flow_motion_vector_field, iPxPos ) ? rw_optical_flow_motion_vector_field[iPxPos + FfxFieldY( rw_optical_flow_motion_vector_field )] : 0;
}
void StoreOpticalflowMotionVectorFieldY(FFX_PARAMETER_IN FfxInt32x2 iPxPos, FFX_PARAMETER_IN FfxUInt32 val)
{
	if ( FfxFieldInside( rw_optical_flow_motion_vector_field, iPxPos ) ) rw_optical_flow_motion_vector_field[iPxPos + FfxFieldY( rw_optical_flow_motion_vector_field )] = val;
}
void UpdateOpticalflowMotionVectorField(FFX_PARAMETER_IN FfxInt32x2 iPxPos, FFX_PARAMETER_IN FfxUInt32x2 packedVector)
{
	if ( !FfxFieldInside( rw_optical_flow_motion_vector_field, iPxPos ) ) return;

	InterlockedMax(rw_optical_flow_motion_vector_field[iPxPos], packedVector.x);
	InterlockedMax(rw_optical_flow_motion_vector_field[iPxPos + FfxFieldY( rw_optical_flow_motion_vector_field )], packedVector.y);
}
#endif

//Eleven levels: SPD makes floor(log2) of the longer side, which is eleven below 4096 pixels - the rest are not stored
#ifdef FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_0
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_0, 2)]] [format("rgba16f")] RWTexture2D<FfxFloat32x4> rw_inpainting_pyramid0;
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_1, 2)]] [format("rgba16f")] RWTexture2D<FfxFloat32x4> rw_inpainting_pyramid1;
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_2, 2)]] [format("rgba16f")] RWTexture2D<FfxFloat32x4> rw_inpainting_pyramid2;
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_3, 2)]] [format("rgba16f")] RWTexture2D<FfxFloat32x4> rw_inpainting_pyramid3;
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_4, 2)]] [format("rgba16f")] RWTexture2D<FfxFloat32x4> rw_inpainting_pyramid4;
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_5, 2)]] [format("rgba16f")] globallycoherent RWTexture2D<FfxFloat32x4> rw_inpainting_pyramid5;
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_6, 2)]] [format("rgba16f")] RWTexture2D<FfxFloat32x4> rw_inpainting_pyramid6;
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_7, 2)]] [format("rgba16f")] RWTexture2D<FfxFloat32x4> rw_inpainting_pyramid7;
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_8, 2)]] [format("rgba16f")] RWTexture2D<FfxFloat32x4> rw_inpainting_pyramid8;
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_9, 2)]] [format("rgba16f")] RWTexture2D<FfxFloat32x4> rw_inpainting_pyramid9;
[[vk::binding(FFX_FRAMEINTERPOLATION_BIND_UAV_INPAINTING_PYRAMID_MIPMAP_10, 2)]] [format("rgba16f")] RWTexture2D<FfxFloat32x4> rw_inpainting_pyramid10;

FfxFloat32x4 RWLoadInpaintingPyramid(FFX_PARAMETER_IN FfxInt32x2 iPxPos, FFX_PARAMETER_IN FfxUInt32 index)
{
	if ( index == 0 ) return rw_inpainting_pyramid0[iPxPos];
	if ( index == 1 ) return rw_inpainting_pyramid1[iPxPos];
	if ( index == 2 ) return rw_inpainting_pyramid2[iPxPos];
	if ( index == 3 ) return rw_inpainting_pyramid3[iPxPos];
	if ( index == 4 ) return rw_inpainting_pyramid4[iPxPos];
	if ( index == 5 ) return rw_inpainting_pyramid5[iPxPos];
	if ( index == 6 ) return rw_inpainting_pyramid6[iPxPos];
	if ( index == 7 ) return rw_inpainting_pyramid7[iPxPos];
	if ( index == 8 ) return rw_inpainting_pyramid8[iPxPos];
	if ( index == 9 ) return rw_inpainting_pyramid9[iPxPos];
	if ( index == 10 ) return rw_inpainting_pyramid10[iPxPos];
	return 0;
}

void StoreInpaintingPyramid(FFX_PARAMETER_IN FfxInt32x2 iPxPos, FFX_PARAMETER_IN FfxFloat32x4 outValue, FFX_PARAMETER_IN FfxUInt32 index)
{
	if ( index == 0 ) rw_inpainting_pyramid0[iPxPos] = outValue;
	if ( index == 1 ) rw_inpainting_pyramid1[iPxPos] = outValue;
	if ( index == 2 ) rw_inpainting_pyramid2[iPxPos] = outValue;
	if ( index == 3 ) rw_inpainting_pyramid3[iPxPos] = outValue;
	if ( index == 4 ) rw_inpainting_pyramid4[iPxPos] = outValue;
	if ( index == 5 ) rw_inpainting_pyramid5[iPxPos] = outValue;
	if ( index == 6 ) rw_inpainting_pyramid6[iPxPos] = outValue;
	if ( index == 7 ) rw_inpainting_pyramid7[iPxPos] = outValue;
	if ( index == 8 ) rw_inpainting_pyramid8[iPxPos] = outValue;
	if ( index == 9 ) rw_inpainting_pyramid9[iPxPos] = outValue;
	if ( index == 10 ) rw_inpainting_pyramid10[iPxPos] = outValue;
}
#endif

#endif // #if defined(FFX_GPU)
