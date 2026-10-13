namespace Rinten;

/// <summary>
/// A light on the 2D plane, lighting the sprites and tiles that are lit in 2D - see
/// <see cref="SpriteRenderer.Lit2D"/>. Nothing to do with the 3D lights: it reaches no
/// mesh and a 3D light reaches nothing lit this way.
/// </summary>
/// <remarks>
/// Full strength where it stands, fading smoothly to nothing at <see cref="Range"/>,
/// and stopped by whatever is an occluder - see <see cref="SpriteRenderer.Occluder2D"/>.
/// <see cref="SourceSize"/> is how big the light itself is, which is what softens the
/// edges of its shadows.
/// </remarks>
[Expose]
public abstract class Light2D : Component, Component.ExecuteInEditor
{
	/// <summary>The colour of the light.</summary>
	[Property]
	public Color Color { get; set; } = Color.White;

	/// <summary>How strong the light is, as a multiplier on <see cref="Color"/>.</summary>
	[Property, Range( 0, 10 )]
	public float Intensity { get; set; } = 1.0f;

	/// <summary>How far the light reaches, in world units. It fades out smoothly up to here.</summary>
	[Property, Range( 0, 64 )]
	public float Range { get; set; } = 5.0f;

	/// <summary>
	/// How big the light itself is, in world units. Nought casts hard-edged shadows; bigger
	/// softens them, more so the further they fall from what casts them.
	/// </summary>
	[Property, Range( 0, 4 )]
	public float SourceSize { get; set; } = 0.1f;

	/// <summary>The colour times the intensity.</summary>
	internal Color Radiance => Color * Intensity;

	/// <summary>Where the light points on the plane - see <see cref="SpotLight2D"/>.</summary>
	/// <remarks>The object's right, not its forward: forward is -z, straight out of the plane.</remarks>
	internal Vector2 Direction
	{
		get
		{
			var right = WorldRotation.Right;
			var flat = new Vector2( right.x, right.y );

			return flat.LengthSquared > 1e-8f ? flat.Normal : new Vector2( 1, 0 );
		}
	}

	/// <summary>A circle on the plane of the light's own space, for the gizmos.</summary>
	protected static void DrawCircle( float radius, float startDegrees = 0.0f, float degrees = 360.0f, int sections = 32 )
	{
		var last = Vector3.Zero;

		for ( var i = 0; i <= sections; i++ )
		{
			var angle = MathX.DegreeToRadian( startDegrees + degrees * i / sections );
			var point = new Vector3( MathF.Cos( angle ), MathF.Sin( angle ), 0.0f ) * radius;

			if ( i > 0 ) Gizmo.Draw.Line( last, point );
			last = point;
		}
	}

	protected override void DrawGizmos()
	{
		if ( !Gizmo.IsSelected && !Gizmo.IsHovered ) return;

		using var scope = Gizmo.Scope( $"light2d-{GetHashCode()}" );

		Gizmo.Draw.Color = Color.WithAlpha( Gizmo.IsSelected ? 0.9f : 0.4f );
		DrawShape();
	}

	/// <summary>The light's reach, in its own space.</summary>
	protected virtual void DrawShape()
	{
		DrawCircle( Range );
		if ( SourceSize > 0.0f ) DrawCircle( SourceSize, sections: 12 );
	}
}
