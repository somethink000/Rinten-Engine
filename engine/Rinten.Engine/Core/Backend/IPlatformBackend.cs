using System.Runtime.InteropServices;

namespace Rinten;

//Host operating system services the engine needs: clipboard, message boxes. Nothing here touches engine
//state, which is why it can live entirely in the platform layer rather than being mirrored.
internal interface IPlatformBackend
{
	bool HasClipboardText();
	string GetClipboardText();
	void SetClipboardText( string text );
	void ClearClipboardText();

	void MessageBox( string title, string message );

	//Index of the monitor the OS considers primary.
	int GetDefaultMonitorIndex();

	//Index of the monitor an SDL window is on, or -1 if that can't be told.
	int GetWindowMonitorIndex( IntPtr sdlWindow );

	bool GetDesktopResolution( int monitorIndex, ref int width, ref int height, ref uint refreshRate );

	//Diagonal DPI of a monitor, or of the primary one when the index is not one of ours. Callers divide by 96 to get a UI scale.
	float GetDiagonalDpi( int monitorIndex );

	bool SafeRemoveFile( string file );
}

//Platform layer on SDL3 for desktops; Android and web ship no SDL, so calls catch DllNotFound
internal sealed partial class CorePlatformBackend : IPlatformBackend
{
	private const string SDL = "SDL3";
	private const uint SDL_INIT_VIDEO = 0x00000020u;
	private const uint SDL_MESSAGEBOX_ERROR = 0x00000010u;

	[LibraryImport( SDL, EntryPoint = "SDL_WasInit" )]
	private static partial uint SDL_WasInit( uint flags );

	[LibraryImport( SDL, EntryPoint = "SDL_InitSubSystem" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_InitSubSystem( uint flags );

	[LibraryImport( SDL, EntryPoint = "SDL_HasClipboardText" )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_HasClipboardText();

	[LibraryImport( SDL, EntryPoint = "SDL_GetClipboardText" )]
	private static partial IntPtr SDL_GetClipboardText();

	[LibraryImport( SDL, EntryPoint = "SDL_SetClipboardText", StringMarshalling = StringMarshalling.Utf8 )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_SetClipboardText( string text );

	[LibraryImport( SDL, EntryPoint = "SDL_ShowSimpleMessageBox", StringMarshalling = StringMarshalling.Utf8 )]
	[return: MarshalAs( UnmanagedType.U1 )]
	private static partial bool SDL_ShowSimpleMessageBox( uint flags, string title, string message, IntPtr window );

	[LibraryImport( SDL, EntryPoint = "SDL_free" )]
	private static partial void SDL_free( IntPtr mem );

	//Array of display ids, oldest-first. Caller frees it.
	[LibraryImport( SDL, EntryPoint = "SDL_GetDisplays" )]
	private static partial IntPtr SDL_GetDisplays( out int count );

	[LibraryImport( SDL, EntryPoint = "SDL_GetPrimaryDisplay" )]
	private static partial uint SDL_GetPrimaryDisplay();

	[LibraryImport( SDL, EntryPoint = "SDL_GetDisplayForWindow" )]
	private static partial uint SDL_GetDisplayForWindow( IntPtr window );

	[LibraryImport( SDL, EntryPoint = "SDL_GetDesktopDisplayMode" )]
	private static partial IntPtr SDL_GetDesktopDisplayMode( uint displayId );

	[LibraryImport( SDL, EntryPoint = "SDL_GetDisplayContentScale" )]
	private static partial float SDL_GetDisplayContentScale( uint displayId );

	//Leading fields of SDL_DisplayMode. SDL owns the full struct; we only read the
	//prefix, which has been stable across SDL3, and never allocate one ourselves.
	[StructLayout( LayoutKind.Sequential )]
	private struct SdlDisplayModeHead
	{
		public uint DisplayId;
		public uint Format;
		public int Width;
		public int Height;
		public float PixelDensity;
		public float RefreshRate;
	}

	//Display ids in enumeration order, so a monitor index stays stable between runs. Empty if SDL is not up.
	private static uint[] GetDisplayIds()
	{
		if ( !SdlReady ) return Array.Empty<uint>();

		var ptr = SDL_GetDisplays( out var count );
		if ( ptr == IntPtr.Zero || count <= 0 ) return Array.Empty<uint>();

		try
		{
			var ids = new uint[count];
			unsafe
			{
				var p = (uint*)ptr;
				for ( var i = 0; i < count; i++ ) ids[i] = p[i];
			}
			return ids;
		}
		finally { SDL_free( ptr ); }
	}

	//SDL's clipboard and message boxes need the video subsystem up.
	private static bool SdlReady
	{
		get
		{
			try
			{
				if ( OperatingSystem.IsBrowser() ) return false;
				if ( ( SDL_WasInit( SDL_INIT_VIDEO ) & SDL_INIT_VIDEO ) != 0 ) return true;

				//Nothing has started the video subsystem yet, and there is nobody else to ask - so start it here:
				//a message box before the window exists is exactly when something has gone wrong and wants saying.

				return SDL_InitSubSystem( SDL_INIT_VIDEO );
			}
			catch ( DllNotFoundException ) { return false; }
			catch ( EntryPointNotFoundException ) { return false; }
		}
	}

	//Whether SDL was up for the last call. Diagnostics only - see the platform_status command.
	internal static bool LastCallUsedSdl { get; private set; }

	public bool HasClipboardText()
	{
		LastCallUsedSdl = SdlReady;
		return SdlReady && SDL_HasClipboardText();
	}

	public string GetClipboardText()
	{
		LastCallUsedSdl = SdlReady;
		if ( !SdlReady ) return string.Empty;

		//SDL hands back a copy that we own.
		var ptr = SDL_GetClipboardText();
		if ( ptr == IntPtr.Zero ) return string.Empty;

		try { return Marshal.PtrToStringUTF8( ptr ) ?? string.Empty; }
		finally { SDL_free( ptr ); }
	}

	public void SetClipboardText( string text )
	{
		LastCallUsedSdl = SdlReady;
		if ( SdlReady ) SDL_SetClipboardText( text ?? string.Empty );
	}

	public void ClearClipboardText()
	{
		LastCallUsedSdl = SdlReady;
		if ( SdlReady ) SDL_SetClipboardText( string.Empty );
	}

	public void MessageBox( string title, string message )
	{
		LastCallUsedSdl = SdlReady;
		if ( SdlReady ) SDL_ShowSimpleMessageBox( SDL_MESSAGEBOX_ERROR, title, message, IntPtr.Zero );
	}

	public int GetDefaultMonitorIndex()
	{
		var ids = GetDisplayIds();
		if ( ids.Length == 0 ) return 0;

		var primary = SDL_GetPrimaryDisplay();
		var index = Array.IndexOf( ids, primary );
		return index >= 0 ? index : 0;
	}

	public int GetWindowMonitorIndex( IntPtr sdlWindow )
	{
		if ( sdlWindow == IntPtr.Zero ) return -1;

		var ids = GetDisplayIds();
		if ( ids.Length == 0 ) return -1;

		var display = SDL_GetDisplayForWindow( sdlWindow );
		return display == 0 ? -1 : Array.IndexOf( ids, display );
	}

	public bool GetDesktopResolution( int monitorIndex, ref int width, ref int height, ref uint refreshRate )
	{
		var ids = GetDisplayIds();
		if ( monitorIndex < 0 || monitorIndex >= ids.Length )
			return false;

		var mode = SDL_GetDesktopDisplayMode( ids[monitorIndex] );
		if ( mode == IntPtr.Zero )
			return false;

		var head = Marshal.PtrToStructure<SdlDisplayModeHead>( mode );
		width = head.Width;
		height = head.Height;
		refreshRate = (uint)MathF.Round( head.RefreshRate );
		return true;
	}

	public float GetDiagonalDpi( int monitorIndex )
	{
		var ids = GetDisplayIds();
		if ( ids.Length == 0 ) return 96f;

		//The window's own monitor where it is one of ours; the primary display otherwise. Asking the
		//primary display for every window made a game on a differently-scaled monitor use the wrong scale.
		var display = monitorIndex >= 0 && monitorIndex < ids.Length
			? ids[monitorIndex]
			: SDL_GetPrimaryDisplay();

		//SDL reports a scale where 1.0 is 96 DPI, which is the same quantity callers recover by dividing this by 96 - see Screen.DesktopScale.
		var scale = SDL_GetDisplayContentScale( display );
		return scale > 0f ? scale * 96f : 96f;
	}

	public bool SafeRemoveFile( string file )
	{
		try
		{
			if ( System.IO.File.Exists( file ) ) System.IO.File.Delete( file );
			return true;
		}
		catch ( System.IO.IOException ) { return false; }
		catch ( UnauthorizedAccessException ) { return false; }
	}
}
