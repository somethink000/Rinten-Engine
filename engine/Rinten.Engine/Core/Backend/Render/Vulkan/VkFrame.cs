using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Rinten;

//A frame: what one recording owns, what it may bind without saying so twice, and the passes it opens and closes.
internal static unsafe partial class VkFrame
{
	//What one in-flight frame needs to itself.
	internal sealed class Frame
	{
		//How many distinct buffer sets one frame is expected to bind - see ObjectKeys.
		private const int ObjectSetsKept = 16;

		public CommandPool Pool;
		public CommandBuffer Commands;

		/// <summary>Signalled when the image this frame drew into is free.</summary>
		public VkSemaphore ImageAvailable;

		/// <summary>The same for the second image a frame takes for a generated frame before it.</summary>
		public VkSemaphore GeneratedAvailable;

		/// <summary>Signalled when the GPU finishes the frame, so its command buffer may be recorded over.</summary>
		public Fence Finished;

		/// <summary>Whether a submit is pending on that fence; a failed one would otherwise hang the next wait.</summary>
		public bool Submitted;

		/// <summary>Per-source readback buffer, safe to read once the slot's fence has been waited.</summary>
		public sealed class Readback
		{
			public VkBuffer Staging;
			public DeviceMemory Memory;
			public byte* Mapped;
			public ulong Size;

			/// <summary>Whether a copy was recorded into it, so a first frame reads nothing rather than garbage.</summary>
			public bool Filled;
		}

		public readonly Dictionary<ulong, Readback> Readbacks = new();

		/// <summary>Set 1 needs rewriting before next record; can't rewrite while the frame may be in flight</summary>
		public bool FrameSetStale;

		/// <summary>This frame's mapped scratch for draw-time geometry; grown after the fence wait.</summary>
		public VkBuffer Scratch;
		public DeviceMemory ScratchMemory;
		public byte* ScratchMapped;
		public ulong ScratchCapacity;
		public ulong ScratchUsed;

		/// <summary>The most scratch any frame has wanted, given up front - see EnsureScratch.</summary>
		public ulong ScratchPeak;

		/// <summary>Points at this frame's scratch, so a skinned draw supplies only the offset.</summary>
		public DescriptorSet BoneSet;

		/// <summary>Where the camera and lights and the boneless draws' block sit in the scratch.</summary>
		public ulong FrameBlock;
		public ulong NullBones;

		/// <summary>Morphed models' vertices posed for this frame, a VkBuffers handle - see VkFrame.Posed.</summary>
		public ulong Posed;
		public ulong PosedCapacity, PosedUsed, PosedPeak;

		/// <summary>Scratch outgrown mid-frame, kept until the frame's fence proves the GPU done with it.</summary>
		public readonly List<(VkBuffer Buffer, DeviceMemory Memory)> RetiredScratch = new();

		/// <summary>The submission this frame last went as, for the graveyard.</summary>
		public ulong Ticket;

		//-- profiler: per-frame timestamp pool, two for the frame and two per zone, read after the fence

		/// <summary>The pool, or null when the queue cannot timestamp at all.</summary>
		public QueryPool Queries;

		/// <summary>One pipeline statistics query per frame, for RINTEN_ZONE_STATS's zone or the whole frame; such queries can't nest</summary>
		public QueryPool Stats;

		/// <summary>Whether this frame's one statistics query has been taken, and at what zone depth it was opened.</summary>
		public bool StatsTaken;
		public bool StatsRunning;
		public int StatsDepth;

		/// <summary>Object sets reused within this frame by the buffers they name; cleared by BeginFrame</summary>
		public readonly ulong[] ObjectKeys = new ulong[ObjectSetsKept * (int)VkDevice.ObjectBindings];
		public readonly DescriptorSet[] ObjectSets = new DescriptorSet[ObjectSetsKept];
		public int ObjectSetCount;

		/// <summary>The next free timestamp: 0 and 1 are the frame's, zones take two each from 2.</summary>
		public uint QueryNext;

		/// <summary>One named span of the frame, as recorded.</summary>
		public struct ZoneRecord
		{
			public fixed byte Name[VkDevice.ProfileNameSize];
			public uint Begin;
			public uint End;
			public int Depth;
			public long CpuBegin;
			public long CpuEnd;
		}

		public readonly ZoneRecord[] Zones = new ZoneRecord[VkDevice.ProfileMaxZones];
		public uint ZoneCount;

		/// <summary>The zones open right now, as indices, nested at most this deep.</summary>
		public readonly int[] ZoneOpen = new int[8];
		public int ZoneDepth;

		/// <summary>Begins refused for want of a slot, so their ends do nothing and the profile counts them.</summary>
		public uint DroppedOpen;
		public uint ZonesDropped;

		/// <summary>When the recording began and ended on the CPU, in nanoseconds.</summary>
		public long CpuBegin;
		public long CpuEnd;

		/// <summary>Whether the last submission carried profiling data that has not been read back yet.</summary>
		public bool ProfilePending;

		//What the frame did, counted as it happens.
		public uint StatDraws;
		public uint StatDynamic;
		public uint StatSkinned;

		/// <summary>Copies drawn by instanced draws, shown beside the draw count.</summary>
		public uint StatInstances;
		public ulong StatTriangles;
		public uint StatSkippedBinds;

		/// <summary>How many compute passes this frame ran.</summary>
		public uint StatDispatches;

		/// <summary>And how many draws took their arguments from a buffer.</summary>
		public uint StatIndirect;

		//-- bound state cache, reset once per recording, to skip redundant binds

		public Pipeline LastPipeline;
		public ulong LastTexture;
		public ulong LastBoneOffset;
		public ulong LastMorphOffset;
		public bool FrameSetBound;
		public Viewport LastViewport;
		public bool ViewportSet;

		/// <summary>What the geometry bindings hold at offset nought; a dynamic draw nulls it.</summary>
		public VkBuffer LastVertexBuffer;
		public VkBuffer LastSkinBuffer;
		public VkBuffer LastIndexBuffer;
	}

	//The last finished frame of a viewport, measured: what the profiler hands out. Filled when a
	//fence proves the frame done, so what it says is always a frame or two old and never a stall.
	internal sealed class Profile
	{
		public bool Valid;

		public float GpuMs;
		public float CpuMs;

		public uint Draws;
		public uint DynamicDraws;
		public uint SkinnedDraws;

		/// <summary>How many copies the instanced draws drew between them.</summary>
		public uint Instances;
		public ulong Triangles;

		/// <summary>Triangles the frame handed the assembler - see VkDevice.CountsFrame; below nought where nothing counted.</summary>
		public long Counted = -1;
		public ulong ScratchUsed;
		public uint SkippedBinds;

		/// <summary>How many compute passes the frame ran.</summary>
		public uint Dispatches;

		/// <summary>And how many of its draws were argued for by a buffer.</summary>
		public uint Indirect;
		public uint ZonesDropped;

		public struct Zone
		{
			public fixed byte Name[VkDevice.ProfileNameSize];
			public float GpuMs;
			public float CpuMs;
			public int Depth;
		}

		public readonly Zone[] Zones = new Zone[VkDevice.ProfileMaxZones];
		public uint ZoneCount;
	}

	private static VkSwapchain.Swapchain Current => VkSwapchain.Current;

	private static VkSwapchain.Pictures Pics => VkSwapchain.Pics;

	internal static Frame CurrentFrame => VkSwapchain.CurrentFrame;

	//-- descriptor writing -------------------------------------------------

	//Points one set 1 at its bones and the frame's scratch: binding 1, camera and lights, is always the frame's;
	//binding 0 is too, or the matrices an indirect draw of instances binds
	private static void WriteFrameSet( Frame frame, DescriptorSet set, VkBuffer bones, ulong boneRange )
	{
		var vk = VkDevice.vk;
		var device = VkDevice.device;

		var buffers = stackalloc DescriptorBufferInfo[3];
		var writes = stackalloc WriteDescriptorSet[3];

		//A block wide for a pose or a filled run, the whole buffer for an indirect draw indexing by its start;
		//then one object's worth of blend shape weights from the same scratch
		var ranges = stackalloc ulong[3];
		ranges[0] = boneRange;
		ranges[1] = VkDevice.FrameBlockReach;
		ranges[2] = VkDevice.MorphBlockSize;

		var slots = stackalloc uint[3];
		slots[0] = 0;
		slots[1] = 1;
		slots[2] = 6;

		for ( var i = 0u; i < 3; ++i )
		{
			buffers[i].Buffer = i == 0 ? bones : frame.Scratch;
			buffers[i].Offset = 0;
			buffers[i].Range = ranges[i];

			writes[i].SType = StructureType.WriteDescriptorSet;
			writes[i].DstSet = set;
			writes[i].DstBinding = slots[i];
			writes[i].DescriptorCount = 1;
			writes[i].DescriptorType = DescriptorType.StorageBufferDynamic;
			writes[i].PBufferInfo = &buffers[i];
		}

		//And the lights. Always bound, the frame's own scratch where nothing made a buffer
		//yet: an empty descriptor is a fault on this driver the moment anything reads it.
		var lights = new DescriptorBufferInfo
		{
			Buffer = VkDevice.lightBuffer.Handle != 0 ? VkDevice.lightBuffer : frame.Scratch,
			Offset = 0,
			Range = Vk.WholeSize,
		};

		var lightWrite = new WriteDescriptorSet
		{
			SType = StructureType.WriteDescriptorSet,
			DstSet = set,
			DstBinding = 7,
			DescriptorCount = 1,
			DescriptorType = DescriptorType.StorageBuffer,
			PBufferInfo = &lights,
		};

		vk.UpdateDescriptorSets( device, 1, &lightWrite, 0, null );

		vk.UpdateDescriptorSets( device, 3, writes, 0, null );

		if ( VkDevice.Tier == RenderTier.Portable ) WritePortableSun( frame, set );
	}

	//The portable frame's shadow maps into a frame set - see VkDevice.PortableShadowMap; a texel of nothing
	//where there is none yet, which the frame block says is not whole, so it is never read
	private static void WritePortableSun( Frame frame, DescriptorSet set )
	{
		if ( VkDevice.portableNoShadow == 0 ) VkDevice.portableNoShadow = VkTextures.ShadowMapCreate( 1, 1, false );

		var maps = VkDevice.portableShadows;
		var bindings = VkDevice.PortableShadowBindings;

		var images = stackalloc DescriptorImageInfo[maps.Length];
		var writes = stackalloc WriteDescriptorSet[maps.Length];
		var count = 0u;

		for ( var i = 0; i < maps.Length; ++i )
		{
			var map = VkTextures.ShadowMapOf( maps[i] );
			var inPass = Current.MainPass || Current.TargetPass;

			//Every layer made readable before anything has drawn into it - see ReadyWhole - which a barrier inside a pass may not do
			if ( map is null || ( !map.Ready && inPass ) ) map = VkTextures.ShadowMapOf( VkDevice.portableNoShadow );
			if ( map is null || map.Whole.Handle == 0 || ( !map.Ready && inPass ) ) continue;

			ReadyWhole( frame, map );

			images[count] = new DescriptorImageInfo
			{
				Sampler = VkDevice.shadowSampler,
				ImageView = map.Whole,
				ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
			};

			writes[count] = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = bindings[i],
				DescriptorCount = 1,
				DescriptorType = DescriptorType.CombinedImageSampler,
				PImageInfo = &images[count],
			};

			count++;
		}

		if ( count > 0 ) VkDevice.vk.UpdateDescriptorSets( VkDevice.device, count, writes, 0, null );

		WritePortableSkyTable( set );
		WritePortableBake( set, 8, CoreBakedLighting.PortableSheet );
		WritePortableBake( set, 9, CoreBakedLighting.PortableVolume );
	}

	//One of the bake's pictures into a portable frame set; the sky table stands in for none, unread then
	private static void WritePortableBake( DescriptorSet set, uint binding, ulong texture )
	{
		if ( !VkTextures.ViewOf( texture, out var view ) && !VkTextures.PortableSkyTableView( out view ) ) return;

		var image = new DescriptorImageInfo
		{
			Sampler = VkDevice.clampSampler,
			ImageView = view,
			ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
		};

		var write = new WriteDescriptorSet
		{
			SType = StructureType.WriteDescriptorSet,
			DstSet = set,
			DstBinding = binding,
			DescriptorCount = 1,
			DescriptorType = DescriptorType.CombinedImageSampler,
			PImageInfo = &image,
		};

		VkDevice.vk.UpdateDescriptorSets( VkDevice.device, 1, &write, 0, null );
	}

	//Binds the sky table into a portable frame set; zero alpha tells the shader to use the sky colours
	private static void WritePortableSkyTable( DescriptorSet set )
	{
		if ( !VkTextures.PortableSkyTableView( out var view ) ) return;

		var image = new DescriptorImageInfo
		{
			Sampler = VkDevice.clampSampler,
			ImageView = view,
			ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
		};

		var write = new WriteDescriptorSet
		{
			SType = StructureType.WriteDescriptorSet,
			DstSet = set,
			DstBinding = 5,
			DescriptorCount = 1,
			DescriptorType = DescriptorType.CombinedImageSampler,
			PImageInfo = &image,
		};

		VkDevice.vk.UpdateDescriptorSets( VkDevice.device, 1, &write, 0, null );
	}

	//This frame's set 1.
	internal static void WriteFrameSet( Frame frame )
	{
		if ( frame.BoneSet.Handle != 0 ) WriteFrameSet( frame, frame.BoneSet, frame.Scratch, VkDevice.BoneBlockSize );
	}

	//-- layouts and barriers -----------------------------------------------

	//Moves an image between layouts with synchronisation2 barriers, which name both sides' stages
	internal static void Transition( CommandBuffer commands, VkImage image,
		ImageLayout from, ImageLayout to,
		PipelineStageFlags2 sourceStage, AccessFlags2 sourceAccess,
		PipelineStageFlags2 destinationStage, AccessFlags2 destinationAccess,
		ImageAspectFlags aspect = ImageAspectFlags.ColorBit,
		uint baseLevel = 0, uint levelCount = 1, uint layerCount = 1, uint baseLayer = 0 )
	{
		var barrier = new ImageMemoryBarrier2
		{
			SType = StructureType.ImageMemoryBarrier2,
			SrcStageMask = sourceStage,
			SrcAccessMask = sourceAccess,
			DstStageMask = destinationStage,
			DstAccessMask = destinationAccess,
			OldLayout = from,
			NewLayout = to,
			SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
			DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
			Image = image,
			SubresourceRange = new ImageSubresourceRange( aspect, baseLevel, levelCount, baseLayer, layerCount ),
		};

		var dependency = new DependencyInfo
		{
			SType = StructureType.DependencyInfo,
			ImageMemoryBarrierCount = 1,
			PImageMemoryBarriers = &barrier,
		};

		VkDevice.dialect.Barrier( commands, &dependency );
	}

	//Moves a frame picture (all layers) to a layout, tracking it; uses blunt stages on purpose
	internal static void MoveTo( CommandBuffer commands, VkImage image, ref ImageLayout tracked,
		ImageLayout wanted, ImageAspectFlags aspect = ImageAspectFlags.ColorBit, uint levels = 1 )
	{
		if ( image.Handle == 0 || tracked == wanted ) return;

		Transition( commands, image, tracked, wanted,
			PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit,
			PipelineStageFlags2.AllCommandsBit,
			AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
			aspect, 0, levels, Vk.RemainingArrayLayers );

		tracked = wanted;
	}

	//What must finish before an image in this layout is written - only the shadow atlases' three; UNDEFINED
	//throws the contents away, so nothing must, which keeps the cheap path cheap
	private static void WhatUses( ImageLayout layout, out PipelineStageFlags2 stage, out AccessFlags2 access )
	{
		switch ( layout )
		{
			case ImageLayout.DepthAttachmentOptimal:
				stage = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
				access = AccessFlags2.DepthStencilAttachmentWriteBit;
				return;

			case ImageLayout.ShaderReadOnlyOptimal:
				stage = PipelineStageFlags2.FragmentShaderBit;
				access = AccessFlags2.ShaderSampledReadBit;
				return;

			case ImageLayout.TransferSrcOptimal:
				stage = PipelineStageFlags2.CopyBit;
				access = AccessFlags2.TransferReadBit;
				return;

			case ImageLayout.TransferDstOptimal:
				stage = PipelineStageFlags2.CopyBit;
				access = AccessFlags2.TransferWriteBit;
				return;

			default:
				stage = PipelineStageFlags2.TopOfPipeBit;
				access = 0;
				return;
		}
	}

	//Everything a viewport's pictures forget between frames. Nothing the world is drawn into carries over, so wherever
	//those were left is not worth preserving - and saying so lets the transitions into them discard rather than keep.
	internal static void ResetPictures( VkSwapchain.Pictures p )
	{
		p.SceneDrawn = false;
		p.ScreenDepthFilled = false;
		p.DepthFilled = false;
		p.SceneLayout = ImageLayout.Undefined;
		p.NormalLayout = ImageLayout.Undefined;
		p.VelocityLayout = ImageLayout.Undefined;
		p.VelocityReady = false;

		//Not the other picture: a lightmap bake fills it over frames and reads it last, and a reset to UNDEFINED
		//would let the next transition discard it - p.PostLayout is kept on purpose
		p.DepthLayout = ImageLayout.Undefined;
		p.ScreenDepthLayout = ImageLayout.Undefined;

		p.AsideOn = false;
		p.AsideBegun = false;
	}

	//Puts our own frame picture into SHADER_READ_ONLY before a pass samples it; foreign textures skip
	private static void ReadyPictureForRead( Frame frame, ulong texture )
	{
		if ( texture == 0 ) return;

		//A depth pyramid not built yet: out of UNDEFINED into its set's layout once - see VkTextures.PyramidUnready
		if ( VkTextures.PyramidUnready( texture, out var pyramidImage, out var pyramidMips ) )
		{
			Transition( frame.Commands, pyramidImage, ImageLayout.Undefined, ImageLayout.General,
				PipelineStageFlags2.AllCommandsBit, 0,
				PipelineStageFlags2.AllCommandsBit, AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit,
				ImageAspectFlags.ColorBit, 0, pyramidMips );

			VkTextures.PyramidReady( texture );
			return;
		}

		var p = Pics;

		if ( texture == p.DepthHandle )
		{
			MoveTo( frame.Commands, p.DepthImage, ref p.DepthLayout,
				ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.DepthBit );
			return;
		}

		if ( texture == p.SceneHandle )
		{
			MoveTo( frame.Commands, p.SceneImage, ref p.SceneLayout, ImageLayout.ShaderReadOnlyOptimal );
			ReadyMips( frame, p.SceneImage, p.SceneLevels, ref p.SceneMipsReady );
			return;
		}

		if ( texture == p.NormalHandle )
		{
			MoveTo( frame.Commands, p.NormalImage, ref p.NormalLayout, ImageLayout.ShaderReadOnlyOptimal );
			return;
		}

		if ( texture == p.VelocityHandle )
		{
			MoveTo( frame.Commands, p.VelocityImage, ref p.VelocityLayout, ImageLayout.ShaderReadOnlyOptimal );
			return;
		}

		if ( texture == p.PostHandle && p.PostImage.Handle != 0 )
		{
			MoveTo( frame.Commands, p.PostImage, ref p.PostLayout, ImageLayout.ShaderReadOnlyOptimal );
			ReadyMips( frame, p.PostImage, p.PostLevels, ref p.PostMipsReady );
			return;
		}

		for ( var which = 0; which < 2; ++which )
		{
			if ( texture != p.DisplayHandle[which] || p.DisplayImage[which].Handle == 0 ) continue;

			MoveTo( frame.Commands, p.DisplayImage[which], ref p.DisplayLayout[which], ImageLayout.ShaderReadOnlyOptimal );
			ReadyMips( frame, p.DisplayImage[which], p.DisplayLevels, ref p.DisplayMipsReady[which] );
			return;
		}
	}

	//Moves a picture's lower mips to the descriptor's layout once, since validation checks every level
	private static void ReadyMips( Frame frame, VkImage image, uint levels, ref bool ready )
	{
		if ( ready || levels <= 1 || image.Handle == 0 ) return;

		Transition( frame.Commands, image,
			ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal,
			PipelineStageFlags2.AllCommandsBit, 0,
			PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
			ImageAspectFlags.ColorBit, 1, levels - 1 );

		ready = true;
	}


	//Copies world depth into the screen's with nearest filtering, so overlays stay occluded by the world
	private static void LiftDepthToScreen( Frame frame )
	{
		var p = Pics;

		if ( p.ScreenDepthImage.Handle == 0 ) return;

		//Nothing drew the world this frame, so there is no shape to lift and the screen's depth is cleared by its own pass.
		if ( !p.SceneDrawn && !p.DepthFilled ) return;

		//Or the pre-pass wrote it here, which this copy exists to avoid - see Swapchain.DepthOnScreen
		if ( p.ScreenDepthFilled ) return;


		MoveTo( frame.Commands, p.DepthImage, ref p.DepthLayout,
			ImageLayout.TransferSrcOptimal, ImageAspectFlags.DepthBit );

		MoveTo( frame.Commands, p.ScreenDepthImage, ref p.ScreenDepthLayout,
			ImageLayout.TransferDstOptimal, ImageAspectFlags.DepthBit );

		var region = new ImageBlit
		{
			SrcSubresource = new ImageSubresourceLayers( ImageAspectFlags.DepthBit, 0, 0, 1 ),
			DstSubresource = new ImageSubresourceLayers( ImageAspectFlags.DepthBit, 0, 0, 1 ),
		};

		region.SrcOffsets[1] = new Offset3D( (int)p.Extent.Width, (int)p.Extent.Height, 1 );

		if ( Current.WorldWanted.Width > 0 )
		{
			//Part of the screen where the world was put, stretched where it is drawn smaller - see VkSwapchain.Scaled
			var area = VkSwapchain.WorldArea( Current, p );
			region.DstOffsets[0] = new Offset3D( Current.WorldX, Current.WorldY, 0 );
			region.DstOffsets[1] = new Offset3D( Current.WorldX + (int)area.Width,
				Current.WorldY + (int)area.Height, 1 );
		}
		else
		{
			region.DstOffsets[1] = new Offset3D( (int)p.ScreenExtent.Width, (int)p.ScreenExtent.Height, 1 );
		}

		//Own zone so its cost isn't charged to whichever draw reopened the screen pass
		ZoneBegin( "depth lift" );

		VkDevice.vk.CmdBlitImage( frame.Commands,
			p.DepthImage, ImageLayout.TransferSrcOptimal,
			p.ScreenDepthImage, ImageLayout.TransferDstOptimal,
			1, &region, Filter.Nearest );

		ZoneEnd();
	}

	//Begins the world's pass if not begun; every draw calls this, one branch when open, so a shadow pass
	//can be recorded before the world without the caller saying so twice
	internal static void EnsureMainPass()
	{
		if ( Current.MainPass ) return;

		//Opening the pass is the one thing in a draw not per draw - see DrawTrace, where it is broken out
		var traced = DrawTrace.Begin();

		var frame = CurrentFrame;

		//The world's shape, at the screen's size, for whatever is drawn over the world. Does nothing where the two
		//are the same picture. Again after the pass was interrupted for the world to be drawn - see ScreenInterrupt.
		if ( !Current.MainBegun || Current.LiftPending ) LiftDepthToScreen( frame );

		Current.LiftPending = false;

		var p = Pics;

		//The depth this pass tests against: the world's own at screen size, else its copy
		var scaled = p.ScreenDepthImage.Handle != 0;

		var passDepthImage = scaled ? p.ScreenDepthImage : p.DepthImage;

		//Whatever the depth was last used for, the screen writes it again.
		if ( scaled )
		{
			MoveTo( frame.Commands, passDepthImage, ref p.ScreenDepthLayout,
				ImageLayout.DepthAttachmentOptimal, ImageAspectFlags.DepthBit );
		}
		else
		{
			MoveTo( frame.Commands, passDepthImage, ref p.DepthLayout,
				ImageLayout.DepthAttachmentOptimal, ImageAspectFlags.DepthBit );
		}

		//The screen of this viewport: the window's next image or the view's output; all else is the same,
		//which is why a view is a viewport and not a special case
		var view = VkSwapchain.currentView;

		//Or the picture the world is drawn aside into, of the screen's own format - see WorldAside
		var aside = p.AsideOn ? VkPictures.Get( p.Aside ) : null;

		if ( aside is not null )
		{
			LevelTo( frame.Commands, aside, 0, ImageLayout.ColorAttachmentOptimal );
		}
		else if ( view is not null )
		{
			MoveTo( frame.Commands, view.OutputImage, ref view.OutputLayout, ImageLayout.ColorAttachmentOptimal );
		}

		var colour = new RenderingAttachmentInfo
		{
			SType = StructureType.RenderingAttachmentInfo,
			ImageView = aside is not null ? aside.Level[0] : view is not null ? view.OutputView : Current.Views[Current.ImageIndex],
			ImageLayout = ImageLayout.ColorAttachmentOptimal,

			//A pass that was interrupted to take a copy resumes where it left off; clearing again would throw away the world it just drew.
			LoadOp = ( aside is not null ? p.AsideBegun : Current.MainBegun ) ? AttachmentLoadOp.Load : AttachmentLoadOp.Clear,
			StoreOp = AttachmentStoreOp.Store,
		};

		if ( aside is not null ) p.AsideBegun = true;

		colour.ClearValue.Color = new ClearColorValue( Current.Clear[0], Current.Clear[1], Current.Clear[2], Current.Clear[3] );

		var screenFormat = aside?.Format ?? ( view is not null ? view.OutputFormat : Current.Format );
		var samples = MainSamples( p, scaled );

		if ( samples > 1 && !VkSwapchain.EnsureSampled( p, screenFormat, samples ) ) samples = 1;

		Current.MainSamples = samples;
		LastMainSamples = samples;

		//Always attached, whether or not anything drawn this frame tests against it: dynamic rendering demands that
		//pipelines and the pass agree about the attachments, and one honest arrangement beats two to keep in step.
		var depth = new RenderingAttachmentInfo
		{
			SType = StructureType.RenderingAttachmentInfo,
			ImageView = scaled ? p.ScreenDepthView : p.DepthView,
			ImageLayout = ImageLayout.DepthAttachmentOptimal,

			//Keep depth if the world or the pre-pass already filled it; clearing would erase the world
			LoadOp = Current.MainBegun || p.SceneDrawn || p.DepthFilled
				? AttachmentLoadOp.Load
				: AttachmentLoadOp.Clear,

			//Store depth only if the pass may resume; portable frames drop it to save tile bandwidth
			StoreOp = Current.MainBegun || Current.ResumedLastFrame || VkDevice.Tier != RenderTier.Portable
				? AttachmentStoreOp.Store
				: AttachmentStoreOp.DontCare,
		};

		if ( Current.MainBegun ) Current.ResumedThisFrame = true;

		depth.ClearValue.DepthStencil = new ClearDepthStencilValue( 1.0f, 0 );

		//Drawn into the multisampled pair and resolved as the pass ends: colour into what it was going to be, depth into the depth.
		//The pair is kept only where the pass may be resumed; the resolves always are
		if ( samples > 1 )
		{
			if ( !p.SampledReady )
			{
				var colourLayout = ImageLayout.Undefined;
				var depthLayout = ImageLayout.Undefined;

				MoveTo( frame.Commands, p.SampledColourImage, ref colourLayout, ImageLayout.ColorAttachmentOptimal, ImageAspectFlags.ColorBit );
				MoveTo( frame.Commands, p.SampledDepthImage, ref depthLayout, ImageLayout.DepthAttachmentOptimal, ImageAspectFlags.DepthBit );

				p.SampledReady = true;
			}

			var kept = Current.MainBegun || Current.ResumedLastFrame ? AttachmentStoreOp.Store : AttachmentStoreOp.DontCare;

			colour.ResolveMode = ResolveModeFlags.AverageBit;
			colour.ResolveImageView = colour.ImageView;
			colour.ResolveImageLayout = ImageLayout.ColorAttachmentOptimal;
			colour.ImageView = p.SampledColourView;
			colour.StoreOp = kept;

			depth.ResolveMode = ResolveModeFlags.SampleZeroBit;
			depth.ResolveImageView = depth.ImageView;
			depth.ResolveImageLayout = ImageLayout.DepthAttachmentOptimal;
			depth.ImageView = p.SampledDepthView;
			depth.StoreOp = kept;
		}

		//Every eye at once, where the view holds more than one - see ViewMask.
		var viewMask = ViewMask;

		var rendering = new RenderingInfo
		{
			SType = StructureType.RenderingInfo,

			//The screen's size, not the world's: what this pass writes is the window.
			RenderArea = new Rect2D( default, p.ScreenExtent ),
			LayerCount = 1,
			ViewMask = viewMask,
			ColorAttachmentCount = 1,
			PColorAttachments = &colour,
			PDepthAttachment = &depth,
		};

		var formats = VkPassFormats.Of( screenFormat, VkDevice.DepthFormat );
		formats.ViewMask = viewMask;
		formats.Samples = samples;

		//Where the world goes straight to the screen - the portable frame - this is its pass, foveated per eye;
		//the full frame foveated already, and what goes here, the compose, is seen whole
		RenderingFragmentDensityMapAttachmentInfoEXT foveaDensity;
		RenderingFragmentShadingRateAttachmentInfoKHR foveaRate;

		//And the world shaded straight onto a window or a view, coarser where its last picture was smooth - see
		//VkFrame.Adaptive. Only while the pass is the world's: one begun for the panels behind it goes without.
		if ( view is not null && VkDevice.Tier != RenderTier.Full )
			VkFovea.Attach( Fovea( frame, p, bothEyes: true ), &rendering, &foveaDensity, &foveaRate );
		else if ( VkFovea.Adaptive && Current.WorldOnScreen )
			VkFovea.Attach( Fovea( frame, p, bothEyes: false ), &rendering, &foveaDensity, &foveaRate );

		VkDevice.dialect.BeginRendering( frame.Commands, &rendering, formats );

		Current.MainPass = true;
		Current.MainBegun = true;

		//Multiview pipelines don't inherit earlier binds, so clear the bind cache as the pass opens
		if ( viewMask != 0 )
		{
			frame.LastPipeline = default;
			frame.LastTexture = 0;
			frame.FrameSetBound = false;
		}

		DrawTrace.Phase( DrawTrace.Kind.MainPass, traced );
	}

	/// <summary>Samples the last screen's pass opened took, for the report.</summary>
	internal static uint LastMainSamples = 1;

	//Samples the screen's pass takes: the player's anti-aliasing where the portable frame resolves it in the pass, over a depth
	//the pass clears itself; said at the frame's first opening, as a pass resumed has to be the one it was
	private static uint MainSamples( VkSwapchain.Pictures p, bool scaled )
	{
		if ( Current.MainBegun ) return Current.MainSamples;
		if ( VkDevice.mostSamples <= 1 || VkDevice.Tier != RenderTier.Portable || scaled || p.SceneDrawn || p.DepthFilled ) return 1;

		var asked = Rinten.Rendering.DrawSettings.Msaa;
		if ( asked < 2 ) return 1;

		return Math.Min( 1u << System.Numerics.BitOperations.Log2( (uint)asked ), VkDevice.mostSamples );
	}

	//Fovea map for this view's world passes, or null; only when the world is drawn at screen size
	private static VkFovea.Map Fovea( Frame frame, VkSwapchain.Pictures p, bool bothEyes )
	{
		if ( VkFovea.Kind == VkFovea.Kinds.None ) return null;

		//Without a headset, the map the last frame's picture laid - see VkFrame.Adaptive.
		if ( VkFovea.Adaptive && AdaptiveMap( p ) is null ) return null;

		//A world of sky or background alone shades too little to pay for reading its picture; the map starts
		//from every pixel's rate when surfaces come back, not from what a picture long gone said
		if ( VkFovea.Adaptive && !Current.WorldShades )
		{
			p.Fovea.Cleared = false;
			return null;
		}

		if ( p?.Fovea is null ) return null;

		//An adaptive map takes the world at its own size, smaller than the window under an upscaler - see LayRates
		if ( !p.Fovea.Adaptive && ( p.Extent.Width != p.ScreenExtent.Width || p.Extent.Height != p.ScreenExtent.Height ) ) return null;

		var layers = VkSwapchain.currentView?.Layers ?? 1u;
		if ( layers > 1 && !bothEyes ) return null;

		var map = VkFovea.Prepare( frame, p, p.Extent, layers );

		if ( map is not null )
		{
			map.DrawnWith = true;
			map.Back = AdaptiveBack;
		}

		return map;
	}

	//The layers the screen's pass draws at once, a bit each: both of a headset's eyes where the open
	//view holds two, and nought - the one picture - for everything else. See VkDevice.multiview.
	private static uint ViewMask
		=> VkSwapchain.currentView is { Layers: > 1 } layered ? ( 1u << (int)layered.Layers ) - 1 : 0;

	//-- the bind cache -----------------------------------------------------

	//Sets viewport and scissor per draw, but only issues the call when they changed
	private static void SetViewport( Frame frame )
	{
		Viewport viewport = default;
		Rect2D scissor = default;

		if ( Current.TargetPass && Current.Target == VkDevice.TargetShadow )
		{
			//One bake's rectangle of the layer only: the rest holds pages still right
			viewport.X = Current.ShadowArea.Offset.X;
			viewport.Y = Current.ShadowArea.Offset.Y;
			viewport.Width = Current.ShadowArea.Extent.Width;
			viewport.Height = Current.ShadowArea.Extent.Height;
			scissor = Current.ShadowScissor;
		}
		else if ( Current.TargetPass && Current.Target == VkDevice.TargetDepth &&
			VkSwapchain.DepthGoesToScreen )
		{
			//The depth into the screen's own buffer, at the place the world sits in the window - the same viewport
			//the pass that reads it uses, which is the whole point of writing it there. See Swapchain.DepthOnScreen.
			viewport.X = Current.WorldX;
			viewport.Y = Current.WorldY;
			viewport.Width = Pics.Extent.Width;
			viewport.Height = Pics.Extent.Height;
			scissor.Offset = new Offset2D( Current.WorldX, Current.WorldY );
			scissor.Extent = Pics.Extent;
		}
		else if ( Current.TargetPass )
		{
			//The whole picture being filled at its pass's size - the world's, the window's, a frame picture's or the
			//sky table's; a smaller one through the world's viewport would be its corner stretched
			viewport.Width = Current.TargetExtent.Width;
			viewport.Height = Current.TargetExtent.Height;
			scissor.Extent = Current.TargetExtent;
		}
		else if ( Current.WorldWanted.Width > 0 && Current.WorldOnScreen )
		{
			//The world on part of the screen, where the window said, only while drawing the world (WorldOnScreen);
			//the area, not the world's pictures, which are smaller below render scale one
			var area = VkSwapchain.WorldArea( Current, Pics );
			viewport.X = Current.WorldX;
			viewport.Y = Current.WorldY;
			viewport.Width = area.Width;
			viewport.Height = area.Height;
			scissor.Offset = new Offset2D( Current.WorldX, Current.WorldY );
			scissor.Extent = area;
		}
		else
		{
			//The screen, which never is.
			viewport.Width = Pics.ScreenExtent.Width;
			viewport.Height = Pics.ScreenExtent.Height;
			scissor.Extent = Pics.ScreenExtent;
		}

		viewport.MaxDepth = Current.DepthCeiling;

		//The scissor always matches the viewport here, so comparing one is comparing both.
		if ( frame.ViewportSet &&
			viewport.X == frame.LastViewport.X && viewport.Y == frame.LastViewport.Y &&
			viewport.Width == frame.LastViewport.Width && viewport.Height == frame.LastViewport.Height &&
			viewport.MaxDepth == frame.LastViewport.MaxDepth )
		{
			frame.StatSkippedBinds++;
			return;
		}

		VkDevice.vk.CmdSetViewport( frame.Commands, 0, 1, &viewport );
		VkDevice.vk.CmdSetScissor( frame.Commands, 0, 1, &scissor );

		frame.LastViewport = viewport;
		frame.ViewportSet = true;
	}

	//Binds a pipeline, unless it is the one already bound - which in a queue sorted by pipeline is most draws.
	private static void BindPipeline( Frame frame, Pipeline pipeline )
	{
		//A pass drawing both eyes, or multisampled, uses the pipeline remade for it - see VkPipelines.Variant
		var main = Current.MainPass && !Current.TargetPass;
		var mask = main ? ViewMask : 0;
		var samples = main ? Current.MainSamples : 1;

		if ( mask != 0 || samples > 1 ) pipeline = VkPipelines.Variant( pipeline, mask, samples );

		if ( frame.LastPipeline.Handle == pipeline.Handle )
		{
			frame.StatSkippedBinds++;
			return;
		}

		VkDevice.vk.CmdBindPipeline( frame.Commands, PipelineBindPoint.Graphics, pipeline );
		frame.LastPipeline = pipeline;
	}

	//Binds a texture's set unless bound; every layout is the same, so a bound set stays compatible
	private static void BindTexture( Frame frame, PipelineLayout layout, ulong texture )
	{
		if ( texture == 0 || layout.Handle == 0 ) return;

		if ( frame.LastTexture == texture )
		{
			frame.StatSkippedBinds++;
			return;
		}

		var set = VkTextures.SetOf( texture );
		if ( set.Handle == 0 ) return;

		VkDevice.vk.CmdBindDescriptorSets( frame.Commands, PipelineBindPoint.Graphics, layout, 0, 1, &set, 0, null );

		frame.LastTexture = texture;
	}

	//Binds set 3, every picture, once per pipeline change: the set never changes, only its contents,
	//and writing while bound is legal as the pool allows it
	private static void BindBindless( Frame frame, PipelineLayout layout )
	{
		if ( !VkDevice.bindless || VkDevice.bindlessSet.Handle == 0 ) return;

		var set = VkDevice.bindlessSet;

		VkDevice.vk.CmdBindDescriptorSets( frame.Commands, PipelineBindPoint.Graphics, layout, 3, 1, &set, 0, null );
	}

	//Binds set 1 for a draw: its bones, its blend shape weights, and the frame's camera
	//and lights. Skipped when the offsets have not moved - only skinned draws move them.
	private static void BindFrameSet( Frame frame, PipelineLayout layout, ulong boneOffset, ulong morphOffset = 0 )
	{
		//The portable frame's shadow maps changed since its set was written - another view's drawer planned, or a map was made:
		//a set of its own from here, as one already bound in this recording may not be written again
		if ( VkDevice.portableShadowsMoved && frame.BoneSet.Handle != 0 )
		{
			VkDevice.portableShadowsMoved = false;

			if ( VkDevice.AllocateDescriptorSet( VkDevice.boneSetLayout, out var replacement ) )
			{
				VkDevice.RetireDescriptorSet( frame.BoneSet );
				frame.BoneSet = replacement;
				WriteFrameSet( frame );
				frame.FrameSetBound = false;
			}
		}

		if ( frame.FrameSetBound && frame.LastBoneOffset == boneOffset && frame.LastMorphOffset == morphOffset )
		{
			frame.StatSkippedBinds++;
			return;
		}

		var offsets = stackalloc uint[3];
		offsets[0] = (uint)boneOffset;
		offsets[1] = (uint)frame.FrameBlock;
		offsets[2] = (uint)morphOffset;

		var set = frame.BoneSet;

		VkDevice.vk.CmdBindDescriptorSets( frame.Commands, PipelineBindPoint.Graphics, layout, 1, 1, &set, 3, offsets );

		frame.FrameSetBound = true;
		frame.LastBoneOffset = boneOffset;
		frame.LastMorphOffset = morphOffset;

		//World shaders now index the picture table for probe GI, so the set has to be bound on every mesh draw, not only the pulled voxel ones.
		BindBindless( frame, layout );
	}

	//The push constants of a draw, clamped to the declared range and to whole words - Vulkan wants multiples of
	//four, and a caller a byte over the range is a caller with a struct mismatch, not a reason to abort the draw.
	private static void PushConstants( Frame frame, PipelineLayout layout, ShaderStageFlags stages,
		void* data, int size )
	{
		if ( data is null || size <= 0 || layout.Handle == 0 ) return;

		var take = (uint)size & ~3u;
		if ( take > VkDevice.PushConstantSize ) take = VkDevice.PushConstantSize;

		if ( take == 0 ) return;

		VkDevice.vk.CmdPushConstants( frame.Commands, layout, stages, 0, take, data );
	}

	//-- the frame's scratch ------------------------------------------------

	internal static bool EnsureScratch( Frame frame, ulong additional )
	{
		//What this frame came to want, so the next one can be given it before it records anything -
		//see BeginRecording. Kept here because every allocation of scratch comes through this door.
		var wanted = frame.ScratchUsed + additional;
		if ( wanted > frame.ScratchPeak ) frame.ScratchPeak = wanted;

		if ( wanted <= frame.ScratchCapacity ) return true;

		var vk = VkDevice.vk;
		var device = VkDevice.device;

		//Doubles from a 64 KB floor, carrying over this frame's earlier contents
		var capacity = frame.ScratchCapacity < 65536 ? 65536ul : frame.ScratchCapacity * 2;
		while ( capacity < wanted ) capacity *= 2;

		var info = new BufferCreateInfo
		{
			SType = StructureType.BufferCreateInfo,
			Size = capacity,
			Usage = BufferUsageFlags.VertexBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
			SharingMode = SharingMode.Exclusive,
		};

		VkBuffer buffer;
		DeviceMemory memory = default;
		void* mapped = null;

		if ( vk.CreateBuffer( device, &info, null, &buffer ) != Result.Success ) return false;

		MemoryRequirements requirements;
		vk.GetBufferMemoryRequirements( device, buffer, &requirements );

		var allocate = new MemoryAllocateInfo
		{
			SType = StructureType.MemoryAllocateInfo,
			AllocationSize = requirements.Size,

			//Prefer host-visible device-local memory: CPU writes once, GPU reads it every draw
			MemoryTypeIndex = VkDevice.FindMemoryType( requirements.MemoryTypeBits,
				MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit |
				MemoryPropertyFlags.HostCoherentBit ),
		};

		if ( allocate.MemoryTypeIndex != uint.MaxValue &&
			vk.AllocateMemory( device, &allocate, null, &memory ) != Result.Success )
		{
			//The window it lives in is a few hundred megabytes on some cards and is shared with
			//whatever else wanted it. Falling back to system memory is slower and is still a frame.
			memory = default;
			allocate.MemoryTypeIndex = uint.MaxValue;
		}

		if ( allocate.MemoryTypeIndex == uint.MaxValue )
		{
			allocate.MemoryTypeIndex = VkDevice.FindMemoryType( requirements.MemoryTypeBits,
				MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit );
		}

		if ( allocate.MemoryTypeIndex == uint.MaxValue ||
			( memory.Handle == 0 && vk.AllocateMemory( device, &allocate, null, &memory ) != Result.Success ) ||
			vk.BindBufferMemory( device, buffer, memory, 0 ) != Result.Success ||
			vk.MapMemory( device, memory, 0, Vk.WholeSize, 0, &mapped ) != Result.Success )
		{
			if ( memory.Handle != 0 ) vk.FreeMemory( device, memory, null );
			vk.DestroyBuffer( device, buffer, null );
			return false;
		}

		//Copy from the old write-combined buffer; slow but rare, as frames start at last frame's peak
		if ( frame.ScratchUsed > 0 && frame.ScratchMapped is not null )
			System.Buffer.MemoryCopy( frame.ScratchMapped, mapped, capacity, frame.ScratchUsed );

		var used = frame.ScratchUsed;

		//The old buffer retires rather than dying: draws recorded moments ago in this very frame still point at it.
		if ( frame.Scratch.Handle != 0 )
		{
			vk.UnmapMemory( device, frame.ScratchMemory );
			frame.RetiredScratch.Add( ( frame.Scratch, frame.ScratchMemory ) );
		}

		frame.Scratch = buffer;
		frame.ScratchMemory = memory;
		frame.ScratchMapped = (byte*)mapped;
		frame.ScratchCapacity = capacity;

		frame.ScratchUsed = used;

		//Point a new set at the grown buffer; the old set may still be held by this recording
		if ( frame.BoneSet.Handle != 0 )
		{
			//A pool with nothing left is not a reason to lose the frame: writing the old set is what this did before and it
			//draws, whatever validation makes of it. It takes a device out of memory to get here now that the pools grow.
			if ( VkDevice.AllocateDescriptorSet( VkDevice.boneSetLayout, out var replacement ) )
			{
				VkDevice.RetireDescriptorSet( frame.BoneSet );

				frame.BoneSet = replacement;
				frame.FrameSetBound = false;
			}

			WriteFrameSet( frame );
		}

		return true;
	}

	//Writes a pose into scratch (fixed window, identity-padded) and returns its offset
	private static bool PutBones( Frame frame, float* bones, int boneCount, out ulong where )
	{
		where = 0;

		var alignment = (ulong)VkDevice.storageAlignment;
		var aligned = ( frame.ScratchUsed + alignment - 1 ) / alignment * alignment;
		var padding = aligned - frame.ScratchUsed;

		if ( !EnsureScratch( frame, padding + VkDevice.BoneBlockSize ) ) return false;

		var used = (ulong)boneCount * 64;

		var into = frame.ScratchMapped + aligned;

		System.Buffer.MemoryCopy( bones, into, VkDevice.BoneBlockSize, used );
		System.Runtime.InteropServices.NativeMemory.Clear( into + used, (nuint)( VkDevice.BoneBlockSize - used ) );

		for ( var i = (uint)boneCount; i < VkDevice.MaxBones; ++i )
		{
			var m = (float*)( into + ( (ulong)i * 64 ) );

			m[0] = 1.0f;
			m[5] = 1.0f;
			m[10] = 1.0f;
			m[15] = 1.0f;
		}

		frame.ScratchUsed = aligned + VkDevice.BoneBlockSize;
		where = aligned;

		return true;
	}

	//Writes blend shape weights into scratch as a zero-padded fixed block and returns its offset
	private static bool PutMorphWeights( Frame frame, float* weights, int count, out ulong where )
	{
		where = 0;

		var alignment = (ulong)VkDevice.storageAlignment;
		var aligned = ( frame.ScratchUsed + alignment - 1 ) / alignment * alignment;
		var padding = aligned - frame.ScratchUsed;

		if ( !EnsureScratch( frame, padding + VkDevice.MorphBlockSize ) ) return false;

		var used = (ulong)count * 4;

		var into = frame.ScratchMapped + aligned;

		System.Buffer.MemoryCopy( weights, into, VkDevice.MorphBlockSize, used );
		System.Runtime.InteropServices.NativeMemory.Clear( into + used, (nuint)( VkDevice.MorphBlockSize - used ) );

		frame.ScratchUsed = aligned + VkDevice.MorphBlockSize;
		where = aligned;

		return true;
	}

	private static void DestroyRetiredScratch( Frame frame )
	{
		for ( var i = 0; i < frame.RetiredScratch.Count; ++i )
		{
			VkDevice.vk.DestroyBuffer( VkDevice.device, frame.RetiredScratch[i].Buffer, null );
			VkDevice.vk.FreeMemory( VkDevice.device, frame.RetiredScratch[i].Memory, null );
		}

		frame.RetiredScratch.Clear();
	}

	private static void DestroyScratch( Frame frame )
	{
		DestroyRetiredScratch( frame );

		if ( frame.ScratchMemory.Handle != 0 ) VkDevice.vk.UnmapMemory( VkDevice.device, frame.ScratchMemory );
		if ( frame.Scratch.Handle != 0 ) VkDevice.vk.DestroyBuffer( VkDevice.device, frame.Scratch, null );
		if ( frame.ScratchMemory.Handle != 0 ) VkDevice.vk.FreeMemory( VkDevice.device, frame.ScratchMemory, null );

		frame.Scratch = default;
		frame.ScratchMemory = default;
		frame.ScratchMapped = null;
		frame.ScratchCapacity = 0;
		frame.ScratchUsed = 0;
	}

	//-- making and unmaking a frame ----------------------------------------

	internal static void UnmakeFrame( Frame frame )
	{
		var vk = VkDevice.vk;
		var device = VkDevice.device;

		//Wait for in-flight work first: what follows is destroyed outright, not retired
		if ( frame.Submitted && frame.Finished.Handle != 0 && !VkDevice.Lost )
		{
			var pending = frame.Finished;

			if ( vk.WaitForFences( device, 1, &pending, true, ulong.MaxValue ) == Result.Success )
				VkDevice.TicketDone( frame.Ticket );
			else
				VkDevice.MarkLostIf( Result.ErrorDeviceLost );
		}

		frame.Submitted = false;

		DestroyScratch( frame );
		DestroyPosed( frame );

		if ( frame.Queries.Handle != 0 ) vk.DestroyQueryPool( device, frame.Queries, null );
		if ( frame.Stats.Handle != 0 ) vk.DestroyQueryPool( device, frame.Stats, null );

		if ( frame.BoneSet.Handle != 0 ) VkDevice.FreeDescriptorSet( frame.BoneSet );

		foreach ( var pair in frame.Readbacks )
		{
			if ( pair.Value.Mapped is not null ) vk.UnmapMemory( device, pair.Value.Memory );
			if ( pair.Value.Staging.Handle != 0 ) vk.DestroyBuffer( device, pair.Value.Staging, null );
			if ( pair.Value.Memory.Handle != 0 ) vk.FreeMemory( device, pair.Value.Memory, null );
		}

		frame.Readbacks.Clear();

		if ( frame.Finished.Handle != 0 ) vk.DestroyFence( device, frame.Finished, null );
		if ( frame.ImageAvailable.Handle != 0 ) vk.DestroySemaphore( device, frame.ImageAvailable, null );
		if ( frame.GeneratedAvailable.Handle != 0 ) vk.DestroySemaphore( device, frame.GeneratedAvailable, null );

		//The pool owns the command buffer; freeing it separately is allowed but pointless.
		if ( frame.Pool.Handle != 0 ) vk.DestroyCommandPool( device, frame.Pool, null );

		Forget( frame );
	}

	private static void Forget( Frame frame )
	{
		frame.Pool = default;
		frame.Commands = default;
		frame.ImageAvailable = default;
		frame.GeneratedAvailable = default;
		frame.Finished = default;
		frame.Submitted = false;
		frame.FrameSetStale = false;
		frame.BoneSet = default;
		frame.FrameBlock = 0;
		frame.NullBones = 0;
		frame.Ticket = 0;
		frame.Queries = default;
		frame.QueryNext = 0;
		frame.ZoneCount = 0;
		frame.ZoneDepth = 0;
		frame.DroppedOpen = 0;
		frame.ZonesDropped = 0;
		frame.CpuBegin = 0;
		frame.CpuEnd = 0;
		frame.ProfilePending = false;
		frame.StatDraws = 0;
		frame.StatDynamic = 0;
		frame.StatSkinned = 0;
		frame.StatInstances = 0;
		frame.StatTriangles = 0;
		frame.StatSkippedBinds = 0;
		frame.StatDispatches = 0;
		frame.StatIndirect = 0;
		frame.LastPipeline = default;
		frame.LastTexture = 0;
		frame.LastBoneOffset = 0;
		frame.LastMorphOffset = 0;
		frame.FrameSetBound = false;
		frame.LastViewport = default;
		frame.ViewportSet = false;
		frame.LastVertexBuffer = default;
		frame.LastSkinBuffer = default;
		frame.LastIndexBuffer = default;
		frame.ScratchPeak = 0;
	}

	internal static void DestroyFrames( VkSwapchain.Swapchain one )
	{
		for ( var i = 0; i < one.Frames.Length; ++i ) UnmakeFrame( one.Frames[i] );
	}

	//One frame: a command buffer, a fence for when it is done, and the set every draw binds; a semaphore
	//only where it waits on a swapchain image, which a view's frame has none of
	internal static bool MakeFrame( Frame frame, bool presents, ulong which )
	{
		var vk = VkDevice.vk;
		var device = VkDevice.device;

		var poolInfo = new CommandPoolCreateInfo
		{
			SType = StructureType.CommandPoolCreateInfo,

			//One pool per frame, reset whole rather than per buffer: resetting a pool hands its memory back in
			//one go, which is both faster and the only way to avoid the pool growing for the life of the process.
			Flags = CommandPoolCreateFlags.TransientBit,
			QueueFamilyIndex = VkDevice.graphicsFamily,
		};

		CommandPool pool;
		if ( vk.CreateCommandPool( device, &poolInfo, null, &pool ) != Result.Success ) return false;

		frame.Pool = pool;

		var allocateInfo = new CommandBufferAllocateInfo
		{
			SType = StructureType.CommandBufferAllocateInfo,
			CommandPool = pool,
			Level = CommandBufferLevel.Primary,
			CommandBufferCount = 1,
		};

		CommandBuffer commands;
		if ( vk.AllocateCommandBuffers( device, &allocateInfo, &commands ) != Result.Success ) return false;

		frame.Commands = commands;

		if ( presents )
		{
			var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };

			VkSemaphore semaphore;
			if ( vk.CreateSemaphore( device, &semaphoreInfo, null, &semaphore ) != Result.Success ) return false;

			frame.ImageAvailable = semaphore;

			if ( vk.CreateSemaphore( device, &semaphoreInfo, null, &semaphore ) != Result.Success ) return false;

			frame.GeneratedAvailable = semaphore;
		}

		var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };

		//Unsignalled is fine: the submitted flag says whether to wait, so the first frame does not
		Fence fence;
		if ( vk.CreateFence( device, &fenceInfo, null, &fence ) != Result.Success ) return false;

		frame.Finished = fence;

		//Numbered by their place in the ring, so a message says which of the frames in flight it means
		VkDevice.NameHandle( ObjectType.CommandBuffer, (ulong)commands.Handle, "frame commands", which );
		VkDevice.NameHandle( ObjectType.CommandPool, pool.Handle, "frame command pool", which );
		VkDevice.NameHandle( ObjectType.Fence, fence.Handle, "frame finished", which );
		VkDevice.NameHandle( ObjectType.Semaphore, frame.ImageAvailable.Handle, "image available", which );

		//The profiler's timestamps, when the queue can write them at all. A frame without a
		//pool still counts draws and CPU time - the profile just has no GPU numbers in it.
		if ( VkDevice.timestampPeriod > 0.0f )
		{
			var queryInfo = new QueryPoolCreateInfo
			{
				SType = StructureType.QueryPoolCreateInfo,
				QueryType = QueryType.Timestamp,
				QueryCount = 2 + ( VkDevice.ProfileMaxZones * 2u ),
			};

			//A frame that cannot be timed is still a frame.
			QueryPool queries;

			if ( vk.CreateQueryPool( device, &queryInfo, null, &queries ) == Result.Success )
				frame.Queries = queries;
		}

		//And the one statistics query, where the device has the feature; begun only when asked for - see VkDevice.CountsFrame.
		//Five counters: what the assembler was handed, what the clipper kept, and the two stages' invocations.
		if ( VkDevice.statsQueries )
		{
			var statsInfo = new QueryPoolCreateInfo
			{
				SType = StructureType.QueryPoolCreateInfo,
				QueryType = QueryType.PipelineStatistics,
				QueryCount = 1,
				PipelineStatistics =
					QueryPipelineStatisticFlags.InputAssemblyPrimitivesBit |
					QueryPipelineStatisticFlags.VertexShaderInvocationsBit |
					QueryPipelineStatisticFlags.ClippingPrimitivesBit |
					QueryPipelineStatisticFlags.FragmentShaderInvocationsBit |
					QueryPipelineStatisticFlags.ComputeShaderInvocationsBit,
			};

			QueryPool stats;

			if ( vk.CreateQueryPool( device, &statsInfo, null, &stats ) == Result.Success )
				frame.Stats = stats;
		}

		if ( !VkDevice.AllocateDescriptorSet( VkDevice.boneSetLayout, out var boneSet ) ) return false;

		frame.BoneSet = boneSet;

		VkDevice.NameHandle( ObjectType.DescriptorSet, boneSet.Handle, "frame set", which );
		VkDevice.NameHandle( ObjectType.QueryPool, frame.Queries.Handle, "frame timestamps", which );

		return true;
	}

	internal static bool CreateFrames( VkSwapchain.Swapchain one )
	{
		for ( var i = 0; i < one.Frames.Length; ++i )
		{
			if ( !MakeFrame( one.Frames[i], true, (ulong)i ) ) return false;
		}

		return true;
	}

	//Consumes an acquired image's semaphore with an empty submit when the frame won't submit
	internal static void DrainAcquiredImage( Frame frame )
	{
		if ( frame.ImageAvailable.Handle == 0 ) return;

		//A lost device signals nothing and waits for nothing.
		if ( VkDevice.Lost ) return;

		//The fence must be clear to submit with, which it is not while its last outing counts as pending
		if ( frame.Submitted ) return;

		var wait = new SemaphoreSubmitInfo
		{
			SType = StructureType.SemaphoreSubmitInfo,
			Semaphore = frame.ImageAvailable,
			StageMask = PipelineStageFlags2.AllCommandsBit,
		};

		var submit = new SubmitInfo2
		{
			SType = StructureType.SubmitInfo2,
			WaitSemaphoreInfoCount = 1,
			PWaitSemaphoreInfos = &wait,
		};

		if ( VkDevice.dialect.Submit( VkDevice.graphicsQueue, &submit, frame.Finished ) != Result.Success ) return;

		var fence = frame.Finished;
		VkDevice.vk.WaitForFences( VkDevice.device, 1, &fence, true, ulong.MaxValue );
		VkDevice.vk.ResetFences( VkDevice.device, 1, &fence );
	}

	//Reads back what the frame's last outing measured, into a viewport's profile. Only after its
	//fence has passed: the fence is what makes the query results available without waiting for them.
	internal static void ResolveProfile( Frame frame, Profile into )
	{
		if ( !frame.ProfilePending ) return;

		frame.ProfilePending = false;

		into.Valid = true;
		into.GpuMs = 0.0f;
		into.CpuMs = (float)( ( frame.CpuEnd - frame.CpuBegin ) / 1.0e6 );
		into.Draws = frame.StatDraws;
		into.DynamicDraws = frame.StatDynamic;
		into.SkinnedDraws = frame.StatSkinned;
		into.Instances = frame.StatInstances;
		into.Triangles = frame.StatTriangles;
		into.ScratchUsed = frame.ScratchUsed;
		into.SkippedBinds = frame.StatSkippedBinds;
		into.Dispatches = frame.StatDispatches;
		into.Indirect = frame.StatIndirect;
		into.ZonesDropped = frame.ZonesDropped;
		into.ZoneCount = 0;

		var ticks = stackalloc ulong[2 + ( VkDevice.ProfileMaxZones * 2 )];

		var timed = frame.Queries.Handle != 0 && frame.QueryNext >= 2 &&
			VkDevice.vk.GetQueryPoolResults( VkDevice.device, frame.Queries, 0, frame.QueryNext,
				(nuint)( sizeof( ulong ) * frame.QueryNext ), ticks, sizeof( ulong ),
				QueryResultFlags.Result64Bit ) == Result.Success;

		var toMs = VkDevice.timestampPeriod / 1.0e6;

		if ( timed )
		{
			into.GpuMs = (float)( ( ticks[1] - ticks[0] ) * toMs );
			CountCardIdle( ticks[0], ticks[1], toMs );
		}

		//The frame's triangles, or where a run named a zone, its five counts in the log every 120th frame
		into.Counted = -1;

		if ( frame.Stats.Handle != 0 && frame.StatsTaken )
		{
			var counts = stackalloc ulong[5];

			var counted = VkDevice.vk.GetQueryPoolResults( VkDevice.device, frame.Stats, 0, 1,
				(nuint)( sizeof( ulong ) * 5 ), counts, sizeof( ulong ) * 5,
				QueryResultFlags.Result64Bit ) == Result.Success;

			if ( counted && VkDevice.StatsZone is null ) into.Counted = (long)counts[0];

			if ( counted && VkDevice.StatsZone is not null && ( ++VkDevice.statsFrame % 120 ) == 5 )
			{
				Log.Info( $"zone stats '{VkDevice.StatsZone}': {counts[0]:N0} primitives in, {counts[2]:N0} kept by the clipper, " +
					$"{counts[1]:N0} vertex stages, {counts[3]:N0} fragment stages, {counts[4]:N0} compute stages" );
			}
		}

		for ( var at = 0u; at < frame.ZoneCount; ++at )
		{
			ref var zone = ref frame.Zones[at];
			ref var outZone = ref into.Zones[into.ZoneCount++];

			for ( var i = 0; i < VkDevice.ProfileNameSize; ++i ) outZone.Name[i] = zone.Name[i];

			outZone.Depth = zone.Depth;
			outZone.CpuMs = (float)( ( zone.CpuEnd - zone.CpuBegin ) / 1.0e6 );
			outZone.GpuMs = timed ? (float)( ( ticks[zone.End] - ticks[zone.Begin] ) * toMs ) : 0.0f;
		}

		frame.ZoneCount = 0;
	}

	//Total GPU idle time between our recordings since CardIdleReset, to tell GPU-bound from not
	internal static double CardIdleMs { get; private set; }

	internal static long CardIdleRecordings { get; private set; }

	private static ulong lastCardEnd;

	internal static void CardIdleReset()
	{
		CardIdleMs = 0;
		CardIdleRecordings = 0;
	}

	private static void CountCardIdle( ulong begin, ulong end, double toMs )
	{
		if ( lastCardEnd != 0 && begin > lastCardEnd )
		{
			var gap = ( begin - lastCardEnd ) * toMs;

			//A gap of a whole second is a window nobody drew into, not a frame.
			if ( gap < 1000.0 ) CardIdleMs += gap;
		}

		if ( end > lastCardEnd ) lastCardEnd = end;
		CardIdleRecordings++;
	}

	//Labels the command buffer with the zone name for RGP/RenderDoc; no-op without debug utils
	private static void PushLabel( Frame frame, byte* name )
	{
		if ( VkDevice.extDebugUtils is null ) return;
		if ( frame.Commands.Handle == 0 || name is null ) return;

		var label = new DebugUtilsLabelEXT
		{
			SType = StructureType.DebugUtilsLabelExt,
			PLabelName = name,
		};

		VkDevice.extDebugUtils.CmdBeginDebugUtilsLabel( frame.Commands, &label );
	}

	//The matching end. Every path that opened a label has to reach exactly one of these, or a capture is
	//left holding an unbalanced stack - which reads as the whole rest of the frame nested inside one pass.
	private static void PopLabel( Frame frame )
	{
		if ( VkDevice.extDebugUtils is null ) return;
		if ( frame.Commands.Handle == 0 ) return;

		VkDevice.extDebugUtils.CmdEndDebugUtilsLabel( frame.Commands );
	}

	//Ends every zone still open - a caller that forgot an end, or a frame cut short - so
	//every reserved query slot is written and the readback finds only available results.
	internal static void CloseOpenZones( Frame frame )
	{
		var now = VkSwapchain.NowNs();

		while ( frame.ZoneDepth > 0 )
		{
			ref var zone = ref frame.Zones[frame.ZoneOpen[--frame.ZoneDepth]];

			zone.CpuEnd = now;

			if ( frame.Queries.Handle != 0 )
			{
				VkDevice.dialect.Timestamp( frame.Commands, PipelineStageFlags2.AllCommandsBit,
					frame.Queries, zone.End );
			}

			PopLabel( frame );
		}

		//The whole frame's count, or a counted zone closed by the frame ending rather than by its own end: ended
		//here outside any pass, or the pool is left with a query open and the next frame's reset is refused
		if ( frame.StatsRunning )
		{
			CloseOpenPasses( frame );
			VkDevice.vk.CmdEndQuery( frame.Commands, frame.Stats, 0 );
			frame.StatsRunning = false;
		}

		frame.DroppedOpen = 0;
	}

	//Before anything is recorded: wait for the last outing, hand the pool's memory back at once, and reserve
	//the two blocks every draw expects - zeroed bones, and the camera and lights
	internal static bool BeginRecording( Frame frame )
	{
		var vk = VkDevice.vk;
		var device = VkDevice.device;

		if ( frame.Submitted )
		{
			//Checked: a wait returning because the device is gone is as quick as one returning because the GPU
			//finished, and carrying on would reset a pool the GPU may still be reading
			var fence = frame.Finished;
			var waited = vk.WaitForFences( device, 1, &fence, true, ulong.MaxValue );

			if ( waited != Result.Success )
			{
				VkDevice.MarkLostIf( waited );
				return false;
			}

			vk.ResetFences( device, 1, &fence );
			frame.Submitted = false;

			VkDevice.TicketDone( frame.Ticket );
		}

		//Clear the transient set cache after the fence, before recording; views begin here, not BeginFrame
		frame.ObjectSetCount = 0;

		vk.ResetCommandPool( device, frame.Pool, 0 );
		frame.ScratchUsed = 0;
		DestroyRetiredScratch( frame );
		ReadyPosed( frame );

		//Where a recording begins is where "a frame" is counted from, under -drawtrace - see DrawTrace.
		DrawTrace.Frame();

		//Begun before the scratch: rewriting the frame's set readies the portable sun's maps with a barrier. A failure
		//below leaves it recording, which the pool's reset at the next attempt undoes.
		var begin = new CommandBufferBeginInfo
		{
			SType = StructureType.CommandBufferBeginInfo,
			Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
		};

		vk.BeginCommandBuffer( frame.Commands, &begin );

		//Pre-size scratch to last frame's peak so mid-frame growth is rare
		if ( frame.ScratchPeak > 0 ) EnsureScratch( frame, frame.ScratchPeak );

		//Its fence has been waited by now, so nothing is reading the set.
		if ( frame.FrameSetStale && frame.BoneSet.Handle != 0 && frame.Scratch.Handle != 0 )
		{
			WriteFrameSet( frame );
			frame.FrameSetStale = false;
		}

		var alignment = (ulong)VkDevice.storageAlignment;
		var frameStart = ( VkDevice.BoneBlockSize + alignment - 1 ) / alignment * alignment;

		if ( !EnsureScratch( frame, frameStart + VkDevice.FrameBlockReach ) ) return false;

		frame.NullBones = 0;
		frame.FrameBlock = frameStart;
		frame.ScratchUsed = frameStart + VkDevice.FrameBlockReach;

		//Zeroed, so a frame nobody described draws black rather than from whatever the last one left.
		System.Runtime.InteropServices.NativeMemory.Clear( frame.ScratchMapped, (nuint)frame.ScratchUsed );

		//From here, what is retired may be named by this recording; the graveyard keeps it until its fence
		VkDevice.RecordingBegan();

		//A clean slate for the profiler and bind cache: state and bindings belong to the just-reset buffer
		Current.WorldOnScreen = false;
		Current.WorldShades = true;
		frame.ZoneCount = 0;
		frame.ZoneDepth = 0;
		frame.DroppedOpen = 0;
		frame.ZonesDropped = 0;
		frame.StatDraws = 0;
		frame.StatInstances = 0;
		frame.StatDynamic = 0;
		frame.StatSkinned = 0;
		frame.StatTriangles = 0;
		frame.StatSkippedBinds = 0;
		frame.StatDispatches = 0;
		frame.StatIndirect = 0;
		frame.LastPipeline = default;
		frame.LastTexture = 0;
		frame.FrameSetBound = false;
		frame.ViewportSet = false;
		frame.LastVertexBuffer = default;
		frame.LastSkinBuffer = default;
		frame.LastIndexBuffer = default;
		frame.CpuBegin = VkSwapchain.NowNs();
		frame.QueryNext = 0;

		if ( frame.Queries.Handle != 0 )
		{
			vk.CmdResetQueryPool( frame.Commands, frame.Queries, 0, 2 + ( VkDevice.ProfileMaxZones * 2u ) );
			VkDevice.dialect.Timestamp( frame.Commands, PipelineStageFlags2.TopOfPipeBit, frame.Queries, 0 );
			frame.QueryNext = 2;
		}

		frame.StatsTaken = false;
		frame.StatsRunning = false;

		if ( frame.Stats.Handle != 0 ) vk.CmdResetQueryPool( frame.Commands, frame.Stats, 0, 1 );

		//Counted from here to CloseOpenZones; a depth no zone has, so no zone's end closes it
		if ( frame.Stats.Handle != 0 && VkDevice.CountsFrame )
		{
			vk.CmdBeginQuery( frame.Commands, frame.Stats, 0, 0 );
			frame.StatsTaken = true;
			frame.StatsRunning = true;
			frame.StatsDepth = -1;
		}

		return true;
	}

	//Closes any open pass for a command that may not be inside one; the next draw reopens the main pass
	private static void CloseOpenPasses( Frame frame )
	{
		if ( Current.TargetPass )
		{
			VkDevice.dialect.EndRendering( frame.Commands );
			Current.TargetPass = false;
		}

		if ( Current.MainPass )
		{
			VkDevice.dialect.EndRendering( frame.Commands );
			Current.MainPass = false;
		}

		//The pipeline that was bound belonged to a pass that has just closed, and the next draw has to say its own again.
		frame.LastPipeline = default;
		frame.FrameSetBound = false;
	}
}
