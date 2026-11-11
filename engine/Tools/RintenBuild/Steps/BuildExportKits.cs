using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using static Rinten.Build.Constants;

namespace Rinten.Build.Steps;

//The web and Android hosts compiled into <output>/<folder>/prebuilt, so a release exports to both without sources
internal class BuildExportKits( string output )
{
	//A host project, how it is built, and the files beside it a release needs
	private sealed record Kit( string Folder, string Project, string Host, string Arguments, string[] Files );

	private static readonly Kit[] Kits =
	[
		new( "RintenWeb", "RintenWeb.csproj", "Rinten-Web", "-r browser-wasm",
			[ "RintenWeb.csproj", "Directory.Build.props", "nuget.config", "wwwroot/index.html" ] ),
		new( "RintenAndroid", "Rinten-Android.csproj", "Rinten-Android", "-f net11.0-android",
			[ "Rinten-Android.csproj", "Directory.Build.props", "Properties/AndroidManifest.xml", "Properties/AndroidManifest.Vr.xml" ] ),
	];

	internal ExitCode Run()
	{
		var root = Directory.GetCurrentDirectory();
		var launcher = Path.Combine( root, "engine", "Launcher" );
		var managed = Path.Combine( root, "game", "bin", "managed" );
		var into = Path.Combine( root, output );

		if ( Directory.Exists( into ) ) Directory.Delete( into, true );
		Directory.CreateDirectory( into );

		File.Copy( Path.Combine( launcher, "Prebuilt.targets" ), Path.Combine( into, "Prebuilt.targets" ) );

		foreach ( var kit in Kits )
		{
			var source = Path.Combine( launcher, kit.Folder );
			var kitDir = Path.Combine( into, kit.Folder );

			Log.Info( $"Building the {kit.Folder} kit" );

			//Into its own bin/: -o would move the engine projects' outputs too, which are referenced where they already are
			var ok = Utility.RunProcess( "dotnet",
				$"build \"{Path.Combine( source, kit.Project )}\" -c Release {kit.Arguments} -p:BuildProjectReferences=false -nologo -v q",
				root );

			var built = ok ? Output( source, kit.Host ) : null;

			if ( built is null )
			{
				Log.Error( $"{kit.Folder} would not build" );
				return ExitCode.Failure;
			}

			foreach ( var file in kit.Files )
			{
				var from = Path.Combine( source, file );
				var to = Path.Combine( kitDir, file );

				if ( !File.Exists( from ) )
				{
					Log.Error( $"{kit.Folder} has no {file}" );
					return ExitCode.Failure;
				}

				Directory.CreateDirectory( Path.GetDirectoryName( to )! );
				File.Copy( from, to );
			}

			var prebuilt = Path.Combine( kitDir, "prebuilt" );
			Directory.CreateDirectory( prebuilt );

			var count = 0;

			foreach ( var assembly in Closure( kit.Host, built, managed ) )
			{
				File.Copy( assembly, Path.Combine( prebuilt, Path.GetFileName( assembly ) ), true );
				count++;
			}

			Log.Info( $"  {count} assemblies in {Path.GetRelativePath( root, prebuilt )}" );
		}

		return ExitCode.Success;
	}

	//The folder the build wrote the host into, newest first
	private static string Output( string project, string host )
	{
		var bin = Path.Combine( project, "bin", "Release" );

		return Directory.Exists( bin )
			? Directory.GetFiles( bin, host + ".dll", SearchOption.AllDirectories )
				.OrderByDescending( File.GetLastWriteTimeUtc ).Select( Path.GetDirectoryName ).FirstOrDefault()
			: null;
	}

	//The host and every assembly it reaches that is not the framework's, found in the build output or beside the editor
	private static List<string> Closure( string host, string built, string managed )
	{
		var found = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		var walk = new Queue<string>();

		void Take( string name )
		{
			if ( found.ContainsKey( name ) ) return;

			var path = new[] { built, managed }
				.Select( x => Path.Combine( x, name + ".dll" ) )
				.FirstOrDefault( File.Exists );

			if ( path is null ) return;

			found[name] = path;
			walk.Enqueue( path );
		}

		Take( host );

		while ( walk.Count > 0 )
		{
			using var stream = File.OpenRead( walk.Dequeue() );
			using var pe = new PEReader( stream );

			if ( !pe.HasMetadata ) continue;

			var metadata = pe.GetMetadataReader();

			foreach ( var handle in metadata.AssemblyReferences )
				Take( metadata.GetString( metadata.GetAssemblyReference( handle ).Name ) );
		}

		return found.Values.ToList();
	}
}
