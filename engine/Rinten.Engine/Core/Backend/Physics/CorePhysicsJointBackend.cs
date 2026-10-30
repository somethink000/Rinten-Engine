namespace Rinten;

//A joint on our core over Box3D, which has a direct equivalent for every engine joint kind
internal sealed unsafe class CorePhysicsJointBackend : IPhysicsJointBackend
{
	private readonly PhysicsWorld world;
	private ulong joint;

	public CorePhysicsJointBackend( ulong joint, PhysicsWorld world )
	{
		this.joint = joint;
		this.world = world;
	}

	internal ulong Handle => joint;

	public bool IsValid => joint != 0 && CorePhysics.JointValid( joint );

	internal void DestroyNative()
	{
		if ( joint == 0 ) return;

		CorePhysics.JointDestroy( joint );
		joint = 0;
	}

	public PhysicsWorld GetWorld() => world;

	public PhysicsBody GetBody1() => CoreBodyRegistry.Get( CorePhysics.JointGetBodyA( joint ) );
	public PhysicsBody GetBody2() => CoreBodyRegistry.Get( CorePhysics.JointGetBodyB( joint ) );

	//-- frames ---------------------------------------------------------------

	public void GetLocalFrameA( out Vector3 position, out Rotation rotation )
	{
		var frame = CorePhysics.JointGetFrameA( joint );
		position = frame.Position;
		rotation = frame.Rotation;
	}

	public void GetLocalFrameB( out Vector3 position, out Rotation rotation )
	{
		var frame = CorePhysics.JointGetFrameB( joint );
		position = frame.Position;
		rotation = frame.Rotation;
	}

	public void SetLocalFrameA( Vector3 position, Rotation rotation )
		=> CorePhysics.JointSetFrameA( joint, new Transform( position, rotation ) );

	public void SetLocalFrameB( Vector3 position, Rotation rotation )
		=> CorePhysics.JointSetFrameB( joint, new Transform( position, rotation ) );

	//-- shared ---------------------------------------------------------------

	public bool IsCollisionEnabled() => CorePhysics.JointCollideConnected( joint );
	public void SetEnableCollision( bool enabled ) => CorePhysics.JointSetCollideConnected( joint, enabled );

	/// <summary>Box3D breaks a joint on a force threshold rather than an accumulated impulse, which is the same idea measured per second.</summary>
	public float GetMaxLinearImpulse() => CorePhysics.JointGetForceThreshold( joint );
	public void SetMaxLinearImpulse( float value ) => CorePhysics.JointSetForceThreshold( joint, value );

	public float GetMaxAngularImpulse() => CorePhysics.JointGetTorqueThreshold( joint );
	public void SetMaxAngularImpulse( float value ) => CorePhysics.JointSetTorqueThreshold( joint, value );

	public float GetLinearImpulse() => CorePhysics.JointGetForce( joint );
	public float GetAngularImpulse() => CorePhysics.JointGetTorque( joint );

	/// <summary>Which kind of constraint this is.</summary>
	public PhysicsJointType GetJointType() => CorePhysics.JointGetType( joint ) switch
	{
		CorePhysics.JointType.Fixed => PhysicsJointType.WELD_JOINT,
		CorePhysics.JointType.Spring => PhysicsJointType.SPRING,
		CorePhysics.JointType.Hinge => PhysicsJointType.REVOLUTE_JOINT,
		CorePhysics.JointType.Slider => PhysicsJointType.PRISMATIC_JOINT,
		CorePhysics.JointType.BallSocket => PhysicsJointType.SPHERICAL_JOINT,
		//Box3D's motor, wheel and parallel joints have no name in the legacy joint enum.
		//Generic is the honest answer: they are joints, and none of the specific names would be true.
		CorePhysics.JointType.Control or CorePhysics.JointType.Wheel or CorePhysics.JointType.Upright
			=> PhysicsJointType.GENERIC_JOINT,
		CorePhysics.JointType.Filter => PhysicsJointType.NULL_JOINT,
		_ => PhysicsJointType.NULL_JOINT,
	};

	//-- tuning -- the core maps each setting to the Box3D control that matches the joint kind

	/// <summary>Linear spring tuning, as a <c>PhysicsSpring</c> flattened into a vector: stiffness in hertz, damping ratio, and a force cap.</summary>
	public void SetLinearSpring( Vector3 vec ) => CorePhysics.JointSetLinearSpring( joint, vec.x, vec.y, vec.z );

	public Vector3 GetLinearSpring()
	{
		CorePhysics.JointGetLinearSpring( joint, out var hertz, out var damping, out var maximum );
		return new Vector3( hertz, damping, maximum );
	}

	/// <summary>Angular spring tuning. A hinge reads the same three floats differently - target angle, then hertz and damping - because that is the only way the engine above has of asking a hinge to hold an angle.</summary>
	public void SetAngularSpring( Vector3 vec )
	{
		if ( CorePhysics.JointGetType( joint ) == CorePhysics.JointType.Hinge )
		{
			CorePhysics.JointSetTargetAngle( joint, vec.x, vec.y, vec.z );
			return;
		}

		CorePhysics.JointSetAngularSpring( joint, vec.x, vec.y, vec.z );
	}

	public Vector3 GetAngularSpring()
	{
		CorePhysics.JointGetAngularSpring( joint, out var hertz, out var damping, out var maximum );
		return new Vector3( hertz, damping, maximum );
	}

	public void SetAngularMotor( float targetVelocity, float maxTorque ) => CorePhysics.JointSetAngularMotor( joint, targetVelocity, maxTorque );

	//A hinge's "length" is its angle and a slider's is its translation; the engine above converts to radians before it gets here, so these pass through.
	public void SetMinLength( float f ) => CorePhysics.JointSetLowerLimit( joint, f );
	public float GetMinLength() => CorePhysics.JointGetLowerLimit( joint );
	public void SetMaxLength( float f ) => CorePhysics.JointSetUpperLimit( joint, f );
	public float GetMaxLength() => CorePhysics.JointGetUpperLimit( joint );

	public void SetMinForce( float f ) => CorePhysics.JointSetMinForce( joint, f );
	public float GetMinForce() => CorePhysics.JointGetMinForce( joint );
	public void SetMaxForce( float f ) => CorePhysics.JointSetMaxForce( joint, f );
	public float GetMaxForce() => CorePhysics.JointGetMaxForce( joint );

	/// <summary>Resistance to being moved. Box3D has no friction on a joint, so the core makes one out of a motor with no target speed: it fights motion up to the given torque and drives nothing.</summary>
	public void SetFriction( float f ) => CorePhysics.JointSetFriction( joint, f );
	public void SetRangeLimitEnabled( bool enabled ) => CorePhysics.JointEnableRangeLimit( joint, enabled );
	public void SetLinearMotor( float velocity, float maxForce ) => CorePhysics.JointSetLinearMotor( joint, velocity, maxForce );
	public void SetTargetTranslation( float position, float hertz, float damping ) => CorePhysics.JointSetTargetTranslation( joint, position, hertz, damping );

	/// <summary>A ball socket's swing (cone) and twist limits.</summary>
	public void SetLimit( string name, Vector3 limit )
	{
		var lower = limit.x.DegreeToRadian();
		var upper = limit.y.DegreeToRadian();

		if ( name == "swing" )
			CorePhysics.JointSetSwingLimit( joint, lower, upper );
		else if ( name == "twist" )
			CorePhysics.JointSetTwistLimit( joint, lower, upper );
	}

	public void SetLimitEnabled( string name, bool state )
	{
		if ( name == "swing" )
			CorePhysics.JointEnableSwingLimit( joint, state );
		else if ( name == "twist" )
			CorePhysics.JointEnableTwistLimit( joint, state );
	}

	public float GetAngle() => CorePhysics.JointGetAngle( joint );

	public void SetMotorVelocity( Vector3 velocity, float maxTorque ) => CorePhysics.JointSetMotorVelocity( joint, velocity, maxTorque );

	public void SetTargetRotation( Rotation rotation, float hertz, float damping )
		=> CorePhysics.JointSetTargetRotation( joint, rotation, hertz, damping );

	public void Motor_SetLinearVelocity( Vector3 velocity ) => CorePhysics.JointMotorSetLinearVelocity( joint, velocity );
	public void Motor_SetAngularVelocity( Vector3 velocity ) => CorePhysics.JointMotorSetAngularVelocity( joint, velocity );
	public void Motor_SetMaxVelocityForce( float maxForce ) => CorePhysics.JointMotorSetMaxVelocityForce( joint, maxForce );
	public void Motor_SetMaxVelocityTorque( float maxTorque ) => CorePhysics.JointMotorSetMaxVelocityTorque( joint, maxTorque );
	public void Motor_SetLinearHertz( float hertz ) => CorePhysics.JointMotorSetLinearHertz( joint, hertz );
	public void Motor_SetLinearDampingRatio( float damping ) => CorePhysics.JointMotorSetLinearDamping( joint, damping );
	public void Motor_SetAngularHertz( float hertz ) => CorePhysics.JointMotorSetAngularHertz( joint, hertz );
	public void Motor_SetAngularDampingRatio( float damping ) => CorePhysics.JointMotorSetAngularDamping( joint, damping );
	public void Motor_SetMaxSpringForce( float maxForce ) => CorePhysics.JointMotorSetMaxSpringForce( joint, maxForce );
	public void Motor_SetMaxSpringTorque( float maxTorque ) => CorePhysics.JointMotorSetMaxSpringTorque( joint, maxTorque );
	public Vector3 Motor_GetLinearVelocity() => CorePhysics.JointMotorGetLinearVelocity( joint );
	public Vector3 Motor_GetAngularVelocity() => CorePhysics.JointMotorGetAngularVelocity( joint );
	public float Motor_GetMaxVelocityForce() => CorePhysics.JointMotorGetMaxVelocityForce( joint );
	public float Motor_GetMaxVelocityTorque() => CorePhysics.JointMotorGetMaxVelocityTorque( joint );
	public float Motor_GetLinearHertz() => CorePhysics.JointMotorGetLinearHertz( joint );
	public float Motor_GetLinearDampingRatio() => CorePhysics.JointMotorGetLinearDamping( joint );
	public float Motor_GetAngularHertz() => CorePhysics.JointMotorGetAngularHertz( joint );
	public float Motor_GetAngularDampingRatio() => CorePhysics.JointMotorGetAngularDamping( joint );
	public float Motor_GetMaxSpringForce() => CorePhysics.JointMotorGetMaxSpringForce( joint );
	public float Motor_GetMaxSpringTorque() => CorePhysics.JointMotorGetMaxSpringTorque( joint );
	public void Wheel_EnableSuspension( bool flag ) => CorePhysics.JointWheelEnableSuspension( joint, flag );
	public bool Wheel_IsSuspensionEnabled() => CorePhysics.JointWheelSuspensionEnabled( joint );
	public void Wheel_SetSuspensionHertz( float hertz ) => CorePhysics.JointWheelSetSuspensionHertz( joint, hertz );
	public float Wheel_GetSuspensionHertz() => CorePhysics.JointWheelGetSuspensionHertz( joint );
	public void Wheel_SetSuspensionDampingRatio( float dampingRatio ) => CorePhysics.JointWheelSetSuspensionDamping( joint, dampingRatio );
	public float Wheel_GetSuspensionDampingRatio() => CorePhysics.JointWheelGetSuspensionDamping( joint );
	public void Wheel_EnableSuspensionLimit( bool flag ) => CorePhysics.JointWheelEnableSuspensionLimit( joint, flag );
	public bool Wheel_IsSuspensionLimitEnabled() => CorePhysics.JointWheelSuspensionLimitEnabled( joint );
	public float Wheel_GetLowerSuspensionLimit() => CorePhysics.JointWheelGetLowerSuspensionLimit( joint );
	public float Wheel_GetUpperSuspensionLimit() => CorePhysics.JointWheelGetUpperSuspensionLimit( joint );
	public void Wheel_SetSuspensionLimits( float lower, float upper ) => CorePhysics.JointWheelSetSuspensionLimits( joint, lower, upper );
	public void Wheel_EnableSpinMotor( bool flag ) => CorePhysics.JointWheelEnableSpinMotor( joint, flag );
	public bool Wheel_IsSpinMotorEnabled() => CorePhysics.JointWheelSpinMotorEnabled( joint );
	public void Wheel_SetSpinMotorSpeed( float speed ) => CorePhysics.JointWheelSetSpinMotorSpeed( joint, speed );
	public float Wheel_GetSpinMotorSpeed() => CorePhysics.JointWheelGetSpinMotorSpeed( joint );
	public void Wheel_SetMaxSpinTorque( float torque ) => CorePhysics.JointWheelSetMaxSpinTorque( joint, torque );
	public float Wheel_GetMaxSpinTorque() => CorePhysics.JointWheelGetMaxSpinTorque( joint );
	public void Wheel_EnableSteering( bool flag ) => CorePhysics.JointWheelEnableSteering( joint, flag );
	public bool Wheel_IsSteeringEnabled() => CorePhysics.JointWheelSteeringEnabled( joint );
	public void Wheel_SetSteeringHertz( float hertz ) => CorePhysics.JointWheelSetSteeringHertz( joint, hertz );
	public float Wheel_GetSteeringHertz() => CorePhysics.JointWheelGetSteeringHertz( joint );
	public void Wheel_SetSteeringDampingRatio( float dampingRatio ) => CorePhysics.JointWheelSetSteeringDamping( joint, dampingRatio );
	public float Wheel_GetSteeringDampingRatio() => CorePhysics.JointWheelGetSteeringDamping( joint );
	public void Wheel_SetMaxSteeringTorque( float torque ) => CorePhysics.JointWheelSetMaxSteeringTorque( joint, torque );
	public float Wheel_GetMaxSteeringTorque() => CorePhysics.JointWheelGetMaxSteeringTorque( joint );
	public void Wheel_EnableSteeringLimit( bool flag ) => CorePhysics.JointWheelEnableSteeringLimit( joint, flag );
	public bool Wheel_IsSteeringLimitEnabled() => CorePhysics.JointWheelSteeringLimitEnabled( joint );
	public float Wheel_GetLowerSteeringLimit() => CorePhysics.JointWheelGetLowerSteeringLimit( joint );
	public float Wheel_GetUpperSteeringLimit() => CorePhysics.JointWheelGetUpperSteeringLimit( joint );
	public void Wheel_SetSteeringLimits( float lowerRadians, float upperRadians ) => CorePhysics.JointWheelSetSteeringLimits( joint, lowerRadians, upperRadians );
	public void Wheel_SetTargetSteeringAngle( float radians ) => CorePhysics.JointWheelSetTargetSteeringAngle( joint, radians );
	public float Wheel_GetTargetSteeringAngle() => CorePhysics.JointWheelGetTargetSteeringAngle( joint );
	public float Wheel_GetSpinSpeed() => CorePhysics.JointWheelGetSpinSpeed( joint );
	public float Wheel_GetSpinTorque() => CorePhysics.JointWheelGetSpinTorque( joint );
	public float Wheel_GetSteeringAngle() => CorePhysics.JointWheelGetSteeringAngle( joint );
	public float Wheel_GetSteeringTorque() => CorePhysics.JointWheelGetSteeringTorque( joint );
	public void Parallel_SetHertz( float hertz ) => CorePhysics.JointParallelSetHertz( joint, hertz );
	public float Parallel_GetHertz() => CorePhysics.JointParallelGetHertz( joint );
	public void Parallel_SetDampingRatio( float dampingRatio ) => CorePhysics.JointParallelSetDamping( joint, dampingRatio );
	public float Parallel_GetDampingRatio() => CorePhysics.JointParallelGetDamping( joint );
	public void Parallel_SetMaxTorque( float maxTorque ) => CorePhysics.JointParallelSetMaxTorque( joint, maxTorque );
	public float Parallel_GetMaxTorque() => CorePhysics.JointParallelGetMaxTorque( joint );
}
