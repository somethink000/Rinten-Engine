using System.Threading;

namespace Editor;

[DropObject( "sprite", "sprite" )]
partial class SpriteDropObject : BaseDropObject
{
	Sprite sprite;
	Texture texture;
	float aspect = 1f;

	protected override async Task Initialize( string dragData, CancellationToken token )
	{
		Asset asset = await InstallAsset( dragData, token );

		if ( asset is null )
			return;

		if ( token.IsCancellationRequested )
			return;

		PackageStatus = "Loading Sprite";
		sprite = asset.LoadResource<Sprite>();
		if ( sprite is null )
		{
			PackageStatus = "Failed to load Sprite";
			return;
		}

		PackageStatus = "";
		if ( token.IsCancellationRequested )
			return;

		var frame = sprite.Animations.FirstOrDefault()?.Frames.FirstOrDefault();
		texture = frame?.ToTexture();
		if ( texture is null )
		{
			PackageStatus = "Sprite has no textures";
			return;
		}

		//A cell of a sheet is shaped like the cell, not like the sheet.
		var pixels = frame.PixelSize;
		if ( pixels.x > 0 && pixels.y > 0 )
		{
			aspect = pixels.y / pixels.x;
		}
	}

	public override void OnUpdate()
	{
		using var scope = Gizmo.Scope( "DropObject", traceTransform );

		Gizmo.Draw.Color = Color.White;
		if ( texture is not null && aspect != 0 )
		{
			Gizmo.Draw.Sprite( Vector3.Zero, new Vector2( 0.25f, 0.25f * aspect ), texture, true );
		}

		if ( !string.IsNullOrWhiteSpace( PackageStatus ) )
		{
			Gizmo.Draw.Text( PackageStatus, new Transform( Bounds.Center ), "Inter", 14 );
		}
	}

	public override async Task OnDrop()
	{
		await WaitForLoad();

		if ( texture is null )
			return;

		using var scene = SceneEditorSession.Scope();

		using ( SceneEditorSession.Active.UndoScope( "Drop Texture" ).WithGameObjectCreations().Push() )
		{
			GameObject = new GameObject();
			GameObject.Name = texture.ResourceName;
			GameObject.WorldTransform = traceTransform;

			var spriteComponent = GameObject.Components.GetOrCreate<SpriteRenderer>();
			spriteComponent.Sprite = sprite;

			EditorScene.Selection.Clear();
			EditorScene.Selection.Add( GameObject );
		}
	}
}
