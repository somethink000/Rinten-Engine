using System.Linq;

namespace Rinten;

/// <summary>Physics for a model. This is primarily used for ragdolls and other physics driven models, otherwise you should be using a Rigidbody.</summary>
[Expose]
[Title( "Model Physics" )]
[Category( "Physics" )]
[Icon( "check_box_outline_blank" )]
public sealed partial class ModelPhysics : Component, IScenePhysicsEvents, IHasModel
{
	/// <summary>The bodies as one physics group, gathered lazily since rigidbodies start after creation</summary>
	public PhysicsGroup PhysicsGroup
	{
		get
		{
			if ( group is null || group.BodyCount < Bodies.Count ) GatherGroup();

			return group;
		}

		private set => group = value;
	}

	private PhysicsGroup group;

	private Model _model;
	private SkinnedModelRenderer _renderer;
	private RigidbodyFlags _rigidBodyFlags;
	private PhysicsLock _locking;
	private bool _motionEnabled = true;
	private bool _onEnabled = false;

	[Property, Hide]
	public bool PhysicsWereCreated { get; set; }

	//Number of times the physics have been rebuilt. Debugging use only.
	internal int PhysicsRebuildCount { get; set; }

	//Number of times the physics have been destroyed. Debugging use only.
	internal int PhysicsDestroyCount { get; set; }

	/// <summary>The model used to generate physics bodies, collision shapes, and joints.</summary>
	[Property]
	public Model Model
	{
		get => _model;
		set
		{
			if ( _model == value )
				return;

			_model = value;

			OnModelChanged();
		}
	}

	/// <summary>The renderer that receives transform updates from physics bodies.</summary>
	[Property]
	public SkinnedModelRenderer Renderer
	{
		get => _renderer;
		set
		{
			if ( _renderer == value )
				return;

			if ( _renderer.IsValid() )
			{
				_renderer.ClearPhysicsBones();
			}

			_renderer = value;
		}
	}

	/// <summary>If true, the root physics body will not drive this component's transform.</summary>
	[Property]
	public bool IgnoreRoot { get; set; }

	/// <summary>Rigidbody flags applied to all bodies.</summary>
	[Property, Group( "Physics" )]
	public RigidbodyFlags RigidbodyFlags
	{
		get => _rigidBodyFlags;
		set
		{
			if ( _rigidBodyFlags == value )
				return;

			_rigidBodyFlags = value;

			foreach ( var body in Bodies )
			{
				if ( !body.Component.IsValid() )
					continue;

				body.Component.RigidbodyFlags = value;
			}
		}
	}

	/// <summary>Rigidbody locking applied to all bodies.</summary>
	[Property, Group( "Physics" )]
	public PhysicsLock Locking
	{
		get => _locking;
		set
		{
			_locking = value;

			foreach ( var body in Bodies )
			{
				if ( !body.Component.IsValid() )
					continue;

				body.Component.Locking = value;
			}
		}
	}

	/// <summary>All bodies will be put to sleep on start.</summary>
	[Property, Group( "Physics" )]
	public bool StartAsleep { get; set; }

	/// <summary>Enable to drive renderer from physics, disable to drive physics from renderer.</summary>
	[Property, Group( "Physics" )]
	public bool MotionEnabled
	{
		get => _motionEnabled;
		set
		{
			if ( _motionEnabled == value )
				return;

			_motionEnabled = value;

			foreach ( var body in Bodies )
			{
				if ( !body.Component.IsValid() )
					continue;

				body.Component.MotionEnabled = value;
			}
		}
	}

	/// <summary>Returns the total mass of every <see cref="Rigidbody"/></summary>
	public float Mass => Bodies.Sum( x => x.Component?.Mass ?? default );

	/// <summary>Returns the center of mass of every <see cref="Rigidbody"/> in world-space</summary>
	public Vector3 MassCenter
	{
		get
		{
			var world = WorldTransform;

			var mass = 0.0f;
			var center = Vector3.Zero;

			foreach ( var body in Bodies )
			{
				var rb = body.Component;
				if ( !rb.IsValid() ) continue;

				mass += rb.Mass;
				center += rb.Mass * world.PointToWorld( rb.MassCenter );
			}

			return mass > 0.0f ? center / mass : world.Position;
		}
	}

	private void OnModelChanged()
	{
		if ( !_onEnabled ) return;
		if ( !Active ) return;
		if ( IsProxy ) return;
		if ( GameObject.IsDeserializing ) return;

		DestroyPhysics();
		CreatePhysics();
	}

	//Apply body transforms to renderer bones.
	private void PositionRendererBonesFromPhysics()
	{
		var rootBody = GetComponentInChildren<Rigidbody>( true, true );

		//Honour IgnoreRoot in editor too, or the model teleports onto an arbitrary limb's body
		if ( !IgnoreRoot && rootBody.IsValid() )
		{
			//The simulated state in the editor, the component's own property in a game: an editor scene is
			//not stepping physics, so the body is only authoritative there when it actually says it is.
			var driving = Scene.IsEditor
				? rootBody.PhysicsBody.IsValid() && rootBody.PhysicsBody.MotionEnabled
				: rootBody.MotionEnabled;

			if ( driving )
				WorldTransform = rootBody.WorldTransform.WithScale( WorldScale );
		}

		if ( !Renderer.IsValid() )
			return;

		var so = Renderer.SceneModel;
		if ( !so.IsValid() )
			return;

		var world = WorldTransform;

		//Apply overrides parent-first; each is resolved against its parent's current pose
		var bodies = Bodies;

		foreach ( var index in ParentFirst() )
		{
			var body = bodies[index];

			var rb = body.Component;
			if ( !rb.IsValid() )
				continue;

			if ( body.Bone < 0 )
				continue;

			if ( !rb.GameObject.Flags.Contains( GameObjectFlags.PhysicsBone ) )
				continue;

			//Only drive physics from animation if we have motion disabled.
			if ( !MotionEnabled && !rb.MotionEnabled )
			{
				//Use the animation pose when body motion is disabled.
				var local = so.Transform.ToLocal( so.GetWorldSpaceAnimationTransform( body.Bone ) );
				ApplyBoneOverride( so, body.Bone, local, "animation" );

				//Move object to animation pose, physics object will move towards it in pre physics step.
				if ( rb.Transform.SetLocalTransformFast( world.ToWorld( local ) ) )
					rb.Transform.TransformChanged( true );
			}
			else
			{
				//Bone overrides are model space; restore the bone's own scale since the body object is unscaled
				var local = world.ToLocal( rb.WorldTransform ).WithScale( body.LocalTransform.Scale );

				ApplyBoneOverride( so, body.Bone, local, "physics" );
			}
		}
	}

	//Drop NaN or zero-scale overrides (warn once); they would make the cull sphere NaN and hide the model
	void ApplyBoneOverride( SceneModel so, int bone, in Transform local, string from )
	{
		if ( local.Position.IsNaN || local.Scale.IsNaN
			|| local.Scale.x.AlmostEqual( 0f ) || local.Scale.y.AlmostEqual( 0f ) || local.Scale.z.AlmostEqual( 0f ) )
		{
			if ( !_warnedAboutOverride )
			{
				_warnedAboutOverride = true;

				var bones = Model?.Bones?.AllBones;
				var name = bones is not null && bone >= 0 && bone < bones.Count ? bones[bone].Name : "(none)";

				Log.Warning( $"{GameObject?.Name}: bone '{name}' ({bone}) was given an unusable {from} override {local} - " +
					"dropped, because passing it on takes the whole model out of every view. " +
					$"model={Model?.ResourcePath}, world={WorldTransform}" );
			}

			return;
		}

		so.SetBoneOverride( bone, local );
	}

	//Said once - a pose that is wrong is wrong every frame.
	bool _warnedAboutOverride;

	//The bodies' indices, parents before children - worked out again only when the list or the model changes
	int[] ParentFirst()
	{
		var bodies = Bodies;

		if ( _parentFirst is not null && ReferenceEquals( _parentFirstOf, bodies ) && _parentFirst.Length == bodies.Count && ReferenceEquals( _parentFirstModel, Model ) )
			return _parentFirst;

		var order = new int[bodies.Count];
		var depths = new int[bodies.Count];

		for ( var i = 0; i < order.Length; i++ )
		{
			order[i] = i;
			depths[i] = BoneDepth( bodies[i].Bone );
		}

		Array.Sort( depths, order );

		_parentFirstOf = bodies;
		_parentFirstModel = Model;
		return _parentFirst = order;
	}

	int[] _parentFirst;
	List<Body> _parentFirstOf;
	Model _parentFirstModel;

	//How many bones up to the skeleton's root
	int BoneDepth( int boneIndex )
	{
		var bones = Model?.Bones?.AllBones;
		if ( bones is null || boneIndex < 0 || boneIndex >= bones.Count )
			return 0;

		var depth = 0;
		for ( var bone = bones[boneIndex].Parent; bone is not null; bone = bone.Parent )
			depth++;

		return depth;
	}

	//Smooth move bodies to animated bone transforms for bodies that have motion disabled.
	private void MovePhysicsFromAnimation()
	{
		if ( Scene.IsEditor )
			return;

		if ( !Renderer.IsValid() )
			return;

		var so = Renderer.SceneModel;
		if ( !so.IsValid() )
			return;

		foreach ( var body in Bodies )
		{
			if ( !body.Component.IsValid() )
				continue;

			if ( body.Component.MotionEnabled )
				continue;

			if ( !body.Component.PhysicsBody.IsValid() )
				continue;

			//Move kinematic body to object transform.
			body.Component.PhysicsBody.Move( body.Component.WorldTransform, Time.Delta );
		}
	}

	//Adjust joint points for body scaling.
	private void UpdateJointScale()
	{
		// Runs every fixed update for every ragdoll - only touch the native joints when a scale actually changed
		if ( _appliedJointScales is null || _appliedJointScales.Length != Joints.Count * 2 )
		{
			_appliedJointScales = new float[Joints.Count * 2];
			_appliedJoints = new PhysicsJoint[Joints.Count];
		}

		for ( int i = 0; i < Joints.Count; i++ )
		{
			var joint = Joints[i];
			if ( !joint.Component.IsValid() )
				continue;

			// A re-created native joint starts from unscaled anchors again
			if ( joint.Component.NativeJoint != _appliedJoints[i] )
			{
				_appliedJoints[i] = joint.Component.NativeJoint;
				_appliedJointScales[i * 2] = float.NaN;
				_appliedJointScales[i * 2 + 1] = float.NaN;
			}

			var scale1 = joint.Component.WorldTransform.UniformScale;
			if ( scale1 != _appliedJointScales[i * 2] )
			{
				var point1 = joint.Component.Point1;
				point1.LocalPosition = joint.LocalFrame1.Position * scale1;
				joint.Component.Point1 = point1;
				_appliedJointScales[i * 2] = scale1;
			}

			var scale2 = joint.Component.Body.WorldTransform.UniformScale;
			if ( scale2 != _appliedJointScales[i * 2 + 1] )
			{
				var point2 = joint.Component.Point2;
				point2.LocalPosition = joint.LocalFrame2.Position * scale2;
				joint.Component.Point2 = point2;
				_appliedJointScales[i * 2 + 1] = scale2;
			}
		}
	}

	float[] _appliedJointScales;
	PhysicsJoint[] _appliedJoints;

	/// <summary>Put all bodies to sleep.</summary>
	private void Sleep()
	{
		foreach ( var body in Bodies )
		{
			if ( !body.Component.IsValid() )
				continue;

			body.Component.Sleeping = true;
		}
	}

	protected override void OnAwake()
	{
		_renderer ??= GetComponent<SkinnedModelRenderer>();

		//Auto set the model using the renderer model if it's not already set.
		if ( _model is null && _renderer.IsValid() )
		{
			_model = _renderer.Model;
		}
	}

	protected override void OnStart()
	{
		if ( StartAsleep )
		{
			Sleep();
		}
	}

	protected override void OnUpdate()
	{
		//Proxy only needs to sync renderer to physics.
		if ( IsProxy )
		{
			UpdateProxyTransforms();

			return;
		}

		PositionRendererBonesFromPhysics();

		//In editor we need to move the bodies with this transform while keeping the local pose.
		if ( Scene.IsEditor )
		{
			foreach ( var body in Bodies )
			{
				if ( !body.Component.IsValid() )
					continue;

				if ( body.Component.PhysicsBody.MotionEnabled )
				{
					//Update local transform pose when simulating physics in editor.
					//TODO
					//body.LocalTransform = WorldTransform.ToLocal( body.Component.WorldTransform );
				}
				else
				{
					//Move body when moving this gameobject in editor.
					body.Component.WorldTransform = WorldTransform.ToWorld( body.LocalTransform );
				}
			}
		}
	}

	protected override void OnFixedUpdate()
	{
		if ( IsProxy )
			return;

		UpdateJointScale();

	}

	void IScenePhysicsEvents.PrePhysicsStep()
	{
		if ( IsProxy )
		{
			//Move proxy bodies to their networked transform.
			MoveProxyBodies();
		}
		else if ( !MotionEnabled )
		{
			//Only drive physics from animation if we have motion disabled.
			MovePhysicsFromAnimation();
		}
	}

	void IScenePhysicsEvents.PostPhysicsStep()
	{
		if ( !IsProxy )
		{
			//Network body transforms.
			SetBodyTransforms();
		}
	}

	protected override void OnEnabled()
	{
		_onEnabled = true;

		//PhysicsWereCreated can be stale (bodies not saved/deleted); rebuild unless this is a proxy
		if ( PhysicsWereCreated && !IsProxy && !Bodies.Any( x => x.Component.IsValid() ) )
		{
			DestroyPhysics();
		}

		if ( PhysicsWereCreated )
		{
			EnableComponents();
		}
		else
		{
			CreatePhysics();
		}

		//Apply any cached renderer transforms and velocities.
		ApplyCachedRendererTransforms();
	}

	protected override void OnDisabled()
	{
		DisableComponents();

		_onEnabled = false;
	}

	protected override void OnDestroy()
	{
		DestroyPhysics();

		_onEnabled = false;
	}

	//These are temporary holders so you can set the bone positions before the component is enabled They are used once on initializing and then removed
	Transform[] _rendererBonePosition;
	SkinnedModelRenderer.BoneVelocity[] _rendererBoneVelocity;

	private void PositionPhysicsFromRendererBones()
	{
		if ( _rendererBonePosition is null || _rendererBoneVelocity is null )
		{
			if ( Renderer is not null && Renderer.SceneModel.IsValid() )
			{
				_rendererBonePosition = Renderer.GetBoneTransforms( true );
				_rendererBoneVelocity = Renderer.GetBoneVelocities();
			}
		}

		if ( _rendererBonePosition is null || _rendererBoneVelocity is null )
			return;

		//Not marked as bones - see CreateParts for why physics does not ask for a skeleton to be drawn on its way to finding somewhere to put bodies.
		var boneObjects = Model.CreateBoneObjects( GameObject, flagAsBone: false );

		foreach ( var pair in boneObjects )
		{
			if ( pair.Key is null )
				continue;

			if ( !pair.Value.IsValid() )
				continue;

			if ( _rendererBonePosition.Length <= pair.Key.Index )
				continue;

			if ( _rendererBoneVelocity.Length <= pair.Key.Index )
				continue;

			var tx = _rendererBonePosition[pair.Key.Index];
			var vl = _rendererBoneVelocity[pair.Key.Index];

			if ( pair.Value.GetComponent<Rigidbody>() is { } body )
			{
				body.WorldTransform = tx.WithScale( WorldScale );
				body.Velocity = vl.Linear;
				body.AngularVelocity = vl.Angular;
			}
		}

		_rendererBoneVelocity = default;
		_rendererBonePosition = default;
	}

	/// <summary>Copy the bone positions and velocities from a different SkinnedModelRenderer</summary>
	public void CopyBonesFrom( SkinnedModelRenderer source, bool teleport )
	{
		if ( !source.IsValid() )
			return;

		_rendererBonePosition = source.GetBoneTransforms( true );
		_rendererBoneVelocity = source.GetBoneVelocities();

		//Only apply them if we're enabled, they will be applied in enabled if we're not enabled yet.
		if ( _onEnabled )
		{
			ApplyCachedRendererTransforms();
		}
	}

	void ApplyCachedRendererTransforms()
	{
		if ( _rendererBonePosition is null || _rendererBoneVelocity is null )
			return;

		PositionPhysicsFromRendererBones();
		PositionRendererBonesFromPhysics();
	}

	void IHasModel.OnModelReloaded()
	{
		OnModelReloaded();
	}
	//A physics character finds its parts by name - "the Rigidbody on the object called torso" - and there is no way to see from a chair whether the body is missing, on an object called something else, or there and asleep.
	/// <summary>What every ragdoll in the scene actually built, for when a game that drives one says it cannot find its bodies.</summary>
	[ConCmd( "physics_bodies", Help = "List the bodies every ModelPhysics in the scene has made" )]
	public static void PrintBodies()
	{
		var scene = Game.ActiveScene;

		if ( !scene.IsValid() )
		{
			Log.Info( "physics: no scene is running" );
			return;
		}

		var all = scene.GetAllComponents<ModelPhysics>().ToList();

		Log.Info( $"physics: {all.Count} ModelPhysics in '{scene.Name}'" );

		foreach ( var physics in all )
		{
			var model = physics.Renderer.IsValid() ? physics.Renderer.Model?.ResourcePath ?? "(no model)" : "(no renderer)";

			Log.Info( $"  {physics.GameObject?.Name} - {model}, enabled {physics.Enabled}, " +
				$"{physics.PhysicsGroup?.Bodies?.Count() ?? 0} body(s)" );

			foreach ( var body in physics.GameObject.GetComponentsInChildren<Rigidbody>( true ) )
			{
				Log.Info( $"    {body.GameObject?.Name,-14} rigidbody enabled {body.Enabled,-5} " +
					$"motion {body.MotionEnabled,-5} bone {body.GameObject.Flags.Contains( GameObjectFlags.PhysicsBone )}" );
			}
		}
	}

}
