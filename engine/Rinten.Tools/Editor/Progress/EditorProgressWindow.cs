using System;
using Rinten;
using Rinten.UI;

namespace Editor;

//Always-on-top desktop window for the progress bar; on Wayland it stays in the editor overlay
internal static class EditorProgressWindow
{
	//How big the window is. The bar fills it.
	const int Width = 360;
	const int Height = 64;

	//The gap between the window and the corner of the screen.
	const int Margin = 16;

	static CoreUiWindow shell;

	/// <summary>Whether this desktop will hold a window that floats above everything.</summary>
	public static bool Possible =>
		!OperatingSystem.IsBrowser() &&
		!string.Equals( CoreWindow.WindowSystem, "wayland", StringComparison.OrdinalIgnoreCase );

	/// <summary>The bar in it, made with the window the first time it is wanted.</summary>
	public static EditorProgressBar Bar { get; private set; }

	/// <summary>Makes the window if it is not there, and hands back the bar in it - or null where the desktop would not give us the window, which is the caller's sign to put the bar on the editor's overlay instead.</summary>
	public static EditorProgressBar Open()
	{
		if ( Bar is { IsValid: true } ) return Bar;

		if ( !Possible ) return null;

		//Utility: no frame, no resizing, above everything, never taking the keyboard. Glass, because the bar
		//draws its own plate and what is around its rounded corners has to be the desktop rather than a grey slab.
		shell = CoreUiWindow.Open( "Progress - Rinten editor", Width, Height, utility: true, glass: true );

		if ( shell is null )
		{
			EditorProgress.Log.Warning( "no window for the progress bar - it goes on the editor's overlay instead" );
			return null;
		}

		//The look every panel in the editor wears. Its own window has no root that anybody has dressed yet.
		EditorDesign.Apply( shell.Root );

		Bar = shell.Root.AddChild<EditorProgressBar>();
		Bar.FillsItsOwnWindow();

		Place();

		//Nothing to show yet: the window is made the moment there is a job, and Shown is what puts it up once the bar has drawn a frame.
		shell.Shown = false;

		EditorProgress.Log.Info( $"the progress bar has a window of its own ({CoreWindow.WindowSystem})" );

		return Bar;
	}

	//Bottom-left of the usable desktop area, recomputed on each show in case the screen changed
	static void Place()
	{
		if ( shell is null ) return;

		var (x, y, width, height) = CoreWindow.UsableDesktop();

		if ( width <= 1 || height <= 1 ) return;

		//Its own size as the window system counts it, which the scale made of Width and Height when it opened
		var size = shell.Placement.Size;

		shell.Placement = new Rect( x + Margin, y + height - size.y - Margin, size.x, size.y );
	}

	/// <summary>On the screen while there is something to say, and away when there is not.</summary>
	public static void Shown( bool wanted )
	{
		if ( shell is null ) return;

		if ( wanted == shown ) return;

		shown = wanted;

		if ( wanted ) Place();

		shell.Shown = wanted;
	}

	static bool shown;
}
