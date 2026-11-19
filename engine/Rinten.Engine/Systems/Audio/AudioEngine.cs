
using NativeEngine;

namespace Rinten.Audio;

//A mix frame size is 512 samples A second is made up of 44100 samples This means
//one frame is about 11.6ms So as long as mixing takes less than 10ms we're okay
internal static partial class AudioEngine
{
	public static bool IsValid => EngineAudio.Device.IsValid;

	/// <summary>How many output channels do we have? Generally 2, but if they have a 7.1 setup it can be more.</summary>
	public static int ChannelCount { get; private set; } = 2;

	/// <summary>How many seconds one sample lasts</summary>
	public static float SecondsPerSample => 1.0f / SamplingRate;

	//The engine's output sampling rate. This doesn't change.
	public static float SamplingRate => 44100.0f; //MIX_DEFAULT_SAMPLING_RATE

	//The size of one
	public static int MixBufferSize => 512; //MIX_BUFFER_SIZE


	[ConVar( "voice_loopback", ConVarFlags.Protected )]
	public static bool VoiceLoopback { get; set; } = false;

	static AudioEngine()
	{
		if ( Application.IsUnitTest )
			return;

		if ( IsValid )
		{
			ChannelCount = EngineAudio.Device.ChannelCount;
		}

		DspFactory.CreateBuiltIn();
	}

	public static void ClearBuffer()
	{
		if ( !IsValid )
			return;

		EngineAudio.Device.Clear();
	}

	[ConVar( "snd_mute", ConVarFlags.Saved )]
	public static bool Mute { get; set; }

	//Silent for this run only, without touching the saved snd_mute: RINTEN_MUTE=1 or -nosound, for runs nobody is listening to
	private static readonly bool MutedByLaunch = Environment.GetEnvironmentVariable( "RINTEN_MUTE" ) == "1"
		|| Rinten.Utility.CommandLine.HasSwitch( "-nosound" );

	[ConVar( "snd_mute_losefocus", ConVarFlags.Saved )]
	public static bool MuteLoseFocus { get; set; }

	//Called every frame.
	internal static void Tick()
	{
		var device = EngineAudio.Device;

		if ( !device.IsValid )
			return;

		//Setting the mute state is also what starts the native device for the first time - if it is never set, that device never plays anything.
		var mute = Mute || MutedByLaunch || (MuteLoseFocus && !EngineInput.IsAppActive());
		device.Muted = mute;

		device.Tick();

		//And the songs playing, which say when they went round or ended.
		MusicPlayer.TickAll();
	}
}
