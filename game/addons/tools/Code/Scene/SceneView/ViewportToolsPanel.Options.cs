namespace Editor;

partial class ViewportToolsPanel
{
	private void BuildToolbarRight( List<Group> section )
	{
		{
			var group = AddGroup( section );
			group.Items.Add( new Item { NetworkSettingsMenu = true } );
		}

		AddDivider( section );

		{
			var group = AddGroup( section );

			if ( sceneView.CurrentView != SceneViewMode.Game )
				AddButton( group, "Viewport Layout", "grid_view", OpenLayoutMenu );

			//The game view has the lighting views too - full bright is for looking at a game being played as much as a scene being edited.
			group.Items.Add( new Item { ViewSettingsMenu = true } );

			AddButton( group, "Toggle Fullscreen", "crop_free", ToggleFullscreen );

			AddButton( group, "Show Stats", "speed", () => SceneStatsPanel.Showing = !SceneStatsPanel.Showing )
				.IsActive = () => SceneStatsPanel.Showing;
		}
	}
}
