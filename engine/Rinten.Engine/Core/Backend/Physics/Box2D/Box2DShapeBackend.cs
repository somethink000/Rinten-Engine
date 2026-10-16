using NativeEngine;
using System.Threading;
using Rinten.Native.Box2D;
using static Rinten.Native.Box2D.Box2D;

namespace Rinten;

//A physics shape in the plane: one or more Box2D shapes on the same body. Usually one. A hull seen side-on can have more corners than a Box2D polygon
//takes, and a mesh is a triangle each, so the managed shape is the set - every setting goes to every piece, and every piece maps back to this shape.
internal sealed unsafe class Box2DShapeBackend : IPhysicsShapeBackend
{
	//kg per square metre with no surface of its own: Surface's default, read as a slab a metre deep.
	internal const float DefaultDensity = 2000.0f;

	private static long nextSerial;

	private readonly Box2DBodyBackend body;
	private readonly List<b2ShapeId> ids = new();
	private List<Box2DPart> parts;

	//What this shape was last made as - a box, a sphere, a capsule, a mesh - whatever pieces the plane cuts it into at
	//the moment. An update makes it again as what it names, as a 3D shape is swapped for a new one. See GetShapeType.
	private PhysicsShapeType kind;

	//Still on its body: false once it is taken off or the body goes.
	private bool alive = true;

	private bool trigger;
	private float density = DefaultDensity;
	private float friction = 0.6f;
	private float restitution;
	private float rollingResistance;
	private float tangentSpeed;
	private byte collisionMask = (byte)(CollisionFunctionMask.EnableSolidContact | CollisionFunctionMask.EnableTouchEvent);
	private bool hasNoMass;
	private ulong tagMask;
	private readonly HashSet<uint> tags = new();
	private string materialName = "default";

	internal Box2DShapeBackend( Box2DBodyBackend body, PhysicsShapeType kind, List<Box2DPart> parts, bool trigger )
	{
		this.body = body;
		this.kind = kind;
		this.parts = parts ?? new List<Box2DPart>();
		this.trigger = trigger;
		Serial = Interlocked.Increment( ref nextSerial );
	}

	//The managed shape this backs, for the registry.
	internal PhysicsShape Owner { get; set; }

	//Unique for the life of the process; how contact pairs are counted.
	internal long Serial { get; }

	internal IReadOnlyList<Box2DPart> Parts => parts;
	internal IReadOnlyList<b2ShapeId> Ids => ids;
	internal HashSet<uint> Tags => tags;

	//Its tags as the bits of the compiled collision rules - what the world filters it by.
	internal ulong TagMask => tagMask;
	internal bool IgnoresTraces { get; private set; }

	/// <summary>Whether the shape is still on a body - not whether it has pieces. A box seen edge-on, or sized to nothing for the moment, has none and is still the collider's box: it has to stay valid so it can be sized back. It read invalid, and the collider could never reach it again.</summary>
	public bool IsValid => alive && body.IsValid;

	//-- native lifetime ------------------------------------------------------

	//Make the Box2D shapes. False when not one of the pieces would take.
	internal bool Build()
	{
		if ( !body.IsValid ) return false;

		var def = b2DefaultShapeDef();
		def.density = hasNoMass ? 0.0f : density;
		def.material.friction = friction;
		def.material.restitution = restitution;
		def.material.rollingResistance = rollingResistance;
		def.material.tangentSpeed = tangentSpeed;
		def.isSensor = trigger;
		def.enableSensorEvents = true;
		def.enableContactEvents = !trigger && TouchEvents;
		def.enableHitEvents = !trigger && TouchEvents;
		def.enableCustomFiltering = true;
		def.userData = (void*)tagMask;

		//The body works its mass out once, after every piece is on.
		def.updateBodyMass = false;

		foreach ( var part in parts )
		{
			var id = part.Create( body.Id, def );
			if ( !b2Shape_IsValid( id ) ) continue;

			ids.Add( id );
			if ( Owner is not null ) Box2DRegistry.Shapes[Box2DPlane.Key( id )] = Owner;
		}

		return ids.Count > 0;
	}

	//Off its body for good: its pieces go and it answers as gone from now on.
	internal void Remove( bool updateMass )
	{
		DestroyNative( updateMass );
		alive = false;
	}

	internal void DestroyNative( bool updateMass )
	{
		foreach ( var id in ids )
		{
			Box2DRegistry.Shapes.TryRemove( Box2DPlane.Key( id ), out _ );
			if ( b2Shape_IsValid( id ) ) b2DestroyShape( id, false );
		}

		ids.Clear();

		if ( updateMass ) body.ApplyMass();
	}

	//Drop the ids without touching Box2D, for when the body took its shapes with it.
	internal void Forget()
	{
		foreach ( var id in ids )
			Box2DRegistry.Shapes.TryRemove( Box2DPlane.Key( id ), out _ );

		ids.Clear();
		alive = false;
	}

	//Make it again with new pieces, keeping everything it was set to. None is an answer too - a box sized to
	//nothing across the plane - and it is taken: the old pieces went on colliding where the collider no longer was.
	private void Rebuild( List<Box2DPart> replacement ) => Rebuild( kind, replacement );

	private void Rebuild( PhysicsShapeType made, List<Box2DPart> replacement )
	{
		if ( !IsValid ) return;

		DestroyNative( false );
		kind = made;
		parts = replacement ?? new List<Box2DPart>();
		Build();
		body.ApplyMass();
	}

	public PhysicsBody GetBody() => body.Owner;

	/// <summary>The kind the shape was created as, not guessed from its current pieces</summary>
	public PhysicsShapeType GetShapeType() => kind;

	//-- collision filtering --------------------------------------------------

	public byte GetCollisionFunctionMask() => collisionMask;

	public void AddCollisionFunctionMask( byte mask )
	{
		collisionMask |= mask;
		ApplyTouchEvents();
	}

	public void RemoveCollisionFunctionMask( byte mask )
	{
		collisionMask &= (byte)~mask;
		ApplyTouchEvents();
	}

	private bool TouchEvents => (collisionMask & (byte)CollisionFunctionMask.EnableTouchEvent) != 0;

	internal bool ContactEventsEnabled
	{
		get => TouchEvents;
		set
		{
			if ( value ) collisionMask |= (byte)CollisionFunctionMask.EnableTouchEvent;
			else collisionMask &= (byte)~CollisionFunctionMask.EnableTouchEvent;
			ApplyTouchEvents();
		}
	}

	private void ApplyTouchEvents()
	{
		if ( trigger ) return;

		foreach ( var id in ids )
		{
			b2Shape_EnableContactEvents( id, TouchEvents );
			b2Shape_EnableHitEvents( id, TouchEvents );
		}
	}

	public bool IsTrigger() => trigger;

	/// <summary>Box2D decides sensor-ness when a shape is made, so this makes it again.</summary>
	public void SetTrigger( bool value )
	{
		if ( trigger == value ) return;

		trigger = value;
		Rebuild( parts );
	}

	public void SetIgnoreTraces( bool ignore ) => IgnoresTraces = ignore;

	public void SetHasNoMass( bool noMass )
	{
		if ( hasNoMass == noMass ) return;
		hasNoMass = noMass;
		ApplyDensity();
	}

	private void ApplyDensity()
	{
		foreach ( var id in ids )
			b2Shape_SetDensity( id, hasNoMass ? 0.0f : density, false );

		body.ApplyMass();
	}

	//Density as a shape made now would get it.
	internal float Density => hasNoMass ? 0.0f : density;

	//-- tags -----------------------------------------------------------------

	public bool HasTag( uint tag ) => tags.Contains( tag );

	public bool AddTag( uint tag )
	{
		if ( !tags.Add( tag ) ) return false;
		ApplyTags();
		return true;
	}

	public bool RemoveTag( uint tag )
	{
		if ( !tags.Remove( tag ) ) return false;
		ApplyTags();
		return true;
	}

	public bool ClearTags()
	{
		tags.Clear();
		ApplyTags();
		return true;
	}

	//The tags as the bits of the world's compiled rules, in each piece's user data.
	internal void ApplyTags()
	{
		tagMask = body.World?.CollisionRules?.MaskFor( tags ) ?? 0;

		foreach ( var id in ids )
			b2Shape_SetUserData( id, (void*)tagMask );
	}

	//-- material -------------------------------------------------------------

	public string GetMaterialName() => materialName;

	public void SetMaterialIndex( string name )
	{
		materialName = string.IsNullOrWhiteSpace( name ) ? "default" : name;

		var surface = Surface.FindByName( materialName );
		if ( surface is null ) return;

		friction = surface.Friction;
		restitution = surface.Elasticity;
		rollingResistance = surface.RollingResistance;
		density = surface.Density;

		ApplyMaterial();
		ApplyDensity();
	}

	/// <summary>Per-triangle materials: a Box2D shape has one, so the shape's own stands for all of them.</summary>
	public void SetSurfaceIndex( int surfaceIndex, int index ) { }

	public float GetFriction() => friction;

	public void SetFriction( float value )
	{
		friction = value;
		ApplyMaterial();
	}

	public void SetElasticity( float value )
	{
		restitution = value;
		ApplyMaterial();
	}

	public void SetRollingResistance( float value )
	{
		rollingResistance = value;
		ApplyMaterial();
	}

	/// <summary>A conveyor belt in the plane moves along its own surface, so only the speed along it (X) means anything.</summary>
	public Vector3 GetLocalVelocity() => new( tangentSpeed, 0, 0 );

	public void SetLocalVelocity( Vector3 velocity )
	{
		tangentSpeed = velocity.x;
		ApplyMaterial();
	}

	private void ApplyMaterial()
	{
		foreach ( var id in ids )
		{
			var material = b2Shape_GetSurfaceMaterial( id );
			material.friction = friction;
			material.restitution = restitution;
			material.rollingResistance = rollingResistance;
			material.tangentSpeed = tangentSpeed;
			b2Shape_SetSurfaceMaterial( id, &material );
		}
	}

	//-- geometry -------------------------------------------------------------

	public Sphere AsSphere()
	{
		foreach ( var part in parts )
		{
			if ( part.Type == b2ShapeType.b2_circleShape )
				return new Sphere( body.FromLocal( part.Circle.center ), part.Circle.radius );
		}

		return default;
	}

	public Capsule AsCapsule()
	{
		foreach ( var part in parts )
		{
			if ( part.Type == b2ShapeType.b2_capsuleShape )
				return new Capsule( body.FromLocal( part.Capsule.center1 ), body.FromLocal( part.Capsule.center2 ), part.Capsule.radius );

			//Seen end on, a capsule is a circle - see CapsuleParts.
			if ( part.Type == b2ShapeType.b2_circleShape )
			{
				var middle = body.FromLocal( part.Circle.center );
				return new Capsule( middle, middle, part.Circle.radius );
			}
		}

		return default;
	}

	public BBox BuildBounds()
	{
		var z = body.GetPosition().z;
		BBox? bounds = null;

		foreach ( var id in ids )
		{
			if ( !b2Shape_IsValid( id ) ) continue;

			var box = Box2DPlane.Box( b2Shape_GetAABB( id ), z );
			bounds = bounds.HasValue ? bounds.Value.AddBBox( box ) : box;
		}

		return bounds ?? default;
	}

	/// <summary>Bounds in the body's frame, from the outline, with the plane's depth given a thickness.</summary>
	public BBox LocalBounds()
	{
		Span<Vector2> outline = stackalloc Vector2[Box2DPart.MaxOutline];
		BBox? bounds = null;

		foreach ( var part in parts )
		{
			var count = part.Outline( outline );

			for ( var i = 0; i < count; i++ )
			{
				var front = body.FromLocal( outline[i], Box2DPlane.BoundsHalfDepth );
				var back = body.FromLocal( outline[i], -Box2DPlane.BoundsHalfDepth );

				var box = new BBox( front, front ).AddPoint( back );
				bounds = bounds.HasValue ? bounds.Value.AddBBox( box ) : box;
			}
		}

		return bounds ?? default;
	}

	public void UpdateSphereShape( Vector3 center, float radius )
		=> Rebuild( PhysicsShapeType.SHAPE_SPHERE, body.SphereParts( center, radius ) );

	public void UpdateCapsuleShape( Vector3 center1, Vector3 center2, float radius )
		=> Rebuild( PhysicsShapeType.SHAPE_CAPSULE, body.CapsuleParts( center1, center2, radius ) );

	public void UpdateBoxShape( Vector3 center, Rotation rotation, Vector3 extents )
		=> Rebuild( PhysicsShapeType.SHAPE_HULL, body.BoxParts( center, rotation, extents ) );

	public void UpdateHullShape( Vector3 position, Rotation rotation, int vertexCount, IntPtr vertices )
		=> Rebuild( PhysicsShapeType.SHAPE_HULL, body.HullParts( Box2DBodyBackend.Copy( vertices, vertexCount ), new Transform( position, rotation ) ) );

	public void UpdateMeshShape( int vertexCount, IntPtr vertices, int indexCount, IntPtr indices )
		=> Rebuild( PhysicsShapeType.SHAPE_MESH, body.MeshParts( Box2DBodyBackend.Copy( vertices, vertexCount ), Box2DBodyBackend.CopyIndices( indices, indexCount ), Transform.Zero ) );

	/// <summary>Height fields have no place in the plane; terrain there is a mesh or a set of boxes.</summary>
	public void UpdateHeightShape( IntPtr heights, IntPtr materials, int x, int y, int width, int height, float sizeScale, float heightScale ) { }

	/// <summary>The pieces as flat triangles in the body's frame. Navigation reads these for 3D worlds; in the plane they are what there is.</summary>
	public void GetTriangulation( out Vector3[] positions, out uint[] indices )
	{
		var points = new List<Vector3>();
		var order = new List<int>();

		AppendTriangles( points, order );

		positions = points.ToArray();
		indices = new uint[order.Count];
		for ( var i = 0; i < indices.Length; i++ ) indices[i] = (uint)order[i];
	}

	public void AppendTriangulation( BBox bounds, List<Vector3> positions, List<int> indices )
		=> AppendTriangles( positions, indices );

	private void AppendTriangles( List<Vector3> positions, List<int> indices )
	{
		Span<Vector2> outline = stackalloc Vector2[Box2DPart.MaxOutline];

		foreach ( var part in parts )
		{
			var count = part.Outline( outline );
			if ( count < 3 ) continue;

			var start = positions.Count;
			for ( var i = 0; i < count; i++ )
				positions.Add( body.FromLocal( outline[i] ) );

			for ( var i = 1; i + 1 < count; i++ )
			{
				indices.Add( start );
				indices.Add( start + i );
				indices.Add( start + i + 1 );
			}
		}
	}

	public Vector3[] GetOutlineSegments()
	{
		Span<Vector2> outline = stackalloc Vector2[Box2DPart.MaxOutline];
		var segments = new List<Vector3>();

		foreach ( var part in parts )
		{
			var count = part.Outline( outline );
			if ( count < 2 ) continue;

			var closed = count > 2;
			var edges = closed ? count : 1;

			for ( var i = 0; i < edges; i++ )
			{
				segments.Add( body.FromLocal( outline[i] ) );
				segments.Add( body.FromLocal( outline[(i + 1) % count] ) );
			}
		}

		return segments.ToArray();
	}

	//-- queries --------------------------------------------------------------

	public bool IsTouching( PhysicsShape shape, bool triggersOnly )
	{
		if ( shape?.Backend is not Box2DShapeBackend other ) return false;
		if ( triggersOnly && !trigger && !other.trigger ) return false;

		return body.World?.Events.AreTouching( Serial, other.Serial ) ?? false;
	}

	public void ResetProxy() { }
}
