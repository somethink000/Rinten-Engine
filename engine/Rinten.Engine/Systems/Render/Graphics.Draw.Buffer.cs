using NativeEngine;
using System.Runtime.InteropServices;

namespace Rinten;

partial class Graphics
{
	internal static void ValidateMaterial( Material material )
	{
		if ( material?.core is null || string.IsNullOrWhiteSpace( material.core.Shader ) )
			throw new ArgumentException( $"{nameof( material )} is invalid.", nameof( material ) );
	}

	internal static void ValidateVertexBuffer( GpuBuffer vertexBuffer )
	{
		if ( !vertexBuffer.IsValid() )
			throw new ArgumentException( $"{nameof( vertexBuffer )} is invalid.", nameof( vertexBuffer ) );

		if ( !vertexBuffer.Usage.Contains( GpuBuffer.UsageFlags.Vertex ) )
			throw new ArgumentException( $"{nameof( vertexBuffer )} does not have the required usage flag '{GpuBuffer.UsageFlags.Vertex}'.", nameof( vertexBuffer ) );
	}

	internal static void ValidateIndexBuffer( GpuBuffer indexBuffer )
	{
		if ( !indexBuffer.IsValid() )
			throw new ArgumentException( $"{nameof( indexBuffer )} is invalid.", nameof( indexBuffer ) );

		if ( !indexBuffer.Usage.Contains( GpuBuffer.UsageFlags.Index ) )
			throw new ArgumentException( $"{nameof( indexBuffer )} does not have the required usage flag '{GpuBuffer.UsageFlags.Index}'.", nameof( indexBuffer ) );

		if ( indexBuffer.ElementSize != sizeof( uint ) && indexBuffer.ElementSize != sizeof( ushort ) )
			throw new ArgumentOutOfRangeException( nameof( indexBuffer ), $"{nameof( indexBuffer )} must be size of {sizeof( uint )} or {sizeof( ushort )}." );
	}

	internal static void ValidateIndirectBuffer( GpuBuffer indirectBuffer )
	{
		if ( !indirectBuffer.IsValid() )
			throw new ArgumentException( $"{nameof( indirectBuffer )} is invalid.", nameof( indirectBuffer ) );

		if ( !indirectBuffer.Usage.Contains( GpuBuffer.UsageFlags.IndirectDrawArguments ) )
			throw new ArgumentException( $"Buffer must have the required usage flag '{GpuBuffer.UsageFlags.IndirectDrawArguments}'", nameof( indirectBuffer ) );
	}

	//Validates an indirect draw stride and converts an element offset into a byte offset.
	//A stride of 0 means the draw arguments are tightly packed at their natural size.
	internal static uint GetIndirectBufferOffset( uint elementOffset, uint stride, int naturalSize )
	{
		if ( stride != 0 )
		{
			if ( stride % 4 != 0 )
				throw new ArgumentException( $"Indirect draw stride ({stride}) must be a multiple of 4.", nameof( stride ) );

			if ( stride < (uint)naturalSize )
				throw new ArgumentException( $"Indirect draw stride ({stride}) must be at least the size of the draw argument struct ({naturalSize}).", nameof( stride ) );
		}

		return elementOffset * (stride != 0 ? stride : (uint)naturalSize);
	}

	//Whether a vertex type is the engine's own - the one layout a pipeline here binds as vertices. Any other is
	//read by its shader out of set 2, by SV_VertexID: there is no layout on this side that knows its fields.
	internal static bool BindsAsVertices<T>() where T : unmanaged => typeof( T ) == typeof( Vertex );

	//The draw's shape, checked: a vertex buffer, its range, and whether it is bound or read.
	private static CoreBufferDraw Shape<T>( GpuBuffer<T> vertexBuffer, int first, int count, int total ) where T : unmanaged
	{
		if ( first < 0 || count < 0 || (long)first + count > total )
			throw new ArgumentOutOfRangeException( nameof( count ), $"{count} from {first} runs past the end of a buffer of {total}" );

		return new CoreBufferDraw
		{
			Vertices = BindsAsVertices<T>() ? vertexBuffer.Handle : 0,
			First = first,
			Count = count,
		};
	}

	/// <summary>Draws geometry using a vertex buffer and material.</summary>
	public static void Draw<T>( GpuBuffer<T> vertexBuffer, Material material, int startVertex = 0, int vertexCount = 0, RenderAttributes attributes = null, PrimitiveType primitiveType = PrimitiveType.Triangles ) where T : unmanaged
	{
		AssertRenderBlock();
		ValidateVertexBuffer( vertexBuffer );
		ValidateMaterial( material );

		DrawVerticesOutOf( vertexBuffer, material, startVertex, vertexCount, attributes, primitiveType, null );
	}

	//A draw of vertices in order out of a project's buffer, where it belongs - see DrawOutOf.
	internal static void DrawVerticesOutOf<T>( GpuBuffer<T> vertexBuffer, Material material, int startVertex, int vertexCount,
		RenderAttributes attributes, PrimitiveType primitiveType, Rendering.CommandList list ) where T : unmanaged
	{
		if ( vertexCount == 0 ) vertexCount = vertexBuffer.ElementCount - startVertex;

		DrawOutOf( Shape( vertexBuffer, startVertex, vertexCount, vertexBuffer.ElementCount ),
			BindsAsVertices<T>() ? null : vertexBuffer, material, attributes, primitiveType, list );
	}

	/// <summary>Draws indexed geometry using vertex and index buffers.</summary>
	public static void Draw<T>( GpuBuffer<T> vertexBuffer, GpuBuffer indexBuffer, Material material, int startIndex = 0, int indexCount = 0, RenderAttributes attributes = null, PrimitiveType primitiveType = PrimitiveType.Triangles ) where T : unmanaged
	{
		AssertRenderBlock();
		ValidateVertexBuffer( vertexBuffer );
		ValidateIndexBuffer( indexBuffer );
		ValidateMaterial( material );

		DrawIndexedOutOf( vertexBuffer, indexBuffer, material, startIndex, indexCount, attributes, primitiveType, null );
	}

	//An indexed draw out of a project's buffers, where it belongs - see DrawOutOf.
	internal static void DrawIndexedOutOf<T>( GpuBuffer<T> vertexBuffer, GpuBuffer indexBuffer, Material material, int startIndex, int indexCount,
		RenderAttributes attributes, PrimitiveType primitiveType, Rendering.CommandList list ) where T : unmanaged
	{
		if ( indexCount == 0 ) indexCount = indexBuffer.ElementCount - startIndex;

		if ( startIndex < 0 || indexCount < 0 || (long)startIndex + indexCount > indexBuffer.ElementCount )
			throw new ArgumentOutOfRangeException( nameof( indexCount ), $"{indexCount} indices from {startIndex} run past the end of a buffer of {indexBuffer.ElementCount}" );

		var shape = new CoreBufferDraw
		{
			Vertices = BindsAsVertices<T>() ? vertexBuffer.Handle : 0,
			Indices = indexBuffer.Handle,
			WideIndices = indexBuffer.ElementSize == sizeof( uint ),
			First = startIndex,
			Count = indexCount,
		};

		DrawOutOf( shape, BindsAsVertices<T>() ? null : vertexBuffer, material, attributes, primitiveType, list );
	}

	internal static void DrawInstancedIndirect<T>( GpuBuffer<T> vertexBuffer, Material material, GpuBuffer indirectBuffer, uint indirectElementOffset = 0, RenderAttributes attributes = null, PrimitiveType primitiveType = PrimitiveType.Triangles, uint drawCount = 1, uint stride = 0 ) where T : unmanaged
	{
		AssertRenderBlock();

		DrawInstancedIndirectOutOf( vertexBuffer, material, indirectBuffer, indirectElementOffset, attributes, primitiveType, drawCount, stride, null,
			BindsAsVertices<T>() );
	}

	//A draw whose counts a buffer holds, where it belongs - see DrawOutOf.
	internal static void DrawInstancedIndirectOutOf( GpuBuffer vertexBuffer, Material material, GpuBuffer indirectBuffer, uint indirectElementOffset,
		RenderAttributes attributes, PrimitiveType primitiveType, uint drawCount, uint stride, Rendering.CommandList list, bool bindsVertices = false )
	{
		if ( vertexBuffer is not null ) ValidateVertexBuffer( vertexBuffer );
		ValidateIndirectBuffer( indirectBuffer );
		ValidateMaterial( material );

		if ( drawCount == 0 ) return;

		var shape = new CoreBufferDraw
		{
			Vertices = bindsVertices ? vertexBuffer.Handle : 0,
			Arguments = indirectBuffer.Handle,
			ArgumentsOffset = GetIndirectBufferOffset( indirectElementOffset, stride, Marshal.SizeOf<GpuBuffer.IndirectDrawArguments>() ),
			DrawCount = (int)drawCount,
			Stride = (int)stride,
		};

		DrawOutOf( shape, bindsVertices ? null : vertexBuffer, material, attributes, primitiveType, list );
	}

	internal static void DrawInstancedIndirect( Material material, GpuBuffer indirectBuffer, uint indirectElementOffset = 0, RenderAttributes attributes = null, PrimitiveType primitiveType = PrimitiveType.Triangles, uint drawCount = 1, uint stride = 0 )
	{
		AssertRenderBlock();

		DrawInstancedIndirectOutOf( null, material, indirectBuffer, indirectElementOffset, attributes, primitiveType, drawCount, stride, null );
	}

	//One draw from project buffers into the open list pass or the object's overlay; lit like the world
	//pulledFrom is read by the shader at set 2, with attribute buffers after it in name order
	internal static void DrawOutOf( CoreBufferDraw shape, GpuBuffer pulledFrom, Material material, RenderAttributes attributes,
		PrimitiveType primitiveType, Rendering.CommandList list )
	{
		var lines = primitiveType is PrimitiveType.Lines;

		if ( !lines && primitiveType is not PrimitiveType.Triangles )
		{
			Warn( $"{primitiveType} out of a buffer is not a shape this draws - a list of triangles or of lines is" );
			return;
		}

		var stage = CorePostBlit.Ready;
		var walk = !stage && IsActive && CoreImmediate.Capturing;

		if ( !stage && !walk )
		{
			Warn( "a draw out of buffers was asked for outside a camera's stages and outside a scene object's drawing - there is nothing to draw it into" );
			return;
		}

		//Engine 2D-shape materials have no shader to run over buffers; warn once and skip
		if ( material.Draws != Material.EngineDraw.None )
		{
			Warn( $"{material.Name} stands for one of the engine's own shapes and cannot be drawn out of buffers - draw it as a quad, or put it on a shader" );
			return;
		}

		var core = material.core;
		var texture = CoreResources.Live( material );

		if ( texture == 0 ) return;

		var flags = CoreResources.LiveFlags( material );

		var named = attributes ?? list?.Values ?? Attributes;

		//What the shader reads for itself at set 2: the vertices it pulls, then the attributes' buffers.
		Span<ulong> buffers = stackalloc ulong[8];
		var count = 0;

		if ( pulledFrom is not null )
		{
			var pulled = pulledFrom.Handle;
			if ( pulled == 0 ) return;

			buffers[count++] = pulled;
		}

		count += GpuBuffer.Bindings( named, buffers[count..] );

		if ( shape.Vertices == 0 && count == 0 )
		{
			Warn( $"{core.Shader} was drawn with no vertices bound and no buffer to read them out of" );
			return;
		}

		var kind = new CoreSceneRenderer.PipelineKind( false, flags.Translucent, flags.TwoSided )
		{
			Shader = core.Shader,
			Combo = CoreResources.LiveCombo( material ),
			Pulled = shape.Vertices == 0,
			Lines = lines,
		};

		//Under the names the shader declared: the draw's own set first, then what the list it was recorded in set, where it set anything.
		Span<float> said = stackalloc float[8];
		said.Clear();
		CoreSceneDrawer.DrawSaid( said, core.Shader, named, attributes is not null ? list?.Values : null );

		shape.Texture = texture;

		if ( stage )
		{
			//A draw lands on the frame, so the point effects held for it go on first.
			CorePostBlit.Flush();

			//Into the picture the list named or the one the frame is in, over the world's depth where there is one to test against.
			if ( !CorePasses.OpenForDraw( depth: true, orNone: true, out var target, out var format, out var withDepth, out var finishes ) ) return;

			var baked = CoreResources.Pipeline( kind with
			{
				Target = target,
				PictureFormat = format,
				NoDepth = !withDepth,
				Finishes = finishes,
			} );

			if ( baked == 0 ) return;

			Span<byte> push = stackalloc byte[CoreSceneRenderer.PushSize];
			CoreSceneRenderer.ScenePushInto( push, System.Numerics.Matrix4x4.Identity, Color.White, default, said );

			shape.Pipeline = baked;
			CoreRenderDevice.DrawBuffers( shape, push, buffers[..count] );

			return;
		}

		//Collected, and drawn once the picture it belongs in is open - see CoreImmediate.BufferBatch.
		var kept = CoreImmediate.TakePush();
		CoreSceneRenderer.ScenePushInto( kept, System.Numerics.Matrix4x4.Identity, Color.White, default, said );

		ulong[] lent = null;

		if ( count > 0 )
		{
			lent = CoreImmediate.TakeBuffers();
			buffers[..count].CopyTo( lent );
		}

		CoreImmediate.Add( new CoreImmediate.BufferBatch( kind with { NoDepth = IgnoreDepth }, shape, lent, count, kept, IgnoreDepth ) );
	}

	//Instanced draw with no vertex input; the shader pulls per-instance data from set 2 (sprites)
	internal static void DrawIndexedInstanced( GpuBuffer indexBuffer, Material material, int instanceCount, RenderAttributes attributes = null, PrimitiveType primitiveType = PrimitiveType.Triangles )
	{
		AssertRenderBlock();

		if ( material is null || instanceCount <= 0 ) return;
		if ( !indexBuffer.IsValid() ) return;

		if ( primitiveType is not PrimitiveType.Triangles )
		{
			Warn( $"{primitiveType} is not a shape this draws" );
			return;
		}

		var kind = InstancedKindOf( material, attributes );
		var pipeline = InstancedPipeline( kind, null );

		if ( pipeline == 0 ) return;

		//Up to eight, in the order their names sort - the same rule a compute dispatch binds by, and for the same reason: a
		//dictionary has no order, and a pass whose bindings move between frames cannot be written against. See ComputeShader.Buffers.
		Span<ulong> buffers = stackalloc ulong[8];

		var count = GpuBuffer.Bindings( attributes, buffers );

		if ( count == 0 )
		{
			Warn( $"{material.ShaderName} was drawn with no buffer to read instances out of" );
			return;
		}

		var numbers = InstancedPush( material, attributes );

		//Queued, not recorded: no pass is open during the gather; the drawer replays it later
		CoreImmediate.Add( new CoreImmediate.InstancedBatch(
			pipeline, indexBuffer.Handle, indexBuffer.ElementCount, instanceCount,
			indexBuffer.ElementSize == sizeof( uint ),
			buffers[..count].ToArray(), count, numbers, kind ) );
	}

	//The portable frame's form of DrawIndexedInstanced: the instances handed over as they are rather than in a buffer, one run
	//sharing one picture - see CoreRenderDevice.DrawBlockInstanced, which says why, and InstancesPerRun, which says how many fit.
	internal static void DrawIndexedInstancedRun( GpuBuffer indexBuffer, Material material, int instanceCount,
		ReadOnlySpan<byte> instances, ulong texture, RenderAttributes attributes = null )
	{
		AssertRenderBlock();

		if ( material is null || instanceCount <= 0 || instances.IsEmpty ) return;
		if ( instances.Length > CoreRenderDevice.BlockBytes ) return;
		if ( !indexBuffer.IsValid() ) return;

		var kind = InstancedKindOf( material, attributes );
		var pipeline = InstancedPipeline( kind, null );

		if ( pipeline == 0 ) return;

		//Nothing is listening, so nothing would be given back either - asked before the two arrays are taken rather than after.
		if ( !CoreImmediate.Capturing ) return;

		//Into a slot lent for this view and taken back at the next, not a new array a run: this
		//is called once for every picture a batch of sprites wears, in every view, every frame.
		var block = CoreImmediate.TakeBlock();
		instances.CopyTo( block );

		CoreImmediate.Add( new CoreImmediate.BlockBatch(
			pipeline, indexBuffer.Handle, indexBuffer.ElementCount, instanceCount,
			indexBuffer.ElementSize == sizeof( uint ), texture,
			block, instances.Length, InstancedPush( material, attributes ), kind ) );
	}

	//How many instances of this many bytes one run carries.
	internal static int InstancesPerRun( int instanceBytes ) => CoreRenderDevice.BlockBytes / instanceBytes;

	//An instanced draw's own declared numbers in the push block, the way a compute pass gets them - this
	//draw has a material but the shader reads what a draw set for itself, not what a material holds.
	private static float[] InstancedPush( Material material, RenderAttributes attributes )
	{
		var numbers = CoreImmediate.TakeNumbers();

		System.Array.Clear( numbers );

		//The attributes' own lookup and not one written here: a lambda over them is a closure allocated at every call, and this is a call for
		//every run of every batch - see RenderAttributes.DeclaredLookup, which exists for exactly this and holds one object for their lifetime.
		CoreShaderParams.Into( numbers, CoreResources.Declared( material.ShaderName ),
			attributes?.DeclaredLookup ?? NoNumbers );

		return numbers;
	}

	//The lookup of a draw that was given no attributes: nothing is set.
	private static readonly System.Func<string, Vector4?> NoNumbers = _ => null;

	//What an instanced draw of this material is drawn as: its shader, and how it is laid over what is behind it.
	private static CoreImmediate.InstancedKind InstancedKindOf( Material material, RenderAttributes attributes )
	{
		var additive = attributes is not null && attributes.TryGetCombo( "D_BLEND", out var blend ) && blend != 0;
		var opaque = attributes is not null && attributes.TryGetCombo( "D_OPAQUE", out var solid ) && solid != 0;
		var nearerOnly = attributes is not null && attributes.TryGetCombo( "D_NEARER_ONLY", out var nearer ) && nearer != 0;

		return new CoreImmediate.InstancedKind( material.ShaderName, additive, opaque, nearerOnly );
	}

	//Cached instanced pipeline per target and blend mode, since blending is pipeline state here
	internal static ulong InstancedPipeline( CoreImmediate.InstancedKind kind, CoreRender.TargetKind? world )
	{
		var (shader, additive, opaque, nearerOnly) = kind;

		if ( string.IsNullOrWhiteSpace( shader ) ) return 0;

		var key = ( kind, world );

		if ( instancedPipelines.TryGetValue( key, out var kept ) ) return kept;

		var stages = CoreShaderFile.Stages( shader );

		//In the world's picture the stage that writes both of its pictures; onto the screen the one that finishes the colour itself, because nothing
		//comes after it - see CoreShaderFile.Stage.ScreenPixel. Over the finished picture the plain one, the colour as it is, the way the overlays draw.
		var pixelStage = world switch
		{
			CoreRender.TargetKind.Scene => CoreShaderFile.Stage.ScenePixel,
			CoreRender.TargetKind.Screen => CoreShaderFile.Stage.ScreenPixel,
			_ => CoreShaderFile.Stage.Pixel,
		};

		var vertex = stages?[(int)CoreShaderFile.Stage.Vertex];
		var pixel = stages?[(int)pixelStage];

		if ( vertex is null || pixel is null )
		{
			Warn( $"{shader} has no vertex and pixel stage to draw instances with" );

			//Remembered as nothing, so a shader that is not there is looked for once rather than once a frame per sprite batch.
			instancedPipelines[key] = 0;
			return 0;
		}

		var made = CoreRenderDevice.CreatePipeline( vertex, pixel,
			CoreRender.VertexLayoutKind.None,
			depthTest: true,
			compare: nearerOnly ? CoreRenderDevice.DepthCompare.Nearer : CoreRenderDevice.DepthCompare.NearerOrSame,
			blend: !opaque,
			//Translucent sprites read the depth they are drawn against and do not add to it; an opaque one is an ordinary surface.
			depthWrite: opaque,
			additive: additive,
			target: world ?? CoreRender.TargetKind.Screen,
			//A see-through sprite leaves the world's normals as they were: the surface behind it is still what is there.
			colourAlone: world == CoreRender.TargetKind.Scene && !opaque );

		instancedPipelines[key] = made;

		return made;
	}

	private static readonly Dictionary<(CoreImmediate.InstancedKind Kind, CoreRender.TargetKind? World), ulong> instancedPipelines = new();

	internal static void DrawIndexedInstancedIndirect<T>( GpuBuffer<T> vertexBuffer, GpuBuffer indexBuffer, Material material, GpuBuffer indirectBuffer, uint indirectElementOffset = 0, RenderAttributes attributes = null, PrimitiveType primitiveType = PrimitiveType.Triangles, uint drawCount = 1, uint stride = 0 ) where T : unmanaged
	{
		AssertRenderBlock();
		ValidateVertexBuffer( vertexBuffer );

		DrawIndexedInstancedIndirectOutOf( vertexBuffer, BindsAsVertices<T>(), indexBuffer, material, indirectBuffer, indirectElementOffset, attributes, primitiveType, drawCount, stride, null );
	}

	internal static void DrawIndexedInstancedIndirect( GpuBuffer indexBuffer, Material material, GpuBuffer indirectBuffer, uint indirectElementOffset = 0, RenderAttributes attributes = null, PrimitiveType primitiveType = PrimitiveType.Triangles, uint drawCount = 1, uint stride = 0 )
	{
		AssertRenderBlock();

		DrawIndexedInstancedIndirectOutOf( null, false, indexBuffer, material, indirectBuffer, indirectElementOffset, attributes, primitiveType, drawCount, stride, null );
	}

	//An indexed draw whose counts a buffer holds, where it belongs - see DrawOutOf.
	internal static void DrawIndexedInstancedIndirectOutOf( GpuBuffer vertexBuffer, bool bindsVertices, GpuBuffer indexBuffer, Material material, GpuBuffer indirectBuffer,
		uint indirectElementOffset, RenderAttributes attributes, PrimitiveType primitiveType, uint drawCount, uint stride, Rendering.CommandList list )
	{
		ValidateIndexBuffer( indexBuffer );
		ValidateIndirectBuffer( indirectBuffer );
		ValidateMaterial( material );

		if ( drawCount == 0 ) return;

		var shape = new CoreBufferDraw
		{
			Vertices = bindsVertices ? vertexBuffer.Handle : 0,
			Indices = indexBuffer.Handle,
			WideIndices = indexBuffer.ElementSize == sizeof( uint ),
			Arguments = indirectBuffer.Handle,
			ArgumentsOffset = GetIndirectBufferOffset( indirectElementOffset, stride, Marshal.SizeOf<GpuBuffer.IndirectDrawIndexedArguments>() ),
			DrawCount = (int)drawCount,
			Stride = (int)stride,
		};

		DrawOutOf( shape, bindsVertices ? null : vertexBuffer, material, attributes, primitiveType, list );
	}

	//One shape many times, where it belongs - into a command list's pass at a stage through the general path,
	//see DrawOutOf. The index buffer names the corners and the shader reads everything else out of set 2.
	internal static void DrawIndexedInstancedOutOf( GpuBuffer indexBuffer, Material material, int instanceCount,
		RenderAttributes attributes, PrimitiveType primitiveType, Rendering.CommandList list )
	{
		ValidateIndexBuffer( indexBuffer );
		ValidateMaterial( material );

		if ( instanceCount <= 0 ) return;

		var shape = new CoreBufferDraw
		{
			Indices = indexBuffer.Handle,
			WideIndices = indexBuffer.ElementSize == sizeof( uint ),
			Count = indexBuffer.ElementCount,
			Instances = instanceCount,
		};

		DrawOutOf( shape, null, material, attributes, primitiveType, list );
	}
}
