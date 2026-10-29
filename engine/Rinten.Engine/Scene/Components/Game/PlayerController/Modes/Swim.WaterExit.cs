namespace Rinten.Movement;

public partial class MoveModeSwim
{
	//Lengths below are metres and speeds metres a second, the inch values upstream converted

	//Highest bank above the water surface that the character can climb out onto.
	private const float ExitMaxHeight = 0.81f;

	//How far ahead of the character hull to search for a bank.
	private const float ExitReach = 0.61f;

	//Horizontal speed maintained against the bank until the feet clear its edge.
	private const float ExitSpeed = 3.05f;

	/// <summary>Whether the controller currently owns an assisted exit, including its dry portion.</summary>
	public bool IsExitingWater { get; private set; }

	Rigidbody _exitBody;
	GameObject _landingObject;
	Collider _landingCollider;
	Vector3 _localLanding;
	Vector3 _lastPosition;
	float _exitTime;
	float _retryTime;
	bool _launching;
	bool _suppressSurfaceJump;

	/// <summary>Landing is enabled during the exit but not during ordinary swimming.</summary>
	public override bool AllowGrounding => IsExitingWater && Controller.Body.Velocity.Dot( Controller.UpDirection ) <= 0;

	/// <summary>Preserve the exit trajectory instead of applying ordinary swim acceleration.</summary>
	public override void AddVelocity()
	{
		if ( !IsExitingWater )
		{
			base.AddVelocity();
		}
	}

	/// <summary>Finds a bank while swimming and carries the body onto it; collision stays with the physics.</summary>
	public override void PrePhysicsStep()
	{
		if ( IsProxy || !Controller.Body.IsValid() )
			return;

		if ( !IsExitingWater )
		{
			TryBeginExit();
		}

		if ( !IsExitingWater )
		{
			return;
		}

		_exitTime += Time.Delta;
		if ( !_landingObject.IsValid() || _exitTime > 1.5f
			|| (WorldPosition - _lastPosition).Length > 3.25f )
		{
			CancelExit();
			return;
		}

		_lastPosition = WorldPosition;
		var landing = _landingObject.WorldTransform.PointToWorld( _localLanding );
		var delta = Controller.WithoutVertical( landing - WorldPosition );
		var wish = Controller.WithoutVertical( Controller.WishVelocity );
		if ( wish.Length > 0.254f && wish.Dot( delta ) < -0.00645f )
		{
			CancelExit();
			return;
		}

		var platform = _landingCollider.IsValid() ? _landingCollider.Rigidbody : null;
		var platformVelocity = platform.IsValid() ? platform.GetVelocityAtPoint( landing ) : Vector3.Zero;
		var velocity = Controller.Body.Velocity;

		//Ease into the assistance and brake near the landing, preserving the
		//swimmer's momentum instead of replacing it in a single physics step.
		var target = delta.Normal * MathF.Min( ExitSpeed, delta.Length * 10 ) + Controller.WithoutVertical( platformVelocity );
		var horizontal = Controller.WithoutVertical( velocity );
		horizontal += (target - horizontal).ClampLength( 15.24f * Time.Delta );

		var vertical = velocity.Dot( Controller.UpDirection );
		if ( _launching )
		{
			//Recalculate the impulse still needed as we rise. A fixed launch
			//target would add the ramp's travel on top of the validated apex.
			var gravity = MathF.Max( 0.0254f, -Scene.PhysicsWorld.Gravity.Dot( Controller.UpDirection ) * Controller.Body.GravityScale );
			var rise = MathF.Max( 0, (landing - WorldPosition).Dot( Controller.UpDirection ) + 0.457f );
			var launchSpeed = MathF.Sqrt( 2 * gravity * rise );
			if ( vertical < launchSpeed )
			{
				vertical = MathF.Min( launchSpeed, vertical + 60.96f * Time.Delta );
			}

			_launching = vertical < launchSpeed;
		}

		Controller.Body.Velocity = Controller.WithVertical( horizontal, vertical );
	}

	/// <summary>Stop assistance when an obstruction prevents the feet from reaching the top.</summary>
	public override void PostPhysicsStep()
	{
		if ( !IsExitingWater || !_landingObject.IsValid() )
			return;

		var landing = _landingObject.WorldTransform.PointToWorld( _localLanding );
		if ( _exitTime > 0.1f && Controller.Body.Velocity.Dot( Controller.UpDirection ) <= 0 && (WorldPosition - landing).Dot( Controller.UpDirection ) < -0.0508f )
		{
			CancelExit();
		}
	}

	/// <summary>Use the same slope limit as the character's walking mode for exit landings.</summary>
	public override bool IsStandableSurface( in SceneTraceResult result )
	{
		var walk = GetComponent<MoveModeWalk>();
		return result.Normal.Dot( Controller.UpDirection ) >= MathF.Cos( (walk?.GroundAngle ?? 45) * MathF.PI / 180 );
	}

	/// <summary>Cancel any pending exit after a reset, landing, interruption or obstruction.</summary>
	public void CancelExit()
	{
		if ( IsExitingWater )
		{
			_retryTime = 0.5f;
			_suppressSurfaceJump = true;
		}

		if ( _exitBody.IsValid() )
		{
			_exitBody.SuppressBuoyancy = false;
		}

		_exitBody = null;
		IsExitingWater = false;
		_launching = false;
		_landingObject = null;
		_landingCollider = null;
	}

	/// <summary>Release transient references if the component is disabled during an exit.</summary>
	protected override void OnDisabled()
	{
		CancelExit();
		base.OnDisabled();
	}

	void TryBeginExit()
	{
		if ( _retryTime > 0 || WaterLevel < 0.35f || WaterLevel > 0.9f || Controller.Body.Velocity.Dot( Controller.UpDirection ) < -4.572f )
			return;

		var wish = Controller.WithoutVertical( Controller.WishVelocity );
		if ( wish.Length < 0.254f )
			return;

		var direction = wish.Normal;
		var foot = WorldPosition;
		var scale = MathF.Abs( WorldTransform.UniformScale );
		var height = Controller.CurrentHeight * scale;
		var radius = Controller.BodyRadius * MathF.Sqrt( 0.5f ) * scale;
		var wall = Trace( foot, foot + direction * ExitReach );
		if ( !wall.Hit || wall.StartedSolid || IsStandableSurface( wall ) )
			return;

		var waterHeight = foot.Dot( Controller.UpDirection ) + WaterLevel * height;
		var high = Controller.WithVertical( foot, waterHeight + ExitMaxHeight + 0.0508f );
		var across = high + direction * (wall.Distance + radius * 2 + 0.1524f);
		var upTrace = Trace( foot, high );
		if ( upTrace.Hit || upTrace.StartedSolid )
			return;

		var acrossTrace = Trace( high, across );
		if ( acrossTrace.Hit || acrossTrace.StartedSolid )
			return;

		var down = Trace( across, Controller.WithVertical( across, waterHeight - 0.2032f ) );
		if ( !down.Hit || down.StartedSolid || !IsStandableSurface( down )
			|| down.EndPosition.Dot( Controller.UpDirection ) > waterHeight + ExitMaxHeight || down.EndPosition.Dot( Controller.UpDirection ) < waterHeight - 0.1016f )
			return;

		//The arc rises above the landing. Validate that extra headroom as well.
		var apex = Controller.WithVertical( foot, down.EndPosition.Dot( Controller.UpDirection ) + 0.457f );
		var apexAcross = Controller.WithVertical( across, apex.Dot( Controller.UpDirection ) );
		var riseTrace = Trace( foot, apex );
		var arcTrace = Trace( apex, apexAcross );
		if ( riseTrace.Hit || riseTrace.StartedSolid || arcTrace.Hit || arcTrace.StartedSolid )
			return;

		//Check the whole route, not just a ray over the bank or a clear eye position.
		_landingObject = down.GameObject;
		_landingCollider = down.Component as Collider;
		if ( !_landingObject.IsValid() )
			return;

		_localLanding = _landingObject.WorldTransform.PointToLocal( down.EndPosition );
		_lastPosition = foot;
		_exitTime = 0;
		IsExitingWater = true;
		_launching = true;
		_exitBody = Controller.Body;
		_exitBody.SuppressBuoyancy = true;
		_suppressSurfaceJump = true;
		Controller.PreventGrounding( 0.15f );

		UpdateRigidBody( Controller.Body );
	}

	//Sweeps both character shapes; a small skin allows resting penetration but not a blocked route
	SceneTraceResult Trace( Vector3 from, Vector3 to )
	{
		return Controller.TraceMovement( from, to );
	}
}
