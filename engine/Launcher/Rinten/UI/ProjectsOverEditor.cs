namespace Editor;

//The launcher inside the editor window: filling it at startup, a card over an open project
internal static class ProjectsOverEditor
{
	//Tells chosen which project file was picked
	public static Rinten.UI.Panel Make( Action<string> chosen, bool overProject )
	{
		var panel = new LauncherPanel
		{
			//With mips, so the mark is smooth where it is drawn smaller than it is.
			Mark = Pixmap.FromFile( "logo_rounded.png" )?.picture?.ToTexture( mips: true ),
			Chosen = project =>
			{
				LauncherPreferences.Save();
				chosen( project.ConfigFilePath );
			},
		};

		EditorStyles.Apply( panel, "LauncherPanel.razor.scss" );

		//Its card size and frame are the stylesheet's - see LauncherPanel.over-editor
		if ( overProject ) panel.AddClass( "over-editor" );

		return panel;
	}
}
