namespace Rinten;

public partial class Scene
{
	/// <summary>The scene's baked lighting - its settings and its last bake.</summary>
	public BakedLighting Lighting => _lighting ??= new BakedLighting( this );

	BakedLighting _lighting;

	//The bake when the scene has one made, without making one to ask
	internal BakedLighting LightingIfAny => _lighting;

	//Back to no bake and default settings, for a scene being emptied or destroyed
	void ResetLighting()
	{
		_lighting?.Release();
		_lighting = null;
	}
}
