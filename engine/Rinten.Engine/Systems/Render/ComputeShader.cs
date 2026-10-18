using System.Linq;
using NativeEngine;
using Rinten.VR;

namespace Rinten;

/// <summary>A compute shader is a program that runs on the GPU, often with data provided to/from the CPU by means of a <see cref="GpuBuffer{T}"/> or a <see cref="Texture"/>.</summary>
public class ComputeShader
{
	/// <summary>Attributes that are passed to the compute shader on dispatch.</summary>
	public RenderAttributes Attributes { get; } = new RenderAttributes();

	//Where the shader is written, as a material would name it.
	private readonly string path;

	//Which of the file's compute entries runs, or null for MainCs.
	private readonly string entry;

	//The pipeline built out of its compute stage, made on first use.
	private ulong pipeline;

	//Whether the pipeline has been asked for, however that went.
	private bool asked;

	//What one group of its threads is, out of the code its [numthreads] compiled to.
	private (int X, int Y, int Z) group = (1, 1, 1);

	//Whether it is a pass over the frame - see RunInList.
	private bool pass;

	//The path it was found under, and the file on disk - its outputs are read out of the second.
	private string resource;
	private string source;

	//Profiler zone name (file and entry), built once with the pipeline
	private string zone;

	private static readonly HashSet<string> said = new();

	/// <summary>Create a compute shader from the specified path.</summary>
	public ComputeShader( string path, string entry = null )
	{
		this.entry = string.IsNullOrWhiteSpace( entry ) ? null : entry;

		//A shader that is not there is a shader that cannot be dispatched - which is what every use of this
		//checks for. Throwing instead would take a whole frame's worth of post processing down with it.
		this.path = string.IsNullOrWhiteSpace( path ) ? null : path;
	}

	/// <summary>Whether there is a shader behind this to run.</summary>
	public bool IsValid => Pipeline != 0;

	//Pipeline built on first request and cached, even on failure, so bad shaders don't retry per frame
	private ulong Pipeline
	{
		get
		{
			if ( asked ) return pipeline;

			asked = true;

			if ( path is null ) return 0;

			var named = path.Contains( '/' ) || path.Contains( '\\' ) ? path : "shaders/" + path;
			var code = entry is null
				? CoreShaderFile.Stages( named )?[(int)CoreShaderFile.Stage.Compute]
				: CoreResources.Entry( named, null, entry, CoreShaderCompiler.Stage.Compute );

			if ( code is null )
			{
				Log.Warning( entry is null
					? $"{path} has no compute stage - a compute shader is a MainCs"
					: $"{path} has no compute entry {entry} that compiles: {CoreRenderDevice.ShaderError}" );
				return 0;
			}

			//A pass over the frame - a file written against postprocess.slang - is bound as a blit is and stores into the
			//frame's pictures; anything else is the plain dispatch over buffers it always was. See VkFrame.PassDispatch.
			pass = CoreShaderFile.IsPass( named );
			resource = named;
			zone = Named( named, entry );
			source = CoreShaderFile.Source( CoreShaderFile.Real( named ) );

			pipeline = pass ? CoreRenderDevice.CreatePassPipeline( code ) : CoreRenderDevice.CreateCompute( code );

			if ( pipeline == 0 )
			{
				Log.Warning( $"{path} would not build a compute pipeline: {CoreRenderDevice.ShaderError}" );
				return 0;
			}

			//Out of the code itself where it says, which is always for ours; the text only where it does not.
			group = CoreShaderFile.ThreadGroupOf( code ) ?? CoreShaderFile.ThreadGroupOf( source, entry ?? "MainCs" );

			return pipeline;
		}
	}

	//Runs into one frame picture outside any stage; no frame colour, depth or normals are bound
	internal void RunApart( CorePasses.Named output, int threadsX, int threadsY, int threadsZ )
	{
		var built = Pipeline;

		if ( built == 0 || output is null || output.Picture == 0 ) return;
		if ( threadsX < 1 || threadsY < 1 || threadsZ < 1 ) return;

		if ( !pass )
		{
			Say( $"{path}: a pass that stores into a picture has to be written against postprocess.slang" );
			return;
		}

		CoreRenderDevice.ZoneBegin( zone );

		try
		{
			var push = CorePostBlit.ApartPush( resource, Attributes, output.Width, output.Height );

			Span<ulong> pictures = stackalloc ulong[1] { output.Picture };
			Span<int> levels = stackalloc int[1] { 0 };

			var size = group;

			CoreRenderDevice.DispatchPass( built, CoreSceneDrawer.WhiteTexture, pictures, levels,
				System.ReadOnlySpan<ulong>.Empty, push,
				Groups( threadsX, size.X ), Groups( threadsY, size.Y ), Groups( threadsZ, size.Z ) );
		}
		finally
		{
			CoreRenderDevice.ZoneEnd();
		}
	}

	//Runs over buffers alone outside any stage, a pass written against postprocess.slang binding them after the pictures' places - see
	//VkFrame.PassDispatch. False where it did not run.
	internal bool RunApartOver( ReadOnlySpan<ulong> buffers, int threadsX, int threadsY, int threadsZ )
	{
		var built = Pipeline;

		if ( built == 0 || buffers.IsEmpty || !pass ) return false;
		if ( threadsX < 1 || threadsY < 1 || threadsZ < 1 ) return false;

		CoreRenderDevice.ZoneBegin( zone );

		try
		{
			var push = CorePostBlit.ApartPush( resource, Attributes, 1, 1 );
			var size = group;

			return CoreRenderDevice.DispatchPass( built, CoreSceneDrawer.WhiteTexture, ReadOnlySpan<ulong>.Empty, ReadOnlySpan<int>.Empty,
				buffers, push, Groups( threadsX, size.X ), Groups( threadsY, size.Y ), Groups( threadsZ, size.Z ) );
		}
		finally
		{
			CoreRenderDevice.ZoneEnd();
		}
	}

	//Runs inside a frame from a command list, storing into outputs set on the list's attributes
	internal void RunInList( Rinten.Rendering.CommandList list, int threadsX, int threadsY, int threadsZ )
	{
		var built = Pipeline;

		if ( built == 0 || threadsX < 1 || threadsY < 1 || threadsZ < 1 ) return;

		//Under its own name in the frame's report, like every pass that is a draw - see zone. In a finally because the
		//dispatch below gives up in half a dozen places, and a zone left open takes the rest of the frame's report with it.
		CoreRenderDevice.ZoneBegin( zone );

		try
		{
			Run( built, list, threadsX, threadsY, threadsZ );
		}
		finally
		{
			CoreRenderDevice.ZoneEnd();
		}
	}

	//"bloom GatherCs", out of the file's name and the entry that runs.
	private static string Named( string path, string entry )
	{
		var slash = path.LastIndexOfAny( new[] { '/', '\\' } );
		var name = slash < 0 ? path : path[(slash + 1)..];

		if ( name.EndsWith( ".shader", StringComparison.OrdinalIgnoreCase ) )
			name = name[..^".shader".Length];

		return entry is null ? name : $"{name} {entry}";
	}

	//The dispatch itself, inside the zone RunInList opened.
	private void Run( ulong built, Rinten.Rendering.CommandList list, int threadsX, int threadsY, int threadsZ )
	{
		if ( !pass )
		{
			//Not inside a picture the list opened: a dispatch is recorded outside every pass, and one left open under it would be closed half way.
			CorePasses.Close();

			DispatchWithAttributes( list?.Values ?? Attributes, threadsX, threadsY, threadsZ );
			return;
		}

		//A pass over the frame may read it, so the point effects held go on first.
		CorePostBlit.Flush();
		CorePasses.Close();

		//Off the shader once and kept, not read off the disk again for every dispatch of every frame - see CoreResources.Stored.
		var outputs = CoreResources.Stored( resource );

		Span<ulong> pictures = stackalloc ulong[8];
		Span<int> levels = stackalloc int[8];

		var count = 0;
		var width = 0;
		var height = 0;

		foreach ( var output in outputs )
		{
			if ( count >= pictures.Length ) break;

			var key = output.IsAttribute ? output.Attribute : CoreShaderParams.Bare( output.Name );

			if ( list is null || !list.PictureOf( key, output.Name, out var picture, out var mip ) )
			{
				Say( $"{path}: its output {output.Name} is not set to a picture of the frame's - set it with Attributes.Set( \"{key}\", target.ColorTexture )" );
				return;
			}

			var declared = output.Format ?? CoreShaderParams.DefaultStorageFormat;
			var wanted = CoreRenderDevice.StorageFormat( declared );

			if ( wanted != 0 && wanted != picture.Format )
			{
				Say( $"{path}: its output {output.Name} is declared {declared} and {picture.Name} was made in another format - nothing is stored" );
				return;
			}

			mip = Math.Clamp( mip, 0, Math.Max( picture.Levels - 1, 0 ) );

			pictures[count] = picture.Picture;
			levels[count] = mip;

			if ( count == 0 )
			{
				width = Math.Max( picture.Width >> mip, 1 );
				height = Math.Max( picture.Height >> mip, 1 );
			}

			count++;
		}

		//The frame's size, for a pass that stores into a buffer rather than a picture.
		if ( count == 0 )
		{
			var size = CorePostBlit.OpenSize;
			width = (int)size.x;
			height = (int)size.y;
		}

		var push = CorePostBlit.ComputePush( resource, Attributes, list, width, height );

		if ( push is null )
		{
			Say( $"{path}: a pass over the frame runs inside one of the camera's stages, and this one was not" );
			return;
		}

		Span<ulong> buffers = stackalloc ulong[4];
		var bufferCount = Buffers( list?.Values ?? Attributes, buffers );

		CoreRenderDevice.DispatchPass( built, CoreSceneDrawer.WhiteTexture, pictures[..count], levels[..count],
			buffers[..bufferCount], push,
			Groups( threadsX, group.X ), Groups( threadsY, group.Y ), Groups( threadsZ, group.Z ) );
	}

	private static void Say( string text )
	{
		lock ( said )
		{
			if ( !said.Add( text ) ) return;
		}

		Log.Warning( text );
	}

	//Threads to groups, rounded up so the tail is covered.
	private static int Groups( int threads, int side )
		=> side <= 1 ? threads : ( threads + side - 1 ) / side;

	internal void Destroy()
	{
		if ( pipeline != 0 ) CoreRenderDevice.DestroyPipeline( pipeline );

		pipeline = 0;
		asked = false;
	}

	//Indirect dispatch from a command list over its buffers; frame-picture passes can't use this
	internal void RunIndirectInList( Rinten.Rendering.CommandList list, GpuBuffer indirectBuffer, uint indirectElementOffset )
	{
		if ( Pipeline == 0 ) return;

		if ( pass )
		{
			Say( $"{path}: a pass over the frame is dispatched by the size of what it stores into - DispatchComputeIndirect runs a plain compute shader over buffers" );
			return;
		}

		CorePasses.Close();

		DispatchIndirectWithAttributes( list?.Values ?? Attributes, indirectBuffer, indirectElementOffset );
	}

	/// <summary>Dispatch this compute shader using explicit thread counts.</summary>
	public void Dispatch( int threadsX = 32, int threadsY = 32, int threadsZ = 32 )
	{
		DispatchWithAttributes( Attributes, threadsX, threadsY, threadsZ );
	}

	/// <summary>Dispatch with group counts from an IndirectDispatchArguments buffer; offset is an element index</summary>
	public void DispatchIndirect( GpuBuffer indirectBuffer, uint indirectElementOffset = 0 )
	{
		DispatchIndirectWithAttributes( Attributes, indirectBuffer, indirectElementOffset );
	}

	public void DispatchWithAttributes( RenderAttributes attributes, int threadsX = 32, int threadsY = 32, int threadsZ = 32 )
	{
		if ( threadsX < 1 ) throw new ArgumentException( $"Cannot be less than 1", nameof( threadsX ) );
		if ( threadsY < 1 ) throw new ArgumentException( $"Cannot be less than 1", nameof( threadsY ) );
		if ( threadsZ < 1 ) throw new ArgumentException( $"Cannot be less than 1", nameof( threadsZ ) );

		var built = Pipeline;

		if ( built == 0 ) return;

		//The buffers it works over, in the order their names sort - a dictionary has none of its own, and a pass whose bindings move between frames is
		//a pass nobody can write a shader for. An append buffer's counter takes the binding after it. The rest of what the attributes hold is numbers.
		Span<ulong> buffers = stackalloc ulong[DispatchBuffers];

		var count = Buffers( attributes, buffers );

		if ( count == 0 )
		{
			Log.Warning( $"{path} was dispatched with no buffer to work over" );
			return;
		}

		Span<float> numbers = stackalloc float[CoreShaderParams.PushSlots * 4];

		var push = PushOf( attributes, numbers );

		if ( !CoreGpuWork.Begin( out var own ) )
		{
			Say( $"{path}: there is no device to dispatch it on" );
			return;
		}

		try
		{
			CoreRenderDevice.Dispatch( built, buffers[..count],
				Groups( threadsX, group.X ), Groups( threadsY, group.Y ), Groups( threadsZ, group.Z ),
				push );
		}
		finally
		{
			CoreGpuWork.End( own );
		}
	}

	//How many buffers a dispatch binds: what the core's set holds - see VkDevice.ObjectBindings - counters included.
	private const int DispatchBuffers = 10;

	//Its own declared numbers, in the push block - a compute pipeline has no material to read
	//them out of, so the same names arrive there instead. See CoreShaderParams.DeclarePush.
	private ReadOnlySpan<byte> PushOf( RenderAttributes attributes, Span<float> numbers )
	{
		numbers.Clear();

		CoreShaderParams.Into( numbers, CoreResources.Declared( path ),
			name => attributes is not null && attributes.TryGetDeclared( name, out var value )
				? value
				: null );

		return System.Runtime.InteropServices.MemoryMarshal.AsBytes( numbers );
	}

	//The buffers an attribute set holds, bound in order - see GpuBuffer.Bindings.
	private static int Buffers( RenderAttributes attributes, Span<ulong> into ) => GpuBuffer.Bindings( attributes, into );

	public void DispatchIndirectWithAttributes( RenderAttributes attributes, GpuBuffer indirectBuffer, uint indirectElementOffset = 0 )
	{
		if ( !indirectBuffer.IsValid() )
			throw new ArgumentException( $"Invalid buffer", nameof( indirectBuffer ) );

		if ( indirectBuffer.ElementSize != 12 )
			throw new ArgumentException( $"Buffer element size must 12 bytes", nameof( indirectBuffer ) );

		if ( indirectElementOffset >= indirectBuffer.ElementCount )
			throw new ArgumentOutOfRangeException( nameof( indirectElementOffset ), "Indirect element offset exceeds buffer bounds" );

		if ( !indirectBuffer.Usage.Contains( GpuBuffer.UsageFlags.IndirectDrawArguments ) )
			throw new ArgumentException( $"Buffer must have the required usage flag '{GpuBuffer.UsageFlags.IndirectDrawArguments}'", nameof( indirectBuffer ) );

		var built = Pipeline;

		if ( built == 0 ) return;

		if ( pass )
		{
			Say( $"{path}: a pass over the frame is dispatched by the size of what it stores into, not out of a buffer" );
			return;
		}

		Span<ulong> buffers = stackalloc ulong[DispatchBuffers];

		var count = Buffers( attributes, buffers );

		if ( count == 0 )
		{
			Log.Warning( $"{path} was dispatched with no buffer to work over" );
			return;
		}

		Span<float> numbers = stackalloc float[CoreShaderParams.PushSlots * 4];

		var push = PushOf( attributes, numbers );

		if ( !CoreGpuWork.Begin( out var own ) )
		{
			Say( $"{path}: there is no device to dispatch it on" );
			return;
		}

		try
		{
			//Three words a dispatch, one after another: the offset is an element's.
			CoreRenderDevice.DispatchIndirect( built, buffers[..count], indirectBuffer.Handle,
				(int)( indirectElementOffset * 12 ), push );
		}
		finally
		{
			CoreGpuWork.End( own );
		}
	}
}
