using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace Rinten;

[Expose]
public partial class Scene
{
	Dictionary<Type, GameObjectSystem> systems = new();

	//Systems that add their own trace hits, kept separately so traces don't scan every system
	readonly List<GameObjectSystem.ITraceProvider> traceProviders = new();

	//Call dispose on all installed hooks
	void ShutdownSystems()
	{
		foreach ( var sys in systems.Values )
		{
			//Can become null during hotload development
			if ( sys is null ) continue;

			try
			{
				RemoveObjectFromDirectory( sys );
				sys.Dispose();
			}
			catch ( System.Exception e )
			{
				Log.Warning( e, $"Exception when disposing GameObjectSystem '{sys.GetType()}'" );
			}
		}

		systems.Clear();
	}

	//Find all types of SceneHook, create an instance of each one and install it.
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( PostProcessSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( AudioDebugWorldSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( MovieMaker.MovieRecorderSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( MovieMaker.Properties.MovieBoneAnimatorSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( SubtitlesGameObjectSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( SceneSpriteSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( SceneLight2DSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( SoundSimulationSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( ScenePhysicsSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( SceneSoundscapeSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( VerletRopeGameSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( TerrainTraceSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( HitboxSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( NetworkDebugSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( DecalGameSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( NavMeshGameSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( SceneAnimationSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( InterpolationSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( Clutter.ClutterGridSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( CollisionSoundSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( CameraEffectSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( Volumes.VolumeSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( ParticleGameSystem ) )]
	[DynamicDependency( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof( DspVolumeGameSystem ) )]
	void InitSystems()
	{
		using ( Push() )
		{
			ShutdownSystems();

			var found = Game.TypeLibrary.GetTypes<GameObjectSystem>()
				.Where( x => !x.IsAbstract )
				.ToArray();

			foreach ( var f in found )
			{
				var e = f.Create<GameObjectSystem>( [this] );
				if ( e is null ) continue;

				ApplyGameObjectSystemConfig( e );

				systems[e.GetType()] = e;
				if ( e is GameObjectSystem.ITraceProvider provider ) traceProviders.Add( provider );
				AddObjectToDirectory( e );
			}
		}
	}

	//Apply configuration values to a GameObjectSystem with priority: 1. Project-wide value (from ProjectSettings.Systems) 2. Default value
	//(already set by property initializer) Scene-specific overrides are applied during deserialization via ApplyGameObjectSystemOverrides
	void ApplyGameObjectSystemConfig( GameObjectSystem system )
	{
		var systemType = Game.TypeLibrary.GetType( system.GetType() );
		if ( systemType is null ) return;

		using ( Push() )
		{
			foreach ( var property in systemType.Properties.Where( x => x.HasAttribute<PropertyAttribute>() ) )
			{
				if ( !property.CanWrite ) continue;

				//Apply project-wide value if it exists
				if ( ProjectSettings.Systems.TryGetPropertyValue( systemType, property, out var value ) )
				{
					try
					{
						property.SetValue( system, value );
					}
					catch ( Exception ex )
					{
						Log.Warning( $"Failed to apply config value to {systemType.FullName}.{property.Name}: {ex.Message}" );
					}
				}
			}
		}
	}

	//Signal a hook stage
	internal void Signal( in GameObjectSystem.Stage stage )
	{
		GetCallbacks( stage ).Run();
	}

	Dictionary<GameObjectSystem.Stage, TimedCallbackList> listeners = new Dictionary<GameObjectSystem.Stage, TimedCallbackList>();

	//Get the hook container for this stage
	TimedCallbackList GetCallbacks( in GameObjectSystem.Stage stage )
	{
		if ( listeners.TryGetValue( stage, out var list ) )
			return list;

		list = new TimedCallbackList();
		listeners[stage] = list;
		return list;
	}

	//Reset the listener metrics to 0, like before a benchmark or something
	internal void ResetListenerMetrics()
	{
		foreach ( var l in listeners.Values )
		{
			l.ClearMetrics();
		}
	}

	//Get a JSON serializable list of metrics from the scene's listeners. (this is
	//just internal object[] right now because I can't be fucked to exose it properly)
	internal object[] GetListenerMetrics()
	{
		return listeners.Values.SelectMany( x => x.GetMetrics() ).ToArray();
	}

	/// <summary>Call this method on this stage. This returns a disposable that will remove the hook when disposed.</summary>
	public IDisposable AddHook( GameObjectSystem.Stage stage, int order, Action action, string className, string description )
	{
		return GetCallbacks( stage ).Add( order, action, className, description );
	}

	/// <summary>Get a specific system by type.</summary>
	public T GetSystem<T>() where T : GameObjectSystem
	{
		return systems.TryGetValue( typeof( T ), out var sys ) ? sys as T : null;
	}

	/// <summary>Get a specific system by type.</summary>
	public void GetSystem<T>( out T val ) where T : GameObjectSystem
	{
		val = systems.TryGetValue( typeof( T ), out var sys ) ? sys as T : null;
	}

	//Get a specific system by TypeDescription.
	internal GameObjectSystem GetSystemByType( TypeDescription type )
	{
		return systems.TryGetValue( type.TargetType, out var sys ) ? sys : null;
	}

	//Get all systems belonging to this scene.
	internal Dictionary<Type, GameObjectSystem>.ValueCollection GetSystems()
	{
		return systems.Values;
	}
}
