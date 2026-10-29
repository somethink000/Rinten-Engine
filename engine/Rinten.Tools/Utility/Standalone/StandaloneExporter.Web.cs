using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

namespace Editor;

//Web export: a flat folder with every asset, a content list and a manifest; HTTP can't list dirs
partial class StandaloneExporter
{
	//The page and the scripts it loads before the game (the .NET runtime is loaded separately)
	private static readonly string[] PlayerFiles = ["index.html", "native.js", "webgpu.js", "webgl2.js", "webxr.js",
		"basis.js", "basis_worker.js", "basis_transcoder.js", "basis_transcoder.wasm", "basis_transcoder.NOTICE.txt"];

	//Where a web build's files come from, under the engine.
	private string PlayerFolder => Path.Combine( Environment.CurrentDirectory, ExportPlatform.Web.BinFolder );

	//The base addon's content, which every game draws with.
	private static string BaseAssets => BaseAssetsFolder( Environment.CurrentDirectory );

	//Writes the whole web build: the player, the content, the code, the lists.
	private async Task RunWeb()
	{
		var target = _exportConfig.TargetDir;

		//The module is the last step and the longest wait, so what would stop it is asked first
		if ( !await WebModuleCanBuild() ) return;

		Directory.CreateDirectory( target );

		Logger.Info( "Copying the player.." );
		CopyPlayer( target );

		//A material's pictures as Basis blocks in their place, which the page turns into what its device reads - see CompilePictures.
		Logger.Info( "Compiling pictures.." );
		await CompilePictures();

		Logger.Info( "Copying content.." );
		var content = new SortedSet<string>( StringComparer.OrdinalIgnoreCase );

		CopyProjectAssets( target, content );
		CopyEngineContent( target, content );

		//Font index so the page fetches only the faces its text uses, on demand
		if ( FontIndex.Write( Path.Combine( target, "fonts" ) ) )
		{
			var faces = content.Where( x => x.StartsWith( "fonts/", StringComparison.OrdinalIgnoreCase )
				&& ( x.EndsWith( ".ttf", StringComparison.OrdinalIgnoreCase ) || x.EndsWith( ".otf", StringComparison.OrdinalIgnoreCase ) ) ).ToList();

			foreach ( var face in faces )
			{
				content.Remove( face );
				content.Add( $"{WebBuild.OnDemand}{face}" );
			}

			content.Add( $"fonts/{FontIndex.FileName}" );
		}

		//A model that ships compiled needs its mesh file only to be compiled again, which a page cannot do - and the controllers a
		//headset shows are fetched when the headset says which it holds, not all eight before the start. See XrControllerModels.For.
		foreach ( var compiled in content.Where( x => x.EndsWith( ".mdl_c", StringComparison.OrdinalIgnoreCase ) ).ToList() )
		{
			var folder = compiled[..( compiled.LastIndexOf( '/' ) + 1 )];
			content.RemoveWhere( x => x.StartsWith( folder, StringComparison.OrdinalIgnoreCase ) && x.IndexOf( '/', folder.Length ) < 0
				&& ( x.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) || x.EndsWith( ".obj", StringComparison.OrdinalIgnoreCase ) ) );
		}

		foreach ( var file in content.Where( x => x.StartsWith( $"{XrControllerModels.Folder}/", StringComparison.OrdinalIgnoreCase ) ).ToList() )
		{
			content.Remove( file );
			content.Add( $"{WebBuild.OnDemand}{file}" );
		}

		Logger.Info( "Compiling shaders.." );
		BuildShaders( target, content );

		//Where materials load and scenes draw, which the bake does both of.
		await OnMainThread( () => BakeWebShaders( target, content ) );

		Logger.Info( "Copying code.." );
		var assemblies = CopyAssemblies( target );

		//The engine, the base and the game's code as one module - see CompileWebModule.
		if ( !await CompileWebModule( target ) ) return;

		//And packed beside itself, with its hash - see PackModule.
		Logger.Info( "Packing the module.." );
		var module = await Task.Run( () => PackModule( target ) );

		//The heavy files only other scenes use fetched when those scenes load, and the big ones packed - see PackAndDefer.
		Logger.Info( "Packing content.." );
		var listed = await Task.Run( () => PackAndDefer( target, content ) );

		WriteAssetList( target, listed );
		WriteWebManifest( target, assemblies, module );

		//GitHub Pages runs Jekyll over what it is given, and Jekyll leaves out every folder whose name
		//begins with an underscore - which is the .NET runtime, all of it. One empty file turns that off.
		File.WriteAllBytes( Path.Combine( target, ".nojekyll" ), [] );

		if ( _exportConfig.CreateZip )
		{
			Logger.Info( "Zipping.." );
			await Task.Run( () => Zip( target, listed ) );
		}

		Logger.Info( $"Web build written to {target}" );
	}

	//The engine's own passes - the panels, the lines, the sky's background - and every feature combination the materials chose, none of
	//which a page has a compiler to build. See CoreShaderCache.WriteForWeb, and BakeSurfaces, which is what a desktop build bakes them with.
	private void BakeWebShaders( string target, SortedSet<string> content )
	{
		var cache = Path.Combine( target, CoreShaderCache.FileName.Replace( '/', Path.DirectorySeparatorChar ) );
		var assets = _exportConfig.Project.GetAssetsPath();

		//The runs of point effects the scenes lay, which a page draws carried by the world's own shading or
		//not at all - found by drawing a frame of each scene, as a desktop build finds them. See BakeShaders.
		CorePointChain.Recording = true;

		void Surfaces()
		{
			var materials = 0;
			var shaders = 0;
			BakeSurfaces( assets, portable: true, ref materials, ref shaders );
			CorePointChain.BakeScreens( bakedSurfaces );
		}

		try
		{
			var scenes = BakeSceneChains( assets );
			Logger.Info( $"point effects found in {scenes} scene(s)" );

			if ( CoreShaderCache.WriteForWeb( cache, Surfaces, stereo: PlaysInHeadset() ) )
				content.Add( CoreShaderCache.FileName );
			else
				Logger.Warning( "the engine's own passes did not all compile for the web - see the log" );
		}
		finally
		{
			CorePointChain.Recording = false;
		}
	}

	//The page and the scripts it loads beside the module: the canvas and input (native.js), the two renderers' handle tables, the headset's, the
	//picture transcoder. The page itself comes with the module's launcher, which it boots; the scripts out of the folder the core's own build writes.
	private void CopyPlayer( string target )
	{
		var from = PlayerFolder;

		if ( !Directory.Exists( from ) )
			throw new Exception( $"there is no web player in {from} - run engine/Core/build-web.sh first" );

		foreach ( var name in PlayerFiles )
		{
			var source = name == "index.html"
				? Path.Combine( Path.GetDirectoryName( WebModuleProject ), "wwwroot", "index.html" )
				: Path.Combine( from, name );

			if ( !File.Exists( source ) )
			{
				if ( name == "index.html" ) throw new Exception( $"there is no page at {source}" );

				Logger.Warning( $"the web player has no {name}" );
				continue;
			}

			File.Copy( source, Path.Combine( target, name ), true );
		}
	}

	//Everything the asset walk found in the project, at the path the engine will ask for it under.
	private void CopyProjectAssets( string target, ISet<string> content )
	{
		foreach ( var file in PackageManifest.Assets )
		{
			if ( file.Skip ) continue;

			var relative = file.Name.Replace( '\\', '/' ).TrimStart( '/' );

			//The editor's own sidecars stay behind here too - see ForGames.
			if ( !ForGames( relative ) ) continue;

			if ( !WriteContent( target, relative, file.AbsolutePath, file.Contents ) ) continue;

			content.Add( relative );
		}
	}

	//Engine content a page needs, flattened into one namespace, as both roots mount at the asset root
	private void CopyEngineContent( string target, ISet<string> content )
	{
		var engineDir = Environment.CurrentDirectory;

		foreach ( var file in BaseContent( engineDir ).SelectMany( ShippedAs ) )
		{
			var relative = Path.GetRelativePath( BaseAssets, file ).Replace( '\\', '/' );
			if ( !WriteContent( target, relative, file, null ) ) continue;

			content.Add( relative );
		}

		if ( OwnSplash() is { } own && WriteContent( target, SplashInBuild(), own, null ) )
			content.Add( SplashInBuild() );

		var core = Path.Combine( engineDir, "core" );

		foreach ( var file in GetCoreFiles( engineDir ) )
		{
			var relative = Path.GetRelativePath( core, file ).Replace( '\\', '/' );

			//The engine's shaders compiled for a desktop, which a page cannot run: BuildShaders makes what it can.
			if ( string.Equals( "core/" + relative, CoreShaderCache.FileName, StringComparison.OrdinalIgnoreCase ) ) continue;

			if ( !WriteContent( target, relative, file, null ) ) continue;

			content.Add( relative );
		}
	}

	//One content file into the build. False where there was nothing to write.
	private bool WriteContent( string target, string relative, string source, byte[] contents )
	{
		var into = Path.Combine( target, relative.Replace( '/', Path.DirectorySeparatorChar ) );

		Directory.CreateDirectory( Path.GetDirectoryName( into ) );

		if ( contents is not null )
		{
			File.WriteAllBytes( into, contents );
			return true;
		}

		if ( string.IsNullOrEmpty( source ) || !File.Exists( source ) )
		{
			Logger.Warning( $"{relative} is not there - the build is without it" );
			return false;
		}

		File.Copy( source, into, true );
		return true;
	}

	//The shaders the engine itself draws a frame with, whatever the game says.
	private static readonly string[] EngineShaders = [
		MaterialDefinition.StandardShader,
		"shaders/engine/sky.shader",
	];

	//Compiles every reachable shader to WGSL and GLSL; the browser picks WebGPU or WebGL2 at runtime
	private void BuildShaders( string target, ISet<string> content )
	{
		var wanted = new SortedSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var shader in EngineShaders )
			wanted.Add( shader );

		foreach ( var named in ShadersNamedByMaterials( target, content ) )
			wanted.Add( named );

		//Anything of the project's own that is already a shader, whether it was shipped as source or as the compiled file beside it.
		foreach ( var relative in content )
		{
			if ( relative.EndsWith( ".shader", StringComparison.OrdinalIgnoreCase ) )
				wanted.Add( relative );
			else if ( relative.EndsWith( ".shader_c", StringComparison.OrdinalIgnoreCase ) )
				wanted.Add( relative[..^2] );
		}

		//Under the name the page will ask for: a legacy name a material still says - shaders/unlit.shader, standard.shader - is looked up as the file it
		//stands for (CoreShaderFile.Real), and compiled under its own name it was a source nobody has, a warning, and the file it meant left out of the build.
		wanted = new SortedSet<string>( wanted.Select( CoreShaderFile.Real ), StringComparer.OrdinalIgnoreCase );

		//Post passes need the post library's context, which only WriteForWeb compiles them with (BakeWebShaders)
		wanted.RemoveWhere( IsPostPass );

		var written = 0;
		var missing = new List<string>();

		foreach ( var shader in wanted )
		{
			if ( WriteShader( target, shader ) )
			{
				content.Add( $"{shader}_c" );
				written++;
				continue;
			}

			missing.Add( shader );
		}

		Logger.Info( $"{written} shader(s) ready for the web" );

		foreach ( var shader in missing )
			Logger.Warning( $"{shader} could not be compiled for a browser - anything drawn with it will not draw" );
	}

	//One shader, as a compiled file a page can read. False where it could not be made into one.
	private bool WriteShader( string target, string shader )
	{
		var compiled = $"{shader}_c";
		var into = Path.Combine( target, compiled.Replace( '/', Path.DirectorySeparatorChar ) );

		var existing = ReadShader( target, compiled ) ?? ReadShader( SourceFolderOf( shader ), Path.GetFileName( compiled ) );
		var source = SourceOf( shader );

		//The three languages side by side: each is its stages on the compile threads, and none waits on another - see CoreShaderThreads.
		var wgslMade = Language( existing, source, CoreShaderCompiler.Format.Wgsl );
		var glslMade = Language( existing, source, CoreShaderCompiler.Format.Glsl );

		//Stereo GLSL for one-pass headset rendering, for any game that lists VR among its control modes
		var stereoMade = PlaysInHeadset()
			? Language( existing, source, CoreShaderCompiler.Format.GlslStereo )
			: Task.FromResult<(byte[][] Stages, string Error)>( (null, null) );

		var (wgsl, _) = wgslMade.Result;
		var (glsl, glslError) = glslMade.Result;
		var (stereo, _) = stereoMade.Result;

		if ( wgsl is null && glsl is null ) return false;

		if ( glsl is null )
			Logger.Warning( $"{shader} has no GLSL ({glslError}) - a browser without WebGPU will not draw with it" );

		Directory.CreateDirectory( Path.GetDirectoryName( into ) );
		File.WriteAllBytes( into, CoreShaderFile.Write( new byte[CoreShaderFile.StageCount][], wgsl, null, glsl, stereo ) );
		return true;
	}

	//A shader's stages in one language, compiled from source when present (cached), else from the file
	private static Task<(byte[][] Stages, string Error)> Language( byte[] existing, string source, CoreShaderCompiler.Format format )
		=> Task.Run( () =>
		{
			var stages = Stages( existing, source, format );
			return (stages, stages is null ? CoreRenderDevice.ShaderError : null);
		} );

	private static byte[][] Stages( byte[] existing, string source, CoreShaderCompiler.Format format )
	{
		if ( source is not null && CoreShaderFile.Compile( source, format ) is { } compiled )
			return compiled;

		if ( Draws( existing, format ) )
		{
			var stages = new byte[CoreShaderFile.StageCount][];

			for ( var i = 0; i < CoreShaderFile.StageCount; i++ )
				stages[i] = CoreShaderFile.Read( existing, (CoreShaderFile.Stage)i, format );

			return stages;
		}

		return null;
	}

	//A compiled shader that is already somewhere, or null.
	private static byte[] ReadShader( string folder, string relative )
	{
		if ( folder is null ) return null;

		var path = Path.Combine( folder, relative.Replace( '/', Path.DirectorySeparatorChar ) );
		if ( !File.Exists( path ) ) return null;

		try { return File.ReadAllBytes( path ); }
		catch ( IOException ) { return null; }
	}

	//Where a shader is written, in the project or in the base addon.
	private static bool IsPostPass( string shader )
	{
		try
		{
			return SourceOf( shader ) is { } file && File.ReadAllText( file ).Contains( "#include \"postprocess.slang\"" );
		}
		catch ( IOException )
		{
			return false;
		}
	}

	private static string SourceOf( string shader )
	{
		if ( AssetSystem.FindByPath( shader )?.AbsolutePath is string asset && File.Exists( asset ) )
			return asset;

		var shipped = Path.Combine( BaseAssets, shader.Replace( '/', Path.DirectorySeparatorChar ) );

		return File.Exists( shipped ) ? shipped : null;
	}

	//Which folder a shader's compiled file would already be in.
	private static string SourceFolderOf( string shader )
	{
		var source = SourceOf( shader );

		return source is null ? null : Path.GetDirectoryName( source );
	}

	//Whether a compiled shader has anything to draw with in a language.
	private static bool Draws( byte[] compiled, CoreShaderCompiler.Format format )
		=> compiled is not null
			&& CoreShaderFile.Read( compiled, CoreShaderFile.Stage.Vertex, format ) is not null
			&& CoreShaderFile.Read( compiled, CoreShaderFile.Stage.Pixel, format ) is not null;

	//Every shader the build's own materials name. A material is a resource the asset walk follows; the shader it names is not - it is a path in a json field,
	//and nothing in the project references the file. Read here rather than resolved at runtime, because at runtime there is nothing to resolve it with.
	private IEnumerable<string> ShadersNamedByMaterials( string target, IEnumerable<string> content )
	{
		foreach ( var entry in content )
		{
			//A file fetched on demand - a headset controller's materials - is still a file of the build: the mark is for the page, not part of its path.
			var relative = entry.TrimStart( WebBuild.OnDemand );
			if ( !relative.EndsWith( ".mat", StringComparison.OrdinalIgnoreCase ) ) continue;

			var path = Path.Combine( target, relative.Replace( '/', Path.DirectorySeparatorChar ) );

			string named = null;

			try
			{
				using var document = JsonDocument.Parse( File.ReadAllText( path ) );

				if ( document.RootElement.ValueKind == JsonValueKind.Object
					&& document.RootElement.TryGetProperty( nameof( MaterialDefinition.Shader ), out var shader )
					&& shader.ValueKind == JsonValueKind.String )
				{
					named = shader.GetString();
				}
			}
			catch ( Exception e )
			{
				Logger.Warning( $"{relative} did not read: {e.GetType().Name}: {e.Message}" );
			}

			if ( !string.IsNullOrWhiteSpace( named ) )
				yield return named.Replace( '\\', '/' ).TrimStart( '/' );
		}
	}

	//The game's compiled assemblies, and what to name them in the manifest.
	private List<string> CopyAssemblies( string target )
	{
		var written = new List<string>();

		if ( _exportConfig.AssemblyFiles is null ) return written;

		var into = Path.Combine( target, WebBuild.AssemblyFolder );
		Directory.CreateDirectory( into );

		foreach ( var file in _exportConfig.AssemblyFiles )
		{
			if ( !file.Key.EndsWith( ".dll", StringComparison.OrdinalIgnoreCase ) ) continue;
			if ( file.Value is not byte[] bytes ) continue;

			var name = Path.GetFileName( file.Key );

			File.WriteAllBytes( Path.Combine( into, name ), bytes );
			written.Add( $"{WebBuild.AssemblyFolder}/{name}" );
		}

		return written;
	}

	//Files under this are never packed: their pictures, sounds and fonts are compressed already.
	private static readonly string[] PackedAlready = [".ktx2", ".png", ".jpg", ".jpeg", ".webp", ".ogg", ".mp3", ".ttf", ".otf", ".woff", ".woff2"];

	//Writes content lists, deferring models only non-start scenes use, and gzips large files that shrink
	private List<string> PackAndDefer( string target, IEnumerable<string> content )
	{
		var start = ( _exportConfig.Project.Config.GetMetaOrDefault<string>( "StartupScene", null ) ?? "" ).Replace( '\\', '/' ).TrimStart( '/' );
		var entries = content.ToList();
		static string Plain( string entry ) => entry.TrimStart( WebBuild.OnDemand );

		//The text of everything that may name a model.
		var texts = new List<(string Path, string Text)>();

		foreach ( var entry in entries )
		{
			var path = Plain( entry );
			var full = Path.Combine( target, path );
			if ( !File.Exists( full ) ) continue;

			var extension = Path.GetExtension( path ).ToLowerInvariant();
			if ( extension is ".mdl_c" or ".shader_c" or ".shadercache" or ".wav" or ".bc" or ".tex_c" || PackedAlready.Contains( extension ) ) continue;
			if ( new FileInfo( full ).Length > 32 << 20 ) continue;

			texts.Add( (path, File.ReadAllText( full )) );
		}

		var bySceneLines = new StringBuilder();
		var deferred = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var entry in entries )
		{
			var path = Plain( entry );
			if ( !path.EndsWith( ".mdl_c", StringComparison.OrdinalIgnoreCase ) ) continue;

			//Named as the resource: the compiled file is the source's name and "_c".
			var named = path[..^2];
			var by = texts.Where( x => x.Text.Contains( named, StringComparison.OrdinalIgnoreCase ) ).Select( x => x.Path ).ToList();

			if ( by.Count == 0 ) continue;
			if ( by.Any( x => !x.EndsWith( ".scene", StringComparison.OrdinalIgnoreCase ) || string.Equals( x, start, StringComparison.OrdinalIgnoreCase ) ) ) continue;

			deferred.Add( path );
			foreach ( var scene in by ) bySceneLines.Append( scene ).Append( '\t' ).Append( path ).Append( '\n' );
		}

		File.WriteAllText( Path.Combine( target, WebBuild.SceneListName ), bySceneLines.ToString() );

		var listed = new List<string>( entries.Count );
		long packedFrom = 0, packedTo = 0;

		foreach ( var entry in entries )
		{
			var path = Plain( entry );
			var full = Path.Combine( target, path );
			var lazy = entry.StartsWith( WebBuild.OnDemand ) || deferred.Contains( path );
			var packed = false;

			if ( File.Exists( full ) && !PackedAlready.Contains( Path.GetExtension( path ).ToLowerInvariant() ) && new FileInfo( full ).Length >= 256 << 10 )
			{
				var raw = File.ReadAllBytes( full );
				using var into = new MemoryStream();

				using ( var gzip = new System.IO.Compression.GZipStream( into, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true ) )
					gzip.Write( raw );

				if ( into.Length <= raw.Length * 4 / 5 )
				{
					File.WriteAllBytes( full + ".gz", into.ToArray() );
					File.Delete( full );
					packed = true;
					packedFrom += raw.Length;
					packedTo += into.Length;
				}
			}

			listed.Add( $"{( lazy ? WebBuild.OnDemand.ToString() : "" )}{( packed ? WebBuild.Packed.ToString() : "" )}{path}" );
		}

		Logger.Info( $"web content: {deferred.Count} models fetched with their scenes, {packedFrom / 1048576.0:F0} MB packed into {packedTo / 1048576.0:F0} MB" );

		return listed;
	}

	//Every content file in the build, one per line, which is how the page knows what to fetch.
	private static void WriteAssetList( string target, IEnumerable<string> content )
	{
		var list = new StringBuilder();

		foreach ( var path in content )
			list.Append( path ).Append( '\n' );

		File.WriteAllText( Path.Combine( target, WebBuild.AssetListName ), list.ToString() );
	}

	//Writes gzip and brotli copies of the module plus its hash, since hosts may not compress on the fly
	private string PackModule( string target )
	{
		var wasm = Path.Combine( target, "_framework", "dotnet.native.wasm" );
		if ( !File.Exists( wasm ) ) return "";

		string hash;

		using ( var raw = File.OpenRead( wasm ) )
			hash = Convert.ToHexString( System.Security.Cryptography.SHA256.HashData( raw ) )[..16].ToLowerInvariant();

		//Placed packed with the module where it was kept (PlaceModule): the same module packs the same, and quality eleven is three minutes an export.
		if ( File.Exists( wasm + ".gz" ) && File.Exists( wasm + ".br" ) ) return hash;

		using ( var raw = File.OpenRead( wasm ) )
		using ( var packed = File.Create( wasm + ".gz" ) )
		using ( var gzip = new System.IO.Compression.GZipStream( packed, System.IO.Compression.CompressionLevel.SmallestSize ) )
			raw.CopyTo( gzip );

		//The most brotli has - quality eleven, the widest window: three minutes of an export against
		//every player's download, which it takes from 22.5 MB (quality nine) to 18.4 for a module of 114.
		var bytes = File.ReadAllBytes( wasm );
		var room = new byte[bytes.Length + 1024];

		if ( System.IO.Compression.BrotliEncoder.TryCompress( bytes, room, out var written, 11, 24 ) )
			File.WriteAllBytes( wasm + ".br", room.AsSpan( 0, written ).ToArray() );
		else
			File.Delete( wasm + ".br" );

		//And kept with it, for the next export of the same code.
		if ( keptModule is not null && Directory.Exists( keptModule ) )
			foreach ( var name in PackedModuleFiles )
				if ( File.Exists( Path.Combine( target, "_framework", name ) ) )
					File.Copy( Path.Combine( target, "_framework", name ), Path.Combine( keptModule, name ), true );

		return hash;
	}

	private void WriteWebManifest( string target, List<string> assemblies, string module = "" )
	{
		var project = _exportConfig.Project;

		var manifest = new WebManifest
		{
			Name = project.Config.Title,
			Ident = project.Package?.Ident ?? project.Config.Ident,
			StartScene = project.Config.GetMetaOrDefault<string>( "StartupScene", null ),
			Assemblies = assemblies,
			Code = CodeFingerprints(),
			Module = module,
			BuildDate = _exportConfig.BuildDate,

			//A project played in a headset says so here, and index.html reads it before it makes the graphics context - see WebManifest.Vr.
			Vr = IsVr(),

			//See ExportConfig.ExportDebug.
			Debug = _exportConfig.ExportDebug,
			Splash = SplashInBuild() is { } splash ? "/" + splash : null,
		};

		if ( string.IsNullOrWhiteSpace( manifest.StartScene ) )
			Logger.Warning( "this project names no StartupScene - the build will start nothing" );

		File.WriteAllText( Path.Combine( target, WebBuild.ManifestName ),
			JsonSerializer.Serialize( manifest, new JsonSerializerOptions { WriteIndented = true } ) );
	}

	//What code the module is compiled from, by assembly, as a server may send it: the sources the export compiled, and the same
	//without the server's code, which is what a dedicated server sends - see WebCode.Refuses, which a page joining checks.
	private Dictionary<string, List<string>> CodeFingerprints()
	{
		var code = new Dictionary<string, List<string>>( StringComparer.OrdinalIgnoreCase );

		if ( _exportConfig.AssemblyFiles is null ) return code;

		foreach ( var file in _exportConfig.AssemblyFiles )
		{
			if ( !file.Key.EndsWith( ".cll", StringComparison.OrdinalIgnoreCase ) || file.Value is not byte[] archive ) continue;

			var name = Path.GetFileNameWithoutExtension( file.Key );
			var known = new List<string>();

			foreach ( var each in new[] { archive, Rinten.ProjectCompiling.Current?.WithoutServerCode( name ) } )
			{
				if ( each is null ) continue;

				try
				{
					var print = Rinten.WebCode.Fingerprint( each );
					if ( !known.Contains( print ) ) known.Add( print );
				}
				catch ( Exception e )
				{
					Logger.Warning( $"{name}: its code archive could not be read for the web build's fingerprint - {e.Message}" );
				}
			}

			code[name] = known;
		}

		return code;
	}

	//Zips the build beside the folder for itch.io upload, with only what the page fetches, module packed
	private void Zip( string target, IEnumerable<string> listed )
	{
		var name = string.IsNullOrWhiteSpace( _exportConfig.ExecutableName ) ? "game" : _exportConfig.ExecutableName;
		var parent = Path.GetDirectoryName( Path.GetFullPath( target ) ) ?? target;
		var zip = Path.Combine( parent, $"{name}-web.zip" );

		var files = new List<string>( PlayerFiles ) { WebBuild.ManifestName, WebBuild.AssetListName, WebBuild.SceneListName };

		foreach ( var file in ModuleFiles.Concat( PackedModuleFiles ) )
			files.Add( $"_framework/{file}" );

		//The raw module only where there is no packed one to open instead.
		if ( File.Exists( Path.Combine( target, "_framework", "dotnet.native.wasm.gz" ) ) )
			files.Remove( "_framework/dotnet.native.wasm" );

		foreach ( var entry in listed )
		{
			var path = entry.TrimStart( WebBuild.OnDemand );
			files.Add( path.StartsWith( WebBuild.Packed ) ? $"{path[1..]}.gz" : path );
		}

		if ( File.Exists( zip ) ) File.Delete( zip );

		using ( var archive = ZipFile.Open( zip, ZipArchiveMode.Create ) )
		{
			foreach ( var file in files.Distinct( StringComparer.Ordinal ) )
			{
				var full = Path.Combine( target, file.Replace( '/', Path.DirectorySeparatorChar ) );
				if ( !File.Exists( full ) ) continue;

				var extension = Path.GetExtension( file ).ToLowerInvariant();
				var stored = extension is ".gz" or ".br" || PackedAlready.Contains( extension );

				archive.CreateEntryFromFile( full, file, stored ? CompressionLevel.NoCompression : CompressionLevel.SmallestSize );
			}
		}

		Logger.Info( $"Upload {zip} to itch.io ({new FileInfo( zip ).Length / 1048576.0:F1} MB)" );
	}
}
