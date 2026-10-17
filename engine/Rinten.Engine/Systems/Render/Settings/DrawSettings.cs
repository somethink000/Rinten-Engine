namespace Rinten.Rendering;

//Toggles for each renderer optimisation; all default on, turn one off to see if it breaks the image
internal static partial class DrawSettings
{
	//Whether the world is drawn through its clusters - the cull pass and the material passes. Wherever the frame is the full one: clusters are how
	//the world is drawn, not a choice. The portable frame has no compute stage to cull with and draws every instance the classic way - see RenderTier.
	internal static bool Clusters => !CoreRenderDevice.PortableFrame;

	//Bumped whenever something changes which path an instance takes, so the scene's tables resolve again.
	internal static int PathGeneration { get; private set; }

	//The same, for something the engine found out rather than something somebody set - see CoreResources.StandsIn.
	internal static void PathChanged() => PathGeneration++;


	//The only occlusion there is. It happens inside the dispatch, against the depth this frame's pre-pass drew, so switching it off leaves the frustum test and nothing else - which is what to do to find out whether a thing that failed to draw was culled by depth or by something else entirely.
	/// <summary>Ask the cull pass the second question as well as the first: not only whether a copy is in view but whether anything nearer already covers it.</summary>
	[ConVar( "r_occlusion", Help = "Also cull what the depth already covers (0 leaves the frustum alone)." )]
	internal static bool Occlusion { get; set; } = true;

	/// <summary>Bake the fog into a sheet of slices once instead of marching it per fragment</summary>
	[ConVar( "r_fog_sheet", Help = "Work the volumetric fog out once into a sheet of slices (0 marches it per pixel)." )]
	internal static bool FogSheet { get; set; } = true;

	/// <summary>Whether a texture's finest levels are kept on the device only while something near enough to show them wears it - see CoreTextureStreaming. Off, every texture is whole.</summary>
	[ConVar( "r_texture_streaming", Help = "Keep a texture's finest levels only while something near enough wears it (0 keeps every texture whole)." )]
	internal static bool TextureStreaming { get; set; } = true;

	/// <summary>Megabytes the streamed textures may hold on the device; past it the ones wanted least give up levels first. Nought: no limit past what is needed.</summary>
	[ConVar( "r_texture_budget", ConVarFlags.Saved, Min = 0, Max = 65536,
		Help = "Megabytes the streamed textures may hold on the device (0: no limit past what is needed)." )]
	internal static int TextureBudget { get; set; } = 0;

	/// <summary>Screen pixels across one froxel of the fog sheet; 12 is the measured cost/quality balance</summary>
	[ConVar( "r_fog_sheet_grain", Min = 2, Max = 16,
		Help = "How many screen pixels across one froxel of the fog sheet is (smaller is finer and dearer)." )]
	internal static int FogSheetGrain { get; set; } = 12;

	/// <summary>Present window frames on their own thread so the next frame can start meanwhile</summary>
	[ConVar( "r_submit_thread",
		Help = "Present each window frame on a thread of its own while the next frame is being prepared." )]
	internal static bool SubmitThread { get; set; } = true;

	[ConVar( "r_mesh_lod_pixels", Min = 0.1f, Max = 16.0f,
		Help = "How much error on the screen, in pixels, a cluster may carry before its finer children are drawn instead. A shadow map is cut the same way in its own texels." )]
	internal static float MeshLodPixels { get; set; } = 1.0f;

	/// <summary>Things smaller than this many pixels across are not drawn; measured per whole object</summary>
	[ConVar( "r_cull_pixels", Min = 0.0f, Max = 64.0f,
		Help = "How few pixels across a thing may be before it is not drawn (0 draws all)." )]
	internal static float CullPixels { get; set; } = 2.0f;

	/// <summary>Radiance below which a lamp stops reaching; trims radius and shadow range. 0 trusts radius</summary>
	[ConVar( "r_light_cutoff", Min = 0.0f, Max = 0.5f,
		Help = "Radiance below which a lamp is dropped (0 trusts the authored radius)." )]
	internal static float LightCutoff { get; set; } = 0.001f;

	//FXAA runs in compose (the portable frame's finish off a page), so it forces the world drawn aside (~0.2 ms)
	/// <summary>Whether the edges of the finished picture are softened.</summary>
	[ConVar( "r_fxaa", ConVarFlags.Saved, Help = "Soften the edges of the finished picture (0 off)." )]
	internal static bool Fxaa { get; set; } = true;

	/// <summary>MSAA sample count (0 is none); only the web frame reads it, the desktop ignores it</summary>
	[ConVar( "r_msaa", ConVarFlags.Saved, Min = 0, Max = 16,
		Help = "Samples per pixel (0 off). The web's frame reads it; the desktop does not yet." )]
	internal static int Msaa { get; set; } = 4;

	/// <summary>How a world drawn smaller than its window is upscaled to it; temporal modes also do AA</summary>
	[ConVar( "r_upscaling", ConVarFlags.Saved, Min = 0, Max = 5,
		Help = "How the world reaches the window: 0 native, 1 stretch, 2 FSR1, 3 FSR3, 4 DLSS, 5 temporal (TAA/TAAU)." )]
	internal static int Upscaler { get; set; } = 0;

	//The settings screen wrote this into a convar nobody had registered, so it was a number that went nowhere. RCAS takes it as stops softer than the sharpest - see CoreUpscale.RcasStrength.
	/// <summary>How sharp FSR1's RCAS pass leaves the picture, nought none and one the most it will do.</summary>
	[ConVar( "r_fsr_rcas_sharpness", ConVarFlags.Saved, Min = 0.0f, Max = 1.0f,
		Help = "FSR1 sharpening, 0 none to 1 the most." )]
	internal static float Fsr1Sharpness { get; set; } = 0.25f;

	/// <summary>FSR 3's preset: 0 ultra performance (a third), 1 performance (a half), 2 balanced (1/1.7), 3 quality (1/1.5), 4 native anti-aliasing.</summary>
	[ConVar( "r_fsr3_quality", ConVarFlags.Saved, Min = 0, Max = 4,
		Help = "FSR3 preset: 0 ultra performance, 1 performance, 2 balanced, 3 quality, 4 native AA." )]
	internal static int Fsr3Quality { get; set; } = 3;

	/// <summary>FSR 3's own RCAS pass after the upscale, nought off.</summary>
	[ConVar( "r_fsr3_sharpness", ConVarFlags.Saved, Min = 0.0f, Max = 1.0f,
		Help = "FSR3 sharpening, 0 off to 1 the most." )]
	internal static float Fsr3Sharpness { get; set; } = 0.5f;

	/// <summary>DLSS's preset: 0 ultra performance, 1 performance, 2 balanced, 3 quality, 4 DLAA (native, anti-aliasing only).</summary>
	[ConVar( "r_dlss_quality", ConVarFlags.Saved, Min = 0, Max = 4,
		Help = "DLSS preset: 0 ultra performance, 1 performance, 2 balanced, 3 quality, 4 DLAA." )]
	internal static int DlssQuality { get; set; } = 3;

	/// <summary>Which of DLSS's networks draws: nought is the one the driver and the library shipped with the game think best, which is the newest the library knows; the rest are NGX's render presets by number (1 is A). See CoreDlss.</summary>
	[ConVar( "r_dlss_preset", ConVarFlags.Saved, Min = 0, Max = 26,
		Help = "DLSS render preset: 0 default (newest), else NGX preset number (1 = A, 10 = J, 11 = K...)." )]
	internal static int DlssPreset { get; set; } = 0;

	/// <summary>For a first run of DLSS on a card this was not written on: one flips the sign of the motion it is handed, two the sign of the jitter. Nought is what NVIDIA's guide asks for, as read - see CoreDlss.</summary>
	[ConVar( "r_dlss_flip", Min = 0, Max = 3,
		Help = "DLSS sign check: 1 flips the motion vectors, 2 the jitter (0 = as the guide says)." )]
	internal static int DlssFlip { get; set; } = 0;

	/// <summary>How much of the world's own resolution the engine's temporal upscaler draws: one is temporal anti-aliasing at the window's size, a half draws a quarter of the pixels.</summary>
	[ConVar( "r_taa_scale", ConVarFlags.Saved, Min = 0.33f, Max = 1.0f,
		Help = "Render scale of the engine's temporal upscaler (1 = TAA at native)." )]
	internal static float TemporalScale { get; set; } = 1.0f;

	/// <summary>RCAS over the engine's temporal upscaler, nought off.</summary>
	[ConVar( "r_taa_sharpness", ConVarFlags.Saved, Min = 0.0f, Max = 1.0f,
		Help = "Sharpening after the engine's temporal upscaler, 0 off to 1 the most." )]
	internal static float TemporalSharpness { get; set; } = 0.2f;

	/// <summary>The texture detail a temporal upscaler is drawn with, as the log2 of how much sharper than the world's own resolution asks for. Nought is the ordinary rule for the render size - log2 of the render scale - and each upscaler's guide asks for that; less is blurrier textures and less shimmer.</summary>
	[ConVar( "r_upscaler_mip_bias", ConVarFlags.Saved, Min = -2.0f, Max = 2.0f,
		Help = "Extra texture LOD bias under a temporal upscaler (0 = log2(render/display))." )]
	internal static float UpscalerMipBias { get; set; } = 0.0f;

	/// <summary>How much smaller than the window the world is drawn when an upscaler is active</summary>
	[ConVar( "r_upscaler_render_scale", ConVarFlags.Saved, Min = 0.25f, Max = 1.0f,
		Help = "How much smaller than the window the world is drawn." )]
	internal static float UpscalerScale { get; set; } = 0.75f;

	/// <summary>What the exposure is when it is not following the scene.</summary>
	[ConVar( "r_exposure", Min = 0.01f, Max = 16.0f,
		Help = "The exposure used when r_exposure_auto is off." )]
	internal static float Exposure { get; set; } = 1.0f;
}
