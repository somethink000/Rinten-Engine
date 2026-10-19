using Silk.NET.Vulkan;

namespace Rinten;

//When a window's frames reach the screen, by present ids, and the processor kept no more than one finished frame ahead of
//the screen where it is held to the display's refresh - so a frame starts from input a refresh old, not two or three
internal static unsafe class CorePresent
{
	internal const string IdExtension = "VK_KHR_present_id";
	internal const string WaitExtension = "VK_KHR_present_wait";

	//Whether the device was opened with both.
	internal static bool Available { get; private set; }

	//The device extensions to ask for, where the card has both - see VkDevice.CreateDevice.
	internal static IEnumerable<string> DeviceExtensions( Func<string, bool> has )
	{
		Available = has( IdExtension ) && has( WaitExtension );

		if ( !Available ) yield break;

		yield return IdExtension;
		yield return WaitExtension;
	}

	private static PhysicalDevicePresentIdFeaturesKHR* idFeatures;
	private static PhysicalDevicePresentWaitFeaturesKHR* waitFeatures;

	//Both features at the front of the chain the device is made from.
	internal static void ChainFeatures( PhysicalDeviceFeatures2* features )
	{
		if ( !Available ) return;

		idFeatures = (PhysicalDevicePresentIdFeaturesKHR*)System.Runtime.InteropServices.NativeMemory.AllocZeroed( (nuint)sizeof( PhysicalDevicePresentIdFeaturesKHR ) );
		waitFeatures = (PhysicalDevicePresentWaitFeaturesKHR*)System.Runtime.InteropServices.NativeMemory.AllocZeroed( (nuint)sizeof( PhysicalDevicePresentWaitFeaturesKHR ) );

		idFeatures->SType = StructureType.PhysicalDevicePresentIDFeaturesKhr;
		idFeatures->PresentId = true;
		idFeatures->PNext = features->PNext;

		waitFeatures->SType = StructureType.PhysicalDevicePresentWaitFeaturesKhr;
		waitFeatures->PresentWait = true;
		waitFeatures->PNext = idFeatures;

		features->PNext = waitFeatures;
	}

	internal static void FeaturesDone()
	{
		System.Runtime.InteropServices.NativeMemory.Free( idFeatures );
		System.Runtime.InteropServices.NativeMemory.Free( waitFeatures );
		idFeatures = null;
		waitFeatures = null;
	}

	private static delegate* unmanaged[Cdecl]<Device, SwapchainKHR, ulong, ulong, Result> waitForPresent;

	internal static void DeviceMade()
	{
		if ( !Available ) return;

		waitForPresent = (delegate* unmanaged[Cdecl]<Device, SwapchainKHR, ulong, ulong, Result>)VkDevice.vk.GetDeviceProcAddr( VkDevice.device, "vkWaitForPresentKHR" ).Handle;

		if ( waitForPresent is null ) Available = false;
	}

	internal static void DeviceGoing()
	{
		waitForPresent = null;
		Available = false;
		last = default;
		lastId = 0;
	}

	//The last swapchain presented to that shows at the refresh, and the id that went with it - written by the frame's own thread
	//as it hands a present over. A frame presents several windows; one that shows at once is not what to pace against.
	private static SwapchainKHR last;
	private static ulong lastId;

	/// <summary>Stops pacing against a swapchain about to go.</summary>
	internal static void Forget( SwapchainKHR swapchain )
	{
		if ( last.Handle == swapchain.Handle ) last = default;
	}

	/// <summary>The id the next present of a swapchain goes with, nought where there are none. Remembered as the one to pace against where it shows at the refresh.</summary>
	internal static ulong Next( VkSwapchain.Swapchain swapchain )
	{
		if ( !Available || swapchain is null ) return 0;

		//The loop's frame where it can be, so a frame's id is known before it is presented - see CoreLatency.FrameId; still rising when one window presents twice.
		var id = Math.Max( swapchain.PresentIds + 1, CoreLatency.FrameId );
		swapchain.PresentIds = id;

		if ( swapchain.PresentMode is PresentModeKHR.FifoKhr or PresentModeKHR.FifoRelaxedKhr )
		{
			last = swapchain.Handle;
			lastId = id;
		}
		else if ( last.Handle == swapchain.Handle.Handle )
		{
			last = default;
		}

		return id;
	}

	/// <summary>A present told which id it is: where there is one, its info points at it. The id lives in the caller's frame until the present is made.</summary>
	internal static void Stamp( PresentInfoKHR* present, PresentIdKHR* holder, ulong* id )
	{
		if ( *id == 0 ) return;

		*holder = new PresentIdKHR { SType = StructureType.PresentIDKhr, SwapchainCount = 1, PPresentIds = id, PNext = present->PNext };
		present->PNext = holder;
	}

	//Waits in a row that ran out, and the swapchain given up on for it.
	private static int missed;
	private static ulong unpaced;

	/// <summary>How many frames waited for the screen, and for how long all told - for the report.</summary>
	internal static long Waits, WaitTicks;

	/// <summary>At the top of a frame, held to the refresh: waits until the frame before the last one handed over has been shown.</summary>
	internal static void Pace()
	{
		if ( !Available || lastId < 2 || last.Handle == 0 || last.Handle == unpaced ) return;

		//The driver's own pacing, where it is on, is the one that counts.
		if ( CoreLatency.Mode != 0 && ( CoreLatency.AntiLag || CoreLatency.Reflex ) ) return;

		//The present thread done with the last one first: a swapchain is not waited on while it is being presented to.
		VkSubmitter.Drain();

		var began = System.Diagnostics.Stopwatch.GetTimestamp();

		//Two refreshes at most: a frame shown late is still shown by then, and a window that stopped showing - minimised, covered - cannot hold the game.
		var refresh = EngineLoop.DisplayRefreshRate > 1.0f ? EngineLoop.DisplayRefreshRate : 30.0f;
		var waited = waitForPresent( VkDevice.device, last, lastId - 1, (ulong)( 2.0e9 / refresh ) );

		WaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - began;

		if ( waited == Result.Success )
		{
			Waits++;
			missed = 0;
			return;
		}

		//A compositor that never says a frame was shown - a headless one, some remote desktops - is not paced against at all after a few.
		if ( ++missed >= 3 )
		{
			unpaced = last.Handle;
			missed = 0;
			Log.Info( "frames are not paced to this window's screen: it never says when one was shown" );
		}
	}
}
