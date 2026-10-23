using Rinten.Audio;
using Rinten.Utility;

namespace Rinten;

//Mic voice chat: capture, gate silence by an adaptive noise floor, encode 20 ms frames into packets
//and decodes others'. Unavailable (IsValid false) on server, tests, web, and Android without HostMicrophone
internal static class VoiceManager
{
	static readonly Logger log = new( "Voice" );

	public static Action<Memory<byte>> OnCompressedVoiceData { get; set; }

	/// <summary>The rate voice data is decoded to.</summary>
	public static int SampleRate => VoiceCodec.SampleRate;

	/// <summary>Whether this machine can record a voice at all. False where there is no microphone of ours to open, and for a while after one failed to.</summary>
	public static bool IsValid => Supported && !( openFailed && timeSinceOpenFailed < RetrySeconds );

	/// <summary>Whether the microphone is on and a voice would be sent.</summary>
	public static bool IsListening { get; private set; }

	/// <summary>Whether the microphone is on and has just heard somebody talking.</summary>
	public static bool IsRecording => IsListening && timeSinceLastHear < 0.2f;

	static bool Supported => !Application.IsHeadless
		&& !Application.IsUnitTest
		&& !Application.OnWeb
		&& ( !OperatingSystem.IsAndroid() || HostMicrophone is not null )
		&& CoreNative.Available;

	//Asked before the microphone opens where the system guards it: true when it may, false while the player is asked or said no
	internal static Func<bool> HostMicrophone { get; set; }

	//How long after a microphone would not open before it is tried again.
	const float RetrySeconds = 10.0f;

	//How long the device is kept open after the last word. Opening one takes long enough to be a hitch, and push-to-talk would pay it on every press.
	const float CloseAfterSeconds = 20.0f;

	//How far behind the ring may get before audio is dropped - long past any hitch worth sending.
	const int RingSeconds = 2;

	//How long the gate stays open after the voice drops under it, in frames.
	const int HangoverFrames = 15;

	//How many frames from before the gate opened are sent with it.
	const int PreRollFrames = 2;

	static CoreAudioCapture capture;
	static VoiceCodec.Resampler resampler;
	static VoiceCodec.Encoder encoder;

	static bool openFailed;
	static RealTimeSince timeSinceOpenFailed;
	static RealTimeSince timeSinceStopped;
	static RealTimeSince timeSinceLastHear = 10;

	//What came off the ring, as it came; then as mono at the capture's rate.
	static float[] taken;
	static float[] mono;

	//Mono at the codec's rate, waiting to be a whole frame.
	static readonly float[] pending = new float[VoiceCodec.SampleRate];
	static int pendingCount;

	static readonly short[] frame = new short[VoiceCodec.FrameSamples];
	static readonly short[][] preRoll = { new short[VoiceCodec.FrameSamples], new short[VoiceCodec.FrameSamples] };
	static int preRollCount;
	static int preRollAt;

	static float floor;
	static int hangover;

	public static bool StartRecording()
	{
		if ( !IsValid ) return false;

		if ( capture is null && HostMicrophone is { } allowed && !allowed() ) return false;

		if ( capture is null && !Open() ) return false;

		encoder ??= new VoiceCodec.Encoder();
		encoder.Restart();

		capture.Clear();
		resampler?.Reset();

		pendingCount = 0;
		preRollCount = 0;
		hangover = 0;
		floor = 0.001f;

		IsListening = true;

		return true;
	}

	public static bool StopRecording()
	{
		if ( !IsValid || !IsListening ) return false;

		//What was said up to the release is still worth hearing.
		ReadVoice();
		Send();

		IsListening = false;
		timeSinceStopped = 0;

		return true;
	}

	static bool Open()
	{
		capture = CoreAudioCapture.OpenStream( SampleRate, 1, RingSeconds );

		if ( capture is null )
		{
			if ( !openFailed ) log.Warning( "No microphone could be opened; voice chat is off." );

			openFailed = true;
			timeSinceOpenFailed = 0;

			return false;
		}

		openFailed = false;

		//Miniaudio converts to the rate asked for, but a device is allowed to answer with its own - and then this does it.
		resampler = capture.SampleRate == SampleRate
			? null
			: new VoiceCodec.Resampler( capture.SampleRate );

		taken = new float[capture.SampleRate * capture.ChannelCount / 4];
		mono = new float[capture.SampleRate / 4];

		return true;
	}

	static void Close()
	{
		capture?.Dispose();
		capture = null;
		resampler = null;
	}

	static Superluminal _voiceTick = new Superluminal( "VoiceTick", Color.Yellow );

	static RealTimeSince timeSinceLastTick = 0;
	public static void Tick()
	{
		using ( _voiceTick.Start() )
		{
			if ( timeSinceLastTick < (1.0f / 30.0f) )
				return;

			timeSinceLastTick = 0;

			if ( capture is not null && !IsListening )
			{
				//Nobody is talking: what arrives is thrown away, and after a while the device is closed.
				capture.Clear();

				if ( timeSinceStopped > CloseAfterSeconds ) Close();

				return;
			}

			ReadVoice();
			Send();
		}
	}

	//Everything the ring has, as mono at the codec's rate, onto the end of what is pending.
	static void Pull()
	{
		//A ring that filled is a game that stopped for seconds. What it heard then is too old to be worth sending, so it starts again from now.
		if ( capture.Full )
		{
			capture.Clear();
			pendingCount = 0;
			return;
		}

		var channels = capture.ChannelCount;

		while ( true )
		{
			var got = capture.Take( taken );
			if ( got <= 0 ) break;

			for ( var i = 0; i < got; i++ )
			{
				var sum = 0.0f;
				for ( var c = 0; c < channels; c++ ) sum += taken[( i * channels ) + c];
				mono[i] = sum / channels;
			}

			var room = pending.Length - pendingCount;

			if ( resampler is null )
			{
				var copy = Math.Min( got, room );
				mono.AsSpan( 0, copy ).CopyTo( pending.AsSpan( pendingCount ) );
				pendingCount += copy;
				continue;
			}

			var input = got;
			var output = room;
			resampler.Process( mono.AsSpan( 0, got ), ref input, pending.AsSpan( pendingCount, room ), ref output );
			pendingCount += output;
		}
	}

	static void ReadVoice()
	{
		if ( capture is null || encoder is null ) return;

		Pull();

		var used = 0;

		while ( pendingCount - used >= VoiceCodec.FrameSamples )
		{
			Frame( pending.AsSpan( used, VoiceCodec.FrameSamples ) );
			used += VoiceCodec.FrameSamples;
		}

		//The part of a frame left over waits for the rest of it.
		pending.AsSpan( used, pendingCount - used ).CopyTo( pending );
		pendingCount -= used;
	}

	//One frame through the gate and, if it is speech, into the packet.
	static void Frame( ReadOnlySpan<float> samples )
	{
		var energy = 0.0f;

		for ( var i = 0; i < samples.Length; i++ )
		{
			var sample = Math.Clamp( samples[i], -1.0f, 1.0f );
			energy += sample * sample;
			frame[i] = (short)( sample * short.MaxValue );
		}

		var rms = MathF.Sqrt( energy / samples.Length );
		var db = 20.0f * MathF.Log10( rms + 1e-9f );

		//The floor: straight down to anything quieter, up by about 6 dB a second otherwise, so it
		//follows a room that got louder but never a voice, which does not hold still that long.
		floor = rms < floor ? MathF.Max( rms, 1e-5f ) : floor * 1.014f;

		//Holding a key to talk says the voice is meant, so the gate only keeps out what is barely there; an open microphone has to be surer.
		var pushToTalk = Preferences.VoiceMode == VoiceMode.PushToTalk;
		var absolute = pushToTalk ? -60.0f : -52.0f;
		var margin = pushToTalk ? 6.0f : 9.0f;

		var threshold = MathF.Max( absolute, ( 20.0f * MathF.Log10( floor ) ) + margin );
		var wasOpen = hangover > 0;

		if ( db > threshold ) hangover = HangoverFrames;
		else if ( hangover > 0 ) hangover--;

		if ( hangover <= 0 )
		{
			//Kept, in case the next frame opens the gate and wants what came before it.
			frame.CopyTo( preRoll[preRollAt] );
			preRollAt = ( preRollAt + 1 ) % PreRollFrames;
			preRollCount = Math.Min( preRollCount + 1, PreRollFrames );
			return;
		}

		if ( !wasOpen )
		{
			for ( var i = preRollCount; i > 0; i-- )
				Add( preRoll[( preRollAt - i + PreRollFrames ) % PreRollFrames] );

			preRollCount = 0;
		}

		Add( frame );
		timeSinceLastHear = 0;
	}

	static void Add( short[] samples )
	{
		if ( encoder.IsFull ) Send();

		encoder.Add( samples );
	}

	static void Send()
	{
		if ( encoder is null || !encoder.HasFrames ) return;

		var packet = encoder.Take();

		OnCompressedVoiceData?.Invoke( packet );
	}

	/// <summary>Uncompress a voice buffer and call ondata with the result: 16 bit mono at <see cref="SampleRate"/>, good only for the length of the call.</summary>
	public static void Uncompress( byte[] buffer, Action<Memory<short>> ondata )
	{
		if ( buffer is null || buffer.Length == 0 ) return;

		VoiceCodec.Decode( buffer, ondata );
	}
}
