using System.Text.Json.Serialization;

namespace Rinten;

/// <summary>Runs an emitter's per-particle Initialize and IParticleModuleStep modules; built by ParticlePlayer</summary>
[Hide]
public sealed class ParticleModuleRunner : ParticleController
{
	/// <summary>The emitter this runs the modules of.</summary>
	[JsonIgnore, Hide]
	public ParticleDefinition.Emitter Data { get; set; }

	/// <summary>The player these modules read their parameters off.</summary>
	[JsonIgnore, Hide]
	public ParticlePlayer Player { get; set; }

	TimeSince _sinceStarted;

	//A context per thread, because the per-particle step is run across several of them - see ParticleController.OnParticleStep
	[ThreadStatic]
	static ParticleModuleContext _stepContext;

	//Apart from the step's: a module stepping a particle can make others, and that must not repoint the context it is being run with
	[ThreadStatic]
	static ParticleModuleContext _createContext;

	//How long this emitter runs for: its own duration where it has one, and the effect's where it does not.
	float Duration => Data is { Duration: > 0.0f } ? Data.Duration : Player?.Definition?.Duration ?? 0.0f;

	//The player's own IsActive is true while any one of its runners says no to this, which is what keeps a several-emitter effect alive until the last of them is done rather than the first.
	/// <summary>Past its duration, not looping, and nothing of it left alive.</summary>
	public bool IsFinished
		=> _sinceStarted > Duration
			&& !( ( Player?.Definition?.Looping ?? false ) && !( Player?.PlayOnce ?? false ) )
			&& ( ParticleEffect?.Particles.Count ?? 0 ) <= 0;

	//Called beside the native emitter's own reset. Without it, IsFinished would answer yes again on the very next frame however thoroughly the emitter had restarted, because the clock it reads had not moved.
	/// <summary>Put the clock back to zero.</summary>
	public void Restart()
	{
		_sinceStarted = 0.0f;
		_born = 0;
		_places = 0;
		_eventBits = null;
		Reseed();
	}

	/// <summary>What every random number this emitter draws comes from. Seeded from the player's seed and this emitter's place in the effect, so two emitters of one effect differ and a seek to the same second draws the same. A player with no seed draws a fresh one per build - the usual.</summary>
	public Random Random { get; private set; } = Random.Shared;

	void Reseed()
	{
		var seed = Player?.Seed ?? 0;

		if ( seed == 0 )
		{
			Random = new Random();
			return;
		}

		var index = Player?.Definition?.Emitters.IndexOf( Data ) ?? 0;

		Random = new Random( unchecked( seed * 397 + index * 7919 ) );
	}

	//How many this has made since it started - the next one's index.
	int _born;

	/// <summary>How many places the index runs over: the emitter's own number, else what the position module's shape has, else the burst.</summary>
	public int Places
	{
		get
		{
			if ( _places > 0 ) return _places;

			return _places = CountPlaces();
		}
	}

	int _places;

	//Counted once a run: the burst is evaluated when the emitter is reset, and the shapes do not move under it.
	int CountPlaces()
	{
		if ( Data is null ) return 1;
		if ( Data.Places > 0 ) return Data.Places;

		var burst = Emitter?.BurstCount ?? 0;

		foreach ( var module in Data.InitializeModules )
		{
			if ( module is InitializePositionModule { Enabled: true, Sample: ParticleShapeSample.ByIndex } position )
			{
				var shape = Player?.Definition?.Shape( position.ShapeName );

				if ( shape is not null ) return shape.Places( burst );
			}
		}

		return Math.Max( burst, 1 );
	}

	protected override void OnEnabled()
	{
		base.OnEnabled();

		Restart();
	}

	protected override void OnParticleCreated( Particle particle )
	{
		if ( Data is null ) return;

		particle.Position = ParticleEffect.IsValid() ? ParticleEffect.WorldPosition : particle.Position;

		//The seed every Rand() of this particle grows from: from this runner's random rather than the engine's, so a seeded player is seeded through.
		particle.RandomSeed = Random.Int( 0, 10000 );

		var places = Places;

		particle.Index = _born++;
		particle.IndexDelta = places <= 1 ? 0.0f : ( particle.Index % places ) / (float)( places - 1 );

		var context = Reused( ref _createContext, particle );

		foreach ( var module in Data.InitializeModules )
		{
			if ( module.Enabled ) module.Apply( context );
		}
	}

	protected override void OnParticleStep( Particle particle, float delta )
	{
		base.OnParticleStep( particle, delta );

		if ( Data is null ) return;

		var context = Reused( ref _stepContext, particle );

		//The engine's delta here is where the particle is in its life, nought to one - see ParticleEffect.OnStep.
		//A force wants seconds, so that is what a module is handed; the life fraction is on the particle.
		var seconds = ParticleEffect.StepSeconds( particle );

		//Owned by a module only for the step a module says so.
		particle.Kinematic = false;

		var steps = Steps;
		var state = _frameState;

		for ( var i = 0; i < steps.Length; i++ )
		{
			if ( !steps[i].Module.Enabled ) continue;

			context.Frame = state is not null && i < state.Length ? state[i] : null;

			steps[i].Step.Step( context, seconds );
		}

		context.Frame = null;
	}

	//Cached per-particle step modules in declaration order; rebuilt when the definition revision changes
	private (ParticleModule Module, IParticleModuleStep Step, IParticleModuleFrame Frame)[] Steps
	{
		get
		{
			var revision = Player?.Definition?.Revision ?? 0;

			if ( _steps is not null && ReferenceEquals( _stepsOf, Data ) && _stepsRevision == revision )
				return _steps;

			var found = new List<(ParticleModule, IParticleModuleStep, IParticleModuleFrame)>();

			foreach ( var module in Data.UpdateModules )
			{
				if ( module is IParticleModuleStep step ) found.Add( ( module, step, module as IParticleModuleFrame ) );
			}

			_stepsOf = Data;
			_stepsRevision = revision;
			_frameState = new object[found.Count];

			return _steps = found.ToArray();
		}
	}

	private (ParticleModule Module, IParticleModuleStep Step, IParticleModuleFrame Frame)[] _steps;
	private ParticleDefinition.Emitter _stepsOf;
	private int _stepsRevision;

	//Per-player frame state of each stepping module, written serially before workers read it
	private object[] _frameState;

	//What the whole effect shares this frame, worked out before its particles are handed out - see IParticleModuleFrame.
	protected override void OnBeforeStep( float delta )
	{
		base.OnBeforeStep( delta );

		if ( Data is null ) return;

		var steps = Steps;
		var any = false;

		for ( var i = 0; i < steps.Length; i++ )
		{
			if ( steps[i].Frame is not null && steps[i].Module.Enabled ) { any = true; break; }
		}

		if ( !any ) return;

		var context = _stepContext ??= new ParticleModuleContext();

		context.Particle = null;
		context.Effect = ParticleEffect;
		context.Emitter = Emitter;
		context.Player = Player;
		context.Runner = this;
		context.Frame = null;

		for ( var i = 0; i < steps.Length; i++ )
		{
			if ( steps[i].Frame is not { } frame || !steps[i].Module.Enabled ) continue;

			_frameState[i] = frame.Begin( context, delta, _frameState[i] );
		}
	}

	/// <summary>The emitter component making this runner's particles - found once, since the object is built once.</summary>
	public ParticleEmitter Emitter
	{
		get
		{
			if ( _emitter.IsValid() ) return _emitter;

			return _emitter = ParticleEffect.IsValid() ? ParticleEffect.GetComponent<ParticleEmitter>() : null;
		}
	}

	ParticleEmitter _emitter;

	/// <summary>The relay events spawn through, made the first time one is needed.</summary>
	public ParticleEventRelay Relay
	{
		get
		{
			if ( _relay.IsValid() ) return _relay;

			return _relay = ParticleEffect.IsValid() ? ParticleEffect.GetOrAddComponent<ParticleEventRelay>() : null;
		}
	}

	ParticleEventRelay _relay;

	/// <summary>Each Event module's bit in a particle's <see cref="Particle.EventsFired"/> - the module's place among the emitter's events. Kept here rather than on the module: a module is the definition's, shared by every player of the effect and by the editor, and holds nothing of any one run.</summary>
	public ulong EventBit( EventModule module )
	{
		//Build the map fully, then publish; workers on the pool read it concurrently
		var bits = System.Threading.Volatile.Read( ref _eventBits );

		if ( bits is null )
		{
			bits = new Dictionary<EventModule, ulong>( ReferenceEqualityComparer.Instance );

			var at = 0;

			foreach ( var each in Data?.UpdateModules ?? new List<ParticleModule>() )
			{
				if ( each is EventModule found ) bits[found] = 1ul << Math.Min( at++, 63 );
			}

			System.Threading.Volatile.Write( ref _eventBits, bits );
		}

		return bits.TryGetValue( module, out var bit ) ? bit : 1ul << 63;
	}

	Dictionary<EventModule, ulong> _eventBits;

	/// <summary>A context for this runner, with everything but the particle filled in.</summary>
	//The thread's own context, pointed at this particle: made and stepped particles are many a frame, and a module only reads it while applied
	ParticleModuleContext Reused( ref ParticleModuleContext slot, Particle particle )
	{
		var context = slot ??= new ParticleModuleContext();

		context.Particle = particle;
		context.Effect = ParticleEffect;
		context.Emitter = Emitter;
		context.Player = Player;
		context.Runner = this;
		context.Frame = null;

		return context;
	}

	public ParticleModuleContext Context( Particle particle ) => new()
	{
		Particle = particle,
		Effect = ParticleEffect,
		Emitter = Emitter,
		Player = Player,
		Runner = this,
	};
}
