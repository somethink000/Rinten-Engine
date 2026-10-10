using NativeEngine;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;

namespace Editor;

/// <summary>The asset system, provides access to all the assets.</summary>
public static partial class AssetSystem
{
	static Logger log = new Logger( "AssetSystem" );

	[SkipHotload]
	static Dictionary<ulong, Asset> allAssets = new();

	[SkipHotload] static ConcurrentDictionary<string, Asset> assetsByPath = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>All the assets that are being tracked by the asset system. Does not include deleted assets.</summary>
	public static IEnumerable<Asset> All => allAssets.Values.Where( x => !x.IsDeleted );
	static bool HasChanges;
	static bool IsInitialized = false;

	static HashSet<Asset> UpdateQueue = new();

	//Called after the asset types have been loaded from
	internal static void PreInitialize()
	{
		log.Trace( "PreInitialize" );

		foreach ( var type in AssetType.AssetTypeCache )
		{
			type.Value.Init();
		}
	}

	internal static void InitializeFromProject( Project project )
	{
		//Before anything is drawn: a picture compiled without it loses its settings until they next change.
		PictureBake.Install();
		HdrPictures.Install();

		string path = System.IO.Path.Combine( project.GetRootPath(), Project.CacheFolder, "cloud.db" );
		CloudDirectory = new CloudAssetDirectory( path );

		HasChanges = true;
		IsInitialized = true;
		Tick();
	}

	internal static void Shutdown()
	{
		CloudDirectory?.Dispose();
		CloudDirectory = null;
	}


	//An asset this side found itself, which without a native asset system is every asset
	internal static void AddManaged( Asset asset )
	{
		allAssets[asset.AssetId] = asset;

		UpdateQueue.Add( asset );
		HasChanges = true;
	}

	//One of ours changed on disk.
	internal static void ChangedManaged( Asset asset )
	{
		UpdateQueue.Add( asset );
		HasChanges = true;
	}

	static void UpdateAsset( Asset asset, bool compileImmediately = true )
	{
		asset.UpdateInternals( compileImmediately );

		if ( string.IsNullOrWhiteSpace( asset.Path ) )
			return;

		assetsByPath[asset.Path] = asset;
		assetsByPath[asset.AbsolutePath] = asset;
		assetsByPath[asset.RelativePath] = asset;
		assetsByPath[asset.AbsoluteCompiledPath] = asset;
	}


	//Startup only: the only place an Asset is destroyed; at runtime assets are just marked deleted
	internal static void AssetRemoved( uint index )
	{
		var a = allAssets[index];
		Assert.NotNull( a );

		log.Trace( $"Removed: {a}" );

		if ( a.Path is not null ) assetsByPath.TryRemove( a.Path, out _ );
		if ( a.AbsolutePath is not null ) assetsByPath.TryRemove( a.AbsolutePath, out _ );
		if ( a.RelativePath is not null ) assetsByPath.TryRemove( a.RelativePath, out _ );
		if ( a.AbsoluteCompiledPath is not null ) assetsByPath.TryRemove( a.AbsoluteCompiledPath, out _ );

		//Can it ever come back??
		UpdateQueue.Remove( a );
		allAssets.Remove( index );
		a.OnRemoved();

	}

	internal static void RecordAssetOpen( uint index )
	{
		var a = allAssets[index];
		Assert.NotNull( a );

		a.LastOpened = DateTime.Now;

	}

	internal static void AssetChanged( uint index )
	{
		var asset = allAssets[index];
		UpdateAsset( asset );

		if ( UpdateQueue.Add( asset ) )
		{
			NativeAssetProcessor.OnAssetChanged( asset );

			HasChanges = true;
		}
	}

	internal static void UpdateAssetAutoTags( uint index )
	{
		allAssets[index].UpdateAutoTags();
	}

	internal static void AssetScanComplete()
	{
		foreach ( var type in AssetType.AssetTypeCache )
		{
			type.Value.Init();
		}

		HasChanges = true;
		Tick();

		//No orphan pruning at startup: custom resource types aren't registered yet, so links look broken
		if ( !StartupLoadProject.IsLoading ) DeleteOrphans();
	}

	[EditorEvent.Frame]
	internal static void Tick()
	{
		if ( !HasChanges || !IsInitialized ) return;
		HasChanges = false;

		Parallel.ForEach( UpdateQueue, ( Asset asset ) => UpdateAsset( asset, false ) );

		//Thumbnails have their own queue, so do that all in main
		foreach ( var asset in UpdateQueue )
		{
			EditorEvent.RunInterface<IEventListener>( x => x.OnAssetChanged( asset ) );

			if ( asset.HasCachedThumbnail )
			{
				asset.RebuildThumbnail( true );
			}

			CompileIfStale( asset );
		}

		UpdateQueue.Clear();

		EditorEvent.RunInterface<IEventListener>( x => x.OnAssetSystemChanges() );
	}

	//Builds a file that arrived or changed while open, by the same staleness rule as the opening pass
	static void CompileIfStale( Asset asset )
	{
		//While the project opens, its own pass does this - logged and guarded.
		if ( StartupLoadProject.IsLoading ) return;
		if ( asset.IsDeleted || asset.IsTrivialChild ) return;

		var stale = asset.CanRecompile
			? !asset.IsCompiledAndUpToDate
			: !asset.IsCompiled && asset.AssetType is { IsGameResource: true };

		if ( !stale ) return;

		//A model is seconds of import: the queue builds one a frame, in its turn.
		if ( CompileOnOpen.IsModel( asset.Path ) )
		{
			CompileOnOpen.Enqueue( [asset] );
			return;
		}

		asset.Compile( false );
	}

	//Tells viewers of a rebuild and remakes the preview; not via ChangedManaged, which requeues failed builds
	internal static void Rebuilt( Asset asset )
	{
		ThreadSafe.AssertIsMainThread();

		EditorEvent.RunInterface<IEventListener>( x => x.OnAssetChanged( asset ) );

		if ( asset.HasCachedThumbnail )
			asset.RebuildThumbnail( true );
	}

	/// <summary>Find an asset by path.</summary>
	public static Asset FindByPath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return null;

		foreach ( var key in LookupKeys( path ) )
		{
			if ( assetsByPath.TryGetValue( key, out var asset ) && !asset.IsDeleted )
				return asset;
		}

		return null;
	}

	//Keys a path is known under; a leading slash is a disk path on Unix and a content path elsewhere, so try both
	internal static string[] LookupKeys( string path )
	{
		path = path.Replace( '\\', '/' );

		if ( path.Length > 0 && path[0] == '/' )
			return new[] { path, path.TrimStart( '/' ) };

		return new[] { path };
	}

	internal static Asset Get( uint index )
	{
		return allAssets[index];
	}


	//RegisterFile for each path with a single Tick, then load; that order must stay
	internal static void RegisterFiles( IReadOnlyList<string> absoluteFilePaths )
	{
		ThreadSafe.AssertIsMainThread();

		foreach ( var path in absoluteFilePaths )
		{
			var relative = Editor.FileSystem.Content?.GetRelativePath( path ) ?? System.IO.Path.GetFileName( path );
			CoreAssetScan.RegisterFile( path, relative );
		}

		Tick();

		foreach ( var path in absoluteFilePaths )
		{
			if ( CoreAssetScan.Find( path ) is not { } asset ) continue;

			asset.TryLoadGameResource( typeof( GameResource ), out _, true );
		}
	}

	public static Asset RegisterFile( string absoluteFilePath )
	{
		ThreadSafe.AssertIsMainThread();

		//Registering an asset tells the scan about a file it has not walked past yet
		var relative = Editor.FileSystem.Content?.GetRelativePath( absoluteFilePath ) ?? System.IO.Path.GetFileName( absoluteFilePath );

		CoreAssetScan.RegisterFile( absoluteFilePath, relative );

		Tick();

		var ret = CoreAssetScan.Find( absoluteFilePath );

		//A compiled file beside its source is not an asset of its own (see CoreAssetScan.RegisterFile)
		if ( ret is null ) return null;

		//Make sure the ResoureLibrary has proper loaded version, so that properties that target GameResources
		//do not break child assets if child asset was not loaded with Asset.CreateUI beforehand.
		ret.TryLoadGameResource( typeof( GameResource ), out _, true );

		return ret;
	}

	/// <summary>Deletes generated children nothing references.</summary>
	public static void DeleteOrphans()
	{
		//Only delete orphans from the current project's transient folder. Engine-shipped transients
		//(e.g. addons/menu/Transients/) are shared across all projects and must not be deleted.
		var transientRoot = FileSystem.Transient?.GetFullPath( "/" )?.NormalizeFilename( false );

		var orphans = All
						.Where( x => x.IsTrivialChild )
						.Where( x => !x.IsDeleted )
						.Where( x => x.AssetType == AssetType.Texture ) //Gib models can be trivial too; left out
						.Where( x => transientRoot is not null && x.AbsolutePath.StartsWith( transientRoot, StringComparison.OrdinalIgnoreCase ) )
						.ToArray();

		foreach ( var o in orphans )
		{
			var depCount = o.GetDependants( false ).Count;
			var parentCount = o.GetParents( false ).Count;

			if ( depCount == 0 && parentCount == 0 )
			{
				log.Info( $"Deleting Orphan \"{o}\"" );

				o.Delete();
				UpdateQueue.Add( o );
				HasChanges = true;
			}
		}
	}


	/// <summary>Create an empty <see cref="GameResource"/>.</summary>
	public static Asset CreateResource( string type, string absoluteFilename )
	{
		var gameResourceType = EditorTypeLibrary.GetAttributes<AssetTypeAttribute>().Where( x => x.Extension == type ).FirstOrDefault();
		if ( gameResourceType is null )
		{
			Log.Warning( $"Couldn't find matching resource type for extension {type}" );
			return null;
		}

		var extension = gameResourceType.Extension;
		absoluteFilename = System.IO.Path.ChangeExtension( absoluteFilename, extension );

		//try to find it first. If we find it, return it.
		var found = AssetSystem.FindByPath( absoluteFilename );
		if ( found is not null ) return found;

		//create an empty file
		System.IO.File.WriteAllText( absoluteFilename, "{}" );

		//convert it to an .asset
		var asset = RegisterFile( absoluteFilename );
		if ( asset == null )
		{
			log.Warning( $"Something went wrong when registering {absoluteFilename}" );
			return null;
		}

		//Load and save it, to create an empty version of it
		if ( asset.TryLoadGameResource( typeof( GameResource ), out var obj, true ) )
		{
			asset.SaveToDisk( obj );
		}

		return asset;
	}

	/// <summary>Passed parameters for the AssetPicker going from engine to addon code</summary>
	[EditorBrowsable( EditorBrowsableState.Never )]
	public struct AssetPickerParameters
	{
		public Widget ParentWidget { get; init; }
		public List<AssetType> FilterAssetTypes { get; init; }
		public Action<Asset[]> AssetSelectedCallback { get; init; }
		public int ViewMode { get; init; }
		public Asset InitialSelectedAsset { get; init; }
		public string Title { get; init; }
		public bool ShowCloudAssets { get; init; }
		public string InitialSearchText { get; init; }
	}


	//The editor opens its own asset picker; these handles stay for the API's shape

	static ulong _freeIndex = uint.MaxValue;

	internal static void AddAssetsFromMount( Rinten.Mounting.BaseGameMount source )
	{
		foreach ( var file in source.Resources )
		{
			var index = _freeIndex++;

			var assetType = AssetType.ResolveFromPath( file.Path );

			if ( assetType == null )
			{
				//Log.Warning( $"assetType was null for {file.Path}" );
				continue;
			}

			var asset = new MountAsset( index, file, source );

			allAssets[asset.AssetId] = asset;
			UpdateQueue.Add( asset );
			HasChanges = true;
		}

		Tick();
	}

	/// <summary>Callbacks for the asset system. Add this interface to your Widget to get events.</summary>
	public interface IEventListener
	{
		/// <summary>An asset has been modified</summary>
		void OnAssetChanged( Asset asset ) { }

		/// <summary>The thumbnail for an asset has been updated</summary>
		void OnAssetThumbGenerated( Asset asset ) { }

		/// <summary>Called when the asset system changed; refresh what lists assets.</summary>
		void OnAssetSystemChanges() { }

		/// <summary>Called when a new tag has been added to the asset system.</summary>
		void OnAssetTagsChanged() { }
	}

	/// <summary>Creates an Asset from a serialized property, expected to be an embedded asset property.</summary>
	public static Asset CreateEmbeddedAsset( SerializedProperty target )
	{
		return new EmbeddedAsset( target );
	}
}

