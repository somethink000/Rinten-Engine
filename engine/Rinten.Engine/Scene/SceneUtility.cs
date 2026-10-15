using Facepunch.ActionGraphs;
using System.Text.Json.Nodes;

namespace Rinten;

public static class SceneUtility
{
	/// <summary>Find all "__guid" guids, and replace them with new guids. This is used to make GameObject serializations unique, so when you duplicate stuff, it copies over uniquely and keeps associations.</summary>
	public static Dictionary<Guid, Guid> MakeIdGuidsUnique( JsonObject json, Guid? rootGuid = null )
	{
		var translate = CreateUniqueGuidLookup( json, rootGuid );

		//Find every guid and translate them, but only if they're in our guid dictionary.
		Rinten.Json.WalkJsonTree( json, ( k, v ) =>
		{
			if ( !v.TryGetValue<Guid>( out var guid ) ) return v;
			if ( !translate.TryGetValue( guid, out var updatedGuid ) ) return v;

			return updatedGuid;
		} );

		return translate;
	}

	internal static Dictionary<Guid, Guid> CreateUniqueGuidLookup( JsonObject json, Guid? rootGuid = null )
	{
		Dictionary<Guid, Guid> translate = new();

		//Spawning a prefab with a known id: replace its guid with ours and remap children references
		if ( rootGuid.HasValue )
		{
			if ( json.TryGetPropertyValue( "__guid", out var node ) && node is JsonValue jsv && jsv.TryGetValue<Guid>( out var guid ) )
			{
				translate[guid] = rootGuid.Value;
			}
		}

		//We want to skip ActionGraph Guids if they're cached, so we can re-use the cached versions.
		if ( ActionGraph.SerializationOptions.Cache is { } cache )
		{
			foreach ( var guid in cache.Guids )
			{
				translate[guid] = guid;
			}
		}

		//Find all guids with "__guid" as their name. Add them to translate with a new target value.
		Rinten.Json.WalkJsonTree( json, ( k, v ) =>
		{
			if ( k == GameObject.JsonKeys.Id )
			{
				if ( v.TryGetValue<Guid>( out var guid ) && !translate.ContainsKey( guid ) )
				{
					translate[guid] = Guid.NewGuid();
				}
			}

			return v;
		},
		( k, v ) =>
		{
			//GameObjects & Components that are part of prefab instances have their id stored in the
			//PrefabToInstanceId lookup in JSON. We need to remap those as well in addtion to __guid id's.
			if ( k == GameObject.JsonKeys.PrefabIdToInstanceId )
			{
				foreach ( var (prefabId, instanceID) in v )
				{
					if ( instanceID.AsValue().TryGetValue<Guid>( out var guid ) && !translate.ContainsKey( guid ) )
					{
						translate[guid] = Guid.NewGuid();
					}
				}
			}

			return v;
		} );

		return translate;
	}

	/// <summary>Find all "Id" guids, and replace them with new guids. This is used to make GameObject serializations unique, so when you duplicate stuff, it copies over uniquely and keeps associations.</summary>
	[Obsolete( "Use MakeIdGuidsUnique" )]
	public static void MakeGameObjectsUnique( JsonObject json, Guid? rootGuid = null )
	{
		MakeIdGuidsUnique( json, rootGuid );
	}

	/// <summary>Create a unique copy of the passed in GameObject</summary>
	[System.Obsolete( "Please use GameObject.Clone( ... )" )]
	public static GameObject Instantiate( GameObject template, Transform transform )
	{
		Assert.NotNull( Game.ActiveScene, "No Active Scene" );

		JsonObject json = null;

		if ( template is PrefabScene prefabScene && prefabScene.Source is PrefabFile prefabFile )
		{
			json = prefabFile.RootObject;
		}
		else
		{
			json = template.Serialize();
		}

		if ( json is not null )
		{
			MakeIdGuidsUnique( json );
		}

		var go = new GameObject();

		if ( json is not null )
		{
			using var blobs = BlobDataSerializer.Load( null, null );
			using var batchGroup = CallbackBatch.Batch();

			go.Deserialize( json );
		}

		go.LocalTransform = transform.WithScale( transform.Scale * go.LocalScale );

		if ( template is PrefabScene prefabScene1 )
		{
			go.InitPrefabInstance( prefabScene1.Source.ResourcePath, false );
		}

		go.Parent = Game.ActiveScene;

		return go;
	}

	/// <summary>Create a unique copy of the passed in GameObject</summary>
	[System.Obsolete( "Please use GameObject.Clone( ... )" )]
	public static GameObject Instantiate( GameObject template ) => Instantiate( template, Transform.Zero );

	/// <summary>Create a unique copy of the passed in GameObject</summary>
	[System.Obsolete( "Please use GameObject.Clone( ... )" )]
	public static GameObject Instantiate( GameObject template, Vector3 position, Rotation rotation )
		=> Instantiate( template, new Transform( position, rotation, 1.0f ) );

	/// <summary>Create a unique copy of the passed in GameObject</summary>
	[System.Obsolete( "Please use GameObject.Clone( ... )" )]
	public static GameObject Instantiate( GameObject template, Vector3 position )
		=> Instantiate( template, new Transform( position, Rotation.Identity, 1.0f ) );

	/// <summary>Get a (cached) scene from a PrefabFile</summary>
	public static PrefabScene GetPrefabScene( PrefabFile prefabFile ) => prefabFile.GetScene();

	//Render a GameObject to a bitmap. This is usually used for easily rendering "previews" of GameObjects, for things like saving thumbnails etc.
	static void RenderToBitmap( Bitmap bitmap, Func<GameObject> func )
	{
		var scene = new Scene();
		scene.Name = "RenderGameObjectToBitmap";
		scene.PhysicsWorld?.Gravity = 0;

		try
		{
			using var sceneScope = scene.Push();

			CameraComponent camera = default;

			//camera
			{
				var go = new GameObject( true, "camera" );
				camera = go.AddComponent<CameraComponent>();
				camera.BackgroundColor = Color.Transparent;
				camera.WorldRotation = new Angles( 20, 180 + 45, 0 );
				camera.FieldOfView = 50.0f;
				camera.ZFar = 400.0f;
				camera.ZNear = 0.1f;

				//The effects a thumbnail is drawn with, by name: each is a component like a project's own, which the engine does not name.
				static void Effect( GameObject on, string name, string property = null, object value = null )
				{
					if ( Game.TypeLibrary.GetType<Component>( name, true, true ) is not { } type ) return;

					var added = on.Components.Create( type, true );

					if ( added is not null && property is not null ) type.SetValue( added, property, value );
				}

				Effect( go, "Rinten.Sharpen", "Scale", 0.2f );
				Effect( go, "Rinten.AmbientOcclusion" );
				Effect( go, "Rinten.ScreenSpaceReflections" );
				Effect( go, "Rinten.Bloom" );

			}

			//ambient light
			{
				var go = new GameObject( true, "ambient" );
				var c = go.AddComponent<AmbientLight>();
				c.Color = Color.White * 0.2f;
			}

			//sun light
			{
				var go = new GameObject( true, "sun" );
				var sun = go.AddComponent<DirectionalLight>();
				sun.Shadows = true;
				//Turned the way the camera looks - yaw 225, above - and a little to one side. A light travels along its forward (the
				//renderer takes -Forward as the way to it), so at yaw 45 it lit the side of the model facing away from the camera.
				sun.WorldRotation = new Angles( 60, 180 + 45 - 35, 0 );
				sun.LightColor = Color.White * 0.4f;
			}

			GameObject o = default;

			//prefab spawn
			{
				o = func();
				o.WorldPosition = 0;
			}

			//tick tick
			for ( int i = 0; i < 8; i++ )
			{
				scene.GameTick( 0.1f );
			}

			//place the camera
			{
				var bounds = o.GetBounds();

				var distance = MathX.SphereCameraDistance( bounds.Size.Length * 0.5f, camera.FieldOfView );
				var aspect = bitmap.Width / bitmap.Height;
				if ( aspect > 1 ) distance *= aspect;

				camera.WorldPosition = (o.WorldRotation * bounds.Center) + camera.WorldRotation.Forward * -distance;
			}

			//render twice, for any temporal shit to kick in
			camera.RenderToBitmap( bitmap );
			scene.GameTick( 0.1f );
			camera.RenderToBitmap( bitmap );

			scene.Destroy();
		}
		finally
		{
			scene.Destroy();
		}
	}

	/// <summary>Render a GameObject to a bitmap. This is usually used for easily rendering "previews" of GameObjects, for things like saving thumbnails etc.</summary>
	public static void RenderGameObjectToBitmap( GameObject objSource, Bitmap bitmap )
	{
		if ( objSource == null ) return;
		if ( bitmap == null ) return;

		Rinten.Rendering.TextureStreaming.ExecuteWithDisabled( () =>
		{
			RenderToBitmap( bitmap, () =>
			{
				var o = objSource.Clone();
				o.WorldPosition = 0;
				return o;
			} );
		} );
	}

	/// <summary>Render a Model to a bitmap. This is usually used for easily rendering "previews" of Models for thumbnails</summary>
	public static void RenderModelBitmap( Model model, Bitmap bitmap )
	{
		if ( model == null ) return;
		if ( bitmap == null ) return;

		Rinten.Rendering.TextureStreaming.ExecuteWithDisabled( () =>
		{
			RenderToBitmap( bitmap, () =>
			{
				var o = new GameObject();
				o.AddComponent<ModelRenderer>().Model = model;
				return o;
			} );
		} );
	}

	/// <summary>Run an action inside a batch group. A batchgroup is used with GameObject and Components to make sure that their OnEnable/OnDisable and other callbacks are called in a deterministic order, and that they can find each other during creation.</summary>
	[Obsolete( "Use Scene.BatchGroup() instead" )]
	public static void RunInBatchGroup( Action action )
	{
		using ( Game.ActiveScene?.BatchGroup() )
		{
			action?.Invoke();
		}
	}
}
