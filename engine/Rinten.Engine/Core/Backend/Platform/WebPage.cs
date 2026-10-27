using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Rinten;

//Page-provided services bound by the launcher: canvas size, pointer, keys and preloaded files
internal static class WebPage
{
	internal static Func<int> CanvasWidth;
	internal static Func<int> CanvasHeight;
	internal static Func<int> CanvasCssWidth;
	internal static Func<int> CanvasCssHeight;
	internal static Func<float> PointerX;
	internal static Func<float> PointerY;
	internal static Func<float> PointerDeltaX;
	internal static Func<float> PointerDeltaY;

	//The wheel since last asked, in notches up and right
	internal static Func<float> WheelX;
	internal static Func<float> WheelY;
	internal static Func<int> PointerLocked;
	internal static Action<int> SetPointerLock;

	//Whether the page is full screen, and asking it to be - see CoreWindow.SetFullscreen.
	internal static Func<int> Fullscreen;
	internal static Action<int> SetFullscreen;

	//Whether this browser will go full screen at all: an iframe may forbid it, and iOS has it for video alone.
	internal static Func<int> FullscreenAllowed;
	internal static Func<int> Buttons;
	internal static Func<string, int> KeyDown;

	//Moves whenever a key goes down or up - asked once a frame before any key is.
	internal static Func<int> KeyVersion;
	internal static Func<string, Task<byte[]>> FetchFile;
	internal static Func<Task> NextFrame;

}
