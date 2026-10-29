using System.Collections.Generic;
using System.Linq;

namespace Rinten;

public sealed partial class ModelPhysics
{
	//Create all the bodies, colliders and joints.
	private void CreatePhysics()
	{
		if ( !Active ) return;
		if ( IsProxy ) return;
		if ( PhysicsWereCreated ) return;

		//Make sure any components we create are available immediately after.
		using var batch = Scene.BatchGroup();

		DestroyPhysics();

		if ( !Model.IsValid() ) return;

		var physics = Model.Physics;
		if ( physics is null || physics.Parts.Count == 0 )
		{
			//Models without a physics group can't build anything; warn once per model instead of failing silently
			if ( _warnedNoPhysics != Model )
			{
				_warnedNoPhysics = Model;

				Log.Warning( $"{GameObject?.Name}: ModelPhysics has nothing to build - " +
					$"'{Model.ResourcePath}' has no physics parts. Give the model a physics group, " +
					"or use a ModelCollider if it only needs collision." );
			}

			return;
		}

		PhysicsRebuildCount++;

		var componentFlags = ComponentFlags.None;

		if ( Scene.IsEditor )
		{
			componentFlags |= ComponentFlags.NotEditable;
		}

		var world = WorldTransform;

		CreateParts( physics, world, componentFlags );
		CreateJoints( physics, componentFlags );

		foreach ( var body in Bodies )
		{
			body.Component.Enabled = true;
			Network?.Refresh( body.Component );
		}

		foreach ( var joint in Joints )
		{
			joint.Component.Enabled = true;
			Network?.Refresh( joint.Component );
		}

		GatherGroup();

		PhysicsWereCreated = true;
	}

	//The model we have already said has no physics to build from.
	private Model _warnedNoPhysics;

	//Joints name parts by index, and parts can be skipped: which body each part became, -1 for none
	int[] _partBodyIndex;

	bool TryGetPartBody( int partIndex, out Body body )
	{
		body = default;
		if ( _partBodyIndex is null || partIndex < 0 || partIndex >= _partBodyIndex.Length ) return false;

		var index = _partBodyIndex[partIndex];
		if ( index < 0 || index >= Bodies.Count ) return false;

		body = Bodies[index];
		return true;
	}
	private void CreateJoints( PhysicsGroupDescription physics, ComponentFlags componentFlags )
	{
		var jointFlags = componentFlags;

		foreach ( var jointDesc in physics.Joints )
		{
			//Skip joints to parts that weren't created (e.g. their bone is missing)
			if ( !TryGetPartBody( jointDesc.Body1, out var body1 ) || !TryGetPartBody( jointDesc.Body2, out var body2 ) )
				continue;

			//Disabled joints still get a collision filter so adjacent bodies don't fight over shared space
			if ( !jointDesc.Enabled )
			{
				CreateCollisionFilter( body1.Component.GameObject, body2.Component.GameObject );
				continue;
			}

			var localFrame1 = jointDesc.Frame1;
			var localFrame2 = jointDesc.Frame2;

			Rinten.Joint joint = null;

			if ( jointDesc.Type == PhysicsGroupDescription.JointType.Hinge )
			{
				var hingeJoint = body1.Component.AddComponent<HingeJoint>( false );

				if ( jointDesc.EnableTwistLimit )
				{
					hingeJoint.MinAngle = jointDesc.TwistMin;
					hingeJoint.MaxAngle = jointDesc.TwistMax;
				}

				if ( jointDesc.EnableAngularMotor )
				{
					var worldFrame1 = body1.Component.WorldTransform.ToWorld( localFrame1 );
					var hingeAxis = worldFrame1.Rotation.Up;
					var targetVelocity = hingeAxis.Dot( jointDesc.AngularTargetVelocity );

					hingeJoint.Motor = HingeJoint.MotorMode.TargetVelocity;
					hingeJoint.TargetVelocity = targetVelocity.RadianToDegree();
					hingeJoint.MaxTorque = jointDesc.MaxTorque;
				}

				//The same resistance the ball joint below is given. Left off, a model's hinge friction was read out of the .mdl,
				//carried all the way here and then dropped, so an elbow authored to hold itself swung as freely as one authored not to.
				hingeJoint.Friction = jointDesc.Friction;

				joint = hingeJoint;
			}
			else if ( jointDesc.Type == PhysicsGroupDescription.JointType.Ball )
			{
				var ballJoint = body1.Component.AddComponent<BallJoint>( false );

				if ( jointDesc.EnableSwingLimit )
				{
					ballJoint.SwingLimitEnabled = true;
					ballJoint.SwingLimit = new Vector2( jointDesc.SwingMin, jointDesc.SwingMax );
				}

				if ( jointDesc.EnableTwistLimit )
				{
					ballJoint.TwistLimitEnabled = true;
					ballJoint.TwistLimit = new Vector2( jointDesc.TwistMin, jointDesc.TwistMax );
				}

				ballJoint.Friction = jointDesc.Friction;

				if ( jointDesc.AngularFrequency > 0f )
				{
					ballJoint.Motor = BallJoint.MotorMode.TargetRotation;
					ballJoint.TargetRotation = Rotation.Identity;
					ballJoint.Frequency = jointDesc.AngularFrequency;
					ballJoint.DampingRatio = jointDesc.AngularDampingRatio;
				}

				joint = ballJoint;
			}
			else if ( jointDesc.Type == PhysicsGroupDescription.JointType.Fixed )
			{
				var fixedJoint = body1.Component.AddComponent<FixedJoint>( false );
				fixedJoint.LinearFrequency = jointDesc.LinearFrequency;
				fixedJoint.LinearDamping = jointDesc.LinearDampingRatio;
				fixedJoint.AngularFrequency = jointDesc.AngularFrequency;
				fixedJoint.AngularDamping = jointDesc.AngularDampingRatio;

				joint = fixedJoint;
			}
			else if ( jointDesc.Type == PhysicsGroupDescription.JointType.Slider )
			{
				var sliderJoint = body1.Component.AddComponent<SliderJoint>( false );

				if ( jointDesc.EnableLinearLimit )
				{
					sliderJoint.MinLength = jointDesc.LinearMin;
					sliderJoint.MaxLength = jointDesc.LinearMax;
				}

				var rot = Rotation.FromPitch( -90 );
				localFrame1 = localFrame1.WithRotation( rot * localFrame1.Rotation );
				localFrame2 = localFrame2.WithRotation( rot * localFrame2.Rotation );

				joint = sliderJoint;
			}

			if ( joint.IsValid() )
			{
				//Adjust joint frames for current body separation vs the real rest pose, or limbs snap on start
				var bindPos1 = RestBonePosition( body1.Bone );
				var bindPos2 = RestBonePosition( body2.Bone );
				var currentSeparation = body1.Component.WorldPosition.Distance( body2.Component.WorldPosition );

				if ( bindPos1 is { } p1 && bindPos2 is { } p2 )
				{
					var bindSeparation = p1.Distance( p2 );
					if ( bindSeparation > 0.0f )
					{
						var scale = currentSeparation / bindSeparation;
						localFrame1 = localFrame1.WithPosition( localFrame1.Position * scale );
						localFrame2 = localFrame2.WithPosition( localFrame2.Position * scale );
					}
				}

				joint.Flags |= jointFlags;
				joint.Body = body2.Component.GameObject;
				joint.Attachment = Rinten.Joint.AttachmentMode.LocalFrames;
				joint.LocalFrame1 = localFrame1.WithPosition( localFrame1.Position * body1.Component.WorldScale );
				joint.LocalFrame2 = localFrame2.WithPosition( localFrame2.Position * body2.Component.WorldScale );

				if ( ragdollDebug ) SayJoint( jointDesc, body1, body2, localFrame1, localFrame2 );
				joint.EnableCollision = jointDesc.EnableCollision;
				joint.BreakForce = jointDesc.LinearStrength;
				joint.BreakTorque = jointDesc.AngularStrength;

				Joints.Add( new Joint( joint, body1, body2, localFrame1, localFrame2 ) );
			}
		}
	}

	//Stop two bodies colliding with each other, without constraining them at all.
	static void CreateCollisionFilter( GameObject a, GameObject b )
	{
		if ( !a.IsValid() || !b.IsValid() )
			return;

		var filter = a.AddComponent<PhysicsFilter>( false );
		filter.Body = b;
		filter.Enabled = true;
	}

	//Where a bone sits in the model's rest pose, model-space.
	Vector3? RestBonePosition( int boneIndex )
	{
		if ( boneIndex < 0 )
			return null;

		var bones = Model?.Bones?.AllBones;
		if ( bones is null || boneIndex >= bones.Count )
			return null;

		return RestBoneTransform( bones[boneIndex] ).Position;
	}

	//Rest-pose transform composed through parents, keeping bone scale (the root has a real scale)
	static Transform RestBoneTransform( BoneCollection.Bone bone )
	{
		var local = bone.LocalTransform;
		return bone.Parent is null ? local : RestBoneTransform( bone.Parent ).ToWorld( local );
	}

	private void CreateParts( PhysicsGroupDescription physics, Transform world, ComponentFlags componentFlags )
	{
		//Somewhere to hang bodies, not a skeleton to look at - so these are not marked as bones. A renderer asked to make bone objects marks
		//the same ones itself, and this reuses those by name rather than making a second set, so switching that on still gets a skeleton drawn.
		var boneObjects = Model.CreateBoneObjects( GameObject, flagAsBone: false );
		var bones = Model.Bones;
		var bodyFlags = componentFlags;
		var colliderFlags = componentFlags;

		//Part transforms are always identity, so boneless parts share one body centred on their shapes
		var bonelessCenter = BonelessShapeCenter( physics );

		_partBodyIndex = new int[physics.Parts.Count];
		Array.Fill( _partBodyIndex, -1 );
		var partIndex = -1;

		foreach ( var part in physics.Parts )
		{
			partIndex++;
			var go = HostFor( part, bones, boneObjects, world, bonelessCenter, out var boneIndex, out var boneWorld );
			if ( !go.IsValid() )
				continue;

			var body = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>( false );
			body.Flags |= bodyFlags;
			body.RigidbodyFlags = RigidbodyFlags;
			body.Locking = Locking;
			body.MotionEnabled = MotionEnabled;
			body.LinearDamping = part.LinearDamping;
			body.AngularDamping = part.AngularDamping;
			body.StartAsleep = StartAsleep;
			body.MassOverride = part.Mass;
			body.OverrideMassCenter = part.OverrideMassCenter;
			body.MassCenterOverride = part.MassCenterOverride;
			body.GravityScale = part.GravityScale;
			body.GameObjectSource = GameObject;

			BodyTransforms.Set( Bodies.Count, boneWorld );
			_partBodyIndex[partIndex] = Bodies.Count;
			Bodies.Add( new Body( body, boneIndex, world.ToLocal( boneWorld ) ) );

			//Shapes are authored in the bone's space (or model space, for a boneless part) - moving a boneless body's own origin off to bonelessCenter
			//means its shapes need the same offset subtracted, so each one still lands exactly where it was authored rather than sliding by bonelessCenter.
			var shapeOffset = boneIndex < 0 ? bonelessCenter : Vector3.Zero;

			foreach ( var sphere in part.Spheres )
			{
				var collider = go.AddComponent<SphereCollider>();
				collider.Flags |= colliderFlags;
				collider.Center = sphere.Sphere.Center - shapeOffset;
				collider.Radius = sphere.Sphere.Radius;
				collider.Surface = sphere.Surface;
			}

			foreach ( var capsule in part.Capsules )
			{
				var collider = go.AddComponent<CapsuleCollider>();
				collider.Flags |= colliderFlags;
				collider.Start = capsule.Capsule.CenterA - shapeOffset;
				collider.End = capsule.Capsule.CenterB - shapeOffset;
				collider.Radius = capsule.Capsule.Radius;
				collider.Surface = capsule.Surface;
			}

			foreach ( var hull in part.Hulls )
			{
				var collider = go.AddComponent<HullCollider>();
				collider.Flags |= colliderFlags;
				collider.Type = HullCollider.PrimitiveType.Points;
				collider.Points = shapeOffset == Vector3.Zero
					? hull.GetPoints().ToList()
					: hull.GetPoints().Select( p => p - shapeOffset ).ToList();
				collider.Surface = hull.Surface;
			}

			//Warn about parts with no usable shape; a mesh can't be a dynamic body
			if ( part.Spheres.Count > 0 || part.Capsules.Count > 0 || part.Hulls.Count > 0 ) continue;

			Log.Warning( part.Meshes.Count > 0
				? $"{Model?.ResourcePath}: '{part.BoneName}' is collided by a mesh, which cannot be a moving body - it has nothing to hit with"
				: $"{Model?.ResourcePath}: '{part.BoneName}' has no collision shape at all" );
		}
	}

	//The combined geometric centre of every shape on every part with no bone, model-space. One shape, one sample - a
	//hull's own points are averaged first, so a forty-corner hull doesn't outweigh a single sphere sitting next to it.
	static Vector3 BonelessShapeCenter( PhysicsGroupDescription physics )
	{
		var sum = Vector3.Zero;
		var count = 0;

		foreach ( var part in physics.Parts )
		{
			if ( !string.IsNullOrWhiteSpace( part.BoneName ) ) continue;

			foreach ( var sphere in part.Spheres ) { sum += sphere.Sphere.Center; count++; }
			foreach ( var capsule in part.Capsules ) { sum += (capsule.Capsule.CenterA + capsule.Capsule.CenterB) * 0.5f; count++; }

			foreach ( var hull in part.Hulls )
			{
				var points = hull.GetPoints();
				var hullSum = Vector3.Zero;
				var hullCount = 0;
				foreach ( var point in points ) { hullSum += point; hullCount++; }
				if ( hullCount == 0 ) continue;
				sum += hullSum / hullCount;
				count++;
			}
		}

		return count > 0 ? sum / count : Vector3.Zero;
	}

	//Bone object when the part names one, otherwise a child on this object.
	GameObject HostFor( PhysicsGroupDescription.BodyPart part, BoneCollection bones,
		Dictionary<BoneCollection.Bone, GameObject> boneObjects, Transform world, Vector3 bonelessCenter,
		out int boneIndex, out Transform boneWorld )
	{
		var bone = string.IsNullOrWhiteSpace( part.BoneName ) ? null : bones?.GetBone( part.BoneName );
		if ( bone is not null && boneObjects.TryGetValue( bone, out var found ) && found.IsValid() )
		{
			boneIndex = bone.Index;

			//Bone world transform from the renderer, or the rest pose (keeps scale) if it isn't posed yet
			Transform BoneWorldNow()
				=> Renderer.IsValid() && Renderer.TryGetBoneTransform( bone, out var tx )
					? tx
					: world.ToWorld( RestBoneTransform( bone ) );

			if ( found.Flags.Contains( GameObjectFlags.Absolute | GameObjectFlags.PhysicsBone ) )
			{
				boneWorld = BoneWorldNow();

				found.WorldTransform = boneWorld.WithScale( world.Scale );
				return found;
			}

			found.Flags |= GameObjectFlags.Absolute | GameObjectFlags.PhysicsBone;
			boneWorld = BoneWorldNow();

			//Collider shapes are authored assuming this object is unscaled, so the object itself stays that way here. boneWorld keeps its real scale - it
			//is handed back to the caller and baked into Body.LocalTransform, which is what PositionRendererBonesFromPhysics feeds back as a bone override.
			found.WorldTransform = boneWorld.WithScale( world.Scale );

			return found;
		}

		boneIndex = -1;
		var name = string.IsNullOrWhiteSpace( part.BoneName ) ? "physics" : part.BoneName;
		var go = GameObject.Children.FirstOrDefault( child =>
			child.Name == name && !child.Flags.Contains( GameObjectFlags.Attachment ) );

		if ( !go.IsValid() )
		{
			go = Scene.CreateObject( true );
			go.Parent = GameObject;
			go.Name = name;
		}

		//Every boneless part shares this object (by name, above) - only the first of them should plant its pivot; a later one finding
		//it already placed would otherwise drag it (and every shape already added under it) off to its own idea of the centre instead.
		if ( go.Flags.Contains( GameObjectFlags.Absolute | GameObjectFlags.PhysicsBone ) )
		{
			boneWorld = go.WorldTransform;
			return go;
		}

		go.Flags |= GameObjectFlags.Absolute | GameObjectFlags.PhysicsBone;
		boneWorld = world.ToWorld( new Transform( bonelessCenter ) ).WithScale( world.Scale );
		go.WorldTransform = boneWorld;
		return go;
	}

	//The bodies just created, as the one group somebody can reach for.
	private void GatherGroup()
	{
		if ( Scene?.PhysicsWorld is not { } world )
			return;

		var group = new PhysicsGroup();
		var backend = new CorePhysicsGroupBackend( world );
		group.AttachBackend( backend );

		foreach ( var body in Bodies )
		{
			if ( body.Component?.PhysicsBody is not { } physics ) continue;

			backend.Add( physics, body.Component.GameObject?.Name );
		}

		PhysicsGroup = group;
	}

	private void DestroyPhysics()
	{

		if ( Renderer.IsValid() )
		{
			Renderer.ClearPhysicsBones();
		}

		BodyTransforms.Clear();

		if ( !PhysicsWereCreated )
			return;

		PhysicsDestroyCount++;

		foreach ( var joint in Joints )
		{
			if ( !joint.Component.IsValid() )
				continue;

			joint.Component.Destroy();

			Network?.Refresh( joint.Component );
		}

		foreach ( var collider in GetComponentsInChildren<Collider>( true ) )
		{
			if ( !collider.IsValid() ) continue;
			if ( !collider.GameObject.Flags.Contains( GameObjectFlags.PhysicsBone ) ) continue;

			collider.Destroy();

			Network?.Refresh( collider );
		}

		foreach ( var body in Bodies )
		{
			if ( !body.Component.IsValid() )
				continue;

			body.Component.GameObject.Flags &= ~GameObjectFlags.Absolute;
			body.Component.GameObject.Flags &= ~GameObjectFlags.PhysicsBone;
			body.Component.Destroy();

			Network?.Refresh( body.Component );
		}

		Bodies.Clear();
		Joints.Clear();
		PhysicsGroup = null;
		_appliedJointScales = null;

		PhysicsWereCreated = false;
	}

	void SetJointsEnabled( bool enabled )
	{
		foreach ( var joint in Joints )
		{
			if ( !joint.Component.IsValid() ) continue;

			joint.Component.Enabled = enabled;
		}
	}

	void SetBodiesEnabled( bool enabled )
	{
		foreach ( var body in Bodies )
		{
			if ( !body.Component.IsValid() ) continue;

			var world = body.Component.WorldTransform;
			var flags = body.Component.GameObject.Flags;
			var mask = GameObjectFlags.Absolute | GameObjectFlags.PhysicsBone;
			body.Component.GameObject.Flags = enabled ? flags | mask : flags & ~mask;
			body.Component.WorldTransform = world;
			body.Component.Enabled = enabled;
		}
	}

	void SetCollidersEnabled( bool enabled )
	{
		foreach ( var collider in GetComponentsInChildren<Collider>( true ) )
		{
			if ( !collider.IsValid() ) continue;
			if ( !collider.GameObject.Flags.Contains( GameObjectFlags.PhysicsBone ) ) continue;

			collider.Enabled = enabled;
		}
	}

	void SetComponentsEnabled( bool enabled )
	{
		if ( !PhysicsWereCreated ) return;

		if ( enabled )
		{
			SetBodiesEnabled( enabled );
			SetCollidersEnabled( enabled );
			SetJointsEnabled( enabled );
		}
		else
		{
			SetJointsEnabled( enabled );
			SetCollidersEnabled( enabled );
			SetBodiesEnabled( enabled );
		}
	}

	void EnableComponents() => SetComponentsEnabled( true );
	void DisableComponents() => SetComponentsEnabled( false );

	internal void OnModelReloaded()
	{
		DestroyPhysics();
		CreatePhysics();
	}

	//RINTEN_RAGDOLL_DEBUG=1 logs each joint's anchors and their gap; a nonzero gap makes limbs jump
	private static readonly bool ragdollDebug = Environment.GetEnvironmentVariable( "RINTEN_RAGDOLL_DEBUG" ) == "1";

	private void SayJoint( PhysicsGroupDescription.Joint desc, Body body1, Body body2,
		Transform frame1, Transform frame2 )
	{
		var bones = Model?.Bones?.AllBones;

		string named( int index ) => bones is not null && index >= 0 && index < bones.Count
			? bones[index].Name : $"#{index}";

		var at1 = body1.Component.WorldTransform.PointToWorld( frame1.Position * body1.Component.WorldScale );
		var at2 = body2.Component.WorldTransform.PointToWorld( frame2.Position * body2.Component.WorldScale );

		var rest1 = RestBonePosition( body1.Bone );
		var rest2 = RestBonePosition( body2.Bone );

		Log.Info( $"ragdoll {desc.Type} {named( body1.Bone )} -> {named( body2.Bone )}: " +
			$"gap {at1.Distance( at2 ) * 100.0f:0.00} cm, " +
			$"frame1 {frame1.Position.Length * 100.0f:0.00} cm, frame2 {frame2.Position.Length * 100.0f:0.00} cm, " +
			$"bodies apart {body1.Component.WorldPosition.Distance( body2.Component.WorldPosition ) * 100.0f:0.00} cm, " +
			$"rest apart {( rest1 is { } r1 && rest2 is { } r2 ? r1.Distance( r2 ) * 100.0f : -1.0f ):0.00} cm, " +
			$"scale {body1.Component.WorldScale} / {body2.Component.WorldScale}" );
	}
}
