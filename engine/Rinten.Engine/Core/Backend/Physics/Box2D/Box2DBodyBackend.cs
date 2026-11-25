using Rinten.Native.Box2D;
using static Rinten.Native.Box2D.Box2D;
using static Rinten.PhysicsGroupDescription.BodyPart;

namespace Rinten;

//2D body over Box2D; keeps depth and off-plane rotation itself and flattens 3D shapes onto XY
internal sealed unsafe partial class Box2DBodyBackend : IPhysicsBodyBackend
{
	private readonly Box2DWorldBackend world;
	private readonly PhysicsBody owner;
	private b2BodyId id;

	//The orientation last given, and the angle Box2D was set to for it.
	private Rotation reference = Rotation.Identity;
	private float referenceAngle;

	//Depth, which Box2D has no use for.
	private float depth;

	private readonly List<PhysicsShape> shapes = new();

	//The joints holding this body, so they can be told when it goes.
	internal readonly List<Box2DJointBackend> Joints = new();

	//Whether shapes added from here on are triggers - see SetTrigger.
	private bool addingTriggers;

	public Box2DBodyBackend( Box2DWorldBackend world, b2BodyId id, PhysicsBody owner )
	{
		this.world = world;
		this.id = id;
		this.owner = owner;

		Box2DRegistry.Bodies[Box2DPlane.Key( id )] = owner;
	}

	internal b2BodyId Id => id;
	internal Box2DWorldBackend World => world;
	internal PhysicsBody Owner => owner;
	internal IReadOnlyList<PhysicsShape> ShapeList => shapes;

	public bool IsValid => b2Body_IsValid( id );

	internal void DestroyNative()
	{
		if ( !IsValid ) return;

		foreach ( var shape in shapes )
		{
			if ( shape?.Backend is Box2DShapeBackend s )
				s.Forget();
		}

		shapes.Clear();

		Box2DRegistry.Bodies.TryRemove( Box2DPlane.Key( id ), out _ );

		//Joints go with the body in Box2D; their managed side has to hear it.
		foreach ( var joint in Joints.ToArray() )
			joint.Forget();

		b2DestroyBody( id );
		id = default;
	}

	public PhysicsWorld GetWorld() => owner?.World;

	//-- the plane ------------------------------------------------------------

	//A point in the body's 3D frame, in Box2D's.
	internal Vector2 Local( Vector3 point )
		=> Box2DPlane.Rotate( Box2DPlane.Flat( reference * point ), -referenceAngle );

	//A point in Box2D's frame back in the body's 3D frame, at a depth in front of or behind the plane.
	internal Vector3 FromLocal( Vector2 point, float z = 0.0f )
	{
		var flat = Box2DPlane.Rotate( point, referenceAngle );
		return reference.Inverse * new Vector3( flat.x, flat.y, z );
	}

	internal Vector3 FromLocal( b2Vec2 point, float z = 0.0f ) => FromLocal( Box2DPlane.Flat( point ), z );

	//A frame on the body, as a joint takes it.
	internal b2Transform LocalFrame( Transform frame )
	{
		var angle = Box2DPlane.AngleOf( reference * frame.Rotation ) - referenceAngle;
		return Box2DPlane.Xf( Local( frame.Position ), angle );
	}

	internal Transform FromLocalFrame( b2Transform frame )
	{
		var rotation = reference.Inverse * Box2DPlane.AboutZ( Box2DPlane.Angle( frame.q ) + referenceAngle );
		return new Transform( FromLocal( frame.p ), rotation );
	}

	//Where a point in Box2D's frame is in the world's plane.
	internal Vector2 WorldPoint( b2Vec2 local ) => Box2DPlane.Flat( b2Body_GetWorldPoint( id, local ) );

	//-- transform ------------------------------------------------------------

	public Vector3 GetPosition()
	{
		if ( !IsValid ) return default;
		return Box2DPlane.Point( b2Body_GetPosition( id ), depth );
	}

	public void SetPosition( Vector3 position )
	{
		if ( !IsValid ) return;

		depth = position.z;
		b2Body_SetTransform( id, Box2DPlane.Vec( position ), b2Body_GetRotation( id ) );
	}

	public Rotation GetOrientation()
	{
		if ( !IsValid ) return reference;

		var turned = Box2DPlane.Angle( b2Body_GetRotation( id ) ) - referenceAngle;
		return Box2DPlane.AboutZ( turned ) * reference;
	}

	public void SetOrientation( Rotation rotation )
	{
		if ( !IsValid ) return;

		reference = rotation;
		referenceAngle = Box2DPlane.AngleOf( rotation );
		b2Body_SetTransform( id, b2Body_GetPosition( id ), Box2DPlane.Rot( referenceAngle ) );
	}

	public Transform GetTransform() => new( GetPosition(), GetOrientation() );

	public void SetTransform( Vector3 position, Rotation rotation )
	{
		if ( !IsValid ) return;

		depth = position.z;
		reference = rotation;
		referenceAngle = Box2DPlane.AngleOf( rotation );
		b2Body_SetTransform( id, Box2DPlane.Vec( position ), Box2DPlane.Rot( referenceAngle ) );
	}

	/// <summary>Box2D moves a kinematic body to a pose over a step itself, so what is in the way is pushed. Depth is not simulated and is simply taken.</summary>
	public void SetTargetTransform( Vector3 position, Rotation rotation, float timeOffset )
	{
		if ( !IsValid ) return;

		if ( timeOffset <= 0.0f )
		{
			SetTransform( position, rotation );
			return;
		}

		depth = position.z;

		//The turn about Z since the reference, carried onto Box2D's angle.
		var angle = referenceAngle + Box2DPlane.AngleOf( rotation ) - Box2DPlane.AngleOf( reference );
		b2Body_SetTargetTransform( id, Box2DPlane.Xf( Box2DPlane.Flat( position ), angle ), timeOffset, true );
	}

	//-- motion ---------------------------------------------------------------

	public Vector3 GetLinearVelocity() => IsValid ? Box2DPlane.Point( b2Body_GetLinearVelocity( id ) ) : default;

	public void SetLinearVelocity( Vector3 velocity )
	{
		if ( IsValid ) b2Body_SetLinearVelocity( id, Box2DPlane.Vec( velocity ) );
	}

	/// <summary>Radians a second about Z, the only axis there is.</summary>
	public Vector3 GetAngularVelocity() => IsValid ? new Vector3( 0, 0, b2Body_GetAngularVelocity( id ) ) : default;

	public void SetAngularVelocity( Vector3 velocity )
	{
		if ( IsValid ) b2Body_SetAngularVelocity( id, velocity.z );
	}

	public Vector3 GetVelocityAtPoint( Vector3 point )
		=> IsValid ? Box2DPlane.Point( b2Body_GetWorldPointVelocity( id, Box2DPlane.Vec( point ) ) ) : default;

	public float GetLinearDamping() => IsValid ? b2Body_GetLinearDamping( id ) : 0;
	public void SetLinearDamping( float damping ) { if ( IsValid ) b2Body_SetLinearDamping( id, damping ); }

	public float GetAngularDamping() => IsValid ? b2Body_GetAngularDamping( id ) : 0;
	public void SetAngularDamping( float damping ) { if ( IsValid ) b2Body_SetAngularDamping( id, damping ); }

	public PhysicsBodyType GetBodyType()
	{
		if ( !IsValid ) return PhysicsBodyType.Static;

		return b2Body_GetType( id ) switch
		{
			b2BodyType.b2_dynamicBody => PhysicsBodyType.Dynamic,
			b2BodyType.b2_kinematicBody => PhysicsBodyType.Keyframed,
			_ => PhysicsBodyType.Static,
		};
	}

	public void SetBodyType( PhysicsBodyType type )
	{
		if ( !IsValid ) return;

		b2Body_SetType( id, type switch
		{
			PhysicsBodyType.Dynamic => b2BodyType.b2_dynamicBody,
			PhysicsBodyType.Keyframed => b2BodyType.b2_kinematicBody,
			_ => b2BodyType.b2_staticBody,
		} );

		ApplyMass();
	}

	/// <summary>Box2D can hold X, Y and the turn about Z. Depth and the two turns out of the plane cannot happen here, so their locks are already true.</summary>
	public void SetMotionLocks( bool x, bool y, bool z, bool pitch, bool yaw, bool roll )
	{
		if ( !IsValid ) return;
		b2Body_SetMotionLocks( id, new b2MotionLocks { linearX = x, linearY = y, angularZ = roll } );
	}

	public void SetBullet( bool enabled )
	{
		if ( IsValid ) b2Body_SetBullet( id, enabled );
	}

	//-- mass -- an explicit mass is kept and reapplied whenever the shapes change, as on Box3D bodies

	private float? explicitMass;
	private bool overrideMassCenter;
	private Vector3 localMassCenter;
	private float? explicitInertia;

	//The shapes' mass, then whatever was set by hand on top.
	internal void ApplyMass()
	{
		if ( !IsValid ) return;

		b2Body_UpdateMassFromShapes( id );

		if ( b2Body_GetType( id ) != b2BodyType.b2_dynamicBody ) return;
		if ( !explicitMass.HasValue && !overrideMassCenter && !explicitInertia.HasValue ) return;

		var data = b2Body_GetMassData( id );

		if ( explicitMass is float mass && mass > 0.0f )
		{
			//Inertia scales with mass for the same shape.
			if ( data.mass > 0.0f ) data.rotationalInertia *= mass / data.mass;
			data.mass = mass;
		}

		if ( overrideMassCenter )
			data.center = Box2DPlane.Vec( Local( localMassCenter ) );

		if ( explicitInertia is float inertia )
			data.rotationalInertia = inertia;

		b2Body_SetMassData( id, data );
	}

	public float GetMass() => IsValid ? b2Body_GetMass( id ) : 0;

	public void SetMass( float mass )
	{
		explicitMass = mass;
		ApplyMass();
	}

	public Vector3 GetMassCenter() => IsValid ? Box2DPlane.Point( b2Body_GetWorldCenter( id ), depth ) : default;
	public Vector3 GetLocalMassCenter() => IsValid ? FromLocal( b2Body_GetLocalCenter( id ) ) : default;

	public void SetLocalMassCenter( Vector3 center )
	{
		localMassCenter = center;
		if ( overrideMassCenter ) ApplyMass();
	}

	public bool GetOverrideMassCenter() => overrideMassCenter;

	public void SetOverrideMassCenter( bool value )
	{
		if ( overrideMassCenter == value ) return;
		overrideMassCenter = value;
		ApplyMass();
	}

	public void BuildMass()
	{
		explicitMass = null;
		ApplyMass();
	}

	public float GetDensity()
	{
		if ( shapes.Count == 0 ) return 0;

		var total = 0.0f;
		foreach ( var shape in shapes )
		{
			if ( shape?.Backend is Box2DShapeBackend s ) total += s.Density;
		}

		return total / shapes.Count;
	}

	/// <summary>There is one moment in the plane, about Z.</summary>
	public Vector3 GetLocalInertiaVector() => IsValid ? new Vector3( 0, 0, b2Body_GetRotationalInertia( id ) ) : default;
	public Rotation GetLocalInertiaOrientation() => Rotation.Identity;

	public void SetLocalInertia( Vector3 inertia, Rotation rotation )
	{
		explicitInertia = inertia.z;
		ApplyMass();
	}

	public void ResetLocalInertia()
	{
		explicitInertia = null;
		ApplyMass();
	}

	//-- gravity -------------------------------------------------------------- Box2D has a scale and no switch, so the switch is a scale of nought.

	private bool gravityEnabled = true;
	private float gravityScale = 1.0f;

	public bool IsGravityEnabled() => gravityEnabled;

	public void EnableGravity( bool enabled )
	{
		gravityEnabled = enabled;
		if ( IsValid ) b2Body_SetGravityScale( id, enabled ? gravityScale : 0.0f );
	}

	public float GetGravityScale() => gravityScale;

	public void SetGravityScale( float scale )
	{
		gravityScale = scale;
		if ( IsValid ) b2Body_SetGravityScale( id, gravityEnabled ? scale : 0.0f );
	}

	//-- sleeping -------------------------------------------------------------

	public bool IsSleeping() => IsValid && !b2Body_IsAwake( id );
	public void Sleep() { if ( IsValid ) b2Body_SetAwake( id, false ); }
	public void Wake() { if ( IsValid ) b2Body_SetAwake( id, true ); }

	public void EnableAutoSleeping() { if ( IsValid ) b2Body_EnableSleep( id, true ); }
	public void DisableAutoSleeping() { if ( IsValid ) b2Body_EnableSleep( id, false ); }

	public void SetContactRecycling( bool enabled ) { if ( IsValid ) b2Body_EnableContactRecycling( id, enabled ); }

	public float GetSleepThreshold() => IsValid ? b2Body_GetSleepThreshold( id ) : 0;
	public void SetSleepThreshold( float threshold ) { if ( IsValid ) b2Body_SetSleepThreshold( id, threshold ); }

	//-- enabled state --------------------------------------------------------

	public bool IsEnabled() => IsValid && b2Body_IsEnabled( id );
	public void Enable() { if ( IsValid ) b2Body_Enable( id ); }
	public void Disable() { if ( IsValid ) b2Body_Disable( id ); }

	/// <summary>Whether shapes made from here on are sensors; Collider sets it around making its shapes.</summary>
	public void SetTrigger( bool trigger ) => addingTriggers = trigger;

	public bool IsTouchEventEnabled()
	{
		foreach ( var shape in shapes )
		{
			if ( shape?.Backend is Box2DShapeBackend s && s.ContactEventsEnabled ) return true;
		}

		return false;
	}

	public void EnableTouchEvents() => SetTouchEvents( true );
	public void DisableTouchEvents() => SetTouchEvents( false );

	private void SetTouchEvents( bool enabled )
	{
		foreach ( var shape in shapes )
		{
			if ( shape?.Backend is Box2DShapeBackend s ) s.ContactEventsEnabled = enabled;
		}
	}

	//-- forces ---------------------------------------------------------------

	public void ApplyLinearImpulse( Vector3 impulse )
	{
		if ( IsValid ) b2Body_ApplyLinearImpulseToCenter( id, Box2DPlane.Vec( impulse ), true );
	}

	public void ApplyLinearImpulseAtWorldSpace( Vector3 impulse, Vector3 position )
	{
		if ( IsValid ) b2Body_ApplyLinearImpulse( id, Box2DPlane.Vec( impulse ), Box2DPlane.Vec( position ), true );
	}

	public void ApplyAngularImpulse( Vector3 impulse )
	{
		if ( IsValid ) b2Body_ApplyAngularImpulse( id, impulse.z, true );
	}

	public void ApplyForce( Vector3 force )
	{
		if ( IsValid ) b2Body_ApplyForceToCenter( id, Box2DPlane.Vec( force ), true );
	}

	public void ApplyForceAt( Vector3 force, Vector3 position )
	{
		if ( IsValid ) b2Body_ApplyForce( id, Box2DPlane.Vec( force ), Box2DPlane.Vec( position ), true );
	}

	public void ApplyTorque( Vector3 torque )
	{
		if ( IsValid ) b2Body_ApplyTorque( id, torque.z, true );
	}

	/// <summary>Box2D keeps force and torque in one accumulator, so clearing either clears both.</summary>
	public void ClearForces() { if ( IsValid ) b2Body_ClearForces( id ); }
	public void ClearTorque() { if ( IsValid ) b2Body_ClearForces( id ); }

	//-- shapes ---------------------------------------------------------------

	public int GetShapeCount() => shapes.Count;
	public PhysicsShape GetShape( int index ) => index >= 0 && index < shapes.Count ? shapes[index] : null;

	//Wraps the pieces as a shape of this kind, even with none, so a collider can resize it later
	private PhysicsShape Adopt( PhysicsShapeType kind, List<Box2DPart> parts )
	{
		if ( !IsValid ) return null;

		var shape = PhysicsShape.CreateDetached();
		var backend = new Box2DShapeBackend( this, kind, parts, addingTriggers ) { Owner = shape };

		//The world's rules, and the default surface's feel, go on before the pieces are made, so no piece is ever seen without them.
		backend.ApplyTags();

		backend.Build();

		shape.AttachBackend( backend );
		shapes.Add( shape );

		backend.SetMaterialIndex( null );
		ApplyMass();

		return shape;
	}

	internal List<Box2DPart> SphereParts( Vector3 center, float radius )
	{
		if ( radius <= 0.0f ) return null;

		return [Box2DPart.Of( new b2Circle { center = Box2DPlane.Vec( Local( center ) ), radius = radius } )];
	}

	internal List<Box2DPart> CapsuleParts( Vector3 a, Vector3 b, float radius )
	{
		var la = Local( a );
		var lb = Local( b );

		//Seen end on, a capsule is a circle.
		if ( (la - lb).LengthSquared < 1e-6f )
			return SphereParts( (a + b) * 0.5f, radius );

		return [Box2DPart.Of( new b2Capsule { center1 = Box2DPlane.Vec( la ), center2 = Box2DPlane.Vec( lb ), radius = radius } )];
	}

	internal List<Box2DPart> BoxParts( Vector3 position, Rotation rotation, Vector3 extent )
	{
		Span<Vector3> corners = stackalloc Vector3[8];
		for ( var i = 0; i < 8; i++ )
		{
			corners[i] = new Vector3(
				(i & 1) == 0 ? -extent.x : extent.x,
				(i & 2) == 0 ? -extent.y : extent.y,
				(i & 4) == 0 ? -extent.z : extent.z );
		}

		return HullParts( corners, new Transform( position, rotation ) );
	}

	//The outline of some points seen side-on, cut into pieces Box2D can hold.
	internal List<Box2DPart> HullParts( ReadOnlySpan<Vector3> points, Transform transform )
	{
		if ( points.Length == 0 ) return null;

		Span<Vector2> flat = points.Length <= 256 ? stackalloc Vector2[points.Length] : new Vector2[points.Length];
		for ( var i = 0; i < points.Length; i++ )
			flat[i] = Local( transform.PointToWorld( points[i] ) );

		var hull = Box2DConvexDecomposition.ConvexHull( flat );
		var parts = new List<Box2DPart>();

		if ( hull.Count < 3 )
		{
			if ( Box2DPart.TrySegment( flat, out var segment ) ) parts.Add( segment );
			return parts;
		}

		foreach ( var piece in Box2DConvexDecomposition.Split( hull ) )
		{
			if ( Box2DPart.TryPolygon( piece, out var part ) ) parts.Add( part );
		}

		return parts;
	}

	//A polygon a triangle. A mesh seen edge-on - a floor plane in a side-on scene - has no triangle with any area, and becomes the line across it.
	internal List<Box2DPart> MeshParts( ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices, Transform transform )
	{
		if ( vertices.Length < 2 ) return null;

		var flat = new Vector2[vertices.Length];
		for ( var i = 0; i < vertices.Length; i++ )
			flat[i] = Local( transform.PointToWorld( vertices[i] ) );

		var parts = new List<Box2DPart>();
		Span<Vector2> triangle = stackalloc Vector2[3];

		for ( var i = 0; i + 2 < indices.Length; i += 3 )
		{
			int ia = indices[i], ib = indices[i + 1], ic = indices[i + 2];
			if ( (uint)ia >= (uint)flat.Length || (uint)ib >= (uint)flat.Length || (uint)ic >= (uint)flat.Length ) continue;

			triangle[0] = flat[ia];
			triangle[1] = flat[ib];
			triangle[2] = flat[ic];

			if ( MathF.Abs( Box2DConvexDecomposition.Cross( triangle[0], triangle[1], triangle[2] ) ) * 0.5f < Box2DConvexDecomposition.AreaEpsilon )
				continue;

			if ( Box2DPart.TryPolygon( triangle, out var part ) && part.Type == b2ShapeType.b2_polygonShape )
				parts.Add( part );
		}

		if ( parts.Count == 0 && Box2DPart.TrySegment( flat, out var line ) )
			parts.Add( line );

		return parts;
	}

	internal static Vector3[] Copy( IntPtr vertices, int count )
	{
		if ( vertices == IntPtr.Zero || count <= 0 ) return Array.Empty<Vector3>();
		return new ReadOnlySpan<Vector3>( (void*)vertices, count ).ToArray();
	}

	internal static int[] CopyIndices( IntPtr indices, int count )
	{
		if ( indices == IntPtr.Zero || count <= 0 ) return Array.Empty<int>();
		return new ReadOnlySpan<int>( (void*)indices, count ).ToArray();
	}

	public PhysicsShape AddSphereShape( Vector3 center, float radius ) => Adopt( PhysicsShapeType.SHAPE_SPHERE, SphereParts( center, radius ) );
	public PhysicsShape AddCapsuleShape( Vector3 center1, Vector3 center2, float radius ) => Adopt( PhysicsShapeType.SHAPE_CAPSULE, CapsuleParts( center1, center2, radius ) );
	public PhysicsShape AddBoxShape( Vector3 position, Rotation rotation, Vector3 extent ) => Adopt( PhysicsShapeType.SHAPE_HULL, BoxParts( position, rotation, extent ) );

	public PhysicsShape AddHullShape( Vector3 position, Rotation rotation, int vertexCount, IntPtr vertices )
		=> Adopt( PhysicsShapeType.SHAPE_HULL, HullParts( Copy( vertices, vertexCount ), new Transform( position, rotation ) ) );

	public PhysicsShape AddMeshShape( int vertexCount, IntPtr vertices, int indexCount, IntPtr indices, int materialCount )
		=> Adopt( PhysicsShapeType.SHAPE_MESH, MeshParts( Copy( vertices, vertexCount ), CopyIndices( indices, indexCount ), Transform.Zero ) );

	/// <summary>Height fields are not a thing in the plane.</summary>
	public PhysicsShape AddHeightFieldShape( IntPtr heights, IntPtr materials, int sizeX, int sizeY, float sizeScale, float heightScale, int materialCount )
		=> null;

	public PhysicsShape AddHullShape( HullPart part, Transform transform )
	{
		var points = part?.GetPoints()?.ToArray();
		return points is null ? null : Adopt( PhysicsShapeType.SHAPE_HULL, HullParts( points, transform ) );
	}

	public PhysicsShape AddHullShape( MeshPart part, Transform transform )
	{
		var points = part?.GetVertices();
		return points is null ? null : Adopt( PhysicsShapeType.SHAPE_HULL, HullParts( points, transform ) );
	}

	public PhysicsShape AddMeshShape( MeshPart part, Transform transform, int materialCount )
	{
		var vertices = part?.GetVertices();
		var indices = part?.GetIndices();
		if ( vertices is null || indices is null ) return null;

		return Adopt( PhysicsShapeType.SHAPE_MESH, MeshParts( vertices, indices, transform ) );
	}

	public void RemoveShape( PhysicsShape shape )
	{
		if ( shape?.Backend is not Box2DShapeBackend s ) return;

		shapes.Remove( shape );
		s.Remove( true );
	}

	public void PurgeShapes()
	{
		foreach ( var shape in shapes )
		{
			if ( shape?.Backend is Box2DShapeBackend s )
				s.Remove( false );
		}

		shapes.Clear();
		ApplyMass();
	}

	public void SetMaterialIndex( string name )
	{
		foreach ( var shape in shapes )
			shape?.Backend?.SetMaterialIndex( name );
	}

	internal void RefreshTags()
	{
		foreach ( var shape in shapes )
		{
			if ( shape?.Backend is Box2DShapeBackend s ) s.ApplyTags();
		}
	}

	//-- queries --------------------------------------------------------------

	public BBox BuildBounds()
	{
		if ( !IsValid ) return default;
		if ( shapes.Count == 0 ) return BBox.FromPositionAndSize( GetPosition(), 0 );

		return Box2DPlane.Box( b2Body_ComputeAABB( id ), depth );
	}

	public Vector3 GetClosestPoint( Vector3 point )
	{
		if ( !IsValid ) return point;

		var target = Box2DPlane.Vec( point );
		var best = point;
		var bestDistance = float.MaxValue;

		foreach ( var shape in shapes )
		{
			if ( shape?.Backend is not Box2DShapeBackend s ) continue;

			foreach ( var part in s.Ids )
			{
				var closest = b2Shape_GetClosestPoint( part, target );
				var distance = (Box2DPlane.Flat( closest ) - Box2DPlane.Flat( point )).LengthSquared;
				if ( distance >= bestDistance ) continue;

				bestDistance = distance;
				best = new Vector3( closest.x, closest.y, point.z );
			}
		}

		return best;
	}

	public bool CheckOverlap( PhysicsBody body, Transform transform )
		=> ComputePenetration( body, transform, out _, out _ );

	/// <summary>How far <paramref name="body"/> would have to move to stop overlapping this one, were it at <paramref name="transform"/>: the deepest point over every pair of pieces, from Box2D's own manifolds.</summary>
	public bool ComputePenetration( PhysicsBody body, Transform transform, out Vector3 direction, out float distance )
	{
		direction = default;
		distance = default;

		if ( !IsValid || body?.Backend is not Box2DBodyBackend other || !other.IsValid ) return false;

		var xfA = b2Body_GetTransform( id );
		var angleB = other.referenceAngle + Box2DPlane.AngleOf( transform.Rotation ) - Box2DPlane.AngleOf( other.reference );
		var xfB = Box2DPlane.Xf( Box2DPlane.Flat( transform.Position ), angleB );

		var deepest = 0.0f;
		var normal = Vector2.Zero;

		foreach ( var mine in shapes )
		{
			if ( mine?.Backend is not Box2DShapeBackend a ) continue;

			foreach ( var theirs in other.shapes )
			{
				if ( theirs?.Backend is not Box2DShapeBackend b ) continue;

				foreach ( var pa in a.Parts )
				{
					foreach ( var pb in b.Parts )
					{
						if ( !Box2DCollide.Separation( pa, xfA, pb, xfB, out var n, out var separation ) ) continue;
						if ( separation >= deepest ) continue;

						deepest = separation;
						normal = n;
					}
				}
			}
		}

		if ( deepest >= 0.0f ) return false;

		direction = new Vector3( normal.x, normal.y, 0 );
		distance = -deepest;
		return true;
	}

	public bool IsTouching( PhysicsBody body, bool triggersOnly )
	{
		if ( body?.Backend is not Box2DBodyBackend other ) return false;

		foreach ( var mine in shapes )
		{
			foreach ( var theirs in other.shapes )
			{
				if ( mine?.Backend is Box2DShapeBackend s && s.IsTouching( theirs, triggersOnly ) ) return true;
			}
		}

		return false;
	}

	public bool IsTouching( PhysicsShape shape, bool triggersOnly )
	{
		foreach ( var mine in shapes )
		{
			if ( mine?.Backend is Box2DShapeBackend s && s.IsTouching( shape, triggersOnly ) ) return true;
		}

		return false;
	}

	//-- misc -----------------------------------------------------------------

	private PhysicsGroup aggregate;

	public PhysicsGroup GetAggregate() => aggregate;
	internal void SetAggregate( PhysicsGroup group ) => aggregate = group;

	public void ResetProxy() { }
}
