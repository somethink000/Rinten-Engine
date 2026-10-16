using Facepunch.ActionGraphs;
using Rinten.ActionGraphs;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rinten;

public partial class GameObject
{
	internal const int GameObjectVersion = 2;

	//The only flags we actually save. Everything else is runtime junk (Loading, Bone, etc) and saving
	//it just causes phantom Flags overrides in prefab diffs. Networking is the exception, see below.
	internal const GameObjectFlags PersistedFlags =
					GameObjectFlags.ProceduralBone |
					GameObjectFlags.EditorOnly |
					GameObjectFlags.NotNetworked |
					GameObjectFlags.Absolute |
					GameObjectFlags.PhysicsBone |
					GameObjectFlags.Static |
					GameObjectFlags.Hidden;

	//Helper variable for editor refreshes during deserialization.
	private bool _removeAfterDeserializationRefresh = false;

	public class SerializeOptions
	{
		/// <summary>If we're serializing for network, we won't include any networked objects</summary>
		public bool SceneForNetwork { get; set; }

		/// <summary>We're cloning this object</summary>
		[Obsolete( "Has no effect" )]
		public bool Cloning { get; set; }

		/// <summary>We're going to send a single network object</summary>
		public bool SingleNetworkObject { get; set; }

		//Serialize the full hierarchy, prefab instances will be expanded to include all their children and components. All instances will be
		//considered nested instances. The path to the prefab instance will be included in the JSON JsonKeys.EditorPrefabInstanceNestedSource Implies
		internal bool SerializePrefabForDiff { get; set; }

		//Serialize the prefab instance so it can be used to update the state of the prefab. Makes sure TopLevel
		//nested prefabs are flagged, so they can get converted to a full prefab instance in the prefab.
		internal bool SerializeForPrefabInstanceToPrefabUpdate { get; set; }

		//Capture shallow undo state while preserving existing prefab ownership on restore.
		internal bool SerializeForUndo { get; set; }

		//Don't serialize gameObject children.
		internal bool IgnoreChildren { get; set; }

		//Don't serialize gameObject components.
		internal bool IgnoreComponents { get; set; }

		//Skip null members; receivers must deserialize with ClearAbsentFields so absent means null
		internal bool SkipNulls { get; set; }

		internal bool ShouldSave( GameObject gameObject )
		{
			var shouldIgnoreNotSavedFlag = SingleNetworkObject || SceneForNetwork;

			//Marked as do not save. This means to disk, really.
			if ( gameObject.Flags.Contains( GameObjectFlags.NotSaved ) && !shouldIgnoreNotSavedFlag ) return false;

			//We're saving for the network.
			if ( SceneForNetwork || SingleNetworkObject )
			{
				if ( gameObject.NetworkMode == NetworkMode.Never ) return false;
				if ( gameObject.Flags.Contains( GameObjectFlags.NotNetworked ) ) return false;
			}

			var isObjectNetworked = gameObject.Network.Active || gameObject.NetworkMode == NetworkMode.Object;

			//If we're serializing the entire scene to send down the network, then don't send this object if it's a network object, it'll get sent in the snapshots.
			if ( SceneForNetwork && isObjectNetworked ) return false;

			return true;
		}
	}

	private static readonly SerializeOptions _defaultSerializeOptions = new();

	public struct DeserializeOptions
	{
		//Update the existing hierarchy in place, matched by GUID, keeping references (undo, prefab patch)
		internal bool IsRefreshing { get; set; }

		//Should be used in Conjunction with IsRefreshing. Makes sure child networked objects are not removed during refresh.
		internal bool IsNetworkRefresh { get; set; }

		//When true, component properties absent from the JSON (e.g. omitted by SerializeOptions.SkipNulls)
		//are explicitly cleared to null/default rather than left at their existing value.
		internal bool ClearAbsentFields { get; set; }

		/// <summary>Allows overriding the transform when deserializing. Will apply only to the root object.</summary>
		public Transform? TransformOverride { get; set; }

		//Whether each object keeps hold of the json it was built from, so that the next refresh can tell what actually changed. On for the contents
		//of a prefab instance, which is the only thing that is ever refreshed. Elsewhere it would hold a whole scene's json alive for nothing.
		internal bool KeepJson { get; set; }
	}

	//The json this object was last built from, where it is worth remembering - see DeserializeOptions.KeepJson. A node inside the tree
	//the object was deserialised from, so remembering it costs a reference and keeps that one tree alive rather than a copy of it.
	internal JsonNode BuiltFrom;

	//Whether this object was built from exactly this json and so has nothing to do about it.
	internal bool WasBuiltFrom( JsonNode node )
	{
		return BuiltFrom is not null && JsonNode.DeepEquals( BuiltFrom, node );
	}

	private static readonly DeserializeOptions _defaultDeserializeOptions = new();

	//Stashed by Deserialize(), consumed by InvokeCallback(Deserialize) to avoid a closure allocation.
	private DeserializeOptions _pendingDeserializeOptions;

	/// <summary>Returns either a full JsonObject with all the GameObjects data, or if this GameObject is a prefab instance, it will return an object containing the patch/diff between instance and prefab.</summary>
	public virtual JsonObject Serialize( SerializeOptions options = null )
	{
		options ??= _defaultSerializeOptions;

		if ( !options.ShouldSave( this ) ) return null;

		if ( IsOutermostPrefabInstanceRoot && !options.SerializePrefabForDiff && !(options.SerializeForUndo && options.IgnoreChildren) && !options.SingleNetworkObject && !options.SceneForNetwork )
		{
			return SerializePrefabInstance();
		}

		var json = SerializeStandard( options );

		return json;
	}

	//Creates a JSON representation of this prefab instance including its overrides and GUID mappings.
	private JsonObject SerializePrefabInstance()
	{
		PrefabInstance.RefreshPatch();

		var json = new JsonObject();

		json[JsonKeys.Id] = Id;
		if ( GameObjectVersion != 0 ) json[JsonKeys.Version] = GameObjectVersion;
		json[JsonKeys.PrefabInstanceSource] = JsonValue.Create( PrefabInstance.PrefabSource );
		json[JsonKeys.PrefabInstancePatch] = Json.ToNode( PrefabInstance.Patch );
		json[JsonKeys.PrefabIdToInstanceId] = Json.ToNode( PrefabInstance.PrefabToInstanceLookup );

		return json;
	}

	//Returns a JsonObject containing all the GameObject's data.
	internal virtual JsonObject SerializeStandard( SerializeOptions options )
	{
		using var sceneScope = Scene.Push();

		//Will omit serializing the target of embedded Action Graphs
		using var targetScope = ActionGraph.PushTarget( InputDefinition.Target( typeof( GameObject ) ) );

		if ( !options.ShouldSave( this ) ) return null;

		var json = new JsonObject();

		json[JsonKeys.Id] = Id;
		if ( GameObjectVersion != 0 ) json[JsonKeys.Version] = GameObjectVersion;

		//Networking wants all the flags (it applies them verbatim on the other end), otherwise just the saved ones.
		var serializedFlags = (options.SceneForNetwork || options.SingleNetworkObject) ? Flags : (Flags & PersistedFlags);
		json[JsonKeys.Flags] = (long)serializedFlags;
		json[JsonKeys.Name] = Name;

		SerializeTransform( json );

		json.Add( JsonKeys.Tags, string.Join( ",", Tags.TryGetAll( false ) ) );
		json.Add( JsonKeys.Enabled, Enabled );
		json.Add( JsonKeys.NetworkMode, (int)NetworkMode );
		json.Add( JsonKeys.NetworkFlags, (int)NetworkFlags );
		json.Add( JsonKeys.NetworkOrphaned, (int)NetworkOrphaned );
		json.Add( JsonKeys.AlwaysTransmit, AlwaysTransmit );
		json.Add( JsonKeys.OwnerTransfer, (int)OwnerTransfer );

		if ( options.SerializeForUndo && options.IgnoreChildren && IsPrefabInstanceRoot )
		{
			json[JsonKeys.EditorSkipPrefabBreakOnRefresh] = true;
		}
		else if ( (!options.SceneForNetwork && !options.SingleNetworkObject)
				&& (IsNestedPrefabInstanceRoot || (IsOutermostPrefabInstanceRoot && options.SerializePrefabForDiff)) )
		{
			//For prefab updates all existing nested roots keep their instance data, regardless of depth. Marking them as nested source instead would
			//wipe their mappings in the prefab cache scene, where InitMappingsForNestedInstance has no outer instance lookup to rebuild them from.
			if ( options.SerializeForPrefabInstanceToPrefabUpdate )
			{
				json[JsonKeys.EditorSkipPrefabBreakOnRefresh] = true;
			}
			else
			{
				json[JsonKeys.EditorPrefabInstanceNestedSource] = JsonValue.Create( PrefabInstance.PrefabSource );
			}
		}

		//Preserve the prefab path when networking an instance
		if ( options.SingleNetworkObject && IsPrefabInstanceRoot )
		{
			json[JsonKeys.NetworkedPrefabInstance] = PrefabInstanceSource;
		}

		if ( !options.IgnoreComponents )
		{
			var components = new JsonArray();

			foreach ( var component in Components.GetAll() )
			{
				if ( component is null ) continue;

				if ( component is MissingComponent missing )
				{
					components.Add( missing.GetJson() );
					continue;
				}

				try
				{
					var result = component.Serialize( options );
					if ( result is null ) continue;

					components.Add( result );
				}
				catch ( System.Exception e )
				{
					Log.Warning( e, $"Exception when serializing {component} - skipping!" );
				}
			}

			json.Add( JsonKeys.Components, components );
		}

		if ( !options.IgnoreChildren )
		{
			var children = new JsonArray();

			for ( int i = 0; i < Children.Count; i++ )
			{
				var child = Children[i];

				if ( child is null ) continue;

				//Child GameObjects that are being destroyed don't want to be serialized
				if ( child.IsDestroyed ) continue;

				//check both our current network status, and our wish network status because we
				//might be in the middle of spawning, and will become network active after this.
				bool childIsNetworked = child.IsNetworkRoot || child.NetworkMode == NetworkMode.Object;

				//If this child is an independently networked object, and we're already serializing a single network object we should not include it.
				if ( options.SingleNetworkObject && childIsNetworked ) continue;

				try
				{
					var result = child.Serialize( options );

					if ( result is not null )
					{
						children.Add( result );
					}
				}
				catch ( System.Exception e )
				{
					Log.Warning( e, $"Exception when serializing GameObject" );
				}
			}

			json.Add( JsonKeys.Children, children );
		}

		return json;
	}

	//Which component types have already been named as slow to load.
	private static readonly HashSet<string> saidSlow = new( StringComparer.Ordinal );

	//Logs slow component loads once per type, and swaps placeholders for types that have since loaded
	internal int ResolveMissingComponents()
	{
		List<MissingComponent> placeholders = null;

		foreach ( var component in Components.GetAll() )
		{
			if ( component is not MissingComponent missing ) continue;

			placeholders ??= new List<MissingComponent>();
			placeholders.Add( missing );
		}

		if ( placeholders is null ) return 0;

		var resolved = 0;

		foreach ( var missing in placeholders )
		{
			var typeName = missing.TypeName;
			if ( string.IsNullOrEmpty( typeName ) ) continue;

			var type = Game.TypeLibrary.GetType<Component>( typeName, true );
			if ( type is null || type.TargetType.IsAbstract ) continue;

			var json = missing.GetJson();
			if ( json is null ) continue;

			var index = Components.IndexOf( missing );

			Component component;

			try
			{
				component = Components.Create( type, false );
			}
			catch ( Exception e )
			{
				Log.Warning( e, $"{typeName} came back but could not be made on {this}" );
				continue;
			}

			if ( component is null ) continue;

			component.Deserialize( json );

			if ( index >= 0 ) Components.MoveToIndex( component, index );

			missing.Destroy();
			resolved++;
		}

		if ( resolved > 0 )
		{
			Log.Info( $"{resolved} component(s) on {this} came back now their code has loaded" );
		}

		return resolved;
	}

	private static void SaidSlow( TypeDescription type, long startedAt )
	{
		var ms = ( System.Diagnostics.Stopwatch.GetTimestamp() - startedAt ) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

		ComponentLoadMs += ms;
		ComponentsLoaded++;

		if ( ms < 250.0 ) return;

		var name = type?.FullName ?? "unknown";

		lock ( saidSlow )
		{
			if ( !saidSlow.Add( name ) ) return;
		}

		Log.Warning( $"{name} took {ms:F0} ms to load - said once a type, see GameObject.SaidSlow" );
	}

	//Running total time and count of components created from JSON; never reset, diff two samples
	internal static double ComponentLoadMs;
	internal static int ComponentsLoaded;

	//Running ms totals for prefab instance fetch, patch and id remap, which can dominate scene loads
	internal static double PrefabFetchMs, PrefabPatchMs, PrefabRemapMs;

	private static double Since( long from, long to )
		=> ( to - from ) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

	public virtual void Deserialize( JsonObject node ) => Deserialize( node, _defaultDeserializeOptions );

	public virtual void Deserialize( JsonObject node, DeserializeOptions options )
	{
		ArgumentNullException.ThrowIfNull( node, nameof( node ) );

		using var sceneScope = Scene.Push();

		var serializedVersion = (int)(node[JsonKeys.Version] ?? 0);
		if ( serializedVersion < GameObjectVersion )
		{
			JsonUpgrader.Upgrade( serializedVersion, node, GetType() );
		}

		if ( node[JsonKeys.EditorSkipPrefabBreakOnRefresh] is null )
		{
			_prefabInstanceData = null;
		}

		//Handle nested prefab instances Only init with a path, we don't have any patches or lookups for nested instances.
		if ( PrefabSourceOf( node[JsonKeys.EditorPrefabInstanceNestedSource] ) is { } prefabNestedSource )
		{
			if ( this is not PrefabScene )
			{
				//Set the persisted id first; nested mapping gap-fill is seeded by it.
				DeserializeId( node );

				InitPrefabInstance( prefabNestedSource, true );

				var prefabFile = PrefabFile.Load( PrefabInstance.PrefabSource );
				if ( !IsPrefabLoaded( prefabFile ) )
				{
					PostDeserialize( options );
					return;
				}

				//Build the (unstored) nested mappings in PostDeserialize, once the subtree has
				//its final ids. Doing it here would run against temp ids and an empty subtree.
				_pendingNestedMappingId = node[JsonKeys.Id].Deserialize<Guid>();
			}
		}
		//Handle full prefab instances
		else if ( PrefabSourceOf( node[JsonKeys.PrefabInstanceSource] ) is { } prefabSource )
		{
			//Set the persisted id first; mapping gap-fill is seeded by it.
			DeserializeId( node );

			InitPrefabInstance( prefabSource, false );

			var prefabFile = PrefabFile.Load( PrefabInstance.PrefabSource );
			if ( !IsPrefabLoaded( prefabFile ) )
			{
				//Preserve patch and GUID mappings so the instance data survives save/load round-trips and can be fully restored when the prefab file comes back.
				if ( node[JsonKeys.PrefabInstancePatch] is JsonObject stubPatchJson )
				{
					PrefabInstance.InitPatch( Json.FromNode<Json.Patch>( stubPatchJson ) );
					PrefabInstance.InitLookups( node[JsonKeys.PrefabIdToInstanceId]?.Deserialize<Dictionary<Guid, Guid>>() ?? new Dictionary<Guid, Guid>() );
				}

				//Keep this object visible in the hierarchy as a disabled stub.
				DeserializeId( node );
				Name = $"[Missing Prefab] {PrefabInstance.PrefabSource}";
				_enabled = false;
				Flags |= GameObjectFlags.Error;

				PostDeserialize( options );
				UpdateEnabledStatus();
				return;
			}

			Json.Patch instancePatch = null;
			Dictionary<Guid, Guid> nodePrefabToInstanceId = null;
			if ( node[JsonKeys.PrefabInstancePatch] is JsonObject patchJson )
			{
				instancePatch = Json.FromNode<Json.Patch>( patchJson );
				nodePrefabToInstanceId = node[JsonKeys.PrefabIdToInstanceId]?.Deserialize<Dictionary<Guid, Guid>>() ?? new Dictionary<Guid, Guid>();
			}
			else
			{
				//This shouldn't be able to happen in a valid project, if it for some reason does we want to know about it.

				//The prefab is missing, catch this and demote to a warning
				if ( prefabFile.IsPromise )
				{
					Log.Warning( $"Unable to load prefab '{PrefabInstance.PrefabSource}'" );
				}
				else
				{
					Log.Error( $"Prefab instance '{PrefabInstance.PrefabSource}' missing overrides, upgrader did not run for some reason." );
				}

				PostDeserialize( options );
				return;
			}

			var atPrefab = System.Diagnostics.Stopwatch.GetTimestamp();

			var prefabScene = (PrefabCacheScene)GetPrefab( prefabSource );
			Assert.IsValid( prefabScene );

			var fullPrefabData = prefabScene.FullPrefabInstanceJson;

			var atPatch = System.Diagnostics.Stopwatch.GetTimestamp();

			node = Json.ApplyPatch( fullPrefabData, instancePatch, DiffObjectDefinitions );

			var atRemap = System.Diagnostics.Stopwatch.GetTimestamp();

			PrefabInstance.InitLookups( nodePrefabToInstanceId );
			PrefabInstance.InitPatch( instancePatch );
			PrefabInstance.RemapPrefabIdsToInstanceIds( ref node );

			//Where a prefab instance's time goes. Three steps that each walk the whole of a
			//prefab's json, and on a level authored as one big prefab that json is the level.
			PrefabFetchMs += Since( atPrefab, atPatch );
			PrefabPatchMs += Since( atPatch, atRemap );
			PrefabRemapMs += Since( atRemap, System.Diagnostics.Stopwatch.GetTimestamp() );

			//Everything below here is instance content, and instance content is the only thing that is ever handed new json and asked to become it.
			options.KeepJson = true;
		}

		DeserializeFlags( node, options );
		Flags |= GameObjectFlags.Deserializing;

		//Handle networked prefab instances, we just init the path
		if ( node[JsonKeys.NetworkedPrefabInstance] is JsonValue _prefab && _prefab.TryGetValue( out string prefabNetworkedSource ) )
		{
			InitPrefabInstance( prefabNetworkedSource, false );
		}

		//Stop right here if we are EditorOnly If we are a PrefabRoot/PrefabCacheScene marked
		//as editor only we still want to load as the Prefab might be referenced by instances
		if ( !Scene.IsEditor && Flags.Contains( GameObjectFlags.EditorOnly ) && this is not PrefabCacheScene )
		{
			//Immediately destroy this GameObject, we don't want it in the scene.
			DestroyImmediate();
			return;
		}

		Name = node.GetPropertyValue( "Name", Name );
		DeserializeTransform( node, options );

		_enabled = node.GetPropertyValue( JsonKeys.Enabled, false );

		using var batchGroup = CallbackBatch.Batch();

		DeserializeId( node );

		if ( node[JsonKeys.Tags].Deserialize<string>() is { } tags )
		{
			Tags.RemoveAll();
			Tags.Add( tags.Split( ',', StringSplitOptions.RemoveEmptyEntries ) );
		}

		if ( node.TryGetPropertyValue( JsonKeys.NetworkMode, out var propertyNode ) ) NetworkMode = (NetworkMode)(int)propertyNode;
		if ( node.TryGetPropertyValue( JsonKeys.NetworkOrphaned, out propertyNode ) ) NetworkOrphaned = (NetworkOrphaned)(int)propertyNode;
		if ( node.TryGetPropertyValue( JsonKeys.AlwaysTransmit, out propertyNode ) ) AlwaysTransmit = (bool)propertyNode;
		if ( node.TryGetPropertyValue( JsonKeys.OwnerTransfer, out propertyNode ) ) OwnerTransfer = (OwnerTransfer)(int)propertyNode;
		if ( node.TryGetPropertyValue( JsonKeys.NetworkFlags, out propertyNode ) ) NetworkFlags = (NetworkFlags)(int)propertyNode;

		if ( node[JsonKeys.Components] is JsonArray componentArray )
		{
			//Track which existing components we process so we can destroy any that disappeared, keeping an unchanged refresh allocation-free.
			Component[] existing = null;
			int existingCount = 0;
			Component[] processed = null;
			int processedCount = 0;

			if ( options.IsRefreshing )
			{
				existing = ArrayPool<Component>.Shared.Rent( Components.Count );
				foreach ( var c in Components.GetAll() ) existing[existingCount++] = c;

				processed = ArrayPool<Component>.Shared.Rent( componentArray.Count );
			}

			for ( int componentIndex = 0; componentIndex < componentArray.Count; componentIndex++ )
			{
				var component = componentArray[componentIndex];

				if ( component is not JsonObject componentJson )
				{
					Log.Warning( $"Component entry is not an object!" );
					continue;
				}

				string componentTypeName = componentJson.GetPropertyValue( Component.JsonKeys.Type, "" );

				//Prefer a same-named type from addon code over engine types, e.g. for RequireComponent references
				var componentType = Game.TypeLibrary.GetType<Component>( componentTypeName, true );

				//We didn't find this component type. So lets cut them some slack by searching for it
				//without the namespace - because maybe they refactored and didn't want everything to break..
				if ( componentType is null )
				{
					var idx = componentTypeName.LastIndexOf( '.' );
					if ( idx > 0 && idx < componentTypeName.Length - 1 )
					{
						var componentClassName = componentTypeName[(idx + 1)..];
						componentType = Game.TypeLibrary.GetType<Component>( componentClassName, true );
					}
				}

				//Okay definitely not found, lets give up
				if ( componentType is null || componentType.TargetType.IsAbstract )
				{
					Log.Warning( $"Missing Component: couldn't find Component type {componentTypeName} on {this}" );

					var missing = new MissingComponent( componentJson );
					Components.AddMissing( missing );
					continue;
				}

				Component c = null;

				if ( options.IsRefreshing )
				{
					var guid = componentJson[Component.JsonKeys.Id].Deserialize<Guid>();
					c = Scene.Directory.FindComponentByGuid( guid );
				}

				//Components should be created disabled, and then enabled in deserialize because if not, disabled components will enable and then disable.
				var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

				try
				{
					c ??= Components.Create( componentType, false );
				}
				catch ( Exception e )
				{
					Log.Error( e );
				}

				if ( c is null )
				{
					//The component was null, maybe there was an error creating it. Add a missing component reference.
					var missing = new MissingComponent( componentJson );
					Components.AddMissing( missing );
					continue;
				}

				//Already exactly this. Nothing a refresh could do to it that has not been done, and for a mesh or a collider
				//"doing it again" means building the geometry and the collision hull from the numbers it already has.
				var unchanged = options.IsRefreshing && c.WasBuiltFrom( componentJson );

				if ( !unchanged )
				{
					if ( options.ClearAbsentFields )
					{
						c.DeserializeInternal( componentJson, true );
					}
					else
					{
						c.Deserialize( componentJson );
					}

					c.BuiltFrom = options.KeepJson ? componentJson : null;
				}

				SaidSlow( componentType, startedAt );

				if ( options.IsRefreshing )
				{
					processed[processedCount++] = c;

					//change order of components needed
					if ( Components.IndexOf( c ) != componentIndex )
					{
						Components.MoveToIndex( c, componentIndex );
					}
				}
			}

			if ( options.IsRefreshing )
			{
				//Destroy any pre-existing component we didn't process this pass. We iterate the snapshot, not the live list, so destroying here is safe.
				for ( int i = 0; i < existingCount; i++ )
				{
					var existingComponent = existing[i];

					if ( WasProcessed( processed, processedCount, existingComponent ) ) continue;

					//Keep components that shouldn't be networked during a network refresh.
					if ( options.IsNetworkRefresh && existingComponent.Flags.Contains( ComponentFlags.NotNetworked ) ) continue;

					existingComponent.Destroy();
				}

				ArrayPool<Component>.Shared.Return( processed, clearArray: true );
				ArrayPool<Component>.Shared.Return( existing, clearArray: true );
			}
		}

		if ( node[JsonKeys.Children] is JsonArray childArray )
		{
			if ( options.IsRefreshing )
			{
				//Flag all our children for removal flag will be cleared if the child is found in the JSON somewhere
				foreach ( var child in Children )
				{
					child._removeAfterDeserializationRefresh = true;
				}
			}

			for ( int childIndex = 0; childIndex < childArray.Count; childIndex++ )
			{
				var child = childArray[childIndex];

				if ( child is not JsonObject jso ) continue;

				//This GameObject is only for the editor. Don't load it!
				if ( Flags.Contains( GameObjectFlags.EditorOnly ) && !Scene.IsEditor ) continue;

				GameObject go = null;

				if ( options.IsRefreshing )
				{
					var guid = jso[JsonKeys.Id].Deserialize<Guid>();
					go = Scene.Directory.FindByGuid( guid );

					if ( go is not null )
					{
						//Existing object may have been moved here from another go make sure we update the parent
						go.Parent = this;
						go._removeAfterDeserializationRefresh = false;

						//The whole subtree is already exactly this json, so there is nothing under it to visit either.
						//This is what makes a prefab edit cost the size of the edit rather than the size of the prefab.
						if ( go.WasBuiltFrom( jso ) )
						{
							KeepInOrder( go, childIndex );
							continue;
						}
					}
				}

				go ??= new GameObject( this, false );
				go.Deserialize( jso, options with { TransformOverride = default } );

				if ( options.KeepJson ) go.BuiltFrom = jso;

				if ( options.IsRefreshing ) KeepInOrder( go, childIndex );
			}
		}

		Components.ForEach( "OnLoadInternal", true, c => c.OnLoadInternal() );
		Components.ForEach( "OnValidate", true, c => c.Validate() );

		//PostDeserialize recurses into children, so if our parent is deserializing we don't need to queue a call to it ourselves

		if ( Parent is null || (Parent.Flags & GameObjectFlags.Deserializing) == 0 )
		{
			_pendingDeserializeOptions = options;
			CallbackBatch.Add( CommonCallback.Deserialize, this, "PostDeserialize" );
		}

		if ( options.KeepJson ) BuiltFrom = node;

		//Trigger OnEnabled after the GameObject has been deserialized fully, _enabled was set before, so OnAwake calls properly
		UpdateEnabledStatus();
	}

	//Puts a child where the json says it goes.
	private void KeepInOrder( GameObject go, int childIndex )
	{
		var childActualIndex = Children.IndexOf( go );
		if ( childActualIndex == childIndex ) return;

		//Swap the GameObject in the hierarchy to match the order in the JSON.
		Children[childActualIndex] = Children[childIndex];
		Children[childIndex] = go;
	}

	private static bool WasProcessed( Component[] processed, int count, Component component )
	{
		for ( int i = 0; i < count; i++ )
		{
			if ( ReferenceEquals( processed[i], component ) ) return true;
		}

		return false;
	}

	private void DeserializeFlags( JsonObject node, DeserializeOptions options )
	{
		if ( !node.TryGetPropertyValue( JsonKeys.Flags, out var inFlagNode ) )
			return;

		var inFlags = (GameObjectFlags)(long)inFlagNode;

		if ( options.IsRefreshing )
		{
			Flags = inFlags;
			return;
		}

		//Only take the flags we actually save, keep whatever runtime ones we already have.
		Flags = (Flags & ~PersistedFlags) | (inFlags & PersistedFlags);
	}

	private bool IsPrefabLoaded( PrefabFile prefabFile )
	{
		if ( prefabFile?.RootObject is null )
		{
			//Sol: the prefab is missing, register a promise like the REAL resource json converter does, so we know it's wanted.
			GameResource.GetPromise( typeof( PrefabFile ), PrefabInstance.PrefabSource );
		}

		if ( prefabFile == null || prefabFile.IsPromise )
		{
			Log.Warning( $"Unable to load prefab '{PrefabInstance.PrefabSource}'. Prefab is not loaded or does not exist." );
			return false;
		}

		return true;
	}

	//Serializing the transform depends on a bunch of stuff, so split it into this method for clarity.
	private void SerializeTransform( JsonObject json )
	{
		//If we're a physics bone then we need to serialize the local position.
		if ( Flags.Contains( GameObjectFlags.PhysicsBone ) )
		{
			var localTx = Parent?.WorldTransform.ToLocal( WorldTransform ) ?? WorldTransform;

			json.Add( JsonKeys.Position, JsonValue.Create( localTx.Position ) );
			json.Add( JsonKeys.Rotation, JsonValue.Create( localTx.Rotation ) );
			json.Add( JsonKeys.Scale, JsonValue.Create( localTx.Scale ) );
			return;
		}

		//If it's an animated bone, we don't bother with the transform position at all
		var isAttachment = Flags.Contains( GameObjectFlags.Attachment );
		var isAnimated = Flags.Contains( GameObjectFlags.Bone ) && !Flags.Contains( GameObjectFlags.ProceduralBone );

		if ( isAttachment || isAnimated )
		{
			json.Add( JsonKeys.Position, JsonValue.Create( Vector3.Zero ) );
			json.Add( JsonKeys.Rotation, JsonValue.Create( Rotation.Identity ) );
			json.Add( JsonKeys.Scale, JsonValue.Create( Vector3.One ) );
			return;
		}

		//The default is to just save the local transform
		{
			var tx = LocalTransform;

			json.Add( JsonKeys.Position, JsonValue.Create( tx.Position ) );
			json.Add( JsonKeys.Rotation, JsonValue.Create( tx.Rotation ) );
			json.Add( JsonKeys.Scale, JsonValue.Create( tx.Scale ) );
		}
	}

	//Again - this can be complicated, so this is extracted
	private void DeserializeTransform( JsonObject node, DeserializeOptions options )
	{
		//They're doing something special. Maybe they're creating a "duplication" at a certain position.
		if ( options.TransformOverride is Transform overrideTransform )
		{
			WorldTransform = overrideTransform;
			return;
		}

		//If we're a physics bone then we need to serialize the local position.
		if ( Flags.Contains( GameObjectFlags.PhysicsBone ) )
		{
			var tx = global::Transform.Zero;
			tx.Position = node[JsonKeys.Position]?.Deserialize<Vector3>() ?? Vector3.Zero;
			tx.Rotation = node[JsonKeys.Rotation]?.Deserialize<Rotation>() ?? Rotation.Identity;
			tx.Scale = node[JsonKeys.Scale]?.Deserialize<Vector3>() ?? Vector3.One;

			var worldTx = Parent?.WorldTransform.ToWorld( tx ) ?? tx;
			WorldTransform = worldTx;

			return;
		}

		//Only update transform if we're not refreshing or we aren't a bone. Bones use proxy transforms
		//and we don't want to set overrides here because then they won't animate correctly.
		if ( !(options.IsRefreshing && options.IsNetworkRefresh) || !Flags.Contains( GameObjectFlags.Bone ) )
		{
			var tx = global::Transform.Zero;
			tx.Position = node[JsonKeys.Position]?.Deserialize<Vector3>() ?? Vector3.Zero;
			tx.Rotation = node[JsonKeys.Rotation]?.Deserialize<Rotation>() ?? Rotation.Identity;
			tx.Scale = node[JsonKeys.Scale]?.Deserialize<Vector3>() ?? Vector3.One;

			//Use exact (bitwise) equality to avoid Vector3.operator== swallowing tiny differences within its 0.0001 AlmostEqual tolerance during deserialization.
			Transform.SetLocalTransformExact( tx );
		}
	}

	internal void DeserializeId( JsonObject node )
	{
		if ( node.TryGetPropertyValue( JsonKeys.Id, out var propertyNode ) ) SetDeterministicId( (Guid)propertyNode );
	}

	//Only needed for legacy support, when cloning.
	internal void DeserializePrefabVariables( JsonObject variables )
	{
		if ( variables is null || variables.Count == 0 ) return;

		var prefabFile = PrefabFile.Load( PrefabInstance.PrefabSource );
		if ( prefabFile is null ) return;

		var prefabScene = SceneUtility.GetPrefabScene( prefabFile );
		if ( prefabScene is null ) return;

		foreach ( (string name, JsonNode value) in variables )
		{
#pragma warning disable CS0612
			var variable = prefabScene.Variables.Where( x => x.Id == name ).FirstOrDefault();
#pragma warning restore CS0612
			if ( variable is null )
			{
				Log.Warning( $"Prefab Variable not in prefab: {name}" );
				continue;
			}

			foreach ( var target in variable.Targets )
			{
				if ( !PrefabInstance.PrefabToInstanceLookup.TryGetValue( target.Id, out Guid guid ) )
				{
					Log.Warning( $"Prefab variable target '{target.Id}' not found" );
					continue;
				}

				var component = Scene.Directory.FindComponentByGuid( guid );
				if ( component.IsValid() )
				{
					var t = Game.TypeLibrary.GetType( component.GetType() );

					if ( value is null )
						return;

					//TODO when we eventually get rid of DeserializePrefabVariables, make DeserializeProperty private again
					component.DeserializeProperty( t.Members.FirstOrDefault( x => x.Name == target.Property ), value );
				}
			}
		}
	}

	//Rewrites members marked with SyncFlags.FromHost to current component values for all component payloads inside a serialized game object tree.
	internal void PreserveFromHostSyncMembers( JsonObject node )
	{
		if ( node[JsonKeys.Components] is JsonArray componentArray )
		{
			foreach ( var componentNode in componentArray )
			{
				if ( componentNode is JsonObject componentJson )
				{
					Component existingComponent = null;

					if ( Scene.IsValid() && componentJson.TryGetPropertyValue( Component.JsonKeys.Id, out var componentIdNode ) )
					{
						try
						{
							existingComponent = Scene.Directory.FindComponentByGuid( componentIdNode.Deserialize<Guid>() );
						}
						catch
						{
							existingComponent = null;
						}
					}

					if ( existingComponent.IsValid() )
					{
						existingComponent.PreserveFromHostSyncMembers( componentJson );
					}
					else
					{
						RemoveFromHostSyncMembers( componentJson );
					}
				}
			}
		}

		if ( node[JsonKeys.Children] is JsonArray childArray )
		{
			foreach ( var childNode in childArray )
			{
				if ( childNode is JsonObject childJson )
				{
					PreserveFromHostSyncMembers( childJson );
				}
			}
		}
	}


	private void RemoveFromHostSyncMembers( JsonObject componentJson )
	{
		var componentTypeName = componentJson.GetPropertyValue( Component.JsonKeys.Type, "" );
		if ( string.IsNullOrEmpty( componentTypeName ) )
			return;

		var componentType = Game.TypeLibrary.GetType<Component>( componentTypeName, true );
		if ( componentType is null || componentType.TargetType.IsAbstract )
			return;

		foreach ( var propertyAndAttribute in ReflectionQueryCache.SyncProperties( componentType.TargetType ) )
		{
			if ( !propertyAndAttribute.Attribute.Flags.HasFlag( SyncFlags.FromHost ) )
				continue;

			componentJson.Remove( propertyAndAttribute.Property.Name );
		}
	}

	//Push ActionGraph source location and cache if we're a prefab instance.
	private ActionGraph.SerializationOptionsScope? PushDeserializeContext()
	{
		if ( IsPrefabInstanceRoot )
		{
			var prefabFile = PrefabFile.Load( PrefabInstanceSource );
			if ( prefabFile is null )
			{
				Log.Warning( $"Unable to find prefab source file: \"{PrefabInstanceSource}\"." );
				return null;
			}

			return ActionGraph.PushSerializationOptions( prefabFile.SerializationOptions with
			{
				ForceUpdateCached = false,
				GuidMap = PrefabInstance.InstanceToPrefabLookup
			} );
		}

		return null;
	}

	internal void PostDeserialize( DeserializeOptions options )
	{
		//Build deferred nested mappings now the subtree has its final ids, before PushDeserializeContext consumes the lookup.
		if ( _pendingNestedMappingId is Guid pendingNestedMappingId )
		{
			_pendingNestedMappingId = null;
			PrefabInstance.InitMappingsForNestedInstance( pendingNestedMappingId );
		}

		using var prefabContext = PushDeserializeContext();

		Components.ForEach( "PostDeserialize", true, c => c.PostDeserialize() );

		for ( int i = 0; i < Children.Count; i++ )
		{
			Children[i].PostDeserialize( options );
		}

		if ( options.IsRefreshing )
		{
			//Iterate all children check which are pending deletion and destroy them
			foreach ( var child in Children )
			{
				if ( options.IsNetworkRefresh )
				{
					//Only consider networked snapshot children for pruning during network refresh Skip independently networked objects and objects marked as not networked
					if ( child.NetworkMode != NetworkMode.Snapshot || child.Flags.Contains( GameObjectFlags.NotNetworked ) )
					{
						continue;
					}
				}

				if ( child._removeAfterDeserializationRefresh )
				{
					child.DestroyImmediate();
				}
			}
			Components.ForEach( "OnRefresh", true, c => c.OnRefreshInternal() );
		}

		Flags &= ~GameObjectFlags.Deserializing;
	}

	enum NetworkReferenceType
	{
		Invalid = 0,
		GameObject = 1,
		Prefab = 2,
	}

	static object BytePack.ISerializer.BytePackRead( ref ByteStream bs, Type targetType )
	{
		var refType = (NetworkReferenceType)bs.Read<byte>();

		switch ( refType )
		{
			case NetworkReferenceType.Invalid:
				return default;
			case NetworkReferenceType.GameObject:
				if ( !Game.ActiveScene.IsValid() ) return default;
				var id = bs.Read<Guid>();
				return Game.ActiveScene.Directory.FindByGuid( id );
			case NetworkReferenceType.Prefab:
				var resourceId = bs.Read<ulong>();
				var prefabFile = Game.Resources.GetByIdLong<PrefabFile>( resourceId );
				return SceneUtility.GetPrefabScene( prefabFile );
			default:
				return default;
		}
	}

	static void BytePack.ISerializer.BytePackWrite( object value, ref ByteStream bs )
	{
		if ( value is not GameObject go )
		{
			bs.Write( (byte)NetworkReferenceType.Invalid );
			return;
		}

		if ( go is PrefabScene prefabScene )
		{
			bs.Write( (byte)NetworkReferenceType.Prefab );
			bs.Write( prefabScene.Source.ResourceIdLong );
			return;
		}

		bs.Write( (byte)NetworkReferenceType.GameObject );
		bs.Write( go.Id );
	}

	public static object JsonRead( ref Utf8JsonReader reader, Type targetType )
	{
		if ( reader.TokenType == JsonTokenType.StartObject )
		{
			var goRef = JsonSerializer.Deserialize<GameObjectReference>( ref reader, EnumConvert.Plain );

			return goRef.Resolve( Game.ActiveScene, warn: true );
		}

		//Legacy way, guid or prefab
		if ( reader.TokenType == JsonTokenType.String )
		{
			if ( reader.TryGetGuid( out Guid guid ) )
			{
				var go = Game.ActiveScene.Directory.FindByGuid( guid );

				if ( go is null )
				{
					Log.Warning( $"Couldn't find GameObject {guid}" );
				}

				return go;
			}

			var stringValue = reader.GetString();

			//Added 12 dec 2023
			stringValue = stringValue.Replace( ".object", ".prefab", StringComparison.OrdinalIgnoreCase );

			//Load the prefab, not just a cache lookup: in game the cache only has what was already loaded
			var prefabFile = PrefabFile.Load( stringValue );

			if ( prefabFile is not null )
			{
				return SceneUtility.GetPrefabScene( prefabFile );
			}

			throw new Exception( $"Prefab not found '{stringValue}'" );
		}

		reader.Skip();
		return null;
	}

	public static void JsonWrite( object value, Utf8JsonWriter writer )
	{
		if ( value is not GameObject go )
			throw new NotImplementedException();

		if ( !go.IsValid )
		{
			writer.WriteNullValue();
			return;
		}

		JsonSerializer.Serialize( writer, GameObjectReference.FromInstance( go ), Json.options );
	}

	//The prefab a __Prefab entry names. This engine writes a plain path; legacy files write {"Id": guid,
	//"Path": path}, and only the path of that is read - there are no GUIDs here.
	internal static string PrefabSourceOf( JsonNode node )
	{
		if ( node is JsonValue value && value.TryGetValue( out string path ) )
			return path;

		if ( node is JsonObject reference && reference["Path"] is JsonValue held && held.TryGetValue( out string referenced ) )
			return referenced;

		return null;
	}

	//Json Keys used for serialization and deserialization of GameObjects. Kept here so they are easier to change, and we are less susceptible to typos.
	internal static class JsonKeys
	{
		internal const string PrefabInstanceSource = "__Prefab";
		internal const string PrefabInstancePatch = "__PrefabInstancePatch";
		internal const string PrefabIdToInstanceId = "__PrefabIdToInstanceId";
		internal const string PrefabInstanceVariables = "__PrefabVariables"; //Legacy

		internal const string Id = "__guid";
		internal const string Children = "Children";
		internal const string Components = "Components";
		internal const string Flags = "Flags";
		internal const string Name = "Name";
		internal const string Position = "Position";
		internal const string Rotation = "Rotation";
		internal const string Scale = "Scale";
		internal const string Enabled = "Enabled";
		internal const string Hidden = "Hidden";
		internal const string Tags = "Tags";
		internal const string Version = "__version";
		internal const string NetworkMode = "NetworkMode";
		internal const string NetworkFlags = "NetworkFlags";
		internal const string NetworkOrphaned = "NetworkOrphaned";
		internal const string AlwaysTransmit = "NetworkTransmit";
		internal const string OwnerTransfer = "OwnerTransfer";
		internal const string NetworkInterpolation = "NetworkInterpolation"; //Legacy

		//Network kyes for spawning prefa instance
		internal const string NetworkedPrefabInstance = "__NetworkedPrefaInstance";

		//Editor only keys used to influence serialization logic when performing editor actions
		internal const string EditorPrefabInstanceNestedSource = "__EditorPrefabNestedInstance";
		internal const string EditorSkipPrefabBreakOnRefresh = "__EditorSkipPrefabBreakOnRefresh";
	}
}
