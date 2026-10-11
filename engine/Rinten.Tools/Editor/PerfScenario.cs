using Rinten;

namespace Editor;

/// <summary>Sets a named editor state from the command line (-perfworkspace etc.) so measured runs repeat</summary>
public static class PerfScenario
{
	private static readonly string workspace =
		Rinten.Utility.CommandLine.GetSwitch( "-perfworkspace", "" ).TrimQuoted();

	//-perfscene: scene(s) to open by path or name, comma-separated; the last one ends up on top
	private static readonly string scene =
		Rinten.Utility.CommandLine.GetSwitch( "-perfscene", "" ).TrimQuoted();

	//-perfdock: docks to have open, by View menu name, comma-separated
	private static readonly string docks =
		Rinten.Utility.CommandLine.GetSwitch( "-perfdock", "" ).TrimQuoted();

	//-perfwindow WxH: editor window size, since restored geometry can be garbage after a measured run
	private static readonly string window =
		Rinten.Utility.CommandLine.GetSwitch( "-perfwindow", "" ).TrimQuoted();

	//-perfplay: press play once the scene opens, to measure the game with its own camera
	private static readonly bool play = Rinten.Utility.CommandLine.HasSwitch( "-perfplay" );

	//-perfconvar "a 1;b=2": console commands run after project settings are applied
	private static readonly string convars =
		Rinten.Utility.CommandLine.GetSwitch( "-perfconvar", "" ).TrimQuoted();

	//Runs what -perfconvar asked for, one command a semicolon.
	private static void RunConvars()
	{
		if ( string.IsNullOrWhiteSpace( convars ) ) return;

		foreach ( var asked in convars.Split( ';', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries ) )
		{
			//Accept name=value too; the console silently ignores it, which once made an A/B test meaningless
			var one = asked;
			var equals = one.IndexOf( '=' );

			if ( equals > 0 && one.IndexOf( ' ' ) is var space && ( space < 0 || space > equals ) )
				one = one.Substring( 0, equals ) + " " + one.Substring( equals + 1 );

			try
			{
				ConsoleSystem.Run( one );
				Log.Info( $"-perfconvar: {one}" );
			}
			catch ( System.Exception e )
			{
				Log.Warning( e, $"-perfconvar {one}: would not run" );
			}
		}
	}

	//An object to select once the run has settled - by name, or first for the scene's first root - so what selecting costs can be measured without a mouse.
	private static readonly string select = Rinten.Utility.CommandLine.GetSwitch( "-perfselect", "" ).TrimQuoted();

	//An asset to open in its own editor once the run has settled, by path.
	private static readonly string open = Rinten.Utility.CommandLine.GetSwitch( "-perfopen", "" ).TrimQuoted();

	//Whether to bake the open scene's lighting once it is up - -perfbake - so a run can look at a bake
	//nobody pressed the button for: see BakedLighting.Bake. The frames after it draw what it made.
	private static readonly bool bake = Rinten.Utility.CommandLine.HasSwitch( "-perfbake" );

	private static bool baked;

	//How long after the scenario to do those, in seconds - long enough for the memory before them to settle.
	private static readonly int actAfter = Rinten.Utility.CommandLine.GetSwitchInt( "-perfactafter", 20 );

	//-perfeye x,y,z[,pitch,yaw]: camera anchor; read raw since values can start with a minus
	private static readonly string eye = RawSwitch( "-perfeye" );

	//What follows a switch on the command line as written, minus quotes.
	private static string RawSwitch( string name )
	{
		var line = Rinten.Utility.CommandLine.Full;
		if ( string.IsNullOrEmpty( line ) ) return "";

		var at = line.IndexOf( name, System.StringComparison.OrdinalIgnoreCase );
		if ( at < 0 ) return "";

		at += name.Length;
		while ( at < line.Length && char.IsWhiteSpace( line[at] ) ) at++;

		var end = at;
		while ( end < line.Length && !char.IsWhiteSpace( line[end] ) ) end++;

		return line[at..end].TrimQuoted();
	}

	//What reads this lives in the tools addon rather than here, so this property is the seam between them. Parsed on the first ask and kept, because a warning about a misspelled switch is worth saying once.
	/// <summary>Where <c>-perfeye</c> asked the camera to stand, or nothing.</summary>
	public static (Vector3 Position, Rotation Rotation)? EyeOverride
	{
		get
		{
			if ( !eyeParsed )
			{
				eyeParsed = true;
				parsedEye = ParseEye();
			}

			return parsedEye;
		}
	}

	private static bool eyeParsed;
	private static (Vector3 Position, Rotation Rotation)? parsedEye;

	private static (Vector3 Position, Rotation Rotation)? ParseEye()
	{
		if ( string.IsNullOrWhiteSpace( eye ) ) return null;

		var parts = eye.Split( ',', System.StringSplitOptions.RemoveEmptyEntries |
			System.StringSplitOptions.TrimEntries );

		var culture = System.Globalization.CultureInfo.InvariantCulture;

		if ( parts.Length is not ( 3 or 5 ) ||
			!float.TryParse( parts[0], culture, out var x ) ||
			!float.TryParse( parts[1], culture, out var y ) ||
			!float.TryParse( parts[2], culture, out var z ) )
		{
			Log.Warning( $"-perfeye {eye}: not x,y,z or x,y,z,pitch,yaw - left where it was" );
			return null;
		}

		var rotation = Rotation.Identity;

		if ( parts.Length == 5 &&
			float.TryParse( parts[3], culture, out var pitch ) &&
			float.TryParse( parts[4], culture, out var yaw ) )
		{
			rotation = Rotation.From( pitch, yaw, 0.0f );
		}

		Log.Info( $"-perfeye: camera at {x},{y},{z}" );

		return ( new Vector3( x, y, z ), rotation );
	}

	/// <summary>Whether this process is a measurement run.</summary>
	public static bool Measuring { get; } =
		Rinten.Utility.CommandLine.GetSwitchInt( "-perfreport", 0 ) > 0 ||
		Rinten.Utility.CommandLine.GetSwitchInt( "-perfframes", 0 ) > 0;

	private static bool done;

	/// <summary>Once, as soon as there is an editor window to ask. Not from the window's own constructor: a workspace built before the project has finished loading is a workspace with nothing in it, which measures the empty case under the name of the full one.</summary>
	[EditorEvent.Frame]
	public static void Frame()
	{
		//An editor comes up before its project, which is opened a frame or two later between two of them. Nothing to measure until it is in.
		if ( Project.Current is null || StartupLoadProject.IsLoading ) return;

		if ( done )
		{
			Act();
			return;
		}
		if ( string.IsNullOrWhiteSpace( workspace ) && string.IsNullOrWhiteSpace( scene ) &&
			string.IsNullOrWhiteSpace( window ) && string.IsNullOrWhiteSpace( docks ) && !play ) return;

		var editor = Rinten.Internal.GlobalToolsNamespace.EditorWindow;
		if ( !editor.IsValid() ) return;

		done = true;
		sinceScenario = 0;

		try
		{
			if ( !string.IsNullOrWhiteSpace( window ) ) Resize( editor );
		}
		catch ( System.Exception e )
		{
			Log.Warning( e, $"-perfwindow {window}: could not resize" );
		}

		//The scene first: the mapping workspace takes whatever scene is open when it is entered, so opening one afterwards puts it somewhere else.
		try
		{
			if ( !string.IsNullOrWhiteSpace( scene ) ) OpenScene();
		}
		catch ( System.Exception e )
		{
			Log.Warning( e, $"-perfscene {scene}: could not open" );
		}

		try
		{
			if ( !string.IsNullOrWhiteSpace( workspace ) ) Apply( editor );
		}
		catch ( System.Exception e )
		{
			Log.Warning( e, $"-perfworkspace {workspace}: could not switch" );
		}

		//Last: the workspace switch above brings its own layout with it.
		try
		{
			if ( !string.IsNullOrWhiteSpace( docks ) ) OpenDocks();
		}
		catch ( System.Exception e )
		{
			Log.Warning( e, $"-perfdock {docks}: could not open" );
		}

		//After the docks, because playing takes the scene that is open, and everything above is what decides which one that is.
		try
		{
			if ( play ) Play();
		}
		catch ( System.Exception e )
		{
			Log.Warning( e, "-perfplay: could not start the game" );
		}
	}

	//Brings up the docks the run asked for, in the layout on screen.
	private static RealTimeSince sinceScenario;
	private static RealTimeSince sinceAct;
	private static int actStage;
	private static bool convarsRun;

	//The selection and the opening, after the scenario has settled, with the memory written down before them and
	//twice after - the growth somebody sees when they click an object or open an editor, in numbers and in the log.
	private static void Act()
	{
		//Convars go first, just after the project's settings apply on the first frame, or they'd be undone
		if ( !convarsRun && sinceScenario > 0.05f )
		{
			convarsRun = true;
			RunConvars();
		}

		//A few seconds in, once the scene's models have been built and placed.
		if ( bake && !baked && sinceScenario > 3.0f )
		{
			baked = true;
			_ = Bake();
		}

		if ( string.IsNullOrWhiteSpace( select ) && string.IsNullOrWhiteSpace( open ) ) return;

		if ( actStage == 0 && sinceScenario > actAfter )
		{
			actStage = 1;
			NoteMemory( "before" );

			try
			{
				if ( !string.IsNullOrWhiteSpace( select ) ) Select();
				if ( !string.IsNullOrWhiteSpace( open ) ) Open();
			}
			catch ( System.Exception e )
			{
				Log.Warning( e, "-perfselect/-perfopen: failed" );
			}

			sinceAct = 0;
		}
		else if ( actStage == 1 && sinceAct > 5 )
		{
			actStage = 2;
			NoteMemory( "5 s after" );
		}
		else if ( actStage == 2 && sinceAct > 20 )
		{
			actStage = 3;
			NoteMemory( "20 s after" );
		}
	}

	//What the process holds, with the managed heap collected first so it is what is alive.
	internal static void NoteMemory( string when, bool collect = true )
	{
		var live = System.GC.GetTotalMemory( forceFullCollection: collect );
		var info = System.GC.GetGCMemoryInfo();

		using var process = System.Diagnostics.Process.GetCurrentProcess();

		Log.Info( $"-perfmemory {when}: {( collect ? "live" : "allocated, uncollected" )} {live >> 20} MB, " +
			$"heap {info.HeapSizeBytes >> 20} MB, fragmented {info.FragmentedBytes >> 20} MB, " +
			$"committed {info.TotalCommittedBytes >> 20} MB, working set {process.WorkingSet64 >> 20} MB, " +
			$"private {process.PrivateMemorySize64 >> 20} MB, gc {System.GC.CollectionCount( 0 )}/{System.GC.CollectionCount( 1 )}/{System.GC.CollectionCount( 2 )}" );
	}

	private static async System.Threading.Tasks.Task Bake()
	{
		if ( SceneEditorSession.Active?.Scene is not { } scene )
		{
			Log.Warning( "-perfbake: no scene is open" );
			return;
		}

		var lighting = scene.Lighting;

		var started = RealTime.Now;

		try
		{
			await lighting.Bake();
			Log.Info( $"-perfbake: baked in {RealTime.Now - started:0.0} s - {( lighting.HasBake ? "a bake to read" : "nothing came of it" )}" );
		}
		catch ( System.Exception e )
		{
			Log.Warning( e, "-perfbake: the bake failed" );
		}
	}

	private static void Select()
	{
		if ( SceneEditorSession.Active is not { } session )
		{
			Log.Warning( $"-perfselect {select}: no scene is open" );
			return;
		}

		var go = string.Equals( select, "first", System.StringComparison.OrdinalIgnoreCase )
			? session.Scene.Children.FirstOrDefault()
			: session.Scene.Directory.FindByName( select ).FirstOrDefault();

		if ( go is null )
		{
			Log.Warning( $"-perfselect {select}: no such object" );
			return;
		}

		session.Selection.Set( go );

		Log.Info( $"-perfselect: selected {go.Name}" );
	}

	private static void Open()
	{
		if ( AssetSystem.FindByPath( open ) is not { } asset )
		{
			Log.Warning( $"-perfopen {open}: no such asset" );
			return;
		}

		asset.OpenInEditor();

		Log.Info( $"-perfopen: opened {open}" );
	}

	private static void OpenDocks()
	{
		var names = docks.Split( ',', System.StringSplitOptions.RemoveEmptyEntries |
			System.StringSplitOptions.TrimEntries );

		var layout = DockWindow.DocksInFront;

		if ( layout is null )
		{
			Log.Warning( $"-perfdock {docks}: no layout in front - the run is measuring whatever was already open" );
			return;
		}

		foreach ( var name in names )
		{
			//A name with ! in front of it is one the run wants shut, which is half of what a repeatable scenario needs and cannot be said twice.
			var wanted = !name.StartsWith( '!' );
			var dock = wanted ? name : name[1..].Trim();

			if ( string.IsNullOrEmpty( dock ) ) continue;

			layout.SetDockState( dock, wanted );

			Log.Info( $"-perfdock: {dock} {( wanted ? "opened" : "closed" )}" );
		}
	}

	//Presses play on whatever scene the run ended up with. The scene, not the play mode : the mode loads the project's own startup game through
	//the package loader, which is a second level and a second loading screen, and not what a scene named on the command line was asking to measure.
	private static void Play()
	{
		if ( SceneEditorSession.Active is null )
		{
			Log.Warning( "-perfplay: no scene is open - the run is measuring the editor" );
			return;
		}

		EditorScene.Play( playMode: false );

		Log.Info( "-perfplay: playing" );
	}

	//Puts the editor window at the size the run asked for.
	private static void Resize( EditorMainWindow editor )
	{
		//The screen, which no size can exceed: a request for 1920x1200 on a 1920x1200 display comes out at whatever
		//the window system leaves for a window with a caption, so the run that wants the whole of it says so.
		if ( string.Equals( window, "max", System.StringComparison.OrdinalIgnoreCase ) )
		{
			editor.MakeMaximized();

			Log.Info( "-perfwindow: window maximized" );
			return;
		}

		var parts = window.Split( 'x', 'X' );

		if ( parts.Length != 2 ||
			!int.TryParse( parts[0], out var width ) || !int.TryParse( parts[1], out var height ) ||
			width < 320 || height < 240 )
		{
			Log.Warning( $"-perfwindow {window}: not a size like 1600x900 - left as it was" );
			return;
		}

		editor.Size = new Vector2( width, height );

		Log.Info( $"-perfwindow: window set to {width}x{height}" );
	}

	//Opens the named scene(s), trying the name as given and then under the scenes folder
	private static void OpenScene()
	{
		var names = scene.Split( ',', System.StringSplitOptions.RemoveEmptyEntries |
			System.StringSplitOptions.TrimEntries );

		foreach ( var name in names )
			OpenOne( name );
	}

	private static void OpenOne( string name )
	{
		foreach ( var path in Candidates( name ) )
		{
			if ( ResourceLibrary.Get<SceneFile>( path ) is not { } file ) continue;

			EditorScene.OpenScene( file );

			Log.Info( $"-perfscene: opened {path}" );
			return;
		}

		//Named rather than ignored, for the same reason as the workspace: a report headed with a scene it never opened is worse than no report.
		Log.Warning( $"-perfscene {name}: no such scene - the run is measuring whatever was already open" );
	}

	private static IEnumerable<string> Candidates( string name )
	{
		yield return name;

		if ( !name.EndsWith( ".scene", System.StringComparison.OrdinalIgnoreCase ) )
			yield return $"{name}.scene";

		if ( !name.Contains( '/' ) )
		{
			yield return $"scenes/{name}";
			yield return $"scenes/{name}.scene";
		}
	}

	private static void Apply( EditorMainWindow window )
	{
		//Mapping is built rather than found: it is not open until somebody asks for it, and the whole point of asking for it here is that nobody will.
		if ( string.Equals( workspace, "Mapping", System.StringComparison.OrdinalIgnoreCase ) )
		{
			window.OpenMapping();

			Log.Info( "-perfworkspace: opened Mapping" );
			return;
		}

		if ( WorkspaceArea.Current is not { } area )
		{
			Log.Warning( $"-perfworkspace {workspace}: no workspace area yet" );
			return;
		}

		if ( area.Find( workspace ) is { } found )
		{
			area.MakeCurrent( found );

			Log.Info( $"-perfworkspace: switched to {workspace}" );
			return;
		}

		//Named rather than ignored. A misspelled workspace that silently leaves the editor in General
		//produces a report labelled Mapping with General's numbers in it, and nothing in the file says so.
		Log.Warning( $"-perfworkspace {workspace}: no such workspace - the run is measuring whatever was open" );
	}
}
