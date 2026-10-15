namespace Rinten;

//The bake keeps a group's glow on its own layer; the frame scales that layer by the group's brightness
/// <summary>Bakes this object's glow onto the room apart, so it can be dimmed or switched off while playing.</summary>
[Expose]
[Title( "Baked Glow" )]
[Category( "Light" )]
[Icon( "highlight" )]
public sealed class BakedGlow : Component, Component.ExecuteInEditor
{
	/// <summary>How many groups a bake keeps apart: one layer each.</summary>
	public const int Groups = 4;

	//Glows of one group share a layer and change together; the brightest switched on decides it
	/// <summary>Which of the bake's glow layers this object's light goes into.</summary>
	[Property, Range( 1, Groups )]
	public int Group { get; set; } = 1;

	//The material's own glow on the object is the material's to change; this is the light it baked onto the room
	/// <summary>How bright the baked light of its group is now: one as baked, nought off.</summary>
	[Property, Range( 0, 4 )]
	public float Brightness { get; set; } = 1.0f;

	/// <summary>The group as a layer index, nought to three.</summary>
	internal int Layer => Math.Clamp( Group, 1, Groups ) - 1;
}
