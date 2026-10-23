using System.Collections.Concurrent;
using System.Threading;

namespace Rinten;

//Built by ParticlePlayer from a .fx file, not added by hand - see ParticleEffect.
/// <summary>Adds lighting to particles in your effect.</summary>
[Hide]
[Expose]
[Title( "Particle Light Renderer" )]
[Category( "Effects" )]
[Icon( "tips_and_updates" )]
public sealed class ParticleLightRenderer : ParticleController
{
	/// <summary>If 1, then every particle will get a light. If 0, no particles will get a light. If 0.5, half will get a particle.</summary>
	[Group( "Performance" )]
	[Range( 0, 1 )]
	[Property] public float Ratio { get; set; } = 1;

	[Group( "Performance" )]
	[Property] public int MaximumLights { get; set; } = 8;

	[Group( "Light Description" )]
	[Property] public ParticleFloat Scale { get; set; } = 0.8f;

	/// <summary>How fast each light dies with distance, as the power the squared distance is raised to: one is the inverse square, nought fills the radius evenly. See <see cref="Light.Attenuation"/>.</summary>
	[Group( "Light Description" )]
	[Property] public ParticleFloat Attenuation { get; set; } = 1;

	[Group( "Light Description" )]
	[Property] public ParticleFloat Brightness { get; set; } = 1;

	[Group( "Light Description" )]
	[Property] public ParticleGradient LightColor { get; set; } = Color.White;

	[Group( "Light Description" )]
	[Property] public bool UseParticleColor { get; set; } = true;


	internal int currentLightCount;

	//Listeners and lights of particles that have gone, for the next ones: one of each per particle was garbage every frame
	readonly ConcurrentBag<ParticleLight> spareListeners = new();
	readonly ConcurrentBag<ScenePointLight> spareLights = new();

	protected override void OnParticleCreated( Particle p )
	{
		if ( Random.Shared.Float( 0, 1 ) > Ratio )
			return;

		if ( currentLightCount > MaximumLights )
			return;

		currentLightCount++;

		p.AddListener( spareListeners.TryTake( out var kept ) ? kept : new ParticleLight( this ), this );
	}

	internal ScenePointLight TakeLight( Vector3 position )
	{
		while ( spareLights.TryTake( out var kept ) )
		{
			if ( !kept.IsValid() ) continue;

			kept.Position = position;
			kept.RenderingEnabled = true;
			return kept;
		}

		return new ScenePointLight( Scene.SceneWorld, position, 5.0f, Color.Red );
	}

	//A particle's listener and light, back once it has gone; deleted outright where this is no longer running
	internal void Return( ParticleLight listener, ScenePointLight light )
	{
		if ( light.IsValid() )
		{
			if ( !Active ) light.Delete();
			else
			{
				light.RenderingEnabled = false;
				spareLights.Add( light );
			}
		}

		if ( Active ) spareListeners.Add( listener );
	}

	protected override void OnDisabled()
	{
		base.OnDisabled();

		while ( spareLights.TryTake( out var kept ) ) kept.Delete();
		spareListeners.Clear();
	}
}


class ParticleLight : Particle.BaseListener
{
	public ParticleLightRenderer Renderer;
	ScenePointLight so;

	public ParticleLight( ParticleLightRenderer particleLightRenderer )
	{
		Renderer = particleLightRenderer;
	}

	public override void OnEnabled( Particle p )
	{
		//Its radius is overwritten by the first OnUpdate from the particle's own scale
		so = Renderer.TakeLight( p.Position );
	}

	public override void OnDisabled( Particle p )
	{
		Interlocked.Decrement( ref Renderer.currentLightCount );

		var light = so;
		so = null;

		Renderer.Return( this, light );
	}

	public override void OnUpdate( Particle p, float dt )
	{
		if ( !so.IsValid() ) return;

		float brightness = Renderer.Brightness.Evaluate( p, 2346 );
		Color color = Renderer.LightColor.Evaluate( p, 6342 );
		color = color.WithAlpha( 1 ) * color.a * brightness;

		so.ShadowsEnabled = false;
		so.Transform = new Transform( p.Position, p.Angles );

		if ( !Renderer.UseParticleColor )
		{
			so.LightColor = color;
		}
		else
		{
			so.LightColor = p.Color.WithAlpha( 1 ) * p.Alpha * p.Color.a * color;
		}

		so.Radius = Renderer.Scale.Evaluate( p, 43 ) * p.Size.x;
		so.Falloff = Renderer.Attenuation.Evaluate( p, 4323 );
		so.ColorTint = p.Color.WithAlphaMultiplied( p.Alpha );
	}
}
