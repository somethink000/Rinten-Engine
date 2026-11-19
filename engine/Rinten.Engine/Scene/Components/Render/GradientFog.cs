namespace Rinten;

/// <summary>Adds a gradient fog to the world</summary>
[Title( "Gradient Fog" )]
[Category( "Rendering" )]
[Icon( "foggy" )]
public class GradientFog : Component, Component.ExecuteInEditor
{

	[Group( "Vertical Fog" )]
	[Property] public Color Color { get; set; } = Color.White;

	/// <summary>How thick the layer is above where this stands, in metres.</summary>
	[Group( "Vertical Fog" )]
	[Property] public float Height { get; set; } = 2.54f;

	[Group( "Vertical Fog" )]
	[Property] public float VerticalFalloffExponent { get; set; } = 1.0f;

	[Group( "Camera Distance Fade" )]
	[Property] public float StartDistance { get; set; } = 0.0f;

	//Twenty-six, which is the old default of a thousand and twenty-four inches. It stayed at 1024 while Height was converted, so the two were a kilometre and two metres and a half in the same component - fog that never arrived over a layer nobody could stand up in.
	/// <summary>How far from the eye the fog reaches its full depth, in metres.</summary>
	[Group( "Camera Distance Fade" )]
	[Property] public float EndDistance { get; set; } = 26.0f;

	[Group( "Camera Distance Fade" )]
	[Property] public float FalloffExponent { get; set; } = 1.0f;

	/// <summary>Whether the fog takes the sky's colour in each direction, times Color - haze glowing toward a low sun.</summary>
	[Property] public bool ColorFromSky { get; set; }

	protected override void OnPreRender()
	{
		var world = Scene?.SceneWorld;
		if ( !world.IsValid() ) return;

		world.GradientFog.Enabled = true;
		world.GradientFog.ColorFromSky = ColorFromSky;
		world.GradientFog.StartDistance = StartDistance;
		world.GradientFog.EndDistance = EndDistance;
		world.GradientFog.Color = Color.WithAlpha( 1 );
		world.GradientFog.DistanceFalloffExponent = FalloffExponent;
		world.GradientFog.MaximumOpacity = Color.a;
		world.GradientFog.VerticalFalloffExponent = VerticalFalloffExponent;
		world.GradientFog.StartHeight = WorldPosition.y;
		world.GradientFog.EndHeight = WorldPosition.y + MathF.Max( 1, Height );
	}
}
