//AMD's optical flow callbacks over the engine's pictures, one for one - see framegen.slang and CoreFrameGeneration
//Integers the table cannot read are stored pictures at FFX_RINTEN_SRV_BASE plus their SRV number; 8-bit luma is r32ui, flow rg32i

#ifndef FFX_OPTICALFLOW_CALLBACKS_RINTEN_H
#define FFX_OPTICALFLOW_CALLBACKS_RINTEN_H

#if defined(FFX_GPU)

#include "../ffx_core.h"

#define FFX_OPTICALFLOW_USE_MSAD4_INSTRUCTION 1
#define FFX_OPTICALFLOW_FIX_TOP_LEFT_BIAS 1
#define FFX_OPTICALFLOW_USE_HEURISTICS 1
#define FFX_OPTICALFLOW_BLOCK_SIZE 8
#define FFX_LOCAL_SEARCH_FALLBACK 1

//AMD's sum of absolute differences instruction is a D3D one: the plain search everywhere
#undef FFX_OPTICALFLOW_USE_MSAD4_INSTRUCTION

#include "ffx_opticalflow_common.h"

#ifndef FFX_PREFER_WAVE64
#define FFX_PREFER_WAVE64
#endif

//-- the constants --

//Laid out by CoreFrameGeneration.Constants, a float4 a line, the optical flow's
//first:

//  0  luma size, frame index, transfer function
//  1  least and most luminance, spare, spare

//and what changes pass by pass in the push block's last line - see FfxStep.

[[vk::binding(FFX_RINTEN_CONSTANTS_BINDING, 2)]] StructuredBuffer<float4> g_ffxConstants;

float4 FfxConstant( uint line ) { return g_ffxConstants[line]; }

[[vk::push_constant]]
cbuffer FfxPush
{
	uint4 g_ffxSlots[4];
};

uint FfxSlot( uint binding ) { return g_ffxSlots[binding >> 2][binding & 3]; }

//The pass's own numbers: the pyramid level and how many there are, or SPD's mips and work groups
uint FfxStep( uint which ) { return g_ffxSlots[3][which]; }

FfxInt32x2 DisplaySize() { return FfxInt32x2( FfxConstant( 0 ).xy ); }
FfxUInt32 FrameIndex() { return FfxUInt32( FfxConstant( 0 ).z ); }
FfxUInt32 BackbufferTransferFunction() { return FfxUInt32( FfxConstant( 0 ).w ); }
FfxFloat32x2 MinMaxLuminance() { return FfxConstant( 1 ).xy; }

FfxBoolean CrossedSceneChangeThreshold(FfxFloat32 sceneChangeValue)
{
	return sceneChangeValue > 0.45f;
}

FfxUInt32 OpticalFlowPyramidLevel() { return FfxStep( 0 ); }
FfxUInt32 OpticalFlowPyramidLevelCount() { return FfxStep( 1 ); }

FfxInt32x2 OpticalFlowHistogramMaxVelocity()
{
	const FfxInt32 searchRadius = 8;
	FfxInt32 scale = FfxInt32(1) << (OpticalFlowPyramidLevelCount() - 1 - OpticalFlowPyramidLevel());
	FfxInt32 maxVelocity = searchRadius * scale;
	return FfxInt32x2(maxVelocity, maxVelocity);
}

#if defined(FFX_OPTICALFLOW_BIND_CB_SPD)
FfxUInt32 NumWorkGroups() { return FfxStep( 1 ); }
#endif

//-- what is read --

[[vk::binding(0, 3)]] Sampler2D g_ffxTable[];

#if defined(FFX_OPTICALFLOW_BIND_SRV_INPUT_COLOR)
FfxFloat32x4 LoadInputColor(FfxUInt32x2 iPxHistory)
{
	return g_ffxTable[FfxSlot( FFX_OPTICALFLOW_BIND_SRV_INPUT_COLOR )].Load( int3( iPxHistory, 0 ) );
}
#endif

#if defined(FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW_INPUT)
[[vk::binding(FFX_RINTEN_SRV_BASE + FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW_INPUT, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> r_optical_flow_input;
FfxUInt32 LoadOpticalFlowInput(FfxInt32x2 iPxPos)
{
#if FFX_OPTICALFLOW_USE_MSAD4_INSTRUCTION == 1
	return max(1, r_optical_flow_input[iPxPos]);
#else
	return r_optical_flow_input[iPxPos];
#endif
}
#endif

#if defined(FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW_PREVIOUS_INPUT)
[[vk::binding(FFX_RINTEN_SRV_BASE + FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW_PREVIOUS_INPUT, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> r_optical_flow_previous_input;
FfxUInt32 LoadOpticalFlowPreviousInput(FfxInt32x2 iPxPos)
{
#if FFX_OPTICALFLOW_USE_MSAD4_INSTRUCTION == 1
	return max(1, r_optical_flow_previous_input[iPxPos]);
#else
	return r_optical_flow_previous_input[iPxPos];
#endif
}
#endif

//A stored picture reads nothing outside itself, where AMD's texture read did the same - so no clamp is needed
#if defined(FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW)
[[vk::binding(FFX_RINTEN_SRV_BASE + FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW, 2)]] [format("rg32i")] RWTexture2D<FfxInt32x2> r_optical_flow;
FfxInt32x2 LoadOpticalFlow(FfxInt32x2 iPxPos) { return r_optical_flow[iPxPos]; }
#endif

#if defined(FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW_PREVIOUS)
[[vk::binding(FFX_RINTEN_SRV_BASE + FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW_PREVIOUS, 2)]] [format("rg32i")] RWTexture2D<FfxInt32x2> r_optical_flow_previous;
FfxInt32x2 LoadPreviousOpticalFlow(FfxInt32x2 iPxPos) { return r_optical_flow_previous[iPxPos]; }
#endif

//-- what is written --

#if defined(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT)
[[vk::binding(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> rw_optical_flow_input;
void StoreOpticalFlowInput(FfxInt32x2 iPxPos, FfxUInt32 fLuma) { rw_optical_flow_input[iPxPos] = fLuma; }
FfxUInt32 LoadRwOpticalFlowInput(FfxInt32x2 iPxPos) { return rw_optical_flow_input[iPxPos]; }
#endif

#if defined(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_1)
[[vk::binding(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_1, 2)]] [format("r32ui")] globallycoherent RWTexture2D<FfxUInt32> rw_optical_flow_input_level_1;
[[vk::binding(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_2, 2)]] [format("r32ui")] globallycoherent RWTexture2D<FfxUInt32> rw_optical_flow_input_level_2;
[[vk::binding(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_3, 2)]] [format("r32ui")] globallycoherent RWTexture2D<FfxUInt32> rw_optical_flow_input_level_3;
[[vk::binding(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_4, 2)]] [format("r32ui")] globallycoherent RWTexture2D<FfxUInt32> rw_optical_flow_input_level_4;
[[vk::binding(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_5, 2)]] [format("r32ui")] globallycoherent RWTexture2D<FfxUInt32> rw_optical_flow_input_level_5;
[[vk::binding(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_6, 2)]] [format("r32ui")] globallycoherent RWTexture2D<FfxUInt32> rw_optical_flow_input_level_6;
#endif

#if defined(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW)
[[vk::binding(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW, 2)]] [format("rg32i")] RWTexture2D<FfxInt32x2> rw_optical_flow;
FfxInt32x2 LoadRwOpticalFlow(FfxInt32x2 iPxPos) { return rw_optical_flow[iPxPos]; }
void StoreOpticalFlow(FfxInt32x2 iPxPos, FfxInt32x2 motionVector) { rw_optical_flow[iPxPos] = motionVector; }
#endif

#if defined(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_NEXT_LEVEL)
[[vk::binding(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_NEXT_LEVEL, 2)]] [format("rg32i")] RWTexture2D<FfxInt32x2> rw_optical_flow_next_level;
void StoreOpticalFlowNextLevel(FfxInt32x2 iPxPos, FfxInt32x2 motionVector) { rw_optical_flow_next_level[iPxPos] = motionVector; }
#endif

#if defined(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_SCD_HISTOGRAM)
[[vk::binding(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_SCD_HISTOGRAM, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> rw_optical_flow_scd_histogram;
FfxUInt32 LoadRwSCDHistogram(FfxInt32 iIndex) { return rw_optical_flow_scd_histogram[FfxInt32x2(iIndex, 0)]; }
void StoreSCDHistogram(FfxInt32 iIndex, FfxUInt32 value) { rw_optical_flow_scd_histogram[FfxInt32x2(iIndex, 0)] = value; }
void AtomicIncrementSCDHistogram(FfxInt32 iIndex, FfxUInt32 valueToAdd) { InterlockedAdd(rw_optical_flow_scd_histogram[FfxInt32x2(iIndex, 0)], valueToAdd); }
#endif

#if defined(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_SCD_PREVIOUS_HISTOGRAM)
[[vk::binding(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_SCD_PREVIOUS_HISTOGRAM, 2)]] [format("r32f")] RWTexture2D<FfxFloat32> rw_optical_flow_scd_previous_histogram;
FfxFloat32 LoadRwSCDPreviousHistogram(FfxInt32 iIndex) { return rw_optical_flow_scd_previous_histogram[FfxInt32x2(iIndex, 0)]; }
void StoreSCDPreviousHistogram(FfxInt32 iIndex, FfxFloat32 value) { rw_optical_flow_scd_previous_histogram[FfxInt32x2(iIndex, 0)] = value; }
#endif

#if defined(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_SCD_TEMP)
[[vk::binding(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_SCD_TEMP, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> rw_optical_flow_scd_temp;
FfxUInt32 LoadRwSCDTemp(FfxInt32 iIndex) { return rw_optical_flow_scd_temp[FfxInt32x2(iIndex, 0)]; }
void AtomicIncrementSCDTemp(FfxInt32 iIndex, FfxUInt32 valueToAdd) { InterlockedAdd(rw_optical_flow_scd_temp[FfxInt32x2(iIndex, 0)], valueToAdd); }
void ResetSCDTemp()
{
	rw_optical_flow_scd_temp[FfxInt32x2(0, 0)] = 0;
	rw_optical_flow_scd_temp[FfxInt32x2(1, 0)] = 0;
	rw_optical_flow_scd_temp[FfxInt32x2(2, 0)] = 0;
}
#endif

#if defined(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_SCD_OUTPUT)
[[vk::binding(FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_SCD_OUTPUT, 2)]] [format("r32ui")] RWTexture2D<FfxUInt32> rw_optical_flow_scd_output;
FfxUInt32 LoadRwSCDOutput(FfxInt32 iIndex) { return rw_optical_flow_scd_output[FfxInt32x2(iIndex, 0)]; }
void StoreSCDOutput(FfxInt32 iIndex, FfxUInt32 value) { rw_optical_flow_scd_output[FfxInt32x2(iIndex, 0)] = value; }
FfxUInt32 AtomicIncrementSCDOutput(FfxInt32 iIndex, FfxUInt32 valueToAdd)
{
	FfxUInt32 initialValue;
	InterlockedAdd(rw_optical_flow_scd_output[FfxInt32x2(iIndex, 0)], valueToAdd, initialValue);
	return initialValue;
}

FfxFloat32 GetSceneChangeValue()
{
	if (FrameIndex() <= 5)
		return 1.0;
	else
		return ffxAsFloat(LoadRwSCDOutput(SCD_OUTPUT_SCENE_CHANGE_SLOT));
}

FfxBoolean IsSceneChanged()
{
	if (FrameIndex() <= 5)
		return 1.0;
	else
		return (LoadRwSCDOutput(SCD_OUTPUT_HISTORY_BITS_SLOT) & 0xfu) != 0;
}
#endif

#if defined(FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW_INPUT)
FfxUInt32 LoadFirstImagePackedLuma(FfxInt32x2 iPxPos)
{
	const FfxInt32 lumaTextureWidth = DisplaySize().x >> OpticalFlowPyramidLevel();
	const FfxInt32 lumaTextureHeight = DisplaySize().y >> OpticalFlowPyramidLevel();

	FfxInt32x2 adjustedPos = FfxInt32x2(
		ffxClamp(iPxPos.x, 0, lumaTextureWidth - 4),
		ffxClamp(iPxPos.y, 0, lumaTextureHeight - 1)
	);

	FfxUInt32 luma0 = LoadOpticalFlowInput(adjustedPos + FfxInt32x2(0, 0));
	FfxUInt32 luma1 = LoadOpticalFlowInput(adjustedPos + FfxInt32x2(1, 0));
	FfxUInt32 luma2 = LoadOpticalFlowInput(adjustedPos + FfxInt32x2(2, 0));
	FfxUInt32 luma3 = LoadOpticalFlowInput(adjustedPos + FfxInt32x2(3, 0));

	return GetPackedLuma(lumaTextureWidth, iPxPos.x, luma0, luma1, luma2, luma3);
}
#endif

#if defined(FFX_OPTICALFLOW_BIND_SRV_OPTICAL_FLOW_PREVIOUS_INPUT)
FfxUInt32 LoadSecondImagePackedLuma(FfxInt32x2 iPxPos)
{
	const FfxInt32 lumaTextureWidth = DisplaySize().x >> OpticalFlowPyramidLevel();
	const FfxInt32 lumaTextureHeight = DisplaySize().y >> OpticalFlowPyramidLevel();

	FfxInt32x2 adjustedPos = FfxInt32x2(
		ffxClamp(iPxPos.x, 0, lumaTextureWidth - 4),
		ffxClamp(iPxPos.y, 0, lumaTextureHeight - 1)
	);

	FfxUInt32 luma0 = LoadOpticalFlowPreviousInput(adjustedPos + FfxInt32x2(0, 0));
	FfxUInt32 luma1 = LoadOpticalFlowPreviousInput(adjustedPos + FfxInt32x2(1, 0));
	FfxUInt32 luma2 = LoadOpticalFlowPreviousInput(adjustedPos + FfxInt32x2(2, 0));
	FfxUInt32 luma3 = LoadOpticalFlowPreviousInput(adjustedPos + FfxInt32x2(3, 0));

	return GetPackedLuma(lumaTextureWidth, iPxPos.x, luma0, luma1, luma2, luma3);
}
#endif

void SPD_SetMipmap(int2 iPxPos, int index, float value)
{
	switch (index)
	{
	case 0:
#if defined FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_1
		rw_optical_flow_input_level_1[iPxPos] = FfxUInt32(value);
#endif
		break;
	case 1:
#if defined FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_2
		rw_optical_flow_input_level_2[iPxPos] = FfxUInt32(value);
#endif
		break;
	case 2:
#if defined FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_3
		rw_optical_flow_input_level_3[iPxPos] = FfxUInt32(value);
#endif
		break;
	case 3:
#if defined FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_4
		rw_optical_flow_input_level_4[iPxPos] = FfxUInt32(value);
#endif
		break;
	case 4:
#if defined FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_5
		rw_optical_flow_input_level_5[iPxPos] = FfxUInt32(value);
#endif
		break;
	case 5:
#if defined FFX_OPTICALFLOW_BIND_UAV_OPTICAL_FLOW_INPUT_LEVEL_6
		rw_optical_flow_input_level_6[iPxPos] = FfxUInt32(value);
#endif
		break;
	}
}

#endif // #if defined(FFX_GPU)

#endif // FFX_OPTICALFLOW_CALLBACKS_RINTEN_H
