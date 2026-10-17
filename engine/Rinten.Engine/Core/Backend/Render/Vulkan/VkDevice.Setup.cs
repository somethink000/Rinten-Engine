using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;

namespace Rinten;

//Vulkan bring-up: instance, extensions, physical device, queue, heaps and debug messenger
//Function pointers come from VulkanLoader, so building needs no Vulkan SDK, only a driver to run
internal static unsafe partial class VkDevice
{
	//What the validation layers have to say, forwarded to whoever is listening.
	[System.Runtime.InteropServices.UnmanagedCallersOnly( CallConvs = [typeof( System.Runtime.CompilerServices.CallConvCdecl )] )]
	private static Bool32 OnValidationMessage( DebugUtilsMessageSeverityFlagsEXT severity,
		DebugUtilsMessageTypeFlagsEXT types, DebugUtilsMessengerCallbackDataEXT* data, void* user )
	{
		//Errors and warnings only. The informational stream is enormous and says nothing that is not visible elsewhere.
		if ( ( severity & ( DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt |
			DebugUtilsMessageSeverityFlagsEXT.WarningBitExt ) ) == 0 )
		{
			return false;
		}

		try
		{
			if ( data is not null && data->PMessage is not null )
			{
				var text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8( (nint)data->PMessage );

				if ( !string.IsNullOrEmpty( text ) )
				{
					if ( ( severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt ) != 0 ) SayError( text );
					else SayWarning( text );
				}
			}
		}
		catch
		{
			//Swallowed: an exception unwinding into a driver frame takes the process down
		}

		//False carries on; true aborts the call that produced the message, a debugging tool only
		return false;
	}

	private static bool HasLayer( string name )
	{
		uint count = 0;
		vk.EnumerateInstanceLayerProperties( ref count, null );

		if ( count == 0 ) return false;

		var layers = new LayerProperties[count];

		fixed ( LayerProperties* into = layers )
		{
			vk.EnumerateInstanceLayerProperties( ref count, into );

			for ( var i = 0u; i < count; ++i )
			{
				if ( Same( into[i].LayerName, name ) ) return true;
			}
		}

		return false;
	}

	//Whether a NUL-terminated name out of a Vulkan struct is this one.
	private static bool Same( byte* text, string name )
	{
		for ( var i = 0; i < name.Length; ++i )
		{
			if ( text[i] != (byte)name[i] ) return false;
		}

		return text[name.Length] == 0;
	}

	//The surface extensions this platform might have. Might, not does: a Linux build can be running under X11 or Wayland
	//and only finds out which when it sees a window, so it asks for both and takes whichever the loader admits to.
	private static string[] PlatformSurfaceExtensions()
	{
		if ( OperatingSystem.IsWindows() ) return ["VK_KHR_win32_surface"];
		if ( OperatingSystem.IsAndroid() ) return ["VK_KHR_android_surface"];
		if ( OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() ) return ["VK_EXT_metal_surface"];

		return ["VK_KHR_xlib_surface", "VK_KHR_wayland_surface"];
	}

	private static bool HasInstanceExtension( string name )
	{
		uint count = 0;
		vk.EnumerateInstanceExtensionProperties( (byte*)null, ref count, null );

		if ( count == 0 ) return false;

		var extensions = new ExtensionProperties[count];

		fixed ( ExtensionProperties* into = extensions )
		{
			vk.EnumerateInstanceExtensionProperties( (byte*)null, ref count, into );

			for ( var i = 0u; i < count; ++i )
			{
				if ( Same( into[i].ExtensionName, name ) ) return true;
			}
		}

		return false;
	}

	internal static bool HasDeviceExtension( PhysicalDevice candidate, string name )
		=> HasDeviceExtension( candidate, name, null );

	//The same, of what one layer adds - a layer the instance asked for by name is
	//not among those whose extensions the plain question counts; see CoreLatency.
	internal static bool HasDeviceExtension( PhysicalDevice candidate, string name, string layer )
	{
		var layerName = layer is null ? null : System.Text.Encoding.ASCII.GetBytes( layer + "\0" );

		fixed ( byte* inLayer = layerName )
		{
			uint count = 0;
			vk.EnumerateDeviceExtensionProperties( candidate, inLayer, ref count, null );

			if ( count == 0 ) return false;

			var extensions = new ExtensionProperties[count];

			fixed ( ExtensionProperties* into = extensions )
			{
				vk.EnumerateDeviceExtensionProperties( candidate, inLayer, ref count, into );

				for ( var i = 0u; i < count; ++i )
				{
					if ( Same( into[i].ExtensionName, name ) ) return true;
				}
			}
		}

		return false;
	}

	//How much we want a given device. A discrete card beats an integrated one, which beats anything else.
	//Laptops report both, and picking the first would land on the integrated one about half the time.
	private static int Score( PhysicalDevice candidate )
	{
		PhysicalDeviceProperties properties;
		vk.GetPhysicalDeviceProperties( candidate, &properties );

		return properties.DeviceType switch
		{
			PhysicalDeviceType.DiscreteGpu => 3,
			PhysicalDeviceType.IntegratedGpu => 2,
			PhysicalDeviceType.VirtualGpu => 1,
			_ => 0,
		};
	}

	//Dialects to try, best first; -portable skips the full one to preview the portable frame on desktop
	private static VkDialect[] Dialects()
		=> Rinten.Utility.CommandLine.HasSwitch( "-portable" )
			? [new VkClassic()]
			: [new VkModern(), new VkClassic()];

	//The first queue family that can draw, or uint.MaxValue.
	private static uint FindGraphicsFamily( PhysicalDevice candidate )
	{
		uint count = 0;
		vk.GetPhysicalDeviceQueueFamilyProperties( candidate, ref count, null );

		if ( count == 0 ) return uint.MaxValue;

		var families = new QueueFamilyProperties[count];

		fixed ( QueueFamilyProperties* into = families )
		{
			vk.GetPhysicalDeviceQueueFamilyProperties( candidate, ref count, into );

			for ( var i = 0u; i < count; ++i )
			{
				if ( ( into[i].QueueFlags & QueueFlags.GraphicsBit ) != 0 ) return i;
			}
		}

		return uint.MaxValue;
	}

	private static bool CreateInstance( bool wantValidation )
	{
		using var names = new Utf8Names( 16 );

		var application = new ApplicationInfo
		{
			SType = StructureType.ApplicationInfo,
			PApplicationName = names.Add( "rinten" ),
			PEngineName = names.Add( "rinten" ),

			//1.3 is where dynamic rendering and synchronisation2 became core, which between them remove
			//most of the render pass and barrier bookkeeping - where the loader speaks it; see Init.
			ApiVersion = instanceApi,
		};

		var layers = stackalloc byte*[2];
		var extensions = stackalloc byte*[24];

		var layerCount = 0u;
		var extensionCount = 0u;

		//The surface extensions, asked for here because the instance is made before anyone has mentioned a window. They
		//cost nothing when unused, and a headless run - a test, a dedicated server - simply never asks for a swapchain.
		if ( HasInstanceExtension( KhrSurface.ExtensionName ) )
		{
			extensions[extensionCount++] = names.Add( KhrSurface.ExtensionName );

			foreach ( var platform in PlatformSurfaceExtensions() )
			{
				if ( HasInstanceExtension( platform ) ) extensions[extensionCount++] = names.Add( platform );
			}
		}

		var wanted = wantValidation && HasLayer( "VK_LAYER_KHRONOS_validation" );

		if ( wanted ) layers[layerCount++] = names.Add( "VK_LAYER_KHRONOS_validation" );

		//Mesa's Anti-Lag, a layer that stays off unless asked for - see CoreLatency; by name, where the setting is on
		if ( CoreLatency.WantsMesaLayer && HasLayer( CoreLatency.MesaLayer ) )
			layers[layerCount++] = names.Add( CoreLatency.MesaLayer );

		//Enable debug utils whenever available, not just with validation: it carries labels for RGP/RenderDoc
		var hasDebugUtils = HasInstanceExtension( ExtDebugUtils.ExtensionName );

		if ( hasDebugUtils ) extensions[extensionCount++] = names.Add( ExtDebugUtils.ExtensionName );

		//What NVIDIA's NGX asks of the instance, where its library is beside the engine - DLSS is decided per
		//card, after the instance exists, so the few it wants are there whichever card is chosen. See CoreDlss.
		foreach ( var wantedByNgx in CoreDlss.InstanceExtensions() )
		{
			if ( extensionCount >= 24 || !HasInstanceExtension( wantedByNgx ) ) continue;

			var already = false;

			for ( var i = 0u; i < extensionCount && !already; i++ )
				already = System.Runtime.InteropServices.Marshal.PtrToStringUTF8( (nint)extensions[i] ) == wantedByNgx;

			if ( !already ) extensions[extensionCount++] = names.Add( wantedByNgx );
		}

		//Synchronisation validation: catches missing barriers that only flicker on some vendors
		var enabled = stackalloc ValidationFeatureEnableEXT[1];
		enabled[0] = ValidationFeatureEnableEXT.SynchronizationValidationExt;

		var validationFeatures = new ValidationFeaturesEXT
		{
			SType = StructureType.ValidationFeaturesExt,
			EnabledValidationFeatureCount = 1,
			PEnabledValidationFeatures = enabled,
		};

		var info = new InstanceCreateInfo
		{
			SType = StructureType.InstanceCreateInfo,
			PNext = wanted ? &validationFeatures : null,
			PApplicationInfo = &application,
			EnabledLayerCount = layerCount,
			PpEnabledLayerNames = layers,
			EnabledExtensionCount = extensionCount,
			PpEnabledExtensionNames = extensions,
		};

		Instance made;

		//Through the headset's runtime where there is one: it adds what it needs to what is asked for here - see CoreXr.
		if ( CoreXr.Hooked )
		{
			if ( !CoreXr.CreateVulkanInstance( &info, loader.GetInstanceProcAddrPointer, out var byRuntime, out var said ) )
			{
				SetText( $"the headset's runtime would not make a Vulkan instance ({(Result)said})" );
				return false;
			}

			made = new Instance( byRuntime );
		}
		else if ( vk.CreateInstance( &info, null, &made ) != Result.Success )
		{
			SetText( "no Vulkan instance - the driver may be too old" );
			return false;
		}

		instance = made;

		//As volkLoadInstance: every lookup from here goes through the instance, not the loader's terminator
		loader.OnInstance( instance.Handle );
		vk.CurrentInstance = instance;
		validation = wanted;

		vk.TryGetInstanceExtension( instance, out khrSurface );
		vk.TryGetInstanceExtension( instance, out khrWin32Surface );
		vk.TryGetInstanceExtension( instance, out khrXlibSurface );
		vk.TryGetInstanceExtension( instance, out khrWaylandSurface );
		vk.TryGetInstanceExtension( instance, out khrAndroidSurface );
		vk.TryGetInstanceExtension( instance, out extMetalSurface );

		if ( hasDebugUtils ) vk.TryGetInstanceExtension( instance, out extDebugUtils );

		if ( wanted && extDebugUtils is not null )
		{
			var messengerInfo = new DebugUtilsMessengerCreateInfoEXT
			{
				SType = StructureType.DebugUtilsMessengerCreateInfoExt,
				MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
				MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
					DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
					DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
				PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(
					(delegate* unmanaged[Cdecl]<DebugUtilsMessageSeverityFlagsEXT, DebugUtilsMessageTypeFlagsEXT,
						DebugUtilsMessengerCallbackDataEXT*, void*, Bool32>)&OnValidationMessage ),
			};

			DebugUtilsMessengerEXT made2;

			if ( extDebugUtils.CreateDebugUtilsMessenger( instance, &messengerInfo, null, &made2 ) == Result.Success )
				messenger = made2;
		}

		return true;
	}

	private static bool PickPhysicalDevice()
	{
		uint count = 0;
		vk.EnumeratePhysicalDevices( instance, ref count, null );

		if ( count == 0 )
		{
			SetText( "Vulkan is here but reports no devices" );
			return false;
		}

		var candidates = new PhysicalDevice[count];

		var best = -1;

		//Kept so that a machine where nothing qualifies is told which device came closest and what it was short of.
		//"No device that can draw" on a machine with a perfectly good card in it is not something anybody can act on.
		var bestRejected = -1;
		string rejection = null;

		var dialects = Dialects();

		//The headset's card, where there is one: its runtime can only composite from that card
		var headsetCard = CoreXr.Hooked ? CoreXr.PhysicalDevice( instance.Handle ) : 0;

		fixed ( PhysicalDevice* into = candidates )
		{
			vk.EnumeratePhysicalDevices( instance, ref count, into );

			for ( var i = 0u; i < count; ++i )
			{
				var candidate = into[i];

				if ( headsetCard != 0 && candidate.Handle != headsetCard ) continue;

				PhysicalDeviceProperties properties;
				vk.GetPhysicalDeviceProperties( candidate, &properties );

				var score = Score( candidate );

				//A device that cannot draw, or cannot do what CreateDevice asks, is no use however good it looks;
				//picking by type alone lands on whichever a laptop's driver listed first
				var family = FindGraphicsFamily( candidate );

				//The best way this one can be spoken to, and what it lacked for the better ones - what a rejection says
				VkDialect speaks = null;
				var missing = family == uint.MaxValue ? "a queue that can draw" : null;

				for ( var d = 0; d < dialects.Length && missing is null && speaks is null; ++d )
				{
					var lacks = dialects[d].Missing( candidate );

					if ( lacks is null ) speaks = dialects[d];
					else if ( d == dialects.Length - 1 ) missing = lacks;
				}

				if ( speaks is null )
				{
					if ( score > bestRejected )
					{
						bestRejected = score;
						rejection = $"{NameOf( &properties )} has no {missing ?? "Vulkan this engine can use"}";
					}

					continue;
				}

				//A device that holds the full frame before any that does not, whatever kind each is: a laptop's
				//integrated card on 1.3 draws everything, and a discrete one stuck on an old driver does not.
				if ( speaks.Tier == RenderTier.Full ) score += 1000;

				if ( score <= best ) continue;

				best = score;
				physicalDevice = candidate;
				graphicsFamily = family;
				dialect = speaks;
			}
		}

		if ( physicalDevice.Handle == 0 )
		{
			SetText( rejection ?? "no device that can draw" );
			return false;
		}

		{
			PhysicalDeviceProperties properties;
			vk.GetPhysicalDeviceProperties( physicalDevice, &properties );

			deviceNameText = NameOf( &properties );
			apiVersion = properties.ApiVersion;

			//Said where it is not the usual answer, because it decides what the whole frame is
			//made of and nothing else in the log would explain a game with no shadows in it.
			if ( dialect.Tier == RenderTier.Portable )
			{
				var version = (Version32)apiVersion;

				log.Info( $"{deviceNameText} speaks Vulkan {version.Major}.{version.Minor}: drawing the portable frame - see RenderTier" );
			}
		}

		return true;
	}

	private static string NameOf( PhysicalDeviceProperties* properties )
		=> System.Runtime.InteropServices.Marshal.PtrToStringUTF8( (nint)properties->DeviceName ) ?? "";

	private static bool CreateDevice()
	{
		using var names = new Utf8Names( 8 );

		var priority = 1.0f;

		var queue = new DeviceQueueCreateInfo
		{
			SType = StructureType.DeviceQueueCreateInfo,
			QueueFamilyIndex = graphicsFamily,
			QueueCount = 1,
			PQueuePriorities = &priority,
		};

		//Who made the card, and so whether NVIDIA's NGX is to be given what it asks of the device - see CoreDlss.
		//Before the features: one of its extensions is a feature of 1.2's too, and the two may not both be asked for.
		PhysicalDeviceProperties chosen;
		vk.GetPhysicalDeviceProperties( physicalDevice, &chosen );
		VendorId = chosen.VendorID;

		var ngxExtensions = CoreDlss.DeviceExtensions( chosen.VendorID );
		ngxDeviceAddress = dialect is VkModern && Array.IndexOf( ngxExtensions, "VK_EXT_buffer_device_address" ) >= 0;

		//And which of the drivers' own latency modes this card has - see CoreLatency.
		//Reflex waits on a timeline semaphore, 1.2's; the classic dialect goes without.
		var mesaLayer = CoreLatency.WantsMesaLayer && HasLayer( CoreLatency.MesaLayer );

		var latencyExtensions = CoreLatency.DeviceExtensions(
			name => ( HasDeviceExtension( physicalDevice, name ) || ( mesaLayer && HasDeviceExtension( physicalDevice, name, CoreLatency.MesaLayer ) ) ) &&
				( dialect is VkModern || name != CoreLatency.ReflexExtension ),
			chosen.VendorID ).ToArray();

		//The dialect says what it needs; what it chains stays alive until the device is made - see ChainFeatures
		var features = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2 };

		//Whether the rays are offered before the dialect chains its features: it turns on the buffer addresses they need
		VkRays.Choose( physicalDevice, dialect is VkModern, dialect.Tier == RenderTier.Full );

		dialect.ChainFeatures( physicalDevice, &features );

		//And shading an eye coarser away from where it looks, where a headset was found and the device can - see VkFovea.
		VkFovea.Choose( physicalDevice, CoreXr.Hooked, dialect is VkModern );
		VkFovea.ChainFeatures( &features );

		CoreLatency.ChainFeatures( &features );

		//And hardware rays, optional, where the card has all they need - see VkRays.
		VkRays.ChainFeatures( &features );

		//Asked about rather than demanded, which is why none of these is in a dialect's Missing: ground seen
		//at a glancing angle is a smear without the first, and that is a worse picture rather than no picture.
		PhysicalDeviceFeatures supported;
		vk.GetPhysicalDeviceFeatures( physicalDevice, &supported );

		anisotropy = supported.SamplerAnisotropy;
		features.Features.SamplerAnisotropy = anisotropy;

		//The same for block-compressed textures: without it they unpack to four bytes a pixel on the way in;
		//the formats are checked once the device exists
		features.Features.TextureCompressionBC = supported.TextureCompressionBC;

		//And what a phone has instead - ASTC, ETC2 - which a build's KTX2 pictures are
		//turned into as they load where there is no BC (plan В7; see CoreBasisTranscoder).
		features.Features.TextureCompressionAstcLdr = supported.TextureCompressionAstcLdr;
		features.Features.TextureCompressionEtc2 = supported.TextureCompressionEtc2;

		//Blending one attachment and not the other, which is what the world's pass does: the colour blends, the
		//normals never do. Asked for rather than demanded - see independentBlend for what happens without it.
		independentBlend = supported.IndependentBlend;
		features.Features.IndependentBlend = independentBlend;

		//And drawing triangles as their edges, which is what a wireframe view is. Asked for rather than demanded for
		//the same reason: without it the view draws the solid picture, which is a worse answer rather than no answer.
		fillLines = supported.FillModeNonSolid;
		features.Features.FillModeNonSolid = fillLines;

		//And an indirect draw naming where its instances start. Asked for rather than demanded: without it the pass
		//that decides copies on the card has nowhere to say which slot a command's survivors landed in, and refuses.
		firstInstance = supported.DrawIndirectFirstInstance;
		features.Features.DrawIndirectFirstInstance = firstInstance;

		//And one command drawing several of the arguments behind it, which is what the batch path records
		//a run of commands as. Asked for on the same terms: without it the run goes a command at a time.
		multiDraw = supported.MultiDrawIndirect;
		features.Features.MultiDrawIndirect = multiDraw;

		//And word-wide indices: the cluster raster packs an entry and a vertex into one index; without them it stops
		//at 2^24 and visible clusters at 131072 - see CoreMeshRender.Begin, which sizes for either
		fullIndex = supported.FullDrawIndexUint32;
		features.Features.FullDrawIndexUint32 = fullIndex;

		//And storing into the smaller float formats, which is what the bloom's chains are where it can - see
		//CompactFormat. Asked for rather than demanded: without it they are half floats, which every device stores.
		storageExtended = supported.ShaderStorageImageExtendedFormats;
		features.Features.ShaderStorageImageExtendedFormats = storageExtended;

		//Storage images without a declared format, as project compute passes can't know the engine's format
		storageWithoutFormat = supported.ShaderStorageImageWriteWithoutFormat && supported.ShaderStorageImageReadWithoutFormat;
		features.Features.ShaderStorageImageWriteWithoutFormat = storageWithoutFormat;
		features.Features.ShaderStorageImageReadWithoutFormat = storageWithoutFormat;

		//And the geometry capability SPIR-V asks of a pixel stage reading its primitive, as the cluster raster's does;
		//no geometry shader is made, but without it that read is undefined
		features.Features.GeometryShader = supported.GeometryShader;

		//Pipeline statistics queries, only when a run asked for them (see VkDevice.StatsZone)
		statsQueries = StatsZone is not null && supported.PipelineStatisticsQuery;
		features.Features.PipelineStatisticsQuery = statsQueries;

		if ( StatsZone is not null && !statsQueries )
			Log.Warning( $"RINTEN_ZONE_STATS asked for '{StatsZone}' and this device has no pipeline statistics" );

		var deviceExtensions = stackalloc byte*[32];
		var extensionCount = 0u;

		//Mesh stages, where the device has them - asked about, and the indexed draw stands in without.
		//Only on the full frame: the portable one is promised nothing past what every device has.
		var meshFeatures = new PhysicalDeviceMeshShaderFeaturesEXT { SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt };

		meshShaders = false;

		if ( dialect.Tier == RenderTier.Full && HasDeviceExtension( physicalDevice, ExtMeshShader.ExtensionName ) )
		{
			var supportedMesh = new PhysicalDeviceMeshShaderFeaturesEXT { SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt };
			var asked = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &supportedMesh };

			vk.GetPhysicalDeviceFeatures2( physicalDevice, &asked );

			//And big enough for a cluster: a workgroup of 128 making up to 128 vertices and 124 triangles - see
			//MainMs in mesh_raster.slang. Every implementation known is, the extension only promises 128/128/256.
			var limits = new PhysicalDeviceMeshShaderPropertiesEXT { SType = StructureType.PhysicalDeviceMeshShaderPropertiesExt };
			var meshProperties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &limits };

			vk.GetPhysicalDeviceProperties2( physicalDevice, &meshProperties );

			meshShaders = supportedMesh.MeshShader
				&& limits.MaxMeshOutputVertices >= 128 && limits.MaxMeshOutputPrimitives >= 124
				&& limits.MaxMeshWorkGroupInvocations >= 128 && limits.MaxMeshWorkGroupCount[0] >= 65535
				&& limits.MaxMeshWorkGroupCount[1] >= 512;
		}

		if ( meshShaders )
		{
			deviceExtensions[extensionCount++] = names.Add( ExtMeshShader.ExtensionName );
			meshFeatures.MeshShader = true;
			meshFeatures.PNext = features.PNext;
			features.PNext = &meshFeatures;
		}

		//Presenting is an extension, asked for always: a device that cannot present is a compute device
		deviceExtensions[extensionCount++] = names.Add( KhrSwapchain.ExtensionName );

		//Memory budget extension: the driver's view of our usage, which is what decides allocation failures
		memoryBudget = HasDeviceExtension( physicalDevice, "VK_EXT_memory_budget" );

		if ( memoryBudget ) deviceExtensions[extensionCount++] = names.Add( "VK_EXT_memory_budget" );

		if ( VkFovea.Extension is { } fovea ) deviceExtensions[extensionCount++] = names.Add( fovea );

		foreach ( var ray in VkRays.Wanted ) deviceExtensions[extensionCount++] = names.Add( ray );

		//NGX's extensions for DLSS where present; skip the buffer-address ext on 1.2 (it's core there)
		foreach ( var latency in latencyExtensions )
			if ( extensionCount < 24 ) deviceExtensions[extensionCount++] = names.Add( latency );

		foreach ( var wantedByNgx in ngxExtensions )
		{
			if ( ngxDeviceAddress && wantedByNgx == "VK_EXT_buffer_device_address" ) continue;
			if ( extensionCount >= 24 || !HasDeviceExtension( physicalDevice, wantedByNgx ) ) continue;

			var already = false;

			for ( var i = 0u; i < extensionCount && !already; i++ )
				already = System.Runtime.InteropServices.Marshal.PtrToStringUTF8( (nint)deviceExtensions[i] ) == wantedByNgx;

			if ( !already ) deviceExtensions[extensionCount++] = names.Add( wantedByNgx );
		}

		var info = new DeviceCreateInfo
		{
			SType = StructureType.DeviceCreateInfo,
			PNext = &features,
			QueueCreateInfoCount = 1,
			PQueueCreateInfos = &queue,
			EnabledExtensionCount = extensionCount,
			PpEnabledExtensionNames = deviceExtensions,
		};

		Device made;

		Result opened;

		//And the device the same way as the instance - see CreateInstance.
		if ( CoreXr.Hooked )
		{
			opened = CoreXr.CreateVulkanDevice( physicalDevice.Handle, &info, loader.GetInstanceProcAddrPointer, out var byRuntime, out var said )
				? Result.Success
				: said != 0 ? (Result)said : Result.ErrorInitializationFailed;

			made = new Device( byRuntime );
		}
		else
		{
			opened = vk.CreateDevice( physicalDevice, &info, null, &made );
		}

		dialect.FeaturesDone();
		VkFovea.FeaturesDone();
		CoreLatency.FeaturesDone();
		VkRays.FeaturesDone();

		if ( opened != Result.Success )
		{
			VkFovea.Forget();

			SetText( $"the device would not open as {dialect.Name} ({opened})" );
			return false;
		}

		device = made;

		//What the card offers for foveation is not said here: it is used only with a headset,
		//and on a desktop "foveated through ..." on every start read as though something were.

		//Loading the device's own function pointers skips a dispatch through the loader on
		//every call, which matters for the ones made per draw - what volkLoadDevice did.
		loader.OnDevice( device.Handle );
		vk.CurrentDevice = device;

		CoreLatency.DeviceMade();
		VkRays.DeviceMade();

		vk.TryGetDeviceExtension( instance, device, out khrSwapchain );

		if ( meshShaders && !vk.TryGetDeviceExtension( instance, device, out extMeshShader ) ) meshShaders = false;

		Queue got;
		vk.GetDeviceQueue( device, graphicsFamily, 0, &got );
		graphicsQueue = got;

		//Asked once: allocations want the memory types, the profiler a timestamp tick's worth; a queue without valid
		//timestamp bits leaves the period at nought, which tells the profiler it cannot time
		PhysicalDeviceMemoryProperties memory;
		vk.GetPhysicalDeviceMemoryProperties( physicalDevice, &memory );
		memoryProperties = memory;

		PhysicalDeviceProperties properties;
		vk.GetPhysicalDeviceProperties( physicalDevice, &properties );

		uint familyCount = 0;
		vk.GetPhysicalDeviceQueueFamilyProperties( physicalDevice, ref familyCount, null );

		if ( familyCount > 0 )
		{
			var families = new QueueFamilyProperties[familyCount];

			fixed ( QueueFamilyProperties* into = families )
			{
				vk.GetPhysicalDeviceQueueFamilyProperties( physicalDevice, ref familyCount, into );

				if ( graphicsFamily < familyCount && into[graphicsFamily].TimestampValidBits > 0 )
					timestampPeriod = properties.Limits.TimestampPeriod;
			}
		}

		//Sixteen taps is where the returns stop and every desktop card allows it; the limit is
		//read rather than assumed, because a device that allows fewer must not be asked for more.
		maxAnisotropy = properties.Limits.MaxSamplerAnisotropy < 16.0f
			? properties.Limits.MaxSamplerAnisotropy
			: 16.0f;

		//How many arguments one indirect call may name. One where the feature above was refused, whatever the
		//limit says, because the spec allows nought or one there and the limit is only about the other case.
		maxDrawIndirect = multiDraw
			? ( properties.Limits.MaxDrawIndirectCount > 0 ? properties.Limits.MaxDrawIndirectCount : 1 )
			: 1;

		//Whether a reduced-resolution world is possible: blit source and destination are optional for depth formats,
		//and a driver without them would not refuse the blit, just do something undefined
		FormatProperties depthFormat;
		vk.GetPhysicalDeviceFormatProperties( physicalDevice, DepthFormat, &depthFormat );

		const FormatFeatureFlags NeedsBlit = FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit;

		depthBlit = ( depthFormat.OptimalTilingFeatures & NeedsBlit ) == NeedsBlit;

		//And whether imported textures keep their blocks: the feature covers the family, each format is asked alone,
		//since only a format property says this device can sample it
		const FormatFeatureFlags NeedsSampling =
			FormatFeatureFlags.SampledImageBit | FormatFeatureFlags.SampledImageFilterLinearBit;

		var blockFormats = stackalloc Format[5];
		blockFormats[0] = Format.BC1RgbaUnormBlock;
		blockFormats[1] = Format.BC3UnormBlock;
		blockFormats[2] = Format.BC4UnormBlock;
		blockFormats[3] = Format.BC5UnormBlock;
		blockFormats[4] = Format.BC7UnormBlock;

		PhysicalDeviceFeatures has;
		vk.GetPhysicalDeviceFeatures( physicalDevice, &has );

		blockCompression = has.TextureCompressionBC;

		for ( var i = 0; i < 5 && blockCompression; ++i )
		{
			FormatProperties says;
			vk.GetPhysicalDeviceFormatProperties( physicalDevice, blockFormats[i], &says );

			if ( ( says.OptimalTilingFeatures & NeedsSampling ) != NeedsSampling )
			{
				blockCompression = false;
				break;
			}
		}

		//The ASTC and ETC2 a phone samples the build's pictures as, where it has no BC.
		astc = has.TextureCompressionAstcLdr && Samples( Format.Astc4x4UnormBlock ) && Samples( Format.Astc4x4SrgbBlock );
		etc2 = has.TextureCompressionEtc2 && Samples( Format.Etc2R8G8B8A8UnormBlock ) && Samples( Format.Etc2R8G8B8A8SrgbBlock );

		//What the three formats are attached and sampled as; mandatory by the spec, so this names the format
		//if a machine ever lacks one rather than failing later with nothing to go on
		if ( !RequireFormat( DepthFormat, FormatFeatureFlags.DepthStencilAttachmentBit | FormatFeatureFlags.SampledImageBit, "depth" ) ) return false;
		//The pictures between the world and the window, which the portable frame has none of - see RenderTier
		if ( Tier == RenderTier.Portable ) return MakePipelineCache();

		if ( !RequireFormat( SceneFormat, FormatFeatureFlags.ColorAttachmentBit | FormatFeatureFlags.SampledImageBit, "the frame's pictures" ) ) return false;
		//The world's picture in four bytes where the device does everything the frame does with it
		//in them - see WorldFormat - and in the half floats every device has where it does not.
		const FormatFeatureFlags NeedsWorld = FormatFeatureFlags.ColorAttachmentBit | FormatFeatureFlags.ColorAttachmentBlendBit |
			FormatFeatureFlags.SampledImageBit | FormatFeatureFlags.SampledImageFilterLinearBit | FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit;

		{
			FormatProperties shared;
			vk.GetPhysicalDeviceFormatProperties( physicalDevice, Format.E5B9G9R9UfloatPack32, &shared );

			WorldFormat = ( shared.OptimalTilingFeatures & NeedsWorld ) == NeedsWorld
				? Format.E5B9G9R9UfloatPack32
				: SceneFormat;
		}

		if ( !RequireFormat( WorldFormat, NeedsWorld, "the world's picture" ) ) return false;
		if ( !RequireFormat( NormalFormat, FormatFeatureFlags.ColorAttachmentBit | FormatFeatureFlags.SampledImageBit, "the world's normals" ) ) return false;

		//And the frame's compact pictures in thirty-two bits where the device draws into, blends, filters and stores into
		//that format - see CompactFormat - and in the world's half floats where it does not, which every device does.
		{
			const FormatFeatureFlags NeedsCompact = FormatFeatureFlags.ColorAttachmentBit |
				FormatFeatureFlags.ColorAttachmentBlendBit | FormatFeatureFlags.SampledImageBit |
				FormatFeatureFlags.SampledImageFilterLinearBit | FormatFeatureFlags.StorageImageBit;

			FormatProperties small;
			vk.GetPhysicalDeviceFormatProperties( physicalDevice, Format.B10G11R11UfloatPack32, &small );

			CompactFormat = storageExtended && ( small.OptimalTilingFeatures & NeedsCompact ) == NeedsCompact
				? Format.B10G11R11UfloatPack32
				: SceneFormat;
		}

		return MakePipelineCache();
	}

	//The cache every pipeline is baked through. Empty until the managed side hands over what it saved
	//last run; a failure here is not fatal, it only means every pipeline is compiled from scratch.
	private static bool MakePipelineCache()
	{
		var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };

		PipelineCache cache;
		if ( vk.CreatePipelineCache( device, &cacheInfo, null, &cache ) == Result.Success )
			pipelineCache = cache;

		return true;
	}

	//Whether a format is sampled and filtered with optimal tiling - what a picture needs.
	private static bool Samples( Format format )
	{
		const FormatFeatureFlags Needs = FormatFeatureFlags.SampledImageBit | FormatFeatureFlags.SampledImageFilterLinearBit;

		FormatProperties says;
		vk.GetPhysicalDeviceFormatProperties( physicalDevice, format, &says );

		return ( says.OptimalTilingFeatures & Needs ) == Needs;
	}

	private static bool RequireFormat( Format format, FormatFeatureFlags need, string name )
	{
		FormatProperties says;
		vk.GetPhysicalDeviceFormatProperties( physicalDevice, format, &says );

		if ( ( says.OptimalTilingFeatures & need ) == need ) return true;

		SetText( $"this device cannot draw into and read back {name}" );
		return false;
	}

	//-- the shadow atlases and the stand-in pictures ------------------------

	//A square picture of the device's own, this many texels a side - the sky's table.
	private static bool CreatePicture( Format format, ImageUsageFlags usage, ImageAspectFlags aspect,
		out VkImage outImage, out DeviceMemory outMemory, out ImageView outView, string name, uint side )
	{
		outImage = default;
		outMemory = default;
		outView = default;

		var imageInfo = new ImageCreateInfo
		{
			SType = StructureType.ImageCreateInfo,
			ImageType = ImageType.Type2D,
			Format = format,
			Extent = new Extent3D( side, side, 1 ),
			MipLevels = 1,
			ArrayLayers = 1,
			Samples = SampleCountFlags.Count1Bit,
			Tiling = ImageTiling.Optimal,
			Usage = usage,
			InitialLayout = ImageLayout.Undefined,
		};

		VkImage image;
		if ( vk.CreateImage( device, &imageInfo, null, &image ) != Result.Success ) return false;
		outImage = image;

		MemoryRequirements requirements;
		vk.GetImageMemoryRequirements( device, image, &requirements );

		var allocate = new MemoryAllocateInfo
		{
			SType = StructureType.MemoryAllocateInfo,
			AllocationSize = requirements.Size,
			MemoryTypeIndex = FindMemoryType( requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit ),
		};

		DeviceMemory memory;
		if ( allocate.MemoryTypeIndex == uint.MaxValue ||
			vk.AllocateMemory( device, &allocate, null, &memory ) != Result.Success )
			return false;
		outMemory = memory;

		if ( vk.BindImageMemory( device, image, memory, 0 ) != Result.Success ) return false;

		var viewInfo = new ImageViewCreateInfo
		{
			SType = StructureType.ImageViewCreateInfo,
			Image = image,
			ViewType = ImageViewType.Type2D,
			Format = format,
			SubresourceRange = new ImageSubresourceRange( aspect, 0, 1, 0, 1 ),
		};

		ImageView view;
		if ( vk.CreateImageView( device, &viewInfo, null, &view ) != Result.Success ) return false;
		outView = view;

		NameHandle( ObjectType.Image, image.Handle, name );
		return true;
	}

	//The sky's table of colour by elevation and sun angle - see TargetSkyTable.
	private static bool CreateSkyTable()
	{
		//Cleared once before anything draws it (VkFrame.ClearSkyTableOnce), so it must be a transfer's target
		if ( !CreatePicture( SceneFormat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
			ImageAspectFlags.ColorBit, out skyTableImage, out skyTableMemory, out skyTableView, "sky table", SkyTableSide ) )
			return false;

		skyTableLayout = ImageLayout.Undefined;
		skyTableHandle = 0;
		return true;
	}

	//The sky's table as a texture in the indexed table, adopted on first asking rather than when the table is made.
	internal static ulong SkyTableTexture()
	{
		if ( skyTableHandle == 0 && skyTableView.Handle != 0 )
			skyTableHandle = VkTextures.Adopt( skyTableView, clampSampler );

		return skyTableHandle;
	}

	private static void DestroySkyTable()
	{
		DestroyView( skyTableView );
		if ( skyTableImage.Handle != 0 ) vk.DestroyImage( device, skyTableImage, null );
		if ( skyTableMemory.Handle != 0 ) vk.FreeMemory( device, skyTableMemory, null );

		skyTableView = default; skyTableImage = default; skyTableMemory = default;
		skyTableHandle = 0; skyTableLayout = ImageLayout.Undefined;
	}

	//Bindless texture table setup; without descriptor indexing every index reads slot zero
	private static void MakeBindless()
	{
		//As many as the device takes, up to what is asked: the update-after-bind limits, separate from the ordinary
		//ones - a driver may allow a million of these and a few thousand bound the old way
		var indexing = new PhysicalDeviceDescriptorIndexingProperties
		{
			SType = StructureType.PhysicalDeviceDescriptorIndexingProperties,
		};

		var properties = new PhysicalDeviceProperties2
		{
			SType = StructureType.PhysicalDeviceProperties2,
			PNext = &indexing,
		};

		vk.GetPhysicalDeviceProperties2( physicalDevice, &properties );

		var allowed = BindlessSlotsWanted;

		if ( indexing.MaxDescriptorSetUpdateAfterBindSampledImages > 0 &&
			indexing.MaxDescriptorSetUpdateAfterBindSampledImages < allowed )
		{
			allowed = indexing.MaxDescriptorSetUpdateAfterBindSampledImages;
		}

		if ( indexing.MaxPerStageDescriptorUpdateAfterBindSampledImages > 0 &&
			indexing.MaxPerStageDescriptorUpdateAfterBindSampledImages < allowed )
		{
			allowed = indexing.MaxPerStageDescriptorUpdateAfterBindSampledImages;
		}

		//Below the floor the shaders clamp to, the table is not worth having
		if ( allowed < BindlessSlotsLeast ) return;

		bindlessSlots = allowed;

		var size = new DescriptorPoolSize
		{
			Type = DescriptorType.CombinedImageSampler,
			DescriptorCount = bindlessSlots,
		};

		var poolInfo = new DescriptorPoolCreateInfo
		{
			SType = StructureType.DescriptorPoolCreateInfo,

			//Its own pool because of this flag: a set written while frames that were recorded against it are still
			//in flight has to be allocated from a pool that allows it, and the pool everything else uses does not.
			Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
			MaxSets = 1,
			PoolSizeCount = 1,
			PPoolSizes = &size,
		};

		DescriptorPool pool;
		if ( vk.CreateDescriptorPool( device, &poolInfo, null, &pool ) != Result.Success ) return;

		bindlessPool = pool;

		var binding = new DescriptorSetLayoutBinding
		{
			Binding = 0,
			DescriptorType = DescriptorType.CombinedImageSampler,
			DescriptorCount = bindlessSlots,

			//And compute: a pass over the frame reads its pictures by index as a full-screen draw does
			StageFlags = DrawStages | ShaderStageFlags.ComputeBit,
		};

		var flags = DescriptorBindingFlags.PartiallyBoundBit |
			DescriptorBindingFlags.UpdateAfterBindBit |
			DescriptorBindingFlags.UpdateUnusedWhilePendingBit;

		var bindingFlags = new DescriptorSetLayoutBindingFlagsCreateInfo
		{
			SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
			BindingCount = 1,
			PBindingFlags = &flags,
		};

		var layoutInfo = new DescriptorSetLayoutCreateInfo
		{
			SType = StructureType.DescriptorSetLayoutCreateInfo,
			Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
			BindingCount = 1,
			PBindings = &binding,
			PNext = &bindingFlags,
		};

		DescriptorSetLayout layout;
		if ( vk.CreateDescriptorSetLayout( device, &layoutInfo, null, &layout ) != Result.Success ) return;

		bindlessSetLayout = layout;

		var allocate = new DescriptorSetAllocateInfo
		{
			SType = StructureType.DescriptorSetAllocateInfo,
			DescriptorPool = bindlessPool,
			DescriptorSetCount = 1,
			PSetLayouts = &layout,
		};

		DescriptorSet set;
		if ( vk.AllocateDescriptorSets( device, &allocate, &set ) != Result.Success ) return;

		bindlessSet = set;
		bindless = true;
	}

	//The sampler every material reads through, at this many taps.
	private static Sampler MakeMaterialSampler( float taps )
	{
		var samplerInfo = new SamplerCreateInfo
		{
			SType = StructureType.SamplerCreateInfo,
			MagFilter = Filter.Linear,
			MinFilter = Filter.Linear,
			MipmapMode = SamplerMipmapMode.Linear,
			AddressModeU = SamplerAddressMode.Repeat,
			AddressModeV = SamplerAddressMode.Repeat,
			AddressModeW = SamplerAddressMode.Repeat,
			MaxLod = Vk.LodClampNone,
		};

		//Anisotropic filtering keeps glancing surfaces sharp; one tap means off
		if ( taps < 1.0f ) taps = 1.0f;
		if ( taps > maxAnisotropy ) taps = maxAnisotropy;

		samplerInfo.AnisotropyEnable = anisotropy && taps > 1.0f;
		samplerInfo.MaxAnisotropy = taps;

		Sampler made;

		if ( vk.CreateSampler( device, &samplerInfo, null, &made ) != Result.Success ) return default;

		return made;
	}

	//The sampler every material reads through, made again at this many taps. Called where
	//the setting changes; false where the sampler would not be made, and the old one stays.
	internal static bool RemakeSampler( float taps )
	{
		if ( device.Handle == 0 ) return false;

		if ( taps < 1.0f ) taps = 1.0f;
		if ( taps > maxAnisotropy ) taps = maxAnisotropy;

		if ( taps == anisotropyAsked ) return true;

		var made = MakeMaterialSampler( taps );

		if ( made.Handle == 0 )
		{
			SetText( "the sampler would not be made again" );
			return false;
		}

		//Every set that names the old one has to be told, and no frame may be reading them while
		//that happens. Blunt, and right: this is a settings change, not something a frame does.
		VkSubmitter.DeviceWaitIdle();

		var old = sampler;

		sampler = made;
		anisotropyAsked = taps;

		VkTextures.RewriteMaterialSets();

		if ( old.Handle != 0 ) vk.DestroySampler( device, old, null );

		return true;
	}

	private static bool CreateSamplingInfrastructure()
	{
		sampler = MakeMaterialSampler( maxAnisotropy );

		if ( sampler.Handle == 0 ) return false;

		anisotropyAsked = maxAnisotropy;

		var samplerInfo = new SamplerCreateInfo
		{
			SType = StructureType.SamplerCreateInfo,
			MagFilter = Filter.Linear,
			MinFilter = Filter.Linear,
			MipmapMode = SamplerMipmapMode.Linear,
			MaxLod = Vk.LodClampNone,

			//Never on the post samplers below: a post pass reads a picture the size of the one it is writing,
			//straight on, where there is no angle to compensate for and the extra taps would only cost.
			AnisotropyEnable = false,
			MaxAnisotropy = 1.0f,

			//And the two the post passes read with: clamped, so a tap off the screen finds its edge
			AddressModeU = SamplerAddressMode.ClampToEdge,
			AddressModeV = SamplerAddressMode.ClampToEdge,
			AddressModeW = SamplerAddressMode.ClampToEdge,
		};

		Sampler made;

		if ( vk.CreateSampler( device, &samplerInfo, null, &made ) != Result.Success ) return false;

		clampSampler = made;

		//Depth is never averaged: halfway between near and far is where nothing is, and occlusion on it haloes
		samplerInfo.MagFilter = Filter.Nearest;
		samplerInfo.MinFilter = Filter.Nearest;
		samplerInfo.MipmapMode = SamplerMipmapMode.Nearest;

		if ( vk.CreateSampler( device, &samplerInfo, null, &made ) != Result.Success ) return false;

		pointSampler = made;

		//And the one every shadow map is read with: four texels compared and weighed, wrapped rather than clamped
		var compareInfo = new SamplerCreateInfo
		{
			SType = StructureType.SamplerCreateInfo,
			MagFilter = Filter.Linear,
			MinFilter = Filter.Linear,
			MipmapMode = SamplerMipmapMode.Nearest,
			AddressModeU = SamplerAddressMode.Repeat,
			AddressModeV = SamplerAddressMode.Repeat,
			AddressModeW = SamplerAddressMode.ClampToEdge,
			CompareEnable = true,
			CompareOp = CompareOp.LessOrEqual,
			MaxLod = 0.0f,
		};

		if ( vk.CreateSampler( device, &compareInfo, null, &made ) != Result.Success ) return false;

		shadowSampler = made;

		//Shadow maps as plain filtered depth, for the blocker search; unfiltered it made jagged penumbrae
		compareInfo.CompareEnable = false;

		if ( vk.CreateSampler( device, &compareInfo, null, &made ) != Result.Success ) return false;

		shadowDepthSampler = made;

		//Set 0, a material: four images then the numbers.
		{
			var bindings = stackalloc DescriptorSetLayoutBinding[(int)MaterialTextures + 1];

			for ( var i = 0u; i < MaterialTextures; ++i )
			{
				bindings[i].Binding = i;
				bindings[i].DescriptorType = DescriptorType.CombinedImageSampler;
				bindings[i].DescriptorCount = 1;

				//Compute and vertex stages too: post passes bind this set, and vertex shaders read material params
				bindings[i].StageFlags = DrawStages | ShaderStageFlags.ComputeBit;
			}

			bindings[MaterialTextures].Binding = MaterialTextures;
			bindings[MaterialTextures].DescriptorType = DescriptorType.UniformBuffer;
			bindings[MaterialTextures].DescriptorCount = 1;
			bindings[MaterialTextures].StageFlags = DrawStages | ShaderStageFlags.ComputeBit;

			var layoutInfo = new DescriptorSetLayoutCreateInfo
			{
				SType = StructureType.DescriptorSetLayoutCreateInfo,
				BindingCount = MaterialTextures + 1,
				PBindings = bindings,
			};

			DescriptorSetLayout layout;
			if ( vk.CreateDescriptorSetLayout( device, &layoutInfo, null, &layout ) != Result.Success ) return false;

			textureSetLayout = layout;
		}

		//And the set one level of the depth pyramid is built through: the level below to read, the level being
		//written to store into. A storage image, which the material set above has no room for and no business holding.
		{
			var levels = stackalloc DescriptorSetLayoutBinding[1 + (int)PyramidLevelsAtOnce];

			levels[0].Binding = 0;
			levels[0].DescriptorType = DescriptorType.CombinedImageSampler;
			levels[0].DescriptorCount = 1;
			levels[0].StageFlags = ShaderStageFlags.ComputeBit;

			for ( var l = 0u; l < PyramidLevelsAtOnce; ++l )
			{
				levels[1 + l].Binding = 1 + l;
				levels[1 + l].DescriptorType = DescriptorType.StorageImage;
				levels[1 + l].DescriptorCount = 1;
				levels[1 + l].StageFlags = ShaderStageFlags.ComputeBit;
			}

			var pyramidInfo = new DescriptorSetLayoutCreateInfo
			{
				SType = StructureType.DescriptorSetLayoutCreateInfo,
				BindingCount = 1 + PyramidLevelsAtOnce,
				PBindings = levels,
			};

			DescriptorSetLayout layout;
			if ( vk.CreateDescriptorSetLayout( device, &pyramidInfo, null, &layout ) != Result.Success ) return false;

			pyramidSetLayout = layout;
		}

		//The set a fold of a picture's chain runs through: two levels to read and those it stores into
		{
			var bindings = stackalloc DescriptorSetLayoutBinding[2 + (int)FoldLevelsAtOnce];

			for ( var b = 0u; b < 2 + FoldLevelsAtOnce; ++b )
			{
				bindings[b].Binding = b;
				bindings[b].DescriptorType = b < 2 ? DescriptorType.CombinedImageSampler : DescriptorType.StorageImage;
				bindings[b].DescriptorCount = 1;
				bindings[b].StageFlags = ShaderStageFlags.ComputeBit;
			}

			var glowInfo = new DescriptorSetLayoutCreateInfo
			{
				SType = StructureType.DescriptorSetLayoutCreateInfo,
				BindingCount = 2 + FoldLevelsAtOnce,
				PBindings = bindings,
			};

			DescriptorSetLayout layout;
			if ( vk.CreateDescriptorSetLayout( device, &glowInfo, null, &layout ) != Result.Success ) return false;

			foldSetLayout = layout;
		}

		//The set a compute pass over the frame's pictures stores through: levels, then buffers
		{
			var count = PassImages + PassBuffers;
			var bindings = stackalloc DescriptorSetLayoutBinding[(int)count];

			for ( var b = 0u; b < count; ++b )
			{
				bindings[b].Binding = b;
				bindings[b].DescriptorType = b < PassImages ? DescriptorType.StorageImage : DescriptorType.StorageBuffer;
				bindings[b].DescriptorCount = 1;
				bindings[b].StageFlags = ShaderStageFlags.ComputeBit;
			}

			var passInfo = new DescriptorSetLayoutCreateInfo
			{
				SType = StructureType.DescriptorSetLayoutCreateInfo,
				BindingCount = count,
				PBindings = bindings,
			};

			DescriptorSetLayout layout;
			if ( vk.CreateDescriptorSetLayout( device, &passInfo, null, &layout ) != Result.Success ) return false;

			passSetLayout = layout;
		}

		//The sampler that takes the farthest of a quad rather than the average of it - see farthestSampler.
		{
			var farthest = new SamplerReductionModeCreateInfo
			{
				SType = StructureType.SamplerReductionModeCreateInfo,
				ReductionMode = filterMinmax ? SamplerReductionMode.Max : SamplerReductionMode.WeightedAverage,
			};

			var info = new SamplerCreateInfo
			{
				SType = StructureType.SamplerCreateInfo,

				//A structure that arrived with 1.2, and says nothing but the default while no device has been
				//asked for the other mode - so a device older than that is not handed it. See VkDialect.
				PNext = Tier == RenderTier.Full ? &farthest : null,
				MagFilter = Filter.Linear,
				MinFilter = Filter.Linear,
				MipmapMode = SamplerMipmapMode.Nearest,
				AddressModeU = SamplerAddressMode.ClampToEdge,
				AddressModeV = SamplerAddressMode.ClampToEdge,
				AddressModeW = SamplerAddressMode.ClampToEdge,
				MinLod = 0.0f,
				MaxLod = 16.0f,
			};

			Sampler reduce;
			if ( vk.CreateSampler( device, &info, null, &reduce ) != Result.Success ) return false;

			farthestSampler = reduce;
		}

		//The parameters a material has when it says nothing: a middling roughness, no metal, no
		//glow, and a cutoff below anything - each in a slot of its own, the rest of them zero.
		{
			var defaults = stackalloc float[(int)MaterialParamFloats];

			for ( var i = 0; i < MaterialParamFloats; ++i ) defaults[i] = 0.0f;

			defaults[0] = 0.5f;

			if ( !MakeParamsBuffer( defaults, out defaultParams, out defaultParamsMemory ) ) return false;
		}

		//The sky table, which lit surfaces read ambient from; a 32-texel picture redrawn every frame
		if ( skyTableImage.Handle == 0 && !CreateSkyTable() ) return false;

		//Set 1: the bones of this draw, and the camera and lights of this frame.
		{
			var frameBindings = stackalloc DescriptorSetLayoutBinding[12];

			//Read by a pixel too where the slot holds a run of sprites - the portable frame's, see sprite_ps.shader
			frameBindings[0].Binding = 0;
			frameBindings[0].DescriptorType = DescriptorType.StorageBufferDynamic;
			frameBindings[0].DescriptorCount = 1;
			frameBindings[0].StageFlags = DrawStages;

			frameBindings[1].Binding = 1;
			frameBindings[1].DescriptorType = DescriptorType.StorageBufferDynamic;
			frameBindings[1].DescriptorCount = 1;
			frameBindings[1].StageFlags = DrawStages | ShaderStageFlags.ComputeBit;

			//Bindings 2-5 are unused (now in the picture table), except 2 for the portable frame's sun

			//How far each of this draw's blend shapes is pushed. A window into the same scratch the bones came out
			//of, moved by an offset of its own for the same reason theirs is: one object's worth, written this frame.
			frameBindings[2].Binding = 6;
			frameBindings[2].DescriptorType = DescriptorType.StorageBufferDynamic;
			frameBindings[2].DescriptorCount = 1;
			frameBindings[2].StageFlags = ShaderStageFlags.VertexBit;

			//Lights and light lists: a plain storage buffer, since it is always read from its start
			frameBindings[3].Binding = 7;
			frameBindings[3].DescriptorType = DescriptorType.StorageBuffer;
			frameBindings[3].DescriptorCount = 1;
			frameBindings[3].StageFlags = DrawStages | ShaderStageFlags.ComputeBit;

			//And in the portable frame the sun's pages, which have no table to be read out of there - see PortableShadowMap.
			frameBindings[4].Binding = 2;
			frameBindings[4].DescriptorType = DescriptorType.CombinedImageSampler;
			frameBindings[4].DescriptorCount = 1;
			frameBindings[4].StageFlags = ShaderStageFlags.FragmentBit;

			//And its sky's table, for the same reason - read only by g_skyTable in frame.slang
			frameBindings[5].Binding = 5;
			frameBindings[5].DescriptorType = DescriptorType.CombinedImageSampler;
			frameBindings[5].DescriptorCount = 1;
			frameBindings[5].StageFlags = ShaderStageFlags.FragmentBit;

			//And its bake's sheet and volume - see g_bakedSheet in frame.slang and CoreBakedLighting.Portable
			for ( var i = 6; i < 8; ++i )
			{
				frameBindings[i].Binding = (uint)( i + 2 );
				frameBindings[i].DescriptorType = DescriptorType.CombinedImageSampler;
				frameBindings[i].DescriptorCount = 1;
				frameBindings[i].StageFlags = ShaderStageFlags.FragmentBit;
			}

			//And the other shadow maps beside the sun's pages in 2: its other pages, its movers', the lamps' and theirs - see g_portablePages in frame.slang
			for ( var i = 8; i < 12; ++i )
			{
				frameBindings[i].Binding = (uint)( i + 2 );
				frameBindings[i].DescriptorType = DescriptorType.CombinedImageSampler;
				frameBindings[i].DescriptorCount = 1;
				frameBindings[i].StageFlags = ShaderStageFlags.FragmentBit;
			}

			var boneLayoutInfo = new DescriptorSetLayoutCreateInfo
			{
				SType = StructureType.DescriptorSetLayoutCreateInfo,
				BindingCount = Tier == RenderTier.Portable ? 12u : 4u,
				PBindings = frameBindings,
			};

			DescriptorSetLayout layout;
			if ( vk.CreateDescriptorSetLayout( device, &boneLayoutInfo, null, &layout ) != Result.Success ) return false;

			boneSetLayout = layout;
		}

		//Set 2: per-draw buffers, plain storage (no offsets); compute reuses this layout as its set 0
		{
			var objectBindings = stackalloc DescriptorSetLayoutBinding[(int)ObjectBindings];

			for ( var i = 0u; i < ObjectBindings; ++i )
			{
				objectBindings[i].Binding = i;
				objectBindings[i].DescriptorType = DescriptorType.StorageBuffer;
				objectBindings[i].DescriptorCount = 1;
				objectBindings[i].StageFlags = DrawStages | ShaderStageFlags.ComputeBit;
			}

			var objectLayoutInfo = new DescriptorSetLayoutCreateInfo
			{
				SType = StructureType.DescriptorSetLayoutCreateInfo,
				BindingCount = ObjectBindings,
				PBindings = objectBindings,
			};

			DescriptorSetLayout layout;
			if ( vk.CreateDescriptorSetLayout( device, &objectLayoutInfo, null, &layout ) != Result.Success ) return false;

			objectSetLayout = layout;
		}

		//The table of pictures is what the full frame is built on, and asking a device that has never
		//heard of descriptor indexing how big its may be is a structure it was not told to expect.
		if ( Tier == RenderTier.Full ) MakeBindless();

		{
			PhysicalDeviceProperties properties;
			vk.GetPhysicalDeviceProperties( physicalDevice, &properties );

			storageAlignment = properties.Limits.MinStorageBufferOffsetAlignment > 0
				? (uint)properties.Limits.MinStorageBufferOffsetAlignment
				: 1;

			if ( properties.Limits.MaxImageDimension2D > 0 ) maxImageSize = properties.Limits.MaxImageDimension2D;
		}

		descriptorPool = MakeDescriptorPool();

		if ( descriptorPool.Handle == 0 ) return false;

		descriptorPools.Add( new DescriptorPoolSlot { Pool = descriptorPool } );

		return true;
	}

	//Destroys whatever was made, in the reverse order. Safe on a half-built device, which is when init calls it.
	private static void Destroy()
	{
		//The swapchain holds device images and an instance surface, so it goes before both; pipelines hold only
		//the device and go after the swapchain has waited for the GPU
		VkSwapchain.DestroyEverySwapchain();

		//The graveyard next, while the device and the descriptor pool are both still here to destroy its
		//contents with. The wait is explicit because a headless run may have had no swapchain to wait through.
		if ( device.Handle != 0 ) VkSubmitter.DeviceWaitIdle();

		//The headset's session before its device - its swapchain images and the eyes' views are the device's
		CoreXr.Shutdown();

		//The portable frame's sun map is the device's own: into the graveyard before it is drained
		if ( portableNoShadow != 0 ) VkTextures.ShadowMapDestroy( portableNoShadow );
		portableNoShadow = 0;
		Array.Clear( portableShadows );

		VkRays.Destroy();

		DrainGraveyard();

		VkPipelines.DestroyPipelines();

		//And what the dialect kept to draw them in - after the pipelines made against it, before its device
		dialect?.Release();

		//After the pipelines, which were baked through it.
		if ( pipelineCache.Handle != 0 ) vk.DestroyPipelineCache( device, pipelineCache, null );

		//The host's copies of buffers on their way back, before the buffers.
		VkReadbacks.DestroyAll();

		VkBuffers.DestroyBuffers();
		//The pictures first: their handles are textures, and go with them.
		VkPictures.DestroyAll();
		VkTextures.DestroyTextures();

		trackedBufferBytes = 0;
		trackedTextureBytes = 0;
		trackedBufferCount = 0;
		trackedTextureCount = 0;

		DestroySkyTable();


		if ( defaultParams.Handle != 0 ) vk.DestroyBuffer( device, defaultParams, null );
		if ( defaultParamsMemory.Handle != 0 ) vk.FreeMemory( device, defaultParamsMemory, null );

		DestroyDescriptorPools();

		if ( textureSetLayout.Handle != 0 ) vk.DestroyDescriptorSetLayout( device, textureSetLayout, null );
		if ( pyramidSetLayout.Handle != 0 ) vk.DestroyDescriptorSetLayout( device, pyramidSetLayout, null );
		if ( foldSetLayout.Handle != 0 ) vk.DestroyDescriptorSetLayout( device, foldSetLayout, null );
		if ( passSetLayout.Handle != 0 ) vk.DestroyDescriptorSetLayout( device, passSetLayout, null );
		if ( farthestSampler.Handle != 0 ) vk.DestroySampler( device, farthestSampler, null );
		if ( boneSetLayout.Handle != 0 ) vk.DestroyDescriptorSetLayout( device, boneSetLayout, null );
		if ( objectSetLayout.Handle != 0 ) vk.DestroyDescriptorSetLayout( device, objectSetLayout, null );

		//The set goes with its pool, so only the pool and the layout are owed.
		if ( bindlessPool.Handle != 0 ) vk.DestroyDescriptorPool( device, bindlessPool, null );
		if ( bindlessSetLayout.Handle != 0 ) vk.DestroyDescriptorSetLayout( device, bindlessSetLayout, null );

		if ( pointSampler.Handle != 0 ) vk.DestroySampler( device, pointSampler, null );
		if ( shadowSampler.Handle != 0 ) vk.DestroySampler( device, shadowSampler, null );
		shadowSampler = default;
		portableNoShadow = 0;

		if ( shadowDepthSampler.Handle != 0 ) vk.DestroySampler( device, shadowDepthSampler, null );
		shadowDepthSampler = default;
		if ( clampSampler.Handle != 0 ) vk.DestroySampler( device, clampSampler, null );
		if ( sampler.Handle != 0 ) vk.DestroySampler( device, sampler, null );

		if ( device.Handle != 0 )
		{
			//Nothing may be in flight when the device goes. Waiting is the blunt version of tracking it, and correct.
			VkSubmitter.DeviceWaitIdle();

			//NGX's features and its hold on the device first - see CoreDlss.
			CoreDlss.DeviceGoing();
			CoreLatency.DeviceGoing();

			vk.DestroyDevice( device, null );
		}

		if ( messenger.Handle != 0 && extDebugUtils is not null )
			extDebugUtils.DestroyDebugUtilsMessenger( instance, messenger, null );

		if ( instance.Handle != 0 ) vk.DestroyInstance( instance, null );

		Forget();
	}

	//Everything back to what it was before init. What device = Device{} was.
	private static void Forget()
	{
		khrSurface = null;
		khrSwapchain = null;
		extMeshShader = null;
		extDebugUtils = null;
		khrWin32Surface = null;
		khrXlibSurface = null;
		khrWaylandSurface = null;
		khrAndroidSurface = null;
		extMetalSurface = null;

		apiVersion = 0;
		validation = false;
		memoryBudget = false;

		instance = default;
		messenger = default;
		physicalDevice = default;
		dialect = null;
		device = default;

		graphicsFamily = uint.MaxValue;
		graphicsQueue = default;

		sampler = default;
		clampSampler = default;
		pointSampler = default;
		farthestSampler = default;

		textureSetLayout = default;
		pyramidSetLayout = default;
		foldSetLayout = default;
		passSetLayout = default;
		descriptorPool = default;
		boneSetLayout = default;
		objectSetLayout = default;
		bindlessSetLayout = default;
		bindlessPool = default;
		bindlessSet = default;
		bindless = false;

		defaultParams = default;
		defaultParamsMemory = default;
		lightBuffer = default;



		storageAlignment = 256;
		bindlessSlots = BindlessSlotsLeast;
		maxImageSize = 16384;
		memoryProperties = default;
		timestampPeriod = 0.0f;
		pipelineCache = default;

		anisotropy = false;
		maxAnisotropy = 1.0f;
		anisotropyAsked = 1.0f;
		textureMaxEdge = 0;

		depthBlit = false;
		blockCompression = false;
		independentBlend = false;
		fillLines = false;
		firstInstance = false;
		multiDraw = false;
		fullIndex = false;
		meshShaders = false;
		drawIndirectCount = false;
		maxDrawIndirect = 1;

		loader?.OnInstance( 0 );
	}

	//-- what the managed side calls ----------------------------------------

	//Brings Vulkan up. False with a reason in infoText.
	internal static bool Init( bool enableValidation )
	{
		lock ( renderLock )
		{
			if ( isReady ) return true;

			if ( loader is null )
			{
				loader = VulkanLoader.Open( out var failure );

				if ( loader is null )
				{
					SetText( failure );
					return false;
				}
			}

			//New Vk binding per bring-up, since Silk caches function addresses from the first device
			vk = new Vk( loader.Context() );

			//Check the loader's version up front; asking an old loader for 1.3 fails with a useless error
			uint instanceVersion = Vk.Version10;

			if ( vk.EnumerateInstanceVersion( ref instanceVersion ) != Result.Success )
				instanceVersion = Vk.Version10;

			var speaks = (Version32)instanceVersion;

			if ( speaks.Major < 1 || ( speaks.Major == 1 && speaks.Minor < 1 ) )
			{
				SetText( $"the Vulkan driver speaks {speaks.Major}.{speaks.Minor} and this engine needs 1.1 at the least - update the graphics driver" );
				return false;
			}

			//As new as the loader goes and no newer than is used: asking a 1.1 loader for a 1.3 instance is refused,
			//and a device on it would be spoken to in 1.1's words whatever the instance said - see VkDialect.
			instanceApi = speaks.Minor >= 3 ? Vk.Version13 : speaks.Minor == 2 ? Vk.Version12 : Vk.Version11;

			//RADV traces rays in shaders on a card without the hardware (RDNA1), asked before its instance: a second one
			var picked = CreateInstance( enableValidation ) && PickPhysicalDevice();

			if ( picked && VkRays.AskEmulation( physicalDevice ) )
			{
				if ( messenger.Handle != 0 && extDebugUtils is not null ) extDebugUtils.DestroyDebugUtilsMessenger( instance, messenger, null );
				vk.DestroyInstance( instance, null );

				instance = default;
				messenger = default;
				physicalDevice = default;

				picked = CreateInstance( enableValidation ) && PickPhysicalDevice();
			}

			if ( !picked || !CreateDevice() )
			{
				//Whichever of the three gave up has already said so in infoText.
				Destroy();
				return false;
			}

			if ( !CreateSamplingInfrastructure() )
			{
				SetText( "the sampling infrastructure would not be made" );
				Destroy();
				return false;
			}

			var version = (Version32)apiVersion;

			SetText( $"{deviceNameText}, vulkan {version.Major}.{version.Minor}.{version.Patch}{( validation ? " (validation on)" : "" )}" );

			isLost = false;
			isReady = true;

			//The headset's session, on the device just made - see CoreXr.
			CoreXr.Bind( instance.Handle, physicalDevice.Handle, device.Handle, graphicsFamily );

			return true;
		}
	}

	internal static void Shutdown()
	{
		lock ( renderLock )
		{
			//Shutting down twice, or without having started, is an ordinary thing for a host that is not sure which it did.
			if ( instance.Handle == 0 ) return;

			isReady = false;
			isLost = false;
			Destroy();
			VkFovea.Forget();

			SetText( "shut down" );
			deviceNameText = "";
		}
	}

	//Writes four ints: the driver's Vulkan major, minor and patch, then whether the validation layers are on.
	internal static void DeviceInfo( int* outInfo )
	{
		if ( outInfo is null ) return;

		lock ( renderLock )
		{
			var version = (Version32)apiVersion;

			outInfo[0] = (int)version.Major;
			outInfo[1] = (int)version.Minor;
			outInfo[2] = (int)version.Patch;
			outInfo[3] = validation ? 1 : 0;
		}
	}

	//What is on the device, ours and the driver's.
	internal static int Memory( long* into, int count )
	{
		lock ( renderLock )
		{
			if ( into is null || count <= 0 ) return 0;
			if ( !isReady ) return 0;

			//What the driver says, if it will: VK_EXT_memory_budget answers per heap, and only the device's own heaps
			//run out - host memory it can see is the machine's problem
			long driverUsed = 0;
			long driverBudget = 0;
			long heapSize = 0;
			long localHeaps = 0;

			var budget = new PhysicalDeviceMemoryBudgetPropertiesEXT
			{
				SType = StructureType.PhysicalDeviceMemoryBudgetPropertiesExt,
			};

			var properties = new PhysicalDeviceMemoryProperties2
			{
				SType = StructureType.PhysicalDeviceMemoryProperties2,
				PNext = memoryBudget ? &budget : null,
			};

			//Asked every time rather than cached: a budget that was true a minute ago is a budget from before whatever
			//else on this machine started drawing, and a stale one is worse than none - it reads as headroom that is gone.
			vk.GetPhysicalDeviceMemoryProperties2( physicalDevice, &properties );

			for ( var at = 0u; at < properties.MemoryProperties.MemoryHeapCount; ++at )
			{
				if ( ( properties.MemoryProperties.MemoryHeaps[(int)at].Flags & MemoryHeapFlags.DeviceLocalBit ) == 0 )
					continue;

				localHeaps++;
				heapSize += (long)properties.MemoryProperties.MemoryHeaps[(int)at].Size;

				if ( memoryBudget )
				{
					driverUsed += (long)budget.HeapUsage[(int)at];
					driverBudget += (long)budget.HeapBudget[(int)at];
				}
			}

			//Bytes, in int64. The profiler's own stats are floats, which stop being exact above sixteen megabytes - fine
			//for "how many draws", useless for a number that is being compared against another number to find a gap.
			var values = stackalloc long[10];
			values[0] = trackedTextureBytes;
			values[1] = trackedBufferBytes;
			values[2] = driverUsed;
			values[3] = driverBudget;
			values[4] = heapSize;
			values[5] = localHeaps;
			values[6] = memoryBudget ? 1 : 0;
			values[7] = trackedTargetBytes;
			values[8] = VkTextures.ShadowMapBytes();
			values[9] = VkTextures.PyramidBytes();

			var give = count < 10 ? count : 10;

			for ( var i = 0; i < give; ++i ) into[i] = values[i];

			return give;
		}
	}

	//Hands the device the pipeline cache saved by a previous run. Bytes written by another vendor, another
	//card or another driver build are ignored, so a stale or foreign file costs nothing but the read.
	internal static int PipelineCacheLoad( byte* data, int size )
	{
		lock ( renderLock )
		{
			if ( !isReady ) return 0;
			if ( pipelineCache.Handle == 0 || data is null ) return 0;

			//Validate the pipeline cache header first; a foreign or corrupt blob is undefined behaviour
			const int UuidSize = 16;
			const int HeaderSize = 16 + UuidSize;

			if ( size < HeaderSize ) return 0;

			var headerLength = *(uint*)( data + 0 );
			var headerVersion = *(uint*)( data + 4 );
			var vendorId = *(uint*)( data + 8 );
			var deviceId = *(uint*)( data + 12 );

			if ( headerLength < HeaderSize || headerLength > (uint)size ) return 0;

			//VK_PIPELINE_CACHE_HEADER_VERSION_ONE.
			if ( headerVersion != 1 ) return 0;

			PhysicalDeviceProperties properties;
			vk.GetPhysicalDeviceProperties( physicalDevice, &properties );

			if ( vendorId != properties.VendorID || deviceId != properties.DeviceID ) return 0;

			//The driver's own build stamp. A driver update invalidates every compiled pipeline, and this is how it says so.
			for ( var i = 0; i < UuidSize; ++i )
			{
				if ( data[16 + i] != properties.PipelineCacheUuid[i] ) return 0;
			}

			//Merged in rather than replacing: the cache the device already has may hold pipelines
			//baked since it opened, and there is no call that loads bytes into an existing cache.
			var info = new PipelineCacheCreateInfo
			{
				SType = StructureType.PipelineCacheCreateInfo,
				InitialDataSize = (nuint)size,
				PInitialData = data,
			};

			PipelineCache loaded;

			if ( vk.CreatePipelineCache( device, &info, null, &loaded ) != Result.Success ) return 0;

			var merged = vk.MergePipelineCaches( device, pipelineCache, 1, &loaded );

			vk.DestroyPipelineCache( device, loaded, null );

			return merged == Result.Success ? 1 : 0;
		}
	}

	//Takes the pipeline cache out to be written. Called with null it answers how many bytes
	//it wants; called with a buffer that big it fills it and answers how many it wrote.
	internal static int PipelineCacheSave( byte* outData, int capacity )
	{
		lock ( renderLock )
		{
			if ( !isReady ) return 0;
			if ( pipelineCache.Handle == 0 ) return 0;

			nuint needed = 0;

			if ( vk.GetPipelineCacheData( device, pipelineCache, ref needed, null ) != Result.Success ) return 0;

			//Asked how much room it wants. The caller allocates and asks again.
			if ( outData is null || capacity <= 0 ) return (int)needed;

			if ( needed == 0 || needed > (nuint)capacity ) return 0;

			if ( vk.GetPipelineCacheData( device, pipelineCache, ref needed, outData ) != Result.Success ) return 0;

			return (int)needed;
		}
	}

	//How many pictures the indexed table holds, or nought without one.
	internal static int BindlessSlotCount()
	{
		lock ( renderLock ) return bindless ? (int)bindlessSlots : 0;
	}

	internal static int SupportsBc()
	{
		lock ( renderLock ) return blockCompression ? 1 : 0;
	}

	//The biggest edge a texture may be uploaded at; zero is no limit.
	internal static void TextureLimit( int maxEdge )
	{
		lock ( renderLock )
		{
			//A limit below a block is a texture that cannot exist: four is one compressed block
			if ( maxEdge < 0 ) maxEdge = 0;
			if ( maxEdge > 0 && maxEdge < 4 ) maxEdge = 4;

			textureMaxEdge = (uint)maxEdge;
		}
	}

	//How many taps a material's sampler spends along the axis that needs them. One is off.
	internal static int Anisotropy( float taps )
	{
		lock ( renderLock )
		{
			if ( !isReady ) return 0;

			return RemakeSampler( taps ) ? 1 : 0;
		}
	}

	internal static int FirstInstanceAllowed()
	{
		lock ( renderLock ) return firstInstance ? 1 : 0;
	}

	internal static int FullIndexAllowed()
	{
		lock ( renderLock ) return fullIndex ? 1 : 0;
	}

	internal static int DrawIndirectCountAllowed()
	{
		lock ( renderLock ) return drawIndirectCount ? 1 : 0;
	}

	//An arena of NUL-terminated UTF-8 for the name arrays Vulkan wants; freed when its call returns
	private readonly ref struct Utf8Names
	{
		private readonly byte** slots;
		private readonly int capacity;
		private readonly int* used;

		public Utf8Names( int names )
		{
			capacity = names;
			slots = (byte**)System.Runtime.InteropServices.NativeMemory.Alloc( (nuint)( names * sizeof( byte* ) ) );
			used = (int*)System.Runtime.InteropServices.NativeMemory.Alloc( sizeof( int ) );
			*used = 0;
		}

		public byte* Add( string value )
		{
			if ( *used >= capacity ) return null;

			var bytes = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc( (nuint)( value.Length * 4 + 1 ) );
			WriteUtf8( value, bytes, value.Length * 4 + 1 );

			slots[*used] = bytes;
			( *used )++;

			return bytes;
		}

		public void Dispose()
		{
			for ( var i = 0; i < *used; ++i ) System.Runtime.InteropServices.NativeMemory.Free( slots[i] );

			System.Runtime.InteropServices.NativeMemory.Free( slots );
			System.Runtime.InteropServices.NativeMemory.Free( used );
		}
	}
}
