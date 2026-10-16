namespace Editor;

/// <summary>The one light every editor preview is lit by: a sun turned to the view and the engine's sky.</summary>
public static class PreviewLighting
{
	/// <summary>The tag on the preview light's objects, which tells them from a scene's own lights.</summary>
	public const string Tag = "preview_light";

	/// <summary>Lights a scene seen from this view, unless it brings lights of its own; then the preview light goes.</summary>
	public static void Light( Scene scene, Angles view )
	{
		if ( !scene.IsValid() ) return;

		using var _ = scene.Push();

		if ( scene.GetAllComponents<Rinten.Light>().Any( x => !x.Tags.Has( Tag ) ) )
		{
			Remove( scene );
			return;
		}

		if ( scene.Children.Any( x => x.Tags.Has( Tag ) ) ) return;

		var go = new GameObject( true, "preview light" );
		go.Flags = GameObjectFlags.NotSaved | GameObjectFlags.Hidden;
		go.Tags.Add( Tag );

		//A light travels along its forward: from above the view and a little to one side, so the side facing the camera is lit
		go.WorldRotation = new Angles( 50, view.yaw - 35, 0 );

		var sun = go.AddComponent<DirectionalLight>();
		sun.Shadows = true;

		//Ambient light and reflections; a scene with its own sky keeps that one
		if ( scene.GetAllComponents<SkyBox2D>().Any() ) return;

		var sky = new GameObject( go, true, "preview sky" );
		sky.Flags = GameObjectFlags.NotSaved | GameObjectFlags.Hidden;
		sky.AddComponent<SkyBox2D>();
	}

	/// <summary>Takes the preview light out of a scene.</summary>
	public static void Remove( Scene scene )
	{
		foreach ( var go in scene.Children.Where( x => x.Tags.Has( Tag ) ).ToList() )
			go.Destroy();
	}

	/// <summary>Shows the camera's background colour instead of the sky, which still lights the scene.</summary>
	public static void HideSky( CameraComponent camera ) => camera.RenderExcludeTags.Add( "skybox" );
}
