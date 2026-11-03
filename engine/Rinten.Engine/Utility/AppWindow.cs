namespace Rinten;

/// <summary>The game's own window, for an app that draws its own title bar; does nothing in the editor or on the web</summary>
public static class AppWindow
{
	//Played in the editor the game is a viewport, and the window there is the editor's
	private static CoreWindow Window => Application.IsEditor ? null : CoreGameRenderer.Window is { Adopted: false } w ? w : null;

	private static bool frameless;

	private static bool? sleepWhenIdle;
	private static long wokeAt;

	/// <summary>Slows to <see cref="IdleFrameRate"/> while nobody uses the app and it is not <see cref="Busy"/>; on by default for an app project</summary>
	public static bool SleepWhenIdle
	{
		get => sleepWhenIdle ?? Application.IsApp;
		set => sleepWhenIdle = value;
	}

	/// <summary>Frames a second while idle; any event wakes the app at once, so this only paces what nobody is looking at</summary>
	public static int IdleFrameRate { get; set; } = 4;

	/// <summary>Seconds without events before the app counts as idle</summary>
	public static float IdleAfter { get; set; } = 1.5f;

	/// <summary>Keeps the full frame rate while true: set it while the app shows work in progress, such as streaming text</summary>
	public static bool Busy { get; set; }

	/// <summary>The full frame rate for a moment, for a change that should show at once</summary>
	public static void Wake() => System.Threading.Volatile.Write( ref wokeAt, System.Diagnostics.Stopwatch.GetTimestamp() );

	/// <summary>Whether the app is asleep right now</summary>
	public static bool Idle
	{
		get
		{
			if ( !SleepWhenIdle || Busy || !HasWindow ) return false;
			var sinceWake = ( System.Diagnostics.Stopwatch.GetTimestamp() - System.Threading.Volatile.Read( ref wokeAt ) ) / (double)System.Diagnostics.Stopwatch.Frequency;
			return sinceWake > IdleAfter && CoreWindow.SecondsSinceEvent > IdleAfter;
		}
	}

	/// <summary>Whether there is a desktop window to frame, move and resize</summary>
	public static bool HasWindow => Window is not null;

	/// <summary>No system frame: the game draws the caption and says where it drags with <see cref="SetDragAreas"/></summary>
	public static bool Frameless
	{
		get => frameless && HasWindow;
		set
		{
			if ( Window is not { } window || frameless == value ) return;

			frameless = value;
			if ( value ) window.TakeTheFrame();
			else window.GiveBackTheFrame();
		}
	}

	/// <summary>The parts of a frameless window's caption that drag it, in screen pixels; buttons stay out of them</summary>
	public static void SetDragAreas( params Rect[] areas )
	{
		if ( Window is not { } window ) return;

		window.DragStrips = (areas ?? [])
			.Where( a => a.Width > 0 && a.Height > 0 )
			.Select( a => ((int)a.Left, (int)a.Top, (int)a.Width, (int)a.Height) )
			.ToArray();
	}

	/// <summary>Whether the window fills its screen</summary>
	public static bool IsMaximized => Window?.IsMaximized ?? false;

	/// <summary>Folds the window away to the taskbar</summary>
	public static void Minimize() => Window?.Minimize();

	/// <summary>Maximizes the window, or gives it back its size if it already is</summary>
	public static void ToggleMaximized()
	{
		if ( Window is not { } window ) return;

		if ( window.IsMaximized ) window.Restore();
		else window.Maximize();
	}

	/// <summary>What the taskbar calls the window</summary>
	public static void SetTitle( string title ) => Window?.SetTitle( title );

	/// <summary>Closes the app; in the editor it leaves play mode</summary>
	public static void Close() => Game.Close();

	/// <summary>Asks for a folder in the system's own dialog; null if it was dismissed. Blocks until answered</summary>
	public static string PickFolder( string title = null, string startIn = null )
	{
		if ( OperatingSystem.IsBrowser() || OperatingSystem.IsAndroid() ) return null;

		return CoreFileDialog.Folder( title, startIn )?.FirstOrDefault();
	}

	/// <summary>Asks for files to open in the system's own dialog; null if it was dismissed. Blocks until answered</summary>
	public static List<string> PickFiles( string title = null, string startIn = null, bool many = false )
	{
		if ( OperatingSystem.IsBrowser() || OperatingSystem.IsAndroid() ) return null;

		return CoreFileDialog.Open( title, startIn, null, null, many );
	}
}
