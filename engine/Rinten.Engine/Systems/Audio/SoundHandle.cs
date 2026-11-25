using Rinten.Audio;
using System.Collections.Generic;
using System.IO;

namespace Rinten;

/// <summary>A handle to a sound that is currently playing. You can use this to control the sound's position, volume, pitch etc.</summary>
[Expose]
public partial class SoundHandle : IValid, IDisposable
{
	static readonly HashSet<SoundHandle> active = new();

	internal AudioSampler sampler;

	int _ticks;
	Transform _transform = Transform.Zero;

	static SoundHandle _empty;
	internal NativeReverbEffect _reverb;

	//Source room reverb estimate. Written by SoundSimulationSystem on the main thread.
	internal ReverbSnapshot SourceRoom { get; set; }
	internal int OcclusionPhase { get; set; } = -1;
	internal int DiffractionTick;
	internal RealTimeUntil ReverbTailUntil;
	internal bool SamplerEnded;

	internal NativeReverbEffect GetOrCreateReverb()
	{
		_reverb ??= new NativeReverbEffect();
		return _reverb;
	}

	//RealTime that this sound was created
	internal float _CreatedTime;

	//An empty, do nothing sound, that we can return to avoid NREs
	internal static SoundHandle Empty
	{
		get
		{
			if ( _empty is null )
			{
				_empty = new SoundHandle();
			}

			return _empty;
		}
	}


	/// <summary>Position of the sound.</summary>
	public Vector3 Position
	{
		get => _transform.Position;
		set => _transform.Position = value;
	}

	/// <summary>The direction the sound is facing</summary>
	public Rotation Rotation
	{
		get => _transform.Rotation;
		set => _transform.Rotation = value;
	}

	/// <summary>This sound's transform</summary>
	public Transform Transform
	{
		get => _transform;
		set => _transform = value;
	}

	/// <summary>Volume of the sound.</summary>
	public float Volume { get; set; } = 1.0f;

	/// <summary>A debug name to help identify the sound</summary>
	public string Name { get; set; }

	/// <summary>How 3d the sound should be. 0 means no 3d, 1 means fully</summary>
	[Range( 0, 1 )]
	public float SpacialBlend { get; set; } = 1.0f;

	/// <summary>How many units the sound can be heard from.</summary>
	public float Distance { get; set; } = 15_000f;

	/// <summary>The falloff curve for the sound.</summary>
	public Curve Falloff { get; set; } = new Curve( new( 0, 1, 0, -1.8f ), new( 0.05f, 0.22f, 3.5f, -3.5f ), new( 0.2f, 0.04f, 0.16f, -0.16f ), new( 1, 0 ) );

	/// <summary>The fadeout curve for when the sound stops.</summary>
	public Curve Fadeout { get; set; } = new Curve( new( 0, 1 ), new( 1, 0 ) );

	/// <summary>The fadein curve for when the sound starts.</summary>
	public Curve Fadein { get; set; } = new Curve( new( 0, 0 ), new( 1, 1 ) );


	[Obsolete( "This is not used anymore" )]
	public float Decibels { get; set; } = 70.0f;

	/// <summary>Pitch of the sound.</summary>
	public float Pitch { get; set; } = 1.0f;

	/// <summary>
	/// How strongly the sound shifts in pitch when it and the listener move toward or away from each other (Doppler).
	/// 1 is physical, 0 turns it off.
	/// </summary>
	public float DopplerScale { get; set; } = 1.0f;

	VelocityTracker _motion;

	/// <summary>Whether the sound is currently playing or not.</summary>
	public bool IsPlaying => !IsStopped;

	/// <summary>Whether the sound is currently paused or not.</summary>
	public bool Paused { get; set; } = false;

	/// <summary>Sound is done</summary>
	public bool Finished { get; set; }

	/// <summary>Enable reverb simulation for this sound (reflections/late reverb).</summary>
	public bool ReverbEnabled { get; set; } = true;

	/// <summary>Legacy alias for <see cref="ReverbEnabled"/>.</summary>
	[Obsolete( "Use ReverbEnabled instead." )]
	public bool Reflections
	{
		get => ReverbEnabled;
		set => ReverbEnabled = value;
	}

	/// <summary>Allow this sound to be occluded by geometry etc</summary>
	public bool OcclusionEnabled { get; set; } = true;

	/// <summary>Legacy alias for <see cref="OcclusionEnabled"/>.</summary>
	[Obsolete( "Use OcclusionEnabled instead." )]
	public bool Occlusion
	{
		get => OcclusionEnabled;
		set => OcclusionEnabled = value;
	}

	/// <summary>Legacy occlusion radius setting. No longer used by the simulation.</summary>
	[Obsolete( "OcclusionRadius is no longer used by the simulation." ), Hide]
	public float OcclusionRadius { get; set; } = 32.0f;

	/// <summary>Should the sound fade out over distance</summary>
	public bool DistanceAttenuation { get; set; } = true;

	/// <summary>How much this sound contributes to room reverb. 1 = full reverb, 0 = completely dry.</summary>
	public float Reverb { get; set; } = 1.0f;

	/// <summary>Should the sound get absorbed by air, so it sounds different at distance</summary>
	public bool AirAbsorption { get; set; } = true;

	/// <summary>Legacy transmission toggle. Transmission is now derived from occlusion and material response.</summary>
	[Obsolete( "Transmission is derived from occlusion and material response." ), Hide]
	public bool Transmission { get; set; } = true;

	/// <summary>Which mixer do we want to write to</summary>
	public Mixer TargetMixer { get; set; }

	//Marks this sound as voice/speech audio (e.g., from VoiceComponent). Voice sounds
	//use cheaper HRTF interpolation since they don't benefit from bilinear filtering.
	internal bool IsVoice { get; set; }

	/// <summary>How many samples per second?</summary>
	public int SampleRate { get; private init; }

	//Keep playing silently for a second or two, to finish reverb effect
	internal RealTimeUntil TimeUntilFinished { get; set; }

	//Keep playing until faded out
	internal RealTimeUntil TimeUntilFaded { get; set; }

	//Time remaining until the fade-in completes
	internal RealTimeUntil TimeUntilFadedIn { get; set; }

	//Have we started fading out?
	internal bool IsFadingOut { get; set; }

	//Are we currently fading in?
	internal bool IsFadingIn { get; set; }

	/// <summary>True if the sound has been stopped</summary>
	public bool IsStopped => !IsValid;

	[Obsolete( "Use Time instead" )]
	public float ElapsedTime => Time;

	int _pendingSeekSample = -1;
	int _lastSamplePosition;

	/// <summary>The current time of the playing sound in seconds. Note: for some formats seeking may be expensive, and some may not support it at all.</summary>
	public float Time
	{
		get
		{
			if ( IsStopped ) return 0.0f;
			if ( sampler is null ) return 0.0f;

			return SampleRate > 0 ? System.Threading.Volatile.Read( ref _lastSamplePosition ) / (float)SampleRate : 0.0f;
		}
		set
		{
			if ( sampler is null ) return;

			int sample = (int)(value * SampleRate);
			//Publish the seek intent first so a mix in-flight picks it up; reflect it immediately on the getter.
			System.Threading.Interlocked.Exchange( ref _pendingSeekSample, sample );
			System.Threading.Volatile.Write( ref _lastSamplePosition, sample );
		}
	}

	//Mix-thread: apply any pending seek, sample, then publish the position back to the main thread.
	internal void SampleWithSeek( AudioSampler s, float pitch )
	{
		int pending = System.Threading.Interlocked.Exchange( ref _pendingSeekSample, -1 );
		if ( pending >= 0 ) s.SamplePosition = pending;

		s.Sample( pitch );

		if ( System.Threading.Volatile.Read( ref _pendingSeekSample ) < 0 )
		{
			System.Threading.Volatile.Write( ref _lastSamplePosition, s.SamplePosition );
		}
	}

	public void Stop( float fadeTime = 0.0f )
	{
		if ( Finished || IsFadingOut ) return;

		if ( fadeTime > 0.0f )
		{
			TimeUntilFaded = fadeTime;
			IsFadingOut = true;

			return;
		}

		Finished = true;
	}

	/// <summary>Place the listener at 0,0,0 facing 1,0,0.</summary>
	public bool ListenLocal { get; set; }

	/// <summary>If true, then this sound won't be played unless voice_loopback is 1. The assumption is that it's the local user's voice. Amplitude and visme data will still be available!</summary>
	public bool Loopback { get; set; }

	/// <summary>Measure of audio loudness.</summary>
	public float Amplitude { get; set; }

	volatile bool _destroyed;

	//Weak reference to the scene, so we don't prevent GC of the scene when sound handles are held in static queues.
	private readonly WeakReference<Scene> _sceneRef;

	//Scene this sound belongs to. May return null if the scene has been collected.
	internal Scene Scene => _sceneRef is not null && _sceneRef.TryGetTarget( out var scene ) ? scene : null;

	//A sound of ours, played straight from a sampler. No sound resource behind it: what this plays is a stream the game is filling as it
	//goes, and there is nothing to look up or preload. Everything past this point - mixers, distance, effects - does not know the difference.
	internal SoundHandle( AudioSampler ours, int sampleRate )
	{
		ThreadSafe.AssertIsMainThread();

		_sceneRef = new WeakReference<Scene>( Game.ActiveScene );

		SampleRate = sampleRate;
		sampler = ours;

		active.Add( this );
		_CreatedTime = RealTime.Now;
	}

	//A playing of a sound file: the file is held once and this reads its own way through it.
	internal SoundHandle( SoundFile file )
	{
		ThreadSafe.AssertIsMainThread();

		SoundFile = file;
		_sceneRef = new WeakReference<Scene>( Game.ActiveScene );

		SampleRate = file.IsValid() && file.Rate > 0 ? file.Rate : 48000;

		TryCreateMixer();
		active.Add( this );
		_CreatedTime = RealTime.Now;
	}

	//an empty soundhandle
	internal SoundHandle()
	{
		SampleRate = 48000;
		_destroyed = true;
		_CreatedTime = RealTime.Now;
	}

	//Return true if this has no mixer specified, so will use the default mixer
	internal bool WantsDefaultMixer() => TargetMixer is null;

	//Return true if we want to play on this mixer. Will return true if we have no mixer specified, and the provided mixer is the default.
	internal bool IsTargettingMixer( Mixer mixer )
	{
		if ( _destroyed ) return false;
		if ( WantsDefaultMixer() && Mixer.Default == mixer ) return true;
		if ( TargetMixer is null ) return false;
		if ( string.IsNullOrEmpty( mixer.Name ) ) return false;

		//Compare names instead of mixers, because they may have deserialized etc
		return TargetMixer.Name == mixer.Name;
	}

	//Gets the effective mixer this sound will play on. Returns the TargetMixer if set, otherwise the default mixer.
	internal Mixer GetEffectiveMixer()
	{
		if ( _destroyed ) return null;
		return TargetMixer ?? Mixer.Default;
	}

	//Returns true if this sound is ready to be mixed.
	internal bool CanBeMixed()
	{
		if ( !IsValid ) return false;
		if ( sampler is null ) return false;
		if ( Finished ) return false;
		return true;
	}

	public bool IsValid => !_destroyed;

	void TryCreateMixer()
	{
		if ( sampler is not null )
			return;

		//A handle with no file behind it is playing something that was handed over as a sampler already - there is nothing here to make.
		if ( !SoundFile.IsValid() )
			return;

		sampler = CoreSounds.Sampler( SoundFile.ResourcePath ?? SoundFile.Name );

		if ( sampler is null )
			Finished = true;

	}

	public void Dispose()
	{
		if ( _destroyed )
			return;

		_destroyed = true;

		DisposeSources();

		Audio.MixingThread.QueueSamplerDisposal( sampler );
		sampler = null;

		active.Remove( this );

		if ( LipSync.Enabled )
			LipSync.DisableLipSync();
	}

	~SoundHandle()
	{
		using var finalizing = MainThread.InFinalizer();
		MainThread.QueueDispose( this );
	}

	//Cheap per-frame update: dispose if finished, otherwise follow parent.
	internal bool PreTick()
	{
		if ( _destroyed ) return false;
		if ( Finished ) { Dispose(); return false; }
		if ( Paused ) return false;
		UpdateFollower();
		return true;
	}

	//Full per-frame update; runs only for handles that survived voice culling.
	internal void TickForSnapshot( IReadOnlyList<Listener> removedListeners )
	{
		if ( _destroyed ) return;
		TryCreateMixer();
		UpdateSources( removedListeners );
		_ticks++;
	}

	/// <summary>Called to push changes to a sound immediately, rather than waiting for the next tick. You should call this if you make changes to a sound.</summary>
	[System.Obsolete( "This no longer needs to exist" )]
	public void Update()
	{

	}

	//Reused scratch list to avoid allocations in Shutdown/StopAll.
	static readonly List<SoundHandle> _tickList = new();

	internal static void StopAll( float fade, Mixer mixer = null )
	{
		_tickList.Clear();
		_tickList.AddRange( active );
		foreach ( var handle in _tickList )
		{
			if ( mixer is not null && handle.TargetMixer != mixer ) continue;
			if ( handle.IsValid ) handle.Stop( fade );
		}
	}

	internal static void Shutdown()
	{
		//Snapshot active so Dispose() can safely remove from the set during iteration.
		_tickList.Clear();
		_tickList.AddRange( active );
		active.Clear();

		foreach ( var handle in _tickList )
		{
			if ( !handle.IsValid ) continue;
			handle.Dispose();
		}
	}

	internal static void StopAllWithParent( GameObject parent, float fade )
	{
		_tickList.Clear();
		_tickList.AddRange( active );
		foreach ( var handle in _tickList )
		{
			if ( handle.Parent != parent ) continue;
			if ( handle.IsValid ) handle.Stop( fade );
		}
	}

	internal static void StopAll( SoundFile file )
	{
		_tickList.Clear();
		_tickList.AddRange( active );
		foreach ( var handle in _tickList )
		{
			if ( handle.SoundFile != file ) continue;
			if ( handle.IsValid ) handle.Stop();
		}
	}

	public static void GetActive( List<SoundHandle> handles )
	{
		foreach ( var handle in active )
		{
			if ( !handle.IsValid() ) continue;
			if ( handle._ticks == 0 ) continue;
			if ( handle.Paused ) continue;

			handles.Add( handle );
		}
	}

	//Copy every entry from the active set without filtering.
	internal static void CopyActiveUnfiltered( List<SoundHandle> handles )
	{
		foreach ( var handle in active ) handles.Add( handle );
	}

	internal Audio.VoiceState BuildVoiceState( Audio.VoiceFrameSnapshot snap )
	{
		var fadeVolume = 1.0f;
		if ( IsFadingOut ) fadeVolume *= Fadeout.EvaluateDelta( (float)TimeUntilFaded.Fraction );
		if ( IsFadingIn ) fadeVolume *= Fadein.EvaluateDelta( (float)TimeUntilFadedIn.Fraction );

		var isLocal = ListenLocal || Scene is null;
		var sourceOffset = snap.AllModels.Count;

		if ( isLocal )
		{
			snap.AllModels.Add( _acousticModel );
			snap.AllParams.Add( _acousticModel?.GetParams() ?? default );
			snap.AllBinaurals.Add( _binauralEffect );
		}
		else
		{
			for ( var i = 0; i < snap.Listeners.Count; i++ )
			{
				var listener = snap.Listeners[i].Listener;
				var src = GetDirectSoundModel( listener );
				snap.AllModels.Add( src );
				snap.AllParams.Add( src?.GetParams() ?? default );
				snap.AllBinaurals.Add( GetBinaural( listener ) );
			}
		}

		var reverb = isLocal ? null : GetOrCreateReverb();

		//One pitch per voice, so the Doppler shift follows the first listener in the sound's scene. Not for streams (voice
		//chat, video, music): they have to be read as fast as they arrive.
		var pitch = Pitch;
		if ( !isLocal && !IsVoice && SoundFile is not null )
		{
			_motion.Update( Position, RealTime.NowDouble );

			//Not before the sound's speed is known: a new sound moving with the listener would start off pitch
			if ( _motion.Known )
			{
				foreach ( var listener in snap.Listeners )
				{
					if ( listener.Scene != Scene ) continue;

					pitch *= Audio.Doppler.Factor( Position, _motion.Velocity, listener.MixTransform.Position, listener.Listener.Motion.Velocity, DopplerScale );
					break;
				}
			}
		}

		return new Audio.VoiceState
		{
			Sampler = sampler,
			Pitch = pitch,
			Volume = Volume,
			FadeVolume = fadeVolume,
			IsFadingOut = IsFadingOut,
			FadeOutTimer = TimeUntilFaded,
			IsFadingIn = IsFadingIn,
			FadeInTimer = TimeUntilFadedIn,
			ListenLocal = isLocal,
			Loopback = Loopback,
			IsVoice = IsVoice,
			SpacialBlend = SpacialBlend,
			Position = Position,
			Scene = Scene,
			TargetMixer = TargetMixer,
			CreatedTime = _CreatedTime,
			SourceOffset = sourceOffset,
			SourceCount = snap.AllModels.Count - sourceOffset,
			HasLipSync = LipSync.Enabled,
			LipSync = LipSync,
			Handle = this,
			Reverb = reverb,
			ReverbRoom = ReverbEnabled && reverb is not null ? SourceRoom : default,
		};
	}

	//Sample-only voice for a handle that lost the per-mixer priority race. SampleVoices still
	//advances the sampler; Mixer.ShouldPlay rejects SourceCount == 0 so it never gets mixed.
	internal Audio.VoiceState BuildSampleOnlyVoiceState()
	{
		return new Audio.VoiceState
		{
			Sampler = sampler,
			Pitch = Pitch,
			Loopback = Loopback,
			IsVoice = IsVoice,
			Scene = Scene,
			TargetMixer = TargetMixer,
			CreatedTime = _CreatedTime,
			SourceOffset = 0,
			SourceCount = 0,
			Handle = this,
			IsFadingOut = IsFadingOut,
			FadeOutTimer = TimeUntilFaded,
			IsFadingIn = IsFadingIn,
			FadeInTimer = TimeUntilFadedIn,
		};
	}

}
