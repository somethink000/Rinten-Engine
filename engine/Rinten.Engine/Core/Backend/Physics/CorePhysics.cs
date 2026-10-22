using System.Runtime.InteropServices;
using Rinten.Native.Box3D;
using static Rinten.Native.Box3D.Box3D;
using static Rinten.Native.Box3D.Box3DMath;

namespace Rinten;

//Physics on Box3D in SI units on both sides, no conversion; backend classes hold handles and call in
internal static unsafe partial class CorePhysics
{
	/// <summary>Matches PhysicsBodyType and PhysicsMotionType, which agree.</summary>
	public enum MotionType
	{
		Static = 0,
		Keyframed = 1,
		Dynamic = 2,
	}

	/// <summary>Matches PhysicsShapeType.</summary>
	public enum ShapeType
	{
		Sphere = 0,
		Capsule = 1,
		Hull = 2,
		Mesh = 3,
		HeightField = 4,
		Unknown = 5,
	}

	//-- handles -- Box3D ids reinterpreted as ulong; all-zero is null on both sides

	private static ulong Pack( b3WorldId id ) { ulong handle = 0; *(b3WorldId*)&handle = id; return handle; }
	private static ulong Pack( b3BodyId id ) { ulong handle = 0; *(b3BodyId*)&handle = id; return handle; }
	private static ulong Pack( b3ShapeId id ) { ulong handle = 0; *(b3ShapeId*)&handle = id; return handle; }
	private static ulong Pack( b3JointId id ) { ulong handle = 0; *(b3JointId*)&handle = id; return handle; }

	private static b3WorldId UnpackWorld( ulong handle ) => *(b3WorldId*)&handle;
	private static b3BodyId UnpackBody( ulong handle ) => *(b3BodyId*)&handle;
	private static b3ShapeId UnpackShape( ulong handle ) => *(b3ShapeId*)&handle;
	private static b3JointId UnpackJoint( ulong handle ) => *(b3JointId*)&handle;

	//Every entry point goes through these. A managed object can outlive its native peer - during hotload,
	//or when a world is torn down while game code still holds bodies - and that must be inert, not fatal.

	private static bool ResolveWorld( ulong handle, out b3WorldId id )
	{
		id = default;
		if ( handle == 0 ) return false;
		id = UnpackWorld( handle );
		return b3World_IsValid( id );
	}

	private static bool ResolveBody( ulong handle, out b3BodyId id )
	{
		id = default;
		if ( handle == 0 ) return false;
		id = UnpackBody( handle );
		return b3Body_IsValid( id );
	}

	private static bool ResolveShape( ulong handle, out b3ShapeId id )
	{
		id = default;
		if ( handle == 0 ) return false;
		id = UnpackShape( handle );
		return b3Shape_IsValid( id );
	}

	private static bool ResolveJoint( ulong handle, out b3JointId id )
	{
		id = default;
		if ( handle == 0 ) return false;
		id = UnpackJoint( handle );
		return b3Joint_IsValid( id );
	}

	//-- conversions ------------------------------------------------------------

	private static b3Vec3 ToBox3D( Vector3 v ) => Vec3( v.x, v.y, v.z );
	private static Vector3 FromBox3D( b3Vec3 v ) => new( v.x, v.y, v.z );

	private static b3Quat ToBox3D( Rotation r ) => Quat( r.x, r.y, r.z, r.w );
	private static Rotation FromBox3D( b3Quat q ) => new( q.v.x, q.v.y, q.v.z, q.s );

	private static b3Transform ToBox3D( Transform t ) => new() { p = ToBox3D( t.Position ), q = ToBox3D( t.Rotation ) };
	private static Transform FromBox3D( b3Transform t ) => new( FromBox3D( t.p ), FromBox3D( t.q ) );

	private static BBox FromBox3D( b3AABB aabb ) => new( FromBox3D( aabb.lowerBound ), FromBox3D( aabb.upperBound ) );

	private static b3BodyType ToBox3D( MotionType motionType )
	{
		switch ( motionType )
		{
			case MotionType.Static: return b3BodyType.b3_staticBody;
			case MotionType.Keyframed: return b3BodyType.b3_kinematicBody;
			default: return b3BodyType.b3_dynamicBody;
		}
	}

	private static MotionType FromBox3D( b3BodyType type )
	{
		switch ( type )
		{
			case b3BodyType.b3_staticBody: return MotionType.Static;
			case b3BodyType.b3_kinematicBody: return MotionType.Keyframed;
			default: return MotionType.Dynamic;
		}
	}

	private static ShapeType FromBox3D( b3ShapeType type )
	{
		switch ( type )
		{
			case b3ShapeType.b3_sphereShape: return ShapeType.Sphere;
			case b3ShapeType.b3_capsuleShape: return ShapeType.Capsule;
			case b3ShapeType.b3_hullShape: return ShapeType.Hull;
			case b3ShapeType.b3_meshShape: return ShapeType.Mesh;
			case b3ShapeType.b3_heightShape: return ShapeType.HeightField;
			default: return ShapeType.Unknown;
		}
	}

	private static b3ShapeDef MakeShapeDef( float density, bool isTrigger )
	{
		b3ShapeDef def = b3DefaultShapeDef();
		def.density = density;

		//A trigger reports overlaps and never pushes back, which is what PhysicsShape.IsTrigger means.
		def.isSensor = isTrigger;

		//Box3D asks both sides of a sensor pair for this flag: on the sensor it means "report", on everything else it means "can be reported". With it
		//only on triggers, a trigger saw other triggers and nothing solid - every rigidbody fell straight through. Solid shapes are what triggers are for.
		def.enableSensorEvents = true;

		//Touch events are dispatched off contacts, so they have to be on for the events
		//to exist at all; the shape's collision mask decides whether anything listens.
		def.enableContactEvents = !isTrigger;

		//Tag-pair collision rules are decided in a callback, and only one of the two shapes
		//has to ask for it. Every shape asks, because any of them may be tagged later.
		def.enableCustomFiltering = true;

		return def;
	}

	//A body's shapes, into a buffer the caller sized from b3Body_GetShapeCount. Returns how many were written.
	private static int GetShapes( b3BodyId body, Span<b3ShapeId> into )
	{
		fixed ( b3ShapeId* buffer = into )
		{
			return b3Body_GetShapes( body, buffer, into.Length );
		}
	}

	//Bodies with more shapes than this read them into a heap array; the rest fit on the stack. A compound collider is rarely past a dozen.
	private const int StackShapes = 64;

	//-- borrowed geometry -- Box3D only borrows meshes and height fields; we free them after the shape

	private struct BorrowedGeometry
	{
		public CookedMesh Mesh;
		public b3HeightFieldData* HeightField;
	}

	/// <summary>A triangle mesh cooked once and borrowed by every shape made from it; freed with the last of them.</summary>
	public sealed class CookedMesh
	{
		internal b3MeshData* Data;

		//Shapes borrowing it, under borrowedLock; nought means freed and not to be borrowed again
		internal int Users;

		/// <summary>Whether a shape can still be made from it.</summary>
		public bool IsLive
		{
			get { lock ( borrowedLock ) return Users > 0; }
		}
	}

	//Guarded, because worlds do not all step on the same thread and a shape can be made on any of them.
	//The C++ left this to luck; a Dictionary written from two threads is not merely wrong but corrupt.
	private static readonly object borrowedLock = new();
	private static readonly Dictionary<ulong, BorrowedGeometry> borrowedGeometry = new();

	private static void RememberMesh( ulong shape, CookedMesh mesh )
	{
		lock ( borrowedLock ) borrowedGeometry[shape] = new BorrowedGeometry { Mesh = mesh };
	}

	//One shape fewer on the mesh; the last one frees it
	private static void Release( CookedMesh mesh )
	{
		b3MeshData* freed = null;

		lock ( borrowedLock )
		{
			if ( mesh.Users <= 0 ) return;
			if ( --mesh.Users > 0 ) return;

			freed = mesh.Data;
			mesh.Data = null;
		}

		if ( freed != null ) b3DestroyMesh( freed );
	}

	private static void RememberHeightField( ulong shape, b3HeightFieldData* field )
	{
		lock ( borrowedLock ) borrowedGeometry[shape] = new BorrowedGeometry { HeightField = field };
	}

	private static void ForgetGeometry( ulong shape )
	{
		BorrowedGeometry found;

		lock ( borrowedLock )
		{
			if ( !borrowedGeometry.Remove( shape, out found ) ) return;
		}

		if ( found.Mesh != null ) Release( found.Mesh );
		if ( found.HeightField != null ) b3DestroyHeightField( found.HeightField );
	}

	//Free what every shape on a body borrowed, before the body takes the shapes with it.
	private static void ForgetBodyGeometry( b3BodyId body )
	{
		lock ( borrowedLock )
		{
			if ( borrowedGeometry.Count == 0 ) return;
		}

		int count = b3Body_GetShapeCount( body );
		if ( count <= 0 ) return;

		Span<b3ShapeId> shapes = count <= StackShapes ? stackalloc b3ShapeId[count] : new b3ShapeId[count];
		int written = GetShapes( body, shapes );

		for ( int i = 0; i < written; ++i )
			ForgetGeometry( Pack( shapes[i] ) );
	}

	//How many pieces of geometry are being held on shapes' behalf. Diagnostics.
	private static int BorrowedCount
	{
		get { lock ( borrowedLock ) return borrowedGeometry.Count; }
	}

	//-- principal axes -----------------------------------------------------------

	//Principal axes of a symmetric tensor via Jacobi rotations, exiting early once diagonal
	private static b3Quat Diagonalise( in b3Matrix3 tensor )
	{
		b3Quat q = Quat( 0.0f, 0.0f, 0.0f, 1.0f );

		Span<float> offDiagonal = stackalloc float[3];
		Span<float> diagonal = stackalloc float[3];
		Span<float> jacobi = stackalloc float[4];

		for ( int step = 0; step < 24; ++step )
		{
			b3Matrix3 rotation = b3MakeMatrixFromQuat( q );
			b3Matrix3 d = b3MulMM( b3Transpose( rotation ), b3MulMM( tensor, rotation ) );

			//Column-major: cy.z is the (row z, column y) term, and so on.
			offDiagonal[0] = d.cy.z;
			offDiagonal[1] = d.cx.z;
			offDiagonal[2] = d.cx.y;

			int k = 0;
			if ( b3AbsFloat( offDiagonal[1] ) > b3AbsFloat( offDiagonal[k] ) ) k = 1;
			if ( b3AbsFloat( offDiagonal[2] ) > b3AbsFloat( offDiagonal[k] ) ) k = 2;

			if ( offDiagonal[k] == 0.0f ) break;

			int k1 = ( k + 1 ) % 3;
			int k2 = ( k + 2 ) % 3;

			diagonal[0] = d.cx.x;
			diagonal[1] = d.cy.y;
			diagonal[2] = d.cz.z;

			//The angle that cancels this term, taken through its tangent so the nearly-diagonal case stays well conditioned.
			float theta = ( diagonal[k2] - diagonal[k1] ) / ( 2.0f * offDiagonal[k] );
			float sign = theta > 0.0f ? 1.0f : -1.0f;

			theta *= sign;

			float t = sign / ( theta + ( theta < 1.0e6f ? MathF.Sqrt( theta * theta + 1.0f ) : theta ) );
			float c = 1.0f / MathF.Sqrt( t * t + 1.0f );

			//Nothing left to turn.
			if ( c == 1.0f ) break;

			jacobi[0] = jacobi[1] = jacobi[2] = jacobi[3] = 0.0f;
			jacobi[k] = -sign * MathF.Sqrt( ( 1.0f - c ) * 0.5f );
			jacobi[3] = MathF.Sqrt( 1.0f - jacobi[k] * jacobi[k] );

			if ( jacobi[3] == 1.0f ) break;

			q = b3NormalizeQuat( b3MulQuat( q, Quat( jacobi[0], jacobi[1], jacobi[2], jacobi[3] ) ) );
		}

		return q;
	}

	//World

	public static ulong WorldCreate()
	{
		b3WorldDef def = b3DefaultWorldDef();

		//Single-threaded. Determinism is worth more than throughput while the core is being validated tick-for-tick against the old physics.
		def.workerCount = 1;

		//Earth, in metres per second squared. The scene overrides it on creation anyway; this is what a world nobody configured falls at.
		def.gravity = Vec3( 0.0f, -9.81f, 0.0f );

		return Pack( b3CreateWorld( &def ) );
	}

	public static void WorldDestroy( ulong world )
	{
		if ( !ResolveWorld( world, out var id ) ) return;

		//Destroying a world takes its bodies and shapes with it without going through us, so anything we own on their
		//behalf has to go first. A shape id carries the world it belongs to, which is how we know which are ours.
		List<KeyValuePair<ulong, BorrowedGeometry>> ours = null;

		lock ( borrowedLock )
		{
			foreach ( var entry in borrowedGeometry )
			{
				if ( UnpackShape( entry.Key ).world0 == id.index1 - 1 )
					( ours ??= new() ).Add( entry );
			}

			if ( ours is not null )
			{
				foreach ( var entry in ours ) borrowedGeometry.Remove( entry.Key );
			}
		}

		if ( ours is not null )
		{
			foreach ( var entry in ours )
			{
				if ( entry.Value.Mesh != null ) Release( entry.Value.Mesh );
				if ( entry.Value.HeightField != null ) b3DestroyHeightField( entry.Value.HeightField );
			}
		}

		ReleaseCollisionRules( world );
		b3DestroyWorld( id );
	}

	public static bool WorldValid( ulong world ) => ResolveWorld( world, out _ );

	public static void WorldSetGravity( ulong world, Vector3 gravity )
	{
		if ( !ResolveWorld( world, out var id ) ) return;
		b3World_SetGravity( id, ToBox3D( gravity ) );
	}

	public static Vector3 WorldGetGravity( ulong world )
	{
		if ( !ResolveWorld( world, out var id ) ) return Vector3.Zero;
		return FromBox3D( b3World_GetGravity( id ) );
	}

	public static void WorldStep( ulong world, float deltaTime, int substeps )
	{
		if ( !ResolveWorld( world, out var id ) ) return;
		if ( deltaTime <= 0.0f ) return;
		if ( substeps < 1 ) substeps = 1;

		b3World_Step( id, deltaTime, substeps );
	}

	public static int WorldBodyCount( ulong world )
	{
		if ( !ResolveWorld( world, out var id ) ) return 0;
		return b3World_GetCounters( id ).bodyCount;
	}

	/// <summary>Live object counts, for tracking down leaks. Writes six ints: bodies, shapes, contacts, joints, islands, and the pieces of geometry being held on behalf of shapes that only borrow theirs - meshes and height fields.</summary>
	public static void WorldCounts( ulong world, Span<int> outCounts )
	{
		if ( outCounts.Length < 6 ) return;
		for ( int i = 0; i < 6; ++i ) outCounts[i] = 0;

		if ( !ResolveWorld( world, out var id ) ) return;

		b3Counters counters = b3World_GetCounters( id );
		outCounts[0] = counters.bodyCount;
		outCounts[1] = counters.shapeCount;
		outCounts[2] = counters.contactCount;
		outCounts[3] = counters.jointCount;
		outCounts[4] = counters.islandCount;
		outCounts[5] = BorrowedCount;
	}

	public static bool WorldSleepingEnabled( ulong world )
	{
		if ( !ResolveWorld( world, out var id ) ) return false;
		return b3World_IsSleepingEnabled( id );
	}

	public static void WorldEnableSleeping( ulong world, bool enabled )
	{
		if ( !ResolveWorld( world, out var id ) ) return;
		b3World_EnableSleeping( id, enabled );
	}

	public static void WorldSetMaxLinearSpeed( ulong world, float speed )
	{
		if ( !ResolveWorld( world, out var id ) ) return;
		b3World_SetMaximumLinearSpeed( id, speed );
	}

	public static float WorldGetMaxLinearSpeed( ulong world )
	{
		if ( !ResolveWorld( world, out var id ) ) return 0.0f;
		return b3World_GetMaximumLinearSpeed( id );
	}

	public static bool WorldContinuousEnabled( ulong world )
	{
		if ( !ResolveWorld( world, out var id ) ) return false;
		return b3World_IsContinuousEnabled( id );
	}

	public static void WorldEnableContinuous( ulong world, bool enabled )
	{
		if ( !ResolveWorld( world, out var id ) ) return;
		b3World_EnableContinuous( id, enabled );
	}

	//Collision rules

	/// <summary>How many distinct tags can take part in collision rules. One bit each, so a shape's whole tag set is a single word and filtering never allocates.</summary>
	public const int MaxCollisionTags = 64;

	/// <summary>Per-pair collision results, matching CollisionRules.Result - CoreCollisionRules says them.</summary>
	public const byte Collide = CoreCollisionRules.Collide;
	public const byte Trigger = CoreCollisionRules.Trigger;  //no response, but the touch is still reported
	public const byte Ignore = CoreCollisionRules.Ignore;    //no contact at all

	//A shape's tag set lives in its user data. Box3D stores a pointer there and never looks at it, and
	//a 64-bit mask fits, so this needs no side table and nothing to clean up when the shape goes away.
	private static ulong TagMask( b3ShapeId shape ) => (ulong)b3Shape_GetUserData( shape );

	//One table per world, MaxCollisionTags squared bytes, in unmanaged memory: the callbacks hold its address as
	//their context and read it on whichever thread the world steps on, so it can neither move nor be collected.
	private static readonly object rulesLock = new();
	private static readonly Dictionary<ulong, IntPtr> collisionRules = new();

	//The least colliding rule across every tag pair the two shapes share, or Collide when either side is untagged.
	private static byte ResolveRule( byte* table, b3ShapeId a, b3ShapeId b )
		=> CoreCollisionRules.Resolve( table, TagMask( a ), TagMask( b ) );

	[UnmanagedCallersOnly( CallConvs = [typeof( System.Runtime.CompilerServices.CallConvCdecl )] )]
	private static B3Bool OnCustomFilter( b3ShapeId a, b3ShapeId b, void* context )
	{
		return ResolveRule( (byte*)context, a, b ) != Ignore;
	}

	[UnmanagedCallersOnly( CallConvs = [typeof( System.Runtime.CompilerServices.CallConvCdecl )] )]
	private static B3Bool OnPreSolve( b3ShapeId a, b3ShapeId b, b3Vec3 point, b3Vec3 normal, void* context )
	{
		//A trigger pair keeps its contact - that is what reports the touch - but takes no response, so returning false here is the whole of it.
		return ResolveRule( (byte*)context, a, b ) != Trigger;
	}

	/// <summary>Tag-pair rule table, row-major; resolved values only, the least colliding pair wins</summary>
	public static void WorldSetCollisionRules( ulong world, ReadOnlySpan<byte> matrix )
	{
		if ( !ResolveWorld( world, out var id ) ) return;
		if ( matrix.Length < MaxCollisionTags * MaxCollisionTags ) return;

		byte* table;

		lock ( rulesLock )
		{
			if ( !collisionRules.TryGetValue( world, out var existing ) )
			{
				existing = (IntPtr)NativeMemory.AllocZeroed( MaxCollisionTags * MaxCollisionTags );
				collisionRules[world] = existing;
			}

			table = (byte*)existing;
		}

		matrix.Slice( 0, MaxCollisionTags * MaxCollisionTags ).CopyTo( new Span<byte>( table, MaxCollisionTags * MaxCollisionTags ) );

		b3World_SetCustomFilterCallback( id, CustomFilterEntry(), table );
		b3World_SetPreSolveCallback( id, PreSolveEntry(), table );
	}

	public static void WorldClearCollisionRules( ulong world )
	{
		if ( ResolveWorld( world, out var id ) )
		{
			b3World_SetCustomFilterCallback( id, null, null );
			b3World_SetPreSolveCallback( id, null, null );
		}

		ReleaseCollisionRules( world );
	}

	private static void ReleaseCollisionRules( ulong world )
	{
		IntPtr table;

		lock ( rulesLock )
		{
			if ( !collisionRules.Remove( world, out table ) ) return;
		}

		NativeMemory.Free( (void*)table );
	}

	/// <summary>The tags this shape carries, one bit per tag index.</summary>
	public static void ShapeSetTagMask( ulong shape, ulong mask )
	{
		if ( !ResolveShape( shape, out var id ) ) return;
		b3Shape_SetUserData( id, (void*)mask );
	}

	public static ulong ShapeGetTagMask( ulong shape )
	{
		if ( !ResolveShape( shape, out var id ) ) return 0;
		return TagMask( id );
	}

	/// <summary>Let this shape's contacts be vetoed each step, which is how a pair-wise trigger reports a touch without pushing back. Costs solver time, so it is only worth turning on for shapes whose tags appear in a trigger rule.</summary>
	public static void ShapeEnablePresolve( ulong shape, bool enabled )
	{
		if ( !ResolveShape( shape, out var id ) ) return;
		b3Shape_EnablePreSolveEvents( id, enabled );
	}
}
