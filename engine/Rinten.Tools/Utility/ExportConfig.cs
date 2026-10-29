using System;

namespace Editor;

public class ExportConfig
{
	public Project Project { get; set; }

	/// <summary>Asset packages the compiled code references.</summary>
	public HashSet<string> CodePackages = new();

	/// <summary>If the compile process created any assemblies</summary>
	public Dictionary<string, object> AssemblyFiles { get; set; }

	/// <summary>Where are we putting the exported build?</summary>
	[Title( "Export Directory" ), FolderPath]
	public string TargetDir { get; set; }

	//Defaults to the editor's machine; another desktop changes only the native libraries and the program's name
	/// <summary>Which platform the build is for.</summary>
	[Title( "Platform" )]
	public ExportPlatform Platform { get; set; } = ExportPlatform.Host;

	/// <summary>The target .exe name for this export</summary>
	[Title( "Executable Name" )]
	public string ExecutableName { get; set; }

	/// <summary>The icon for the target .exe</summary>
	[Title( "Executable Icon" )]
	public string TargetIcon { get; set; }

	/// <summary>The game's own picture for the startup, with LoadingScreen Own; empty is black.</summary>
	[Title( "Startup Image" ), FilePath( "png,jpg", AllowExternal = true )]
	public string StartupImage { get; set; }

	//Web only: an itch.io upload is one archive with index.html at its root
	/// <summary>Whether to zip the finished build beside itself.</summary>
	[Title( "Zip For Upload" )]
	public bool CreateZip { get; set; } = true;

	/// <summary>A build for the game's developer; the game can ask through Application.IsDebugBuild.</summary>
	[Title( "Export Debug" )]
	public bool ExportDebug { get; set; }

	/// <summary>What the game shows while it starts: the engine's splash or its own picture.</summary>
	[Title( "Loading Screen" )]
	public LoadingScreenKind LoadingScreen { get; set; }

	/// <summary>Game's build date</summary>
	public DateTime BuildDate { get; set; } = DateTime.Now;
}

/// <summary>What an exported game shows while it starts.</summary>
public enum LoadingScreenKind
{
	/// <summary>The engine's splash picture.</summary>
	Engine,

	/// <summary>The game's own picture, or black until the game draws where it has none.</summary>
	Own,
}
