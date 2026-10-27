using System.Threading.Tasks;

namespace Rinten;

//Binds page-only services for the engine: canvas, pointer, keys, files and frame timing
static class WebBind
{
	internal static void Attach()
	{
		WebPage.CanvasWidth = Native.CanvasWidth;
		WebPage.CanvasHeight = Native.CanvasHeight;
		WebPage.CanvasCssWidth = Native.CanvasCssWidth;
		WebPage.CanvasCssHeight = Native.CanvasCssHeight;
		WebPage.PointerX = Native.PointerX;
		WebPage.PointerY = Native.PointerY;
		WebPage.PointerDeltaX = Native.PointerDeltaX;
		WebPage.PointerDeltaY = Native.PointerDeltaY;
		WebPage.WheelX = Native.WheelX;
		WebPage.WheelY = Native.WheelY;
		WebPage.PointerLocked = Native.PointerLocked;
		WebPage.SetPointerLock = Native.SetPointerLock;
		WebPage.Fullscreen = Native.Fullscreen;
		WebPage.FullscreenAllowed = Native.FullscreenAllowed;
		WebPage.SetFullscreen = Native.SetFullscreen;
		WebPage.Buttons = Native.Buttons;
		WebPage.KeyDown = Native.KeyDown;
		WebPage.KeyVersion = Native.KeyVersion;
		WebPage.FetchFile = LoadFile;
		WebPage.NextFrame = Native.NextFrame;
	}

	static async Task<byte[]> LoadFile( string url )
	{
		var n = await Native.FetchBytes( url );
		return n > 0 ? Native.TakeFetch() : null;
	}
}
