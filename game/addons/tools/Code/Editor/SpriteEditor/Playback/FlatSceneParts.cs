namespace Editor.SpriteEditor;

//The pieces a flat, orthographic scene is made of. Both the preview and the spritesheet
//importer build one, out of the same three things. Call with the scene pushed.
internal static class FlatSceneParts
{
	/// <summary>How tall the view is in world units - the size every sprite in it is placed against.</summary>
	public const float ViewHeight = 100f;

	public static CameraComponent AddOrthographicCamera()
	{
		var camera = new GameObject( true, "camera" ).Components.Create<CameraComponent>();

		camera.Orthographic = true;
		camera.OrthographicHeight = ViewHeight;
		camera.BackgroundColor = EditorPreferences.CameraBackgroundColor;

		return camera;
	}

	/// <summary>A sprite renderer that fills the view, for the picture itself.</summary>
	public static SpriteRenderer AddSprite( string name, Vector3 position )
	{
		var gameObject = new GameObject( true, name );
		gameObject.Flags = gameObject.Flags.WithFlag( GameObjectFlags.EditorOnly, true );

		var renderer = gameObject.Components.Create<SpriteRenderer>();
		renderer.WorldPosition = position;
		renderer.Size = ViewHeight;
		renderer.IsSorted = true;

		return renderer;
	}

	/// <summary>A dark plate behind the picture, so a transparent sprite has something to be seen against.</summary>
	public static SpriteRenderer AddBackdrop( Vector3 position )
	{
		var backdrop = new GameObject( true, "background" ).Components.Create<SpriteRenderer>();

		backdrop.WorldPosition = position;
		backdrop.Size = ViewHeight;
		backdrop.IsSorted = true;
		backdrop.Color = Theme.ControlBackground.Darken( 0.9f );
		backdrop.Sprite = Sprite.FromTexture( Texture.White );

		return backdrop;
	}
}
