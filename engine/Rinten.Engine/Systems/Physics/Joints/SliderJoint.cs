namespace Rinten.Physics;

/// <summary>A slider constraint, basically allows movement only on the arbitrary axis between the 2 constrained objects on creation.</summary>
public partial class SliderJoint : PhysicsJoint
{
	internal SliderJoint( HandleCreationData _ ) { }

	/// <summary>Maximum length it should be allowed to go</summary>
	public float MaxLength
	{
		get => backend.GetMaxLength();
		set => backend.SetMaxLength( value );
	}

	/// <summary>Minimum length it should be allowed to go</summary>
	public float MinLength
	{
		get => backend.GetMinLength();
		set => backend.SetMinLength( value );
	}

	/// <summary>Slider friction.</summary>
	public float Friction
	{
		set => backend.SetFriction( value );
	}

	/// <summary>Sets and enables a linear velocity motor, in metres a second.</summary>
	public void SetLinearMotor( float targetVelocity, float maxForce ) => backend.SetLinearMotor( targetVelocity, maxForce );

	/// <summary>Sets and enables a position spring motor.</summary>
	public void SetTargetPosition( float position, float frequency, float dampingRatio ) => backend.SetTargetTranslation( position, frequency, dampingRatio );

	//The range, and the limit forced on or off; null leaves it on whenever the range is not empty
	internal void ConfigureLimits( Vector2 limits, bool? enabled )
	{
		MinLength = limits.x;
		MaxLength = limits.y;
		if ( enabled is bool state ) backend.SetRangeLimitEnabled( state );
	}
}
