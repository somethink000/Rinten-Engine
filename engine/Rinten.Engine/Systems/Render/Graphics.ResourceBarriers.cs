using NativeEngine;
using Rinten.Rendering;

namespace Rinten;

public static partial class Graphics
{
	//The Vulkan backend handles all hazards itself, so these only validate and record nothing

	/// <summary>Executes a barrier transition for the given GPU Texture Resource. Transitions the texture resource to a new pipeline stage and access state.</summary>
	public static void ResourceBarrierTransition( Texture texture, ResourceState state, int mip = -1 )
	{
		ArgumentNullException.ThrowIfNull( texture );
	}

	/// <summary>Executes a barrier transition for the given GPU Buffer Resource. Transitions the buffer resource to a new pipeline stage and access state.</summary>
	public static void ResourceBarrierTransition<T>( GpuBuffer<T> buffer, ResourceState state ) where T : unmanaged
	{
		ArgumentNullException.ThrowIfNull( buffer );
	}

	/// <summary>Executes a barrier transition for the given GPU Buffer Resource. Transitions the buffer resource to a new pipeline stage and access state.</summary>
	public static void ResourceBarrierTransition( GpuBuffer buffer, ResourceState state )
	{
		ArgumentNullException.ThrowIfNull( buffer );
	}

	/// <summary>Executes a barrier transition for the given GPU Buffer Resource. Transitions the buffer resource from a known source state to a specified destination state.</summary>
	public static void ResourceBarrierTransition<T>( GpuBuffer<T> buffer, ResourceState before, ResourceState after ) where T : unmanaged
	{
		ArgumentNullException.ThrowIfNull( buffer );
	}

	/// <summary>Executes a barrier transition for the given GPU Buffer Resource. Transitions the buffer resource from a known source state to a specified destination state.</summary>
	public static void ResourceBarrierTransition( GpuBuffer buffer, ResourceState before, ResourceState after )
	{
		ArgumentNullException.ThrowIfNull( buffer );
	}

	/// <summary>Issues a UAV barrier for the given texture, ensuring writes from prior shader invocations are visible to subsequent ones without changing the resource layout.</summary>
	public static void UavBarrier( Texture texture )
	{
		ArgumentNullException.ThrowIfNull( texture );
	}

	/// <summary>Issues a UAV barrier for the given GPU buffer, ensuring writes from prior shader invocations are visible to subsequent ones.</summary>
	public static void UavBarrier( GpuBuffer buffer )
	{
		ArgumentNullException.ThrowIfNull( buffer );
	}

	//Figure out what flags Vulkan needs for the given ResourceState
	private static void ResourceStateToVulkanFlags( ResourceState resourceState, out RenderBarrierPipelineStageFlags_t dstStageFlags, out RenderBarrierAccessFlags_t accessFlags, out RenderImageLayout_t imageLayout, GpuBuffer.UsageFlags bufferUsageFlags = 0 )
	{
		//By default set the barrier to max, we want to use this from anywhere
		dstStageFlags = RenderBarrierPipelineStageFlags_t.TopOfPipeBit;
		accessFlags = 0;
		imageLayout = RenderImageLayout_t.RENDER_IMAGE_LAYOUT_GENERAL;

		//We can refine usage from the given state, the only thing we gain from getting this right is performance In terms
		//of everything working the above case covers everything TODO: These states are FLAGS, we should be combining them
		switch ( resourceState )
		{
			case ResourceState.GenericRead:
				break;
			case ResourceState.VertexOrIndexBuffer:
				dstStageFlags = RenderBarrierPipelineStageFlags_t.VertexInputBit;

				if ( ( ( bufferUsageFlags & GpuBuffer.UsageFlags.Index ) == GpuBuffer.UsageFlags.Index ) )
					accessFlags = RenderBarrierAccessFlags_t.IndexReadBit;

				if ( ( ( bufferUsageFlags & GpuBuffer.UsageFlags.Vertex ) == GpuBuffer.UsageFlags.Vertex ) )
					accessFlags = RenderBarrierAccessFlags_t.VertexAttributeReadBit;

				break;
			case ResourceState.RenderTarget:
				imageLayout = RenderImageLayout_t.RENDER_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
				accessFlags = RenderBarrierAccessFlags_t.ColorAttachmentReadBit | RenderBarrierAccessFlags_t.ColorAttachmentWriteBit;
				dstStageFlags = RenderBarrierPipelineStageFlags_t.ColorAttachmentOutputBit;
				break;
			case ResourceState.UnorderedAccess:
				accessFlags = RenderBarrierAccessFlags_t.ShaderReadBit | RenderBarrierAccessFlags_t.ShaderWriteBit;
				dstStageFlags = RenderBarrierPipelineStageFlags_t.FragmentShaderBit | RenderBarrierPipelineStageFlags_t.ComputeShaderBit;
				break;
			case ResourceState.DepthRead:
				imageLayout = RenderImageLayout_t.RENDER_IMAGE_LAYOUT_DEPTH_STENCIL_READ_ONLY_OPTIMAL;
				accessFlags = RenderBarrierAccessFlags_t.DepthStencilAttachmentReadBit;
				dstStageFlags = RenderBarrierPipelineStageFlags_t.FragmentShaderBit;
				break;
			case ResourceState.DepthWrite:
				imageLayout = RenderImageLayout_t.RENDER_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL;
				accessFlags = RenderBarrierAccessFlags_t.DepthStencilAttachmentReadBit | RenderBarrierAccessFlags_t.DepthStencilAttachmentWriteBit;
				dstStageFlags = RenderBarrierPipelineStageFlags_t.FragmentShaderBit;
				break;
			case ResourceState.NonPixelShaderResource:
				imageLayout = RenderImageLayout_t.RENDER_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
				accessFlags = RenderBarrierAccessFlags_t.ShaderReadBit;
				dstStageFlags = RenderBarrierPipelineStageFlags_t.PreRasterizationShadersBit | RenderBarrierPipelineStageFlags_t.ComputeShaderBit;
				break;
			case ResourceState.PixelShaderResource:
				imageLayout = RenderImageLayout_t.RENDER_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
				accessFlags = RenderBarrierAccessFlags_t.ShaderReadBit;
				dstStageFlags = RenderBarrierPipelineStageFlags_t.FragmentShaderBit;
				break;
			case ResourceState.CopyDestination:
				imageLayout = RenderImageLayout_t.RENDER_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
				accessFlags = RenderBarrierAccessFlags_t.TransferWriteBit;
				dstStageFlags = RenderBarrierPipelineStageFlags_t.TransferBit;
				break;
			case ResourceState.CopySource:
				imageLayout = RenderImageLayout_t.RENDER_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
				accessFlags = RenderBarrierAccessFlags_t.TransferReadBit;
				dstStageFlags = RenderBarrierPipelineStageFlags_t.TransferBit;
				break;
			case ResourceState.IndirectArgument:
				accessFlags = RenderBarrierAccessFlags_t.IndirectCommandReadBit;
				dstStageFlags = RenderBarrierPipelineStageFlags_t.DrawIndirectBit;
				break;
		}
	}
}
