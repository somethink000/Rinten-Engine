using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Rinten;

/// <summary>
/// NVIDIA DLSS - Super Resolution, and DLAA at its native preset - through NGX,
/// NVIDIA's own library, recorded into the engine's frame. One drawer's feature
/// of it; the library and its one initialisation a device are shared.
/// </summary>
/// <remarks>
/// NGX is a static library in NVIDIA's SDK. engine/Core/build-ngx.sh downloads
/// the SDK at a pinned release and links it, whole, into <c>librintenngx.so</c>
/// (<c>rintenngx.dll</c> on Windows) beside the engine, with NVIDIA's DLSS
/// runtime - <c>libnvidia-ngx-dlss.so.*</c> / <c>nvngx_dlss.dll</c> - next to
/// it. Its API is C throughout, so every function is fetched from that library
/// by name here, the way XrLoader fetches OpenXR's; nothing of NVIDIA's is
/// compiled into the engine, and nothing of it is in this repository - the
/// SDK's licence forbids it becoming subject to an open one.
/// <para>
/// What is handed over: the world's picture (copied to half floats where it is
/// in another format - see CopyCs), its depth, the motion MotionCs worked out,
/// and the jitter; out comes the window's picture, which the compose reads. DLSS
/// sees the world after its effects, as every upscaler here does - see
/// CoreUpscaler. The network is whichever the shipped runtime picks for the
/// preset, which r_dlss_preset may override - K is the transformer, J before it.
/// </para>
/// <para>
/// Written against SDK 310.9.1 and never run here: the machine it was written
/// on has an AMD card. r_dlss_flip exists for the two signs a first run on
/// NVIDIA may show wrong - the motion (the picture smears behind what moves)
/// and the jitter (still edges shimmer).
/// </para>
/// </remarks>
internal sealed unsafe class CoreDlss
{
	//-- the library ----------------------------------------------------------

	private static bool opened;
	private static nint library;
	private static string whyNot = "not asked yet";

	private static delegate* unmanaged[Cdecl]<uint*, byte***, uint*, byte***, int> requiredExtensions;
	private static delegate* unmanaged[Cdecl]<byte*, int, byte*, void*, nint, nint, nint, nint, nint, FeatureCommonInfo*, int, int> initWithProjectId;
	private static delegate* unmanaged[Cdecl]<nint, int> shutdown;
	private static delegate* unmanaged[Cdecl]<nint*, int> getCapabilityParameters;
	private static delegate* unmanaged[Cdecl]<nint*, int> allocateParameters;
	private static delegate* unmanaged[Cdecl]<nint, int> destroyParameters;
	private static delegate* unmanaged[Cdecl]<nint, nint, int, nint, nint*, int> createFeature;
	private static delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int> evaluateFeature;
	private static delegate* unmanaged[Cdecl]<nint, int> releaseFeature;
	private static delegate* unmanaged[Cdecl]<nint, byte*, int, void> setI;
	private static delegate* unmanaged[Cdecl]<nint, byte*, uint, void> setUI;
	private static delegate* unmanaged[Cdecl]<nint, byte*, float, void> setF;
	private static delegate* unmanaged[Cdecl]<nint, byte*, void*, void> setPointer;
	private static delegate* unmanaged[Cdecl]<nint, byte*, int*, int> getI;

	private static string LibraryName => OperatingSystem.IsWindows() ? "rintenngx.dll" : "librintenngx.so";

	//The library, opened once - false where it is not beside the engine.
	private static bool Open()
	{
		if ( opened ) return library != 0;

		opened = true;

		if ( OperatingSystem.IsBrowser() || OperatingSystem.IsAndroid() || OperatingSystem.IsMacOS() )
		{
			whyNot = "DLSS is Windows and Linux only";
			return false;
		}

		var beside = Path.Combine( NetCore.NativeDllPath, LibraryName );

		if ( !File.Exists( beside ) || !NativeLibrary.TryLoad( beside, out library ) )
		{
			library = 0;
			whyNot = $"no {LibraryName} beside the engine - engine/Core/build-ngx.sh makes it";
			return false;
		}

		nint Get( string name ) => NativeLibrary.TryGetExport( library, name, out var address ) ? address : 0;

		requiredExtensions = (delegate* unmanaged[Cdecl]<uint*, byte***, uint*, byte***, int>)Get( "NVSDK_NGX_VULKAN_RequiredExtensions" );
		initWithProjectId = (delegate* unmanaged[Cdecl]<byte*, int, byte*, void*, nint, nint, nint, nint, nint, FeatureCommonInfo*, int, int>)Get( "NVSDK_NGX_VULKAN_Init_with_ProjectID" );
		shutdown = (delegate* unmanaged[Cdecl]<nint, int>)Get( "NVSDK_NGX_VULKAN_Shutdown1" );
		getCapabilityParameters = (delegate* unmanaged[Cdecl]<nint*, int>)Get( "NVSDK_NGX_VULKAN_GetCapabilityParameters" );
		allocateParameters = (delegate* unmanaged[Cdecl]<nint*, int>)Get( "NVSDK_NGX_VULKAN_AllocateParameters" );
		destroyParameters = (delegate* unmanaged[Cdecl]<nint, int>)Get( "NVSDK_NGX_VULKAN_DestroyParameters" );
		createFeature = (delegate* unmanaged[Cdecl]<nint, nint, int, nint, nint*, int>)Get( "NVSDK_NGX_VULKAN_CreateFeature1" );
		evaluateFeature = (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)Get( "NVSDK_NGX_VULKAN_EvaluateFeature_C" );
		releaseFeature = (delegate* unmanaged[Cdecl]<nint, int>)Get( "NVSDK_NGX_VULKAN_ReleaseFeature" );
		setI = (delegate* unmanaged[Cdecl]<nint, byte*, int, void>)Get( "NVSDK_NGX_Parameter_SetI" );
		setUI = (delegate* unmanaged[Cdecl]<nint, byte*, uint, void>)Get( "NVSDK_NGX_Parameter_SetUI" );
		setF = (delegate* unmanaged[Cdecl]<nint, byte*, float, void>)Get( "NVSDK_NGX_Parameter_SetF" );
		setPointer = (delegate* unmanaged[Cdecl]<nint, byte*, void*, void>)Get( "NVSDK_NGX_Parameter_SetVoidPointer" );
		getI = (delegate* unmanaged[Cdecl]<nint, byte*, int*, int>)Get( "NVSDK_NGX_Parameter_GetI" );

		if ( requiredExtensions is null || initWithProjectId is null || shutdown is null || getCapabilityParameters is null ||
			allocateParameters is null || destroyParameters is null || createFeature is null || evaluateFeature is null ||
			releaseFeature is null || setI is null || setUI is null || setF is null || setPointer is null || getI is null )
		{
			whyNot = $"{LibraryName} is missing part of NGX";
			NativeLibrary.Free( library );
			library = 0;
			return false;
		}

		return true;
	}

	//-- what the device has to be made with -----------------------------------

	//The instance extensions NGX asks for, where its library is beside the engine - asked of every instance,
	//before anyone knows which card it will choose; an extension the loader has not got is left out by the caller.
	internal static string[] InstanceExtensions() => Extensions( instance: true );

	//The device extensions NGX asks for, on an NVIDIA card only.
	internal static string[] DeviceExtensions( uint vendor ) => vendor == NvidiaVendor ? Extensions( instance: false ) : [];

	private const uint NvidiaVendor = 0x10DE;

	private static string[] Extensions( bool instance )
	{
		if ( !Open() ) return [];

		uint instanceCount = 0, deviceCount = 0;
		byte** instanceNames = null;
		byte** deviceNames = null;

		if ( !Succeeded( requiredExtensions( &instanceCount, &instanceNames, &deviceCount, &deviceNames ) ) ) return [];

		var count = instance ? instanceCount : deviceCount;
		var names = instance ? instanceNames : deviceNames;

		if ( names is null ) return [];

		var found = new string[count];

		for ( var i = 0; i < count; i++ )
			found[i] = Marshal.PtrToStringUTF8( (nint)names[i] ) ?? "";

		return found;
	}

	//-- the device's NGX ------------------------------------------------------

	private static ulong initialisedFor;
	private static bool initialised;
	private static bool available;

	//Whether this device runs DLSS: an NVIDIA card whose driver has NGX, the library and the runtime beside the engine, and the full frame.
	internal static bool Available => EnsureInitialised() && available;

	internal static string WhyNot => whyNot;

	//NGX set up for the device the engine has now, once - asked the first time anything wants to know whether DLSS is there.
	private static bool EnsureInitialised()
	{
		var device = VkDevice.device.Handle;

		if ( initialised && initialisedFor == (ulong)device ) return available;

		initialised = true;
		initialisedFor = (ulong)device;
		available = false;

		if ( CoreRenderDevice.PortableFrame || device == 0 )
		{
			whyNot = "no full-frame Vulkan device";
			return false;
		}

		if ( !Open() ) return false;

		if ( VkDevice.VendorId != NvidiaVendor )
		{
			whyNot = "not an NVIDIA card";
			return false;
		}

		//Where NGX writes its logs, and where it looks for the DLSS runtime besides the executable's own folder - beside the engine, where build-ngx.sh put it.
		var data = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Rinten", "ngx" );

		try { Directory.CreateDirectory( data ); } catch { }

		var dataChars = WideString( data );
		var runtimeChars = WideString( NetCore.NativeDllPath );

		fixed ( byte* dataPath = dataChars )
		fixed ( byte* runtimePath = runtimeChars )
		fixed ( byte* project = Encoding.ASCII.GetBytes( ProjectId + "\0" ) )
		fixed ( byte* version = Encoding.ASCII.GetBytes( "1.0\0" ) )
		{
			var paths = stackalloc byte*[1] { runtimePath };

			var info = new FeatureCommonInfo
			{
				Paths = paths,
				PathCount = 1,
			};

			var result = initWithProjectId( project, EngineTypeCustom, version, dataPath,
				VkDevice.instance.Handle, VkDevice.physicalDevice.Handle, device,
				VkDevice.loader?.GetInstanceProcAddrPointer ?? 0, 0, &info, SdkVersion );

			if ( !Succeeded( result ) )
			{
				whyNot = $"NGX would not start ({Describe( result )}) - the driver may be too old";
				return false;
			}
		}

		//And whether this card, this driver and the runtime beside the engine do Super Resolution at all.
		nint capabilities = 0;

		if ( !Succeeded( getCapabilityParameters( &capabilities ) ) || capabilities == 0 )
		{
			whyNot = "NGX would not say what it can do";
			Shutdown();
			return false;
		}

		var there = Get( capabilities, "SuperSampling.Available\0"u8 );
		var needsDriver = Get( capabilities, "SuperSampling.NeedsUpdatedDriver\0"u8 );

		destroyParameters( capabilities );

		if ( there == 0 )
		{
			whyNot = needsDriver != 0 ? "DLSS needs a newer NVIDIA driver" : "this card or runtime has no DLSS";
			Shutdown();
			return false;
		}

		available = true;
		whyNot = "";
		Log.Info( "DLSS is available (NGX)" );

		return true;
	}

	//NGX let go of, before the device it was set up for goes - see VkDevice.Shutdown.
	internal static void DeviceGoing()
	{
		foreach ( var one in live.ToArray() ) one.Release();

		if ( initialised && available ) Shutdown();

		initialised = false;
		available = false;
	}

	private static void Shutdown()
	{
		if ( shutdown is not null && initialisedFor != 0 ) shutdown( (nint)initialisedFor );
		available = false;
	}

	//The name the engine gives NGX in place of an application id from NVIDIA - what NVSDK_NGX_VULKAN_Init_with_ProjectID asks of an engine of its own.
	private const string ProjectId = "a0f5e3c2-6b1d-4f8e-9c47-3d2b1e0f9a86";

	private const int EngineTypeCustom = 0;

	//NVSDK_NGX_Version_API of the SDK this was written against, 310.9.1.
	private const int SdkVersion = 0x15;

	//A string as NGX's wchar_t takes it: four bytes a character on Linux, two on Windows, ended by a nought.
	private static byte[] WideString( string text )
		=> OperatingSystem.IsWindows()
			? Encoding.Unicode.GetBytes( text + "\0" )
			: Encoding.UTF32.GetBytes( text + "\0" );

	private static bool Succeeded( int result ) => ( (uint)result & 0xFFF00000u ) != 0xBAD00000u;

	private static string Describe( int result ) => $"0x{(uint)result:X8}";

	private static int Get( nint parameters, ReadOnlySpan<byte> name )
	{
		var value = 0;

		fixed ( byte* key = name )
			getI( parameters, key, &value );

		return value;
	}

	[StructLayout( LayoutKind.Sequential )]
	private struct FeatureCommonInfo
	{
		public byte** Paths;
		public uint PathCount;
		public nint InternalData;
		public nint LogCallback;
		public int LogLevel;
		public byte DisableOtherSinks;
	}

	[StructLayout( LayoutKind.Sequential )]
	private struct Resource
	{
		public ulong View;
		public ulong Image;
		public uint Aspect;
		public uint BaseLevel;
		public uint LevelCount;
		public uint BaseLayer;
		public uint LayerCount;
		public int Format;
		public uint Width;
		public uint Height;
		public int Type;
		public byte ReadWrite;
	}

	//-- one drawer's feature --------------------------------------------------

	private static readonly List<CoreDlss> live = new();

	private nint feature;
	private nint parameters;
	private (int Width, int Height) madeRender;
	private (int Width, int Height) madeDisplay;
	private int madeQuality = -1;
	private int madePreset = -1;

	//The world's picture in half floats, where it is in another format - see CopyCs.
	private ulong copy;

	internal void Release()
	{
		if ( feature != 0 && releaseFeature is not null ) releaseFeature( feature );
		if ( parameters != 0 && destroyParameters is not null ) destroyParameters( parameters );

		feature = 0;
		parameters = 0;
		madeQuality = -1;
		madePreset = -1;

		CoreRenderDevice.ReleasePicture( copy );
		copy = 0;

		live.Remove( this );
	}

	//One frame of it: the world's picture, depth and motion in, the window's picture out. False where it could not, and the caller shows something else.
	internal bool Run( ulong colour, ulong depth, ulong motion, ulong output,
		(int Width, int Height) render, (int Width, int Height) display, Vector2 jitter, bool reset, float delta, ulong copyPipeline )
	{
		if ( !Available ) return false;

		if ( !VkFrame.ForeignTexture( colour, out var colourImage ) || !VkFrame.ForeignTexture( depth, out var depthImage ) ||
			!VkFrame.ForeignPicture( motion, out var motionImage ) || !VkFrame.ForeignPicture( output, out var outputImage ) )
			return false;

		//DLSS takes half floats, and the world may be in something smaller.
		if ( colourImage.Format != VkDevice.SceneFormat )
		{
			if ( copyPipeline == 0 ) return false;

			if ( copy == 0 ) copy = CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.Hdr, render.Width, render.Height, 1, storage: true );
			if ( copy == 0 ) return false;

			Span<byte> push = stackalloc byte[128];
			push.Clear();

			var floats = MemoryMarshal.Cast<byte, float>( push );
			floats[0] = render.Width;
			floats[1] = render.Height;
			floats[2] = 1.0f / render.Width;
			floats[3] = 1.0f / render.Height;
			floats[8] = CoreSceneDrawer.Indexed( colour );

			Span<ulong> outputs = stackalloc ulong[1] { copy };
			Span<int> levels = stackalloc int[1] { 0 };

			if ( !CoreRenderDevice.DispatchPass( copyPipeline, CoreSceneDrawer.WhiteTexture, outputs, levels,
				ReadOnlySpan<ulong>.Empty, push, ( render.Width + 7 ) / 8, ( render.Height + 7 ) / 8 ) )
				return false;

			if ( !VkFrame.ForeignPicture( copy, out colourImage ) ) return false;
		}

		var quality = Rinten.Rendering.DrawSettings.DlssQuality;
		var preset = Rinten.Rendering.DrawSettings.DlssPreset;

		var commands = VkFrame.ForeignBegin( output );
		if ( commands == 0 ) return false;

		try
		{
			if ( feature == 0 || madeRender != render || madeDisplay != display || madeQuality != quality || madePreset != preset )
			{
				if ( !Create( commands, render, display, quality, preset ) ) return false;
				reset = true;
			}

			var flip = Rinten.Rendering.DrawSettings.DlssFlip;
			var motionSign = ( flip & 1 ) != 0 ? -1.0f : 1.0f;
			var jitterSign = ( flip & 2 ) != 0 ? -1.0f : 1.0f;

			var colourResource = Describe( colourImage, readWrite: false );
			var depthResource = Describe( depthImage, readWrite: false );
			var motionResource = Describe( motionImage, readWrite: false );
			var outputResource = Describe( outputImage, readWrite: true );

			SetPointer( "Color\0"u8, &colourResource );
			SetPointer( "Output\0"u8, &outputResource );
			SetPointer( "Depth\0"u8, &depthResource );
			SetPointer( "MotionVectors\0"u8, &motionResource );

			//The motion is in uv, back to where each pixel was: times the world's size it is the pixels DLSS reads.
			SetF( "MV.Scale.X\0"u8, motionSign * render.Width );
			SetF( "MV.Scale.Y\0"u8, motionSign * render.Height );
			SetF( "Jitter.Offset.X\0"u8, jitterSign * jitter.x );
			SetF( "Jitter.Offset.Y\0"u8, jitterSign * jitter.y );
			SetI( "Reset\0"u8, reset ? 1 : 0 );
			SetF( "FrameTimeDeltaInMsec\0"u8, delta * 1000.0f );
			SetUI( "DLSS.Render.Subrect.Dimensions.Width\0"u8, (uint)render.Width );
			SetUI( "DLSS.Render.Subrect.Dimensions.Height\0"u8, (uint)render.Height );
			SetF( "DLSS.Pre.Exposure\0"u8, 1.0f );
			SetF( "DLSS.Exposure.Scale\0"u8, 1.0f );

			var result = evaluateFeature( commands, feature, parameters, 0 );

			if ( !Succeeded( result ) )
			{
				if ( !saidEvaluate ) Log.Warning( $"DLSS would not evaluate ({Describe( result )})" );
				saidEvaluate = true;
				return false;
			}

			return true;
		}
		finally
		{
			VkFrame.ForeignEnd( output );
		}
	}

	private static bool saidEvaluate;

	//The feature made again for these sizes, this quality and this network.
	private bool Create( nint commands, (int Width, int Height) render, (int Width, int Height) display, int quality, int preset )
	{
		if ( feature != 0 ) releaseFeature( feature );
		feature = 0;

		if ( parameters == 0 )
		{
			nint allocated = 0;
			if ( !Succeeded( allocateParameters( &allocated ) ) || allocated == 0 ) return false;
			parameters = allocated;
		}

		SetUI( "CreationNodeMask\0"u8, 1 );
		SetUI( "VisibilityNodeMask\0"u8, 1 );
		SetUI( "Width\0"u8, (uint)render.Width );
		SetUI( "Height\0"u8, (uint)render.Height );
		SetUI( "OutWidth\0"u8, (uint)display.Width );
		SetUI( "OutHeight\0"u8, (uint)display.Height );
		SetI( "PerfQualityValue\0"u8, quality switch
		{
			0 => 3, //ultra performance
			1 => 0, //performance - NVSDK_NGX_PerfQuality_Value_MaxPerf
			2 => 1, //balanced
			3 => 2, //quality - MaxQuality
			_ => 5, //DLAA
		} );

		//The world is light past white; its motion is at its own size and has no jitter in it; its depth runs
		//from one at the near plane (DepthInverted). The exposure the compose applies is DLSS's to find: AutoExposure.
		const int IsHdr = 1 << 0, MvLowRes = 1 << 1, DepthInverted = 1 << 3, AutoExposure = 1 << 6;

		SetI( "DLSS.Feature.Create.Flags\0"u8, IsHdr | MvLowRes | DepthInverted | AutoExposure );
		SetI( "DLSS.Enable.Output.Subrects\0"u8, 0 );

		//Which network each preset draws with: nought is the runtime's own choice.
		var network = (uint)Math.Max( preset, 0 );

		SetUI( "DLSS.Hint.Render.Preset.DLAA\0"u8, network );
		SetUI( "DLSS.Hint.Render.Preset.Quality\0"u8, network );
		SetUI( "DLSS.Hint.Render.Preset.Balanced\0"u8, network );
		SetUI( "DLSS.Hint.Render.Preset.Performance\0"u8, network );
		SetUI( "DLSS.Hint.Render.Preset.UltraPerformance\0"u8, network );
		SetUI( "DLSS.Hint.Render.Preset.UltraQuality\0"u8, network );

		nint made = 0;
		var result = createFeature( VkDevice.device.Handle, commands, FeatureSuperSampling, parameters, &made );

		if ( !Succeeded( result ) || made == 0 )
		{
			Log.Warning( $"DLSS would not start for {render.Width}x{render.Height} -> {display.Width}x{display.Height} ({Describe( result )})" );
			return false;
		}

		feature = made;
		madeRender = render;
		madeDisplay = display;
		madeQuality = quality;
		madePreset = preset;

		if ( !live.Contains( this ) ) live.Add( this );

		return true;
	}

	private const int FeatureSuperSampling = 1;

	private static Resource Describe( in VkFrame.ForeignImage image, bool readWrite ) => new()
	{
		View = image.View,
		Image = image.Image,
		Aspect = image.Depth ? 2u : 1u,
		BaseLevel = 0,
		LevelCount = 1,
		BaseLayer = 0,
		LayerCount = 1,
		Format = (int)image.Format,
		Width = image.Width,
		Height = image.Height,
		Type = 0,
		ReadWrite = readWrite ? (byte)1 : (byte)0,
	};

	//Names as NGX spells them, ended by a nought - literals, so a frame's dozen of them allocate nothing.

	private void SetI( ReadOnlySpan<byte> name, int value )
	{
		fixed ( byte* key = name ) setI( parameters, key, value );
	}

	private void SetUI( ReadOnlySpan<byte> name, uint value )
	{
		fixed ( byte* key = name ) setUI( parameters, key, value );
	}

	private void SetF( ReadOnlySpan<byte> name, float value )
	{
		fixed ( byte* key = name ) setF( parameters, key, value );
	}

	private void SetPointer( ReadOnlySpan<byte> name, void* value )
	{
		fixed ( byte* key = name ) setPointer( parameters, key, value );
	}
}
