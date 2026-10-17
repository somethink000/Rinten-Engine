using System.Diagnostics;
using System.Threading;

namespace Rinten;

/// <summary>Ticks the physics in FrameStage.PhysicsStep</summary>
[Expose]
sealed class ParticleGameSystem : GameObjectSystem
{
	private List<ParticleEffect.ParticleWork> workList = new( 64 );

	//Reused so we don't re-enumerate Scene.GetAll (iterator allocs) three times per frame
	private readonly List<ParticleEffect> _effects = new();

	public ParticleGameSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.FinishUpdate, 0, UpdateParticles, "UpdateParticles" );
	}

	//Particle step counters for PerfReport; cost per particle is the only comparable measure between runs
	internal static long Stepped;

	//How many effects those particles were spread over.
	internal static long Effects;

	//And how many frames stepped any at all.
	internal static long Steps;

	//How much processor time the steps took, and over how many workers - see UpdateParticles.
	internal static long StepTicks;
	internal static long StepRuns;

	void UpdateParticles()
	{
		using var __ = PerformanceStats.Timings.Particles.Scope();

		_effects.Clear();
		Scene.GetAll( _effects );
		if ( _effects.Count == 0 ) return;

		var timeDelta = MathX.Clamp( Time.Delta, 0.0f, 1.0f / 30.0f );
		var realTimeDelta = MathX.Clamp( RealTime.Delta, 0.0f, 1.0f / 30.0f );

		workList.Clear();

		//Camera view for step rates; without a camera (bake, preview, headless) everything steps every frame
		var look = Scene?.Detail ?? default;
		var looking = look.Looking && StepRates;

		var turn = EngineLoop.LoopFrames;
		var stepped = 0L;

		Asked += _effects.Count;

		var before = Rinten.Diagnostics.FrameProfiler.Push( "particle prestep" );

		foreach ( var p in _effects )
		{
			var delta = p.Timing switch
			{
				ParticleEffect.TimingMode.GameTime => timeDelta,
				ParticleEffect.TimingMode.RealTime => realTimeDelta,
				_ => timeDelta //default to GameTime
			};

			p.Owed += delta;

			//Distant effects step less often; owed time is carried, so they travel the same distance
			var every = looking ? RateFor( look, p ) : 1;

			//Staggered by identity, so a thousand effects on the same rate do not all land on the same frame and leave the ones between empty.
			if ( every > 1 && ( ( turn + p.StepPhase ) % every ) != 0 ) { Held++; continue; }

			var owed = p.Owed;
			p.Owed = 0.0f;

			if ( p.CountedSince != CountedFrom )
			{
				p.CountedSince = CountedFrom;
				p.StepTicks = p.StepParticles = 0;
			}

			p.StepParticles += p.Particles.Count;

			p.TryPreWarm();
			p.PreStep( owed );

			if ( p.CardStep() )
			{
				stepped += p.ParticleCount;
				continue;
			}

			p.CollectWork( workList );

			stepped += p.Particles.Count;
		}

		before.Dispose();

		Stepped += stepped;
		Effects += _effects.Count;
		Steps++;

		var work = Rinten.Diagnostics.FrameProfiler.Push( "particle work" );

		//Parallelise only with enough particles; Parallel.ForEach setup costs a few microseconds
		if ( stepped >= ParallelParticles && !Rinten.Utility.Parallel.InOrder )
		{
			//Timed per worker, not per item; Parallel beat a custom pool since its workers are already awake
			System.Threading.Tasks.Parallel.ForEach( workList,
				static () => Stopwatch.GetTimestamp(),
				( work, _, began ) => { ProcessWork( work ); return began; },
				static began =>
				{
					Interlocked.Add( ref StepTicks, Stopwatch.GetTimestamp() - began );
					Interlocked.Increment( ref StepRuns );
				} );
		}
		else
		{
			var began = Stopwatch.GetTimestamp();

			for ( var i = 0; i < workList.Count; i++ ) ProcessWork( workList[i] );

			StepTicks += Stopwatch.GetTimestamp() - began;
			StepRuns++;
		}

		work.Dispose();

		using var after = Rinten.Diagnostics.FrameProfiler.Push( "particle poststep" );

		foreach ( var p in _effects )
		{
			//Only the ones that stepped: the rest were not touched this frame and have nothing to spawn, nothing to push and nothing to put back.
			if ( p.Owed != 0.0f ) continue;

			p.SpawnDeferredParticleCollisionPrefabs();
			p.ApplyDeferredParticleForces();
			p.PostStep();
		}

		workList.Clear();
		_effects.Clear();
	}

	//How many particles are worth sharing out across threads - see UpdateParticles.
	const int ParallelParticles = 512;

	//0 steps every effect every frame, useful for comparing against step rates
	/// <summary>Whether an effect far from the eye is stepped less often.</summary>
	[ConVar( "particle_step_rates", Help = "Whether an effect far from the eye is stepped less often." )]
	internal static bool StepRates { get; set; } = true;

	//The rate is decided by CoreDetail.StepRate from screen coverage; particle_step_near was removed

	//How many effects were asked to step, and how many were held back.
	internal static long Asked;
	internal static long Held;

	//Frames between steps, from the screen coverage of the effect's bounds grown by its largest particle
	private static int RateFor( in CoreDetail.Look look, ParticleEffect effect )
	{
		//A thing still growing is stepped every frame whatever: its bounds are the bounds of what it
		//has emitted so far, which on the first frames of a burst is one particle at the emitter.
		if ( effect.ParticleCount == 0 ) return 1;

		var bounds = effect.ParticleBounds;
		var grown = bounds.Grow( MathF.Max( effect.MaxParticleSize, 0.0f ) );

		return CoreDetail.StepRate( look, CoreDetail.Coverage( look, grown ), CoreDetail.Seen( look, grown ) );
	}


	//We process the particles in chunks, in parallel. We don't do one particle at a time because
	//it'd spend more time doing all the admin of giving them to threads than it would actually take.
	private static void ProcessWork( ParticleEffect.ParticleWork work )
	{
		var began = Stopwatch.GetTimestamp();

		for ( int i = work.startIndex; i < work.endIndex; i++ )
		{
			work.effect.UpdateParticle( i );
		}

		Interlocked.Add( ref work.effect.StepTicks, Stopwatch.GetTimestamp() - began );
	}

	//Bumped when the report's window opens: each effect counts its steps afresh from there - see ParticleEffect.StepTicks
	internal static long CountedFrom;

	/// <summary>The effects of a scene that cost the most processor since the window opened: name, ticks, particles stepped.</summary>
	internal static List<(string Name, long Ticks, long Particles)> Costliest( Scene scene, int most )
	{
		var found = new List<ParticleEffect>();
		scene.GetAll( found );

		var named = new List<(string Name, long Ticks, long Particles)>();

		foreach ( var effect in found )
		{
			if ( effect.CountedSince != CountedFrom || effect.StepTicks <= 0 ) continue;

			var player = effect.GameObject?.Parent?.Components.Get<ParticlePlayer>();
			var name = player?.Definition is { } definition
				? $"{definition.ResourcePath} : {effect.GameObject.Name}"
				: effect.GameObject?.Name ?? "?";

			named.Add( (effect.OffCard is { } why ? $"{name} (processor: {why})" : name, effect.StepTicks, effect.StepParticles) );
		}

		named.Sort( ( a, b ) => b.Ticks.CompareTo( a.Ticks ) );
		if ( named.Count > most ) named.RemoveRange( most, named.Count - most );

		return named;
	}
}
