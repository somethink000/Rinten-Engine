using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rinten.Porting.SBox;

/// <summary>Ports a project off s&amp;box asset formats into a new directory; the source is never written</summary>
public static class SBoxPorter
{
	private static readonly Logger log = new( "Porter" );

	//Compiled output is never copied; it gets rebuilt (see PortPaths.IsCompiled)
	private static bool IsCompiled( string extension ) => PortPaths.IsCompiled( extension );

	//Source files, and everything under a Code or Editor folder (razor styles live beside their markup)
	private static bool IsCode( string extension, string relative )
	{
		if ( PortNames.IsCode( extension ) || extension is ".sln" or ".slnx" or ".user" ) return true;

		return relative.Split( '/', '\\' ).SkipLast( 1 )
			.Any( x => x.Equals( "Code", StringComparison.OrdinalIgnoreCase ) || x.Equals( "Editor", StringComparison.OrdinalIgnoreCase ) );
	}

	//s&box formats nothing here can read; counted in the report, not copied. .vmap is a fallback
	private static readonly string[] SourceFormats =
	{
		".vmdl_prefab", ".vanmgrph", ".vanim", ".vtex", ".vsurf", ".vpcf",
		".vsnd", ".vsndevts", ".vsndstck", ".vmap", ".vphys", ".vmesh",
		".vsmart", ".vpost", ".vrman", ".vents",

		//Their tools' crash dumps
		".mdmp",
	};

	//Directories that hold nothing worth taking: build output, editor state, version control, and the asset compiler's own cache.
	private static readonly string[] SkippedDirectories =
	{
		//.sbox is theirs and .rinten is ours - a project that has been opened in either has one, and neither holds anything the person wrote.
		"obj", "bin", ".git", ".vs", ".vscode", ".sbox", ".rinten", ".claude",
		"Transients", "node_modules",
	};

	/// <summary>Port a whole project - assets, code and everything else - from <paramref name="sourceRoot"/> into a new one at <paramref name="destination"/>.</summary>
	public static PortReport Port( string sourceRoot, string destination, Project sourceProject = null,
		Action<string, float> progress = null )
	{
		var report = new PortReport();

		if ( !Directory.Exists( sourceRoot ) )
		{
			report.Skipped.Add( $"no such directory: {sourceRoot}" );
			return report;
		}

		var assets = Path.Combine( destination, "Assets" );
		var sourceAssets = PortPaths.FindDirectory( sourceRoot, "Assets" );

		if ( Directory.Exists( assets ) && Directory.EnumerateFileSystemEntries( assets ).Any() )
		{
			FillFromSource( sourceRoot, destination, sourceProject, report );

			//FilesCopied alongside the three asset counts: a fill that only touched a library or an editor folder ported no model,
			//material or animation, and would otherwise report the exact same "nothing to do" as a fill that genuinely found nothing.
			if ( report.ModelsPorted == 0 && report.MaterialsPorted == 0 && report.AnimationsPorted == 0
				&& report.FilesCopied == 0 )
			{
				report.Skipped.Add( $"destination already has assets: {assets}" );
				return report;
			}

			progress?.Invoke( Carrying, 0.85f );
			report.FilesCopied += SBoxBaseContent.Carry( assets, report );
			UnownedGraphs( report );

			progress?.Invoke( Naming, 0.95f );
			SBoxResourcePaths.Rewrite( assets, destination, report );
			report.MapsPlaced += SBoxMapPlacements.Place( assets, report );
			WriteUnitsNote( destination, report );

			log.Info( $"filled {sourceAssets} -> {destination}: {report}" );
			return report;
		}

		Directory.CreateDirectory( assets );

		report.ProjectFile = WriteProjectFile( destination, sourceProject );

		//Counted rather than walked lazily, because a bar wants a fraction and a fraction wants a
		//denominator. One directory walk against a port that reads every one of these files afterwards.
		var files = PortPaths.Walk( sourceRoot );
		var seen = 0;

		//Scenes are converted after the walk, once every model and material they name is across
		var scenes = new List<(string Path, string Named, string Source)>();

		foreach ( var file in files )
		{
			var fromRoot = Path.GetRelativePath( sourceRoot, file );

			//Before the skips, so the bar moves through a folder the port passes over rather than stopping on it.
			progress?.Invoke( $"Porting {fromRoot}", ++seen / (float)files.Length * 0.8f );

			if ( IsInSkippedDirectory( fromRoot ) ) continue;

			var extension = Path.GetExtension( file ).ToLowerInvariant();

			//Their build output. Counted rather than passed over in silence: a project whose art is only there
			//compiled comes across with holes in it, and the report is where that is said - see PortPaths.IsCompiled.
			if ( IsCompiled( extension ) )
			{
				report.LeftBehind[extension] = report.LeftBehind.GetValueOrDefault( extension ) + 1;
				continue;
			}

			//The binary half beside a document is not copied and is not left behind either: it is read where it lies, when the
			//document that points into it is converted, and what it holds ends up in that document - see Blobs and SBoxSceneBlobs.
			if ( PortPaths.IsBlobSidecar( file ) ) continue;

			//Only the project's own file is left behind - the new project has one already, written by whoever asked for the port. Libraries under
			//it have their own, and without them their code is copied but never compiled, which shows up as every type they define being missing.
			if ( extension == ".sbproj" && !fromRoot.Contains( Path.DirectorySeparatorChar ) ) continue;

			//Which Assets folder this file belongs to, which is not always the project's - see AssetsRootOf.
			var ownSource = AssetsRootOf( file, sourceRoot );
			var inAssets = ownSource is not null;

			var relative = inAssets ? Path.GetRelativePath( ownSource, file ) : fromRoot;
			var ownAssets = inAssets ? Beside( destination, sourceRoot, ownSource ) : assets;

			PortWalkedFile( file, relative, inAssets, ownSource ?? sourceAssets, ownAssets,
				destination, report, scenes );
		}

		foreach ( var (scene, named, source) in scenes )
		{
			try
			{
				if ( SceneUnits.ConvertFile( scene, PortUnits.SBox, ForeignBasis.Quake, named, report ) )
					++report.ScenesConverted;

				//Embeds mesh blobs into the scene, from the component or the _d sidecar (see SBoxSceneBlobs)
				Blobs( scene, source, named, report );

				Terrains( scene, source, named, report );

				//And a terrain painted whole with a material whose shader did not come across,
				//which otherwise draws as nothing at all rather than as the layers underneath it.
				SBoxTerrainMaterial.Strip( scene, assets, named, report );
			}
			catch ( Exception e )
			{
				report.Skipped.Add( $"{named}: {e.Message}" );
			}
		}

		//And the content the project names without containing - the engine's own, which a project uses by naming
		//and never carries. Last, because what is missing is only knowable once everything that is not is here.
		progress?.Invoke( Carrying, 0.85f );
		report.FilesCopied += SBoxBaseContent.Carry( assets, report );

		//Every model is across now, so a graph none of them took in is one nothing took in.
		UnownedGraphs( report );

		//And now that every file the port writes is written, what names them. Last for that reason: a name is
		//only rewritten where the file it would then point at is there, and the base content arrives at the end.
		progress?.Invoke( Naming, 0.95f );
		SBoxResourcePaths.Rewrite( assets, destination, report );

		//And now that the levels are prefabs and every name points at one, the scenes that used to name a map get the map itself put into
		//them. After the rewrite so it reads one spelling, and after the base content so the file it points at is there to be found.
		report.MapsPlaced += SBoxMapPlacements.Place( assets, report );

		WriteUnitsNote( destination, report );

		log.Info( $"ported {sourceAssets} -> {destination}: {report}" );
		return report;
	}

	//Nearest Assets folder above a file, or null; libraries and editor addons have their own
	private static string AssetsRootOf( string file, string sourceRoot )
	{
		var root = Path.GetFullPath( sourceRoot ).TrimEnd( Path.DirectorySeparatorChar );
		var at = Path.GetDirectoryName( Path.GetFullPath( file ) );

		while ( !string.IsNullOrEmpty( at ) && at.Length >= root.Length )
		{
			if ( string.Equals( Path.GetFileName( at ), "Assets", StringComparison.OrdinalIgnoreCase ) )
				return at;

			if ( string.Equals( at, root, StringComparison.OrdinalIgnoreCase ) ) break;

			at = Path.GetDirectoryName( at );
		}

		return null;
	}

	//The same place under the ported project that a folder had under theirs.
	private static string Beside( string destination, string sourceRoot, string folder )
		=> Path.Combine( destination, Path.GetRelativePath( sourceRoot, folder ) );

	//Terrain maps inlined and transposed, and terrain objects turned to match - see SBoxTerrains
	private static void Terrains( string file, string source, string named, PortReport report )
	{
		if ( file.EndsWith( ".terrain", StringComparison.OrdinalIgnoreCase ) )
		{
			if ( source is not null ) SBoxTerrains.ConvertStorage( file, source, named, report );
			return;
		}

		SBoxTerrains.TurnObjects( file );
	}

	//Writes referenced mesh blobs into the ported scene as base64; missing ones are reported
	private static void Blobs( string scene, string source, string named, PortReport report )
	{
		var carried = SBoxSceneBlobs.Convert( scene,
			source is null ? null : source + "_d", PortUnits.SBoxUnit );

		if ( carried.Vertices > 0 )
			report.Notes.Add( $"{named}: {carried.Vertices} mesh vertex/vertices from inches" +
				( carried.Inlined > 0
					? $", and {carried.Inlined} mesh(es) written into the scene rather than beside it"
					: "" ) );

		if ( carried.Missing > 0 )
			report.Skipped.Add( $"{named}: {carried.Missing} mesh(es) name a blob that is not in the " +
				"file beside the scene - the object is there and its geometry is not" );
	}

	private static void PortWalkedFile( string file, string relative, bool inAssets, string sourceAssets,
		string assets, string destination, PortReport report, List<(string Path, string Named, string Source)> scenes )
	{
		var extension = Path.GetExtension( file ).ToLowerInvariant();

		//Code is ported by hand: its units, axes and API cannot be rewritten mechanically
		if ( IsCode( extension, relative ) )
		{
			report.LeftBehind[extension] = report.LeftBehind.GetValueOrDefault( extension ) + 1;
			return;
		}

		try
		{
			switch ( extension )
			{
				case ".vmdl" when inAssets:
					PortModel( file, relative, sourceAssets, assets, report );
					break;

				case ".vmat" when inAssets:
					PortMaterial( file, relative, sourceAssets, assets, report );
					break;

				case ".tmat" when inAssets:
					PortTerrainLayer( file, relative, sourceAssets, assets, report );
					break;

				//A graph comes across inside every model that names it, and nowhere else - see
				//EmbeddedGraph. Remembered so the one no model names can be said to have stayed behind.
				case ".vanmgrph":
					report.GraphsSeen.Add( relative.Replace( '\\', '/' ) );
					break;

				//A library's own project file. The root project's was left behind by the walk; anything else with this extension is
				//a library under Libraries/, and it is the whole of what makes that library more than files sitting on the disk.
				case ".sbproj":
					PortLibraryProject( file, relative, destination, report );
					break;

				//A level, which is the one thing in a s&box project with nowhere of its own to go. It becomes a prefab -
				//see SBoxMaps for why that is the right shape rather than a scene of its own or a compiled map beside it.
				case ".vmap" when inAssets:
					if ( SBoxMaps.Port( file, relative, sourceAssets, assets, report ) )
						++report.MapsPorted;
					break;

				default:
					if ( SourceFormats.Contains( extension ) )
					{
						report.LeftBehind[extension] = report.LeftBehind.GetValueOrDefault( extension ) + 1;
						break;
					}

					//Where it lands, which is what everything below reopens.
					var to = Copy( file, Path.Combine( inAssets ? assets : destination, relative ) );

					//Everything else comes across untouched: shaders, art in open formats, settings, localisation, readmes
					++report.FilesCopied;

					var named = relative.Replace( '\\', '/' );

					//One measured object of a known type, converted with the scenes
					if ( PortNames.IsResource( extension ) )
					{
						scenes.Add( (to, named, file) );
					}
					else if ( PortNames.IsScene( extension ) )
					{
						//Names before measurements: the units table is keyed by this engine's type names, so a
						//scene still calling them Sandbox.* converts nothing and reports every number as unknown.
						report.TypeNamesRenamed += PortNames.RewriteTypesInFile( to, named, report );

						//Scene units (inches to metres) are converted later, after all source is read
						scenes.Add( (to, named, file) );
					}

					break;
			}
		}
		catch ( Exception e )
		{
			report.Skipped.Add( $"{relative}: {e.Message}" );
		}
	}

	//Ports what a previous port skipped: Assets first, then code outside Assets
	private static void FillFromSource( string sourceRoot, string destination, Project sourceProject, PortReport report )
	{
		var assets = Path.Combine( destination, "Assets" );
		var sourceAssets = PortPaths.FindDirectory( sourceRoot, "Assets" );

		//Empty rather than null is what a fresh report carries, so ??= never fired here and a fill reported no project file at all.
		if ( string.IsNullOrEmpty( report.ProjectFile ) )
		{
			report.ProjectFile = Directory.EnumerateFiles( destination, "*.rinproj" )
				.OrderBy( x => x, StringComparer.Ordinal ).FirstOrDefault() ?? "";
		}

		//The compiler block only, on a project that already exists. Its settings are the user's by now and are not overwritten - but which engine
		//the code is built against is not a setting anybody chose, it is something an earlier port carried over from theirs and got wrong.
		FixCompilerSettings( report.ProjectFile );

		if ( Directory.Exists( sourceRoot ) )
		{
			//Every Assets folder in the project rather than only the project's own - see AssetsRootOf.
			foreach ( var file in PortPaths.Walk( sourceRoot ) )
			{
				var ownSource = AssetsRootOf( file, sourceRoot );
				if ( ownSource is null ) continue;

				var relative = Path.GetRelativePath( ownSource, file );
				var ownAssets = Beside( destination, sourceRoot, ownSource );
				var extension = Path.GetExtension( file ).ToLowerInvariant();

				try
				{
					switch ( extension )
					{
						case ".vmdl":
							PortModel( file, relative, ownSource, ownAssets, report );
							break;

						case ".vmat":
							PortMaterial( file, relative, ownSource, ownAssets, report );
							break;

						case ".tmat":
							PortTerrainLayer( file, relative, ownSource, ownAssets, report );
							break;

						case ".vanmgrph":
							report.GraphsSeen.Add( relative.Replace( '\\', '/' ) );
							break;
					}
				}
				catch ( Exception e )
				{
					report.Skipped.Add( $"{relative}: {e.Message}" );
				}
			}
		}

		if ( !Directory.Exists( sourceRoot ) ) return;

		var scenes = new List<(string Path, string Named, string Source)>();

		foreach ( var file in PortPaths.Walk( sourceRoot ) )
		{
			//Anything under an Assets folder was dealt with above, whichever Assets folder it was.
			if ( AssetsRootOf( file, sourceRoot ) is not null ) continue;

			var fromRoot = Path.GetRelativePath( sourceRoot, file );

			if ( IsInSkippedDirectory( fromRoot ) ) continue;

			var extension = Path.GetExtension( file ).ToLowerInvariant();

			//Their build output, and the sidecars that are read rather than carried - see the first port, above.
			if ( IsCompiled( extension ) )
			{
				report.LeftBehind[extension] = report.LeftBehind.GetValueOrDefault( extension ) + 1;
				continue;
			}

			if ( PortPaths.IsBlobSidecar( file ) ) continue;

			//Same reason as the first port: the new project already has its own, and a library's is what lets its own code compile.
			if ( extension == ".sbproj" && !fromRoot.Contains( Path.DirectorySeparatorChar ) ) continue;

			PortWalkedFile( file, fromRoot, false, sourceAssets, assets, destination, report, scenes );
		}

		foreach ( var (scene, named, source) in scenes )
		{
			try
			{
				if ( SceneUnits.ConvertFile( scene, PortUnits.SBox, ForeignBasis.Quake, named, report ) )
					++report.ScenesConverted;

				//And its geometry - see the first port, above.
				Blobs( scene, source, named, report );

				Terrains( scene, source, named, report );

				//And a terrain painted whole with a material whose shader did not come across,
				//which otherwise draws as nothing at all rather than as the layers underneath it.
				SBoxTerrainMaterial.Strip( scene, assets, named, report );
			}
			catch ( Exception e )
			{
				report.Skipped.Add( $"{named}: {e.Message}" );
			}
		}
	}

	//Writes the list of numbers still in old units into the project, to work through over time
	private static void WriteUnitsNote( string destination, PortReport report )
	{
		if ( report.Unconverted.Count == 0 &&
			report.CustomShaders.Count == 0 && report.Graphs.Count == 0 &&
			report.EmbeddedAnimations.Count == 0 && report.Notes.Count == 0 &&
			report.Skipped.Count == 0 && report.MapsPorted == 0 ) return;

		var text = new System.Text.StringBuilder();

		//Port limitations go in the project notes, since launcher-created ports have no console
		if ( report.CustomShaders.Count > 0 )
		{
			text.AppendLine( "# Shaders" );
			text.AppendLine();
			text.AppendLine( "These are the project's own, written for the engine it came from. They came " +
				"across as they were, and nothing here can compile one: this engine's shaders are its own " +
				"format, bound its own way. Until each is rewritten, anything drawn with it draws nothing." );
			text.AppendLine();

			//Shader numbers can't be converted automatically; warn that they are in inches
			text.AppendLine( "Their unit is an inch and this engine's is a metre, so every number in one " +
				"of these that touches the world is wrong by about forty until it is changed. A **distance** " +
				"- how far a wave of noise drifts, where a fade begins - is multiplied by `0.0254`. A " +
				"**scale**, meaning a number the world position is multiplied by before anything is done " +
				"with it, goes the other way and is multiplied by `39.37`: that is a frequency, and a " +
				"frequency is one over a length. Numbers in texture coordinates, in normals, or in the cells " +
				"of a noise that has already been scaled are in none of those units and want leaving alone." );
			text.AppendLine();

			foreach ( var shader in report.CustomShaders )
				text.AppendLine( $"- `{shader}`" );

			text.AppendLine();
		}

		if ( report.EmbeddedAnimations.Count > 0 )
		{
			text.AppendLine( "# Animations with no source" );
			text.AppendLine();
			text.AppendLine( "These clips were read out of the compiled models themselves. That engine keeps a " +
				"character's poses inside its model and ships no export beside them, so these files are the " +
				"only copy of those animations there is. Deleting one loses it." );
			text.AppendLine();
			text.AppendLine( "They are written compiled, with no `.anim` beside them, for the same reason: " +
				"there is no art for one to name. Each model's clips sit in a folder next to it, and the " +
				"model's definition lists them." );
			text.AppendLine();

			foreach ( var (model, clips) in report.EmbeddedAnimations )
				text.AppendLine( $"- `{model}` — {clips} clip(s)" );

			text.AppendLine();
		}

		if ( report.Graphs.Count > 0 )
		{
			text.AppendLine( "# Animation graphs that did not come across" );
			text.AppendLine();
			text.AppendLine( "Graphs are translated node by node, and these are the ones that could not be. " +
				"A graph only their editor ever saved has nothing compiled to read; the rest are named " +
				"above with what became of them. Each of these has to be built again by hand." );
			text.AppendLine();

			foreach ( var graph in report.Graphs )
				text.AppendLine( $"- `{graph}`" );

			text.AppendLine();
		}

		if ( report.Skipped.Count > 0 )
		{
			text.AppendLine( "# What the port could not do" );
			text.AppendLine();
			text.AppendLine( "Each of these is something the port tried and did not manage. A file that would " +
				"not read, a resource with nothing to build it from. Nothing here stopped the port; each is " +
				"one thing that is missing from the project because of it." );
			text.AppendLine();

			foreach ( var skipped in report.Skipped ) text.AppendLine( $"- {skipped}" );

			text.AppendLine();
		}

		if ( report.Notes.Count > 0 )
		{
			text.AppendLine( "# What the port did that is worth knowing" );
			text.AppendLine();
			text.AppendLine( "Decisions rather than failures: what was replaced with an equivalent, what was " +
				"built rather than carried, what came out of a file that had no other copy." );
			text.AppendLine();

			foreach ( var note in report.Notes ) text.AppendLine( $"- {note}" );

			text.AppendLine();
		}

		if ( report.MapsPorted > 0 )
		{
			text.AppendLine( "# Levels" );
			text.AppendLine();
			text.AppendLine( $"{report.MapsPorted} Hammer map(s) came across as prefabs, beside where the `.vmap` " +
				"was. There is no map format on this side and nothing here compiles one: a level is a prefab, so " +
				"it drops into whatever scene you want it in, alongside anything else already there, as many " +
				"times as you like. The brushwork is an editable mesh, not baked geometry, so it can still be " +
				"cut about." );
			text.AppendLine();
			text.AppendLine( $"Where a scene named its level through a `MapInstance`, the level itself is in it " +
				$"now: the component is gone and the prefab stands under the object that carried it, which is " +
				$"where that component put the map. {report.MapsPlaced} of those." );
			text.AppendLine();

			if ( report.MapsPlaced < report.MapsPorted )
			{
				text.AppendLine( "Anything above that says a map was named and not found is one this could not " +
					"do: a map package off their site, a level whose file the port never saw, or a scene that " +
					"took its map from the launch arguments. Those objects are empty now and the level has to " +
					"be dropped in by hand." );
				text.AppendLine();
			}
			text.AppendLine( "Anything above that says an entity was kept as an empty object is a classname this " +
				"engine has no component for, and each of those is somebody's afternoon." );
			text.AppendLine();
		}

		text.AppendLine( "# Units" );
		text.AppendLine();
		text.AppendLine( "This project measured in inches. This engine measures in metres." );
		text.AppendLine();
		text.AppendLine( $"The port converted every number it could name: {report.ScenesConverted} scene(s) and " +
			$"prefab(s) and {report.MapsPorted} map(s). Code is not ported: it is rewritten by hand, in metres." );
		text.AppendLine();

		if ( report.Unconverted.Count == 0 )
		{
			text.AppendLine( "Nothing was left unnamed." );
		}
		else
		{
			text.AppendLine( $"## Left alone ({report.Unconverted.Count})" );
			text.AppendLine();
			text.AppendLine( "Each of these is a number the port could not prove the meaning of. It is still in " +
				"the old units if it was a measurement at all - and plenty of them are counts, angles and " +
				"fractions, which are already right." );
			text.AppendLine();

			foreach ( var number in report.Unconverted )
			{
				text.AppendLine( $"- `{number.Where}` **{number.What}** — {number.Why}" );
				text.AppendLine( $"  ```" );
				text.AppendLine( $"  {number.Value}" );
				text.AppendLine( $"  ```" );
			}
		}

		try
		{
			File.WriteAllText( Path.Combine( destination, "PORT.md" ), text.ToString() );
		}
		catch ( Exception e )
		{
			report.Skipped.Add( $"PORT.md: could not write the port's note - {e.Message}" );
		}
	}

	//Their standard shaders we ship under the same names; others are kept as the project's own
	private static readonly HashSet<string> StandardShaders = new( StringComparer.OrdinalIgnoreCase )
	{
		"complex", "simple", "vr_standard", "generic",
		"blendable", "glass", "unlit", "sprite", "spritecard",
		"vertex_color", "sky",

		//A character's two. This engine answers both with its standard surface - see CoreShaderFile.Aliases -
		//so they are not the project's own and do not belong in the list of shaders somebody has to rewrite.
		"skin", "eyeball",
	};

	//Ours, under the name theirs had.
	private static string OurShader( string name ) => Named( $"shaders/{name}{Extension}" );

	//What a shader file is called here, both theirs and ours.
	private const string Extension = ".shader";

	//Shader path as a ported material names it; keeps the .shader name, unrewritten ones draw missing
	private static string Named( string shader )
	{
		if ( string.IsNullOrWhiteSpace( shader ) ) return "";

		var written = shader.Replace( '\\', '/' );

		//Only the compiled spelling is rewritten - a name that ends in something else is a name somebody chose, and it is not this function's
		//business to correct it. A name with no extension at all is one of ours named bare, which is the one case there is nothing to keep.
		if ( written.EndsWith( ".shader_c", StringComparison.OrdinalIgnoreCase ) ) return written[..^2];

		return Path.GetExtension( written ).Length == 0 ? written + Extension : written;
	}

	//Pulls quoted key/value pairs from a text asset; the authored paths only exist in the file
	internal static List<KeyValuePair<string, string>> ReadAuthoredValues( string file )
	{
		var values = new List<KeyValuePair<string, string>>();

		try
		{
			foreach ( var line in File.ReadLines( file ) )
			{
				var match = AuthoredValue.Match( line );
				if ( !match.Success ) continue;

				values.Add( new KeyValuePair<string, string>(
					match.Groups["key"].Value, match.Groups["value"].Value ) );
			}
		}
		catch ( Exception )
		{
			//A file that cannot be read is reported by whatever tried to use it.
		}

		return values;
	}

	//A value is quoted, or a bare word as features are written: F_ALPHA 1 // On
	private static readonly System.Text.RegularExpressions.Regex AuthoredValue = new(
		"^\\s*\"?(?<key>[A-Za-z_][A-Za-z0-9_]*)\"?\\s*=?\\s*(?:\"(?<value>[^\"]*)\"|(?<value>[^\\s\"/{}]+))",
		System.Text.RegularExpressions.RegexOptions.Compiled );

	internal static string Authored( List<KeyValuePair<string, string>> values, string key )
	{
		foreach ( var pair in values )
		{
			if ( string.Equals( pair.Key, key, StringComparison.OrdinalIgnoreCase ) )
				return pair.Value.Replace( '\\', '/' ).TrimStart( '/' );
		}

		return "";
	}

	//Whether any part of this path is a directory we do not carry over.
	private static bool IsInSkippedDirectory( string relative )
	{
		foreach ( var part in relative.Split( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ) )
		{
			if ( SkippedDirectories.Contains( part, StringComparer.OrdinalIgnoreCase ) )
				return true;
		}

		return false;
	}

	//Project settings carried over (tick rate, resources, deps); identity is deliberately not
	private static readonly string[] CarriedSettings =
	{
		"Resources", "PackageReferences", "EditorReferences", "Mounts",
		"IncludeSourceFiles", "Metadata",
	};

	private static void CarryOverSettings( Project source, string projectFile )
	{
		var sourceFile = source?.ConfigFilePath;
		if ( string.IsNullOrEmpty( sourceFile ) || !File.Exists( sourceFile ) ) return;

		try
		{
			var from = JsonNode.Parse( File.ReadAllText( sourceFile ) )?.AsObject();
			var to = JsonNode.Parse( File.ReadAllText( projectFile ) )?.AsObject();

			if ( from is null || to is null ) return;

			foreach ( var key in CarriedSettings )
			{
				if ( !from.TryGetPropertyValue( key, out var value ) || value is null ) continue;
				to[key] = value.DeepClone();
			}

			//Fix the compiler metadata's engine, as already done for libraries
			OursToCompileWith( to["Metadata"]?["Compiler"] as JsonObject );

			File.WriteAllText( projectFile, to.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );
		}
		catch ( Exception e )
		{
			//A project that opens with default settings beats one that does not open at all.
			log.Warning( $"could not carry settings over from {sourceFile}: {e.Message}" );
		}
	}

	//Correct the compiler block of a project that is being filled rather than written, leaving everything else in it alone.
	private static void FixCompilerSettings( string projectFile )
	{
		if ( string.IsNullOrEmpty( projectFile ) || !File.Exists( projectFile ) ) return;

		try
		{
			var config = JsonNode.Parse( File.ReadAllText( projectFile ) )?.AsObject();
			if ( config?["Metadata"]?["Compiler"] is not JsonObject compiler ) return;

			var before = compiler.ToJsonString();

			OursToCompileWith( compiler );

			if ( compiler.ToJsonString() == before ) return;

			File.WriteAllText( projectFile, config.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );
		}
		catch ( Exception e )
		{
			//Said rather than thrown: the rest of the fill is worth having.
			log.Warning( $"could not correct the compiler settings in {projectFile}: {e.Message}" );
		}
	}

	//Points compiler settings at our engine: root namespace, and SANDBOX defines renamed to RINTEN
	private static void OursToCompileWith( JsonObject compiler )
	{
		if ( compiler is null ) return;

		if ( compiler["RootNamespace"]?.GetValue<string>() == "Sandbox" )
			compiler["RootNamespace"] = "Rinten";

		if ( compiler["DefineConstants"]?.GetValue<string>() is { } defines && defines.Contains( "SANDBOX" ) )
			compiler["DefineConstants"] = defines.Replace( "SANDBOX", "RINTEN" );
	}

	//Give the ported assets a project to belong to, so it can be opened and listed like any
	//other. A directory of assets with no project file is not something anything can pick up.
	private static string WriteProjectFile( string destination, Project source )
	{
		//The project creator makes one before asking for the port, and it knows more about what the user wanted
		//than this does - but it knows nothing about the project being ported, so its settings are merged in.
		var existing = Directory.EnumerateFiles( destination, Project.ExtensionSearch )
			.OrderBy( x => x, StringComparer.Ordinal ).FirstOrDefault();
		if ( existing is not null )
		{
			CarryOverSettings( source, existing );
			return existing;
		}

		var ident = Path.GetFileName( destination.TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ) );
		if ( string.IsNullOrWhiteSpace( ident ) ) ident = "ported";

		//An ident has to be a plain lowercase name; a directory name need not be.
		var safe = new string( ident.Where( char.IsLetterOrDigit ).ToArray() ).ToLowerInvariant();
		if ( string.IsNullOrEmpty( safe ) ) safe = "ported";

		var config = new
		{
			Title = source?.Config?.Title ?? ident,
			Type = "game",
			Org = source?.Config?.Org ?? "local",
			Ident = safe,
			Schema = 1,
			IncludeSourceFiles = false,
			PackageReferences = Array.Empty<string>(),
		};

		var path = Path.Combine( destination, $"{safe}{Project.Extension}" );
		File.WriteAllText( path, JsonSerializer.Serialize( config, new JsonSerializerOptions { WriteIndented = true } ) );

		//Startup scene, compiler settings and the rest come from theirs, same as for a file the creator made
		CarryOverSettings( source, path );

		return path;
	}

	//-- libraries ------------------------------------------------------------

	//Rewrites a library's .sbproj as our project file so it's found and compiled; identity stays
	private static void PortLibraryProject( string file, string relative, string destination, PortReport report )
	{
		var to = Path.ChangeExtension( Path.Combine( destination, relative ), Project.Extension );
		var named = relative.Replace( '\\', '/' );

		try
		{
			var config = JsonNode.Parse( File.ReadAllText( file ) )?.AsObject();

			if ( config is null )
			{
				report.Skipped.Add( $"{named}: could not be read, so its library will not be compiled" );
				return;
			}

			//Their compiler's, and wrong here in a way nothing would report: every type the library declares would
			//land under their root namespace rather than ours.
			OursToCompileWith( config["Metadata"]?["Compiler"] as JsonObject );

			Directory.CreateDirectory( Path.GetDirectoryName( to ) );
			File.WriteAllText( to, config.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );

			++report.FilesCopied;

			var title = config["Title"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension( file );
			report.Notes.Add( $"{named}: came across as a library of its own, so its code is compiled with the project ({title})" );
		}
		catch ( Exception e )
		{
			//Said rather than thrown: the rest of the port is worth having, and a library that did not arrive is exactly what has to be read.
			report.Skipped.Add( $"{named}: could not be written as a library - {e.Message}" );
		}
	}

	//-- animation graphs -----------------------------------------------------

	//Translates the graph a model names into one embedded in the model, or null
	private static ModelDefinition.ModelGraph EmbeddedGraph( string file, string relative, string sourceAssets,
		PortReport report, IEnumerable<string> roots )
	{
		if ( SBoxAnimGraph.NamedBy( file ) is not { Length: > 0 } named ) return null;

		var owner = Path.ChangeExtension( relative, ".mdl" ).Replace( '\\', '/' );

		report.GraphOwners.TryAdd( named, owner );

		if ( !report.GraphDocuments.TryGetValue( named, out var document ) )
		{
			var source = FindGraph( named, sourceAssets, roots );

			if ( source is null )
				report.Skipped.Add( $"{relative}: names the graph {named}, which is nowhere this port can read" );
			else
				document = SBoxAnimGraph.Translate( source, named, owner, report, out _ );

			if ( document is null && source is not null )
				report.Graphs.Add( named );

			report.GraphDocuments[named] = document;
		}

		return document is null ? null : new ModelDefinition.ModelGraph( document );
	}

	//Says so of every graph in their project that no model named. A graph comes across inside the model it animates and nowhere else, so
	//one that no model names has nowhere to go. Listed with the graphs that need a person rather than dropped quietly: somebody drew it.
	private static void UnownedGraphs( PortReport report )
	{
		foreach ( var graph in report.GraphsSeen.OrderBy( x => x, StringComparer.Ordinal ) )
		{
			if ( report.GraphOwners.ContainsKey( graph ) ) continue;

			report.Graphs.Add( graph );
			report.Notes.Add( $"{graph}: no model names this graph, and a graph comes across inside the model " +
				"it animates - it was left behind" );
		}
	}

	//Their graph file for the name a model gives it, wherever the port can see one. The name as the model spells it, which is not
	//always the file's own on a disk that minds the case - so a folder is searched without it where the exact name is not there.
	private static string FindGraph( string named, string sourceAssets, IEnumerable<string> roots )
	{
		var places = new List<string> { sourceAssets };
		if ( roots is not null ) places.AddRange( roots );

		foreach ( var root in places )
		{
			if ( string.IsNullOrWhiteSpace( root ) || !Directory.Exists( root ) ) continue;

			var exact = Path.Combine( root, named.Replace( '/', Path.DirectorySeparatorChar ) );
			if ( File.Exists( exact ) ) return exact;

			if ( AnyCase( root, named ) is { } found ) return found;
		}

		return null;
	}

	//A file under a folder by a relative name, matching each part of it without the case.
	private static string AnyCase( string root, string relative )
	{
		var at = root;

		foreach ( var part in relative.Split( '/', '\\', StringSplitOptions.RemoveEmptyEntries ) )
		{
			if ( !Directory.Exists( at ) ) return null;

			var next = Directory.EnumerateFileSystemEntries( at )
				.FirstOrDefault( x => string.Equals( Path.GetFileName( x ), part, StringComparison.OrdinalIgnoreCase ) );

			if ( next is null ) return null;

			at = next;
		}

		return File.Exists( at ) ? at : null;
	}

	//-- models ---------------------------------------------------------------

	//graphRoots: where else the graph the model names may be, beside its own project - the install's shelves, for a model carried from there.
	internal static void PortModel( string file, string relative, string sourceAssets, string assets, PortReport report,
		IEnumerable<string> graphRoots = null )
	{
		var resource = relative.Replace( '\\', '/' );
		var target = Path.Combine( assets, Path.ChangeExtension( relative, ".mdl" ) );
		Directory.CreateDirectory( Path.GetDirectoryName( target ) );

		var assigned = SBoxMaterials.Read( file );
		var remaps = assigned.Remaps;
		//Every export the document names, with the scale, offset and turn it names beside each - all of it
		//already in our units, and already carrying whatever their modifier list resizes the whole model by.
		var authored = SBoxMeshes.Read( file );

		//Which axes the numbers this document writes against its own bones are already in, which is decided by the art the skeleton
		//comes out of - see SBoxSkeleton. Everything below that is a place on a bone rather than a place in the model is read through it.
		var bones = SBoxSkeleton.BoneAxes( authored.ConvertAll( part => part.File ) );

		//The other half of that modifier. A flip is not a size and nothing here flips a model yet, so it is said
		//out loud rather than dropped: a model that comes across unmirrored is a left hand where a right one was.
		if ( SBoxModelModifiers.Read( file ) is { } modifier && (modifier.Mirrors || modifier.SwapLeftRight) )
		{
			report.Notes.Add( $"{relative}: its document mirrors the model" +
				( modifier.SwapLeftRight ? " and swaps its left and right bones" : "" ) +
				" - ported as it was drawn" );
		}

		//What the model is collided with, as their document says - which is nothing at all for a model whose artist never gave it a shape.
		var hulls = SBoxHulls.Read( file, bones,
			named => CarriedExport( named, file, sourceAssets, assets, relative, report ),
			note => report.Notes.Add( $"{relative}: {note}" ) );

		//A hull's own art goes through the same reader as the meshes, so its scale is measured the same way: what their document asked for,
		//over what one unit of the file turns out to be worth. Only knowable here, where the file has been carried across and can be looked at.
		foreach ( var hull in hulls )
		{
			if ( hull is not ModelDefinition.RenderHull { HasFile: true } shape ) continue;

			//Read where it is now: an export inside their assets is copied across only after the models
			var named = shape.File.Replace( '/', Path.DirectorySeparatorChar );
			var art = Path.Combine( sourceAssets, named );

			if ( !File.Exists( art ) ) art = Path.Combine( assets, named );
			if ( !File.Exists( art ) ) continue;

			shape.Scale = SBoxMeshes.ScaleFor( shape.Scale, art );
		}

		//The meshes among those that are only there to be collided with: a crude shape beside the art, or the level of detail nobody sees.
		//Listed as meshes as well, they were drawn as well - a world weapon came out as itself with its own low-poly copy laid over it.
		var shapes = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var hull in hulls )
		{
			if ( hull is ModelDefinition.RenderHull { HasFile: true } named )
				shapes.Add( Path.GetFileName( named.File ) );
		}

		var parts = new List<ModelDefinition.MeshPart>();

		foreach ( var authoredPart in authored )
		{
			var found = FindExport( authoredPart.File, file, sourceAssets );
			if ( found is null )
			{
				NoMesh( authoredPart.File, null, relative, report );

				//Keep missing-art parts but disabled, so the model is still written and can be fixed later
				parts.Add( new ModelDefinition.MeshPart
				{
					Name = authoredPart.Name,
					File = authoredPart.File,
					Scale = authoredPart.Scale,
					Position = authoredPart.Position,
					Rotation = authoredPart.Rotation,
					Parent = authoredPart.Parent,
					Enabled = false,
				} );

				continue;
			}

			//Every format is in inches: same conversion plus the document's scale, offset and rotation
			var include = ResolveInclude( authoredPart, found );

			parts.Add( new ModelDefinition.MeshPart
			{
				//What their document calls this piece. Their body groups name their pieces, so a part that comes
				//across unnamed is a group that can switch nothing: a citizen wearing all five of its heads at once.
				Name = authoredPart.Name,
				File = CarryExport( found, sourceAssets, assets, relative, report ),
				Scale = SBoxMeshes.ScaleFor( authoredPart, found ),
				Position = authoredPart.Position,
				Rotation = authoredPart.Rotation,
				Parent = authoredPart.Parent,
				Include = include,
			} );

			if ( include is { Count: > 0 } )
			{
				report.Notes.Add( $"{relative}: {Path.GetFileName( found )}'s mesh filter keeps {include.Count} " +
					"of its meshes - the rest were never meant to be drawn here" );
			}
		}

		//Only where something else is left to draw: a model collided with by its own geometry
		//names one mesh under both headings, and that is the ordinary thing rather than this.
		if ( parts.Count > 1 && shapes.Count > 0 )
		{
			var drawn = parts.Where( part => !shapes.Contains( Path.GetFileName( part.File ) ) ).ToList();

			if ( drawn.Count > 0 && drawn.Count < parts.Count )
			{
				report.Notes.Add( $"{relative}: {parts.Count - drawn.Count} mesh(es) are hulls, not drawn" );
				parts = drawn;
			}
		}

		//Document-added bones (e.g. hold_R), passed to the compile and written into the definition
		var extra = SBoxBones.Read( file, sourceAssets, bones );

		var collision = new ModelDefinition
		{
			Hulls = hulls,
			Bones = extra,
			Meshes = parts,

			//Carried into both compiles below, so whichever of them builds the model puts the same material on it that the definition names.
			Material = Overriding( remaps, assigned.Default ),
		};

		var scale = 1.0f;
		string facingArt = null;

		var fromSource = parts.Count > 0 &&
			FromAuthoredMeshes( authored, file, sourceAssets, out scale, out facingArt );

		if ( !fromSource && parts.Count == 0 )
		{
			report.Skipped.Add( $"{relative}: would not load" );
			return;
		}

		if ( !fromSource )
		{
			report.Notes.Add( $"{relative}: names {parts.Count} mesh file(s) whose art is not in the " +
				"project - the definition lists them so they can be reimported" );
		}

		var materials = remaps.Values
			.Append( assigned.Default )
			.Where( material => !string.IsNullOrWhiteSpace( material ) )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToList();

		//And the same table written down by name, because the list above is a set and says nothing about which surface wears what. The compile above used the
		//table; a rebuild from the definition has only what is written in it, and rebuilt by position a weapon comes out with a scope's material on its barrel.
		var pairs = remaps
			.Select( pair => new ModelDefinition.SurfaceMaterial
			{
				Surface = Path.GetFileNameWithoutExtension( pair.Key ),
				Material = pair.Value,
			} )
			.GroupBy( pair => pair.Surface, StringComparer.OrdinalIgnoreCase )
			.Select( group => group.First() )
			.Where( pair => !string.IsNullOrWhiteSpace( pair.Surface ) )
			.ToList();

		//Facing fix on the model transform, so mesh, skeleton and clips turn together; rebuild them together
		var turn = SBoxFbxAxes.Turn( facingArt );

		if ( turn != 0.0f )
		{
			report.Notes.Add( $"{relative}: its art declares a front this engine calls " +
				$"{( turn == 180.0f ? "back" : "sideways" )} - the model is turned {turn:0}° to face forwards" );
		}

		var definition = new ModelDefinition
		{
			Meshes = parts,
			Scale = scale,

			Transform = new ModelDefinition.ModelTransform
			{
				Enabled = turn != 0.0f,
				Rotation = new Angles( 0.0f, turn, 0.0f ),
			},
			Materials = materials,
			MaterialRemaps = pairs,

			//Global default as our material override, computed like the compile so the two agree
			Material = Overriding( remaps, assigned.Default ),
			Hulls = hulls,
			Joints = SBoxJoints.Read( file, bones ),
			Clips = PortAnimations( file, relative, sourceAssets, assets, scale, report ),
			Graph = EmbeddedGraph( file, relative, sourceAssets, report, graphRoots ),

			//The five lists a document keeps beside its meshes. Each of these used to be read out of the file their compiler wrote;
			//each of them is written in the document all along, in the prefabs a model is assembled from - see SBoxModelLists.
			LookAtChains = SBoxModelLists.LookChains( file ),
			IkChains = SBoxModelLists.IkChains( file ),
			WeightLists = SBoxModelLists.WeightLists( file ),
			BodyGroups = SBoxModelLists.BodyGroups( file ),
			CoarseMeshes = SBoxModelLists.CoarseMeshes( file ),

			//Their attachment lists, followed through the prefabs they nearly always live in. Code asks for these by
			//name, so a model that comes across without them is a weapon with no muzzle and a hand with nothing in it.
			Attachments = SBoxAttachments.Read( file, sourceAssets, bones ),

			//The bones the model drives itself - a forearm's twist, an elbow's helper - which are worked out every frame rather than animated.
			Constraints = SBoxConstraints.Read( file, sourceAssets, bones ),

			//And the bones, so that recompiling the model here puts them back.
			Bones = extra,
		};

		//Their document said both, and one material over the whole model is all-or-nothing here. The table is the more specific of the two and
		//wins, but a surface it does not name then falls to the guesses rather than to what they chose, so the choice that was dropped is named.
		if ( pairs.Count > 0 && !string.IsNullOrWhiteSpace( assigned.Default ) )
		{
			report.Notes.Add( $"{relative}: names {assigned.Default} for every surface its table does not, " +
				"and one material here is over the whole model or none of it - the table came across and " +
				"that one did not." );
		}

		Write( target, definition );
		++report.ModelsPorted;
	}

	//Checks the named art exists and finds its rig scale; building is left to ModelDefinitionCompiler
	private static bool FromAuthoredMeshes( List<SBoxMeshes.Part> meshes, string modelFile, string sourceAssets,
		out float scale, out string source )
	{
		scale = 1.0f;
		source = null;

		if ( meshes is not { Count: > 0 } ) return false;

		//Unparented parts first, so attached accessories don't decide the model's units or facing
		foreach ( var part in meshes.Where( piece => string.IsNullOrWhiteSpace( piece.Parent ) )
			.Concat( meshes.Where( piece => !string.IsNullOrWhiteSpace( piece.Parent ) ) ) )
		{
			var found = FindExport( part.File, modelFile, sourceAssets );
			if ( found is null ) continue;

			//An smd is read by a reader of our own; everything else has to be something the importer knows.
			//A name that is neither is a mesh the definition still lists, for somebody to point at again.
			if ( Path.GetExtension( found ).ToLowerInvariant() != ".smd" && !CoreMeshImport.CanRead( found ) )
				continue;

			source = found;
			scale = SBoxMeshes.ScaleFor( part, found );
			break;
		}

		return source is not null;
	}

	//Whole-model material override, or empty if the surface table is used; table wins
	internal static string Overriding( Dictionary<string, string> remaps, string fallback )
		=> remaps is { Count: > 0 } ? "" : fallback ?? "";

	//Puts one material on every mesh in builder. Does nothing where there is none to put.
	private static void LayOneMaterial( CoreAssets.ModelBuilder builder, string material )
	{
		if ( string.IsNullOrWhiteSpace( material ) ) return;

		var count = builder.MeshCount;

		for ( var i = 0; i < count; i++ )
			builder.SetMeshMaterial( i, material );
	}

	//Drops meshes a part's filter excludes, matching what a rebuild from the definition does
	private static void KeepIncludedMeshes( CoreAssets.ModelBuilder builder, List<ModelDefinition.MeshPart> meshes, string assets )
	{
		if ( meshes is not { Count: > 0 } ) return;

		var keep = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var filtering = false;

		foreach ( var part in meshes )
		{
			if ( part is null || string.IsNullOrWhiteSpace( part.File ) ) continue;

			if ( part.Include is not { Count: > 0 } )
			{
				//Nothing named for this part, so everything it imported stays - which needs its own name list
				//too, or a model with one filtered part and one plain one loses the plain one's meshes as well.
				var source = Path.Combine( assets, part.File.Replace( '/', Path.DirectorySeparatorChar ) );
				if ( !File.Exists( source ) ) continue;

				foreach ( var name in CoreMeshImport.Catalog( source, part.Scale > 0.0f ? part.Scale : 1.0f ) )
					keep.Add( name );

				continue;
			}

			filtering = true;

			foreach ( var name in part.Include )
			{
				if ( !string.IsNullOrWhiteSpace( name ) ) keep.Add( name );
			}
		}

		if ( !filtering ) return;

		for ( var i = builder.MeshCount - 1; i >= 0; i-- )
		{
			var name = builder.MeshName( i );
			if ( !NamedMesh( keep, name ) ) builder.DropMesh( i );
		}
	}

	//Whether a mesh the builder holds is one of the names being kept - bare, because
	//Blender's own ".001" on a duplicate is not what the artist meant to distinguish.
	private static bool NamedMesh( HashSet<string> keep, string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return false;
		if ( keep.Contains( name ) ) return true;

		var bare = ModelImportCatalog.Bare( name );
		if ( keep.Contains( bare ) ) return true;

		foreach ( var held in keep )
		{
			if ( string.Equals( ModelImportCatalog.Bare( held ), name, StringComparison.OrdinalIgnoreCase ) ) return true;
			if ( string.Equals( ModelImportCatalog.Bare( held ), bare, StringComparison.OrdinalIgnoreCase ) ) return true;
		}

		return false;
	}

	//Finds an export on disk from the assets root, the model's folder, or an absolute path; or null
	//Looks in the source project, so results don't depend on copy order
	internal static string FindExport( string authored, string namedIn, string sourceAssets )
	{
		if ( string.IsNullOrWhiteSpace( authored ) ) return null;

		//Somebody's own machine, written in by whatever exported it. Either it is still there or the name is
		//worth nothing. Asked before the separators are swapped, because C:\ is not a rooted path to Linux.
		if ( PortPaths.IsRootedAnywhere( authored ) )
		{
			var written = authored.Replace( '\\', Path.DirectorySeparatorChar );

			return PortPaths.FindFile( written ) is { } rooted ? Path.GetFullPath( rooted ) : null;
		}

		var relative = PortPaths.Resource( authored ).Replace( '/', Path.DirectorySeparatorChar );

		//Case-insensitive lookup, since documents name files in whatever case was typed
		var fromAssets = PortPaths.FindFile( Path.Combine( sourceAssets, relative ) );
		if ( fromAssets is not null ) return Path.GetFullPath( fromAssets );

		//From the model's own folder, or from somewhere between it and the assets root. Stopped at the root rather than
		//walked to the top of the disk: a name that only matches something outside the project matches it by accident.
		var top = Path.GetFullPath( sourceAssets );
		var directory = Path.GetDirectoryName( Path.GetFullPath( namedIn ) );

		while ( !string.IsNullOrEmpty( directory ) )
		{
			if ( PortPaths.FindFile( Path.Combine( directory, relative ) ) is { } candidate )
				return Path.GetFullPath( candidate );

			if ( string.Equals( directory, top, StringComparison.OrdinalIgnoreCase ) ) break;

			directory = Path.GetDirectoryName( directory );
		}

		return null;
	}

	//Leaves the mesh field empty when the export is unusable, so the compiled geometry is used
	private static string NoMesh( string authored, string source, string modelRelative, PortReport report )
	{
		if ( string.IsNullOrWhiteSpace( authored ) ) return "";

		//Said either way, because the name is the only record of what the model was made of and it is about to stop being written down anywhere.
		report.Notes.Add( source is null
			? $"{modelRelative}: names {authored}, which is not in the project - the definition is written " +
				"with the name in it, for the art to be pointed at again"
			: $"{modelRelative}: {authored} is there but nothing of ours could read it - the definition is " +
				"written with the name in it, for the art to be pointed at again" );

		return "";
	}

	//The mesh names part's own filter allows, resolved against the file itself - so Include, which only
	//ever means "keep these", still gets the right answer out of a document that said "drop these" instead.
	private static List<string> ResolveInclude( SBoxMeshes.Part part, string found )
	{
		if ( part.FilterNames is not { Count: > 0 } ) return null;

		var all = CoreMeshImport.Catalog( found, part.Scale > 0.0f ? part.Scale : 1.0f );

		//Nothing to resolve against - an export nothing here can read, or one with no meshes in it. Their names are the best answer there is.
		if ( all.Count == 0 ) return part.FilterExcludesByDefault ? part.FilterNames.ToList() : null;

		if ( part.FilterExcludesByDefault )
		{
			var named = all.Where( name => Names( part.FilterNames, name ) ).ToList();

			//Their list names nothing this file actually holds. Written down as they said it rather
			//than as an empty filter, which would quietly keep the meshes they asked to drop.
			return named.Count > 0 ? named : part.FilterNames.ToList();
		}

		var kept = all.Where( name => !Names( part.FilterNames, name ) ).ToList();

		//Naming everything, or naming nothing that was actually in the file, is not a filter at all - it
		//is the whole thing, and Include says that by staying empty rather than by listing it all out.
		return kept.Count > 0 && kept.Count < all.Count ? kept : null;
	}

	//Filter name match that also accepts our per-material suffix (book matches book.book_shell)
	private static bool Names( IReadOnlyList<string> filter, string mesh )
	{
		if ( string.IsNullOrWhiteSpace( mesh ) ) return false;

		var bare = ModelImportCatalog.Bare( mesh );

		foreach ( var name in filter )
		{
			if ( string.IsNullOrWhiteSpace( name ) ) continue;

			if ( string.Equals( name, mesh, StringComparison.OrdinalIgnoreCase ) ) return true;

			var named = ModelImportCatalog.Bare( name );

			if ( string.Equals( named, bare, StringComparison.OrdinalIgnoreCase ) ) return true;

			//The node they named, with the material we split it by after it.
			if ( bare.Length > named.Length && bare.StartsWith( named, StringComparison.OrdinalIgnoreCase )
				&& bare[named.Length] == '.' )
			{
				return true;
			}
		}

		return false;
	}

	//One file their document names, as a path in the new project - copied in where their export was outside the assets. The name unchanged where there is
	//no such file. What a hull was built from is worth keeping written down even when the file did not come with the project: somebody can put it back.
	private static string CarriedExport( string named, string modelFile, string sourceAssets,
		string assets, string relative, PortReport report )
	{
		var found = FindExport( named, modelFile, sourceAssets );

		if ( found is null )
		{
			report.Notes.Add( $"{relative}: its collision is built from {named}, which is not in the project" );
			return named;
		}

		return CarryExport( found, sourceAssets, assets, relative, report );
	}

	//Export path from the assets root, copying it in first if it lives outside the source assets
	private static string CarryExport( string source, string sourceAssets, string assets,
		string beside, PortReport report )
	{
		if ( Under( source, sourceAssets, out var inside ) )
			return inside.Replace( '\\', '/' );

		var folder = Path.GetDirectoryName( beside ) ?? "";
		var carried = Path.Combine( folder, Path.GetFileName( source ) );
		var to = Path.Combine( assets, carried );

		//Once. A model and every clip that plays on it can name the same export, and the number in the report is meant to be a number of files.
		if ( !File.Exists( to ) )
		{
			Copy( source, to );
			++report.FilesCopied;

			report.Notes.Add( $"{beside}: its export was outside the project's assets ({source}) - copied in beside it" );
		}

		return carried.Replace( '\\', '/' );
	}

	//Whether a file is inside a directory, and where in it.
	private static bool Under( string file, string root, out string relative )
	{
		relative = Path.GetRelativePath( root, file );

		return !Path.IsPathRooted( relative ) &&
			relative != ".." &&
			!relative.StartsWith( ".." + Path.DirectorySeparatorChar, StringComparison.Ordinal );
	}

	//Mesh files a model can have been built from.
	private static readonly string[] MeshSources = { ".fbx", ".gltf", ".glb", ".obj", ".smd", ".dmx", ".dae" };

	internal static List<string> AuthoredMeshes( string file )
	{
		var found = new List<string>();
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var pair in ReadAuthoredValues( file ) )
		{
			if ( !string.Equals( pair.Key, "filename", StringComparison.OrdinalIgnoreCase ) ) continue;

			var extension = Path.GetExtension( pair.Value ).ToLowerInvariant();
			if ( !MeshSources.Contains( extension ) ) continue;
			if ( !seen.Add( pair.Value ) ) continue;

			found.Add( pair.Value );
		}

		return found;
	}

	internal static string AuthoredMesh( string file )
	{
		foreach ( var pair in ReadAuthoredValues( file ) )
		{
			var extension = Path.GetExtension( pair.Value ).ToLowerInvariant();
			if ( MeshSources.Contains( extension ) ) return pair.Value;
		}

		return "";
	}

	//-- animations --- Models declare their animations as a named list of exports

	//One clip a model declares.
	internal readonly record struct AnimationEntry(
		string Name,
		string Source,
		bool Looping,
		bool Additive,
		int Take,
		int FirstFrame,
		int LastFrame,
		bool Reverse )
	{
		/// <summary>What the artist marked along it.</summary>
		public IReadOnlyList<ModelDefinition.ModelClip.Event> Events { get; init; }

		/// <summary>The animation a frame of which every frame of this is measured against.</summary>
		public string SubtractFrom { get; init; }

		/// <summary>Which frame of it, or below zero where this is not a difference.</summary>
		public int SubtractFrame { get; init; }

		/// <summary>Whether it is measured where the bone stands rather than under its parent.</summary>
		public bool SubtractModelSpace { get; init; }

		/// <summary>The bone their processing takes the motion out of, if any.</summary>
		public string MotionBone { get; init; }

		/// <summary>Whether the motion across the ground comes out of it.</summary>
		public bool MotionAcross { get; init; }

		/// <summary>And the motion up and down.</summary>
		public bool MotionUp { get; init; }

		/// <summary>And the turn.</summary>
		public bool MotionTurn { get; init; }

		/// <summary>The animation this one's frame is lined up with.</summary>
		public string AlignTo { get; init; }

		/// <summary>Which frame of this one, and which of the other.</summary>
		public int AlignFrame { get; init; }

		public int AlignToFrame { get; init; }

		/// <summary>The bone that is lined up.</summary>
		public string AlignBone { get; init; }

		/// <summary>Whether across the ground, and whether up and down.</summary>
		public bool AlignAcross { get; init; }

		public bool AlignUp { get; init; }

		/// <summary>How many frames at each end are brought together, or none.</summary>
		public int SmoothFromStart { get; init; }

		public int SmoothFromEnd { get; init; }

		/// <summary>A difference laid over every frame of this, or nothing.</summary>
		public string LayerFrom { get; init; }

		/// <summary>How far the whole clip is turned about the model's upright.</summary>
		public float TurnDegrees { get; init; }

		/// <summary>The rate their document plays it at, or below zero for the export's own.</summary>
		public float FrameRate { get; init; }

		/// <summary>The frame a loop goes back to, or the first.</summary>
		public int LoopStart { get; init; }
	}

	//Reads a model's declared animations from the text, following referenced lists
	//Not a KV3 parser: tracks braces and reads only the keys an AnimFile needs
	internal static (List<AnimationEntry> Clips, List<string> Composites) ReadAnimations(
		string modelFile, string assetsRoot )
	{
		var found = new List<AnimationEntry>();
		var composites = new List<string>();
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		//Which processing prefabs are being pulled into a clip right now, so a prefab that names itself cannot recurse for ever.
		//Not the "seen" set below: the same processing prefab belongs to thirty clips and has to be read into every one of them.
		var inlining = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		//The lists each document being read points at, one list per document: a block closes while its own file is the one
		//on top, and a prefab is either a list of the model's or a clip's own processing depending on what encloses it.
		var pending = new List<List<string>>();

		Read( modelFile, null );

		return (found, composites);

		void Read( string file, Block outer )
		{
			if ( string.IsNullOrEmpty( file ) ) return;

			//A list a model references is read once; a processing prefab is read into each clip that names it, and its own guard is above.
			if ( outer is null && !seen.Add( Path.GetFullPath( file ) ) ) return;
			if ( !File.Exists( file ) ) return;

			var stack = new List<Block>();
			var referenced = new List<string>();

			pending.Add( referenced );

			try
			{
				foreach ( var raw in File.ReadLines( file ) )
				{
					var line = raw.Trim();
					if ( line.Length == 0 ) continue;

					if ( line[0] == '{' )
					{
						stack.Add( new Block { Held = found.Count, HeldComposites = composites.Count } );
						continue;
					}

					if ( line[0] == '}' )
					{
						if ( stack.Count == 0 ) continue;

						var node = stack[^1];
						stack.RemoveAt( stack.Count - 1 );

						var above = stack.Count > 0 ? stack[^1] : outer;

						//A prefab read into a clip is a whole document, with a document block and a root node between its contents and the clip
						//that named it. Neither of those is a thing the clip has, so what they hold belongs to the clip rather than to them.
						if ( outer is not null && above is not null && !ReferenceEquals( above, outer ) &&
							above.Fields.GetValueOrDefault( "_class", "" ) is "" or "RootNode" )
						{
							above = outer;
						}

						Close( node, above );
						continue;
					}

					if ( stack.Count == 0 ) continue;

					var match = Field.Match( line );
					if ( !match.Success ) continue;

					var key = match.Groups["key"].Value;
					var value = match.Groups["value"].Value.Trim().Trim( ',' ).Trim();

					//Quoted or not - a name is quoted, a flag is not.
					if ( value.Length >= 2 && value[0] == '"' && value[^1] == '"' ) value = value[1..^1];

					stack[^1].Fields[key] = value;

				}
			}
			catch ( IOException )
			{
				//Reported by whoever asked for the port; an unreadable file is not worth taking the whole port down for.
				pending.RemoveAt( pending.Count - 1 );
				return;
			}

			pending.RemoveAt( pending.Count - 1 );

			foreach ( var reference in referenced )
			{
				if ( string.IsNullOrWhiteSpace( reference ) ) continue;
				if ( !reference.EndsWith( ".vmdl_prefab", StringComparison.OrdinalIgnoreCase ) ) continue;

				Read( Path.Combine( assetsRoot, reference.Replace( '/', Path.DirectorySeparatorChar ) ), null );
			}
		}

		//A block has ended: it is either a clip, something marked on the one above it, or a bag of keys belonging to that one.
		void Close( Block node, Block parent )
		{
			//Disabled entries are dropped along with everything read inside them
			if ( Flag( node, "disabled" ) )
			{
				if ( found.Count > node.Held ) found.RemoveRange( node.Held, found.Count - node.Held );

				if ( composites.Count > node.HeldComposites )
					composites.RemoveRange( node.HeldComposites, composites.Count - node.HeldComposites );

				return;
			}

			if ( !node.Fields.TryGetValue( "_class", out var kind ) )
			{
				//An unnamed block is a set of keys belonging to whatever holds it - which is how an event's arguments are written.
				if ( parent is null ) return;

				foreach ( var (key, value) in node.Fields ) parent.Fields.TryAdd( key, value );
				return;
			}

			if ( kind == "AnimEvent" )
			{
				if ( parent is null ) return;

				var marked = Marked( node );
				if ( marked is not null ) parent.Events.Add( marked );

				return;
			}

			//A prefab inside a clip holds that clip's processing (footsteps, align, motion extraction)
			if ( kind == "Prefab" &&
				node.Fields.TryGetValue( "target_file", out var inside ) &&
				inside.EndsWith( ".vmdl_prefab", StringComparison.OrdinalIgnoreCase ) )
			{
				var path = Path.Combine( assetsRoot, inside.Replace( '/', Path.DirectorySeparatorChar ) );

				//A prefab that is not a clip's own is a list of the model's, followed once the document
				//naming it has been read through - which is where the animation list itself comes from.
				if ( parent is null ||
					!parent.Fields.GetValueOrDefault( "_class", "" ).Equals( "AnimFile", StringComparison.OrdinalIgnoreCase ) )
				{
					if ( pending.Count > 0 ) pending[^1].Add( inside );
					return;
				}

				if ( inlining.Add( path ) )
				{
					Read( path, parent );
					inlining.Remove( path );
				}

				return;
			}

			//Motion extraction and loop start: cycles play in place and loop after their lead-in
			if ( kind == "AnimStartLoop" )
			{
				if ( parent is null ) return;

				parent.LoopStart = Number( node, "start_loop_frame", 0 );

				return;
			}

			//Offset clips are turned copies (e.g. diagonals); only the yaw is carried, shifts are noted
			if ( kind == "AnimOffset" )
			{
				if ( parent is null ) return;

				var turned = node.Fields.GetValueOrDefault( "rotation_xyz", "" );
				var numbers = turned.Trim( '[', ']', ' ' ).Split( ',' );

				if ( numbers.Length >= 3 &&
					float.TryParse( numbers[2].Trim(), System.Globalization.NumberStyles.Float,
						System.Globalization.CultureInfo.InvariantCulture, out var about ) )
				{
					parent.TurnDegrees = about;
				}

				return;
			}

			//Build-time additive layer, e.g. human proportions over borrowed citizen clips
			if ( kind == "AnimAddLayer" )
			{
				if ( parent is null ) return;

				parent.LayerFrom = node.Fields.GetValueOrDefault( "anim_name", "" );

				return;
			}

			//A cycle cut out of a longer take does not end where it began, and this says over how many frames at each end to bring the two
			//together. Their shotgun's reload loop is frames twelve to twenty of one animation, and without this it jumps every time round.
			if ( kind == "AnimSmoothLoop" )
			{
				if ( parent is null ) return;

				parent.SmoothFromStart = Number( node, "frames_from_start", 0 );
				parent.SmoothFromEnd = Number( node, "frames_from_end", 0 );

				return;
			}

			//Aligns the first frame to the idle pose before motion extraction
			if ( kind == "AnimAlign" )
			{
				if ( parent is null ) return;

				parent.AlignTo = node.Fields.GetValueOrDefault( "align_to_anim", "" );
				parent.AlignFrame = Number( node, "frame", 0 );
				parent.AlignToFrame = Number( node, "align_to_frame", 0 );
				parent.AlignBone = node.Fields.GetValueOrDefault( "bone", "" );
				parent.AlignAcross = Flag( node, "align_tx" ) || Flag( node, "align_ty" );
				parent.AlignUp = Flag( node, "align_tz" );

				return;
			}

			if ( kind == "ExtractMotion" )
			{
				if ( parent is null ) return;

				parent.MotionBone = node.Fields.GetValueOrDefault( "root_bone_name", "" );

				//Across the ground and up and down, rather than their three named axes: an export arrives here in this engine's
				//own space, where up is up, and the axis their document numbered is a fact about the space it was authored in.
				parent.MotionAcross = Flag( node, "extract_tx" ) || Flag( node, "extract_ty" );
				parent.MotionUp = Flag( node, "extract_tz" );
				parent.MotionTurn = Flag( node, "extract_rz" );

				return;
			}

			//Subtract child marks the parent clip as a delta, local or model space
			if ( kind is "AnimSubtract" or "AnimSubtractModelSpace" )
			{
				if ( parent is null ) return;

				parent.SubtractFrom = node.Fields.GetValueOrDefault( "anim_name", "" );
				parent.SubtractFrame = Number( node, "frame", 0 );
				parent.SubtractModelSpace = kind == "AnimSubtractModelSpace";

				return;
			}

			if ( kind != "AnimFile" )
			{
				//A sequence that is made of other sequences - a blend, a subtraction, a selector. It is a name the
				//game can ask for and there is no file behind it, so it is named for the report rather than ported.
				if ( Composite.Contains( kind ) && node.Fields.TryGetValue( "name", out var composite ) &&
					!string.IsNullOrWhiteSpace( composite ) )
				{
					composites.Add( $"{composite} ({kind})" );
				}

				return;
			}

			if ( !node.Fields.TryGetValue( "source_filename", out var source ) || string.IsNullOrWhiteSpace( source ) ) return;

			var name = node.Fields.GetValueOrDefault( "name", "" );
			if ( string.IsNullOrWhiteSpace( name ) ) name = Path.GetFileNameWithoutExtension( source );

			//Playback rate from the document, overriding the export's frame rate
			var rate = -1.0f;

			if ( node.Fields.TryGetValue( "framerate", out var written ) )
			{
				float.TryParse( written, System.Globalization.NumberStyles.Float,
					System.Globalization.CultureInfo.InvariantCulture, out rate );
			}

			found.Add( new AnimationEntry(
				name,
				source.Replace( '\\', '/' ),
				Flag( node, "looping" ),
				Flag( node, "delta" ),
				Number( node, "take", 0 ),
				Number( node, "start_frame", -1 ),
				Number( node, "end_frame", -1 ),
				Flag( node, "reverse" ) )
			{
				Events = node.Events,
				SubtractFrom = node.SubtractFrom,
				SubtractFrame = node.SubtractFrame,
				SubtractModelSpace = node.SubtractModelSpace,
				MotionBone = node.MotionBone,
				MotionAcross = node.MotionAcross,
				MotionUp = node.MotionUp,
				MotionTurn = node.MotionTurn,
				AlignTo = node.AlignTo,
				AlignFrame = node.AlignFrame,
				AlignToFrame = node.AlignToFrame,
				AlignBone = node.AlignBone,
				AlignAcross = node.AlignAcross,
				AlignUp = node.AlignUp,
				SmoothFromStart = node.SmoothFromStart,
				SmoothFromEnd = node.SmoothFromEnd,
				LayerFrom = node.LayerFrom,
				TurnDegrees = node.TurnDegrees,
				FrameRate = rate,
				LoopStart = node.LoopStart,
			} );
		}

		//What an AnimEvent becomes. Footsteps are the one kind worth understanding rather than carrying: a graph asks which foot is down, and that answer
		//is in the event's keys rather than in its name. Everything else keeps the name s&box gave it, because the game listening for it knows that name.
		static ModelDefinition.ModelClip.Event Marked( Block node )
		{
			var kind = node.Fields.GetValueOrDefault( "event_class", "" );
			if ( string.IsNullOrWhiteSpace( kind ) ) return null;

			var frame = Number( node, "event_frame", 0 );

			//A sound they play off the animation, which this engine plays its own way. Dropped rather
			//than carried under a name nothing listens for - see the port's own note about sounds.
			if ( Sound.Contains( kind ) ) return null;

			if ( !kind.Equals( "AE_FOOTSTEP", StringComparison.OrdinalIgnoreCase ) )
			{
				//What the game switches on. Their generic event is a class with the name inside it - AE_GENERIC_EVENT carrying TypeName
				//"reload_end" - and carrying the class across instead left every weapon in a project listening for a name nothing ever said.
				var named = node.Fields.GetValueOrDefault( "TypeName", "" );

				if ( string.IsNullOrWhiteSpace( named ) )
					named = node.Fields.GetValueOrDefault( "name", "" );

				return new ModelDefinition.ModelClip.Event
				{
					Id = string.IsNullOrWhiteSpace( named ) ? kind : named,
					Kind = ModelDefinition.ModelClip.EventKind.Generic,
					StartFrame = frame,
					Data = Number( node, "Int", 0 ),
				};
			}

			//"Foot" is which one: nothing else in the event says, and the attachment it names is what the game plays a sound on.
			var foot = node.Fields.GetValueOrDefault( "Foot", "0" );
			var attachment = node.Fields.GetValueOrDefault( "Attachment", "" );

			var right = foot.Trim() == "1";

			return new ModelDefinition.ModelClip.Event
			{
				Id = string.IsNullOrWhiteSpace( attachment ) ? ( right ? "foot_R" : "foot_L" ) : attachment,
				Kind = ModelDefinition.ModelClip.EventKind.Foot,
				StartFrame = frame,

				//The phases our graph knows, of which a s&box footstep is the "down" one - it marks the moment the foot lands.
				Data = right ? 2 : 0,
			};
		}

		static bool Flag( Block node, string key )
			=> node.Fields.TryGetValue( key, out var value ) && value.Equals( "true", StringComparison.OrdinalIgnoreCase );

		static int Number( Block node, string key, int fallback )
			=> node.Fields.TryGetValue( key, out var value ) && int.TryParse( value, out var parsed ) ? parsed : fallback;
	}

	//Their events that only play a sound, which this engine does not play this way.
	private static readonly HashSet<string> Sound = new( StringComparer.OrdinalIgnoreCase )
	{
		"AE_CL_PLAYSOUND", "AE_CL_PLAYSOUND_ATTACHMENT", "AE_CL_PLAYSOUND_POSITION",
		"AE_SV_PLAYSOUND", "SoundEvent",
	};

	//One { } of a model document, while it is being read.
	private sealed class Block
	{
		public Dictionary<string, string> Fields { get; } = new( StringComparer.OrdinalIgnoreCase );

		/// <summary>Clip count when this block opened, so a disabled folder can remove its children</summary>
		public int Held { get; set; }

		/// <summary>The same, for the sequences named but not ported.</summary>
		public int HeldComposites { get; set; }

		/// <summary>What was marked inside it.</summary>
		public List<ModelDefinition.ModelClip.Event> Events { get; } = new();

		/// <summary>What this one is measured against, where it is a difference.</summary>
		public string SubtractFrom { get; set; } = "";

		public int SubtractFrame { get; set; } = -1;

		/// <summary>Whether that difference is measured where the bone stands.</summary>
		public bool SubtractModelSpace { get; set; }

		/// <summary>The bone their processing takes the motion out of, if any.</summary>
		public string MotionBone { get; set; } = "";

		/// <summary>Whether it takes the motion across the ground out of it.</summary>
		public bool MotionAcross { get; set; }

		/// <summary>And the motion up and down.</summary>
		public bool MotionUp { get; set; }

		/// <summary>And the turn, which nothing here does yet.</summary>
		public bool MotionTurn { get; set; }

		/// <summary>The animation this one's first frame is lined up with.</summary>
		public string AlignTo { get; set; } = "";

		/// <summary>Which frame of this one is lined up.</summary>
		public int AlignFrame { get; set; }

		/// <summary>And which frame of the other it is lined up with.</summary>
		public int AlignToFrame { get; set; }

		/// <summary>The bone that is lined up.</summary>
		public string AlignBone { get; set; } = "";

		/// <summary>Whether it is lined up across the ground.</summary>
		public bool AlignAcross { get; set; }

		/// <summary>And up and down.</summary>
		public bool AlignUp { get; set; }

		/// <summary>How many frames at each end are brought together, or none.</summary>
		public int SmoothFromStart { get; set; }

		public int SmoothFromEnd { get; set; }

		/// <summary>A difference laid over every frame of this, or nothing.</summary>
		public string LayerFrom { get; set; } = "";

		/// <summary>How far the whole clip is turned about the model's upright.</summary>
		public float TurnDegrees { get; set; }

		/// <summary>The rate their document plays it at, or below zero for the export's own.</summary>
		public float FrameRate { get; set; } = -1.0f;

		/// <summary>The frame a loop goes back to, or the first.</summary>
		public int LoopStart { get; set; }
	}

	private static readonly System.Text.RegularExpressions.Regex Field = new(
		"^(?<key>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*(?<value>.*)$",
		System.Text.RegularExpressions.RegexOptions.Compiled );

	//Animation list nodes that are sequences made of other sequences. Nothing here has a file behind it, so nothing
	//here can be ported - but each one is a name the game may ask for, and going missing quietly is the problem.
	private static readonly string[] Composite =
	{
		"1DBlend", "2DBlend", "AnimSubtract", "AnimAdd", "Selector", "BlendList",
		"SingleFrame", "LayeredSequence", "Sequence",
	};

	//Write a clip definition for every animation a model declares, and hand back what to list on the model. Each one lands beside the export it reads,
	//which is where an artist looks for it, and is named after the animation rather than the file - two animations can be two slices of one export.
	private static List<ModelDefinition.ModelClip> PortAnimations( string modelFile, string relative,
		string sourceAssets, string assets, float scale, PortReport report )
	{
		var ported = new List<ModelDefinition.ModelClip>();
		var (entries, composites) = ReadAnimations( modelFile, sourceAssets );

		//Sequences built out of other sequences: named, because the game can ask for one
		//and a name that answers nothing is worse than a name that is known to be missing.
		if ( composites.Count > 0 )
		{
			report.Notes.Add( $"{relative}: {composites.Count} sequence(s) are built from others and were not ported - " +
				string.Join( ", ", composites.Take( 6 ) ) + ( composites.Count > 6 ? ", ..." : "" ) );
		}

		if ( entries.Count == 0 ) return ported;


		foreach ( var entry in entries )
		{
			//Only what our own importers read. A dmx or a dae still needs s&box to open it, and there is nothing to convert it with.
			if ( !CoreMeshImport.CanRead( entry.Source ) )
			{
				report.Skipped.Add( $"{relative}: '{entry.Name}' is a {Path.GetExtension( entry.Source ).ToLowerInvariant()} export" );
				continue;
			}

			var export = FindExport( entry.Source, modelFile, sourceAssets );

			if ( export is null )
			{
				report.Skipped.Add( $"{relative}: '{entry.Name}' names {entry.Source}, which is not in the project" );
				continue;
			}

			//Where that export lands in the ported project, which is what the clip below names. The clip itself is not built here: naming the export and the
			//take in the definition is what a build needs, and the build is this engine's - see ModelDefinitionCompiler, which runs when the project is opened.
			var carried = CarryExport( export, sourceAssets, assets, relative, report );

			if ( entry.Reverse )
				report.Notes.Add( $"{relative}: '{entry.Name}' plays in reverse - ported forwards" );

			//The export it was built from, not the compiled clip beside it - so a rebuild has something to build from, the same as any other take on the model.
			ported.Add( new ModelDefinition.ModelClip
			{
				File = carried,

				//Name is what their graph and code play; Take is picked by number, not the export's take name
				Name = entry.Name,
				Take = entry.Take,
				Scale = scale,
				Looping = entry.Looping,

				//A clip with a subtract source is additive regardless of its flag
				Additive = entry.Additive || !string.IsNullOrWhiteSpace( entry.SubtractFrom ),
				SubtractFrom = entry.SubtractFrom ?? "",
				SubtractFrame = entry.SubtractFrame,
				SubtractModelSpace = entry.SubtractModelSpace,

				//Which part of the export this clip is. Their documents cut several out of one file - the citizen's eight pelvis lean
				//poses are one frame each of the same eight-frame export - and carried whole they are eight copies of the same swing.
				FirstFrame = entry.FirstFrame,
				LastFrame = entry.LastFrame,

				//Motion bone; unnamed means the align bone, not just any root
				MotionBone = string.IsNullOrWhiteSpace( entry.MotionBone ) ? entry.AlignBone ?? "" : entry.MotionBone,
				MotionAcross = entry.MotionAcross,
				MotionUp = entry.MotionUp,

				//And where its first frame is put before that happens.
				AlignTo = entry.AlignTo ?? "",
				AlignFrame = entry.AlignFrame,
				AlignToFrame = entry.AlignToFrame,
				AlignBone = entry.AlignBone ?? "",
				AlignAcross = entry.AlignAcross,
				AlignUp = entry.AlignUp,

				//And how far in from each end its seam is smoothed.
				SmoothFromStart = entry.SmoothFromStart,
				SmoothFromEnd = entry.SmoothFromEnd,

				//And what is laid over it to put it on this model's body.
				LayerFrom = entry.LayerFrom ?? "",

				//And how far round it is turned, which is what makes a diagonal out of a cardinal.
				TurnDegrees = entry.TurnDegrees,

				//And how fast their document plays it.
				FrameRate = entry.FrameRate,

				//And which frame it goes back to when it comes round.
				LoopStart = entry.LoopStart,
				Events = entry.Events?.ToList() ?? new(),
			} );

			++report.AnimationsPorted;
		}

		//Animations only inside their compiled file are no longer extracted

		return ported;
	}

	//An animation's name as a file name. Names come from an artist and a file system is fussier than they are.
	private static string SafeFileName( string name )
	{
		var invalid = Path.GetInvalidFileNameChars();
		var safe = new char[name.Length];

		for ( var i = 0; i < name.Length; i++ )
			safe[i] = Array.IndexOf( invalid, name[i] ) >= 0 ? '_' : name[i];

		var result = new string( safe ).Trim();

		return string.IsNullOrEmpty( result ) ? "animation" : result;
	}

	//-- materials ------------------------------------------------------------

	//How s&box's standard shader names its parameters. Taken from the materials in a real project rather than from documentation.
	//Their terrain layer resource as a material on our terrain layer shader, beside where it was
	internal static void PortTerrainLayer( string file, string relative, string sourceAssets, string assets, PortReport report )
	{
		if ( JsonNode.Parse( File.ReadAllText( file ) ) is not JsonObject layer ) return;

		var resource = relative.Replace( '\\', '/' );

		string Picture( string key, string what )
			=> Carried( sourceAssets, layer[key]?.GetValue<string>(), resource, what, report );

		float Value( string key, float otherwise )
			=> layer[key] is JsonValue v && v.TryGetValue<double>( out var d ) ? (float)d : otherwise;

		static string Four( float x ) => $"{x.ToString( System.Globalization.CultureInfo.InvariantCulture )},0,0,0";

		var textures = new JsonObject();

		void Texture( string slot, string key, string what )
		{
			if ( Picture( key, what ) is { Length: > 0 } found ) textures[slot] = found;
		}

		Texture( "g_tRoughness", "RoughnessImage", "its roughness map" );
		Texture( "g_tHeight", "HeightImage", "its height map" );
		Texture( "g_tOcclusion", "AOImage", "its occlusion map" );

		var noTiling = layer["NoTiling"] is JsonValue t && t.TryGetValue<bool>( out var b ) && b;

		var material = new JsonObject
		{
			["Shader"] = "shaders/terrain_layer.shader",
			["Features"] = new JsonObject(),
			["Numbers"] = new JsonObject
			{
				["g_flUvScale"] = Four( Value( "UVScale", 1.0f ) ),
				["g_flNormalStrength"] = Four( Value( "NormalStrength", 1.0f ) ),
				["g_flHeightBlend"] = Four( Value( "HeightBlendStrength", 1.0f ) ),

				//Theirs is in inches like everything else
				["g_flDisplacement"] = Four( Value( "DisplacementScale", 0.0f ) * PortUnits.SBoxUnit ),
				["g_bNoTiling"] = Four( noTiling ? 1.0f : 0.0f ),
			},
			["Textures"] = textures,
			["Tint"] = "1,1,1,1",
			["ColorMap"] = Picture( "AlbedoImage", "its colour map" ) ?? "",
			["Normal"] = Picture( "NormalImage", "its normal map" ) ?? "",
			["Roughness"] = 1.0f,
			["Metalness"] = Value( "Metalness", 0.0f ),
			["EmissionStrength"] = 0.0f,
			["PhysicsSurface"] = layer["Surface"]?.GetValue<string>() ?? "",
			["AlphaCutoff"] = 0.5f,
		};

		var target = Path.Combine( assets, Path.ChangeExtension( relative, ".mat" ) );
		Directory.CreateDirectory( Path.GetDirectoryName( target ) );
		File.WriteAllText( target, material.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );

		++report.MaterialsPorted;
	}

	internal static void PortMaterial( string file, string relative, string sourceAssets, string assets,
		PortReport report )
	{
		var resource = relative.Replace( '\\', '/' );
		var authored = ReadAuthoredValues( file );
		var material = Material.Load( resource );

		if ( material is null && authored.Count == 0 )
		{
			report.Skipped.Add( $"{relative}: would not load" );
			return;
		}

		//Compared by name rather than by path: a shader is referenced as shaders/complex.shader and sometimes as the compiled shaders/complex.shader_c.
		var shader = Authored( authored, "shader" );
		if ( string.IsNullOrEmpty( shader ) ) shader = material?.ShaderName ?? "";

		shader = shader.Replace( ".shader_c", ".shader" );

		var shaderName = Path.GetFileNameWithoutExtension( shader );
		var isStandard = StandardShaders.Contains( shaderName );

		//Slots hold a texture or an [r g b a] constant; A-suffixed names are a blend's first layer
		var slots = SBoxMaterialSlots.Read( authored );

		var colour = SBoxMaterialSlots.Texture( slots, SBoxMaterialSlots.Slot.Colour, out var colourConstant );
		var normal = SBoxMaterialSlots.Texture( slots, SBoxMaterialSlots.Slot.Normal, out _ );
		var rough = SBoxMaterialSlots.Texture( slots, SBoxMaterialSlots.Slot.Roughness, out _ );
		var metal = SBoxMaterialSlots.Texture( slots, SBoxMaterialSlots.Slot.Metalness, out _ );
		var occlusion = SBoxMaterialSlots.Texture( slots, SBoxMaterialSlots.Slot.Occlusion, out _ );
		var emission = SBoxMaterialSlots.Texture( slots, SBoxMaterialSlots.Slot.Emission, out _ );

		//And what the other layers were, since they are being left behind. A material that quietly
		//drops three of its four surfaces is one somebody finds by wondering why the cliff is grass.
		var layers = SBoxMaterialSlots.Layers( slots );

		if ( layers > 1 )
		{
			report.Notes.Add( $"{resource}: a blended material of {layers} layers - the first is what came " +
				"across, because a material here holds one set of maps. The mesh keeps the weights it was " +
				"painted with, so the rest is waiting on the material rather than lost." );
		}

		//Roughness, metalness and occlusion are a picture each there and a picture each here, read in red - see complex.shader - so three separate maps
		//are carried as they are. One file named for all three is a packed map whose maps are its channels, and is split into three: see SBoxMaterialMaps.
		if ( !string.IsNullOrEmpty( rough ) && rough == metal && rough == occlusion )
		{
			var split = SBoxMaterialMaps.Split( Beside( sourceAssets, rough ), assets, resource );

			report.Notes.Add( split is null
				? $"{resource}: its roughness, metalness and occlusion are one picture, {rough}, which could " +
					"not be read - the material keeps its own numbers."
				: $"{resource}: its roughness, metalness and occlusion were one picture, split into three." );

			(rough, metal, occlusion) = split ?? ("", "", "");
		}
		else
		{
			rough = Carried( sourceAssets, rough, resource, "its roughness map", report );
			metal = Carried( sourceAssets, metal, resource, "its metalness map", report );
			occlusion = Carried( sourceAssets, occlusion, resource, "its occlusion map", report );
		}

		//Drop maps the source doesn't contain: an empty slot draws white, a missing file checkerboard
		colour = Carried( sourceAssets, colour, resource, "its colour map", report );
		normal = Carried( sourceAssets, normal, resource, "its normal map", report );
		emission = Carried( sourceAssets, emission, resource, "its emission map", report );

		//Under the names the shader asks for them by, which is what turns each one's feature on - see MaterialDefinition.Textures.
		var pictures = new Dictionary<string, string>();
		if ( !string.IsNullOrEmpty( rough ) ) pictures["g_tRoughness"] = rough;
		if ( !string.IsNullOrEmpty( metal ) ) pictures["g_tMetalness"] = metal;
		if ( !string.IsNullOrEmpty( occlusion ) ) pictures["g_tAO"] = occlusion;

		var definition = new MaterialDefinition
		{
			ColorMap = colour,
			Normal = normal,
			Emission = emission,

			Tint = Tint( material, colourConstant ),

			Textures = pictures,

			//Roughness/metalness constants or multipliers; metalness must be above 0 for its map to be read
			Roughness = SBoxMaterialSlots.Scalar( slots, SBoxMaterialSlots.Slot.Roughness )
				?? Clamp01( material?.GetVector4( "g_flRoughnessMultiplier" ).x ?? 0,
					string.IsNullOrEmpty( rough ) ? 0.5f : 1.0f ),

			Metalness = SBoxMaterialSlots.Scalar( slots, SBoxMaterialSlots.Slot.Metalness )
				?? ( string.IsNullOrEmpty( metal ) ? 0.0f : 1.0f ),

			EmissionStrength = material?.GetVector4( "g_flEmissiveIntensity" ).x ?? 0,

			//One of s&box's own shaders keeps the name it had, because this engine ships one under it. A shader the project wrote is its own HLSL, which nothing
			//here compiles - unless this engine happens to have one by the same name, which is what somebody rewriting one for a ported project produces.
			Shader = isStandard ? OurShader( shaderName ) : Named( Replacement( shader ) ?? shader ),

			//How it meets what is behind it and whether both its faces are drawn - features of the surface here, the same
			//as theirs: a cutout is F_ALPHA, see-through is F_TRANSLUCENT, two-sided is F_BACKFACES. See FeaturesOf.
			Features = FeaturesOf( authored ),

			//And where a masked one cuts the pixel out. Theirs is a reference the alpha is compared against;
			//one that asks for masking and names no number gets the half everything else here defaults to.
			AlphaCutoff = Clamp01( Number( authored, "g_flAlphaTestReference" ), 0.5f ),
		};

		if ( !isStandard && !string.IsNullOrEmpty( shader ) )
		{
			SBoxShaderParameters.Carry( definition, authored, Beside( sourceAssets, shader ),
				path => Carried( sourceAssets, path, resource, "a picture its shader reads", report ) );
		}

		var target = Path.Combine( assets, Path.ChangeExtension( relative, ".mat" ) );
		Directory.CreateDirectory( Path.GetDirectoryName( target ) );

		Write( target, definition );
		++report.MaterialsPorted;

		if ( !isStandard && !string.IsNullOrEmpty( shader ) )
		{
			//Pointed at this engine's own, which somebody wrote for exactly this material. Said out loud either
			//way: a swap nobody was told about is as hard to account for later as a shader that never drew.
			if ( Replacement( shader ) is { } ours2 )
				report.Notes.Add( $"{resource}: draws with {ours2} - this engine has one by that name, and its own cannot be compiled here." );

			//Otherwise the shader file comes across and nothing can compile it. Reported now, because a
			//material that looks wrong later is much harder to explain than one that was named at the time.
			else
				report.CustomShaders.Add( shader );
		}
	}

	/// <summary>Ports a material an install ships only compiled: its numbers read out of the .vmat_c, its generated pictures unpacked to png in the project.</summary>
	internal static bool PortCompiledMaterial( string compiled, string relative, string sourceRoot, string assets, PortReport report )
	{
		var values = CoreImportedMaterial.CompiledValues( compiled );
		if ( values is null ) return false;

		var text = new System.Text.StringBuilder( "Layer0\n{\n" );

		foreach ( var (key, value) in values )
		{
			var written = value;

			//A picture their compiler generated has no source; the compiled one is unpacked into the project as the source it stands for
			if ( written.EndsWith( ".vtex", StringComparison.OrdinalIgnoreCase ) )
			{
				written = "";

				var picture = PortPaths.FindFile( Path.Combine( sourceRoot, value + "_c" ) );

				if ( picture is not null && CoreImportedTexture.Load( picture ) is { } bitmap )
				{
					using ( bitmap )
					{
						var image = Path.ChangeExtension( value, ".png" ).Replace( '\\', '/' );
						var to = Path.Combine( assets, image );

						Directory.CreateDirectory( Path.GetDirectoryName( to ) );
						File.WriteAllBytes( to, bitmap.ToPng() );

						written = image;
					}
				}
			}

			text.Append( '"' ).Append( key ).Append( "\" \"" ).Append( written ).Append( "\"\n" );
		}

		text.Append( "}\n" );

		//Ported as the text material it was compiled from, its pictures now standing in the project rather than in the install
		var document = Path.Combine( assets, relative );
		Directory.CreateDirectory( Path.GetDirectoryName( document ) );
		File.WriteAllText( document, text.ToString() );

		try
		{
			PortMaterial( document, relative, assets, assets, report );
		}
		finally
		{
			File.Delete( document );
		}

		return true;
	}

	//Our shader with the same name if one has been written (a rewritten port), else null
	private static string Replacement( string shader )
	{
		if ( string.IsNullOrWhiteSpace( shader ) ) return null;

		var bare = $"shaders/{Path.GetFileNameWithoutExtension( shader )}";

		//Already one of ours by that path, which is what the standard ones map to and not something to map again.
		var written = shader.Replace( '\\', '/' );
		var ours = $"{bare}{Extension}";

		if ( written.Equals( ours, StringComparison.OrdinalIgnoreCase ) ) return null;

		//One spelling to look for: this engine's shaders are .shader files of its own format, and a rewrite of a ported shader lands as one of them.
		return CoreModel.ResolveOnDisk( ours ) is not null ? ours : null;
	}


	//[r g b a] as a colour, or null if it is not four numbers.
	private static Color? ParseConstant( string value )
	{
		var parts = value.Trim( '[', ']', ' ' )
			.Split( ' ', StringSplitOptions.RemoveEmptyEntries );

		if ( parts.Length is < 3 or > 4 ) return null;

		var channels = new float[4] { 0f, 0f, 0f, 1f };

		for ( var i = 0; i < parts.Length; i++ )
		{
			if ( !float.TryParse( parts[i], System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out channels[i] ) )
				return null;
		}

		return new Color( channels[0], channels[1], channels[2], channels[3] );
	}

	//Colour multiplier: slot constant, else tint, else white; an all-zero tint means unset
	private static Color Tint( Material material, Color? constant )
	{
		//A colour map given as a constant is that colour; its alpha nought is how their tools write "[0 0 0 0]" black glass
		if ( constant is { } authored )
			return authored.a > 0.0f ? authored : authored.WithAlpha( 1.0f );

		if ( material is null ) return Color.White;

		var tint = material.GetColor( "g_vColorTint" );

		if ( tint.r > 0.0f || tint.g > 0.0f || tint.b > 0.0f )
			return tint.a > 0.0f ? tint : tint.WithAlpha( 1.0f );

		return Color.White;
	}

	private static string TexturePath( Material material, string parameter )
	{
		var texture = material.GetTexture( parameter );
		return Rewrite( texture?.ResourcePath );
	}

	//Zero from a material usually means the parameter is absent rather than deliberately zero, so it falls back to what the shader would have used.
	private static float Clamp01( float value, float whenAbsent )
	{
		if ( value <= 0.0f ) return whenAbsent;
		return value > 1.0f ? 1.0f : value;
	}

	//-- shared ---------------------------------------------------------------

	//Point a reference at what the porter will have written, rather than at what it read.
	internal static string Rewrite( string path )
	{
		if ( string.IsNullOrEmpty( path ) ) return "";

		return Path.GetExtension( path ).ToLowerInvariant() switch
		{
			".vmdl" => Path.ChangeExtension( path, ".mdl" ).Replace( '\\', '/' ),
			".vmat" => Path.ChangeExtension( path, ".mat" ).Replace( '\\', '/' ),
			_ => path.Replace( '\\', '/' ),
		};
	}

	private static void Write( string path, object definition )
	{
		var json = JsonSerializer.Serialize( definition, new JsonSerializerOptions { WriteIndented = true } );
		File.WriteAllText( path, json );
	}

	//Maps their F_ feature switches (translucent, additive, alpha test, backfaces) to ours
	private static Dictionary<string, int> FeaturesOf( List<KeyValuePair<string, string>> authored )
	{
		var features = new Dictionary<string, int>();

		if ( Switch( authored, "F_ADDITIVE" ) || Switch( authored, "F_ADDITIVE_BLEND" ) ||
			Switch( authored, "F_TRANSLUCENT" ) || Switch( authored, "F_BLEND" ) )
			features["F_TRANSLUCENT"] = 1;
		else if ( Switch( authored, "F_ALPHA_TEST" ) )
			features["F_ALPHA"] = 1;

		if ( Switch( authored, "F_RENDER_BACKFACES" ) || Switch( authored, "F_DONT_CULL" ) )
			features["F_BACKFACES"] = 1;

		return features;
	}

	//One of their features, on or off.
	private static bool Switch( List<KeyValuePair<string, string>> authored, string name )
	{
		var value = Authored( authored, name );

		return !string.IsNullOrEmpty( value ) && value != "0";
	}

	//One of their numbers, or nought where it was not written.
	private static float Number( List<KeyValuePair<string, string>> authored, string name )
	{
		return float.TryParse( Authored( authored, name ), System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out var number ) ? number : 0.0f;
	}

	//File a resource path names in the source project, or null; checked in source, not output
	private static string Carried( string sourceAssets, string path, string resource, string what,
		PortReport report )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return path;

		//Their file system ignores case and their materials were written on one that does
		if ( PortPaths.FindFile( Beside( sourceAssets, path ) ) is { } full )
			return Path.GetRelativePath( sourceAssets, full ).Replace( '\\', '/' );

		report.Notes.Add( $"{resource}: {what} is {path}, which the project it was ported from does not " +
			"contain - left empty, so the material draws plainly rather than as the missing texture." );

		return "";
	}

	private static string Beside( string root, string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return null;

		return Path.Combine( root, path.Replace( '/', Path.DirectorySeparatorChar ) );
	}

	//Copies a file and returns where it landed (see PortPaths.OntoExisting); use that path after
	private static string Copy( string from, string to )
	{
		to = PortPaths.OntoExisting( to );

		Directory.CreateDirectory( Path.GetDirectoryName( to ) );
		File.Copy( from, to, overwrite: true );

		return to;
	}

	//-- the command ----------------------------------------------------------

	//What the two phases after the files are called, on the bar.
	private const string Carrying = "Bringing s&box's own content across";
	private const string Naming = "Rewriting resource paths";

	[ConCmd( "port_project", ConVarFlags.Protected,
		Help = "Port the open project's assets into a new one at the given path" )]
	public static void PortCurrentProject( string destination ) => PortCurrentProject( destination, null );

	public static void PortCurrentProject( string destination, Action<string, float> progress )
	{
		var project = Project.Current;

		if ( project is null )
		{
			Log.Warning( "no project is open" );
			return;
		}

		if ( string.IsNullOrWhiteSpace( destination ) )
		{
			Log.Warning( "give a destination directory" );
			return;
		}

		var report = Port( project.RootDirectory.FullName, destination, project, progress );

		Log.Info( $"port: {report}" );

		if ( !string.IsNullOrEmpty( report.ProjectFile ) )
			Log.Info( $"  new project: {report.ProjectFile}" );

		foreach ( var shader in report.CustomShaders )
			Log.Info( $"  shader waiting on the renderer: {shader}" );

		//What was left where it was, so the difference between "ported" and "everything that was there" is a number rather than a surprise.
		foreach ( var (extension, count) in report.LeftBehind.OrderByDescending( x => x.Value ) )
			Log.Info( $"  left behind: {count} {extension}" );

		if ( report.Graphs.Count > 0 )
		{
			Log.Warning( $"  {report.Graphs.Count} animation graph(s) need re-authoring - " +
				"their nodes are s&box's and ours are not the same shape:" );

			foreach ( var graph in report.Graphs.Take( 20 ) )
				Log.Warning( $"    {graph}" );

			if ( report.Graphs.Count > 20 )
				Log.Warning( $"    ...and {report.Graphs.Count - 20} more" );
		}

		//The numbers still in their units. The whole point of writing them down is that somebody reads them,
		//and until now nothing printed them - the report counted them and the count went past in one line.
		foreach ( var number in report.Unconverted.Take( 40 ) )
			Log.Warning( $"  still in their units: {number.What} = {number.Value} in {number.Where} ({number.Why})" );

		if ( report.Unconverted.Count > 40 )
			Log.Warning( $"  ...and {report.Unconverted.Count - 40} more numbers left alone" );

		foreach ( var note in report.Notes.Take( 40 ) )
			Log.Info( $"  note: {note}" );

		if ( report.Notes.Count > 40 )
			Log.Info( $"  ...and {report.Notes.Count - 40} more notes" );

		foreach ( var line in report.Skipped.Take( 40 ) )
			Log.Warning( $"  skipped {line}" );

		if ( report.Skipped.Count > 40 )
			Log.Warning( $"  ...and {report.Skipped.Count - 40} more" );
	}
}
