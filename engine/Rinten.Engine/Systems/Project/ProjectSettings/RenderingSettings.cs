namespace Rinten;

/// <summary>The machines a rendering setting works on.</summary>
[Flags]
public enum RenderPlatforms
{
	/// <summary>Windows, Linux, macOS - anything with a desktop driver.</summary>
	Desktop = 1,

	/// <summary>Android and iOS.</summary>
	Mobile = 2,

	/// <summary>A browser, through WebGPU.</summary>
	Web = 4,

	Everywhere = Desktop | Mobile | Web,
}

//Read twice: by RenderingSettings.Apply, which turns a setting off in a build for a machine that cannot run it, and by the project settings page, which prints Note under it in red. One table, so the warning in the editor and the value in the build cannot drift apart.
/// <summary>Where a setting does anything, and what to say about it where it does not.</summary>
[AttributeUsage( AttributeTargets.Property )]
public sealed class RunsOnAttribute : Attribute
{
	public RunsOnAttribute( RenderPlatforms platforms, string note = null )
	{
		Platforms = platforms;
		Note = note;
	}

	/// <summary>What this setting works on.</summary>
	public RenderPlatforms Platforms { get; }

	/// <summary>What the page says under it, in red.</summary>
	public string Note { get; }
}

//A setting the machine cannot run is turned off as this is applied, not asked about in the frame: an export for the web has no compute shaders, and what it ships is a build whose cluster path was never switched on rather than one that tests for it. See RunsOnAttribute.
/// <summary>How this project draws its world. Applied once, when the project's settings are first read, into the convars the renderer reads - so a setting costs a static field read where it is used and nothing at all per draw.</summary>
[Expose]
public class RenderingSettings : ConfigData
{
	/// <summary>What this build runs on.</summary>
	public static RenderPlatforms Running => OperatingSystem.IsBrowser()
		? RenderPlatforms.Web
		: OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
			? RenderPlatforms.Mobile
			: RenderPlatforms.Desktop;

	/// <summary>How much of the screen the depth is reduced to before the cull reads it, in patches across. Finer catches more of what is covered and costs more to build; coarser is cheaper and lets a little through.</summary>
	[Group( "Mesh Rendering" ), Title( "Occlusion Detail" ), Order( 3 ), Range( 64, 2048 )]
	[RunsOn( RenderPlatforms.Desktop | RenderPlatforms.Mobile,
		"Web builds and phones on the portable frame draw every model the old way, with no depth to test against." )]
	public int OcclusionGrain { get; set; } = 1024;

	/// <summary>Max screen error in pixels before a cluster's finer children are drawn; the only LOD knob.</summary>
	[Group( "Mesh Rendering" ), Title( "Detail, In Pixels" ), Order( 4 ), Range( 0.1f, 16.0f ), Step( 0.1f )]
	[RunsOn( RenderPlatforms.Desktop | RenderPlatforms.Mobile,
		"Web builds and phones on the portable frame draw every model at full detail." )]
	public float LodPixels { get; set; } = 1.0f;

	/// <summary>How small a thing may be on the screen, across, before it is not drawn at all. Nought draws everything, however far away.</summary>
	[Group( "Mesh Rendering" ), Title( "Smallest Thing Drawn" ), Order( 5 ), Range( 0.0f, 64.0f ), Step( 0.5f )]
	[RunsOn( RenderPlatforms.Desktop | RenderPlatforms.Mobile,
		"Not read by web builds or phones on the portable frame." )]
	public float CullPixels { get; set; } = 2.0f;

	/// <summary>Kinds of thing that keep their detail differently with distance - characters, debris - each a curve of detail against metres from the camera. A renderer names one in its Detail Group; one that names none keeps the world's own detail at every distance. See <see cref="LodGroup"/>.</summary>
	[Group( "Mesh Rendering" ), Title( "Detail Groups" ), Order( 6 )]
	[RunsOn( RenderPlatforms.Desktop | RenderPlatforms.Mobile,
		"Web builds and phones on the portable frame draw every model at full detail." )]
	public List<LodGroup> LodGroups { get; set; } = new();

	/// <summary>Whether a setting of this name does anything on this machine.</summary>
	public static bool RunsHere( string property )
	{
		var found = typeof( RenderingSettings ).GetProperty( property );
		if ( found is null ) return true;

		var says = (RunsOnAttribute)Attribute.GetCustomAttribute( found, typeof( RunsOnAttribute ) );

		if ( says is null ) return true;
		if ( ( says.Platforms & Running ) == 0 ) return false;

		//Devices on the portable frame run only what runs on web, whatever their platform
		return ( says.Platforms & RenderPlatforms.Web ) != 0 || !CoreRenderDevice.PortableFrame;
	}

	/// <summary>Writes settings into renderer convars, skipping ones unsupported here or overridden by hand</summary>
	public void Apply()
	{
		if ( !ConVarSystem.Overridden( "r_mesh_lod_pixels" ) )
			Rendering.DrawSettings.MeshLodPixels = LodPixels;

		if ( !ConVarSystem.Overridden( "r_cull_pixels" ) )
			Rendering.DrawSettings.CullPixels = CullPixels;

		if ( !ConVarSystem.Overridden( "r_occlusion_grain" ) )
			CoreDepthPyramid.Grain = OcclusionGrain;

		//Not a convar: a table of curves, baked once here and copied into the cull's parameters each frame.
		Rendering.LodGroups.Set( LodGroups );
	}

	private static bool applied;

	//Applies the project's settings the first time the renderer asks, and not again until the project changes. A frame's cost is this bool.
	internal static void EnsureApplied()
	{
		if ( applied ) return;

		applied = true;
		ProjectSettings.Rendering.Apply();
	}

	//The project changed: read them again on the next frame.
	internal static void Forget() => applied = false;
}
