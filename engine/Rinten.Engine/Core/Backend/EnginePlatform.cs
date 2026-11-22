namespace Rinten;

//Host OS services - the clipboard, message boxes, what the monitors are.
internal static class EnginePlatform
{
	private static readonly IPlatformBackend backend = new CorePlatformBackend();

	public static bool HasClipboardText() => backend.HasClipboardText();
	public static string GetClipboardText() => backend.GetClipboardText();
	public static void SetClipboardText( string text ) => backend.SetClipboardText( text );
	public static void ClearClipboardText() => backend.ClearClipboardText();

	public static void MessageBox( string title, string message ) => backend.MessageBox( title, message );

	public static int GetDefaultMonitorIndex() => backend.GetDefaultMonitorIndex();

	/// <summary>The monitor the game window is on, or the default monitor before there is one - or when the window is one SDL didn't make (a canvas, an Android surface).</summary>
	public static int GetEngineWindowMonitorIndex()
	{
		var window = CoreGameRenderer.Window;
		if ( window is not null && !window.Adopted && window.Handle != IntPtr.Zero )
		{
			var monitor = backend.GetWindowMonitorIndex( window.Handle );
			if ( monitor >= 0 ) return monitor;
		}

		return GetDefaultMonitorIndex();
	}

	public static bool GetDesktopResolution( int monitorIndex, ref int width, ref int height, ref uint refreshRate )
		=> backend.GetDesktopResolution( monitorIndex, ref width, ref height, ref refreshRate );

	/// <summary>The diagonal DPI of the monitor the game window is on, or of the default monitor when there is no window.</summary>
	public static float GetDiagonalDpi() => GetDiagonalDpi( GetEngineWindowMonitorIndex() );

	/// <summary>The diagonal DPI of a monitor by index; the primary one where the index is not one of ours.</summary>
	public static float GetDiagonalDpi( int monitorIndex ) => backend.GetDiagonalDpi( monitorIndex );

	public static bool SafeRemoveFile( string file ) => backend.SafeRemoveFile( file );

	[ConCmd( "platform_status", ConVarFlags.Protected, Help = "Check the platform backend by round-tripping the clipboard" )]
	public static void PrintStatus()
	{
		var isCore = backend is CorePlatformBackend;
		Log.Info( $"Platform backend: {(isCore ? "our core (SDL3)" : "s&box (tier0)")}" );

		var before = GetClipboardText();
		const string probe = "sbox-platform-probe";

		SetClipboardText( probe );
		var readBack = GetClipboardText();

		//Put back whatever the user had.
		SetClipboardText( before );

		if ( isCore )
			Log.Info( $"  served by SDL3 : {CorePlatformBackend.LastCallUsedSdl}" );

		Log.Info( readBack == probe
			? "  clipboard round-trip: OK"
			: $"  clipboard round-trip: FAILED (wrote '{probe}', read '{readBack}')" );

		var monitor = GetDefaultMonitorIndex();
		int w = 0, h = 0;
		uint hz = 0;
		var got = GetDesktopResolution( monitor, ref w, ref h, ref hz );

		Log.Info( $"  primary monitor: index {monitor}" );
		Log.Info( got
			? $"  desktop mode   : {w}x{h} @ {hz}Hz"
			: "  desktop mode   : query failed" );
		Log.Info( $"  dpi            : {GetDiagonalDpi()}  (ui scale {GetDiagonalDpi() / 96.0f:0.00})" );
	}
}
