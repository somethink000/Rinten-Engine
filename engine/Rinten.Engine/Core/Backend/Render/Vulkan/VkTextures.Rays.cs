using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Rinten;

internal static unsafe partial class VkTextures
{
	//A run of texels from a buffer into a sampled picture, after the compute that wrote them - see CoreLiveBounce
	internal static bool CopyFromBuffer( CommandBuffer commands, VkBuffer source, ulong offset, ulong texture, int width, int height )
	{
		if ( source.Handle == 0 || !textures.TryGetValue( texture, out var target ) || target.Image.Handle == 0 ) return false;

		var toTransfer = new ImageMemoryBarrier2
		{
			SType = StructureType.ImageMemoryBarrier2,
			SrcStageMask = PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
			SrcAccessMask = AccessFlags2.ShaderSampledReadBit,
			DstStageMask = PipelineStageFlags2.CopyBit,
			DstAccessMask = AccessFlags2.TransferWriteBit,
			OldLayout = ImageLayout.ShaderReadOnlyOptimal,
			NewLayout = ImageLayout.TransferDstOptimal,
			SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
			DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
			Image = target.Image,
			SubresourceRange = new ImageSubresourceRange( ImageAspectFlags.ColorBit, 0, 1, 0, 1 ),
		};

		var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &toTransfer };
		VkDevice.dialect.Barrier( commands, &dependency );

		var copy = new BufferImageCopy
		{
			BufferOffset = offset,
			ImageSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, 0, 0, 1 ),
			ImageExtent = new Extent3D( (uint)width, (uint)height, 1 ),
		};

		VkDevice.vk.CmdCopyBufferToImage( commands, source, target.Image, ImageLayout.TransferDstOptimal, 1, &copy );

		var toSampling = toTransfer;
		toSampling.SrcStageMask = PipelineStageFlags2.CopyBit;
		toSampling.SrcAccessMask = AccessFlags2.TransferWriteBit;
		toSampling.DstStageMask = PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit;
		toSampling.DstAccessMask = AccessFlags2.ShaderSampledReadBit;
		toSampling.OldLayout = ImageLayout.TransferDstOptimal;
		toSampling.NewLayout = ImageLayout.ShaderReadOnlyOptimal;

		dependency.PImageMemoryBarriers = &toSampling;
		VkDevice.dialect.Barrier( commands, &dependency );

		return true;
	}
}
