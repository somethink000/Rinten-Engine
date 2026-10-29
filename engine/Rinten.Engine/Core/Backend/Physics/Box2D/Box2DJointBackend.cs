using Rinten.Native.Box2D;
using static Rinten.Native.Box2D.Box2D;
using Kind = Rinten.Box2DWorldBackend.JointKind;

namespace Rinten;

//A joint in the plane, over Box2D. The controls line up with the Box3D joint's, less the ones that need a third dimension: a ball
//socket turns only about Z, so its swing and twist limits are the hinge's limits, and a steering wheel has nowhere to steer.
internal sealed unsafe class Box2DJointBackend : IPhysicsJointBackend
{
	private readonly PhysicsWorld world;
	private readonly Kind kind;
	private b2JointId id;

	public Box2DJointBackend( b2JointId id, Kind kind, PhysicsWorld world, PhysicsBody body1, PhysicsBody body2 )
	{
		this.id = id;
		this.kind = kind;
		this.world = world;
		Body1 = body1;
		Body2 = body2;
	}

	internal PhysicsBody Body1 { get; }
	internal PhysicsBody Body2 { get; }

	public bool IsValid => b2Joint_IsValid( id );

	internal void DestroyNative()
	{
		if ( IsValid ) b2DestroyJoint( id );
		Forget();
	}

	//Drop the id without touching Box2D, for when a body took the joint with it.
	internal void Forget()
	{
		Box2DRegistry.Joints.TryRemove( Box2DPlane.Key( id ), out _ );
		id = default;

		(Body1?.Backend as Box2DBodyBackend)?.Joints.Remove( this );
		(Body2?.Backend as Box2DBodyBackend)?.Joints.Remove( this );
	}

	private b2JointType Type => b2Joint_GetType( id );

	public PhysicsWorld GetWorld() => world;
	public PhysicsBody GetBody1() => Body1;
	public PhysicsBody GetBody2() => Body2;

	//-- frames ---------------------------------------------------------------

	public void GetLocalFrameA( out Vector3 position, out Rotation rotation )
		=> ReadFrame( Body1, IsValid ? b2Joint_GetLocalFrameA( id ) : default, out position, out rotation );

	public void GetLocalFrameB( out Vector3 position, out Rotation rotation )
		=> ReadFrame( Body2, IsValid ? b2Joint_GetLocalFrameB( id ) : default, out position, out rotation );

	private static void ReadFrame( PhysicsBody body, b2Transform frame, out Vector3 position, out Rotation rotation )
	{
		if ( body?.Backend is Box2DBodyBackend b )
		{
			var t = b.FromLocalFrame( frame );
			position = t.Position;
			rotation = t.Rotation;
			return;
		}

		position = Box2DPlane.Point( frame.p );
		rotation = Box2DPlane.AboutZ( Box2DPlane.Angle( frame.q ) );
	}

	public void SetLocalFrameA( Vector3 position, Rotation rotation )
	{
		if ( IsValid && Body1?.Backend is Box2DBodyBackend b )
			b2Joint_SetLocalFrameA( id, b.LocalFrame( new Transform( position, rotation ) ) );
	}

	public void SetLocalFrameB( Vector3 position, Rotation rotation )
	{
		if ( IsValid && Body2?.Backend is Box2DBodyBackend b )
			b2Joint_SetLocalFrameB( id, b.LocalFrame( new Transform( position, rotation ) ) );
	}

	//-- shared ---------------------------------------------------------------

	public bool IsCollisionEnabled() => IsValid && b2Joint_GetCollideConnected( id );
	public void SetEnableCollision( bool enabled ) { if ( IsValid ) b2Joint_SetCollideConnected( id, enabled ); }

	public PhysicsJointType GetJointType() => kind switch
	{
		Kind.Fixed => PhysicsJointType.WELD_JOINT,
		Kind.Spring => PhysicsJointType.SPRING,
		Kind.Hinge => PhysicsJointType.REVOLUTE_JOINT,
		Kind.Slider => PhysicsJointType.PRISMATIC_JOINT,
		Kind.BallSocket => PhysicsJointType.SPHERICAL_JOINT,
		Kind.Control or Kind.Wheel or Kind.Upright => PhysicsJointType.GENERIC_JOINT,
		_ => PhysicsJointType.NULL_JOINT,
	};

	/// <summary>Box2D breaks a joint on a force, not an accumulated impulse - the same idea per second.</summary>
	public float GetMaxLinearImpulse() => IsValid ? b2Joint_GetForceThreshold( id ) : 0;
	public void SetMaxLinearImpulse( float value ) { if ( IsValid ) b2Joint_SetForceThreshold( id, value > 0 ? value : float.MaxValue ); }

	public float GetMaxAngularImpulse() => IsValid ? b2Joint_GetTorqueThreshold( id ) : 0;
	public void SetMaxAngularImpulse( float value ) { if ( IsValid ) b2Joint_SetTorqueThreshold( id, value > 0 ? value : float.MaxValue ); }

	public float GetLinearImpulse()
	{
		if ( !IsValid ) return 0;
		var force = b2Joint_GetConstraintForce( id );
		return MathF.Sqrt( force.x * force.x + force.y * force.y );
	}

	public float GetAngularImpulse() => IsValid ? MathF.Abs( b2Joint_GetConstraintTorque( id ) ) : 0;

	//-- limits ----------------------------------------------------------------- One managed
	//property, three constraints: a spring's length, a hinge's angle, a slider's or a wheel's travel.

	private void ReadLimits( out float lower, out float upper )
	{
		lower = upper = 0;
		if ( !IsValid ) return;

		switch ( Type )
		{
			case b2JointType.b2_distanceJoint: lower = b2DistanceJoint_GetMinLength( id ); upper = b2DistanceJoint_GetMaxLength( id ); break;
			case b2JointType.b2_revoluteJoint: lower = b2RevoluteJoint_GetLowerLimit( id ); upper = b2RevoluteJoint_GetUpperLimit( id ); break;
			case b2JointType.b2_prismaticJoint: lower = b2PrismaticJoint_GetLowerLimit( id ); upper = b2PrismaticJoint_GetUpperLimit( id ); break;
		}
	}

	private void WriteLimits( float lower, float upper )
	{
		if ( !IsValid ) return;

		switch ( Type )
		{
			case b2JointType.b2_distanceJoint:
				b2DistanceJoint_SetLengthRange( id, lower, upper );
				b2DistanceJoint_EnableLimit( id, true );
				break;

			//An empty range is "free", not "locked" - see the Box3D joint.
			case b2JointType.b2_revoluteJoint:
				b2RevoluteJoint_SetLimits( id, lower, upper );
				b2RevoluteJoint_EnableLimit( id, lower < upper );
				break;

			case b2JointType.b2_prismaticJoint:
				b2PrismaticJoint_SetLimits( id, lower, upper );
				b2PrismaticJoint_EnableLimit( id, lower < upper );
				break;
		}
	}

	public void SetMinLength( float f ) { ReadLimits( out _, out var upper ); WriteLimits( f, upper ); }
	public float GetMinLength() { ReadLimits( out var lower, out _ ); return lower; }
	public void SetMaxLength( float f ) { ReadLimits( out var lower, out _ ); WriteLimits( lower, f ); }
	public float GetMaxLength() { ReadLimits( out _, out var upper ); return upper; }

	public void SetMinForce( float f )
	{
		if ( !IsValid || Type != b2JointType.b2_distanceJoint ) return;
		float lower, upper;
		b2DistanceJoint_GetSpringForceRange( id, &lower, &upper );
		b2DistanceJoint_SetSpringForceRange( id, f, upper );
	}

	public float GetMinForce()
	{
		if ( !IsValid || Type != b2JointType.b2_distanceJoint ) return 0;
		float lower, upper;
		b2DistanceJoint_GetSpringForceRange( id, &lower, &upper );
		return lower;
	}

	public void SetMaxForce( float f )
	{
		if ( !IsValid || Type != b2JointType.b2_distanceJoint ) return;
		float lower, upper;
		b2DistanceJoint_GetSpringForceRange( id, &lower, &upper );
		b2DistanceJoint_SetSpringForceRange( id, lower, f );
	}

	public float GetMaxForce()
	{
		if ( !IsValid || Type != b2JointType.b2_distanceJoint ) return 0;
		float lower, upper;
		b2DistanceJoint_GetSpringForceRange( id, &lower, &upper );
		return upper;
	}

	//-- springs ----------------------------------------------------------------

	public void SetLinearSpring( Vector3 vec )
	{
		if ( !IsValid ) return;

		switch ( Type )
		{
			case b2JointType.b2_distanceJoint:
				b2DistanceJoint_SetSpringHertz( id, vec.x );
				b2DistanceJoint_SetSpringDampingRatio( id, vec.y );
				b2DistanceJoint_EnableSpring( id, vec.x > 0.0f );
				break;

			case b2JointType.b2_weldJoint:
				b2WeldJoint_SetLinearHertz( id, vec.x );
				b2WeldJoint_SetLinearDampingRatio( id, vec.y );
				break;

			case b2JointType.b2_prismaticJoint:
				b2PrismaticJoint_SetSpringHertz( id, vec.x );
				b2PrismaticJoint_SetSpringDampingRatio( id, vec.y );
				b2PrismaticJoint_EnableSpring( id, vec.x > 0.0f );
				break;
		}

		if ( vec.z > 0.0f && vec.z < 3.0e38f )
			b2Joint_SetForceThreshold( id, vec.z );
	}

	public Vector3 GetLinearSpring()
	{
		if ( !IsValid ) return default;

		return Type switch
		{
			b2JointType.b2_distanceJoint => new Vector3( b2DistanceJoint_GetSpringHertz( id ), b2DistanceJoint_GetSpringDampingRatio( id ), b2Joint_GetForceThreshold( id ) ),
			b2JointType.b2_weldJoint => new Vector3( b2WeldJoint_GetLinearHertz( id ), b2WeldJoint_GetLinearDampingRatio( id ), b2Joint_GetForceThreshold( id ) ),
			b2JointType.b2_prismaticJoint => new Vector3( b2PrismaticJoint_GetSpringHertz( id ), b2PrismaticJoint_GetSpringDampingRatio( id ), b2Joint_GetForceThreshold( id ) ),
			_ => new Vector3( 0, 0, b2Joint_GetForceThreshold( id ) ),
		};
	}

	/// <summary>A hinge reads these as target angle, hertz and damping - the Box3D joint's reading.</summary>
	public void SetAngularSpring( Vector3 vec )
	{
		if ( !IsValid ) return;

		switch ( Type )
		{
			case b2JointType.b2_revoluteJoint:
				b2RevoluteJoint_SetTargetAngle( id, vec.x );
				b2RevoluteJoint_SetSpringHertz( id, vec.y );
				b2RevoluteJoint_SetSpringDampingRatio( id, vec.z );
				b2RevoluteJoint_EnableSpring( id, vec.y > 0.0f );
				b2RevoluteJoint_EnableMotor( id, false );
				break;

			case b2JointType.b2_weldJoint:
				b2WeldJoint_SetAngularHertz( id, vec.x );
				b2WeldJoint_SetAngularDampingRatio( id, vec.y );
				if ( vec.z > 0.0f && vec.z < 3.0e38f ) b2Joint_SetTorqueThreshold( id, vec.z );
				break;
		}
	}

	public Vector3 GetAngularSpring()
	{
		if ( !IsValid ) return default;

		return Type switch
		{
			b2JointType.b2_weldJoint => new Vector3( b2WeldJoint_GetAngularHertz( id ), b2WeldJoint_GetAngularDampingRatio( id ), b2Joint_GetTorqueThreshold( id ) ),
			b2JointType.b2_revoluteJoint => new Vector3( b2RevoluteJoint_GetSpringHertz( id ), b2RevoluteJoint_GetSpringDampingRatio( id ), b2Joint_GetTorqueThreshold( id ) ),
			_ => new Vector3( 0, 0, b2Joint_GetTorqueThreshold( id ) ),
		};
	}

	//-- motors -----------------------------------------------------------------

	public void SetAngularMotor( float targetVelocity, float maxTorque )
	{
		if ( !IsValid || Type != b2JointType.b2_revoluteJoint ) return;

		b2RevoluteJoint_SetMotorSpeed( id, targetVelocity );
		b2RevoluteJoint_SetMaxMotorTorque( id, maxTorque );
		b2RevoluteJoint_EnableMotor( id, true );
		b2RevoluteJoint_EnableSpring( id, false );
	}

	public void SetRangeLimitEnabled( bool enabled )
	{
		if ( !IsValid ) return;

		if ( Type == b2JointType.b2_revoluteJoint ) b2RevoluteJoint_EnableLimit( id, enabled );
		else if ( Type == b2JointType.b2_prismaticJoint ) b2PrismaticJoint_EnableLimit( id, enabled );
	}

	public void SetLinearMotor( float velocity, float maxForce )
	{
		if ( !IsValid || Type != b2JointType.b2_prismaticJoint ) return;

		b2PrismaticJoint_EnableSpring( id, false );
		b2PrismaticJoint_SetMotorSpeed( id, velocity );
		b2PrismaticJoint_SetMaxMotorForce( id, maxForce );
		b2PrismaticJoint_EnableMotor( id, true );
	}

	public void SetTargetTranslation( float position, float hertz, float damping )
	{
		if ( !IsValid || Type != b2JointType.b2_prismaticJoint ) return;

		b2PrismaticJoint_EnableMotor( id, false );
		b2PrismaticJoint_SetTargetTranslation( id, position );
		b2PrismaticJoint_SetSpringHertz( id, hertz );
		b2PrismaticJoint_SetSpringDampingRatio( id, damping );
		b2PrismaticJoint_EnableSpring( id, hertz > 0.0f );
	}

	/// <summary>Resistance to turning or sliding: a motor that wants no speed.</summary>
	public void SetFriction( float f )
	{
		if ( !IsValid ) return;

		switch ( Type )
		{
			case b2JointType.b2_revoluteJoint:
				b2RevoluteJoint_SetMotorSpeed( id, 0.0f );
				b2RevoluteJoint_SetMaxMotorTorque( id, f );
				b2RevoluteJoint_EnableMotor( id, f > 0.0f );
				break;

			case b2JointType.b2_prismaticJoint:
				b2PrismaticJoint_SetMotorSpeed( id, 0.0f );
				b2PrismaticJoint_SetMaxMotorForce( id, f );
				b2PrismaticJoint_EnableMotor( id, f > 0.0f );
				break;
		}
	}

	/// <summary>A ball socket's swing and twist, in degrees. In the plane both are the one turn there is. Twist says which way as well as how far, so it is the hinge's limit when there is one; a swing, being a cone, limits both ways by its wider end.</summary>
	public void SetLimit( string name, Vector3 limit )
	{
		if ( !IsValid || Type != b2JointType.b2_revoluteJoint ) return;

		var lower = limit.x.DegreeToRadian();
		var upper = limit.y.DegreeToRadian();

		if ( name == "twist" )
		{
			twistSet = true;
			b2RevoluteJoint_SetLimits( id, MathF.Min( lower, upper ), MathF.Max( lower, upper ) );
		}
		else if ( name == "swing" && !twistSet )
		{
			var cone = MathF.Max( MathF.Abs( lower ), MathF.Abs( upper ) );
			b2RevoluteJoint_SetLimits( id, -cone, cone );
		}
	}

	private bool twistSet, swingEnabled, twistEnabled;

	public void SetLimitEnabled( string name, bool state )
	{
		if ( !IsValid || Type != b2JointType.b2_revoluteJoint ) return;

		if ( name == "twist" ) twistEnabled = state;
		else if ( name == "swing" ) swingEnabled = state;

		b2RevoluteJoint_EnableLimit( id, twistEnabled || swingEnabled );
	}

	public float GetAngle() => IsValid && Type == b2JointType.b2_revoluteJoint ? b2RevoluteJoint_GetAngle( id ) : 0;

	/// <summary>A ball socket driven at a speed: the turn about Z is the only one it can make.</summary>
	public void SetMotorVelocity( Vector3 velocity, float maxTorque ) => SetAngularMotor( velocity.z, maxTorque );

	/// <summary>A ball socket held at a rotation: the turn of it about Z, on a spring.</summary>
	public void SetTargetRotation( Rotation rotation, float hertz, float damping )
		=> SetAngularSpring( new Vector3( Box2DPlane.AngleOf( rotation ), hertz, damping ) );

	private bool IsMotor => IsValid && Type == b2JointType.b2_motorJoint;
	private bool IsControl => IsMotor && kind == Kind.Control;
	private bool IsWheel => IsValid && Type == b2JointType.b2_wheelJoint;
	private bool IsUpright => IsMotor && kind == Kind.Upright;

	public void Motor_SetLinearVelocity( Vector3 velocity ) { if ( IsControl ) b2MotorJoint_SetLinearVelocity( id, Box2DPlane.Vec( velocity ) ); }
	public void Motor_SetAngularVelocity( Vector3 velocity ) { if ( IsControl ) b2MotorJoint_SetAngularVelocity( id, velocity.z ); }
	public void Motor_SetMaxVelocityForce( float maxForce ) { if ( IsControl ) b2MotorJoint_SetMaxVelocityForce( id, maxForce ); }
	public void Motor_SetMaxVelocityTorque( float maxTorque ) { if ( IsControl ) b2MotorJoint_SetMaxVelocityTorque( id, maxTorque ); }
	public void Motor_SetLinearHertz( float hertz ) { if ( IsControl ) b2MotorJoint_SetLinearHertz( id, hertz ); }
	public void Motor_SetLinearDampingRatio( float damping ) { if ( IsControl ) b2MotorJoint_SetLinearDampingRatio( id, damping ); }
	public void Motor_SetAngularHertz( float hertz ) { if ( IsControl ) b2MotorJoint_SetAngularHertz( id, hertz ); }
	public void Motor_SetAngularDampingRatio( float damping ) { if ( IsControl ) b2MotorJoint_SetAngularDampingRatio( id, damping ); }
	public void Motor_SetMaxSpringForce( float maxForce ) { if ( IsControl ) b2MotorJoint_SetMaxSpringForce( id, maxForce ); }
	public void Motor_SetMaxSpringTorque( float maxTorque ) { if ( IsControl ) b2MotorJoint_SetMaxSpringTorque( id, maxTorque ); }
	public Vector3 Motor_GetLinearVelocity() => IsControl ? Box2DPlane.Point( b2MotorJoint_GetLinearVelocity( id ) ) : default;
	public Vector3 Motor_GetAngularVelocity() => IsControl ? new Vector3( 0, 0, b2MotorJoint_GetAngularVelocity( id ) ) : default;
	public float Motor_GetMaxVelocityForce() => IsControl ? b2MotorJoint_GetMaxVelocityForce( id ) : 0;
	public float Motor_GetMaxVelocityTorque() => IsControl ? b2MotorJoint_GetMaxVelocityTorque( id ) : 0;
	public float Motor_GetLinearHertz() => IsControl ? b2MotorJoint_GetLinearHertz( id ) : 0;
	public float Motor_GetLinearDampingRatio() => IsControl ? b2MotorJoint_GetLinearDampingRatio( id ) : 0;
	public float Motor_GetAngularHertz() => IsControl ? b2MotorJoint_GetAngularHertz( id ) : 0;
	public float Motor_GetAngularDampingRatio() => IsControl ? b2MotorJoint_GetAngularDampingRatio( id ) : 0;
	public float Motor_GetMaxSpringForce() => IsControl ? b2MotorJoint_GetMaxSpringForce( id ) : 0;
	public float Motor_GetMaxSpringTorque() => IsControl ? b2MotorJoint_GetMaxSpringTorque( id ) : 0;

	public void Wheel_EnableSuspension( bool flag ) { if ( IsWheel ) b2WheelJoint_EnableSpring( id, flag ); }
	public bool Wheel_IsSuspensionEnabled() => IsWheel && b2WheelJoint_IsSpringEnabled( id );
	public void Wheel_SetSuspensionHertz( float hertz ) { if ( IsWheel ) b2WheelJoint_SetSpringHertz( id, hertz ); }
	public float Wheel_GetSuspensionHertz() => IsWheel ? b2WheelJoint_GetSpringHertz( id ) : 0;
	public void Wheel_SetSuspensionDampingRatio( float dampingRatio ) { if ( IsWheel ) b2WheelJoint_SetSpringDampingRatio( id, dampingRatio ); }
	public float Wheel_GetSuspensionDampingRatio() => IsWheel ? b2WheelJoint_GetSpringDampingRatio( id ) : 0;
	public void Wheel_EnableSuspensionLimit( bool flag ) { if ( IsWheel ) b2WheelJoint_EnableLimit( id, flag ); }
	public bool Wheel_IsSuspensionLimitEnabled() => IsWheel && b2WheelJoint_IsLimitEnabled( id );
	public float Wheel_GetLowerSuspensionLimit() => IsWheel ? b2WheelJoint_GetLowerLimit( id ) : 0;
	public float Wheel_GetUpperSuspensionLimit() => IsWheel ? b2WheelJoint_GetUpperLimit( id ) : 0;
	public void Wheel_SetSuspensionLimits( float lower, float upper ) { if ( IsWheel ) b2WheelJoint_SetLimits( id, lower, upper ); }
	public void Wheel_EnableSpinMotor( bool flag ) { if ( IsWheel ) b2WheelJoint_EnableMotor( id, flag ); }
	public bool Wheel_IsSpinMotorEnabled() => IsWheel && b2WheelJoint_IsMotorEnabled( id );
	public void Wheel_SetSpinMotorSpeed( float speed ) { if ( IsWheel ) b2WheelJoint_SetMotorSpeed( id, speed ); }
	public float Wheel_GetSpinMotorSpeed() => IsWheel ? b2WheelJoint_GetMotorSpeed( id ) : 0;
	public void Wheel_SetMaxSpinTorque( float torque ) { if ( IsWheel ) b2WheelJoint_SetMaxMotorTorque( id, torque ); }
	public float Wheel_GetMaxSpinTorque() => IsWheel ? b2WheelJoint_GetMaxMotorTorque( id ) : 0;

	//A wheel in the plane cannot steer: there is no axis out of it to turn about.
	public void Wheel_EnableSteering( bool flag ) { }
	public bool Wheel_IsSteeringEnabled() => false;
	public void Wheel_SetSteeringHertz( float hertz ) { }
	public float Wheel_GetSteeringHertz() => 0;
	public void Wheel_SetSteeringDampingRatio( float dampingRatio ) { }
	public float Wheel_GetSteeringDampingRatio() => 0;
	public void Wheel_SetMaxSteeringTorque( float torque ) { }
	public float Wheel_GetMaxSteeringTorque() => 0;
	public void Wheel_EnableSteeringLimit( bool flag ) { }
	public bool Wheel_IsSteeringLimitEnabled() => false;
	public float Wheel_GetLowerSteeringLimit() => 0;
	public float Wheel_GetUpperSteeringLimit() => 0;
	public void Wheel_SetSteeringLimits( float lowerRadians, float upperRadians ) { }
	public void Wheel_SetTargetSteeringAngle( float radians ) { }
	public float Wheel_GetTargetSteeringAngle() => 0;
	public float Wheel_GetSteeringAngle() => 0;
	public float Wheel_GetSteeringTorque() => 0;

	/// <summary>How fast the wheel turns relative to its axle body, in radians a second.</summary>
	public float Wheel_GetSpinSpeed()
	{
		if ( !IsWheel ) return 0;

		var a = b2Joint_GetBodyA( id );
		var b = b2Joint_GetBodyB( id );
		return b2Body_GetAngularVelocity( b ) - b2Body_GetAngularVelocity( a );
	}

	public float Wheel_GetSpinTorque() => IsWheel ? b2WheelJoint_GetMotorTorque( id ) : 0;

	//The upright joint is a motor joint holding only the angle.
	public void Parallel_SetHertz( float hertz ) { if ( IsUpright ) b2MotorJoint_SetAngularHertz( id, hertz ); }
	public float Parallel_GetHertz() => IsUpright ? b2MotorJoint_GetAngularHertz( id ) : 0;
	public void Parallel_SetDampingRatio( float dampingRatio ) { if ( IsUpright ) b2MotorJoint_SetAngularDampingRatio( id, dampingRatio ); }
	public float Parallel_GetDampingRatio() => IsUpright ? b2MotorJoint_GetAngularDampingRatio( id ) : 0;
	public void Parallel_SetMaxTorque( float maxTorque ) { if ( IsUpright ) b2MotorJoint_SetMaxSpringTorque( id, maxTorque ); }
	public float Parallel_GetMaxTorque() => IsUpright ? b2MotorJoint_GetMaxSpringTorque( id ) : 0;
}
