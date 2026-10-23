using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Rinten;

public static class LauncherEnvironment
{
	//The folder containing Rinten.exe
	public static string GamePath { get; set; }

	//The folder containing Rinten.Engine.dll
	public static string ManagedDllPath { get; set; }

	public static string PlatformName
	{
		get
		{
			if ( OperatingSystem.IsBrowser() ) return "wasm32";

			var platform = OperatingSystem.IsWindows() ? "win"
				: OperatingSystem.IsLinux() ? "linuxsteamrt"
				: OperatingSystem.IsMacOS() ? "osx"
				: throw new Exception( "Unsupported platform" );

			var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "64";
			return $"{platform}{architecture}";
		}
	}

	public static void Init()
	{
		AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

		GamePath = AppContext.BaseDirectory;

		//this exe is in the bin folder
		if ( GamePath.EndsWith( System.IO.Path.Combine( "bin", PlatformName ) ) )
		{
			//go up two folders
			GamePath = System.IO.Path.GetDirectoryName( GamePath );
			GamePath = System.IO.Path.GetDirectoryName( GamePath );
		}

		//Packaged installs keep the content in one engine/ folder next to the binaries - see Launcher.cs, which makes the same decision the same way
		var packagedRoot = System.IO.Path.Combine( GamePath, "engine" );
		if ( System.IO.Directory.Exists( System.IO.Path.Combine( packagedRoot, "bin" ) ) )
		{
			GamePath = packagedRoot;
		}

		//this exe is in the game folder
		ManagedDllPath = $"{GamePath}/bin/managed/";
		var nativeDllPath = $"{GamePath}/bin/{PlatformName}/";

		//make the game dir our current dir
		Environment.CurrentDirectory = GamePath;

		RecordEngineFolder( GamePath );

		UpdateNativeDllPath( nativeDllPath );
	}

	//Separate method so Rinten.System loads only after the assembly resolver is hooked up
	private static void RecordEngineFolder( string gamePath )
	{
		EngineEnvironment.EngineFolder = gamePath;
	}

	private static void UpdateNativeDllPath( string nativeDllPath )
	{
		//WARNING: this calls into Rinten.Engine.dll - so we need to put it in this method, which is
		//executed AFTER CurrentDomain_AssemblyResolve is set so that managed can find the correct dll
		NetCore.NativeDllPath = nativeDllPath;

		if ( OperatingSystem.IsLinux() )
			RelinkLibraries( nativeDllPath );

		//Put our native dll path first so that when looking up native dlls we'll always use the ones from our folder first
		if ( OperatingSystem.IsWindows() )
		{
			var path = System.Environment.GetEnvironmentVariable( "PATH" );
			path = $"{nativeDllPath};{path}";
			System.Environment.SetEnvironmentVariable( "PATH", path );
		}
	}

	//A release stores libSkiaSharp.so and the like as links; an unpacker that ignores links leaves a file
	//holding the target's name, which no loader can open. Those files become the links they were meant to be
	private static void RelinkLibraries( string nativeDllPath )
	{
		try
		{
			foreach ( var file in System.IO.Directory.GetFiles( nativeDllPath, "*.so*" ) )
			{
				var info = new System.IO.FileInfo( file );
				if ( info.LinkTarget is not null || info.Length > 255 ) continue;

				var target = System.IO.File.ReadAllText( file ).Trim();
				if ( target.Length == 0 || target.Contains( '/' ) || target == info.Name ) continue;

				var targetInfo = new System.IO.FileInfo( System.IO.Path.Combine( nativeDllPath, target ) );
				if ( !targetInfo.Exists || targetInfo.Length <= 255 ) continue;

				System.IO.File.Delete( file );
				System.IO.File.CreateSymbolicLink( file, target );
			}
		}
		catch ( Exception e ) when ( e is System.IO.IOException or UnauthorizedAccessException )
		{
			//A read-only install stays as it is; the library that cannot load says so itself
		}
	}

	private static Assembly CurrentDomain_AssemblyResolve( object sender, ResolveEventArgs args )
	{
		var trim = args.Name.Split( ',' )[0];

		var name = $"{ManagedDllPath}/{trim}.dll";

		//dlls with resources inside appear as a different name
		name = name.Replace( ".resources.dll", ".dll" );

		if ( System.IO.File.Exists( name ) )
		{
			return Assembly.LoadFrom( name );
		}

		return null;
	}
}
