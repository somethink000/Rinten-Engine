using Rinten.Utility;

namespace Rinten;

/// <summary>Ticks the physics in FrameStage.PhysicsStep</summary>
[Expose]
sealed class ScenePhysicsSystem : GameObjectSystem<ScenePhysicsSystem>
{
	private PhysicsWorld PhysicsWorld;
	private HashSetEx<Collider> KeyframeColliders { get; set; } = new();
	private HashSet<Rigidbody> RigidBodies { get; set; } = new();
	private List<ISceneCollisionEvents> CollisionEvents { get; } = new();

	internal bool Enabled { get; set; }

	public ScenePhysicsSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.PhysicsStep, 0, UpdatePhysics, "UpdatePhysics" );
		Listen( Stage.FinishUpdate, 0, DebugDrawPhysics, "DebugDrawPhysics" );
	}

	//Called by the scene when it creates its physics world. The world is created on demand
	//by the first thing that needs physics - we shouldn't be the ones forcing it to exist.
	internal void OnPhysicsWorldCreated( PhysicsWorld world )
	{
		//A scene whose physics mode changed gets a new world of the other kind; the old one is gone and must not keep calling in.
		Unhook();

		PhysicsWorld = world;
		PhysicsWorld.OnIntersectionStart += OnIntersectionStart;
		PhysicsWorld.OnIntersectionHit += OnIntersectionHit;
		PhysicsWorld.OnIntersectionUpdate += OnIntersectionUpdate;
		PhysicsWorld.OnIntersectionEnd += OnIntersectionEnd;
		PhysicsWorld.OnBodyOutOfBounds += OnBodyOutOfBounds;
		PhysicsWorld.OnBodyFellAsleep += OnBodyFellAsleep;
	}

	public override void Dispose()
	{
		base.Dispose();
		Unhook();

		foreach ( var c in KeyframeColliders.EnumerateLocked( true ) )
			c.KeyframeQueued = false;

		KeyframeColliders.Clear();
	}

	private void Unhook()
	{
		if ( PhysicsWorld is null )
			return;

		PhysicsWorld.OnIntersectionStart -= OnIntersectionStart;
		PhysicsWorld.OnIntersectionHit -= OnIntersectionHit;
		PhysicsWorld.OnIntersectionUpdate -= OnIntersectionUpdate;
		PhysicsWorld.OnIntersectionEnd -= OnIntersectionEnd;
		PhysicsWorld.OnBodyOutOfBounds -= OnBodyOutOfBounds;
		PhysicsWorld.OnBodyFellAsleep -= OnBodyFellAsleep;
		PhysicsWorld = null;
	}

	void UpdatePhysics()
	{
		if ( Scene.IsEditor && !Enabled )
			return;

		using var _ = PerformanceStats.Timings.Physics.Scope();

		var idealHz = 120.0f;
		var idealStep = 1.0f / idealHz;
		int steps = (Time.Delta / idealStep).FloorToInt().Clamp( 1, 10 );

		//Get collision events
		CollisionEvents.Clear();
		Scene.GetAll( CollisionEvents );

		//Called before UpdateKeyframeTransform on purpose, because I assume people are going to use
		//this to move their keyframes, and I want to catch those changes before the physics step.
		IScenePhysicsEvents.Post( x => x.PrePhysicsStep() );

		//A callback in PrePhysicsStep can tear the scene down, world and all. Nothing else below can run on that.
		if ( !PhysicsWorld.IsValid() )
			return;

		//Tell all the keyframe colliders to "move" their keyframes to their new position - we do this right before the
		//physics step - this means that changes in FixedUpdate are immediate Box3D doesn't like us threading things
		//System.Threading.Tasks.Parallel.ForEach( KeyframeColliders.EnumerateLocked( true ), c => c.UpdateKeyframeTransform() );
		foreach ( var c in KeyframeColliders.EnumerateLocked( true ) )
		{
			if ( c.UpdateKeyframeTransform() )
				RemoveKeyframe( c );
		}

		//The actual physics step - if the world was never created there's nothing to step
		PhysicsWorld?.Step( Time.NowDouble, Time.Delta, steps );

		//The step runs collision callbacks, and one of them can have removed the world under us.
		if ( !PhysicsWorld.IsValid() )
			return;

		//Update the positions of the rigidbodies based on the new physics positions todo: we should only update ones that have
		//changed, skip asleep? I don't feel comfortable doing this in a thread, because of all the LocalTransformChanged callbacks
		//System.Threading.Tasks.Parallel.ForEach( Scene.GetAll<Rigidbody>(), c => c.UpdateTransformFromBody() );
		foreach ( var obj in Scene.GetAll<Rigidbody>() )
		{
			//Asleep with nothing pending
			if ( !obj.NeedsTransformSync ) continue;

			obj.UpdateTransformFromBody();
		}

		//Called after the positions of everything are updated, because I assume that people are going to want to access those positions and do shit with them.
		IScenePhysicsEvents.Post( x => x.PostPhysicsStep() );
	}

	void OnIntersectionStart( PhysicsIntersection o )
	{
		if ( CollisionEvents.Count == 0 ) return;

		var c = new Collision( new CollisionSource( o.Self ), new CollisionSource( o.Other ), o.Contact );
		foreach ( var e in CollisionEvents )
		{
			e.OnCollisionStart( c );
		}
	}

	void OnIntersectionHit( PhysicsIntersection o )
	{
		if ( CollisionEvents.Count == 0 ) return;

		var c = new Collision( new CollisionSource( o.Self ), new CollisionSource( o.Other ), o.Contact );
		foreach ( var e in CollisionEvents )
		{
			e.OnCollisionHit( c );
		}
	}

	void OnIntersectionUpdate( PhysicsIntersection o )
	{
		if ( CollisionEvents.Count == 0 ) return;

		var c = new Collision( new CollisionSource( o.Self ), new CollisionSource( o.Other ), o.Contact );
		foreach ( var e in CollisionEvents )
		{
			e.OnCollisionUpdate( c );
		}
	}

	void OnIntersectionEnd( PhysicsIntersectionEnd o )
	{
		if ( CollisionEvents.Count == 0 ) return;

		var c = new CollisionStop( new CollisionSource( o.Self ), new CollisionSource( o.Other ) );
		foreach ( var e in CollisionEvents )
		{
			e.OnCollisionStop( c );
		}
	}

	void OnBodyOutOfBounds( PhysicsBody body )
	{
		var rb = body.Component as Rigidbody;
		if ( rb.IsValid() == false ) return;
		IScenePhysicsEvents.Post( x => x.OnOutOfBounds( rb ) );
	}

	void OnBodyFellAsleep( PhysicsBody body )
	{
		var rb = body.Component as Rigidbody;
		if ( rb.IsValid() == false ) return;
		IScenePhysicsEvents.Post( x => x.OnFellAsleep( rb ) );
	}

	void DebugDrawPhysics()
	{
		if ( !PhysicsWorld.IsValid() )
			return;

		using ( Performance.Scope( "PhysicsDraw" ) )
		{
			PhysicsWorld.DebugDraw();
		}
	}

	internal void AddKeyframe( Collider collider )
	{
		KeyframeColliders.Add( collider );
		collider.KeyframeQueued = true;
	}

	internal void RemoveKeyframe( Collider collider )
	{
		KeyframeColliders.Remove( collider );
		collider.KeyframeQueued = false;
	}

	internal void AddRigidBody( Rigidbody rigidBody )
	{
		if ( !rigidBody.IsValid() )
			return;

		RigidBodies.Add( rigidBody );
		rigidBody.UpdateBody();
	}

	internal void RemoveRigidBody( Rigidbody rigidBody )
	{
		if ( !rigidBody.IsValid() )
			return;

		RigidBodies.Remove( rigidBody );
		rigidBody.UpdateBody();
	}

	internal void RemoveRigidBodies()
	{
		var bodies = RigidBodies.ToList();
		RigidBodies.Clear();

		foreach ( var rb in bodies )
			rb.UpdateBody();
	}

	internal bool HasRigidBody( Rigidbody rigidBody )
	{
		return RigidBodies.Contains( rigidBody );
	}
}
