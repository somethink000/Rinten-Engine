namespace Editor;

public static class SettingsMenus
{
	//One kind at a time: SMAA T2x, CMAA2 or FXAA over the finished picture, TAA through the engine's temporal upscaler, or MSAA
	[Menu( "Editor", "Settings/Anti-Aliasing/Off" )]
	public static bool AntiAliasOff
	{
		get => !Smaa && !Cmaa && !Fxaa && !Taa && ConsoleSystem.GetValueInt( "r_msaa" ) < 2;
		set => AntiAlias( smaa: false, taa: false, msaa: 0 );
	}

	[Menu( "Editor", "Settings/Anti-Aliasing/SMAA T2x" )]
	public static bool Smaa
	{
		get => ConsoleSystem.GetValueInt( "r_smaa" ) != 0;
		set => AntiAlias( smaa: true, taa: false, msaa: 0 );
	}

	[Menu( "Editor", "Settings/Anti-Aliasing/CMAA2" )]
	public static bool Cmaa
	{
		get => ConsoleSystem.GetValueInt( "r_cmaa" ) != 0 && !Smaa;
		set => AntiAlias( smaa: false, taa: false, msaa: 0, cmaa: true );
	}

	[Menu( "Editor", "Settings/Anti-Aliasing/FXAA" )]
	public static bool Fxaa
	{
		get => ConsoleSystem.GetValueInt( "r_fxaa" ) != 0 && !Smaa && !Cmaa;
		set => AntiAlias( smaa: false, taa: false, msaa: 0, fxaa: true );
	}

	[Menu( "Editor", "Settings/Anti-Aliasing/TAA" )]
	public static bool Taa
	{
		get => ConsoleSystem.GetValueInt( "r_upscaling" ) == 5;
		set => AntiAlias( smaa: false, taa: true, msaa: 0 );
	}

	[Menu( "Editor", "Settings/Anti-Aliasing/MSAA 2x" )]
	public static bool Msaa2x
	{
		get => Multisampled( 2 );
		set => AntiAlias( smaa: false, taa: false, msaa: 2 );
	}

	[Menu( "Editor", "Settings/Anti-Aliasing/MSAA 4x" )]
	public static bool Msaa4x
	{
		get => Multisampled( 4 );
		set => AntiAlias( smaa: false, taa: false, msaa: 4 );
	}

	[Menu( "Editor", "Settings/Anti-Aliasing/MSAA 8x" )]
	public static bool Msaa8x
	{
		get => Multisampled( 8 );
		set => AntiAlias( smaa: false, taa: false, msaa: 8 );
	}

	//A temporal upscaler replaces MSAA while it is on, so a count shows only without one
	private static bool Multisampled( int samples )
		=> !Smaa && !Cmaa && !Fxaa && !Taa && ConsoleSystem.GetValueInt( "r_msaa" ) == samples;

	private static void AntiAlias( bool smaa, bool taa, int msaa, bool fxaa = false, bool cmaa = false )
	{
		ConsoleSystem.SetValue( "r_smaa", smaa ? "1" : "0" );
		ConsoleSystem.SetValue( "r_cmaa", cmaa ? "1" : "0" );
		ConsoleSystem.SetValue( "r_fxaa", fxaa ? "1" : "0" );
		ConsoleSystem.SetValue( "r_msaa", msaa.ToString() );

		//Leaving TAA goes back to drawing at the window's size; another upscaler is left as it was
		if ( taa ) ConsoleSystem.SetValue( "r_upscaling", "5" );
		else if ( Taa ) ConsoleSystem.SetValue( "r_upscaling", "0" );

		if ( taa ) ConsoleSystem.SetValue( "r_taa_scale", "1" );
	}

	/// <summary>Whether lighting is traced with hardware rays (r_rt), where the card can.</summary>
	[Menu( "Editor", "Settings/Ray Tracing" )]
	public static bool RayTracing
	{
		get => EditorUtility.RenderSettings.RayTracing;
		set => EditorUtility.RenderSettings.RayTracing = value;
	}

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
