using Rinten.UI;

namespace Rinten;

//The scene's side of drawing its interface: the screen panels and camera pictures CoreUiRenderer walks into batches
internal static class CoreSceneUi
{
	/// <summary>The scene's other cameras, each a box in its rectangle of the screen, for a caller that walks the panels root by root.</summary>
	public static void BuildCameraLayers( Scene scene, List<CoreUiRenderer.UiBatch> output )
	{
		if ( !scene.IsValid() ) return;

		foreach ( var (picture, area) in CoreCameraTargets.Layers( scene ) )
			CoreUiRenderer.Picture( output, area, picture );
	}

	/// <summary>Every screen panel of the scene in paint order, over its other cameras.</summary>
	public static void Build( Scene scene, List<CoreUiRenderer.UiBatch> output,
		CoreUiRenderer.TextRasteriser rasteriseText, CoreUiRenderer.TextureUploader uploadTexture = null )
	{
		if ( !scene.IsValid() ) return;

		CoreUiRenderer.Build( ScreenRoots( scene ), output, rasteriseText, uploadTexture, o => BuildCameraLayers( scene, o ) );
	}

	private static IEnumerable<Panel> ScreenRoots( Scene scene )
	{
		foreach ( var screen in scene.GetAllComponents<ScreenPanel>() )
		{
			if ( !screen.IsValid() ) continue;

			//A sheet in front of a headset is drawn in the eyes, not on the window - see CoreSceneDrawer.DrawHeadsetPanels
			if ( screen.GetPanel() is not Panel root || root is GameRootPanel { InHeadset: true } ) continue;

			yield return root;
		}
	}
}
