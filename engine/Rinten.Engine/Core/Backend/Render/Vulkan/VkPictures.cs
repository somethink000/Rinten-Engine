using Silk.NET.Vulkan;

namespace Rinten;

//Scratch pictures for frame passes, requested by shape and reused next frame for the same shape
internal static unsafe class VkPictures
{
	//One picture and what is known about it.
	internal sealed class Picture
	{
		public ulong Id;
		public Silk.NET.Vulkan.Image Image;
		public DeviceMemory Memory;

		/// <summary>Over every level - what a shader reads through the table.</summary>
		public ImageView View;

		/// <summary>One level each - what a pass draws into or stores into.</summary>
		public ImageView[] Level;

		public Format Format;
		public Extent2D Extent;
		public uint Levels;
		public bool Storage;

		/// <summary>Where each level is, as far as what has been recorded knows.</summary>
		public ImageLayout[] Layout;

		/// <summary>Its handle in the texture table, which is what a shader indexes by.</summary>
		public ulong Handle;

		public bool Held;
		public ulong IdleSince;
	}

	//How many frames a picture nobody asked for again is kept before it goes.
	private const ulong KeepFrames = 240;

	private static readonly Dictionary<ulong, Picture> all = new();
	private static ulong nextId = 1;
	private static ulong tick;

	internal static Picture Get( ulong id ) => id != 0 && all.TryGetValue( id, out var found ) ? found : null;

	//Vulkan format for a picture kind, given storage/chain needs; cached since the device can't change
	internal static Format FormatOf( int kind, bool storage, bool chain )
	{
		var at = ( kind * 4 ) + ( storage ? 2 : 0 ) + ( chain ? 1 : 0 );

		if ( at >= 0 && at < formatCache.Length && formatCache[at] != Format.Undefined )
			return formatCache[at];

		var found = FindFormat( kind, storage, chain );

		if ( at >= 0 && at < formatCache.Length ) formatCache[at] = found;

		return found;
	}

	//The answers, by kind and by what is asked of them - see FormatOf.
	private static readonly Format[] formatCache = new Format[64];

	//Forgotten with the device, which is the only thing that can change the answers.
	internal static void ForgetFormats() => Array.Clear( formatCache );

	private static Format FindFormat( int kind, bool storage, bool chain )
	{
		var wanted = kind switch
		{
			(int)CoreRender.PictureKind.Hdr => VkDevice.SceneFormat,
			(int)CoreRender.PictureKind.HdrCompact => VkDevice.CompactFormat,
			(int)CoreRender.PictureKind.Colour => Format.R8G8B8A8Unorm,
			(int)CoreRender.PictureKind.Single => Format.R16Sfloat,
			(int)CoreRender.PictureKind.Pair => Format.R16G16Sfloat,
			(int)CoreRender.PictureKind.Full => Format.R32G32B32A32Sfloat,
			(int)CoreRender.PictureKind.SingleFull => Format.R32Sfloat,
			(int)CoreRender.PictureKind.Ten => Format.A2B10G10R10UnormPack32,
			(int)CoreRender.PictureKind.Whole => Format.R32Uint,
			(int)CoreRender.PictureKind.WholePair => Format.R32G32Sint,
			_ => VkDevice.SceneFormat,
		};

		//An integer is never filtered, and is only ever stored into and read back by the pass that stores it - so it asks
		//for storage and nothing else, and never falls back to half floats, which would be the wrong kind of number.
		if ( kind is (int)CoreRender.PictureKind.Whole or (int)CoreRender.PictureKind.WholePair )
			return wanted;

		var needs = FormatFeatureFlags.ColorAttachmentBit | FormatFeatureFlags.SampledImageBit |
			FormatFeatureFlags.SampledImageFilterLinearBit;

		if ( storage ) needs |= FormatFeatureFlags.StorageImageBit;
		if ( chain ) needs |= FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit;

		FormatProperties properties;
		VkDevice.vk.GetPhysicalDeviceFormatProperties( VkDevice.physicalDevice, wanted, &properties );

		return ( properties.OptimalTilingFeatures & needs ) == needs ? wanted : VkDevice.SceneFormat;
	}

	//How many levels a picture of this size can have: halvings of the longer side, and the top.
	internal static uint MostLevels( uint width, uint height )
	{
		var side = Math.Max( width, height );
		var levels = 1u;

		while ( side > 1 )
		{
			side >>= 1;
			levels++;
		}

		return levels;
	}

	internal static Extent2D LevelExtent( Picture picture, int level )
		=> new( Math.Max( picture.Extent.Width >> level, 1u ), Math.Max( picture.Extent.Height >> level, 1u ) );

	//A picture of this shape for the caller until it gives it back - one given back earlier
	//where there is one, a new one where there is not. Nought where the device would not make it.
	internal static ulong Acquire( int kind, int width, int height, int levels, bool storage )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !VkDevice.Ready ) return 0;

			var extent = new Extent2D( (uint)Math.Max( width, 1 ), (uint)Math.Max( height, 1 ) );
			var count = (uint)Math.Clamp( levels, 1, (int)MostLevels( extent.Width, extent.Height ) );
			var format = FormatOf( kind, storage, count > 1 );

			foreach ( var one in all.Values )
			{
				if ( one.Held || one.Format != format || one.Levels != count || one.Storage != storage ) continue;
				if ( one.Extent.Width != extent.Width || one.Extent.Height != extent.Height ) continue;

				one.Held = true;
				return one.Id;
			}

			var made = Make( format, extent, count, storage );

			if ( made is null ) return 0;

			made.Held = true;
			all[made.Id] = made;

			return made.Id;
		}
	}

	//The same, in a format named outright - the screen's, for a pass whose pipelines were made for the screen
	internal static ulong AcquireIn( Format format, uint width, uint height )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !VkDevice.Ready ) return 0;

			foreach ( var one in all.Values )
			{
				if ( one.Held || one.Format != format || one.Levels != 1 || one.Storage ) continue;
				if ( one.Extent.Width != width || one.Extent.Height != height ) continue;

				one.Held = true;
				return one.Id;
			}

			var made = Make( format, new Extent2D( width, height ), 1, false );

			if ( made is null ) return 0;

			made.Held = true;
			all[made.Id] = made;

			return made.Id;
		}
	}

	//Given back: the next request of the same shape may have it.
	internal static void Release( ulong id )
	{
		lock ( VkDevice.renderLock )
		{
			if ( Get( id ) is not { } found ) return;

			found.Held = false;
			found.IdleSince = tick;
		}
	}

	//Its size, levels and format - the format as Vulkan names it, which is what a pipeline is baked against.
	internal static int Shape( ulong id, out int width, out int height, out int levels, out int format )
	{
		lock ( VkDevice.renderLock )
		{
			width = height = levels = format = 0;

			if ( Get( id ) is not { } found ) return 0;

			width = (int)found.Extent.Width;
			height = (int)found.Extent.Height;
			levels = (int)found.Levels;
			format = (int)found.Format;

			return 1;
		}
	}

	//The texture handle a shader reads it through - see VkTextures.Adopt.
	internal static ulong TextureOf( ulong id )
	{
		lock ( VkDevice.renderLock ) return Get( id )?.Handle ?? 0;
	}

	//Once a frame: pictures nobody has asked for in a while go, through the graveyard, since a frame still in flight may be reading one.
	internal static void Sweep()
	{
		lock ( VkDevice.renderLock )
		{
			tick++;

			List<Picture> old = null;

			foreach ( var one in all.Values )
			{
				if ( !one.Held && tick - one.IdleSince > KeepFrames ) ( old ??= new() ).Add( one );
			}

			if ( old is null ) return;

			foreach ( var one in old )
			{
				Drop( one, deferred: true );
				all.Remove( one.Id );
			}
		}
	}

	//Every picture, now - for a device going away, which has been waited on.
	internal static void DestroyAll()
	{
		lock ( VkDevice.renderLock )
		{
			foreach ( var one in all.Values ) Drop( one, deferred: false );

			all.Clear();

			//The formats were the old device's answers - see FormatOf.
			ForgetFormats();
		}
	}

	private static Picture Make( Format format, Extent2D extent, uint levels, bool storage )
	{
		var usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit |
			ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;

		if ( storage ) usage |= ImageUsageFlags.StorageBit;

		if ( !VkSwapchain.MakePicture( format, extent, levels, usage, out var image, out var memory, out var view ) )
		{
			VkSwapchain.DropImage( image, memory, view, false );
			VkDevice.SetText( $"a {extent.Width}x{extent.Height} {format} picture would not be made" );
			return null;
		}

		var made = new Picture
		{
			Id = nextId++,
			Image = image,
			Memory = memory,
			View = view,
			Format = format,
			Extent = extent,
			Levels = levels,
			Storage = storage,
			Level = new ImageView[levels],
			Layout = new ImageLayout[levels],
		};

		for ( var level = 0u; level < levels; ++level )
		{
			var info = new ImageViewCreateInfo
			{
				SType = StructureType.ImageViewCreateInfo,
				Image = image,
				ViewType = ImageViewType.Type2D,
				Format = format,
				SubresourceRange = new ImageSubresourceRange( ImageAspectFlags.ColorBit, level, 1, 0, 1 ),
			};

			ImageView one;

			if ( VkDevice.vk.CreateImageView( VkDevice.device, &info, null, &one ) != Result.Success )
			{
				Drop( made, deferred: false );
				VkDevice.SetText( "a picture's level would not get a view" );
				return null;
			}

			made.Level[level] = one;
		}

		made.Handle = VkTextures.Adopt( view, VkDevice.clampSampler );

		return made;
	}

	private static void Drop( Picture picture, bool deferred )
	{
		VkSwapchain.DropHandle( picture.Handle, deferred );
		picture.Handle = 0;

		foreach ( var level in picture.Level )
		{
			if ( level.Handle == 0 ) continue;

			if ( deferred ) VkDevice.RetireImage( default, default, level );
			else VkDevice.DestroyView( level );
		}

		VkSwapchain.DropImage( picture.Image, picture.Memory, picture.View, deferred );
	}
}
