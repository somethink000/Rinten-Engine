using NativeEngine;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rinten;

/// <summary>GPU buffer for compute shaders with CPU read/write; Structured maps to (RW)StructuredBuffer&lt;T&gt;</summary>
public partial class GpuBuffer : IValid, IDisposable
{
	/// <summary>Number of elements in the buffer.</summary>
	public int ElementCount { get; private set; }

	/// <summary>Size of a single element in the buffer.</summary>
	public int ElementSize { get; private set; }

	/// <summary>What sort of buffer this is</summary>
	public UsageFlags Usage { get; private set; }

	//Device buffer, created lazily at the declared size and then written in place; 0 until then
	internal ulong handle;

	//The hidden word an UsageFlags.Append buffer counts its writes in - a buffer of
	//four bytes of its own, bound beside it. See Bindings, which is what puts it there.
	private ulong counter;

	private bool disposed;

	private readonly object making = new();

	//The buffer on the device, made if it has not been. Nought where there is no device.
	internal ulong Handle
	{
		get
		{
			if ( handle != 0 || disposed || ElementCount <= 0 ) return handle;

			lock ( making )
			{
				if ( handle == 0 && !disposed ) Make();
			}

			return handle;
		}
	}

	//The counter an append buffer carries, made with it. Nought for any other.
	internal ulong Counter => Handle != 0 ? counter : 0;

	public bool IsValid => Handle != 0;

	//How many bytes the buffer holds.
	private long Bytes => (long)ElementCount * ElementSize;

	private void Make()
	{
		if ( !CoreRenderDevice.IsReady ) return;

		var made = CoreRenderDevice.ReserveBuffer( Bytes );
		if ( made == 0 ) return;

		if ( ( ( Usage & UsageFlags.Append ) == UsageFlags.Append ) )
		{
			//Starts at nought, which is what a count of nothing written is.
			Span<byte> zero = stackalloc byte[4];
			zero.Clear();

			counter = CoreRenderDevice.CreateBuffer( zero );
		}

		handle = made;
	}

	/// <summary>Creates a new GPU buffer with a specified number of elements and a specific buffer type.</summary>
	public GpuBuffer( int elementCount, int elementSize, UsageFlags flags = UsageFlags.Structured, string debugName = null )
	{
		Initialize( elementCount, elementSize, flags, debugName );
	}

	protected GpuBuffer() { }

	~GpuBuffer()
	{
		//The graveyard is the device's: handed to the main loop, not written to from the
		//middle of whatever a page's finalizer interrupted - see MainThread.InFinalizer.
		using var finalizing = MainThread.InFinalizer();
		MainThread.Queue( () => Dispose( false ) );
	}

	protected void Initialize( int elementCount, int elementSize, UsageFlags usageFlags = UsageFlags.Structured, string debugName = null )
	{
		if ( elementCount <= 0 ) throw new ArgumentException( "Element count must be greater than zero." );
		if ( elementSize <= 0 ) throw new ArgumentException( "Element size must be greater than zero." );

		//Structured buffers element size must be 4 byte aligned
		if ( ( ( usageFlags & UsageFlags.Structured ) == UsageFlags.Structured ) )
		{
			if ( elementSize % 4 != 0 )
			{
				throw new InvalidOperationException( $"Structured buffers element size must be divisible by 4" );
			}
		}

		//short or int
		if ( ( ( usageFlags & UsageFlags.Index ) == UsageFlags.Index ) )
		{
			if ( elementSize != 2 && elementSize != 4 )
			{
				throw new InvalidOperationException( $"Index buffers element size must be 2 or 4 bytes" );
			}
		}

		//Buffers can usually be as big as VRAM, but this seems reasonable until someone complains
		if ( (ulong)elementSize * (ulong)elementCount > UInt32.MaxValue )
		{
			throw new InvalidOperationException( $"Buffer can't exceed 4GB" );
		}

		//What the buffer is. The device's half is made the first time anything needs it - see Handle - so a
		//buffer declared before there is a device, or on a server that has none, is still a buffer of this shape.
		ElementCount = elementCount;
		ElementSize = elementSize;
		Usage = usageFlags;

		Name = debugName;
	}

	//What to call this where a device or a log names buffers.
	internal string Name { get; private set; }

	/// <summary>Destroys the GPU buffer, don't use it no more</summary>
	public void Dispose()
	{
		ThreadSafe.AssertIsMainThread();

		Dispose( true );
		GC.SuppressFinalize( this );
	}

	void Dispose( bool disposing )
	{
		disposed = true;

		//Through the graveyard, both: a frame in flight may still be reading them.
		if ( handle != 0 ) CoreRenderDevice.DestroyBuffer( handle );
		if ( counter != 0 ) CoreRenderDevice.DestroyBuffer( counter );

		handle = 0;
		counter = 0;
	}

	//Attribute buffers in name order (shader binding order), each append counter right after its buffer
	internal static int Bindings( RenderAttributes attributes, Span<ulong> into )
	{
		if ( attributes is null ) return 0;

		var at = 0;

		foreach ( var name in attributes.OrderedKeys() )
		{
			if ( at >= into.Length ) break;
			if ( !attributes.TryGetHeld( name, out var held ) || held.Reference is not GpuBuffer buffer ) continue;

			var bound = buffer.Handle;
			if ( bound == 0 ) continue;

			into[at++] = bound;

			if ( buffer.counter != 0 && at < into.Length ) into[at++] = buffer.counter;
		}

		return at;
	}

	//What is bigger than this goes onto the device ahead of the frame being recorded rather than through the frame's own
	//scratch, which a write lives in until the frame is done with it and which grows to the most any frame asked for.
	private const int FrameWriteMost = 4 * 1024 * 1024;

	//Bytes into the buffer where the frame being recorded is, in its order - so a write between two draws of it lands
	//between them - or ahead of the next frame where none is being recorded, or where the frame will not take it.
	private bool Write( long offset, ReadOnlySpan<byte> bytes )
	{
		var into = Handle;
		if ( into == 0 ) return false;

		if ( bytes.Length <= FrameWriteMost && CoreRenderDevice.Recording && !CoreRenderDevice.PortableFrame )
		{
			//Nothing of a list's is left open under it: a write is a copy, and a copy is not recorded inside a pass.
			CorePasses.Close();

			if ( CoreRenderDevice.FrameWrite( into, offset, bytes ) ) return true;
		}

		return CoreRenderDevice.WriteBuffer( into, offset, bytes );
	}

	//Bytes between two buffers, ordered the way Write is.
	private static bool Copy( ulong from, long fromOffset, ulong into, long intoOffset, long bytes )
	{
		if ( from == 0 || into == 0 || bytes <= 0 ) return false;

		if ( CoreRenderDevice.Recording && !CoreRenderDevice.PortableFrame )
		{
			CorePasses.Close();

			if ( CoreRenderDevice.FrameCopy( from, fromOffset, into, intoOffset, bytes ) ) return true;
		}

		return CoreRenderDevice.CopyBuffer( from, fromOffset, into, intoOffset, bytes );
	}

	//Whole words of one value, ordered the way Write is.
	private static bool Fill( ulong buffer, long offset, long bytes, uint value )
	{
		if ( buffer == 0 || bytes <= 0 ) return false;

		if ( CoreRenderDevice.Recording && !CoreRenderDevice.PortableFrame )
		{
			CorePasses.Close();

			if ( CoreRenderDevice.FrameFill( buffer, offset, bytes, value ) ) return true;
		}

		return CoreRenderDevice.FillBuffer( buffer, offset, bytes, value );
	}

	/// <summary>Retrieves the GPU buffer and copies them into a provided Span.</summary>
	public void GetData<T>( Span<T> data ) where T : unmanaged
	{
		var bytes = Math.Min( (long)data.Length * Unsafe.SizeOf<T>(), Bytes );

		GetDataInternal( MemoryMarshal.AsBytes( data ), 0, (uint)bytes );
	}

	//This operation is synchronous and will block until the specified range of data has been fully downloaded from the GPU.
	/// <summary>Retrieves a number of elements from the GPU buffer and copies them into a provided Span.</summary>
	public void GetData<T>( Span<T> data, int start, int count ) where T : unmanaged
	{
		CheckRange( start, count );

		var bytes = (long)count * ElementSize;

		if ( bytes > (long)data.Length * Unsafe.SizeOf<T>() )
			throw new ArgumentException( $"{count} elements of {ElementSize} bytes do not fit in {data.Length} of {Unsafe.SizeOf<T>()}", nameof( data ) );

		GetDataInternal( MemoryMarshal.AsBytes( data ), (uint)( (long)start * ElementSize ), (uint)bytes );
	}

	private void CheckRange( int start, int count )
	{
		if ( start < 0 || count < 0 || (long)start + count > ElementCount )
			throw new ArgumentOutOfRangeException( nameof( start ), $"elements {start} to {(long)start + count} are not all in a buffer of {ElementCount}" );
	}

	private void GetDataInternal( Span<byte> data, uint startInBytes, uint countInBytes )
	{
		if ( countInBytes == 0 ) return;

		var from = Handle;

		if ( from == 0 || !CoreRenderDevice.ReadBuffer( from, startInBytes, data[..(int)countInBytes] ) )
			Log.Warning( $"GpuBuffer.GetData read nothing back: {CoreRenderDevice.Description}" );
	}

	/// <summary>Asynchronously retrieves data from the GPU buffer and provides it to the callback.</summary>
	public void GetDataAsync<T>( Action<ReadOnlySpan<T>> callback ) where T : unmanaged
	{
		GetDataAsync( callback, 0, ElementCount );
	}

	//This operation is asynchronous and won't block the calling thread while data is downloaded from the GPU. The data span is only valid during the callback execution.
	/// <summary>Asynchronously retrieves a number of elements from the GPU buffer and provides it to the callback.</summary>
	public void GetDataAsync<T>( Action<ReadOnlySpan<T>> callback, int start, int count ) where T : unmanaged
	{
		ArgumentNullException.ThrowIfNull( callback );
		CheckRange( start, count );

		if ( count == 0 ) return;

		if ( !CoreReadbacks.Ask( Handle, (long)start * ElementSize, (long)count * ElementSize, callback ) )
			Log.Warning( $"GpuBuffer.GetDataAsync could not ask for the buffer back: {CoreRenderDevice.Description}" );
	}

	//Written where the frame being recorded is, in its order, and ahead of the next frame where none is. The span is free the moment this returns.
	/// <summary>Uploads data from a Span to the GPU, into this GpuBuffer from the element offset on.</summary>
	public void SetData<T>( ReadOnlySpan<T> data, int elementOffset = 0 ) where T : unmanaged
	{
		SetDataInternal( MemoryMarshal.AsBytes( data ), elementOffset );
	}

	/// <summary>Uploads data from a List to the GPU, into this GpuBuffer from the element offset on.</summary>
	public void SetData<T>( List<T> data, int elementOffset = 0 ) where T : unmanaged
	{
		SetData<T>( CollectionsMarshal.AsSpan( data ), elementOffset );
	}

	private void SetDataInternal( ReadOnlySpan<byte> data, int elementOffset )
	{
		if ( elementOffset < 0 ) throw new ArgumentOutOfRangeException( nameof( elementOffset ) );

		var offset = (long)elementOffset * ElementSize;

		if ( offset + data.Length > Bytes )
		{
			throw new ArgumentOutOfRangeException( $"SetData of {data.Length} bytes at element {elementOffset} runs past the end of a GpuBuffer of {Bytes}" );
		}

		if ( data.IsEmpty ) return;

		if ( !Write( offset, data ) )
			Log.Warning( $"GpuBuffer.SetData wrote nothing: {CoreRenderDevice.Description}" );
	}

	/// <summary>For <see cref="UsageFlags.Append"/> buffers there is a hidden uint 32-bit atomic counter in the buffer that contains the number of writes to the buffer after invocation of the compute shader. In order to get the value of the counter, the data needs to be copied to another GPU buffer that can be used.</summary>
	public void CopyStructureCount( GpuBuffer destBuffer, int destBufferOffset = 0 )
	{
		ArgumentNullException.ThrowIfNull( destBuffer );

		if ( destBufferOffset < 0 || destBufferOffset + 4L > destBuffer.Bytes )
			throw new ArgumentOutOfRangeException( nameof( destBufferOffset ) );

		if ( !HasCounter( nameof( CopyStructureCount ) ) ) return;

		Copy( Counter, 0, destBuffer.Handle, destBufferOffset, 4 );
	}

	//Whether this buffer carries a counter, said once where one is asked of a buffer that does not.
	private bool HasCounter( string what )
	{
		if ( Counter != 0 ) return true;

		if ( Handle != 0 && !( ( Usage & UsageFlags.Append ) == UsageFlags.Append ) && !saidNoCounter )
		{
			saidNoCounter = true;
			Log.Warning( $"GpuBuffer.{what}: only a buffer made with UsageFlags.Append carries a counter here - an AppendStructuredBuffer's" );
		}

		return false;
	}

	private bool saidNoCounter;

	/// <summary>Fills the entire buffer with a repeated uint32 value. Uses the native GPU fill command (vkCmdFillBuffer) — no CPU-side allocation needed.</summary>
	public void Clear( uint value = 0 )
	{
		//Whole words, which is what the fill takes: a buffer of an odd number of short indices has its
		//last two bytes' word filled too, and the device buffer is never less than whole words long.
		if ( !Fill( Handle, 0, ( Bytes + 3 ) & ~3L, value ) && !saidNoFill )
		{
			saidNoFill = true;
			Log.Warning( $"GpuBuffer.Clear filled nothing: {CoreRenderDevice.Description}" );
		}
	}

	private bool saidNoFill;

	/// <summary>Sets the counter value for <see cref="UsageFlags.Append"/> or <see cref="UsageFlags.Counter"/> structured buffers.</summary>
	public void SetCounterValue( uint counterValue )
	{
		if ( !HasCounter( nameof( SetCounterValue ) ) ) return;

		Span<byte> word = stackalloc byte[4];
		BitConverter.TryWriteBytes( word, counterValue );

		var into = Counter;

		if ( CoreRenderDevice.Recording && !CoreRenderDevice.PortableFrame )
		{
			CorePasses.Close();

			if ( CoreRenderDevice.FrameWrite( into, 0, word ) ) return;
		}

		CoreRenderDevice.WriteBuffer( into, 0, word );
	}

	//A range of this buffer copied into another on the device, in bytes - see GpuBuffer .CopyTo.
	private protected void CopyBytesTo( GpuBuffer destination, long fromOffset, long intoOffset, long bytes )
	{
		ArgumentNullException.ThrowIfNull( destination );

		if ( fromOffset < 0 || intoOffset < 0 || bytes < 0 || fromOffset + bytes > Bytes || intoOffset + bytes > destination.Bytes )
			throw new ArgumentOutOfRangeException( nameof( bytes ), $"a copy of {bytes} bytes from {fromOffset} into {intoOffset} runs past the end of a buffer" );

		if ( bytes == 0 ) return;

		if ( !Copy( Handle, fromOffset, destination.Handle, intoOffset, bytes ) )
			Log.Warning( $"GpuBuffer.CopyTo copied nothing: {CoreRenderDevice.Description}" );
	}
}

/// <summary>A typed GpuBuffer</summary>
public class GpuBuffer<T> : GpuBuffer where T : unmanaged
{
	public GpuBuffer( int elementCount, UsageFlags flags = UsageFlags.Structured, string debugName = null ) : base()
	{
		//Generic check is cached per T; the Type overload uses reflection on every call
		if ( !SandboxedUnsafe.IsAcceptablePod<T>() )
			throw new InvalidOperationException( $"{typeof( T )} is not allowed in GPU buffers" );

		Initialize( elementCount, Unsafe.SizeOf<T>(), flags, debugName );
	}

	public void GetData( Span<T> data ) => GetData<T>( data );
	public void GetData( Span<T> data, int start, int count ) => GetData<T>( data, start, count );
	public void SetData( Span<T> data, int elementOffset = 0 ) => SetData<T>( data, elementOffset );
	public void GetDataAsync( Action<ReadOnlySpan<T>> callback ) => GetDataAsync<T>( callback );
	public void GetDataAsync( Action<ReadOnlySpan<T>> callback, int start, int count ) => GetDataAsync<T>( callback, start, count );

	/// <summary>Tell the GPU to copy all elements from this buffer to <paramref name="dst"/>.</summary>
	public void CopyTo( GpuBuffer<T> dst )
	{
		CopyTo( dst, 0, 0, ElementCount );
	}

	public void CopyTo( GpuBuffer<T> dst, int elementCount )
	{
		CopyTo( dst, 0, 0, elementCount );
	}

	//On the device and nowhere else: ordered after what was handed to it before - in the frame being recorded where there is one - and before what comes after.
	/// <summary>Tell the GPU to copy a range of elements from this buffer to <paramref name="dst"/>.</summary>
	public void CopyTo( GpuBuffer<T> dst, int srcElementOffset, int destElementOffset, int elementCount )
	{
		ArgumentNullException.ThrowIfNull( dst );

		var size = Unsafe.SizeOf<T>();

		CopyBytesTo( dst, (long)srcElementOffset * size, (long)destElementOffset * size, (long)elementCount * size );
	}
}
