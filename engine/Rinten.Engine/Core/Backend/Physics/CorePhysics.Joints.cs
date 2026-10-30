using Rinten.Native.Box3D;
using static Rinten.Native.Box3D.Box3D;
using static Rinten.Native.Box3D.Box3DMath;

namespace Rinten;

//Joints and motors; each engine joint kind maps directly to a Box3D one
internal static unsafe partial class CorePhysics
{
	/// <summary>Matches PhysicsJointType. The comment beside each is Box3D's own name for the same constraint.</summary>
	public enum JointType
	{
		Fixed = 0,      //weld
		Spring = 1,     //distance
		Hinge = 2,      //revolute
		Slider = 3,     //prismatic
		BallSocket = 4, //spherical
		Control = 5,    //motor
		Wheel = 6,      //wheel
		Upright = 7,    //parallel
		Filter = 8,     //no constraint, only stops the pair colliding
		Unknown = -1,
	}

	//Fills in the half of a joint definition that every kind shares.
	private static void FillBase( ref b3JointDef def, b3BodyId a, b3BodyId b, in Transform frameA, in Transform frameB )
	{
		def.bodyIdA = a;
		def.bodyIdB = b;
		def.localFrameA = ToBox3D( frameA );
		def.localFrameB = ToBox3D( frameB );

		//Joints constrain bodies that are meant to overlap - a wheel and its chassis - so they must not collide unless something says otherwise.
		def.collideConnected = false;
	}

	public static ulong JointCreate( ulong world, JointType type, ulong bodyA, ulong bodyB,
		in Transform frameA, in Transform frameB )
	{
		if ( !ResolveWorld( world, out var worldId ) ) return 0;
		if ( !ResolveBody( bodyA, out var a ) ) return 0;
		if ( !ResolveBody( bodyB, out var b ) ) return 0;

		switch ( type )
		{
			case JointType.Fixed:
			{
				b3WeldJointDef def = b3DefaultWeldJointDef();
				FillBase( ref def.@base, a, b, frameA, frameB );
				return Pack( b3CreateWeldJoint( worldId, &def ) );
			}

			case JointType.Spring:
			{
				b3DistanceJointDef def = b3DefaultDistanceJointDef();
				FillBase( ref def.@base, a, b, frameA, frameB );
				return Pack( b3CreateDistanceJoint( worldId, &def ) );
			}

			case JointType.Hinge:
			{
				b3RevoluteJointDef def = b3DefaultRevoluteJointDef();
				FillBase( ref def.@base, a, b, frameA, frameB );
				return Pack( b3CreateRevoluteJoint( worldId, &def ) );
			}

			case JointType.Slider:
			{
				b3PrismaticJointDef def = b3DefaultPrismaticJointDef();
				FillBase( ref def.@base, a, b, frameA, frameB );
				return Pack( b3CreatePrismaticJoint( worldId, &def ) );
			}

			case JointType.BallSocket:
			{
				b3SphericalJointDef def = b3DefaultSphericalJointDef();
				FillBase( ref def.@base, a, b, frameA, frameB );
				return Pack( b3CreateSphericalJoint( worldId, &def ) );
			}

			case JointType.Control:
			{
				b3MotorJointDef def = b3DefaultMotorJointDef();
				FillBase( ref def.@base, a, b, frameA, frameB );
				return Pack( b3CreateMotorJoint( worldId, &def ) );
			}

			case JointType.Wheel:
			{
				b3WheelJointDef def = b3DefaultWheelJointDef();
				FillBase( ref def.@base, a, b, frameA, frameB );
				return Pack( b3CreateWheelJoint( worldId, &def ) );
			}

			case JointType.Upright:
			{
				b3ParallelJointDef def = b3DefaultParallelJointDef();
				FillBase( ref def.@base, a, b, frameA, frameB );
				return Pack( b3CreateParallelJoint( worldId, &def ) );
			}

			case JointType.Filter:
			{
				b3FilterJointDef def = b3DefaultFilterJointDef();
				FillBase( ref def.@base, a, b, frameA, frameB );
				return Pack( b3CreateFilterJoint( worldId, &def ) );
			}

			default:
				return 0;
		}
	}

	public static void JointDestroy( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3DestroyJoint( id, true );
	}

	public static bool JointValid( ulong joint ) => ResolveJoint( joint, out _ );

	public static ulong JointGetBodyA( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0;
		return Pack( b3Joint_GetBodyA( id ) );
	}

	public static ulong JointGetBodyB( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0;
		return Pack( b3Joint_GetBodyB( id ) );
	}

	public static Transform JointGetFrameA( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return Transform.Zero;
		return FromBox3D( b3Joint_GetLocalFrameA( id ) );
	}

	public static Transform JointGetFrameB( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return Transform.Zero;
		return FromBox3D( b3Joint_GetLocalFrameB( id ) );
	}

	public static void JointSetFrameA( ulong joint, in Transform frame )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3Joint_SetLocalFrameA( id, ToBox3D( frame ) );
	}

	public static void JointSetFrameB( ulong joint, in Transform frame )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3Joint_SetLocalFrameB( id, ToBox3D( frame ) );
	}

	public static bool JointCollideConnected( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return false;
		return b3Joint_GetCollideConnected( id );
	}

	public static void JointSetCollideConnected( ulong joint, bool enabled )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3Joint_SetCollideConnected( id, enabled );
	}

	public static float JointGetForceThreshold( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;

		//A force is a length-like quantity, same as everywhere else here.
		return b3Joint_GetForceThreshold( id );
	}

	public static void JointSetForceThreshold( ulong joint, float threshold )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3Joint_SetForceThreshold( id, threshold );
	}

	public static float JointGetTorqueThreshold( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3Joint_GetTorqueThreshold( id );
	}

	public static void JointSetTorqueThreshold( ulong joint, float threshold )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3Joint_SetTorqueThreshold( id, threshold );
	}

	public static float JointGetForce( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3Length( b3Joint_GetConstraintForce( id ) );
	}

	public static float JointGetTorque( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3Length( b3Joint_GetConstraintTorque( id ) );
	}

	public static void JointWakeBodies( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3Joint_WakeBodies( id );
	}

	public static JointType JointGetType( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return JointType.Unknown;

		switch ( b3Joint_GetType( id ) )
		{
			case b3JointType.b3_weldJoint: return JointType.Fixed;
			case b3JointType.b3_distanceJoint: return JointType.Spring;
			case b3JointType.b3_revoluteJoint: return JointType.Hinge;
			case b3JointType.b3_prismaticJoint: return JointType.Slider;
			case b3JointType.b3_sphericalJoint: return JointType.BallSocket;
			case b3JointType.b3_motorJoint: return JointType.Control;
			case b3JointType.b3_wheelJoint: return JointType.Wheel;
			case b3JointType.b3_parallelJoint: return JointType.Upright;
			case b3JointType.b3_filterJoint: return JointType.Filter;
			default: return JointType.Unknown;
		}
	}

	//-- limits ------------------------------------------------------------------- One managed property, three constraints underneath.
	//A spring limits its length, a hinge its angle, a slider its translation - so the kind decides which Box3D call to make.

	private static void ReadLimits( b3JointId id, out float lower, out float upper )
	{
		lower = 0.0f;
		upper = 0.0f;

		switch ( b3Joint_GetType( id ) )
		{
			case b3JointType.b3_distanceJoint:
				lower = b3DistanceJoint_GetMinLength( id );
				upper = b3DistanceJoint_GetMaxLength( id );
				break;

			case b3JointType.b3_revoluteJoint:
				lower = b3RevoluteJoint_GetLowerLimit( id );
				upper = b3RevoluteJoint_GetUpperLimit( id );
				break;

			case b3JointType.b3_prismaticJoint:
				lower = b3PrismaticJoint_GetLowerLimit( id );
				upper = b3PrismaticJoint_GetUpperLimit( id );
				break;

			default:
				break;
		}
	}

	private static void WriteLimits( b3JointId id, float lower, float upper )
	{
		switch ( b3Joint_GetType( id ) )
		{
			case b3JointType.b3_distanceJoint:
				b3DistanceJoint_SetLengthRange( id, lower, upper );

				//A range only does anything once the limit is on, and setting one is the only way the engine above says it wants one.
				b3DistanceJoint_EnableLimit( id, true );
				break;

			//An empty range means no limit, not a lock: a hinge or slider component leaves both at zero to say "free", and a motor cannot turn
			//a joint pinned to a zero-degree window. A rope of fixed length is a real thing, so the distance joint above keeps its equal ends.
			case b3JointType.b3_revoluteJoint:
				b3RevoluteJoint_SetLimits( id, lower, upper );
				b3RevoluteJoint_EnableLimit( id, lower < upper );
				break;

			case b3JointType.b3_prismaticJoint:
				b3PrismaticJoint_SetLimits( id, lower, upper );
				b3PrismaticJoint_EnableLimit( id, lower < upper );
				break;

			default:
				break;
		}
	}

	public static void JointSetLowerLimit( ulong joint, float lower )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;

		ReadLimits( id, out _, out float upper );
		WriteLimits( id, lower, upper );
	}

	public static void JointSetUpperLimit( ulong joint, float upper )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;

		ReadLimits( id, out float lower, out _ );
		WriteLimits( id, lower, upper );
	}

	public static float JointGetLowerLimit( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;

		ReadLimits( id, out float lower, out _ );
		return lower;
	}

	public static float JointGetUpperLimit( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;

		ReadLimits( id, out _, out float upper );
		return upper;
	}

	//-- spring force range --------------------------------------------------------

	public static void JointSetMinForce( ulong joint, float lower )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		if ( b3Joint_GetType( id ) != b3JointType.b3_distanceJoint ) return;

		float current, upper;
		b3DistanceJoint_GetSpringForceRange( id, &current, &upper );
		b3DistanceJoint_SetSpringForceRange( id, lower, upper );
	}

	public static void JointSetMaxForce( ulong joint, float upper )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		if ( b3Joint_GetType( id ) != b3JointType.b3_distanceJoint ) return;

		float lower, current;
		b3DistanceJoint_GetSpringForceRange( id, &lower, &current );
		b3DistanceJoint_SetSpringForceRange( id, lower, upper );
	}

	public static float JointGetMinForce( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		if ( b3Joint_GetType( id ) != b3JointType.b3_distanceJoint ) return 0.0f;

		float lower, upper;
		b3DistanceJoint_GetSpringForceRange( id, &lower, &upper );
		return lower;
	}

	public static float JointGetMaxForce( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		if ( b3Joint_GetType( id ) != b3JointType.b3_distanceJoint ) return 0.0f;

		float lower, upper;
		b3DistanceJoint_GetSpringForceRange( id, &lower, &upper );
		return upper;
	}

	//-- springs -------------------------------------------------------------------

	public static void JointSetLinearSpring( ulong joint, float hertz, float damping, float maximum )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;

		switch ( b3Joint_GetType( id ) )
		{
			case b3JointType.b3_distanceJoint:
				b3DistanceJoint_SetSpringHertz( id, hertz );
				b3DistanceJoint_SetSpringDampingRatio( id, damping );
				b3DistanceJoint_EnableSpring( id, hertz > 0.0f );
				break;

			case b3JointType.b3_weldJoint:
				b3WeldJoint_SetLinearHertz( id, hertz );
				b3WeldJoint_SetLinearDampingRatio( id, damping );
				break;

			case b3JointType.b3_prismaticJoint:
				b3PrismaticJoint_SetSpringHertz( id, hertz );
				b3PrismaticJoint_SetSpringDampingRatio( id, damping );
				b3PrismaticJoint_EnableSpring( id, hertz > 0.0f );
				break;

			default:
				break;
		}

		//The cap belongs to the joint as a whole, not to the spring.
		if ( maximum > 0.0f && maximum < 3.0e38f )
			b3Joint_SetForceThreshold( id, maximum );
	}

	public static void JointGetLinearSpring( ulong joint, out float hertz, out float damping, out float maximum )
	{
		hertz = 0.0f;
		damping = 0.0f;
		maximum = 0.0f;

		if ( !ResolveJoint( joint, out var id ) ) return;

		switch ( b3Joint_GetType( id ) )
		{
			case b3JointType.b3_distanceJoint:
				hertz = b3DistanceJoint_GetSpringHertz( id );
				damping = b3DistanceJoint_GetSpringDampingRatio( id );
				break;

			case b3JointType.b3_weldJoint:
				hertz = b3WeldJoint_GetLinearHertz( id );
				damping = b3WeldJoint_GetLinearDampingRatio( id );
				break;

			case b3JointType.b3_prismaticJoint:
				hertz = b3PrismaticJoint_GetSpringHertz( id );
				damping = b3PrismaticJoint_GetSpringDampingRatio( id );
				break;

			default:
				break;
		}

		maximum = b3Joint_GetForceThreshold( id );
	}

	public static void JointSetAngularSpring( ulong joint, float hertz, float damping, float maximum )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;

		switch ( b3Joint_GetType( id ) )
		{
			case b3JointType.b3_weldJoint:
				b3WeldJoint_SetAngularHertz( id, hertz );
				b3WeldJoint_SetAngularDampingRatio( id, damping );
				break;

			case b3JointType.b3_revoluteJoint:
				b3RevoluteJoint_SetSpringHertz( id, hertz );
				b3RevoluteJoint_SetSpringDampingRatio( id, damping );
				b3RevoluteJoint_EnableSpring( id, hertz > 0.0f );
				break;

			case b3JointType.b3_sphericalJoint:
				b3SphericalJoint_SetSpringHertz( id, hertz );
				b3SphericalJoint_SetSpringDampingRatio( id, damping );
				b3SphericalJoint_EnableSpring( id, hertz > 0.0f );
				break;

			default:
				break;
		}

		if ( maximum > 0.0f && maximum < 3.0e38f )
			b3Joint_SetTorqueThreshold( id, maximum );
	}

	public static void JointGetAngularSpring( ulong joint, out float hertz, out float damping, out float maximum )
	{
		hertz = 0.0f;
		damping = 0.0f;
		maximum = 0.0f;

		if ( !ResolveJoint( joint, out var id ) ) return;

		switch ( b3Joint_GetType( id ) )
		{
			case b3JointType.b3_weldJoint:
				hertz = b3WeldJoint_GetAngularHertz( id );
				damping = b3WeldJoint_GetAngularDampingRatio( id );
				break;

			case b3JointType.b3_revoluteJoint:
				hertz = b3RevoluteJoint_GetSpringHertz( id );
				damping = b3RevoluteJoint_GetSpringDampingRatio( id );
				break;

			case b3JointType.b3_sphericalJoint:
				hertz = b3SphericalJoint_GetSpringHertz( id );
				damping = b3SphericalJoint_GetSpringDampingRatio( id );
				break;

			default:
				break;
		}

		maximum = b3Joint_GetTorqueThreshold( id );
	}

	//-- ball socket limits ---------------------------------------------------------

	public static void JointSetSwingLimit( ulong joint, float lower, float upper )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		if ( b3Joint_GetType( id ) != b3JointType.b3_sphericalJoint ) return;

		//Box3D's cone limit is a single half-angle, so the wider of the two ends is the cone that contains both.
		b3SphericalJoint_SetConeLimit( id, b3MaxFloat( b3AbsFloat( lower ), b3AbsFloat( upper ) ) );
	}

	public static void JointEnableSwingLimit( ulong joint, bool enabled )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		if ( b3Joint_GetType( id ) != b3JointType.b3_sphericalJoint ) return;

		b3SphericalJoint_EnableConeLimit( id, enabled );
	}

	public static void JointSetTwistLimit( ulong joint, float lower, float upper )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		if ( b3Joint_GetType( id ) != b3JointType.b3_sphericalJoint ) return;

		b3SphericalJoint_SetTwistLimits( id, lower, upper );
	}

	public static void JointEnableTwistLimit( ulong joint, bool enabled )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		if ( b3Joint_GetType( id ) != b3JointType.b3_sphericalJoint ) return;

		b3SphericalJoint_EnableTwistLimit( id, enabled );
	}

	public static float JointGetAngle( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;

		switch ( b3Joint_GetType( id ) )
		{
			case b3JointType.b3_revoluteJoint: return b3RevoluteJoint_GetAngle( id );
			case b3JointType.b3_sphericalJoint: return b3SphericalJoint_GetTwistAngle( id );
			default: return 0.0f;
		}
	}

	public static void JointEnableRangeLimit( ulong joint, bool enabled )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;

		switch ( b3Joint_GetType( id ) )
		{
			case b3JointType.b3_revoluteJoint: b3RevoluteJoint_EnableLimit( id, enabled ); break;
			case b3JointType.b3_prismaticJoint: b3PrismaticJoint_EnableLimit( id, enabled ); break;
		}
	}

	public static void JointSetLinearMotor( ulong joint, float speed, float maxForce )
	{
		if ( !ResolveJoint( joint, out var id ) || b3Joint_GetType( id ) != b3JointType.b3_prismaticJoint ) return;

		b3PrismaticJoint_EnableSpring( id, false );
		b3PrismaticJoint_SetMotorSpeed( id, speed );
		b3PrismaticJoint_SetMaxMotorForce( id, maxForce );
		b3PrismaticJoint_EnableMotor( id, true );
	}

	public static void JointSetTargetTranslation( ulong joint, float target, float hertz, float damping )
	{
		if ( !ResolveJoint( joint, out var id ) || b3Joint_GetType( id ) != b3JointType.b3_prismaticJoint ) return;

		b3PrismaticJoint_EnableMotor( id, false );
		b3PrismaticJoint_SetTargetTranslation( id, target );
		b3PrismaticJoint_SetSpringHertz( id, hertz );
		b3PrismaticJoint_SetSpringDampingRatio( id, damping );
		b3PrismaticJoint_EnableSpring( id, hertz > 0.0f );
	}

	public static void JointSetFriction( ulong joint, float friction )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;

		//Box3D models joint friction as a motor with no target speed: it resists motion up to the torque or force given and drives nothing.
		switch ( b3Joint_GetType( id ) )
		{
			case b3JointType.b3_revoluteJoint:
				b3RevoluteJoint_SetMotorSpeed( id, 0.0f );
				b3RevoluteJoint_SetMaxMotorTorque( id, friction );
				b3RevoluteJoint_EnableMotor( id, friction > 0.0f );
				break;

			case b3JointType.b3_prismaticJoint:
				b3PrismaticJoint_SetMotorSpeed( id, 0.0f );
				b3PrismaticJoint_SetMaxMotorForce( id, friction );
				b3PrismaticJoint_EnableMotor( id, friction > 0.0f );
				break;

			case b3JointType.b3_sphericalJoint:
				b3SphericalJoint_SetMotorVelocity( id, b3Vec3_zero );
				b3SphericalJoint_SetMaxMotorTorque( id, friction );
				b3SphericalJoint_EnableMotor( id, friction > 0.0f );
				break;

			default:
				break;
		}
	}

	//-- motors -- driven towards a pose with a spring, or at a speed with a torque cap

	public static void JointSetTargetAngle( ulong joint, float radians, float hertz, float damping )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		if ( b3Joint_GetType( id ) != b3JointType.b3_revoluteJoint ) return;

		b3RevoluteJoint_SetTargetAngle( id, radians );
		b3RevoluteJoint_SetSpringHertz( id, hertz );
		b3RevoluteJoint_SetSpringDampingRatio( id, damping );

		//A spring at zero hertz is infinitely soft, which is to say off. Leaving it enabled would only cost solver time and hold nothing.
		b3RevoluteJoint_EnableSpring( id, hertz > 0.0f );

		//The angle is what drives the hinge now, so the speed motor must let go of it.
		//Two things pulling the same degree of freedom fight, and the spring loses.
		b3RevoluteJoint_EnableMotor( id, false );
	}

	public static void JointSetAngularMotor( ulong joint, float radiansPerSecond, float maxTorque )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		if ( b3Joint_GetType( id ) != b3JointType.b3_revoluteJoint ) return;

		b3RevoluteJoint_SetMotorSpeed( id, radiansPerSecond );
		b3RevoluteJoint_SetMaxMotorTorque( id, maxTorque );
		b3RevoluteJoint_EnableMotor( id, true );

		b3RevoluteJoint_EnableSpring( id, false );
	}

	public static void JointSetMotorVelocity( ulong joint, Vector3 velocity, float maxTorque )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		if ( b3Joint_GetType( id ) != b3JointType.b3_sphericalJoint ) return;

		b3SphericalJoint_SetMotorVelocity( id, ToBox3D( velocity ) );
		b3SphericalJoint_SetMaxMotorTorque( id, maxTorque );
		b3SphericalJoint_EnableMotor( id, true );

		b3SphericalJoint_EnableSpring( id, false );
	}

	public static void JointSetTargetRotation( ulong joint, Rotation rotation, float hertz, float damping )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		if ( b3Joint_GetType( id ) != b3JointType.b3_sphericalJoint ) return;

		b3SphericalJoint_SetTargetRotation( id, ToBox3D( rotation ) );
		b3SphericalJoint_SetSpringHertz( id, hertz );
		b3SphericalJoint_SetSpringDampingRatio( id, damping );
		b3SphericalJoint_EnableSpring( id, hertz > 0.0f );

		b3SphericalJoint_EnableMotor( id, false );
	}

	//-- wheels ---------------------------------------------------------------------

	public static void JointWheelEnableSuspension( ulong joint, bool enabled )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3WheelJoint_EnableSuspension( id, enabled );
	}

	public static bool JointWheelSuspensionEnabled( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return false;
		return b3WheelJoint_IsSuspensionEnabled( id );
	}

	public static void JointWheelSetSuspensionHertz( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3WheelJoint_SetSuspensionHertz( id, value );
	}

	public static float JointWheelGetSuspensionHertz( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3WheelJoint_GetSuspensionHertz( id );
	}

	public static void JointWheelSetSuspensionDamping( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3WheelJoint_SetSuspensionDampingRatio( id, value );
	}

	public static float JointWheelGetSuspensionDamping( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3WheelJoint_GetSuspensionDampingRatio( id );
	}

	public static void JointWheelEnableSuspensionLimit( ulong joint, bool enabled )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3WheelJoint_EnableSuspensionLimit( id, enabled );
	}

	public static bool JointWheelSuspensionLimitEnabled( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return false;
		return b3WheelJoint_IsSuspensionLimitEnabled( id );
	}

	public static float JointWheelGetLowerSuspensionLimit( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3WheelJoint_GetLowerSuspensionLimit( id );
	}

	public static float JointWheelGetUpperSuspensionLimit( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3WheelJoint_GetUpperSuspensionLimit( id );
	}

	public static void JointWheelSetSuspensionLimits( ulong joint, float lower, float upper )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3WheelJoint_SetSuspensionLimits( id, lower, upper );
	}

	public static void JointWheelEnableSpinMotor( ulong joint, bool enabled )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3WheelJoint_EnableSpinMotor( id, enabled );
	}

	public static bool JointWheelSpinMotorEnabled( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return false;
		return b3WheelJoint_IsSpinMotorEnabled( id );
	}

	public static void JointWheelSetSpinMotorSpeed( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3WheelJoint_SetSpinMotorSpeed( id, value );
	}

	public static float JointWheelGetSpinMotorSpeed( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3WheelJoint_GetSpinMotorSpeed( id );
	}

	public static void JointWheelSetMaxSpinTorque( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3WheelJoint_SetMaxSpinTorque( id, value );
	}

	public static float JointWheelGetMaxSpinTorque( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3WheelJoint_GetMaxSpinTorque( id );
	}

	public static void JointWheelEnableSteering( ulong joint, bool enabled )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3WheelJoint_EnableSteering( id, enabled );
	}

	public static bool JointWheelSteeringEnabled( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return false;
		return b3WheelJoint_IsSteeringEnabled( id );
	}

	public static void JointWheelSetSteeringHertz( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3WheelJoint_SetSteeringHertz( id, value );
	}

	public static float JointWheelGetSteeringHertz( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3WheelJoint_GetSteeringHertz( id );
	}

	public static void JointWheelSetSteeringDamping( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3WheelJoint_SetSteeringDampingRatio( id, value );
	}

	public static float JointWheelGetSteeringDamping( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3WheelJoint_GetSteeringDampingRatio( id );
	}

	public static void JointWheelSetMaxSteeringTorque( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3WheelJoint_SetMaxSteeringTorque( id, value );
	}

	public static float JointWheelGetMaxSteeringTorque( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3WheelJoint_GetMaxSteeringTorque( id );
	}

	public static void JointWheelEnableSteeringLimit( ulong joint, bool enabled )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3WheelJoint_EnableSteeringLimit( id, enabled );
	}

	public static bool JointWheelSteeringLimitEnabled( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return false;
		return b3WheelJoint_IsSteeringLimitEnabled( id );
	}

	public static float JointWheelGetLowerSteeringLimit( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3WheelJoint_GetLowerSteeringLimit( id );
	}

	public static float JointWheelGetUpperSteeringLimit( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3WheelJoint_GetUpperSteeringLimit( id );
	}

	public static void JointWheelSetSteeringLimits( ulong joint, float lower, float upper )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3WheelJoint_SetSteeringLimits( id, lower, upper );
	}

	public static void JointWheelSetTargetSteeringAngle( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3WheelJoint_SetTargetSteeringAngle( id, value );
	}

	public static float JointWheelGetTargetSteeringAngle( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3WheelJoint_GetTargetSteeringAngle( id );
	}

	public static float JointWheelGetSpinSpeed( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3WheelJoint_GetSpinSpeed( id );
	}

	public static float JointWheelGetSpinTorque( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3WheelJoint_GetSpinTorque( id );
	}

	public static float JointWheelGetSteeringAngle( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3WheelJoint_GetSteeringAngle( id );
	}

	public static float JointWheelGetSteeringTorque( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3WheelJoint_GetSteeringTorque( id );
	}

	//-- the motor joint --------------------------------------------------------------

	public static void JointMotorSetLinearVelocity( ulong joint, Vector3 velocity )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3MotorJoint_SetLinearVelocity( id, ToBox3D( velocity ) );
	}

	public static Vector3 JointMotorGetLinearVelocity( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return Vector3.Zero;
		return FromBox3D( b3MotorJoint_GetLinearVelocity( id ) );
	}

	public static void JointMotorSetAngularVelocity( ulong joint, Vector3 velocity )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3MotorJoint_SetAngularVelocity( id, ToBox3D( velocity ) );
	}

	public static Vector3 JointMotorGetAngularVelocity( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return Vector3.Zero;
		return FromBox3D( b3MotorJoint_GetAngularVelocity( id ) );
	}

	public static void JointMotorSetMaxVelocityForce( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3MotorJoint_SetMaxVelocityForce( id, value );
	}

	public static float JointMotorGetMaxVelocityForce( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3MotorJoint_GetMaxVelocityForce( id );
	}

	public static void JointMotorSetMaxVelocityTorque( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3MotorJoint_SetMaxVelocityTorque( id, value );
	}

	public static float JointMotorGetMaxVelocityTorque( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3MotorJoint_GetMaxVelocityTorque( id );
	}

	public static void JointMotorSetLinearHertz( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3MotorJoint_SetLinearHertz( id, value );
	}

	public static float JointMotorGetLinearHertz( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3MotorJoint_GetLinearHertz( id );
	}

	public static void JointMotorSetLinearDamping( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3MotorJoint_SetLinearDampingRatio( id, value );
	}

	public static float JointMotorGetLinearDamping( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3MotorJoint_GetLinearDampingRatio( id );
	}

	public static void JointMotorSetAngularHertz( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3MotorJoint_SetAngularHertz( id, value );
	}

	public static float JointMotorGetAngularHertz( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3MotorJoint_GetAngularHertz( id );
	}

	public static void JointMotorSetAngularDamping( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3MotorJoint_SetAngularDampingRatio( id, value );
	}

	public static float JointMotorGetAngularDamping( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3MotorJoint_GetAngularDampingRatio( id );
	}

	public static void JointMotorSetMaxSpringForce( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3MotorJoint_SetMaxSpringForce( id, value );
	}

	public static float JointMotorGetMaxSpringForce( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3MotorJoint_GetMaxSpringForce( id );
	}

	public static void JointMotorSetMaxSpringTorque( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3MotorJoint_SetMaxSpringTorque( id, value );
	}

	public static float JointMotorGetMaxSpringTorque( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3MotorJoint_GetMaxSpringTorque( id );
	}

	//-- the parallel joint -----------------------------------------------------------

	public static void JointParallelSetHertz( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3ParallelJoint_SetSpringHertz( id, value );
	}

	public static float JointParallelGetHertz( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3ParallelJoint_GetSpringHertz( id );
	}

	public static void JointParallelSetDamping( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3ParallelJoint_SetSpringDampingRatio( id, value );
	}

	public static float JointParallelGetDamping( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3ParallelJoint_GetSpringDampingRatio( id );
	}

	public static void JointParallelSetMaxTorque( ulong joint, float value )
	{
		if ( !ResolveJoint( joint, out var id ) ) return;
		b3ParallelJoint_SetMaxTorque( id, value );
	}

	public static float JointParallelGetMaxTorque( ulong joint )
	{
		if ( !ResolveJoint( joint, out var id ) ) return 0.0f;
		return b3ParallelJoint_GetMaxTorque( id );
	}
}
