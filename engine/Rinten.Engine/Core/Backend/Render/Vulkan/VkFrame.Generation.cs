using Silk.NET.Vulkan;
using VkImage = Silk.NET.Vulkan.Image;

namespace Rinten;

//A frame generated between two drawn ones: the window's picture copied out for it, and a second image of the swapchain
//filled with what CoreFrameGeneration made, presented ahead of the frame it was made with
internal static unsafe partial class VkFrame
{
	//The window whose frame end makes a frame - set by GenerationCapture, gone with that frame's end.
	private static VkSwapchain.Swapchain generating;

	/// <summary>Generated frames handed to the display, for the report.</summary>
	internal static long GeneratedShown;

	//When the last frame with a generated one before it ended, for how far apart to show the two.
	private static long generatedLast;

	//The world as it stands in the window, before the interface, into a picture - and the frame's end told to make a frame.
	internal static int GenerationCapture( ulong id )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || Current.TargetPass || VkSwapchain.currentView is not null ) return 0;
			if ( ReferenceEquals( generating, Current ) ) return 0;
			if ( VkPictures.Get( id ) is not { } picture ) return 0;

			//Only a world that fills the window: the copy is of the whole window, and the frame made from it is shown as one
			if ( picture.Extent.Width != Current.Extent.Width || picture.Extent.Height != Current.Extent.Height ) return 0;

			//A copy inside a render pass is not a thing Vulkan allows; the next draw reopens it, loading what is there
			EnsureMainPass();

			VkDevice.dialect.EndRendering( CurrentFrame.Commands );
			Current.MainPass = false;

			ScreenInto( CurrentFrame.Commands, picture );
			generating = Current;

			return 1;
		}
	}

	//The window's image into a picture of its own size, both left as they were found.
	private static void ScreenInto( CommandBuffer commands, VkPictures.Picture picture )
	{
		var image = Current.Images[Current.ImageIndex];

		Transition( commands, image,
			ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
			PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
			PipelineStageFlags2.BlitBit, AccessFlags2.TransferReadBit );

		ReadyLevels( commands, picture );
		LevelTo( commands, picture, 0, ImageLayout.TransferDstOptimal );

		Blit( commands, image, Current.Extent, ImageLayout.TransferSrcOptimal, picture.Image, picture.Extent, ImageLayout.TransferDstOptimal );

		LevelTo( commands, picture, 0, ImageLayout.ShaderReadOnlyOptimal );

		Transition( commands, image,
			ImageLayout.TransferSrcOptimal, ImageLayout.ColorAttachmentOptimal,
			PipelineStageFlags2.BlitBit, AccessFlags2.TransferReadBit,
			PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit );
	}

	private static void Blit( CommandBuffer commands, VkImage from, Extent2D fromSize, ImageLayout fromLayout,
		VkImage into, Extent2D intoSize, ImageLayout intoLayout )
	{
		var blit = new ImageBlit
		{
			SrcSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, 0, 0, 1 ),
			DstSubresource = new ImageSubresourceLayers( ImageAspectFlags.ColorBit, 0, 0, 1 ),
		};

		blit.SrcOffsets[1] = new Offset3D( (int)fromSize.Width, (int)fromSize.Height, 1 );
		blit.DstOffsets[1] = new Offset3D( (int)intoSize.Width, (int)intoSize.Height, 1 );

		var same = fromSize.Width == intoSize.Width && fromSize.Height == intoSize.Height;

		VkDevice.vk.CmdBlitImage( commands, from, fromLayout, into, intoLayout, 1, &blit, same ? Filter.Nearest : Filter.Linear );
	}

	//At the frame's end: the window's picture copied out, the frame between made into a second image
	//False where nothing was captured or made, or no image came in time
	private static bool GenerateBeforeEnd( Frame frame, out uint image )
	{
		image = 0;

		if ( !ReferenceEquals( generating, Current ) ) return false;

		generating = null;

		if ( VkPictures.Get( CoreFrameGeneration.PresentPicture ) is not { } shown ) return false;

		ScreenInto( frame.Commands, shown );

		if ( VkPictures.Get( CoreFrameGeneration.Generate() ) is not { } made ) return false;
		if ( frame.GeneratedAvailable.Handle == 0 ) return false;

		//An image is acquired from a swapchain nobody is presenting to; two refreshes at most, or this frame goes alone
		VkSubmitter.Yield();

		var refresh = EngineLoop.DisplayRefreshRate > 1.0f ? EngineLoop.DisplayRefreshRate : 30.0f;
		var acquired = VkDevice.khrSwapchain.AcquireNextImage( VkDevice.device, Current.Handle, (ulong)( 2.0e9 / refresh ),
			frame.GeneratedAvailable, default, ref image );

		if ( acquired == Result.SuboptimalKhr ) VkSwapchain.MarkSuboptimal();
		else if ( acquired != Result.Success ) return false;

		var target = Current.Images[image];

		Transition( frame.Commands, target,
			ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
			PipelineStageFlags2.AllTransferBit, 0,
			PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit );

		LevelTo( frame.Commands, made, 0, ImageLayout.TransferSrcOptimal );

		Blit( frame.Commands, made.Image, made.Extent, ImageLayout.TransferSrcOptimal, target, Current.Extent, ImageLayout.TransferDstOptimal );

		LevelTo( frame.Commands, made, 0, ImageLayout.ShaderReadOnlyOptimal );

		Transition( frame.Commands, target,
			ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr,
			PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit,
			PipelineStageFlags2.BottomOfPipeBit, 0 );

		GeneratedShown++;
		return true;
	}

	//How long after the generated frame this one is shown, in stopwatch ticks: half the time between two drawn frames where the
	//display shows at once; nought where it keeps to its refresh, which spaces every present by a refresh already.
	private static long GeneratedSpacing()
	{
		var now = System.Diagnostics.Stopwatch.GetTimestamp();
		var since = generatedLast == 0 ? 0 : now - generatedLast;

		generatedLast = now;

		if ( Current.PresentMode is PresentModeKHR.FifoKhr or PresentModeKHR.FifoRelaxedKhr ) return 0;

		return Math.Min( since / 2, System.Diagnostics.Stopwatch.Frequency / 20 );
	}
}
