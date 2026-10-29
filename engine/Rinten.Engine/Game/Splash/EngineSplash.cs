using Rinten.Engine;
using Rinten.UI;

namespace Rinten.Splash;

//A picture over an exported game's window: up while the game loads, and a moment longer where it loads faster
internal static class EngineSplash
{
	//Seconds the splash stays up at the least
	const float Shortest = 2.5f;

	//Seconds it fades out over
	const float FadeOut = 0.6f;

	//The picture the export named, or null for none
	internal static string Picture { get; set; }

	static SplashRoot root;
	static RealTimeSince sinceShown;
	static RealTimeSince sinceReady;
	static bool ready;
	static bool finished;

	//Once a frame, after the game has drawn and before the frame goes on screen
	internal static void Draw()
	{
		if ( finished || string.IsNullOrWhiteSpace( Picture ) ) return;
		if ( Application.IsEditor || Application.IsHeadless ) return;

		using var scope = GlobalContext.GameScope();

		//A root registers with the game's UI system, which the game instance makes on its first frame
		if ( GlobalContext.Current.UISystem is null ) return;

		if ( root is null )
		{
			root = new SplashRoot( Picture );
			sinceShown = 0;
		}

		if ( !ready && sinceShown >= Shortest && !Loading() )
		{
			ready = true;
			sinceReady = 0;
		}

		var opacity = ready ? 1f - sinceReady / FadeOut : 1f;

		if ( opacity <= 0f )
		{
			root.Delete();
			root = null;
			finished = true;
			return;
		}

		root.Style.Opacity = opacity;

		var (width, height) = CoreGameRenderer.Size;
		if ( width <= 0 || height <= 0 ) return;

		root.TickInternal();
		root.PreLayout( new Rect( 0, 0, width, height ) );
		root.CalculateLayout();
		root.PostLayout();

		CoreGameRenderer.DrawOver( root );
	}

	static bool Loading() => LoadingScreen.IsVisible || !Game.IsPlaying || ( Game.ActiveScene?.IsLoading ?? true );
}

//One picture, letterboxed in its own black; laid out and drawn by EngineSplash rather than the game's UI system
internal sealed class SplashRoot : RootPanel
{
	public SplashRoot( string picture )
	{
		RenderedManually = true;

		Style.Set( $"background-color: #0b0b0c; background-image: url( \"{picture}\" ); background-size: contain; background-repeat: no-repeat; background-position: center;" );
	}

	public override bool IsWorldPanel => false;

	internal override bool ExternallyDriven => true;
}
