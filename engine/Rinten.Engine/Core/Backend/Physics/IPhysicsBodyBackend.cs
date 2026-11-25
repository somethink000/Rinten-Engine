using static Rinten.PhysicsGroupDescription.BodyPart;

namespace Rinten;

//Everything PhysicsBody asks of the simulation underneath it. As with IPhysicsWorldBackend, this is not a
//designed API - it is the set of calls the managed body already made into native code, restated in managed types.
internal interface IPhysicsBodyBackend
{
	//False once the simulation's side of this body is gone.
	bool IsValid { get; }

	PhysicsWorld GetWorld();

	//-- transform ------------------------------------------------------------

	Vector3 GetPosition();
	void SetPosition( Vector3 position );

	Rotation GetOrientation();
	void SetOrientation( Rotation rotation );

	Transform GetTransform();
	void SetTransform( Vector3 position, Rotation rotation );

	//Sweep towards a pose over timeOffset rather than teleporting, so a keyframed body pushes what is in its way. See PhysicsBody.UseController.
	void SetTargetTransform( Vector3 position, Rotation rotation, float timeOffset );

	//-- motion ---------------------------------------------------------------

	Vector3 GetLinearVelocity();
	void SetLinearVelocity( Vector3 velocity );

	Vector3 GetAngularVelocity();
	void SetAngularVelocity( Vector3 velocity );

	Vector3 GetVelocityAtPoint( Vector3 point );

	float GetLinearDamping();
	void SetLinearDamping( float damping );

	float GetAngularDamping();
	void SetAngularDamping( float damping );

	PhysicsBodyType GetBodyType();
	void SetBodyType( PhysicsBodyType type );

	//Freeze translation or rotation per axis.
	void SetMotionLocks( bool x, bool y, bool z, bool pitch, bool yaw, bool roll );

	//Enhanced continuous collision detection, for bullets and rockets.
	void SetBullet( bool enabled );

	//-- mass -----------------------------------------------------------------

	float GetMass();
	void SetMass( float mass );

	Vector3 GetMassCenter();
	Vector3 GetLocalMassCenter();
	void SetLocalMassCenter( Vector3 center );

	bool GetOverrideMassCenter();
	void SetOverrideMassCenter( bool value );

	//Recompute mass from the shapes and their materials.
	void BuildMass();

	//Average density of the shapes, from their surface materials.
	float GetDensity();

	Vector3 GetLocalInertiaVector();
	Rotation GetLocalInertiaOrientation();
	void SetLocalInertia( Vector3 inertia, Rotation rotation );
	void ResetLocalInertia();

	//-- gravity --------------------------------------------------------------

	bool IsGravityEnabled();
	void EnableGravity( bool enabled );

	float GetGravityScale();
	void SetGravityScale( float scale );

	//-- sleeping -------------------------------------------------------------

	bool IsSleeping();
	void Sleep();
	void Wake();

	void EnableAutoSleeping();
	void DisableAutoSleeping();

	//Keep warm contacts between steps where the body allows it - see b3Body_EnableContactRecycling.
	void SetContactRecycling( bool enabled );

	float GetSleepThreshold();
	void SetSleepThreshold( float threshold );

	//-- enabled state --------------------------------------------------------

	bool IsEnabled();
	void Enable();
	void Disable();

	//Make every shape on this body a trigger, or stop doing so.
	void SetTrigger( bool trigger );

	bool IsTouchEventEnabled();
	void EnableTouchEvents();
	void DisableTouchEvents();

	//-- forces ---------------------------------------------------------------

	void ApplyLinearImpulse( Vector3 impulse );
	void ApplyLinearImpulseAtWorldSpace( Vector3 impulse, Vector3 position );
	void ApplyAngularImpulse( Vector3 impulse );

	void ApplyForce( Vector3 force );
	void ApplyForceAt( Vector3 force, Vector3 position );
	void ApplyTorque( Vector3 torque );

	void ClearForces();
	void ClearTorque();

	void ApplyBuoyancyImpulse( Vector3 planeOrigin, Vector3 planeNormal, float fluidDensity,
		float linearDrag, float angularDrag, Vector3 fluidVelocity, Vector3 gravity, float dt );

	//-- shapes ---------------------------------------------------------------

	int GetShapeCount();
	PhysicsShape GetShape( int index );

	PhysicsShape AddSphereShape( Vector3 center, float radius );
	PhysicsShape AddCapsuleShape( Vector3 center1, Vector3 center2, float radius );
	PhysicsShape AddBoxShape( Vector3 position, Rotation rotation, Vector3 extent );

	//Convex hull of loose points. vertices is a pinned array of Vector3; it does not have to outlive the call.
	PhysicsShape AddHullShape( Vector3 position, Rotation rotation, int vertexCount, IntPtr vertices );

	//Triangle mesh of loose points. Mesh shapes cannot be simulated, only collided with.
	PhysicsShape AddMeshShape( int vertexCount, IntPtr vertices, int indexCount, IntPtr indices, int materialCount );

	PhysicsShape AddHeightFieldShape( IntPtr heights, IntPtr materials, int sizeX, int sizeY,
		float sizeScale, float heightScale, int materialCount );

	//Baked hull out of a model's collision.
	PhysicsShape AddHullShape( HullPart part, Transform transform );

	//A baked mesh, collapsed to its convex hull.
	PhysicsShape AddHullShape( MeshPart part, Transform transform );

	//Baked mesh out of a model's collision, kept as a mesh.
	PhysicsShape AddMeshShape( MeshPart part, Transform transform, int materialCount );

	void RemoveShape( PhysicsShape shape );
	void PurgeShapes();

	void SetMaterialIndex( string name );

	//-- queries --------------------------------------------------------------

	BBox BuildBounds();

	Vector3 GetClosestPoint( Vector3 point );

	bool CheckOverlap( PhysicsBody body, Transform transform );
	bool ComputePenetration( PhysicsBody body, Transform transform, out Vector3 direction, out float distance );

	bool IsTouching( PhysicsBody body, bool triggersOnly );
	bool IsTouching( PhysicsShape shape, bool triggersOnly );

	//-- misc -----------------------------------------------------------------

	PhysicsGroup GetAggregate();

	//Rebuild the broadphase proxy after the shapes changed underneath it.
	void ResetProxy();
}
