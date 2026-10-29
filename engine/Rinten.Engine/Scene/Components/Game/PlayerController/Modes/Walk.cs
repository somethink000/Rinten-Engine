namespace Rinten.Movement;

/// <summary>The character is walking</summary>
[Icon( "transfer_within_a_station" ), Group( "Movement" ), Title( "MoveMode - Walk" ), Alias( "Rinten.PhysicsCharacterMode.PhysicsCharacterWalkMode" )]
public partial class MoveModeWalk : MoveMode
{
	[Property] public int Priority { get; set; } = 0;

	[Property] public float GroundAngle { get; set; } = 45.0f;
	//Forty-five centimetres, in metres - a tall kerb, a low stair.
	[Property] public float StepUpHeight { get; set; } = 0.45f;
	[Property] public float StepDownHeight { get; set; } = 0.45f;


	public override bool AllowGrounding => true;
	public override bool AllowFalling => true;

	public override int Score( PlayerController controller ) => Priority;

	public override void AddVelocity()
	{
		Controller.WishVelocity = Controller.WithoutVertical( Controller.WishVelocity );
		if ( !Controller.IsOnGround || Controller.GroundIsDynamic )
		{
			base.AddVelocity();
			return;
		}

		var body = Controller.Body;
		var normal = Controller.GroundNormal;
		var velocity = body.Velocity - Controller.GroundVelocity;
		velocity -= normal * velocity.Dot( normal );

		// Brake along the ground plane, rather than using contact friction which also grabs walls
		var speed = velocity.Length;
		var friction = Controller.GroundFriction.Clamp( 0, 1 );
		if ( speed > 0 )
		{
			//Metres a second: Source's stop speed of 100 inches
			var drop = MathF.Max( speed, 2.54f ) * 6 * Controller.BrakePower * friction * Time.Delta;
			velocity *= MathF.Max( speed - drop, 0 ) / speed;
		}

		var wish = Controller.WishVelocity;
		var wishSpeed = wish.Length;
		var wishDirection = (wish - normal * wish.Dot( normal )).Normal;
		var addSpeed = wishSpeed - velocity.Dot( wishDirection );
		if ( addSpeed > 0 )
		{
			velocity += wishDirection * MathF.Min( addSpeed, 10 * wishSpeed * friction * Time.Delta );
		}

		body.Velocity = velocity + Controller.GroundVelocity;
	}

	public override void UpdateRigidBody( Rigidbody body )
	{
		base.UpdateRigidBody( body );

		if ( !Controller.IsOnGround || Controller.GroundIsDynamic )
			return;

		// Grounded motion follows the slope and StickToGround handles descending.
		// (Gravity would otherwise add downhill drift every physics step)
		body.Gravity = Controller.GroundFriction <= 0;
		body.LinearDamping = 0;
		Controller.FeetCollider.Friction = 0;
	}

	public override void PrePhysicsStep()
	{
		base.PrePhysicsStep();

		if ( StepUpHeight > 0 )
		{
			TrySteppingUp( StepUpHeight );
		}
	}

	public override void PostPhysicsStep()
	{
		base.PostPhysicsStep();

		StickToGround( StepDownHeight );
	}

	public override bool IsStandableSurface( in SceneTraceResult result )
	{
		if ( Vector3.GetAngle( Vector3.Up, result.Normal ) > GroundAngle )
			return false;

		return true;
	}

	public override Vector3 UpdateMove( Rotation eyes, Vector3 input )
	{
		// ignore pitch when walking
		eyes = eyes.Angles() with { pitch = 0 };

		return base.UpdateMove( eyes, input );
	}
}
