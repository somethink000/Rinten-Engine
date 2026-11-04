using Rinten.Engine;
using System;
using System.Linq;

namespace Rinten;

public static class Launcher
{
	//Marker a running build leaves; starting an editor while binaries are overwritten crashes it
	private const string BuildingMarker = ".building";

	public static int Main()
	{
		if ( Building() )
		{
			Console.Error.WriteLine( "A build is writing the engine's binaries right now." );
			Console.Error.WriteLine( "Starting on top of one crashes in the loader and can take" );
			Console.Error.WriteLine( "the desktop session down. Wait for it to finish." );

			return 1;
		}

		if ( HasCommandLineSwitch( "-generatesolution" ) )
		{
			Bootstrap.InitMinimal( Environment.CurrentDirectory );
			Project.InitializeBuiltIn( false ).GetAwaiter().GetResult();
			RintenCode.GenerateSolution().GetAwaiter().GetResult();
			EngineFileSystem.Shutdown();
			return 0;
		}

		//A project handed over on its own, which is how a file manager opens one.
		AdoptProjectArgument();

		//With no -project the editor window opens filled by the launcher while it loads - see StartupLoadProject.ShowPicker
		Editor.StartupLoadProject.ProjectPicker = Editor.ProjectsOverEditor.Make;

		new CoreEditorAppSystem().Run();

		return 0;
	}

	//Whether a build is writing the binaries this process is about to load.
	private static bool Building()
	{
		try
		{
			return System.IO.File.Exists(
				System.IO.Path.Combine( AppContext.BaseDirectory, BuildingMarker ) );
		}
		catch
		{
			//Nothing about a missing folder or a denied read is worth refusing to start over: the marker not being readable is the marker not being there.
			return false;
		}
	}

	//Turns a bare project path argument (file manager open) into -project on the parsed command line
	private static void AdoptProjectArgument()
	{
		if ( HasCommandLineSwitch( "-project" ) ) return;

		var path = Environment.GetCommandLineArgs()
			.Skip( 1 )
			.FirstOrDefault( x =>
				x.EndsWith( ".rinproj", StringComparison.OrdinalIgnoreCase )
				&& !x.StartsWith( '-' ) && !x.StartsWith( '+' ) );

		if ( string.IsNullOrEmpty( path ) ) return;

		//A quote in the path would end the one this goes inside, and the rest of it would be read as
		//switches. Nothing on a desktop is named that way and a launcher is not the place to find out.
		if ( path.Contains( '"' ) ) return;

		try
		{
			path = System.IO.Path.GetFullPath( path );

			if ( !System.IO.File.Exists( path ) ) return;
		}
		catch
		{
			//A path the filesystem will not even look at is not a project.
			return;
		}

		Rinten.Utility.CommandLine.CommandLineString = $"{Environment.CommandLine} -project \"{path}\"";
		Rinten.Utility.CommandLine.Parse();
	}

	private static bool HasCommandLineSwitch( string switchName )
	{
		return Environment.GetCommandLineArgs().Any( arg => arg.Equals( switchName, StringComparison.OrdinalIgnoreCase ) );
	}
}
