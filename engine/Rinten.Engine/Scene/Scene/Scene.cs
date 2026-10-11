using Rinten.Utility;
using Rinten.Volumes;
using System.Text.Json.Serialization;

namespace Rinten;

public partial class Scene : GameObject
{
	public bool IsEditor { get; private set; }

	bool _destroyed;
	SceneWorld _sceneWorld;

	//True if the scene world has been created. Reading SceneWorld creates it - check this first when you only want to act on a world that already exists.
	internal bool HasSceneWorld => _sceneWorld is not null;

	//True if the physics world has been created. Reading PhysicsWorld creates it
	//- check this first when you only want to act on a world that already exists.
	internal bool HasPhysicsWorld => _physicsWorld.IsValid();

	/// <summary>The scene world, holding this scene's renderables. Created on first access, so scenes that never render anything (like prefab caches or tests) never create one.</summary>
	public SceneWorld SceneWorld
	{
		get
		{
			if ( _sceneWorld is null && !_destroyed )
			{
				_sceneWorld = new SceneWorld { Scene = this };
			}

			return _sceneWorld;
		}
		private set => _sceneWorld = value;
	}

	public SceneWorld DebugSceneWorld => gizmoInstance?.World;

	[System.Obsolete( "Use Scene.Editor.HasUnsavedChanges" )]
	public bool HasUnsavedChanges { get; private set; }

	public GameResource Source { get; internal set; }

	//For scenes within a map's MapWorld, for action graph stack traces, and so the editor knows the map must be saved when editing graphs from it.
	internal Facepunch.ActionGraphs.ISourceLocation OverrideSourceLocation { get; set; }

	public GameObjectDirectory Directory { get; private set; }

	Gizmo.Instance gizmoInstance = NewGizmoInstance();

	//This scene's own gizmo instance: what game code draws into with Gizmo.Draw , kept for as long
	//as each thing was asked to be, and drawn in every view of the scene - see CoreSceneDrawer.
	internal Gizmo.Instance GizmoInstance => gizmoInstance;

	//What GizmoInstance is drawing now, or nothing.
	internal DebugDrawList GizmoDraws => gizmoInstance?.Draws;

	static Gizmo.Instance NewGizmoInstance()
	{
		var instance = new Gizmo.Instance();
		instance.KeepDraws();
		return instance;
	}

	[JsonIgnore]
	[System.Obsolete( "please use the SceneInformation component" )]
	public string Title { get; set; }

	[JsonIgnore]
	[System.Obsolete( "please use the SceneInformation component" )]
	public string Description { get; set; }

	/// <summary>If true we'll additive load the system scene when this scene is loaded. Defaults to true. You might want to disable this for specific scenes, like menu scenes etc.</summary>
	[Property]
	public bool WantsSystemScene { get; set; } = true;

	/// <summary>Global render attributes accessible on any renderable in this Scene.</summary>
	public RenderAttributes RenderAttributes { get; }

	private PhysicsWorld _physicsWorld;
	private ScenePhysicsMode _physicsMode;

	/// <summary>Which physics simulation this scene runs. The physics world is built for one mode, so this is chosen when the scene is authored: the editor rebuilds the scene when it changes, and a running game picks it up the next time the scene is loaded.</summary>
	[Property]
	public ScenePhysicsMode PhysicsMode
	{
		get => _physicsMode;
		set
		{
			if ( _physicsMode == value )
				return;

			//Tearing down a live world would take every body in the scene with it. The editor picks the
			//change up through ReloadHash and rebuilds; a load replaces the world itself - see Deserialize.
			if ( HasPhysicsWorld && !IsEditor )
			{
				Log.Warning( $"Can't change {nameof( PhysicsMode )} once the physics world exists - reload the scene instead" );
				return;
			}

			_physicsMode = value;
		}
	}

	/// <summary>True when this scene simulates in the plane. See <see cref="ScenePhysicsMode.Physics2D"/>.</summary>
	public bool Is2D => PhysicsMode == ScenePhysicsMode.Physics2D;

	//Hash of the scene settings that need the world rebuilt when they change.
	internal int ReloadHash => HashCode.Combine( PhysicsMode );

	public PhysicsWorld PhysicsWorld => _physicsWorld ??= CreatePhysicsWorld();

	//The physics world for this scene, of the kind PhysicsMode asks for.
	private PhysicsWorld CreatePhysicsWorld()
	{
		var world = new PhysicsWorld( PhysicsMode )
		{
			DebugSceneWorld = DebugSceneWorld,
			//Metres per second squared: a bit over two gravities, a game's gravity rather than a planet's, which is what makes a jump land when
			//it looks like it should. The same in the plane - 850 in/s^2 for both, so a 2D game tuned for that falls as it did.
			Gravity = Vector3.Down * 21.6f,
			SimulationMode = PhysicsSimulationMode.Continuous,
			CollisionRules = ProjectSettings.Collision,
			Scene = this
		};

		//the physics system steps the world and forwards its collision events
		GetSystem<ScenePhysicsSystem>()?.OnPhysicsWorldCreated( world );

		return world;
	}

	//Drop the physics world if it is not the kind PhysicsMode asks for, so the next thing
	//to want one gets the right kind. Only safe with nothing in the scene holding a body.
	internal void DropMismatchedPhysicsWorld()
	{
		if ( !HasPhysicsWorld || _physicsWorld.Mode == PhysicsMode )
			return;

		_physicsWorld.Delete();
		_physicsWorld = null;
	}

	protected Scene( bool isEditor ) : base( true, "Scene" )
	{
		_all.Add( this );

		Directory = new GameObjectDirectory( this );

		RenderAttributes = new();
		IsEditor = isEditor;

		if ( this is not PrefabCacheScene )
		{
			InitSystems();
		}
	}

	public Scene() : this( false ) { }

	~Scene()
	{
		using var finalizing = MainThread.InFinalizer();
		MainThread.Queue( DestroyInternal );
	}

	/// <summary>Returns true if this scene has not been destroyed</summary>
	public override bool IsValid => !_destroyed;

	/// <summary>Destroy this scene. After this you should never use it again.</summary>
	public override void Destroy()
	{
		DestroyInternal();
	}

	internal virtual void DestroyInternal()
	{
		_all.Remove( this );

		//Clearing the object index now means we can save time because we don't have to do it for each object. Note that we can't do this
		//in Clear because we don't want to remove any GameObjectSystem's from the index - because they're persistent between the clears.
		ClearObjectIndex();

		Clear();
		ProcessDeletes();
		ShutdownSystems();
		ResetLighting();

		GC.SuppressFinalize( this );

		//the lazy world properties stop creating once this is set, so tearing down a world that was never created stays a no-op
		_destroyed = true;

		_physicsWorld?.Delete();
		_physicsWorld = default;

		SceneWorld?.Delete();
		SceneWorld = default;

		gizmoInstance?.Dispose();
		gizmoInstance = default;

		NavMesh?.Dispose();
		NavMesh = default;

		DisposeListeners();
	}

	public static Scene CreateEditorScene()
	{
		return new Scene( true );
	}

	/// <summary>Create a GameObject on this scene. This doesn't require the scene to be the active scene.</summary>
	public GameObject CreateObject( bool enabled = true )
	{
		using ( Push() )
		{
			var go = new GameObject( enabled );
			go.Enabled = enabled;
			go.Parent = this;
			return go;
		}
	}


	//The struct itself, not IDisposable: returned through the interface it boxed on every call, which in the editor is thirty-odd allocations a frame for a scope whose whole point was to be free. using and using var take it as before.
	/// <summary>Push this scene as the active scene, for a scope</summary>
	public ScenePushScope Push()
	{
		return new ScenePushScope( this );
	}

	/// <summary>Collects anything inside into a batch group. A batchgroup is used with GameObject and Components to make sure that their OnEnable/OnDisable and other callbacks are called in a deterministic order, and that they can find each other during creation. <see cref="GameObject.NetworkSpawn()"/> calls will also be batched.</summary>
	public IDisposable BatchGroup()
	{
		var networkScope = SceneNetworkSystem.Instance?.NetworkSpawnBatch();
		var callbackScope = CallbackBatch.Isolated();

		return DisposeAction.Create( () =>
		{
			callbackScope?.Dispose();
			networkScope?.Dispose();
		} );
	}

	internal override void OnHotload()
	{
		base.OnHotload();

		SceneNetworkSystem.Instance?.OnHotload();

		foreach ( var obj in Directory.AllGameObjects )
		{
			if ( obj == this ) continue;

			obj.OnHotload();
		}

		foreach ( var obj in networkedObjects )
		{
			obj.OnHotload();
		}

		foreach ( var obj in systems.Values )
		{
			obj.OnHotload();
		}

		ResolveMissingComponentsEverywhere();

		HotloadObjectIndex();
	}

	//After a hotload, swap MissingComponent placeholders back to real components where types now exist
	private void ResolveMissingComponentsEverywhere()
	{
		foreach ( var obj in Directory.AllGameObjects.ToArray() )
		{
			if ( obj == this ) continue;

			obj?.ResolveMissingComponents();
		}
	}

	static Superluminal _renderTimer = new Superluminal( "Scene.Render", Color.Cyan );
	static Superluminal _cameraRenderTimer = new Superluminal( "Camera", Color.Cyan );

	internal void Render( Vector2? size )
	{
		using var _renderScope = _renderTimer.Start();

		PreCameraRender();

		//Get all cameras sorted by render priority
		var cameras = Cameras.OrderBy( x => x.Priority );
		foreach ( var cc in cameras )
		{
			if ( cc.Active == false ) continue;
			if ( cc.IsSceneEditorCamera ) continue;

			using var _cam = _cameraRenderTimer.Start( cc.GameObject?.Name );
			cc.AddToRenderList( size );
		}
	}

	internal void RenderEnvmaps()
	{
		//Can't render envmaps while already inside a render pass
		if ( Graphics.IsActive )
		{
			if ( !Application.IsRetail ) Log.Error( "Attempted to render envmaps while inside a render pass!" );
			return;
		}

		//We pre-render envmaps, we dont need to render them parallelly in a frame anymore, this can
		//cause transform buffers and descriptor sets to balloon in complex scenes and cause crashes.
		const int maxSimultaniousUpdates = 1;
		foreach ( var envmap in GetAllComponents<EnvmapProbe>().Where( x => x.Dirty ).Take( maxSimultaniousUpdates ) )
		{
			envmap.RenderCubemap();
		}
	}

	//Cache all enabled and disabled cameras in the scene. Registered/Deregistered in CameraComponent.OnAwake and OnDestroy.
	internal HashSet<CameraComponent> Cameras { get; } = new HashSet<CameraComponent>();

	//Should be called before rendering. This allows things like reflections to render.
	internal void PreCameraRender()
	{
		//We want to initialize all cameras (enabled & disabled) incase they're used to render manually we need to make sure the SceneCamera is created etc.
		var cameras = Cameras.OrderBy( x => x.Priority );
		foreach ( var cc in cameras )
		{
			cc.InitializeRendering();
		}

		RenderEnvmaps();

		if ( SceneWorld.IsValid() )
		{
			SceneWorld.AmbientLightColor = GatherAmbientLight();
		}
	}

	//Sum of AmbientLight and directional sky colours; alpha is how much replaces sky/probe light
	internal Color GatherAmbientLight()
	{
		var ambientLight = Color.Transparent;

		foreach ( var light in GetAllComponents<DirectionalLight>() )
		{
			if ( !light.IsValid() || !light.Enabled ) continue;
			if ( Camera.IsValid() && light.Tags.HasAny( Camera.RenderExcludeTags ) ) continue;

			ambientLight += light.SkyColor;
		}

		foreach ( var light in GetAllComponents<AmbientLight>() )
		{
			if ( !light.IsValid() || !light.Enabled ) continue;
			if ( Camera.IsValid() && light.Tags.HasAny( Camera.RenderExcludeTags ) ) continue;

			ambientLight += light.Color;
		}

		return ambientLight;
	}

	private VolumeSystem _volumeSystem;

	/// <summary>Allows quickly finding components that have a volume</summary>
	public VolumeSystem Volumes => _volumeSystem ??= VolumeSystem.Get( this );

	//Adds the "system" scene, which is defined in the project settings.
	internal void AddSystemScene()
	{
		if ( !WantsSystemScene )
			return;

		var systemScene = Application.GamePackage?.GetMeta( "SystemScene", "" ) ?? null;

		if ( string.IsNullOrWhiteSpace( systemScene ) )
			return;

		//additive load this scene
		var slo = new SceneLoadOptions();
		slo.IsSystemScene = true;
		slo.IsAdditive = true;
		slo.SetScene( systemScene );
		Load( slo );
	}

	/// <summary>Find objects with all tags</summary>
	public IEnumerable<GameObject> FindAllWithTags( IEnumerable<string> tags )
	{
		foreach ( var go in Directory.AllGameObjects )
		{
			if ( go.Tags.HasAll( tags ) )
			{
				yield return go;
			}
		}
	}

	/// <summary>Find objects with tag</summary>
	public IEnumerable<GameObject> FindAllWithTag( string tag )
	{
		return Directory.AllGameObjects.Where( x => x.Tags.Has( tag ) );
	}
}

/// <summary>Allocation-free scope returned by <see cref="Scene.Push"/>. Use with <c>using var</c> to keep it stack-allocated; storing as <c>IDisposable</c> will box it.</summary>
public struct ScenePushScope : IDisposable
{
	Scene _pushed;
	Scene _prev;
	double _prevNowDouble;
	float _prevDelta;
	float _prevNow;

	internal ScenePushScope( Scene scene )
	{
		ThreadSafe.AssertIsMainThread();
		_pushed = scene;
		_prev = Game.ActiveScene;
		_prevNowDouble = Time.NowDouble;
		_prevDelta = Time.Delta;
		_prevNow = Time.Now;
		Game.ActiveScene = scene;
		Time.Update( scene.TimeNow, scene.TimeDelta );
	}

	public void Dispose()
	{
		if ( _pushed is null ) return;
		ThreadSafe.AssertIsMainThread();
		if ( Game.ActiveScene == _pushed ) Game.ActiveScene = _prev;
		Time.NowDouble = _prevNowDouble;
		Time.Delta = _prevDelta;
		Time.Now = _prevNow;
		_pushed = null;
	}
}
