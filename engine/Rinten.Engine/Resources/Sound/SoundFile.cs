using Rinten.Audio;

namespace Rinten;

public enum SoundFormat : byte
{
	PCM16 = 0,
	PCM8,
	MP3,
	ADPCM,
};

//A sound file is our own core's sound - the file as it was written, held once and played by as many samplers as ask - so what a sound file knows is what the core knows about it.
/// <summary>A sound resource.</summary>
public partial class SoundFile : Resource, IValid
{
	/// <summary>Options for creating a sound from raw PCM data via <see cref="FromPcm(string, Span{byte}, PcmOptions?)"/>.</summary>
	public struct PcmOptions()
	{
		/// <summary>Number of channels (1 = mono, 2 = stereo).</summary>
		public int Channels { get; set; } = 1;

		/// <summary>Sample rate in Hz (e.g. 44100).</summary>
		public uint Rate { get; set; } = 44100;

		/// <summary>Bits per sample (8, 16, or 32).</summary>
		public int Bits { get; set; } = 16;

		/// <summary>Whether the sound should loop. Overridden by <see cref="LoopStart"/>/<see cref="LoopEnd"/> if set.</summary>
		public bool Loop { get; set; } = false;

		/// <summary>Start of the loop region, in sample frames (one sample per channel). -1 means no loop.</summary>
		public int LoopStart { get; set; } = -1;

		/// <summary>End of the loop region, in sample frames - the first frame after it. 0 means end of sound.</summary>
		public int LoopEnd { get; set; } = 0;
	}

	/// <summary>Options for FromWav/FromMp3; WAVs loop as their chunks declare. Positions in sample frames</summary>
	public struct LoadOptions()
	{
		/// <summary>Loop the whole sound. Overridden by <see cref="LoopStart"/>/<see cref="LoopEnd"/> if set.</summary>
		public bool Loop { get; set; } = false;

		/// <summary>Start of the loop region, in sample frames. -1 uses whatever the file declares, or the start.</summary>
		public int LoopStart { get; set; } = -1;

		/// <summary>End of the loop region, in sample frames - the first frame after it. 0 uses whatever the file declares, or the end of the sound.</summary>
		public int LoopEnd { get; set; } = 0;
	}

	//The sound behind this file.
	private CoreSound sound;

	//Keyed by resource path. Wrappers are created here both by game code asking for a sound and by
	//the resource system loading one - the key has to match either way, so normalize case and slashes.
	internal static Dictionary<string, SoundFile> Loaded = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>true if sound is loaded</summary>
	public bool IsLoaded => sound is not null;

	/// <summary>Format of the audio file. Everything our core decodes is handed out as 16 bit samples, whatever it was written as.</summary>
	public SoundFormat Format => SoundFormat.PCM16;

	/// <summary>Bits per each sample of this sound file.</summary>
	public int BitsPerSample => 16;

	/// <summary>Number of channels this audio file has.</summary>
	public int Channels => sound?.ChannelCount ?? 0;

	/// <summary>Bytes per each sample of this sound file.</summary>
	public int BytesPerSample => 2;

	/// <summary>Size of one sample, typically this would be "sample size * channel count", but can vary on audio format.</summary>
	public int SampleFrameSize => BytesPerSample * Channels;

	/// <summary>Sample rate of this sound file, per second.</summary>
	public int Rate => sound?.SampleRate ?? 0;

	/// <summary>Duration of the sound this sound file contains, in seconds.</summary>
	public float Duration => sound?.Duration ?? 0;

	public override bool IsValid => sound is not null;

	//Can be played
	public bool IsValidForPlayback => IsValid;

	private SoundFile( string path, CoreSound sound )
	{
		this.sound = sound;

		Name = path;
		ResourcePath = path;

		RegisterWeakResourceId( path );
	}

	/// <summary>What this sound is called - the path it was loaded from.</summary>
	public string Name { get; private set; }

	internal static void Init()
	{
		Shutdown();

		Loaded = new Dictionary<string, SoundFile>( StringComparer.OrdinalIgnoreCase );
	}

	internal static void Shutdown()
	{
		if ( Loaded != null )
		{
			foreach ( var file in Loaded.Values )
			{
				file.Destroy();
			}
		}
	}

	internal override void Destroy()
	{
		//The sound itself belongs to CoreSounds, which holds one copy for everything playing it - so this lets go of it rather than freeing it.
		sound = null;

		base.Destroy();
	}

	/// <summary>Load a new sound from disk. Includes automatic caching.</summary>
	public static SoundFile Load( string filename )
	{
		ThreadSafe.AssertIsMainThread( "SoundFile.Load" );

		if ( string.IsNullOrWhiteSpace( filename ) )
			return null;

		filename = filename.Replace( '\\', '/' );

		if ( Loaded.TryGetValue( filename, out var soundFile ) )
			return soundFile;

		if ( Mounting.Directory.TryLoad( filename, out object sound ) && sound is SoundFile s )
			return s;

		//The core takes the file as the game named it, and a name in the legacy compiled format as the file it was
		//compiled from - so a ported project asks for a .vsnd and gets the wav sitting beside it.
		var loaded = CoreSounds.Get( filename );
		if ( loaded is null )
			return null;

		soundFile = new SoundFile( filename, loaded );
		Loaded[filename] = soundFile;

		return soundFile;
	}

	//The file under a sound changed - replaced, or baked again from its settings - and what is already
	//loaded under that name takes up the new one, so its length and channels say what now plays.
	internal static void Reread( string filename )
	{
		if ( string.IsNullOrWhiteSpace( filename ) ) return;

		if ( !Loaded.TryGetValue( filename.Replace( '\\', '/' ), out var soundFile ) ) return;

		if ( CoreSounds.Get( soundFile.ResourcePath ?? filename ) is { } reread )
			soundFile.sound = reread;
	}

	//Hold a sound the game built at runtime, under a name of its own.
	private static SoundFile Create( string filename, ReadOnlySpan<byte> encoded, bool looping, long loopStart, long loopEnd )
	{
		if ( Application.IsHeadless )
			return null;

		var sound = CoreSounds.FromMemory( filename, encoded, looping, loopStart, loopEnd );
		if ( sound is null )
			return null;

		var soundFile = new SoundFile( filename, sound );
		Loaded[filename] = soundFile;

		return soundFile;
	}

	//A WAV header for raw samples, so a sound built from PCM reads back through the same decoder as one that came off disk.
	private static byte[] WrapAsWav( ReadOnlySpan<byte> pcm, int channels, uint rate, int bits )
	{
		var bytes = new byte[44 + pcm.Length];
		var writer = new System.IO.BinaryWriter( new System.IO.MemoryStream( bytes ) );

		var blockAlign = channels * ( bits >> 3 );
		var format = bits == 32 ? (short)3 : (short)1; //float, or integer samples

		writer.Write( "RIFF"u8 );
		writer.Write( 36 + pcm.Length );
		writer.Write( "WAVE"u8 );
		writer.Write( "fmt "u8 );
		writer.Write( 16 );
		writer.Write( format );
		writer.Write( (short)channels );
		writer.Write( rate );
		writer.Write( (int)( rate * blockAlign ) );
		writer.Write( (short)blockAlign );
		writer.Write( (short)bits );
		writer.Write( "data"u8 );
		writer.Write( pcm.Length );
		writer.Write( pcm );
		writer.Flush();

		return bytes;
	}

	private static string Named( string filename )
	{
		return string.IsNullOrWhiteSpace( filename ) ? null : filename.Replace( '\\', '/' );
	}

	/// <summary>Create a sound from raw PCM data.</summary>
	public static SoundFile FromPcm( string filename, Span<byte> data, PcmOptions? pcmOptions = null )
	{
		ThreadSafe.AssertIsMainThread( "SoundFile.FromPcm" );

		var options = pcmOptions ?? new PcmOptions();

		filename = Named( filename );

		if ( Loaded.TryGetValue( filename, out var sf ) )
			return sf;

		if ( data.Length <= 0 )
			throw new ArgumentException( "Invalid data" );

		//Raw samples mark nothing themselves, so the region is only ever what was asked for: from LoopStart, or the start when only an end was given.
		var region = options.LoopStart >= 0 || options.LoopEnd > 0;
		var looping = options.Loop || region;

		var loopStart = region ? Math.Max( options.LoopStart, 0 ) : 0;
		var loopEnd = region ? Math.Max( options.LoopEnd, 0 ) : 0;

		return Create( filename, WrapAsWav( data, options.Channels, options.Rate, options.Bits ), looping, loopStart, loopEnd );
	}

	/// <summary>Load from WAV.</summary>
	public static SoundFile FromWav( string filename, Span<byte> data, LoadOptions? loadOptions = null )
		=> FromEncoded( filename, data, loadOptions );

	/// <summary>Load from MP3.</summary>
	public static SoundFile FromMp3( string filename, Span<byte> data, LoadOptions? loadOptions = null )
		=> FromEncoded( filename, data, loadOptions );

	/// <summary>Load sound from OGG vorbis.</summary>
	public static SoundFile FromOgg( string filename, Span<byte> data, LoadOptions? loadOptions = null )
		=> FromEncoded( filename, data, loadOptions );

	//A file in whatever format it was written in - the core reads wav, mp3, flac and ogg, and tells them apart itself.
	private static SoundFile FromEncoded( string filename, Span<byte> data, LoadOptions? loadOptions )
	{
		ThreadSafe.AssertIsMainThread( "SoundFile.From" );

		var options = loadOptions ?? new LoadOptions();

		filename = Named( filename );

		if ( Loaded.TryGetValue( filename, out var soundFile ) )
			return soundFile;

		if ( data.Length <= 0 )
			throw new ArgumentException( "Invalid data" );

		//What the file marks - nothing, for anything but a WAV that says so.
		var (declaredStart, declaredEnd) = SoundData.ReadLoop( data );

		bool looping;
		long loopStart = 0, loopEnd = 0;

		if ( options.LoopStart >= 0 || options.LoopEnd > 0 )
		{
			//Whatever was given, and the file's own marks for whatever was not.
			looping = true;
			loopStart = options.LoopStart >= 0 ? options.LoopStart : Math.Max( declaredStart, 0 );
			loopEnd = options.LoopEnd > 0 ? options.LoopEnd : declaredEnd;
		}
		else if ( options.Loop )
		{
			looping = true;
		}
		else
		{
			looping = declaredStart >= 0;
			loopStart = Math.Max( declaredStart, 0 );
			loopEnd = declaredEnd;
		}

		return Create( filename, data, looping, loopStart, loopEnd );
	}

	[Obsolete( $"Use {nameof( FromPcm )}( filename, data, {nameof( PcmOptions )} )" )]
	public static SoundFile FromPcm( string filename, Span<byte> data, int channels, uint rate, int bits, bool loop )
		=> FromPcm( filename, data, new PcmOptions { Channels = channels, Rate = rate, Bits = bits, Loop = loop } );

	[Obsolete( $"Use {nameof( FromPcm )}( filename, data, {nameof( PcmOptions )} )" )]
	public static SoundFile FromPcm( string filename, Span<byte> data, int channels, uint rate, int bits, int loopStart, int loopEnd )
		=> FromPcm( filename, data, new PcmOptions { Channels = channels, Rate = rate, Bits = bits, LoopStart = loopStart, LoopEnd = loopEnd } );

	[Obsolete( $"Use {nameof( FromWav )}( filename, data, {nameof( LoadOptions )} )" )]
	public static SoundFile FromWav( string filename, Span<byte> data, bool loop )
		=> FromWav( filename, data, new LoadOptions { Loop = loop } );

	[Obsolete( $"Use {nameof( FromWav )}( filename, data, {nameof( LoadOptions )} )" )]
	public static SoundFile FromWav( string filename, Span<byte> data, int loopStart, int loopEnd )
		=> FromWav( filename, data, new LoadOptions { LoopStart = loopStart, LoopEnd = loopEnd } );

	[Obsolete( $"Use {nameof( FromMp3 )}( filename, data, {nameof( LoadOptions )} )" )]
	public static SoundFile FromMp3( string filename, Span<byte> data, bool loop )
		=> FromMp3( filename, data, new LoadOptions { Loop = loop } );

	[Obsolete( $"Use {nameof( FromMp3 )}( filename, data, {nameof( LoadOptions )} )" )]
	public static SoundFile FromMp3( string filename, Span<byte> data, int loopStart, int loopEnd )
		=> FromMp3( filename, data, new LoadOptions { LoopStart = loopStart, LoopEnd = loopEnd } );

	/// <summary>The sound is read from disk as it is played, so there is nothing to wait for beyond it being there at all.</summary>
	public Task<bool> LoadAsync() => Task.FromResult( IsValid );

	public void Preload()
	{
		//Loading is what made this, so it is already in memory.
	}

	internal enum CacheStatus
	{
		NotLoaded = 0,
		IsLoaded,
		ErrorLoading,
	};

	/// <summary>Request decompressed audio samples. Multi-channel sounds are downmixed to mono.</summary>
	public Task<short[]> GetSamplesAsync() => GetSamplesAsync( false );

	/// <summary>Request decompressed audio samples. Stereo sounds keep both channels, interleaved (L,R,L,R..). Sounds with any other channel count are downmixed to mono.</summary>
	public Task<short[]> GetSamplesInterleavedAsync() => GetSamplesAsync( true );

	//Reads the whole sound out through a sampler of its own, which is how anything else plays it - so what comes back is what would be heard.
	private Task<short[]> GetSamplesAsync( bool interleaved )
	{
		if ( sound is null )
			return Task.FromResult<short[]>( null );

		var sampler = CoreSounds.Sampler( ResourcePath ?? Name );
		if ( sampler is null )
			return Task.FromResult<short[]>( null );

		try
		{
			return Task.FromResult( sampler.ReadAll( interleaved ) );
		}
		finally
		{
			sampler.Dispose();
		}
	}
}
