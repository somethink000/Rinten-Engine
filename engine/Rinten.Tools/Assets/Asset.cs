using NativeEngine;
using System;

namespace Editor;

[SkipHotload]
public abstract partial class Asset
{
	//Suffixes of every file belonging to an asset, moved, copied or deleted together
	internal static readonly string[] RelatedFileExts =
	{
		"",				//.scene
		"_c",			//.scene_c, what an export packs it into
		"_d",			//.scene_d, the blob sidecar - see CompiledPaths.BlobSidecar
		"_c.baked",		//.mdl_c.baked
		"_c.chunks",	//The terrain chunks baked of a .terrain or .scene - see TerrainBakeCache
	};

	//The name every related file hangs off: the source path, never the compiled one.
	internal static string RelatedFileRoot( string path )
		=> path is not null && path.EndsWith( "_c", StringComparison.OrdinalIgnoreCase ) ? path[..^2] : path;

	internal Pixmap thumbnailOverride;

	internal Asset()
	{
		Tags = new AssetTags( this );
	}

	public override string ToString() => Path;

	internal abstract void UpdateInternals( bool compileImmediately = true );

	/// <summary>Tags for this asset, for filtering purposes in the Asset Browser.</summary>
	public AssetTags Tags { get; private set; }

	internal ulong AssetId { get; set; }

	/// <summary>Name of the asset, usually the filename.</summary>
	public string Name { get; protected set; }

	/// <summary>The relative path with the asset extension. ie .wav becomes .vsnd</summary>
	public string Path { get; protected set; }

	/// <summary>The relative path as it is on disk (ie .wav not .vsnd)</summary>
	public string RelativePath { get; protected set; }

	/// <summary>The absolute path as it is on disk (ie .wav not .vsnd)</summary>
	public string AbsolutePath { get; protected set; }

	//The absolute path as it is on disk. Should be null if we don't have source.
	internal string AbsoluteSourcePath { get; set; }

	//The absolute path as it is on disk. Should be null if we don't have a compiled.
	internal string AbsoluteCompiledPath { get; set; }

	/// <summary>When the asset was last opened through the editor.</summary>
	public DateTime? LastOpened { get; internal set; }

	/// <summary>The type of this asset.</summary>
	public AssetType AssetType { get; protected set; }

	/// <summary>If this asset was downloaded from the backend then this will be the package from which this asset was downloaded. If not then it'll be null.</summary>
	public Package Package { get; internal set; }

	/// <summary>Whether the asset is deleted or not. This can happen after <see cref="Delete"/> was called on it, or <see cref="AbsolutePath"/> is empty.</summary>
	public bool IsDeleted { get; internal set; }

	/// <summary>If true then this asset is generated at runtime somehow. Possibly from a mount system.</summary>
	public virtual bool IsProcedural => false;

	/// <summary>This asset is generated in the transient folder. You don't need to see it, or keep it around. It will re-generate from something else.</summary>
	public virtual bool IsTransient => AbsolutePath.Contains( Project.TransientPath );

	/// <summary>This asset is from the cloud, it's in the cloud folder</summary>
	public virtual bool IsCloud => AbsolutePath.Contains( Project.CloudPath );

	/// <summary>The asset was generated from another asset compile and has no source asset of its own. For example model break gibs, generated textures for materials, etc.</summary>
	public bool IsTrivialChild { get; protected set; }

	/// <summary>Can this asset be recompiled?</summary>
	public abstract bool CanRecompile { get; }

	/// <summary>Compiled file whose source is gone; not loaded, compiled or scanned for references</summary>
	public virtual bool IsCompiledLeftover => false;

	internal virtual void OnRemoved()
	{
		CachedThumbnail = default;
	}

	/// <summary>Delete this asset, and every file that goes with it - see <see cref="RelatedFileExts"/>.</summary>
	public bool Delete()
	{
		var path = RelatedFileRoot( AbsolutePath );
		if ( string.IsNullOrEmpty( path ) ) return false;

		//Held back rather than dropped, so the watchers see the whole delete in one batch once every file has gone.
		using ( FileWatch.Suppress() )
		{
			foreach ( var ext in RelatedFileExts )
			{
				EditorUtility.SendToRecycleBin( path + ext );
			}
		}

		IsDeleted = true;
		return true;
	}

	/// <summary>Returns the compiled file path, if the asset is compiled.</summary>
	public abstract string GetCompiledFile( bool absolute = false );

	/// <summary>Returns the source file path, if the sources are present.</summary>
	public abstract string GetSourceFile( bool absolute = false );

	internal Pixmap CachedThumbnail;
	public bool HasCachedThumbnail => CachedThumbnail is not null && CachedThumbnail != AssetType?.Icon256;

	/// <summary>Returns the asset preview thumbnail, with fallback to the asset type icon if there is no preview.</summary>
	public Pixmap GetAssetThumb( bool generateIfNotInCache = true )
	{
		return AssetThumbnail.GetAssetThumb( this, generateIfNotInCache );
	}

	public void CancelThumbBuild()
	{
		AssetThumbnail.DequeueThumbBuild( this );
	}

	internal abstract int FindIntEditInfo( string name );

	public abstract string FindStringEditInfo( string name );

	/// <summary>Delete existing cached thumbnail, optionally queuing for building a new one ASAP.</summary>
	public void RebuildThumbnail( bool startBuild = true )
	{
		//Nothing would draw a new one, so throwing the old one away only leaves a hole - and the inspector
		//asks for a rebuild every time it shows an asset that has no thumbnail, which is every scene.
		if ( AssetThumbnail.IsRenderedThumbnailBlocked( this ) )
			return;

		var fullPath = AssetThumbnail.GetThumbnailFile( this, false );

		try
		{
			if ( System.IO.File.Exists( fullPath ) )
				System.IO.File.Delete( fullPath );
		}
		catch ( System.Exception )
		{
			return;
		}

		CachedThumbnail = thumbnailOverride ?? AssetType.Icon256;

		if ( startBuild )
			AssetThumbnail.QueueThumbBuild( this );
		else
			CachedThumbnail = null;
	}

	/// <summary>Try to open this asset in a supported editor. You can specify nativeEditor to open in a specific editor.</summary>
	public abstract void OpenInEditor( string nativeEditor = null );

	/// <summary>Whether <see cref="OpenInEditor"/> can do anything useful for this asset. Used to decide whether to offer an "Open" action for assets that have no file on disk (e.g. mounted resources).</summary>
	public virtual bool CanOpenInEditor => true;

	/// <summary>Returns assets that this asset references/uses.</summary>
	public abstract List<Asset> GetReferences( bool deep );

	/// <summary>Returns assets that depend/use this asset.</summary>
	public abstract List<Asset> GetDependants( bool deep );

	/// <summary>Returns assets that are parents of this asset (i.e. this asset is a compiled child resource of the returned assets).</summary>
	public abstract List<Asset> GetParents( bool deep );


	/// <summary>Gets additional content-side related files. This includes like .rect files for materials, all .fbx and .lxo files for models, etc.</summary>
	public abstract List<string> GetAdditionalContentFiles();

	/// <summary>Gets additional game-side files to be packaged (e.g. navdata). These are files that are loaded by managed code, not as native resources.</summary>
	public abstract List<string> GetAdditionalGameFiles();

	/// <summary>Gets input dependencies for an asset. This'll be tga's for a texture and stuff like that.</summary>
	public abstract List<string> GetInputDependencies();

	/// <summary>Unrecognized reference paths listed by the data that could not be resolved into Asset*s</summary>
	public abstract List<string> GetUnrecognizedReferencePaths();

	/// <summary>Forcibly recompile the asset.</summary>
	public abstract bool Compile( bool full );

	MetaData _metadata;

	/// <summary>Asset type specific key-value based data storage.</summary>
	public MetaData MetaData
	{
		get
		{
			if ( _metadata != null ) return _metadata;

			var f = GetSourceFile( true );
			if ( string.IsNullOrEmpty( f ) ) f = GetCompiledFile( true );
			if ( string.IsNullOrEmpty( f ) ) return null;

			//tony: Don't make metadata for cloud assets.. not my favourite addition ever we're mounting all the
			//downloaded cloud assets first, and checking their tags, which is making fake metadata using the wrong paths
			if ( f.Contains( Project.CloudPath ) ) return null;

			//modelname.mdl_c -> modelname.mdl
			if ( f.EndsWith( "_c" ) )
				f = f.Substring( 0, f.Length - 2 );

			//modelname.mdl -> modelname.mdl.meta
			f += ".meta";

			_metadata = new MetaData( f );
			return _metadata;
		}
	}

	/// <summary>Renders the thumbnail and then saves it to disk.</summary>
	public async Task DumpThumbnail()
	{
		var thumb = await RenderThumb();
		var thumbPath = $"{AbsolutePath}.png";

		//Thumbnail might not be valid (e.g. not a supported asset type)
		if ( thumb == null )
			return;

		thumb.SavePng( thumbPath );
	}

	/// <summary>Immediately render a preview thumbnail for this asset, and return it.</summary>
	public Task<Pixmap> RenderThumb()
	{
		return AssetThumbnail.RenderAssetThumb( this );
	}

	/// <summary>Try to create a preview model if we're fbx, obj, etc</summary>
	public abstract Model GetPreviewModel();

	/// <summary>Try to load this asset as an automatically determined resource type. If this isn't a resource type (like an Image) then it will return null.</summary>
	public Resource LoadResource()
	{
		if ( AssetType.ResourceType is not { } type )
		{
			return null;
		}

		return LoadResource( type );
	}

	/// <summary>Try to load this asset as a <see cref="Resource"/> of given type.</summary>
	public T LoadResource<T>() where T : Resource
	{
		return LoadResource( typeof( T ) ) as T;
	}

	/// <summary>Try to load this asset as a <see cref="Resource"/> of given type.</summary>
	public Resource LoadResource( Type resourceType )
	{
		if ( resourceType.IsAssignableTo( typeof( GameResource ) ) )
		{
			if ( TryLoadGameResource( resourceType, out var o ) )
				return o;

			return null;
		}

		if ( resourceType.IsAssignableTo( typeof( Texture ) ) )
		{
			//Use the relative path here so we can load png/jpg/ect not *just* vtex
			return Texture.Load( RelativePath );
		}

		return Resource.Load( resourceType, Path );
	}

	/// <summary>Try to load this asset as a <see cref="Resource"/> of given type.</summary>
	public bool TryLoadResource<T>( out T obj ) where T : Resource
	{
		obj = LoadResource<T>();
		return obj != null;
	}

	private bool TryLoadGameResource<T>( out T obj ) where T : GameResource
	{
		obj = null;

		if ( TryLoadGameResource( typeof( T ), out GameResource resource ) == false )
		{
			return false;
		}

		obj = (T)resource;
		return obj is not null;
	}

	internal virtual bool TryLoadGameResource( Type t, out GameResource obj, bool allowCreate = false )
	{
		obj = null;

		//A file of no known type is no game resource, and a project half-open has no resource system yet to
		//ask - both were a NullReferenceException that took the rest of the tools' register event with it.
		if ( AssetType is null || Game.Resources is not { } resources || IsCompiledLeftover )
			return false;

		if ( !resources.TryGetType( AssetType.FileExtension, out var attribute ) )
			return false;

		if ( !attribute.TargetType.IsAssignableTo( t ) || attribute.TargetType.IsAbstract )
			return false;

		//Defer while a project opens or leftover models are queued; compiling here crashed
		if ( CanRecompile && !CompileOnOpen.Defer( Path ) )
		{
			Compile( false );
		}

		obj = GameResource.GetPromise( attribute.TargetType, Path );
		if ( obj != null && !obj.IsPromise )
			return true; //already exists and loaded

		//get compiled path
		var compiledFilePath = GetCompiledFile( true );

		if ( string.IsNullOrEmpty( compiledFilePath ) )
		{
			if ( allowCreate ) return true;

			Log.Warning( $"Tried to load {this} but couldn't get compiled file" );
			return false;
		}

		if ( !System.IO.File.Exists( compiledFilePath ) )
		{
			Log.Warning( $"Tried to load {this} but compiled file doesn't exist ({compiledFilePath})" );
			return false;
		}

		var data = System.IO.File.ReadAllBytes( compiledFilePath );
		if ( data == null )
		{
			Log.Warning( $"Tried to load {this} but couldn't read file" );
			return false;
		}

		if ( !obj.TryLoadFromData( data ) )
		{
			Log.Warning( $"Tried to load {this} but couldn't load from data" );
			return false;
		}

		return true;
	}

	/// <summary>Try to get the raw Json string, for a managed asset type (a GameResource)</summary>
	public unsafe string ReadJson()
	{
		//Don't bother: not json, or a compiled file that is no json at all.
		if ( !AssetType.IsGameResource || IsCompiledLeftover )
			return null;

		try
		{
			var filename = GetSourceFile( true );

			//Only a compiled file, and reading one of those was their resource system's - so there is no json to show for it.
			if ( string.IsNullOrWhiteSpace( filename ) )
				return null;

			return System.IO.File.ReadAllText( filename );
		}
		catch ( System.Exception e )
		{
			Log.Warning( e, $"Couldn't read {Path}: {e.Message}" );

			return null;
		}
	}

	/// <summary>Rebuild the compiled model from an unsaved definition.</summary>
	public bool CompileModel( ModelDefinition definition )
	{
		var filename = GetSourceFile( true );
		if ( string.IsNullOrWhiteSpace( filename ) || definition is null )
			return false;

		return ModelDefinitionCompiler.Compile( definition, filename );
	}

	/// <summary>Save a game resource instance to disk. This is used internally by asset inspector and for asset creation.</summary>
	public virtual bool SaveToDisk( GameResource obj, bool compile = true, bool background = false )
	{
		if ( obj == null )
			return false;

		var filename = GetSourceFile( true );

		if ( string.IsNullOrWhiteSpace( filename ) )
			return false;

		var attribute = EditorTypeLibrary.GetAttributes<AssetTypeAttribute>().Where( x => x.Extension == AssetType.FileExtension ).FirstOrDefault();

		if ( attribute == null || !obj.GetType().IsAssignableTo( attribute.TargetType ) )
			return false;

		RecordOpened();

		//Timed apart. Turning a resource into the words that go in the file is two things - the
		//properties into json, then the json into text - and they are slow for different reasons.
		var started = System.Diagnostics.Stopwatch.GetTimestamp();

		var json = obj.Serialize();

		var serialised = System.Diagnostics.Stopwatch.GetTimestamp();

		var jsonString = json.ToJsonString( Json.options );

		var whole = System.Diagnostics.Stopwatch.GetElapsedTime( started ).TotalMilliseconds;

		if ( whole > 50.0 )
		{
			var properties = System.Diagnostics.Stopwatch.GetElapsedTime( started, serialised ).TotalMilliseconds;

			Log.Info( $"{Path}: {jsonString.Length / 1024}kb of json in {whole:0.#}ms " +
				$"(properties {properties:0.#}ms, text {whole - properties:0.#}ms)" );
		}

		for ( int retry = 0; retry < 10; retry++ )
		{
			try
			{
				obj.SaveToDisk( filename, jsonString, background );

				//The terrain chunks the editor baked of what this holds - a terrain, or a scene one is set in - packed beside it for a game
				if ( Rinten.TerrainBakeCache.Holds( Path ) )
				{
					var holder = Path;
					var pack = Rinten.CompiledPaths.TerrainChunks( filename );

					System.Threading.Tasks.Task.Run( () => Rinten.TerrainBakeCache.WritePack( holder, pack ) );
				}

				if ( compile ) Compile( false );

				obj.Register( Path );
				return true;
			}
			catch ( System.Exception e )
			{
				Log.Warning( e, $"Problem writing {filename} - retrying" );

				if ( retry == 9 )
				{
					Log.Warning( "Aborting - couldn't write." );
				}

				System.Threading.Thread.Sleep( 200 );
			}
		}

		return false;
	}

	/// <summary>Saves without rebuilding, keeping a current compiled file current (e.g. a model's anim graph)</summary>
	public virtual bool SaveToDiskUnbuilt( GameResource obj ) => SaveToDisk( obj, compile: false );

	/// <summary>Tell asset system that this asset was opened. Sticks it on the recent opened list.</summary>
	public abstract void RecordOpened();

	/// <summary>Whether the asset is compiled.</summary>
	public abstract bool IsCompiled { get; }

	/// <summary>Whether the asset is compiled and all dependencies are up to date. (Slower than IsCompiled)</summary>
	public abstract bool IsCompiledAndUpToDate { get; }

	/// <summary>Whether the asset failed to compile.</summary>
	public abstract bool IsCompileFailed { get; }

	/// <summary>Returns a task that will resolve when the asset is compiled. If the asset is already compiled, do nothing. Does not support maps.</summary>
	public abstract ValueTask<bool> CompileIfNeededAsync( float timeout = 30.0f );

	/// <summary>Override the Assets thumbnail with given one.</summary>
	public void OverrideThumbnail( Pixmap pixmap )
	{
		thumbnailOverride = pixmap;
		RebuildThumbnail();
	}

	/// <summary>True if we have a source file, and aren't just a _c file</summary>
	public abstract bool HasSourceFile { get; }

	/// <summary>True if we have a compiled file, and aren't just a source file</summary>
	public abstract bool HasCompiledFile { get; }

	/// <summary>A free-use variable for the editor to use to portray that this asset somehow has changes that need to be saved to disk.</summary>
	public bool HasUnsavedChanges { get; set; }

	/// <summary>Set data for this asset which will be compiled in memory. This is used to preview asset changes (like materials) before committing to disk.</summary>
	public abstract bool SetInMemoryReplacement( string sourceData );

	/// <summary>Reverse the changes of SetInMemoryReplacement</summary>
	public abstract void ClearInMemoryReplacement();

	internal virtual void Uncache() { }
	internal virtual Task<bool> CacheAsync() { return Task.FromResult( false ); }
}
