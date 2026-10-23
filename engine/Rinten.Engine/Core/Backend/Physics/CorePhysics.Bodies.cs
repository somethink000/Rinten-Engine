using Rinten.Native.Box3D;
using static Rinten.Native.Box3D.Box3D;
using static Rinten.Native.Box3D.Box3DMath;

namespace Rinten;

//Bodies: creation, pose, motion, mass, forces, and the shapes hung off them.
internal static unsafe partial class CorePhysics
{
	//Bodies

	/// <summary>A body with no shapes yet.</summary>
	public static ulong BodyCreate( ulong world, MotionType motionType, Vector3 position, Rotation rotation )
	{
		if ( !ResolveWorld( world, out var worldId ) ) return 0;

		b3BodyDef def = b3DefaultBodyDef();
		def.type = ToBox3D( motionType );
		def.position = ToBox3D( position );
		def.rotation = ToBox3D( rotation );

		return Pack( b3CreateBody( worldId, &def ) );
	}

	public static void BodyDestroy( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return;

		ForgetBodyGeometry( id );
		b3DestroyBody( id );
	}

	public static bool BodyValid( ulong body ) => ResolveBody( body, out _ );

	public static Vector3 BodyGetPosition( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return Vector3.Zero;
		return FromBox3D( b3Body_GetPosition( id ) );
	}

	//Moves a body and wakes what touched it, or sleeping bodies float when their ground moves
	private static void MoveBodyAndWakeNeighbours( b3BodyId id, b3Vec3 position, b3Quat rotation )
	{
		b3Vec3 was = b3Body_GetPosition( id );
		b3Quat turned = b3Body_GetRotation( id );

		float dx = was.x - position.x;
		float dy = was.y - position.y;
		float dz = was.z - position.z;

		//A quaternion and its negation are the same rotation, so the difference is measured through the dot: one means "no turn" whichever sign it came in.
		float dot = ( turned.v.x * rotation.v.x ) + ( turned.v.y * rotation.v.y )
			+ ( turned.v.z * rotation.v.z ) + ( turned.s * rotation.s );

		bool moved = ( ( dx * dx ) + ( dy * dy ) + ( dz * dz ) ) > 1.0e-12f
			|| b3AbsFloat( dot ) < 0.9999999f;

		b3Body_SetTransform( id, position, rotation );

		if ( !moved ) return;

		int capacity = b3Body_GetContactCapacity( id );
		if ( capacity <= 0 ) return;

		//Kept between calls rather than allocated per move - a platform carrying something is moved every frame it moves.
		var contacts = moveContacts;
		if ( contacts is null || contacts.Length < capacity )
			moveContacts = contacts = new b3ContactData[capacity];

		int count;
		fixed ( b3ContactData* buffer = contacts )
		{
			count = b3Body_GetContactData( id, buffer, capacity );
		}

		for ( int i = 0; i < count; ++i )
		{
			b3Body_SetAwake( b3Shape_GetBody( contacts[i].shapeIdA ), true );
			b3Body_SetAwake( b3Shape_GetBody( contacts[i].shapeIdB ), true );
		}
	}

	[ThreadStatic]
	private static b3ContactData[] moveContacts;

	public static void BodySetPosition( ulong body, Vector3 position )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		MoveBodyAndWakeNeighbours( id, ToBox3D( position ), b3Body_GetRotation( id ) );
	}

	public static Rotation BodyGetRotation( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return Rotation.Identity;
		return FromBox3D( b3Body_GetRotation( id ) );
	}

	public static void BodySetRotation( ulong body, Rotation rotation )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		MoveBodyAndWakeNeighbours( id, b3Body_GetPosition( id ), ToBox3D( rotation ) );
	}

	public static void BodySetTransform( ulong body, Vector3 position, Rotation rotation )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		MoveBodyAndWakeNeighbours( id, ToBox3D( position ), ToBox3D( rotation ) );
	}

	public static Vector3 BodyGetVelocity( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return Vector3.Zero;
		return FromBox3D( b3Body_GetLinearVelocity( id ) );
	}

	public static void BodySetVelocity( ulong body, Vector3 velocity )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_SetLinearVelocity( id, ToBox3D( velocity ) );
	}

	/// <summary>Radians per second - an angle, not a length, so no conversion.</summary>
	public static Vector3 BodyGetAngularVelocity( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return Vector3.Zero;
		return FromBox3D( b3Body_GetAngularVelocity( id ) );
	}

	public static void BodySetAngularVelocity( ulong body, Vector3 velocity )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_SetAngularVelocity( id, ToBox3D( velocity ) );
	}

	public static Vector3 BodyGetVelocityAtPoint( ulong body, Vector3 point )
	{
		if ( !ResolveBody( body, out var id ) ) return Vector3.Zero;
		return FromBox3D( b3Body_GetWorldPointVelocity( id, ToBox3D( point ) ) );
	}

	public static float BodyGetLinearDamping( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return 0.0f;
		return b3Body_GetLinearDamping( id );
	}

	public static void BodySetLinearDamping( ulong body, float damping )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_SetLinearDamping( id, damping );
	}

	public static float BodyGetAngularDamping( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return 0.0f;
		return b3Body_GetAngularDamping( id );
	}

	public static void BodySetAngularDamping( ulong body, float damping )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_SetAngularDamping( id, damping );
	}

	public static MotionType BodyGetMotionType( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return MotionType.Static;
		return FromBox3D( b3Body_GetType( id ) );
	}

	public static void BodySetMotionType( ulong body, MotionType motionType )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_SetType( id, ToBox3D( motionType ) );
	}

	public static void BodySetMotionLocks( ulong body, bool x, bool y, bool z, bool pitch, bool yaw, bool roll )
	{
		if ( !ResolveBody( body, out var id ) ) return;

		b3MotionLocks locks = default;
		locks.linearX = x;
		locks.linearY = y;
		locks.linearZ = z;

		//The rotation axes are named pitch, yaw, roll: rotation about Y, Z and X.
		locks.angularX = roll;
		locks.angularY = pitch;
		locks.angularZ = yaw;

		b3Body_SetMotionLocks( id, locks );
	}

	public static void BodySetBullet( ulong body, bool enabled )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_SetBullet( id, enabled );
	}

	public static float BodyGetMass( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return 0.0f;
		return b3Body_GetMass( id );
	}

	public static void BodySetMass( ulong body, float mass )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		if ( mass <= 0.0f ) return;

		//Only a dynamic body has a mass. Box3D takes one for any body and gives it an inverse mass, and its solver puts gravity on anything with one
		//- so a keyframed body handed its mass back after a collider changed fell out of the world while the transform it was keyed to stood still.
		if ( b3Body_GetType( id ) != b3BodyType.b3_dynamicBody ) return;

		//Box3D has no "set the mass, keep the shape of the body" call. Scaling the inertia by
		//the same factor is what keeps the body rotating the way its geometry says it should.
		b3MassData data = b3Body_GetMassData( id );
		float previous = data.mass;
		float scale = previous > 0.0f ? mass / previous : 1.0f;

		data.mass = mass;
		data.inertia.cx.x *= scale; data.inertia.cx.y *= scale; data.inertia.cx.z *= scale;
		data.inertia.cy.x *= scale; data.inertia.cy.y *= scale; data.inertia.cy.z *= scale;
		data.inertia.cz.x *= scale; data.inertia.cz.y *= scale; data.inertia.cz.z *= scale;

		b3Body_SetMassData( id, data );
	}

	public static Vector3 BodyGetMassCenter( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return Vector3.Zero;
		return FromBox3D( b3Body_GetWorldCenterOfMass( id ) );
	}

	public static Vector3 BodyGetLocalMassCenter( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return Vector3.Zero;
		return FromBox3D( b3Body_GetLocalCenterOfMass( id ) );
	}

	public static void BodySetLocalMassCenter( ulong body, Vector3 center )
	{
		if ( !ResolveBody( body, out var id ) ) return;

		//Mass data again, and the same reason as BodySetMass.
		if ( b3Body_GetType( id ) != b3BodyType.b3_dynamicBody ) return;

		b3MassData data = b3Body_GetMassData( id );
		data.center = ToBox3D( center );
		b3Body_SetMassData( id, data );
	}

	public static void BodyBuildMass( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_ApplyMassFromShapes( id );
	}

	/// <summary>Three principal moments, and the rotation they are measured along.</summary>
	public static void BodyGetInertiaTensor( ulong body, out Vector3 moments, out Rotation axes )
	{
		moments = Vector3.Zero;
		axes = Rotation.Identity;

		if ( !ResolveBody( body, out var id ) ) return;

		b3Matrix3 tensor = b3Body_GetLocalRotationalInertia( id );
		b3Quat principal = Diagonalise( tensor );

		//Rotating the tensor into its own axes leaves the principal moments on the diagonal, which is the answer in the form the engine above wants.
		b3Matrix3 rotation = b3MakeMatrixFromQuat( principal );
		b3Matrix3 diagonal = b3MulMM( b3Transpose( rotation ), b3MulMM( tensor, rotation ) );

		moments = new Vector3( diagonal.cx.x, diagonal.cy.y, diagonal.cz.z );
		axes = FromBox3D( principal );
	}

	public static void BodySetInertiaTensor( ulong body, Vector3 moments, Rotation rotation )
	{
		if ( !ResolveBody( body, out var id ) ) return;

		b3Quat axes = ToBox3D( rotation );

		b3Matrix3 diagonal = b3Mat3_zero;
		diagonal.cx.x = moments.x;
		diagonal.cy.y = moments.y;
		diagonal.cz.z = moments.z;

		//The tensor is the moments seen from outside their own axes: R D R^T.
		b3Matrix3 axisMatrix = b3MakeMatrixFromQuat( axes );

		b3MassData mass = b3Body_GetMassData( id );
		mass.inertia = b3MulMM( axisMatrix, b3MulMM( diagonal, b3Transpose( axisMatrix ) ) );

		b3Body_SetMassData( id, mass );
	}

	public static bool BodyGravityEnabled( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return false;

		//Box3D has no gravity switch, only a scale, so zero means off.
		return b3Body_GetGravityScale( id ) != 0.0f;
	}

	public static void BodyEnableGravity( ulong body, bool enabled )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_SetGravityScale( id, enabled ? 1.0f : 0.0f );
	}

	public static float BodyGetGravityScale( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return 0.0f;
		return b3Body_GetGravityScale( id );
	}

	public static void BodySetGravityScale( ulong body, float scale )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_SetGravityScale( id, scale );
	}

	public static bool BodyIsAwake( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return false;
		return b3Body_IsAwake( id );
	}

	public static void BodySetAwake( ulong body, bool awake )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_SetAwake( id, awake );
	}

	public static bool BodyAutoSleepEnabled( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return false;
		return b3Body_IsSleepEnabled( id );
	}

	public static void BodyEnableAutoSleep( ulong body, bool enabled )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_EnableSleep( id, enabled );
	}

	public static float BodyGetSleepThreshold( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return 0.0f;
		return b3Body_GetSleepThreshold( id );
	}

	public static void BodySetSleepThreshold( ulong body, float threshold )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_SetSleepThreshold( id, threshold );
	}

	public static bool BodyIsEnabled( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return false;
		return b3Body_IsEnabled( id );
	}

	public static void BodySetEnabled( ulong body, bool enabled )
	{
		if ( !ResolveBody( body, out var id ) ) return;

		if ( enabled ) b3Body_Enable( id );
		else b3Body_Disable( id );
	}

	public static void BodyApplyImpulse( ulong body, Vector3 impulse )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_ApplyLinearImpulseToCenter( id, ToBox3D( impulse ), true );
	}

	public static void BodyApplyImpulseAt( ulong body, Vector3 impulse, Vector3 point )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_ApplyLinearImpulse( id, ToBox3D( impulse ), ToBox3D( point ), true );
	}

	/// <summary>Angular impulse is inertia times angular velocity, so it carries length^2.</summary>
	public static void BodyApplyAngularImpulse( ulong body, Vector3 impulse )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_ApplyAngularImpulse( id, ToBox3D( impulse ), true );
	}

	public static void BodyApplyForce( ulong body, Vector3 force )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_ApplyForceToCenter( id, ToBox3D( force ), true );
	}

	public static void BodyApplyForceAt( ulong body, Vector3 force, Vector3 point )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_ApplyForce( id, ToBox3D( force ), ToBox3D( point ), true );
	}

	/// <summary>Torque is force times a lever arm, so it carries length^2.</summary>
	public static void BodyApplyTorque( ulong body, Vector3 torque )
	{
		if ( !ResolveBody( body, out var id ) ) return;
		b3Body_ApplyTorque( id, ToBox3D( torque ), true );
	}

	public static BBox BodyGetBounds( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return default;
		return FromBox3D( b3Body_ComputeAABB( id ) );
	}

	public static Vector3 BodyGetClosestPoint( ulong body, Vector3 point )
	{
		if ( !ResolveBody( body, out var id ) ) return point;

		b3Vec3 result = default;
		b3Body_GetClosestPoint( id, &result, ToBox3D( point ) );
		return FromBox3D( result );
	}

	//Shapes on a body

	public static int BodyShapeCount( ulong body )
	{
		if ( !ResolveBody( body, out var id ) ) return 0;
		return b3Body_GetShapeCount( id );
	}

	public static ulong BodyGetShape( ulong body, int index )
	{
		if ( !ResolveBody( body, out var id ) ) return 0;
		if ( index < 0 ) return 0;

		int count = b3Body_GetShapeCount( id );
		if ( index >= count ) return 0;

		Span<b3ShapeId> shapes = count <= StackShapes ? stackalloc b3ShapeId[count] : new b3ShapeId[count];
		int written = GetShapes( id, shapes );
		if ( index >= written ) return 0;

		return Pack( shapes[index] );
	}

	public static ulong BodyAddSphere( ulong body, Vector3 center, float radius, float density, bool isTrigger )
	{
		if ( !ResolveBody( body, out var id ) ) return 0;

		b3ShapeDef def = MakeShapeDef( density, isTrigger );

		b3Sphere sphere = default;
		sphere.center = ToBox3D( center );
		sphere.radius = radius;

		return Pack( b3CreateSphereShape( id, &def, &sphere ) );
	}

	public static ulong BodyAddCapsule( ulong body, Vector3 a, Vector3 b, float radius, float density, bool isTrigger )
	{
		if ( !ResolveBody( body, out var id ) ) return 0;

		b3ShapeDef def = MakeShapeDef( density, isTrigger );

		b3Capsule capsule = default;
		capsule.center1 = ToBox3D( a );
		capsule.center2 = ToBox3D( b );
		capsule.radius = radius;

		return Pack( b3CreateCapsuleShape( id, &def, &capsule ) );
	}

	public static ulong BodyAddBox( ulong body, Vector3 center, Rotation rotation, Vector3 extents, float density, bool isTrigger )
	{
		if ( !ResolveBody( body, out var id ) ) return 0;

		b3ShapeDef def = MakeShapeDef( density, isTrigger );

		b3Transform transform = default;
		transform.p = ToBox3D( center );
		transform.q = ToBox3D( rotation );

		//b3BoxHull owns the storage b3HullData points into - the arrays sit at offsets from its own address - so
		//it has to stay put until the create call has cloned it: a named local, taken by address, not a temporary.
		b3BoxHull hull = b3MakeTransformedBoxHull( extents.x, extents.y, extents.z, transform );

		return Pack( b3CreateHullShape( id, &def, &hull.@base ) );
	}

	/// <summary>A convex hull around <paramref name="points"/>, given as xyz triples.</summary>
	public static ulong BodyAddHull( ulong body, ReadOnlySpan<float> points, float density, bool isTrigger )
	{
		if ( !ResolveBody( body, out var id ) ) return 0;

		int pointCount = points.Length / 3;
		if ( pointCount < 4 ) return 0;

		b3HullData* hull;

		//Three floats in a row are a b3Vec3 already, so the caller's triples are read in place rather than copied into a second array.
		fixed ( float* pinned = points )
		{
			//The vertex cap is clamped to at least 4, so passing zero to mean "no limit" silently
			//simplifies every hull down to a tetrahedron. Ask for as many vertices as we handed in.
			hull = b3CreateHull( (b3Vec3*)pinned, pointCount, pointCount );
		}

		if ( hull == null ) return 0;

		b3ShapeDef def = MakeShapeDef( density, isTrigger );

		//The shape clones the geometry, so ours is ours to free.
		b3ShapeId shape = b3CreateHullShape( id, &def, hull );
		b3DestroyHull( hull );

		return Pack( shape );
	}

	/// <summary>A triangle mesh from xyz triples and three indices per triangle. Density zero: a triangle soup has no interior, so it contributes no mass.</summary>
	public static ulong BodyAddMesh( ulong body, ReadOnlySpan<float> vertices, ReadOnlySpan<int> indices, bool isTrigger )
		=> BodyAddMesh( body, vertices, indices, isTrigger, out _ );

	/// <summary>The same, handing back the cooked mesh so later shapes can borrow it through <see cref="BodyAddCookedMesh"/>.</summary>
	public static ulong BodyAddMesh( ulong body, ReadOnlySpan<float> vertices, ReadOnlySpan<int> indices, bool isTrigger, out CookedMesh cooked )
	{
		cooked = null;
		if ( !ResolveBody( body, out var id ) ) return 0;

		int vertexCount = vertices.Length / 3;
		int indexCount = indices.Length;
		if ( vertexCount < 3 || indexCount < 3 ) return 0;

		b3MeshData* mesh;

		fixed ( float* pinnedVertices = vertices )
		fixed ( int* pinnedIndices = indices )
		{
			b3MeshDef meshDef = default;
			meshDef.vertices = (b3Vec3*)pinnedVertices;
			meshDef.indices = pinnedIndices;
			meshDef.vertexCount = vertexCount;
			meshDef.triangleCount = indexCount / 3;

			//The source mesh is already welded, and welding again would renumber the indices we were handed.
			meshDef.weldVertices = false;
			meshDef.weldTolerance = 0.0f;

			//Adjacency lets the solver ignore edges that are interior to the surface, which is what stops things catching on the seams between triangles.
			meshDef.identifyEdges = true;

			mesh = b3CreateMesh( &meshDef, null, 0 );
		}

		if ( mesh == null ) return 0;

		b3ShapeDef def = MakeShapeDef( 0.0f, isTrigger );

		b3ShapeId shape = b3CreateMeshShape( id, &def, mesh, Vec3( 1.0f, 1.0f, 1.0f ) );
		if ( B3_IS_NULL( shape ) )
		{
			b3DestroyMesh( mesh );
			return 0;
		}

		//Unlike a hull, the shape only borrows this - we own it for as long as a shape on it lives.
		cooked = new CookedMesh { Data = mesh, Users = 1 };
		RememberMesh( Pack( shape ), cooked );
		return Pack( shape );
	}

	/// <summary>A shape on a mesh another shape cooked, at no cost but the shape. Nought once that mesh has gone with its last shape.</summary>
	public static ulong BodyAddCookedMesh( ulong body, CookedMesh cooked, bool isTrigger )
	{
		if ( cooked is null || !ResolveBody( body, out var id ) ) return 0;

		//Taken before the shape is made, so the last other shape going meanwhile cannot free it under us
		lock ( borrowedLock )
		{
			if ( cooked.Users <= 0 ) return 0;
			cooked.Users++;
		}

		b3ShapeDef def = MakeShapeDef( 0.0f, isTrigger );

		b3ShapeId shape = b3CreateMeshShape( id, &def, cooked.Data, Vec3( 1.0f, 1.0f, 1.0f ) );
		if ( B3_IS_NULL( shape ) )
		{
			Release( cooked );
			return 0;
		}

		RememberMesh( Pack( shape ), cooked );
		return Pack( shape );
	}

	/// <summary>Terrain. The grid is on local X and Z with height on local Y; a sample at (x, z) is at z * countX + x in <paramref name="heights"/>, and one material per cell is read from the sample at its corner. <paramref name="materials"/> may be empty.</summary>
	public static ulong BodyAddHeightField( ulong body, ReadOnlySpan<ushort> heights, ReadOnlySpan<byte> materials,
		int countX, int countZ, float scaleX, float scaleZ, float heightScale, int materialCount )
	{
		if ( !ResolveBody( body, out var id ) ) return 0;
		if ( countX < 2 || countZ < 2 ) return 0;

		int sampleCount = countX * countZ;
		int cellCount = ( countX - 1 ) * ( countZ - 1 );
		if ( heights.Length < sampleCount ) return 0;

		//Box3D takes floats and requantises them against the range we give it, so handing it the raw 16-bit samples over their full range is lossless.
		var samples = new float[sampleCount];
		for ( int i = 0; i < sampleCount; ++i )
			samples[i] = heights[i];

		var cellMaterials = new byte[cellCount];
		if ( materials.Length >= sampleCount )
		{
			byte highest = materialCount > 0 ? (byte)( materialCount - 1 ) : (byte)0;

			for ( int row = 0; row < countZ - 1; ++row )
			{
				for ( int column = 0; column < countX - 1; ++column )
				{
					//One material per cell, taken from the sample at its corner.
					byte value = materials[row * countX + column];

					//255 is a hole on both sides. Anything else has to index the shape's material table or Box3D reads past it.
					cellMaterials[row * ( countX - 1 ) + column] =
						value == B3_HEIGHT_FIELD_HOLE ? value : ( value > highest ? highest : value );
				}
			}
		}

		b3HeightFieldData* field;

		fixed ( float* pinnedSamples = samples )
		fixed ( byte* pinnedMaterials = cellMaterials )
		{
			b3HeightFieldDef def = default;
			def.heights = pinnedSamples;
			def.materialIndices = pinnedMaterials;
			def.countX = countX;
			def.countZ = countZ;
			def.scale = Vec3( scaleX, heightScale, scaleZ );
			def.globalMinimumHeight = 0.0f;
			def.globalMaximumHeight = 65535.0f;

			//Default winding puts the surface normals along local +Y, which is the same axis the height is on - so the ground faces up in this frame, and it stays
			//facing up under any rotation the caller applies to the body. Flipping this points the terrain's normals into the ground and nothing can stand on it.
			def.clockwiseWinding = false;

			field = b3CreateHeightField( &def );
		}

		if ( field == null ) return 0;

		b3ShapeDef shapeDef = MakeShapeDef( 0.0f, false );

		//Materials are per triangle, so the table has to be as long as the indices we just clamped against.
		b3SurfaceMaterial[] table = null;
		if ( materialCount > 0 )
		{
			table = new b3SurfaceMaterial[materialCount];
			var surface = b3DefaultSurfaceMaterial();
			for ( int i = 0; i < materialCount; ++i ) table[i] = surface;
		}

		b3ShapeId shape;

		fixed ( b3SurfaceMaterial* pinnedTable = table )
		{
			if ( table is not null )
			{
				shapeDef.materials = pinnedTable;
				shapeDef.materialCount = materialCount;
			}

			shape = b3CreateHeightFieldShape( id, &shapeDef, field );
		}

		if ( B3_IS_NULL( shape ) )
		{
			b3DestroyHeightField( field );
			return 0;
		}

		//Unlike hulls and meshes, the shape only borrows this - we own it for as long as the shape lives.
		RememberHeightField( Pack( shape ), field );
		return Pack( shape );
	}
}
