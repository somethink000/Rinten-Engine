namespace Rinten;

/// <summary>
/// The scene's 2D lighting, gathered for the renderer when it builds a frame: whether it
/// is on at all, its settings, the global light, and the 2D lights nearest the camera.
/// </summary>
/// <remarks>
/// Gathered as the frame is built rather than in the scene's update, so an editor scene
/// that is not playing - which does not update - is lit all the same. See
/// CoreSceneRenderer.SetLight2D.
/// </remarks>
public sealed class SceneLight2DSystem : GameObjectSystem<SceneLight2DSystem>
{
	/// <summary>How many 2D lights a frame carries: the nearest to the camera, where there are more.</summary>
	public const int MostLights = 32;

	/// <summary>How the 2D lighting is worked out - a <see cref="Lighting2D"/>'s, or the defaults.</summary>
	public readonly record struct Options( bool SelfShadow, float ResolutionScale, float Margin, Lighting2D.DebugView Debug );

	/// <summary>
	/// One 2D light, as the renderer lights with it.
	/// </summary>
	/// <param name="Position">Where it stands.</param>
	/// <param name="Radiance">Its colour times its intensity.</param>
	/// <param name="Range">How far it reaches.</param>
	/// <param name="SourceSize">How big the light itself is.</param>
	/// <param name="Direction">Where a spot points on the plane.</param>
	/// <param name="HalfAngle">Half the spot's cone, in radians; π for a point light, which shines every way.</param>
	/// <param name="Softness">How much of the cone's edge fades, nought to one.</param>
	public readonly record struct Record( Vector3 Position, Color Radiance, float Range, float SourceSize, Vector2 Direction, float HalfAngle, float Softness );

	readonly List<Record> lights = new();
	readonly List<Light2D> found = new();

	/// <summary>Whether the scene has anything 2D-lit by: a <see cref="Light2D"/> or a <see cref="GlobalLight2D"/>.</summary>
	public bool Active { get; private set; }

	/// <summary>The settings it is worked out with.</summary>
	public Options Settings { get; private set; } = Defaults;

	static readonly Options Defaults = new( false, 0.5f, 0.15f, Lighting2D.DebugView.None );

	/// <summary>Every <see cref="GlobalLight2D"/>, added together, already multiplied by its intensity.</summary>
	public Color GlobalLight { get; private set; }

	/// <summary>The scene's 2D lights this frame, the nearest to the camera first.</summary>
	public IReadOnlyList<Record> Lights => lights;

	public SceneLight2DSystem( Scene scene ) : base( scene )
	{
	}

	/// <summary>Looks the scene over again - see CoreSceneRenderer.SetLight2D, which asks once a frame.</summary>
	internal void Gather()
	{
		lights.Clear();
		found.Clear();

		var settings = Scene.Camera.IsValid() && Scene.Camera.GetComponent<Lighting2D>() is { Enabled: true } own ? own : null;

		if ( settings is null )
		{
			foreach ( var one in Scene.GetAllComponents<Lighting2D>() )
			{
				if ( !one.IsValid() || !one.Enabled ) continue;

				settings = one;
				break;
			}
		}

		Settings = settings is null ? Defaults
			: new Options( settings.SelfShadow, settings.ResolutionScale, settings.Margin, settings.Debug );

		var global = Color.Black;
		var anyGlobal = false;

		foreach ( var light in Scene.GetAllComponents<GlobalLight2D>() )
		{
			if ( !light.IsValid() || !light.Enabled ) continue;

			global += light.Color * light.Intensity;
			anyGlobal = true;
		}

		GlobalLight = global.WithAlpha( 1.0f );

		foreach ( var light in Scene.GetAllComponents<Light2D>() )
		{
			if ( !light.IsValid() || !light.Enabled || light.Range <= 0.0f ) continue;

			found.Add( light );
		}

		Active = anyGlobal || found.Count > 0;

		//The nearest first, so a scene with more lights than a frame carries keeps the ones on screen
		if ( found.Count > MostLights && Scene.Camera.IsValid() )
		{
			var eye = Scene.Camera.WorldPosition;
			found.Sort( ( a, b ) => a.WorldPosition.DistanceSquared( eye ).CompareTo( b.WorldPosition.DistanceSquared( eye ) ) );
		}

		for ( var i = 0; i < found.Count && i < MostLights; i++ )
		{
			var light = found[i];
			var spot = light as SpotLight2D;

			lights.Add( new Record(
				light.WorldPosition,
				light.Radiance,
				light.Range,
				MathF.Max( light.SourceSize, 0.0f ),
				light.Direction,
				spot is null ? MathF.PI : MathX.DegreeToRadian( Math.Clamp( spot.Angle, 0.0f, 360.0f ) * 0.5f ),
				spot?.Softness ?? 0.0f ) );
		}
	}
}
