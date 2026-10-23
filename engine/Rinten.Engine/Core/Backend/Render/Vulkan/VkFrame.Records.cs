using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Rinten;

//The draws themselves, and the profiler that counts them.
internal static unsafe partial class VkFrame
{
	private static ShaderStageFlags DrawStages => VkDevice.DrawStages;

	internal static int Draw( ulong pipeline, int vertexCount, void* push, int pushSize, ulong texture )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording || vertexCount <= 0 ) return 0;

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );
			if ( bound.Handle == 0 ) return 0;

			var frame = CurrentFrame;

			if ( !Current.TargetPass ) EnsureMainPass();

			BindPipeline( frame, bound );

			if ( push is not null && pushSize > 0 && layout.Handle != 0 )
			{
				//The whole of what was handed over, clamped to the range - this one path does not round down to whole
				//words, because it was written for callers that pass a struct and the C++ did not round it either.
				var size = (uint)pushSize > VkDevice.PushConstantSize ? VkDevice.PushConstantSize : (uint)pushSize;

				VkDevice.vk.CmdPushConstants( frame.Commands, layout, DrawStages, 0, size, push );
			}

			BindTexture( frame, layout, texture );

			//The whole window, every draw, until somebody needs less of it. Dynamic state rather than
			//baked into the pipeline, so a resize does not mean rebuilding every pipeline in the game.
			SetViewport( frame );

			BindFrameSet( frame, layout, frame.NullBones );

			VkDevice.vk.CmdDraw( frame.Commands, (uint)vertexCount, 1, 0, 0 );

			frame.StatDraws++;
			frame.StatTriangles += (ulong)vertexCount / 3;

			DrawTrace.End( DrawTrace.Kind.Draw, traced, inside );

			return 1;
		}
	}

	//The same draw with a set of buffers at set 2 - what a full-screen pass that shades the world out of the
	//pre-pass's picture reads: the cull's candidates, the clusters, the pool, and the picture as numbers.
	internal static int DrawObjects( ulong pipeline, int vertexCount, void* push, int pushSize,
		ulong texture, ulong* buffers, int bufferCount )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording || vertexCount <= 0 ) return 0;

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );
			if ( bound.Handle == 0 || layout.Handle == 0 ) return 0;

			var objects = TransientObjectSet( buffers, bufferCount, "a full-screen draw" );
			if ( objects.Handle == 0 ) return 0;

			var frame = CurrentFrame;

			if ( !Current.TargetPass ) EnsureMainPass();

			BindPipeline( frame, bound );

			if ( push is not null && pushSize > 0 )
			{
				var size = (uint)pushSize > VkDevice.PushConstantSize ? VkDevice.PushConstantSize : (uint)pushSize;

				VkDevice.vk.CmdPushConstants( frame.Commands, layout, DrawStages, 0, size, push );
			}

			BindTexture( frame, layout, texture );
			SetViewport( frame );
			BindFrameSet( frame, layout, frame.NullBones );

			VkDevice.vk.CmdBindDescriptorSets( frame.Commands, PipelineBindPoint.Graphics, layout, 2, 1, &objects, 0, null );

			BindBindless( frame, layout );

			VkDevice.vk.CmdDraw( frame.Commands, (uint)vertexCount, 1, 0, 0 );

			frame.StatDraws++;
			frame.StatTriangles += (ulong)vertexCount / 3;

			DrawTrace.End( DrawTrace.Kind.DrawObjects, traced, inside );

			return 1;
		}
	}

	//Indexed instanced draw with no vertex buffer; the shader pulls vertices and instances from set 2
	internal static int DrawObjectsIndexed( ulong pipeline, ulong indexBuffer, int indexCount,
		int instanceCount, bool wideIndices, byte* pushData, int pushSize,
		ulong texture, ulong* buffers, int bufferCount )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording || indexCount <= 0 || instanceCount <= 0 ) return 0;

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );
			if ( bound.Handle == 0 || layout.Handle == 0 ) return 0;

			var indices = VkBuffers.Get( indexBuffer );
			if ( indices.Handle == 0 ) return 0;

			var objects = TransientObjectSet( buffers, bufferCount, "an instanced draw" );
			if ( objects.Handle == 0 ) return 0;

			var frame = CurrentFrame;

			if ( !Current.TargetPass ) EnsureMainPass();

			BindPipeline( frame, bound );

			PushConstants( frame, layout, DrawStages, pushData, pushSize );

			BindTexture( frame, layout, texture );
			SetViewport( frame );
			BindFrameSet( frame, layout, frame.NullBones );

			VkDevice.vk.CmdBindDescriptorSets( frame.Commands, PipelineBindPoint.Graphics, layout, 2, 1, &objects, 0, null );

			BindBindless( frame, layout );

			//Tracked like every other bind, so a run of draws over one shape - which is what a scene full of sprites is - binds it once.
			if ( frame.LastIndexBuffer.Handle != indices.Handle )
			{
				VkDevice.vk.CmdBindIndexBuffer( frame.Commands, indices, 0,
					wideIndices ? IndexType.Uint32 : IndexType.Uint16 );

				frame.LastIndexBuffer = indices;
			}

			VkDevice.vk.CmdDrawIndexed( frame.Commands, (uint)indexCount, (uint)instanceCount, 0, 0, 0 );

			frame.StatDraws++;
			frame.StatTriangles += (ulong)indexCount / 3 * (ulong)instanceCount;

			DrawTrace.End( DrawTrace.Kind.DrawObjects, traced, inside );

			return 1;
		}
	}

	//Instanced draw reading instance data from a bone-ring slot (portable sprites); blockOffset is slot + 1
	internal static int DrawIndexedInstancedBlock( ulong pipeline, ulong vertexBuffer, ulong indexBuffer,
		int indexCount, int instanceCount, uint blockOffset, int firstIndex, int firstVertex,
		ulong texture, byte* pushData, int pushSize )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording || indexCount <= 0 || instanceCount <= 0 || blockOffset == 0 ) return 0;

			var frame = CurrentFrame;
			var offset = (ulong)blockOffset - 1;

			if ( offset + VkDevice.BoneBlockSize > frame.ScratchUsed ) return 0;

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );
			var vertices = VkBuffers.Get( vertexBuffer );
			var indices = VkBuffers.Get( indexBuffer );

			if ( bound.Handle == 0 || layout.Handle == 0 || vertices.Handle == 0 || indices.Handle == 0 ) return 0;

			if ( !Current.TargetPass ) EnsureMainPass();

			BindPipeline( frame, bound );
			SetViewport( frame );
			PushConstants( frame, layout, DrawStages, pushData, pushSize );
			BindTexture( frame, layout, texture );
			BindFrameSet( frame, layout, offset );

			if ( frame.LastVertexBuffer.Handle != vertices.Handle )
			{
				ulong at = 0;
				VkDevice.vk.CmdBindVertexBuffers( frame.Commands, 0, 1, &vertices, &at );
				frame.LastVertexBuffer = vertices;
			}
			else
			{
				frame.StatSkippedBinds++;
			}

			if ( frame.LastIndexBuffer.Handle != indices.Handle )
			{
				VkDevice.vk.CmdBindIndexBuffer( frame.Commands, indices, 0, IndexType.Uint32 );
				frame.LastIndexBuffer = indices;
			}

			VkDevice.vk.CmdDrawIndexed( frame.Commands, (uint)indexCount, (uint)instanceCount,
				(uint)firstIndex, firstVertex, 0 );

			frame.StatDraws++;
			frame.StatInstances += (uint)instanceCount;
			frame.StatTriangles += (ulong)indexCount / 3 * (ulong)instanceCount;

			DrawTrace.End( DrawTrace.Kind.DrawObjects, traced, inside );

			return 1;
		}
	}

	internal static int DrawBlockInstanced( ulong pipeline, ulong indexBuffer, int indexCount,
		int instanceCount, bool wideIndices, ulong texture, uint blockOffset, byte* pushData, int pushSize )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording || indexCount <= 0 || instanceCount <= 0 || blockOffset == 0 ) return 0;

			var frame = CurrentFrame;
			var offset = (ulong)blockOffset - 1;

			if ( offset + VkDevice.BoneBlockSize > frame.ScratchUsed ) return 0;

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );
			if ( bound.Handle == 0 || layout.Handle == 0 ) return 0;

			var indices = VkBuffers.Get( indexBuffer );
			if ( indices.Handle == 0 ) return 0;

			if ( !Current.TargetPass ) EnsureMainPass();

			BindPipeline( frame, bound );
			PushConstants( frame, layout, DrawStages, pushData, pushSize );
			BindTexture( frame, layout, texture );
			SetViewport( frame );
			BindFrameSet( frame, layout, offset );

			if ( frame.LastIndexBuffer.Handle != indices.Handle )
			{
				VkDevice.vk.CmdBindIndexBuffer( frame.Commands, indices, 0,
					wideIndices ? IndexType.Uint32 : IndexType.Uint16 );

				frame.LastIndexBuffer = indices;
			}

			VkDevice.vk.CmdDrawIndexed( frame.Commands, (uint)indexCount, (uint)instanceCount, 0, 0, 0 );

			frame.StatDraws++;
			frame.StatTriangles += (ulong)indexCount / 3 * (ulong)instanceCount;

			DrawTrace.End( DrawTrace.Kind.DrawObjects, traced, inside );

			return 1;
		}
	}

	internal static int DrawPulledIndirect( ulong pipeline, ulong argsBuffer, int argsOffset,
		int drawCount, ulong texture, byte* pushData, int pushSize, ulong* buffers, int bufferCount )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording || drawCount <= 0 || argsOffset < 0 ) return 0;

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );
			if ( bound.Handle == 0 || layout.Handle == 0 ) return 0;

			var args = VkBuffers.Get( argsBuffer );
			if ( args.Handle == 0 ) return 0;

			var objects = TransientObjectSet( buffers, bufferCount, "an indirect draw" );
			if ( objects.Handle == 0 ) return 0;

			var frame = CurrentFrame;

			if ( !Current.TargetPass ) EnsureMainPass();

			BindPipeline( frame, bound );

			PushConstants( frame, layout, DrawStages, pushData, pushSize );

			BindTexture( frame, layout, texture );
			SetViewport( frame );
			BindFrameSet( frame, layout, frame.NullBones );

			VkDevice.vk.CmdBindDescriptorSets( frame.Commands, PipelineBindPoint.Graphics, layout, 2, 1, &objects, 0, null );

			BindBindless( frame, layout );

			//One command a draw, sixteen bytes each; a device without multi-draw takes them one at a time.
			var stride = (uint)sizeof( DrawIndirectCommand );
			var from = 0u;

			while ( from < (uint)drawCount )
			{
				var most = VkDevice.maxDrawIndirect > 0 ? VkDevice.maxDrawIndirect : 1;
				var take = (uint)drawCount - from < most ? (uint)drawCount - from : most;

				VkDevice.vk.CmdDrawIndirect( frame.Commands, args, (ulong)argsOffset + ( from * (ulong)stride ), take, stride );

				from += take;
			}

			frame.StatDraws++;
			frame.StatIndirect++;

			DrawTrace.End( DrawTrace.Kind.DrawPulledIndirect, traced, inside );

			return 1;
		}
	}

	//The clusters of a list as a workgroup each, where the device has mesh stages: one indirect command of three group counts at argsOffset.
	//Nothing is read from an index buffer - the stage makes a cluster's vertices once each and its triangles out of the cluster's own bytes.
	internal static int DrawMeshIndirect( ulong pipeline, ulong argsBuffer, int argsOffset,
		ulong texture, byte* pushData, int pushSize, ulong* buffers, int bufferCount )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording || argsOffset < 0 || !VkDevice.meshShaders ) return 0;

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );
			if ( bound.Handle == 0 || layout.Handle == 0 ) return 0;

			var args = VkBuffers.Get( argsBuffer );
			if ( args.Handle == 0 ) return 0;

			var objects = TransientObjectSet( buffers, bufferCount, "a mesh draw" );
			if ( objects.Handle == 0 ) return 0;

			var frame = CurrentFrame;

			if ( !Current.TargetPass ) EnsureMainPass();

			BindPipeline( frame, bound );

			PushConstants( frame, layout, DrawStages, pushData, pushSize );

			BindTexture( frame, layout, texture );
			SetViewport( frame );
			BindFrameSet( frame, layout, frame.NullBones );

			VkDevice.vk.CmdBindDescriptorSets( frame.Commands, PipelineBindPoint.Graphics, layout, 2, 1, &objects, 0, null );

			BindBindless( frame, layout );

			VkDevice.extMeshShader.CmdDrawMeshTasksIndirect( frame.Commands, args, (ulong)argsOffset, 1,
				(uint)sizeof( DrawMeshTasksIndirectCommandEXT ) );

			frame.StatDraws++;
			frame.StatIndirect++;

			DrawTrace.End( DrawTrace.Kind.DrawMeshIndirect, traced, inside );

			return 1;
		}
	}

	//Pulled draw indexed by a GPU-written index buffer, via one indexed indirect command (clusters)
	internal static int DrawPulledIndexedIndirect( ulong pipeline, ulong argsBuffer, int argsOffset, ulong indexBuffer,
		ulong texture, byte* pushData, int pushSize, ulong* buffers, int bufferCount )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording || argsOffset < 0 ) return 0;

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );
			if ( bound.Handle == 0 || layout.Handle == 0 ) return 0;

			var args = VkBuffers.Get( argsBuffer );
			var indices = VkBuffers.Get( indexBuffer );
			if ( args.Handle == 0 || indices.Handle == 0 ) return 0;

			var objects = TransientObjectSet( buffers, bufferCount, "an indexed indirect draw" );
			if ( objects.Handle == 0 ) return 0;

			var frame = CurrentFrame;

			if ( !Current.TargetPass ) EnsureMainPass();

			BindPipeline( frame, bound );

			PushConstants( frame, layout, DrawStages, pushData, pushSize );

			BindTexture( frame, layout, texture );
			SetViewport( frame );
			BindFrameSet( frame, layout, frame.NullBones );

			VkDevice.vk.CmdBindDescriptorSets( frame.Commands, PipelineBindPoint.Graphics, layout, 2, 1, &objects, 0, null );

			BindBindless( frame, layout );

			if ( frame.LastIndexBuffer.Handle != indices.Handle )
			{
				VkDevice.vk.CmdBindIndexBuffer( frame.Commands, indices, 0, IndexType.Uint32 );
				frame.LastIndexBuffer = indices;
			}

			VkDevice.vk.CmdDrawIndexedIndirect( frame.Commands, args, (ulong)argsOffset, 1, (uint)sizeof( DrawIndexedIndirectCommand ) );

			frame.StatDraws++;
			frame.StatIndirect++;

			DrawTrace.End( DrawTrace.Kind.DrawPulledIndexedIndirect, traced, inside );

			return 1;
		}
	}

	internal static int DrawDynamic( ulong pipeline, byte* vertexData, int vertexCount, int vertexStride,
		ulong texture, byte* pushData, int pushSize )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording || vertexData is null ) return 0;
			if ( vertexCount <= 0 || vertexStride <= 0 ) return 0;

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );

			if ( bound.Handle == 0 ) return 0;

			var frame = CurrentFrame;

			var bytes = (ulong)vertexCount * (ulong)vertexStride;

			if ( !EnsureScratch( frame, bytes ) )
			{
				VkDevice.SetText( "the frame's scratch space would not grow" );
				return 0;
			}

			var copying = DrawTrace.Begin();

			var offset = frame.ScratchUsed;
			System.Buffer.MemoryCopy( vertexData, frame.ScratchMapped + offset, bytes, bytes );
			frame.ScratchUsed += bytes;

			DrawTrace.Phase( DrawTrace.Kind.ScratchCopy, copying );

			if ( !Current.TargetPass ) EnsureMainPass();

			BindPipeline( frame, bound );

			SetViewport( frame );

			PushConstants( frame, layout, DrawStages, pushData, pushSize );

			BindTexture( frame, layout, texture );

			BindFrameSet( frame, layout, frame.NullBones );

			var scratch = frame.Scratch;
			VkDevice.vk.CmdBindVertexBuffers( frame.Commands, 0, 1, &scratch, &offset );

			//Scratch at an offset is not "a buffer at zero", so the next mesh draw has to say its buffers again.
			frame.LastVertexBuffer = default;

			VkDevice.vk.CmdDraw( frame.Commands, (uint)vertexCount, 1, 0, 0 );

			frame.StatDraws++;
			frame.StatDynamic++;
			frame.StatTriangles += (ulong)vertexCount / 3;

			DrawTrace.End( DrawTrace.Kind.DrawDynamic, traced, inside );

			return 1;
		}
	}

	internal static int DrawIndexed( ulong pipeline, ulong vertexBuffer, ulong indexBuffer,
		int firstIndex, int indexCount, int firstVertex, ulong texture, byte* pushData, int pushSize )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording || firstIndex < 0 || indexCount <= 0 ) return 0;

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );
			var vertices = VkBuffers.Get( vertexBuffer );
			var indices = VkBuffers.Get( indexBuffer );

			if ( bound.Handle == 0 || vertices.Handle == 0 || indices.Handle == 0 ) return 0;

			var frame = CurrentFrame;

			if ( !Current.TargetPass ) EnsureMainPass();

			BindPipeline( frame, bound );

			SetViewport( frame );

			PushConstants( frame, layout, DrawStages, pushData, pushSize );

			BindTexture( frame, layout, texture );

			BindFrameSet( frame, layout, frame.NullBones );

			if ( frame.LastVertexBuffer.Handle != vertices.Handle )
			{
				ulong offset = 0;
				VkDevice.vk.CmdBindVertexBuffers( frame.Commands, 0, 1, &vertices, &offset );
				frame.LastVertexBuffer = vertices;
			}
			else
			{
				frame.StatSkippedBinds++;
			}

			if ( frame.LastIndexBuffer.Handle != indices.Handle )
			{
				VkDevice.vk.CmdBindIndexBuffer( frame.Commands, indices, 0, IndexType.Uint32 );
				frame.LastIndexBuffer = indices;
			}

			VkDevice.vk.CmdDrawIndexed( frame.Commands, (uint)indexCount, 1, (uint)firstIndex, firstVertex, 0 );

			frame.StatDraws++;
			frame.StatTriangles += (ulong)indexCount / 3;

			DrawTrace.End( DrawTrace.Kind.DrawIndexed, traced, inside );

			return 1;
		}
	}

	internal static int DrawPulled( ulong pipeline, ulong geometryBuffer, ulong dataBuffer,
		int vertexCount, ulong texture, byte* pushData, int pushSize )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording || vertexCount <= 0 ) return 0;

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );

			if ( bound.Handle == 0 ) return 0;

			//The set is what the draw is for: without it the shader has no vertices to read, and
			//drawing anyway is a shader reading whatever the last object left bound at set 2.
			var objects = VkBuffers.ObjectSetGet( geometryBuffer, dataBuffer );
			if ( objects.Handle == 0 ) return 0;

			var frame = CurrentFrame;

			if ( !Current.TargetPass ) EnsureMainPass();

			BindPipeline( frame, bound );

			SetViewport( frame );

			PushConstants( frame, layout, DrawStages, pushData, pushSize );

			BindTexture( frame, layout, texture );

			BindFrameSet( frame, layout, frame.NullBones );

			//Bound every time. Set 2 is per object by definition, so there is no run of draws that
			//share one to skip a bind for - and tracking the last would cost more than the bind.
			VkDevice.vk.CmdBindDescriptorSets( frame.Commands, PipelineBindPoint.Graphics, layout, 2, 1, &objects, 0, null );

			//No vertex buffer and no index buffer: the shader reads its vertices out of set 2 by SV_VertexID. Whatever
			//was bound stays bound and is not read, which is why the pipeline for this must be the "no vertices" layout.
			VkDevice.vk.CmdDraw( frame.Commands, (uint)vertexCount, 1, 0, 0 );

			frame.StatDraws++;
			frame.StatTriangles += (ulong)vertexCount / 3;

			DrawTrace.End( DrawTrace.Kind.DrawPulled, traced, inside );

			return 1;
		}
	}

	//-- skinned and morphed ------------------------------------------------

	//Records a skinned draw against a pose that is already in the scratch. Everything from the pipeline to the draw call, shared by
	//the two ways of getting here: handing the bones over with the draw, and handing them over once and then naming where they went.
	private static int RecordSkinned( Frame frame, Pipeline bound, PipelineLayout layout,
		VkBuffer vertices, VkBuffer skin, VkBuffer indices,
		int firstIndex, int indexCount, int firstVertex,
		ulong texture, ulong boneOffset, byte* pushData, int pushSize )
	{
		if ( !Current.TargetPass ) EnsureMainPass();

		BindPipeline( frame, bound );

		SetViewport( frame );

		BindTexture( frame, layout, texture );

		BindFrameSet( frame, layout, boneOffset );

		PushConstants( frame, layout, DrawStages, pushData, pushSize );

		if ( frame.LastVertexBuffer.Handle != vertices.Handle || frame.LastSkinBuffer.Handle != skin.Handle )
		{
			var buffers = stackalloc VkBuffer[2];
			buffers[0] = vertices;
			buffers[1] = skin;

			var offsets = stackalloc ulong[2];
			offsets[0] = 0;
			offsets[1] = 0;

			VkDevice.vk.CmdBindVertexBuffers( frame.Commands, 0, 2, buffers, offsets );

			frame.LastVertexBuffer = vertices;
			frame.LastSkinBuffer = skin;
		}
		else
		{
			frame.StatSkippedBinds++;
		}

		if ( frame.LastIndexBuffer.Handle != indices.Handle )
		{
			VkDevice.vk.CmdBindIndexBuffer( frame.Commands, indices, 0, IndexType.Uint32 );
			frame.LastIndexBuffer = indices;
		}

		//The mesh's place in the pool: its indices count from its own first vertex, in the skin beside it as much as in the vertices
		VkDevice.vk.CmdDrawIndexed( frame.Commands, (uint)indexCount, 1, (uint)firstIndex, firstVertex, 0 );

		frame.StatDraws++;
		frame.StatSkinned++;
		frame.StatTriangles += (ulong)indexCount / 3;

		return 1;
	}

	//The same draw with blend shapes on top: a third vertex buffer saying which deltas each
	//vertex owns, the deltas themselves at set 2, and the weights out of the frame's scratch.
	private static int RecordMorphed( Frame frame, Pipeline bound, PipelineLayout layout,
		VkBuffer vertices, VkBuffer skin, VkBuffer runs, VkBuffer indices, DescriptorSet deltas,
		int firstIndex, int indexCount, int firstVertex, int firstRun,
		ulong texture, ulong boneOffset, ulong morphOffset, byte* pushData, int pushSize )
	{
		if ( !Current.TargetPass ) EnsureMainPass();

		BindPipeline( frame, bound );

		SetViewport( frame );

		BindTexture( frame, layout, texture );

		BindFrameSet( frame, layout, boneOffset, morphOffset );

		VkDevice.vk.CmdBindDescriptorSets( frame.Commands, PipelineBindPoint.Graphics, layout, 2, 1, &deltas, 0, null );

		PushConstants( frame, layout, DrawStages, pushData, pushSize );

		//Three bindings, offset per binding: vertices and skin start in the pool, runs start in the model
		{
			var buffers = stackalloc VkBuffer[3];
			buffers[0] = vertices;
			buffers[1] = skin;
			buffers[2] = runs;

			var offsets = stackalloc ulong[3];
			offsets[0] = (ulong)firstVertex * VkDevice.EngineVertexStride;
			offsets[1] = (ulong)firstVertex * VkDevice.EngineSideStride;
			offsets[2] = (ulong)firstRun * VkDevice.EngineSideStride;

			VkDevice.vk.CmdBindVertexBuffers( frame.Commands, 0, 3, buffers, offsets );
		}

		//Bound at an offset, so the next draw of the same buffers has to bind again.
		frame.LastVertexBuffer = default;
		frame.LastSkinBuffer = default;

		if ( frame.LastIndexBuffer.Handle != indices.Handle )
		{
			VkDevice.vk.CmdBindIndexBuffer( frame.Commands, indices, 0, IndexType.Uint32 );
			frame.LastIndexBuffer = indices;
		}

		VkDevice.vk.CmdDrawIndexed( frame.Commands, (uint)indexCount, 1, (uint)firstIndex, 0, 0 );

		frame.StatDraws++;
		frame.StatSkinned++;
		frame.StatTriangles += (ulong)indexCount / 3;

		return 1;
	}

	internal static int DrawSkinned( ulong pipeline, ulong vertexBuffer, ulong skinBuffer, ulong indexBuffer,
		int firstIndex, int indexCount, int firstVertex, ulong texture, float* bones, int boneCount,
		byte* pushData, int pushSize )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording || firstIndex < 0 || indexCount <= 0 ) return 0;
			if ( bones is null || boneCount <= 0 ) return 0;

			//Refused rather than truncated: a character missing its last bones is a character
			//with limbs at the origin, which looks like a renderer bug and is not one.
			if ( (uint)boneCount > VkDevice.MaxBones )
			{
				VkDevice.SetText( "more bones than a skinned draw can carry" );
				return 0;
			}

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );
			var vertices = VkBuffers.Get( vertexBuffer );
			var skin = VkBuffers.Get( skinBuffer );
			var indices = VkBuffers.Get( indexBuffer );

			if ( bound.Handle == 0 || vertices.Handle == 0 || skin.Handle == 0 || indices.Handle == 0 ) return 0;

			var frame = CurrentFrame;

			if ( !PutBones( frame, bones, boneCount, out var offset ) )
			{
				VkDevice.SetText( "no room in the frame for the bones" );
				return 0;
			}

			var drawn = RecordSkinned( frame, bound, layout, vertices, skin, indices,
				firstIndex, indexCount, firstVertex, texture, offset, pushData, pushSize );

			DrawTrace.End( DrawTrace.Kind.DrawSkinned, traced, inside );

			return drawn;
		}
	}

	internal static uint Bones( float* bones, int boneCount )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording ) return 0;
			if ( bones is null || boneCount <= 0 ) return 0;

			if ( (uint)boneCount > VkDevice.MaxBones )
			{
				VkDevice.SetText( "more bones than a skinned draw can carry" );
				return 0;
			}

			var frame = CurrentFrame;

			if ( !PutBones( frame, bones, boneCount, out var offset ) )
			{
				VkDevice.SetText( "no room in the frame for the bones" );
				return 0;
			}

			return (uint)offset + 1;
		}
	}

	internal static uint MorphWeights( float* weights, int count )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording ) return 0;
			if ( weights is null || count <= 0 ) return 0;

			if ( (uint)count > VkDevice.MaxMorphs )
			{
				VkDevice.SetText( "more blend shapes than a draw can carry" );
				return 0;
			}

			var frame = CurrentFrame;

			if ( !PutMorphWeights( frame, weights, count, out var offset ) )
			{
				VkDevice.SetText( "no room in the frame for the blend shape weights" );
				return 0;
			}

			return (uint)offset + 1;
		}
	}

	internal static int DrawSkinnedAt( ulong pipeline, ulong vertexBuffer, ulong skinBuffer, ulong indexBuffer,
		int firstIndex, int indexCount, int firstVertex, ulong texture, uint boneOffset,
		byte* pushData, int pushSize )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording || firstIndex < 0 || indexCount <= 0 ) return 0;
			if ( boneOffset == 0 ) return 0;

			var frame = CurrentFrame;

			//It has to name a pose this frame actually wrote. An offset kept from an earlier frame points into scratch that
			//has since been used for something else, and drawing a character by the camera block is not a thing to do quietly.
			var offset = (ulong)boneOffset - 1;

			if ( offset + VkDevice.BoneBlockSize > frame.ScratchUsed )
			{
				VkDevice.SetText( "a pose from another frame" );
				return 0;
			}

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );
			var vertices = VkBuffers.Get( vertexBuffer );
			var skin = VkBuffers.Get( skinBuffer );
			var indices = VkBuffers.Get( indexBuffer );

			if ( bound.Handle == 0 || vertices.Handle == 0 || skin.Handle == 0 || indices.Handle == 0 ) return 0;

			var drawn = RecordSkinned( frame, bound, layout, vertices, skin, indices,
				firstIndex, indexCount, firstVertex, texture, offset, pushData, pushSize );

			DrawTrace.End( DrawTrace.Kind.DrawSkinnedAt, traced, inside );

			return drawn;
		}
	}

	internal static int DrawMorphedIndexed( ulong pipeline, ulong vertexBuffer, ulong runBuffer,
		ulong deltaBuffer, ulong indexBuffer, int firstIndex, int indexCount, int firstVertex, int firstRun,
		ulong texture, uint morphOffset, byte* pushData, int pushSize )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording || firstIndex < 0 || indexCount <= 0 ) return 0;
			if ( morphOffset == 0 ) return 0;

			var frame = CurrentFrame;

			var morphs = (ulong)morphOffset - 1;

			if ( morphs + VkDevice.MorphBlockSize > frame.ScratchUsed )
			{
				VkDevice.SetText( "blend shape weights from another frame" );
				return 0;
			}

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );
			var vertices = VkBuffers.Get( vertexBuffer );
			var runs = VkBuffers.Get( runBuffer );
			var indices = VkBuffers.Get( indexBuffer );

			if ( bound.Handle == 0 || vertices.Handle == 0 || runs.Handle == 0 || indices.Handle == 0 ) return 0;

			var deltas = VkBuffers.ObjectSetGet( deltaBuffer, deltaBuffer );
			if ( deltas.Handle == 0 ) return 0;

			if ( !Current.TargetPass ) EnsureMainPass();

			BindPipeline( frame, bound );

			SetViewport( frame );

			BindTexture( frame, layout, texture );

			//A rigid draw stands where its push constants say, so the bone window is the empty one every other rigid draw uses - only the weights move.
			BindFrameSet( frame, layout, frame.NullBones, morphs );

			VkDevice.vk.CmdBindDescriptorSets( frame.Commands, PipelineBindPoint.Graphics, layout, 2, 1, &deltas, 0, null );

			PushConstants( frame, layout, DrawStages, pushData, pushSize );

			//Offset per binding, not by vertex offset: pool vertices and model blend-shape runs start differently
			{
				var buffers = stackalloc VkBuffer[2];
				buffers[0] = vertices;
				buffers[1] = runs;

				var offsets = stackalloc ulong[2];
				offsets[0] = (ulong)firstVertex * VkDevice.EngineVertexStride;
				offsets[1] = (ulong)firstRun * VkDevice.EngineSideStride;

				VkDevice.vk.CmdBindVertexBuffers( frame.Commands, 0, 2, buffers, offsets );
			}

			//Bound at an offset, so the next draw of the same buffer has to bind again.
			frame.LastVertexBuffer = default;
			frame.LastSkinBuffer = default;

			if ( frame.LastIndexBuffer.Handle != indices.Handle )
			{
				VkDevice.vk.CmdBindIndexBuffer( frame.Commands, indices, 0, IndexType.Uint32 );
				frame.LastIndexBuffer = indices;
			}

			VkDevice.vk.CmdDrawIndexed( frame.Commands, (uint)indexCount, 1, (uint)firstIndex, 0, 0 );

			frame.StatDraws++;
			frame.StatTriangles += (ulong)indexCount / 3;

			DrawTrace.End( DrawTrace.Kind.DrawMorphedIndexed, traced, inside );

			return 1;
		}
	}

	internal static int DrawMorphedAt( ulong pipeline, ulong vertexBuffer, ulong skinBuffer, ulong runBuffer,
		ulong deltaBuffer, ulong indexBuffer, int firstIndex, int indexCount, int firstVertex, int firstRun,
		ulong texture, uint boneOffset, uint morphOffset, byte* pushData, int pushSize )
	{
		var traced = DrawTrace.Begin();

		lock ( VkDevice.renderLock )
		{
			var inside = DrawTrace.Locked( traced );

			if ( !Current.Recording || firstIndex < 0 || indexCount <= 0 ) return 0;
			if ( boneOffset == 0 || morphOffset == 0 ) return 0;

			var frame = CurrentFrame;

			//Both have to name something this frame wrote, for the same reason a pose does: an offset
			//kept from an earlier frame points into scratch that has since been used for something else.
			var bones = (ulong)boneOffset - 1;
			var morphs = (ulong)morphOffset - 1;

			if ( bones + VkDevice.BoneBlockSize > frame.ScratchUsed ||
				morphs + VkDevice.MorphBlockSize > frame.ScratchUsed )
			{
				VkDevice.SetText( "a pose from another frame" );
				return 0;
			}

			VkPipelines.GetBoth( pipeline, out var bound, out var layout );
			var vertices = VkBuffers.Get( vertexBuffer );
			var skin = VkBuffers.Get( skinBuffer );
			var runs = VkBuffers.Get( runBuffer );
			var indices = VkBuffers.Get( indexBuffer );

			if ( bound.Handle == 0 || vertices.Handle == 0 || skin.Handle == 0 ||
				runs.Handle == 0 || indices.Handle == 0 ) return 0;

			//The deltas, at set 2. Without them the shader reads whatever the last object left bound there, which is somebody else's face.
			var deltas = VkBuffers.ObjectSetGet( deltaBuffer, deltaBuffer );
			if ( deltas.Handle == 0 ) return 0;

			var drawn = RecordMorphed( frame, bound, layout, vertices, skin, runs, indices, deltas,
				firstIndex, indexCount, firstVertex, firstRun, texture, bones, morphs, pushData, pushSize );

			DrawTrace.End( DrawTrace.Kind.DrawMorphedAt, traced, inside );

			return drawn;
		}
	}

	//-- reading a buffer back ----------------------------------------------

	internal static int BufferCapture( ulong buffer )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording ) return 0;

			var source = VkBuffers.Get( buffer );
			if ( source.Handle == 0 ) return 0;

			var size = VkBuffers.BytesOf( buffer );
			if ( size == 0 ) return 0;

			var frame = CurrentFrame;
			if ( frame.Commands.Handle == 0 ) return 0;

			var vk = VkDevice.vk;
			var device = VkDevice.device;

			if ( !frame.Readbacks.TryGetValue( buffer, out var readback ) )
			{
				readback = new Frame.Readback();
				frame.Readbacks[buffer] = readback;
			}

			//Made once per frame slot and kept mapped. The old synchronous read made a buffer, an allocation, a pool,
			//a command buffer and a fence on every call and destroyed all five again - nine times a frame, in a frame.
			if ( readback.Staging.Handle == 0 || readback.Size < size )
			{
				if ( readback.Mapped is not null ) vk.UnmapMemory( device, readback.Memory );
				if ( readback.Staging.Handle != 0 ) VkDevice.RetireBuffer( readback.Staging, readback.Memory );

				readback.Staging = default;
				readback.Memory = default;
				readback.Mapped = null;
				readback.Size = 0;
				readback.Filled = false;

				var info = new BufferCreateInfo
				{
					SType = StructureType.BufferCreateInfo,
					Size = size,
					Usage = BufferUsageFlags.TransferDstBit,
					SharingMode = SharingMode.Exclusive,
				};

				VkBuffer staging;
				if ( vk.CreateBuffer( device, &info, null, &staging ) != Result.Success ) return 0;

				readback.Staging = staging;

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

				readback.Memory = memory;

				if ( !had ||
					vk.BindBufferMemory( device, staging, memory, 0 ) != Result.Success ||
					vk.MapMemory( device, memory, 0, Vk.WholeSize, 0, &mapped ) != Result.Success )
				{
					return 0;
				}

				readback.Mapped = (byte*)mapped;
				readback.Size = size;
			}

			var copy = new BufferCopy { Size = size };

			vk.CmdCopyBuffer( frame.Commands, source, readback.Staging, 1, &copy );

			//So the host may read what the copy wrote once this frame's fence is signalled. Coherent memory needs
			//no flush; it does need the write to have been made available to the host, and that is what this says.
			var barrier = new MemoryBarrier2
			{
				SType = StructureType.MemoryBarrier2,
				SrcStageMask = PipelineStageFlags2.CopyBit,
				SrcAccessMask = AccessFlags2.TransferWriteBit,
				DstStageMask = PipelineStageFlags2.HostBit,
				DstAccessMask = AccessFlags2.HostReadBit,
			};

			var dependency = new DependencyInfo
			{
				SType = StructureType.DependencyInfo,
				MemoryBarrierCount = 1,
				PMemoryBarriers = &barrier,
			};

			VkDevice.dialect.Barrier( frame.Commands, &dependency );

			readback.Filled = true;

			return 1;
		}
	}

	internal static int BufferTake( ulong buffer, byte* outBytes, int capacity )
	{
		lock ( VkDevice.renderLock )
		{
			if ( outBytes is null || capacity <= 0 ) return 0;

			var frame = CurrentFrame;

			if ( !frame.Readbacks.TryGetValue( buffer, out var readback ) ) return 0;

			if ( !readback.Filled || readback.Mapped is null ) return 0;
			if ( readback.Size < (ulong)capacity ) return 0;

			//Safe without waiting for anything: this frame slot's fence was waited before a single command was
			//recorded into it, which is exactly the proof that the copy recorded here last time round has finished.
			System.Buffer.MemoryCopy( readback.Mapped, outBytes, capacity, capacity );

			return capacity;
		}
	}

	//-- the profiler -------------------------------------------------------

	//Whether the zones are recorded at all - see CoreRenderProfiler.ZonesWanted. Said once a loop, by the engine.
	internal static bool ZonesWanted;

	internal static int ZoneBegin( string name )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || name is null ) return 0;

			var frame = CurrentFrame;

			//Nobody is reading them: refused the way a zone past the slots is, so the matching end is swallowed and a
			//flag that turns mid-frame cannot unbalance the stack - and not counted as dropped, because nothing was lost.
			if ( !ZonesWanted )
			{
				frame.DroppedOpen++;
				return 0;
			}

			//Out of slots or nested too deep: counted, and the matching end knows to do nothing rather than closing somebody else's zone.
			if ( frame.ZoneCount >= VkDevice.ProfileMaxZones || frame.ZoneDepth >= frame.ZoneOpen.Length )
			{
				frame.ZonesDropped++;
				frame.DroppedOpen++;
				return 0;
			}

			ref var zone = ref frame.Zones[frame.ZoneCount];

			fixed ( byte* into = zone.Name ) VkDevice.WriteUtf8( name, into, VkDevice.ProfileNameSize );

			zone.Depth = frame.ZoneDepth;
			zone.CpuBegin = VkSwapchain.NowNs();
			zone.CpuEnd = zone.CpuBegin;
			zone.Begin = frame.QueryNext;
			zone.End = frame.QueryNext + 1;
			frame.QueryNext += 2;

			if ( frame.Queries.Handle != 0 )
			{
				VkDevice.dialect.Timestamp( frame.Commands, PipelineStageFlags2.AllCommandsBit,
					frame.Queries, zone.Begin );
			}

			//The one zone somebody asked to have counted rather than timed - the first time it comes round in this frame. See Frame.Stats.
			if ( frame.Stats.Handle != 0 && !frame.StatsTaken && VkDevice.StatsZone == name )
			{
				VkDevice.vk.CmdBeginQuery( frame.Commands, frame.Stats, 0, 0 );
				frame.StatsTaken = true;
				frame.StatsRunning = true;
				frame.StatsDepth = frame.ZoneDepth;
			}

			fixed ( byte* into = zone.Name ) PushLabel( frame, into );

			frame.ZoneOpen[frame.ZoneDepth++] = (int)frame.ZoneCount;
			frame.ZoneCount++;

			return 1;
		}
	}

	internal static void ZoneEnd()
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording ) return;

			var frame = CurrentFrame;

			//The end of a begin that was refused ends nothing.
			if ( frame.DroppedOpen > 0 )
			{
				frame.DroppedOpen--;
				return;
			}

			if ( frame.ZoneDepth <= 0 ) return;

			ref var zone = ref frame.Zones[frame.ZoneOpen[--frame.ZoneDepth]];

			zone.CpuEnd = VkSwapchain.NowNs();

			if ( frame.Queries.Handle != 0 )
			{
				VkDevice.dialect.Timestamp( frame.Commands, PipelineStageFlags2.AllCommandsBit,
					frame.Queries, zone.End );
			}

			//And the counted zone closes where it opened - by depth, so that a zone of the same name opened inside it cannot close it early.
			if ( frame.StatsRunning && frame.StatsDepth == frame.ZoneDepth )
			{
				VkDevice.vk.CmdEndQuery( frame.Commands, frame.Stats, 0 );
				frame.StatsRunning = false;
			}

			PopLabel( frame );
		}
	}

	internal static int ProfileStats( ulong viewport, float* into, int count )
	{
		lock ( VkDevice.renderLock )
		{
			if ( into is null || count <= 0 ) return 0;

			var profile = VkSwapchain.ProfileFor( viewport );
			if ( profile is null || !profile.Valid ) return 0;

			var values = stackalloc float[18];
			values[0] = profile.GpuMs;
			values[1] = profile.CpuMs;
			values[2] = profile.Draws;
			values[3] = profile.Triangles;
			values[4] = profile.DynamicDraws;
			values[5] = profile.SkinnedDraws;
			values[6] = profile.ScratchUsed;
			values[7] = profile.SkippedBinds;
			values[8] = profile.ZoneCount;
			values[9] = profile.ZonesDropped;
			values[10] = VkDevice.trackedTextureBytes;
			values[11] = VkDevice.trackedBufferBytes;
			values[12] = VkDevice.trackedTextureCount;
			values[13] = VkDevice.trackedBufferCount;
			values[14] = profile.Instances;
			values[15] = profile.Dispatches;
			values[16] = profile.Indirect;
			values[17] = profile.Counted;

			var give = count < 18 ? count : 18;

			for ( var i = 0; i < give; ++i ) into[i] = values[i];

			return give;
		}
	}

	internal static int ProfileZone( ulong viewport, int index, byte* name, int nameCapacity, float* into, int count )
	{
		lock ( VkDevice.renderLock )
		{
			var profile = VkSwapchain.ProfileFor( viewport );
			if ( profile is null || !profile.Valid ) return 0;
			if ( index < 0 || (uint)index >= profile.ZoneCount ) return 0;

			ref var zone = ref profile.Zones[index];

			if ( name is not null && nameCapacity > 0 )
			{
				var take = nameCapacity < VkDevice.ProfileNameSize ? nameCapacity : VkDevice.ProfileNameSize;
				var wrote = 0;

				fixed ( byte* from = zone.Name )
				{
					while ( wrote < take - 1 && from[wrote] != 0 )
					{
						name[wrote] = from[wrote];
						wrote++;
					}
				}

				name[wrote] = 0;
			}

			if ( into is not null )
			{
				var values = stackalloc float[3];
				values[0] = zone.GpuMs;
				values[1] = zone.CpuMs;
				values[2] = zone.Depth;

				var give = count < 3 ? ( count < 0 ? 0 : count ) : 3;

				for ( var i = 0; i < give; ++i ) into[i] = values[i];
			}

			return 1;
		}
	}
}
