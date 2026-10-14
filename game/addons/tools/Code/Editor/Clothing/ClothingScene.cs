namespace Editor;

public class ClothingScene
{
	public Scene Scene;

	public float Pitch = 15.0f;
	public float Yaw = 35.0f;

	public SkinnedModelRenderer Body;
	Clothing.IconSetup iconSetup;

	public ClothingScene()
	{
		Scene = Scene.CreateEditorScene();

		using ( Scene.Push() )
		{
			var camera = new GameObject( true, "camera" ).GetOrAddComponent<CameraComponent>();

			Body = new GameObject( true, "player" ).GetOrAddComponent<SkinnedModelRenderer>();
			//Body.Model = Model.Load( "models/citizen/citizen.vmdl" );
			//Body.Model = Model.Load( "models/citizen_mannequin/mannequin.vmdl" );
			//Body.Model = Model.Load( "models/citizen_human/citizen_human_female.vmdl" );
			Body.Model = Model.Load( "models/citizen_human/citizen_human_male.vmdl" );

			camera.BackgroundColor = new Color( 0.1f, 0.1f, 0.1f, 0.0f );
		}
	}

	List<SceneModel> clothingModels = new();

	public void InstallClothing( Clothing clothing )
	{
		iconSetup = clothing.Icon;

		bool wantsCitizenModel = iconSetup.Mode == Clothing.IconSetup.IconModes.CitizenSkin;
		bool wantsGreySkin = !wantsCitizenModel && iconSetup.Mode != Clothing.IconSetup.IconModes.HumanSkin;


		using var x = Scene.Push();

		if ( wantsCitizenModel )
		{
			Body.Model = Model.Load( "models/citizen/citizen.vmdl" );
		}
		else
		{
			Body.Model = Model.Load( "models/citizen_human/citizen_human_male.vmdl" );
		}

		foreach ( var m in clothingModels )
		{
			m.Delete();
		}

		ClothingContainer container = new ClothingContainer();
		container.Add( clothing );

		container.Apply( Body );

		//Remove any extra clothes (underwear) that was added by the ClothingContainer
		foreach ( var child in Body.GameObject.Children.ToArray() )
		{
			if ( !child.Tags.Has( "clothing" ) ) continue;
			if ( child.Name == $"Clothing - {clothing.ResourceName}" ) continue;

			child.Destroy();
		}

		//Recomputer body groups
		foreach ( var (name, value) in container.GetBodyGroups( new[] { clothing }, Body.Model ) )
		{
			Body.SetBodyGroup( name, value );
		}

		if ( wantsGreySkin )
		{
			var greySkin = Material.Load( "models/citizen/skin/citizen_skin_grey.vmat" );
			foreach ( var model in Scene.GetAll<SkinnedModelRenderer>() )
			{
				model.SetMaterialOverride( greySkin, "skin" );
				model.SetMaterialOverride( greySkin, "face" );
				model.SetMaterialOverride( greySkin, "eyes" );
				model.SetMaterialOverride( greySkin, "eyeao" );
			}
		}
	}

	public void Update()
	{
		UpdateLighting();

		Body.Set( "b_grounded", true );
		Body.Set( "aim_eyes", Vector3.Forward * (2.54f) );
		Body.Set( "aim_head", Vector3.Forward * (2.54f) );
		Body.Set( "aim_body", Vector3.Forward * (2.54f) );
		Body.Set( "aim_body_weight", 1.0f );
		Body.Set( "static_pose", 1 );

		Body.Morphs.Set( "lipcornerpullerL", 0.5f );
		Body.Morphs.Set( "lipcornerpullerR", 0.5f );
		Body.Morphs.Set( "innerbrowraiserL", 0.9f );
		Body.Morphs.Set( "innerbrowraiserR", 0.9f );
		Body.Morphs.Set( "outerbrowraiserL", 0.3f );
		Body.Morphs.Set( "outerbrowraiserR", 0.3f );
		Body.Morphs.Set( "openjawL", 0.1f );
		Body.Morphs.Set( "openjawR", 0.1f );

		Scene.EditorTick( RealTime.Now, RealTime.Delta );

		UpdateCameraPosition();
	}

	void UpdateLighting()
	{
		using var _ = Scene.Push();

		//sun
		{
			var go = Scene.Directory.FindByName( "sun" )?.FirstOrDefault() ?? new GameObject( true, "sun" );
			var light = go.GetOrAddComponent<DirectionalLight>();
			light.WorldRotation = Rotation.From( 45, -180, 0 );
			light.LightColor = new Color( 1.0f, 0.9f, 0.7f );
			light.SkyColor = Color.Gray * 0.55f;
		}


		//rim l
		{
			var go = Scene.Directory.FindByName( "pointlight1" )?.FirstOrDefault() ?? new GameObject( true, "pointlight1" );
			var light = go.GetOrAddComponent<PointLight>();
			light.WorldPosition = new Vector3( -100, 10, 70 ) * 0.02794f;
			light.LightColor = new Color( 0.1f, 0.5f, 1.0f ) * 30;
			light.Radius = 12.7f;
			light.Shadows = true;
		}

		//rim r
		{
			var go = Scene.Directory.FindByName( "pointlight3" )?.FirstOrDefault() ?? new GameObject( true, "pointlight3" );
			var light = go.GetOrAddComponent<PointLight>();
			light.WorldPosition = new Vector3( -20, 40, 20 ) * 0.127f;
			light.LightColor = new Color( 1.0f, 0.6f, 0.1f ) * 5;
			light.Radius = 12.7f;
			light.Shadows = true;
		}

		//warm
		{
			var go = Scene.Directory.FindByName( "pointlight2" )?.FirstOrDefault() ?? new GameObject( true, "pointlight2" );
			var light = go.GetOrAddComponent<PointLight>();
			light.WorldPosition = new Vector3( 1.27f, 2.54f, 4.318f );
			light.LightColor = new Color( 0.9f, 0.8f, 0.6f ) * 2;
			light.Radius = 12.7f;
			light.Shadows = true;
		}

		//{
		//var go = Scene.Directory.FindByName( "pointlight2" )?.FirstOrDefault() ?? new GameObject( true, "pointlight2" );
		//var light = go.GetOrAddComponent<PointLight>();
		//light.WorldPosition = new Vector3( 50, 100, 400 );
		//light.LightColor = new Color( 0.9f, 0.8f, 0.6f ) * 30;
		//light.Radius = 500;
		//light.Shadows = true;
		//}

		var sharpen = Scene.Camera.GetOrAddComponent<Sharpen>();
		sharpen.Scale = 0.5f;
	}

	void UpdateCameraPosition()
	{
		using var x = Scene.Push();

		if ( !Body.IsValid() )
			return;

		Scene.Camera.FieldOfView = 5;
		Scene.Camera.ZFar = 127f;
		Scene.Camera.ZNear = 0.00254f;

		var bounds = Body.Bounds;

		var lookAngle = new Angles( Pitch, 180 - Yaw, 0 );
		var forward = lookAngle.Forward;
		var distance = 21.59f;
		var pos = bounds.Center;

		if ( iconSetup.Mode == Clothing.IconSetup.IconModes.Hand )
		{
			lookAngle = new Angles( 15, 130, 0 );
			forward = lookAngle.Forward;

			var tx = Body.GetAttachment( "hand_r" );
			pos = tx.Value.Position + Vector3.Down * (0.0762f);
			distance = 3.81f;
		}

		if ( iconSetup.Mode == Clothing.IconSetup.IconModes.Eyes )
		{
			lookAngle = new Angles( 15, 150, 0 );
			forward = lookAngle.Forward;

			var tx = Body.GetAttachment( "eyes" );
			pos = tx.Value.Position + lookAngle.ToRotation().Left * (0.03302f);
			distance = 3.302f;
		}

		if ( iconSetup.Mode == Clothing.IconSetup.IconModes.Head )
		{
			lookAngle = new Angles( 15, 150, 0 );
			forward = lookAngle.Forward;

			var tx = Body.GetAttachment( "eyes" );
			pos = tx.Value.Position + lookAngle.ToRotation().Left * (0.03302f) + lookAngle.ToRotation().Up * (0.0381f);
			distance = 4.064f;
		}

		if ( iconSetup.Mode == Clothing.IconSetup.IconModes.Foot )
		{
			lookAngle = new Angles( 15, 150, 0 );
			forward = lookAngle.Forward;

			var tx = Body.GetAttachment( "foot_l" );
			pos = tx.Value.Position + lookAngle.ToRotation().Left * (0.00762f) + lookAngle.ToRotation().Up * (0.0381f);
			distance = 3.302f;
		}

		if ( iconSetup.Mode == Clothing.IconSetup.IconModes.HumanSkin )
		{
			lookAngle = new Angles( 5, 160, 0 );
			forward = lookAngle.Forward;

			var tx = Body.GetAttachment( "eyes" );
			pos = tx.Value.Position + lookAngle.ToRotation().Left * (0.00762f) + lookAngle.ToRotation().Up * (-0.0127f);
			distance = 3.556f;
		}

		if ( iconSetup.Mode == Clothing.IconSetup.IconModes.CitizenSkin )
		{
			lookAngle = new Angles( 5, 150, 0 );
			forward = lookAngle.Forward;

			Scene.Camera.FieldOfView = 30;

			var tx = Body.GetAttachment( "eyes" );
			pos = tx.Value.Position + lookAngle.ToRotation().Left * (0.04064f) + lookAngle.ToRotation().Up * (-0.0127f);
			distance = 1.016f;
		}

		if ( iconSetup.Mode == Clothing.IconSetup.IconModes.Mouth )
		{
			lookAngle = new Angles( 5, 160, 0 );
			forward = lookAngle.Forward;

			var tx = Body.GetAttachment( "eyes" );
			pos = tx.Value.Position + lookAngle.ToRotation().Left * (0.00762f) + lookAngle.ToRotation().Up * (-0.0762f);
			distance = 3.302f;
		}

		if ( iconSetup.Mode == Clothing.IconSetup.IconModes.Chest )
		{
			lookAngle = new Angles( 5, 160, 0 );
			forward = lookAngle.Forward;

			if ( Body.TryGetBoneTransform( "spine_2", out var tx ) )
			{
				pos = tx.Position + lookAngle.ToRotation().Left * (0.00762f);
				distance = 10.16f;
			}
		}

		if ( iconSetup.Mode == Clothing.IconSetup.IconModes.Wrist )
		{
			lookAngle = new Angles( 15, 220, 45 );
			forward = lookAngle.Forward;

			if ( Body.TryGetBoneTransform( "hand_L", out var tx ) )
			{
				pos = tx.Position;
				distance = 1.016f;
			}
		}

		pos += lookAngle.ToRotation() * iconSetup.PositionOffset;


		Scene.Camera.WorldPosition = pos - forward * distance;
		Scene.Camera.WorldRotation = Rotation.From( lookAngle );

	}
}
