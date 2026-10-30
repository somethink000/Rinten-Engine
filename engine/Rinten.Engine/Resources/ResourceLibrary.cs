using NativeEngine;
using Rinten.Engine;
using System.Reflection;
using System.Runtime.InteropServices;
using static Rinten.ResourceLibrary;

namespace Rinten;

public class ResourceSystem
{
	//Index of Json based, GameResources PrefabFile, DecalDefintions etc.
	private Dictionary<int, Resource> ResourceIndex { get; } = new();

	//Weak references to native resources (Model, Material, Texture, Shader, etc.) — GC-friendly.
	private Dictionary<int, WeakReference<Resource>> WeakIndex { get; } = new();

	private Dictionary<ulong, Resource> ResourceIndexLong { get; } = new();

	private Dictionary<ulong, WeakReference<Resource>> WeakIndexLong { get; } = new();

	//Maps ResourceIdLong → path for all resources that exist on disk. Populated at startup without loading anything; used
	//as on-demand load fallback during network deserialization when a resource hasn't been loaded on the client yet.
	private Dictionary<ulong, string> PathIndex { get; } = new();

	//Every index above, under one lock: resources are made on worker threads too - a model built off the main thread
	//registers itself there - and a dictionary written from two threads at once is corrupted
	private readonly object indexLock = new();

	//Used to register resources path for use in networking
	internal void RegisterPath( string path )
	{
		path = Resource.FixPath( path );
		if ( string.IsNullOrEmpty( path ) ) return;
		lock ( indexLock ) PathIndex[path.FastHash64()] = path;
	}

	internal void Register( Resource resource )
	{
		lock ( indexLock )
		{
#pragma warning disable CS0618 //Type or member is obsolete
			ResourceIndex[resource.ResourceId] = resource;
#pragma warning restore CS0618 //Type or member is obsolete
			ResourceIndexLong[resource.ResourceIdLong] = resource;
		}

		if ( resource is GameResource gameResource && !gameResource.IsPromise )
		{
			IToolsDll.Current?.RunEvent<IEventListener>( i => i.OnRegister( gameResource ) );
		}
	}

	//Register a resource with a weak reference, allowing GC to collect it when no longer in use.
	internal void RegisterWeak( Resource resource )
	{
		lock ( indexLock )
		{
#pragma warning disable CS0618 //Type or member is obsolete
			WeakIndex[resource.ResourceId] = new WeakReference<Resource>( resource );
#pragma warning restore CS0618 //Type or member is obsolete
			WeakIndexLong[resource.ResourceIdLong] = new WeakReference<Resource>( resource );
		}
	}

	internal void Unregister( Resource resource )
	{
		//Only remove entries that index this exact resource. A stale instance whose index entry was replaced
		//(e.g. re-registered under the same path) must not unregister the live resource when it gets finalized.
		bool removed;

		lock ( indexLock )
		{
			removed = RemoveIndexed( ResourceIndexLong, resource.ResourceIdLong, resource );
			removed |= RemoveIndexedWeak( WeakIndexLong, resource.ResourceIdLong, resource );

#pragma warning disable CS0618 //Type or member is obsolete
			removed |= RemoveIndexed( ResourceIndex, resource.ResourceId, resource );
			removed |= RemoveIndexedWeak( WeakIndex, resource.ResourceId, resource );
#pragma warning restore CS0618 //Type or member is obsolete
		}

		if ( removed && resource is GameResource gameResource && !gameResource.IsPromise )
		{
			IToolsDll.Current?.RunEvent<IEventListener>( i => i.OnUnregister( gameResource ) );
		}
	}

	private static bool RemoveIndexed<TKey>( Dictionary<TKey, Resource> index, TKey key, Resource resource )
	{
		if ( !index.TryGetValue( key, out var indexed ) || indexed != resource )
			return false;

		return index.Remove( key );
	}

	private static bool RemoveIndexedWeak<TKey>( Dictionary<TKey, WeakReference<Resource>> index, TKey key, Resource resource )
	{
		if ( !index.TryGetValue( key, out var weakRef ) )
			return false;

		if ( !weakRef.TryGetTarget( out var target ) )
		{
			//A dead entry can't be this resource - we hold a live reference to it. Prune it, but it doesn't count as unregistering this resource.
			index.Remove( key );
			return false;
		}

		if ( target != resource )
			return false;

		return index.Remove( key );
	}

	internal void OnHotload()
	{
		TypeCache.Clear();
	}

	//Prune dead entries from the WeakIndex to prevent unbounded growth. Only runs every 30 seconds.
	TimeSince _lastWeakPrune;

	internal void PruneWeakIndex()
	{
		if ( _lastWeakPrune < 30 )
			return;

		_lastWeakPrune = 0;

		lock ( indexLock )
		{
			var dead = WeakIndex.Where( kvp => !kvp.Value.TryGetTarget( out _ ) ).Select( kvp => kvp.Key ).ToList();
			foreach ( var key in dead ) WeakIndex.Remove( key );

			var deadNative = WeakIndexLong.Where( kvp => !kvp.Value.TryGetTarget( out _ ) ).Select( kvp => kvp.Key ).ToList();
			foreach ( var key in deadNative ) WeakIndexLong.Remove( key );
		}
	}

	internal void Clear()
	{
		Resource[] toDispose;
		lock ( indexLock ) toDispose = ResourceIndex.Values.ToArray();

		foreach ( var resource in toDispose.OfType<GameResource>() )
		{
			resource.DestroyInternal();
		}

		foreach ( var resource in toDispose )
		{
			//Don't wait/rely for finalizer get rid of this immediately
			resource?.Destroy();
		}

		lock ( indexLock )
		{
			ResourceIndex.Clear();
			WeakIndex.Clear();

			ResourceIndexLong.Clear();
			WeakIndexLong.Clear();
			PathIndex.Clear();
		}

		TypeCache.Clear();
	}

	//Find all alive weak resources of a given type whose ResourcePath starts with the
	//given prefix. Used for hotload scenarios like SVG textures with query parameters.
	internal IEnumerable<T> FindWeakByPathPrefix<T>( string pathPrefix ) where T : Resource
	{
		KeyValuePair<int, WeakReference<Resource>>[] held;
		lock ( indexLock ) held = WeakIndex.ToArray();

		foreach ( var kvp in held )
		{
			if ( !kvp.Value.TryGetTarget( out var resource ) )
				continue;

			if ( resource is not T typed )
				continue;

			if ( resource.ResourcePath is not null && resource.ResourcePath.TrimStart( '/' ).StartsWith( pathPrefix, StringComparison.OrdinalIgnoreCase ) )
				yield return typed;
		}
	}

	internal Resource Get( System.Type t, string filepath )
	{
		filepath = Resource.FixPath( filepath );
		ulong identifier = filepath.FastHash64();

		lock ( indexLock )
		{
			if ( ResourceIndexLong.TryGetValue( identifier, out var resource ) )
				return resource.GetType().IsAssignableTo( t ) ? resource : null;

			if ( WeakIndexLong.TryGetValue( identifier, out var weakRef ) && weakRef.TryGetTarget( out var weakResource ) &&
				weakResource.GetType().IsAssignableTo( t ) )
				return weakResource;
		}

		return null;
	}

	/// <summary>Get a cached resource by its hash.</summary>
	[Obsolete( "Use Get<T>(path) instead. Identifier based access will be removed in a future update." )]
	public T Get<T>( int identifier ) where T : Resource
	{
		lock ( indexLock )
		{
			if ( ResourceIndex.TryGetValue( identifier, out var resource ) )
				return resource as T;

			if ( WeakIndex.TryGetValue( identifier, out var weakRef ) && weakRef.TryGetTarget( out var weakResource ) )
				return weakResource as T;
		}

		return default;
	}

	//Internal use only — do not expose ulong IDs publicly.
	internal T GetByIdLong<T>( ulong idLong ) where T : Resource
	{
		lock ( indexLock )
		{
			if ( ResourceIndexLong.TryGetValue( idLong, out var resource ) )
				return resource as T;

			if ( WeakIndexLong.TryGetValue( idLong, out var weakRef ) && weakRef.TryGetTarget( out var weakResource ) )
				return weakResource as T;
		}

		return default;
	}

	internal string LookupPath( ulong idLong )
	{
		lock ( indexLock ) return PathIndex.GetValueOrDefault( idLong );
	}

	/// <summary>Get a cached resource by its file path.</summary>
	public T Get<T>( string filepath ) where T : Resource
	{
		filepath = Resource.FixPath( filepath );

		return GetByIdLong<T>( filepath.FastHash64() );
	}

	/// <summary>Try to get a cached resource by its file path.</summary>
	public bool TryGet<T>( string filepath, out T resource ) where T : Resource
	{
		resource = Get<T>( filepath );
		return resource != null;
	}

	/// <summary>Get all cached resources of given type.</summary>
	public IEnumerable<T> GetAll<T>()
	{
		lock ( indexLock ) return ResourceIndex.Values.OfType<T>().Distinct().ToArray();
	}

	/// <summary>Get all cached resources of given type in a specific folder.</summary>
	public IEnumerable<T> GetAll<T>( string filepath, bool recursive = true ) where T : Resource
	{
		filepath = filepath.Replace( '\\', '/' );
		if ( !filepath.EndsWith( "/" ) ) filepath += "/";

		T[] all;
		lock ( indexLock ) all = ResourceIndex.Values.OfType<T>().Distinct().ToArray();

		return all.Where( x =>
		{
			if ( x.ResourcePath.StartsWith( filepath ) )
			{
				if ( recursive ) return true;
				if ( !x.ResourcePath.Substring( filepath.Length ).Contains( "/" ) ) return true;
			}
			return false;
		} );
	}

	//Returns stats about the ResourceIndex and WeakIndex for debug overlays.
	internal ResourceStats GetResourceStats()
	{
		var stats = new ResourceStats();

		lock ( indexLock )
		{
			foreach ( var resource in ResourceIndex.Values )
			{
				var typeName = resource.GetType().Name;
				stats.StrongIndex.TryGetValue( typeName, out var count );
				stats.StrongIndex[typeName] = count + 1;
			}

			foreach ( var kvp in WeakIndex )
			{
				var alive = kvp.Value.TryGetTarget( out var resource );
				var typeName = alive ? resource.GetType().Name : "(dead)";
				stats.WeakIndexEntries.TryGetValue( typeName, out var count );
				stats.WeakIndexEntries[typeName] = count + 1;
			}

			stats.StrongTotal = ResourceIndex.Count;
			stats.WeakTotal = WeakIndex.Count;
		}

		return stats;
	}

	internal struct ResourceStats
	{
		public Dictionary<string, int> StrongIndex;
		public Dictionary<string, int> WeakIndexEntries;
		public int StrongTotal;
		public int WeakTotal;

		public ResourceStats()
		{
			StrongIndex = new();
			WeakIndexEntries = new();
		}
	}

	//Reads a resource as JSON from a buffer; non-text bytes (foreign compiled containers) yield null
	internal string ReadCompiledResourceJson( Span<byte> data )
	{
		if ( data.Length == 0 )
			return null;

		//A byte order mark, if whatever wrote the file left one
		if ( data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF )
			data = data[3..];

		var start = 0;
		while ( start < data.Length && data[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' )
			start++;

		//Every resource of ours is a json object, so anything else is something else and saying so is better than handing back half of it
		if ( start >= data.Length || data[start] != (byte)'{' )
			return null;

		return System.Text.Encoding.UTF8.GetString( data[start..] );
	}

	//Read compiled resource as JSON from the provided file path.
	internal string ReadCompiledResourceJson( BaseFileSystem fs, string fileName )
	{
		if ( !fs.FileExists( fileName ) )
			return string.Empty;

		return ReadCompiledResourceJson( fs.ReadAllBytes( fileName ) );
	}

	internal unsafe byte[] ReadCompiledResourceBlock( string blockName, Span<byte> data )
	{
		//Compiled resource containers are not supported; a resource is read from its source instead and has no blocks.
		return null;
	}

	private Dictionary<string, AssetTypeAttribute> TypeCache { get; } = new( StringComparer.OrdinalIgnoreCase );

	//Get the AssetTypeAttribute for a given extension.
	internal bool TryGetType( string extension, out AssetTypeAttribute resourceAttribute )
	{
		if ( extension.StartsWith( '.' ) ) extension = extension[1..];
		if ( extension.EndsWith( "_c", StringComparison.OrdinalIgnoreCase ) ) extension = extension[..^2];

		if ( TypeCache.TryGetValue( extension, out resourceAttribute ) )
			return true;

		resourceAttribute = Game.TypeLibrary.GetAttributes<AssetTypeAttribute>()
			.FirstOrDefault( x => string.Equals( x.Extension, extension, StringComparison.OrdinalIgnoreCase ) );

		if ( resourceAttribute != null )
		{
			TypeCache[extension] = resourceAttribute;
			return true;
		}

		return false;
	}

	internal GameResource LoadRawGameResource( string path )
	{
		var extension = System.IO.Path.GetExtension( path );
		if ( !TryGetType( extension, out var type ) )
		{
			Log.Warning( $"Could not find GameResource for extension '{extension}'" );
			return null;
		}
		//No-op: nothing to do here.
		return default;
	}

	internal T LoadGameResource<T>( string file, BaseFileSystem fs, bool deferPostload = false, Package sourcePackage = null ) where T : GameResource
	{
		var attr = typeof( T ).GetCustomAttribute<AssetTypeAttribute>();
		if ( attr == null ) return default;

		//this is filled in automatically when accessed via TypeLibrary but this ain't TypeLibrary kiddo
		attr.TargetType = typeof( T );

		return LoadGameResource( attr, file, fs, deferPostload, sourcePackage ) as T;
	}

	//Loads a Gameresource from disk. Doesn't look at cache. Registers the resource if successful.
	internal GameResource LoadGameResource( AssetTypeAttribute type, string file, BaseFileSystem fs, bool deferPostload = false, Package sourcePackage = null )
	{
		Assert.NotNull( type );
		Assert.NotNull( file );

		if ( !file.EndsWith( "_c" ) ) file += "_c";

		Span<byte> data = null;

		try
		{
			//Prefer our packed compiled file; a foreign compiled file can't be read, so fall back to the source
			var sourceFile = file[..^2];

			string json = null;

			//Its header first: a model's compiled file is its built mesh, not one of these,
			//and reading all of it to find that out was hundreds of megabytes a project.
			if ( fs.FileExists( file ) && IsPacked( fs, file ) && CompiledFile.Unpack( fs.ReadAllBytes( file ) ) is { } packed )
			{
				json = packed;
			}
			else if ( fs.FileExists( sourceFile ) )
			{
				data = fs.ReadAllBytes( sourceFile );

				if ( data.Length <= 3 )
				{
					Log.Warning( $"		Skipping {file} (is null)" );
					return null;
				}

				json = System.Text.Encoding.UTF8.GetString( data );
			}

			if ( json is null )
			{
				//Neither one. Nothing can be read, and saying so once beats a stack trace per file.
				Log.Warning( $"		Skipping {file} (no source or compiled file to read)" );
				return null;
			}

			var se = GameResource.GetPromise( type.TargetType, file );
			if ( se is null ) return null;

			//Attribute the resource to the package it was loaded from (null = local project). Only overwrite when
			//a package is supplied so aggregate reloads don't clobber a previously attributed cloud package.
			if ( sourcePackage is not null )
				se.Package = sourcePackage;

			se.LoadFromJson( json );

			if ( Application.IsEditor )
			{
				var sourceFilePath = file.Substring( 0, file.Length - 2 );
				if ( fs.FileExists( sourceFilePath ) )
				{
					var jsonBlob = fs.ReadAllText( sourceFilePath );
					se.LastSavedSourceHash = jsonBlob.FastHash();
				}
			}

			//Purpose unclear - possibly for maps
			if ( Application.IsDedicatedServer )
			{
				InstallReferences( se );
			}

			Register( se );

			if ( !deferPostload )
				se.PostLoadInternal();

			return se;
		}
		catch ( System.Exception ex )
		{
			Log.Warning( ex, $"		Error when deserializing {file} ({ex.Message})" );
		}

		return null;
	}

	private static bool IsPacked( BaseFileSystem fs, string file )
	{
		using var stream = fs.OpenRead( file );
		return CompiledFile.IsCompiled( stream );
	}

	//Installs all references for a GameResource
	private void InstallReferences( GameResource se )
	{
		var references = se.GetReferencedPackages();

		foreach ( var r in references )
		{
			_ = PackageManager.InstallAsync( new PackageLoadOptions() { PackageIdent = r, ContextTag = "server" } );
		}
	}
}

/// <summary>Keeps a library of all available <see cref="Resource"/>.</summary>
public static class ResourceLibrary
{
	/// <summary>Get a cached resource by its hash.</summary>
	[Obsolete( "Use Get<T>(filepath) instead. Identifier based access will be removed in a future update." )]
	public static T Get<T>( int identifier ) where T : Resource => Game.Resources.Get<T>( identifier );

	/// <summary>Get a cached resource by its file path.</summary>
	public static T Get<T>( string filepath ) where T : Resource => Game.Resources.Get<T>( filepath );

	/// <summary>Try to get a cached resource by its file path.</summary>
	public static bool TryGet<T>( string filepath, out T resource ) where T : Resource => Game.Resources.TryGet<T>( filepath, out resource );

	/// <summary>Get all cached resources of given type.</summary>
	public static IEnumerable<T> GetAll<T>() => Game.Resources.GetAll<T>();

	/// <summary>Get all cached resources of given type in a specific folder.</summary>
	public static IEnumerable<T> GetAll<T>( string filepath, bool recursive = true ) where T : Resource => Game.Resources.GetAll<T>( filepath, recursive );

	/// <summary>Load a resource by its file path.</summary>
	public static async Task<T> LoadAsync<T>( string path ) where T : Resource
	{
		//try to load cached version first
		if ( TryGet<T>( path, out var cached ) )
			return cached;

		//Check if the type is a GameResource, and handle it accordingly
		var type = typeof( T );
		if ( type.IsAssignableTo( typeof( GameResource ) ) )
		{
			if ( Mounting.MountUtility.IsMountPath( path ) )
			{
				var mounted = await Mounting.Directory.TryLoadAsync( path );
				if ( mounted is null || mounted is T )
					return mounted as T;

				Log.Warning( $"Mounted resource '{path}' is not a '{type.FullName}'." );
				return null;
			}

			if ( type == typeof( PrefabFile ) )
			{
				return (T)(object)PrefabFile.Load( path );
			}

			//Really should be loaded already I think?
			return Get<T>( path );
		}

		if ( type == typeof( Model ) )
		{
			return (T)(object)(await Rinten.Model.LoadAsync( path ));
		}

		if ( type == typeof( Material ) )
		{
			return (T)(object)(await Rinten.Material.LoadAsync( path ));
		}

		return default;
	}

	/// <summary>Render a thumbnail for this resource</summary>
	public static async Task<Bitmap> GetThumbnail( string path, int width = 256, int height = 256 )
	{
		var resource = await ResourceLibrary.LoadAsync<Resource>( path );
		if ( resource is null ) return default;

		//try to render it
		return resource.RenderThumbnail( new() { Width = width, Height = height } );
	}

	public interface IEventListener
	{
		/// <summary>Called when a new resource has been registered</summary>
		void OnRegister( GameResource resource ) { }

		/// <summary>Called when a previously known resource has been unregistered</summary>
		void OnUnregister( GameResource resource ) { }

		/// <summary>Called when a resource has been saved</summary>
		void OnSave( GameResource resource ) { }

		/// <summary>Called when the source file of a known resource has been externally modified on disk</summary>
		void OnExternalChanges( GameResource resource ) { }

		/// <summary>Called when the source file of a known resource has been externally modified on disk and after it has been fully loaded (after post load is called)</summary>
		void OnExternalChangesPostLoad( GameResource resource ) { }
	}
}
