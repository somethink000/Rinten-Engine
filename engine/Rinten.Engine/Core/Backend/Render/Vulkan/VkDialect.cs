using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace Rinten;

//The formats a pass draws into: learnt by dynamic rendering, told to a render pass object
internal struct VkPassFormats
{
	public Format Colour0;
	public Format Colour1;
	public uint Colours;

	/// <summary>Undefined where the pass has no depth.</summary>
	public Format Depth;

	/// <summary>Layers one draw goes to, a bit each; nought for every pass but a headset's.</summary>
	public uint ViewMask;

	public static VkPassFormats Of( Format colour, Format depth = Format.Undefined )
		=> new() { Colour0 = colour, Colours = colour == Format.Undefined ? 0u : 1u, Depth = depth };

	public static VkPassFormats DepthOnly( Format depth )
		=> new() { Colours = 0, Depth = depth };

	public readonly Format Colour( uint index ) => index == 0 ? Colour0 : Colour1;
}

//How the device is spoken to: the renderer uses Vulkan 1.3 calls, translated for 1.1 drivers
//Only dialects may call 1.3 entry points; real missing capabilities are RenderTier's business
internal abstract unsafe class VkDialect
{
	/// <summary>The frame a device spoken to this way can hold.</summary>
	public abstract RenderTier Tier { get; }

	/// <summary>What the dialect is called in a log line.</summary>
	public abstract string Name { get; }

	/// <summary>What this device lacks to be spoken to this way, or null; asked before anything is made.</summary>
	public abstract string Missing( PhysicalDevice candidate );

	/// <summary>Chains the dialect's features and the optional ones the device has, until FeaturesDone.</summary>
	public abstract void ChainFeatures( PhysicalDevice physical, PhysicalDeviceFeatures2* features );

	/// <summary>The device is made, or was refused: the chain may go.</summary>
	public abstract void FeaturesDone();

	/// <summary>Opens a pass. The attachments are already in the layouts they name.</summary>
	public abstract void BeginRendering( CommandBuffer commands, RenderingInfo* info, in VkPassFormats formats );

	public abstract void EndRendering( CommandBuffer commands );

	/// <summary>Tells a pipeline its formats, chained on or made into the render pass they need.</summary>
	public abstract bool DescribePass( GraphicsPipelineCreateInfo* pipeline, PipelineRenderingCreateInfo* rendering );

	public abstract void Barrier( CommandBuffer commands, DependencyInfo* dependency );

	/// <summary>A submission to the queue, after any frame the submit thread holds - see VkSubmitter.</summary>
	public Result Submit( Queue queue, SubmitInfo2* submit, Fence fence )
	{
		//Under the render lock the worker's parked present waits for it - see VkSubmitter.Yield
		if ( VkDevice.renderLock.IsHeldByCurrentThread ) VkSubmitter.Yield();
		else VkSubmitter.Drain();

		return SubmitNow( queue, submit, fence );
	}

	/// <summary>The submission itself in this dialect's words, for the submit thread.</summary>
	public abstract Result SubmitNow( Queue queue, SubmitInfo2* submit, Fence fence );

	/// <summary>The card's clock written into a query when the commands so far have reached a stage.</summary>
	public abstract void Timestamp( CommandBuffer commands, PipelineStageFlags2 stage, QueryPool pool, uint query );

	/// <summary>A view is about to be destroyed: nothing kept may go on naming it.</summary>
	public virtual void ForgetView( ImageView view ) { }

	/// <summary>The device is going: everything kept goes first.</summary>
	public virtual void Release() { }

	//Whether a candidate speaks at least this version.
	protected static bool Speaks( PhysicalDevice candidate, uint minor )
	{
		PhysicalDeviceProperties properties;
		VkDevice.vk.GetPhysicalDeviceProperties( candidate, &properties );

		var version = (Version32)properties.ApiVersion;

		return version.Major > 1 || ( version.Major == 1 && version.Minor >= minor );
	}
}

//Vulkan 1.3 as written: dynamic rendering, synchronisation2 and the picture table the full frame needs
internal sealed unsafe class VkModern : VkDialect
{
	public override RenderTier Tier => RenderTier.Full;
	public override string Name => "vulkan 1.3";

	private PhysicalDeviceVulkan13Features* features13;
	private PhysicalDeviceVulkan12Features* features12;
	private PhysicalDeviceVulkan11Features* features11;

	//Checks every feature ChainFeatures will request, so failures say what is missing
	public override string Missing( PhysicalDevice candidate )
	{
		if ( !Speaks( candidate, 3 ) ) return "Vulkan 1.3";

		var has13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };
		var has12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &has13 };
		var has11 = new PhysicalDeviceVulkan11Features { SType = StructureType.PhysicalDeviceVulkan11Features, PNext = &has12 };
		var has = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &has11 };

		VkDevice.vk.GetPhysicalDeviceFeatures2( candidate, &has );

		//In the order ChainFeatures asks for them, so the two lists can be read against each other.
		if ( !has13.DynamicRendering ) return "dynamic rendering";
		if ( !has13.Synchronization2 ) return "synchronisation2";

		if ( !has12.DescriptorIndexing ) return "descriptor indexing";
		if ( !has12.RuntimeDescriptorArray ) return "runtime descriptor arrays";
		if ( !has12.ShaderSampledImageArrayNonUniformIndexing ) return "non-uniform image indexing";
		if ( !has12.DescriptorBindingPartiallyBound ) return "partially bound descriptors";
		if ( !has12.DescriptorBindingSampledImageUpdateAfterBind ) return "update-after-bind images";
		if ( !has12.DescriptorBindingUpdateUnusedWhilePending ) return "update-while-pending descriptors";

		if ( !has11.ShaderDrawParameters ) return "shader draw parameters";

		return null;
	}

	public override void ChainFeatures( PhysicalDevice physical, PhysicalDeviceFeatures2* features )
	{
		features13 = (PhysicalDeviceVulkan13Features*)NativeMemory.AllocZeroed( (nuint)sizeof( PhysicalDeviceVulkan13Features ) );
		features12 = (PhysicalDeviceVulkan12Features*)NativeMemory.AllocZeroed( (nuint)sizeof( PhysicalDeviceVulkan12Features ) );
		features11 = (PhysicalDeviceVulkan11Features*)NativeMemory.AllocZeroed( (nuint)sizeof( PhysicalDeviceVulkan11Features ) );

		//The two 1.3 features worth having on from the start. Dynamic rendering removes render pass objects
		//entirely; synchronisation2 replaces the old barrier API with one that is much harder to get subtly wrong.
		features13->SType = StructureType.PhysicalDeviceVulkan13Features;
		features13->DynamicRendering = true;
		features13->Synchronization2 = true;

		//Descriptor indexing for the bindless table: partially bound, updated while frames are in flight
		features12->SType = StructureType.PhysicalDeviceVulkan12Features;
		features12->DescriptorIndexing = true;
		features12->RuntimeDescriptorArray = true;
		features12->ShaderSampledImageArrayNonUniformIndexing = true;
		features12->DescriptorBindingPartiallyBound = true;
		features12->DescriptorBindingSampledImageUpdateAfterBind = true;
		features12->DescriptorBindingUpdateUnusedWhilePending = true;
		features12->PNext = features13;

		//A draw whose count the card wrote. Asked for rather than demanded.
		var has12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features };
		var has = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &has12 };
		VkDevice.vk.GetPhysicalDeviceFeatures2( physical, &has );

		VkDevice.drawIndirectCount = has12.DrawIndirectCount;
		features12->DrawIndirectCount = has12.DrawIndirectCount;

		//Buffer device addresses, for NVIDIA's NGX (CoreDlss) or the rays (VkRays), where the card has them
		features12->BufferDeviceAddress = ( VkDevice.ngxDeviceAddress || VkRays.WantsAddresses ) && has12.BufferDeviceAddress;

		//A semaphore that counts, which Reflex's sleep is signalled through - see CoreLatency. Where the card has one.
		features12->TimelineSemaphore = CoreLatency.Reflex && has12.TimelineSemaphore;

		//Both eyes of a headset in one pass; asked rather than assumed - see VkDevice.multiview
		var has11 = new PhysicalDeviceVulkan11Features { SType = StructureType.PhysicalDeviceVulkan11Features };
		var hasEleven = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &has11 };
		VkDevice.vk.GetPhysicalDeviceFeatures2( physical, &hasEleven );

		VkDevice.multiview = has11.Multiview;

		//Slang compiles SV_VertexID against the base-vertex-relative index, which SPIR-V files under DrawParameters -
		//so without shaderDrawParameters, the very first vertex shader is refused. Universal on hardware that has 1.3.
		features11->SType = StructureType.PhysicalDeviceVulkan11Features;
		features11->ShaderDrawParameters = true;
		features11->Multiview = VkDevice.multiview;
		features11->PNext = features12;

		features->PNext = features11;
	}

	public override void FeaturesDone()
	{
		NativeMemory.Free( features13 );
		NativeMemory.Free( features12 );
		NativeMemory.Free( features11 );

		features13 = null;
		features12 = null;
		features11 = null;
	}

	public override void BeginRendering( CommandBuffer commands, RenderingInfo* info, in VkPassFormats formats )
		=> VkDevice.vk.CmdBeginRendering( commands, info );

	public override void EndRendering( CommandBuffer commands )
		=> VkDevice.vk.CmdEndRendering( commands );

	public override bool DescribePass( GraphicsPipelineCreateInfo* pipeline, PipelineRenderingCreateInfo* rendering )
	{
		pipeline->PNext = rendering;
		return true;
	}

	public override void Barrier( CommandBuffer commands, DependencyInfo* dependency )
		=> VkDevice.vk.CmdPipelineBarrier2( commands, dependency );

	public override Result SubmitNow( Queue queue, SubmitInfo2* submit, Fence fence )
		=> VkDevice.vk.QueueSubmit2( queue, 1, submit, fence );

	public override void Timestamp( CommandBuffer commands, PipelineStageFlags2 stage, QueryPool pool, uint query )
		=> VkDevice.vk.CmdWriteTimestamp2( commands, stage, pool, query );
}

//Vulkan 1.1 dialect: a thin translator to render passes, old barriers and old submits
//Depth-only layouts from 1.2 are rewritten to the 1.1 equivalent
internal sealed unsafe class VkClassic : VkDialect
{
	public override RenderTier Tier => RenderTier.Portable;
	public override string Name => "vulkan 1.1";

	private PhysicalDeviceShaderDrawParametersFeatures* drawParameters;
	private PhysicalDeviceMultiviewFeatures* multiview;

	public override string Missing( PhysicalDevice candidate )
	{
		if ( !Speaks( candidate, 1 ) ) return "Vulkan 1.1";

		//The one feature of 1.1's the shaders cannot be compiled around - see VkModern.ChainFeatures.
		//Its own structure here: the one that gathers 1.1's features together arrived with 1.2.
		var has = new PhysicalDeviceShaderDrawParametersFeatures
		{
			SType = StructureType.PhysicalDeviceShaderDrawParametersFeatures,
		};

		var all = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &has };

		VkDevice.vk.GetPhysicalDeviceFeatures2( candidate, &all );

		return has.ShaderDrawParameters ? null : "shader draw parameters";
	}

	public override void ChainFeatures( PhysicalDevice physical, PhysicalDeviceFeatures2* features )
	{
		drawParameters = (PhysicalDeviceShaderDrawParametersFeatures*)NativeMemory.AllocZeroed(
			(nuint)sizeof( PhysicalDeviceShaderDrawParametersFeatures ) );

		drawParameters->SType = StructureType.PhysicalDeviceShaderDrawParametersFeatures;
		drawParameters->ShaderDrawParameters = true;

		//Arrived with 1.2, and the frame that would use it is not this one.
		VkDevice.drawIndirectCount = false;

		//Both eyes of a headset in one pass - see VkDevice.multiview.
		var has = new PhysicalDeviceMultiviewFeatures { SType = StructureType.PhysicalDeviceMultiviewFeatures };
		var all = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &has };
		VkDevice.vk.GetPhysicalDeviceFeatures2( physical, &all );

		VkDevice.multiview = has.Multiview;

		multiview = (PhysicalDeviceMultiviewFeatures*)NativeMemory.AllocZeroed( (nuint)sizeof( PhysicalDeviceMultiviewFeatures ) );
		multiview->SType = StructureType.PhysicalDeviceMultiviewFeatures;
		multiview->Multiview = has.Multiview;

		drawParameters->PNext = multiview;
		features->PNext = drawParameters;
	}

	public override void FeaturesDone()
	{
		NativeMemory.Free( drawParameters );
		NativeMemory.Free( multiview );
		drawParameters = null;
		multiview = null;
	}

	//-- passes ----------------------------------------------------------------

	//What makes two passes the same object: the formats, and what is done with each on the way in and out.
	private readonly record struct PassKey(
		Format Colour0, Format Colour1, uint Colours, Format Depth,
		AttachmentLoadOp Load0, AttachmentLoadOp Load1, AttachmentLoadOp LoadDepth,
		AttachmentStoreOp Store0, AttachmentStoreOp Store1, AttachmentStoreOp StoreDepth,
		ImageLayout Layout0, ImageLayout Layout1, ImageLayout LayoutDepth, uint ViewMask );

	//What makes two framebuffers the same object. Any pass of the same formats may draw into it.
	private readonly record struct TargetKey(
		Format Colour0, Format Colour1, uint Colours, Format Depth,
		ulong View0, ulong View1, ulong ViewDepth,
		uint Width, uint Height, uint Layers );

	private readonly Dictionary<PassKey, RenderPass> passes = new();
	private readonly Dictionary<TargetKey, Framebuffer> targets = new();

	//Reused between frames: a removal walks the keys, and a list a walk is garbage the frame does not need.
	private readonly List<TargetKey> doomed = new();

	private static ImageLayout Known( ImageLayout layout ) => layout switch
	{
		ImageLayout.DepthAttachmentOptimal => ImageLayout.DepthStencilAttachmentOptimal,
		ImageLayout.DepthReadOnlyOptimal => ImageLayout.DepthStencilReadOnlyOptimal,
		_ => layout,
	};

	private RenderPass PassFor( in PassKey key )
	{
		if ( passes.TryGetValue( key, out var kept ) ) return kept;

		var attachments = stackalloc AttachmentDescription[3];
		var colours = stackalloc AttachmentReference[2];
		var count = 0u;

		for ( var i = 0u; i < key.Colours; ++i )
		{
			var layout = i == 0 ? key.Layout0 : key.Layout1;

			attachments[count] = new AttachmentDescription
			{
				Format = i == 0 ? key.Colour0 : key.Colour1,
				Samples = SampleCountFlags.Count1Bit,
				LoadOp = i == 0 ? key.Load0 : key.Load1,
				StoreOp = i == 0 ? key.Store0 : key.Store1,
				StencilLoadOp = AttachmentLoadOp.DontCare,
				StencilStoreOp = AttachmentStoreOp.DontCare,

				//In and out as it came. The renderer moves its own pictures between layouts and knows where each
				//of them is; a pass that moved one behind its back would make every barrier after it a lie.
				InitialLayout = layout,
				FinalLayout = layout,
			};

			colours[i] = new AttachmentReference { Attachment = count, Layout = layout };
			count++;
		}

		var depth = new AttachmentReference { Attachment = count, Layout = key.LayoutDepth };

		if ( key.Depth != Format.Undefined )
		{
			attachments[count] = new AttachmentDescription
			{
				Format = key.Depth,
				Samples = SampleCountFlags.Count1Bit,
				LoadOp = key.LoadDepth,
				StoreOp = key.StoreDepth,
				StencilLoadOp = AttachmentLoadOp.DontCare,
				StencilStoreOp = AttachmentStoreOp.DontCare,
				InitialLayout = key.LayoutDepth,
				FinalLayout = key.LayoutDepth,
			};

			count++;
		}

		var subpass = new SubpassDescription
		{
			PipelineBindPoint = PipelineBindPoint.Graphics,
			ColorAttachmentCount = key.Colours,
			PColorAttachments = key.Colours > 0 ? colours : null,
			PDepthStencilAttachment = key.Depth != Format.Undefined ? &depth : null,
		};

		//Every eye at once where the pass draws more than one - the one subpass goes to each layer the mask
		//names, and the eyes are said to see much the same, which lets a driver share work between them.
		var viewMask = key.ViewMask;

		var views = new RenderPassMultiviewCreateInfo
		{
			SType = StructureType.RenderPassMultiviewCreateInfo,
			SubpassCount = 1,
			PViewMasks = &viewMask,
			CorrelationMaskCount = 1,
			PCorrelationMasks = &viewMask,
		};

		var info = new RenderPassCreateInfo
		{
			SType = StructureType.RenderPassCreateInfo,
			PNext = viewMask != 0 ? &views : null,
			AttachmentCount = count,
			PAttachments = attachments,
			SubpassCount = 1,
			PSubpasses = &subpass,
		};

		RenderPass made;

		if ( VkDevice.vk.CreateRenderPass( VkDevice.device, &info, null, &made ) != Result.Success )
		{
			VkDevice.SetText( "a render pass would not be made" );
			return default;
		}

		passes[key] = made;

		return made;
	}

	//The pass a pipeline is made against and a framebuffer is made for: the formats, and nothing about
	//what is done with them - which is all that has to agree between a pass and what is used in it.
	private RenderPass ShapeOf( in VkPassFormats formats ) => PassFor( new PassKey(
		formats.Colours > 0 ? formats.Colour0 : Format.Undefined,
		formats.Colours > 1 ? formats.Colour1 : Format.Undefined,
		formats.Colours, formats.Depth,
		AttachmentLoadOp.Load, AttachmentLoadOp.Load, AttachmentLoadOp.Load,
		AttachmentStoreOp.Store, AttachmentStoreOp.Store, AttachmentStoreOp.Store,
		ImageLayout.ColorAttachmentOptimal, ImageLayout.ColorAttachmentOptimal,
		ImageLayout.DepthStencilAttachmentOptimal, formats.ViewMask ) );

	public override bool DescribePass( GraphicsPipelineCreateInfo* pipeline, PipelineRenderingCreateInfo* rendering )
	{
		var formats = new VkPassFormats
		{
			Colours = rendering->ColorAttachmentCount,
			Colour0 = rendering->ColorAttachmentCount > 0 ? rendering->PColorAttachmentFormats[0] : Format.Undefined,
			Colour1 = rendering->ColorAttachmentCount > 1 ? rendering->PColorAttachmentFormats[1] : Format.Undefined,
			Depth = rendering->DepthAttachmentFormat,
			ViewMask = rendering->ViewMask,
		};

		var pass = ShapeOf( formats );

		if ( pass.Handle == 0 ) return false;

		pipeline->PNext = null;
		pipeline->RenderPass = pass;
		pipeline->Subpass = 0;

		return true;
	}

	public override void BeginRendering( CommandBuffer commands, RenderingInfo* info, in VkPassFormats formats )
	{
		var colours = info->ColorAttachmentCount;
		var first = colours > 0 ? info->PColorAttachments : null;
		var second = colours > 1 ? info->PColorAttachments + 1 : null;
		var depth = formats.Depth != Format.Undefined ? info->PDepthAttachment : null;

		var pass = PassFor( new PassKey(
			colours > 0 ? formats.Colour0 : Format.Undefined,
			colours > 1 ? formats.Colour1 : Format.Undefined,
			colours, depth is null ? Format.Undefined : formats.Depth,
			first is null ? AttachmentLoadOp.Load : first->LoadOp,
			second is null ? AttachmentLoadOp.Load : second->LoadOp,
			depth is null ? AttachmentLoadOp.Load : depth->LoadOp,
			first is null ? AttachmentStoreOp.Store : first->StoreOp,
			second is null ? AttachmentStoreOp.Store : second->StoreOp,
			depth is null ? AttachmentStoreOp.Store : depth->StoreOp,
			first is null ? ImageLayout.ColorAttachmentOptimal : Known( first->ImageLayout ),
			second is null ? ImageLayout.ColorAttachmentOptimal : Known( second->ImageLayout ),
			depth is null ? ImageLayout.DepthStencilAttachmentOptimal : Known( depth->ImageLayout ),
			info->ViewMask ) );

		if ( pass.Handle == 0 ) return;

		var key = new TargetKey(
			colours > 0 ? formats.Colour0 : Format.Undefined,
			colours > 1 ? formats.Colour1 : Format.Undefined,
			colours, depth is null ? Format.Undefined : formats.Depth,
			first is null ? 0 : first->ImageView.Handle,
			second is null ? 0 : second->ImageView.Handle,
			depth is null ? 0 : depth->ImageView.Handle,
			info->RenderArea.Offset.X < 0 ? 0u : (uint)info->RenderArea.Offset.X + info->RenderArea.Extent.Width,
			info->RenderArea.Offset.Y < 0 ? 0u : (uint)info->RenderArea.Offset.Y + info->RenderArea.Extent.Height,
			info->LayerCount == 0 ? 1u : info->LayerCount );

		if ( !targets.TryGetValue( key, out var target ) )
		{
			var views = stackalloc ImageView[3];
			var count = 0u;

			if ( first is not null ) views[count++] = first->ImageView;
			if ( second is not null ) views[count++] = second->ImageView;
			if ( depth is not null ) views[count++] = depth->ImageView;

			var made = new FramebufferCreateInfo
			{
				SType = StructureType.FramebufferCreateInfo,
				RenderPass = pass,
				AttachmentCount = count,
				PAttachments = views,
				Width = key.Width,
				Height = key.Height,
				Layers = key.Layers,
			};

			Framebuffer framebuffer;

			if ( VkDevice.vk.CreateFramebuffer( VkDevice.device, &made, null, &framebuffer ) != Result.Success )
			{
				VkDevice.SetText( "a framebuffer would not be made" );
				return;
			}

			targets[key] = target = framebuffer;
		}

		//In the order the pass lists them: the colours, then the depth.
		var clears = stackalloc ClearValue[3];
		var cleared = 0u;

		if ( first is not null ) clears[cleared++] = first->ClearValue;
		if ( second is not null ) clears[cleared++] = second->ClearValue;
		if ( depth is not null ) clears[cleared++] = depth->ClearValue;

		var begin = new RenderPassBeginInfo
		{
			SType = StructureType.RenderPassBeginInfo,
			RenderPass = pass,
			Framebuffer = target,
			RenderArea = info->RenderArea,
			ClearValueCount = cleared,
			PClearValues = clears,
		};

		VkDevice.vk.CmdBeginRenderPass( commands, &begin, SubpassContents.Inline );
	}

	public override void EndRendering( CommandBuffer commands )
		=> VkDevice.vk.CmdEndRenderPass( commands );

	//Whoever destroys a view says so first - see VkDevice.DestroyView - and by then the card
	//is provably done with it, so what was drawn into it through here is done with as well.
	public override void ForgetView( ImageView view )
	{
		if ( view.Handle == 0 || targets.Count == 0 ) return;

		doomed.Clear();

		foreach ( var key in targets.Keys )
		{
			if ( key.View0 == view.Handle || key.View1 == view.Handle || key.ViewDepth == view.Handle )
				doomed.Add( key );
		}

		foreach ( var key in doomed )
		{
			VkDevice.vk.DestroyFramebuffer( VkDevice.device, targets[key], null );
			targets.Remove( key );
		}
	}

	public override void Release()
	{
		foreach ( var target in targets.Values )
			VkDevice.vk.DestroyFramebuffer( VkDevice.device, target, null );

		foreach ( var pass in passes.Values )
			VkDevice.vk.DestroyRenderPass( VkDevice.device, pass, null );

		targets.Clear();
		passes.Clear();
	}

	//-- dependencies ----------------------------------------------------------

	//A 1.3 access as 1.1 knows it. The low word is the same bits; the three above it
	//split "a shader reads this" by what kind of thing is read, which 1.1 never asked.
	private static AccessFlags Known( AccessFlags2 access )
	{
		var known = (AccessFlags)(uint)( (ulong)access & 0xFFFFFFFFul );

		if ( ( access & ( AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit ) ) != 0 )
			known |= AccessFlags.ShaderReadBit;

		if ( ( access & AccessFlags2.ShaderStorageWriteBit ) != 0 )
			known |= AccessFlags.ShaderWriteBit;

		return known;
	}

	//A 1.3 stage as 1.1 knows it. The low word is the same bits; the copy, blit, resolve and clear stages
	//above it are all of them 1.1's one transfer stage, and dropped they left a barrier waiting on nothing.
	private static PipelineStageFlags Known( PipelineStageFlags2 stages )
	{
		var known = (PipelineStageFlags)(uint)( (ulong)stages & 0xFFFFFFFFul );

		const PipelineStageFlags2 transfers = PipelineStageFlags2.CopyBit | PipelineStageFlags2.BlitBit |
			PipelineStageFlags2.ResolveBit | PipelineStageFlags2.ClearBit | PipelineStageFlags2.AllTransferBit;

		if ( ( stages & transfers ) != 0 ) known |= PipelineStageFlags.TransferBit;

		return known;
	}

	public override void Barrier( CommandBuffer commands, DependencyInfo* dependency )
	{
		//One pair of stages for the whole call, which is the older API's shape: everything any of the barriers
		//waits on, and everything any of them holds up. Wider than each asked for where they differ, never narrower.
		PipelineStageFlags from = 0;
		PipelineStageFlags into = 0;

		var memoryCount = dependency->MemoryBarrierCount;
		var bufferCount = dependency->BufferMemoryBarrierCount;
		var imageCount = dependency->ImageMemoryBarrierCount;

		var memory = stackalloc MemoryBarrier[(int)memoryCount];
		var buffers = stackalloc BufferMemoryBarrier[(int)bufferCount];
		var images = stackalloc ImageMemoryBarrier[(int)imageCount];

		for ( var i = 0; i < memoryCount; ++i )
		{
			var asked = dependency->PMemoryBarriers[i];

			from |= Known( asked.SrcStageMask );
			into |= Known( asked.DstStageMask );

			memory[i] = new MemoryBarrier
			{
				SType = StructureType.MemoryBarrier,
				SrcAccessMask = Known( asked.SrcAccessMask ),
				DstAccessMask = Known( asked.DstAccessMask ),
			};
		}

		for ( var i = 0; i < bufferCount; ++i )
		{
			var asked = dependency->PBufferMemoryBarriers[i];

			from |= Known( asked.SrcStageMask );
			into |= Known( asked.DstStageMask );

			buffers[i] = new BufferMemoryBarrier
			{
				SType = StructureType.BufferMemoryBarrier,
				SrcAccessMask = Known( asked.SrcAccessMask ),
				DstAccessMask = Known( asked.DstAccessMask ),
				SrcQueueFamilyIndex = asked.SrcQueueFamilyIndex,
				DstQueueFamilyIndex = asked.DstQueueFamilyIndex,
				Buffer = asked.Buffer,
				Offset = asked.Offset,
				Size = asked.Size,
			};
		}

		for ( var i = 0; i < imageCount; ++i )
		{
			var asked = dependency->PImageMemoryBarriers[i];

			from |= Known( asked.SrcStageMask );
			into |= Known( asked.DstStageMask );

			images[i] = new ImageMemoryBarrier
			{
				SType = StructureType.ImageMemoryBarrier,
				SrcAccessMask = Known( asked.SrcAccessMask ),
				DstAccessMask = Known( asked.DstAccessMask ),
				OldLayout = Known( asked.OldLayout ),
				NewLayout = Known( asked.NewLayout ),
				SrcQueueFamilyIndex = asked.SrcQueueFamilyIndex,
				DstQueueFamilyIndex = asked.DstQueueFamilyIndex,
				Image = asked.Image,
				SubresourceRange = asked.SubresourceRange,
			};
		}

		//"No stage" is a thing only the newer API can say. The older one's words for it are the
		//two ends of the pipe: nothing to wait for is the top, nothing held up is the bottom.
		if ( from == 0 ) from = PipelineStageFlags.TopOfPipeBit;
		if ( into == 0 ) into = PipelineStageFlags.BottomOfPipeBit;

		VkDevice.vk.CmdPipelineBarrier( commands, from, into, 0,
			memoryCount, memory, bufferCount, buffers, imageCount, images );
	}

	public override Result SubmitNow( Queue queue, SubmitInfo2* submit, Fence fence )
	{
		var waitCount = submit->WaitSemaphoreInfoCount;
		var commandCount = submit->CommandBufferInfoCount;
		var signalCount = submit->SignalSemaphoreInfoCount;

		var waits = stackalloc Silk.NET.Vulkan.Semaphore[(int)waitCount];
		var stages = stackalloc PipelineStageFlags[(int)waitCount];
		var commands = stackalloc CommandBuffer[(int)commandCount];
		var signals = stackalloc Silk.NET.Vulkan.Semaphore[(int)signalCount];

		for ( var i = 0; i < waitCount; ++i )
		{
			waits[i] = submit->PWaitSemaphoreInfos[i].Semaphore;
			stages[i] = Known( submit->PWaitSemaphoreInfos[i].StageMask );

			if ( stages[i] == 0 ) stages[i] = PipelineStageFlags.TopOfPipeBit;
		}

		for ( var i = 0; i < commandCount; ++i )
			commands[i] = submit->PCommandBufferInfos[i].CommandBuffer;

		for ( var i = 0; i < signalCount; ++i )
			signals[i] = submit->PSignalSemaphoreInfos[i].Semaphore;

		var known = new SubmitInfo
		{
			SType = StructureType.SubmitInfo,
			WaitSemaphoreCount = waitCount,
			PWaitSemaphores = waits,
			PWaitDstStageMask = stages,
			CommandBufferCount = commandCount,
			PCommandBuffers = commands,
			SignalSemaphoreCount = signalCount,
			PSignalSemaphores = signals,
		};

		return VkDevice.vk.QueueSubmit( queue, 1, &known, fence );
	}

	//One stage, as the older call takes: the first of whatever was named, and the
	//end of the pipe for "all of it" - which is when every command before has run.
	public override void Timestamp( CommandBuffer commands, PipelineStageFlags2 stage, QueryPool pool, uint query )
	{
		var known = stage == PipelineStageFlags2.AllCommandsBit || stage == 0
			? PipelineStageFlags.BottomOfPipeBit
			: Known( stage );

		VkDevice.vk.CmdWriteTimestamp( commands, known, pool, query );
	}
}
