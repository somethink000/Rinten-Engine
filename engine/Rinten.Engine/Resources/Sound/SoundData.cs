using System.IO;
using System.Text;

namespace Rinten;

//Raw PCM sound data, kind of like a bitmap but for sounds
internal partial class SoundData
{
	public ushort Format { get; private set; }
	public ushort Channels { get; private set; }
	public uint SampleRate { get; private set; }
	public ushort BitsPerSample { get; private set; }
	public uint SampleCount { get; private set; }
	public float Duration { get; private set; }
	public byte[] PCMData { get; private set; }

	/// <summary>Start sample of the loop region the file declares - see <see cref="ReadLoop"/> - or -1 if it has none.</summary>
	public int LoopStart { get; private set; } = -1;

	/// <summary>End sample of the loop region, or 0 when the loop runs to the end of the sound.</summary>
	public int LoopEnd { get; private set; }

	public static SoundData FromWav( Span<byte> data )
	{
		using var stream = new MemoryStream( data.ToArray() );
		using var reader = new BinaryReader( stream );

		Span<byte> header = stackalloc byte[4];
		if ( stream.Length < 12 || reader.Read( header ) != 4 || !header.SequenceEqual( RIFF ) )
		{
			throw new ArgumentException( "Invalid WAV file format" );
		}
		var riffLength = (long)reader.ReadUInt32() + 8;
		if ( (riffLength != stream.Length && riffLength != stream.Length + 1)
			|| reader.Read( header ) != 4 || !header.SequenceEqual( WAVE ) )
			throw new ArgumentException( "Invalid WAV file format" );

		Span<byte> fmtChunk = default;
		Span<byte> dataChunk = default;

		uint fmtSize = 0;
		uint dataSize = 0;
		uint? factSamples = null;

		while ( stream.Position + 8 <= stream.Length )
		{
			reader.Read( header );
			var chunkSize = reader.ReadUInt32();
			var chunkStart = stream.Position;

			if ( chunkStart + chunkSize > stream.Length )
				throw new ArgumentException( "Invalid chunk size in WAV file" );

			if ( header.SequenceEqual( FMT ) )
			{
				if ( chunkSize < 16 )
					throw new ArgumentException( "Format chunk size too small" );

				fmtChunk = data.Slice( (int)chunkStart, (int)chunkSize );
				fmtSize = chunkSize;
			}
			else if ( header.SequenceEqual( DATA ) )
			{
				dataChunk = data.Slice( (int)chunkStart, (int)chunkSize );
				dataSize = chunkSize;
			}
			else if ( header.SequenceEqual( "fact"u8 ) && chunkSize >= 4 )
			{
				factSamples = BitConverter.ToUInt32( data[(int)chunkStart..] );
			}

			stream.Position = chunkStart + chunkSize;
			if ( chunkSize % 2 != 0 )
				stream.Position++;
		}
		if ( riffLength == stream.Length + 1 && stream.Position != riffLength )
			throw new ArgumentException( "Invalid WAV file format" );

		if ( fmtChunk.IsEmpty )
			throw new ArgumentException( "Missing required FMT chunks" );

		if ( dataChunk.IsEmpty )
			throw new ArgumentException( "Missing required DATA chunks" );

		var channels = BitConverter.ToUInt16( fmtChunk[2..] );
		var format = BitConverter.ToUInt16( fmtChunk );
		var sampleRate = BitConverter.ToUInt32( fmtChunk[4..] );
		var bitsPerSample = BitConverter.ToUInt16( fmtChunk[14..] );
		if ( channels == 0 || sampleRate == 0 )
			throw new ArgumentException( "Invalid WAV channels or sample rate" );
		var (loopStart, loopEnd) = ReadLoop( data );
		if ( format == 2 )
		{
			var decoded = DecodeAdpcm( fmtChunk, dataChunk, factSamples );
			return new SoundData
			{
				Format = 1,
				Channels = channels,
				SampleRate = sampleRate,
				BitsPerSample = 16,
				SampleCount = decoded.Samples,
				Duration = decoded.Samples / (float)sampleRate,
				PCMData = decoded.Data,
				LoopStart = loopStart,
				LoopEnd = loopEnd
			};
		}
		if ( bitsPerSample == 0 || bitsPerSample % 8 != 0 )
			throw new ArgumentException( "Invalid WAV bits per sample" );
		var bytesPerSample = (uint)bitsPerSample / 8;
		var sampleSize = bytesPerSample * channels;

		if ( dataSize % sampleSize != 0 )
			throw new ArgumentException( "Data chunk size is not a multiple of sample size" );

		var sampleCount = dataSize / sampleSize;
		var duration = sampleRate > 0 ? (float)sampleCount / sampleRate : 0.0f;
		var pcmData = dataChunk[..(int)dataSize].ToArray();


		return new SoundData
		{
			Format = format,
			Channels = channels,
			SampleRate = sampleRate,
			BitsPerSample = bitsPerSample,
			SampleCount = sampleCount,
			Duration = duration,
			PCMData = pcmData,
			LoopStart = loopStart,
			LoopEnd = loopEnd
		};
	}

	//Reads a WAV loop region in frames from "smpl" or "cue " chunks; (-1, 0) when none
	internal static (int LoopStart, int LoopEnd) ReadLoop( ReadOnlySpan<byte> data )
	{
		if ( data.Length < 12 || !data[..4].SequenceEqual( RIFF ) || !data.Slice( 8, 4 ).SequenceEqual( WAVE ) )
			return (-1, 0);

		var cueStart = -1;
		var listStart = -1;
		var smplStart = -1;
		var smplSize = 0;

		var at = 12;

		while ( at + 8 <= data.Length )
		{
			var id = data.Slice( at, 4 );
			var size = BitConverter.ToUInt32( data[( at + 4 )..] );

			//A chunk that says it runs past the end is the end of what can be trusted - a truncated file, or a writer that lied about a size.
			if ( size > (uint)( data.Length - at - 8 ) )
				break;

			if ( id.SequenceEqual( SMPL ) && smplStart < 0 )
			{
				smplStart = at;
				smplSize = (int)size;
			}
			else if ( id.SequenceEqual( CUE ) && cueStart < 0 )
			{
				cueStart = at;
			}
			else if ( id.SequenceEqual( LIST ) && cueStart >= 0 && listStart < 0 )
			{
				listStart = at;
			}

			at += 8 + (int)size + (int)( size & 1 );
		}

		//The sampler chunk: 36 bytes of header, the loop count at +28 of its payload, then 24 bytes a loop - id, type, start, end, fraction, count.
		if ( smplStart >= 0 && smplSize >= 36 + 24 && BitConverter.ToInt32( data[( smplStart + 8 + 28 )..] ) > 0 )
		{
			var loop = smplStart + 8 + 36;
			var start = BitConverter.ToInt32( data[( loop + 8 )..] );
			var end = BitConverter.ToInt32( data[( loop + 12 )..] );

			if ( start >= 0 && end >= start )
				return (start, end + 1);
		}

		if ( cueStart < 0 || cueStart + 36 > data.Length )
			return (-1, 0);

		var loopStart = BitConverter.ToInt32( data[( cueStart + 32 )..] );
		if ( loopStart < 0 )
			return (-1, 0);

		if ( listStart < 0 || listStart + 32 > data.Length || !data.Slice( listStart + 28, 4 ).SequenceEqual( MARK ) )
			return (loopStart, 0);

		return (loopStart, loopStart + BitConverter.ToInt32( data[( listStart + 24 )..] ));
	}

	private static readonly byte[] RIFF = Encoding.ASCII.GetBytes( "RIFF" );
	private static readonly byte[] WAVE = Encoding.ASCII.GetBytes( "WAVE" );
	private static readonly byte[] FMT = Encoding.ASCII.GetBytes( "fmt " );
	private static readonly byte[] DATA = Encoding.ASCII.GetBytes( "data" );
	private static readonly byte[] CUE = Encoding.ASCII.GetBytes( "cue " );
	private static readonly byte[] LIST = Encoding.ASCII.GetBytes( "LIST" );
	private static readonly byte[] MARK = Encoding.ASCII.GetBytes( "mark" );
	private static readonly byte[] SMPL = Encoding.ASCII.GetBytes( "smpl" );
}
