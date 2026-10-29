namespace Rinten;

//What PhysicsJoint asks of the simulation; Motor_, Wheel_, Parallel_ apply only to their joint kinds
internal interface IPhysicsJointBackend
{
	bool IsValid { get; }

﻿
	PhysicsWorld GetWorld();
	PhysicsBody GetBody1();
	PhysicsBody GetBody2();
	void GetLocalFrameA( out Vector3 position, out Rotation rotation );
	void GetLocalFrameB( out Vector3 position, out Rotation rotation );
	void SetLocalFrameA( Vector3 position, Rotation rotation );
	void SetLocalFrameB( Vector3 position, Rotation rotation );
	void SetEnableCollision( bool bEnabled );
	bool IsCollisionEnabled();
	PhysicsJointType GetJointType();
	void SetLinearSpring( Vector3 vec );
	Vector3 GetLinearSpring();
	void SetAngularSpring( Vector3 vec );
	Vector3 GetAngularSpring();
	void SetAngularMotor( float targetVelocity, float maxTorque );
	void SetMinLength( float f );
	float GetMinLength();
	void SetMaxLength( float f );
	float GetMaxLength();
	void SetMinForce( float f );
	float GetMinForce();
	void SetMaxForce( float f );
	float GetMaxForce();
	void SetFriction( float f );

	//A hinge's or slider's range limit on or off, whatever its range
	void SetRangeLimitEnabled( bool enabled );

	//A slider driven at a speed with at most this force, its spring off
	void SetLinearMotor( float velocity, float maxForce );

	//A slider's spring pulling it to this offset along its axis, its motor off
	void SetTargetTranslation( float position, float hertz, float damping );
	void SetLimit( string name, Vector3 limit );
	void SetLimitEnabled( string name, bool state );
	float GetAngle();
	float GetLinearImpulse();
	float GetAngularImpulse();
	float GetMaxLinearImpulse();
	float GetMaxAngularImpulse();
	void SetMaxLinearImpulse( float flMaxLinearImpulse );
	void SetMaxAngularImpulse( float flMaxAngularImpulse );
	void SetMotorVelocity( Vector3 velocity, float maxTorque );
	void SetTargetRotation( Rotation rotation, float hertz, float damping );
	void Motor_SetLinearVelocity( Vector3 velocity );
	void Motor_SetAngularVelocity( Vector3 velocity );
	void Motor_SetMaxVelocityForce( float maxForce );
	void Motor_SetMaxVelocityTorque( float maxTorque );
	void Motor_SetLinearHertz( float hertz );
	void Motor_SetLinearDampingRatio( float damping );
	void Motor_SetAngularHertz( float hertz );
	void Motor_SetAngularDampingRatio( float damping );
	void Motor_SetMaxSpringForce( float maxForce );
	void Motor_SetMaxSpringTorque( float maxTorque );
	Vector3 Motor_GetLinearVelocity();
	Vector3 Motor_GetAngularVelocity();
	float Motor_GetMaxVelocityForce();
	float Motor_GetMaxVelocityTorque();
	float Motor_GetLinearHertz();
	float Motor_GetLinearDampingRatio();
	float Motor_GetAngularHertz();
	float Motor_GetAngularDampingRatio();
	float Motor_GetMaxSpringForce();
	float Motor_GetMaxSpringTorque();
	void Wheel_EnableSuspension( bool flag );
	bool Wheel_IsSuspensionEnabled();
	void Wheel_SetSuspensionHertz( float hertz );
	float Wheel_GetSuspensionHertz();
	void Wheel_SetSuspensionDampingRatio( float dampingRatio );
	float Wheel_GetSuspensionDampingRatio();
	void Wheel_EnableSuspensionLimit( bool flag );
	bool Wheel_IsSuspensionLimitEnabled();
	float Wheel_GetLowerSuspensionLimit();
	float Wheel_GetUpperSuspensionLimit();
	void Wheel_SetSuspensionLimits( float lower, float upper );
	void Wheel_EnableSpinMotor( bool flag );
	bool Wheel_IsSpinMotorEnabled();
	void Wheel_SetSpinMotorSpeed( float speed );
	float Wheel_GetSpinMotorSpeed();
	void Wheel_SetMaxSpinTorque( float torque );
	float Wheel_GetMaxSpinTorque();
	void Wheel_EnableSteering( bool flag );
	bool Wheel_IsSteeringEnabled();
	void Wheel_SetSteeringHertz( float hertz );
	float Wheel_GetSteeringHertz();
	void Wheel_SetSteeringDampingRatio( float dampingRatio );
	float Wheel_GetSteeringDampingRatio();
	void Wheel_SetMaxSteeringTorque( float torque );
	float Wheel_GetMaxSteeringTorque();
	void Wheel_EnableSteeringLimit( bool flag );
	bool Wheel_IsSteeringLimitEnabled();
	float Wheel_GetLowerSteeringLimit();
	float Wheel_GetUpperSteeringLimit();
	void Wheel_SetSteeringLimits( float lowerRadians, float upperRadians );
	void Wheel_SetTargetSteeringAngle( float radians );
	float Wheel_GetTargetSteeringAngle();
	float Wheel_GetSpinSpeed();
	float Wheel_GetSpinTorque();
	float Wheel_GetSteeringAngle();
	float Wheel_GetSteeringTorque();
	void Parallel_SetHertz( float hertz );
	float Parallel_GetHertz();
	void Parallel_SetDampingRatio( float dampingRatio );
	float Parallel_GetDampingRatio();
	void Parallel_SetMaxTorque( float maxTorque );
	float Parallel_GetMaxTorque();
}
