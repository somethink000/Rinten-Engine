using System.Text.Json.Serialization;

namespace Rinten;

//The shape is drawn here rather than left to one of the engine's shaped emitters, because those also decide the outgoing velocity and the rate. This only places the particle; a point emitter is what actually makes it.
/// <summary>Where a particle starts, relative to the emitter.</summary>
[Title( "Position" ), Category( "Initialize" ), Icon( "place" )]
public sealed class InitializePositionModule : ParticleModule, IParticleModuleComponents
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Initialize;

	/// <summary>The volume the particle is placed somewhere in.</summary>
	public enum SpawnShape
	{
		Point,
		Sphere,
		Box,
		Cone,
		Circle,
		Line
	}

	/// <summary>What of the emitter's own transform the shape is measured in.</summary>
	[Flags]
	public enum ShapeTransform
	{
		None = 0,

		/// <summary>The shape turns with the emitter.</summary>
		Rotation = 1,

		/// <summary>The shape grows with the emitter.</summary>
		Scale = 2
	}

	/// <summary>One of the effect's shapes to be born on, instead of the volume written here. Empty is the volume below, which is the shape old files have.</summary>
	[Property, ParticleShapeName, Title( "Effect Shape" )]
	public string ShapeName { get; set; }

	/// <summary>How the effect shape is walked: anywhere on it, the k-th of its places for the k-th particle, or where it is at <see cref="Along"/>.</summary>
	[Property, ShowIf( nameof( UsesEffectShape ), true )]
	public ParticleShapeSample Sample { get; set; } = ParticleShapeSample.Random;

	/// <summary>Where on the shape, nought to one, when sampled by time.</summary>
	[Property, ShowIf( nameof( Sample ), ParticleShapeSample.ByTime )]
	public ParticleFloatBinding Along { get; set; } = 0.0f;

	/// <summary>Whether the particle is turned to face the way the shape does where it was born.</summary>
	[Property, ShowIf( nameof( UsesEffectShape ), true )]
	public bool AlignToShape { get; set; }

	[Hide, JsonIgnore]
	public bool UsesEffectShape => !string.IsNullOrEmpty( ShapeName );

	[Property, ShowIf( nameof( UsesEffectShape ), false )]
	public SpawnShape Shape { get; set; } = SpawnShape.Sphere;

	/// <summary>Where the shape sits, from the emitter. Turned with the emitter like the shape is, so a splash born a storey under a cloud stays under it.</summary>
	[Property]
	public Vector3 Offset { get; set; } = Vector3.Zero;

	[Property]
	public ShapeTransform Follows { get; set; } = ShapeTransform.Rotation | ShapeTransform.Scale;

	//One ShowIf per shape with a radius; multiple ShowIfs combine as "or"
	[Property, Range( 0.0f, 100.0f )]
	[ShowIf( nameof( Shape ), SpawnShape.Sphere )]
	[ShowIf( nameof( Shape ), SpawnShape.Circle )]
	[ShowIf( nameof( Shape ), SpawnShape.Cone )]
	public float Radius { get; set; } = 0.5f;

	[Property, ShowIf( nameof( Shape ), SpawnShape.Box )]
	public Vector3 BoxSize { get; set; } = new( 1.0f, 1.0f, 1.0f );

	[Property, Range( 0.0f, 180.0f ), ShowIf( nameof( Shape ), SpawnShape.Cone )]
	public float ConeAngle { get; set; } = 45.0f;

	/// <summary>Place the particle on the surface of the shape rather than anywhere inside it.</summary>
	[Property]
	public bool OnShell { get; set; }

	[Property, ShowIf( nameof( Shape ), SpawnShape.Line )]
	public Vector3 LineStart { get; set; } = Vector3.Zero;

	[Property, ShowIf( nameof( Shape ), SpawnShape.Line )]
	public Vector3 LineEnd { get; set; } = Vector3.Up;

	/// <summary>A point emitter, so there is something making particles at all - this module is what says where they go.</summary>
	public void CreateComponents( GameObject target )
	{
		var emitter = target.AddComponent<ParticleSphereEmitter>();

		emitter.Radius = 0.0f;
		emitter.Velocity = 0.0f;
		emitter.Burst = 0.0f;
		emitter.Rate = 0.0f;
	}

	public override void Apply( ParticleModuleContext context )
	{
		if ( context.Particle is null ) return;

		var particle = context.Particle;
		var shape = UsesEffectShape ? context.Player?.Definition?.Shape( ShapeName ) : null;

		Vector3 offset;
		Vector3 normal = Vector3.Up;

		if ( shape is not null )
		{
			//In the world already, frame and anchor applied; the module's own offset goes on in the emitter's space like the inline shape's.
			var places = shape.Places( context.Emitter?.BurstCount ?? 1 );
			var along = Sample == ParticleShapeSample.ByTime ? Along.Evaluate( particle, context.Player ) : 0.0f;

			//A piece of a broken model: the particle is told which, and the pose is that piece's - by
			//index the k-th, at random any, so the two agree and the mesh drawn is the one born here.
			var part = shape.PartFor( Sample, particle.Index, places, along );
			var sample = Sample;

			if ( part >= 0 )
			{
				particle.Part = part;
				sample = ParticleShapeSample.ByTime;
				along = shape.Model.MeshCount <= 1 ? 0.0f : part / (float)( shape.Model.MeshCount - 1 );
			}

			var pose = shape.SampleWorld( context.Player, sample, particle.Index, places, along, Follows, context.Random );

			var nudge = Offset;

			if ( context.Player.IsValid() )
			{
				if ( ( Follows & ShapeTransform.Scale ) != 0 ) nudge *= context.Player.WorldScale;
				if ( ( Follows & ShapeTransform.Rotation ) != 0 ) nudge = context.Player.WorldRotation * nudge;
			}

			particle.Position = pose.Position + nudge;

			if ( AlignToShape && !pose.Normal.IsNearlyZero() )
				particle.Angles = Rotation.LookAt( pose.Normal ).Angles();

			return;
		}
		else
		{
			offset = Offset + Shape switch
			{
				SpawnShape.Sphere => InSphere( context.Random ),
				SpawnShape.Box => InBox( context.Random ),
				SpawnShape.Cone => InCone( context.Random ),
				SpawnShape.Circle => InCircle( context.Random ),
				SpawnShape.Line => OnLine( context.Random ),
				_ => Vector3.Zero
			};
		}

		if ( context.Player.IsValid() )
		{
			if ( ( Follows & ShapeTransform.Scale ) != 0 )
				offset *= context.Player.WorldScale;

			if ( ( Follows & ShapeTransform.Rotation ) != 0 )
			{
				offset = offset.RotateAround( Vector3.Zero, context.Player.WorldRotation );
				normal = context.Player.WorldRotation * normal;
			}
		}

		particle.Position += offset;

		if ( shape is not null && AlignToShape && !normal.IsNearlyZero() )
			particle.Angles = Rotation.LookAt( normal ).Angles();
	}

	Vector3 InSphere( Random random )
	{
		var radius = OnShell ? Radius : random.Float( 0.0f, Radius );

		return random.VectorInSphere().Normal * radius;
	}

	Vector3 InBox( Random random )
	{
		var half = BoxSize * 0.5f;

		if ( !OnShell )
		{
			return new Vector3(
				random.Float( -half.x, half.x ),
				random.Float( -half.y, half.y ),
				random.Float( -half.z, half.z ) );
		}

		var u = random.Float( 0.0f, 1.0f );
		var v = random.Float( 0.0f, 1.0f );

		return random.Int( 0, 5 ) switch
		{
			0 => new Vector3( -half.x, MathX.Lerp( -half.y, half.y, u ), MathX.Lerp( -half.z, half.z, v ) ),
			1 => new Vector3( half.x, MathX.Lerp( -half.y, half.y, u ), MathX.Lerp( -half.z, half.z, v ) ),
			2 => new Vector3( MathX.Lerp( -half.x, half.x, u ), -half.y, MathX.Lerp( -half.z, half.z, v ) ),
			3 => new Vector3( MathX.Lerp( -half.x, half.x, u ), half.y, MathX.Lerp( -half.z, half.z, v ) ),
			4 => new Vector3( MathX.Lerp( -half.x, half.x, u ), MathX.Lerp( -half.y, half.y, v ), -half.z ),
			_ => new Vector3( MathX.Lerp( -half.x, half.x, u ), MathX.Lerp( -half.y, half.y, v ), half.z )
		};
	}

	Vector3 InCone( Random random )
	{
		var angle = random.Float( 0.0f, 360.0f ).DegreeToRadian();
		var distance = random.Float( 0.0f, Radius );
		var mouth = MathF.Tan( ConeAngle.DegreeToRadian() ) * distance;
		var radius = OnShell ? mouth : random.Float( 0.0f, mouth );

		//Up the cone is up the world, which here is Y - see Vector3.Up.
		return new Vector3( MathF.Cos( angle ) * radius, distance, MathF.Sin( angle ) * radius );
	}

	Vector3 InCircle( Random random )
	{
		var angle = random.Float( 0.0f, 360.0f ).DegreeToRadian();
		var radius = OnShell ? Radius : random.Float( 0.0f, Radius );

		//Flat on the ground, which is the XZ plane in a world where Y is up.
		return new Vector3( MathF.Cos( angle ) * radius, 0.0f, MathF.Sin( angle ) * radius );
	}

	Vector3 OnLine( Random random ) => Vector3.Lerp( LineStart, LineEnd, random.Float( 0.0f, 1.0f ) );
}

/// <summary>Which way a particle is thrown, and how hard.</summary>
[Title( "Velocity" ), Category( "Initialize" ), Icon( "air" )]
public sealed class InitializeVelocityModule : ParticleModule
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Initialize;

	/// <summary>The direction and speed every particle leaves with.</summary>
	[Property]
	public ParticleVectorBinding Velocity { get; set; } = Vector3.Up * 2.0f;

	/// <summary>Speed thrown in a random direction on top of that.</summary>
	[Property]
	public ParticleFloatBinding Scatter { get; set; } = 0.0f;

	/// <summary>Whether the direction turns with the emitter.</summary>
	[Property]
	public bool LocalSpace { get; set; }

	/// <summary>Whether the emitter's own movement is added to what it throws.</summary>
	[Property]
	public bool InheritEmitterVelocity { get; set; }

	[Property, ShowIf( nameof( InheritEmitterVelocity ), true )]
	public float InheritScale { get; set; } = 1.0f;

	//Movement rather than a force: it does not accumulate, so drag and gravity never cancel it out.
	/// <summary>A drift added to the particle every frame, on top of whatever its velocity has become - wind, or a rising column of smoke.</summary>
	[Property]
	public ParticleVector3 Drift { get; set; }

	public override void Apply( ParticleModuleContext context )
	{
		if ( !context.Effect.IsValid() ) return;

		context.Effect.ConstantMovement = Drift;

		var velocity = Velocity.Evaluate( context.Particle, context.Player );

		if ( LocalSpace && context.Player.IsValid() )
			velocity = velocity.RotateAround( Vector3.Zero, context.Player.WorldRotation );

		if ( InheritEmitterVelocity && context.Player.IsValid() )
			velocity += context.Player.Velocity * InheritScale;

		context.Effect.StartVelocity = Scatter.Resolve( context.Player );
		context.Effect.InitialVelocity = velocity;
	}
}

/// <summary>How long a particle lives for.</summary>
[Title( "Lifetime" ), Category( "Initialize" ), Icon( "schedule" )]
public sealed class InitializeLifetimeModule : ParticleModule
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Initialize;

	[Property, Range( 0.0f, 100.0f )]
	public ParticleFloatBinding Lifetime { get; set; } = 2.0f;

	public override void Apply( ParticleModuleContext context )
	{
		if ( context.Effect.IsValid() )
			context.Effect.Lifetime = Lifetime.Resolve( context.Player );
	}
}

/// <summary>How big a particle is drawn.</summary>
[Title( "Size" ), Category( "Initialize" ), Icon( "photo_size_select_small" )]
public sealed class InitializeSizeModule : ParticleModule
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Initialize;

	[Property, Range( 0.0f, 10.0f )]
	public ParticleFloatBinding Size { get; set; } = 0.25f;

	//The engine already multiplies the particle's own scale by the object's, so keeping the size the same across a scaled emitter is the case that needs doing something about - the size is divided back out.
	/// <summary>Whether a scaled-up emitter makes bigger particles.</summary>
	[Property]
	public bool InheritEmitterScale { get; set; } = true;

	public override void Apply( ParticleModuleContext context )
	{
		if ( !context.Effect.IsValid() ) return;

		context.Effect.ApplyShape = true;

		var scale = context.Player.IsValid() ? context.Player.WorldScale.x : 1.0f;

		context.Effect.Scale = InheritEmitterScale || scale.AlmostEqual( 0.0f )
			? Size.Resolve( context.Player )
			: Size.Scaled( context.Player, 1.0f / scale );
	}
}

/// <summary>How far a particle is stretched along the way it is moving.</summary>
[Title( "Stretch" ), Category( "Initialize" ), Icon( "open_in_full" )]
public sealed class InitializeStretchModule : ParticleModule
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Initialize;

	[Property, Range( 0.0f, 10.0f )]
	public ParticleFloatBinding Stretch { get; set; } = 1.0f;

	public override void Apply( ParticleModuleContext context )
	{
		if ( !context.Effect.IsValid() ) return;

		context.Effect.ApplyShape = true;
		context.Effect.Stretch = Stretch.Resolve( context.Player );
	}
}

/// <summary>What colour a particle is, over its life.</summary>
[Title( "Color" ), Category( "Initialize" ), Icon( "palette" )]
public sealed class InitializeColorModule : ParticleModule
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Initialize;

	[Property]
	public ParticleColorBinding Color { get; set; } = global::Color.White;

	/// <summary>How see-through the particle is, over its life.</summary>
	[Property, Range( 0.0f, 1.0f )]
	public ParticleFloatBinding Alpha { get; set; } = 1.0f;

	/// <summary>What the colour is multiplied by - over one is what makes a particle glow into the bloom.</summary>
	[Property, Range( 0.0f, 16.0f )]
	public ParticleFloatBinding Brightness { get; set; } = 1.0f;

	/// <summary>A flat colour multiplied into the gradient, for tinting a whole effect at once.</summary>
	[Property]
	public Color Tint { get; set; } = global::Color.White;

	public override void Apply( ParticleModuleContext context )
	{
		if ( !context.Effect.IsValid() ) return;

		context.Effect.ApplyColor = true;
		context.Effect.ApplyAlpha = true;
		context.Effect.Gradient = Color?.Evaluate( context.Player ) ?? global::Color.White;
		context.Effect.Alpha = Alpha.Resolve( context.Player );
		context.Effect.Brightness = Brightness.Resolve( context.Player );
		context.Effect.Tint = Tint;
	}
}

/// <summary>Which way up a particle starts.</summary>
[Title( "Rotation" ), Category( "Initialize" ), Icon( "rotate_90_degrees_ccw" )]
public sealed class InitializeRotationModule : ParticleModule
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Initialize;

	[Property]
	public ParticleVector3 Rotation { get; set; } = Vector3.Zero;

	public override void Apply( ParticleModuleContext context )
	{
		if ( context.Particle is null ) return;

		context.Particle.Angles = new Angles( Rotation.Evaluate( context.Particle, 0 ) );
	}
}

//Zero leaves it where it was made and the emitter moves out from under it; one drags it along, so the whole effect moves as a piece.
/// <summary>How much a particle is carried along by the emitter after it is born.</summary>
[Title( "Local Space" ), Category( "Initialize" ), Icon( "transform" )]
public sealed class InitializeLocalSpaceModule : ParticleModule
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Initialize;

	[Property, Range( 0.0f, 1.0f )]
	public ParticleFloatBinding LocalSpace { get; set; } = 0.0f;

	public override void Apply( ParticleModuleContext context )
	{
		if ( context.Effect.IsValid() )
			context.Effect.LocalSpace = LocalSpace.Resolve( context.Player );
	}
}

/// <summary>Which frame of a sprite sheet a particle plays, and how fast.</summary>
[Title( "Sprite Sheet" ), Category( "Initialize" ), Icon( "burst_mode" )]
public sealed class SpriteSheetModule : ParticleModule
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Initialize;

	[Property]
	public ParticleFloatBinding Sequence { get; set; } = 0.0f;

	[Property]
	public ParticleFloatBinding Speed { get; set; } = 1.0f;

	[Property]
	public ParticleFloatBinding Time { get; set; } = 0.0f;

	public override void Apply( ParticleModuleContext context )
	{
		if ( !context.Effect.IsValid() ) return;

		context.Effect.SheetSequence = true;
		context.Effect.SequenceId = Sequence.Resolve( context.Player );
		context.Effect.SequenceSpeed = Speed.Resolve( context.Player );
		context.Effect.SequenceTime = Time.Resolve( context.Player );
	}
}

/// <summary>What happens when a particle hits the world.</summary>
[Title( "Collision" ), Category( "Initialize" ), Icon( "sports_soccer" )]
public sealed class ParticleCollisionModule : ParticleModule
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Initialize;

	/// <summary>Tags a particle passes straight through.</summary>
	[Property]
	public TagSet Ignore { get; set; } = new();

	/// <summary>How far from a surface a particle counts as having hit it.</summary>
	[Property]
	public ParticleFloatBinding Radius { get; set; } = 0.05f;

	/// <summary>How likely a particle is to die where it lands.</summary>
	[Property, Range( 0.0f, 1.0f )]
	public ParticleFloatBinding DieOnHitChance { get; set; } = 0.0f;

	/// <summary>Things to leave behind where a particle lands - a decal, a splash.</summary>
	[Property]
	public List<GameObject> Prefabs { get; set; } = new();

	/// <summary>How likely a hit is to leave one.</summary>
	[Property, Range( 0.0f, 1.0f )]
	public ParticleFloatBinding PrefabChance { get; set; } = 1.0f;

	/// <summary>How far round the surface normal what is left behind is turned.</summary>
	[Property]
	public ParticleFloatBinding PrefabRotation { get; set; } = 0.0f;

	/// <summary>Whether what is left behind is laid flat against the surface.</summary>
	[Property]
	public bool PrefabAlign { get; set; }

	/// <summary>How much of its speed a particle keeps when it bounces.</summary>
	[Property, Range( 0.0f, 1.0f ), Header( "Surface" )]
	public ParticleFloatBinding Bounce { get; set; } = 1.0f;

	/// <summary>How much a particle sliding along a surface is slowed by it.</summary>
	[Property, Range( 0.0f, 1.0f )]
	public ParticleFloatBinding Friction { get; set; } = 1.0f;

	/// <summary>How much a bounce is scattered, for a surface that is not flat.</summary>
	[Property, Range( 0.0f, 1.0f )]
	public ParticleFloatBinding Bumpiness { get; set; } = 0.0f;

	/// <summary>How hard a particle is pushed out of anything it ends up inside.</summary>
	[Property, Range( 0.0f, 10.0f )]
	public ParticleFloatBinding PushStrength { get; set; } = 0.0f;

	public override void Apply( ParticleModuleContext context )
	{
		if ( !context.Effect.IsValid() ) return;

		var effect = context.Effect;

		effect.Collision = true;
		effect.CollisionIgnore = Ignore;
		effect.CollisionRadius = Radius.Evaluate( context.Player );
		effect.DieOnCollisionChance = DieOnHitChance.Resolve( context.Player );

		effect.CollisionPrefab = Prefabs;
		effect.CollisionPrefabChance = PrefabChance.Resolve( context.Player );
		effect.CollisionPrefabRotation = PrefabRotation.Resolve( context.Player );
		effect.CollisionPrefabAlign = PrefabAlign;

		effect.Bounce = Bounce.Resolve( context.Player );
		effect.Friction = Friction.Resolve( context.Player );
		effect.Bumpiness = Bumpiness.Resolve( context.Player );
		effect.PushStrength = PushStrength.Resolve( context.Player );

		if ( Prefabs is { Count: > 0 } )
			effect.UsePrefabFeature = true;
	}
}

//Cheaper than lowering the rate where the rate is what gives the effect its shape - a burst that should be ragged rather than smaller.
/// <summary>Kills some of the particles as they are born, to thin an effect out.</summary>
[Title( "Random Kill" ), Category( "Initialize" ), Icon( "casino" )]
public sealed class RandomKillModule : ParticleModule
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Initialize;

	/// <summary>How likely a particle is not to make it.</summary>
	[Property, Range( 0.0f, 1.0f )]
	public float Chance { get; set; } = 0.5f;

	public override void Apply( ParticleModuleContext context )
	{
		if ( context.Particle is null ) return;

		if ( context.Random.Float( 0.0f, 1.0f ) < Chance )
			context.Particle.Age = float.MaxValue;
	}
}

//Separate from InitializeRotationModule, which turns a particle once as it is born and then leaves it alone. This drives all three angles every frame, so what a curve says is what the particle is at that point of its life - and it overrides anything else that turns it.
/// <summary>Which way a particle faces over its life, rather than where it started.</summary>
[Title( "Rotation Over Life" ), Category( "Initialize" ), Icon( "3d_rotation" )]
public sealed class RotationOverLifeModule : ParticleModule
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Initialize;

	[Property]
	public ParticleFloatBinding Pitch { get; set; } = 0.0f;

	[Property]
	public ParticleFloatBinding Yaw { get; set; } = 0.0f;

	[Property]
	public ParticleFloatBinding Roll { get; set; } = 0.0f;

	public override void Apply( ParticleModuleContext context )
	{
		if ( !context.Effect.IsValid() ) return;

		context.Effect.ApplyRotation = true;
		context.Effect.Pitch = Pitch.Resolve( context.Player );
		context.Effect.Yaw = Yaw.Resolve( context.Player );
		context.Effect.Roll = Roll.Resolve( context.Player );
	}
}

//A prefab per particle, following it for as long as it lives - a sound, a light with its own behaviour, anything that is more than the particle system can draw. Expensive per particle, which is what Chance is for.
/// <summary>Objects that ride along with the particles.</summary>
[Title( "Follower Prefab" ), Category( "Initialize" ), Icon( "attachment" )]
public sealed class FollowerPrefabModule : ParticleModule
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Initialize;

	/// <summary>What to spawn. One is picked per particle.</summary>
	[Property]
	public List<GameObject> Prefabs { get; set; } = new();

	/// <summary>How likely a particle is to get one.</summary>
	[Property, Range( 0.0f, 1.0f )]
	public ParticleFloatBinding Chance { get; set; } = 1.0f;

	/// <summary>Whether the follower goes when the particle does.</summary>
	[Property]
	public bool DieWithParticle { get; set; } = true;

	public override void Apply( ParticleModuleContext context )
	{
		if ( !context.Effect.IsValid() ) return;

		context.Effect.FollowerPrefab = Prefabs;
		context.Effect.FollowerPrefabChance = Chance.Resolve( context.Player );
		context.Effect.FollowerPrefabKill = DieWithParticle;

		if ( Prefabs is { Count: > 0 } )
			context.Effect.UsePrefabFeature = true;
	}
}
