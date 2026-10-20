using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Rinten;

//Hand-loaded LGPL FFmpeg libraries and the functions we call, in dependency order
//Struct offsets match one major version; other majors are refused. Update all together
internal static unsafe class FFmpeg
{
	//The major versions Offsets was taken from: FFmpeg 9.0.
	internal const int AvUtilMajor = 61;
	internal const int AvCodecMajor = 63;
	internal const int AvFormatMajor = 63;
	internal const int SwScaleMajor = 10;
	internal const int SwResampleMajor = 7;

	static readonly Logger log = new( "Video" );

	static readonly object gate = new();
	static bool opened;
	static string unavailable;
	static bool saidUnavailable;

	//Whether video can be played and written in this process. Asked once: nothing it depends on changes while the engine runs.
	internal static bool IsAvailable
	{
		get
		{
			Open();
			return unavailable is null;
		}
	}

	//Why there is no video, in words, or null where there is.
	internal static string Unavailable
	{
		get
		{
			Open();
			return unavailable;
		}
	}

	//Says once why a video does nothing, the first time one would have.
	internal static void SayUnavailable()
	{
		if ( saidUnavailable || unavailable is null ) return;
		saidUnavailable = true;

		log.Info( $"video does nothing here: {unavailable}" );
	}

	//Where the libraries are looked for: beside the engine's other natives, in a folder of their own.
	internal static string Folder => Path.GetFullPath( Path.Combine( NetCore.NativeDllPath, "ffmpeg" ) );

	//The files, in the order they have to be opened - each one needs those before it already loaded.
	static (string Name, string File)[] Files()
	{
		if ( OperatingSystem.IsWindows() )
		{
			return
			[
				("avutil", $"avutil-{AvUtilMajor}.dll"),
				("swresample", $"swresample-{SwResampleMajor}.dll"),
				("swscale", $"swscale-{SwScaleMajor}.dll"),
				("avcodec", $"avcodec-{AvCodecMajor}.dll"),
				("avformat", $"avformat-{AvFormatMajor}.dll"),
			];
		}

		if ( OperatingSystem.IsMacOS() )
		{
			return
			[
				("avutil", $"libavutil.{AvUtilMajor}.dylib"),
				("swresample", $"libswresample.{SwResampleMajor}.dylib"),
				("swscale", $"libswscale.{SwScaleMajor}.dylib"),
				("avcodec", $"libavcodec.{AvCodecMajor}.dylib"),
				("avformat", $"libavformat.{AvFormatMajor}.dylib"),
			];
		}

		//In the package's own library folder, by the plain names a package carries
		if ( OperatingSystem.IsAndroid() )
		{
			return
			[
				("avutil", "libavutil.so"),
				("swresample", "libswresample.so"),
				("swscale", "libswscale.so"),
				("avcodec", "libavcodec.so"),
				("avformat", "libavformat.so"),
			];
		}

		return
		[
			("avutil", $"libavutil.so.{AvUtilMajor}"),
			("swresample", $"libswresample.so.{SwResampleMajor}"),
			("swscale", $"libswscale.so.{SwScaleMajor}"),
			("avcodec", $"libavcodec.so.{AvCodecMajor}"),
			("avformat", $"libavformat.so.{AvFormatMajor}"),
		];
	}

	//The folders the libraries may be in, best first: the platform's own under the working directory, which is the game folder once the engine has
	//started, and then the same folder found up the tree from the engine's assembly - a host that never starts the engine runs from somewhere else.
	static IEnumerable<string> Folders()
	{
		yield return Folder;

		var directory = Path.GetDirectoryName( typeof( FFmpeg ).Assembly.Location );

		while ( !string.IsNullOrEmpty( directory ) )
		{
			yield return Path.Combine( directory, "game", NetCore.NativeDllPath, "ffmpeg" );
			yield return Path.Combine( directory, NetCore.NativeDllPath, "ffmpeg" );

			directory = Path.GetDirectoryName( directory );
		}
	}

	static void Open()
	{
		if ( Volatile.Read( ref opened ) ) return;

		lock ( gate )
		{
			if ( opened ) return;

			try
			{
				unavailable = Load();
			}
			catch ( Exception e )
			{
				unavailable = $"FFmpeg would not load: {e.Message}";
			}

			Volatile.Write( ref opened, true );
		}
	}

	static string Load()
	{
		//A page in a browser has the browser's video and not ours; Android's is built by build-android-ffmpeg.sh
		if ( OperatingSystem.IsBrowser() ) return "a game in a browser has no FFmpeg";
		if ( OperatingSystem.IsIOS() ) return "there is no FFmpeg for an iPhone";

		var files = Files();
		var handles = new Dictionary<string, nint>();

		string found = null;

		foreach ( var folder in Folders() )
		{
			if ( File.Exists( Path.Combine( folder, files[^1].File ) ) )
			{
				found = folder;
				break;
			}
		}

		foreach ( var (name, file) in files )
		{
			nint handle = 0;

			if ( found is not null && NativeLibrary.TryLoad( Path.Combine( found, file ), out var local ) ) handle = local;
			else if ( NativeLibrary.TryLoad( file, out var system ) ) handle = system;

			if ( handle == 0 )
			{
				if ( OperatingSystem.IsAndroid() ) return $"the package has no {file}: its export carries FFmpeg only for a game that uses video";

				return found is null
					? $"{Folder} has no {file} - fetch it with: dotnet run --project engine/Tools/RintenBuild -- fetch-natives"
					: $"{Path.Combine( found, file )} would not load";
			}

			handles[name] = handle;
		}

		var avutil = handles["avutil"];
		var swresample = handles["swresample"];
		var swscale = handles["swscale"];
		var avcodec = handles["avcodec"];
		var avformat = handles["avformat"];

		//The versions before anything else: the offsets below are those of one release's structures, and reading another's with them is reading nonsense.
		var versions = new (string Name, nint Library, string Function, int Major)[]
		{
			("libavutil", avutil, "avutil_version", AvUtilMajor),
			("libswresample", swresample, "swresample_version", SwResampleMajor),
			("libswscale", swscale, "swscale_version", SwScaleMajor),
			("libavcodec", avcodec, "avcodec_version", AvCodecMajor),
			("libavformat", avformat, "avformat_version", AvFormatMajor),
		};

		foreach ( var (name, library, function, major) in versions )
		{
			if ( !NativeLibrary.TryGetExport( library, function, out var address ) )
				return $"{name} has no {function}";

			var version = ( (delegate* unmanaged[Cdecl]<uint>)address )();
			var got = (int)( version >> 16 );

			if ( got != major )
				return $"{name} is version {got}.{( version >> 8 ) & 0xff}.{version & 0xff}, and this engine reads version {major} only";
		}

		nint Get( nint library, string function ) => NativeLibrary.GetExport( library, function );

		av_log_set_level = (delegate* unmanaged[Cdecl]<int, void>)Get( avutil, "av_log_set_level" );
		av_frame_alloc = (delegate* unmanaged[Cdecl]<void*>)Get( avutil, "av_frame_alloc" );
		av_frame_free = (delegate* unmanaged[Cdecl]<void**, void>)Get( avutil, "av_frame_free" );
		av_frame_unref = (delegate* unmanaged[Cdecl]<void*, void>)Get( avutil, "av_frame_unref" );
		av_frame_get_buffer = (delegate* unmanaged[Cdecl]<void*, int, int>)Get( avutil, "av_frame_get_buffer" );
		av_frame_make_writable = (delegate* unmanaged[Cdecl]<void*, int>)Get( avutil, "av_frame_make_writable" );
		av_dict_get = (delegate* unmanaged[Cdecl]<void*, byte*, void*, int, void*>)Get( avutil, "av_dict_get" );
		av_dict_set = (delegate* unmanaged[Cdecl]<void**, byte*, byte*, int, int>)Get( avutil, "av_dict_set" );
		av_dict_free = (delegate* unmanaged[Cdecl]<void**, void>)Get( avutil, "av_dict_free" );
		av_strerror = (delegate* unmanaged[Cdecl]<int, byte*, nuint, int>)Get( avutil, "av_strerror" );
		av_channel_layout_default = (delegate* unmanaged[Cdecl]<void*, int, void>)Get( avutil, "av_channel_layout_default" );
		av_channel_layout_uninit = (delegate* unmanaged[Cdecl]<void*, void>)Get( avutil, "av_channel_layout_uninit" );
		av_channel_layout_copy = (delegate* unmanaged[Cdecl]<void*, void*, int>)Get( avutil, "av_channel_layout_copy" );
		av_malloc = (delegate* unmanaged[Cdecl]<nuint, void*>)Get( avutil, "av_malloc" );
		av_freep = (delegate* unmanaged[Cdecl]<void*, void>)Get( avutil, "av_freep" );
		av_opt_set = (delegate* unmanaged[Cdecl]<void*, byte*, byte*, int, int>)Get( avutil, "av_opt_set" );

		swr_alloc_set_opts2 = (delegate* unmanaged[Cdecl]<void**, void*, int, int, void*, int, int, int, void*, int>)Get( swresample, "swr_alloc_set_opts2" );
		swr_init = (delegate* unmanaged[Cdecl]<void*, int>)Get( swresample, "swr_init" );
		swr_convert = (delegate* unmanaged[Cdecl]<void*, byte**, int, byte**, int, int>)Get( swresample, "swr_convert" );
		swr_get_out_samples = (delegate* unmanaged[Cdecl]<void*, int, int>)Get( swresample, "swr_get_out_samples" );
		swr_free = (delegate* unmanaged[Cdecl]<void**, void>)Get( swresample, "swr_free" );

		sws_alloc_context = (delegate* unmanaged[Cdecl]<void*>)Get( swscale, "sws_alloc_context" );
		sws_free_context = (delegate* unmanaged[Cdecl]<void**, void>)Get( swscale, "sws_free_context" );
		sws_scale_frame = (delegate* unmanaged[Cdecl]<void*, void*, void*, int>)Get( swscale, "sws_scale_frame" );

		avcodec_find_decoder = (delegate* unmanaged[Cdecl]<int, void*>)Get( avcodec, "avcodec_find_decoder" );
		avcodec_find_encoder_by_name = (delegate* unmanaged[Cdecl]<byte*, void*>)Get( avcodec, "avcodec_find_encoder_by_name" );
		avcodec_alloc_context3 = (delegate* unmanaged[Cdecl]<void*, void*>)Get( avcodec, "avcodec_alloc_context3" );
		avcodec_free_context = (delegate* unmanaged[Cdecl]<void**, void>)Get( avcodec, "avcodec_free_context" );
		avcodec_parameters_to_context = (delegate* unmanaged[Cdecl]<void*, void*, int>)Get( avcodec, "avcodec_parameters_to_context" );
		avcodec_parameters_from_context = (delegate* unmanaged[Cdecl]<void*, void*, int>)Get( avcodec, "avcodec_parameters_from_context" );
		avcodec_open2 = (delegate* unmanaged[Cdecl]<void*, void*, void**, int>)Get( avcodec, "avcodec_open2" );
		avcodec_send_packet = (delegate* unmanaged[Cdecl]<void*, void*, int>)Get( avcodec, "avcodec_send_packet" );
		avcodec_receive_frame = (delegate* unmanaged[Cdecl]<void*, void*, int>)Get( avcodec, "avcodec_receive_frame" );
		avcodec_send_frame = (delegate* unmanaged[Cdecl]<void*, void*, int>)Get( avcodec, "avcodec_send_frame" );
		avcodec_receive_packet = (delegate* unmanaged[Cdecl]<void*, void*, int>)Get( avcodec, "avcodec_receive_packet" );
		avcodec_flush_buffers = (delegate* unmanaged[Cdecl]<void*, void>)Get( avcodec, "avcodec_flush_buffers" );
		avcodec_get_supported_config = (delegate* unmanaged[Cdecl]<void*, void*, int, uint, void**, int*, int>)Get( avcodec, "avcodec_get_supported_config" );
		av_packet_alloc = (delegate* unmanaged[Cdecl]<void*>)Get( avcodec, "av_packet_alloc" );
		av_packet_free = (delegate* unmanaged[Cdecl]<void**, void>)Get( avcodec, "av_packet_free" );
		av_packet_unref = (delegate* unmanaged[Cdecl]<void*, void>)Get( avcodec, "av_packet_unref" );

		avformat_network_init = (delegate* unmanaged[Cdecl]<int>)Get( avformat, "avformat_network_init" );
		avformat_alloc_context = (delegate* unmanaged[Cdecl]<void*>)Get( avformat, "avformat_alloc_context" );
		avformat_open_input = (delegate* unmanaged[Cdecl]<void**, byte*, void*, void**, int>)Get( avformat, "avformat_open_input" );
		avformat_find_stream_info = (delegate* unmanaged[Cdecl]<void*, void**, int>)Get( avformat, "avformat_find_stream_info" );
		avformat_close_input = (delegate* unmanaged[Cdecl]<void**, void>)Get( avformat, "avformat_close_input" );
		av_read_frame = (delegate* unmanaged[Cdecl]<void*, void*, int>)Get( avformat, "av_read_frame" );
		av_seek_frame = (delegate* unmanaged[Cdecl]<void*, int, long, int, int>)Get( avformat, "av_seek_frame" );
		av_find_best_stream = (delegate* unmanaged[Cdecl]<void*, int, int, int, void**, int, int>)Get( avformat, "av_find_best_stream" );
		avio_alloc_context = (delegate* unmanaged[Cdecl]<byte*, int, int, void*, nint, nint, nint, void*>)Get( avformat, "avio_alloc_context" );
		avio_context_free = (delegate* unmanaged[Cdecl]<void**, void>)Get( avformat, "avio_context_free" );
		avformat_alloc_output_context2 = (delegate* unmanaged[Cdecl]<void**, void*, byte*, byte*, int>)Get( avformat, "avformat_alloc_output_context2" );
		avformat_new_stream = (delegate* unmanaged[Cdecl]<void*, void*, void*>)Get( avformat, "avformat_new_stream" );
		avformat_write_header = (delegate* unmanaged[Cdecl]<void*, void**, int>)Get( avformat, "avformat_write_header" );
		av_interleaved_write_frame = (delegate* unmanaged[Cdecl]<void*, void*, int>)Get( avformat, "av_interleaved_write_frame" );
		av_write_trailer = (delegate* unmanaged[Cdecl]<void*, int>)Get( avformat, "av_write_trailer" );
		avformat_free_context = (delegate* unmanaged[Cdecl]<void*, void>)Get( avformat, "avformat_free_context" );
		avio_open = (delegate* unmanaged[Cdecl]<void**, byte*, int, int>)Get( avformat, "avio_open" );
		avio_closep = (delegate* unmanaged[Cdecl]<void**, int>)Get( avformat, "avio_closep" );

		//Its own complaints go to the process's standard error, where nobody reading the engine's log looks -
		//so only the ones that are errors, and every failure that matters is said again here with its reason.
		av_log_set_level( AV_LOG_ERROR );
		avformat_network_init();

		return null;
	}

	internal static delegate* unmanaged[Cdecl]<int, void> av_log_set_level;
	internal static delegate* unmanaged[Cdecl]<void*> av_frame_alloc;
	internal static delegate* unmanaged[Cdecl]<void**, void> av_frame_free;
	internal static delegate* unmanaged[Cdecl]<void*, void> av_frame_unref;
	internal static delegate* unmanaged[Cdecl]<void*, int, int> av_frame_get_buffer;
	internal static delegate* unmanaged[Cdecl]<void*, int> av_frame_make_writable;
	internal static delegate* unmanaged[Cdecl]<void*, byte*, void*, int, void*> av_dict_get;
	internal static delegate* unmanaged[Cdecl]<void**, byte*, byte*, int, int> av_dict_set;
	internal static delegate* unmanaged[Cdecl]<void**, void> av_dict_free;
	internal static delegate* unmanaged[Cdecl]<int, byte*, nuint, int> av_strerror;
	internal static delegate* unmanaged[Cdecl]<void*, int, void> av_channel_layout_default;
	internal static delegate* unmanaged[Cdecl]<void*, void> av_channel_layout_uninit;
	internal static delegate* unmanaged[Cdecl]<void*, void*, int> av_channel_layout_copy;
	internal static delegate* unmanaged[Cdecl]<nuint, void*> av_malloc;
	internal static delegate* unmanaged[Cdecl]<void*, void> av_freep;
	internal static delegate* unmanaged[Cdecl]<void*, byte*, byte*, int, int> av_opt_set;

	internal static delegate* unmanaged[Cdecl]<void**, void*, int, int, void*, int, int, int, void*, int> swr_alloc_set_opts2;
	internal static delegate* unmanaged[Cdecl]<void*, int> swr_init;
	internal static delegate* unmanaged[Cdecl]<void*, byte**, int, byte**, int, int> swr_convert;
	internal static delegate* unmanaged[Cdecl]<void*, int, int> swr_get_out_samples;
	internal static delegate* unmanaged[Cdecl]<void**, void> swr_free;

	internal static delegate* unmanaged[Cdecl]<void*> sws_alloc_context;
	internal static delegate* unmanaged[Cdecl]<void**, void> sws_free_context;
	internal static delegate* unmanaged[Cdecl]<void*, void*, void*, int> sws_scale_frame;

	internal static delegate* unmanaged[Cdecl]<int, void*> avcodec_find_decoder;
	internal static delegate* unmanaged[Cdecl]<byte*, void*> avcodec_find_encoder_by_name;
	internal static delegate* unmanaged[Cdecl]<void*, void*> avcodec_alloc_context3;
	internal static delegate* unmanaged[Cdecl]<void**, void> avcodec_free_context;
	internal static delegate* unmanaged[Cdecl]<void*, void*, int> avcodec_parameters_to_context;
	internal static delegate* unmanaged[Cdecl]<void*, void*, int> avcodec_parameters_from_context;
	internal static delegate* unmanaged[Cdecl]<void*, void*, void**, int> avcodec_open2;
	internal static delegate* unmanaged[Cdecl]<void*, void*, int> avcodec_send_packet;
	internal static delegate* unmanaged[Cdecl]<void*, void*, int> avcodec_receive_frame;
	internal static delegate* unmanaged[Cdecl]<void*, void*, int> avcodec_send_frame;
	internal static delegate* unmanaged[Cdecl]<void*, void*, int> avcodec_receive_packet;
	internal static delegate* unmanaged[Cdecl]<void*, void> avcodec_flush_buffers;
	internal static delegate* unmanaged[Cdecl]<void*, void*, int, uint, void**, int*, int> avcodec_get_supported_config;
	internal static delegate* unmanaged[Cdecl]<void*> av_packet_alloc;
	internal static delegate* unmanaged[Cdecl]<void**, void> av_packet_free;
	internal static delegate* unmanaged[Cdecl]<void*, void> av_packet_unref;

	internal static delegate* unmanaged[Cdecl]<int> avformat_network_init;
	internal static delegate* unmanaged[Cdecl]<void*> avformat_alloc_context;
	internal static delegate* unmanaged[Cdecl]<void**, byte*, void*, void**, int> avformat_open_input;
	internal static delegate* unmanaged[Cdecl]<void*, void**, int> avformat_find_stream_info;
	internal static delegate* unmanaged[Cdecl]<void**, void> avformat_close_input;
	internal static delegate* unmanaged[Cdecl]<void*, void*, int> av_read_frame;
	internal static delegate* unmanaged[Cdecl]<void*, int, long, int, int> av_seek_frame;
	internal static delegate* unmanaged[Cdecl]<void*, int, int, int, void**, int, int> av_find_best_stream;
	internal static delegate* unmanaged[Cdecl]<byte*, int, int, void*, nint, nint, nint, void*> avio_alloc_context;
	internal static delegate* unmanaged[Cdecl]<void**, void> avio_context_free;
	internal static delegate* unmanaged[Cdecl]<void**, void*, byte*, byte*, int> avformat_alloc_output_context2;
	internal static delegate* unmanaged[Cdecl]<void*, void*, void*> avformat_new_stream;
	internal static delegate* unmanaged[Cdecl]<void*, void**, int> avformat_write_header;
	internal static delegate* unmanaged[Cdecl]<void*, void*, int> av_interleaved_write_frame;
	internal static delegate* unmanaged[Cdecl]<void*, int> av_write_trailer;
	internal static delegate* unmanaged[Cdecl]<void*, void> avformat_free_context;
	internal static delegate* unmanaged[Cdecl]<void**, byte*, int, int> avio_open;
	internal static delegate* unmanaged[Cdecl]<void**, int> avio_closep;

	internal const int AV_LOG_ERROR = 16;

	internal const int AVMEDIA_TYPE_VIDEO = 0;
	internal const int AVMEDIA_TYPE_AUDIO = 1;

	internal const int AV_PIX_FMT_YUV420P = 0;
	internal const int AV_PIX_FMT_RGBA = 26;
	internal const int AV_PIX_FMT_BGRA = 28;
	internal const int AV_PIX_FMT_YUVA420P = 33;

	internal const int AV_SAMPLE_FMT_S16 = 1;
	internal const int AV_SAMPLE_FMT_FLT = 3;
	internal const int AV_SAMPLE_FMT_FLTP = 8;

	internal const int AVERROR_EOF = -541478725;
	internal const int AVERROR_EAGAIN = -11;
	internal const int AVERROR_EINVAL = -22;
	internal const long AV_NOPTS_VALUE = long.MinValue;

	internal const int AVSEEK_FLAG_BACKWARD = 1;
	internal const int AVSEEK_SIZE = 0x10000;
	internal const int AVIO_FLAG_WRITE = 2;
	internal const int AVFMT_NOFILE = 1;
	internal const int AVFMT_GLOBALHEADER = 64;
	internal const int AV_CODEC_FLAG_GLOBAL_HEADER = 1 << 22;
	internal const int AV_CODEC_CONFIG_PIX_FORMAT = 0;
	internal const int AV_CODEC_CONFIG_SAMPLE_FORMAT = 3;
	internal const int AV_OPT_SEARCH_CHILDREN = 1;
	internal const int FF_COMPLIANCE_EXPERIMENTAL = -2;

	internal const int SWS_BILINEAR = 2;

	internal const int AVCOL_RANGE_MPEG = 1;
	internal const int AVCOL_BT709 = 1;

	//Where the fields this engine reads and writes are inside FFmpeg 9's structures, taken with offsetof against the headers of the fetched
	//release. The same on every 64-bit platform it is fetched for: no field read here is a long , the one type whose size Windows disagrees on.
	internal static class Offsets
	{
		internal const int FormatOutputFormat = 16;
		internal const int FormatPb = 32;
		internal const int FormatStreamCount = 44;
		internal const int FormatStreams = 48;
		internal const int FormatStartTime = 96;
		internal const int FormatDuration = 104;
		internal const int FormatFlags = 128;
		internal const int FormatMetadata = 192;
		internal const int FormatInterruptCallback = 216;

		internal const int StreamIndex = 8;
		internal const int StreamCodecParameters = 16;
		internal const int StreamTimeBase = 32;
		internal const int StreamStartTime = 40;
		internal const int StreamDuration = 48;
		internal const int StreamMetadata = 80;
		internal const int StreamAverageFrameRate = 88;

		internal const int ParametersCodecType = 0;
		internal const int ParametersWidth = 72;
		internal const int ParametersHeight = 76;
		internal const int ParametersChannelLayout = 128;
		internal const int ParametersSampleRate = 152;

		internal const int ChannelLayoutChannels = 4;
		internal const int ChannelLayoutSize = 24;

		internal const int FrameData = 0;
		internal const int FrameLineSize = 64;
		internal const int FrameExtendedData = 96;
		internal const int FrameWidth = 104;
		internal const int FrameHeight = 108;
		internal const int FrameSampleCount = 112;
		internal const int FrameFormat = 116;
		internal const int FramePts = 136;
		internal const int FrameSampleRate = 180;
		internal const int FrameColorRange = 280;
		internal const int FrameColorPrimaries = 284;
		internal const int FrameColorTransfer = 288;
		internal const int FrameColorSpace = 292;
		internal const int FrameBestEffortTimestamp = 304;
		internal const int FrameChannelLayout = 384;
		internal const int FrameDuration = 408;

		internal const int PacketPts = 8;
		internal const int PacketDts = 16;
		internal const int PacketStreamIndex = 36;
		internal const int PacketDuration = 64;

		internal const int CodecBitRate = 56;
		internal const int CodecFlags = 64;
		internal const int CodecTimeBase = 84;
		internal const int CodecFrameRate = 100;
		internal const int CodecWidth = 112;
		internal const int CodecHeight = 116;
		internal const int CodecPixelFormat = 136;
		internal const int CodecColorPrimaries = 144;
		internal const int CodecColorTransfer = 148;
		internal const int CodecColorSpace = 152;
		internal const int CodecColorRange = 156;
		internal const int CodecGopSize = 332;
		internal const int CodecSampleRate = 344;
		internal const int CodecSampleFormat = 348;
		internal const int CodecChannelLayout = 352;
		internal const int CodecFrameSize = 376;
		internal const int CodecStrictCompliance = 516;
		internal const int CodecThreadCount = 656;

		internal const int OutputFormatFlags = 44;

		internal const int DictionaryEntryValue = 8;

		internal const int IoBuffer = 8;

		internal const int ScaleFlags = 16;
		internal const int ScaleThreads = 40;
	}

	internal static int Int( void* at, int offset ) => *(int*)( (byte*)at + offset );
	internal static long Long( void* at, int offset ) => *(long*)( (byte*)at + offset );
	internal static void* Pointer( void* at, int offset ) => *(void**)( (byte*)at + offset );
	internal static void SetInt( void* at, int offset, int value ) => *(int*)( (byte*)at + offset ) = value;
	internal static void SetLong( void* at, int offset, long value ) => *(long*)( (byte*)at + offset ) = value;
	internal static void SetPointer( void* at, int offset, void* value ) => *(void**)( (byte*)at + offset ) = value;

	//A rational of FFmpeg's - two ints, numerator first - as a number.
	internal static double Rational( void* at, int offset )
	{
		var num = Int( at, offset );
		var den = Int( at, offset + 4 );

		return den == 0 ? 0.0 : (double)num / den;
	}

	internal static void SetRational( void* at, int offset, int num, int den )
	{
		SetInt( at, offset, num );
		SetInt( at, offset + 4, den );
	}

	//a * b / c rounded to the nearest, without the overflow the plain product has - what
	//av_rescale does, here rather than through a call that takes its rationals by value.
	internal static long Rescale( long a, long b, long c )
	{
		if ( c == 0 ) return 0;

		var product = (Int128)a * b;
		var half = (Int128)( Math.Abs( c ) / 2 );

		return (long)( product >= 0 ? ( product + half ) / c : ( product - half ) / c );
	}

	//An FFmpeg error code as the words it stands for.
	internal static string Error( int code )
	{
		var buffer = stackalloc byte[256];

		if ( av_strerror is null || av_strerror( code, buffer, 256 ) < 0 ) return $"error {code}";

		return Marshal.PtrToStringUTF8( (nint)buffer ) ?? $"error {code}";
	}

	//A string as FFmpeg takes one: UTF-8, ended with a nought.
	internal static byte[] Text( string value ) => Encoding.UTF8.GetBytes( ( value ?? "" ) + "\0" );

	//Sets one of a codec's or a context's options by name, as text; false where it has no such option.
	internal static bool SetOption( void* target, string name, string value )
	{
		fixed ( byte* key = Text( name ) )
		fixed ( byte* text = Text( value ) )
			return av_opt_set( target, key, text, AV_OPT_SEARCH_CHILDREN ) >= 0;
	}

	//The value an FFmpeg dictionary holds for a key, matched without regard to case, or null.
	internal static string DictionaryValue( void* dictionary, string key )
	{
		if ( dictionary is null || string.IsNullOrEmpty( key ) ) return null;

		fixed ( byte* name = Text( key ) )
		{
			var entry = av_dict_get( dictionary, name, null, 0 );
			if ( entry is null ) return null;

			return Marshal.PtrToStringUTF8( (nint)Pointer( entry, Offsets.DictionaryEntryValue ) );
		}
	}
}
