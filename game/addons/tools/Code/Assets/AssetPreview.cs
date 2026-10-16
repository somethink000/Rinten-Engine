using System.Diagnostics;

namespace Editor.Assets;


public class AssetPreview : IDisposable
{
	public Asset Asset { get; private set; }

	public Scene Scene { get; private set; }

	public CameraComponent Camera => Scene.IsValid() ? Scene.Camera : null;

	/// <summary>Whether a thumbnail may be drawn by rendering this preview. A preview that has to load a whole scene before it can draw anything says no: the asset wears its type icon in the browser and the editor keeps its frame rate.</summary>
	public virtual bool CanRenderThumbnail => true;

	/// <summary>Background color for the preview camera. Persisted between previews via a cookie.</summary>
	public Color BackgroundColor
	{
		get => EditorCookie.Get( "AssetPreview.BackgroundColor", Theme.ControlBackground );
		set
		{
			EditorCookie.Set( "AssetPreview.BackgroundColor", value );

			if ( Scene.IsValid() && Camera.IsValid() )
				Camera.BackgroundColor = value;
		}
	}

	public Vector3 SceneCenter;
	public Vector3 SceneSize;
	public Vector2Int ScreenSize = 100;
	public GameObject PrimaryObject;

	/// <summary>If you want to slow down the cycle speed when displaying in a scene widget, you can do that here</summary>
	public virtual float PreviewWidgetCycleSpeed => 1.0f;

	/// <summary>If true, this preview will render the thumbnail multiple times in the cycle and pick the one with the least alpha and most luminance</summary>
	public virtual bool UsePixelEvaluatorForThumbs => false;

	/// <summary>Is this preview animated? If it's not animated then it's a waste of time rendering a video.</summary>
	public virtual bool IsAnimatedPreview => false;

	/// <summary>How long should the video be</summary>
	public virtual float VideoLength => 3.0f;

	protected bool IsRenderingThumbnail { get; private set; }
	protected bool IsRenderingVideo { get; private set; }

	public AssetPreview( Asset asset )
	{
		Asset = asset;
	}

	public virtual void Dispose()
	{
		Asset = null;

		Scene?.Destroy();
		Scene = default;
	}

	/// <summary>Create the world, camera and the shared preview light</summary>
	public virtual Task InitializeScene()
	{
		Scene = Scene.CreateEditorScene();
		Scene.Name = "Asset Preview";

		using ( Scene.Push() )
		{
			{
				var go = new GameObject( true, "camera" );
				var cc = go.AddComponent<CameraComponent>();
				cc.BackgroundColor = Color.Transparent;
				cc.WorldRotation = new Angles( 20, 180 + 45, 0 );
				cc.FieldOfView = 30.0f;
				cc.ZFar = 2540f;
				cc.ZNear = 0.00254f;
			}

			PreviewLighting.HideSky( Scene.Camera );
			PreviewLighting.Light( Scene, Scene.Camera.WorldRotation.Angles() );
		}

		return Task.CompletedTask;
	}

	/// <summary>Create the model or whatever</summary>
	public virtual Task InitializeAsset()
	{
		return Task.CompletedTask;
	}

	/// <summary>Create a widget to always show in the asset preview</summary>
	public virtual Widget CreateWidget( Widget parent )
	{
		return null;
	}

	/// <summary>A panel shown under the asset's page in the inspector, where the preview is something to use rather than a picture - hearing a sound from a distance. Standing on its own: the preview is disposed once it is made.</summary>
	public virtual Rinten.UI.Panel CreatePanel()
	{
		return null;
	}

	/// <summary>Preferred height of the preview panel. Scene-based previews want to be tall; flat previews like the sound waveform can override this to take up less vertical space. Width is unaffected.</summary>
	public virtual float PreferredHeight => 400;

	/// <summary>Create a toolbar docked above the preview's content</summary>
	public virtual Widget CreateToolbar()
	{
		return null;
	}

	//How much room is left around the thing being looked at.
	private const float Margin = 1.06f;

	/// <summary>Stands the camera where the whole of the thing fits the picture.</summary>
	public void FrameScene()
	{
		if ( !Camera.IsValid() || !PrimaryObject.IsValid() )
			return;

		var rotation = Camera.WorldRotation;
		var centre = PrimaryObject.WorldRotation * SceneCenter;

		var aspect = ScreenSize.y > 0 ? (float)ScreenSize.x / ScreenSize.y : 1.0f;
		if ( aspect <= 0.01f ) aspect = 1.0f;

		//The field of view a camera carries is the horizontal one - see CoreCameraFrustum,
		//which builds its projection from that and lets the vertical follow the aspect.
		var wide = MathF.Tan( Camera.FieldOfView.Clamp( 1.0f, 179.0f ).DegreeToRadian() * 0.5f );
		var tall = wide / aspect;

		if ( wide < 0.001f || tall < 0.001f )
			return;

		var half = SceneSize * 0.5f;
		var forward = rotation.Forward;
		var right = rotation.Right;
		var up = rotation.Up;

		var distance = 0.0f;

		for ( var corner = 0; corner < 8; corner++ )
		{
			var offset = new Vector3(
				( corner & 1 ) == 0 ? -half.x : half.x,
				( corner & 2 ) == 0 ? -half.y : half.y,
				( corner & 4 ) == 0 ? -half.z : half.z );

			//Where this corner sits in the camera's own axes, measured from the middle of the thing. Depth
			//counts against the distance: a corner nearer the camera than the middle needs more room behind it.
			var depth = Vector3.Dot( offset, forward );

			distance = MathF.Max( distance, ( MathF.Abs( Vector3.Dot( offset, right ) ) / wide ) - depth );
			distance = MathF.Max( distance, ( MathF.Abs( Vector3.Dot( offset, up ) ) / tall ) - depth );
		}

		distance = MathF.Max( distance * Margin, 0.01f );

		Camera.WorldPosition = centre + forward * -distance;
	}

	float _time;

	/// <summary>Cycle is a float 0-1, timestep is the time since the last frame</summary>
	public virtual void UpdateScene( float cycle, float timeStep )
	{
		if ( !Scene.IsValid() )
			return;

		using ( Scene.Push() )
		{
			if ( PrimaryObject.IsValid() )
			{
				PrimaryObject.WorldRotation = new Angles( 0, (cycle * 360.0f), 0 );
				FrameScene();
			}
		}

		TickScene( timeStep );
	}

	public void TickScene( float timeStep )
	{
		if ( !Scene.IsValid() )
			return;

		using ( Scene.Push() )
		{
			Scene.EditorTick( _time += timeStep, timeStep );
		}
	}

	public async Task<byte[]> CreateVideo( float secondsLength, VideoWriter.Config config )
	{
		var path = System.IO.Path.GetTempFileName();

		//Camera.Size = new Vector2( config.Width, config.Height );
		Camera.BackgroundColor = "#32415e";

		var writer = EditorUtility.CreateVideoWriter( path, config );

		var frameRate = config.FrameRate;
		var frameStep = 1.0f / frameRate;
		var frames = secondsLength * frameRate;

		var timeTaken = Stopwatch.StartNew();
		using var bitmap = new Bitmap( config.Width, config.Height );
		IsRenderingVideo = true;

		//Update ScreenSize so FrameScene() calculates correct camera distance
		ScreenSize = new Vector2Int( config.Width, config.Height );

		for ( float i = 0; i < frames; i += 1.0f )
		{
			float delta = i / frames;
			UpdateScene( delta, frameStep );

			Camera.RenderToBitmap( bitmap );
			writer.AddFrame( bitmap );

			if ( timeTaken.Elapsed.TotalMilliseconds > 1.5f )
			{
				await Task.Delay( 1 );
				timeTaken.Restart();
			}
		}

		await writer.FinishAsync();
		writer.Dispose();

		IsRenderingVideo = false;

		var bytes = await System.IO.File.ReadAllBytesAsync( path );

		//delete temporary file
		System.IO.File.Delete( path );

		return bytes;
	}

	public virtual Task RenderToPixmap( Pixmap pixmap )
	{
		//Loading content into the preview scene can take its camera with it, so the camera that was there when the render was asked for may be gone.
		if ( !Camera.IsValid() )
			return Task.CompletedTask;

		Camera.RenderToPixmap( pixmap );
		return Task.CompletedTask;
	}

	public virtual Task RenderToBitmap( Bitmap bitmap )
	{
		ScreenSize = bitmap.Size;

		if ( !Camera.IsValid() )
			return Task.CompletedTask;

		Camera.RenderToBitmap( bitmap );
		return Task.CompletedTask;
	}

	[Asset.ThumbnailRenderer]
	public static async Task<Bitmap> RenderAssetThumbnail( Asset asset )
	{
		using AssetPreview v = CreateForAsset( asset );

		//unsupported
		if ( v is null )
			return null;

		//Asked before anything is built: a preview that would have to load the asset's whole world to draw one picture is not worth the frame it costs.
		if ( !v.CanRenderThumbnail )
			return null;

		v.IsRenderingThumbnail = true;

		//Set the size before framing, since FrameScene reads the aspect from it
		v.ScreenSize = new Vector2Int( 256, 256 );

		await v.InitializeScene();
		await v.InitializeAsset();

		Bitmap best = null;
		double bestPixels = 0;

		//Render a few turns and keep the one with the most alpha, sampling every eighth pixel
		const float Turn = 0.25f;
		const int Every = 8;

		for ( float f = 0.0f; f < 1.0f; f += Turn )
		{
			v.UpdateScene( f, 0.2f );

			//Whatever the asset loaded may have taken the camera with it - there is nothing to
			//draw with, and a blank picture would otherwise be cached as the asset's thumbnail.
			if ( !v.Camera.IsValid() )
				return best;

			var pix = new Bitmap( 256, 256 );
			await v.RenderToBitmap( pix );

			if ( !v.UsePixelEvaluatorForThumbs )
			{
				best = pix;
				break;
			}

			double pixels = 0;
			for ( int x = 0; x < pix.Width; x += Every )
				for ( int y = 0; y < pix.Height; y += Every )
				{
					var c = pix.GetPixel( x, y );
					pixels += c.a;
					pixels += c.Luminance;
				}

			if ( best == null || pixels > bestPixels )
			{
				best = pix;
				bestPixels = pixels;
			}
		}

		return best;
	}

	public static AssetPreview CreateForAsset( Asset asset )
	{
		if ( asset is null ) return null;

		var file = System.IO.Path.GetExtension( asset.Path )?.TrimStart( '.' );

		var type = EditorTypeLibrary.GetTypesWithAttribute<AssetPreviewAttribute>().Where( x =>
			Matches( x.Attribute.Extension, file ) ||
			Matches( x.Attribute.Extension, asset.AssetType?.FileExtension ) ).FirstOrDefault();
		if ( type.Type is null ) return null;

		return type.Type.Create<AssetPreview>( new[] { asset } );
	}

	static bool Matches( string left, string right )
	{
		return !string.IsNullOrEmpty( left ) && !string.IsNullOrEmpty( right )
			&& string.Equals( left, right, StringComparison.OrdinalIgnoreCase );
	}
}
