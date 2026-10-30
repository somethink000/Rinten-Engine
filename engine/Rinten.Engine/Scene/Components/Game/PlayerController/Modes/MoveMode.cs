
namespace Rinten.Movement;

/// <summary>A move mode for this character</summary>
public abstract partial class MoveMode : Component
{
	public virtual bool AllowGrounding => false;
	public virtual bool AllowFalling => false;

	[RequireComponent]
	public PlayerController Controller { get; set; }

	/// <summary>Highest number becomes the new control mode</summary>
	public virtual int Score( PlayerController controller ) => 0;

	/// <summary>Called before the physics step is run</summary>
	public virtual void PrePhysicsStep()
	{

	}

	/// <summary>Called after the physics step is run</summary>
	public virtual void PostPhysicsStep()
	{

	}

	public virtual void UpdateRigidBody( Rigidbody body )
	{
		bool wantsGravity = false;

		//Disable gravity while standing still on ground (below 2.5 cm/s) so we don't slide down slopes
		if ( !Controller.IsOnGround ) wantsGravity = true;
		if ( Controller.WithoutVertical( Controller.Velocity ).Length > 0.0254f ) wantsGravity = true;
		if ( Controller.GroundVelocity.Length > 0.0254f ) wantsGravity = true;
		if ( Controller.GroundUsesContactFriction ) wantsGravity = true;

		body.Gravity = wantsGravity;

		//when we're standing on the still ground and aren't wishing to move we apply a high linear
		//damping to the body this stops whatever momentum it had from dragging it slowly down hills.
		bool wantsbrakes = Controller.IsOnGround && Controller.WishVelocity.Length < 0.025f && Controller.GroundVelocity.Length < 0.025f;
		body.LinearDamping = wantsbrakes ? (10.0f * Controller.BrakePower) : Controller.AirFriction;

		body.AngularDamping = 1f;
	}

	public virtual void AddVelocity()
	{
		var body = Controller.Body;
		var wish = Controller.WishVelocity;
		if ( wish.IsNearZeroLength ) return;

		var groundFriction = 0.25f + Controller.GroundFriction * 10;
		var groundVelocity = Controller.GroundVelocity;

		var up = body.Velocity.y;

		var velocity = (body.Velocity - Controller.GroundVelocity);
		var speed = velocity.Length;

		var maxSpeed = MathF.Max( wish.Length, speed );

		if ( Controller.IsOnGround )
		{
			var amount = 1 * groundFriction;
			velocity = velocity.AddClamped( wish * amount, wish.Length * amount );
		}
		else
		{
			var amount = 0.05f;
			velocity = velocity.AddClamped( wish * amount, wish.Length );
		}

		if ( velocity.Length > maxSpeed )
			velocity = velocity.Normal * maxSpeed;

		velocity += groundVelocity;

		if ( Controller.IsOnGround )
		{
			velocity.y = up;
		}

		body.Velocity = velocity;
	}

	/// <summary>This mode has just started</summary>
	public virtual void OnModeBegin()
	{

	}

	/// <summary>This mode has stopped. We're swapping to another move mode.</summary>
	public virtual void OnModeEnd( MoveMode next )
	{

	}

	/// <summary>Called before a jump is applied, allowing the mode to release its attachment.</summary>
	public virtual void OnJump()
	{
	}

	/// <summary>If we're approaching a step, step up if possible</summary>
	protected void TrySteppingUp( float maxDistance )
	{
		Controller.TryStep( maxDistance );
	}

	//If we're on the ground, make sure we stay there by falling to the ground
	protected void StickToGround( float maxDistance )
	{
		Controller.Reground( maxDistance );
	}

	public virtual bool IsStandableSurace( in SceneTraceResult result )
	{
		return false;
	}

	public virtual bool IsStandableSurface( in SceneTraceResult result )
	{
		return IsStandableSurace( result );
	}

	Vector3.SmoothDamped smoothedMovement;

	/// <summary>Whether to ease input speed using the controller's acceleration times.</summary>
	protected virtual bool SmoothInput => true;

	/// <summary>Read inputs, return WishVelocity</summary>
	public virtual Vector3 UpdateMove( Rotation eyes, Vector3 input )
	{
		//don't normalize, because analog input might want to go slow
		input = input.ClampLength( 1 );

		var direction = eyes * input;

		//Run if we're holding down alt move button
		bool run = Input.Down( Controller.AltMoveButton );

		//if Run is default, flip that logic
		if ( Controller.RunByDefault ) run = !run;

		//if we're running, use run speed, if not use walk speed
		var velocity = run ? Controller.RunSpeed : Controller.WalkSpeed;

		//Walking crops crouched input only on ground; air strafing keeps its wish speed.
		if ( Controller.IsDucking && (this is not MoveModeWalk || Controller.IsOnGround) ) velocity = Controller.DuckedSpeed;

		if ( direction.IsNearlyZero( 0.1f ) )
		{
			direction = 0;
		}
		else
		{
			//Retain momentum, once we're moving, we're moving. Don't lerp between directions, only between speeds.
			smoothedMovement.Current = direction.Normal * smoothedMovement.Current.Length;
		}

		//Smooth the wish velocity
		smoothedMovement.Target = direction * velocity;
		if ( !SmoothInput )
		{
			smoothedMovement.Current = smoothedMovement.Target;
			return smoothedMovement.Current;
		}

		smoothedMovement.SmoothTime = smoothedMovement.Target.Length < smoothedMovement.Current.Length ? Controller.DeaccelerationTime : Controller.AccelerationTime;
		smoothedMovement.Update( Time.Delta );

		//If it's near zero, just stop. A quarter of a millimetre a second.
		if ( smoothedMovement.Current.IsNearlyZero( 0.00025f ) )
		{
			smoothedMovement.Current = 0;
		}

		//Gizmo.Draw.ScreenText( $"{smoothedMovement.Current.Length}", 200 );

		return smoothedMovement.Current;
	}
}
