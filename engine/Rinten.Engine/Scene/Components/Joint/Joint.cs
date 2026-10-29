using System.Text.Json.Serialization;

namespace Rinten;

[Expose]
public abstract class Joint : Component, Component.ExecuteInEditor
{
	private PhysicsJoint _joint;

	internal PhysicsJoint NativeJoint => _joint;
	private PhysicsBody _worldBody;

	private bool started;
	private bool enableCollision;
	private float strength;
	private float angularStrength;

	public enum AttachmentMode
	{
		/// <summary>Local frames are calculated automatically using this component transform and connected body transform.</summary>
		Auto,

		/// <summary>Local frames are set manually. See <see cref="LocalFrame1"/>, <see cref="LocalFrame2"/></summary>
		LocalFrames,
	}

	/// <summary>Are local frames calculated automatically or set manually. See <see cref="LocalFrame1"/>, <see cref="LocalFrame2"/></summary>
	[Property, Hide]
	public AttachmentMode Attachment { get; set; }

	/// <summary>Only used on joint creation. See <see cref="AttachmentMode.LocalFrames"/></summary>
	[Property, Hide]
	public Transform LocalFrame1 { get; set; }

	/// <summary>Only used on joint creation. See <see cref="AttachmentMode.LocalFrames"/></summary>
	[Property, Hide]
	public Transform LocalFrame2 { get; set; }

	//Frames the joint was first built with in play, reused on rebuilds; authored frames stay untouched
	private (Transform frame1, Transform frame2)? builtFrames;

	//The frames to build the joint on: the authored pair, or the automatic pair the joint
	//type worked out from its two points - and in play, the pair it was first built with.
	protected (Transform frame1, Transform frame2) FramesFor( Transform auto1, Transform auto2 )
	{
		if ( builtFrames is { } built )
			return built;

		var frames = Attachment == AttachmentMode.Auto ? (auto1, auto2) : (LocalFrame1, LocalFrame2);

		if ( !Scene.IsEditor )
			builtFrames = frames;

		return frames;
	}

	/// <summary>The body this joint is anchored to. If this is null then it will use the current GameObject as the anchor.</summary>
	[Property, Advanced]
	public GameObject AnchorBody
	{
		get => field;
		set
		{
			if ( value == field )
				return;

			field = value;

			//Frames are relative to the bodies; new bodies mean new frames.
			builtFrames = null;
			CreateJoint();
		}
	}

	/// <summary>Game object to find the body to attach this joint to.</summary>
	[Property]
	public GameObject Body
	{
		get => field;
		set
		{
			if ( value == field )
				return;

			field = value;

			builtFrames = null;
			CreateJoint();
		}
	}

	/// <summary>Enable or disable collision between the two bodies.</summary>
	[Property]
	public bool EnableCollision
	{
		get => enableCollision;
		set
		{
			enableCollision = value;
			if ( _joint.IsValid() )
				_joint.Collisions = enableCollision;
		}
	}

	/// <summary>Is the joint broken on start.</summary>
	[Property, Group( "Breaking" )]
	public bool StartBroken { get; set; }

	/// <summary>Strength of the linear constraint. If it takes any more energy than this, it'll break. Nought is a joint that does not break.</summary>
	[Property, Group( "Breaking" )]
	public float BreakForce
	{
		get => strength;
		set
		{
			strength = value;
			if ( _joint.IsValid() )
				_joint.Strength = Threshold( value );
		}
	}

	/// <summary>Strength of the angular constraint. If it takes any more energy than this, it'll break. Nought is a joint that does not break.</summary>
	[Property, Group( "Breaking" )]
	public float BreakTorque
	{
		get => angularStrength;
		set
		{
			angularStrength = value;
			if ( _joint.IsValid() )
				_joint.AngularStrength = Threshold( value );
		}
	}

	//Strength zero means unbreakable; the solver reads zero as "break now", so map it to MaxValue
	private static float Threshold( float strength ) => strength > 0.0f ? strength : float.MaxValue;

	/// <summary>Called when the joint breaks.</summary>
	[Property, Group( "Breaking" )] public Action OnBreak { get; set; }

	/// <summary>Current linear stress applied to the joint.</summary>
	[Group( "Breaking" )]
	[Property, JsonIgnore]
	public float LinearStress => _joint.IsValid() ? _joint.LinearImpulse : default;

	/// <summary>Current angular stress applied to the joint.</summary>
	[Group( "Breaking" )]
	[Property, JsonIgnore]
	public float AngularStress => _joint.IsValid() ? _joint.AngularImpulse : default;

	/// <summary>Is the joint currently broken and inactive.</summary>
	[Group( "Breaking" )]
	[Property, JsonIgnore]
	public bool IsBroken { get; private set; }

	//The source physics body this joint is attached to.
	public PhysicsBody Body1 => _joint.IsValid() ? _joint.Body1 : null; //I would like to hide this

	//The source GameObject we're connected to
	public GameObject Object1 => Body1?.GameObject; //purposely not named GameObject1 to avoid confusion with Component.GameObject

	//The target physics body this joint is constraining.
	public PhysicsBody Body2 => _joint.IsValid() ? _joint.Body2 : null; //I would like to hide this

	//The target GameObject we're connected to
	public GameObject Object2 => Body2?.GameObject; //purposely not named GameObject2 to avoid confusion with Component.GameObject

	/// <summary>A specific point this joint is attached at on <see cref="Body1"/></summary>
	public PhysicsPoint Point1
	{
		get => _joint.IsValid() ? _joint.Point1 : default;
		set
		{
			if ( _joint.IsValid() )
				_joint.Point1 = value;
		}
	}

	/// <summary>A specific point this joint is attached at on <see cref="Body2"/></summary>
	public PhysicsPoint Point2
	{
		get => _joint.IsValid() ? _joint.Point2 : default;
		set
		{
			if ( _joint.IsValid() )
				_joint.Point2 = value;
		}
	}

	protected override void OnStart()
	{
		base.OnStart();

		started = true;

		IsBroken = StartBroken;

		CreateJoint();
	}

	protected override void OnDestroy()
	{
		base.OnDestroy();

		started = false;

		DestroyJoint();
	}

	protected override void OnEnabled()
	{
		base.OnEnabled();

		CreateJoint();
	}

	protected override void OnDisabled()
	{
		base.OnDisabled();

		DestroyJoint();
	}

	//Joint type implementation.
	protected abstract PhysicsJoint CreateJoint( PhysicsPoint point1, PhysicsPoint point2 );

	internal static PhysicsBody FindPhysicsBody( GameObject go, GameObject source = null )
	{
		if ( go is null ) return null;
		if ( go is Scene ) return null;

		if ( go.Components.TryGet<Rigidbody>( out var rb ) )
		{
			if ( rb.PhysicsBody is not null )
				return rb.PhysicsBody;
		}

		if ( go.Components.TryGet<Collider>( out var collider ) )
		{
			if ( collider.KeyBody is not null )
				return collider.KeyBody;
		}

		return FindPhysicsBody( go.Parent, source );
	}

	private void CreateJoint()
	{
		if ( !started )
			return;

		DestroyJoint();

		if ( IsBroken )
			return;

		var body1Go = AnchorBody.IsValid() ? AnchorBody : GameObject; //fall back to self if no anchor body set
		var body1 = FindPhysicsBody( body1Go, body1Go );
		if ( !body1.IsValid() )
			return;

		var body2Go = Body;
		var body2 = FindPhysicsBody( body2Go, body2Go );
		if ( !body2.IsValid() )
			body2 = Scene?.PhysicsWorld?.Body;

		if ( !body2.IsValid() )
		{
			//Create a new world reference body if all else fails. This shouldn't be needed when scenes sets the world reference body.
			_worldBody = new PhysicsBody( body1.World );
			body2 = _worldBody;
		}

		// Both ends on the same body (e.g. two children of one prop) - nothing to constrain
		if ( body1 == body2 )
			return;

		var world = body1Go.WorldTransform;

		//Anchor is this component transform
		var anchor1 = body1.Transform.ToLocal( world );

		//Connected anchor is the connected body transform if it exists, otherwise this component transform
		var anchor2 = Body.IsValid() ? body2.Transform.ToLocal( Body.WorldTransform ) : body2.Transform.ToLocal( world );

		var point1 = new PhysicsPoint( body1, anchor1.Position, anchor1.Rotation );
		var point2 = new PhysicsPoint( body2, anchor2.Position, anchor2.Rotation );

		_joint = CreateJoint( point1, point2 );
		if ( _joint.IsValid() )
		{
			body1?.AddJoint( this );
			body2?.AddJoint( this );

			_joint.Collisions = EnableCollision;
			_joint.Strength = Threshold( BreakForce );
			_joint.AngularStrength = Threshold( BreakTorque );
			_joint.OnBreak += Break;
		}
	}

	protected virtual void DestroyJoint()
	{
		_joint?.Body1?.RemoveJoint( this );
		_joint?.Body2?.RemoveJoint( this );

		_joint?.Remove();
		_joint = null;

		_worldBody?.Remove();
		_worldBody = null;
	}

	[Button, Group( "Breaking" ), ShowIf( nameof( IsBroken ), false )]
	public void Break()
	{
		if ( Scene.IsEditor )
			return;

		if ( !_joint.IsValid() )
			return;

		if ( IsBroken )
			return;

		OnBreak?.Invoke();

		DestroyJoint();

		IsBroken = true;
	}

	[Button, Group( "Breaking" ), ShowIf( nameof( IsBroken ), true )]
	public void Unbreak()
	{
		if ( Scene.IsEditor )
			return;

		if ( !IsBroken )
			return;

		IsBroken = false;

		CreateJoint();
	}

	protected override void DrawGizmos()
	{
		if ( !Body.IsValid() )
			return;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 1;
		Gizmo.Draw.Color = Gizmo.Colors.Green.WithAlpha( Gizmo.IsSelected ? 1.0f : 0.75f );

		using var scope = Gizmo.Scope();

		//use world space
		Gizmo.Transform = global::Transform.Zero;

		//if we have a joint, use its raw data
		if ( _joint.IsValid() )
		{
			Gizmo.Draw.Line( _joint.Point1.Transform.Position, _joint.Point2.Transform.Position );
		}
		else
		{
			var body1Go = AnchorBody.IsValid() ? AnchorBody : GameObject;
			var body2Go = Body;

			var a = body1Go.WorldPosition;
			var b = body2Go.WorldPosition;

			Gizmo.Draw.Sprite( a, 0.005f, Texture.White );
			Gizmo.Draw.Line( a, b );
			Gizmo.Draw.Sprite( b, 0.005f, Texture.White );
		}
	}
}
