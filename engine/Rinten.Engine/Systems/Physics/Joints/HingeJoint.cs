namespace Rinten.Physics;

/// <summary>A hinge-like constraint.</summary>
public partial class HingeJoint : PhysicsJoint
{
	internal HingeJoint( HandleCreationData _ ) { }

	/// <summary>Maximum angle it should be allowed to go</summary>
	public float MaxAngle
	{
		get => backend.GetMaxLength();
		set => backend.SetMaxLength( value.DegreeToRadian() );
	}

	/// <summary>Minimum angle it should be allowed to go</summary>
	public float MinAngle
	{
		get => backend.GetMinLength();
		set => backend.SetMinLength( value.DegreeToRadian() );
	}

	public float Angle
	{
		get => backend.GetAngle().RadianToDegree();
	}

	public Vector3 Axis
	{
		get
		{
			//The solver turns a revolute about its frame's third axis, whichever way that points in the world - a number, not a direction.
			backend.GetLocalFrameA( out _, out var rotation );
			return rotation * new Vector3( 0, 0, 1 );
		}
	}

	public float Speed => Axis.Dot( Body2.AngularVelocity - Body1.AngularVelocity );

	//The range in degrees, and the limit forced on or off; null leaves it on whenever the range is not empty
	internal void ConfigureLimits( Vector2 limits, bool? enabled )
	{
		MinAngle = limits.x;
		MaxAngle = limits.y;
		if ( enabled is bool state ) backend.SetRangeLimitEnabled( state );
	}

	/// <summary>Hinge friction.</summary>
	public float Friction
	{
		set => backend.SetFriction( value );
	}
}
