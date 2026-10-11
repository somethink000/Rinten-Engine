using Facepunch.ActionGraphs;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rinten;

public partial class Scene : GameObject
{
	/// <summary>Load from the provided <see cref="SceneFile"/>. This will not load the scene for other clients in a multiplayer session, you should instead use <see cref="Game.ChangeScene"/> if you want to bring other clients.</summary>
	public virtual bool Load( GameResource resource )
	{
		if ( resource is SceneFile sf )
		{
			var options = new SceneLoadOptions();

			if ( !options.SetScene( sf ) )
				return false;

			return Load( options );
		}

		return false;
	}

	//Time in ms the last load spent building the object tree from the file's root objects
	internal static double RootsReadMs;

	public bool Load( SceneLoadOptions options )
	{
		var sceneFile = options.GetSceneFile();

		if ( !sceneFile.IsValid() )
		{
			Log.Error( "No valid Scene was found in SceneLoadOptions." );
			return false;
		}

		//On a page, what only this scene uses may still be on its way: the load waits for it and runs again when it is here - see WebContent.ForScene.
		if ( OperatingSystem.IsBrowser() && WebContent.ForScene( sceneFile.ResourcePath, () => Load( options ) ) )
			return true;

		if ( sceneFile.ResourceName != null )
		{
			Name = sceneFile.ResourceName;
		}

		ProcessDeletes();

		if ( !options.IsAdditive )
		{
			if ( options.DeleteEverything )
			{
				Clear( true );
			}
			else
			{
				//get all the gameobjects that should survive
				var savedObjects = GetAllObjects( false ).Where( x => x.Flags.Contains( GameObjectFlags.DontDestroyOnLoad ) );

				//move only DontDestroyOnLoad roots that are not already under another DontDestroyOnLoad ancestor to the scene root
				foreach ( var saved in savedObjects )
				{
					bool hasSurvivingAncestor = false;
					for ( var parent = saved.Parent; parent != this; parent = parent.Parent )
					{
						if ( parent?.Flags.Contains( GameObjectFlags.DontDestroyOnLoad ) == true )
						{
							hasSurvivingAncestor = true;
							break;
						}
					}

					if ( hasSurvivingAncestor )
						continue;

					saved.SetParent( this );
				}

				Clear( false );
			}

			ProcessDeletes();
			NavMesh.Reset();
			ResetLighting();
		}

		if ( !IsEditor && options.ShowLoadingScreen )
		{
			StartLoading();
			LoadingScreen.IsVisible = true;
			LoadingScreen.Title = "Loading Scene";
		}

		RunEvent<ISceneLoadingEvents>( x => x.BeforeLoad( this, options ) );

		if ( sceneFile.Id != Guid.Empty && sceneFile.Id != Id )
		{
			var previous = Id;

			ForceChangeId( sceneFile.Id );
			Directory.AddScene( this, previous );
		}

		if ( !options.IsAdditive )
		{
			Source = sceneFile;
		}

		{
			using var optionsScope = ActionGraph.PushSerializationOptions( sceneFile.SerializationOptions with { ForceUpdateCached = IsEditor } );
			using var sceneScope = Push();

			//Depending on if we load a scene from file or from memory, we need to account for that here
			using var blobs = BlobDataSerializer.Load( sceneFile.BinaryData, sceneFile.ResourcePath );
			using var batchGroup = CallbackBatch.Batch();

			//Clear cached binary data now that we've loaded it
			sceneFile.BinaryData = null;

			if ( sceneFile.GameObjects is not null )
			{
				var startedRoots = System.Diagnostics.Stopwatch.GetTimestamp();

				foreach ( var json in sceneFile.GameObjects )
				{
					var go = CreateObject( false );
					go.Deserialize( json );
				}

				RootsReadMs = ( System.Diagnostics.Stopwatch.GetTimestamp() - startedRoots ) * 1000.0
					/ System.Diagnostics.Stopwatch.Frequency;
			}

			if ( sceneFile.SceneProperties is not null )
			{
				//The physics mode is the scene's own: a scene added on top of it does not get to change what the loaded one simulates.
				DeserializeProperties( sceneFile.SceneProperties, options.IsSystemScene, keepPhysicsMode: options.IsAdditive );
			}

			//The scene was emptied above, so if the one loaded simulates differently the old world can go. Still inside the callback
			//batch: the components that make bodies are only started when it ends, so they get a world of the right kind. See Deserialize.
			if ( !options.IsAdditive && !options.IsSystemScene )
			{
				DropMismatchedPhysicsWorld();
			}

			//Let ISceneLoadingEvents add their own tasks
			List<LoadingContext> sceneLoadingTasks = new();
			RunEvent<ISceneLoadingEvents>( x =>
			{
				var context = new LoadingContext();
				context.Task = x.OnLoad( this, options, context );

				sceneLoadingTasks.Add( context );
			} );

			foreach ( var task in sceneLoadingTasks )
			{
				AddLoadingTask( task );
			}

			if ( !IsEditor )
			{
				NetworkSpawnRecursive( null );
			}
		}

		//Now that we're done, add the system scene
		if ( !IsEditor && !options.IsAdditive )
		{
			AddSystemScene();
		}

		if ( !options.IsSystemScene )
		{
			//Now we can signal to GameObjectSystems that we have finished loading. We wrap this
			//in an IsSystemScene check so that it's not called twice for every scene load.
			Signal( GameObjectSystem.Stage.SceneLoaded );
		}

		return true;
	}

	/// <summary>Load from the provided file name. This will not load the scene for other clients in a multiplayer session, you should instead use <see cref="Game.ChangeScene"/> if you want to bring other clients.</summary>
	public bool LoadFromFile( string filename )
	{
		var options = new SceneLoadOptions();

		if ( !options.SetScene( filename ) )
			return false;

		return Load( options );
	}

	public override JsonObject Serialize( SerializeOptions options = null )
	{
		if ( this is PrefabScene )
		{
			return base.Serialize( options );
		}

		var json = new JsonObject
		{
			{ "Type", "Scene" },
			{ "Properties", SerializeProperties() },
		};

		var children = new JsonArray();

		using var sceneScope = Push();

		foreach ( var child in Children )
		{
			var jso = child.Serialize( options );
			if ( jso is null ) continue;

			children.Add( jso );
		}

		json.Add( "GameObjects", children );

		return json;
	}

	public override void Deserialize( JsonObject node, DeserializeOptions option )
	{
		if ( this is PrefabScene )
		{
			base.Deserialize( node, option );
			return;
		}

		ProcessDeletes();
		Clear();

		if ( node.TryGetPropertyValue( "Properties", out var props ) )
		{
			DeserializeProperties( props.AsObject() );
		}

		//The scene is empty now, so if what was loaded simulates differently the old world can go and the first thing to need physics makes the new one.
		DropMismatchedPhysicsWorld();

		using var sceneScope = Push();
		using var batchGroup = CallbackBatch.Batch();

		if ( node["GameObjects"] is JsonArray childArray )
		{
			foreach ( var child in childArray )
			{
				if ( child is not JsonObject jso )
					return;

				var go = new GameObject( false );

				go.Parent = this;
				go.Deserialize( jso, option );
			}
		}
	}

	//Rebuild the scene from itself, for when a setting that decides how its world is built has changed - see ReloadHash.
	internal void Reload()
	{
		var json = Serialize();
		Deserialize( json );
	}

	public JsonObject SerializeProperties()
	{
		var jso = new JsonObject();

		foreach ( var prop in Game.TypeLibrary.GetType<Scene>()
			.Properties
			.Where( x => x.HasAttribute<PropertyAttribute>() )
			.OrderBy( x => x.Name ) )
		{
			if ( prop.Name == "Enabled" ) continue;
			if ( prop.Name == "Name" ) continue;
			if ( prop.Name == "Lerp" ) continue;

			jso.Add( prop.Name, JsonValue.Create( prop.GetValue( this ) ) );
		}

		jso.Add( "Metadata", SerializeMetadata() );
		jso.Add( "NavMesh", NavMesh.Serialize() );

		if ( this is not PrefabScene )
		{
			jso.Add( "Lighting", Lighting.Serialize() );

			var serializedSystems = SerializeGameObjectSystems();
			if ( serializedSystems is not null )
			{
				jso.Add( "GameObjectSystems", serializedSystems );
			}
		}

		return jso;
	}

	JsonNode SerializeGameObjectSystems()
	{
		//Sorted by type name so the serialized order is stable across saves.
		var systemsToSerialize = new SortedDictionary<string, SortedDictionary<string, object>>( StringComparer.Ordinal );

		foreach ( var system in GetSystems() )
		{
			var systemType = Game.TypeLibrary.GetType( system.GetType() );
			if ( systemType is null ) continue;

			var systemTypeName = systemType.FullName;
			SortedDictionary<string, object> propertiesToSerialize = null;

			foreach ( var property in systemType.Properties.Where( x => x.HasAttribute<PropertyAttribute>() ) )
			{
				if ( !property.CanWrite ) continue;

				var currentValue = TryGetPreTransientValue( system, property, out var preTransientValue )
					? preTransientValue
					: property.GetValue( system );
				var hasGlobalValue = ProjectSettings.Systems.TryGetPropertyValue( systemType, property, out var globalValue );
				var compareValue = hasGlobalValue ? globalValue : SystemsConfig.GetDefaultValue( property );

				var currentJson = JsonSerializer.SerializeToNode( currentValue, Json.options );
				var compareJson = JsonSerializer.SerializeToNode( compareValue, Json.options );

				//Is this slow?
				if ( !JsonNode.DeepEquals( currentJson, compareJson ) )
				{
					propertiesToSerialize ??= new SortedDictionary<string, object>( StringComparer.Ordinal );
					propertiesToSerialize[property.Name] = currentValue;
				}
			}

			if ( propertiesToSerialize is not null )
			{
				systemsToSerialize[systemTypeName] = propertiesToSerialize;
			}
		}

		return systemsToSerialize.Any() ? JsonSerializer.SerializeToNode( systemsToSerialize, Json.options ) : null;
	}

	JsonObject SerializeMetadata()
	{
		var metadata = new JsonObject();
		foreach ( var c in GetAllComponents<ISceneMetadata>() )
		{
			var data = c.GetMetadata();
			if ( data is null ) continue;

			foreach ( var entry in data )
			{
				metadata[entry.Key] = entry.Value;
			}
		}
		return metadata;
	}

	void DeserializeProperties( JsonObject data, bool isSystemScene = false, bool keepPhysicsMode = false )
	{
		var sceneType = Game.TypeLibrary.GetType<Scene>();
		Assert.NotNull( sceneType, "Scene type is inaccessible!" );

		//The physics mode belongs to the scene being loaded, never to a system scene added on top of it, and a scene saved before there was a choice
		//is a 3D one. Written past the setter, which refuses a change while a world exists - Deserialize replaces that world once the scene is empty.
		if ( !isSystemScene && !keepPhysicsMode )
		{
			var mode = ScenePhysicsMode.Physics3D;

			if ( data.TryGetPropertyValue( nameof( PhysicsMode ), out var modeNode ) && modeNode is not null )
			{
				try { mode = Json.FromNode<ScenePhysicsMode>( modeNode ); }
				catch ( System.Exception e ) { Log.Warning( e, $"Error when deserializing {this}.{nameof( PhysicsMode )} ({e.Message})" ); }
			}

			_physicsMode = mode;
		}

		foreach ( var prop in sceneType.Properties.Where( x => x.HasAttribute<PropertyAttribute>() ) )
		{
			if ( prop.Name == "Enabled" ) continue;
			if ( prop.Name == "Name" ) continue;
			if ( prop.Name == "Lerp" ) continue;
			if ( prop.Name == nameof( PhysicsMode ) ) continue;

			if ( !data.TryGetPropertyValue( prop.Name, out JsonNode node ) )
				continue;

			try
			{
				prop.SetValue( this, Json.FromNode( node, prop.PropertyType ) );
			}
			catch ( System.Exception e )
			{
				Log.Warning( e, $"Error when deserializing {this}.{prop.Name} ({e.Message})" );
			}
		}

		//We don't want system scene loads to overwrite the main scene's system properties. System scenes can add GameObjects,
		//but they should not reconfigure systems that already belong to the main scene (same reason NavMesh is guarded below).
		if ( !isSystemScene && data.TryGetPropertyValue( "GameObjectSystems", out var systemOverridesNode ) )
		{
			ApplyGameObjectSystemOverrides( systemOverridesNode );
		}

		//We don't want navmesh to be overwritten by system scene loads
		if ( !isSystemScene )
		{
			NavMesh.Deserialize( data["NavMesh"] as JsonObject );
		}

		//Nor the bake, which a scene added on top does not get to change either
		if ( !isSystemScene && !keepPhysicsMode && this is not PrefabScene && data["Lighting"] is JsonObject lighting )
		{
			Lighting.Deserialize( lighting );
		}
	}

	//Create a new SceneFile from this scene
	internal SceneFile CreateSceneFile()
	{
		var a = new SceneFile();
		ToSceneFile( a );
		return a;
	}

	//Save the contents of this scene to the SceneFile
	internal void ToSceneFile( SceneFile target )
	{
		Assert.IsValid( this );

		target.ActionGraphCache.Clear();

		using var sceneScope = Push();
		using var optionsScope = target.PushSerializationScope();
		using var blobs = BlobDataSerializer.Capture();

		target.Id = Id;
		target.GameObjects = Children.Select( x => x.Serialize() ).Where( x => x is not null ).ToArray();
		target.SceneProperties = SerializeProperties();
		target.BinaryData = blobs.ToByteArray();
	}
}
