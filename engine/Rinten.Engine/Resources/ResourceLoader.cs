using Rinten.Engine;
using System.Diagnostics;
using System.Threading;

namespace Rinten;

internal static class ResourceLoader
{
	//Native resource extensions (with _c suffix) not covered by AssetTypeAttribute.
	private static readonly HashSet<string> NativeExtensions = new( StringComparer.OrdinalIgnoreCase )
	{
		".vmat_c", ".vmdl_c", ".vtex_c", ".shader_c", ".vanmgrph_c"
	};

	//Maps every extension a game resource can have, source and compiled name, to its asset type
	private static Dictionary<string, AssetTypeAttribute> ResourceTypes()
	{
		var types = new Dictionary<string, AssetTypeAttribute>( StringComparer.OrdinalIgnoreCase );

		foreach ( var type in Game.TypeLibrary.GetAttributes<AssetTypeAttribute>().DistinctBy( x => x.Extension ) )
		{
			types[$".{type.Extension}"] = type;
			types[$".{type.Extension}_c"] = type;
		}

		return types;
	}

	//Whether a compiled file is one of ours - see CompiledFile. Its head, not the whole file: one of theirs can be fifty megabytes of mesh.
	private static bool Packed( BaseFileSystem fileSystem, string file )
	{
		try
		{
			using var stream = fileSystem.OpenRead( file );

			Span<byte> head = stackalloc byte[12];

			return stream.ReadAtLeast( head, head.Length, throwOnEndOfStream: false ) == head.Length &&
				CompiledFile.IsCompiled( head );
		}
		catch ( Exception e ) when ( e is System.IO.IOException or UnauthorizedAccessException )
		{
			return false;
		}
	}

	//The source name of a file, whether or not it is the compiled one.
	private static string SourceOf( string file )
		=> file.EndsWith( "_c", StringComparison.OrdinalIgnoreCase ) ? file[..^2] : file;

	//Registers resource paths into PathIndex without loading them, for any file whose extension is in extensions. Called during LoadAllGameResource.
	private static void RegisterPaths( ReadOnlySpan<string> files, IReadOnlySet<string> extensions )
	{
		foreach ( var file in files )
		{
			if ( !extensions.Contains( System.IO.Path.GetExtension( file ) ) )
				continue;

			//RegisterPath calls FixPath internally, which strips the _c suffix.
			Game.Resources.RegisterPath( file );
		}
	}

	internal static void LoadAllGameResource( BaseFileSystem fileSystem, bool reloadExisting = false, Package sourcePackage = null )
	{
		var sw = Stopwatch.StartNew();
		var types = ResourceTypes();

		var allFiles = fileSystem.FindFile( "/", "*", true ).ToArray();

		//Union GameResource extensions with native-only ones so PathIndex covers everything.
		var allExtensions = new HashSet<string>( types.Keys, StringComparer.OrdinalIgnoreCase );
		allExtensions.UnionWith( NativeExtensions );

		RegisterPaths( allFiles, allExtensions );

		var allResources = new List<GameResource>();

		//A source and the compiled file beside it are one resource, and both are in the walk. Whichever comes out of it first is the one that loads.
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var file in allFiles )
		{
			var extension = System.IO.Path.GetExtension( file );

			if ( !types.TryGetValue( extension, out var type ) )
				continue;

			//Graphs, models and clips are Resource, not GameResource: there is no JSON shell to construct, and
			//Activator.CreateInstance as a GameResource is always null. They load through their own readers.
			if ( type.TargetType is null || !type.TargetType.IsAssignableTo( typeof( GameResource ) ) )
				continue;

			var source = SourceOf( file );
			if ( !seen.Add( source ) ) continue;

			//Skip compiled files with no source unless packed by our export; foreign ones are unreadable
			if ( !ReferenceEquals( source, file ) && !fileSystem.FileExists( source ) && !Packed( fileSystem, file ) )
				continue;

			//Skip resources that are already fully loaded - this allows calling this method multiple times (e.g. once per package) without redundant work.
			if ( !reloadExisting && ResourceLibrary.TryGet<GameResource>( source.Trim( '/' ), out var existing ) && !existing.IsPromise )
				continue;

			try
			{
				var se = Game.Resources.LoadGameResource( type, source, fileSystem, true, sourcePackage );
				if ( se != null ) allResources.Add( se );
			}
			catch ( Exception ex )
			{
				Log.Warning( ex, $"Exception when trying to load {file}" );
			}
		}

		//When we're loading a bunch of GameResource we defer their PostLoad until everything is loaded. Everyone is gonna wanna
		//do Resource.Get<>() within their PostLoad and not care about load order. This keeps things intuitive for end users.
		foreach ( var resource in allResources )
		{
			resource.PostLoadInternal();
		}

		foreach ( var type in types )
		{
			AddWatcherForType( type.Value );
		}

		//TODO: Check for edited but not saved OR recompiled assets and load in their values
		//on server/client like editing an asset while the gamemode is running would?
	}

	internal static async Task LoadAllGameResourceAsync( BaseFileSystem fileSystem, CancellationToken ct = default, bool reloadExisting = false, Package sourcePackage = null )
	{
		var sw = Stopwatch.StartNew();
		var types = ResourceTypes();

		var allExtensions = new HashSet<string>( types.Keys, StringComparer.OrdinalIgnoreCase );
		allExtensions.UnionWith( NativeExtensions );

		var allFiles = new List<string>();
		foreach ( var file in fileSystem.FindFile( "/", "*", true ) )
		{
			ct.ThrowIfCancellationRequested();
			allFiles.Add( file );
			if ( allExtensions.Contains( System.IO.Path.GetExtension( file ) ) )
				Game.Resources.RegisterPath( file );
			if ( sw.ElapsedMilliseconds > 8 ) { LoadingScreen.Subtitle = System.IO.Path.GetFileName( file ); await Task.Yield(); sw.Restart(); }
		}

		var allResources = new List<GameResource>();

		//As above: source and compiled file are one resource, and only one of them is loaded.
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var file in allFiles )
		{
			ct.ThrowIfCancellationRequested();
			var extension = System.IO.Path.GetExtension( file );
			if ( !types.TryGetValue( extension, out var type ) ) continue;

			var source = SourceOf( file );
			if ( !seen.Add( source ) ) continue;

			//Skip compiled files with no source unless packed by our export; foreign ones are unreadable
			if ( !ReferenceEquals( source, file ) && !fileSystem.FileExists( source ) && !Packed( fileSystem, file ) )
				continue;

			//Skip resources that are already fully loaded - this allows calling this method multiple times (e.g. once per package) without redundant work.
			if ( !reloadExisting && ResourceLibrary.TryGet<GameResource>( source.Trim( '/' ), out var existing ) && !existing.IsPromise )
				continue;

			try
			{
				var se = Game.Resources.LoadGameResource( type, source, fileSystem, true, sourcePackage );
				if ( se != null ) allResources.Add( se );
			}
			catch ( Exception ex )
			{
				Log.Warning( ex, $"Exception when trying to load {file}" );
			}

			if ( sw.ElapsedMilliseconds > 8 ) { LoadingScreen.Subtitle = System.IO.Path.GetFileName( file ); await Task.Yield(); sw.Restart(); }
		}

		foreach ( var resource in allResources )
		{
			ct.ThrowIfCancellationRequested();
			resource.PostLoadInternal();
			if ( sw.ElapsedMilliseconds > 8 ) { LoadingScreen.Subtitle = System.IO.Path.GetFileName( resource.ResourcePath ); await Task.Yield(); sw.Restart(); }
		}

		LoadingScreen.Subtitle = null;

		foreach ( var type in types )
			AddWatcherForType( type.Value );
	}


	static Dictionary<string, FileWatch> Watchers = new();

	static void AddWatcherForType( AssetTypeAttribute type )
	{
		if ( string.IsNullOrEmpty( type.Extension ) )
			return;

		//Watcher already set up for this type - no need to allocate another one.
		if ( Watchers.ContainsKey( type.TargetType.AssemblyQualifiedName ) )
			return;

		var watcher = EngineFileSystem.Mounted.Watch( $"*.{type.Extension}_c" );
		watcher.OnChanges += ( w ) => OnAssetFilesChanged( w, type );

		Watchers[type.TargetType.AssemblyQualifiedName] = watcher;

		//And the source, which is what gets edited, and is all a resource written in this engine has - nothing here compiles one.
		var source = EngineFileSystem.Mounted.Watch( $"*.{type.Extension}" );
		source.OnChanges += ( w ) => OnSourceFilesChanged( w, type );

		Watchers[$"{type.TargetType.AssemblyQualifiedName}:source"] = source;
	}

	private static void OnAssetFilesChanged( FileWatch watch, AssetTypeAttribute type )
	{
		var batch = new Batch();

		foreach ( var change in watch.Changes )
		{
			OnAssetFileChanged( change, type, batch );
		}

		batch.Settle( load: file => Game.Resources.LoadGameResource( type, file, EngineFileSystem.Mounted ) );
	}

	private static void OnSourceFilesChanged( FileWatch watch, AssetTypeAttribute type )
	{
		var batch = new Batch();

		foreach ( var change in watch.Changes )
		{
			OnSourceFileChanged( change, type, batch );
		}

		//Where a compiled file is beside it, the other watcher loads it - see OnSourceFileChanged. It still takes part in pairing a move.
		batch.Settle( load: source =>
		{
			var fs = EngineFileSystem.Mounted;
			if ( fs.FileExists( $"{source}_c" ) ) return;

			Game.Resources.LoadGameResource( type, source, fs );
		} );
	}

	//One watcher batch of adds/removes; pairs removal+arrival by name as a move to keep the resource
	private sealed class Batch
	{
		public readonly List<(string Path, GameResource Resource)> Removed = new();
		public readonly List<string> Added = new();

		public void Settle( Action<string> load )
		{
			foreach ( var (path, resource) in Removed )
			{
				var arrived = Added.FirstOrDefault( x => string.Equals( System.IO.Path.GetFileName( x ), System.IO.Path.GetFileName( path ), StringComparison.OrdinalIgnoreCase ) );

				if ( arrived is null && Removed.Count == 1 && Added.Count == 1 )
					arrived = Added[0];

				//Something already asked for the new name, and is holding a promise of it: that is what has to be filled, so it loads as an add.
				if ( arrived is not null && ResourceLibrary.TryGet<GameResource>( Resource.FixPath( arrived ), out _ ) )
					arrived = null;

				//Paired by name only - is it the same file?
				if ( arrived is not null && !FileWatch.IsSuppressedFlush && !SameSource( resource, arrived ) )
					arrived = null;

				if ( arrived is null )
				{
					Log.Info( $"Detected Asset File Deleted {path}" );

					//Removes from ResourceLibrary
					resource.DestroyInternal();
					continue;
				}

				Added.Remove( arrived );

				Log.Info( $"Detected Asset File Moved {path} -> {arrived}" );

				Game.Resources.Unregister( resource );
				resource.Register( arrived );
				Game.Resources.RegisterPath( arrived );

				//Read what arrived into the object that kept its identity, and tell whatever built itself from it.
				try
				{
					load( arrived );
					resource.PostReloadInternal();
				}
				catch ( Exception ex )
				{
					Log.Warning( ex, $"Exception reloading moved {arrived}" );
				}
			}

			foreach ( var file in Added )
			{
				Log.Info( $"Detected Added File {file}" );
				load( file );
			}
		}

		//Whether the source text of arrived is what resource was last loaded or saved
		//from. Only known in the editor, which is the only place a resource is moved.
		static bool SameSource( GameResource resource, string arrived )
		{
			if ( resource.LastSavedSourceHash == 0 ) return false;

			var source = arrived.EndsWith( "_c" ) ? arrived[..^2] : arrived;
			var fs = EngineFileSystem.Mounted;

			try
			{
				if ( !fs.FileExists( source ) ) return false;

				var json = fs.ReadAllText( source );
				return !string.IsNullOrEmpty( json ) && json.FastHash() == resource.LastSavedSourceHash;
			}
			catch ( Exception )
			{
				return false;
			}
		}
	}

	//Source file changed: reload it or forget the resource; adds/removes are settled with the batch
	static void OnSourceFileChanged( string file, AssetTypeAttribute type, Batch batch )
	{
		var fs = EngineFileSystem.Mounted;
		var source = SourceOf( file );

		if ( !fs.FileExists( source ) )
		{
			if ( ResourceLibrary.TryGet<GameResource>( source.Trim( '/' ), out var gone ) && !gone.IsPromise )
			{
				batch.Removed.Add( (source, gone) );
			}

			return;
		}

		var known = ResourceLibrary.TryGet<GameResource>( source.Trim( '/' ), out var asset ) && !asset.IsPromise;

		if ( !known )
		{
			batch.Added.Add( source );
			return;
		}

		if ( fs.FileExists( $"{source}_c" ) ) return;

		//What the editor itself just wrote. Reading our own save back would tell the tools the file changed underneath them, on every save.
		if ( asset.IsOwnWrite( fs.GetFullPath( source ) ) ) return;

		var json = fs.ReadAllText( source );

		if ( string.IsNullOrEmpty( json ) ) return;
		if ( json.FastHash() == asset.LastSavedSourceHash ) return;

		IToolsDll.Current?.RunEvent<ResourceLibrary.IEventListener>( i => i.OnExternalChanges( asset ) );

		Game.Resources.LoadGameResource( type, source, fs );

		//Read back over a resource that was already out in the world, so whatever built itself from the old one is told - a particle
		//player rebuilds its emitters off this, see ParticleDefinition. Loading calls PostLoad, which is for the first time.
		asset.PostReloadInternal();

		IToolsDll.Current?.RunEvent<ResourceLibrary.IEventListener>( i => i.OnExternalChangesPostLoad( asset ) );
	}

	static void OnAssetFileChanged( string file, AssetTypeAttribute type, Batch batch )
	{
		var fs = EngineFileSystem.Mounted;

		if ( !file.EndsWith( "_c" ) )
			file += "_c";

		//The editor's own write, of something the compiled file does not carry.
		if ( fs.GetFullPath( file ) is { } onDisk && ResourceReload.IsUnchanged( onDisk ) )
			return;

		//Asset doesn't exist, maybe just added?
		if ( !ResourceLibrary.TryGet<GameResource>( file.Trim( '/' ), out var asset ) || asset.IsPromise )
		{
			//file wasn't found, so I don't know what was happening.
			if ( !fs.FileExists( file ) )
				return;

			batch.Added.Add( file );
			return;
		}

		//File was removed - but held until the batch is settled, in case it was moved rather than deleted.
		if ( !fs.FileExists( file ) )
		{
			batch.Removed.Add( (file, asset) );
			return;
		}

		Span<byte> data = fs.ReadAllBytes( file );

		if ( data.Length <= 3 )
		{
			Log.Warning( $"Couldn't load json data from {file}" );
			return;
		}

		bool hasCompiledChanges = asset.TryLoadFromData( data );
		bool externalChanges = false;
		if ( hasCompiledChanges )
		{
			//check for source file changes
			if ( fs.FileExists( asset.ResourcePath ) )
			{
				var jsonBlob = fs.ReadAllText( asset.ResourcePath );
				if ( string.IsNullOrEmpty( jsonBlob ) ) return;

				var sourceHash = jsonBlob.FastHash();
				if ( sourceHash != asset.LastSavedSourceHash && asset.LastSavedSourceHash != 0 )
				{
					IToolsDll.Current?.RunEvent<ResourceLibrary.IEventListener>( i => i.OnExternalChanges( asset ) );
					externalChanges = true;
				}
			}
		}

		asset.PostReloadInternal();

		if ( externalChanges )
		{
			IToolsDll.Current?.RunEvent<ResourceLibrary.IEventListener>( i => i.OnExternalChangesPostLoad( asset ) );
		}
	}

	internal static void Clear()
	{
		//Dispose of watchers too
		foreach ( var watcher in Watchers ) watcher.Value.Dispose();
		Watchers.Clear();
	}
}
