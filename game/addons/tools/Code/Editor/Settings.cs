namespace Editor;

public static class SettingsMenus
{
	/// <summary>Whether the finished picture has its edges softened by the filter.</summary>
	[Menu( "Editor", "Settings/FXAA" )]
	public static bool Fxaa
	{
		get => ConsoleSystem.GetValueInt( "r_fxaa" ) != 0;
		set => ConsoleSystem.SetValue( "r_fxaa", value ? "1" : "0" );
	}

	/// <summary>Whether lighting is traced with hardware rays (r_rt), where the card can.</summary>
	[Menu( "Editor", "Settings/Ray Tracing" )]
	public static bool RayTracing
	{
		get => EditorUtility.RenderSettings.RayTracing;
		set => EditorUtility.RenderSettings.RayTracing = value;
	}

	//No MSAA entry: no desktop target is multisampled, and r_msaa is read by the web's frame alone

	[Menu( "Editor", "Settings/Frame Rate Limit/10fps" )]
	public static bool Frame10
	{
		get => EditorUtility.RenderSettings.MaxFrameRate == 10;
		set => EditorUtility.RenderSettings.MaxFrameRate = 10;
	}


	[Menu( "Editor", "Settings/Frame Rate Limit/30fps" )]
	public static bool Frame30
	{
		get => EditorUtility.RenderSettings.MaxFrameRate == 30;
		set => EditorUtility.RenderSettings.MaxFrameRate = 30;
	}

	[Menu( "Editor", "Settings/Frame Rate Limit/60fps" )]
	public static bool Frame60
	{
		get => EditorUtility.RenderSettings.MaxFrameRate == 60;
		set => EditorUtility.RenderSettings.MaxFrameRate = 60;
	}

	[Menu( "Editor", "Settings/Frame Rate Limit/144fps" )]
	public static bool Frame144
	{
		get => EditorUtility.RenderSettings.MaxFrameRate == 144;
		set => EditorUtility.RenderSettings.MaxFrameRate = 144;
	}

	[Menu( "Editor", "Settings/Frame Rate Limit/240fps" )]
	public static bool Frame240
	{
		get => EditorUtility.RenderSettings.MaxFrameRate == 240;
		set => EditorUtility.RenderSettings.MaxFrameRate = 240;
	}

	/// <summary>No cap at all, which is what zero means to the loop.</summary>
	[Menu( "Editor", "Settings/Frame Rate Limit/Off" )]
	public static bool FrameUncapped
	{
		get => EditorUtility.RenderSettings.MaxFrameRate <= 0;
		set => EditorUtility.RenderSettings.MaxFrameRate = 0;
	}

	[Menu( "Editor", "Settings/VSync" )]
	public static bool VSync
	{
		get => EditorUtility.RenderSettings.VSync;
		set
		{
			EditorUtility.RenderSettings.VSync = value;
			EditorUtility.RenderSettings.Apply();
		}
	}

	[Menu( "Editor", "Settings/VR" )]
	public static bool VR
	{
		get => EditorUtility.VR.Enabled;
		set => EditorUtility.VR.Enabled = value;
	}
}
