using System;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

namespace Rinten;

//JS imports from native.js: canvas, input and prefetched files. No rendering here
static partial class Native
{
	[JSImport( "globalThis.RintenNative.whenReady" )]
	internal static partial Task WhenReady();

	[JSImport( "globalThis.RintenNative.canvasWidth" )]
	internal static partial int CanvasWidth();

	[JSImport( "globalThis.RintenNative.canvasHeight" )]
	internal static partial int CanvasHeight();

	[JSImport( "globalThis.RintenNative.canvasCssWidth" )]
	internal static partial int CanvasCssWidth();

	[JSImport( "globalThis.RintenNative.canvasCssHeight" )]
	internal static partial int CanvasCssHeight();

	[JSImport( "globalThis.RintenNative.pointerX" )]
	internal static partial float PointerX();

	[JSImport( "globalThis.RintenNative.pointerY" )]
	internal static partial float PointerY();

	[JSImport( "globalThis.RintenNative.pointerDeltaX" )]
	internal static partial float PointerDeltaX();

	[JSImport( "globalThis.RintenNative.pointerDeltaY" )]
	internal static partial float PointerDeltaY();

	[JSImport( "globalThis.RintenNative.wheelX" )]
	internal static partial float WheelX();

	[JSImport( "globalThis.RintenNative.wheelY" )]
	internal static partial float WheelY();

	[JSImport( "globalThis.RintenNative.pointerLocked" )]
	internal static partial int PointerLocked();

	[JSImport( "globalThis.RintenNative.buttons" )]
	internal static partial int Buttons();

	[JSImport( "globalThis.RintenNative.keyDown" )]
	internal static partial int KeyDown( string code );

	[JSImport( "globalThis.RintenNative.keyVersion" )]
	internal static partial int KeyVersion();

	[JSImport( "globalThis.RintenNative.nextFrame" )]
	internal static partial Task NextFrame();

	//The game's loop, run by the page: `body` is called once a turn and answers 0 to stop, 1 for the next turn
	//at once, 2 for the display's next frame - see runLoop in native.js. Completes when the body says stop.
	[JSImport( "globalThis.RintenNative.runLoop" )]
	internal static partial Task RunLoop( [JSMarshalAs<JSType.Function<JSType.Number>>] Func<int> body );

	[JSImport( "globalThis.RintenNative.say" )]
	internal static partial void Say( string text );

	[JSImport( "globalThis.RintenNative.progress" )]
	internal static partial void Progress( int done, int total );

	[JSImport( "globalThis.RintenNative.setTitle" )]
	internal static partial void SetTitle( string text );

	[JSImport( "globalThis.RintenNative.setPointerLock" )]
	internal static partial void SetPointerLock( int state );

	[JSImport( "globalThis.RintenNative.fullscreen" )]
	internal static partial int Fullscreen();

	[JSImport( "globalThis.RintenNative.fullscreenAllowed" )]
	internal static partial int FullscreenAllowed();

	[JSImport( "globalThis.RintenNative.setFullscreen" )]
	internal static partial void SetFullscreen( int state );

	[JSImport( "globalThis.RintenNative.fetchText" )]
	internal static partial Task<string> FetchText( string url );

	[JSImport( "globalThis.RintenNative.fetchBytes" )]
	internal static partial Task<int> FetchBytes( string url );

	[JSImport( "globalThis.RintenNative.takeFetch" )]
	internal static partial byte[] TakeFetch();

	//What the last FetchBytes brought back, copied into memory the caller holds - no array on the heap.
	[JSImport( "globalThis.RintenNative.takeFetchInto" )]
	internal static partial void TakeFetchInto( [JSMarshalAs<JSType.MemoryView>] Span<byte> into );

	[JSImport( "globalThis.RintenNative.assetManifest" )]
	internal static partial Task<string> AssetManifest();

	[JSImport( "globalThis.RintenNative.prefetch" )]
	internal static partial Task<int> Prefetch( string list, int concurrency );

	[JSImport( "globalThis.RintenNative.takeFile" )]
	internal static partial byte[] TakeFile( string path );

	[JSImport( "globalThis.RintenNative.fileLength" )]
	internal static partial int FileLength( string path );

	//A prefetched file copied into memory the caller holds, and dropped by the page.
	[JSImport( "globalThis.RintenNative.takeFileInto" )]
	internal static partial void TakeFileInto( string path, [JSMarshalAs<JSType.MemoryView>] Span<byte> into );
}
