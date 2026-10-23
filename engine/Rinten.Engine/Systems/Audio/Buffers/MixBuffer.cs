namespace Rinten.Audio;

/// <summary>Contains 512 samples of audio data, this is used when mixing a single channel</summary>
public sealed unsafe class MixBuffer : IDisposable
{
	//512 floats, about 2kb

	float* _buffer;
	const int bufferLengthBytes = 512 * sizeof( float );

	//This is locked from being disposed
	bool _locked;

	//It is memory, and the mixing that fills it is managed code - the native flag is what it was when a buffer could belong to their audio device instead. Every buffer is ours now.
	/// <summary>Somewhere to put 512 samples.</summary>
	public MixBuffer( bool native = false )
	{
		_buffer = (float*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc( bufferLengthBytes, 16 );

		Buffer.Clear();
	}

	~MixBuffer()
	{
		//Nothing can be holding the lock once nothing holds the buffer, and a finalizer that threw would take the process with it.
		Free();
	}

	public void Dispose()
	{
		if ( _locked )
		{
			throw new MethodAccessException( "MixBuffer is locked, can't be disposed" );
		}

		GC.SuppressFinalize( this );

		Free();
	}

	void Free()
	{
		if ( _buffer is null ) return;

		System.Runtime.InteropServices.NativeMemory.AlignedFree( _buffer );
		_buffer = null;
	}

	/// <summary>Silence this buffer</summary>
	public void Silence()
	{
		Buffer.Clear();
	}

	//The start of the floats
	internal MixBufferLock DataPointer( out IntPtr ptr )
	{
		//Already locked - cannot lock twice!

		if ( _locked )
			throw new ArgumentException();

		_locked = true;
		ptr = (IntPtr)_buffer;

		return new MixBufferLock( this );
	}

	internal ref struct MixBufferLock
	{
		private MixBuffer mixBuffer;

		public MixBufferLock( MixBuffer mixBuffer )
		{
			this.mixBuffer = mixBuffer;
		}

		public void Dispose()
		{
			mixBuffer._locked = false;
		}
	}

	//Get direct access to the memory
	internal unsafe Span<float> Buffer => new Span<float>( _buffer, 512 );

	//The same memory, for something that hands it to native code. Nothing to pin: the samples do not live
	//in the managed heap, which is what makes it safe to give a decoder the address and let it write.
	internal unsafe float* Pointer => _buffer;

	/// <summary>Set this buffer to this value</summary>
	public unsafe void CopyFrom( MixBuffer other )
	{
		//A memory copy, which needs nobody's engine: the samples are in unmanaged memory either way, and this is the same instruction their glue was calling.
		System.Buffer.MemoryCopy( other._buffer, _buffer, bufferLengthBytes, bufferLengthBytes );
	}

	/// <summary>Mix this buffer with another</summary>
	public void MixFrom( MixBuffer other, float scale )
	{
		if ( scale.AlmostEqual( 1.0f, 0.001f ) )
		{
			Buffer.AsFloatSpan().Add( other.Buffer );
			return;
		}

		Buffer.AsFloatSpan().AddScaled( other.Buffer, scale );
	}

	/// <summary>Mix this buffer with another</summary>
	public void MixFrom( MultiChannelBuffer other, float scale )
	{
		scale = scale / ((float)other.ChannelCount);

		for ( int i = 0; i < other.ChannelCount; i++ )
		{
			MixFrom( other.Get( i ), scale );
		}
	}

	/// <summary>Scale the buffer by volume</summary>
	public void Scale( float volume )
	{
		Buffer.AsFloatSpan().Scale( volume );
	}

	/// <summary>Clamp each sample to [-1, 1] to prevent digital clipping from multiple voices accumulating.</summary>
	public void HardLimit()
	{
		var span = Buffer;
		for ( var i = 0; i < span.Length; i++ )
		{
			if ( span[i] > 1.0f ) span[i] = 1.0f;
			else if ( span[i] < -1.0f ) span[i] = -1.0f;

			//Neither comparison holds for NaN, which the device would play as noise
			else if ( float.IsNaN( span[i] ) ) span[i] = 0.0f;
		}
	}

	public float LevelMax => Buffer.AsFloatSpan().Max();
	public float LevelAvg => Buffer.AsFloatSpan().Average();

	public void RandomFill()
	{
		var span = Buffer;
		var rand = new Random();

		for ( int i = 0; i < span.Length; i++ )
		{
			span[i] = (float)rand.NextDouble() * 100f; //Random float values
		}
	}
}
