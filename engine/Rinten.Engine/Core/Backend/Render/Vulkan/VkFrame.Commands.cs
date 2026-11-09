using Silk.NET.Vulkan;

namespace Rinten;

//What a project's own commands record: a draw out of its buffers, and the copies
//and fills a buffer of its is given in the order the frame does its work.
internal static unsafe partial class VkFrame
{
	//Ensures no pass is open for a command that can't be recorded inside one; refuses inside a target pass
	private static bool OutsidePasses( Frame frame, string what )
	{
		if ( Current.TargetPass )
		{
			VkDevice.SetText( $"{what} cannot be recorded while a target is open" );
			return false;
		}

		CloseOpenPasses( frame );

		return true;
	}

	//What a shader or a copy wrote before, made readable and writable by the copy about to follow. A
	//copy in the frame is ordered against the draws and dispatches recorded before it by nothing else.
	private static void BeforeTransfer( Frame frame )
	{
		var barrier = new MemoryBarrier2
		{
			SType = StructureType.MemoryBarrier2,
			SrcStageMask = PipelineStageFlags2.AllCommandsBit,
			SrcAccessMask = AccessFlags2.ShaderStorageWriteBit | AccessFlags2.TransferWriteBit,
			DstStageMask = PipelineStageFlags2.AllTransferBit,
			DstAccessMask = AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit,
		};

		var dependency = new DependencyInfo
		{
			SType = StructureType.DependencyInfo,
			MemoryBarrierCount = 1,
			PMemoryBarriers = &barrier,
		};

		VkDevice.dialect.Barrier( frame.Commands, &dependency );
	}

	//And what the copy wrote, to everything that reads a buffer next - the same readers
	//FrameWrite names, with the vertex and index fetch that a project's own geometry is read by.
	private static void AfterTransfer( Frame frame )
	{
		var barrier = new MemoryBarrier2
		{
			SType = StructureType.MemoryBarrier2,
			SrcStageMask = PipelineStageFlags2.AllTransferBit,
			SrcAccessMask = AccessFlags2.TransferWriteBit,
			DstStageMask = PipelineStageFlags2.ComputeShaderBit | VkDevice.VertexStages |
				PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.DrawIndirectBit |
				PipelineStageFlags2.VertexInputBit | PipelineStageFlags2.AllTransferBit,
			DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit |
				AccessFlags2.IndirectCommandReadBit | AccessFlags2.VertexAttributeReadBit | AccessFlags2.IndexReadBit |
				AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit,
		};

		var dependency = new DependencyInfo
		{
			SType = StructureType.DependencyInfo,
			MemoryBarrierCount = 1,
			PMemoryBarriers = &barrier,
		};

		VkDevice.dialect.Barrier( frame.Commands, &dependency );
	}

	//Bytes from one buffer into another where the frame is, not ahead of it - the copy a command list records lands after the dispatch recorded before it.
	internal static int FrameCopy( ulong from, long fromOffset, ulong into, long intoOffset, long size )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || size <= 0 || fromOffset < 0 || intoOffset < 0 ) return 0;

			var source = VkBuffers.Get( from );
			var target = VkBuffers.Get( into );

			if ( source.Handle == 0 || target.Handle == 0 ) return 0;

			if ( (ulong)fromOffset + (ulong)size > VkBuffers.BytesOf( from ) ||
				(ulong)intoOffset + (ulong)size > VkBuffers.BytesOf( into ) )
			{
				VkDevice.SetText( "that copy runs past the end of a buffer" );
				return 0;
			}

			//One buffer into itself is a copy of a region over another, which the command does not allow where the two overlap.
			if ( from == into && fromOffset < intoOffset + size && intoOffset < fromOffset + size )
			{
				VkDevice.SetText( "a buffer copied over itself" );
				return 0;
			}

			var frame = CurrentFrame;

			if ( !OutsidePasses( frame, "a buffer copy" ) ) return 0;

			BeforeTransfer( frame );

			var copy = new BufferCopy
			{
				SrcOffset = (ulong)fromOffset,
				DstOffset = (ulong)intoOffset,
				Size = (ulong)size,
			};

			VkDevice.vk.CmdCopyBuffer( frame.Commands, source, target, 1, &copy );

			AfterTransfer( frame );

			return 1;
		}
	}

	//A run of a buffer filled with one word, where the frame is. The offset and the length are whole words, which is what the command takes.
	internal static int FrameFill( ulong buffer, long offset, long size, uint value )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || size <= 0 || offset < 0 ) return 0;
			if ( ( offset & 3 ) != 0 || ( size & 3 ) != 0 ) return 0;

			var target = VkBuffers.Get( buffer );
			if ( target.Handle == 0 ) return 0;

			if ( (ulong)offset + (ulong)size > VkBuffers.BytesOf( buffer ) )
			{
				VkDevice.SetText( "that fill runs past the end of the buffer" );
				return 0;
			}

			var frame = CurrentFrame;

			if ( !OutsidePasses( frame, "a buffer fill" ) ) return 0;

			BeforeTransfer( frame );

			VkDevice.vk.CmdFillBuffer( frame.Commands, target, (ulong)offset, (ulong)size, value );

			AfterTransfer( frame );

			return 1;
		}
	}

	//Copies a frame picture into a game texture, every mip, converting format via blit
	internal static int PictureToTexture( ulong pictureId, ulong texture )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording ) return 0;

			var picture = VkPictures.Get( pictureId );
			if ( picture is null ) return 0;

			if ( !VkTextures.ImageOf( texture, out var image, out var width, out var height, out var levels ) )
			{
				VkDevice.SetText( "that texture has no picture of its own to copy into" );
				return 0;
			}

			var frame = CurrentFrame;
			var commands = frame.Commands;

			EndPictureIfOpen( frame );

			if ( !OutsidePasses( frame, "a copy of a picture into a texture" ) ) return 0;

			ReadyLevels( commands, picture );
			LevelTo( commands, picture, 0, ImageLayout.TransferSrcOptimal );

			//Every level is written whole, so what it held is not kept - and whatever last sampled it is behind this in the queue.
			Transition( commands, image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
				PipelineStageFlags2.AllCommandsBit, AccessFlags2.None,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
				levelCount: levels );

			var from = VkPictures.LevelExtent( picture, 0 );

			for ( var level = 0u; level < levels; ++level )
			{
				var blit = new ImageBlit
				{
					SrcSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, 0, 0, 1 ),
					DstSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, level, 0, 1 ),
				};

				blit.SrcOffsets[1] = new Offset3D( (int)from.Width, (int)from.Height, 1 );
				blit.DstOffsets[1] = new Offset3D( (int)Math.Max( width >> (int)level, 1u ), (int)Math.Max( height >> (int)level, 1u ), 1 );

				VkDevice.vk.CmdBlitImage( commands, picture.Image, ImageLayout.TransferSrcOptimal,
					image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear );
			}

			Transition( commands, image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
				PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
				PipelineStageFlags2.AllCommandsBit, AccessFlags2.ShaderSampledReadBit,
				levelCount: levels );

			LevelTo( commands, picture, 0, ImageLayout.ShaderReadOnlyOptimal );

			return 1;
		}
	}

	//The command buffer a piece of texture work goes into where a frame is being recorded, outside
	//its passes - or none, and the work goes with the uploads. See VkTextures.WorkCommands.
	internal static bool FrameWork( out CommandBuffer commands )
	{
		commands = default;

		if ( !Current.Recording ) return false;

		var frame = CurrentFrame;

		if ( !OutsidePasses( frame, "a copy between pictures" ) ) return false;

		commands = frame.Commands;

		return true;
	}

	//Draws from a project's own buffers with the standard sets; 16-bit index binds aren't cached
	internal static int DrawBuffers( CoreBufferDraw* draw, byte* pushData, int pushSize, ulong* buffers, int bufferCount )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording || draw is null ) return 0;

			var indirect = draw->Arguments != 0;

			if ( !indirect && draw->Count <= 0 ) return 0;
			if ( indirect && ( draw->DrawCount <= 0 || draw->ArgumentsOffset < 0 ) ) return 0;

			var bound = VkPipelines.Get( draw->Pipeline );
			var layout = VkPipelines.LayoutGet( draw->Pipeline );
			if ( bound.Handle == 0 || layout.Handle == 0 ) return 0;

			var vertices = draw->Vertices != 0 ? VkBuffers.Get( draw->Vertices ) : default;
			var indices = draw->Indices != 0 ? VkBuffers.Get( draw->Indices ) : default;
			var args = indirect ? VkBuffers.Get( draw->Arguments ) : default;

			if ( draw->Vertices != 0 && vertices.Handle == 0 ) return 0;
			if ( draw->Indices != 0 && indices.Handle == 0 ) return 0;
			if ( indirect && args.Handle == 0 ) return 0;

			//The buffers the shader reads for itself: a set made for this frame out of the ones the draw named, or the pair a mesh keeps.
			DescriptorSet objects = default;

			if ( bufferCount > 0 )
			{
				objects = TransientObjectSet( buffers, bufferCount, "a draw of a project's buffers" );
				if ( objects.Handle == 0 ) return 0;
			}
			else if ( draw->Geometry != 0 )
			{
				objects = VkBuffers.ObjectSetGet( draw->Geometry, draw->Data );
				if ( objects.Handle == 0 ) return 0;
			}

			var frame = CurrentFrame;

			if ( !Current.TargetPass ) EnsureMainPass();

			BindPipeline( frame, bound );
			SetViewport( frame );
			PushConstants( frame, layout, DrawStages, pushData, pushSize );
			BindTexture( frame, layout, draw->Texture );
			BindFrameSet( frame, layout, frame.NullBones );

			if ( objects.Handle != 0 )
			{
				VkDevice.vk.CmdBindDescriptorSets( frame.Commands, PipelineBindPoint.Graphics, layout, 2, 1, &objects, 0, null );
			}

			if ( vertices.Handle != 0 && frame.LastVertexBuffer.Handle != vertices.Handle )
			{
				ulong offset = 0;
				VkDevice.vk.CmdBindVertexBuffers( frame.Commands, 0, 1, &vertices, &offset );
				frame.LastVertexBuffer = vertices;
			}

			if ( indices.Handle != 0 )
			{
				var wide = draw->WideIndices;

				if ( !wide || frame.LastIndexBuffer.Handle != indices.Handle )
				{
					VkDevice.vk.CmdBindIndexBuffer( frame.Commands, indices, 0, wide ? IndexType.Uint32 : IndexType.Uint16 );
				}

				frame.LastIndexBuffer = wide ? indices : default;
			}

			var instances = (uint)System.Math.Max( draw->Instances, 1 );

			if ( indirect )
			{
				//A command a draw, the natural size apart unless the arguments are packed with something
				//of the project's own between them; a device without multi-draw takes them one at a time.
				var natural = indices.Handle != 0 ? (uint)sizeof( DrawIndexedIndirectCommand ) : (uint)sizeof( DrawIndirectCommand );
				var stride = draw->Stride > 0 ? (uint)draw->Stride : natural;
				var most = VkDevice.maxDrawIndirect > 0 ? VkDevice.maxDrawIndirect : 1;
				var from = 0u;

				while ( from < (uint)draw->DrawCount )
				{
					var take = System.Math.Min( (uint)draw->DrawCount - from, most );
					var at = (ulong)draw->ArgumentsOffset + ( from * (ulong)stride );

					if ( indices.Handle != 0 )
						VkDevice.vk.CmdDrawIndexedIndirect( frame.Commands, args, at, take, stride );
					else
						VkDevice.vk.CmdDrawIndirect( frame.Commands, args, at, take, stride );

					from += take;
				}

				frame.StatIndirect++;
			}
			else if ( indices.Handle != 0 )
			{
				VkDevice.vk.CmdDrawIndexed( frame.Commands, (uint)draw->Count, instances, (uint)System.Math.Max( draw->First, 0 ), draw->BaseVertex, 0 );
				frame.StatTriangles += (ulong)draw->Count / 3 * instances;
			}
			else
			{
				VkDevice.vk.CmdDraw( frame.Commands, (uint)draw->Count, instances, (uint)System.Math.Max( draw->First, 0 ), 0 );
				frame.StatTriangles += (ulong)draw->Count / 3 * instances;
			}

			frame.StatDraws++;
			if ( instances > 1 ) frame.StatInstances += instances;

			DrawTrace.End( DrawTrace.Kind.DrawBuffers, traced, inside );

			return 1;
		}
	}
}
