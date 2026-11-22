using Rinten;

namespace EngineTests;

//The scale the game's interface and gizmos use has to follow the display, not only the pixel size.
[TestClass]
[DoNotParallelize]
public class ScreenDesktopScaleTests
{
	[TestMethod]
	public void TheDesktopScaleFollowsTheDisplayWhenThePixelSizeDoesNotChange()
	{
		ThreadSafe.MarkMainThread();

		var previousSize = Screen.Size;
		var previousSource = Screen.DesktopScaleSource;

		try
		{
			//A maximized window on two monitors of the same resolution: the size the game reports is
			//the same, while the display scale the window is on is not.
			Screen.Size = new Vector2( 1024, 1024 );
			Screen.DesktopScaleSource = () => 1.0f;

			Screen.UpdateFromEngine();
			Assert.AreEqual( 1.0f, Screen.DesktopScale, 0.001f );

			Screen.DesktopScaleSource = () => 1.5f;

			Screen.UpdateFromEngine();
			Assert.AreEqual( 1.5f, Screen.DesktopScale, 0.001f,
				"the scale changed without the pixel size changing, and the old value was left behind" );
		}
		finally
		{
			Screen.DesktopScaleSource = previousSource;
			Screen.Size = previousSize;
		}
	}
}
