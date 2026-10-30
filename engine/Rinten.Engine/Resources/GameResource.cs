using Facepunch.ActionGraphs;
using Rinten.ActionGraphs;
using Rinten.Engine;
using Rinten.Internal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Rinten;

/// <summary>Assets defined in C# and created through tools. You can define your own <a href="">Custom Asset Types</a>.</summary>
[Library]
public abstract partial class GameResource : Resource, ISourceLineProvider
{
	//Allows tools to post process the serialized json object
	internal static Action<object, JsonObject> ProcessSerializedObject { get; set; }

	[JsonInclude]
	[JsonPropertyName( "__references" )]
	internal string[] referencedPackages { get; set; }

	//The last saved compiled hash for this file.
	private int _jsonHash;

	//The last saved uncompiled hash for this file. We use this to detect external changes in the editor, it's never serialized.
	[Hide, JsonIgnore]
	internal int LastSavedSourceHash { get; set; }

	//Re-use ActionGraph instances when deserializing this resource.
	[JsonIgnore]
	internal ActionGraphCache ActionGraphCache { get; } = new();

	bool _unsavedChanges;

	/// <summary>True if this resource has changed but the changes aren't written to disk</summary>
	[Hide, JsonIgnore]
	public sealed override bool HasUnsavedChanges => _unsavedChanges;

	//Binary data to be written alongside the JSON file.
	[Hide, JsonIgnore]
	internal byte[] BinaryData
	{
		get => _binaryData;
		set
		{
			_binaryData = value;
			if ( value is not null ) BinaryDataGeneration++;
		}
	}

	byte[] _binaryData;

	//Moves on whenever the resource's blobs may have changed: new BinaryData was set, or the resource was read back from json (and so
	//from whatever blobs are on disk). Lets something that parsed the blobs once keep them until they change - see PrefabFile.BlobTable.
	[Hide, JsonIgnore]
	internal int BinaryDataGeneration { get; private set; }

	//The blobs of a save, just before they are written and let go of.
	internal virtual void OnBinaryDataSaved( byte[] data )
	{
	}

	/// <summary>Should be called after the resource has been edited by the inspector</summary>
	public sealed override void StateHasChanged()
	{
		_unsavedChanges = true;
	}

	//The package this resource was loaded from, if it came from a downloaded/mounted package. Null for resources that belong to the local/current project.
	[Hide, JsonIgnore]
	internal Package Package { get; set; }

	/// <summary>True if this resource was loaded from a mounted remote (cloud) package rather than the local project.</summary>
	[Hide, JsonIgnore]
	public bool IsRemote => Package is { IsRemote: true };

	//True if we're waiting for our load to complete
	bool _awaitingLoad;

	//True if we're a promise, waiting to finalize the load
	internal bool IsPromise => _awaitingLoad;

	/// <summary>Get a list of packages that are needed to load this asset</summary>
	public IEnumerable<string> GetReferencedPackages()
	{
		return referencedPackages ?? Array.Empty<string>();
	}

	//Called when the asset is first loaded from disk.
	protected virtual void PostLoad()
	{
	}

	internal bool PostLoadInternal()
	{
		_awaitingLoad = false;

		try
		{
			PostLoad();
			return true;
		}
		catch ( System.Exception e )
		{
			Log.Error( e );
			return false;
		}
	}

	//Called when the asset is recompiled/reloaded from disk.
	protected virtual void PostReload()
	{
	}

	internal bool PostReloadInternal()
	{
		_awaitingLoad = false;

		try
		{
			PostReload();
			return true;
		}
		catch ( System.Exception e )
		{
			Log.Error( e );
			return false;
		}
	}

	//Creates an instance of this type that will get loaded into later. This allows us to
	//have resources that reference other resources that aren't loaded yet (or are missing).
	internal static GameResource GetPromise( System.Type type, string filename )
	{
		var path = FixPath( filename );
		if ( string.IsNullOrEmpty( path ) ) return default;

		var obj = Game.Resources.Get( type, path ) as GameResource;
		if ( obj != null ) return obj;

		if ( !type.IsAssignableTo( typeof( GameResource ) ) )
			return default;

		if ( Mounting.MountUtility.IsMountPath( path ) )
		{
			if ( !Mounting.Directory.TryLoad( path, out var mounted ) )
				return null;

			if ( mounted is GameResource resource && resource.GetType().IsAssignableTo( type ) )
				return resource;

			Log.Warning( $"Mounted resource '{path}' is not a '{type.FullName}'." );
			return null;
		}

		obj = System.Activator.CreateInstance( type ) as GameResource;

		if ( obj is null )
		{
			Log.Warning( $"Failed to create '{type.FullName}'" );
			return default;
		}

		obj.InternalInitialize( filename );

		Game.Resources.Register( obj );
		return obj;
	}

	private void InternalInitialize( string filename )
	{
		ResourcePath = FixPath( filename );
		ResourceName = System.IO.Path.GetFileNameWithoutExtension( ResourcePath );
		//Keep this for backwards compat for now
#pragma warning disable CS0618 //Type or member is obsolete
		ResourceId = ResourcePath.FastHash();
#pragma warning restore CS0618 //Type or member is obsolete
		ResourceIdLong = ResourcePath.FastHash64();

		Manifest = AsyncResourceLoader.Load( ResourcePath );

		_awaitingLoad = true;
	}

	//Makes sure all properties are derived properly from filename, and then registered to ResourceLibrary
	internal void Register( string filename )
	{
		if ( string.IsNullOrEmpty( filename ) ) return;

		ResourcePath = FixPath( filename );
		ResourceName = System.IO.Path.GetFileNameWithoutExtension( ResourcePath );
		//Keep this for backwards compat for now
#pragma warning disable CS0618 //Type or member is obsolete
		ResourceId = ResourcePath.FastHash();
#pragma warning restore CS0618 //Type or member is obsolete
		ResourceIdLong = ResourcePath.FastHash64();

		Game.Resources.Register( this );
	}

	Mounting.ResourceLoader _mountLoader;
	GlobalContext _mountContext;

	internal void RegisterMounted( Mounting.ResourceLoader loader )
	{
		Register( loader.Path );
		_mountLoader = loader;
		_mountContext = GlobalContext.Current;
		loader.ShutdownActions += OnMountShutdown;
	}

	void OnMountShutdown()
	{
		using var scope = new GlobalContext.GlobalContextScope( _mountContext );
		DestroyInternal();
	}

	/// <summary>Loads a game resource by path, including mount:// paths.</summary>
	public static T Load<T>( string filename ) where T : GameResource
	{
		if ( string.IsNullOrWhiteSpace( filename ) )
			return null;

		if ( ResourceLibrary.TryGet<T>( filename, out var resource ) )
		{
			return resource;
		}

		if ( Mounting.MountUtility.IsMountPath( filename ) )
			return GetPromise( typeof( T ), filename ) as T;

		return null;
	}

	private SerializationOptions _serializationOptions;

	[JsonIgnore, Hide]
	internal SerializationOptions SerializationOptions => _serializationOptions ??= CreateSerializationOptions();

	//Target type used for any action graphs contained in this resource. Defaults to this resource's type.
	[JsonIgnore, Hide]
	protected virtual Type ActionGraphTargetType => ActionGraphTarget?.GetType() ?? GetType();

	//Target instance used for any action graphs contained in this resource. Defaults to this resource itself.
	[JsonIgnore, Hide]
	protected virtual object ActionGraphTarget => this;

	private SerializationOptions CreateSerializationOptions()
	{
		return new(
			Cache: ActionGraphCache,
			WriteCacheReferences: false,
			ForceUpdateCached: true,
			SourceLocation: new GameResourceSourceLocation( this ),
			ImpliedTarget: ActionGraphTargetType is { } targetType
				? InputDefinition.Target( targetType, ActionGraphTarget )
				: null );
	}

	//Pushes a context in which action graphs belonging to this resource can be serialized or deserialized.
	protected internal IDisposable PushSerializationScope()
	{
		return ActionGraph.PushSerializationOptions( SerializationOptions );
	}

	/// <summary>Serialize the current state to a JsonObject</summary>
	public JsonObject Serialize()
	{
		JsonObject jsobj;

		using ( PushSerializationScope() )
		using ( var blobs = BlobDataSerializer.Capture() )
		{
			jsobj = Json.SerializeAsObject( this );
			OnJsonSerialize( jsobj );

			var capturedData = blobs.ToByteArray();
			if ( capturedData != null || BinaryData == null )
			{
				BinaryData = capturedData;
			}
		}

		jsobj["__version"] = ResourceVersion;
		ProcessSerializedObject?.Invoke( this, jsobj );

		return jsobj;
	}

	//called to upgrade a bunch of json to the latest version
	void JsonUpgrade( JsonObject node )
	{
		if ( ResourceVersion == 0 )
			return;

		var serializedVersion = (int)(node["__version"] ?? 0);
		if ( serializedVersion >= ResourceVersion ) return;

		JsonUpgrader.Upgrade( serializedVersion, node, GetType() );
	}

	string ISourcePathProvider.Path => ResourcePath;
	int ISourceLineProvider.Line => 0;

	//The version of the component. Used by JsonUpgrader.
	[JsonIgnore, Hide] public virtual int ResourceVersion => 0;

	public void LoadFromJson( string json )
	{
		Assert.NotNull( json, "json should not be null" );

		_jsonHash = json.FastHash();
		BinaryDataGeneration++;

		var docOptions = new JsonDocumentOptions();
		docOptions.MaxDepth = 512;

		//Read in place, out of the document's pooled buffers: a tree of nodes for every resource the game has was the most of what the collector chased at
		//startup (on a page, a third of the resources' time). The tree only where it is changed - a resource written by an older version, which is upgraded first.
		using var document = JsonDocument.Parse( json, docOptions );
		var root = document.RootElement;

		if ( root.ValueKind != JsonValueKind.Object )
		{
			throw new ArgumentException( "Couldn't load json" );
		}

		using var blobs = BlobDataSerializer.Load( BinaryData, ResourcePath );

		if ( NeedsUpgrade( root ) )
		{
			var nodeOptions = new JsonNodeOptions();
			nodeOptions.PropertyNameCaseInsensitive = true;

			var jso = JsonObject.Create( root, nodeOptions );

			JsonUpgrade( jso );
			jso.Remove( "__version" );

			Deserialize( jso );
		}
		else
		{
			using ( PushSerializationScope() )
			{
				Json.DeserializeToObject( this, root );
			}
		}

		BinaryData = null;
		_awaitingLoad = false;
	}

	//Whether this json was written by an older version of the resource - see JsonUpgrade.
	bool NeedsUpgrade( JsonElement root )
	{
		if ( ResourceVersion == 0 ) return false;

		var written = 0;

		foreach ( var property in root.EnumerateObject() )
		{
			if ( !property.NameEquals( "__version" ) && !string.Equals( property.Name, "__version", StringComparison.OrdinalIgnoreCase ) ) continue;
			if ( property.Value.ValueKind == JsonValueKind.Number ) written = property.Value.GetInt32();
			else if ( property.Value.ValueKind == JsonValueKind.String && int.TryParse( property.Value.GetString(), out var parsed ) ) written = parsed;
			break;
		}

		return written < ResourceVersion;
	}

	/// <summary>Deserialize values from a JsonObject</summary>
	public void Deserialize( JsonObject jso )
	{
		using ( PushSerializationScope() )
		{
			Json.DeserializeToObject( this, jso );
		}
	}

	/// <summary>Copy the serialized state of another resource into this one, including binary blob data.</summary>
	public void CopyFrom( GameResource source )
	{
		var json = source.Serialize();
		json.Remove( "__version" );

		//Resolve $blob references from the binary data Serialize just captured
		using var blobs = BlobDataSerializer.LoadFromMemory( source.BinaryData );
		Deserialize( json );
	}

	//Called after we serialize, allowing you to store any extra or modify the output.
	protected virtual void OnJsonSerialize( JsonObject node )
	{
	}

	internal bool TryLoadFromData( Span<byte> data )
	{
		var json = Game.Resources.ReadCompiledResourceJson( data );
		if ( json is null )
			return false;

		//Prevent loading the same data twice todo - this should be a CRC on data
		var newHash = json.FastHash();
		if ( newHash == _jsonHash )
			return false;

		//Load binary data from compiled resource binary blobs if present
		BinaryData = Game.Resources.ReadCompiledResourceBlock( BlobDataSerializer.CompiledBlobName, data );

		LoadFromJson( json );
		LoadFromResource( data );
		return true;
	}

	internal virtual bool LoadFromResource( Span<byte> data )
	{
		return false;
	}

	//Atomic save: write a temp file beside it, flush, then rename over; falls back to direct write
	private static void WriteWithoutLosingIt( string filename, byte[] contents )
	{
		var directory = System.IO.Path.GetDirectoryName( filename );
		var temporary = filename + ".writing";

		try
		{
			if ( !string.IsNullOrEmpty( directory ) ) System.IO.Directory.CreateDirectory( directory );

			using ( var stream = new System.IO.FileStream( temporary, System.IO.FileMode.Create,
				System.IO.FileAccess.Write, System.IO.FileShare.None ) )
			{
				stream.Write( contents, 0, contents.Length );

				//Onto the disk rather than into the operating system's idea of it: a rename that lands before the contents do is a file that exists and is empty.
				stream.Flush( flushToDisk: true );
			}

			//One step either way: over the top of what is there, or into a name that has nothing in it.
			System.IO.File.Move( temporary, filename, overwrite: true );
		}
		catch ( System.Exception e )
		{
			Log.Warning( e, $"Could not write {filename} the safe way - writing it directly" );

			try { System.IO.File.Delete( temporary ); } catch ( System.Exception ) { }

			System.IO.File.WriteAllBytes( filename, contents );
		}
	}

	internal virtual void SaveToDisk( string filename, string jsonString, bool background = false )
	{
		_unsavedChanges = false;

		LastSavedSourceHash = jsonString.FastHash();

		var bytes = System.Text.Encoding.UTF8.GetBytes( jsonString );

		if ( background )
			ResourceWrites.Queue( filename, bytes, WriteWithoutLosingIt );
		else
			WriteWithoutLosingIt( filename, bytes );

		//Write binary data if present
		if ( BinaryData != null && BinaryData.Length > 0 )
		{
			OnBinaryDataSaved( BinaryData );

			try
			{
				var blobPath = CompiledPaths.BlobSidecar( filename );

				if ( background )
					ResourceWrites.Queue( blobPath, BinaryData, WriteWithoutLosingIt );
				else
					WriteWithoutLosingIt( blobPath, BinaryData );
			}
			catch ( Exception e )
			{
				Log.Warning( e, $"Failed to write binary data file: {e.Message}" );
			}
			finally
			{
				BinaryData = null;
			}
		}

		IToolsDll.Current?.RunEvent<ResourceLibrary.IEventListener>( i => i.OnSave( this ) );
	}

	/// <summary>Waits for every resource write still on its way to the disk.</summary>
	public static void FlushWrites() => ResourceWrites.Flush();

	internal virtual bool CanLoadFromJson()
	{
		return true;
	}

	private bool _destroyed;

	[Hide, JsonIgnore] public sealed override bool IsValid => !_destroyed;

	//Called when this resource is being unloaded. Clean up any resources owned by this instance here.
	protected virtual void OnDestroy()
	{

	}

	internal void DestroyInternal()
	{
		if ( _destroyed ) return;

		_destroyed = true;

		Game.Resources.Unregister( this );

		try
		{
			OnDestroy();
		}
		catch ( Exception ex )
		{
			Log.Warning( ex, $"{ex.GetType().Name} when destroying {ResourcePath}" );
		}
		finally
		{
			if ( _mountLoader is not null )
				_mountLoader.ShutdownActions -= OnMountShutdown;
			_mountLoader = null;
			_mountContext = null;
		}
	}
}

