using static Rinten.Build.Constants;

namespace Rinten.Build.Steps;

internal class BuildManaged( bool clean = false, bool aot = true )
{
	internal ExitCode Run()
	{
		string engineDir = Path.Combine( Directory.GetCurrentDirectory(), "engine" );
		string rootDir = Directory.GetCurrentDirectory();

		try
		{
			Log.Info( "Step 1: Dotnet Clean" );
			if ( clean )
			{
				if ( !Utility.RunDotnetCommand( engineDir, "clean" ) )
					return ExitCode.Failure;
			}
			else
			{
				Log.Info( "Skipping dotnet clean as cleanBuild is false." );
			}

			//Named rather than left to the folder: a bare restore takes whatever solution it finds there, and step 5 has to build the same one.
			Log.Info( "Step 2: Dotnet Restore" );
			if ( !Utility.RunDotnetCommand( engineDir, "restore Rinten-Engine.slnx" ) )
				return ExitCode.Failure;

			Log.Info( "Step 3: Build CodeGen.exe" );
			if ( !Utility.RunDotnetCommand( engineDir, "build Tools/CodeGen/ -o Tools/CodeGen/bin" ) )
				return ExitCode.Failure;

			Log.Info( "Step 3a: Build CreateGameCache.exe" );
			if ( !Utility.RunDotnetCommand( engineDir, "build Tools/CreateGameCache/ -o Tools/CreateGameCache/bin" ) )
				return ExitCode.Failure;

			Log.Info( "Step 4: Clear managed folder" );
			string managedDir = Path.Combine( rootDir, "game", "bin", "managed" );
			if ( !clean )
			{
				Log.Info( "Skipping managed folder cleanup for incremental build." );
			}
			else if ( Directory.Exists( managedDir ) )
			{
				try
				{
					Directory.Delete( managedDir, true );
					Directory.CreateDirectory( managedDir ); //Recreate the empty directory
					Log.Info( $"Successfully cleared directory: {managedDir}" );
				}
				catch ( Exception ex )
				{
					Log.Warning( $"Warning: Failed to clear directory: {managedDir}. Error: {ex.Message}" );
					//Continue execution since this is a warning in the original script
				}
			}
			else
			{
				Log.Info( $"Directory does not exist, creating: {managedDir}" );
				Directory.CreateDirectory( managedDir );
			}

			Log.Info( "Step 5: Build Managed" );
			if ( !Utility.RunDotnetCommand( engineDir, "build -c Release Rinten-Engine.slnx -p:TreatWarningsAsErrors=true" ) )
				return ExitCode.Failure;

			Log.Info( "Step 6: Install the restored native assets" );
			InstallRestoredNatives( rootDir );

			//Skipped by --no-aot: a publish compiles the editor's whole closure a second time, so somebody editing
			//C# in a loop can leave it to the builds that will be measured or played.
			if ( aot )
			{
				Log.Info( "Step 7: Compile the engine's assemblies ahead of time" );
				AheadOfTime( engineDir, rootDir );
			}

			Log.Info( "Build completed successfully!" );
			return ExitCode.Success;
		}
		catch ( Exception ex )
		{
			Log.Error( $"Build failed with error: {ex}" );
			return ExitCode.Failure;
		}
	}

	//Publishes the editor's assembly closure with ReadyToRun and copies it over the IL in game/bin/managed. The runtime then
	//runs precompiled code for the engine and Roslyn at startup instead of compiling it on every launch. Non-fatal: an IL build
	//is a correct build that pays the JIT, which is the only thing this saves.
	private static void AheadOfTime( string engineDir, string rootDir )
	{
		//The editor's top assembly, so the publish carries what it references: the engine, its compiler, the tools and the UI.
		//The few the editor loads beside it rather than through it - Rinten.AppSystem, the upgrader, NetCore, MonoMod.Iced - are
		//not in that closure and stay as step 5 left them: IL. They are small; the engine and Roslyn are what this is for.
		var project = Path.Combine( engineDir, "Rinten.Tools", "Rinten.Tools.csproj" );
		var rid = Platform.Create().RuntimeIdentifiers[0];
		var staged = Path.Combine( engineDir, "bin", "obj", "r2r" );
		var building = Path.Combine( staged, "build" );
		var published = Path.Combine( staged, "out" );
		var stamp = Path.Combine( staged, ".stamp" );

		Directory.CreateDirectory( staged );

		//The publish is not incremental: it compiles its whole closure again even when nothing changed. A stamp of the
		//engine's sources is what decides, so the minute it costs is only paid by a build that changed something. The
		//copy below still runs either way: step 5 wrote IL over game/bin/managed, and this is what puts the precompiled
		//code back. It is the same bytes the last publish made, so the cache fingerprint in the editor does not move.
		if ( File.Exists( stamp ) && File.Exists( Path.Combine( published, "Rinten.Engine.dll" ) )
			&& !AnySourceNewer( engineDir, File.GetLastWriteTimeUtc( stamp ) ) )
		{
			Log.Info( "  using the assemblies compiled ahead of time by the last build" );
		}
		else
		{
			//Framework-dependent, as the editor is: the runtime is the machine's, only our assemblies are precompiled.
			var arguments = $"publish \"{project}\" -c Release -r {rid} --self-contained false " +
				$"-p:PublishReadyToRun=true -p:PublishSingleFile=false " +
				$"-p:OutputPath=\"{building}{Path.DirectorySeparatorChar}\" " +
				$"-p:PublishDir=\"{published}{Path.DirectorySeparatorChar}\" " +
				$"-p:SolutionDir=\"{engineDir}{Path.DirectorySeparatorChar}\"";

			Log.Info( $"  ReadyToRun for {rid}" );

			if ( !Utility.RunProcess( "dotnet", arguments, engineDir ) )
			{
				Log.Warning( "  the engine could not be compiled ahead of time - keeping the IL build; startup pays the JIT" );
				return;
			}

			//A publish that reported success but left no engine assembly would copy a half-written closure over the IL.
			//No stamp is written, so the next build tries again rather than reusing it.
			if ( !File.Exists( Path.Combine( published, "Rinten.Engine.dll" ) ) )
			{
				Log.Warning( "  the publish left no engine assembly - keeping the IL build; startup pays the JIT" );
				return;
			}

			File.WriteAllText( stamp, DateTime.UtcNow.ToString( "O" ) );
		}

		var managed = Path.Combine( rootDir, "game", "bin", "managed" );
		var copied = 0;

		foreach ( var file in Directory.EnumerateFiles( published, "*.dll" ) )
		{
			try
			{
				File.Copy( file, Path.Combine( managed, Path.GetFileName( file ) ), overwrite: true );
				copied++;
			}
			catch ( IOException ex )
			{
				//Held open by an editor that is running. The assembly already there is the same one, so the build goes on.
				Log.Warning( $"  {Path.GetFileName( file )} could not be replaced - {ex.Message}" );
			}
		}

		Log.Info( $"  {copied} assemblies compiled ahead of time into game/bin/managed" );
	}

	//Whether any source under engine/ is newer than the stamp. Build output and fetched natives are skipped: they are
	//rewritten by the build and say nothing about what the assembly will contain.
	private static bool AnySourceNewer( string directory, DateTime stamp )
	{
		foreach ( var file in Directory.EnumerateFiles( directory ) )
		{
			if ( Path.GetExtension( file ) is not (".cs" or ".razor" or ".csproj" or ".props" or ".targets" or ".resx" or ".scss" or ".bin") )
				continue;

			if ( File.GetLastWriteTimeUtc( file ) > stamp )
				return true;
		}

		foreach ( var child in Directory.EnumerateDirectories( directory ) )
		{
			//The build tool, the tests and the benchmarks are not part of the editor's assembly closure.
			if ( Path.GetFileName( child ) is "bin" or "obj" or "Natives" or "TestResults" or ".git"
				or "RintenBuild" or "Tests" or "Benchmark" )
				continue;

			if ( AnySourceNewer( child, stamp ) )
				return true;
		}

		return false;
	}

	//Copies NuGet native libraries (Skia, HarfBuzz) from runtimes/<rid>/native next to engine libs
	private static void InstallRestoredNatives( string rootDir )
	{
		var runtimesDir = Path.Combine( rootDir, "game", "bin", "managed", "runtimes" );

		if ( !Directory.Exists( runtimesDir ) )
			return;

		var host = Platform.Create().PlatformID;

		foreach ( var (bin, rids) in NativeTargets )
		{
			var nativeDir = Path.Combine( rootDir, "game", "bin", bin );

			//The machine's own always gets one. The others only where a core for that platform is already there: an otherwise empty bin folder tells
			//the exporter it has native libraries when what it has is Skia and no engine, and that swallows the warning that would have said so.
			if ( bin == host )
				Directory.CreateDirectory( nativeDir );
			else if ( !Directory.Exists( nativeDir ) )
				continue;

			//Most specific identifier first, and once a name has been taken the broader folders do not get to overwrite it.
			var taken = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

			foreach ( var rid in rids )
			{
				var from = Path.Combine( runtimesDir, rid, "native" );

				if ( !Directory.Exists( from ) )
					continue;

				foreach ( var file in Directory.GetFiles( from ) )
				{
					var name = Path.GetFileName( file );

					if ( !taken.Add( name ) )
						continue;

					try
					{
						var into = Path.Combine( nativeDir, name );

						//The same library under a second name is a link to the first, not another copy of it. SkiaSharp's package carries
						//libSkiaSharp.so and libSkiaSharp.so.116.0.0, the same nine megabytes twice, and every export carried both.
						if ( Twin( file, from, taken ) is { } first && !OperatingSystem.IsWindows() )
						{
							if ( new FileInfo( into ) is { Exists: true } or { LinkTarget: not null } )
								File.Delete( into );

							File.CreateSymbolicLink( into, first );
							Log.Info( $"  {name} -> game/bin/{bin} (a link to {first})" );
							continue;
						}

						File.Copy( file, into, overwrite: true );
					}
					catch ( IOException )
					{
						//Held open by something that is running - an editor, a launcher. The file it is holding is the same file
						//this would have written, so saying so is the whole of what wants doing; failing the build over it is not.
						Log.Warning( $"  {name} is in use - keeping the one already in game/bin/{bin}" );
						continue;
					}

					Log.Info( $"  {name} -> game/bin/{bin}" );
				}
			}
		}
	}

	//Name an identical library was already copied under (by content), or null if it's unique
	private static string Twin( string file, string folder, HashSet<string> taken )
	{
		var length = new FileInfo( file ).Length;

		foreach ( var name in taken )
		{
			if ( string.Equals( name, Path.GetFileName( file ), StringComparison.Ordinal ) ) continue;

			var other = Path.Combine( folder, name );

			if ( !File.Exists( other ) || new FileInfo( other ).Length != length ) continue;
			if ( !File.ReadAllBytes( other ).AsSpan().SequenceEqual( File.ReadAllBytes( file ) ) ) continue;

			return name;
		}

		return null;
	}

	//Every exportable platform's bin folder and its RIDs, so cross-exported games still get Skia
	private static readonly (string Bin, string[] Rids)[] NativeTargets =
	[
		( "win64", [ "win-x64", "win" ] ),
		( "linuxsteamrt64", [ "linux-x64", "linux" ] ),
		( "linuxsteamrtarm64", [ "linux-arm64" ] ),
		( "osxarm64", [ "osx-arm64", "osx" ] ),
	];
}
