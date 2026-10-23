namespace Rinten;

//A physics shape on our own core, over Box3D.
internal sealed unsafe class CorePhysicsShapeBackend : IPhysicsShapeBackend
{
	private readonly PhysicsBody owner;
	private ulong shape;

	//The collision function mask. Box3D has no equivalent single field - solid contact is a shape's sensor
	//flag and touch events are per-shape switches - so the mask is kept here and applied where it maps.
	private byte collisionMask;

	private readonly HashSet<uint> tags = new();

	public CorePhysicsShapeBackend( ulong shape, PhysicsBody owner )
	{
		this.shape = shape;
		this.owner = owner;

		//Solid contact and touch events on, the defaults for a new shape.
		collisionMask = (byte)(CollisionFunctionMask.EnableSolidContact | CollisionFunctionMask.EnableTouchEvent);
	}

	internal ulong Handle => shape;

	//Its tags as the bits of the compiled collision rules - what the world filters it by.
	internal ulong TagMask => CorePhysics.ShapeGetTagMask( shape );

	public bool IsValid => shape != 0 && CorePhysics.ShapeValid( shape );

	internal void DestroyNative()
	{
		if ( shape == 0 ) return;

		CoreShapeRegistry.Remove( shape );
		CorePhysics.ShapeDestroy( shape );
		shape = 0;
	}

	//Drop the handle without destroying anything, for when the body took the shape with it.
	internal void Forget()
	{
		if ( shape == 0 ) return;

		CoreShapeRegistry.Remove( shape );
		shape = 0;
	}

	//Tags this shape carries, for trace filtering.
	internal HashSet<uint> Tags => tags;

	public PhysicsBody GetBody() => owner;

	public PhysicsShapeType GetShapeType() => (PhysicsShapeType)CorePhysics.ShapeGetType( shape );

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

	//Whether this shape reports contacts to the event pump.
	internal bool ContactEventsEnabled
	{
		get => CorePhysics.ShapeContactEventsEnabled( shape );
		set => CorePhysics.ShapeEnableContactEvents( shape, value );
	}

	//The touch-event bit of the collision mask is what Box3D calls contact events, so the two are kept in step.
	private void ApplyTouchEvents()
		=> ContactEventsEnabled = (collisionMask & (byte)CollisionFunctionMask.EnableTouchEvent) != 0;

	public bool IsTrigger() => CorePhysics.ShapeIsTrigger( shape );

	/// <summary>Box3D fixes sensor-ness at creation, so the core rebuilds the shape from its stored geometry</summary>
	public void SetTrigger( bool trigger )
	{
		if ( IsTrigger() == trigger ) return;

		Swap( _ => CorePhysics.ShapeRebuildTrigger( shape, trigger ) );
	}

	//Whether traces should pretend this shape is not there. Box3D could do this with category and mask bits, but those also decide what the shape collides
	//with, so borrowing them would make a shape that traces skip also fall through the world. Traces filter on this instead - see CorePhysicsTrace.
	internal bool IgnoresTraces { get; private set; }

	public void SetIgnoreTraces( bool ignore ) => IgnoresTraces = ignore;

	/// <summary>Solid, but contributes nothing to the body's mass. Only a change touches the simulation: writing the density on every configure pass rebuilt the body's mass from volume each time, which threw away both a hand-set MassOverride and the density the surface had put there.</summary>
	public void SetHasNoMass( bool noMass )
	{
		if ( noMass == hasNoMass ) return;
		hasNoMass = noMass;

		SetDensity( noMass ? 0.0f : SurfaceDensity );
	}

	private bool hasNoMass;

	//The density of the surface this shape carries, or the default's.
	private float SurfaceDensity => Surface.FindByName( materialName )?.Density ?? DefaultDensity;

	//Density, with the body's mass rebuilt from it - and a hand-set mass put back on top, since a rebuild is not a request to lose that.
	private void SetDensity( float density )
	{
		CorePhysics.ShapeSetDensity( shape, density, true );

		if ( owner?.Backend is CorePhysicsBodyBackend body )
			body.RestoreExplicitMass();
	}

	//kg/m^3 for a shape with no surface material of its own. Matches Surface's default.
	private const float DefaultDensity = 2000.0f;

	//-- tags ----------------------------------------------------------------- Tags drive collision rules, and those are not wired to
	//Box3D's category and mask bits yet. Storing them keeps the managed side honest in the meantime - a tag that was set reads back as set.

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

	//Push this shape's tags down as the bit mask the core filters with, and turn on per-step contact vetoing only if one of them takes part
	//in a trigger rule - that veto is what makes a trigger pair touch without pushing, and it costs solver time on every shape that has it.
	internal void ApplyTags()
	{
		if ( owner?.World?.Backend is not CorePhysicsWorldBackend world ) return;

		var rules = world.CollisionRules;
		if ( rules is null )
		{
			CorePhysics.ShapeSetTagMask( shape, 0 );
			CorePhysics.ShapeEnablePresolve( shape, false );
			return;
		}

		var mask = rules.MaskFor( tags );
		CorePhysics.ShapeSetTagMask( shape, mask );
		CorePhysics.ShapeEnablePresolve( shape, (mask & rules.TriggerTags) != 0 );
	}

	//-- material -------------------------------------------------------------

	private string materialName = "default";

	public string GetMaterialName() => materialName;

	/// <summary>Surface properties by name. The named <see cref="Surface"/> is a managed resource, so the lookup happens here and only the numbers it carries reach the simulation.</summary>
	public void SetMaterialIndex( string name )
	{
		materialName = string.IsNullOrWhiteSpace( name ) ? "default" : name;

		var surface = Surface.FindByName( materialName );
		if ( surface is null ) return;

		CorePhysics.ShapeSetFriction( shape, surface.Friction );
		CorePhysics.ShapeSetRestitution( shape, surface.Elasticity );

		if ( !hasNoMass )
			SetDensity( surface.Density );
	}

	/// <summary>One entry of a mesh or height field's per-triangle material table - what a painted terrain material or a mesh's material slot actually feels like.</summary>
	public void SetSurfaceIndex( int surfaceIndex, int index )
	{
		var surface = surfaceIndex >= 0 ? Surface.FindByIndex( surfaceIndex ) : null;
		surface ??= Surface.FindByName( "default" );
		if ( surface is null ) return;

		CorePhysics.ShapeSetMeshMaterial( shape, index,
			surface.Friction, surface.Elasticity, 0.0f );
	}

	public float GetFriction() => CorePhysics.ShapeGetFriction( shape );
	public void SetFriction( float friction ) => CorePhysics.ShapeSetFriction( shape, friction );

	public void SetElasticity( float elasticity ) => CorePhysics.ShapeSetRestitution( shape, elasticity );
	public void SetRollingResistance( float resistance ) => CorePhysics.ShapeSetRollingResistance( shape, resistance );

	/// <summary>Conveyor-belt surface velocity.</summary>
	public Vector3 GetLocalVelocity() => CorePhysics.ShapeGetSurfaceVelocity( shape );

	public void SetLocalVelocity( Vector3 velocity )
		=> CorePhysics.ShapeSetSurfaceVelocity( shape, velocity );

	//-- geometry -------------------------------------------------------------

	public Sphere AsSphere() => CorePhysics.ShapeAsSphere( shape );
	public Capsule AsCapsule() => CorePhysics.ShapeAsCapsule( shape );

	public BBox BuildBounds() => CorePhysics.ShapeGetBounds( shape );

	/// <summary>Body-space bounds: exact for primitives, otherwise the world box moved in, which may be larger</summary>
	public BBox LocalBounds()
	{
		switch ( GetShapeType() )
		{
			case PhysicsShapeType.SHAPE_SPHERE:
			{
				var sphere = AsSphere();
				return BBox.FromPositionAndSize( sphere.Center, sphere.Radius * 2.0f );
			}

			case PhysicsShapeType.SHAPE_CAPSULE:
			{
				var capsule = AsCapsule();
				var bounds = new BBox( capsule.CenterA, capsule.CenterA );
				bounds = bounds.AddPoint( capsule.CenterB );
				return bounds.Grow( capsule.Radius );
			}
		}

		var world = BuildBounds();
		var origin = owner.IsValid() ? owner.Position : Vector3.Zero;

		return new BBox( world.Mins - origin, world.Maxs - origin );
	}

	//Box3D shapes are immutable once created, so changing the geometry means building the new shape, then dropping the old one, and pointing
	//the same managed object at the result. Building first means a failed update leaves the old shape in place rather than a hole in the world.

	public void UpdateSphereShape( Vector3 center, float radius )
		=> Swap( body => body.AddSphereNative( center, radius, Density, IsTrigger() ) );

	public void UpdateCapsuleShape( Vector3 center1, Vector3 center2, float radius )
		=> Swap( body => body.AddCapsuleNative( center1, center2, radius, Density, IsTrigger() ) );

	public void UpdateBoxShape( Vector3 center, Rotation rotation, Vector3 extents )
		=> Swap( body => body.AddBoxNative( center, rotation, extents, Density, IsTrigger() ) );

	public unsafe void UpdateHullShape( Vector3 position, Rotation rotation, int vertexCount, IntPtr vertices )
		=> Swap( body => body.AddHullNative( position, rotation, vertexCount, vertices, Density, IsTrigger() ) );

	public unsafe void UpdateMeshShape( int vertexCount, IntPtr vertices, int indexCount, IntPtr indices )
		=> Swap( body => body.AddMeshNative( vertexCount, vertices, indexCount, indices, IsTrigger() ) );

	//Density of this shape, so a rebuild keeps the mass it had.
	private float Density => CorePhysics.ShapeGetDensity( shape );

	private void Swap( Func<CorePhysicsBodyBackend, ulong> build )
	{
		if ( owner?.Backend is not CorePhysicsBodyBackend body ) return;

		var friction = GetFriction();
		var restitution = CorePhysics.ShapeGetRestitution( shape );

		var replacement = build( body );
		if ( replacement == 0 ) return;

		DestroyNative();
		shape = replacement;

		//A new shape starts on Box3D's defaults, and the tags live in the native shape's user data, so everything this one carried has to be put back.
		SetFriction( friction );
		CorePhysics.ShapeSetRestitution( shape, restitution );
		ApplyTouchEvents();
		ApplyTags();

		body.ReplaceShape( ownerShape, replacement );
	}
	/// <summary>Terrain edits. Box3D height fields are immutable, so the grid is patched and the shape rebuilt underneath the same managed object - see <see cref="CoreHeightField"/>.</summary>
	public unsafe void UpdateHeightShape( IntPtr heights, IntPtr materials, int x, int y, int width, int height, float sizeScale, float heightScale )
	{
		if ( heightField is null ) return;

		if ( heights != IntPtr.Zero )
			heightField.PatchHeights( (ushort*)heights, sizeScale, heightScale );

		if ( materials != IntPtr.Zero )
			heightField.PatchMaterials( (byte*)materials, x, y, width, height );

		Rebuild();
	}

	private CoreHeightField heightField;

	internal void AttachHeightField( CoreHeightField state ) => heightField = state;

	private void Rebuild()
	{
		if ( owner?.Backend is not CorePhysicsBodyBackend body ) return;

		var replacement = heightField.Build();
		if ( replacement == 0 ) return;

		//Only drop the old shape once the new one exists, so a failed rebuild leaves the terrain solid rather than leaving a hole in the world.
		DestroyNative();

		shape = replacement;
		ApplyTouchEvents();
		ApplyTags();

		body.ReplaceShape( ownerShape, replacement );
	}

	//The managed shape this backend belongs to. Needed when the native shape is replaced, so the handle registry keeps pointing at the same object.
	internal PhysicsShape ownerShape;

	public void GetTriangulation( out Vector3[] positions, out uint[] indices )
	{
		var points = new List<Vector3>();
		var order = new List<int>();

		Weld( Triangulate( null ), points, order );

		positions = points.ToArray();

		indices = new uint[order.Count];
		for ( var i = 0; i < indices.Length; ++i )
			indices[i] = (uint)order[i];
	}

	//Welds triangle corners into shared vertices by exact match; shared corners are identical values
	private static void Weld( Vector3[] corners, List<Vector3> positions, List<int> indices )
	{
		if ( corners.Length == 0 ) return;

		var start = positions.Count;
		var seen = new Dictionary<Vector3, int>( corners.Length );

		foreach ( var corner in corners )
		{
			if ( !seen.TryGetValue( corner, out var at ) )
			{
				at = positions.Count - start;
				seen[corner] = at;
				positions.Add( corner );
			}

			indices.Add( start + at );
		}
	}

	//This shape's triangles in the body's local space, optionally only those near bounds, which is in world space.
	internal Vector3[] Triangulate( BBox? bounds )
	{
		if ( shape == 0 ) return Array.Empty<Vector3>();

		var triangles = bounds.HasValue
			? CorePhysics.ShapeTriangulate( shape, bounds.Value, Span<float>.Empty )
			: CorePhysics.ShapeTriangulate( shape, Span<float>.Empty );

		if ( triangles <= 0 ) return Array.Empty<Vector3>();

		var positions = new Vector3[triangles * 3];

		fixed ( Vector3* into = positions )
		{
			//Vector3 is three floats and nothing else, so the corners are written straight into the array they will be read out of.
			var span = new Span<float>( into, triangles * 9 );

			if ( bounds.HasValue ) CorePhysics.ShapeTriangulate( shape, bounds.Value, span );
			else CorePhysics.ShapeTriangulate( shape, span );
		}

		return positions;
	}

	public void AppendTriangulation( BBox bounds, List<Vector3> positions, List<int> indices )
		=> Weld( Triangulate( bounds ), positions, indices );

	public Vector3[] GetOutlineSegments()
	{
		if ( shape == 0 ) return Array.Empty<Vector3>();

		var points = CorePhysics.ShapeOutline( shape, Span<float>.Empty );
		if ( points <= 0 ) return Array.Empty<Vector3>();

		var segments = new Vector3[points];

		fixed ( Vector3* into = segments )
		{
			CorePhysics.ShapeOutline( shape, new Span<float>( into, points * 3 ) );
		}

		return segments;
	}

	//-- queries --------------------------------------------------------------

	public bool IsTouching( PhysicsShape other, bool triggersOnly )
	{
		if ( other?.Backend is not CorePhysicsShapeBackend core ) return false;
		if ( triggersOnly && !core.IsTrigger() ) return false;
		if ( owner?.World?.Backend is not CorePhysicsWorldBackend world ) return false;

		return world.Events.AreTouching( shape, core.Handle );
	}

	public void ResetProxy() { }
}
