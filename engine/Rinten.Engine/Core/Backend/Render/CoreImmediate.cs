using System.Collections.Generic;

namespace Rinten;

//Immediate draws collected during a view and drawn in their pass; leftovers dropped at next Begin
internal static class CoreImmediate
{
	//One immediate draw; an attribute picture overrides the material map, screen batches use pixels
	internal readonly record struct Batch(
		Vertex[] Vertices,
		int Count,
		bool Lines,
		Transform Transform,
		Color Tint,
		string Material,
		bool IgnoreDepth,
		Texture Picture = null,
		bool Screen = false );

	//A model drawn for one frame and lit with the world; instance count may come from a GPU buffer
	internal readonly record struct ModelBatch( Model Model, Transform Transform, Color Tint,
		Material Override = null, int Instances = 0, GpuBuffer CountFrom = null, long CountAt = 0,
		RenderAttributes Attributes = null, int Level = -1, System.Numerics.Matrix4x4[] Pose = null );

	//A draw out of a project's own buffers with a material of its own - see Graphics.Draw( GpuBuffer ) - replayed onto the finished picture with
	//the immediate draws, lit as the world is. The pipeline in Draw is baked at replay, when the target is known; Kind says everything else.
	internal readonly record struct BufferBatch(
		CoreSceneRenderer.PipelineKind Kind,
		CoreBufferDraw Draw,
		ulong[] Buffers,
		int BufferCount,
		byte[] Push,
		bool IgnoreDepth );

	//A copy of the frame asked for by a scene object while it drew itself, before there was a frame to copy - taken
	//when the immediate draws are replayed, and pointed at by Into 's colour texture. See Graphics.GrabFrameTexture.
	internal readonly record struct Grab( RenderTarget Into );

	//One shape drawn many times from instance buffers; collected, since recording early would be lost
	internal readonly record struct InstancedBatch(
		ulong Pipeline,
		ulong IndexBuffer,
		int IndexCount,
		int InstanceCount,
		bool WideIndices,
		ulong[] Buffers,
		int BufferCount,
		float[] Push,
		InstancedKind Kind );

	//What an instanced draw's pipeline is made of, kept so that it can be baked again for the picture it is replayed into: Pipeline
	//is the one for the finished picture, and the world's own pass wants its own - see CoreSceneDrawer.DrawInstancedInWorld.
	internal readonly record struct InstancedKind( string Shader, bool Additive, bool Opaque, bool NearerOnly );

	//The portable frame's form of an instanced draw: the instances ride in a slot of the frame's bone ring rather than in
	//a buffer of their own, a run at a time, with the one picture the run shares - see CoreRenderDevice.DrawBlockInstanced.
	internal readonly record struct BlockBatch(
		ulong Pipeline,
		ulong IndexBuffer,
		int IndexCount,
		int InstanceCount,
		bool WideIndices,
		ulong Texture,
		byte[] Block,
		int BlockBytes,
		float[] Push,
		InstancedKind Kind );

	private static readonly List<Batch> batches = new();
	private static readonly List<ModelBatch> models = new();
	private static readonly List<InstancedBatch> instanced = new();
	private static readonly List<BlockBatch> blocks = new();
	private static readonly List<BufferBatch> buffered = new();
	private static readonly List<Grab> grabs = new();

	//Push blocks and buffer lists lent to the buffer draws of a view, and taken back at the next - the same reason the vertex arrays are, see spare.
	private static readonly Stack<byte[]> sparePush = new();
	private static readonly Stack<ulong[]> spareBuffers = new();

	//Pools for instance blocks and draw numbers, reused across runs to avoid per-frame allocations
	private static readonly Stack<byte[]> spareBlocks = new();
	private static readonly Stack<float[]> spareNumbers = new();

	//One push block, for this view.
	internal static byte[] TakePush()
	{
		var push = sparePush.Count > 0 ? sparePush.Pop() : new byte[CoreSceneRenderer.PushSize];

		System.Array.Clear( push );

		return push;
	}

	//A list of up to eight buffers, for this view.
	internal static ulong[] TakeBuffers() => spareBuffers.Count > 0 ? spareBuffers.Pop() : new ulong[8];

	//One slot's worth of instances, for this view. Always a whole slot, so what fills part of
	//it may still be handed to the ring as it stands - see CoreRenderDevice.DrawBlockInstanced.
	internal static byte[] TakeBlock()
		=> spareBlocks.Count > 0 ? spareBlocks.Pop() : new byte[CoreRenderDevice.BlockBytes];

	//The numbers one run's draw declares, for this view.
	internal static float[] TakeNumbers()
		=> spareNumbers.Count > 0 ? spareNumbers.Pop() : new float[CoreShaderParams.PushSlots * 4];

	/// <summary>Whether anything is listening. False everywhere except inside the part of a frame that runs the overrides, so a stray draw from a background thread or a tool window is dropped rather than kept for ever.</summary>
	public static bool Capturing { get; private set; }

	/// <summary>Starts collecting for a view, throwing away anything the last one left.</summary>
	public static void Begin()
	{
		//What the last view was handed goes back before the list of it does. Nothing reads a batch after the
		//view that asked for it has drawn, and the next Begin is the first moment that is certainly true.
		for ( var i = 0; i < batches.Count; i++ ) Give( batches[i].Vertices );

		for ( var i = 0; i < buffered.Count; i++ )
		{
			if ( sparePush.Count < 1024 ) sparePush.Push( buffered[i].Push );
			if ( buffered[i].Buffers is { } lent && spareBuffers.Count < 1024 ) spareBuffers.Push( lent );
		}

		for ( var i = 0; i < blocks.Count; i++ )
		{
			if ( blocks[i].Block is { } slot && spareBlocks.Count < 1024 ) spareBlocks.Push( slot );
			if ( blocks[i].Push is { } numbers && spareNumbers.Count < 1024 ) spareNumbers.Push( numbers );
		}

		for ( var i = 0; i < lentAttributes.Count; i++ ) RenderAttributes.Pool.Return( lentAttributes[i] );

		lentAttributes.Clear();
		batches.Clear();
		models.Clear();
		instanced.Clear();
		blocks.Clear();
		buffered.Clear();
		grabs.Clear();
		Capturing = true;
	}

	//Vertex arrays lent for a view by doubling size class from 16; the largest may still be too small
	private static readonly Stack<Vertex[]>[] spare = MakeSpare();

	private static Stack<Vertex[]>[] MakeSpare()
	{
		var made = new Stack<Vertex[]>[12];

		for ( var i = 0; i < made.Length; i++ ) made[i] = new Stack<Vertex[]>();

		return made;
	}

	private static int ClassOf( int count )
	{
		var size = 16;
		var at = 0;

		while ( size < count && at < spare.Length - 1 )
		{
			size *= 2;
			at++;
		}

		return at;
	}

	//One array of at least that many, borrowed for this view.
	internal static Vertex[] Take( int count )
	{
		if ( count <= 0 ) return System.Array.Empty<Vertex>();

		var at = ClassOf( count );
		var stack = spare[at];

		if ( stack.Count > 0 )
		{
			var found = stack.Pop();

			if ( found.Length >= count ) return found;
		}

		return new Vertex[Math.Max( count, 16 << at )];
	}

	//And back, when nothing is reading it any more.
	internal static void Give( Vertex[] array )
	{
		if ( array is null || array.Length == 0 ) return;

		var at = ClassOf( array.Length );
		var stack = spare[at];

		//Cap per class so one huge frame does not hold arrays forever, but above a normal frame's use
		if ( stack.Count < 8192 ) stack.Push( array );
	}

	/// <summary>Stops collecting. What came in stays until the next view.</summary>
	public static void End() => Capturing = false;

	/// <summary>The vertices handed over, in the order they were.</summary>
	public static IReadOnlyList<Batch> Batches => batches;

	/// <summary>The models asked for, in the order they were.</summary>
	public static IReadOnlyList<ModelBatch> Models => models;

	//The same, as a span: walked straight, with no enumerator boxed behind an interface - which the renderer did every frame, and which the
	//web player's AOT code called through a broken pointer (a struct element behind IEnumerator<T>), dying on the first frame of every page.
	internal static ReadOnlySpan<ModelBatch> ModelSpan => System.Runtime.InteropServices.CollectionsMarshal.AsSpan( models );

	//The vertices handed over, as a span - see ModelSpan.
	internal static ReadOnlySpan<Batch> BatchSpan => System.Runtime.InteropServices.CollectionsMarshal.AsSpan( batches );

	//The instanced draws asked for, in the order they were.
	internal static ReadOnlySpan<InstancedBatch> Instanced => System.Runtime.InteropServices.CollectionsMarshal.AsSpan( instanced );

	//Adds an instanced draw, if anything is listening.
	internal static void Add( in InstancedBatch batch )
	{
		if ( !Capturing ) return;
		if ( batch.Pipeline == 0 || batch.InstanceCount <= 0 || batch.BufferCount <= 0 ) return;

		if ( instanced.Count > 1024 ) return;

		instanced.Add( batch );
	}

	//The portable frame's instanced draws, in the order they were.
	internal static ReadOnlySpan<BlockBatch> Blocks => System.Runtime.InteropServices.CollectionsMarshal.AsSpan( blocks );

	//Adds a run of instances, if anything is listening.
	internal static void Add( in BlockBatch batch )
	{
		if ( !Capturing ) return;
		if ( batch.Pipeline == 0 || batch.InstanceCount <= 0 || batch.Block is not { Length: > 0 } ) return;

		if ( blocks.Count > 1024 ) return;

		blocks.Add( batch );
	}

	/// <summary>Adds a drawing, if anything is listening. The vertices are kept as they are, so the caller must not write over them afterwards - which is what every caller of the immediate API does anyway.</summary>
	public static void Add( in Batch batch )
	{
		if ( !Capturing ) return;
		if ( batch.Vertices is not { Length: > 0 } || batch.Count <= 0 ) return;

		//A frame that draws this much has gone wrong, and the frame after it would go wrong for a different reason if this grew without end.
		if ( batches.Count > 8192 ) return;

		batches.Add( batch );
	}

	/// <summary>Adds a model, if anything is listening.</summary>
	public static void Add( in ModelBatch batch )
	{
		if ( !Capturing ) return;
		if ( batch.Model is null ) return;
		if ( models.Count > 8192 ) return;

		//What the draw set for itself is read when the frame gathers, by which time the set it came in - the view's,
		//as often as not - has been cleared for the next object. So it is copied, into one lent for the view.
		if ( batch.Attributes is { Count: > 0 } said )
		{
			var kept = RenderAttributes.Pool.Get();
			said.MergeTo( kept );
			lentAttributes.Add( kept );

			models.Add( batch with { Attributes = kept } );
			return;
		}

		models.Add( batch with { Attributes = null } );
	}

	//The copies of the models' attributes this view was lent - see Add( ModelBatch ).
	private static readonly List<RenderAttributes> lentAttributes = new();

	//The draws out of a project's buffers, in the order they were asked.
	internal static IReadOnlyList<BufferBatch> Buffered => buffered;

	//Adds a draw out of a project's buffers, if anything is listening.
	internal static void Add( in BufferBatch batch )
	{
		if ( !Capturing || batch.Push is null ) return;
		if ( buffered.Count > 8192 ) return;

		buffered.Add( batch );
	}

	//The copies of the frame asked for, in the order they were.
	internal static IReadOnlyList<Grab> Grabs => grabs;

	//Adds a copy of the frame to take, if anything is listening.
	internal static void Add( in Grab grab )
	{
		if ( !Capturing || grab.Into is null ) return;
		if ( grabs.Count > 64 ) return;

		grabs.Add( grab );
	}

	/// <summary>Expands indices into a flat run of vertices, borrowing a frame array; no allocations</summary>
	public static Vertex[] Expand( Vertex[] vertices, int vertexCount, ReadOnlySpan<ushort> indices, out int count )
	{
		count = 0;
		if ( vertices is null || vertexCount <= 0 ) return System.Array.Empty<Vertex>();

		vertexCount = System.Math.Min( vertexCount, vertices.Length );

		if ( indices.IsEmpty )
		{
			count = vertexCount;
			return vertices;
		}

		return Expand( vertices.AsSpan(), vertexCount, indices, out count );
	}

	/// <summary>Same, from any span of vertices; only reads them, so callers need not copy first</summary>
	public static Vertex[] Expand( ReadOnlySpan<Vertex> vertices, int vertexCount, ReadOnlySpan<ushort> indices, out int count )
	{
		count = 0;
		if ( vertexCount <= 0 || indices.IsEmpty ) return System.Array.Empty<Vertex>();

		vertexCount = System.Math.Min( vertexCount, vertices.Length );

		var expanded = Take( indices.Length );

		for ( var i = 0; i < indices.Length; i++ )
		{
			var index = indices[i];

			//An index past the end of what was handed over draws nothing rather than reading whatever is next in memory.
			if ( index >= vertexCount )
			{
				Give( expanded );
				return System.Array.Empty<Vertex>();
			}

			expanded[i] = vertices[index];
		}

		count = indices.Length;
		return expanded;
	}
}
