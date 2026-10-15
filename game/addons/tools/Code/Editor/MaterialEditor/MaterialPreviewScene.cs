using System;

namespace Editor.Materials;

/// <summary>Which shape the preview puts the material on.</summary>
public enum PreviewShape
{
	Cube,
	Sphere,
	Plane,
}

/// <summary>Preview scene: shape, lights and orbit camera; the hosting panel only feeds input and ticks</summary>
public sealed class MaterialPreviewScene : IDisposable
{
	//How far a notch of the wheel moves the camera, as a share of the framing distance.
	const float PerNotch = 0.1f;

	//Degrees of orbit per pixel of drag. This view's own number, not the scene view's: the drag is read off the unscaled cursor and turns a shape
	//filling a thumbnail, so the two are not answering the same question. Only the direction has to agree, and that is what the yaw below was got wrong.
	const float PerPixel = 0.4f;

	public Scene Scene { get; private set; }
	public CameraComponent Camera { get; private set; }

	/// <summary>The shape the material is on.</summary>
	public PreviewShape Worn { get; private set; }

	ModelRenderer _renderer;
	Material _material;
	MaterialDefinition _definition;
	float _yaw;
	float _pitch;
	float _distance;
	float _nearest;
	float _farthest;
	Vector3 _center;

	public MaterialPreviewScene( PreviewShape shape )
	{
		Worn = shape;

		Build();
		ResetView();
	}

	public void Dispose()
	{
		Scene?.Destroy();
		Scene = null;
		Camera = null;
		_renderer = null;
	}

	/// <summary>Puts the material, as it stands now, on the shape. Null leaves the shape bare.</summary>
	public void Apply( MaterialDefinition definition )
	{
		_definition = definition;

		if ( !_renderer.IsValid() ) return;

		if ( definition is null )
		{
			_renderer.MaterialOverride = null;
			_renderer.Tint = Color.White;
			return;
		}

		if ( _material is null )
			_material = Material.FromDefinition( definition, "mat-preview" );
		else
			_material.ApplyDefinition( definition );

		_renderer.MaterialOverride = _material;
		_renderer.Tint = definition.Tint;
	}

	public void Wear( PreviewShape shape )
	{
		Worn = shape;

		if ( !_renderer.IsValid() ) return;

		_renderer.Model = ModelFor( shape );
		Apply( _definition );
	}

	//How far back is worked out from the shape's own bounds and the camera's field of view, the way the model preview frames a model - a fixed distance framed one shape and put the camera inside the next.
	/// <summary>The three-quarter view from a little above, with the whole shape in the picture.</summary>
	public void ResetView()
	{
		_yaw = 225f;
		_pitch = 20f;

		var bounds = _renderer?.Model is { } shape && shape.IsValid()
			? shape.Bounds
			: BBox.FromPositionAndSize( Vector3.Zero, 1f );

		_center = bounds.Center;
		_distance = MathX.SphereCameraDistance( bounds.Size.Length * 0.5f, Camera.FieldOfView ) * 1.2f;

		//The wheel can go in to a quarter of that and out to four times it, whatever size the shape is.
		_nearest = _distance * 0.25f;
		_farthest = _distance * 4f;

		PlaceCamera();
	}

	/// <summary>Orbits like the scene view (OrbitCameraAroundPivot): same direction and speed</summary>
	public void Orbit( Vector2 delta )
	{
		_yaw -= delta.x * PerPixel;
		_pitch = ( _pitch + delta.y * PerPixel ).Clamp( -80f, 80f );

		PlaceCamera();
	}

	/// <summary>Move the camera in or out by notches of the wheel. A notch towards the hand is negative, and moves the camera in.</summary>
	public void Zoom( float notches )
	{
		_distance = ( _distance * ( 1f + notches * PerNotch ) ).Clamp( _nearest, _farthest );

		PlaceCamera();
	}

	/// <summary>Advance the scene a frame.</summary>
	public void Frame( float delta )
	{
		if ( !Scene.IsValid() ) return;

		PlaceCamera();
		Scene.EditorTick( RealTime.Now, delta );
	}

	static Model ModelFor( PreviewShape shape ) => shape switch
	{
		PreviewShape.Sphere => Model.Sphere,
		PreviewShape.Plane => Model.Plane,
		_ => Model.Cube,
	};

	void Build()
	{
		Scene = Scene.CreateEditorScene();
		Scene.Name = "Material Preview";

		using ( Scene.Push() )
		{
			Camera = new GameObject( true, "camera" ).AddComponent<CameraComponent>();
			Camera.BackgroundColor = Theme.Sunken;
			Camera.FieldOfView = 40f;
			Camera.ZFar = 2540f;
			Camera.ZNear = 0.00254f;

			new GameObject( true, "ambient" ).AddComponent<AmbientLight>().Color = Color.Cyan * 0.05f;

			var sun = new GameObject( true, "sun" ).AddComponent<DirectionalLight>();
			sun.Shadows = true;
			sun.WorldRotation = new Angles( 50, 45, 0 );
			sun.LightColor = Color.White * 0.6f;

			_renderer = new GameObject( true, "shape" ).AddComponent<ModelRenderer>();
			_renderer.Model = ModelFor( Worn );
		}
	}

	void PlaceCamera()
	{
		if ( !Camera.IsValid() ) return;

		Camera.WorldRotation = new Angles( _pitch, _yaw, 0 );
		Camera.WorldPosition = _center + Camera.WorldRotation.Backward * _distance;
	}
}
