namespace Rinten.Porting;

/// <summary>What a port did, and - the part that matters - what it could not do.</summary>
public sealed class PortReport
{
	/// <summary>The project file the port wrote, if it wrote one.</summary>
	public string ProjectFile { get; set; } = "";

	public int FilesCopied { get; set; }
	public int ModelsPorted { get; set; }
	public int MaterialsPorted { get; set; }
	public int AnimationsPorted { get; set; }
	public int ScenesConverted { get; set; }

	/// <summary>Hammer maps that were read and written out as prefabs.</summary>
	public int MapsPorted { get; set; }

	/// <summary>Levels that were put into a scene where a map component used to name one.</summary>
	public int MapsPlaced { get; set; }

	/// <summary>Prefab each level was written as, keyed by lowercase .vmap path; may differ on name collisions</summary>
	public Dictionary<string, string> Levels { get; } = new( StringComparer.OrdinalIgnoreCase );

	//Worth saying separately because these are the ones with no source: every other clip a port writes can be built again from the art it names, and these cannot be built again from anything.
	/// <summary>Models whose clips were read out of the compiled file itself, and how many came out of each.</summary>
	public List<(string Model, int Clips)> EmbeddedAnimations { get; } = new();

	/// <summary>Type names renamed in scenes and prefabs</summary>
	public int TypeNamesRenamed { get; set; }

	/// <summary>Files the port read but could not bring across, and why.</summary>
	public List<string> Skipped { get; } = new();

	//Graphs are listed one at a time because each is somebody's afternoon and losing one silently is not on. Shaders are distinct, because ninety-eight materials sharing one shader is one problem and not ninety-eight.
	/// <summary>Things that came across but need a person.</summary>
	public List<string> Graphs { get; } = new();
	public HashSet<string> CustomShaders { get; } = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Their graphs translated to model documents by name (null if failed); one translation, many copies</summary>
	public Dictionary<string, string> GraphDocuments { get; } = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>The first model each of their graphs went into, by the graph's name - what a scene or a source file that named the graph is pointed at.</summary>
	public Dictionary<string, string> GraphOwners { get; } = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Their graph files the walk passed, which only come across inside a model that names them.</summary>
	public HashSet<string> GraphsSeen { get; } = new( StringComparer.OrdinalIgnoreCase );

	//Counted rather than listed: a project has hundreds of them and one line each would bury everything else. What matters is that a port says how much it left behind instead of quietly dropping it.
	/// <summary>Source formats left where they were, counted by extension.</summary>
	public Dictionary<string, int> LeftBehind { get; } = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Anything worth saying that is not a failure.</summary>
	public List<string> Notes { get; } = new();

	/// <summary>Likely measurements left unconverted because their dimension is unknown; need manual review</summary>
	public List<Unconvertible> Unconverted { get; } = new();

	/// <summary>One number a port could not name.</summary>
	public readonly record struct Unconvertible( string Where, string What, string Value, string Why );

	public void CouldNotConvert( string where, string what, string value, string why )
		=> Unconverted.Add( new Unconvertible( where, what, value, why ) );

	public override string ToString()
		=> $"{FilesCopied} files, {ModelsPorted} models, {MaterialsPorted} materials, " +
			$"{AnimationsPorted} animations, {ScenesConverted} scenes, {MapsPorted} maps, " +
			$"{TypeNamesRenamed} type names renamed, " +
			$"{Skipped.Count} skipped, {Unconverted.Count} numbers left alone";
}
