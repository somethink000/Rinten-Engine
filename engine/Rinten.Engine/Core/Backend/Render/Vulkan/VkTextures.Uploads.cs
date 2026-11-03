using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Rinten;

//Upload ring of four slots; a slot only waits when it is reused before its last copy finished
internal static unsafe partial class VkTextures
{
	//What one upload is recorded and submitted through.
	private sealed class UploadContext
	{
		public CommandPool Pool;
		public CommandBuffer Commands;
		public Fence Fence;

		/// <summary>Whether this slot has been submitted and not yet waited for.</summary>
		public bool Pending;

		/// <summary>Whether this slot is the batch being recorded into right now.</summary>
		public bool Open;

		/// <summary>How much of its staging buffer the copies recorded so far have spoken for. Every upload appends at this offset.</summary>
		public ulong StagingUsed;

		/// <summary>Per-slot staging, grown and reused instead of per picture; stays mapped (coherent memory)</summary>
		public VkBuffer Staging;
		public DeviceMemory StagingMemory;
		public ulong StagingBytes;
		public byte* StagingMapped;

		public void Forget()
		{
			Pool = default;
			Commands = default;
			Fence = default;
			Pending = false;
			Open = false;
			StagingUsed = 0;
			Staging = default;
			StagingMemory = default;
			StagingBytes = 0;
			StagingMapped = null;
		}
	}

	private const uint UploadSlots = 4;

	private static readonly UploadContext[] uploads =
		[new UploadContext(), new UploadContext(), new UploadContext(), new UploadContext()];

	private static uint uploadAt;

	//The batch being recorded into, or null when there is none open.
	private static UploadContext batch;

	//How much one batch carries before it is sent on its own. Only a ceiling on the host memory a single
	//burst may hold; a batch is normally sent by the frame it belongs to long before it gets near this.
	private const ulong BatchBytes = 32ul * 1024ul * 1024ul;

	//What a batch's staging buffer starts at, so a burst of small pictures does not grow it a hundred times on its way up.
	private const ulong BatchStart = 4ul * 1024ul * 1024ul;

	//Past this much held per slot, the staging is given back rather than kept - the buffers
	//only ever grow, and one enormous mesh would otherwise leave every slot sized for it.
	private const ulong KeepStagingUpTo = 32ul * 1024ul * 1024ul;

	//What an environment variable says, or nothing.
	private static bool Said( string name, out string value )
	{
		value = Environment.GetEnvironmentVariable( name );
		return !string.IsNullOrEmpty( value );
	}

	//Whether uploads are collected at all. RINTEN_UPLOAD_BATCH=0 sends each one on its own, the way they went before - so
	//the same binary can be measured both ways in the same afternoon, which is the only comparison worth anything. Read once.
	private static readonly bool batching = !Said( "RINTEN_UPLOAD_BATCH", out var batchSaid ) || batchSaid[0] != '0';

	//Whether a picture whose owner went away is kept for the next one. RINTEN_TEXTURE_SPARES=0
	//frees it instead, the way it used to be - so the same binary can be measured both ways.
	private static readonly bool sparing = !Said( "RINTEN_TEXTURE_SPARES", out var spareSaid ) || spareSaid[0] != '0';

	//Whether to say where making a picture's time went. RINTEN_TEXTURE_TIMING=1, reported every hundred.
	private static readonly bool timingTextures = Said( "RINTEN_TEXTURE_TIMING", out var timeSaid ) && timeSaid[0] != '0';

	//Nanoseconds spent in each part of making one, since the last report.
	private struct MakingCost
	{
		public long Image;
		public long Memory;
		public long View;
		public long Descriptors;
		public long Copy;
		public int Made;

		/// <summary>Writing into a picture that already exists - a repaint, or a spare taken back into use - and how much of that was waiting for a slot.</summary>
		public long Written;
		public int Writes;
		public long Waited;
		public int Waits;
		public int Batches;
	}

	private static MakingCost making;

	//The processor's clock in nanoseconds, only where the timing is asked for - reading a clock five times per picture is not free either.
	private static long TimedNs()
		=> timingTextures ? (long)( System.Diagnostics.Stopwatch.GetTimestamp() * NsPerTick ) : 0;

	private static readonly double NsPerTick = 1.0e9 / System.Diagnostics.Stopwatch.Frequency;

	//Says where the last hundred pictures' time went, and starts again.
	private static void SayMakingCost()
	{
		if ( !timingTextures ) return;
		if ( making.Made + making.Writes < 100 ) return;

		var each = 1.0e3 * ( making.Made > 0 ? making.Made : 1 );
		var perWrite = 1.0e3 * ( making.Writes > 0 ? making.Writes : 1 );

		VkDevice.Say( $"textures: {making.Made} made - image {making.Image / each:F0}, memory {making.Memory / each:F0}, " +
			$"view {making.View / each:F0}, descriptors {making.Descriptors / each:F0}, copy {making.Copy / each:F0} us each; " +
			$"{making.Writes} written over at {making.Written / perWrite:F0} us each; {making.Batches} batches sent, " +
			$"{making.Waits} waited for a slot, {making.Waited / 1.0e6:F1} ms waiting" );

		making = default;
	}

	private static string ResultName( Result result ) => result switch
	{
		Result.ErrorOutOfHostMemory => "host memory",
		Result.ErrorOutOfDeviceMemory => "device memory",
		Result.ErrorDeviceLost => "device lost",
		_ => "failed",
	};

	private static void SayUploadFailed( Result result )
		=> VkDevice.SetText( $"the texture would not upload ({ResultName( result )})" );

	//The next slot to record into, ready to be recorded over, or null.
	private static UploadContext TakeUploadContext()
	{
		if ( VkDevice.Lost )
		{
			VkDevice.SetText( "the device was lost" );
			return null;
		}

		var slot = uploads[uploadAt];
		uploadAt = ( uploadAt + 1 ) % UploadSlots;

		//A whole lap round to the batch still being recorded into. It goes now rather than having its command pool reset underneath it.
		if ( slot.Open ) SubmitBatch();

		var vk = VkDevice.vk;
		var device = VkDevice.device;

		if ( slot.Pool.Handle == 0 )
		{
			var poolInfo = new CommandPoolCreateInfo
			{
				SType = StructureType.CommandPoolCreateInfo,
				Flags = CommandPoolCreateFlags.TransientBit,
				QueueFamilyIndex = VkDevice.graphicsFamily,
			};

			CommandPool pool;

			if ( vk.CreateCommandPool( device, &poolInfo, null, &pool ) != Result.Success )
			{
				VkDevice.SetText( "no command pool for a texture upload" );
				return null;
			}

			slot.Pool = pool;

			var commandInfo = new CommandBufferAllocateInfo
			{
				SType = StructureType.CommandBufferAllocateInfo,
				CommandPool = pool,
				Level = CommandBufferLevel.Primary,
				CommandBufferCount = 1,
			};

			var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };

			CommandBuffer commands;
			Fence fence = default;

			if ( vk.AllocateCommandBuffers( device, &commandInfo, &commands ) != Result.Success ||
				vk.CreateFence( device, &fenceInfo, null, &fence ) != Result.Success )
			{
				if ( fence.Handle != 0 ) vk.DestroyFence( device, fence, null );
				vk.DestroyCommandPool( device, pool, null );

				slot.Forget();
				VkDevice.SetText( "no command pool for a texture upload" );
				return null;
			}

			slot.Commands = commands;
			slot.Fence = fence;
		}

		//Its own last outing, if it had one. A lap behind, so in practice this has long since passed. A lost device returns at once, and
		//resetting the pool of a command buffer that is still pending is illegal - so the slot is left alone and the caller hears why.
		if ( slot.Pending )
		{
			var startedNs = TimedNs();

			var thisFence = slot.Fence;
			var waited = vk.WaitForFences( device, 1, &thisFence, true, ulong.MaxValue );

			if ( timingTextures )
			{
				making.Waited += TimedNs() - startedNs;
				making.Waits++;
			}

			if ( waited != Result.Success )
			{
				VkDevice.MarkLostIf( waited );
				VkDevice.SetText( waited == Result.ErrorDeviceLost
					? "the device was lost waiting for a texture"
					: "a texture upload never finished" );

				return null;
			}

			slot.Pending = false;
		}

		{
			var thisFence = slot.Fence;
			vk.ResetFences( device, 1, &thisFence );
		}

		vk.ResetCommandPool( device, slot.Pool, 0 );

		return slot;
	}

	private static void DestroyUploadContext()
	{
		//Whatever is half-recorded goes first. Freeing a slot's staging out from under an open command buffer is a copy pointed at nothing.
		SubmitBatch();

		var vk = VkDevice.vk;
		var device = VkDevice.device;

		for ( var i = 0; i < uploads.Length; ++i )
		{
			var slot = uploads[i];

			//Waited on first: what it copies out of is freed below, and a copy still in flight is a copy still reading it.
			if ( slot.Pending && slot.Fence.Handle != 0 )
			{
				var fence = slot.Fence;
				vk.WaitForFences( device, 1, &fence, true, ulong.MaxValue );
				slot.Pending = false;
			}

			FreeStaging( slot );

			if ( slot.Fence.Handle != 0 ) vk.DestroyFence( device, slot.Fence, null );
			if ( slot.Pool.Handle != 0 ) vk.DestroyCommandPool( device, slot.Pool, null );

			slot.Forget();
		}

		uploadAt = 0;
		batch = null;
	}

	//Frees a slot's staging buffer. Only called where nothing is copying out of it - either the slot's fence has been waited on, or the device is going.
	private static void FreeStaging( UploadContext slot )
	{
		var vk = VkDevice.vk;
		var device = VkDevice.device;

		if ( slot.StagingMapped is not null ) vk.UnmapMemory( device, slot.StagingMemory );
		if ( slot.Staging.Handle != 0 ) vk.DestroyBuffer( device, slot.Staging, null );
		if ( slot.StagingMemory.Handle != 0 ) vk.FreeMemory( device, slot.StagingMemory, null );

		slot.Staging = default;
		slot.StagingMemory = default;
		slot.StagingBytes = 0;
		slot.StagingMapped = null;
	}

	//Room for this many bytes in the slot's staging buffer, made if there is not enough of it yet. False where the host has no memory for it.
	private static bool EnsureStaging( UploadContext slot, ulong size )
	{
		if ( size == 0 ) return false;

		if ( slot.StagingBytes < size )
		{
			FreeStaging( slot );

			var vk = VkDevice.vk;
			var device = VkDevice.device;

			var info = new BufferCreateInfo
			{
				SType = StructureType.BufferCreateInfo,
				Size = size,
				Usage = BufferUsageFlags.TransferSrcBit,
				SharingMode = SharingMode.Exclusive,
			};

			VkBuffer staging;

			if ( vk.CreateBuffer( device, &info, null, &staging ) != Result.Success )
			{
				slot.Staging = default;
				VkDevice.SetText( "no host memory to copy the texture through" );
				return false;
			}

			slot.Staging = staging;

			MemoryRequirements requirements;
			vk.GetBufferMemoryRequirements( device, staging, &requirements );

			var allocate = new MemoryAllocateInfo
			{
				SType = StructureType.MemoryAllocateInfo,
				AllocationSize = requirements.Size,
				MemoryTypeIndex = VkDevice.FindMemoryType( requirements.MemoryTypeBits,
					MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit ),
			};

			DeviceMemory memory = default;
			void* mapped = null;

			var had = allocate.MemoryTypeIndex != uint.MaxValue &&
				vk.AllocateMemory( device, &allocate, null, &memory ) == Result.Success;

			slot.StagingMemory = memory;

			if ( !had ||
				vk.BindBufferMemory( device, staging, memory, 0 ) != Result.Success ||
				vk.MapMemory( device, memory, 0, Vk.WholeSize, 0, &mapped ) != Result.Success )
			{
				slot.StagingMapped = null;
				FreeStaging( slot );
				VkDevice.SetText( "no host memory to copy the texture through" );
				return false;
			}

			slot.StagingMapped = (byte*)mapped;
			slot.StagingBytes = requirements.Size;
		}

		return slot.StagingMapped is not null;
	}

	private static bool FillStaging( UploadContext slot, void* data, ulong size )
	{
		if ( !EnsureStaging( slot, size ) ) return false;

		System.Buffer.MemoryCopy( data, slot.StagingMapped, size, size );

		return true;
	}

	//Submits the open batch: all pictures uploaded since the last send in one command buffer
	private static void SubmitBatch()
	{
		if ( batch is null ) return;

		var slot = batch;

		batch = null;
		slot.Open = false;

		var vk = VkDevice.vk;
		var device = VkDevice.device;

		if ( vk.EndCommandBuffer( slot.Commands ) != Result.Success )
		{
			VkDevice.SetText( "a batch of texture uploads could not be recorded" );

			if ( !VkDevice.Lost ) vk.ResetCommandPool( device, slot.Pool, 0 );

			slot.StagingUsed = 0;
			return;
		}

		var commandSubmit = new CommandBufferSubmitInfo
		{
			SType = StructureType.CommandBufferSubmitInfo,
			CommandBuffer = slot.Commands,
		};

		var submit = new SubmitInfo2
		{
			SType = StructureType.SubmitInfo2,
			CommandBufferInfoCount = 1,
			PCommandBufferInfos = &commandSubmit,
		};

		//Submitted and left to happen. One queue means a frame submitted after this cannot read any of the pictures before their copies have
		//run, so nobody waits here; the slot's own fence is what stops it being recorded over while the card is still reading its bytes.
		var submitted = VkDevice.dialect.Submit( VkDevice.graphicsQueue, &submit, slot.Fence );

		slot.StagingUsed = 0;

		if ( submitted != Result.Success )
		{
			VkDevice.MarkLostIf( submitted );
			SayUploadFailed( submitted );

			if ( !VkDevice.Lost ) vk.ResetCommandPool( device, slot.Pool, 0 );

			return;
		}

		slot.Pending = true;

		if ( timingTextures ) making.Batches++;

		//One outsized upload against tens of megabytes standing idle behind this slot for the rest of the run.
		if ( slot.StagingBytes > KeepStagingUpTo )
		{
			var fence = slot.Fence;
			var waited = vk.WaitForFences( device, 1, &fence, true, ulong.MaxValue );

			VkDevice.MarkLostIf( waited );

			if ( waited == Result.Success )
			{
				slot.Pending = false;
				FreeStaging( slot );
			}
		}
	}

	//Sends the pictures uploaded since the last time anything did. A frame calls this just before its own
	//submission, so that the copies are queued ahead of the draws that sample them. The lock has to be held.
	internal static void FlushTextureUploads() => SubmitBatch();

	//Whether uploads are recorded and not yet sent - what is retired meanwhile waits for the frame they go ahead of
	internal static bool BatchOpen => batch is not null;

	//Room for this many bytes in the open batch, opening one where there is none. Null where the device would not give it what it needs. The bytes go at
	//offset in the slot's staging buffer, which is what the caller's copy has to name - a batch is one buffer with everybody's pixels laid end to end in it.
	private static UploadContext ReserveUpload( ulong size, out ulong offset )
	{
		offset = 0;

		if ( size == 0 ) return null;

		//Four, because a buffer-to-image copy's offset has to be a multiple of the texel size and every format
		//here is four bytes wide; sixteen because it costs nothing and keeps every row aligned for the copy.
		var need = ( size + 15ul ) & ~15ul;

		//Full enough, or too full for this one: what is recorded goes now and a fresh slot takes over.
		if ( batch is not null &&
			( batch.StagingUsed >= BatchBytes || batch.StagingUsed + need > batch.StagingBytes ) )
		{
			SubmitBatch();
		}

		if ( batch is null )
		{
			var slot = TakeUploadContext();

			if ( slot is null ) return null;

			var begin = new CommandBufferBeginInfo
			{
				SType = StructureType.CommandBufferBeginInfo,
				Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
			};

			if ( VkDevice.vk.BeginCommandBuffer( slot.Commands, &begin ) != Result.Success )
			{
				VkDevice.SetText( "a batch of texture uploads could not be started" );
				return null;
			}

			slot.StagingUsed = 0;
			slot.Open = true;

			batch = slot;
		}

		//Grown only while nothing is recorded against it: growing frees the buffer, and a copy already written down names the one it was written with.
		if ( batch.StagingUsed == 0 && batch.StagingBytes < need )
		{
			if ( !EnsureStaging( batch, need > BatchStart ? need : BatchStart ) )
			{
				//Nothing has been recorded into it, so the slot is simply given back rather than sent.
				var given = batch;

				given.Open = false;
				batch = null;

				VkDevice.vk.EndCommandBuffer( given.Commands );

				if ( !VkDevice.Lost ) VkDevice.vk.ResetCommandPool( VkDevice.device, given.Pool, 0 );

				return null;
			}
		}

		if ( batch.StagingUsed + need > batch.StagingBytes ) return null;

		offset = batch.StagingUsed;
		batch.StagingUsed += need;

		return batch;
	}

	//Copies bytes into a device-local buffer via the upload ring, with a barrier for mesh reads
	internal static bool UploadToBuffer( VkBuffer destination, void* data, ulong size, ulong offset = 0 )
	{
		if ( destination.Handle == 0 || data is null || size == 0 ) return false;

		//Into the open batch with the pictures: a submission each takes one of four slots, and the fifth waits for the card.
		//What fills, copies or reads buffers on its own sends the batch first
		if ( batching && size <= BatchBytes && ReserveUpload( size, out var at ) is { } into )
		{
			System.Buffer.MemoryCopy( data, into.StagingMapped + at, size, size );

			var batched = new BufferCopy { Size = size, SrcOffset = at, DstOffset = offset };
			VkDevice.vk.CmdCopyBuffer( into.Commands, into.Staging, destination, 1, &batched );
			BufferReadable( into.Commands, destination );

			return true;
		}

		var slot = TakeUploadContext();
		if ( slot is null ) return false;

		if ( !FillStaging( slot, data, size ) ) return false;

		var vk = VkDevice.vk;
		var commands = slot.Commands;

		var begin = new CommandBufferBeginInfo
		{
			SType = StructureType.CommandBufferBeginInfo,
			Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
		};

		vk.BeginCommandBuffer( commands, &begin );

		var copy = new BufferCopy { Size = size, DstOffset = offset };

		vk.CmdCopyBuffer( commands, slot.Staging, destination, 1, &copy );

		BufferReadable( commands, destination );

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

		//Left to happen, the same way a texture's copy is: one queue, so a frame submitted after this cannot read
		//the mesh before the copy has run, and a level loading a thousand meshes does not stall once per mesh.
		var submitted = VkDevice.dialect.Submit( VkDevice.graphicsQueue, &submit, slot.Fence );

		if ( submitted != Result.Success )
		{
			VkDevice.MarkLostIf( submitted );
			VkDevice.SetText( $"the mesh would not upload ({ResultName( submitted )})" );
			return false;
		}

		slot.Pending = true;

		//Past KeepStagingUpTo, wait and free the staging so slots don't keep huge host buffers
		if ( size > KeepStagingUpTo )
		{
			var fence = slot.Fence;
			var waited = vk.WaitForFences( VkDevice.device, 1, &fence, true, ulong.MaxValue );

			VkDevice.MarkLostIf( waited );

			if ( waited == Result.Success )
			{
				slot.Pending = false;
				FreeStaging( slot );
			}
		}

		return true;
	}

	//A copy into a buffer made visible to every later use - vertex, index, shader, indirect, copy - or validation flags it
	private static void BufferReadable( CommandBuffer commands, VkBuffer buffer )
	{
		var barrier = new BufferMemoryBarrier2
		{
			SType = StructureType.BufferMemoryBarrier2,
			SrcStageMask = PipelineStageFlags2.CopyBit,
			SrcAccessMask = AccessFlags2.TransferWriteBit,
			DstStageMask = PipelineStageFlags2.VertexInputBit |
				PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit |
				PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.DrawIndirectBit |
				PipelineStageFlags2.CopyBit,
			DstAccessMask = AccessFlags2.VertexAttributeReadBit |
				AccessFlags2.IndexReadBit | AccessFlags2.ShaderStorageReadBit |
				AccessFlags2.ShaderStorageWriteBit | AccessFlags2.IndirectCommandReadBit |
				AccessFlags2.TransferWriteBit | AccessFlags2.TransferReadBit,
			SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
			DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
			Buffer = buffer,
			Size = Vk.WholeSize,
		};

		var dependency = new DependencyInfo
		{
			SType = StructureType.DependencyInfo,
			BufferMemoryBarrierCount = 1,
			PBufferMemoryBarriers = &barrier,
		};

		VkDevice.dialect.Barrier( commands, &dependency );
	}

	//Copies bytes between two buffers on the device through the same upload slots, so it is ordered like an upload against whatever reads next.
	internal static bool CopyBetweenBuffers( VkBuffer from, ulong fromOffset, VkBuffer into, ulong intoOffset, ulong size )
	{
		if ( from.Handle == 0 || into.Handle == 0 || size == 0 ) return false;

		//After the uploads batched before it, which may write what it copies
		SubmitBatch();

		var slot = TakeUploadContext();
		if ( slot is null ) return false;

		var vk = VkDevice.vk;
		var commands = slot.Commands;

		var begin = new CommandBufferBeginInfo
		{
			SType = StructureType.CommandBufferBeginInfo,
			Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
		};

		vk.BeginCommandBuffer( commands, &begin );

		var copy = new BufferCopy { Size = size, SrcOffset = fromOffset, DstOffset = intoOffset };

		vk.CmdCopyBuffer( commands, from, into, 1, &copy );

		BufferReadable( commands, into );

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

		var submitted = VkDevice.dialect.Submit( VkDevice.graphicsQueue, &submit, slot.Fence );

		if ( submitted != Result.Success )
		{
			VkDevice.MarkLostIf( submitted );
			VkDevice.SetText( $"the pool would not copy ({ResultName( submitted )})" );
			return false;
		}

		slot.Pending = true;

		return true;
	}
}
