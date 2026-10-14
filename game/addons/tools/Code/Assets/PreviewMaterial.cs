namespace Editor.Assets;

using Editor.Inspectors;

[AssetPreview( "mat" )]
class PreviewMaterial : AssetPreview
{
	public override float PreviewWidgetCycleSpeed => 0.0f;

	SkyBox2D skyboxObject;

	//How wide a look at it, and how far back that puts the camera.
	const float Fov = 30.0f;

	static readonly Model Plane = Model.Plane;

	//Camera distance for the plane to fill the frame, from the plane's measured width
	static float Distance
	{
		get
		{
			var edge = Plane.IsValid() ? Plane.Bounds.Size.x : 1.0f;

			return ( edge * 0.5f ) / MathF.Tan( Fov * 0.5f * MathF.PI / 180.0f ) * 1.02f;
		}
	}

	public PreviewMaterial( Asset asset ) : base( asset )
	{

	}

	/// <summary>Create the model or whatever needs to be viewed</summary>
	public override async Task InitializeAsset()
	{
		var material = await Material.LoadAsync( Asset.Path );
		if ( material is null ) return;

		using ( Scene.Push() )
		{
			PrimaryObject = new GameObject();
			PrimaryObject.WorldTransform = Transform.Zero;

			if ( material.Flags.IsSky )
			{
				skyboxObject = PrimaryObject.AddComponent<SkyBox2D>();
				skyboxObject.SkyMaterial = material;
			}
			else
			{
				var go = Scene.Directory.FindByName( "envmap" )?.FirstOrDefault() ?? new GameObject( true, "envmap" );
				var c = go.GetOrAddComponent<EnvmapProbe>();
				c.Mode = EnvmapProbe.EnvmapProbeMode.Runtime;
				c.WorldPosition = new Vector3( 0, 0, 0 );
				c.TintColor = Color.White * 0.1f;
				var sprite = PrimaryObject.AddComponent<ModelRenderer>();
				sprite.Model = Plane;
				sprite.MaterialOverride = material;
				sprite.Tint = material.GetColor( "Tint" );

				//Perspective, not orthographic: the ortho path rendered the texture as its smallest mip
				Camera.Orthographic = false;
				Camera.FieldOfView = Fov;
			}

		}
	}

	public override void UpdateScene( float cycle, float timeStep )
	{
		//A material that would not load leaves InitializeAsset before it makes anything to turn, and this then runs
		//every frame against nothing. It is how one unreadable material took every thumbnail in the browser with it.
		if ( !PrimaryObject.IsValid() )
			return;

		//Big fov for skybox preview so you can see a few sides
		if ( skyboxObject.IsValid() )
		{
			Camera.WorldPosition = Vector3.Zero;
			Camera.WorldRotation = new Angles( 0, 180 * cycle, 0 );
			Camera.FieldOfView = 120;
			return;
		}

		Camera.WorldPosition = Vector3.Up * Distance;

		//Look straight down with right = +X and up = -Z so the image is the right way up
		Camera.WorldRotation = new Angles( 90, 0, 0 );
		PrimaryObject.WorldRotation = Rotation.Identity;
	}

	public override Widget CreateToolbar()
	{
		var info = new IconButton( "settings" );
		info.Layout = Layout.Row();
		info.MinimumSize = 16;
		info.MouseLeftPress = () => OpenSettings( info );

		return info;
	}

	public void OpenSettings( Widget parent )
	{
		var popup = new PopupWidget( parent );
		popup.IsPopup = true;


		popup.Layout = Layout.Column();
		popup.Layout.Margin = 16;

		var ps = new PanelControlSheetHost( popup );
		ps.AddProperty( this, x => x.BackgroundColor );
		ps.AddProperty( PrimaryObject.GetComponent<ModelRenderer>(), x => x.Tint );
		//ps.AddProperty( Camera, x => x.EnablePostProcessing );

		popup.Layout.Add( ps );
		popup.MaximumWidth = 300;
		popup.Show();
		popup.Position = parent.ScreenRect.TopRight - popup.Size;
		popup.ConstrainToScreen();
	}
}
