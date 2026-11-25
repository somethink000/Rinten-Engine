using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;

namespace Rinten.Audio;

//Voice wire format: Opus, 20 ms frames, a few per packet, one decoder per talker stream
//Packet: [tag] [stream: 4] [first frame's sequence: 2] [frame count] ([length] [frame])*
internal static class VoiceCodec
{
	/// <summary>The rate a voice is coded at and decoded to.</summary>
	public const int SampleRate = 48000;

	/// <summary>Samples in one frame - twenty milliseconds.</summary>
	public const int FrameSamples = SampleRate / 50;

	/// <summary>The most one coded frame may take, so its length fits in a byte.</summary>
	public const int MaxFrameBytes = 255;

	/// <summary>The most frames in one packet.</summary>
	public const int MaxFramesPerPacket = 12;

	//The first byte of every packet, and the version of this layout.
	const byte Tag = 0xB1;

	const int HeaderBytes = 8;

	/// <summary>The biggest packet there can be.</summary>
	public const int MaxPacketBytes = HeaderBytes + ( MaxFramesPerPacket * ( 1 + MaxFrameBytes ) );

	//How many lost frames in a row are guessed at before the gap is just let go.
	const int MaxConcealedFrames = 5;

	//How long a talker can be silent before their decoder is let go of.
	const float ForgetSeconds = 30.0f;

	static VoiceCodec()
	{
		//One implementation everywhere: the probe would otherwise pick up whatever libopus a machine
		//happens to have, and a voice would be coded by different code on different computers.
		OpusCodecFactory.AttemptToUseNativeLibrary = false;
	}

	//Here and not in VoiceManager because it is Concentus's, and VoiceManager is asked every frame: a field of Concentus's type there loaded the codec into every game, and onto every page, whether anybody spoke or not.
	/// <summary>A microphone's rate turned into the codec's.</summary>
	public sealed class Resampler
	{
		readonly IResampler resampler;

		public Resampler( int fromRate ) => resampler = ResamplerFactory.CreateResampler( 1, fromRate, SampleRate, 5 );

		public void Reset() => resampler.ResetMem();

		/// <summary>As much of <paramref name="input"/> as fits, resampled into <paramref name="output"/>; both counts are updated to what was used.</summary>
		public void Process( Span<float> input, ref int inputCount, Span<float> output, ref int outputCount )
			=> resampler.ProcessInterleaved( input, ref inputCount, output, ref outputCount );
	}

	/// <summary>One talker's side: the encoder, and the packet being put together.</summary>
	public sealed class Encoder : IDisposable
	{
		readonly IOpusEncoder opus;
		readonly byte[] packet = new byte[MaxPacketBytes];
		int length;
		int frames;

		uint stream;
		ushort sequence;

		public Encoder()
		{
			opus = OpusCodecFactory.CreateEncoder( SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP );
			opus.Bitrate = 24000;
			opus.Complexity = 5;
			opus.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
			opus.UseInbandFEC = true;
			opus.PacketLossPercent = 10;

			Restart();
		}

		/// <summary>A new stream: a fresh encoder, a new number, the count from zero.</summary>
		public void Restart()
		{
			opus.ResetState();

			stream = (uint)Random.Shared.Next( 1, int.MaxValue );
			sequence = 0;
			length = 0;
			frames = 0;
		}

		/// <summary>Whether the packet has frames in it waiting to be taken.</summary>
		public bool HasFrames => frames > 0;

		/// <summary>Whether the packet is full and must be taken before the next frame.</summary>
		public bool IsFull => frames >= MaxFramesPerPacket;

		/// <summary>Code one frame of <see cref="FrameSamples"/> samples onto the packet.</summary>
		public void Add( ReadOnlySpan<short> frame )
		{
			if ( IsFull ) return;

			if ( frames == 0 )
			{
				packet[0] = Tag;
				BinaryPrimitives.WriteUInt32LittleEndian( packet.AsSpan( 1 ), stream );
				BinaryPrimitives.WriteUInt16LittleEndian( packet.AsSpan( 5 ), sequence );
				length = HeaderBytes;
			}

			var written = opus.Encode( frame[..FrameSamples], FrameSamples, packet.AsSpan( length + 1, MaxFrameBytes ), MaxFrameBytes );
			if ( written <= 0 ) return;

			packet[length] = (byte)written;
			length += 1 + written;

			frames++;
			packet[7] = (byte)frames;

			sequence++;
		}

		/// <summary>The packet so far, and a fresh one started. Valid until the next <see cref="Add"/>.</summary>
		public Memory<byte> Take()
		{
			var taken = packet.AsMemory( 0, frames > 0 ? length : 0 );

			frames = 0;
			length = 0;

			return taken;
		}

		public void Dispose() => ( opus as IDisposable )?.Dispose();
	}

	//One talker as heard: their decoder, and where in their stream we are.
	sealed class Listener
	{
		public IOpusDecoder Opus;
		public ushort Next;
		public bool Started;
		public RealTimeSince LastHeard;
	}

	static readonly Dictionary<uint, Listener> listeners = new();
	static readonly object padlock = new();
	static readonly short[] decoded = new short[FrameSamples * ( MaxFramesPerPacket + MaxConcealedFrames )];
	static RealTimeSince lastSwept;

	/// <summary>Decode a packet, hiding any frames lost since the talker's last one, and hand what it says to <paramref name="ondata"/> - 16 bit mono at <see cref="SampleRate"/>. The callback gets its own copy, and runs outside the decoder lock, so it may keep the samples. False, with nothing handed over, when it is not a voice packet.</summary>
	public static bool Decode( ReadOnlySpan<byte> packet, Action<Memory<short>> ondata )
	{
		if ( packet.Length < HeaderBytes || packet[0] != Tag ) return false;

		var stream = BinaryPrimitives.ReadUInt32LittleEndian( packet[1..] );
		var sequence = BinaryPrimitives.ReadUInt16LittleEndian( packet[5..] );
		var count = packet[7];

		if ( count == 0 || count > MaxFramesPerPacket ) return false;

		short[] result = null;

		lock ( padlock )
		{
			Sweep();

			if ( !listeners.TryGetValue( stream, out var listener ) )
			{
				listener = new Listener { Opus = OpusCodecFactory.CreateDecoder( SampleRate, 1 ) };
				listeners[stream] = listener;
			}

			listener.LastHeard = 0;

			var produced = 0;
			var at = HeaderBytes;

			for ( var i = 0; i < count; i++, sequence++ )
			{
				if ( at >= packet.Length ) break;

				var size = packet[at];
				if ( at + 1 + size > packet.Length ) break;

				var frame = packet.Slice( at + 1, size );
				at += 1 + size;

				if ( listener.Started )
				{
					//How far ahead of what we expected this frame is. Behind - a duplicate, or late
					//past its turn - reads as a huge gap in unsigned arithmetic, and is dropped.
					var gap = (ushort)( sequence - listener.Next );

					if ( gap >= 0x8000 ) continue;

					if ( gap > 0 && gap <= MaxConcealedFrames )
					{
						//The lost frames guessed at, the last of them rebuilt from the copy this frame carries of it.
						for ( var lost = 0; lost < gap; lost++ )
						{
							var fec = lost == gap - 1;
							produced += DecodeFrame( listener.Opus, fec ? frame : ReadOnlySpan<byte>.Empty, produced, fec );
						}
					}
				}

				if ( size > 0 ) produced += DecodeFrame( listener.Opus, frame, produced, false );

				listener.Next = (ushort)( sequence + 1 );
				listener.Started = true;
			}

			if ( produced > 0 ) result = decoded.AsSpan( 0, produced ).ToArray();
		}

		//The callback can keep the memory, so it gets a copy rather than the decoder's shared scratch;
		//and it runs after the lock, so a slow or re-entrant callback can't stall or deadlock another talker.
		if ( result is not null ) ondata?.Invoke( result );

		return true;
	}

	//One frame into the output at at; how many samples that made. What arrives off the network is
	//whatever somebody sent, so a frame the decoder refuses is heard as nothing rather than thrown.
	static int DecodeFrame( IOpusDecoder opus, ReadOnlySpan<byte> frame, int at, bool fec )
	{
		if ( at + FrameSamples > decoded.Length ) return 0;

		try
		{
			var made = opus.Decode( frame, decoded.AsSpan( at, FrameSamples ), FrameSamples, fec );
			return made > 0 ? made : 0;
		}
		catch ( OpusException )
		{
			return 0;
		}
		catch ( ArgumentException )
		{
			return 0;
		}
	}

	//Let go of talkers not heard from in a while, at most once a second.
	static void Sweep()
	{
		if ( lastSwept < 1.0f ) return;
		lastSwept = 0;

		List<uint> quiet = null;

		foreach ( var (stream, listener) in listeners )
		{
			if ( listener.LastHeard > ForgetSeconds ) ( quiet ??= new() ).Add( stream );
		}

		if ( quiet is null ) return;

		foreach ( var stream in quiet )
		{
			( listeners[stream].Opus as IDisposable )?.Dispose();
			listeners.Remove( stream );
		}
	}
}
