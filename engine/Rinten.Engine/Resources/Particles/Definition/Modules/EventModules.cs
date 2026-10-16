using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace Rinten;

/// <summary>What sets an Event off.</summary>
[Expose]
public enum ParticleEventTrigger
{
	/// <summary>The first step of the particle's life.</summary>
	OnBirth,

	/// <summary>The particle hit something - once per hit.</summary>
	OnCollision,

	/// <summary>The particle's Goal has it within <see cref="EventModule.Distance"/> of its target.</summary>
	OnGoalReached,

	/// <summary>The particle's age reached <see cref="EventModule.At"/> seconds, and then every <see cref="EventModule.Every"/> after.</summary>
	OnTime,

	/// <summary>The particle died, of age or of a hit.</summary>
	OnDeath,

	/// <summary>Every step, for as long as the particle lives - what a particle that publishes where it is uses.</summary>
	EveryStep,
}

/// <summary>Something about the parent particle handed to a spawned effect as one of its parameters.</summary>
[Expose]
public sealed class ParticleContextRow
{
	[Property]
	public bool Enabled { get; set; } = true;

	/// <summary>What of the particle is read.</summary>
	[Property]
	public ParticleFact Source { get; set; } = ParticleFact.Color;

	/// <summary>Which slot, where the source is one of the particle's slots.</summary>
	[Property, ParticleSlotName]
	public string Slot { get; set; }

	/// <summary>The spawned effect's parameter this is written to - a float takes one channel, a vector x y z, a colour rgba.</summary>
	[Property]
	public string Parameter { get; set; }

	/// <summary>Which of the four numbers a float parameter takes.</summary>
	[Property]
	public ParticleSlotChannel Channel { get; set; } = ParticleSlotChannel.X;

	/// <summary>What the numbers are multiplied by on the way.</summary>
	[Property]
	public float Scale { get; set; } = 1.0f;

	/// <summary>Whether the source is read as a slot rather than a fact of the particle.</summary>
	[Property]
	public bool FromSlot { get; set; }
}

/// <summary>Something written onto the particle when an Event fires.</summary>
[Expose]
public sealed class ParticleSetRow
{
	[Expose]
	public enum What
	{
		Slot,
		Velocity,
		Color,
		Size,
		Angles,

		/// <summary>Seconds it has left - nought kills it.</summary>
		LifeLeft,

		/// <summary>One of the effect's anchors, set to where this particle is (and the way it faces) - published, so a shader row, a shape or another emitter can read the particle's pose by the anchor's name.</summary>
		Anchor,
	}

	[Property]
	public bool Enabled { get; set; } = true;

	[Property]
	public What Set { get; set; } = What.Slot;

	[Property, ShowIf( nameof( Set ), What.Slot ), ParticleSlotName]
	public string Slot { get; set; }

	[Property, ShowIf( nameof( Set ), What.Anchor ), ParticleAnchorName, Title( "Anchor" )]
	public string AnchorName { get; set; }

	/// <summary>The value written - x y z, and a fourth for a slot or a colour's alpha.</summary>
	[Property]
	public ParticleVectorBinding Value { get; set; } = Vector3.Zero;

	[Property]
	public ParticleFloatBinding W { get; set; } = 1.0f;

	/// <summary>Whether the value multiplies what is there, rather than standing in for it.</summary>
	[Property]
	public bool Multiply { get; set; }
}

//"Stop spinning when it lands" is an Event on collision that sets a slot the Spin module multiplies by. "Pass the colour down the chain" is an Event on death that spawns the next effect with a Colour row. The module itself knows nothing of either - it is a trigger and a list of actions.
/// <summary>Something that happens to a particle at a moment: at birth, on a hit, on reaching its Goal, at a time, at death. What happens is written here - values set on the particle itself, and an effect spawned with the particle's facts handed over as that effect's parameters.</summary>
[Title( "Event" ), Category( "Update" ), Icon( "bolt" )]
public sealed class EventModule : ParticleModule, IParticleModuleStep, IParticleModuleComponents
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Update;

	[Property]
	public ParticleEventTrigger Trigger { get; set; } = ParticleEventTrigger.OnCollision;

	/// <summary>Seconds into the particle's life, for On Time.</summary>
	[Property, ShowIf( nameof( Trigger ), ParticleEventTrigger.OnTime )]
	public ParticleFloatBinding At { get; set; } = 1.0f;

	/// <summary>Seconds between firings after the first, for On Time; nought is once.</summary>
	[Property, ShowIf( nameof( Trigger ), ParticleEventTrigger.OnTime )]
	public float Every { get; set; } = 0.0f;

	/// <summary>How close to its Goal's target counts as reached.</summary>
	[Property, ShowIf( nameof( Trigger ), ParticleEventTrigger.OnGoalReached )]
	public float Distance { get; set; } = 0.05f;

	/// <summary>Whether this fires once per particle, or every time the trigger holds again.</summary>
	[Property, ShowIf( nameof( Trigger ), ParticleEventTrigger.OnCollision )]
	public bool Once { get; set; }

	/// <summary>How likely a firing is to do anything, nought to one.</summary>
	[Property, Range( 0.0f, 1.0f )]
	public float Chance { get; set; } = 1.0f;

	/// <summary>What is written onto the particle.</summary>
	[Property, InlineEditor, Group( "Set" )]
	public List<ParticleSetRow> Set { get; set; } = new();

	/// <summary>The effect played where the particle is, if any.</summary>
	[Property, Group( "Spawn" )]
	public ParticleDefinition Effect { get; set; }

	[Property, Group( "Spawn" )]
	public ParticleFloatBinding Scale { get; set; } = 1.0f;

	/// <summary>Whether the spawned effect faces the way the particle is going.</summary>
	[Property, Group( "Spawn" )]
	public bool AlignToVelocity { get; set; }

	/// <summary>Whether the spawned effect is carried along with the particle for as long as it lives.</summary>
	[Property, Group( "Spawn" )]
	public bool Follow { get; set; }

	/// <summary>The most spawned effects alive at once from this event.</summary>
	[Property, Group( "Spawn" )]
	public int MaxAlive { get; set; } = 16;

	/// <summary>What of the particle the spawned effect is told, as its parameters.</summary>
	[Property, InlineEditor, Group( "Spawn" ), Title( "Context" )]
	public List<ParticleContextRow> Context { get; set; } = new();

	public override void Apply( ParticleModuleContext context )
	{
		context.Runner?.Relay?.Note( this );
	}

	public void CreateComponents( GameObject target )
	{
		target.GetOrAddComponent<ParticleEventRelay>();
	}

	public void Step( ParticleModuleContext context, float delta )
	{
		var particle = context.Particle;

		if ( particle is null || Trigger == ParticleEventTrigger.OnDeath ) return;

		var bit = context.Runner?.EventBit( this ) ?? 1ul;
		var fired = ( particle.EventsFired & bit ) != 0;
		var fire = false;

		switch ( Trigger )
		{
			case ParticleEventTrigger.OnBirth:
				fire = !fired;
				break;

			case ParticleEventTrigger.EveryStep:
				fire = true;
				break;

			case ParticleEventTrigger.OnCollision:
				if ( particle.HitTime > particle.EventHitTime + 0.02f && Time.Now - particle.HitTime < 0.05f )
				{
					particle.EventHitTime = particle.HitTime;
					fire = !( Once && fired );
				}
				break;

			case ParticleEventTrigger.OnGoalReached:
				fire = !fired && particle.GoalDistance >= 0.0f && particle.GoalDistance <= Distance;
				break;

			case ParticleEventTrigger.OnTime:
			{
				var at = At.Evaluate( particle, context.Player );

				if ( !fired )
				{
					fire = particle.Age >= at;
				}
				else if ( Every > 0.0f )
				{
					//Fired already: fires again each time another Every has gone by - the step that crosses the next multiple.
					var since = particle.Age - at;
					var before = since - delta;

					fire = since >= 0.0f && MathF.Floor( since / Every ) > MathF.Floor( MathF.Max( before, 0.0f ) / Every );
				}
				break;
			}
		}

		if ( !fire ) return;

		particle.EventsFired |= bit;

		Fire( context, particle );
	}

	//Event fire / anchor / spawn-queue counters for PerfReport; racy adds are acceptable
	internal static long Fired;
	internal static long Anchored;
	internal static long Queued;

	//Do what the event does - the sets now, the spawn queued for the main thread.
	internal void Fire( ParticleModuleContext context, Particle particle )
	{
		if ( Chance < 1.0f && context.Random.Float( 0.0f, 1.0f ) > Chance ) return;

		Fired++;

		var player = context.Player;
		var definition = player?.Definition;

		foreach ( var row in Set )
		{
			if ( !row.Enabled ) continue;

			var xyz = row.Value.Evaluate( particle, player );
			var w = row.W.Evaluate( particle, player );

			switch ( row.Set )
			{
				case ParticleSetRow.What.Slot:
				{
					var index = definition?.SlotIndex( row.Slot ) ?? -1;
					if ( index < 0 ) break;

					var value = new Vector4( xyz, w );

					if ( row.Multiply ) value *= particle.Slot( index );

					particle.SetSlot( index, definition.Slots.Count, value );
					break;
				}

				case ParticleSetRow.What.Velocity:
					particle.Velocity = row.Multiply ? particle.Velocity * xyz : xyz;
					break;

				case ParticleSetRow.What.Color:
				{
					var colour = new Color( xyz.x, xyz.y, xyz.z, w );
					particle.Color = row.Multiply ? particle.Color * colour : colour;
					if ( !row.Multiply ) particle.Alpha = w;
					break;
				}

				case ParticleSetRow.What.Size:
					particle.Size = row.Multiply ? particle.Size * xyz : xyz;
					break;

				case ParticleSetRow.What.Angles:
					particle.Angles = row.Multiply ? new Angles( particle.Angles.pitch * xyz.x, particle.Angles.yaw * xyz.y, particle.Angles.roll * xyz.z ) : new Angles( xyz.x, xyz.y, xyz.z );
					break;

				case ParticleSetRow.What.LifeLeft:
					particle.DeathTime = particle.BornTime + particle.Age + MathF.Max( xyz.x, 0.0f );
					break;

				case ParticleSetRow.What.Anchor:
				{
					if ( player is null || string.IsNullOrEmpty( row.AnchorName ) ) break;

					//In the player's space, which is what an anchor override is - and the value row is an offset from the particle, in the world.
					var world = new Transform( particle.Position + xyz, particle.Angles.ToRotation(), particle.Size.x );

					Anchored++;
					player.SetAnchor( row.AnchorName, player.WorldTransform.ToLocal( world ) );
					break;
				}
			}
		}

		if ( Effect is null ) return;
		if ( context.Runner?.Relay is not { } relay ) return;

		Queued++;
		relay.Queue( this, particle, context );
	}
}

/// <summary>What an Event needs the main thread for: it steps on worker threads, and a spawned effect is a game object. This holds the queue, drains it each frame, keeps the spawned effects, and hands death to the events that wait on it.</summary>
[Hide]
[Expose]
[Title( "Particle Event Relay" )]
[Category( "Effects" )]
[Icon( "bolt" )]
public sealed class ParticleEventRelay : ParticleController
{
	readonly ConcurrentQueue<Pending> _pending = new();
	readonly Dictionary<EventModule, List<Child>> _spawned = new();
	readonly List<EventModule> _events = new();

	//Spawned effects that have played through, kept to be played again rather than built again - an effect is a tree
	//of objects and components, and a burst of sparks on every bounce is that tree made and thrown away each time
	readonly Dictionary<EventModule, Stack<ParticlePlayer>> _idle = new();

	//A spawned effect, the particle it follows, and the frame it began - it is not over before it has stepped once.
	record struct Child( GameObject Object, ParticlePlayer Player, Particle Particle, long Began );

	//The context rows' values come from a shared pool and go back once spawned: an event is often, and each wanted an array of its own.
	readonly record struct Pending( EventModule Event, Vector3 Position, Vector3 Velocity, Vector4[] Context, Particle Particle, float Scale );

	//An event module of this emitter, told so death can reach it.
	internal void Note( EventModule module )
	{
		if ( !_events.Contains( module ) ) _events.Add( module );
	}

	internal void Queue( EventModule module, Particle particle, ParticleModuleContext context )
	{
		//The facts read now, on this thread, while the particle is what the event saw: by the time the spawn happens it may have moved or gone.
		var rows = module.Context;
		var values = rows.Count == 0 ? System.Array.Empty<Vector4>() : System.Buffers.ArrayPool<Vector4>.Shared.Rent( rows.Count );

		for ( var i = 0; i < rows.Count; i++ )
		{
			var row = rows[i];
			values[i] = Vector4.Zero;

			if ( !row.Enabled ) continue;

			if ( row.FromSlot )
			{
				var index = context.Player?.Definition?.SlotIndex( row.Slot ) ?? -1;
				values[i] = index >= 0 ? particle.Slot( index ) : Vector4.Zero;
			}
			else
			{
				values[i] = RememberModule.Read( row.Source, particle, context );
			}
		}

		var scale = module.Scale.Evaluate( particle, context.Player );

		_pending.Enqueue( new Pending( module, particle.Position, particle.Velocity, values, particle, scale ) );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		while ( _pending.TryDequeue( out var pending ) )
		{
			Spawn( pending );

			if ( pending.Context.Length > 0 ) System.Buffers.ArrayPool<Vector4>.Shared.Return( pending.Context );
		}

		//Carried along: a spawned effect that follows its particle goes where it goes, and is let go where it dies.
		foreach ( var (module, list) in _spawned )
		{
			for ( var i = list.Count - 1; i >= 0; i-- )
			{
				var child = list[i];

				if ( !child.Object.IsValid() || !child.Player.IsValid() )
				{
					list.RemoveAt( i );
					continue;
				}

				//Played through: kept for the next spawn of this event
				if ( child.Began < EngineLoop.LoopFrames && !child.Player.IsActive )
				{
					list.RemoveAt( i );
					Idle( module ).Push( child.Player );
					continue;
				}

				if ( module.Follow && child.Particle is not null ) child.Object.WorldPosition = child.Particle.Position;
			}
		}
	}

	Stack<ParticlePlayer> Idle( EventModule module )
	{
		if ( !_idle.TryGetValue( module, out var idle ) ) _idle[module] = idle = new Stack<ParticlePlayer>();
		return idle;
	}

	void Spawn( in Pending pending )
	{
		var module = pending.Event;

		if ( module.Effect is null || !Scene.IsValid() ) return;

		if ( !_spawned.TryGetValue( module, out var list ) )
			_spawned[module] = list = new List<Child>();

		if ( list.Count >= module.MaxAlive ) return;

		var rotation = module.AlignToVelocity && !pending.Velocity.IsNearlyZero()
			? Rotation.LookAt( pending.Velocity.Normal )
			: Rotation.Identity;

		var player = Reused( module );
		var go = player?.GameObject;

		if ( player is null )
		{
			go = new GameObject( false, $"{module.Effect.ResourceName} (event)" );

			go.Flags = go.Flags
				.WithFlag( GameObjectFlags.Hidden, true )
				.WithFlag( GameObjectFlags.NotSaved, true )
				.WithFlag( GameObjectFlags.NotNetworked, true );

			player = go.AddComponent<ParticlePlayer>();

			player.Definition = module.Effect;
			player.PlayOnce = true;
		}

		go.WorldPosition = pending.Position;
		go.WorldRotation = rotation;
		go.WorldScale = pending.Scale;

		//The parent's facts as the child's parameters, by the row's kind: a float parameter takes one channel, a vector three, a colour four.
		var rows = module.Context;
		var definition = module.Effect;

		for ( var i = 0; i < rows.Count && i < pending.Context.Length; i++ )
		{
			var row = rows[i];
			if ( !row.Enabled || string.IsNullOrEmpty( row.Parameter ) ) continue;

			var v = pending.Context[i] * row.Scale;

			if ( Declares( definition.ColorParameters, row.Parameter ) )
				player.Set( row.Parameter, new Color( v.x, v.y, v.z, row.Scale == 1.0f ? v.w : pending.Context[i].w ) );
			else if ( Declares( definition.VectorParameters, row.Parameter ) )
				player.Set( row.Parameter, new Vector3( v.x, v.y, v.z ) );
			else
				player.Set( row.Parameter, ParticleFloatBinding.Channel( v, row.Channel ) );
		}

		if ( go.Enabled ) player.Restart();
		else go.Enabled = true;

		list.Add( new Child( go, player, module.Follow ? pending.Particle : null, EngineLoop.LoopFrames ) );
	}

	//A played-through effect of this event still standing, or null
	ParticlePlayer Reused( EventModule module )
	{
		if ( !_idle.TryGetValue( module, out var idle ) ) return null;

		while ( idle.TryPop( out var player ) )
		{
			if ( player.IsValid() && player.GameObject.IsValid() && player.Definition == module.Effect ) return player;
			if ( player.GameObject.IsValid() ) player.GameObject.Destroy();
		}

		return null;
	}

	static bool Declares<T>( List<T> parameters, string name ) where T : ParticleDefinition.Parameter
	{
		for ( var i = 0; i < parameters.Count; i++ )
		{
			if ( string.Equals( parameters[i].Name, name, StringComparison.OrdinalIgnoreCase ) ) return true;
		}

		return false;
	}

	protected override void OnParticleDestroyed( Particle p )
	{
		//A followed effect is let go where its particle died - the particle object goes back to the pool and would be somebody else's next.
		foreach ( var list in _spawned.Values )
		{
			for ( var i = 0; i < list.Count; i++ )
			{
				if ( ReferenceEquals( list[i].Particle, p ) ) list[i] = list[i] with { Particle = null };
			}
		}

		if ( _events.Count == 0 || !ParticleEffect.IsValid() ) return;

		var runner = ParticleEffect.GetComponent<ParticleModuleRunner>();
		var context = runner.IsValid() ? runner.Context( p ) : new ParticleModuleContext { Particle = p, Effect = ParticleEffect };

		foreach ( var module in _events )
		{
			if ( module.Enabled && module.Trigger == ParticleEventTrigger.OnDeath )
				module.Fire( context, p );
		}
	}

	protected override void OnDisabled()
	{
		base.OnDisabled();

		foreach ( var list in _spawned.Values )
		{
			foreach ( var child in list )
			{
				if ( child.Object.IsValid() ) child.Object.Destroy();
			}
		}

		foreach ( var idle in _idle.Values )
		{
			foreach ( var player in idle )
			{
				if ( player.IsValid() && player.GameObject.IsValid() ) player.GameObject.Destroy();
			}
		}

		_spawned.Clear();
		_idle.Clear();
	}
}
