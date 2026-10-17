namespace Rinten;

public sealed partial class ParticleEffect
{
	//This effect's run on the card where it has one - see OnCard
	internal ParticleGpuRun Card;

	//What OnCard last said and when, as it reads several components and is not worth asking every frame
	private bool onCard;
	private long onCardAskedAt = -1000;

	//Whether everything this effect does has a version on the card - particles.slang - and the card can run it. A particle a
	//game reaches into, one that collides, follows a bone or is drawn as anything but a sprite stays on the processor
	private bool OnCard()
	{
		if ( EngineLoop.LoopFrames - onCardAskedAt < 30 ) return onCard;
		onCardAskedAt = EngineLoop.LoopFrames;

		OffCard = WhyNotOnCard();

		return onCard = OffCard is null;
	}

	/// <summary>Why the processor steps this effect rather than the card, as OnCard last found; null where the card does.</summary>
	internal string OffCard { get; private set; }

	private string WhyNotOnCard()
	{
		if ( !ParticleGpuRun.Possible ) return "no compute here";

		if ( Collision ) return "collides";
		if ( UsePrefabFeature ) return "makes prefabs";
		if ( SheetSequence ) return "plays a sheet";
		if ( SnapToFrame ) return "snaps to frames";
		if ( Force && !ForceDirection.IsNearlyZero() ) return "has a force";
		if ( !OrbitalForce.IsNearlyZero() || !OrbitalPull.IsNearlyZero() ) return "has an orbital force";
		if ( !ConstantMovement.IsNearlyZero() ) return "drifts";
		if ( !LocalSpace.IsPlainConstant || LocalSpace.ConstantValue != 0.0f ) return "moves in local space";
		if ( !PerParticleTimeScale.IsPlainConstant ) return "has a time scale per particle";
		if ( _boneTargets.Count > 0 ) return "follows bones";

		//Only the runner steps each particle, and only with modules the card has
		if ( OnStep is not { HasSingleTarget: true } step ) return "something besides its runner steps its particles";
		if ( step.Target is not ParticleModuleRunner { Data: { } data } ) return "no runner";

		var orbits = 0;
		var follows = 0;
		var attractors = 0;
		var vortices = 0;

		foreach ( var module in data.UpdateModules )
		{
			if ( !module.Enabled ) continue;

			var carried = module switch
			{
				DragModule => true,
				OrbitModule orbit => orbit.Axis.Uniform && orbit.Speed.Uniform && ++orbits == 1,
				FollowShapeModule follow => Followed( follow, data ) && ++follows == 1,
				AttractorModule attract => attract.Position.Uniform && attract.Strength.Uniform && ++attractors == 1,
				VortexModule vortex => vortex.Axis.Uniform && vortex.Strength.Uniform && ++vortices == 1,
				GravityModule gravity => gravity.Force.Uniform,
				SpinModule spin => Fixed( spin.Speed ),
				CurlNoiseModule curl => curl.Strength.Uniform && curl.Scale.Uniform && curl.TimeScale.Uniform,
				_ => false,
			};

			if ( !carried ) return module.GetType().Name;
		}

		if ( Components.Get<ParticleTrailRenderer>() is not null ) return "has a trail";
		if ( Components.Get<ParticleTextRenderer>() is not null ) return "is text";
		if ( Components.Get<ParticleModelEmitter>() is not null ) return "emits from a model";

		if ( Components.Get<ParticleSpriteRenderer>() is not { } sprite ) return "no sprite";
		if ( sprite.IsSorted ) return "sorted";
		if ( sprite.MotionBlur ) return "motion blur";
		if ( sprite.Shadows ) return "casts shadows";
		if ( sprite.MaterialOverride is not null ) return "its own material";
		if ( sprite.Alignment == ParticleSpriteRenderer.BillboardAlignment.Object ) return "turns with the object";

		return null;
	}

	//A shape whose place depends on how far along it alone, followed by numbers no particle changes - see ParticleGpuRun's shape table
	private bool Followed( FollowShapeModule follow, ParticleDefinition.Emitter data )
	{
		if ( follow.AlignToShape || !follow.Speed.Uniform || !follow.Weight.Uniform ) return false;
		if ( follow.Along.HasSlot || ( follow.Along.IsBound && follow.Along.Mode != ParticleBindingMode.Multiply ) ) return false;
		if ( follow.Stagger.HasSlot || !Fixed( follow.Stagger.Value ) ) return false;

		var definition = Components.Get<ParticleModuleRunner>()?.Player?.Definition;
		if ( definition?.Shape( follow.ShapeName ) is not { } shape ) return false;

		return shape.Kind switch
		{
			ParticleShape.ShapeKind.Circle or ParticleShape.ShapeKind.Cone or ParticleShape.ShapeKind.Spline or ParticleShape.ShapeKind.Helix => true,
			ParticleShape.ShapeKind.Line => string.IsNullOrEmpty( shape.LineStartAnchor ) && string.IsNullOrEmpty( shape.LineEndAnchor ),
			_ => false,
		};
	}

	//A vector the same for a particle all its life: read once as it is made, so nothing in it may run over its life
	private static bool Fixed( ParticleVectorBinding value )
	{
		if ( value.HasSlot ) return false;

		return Fixed( value.Value.X ) && Fixed( value.Value.Y ) && Fixed( value.Value.Z );
	}

	private static bool Fixed( in ParticleFloat value )
		=> value.IsPlainConstant || value.Evaluation is ParticleFloat.EvaluationType.Seed or ParticleFloat.EvaluationType.Index;

	/// <summary>Steps this effect on the card where it can be: what it made since the last step goes over, and nothing of it stays here. False where it is stepped on the processor.</summary>
	internal bool CardStep()
	{
		if ( !OnCard() )
		{
			LeaveCard();
			return false;
		}

		if ( Card is null || !Card.Fits( this ) )
		{
			Card?.Dispose();
			Card = new ParticleGpuRun( this );
		}

		foreach ( var p in Particles )
		{
			Card.Take( p, p.RandomSeed );
			Particle.Pool.Enqueue( p );
		}

		Particles.Clear();

		Card.Step( _timeDelta );

		return true;
	}

	//What the card holds goes with it
	private void LeaveCard()
	{
		Card?.Dispose();
		Card = null;
	}
}
