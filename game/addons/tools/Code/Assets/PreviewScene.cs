namespace Editor.Assets;

[AssetPreview( "scene" )]
class PreviewScene : AssetPreview
{
	public override bool IsAnimatedPreview => true;
	public override float PreviewWidgetCycleSpeed => 0.2f;

	public override bool UsePixelEvaluatorForThumbs => true;

	/// <summary>A scene thumbnail meant loading the whole scene, ticking it and taking a screenshot - seconds of main-thread work per scene in a folder, and it throws outright on a scene whose components no longer resolve. The browser shows the scene icon instead; the live preview here still loads on request.</summary>
	public override bool CanRenderThumbnail => false;

	public override float VideoLength => 6.0f;

	Rotation baseRotation;

	public PreviewScene( Asset asset ) : base( asset )
	{

	}

	/// <summary>The preview world is built empty. A scene's own content is only ever loaded by the live preview here, on request.</summary>
	public override Task InitializeAsset()
	{
		//Nothing loads the scene up front any more - CanRenderThumbnail is false, so the only thing that ever asks for its content is the live preview.
		return Task.CompletedTask;
	}

	public override Widget CreateWidget( Widget parent )
	{
		return new ThumbnailPreviewWidget( this, LoadSceneContent );
	}

	internal async Task LoadSceneContent()
	{
		using ( EditorUtility.DisableTextureStreaming() )
		{
			var sf = Asset.LoadResource<SceneFile>();
			if ( sf is null ) return;



			using ( Scene.Push() )
			{
				Scene.Load( sf );
			}

			while ( Scene.IsLoading )
			{
				await Task.Delay( 100 );
			}

			using ( Scene.Push() )
			{
				if ( !Scene.Camera.IsValid() )
				{
					var camera = new GameObject( true, "camera" );
					var cc = camera.Components.Create<CameraComponent>();
					cc.FieldOfView = 40;
					cc.BackgroundColor = "#19181a";
					cc.ZFar = 2540f;
					cc.ZNear = 0.0254f;
				}

				//tonemapping is gonna fuck us up
				foreach ( var x in Scene.GetAllComponents<Tonemapping>() )
				{
					x.Destroy();
				}

				Scene.Camera.FieldOfView = 60;

				if ( !Scene.Camera.Components.Get<Bloom>().IsValid() )
				{
					var bloom = Scene.Camera.Components.Create<Bloom>();
					bloom.Threshold = 0.2f;
					bloom.Strength = 0.2f;
				}

				SceneCenter = Scene.Camera.WorldPosition;
				baseRotation = Scene.Camera.WorldRotation;

				PreviewLighting.Light( Scene, baseRotation.Angles() );
			}
		}
	}

	float time = 0;

	public override void UpdateScene( float cycle, float timeStep )
	{
		time += timeStep;

		//Loading a scene in here takes the previous one's objects with it, and the camera can be one of them. Without this the thumbnail
		//throws instead of drawing, and the scene it was drawing keeps its blank picture for as long as the editor keeps retrying it.
		if ( !Camera.IsValid() )
			return;

		using ( Scene.Push() )
		{
			if ( IsRenderingVideo )
			{
				Camera.WorldPosition = SceneCenter + baseRotation.Right * (cycle).Remap( 0, 1, -1, 1 ) * (2.54f);
				Camera.WorldRotation = baseRotation;
			}
			else
			{
				Camera.WorldPosition = SceneCenter + baseRotation.Right * MathF.Sin( cycle * 2.0f ) * (2.54f);
				Camera.WorldRotation = baseRotation;
			}
		}

		base.UpdateScene( cycle, timeStep );
	}

}
