using static Rinten.PhysicsGroupDescription.BodyPart;

namespace Rinten;

//A physics body on our own core, over Box3D.
internal sealed unsafe class CorePhysicsBodyBackend : IPhysicsBodyBackend
{
	//Default density in kg/m^3 for shapes without a surface material, matching Surface's default
	private const float DefaultDensity = 2000.0f;

	private readonly PhysicsBody owner;
	private ulong body;

	//Shapes in creation order, because that is the order PhysicsBody.Shapes and GetShape( i
	//) promise. Box3D's own ordering is an implementation detail we should not be exposing.
	private readonly List<PhysicsShape> shapes = new();

	private bool isTrigger;

	public CorePhysicsBodyBackend( ulong body, PhysicsBody owner )
	{
		this.body = body;
		this.owner = owner;

		//Joints report the bodies they connect by handle; this is how those get back to the managed object.
		CoreBodyRegistry.Add( body, owner );
	}

	internal ulong Handle => body;

	public bool IsValid => body != 0 && CorePhysics.BodyValid( body );

	//Tear down the native body. The managed object may outlive it.
	internal void DestroyNative()
	{
		if ( body == 0 ) return;

		//Destroying a body takes its shapes with it, so forget them here rather than leaving the registry pointing at handles Box3D has reused.
		foreach ( var shape in shapes )
		{
			if ( shape?.Backend is CorePhysicsShapeBackend core )
				core.Forget();
		}

		CoreBodyRegistry.Remove( body );
		CorePhysics.BodyDestroy( body );
		body = 0;
		shapes.Clear();
	}

	public PhysicsWorld GetWorld() => owner?.World;

	//-- transform ------------------------------------------------------------

	public Vector3 GetPosition() => CorePhysics.BodyGetPosition( body );
	public void SetPosition( Vector3 position ) => CorePhysics.BodySetPosition( body, position );

	public Rotation GetOrientation() => CorePhysics.BodyGetRotation( body );
	public void SetOrientation( Rotation r ) => CorePhysics.BodySetRotation( body, r );

	public Transform GetTransform() => new( GetPosition(), GetOrientation() );

	public void SetTransform( Vector3 position, Rotation r )
		=> CorePhysics.BodySetTransform( body, position, r );

	/// <summary>Drives a keyframed body with the velocity that reaches the pose in one step, so it pushes things</summary>
	public void SetTargetTransform( Vector3 position, Rotation rotation, float timeOffset )
	{
		if ( timeOffset <= 0.0f )
		{
			SetTransform( position, rotation );
			return;
		}

		var current = GetPosition();
		SetLinearVelocity( (position - current) / timeOffset );

		//Angular velocity is an axis scaled by radians per second, which is what the delta rotation gives in axis-angle
		//form. The vector part of a quaternion is its axis times sin(half-angle), so normalising it is the axis.
		var delta = rotation * GetOrientation().Inverse;
		var angle = delta.Angle().DegreeToRadian();

		var axis = new Vector3( delta.x, delta.y, delta.z );
		var length = axis.Length;

		SetAngularVelocity( angle > 0.0001f && length > 0.0001f
			? axis / length * (angle / timeOffset)
			: Vector3.Zero );
	}

	//-- motion ---------------------------------------------------------------

	public Vector3 GetLinearVelocity() => CorePhysics.BodyGetVelocity( body );
	public void SetLinearVelocity( Vector3 v ) => CorePhysics.BodySetVelocity( body, v );

	public Vector3 GetAngularVelocity() => CorePhysics.BodyGetAngularVelocity( body );
	public void SetAngularVelocity( Vector3 v ) => CorePhysics.BodySetAngularVelocity( body, v );

	public Vector3 GetVelocityAtPoint( Vector3 point ) => CorePhysics.BodyGetVelocityAtPoint( body, point );

	public float GetLinearDamping() => CorePhysics.BodyGetLinearDamping( body );
	public void SetLinearDamping( float d ) => CorePhysics.BodySetLinearDamping( body, d );

	public float GetAngularDamping() => CorePhysics.BodyGetAngularDamping( body );
	public void SetAngularDamping( float d ) => CorePhysics.BodySetAngularDamping( body, d );

	public PhysicsBodyType GetBodyType() => (PhysicsBodyType)(int)CorePhysics.BodyGetMotionType( body );
	public void SetBodyType( PhysicsBodyType type )
	{
		CorePhysics.BodySetMotionType( body, (CorePhysics.MotionType)(int)type );

		//Box3D works a body's mass out from its shapes when it turns dynamic, and a hand-set
		//one is only ever given to a dynamic body - see BodySetMass - so it goes back on here.
		if ( type == PhysicsBodyType.Dynamic ) RestoreExplicitMass();
	}

	public void SetMotionLocks( bool x, bool y, bool z, bool pitch, bool yaw, bool roll )
		=> CorePhysics.BodySetMotionLocks( body, x, y, z, pitch, yaw, roll );

	public void SetBullet( bool enabled ) => CorePhysics.BodySetBullet( body, enabled );

	//-- mass -----------------------------------------------------------------

	public float GetMass() => CorePhysics.BodyGetMass( body );

	//Mass set by hand, reapplied after Box3D recomputes mass from shape densities
	private float? explicitMass;

	public void SetMass( float mass )
	{
		explicitMass = mass;
		CorePhysics.BodySetMass( body, mass );
	}

	//Put a hand-set mass, and a hand-set centre of mass, back after the shapes changed
	//underneath them - Box3D rebuilds both from the shapes whenever a density changes.
	internal void RestoreExplicitMass()
	{
		if ( explicitMass.HasValue )
			CorePhysics.BodySetMass( body, explicitMass.Value );

		if ( overrideMassCenter )
			CorePhysics.BodySetLocalMassCenter( body, localMassCenter );
	}

	public Vector3 GetMassCenter() => CorePhysics.BodyGetMassCenter( body );
	public Vector3 GetLocalMassCenter() => CorePhysics.BodyGetLocalMassCenter( body );

	public void SetLocalMassCenter( Vector3 c )
	{
		localMassCenter = c;

		if ( overrideMassCenter )
			CorePhysics.BodySetLocalMassCenter( body, c );
	}

	//Box3D always derives the centre of mass from the shapes unless mass data is set explicitly, so there is no flag
	//to read back. We track what was asked for so the property round-trips, and so it can be put back after a rebuild.
	private bool overrideMassCenter;
	private Vector3 localMassCenter;

	public bool GetOverrideMassCenter() => overrideMassCenter;

	public void SetOverrideMassCenter( bool value )
	{
		if ( overrideMassCenter == value ) return;
		overrideMassCenter = value;

		if ( value )
		{
			CorePhysics.BodySetLocalMassCenter( body, localMassCenter );
			return;
		}

		//The centre goes back to what the shapes say; a hand-set mass is a separate thing and stays. Rigidbody writes
		//this right after the mass on every configure, so rebuilding here unconditionally erased MassOverride.
		CorePhysics.BodyBuildMass( body );
		RestoreExplicitMass();
	}

	/// <summary>Recompute the mass from the shapes. This is the one thing that is allowed to discard a hand-set mass, because asking for a rebuild is asking for exactly that.</summary>
	public void BuildMass()
	{
		explicitMass = null;
		CorePhysics.BodyBuildMass( body );
	}

	/// <summary>Average density of the shapes. Every shape currently gets the same default, so this is that default until surface materials carry one.</summary>
	public float GetDensity() => DefaultDensity;

	/// <summary>The principal moments of inertia - how hard the body is to spin about each of its own axes.</summary>
	public Vector3 GetLocalInertiaVector()
	{
		CorePhysics.BodyGetInertiaTensor( body, out var moments, out _ );
		return moments;
	}

	/// <summary>The axes the principal moments are measured along.</summary>
	public Rotation GetLocalInertiaOrientation()
	{
		CorePhysics.BodyGetInertiaTensor( body, out _, out var axes );
		return axes;
	}

	public void SetLocalInertia( Vector3 inertia, Rotation rotation )
		=> CorePhysics.BodySetInertiaTensor( body, inertia, rotation );

	public void ResetLocalInertia() => BuildMass();

	//-- gravity --------------------------------------------------------------

	public bool IsGravityEnabled() => CorePhysics.BodyGravityEnabled( body );
	public void EnableGravity( bool enabled ) => CorePhysics.BodyEnableGravity( body, enabled );

	public float GetGravityScale() => CorePhysics.BodyGetGravityScale( body );
	public void SetGravityScale( float scale ) => CorePhysics.BodySetGravityScale( body, scale );

	//-- sleeping -------------------------------------------------------------

	public bool IsSleeping() => !CorePhysics.BodyIsAwake( body );
	public void Sleep() => CorePhysics.BodySetAwake( body, false );
	public void Wake() => CorePhysics.BodySetAwake( body, true );

	public void EnableAutoSleeping() => CorePhysics.BodyEnableAutoSleep( body, true );
	public void DisableAutoSleeping() => CorePhysics.BodyEnableAutoSleep( body, false );

	public float GetSleepThreshold() => CorePhysics.BodyGetSleepThreshold( body );
	public void SetSleepThreshold( float threshold ) => CorePhysics.BodySetSleepThreshold( body, threshold );

	//-- enabled state --------------------------------------------------------

	public bool IsEnabled() => CorePhysics.BodyIsEnabled( body );
	public void Enable() => CorePhysics.BodySetEnabled( body, true );
	public void Disable() => CorePhysics.BodySetEnabled( body, false );

	/// <summary>Whether shapes added from here on are triggers. Box3D fixes sensor-ness at shape creation, and Collider sets this immediately before adding its shapes, which is exactly the pattern that works.</summary>
	public void SetTrigger( bool trigger ) => isTrigger = trigger;

	/// <summary>Touch events are per shape underneath; the body-level switch is "any of them" on read and "all of them" on write.</summary>
	public bool IsTouchEventEnabled()
	{
		foreach ( var shape in shapes )
		{
			if ( shape?.Backend is CorePhysicsShapeBackend core && core.ContactEventsEnabled )
				return true;
		}

		return false;
	}

	public void EnableTouchEvents() => SetTouchEvents( true );
	public void DisableTouchEvents() => SetTouchEvents( false );

	private void SetTouchEvents( bool enabled )
	{
		foreach ( var shape in shapes )
		{
			if ( shape?.Backend is CorePhysicsShapeBackend core )
				core.ContactEventsEnabled = enabled;
		}
	}

	//-- forces ---------------------------------------------------------------

	public void ApplyLinearImpulse( Vector3 i ) => CorePhysics.BodyApplyImpulse( body, i );
	public void ApplyLinearImpulseAtWorldSpace( Vector3 i, Vector3 p ) => CorePhysics.BodyApplyImpulseAt( body, i, p );
	public void ApplyAngularImpulse( Vector3 i ) => CorePhysics.BodyApplyAngularImpulse( body, i );

	public void ApplyForce( Vector3 f ) => CorePhysics.BodyApplyForce( body, f );
	public void ApplyForceAt( Vector3 f, Vector3 p ) => CorePhysics.BodyApplyForceAt( body, f, p );
	public void ApplyTorque( Vector3 t ) => CorePhysics.BodyApplyTorque( body, t );

	/// <summary>Box3D accumulates forces over a step and clears them itself, so there is no pending force to drop. Nothing to do rather than nothing implemented.</summary>
	public void ClearForces() { }
	public void ClearTorque() { }

	/// <summary>Float on a fluid whose surface is the given plane. The core measures how much of the body is under it and pushes back with the weight of the fluid that displaces.</summary>
	public void ApplyBuoyancyImpulse( Vector3 planeOrigin, Vector3 planeNormal, float fluidDensity,
		float linearDrag, float angularDrag, Vector3 fluidVelocity, Vector3 gravity, float dt )
		=> CorePhysics.BodyBuoyancy( body, planeOrigin, planeNormal,
			fluidDensity, linearDrag, angularDrag, fluidVelocity, gravity, dt );

	//-- shapes ---------------------------------------------------------------

	public int GetShapeCount() => shapes.Count;

	public PhysicsShape GetShape( int index )
		=> index >= 0 && index < shapes.Count ? shapes[index] : null;

	//Wrap a freshly created native shape in a managed one.
	private PhysicsShape Adopt( ulong handle )
	{
		if ( handle == 0 ) return null;

		var shape = PhysicsShape.CreateDetached();

		var backend = new CorePhysicsShapeBackend( handle, owner ) { ownerShape = shape };
		shape.AttachBackend( backend );
		shapes.Add( shape );

		//Traces come back with native handles; this is how they find their way to the managed shape again.
		CoreShapeRegistry.Add( handle, shape );

		//Adding a shape recomputed the mass from densities; if someone had set one by hand, that is still the mass they want.
		RestoreExplicitMass();

		//A new Box3D shape has Box3D's numbers (friction 0.6, no bounce), not the default surface's. The surface is what every collider
		//without an override is meant to feel like, so it goes on first; a model's own material or a collider's Surface replaces it afterwards.
		backend.SetMaterialIndex( null );

		return shape;
	}

	public PhysicsShape AddSphereShape( Vector3 center, float radius )
		=> Adopt( AddSphereNative( center, radius, DefaultDensity, isTrigger ) );

	public PhysicsShape AddCapsuleShape( Vector3 a, Vector3 b, float radius )
		=> Adopt( AddCapsuleNative( a, b, radius, DefaultDensity, isTrigger ) );

	public PhysicsShape AddBoxShape( Vector3 position, Rotation r, Vector3 extent )
		=> Adopt( AddBoxNative( position, r, extent, DefaultDensity, isTrigger ) );

	//The native halves, separate so a shape can be rebuilt in place when its geometry changes - see CorePhysicsShapeBackend.

	internal ulong AddSphereNative( Vector3 center, float radius, float density, bool trigger )
		=> CorePhysics.BodyAddSphere( body, center, radius, density, trigger );

	internal ulong AddCapsuleNative( Vector3 a, Vector3 b, float radius, float density, bool trigger )
		=> CorePhysics.BodyAddCapsule( body, a, b, radius, density, trigger );

	internal ulong AddBoxNative( Vector3 position, Rotation r, Vector3 extent, float density, bool trigger )
		=> CorePhysics.BodyAddBox( body, position, r, extent, density, trigger );

	internal ulong AddHullNative( Vector3 position, Rotation rotation, int vertexCount, IntPtr vertices, float density, bool trigger )
	{
		if ( vertices == IntPtr.Zero || vertexCount < 4 ) return 0;

		var source = (Vector3*)vertices;
		var points = new Vector3[vertexCount];
		for ( var i = 0; i < vertexCount; i++ ) points[i] = source[i];

		var flat = Flatten( points, new Transform( position, rotation ) );

		return CorePhysics.BodyAddHull( body, flat, density, trigger );
	}

	internal ulong AddMeshNative( int vertexCount, IntPtr vertices, int indexCount, IntPtr indices, bool trigger )
	{
		if ( vertices == IntPtr.Zero || indices == IntPtr.Zero ) return 0;
		if ( vertexCount < 3 || indexCount < 3 ) return 0;

		var sourceVertices = (Vector3*)vertices;
		var points = new Vector3[vertexCount];
		for ( var i = 0; i < vertexCount; i++ ) points[i] = sourceVertices[i];

		var sourceIndices = (int*)indices;
		var triangles = new int[indexCount];
		for ( var i = 0; i < indexCount; i++ ) triangles[i] = sourceIndices[i];

		var flat = Flatten( points, Transform.Zero );

		return CorePhysics.BodyAddMesh( body, flat, triangles.AsSpan( 0, indexCount ), trigger );
	}

	public PhysicsShape AddHullShape( Vector3 position, Rotation rotation, int vertexCount, IntPtr vertices )
	{
		if ( vertices == IntPtr.Zero || vertexCount <= 0 ) return null;

		var source = (Vector3*)vertices;
		var points = new Vector3[vertexCount];
		for ( var i = 0; i < vertexCount; i++ ) points[i] = source[i];

		return AddHull( points, new Transform( position, rotation ) );
	}

	public PhysicsShape AddMeshShape( int vertexCount, IntPtr vertices, int indexCount, IntPtr indices, int materialCount )
	{
		if ( vertices == IntPtr.Zero || indices == IntPtr.Zero ) return null;
		if ( vertexCount <= 0 || indexCount <= 0 ) return null;

		var sourceVertices = (Vector3*)vertices;
		var points = new Vector3[vertexCount];
		for ( var i = 0; i < vertexCount; i++ ) points[i] = sourceVertices[i];

		var sourceIndices = (int*)indices;
		var triangles = new int[indexCount];
		for ( var i = 0; i < indexCount; i++ ) triangles[i] = sourceIndices[i];

		return AddMesh( points, triangles, Transform.Zero );
	}

	/// <summary>Terrain collision. The core lays a height field out with the grid on local X and Z and height on local Y, which is how terrain is laid out too - so the samples go straight across, in the order they are already in.</summary>
	public PhysicsShape AddHeightFieldShape( IntPtr heights, IntPtr materials, int sizeX, int sizeY,
		float sizeScale, float heightScale, int materialCount )
	{
		if ( heights == IntPtr.Zero || sizeX < 2 || sizeY < 2 ) return null;

		var state = new CoreHeightField( this, (ushort*)heights, (byte*)materials,
			sizeX, sizeY, sizeScale, heightScale, materialCount );

		var shape = Adopt( state.Build() );
		if ( shape?.Backend is CorePhysicsShapeBackend core )
			core.AttachHeightField( state );

		return shape;
	}

	//Build a native height field shape on this body from a grid in the terrain's layout. Used both when terrain is first created and every time it is edited.
	internal ulong CreateHeightFieldShape( ushort* heights, byte* materials, int sizeX, int sizeY,
		float sizeScale, float heightScale, int materialCount )
	{
		//The core uses the same height field layout as terrain, so the caller's buffers pass through as-is
		int samples = sizeX * sizeY;

		return CorePhysics.BodyAddHeightField( body,
			new ReadOnlySpan<ushort>( heights, samples ),
			materials is null ? default : new ReadOnlySpan<byte>( materials, samples ),
			sizeX, sizeY, sizeScale, sizeScale, heightScale, materialCount );
	}

	//Put a rebuilt shape in place of one already in the list.
	internal void ReplaceShape( PhysicsShape shape, ulong handle )
	{
		if ( shape is null || handle == 0 ) return;
		CoreShapeRegistry.Add( handle, shape );
	}

	//Re-apply every shape's tags, for when the world's collision rules changed after the shapes were made.
	internal void RefreshTags()
	{
		foreach ( var shape in shapes )
		{
			if ( shape?.Backend is CorePhysicsShapeBackend core )
				core.ApplyTags();
		}
	}

	//-- baked collision -- rebuilt from the points and indices HullPart and MeshPart expose

	public PhysicsShape AddHullShape( HullPart part, Transform transform )
		=> AddHull( part?.GetPoints()?.ToArray(), transform );

	public PhysicsShape AddHullShape( MeshPart part, Transform transform )
		=> AddHull( part?.GetVertices(), transform );

	public PhysicsShape AddMeshShape( MeshPart part, Transform transform, int materialCount )
		=> AddMesh( part?.GetVertices(), part?.GetIndices(), transform );

	//Convex hull from points in part space. transform places the part on the body; the ABI
	//takes body-local points, so it is baked in here rather than passed down for one use.
	private PhysicsShape AddHull( Vector3[] points, Transform transform )
	{
		//Four points is the least that bounds a volume.
		if ( points is null || points.Length < 4 ) return null;

		var flat = Flatten( points, transform );

		return Adopt( CorePhysics.BodyAddHull( body, flat, DefaultDensity, isTrigger ) );
	}

	private PhysicsShape AddMesh( Vector3[] points, int[] indices, Transform transform )
	{
		if ( points is null || points.Length < 3 ) return null;
		if ( indices is null || indices.Length < 3 ) return null;

		var flat = Flatten( points, transform );

		return Adopt( CorePhysics.BodyAddMesh( body, flat, indices, isTrigger ) );
	}

	//A mesh in body space, cooked here and handed back so the next body made from the same model shape borrows it
	internal PhysicsShape AddMeshShape( List<Vector3> points, List<int> indices, out CorePhysics.CookedMesh cooked )
	{
		cooked = null;
		if ( points is null || points.Count < 3 ) return null;
		if ( indices is null || indices.Count < 3 ) return null;

		//Box3D reads the vertices an index names without asking
		foreach ( var i in indices )
		{
			if ( i < 0 || i >= points.Count ) return null;
		}

		var flat = new float[points.Count * 3];

		for ( var i = 0; i < points.Count; i++ )
		{
			flat[i * 3 + 0] = points[i].x;
			flat[i * 3 + 1] = points[i].y;
			flat[i * 3 + 2] = points[i].z;
		}

		var triangles = System.Runtime.InteropServices.CollectionsMarshal.AsSpan( indices );

		return Adopt( CorePhysics.BodyAddMesh( body, flat, triangles, isTrigger, out cooked ) );
	}

	//A mesh another body cooked; null once that mesh has gone with the last shape on it
	internal PhysicsShape AddMeshShape( CorePhysics.CookedMesh cooked )
		=> Adopt( CorePhysics.BodyAddCookedMesh( body, cooked, isTrigger ) );

	//Points into the flat float triples the ABI takes, placed on the body.
	private static float[] Flatten( Vector3[] points, Transform transform )
	{
		var flat = new float[points.Length * 3];

		for ( var i = 0; i < points.Length; i++ )
		{
			var p = transform.PointToWorld( points[i] );
			flat[i * 3 + 0] = p.x;
			flat[i * 3 + 1] = p.y;
			flat[i * 3 + 2] = p.z;
		}

		return flat;
	}

	public void RemoveShape( PhysicsShape shape )
	{
		if ( shape?.Backend is not CorePhysicsShapeBackend core ) return;

		shapes.Remove( shape );
		core.DestroyNative();
		RestoreExplicitMass();
	}

	public void PurgeShapes()
	{
		foreach ( var shape in shapes.ToArray() )
		{
			if ( shape?.Backend is CorePhysicsShapeBackend core )
				core.DestroyNative();
		}

		shapes.Clear();
	}

	/// <summary>Set the surface material on every shape. Materials live on shapes, so the body-level setter is just a broadcast.</summary>
	public void SetMaterialIndex( string name )
	{
		foreach ( var shape in shapes )
		{
			shape?.Backend?.SetMaterialIndex( name );
		}
	}

	//-- queries --------------------------------------------------------------

	public BBox BuildBounds() => CorePhysics.BodyGetBounds( body );

	public Vector3 GetClosestPoint( Vector3 point ) => CorePhysics.BodyGetClosestPoint( body, point );

	public bool CheckOverlap( PhysicsBody other, Transform transform )
		=> ComputePenetration( other, transform, out _, out _ );

	/// <summary>How far <paramref name="other"/> would have to move to stop overlapping this body, were it standing at <paramref name="transform"/>.</summary>
	public unsafe bool ComputePenetration( PhysicsBody other, Transform transform, out Vector3 direction, out float distance )
	{
		direction = default;
		distance = default;

		if ( other?.Backend is not CorePhysicsBodyBackend core ) return false;

		if ( !CorePhysics.BodyPenetration( body, core.body, transform, out direction, out distance ) )
			return false;

		return true;
	}

	/// <summary>Answered from the contacts the last step produced, so it agrees with the touch callbacks rather than being a second opinion from a fresh overlap test.</summary>
	public bool IsTouching( PhysicsBody other, bool triggersOnly )
	{
		if ( other?.Backend is not CorePhysicsBodyBackend core ) return false;
		if ( owner?.World?.Backend is not CorePhysicsWorldBackend world ) return false;

		foreach ( var shape in core.shapes )
		{
			if ( shape?.Backend is not CorePhysicsShapeBackend otherShape ) continue;
			if ( triggersOnly && !otherShape.IsTrigger() ) continue;

			if ( world.Events.IsTouchingAny( shapes, otherShape.Handle ) ) return true;
		}

		return false;
	}

	public bool IsTouching( PhysicsShape shape, bool triggersOnly )
	{
		if ( shape?.Backend is not CorePhysicsShapeBackend core ) return false;
		if ( triggersOnly && !core.IsTrigger() ) return false;
		if ( owner?.World?.Backend is not CorePhysicsWorldBackend world ) return false;

		return world.Events.IsTouchingAny( shapes, core.Handle );
	}

	//-- misc -----------------------------------------------------------------

	/// <summary>The group this body was instanced as part of, if it was. Set by whoever built the group; a body made on its own never belongs to one.</summary>
	public PhysicsGroup GetAggregate() => aggregate;

	internal void SetAggregate( PhysicsGroup group ) => aggregate = group;

	private PhysicsGroup aggregate;

	/// <summary>Box3D rebuilds its broadphase proxies as shapes change, so there is nothing to prod.</summary>
	public void ResetProxy() { }
}
