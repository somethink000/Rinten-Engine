using Silk.NET.Vulkan;
using VkImage = Silk.NET.Vulkan.Image;

namespace Rinten;

//Depth pyramid: one mipped image kept in GENERAL, so levels need no per-frame transitions
internal static unsafe partial class VkTextures
{
	private sealed class GpuPyramid
	{
		public VkImage Image;
		public DeviceMemory Memory;
		public int Block = -1;
		public ulong Offset;
		public ulong Bytes;

		/// <summary>Every level at once, which is what the cull pass samples.</summary>
		public ImageView Whole;

		/// <summary>And one apiece, which is what a level is written through.</summary>
		public ImageView[] Levels = [];

		/// <summary>In the shape of a material set, so a dispatch may be handed this the same way it is handed any other picture.</summary>
		public DescriptorSet Set;

		public uint Width;
		public uint Height;
		public uint Mips;

		/// <summary>Whether it has been brought out of UNDEFINED yet.</summary>
		public bool Ready;
	}

	private static readonly Dictionary<ulong, GpuPyramid> pyramids = new();

	//What every depth pyramid holds between them, for the memory report.
	internal static long PyramidBytes()
	{
		var bytes = 0L;
		foreach ( var pair in pyramids ) bytes += (long)pair.Value.Bytes;
		return bytes;
	}

	private static void DestroyOne( GpuPyramid pyramid )
	{
		if ( pyramid.Set.Handle != 0 ) VkDevice.RetireDescriptorSet( pyramid.Set );

		for ( var i = 0; i < pyramid.Levels.Length; ++i )
		{
			if ( pyramid.Levels[i].Handle != 0 ) VkDevice.RetireImage( default, default, pyramid.Levels[i] );
		}

		//Sliced images go back to their shared block; freeing the block would kill its neighbours
		if ( pyramid.Block >= 0 )
		{
			VkDevice.RetireSlicedImage( pyramid.Image, pyramid.Whole, pyramid.Block, pyramid.Offset, pyramid.Bytes );
		}
		else if ( pyramid.Whole.Handle != 0 || pyramid.Image.Handle != 0 )
		{
			VkDevice.RetireImage( pyramid.Image, pyramid.Memory, pyramid.Whole );
		}

		pyramid.Set = default;
		pyramid.Levels = [];
		pyramid.Image = default;
		pyramid.Memory = default;
		pyramid.Whole = default;
		pyramid.Block = -1;
	}

	//What one level of the depth pyramid is written through, and how big it is. False where the handle is not a pyramid or the level is past its top.
	internal static bool PyramidLevel( ulong handle, uint lod, out VkImage image, out ImageView into,
		out ImageView whole, out uint levelWidth, out uint levelHeight, out uint mips, out bool first )
	{
		image = default;
		into = default;
		whole = default;
		levelWidth = 0;
		levelHeight = 0;
		mips = 0;
		first = false;

		if ( !pyramids.TryGetValue( handle, out var pyramid ) || lod >= pyramid.Mips ) return false;

		image = pyramid.Image;
		into = pyramid.Levels[lod];
		whole = pyramid.Whole;
		mips = pyramid.Mips;
		first = !pyramid.Ready;

		levelWidth = pyramid.Width >> (int)lod;
		levelHeight = pyramid.Height >> (int)lod;

		if ( levelWidth < 1 ) levelWidth = 1;
		if ( levelHeight < 1 ) levelHeight = 1;

		return true;
	}

	//Says the pyramid has been brought out of UNDEFINED, so the next level does not do it again.
	internal static void PyramidReady( ulong handle )
	{
		if ( pyramids.TryGetValue( handle, out var pyramid ) ) pyramid.Ready = true;
	}

	//True for a pyramid still in UNDEFINED (never built), so a reader can move it to GENERAL first
	internal static bool PyramidUnready( ulong handle, out VkImage image, out uint mips )
	{
		image = default;
		mips = 0;

		if ( !pyramids.TryGetValue( handle, out var pyramid ) || pyramid.Ready ) return false;

		image = pyramid.Image;
		mips = pyramid.Mips;

		return true;
	}

	internal static ulong PyramidCreate( int width, int height )
	{
		lock ( VkDevice.renderLock )
		{
			if ( VkDevice.device.Handle == 0 || width < 1 || height < 1 ) return 0;

			var made = new GpuPyramid
			{
				Width = (uint)width,
				Height = (uint)height,
				Mips = 1,
			};

			//Down to one texel, the way a mip chain always goes. Each level is the one below halved
			//and floored, so the sides need not be powers of two and need not match each other.
			for ( uint w = made.Width, h = made.Height; w > 1 || h > 1; )
			{
				w = w > 1 ? w >> 1 : 1;
				h = h > 1 ? h >> 1 : 1;
				made.Mips++;
			}

			var vk = VkDevice.vk;
			var device = VkDevice.device;

			var imageInfo = new ImageCreateInfo
			{
				SType = StructureType.ImageCreateInfo,
				ImageType = ImageType.Type2D,
				Format = Format.R32Sfloat,
				Extent = new Extent3D( made.Width, made.Height, 1 ),
				MipLevels = made.Mips,
				ArrayLayers = 1,
				Samples = SampleCountFlags.Count1Bit,
				Tiling = ImageTiling.Optimal,
				Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit,
				InitialLayout = ImageLayout.Undefined,
			};

			VkImage image;

			if ( vk.CreateImage( device, &imageInfo, null, &image ) != Result.Success )
			{
				VkDevice.SetText( "the depth pyramid would not be made" );
				return 0;
			}

			made.Image = image;

			MemoryRequirements requirements;
			vk.GetImageMemoryRequirements( device, image, &requirements );

			if ( !TakeMemory( requirements, out var memory, out var block, out var offset ) ||
				vk.BindImageMemory( device, image, memory, offset ) != Result.Success )
			{
				vk.DestroyImage( device, image, null );
				VkDevice.SetText( "the depth pyramid's memory would not be had" );
				return 0;
			}

			made.Memory = memory;
			made.Block = block;
			made.Offset = offset;
			made.Bytes = requirements.Size;

			var viewInfo = new ImageViewCreateInfo
			{
				SType = StructureType.ImageViewCreateInfo,
				Image = image,
				ViewType = ImageViewType.Type2D,
				Format = Format.R32Sfloat,
				SubresourceRange = new ImageSubresourceRange( ImageAspectFlags.ColorBit, 0, made.Mips, 0, 1 ),
			};

			ImageView whole;

			if ( vk.CreateImageView( device, &viewInfo, null, &whole ) != Result.Success )
			{
				DestroyOne( made );
				VkDevice.SetText( "the depth pyramid's view would not be made" );
				return 0;
			}

			made.Whole = whole;
			made.Levels = new ImageView[made.Mips];

			for ( var lod = 0u; lod < made.Mips; ++lod )
			{
				viewInfo.SubresourceRange.BaseMipLevel = lod;
				viewInfo.SubresourceRange.LevelCount = 1;

				ImageView level;

				if ( vk.CreateImageView( device, &viewInfo, null, &level ) != Result.Success )
				{
					DestroyOne( made );
					VkDevice.SetText( "a level of the depth pyramid would not be viewed" );
					return 0;
				}

				made.Levels[lod] = level;
			}

			if ( !AllocateTextureSet( out var set ) )
			{
				DestroyOne( made );
				VkDevice.SetText( "no descriptor set for the depth pyramid" );
				return 0;
			}

			made.Set = set;

			//Every slot of the material set points at the whole chain: a dispatch reads the first, and the rest have to name something
			//the driver may answer for. Read texel by texel, so the sampler is only what the set has to name.
			var views = stackalloc ImageView[(int)VkDevice.MaterialTextures];
			for ( var i = 0; i < VkDevice.MaterialTextures; ++i ) views[i] = whole;

			//In GENERAL, and said so: the chain is written as a storage image and read as a picture without ever leaving that layout.
			WriteMaterialSet( made.Set, views, VkDevice.defaultParams, VkDevice.pointSampler, ImageLayout.General );

			var handle = nextHandle++;

			VkDevice.NameHandle( ObjectType.Image, made.Image.Handle, "depth pyramid", handle );

			pyramids[handle] = made;

			return handle;
		}
	}

	internal static void PyramidDestroy( ulong pyramid )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !pyramids.TryGetValue( pyramid, out var found ) ) return;

			DestroyOne( found );
			pyramids.Remove( pyramid );
		}
	}
}
