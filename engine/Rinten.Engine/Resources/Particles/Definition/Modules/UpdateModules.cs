namespace Rinten;

/// <summary>A steady pull in one direction.</summary>
[Title( "Gravity" ), Category( "Update" ), Icon( "arrow_downward" )]
public sealed class GravityModule : ParticleModule, IParticleModuleStep, IParticleModuleFrame
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Update;

	//Worth saying once for the whole module set: every distance and speed a module starts at is metric, because this engine counts in metres. The numbers a legacy effect is written in are inches and about forty times larger, which is a particle the size of a room leaving at highway speed.
	/// <summary>Units per second per second - and a unit is a metre, so the default is what things actually fall at.</summary>
	[Property]
	public ParticleVectorBinding Force { get; set; } = Vector3.Down * 9.8f;

	public override void Apply( ParticleModuleContext context ) { }

	public void Step( ParticleModuleContext context, float delta )
		=> context.Particle.Velocity += ( context.Frame is Held held ? held.Force : Force.Evaluate( context.Particle, context.Player ) ) * delta;

	//The force worked out once an effect where no particle changes it, not two lookups by name a particle
	private sealed class Held
	{
		internal Vector3 Force;
	}

	public object Begin( ParticleModuleContext context, float delta, object was )
	{
		if ( !Force.Uniform ) return null;

		var held = was as Held ?? new Held();
		held.Force = Force.Evaluate( null, context.Player );

		return held;
	}
}

//Handed to the effect rather than stepped here: damping is one of the things the engine's own simulation already does, and doing it there means it happens with everything else that moves a particle rather than after all of it.
/// <summary>Air resistance - what stops a particle rather than turning it.</summary>
[Title( "Drag" ), Category( "Update" ), Icon( "air" )]
public sealed class DragModule : ParticleModule
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Update;

	/// <summary>How much of the speed is lost every second.</summary>
	[Property, Range( 0.0f, 10.0f )]
	public ParticleFloatBinding Damping { get; set; } = 0.1f;

	public override void Apply( ParticleModuleContext context )
	{
		if ( context.Effect.IsValid() )
			context.Effect.Damping = Damping.Resolve( context.Player );
	}
}

/// <summary>Turns a particle as it goes.</summary>
[Title( "Spin" ), Category( "Update" ), Icon( "rotate_right" )]
public sealed class SpinModule : ParticleModule, IParticleModuleStep
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Update;

	/// <summary>Degrees per second, per axis.</summary>
	[Property]
	public ParticleVectorBinding Speed { get; set; } = Vector3.Zero;

	public override void Apply( ParticleModuleContext context ) { }

	public void Step( ParticleModuleContext context, float delta )
		=> context.Particle.Angles += Speed.Evaluate( context.Particle, context.Player ) * delta;
}

//For anything with a front - a model, a stretched sprite. A billboard does not need it.
/// <summary>Points a particle the way it is travelling.</summary>
[Title( "Face Velocity" ), Category( "Update" ), Icon( "navigation" )]
public sealed class FaceVelocityModule : ParticleModule, IParticleModuleStep
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Update;

	public override void Apply( ParticleModuleContext context ) { }

	public void Step( ParticleModuleContext context, float delta )
	{
		var velocity = context.Particle.Velocity;

		if ( velocity.IsNearZeroLength ) return;

		context.Particle.Angles = Rotation.LookAt( velocity ).Angles();
	}
}

/// <summary>Where the particle positions are measured from.</summary>
[Expose]
public enum ParticleSpace
{
	/// <summary>Relative to the emitter.</summary>
	Local,

	/// <summary>An absolute place in the world.</summary>
	World
}

/// <summary>A point that pulls particles towards it - full strength inside its own size, falling away outside it.</summary>
[Title( "Attractor" ), Category( "Update" ), Icon( "my_location" )]
public sealed class AttractorModule : ParticleModule, IParticleModuleStep, IParticleModuleFrame
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Update;

	[Property]
	public ParticleSpace Space { get; set; } = ParticleSpace.Local;

	[Property]
	public ParticleVectorBinding Position { get; set; } = Vector3.Zero;

	/// <summary>How hard it pulls, in units per second per second.</summary>
	[Property, Range( 0.0f, 100.0f )]
	public ParticleFloatBinding Strength { get; set; } = 5.0f;

	/// <summary>How far out the pull is at full strength.</summary>
	[Property, Range( 0.01f, 100.0f )]
	public float Size { get; set; } = 0.5f;

	/// <summary>Push rather than pull.</summary>
	[Property]
	public bool Invert { get; set; }

	/// <summary>How sharply the pull falls away past the edge. 1 is linear, 2 is inverse square, higher is sharper.</summary>
	[Property, Range( 0.1f, 8.0f )]
	public float Falloff { get; set; } = 2.0f;

	public override void Apply( ParticleModuleContext context ) { }

	public void Step( ParticleModuleContext context, float delta )
	{
		var held = context.Frame as EmitterFrame;
		var origin = held?.Point ?? Position.Evaluate( context.Particle, context.Player );

		//Local means the emitter's own frame: turned with it as well as carried by it, so a point beside a pitched emitter stays beside it.
		if ( held is null && Space == ParticleSpace.Local && context.Emitter.IsValid() )
			origin = context.Emitter.WorldPosition + ( context.Emitter.WorldRotation * origin );
		else if ( held is { Point: null, Local: true } )
			origin = held.Emitter.Position + ( held.Emitter.Rotation * origin );

		var toward = origin - context.Particle.Position;
		var distance = toward.Length;

		if ( distance < 0.01f ) return;

		var strength = 1.0f;

		if ( distance > Size )
		{
			//How many of its own radii past the edge we are: zero at the surface, growing outward.
			var excess = ( distance - Size ) / Size;

			strength = 1.0f / MathF.Pow( 1.0f + excess, Falloff );
		}

		//A push is the pull the other way, with the same falloff - not the falloff turned inside out, which pushed nothing near and everything far.
		if ( Invert ) toward = -toward;

		context.Particle.Velocity += toward.Normal * ( held?.Amount ?? Strength.Evaluate( context.Player ) ) * strength * delta;
	}

	public object Begin( ParticleModuleContext context, float delta, object was )
	{
		var held = EmitterFrame.Of( context, Space, was );

		held.Point = Position.Uniform ? held.ToWorld( Position.Evaluate( null, context.Player ) ) : null;
		held.Amount = Strength.Uniform ? Strength.Evaluate( context.Player ) : null;

		return held;
	}
}

/// <summary>A swirl about an axis - particles orbit rather than fall in.</summary>
[Title( "Vortex" ), Category( "Update" ), Icon( "cyclone" )]
public sealed class VortexModule : ParticleModule, IParticleModuleStep, IParticleModuleFrame
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Update;

	[Property]
	public ParticleSpace Space { get; set; } = ParticleSpace.Local;

	[Property]
	public Vector3 Center { get; set; } = Vector3.Zero;

	[Property]
	public ParticleVectorBinding Axis { get; set; } = Vector3.Up;

	[Property, Range( 0.0f, 1000.0f )]
	public ParticleFloatBinding Strength { get; set; } = 5.0f;

	public override void Apply( ParticleModuleContext context ) { }

	public void Step( ParticleModuleContext context, float delta )
	{
		var held = context.Frame as EmitterFrame;
		var center = held?.Point ?? Center;
		var axis = held?.Axis ?? Axis.Evaluate( context.Particle, context.Player ).Normal;

		//Local means the emitter's own frame: the axis turns with the emitter, so a portal stood on its side still spins in its own plane.
		if ( held is null && Space == ParticleSpace.Local && context.Emitter.IsValid() )
		{
			center = context.Emitter.WorldPosition + ( context.Emitter.WorldRotation * center );
			axis = context.Emitter.WorldRotation * axis;
		}
		else if ( held is { Axis: null, Local: true } )
		{
			axis = held.Emitter.Rotation * axis;
		}

		var outward = context.Particle.Position - center;
		var distance = outward.Length;

		if ( distance < 0.01f ) return;
		var tangent = Vector3.Cross( axis, outward.Normal );

		context.Particle.Velocity += tangent * ( ( held?.Amount ?? Strength.Evaluate( context.Player ) ) / distance ) * delta;
	}

	public object Begin( ParticleModuleContext context, float delta, object was )
	{
		var held = EmitterFrame.Of( context, Space, was );

		held.Point = held.ToWorld( Center );
		held.Axis = Axis.Uniform ? held.TurnToWorld( Axis.Evaluate( null, context.Player ).Normal ) : null;
		held.Amount = Strength.Uniform ? Strength.Evaluate( context.Player ) : null;

		return held;
	}
}

//The curl of a noise field, which is divergence-free by construction: whatever flows into a region flows out of it again. The field itself is a handful of sines rather than proper Perlin noise - cheap, and at these scales the difference does not show.
/// <summary>A flow field that swirls without ever pushing particles together - what makes smoke look like smoke rather than like a spray.</summary>
[Title( "Curl Noise" ), Category( "Update" ), Icon( "waves" )]
public sealed class CurlNoiseModule : ParticleModule, IParticleModuleStep, IParticleModuleFrame
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Update;

	/// <summary>How hard the flow pushes.</summary>
	[Property, Range( 0.0f, 100.0f )]
	public ParticleFloatBinding Strength { get; set; } = 1.0f;

	/// <summary>How big the swirls are - smaller numbers make wider ones.</summary>
	[Property, Range( 0.01f, 10.0f )]
	public ParticleFloatBinding Scale { get; set; } = 1.0f;

	/// <summary>How fast the field itself moves.</summary>
	[Property, Range( 0.0f, 10.0f )]
	public ParticleFloatBinding TimeScale { get; set; } = 1.0f;

	/// <summary>Where in the field this effect sits, so two of them differ.</summary>
	[Property]
	public Vector3 Offset { get; set; } = Vector3.Zero;

	public override void Apply( ParticleModuleContext context ) { }

	public void Step( ParticleModuleContext context, float delta )
	{
		var particle = context.Particle;
		var held = context.Frame as Held;

		var scale = held?.Scale ?? Scale.Evaluate( context.Player );
		var timeScale = held?.TimeScale ?? TimeScale.Evaluate( context.Player );
		var strength = held?.Strength ?? Strength.Evaluate( context.Player );

		var position = ( particle.Position + Offset ) * scale;
		var time = particle.Age * timeScale;

		particle.Velocity += Curl( position, time ) * strength * delta;
	}

	//The three numbers worked out once for the effect where they are plain numbers, read as they are per particle otherwise
	private sealed class Held
	{
		internal float? Scale, TimeScale, Strength;
	}

	public object Begin( ParticleModuleContext context, float delta, object was )
	{
		var held = was as Held ?? new Held();

		held.Scale = Scale.Uniform ? Scale.Evaluate( context.Player ) : null;
		held.TimeScale = TimeScale.Uniform ? TimeScale.Evaluate( context.Player ) : null;
		held.Strength = Strength.Uniform ? Strength.Evaluate( context.Player ) : null;

		return held;
	}

	//The potential field's exact curl: the field is a sum of sines of lines, whose gradients are cosines
	static Vector3 Curl( Vector3 position, float time )
	{
		var x = Gradient( position, time );
		var y = Gradient( position + new Vector3( 31.416f, -47.853f, 12.793f ), time );
		var z = Gradient( position + new Vector3( -17.737f, 86.214f, -59.482f ), time );

		return new Vector3( z.y - y.z, x.z - z.x, y.x - x.y );
	}

	//Noise's gradient at the point moved by time along (1, 0.7, 0.5); Noise is 0.5 sin(x + 1.3y) + 0.3 sin(1.7y + 0.9z)
	//+ 0.2 sin(2.1z + 1.1x) + 0.15 sin(3.7x + 2.3y + 1.9z)
	static Vector3 Gradient( Vector3 position, float time )
	{
		var p = position + new Vector3( time, time * 0.7f, time * 0.5f );

		var a = MathF.Cos( p.x + p.y * 1.3f ) * 0.5f;
		var b = MathF.Cos( p.y * 1.7f + p.z * 0.9f ) * 0.3f;
		var c = MathF.Cos( p.z * 2.1f + p.x * 1.1f ) * 0.2f;
		var d = MathF.Cos( p.x * 3.7f + p.y * 2.3f + p.z * 1.9f ) * 0.15f;

		return new Vector3(
			a + ( c * 1.1f ) + ( d * 3.7f ),
			( a * 1.3f ) + ( b * 1.7f ) + ( d * 2.3f ),
			( b * 0.9f ) + ( c * 2.1f ) + ( d * 1.9f ) );
	}
}

//Rotates position about the axis at a fixed rate instead of applying a force
/// <summary>Carries the particles round an axis at a steady rate, whatever else they are doing - a ring that stays a ring.</summary>
[Title( "Orbit" ), Category( "Update" ), Icon( "autorenew" )]
public sealed class OrbitModule : ParticleModule, IParticleModuleStep, IParticleModuleFrame
{
	public override ParticleModuleStage Stage => ParticleModuleStage.Update;

	[Property]
	public ParticleSpace Space { get; set; } = ParticleSpace.Local;

	/// <summary>Where the axis passes through, from the emitter or in the world.</summary>
	[Property]
	public Vector3 Center { get; set; } = Vector3.Zero;

	/// <summary>Which way the axis points. Up is a carousel.</summary>
	[Property]
	public ParticleVectorBinding Axis { get; set; } = Vector3.Up;

	/// <summary>Degrees a second. Negative goes the other way round.</summary>
	[Property, Range( -720.0f, 720.0f )]
	public ParticleFloatBinding Speed { get; set; } = 90.0f;

	public override void Apply( ParticleModuleContext context ) { }

	public void Step( ParticleModuleContext context, float delta )
	{
		var held = context.Frame as EmitterFrame;
		var center = held?.Point ?? Center;
		var axis = held?.Axis ?? Axis.Evaluate( context.Particle, context.Player );

		if ( held is null && Space == ParticleSpace.Local && context.Emitter.IsValid() )
		{
			center = context.Emitter.WorldPosition + ( context.Emitter.WorldRotation * center );
			axis = context.Emitter.WorldRotation * axis;
		}
		else if ( held is { Axis: null, Local: true } )
		{
			axis = held.Emitter.Rotation * axis;
		}

		if ( axis.IsNearlyZero() ) return;

		var degrees = ( held?.Amount ?? Speed.Evaluate( context.Particle, context.Player ) ) * delta;
		if ( degrees == 0.0f ) return;

		var turn = Rotation.FromAxis( axis.Normal, degrees );
		var particle = context.Particle;

		particle.Position = center + ( turn * ( particle.Position - center ) );
		particle.Velocity = turn * particle.Velocity;
	}

	public object Begin( ParticleModuleContext context, float delta, object was )
	{
		var held = EmitterFrame.Of( context, Space, was );

		held.Point = held.ToWorld( Center );
		held.Axis = Axis.Uniform ? held.TurnToWorld( Axis.Evaluate( null, context.Player ) ) : null;
		held.Amount = Speed.Uniform ? Speed.Evaluate( context.Player ) : null;

		return held;
	}
}

//What a module about a point of the emitter's holds for the effect's step: the emitter where it stands this frame - read
//once, not walked up its parents for every particle - and whichever of its point, axis and amount no particle changes
internal sealed class EmitterFrame
{
	internal bool Local;
	internal Transform Emitter;
	internal Vector3? Point;
	internal Vector3? Axis;
	internal float? Amount;

	internal static EmitterFrame Of( ParticleModuleContext context, ParticleSpace space, object was )
	{
		var held = was as EmitterFrame ?? new EmitterFrame();

		held.Local = space == ParticleSpace.Local && context.Emitter.IsValid();
		held.Emitter = held.Local ? context.Emitter.WorldTransform : Transform.Zero;

		return held;
	}

	internal Vector3 ToWorld( Vector3 point ) => Local ? Emitter.Position + ( Emitter.Rotation * point ) : point;

	internal Vector3 TurnToWorld( Vector3 direction ) => Local ? Emitter.Rotation * direction : direction;
}
