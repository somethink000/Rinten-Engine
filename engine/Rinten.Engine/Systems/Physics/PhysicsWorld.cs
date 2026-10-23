using NativeEngine;
using System.Runtime.InteropServices;
using static Rinten.PhysicsWorld;

namespace Rinten;

/// <summary>Physics simulation mode. For use with <see cref="PhysicsWorld.SimulationMode"/>.</summary>
public enum PhysicsSimulationMode
{
	/// <summary>Discrete collision detection. In this mode physics bodies can fly through thin walls when moving very quickly, but it is has better performance.</summary>
	Discrete,

	/// <summary>Continuous collision detection. This is the default mode.</summary>
	Continuous
};

/// <summary>A world in which physics objects exist. You can create your own world but you really don't need to. A world for the map is created clientside and serverside automatically.</summary>
[Expose, ActionGraphIgnore]
public sealed partial class PhysicsWorld : IHandle
{
	[SkipHotload]
	internal static HashSet<PhysicsWorld> All = new HashSet<PhysicsWorld>();

	//The simulation underneath this world. Everything the world does goes through here, so which physics engine is running is one assignment.
	internal IPhysicsWorldBackend Backend => backend;
	private IPhysicsWorldBackend backend;

	private bool BackendValid => backend?.IsValid ?? false;
	HashSet<PhysicsBody> bodies = new HashSet<PhysicsBody>();

	/// <summary>All bodies in the world</summary>
	public IEnumerable<PhysicsBody> Bodies => bodies.Where( x => x.IsValid() );

	//Every body, unfiltered and without an iterator to make - check each IsValid
	internal HashSet<PhysicsBody> BodySet => bodies;

	internal int BodyCount => bodies.Count;

	//public Action<int, PhysicsBody, PhysicsBody, Vector3> Internal_OnCollision;

	/// <summary>Set or retrieve the collision rules for this <see cref="PhysicsWorld"/>.</summary>
	public CollisionRules CollisionRules { get; set; }

	void IHandle.HandleDestroy()
	{
		backend = null;
		All.Remove( this );
	}
	void IHandle.HandleInit( IntPtr ptr )
	{
		//A world is created by our own core in the constructor below, not handed to us by anyone.
	}
	bool IHandle.HandleValid() => BackendValid;
	internal PhysicsWorld( HandleCreationData _ ) { }

	internal bool IsTransient { get; set; }

	/// <summary>Create a new physics world. You should only do this if you want to simulate an extra world for some reason.</summary>
	public PhysicsWorld() : this( ScenePhysicsMode.Physics3D )
	{
	}

	/// <summary>Create a new physics world of the given kind: Box3D for <see cref="ScenePhysicsMode.Physics3D"/>, Box2D in the XY plane for <see cref="ScenePhysicsMode.Physics2D"/>.</summary>
	public PhysicsWorld( ScenePhysicsMode mode )
	{
		IsTransient = true;
		Mode = mode;

		//Our core creates the simulation up front and hands it back, rather than creating it natively and
		//calling us to bind it. Which one is the only thing that differs between the two kinds of world.
		backend = mode == ScenePhysicsMode.Physics2D
			? new Box2DWorldBackend( this )
			: new CorePhysicsWorldBackend( this );

		//No reference body up front: our core makes it the first time something asks, rather than needing one to exist to be bound to.
		gravity = backend.GetGravity();
		All.Add( this );

		using ( var h = IHandle.MakeNextHandle( this ) )
		{
		//No-op: nothing to do here
		}
	}

	/// <summary>Which simulation this world runs.</summary>
	public ScenePhysicsMode Mode { get; }

	/// <summary>True when this world simulates in the XY plane.</summary>
	public bool Is2D => Mode == ScenePhysicsMode.Physics2D;

	/// <summary>Temp function for creating model physics until entity system handles it</summary>
	public PhysicsGroup SetupPhysicsFromModel( Model model, PhysicsMotionType motionType )
	{
		return backend.CreateAggregateInstance( model, Transform.Zero, 0, motionType );
	}

	/// <summary>Temp function for creating model physics until entity system handles it</summary>
	public PhysicsGroup SetupPhysicsFromModel( Model model, Transform transform, PhysicsMotionType motionType )
	{
		return backend.CreateAggregateInstance( model, transform, 0, motionType );
	}

	/// <summary>Delete this world and all objects inside. Will throw an exception if you try to delete a world that you didn't manually create.</summary>
	public void Delete()
	{
		Assert.True( IsTransient );
		if ( !BackendValid ) return;

		backend.Destroy( this );

		backend = null;
		All.Remove( this );
	}

	/// <summary>Step simulation of this physics world. You can only do this on physics worlds that you manually create.</summary>
	public void Step( float delta ) => Step( delta, 1 );


	internal double CurrentTime;
	internal float CurrentDelta;

	/// <summary>Step simulation of this physics world. You can only do this on physics worlds that you manually create.</summary>
	public unsafe void Step( float delta, int subSteps )
	{
		CurrentTime += delta;
		Step( CurrentTime, delta, subSteps * SubSteps );
	}

	/// <summary>Step simulation of this physics world. You can only do this on physics worlds that you manually create.</summary>
	public void Step( double worldTime, float delta, int subSteps )
	{
		Assert.True( IsTransient, "You can only step simulation of physics worlds that you create" );
		if ( !BackendValid ) return;

		UpdateCollisionRulesHash();

		CurrentTime = worldTime;
		CurrentDelta = delta;

		backend.StepSimulation( delta, subSteps * SubSteps );

		ProcessIntersections();
	}

	private int _collisionRulesHash;
	private void UpdateCollisionRulesHash()
	{
		if ( CollisionRules is null )
			return;

		var hash = CollisionRules.GetHashCode();
		if ( _collisionRulesHash == hash )
			return;

		var json = Json.SerializeAsObject( CollisionRules );
		backend.SetCollisionRulesFromJson( json.ToJsonString() );
		_collisionRulesHash = hash;
	}

	internal void ProcessIntersections()
	{
		backend.ProcessIntersections();
	}

	internal enum IntersectionEventType_t
	{
		TouchBegin,
		TouchEnd,
		TouchPersists,
		Hit,
		TriggerBegin,
		TriggerEnd,
	}

	internal Action<PhysicsIntersection> OnIntersectionStart { get; set; }
	internal Action<PhysicsIntersection> OnIntersectionHit { get; set; }
	internal Action<PhysicsIntersectionEnd> OnIntersectionEnd { get; set; }
	internal Action<PhysicsIntersection> OnIntersectionUpdate { get; set; }
	internal Action<PhysicsBody> OnBodyOutOfBounds { get; set; }
	internal Action<PhysicsBody> OnBodyFellAsleep { get; set; }

	Vector3 gravity;

	/// <summary>Access the world's current gravity.</summary>
	[ActionGraphInclude]
	public Vector3 Gravity
	{
		get => gravity;
		set
		{
			if ( gravity == value ) return;

			gravity = value;
			backend.SetGravity( gravity );
		}
	}

	float airDensity;

	/// <summary>Air density of this physics world, for things like air drag.</summary>
	[ActionGraphInclude]
	public float AirDensity
	{
		get => airDensity;
		set
		{
			if ( airDensity == value ) return;

			airDensity = value;
		}
	}

	PhysicsBody _cachedWorldBody;

	/// <summary>The body of this physics world.</summary>
	[ActionGraphInclude]
	public PhysicsBody Body
	{
		get
		{
			if ( !_cachedWorldBody.IsValid() )
			{
				_cachedWorldBody = backend.GetWorldReferenceBody();
			}

			return _cachedWorldBody;
		}
	}

	PhysicsGroup _cachedGroup;

	/// <summary>The physics group of this physics world. A physics world will contain only 1 body.</summary>
	[ActionGraphInclude]
	public PhysicsGroup Group
	{
		get
		{
			if ( !_cachedGroup.IsValid() )
			{
				_cachedGroup = Body?.PhysicsGroup ?? null;
			}

			return _cachedGroup;
		}
	}

	/// <summary>If true then bodies will be able to sleep after a period of inactivity</summary>
	public bool SleepingEnabled
	{
		get => backend.IsSleepingEnabled();
		set
		{
			if ( value ) backend.EnableSleeping();
			else backend.DisableSleeping();
		}
	}

	internal float MaximumLinearSpeed
	{
		set => backend.SetMaximumLinearSpeed( value );
	}

	/// <summary>Physics simulation mode. See <see cref="PhysicsSimulationMode"/> for explanation of each mode.</summary>
	public PhysicsSimulationMode SimulationMode
	{
		get => backend.GetSimulation();
		set => backend.SetSimulation( value );
	}

	[Obsolete]
	public int PositionIterations
	{
		get => 0;
		set
		{
		}
	}

	[Obsolete]
	public int VelocityIterations
	{
		get => 0;
		set
		{
		}
	}

	/// <summary>Physics substeps per tick; raise if objects tunnel. Steps per second = tickrate * substeps.</summary>
	public int SubSteps { get; set; } = 1;

	[Obsolete]
	public float TimeScale { get; set; }

	/// <summary>Used internally to set collision rules from gamemode's project settings. You shouldn't need to call this yourself.</summary>
	[Obsolete( "Use CollisionRules Property" )]
	public void SetCollisionRules( CollisionRules rules )
	{
		CollisionRules = rules;
	}

	/// <summary>Gets the specific collision rule for a pair of tags.</summary>
	public CollisionRules.Result GetCollisionRule( string left, string right )
	{
		return CollisionRules.GetCollisionRule( left, right );
	}

	/// <summary>Raytrace against this world</summary>
	public PhysicsTraceBuilder Trace
	{
		get
		{
			return new PhysicsTraceBuilder( this );
		}
	}

	/// <summary>Like calling PhysicsTraceBuilder.Run, except will re-target this world if it's not already the target</summary>
	public PhysicsTraceResult RunTrace( in PhysicsTraceBuilder trace )
	{
		var newTrace = Trace;
		newTrace.request = trace.request;
		newTrace.targetBody = trace.targetBody;
		newTrace.filterCallback = trace.filterCallback;
		return newTrace.Run();
	}

	/// <summary>Like calling PhysicsTraceBuilder.RunAll, except will re-target this world if it's not already the target</summary>
	public PhysicsTraceResult[] RunTraceAll( in PhysicsTraceBuilder trace )
	{
		var newTrace = Trace;
		newTrace.request = trace.request;
		newTrace.targetBody = trace.targetBody;
		newTrace.filterCallback = trace.filterCallback;
		return newTrace.RunAll();
	}

	internal void RegisterBody( PhysicsBody physicsBody )
	{
		bodies.Add( physicsBody );
	}

	internal void UnregisterBody( PhysicsBody physicsBody )
	{
		backend.RemoveBody( physicsBody );
		bodies.Remove( physicsBody );
	}

	//If a body handle is deleted from native, we can forget it here (empties bodies list of invalid bodies).
	internal void ForgetBody( PhysicsBody physicsBody ) => bodies.Remove( physicsBody );
}

[Expose]
public readonly unsafe struct PhysicsContact
{
	//For backends that report contacts as managed values rather than filling a native notification struct.
	internal PhysicsContact( Vector3 point, Vector3 normal, float normalSpeed )
	{
		Point = point;
		Speed = normal * normalSpeed;
		Normal = normal;
		NormalSpeed = normalSpeed;
		Impulse = 0;
	}

	public readonly Vector3 Point;
	public readonly Vector3 Speed;
	public readonly Vector3 Normal;
	public readonly float NormalSpeed;
	public readonly float Impulse;

	[Expose]
	public readonly struct Target
	{
		//For backends that already have the managed objects, rather than native handle indices.
		internal Target( PhysicsShape shape )
		{
			Shape = shape;
			Body = shape?.Body;
			Surface = shape?.Surface;
		}

		public readonly PhysicsBody Body;
		public readonly PhysicsShape Shape;
		public readonly Surface Surface;
	}
}

[Expose]
public readonly record struct PhysicsIntersection( PhysicsContact.Target Self, PhysicsContact.Target Other, PhysicsContact Contact );

[Expose]
public readonly record struct PhysicsIntersectionEnd( PhysicsContact.Target Self, PhysicsContact.Target Other );
