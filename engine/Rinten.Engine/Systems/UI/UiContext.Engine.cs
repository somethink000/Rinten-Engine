using Rinten.Engine;

namespace Rinten.UI;

//The engine's contexts are its GlobalContexts: one for the menu, one for the game
internal partial class UiContext
{
	private static partial UiContext CurrentContext() => GlobalContext.Current;

	private static partial UiContext OwnerOf( RootPanel root )
		=> GlobalContext.Menu?.UISystem?.RootPanels.Contains( root ) == true ? GlobalContext.Menu : GlobalContext.Game;
}
