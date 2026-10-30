using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Rinten;

[Expose]
public partial class Particle : IDynamicFloatContext
{
	[ActionGraphInclude]
	public Vector3 Position;

	[ActionGraphInclude]
	public Vector3 Size;

	[ActionGraphInclude]
	public Vector3 Velocity;

	//The last non-zero velocity, used by velocity aligned billboards to keep their orientation when the particle stops moving.
	internal Vector3 LastVelocity;

	public Color Color;
	public Color OverlayColor;
	public float Alpha;
	public float BornTime;
	public float Age;
	public float Radius;
	public Angles Angles;
	public int Sequence;

	/// <summary>Which one this is: the order it was born in, from nought, per emitter. See ParticleShape's By Index.</summary>
	public int Index;

	/// <summary>Where <see cref="Index"/> is between nought and one, over the emitter's places - what a curve read By Index is given.</summary>
	public float IndexDelta;

	/// <summary>Which piece of a model this stands for, where it was born on one by parts; -1 is none. See ParticleShape.PartFor.</summary>
	public int Part = -1;

	/// <summary>Which events have fired for this particle, one bit per Event module - what "once" is kept by.</summary>
	public ulong EventsFired;

	/// <summary>The hit an Event last saw, so a collision fires once per hit and not once per frame lying on the floor.</summary>
	public float EventHitTime;

	/// <summary>How far this is from its Goal's target, written by the Goal module every step; -1 is no goal.</summary>
	public float GoalDistance = -1.0f;

	/// <summary>What this particle remembers - the effect's declared slots, by number. Null until something writes one. See ParticleDefinition.Slots.</summary>
	public Vector4[] Slots;

	/// <summary>Whether a module owns this particle's position for the step: the engine then neither pushes it with forces, moves it by its velocity, nor collides it. Set by a module every step it wants it - it is cleared before the modules run - so a Goal at weight nought hands the particle back to the simulation.</summary>
	public bool Kinematic;

	/// <summary>What the drivers keep between steps: where the simulation alone would have the particle, and what a Transform Over Life last added - so each step applies only the change. See GoalModule.</summary>
	public Vector3 DriverFree;
	public Vector3 DriverFreeVelocity;
	public Vector3 DriverShown;
	public bool DriverHeld;
	public Vector3 DriverOffset;
	public Angles DriverAngles;
	public float DriverScale;
	public bool DriverStarted;

	/// <summary>The slot, or nothing where it was never written.</summary>
	public Vector4 Slot( int index ) => Slots is not null && index >= 0 && index < Slots.Length ? Slots[index] : Vector4.Zero;

	/// <summary>Writes a slot, making room the first time.</summary>
	public void SetSlot( int index, int count, Vector4 value )
	{
		if ( index < 0 ) return;

		if ( Slots is null || Slots.Length < count )
		{
			var grown = new Vector4[Math.Max( count, index + 1 )];
			Slots?.CopyTo( grown, 0 );
			Slots = grown;
		}

		Slots[index] = value;
	}
	public Vector3 SequenceTime;
	public int Frame;
	public Angles StartAngles;
	public float StartScale;

	/// <summary>What every Rand() of this particle grows from. The engine draws one; a seeded player draws its own - see ParticleModuleRunner.</summary>
	public int RandomSeed;
	internal bool hasUpdated;

	//Set by the parallel update when the particle reached the end of its life; swept in PostStep
	internal bool IsDead;
	internal bool IsReleased;
	internal uint Generation;

	[System.Obsolete]
	public float Random01;

	[System.Obsolete]
	public float Random02;

	[System.Obsolete]
	public float Random03;

	[System.Obsolete]
	public float Random04;

	[System.Obsolete]
	public float Random05;

	[System.Obsolete]
	public float Random06;

	[System.Obsolete]
	public float Random07;

	[ActionGraphInclude]
	public Vector3 HitPos;
	[ActionGraphInclude]
	public Vector3 HitNormal;

	public float HitTime;
	public float LastHitTime;
	public Vector3 StartPosition;

	/// <summary>A range from 0 to 1 descriving how long this particle has been alive</summary>
	public float LifeDelta;

	/// <summary>The time that this particle is scheduled to die</summary>
	public float DeathTime;


	public float TimeScale;

	//A GameObject that is following us. Might be emitting other particles or something.
	internal GameObject Follower;

	//When >= 0, local space simulation follows this bone on AttachTarget.
	internal int AttachBoneIndex = -1;

	//The skinned model this particle follows in local space, paired with AttachBoneIndex.
	internal SkinnedModelRenderer AttachTarget;


	public float LifeTimeRemaining => DeathTime - BornTime;

	float IDynamicFloatContext.LifetimeDelta => LifeDelta;
	int IDynamicFloatContext.RandomSeed => RandomSeed;
	float IDynamicFloatContext.IndexDelta => IndexDelta;

	private Dictionary<string, object> _data;

	//Taken and given back from the effects' steps, which run on several threads at once
	/// <summary>Particles ended and kept for the next one made.</summary>
	public static readonly System.Collections.Concurrent.ConcurrentQueue<Particle> Pool = new();

	public static Particle Create()
	{
		if ( !Pool.TryDequeue( out Particle p ) )
		{
			p = new Particle();
		}

		p.RandomSeed = Random.Shared.Int( 0, 10000 );
		p.BornTime = Time.Now;
		p.Age = 0;
		p.Angles = Angles.Zero;
		p.StartAngles = Angles.Zero;
		p.Frame = 0;
		p.Velocity = 0;
		p.LastVelocity = 0;
		p.Color = Color.White;
		p.OverlayColor = Color.White.WithAlpha( 0 );
		p.Alpha = 1;
		p.Sequence = 0;
		p.Index = 0;
		p.IndexDelta = 0;
		p.Part = -1;
		p.EventsFired = 0;
		p.EventHitTime = -1000.0f;
		p.GoalDistance = -1.0f;
		p.Kinematic = false;
		p.DriverFree = default;
		p.DriverFreeVelocity = default;
		p.DriverShown = default;
		p.DriverHeld = false;
		p.DriverOffset = default;
		p.DriverAngles = default;
		p.DriverScale = 1.0f;
		p.DriverStarted = false;
		if ( p.Slots is not null ) Array.Clear( p.Slots );
		p.SequenceTime = 0;
		p.Size = 5;
		p.StartScale = 1;
		p.HitTime = -1000;
		p.LastHitTime = -1000;
		p.TimeScale = 1;
		p._data?.Clear();
		p._controllers?.Clear();
		p.hasUpdated = false;
		p.IsDead = false;
		p.IsReleased = false;
		p.Generation++;
		p.AttachBoneIndex = -1;
		p.AttachTarget = null;

		return p;
	}

	/// <summary>Get an arbituary data value</summary>
	public T Get<T>( string key )
	{
		if ( _data is null ) return default;

		if ( !_data.TryGetValue( key, out var val ) )
			return default;

		return (T)val;
	}

	/// <summary>Set an arbituary data value</summary>
	public void Set<T>( string key, T tvalue )
	{
		_data ??= new Dictionary<string, object>( 4, StringComparer.OrdinalIgnoreCase );
		_data[key] = tvalue;
	}

	public void ApplyDamping( in float amount )
	{
		//Below 0.1 m/s drag becomes a fixed amount so slow particles come to rest
		Velocity = Velocity.WithFriction( amount, 0.1f );
	}

	internal bool MoveWithCollision( in float bounce, in float friction, in float bumpiness, in float push, in bool die, in float dt, float radius, in SceneTrace trace, ConcurrentBag<ParticleEffect.DeferredParticleForce> deferredForces )
	{
		const float surfaceOffset = 0.1f;

		//We previously hit something. Keep the surface normal out of our velocity Periodically check whether it's still there.
		if ( HitTime > 0 )
		{
			//if time passed, or we moved too far, see if it's still there Four tenths of a metre - the sixteen units this was written as.
			bool recheck = HitTime < Time.Now - 0.1f || HitPos.Distance( Position ) > 0.4f;

			if ( recheck )
			{
				var checkTrace = trace.Ray( Position, Position + HitNormal * surfaceOffset * -2.0f )
								.Radius( radius * Radius )
								.Run();

				if ( checkTrace.Hit )
				{
					HitPos = checkTrace.HitPosition;
					HitNormal = checkTrace.Normal;
					HitTime = Time.Now;
				}
				else
				{
					HitTime = 0;
					HitPos = 0;
					HitNormal = 0;
				}
			}

			if ( HitTime > 0 )
			{
				LastHitTime = Time.Now;
				//Keep removing the ground velocity
				Velocity = Velocity.SubtractDirection( HitNormal );
			}

		}

		if ( LastHitTime > Time.Now - 0.03f )
		{
			ApplyDamping( friction * dt * 5.0f );
		}

		var targetPosition = Position + Velocity * dt;

		var tr = trace.Ray( Position, targetPosition )
										.Radius( radius * Radius )
										.Run();
		if ( !tr.Hit )
		{
			Position = targetPosition;
			return false;
		}

		//If we want to die on collision then set its age to max
		if ( die )
		{
			Age = float.MaxValue;
		}

		//If we have push, then push the physics object we hit
		if ( push != 0 && tr.Body is not null )
		{
			deferredForces?.Add( new( tr.Body, tr.HitPosition, Velocity * tr.Body.Mass * push ) );
		}

		HitPos = tr.HitPosition;
		HitNormal = tr.Normal;
		HitTime = Time.Now;

		var velocity = Velocity;
		var speed = Velocity.Length;

		var surfaceNormal = tr.Normal;

		//make the hit normal bumpy if we have bumpiness
		if ( speed > 10f && bumpiness > 0 )
		{
			surfaceNormal += Vector3.Random * bumpiness * 0.5f;
		}

		var surfaceVelocityNormal = velocity.SubtractDirection( surfaceNormal, 1 + bounce ).Normal;

		targetPosition = tr.EndPosition;//+ tr.Normal * surfaceOffset;

		Velocity = surfaceVelocityNormal * speed;

		if ( bounce > 0 && Velocity.Dot( tr.Normal ) > 5.0f )
		{
			HitTime = 0;
		}

		Position = targetPosition;
		return true;
	}


	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public float Rand( int seed = 0, [CallerLineNumber] int line = 0 )
	{
		int i = RandomSeed + (line * 20) + seed;
		return Game.Random.FloatDeterministic( i );
	}
}
