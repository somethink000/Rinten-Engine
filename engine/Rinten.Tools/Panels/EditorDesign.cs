using Rinten.UI;

namespace Editor;

//One sheet on the window root; styles fall down the tree. Panel .razor.scss files get tokens and theme via EditorSheetPreamble.
/// <summary>The look every panel in the editor is dressed in.</summary>
public static class EditorDesign
{
	/// <summary>Editor theme variables — one SCSS file per theme.</summary>
	public const string ThemePath = "/addons/base/code/Styles/kit/_theme.scss";

	/// <summary>Where the sheet lives: the base addon's UI kit, which games import too.</summary>
	public const string Path = "/addons/base/code/Styles/kit/_design.scss";

	private static StyleSheet sheet;
	private static bool tried;

	/// <summary>Dresses a panel and everything below it. Once per window root is enough.</summary>
	public static void Apply( Panel panel )
	{
		if ( panel is null ) return;

		var styles = Sheet();

		if ( styles is null ) return;

		panel.StyleSheet.Add( styles );
	}

	//Re-reads the design sheet from disk, or forgets it if it was not loaded yet.
	internal static void Reload()
	{
		if ( sheet is not null )
		{
			RefreshSheet( sheet );
			return;
		}

		tried = false;
		sheet = null;
	}

	private static StyleSheet Sheet()
	{
		if ( tried ) return sheet;

		tried = true;

		string text;

		try
		{
			text = FileSystem.Root?.ReadAllText( Path );
		}
		catch ( System.Exception )
		{
			text = null;
		}

		if ( string.IsNullOrEmpty( text ) )
		{
			new Rinten.Diagnostics.Logger( "Ui" ).Warning( $"the editor's design sheet is missing ({Path})" );
			return null;
		}

		sheet = StyleSheet.FromString(
			EditorTokens.InFrontOf( text, EditorSheetPreamble.ThemeText() ),
			Path );

		return sheet;
	}

	private static void RefreshSheet( StyleSheet target )
	{
		string text;

		try
		{
			text = FileSystem.Root?.ReadAllText( Path );
		}
		catch ( System.Exception )
		{
			text = null;
		}

		if ( string.IsNullOrEmpty( text ) )
			return;

		target.Variables = null;
		target.UpdateFromString(
			EditorTokens.InFrontOf( text, EditorSheetPreamble.ThemeText() ),
			Path,
			failSilently: true );
	}

	//Redraws panels that wear the design sheet.
	internal static void MarkDirty()
	{
		if ( sheet?.Nodes is null )
			return;

		foreach ( var node in sheet.Nodes )
			node.Styles?.MarkPanelsDirty();
	}
}
