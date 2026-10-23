using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;

namespace Rinten;

//What a frame does: the passes it opens, the draws it records, the dispatches between them, and the profiler that measures all of it.
internal static unsafe partial class VkFrame
{
	//-- the frame ----------------------------------------------------------

	internal static int FrameBegin( ulong handle, float r, float g, float b, float a )
	{
		lock ( VkDevice.renderLock )
		{
			//The frame before this one out of the submit thread's hands, and what the queue and the display said about it acted on - see VkSubmitter. Before
			//anything here, because the acquire below and that frame's present may not run at once, and what they said may be that this swapchain has gone.
			//Yielded rather than drained: a frame left waiting to be shown after a generated one takes this lock before it is presented.
			VkSubmitter.Yield();
			VkSubmitter.Settle();

			//A device that has been lost stays lost until it is shut down and brought up again. Every call past this
			//point would fail anyway; refusing here keeps the failure to one line in the log rather than one per frame.
			if ( VkDevice.Lost ) return 0;

			//A frame belongs to one swapchain from here until frame end, and that is what everything drawn in between draws into. Nothing
			//else may begin one meanwhile - one frame at a time is the whole model, and a view drawn on its own is a frame like any other.
			var found = VkSwapchain.FindSwapchain( handle );
			if ( found is null ) return 0;

			if ( !ReferenceEquals( VkSwapchain.active, VkSwapchain.placeholder ) ||
				VkSwapchain.activeFrame is not null || VkSwapchain.placeholder.Recording ) return 0;

			VkSwapchain.active = found;

			if ( Current.Handle.Handle == 0 || Current.Recording )
			{
				VkSwapchain.active = VkSwapchain.placeholder;
				return 0;
			}

			//The frame's pictures nobody has asked for in a while - see VkPictures.
			VkPictures.Sweep();

			//A window that has changed shape but still presents is rebuilt once its shape has stood still, rather than once for every frame of the drag.
			var settled = VkSwapchain.SuboptimalSettled();

			//A present on the submit thread found the surface gone: built again now, the way the frame end would have done it there and then.
			if ( Current.SurfaceLost )
			{
				Current.SurfaceLost = false;

				if ( !VkSwapchain.RebuildTheSurface() ) Current.OutOfDate = true;

				VkSwapchain.active = VkSwapchain.placeholder;
				return 0;
			}

			//A frame that ended with the window resized rebuilds now rather than at the moment it went wrong, when there was still work in flight.
			if ( ( Current.OutOfDate || settled ) && !VkSwapchain.Rebuild() )
			{
				VkSwapchain.active = VkSwapchain.placeholder;
				return 0;
			}

			var vk = VkDevice.vk;
			var device = VkDevice.device;

			var frame = CurrentFrame;

			//Wait for this frame's last outing to finish before recording over its command buffer. Two frames back, so in practice this does not block.
			if ( frame.Submitted )
			{
				//A wait that fails means the device is gone, not that the GPU finished. Nothing may be recorded over a command buffer it may still hold.
				var fence = frame.Finished;

				Result waited;

				//Separate zone for waiting on our own GPU work vs. the display; a third frame in flight didn't help
				using ( Diagnostics.Performance.Scope( "ui wait card" ) )
					waited = vk.WaitForFences( device, 1, &fence, true, ulong.MaxValue );

				if ( waited != Result.Success )
				{
					VkDevice.MarkLostIf( waited );
					VkDevice.SetText( waited == Result.ErrorDeviceLost
						? "the device was lost waiting for a frame"
						: "a frame never finished" );

					VkSwapchain.active = VkSwapchain.placeholder;
					return 0;
				}

				vk.ResetFences( device, 1, &fence );
				frame.Submitted = false;

				//The fence proved that submission finished: the graveyard may bury what was waiting on it, and the frame's measurements are readable.
				VkDevice.TicketDone( frame.Ticket );
				ResolveProfile( frame, Current.Profile );
			}

			//Don't wait forever on acquire: hidden windows may never get an image; after one stall, just poll
			var patience = Current.DisplayStalled ? 0ul : VkSwapchain.AcquireTimeoutNs;

			uint imageIndex = 0;

			Result acquired;

			using ( Diagnostics.Performance.Scope( "ui wait display" ) )
				acquired = VkDevice.khrSwapchain.AcquireNextImage( device, Current.Handle, patience,
					frame.ImageAvailable, default, ref imageIndex );

			Current.ImageIndex = imageIndex;

			if ( acquired == Result.Timeout || acquired == Result.NotReady )
			{
				//Nothing was acquired and nothing was signalled, so there is nothing to unwind: the next frame asks again with the same semaphore.
				Current.DisplayStalled = true;

				VkDevice.SetText( "the display is not taking frames" );

				VkSwapchain.active = VkSwapchain.placeholder;
				return 0;
			}

			Current.DisplayStalled = false;

			if ( acquired == Result.ErrorSurfaceLostKhr )
			{
				//The window survives; only the connection to it has gone. Building a new surface
				//for the same window lets the next frame carry on as though nothing happened.
				if ( !VkSwapchain.RebuildTheSurface() ) Current.OutOfDate = true;

				VkSwapchain.active = VkSwapchain.placeholder;
				return 0;
			}

			if ( acquired == Result.ErrorOutOfDateKhr )
			{
				//Rebuild and skip. Acquiring again immediately would work, but a frame dropped while the window is being dragged is not worth the extra path.
				Current.OutOfDate = true;
				VkSwapchain.Rebuild();

				VkSwapchain.active = VkSwapchain.placeholder;
				return 0;
			}

			if ( acquired != Result.Success && acquired != Result.SuboptimalKhr )
			{
				//Said first, so that a device lost overwrites it with the specific reason. Anything
				//left here is either the device going or something nobody on this side can act on.
				VkDevice.SetText( "no swapchain image could be acquired" );
				VkDevice.MarkLostIf( acquired );

				if ( acquired == Result.ErrorDeviceLost )
					VkDevice.SetText( "the device was lost acquiring an image" );

				VkSwapchain.active = VkSwapchain.placeholder;
				return 0;
			}

			//Suboptimal means it still works but the window has changed shape. Draw this frame and rebuild after presenting it.
			if ( acquired == Result.SuboptimalKhr ) VkSwapchain.MarkSuboptimal();

			if ( !BeginRecording( frame ) )
			{
				//The image was acquired before this could fail, and its semaphore has been signalled. Nothing is going to wait on
				//it now, and a signalled semaphore handed to the next acquire is a hang - so it is consumed here, on the way out.
				DrainAcquiredImage( frame );

				VkDevice.SetText( "no room in the frame for the camera and lights" );
				Current.OutOfDate = true;

				VkSwapchain.active = VkSwapchain.placeholder;
				return 0;
			}

			//Recording from here, so the wait below can be measured as a zone of its own: it is the first thing the card
			//does for the frame, and it used to land in whichever zone came first and be read as that zone's own cost.
			Current.Recording = true;
			Current.DepthCeiling = 1.0f;

			ZoneBegin( "image wait" );

			//From Undefined since it gets cleared; at the semaphore's wait stage so it runs after the acquire
			Transition( frame.Commands, Current.Images[Current.ImageIndex],
				ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
				PipelineStageFlags2.ColorAttachmentOutputBit, 0,
				PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit );

			//Depth is shared by two frames in flight; wait for the previous frame's depth writes
			Transition( frame.Commands, Pics.DepthImage,
				ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal,
				PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
				AccessFlags2.DepthStencilAttachmentWriteBit,
				PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
				AccessFlags2.DepthStencilAttachmentWriteBit,
				ImageAspectFlags.DepthBit );

			ZoneEnd();

			//And the sky's table, the once: cleared, so that a frame which says it has one before anything has drawn into it reads nought rather than whatever
			//the memory held. See SkyTableBegin, which draws it, and SkyTableAt in frame.slang, which reads a nought alpha as "this frame has no table".
			ClearSkyTableOnce( frame.Commands );

			//The pass that draws the world does not begin here. It waits until something draws, so the sun can have its own pass
			//in front of it - dynamic rendering does not nest, and nothing may ask what the sun saw while it is still looking.
			Current.Clear[0] = r;
			Current.Clear[1] = g;
			Current.Clear[2] = b;
			Current.Clear[3] = a;

			Current.MainPass = false;
			Current.MainBegun = false;
			Current.BackdropTaken = false;
			Current.ResumedLastFrame = Current.ResumedThisFrame;
			Current.ResumedThisFrame = false;

			Current.TargetPass = false;
			Current.Target = VkDevice.TargetScreen;

			ResetPictures( Current.Pics );

			WriteFrameSet( frame );

			//The window's depth was moved a few lines up, before anything could want it - a view's has not been touched yet and stays where ResetPictures put it.
			Current.Pics.DepthLayout = ImageLayout.DepthAttachmentOptimal;

			Current.Recording = true;
			Current.DepthCeiling = 1.0f;
			return 1;
		}
	}

	internal static int FrameSetup( float* data, int floatCount )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || data is null || floatCount <= 0 ) return 0;

			var frame = CurrentFrame;

			//Clamped rather than refused: a caller with more lights than the frame can hold has
			//already been told the limit, and dropping the tail is better than dropping the picture.
			var bytes = (ulong)floatCount * sizeof( float );
			if ( bytes > VkDevice.FrameBlockSize ) bytes = VkDevice.FrameBlockSize;

			System.Buffer.MemoryCopy( data, frame.ScratchMapped + frame.FrameBlock, bytes, bytes );
			return 1;
		}
	}

	//The second eye's frame block, beside the first - read by a pass drawing both eyes at once, at the second layer; see VkDevice.FrameBlockReach.
	internal static int FrameSetupSecondEye( float* data, int floatCount )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || data is null || floatCount <= 0 ) return 0;

			var frame = CurrentFrame;

			var bytes = (ulong)floatCount * sizeof( float );
			if ( bytes > VkDevice.FrameBlockSize ) bytes = VkDevice.FrameBlockSize;

			System.Buffer.MemoryCopy( data, frame.ScratchMapped + frame.FrameBlock + VkDevice.FrameBlockSize, bytes, bytes );
			return 1;
		}
	}

	//A run of the frame block written again, part way through the frame - the point effects' numbers, written where each chain of them
	//is drawn. Only a run nothing drawn before reads: every draw reads the block when the frame is submitted. See CorePointChain.Upload.
	internal static int FrameBlockPatch( int floatOffset, float* data, int floatCount )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || data is null || floatCount <= 0 || floatOffset < 0 ) return 0;

			var at = (ulong)floatOffset * sizeof( float );
			var bytes = (ulong)floatCount * sizeof( float );

			if ( at + bytes > VkDevice.FrameBlockSize ) return 0;

			var frame = CurrentFrame;

			System.Buffer.MemoryCopy( data, frame.ScratchMapped + frame.FrameBlock + at, bytes, bytes );
			return 1;
		}
	}

	//Copies a second frame block (e.g. a light's view) into scratch; returns its offset, or 0 if no room
	internal static ulong FrameBlockPut( float* data, int floatCount )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || data is null || floatCount <= 0 ) return 0;

			var frame = CurrentFrame;

			var alignment = (ulong)VkDevice.storageAlignment;
			var aligned = ( frame.ScratchUsed + alignment - 1 ) / alignment * alignment;
			var padding = aligned - frame.ScratchUsed;

			if ( !EnsureScratch( frame, padding + VkDevice.FrameBlockReach ) ) return 0;

			var bytes = (ulong)floatCount * sizeof( float );
			if ( bytes > VkDevice.FrameBlockSize ) bytes = VkDevice.FrameBlockSize;

			//The room a set bound here reaches over - see FrameBlockReach - with nothing in the second eye's half: a light's view is one eye.
			var into = frame.ScratchMapped + aligned;
			System.Buffer.MemoryCopy( data, into, VkDevice.FrameBlockReach, bytes );
			System.Runtime.InteropServices.NativeMemory.Clear( into + bytes, (nuint)( VkDevice.FrameBlockReach - bytes ) );

			frame.ScratchUsed = aligned + VkDevice.FrameBlockReach;
			return aligned;
		}
	}

	//Which frame block the draws from here on read - one FrameBlockPut made, or the one it returned to put back;
	//what was in use before. The set is bound again on the next draw, whose offsets would otherwise look unmoved.
	internal static ulong FrameBlockUse( ulong offset )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording ) return 0;

			var frame = CurrentFrame;
			var was = frame.FrameBlock;

			frame.FrameBlock = offset;
			frame.FrameSetBound = false;

			return was;
		}
	}

	internal static int FrameEnd()
	{
		lock ( VkDevice.renderLock )
		{
			//A view drawn on its own keeps its pass state in the placeholder, so being told a frame is open is not enough - it has to be a window's.
			if ( !Current.Recording || ReferenceEquals( VkSwapchain.active, VkSwapchain.placeholder ) ) return 0;

			var vk = VkDevice.vk;

			try
			{
				var frame = CurrentFrame;

				//A frame where the sun looked and nothing else drew still has a pass to close, and a frame where nothing drew at all still owes the screen its clear.

				EnsureMainPass();

				VkDevice.dialect.EndRendering( frame.Commands );
				Current.MainPass = false;

				//The frame between the last and this one, in a second image, where the world was kept for one
				var generated = GenerateBeforeEnd( frame, out var generatedImage );

				//The buffers somebody asked to have back, read after everything the frame did to them - see VkReadbacks.
				VkReadbacks.RecordInto( frame );

				//The next frame's shading rates, off what this one is about to show - see VkFrame.Adaptive. Inside the frame's own timestamps,
				//so what it costs is counted with the rest. The image comes out of it in the layout a shader reads, and goes on from there.
				var ratesZone = ZoneBegin( "shading rates" );
				var readForRates = AdaptiveRead( frame );
				if ( ratesZone != 0 ) ZoneEnd();

				var shownLayout = readForRates ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.ColorAttachmentOptimal;
				var shownStage = readForRates ? PipelineStageFlags2.ComputeShaderBit : PipelineStageFlags2.ColorAttachmentOutputBit;
				var shownAccess = readForRates ? AccessFlags2.ShaderSampledReadBit : AccessFlags2.ColorAttachmentWriteBit;

				//The frame's own pair of timestamps closes here, with any zone somebody forgot to end
				//- every reserved query has to be written or the readback would find nothing ready.
				CloseOpenZones( frame );
				Current.Recording = false;

				if ( frame.Queries.Handle != 0 )
					VkDevice.dialect.Timestamp( frame.Commands, PipelineStageFlags2.AllCommandsBit, frame.Queries, 1 );

				//A copy of what is about to be shown, where somebody asked to keep one - see Swapchain.ReadBuffer. Recorded here because this is the last moment
				//the image is ours and the frame is open. And into this frame's recording copy, where a recording of the screen is running - see Swapchain.Captures.
				var capture = Current.Captures is { } captures && Current.Frame < captures.Length ? captures[Current.Frame] : null;

				if ( capture is not null && ( capture.Mapped is null ||
					capture.Extent.Width != Current.Extent.Width || capture.Extent.Height != Current.Extent.Height ) )
					capture = null;

				var keepsRead = Current.ReadBuffer.Handle != 0 && Current.ReadMapped is not null;

				if ( keepsRead || capture is not null )
				{
					Transition( frame.Commands, Current.Images[Current.ImageIndex],
						shownLayout, ImageLayout.TransferSrcOptimal,
						shownStage, shownAccess,
						PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit );

					if ( keepsRead )
					{
						//One read buffer per window and every frame in flight copies into it, so the frame before may not have finished its
						//copy yet - two writes on one queue are ordered, their memory is not. The same barrier the view's read buffer has.
						var previous = new BufferMemoryBarrier2
						{
							SType = StructureType.BufferMemoryBarrier2,
							SrcStageMask = PipelineStageFlags2.AllTransferBit,
							SrcAccessMask = AccessFlags2.TransferWriteBit,
							DstStageMask = PipelineStageFlags2.AllTransferBit,
							DstAccessMask = AccessFlags2.TransferWriteBit,
							SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
							DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
							Buffer = Current.ReadBuffer,
							Size = Vk.WholeSize,
						};

						var dependency = new DependencyInfo
						{
							SType = StructureType.DependencyInfo,
							BufferMemoryBarrierCount = 1,
							PBufferMemoryBarriers = &previous,
						};

						VkDevice.dialect.Barrier( frame.Commands, &dependency );

						var copy = new BufferImageCopy
						{
							ImageSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, 0, 0, 1 ),
							ImageExtent = new Extent3D( Current.ReadExtent.Width, Current.ReadExtent.Height, 1 ),
						};

						vk.CmdCopyImageToBuffer( frame.Commands, Current.Images[Current.ImageIndex],
							ImageLayout.TransferSrcOptimal, Current.ReadBuffer, 1, &copy );

						Current.ReadPending = true;
					}

					if ( capture is not null )
					{
						var copy = new BufferImageCopy
						{
							ImageSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, 0, 0, 1 ),
							ImageExtent = new Extent3D( capture.Extent.Width, capture.Extent.Height, 1 ),
						};

						vk.CmdCopyImageToBuffer( frame.Commands, Current.Images[Current.ImageIndex],
							ImageLayout.TransferSrcOptimal, capture.Buffer, 1, &copy );

						capture.Stamp = ++Current.CaptureCount;
						capture.Pending = true;
					}

					Transition( frame.Commands, Current.Images[Current.ImageIndex],
						ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr,
						PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit,
						PipelineStageFlags2.BottomOfPipeBit, 0 );
				}
				else
				{
					Transition( frame.Commands, Current.Images[Current.ImageIndex],
						shownLayout, ImageLayout.PresentSrcKhr,
						shownStage, shownAccess,
						PipelineStageFlags2.BottomOfPipeBit, 0 );
				}

				vk.EndCommandBuffer( frame.Commands );
				frame.CpuEnd = VkSwapchain.NowNs();

				var wait = stackalloc SemaphoreSubmitInfo[2];
				var signal = stackalloc SemaphoreSubmitInfo[2];

				wait[0] = new SemaphoreSubmitInfo
				{
					SType = StructureType.SemaphoreSubmitInfo,
					Semaphore = frame.ImageAvailable,

					//Wait only where the image is actually written. Everything before that - vertex work, uploads - may run while the image is still on the screen.
					StageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
				};

				signal[0] = new SemaphoreSubmitInfo
				{
					SType = StructureType.SemaphoreSubmitInfo,
					Semaphore = Current.RenderFinished[Current.ImageIndex],
					StageMask = PipelineStageFlags2.AllCommandsBit,
				};

				//The generated frame's image is written by a copy at the end
				wait[1] = new SemaphoreSubmitInfo
				{
					SType = StructureType.SemaphoreSubmitInfo,
					Semaphore = frame.GeneratedAvailable,
					StageMask = PipelineStageFlags2.AllTransferBit,
				};

				signal[1] = new SemaphoreSubmitInfo
				{
					SType = StructureType.SemaphoreSubmitInfo,
					Semaphore = generated ? Current.RenderFinished[generatedImage] : default,
					StageMask = PipelineStageFlags2.AllCommandsBit,
				};

				var commands = new CommandBufferSubmitInfo
				{
					SType = StructureType.CommandBufferSubmitInfo,
					CommandBuffer = frame.Commands,
				};

				var submit = new SubmitInfo2
				{
					SType = StructureType.SubmitInfo2,
					WaitSemaphoreInfoCount = generated ? 2u : 1u,
					PWaitSemaphoreInfos = wait,
					CommandBufferInfoCount = 1,
					PCommandBufferInfos = &commands,
					SignalSemaphoreInfoCount = generated ? 2u : 1u,
					PSignalSemaphoreInfos = signal,
				};

				//Every picture uploaded while this frame was being recorded goes to the card first, in one submission.
				//The queue is one, so copies queued here have run by the time the draws below sample what they filled.
				VkTextures.FlushTextureUploads();

				var submitted = VkDevice.dialect.Submit( VkDevice.graphicsQueue, &submit, frame.Finished );

				if ( submitted != Result.Success )
				{
					VkDevice.MarkLostIf( submitted );
					VkDevice.SetText( submitted == Result.ErrorDeviceLost
						? "the device was lost submitting a frame"
						: "the frame would not submit" );

					VkDevice.RecordingAbandoned();
					return 0;
				}

				CoreLatency.Submitted();

				//The graveyard learns which ticket this recording went to the queue as - everything retired while it was open waits for exactly this fence.
				frame.Ticket = VkDevice.RecordingSubmitted();
				frame.Submitted = true;
				frame.ProfilePending = true;

				var finished = Current.RenderFinished[Current.ImageIndex];
				var swapchain = Current.Handle;
				var index = Current.ImageIndex;

				//The generated frame goes without an id: ids are the loop's frames, which both latency drivers tie its stages to
				var spacing = generated ? GeneratedSpacing() : 0L;
				var id = CorePresent.Next( Current );

				//Handed over: the present goes on the submit thread while this one gets on with the next frame - see VkSubmitter. The submission
				//above stays here, so the card starts on the frame the moment it is closed; what is handed over is the display's half.
				if ( VkSubmitter.Enabled )
				{
					VkSubmitter.Hand( new VkSubmitter.Job
					{
						Signal = finished,
						Handle = swapchain,
						Image = index,
						Owner = Current,
						Id = id,
						Generated = generated,
						GeneratedSignal = signal[1].Semaphore,
						GeneratedImage = generatedImage,
						Spacing = spacing,
					} );

					Current.Frame = ( Current.Frame + 1 ) % VkSwapchain.FramesInFlight;
					return 1;
				}

				if ( generated )
				{
					var began = System.Diagnostics.Stopwatch.GetTimestamp();

					//What it answers is what the frame's own present answers a moment later
					VkSubmitter.Present( signal[1].Semaphore, swapchain, generatedImage, 0 );
					PreciseSleep.Until( began + spacing );
				}

				CoreLatency.Presenting( id );
				var presented = VkSubmitter.Present( finished, swapchain, index, id );
				CoreLatency.Presented( id );

				Current.Frame = ( Current.Frame + 1 ) % VkSwapchain.FramesInFlight;

				if ( presented == Result.ErrorOutOfDateKhr )
				{
					//The frame was drawn and submitted; it is only the screen that has moved on. The next begin rebuilds, by which time this frame's work is done.
					Current.OutOfDate = true;
					return 0;
				}

				if ( presented == Result.SuboptimalKhr )
				{
					//Still presenting, only at the wrong size - waited out rather than acted on. See MarkSuboptimal.
					VkSwapchain.MarkSuboptimal();
					return 0;
				}

				if ( presented != Result.Success )
				{
					if ( presented == Result.ErrorSurfaceLostKhr )
					{
						//The work is done and the image submitted; only the showing of it failed. The next begin finds a swapchain built around a new surface.
						if ( !VkSwapchain.RebuildTheSurface() ) Current.OutOfDate = true;
						return 0;
					}

					VkDevice.SetText( "the frame would not present" );
					VkDevice.MarkLostIf( presented );
					return 0;
				}

				Current.Presented++;
				return 1;
			}
			finally
			{
				//Whatever happened above, the frame is over and the swapchain goes back to nobody's - so the next one can begin, on this or on any other.
				if ( ReferenceEquals( generating, Current ) ) generating = null;

				Current.Recording = false;
				VkSwapchain.active = VkSwapchain.placeholder;
			}
		}
	}

	//-- the targets --------------------------------------------------------

	//A shadow map made readable whole the first time anything touches it, so that every layer rests
	//readable from then on - see GpuShadowMap.Layouts. Nothing is kept: the map has never held anything.
	private static void ReadyWhole( Frame frame, VkTextures.GpuShadowMap map )
	{
		if ( map.Ready ) return;

		Transition( frame.Commands, map.Image, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal,
			PipelineStageFlags2.TopOfPipeBit, 0,
			PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.CopyBit,
			AccessFlags2.ShaderSampledReadBit,
			ImageAspectFlags.DepthBit, 0, 1, (uint)map.Layers.Length );

		Array.Fill( map.Layouts, ImageLayout.ShaderReadOnlyOptimal );
		map.Ready = true;
	}

	//Begins drawing one shadow map layer; never cleared whole, the bake clears only its rectangle
	internal static int ShadowBegin( ulong handle, int layer )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || Current.TargetPass || Current.MainPass ) return 0;

			var map = VkTextures.ShadowMapOf( handle );

			if ( map is null || map.Image.Handle == 0 || layer < 0 || layer >= map.Layers.Length ) return 0;

			var frame = CurrentFrame;

			ReadyWhole( frame, map );

			var tracked = map.Layouts[layer];

			WhatUses( tracked, out var fromStage, out var fromAccess );

			//Only this layer: the rest stay readable as they are.
			Transition( frame.Commands, map.Image, tracked, ImageLayout.DepthAttachmentOptimal,
				fromStage, fromAccess,
				PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
				AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentReadBit,
				ImageAspectFlags.DepthBit, 0, 1, 1, (uint)layer );

			map.Layouts[layer] = ImageLayout.DepthAttachmentOptimal;

			var depth = new RenderingAttachmentInfo
			{
				SType = StructureType.RenderingAttachmentInfo,
				ImageView = map.Layers[layer],
				ImageLayout = ImageLayout.DepthAttachmentOptimal,

				//Kept: a bake clears only its own rectangle - see CoreSunPages.
				LoadOp = AttachmentLoadOp.Load,
				StoreOp = AttachmentStoreOp.Store,
			};

			var whole = new Rect2D( default, new Extent2D( map.Side, map.Side ) );

			var rendering = new RenderingInfo
			{
				SType = StructureType.RenderingInfo,
				RenderArea = whole,
				LayerCount = 1,
				PDepthAttachment = &depth,
			};

			VkDevice.dialect.BeginRendering( frame.Commands, &rendering, VkPassFormats.DepthOnly( VkDevice.DepthFormat ) );

			Current.TargetPass = true;
			Current.Target = VkDevice.TargetShadow;
			Current.ShadowMap = handle;
			Current.ShadowLayer = layer;
			Current.ShadowArea = whole;
			Current.ShadowScissor = whole;
			Current.TargetExtent = whole.Extent;
			frame.ViewportSet = false;

			return 1;
		}
	}

	//The draws that follow land only in this part of the open shadow area - which still says where the whole view is: a
	//face whose projection covers all of its square, drawn over only where something moves. Cleared first where asked.
	internal static void ShadowScissor( int x, int y, int width, int height, int clear )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || !Current.TargetPass || Current.Target != VkDevice.TargetShadow ) return;

			var area = Current.ShadowArea;
			var left = System.Math.Clamp( x, area.Offset.X, area.Offset.X + (int)area.Extent.Width );
			var top = System.Math.Clamp( y, area.Offset.Y, area.Offset.Y + (int)area.Extent.Height );
			var right = System.Math.Clamp( x + width, left, area.Offset.X + (int)area.Extent.Width );
			var bottom = System.Math.Clamp( y + height, top, area.Offset.Y + (int)area.Extent.Height );

			Current.ShadowScissor = new Rect2D( new Offset2D( left, top ), new Extent2D( (uint)( right - left ), (uint)( bottom - top ) ) );
			CurrentFrame.ViewportSet = false;

			if ( clear == 0 || right <= left || bottom <= top ) return;

			//Cleared to nothing in the way first, this part alone.
			var attachment = new ClearAttachment
			{
				AspectMask = ImageAspectFlags.DepthBit,
				ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue( 1.0f, 0 ) },
			};

			var rect = new ClearRect { Rect = Current.ShadowScissor, BaseArrayLayer = 0, LayerCount = 1 };

			VkDevice.vk.CmdClearAttachments( CurrentFrame.Commands, 1, &attachment, 1, &rect );
		}
	}

	//Where in the open shadow layer the draws that follow land - one bake's rectangle - cleared to nothing in the
	//way first where asked. The viewport and the scissor are both this rectangle, so a bake cannot write outside it.
	internal static void ShadowArea( int x, int y, int width, int height, int clear )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || !Current.TargetPass || Current.Target != VkDevice.TargetShadow ) return;

			var map = VkTextures.ShadowMapOf( Current.ShadowMap );
			if ( map is null ) return;

			var side = (int)map.Side;

			var left = System.Math.Clamp( x, 0, side );
			var top = System.Math.Clamp( y, 0, side );
			var right = System.Math.Clamp( x + width, left, side );
			var bottom = System.Math.Clamp( y + height, top, side );

			var area = new Rect2D( new Offset2D( left, top ), new Extent2D( (uint)( right - left ), (uint)( bottom - top ) ) );

			var frame = CurrentFrame;
			Current.ShadowArea = area;
			Current.ShadowScissor = area;
			frame.ViewportSet = false;

			if ( clear == 0 || area.Extent.Width == 0 || area.Extent.Height == 0 ) return;

			var attachment = new ClearAttachment
			{
				AspectMask = ImageAspectFlags.DepthBit,
				ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue( 1.0f, 0 ) },
			};

			var rect = new ClearRect { Rect = area, BaseArrayLayer = 0, LayerCount = 1 };

			VkDevice.vk.CmdClearAttachments( frame.Commands, 1, &attachment, 1, &rect );
		}
	}

	//Clears the sky table once after device creation, so reads before the sky pass see zero alpha
	private static void ClearSkyTableOnce( CommandBuffer commands )
	{
		if ( VkDevice.skyTableImage.Handle == 0 || VkDevice.skyTableLayout != ImageLayout.Undefined ) return;

		Transition( commands, VkDevice.skyTableImage,
			ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
			PipelineStageFlags2.TopOfPipeBit, 0,
			PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit );

		var colour = new ClearColorValue( 0.0f, 0.0f, 0.0f, 0.0f );
		var range = new ImageSubresourceRange( ImageAspectFlags.ColorBit, 0, 1, 0, 1 );

		VkDevice.vk.CmdClearColorImage( commands, VkDevice.skyTableImage,
			ImageLayout.TransferDstOptimal, &colour, 1, &range );

		Transition( commands, VkDevice.skyTableImage,
			ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
			PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit,
			PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit );

		VkDevice.skyTableLayout = ImageLayout.ShaderReadOnlyOptimal;
	}

	//The sky's table as a target - see VkDevice.TargetSkyTable. Every texel is written by the one triangle drawn over it, so nothing is loaded.
	private static int SkyTableBegin()
	{
		var frame = CurrentFrame;

		if ( VkDevice.skyTableImage.Handle == 0 ) return 0;

		WhatUses( VkDevice.skyTableLayout, out var fromStage, out var fromAccess );

		Transition( frame.Commands, VkDevice.skyTableImage, VkDevice.skyTableLayout, ImageLayout.ColorAttachmentOptimal,
			fromStage, fromAccess,
			PipelineStageFlags2.ColorAttachmentOutputBit,
			AccessFlags2.ColorAttachmentWriteBit );

		VkDevice.skyTableLayout = ImageLayout.ColorAttachmentOptimal;

		var colour = new RenderingAttachmentInfo
		{
			SType = StructureType.RenderingAttachmentInfo,
			ImageView = VkDevice.skyTableView,
			ImageLayout = ImageLayout.ColorAttachmentOptimal,
			LoadOp = AttachmentLoadOp.DontCare,
			StoreOp = AttachmentStoreOp.Store,
		};

		var rendering = new RenderingInfo
		{
			SType = StructureType.RenderingInfo,
			RenderArea = new Rect2D( default, new Extent2D( VkDevice.SkyTableSide, VkDevice.SkyTableSide ) ),
			LayerCount = 1,
			ColorAttachmentCount = 1,
			PColorAttachments = &colour,
		};

		VkDevice.dialect.BeginRendering( frame.Commands, &rendering, VkPassFormats.Of( VkDevice.SceneFormat ) );

		Current.TargetPass = true;
		Current.Target = VkDevice.TargetSkyTable;
		Current.TargetExtent = new Extent2D( VkDevice.SkyTableSide, VkDevice.SkyTableSide );

		return 1;
	}

	internal static int TargetBegin( int target, float r, float g, float b, float a, int clear )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || Current.TargetPass ) return 0;

			if ( target == VkDevice.TargetSkyTable )
				return Current.MainPass ? 0 : SkyTableBegin();

			//The screen is not one of these: it is what the frame ends on, and it opens itself at the first draw that has nowhere else to go.
			if ( target != VkDevice.TargetScene && target != VkDevice.TargetDepth &&
				target != VkDevice.TargetVelocity &&
				target != VkDevice.TargetPost && target != VkDevice.TargetSceneColour &&
				target != VkDevice.TargetFinal && target != VkDevice.TargetFinalSecond &&
				target != VkDevice.TargetDisplay && target != VkDevice.TargetDisplaySecond )
			{
				return 0;
			}

			if ( Current.MainPass )
			{
				VkDevice.SetText( "the passes before the screen have to come before it" );
				return 0;
			}

			var frame = CurrentFrame;
			var p = Pics;

			var colour = stackalloc RenderingAttachmentInfo[2];
			var colourCount = 1u;

			RenderingAttachmentInfo depth = default;
			var hasDepth = false;

			var extent = p.Extent;

			//Where the pass lands. Only the depth may be drawn somewhere other than the top-left of its own picture - see Swapchain.DepthOnScreen.
			var depthArea = new Rect2D( default, extent );

			if ( target == VkDevice.TargetDepth )
			{
				//Depth pre-pass, into the screen's depth where requested so nothing needs copying later
				if ( VkSwapchain.DepthGoesToScreen )
				{
					MoveTo( frame.Commands, p.ScreenDepthImage, ref p.ScreenDepthLayout,
						ImageLayout.DepthAttachmentOptimal, ImageAspectFlags.DepthBit );

					depth.ImageView = p.ScreenDepthView;

					//Render area covers the whole attachment, keeping fast clear and compression; scissor still clips
					depthArea = new Rect2D( default, p.ScreenExtent );
				}
				else
				{
					MoveTo( frame.Commands, p.DepthImage, ref p.DepthLayout,
						ImageLayout.DepthAttachmentOptimal, ImageAspectFlags.DepthBit );

					depth.ImageView = p.DepthView;
				}

				depth.SType = StructureType.RenderingAttachmentInfo;
				depth.ImageLayout = ImageLayout.DepthAttachmentOptimal;
				depth.LoadOp = clear != 0 ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load;
				depth.StoreOp = AttachmentStoreOp.Store;
				depth.ClearValue.DepthStencil = new ClearDepthStencilValue( 1.0f, 0 );

				hasDepth = true;
				colourCount = 0;
			}
			else if ( target == VkDevice.TargetScene )
			{
				if ( p.SceneImage.Handle == 0 || p.NormalImage.Handle == 0 ) return 0;

				MoveTo( frame.Commands, p.SceneImage, ref p.SceneLayout,
					ImageLayout.ColorAttachmentOptimal, ImageAspectFlags.ColorBit );

				MoveTo( frame.Commands, p.NormalImage, ref p.NormalLayout, ImageLayout.ColorAttachmentOptimal );

				MoveTo( frame.Commands, p.DepthImage, ref p.DepthLayout,
					ImageLayout.DepthAttachmentOptimal, ImageAspectFlags.DepthBit );

				colour[0].ImageView = p.SceneTop;
				colour[1].ImageView = p.NormalView;
				colourCount = 2;

				depth.SType = StructureType.RenderingAttachmentInfo;
				depth.ImageView = p.DepthView;
				depth.ImageLayout = ImageLayout.DepthAttachmentOptimal;

				//Keep depth if a pre-pass filled it or the pass is reopening mid-world; colour still clears
				depth.LoadOp = p.DepthFilled || p.SceneDrawn ? AttachmentLoadOp.Load : AttachmentLoadOp.Clear;
				depth.StoreOp = AttachmentStoreOp.Store;
				depth.ClearValue.DepthStencil = new ClearDepthStencilValue( 1.0f, 0 );

				hasDepth = true;
				p.SceneDrawn = true;
			}
			else if ( target == VkDevice.TargetSceneColour )
			{
				//The world's own picture, its colour alone. Everything a full-screen pass needs and nothing it would leave undefined.
				if ( p.SceneImage.Handle == 0 ) return 0;

				MoveTo( frame.Commands, p.SceneImage, ref p.SceneLayout,
					ImageLayout.ColorAttachmentOptimal, ImageAspectFlags.ColorBit );

				colour[0].ImageView = p.SceneTop;
				colourCount = 1;

				p.SceneDrawn = true;
			}
			else if ( target == VkDevice.TargetPost )
			{
				//Made here rather than with the rest of them: a project with no post-process of its own
				//never opens this, and never pays for it. One allocation, the first time an effect draws.
				if ( p.PostImage.Handle == 0 )
				{
					//With a mip chain, so bloom can read blurred copies of what it kept
					var levels = VkSwapchain.LevelsForBloom( p.Extent );

					if ( !VkSwapchain.MakePicture( VkDevice.WorldFormat, p.Extent, levels,
						ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit |
						ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
						out var postImage, out var postMemory, out var postView ) )
					{
						p.PostImage = postImage;
						p.PostMemory = postMemory;
						p.PostView = postView;

						VkDevice.SetText( "the other picture would not be made" );
						return 0;
					}

					p.PostImage = postImage;
					p.PostMemory = postMemory;
					p.PostView = postView;
					p.PostLevels = levels;
					p.PostMipsReady = false;
					p.PostHandle = VkTextures.Adopt( postView, VkDevice.clampSampler );
					p.PostLayout = ImageLayout.Undefined;

					//The attachment names one level of its own: a pass writes the top and the view above is the whole chain.
					var topInfo = new ImageViewCreateInfo
					{
						SType = StructureType.ImageViewCreateInfo,
						Image = postImage,
						ViewType = ImageViewType.Type2D,
						Format = VkDevice.WorldFormat,
						SubresourceRange = new ImageSubresourceRange( ImageAspectFlags.ColorBit, 0, 1, 0, 1 ),
					};

					ImageView postTop;

					if ( VkDevice.vk.CreateImageView( VkDevice.device, &topInfo, null, &postTop ) != Result.Success )
					{
						VkDevice.SetText( "the other picture's top level would not be made" );
						return 0;
					}

					p.PostTop = postTop;

					VkDevice.NameHandle( ObjectType.Image, postImage.Handle, "the other picture" );
				}

				MoveTo( frame.Commands, p.PostImage, ref p.PostLayout, ImageLayout.ColorAttachmentOptimal );

				colour[0].ImageView = p.PostTop;
				colourCount = 1;

				//No depth. What draws here covers the screen and is already sorted by the order the effects were put in.
			}
			else if ( target == VkDevice.TargetDisplay || target == VkDevice.TargetDisplaySecond )
			{
				//The world's picture at the window's size - see TargetDisplay. Made here: only
				//a frame a temporal upscaler carries up before its effects ever opens one.
				var which = target == VkDevice.TargetDisplay ? 0 : 1;

				if ( !MakeDisplay( p ) ) return 0;

				MoveTo( frame.Commands, p.DisplayImage[which], ref p.DisplayLayout[which],
					ImageLayout.ColorAttachmentOptimal );

				colour[0].ImageView = p.DisplayTop[which];
				colourCount = 1;
				extent = p.ScreenExtent;

				//No depth: what draws here covers the screen.
			}
			else if ( target == VkDevice.TargetFinal || target == VkDevice.TargetFinalSecond )
			{
				//The finished picture, at the window's size. Made here rather than with the rest of them:
				//a project with nothing hung after the compose never opens one and never pays for it.
				var which = target == VkDevice.TargetFinal ? 0 : 1;

				if ( !MakeFinal( p, which ) ) return 0;

				MoveTo( frame.Commands, p.FinalImage[which], ref p.FinalLayout[which],
					ImageLayout.ColorAttachmentOptimal );

				colour[0].ImageView = p.FinalView[which];
				colourCount = 1;

				//The window's size, not the world's: what is in here has been carried up already.
				extent = p.ScreenExtent;

				//No depth. What draws here covers the screen.
			}
			else if ( target == VkDevice.TargetVelocity )
			{
				if ( !MakeVelocity( p ) ) return 0;

				MoveTo( frame.Commands, p.VelocityImage, ref p.VelocityLayout, ImageLayout.ColorAttachmentOptimal );

				//Drawn into, so nothing has to clear it before it is read.
				p.VelocityReady = true;

				colour[0].ImageView = p.VelocityView;
				colourCount = 1;

				//The depth the world was drawn with, read and not written: only the nearest thing to the eye should say how fast it is going, and a
				//thing behind a wall should say nothing at all. It has to be there for that test to happen, and the pipeline is what turns writing off.
				if ( p.DepthImage.Handle != 0 && p.DepthFilled )
				{
					MoveTo( frame.Commands, p.DepthImage, ref p.DepthLayout,
						ImageLayout.DepthAttachmentOptimal, ImageAspectFlags.DepthBit );

					depth.SType = StructureType.RenderingAttachmentInfo;
					depth.ImageView = p.DepthView;
					depth.ImageLayout = ImageLayout.DepthAttachmentOptimal;
					depth.LoadOp = AttachmentLoadOp.Load;
					depth.StoreOp = AttachmentStoreOp.Store;

					hasDepth = true;
				}
			}

			for ( var i = 0u; i < colourCount; ++i )
			{
				colour[i].SType = StructureType.RenderingAttachmentInfo;
				colour[i].ImageLayout = ImageLayout.ColorAttachmentOptimal;

				//Two is neither: the pass is about to write every pixel and what was there is not worth reading in - which on a tiler is the whole of the
				//load, and on a desktop is the decompress. A full-screen pass that replaces the picture asks for this; one that blends into it cannot.
				colour[i].LoadOp = clear == 2
					? AttachmentLoadOp.DontCare
					: clear != 0 ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load;
				colour[i].StoreOp = AttachmentStoreOp.Store;
				colour[i].ClearValue.Color = new ClearColorValue( r, g, b, a );
			}

			var rendering = new RenderingInfo
			{
				SType = StructureType.RenderingInfo,
				RenderArea = target == VkDevice.TargetDepth ? depthArea : new Rect2D( default, extent ),
				LayerCount = 1,
				ColorAttachmentCount = colourCount,
				PColorAttachments = colour,
				PDepthAttachment = hasDepth ? &depth : null,
			};

			//The same formats a pipeline for this target was made with - one answer for both, see VkPipelines.FormatsOf -
			//less whatever this pass leaves out: the depth pre-pass has no colour, and not every target is given the depth.
			VkPipelines.FormatsOf( target, 0, VkSwapchain.ScreenFormat(), out var formats );

			formats.Colours = colourCount;
			if ( !hasDepth ) formats.Depth = Format.Undefined;

			//An eye's world shaded finest where it looks - the pre-pass and the world's own pass with the
			//one map, so a depth-equal test between them meets depths rasterised the same way. See VkFovea.
			RenderingFragmentDensityMapAttachmentInfoEXT foveaDensity;
			RenderingFragmentShadingRateAttachmentInfoKHR foveaRate;

			if ( target == VkDevice.TargetDepth || target == VkDevice.TargetScene )
				VkFovea.Attach( Fovea( frame, p, bothEyes: false ), &rendering, &foveaDensity, &foveaRate );

			VkDevice.dialect.BeginRendering( frame.Commands, &rendering, formats );

			Current.TargetPass = true;
			Current.Target = target;
			Current.TargetExtent = extent;

			return 1;
		}
	}

	internal static void TargetEnd()
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.TargetPass ) return;

			var frame = CurrentFrame;
			var p = Pics;

			VkDevice.dialect.EndRendering( frame.Commands );
			Current.TargetPass = false;

			//What was written becomes readable, because the whole point of drawing into a picture rather than the screen is that something reads it next.
			if ( Current.Target == VkDevice.TargetShadow )
			{
				//The layer drawn readable again, like the rest of the map.
				if ( VkTextures.ShadowMapOf( Current.ShadowMap ) is { } map &&
					Current.ShadowLayer >= 0 && Current.ShadowLayer < map.Layouts.Length )
				{
					Transition( frame.Commands, map.Image,
						ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
						PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
						AccessFlags2.DepthStencilAttachmentWriteBit,
						PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
						ImageAspectFlags.DepthBit, 0, 1, 1, (uint)Current.ShadowLayer );

					map.Layouts[Current.ShadowLayer] = ImageLayout.ShaderReadOnlyOptimal;
				}

				Current.ShadowMap = 0;
			}
			else if ( Current.Target == VkDevice.TargetSkyTable )
			{
				Transition( frame.Commands, VkDevice.skyTableImage,
					ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
					PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
					PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit );

				VkDevice.skyTableLayout = ImageLayout.ShaderReadOnlyOptimal;
			}
			else if ( Current.Target == VkDevice.TargetDepth )
			{
				p.DepthFilled = true;

				if ( VkSwapchain.DepthGoesToScreen )
				{
					p.ScreenDepthFilled = true;

					MoveTo( frame.Commands, p.ScreenDepthImage, ref p.ScreenDepthLayout,
						ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.DepthBit );
				}
				else
				{
					MoveTo( frame.Commands, p.DepthImage, ref p.DepthLayout,
						ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.DepthBit );
				}
			}
			else if ( Current.Target == VkDevice.TargetScene )
			{
				MoveTo( frame.Commands, p.SceneImage, ref p.SceneLayout, ImageLayout.ShaderReadOnlyOptimal );
				MoveTo( frame.Commands, p.NormalImage, ref p.NormalLayout, ImageLayout.ShaderReadOnlyOptimal );

				//And its levels under the top out of UNDEFINED, once: what reads the world - the compose - reads it through the view over the
				//whole chain, whether or not anything filled the chain this frame, and the validation layer checks every level that view names.
				ReadyMips( frame, p.SceneImage, p.SceneLevels, ref p.SceneMipsReady );

				MoveTo( frame.Commands, p.DepthImage, ref p.DepthLayout,
					ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.DepthBit );
			}
			else if ( Current.Target == VkDevice.TargetSceneColour )
			{
				//The world's own picture again, so it ends where the world's pass would have left it: readable. Everything after a post-process chain - the reflections,
				//the compose - reads it, and a pass that wrote the colour and left it attached is an error reported against whichever of them read it next.
				MoveTo( frame.Commands, p.SceneImage, ref p.SceneLayout, ImageLayout.ShaderReadOnlyOptimal );
				ReadyMips( frame, p.SceneImage, p.SceneLevels, ref p.SceneMipsReady );
			}
			else if ( Current.Target == VkDevice.TargetPost )
			{
				MoveTo( frame.Commands, p.PostImage, ref p.PostLayout, ImageLayout.ShaderReadOnlyOptimal );

				//And the levels under the top out of UNDEFINED, once, as the world's picture's are: a pass reads it through
				//the view over the whole chain, and a level nothing filled is still a level that has to be readable.
				ReadyMips( frame, p.PostImage, p.PostLevels, ref p.PostMipsReady );
			}
			else if ( Current.Target == VkDevice.TargetDisplay || Current.Target == VkDevice.TargetDisplaySecond )
			{
				var which = Current.Target == VkDevice.TargetDisplay ? 0 : 1;

				MoveTo( frame.Commands, p.DisplayImage[which], ref p.DisplayLayout[which], ImageLayout.ShaderReadOnlyOptimal );
				ReadyMips( frame, p.DisplayImage[which], p.DisplayLevels, ref p.DisplayMipsReady[which] );
			}
			else if ( Current.Target == VkDevice.TargetFinal || Current.Target == VkDevice.TargetFinalSecond )
			{
				var which = Current.Target == VkDevice.TargetFinal ? 0 : 1;

				MoveTo( frame.Commands, p.FinalImage[which], ref p.FinalLayout[which],
					ImageLayout.ShaderReadOnlyOptimal );
			}
			else if ( Current.Target == VkDevice.TargetPicture )
			{
				PictureEnd( frame );
			}
			else if ( Current.Target == VkDevice.TargetVelocity )
			{
				MoveTo( frame.Commands, p.VelocityImage, ref p.VelocityLayout, ImageLayout.ShaderReadOnlyOptimal );

				//Return the borrowed depth to readable, since later passes sample it
				MoveTo( frame.Commands, p.DepthImage, ref p.DepthLayout,
					ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.DepthBit );
			}

			Current.Target = VkDevice.TargetScreen;
		}
	}

	//Makes a window-sized post-tonemap picture (10-bit if supported); remade on window resize
	private static bool MakeDisplay( VkSwapchain.Pictures p )
	{
		if ( p.ScreenExtent.Width == 0 || p.ScreenExtent.Height == 0 ) return false;

		//Made and resized as a pair, at the window's size.
		if ( p.DisplayExtent.Width != p.ScreenExtent.Width || p.DisplayExtent.Height != p.ScreenExtent.Height )
		{
			VkSwapchain.DropDisplay( p, deferred: true );
			p.DisplayExtent = p.ScreenExtent;
		}

		if ( p.DisplayImage[0].Handle != 0 ) return true;

		//With a chain of halvings each, like the world's two: a bloom keeps what is over its threshold into one and reads blurred copies of it.
		var levels = VkSwapchain.LevelsForBloom( p.ScreenExtent );

		for ( var which = 0; which < 2; ++which )
		{
			if ( !VkSwapchain.MakePicture( VkDevice.WorldFormat, p.ScreenExtent, levels,
				ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit |
				ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
				out var image, out var memory, out var view ) )
			{
				VkSwapchain.DropImage( image, memory, view, deferred: false );
				VkSwapchain.DropDisplay( p, deferred: true );
				VkDevice.SetText( "a window-size picture of the world would not be made" );
				return false;
			}

			var topInfo = new ImageViewCreateInfo
			{
				SType = StructureType.ImageViewCreateInfo,
				Image = image,
				ViewType = ImageViewType.Type2D,
				Format = VkDevice.WorldFormat,
				SubresourceRange = new ImageSubresourceRange( ImageAspectFlags.ColorBit, 0, 1, 0, 1 ),
			};

			ImageView top;

			if ( VkDevice.vk.CreateImageView( VkDevice.device, &topInfo, null, &top ) != Result.Success )
			{
				VkSwapchain.DropImage( image, memory, view, deferred: false );
				VkSwapchain.DropDisplay( p, deferred: true );
				VkDevice.SetText( "a window-size picture's top level would not be made" );
				return false;
			}

			p.DisplayImage[which] = image;
			p.DisplayMemory[which] = memory;
			p.DisplayView[which] = view;
			p.DisplayTop[which] = top;
			p.DisplayLayout[which] = ImageLayout.Undefined;
			p.DisplayMipsReady[which] = false;
			p.DisplayHandle[which] = VkTextures.Adopt( view, VkDevice.clampSampler );

			VkDevice.NameHandle( ObjectType.Image, image.Handle,
				which == 0 ? "the world at the window's size" : "the world at the window's size, the other one" );
		}

		p.DisplayLevels = levels;
		return true;
	}

	private static bool MakeFinal( VkSwapchain.Pictures p, int which )
	{
		if ( p.ScreenExtent.Width == 0 || p.ScreenExtent.Height == 0 ) return false;

		//A window that changed size takes both of them with it, whichever was asked for.
		if ( p.FinalExtent.Width != p.ScreenExtent.Width || p.FinalExtent.Height != p.ScreenExtent.Height )
		{
			for ( var i = 0; i < 2; ++i )
			{
				if ( p.FinalHandle[i] != 0 ) VkSwapchain.DropHandle( p.FinalHandle[i], deferred: true );
				if ( p.FinalImage[i].Handle != 0 )
					VkSwapchain.DropImage( p.FinalImage[i], p.FinalMemory[i], p.FinalView[i], deferred: true );

				p.FinalHandle[i] = 0;
				p.FinalImage[i] = default;
				p.FinalMemory[i] = default;
				p.FinalView[i] = default;
				p.FinalLayout[i] = ImageLayout.Undefined;
			}

			p.FinalExtent = p.ScreenExtent;
		}

		if ( p.FinalImage[which].Handle != 0 ) return true;

		const ImageUsageFlags usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit;

		//In the one format its pipelines are made for - see VkPipelines, which names every picture's own. It used to try the world's half floats
		//when this would not be made, which no pipeline drawing into it expected; and this format is one every device draws into and blends.
		if ( !VkSwapchain.MakePicture( VkDevice.FinalFormat, p.ScreenExtent, 1, usage,
			out var image, out var memory, out var view ) )
		{
			//Half-made, and the pieces have to go or they leak.
			VkSwapchain.DropImage( image, memory, view, deferred: false );
			VkDevice.SetText( "a screen-size picture would not be made" );
			return false;
		}

		p.FinalImage[which] = image;
		p.FinalMemory[which] = memory;
		p.FinalView[which] = view;
		p.FinalLayout[which] = ImageLayout.Undefined;
		p.FinalHandle[which] = VkTextures.Adopt( view, VkDevice.clampSampler );

		VkDevice.NameHandle( ObjectType.Image, image.Handle,
			which == 0 ? "the finished picture" : "the finished picture, the other one" );

		return true;
	}

	//Makes the velocity picture lazily on first draw; missing reads as zero motion
	private static bool MakeVelocity( VkSwapchain.Pictures p )
	{
		if ( p.VelocityImage.Handle != 0 ) return true;
		if ( p.Extent.Width == 0 || p.Extent.Height == 0 ) return false;

		if ( !VkSwapchain.MakePicture( VkDevice.VelocityFormat, p.Extent, 1,
			ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit |
			ImageUsageFlags.TransferDstBit,
			out var image, out var memory, out var view ) )
		{
			VkSwapchain.DropImage( image, memory, view, deferred: false );
			VkDevice.SetText( "the velocity picture would not be made" );

			return false;
		}

		p.VelocityImage = image;
		p.VelocityMemory = memory;
		p.VelocityView = view;
		p.VelocityLayout = ImageLayout.Undefined;
		p.VelocityHandle = VkTextures.Adopt( view, VkDevice.pointSampler );

		VkDevice.NameHandle( ObjectType.Image, image.Handle, "world velocities" );

		return true;
	}

	//One of the frame's pictures into the layout its descriptor promises, for a dispatch that reads it by index - see
	//ReadyPictureForRead, which a draw's own material gets for nothing and a picture named only by its place in the table does not.
	internal static int ReadyToRead( ulong texture )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || texture == 0 ) return 0;

			var frame = CurrentFrame;

			//A layout cannot change while the picture is attached to something.
			EndPictureIfOpen( frame );
			CloseOpenPasses( frame );

			ReadyPictureForRead( frame, texture );

			return 1;
		}
	}

	internal static int DepthReadable()
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || Current.TargetPass ) return 0;
			if ( Current.MainPass || Pics.DepthImage.Handle == 0 ) return 0;

			var frame = CurrentFrame;
			var p = Pics;

			MoveTo( frame.Commands, p.DepthImage, ref p.DepthLayout,
				ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.DepthBit );

			return 1;
		}
	}

	//Generates the world picture's mip chain by blits, outside any pass; only when an effect needs it
	internal static int PictureMips( int target )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording ) return 0;
			if ( Current.MainPass || Current.TargetPass ) return 0;

			var p = Pics;

			if ( p is null ) return 0;

			//Which picture: one of the two full-size ones. The frame's other pictures fill
			//their chains through PictureChain, and the screen cannot be sampled at all.
			var post = target == VkDevice.TargetPost;
			var display = target == VkDevice.TargetDisplay ? 0 : target == VkDevice.TargetDisplaySecond ? 1 : -1;

			if ( !post && display < 0 && target != VkDevice.TargetScene && target != VkDevice.TargetSceneColour ) return 0;

			var image = display >= 0 ? p.DisplayImage[display] : post ? p.PostImage : p.SceneImage;
			var levels = display >= 0 ? p.DisplayLevels : post ? p.PostLevels : p.SceneLevels;

			if ( image.Handle == 0 || levels <= 1 ) return 0;

			var frame = CurrentFrame;

			//The top level is what everything else in the frame reads, so it is the tracked one and it goes back where it was found.
			if ( display >= 0 ) MoveTo( frame.Commands, image, ref p.DisplayLayout[display], ImageLayout.TransferSrcOptimal );
			else if ( post ) MoveTo( frame.Commands, image, ref p.PostLayout, ImageLayout.TransferSrcOptimal );
			else MoveTo( frame.Commands, image, ref p.SceneLayout, ImageLayout.TransferSrcOptimal );

			//The window-size pair is the window's size; the world's two are the world's.
			var width = (int)( display >= 0 ? p.ScreenExtent.Width : p.Extent.Width );
			var height = (int)( display >= 0 ? p.ScreenExtent.Height : p.Extent.Height );

			for ( uint level = 1; level < levels; ++level )
			{
				var toWidth = width > 1 ? width >> 1 : 1;
				var toHeight = height > 1 ? height >> 1 : 1;

				Transition( frame.Commands, image,
					ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
					PipelineStageFlags2.AllCommandsBit, 0,
					PipelineStageFlags2.BlitBit, AccessFlags2.TransferWriteBit,
					ImageAspectFlags.ColorBit, level, 1 );

				var region = new ImageBlit
				{
					SrcSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, level - 1, 0, 1 ),
					DstSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, level, 0, 1 ),
				};

				region.SrcOffsets[1] = new Offset3D( width, height, 1 );
				region.DstOffsets[1] = new Offset3D( toWidth, toHeight, 1 );

				VkDevice.vk.CmdBlitImage( frame.Commands,
					image, ImageLayout.TransferSrcOptimal,
					image, ImageLayout.TransferDstOptimal,
					1, &region, Filter.Linear );

				//This level is the next one's source, so it moves on rather than waiting for the whole chain to be done.
				Transition( frame.Commands, image,
					ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal,
					PipelineStageFlags2.BlitBit, AccessFlags2.TransferWriteBit,
					PipelineStageFlags2.BlitBit, AccessFlags2.TransferReadBit,
					ImageAspectFlags.ColorBit, level, 1 );

				width = toWidth;
				height = toHeight;
			}

			Transition( frame.Commands, image,
				ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal,
				PipelineStageFlags2.BlitBit, AccessFlags2.TransferReadBit,
				PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
				ImageAspectFlags.ColorBit, 1, levels - 1 );

			if ( display >= 0 )
			{
				MoveTo( frame.Commands, image, ref p.DisplayLayout[display], ImageLayout.ShaderReadOnlyOptimal );
				p.DisplayMipsReady[display] = true;
			}
			else if ( post )
			{
				MoveTo( frame.Commands, image, ref p.PostLayout, ImageLayout.ShaderReadOnlyOptimal );
				p.PostMipsReady = true;
			}
			else
			{
				MoveTo( frame.Commands, image, ref p.SceneLayout, ImageLayout.ShaderReadOnlyOptimal );
				p.SceneMipsReady = true;
			}

			return 1;
		}
	}

	//-- the backdrop -------------------------------------------------------

	//Ends the screen pass so the world's passes can run; the next draw (the compose) reopens it
	internal static int ScreenInterrupt()
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording ) return 0;

			//Until ScreenResume, the screen's draws are the world's - see WorldOnScreen.
			Current.WorldOnScreen = true;

			if ( Current.TargetPass ) return 0;

			if ( Current.MainPass )
			{
				VkDevice.dialect.EndRendering( CurrentFrame.Commands );
				Current.MainPass = false;
				Current.LiftPending = true;
			}

			return 1;
		}
	}

	//Squeezes following draws' depth into [0, ceiling], or restores the full range with 1
	internal static void SetDepthCeiling( float ceiling )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording ) return;

			Current.DepthCeiling = System.Math.Clamp( ceiling, 0.0001f, 1.0f );
		}
	}

	internal static void WorldShades( bool surfaces )
	{
		lock ( VkDevice.renderLock )
		{
			if ( Current.Recording ) Current.WorldShades = surfaces;
		}
	}

	internal static int ScreenResume()
	{
		lock ( VkDevice.renderLock )
		{
			Current.WorldOnScreen = false;
			return 1;
		}
	}

	internal static int BackdropCapture()
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording ) return 0;

			//The screen of whichever viewport is open: a view's own output and its own backdrop, or
			//the window's image and the window's - see EnsureMainPass, which draws into the same one.
			var view = VkSwapchain.currentView;

			VkImage source, backdrop;
			Extent2D extent;
			uint levels;

			if ( view is not null )
			{
				if ( view.BackdropImage.Handle == 0 || view.BackdropHandle == 0 ) return 0;

				source = view.OutputImage;
				backdrop = view.BackdropImage;
				extent = view.OutputExtent;
				levels = Math.Max( view.BackdropLevels, 1u );
			}
			else
			{
				if ( Current.BackdropImage.Handle == 0 || Current.BackdropHandle == 0 ) return 0;

				source = Current.Images[Current.ImageIndex];
				backdrop = Current.BackdropImage;
				extent = Current.Extent;
				levels = Math.Max( Current.BackdropLevels, 1u );
			}

			var frame = CurrentFrame;

			//Whatever has been drawn so far is what goes behind the panels, so the world's pass has to close before the copy - a copy inside a
			//render pass is not a thing Vulkan allows. EnsureMainPass reopens it loading rather than clearing, so nothing already drawn is lost.
			EnsureMainPass();

			VkDevice.dialect.EndRendering( frame.Commands );
			Current.MainPass = false;

			Transition( frame.Commands, source,
				ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
				PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
				PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit );

			//The old contents are about to be overwritten wholesale, so nothing is worth preserving and the layout it was left in does not matter.
			Transition( frame.Commands, backdrop,
				ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
				PipelineStageFlags2.FragmentShaderBit, 0,
				PipelineStageFlags2.CopyBit | PipelineStageFlags2.BlitBit, AccessFlags2.TransferWriteBit,
				levelCount: levels );

			var region = new ImageCopy
			{
				SrcSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, 0, 0, 1 ),
				DstSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, 0, 0, 1 ),
				Extent = new Extent3D( extent.Width, extent.Height, 1 ),
			};

			VkDevice.vk.CmdCopyImage( frame.Commands,
				source, ImageLayout.TransferSrcOptimal,
				backdrop, ImageLayout.TransferDstOptimal,
				1, &region );

			//And its halvings, each blitted out of the one above: the interface blurs through the level its radius wants - see Backdrop in ui.slang.
			for ( var level = 1u; level < levels; level++ )
			{
				Transition( frame.Commands, backdrop,
					ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal,
					PipelineStageFlags2.CopyBit | PipelineStageFlags2.BlitBit, AccessFlags2.TransferWriteBit,
					PipelineStageFlags2.BlitBit, AccessFlags2.TransferReadBit,
					baseLevel: level - 1 );

				var blit = new ImageBlit
				{
					SrcSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, level - 1, 0, 1 ),
					DstSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, level, 0, 1 ),
				};

				blit.SrcOffsets[1] = new Offset3D( (int)Math.Max( extent.Width >> (int)( level - 1 ), 1u ), (int)Math.Max( extent.Height >> (int)( level - 1 ), 1u ), 1 );
				blit.DstOffsets[1] = new Offset3D( (int)Math.Max( extent.Width >> (int)level, 1u ), (int)Math.Max( extent.Height >> (int)level, 1u ), 1 );

				VkDevice.vk.CmdBlitImage( frame.Commands,
					backdrop, ImageLayout.TransferSrcOptimal,
					backdrop, ImageLayout.TransferDstOptimal,
					1, &blit, Filter.Linear );

				Transition( frame.Commands, backdrop,
					ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal,
					PipelineStageFlags2.BlitBit, AccessFlags2.TransferReadBit,
					PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
					baseLevel: level - 1 );
			}

			//The last level written, now read.
			Transition( frame.Commands, backdrop,
				ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
				PipelineStageFlags2.CopyBit | PipelineStageFlags2.BlitBit, AccessFlags2.TransferWriteBit,
				PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
				baseLevel: levels - 1 );

			Transition( frame.Commands, source,
				ImageLayout.TransferSrcOptimal, ImageLayout.ColorAttachmentOptimal,
				PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit,
				PipelineStageFlags2.ColorAttachmentOutputBit,
				AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit );

			if ( view is not null ) view.BackdropTaken = true;
			else Current.BackdropTaken = true;

			return 1;
		}
	}

	internal static ulong BackdropTexture()
	{
		lock ( VkDevice.renderLock )
		{
			if ( VkSwapchain.currentView is { } view ) return view.BackdropTaken ? view.BackdropHandle : 0;

			return Current.BackdropTaken ? Current.BackdropHandle : 0;
		}
	}

	//-- the lights' buffer and the frame's own writes ----------------------

	internal static int SetLightBuffer( ulong buffer )
	{
		lock ( VkDevice.renderLock )
		{
			//Nought puts it back to the scratch, which is what a frame with no lights of its own binds - see WriteFrameSet.
			VkDevice.lightBuffer = buffer == 0 ? default : VkBuffers.Get( buffer );

			//Every set 1 already written names the old one, so every one of them is marked to be written again before
			//anything records against it - in every viewport, not only whichever is drawing when this is called.
			VkSwapchain.MarkEveryFrameSetStale();

			return 1;
		}
	}

	internal static int FrameWrite( ulong buffer, long offset, byte* data, long size )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || data is null || size <= 0 || offset < 0 ) return 0;

			var into = VkBuffers.Get( buffer );
			if ( into.Handle == 0 ) return 0;
			if ( (ulong)offset + (ulong)size > VkBuffers.BytesOf( buffer ) ) return 0;

			var frame = CurrentFrame;

			//Into the frame's own scratch, and from there a copy the card runs where this was recorded
			//- so the write lands in order with the frame's dispatches rather than ahead of all of them.
			var aligned = ( frame.ScratchUsed + 15 ) & ~15ul;
			var bytes = (ulong)size;

			if ( !EnsureScratch( frame, ( aligned - frame.ScratchUsed ) + bytes ) )
			{
				VkDevice.SetText( "the frame's scratch space would not grow" );
				return 0;
			}

			System.Buffer.MemoryCopy( data, frame.ScratchMapped + aligned, bytes, bytes );
			frame.ScratchUsed = aligned + bytes;

			CloseOpenPasses( frame );

			var copy = new BufferCopy
			{
				SrcOffset = aligned,
				DstOffset = (ulong)offset,
				Size = bytes,
			};

			VkDevice.vk.CmdCopyBuffer( frame.Commands, frame.Scratch, into, 1, &copy );

			//One barrier per write on purpose, unless held; batching them inside a pass was measured slower
			if ( barriersHeld ) copyOwed = true;
			else Barriers( frame, copies: true, dispatches: false );

			return 1;
		}
	}

	//Updates existing materials' params from frame scratch, with one barrier before and after all copies
	internal static void MaterialNumbers( ulong* materials, float* numbers, int count, byte* written )
	{
		new Span<byte>( written, count ).Clear();

		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || count <= 0 ) return;

			var frame = CurrentFrame;
			var size = (ulong)VkDevice.MaterialParamSize;
			var any = false;

			for ( var i = 0; i < count; i++ )
			{
				var into = VkTextures.MaterialParams( materials[i] );
				if ( into.Handle == 0 ) continue;

				var aligned = ( frame.ScratchUsed + 15 ) & ~15ul;

				if ( !EnsureScratch( frame, ( aligned - frame.ScratchUsed ) + size ) )
				{
					VkDevice.SetText( "the frame's scratch space would not grow" );
					break;
				}

				if ( !any )
				{
					CloseOpenPasses( frame );

					//Whatever read the old numbers before this - a frame still on the card - is done before they are written over.
					var before = new MemoryBarrier2
					{
						SType = StructureType.MemoryBarrier2,
						SrcStageMask = PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit |
							PipelineStageFlags2.ComputeShaderBit,
						DstStageMask = PipelineStageFlags2.CopyBit,
					};

					var waitBefore = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &before };
					VkDevice.dialect.Barrier( frame.Commands, &waitBefore );

					any = true;
				}

				System.Buffer.MemoryCopy( numbers + ( (long)i * VkDevice.MaterialParamFloats ), frame.ScratchMapped + aligned, size, size );
				frame.ScratchUsed = aligned + size;

				var copy = new BufferCopy { SrcOffset = aligned, DstOffset = 0, Size = size };
				VkDevice.vk.CmdCopyBuffer( frame.Commands, frame.Scratch, into, 1, &copy );

				written[i] = 1;
			}

			if ( !any ) return;

			//And everything recorded after reads what was written.
			var after = new MemoryBarrier2
			{
				SType = StructureType.MemoryBarrier2,
				SrcStageMask = PipelineStageFlags2.CopyBit,
				SrcAccessMask = AccessFlags2.TransferWriteBit,
				DstStageMask = PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit |
					PipelineStageFlags2.ComputeShaderBit,
				DstAccessMask = AccessFlags2.UniformReadBit,
			};

			var waitAfter = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &after };
			VkDevice.dialect.Barrier( frame.Commands, &waitAfter );
		}
	}

	//-- the object set a dispatch or a pulled draw is given ----------------

	//A set over set 2 from one to eight buffers, bindings past the count repeating the first; retired
	//with the frame. What a dispatch reads and what a draw that pulls its vertices out of the pool reads.
	private static DescriptorSet TransientObjectSet( ulong* buffers, int bufferCount, string who )
	{
		if ( buffers is null || bufferCount < 1 || bufferCount > (int)VkDevice.ObjectBindings )
		{
			VkDevice.SetText( $"{who} takes one to {VkDevice.ObjectBindings} buffers" );
			return default;
		}

		var traced = DrawTrace.Begin();

		//Fresh set per entry; caching saved almost nothing and risks use-after-free on dead buffers
		var bufferHandles = stackalloc VkBuffer[(int)VkDevice.ObjectBindings];

		for ( var i = 0; i < (int)VkDevice.ObjectBindings; ++i )
		{
			var handle = i < bufferCount ? buffers[i] : buffers[0];
			bufferHandles[i] = VkBuffers.Get( handle );

			if ( bufferHandles[i].Handle == 0 )
			{
				VkDevice.SetText( $"{who} was given a buffer that is not there" );
				return default;
			}
		}

		//One this frame already made for these very buffers, if it did.
		var kept = CurrentFrame;
		var bindings = (int)VkDevice.ObjectBindings;

		for ( var s = 0; s < kept.ObjectSetCount; ++s )
		{
			var same = true;

			for ( var i = 0; i < bindings; ++i )
			{
				if ( kept.ObjectKeys[( s * bindings ) + i] == bufferHandles[i].Handle ) continue;

				same = false;
				break;
			}

			if ( same )
			{
				DrawTrace.Phase( DrawTrace.Kind.ObjectSet, traced );

				return kept.ObjectSets[s];
			}
		}

		if ( !VkDevice.AllocateDescriptorSet( VkDevice.objectSetLayout, out var set ) )
		{
			VkDevice.SetText( $"no descriptor set for {who}" );
			return default;
		}

		var infos = stackalloc DescriptorBufferInfo[(int)VkDevice.ObjectBindings];
		var writes = stackalloc WriteDescriptorSet[(int)VkDevice.ObjectBindings];

		for ( var i = 0u; i < VkDevice.ObjectBindings; ++i )
		{
			infos[i].Buffer = bufferHandles[i];
			infos[i].Offset = 0;
			infos[i].Range = Vk.WholeSize;

			writes[i].SType = StructureType.WriteDescriptorSet;
			writes[i].DstSet = set;
			writes[i].DstBinding = i;
			writes[i].DescriptorCount = 1;
			writes[i].DescriptorType = DescriptorType.StorageBuffer;
			writes[i].PBufferInfo = &infos[i];
		}

		VkDevice.vk.UpdateDescriptorSets( VkDevice.device, VkDevice.ObjectBindings, writes, 0, null );
		VkDevice.RetireDescriptorSet( set );

		//Kept for the rest of this frame, while there is room to keep it.
		if ( kept.ObjectSetCount < kept.ObjectSets.Length )
		{
			for ( var i = 0; i < bindings; ++i )
				kept.ObjectKeys[( kept.ObjectSetCount * bindings ) + i] = bufferHandles[i].Handle;

			kept.ObjectSets[kept.ObjectSetCount++] = set;
		}

		DrawTrace.Phase( DrawTrace.Kind.ObjectSet, traced );

		return set;
	}

	//-- compute ------------------------------------------------------------

	//A dispatch, by counts given here or by three read out of a buffer the card wrote.
	private static int RecordDispatch( ulong pipeline, ulong* buffers, int bufferCount,
		ulong texture, byte* pushData, int pushSize,
		int groupsX, int groupsY, int groupsZ, ulong argsBuffer, int argsOffset )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording ) return 0;
			if ( argsBuffer == 0 && ( groupsX <= 0 || groupsY <= 0 || groupsZ <= 0 ) ) return 0;

			var args = argsBuffer != 0 ? VkBuffers.Get( argsBuffer ) : default;
			if ( argsBuffer != 0 && ( args.Handle == 0 || argsOffset < 0 ) ) return 0;

			if ( !VkPipelines.IsCompute( pipeline ) )
			{
				VkDevice.SetText( "that is not a compute pipeline" );
				return 0;
			}

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );

			if ( bound.Handle == 0 || layout.Handle == 0 ) return 0;

			//The set names every binding whatever the shader reads, because a binding left unwritten is one the driver may answer for however it likes.
			var set = TransientObjectSet( buffers, bufferCount, "a dispatch" );
			if ( set.Handle == 0 ) return 0;

			DescriptorSet pictures = default;

			if ( texture != 0 )
			{
				pictures = VkTextures.SetOf( texture );

				if ( pictures.Handle == 0 )
				{
					VkDevice.SetText( "a dispatch was given a texture that is not there" );
					return 0;
				}
			}

			var vk = VkDevice.vk;
			var frame = CurrentFrame;

			//Outside any rendering. A dispatch is not part of a pass and cannot be recorded inside one, so
			//whatever is open closes first - the main pass reopens by itself when the next draw asks for it.
			CloseOpenPasses( frame );

			vk.CmdBindPipeline( frame.Commands, PipelineBindPoint.Compute, bound );

			vk.CmdBindDescriptorSets( frame.Commands, PipelineBindPoint.Compute, layout, 0, 1, &set, 0, null );

			if ( pictures.Handle != 0 )
			{
				//After the pass above was closed, because a layout cannot change while the picture is attached to something.
				ReadyPictureForRead( frame, texture );

				vk.CmdBindDescriptorSets( frame.Commands, PipelineBindPoint.Compute, layout, 1, 1, &pictures, 0, null );
			}

			PushConstants( frame, layout, ShaderStageFlags.ComputeBit, pushData, pushSize );

			if ( args.Handle != 0 )
			{
				vk.CmdDispatchIndirect( frame.Commands, args, (ulong)argsOffset );
			}
			else
			{
				vk.CmdDispatch( frame.Commands, (uint)groupsX, (uint)groupsY, (uint)groupsZ );
			}

			if ( barriersHeld ) dispatchOwed = true;
			else Barriers( frame, copies: false, dispatches: true );

			frame.StatDispatches++;

			DrawTrace.End( DrawTrace.Kind.Dispatch, traced, inside );

			return 1;
		}
	}

	//Set while what is recorded - frame writes and dispatches - reads nothing another of them writes: one barrier
	//after the lot, for what is owed - see HoldBarriers
	private static bool barriersHeld, copyOwed, dispatchOwed;

	/// <summary>Holds the barrier after each frame write and dispatch until released, then records one for them all.</summary>
	internal static void HoldBarriers( bool hold )
	{
		lock ( VkDevice.renderLock )
		{
			barriersHeld = hold;

			if ( hold ) return;

			if ( ( copyOwed || dispatchOwed ) && Current.Recording ) Barriers( CurrentFrame, copyOwed, dispatchOwed );

			copyOwed = dispatchOwed = false;
		}
	}

	//What copies and dispatches wrote made visible to every later stage that may read it; broad on purpose, as
	//narrowing it was measured slower
	private static void Barriers( Frame frame, bool copies, bool dispatches )
	{
		var barriers = stackalloc MemoryBarrier2[2];
		var count = 0u;

		if ( copies )
		{
			barriers[count++] = new MemoryBarrier2
			{
				SType = StructureType.MemoryBarrier2,
				SrcStageMask = PipelineStageFlags2.CopyBit,
				SrcAccessMask = AccessFlags2.TransferWriteBit,
				DstStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.VertexShaderBit |
					PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.DrawIndirectBit | PipelineStageFlags2.CopyBit,
				DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit |
					AccessFlags2.IndirectCommandReadBit | AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit,
			};
		}

		if ( dispatches )
		{
			barriers[count++] = new MemoryBarrier2
			{
				SType = StructureType.MemoryBarrier2,
				SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
				SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
				DstStageMask = PipelineStageFlags2.ComputeShaderBit |
					PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit |
					PipelineStageFlags2.VertexInputBit | PipelineStageFlags2.DrawIndirectBit |
					PipelineStageFlags2.CopyBit,
				DstAccessMask = AccessFlags2.ShaderStorageReadBit |
					AccessFlags2.VertexAttributeReadBit | AccessFlags2.IndexReadBit |
					AccessFlags2.IndirectCommandReadBit | AccessFlags2.TransferReadBit,
			};
		}

		var dependency = new DependencyInfo
		{
			SType = StructureType.DependencyInfo,
			MemoryBarrierCount = count,
			PMemoryBarriers = barriers,
		};

		VkDevice.dialect.Barrier( frame.Commands, &dependency );
	}

	internal static int ComputeDispatch( ulong pipeline, ulong* buffers, int bufferCount,
		ulong texture, byte* pushData, int pushSize, int groupsX, int groupsY, int groupsZ )
		=> RecordDispatch( pipeline, buffers, bufferCount, texture, pushData, pushSize, groupsX, groupsY, groupsZ, 0, 0 );

	internal static int ComputeDispatchIndirect( ulong pipeline, ulong* buffers, int bufferCount,
		ulong texture, byte* pushData, int pushSize, ulong argsBuffer, int argsOffset )
		=> RecordDispatch( pipeline, buffers, bufferCount, texture, pushData, pushSize, 1, 1, 1, argsBuffer, argsOffset );

	internal static int PyramidBuild( ulong pipeline, ulong pyramid, int lod, int count,
		ulong source, byte* pushData, int pushSize, ulong argsBuffer = 0, int argsOffset = 0 )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || lod < 0 || count < 1 || count > (int)VkDevice.PyramidLevelsAtOnce ) return 0;
			if ( !VkPipelines.IsCompute( pipeline ) ) return 0;

			var args = argsBuffer != 0 ? VkBuffers.Get( argsBuffer ) : default;
			if ( argsBuffer != 0 && ( args.Handle == 0 || argsOffset < 0 ) ) return 0;

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );

			if ( bound.Handle == 0 || layout.Handle == 0 ) return 0;

			VkImage image = default;
			var into = stackalloc ImageView[(int)VkDevice.PyramidLevelsAtOnce];
			ImageView whole = default;
			uint levelWidth = 0, levelHeight = 0, mips = 0;
			var first = false;

			for ( var l = 0; l < count; ++l )
			{
				if ( !VkTextures.PyramidLevel( pyramid, (uint)( lod + l ), out image, out into[l], out whole,
					out var width, out var height, out mips, out var fresh ) )
				{
					VkDevice.SetText( "that level of the depth pyramid is not there" );
					return 0;
				}

				if ( l == 0 )
				{
					levelWidth = width;
					levelHeight = height;
					first = fresh;
				}
			}

			//The slots past the levels asked for point at the last one; the shader never stores into them.
			for ( var l = count; l < (int)VkDevice.PyramidLevelsAtOnce; ++l ) into[l] = into[count - 1];

			ImageView from = default;

			if ( lod == 0 && !VkTextures.ViewOf( source, out from ) )
			{
				VkDevice.SetText( "the depth the pyramid is built from is not there" );
				return 0;
			}

			if ( lod > 0 ) from = whole;

			var vk = VkDevice.vk;
			var frame = CurrentFrame;

			CloseOpenPasses( frame );

			if ( lod == 0 ) ReadyPictureForRead( frame, source );

			if ( first )
			{
				Transition( frame.Commands, image, ImageLayout.Undefined, ImageLayout.General,
					PipelineStageFlags2.TopOfPipeBit, 0,
					PipelineStageFlags2.ComputeShaderBit,
					AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderSampledReadBit,
					ImageAspectFlags.ColorBit, 0, mips );

				VkTextures.PyramidReady( pyramid );
			}

			if ( !VkDevice.AllocateDescriptorSet( VkDevice.pyramidSetLayout, out var set ) )
			{
				VkDevice.SetText( "no descriptor set for a pyramid level" );
				return 0;
			}

			VkDevice.RetireDescriptorSet( set );

			var below = new DescriptorImageInfo
			{
				Sampler = VkDevice.farthestSampler,
				ImageView = from,
				ImageLayout = lod == 0 ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.General,
			};

			var targets = stackalloc DescriptorImageInfo[(int)VkDevice.PyramidLevelsAtOnce];
			var writes = stackalloc WriteDescriptorSet[1 + (int)VkDevice.PyramidLevelsAtOnce];

			writes[0].SType = StructureType.WriteDescriptorSet;
			writes[0].DstSet = set;
			writes[0].DstBinding = 0;
			writes[0].DescriptorCount = 1;
			writes[0].DescriptorType = DescriptorType.CombinedImageSampler;
			writes[0].PImageInfo = &below;

			for ( var l = 0u; l < VkDevice.PyramidLevelsAtOnce; ++l )
			{
				targets[l].ImageView = into[l];
				targets[l].ImageLayout = ImageLayout.General;

				writes[1 + l].SType = StructureType.WriteDescriptorSet;
				writes[1 + l].DstSet = set;
				writes[1 + l].DstBinding = 1 + l;
				writes[1 + l].DescriptorCount = 1;
				writes[1 + l].DescriptorType = DescriptorType.StorageImage;
				writes[1 + l].PImageInfo = &targets[l];
			}

			vk.UpdateDescriptorSets( VkDevice.device, 1 + VkDevice.PyramidLevelsAtOnce, writes, 0, null );

			vk.CmdBindPipeline( frame.Commands, PipelineBindPoint.Compute, bound );

			vk.CmdBindDescriptorSets( frame.Commands, PipelineBindPoint.Compute, layout, 1, 1, &set, 0, null );

			PushConstants( frame, layout, ShaderStageFlags.ComputeBit, pushData, pushSize );

			//A group a 32x32 tile of the first level written - or however many the card wrote down, which is the same number or none.
			if ( args.Handle != 0 )
				vk.CmdDispatchIndirect( frame.Commands, args, (ulong)argsOffset );
			else
				vk.CmdDispatch( frame.Commands, ( levelWidth + 31 ) / 32, ( levelHeight + 31 ) / 32, 1 );

			var written = new MemoryBarrier2
			{
				SType = StructureType.MemoryBarrier2,
				SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
				SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
				DstStageMask = PipelineStageFlags2.ComputeShaderBit,
				DstAccessMask = AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit,
			};

			var settled = new DependencyInfo
			{
				SType = StructureType.DependencyInfo,
				MemoryBarrierCount = 1,
				PMemoryBarriers = &written,
			};

			VkDevice.dialect.Barrier( frame.Commands, &settled );

			frame.StatDispatches++;

			return 1;
		}
	}
}
