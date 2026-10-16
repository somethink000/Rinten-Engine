namespace Rinten;

//Added by SpawnEffectOnDeathModule; the spawned players are hidden, unsaved, play loops once and are played again when done
/// <summary>Plays another effect where a particle dies - a burst where a rocket ran out, a splash where a drop stopped, a puff where an ember went out.</summary>
[Hide]
[Expose]
[Title( "Particle Effect Spawner" )]
[Category( "Effects" )]
[Icon( "call_split" )]
public sealed class ParticleEffectSpawner : ParticleController
{
	/// <summary>What is played.</summary>
	[Property]
	public ParticleDefinition Effect { get; set; }

	/// <summary>How likely each death is to play it, nought to one.</summary>
	[Property, Range( 0.0f, 1.0f )]
	public float Chance { get; set; } = 1.0f;

	/// <summary>How big the spawned effect is, on top of its own scale.</summary>
	[Property]
	public float Scale { get; set; } = 1.0f;

	/// <summary>Whether the effect faces the way the particle was going.</summary>
	[Property]
	public bool AlignToVelocity { get; set; }

	/// <summary>Whether a particle that was killed by hitting something plays it, or only one that ran out of life.</summary>
	[Property]
	public bool OnCollision { get; set; } = true;

	/// <summary>Whether a particle that lived its life out plays it, or only one that died hitting something.</summary>
	[Property]
	public bool OnExpiry { get; set; } = true;

	/// <summary>The most alive at once, so a thousand sparks cannot become a thousand effects.</summary>
	[Property]
	public int MaxAlive { get; set; } = 16;

	readonly ParticleChildEffects _spawned = new();

	//Where particles died, from the threads the effect steps on; played on the main thread, as an effect is a game object
	readonly System.Collections.Concurrent.ConcurrentQueue<(Vector3 Position, Vector3 Velocity)> _deaths = new();

	protected override void OnParticleDestroyed( Particle p )
	{
		if ( Effect is null ) return;

		//A death by collision leaves a hit time behind; one by age does not.
		var collided = p.HitTime > 0.0f && ( Time.Now - p.HitTime ) < 0.1f;

		if ( collided && !OnCollision ) return;
		if ( !collided && !OnExpiry ) return;

		if ( Chance < 1.0f && Random.Shared.Float( 0.0f, 1.0f ) > Chance ) return;

		_deaths.Enqueue( (p.Position, p.Velocity) );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		_spawned.Update();

		while ( _deaths.TryDequeue( out var death ) )
		{
			if ( Effect is null || !Scene.IsValid() || _spawned.Playing >= MaxAlive ) continue;

			var player = _spawned.Take( Effect, $"{Effect.ResourceName} (spawned)" );
			var go = player.GameObject;

			go.WorldPosition = death.Position;
			go.WorldRotation = AlignToVelocity && !death.Velocity.IsNearlyZero()
				? Rotation.LookAt( death.Velocity.Normal )
				: Rotation.Identity;
			go.WorldScale = Scale;

			_spawned.Start( player );
		}
	}

	protected override void OnDisabled()
	{
		base.OnDisabled();

		_spawned.Clear();
		_deaths.Clear();
	}
}
