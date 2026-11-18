using System.Text;
using static Rinten.Build.Constants;

namespace Rinten.Build.Steps;

internal class Test( bool noBuild = false, string filter = null )
{
	//Filter for pull request runs: everything except tests that talk to the live backend, so
	//a backend hiccup can't fail an unrelated PR. The full suite still runs on release builds.
	public const string ExcludeLiveBackend = "TestCategory!=LiveBackend";

	private record TestProject( string Name, bool NeedsEngine );

	//Test projects, fastest first; a failing tier stops the run. Unit tier runs without the engine
	private static readonly TestProject[] Projects =
	[
		new( "Rinten.Test.Unit", NeedsEngine: false ),
		new( "Rinten.Test.Engine", NeedsEngine: true ),
		new( "Rinten.Test.Integration", NeedsEngine: true ),
	];

	internal ExitCode Run()
	{
		try
		{
			string rootDir = Directory.GetCurrentDirectory();
			string engineDir = Path.Combine( rootDir, "engine" );
			string gameDir = Path.Combine( rootDir, "game" );

			foreach ( var project in Projects )
			{
				if ( !RunTestProject( project, engineDir, gameDir ) )
				{
					Log.Error( $"{project.Name} tests failed!" );
					return ExitCode.Failure;
				}
			}

			Log.Info( "All tests completed successfully!" );
			return ExitCode.Success;
		}
		catch ( Exception ex )
		{
			Log.Error( $"Test operations failed with error: {ex}" );
			return ExitCode.Failure;
		}
	}

	//Runs a single test project with the environment its tier expects, logging a summary of any failed tests. Returns whether the run passed.
	private bool RunTestProject( TestProject project, string engineDir, string gameDir )
	{
		var csproj = Path.Combine( "Tests", project.Name, $"{project.Name}.csproj" );

		//--no-build skips the build this run would otherwise do. A normal managed build leaves the test projects out
		//- they write nothing a build or a game reads - so it is only right after something else has built them.
		var noBuildFlag = noBuild ? " --no-build" : string.Empty;
		var filterFlag = string.IsNullOrEmpty( filter ) ? string.Empty : $" --filter \"{filter}\"";
		var testArgs = $"test {csproj} --logger \"console;verbosity=normal;consoleLoggerParameters=ErrorsOnly\" -c Release{noBuildFlag}{filterFlag}";

		Log.Info( "" );
		Log.Info( project.NeedsEngine
			? $"Running {project.Name}..."
			: $"Running {project.Name} (RINTEN_ENGINE removed - this tier must not need the native engine)..." );

		//Track output for failed tests:
		List<string> failedTests = new List<string>();
		StringBuilder currentFailedTestInfo = new();
		var isCollectingFailedTestInfo = false;

		bool success = Utility.RunProcess(
			"dotnet",
			testArgs,
			engineDir,
			new Dictionary<string, string> { { "RINTEN_ENGINE", project.NeedsEngine ? gameDir : null } },
			//A bit hacky but we collect failed tests to get a nicer summary in the end
			onDataReceived: ( sender, e ) =>
			{
				if ( e.Data != null )
				{
					Log.Info( e.Data );

					if ( isCollectingFailedTestInfo && e.Data.TrimStart().StartsWith( "Passed" ) )
					{
						failedTests.Add( currentFailedTestInfo.ToString().Trim( '\n' ) );
						currentFailedTestInfo = currentFailedTestInfo.Clear();
						isCollectingFailedTestInfo = false;
					}
					if ( e.Data.TrimStart().StartsWith( "Failed " ) )
					{
						isCollectingFailedTestInfo = true;
					}
					if ( isCollectingFailedTestInfo )
					{
						currentFailedTestInfo.AppendLine( e.Data );
					}
				}
			}
		);

		if ( !success )
		{
			Log.Info( "" );
			Log.Info( $"Failed Tests Summary ({project.Name}):" );
			Log.Info( "" );

			foreach ( var failedTest in failedTests )
			{
				Log.Info( failedTest );
				Log.Info( "" );
			}
		}

		return success;
	}
}
