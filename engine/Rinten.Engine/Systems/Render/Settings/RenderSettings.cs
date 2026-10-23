using System;
using NativeEngine;


namespace Rinten.Engine.Settings;

/// <summary>User graphics settings</summary>
public partial class RenderSettings
{
	internal static RenderSettings Instance = new RenderSettings();

	internal CookieContainer VideoSettings { get; } = new( "video", true );

	public event Action OnVideoSettingsChanged;
	internal RenderQualityProfiles Config { get; } = new();

	internal RenderSettings()
	{
		Config.SetDefaults( this );
	}

	public int MaxFrameRate
	{
		get => ConVarSystem.GetInt( "fps_max", 144, true );
		set => ConVarSystem.SetInt( "fps_max", value, true );
	}

	public int MaxFrameRateInactive
	{
		get => ConVarSystem.GetInt( "fps_max_inactive", 100, true );
		set => ConVarSystem.SetInt( "fps_max_inactive", value, true );
	}

	public int MaxFrameRateMenu
	{
		get => ConVarSystem.GetInt( "fps_max_menu", 144, true );
		set => ConVarSystem.SetInt( "fps_max_menu", value, true );
	}

	public float DefaultFOV
	{
		get => ConVarSystem.GetFloat( "default_fov", 80, true );
		set => ConVarSystem.SetFloat( "default_fov", value, true );
	}

	public TextureQuality TextureQuality
	{
		get => VideoSettings.Get<TextureQuality>( "texture.quality", TextureQuality.High );
		set
		{
			VideoSettings.Set<TextureQuality>( "texture.quality", value );
			Config.SetGroupConVars( "TextureQuality", value.ToString() );
		}
	}

	public VolumetricFogQuality VolumetricFogQuality
	{
		get => VideoSettings.Get<VolumetricFogQuality>( "volumetricfog.quality", VolumetricFogQuality.High );
		set
		{
			VideoSettings.Set<VolumetricFogQuality>( "volumetricfog.quality", value );
			Config.SetGroupConVars( "VolumetricFogQuality", value.ToString() );
		}
	}

	public PostProcessQuality PostProcessQuality
	{
		get => VideoSettings.Get<PostProcessQuality>( "postprocess.quality", PostProcessQuality.High );
		set
		{
			VideoSettings.Set<PostProcessQuality>( "postprocess.quality", value );
			Config.SetGroupConVars( "PostProcessQuality", value.ToString() );
		}
	}

	public ShadowQuality ShadowQuality
	{
		get => VideoSettings.Get<ShadowQuality>( "shadow.quality", ShadowQuality.High );
		set
		{
			VideoSettings.Set<ShadowQuality>( "shadow.quality", value );
			Config.SetGroupConVars( "ShadowQuality", value.ToString() );
		}
	}

	/// <summary>Whether lighting uses hardware ray tracing (RTX, RDNA2+, Arc); off by default.</summary>
	public bool RayTracing
	{
		get => ConVarSystem.GetInt( "r_rt", 0, true ) != 0;
		set => ConVarSystem.SetInt( "r_rt", value ? 1 : 0, true );
	}

	/// <summary>Whether this card can trace rays, so a menu offers RayTracing only where it works.</summary>
	public bool RayTracingAvailable => VkRays.On;

	public float MotionBlurScale
	{
		get => VideoSettings.Get<float>( "motionblur.scale", 1.0f );
		set
		{
			VideoSettings.Set<float>( "motionblur.scale", value );
			//The effect's own switch, by its name - see MotionBlur.UserScale.
			ConVarSystem.SetFloat( "r_motionblur_scale", value, true );
		}
	}

	//The upscaler's settings are convars that keep themselves - see DrawSettings.Upscaler - so a restarted
	//game shows and draws the same setting

	public UpscalerMode UpscalerMode
	{
		get => (UpscalerMode)ConVarSystem.GetInt( "r_upscaling", 0, true );
		set => ConVarSystem.SetInt( "r_upscaling", (int)value, true );
	}

	/// <summary>Render-resolution scale used by Stretch and FSR1 (40-100%).</summary>
	public float UpscalerRenderScale
	{
		get => ConVarSystem.GetFloat( "r_upscaler_render_scale", 0.75f, true );
		set => ConVarSystem.SetFloat( "r_upscaler_render_scale", Math.Clamp( value, 0.4f, 1.0f ), true );
	}

	/// <summary>FSR1 RCAS sharpness in [0..1]. Only used when <see cref="UpscalerMode"/> is FSR1.</summary>
	public float Fsr1Sharpness
	{
		get => ConVarSystem.GetFloat( "r_fsr_rcas_sharpness", 0.25f, true );
		set => ConVarSystem.SetFloat( "r_fsr_rcas_sharpness", Math.Clamp( value, 0.0f, 1.0f ), true );
	}

	/// <summary>FSR3 quality preset, a render-resolution multiplier; only with UpscalerMode FSR3.</summary>
	public Fsr3UpscalerQuality Fsr3UpscalerQuality
	{
		get => (Fsr3UpscalerQuality)ConVarSystem.GetInt( "r_fsr3_quality", (int)Fsr3UpscalerQuality.Quality, true );
		set
		{
			if ( value != Fsr3UpscalerQuality.Off )
				ConVarSystem.SetInt( "r_fsr3_quality", (int)value, true );
		}
	}

	/// <summary>FSR3 RCAS sharpness in [0..1]. Only used when <see cref="UpscalerMode"/> is FSR3.</summary>
	public float Fsr3Sharpness
	{
		get => ConVarSystem.GetFloat( "r_fsr3_sharpness", 0.5f, true );
		set => ConVarSystem.SetFloat( "r_fsr3_sharpness", Math.Clamp( value, 0.0f, 1.0f ), true );
	}

	/// <summary>DLSS quality preset, a render-resolution multiplier; only with UpscalerMode DLSS.</summary>
	public DlssQuality DlssQuality
	{
		get => (DlssQuality)ConVarSystem.GetInt( "r_dlss_quality", (int)DlssQuality.Quality, true );
		set
		{
			if ( value != DlssQuality.Off )
				ConVarSystem.SetInt( "r_dlss_quality", (int)value, true );
		}
	}

	/// <summary>Which DLSS network draws: nought the newest shipped, the rest NGX presets by number, 1 is A.</summary>
	public int DlssPreset
	{
		get => ConVarSystem.GetInt( "r_dlss_preset", 0, true );
		set => ConVarSystem.SetInt( "r_dlss_preset", Math.Clamp( value, 0, 26 ), true );
	}

	/// <summary>How much of the window the engine's temporal upscaler draws at; one is native TAA.</summary>
	public float TemporalRenderScale
	{
		get => ConVarSystem.GetFloat( "r_taa_scale", 1.0f, true );
		set => ConVarSystem.SetFloat( "r_taa_scale", Math.Clamp( value, 0.33f, 1.0f ), true );
	}

	/// <summary>Sharpening after the engine's temporal upscaler, in [0..1].</summary>
	public float TemporalSharpness
	{
		get => ConVarSystem.GetFloat( "r_taa_sharpness", 0.2f, true );
		set => ConVarSystem.SetFloat( "r_taa_sharpness", Math.Clamp( value, 0.0f, 1.0f ), true );
	}

	/// <summary>Whether a frame is generated between every two drawn; works with a temporal upscaler.</summary>
	public bool FrameGeneration
	{
		get => ConVarSystem.GetInt( "r_frame_generation", 0, true ) != 0;
		set => ConVarSystem.SetInt( "r_frame_generation", value ? 1 : 0, true );
	}

	/// <summary>Keeps the processor from running ahead: Anti-Lag or Reflex - 0 off, 1 on, 2 with boost.</summary>
	public int LowLatency
	{
		get => ConVarSystem.GetInt( "r_low_latency", 0, true );
		set => ConVarSystem.SetInt( "r_low_latency", Math.Clamp( value, 0, 2 ), true );
	}

	/// <summary>Whether an UpscalerMode works on this device - temporal needs the full frame, DLSS NGX.</summary>
	public static bool IsUpscalerModeSupported( UpscalerMode mode ) => CoreUpscale.Supported( mode );

	public void ResetVideoConfig()
	{
		int desktopWidth = 0;
		int desktopHeight = 0;
		uint desktopRefreshRate = 0;
		EnginePlatform.GetDesktopResolution( EnginePlatform.GetDefaultMonitorIndex(), ref desktopWidth, ref desktopHeight, ref desktopRefreshRate );
		ResolutionWidth = desktopWidth;
		ResolutionHeight = desktopHeight;

		//Whatever this build comes up as when nobody has chosen - a reset puts the setting back where
		//it started rather than picking windowed, which on a game is a choice the player did not make.
		Fullscreen = FullscreenByDefault;
		Borderless = true;
		VSync = true;

		//Four samples: a page's canvas and the portable frame's pass are multisampled; the full frame does not read it
		AntiAliasQuality = MultisampleAmount.Multisample4x;
		MaxFrameRate = 144;
		MaxFrameRateInactive = 60;
		MaxFrameRateMenu = 144;
		DefaultFOV = 75;
		UpscalerMode = UpscalerMode.Off;
		UpscalerRenderScale = 0.75f;
		Fsr1Sharpness = 0.25f;
		Fsr3UpscalerQuality = Fsr3UpscalerQuality.Quality;
		Fsr3Sharpness = 0.5f;
		DlssQuality = DlssQuality.Quality;
		DlssPreset = 0;
		TemporalRenderScale = 1.0f;
		TemporalSharpness = 0.2f;
		FrameGeneration = false;

		VideoSettings.Save();
	}

	public void Apply()
	{
		ApplyVideoMode();

		OnVideoSettingsChanged?.Invoke();

		VideoSettings.Save();
	}

	//Benchmarks share settings, so only resolution and hardware vary
	internal void ApplySettingsForBenchmarks()
	{
		ResetVideoConfig();

		Fullscreen = false;
		Borderless = false;
		VSync = false;
		AntiAliasQuality = MultisampleAmount.MultisampleNone;
		MaxFrameRate = 10000;
		MaxFrameRateInactive = 10000;
		DefaultFOV = 75;
		ResolutionWidth = 1920;
		ResolutionHeight = 1080;
	}

}
