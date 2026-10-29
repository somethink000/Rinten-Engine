using System.Collections.Generic;

namespace Rinten;

/// <summary>What a game exported for the browser tells the player about itself.</summary>
public class WebManifest
{
	/// <summary>What the game is called, which is also the page's title.</summary>
	public string Name { get; set; }

	/// <summary>The package ident this was exported from.</summary>
	public string Ident { get; set; }

	/// <summary>The scene the game starts in, as a resource path.</summary>
	public string StartScene { get; set; }

	/// <summary>The game's own assemblies, in load order, relative to the page.</summary>
	public List<string> Assemblies { get; set; } = new();

	/// <summary>Per assembly, source fingerprints a server may send; a page only joins servers matching one</summary>
	public Dictionary<string, List<string>> Code { get; set; } = new();

	/// <summary>The module's own hash: what the page keeps its compiled copy under, so a visit after the first takes it out of the browser's cache instead of downloading and compiling a hundred megabytes again - and a new export is a new key. Empty where the export made no module. See index.html.</summary>
	public string Module { get; set; }

	/// <summary>When this was exported.</summary>
	public DateTime BuildDate { get; set; } = DateTime.UnixEpoch;

	/// <summary>VR build: page requests a headset context at creation; ?vr / ?novr in the URL override it</summary>
	public bool Vr { get; set; }

	//For looking at a scene that has no camera and no code to make one - the dev build the core's own build script writes. A shipped game leaves it off and the scene's camera is the camera.
	/// <summary>Fly the camera around with WASD instead of letting the game drive it.</summary>
	public bool FreeCamera { get; set; }

	/// <summary>Exported for the one making it - see Application.IsDebugBuild.</summary>
	public bool Debug { get; set; }

	/// <summary>The picture shown while the game starts, as a path in the engine's content; empty shows none.</summary>
	public string Splash { get; set; }
}

/// <summary>The names a web build agrees on, written by the export and read by the page.</summary>
public static class WebBuild
{
	/// <summary>The manifest, beside the page.</summary>
	public const string ManifestName = "web-game.json";

	/// <summary>Every asset file in the build, one path per line.</summary>
	public const string AssetListName = "web-assets.txt";

	//For what a game carries many of and uses few: the fonts, sixty-odd faces of which a game's text is set in a handful. See WebContent.Request.
	/// <summary>Opens a line of <see cref="AssetListName"/> naming a file the page fetches the first time something asks for it, rather than before the game starts.</summary>
	public const char OnDemand = '~';

	/// <summary>Marks an asset list line for a file shipped gzipped as path.gz, unpacked as it arrives</summary>
	public const char Packed = '+';

	/// <summary>The files each scene needs that the page does not fetch before the start - a line a file, <c>scene file</c>. Loading such a scene waits for them; the rest come down while the game runs. See WebContent.ForScene.</summary>
	public const string SceneListName = "web-scenes.txt";

	//Not .bin , which is where a project keeps them: a good many static hosts will not serve anything whose name begins with a dot, and a game whose code quietly 404s is a game with no code in it.
	/// <summary>Where the game's assemblies go, as a folder of the build.</summary>
	public const string AssemblyFolder = "bin";
}
