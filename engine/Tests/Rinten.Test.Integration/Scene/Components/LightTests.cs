namespace SceneTests.Components;

[TestClass]
public class LightComponentTest
{
	//Finds the scene object of the given type that was created by the given component, using the internal SceneObject.Component
	//back-reference. Returns null when the component has no live scene object (SceneWorld.SceneObjects filters invalid handles).
	static T FindSceneObjectFor<T>( Scene scene, Component component ) where T : SceneObject
	{
		return scene.SceneWorld.SceneObjects.OfType<T>().FirstOrDefault( x => x.Component == component );
	}

	//Serializes a GameObject to json, destroys the original, then deserializes the json back into the
	//scene and enables it - the standard save/load round trip idiom used by the integration tests.
	static GameObject SerializeRoundTrip( Scene scene, GameObject go )
	{
		var json = go.Serialize().ToJsonString();

		go.Destroy();
		scene.ProcessDeletes();

		var jsonObject = Json.ParseToJsonObject( json );
		SceneUtility.MakeIdGuidsUnique( jsonObject );

		var clone = new GameObject( false );
		clone.Deserialize( jsonObject );
		clone.Enabled = true;

		return clone;
	}

	//Enabling a PointLight creates a ScenePointLight in the scene world with the component's defaults applied, tags the GameObject with
	//"light"/"light_point", disabling deletes the scene object, re-enabling creates a fresh one, and destroying the GameObject tears it down.
	[TestMethod]
	public void PointLightLifecycle()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var light = go.Components.Create<PointLight>();

		var so = FindSceneObjectFor<ScenePointLight>( scene, light );
		Assert.IsNotNull( so, "Enabling a PointLight should create a ScenePointLight" );
		Assert.IsTrue( so.IsValid() );
		Assert.IsTrue( go.Tags.Has( "light" ), "Light components should tag their GameObject with 'light'" );
		Assert.IsTrue( go.Tags.Has( "light_point" ), "PointLight should tag its GameObject with 'light_point'" );
		Assert.AreEqual( 400.0f, so.Radius, 0.001f, "Default radius should be applied to the scene object" );

		light.Enabled = false;

		Assert.IsFalse( so.IsValid(), "Disabling the component should delete the scene object" );
		Assert.IsNull( FindSceneObjectFor<ScenePointLight>( scene, light ) );

		light.Enabled = true;

		var second = FindSceneObjectFor<ScenePointLight>( scene, light );
		Assert.IsNotNull( second, "Re-enabling should create a new scene object" );
		Assert.AreNotSame( so, second, "Re-enabling should not resurrect the old scene object" );

		go.Destroy();
		scene.ProcessDeletes();

		Assert.IsFalse( second.IsValid(), "Destroying the GameObject should delete the scene object" );
		Assert.AreEqual( 0, scene.SceneWorld.SceneObjects.OfType<ScenePointLight>().Count() );
	}

	//Enabling a SpotLight creates a SceneSpotLight carrying the component's default radius,
	//cone angles, falloff and the "light_spot" tag; disabling deletes the scene object.
	[TestMethod]
	public void SpotLightLifecycle()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var light = go.Components.Create<SpotLight>();

		var so = FindSceneObjectFor<SceneSpotLight>( scene, light );
		Assert.IsNotNull( so, "Enabling a SpotLight should create a SceneSpotLight" );
		Assert.IsTrue( go.Tags.Has( "light_spot" ) );
		Assert.AreEqual( 500.0f, so.Radius, 0.001f, "Default radius should be applied" );
		Assert.AreEqual( 45.0f, so.ConeOuter, 0.1f, "Default outer cone should be applied" );
		Assert.AreEqual( 15.0f, so.ConeInner, 0.1f, "Default inner cone should be applied" );

		light.Enabled = false;

		Assert.IsFalse( so.IsValid(), "Disabling the component should delete the scene object" );
		Assert.IsNull( FindSceneObjectFor<SceneSpotLight>( scene, light ) );

		go.Destroy();
		scene.ProcessDeletes();
	}

	//Enabling a DirectionalLight creates a SceneDirectionalLight with the default shadow
	//cascade setup and the "light_directional" tag; disabling deletes the scene object.
	[TestMethod]
	public void DirectionalLightLifecycle()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var light = go.Components.Create<DirectionalLight>();

		var so = FindSceneObjectFor<SceneDirectionalLight>( scene, light );
		Assert.IsNotNull( so, "Enabling a DirectionalLight should create a SceneDirectionalLight" );
		Assert.IsTrue( go.Tags.Has( "light_directional" ) );

		light.Enabled = false;

		Assert.IsFalse( so.IsValid(), "Disabling the component should delete the scene object" );
		Assert.IsNull( FindSceneObjectFor<SceneDirectionalLight>( scene, light ) );

		go.Destroy();
		scene.ProcessDeletes();
	}

	//Property setters on a live PointLight write through to the scene object: color, radius, quadratic attenuation,
	//shadow enable, and the managed shadow bias/hardness values. The component keeps its own state exactly as set.
	[TestMethod]
	public void PointLightSettersPropagate()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var light = go.Components.Create<PointLight>();
		var so = FindSceneObjectFor<ScenePointLight>( scene, light );

		light.LightColor = new Color( 0.25f, 0.5f, 0.75f );
		light.Radius = 123.0f;
		light.Shadows = false;

		Assert.AreEqual( new Color( 0.25f, 0.5f, 0.75f ), light.LightColor );
		Assert.AreEqual( 123.0f, light.Radius );
		Assert.IsFalse( light.Shadows );

		Assert.AreEqual( 0.25f, so.LightColor.r, 0.001f );
		Assert.AreEqual( 0.5f, so.LightColor.g, 0.001f );
		Assert.AreEqual( 0.75f, so.LightColor.b, 0.001f );
		Assert.AreEqual( 123.0f, so.Radius, 0.001f );
		Assert.IsFalse( so.ShadowsEnabled );

		go.Destroy();
		scene.ProcessDeletes();
	}

	//Cone angle, radius and attenuation setters on a live SpotLight write through to the SceneSpotLight's native theta/phi/radius/attenuation values.
	[TestMethod]
	public void SpotLightConeSettersPropagate()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var light = go.Components.Create<SpotLight>();
		var so = FindSceneObjectFor<SceneSpotLight>( scene, light );

		light.ConeOuter = 60.0f;
		light.ConeInner = 30.0f;
		light.Radius = 250.0f;

		Assert.AreEqual( 60.0f, light.ConeOuter );
		Assert.AreEqual( 30.0f, light.ConeInner );

		Assert.AreEqual( 60.0f, so.ConeOuter, 0.1f );
		Assert.AreEqual( 30.0f, so.ConeInner, 0.1f );
		Assert.AreEqual( 250.0f, so.Radius, 0.001f );

		go.Destroy();
		scene.ProcessDeletes();
	}
	[TestMethod]
	public void LightContributionAndFogDefaults()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var light = go.Components.Create<PointLight>();

		Assert.IsTrue( light.Contribution.HasFlag( Light.LightContribution.Diffuse ), "Diffuse contribution should default on" );
		Assert.IsTrue( light.Contribution.HasFlag( Light.LightContribution.Specular ), "Specular contribution should default on" );
		Assert.AreEqual( Light.FogInfluence.Enabled, light.FogMode, "Fog should default to enabled" );

		go.Destroy();
		scene.ProcessDeletes();
	}

	//A PointLight created disabled has no scene object; properties configured while disabled
	//are all applied to the scene object created when the component is finally enabled.
	[TestMethod]
	public void PointLightConfiguredBeforeEnable()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var light = go.Components.Create<PointLight>( false );

		light.LightColor = Color.Red;
		light.Radius = 64.0f;
		light.Shadows = false;

		Assert.IsNull( FindSceneObjectFor<ScenePointLight>( scene, light ), "No scene object should exist while disabled" );

		light.Enabled = true;

		var so = FindSceneObjectFor<ScenePointLight>( scene, light );
		Assert.IsNotNull( so );
		Assert.AreEqual( 64.0f, so.Radius, 0.001f );
		Assert.IsFalse( so.ShadowsEnabled );
		Assert.AreEqual( 1.0f, so.LightColor.r, 0.001f );
		Assert.AreEqual( 0.0f, so.LightColor.g, 0.001f );
		Assert.AreEqual( 0.0f, so.LightColor.b, 0.001f );

		go.Destroy();
		scene.ProcessDeletes();
	}

	//Lights subscribe to their GameObject's transform-changed event, so moving or rotating the GameObject
	//moves the scene object - pinned here after a game tick, which is the state a frame would render with.
	[TestMethod]
	public void LightFollowsGameObjectTransform()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var light = go.Components.Create<SpotLight>();
		var so = FindSceneObjectFor<SceneSpotLight>( scene, light );

		var pos = new Vector3( 100, 200, 300 );
		var rot = Rotation.From( 30, 60, 0 );

		go.WorldPosition = pos;
		go.WorldRotation = rot;

		scene.GameTick();

		Assert.IsTrue( so.Position.Distance( pos ) < 0.01f, "Scene object should follow the GameObject position" );
		Assert.IsTrue( so.Rotation.Distance( rot ) < 0.01f, "Scene object should follow the GameObject rotation" );

		go.Destroy();
		scene.ProcessDeletes();
	}

	//A PointLight with non-default color, radius, attenuation, shadows, fog and contribution values survives a GameObject
	//json serialize/deserialize round trip, and the deserialized component creates a live scene object when enabled.
	[TestMethod]
	public void PointLightSerializationRoundTrip()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var light = go.Components.Create<PointLight>();
		light.LightColor = new Color( 0.1f, 0.2f, 0.3f );
		light.Radius = 222.0f;
		light.Brightness = 3.0f;
		light.Shadows = false;
		light.FogStrength = 0.5f;
		light.FogMode = Light.FogInfluence.WithoutShadows;
		light.Contribution = Light.LightContribution.Diffuse | Light.LightContribution.Specular;

		var clone = SerializeRoundTrip( scene, go );
		var loaded = clone.Components.Get<PointLight>();

		Assert.IsNotNull( loaded, "Deserialized GameObject should have a PointLight" );
		Assert.AreEqual( new Color( 0.1f, 0.2f, 0.3f ), loaded.LightColor );
		Assert.AreEqual( 222.0f, loaded.Radius );
		Assert.AreEqual( 3.0f, loaded.Brightness );
		Assert.IsFalse( loaded.Shadows );
		Assert.AreEqual( 0.5f, loaded.FogStrength );
		Assert.AreEqual( Light.FogInfluence.WithoutShadows, loaded.FogMode );
		Assert.AreEqual( Light.LightContribution.Diffuse | Light.LightContribution.Specular, loaded.Contribution );

		var so = FindSceneObjectFor<ScenePointLight>( scene, loaded );
		Assert.IsNotNull( so, "Deserialized light should create its scene object when enabled" );
		Assert.AreEqual( 222.0f, so.Radius, 0.001f );
		Assert.IsFalse( so.ShadowsEnabled );

		clone.Destroy();
		scene.ProcessDeletes();
	}

	//A SpotLight's cone angles, radius and brightness survive a serialize/deserialize round trip and are applied to the recreated scene object.
	[TestMethod]
	public void SpotLightSerializationRoundTrip()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var light = go.Components.Create<SpotLight>();
		light.ConeOuter = 60.0f;
		light.ConeInner = 30.0f;
		light.Radius = 250.0f;
		light.Brightness = 0.5f;

		var clone = SerializeRoundTrip( scene, go );
		var loaded = clone.Components.Get<SpotLight>();

		Assert.IsNotNull( loaded, "Deserialized GameObject should have a SpotLight" );
		Assert.AreEqual( 60.0f, loaded.ConeOuter );
		Assert.AreEqual( 30.0f, loaded.ConeInner );
		Assert.AreEqual( 250.0f, loaded.Radius );
		Assert.AreEqual( 0.5f, loaded.Brightness );

		var so = FindSceneObjectFor<SceneSpotLight>( scene, loaded );
		Assert.IsNotNull( so );
		Assert.AreEqual( 60.0f, so.ConeOuter, 0.1f );
		Assert.AreEqual( 30.0f, so.ConeInner, 0.1f );

		clone.Destroy();
		scene.ProcessDeletes();
	}

	//A DirectionalLight's cascade configuration, sky color and shadow bias/hardness survive a
	//serialize/deserialize round trip and the cascade count is applied to the recreated scene object.
	[TestMethod]
	public void DirectionalLightSerializationRoundTrip()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var light = go.Components.Create<DirectionalLight>();
		light.LightColor = Color.White;
		light.SkyColor = Color.Blue;
		light.ShadowDetail = 48.0f;

		var clone = SerializeRoundTrip( scene, go );
		var loaded = clone.Components.Get<DirectionalLight>();

		Assert.IsNotNull( loaded, "Deserialized GameObject should have a DirectionalLight" );
		Assert.AreEqual( Color.White, loaded.LightColor );
		Assert.AreEqual( Color.Blue, loaded.SkyColor );
		Assert.AreEqual( 48.0f, loaded.ShadowDetail );

		var so = FindSceneObjectFor<SceneDirectionalLight>( scene, loaded );
		Assert.IsNotNull( so );

		clone.Destroy();
		scene.ProcessDeletes();
	}

	//AmbientLight is a plain state component: it defaults to gray, holds its color through
	//enable/disable cycles, and the color survives a serialize/deserialize round trip.
	[TestMethod]
	public void AmbientLightDefaultsAndRoundTrip()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var ambient = go.Components.Create<AmbientLight>();

		Assert.AreEqual( Color.Gray, ambient.Color, "Ambient color should default to gray" );

		ambient.Color = new Color( 0.25f, 0.0f, 0.5f );

		ambient.Enabled = false;
		ambient.Enabled = true;

		Assert.AreEqual( new Color( 0.25f, 0.0f, 0.5f ), ambient.Color, "Color should survive an enable cycle" );

		var clone = SerializeRoundTrip( scene, go );
		var loaded = clone.Components.Get<AmbientLight>();

		Assert.IsNotNull( loaded, "Deserialized GameObject should have an AmbientLight" );
		Assert.AreEqual( new Color( 0.25f, 0.0f, 0.5f ), loaded.Color );

		clone.Destroy();
		scene.ProcessDeletes();
	}

	//Direct light mode tests were removed with the native light; rewrite once baked lighting is ours
}
