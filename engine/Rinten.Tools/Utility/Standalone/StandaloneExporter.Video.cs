using System;
using System.IO;

namespace Editor;

//FFmpeg, in an export: bin/<platform>/ffmpeg, only for a game with video in its code or its files; the LGPL licence goes with it
partial class StandaloneExporter
{
	//The types whose use means a game has video.
	private static readonly (string Namespace, string Name)[] VideoTypes =
	[
		("Rinten", "VideoPlayer"),
		("Rinten", "VideoWriter"),
	];

	//What a file a game ships is called when it is a video - what a texture of it is played from.
	private static bool IsVideoFile( string name )
	{
		if ( string.IsNullOrEmpty( name ) ) return false;

		return Rinten.TextureLoader.VideoTextureLoader.Extensions.Contains( Path.GetExtension( name ) );
	}

	private bool GameUsesVideo()
	{
		if ( GameReferencesAny( VideoTypes ) ) return true;

		return PackageManifest?.Assets?.Any( asset => IsVideoFile( asset.Name ) ) ?? false;
	}

	//FFmpeg's files for the platform being exported for, where the game has video. Empty
	//otherwise, and empty - with the reason said - where they are not there to carry.
	private IEnumerable<string> GetVideoFiles( string engineDir )
	{
		var platform = _exportConfig.Platform ?? ExportPlatform.Host;

		//A page has the browser's video
		if ( platform.IsWeb ) return [];

		if ( !HasVideoCodecs( engineDir, platform, out var folder ) ) return [];

		Logger.Info( "The game has video: FFmpeg goes with it." );

		//A phone's libraries go in the package itself - see AndroidVideo - and only the licence with the game's files
		if ( platform.IsAndroid ) return Directory.EnumerateFiles( folder, "LICENSE*", SearchOption.TopDirectoryOnly );

		return Directory.EnumerateFiles( folder, "*", SearchOption.TopDirectoryOnly );
	}

	//Whether the game has video and FFmpeg is there to carry for the platform; says why not where the game has video.
	private bool HasVideoCodecs( string engineDir, ExportPlatform platform, out string folder )
	{
		folder = Path.Combine( engineDir, platform.BinFolder, "ffmpeg" );

		if ( !GameUsesVideo() ) return false;

		var codecs = Directory.Exists( folder ) &&
			Directory.EnumerateFiles( folder ).Any( file => Path.GetFileName( file ).Contains( "avcodec", StringComparison.OrdinalIgnoreCase ) );

		if ( codecs ) return true;

		Problem( $"the game has video, but there is no FFmpeg for {platform.Title} in {folder} - " +
			"its videos will play nothing. " + ( platform.IsAndroid
				? "Build it with: engine/Core/build-android-ffmpeg.sh"
				: $"Fetch it with: dotnet run --project engine/Tools/RintenBuild -- fetch-natives --platform {platform.Id}" ) );

		return false;
	}
}
