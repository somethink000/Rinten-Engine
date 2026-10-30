namespace Rinten;

partial class Rigidbody
{
	internal bool SuppressBuoyancy { get; set; }

	/// <summary>Applies buoyancy and drag to the rigidbody relative to a plane to simulate things floating in water.</summary>
	public void ApplyBuoyancy( Plane plane, float dt )
	{
		ApplyBuoyancy( plane, 1000.0f, 0.1f, 0.5f, Vector3.Zero, dt );
	}

	/// <summary>Applies buoyancy and drag to the rigidbody relative to a plane to simulate things floating in water.</summary>
	public void ApplyBuoyancy( Plane plane, float fluidDensity, float linearDrag, float angularDrag, Vector3 fluidVelocity, float dt )
	{
		if ( SuppressBuoyancy || !PhysicsBody.IsValid() ) return;

		var gravity = Scene.PhysicsWorld.Gravity;

		PhysicsBody.Backend.ApplyBuoyancyImpulse( plane.Position, plane.Normal, fluidDensity, linearDrag, angularDrag, fluidVelocity, gravity, dt );
	}
}
