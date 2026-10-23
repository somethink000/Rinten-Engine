using NativeEngine;

namespace Rinten.Engine.Settings;

public partial class RenderSettings
{
	public int ResolutionWidth
	{
		get => VideoSettings.Get<int>( "defaultreswidth", 1920 );
		set => VideoSettings.Set<int>( "defaultreswidth", value );
	}

	public int ResolutionHeight
	{
		get => VideoSettings.Get<int>( "defaultresheight", 1080 );
		set => VideoSettings.Set<int>( "defaultresheight", value );
	}

	/// <summary>Fullscreen by default in a shipped game; only the player's own choice is saved, flags override once</summary>
	public bool Fullscreen
	{
		get => ForcedFullscreen ?? VideoSettings.Get<bool>( "fullscreen", FullscreenByDefault );
		set => VideoSettings.Set<bool>( "fullscreen", value );
	}

	//Default when nothing is saved: windowed on web (needs a user gesture) and in the editor
	internal static bool FullscreenByDefault => !Application.IsEditor && !Application.OnWeb;

	//-fullscreen or -windowed , beating what is saved without writing over it - the same bargain Forced makes for vsync.
	private static bool? ForcedFullscreen
	{
		get
		{
			//Read once, for the same reason the vsync switch is: the window asks for this every time it attaches, and a command line does not change.
			if ( forcedScreenRead ) return forcedScreen;

			if ( Rinten.Utility.CommandLine.HasSwitch( "-fullscreen" ) ) forcedScreen = true;
			else if ( Rinten.Utility.CommandLine.HasSwitch( "-windowed" ) ) forcedScreen = false;
			else forcedScreen = null;

			forcedScreenRead = true;

			return forcedScreen;
		}
	}

	private static bool? forcedScreen;
	private static bool forcedScreenRead;

	public bool Borderless
	{
		get => VideoSettings.Get<bool>( "borderless", true );
		set => VideoSettings.Set<bool>( "borderless", value );
	}

	/// <summary>Whether a finished frame waits for the display before it is shown.</summary>
	public bool VSync
	{
		get => Forced ?? VideoSettings.Get<bool>( "vsync", false );
		set => VideoSettings.Set<bool>( "vsync", value );
	}

	//-vsync 1/0 overrides the saved value without saving, so measurements don't alter settings
	private static bool? Forced
	{
		get
		{
			//Read once. The command line does not change while the process runs, and parsing it on every read was a string
			//and a char[] for every window, every frame - the window asks for the vsync setting each time it attaches.
			if ( forcedRead ) return forced;

			var asked = Rinten.Utility.CommandLine.GetSwitchInt( "-vsync", -1 );

			forced = asked < 0 ? null : asked != 0;
			forcedRead = true;

			return forced;
		}
	}

	private static bool? forced;
	private static bool forcedRead;

	/// <summary>How much the edges are smoothed.</summary>
	public MultisampleAmount AntiAliasQuality
	{
		get
		{
			//None, which is also all this renderer can currently do.
			var defaultValue = RenderMultisampleType.RENDER_MULTISAMPLE_4X;
			var value = VideoSettings.Get( "aaquality", defaultValue );

			if ( !Enum.IsDefined( typeof( RenderMultisampleType ), value ) )
				value = defaultValue;

			return value.FromEngine();
		}

		set
		{
			VideoSettings.Set( "aaquality", value.ToEngine() );

			Rinten.Rendering.DrawSettings.Msaa = value.SampleCount();
		}
	}

	internal struct VideoModeSnapshot
	{
		public int Width, Height;
		public bool Fullscreen, Borderless, VSync;
		public MultisampleAmount AntiAlias;
		public int MaxFps, MaxFpsInactive;
		public float Fov;
	}

	internal VideoModeSnapshot CaptureSnapshot() => new VideoModeSnapshot
	{
		Width = ResolutionWidth,
		Height = ResolutionHeight,
		Fullscreen = Fullscreen,
		Borderless = Borderless,
		VSync = VSync,
		AntiAlias = AntiAliasQuality,
		MaxFps = MaxFrameRate,
		MaxFpsInactive = MaxFrameRateInactive,
		Fov = DefaultFOV,
	};

	internal void RestoreSnapshot( VideoModeSnapshot snap )
	{
		ResolutionWidth = snap.Width;
		ResolutionHeight = snap.Height;
		Fullscreen = snap.Fullscreen;
		Borderless = snap.Borderless;
		VSync = snap.VSync;
		AntiAliasQuality = snap.AntiAlias;
		MaxFrameRate = snap.MaxFps;
		MaxFrameRateInactive = snap.MaxFpsInactive;
		DefaultFOV = snap.Fov;

		//Put back on the window too, or a cancelled change stays on the screen while the settings say it went.
		ApplyVideoMode();
		VideoSettings.Save();
	}

	//Applies fullscreen, borderless or windowed mode; borderless only matters when not fullscreen
	private void ApplyVideoMode() => ApplyVideoMode( CoreGameRenderer.Window );

	//The same, to a given window - a game's, the moment it is opened, before the renderer has been handed it. See CoreAppSystem.OpenGameWindow.
	internal void ApplyVideoMode( CoreWindow window )
	{
		//No changing this in the editor
		if ( Application.IsEditor || window is null )
			return;

		window.SetFullscreen( Fullscreen );

		//A page has no frame and no size of its own to set - the browser has them.
		if ( Fullscreen || OperatingSystem.IsBrowser() )
			return;

		if ( Borderless )
		{
			window.CoverDisplay();
			return;
		}

		window.SetBordered( true );
		window.SetSize( Math.Max( ResolutionWidth, 640 ), Math.Max( ResolutionHeight, 480 ) );
	}

	public unsafe VideoDisplayMode[] DisplayModes( bool windowed )
	{
		//What the game's screen can be driven at, as the screen lists it - the same list whether windowed or not; the
		//parameter stays for the code written against it. A size the screen offers at several refresh rates is there once for each.
		if ( CoreGameRenderer.Window?.DisplayModes() is { Length: > 0 } modes )
		{
			return modes
				.Select( x => new VideoDisplayMode { Width = x.Width, Height = x.Height, RefreshRate = x.RefreshRate, Format = ImageFormat.RGBA8888 } )
				.ToArray();
		}

		//No game window to ask - the editor, a headless run: the desktop is the one mode known, and the one a window ends up at anyway.
		int width = 0, height = 0;
		uint refreshRate = 0;

		if ( !EnginePlatform.GetDesktopResolution( EnginePlatform.GetDefaultMonitorIndex(),
			ref width, ref height, ref refreshRate ) )
			return Array.Empty<VideoDisplayMode>();

		return new[]
		{
			new VideoDisplayMode
			{
				Width = width,
				Height = height,
				RefreshRate = refreshRate,
				Format = ImageFormat.RGBA8888,
			},
		};
	}


	public struct VideoDisplayMode
	{
		public int Width { get; set; }
		public int Height { get; set; }
		public float RefreshRate { get; set; }
		public ImageFormat Format { get; set; }
	}
}
