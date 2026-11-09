using Rinten.Engine;
using Rinten.UI;

namespace Editor;

/// <summary>Prepends design tokens and theme variables before editor UI sheets are parsed.</summary>
public static class EditorSheetPreamble
{
	private static string themeText;

	//Where the theme is read from - a test points this at a file of its own.
	internal static string ThemePath = EditorDesign.ThemePath;
	private static bool themeTried;

	//How many times ReloadAll ran (tests).
	internal static int ReloadAllCount;

	/// <summary>Registers theme hooks on <see cref="EditorTokens"/> once the project filesystem is mounted.</summary>
	public static void Register()
	{
		EditorTokens.ThemePreamble = ThemeText;
		EditorTokens.ShouldPreparePath = ShouldPreparePath;

		//The projects have just changed, so what any of them holds is worth asking again.
		prepared.Clear();
	}

	/// <summary>Forgets cached theme text so the next sheet read picks up disk changes.</summary>
	public static void Reload()
	{
		themeText = null;
		themeTried = false;

		//Which sheets are the editor's own follows the projects, and those come and go.
		prepared.Clear();
	}

	//Re-reads theme, design, and every editor sheet already in memory.
	internal static void ReloadAll()
	{
		ReloadAllCount++;
		Reload();
		Theme.Reload();
		EditorDesign.Reload();
		EditorDesign.MarkDirty();
		EditorStyles.Reload();
		ReloadFilesystemSheets();
		DirtyAllStyles();
	}

	/// <summary>The theme sheet, put in front of every editor sheet as it parses.</summary>
	public static string ThemeText()
	{
		if ( themeTried ) return themeText;

		themeTried = true;

		try
		{
			themeText = FileSystem.Root?.ReadAllText( ThemePath );
		}
		catch ( System.Exception )
		{
			themeText = null;
		}

		return themeText;
	}

	/// <summary>Parses a sheet the way editor panels do: tokens, theme, then rules.</summary>
	public static StyleSheet ParseSheet( string text, string path, IEnumerable<(string key, string value)> variables = null )
	{
		return StyleSheet.FromString(
			EditorTokens.PrepareSheetText( text, path ),
			path,
			variables );
	}

	//Answers already worked out, since a sheet is asked about every time it loads.
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> prepared = new();

	//Whether a sheet belongs to an editor project (and gets the theme), asked of projects not paths
	internal static bool ShouldPreparePath( string path )
	{
		if ( string.IsNullOrEmpty( path ) ) return false;

		var normalized = path.Replace( '\\', '/' );

		//The two sheets the theme itself comes from can't be written against it.
		if ( normalized.EndsWith( "design.scss", System.StringComparison.OrdinalIgnoreCase )
			|| normalized.EndsWith( "theme.scss", System.StringComparison.OrdinalIgnoreCase )
			|| normalized.Contains( "/styles/kit/", System.StringComparison.OrdinalIgnoreCase ) )
			return false;

		return prepared.GetOrAdd( normalized, IsEditorSheet );
	}

	//Whether the file sits in a tool project - the editor and its addons.
	private static bool IsEditorSheet( string path )
	{
		var relative = path.TrimStart( '/' );
		var tools = ToolProjects();

		foreach ( var project in tools )
		{
			//Named from the project, and named from its code folder, which is how a panel's own sheet is mounted.
			if ( FileExists( project.GetRootPath(), relative ) ) return true;
			if ( FileExists( project.GetCodePath(), relative ) ) return true;
		}

		//Named from the game folder - "addons/tools/Code/..." as it sits on disk.
		if ( Project.Current?.GetRootPath() is { } gameRoot && FileExists( gameRoot, relative ) )
		{
			var full = System.IO.Path.GetFullPath( System.IO.Path.Combine( gameRoot, relative ) );

			foreach ( var project in tools )
			{
				if ( project.GetRootPath() is not { } root ) continue;

				if ( full.StartsWith( System.IO.Path.GetFullPath( root ), System.StringComparison.OrdinalIgnoreCase ) )
					return true;
			}
		}

		//With the projects up, they are the answer.
		if ( tools.Count > 0 ) return false;

		//Before they are loaded there is nobody to ask - the unit tests, and the first moments of a session. The
		//layout as it stood when this was written, and only as a fallback: a new addon needs nothing added here.
		return path.Contains( "addons/tools/", System.StringComparison.OrdinalIgnoreCase )
			|| StartsWithAny( relative, "editor/", "scene/", "audio/", "code/" );
	}

	private static bool StartsWithAny( string path, params string[] prefixes )
	{
		foreach ( var prefix in prefixes )
		{
			if ( path.StartsWith( prefix, System.StringComparison.OrdinalIgnoreCase ) )
				return true;
		}

		return false;
	}

	private static List<Project> ToolProjects()
	{
		return Project.All?.Where( x => x?.Config?.Type == "tool" ).ToList() ?? new List<Project>();
	}

	private static bool FileExists( string root, string relative )
	{
		if ( string.IsNullOrEmpty( root ) ) return false;

		try
		{
			return System.IO.File.Exists( System.IO.Path.Combine( root, relative ) );
		}
		catch ( System.Exception )
		{
			//A path that isn't one - nothing to prepare.
			return false;
		}
	}

	private static void ReloadFilesystemSheets()
	{
		foreach ( var sheet in StyleSheet.Loaded.ToArray() )
		{
			if ( sheet?.FileName is null || sheet.Context is null )
				continue;

			if ( sheet.FileName == EditorDesign.Path )
				continue;

			if ( !ShouldPreparePath( sheet.FileName ) )
				continue;

			using ( sheet.Context?.Enter() )
			{
				sheet.Variables = null;
				sheet.UpdateFromFile( sheet.FileName, true, sheet.Context );
			}
		}
	}

	private static void DirtyAllStyles()
	{
		var seen = new HashSet<UISystem>();

		foreach ( var sheet in StyleSheet.Loaded )
		{
			var ui = sheet?.Context?.UISystem;

			if ( ui is not null && seen.Add( ui ) )
				ui.DirtyAllStyles();
		}

		var current = GlobalContext.Current?.UISystem;

		if ( current is not null && seen.Add( current ) )
			current.DirtyAllStyles();
	}
}
