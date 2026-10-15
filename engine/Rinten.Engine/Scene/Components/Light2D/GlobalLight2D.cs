namespace Rinten;

/// <summary>
/// The light everywhere on the 2D plane - what a sprite lit in 2D gets where no
/// other 2D light reaches it. More than one adds together.
/// </summary>
/// <remarks>
/// Flat ambient: the same everywhere, blocked by no occluder - what shadows are
/// lit by, so they are dark rather than black.
/// </remarks>
[Expose]
[Title( "Global Light 2D" )]
[Category( "Light 2D" )]
[Icon( "wb_sunny" )]
public class GlobalLight2D : Component, Component.ExecuteInEditor
{
	/// <summary>The colour of the light.</summary>
	[Property]
	public Color Color { get; set; } = Color.White;

	/// <summary>How strong the light is, as a multiplier on <see cref="Color"/>.</summary>
	[Property, Range( 0, 10 )]
	public float Intensity { get; set; } = 1.0f;
}
