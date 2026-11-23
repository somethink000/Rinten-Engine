using NativeEngine;

namespace Rinten;

/// <summary>Access screen dimension etc.</summary>
public static class Screen
{
	/// <summary>The total size of the game screen</summary>
	public static Vector2 Size { get; internal set; }

	/// <summary>The width of the game screen. Equal to Screen.x</summary>
	public static float Width => Size.x;

	/// <summary>The height of the game screen. Equal to Screen.y</summary>
	public static float Height => Size.y;

	/// <summary>The aspect ratio of the screen. Equal to Width/Height, or 1 if the screen size is not yet known.</summary>
	public static float Aspect => Height > 0 ? Width / Height : 1f;

	/// <summary>Screen pixels covered on each edge by notches, rounded corners or system bars; zero on desktop</summary>
	public static Rinten.UI.Margin SafeInsets { get; internal set; }

	/// <summary>The part of the screen nothing covers - see <see cref="SafeInsets"/>. The whole screen where nothing does.</summary>
	public static Rect SafeArea
	{
		get
		{
			var insets = SafeInsets;

			return new Rect( insets.Left, insets.Top,
				Math.Max( Width - insets.Left - insets.Right, 0 ),
				Math.Max( Height - insets.Top - insets.Bottom, 0 ) );
		}
	}

	/// <summary>The desktop's dpi scale on the current monitor.</summary>
	public static float DesktopScale { get; private set; } = 1.0f;

	//The scale of the window the game is seen in; 0 before there is one
	private static float WindowScale()
	{
		if ( CoreGameRenderer.Window is { Adopted: false } window ) return window.DisplayScale;

		//Played in the editor: the interface window around the viewport
		return CoreUiWindow.HasPointerWindow ? CoreUiWindow.PointerScale : 0.0f;
	}

	/// <summary>Fullscreen state; on web it applies on the next user gesture, so it may read false briefly after set</summary>
	public static bool Fullscreen
	{
		get => CoreGameRenderer.Window?.IsFullscreen ?? false;

		set
		{
			//Null outside a standalone game - see Application.RenderSettings.
			if ( Application.RenderSettings is not { } settings ) return;

			settings.Fullscreen = value;
			settings.Apply();
		}
	}

	//Viewport the game draws into when it has no window (played in the editor); null otherwise
	internal static Vector2? Viewport { get; set; }

	internal static void UpdateFromEngine()
	{
		ThreadSafe.AssertIsMainThread();

		var width = 1024;
		var height = 1024;

		if ( !Application.IsUnitTest )
		{
			//The editor's game viewport, if the game is being played in one. It is asked first:
			//a process with an editor in it has no game window for the renderer to be holding.
			if ( Viewport is { } viewport && viewport.x >= 1 && viewport.y >= 1 )
			{
				width = viewport.x.CeilToInt();
				height = viewport.y.CeilToInt();
			}
			else
			{
					//Our own window, which the renderer is holding. Before the first frame there is none, and the default above stands.
					var size = CoreGameRenderer.Size;

					if ( size.Width > 0 && size.Height > 0 )
					{
						width = size.Width;
						height = size.Height;
					}
			}
		}

		//Every frame: a window moved to a monitor of another scale keeps its pixel size
		var scale = WindowScale();
		if ( scale > 0.0f ) DesktopScale = scale;

		var newSize = new Vector2( width, height );
		if ( newSize == Size )
			return;

		Size = new Vector2( width, height );

		RenderTarget.Flush();

		if ( scale <= 0.0f ) DesktopScale = EnginePlatform.GetDiagonalDpi() / 96.0f;
	}

	/// <summary>Converts a vertical field of view to a horizontal field of view based on the screen aspect ratio.</summary>
	public static float CreateVerticalFieldOfView( float fieldOfView )
	{
		return CreateVerticalFieldOfView( fieldOfView, Aspect );
	}

	/// <summary>Converts a vertical field of view to a horizontal field of view based on the given aspect ratio.</summary>
	public static float CreateVerticalFieldOfView( float fieldOfView, float aspectRatio )
	{
		float t = MathF.Tan( fieldOfView.DegreeToRadian() * 0.5f );
		return MathF.Atan( t * aspectRatio ).RadianToDegree() * 2.0f;
	}

}
