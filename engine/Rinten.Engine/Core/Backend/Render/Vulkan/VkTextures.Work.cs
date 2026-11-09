using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Rinten;

//Project-requested GPU work (copies, mip builds, fills), recorded in frame or upload order
internal static unsafe partial class VkTextures
{
	//The batch of uploads being recorded, opened if there is none, for work that goes with them rather
	//than with a frame. Its staging is left alone: a copy that reads nothing from the host needs none.
	private static CommandBuffer BatchCommands()
	{
		if ( batch is not null ) return batch.Commands;

		var slot = TakeUploadContext();
		if ( slot is null ) return default;

		var begin = new CommandBufferBeginInfo
		{
			SType = StructureType.CommandBufferBeginInfo,
			Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
		};

		if ( VkDevice.vk.BeginCommandBuffer( slot.Commands, &begin ) != Result.Success )
		{
			VkDevice.SetText( "a batch of texture work could not be started" );
			return default;
		}

		slot.StagingUsed = 0;
		slot.Open = true;
		batch = slot;

		return slot.Commands;
	}

	//Where a piece of work on a picture is recorded: the frame being recorded,
	//outside its passes, or the batch of uploads. False where neither will take it.
	private static bool WorkCommands( out CommandBuffer commands )
	{
		if ( VkSwapchain.Current is { Recording: true } ) return VkFrame.FrameWork( out commands );

		commands = BatchCommands();

		return commands.Handle != 0;
	}

	//Whether a format is one of the block-compressed kinds, which are copied block for block and never blitted - the hardware will not filter between them.
	private static bool Compressed( Format format )
		=> format >= Format.BC1RgbUnormBlock && format <= Format.Astc12x12SrgbBlock;

	//One level of a texture's own image, or false with the reason said.
	private static bool Picture( ulong handle, int level, out GpuTexture texture, out uint width, out uint height )
	{
		width = height = 0;

		if ( !textures.TryGetValue( handle, out texture ) )
		{
			VkDevice.SetText( "there is no texture by that handle" );
			return false;
		}

		//A material or an adopted view names pictures it does not own.
		if ( !texture.OwnsImage || texture.Image.Handle == 0 || texture.Width == 0 )
		{
			VkDevice.SetText( "that texture has no picture of its own" );
			return false;
		}

		if ( level < 0 || level >= (int)System.Math.Max( texture.MipCount, 1u ) )
		{
			VkDevice.SetText( "that texture has no such level" );
			return false;
		}

		width = System.Math.Max( texture.Width >> level, 1u );
		height = System.Math.Max( texture.Height >> level, 1u );

		return true;
	}

	//One level of one texture into one level of another: a copy where the two are the same shape and format, a blit
	//that converts and scales where they are not. Both are left where a shader samples them, as every texture here is.
	internal static int CopyTexture( ulong from, int fromLevel, ulong into, int intoLevel )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !VkDevice.Ready ) return 0;

			if ( !Picture( from, fromLevel, out var source, out var sourceWidth, out var sourceHeight ) ) return 0;
			if ( !Picture( into, intoLevel, out var target, out var targetWidth, out var targetHeight ) ) return 0;

			if ( source.Image.Handle == target.Image.Handle )
			{
				VkDevice.SetText( "a texture copied into itself" );
				return 0;
			}

			var same = source.Format == target.Format && sourceWidth == targetWidth && sourceHeight == targetHeight;

			if ( !same && ( Compressed( source.Format ) || Compressed( target.Format ) ) )
			{
				VkDevice.SetText( "a compressed texture is copied only into one of its own shape and format" );
				return 0;
			}

			if ( !WorkCommands( out var commands ) ) return 0;

			VkFrame.Transition( commands, source.Image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal,
				PipelineStageFlags2.AllCommandsBit, AccessFlags2.None,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit,
				baseLevel: (uint)fromLevel );

			//The whole level is written, so what it held is not worth keeping.
			VkFrame.Transition( commands, target.Image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal,
				PipelineStageFlags2.AllCommandsBit, AccessFlags2.None,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
				baseLevel: (uint)intoLevel );

			if ( same )
			{
				var region = new ImageCopy
				{
					SrcSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, (uint)fromLevel, 0, 1 ),
					DstSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, (uint)intoLevel, 0, 1 ),
					Extent = new Extent3D( sourceWidth, sourceHeight, 1 ),
				};

				VkDevice.vk.CmdCopyImage( commands, source.Image, ImageLayout.TransferSrcOptimal,
					target.Image, ImageLayout.TransferDstOptimal, 1, &region );
			}
			else
			{
				var blit = new ImageBlit
				{
					SrcSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, (uint)fromLevel, 0, 1 ),
					DstSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, (uint)intoLevel, 0, 1 ),
				};

				blit.SrcOffsets[1] = new Offset3D( (int)sourceWidth, (int)sourceHeight, 1 );
				blit.DstOffsets[1] = new Offset3D( (int)targetWidth, (int)targetHeight, 1 );

				VkDevice.vk.CmdBlitImage( commands, source.Image, ImageLayout.TransferSrcOptimal,
					target.Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear );
			}

			VkFrame.Transition( commands, source.Image, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.None,
				PipelineStageFlags2.AllCommandsBit, AccessFlags2.None,
				baseLevel: (uint)fromLevel );

			VkFrame.Transition( commands, target.Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
				PipelineStageFlags2.AllCommandsBit, AccessFlags2.ShaderSampledReadBit,
				baseLevel: (uint)intoLevel );

			return 1;
		}
	}

	//Every level of a texture cleared to one colour. The colour is the game's, in the space a picture
	//is written in; a texture read as sRGB takes it decoded, which is what the clear writes into one.
	internal static int ClearTexture( ulong handle, float r, float g, float b, float a )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !VkDevice.Ready ) return 0;

			if ( !Picture( handle, 0, out var texture, out _, out _ ) ) return 0;

			if ( Compressed( texture.Format ) )
			{
				VkDevice.SetText( "a compressed texture cannot be cleared to a colour" );
				return 0;
			}

			if ( !WorkCommands( out var commands ) ) return 0;

			var levels = System.Math.Max( texture.MipCount, 1u );

			VkFrame.Transition( commands, texture.Image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
				PipelineStageFlags2.AllCommandsBit, AccessFlags2.None,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
				levelCount: levels );

			var srgb = texture.Format is Format.R8G8B8A8Srgb or Format.B8G8R8A8Srgb;

			var colour = new ClearColorValue(
				srgb ? Decoded( r ) : r,
				srgb ? Decoded( g ) : g,
				srgb ? Decoded( b ) : b,
				a );

			var range = new ImageSubresourceRange( ImageAspectFlags.ColorBit, 0, levels, 0, 1 );

			VkDevice.vk.CmdClearColorImage( commands, texture.Image, ImageLayout.TransferDstOptimal, &colour, 1, &range );

			VkFrame.Transition( commands, texture.Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
				PipelineStageFlags2.AllCommandsBit, AccessFlags2.ShaderSampledReadBit,
				levelCount: levels );

			return 1;
		}
	}

	//One channel out of the sRGB curve, into light.
	private static float Decoded( float value )
	{
		value = System.Math.Clamp( value, 0.0f, 1.0f );

		return value <= 0.04045f ? value / 12.92f : System.MathF.Pow( ( value + 0.055f ) / 1.055f, 2.4f );
	}

	//A texture's levels, each the linear blit of the one above, from first down count
	//of them - or to the last, for a count under one. The first is left as it is.
	internal static int GenerateMips( ulong handle, int first, int count )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !VkDevice.Ready ) return 0;

			if ( !Picture( handle, System.Math.Max( first, 0 ), out var texture, out _, out _ ) ) return 0;

			var levels = (int)System.Math.Max( texture.MipCount, 1u );

			//Nothing below the first to fill, which is a texture with one level and not a failure.
			if ( first + 1 >= levels ) return 1;

			if ( Compressed( texture.Format ) )
			{
				VkDevice.SetText( "a compressed texture's levels are made when it is compiled, not blitted" );
				return 0;
			}

			var last = count < 1 ? levels - 1 : System.Math.Min( levels - 1, first + count );

			if ( !WorkCommands( out var commands ) ) return 0;

			VkFrame.Transition( commands, texture.Image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal,
				PipelineStageFlags2.AllCommandsBit, AccessFlags2.None,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit,
				baseLevel: (uint)first );

			for ( var level = first + 1; level <= last; level++ )
			{
				VkFrame.Transition( commands, texture.Image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal,
					PipelineStageFlags2.AllCommandsBit, AccessFlags2.None,
					PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
					baseLevel: (uint)level );

				var blit = new ImageBlit
				{
					SrcSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, (uint)( level - 1 ), 0, 1 ),
					DstSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, (uint)level, 0, 1 ),
				};

				blit.SrcOffsets[1] = new Offset3D( (int)System.Math.Max( texture.Width >> ( level - 1 ), 1u ),
					(int)System.Math.Max( texture.Height >> ( level - 1 ), 1u ), 1 );
				blit.DstOffsets[1] = new Offset3D( (int)System.Math.Max( texture.Width >> level, 1u ),
					(int)System.Math.Max( texture.Height >> level, 1u ), 1 );

				VkDevice.vk.CmdBlitImage( commands, texture.Image, ImageLayout.TransferSrcOptimal,
					texture.Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear );

				//What was written is what the next level is blitted out of.
				VkFrame.Transition( commands, texture.Image, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal,
					PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
					PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit,
					baseLevel: (uint)level );
			}

			VkFrame.Transition( commands, texture.Image, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
				PipelineStageFlags2.AllCommandsBit, AccessFlags2.ShaderSampledReadBit,
				baseLevel: (uint)first, levelCount: (uint)( last - first + 1 ) );

			return 1;
		}
	}

	//Fills a buffer run with one word in its own submission, waiting on earlier work first
	internal static bool FillBuffer( VkBuffer destination, ulong offset, ulong size, uint value )
	{
		if ( destination.Handle == 0 || size == 0 ) return false;

		//After the uploads batched before it, which may write where it fills
		SubmitBatch();

		var slot = TakeUploadContext();
		if ( slot is null ) return false;

		var vk = VkDevice.vk;
		var commands = slot.Commands;

		var begin = new CommandBufferBeginInfo
		{
			SType = StructureType.CommandBufferBeginInfo,
			Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
		};

		vk.BeginCommandBuffer( commands, &begin );

		var before = new MemoryBarrier2
		{
			SType = StructureType.MemoryBarrier2,
			SrcStageMask = PipelineStageFlags2.AllCommandsBit,
			SrcAccessMask = AccessFlags2.MemoryWriteBit,
			DstStageMask = PipelineStageFlags2.AllTransferBit,
			DstAccessMask = AccessFlags2.TransferWriteBit,
		};

		var dependency = new DependencyInfo
		{
			SType = StructureType.DependencyInfo,
			MemoryBarrierCount = 1,
			PMemoryBarriers = &before,
		};

		VkDevice.dialect.Barrier( commands, &dependency );

		vk.CmdFillBuffer( commands, destination, offset, size, value );

		//The same readers an upload names.
		var after = new MemoryBarrier2
		{
			SType = StructureType.MemoryBarrier2,
			SrcStageMask = PipelineStageFlags2.AllTransferBit,
			SrcAccessMask = AccessFlags2.TransferWriteBit,
			DstStageMask = PipelineStageFlags2.VertexInputBit |
				VkDevice.VertexStages | PipelineStageFlags2.FragmentShaderBit |
				PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.DrawIndirectBit |
				PipelineStageFlags2.AllTransferBit,
			DstAccessMask = AccessFlags2.VertexAttributeReadBit |
				AccessFlags2.IndexReadBit | AccessFlags2.ShaderStorageReadBit |
				AccessFlags2.ShaderStorageWriteBit | AccessFlags2.IndirectCommandReadBit |
				AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit,
		};

		dependency.PMemoryBarriers = &after;
		VkDevice.dialect.Barrier( commands, &dependency );

		vk.EndCommandBuffer( commands );

		var commandSubmit = new CommandBufferSubmitInfo
		{
			SType = StructureType.CommandBufferSubmitInfo,
			CommandBuffer = commands,
		};

		var submit = new SubmitInfo2
		{
			SType = StructureType.SubmitInfo2,
			CommandBufferInfoCount = 1,
			PCommandBufferInfos = &commandSubmit,
		};

		var submitted = VkDevice.dialect.Submit( VkDevice.graphicsQueue, &submit, slot.Fence );

		if ( submitted != Result.Success )
		{
			VkDevice.MarkLostIf( submitted );
			VkDevice.SetText( $"the buffer would not fill ({ResultName( submitted )})" );
			return false;
		}

		slot.Pending = true;

		return true;
	}
}
