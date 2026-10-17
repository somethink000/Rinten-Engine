using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;

namespace Rinten;

//Foveated shading for headsets: a per-tile map of shading density, as a density map or shading rate
//Full-frame dialect only; applied to the world's depth and shading passes, never shadows or post
internal static unsafe class VkFovea
{
	internal enum Kinds
	{
		None,

		/// <summary>VK_EXT_fragment_density_map - a density a texel, R8G8, 255 the finest.</summary>
		Density,

		/// <summary>VK_KHR_fragment_shading_rate - a fragment size a texel, R8, log2 width and height.</summary>
		ShadingRate,
	}

	internal const string DensityExtension = "VK_EXT_fragment_density_map";
	internal const string RateExtension = "VK_KHR_fragment_shading_rate";

	//What this device was opened with. None until the device is made, and after it goes.
	internal static Kinds Kind { get; private set; }

	//How many pixels a texel of the map covers.
	internal static Extent2D Texel { get; private set; }

	//Whether the device takes shading rates without a headset, for the world shaded coarser where the last frame was
	//smooth - see Rendering.AdaptiveShading and VkFrame.Adaptive. A headset's map comes first where there is one.
	internal static bool Adaptive { get; private set; }

	//The extension to open the device with, or null.
	internal static string Extension => Kind switch
	{
		Kinds.Density => DensityExtension,
		Kinds.ShadingRate => RateExtension,
		_ => null,
	};

	private static PhysicalDeviceFragmentDensityMapFeaturesEXT* densityFeatures;
	private static PhysicalDeviceFragmentShadingRateFeaturesKHR* rateFeatures;

	//Which of the two this device can do, asked before it is made. A headset wanted and the full frame's dialect, or nothing - see the class remarks.
	internal static void Choose( PhysicalDevice physical, bool headset, bool modern )
	{
		Kind = Kinds.None;
		Texel = default;
		Adaptive = false;

		if ( !modern ) return;

		var vk = VkDevice.vk;

		//No headset: a shading rate only, laid by the picture rather than the eye, and as fine a map as the device
		//takes - a square is shaded coarser only where all of it is smooth, and a smaller square is smooth more often.
		if ( !headset )
		{
			if ( RatesTaken( physical, out var least ) )
			{
				Kind = Kinds.ShadingRate;
				Texel = least;
				Adaptive = true;
			}

			return;
		}

		//The density map first: on a tiler it is the device's own way of doing this - it shrinks
		//the bins, which is where a tiler spends its time - and a device that has both is a tiler.
		if ( VkDevice.HasDeviceExtension( physical, DensityExtension ) )
		{
			var has = new PhysicalDeviceFragmentDensityMapFeaturesEXT { SType = StructureType.PhysicalDeviceFragmentDensityMapFeaturesExt };
			var features = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &has };
			vk.GetPhysicalDeviceFeatures2( physical, &features );

			//Non-subsampled attachments: the world's pictures are ordinary ones, read afterwards
			//by passes that know nothing of this, so the device has to write them out whole.
			if ( has.FragmentDensityMap && has.FragmentDensityMapNonSubsampledImages )
			{
				var said = new PhysicalDeviceFragmentDensityMapPropertiesEXT { SType = StructureType.PhysicalDeviceFragmentDensityMapPropertiesExt };
				var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &said };
				vk.GetPhysicalDeviceProperties2( physical, &properties );

				Kind = Kinds.Density;
				Texel = Clamp( 32, said.MinFragmentDensityTexelSize, said.MaxFragmentDensityTexelSize );
				return;
			}
		}

		if ( VkDevice.HasDeviceExtension( physical, RateExtension ) )
		{
			var has = new PhysicalDeviceFragmentShadingRateFeaturesKHR { SType = StructureType.PhysicalDeviceFragmentShadingRateFeaturesKhr };
			var features = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &has };
			vk.GetPhysicalDeviceFeatures2( physical, &features );

			//The picture needs the pipeline's rate on as well: the pipeline's is what the
			//picture's is combined with, and the spec will not have one without the other.
			if ( has.AttachmentFragmentShadingRate && has.PipelineFragmentShadingRate )
			{
				var said = new PhysicalDeviceFragmentShadingRatePropertiesKHR { SType = StructureType.PhysicalDeviceFragmentShadingRatePropertiesKhr };
				var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &said };
				vk.GetPhysicalDeviceProperties2( physical, &properties );

				Kind = Kinds.ShadingRate;
				Texel = Clamp( 16, said.MinFragmentShadingRateAttachmentTexelSize, said.MaxFragmentShadingRateAttachmentTexelSize );
			}
		}
	}

	//Whether the device takes a shading rate picture, and the finest texel it takes one at.
	private static bool RatesTaken( PhysicalDevice physical, out Extent2D least )
	{
		least = default;

		if ( !VkDevice.HasDeviceExtension( physical, RateExtension ) ) return false;

		var vk = VkDevice.vk;
		var has = new PhysicalDeviceFragmentShadingRateFeaturesKHR { SType = StructureType.PhysicalDeviceFragmentShadingRateFeaturesKhr };
		var features = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &has };
		vk.GetPhysicalDeviceFeatures2( physical, &features );

		if ( !has.AttachmentFragmentShadingRate || !has.PipelineFragmentShadingRate ) return false;

		var said = new PhysicalDeviceFragmentShadingRatePropertiesKHR { SType = StructureType.PhysicalDeviceFragmentShadingRatePropertiesKhr };
		var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &said };
		vk.GetPhysicalDeviceProperties2( physical, &properties );

		//Eight or sixteen, which is what the analysis is laid out for - see shading_rate.slang.
		least = Clamp( 8, said.MinFragmentShadingRateAttachmentTexelSize, said.MaxFragmentShadingRateAttachmentTexelSize );
		if ( least.Width != least.Height || ( least.Width != 8 && least.Width != 16 ) ) return false;

		return true;
	}

	//The size nearest to what is wanted that the device allows - both ends are powers of two, and so is this.
	private static Extent2D Clamp( uint wanted, Extent2D least, Extent2D most )
	{
		static uint One( uint wanted, uint least, uint most )
		{
			if ( least == 0 ) least = 1;
			if ( most < least ) most = least;

			return Math.Clamp( wanted, least, most );
		}

		return new Extent2D( One( wanted, least.Width, most.Width ), One( wanted, least.Height, most.Height ) );
	}

	//The feature this device is opened with, put at the front of the chain the device is made from. Kept until FeaturesDone.
	internal static void ChainFeatures( PhysicalDeviceFeatures2* features )
	{
		if ( Kind == Kinds.Density )
		{
			densityFeatures = (PhysicalDeviceFragmentDensityMapFeaturesEXT*)NativeMemory.AllocZeroed( (nuint)sizeof( PhysicalDeviceFragmentDensityMapFeaturesEXT ) );
			densityFeatures->SType = StructureType.PhysicalDeviceFragmentDensityMapFeaturesExt;
			densityFeatures->FragmentDensityMap = true;
			densityFeatures->FragmentDensityMapNonSubsampledImages = true;
			densityFeatures->PNext = features->PNext;
			features->PNext = densityFeatures;
		}
		else if ( Kind == Kinds.ShadingRate )
		{
			rateFeatures = (PhysicalDeviceFragmentShadingRateFeaturesKHR*)NativeMemory.AllocZeroed( (nuint)sizeof( PhysicalDeviceFragmentShadingRateFeaturesKHR ) );
			rateFeatures->SType = StructureType.PhysicalDeviceFragmentShadingRateFeaturesKhr;
			rateFeatures->PipelineFragmentShadingRate = true;
			rateFeatures->AttachmentFragmentShadingRate = true;
			rateFeatures->PNext = features->PNext;
			features->PNext = rateFeatures;
		}
	}

	internal static void FeaturesDone()
	{
		NativeMemory.Free( densityFeatures );
		NativeMemory.Free( rateFeatures );
		densityFeatures = null;
		rateFeatures = null;
	}

	//The device refused, or is going: nothing is foveated on whatever comes next.
	internal static void Forget()
	{
		Kind = Kinds.None;
		Texel = default;
		Adaptive = false;
	}

	//-- pipelines -------------------------------------------------------------

	//What every graphics pipeline is made with while the device foveates: the flag that lets it be drawn in a pass with the map attached - a pipeline without
	//it may not be, and one with it may still be drawn in a pass without - and, for a shading rate, the rule that the picture's rate is the one taken.
	internal static PipelineCreateFlags PipelineFlags => Kind switch
	{
		Kinds.Density => PipelineCreateFlags.CreateRenderingFragmentDensityMapAttachmentBitExt,
		Kinds.ShadingRate => PipelineCreateFlags.CreateRenderingFragmentShadingRateAttachmentBitKhr,
		_ => 0,
	};

	//Pipeline shading rate state; only depth-tested pipelines follow the map, UI and fullscreen passes don't
	internal static void RateState( PipelineFragmentShadingRateStateCreateInfoKHR* state, bool followsMap )
	{
		state->SType = StructureType.PipelineFragmentShadingRateStateCreateInfoKhr;
		state->FragmentSize = new Extent2D( 1, 1 );
		state->CombinerOps[0] = FragmentShadingRateCombinerOpKHR.KeepKhr;
		state->CombinerOps[1] = followsMap ? FragmentShadingRateCombinerOpKHR.ReplaceKhr : FragmentShadingRateCombinerOpKHR.KeepKhr;
	}

	//-- a view's map ------------------------------------------------------------

	//One view's map and what it was last written from. The settings are the drawer's, said every
	//frame; the picture is made the first time a pass wants it and again when the view changes size.
	internal sealed class Map
	{
		/// <summary>How much coarser the edge is, nought to one. Nought is no map at all.</summary>
		public float Strength;

		/// <summary>Laid by the picture rather than by the eye: written on the card by VkFrame.Adaptive from the frame before, never by Write here.</summary>
		public bool Adaptive;

		/// <summary>The card's own copy an adaptive map is worked out into and copied from - a VkBuffers handle.</summary>
		public ulong Rates;

		/// <summary>Bytes a row of the map takes in <see cref="Rates"/> - whole words, so a thread writes a word.</summary>
		public uint RowBytes;

		/// <summary>Whether the map has been given anything yet - an adaptive one starts every pixel fine.</summary>
		public bool Cleared;

		/// <summary>Whether this frame's world was drawn with it - what says the picture is worth reading into it again. See VkFrame.Adaptive.</summary>
		public bool DrawnWith;

		/// <summary>Whether the middle is where the eyes look, rather than the middle of the field.</summary>
		public bool Tracked;

		/// <summary>Where the middle is in each layer, nought to one across and down.</summary>
		public readonly System.Numerics.Vector2[] Centre = new System.Numerics.Vector2[2];

		public VkImage Image;
		public DeviceMemory Memory;
		public ImageView View;
		public ImageLayout Layout;

		/// <summary>An adaptive map's world, clip space into the frame before's, as the frame drawn with it was - what its next rates follow the view by.</summary>
		public System.Numerics.Matrix4x4 Back = System.Numerics.Matrix4x4.Identity;

		/// <summary>The pixels it was made to cover, and how many layers.</summary>
		public Extent2D For;
		public uint Layers;

		/// <summary>Its size in texels.</summary>
		public Extent2D Size;

		/// <summary>Where the texels are written before they are copied in: host memory, three runs of it used in turn, so a run is never written while a frame still in flight is copying out of it - a view has two frames, see VkSwapchain.View.Frames, and a map is written at most once a frame.</summary>
		public VkBuffer Staging;
		public DeviceMemory StagingMemory;
		public byte* Mapped;
		public ulong RunBytes;
		public int Run;

		/// <summary>What the picture holds now, so a frame where nothing moved writes nothing.</summary>
		public float WrittenStrength = -1.0f;
		public bool WrittenTracked;
		public readonly System.Numerics.Vector2[] WrittenCentre = new System.Numerics.Vector2[2];

		/// <summary>The device would not make it. Refused once is refused for as long as the view lives: nothing is foveated for it, rather than a map asked for and refused every frame.</summary>
		public bool Refused;

		public bool Made => Image.Handle != 0;
	}

	//The drawer's word for this frame: how strongly, and where each layer's middle is. Called outside any pass, before the view is drawn.
	internal static void Set( VkSwapchain.Pictures p, float strength, bool tracked, float u0, float v0, float u1, float v1 )
	{
		if ( Kind == Kinds.None ) return;

		var map = p.Fovea ??= new Map();

		map.Strength = Math.Clamp( strength, 0.0f, 1.0f );
		map.Tracked = tracked;
		map.Centre[0] = new System.Numerics.Vector2( u0, v0 );
		map.Centre[1] = new System.Numerics.Vector2( u1, v1 );
	}

	//The fovea map for a pass, or null if shaded evenly or sized unlike the world. Call outside passes
	internal static Map Prepare( VkFrame.Frame frame, VkSwapchain.Pictures p, Extent2D area, uint layers )
	{
		if ( Kind == Kinds.None || p?.Fovea is not { Strength: > 0.0f, Refused: false } map ) return null;
		if ( area.Width == 0 || area.Height == 0 ) return null;

		layers = Math.Max( layers, 1u );

		if ( map.Made && ( map.For.Width != area.Width || map.For.Height != area.Height || map.Layers != layers ) )
		{
			//A picture of another size is the world at another size; the old map goes after the frames that still read it.
			Release( map, deferred: true );
		}

		if ( !map.Made && !Make( map, area, layers ) )
		{
			Release( map, deferred: true );
			map.Refused = true;
			return null;
		}

		if ( map.Adaptive )
		{
			if ( !map.Cleared ) Clear( frame, map );
		}
		else if ( !Same( map ) )
		{
			Write( frame, map );
		}

		return map;
	}

	private static bool Same( Map map )
	{
		if ( map.WrittenStrength != map.Strength || map.WrittenTracked != map.Tracked ) return false;

		//Moved by less than a texel is not moved: nothing the map says would change.
		for ( var layer = 0; layer < map.Layers; layer++ )
		{
			var moved = map.Centre[layer] - map.WrittenCentre[layer];

			if ( MathF.Abs( moved.X ) * map.Size.Width >= 0.5f || MathF.Abs( moved.Y ) * map.Size.Height >= 0.5f )
				return false;
		}

		return true;
	}

	private static bool Make( Map map, Extent2D area, uint layers )
	{
		var vk = VkDevice.vk;
		var device = VkDevice.device;

		var size = new Extent2D(
			( area.Width + Texel.Width - 1 ) / Texel.Width,
			( area.Height + Texel.Height - 1 ) / Texel.Height );

		var density = Kind == Kinds.Density;
		var format = density ? Format.R8G8Unorm : Format.R8Uint;
		var usage = ImageUsageFlags.TransferDstBit | ( density
			? ImageUsageFlags.FragmentDensityMapBitExt
			: ImageUsageFlags.FragmentShadingRateAttachmentBitKhr );

		if ( !VkSwapchain.MakePicture( format, size, 1, usage, out var image, out var memory, out var view, layers ) )
		{
			VkSwapchain.DropImage( image, memory, view, deferred: false );
			VkDevice.SetText( "the foveation map would not be made" );
			return false;
		}

		map.Image = image;
		map.Memory = memory;
		map.View = view;
		map.Layout = ImageLayout.Undefined;
		map.For = area;
		map.Layers = layers;
		map.Size = size;

		VkDevice.NameHandle( ObjectType.Image, image.Handle, map.Adaptive ? "shading rate map" : "foveation map" );

		//Laid by the card: a buffer of its own the analysis writes and the map is copied from, no host memory. See VkFrame.Adaptive.
		if ( map.Adaptive )
		{
			map.RowBytes = ( size.Width + 3u ) & ~3u;
			map.Rates = VkBuffers.Reserve( (long)map.RowBytes * size.Height * layers );
			map.Cleared = false;
			if ( map.Rates != 0 ) VkDevice.NameHandle( ObjectType.Buffer, VkBuffers.Get( map.Rates ).Handle, "shading rate words" );
			return map.Rates != 0;
		}

		//The staging runs: host memory the texels are written into and copied out of. A texel is two bytes for a density and one for a rate.
		var texelBytes = density ? 2u : 1u;
		var runBytes = (ulong)size.Width * size.Height * layers * texelBytes;

		//Each run starts where a copy may read from - four bytes is every format's texel alignment here and the copy's own.
		runBytes = ( runBytes + 15ul ) & ~15ul;

		var info = new BufferCreateInfo
		{
			SType = StructureType.BufferCreateInfo,
			Size = runBytes * 3,
			Usage = BufferUsageFlags.TransferSrcBit,
			SharingMode = SharingMode.Exclusive,
		};

		VkBuffer buffer;
		if ( vk.CreateBuffer( device, &info, null, &buffer ) != Result.Success ) return false;

		map.Staging = buffer;

		MemoryRequirements requirements;
		vk.GetBufferMemoryRequirements( device, buffer, &requirements );

		var allocate = new MemoryAllocateInfo
		{
			SType = StructureType.MemoryAllocateInfo,
			AllocationSize = requirements.Size,
			MemoryTypeIndex = VkDevice.FindMemoryType( requirements.MemoryTypeBits,
				MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit ),
		};

		if ( allocate.MemoryTypeIndex == uint.MaxValue ) return false;

		DeviceMemory staging;
		if ( vk.AllocateMemory( device, &allocate, null, &staging ) != Result.Success ) return false;

		map.StagingMemory = staging;

		if ( vk.BindBufferMemory( device, buffer, staging, 0 ) != Result.Success ) return false;

		void* mapped;
		if ( vk.MapMemory( device, staging, 0, Vk.WholeSize, 0, &mapped ) != Result.Success ) return false;

		map.Mapped = (byte*)mapped;
		map.RunBytes = runBytes;
		map.Run = 0;
		map.WrittenStrength = -1.0f;

		return true;
	}

	//The map's GPU half let go - on a resize after the frames that read it, and at once where the device is
	//idle. The settings stay: the drawer says them again every frame, and a remade map is written from them.
	internal static void Release( Map map, bool deferred )
	{
		if ( map is null ) return;

		//Freeing the memory unmaps it.
		map.Mapped = null;

		if ( map.Staging.Handle != 0 || map.StagingMemory.Handle != 0 )
		{
			if ( deferred )
			{
				VkDevice.RetireBuffer( map.Staging, map.StagingMemory );
			}
			else
			{
				if ( map.Staging.Handle != 0 ) VkDevice.vk.DestroyBuffer( VkDevice.device, map.Staging, null );
				if ( map.StagingMemory.Handle != 0 ) VkDevice.vk.FreeMemory( VkDevice.device, map.StagingMemory, null );
			}
		}

		VkSwapchain.DropImage( map.Image, map.Memory, map.View, deferred );

		if ( map.Rates != 0 ) VkBuffers.Destroy( map.Rates );
		map.Rates = 0;
		map.Cleared = false;

		map.Staging = default;
		map.StagingMemory = default;
		map.Image = default;
		map.Memory = default;
		map.View = default;
		map.Layout = ImageLayout.Undefined;
		map.For = default;
		map.Layers = 0;
		map.Size = default;
		map.WrittenStrength = -1.0f;
	}

	//Shading fineness by distance from the gaze centre in three rings; smaller centre with eye tracking
	private static float Fineness( Map map, int layer, uint x, uint y )
	{
		var aspect = (float)map.For.Width / MathF.Max( map.For.Height, 1u );

		var u = ( x + 0.5f ) / map.Size.Width;
		var v = ( y + 0.5f ) / map.Size.Height;

		var centre = map.Centre[layer];
		var across = ( u - centre.X ) * aspect;
		var down = v - centre.Y;
		var away = MathF.Sqrt( across * across + down * down );

		var s = map.Strength;

		var inner = map.Tracked ? 0.16f + 0.14f * ( 1.0f - s ) : 0.30f + 0.20f * ( 1.0f - s );
		var outer = inner + ( map.Tracked ? 0.18f : 0.22f );

		if ( away < inner ) return 1.0f;
		if ( away < outer || s < 0.5f ) return 0.5f;

		return 0.25f;
	}

	//The texels worked out into the next staging run and copied into the map, which is left where the passes read it from.
	private static void Write( VkFrame.Frame frame, Map map )
	{
		if ( map.Mapped is null ) return;

		var run = map.Run;
		map.Run = ( map.Run + 1 ) % 3;

		var into = map.Mapped + (ulong)run * map.RunBytes;
		var density = Kind == Kinds.Density;
		var texelBytes = density ? 2u : 1u;
		var layerBytes = (ulong)map.Size.Width * map.Size.Height * texelBytes;

		for ( var layer = 0; layer < map.Layers; layer++ )
		{
			var row = into + (ulong)layer * layerBytes;

			for ( var y = 0u; y < map.Size.Height; y++ )
			{
				for ( var x = 0u; x < map.Size.Width; x++ )
				{
					var fine = Fineness( map, layer, x, y );
					var at = ( y * map.Size.Width + x ) * texelBytes;

					if ( density )
					{
						//A density a direction, 255 being a fragment a pixel.
						var value = (byte)Math.Clamp( (int)MathF.Round( fine * 255.0f ), 1, 255 );
						row[at] = value;
						row[at + 1] = value;
					}
					else
					{
						//A fragment size: log2 of the width in bits two and three, of the height in bits nought
						//and one. Two by two at most - the size every device that takes a picture has to take.
						var side = fine >= 1.0f ? 0 : 1;
						row[at] = (byte)( ( side << 2 ) | side );
					}
				}
			}
		}

		var commands = frame.Commands;

		//Whatever read it last - the passes of a frame before - is finished with it before the copy lands.
		var (readStage, readAccess, readLayout) = ReadBy();

		VkFrame.Transition( commands, map.Image, map.Layout, ImageLayout.TransferDstOptimal,
			map.Layout == ImageLayout.Undefined ? PipelineStageFlags2.None : readStage,
			map.Layout == ImageLayout.Undefined ? AccessFlags2.None : readAccess,
			PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit,
			ImageAspectFlags.ColorBit, 0, 1, map.Layers );

		var region = new BufferImageCopy
		{
			BufferOffset = (ulong)run * map.RunBytes,
			ImageSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, 0, 0, map.Layers ),
			ImageExtent = new Extent3D( map.Size.Width, map.Size.Height, 1 ),
		};

		VkDevice.vk.CmdCopyBufferToImage( commands, map.Staging, map.Image, ImageLayout.TransferDstOptimal, 1, &region );

		VkFrame.Transition( commands, map.Image, ImageLayout.TransferDstOptimal, readLayout,
			PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit,
			readStage, readAccess,
			ImageAspectFlags.ColorBit, 0, 1, map.Layers );

		map.Layout = readLayout;
		map.WrittenStrength = map.Strength;
		map.WrittenTracked = map.Tracked;
		map.WrittenCentre[0] = map.Centre[0];
		map.WrittenCentre[1] = map.Centre[1];
	}

	//An adaptive map's first contents: every square shaded a fragment a pixel.
	private static void Clear( VkFrame.Frame frame, Map map )
	{
		var rates = VkBuffers.Get( map.Rates );
		if ( rates.Handle == 0 ) return;

		VkDevice.vk.CmdFillBuffer( frame.Commands, rates, 0, Vk.WholeSize, 0u );

		var fill = new MemoryBarrier2
		{
			SType = StructureType.MemoryBarrier2,
			SrcStageMask = PipelineStageFlags2.AllTransferBit,
			SrcAccessMask = AccessFlags2.TransferWriteBit,
			DstStageMask = PipelineStageFlags2.AllTransferBit,
			DstAccessMask = AccessFlags2.TransferReadBit,
		};

		var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &fill };
		VkDevice.dialect.Barrier( frame.Commands, &dependency );

		CopyIn( frame, map );
		map.Cleared = true;
	}

	//An adaptive map's buffer copied into its picture, which is left where the world's passes read it. Outside any pass, after whatever wrote the buffer.
	internal static void CopyIn( VkFrame.Frame frame, Map map )
	{
		var rates = VkBuffers.Get( map.Rates );
		if ( rates.Handle == 0 || !map.Made ) return;

		var commands = frame.Commands;
		var (readStage, readAccess, readLayout) = ReadBy();

		VkFrame.Transition( commands, map.Image, map.Layout, ImageLayout.TransferDstOptimal,
			map.Layout == ImageLayout.Undefined ? PipelineStageFlags2.None : readStage,
			map.Layout == ImageLayout.Undefined ? AccessFlags2.None : readAccess,
			PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit,
			ImageAspectFlags.ColorBit, 0, 1, map.Layers );

		var region = new BufferImageCopy
		{
			BufferOffset = 0,
			BufferRowLength = map.RowBytes,
			BufferImageHeight = map.Size.Height,
			ImageSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, 0, 0, map.Layers ),
			ImageExtent = new Extent3D( map.Size.Width, map.Size.Height, 1 ),
		};

		VkDevice.vk.CmdCopyBufferToImage( commands, rates, map.Image, ImageLayout.TransferDstOptimal, 1, &region );

		VkFrame.Transition( commands, map.Image, ImageLayout.TransferDstOptimal, readLayout,
			PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit,
			readStage, readAccess,
			ImageAspectFlags.ColorBit, 0, 1, map.Layers );

		map.Layout = readLayout;
	}

	//Where the passes read the map, and in which layout.
	private static (PipelineStageFlags2, AccessFlags2, ImageLayout) ReadBy() => Kind == Kinds.Density
		? (PipelineStageFlags2.FragmentDensityProcessBitExt, AccessFlags2.FragmentDensityMapReadBitExt,
			ImageLayout.FragmentDensityMapOptimalExt)
		: (PipelineStageFlags2.FragmentShadingRateAttachmentBitKhr, AccessFlags2.FragmentShadingRateAttachmentReadBitKhr,
			ImageLayout.FragmentShadingRateAttachmentOptimalKhr);

	//The map said to a pass: one of the two structures chained on where it begins, filled in density or rate and
	//put at the front of rendering's chain. The two live in the caller's frame, as the pass's other structures do.
	internal static void Attach( Map map, RenderingInfo* rendering,
		RenderingFragmentDensityMapAttachmentInfoEXT* density, RenderingFragmentShadingRateAttachmentInfoKHR* rate )
	{
		if ( map is null || !map.Made ) return;

		if ( Kind == Kinds.Density )
		{
			*density = new RenderingFragmentDensityMapAttachmentInfoEXT
			{
				SType = StructureType.RenderingFragmentDensityMapAttachmentInfoExt,
				PNext = rendering->PNext,
				ImageView = map.View,
				ImageLayout = ImageLayout.FragmentDensityMapOptimalExt,
			};

			rendering->PNext = density;
		}
		else if ( Kind == Kinds.ShadingRate )
		{
			*rate = new RenderingFragmentShadingRateAttachmentInfoKHR
			{
				SType = StructureType.RenderingFragmentShadingRateAttachmentInfoKhr,
				PNext = rendering->PNext,
				ImageView = map.View,
				ImageLayout = ImageLayout.FragmentShadingRateAttachmentOptimalKhr,
				ShadingRateAttachmentTexelSize = Texel,
			};

			rendering->PNext = rate;
		}
	}
}
