using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rinten.Native;

namespace Rinten.Audio;

//One playing of a sound with its own decoder and resampler (which also does pitch); no allocations
internal sealed unsafe class CoreAudioSampler : AudioSampler
{
	//How many input frames are decoded at a time. Enough that a mix buffer is usually one decode, small enough
	//that the leftovers are not worth worrying about. Fixed, because the audio thread must not allocate.
	private const int ScratchFrames = 1024;

	private readonly CoreSound sound;

	private void* decoder;
	private void* resampler;

	private readonly int outputRate;
	private bool looping;
	private bool finished;

	//Input frames decoded and not yet turned into output. Pinned.
	private readonly float[] scratch;
	private int scratchFrames;
	private int scratchTaken;

	//Interleaved output, before it is split into the caller's channels. Pinned.
	private float[] staging;

	//Where the playhead is, counted in output frames - the same frames the mixer counts, so
	//a game asking where a sound is gets an answer in its own units rather than the file's.
	private long position;

	//What the resampler was last set to, so it is only told when it changes.
	private float pitch;

	//Where in the sound's own samples it is, for a sound that has them.
	private long pcmAt;

	//The frame the decoder hands out next, in the file's frames - kept here because
	//the decoder does not say, and a loop has to know when it has reached its end.
	private long decodedAt;

	//The loop, in the file's frames: where it goes back to, and one past its last frame (0 for the end of the sound). See CoreSound.LoopStart.
	private readonly long loopStart;
	private readonly long loopEnd;

	private MultiChannelBuffer buffer;

	public CoreAudioSampler( CoreSound sound, bool looping )
		: this( sound, (int)AudioEngine.SamplingRate, looping )
	{
		if ( !IsValid ) return;

		//As many channels as the sound has, which is what the mixer expects to be handed: a mono sound stays mono until something spatialises it.
		buffer = new MultiChannelBuffer( ChannelCount );
	}

	//A sampler at a rate of its own, with no mixer buffer of its own: what a test reads through, and what the mixer's constructor above builds on.
	internal CoreAudioSampler( CoreSound sound, int outputRate, bool looping )
	{
		if ( sound is null ) return;
		if ( outputRate <= 0 ) outputRate = 44100;

		this.sound = sound;
		this.outputRate = outputRate;
		this.looping = looping;

		loopStart = sound.LoopStart;
		loopEnd = sound.LoopEnd;

		//A sound that was decoded in one go has no bytes left and needs no decoder - its samples are read straight out of it.
		if ( sound.Pcm is null )
		{
			int channels, rate;
			ulong frames;

			fixed ( byte* data = sound.Bytes )
			{
				decoder = MiniAudio.rn_decoder_open( data, (nuint)sound.Bytes.Length, sound.ChannelCount, sound.SampleRate,
					&channels, &rate, &frames );
			}

			if ( decoder is null ) return;
		}

		resampler = MiniAudio.rn_resampler_open( sound.ChannelCount, sound.SampleRate, outputRate );

		if ( resampler is null )
		{
			if ( decoder is not null ) MiniAudio.rn_decoder_close( decoder );
			decoder = null;
			return;
		}

		pitch = 1.0f;

		scratch = GC.AllocateArray<float>( ScratchFrames * sound.ChannelCount, pinned: true );
		staging = GC.AllocateArray<float>( AudioEngine.MixBufferSize * sound.ChannelCount, pinned: true );

		ChannelCount = Math.Clamp( sound.ChannelCount, 1, 8 );
	}

	public bool IsValid => resampler is not null;

	public override int ChannelCount { get; }

	/// <summary>Always: the whole file is in memory before a sampler is made, so there is nothing to wait for.</summary>
	public override bool IsReadyToMix => IsValid;

	public override bool ShouldContinueMixing => IsValid && !finished;

	/// <summary>Whether the sound has played out. A loop never does.</summary>
	public bool Finished => finished;

	/// <summary>Where the playhead is, in the mixer's frames rather than the file's.</summary>
	public long Position => position;

	public bool Looping
	{
		get => looping;
		set => looping = value;
	}

	private int loops;

	/// <summary>How many times a loop has gone back to its start - read from the main thread to say so.</summary>
	public int Loops => System.Threading.Volatile.Read( ref loops );

	//The last TapLength frames that went out, folded to one channel, as a ring; null
	//until somebody asks for it - see Listen. What a spectrum is worked out from.
	private float[] tap;
	private int tapAt;

	//How many frames the tap keeps.
	internal const int TapLength = 1024;

	//Starts keeping what goes out, and hands back the most recent of it, oldest first. Main thread; the audio thread writes the ring as it plays,
	//and a copy taken halfway through a write is a picture of the sound a few samples later on one side - which a spectrum cannot tell apart.
	internal bool Listen( Span<float> into )
	{
		if ( tap is null )
		{
			tap = new float[TapLength];
			return false;
		}

		var at = System.Threading.Volatile.Read( ref tapAt );

		for ( var i = 0; i < into.Length && i < TapLength; ++i )
			into[i] = tap[( at + i ) % TapLength];

		return true;
	}

	public override int SamplePosition
	{
		get => IsValid ? (int)position : 0;
		set { if ( IsValid ) Seek( value ); }
	}

	internal override void DelayOrSkipSamples( int numSamples )
	{
		if ( !IsValid || numSamples == 0 ) return;

		//Skipping forward is a seek. Being asked to start later than now is not something a sampler can do
		//to itself - it is silence in front of the sound, which the mixer puts there by not asking yet.
		if ( numSamples < 0 ) return;

		Seek( position + numSamples );
	}

	//Set the resampler to play at a pitch, if it is not already.
	private void Retune( float pitch )
	{
		if ( pitch <= 0.0f ) pitch = 1.0f;
		if ( pitch == this.pitch ) return;

		this.pitch = pitch;

		//Playing faster and playing at a lower output rate are the same arithmetic, so pitch is the
		//ratio rather than a step of its own - two lots of resampling would be two lots of error.
		if ( pitch == 1.0f )
		{
			MiniAudio.rn_resampler_set_rate( resampler, sound.SampleRate, outputRate );
			return;
		}

		//Off one, the ratio over a fixed denominator rather than an output rate of 48000 / pitch: exact to a few parts in a hundred
		//thousand however near one it is, and small enough that miniaudio's 32-bit timer arithmetic on a rate change cannot overflow
		var input = Math.Clamp( Math.Round( (double)sound.SampleRate * pitch / outputRate * PitchSteps ), 1.0, int.MaxValue );

		MiniAudio.rn_resampler_set_rate( resampler, (int)input, PitchSteps );
	}

	//The denominator a pitch's ratio is given over - see Retune.
	private const int PitchSteps = 1 << 15;

	//Decode more input, going back to the start of the loop at its end if it is looping. The loop is cut here, in the file's frames, before the resampler:
	//the resampler sees one unbroken run of input going round, so the join is as smooth as the file's own loop points make it, whatever the pitch.
	private bool Refill( float* scratchPointer )
	{
		scratchFrames = 0;
		scratchTaken = 0;

		//Already samples: copied out of the sound rather than decoded.
		if ( sound.Pcm is not null )
		{
			var channels = sound.ChannelCount;
			var total = sound.Pcm.Length / channels;

			//Only a loop stops at the loop's end; played through, it goes on to the end of the sound.
			var end = looping && loopEnd > 0 ? Math.Min( loopEnd, total ) : total;

			if ( pcmAt >= end )
			{
				if ( !looping || loopStart >= end ) return false;

				pcmAt = loopStart;
				System.Threading.Interlocked.Increment( ref loops );
			}

			var take = (int)Math.Min( ScratchFrames, end - pcmAt );

			sound.Pcm.AsSpan( (int)( pcmAt * channels ), take * channels ).CopyTo( new Span<float>( scratchPointer, take * channels ) );

			pcmAt += take;
			scratchFrames = take;

			return take > 0;
		}

		var read = MiniAudio.rn_decoder_read( decoder, scratchPointer, Wanted() );

		if ( read == 0 )
		{
			if ( !looping ) return false;

			if ( MiniAudio.rn_decoder_seek( decoder, (ulong)loopStart ) == 0 ) return false;

			decodedAt = loopStart;
			System.Threading.Interlocked.Increment( ref loops );

			read = MiniAudio.rn_decoder_read( decoder, scratchPointer, Wanted() );

			if ( read == 0 ) return false;
		}

		decodedAt += (long)read;
		scratchFrames = (int)read;

		return true;
	}

	//How many frames to ask the decoder for: a scratch's worth, or up to the end of the loop if that comes first. Nothing, once it is there.
	private ulong Wanted()
	{
		if ( !looping || loopEnd <= 0 ) return ScratchFrames;

		return (ulong)Math.Clamp( loopEnd - decodedAt, 0, ScratchFrames );
	}

	/// <summary>Fills deinterleaved channels with the next frames at pitch; returns frames with sound. Audio thread</summary>
	[SkipLocalsInit]
	public int Read( float** channels, int channelCount, int frames, float pitch )
	{
		if ( !IsValid || channels is null || frames <= 0 || channelCount <= 0 ) return 0;

		Retune( pitch );

		var sourceChannels = sound.ChannelCount;
		var wanted = frames * sourceChannels;

		//A caller asking for more than a mix buffer, which only a tool reading a sound whole does - never the mixer, never on the audio thread.
		if ( staging.Length < wanted ) staging = GC.AllocateArray<float>( wanted, pinned: true );

		var scratchPointer = (float*)Unsafe.AsPointer( ref MemoryMarshal.GetArrayDataReference( scratch ) );
		var stagingPointer = (float*)Unsafe.AsPointer( ref MemoryMarshal.GetArrayDataReference( staging ) );

		var produced = 0;

		while ( produced < frames )
		{
			if ( scratchTaken >= scratchFrames )
			{
				if ( !Refill( scratchPointer ) )
				{
					finished = true;
					break;
				}
			}

			var input = (ulong)( scratchFrames - scratchTaken );
			var output = (ulong)( frames - produced );

			if ( MiniAudio.rn_resampler_process( resampler,
				scratchPointer + ( (long)scratchTaken * sourceChannels ), &input,
				stagingPointer + ( (long)produced * sourceChannels ), &output ) == 0 ) break;

			scratchTaken += (int)input;
			produced += (int)output;

			//Neither side moved: the resampler wants more than there is and there is no more coming. Stopping here beats spinning on the audio thread.
			if ( input == 0 && output == 0 ) break;
		}

		for ( var channel = 0; channel < channelCount; channel++ )
		{
			var destination = channels[channel];
			if ( destination is null ) continue;

			if ( channel < sourceChannels )
			{
				for ( var frame = 0; frame < produced; frame++ )
				{
					//Silence for a sample that is not a number: let into a mixer, one stays in every filter and reverb after it for good
					var sample = stagingPointer[( (long)frame * sourceChannels ) + channel];
					destination[frame] = float.IsFinite( sample ) ? sample : 0.0f;
				}
			}
			else
			{
				//A caller with more channels than the sound has: silence rather than the last channel again, which would be the same sound twice and louder for it.
				Unsafe.InitBlockUnaligned( destination, 0, (uint)( produced * sizeof( float ) ) );
			}

			//Whatever is left of the buffer when the sound ran out.
			if ( produced < frames )
			{
				Unsafe.InitBlockUnaligned( destination + produced, 0, (uint)( ( frames - produced ) * sizeof( float ) ) );
			}
		}

		position += produced;

		return produced;
	}

	/// <summary>Moves the playhead to <paramref name="frame"/>, counted in the mixer's frames. False if the sound would not seek.</summary>
	public bool Seek( long frame )
	{
		if ( !IsValid || frame < 0 ) return false;

		//The caller counts in the mixer's frames and the file is in its own, so the position it asks for is converted on the way in.
		var ratio = sound.SampleRate / (double)outputRate;
		var target = (ulong)( frame * ratio );

		//A sound held as samples is simply read from somewhere else.
		if ( decoder is null )
		{
			var total = sound.Pcm.Length / sound.ChannelCount;

			pcmAt = Math.Min( (long)target, total );

			MiniAudio.rn_resampler_reset( resampler );

			scratchFrames = 0;
			scratchTaken = 0;
			position = frame;
			finished = false;

			return true;
		}

		if ( MiniAudio.rn_decoder_seek( decoder, target ) == 0 ) return false;

		//Seek one frame early and discard it, so the resampler's previous frame is correct after a jump
		MiniAudio.rn_resampler_reset( resampler );

		if ( target > 0 && MiniAudio.rn_decoder_seek( decoder, target - 1 ) != 0 )
		{
			var scratchPointer = (float*)Unsafe.AsPointer( ref MemoryMarshal.GetArrayDataReference( scratch ) );
			var stagingPointer = (float*)Unsafe.AsPointer( ref MemoryMarshal.GetArrayDataReference( staging ) );

			if ( MiniAudio.rn_decoder_read( decoder, scratchPointer, 1 ) == 1 )
			{
				ulong input = 1;
				ulong output = 1;

				MiniAudio.rn_resampler_process( resampler, scratchPointer, &input, stagingPointer, &output );
			}
			else
			{
				//Nothing there to prime with - the sound is shorter than the seek.
				MiniAudio.rn_decoder_seek( decoder, target );
			}
		}

		scratchFrames = 0;
		scratchTaken = 0;
		position = frame;
		finished = false;

		//Every way through the above leaves the decoder about to hand out the frame that was asked for.
		decodedAt = (long)target;

		return true;
	}

	public override void Sample( float pitch )
	{
		if ( !IsValid || buffer is null )
		{
			buffer?.Silence();
			return;
		}

		if ( !ShouldContinueMixing )
		{
			buffer.Silence();
			return;
		}

		//Straight into the mixer's own buffers. They are a run of samples per speaker, which is the
		//shape this writes, and they are native memory, so there is nothing to pin and nothing to copy.
		var channels = stackalloc float*[ChannelCount];

		for ( var channel = 0; channel < ChannelCount; channel++ )
		{
			channels[channel] = buffer.Get( channel ).Pointer;
		}

		var produced = Read( channels, ChannelCount, AudioEngine.MixBufferSize, pitch );

		if ( tap is { } ring ) Tap( ring, channels, produced );
	}

	//What just went out, folded to one channel, onto the end of the tap's ring.
	private void Tap( float[] ring, float** channels, int produced )
	{
		var at = tapAt;
		var scale = 1.0f / ChannelCount;

		for ( var frame = 0; frame < produced; ++frame )
		{
			var sum = 0.0f;
			for ( var channel = 0; channel < ChannelCount; ++channel ) sum += channels[channel][frame];

			ring[at] = sum * scale;
			if ( ++at >= ring.Length ) at = 0;
		}

		System.Threading.Volatile.Write( ref tapAt, at );
	}

	/// <summary>Reads the sound from where it stands to its end, as 16 bit samples. Multi-channel sounds are downmixed to mono unless asked to be kept interleaved. This is for whoever wants to look at a sound rather than hear it - a waveform in the editor, say.</summary>
	public short[] ReadAll( bool interleaved )
	{
		if ( !IsValid || buffer is null ) return null;

		//To its end means once through: a loop would never get there.
		looping = false;

		var frames = new List<short>();
		var channels = stackalloc float*[ChannelCount];

		while ( ShouldContinueMixing )
		{
			for ( var channel = 0; channel < ChannelCount; channel++ )
			{
				channels[channel] = buffer.Get( channel ).Pointer;
			}

			var read = Read( channels, ChannelCount, AudioEngine.MixBufferSize, 1.0f );
			if ( read <= 0 ) break;

			for ( var frame = 0; frame < read; frame++ )
			{
				if ( interleaved && ChannelCount > 1 )
				{
					for ( var channel = 0; channel < ChannelCount; channel++ )
						frames.Add( ToPcm( channels[channel][frame] ) );

					continue;
				}

				var sum = 0.0f;

				for ( var channel = 0; channel < ChannelCount; channel++ )
					sum += channels[channel][frame];

				frames.Add( ToPcm( sum / ChannelCount ) );
			}
		}

		return frames.ToArray();
	}

	private static short ToPcm( float sample ) => (short)( Math.Clamp( sample, -1.0f, 1.0f ) * short.MaxValue );

	public override MultiChannelBuffer GetLastReadSamples() => buffer;

	public override void Dispose()
	{
		if ( resampler is not null )
		{
			MiniAudio.rn_resampler_close( resampler );
			resampler = null;
		}

		if ( decoder is not null )
		{
			MiniAudio.rn_decoder_close( decoder );
			decoder = null;
		}

		if ( buffer is null ) return;

		MainThread.QueueDispose( buffer );
		buffer = null;
	}
}
