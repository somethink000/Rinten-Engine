using Silk.NET.Vulkan;

namespace Rinten;

//What a frame's passes do with the pictures of VkPictures: draw into one level, fill a chain of halvings, and run a compute pass that stores
//into levels - every one of them leaving what it wrote readable, because the next thing to touch a picture is always something reading it.
internal static unsafe partial class VkFrame
{
	//One level into a layout, from wherever it was.
	private static void LevelTo( CommandBuffer commands, VkPictures.Picture picture, uint level, ImageLayout wanted )
	{
		var was = picture.Layout[level];

		if ( was == wanted ) return;

		Transition( commands, picture.Image, was, wanted,
			PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit,
			PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
			ImageAspectFlags.ColorBit, level, 1 );

		picture.Layout[level] = wanted;
	}

	//Moves every mip of a picture to one layout in as few barriers as possible; only = required old layout
	private static void LevelsTo( CommandBuffer commands, VkPictures.Picture picture, ImageLayout wanted,
		ImageLayout? only = null )
	{
		var count = picture.Levels;

		if ( count == 0 ) return;

		var barriers = stackalloc ImageMemoryBarrier2[(int)count];
		var made = 0u;

		for ( var level = 0u; level < count; )
		{
			var was = picture.Layout[level];

			if ( was == wanted || ( only is { } from && was != from ) ) { level++; continue; }

			//As many levels as share the layout this one is leaving.
			var run = level;
			while ( run < count && picture.Layout[run] == was ) run++;

			barriers[made++] = new ImageMemoryBarrier2
			{
				SType = StructureType.ImageMemoryBarrier2,
				SrcStageMask = PipelineStageFlags2.AllCommandsBit,
				SrcAccessMask = AccessFlags2.MemoryWriteBit,
				DstStageMask = PipelineStageFlags2.AllCommandsBit,
				DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
				OldLayout = was,
				NewLayout = wanted,
				SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
				DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
				Image = picture.Image,
				SubresourceRange = new ImageSubresourceRange( ImageAspectFlags.ColorBit, level, run - level, 0, 1 ),
			};

			for ( var l = level; l < run; ++l ) picture.Layout[l] = wanted;

			level = run;
		}

		if ( made == 0 ) return;

		var dependency = new DependencyInfo
		{
			SType = StructureType.DependencyInfo,
			ImageMemoryBarrierCount = made,
			PImageMemoryBarriers = barriers,
		};

		VkDevice.dialect.Barrier( commands, &dependency );
	}

	//Every level out of UNDEFINED, once: a shader reads the picture through the view
	//over all of it, whether or not anything filled the levels it does not ask for.
	private static void ReadyLevels( CommandBuffer commands, VkPictures.Picture picture )
	{
		var any = false;

		for ( var level = 0u; level < picture.Levels; ++level )
			any |= picture.Layout[level] == ImageLayout.Undefined;

		if ( !any ) return;

		LevelsTo( commands, picture, ImageLayout.ShaderReadOnlyOptimal, only: ImageLayout.Undefined );
	}

	//One level of a picture as what the next draws fill - see TargetBegin, which this is for a picture of VkPictures;
	//TargetEnd closes it. A clear of nought keeps what is there, two throws it away, anything else clears to the colour.
	internal static int PictureBegin( ulong id, int level, float r, float g, float b, float a, int clear, int depth = 0 )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || Current.TargetPass ) return 0;

			if ( Current.MainPass )
			{
				VkDevice.SetText( "the passes before the screen have to come before it" );
				return 0;
			}

			var picture = VkPictures.Get( id );

			if ( picture is null || level < 0 || level >= picture.Levels ) return 0;

			var frame = CurrentFrame;

			ReadyLevels( frame.Commands, picture );
			LevelTo( frame.Commands, picture, (uint)level, ImageLayout.ColorAttachmentOptimal );

			var extent = VkPictures.LevelExtent( picture, level );

			var colour = new RenderingAttachmentInfo
			{
				SType = StructureType.RenderingAttachmentInfo,
				ImageView = picture.Level[level],
				ImageLayout = ImageLayout.ColorAttachmentOptimal,
				LoadOp = clear == 2 ? AttachmentLoadOp.DontCare : clear != 0 ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
				StoreOp = AttachmentStoreOp.Store,
			};

			colour.ClearValue.Color = new ClearColorValue( r, g, b, a );

			//And the world's depth, read and not written, for a pass that tests against it - a draw that marks out
			//what is in front of a wall and what is behind one. Only for a picture of the world's size, the depth's.
			RenderingAttachmentInfo depthAttachment = default;
			var p = Pics;

			if ( depth != 0 )
			{
				if ( p is null || p.DepthImage.Handle == 0 || !p.DepthFilled ||
					extent.Width != p.Extent.Width || extent.Height != p.Extent.Height )
				{
					VkDevice.SetText( "a picture pass asked for the world's depth, and the picture is not the world's size" );
					return 0;
				}

				MoveTo( frame.Commands, p.DepthImage, ref p.DepthLayout, ImageLayout.DepthAttachmentOptimal, ImageAspectFlags.DepthBit );

				depthAttachment.SType = StructureType.RenderingAttachmentInfo;
				depthAttachment.ImageView = p.DepthView;
				depthAttachment.ImageLayout = ImageLayout.DepthAttachmentOptimal;
				depthAttachment.LoadOp = AttachmentLoadOp.Load;
				depthAttachment.StoreOp = AttachmentStoreOp.Store;
			}

			var rendering = new RenderingInfo
			{
				SType = StructureType.RenderingInfo,
				RenderArea = new Rect2D( default, extent ),
				LayerCount = 1,
				ColorAttachmentCount = 1,
				PColorAttachments = &colour,
				PDepthAttachment = depth != 0 ? &depthAttachment : null,
			};

			VkDevice.dialect.BeginRendering( frame.Commands, &rendering, VkPassFormats.Of(
				picture.Format, depth != 0 ? VkDevice.DepthFormat : Format.Undefined ) );

			Current.TargetPass = true;
			Current.Target = VkDevice.TargetPicture;
			Current.TargetExtent = extent;
			Current.PictureId = id;
			Current.PictureLevel = level;
			Current.PictureDepth = depth != 0;

			return 1;
		}
	}

	//The world's pass into a picture of the screen's format and size, so the screen's pipelines draw there unchanged - its
	//texture; or, turned off, the pass closed and what it drew left readable, and the next draw back on the screen
	internal static ulong WorldAside( int on, out int width, out int height )
	{
		width = height = 0;

		lock ( VkDevice.renderLock )
		{
			var p = Pics;
			var view = VkSwapchain.currentView;

			//A view drawing both eyes at once has no one picture to draw the world into
			if ( !Current.Recording || p is null || Current.TargetPass || view is { Layers: > 1 } ) return 0;

			var frame = CurrentFrame;

			if ( on == 0 )
			{
				if ( !p.AsideOn ) return 0;

				CloseOpenPasses( frame );
				p.AsideOn = false;

				if ( VkPictures.Get( p.Aside ) is { } drawn )
					LevelTo( frame.Commands, drawn, 0, ImageLayout.ShaderReadOnlyOptimal );

				return 0;
			}

			var format = view is not null ? view.OutputFormat : Current.Format;
			var extent = p.ScreenExtent;

			if ( VkPictures.Get( p.Aside ) is not { } aside || aside.Format != format ||
				aside.Extent.Width != extent.Width || aside.Extent.Height != extent.Height )
			{
				if ( p.Aside != 0 ) VkPictures.Release( p.Aside );

				p.Aside = VkPictures.AcquireIn( format, extent.Width, extent.Height );
				aside = VkPictures.Get( p.Aside );
			}

			if ( aside is null ) return 0;

			//What the screen's pass drew so far stays on the screen; the world starts afresh in the picture
			CloseOpenPasses( frame );
			p.AsideOn = true;

			width = (int)extent.Width;
			height = (int)extent.Height;
			return aside.Handle;
		}
	}

	//A picture pass left open ended the way TargetEnd ends one - closed, and what it drew back to being read.
	//CloseOpenPasses alone would leave the level in the attachment layout with nothing tracking that it is.
	private static void EndPictureIfOpen( Frame frame )
	{
		if ( !Current.TargetPass || Current.Target != VkDevice.TargetPicture ) return;

		VkDevice.dialect.EndRendering( frame.Commands );
		Current.TargetPass = false;

		PictureEnd( frame );

		Current.Target = VkDevice.TargetScreen;
	}

	//The level a picture pass drew back to being read - see TargetEnd.
	private static void PictureEnd( Frame frame )
	{
		if ( VkPictures.Get( Current.PictureId ) is { } picture )
			LevelTo( frame.Commands, picture, (uint)Current.PictureLevel, ImageLayout.ShaderReadOnlyOptimal );

		//And the depth it borrowed, which everything after it samples.
		if ( Current.PictureDepth && Pics is { } p && p.DepthImage.Handle != 0 )
			MoveTo( frame.Commands, p.DepthImage, ref p.DepthLayout, ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.DepthBit );

		Current.PictureId = 0;
		Current.PictureLevel = 0;
		Current.PictureDepth = false;
	}

	//Fills a picture's chain of halvings out of its top level, each level the linear blit of the one above - what
	//a pass that reads blurred copies of what it was handed wants. Outside any pass: whatever is open closes.
	internal static int PictureChain( ulong id, ulong fold = 0 )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording ) return 0;

			var picture = VkPictures.Get( id );

			if ( picture is null ) return 0;
			if ( picture.Levels <= 1 ) return 1;

			var frame = CurrentFrame;
			var commands = frame.Commands;

			EndPictureIfOpen( frame );
			CloseOpenPasses( frame );
			ReadyLevels( commands, picture );

			//Folded by compute where the picture can be stored into and there is a fold for its format; blitted down otherwise.
			if ( fold != 0 && picture.Storage && VkPipelines.IsCompute( fold ) ) return FoldChain( frame, picture, fold );

			LevelTo( commands, picture, 0, ImageLayout.TransferSrcOptimal );

			for ( var level = 1u; level < picture.Levels; ++level )
			{
				var from = VkPictures.LevelExtent( picture, (int)level - 1 );
				var into = VkPictures.LevelExtent( picture, (int)level );

				LevelTo( commands, picture, level, ImageLayout.TransferDstOptimal );

				var region = new ImageBlit
				{
					SrcSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, level - 1, 0, 1 ),
					DstSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, level, 0, 1 ),
				};

				region.SrcOffsets[1] = new Offset3D( (int)from.Width, (int)from.Height, 1 );
				region.DstOffsets[1] = new Offset3D( (int)into.Width, (int)into.Height, 1 );

				VkDevice.vk.CmdBlitImage( commands,
					picture.Image, ImageLayout.TransferSrcOptimal,
					picture.Image, ImageLayout.TransferDstOptimal,
					1, &region, Filter.Linear );

				//The source of the next one.
				LevelTo( commands, picture, level, ImageLayout.TransferSrcOptimal );
			}

			LevelsTo( commands, picture, ImageLayout.ShaderReadOnlyOptimal );

			return 1;
		}
	}

	//Fills a mip chain with picture_fold.slang, six levels per dispatch, all levels in GENERAL meanwhile
	private static int FoldChain( Frame frame, VkPictures.Picture picture, ulong fold )
	{
		var bound = VkPipelines.Get( fold );
		var layout = VkPipelines.LayoutGet( fold );

		if ( bound.Handle == 0 || layout.Handle == 0 ) return 0;

		var commands = frame.Commands;
		var vk = VkDevice.vk;
		var levels = (int)picture.Levels;

		LevelsTo( commands, picture, ImageLayout.General );

		vk.CmdBindPipeline( commands, PipelineBindPoint.Compute, bound );

		var push = stackalloc uint[8];

		for ( var from = 0; from + 1 < levels; from += (int)VkDevice.FoldLevelsAtOnce )
		{
			var count = Math.Min( (int)VkDevice.FoldLevelsAtOnce, levels - 1 - from );

			if ( !VkDevice.AllocateDescriptorSet( VkDevice.foldSetLayout, out var set ) )
			{
				VkDevice.SetText( "no descriptor set for a fold" );
				return 0;
			}

			VkDevice.RetireDescriptorSet( set );

			var read = new DescriptorImageInfo
			{
				Sampler = VkDevice.clampSampler,
				ImageView = picture.View,
				ImageLayout = ImageLayout.General,
			};

			var stores = stackalloc DescriptorImageInfo[(int)VkDevice.FoldLevelsAtOnce];
			var writes = stackalloc WriteDescriptorSet[2 + (int)VkDevice.FoldLevelsAtOnce];

			for ( var l = 0; l < (int)VkDevice.FoldLevelsAtOnce; ++l )
			{
				stores[l] = new DescriptorImageInfo
				{
					ImageView = picture.Level[from + 1 + Math.Min( l, count - 1 )],
					ImageLayout = ImageLayout.General,
				};
			}

			for ( var b = 0; b < 2 + (int)VkDevice.FoldLevelsAtOnce; ++b )
			{
				writes[b] = new WriteDescriptorSet
				{
					SType = StructureType.WriteDescriptorSet,
					DstSet = set,
					DstBinding = (uint)b,
					DescriptorCount = 1,
					DescriptorType = b < 2 ? DescriptorType.CombinedImageSampler : DescriptorType.StorageImage,
					PImageInfo = b < 2 ? &read : &stores[b - 2],
				};
			}

			vk.UpdateDescriptorSets( VkDevice.device, 2 + VkDevice.FoldLevelsAtOnce, writes, 0, null );
			vk.CmdBindDescriptorSets( commands, PipelineBindPoint.Compute, layout, 1, 1, &set, 0, null );

			push[0] = 0;
			push[1] = (uint)from;
			push[2] = (uint)count;
			push[3] = 0;
			( (float*)push )[4] = picture.Extent.Width;
			( (float*)push )[5] = picture.Extent.Height;

			PushConstants( frame, layout, ShaderStageFlags.ComputeBit, push, 32 );

			var first = VkPictures.LevelExtent( picture, from + 1 );

			vk.CmdDispatch( commands, ( first.Width + 31 ) / 32, ( first.Height + 31 ) / 32, 1 );

			//The next dispatch starts from the last level this one stored.
			var written = new MemoryBarrier2
			{
				SType = StructureType.MemoryBarrier2,
				SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
				SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
				DstStageMask = PipelineStageFlags2.ComputeShaderBit,
				DstAccessMask = AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit,
			};

			var dependency = new DependencyInfo
			{
				SType = StructureType.DependencyInfo,
				MemoryBarrierCount = 1,
				PMemoryBarriers = &written,
			};

			VkDevice.dialect.Barrier( commands, &dependency );

			frame.StatDispatches++;
		}

		LevelsTo( commands, picture, ImageLayout.ShaderReadOnlyOptimal );

		return 1;
	}

	//Compute pass bound like a fullscreen blit; set 2 holds the storage outputs (images, then buffers)
	private static readonly VkPictures.Picture[] passStored = new VkPictures.Picture[VkDevice.PassImages];

	internal static int PassDispatch( ulong pipeline, ulong material, ulong* outputs, int* levels, int outputCount,
		ulong* buffers, int bufferCount, byte* pushData, int pushSize, int groupsX, int groupsY, int groupsZ )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording ) return 0;
			if ( groupsX <= 0 || groupsY <= 0 || groupsZ <= 0 ) return 0;
			if ( outputCount > (int)VkDevice.PassImages || bufferCount > (int)VkDevice.PassBuffers ) return 0;

			if ( !VkPipelines.IsCompute( pipeline ) )
			{
				VkDevice.SetText( "that is not a compute pipeline" );
				return 0;
			}

			var bound = VkPipelines.Get( pipeline );
			var layout = VkPipelines.LayoutGet( pipeline );

			if ( bound.Handle == 0 || layout.Handle == 0 ) return 0;

			var pictures = stackalloc DescriptorImageInfo[(int)VkDevice.PassImages];
			var stored = passStored;

			for ( var i = 0; i < outputCount; ++i )
			{
				var picture = VkPictures.Get( outputs[i] );

				if ( picture is null || !picture.Storage || levels[i] < 0 || levels[i] >= picture.Levels )
				{
					VkDevice.SetText( "a pass was asked to store into a picture that cannot be stored into" );
					return 0;
				}

				stored[i] = picture;
			}

			DescriptorSet materialSet = default;

			if ( material != 0 )
			{
				materialSet = VkTextures.SetOf( material );

				if ( materialSet.Handle == 0 )
				{
					VkDevice.SetText( "a pass was given a material that is not there" );
					return 0;
				}
			}

			if ( !VkDevice.AllocateDescriptorSet( VkDevice.passSetLayout, out var passSet ) )
			{
				VkDevice.SetText( "no descriptor set for a pass" );
				return 0;
			}

			VkDevice.RetireDescriptorSet( passSet );

			var frame = CurrentFrame;
			var commands = frame.Commands;
			var vk = VkDevice.vk;

			//Outside any rendering, and with what it stores into in GENERAL.
			EndPictureIfOpen( frame );
			CloseOpenPasses( frame );

			for ( var i = 0; i < outputCount; ++i )
			{
				ReadyLevels( commands, stored[i] );
				LevelTo( commands, stored[i], (uint)levels[i], ImageLayout.General );
			}

			//Every binding named: the ones past what was given name the last one given, and are never stored into.
			var writes = stackalloc WriteDescriptorSet[(int)( VkDevice.PassImages + VkDevice.PassBuffers )];
			var bufferInfos = stackalloc DescriptorBufferInfo[(int)VkDevice.PassBuffers];
			var writeCount = 0u;

			if ( outputCount > 0 )
			{
				for ( var b = 0; b < (int)VkDevice.PassImages; ++b )
				{
					var at = Math.Min( b, outputCount - 1 );

					pictures[b] = new DescriptorImageInfo
					{
						ImageView = stored[at].Level[levels[at]],
						ImageLayout = ImageLayout.General,
					};

					writes[writeCount++] = new WriteDescriptorSet
					{
						SType = StructureType.WriteDescriptorSet,
						DstSet = passSet,
						DstBinding = (uint)b,
						DescriptorCount = 1,
						DescriptorType = DescriptorType.StorageImage,
						PImageInfo = &pictures[b],
					};
				}
			}

			if ( bufferCount > 0 )
			{
				for ( var b = 0; b < (int)VkDevice.PassBuffers; ++b )
				{
					var buffer = VkBuffers.Get( buffers[Math.Min( b, bufferCount - 1 )] );

					if ( buffer.Handle == 0 )
					{
						VkDevice.SetText( "a pass was given a buffer that is not there" );
						return 0;
					}

					bufferInfos[b] = new DescriptorBufferInfo { Buffer = buffer, Offset = 0, Range = Vk.WholeSize };

					writes[writeCount++] = new WriteDescriptorSet
					{
						SType = StructureType.WriteDescriptorSet,
						DstSet = passSet,
						DstBinding = VkDevice.PassImages + (uint)b,
						DescriptorCount = 1,
						DescriptorType = DescriptorType.StorageBuffer,
						PBufferInfo = &bufferInfos[b],
					};
				}
			}

			if ( writeCount > 0 ) vk.UpdateDescriptorSets( VkDevice.device, writeCount, writes, 0, null );

			vk.CmdBindPipeline( commands, PipelineBindPoint.Compute, bound );

			if ( materialSet.Handle != 0 )
				vk.CmdBindDescriptorSets( commands, PipelineBindPoint.Compute, layout, 0, 1, &materialSet, 0, null );

			var offsets = stackalloc uint[3];
			offsets[0] = (uint)frame.NullBones;
			offsets[1] = (uint)frame.FrameBlock;
			offsets[2] = 0;

			var frameSet = frame.BoneSet;
			vk.CmdBindDescriptorSets( commands, PipelineBindPoint.Compute, layout, 1, 1, &frameSet, 3, offsets );

			vk.CmdBindDescriptorSets( commands, PipelineBindPoint.Compute, layout, 2, 1, &passSet, 0, null );

			if ( VkDevice.bindless && VkDevice.bindlessSet.Handle != 0 )
			{
				var table = VkDevice.bindlessSet;
				vk.CmdBindDescriptorSets( commands, PipelineBindPoint.Compute, layout, 3, 1, &table, 0, null );
			}

			PushConstants( frame, layout, ShaderStageFlags.ComputeBit, pushData, pushSize );

			vk.CmdDispatch( commands, (uint)groupsX, (uint)groupsY, (uint)groupsZ );

			//What it stored, readable by whatever comes next - a draw, a blit, the next dispatch.
			for ( var i = 0; i < outputCount; ++i )
				LevelTo( commands, stored[i], (uint)levels[i], ImageLayout.ShaderReadOnlyOptimal );

			if ( bufferCount > 0 )
			{
				var barrier = new MemoryBarrier2
				{
					SType = StructureType.MemoryBarrier2,
					SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
					SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
					DstStageMask = PipelineStageFlags2.AllCommandsBit,
					DstAccessMask = AccessFlags2.MemoryReadBit,
				};

				var dependency = new DependencyInfo
				{
					SType = StructureType.DependencyInfo,
					MemoryBarrierCount = 1,
					PMemoryBarriers = &barrier,
				};

				VkDevice.dialect.Barrier( commands, &dependency );
			}

			frame.StatDispatches++;

			return 1;
		}
	}
}
