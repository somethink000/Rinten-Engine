using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Rinten;

//Hardware rays where the card has them, optional: ray queries against acceleration structures of the scene's meshes,
//which the probes' bounce is traced through (CoreLiveBounce). A card without them keeps the surfels (CoreLiveBounce)
internal static unsafe class VkRays
{
	/// <summary>Whether the device was opened with ray queries - that it can, not that it does.</summary>
	internal static bool On { get; private set; }

	//Whether the card offers it all, decided before the device opens
	private static bool offered;

	private static KhrAccelerationStructure ext;

	private static readonly string[] Extensions =
	[
		"VK_KHR_acceleration_structure", "VK_KHR_ray_query", "VK_KHR_deferred_host_operations",
		"VK_KHR_ray_tracing_position_fetch", "VK_KHR_ray_tracing_pipeline",
	];

	//The feature structs chained into the device's, alive until it is made
	private static PhysicalDeviceAccelerationStructureFeaturesKHR* structures;
	private static PhysicalDeviceRayQueryFeaturesKHR* queries;
	private static PhysicalDeviceRayTracingPositionFetchFeaturesKHR* positions;
	private static PhysicalDeviceRayTracingPipelineFeaturesKHR* pipelines;

	//Bytes a build's scratch must start on
	private static ulong scratchAlign = 256;

	/// <summary>Whether the device asks for buffer device addresses for the rays - see VkModern.ChainFeatures.</summary>
	internal static bool WantsAddresses => offered;

	[DllImport( "libc", EntryPoint = "getenv" )]
	private static extern nint GetEnv( string name );

	[DllImport( "libc", EntryPoint = "setenv" )]
	private static extern int SetEnv( string name, string value, int overwrite );

	/// <summary>Asks RADV to trace in shaders on a card without ray hardware; true where it must be asked anew.</summary>
	internal static bool AskEmulation( PhysicalDevice physical )
	{
		if ( !OperatingSystem.IsLinux() || VkDevice.HasDeviceExtension( physical, "VK_KHR_ray_query" ) ) return false;

		PhysicalDeviceProperties properties;
		VkDevice.vk.GetPhysicalDeviceProperties( physical, &properties );

		//RADV names itself in the device name; a card with the hardware would be slowed by it, so only these
		var name = Marshal.PtrToStringUTF8( (nint)properties.DeviceName ) ?? "";
		if ( properties.VendorID != 0x1002 || !name.Contains( "RADV" ) ) return false;

		try
		{
			var said = Marshal.PtrToStringUTF8( GetEnv( "RADV_PERFTEST" ) ) ?? "";
			if ( said.Contains( "emulate_rt" ) ) return false;

			SetEnv( "RADV_PERFTEST", said.Length == 0 ? "emulate_rt" : $"{said},emulate_rt", 1 );
			Log.Info( $"hardware rays: {name} has none - RADV traces them in shaders (emulate_rt)" );
			return true;
		}
		catch ( Exception e ) when ( e is DllNotFoundException or EntryPointNotFoundException )
		{
			return false;
		}
	}

	/// <summary>Asks the card, before its device opens; the full frame on 1.3 only.</summary>
	internal static void Choose( PhysicalDevice physical, bool modern, bool full )
	{
		offered = false;

		if ( !modern || !full ) return;

		foreach ( var name in Extensions )
		{
			if ( VkDevice.HasDeviceExtension( physical, name ) ) continue;

			Log.Info( $"hardware rays: off - the card has no {name}" );
			return;
		}

		var position = new PhysicalDeviceRayTracingPositionFetchFeaturesKHR { SType = StructureType.PhysicalDeviceRayTracingPositionFetchFeaturesKhr };
		var query = new PhysicalDeviceRayQueryFeaturesKHR { SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr, PNext = &position };
		var structure = new PhysicalDeviceAccelerationStructureFeaturesKHR { SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr, PNext = &query };
		var twelve = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &structure };
		var asked = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &twelve };

		VkDevice.vk.GetPhysicalDeviceFeatures2( physical, &asked );

		offered = structure.AccelerationStructure && query.RayQuery && position.RayTracingPositionFetch && twelve.BufferDeviceAddress;

		if ( !offered )
		{
			Log.Info( $"hardware rays: off - structures {structure.AccelerationStructure}, queries {query.RayQuery}, positions {position.RayTracingPositionFetch}, addresses {twelve.BufferDeviceAddress}" );
			return;
		}

		var limits = new PhysicalDeviceAccelerationStructurePropertiesKHR { SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr };
		var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &limits };
		VkDevice.vk.GetPhysicalDeviceProperties2( physical, &properties );

		scratchAlign = Math.Max( limits.MinAccelerationStructureScratchOffsetAlignment, 1u );
	}

	/// <summary>Chains the features the rays need, where the card offered them.</summary>
	internal static void ChainFeatures( PhysicalDeviceFeatures2* features )
	{
		if ( !offered ) return;

		structures = (PhysicalDeviceAccelerationStructureFeaturesKHR*)NativeMemory.AllocZeroed( (nuint)sizeof( PhysicalDeviceAccelerationStructureFeaturesKHR ) );
		queries = (PhysicalDeviceRayQueryFeaturesKHR*)NativeMemory.AllocZeroed( (nuint)sizeof( PhysicalDeviceRayQueryFeaturesKHR ) );
		positions = (PhysicalDeviceRayTracingPositionFetchFeaturesKHR*)NativeMemory.AllocZeroed( (nuint)sizeof( PhysicalDeviceRayTracingPositionFetchFeaturesKHR ) );
		pipelines = (PhysicalDeviceRayTracingPipelineFeaturesKHR*)NativeMemory.AllocZeroed( (nuint)sizeof( PhysicalDeviceRayTracingPipelineFeaturesKHR ) );

		structures->SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr;
		structures->AccelerationStructure = true;
		queries->SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr;
		queries->RayQuery = true;
		positions->SType = StructureType.PhysicalDeviceRayTracingPositionFetchFeaturesKhr;
		positions->RayTracingPositionFetch = true;

		//Its extension only: a structure made from an address is the pipeline extension's in SPIR-V
		pipelines->SType = StructureType.PhysicalDeviceRayTracingPipelineFeaturesKhr;

		structures->PNext = queries;
		queries->PNext = positions;
		positions->PNext = pipelines;
		pipelines->PNext = features->PNext;
		features->PNext = structures;
	}

	/// <summary>The device extensions the rays need, none where not offered.</summary>
	internal static string[] Wanted => offered ? Extensions : [];

	internal static void FeaturesDone()
	{
		NativeMemory.Free( structures );
		NativeMemory.Free( queries );
		NativeMemory.Free( positions );
		NativeMemory.Free( pipelines );
		structures = null;
		queries = null;
		positions = null;
		pipelines = null;
	}

	/// <summary>The device is open: its functions, if it was opened with the rays.</summary>
	internal static void DeviceMade()
	{
		On = offered && VkDevice.vk.TryGetDeviceExtension( VkDevice.instance, VkDevice.device, out ext );

		if ( On ) Log.Info( "hardware rays: the card can trace them - RenderSettings.RayTracing (r_rt) turns them on" );
	}

	/// <summary>A buffer's address on the device.</summary>
	internal static ulong AddressOf( VkBuffer buffer )
	{
		var info = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = buffer };
		return VkDevice.vk.GetBufferDeviceAddress( VkDevice.device, &info );
	}

	//-- buffers of the structures' own -------------------------------------------------------------

	internal struct Owned
	{
		public VkBuffer Buffer;
		public DeviceMemory Memory;
		public ulong Size, Address;
		public void* Mapped;
	}

	private static bool MakeBuffer( ulong size, BufferUsageFlags usage, bool host, out Owned made )
	{
		made = default;
		var vk = VkDevice.vk;

		var info = new BufferCreateInfo
		{
			SType = StructureType.BufferCreateInfo,
			Size = size,
			Usage = usage | BufferUsageFlags.ShaderDeviceAddressBit,
			SharingMode = SharingMode.Exclusive,
		};

		VkBuffer buffer;
		if ( vk.CreateBuffer( VkDevice.device, &info, null, &buffer ) != Result.Success ) return false;

		MemoryRequirements needs;
		vk.GetBufferMemoryRequirements( VkDevice.device, buffer, &needs );

		var type = host
			? VkDevice.FindMemoryType( needs.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit )
			: VkDevice.FindMemoryType( needs.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit );

		if ( type == uint.MaxValue )
		{
			vk.DestroyBuffer( VkDevice.device, buffer, null );
			return false;
		}

		var flags = new MemoryAllocateFlagsInfo { SType = StructureType.MemoryAllocateFlagsInfo, Flags = MemoryAllocateFlags.DeviceAddressBit };
		var allocate = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, PNext = &flags, AllocationSize = needs.Size, MemoryTypeIndex = type };

		DeviceMemory memory;
		if ( vk.AllocateMemory( VkDevice.device, &allocate, null, &memory ) != Result.Success )
		{
			vk.DestroyBuffer( VkDevice.device, buffer, null );
			return false;
		}

		vk.BindBufferMemory( VkDevice.device, buffer, memory, 0 );

		void* mapped = null;
		if ( host ) vk.MapMemory( VkDevice.device, memory, 0, size, 0, &mapped );

		made = new Owned { Buffer = buffer, Memory = memory, Size = size, Address = AddressOf( buffer ), Mapped = mapped };
		return true;
	}

	private static void Free( ref Owned owned )
	{
		if ( owned.Buffer.Handle == 0 ) return;

		VkDevice.vk.DestroyBuffer( VkDevice.device, owned.Buffer, null );
		VkDevice.vk.FreeMemory( VkDevice.device, owned.Memory, null );
		owned = default;
	}

	//-- the structures -----------------------------------------------------------------------------

	//One mesh's structure, built once from the pool's positions and indices
	private sealed class Bottom
	{
		public AccelerationStructureKHR Handle;
		public Owned Storage;
		public ulong Address;
	}

	private static readonly Dictionary<CoreSceneRenderer.SceneMesh, Bottom> bottoms = new();

	//The scene's: rebuilt each trace from where its instances stand
	private static AccelerationStructureKHR top;
	private static Owned topStorage, scratch, instances;
	private static int topRoom;

	//Structures outgrown, kept until teardown: a frame in flight may still read them
	private static readonly List<(AccelerationStructureKHR Handle, Owned Storage)> outgrown = new();

	/// <summary>One instance of a mesh in the scene: its world transform's rows and its record's number.</summary>
	internal struct Instance
	{
		public CoreSceneRenderer.SceneMesh Mesh;
		public System.Numerics.Matrix4x4 Transform;
		public uint Record;
	}

	/// <summary>Builds what is missing and the scene's structure into the frame; its address, or nought.</summary>
	internal static ulong Build( CommandBuffer commands, ReadOnlySpan<Instance> placed )
	{
		if ( !On || placed.Length == 0 ) return 0;

		var any = false;

		foreach ( var one in placed )
		{
			if ( bottoms.ContainsKey( one.Mesh ) ) continue;

			bottoms[one.Mesh] = BuildBottom( commands, one.Mesh );
			any = true;
		}

		//Every bottom built above, before the top reads them
		if ( any ) Barrier( commands );

		return BuildTop( commands, placed );
	}

	//Every part of a pooled mesh as a geometry; null where it has none to build
	private static Bottom BuildBottom( CommandBuffer commands, CoreSceneRenderer.SceneMesh mesh )
	{
		if ( !mesh.Pooled ) return null;

		var vertices = VkBuffers.Get( CoreGeometryPool.Vertices.Side( CoreGeometryPool.PositionSide ) );
		var indices = VkBuffers.Get( mesh.Indices );
		if ( vertices.Handle == 0 || indices.Handle == 0 ) return null;

		var vertexBase = AddressOf( vertices );
		var indexBase = AddressOf( indices );

		var parts = new List<CoreSceneRenderer.MeshDraw>();
		foreach ( var part in mesh.Meshes )
			if ( !part.StandsIn && !part.Translucent && part.IndexCount >= 3 ) parts.Add( part );

		if ( parts.Count == 0 ) return null;

		var geometries = new AccelerationStructureGeometryKHR[parts.Count];
		var counts = new uint[parts.Count];
		var ranges = new AccelerationStructureBuildRangeInfoKHR[parts.Count];

		for ( var i = 0; i < parts.Count; ++i )
		{
			var part = parts[i];
			var last = mesh.PoolVertexAt + mesh.PoolVertexCount - part.FirstVertex - 1;

			geometries[i] = new AccelerationStructureGeometryKHR
			{
				SType = StructureType.AccelerationStructureGeometryKhr,
				GeometryType = GeometryTypeKHR.TrianglesKhr,
				Flags = GeometryFlagsKHR.OpaqueBitKhr,
				Geometry = new AccelerationStructureGeometryDataKHR
				{
					Triangles = new AccelerationStructureGeometryTrianglesDataKHR
					{
						SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
						VertexFormat = Format.R32G32B32Sfloat,
						VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = vertexBase + ( (ulong)part.FirstVertex * 12ul ) },
						VertexStride = 12,
						MaxVertex = (uint)Math.Max( last, 0 ),
						IndexType = IndexType.Uint32,
						IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = indexBase + ( (ulong)part.FirstIndex * 4ul ) },
					},
				},
			};

			counts[i] = (uint)( part.IndexCount / 3 );
			ranges[i] = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = counts[i] };
		}

		fixed ( AccelerationStructureGeometryKHR* geometry = geometries )
		fixed ( uint* count = counts )
		fixed ( AccelerationStructureBuildRangeInfoKHR* range = ranges )
		{
			var build = new AccelerationStructureBuildGeometryInfoKHR
			{
				SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
				Type = AccelerationStructureTypeKHR.BottomLevelKhr,
				Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
				Mode = BuildAccelerationStructureModeKHR.BuildKhr,
				GeometryCount = (uint)parts.Count,
				PGeometries = geometry,
			};

			var sizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
			ext.GetAccelerationStructureBuildSizes( VkDevice.device, AccelerationStructureBuildTypeKHR.DeviceKhr, &build, count, &sizes );

			var made = new Bottom();
			if ( !MakeStructure( AccelerationStructureTypeKHR.BottomLevelKhr, sizes.AccelerationStructureSize, out made.Handle, out made.Storage ) ) return null;
			if ( !Scratch( sizes.BuildScratchSize ) ) return null;

			build.DstAccelerationStructure = made.Handle;
			build.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = Aligned( scratch.Address ) };

			var ranged = range;
			ext.CmdBuildAccelerationStructures( commands, 1, &build, &ranged );

			//The next build reuses the scratch
			Barrier( commands );

			made.Address = AddressOfStructure( made.Handle );
			return made;
		}
	}

	private static ulong BuildTop( CommandBuffer commands, ReadOnlySpan<Instance> placed )
	{
		var count = 0;

		if ( instances.Buffer.Handle == 0 || topRoom < placed.Length )
		{
			Free( ref instances );
			topRoom = Math.Max( placed.Length * 2, 64 );

			if ( !MakeBuffer( (ulong)( topRoom * sizeof( AccelerationStructureInstanceKHR ) ),
				BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr, true, out instances ) ) return 0;

			if ( top.Handle != 0 )
			{
				outgrown.Add( (top, topStorage) );
				top = default;
				topStorage = default;
			}
		}

		var written = (AccelerationStructureInstanceKHR*)instances.Mapped;

		foreach ( var one in placed )
		{
			if ( !bottoms.TryGetValue( one.Mesh, out var bottom ) || bottom is null ) continue;

			var m = one.Transform;
			var transform = new TransformMatrixKHR();
			var rows = (float*)&transform;

			//Rows of a 3x4, out of a matrix that carries its translation in the fourth row
			rows[0] = m.M11; rows[1] = m.M21; rows[2] = m.M31; rows[3] = m.M41;
			rows[4] = m.M12; rows[5] = m.M22; rows[6] = m.M32; rows[7] = m.M42;
			rows[8] = m.M13; rows[9] = m.M23; rows[10] = m.M33; rows[11] = m.M43;

			written[count++] = new AccelerationStructureInstanceKHR
			{
				Transform = transform,
				InstanceCustomIndex = one.Record & 0xFFFFFF,
				Mask = 0xFF,
				InstanceShaderBindingTableRecordOffset = 0,
				Flags = GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr | GeometryInstanceFlagsKHR.ForceOpaqueBitKhr,
				AccelerationStructureReference = bottom.Address,
			};
		}

		if ( count == 0 ) return 0;

		var geometry = new AccelerationStructureGeometryKHR
		{
			SType = StructureType.AccelerationStructureGeometryKhr,
			GeometryType = GeometryTypeKHR.InstancesKhr,
			Geometry = new AccelerationStructureGeometryDataKHR
			{
				Instances = new AccelerationStructureGeometryInstancesDataKHR
				{
					SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
					Data = new DeviceOrHostAddressConstKHR { DeviceAddress = instances.Address },
				},
			},
		};

		var build = new AccelerationStructureBuildGeometryInfoKHR
		{
			SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
			Type = AccelerationStructureTypeKHR.TopLevelKhr,
			Flags = BuildAccelerationStructureFlagsKHR.PreferFastBuildBitKhr,
			Mode = BuildAccelerationStructureModeKHR.BuildKhr,
			GeometryCount = 1,
			PGeometries = &geometry,
		};

		var room = (uint)topRoom;
		var sizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
		ext.GetAccelerationStructureBuildSizes( VkDevice.device, AccelerationStructureBuildTypeKHR.DeviceKhr, &build, &room, &sizes );

		if ( top.Handle == 0 && !MakeStructure( AccelerationStructureTypeKHR.TopLevelKhr, sizes.AccelerationStructureSize, out top, out topStorage ) ) return 0;
		if ( !Scratch( sizes.BuildScratchSize ) ) return 0;

		build.DstAccelerationStructure = top;
		build.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = Aligned( scratch.Address ) };

		var range = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = (uint)count };
		var ranged = &range;

		ext.CmdBuildAccelerationStructures( commands, 1, &build, &ranged );
		Barrier( commands );

		return AddressOfStructure( top );
	}

	/// <summary>A world of triangles in a structure of its own, for a bake to trace: its address, and what it holds.</summary>
	internal sealed class Soup
	{
		public ulong Address;
		internal AccelerationStructureKHR Bottom, Top;
		internal Owned BottomStorage, TopStorage, Corners, Placed, Scratch;
	}

	/// <summary>Builds a structure over triangles of three corners each, the first `solid` opaque; null where it will not build.</summary>
	internal static Soup BuildSoup( CommandBuffer commands, ReadOnlySpan<float> corners, int solid )
	{
		var triangles = corners.Length / 9;
		if ( !On || triangles == 0 ) return null;

		var soup = new Soup();

		if ( !MakeBuffer( (ulong)corners.Length * 4, BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr, true, out soup.Corners ) )
			return null;

		corners.CopyTo( new Span<float>( soup.Corners.Mapped, corners.Length ) );

		//The solid triangles opaque, the hardware's own; the rest come back to the query, each once
		Span<AccelerationStructureGeometryKHR> geometries = stackalloc AccelerationStructureGeometryKHR[2];
		Span<uint> counts = stackalloc uint[2];
		Span<AccelerationStructureBuildRangeInfoKHR> ranges = stackalloc AccelerationStructureBuildRangeInfoKHR[2];
		var used = 0;

		foreach ( var (first, count, flags) in new[] { (0, solid, GeometryFlagsKHR.OpaqueBitKhr),
			(solid, triangles - solid, GeometryFlagsKHR.NoDuplicateAnyHitInvocationBitKhr) } )
		{
			if ( count <= 0 ) continue;

			geometries[used] = new AccelerationStructureGeometryKHR
			{
				SType = StructureType.AccelerationStructureGeometryKhr,
				GeometryType = GeometryTypeKHR.TrianglesKhr,
				Flags = flags,
				Geometry = new AccelerationStructureGeometryDataKHR
				{
					Triangles = new AccelerationStructureGeometryTrianglesDataKHR
					{
						SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
						VertexFormat = Format.R32G32B32Sfloat,
						VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = soup.Corners.Address + ( (ulong)first * 36ul ) },
						VertexStride = 12,
						MaxVertex = (uint)( ( count * 3 ) - 1 ),
						IndexType = IndexType.NoneKhr,
					},
				},
			};

			counts[used] = (uint)count;
			ranges[used] = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = (uint)count };
			used++;
		}

		fixed ( AccelerationStructureGeometryKHR* geometry = geometries )
		fixed ( uint* count = counts )
		fixed ( AccelerationStructureBuildRangeInfoKHR* range = ranges )
		{
			var build = new AccelerationStructureBuildGeometryInfoKHR
			{
				SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
				Type = AccelerationStructureTypeKHR.BottomLevelKhr,
				Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
				Mode = BuildAccelerationStructureModeKHR.BuildKhr,
				GeometryCount = (uint)used,
				PGeometries = geometry,
			};

			var sizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
			ext.GetAccelerationStructureBuildSizes( VkDevice.device, AccelerationStructureBuildTypeKHR.DeviceKhr, &build, count, &sizes );

			if ( !MakeStructure( AccelerationStructureTypeKHR.BottomLevelKhr, sizes.AccelerationStructureSize, out soup.Bottom, out soup.BottomStorage )
				|| !MakeBuffer( sizes.BuildScratchSize + scratchAlign, BufferUsageFlags.StorageBufferBit, false, out soup.Scratch ) )
			{
				RetireSoup( soup );
				return null;
			}

			build.DstAccelerationStructure = soup.Bottom;
			build.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = Aligned( soup.Scratch.Address ) };

			var ranged = range;
			ext.CmdBuildAccelerationStructures( commands, 1, &build, &ranged );
			Barrier( commands );
		}

		//One instance where the world stands, both sides of every triangle
		if ( !MakeBuffer( (ulong)sizeof( AccelerationStructureInstanceKHR ), BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr, true,
			out soup.Placed ) )
		{
			RetireSoup( soup );
			return null;
		}

		var transform = new TransformMatrixKHR();
		var rows = (float*)&transform;
		rows[0] = rows[5] = rows[10] = 1.0f;

		*(AccelerationStructureInstanceKHR*)soup.Placed.Mapped = new AccelerationStructureInstanceKHR
		{
			Transform = transform,
			Mask = 0xFF,
			Flags = GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr,
			AccelerationStructureReference = AddressOfStructure( soup.Bottom ),
		};

		var instances = new AccelerationStructureGeometryKHR
		{
			SType = StructureType.AccelerationStructureGeometryKhr,
			GeometryType = GeometryTypeKHR.InstancesKhr,
			Geometry = new AccelerationStructureGeometryDataKHR
			{
				Instances = new AccelerationStructureGeometryInstancesDataKHR
				{
					SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
					Data = new DeviceOrHostAddressConstKHR { DeviceAddress = soup.Placed.Address },
				},
			},
		};

		var top = new AccelerationStructureBuildGeometryInfoKHR
		{
			SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
			Type = AccelerationStructureTypeKHR.TopLevelKhr,
			Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
			Mode = BuildAccelerationStructureModeKHR.BuildKhr,
			GeometryCount = 1,
			PGeometries = &instances,
		};

		var one = 1u;
		var topSizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
		ext.GetAccelerationStructureBuildSizes( VkDevice.device, AccelerationStructureBuildTypeKHR.DeviceKhr, &top, &one, &topSizes );

		if ( topSizes.BuildScratchSize + scratchAlign > soup.Scratch.Size
			|| !MakeStructure( AccelerationStructureTypeKHR.TopLevelKhr, topSizes.AccelerationStructureSize, out soup.Top, out soup.TopStorage ) )
		{
			RetireSoup( soup );
			return null;
		}

		top.DstAccelerationStructure = soup.Top;
		top.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = Aligned( soup.Scratch.Address ) };

		var topRange = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = 1 };
		var topRanged = &topRange;

		ext.CmdBuildAccelerationStructures( commands, 1, &top, &topRanged );
		Barrier( commands );

		soup.Address = AddressOfStructure( soup.Top );
		return soup;
	}

	/// <summary>Lets go of a soup once the work that traces it is done.</summary>
	internal static void RetireSoup( Soup soup )
	{
		if ( soup is null ) return;

		VkDevice.RetireStructure( soup.Top, soup.TopStorage.Buffer, soup.TopStorage.Memory );
		VkDevice.RetireStructure( soup.Bottom, soup.BottomStorage.Buffer, soup.BottomStorage.Memory );

		foreach ( var owned in new[] { soup.Corners, soup.Placed, soup.Scratch } )
			if ( owned.Buffer.Handle != 0 ) VkDevice.RetireBuffer( owned.Buffer, owned.Memory );

		soup.Top = default;
		soup.Bottom = default;
		soup.TopStorage = soup.BottomStorage = soup.Corners = soup.Placed = soup.Scratch = default;
	}

	/// <summary>Destroys a structure whose grave came up.</summary>
	internal static void DestroyStructure( AccelerationStructureKHR structure ) => ext.DestroyAccelerationStructure( VkDevice.device, structure, null );

	private static bool MakeStructure( AccelerationStructureTypeKHR type, ulong size, out AccelerationStructureKHR handle, out Owned storage )
	{
		handle = default;

		if ( !MakeBuffer( size, BufferUsageFlags.AccelerationStructureStorageBitKhr, false, out storage ) ) return false;

		var info = new AccelerationStructureCreateInfoKHR
		{
			SType = StructureType.AccelerationStructureCreateInfoKhr,
			Buffer = storage.Buffer,
			Size = size,
			Type = type,
		};

		AccelerationStructureKHR made;
		if ( ext.CreateAccelerationStructure( VkDevice.device, &info, null, &made ) != Result.Success )
		{
			Free( ref storage );
			return false;
		}

		handle = made;
		return true;
	}

	private static ulong AddressOfStructure( AccelerationStructureKHR handle )
	{
		var info = new AccelerationStructureDeviceAddressInfoKHR { SType = StructureType.AccelerationStructureDeviceAddressInfoKhr, AccelerationStructure = handle };
		return ext.GetAccelerationStructureDeviceAddress( VkDevice.device, &info );
	}

	//Scratch at least this big, past its alignment
	private static bool Scratch( ulong size )
	{
		var wanted = size + scratchAlign;
		if ( scratch.Buffer.Handle != 0 && scratch.Size >= wanted ) return true;

		//The old one may be in use by a build recorded this frame: kept to teardown
		if ( scratch.Buffer.Handle != 0 ) outgrown.Add( (default, scratch) );

		return MakeBuffer( Math.Max( wanted, 1ul << 20 ), BufferUsageFlags.StorageBufferBit, false, out scratch );
	}

	private static ulong Aligned( ulong address ) => ( address + scratchAlign - 1 ) & ~( scratchAlign - 1 );

	//Builds finish before what reads them: the next build, or a shader's query
	private static void Barrier( CommandBuffer commands )
	{
		var barrier = new MemoryBarrier2
		{
			SType = StructureType.MemoryBarrier2,
			SrcStageMask = PipelineStageFlags2.AccelerationStructureBuildBitKhr,
			SrcAccessMask = AccessFlags2.AccelerationStructureWriteBitKhr,
			DstStageMask = PipelineStageFlags2.AccelerationStructureBuildBitKhr | PipelineStageFlags2.ComputeShaderBit,
			DstAccessMask = AccessFlags2.AccelerationStructureReadBitKhr | AccessFlags2.AccelerationStructureWriteBitKhr,
		};

		var dependency = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
		VkDevice.dialect.Barrier( commands, &dependency );
	}

	/// <summary>Forgets a mesh's structure, which goes at teardown; a mesh given back to the pool calls this.</summary>
	internal static void Forget( CoreSceneRenderer.SceneMesh mesh )
	{
		if ( mesh is null || !bottoms.Remove( mesh, out var bottom ) || bottom is null ) return;
		outgrown.Add( (bottom.Handle, bottom.Storage) );
	}

	/// <summary>Everything off the device, which has been waited idle.</summary>
	internal static void Destroy()
	{
		if ( !On ) return;

		foreach ( var bottom in bottoms.Values )
		{
			if ( bottom is null ) continue;
			ext.DestroyAccelerationStructure( VkDevice.device, bottom.Handle, null );
			var storage = bottom.Storage;
			Free( ref storage );
		}

		bottoms.Clear();

		foreach ( var (handle, storage) in outgrown )
		{
			if ( handle.Handle != 0 ) ext.DestroyAccelerationStructure( VkDevice.device, handle, null );
			var owned = storage;
			Free( ref owned );
		}

		outgrown.Clear();

		if ( top.Handle != 0 ) ext.DestroyAccelerationStructure( VkDevice.device, top, null );
		top = default;

		Free( ref topStorage );
		Free( ref scratch );
		Free( ref instances );
		topRoom = 0;
		On = false;
	}
}
