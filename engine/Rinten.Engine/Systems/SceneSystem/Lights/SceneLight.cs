using NativeEngine;

namespace Rinten;

/// <summary>Base class for light scene objects for use with a <see cref="SceneWorld"/>.</summary>
[Expose]
public class SceneLight : SceneObject
{
	internal SceneLight() { }
	internal SceneLight( HandleCreationData _ ) { }

	/// <summary>Color and brightness of the light</summary>
	public Color LightColor { get; set; } = Color.White;

	//The five metres a PointLight starts at
	/// <summary>How far the light reaches, in metres.</summary>
	public float Radius { get; set; } = 5.0f;

	/// <summary>The power the squared distance is raised to; one is the inverse square.</summary>
	public float Falloff { get; set; } = 1.0f;

	/// <summary>Whether the light casts shadows; only a <see cref="Light"/> component's light gets shadow maps.</summary>
	public bool ShadowsEnabled { get; set; } = true;

	public enum FogLightingMode
	{
		None,
		Baked,
		Dynamic,
		DynamicNoShadows
	}


	//Which way it points.
	internal Vector3 WorldDirection => Transform.Rotation.Forward;

	[Obsolete( "Use ScenePointLight (or stop fucking using SceneObjects at all)" )]
	public SceneLight( SceneWorld sceneWorld, Vector3 position, float radius, Color color )
	{
		Assert.IsValid( sceneWorld );

		JoinWorld( sceneWorld );

		Position = position;
		Radius = radius;
		LightColor = color;
	}

	[Obsolete( "Use ScenePointLight (or stop fucking using SceneObjects at all)" )]
	public SceneLight( SceneWorld sceneWorld ) : this( sceneWorld, Vector3.Zero, 5.0f, Color.White * 10.0f )
	{

	}


}
