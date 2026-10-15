namespace Rinten;

/// <summary>
/// A 2D light shining in a cone along the object's right (+x), turned on the
/// plane by rolling the object.
/// </summary>
[Expose]
[Title( "Spot Light 2D" )]
[Category( "Light 2D" )]
[Icon( "highlight" )]
[EditorHandle( Icon = "highlight" )]
public class SpotLight2D : Light2D
{
	/// <summary>How wide the cone is, in degrees, edge to edge.</summary>
	[Property, Range( 0, 360 )]
	public float Angle { get; set; } = 60.0f;

	/// <summary>How much of the cone's edge fades out rather than stopping hard, from nought to one.</summary>
	[Property, Range( 0, 1 )]
	public float Softness { get; set; } = 0.25f;

	protected override void DrawShape()
	{
		var half = Angle * 0.5f;

		foreach ( var edge in new[] { -half, half } )
		{
			var angle = MathX.DegreeToRadian( edge );
			Gizmo.Draw.Line( Vector3.Zero, new Vector3( MathF.Cos( angle ), MathF.Sin( angle ), 0.0f ) * Range );
		}

		DrawCircle( Range, -half, Angle );
	}
}
