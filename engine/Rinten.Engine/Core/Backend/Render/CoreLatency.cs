using Silk.NET.Vulkan;

namespace Rinten;

//Low-latency mode via AMD Anti-Lag or NVIDIA Reflex, picked by r_low_latency; Reflex is untested
internal static unsafe class CoreLatency
{
	/// <summary>Nought off, one on, two on with NVIDIA's boost - the card kept at its clocks through the gaps this makes, for a little more latency won back.</summary>
	[ConVar( "r_low_latency", ConVarFlags.Saved, Min = 0, Max = 2,
		Help = "Keep the processor from running ahead of the card: 0 off, 1 AMD Anti-Lag / NVIDIA Reflex, 2 Reflex with boost." )]
	internal static int Mode { get; set; } = 0;

	internal const string AntiLagExtension = "VK_AMD_anti_lag";
	internal const string ReflexExtension = "VK_NV_low_latency2";
	internal const string MesaLayer = "VK_LAYER_MESA_anti_lag";

	//Whether the device was opened with Anti-Lag.
	internal static bool AntiLag { get; private set; }

	//Whether the device was opened with Reflex's low latency.
	internal static bool Reflex { get; private set; }

	//Whether the instance asks for Mesa's Anti-Lag layer by name, where it is installed: always, since it does nothing until told to and the
	//saved setting is read only after the device is made - asked for only with the setting on, it was never there at a start.
	internal static bool WantsMesaLayer => OperatingSystem.IsLinux();

	//The device extensions to ask for, of those this card has - see VkDevice.CreateDevice.
	internal static IEnumerable<string> DeviceExtensions( Func<string, bool> has, uint vendor )
	{
		AntiLag = has( AntiLagExtension );
		Reflex = vendor == 0x10DE && has( ReflexExtension );

		if ( AntiLag ) yield return AntiLagExtension;
		if ( Reflex ) yield return ReflexExtension;
	}

	private static PhysicalDeviceAntiLagFeaturesAMD* antiLagFeatures;

	//Anti-Lag's feature at the front of the chain the device is made from, where it has it.
	internal static void ChainFeatures( PhysicalDeviceFeatures2* features )
	{
		if ( !AntiLag ) return;

		antiLagFeatures = (PhysicalDeviceAntiLagFeaturesAMD*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(
			(nuint)sizeof( PhysicalDeviceAntiLagFeaturesAMD ) );

		antiLagFeatures->SType = StructureType.PhysicalDeviceAntiLagFeaturesAmd;
		antiLagFeatures->AntiLag = true;
		antiLagFeatures->PNext = features->PNext;
		features->PNext = antiLagFeatures;
	}

	internal static void FeaturesDone()
	{
		System.Runtime.InteropServices.NativeMemory.Free( antiLagFeatures );
		antiLagFeatures = null;
	}

	//-- the calls ------------------------------------------------------------

	private static delegate* unmanaged[Cdecl]<Device, AntiLagDataAMD*, void> antiLagUpdate;
	private static delegate* unmanaged[Cdecl]<Device, SwapchainKHR, LatencySleepModeInfoNV*, Result> setSleepMode;
	private static delegate* unmanaged[Cdecl]<Device, SwapchainKHR, LatencySleepInfoNV*, Result> latencySleep;
	private static delegate* unmanaged[Cdecl]<Device, SwapchainKHR, SetLatencyMarkerInfoNV*, void> setMarker;

	private static Semaphore sleepSemaphore;
	private static ulong sleepValue;
	private static SwapchainKHR reflexSwapchain;
	private static int modeSet = -1;
	private static bool wasOn;

	//The functions, once the device exists - where it was opened with them.
	internal static void DeviceMade()
	{
		var vk = VkDevice.vk;
		var device = VkDevice.device;

		antiLagUpdate = AntiLag ? (delegate* unmanaged[Cdecl]<Device, AntiLagDataAMD*, void>)vk.GetDeviceProcAddr( device, "vkAntiLagUpdateAMD" ).Handle : null;

		if ( AntiLag && antiLagUpdate is null ) AntiLag = false;

		if ( !Reflex ) return;

		setSleepMode = (delegate* unmanaged[Cdecl]<Device, SwapchainKHR, LatencySleepModeInfoNV*, Result>)vk.GetDeviceProcAddr( device, "vkSetLatencySleepModeNV" ).Handle;
		latencySleep = (delegate* unmanaged[Cdecl]<Device, SwapchainKHR, LatencySleepInfoNV*, Result>)vk.GetDeviceProcAddr( device, "vkLatencySleepNV" ).Handle;
		setMarker = (delegate* unmanaged[Cdecl]<Device, SwapchainKHR, SetLatencyMarkerInfoNV*, void>)vk.GetDeviceProcAddr( device, "vkSetLatencyMarkerNV" ).Handle;

		//A timeline semaphore the driver signals when the frame should start.
		var timeline = new SemaphoreTypeCreateInfo
		{
			SType = StructureType.SemaphoreTypeCreateInfo,
			SemaphoreType = SemaphoreType.Timeline,
			InitialValue = 0,
		};

		var info = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &timeline };

		Semaphore made;

		if ( setSleepMode is null || latencySleep is null || vk.CreateSemaphore( device, &info, null, &made ) != Result.Success )
		{
			Reflex = false;
			return;
		}

		sleepSemaphore = made;
		sleepValue = 0;
	}

	//Everything let go of, before the device goes.
	internal static void DeviceGoing()
	{
		if ( sleepSemaphore.Handle != 0 ) VkDevice.vk.DestroySemaphore( VkDevice.device, sleepSemaphore, null );

		sleepSemaphore = default;
		reflexSwapchain = default;
		antiLagUpdate = null;
		setSleepMode = null;
		latencySleep = null;
		setMarker = null;
		AntiLag = false;
		Reflex = false;
		modeSet = -1;
	}

	//Native, so the swapchain's chain can point at it: a static struct is the collector's to move.
	private static SwapchainLatencyCreateInfoNV* latencyInfo;

	//What a window's swapchain is made with for Reflex, chained ahead of next - or next itself where there is none.
	internal static void* SwapchainChain( void* next )
	{
		if ( !Reflex ) return next;

		if ( latencyInfo is null )
			latencyInfo = (SwapchainLatencyCreateInfoNV*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(
				(nuint)sizeof( SwapchainLatencyCreateInfoNV ) );

		latencyInfo->SType = StructureType.SwapchainLatencyCreateInfoNV;
		latencyInfo->PNext = next;
		latencyInfo->LatencyModeEnable = true;

		return latencyInfo;
	}

	//A window's swapchain was made - Reflex sleeps against the last one.
	internal static void SwapchainMade( SwapchainKHR made )
	{
		if ( !Reflex ) return;

		reflexSwapchain = made;
		modeSet = -1;
	}

	//At the top of a frame, before the input is read: where the setting is on, the processor waits here until the card is nearly free.
	internal static void BeforeInput()
	{
		var mode = Mode;
		var on = mode != 0;

		if ( !on && !wasOn ) return;

		wasOn = on;

		//The frame about to be made: the loop counts it up as it starts - see FrameId.
		var frame = FrameId + 1;

		if ( AntiLag && antiLagUpdate is not null )
		{
			UpdateAntiLag( on, AntiLagStageAMD.InputAmd, frame );
			return;
		}

		if ( !Reflex || reflexSwapchain.Handle == 0 ) return;

		lock ( VkDevice.renderLock )
		{
			if ( modeSet != mode )
			{
				var sleepMode = new LatencySleepModeInfoNV
				{
					SType = StructureType.LatencySleepModeInfoNV,
					LowLatencyMode = on,
					LowLatencyBoost = mode == 2,
					MinimumIntervalUs = 0,
				};

				setSleepMode( VkDevice.device, reflexSwapchain, &sleepMode );
				modeSet = mode;
			}

			if ( !on ) return;

			sleepValue++;

			var sleep = new LatencySleepInfoNV
			{
				SType = StructureType.LatencySleepInfoNV,
				SignalSemaphore = sleepSemaphore,
				Value = sleepValue,
			};

			if ( latencySleep( VkDevice.device, reflexSwapchain, &sleep ) != Result.Success ) return;
		}

		//Outside the lock: this is the wait, and the submit thread may need the lock to hand the card the frames it is waiting on.
		var semaphore = sleepSemaphore;
		var value = sleepValue;

		var wait = new SemaphoreWaitInfo
		{
			SType = StructureType.SemaphoreWaitInfo,
			SemaphoreCount = 1,
			PSemaphores = &semaphore,
			PValues = &value,
		};

		//A tenth of a second at most: a frame never waits longer than that for the card, and a driver that never signals must not hang the game.
		VkDevice.vk.WaitSemaphores( VkDevice.device, &wait, 100_000_000 );

		Mark( LatencyMarkerNV.SimulationStartNV, frame );
		Mark( LatencyMarkerNV.InputSampleNV, frame );
	}

	/// <summary>The frame the loop is on, as the id its presents go with - see CorePresent.Next. What both drivers tie a frame's stages together by.</summary>
	internal static ulong FrameId => (ulong)EngineLoop.LoopFrames;

	private static void UpdateAntiLag( bool on, AntiLagStageAMD stage, ulong frame )
	{
		var told = new AntiLagPresentationInfoAMD
		{
			SType = StructureType.AntiLagPresentationInfoAmd,
			Stage = stage,
			FrameIndex = frame,
		};

		var data = new AntiLagDataAMD
		{
			SType = StructureType.AntiLagDataAmd,
			Mode = on ? AntiLagModeAMD.OnAmd : AntiLagModeAMD.OffAmd,
			MaxFps = 0,
			PPresentationInfo = &told,
		};

		antiLagUpdate( VkDevice.device, &data );
	}

	/// <summary>Where the game's half of a frame ends and its drawing begins.</summary>
	internal static void RenderStarts()
	{
		Mark( LatencyMarkerNV.SimulationEndNV, FrameId );
		Mark( LatencyMarkerNV.RendersubmitStartNV, FrameId );
	}

	/// <summary>The frame's work handed to the card.</summary>
	internal static void Submitted() => Mark( LatencyMarkerNV.RendersubmitEndNV, FrameId );

	/// <summary>Around a present of the frame with this id, on whichever thread presents it.</summary>
	internal static void Presenting( ulong frame )
	{
		if ( Mode != 0 && AntiLag && antiLagUpdate is not null && frame != 0 ) UpdateAntiLag( true, AntiLagStageAMD.PresentAmd, frame );

		Mark( LatencyMarkerNV.PresentStartNV, frame );
	}

	internal static void Presented( ulong frame ) => Mark( LatencyMarkerNV.PresentEndNV, frame );

	//One of Reflex's markers, where it is on.
	private static void Mark( LatencyMarkerNV marker, ulong frame )
	{
		if ( Mode == 0 || !Reflex || setMarker is null || reflexSwapchain.Handle == 0 || frame == 0 ) return;

		var info = new SetLatencyMarkerInfoNV { SType = StructureType.SetLatencyMarkerInfoNV, PresentID = frame, Marker = marker };
		setMarker( VkDevice.device, reflexSwapchain, &info );
	}
}
