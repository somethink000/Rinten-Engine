using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Rinten;

//Buffers on the device. One kind, usable for vertices, indices, storage, indirect arguments and either end of a copy.
//The mutable per-draw data - transforms, the camera - travels as push constants and through the frame's scratch instead.
internal static unsafe class VkBuffers
{
	private struct BufferEntry
	{
		public VkBuffer Buffer;
		public DeviceMemory Memory;
		public ulong Bytes;
	}

	//The buffers, by the handle that names them - an array and not a map, because
	//the draw path asks this twice for every draw it records. See VkHandleTable.
	private static readonly VkHandleTable<BufferEntry> buffers = new();

	//A set that names two buffers, and which two - so that destroying either can find it again.
	private struct ObjectSet
	{
		public DescriptorSet Set;
		public ulong Geometry;
		public ulong Data;
	}

	//Sets naming a pair of buffers, by the pair: a voxel chunk draws every frame from buffers that never
	//change, so its set is written once and kept while the chunk lives
	private static readonly Dictionary<ulong, ObjectSet> objectSets = new();

	//The two handles as one key; handles are never reused while their buffer lives
	private static ulong ObjectKey( ulong geometry, ulong data ) => ( geometry * 1099511628211ul ) ^ data;

	internal static VkBuffer Get( ulong handle )
		=> buffers.TryGet( handle, out var found ) ? found.Buffer : default;

	//How many bytes were asked for when it was made, or zero for no such buffer.
	internal static ulong BytesOf( ulong handle )
		=> buffers.TryGet( handle, out var found ) ? found.Bytes : 0;

	//Cached descriptor set for an object's buffers: 0 = vertices, 1 = extra data (falls back to 0)
	internal static DescriptorSet ObjectSetGet( ulong geometry, ulong data )
	{
		if ( geometry == 0 ) return default;

		//A draw with nothing else still needs a complete set, so the geometry stands in for the second binding:
		//a shader reading it gets its own vertices, wrong but bounded, unlike an unwritten descriptor
		if ( data == 0 ) data = geometry;

		var key = ObjectKey( geometry, data );

		if ( objectSets.TryGetValue( key, out var found ) ) return found.Set;

		var geometryBuffer = Get( geometry );
		var dataBuffer = Get( data );

		if ( geometryBuffer.Handle == 0 || dataBuffer.Handle == 0 ) return default;

		if ( !VkDevice.AllocateDescriptorSet( VkDevice.objectSetLayout, out var set ) ) return default;

		var infos = stackalloc DescriptorBufferInfo[(int)VkDevice.ObjectBindings];
		var writes = stackalloc WriteDescriptorSet[(int)VkDevice.ObjectBindings];

		for ( var i = 0u; i < VkDevice.ObjectBindings; ++i )
		{
			//The first two are the caller's; the rest stand the geometry in, so no descriptor is left unwritten
			infos[i].Buffer = i == 1 ? dataBuffer : geometryBuffer;
			infos[i].Offset = 0;
			infos[i].Range = Vk.WholeSize;

			writes[i].SType = StructureType.WriteDescriptorSet;
			writes[i].DstSet = set;
			writes[i].DstBinding = i;
			writes[i].DescriptorCount = 1;
			writes[i].DescriptorType = DescriptorType.StorageBuffer;
			writes[i].PBufferInfo = &infos[i];
		}

		VkDevice.vk.UpdateDescriptorSets( VkDevice.device, VkDevice.ObjectBindings, writes, 0, null );

		objectSets[key] = new ObjectSet { Set = set, Geometry = geometry, Data = data };

		return set;
	}

	//Forgets every object set. The sets belong to the pool, so this must run before the pool goes.
	internal static void DestroyObjectSets() => objectSets.Clear();

	//Destroys every buffer. Called by device shutdown.
	internal static void DestroyBuffers()
	{
		DestroyObjectSets();

		for ( var at = 0; at < buffers.Used; at++ )
		{
			if ( !buffers.Live( at ) ) continue;

			ref var held = ref buffers.At( at );

			if ( held.Buffer.Handle != 0 ) VkDevice.vk.DestroyBuffer( VkDevice.device, held.Buffer, null );
			if ( held.Memory.Handle != 0 ) VkDevice.vk.FreeMemory( VkDevice.device, held.Memory, null );
		}

		buffers.Clear();
	}

	//-- what the managed side calls ----------------------------------------

	internal static int Write( ulong buffer, long offset, byte* data, long size )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !VkDevice.Ready ) return 0;
			if ( data is null || size <= 0 || offset < 0 ) return 0;

			if ( !buffers.TryGet( buffer, out var found ) )
			{
				VkDevice.SetText( "there is no buffer by that handle to write" );
				return 0;
			}

			if ( (ulong)offset + (ulong)size > found.Bytes )
			{
				VkDevice.SetText( "that write runs past the end of the buffer" );
				return 0;
			}

			//Through the staging ring the loads use, which covers every way a buffer is read next, so the frame
			//reading it sees what is written here
			return VkTextures.UploadToBuffer( found.Buffer, data, (ulong)size, (ulong)offset ) ? 1 : 0;
		}
	}

	internal static ulong Reserve( long size ) => Create( null, size );

	//A run of a buffer filled with one word, ahead of the next frame and behind everything already
	//submitted - see VkTextures.FillBuffer. The same in the frame's own order is VkFrame.FrameFill.
	internal static int Fill( ulong buffer, long offset, long size, uint value )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !VkDevice.Ready || size <= 0 || offset < 0 ) return 0;
			if ( ( offset & 3 ) != 0 || ( size & 3 ) != 0 ) return 0;

			if ( !buffers.TryGet( buffer, out var found ) )
			{
				VkDevice.SetText( "there is no buffer by that handle to fill" );
				return 0;
			}

			if ( (ulong)offset + (ulong)size > found.Bytes )
			{
				VkDevice.SetText( "that fill runs past the end of the buffer" );
				return 0;
			}

			return VkTextures.FillBuffer( found.Buffer, (ulong)offset, (ulong)size, value ) ? 1 : 0;
		}
	}

	internal static int Copy( ulong from, long fromOffset, ulong into, long intoOffset, long size )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !VkDevice.Ready ) return 0;
			if ( size <= 0 || fromOffset < 0 || intoOffset < 0 || from == into ) return 0;

			if ( !buffers.TryGet( from, out var source ) || !buffers.TryGet( into, out var target ) )
			{
				VkDevice.SetText( "there is no buffer by that handle to copy" );
				return 0;
			}

			if ( (ulong)fromOffset + (ulong)size > source.Bytes ||
				(ulong)intoOffset + (ulong)size > target.Bytes )
			{
				VkDevice.SetText( "that copy runs past the end of a buffer" );
				return 0;
			}

			return VkTextures.CopyBetweenBuffers( source.Buffer, (ulong)fromOffset,
				target.Buffer, (ulong)intoOffset, (ulong)size ) ? 1 : 0;
		}
	}

	//What a buffer is also made for where the rays are on: an address, and a mesh their structures are built from
	private static BufferUsageFlags RayUsage => VkRays.On
		? BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr
		: 0;

	internal static ulong Create( byte* data, long size )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !VkDevice.Ready )
			{
				VkDevice.SetText( "there is no device to put a buffer on" );
				return 0;
			}

			//No data is a reserve: a pool that is written as meshes arrive.
			if ( size <= 0 )
			{
				VkDevice.SetText( "a buffer needs some bytes" );
				return 0;
			}

			var made = default( BufferEntry );

			var info = new BufferCreateInfo
			{
				SType = StructureType.BufferCreateInfo,
				Size = (ulong)size,

				//Every use a mesh buffer may have, including indirect args and storage for vertex-pulling shaders
				Usage = BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit |
					BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit |
					BufferUsageFlags.TransferSrcBit | BufferUsageFlags.IndirectBufferBit | RayUsage,
				SharingMode = SharingMode.Exclusive,
			};

			VkBuffer buffer;

			if ( VkDevice.vk.CreateBuffer( VkDevice.device, &info, null, &buffer ) != Result.Success )
			{
				VkDevice.SetText( "the buffer would not be made" );
				return 0;
			}

			made.Buffer = buffer;

			MemoryRequirements requirements;
			VkDevice.vk.GetBufferMemoryRequirements( VkDevice.device, buffer, &requirements );

			//Memory types for a mesh, best first: VRAM via staging, then host-visible VRAM, then system RAM
			//A type that runs out of room falls through to the next instead of failing the load
			var choiceType = stackalloc uint[3];
			var choiceStaged = stackalloc bool[3];
			var choiceCount = 0;

			var deviceOnly = VkDevice.FindMemoryTypeWithout( requirements.MemoryTypeBits,
				MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.HostVisibleBit );

			var sharedWithHost = VkDevice.FindMemoryType( requirements.MemoryTypeBits,
				MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit |
				MemoryPropertyFlags.HostCoherentBit );

			//Where the host writes the fast memory directly - integrated parts, resizable BAR - staging buys nothing
			var writeStraightIn = sharedWithHost != uint.MaxValue &&
				VkDevice.HostCanWriteTheFastMemory( requirements.MemoryTypeBits );

			if ( writeStraightIn )
			{
				choiceType[choiceCount] = sharedWithHost;
				choiceStaged[choiceCount] = false;
				choiceCount++;
			}

			if ( deviceOnly != uint.MaxValue )
			{
				choiceType[choiceCount] = deviceOnly;
				choiceStaged[choiceCount] = true;
				choiceCount++;
			}

			if ( sharedWithHost != uint.MaxValue && !writeStraightIn )
			{
				choiceType[choiceCount] = sharedWithHost;
				choiceStaged[choiceCount] = false;
				choiceCount++;
			}

			if ( choiceCount < 3 )
			{
				var hostOnly = VkDevice.FindMemoryType( requirements.MemoryTypeBits,
					MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit );

				if ( hostOnly != uint.MaxValue )
				{
					choiceType[choiceCount] = hostOnly;
					choiceStaged[choiceCount] = false;
					choiceCount++;
				}
			}

			if ( choiceCount == 0 )
			{
				VkDevice.vk.DestroyBuffer( VkDevice.device, buffer, null );
				VkDevice.SetText( "no memory a buffer could live in" );
				return 0;
			}

			//One allocation per buffer; drivers cap the count near four thousand, far from where this lives
			var staged = false;
			var allocated = Result.ErrorOutOfDeviceMemory;

			for ( var i = 0; i < choiceCount && made.Memory.Handle == 0; ++i )
			{
				//With an address where the rays read meshes straight out of their buffers - see VkRays
				var addressed = new MemoryAllocateFlagsInfo { SType = StructureType.MemoryAllocateFlagsInfo, Flags = MemoryAllocateFlags.DeviceAddressBit };

				var allocate = new MemoryAllocateInfo
				{
					SType = StructureType.MemoryAllocateInfo,
					PNext = VkRays.On ? &addressed : null,
					AllocationSize = requirements.Size,
					MemoryTypeIndex = choiceType[i],
				};

				DeviceMemory memory;
				allocated = VkDevice.vk.AllocateMemory( VkDevice.device, &allocate, null, &memory );

				if ( allocated != Result.Success ) continue;

				if ( VkDevice.vk.BindBufferMemory( VkDevice.device, buffer, memory, 0 ) != Result.Success )
				{
					VkDevice.vk.FreeMemory( VkDevice.device, memory, null );
					continue;
				}

				made.Memory = memory;
				staged = choiceStaged[i];
			}

			if ( made.Memory.Handle == 0 )
			{
				VkDevice.MarkLostIf( allocated );
				VkDevice.vk.DestroyBuffer( VkDevice.device, buffer, null );

				VkDevice.SetText( "the buffer's memory would not be had" );
				return 0;
			}

			if ( data is null )
			{
				//Reserved: cleared by the card so it reads as zeros wherever it was made. Clearing a mapped BAR
				//from here writes every byte across the bus - a third of a second for a grown geometry pool.
				var cleared = VkTextures.FillBuffer( buffer, 0, Vk.WholeSize, 0u );
				void* mapped = null;

				if ( !cleared && !staged && VkDevice.vk.MapMemory( VkDevice.device, made.Memory, 0, Vk.WholeSize, 0, &mapped ) == Result.Success )
				{
					System.Runtime.InteropServices.NativeMemory.Clear( mapped, (nuint)size );
					VkDevice.vk.UnmapMemory( VkDevice.device, made.Memory );
				}
			}
			else if ( staged )
			{
				//Nothing to wait for and nothing to unwind: the staging buffer belongs to the upload ring,
				//and the copy is ordered ahead of any frame submitted after it by the one queue they share.
				if ( !VkTextures.UploadToBuffer( buffer, data, (ulong)size ) )
				{
					VkDevice.vk.DestroyBuffer( VkDevice.device, buffer, null );
					VkDevice.vk.FreeMemory( VkDevice.device, made.Memory, null );

					//UploadToBuffer has already said which half of it failed.
					return 0;
				}
			}
			else
			{
				void* mapped = null;

				if ( VkDevice.vk.MapMemory( VkDevice.device, made.Memory, 0, Vk.WholeSize, 0, &mapped ) != Result.Success )
				{
					VkDevice.vk.DestroyBuffer( VkDevice.device, buffer, null );
					VkDevice.vk.FreeMemory( VkDevice.device, made.Memory, null );

					VkDevice.SetText( "the buffer's memory would not map" );
					return 0;
				}

				System.Buffer.MemoryCopy( data, mapped, size, size );

				//Coherent memory needs no flush; unmapping says nothing writes this buffer again
				VkDevice.vk.UnmapMemory( VkDevice.device, made.Memory );
			}

			made.Bytes = requirements.Size;
			VkDevice.trackedBufferBytes += (long)made.Bytes;
			VkDevice.trackedBufferCount++;

			var handle = buffers.Add( made );

			//By the managed handle, so a message about a buffer traces back to its mesh
			VkDevice.NameHandle( ObjectType.Buffer, made.Buffer.Handle, "mesh buffer", handle );
			VkDevice.NameHandle( ObjectType.DeviceMemory, made.Memory.Handle, "mesh buffer memory", handle );

			return handle;
		}
	}

	//Reads a buffer back with its own staging, command buffer and fence; for tests and tools, not frames
	internal static int Read( ulong buffer, byte* outBytes, int capacity ) => Read( buffer, 0, outBytes, capacity );

	//The same, from offset bytes into the buffer.
	internal static int Read( ulong buffer, long offset, byte* outBytes, int capacity )
	{
		lock ( VkDevice.renderLock )
		{
			if ( outBytes is null || capacity <= 0 || offset < 0 ) return 0;

			var source = Get( buffer );
			if ( source.Handle == 0 || VkDevice.device.Handle == 0 ) return 0;

			if ( (ulong)offset + (ulong)capacity > BytesOf( buffer ) )
			{
				VkDevice.SetText( "that read runs past the end of the buffer" );
				return 0;
			}

			var size = (ulong)capacity;

			VkBuffer staging = default;
			DeviceMemory memory = default;
			CommandPool pool = default;
			CommandBuffer commands = default;
			Fence fence = default;
			void* mapped = null;

			var copied = 0;

			var vk = VkDevice.vk;
			var device = VkDevice.device;

			try
			{
				var info = new BufferCreateInfo
				{
					SType = StructureType.BufferCreateInfo,
					Size = size,
					Usage = BufferUsageFlags.TransferDstBit,
					SharingMode = SharingMode.Exclusive,
				};

				if ( vk.CreateBuffer( device, &info, null, &staging ) != Result.Success ) return 0;

				MemoryRequirements requirements;
				vk.GetBufferMemoryRequirements( device, staging, &requirements );

				var allocate = new MemoryAllocateInfo
				{
					SType = StructureType.MemoryAllocateInfo,
					AllocationSize = requirements.Size,
					MemoryTypeIndex = VkDevice.FindMemoryType( requirements.MemoryTypeBits,
						MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit ),
				};

				if ( allocate.MemoryTypeIndex == uint.MaxValue ) return 0;
				if ( vk.AllocateMemory( device, &allocate, null, &memory ) != Result.Success ) return 0;
				if ( vk.BindBufferMemory( device, staging, memory, 0 ) != Result.Success ) return 0;

				var poolInfo = new CommandPoolCreateInfo
				{
					SType = StructureType.CommandPoolCreateInfo,
					Flags = CommandPoolCreateFlags.TransientBit,
					QueueFamilyIndex = VkDevice.graphicsFamily,
				};

				if ( vk.CreateCommandPool( device, &poolInfo, null, &pool ) != Result.Success ) return 0;

				var commandInfo = new CommandBufferAllocateInfo
				{
					SType = StructureType.CommandBufferAllocateInfo,
					CommandPool = pool,
					Level = CommandBufferLevel.Primary,
					CommandBufferCount = 1,
				};

				if ( vk.AllocateCommandBuffers( device, &commandInfo, &commands ) != Result.Success ) return 0;

				var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };

				if ( vk.CreateFence( device, &fenceInfo, null, &fence ) != Result.Success ) return 0;

				var begin = new CommandBufferBeginInfo
				{
					SType = StructureType.CommandBufferBeginInfo,
					Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
				};

				vk.BeginCommandBuffer( commands, &begin );

				var copy = new BufferCopy { SrcOffset = (ulong)offset, Size = size };

				vk.CmdCopyBuffer( commands, source, staging, 1, &copy );
				vk.EndCommandBuffer( commands );

				var commandSubmit = new CommandBufferSubmitInfo
				{
					SType = StructureType.CommandBufferSubmitInfo,
					CommandBuffer = commands,
				};

				var submit = new SubmitInfo2
				{
					SType = StructureType.SubmitInfo2,
					CommandBufferInfoCount = 1,
					PCommandBufferInfos = &commandSubmit,
				};

				//Everything before it must have happened: what is read was written by a frame, another submission
				VkSubmitter.DeviceWaitIdle();

				if ( VkDevice.dialect.Submit( VkDevice.graphicsQueue, &submit, fence ) != Result.Success ) return 0;
				if ( vk.WaitForFences( device, 1, &fence, true, ulong.MaxValue ) != Result.Success ) return 0;

				if ( vk.MapMemory( device, memory, 0, Vk.WholeSize, 0, &mapped ) != Result.Success ) return 0;

				System.Buffer.MemoryCopy( mapped, outBytes, capacity, capacity );

				copied = capacity;
			}
			finally
			{
				if ( mapped is not null ) vk.UnmapMemory( device, memory );
				if ( fence.Handle != 0 ) vk.DestroyFence( device, fence, null );
				if ( pool.Handle != 0 ) vk.DestroyCommandPool( device, pool, null );
				if ( staging.Handle != 0 ) vk.DestroyBuffer( device, staging, null );
				if ( memory.Handle != 0 ) vk.FreeMemory( device, memory, null );
			}

			return copied;
		}
	}

	internal static void Destroy( ulong buffer )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !buffers.TryGet( buffer, out var found ) ) return;

			//Frames in flight may still be reading it, and so may the frame being recorded right now
			//- so it goes to the graveyard rather than dying here, and nobody waits for anything.
			VkDevice.RetireBuffer( found.Buffer, found.Memory );

			//And any set naming it is retired, not forgotten: a voxel world remakes buffers on every dig, and a
			//pool that only hands sets out runs dry
			RetireSetsNaming( buffer );

			VkDevice.trackedBufferBytes -= (long)found.Bytes;
			VkDevice.trackedBufferCount--;

			buffers.Remove( buffer, out _ );
		}
	}

	//Every object set that names this buffer, retired and forgotten.
	private static void RetireSetsNaming( ulong buffer )
	{
		List<ulong> going = null;

		foreach ( var pair in objectSets )
		{
			if ( pair.Value.Geometry != buffer && pair.Value.Data != buffer ) continue;

			VkDevice.RetireDescriptorSet( pair.Value.Set );

			going ??= [];
			going.Add( pair.Key );
		}

		if ( going is null ) return;

		for ( var i = 0; i < going.Count; ++i ) objectSets.Remove( going[i] );
	}
}
