namespace Rinten;

public sealed partial class PlayerController : ICameraModifier
{
	[Property, FeatureEnabled( "Camera", Icon = "videocam", Description = "Built-in camera controls. Remove this to control the Camera yourself." )]
	public bool UseCameraControls { get; set; } = true;

	[Property, Feature( "Camera" )] public float EyeDistanceFromTop { get; set; } = 0.2f;
	[Property, Feature( "Camera" )] public bool ThirdPerson { get; set; } = true;
	[Property, Feature( "Camera" )] public bool HideBodyInFirstPerson { get; set; } = true;
	[Property, Feature( "Camera" )] public bool UseFovFromPreferences { get; set; } = true;
	/// <summary>Where the third person camera sits relative to the eye, in the eye's own space and in metres: thirty centimetres up and two and a half metres back.</summary>
	[Property, Feature( "Camera" )] public Vector3 CameraOffset { get; set; } = new Vector3( 0.0f, 0.3f, 2.5f );
	[Property, Feature( "Camera" ), InputAction] public string ToggleCameraModeButton { get; set; } = "view";
	/// <summary>The set of tags to ignore during camera collision detection.</summary>
	[Property, Feature( "Camera" ), Title( "Collision Ignore" )] public TagSet CameraCollisionIgnore { get; set; } = [];

	//How far the third person camera currently is, in metres.
	float _cameraDistance = 2.5f;
	float _eyeHeight;

	//Input is read in the update loop - the view itself is composed in the camera's modifier chain.
	void UpdateCameraInput()
	{
		if ( !UseCameraControls ) return;

		if ( !string.IsNullOrWhiteSpace( ToggleCameraModeButton ) && Input.Pressed( ToggleCameraModeButton ) )
		{
			ThirdPerson = !ThirdPerson;
			//Half a metre, so coming out of first person pulls back rather than starting where the camera happens to have been left.
			_cameraDistance = 0.5f;
		}
	}

	int ICameraModifier.CameraOrder => 0;

	//Stage one of the camera pipeline - writes the player's eye view (and the third person boom) into the base view. The obsolete
	//IEvents.PostCameraSetup hook still runs against the camera component afterwards, its changes folded back into the view.
	void ICameraModifier.ModifyCamera( CameraComponent cam, ref CameraView view )
	{
		if ( !UseCameraControls || IsProxy ) return;
		if ( Scene.Camera != cam ) return;

		UpdateEyeTransform();

		var rot = EyeTransform.Rotation;
		var eyePosition = EyeTransform.Position;

		if ( !IsAirborne && _eyeHeight != 0 )
			eyePosition.y = _eyeHeight.LerpTo( eyePosition.y, Time.Delta * 50 );

		_eyeHeight = eyePosition.y;

		if ( !cam.RenderExcludeTags.Contains( "viewer" ) )
		{
			cam.RenderExcludeTags.Add( "viewer" );
		}

		if ( ThirdPerson )
		{
			var cameraDelta = rot * CameraOffset;

			//clip the camera
			var tr = Scene.Trace.FromTo( eyePosition, eyePosition + cameraDelta )
							.IgnoreGameObjectHierarchy( GameObject.Root )
							.Radius( 0.2f )
							.WithoutTags( CameraCollisionIgnore )
							.Run();

			//smooth the zoom in and out
			if ( tr.StartedSolid )
			{
				_cameraDistance = _cameraDistance.LerpTo( cameraDelta.Length, Time.Delta * 100.0f );
			}
			else if ( tr.Distance < _cameraDistance )
			{
				_cameraDistance = _cameraDistance.LerpTo( tr.Distance, Time.Delta * 200.0f );
			}
			else
			{
				_cameraDistance = _cameraDistance.LerpTo( tr.Distance, Time.Delta * 2.0f );
			}

			eyePosition = eyePosition + cameraDelta.Normal * _cameraDistance;
		}

		view.Position = eyePosition;
		view.Rotation = rot;

		if ( UseFovFromPreferences )
			view.FieldOfView = Preferences.FieldOfView;

		//The active move mode reshapes the view - it doesn't join the modifier chain itself.
		Mode?.ModifyCamera( ref view );

		//Legacy bridge - PostCameraSetup mutates the camera component directly. Write the view out, let it run, fold its changes back into the view.
		cam.WorldPosition = view.Position;
		cam.WorldRotation = view.Rotation;
		cam.FieldOfView = view.FieldOfView;

#pragma warning disable CS0618
		GameObject.Components.ExecuteEnabledInSelfAndDescendants<IEvents, CameraComponent>( cam, static ( x, cam ) => x.PostCameraSetup( cam ) );
#pragma warning restore CS0618

		view.Position = cam.WorldPosition;
		view.Rotation = cam.WorldRotation;
		view.FieldOfView = cam.FieldOfView;
	}
}
