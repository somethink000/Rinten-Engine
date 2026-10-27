using NativeEngine;

namespace Rinten;

/// <summary>A model scene object that can be rendered within a <see cref="SceneWorld"/>.</summary>
[Expose]
public partial class SceneObject : IHandle
{
	//Cull pass's per-object slot for state kept between frames, or <0 if none yet; freed with the object
	internal int CullSlot = -1;

	#region IHandle
	//A pointer to the actual native object


	//IHandle implementation
	void IHandle.HandleInit( IntPtr ptr )
	{
		//Nothing hands us an object any more - a scene object is made here and drawn by our own renderer.
	}

	void IHandle.HandleDestroy() => OnNativeDestroy();

	//Alive means in a world, not "has a pointer". Most of these have no native half at all now, and everything that asks IsValid()
	//- components before they update one, the world before it hands its list out - is asking whether it is still there to be drawn.
	bool IHandle.HandleValid() => World is not null;

	#endregion

	RenderAttributes _attributes;

	//One of its own where there is no native half to hang them off, which is nearly always now. Never null: everything that sets an attribute does it straight off this property, and an object that has none is an object that draws with the defaults, not one that throws.
	/// <summary>Per-object shader parameters.</summary>
	public RenderAttributes Attributes
	{
		get
		{
			//Reading doesn't mark the object changed; the bag tracks its own writes via RenderAttributes.Version
			return _attributes ??= new RenderAttributes();
		}
	}

	//How many times this one's attributes have been written to, or nought where it has none.
	internal int AttributesVersion => _attributes?.Version ?? 0;

	//The attributes if any were ever asked for, without asking - for the renderer, which only reads.
	internal RenderAttributes AttributesIfAny => _attributes;

	//Whether this object carries attributes - the renderer's cache keeps only what has none, as they have no version
	internal bool HasAttributes => _attributes is { } held && ( held.Count > 0 || held.Combos.Count > 0 );

	//Bumped on any change to how it is drawn, so the renderer table can skip unchanged ones
	internal int Version { get; private set; }

	//Like Version but not bumped by transform or tint, which the renderer can patch in place cheaply
	internal int DrawVersion { get; private set; }

	internal void Touched()
	{
		DrawVersion++;
		TouchedPlacement();
	}

	//Where it stands or what colour it is - see DrawVersion.
	internal void TouchedPlacement()
	{
		Version++;
		World?.Touch();
	}

	/// <summary>The scene world this object belongs to.</summary>
	public SceneWorld World { get; internal set; }

	//All scene object state lives here; the native half is write-only for kinds not yet moved over
	private Rendering.SceneObjectFlags _flags = Rendering.SceneObjectFlags.CastShadows |
		Rendering.SceneObjectFlags.IsLoaded;

	private Model _model;
	private BBox? _bounds;
	private bool _renderingEnabled = true;
	private Color _tint = Color.White;
	private ulong _meshGroupMask = ulong.MaxValue;
	private int _meshIndex = -1;

	/// <summary>Draws only this mesh of the model, whatever the body groups say; minus one is all.</summary>
	public int MeshIndex
	{
		get => _meshIndex;
		set
		{
			if ( _meshIndex != value ) Touched();
			_meshIndex = value;
		}
	}
	private bool _batchable = true;
	private SceneObject _parent;
	private List<SceneObject> _children;

	internal SceneObject()
	{
		Tags = new Internal.SceneObjectTags( this );
		Flags = new SceneObjectFlagAccessor( this );
	}

	//Puts this object in a world without a native half to put there. Called by the constructor
	//of anything drawn by our renderer.
	internal void JoinWorld( SceneWorld world )
	{
		if ( world is null ) return;

		World = world;

		lock ( world.InternalSceneObjects )
		{
			world.InternalSceneObjects.Add( this );
			if ( this is SceneCustomObject or SceneDynamicObject ) world.InternalHandDrawn.Add( this );
			if ( this is DecalSceneObject decal ) world.InternalDecals.Add( decal );
			if ( this is SceneLight light ) world.InternalLights.Add( light );
			world.Touch();
			world.TouchMembership();
		}
	}

	internal SceneObject( HandleCreationData _ ) : this()
	{
	}

	public SceneObject( SceneWorld sceneWorld, Model model ) : this( sceneWorld, model, Transform.Zero )
	{
	}

	//Stores the model and transform and joins the world itself, since there's no native object anymore
	public SceneObject( SceneWorld sceneWorld, Model model, Transform transform ) : this()
	{
		Assert.IsValid( sceneWorld );

		if ( !model.HasRenderMeshes() ) model = Model.Error;

		_model = model;
		_transform = transform;

		JoinWorld( sceneWorld );
	}

	public SceneObject( SceneWorld sceneWorld, string modelName, Transform transform ) : this( sceneWorld, Model.Load( modelName ), transform )
	{
	}

	public SceneObject( SceneWorld sceneWorld, string modelName ) : this( sceneWorld, modelName, Transform.Zero )
	{
	}

	//Leaves the world and becomes invalid
	/// <summary>Delete this scene object. You shouldn't access it anymore.</summary>
	public void Delete()
	{
		var world = World;
		if ( world is null ) return;

		OnNativeDestroy();

		//The base of that removes it as well. Doing it here too means a subclass that overrides the hook and
		//does not chain up cannot leave itself in the world, which is the whole bug this exists to close.
		lock ( world.InternalSceneObjects )
		{
			world.InternalSceneObjects.Remove( this );
			if ( this is SceneCustomObject or SceneDynamicObject ) world.InternalHandDrawn.Remove( this );
			if ( this is DecalSceneObject decal ) world.InternalDecals.Remove( decal );
			if ( this is SceneLight light ) world.InternalLights.Remove( light );
			world.Touch();
			world.TouchMembership();
		}

		//What IsValid() reads. Deleted twice is deleted once.
		World = null;
	}

	internal virtual void OnNativeDestroy()
	{
		if ( World is not null )
		{
			lock ( World.InternalSceneObjects )
			{
				World.InternalSceneObjects.Remove( this );
				if ( this is SceneCustomObject or SceneDynamicObject ) World.InternalHandDrawn.Remove( this );
				if ( this is DecalSceneObject gone ) World.InternalDecals.Remove( gone );
				if ( this is SceneLight light ) World.InternalLights.Remove( light );
				World.Touch();
				World.TouchMembership();
			}
		}
	}

	//Zero rather than default: a default Transform has a scale of nothing and a rotation that is
	//not a rotation, and an object that was never placed would be drawn collapsed into the origin.
	Transform _transform = Transform.Zero;

	//Incremented whenever Transform changes: consumers (the shadow cache) detect movement without keeping a transform
	internal int TransformVersion = 1;

	/// <summary>Transform relative to its <see cref="Parent"/>, or <see cref="SceneWorld"/> without one.</summary>
	public Transform Transform
	{
		get => _transform;
		set
		{
			if ( _transform == value ) return;
			TouchedPlacement();

			_transform = value;
			TransformVersion++;
		}
	}

	/// <summary>Rotation relative to its <see cref="Parent"/>, or <see cref="SceneWorld"/> without one.</summary>
	public Rotation Rotation
	{
		get => Transform.Rotation;
		set
		{
			if ( Rotation == value )
				return;

			//Transform setter bumps the version only on real change; don't bump here too
			Transform = Transform.WithRotation( value );
		}
	}

	/// <summary>Position relative to its <see cref="Parent"/>, or <see cref="SceneWorld"/> without one.</summary>
	[Property( "origin" )]
	public Vector3 Position
	{
		get => Transform.Position;
		set
		{
			//See Rotation above: the transform setter is what counts the version, and only when the value really moved.
			if ( Position == value )
				return;

			Transform = Transform.WithPosition( value );
		}
	}

	//We could do better here, sometimes sceneobjects are set to infinite bounds by design
	//The model's box put through the transform, and what it was made from: asked of every object by each trace for
	//picking, every frame the pointer or the camera moves, where turning eight corners each time was most of the cost
	private BBox modelBoundsWorld, modelBoundsLocal;
	private Transform modelBoundsAt;
	private bool modelBoundsKept;

	private BBox ModelBoundsInWorld()
	{
		var local = _model.Bounds;

		if ( modelBoundsKept && local == modelBoundsLocal && _transform == modelBoundsAt ) return modelBoundsWorld;

		modelBoundsLocal = local;
		modelBoundsAt = _transform;
		modelBoundsWorld = local.Transform( _transform );
		modelBoundsKept = true;

		return modelBoundsWorld;
	}

	private BBox GetSafeBounds()
	{
		//What was set, or what the model fills where nothing was.
		var bounds = _bounds ?? ( _model.IsValid()
			? ModelBoundsInWorld()
			: BBox.FromPositionAndSize( Position ) );

		var volume = bounds.Volume;

		if ( !float.IsNaN( volume ) && !float.IsInfinity( volume ) )
			return bounds;

		return BBox.FromPositionAndSize( Position );
	}

	/// <summary>Set or get the axis aligned bounding box for this object.</summary>
	public BBox Bounds
	{
		get => GetSafeBounds();
		set => _bounds = value;
	}

	//Whether bounds were explicitly known, not a zero-size or infinite fallback; culling needs real ones
	internal bool BoundsWereGiven
	{
		get
		{
			if ( _bounds is not { } given ) return _model.IsValid();

			var volume = given.Volume;

			return !float.IsNaN( volume ) && !float.IsInfinity( volume ) && volume > 0.0f;
		}
	}

	/// <summary>Set the axis aligned bounding box by transforming by this objects transform.</summary>
	public BBox LocalBounds
	{
		[Obsolete( "LocalBounds.get is incorrect and should not be used. It does not reverse the full transform." )]
		get => Bounds.Translate( -Position );
		set => Bounds = value.Transform( Transform );
	}

	//Metres this object may stray from the surface it stands in for, where it is one detail of a surface drawn by several; nought
	//for anything else. Its coming or going re-bakes only the shadow finer than that - whoever places it stirs real changes itself
	internal float StandInError
	{
		get => _standInError;
		set
		{
			if ( _standInError == value ) return;
			_standInError = value;
			Touched();
		}
	}

	private float _standInError;

	/// <summary>Whether this scene object should render or not.</summary>
	public bool RenderingEnabled
	{
		get => _renderingEnabled;
		set
		{
			if ( _renderingEnabled != value ) Touched();
			_renderingEnabled = value;
		}
	}

	/// <summary>Color tint of this scene object.</summary>
	public Color ColorTint
	{
		get => _tint;
		set
		{
			if ( _tint.Equals( value ) ) return;

			_tint = value;
			TouchedPlacement();
		}
	}

	/// <summary>Clipping plane for this scene object. Requires <see cref="ClipPlaneEnabled"/> to be <c>true</c>.</summary>
	[Obsolete]
	public Plane ClipPlane;

	/// <summary>Whether or not to use the clipping plane defined in <see cref="ClipPlane"/>.</summary>
	[Obsolete]
	public bool ClipPlaneEnabled;

	/// <summary>Movement parent of this scene object, if any.</summary>
	public SceneObject Parent => _parent;

	//What was hung off this one, or nothing.
	internal IReadOnlyList<SceneObject> Children => _children;

	//The name can be used to look up children by name, but it is not bound. (SceneObject_FindChild)
	/// <summary>Add a named child scene object to this one. The child scene object will have its parent set.</summary>
	public void AddChild( string name, SceneObject child )
	{
		if ( !child.IsValid() )
			return;
	}

	/// <summary>Unlinks a child scene object: its parent becomes null, and it is not deleted.</summary>
	public void RemoveChild( SceneObject child )
	{
		if ( !child.IsValid() )
			return;
	}

	/// <summary>The model this scene object will render.</summary>
	public Model Model
	{
		get => _model;

		set
		{
			if ( _model == value ) return;
			Touched();

			//And the membership: whether an object is walked at all turns on its having a model - see SceneWorld.Membership
			World?.TouchMembership();

			_model = value;

			OnModelChanged();
		}
	}

	internal virtual void OnModelChanged()
	{
		_materialOverride = null;
	}

	/// <summary>Body group mask; meshes with no group always draw, and MaxValue means use the model's default</summary>
	public ulong MeshGroupMask
	{
		get => _meshGroupMask;
		set
		{
			if ( _meshGroupMask != value ) Touched();
			_meshGroupMask = value;
		}
	}


	Material _materialOverride;

	//Its place in the baked atlas (xy scale, zw offset) where no ModelRenderer holds one - see MeshComponent.LightmapRect
	internal System.Numerics.Vector4 LightmapRect
	{
		get;
		set
		{
			if ( field == value ) return;

			field = value;
			Touched();
		}
	}

	/// <summary>Override all materials on this object's <see cref="Model"/>.</summary>
	public void SetMaterialOverride( Material material )
	{
		if ( _materialOverride == material )
			return;

		_materialOverride = material;
		Touched();
		//No-op: nothing to do here
	}

	/// <summary>What everything on this object is drawn with instead, if anything.</summary>
	public Material MaterialOverride => _materialOverride;

	/// <summary>Clear all material replacements.</summary>
	public void ClearMaterialOverride()
	{
		if ( _materialOverride is not null ) Touched();
		_materialOverride = null;
		//No-op: nothing to do here
	}

	/// <summary>Replaces the model's materials whose user attribute is set to "1", in either material group.</summary>
	public void SetMaterialOverride( Material material, string attributeName, int attributeValue = 1 )
	{
		//No-op: nothing to do here
	}

	/// <summary>Which of the model's material groups (as set up in ModelDoc) is on it.</summary>
	public string MaterialGroup { get; private set; }

	public void SetMaterialGroup( string name )
	{
		MaterialGroup = name;
		//No-op: nothing to do here
	}

	internal virtual void OnTransformChanged( in Transform tx )
	{

	}

	/// <summary>Whether this may be batched with compatible objects; differing attribute values split batches.</summary>
	public bool Batchable
	{
		get => _batchable;
		set
		{
			//No-op: nothing to do here
		}
	}

	Component _component;

	//For storing and retrieving the GameObject this SceneObject belongs to
	internal GameObject GameObject { get; set; }

	//The component that created this object
	internal Component Component
	{
		get => _component;
		set
		{
			_component = value;
			GameObject = _component?.GameObject;
		}
	}

	[Obsolete( "Use Component property" )]
	public void SetComponentSource( Component c )
	{
		Component = c;
	}

	[Obsolete( "Use GameObject property" )]
	public GameObject GetGameObject() => GameObject;

	//Updates flags like transparent/opaque based on object's material, this is usually called
	//automatically. But some procedural workflows (mesh editor) may want to call this manually.
	internal void UpdateFlagsBasedOnMaterial()
	{
		//No-op: nothing to do here
	}

	/// <summary>Access to various advanced scene object flags.</summary>
	public SceneObjectFlagAccessor Flags { get; internal set; }

	public class SceneObjectFlagAccessor
	{
		SceneObject Object;

		internal SceneObjectFlagAccessor( SceneObject obj )
		{
			Object = obj;
		}

		internal bool HasFlag( Rendering.SceneObjectFlags f )
		{
			return ( Object._flags & f ) != 0;
		}

		internal void SetFlag( Rendering.SceneObjectFlags f, bool val )
		{
			var was = Object._flags;
			Object._flags = val ? Object._flags | f : Object._flags & ~f;
			if ( Object._flags != was ) Object.Touched();
			//No-op: nothing to do here
		}


		/// <summary>Whether this scene object should cast shadows.</summary>
		public bool CastShadows
		{
			get => HasFlag( Rendering.SceneObjectFlags.CastShadows );
			set => SetFlag( Rendering.SceneObjectFlags.CastShadows, value );
		}

		/// <summary>Whether this is here for a moment - see <see cref="Rendering.SceneObjectFlags.NeverStill"/>.</summary>
		public bool NeverStill
		{
			get => HasFlag( Rendering.SceneObjectFlags.NeverStill );
			set => SetFlag( Rendering.SceneObjectFlags.NeverStill, value );
		}

		public bool IsOpaque
		{
			get => HasFlag( Rendering.SceneObjectFlags.IsOpaque );
			set => SetFlag( Rendering.SceneObjectFlags.IsOpaque, value );
		}

		public bool IsTranslucent
		{
			get => HasFlag( Rendering.SceneObjectFlags.IsTranslucent );
			set => SetFlag( Rendering.SceneObjectFlags.IsTranslucent, value );
		}

		/// <summary>Marks an editable polygon mesh (set by MeshComponent) so the editor can draw its grid over it</summary>
		public bool IsEditableMesh
		{
			get => HasFlag( Rendering.SceneObjectFlags.IsEditableMesh );
			set => SetFlag( Rendering.SceneObjectFlags.IsEditableMesh, value );
		}

		[Obsolete( "SceneObject.IsDecal is obsolete" )]
		public bool IsDecal
		{
			get => HasFlag( Rendering.SceneObjectFlags.IsDecal );
			set => SetFlag( Rendering.SceneObjectFlags.IsDecal, value );
		}

		public bool OverlayLayer
		{
			get => HasFlag( Rendering.SceneObjectFlags.GameOverlayLayer );
			set => SetFlag( Rendering.SceneObjectFlags.GameOverlayLayer, value );
		}

		/// <summary>Not drawn in the opaque and translucent passes - for drawing only in the bloom layer.</summary>
		public bool ExcludeGameLayer
		{
			get => HasFlag( Rendering.SceneObjectFlags.ExcludeGameLayer );
			set => SetFlag( Rendering.SceneObjectFlags.ExcludeGameLayer, value );
		}

		public bool ViewModelLayer
		{
			get => HasFlag( Rendering.SceneObjectFlags.ViewModelLayer );
			set => SetFlag( Rendering.SceneObjectFlags.ViewModelLayer, value );
		}

		public bool SkyBoxLayer
		{
			get => HasFlag( Rendering.SceneObjectFlags.Skybox3DLayer );
			set => SetFlag( Rendering.SceneObjectFlags.Skybox3DLayer, value );
		}

		public bool NeedsLightProbe
		{
			get => HasFlag( Rendering.SceneObjectFlags.NeedsLightProbe );
			set => SetFlag( Rendering.SceneObjectFlags.NeedsLightProbe, value );
		}

		/// <summary>True if this object needs cubemap information</summary>
		public bool NeedsEnvironmentMap
		{
			get => HasFlag( Rendering.SceneObjectFlags.EnvironmentMapped );
			set => SetFlag( Rendering.SceneObjectFlags.EnvironmentMapped, value );
		}

		/// <summary>Sets the material's "FrameBufferCopyTexture" itself, as Render.CopyFrameBuffer() does.</summary>
		public bool WantsFrameBufferCopy
		{
			get => HasFlag( Rendering.SceneObjectFlags.WantsFrameBufferCopyTexture );
			set => SetFlag( Rendering.SceneObjectFlags.WantsFrameBufferCopyTexture, value );
		}

		/// <summary>Draw this in cubemaps</summary>
		public bool IncludeInCubemap
		{
			get => !HasFlag( Rendering.SceneObjectFlags.HideInCubemaps );
			set => SetFlag( Rendering.SceneObjectFlags.HideInCubemaps, !value );
		}

		internal bool WantsExecuteBefore
		{
			get => HasFlag( Rendering.SceneObjectFlags.ExecuteBefore );
			set => SetFlag( Rendering.SceneObjectFlags.ExecuteBefore, value );
		}

		internal bool WantsExecuteAfter
		{
			get => HasFlag( Rendering.SceneObjectFlags.ExecuteAfter );
			set => SetFlag( Rendering.SceneObjectFlags.ExecuteAfter, value );
		}

		public bool WantsPrePass
		{
			get => HasFlag( Rendering.SceneObjectFlags.NoZPrepass ) == false;
			set => SetFlag( Rendering.SceneObjectFlags.NoZPrepass, !value );
		}

		/// <summary>Whether this is a permanent part of the world that never moves, which cheaper paths rely on.</summary>
		public bool IsStatic
		{
			get => HasFlag( Rendering.SceneObjectFlags.StaticObject );
			set => SetFlag( Rendering.SceneObjectFlags.StaticObject, value );
		}
	}

	public sealed override int GetHashCode() => base.GetHashCode();
	public sealed override bool Equals( object obj ) => base.Equals( obj );
}
