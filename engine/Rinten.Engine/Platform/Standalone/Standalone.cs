namespace Rinten;

public static class Standalone
{
	internal const string GamePath = "assets/";
	internal const string ManifestName = "standalone.manifest.json";

	//If running in standalone, contains the properties of the standalone game
	internal static StandaloneManifest Manifest { get; private set; }

	internal static void Init()
	{
		if ( !Application.IsStandalone )
			return;
	}

	internal static void SetupFromManifest( StandaloneManifest manifest )
	{
		Manifest = manifest;

		_buildDate = Manifest.BuildDate;
		Application.DebugExport = Manifest.Debug;
		Splash.EngineSplash.Picture = Manifest.Splash;
	}

	private static DateTime _buildDate = DateTime.UnixEpoch;

	/// <summary>The date and time at which the current standalone game was built</summary>
	public static DateTime BuildDate => Application.IsStandalone ? _buildDate : DateTime.UnixEpoch;

	/// <summary>Is the current standalone game running in development mode?</summary>
	[Obsolete]
	public static bool IsDevelopmentBuild => false;

	/// <summary>The date and time at which the current standalone game was built</summary>
	[Obsolete( "Use BuildDate" )]
	public static DateTime VersionDate => BuildDate;

	/// <summary>Represents the current standalone game's version, specified by the developer</summary>
	[Obsolete]
	public static Version Version => new Version( 0, 0, 0 );
}
