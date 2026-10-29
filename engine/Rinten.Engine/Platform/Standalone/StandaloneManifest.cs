namespace Rinten;

public class StandaloneManifest
{
	/// <summary>What is the game's name?</summary>
	public string Name { get; set; }

	/// <summary>What ident are we running under?</summary>
	public string Ident { get; set; }

	/// <summary>Game's executable name (e.g. game.exe)</summary>
	public string ExecutableName { get; set; }

	/// <summary>Game's build date, automatically set when the game was exported.</summary>
	public DateTime BuildDate { get; set; } = DateTime.UnixEpoch;

	/// <summary>Should we automatically launch this project in VR?</summary>
	public bool IsVRProject { get; set; } = false;

	/// <summary>Exported for the one making it - see Application.IsDebugBuild.</summary>
	public bool Debug { get; set; }

	/// <summary>The picture shown while the game starts, as a path in the engine's content; empty shows none.</summary>
	public string Splash { get; set; }
}
