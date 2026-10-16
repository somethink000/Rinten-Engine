namespace Editor.Assets;

[AssetPreview( "prefab" )]
class PreviewPrefab : AssetPreview
{
	public override bool IsAnimatedPreview => true;
	public override float PreviewWidgetCycleSpeed => 0.2f;

	public override bool UsePixelEvaluatorForThumbs => true;

	public PreviewPrefab( Asset asset ) : base( asset )
	{

	}

	/// <summary>Only initialize if we're in the live preview, so we don't load a whole scene when showing a thumbnail</summary>
	public override Task InitializeAsset()
	{
		if ( !IsRenderingThumbnail )
			return Task.CompletedTask;

		return LoadPrefabContent();
	}

	public override Widget CreateWidget( Widget parent )
	{
		return new ThumbnailPreviewWidget( this, LoadPrefabContent );
	}

	internal Task LoadPrefabContent()
	{
		using ( EditorUtility.DisableTextureStreaming() )
		{
			var pf = Asset.LoadResource<PrefabFile>();
			if ( pf is null ) return Task.CompletedTask;

			using ( Scene.Push() )
			{
				PrimaryObject = GameObject.Clone( pf );
				PrimaryObject.WorldPosition = 0;
				SceneCenter = PrimaryObject.GetBounds().Center;
				SceneSize = PrimaryObject.GetBounds().Size;

				PreviewLighting.Light( Scene, Camera.WorldRotation.Angles() );
			}
		}

		return Task.CompletedTask;
	}

}
