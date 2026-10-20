using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Editor;

//Android build: zips the export into the launcher's payload, builds the apk, copies it out by name
partial class StandaloneExporter
{
	/// <summary>Where the build ended up - the folder for the platforms that are one, and the apk for the one that is not.</summary>
	public string Result { get; private set; }

	//Packages the exported content into an apk beside the build.
	private async Task PackageAndroid()
	{
		var engineDir = Environment.CurrentDirectory;

		var project = Path.GetFullPath( Path.Combine( engineDir,
			"..", "engine", "Launcher", "RintenAndroid", "Rinten-Android.csproj" ) );

		if ( !File.Exists( project ) )
		{
			Problem( $"no apk: {project} is not there. " +
				"The content is written, but an engine without its source cannot package it." );
			return;
		}

		//The core, which is what the apk is for. Without it there is a package that installs and stops at its first frame, which is worse than not writing one.
		var core = Path.Combine( engineDir, ExportPlatform.Android.BinFolder, "rintencore.so" );

		if ( !File.Exists( core ) )
		{
			Problem( $"no apk: {core} is not there. " +
				"Build the core for Android first (ANDROID_NDK_HOME=.. engine/Core/build-android.sh)." );
			return;
		}

		var name = string.IsNullOrWhiteSpace( _exportConfig.ExecutableName ) ? "game" : _exportConfig.ExecutableName;

		//Beside the build rather than inside it: what is inside is what gets zipped into the next one.
		var parent = Path.GetDirectoryName( Path.GetFullPath( _exportConfig.TargetDir ) ) ?? _exportConfig.TargetDir;
		var apk = Path.Combine( parent, ExportPlatform.Android.Executable( name ) );

		//Kept out of the project, because it is the same bytes as the folder beside it and nobody wants a second copy of the game in their build.
		var work = Path.Combine( engineDir, "launchers", ExportPlatform.Android.Id, Ident() );
		var payload = Path.Combine( work, "payload.zip" );

		Directory.CreateDirectory( work );

		Say( "Packing the game into the package", 0.8f );

		await Task.Run( () =>
		{
			if ( File.Exists( payload ) ) File.Delete( payload );

			ZipFile.CreateFromDirectory( _exportConfig.TargetDir, payload,
				CompressionLevel.Optimal, includeBaseDirectory: false );
		} );

		Say( "Building the package", 0.9f );

		var built = await Task.Run( () => BuildApk( project, payload, work ) );

		if ( built is null ) return;

		if ( File.Exists( apk ) ) File.Delete( apk );

		File.Copy( built, apk );

		Result = apk;

		Logger.Info( $"Packaged {apk}" );

		//Remove the working folder once the apk exists; kept if the apk failed to build
		Tidy( work, payload );
	}

	//Deletes the intermediate copies (content folder, payload zip, SDK output) after the apk is built
	private void Tidy( string work, string payload )
	{
		void Drop( string path, bool directory )
		{
			if ( string.IsNullOrEmpty( path ) ) return;

			try
			{
				if ( directory && Directory.Exists( path ) ) Directory.Delete( path, true );
				else if ( !directory && File.Exists( path ) ) File.Delete( path );
			}
			catch ( Exception e )
			{
				//Not a failure of the build: the apk is written and the worst that is left is a folder somebody can delete.
				Logger.Warning( $"could not clear {path} after the build: {e.Message}" );
			}
		}

		Drop( _exportConfig.TargetDir, directory: true );
		Drop( payload, directory: false );
		Drop( work is null ? null : Path.Combine( work, "package" ), directory: true );
	}

	//Whether the project is played in a headset - Project Settings' control modes, VR only - and so is packaged for one: see AndroidManifest.Vr.xml.
	private bool IsVr()
		=> _exportConfig.Project.Config.TryGetMeta( "ControlModes", out ControlModeSettings modes ) && modes.IsVROnly;

	//Whether FFmpeg's libraries go in the package, for a game with video; GetVideoFiles says where they are missing.
	private bool AndroidVideo()
		=> GameUsesVideo() &&
			File.Exists( Path.Combine( Environment.CurrentDirectory, ExportPlatform.Android.BinFolder, "ffmpeg", "libavcodec.so" ) );

	//Whether the build page lets the game ask for the microphone (Build.Microphone), which puts RECORD_AUDIO in its manifest.
	private bool Microphone()
		=> _exportConfig.Project?.Config?.GetMetaOrDefault( "Build.Microphone", false ) ?? false;

	//Asks the SDK for the package, and says which file it wrote.
	private string BuildApk( string project, string payload, string into )
	{
		var output = Path.Combine( into, "package" );

		//A fresh folder each time: the apk is found by looking in here, and one left over from a build that has since failed is not this build's.
		if ( Directory.Exists( output ) ) Directory.Delete( output, true );

		var arguments = $"build \"{project}\" -c Release -f net10.0-android " +
			$"-p:RintenPayloadZip=\"{payload}\" " +
			$"-p:ApplicationId={ApplicationId()} " +
			//Each its own property - see the csproj, which makes the manifest's placeholders out of them.
			$"-p:RintenLabel=\"{Label()}\" " +
			$"-p:RintenOrientation={Orientation()} " +
			( IsVr() ? "-p:RintenVr=true " : "" ) +
			( Microphone() ? "-p:RintenMicrophone=true " : "" ) +
			( AndroidVideo() ? "-p:RintenVideo=true " : "" ) +
			$"-p:RintenBin=\"{Path.Combine( Environment.CurrentDirectory, ExportPlatform.Android.BinFolder )}{Path.DirectorySeparatorChar}\" " +
			$"{ExportToolchain.AndroidProperties()}-o \"{output}\"";

		try
		{
			using var process = Process.Start( ExportToolchain.Start( arguments ) );

			if ( process is null )
			{
				Problem( "no apk: the .NET SDK is not on the path" );
				return null;
			}

			var log = process.StandardOutput.ReadToEnd();
			var errors = process.StandardError.ReadToEnd();

			process.WaitForExit();

			if ( process.ExitCode != 0 )
			{
				//The build output rather than a summary. A missing Android workload says so in it, and there is nowhere else to read it.
				Problem( "no apk: the package would not build - see the log" );
				Logger.Warning( $"could not make an apk:\n{log}\n{errors}" );
				return null;
			}
		}
		catch ( Exception e )
		{
			Problem( $"no apk: {e.Message}" );
			return null;
		}

		//The signed one is the one a phone will install; the other is what it was signed from.
		var written = Directory.Exists( output )
			? Directory.GetFiles( output, "*.apk", SearchOption.AllDirectories )
			: [];

		var signed = written.FirstOrDefault( x => x.EndsWith( "-Signed.apk", StringComparison.OrdinalIgnoreCase ) )
			?? written.FirstOrDefault();

		if ( signed is null )
			Problem( $"the package built and there is no apk in {output}" );

		return signed;
	}

	//What this project is called where a name has to be a word.
	private string Ident()
	{
		var ident = _exportConfig.Project?.Config?.Ident;

		return string.IsNullOrWhiteSpace( ident ) ? "game" : Word( ident );
	}

	//The package name, which is what a phone knows the game by: two games installed under one id are one game, and the second replaces the first.
	private string ApplicationId() => $"com.rinten.{Ident()}";

	//Which way up the activity is held, as the manifest spells it - from the build page's choice for Android (Build.Orientation): landscape,
	//portrait, or turning with the phone. Landscape where nothing was chosen, which is what every build was before there was a choice.
	private string Orientation()
		=> _exportConfig.Project?.Config?.GetMetaOrDefault( "Build.Orientation", "landscape" ) switch
		{
			"portrait" => "sensorPortrait",
			"auto" => "fullUser",
			_ => "sensorLandscape",
		};

	//What is written under the icon - the project's title, as it reads.
	private string Label()
	{
		var title = _exportConfig.Project?.Config?.Title;

		if ( string.IsNullOrWhiteSpace( title ) ) title = Ident();

		//The placeholders are a list of key=value pairs, so a title carrying either of those characters would end up being read as another pair.
		return title.Replace( ';', ' ' ).Replace( '=', ' ' ).Replace( '"', ' ' ).Trim();
	}

	//One segment of a package name: letters, digits and underscores, and not starting with a digit.
	private static string Word( string name )
	{
		var word = new StringBuilder();

		foreach ( var c in name.ToLowerInvariant() )
			word.Append( char.IsAsciiLetterOrDigit( c ) ? c : '_' );

		if ( word.Length == 0 || char.IsAsciiDigit( word[0] ) )
			word.Insert( 0, '_' );

		return word.ToString();
	}

	//Says what is happening now, for the page that started this.
	private void Say( string operation, float fraction )
	{
		if ( OnProgressChanged is null ) return;

		UpdateProgress( new ExportProgress
		{
			ProgressFraction = fraction,
			CurrentOperation = operation,
			FilesDone = Files.Count,
			FilesTotal = Files.Count,
			BuildIssues = []
		} );
	}
}
