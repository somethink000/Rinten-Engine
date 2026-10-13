namespace Rinten;

/// <summary>
/// Settings for the scene's 2D lighting. Optional: 2D lighting is on wherever the scene
/// has a <see cref="Light2D"/> or a <see cref="GlobalLight2D"/>, and works with the
/// defaults without one of these. Put it on the camera.
/// </summary>
[Expose]
[Title( "Lighting 2D" )]
[Category( "Light 2D" )]
[Icon( "light" )]
public class Lighting2D : Component, Component.ExecuteInEditor
{
	/// <summary>What of the 2D lighting's working to show over the picture, for seeing what it is made of.</summary>
	public enum DebugView
	{
		/// <summary>Nothing: the picture as it is lit.</summary>
		None,

		/// <summary>What glows and what blocks: glowing sprites in their colour, occluders in grey.</summary>
		Scene,

		/// <summary>The light itself, as every pixel gets it, before anything is multiplied by it.</summary>
		Light,
	}

	/// <summary>
	/// Whether occluders are shadowed. Off, occluders - solid tiles, walls - are lit by every light in
	/// reach as if nothing blocked them, and their shadows fall only on what is not an occluder: the
	/// background, characters, props. What most 2D games want. On, light stops at an occluder's surface:
	/// only the edges that see a light are lit, and one occluder shadows another.
	/// </summary>
	[Property]
	public bool SelfShadow { get; set; }

	/// <summary>
	/// How big the picture the light is worked out in is, against the screen.
	/// Smaller is cheaper and blurrier.
	/// </summary>
	[Property, Range( 0.25f, 1.0f ), Group( "Quality" )]
	public float ResolutionScale { get; set; } = 0.5f;

	/// <summary>
	/// How far past the edge of the screen lights and occluders are still seen,
	/// as a fraction of the screen, so a light just out of shot still lights the
	/// edge of it and an occluder just out of shot still casts into it.
	/// </summary>
	[Property, Range( 0.0f, 0.5f ), Group( "Quality" )]
	public float Margin { get; set; } = 0.15f;

	/// <summary>Shows a step of the 2D lighting over the picture instead of the picture.</summary>
	[Property, Group( "Debug" )]
	public DebugView Debug { get; set; }
}
