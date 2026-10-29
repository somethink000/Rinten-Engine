namespace Rinten;

public sealed partial class PlayerController : Component
{
	//Metres: an inch upstream
	const float _groundedFeetLift = 0.0254f;

	/// <summary>Make sure the body and our components are created</summary>
	void EnsureComponentsCreated()
	{
		if ( !ColliderObject.IsValid() )
		{
			ColliderObject = GameObject.Children.FirstOrDefault( x => x.Name == "Colliders" );
			if ( !ColliderObject.IsValid() )
			{
				ColliderObject = new GameObject( GameObject, true, "Colliders" );
			}
		}

		ColliderObject.LocalTransform = global::Transform.Zero;
		ColliderObject.Tags.SetFrom( BodyCollisionTags );

		Body.CollisionEventsEnabled = true;
		Body.CollisionUpdateEventsEnabled = true;
		Body.RigidbodyFlags = RigidbodyFlags.DisableCollisionSounds;

		BodyCollider = ColliderObject.GetOrAddComponent<CapsuleCollider>();
		FeetCollider = ColliderObject.GetOrAddComponent<BoxCollider>();

		Body.Flags = Body.Flags.WithFlag( ComponentFlags.Hidden, !_showRigidBodyComponent );

		ColliderObject.Flags = ColliderObject.Flags.WithFlag( GameObjectFlags.Hidden, !_showColliderComponent );
		BodyCollider.Flags = BodyCollider.Flags.WithFlag( ComponentFlags.Hidden, !_showColliderComponent );
		FeetCollider.Flags = FeetCollider.Flags.WithFlag( ComponentFlags.Hidden, !_showColliderComponent );

		if ( Renderer is null && UseAnimatorControls )
		{
			Renderer = GetComponentInChildren<SkinnedModelRenderer>();
		}
	}

	//Update the body dimensions, and change the physical properties based on the current state
	void UpdateBody()
	{
		var feetHeight = CurrentHeight * 0.5f;
		var radius = BodyRadius * 0.5f;
		var up = UpDirection;
		var localUp = Rotation.FromYaw( -WorldRotation.Yaw() ) * up;
		var localUpAbs = localUp.Abs();

		//If we're not on the ground, we have slippy as fuck feet
		var feetFriction = 0.0f;

		if ( IsOnGround )
		{
			bool wantsBrakes = false;

			//If we're standing still we want the brakes on. An eighth of a metre a second - a shuffle. It was five, which was five inches
			//a second where this came from and became five metres a second here, so the brakes were on through a walk and most of a run.
			wantsBrakes = WishVelocity.Length < 0.127f;

			//If we're going slower, we want the brakes on
			wantsBrakes = wantsBrakes || WishVelocity.Length < Velocity.Length * 0.9f;

			//The 1 here is normal friction. The 100 is just so we can change BrakePower to a 0-1 range, to make things less
			//confusing to the user. We multiply by our current ground friction so we will still slide around on ice etc.
			if ( wantsBrakes )
			{
				feetFriction = 1 + (100.0f * BrakePower * GroundFriction);
			}
		}

		//Body capsule covers the upper half, a few cm off the floor; disabled if too short to fit
		BodyCollider.Radius = radius;
		BodyCollider.Start = Vector3.Up * (CurrentHeight - radius);
		BodyCollider.End = Vector3.Up * MathF.Max( BodyCollider.Start.y - (feetHeight - radius), radius + 0.025f );
		BodyCollider.Friction = 0.0f;
		BodyCollider.Elasticity = 0.0f;
		BodyCollider.Enabled = BodyCollider.End.y < BodyCollider.Start.y;

		var feetLength = BodyCollider.Enabled ? feetHeight : CurrentHeight;
		FeetCollider.Friction = feetFriction;
		FeetCollider.Elasticity = 0.0f;
		FeetCollider.Enabled = true;

		var locking = Body.Locking;
		locking.Pitch = true;
		locking.Yaw = true;
		locking.Roll = true;
		Body.Locking = locking;

		Body.MassOverride = BodyMass;
		Body.EnhancedCcd = false;
		Body.PhysicsBody?.AutoSleep = false;

		//When trying to move, we move the mass center up to the waist so the player
		//can "step" over smaller shit When not moving we drop it to the foot position.
		float massCenter = IsOnGround ? WishVelocity.Length.Clamp( 0, CurrentHeight * 0.5f ) : CurrentHeight * 0.5f;
		Body.MassCenterOverride = new Vector3( 0, massCenter, 0 );
		Body.OverrideMassCenter = true;

		Mode?.UpdateRigidBody( Body );

		//
		// Small lips are absorbed by Reground, and larger "steps" by TryStep.
		//
		// Keep the feet outside Box3D's speculative mesh contacts while ground probes maintain support.
		var feetLift = IsOnGround && !GroundIsDynamic && !Body.Gravity
			? MathF.Min( _groundedFeetLift / MathF.Abs( WorldTransform.UniformScale ), feetLength * 0.5f )
			: 0.0f;
		var feetBoxLength = feetLength - feetLift;
		//Up is y: as wide as the body on both horizontal axes, as tall as the part of the character it covers
		FeetCollider.Scale = new Vector3( BodyRadius, feetBoxLength, BodyRadius );
		FeetCollider.Center = new Vector3( 0, feetLift + feetBoxLength * 0.5f, 0 );
	}
}
