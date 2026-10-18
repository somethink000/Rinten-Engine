using System;
using System.IO;

namespace Editor;

//A project file as an asset: just the file and its extension-derived type, no dependency graph
[SkipHotload]
internal sealed class CoreAsset : Asset
{
	//Where the file is on disk.
	private readonly string absolute;

	//Its path inside the mounted content, which is how it is named.
	private readonly string relative;

	public CoreAsset( ulong assetId, string absolutePath, string relativePath )
	{
		AssetId = assetId;

		absolute = absolutePath.Replace( '\\', '/' );
		relative = relativePath.Replace( '\\', '/' ).TrimStart( '/' );
	}

	internal override void UpdateInternals( bool compileImmediately = true )
	{
		AssetType = AssetType.ResolveFromPath( relative );

		Name = System.IO.Path.GetFileNameWithoutExtension( relative );

		Path = relative;
		RelativePath = relative;
		AbsolutePath = absolute;

		//Nothing is compiled: the file the game loads is the file on disk, so the source and the compiled thing are the same thing.
		AbsoluteSourcePath = absolute;
		AbsoluteCompiledPath = absolute;

		IsDeleted = !File.Exists( absolute );
		IsTrivialChild = false;

		LoadUserTags();
		UpdateAutoTags();
	}

	/// <summary>Only a model, and only because a model is the one thing here that is built rather than read - see <see cref="Compile" />.</summary>
	public override bool CanRecompile => !IsCompiledLeftover && ( IsModel || Built || IsSound || IsPicture );

	//Where it is under the content, from the moment it was found - Asset.Path is filled in later.
	internal string FoundAs => relative;

	public override bool IsCompiledLeftover
		=> relative.EndsWith( "_c", StringComparison.OrdinalIgnoreCase ) && !File.Exists( absolute[..^2] );

	public override string GetCompiledFile( bool absolute = false ) => absolute ? AbsolutePath : Path;
	public override string GetSourceFile( bool absolute = false ) => absolute ? AbsolutePath : Path;

	internal override int FindIntEditInfo( string name ) => 0;
	public override string FindStringEditInfo( string name ) => null;

	/// <summary>Whether there is an editor for this: a window of its own, or a tab. Not whether opening it does anything - a resource with no editor is edited in the inspector, and a picture opens on the desktop.</summary>
	public override bool CanOpenInEditor => AssetType is not null && IAssetEditor.HasEditorFor( this );

	public override void OpenInEditor( string nativeEditor = null )
	{
		//Whatever the editor has an editor for, opened in it.
		if ( IAssetEditor.OpenInEditor( this, out _ ) ) return;

		//A resource with no editor of its own is edited in the inspector, so that is where opening it goes - see IAssetEditor.HasEditorFor.
		if ( AssetType is { IsGameResource: true } )
		{
			EditorUtility.Inspect( this );
			return;
		}

		//Anything else is the desktop's business.
		EditorUtility.OpenFolder( AbsolutePath );
	}

	//What references what. Their compiler wrote this down as it built; we have nobody to ask yet, so these are empty rather than wrong.

	public override List<Asset> GetReferences( bool deep ) => new();
	public override List<Asset> GetDependants( bool deep ) => new();
	public override List<Asset> GetParents( bool deep ) => new();

	public override List<string> GetAdditionalContentFiles() => new();
	/// <summary>Compiled outputs and sidecars a game build must ship alongside this file</summary>
	public override List<string> GetAdditionalGameFiles()
	{
		var files = new List<string>();

		var root = absolute.EndsWith( relative, StringComparison.OrdinalIgnoreCase ) ? absolute[..^relative.Length] : null;

		void Add( string file )
		{
			if ( string.IsNullOrWhiteSpace( file ) ) return;

			file = file.Replace( '\\', '/' );

			//A clip a definition names by a real path rather than a mounted one: named in the build from the same root as everything else, or not at all.
			if ( System.IO.Path.IsPathRooted( file ) )
			{
				if ( root is null || !file.StartsWith( root, StringComparison.OrdinalIgnoreCase ) ) return;
				file = file[root.Length..];
			}

			files.Add( file.TrimStart( '/' ) );
		}

		if ( IsModel )
		{
			Add( Rinten.CompiledPaths.Model( relative ) );
			Add( Rinten.AnimGraph.ModelGraphFile( relative ) );

			if ( Rinten.ModelDefinitionCompiler.ReadDefinitionOnly( absolute ) is { } definition )
			{
				foreach ( var clip in Rinten.CoreSkeletons.ClipsOf( relative, definition ).Values )
					Add( clip );
			}
		}
		else if ( Built || IsSound )
		{
			//A sound's bake, where its settings asked for one - see SoundBake.
			Add( Rinten.CompiledPaths.Built( relative ) );
		}

		//A resource's binary half, where it wrote one - a scene's meshes, a terrain. The json only names those by guid: a build that shipped
		//the scene without it had every scene mesh in it come up with nothing to build, and the game drew the error marker in their place.
		Add( Rinten.CompiledPaths.BlobSidecar( relative ) );

		//The terrain chunks the editor baked of what this holds - a terrain, or a scene one is set in - so the game starts without
		//baking them; see TerrainBakeCache. Shipped only where the file is there.
		Add( Rinten.CompiledPaths.TerrainChunks( relative ) );

		return files;
	}

	public override List<string> GetInputDependencies() => new();
	public override List<string> GetUnrecognizedReferencePaths() => new();

	//Whether this is a model definition, the one file type compiled here on save
	internal bool IsModel
		=> string.Equals( AssetType?.FileExtension, "mdl", StringComparison.OrdinalIgnoreCase );

	//Whether an addon registered a builder for this type (BuildsAssetTypeAttribute)
	private bool Built => AssetBuilders.Has( AssetType?.FileExtension );

	//Whether this is an audio file, whose settings are baked into the file the game plays - see SoundBake.
	private bool IsSound => AssetType == AssetType.SoundFile;

	//Whether this is a picture, whose settings are baked into its blocks as they are compiled - see PictureBake.
	private bool IsPicture => AssetType == AssetType.ImageFile;

	public override bool Compile( bool full )
	{
		return CompileOnOpen.One( Path, () => CompileBody( full ) );
	}

	private bool CompileBody( bool full )
	{
		if ( IsSound )
		{
			if ( !full && !SoundBake.IsStale( this, absolute ) ) return true;

			return SoundBake.Build( this, absolute );
		}

		if ( IsPicture )
		{
			if ( !full && !PictureBake.IsStale( absolute ) ) return true;

			return PictureBake.Build( this, absolute );
		}

		if ( !IsModel )
		{
			if ( !Built ) return false;

			if ( !full && !NeedsBuild() ) return true;

			return AssetBuilders.Build( AssetType?.FileExtension, this );
		}

		var built = ( !full && !NeedsModelBuild() ) || Rinten.ModelDefinitionCompiler.CompileToFile( absolute );

		//And the graph the model carries, which is built by the model editor - see BuildsAssetTypeAttribute. Not while it is
		//being built: building it reads the definition, and reading a definition asks this whether the model needs building.
		if ( Built && !buildingGraph && ( full || NeedsGraphBuild() ) )
		{
			buildingGraph = true;

			try
			{
				AssetBuilders.Build( AssetType?.FileExtension, this );
			}
			finally
			{
				buildingGraph = false;
			}
		}

		return built;
	}

	[ThreadStatic] private static bool buildingGraph;

	//Whether the graph compiled beside this model is missing or older; a cheap text check
	private bool NeedsGraphBuild()
	{
		try
		{
			var compiled = Rinten.AnimGraph.ModelGraphFile( absolute );

			if ( File.Exists( compiled ) && File.GetLastWriteTimeUtc( compiled ) >= File.GetLastWriteTimeUtc( absolute ) )
				return false;

			var text = File.ReadAllText( absolute );

			return text.Contains( "\"Graph\"", StringComparison.OrdinalIgnoreCase )
				|| LegacyGraph.IsMatch( text );
		}
		catch ( IOException )
		{
			return false;
		}
	}

	//A definition from before a model carried its graph, naming a graph file.
	private static readonly System.Text.RegularExpressions.Regex LegacyGraph = new(
		"\"AnimationGraph\"\\s*:\\s*\"[^\"]+\"",
		System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled );

	/// <summary>Writes the definition and, if the model was current, bumps its date so it stays current</summary>
	public override bool SaveToDiskUnbuilt( GameResource obj )
	{
		if ( !IsModel ) return base.SaveToDiskUnbuilt( obj );

		var compiled = Rinten.CompiledPaths.Model( absolute );

		bool current;

		try
		{
			current = File.Exists( compiled ) && File.Exists( absolute )
				&& File.GetLastWriteTimeUtc( compiled ) >= File.GetLastWriteTimeUtc( absolute );
		}
		catch ( IOException )
		{
			current = false;
		}

		if ( !SaveToDisk( obj, compile: false ) ) return false;

		Rinten.ResourceReload.Unchanged( absolute );

		if ( !current ) return true;

		try
		{
			File.SetLastWriteTimeUtc( compiled, File.GetLastWriteTimeUtc( absolute ) );
			Rinten.ResourceReload.Unchanged( compiled );
		}
		catch ( IOException )
		{
			//Left older than the definition: built again the next time anything asks, which is what would have happened without this.
		}

		return true;
	}

	//Whether the compiled file beside this one is missing or older than it. Dates alone, which is all this
	//can ask: what builds the file is somebody else's code, and only it knows what else the answer depends on.
	private bool NeedsBuild()
	{
		try
		{
			var compiled = Rinten.CompiledPaths.Built( absolute );

			if ( !File.Exists( compiled ) ) return true;

			return File.GetLastWriteTimeUtc( compiled ) < File.GetLastWriteTimeUtc( absolute );
		}
		catch ( IOException )
		{
			return true;
		}
	}

	//Whether the model beside this definition is older than what it is built from - the definition itself, or the art it names.
	private bool NeedsModelBuild()
	{
		try
		{
			var compiled = Rinten.CompiledPaths.Model( absolute );
			if ( !File.Exists( compiled ) ) return true;

			//Built by an older set of importers - which no date can show, since changing an importer touches neither the definition nor the art.
			if ( !Rinten.ModelDefinitionCompiler.BuiltByThisImporter( absolute ) )
				return true;

			var built = File.GetLastWriteTimeUtc( compiled );

			if ( File.GetLastWriteTimeUtc( absolute ) > built )
				return true;

			//What it is built from too - every export it names, and whatever else its last build read - so saving any of them over is enough.
			return !Rinten.ModelInputs.IsCurrent( absolute, built );
		}
		catch ( IOException )
		{
			//Can't tell - building it again is the answer that is never wrong
			return true;
		}
	}

	public override Model GetPreviewModel()
	{
		if ( AssetType == AssetType.Model ) return Model.Load( RelativePath );
		if ( AssetType == AssetType.Mesh ) return MeshPreview();

		return null;
	}

	//Builds a cached model from just this mesh export so it can be previewed without a definition
	private Model MeshPreview()
	{
		var built = BuildMeshPreview();

		return built is null ? null : Model.Load( built );
	}

	//The compiled preview for this export, built if it is not there yet.
	private string BuildMeshPreview()
	{
		if ( !File.Exists( absolute ) ) return null;

		string stamp;

		try
		{
			stamp = File.GetLastWriteTimeUtc( absolute ).Ticks.ToString( "x" );
		}
		catch ( IOException )
		{
			return null;
		}

		//And which build of the importers made it: the same bytes read by a newer importer are a different model
		//- one that has its art's colours, say - and a preview built before would otherwise be the answer forever.
		var name = $"{System.IO.Path.GetFileNameWithoutExtension( absolute )}.{Hashed( absolute )}.{stamp}.{Rinten.CoreAssets.ModelFile.ImportGeneration}.mdl";

		FileSystem.ProjectTemporary.CreateDirectory( MeshPreviewFolder );

		var definitionPath = FileSystem.ProjectTemporary.GetFullPath( $"{MeshPreviewFolder}/{name}" );
		if ( string.IsNullOrWhiteSpace( definitionPath ) ) return null;

		//Built once. The name carries the export's write time, so this is only ever the same file for
		//the same bytes - and compiling a mesh is the expensive half of drawing a folder full of them.
		if ( File.Exists( Rinten.CompiledPaths.Model( definitionPath ) ) )
			return definitionPath;

		var definition = new ModelDefinition();
		definition.Meshes.Add( new ModelDefinition.MeshPart { File = absolute } );

		return Rinten.ModelDefinitionCompiler.Compile( definition, definitionPath )
			? definitionPath
			: null;
	}

	//Where the previews built from exports are kept.
	private const string MeshPreviewFolder = "/meshpreviews";

	//A path as a short word, so two exports with the same file name do not share a preview.
	private static string Hashed( string path )
	{
		var hash = System.Security.Cryptography.MD5.HashData(
			System.Text.Encoding.UTF8.GetBytes( path.ToLowerInvariant() ) );

		return Convert.ToHexString( hash, 0, 4 ).ToLowerInvariant();
	}

	public override void RecordOpened()
	{
		LastOpened = DateTime.Now;
	}

	public override bool IsCompiled => true;

	/// <summary>True of everything but a model out of date with what it is built from.</summary>
	public override bool IsCompiledAndUpToDate => IsModel
		? !NeedsModelBuild() && !( Built && NeedsGraphBuild() )
		: IsSound
			? !SoundBake.IsStale( this, absolute )
			: IsPicture
				? !PictureBake.IsStale( absolute )
				: !Built || !NeedsBuild();
	public override bool IsCompileFailed => false;

	public override ValueTask<bool> CompileIfNeededAsync( float timeout = 30.0f ) => ValueTask.FromResult( false );

	public override bool HasSourceFile => File.Exists( absolute );
	public override bool HasCompiledFile => HasSourceFile;

	public override bool SetInMemoryReplacement( string sourceData ) => false;
	public override void ClearInMemoryReplacement() { }
}
