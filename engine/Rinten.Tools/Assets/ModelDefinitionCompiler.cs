using Rinten.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Rinten;

/// <summary>Compiles a .mdl definition into our model format, importing the mesh it names</summary>
[Expose]
[ResourceIdentity( "mdl" )]
internal class ModelDefinitionCompiler : ResourceCompiler
{
	protected override Task<bool> Compile()
	{
		var path = Path.GetFullPath( Context.AbsolutePath );

		if ( !ReadDefinition( path, out var read ) )
			return Task.FromResult( false );

		//Nothing to build from. A ported model keeps its geometry in the compiled file beside it and names no
		//mesh, so this is not a failure - it is a model that was already compiled once and should be left alone.
		if ( read.Meshes.Count == 0 )
			return Task.FromResult( Keep( path, read.Definition?.LodGroup ) );

		var sources = new List<Source>();

		foreach ( var part in read.Meshes )
		{
			if ( !part.Enabled ) continue;

			var source = Resolve( path, part.File );

			if ( source is null )
			{
				Log.Warning( $"{Context.AbsolutePath}: cannot find its mesh, {part.File}" );
				continue;
			}

			Context.AddCompileReference( source );
			sources.Add( new Source( source, part.Scale, part.Position, part.Rotation, part.Parent,
				part.Name, part.Include ) );
		}

		sources = WithoutLevelsOfDetail( WithoutHullsOnly( sources, read.Definition, path ), path );

		if ( sources.Count == 0 )
			return Task.FromResult( Keep( path, read.Definition?.LodGroup ) );

		var bytes = CompileMeshes( sources, read.Materials, read.Definition, path );

		if ( bytes is null )
		{
			Log.Warning( $"{Context.AbsolutePath}: nothing our importers could read in the named meshes" );
			return Task.FromResult( false );
		}

		//Definition text goes in the data block (for the editor), the model in the streaming block
		Context.Data.Write( read.Json );
		Context.StreamingData.Write( bytes );

		//The skeleton that was read out of the model before this is the old one's, and it is
		//remembered by path - so a bone list, a graph or anything posed by them would go on using it.
		CoreSkeletons.Forget( path );
		CompileClips( path, read.Definition );

		return Task.FromResult( true );
	}

	//Builds the model beside a definition and writes it directly, without the native resource compiler
	internal static bool CompileToFile( string definitionPath )
	{
		if ( string.IsNullOrWhiteSpace( definitionPath ) || !File.Exists( definitionPath ) )
			return false;

		var path = Path.GetFullPath( definitionPath );
		return ReadDefinition( path, out var read ) && Compile( read.Definition, path );
	}

	//The definition in a .mdl, read off the disk the way the compiler reads it. Not through the resource
	//system, which keeps one resource a path: loading the definition there would put it where the model is.
	internal static ModelDefinition ReadDefinitionOnly( string definitionPath )
	{
		if ( string.IsNullOrWhiteSpace( definitionPath ) || !File.Exists( definitionPath ) ) return null;

		return ReadDefinition( Path.GetFullPath( definitionPath ), out var read ) ? read.Definition : null;
	}

	//Build definition to the .mdl_c beside it. Writing down every file the build reads
	//as it goes, which is what says later whether it is out of date - see ModelInputs.
	internal static bool Compile( ModelDefinition definition, string definitionPath )
	{
		if ( definition is null || string.IsNullOrWhiteSpace( definitionPath ) )
			return false;

		using var reads = ImportReads.Record();

		if ( !CompileRead( definition, definitionPath ) )
			return false;

		ModelInputs.Save( definitionPath, reads );
		return true;
	}

	private static bool CompileRead( ModelDefinition definition, string definitionPath )
	{
		var path = Path.GetFullPath( definitionPath );
		var sources = new List<Source>();

		foreach ( var part in definition.AllMeshes() )
		{
			if ( part is null || !part.Enabled || string.IsNullOrWhiteSpace( part.File ) )
				continue;

			var source = Resolve( path, part.File );
			if ( source is null )
			{
				Log.Warning( $"{path}: cannot find its mesh, {part.File}" );
				continue;
			}

			sources.Add( new Source( source, part.Scale, part.Position, part.Rotation, part.Parent,
				part.Name, part.Include ) );
		}

		sources = WithoutLevelsOfDetail( WithoutHullsOnly( sources, definition, path ), path );

		if ( sources.Count == 0 )
			return CompileBaked( definition, path );

		var bytes = CompileMeshes( sources, definition.Materials, definition, path );
		if ( bytes is null )
		{
			Log.Warning( $"{path}: nothing our importers could read in the named meshes" );
			return false;
		}

		try
		{
			var output = CompiledPaths.Model( path );

			File.WriteAllBytes( output, bytes );

			CoreSkeletons.Forget( path );
			CompileClips( path, definition );
			return true;
		}
		catch ( IOException e )
		{
			Log.Warning( $"{path}: could not write the model beside it - {e.Message}" );
			return false;
		}
	}

	//Keep baked geometry and rewrite collision from the definition.
	static bool CompileBaked( ModelDefinition definition, string path )
	{
		if ( !CoreNative.Available )
			return false;

		var output = CompiledPaths.Model( path );
		var baseline = ModelBakeBaseline.PathFor( output );

		//The untransformed state this builds from - the baseline sidecar once one exists, or whatever is on disk
		//the very first time, since nothing has ever baked a Transform into it before now. See ModelBakeBaseline.
		var source = File.Exists( baseline ) ? baseline : output;
		if ( !File.Exists( source ) )
			return false;

		var existing = CoreModel.FromBytes( File.ReadAllBytes( source ), path );
		if ( existing is not { IsValid: true } )
			return false;

		var builder = new CoreAssets.ModelBuilder();
		if ( builder is null )
		{
			existing.Dispose();
			return false;
		}

		try
		{
			try
			{
				if ( !existing.CopyGeometry( builder ) )
					return false;

				builder.SetLodGroup( definition.LodGroup );

				KeepIncludedMeshes( builder, definition, path );
				var count = builder.MeshCount;
				LayMaterials( builder, count, definition.Materials, definition );
				WriteGroups( builder, count, definition, null );
				LayClipSkeleton( builder, definition, path );

				//Lightmap UVs, made at compile so bake and draw agree; skips models with UV2 or skinning
				Unwrapped( builder );
				Cutouts( builder, path );
				Detail( builder, definition, path );
				Impostor( builder, definition, path );
				Skinning( builder, definition );

				ModelCollision.Write( builder, definition, Path.GetDirectoryName( path ) );
				CoreModelBones.Write( builder, definition.Bones );

				//Captured before ApplyOps below lands, so the next compile has this same untransformed state to
				//start from rather than this one's own transformed output - see ModelBakeBaseline's remarks.
				if ( builder.Save( baseline ) == 0 )
					return false;

				ApplyOps( builder, definition );

				if ( builder.Save( output ) == 0 )
					return false;

				CoreSkeletons.Forget( path );
				CompileClips( path, definition );
				return true;
			}
			catch ( Exception e )
			{
				Log.Warning( $"{path}: compiling baked model threw - {e.Message}" );
				return false;
			}
		}
		finally
		{
			builder.Dispose();
			existing.Dispose();
		}
	}

	//Whether the compiled model was built by this build's importers; timestamps can't tell
	internal static bool BuiltByThisImporter( string definitionPath )
	{
		if ( !CoreNative.Available ) return true;

		var compiled = CompiledPaths.Model( Path.GetFullPath( definitionPath ) );

		if ( !File.Exists( compiled ) ) return false;

		return CoreAssets.ModelFile.ImportGenerationOf( compiled )
			== CoreAssets.ModelFile.ImportGeneration;
	}

	//Inputs a definition names (meshes, hulls, clip exports), as names; used before the first build
	internal static IReadOnlyList<string> NamedInputsOf( string definitionPath )
	{
		if ( string.IsNullOrWhiteSpace( definitionPath ) || !File.Exists( definitionPath ) )
			return Array.Empty<string>();

		if ( !ReadDefinition( Path.GetFullPath( definitionPath ), out var read ) )
			return Array.Empty<string>();

		var named = new HashSet<string>( StringComparer.Ordinal );

		void Add( string name )
		{
			if ( !string.IsNullOrWhiteSpace( name ) ) named.Add( name );
		}

		foreach ( var part in read.Meshes )
			Add( part.File );

		if ( read.Definition is { } definition )
		{
			foreach ( var hull in definition.Hulls ?? [] )
				if ( hull is ModelDefinition.RenderHull { HasFile: true } file ) Add( file.File );

			foreach ( var clip in definition.AllClips() ?? [] )
				Add( clip?.File );
		}

		return named.ToArray();
	}

	//Where one of NamedInputsOf is on disk, the way the compile looks for it - or null where it is not there.
	internal static string ResolveInput( string definitionPath, string named )
		=> string.IsNullOrWhiteSpace( named ) ? null : Resolve( Path.GetFullPath( definitionPath ), named );

	//What a definition says, as far as building it is concerned.
	private record struct MeshSource( string File, float Scale, Vector3 Position, Angles Rotation, string Parent,
		bool Enabled, string Name, List<string> Include );

	//One export found on disk, with the part name body groups refer to
	private readonly record struct Source( string Path, float Scale, Vector3 Position, Angles Rotation,
		string Parent, string Name, IReadOnlyList<string> Include );

	private record struct DefinitionRead( string Json, List<MeshSource> Meshes, List<string> Materials, ModelDefinition Definition );

	//Read a definition off disk. False means the file is not one.
	private static bool ReadDefinition( string path, out DefinitionRead read )
	{
		read = default;

		var meshes = new List<MeshSource>();
		var scale = 1.0f;
		var materials = new List<string>();
		string json;

		try
		{
			json = File.ReadAllText( path );

			//As deep as the editor writes: a model's own graph is the graph editor's document, one level further down than it is in a file.
			using var document = JsonDocument.Parse( json, new JsonDocumentOptions { MaxDepth = 512 } );
			var root = document.RootElement;

			if ( root.TryGetProperty( nameof( ModelDefinition.Scale ), out var scaleValue ) &&
				scaleValue.TryGetSingle( out var value ) && value > 0.0f )
			{
				scale = value;
			}

			if ( root.TryGetProperty( nameof( ModelDefinition.Meshes ), out var listed ) &&
				listed.ValueKind == JsonValueKind.Array )
			{
				foreach ( var entry in listed.EnumerateArray() )
				{
					if ( ReadMeshSource( entry, scale ) is { } part )
						meshes.Add( part );
				}
			}

			if ( root.TryGetProperty( nameof( ModelDefinition.Materials ), out var list ) &&
				list.ValueKind == JsonValueKind.Array )
			{
				foreach ( var entry in list.EnumerateArray() )
					materials.Add( entry.GetString() ?? "" );
			}
		}
		catch ( JsonException e )
		{
			Log.Warning( $"{path} is not a model definition: {e.Message}" );
			return false;
		}
		catch ( IOException e )
		{
			Log.Warning( $"{path} could not be read: {e.Message}" );
			return false;
		}

		//The groups want the whole object rather than three fields picked out of it. Separately, and forgivingly: a model whose groups will not
		//read is still a model, and failing the geometry over them would be the compiler deciding that a field it barely uses is the important one.
		ModelDefinition definition = null;

		try
		{
			definition = JsonSerializer.Deserialize<ModelDefinition>( json, DefinitionOptions );
		}
		catch ( JsonException e )
		{
			Log.Warning( $"{path}: no body groups read - {e.Message}" );
		}

		read = new DefinitionRead( json, meshes, materials, definition );
		return true;
	}

	private static MeshSource? ReadMeshSource( JsonElement entry, float fallbackScale )
	{
		if ( entry.ValueKind == JsonValueKind.String )
		{
			var file = entry.GetString() ?? "";
			return string.IsNullOrWhiteSpace( file )
				? null
				: new MeshSource( file, fallbackScale, default, default, "", true, "", null );
		}

		if ( entry.ValueKind != JsonValueKind.Object ) return null;

		var path = "";
		if ( entry.TryGetProperty( nameof( ModelDefinition.MeshPart.File ), out var fileValue ) )
			path = fileValue.GetString() ?? "";

		if ( string.IsNullOrWhiteSpace( path ) ) return null;

		var scale = fallbackScale;
		if ( entry.TryGetProperty( nameof( ModelDefinition.MeshPart.Scale ), out var scaleValue ) &&
			scaleValue.TryGetSingle( out var read ) && read > 0.0f )
		{
			scale = read;
		}

		//Where the part is put once it has been read. Written by the editor as the vectors it writes everywhere else, so read the same way.
		var position = Read( entry, nameof( ModelDefinition.MeshPart.Position ) );
		var rotation = Read( entry, nameof( ModelDefinition.MeshPart.Rotation ) );

		var enabled = true;
		if ( entry.TryGetProperty( nameof( ModelDefinition.MeshPart.Enabled ), out var enabledValue ) )
			enabled = enabledValue.ValueKind != JsonValueKind.False;

		var parent = entry.TryGetProperty( nameof( ModelDefinition.MeshPart.Parent ), out var named )
			? named.GetString() ?? ""
			: "";

		//What the definition calls this piece, and which of its file's meshes it keeps. Both are read
		//here because the compile below decides what a body group claims from them - see Source.
		var name = entry.TryGetProperty( nameof( ModelDefinition.MeshPart.Name ), out var called )
			? called.GetString() ?? ""
			: "";

		List<string> include = null;

		if ( entry.TryGetProperty( nameof( ModelDefinition.MeshPart.Include ), out var kept ) &&
			kept.ValueKind == JsonValueKind.Array )
		{
			foreach ( var one in kept.EnumerateArray() )
			{
				var held = one.GetString();

				if ( string.IsNullOrWhiteSpace( held ) ) continue;

				( include ??= new List<string>() ).Add( held );
			}
		}

		return new MeshSource( path, scale, position,
			new Angles( rotation.x, rotation.y, rotation.z ), parent, enabled, name, include );
	}

	//A vector field of a mesh part, however the editor wrote it.
	private static Vector3 Read( JsonElement entry, string name )
	{
		if ( !entry.TryGetProperty( name, out var value ) ) return default;

		if ( value.ValueKind == JsonValueKind.String )
			return Vector3.Parse( value.GetString() ?? "" );

		if ( value.ValueKind != JsonValueKind.Object ) return default;

		float Component( string of )
			=> value.TryGetProperty( of, out var found ) && found.TryGetSingle( out var number ) ? number : 0.0f;

		return new Vector3( Component( "x" ), Component( "y" ), Component( "z" ) );
	}

	//Definition JSON options: enums as names, and the embedded graph is skipped for speed
	private static readonly JsonSerializerOptions DefinitionOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		IncludeFields = true,
		MaxDepth = 512,
		Converters = { new EnumConvert(), new ModelDefinition.ModelGraph.Skim() },
	};

	//The meshes, imported and named. Null if there was nothing in them.
	private static byte[] CompileMeshes( List<Source> sources, List<string> materials,
		ModelDefinition definition, string definitionPath )
	{
		if ( !CoreNative.Available || sources is not { Count: > 0 } ) return null;

		var sourceDirectory = Path.GetDirectoryName( definitionPath );

		var builder = new CoreAssets.ModelBuilder();
		builder.SetLodGroup( definition?.LodGroup );

		var scratch = Path.Combine( Path.GetTempPath(), $"rinten-mdl-{System.Guid.NewGuid():N}.bin" );

		try
		{
			try
			{
				var added = 0;

				//Source part of each mesh in the builder, kept in step as meshes are dropped; used by body groups
				var owner = new List<string>();

				//Anything hanging off a bone goes in after everything that does not: where such a part
				//belongs is measured from that bone, and the bone arrives with the mesh that was rigged to it.
				foreach ( var part in sources.OrderBy( x => string.IsNullOrWhiteSpace( x.Parent ) ? 0 : 1 ) )
				{
					var (offset, turn) = CoreMeshImport.Placement( builder, part.Parent, part.Position, part.Rotation );

					var first = builder.MeshCount;

					//The steps of a model's compile are trace: the compile's own line says which model, and these were five more a model at info.
					Log.Trace( $"{definitionPath}: mesh {part.Path}" );
					Logging.Flush();

					var imported = CoreMeshImport.Import( builder, part.Path,
						part.Scale > 0.0f ? part.Scale : 1.0f, offset, turn, part.Parent );

					if ( imported <= 0 ) continue;

					//This part's own filter, over this part's own meshes. Taken over the whole model instead - which is what a shared list of
					//names did - one part's filter kept another part's meshes, so a body naming its chest brought its legs and feet along with it.
					var kept = KeepIncluded( builder, part.Include, first );

					for ( var i = 0; i < kept; i++ ) owner.Add( part.Name ?? "" );

					added += kept;
				}

				if ( added <= 0 ) return null;

				Log.Trace( $"{definitionPath}: imported {added} meshes" );
				Logging.Flush();

				//Each step timed into the "writing" line: when a compile is slow the log says which of these it was, rather than that it was one of them.
				var clock = System.Diagnostics.Stopwatch.StartNew();
				var timing = new System.Text.StringBuilder();

				void Lap( string step )
				{
					if ( clock.ElapsedMilliseconds >= 50 ) timing.Append( $" {step} {clock.ElapsedMilliseconds} ms," );
					clock.Restart();
				}

				UniquelyNameMeshes( builder );

				var count = builder.MeshCount;

				LayMaterials( builder, count, materials, definition );
				Lap( "materials" );

				WriteGroups( builder, count, definition, owner );
				LayClipSkeleton( builder, definition, definitionPath );
				Lap( "groups and skeleton" );

				ModelCollision.Write( builder, definition, sourceDirectory );
				Lap( "collision" );

				CoreModelBones.Write( builder, definition?.Bones );

				ApplyOps( builder, definition );
				Lap( "ops" );

				//Lightmap UVs, made at compile so bake and draw agree; skips models with UV2 or skinning
				Unwrapped( builder );
				Lap( "lightmap unwrap" );

				Cutouts( builder, definitionPath );
				Lap( "cut-outs" );

				Detail( builder, definition, definitionPath );

				Impostor( builder, definition, definitionPath );
				Lap( "impostor" );

				Skinning( builder, definition );

				Log.Trace( $"{definitionPath}: writing{( timing.Length > 0 ? " -" + timing.ToString().TrimEnd( ',' ) : "" )}" );
				Logging.Flush();

				if ( builder.Save( scratch ) == 0 ) return null;

				Lap( "save" );
				if ( timing.Length > 0 ) Log.Trace( $"{definitionPath}: written -{timing.ToString().TrimEnd( ',' )}" );

				return File.ReadAllBytes( scratch );
			}
			catch ( Exception e )
			{
				Log.Warning( $"{definitionPath}: compiling meshes threw - {e.Message}" );
				return null;
			}
		}
		finally
		{
			builder.Dispose();

			if ( File.Exists( scratch ) ) File.Delete( scratch );
		}
	}

	//Drops mesh parts that are also listed as hulls, unless it's the only mesh
	private static List<Source> WithoutHullsOnly(
		List<Source> sources,
		ModelDefinition definition, string definitionPath )
	{
		if ( sources.Count < 2 || definition?.Hulls is not { Count: > 0 } ) return sources;

		var shapes = new HashSet<string>( System.StringComparer.OrdinalIgnoreCase );

		foreach ( var hull in definition.Hulls )
		{
			if ( hull is ModelDefinition.RenderHull { HasFile: true } file )
				shapes.Add( Path.GetFileName( file.File ) );
		}

		if ( shapes.Count == 0 ) return sources;

		var kept = new List<Source>();

		foreach ( var part in sources )
		{
			if ( shapes.Contains( Path.GetFileName( part.Path ) ) )
			{
				Log.Info( $"{definitionPath}: {Path.GetFileName( part.Path )} is a hull, not drawn" );
				continue;
			}

			kept.Add( part );
		}

		return kept.Count > 0 ? kept : sources;
	}

	//Drops parts that are LOD copies (foolod.smd, foo_lod1.smd) so they aren't drawn over the model
	private static List<Source> WithoutLevelsOfDetail(
		List<Source> sources, string definitionPath )
	{
		if ( sources.Count < 2 ) return sources;

		var kept = new List<Source>();

		foreach ( var part in sources )
		{
			if ( IsLevelOfDetail( Path.GetFileNameWithoutExtension( part.Path ) ) )
			{
				Log.Info( $"{definitionPath}: {Path.GetFileName( part.Path )} is a level of detail, not drawn" );
				continue;
			}

			kept.Add( part );
		}

		return kept.Count > 0 ? kept : sources;
	}

	//Whether a file is named as somebody's level of detail.
	private static bool IsLevelOfDetail( string name )
	{
		var end = name.Length;

		while ( end > 0 && char.IsAsciiDigit( name[end - 1] ) ) end--;

		//With or without a number after it: an export writes both foolod.smd and foo_lod1.smd, and they mean the same thing.
		if ( end < 3 ) return false;

		if ( !string.Equals( name.Substring( end - 3, 3 ), "lod", System.StringComparison.OrdinalIgnoreCase ) )
			return false;

		//"lod1" on its own is a file called lod1, not somebody's lod1.
		return end - 3 > 0;
	}

	//Applies definition materials by surface name, else by slot in first-named surface order
	private static void LayMaterials( CoreAssets.ModelBuilder builder, int count, List<string> materials,
		ModelDefinition definition )
	{
		//Whether meshes wear the art's own surface values; reapplied every build, see ArtSurfaces
		var use = definition?.ArtSurfaces ?? ModelDefinition.ArtSurfaceUse.Unchosen;

		void Wear( int mesh, bool chosen )
			=> builder.WearMeshSurface( mesh, use == ModelDefinition.ArtSurfaceUse.Everywhere
				|| ( use == ModelDefinition.ArtSurfaceUse.Unchosen && !chosen ) );

		//One material named on the definition is the override, and it is the whole answer: every mesh wears it and nothing below is asked.
		if ( !string.IsNullOrWhiteSpace( definition?.Material ) )
		{
			for ( var i = 0; i < count; i++ )
			{
				builder.SetMeshMaterial( i, definition.Material );
				Wear( i, chosen: true );
			}

			return;
		}

		var byName = new Dictionary<string, string>( System.StringComparer.OrdinalIgnoreCase );

		foreach ( var pair in definition?.MaterialRemaps ?? new() )
		{
			if ( pair is null ) continue;
			if ( string.IsNullOrWhiteSpace( pair.Surface ) || string.IsNullOrWhiteSpace( pair.Material ) ) continue;

			byName[pair.Surface] = pair.Material;
			byName.TryAdd( Bare( pair.Surface ), pair.Material );
		}

		//A material file named after the surface belongs to it, regardless of list order
		foreach ( var material in materials )
		{
			if ( string.IsNullOrWhiteSpace( material ) ) continue;

			var named = Path.GetFileNameWithoutExtension( material );

			if ( string.IsNullOrWhiteSpace( named ) ) continue;

			byName.TryAdd( named, material );
		}

		//Surfaces in the order the meshes first name them - the slots the list of paths is one per. Counted without the duplicate suffix, so a model
		//exported as lowpoly_metal.007 and lowpoly_metal.003 has one metal slot and not two; with two, the list runs out and half the model is bare.
		var slots = new Dictionary<string, int>( System.StringComparer.OrdinalIgnoreCase );

		for ( var i = 0; i < count; i++ )
		{
			var surface = Bare( Surface( builder, i ) );

			if ( !slots.ContainsKey( surface ) ) slots[surface] = slots.Count;
		}

		for ( var i = 0; i < count; i++ )
		{
			var surface = Surface( builder, i );

			if ( byName.TryGetValue( surface, out var named )
				|| byName.TryGetValue( Bare( surface ), out named ) )
			{
				builder.SetMeshMaterial( i, named );
				Wear( i, chosen: true );
				continue;
			}

			if ( slots.TryGetValue( Bare( surface ), out var slot )
				&& slot < materials.Count
				&& !string.IsNullOrWhiteSpace( materials[slot] ) )
			{
				builder.SetMeshMaterial( i, materials[slot] );
				Wear( i, chosen: true );
				continue;
			}

			//Last resort: plain white material instead of the missing texture for unchosen meshes
			builder.SetMeshMaterial( i, ModelDefinition.PlainMaterial );
			Wear( i, chosen: false );
		}
	}

	//Gives the model a lightmap sheet, where it has not got one already.
	private static void Unwrapped( CoreAssets.ModelBuilder builder )
	{
		try
		{
			builder.GenerateLightmapUv( 0 );
		}
		catch ( Exception e )
		{
			Log.Warning( $"lightmap unwrap threw - {e.Message}" );
		}
	}

	//The side of the grid a material's alpha is handed to the builder at, at most.
	private const int CutoutSide = 512;

	//Tells the builder each mesh's cutout alpha, so LODs simplify leaves and cards correctly
	private static void Cutouts( CoreAssets.ModelBuilder builder, string definitionPath )
	{
		var known = new Dictionary<string, (byte[] Alpha, int Width, int Height, float Cutoff)>( StringComparer.OrdinalIgnoreCase );

		for ( var i = 0; i < builder.MeshCount; i++ )
		{
			var material = builder.MeshMaterial( i );
			if ( string.IsNullOrWhiteSpace( material ) ) continue;

			if ( !known.TryGetValue( material, out var cut ) )
			{
				try
				{
					cut = CutoutOf( definitionPath, material );
				}
				catch ( Exception e )
				{
					Log.Warning( $"{definitionPath}: could not read where {material} is cut out - {e.Message}" );
					cut = default;
				}

				known[material] = cut;
			}

			if ( cut.Alpha is not null ) builder.SetMeshCutout( i, cut.Alpha, cut.Width, cut.Height, cut.Cutoff );
		}
	}

	//Bakes the impostor views and quads if the definition asks; material is made at load (CoreImpostor)
	private static void Impostor( CoreAssets.ModelBuilder builder, ModelDefinition definition, string definitionPath )
	{
		if ( definition is null ) return;
		if ( !definition.Impostor && definition.Detail != ModelDefinition.DetailMode.ImpostorOnly ) return;
		if ( definition.Detail == ModelDefinition.DetailMode.None ) return;

		try
		{
			var relative = AssetRelative( definitionPath );
			if ( relative is null ) return;

			var looks = new CoreAssets.ModelBuilder.Look[builder.MeshCount];

			for ( var i = 0; i < looks.Length; i++ )
				looks[i] = LookOf( definitionPath, builder.MeshMaterial( i ) );

			if ( !builder.MakeImpostor( looks, relative + "#impostor" ) )
				Log.Warning( $"{definitionPath}: no impostor - the model is posed by bones or shapes, or nothing of it could be drawn" );
		}
		catch ( Exception e )
		{
			Log.Warning( $"{definitionPath}: the impostor could not be made - {e.Message}" );
		}
	}

	//How the model comes down with distance, into the builder: the chain's length and step, and what the
	//definition says of single meshes along it - see ModelDefinition.Detail and LevelChain.BuildChain.
	private static void Detail( CoreAssets.ModelBuilder builder, ModelDefinition definition, string definitionPath )
	{
		if ( definition is null ) return;

		//The model as authored, alone - the impostor after it where it has one.
		if ( definition.Detail != ModelDefinition.DetailMode.Levels )
		{
			builder.SetClusterLevels( 1 );
			return;
		}

		if ( definition.DetailLevels > 0 ) builder.SetClusterLevels( definition.DetailLevels );
		builder.SetChainStep( definition.DetailStep );
		builder.SetShadowLevelOffset( definition.ShadowLevelOffset );

		if ( definition.DetailMeshes is not { Count: > 0 } ) return;

		var names = Table( builder, builder.MeshCount );

		foreach ( var one in definition.DetailMeshes )
		{
			var mesh = Find( names, one.Mesh );

			if ( mesh < 0 )
			{
				Log.Warning( $"{definitionPath}: detail names a mesh the model does not have - {one.Mesh}" );
				continue;
			}

			if ( one.Use == ModelDefinition.DetailMeshUse.GoneFrom )
			{
				builder.SetMeshLastLevel( mesh, one.Level );
				continue;
			}

			var of = Find( names, one.Of );

			if ( of < 0 || builder.SetMeshAsLevel( mesh, of, one.Level ) == 0 )
				Log.Warning( $"{definitionPath}: {one.Mesh} cannot be level {one.Level} of {one.Of}" );
		}
	}

	//How the model's skin follows its bones, into the file - see ModelFormat.ModelFlags.
	private static void Skinning( CoreAssets.ModelBuilder builder, ModelDefinition definition )
	{
		if ( definition?.Skinning == SkinningMethod.DualQuaternion )
			builder.Flags |= ModelFormat.ModelFlags.DualQuaternion;
	}

	//A path under the assets the definition belongs to, the way materials and pictures are named - or null.
	private static string AssetRelative( string absolute )
	{
		var full = Path.GetFullPath( absolute ).Replace( '\\', '/' );
		var said = Editor.FileSystem.Content?.GetRelativePath( full );
		if ( !string.IsNullOrEmpty( said ) && !Path.IsPathRooted( said ) ) return said.Replace( '\\', '/' ).TrimStart( '/' );

		//Out of the editor's reach: from the nearest folder called Assets.
		var at = full.LastIndexOf( "/Assets/", StringComparison.OrdinalIgnoreCase );
		return at < 0 ? null : full[( at + "/Assets/".Length )..];
	}

	//What a material is read for when it is read for where it cuts holes - see CutoutOf.
	internal const string CutKey = "cut";

	//What a material is read for when the impostor is painted from it - see LookOf.
	internal const string LookKey = "look";

	//The material fields a build actually uses, so unrelated material edits don't trigger rebuilds
	internal static string MaterialBakeKey( string file, string purpose )
	{
		try
		{
			using var document = System.Text.Json.JsonDocument.Parse( File.ReadAllText( file ) );
			var root = document.RootElement;
			if ( root.ValueKind != System.Text.Json.JsonValueKind.Object ) return "";

			string Field( string name ) => root.TryGetProperty( name, out var value ) ? value.GetRawText() : "";

			var alpha = root.TryGetProperty( "Features", out var features ) && features.ValueKind == System.Text.Json.JsonValueKind.Object
				&& features.TryGetProperty( "F_ALPHA", out var feature ) ? feature.GetRawText() : "";

			var key = $"{Field( "Blend" )}|{alpha}|{Field( "AlphaCutoff" )}|{Field( "ColorMap" )}|{Field( "OpacityMask" )}";

			return purpose == LookKey ? $"{key}|{Field( "Tint" )}|{Field( "Roughness" )}" : key;
		}
		catch ( Exception e ) when ( e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException )
		{
			return "unreadable";
		}
	}

	//What a material looks like for the impostor's bake: its picture, its tint and where it cuts.
	private static CoreAssets.ModelBuilder.Look LookOf( string definitionPath, string material )
	{
		var look = new CoreAssets.ModelBuilder.Look();
		if ( string.IsNullOrWhiteSpace( material ) ) return look;

		var file = Resolve( definitionPath, material );
		if ( file is null ) return look;

		ImportReads.Keyed( file, $"{LookKey}:{MaterialBakeKey( file, LookKey )}" );

		using var document = System.Text.Json.JsonDocument.Parse( File.ReadAllText( file ) );
		var root = document.RootElement;
		if ( root.ValueKind != System.Text.Json.JsonValueKind.Object ) return look;

		look.Masked = root.TryGetProperty( "Blend", out var blend ) && blend.ValueKind == System.Text.Json.JsonValueKind.String
			&& string.Equals( blend.GetString(), "Masked", StringComparison.OrdinalIgnoreCase );

		if ( !look.Masked && root.TryGetProperty( "Features", out var features ) && features.ValueKind == System.Text.Json.JsonValueKind.Object
			&& features.TryGetProperty( "F_ALPHA", out var alpha ) && alpha.ValueKind == System.Text.Json.JsonValueKind.Number )
			look.Masked = alpha.GetInt32() != 0;

		if ( root.TryGetProperty( "AlphaCutoff", out var cutoff ) && cutoff.ValueKind == System.Text.Json.JsonValueKind.Number )
			look.Cutoff = cutoff.GetSingle();

		if ( root.TryGetProperty( "Roughness", out var roughness ) && roughness.ValueKind == System.Text.Json.JsonValueKind.Number )
			look.Roughness = Math.Clamp( roughness.GetSingle(), 0.04f, 1.0f );

		if ( root.TryGetProperty( "Tint", out var tint ) && tint.ValueKind == System.Text.Json.JsonValueKind.String )
		{
			var parts = tint.GetString().Split( ',' );
			if ( parts.Length >= 3 && float.TryParse( parts[0], System.Globalization.CultureInfo.InvariantCulture, out var r )
				&& float.TryParse( parts[1], System.Globalization.CultureInfo.InvariantCulture, out var g )
				&& float.TryParse( parts[2], System.Globalization.CultureInfo.InvariantCulture, out var b ) )
				look.Tint = new Color( r, g, b, 1.0f );
		}

		var (pixels, width, height) = PictureOf( definitionPath, root );
		if ( pixels is null ) return look;

		look.Width = width;
		look.Height = height;
		look.Pixels = pixels;

		return look;
	}

	private static (byte[] Alpha, int Width, int Height, float Cutoff) CutoutOf( string definitionPath, string material )
	{
		var file = Resolve( definitionPath, material );
		if ( file is null ) return default;

		ImportReads.Keyed( file, $"{CutKey}:{MaterialBakeKey( file, CutKey )}" );

		using var document = System.Text.Json.JsonDocument.Parse( File.ReadAllText( file ) );
		var root = document.RootElement;

		if ( root.ValueKind != System.Text.Json.JsonValueKind.Object ) return default;

		//Cut out: said either way a material says it.
		var masked = root.TryGetProperty( "Blend", out var blend ) && blend.ValueKind == System.Text.Json.JsonValueKind.String
			&& string.Equals( blend.GetString(), "Masked", StringComparison.OrdinalIgnoreCase );

		if ( !masked && root.TryGetProperty( "Features", out var features ) && features.ValueKind == System.Text.Json.JsonValueKind.Object
			&& features.TryGetProperty( "F_ALPHA", out var alphaFeature ) && alphaFeature.ValueKind == System.Text.Json.JsonValueKind.Number )
			masked = alphaFeature.GetInt32() != 0;

		if ( !masked ) return default;

		var cutoff = root.TryGetProperty( "AlphaCutoff", out var said ) && said.ValueKind == System.Text.Json.JsonValueKind.Number
			? said.GetSingle() : 0.5f;

		var (pixels, width, height) = PictureOf( definitionPath, root );
		if ( pixels is null ) return default;

		var alpha = new byte[width * height];
		var holes = false;
		var under = (int)MathF.Round( cutoff * 255.0f );

		for ( var y = 0; y < height; y++ )
		{
			for ( var x = 0; x < width; x++ )
			{
				var a = pixels[( y * width ) + x].a;

				alpha[( y * width ) + x] = a;
				holes |= a < under;
			}
		}

		//Cut out by a picture with nothing to cut: a leaf pack whose colour came without its alpha
		//and whose mask was never named draws every card solid, and nothing else would say why.
		if ( !holes )
			Log.Warning( $"{definitionPath}: {material} is cut out, but neither its colour map's alpha nor an opacity mask (OpacityMask) has a hole in it - it draws solid" );

		return holes ? ( alpha, width, height, cutoff ) : default;
	}

	//Material colour picture (max CutoutSide) with its cutout mask in alpha; null if none
	private static (Color32[] Pixels, int Width, int Height) PictureOf( string definitionPath, System.Text.Json.JsonElement root )
	{
		if ( !root.TryGetProperty( "ColorMap", out var map ) || map.ValueKind != System.Text.Json.JsonValueKind.String ) return default;

		var picture = Resolve( definitionPath, map.GetString() );
		if ( picture is null ) return default;

		var (pixels, width, height) = Sampled( picture );
		if ( pixels is null ) return default;

		if ( root.TryGetProperty( "OpacityMask", out var named ) && named.ValueKind == System.Text.Json.JsonValueKind.String
			&& !string.IsNullOrWhiteSpace( named.GetString() )
			&& Resolve( definitionPath, named.GetString() ) is { } maskFile )
		{
			var (mask, maskWidth, maskHeight) = Sampled( maskFile );

			if ( mask is not null )
			{
				for ( var y = 0; y < height; y++ )
				{
					for ( var x = 0; x < width; x++ )
					{
						var cut = mask[( ( y * maskHeight / height ) * maskWidth ) + ( x * maskWidth / width )].r;
						ref var texel = ref pixels[( y * width ) + x];
						texel.a = (byte)( ( texel.a * cut + 127 ) / 255 );
					}
				}
			}
		}

		return ( pixels, width, height );
	}

	//A picture's texels, one in every so many, no finer than CutoutSide.
	private static (Color32[] Pixels, int Width, int Height) Sampled( string picture )
	{
		ImportReads.File( picture );

		using var bitmap = Bitmap.CreateFromBytes( File.ReadAllBytes( picture ) );
		if ( bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0 ) return default;

		var pixels = bitmap.GetPixels32();
		if ( pixels is null || pixels.Length < bitmap.Width * bitmap.Height ) return default;

		var step = Math.Max( 1, (int)MathF.Ceiling( Math.Max( bitmap.Width, bitmap.Height ) / (float)CutoutSide ) );
		var width = Math.Max( 1, bitmap.Width / step );
		var height = Math.Max( 1, bitmap.Height / step );
		var sampled = new Color32[width * height];

		for ( var y = 0; y < height; y++ )
			for ( var x = 0; x < width; x++ )
				sampled[( y * width ) + x] = pixels[( Math.Min( y * step, bitmap.Height - 1 ) * bitmap.Width ) + Math.Min( x * step, bitmap.Width - 1 )];

		return ( sampled, width, height );
	}

	//What the export called a mesh's surface.
	private static string Surface( CoreAssets.ModelBuilder builder, int mesh )
		=> builder.MeshMaterial( mesh );

	//A surface name without Blender's duplicate suffix, so "lowpoly_metal.005" and "lowpoly_metal" are
	//the same surface - the exports here are full of them, and they are the same material every time.
	private static string Bare( string surface )
	{
		if ( string.IsNullOrEmpty( surface ) ) return "";

		var dot = surface.LastIndexOf( '.' );

		if ( dot <= 0 || dot != surface.Length - 4 ) return surface;

		for ( var i = dot + 1; i < surface.Length; i++ )
		{
			if ( !char.IsAsciiDigit( surface[i] ) ) return surface;
		}

		return surface[..dot];
	}

	//Mesh name -> index, matched loosely (e.g. Blender .001 suffixes); -1 if not found
	private static Dictionary<string, int> Table( CoreAssets.ModelBuilder builder, int meshCount )
	{
		var names = new Dictionary<string, int>( System.StringComparer.OrdinalIgnoreCase );

		for ( var i = 0; i < meshCount; i++ )
		{
			names.TryAdd( builder.MeshName( i ), i );
		}

		return names;
	}

	//Keeps a part's included meshes and returns the count; a filter matching nothing keeps all
	private static int KeepIncluded( CoreAssets.ModelBuilder builder, IReadOnlyList<string> include, int first )
	{
		var count = builder.MeshCount;

		if ( include is not { Count: > 0 } ) return count - first;

		var keep = new HashSet<string>( System.StringComparer.OrdinalIgnoreCase );

		foreach ( var name in include )
		{
			if ( !string.IsNullOrWhiteSpace( name ) ) keep.Add( name );
		}

		var wanted = new List<int>();

		for ( var i = first; i < count; i++ )
		{
			if ( Named( keep, builder.MeshName( i ) ) ) wanted.Add( i );
		}

		if ( wanted.Count == 0 ) return count - first;

		//Backwards: dropping one moves everything after it down.
		for ( var i = count - 1; i >= first; i-- )
		{
			if ( !wanted.Contains( i ) ) builder.DropMesh( i );
		}

		return wanted.Count;
	}

	private static int Find( Dictionary<string, int> names, string mesh )
	{
		if ( string.IsNullOrWhiteSpace( mesh ) ) return -1;

		if ( names.TryGetValue( mesh, out var index ) ) return index;

		var bare = Bare( mesh );
		if ( !ReferenceEquals( bare, mesh ) && names.TryGetValue( bare, out index ) ) return index;

		foreach ( var (held, at) in names )
		{
			if ( string.Equals( Bare( held ), mesh, System.StringComparison.OrdinalIgnoreCase ) ) return at;
			if ( string.Equals( Bare( held ), bare, System.StringComparison.OrdinalIgnoreCase ) ) return at;
		}

		return -1;
	}

	//Maps body groups and material sets to mask bits; unclaimed meshes always draw, max 64 choices
	private static void WriteGroups( CoreAssets.ModelBuilder builder, int meshCount, ModelDefinition definition,
		List<string> owner )
	{
		if ( definition is null ) return;

		var names = Table( builder, meshCount );

		//The levels first, because they and the body groups both name meshes and both want the same table of names.
		DropCoarseMeshes( builder, names, meshCount, definition, owner );

		//Dropping one moved every mesh after it, so the table is read again rather than trusted: claimed by the old
		//numbering, a model with a coarse mesh in it gave a body group's bits to whatever slid into that mesh's place.
		meshCount = builder.MeshCount;
		names = Table( builder, meshCount );

		var claimed = new ulong[meshCount];

		//The bits are not counted out here: the definition works out its own layout, so the editor showing
		//an artist what a group compiles to and the compiler writing it are reading the same answer.
		var layout = ModelDefinition.BodyGroupLayout.Of( definition.BodyGroups );

		if ( layout.Dropped > 0 )
		{
			Log.Warning( $"{definition.ResourcePath}: more than {ModelDefinition.BodyGroupLayout.MaxChoices} " +
				$"body group choices - {layout.Dropped} of them are ignored" );
		}

		foreach ( var group in layout.Parts )
		{
			var part = builder.AddPart( group.Name );
			if ( part < 0 ) continue;

			foreach ( var choice in group.Choices )
			{
				builder.AddPartChoice( part, choice.Name, choice.Mask );

				foreach ( var meshName in choice.Meshes )
				{
					var index = Find( names, meshName );

					if ( index >= 0 )
					{
						claimed[index] |= choice.Mask;
						continue;
					}

					//Not a mesh, then a part: their groups choose between the pieces a model is assembled from, and a
					//piece is several meshes with names of their own. Everything that piece brought in is claimed together.
					var any = false;

					for ( var i = 0; owner is not null && i < owner.Count && i < meshCount; i++ )
					{
						if ( !string.Equals( owner[i], meshName, System.StringComparison.OrdinalIgnoreCase ) )
							continue;

						claimed[i] |= choice.Mask;
						any = true;
					}

					if ( !any )
						Log.Warning( $"{definition.ResourcePath}: no mesh or part called {meshName}" );
				}
			}
		}

		for ( var i = 0; i < meshCount; i++ )
		{
			if ( claimed[i] != 0 ) builder.SetMeshGroup( i, claimed[i] );
		}

		foreach ( var set in definition.MaterialGroups ?? new() )
		{
			if ( set is null || set.Materials is not { Count: > 0 } ) continue;

			var group = builder.AddMaterialGroup( set.Name ?? "" );
			if ( group < 0 ) continue;

			for ( var i = 0; i < set.Materials.Count && i < meshCount; i++ )
			{
				if ( string.IsNullOrWhiteSpace( set.Materials[i] ) ) continue;

				builder.SetGroupMaterial( group, i, set.Materials[i] );
			}
		}
	}

	//Drops the meshes an export marked as cheaper copies for distance. The clusters are the level of detail now, so a coarse copy is not
	//a level to switch to but a second model in the same place. From the last down, so the indices above the one being dropped still hold.
	private static void DropCoarseMeshes( CoreAssets.ModelBuilder builder, Dictionary<string, int> names, int meshCount,
		ModelDefinition definition, List<string> owner )
	{
		var coarse = definition.CoarseMeshes;
		if ( coarse is not { Count: > 0 } ) return;

		var drop = new bool[meshCount];

		foreach ( var meshName in coarse )
		{
			if ( string.IsNullOrWhiteSpace( meshName ) ) continue;

			var index = Find( names, meshName );
			if ( index < 0 || index >= meshCount ) continue;

			drop[index] = true;
		}

		for ( var i = meshCount - 1; i >= 0; i-- )
		{
			if ( !drop[i] ) continue;

			builder.DropMesh( i );

			if ( owner is not null && i < owner.Count ) owner.RemoveAt( i );
		}
	}

	//Where the definition's mesh actually is: beside it, or from the root of the project
	//it belongs to. Both are written in the field by different tools, so both are tried.
	private static string Resolve( string definition, string mesh )
	{
		//Already a real path. A definition written by hand names its meshes relative to itself, but one built to preview a single export - see
		//CoreAsset.GetPreviewModel - sits in the cache folder with nothing above it, and the only name it can give the export is where the export is.
		if ( Path.IsPathRooted( mesh ) )
			return File.Exists( mesh ) ? Path.GetFullPath( mesh ) : null;

		var relative = mesh.Replace( '/', Path.DirectorySeparatorChar ).TrimStart( Path.DirectorySeparatorChar );

		var beside = Path.GetFullPath( Path.Combine( Path.GetDirectoryName( definition ), relative ) );
		if ( File.Exists( beside ) ) return beside;

		//Up through the parents until something holds the file. The assets root is somewhere above
		//the definition, and this finds it without having to ask the project system which one it is.
		var directory = Path.GetDirectoryName( definition );

		while ( !string.IsNullOrEmpty( directory ) )
		{
			var candidate = Path.Combine( directory, relative );
			if ( File.Exists( candidate ) ) return Path.GetFullPath( candidate );

			directory = Path.GetDirectoryName( directory );
		}

		return null;
	}

	static void UniquelyNameMeshes( CoreAssets.ModelBuilder builder )
	{
		var used = new HashSet<string>( System.StringComparer.OrdinalIgnoreCase );
		var count = builder.MeshCount;

		for ( var i = 0; i < count; i++ )
		{
			var name = builder.MeshName( i );

			if ( string.IsNullOrWhiteSpace( name ) ) name = $"mesh{i}";

			if ( !used.Add( name ) )
			{
				var material = builder.MeshMaterial( i );
				var unique = string.IsNullOrWhiteSpace( material ) ? $"{name}.{i}" : $"{name}.{material}";
				var n = 2;
				while ( !used.Add( unique ) ) unique = $"{name}.{n++}";
				builder.SetMeshName( i, unique );
			}
		}
	}

	//Bones from a compiled take, when the mesh file itself had none.
	static unsafe void LayClipSkeleton( CoreAssets.ModelBuilder builder, ModelDefinition definition, string definitionPath )
	{
		if ( builder is null || definition is null ) return;

		var dir = Path.GetDirectoryName( definitionPath );
		if ( string.IsNullOrWhiteSpace( dir ) ) return;

		foreach ( var clip in definition.AllClips() )
		{
			if ( clip is null || !clip.Enabled ) continue;

			var name = clip.SequenceName;
			if ( string.IsNullOrWhiteSpace( name ) ) continue;

			var loaded = CoreClips.Clip( Path.Combine( dir, name + ".anim_c" ) );
			if ( loaded is null )
			{
				var source = Resolve( definitionPath, clip.File );
				if ( source is not null ) loaded = CoreClips.Clip( source );
			}

			if ( loaded is null ) continue;

			var count = loaded.Bones.Length;
			if ( count <= 0 ) continue;

			var identity = stackalloc float[16] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
			var map = new int[count];

			for ( var bone = 0; bone < count; bone++ )
			{
				var boneName = loaded.Bones[bone].Name;
				if ( string.IsNullOrWhiteSpace( boneName ) )
				{
					map[bone] = -1;
					continue;
				}

				var held = builder.FindBone( boneName );
				if ( held >= 0 )
				{
					map[bone] = held;
					continue;
				}

				var parentClip = loaded.Bones[bone].Parent;
				var parent = parentClip >= 0 && parentClip < bone ? map[parentClip] : -1;
				map[bone] = CoreModelBones.Add( builder, boneName, parent, identity );
			}

			return;
		}
	}

	static void KeepIncludedMeshes( CoreAssets.ModelBuilder builder, ModelDefinition definition, string definitionPath )
	{
		if ( definition?.Meshes is not { Count: > 0 } ) return;

		var keep = new HashSet<string>( System.StringComparer.OrdinalIgnoreCase );
		var filtering = false;

		foreach ( var part in definition.Meshes )
		{
			if ( part is null || !part.Enabled || string.IsNullOrWhiteSpace( part.File ) ) continue;

			if ( part.Include is not { Count: > 0 } )
			{
				var source = Resolve( definitionPath, part.File );
				if ( source is null ) continue;

				foreach ( var name in CoreMeshImport.Catalog( source, part.Scale > 0.0f ? part.Scale : 1.0f ) )
					keep.Add( name );
				continue;
			}

			filtering = true;
			foreach ( var name in part.Include )
			{
				if ( !string.IsNullOrWhiteSpace( name ) ) keep.Add( name );
			}
		}

		if ( !filtering ) return;

		for ( var i = builder.MeshCount - 1; i >= 0; i-- )
		{
			var name = builder.MeshName( i );
			if ( !Named( keep, name ) ) builder.DropMesh( i );
		}
	}

	static bool Named( HashSet<string> keep, string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return false;
		if ( keep.Contains( name ) ) return true;

		var bare = Bare( name );
		if ( keep.Contains( bare ) ) return true;

		foreach ( var held in keep )
		{
			if ( string.Equals( Bare( held ), name, System.StringComparison.OrdinalIgnoreCase ) ) return true;
			if ( string.Equals( Bare( held ), bare, System.StringComparison.OrdinalIgnoreCase ) ) return true;
		}

		return false;
	}

	static void ApplyOps( CoreAssets.ModelBuilder builder, ModelDefinition definition )
	{
		if ( definition is null ) return;

		ModelTransformOp.Apply( builder, definition );

		if ( definition.Mirror is { Enabled: true } mirror )
			builder.Mirror( (int)mirror.Axis, mirror.FlipWinding ? 1 : 0, mirror.RemapLeftRight ? 1 : 0 );
	}

	static void CompileClips( string definitionPath, ModelDefinition definition )
	{
		if ( definition is null ) return;

		var dir = Path.GetDirectoryName( definitionPath );
		if ( string.IsNullOrWhiteSpace( dir ) ) return;

		foreach ( var clip in definition.AllClips() )
		{
			if ( clip is null || !clip.Enabled ) continue;

			var name = clip.SequenceName;
			if ( string.IsNullOrWhiteSpace( name ) ) continue;

			var output = Path.Combine( dir, name + ".anim_c" );
			var result = AnimationCompiler.CompileModelClip( clip, definition, dir, output );
			AnimationClip.Forget( output );
			if ( !result.Success )
				Log.Warning( $"{definitionPath}: clip {name}: {result.Reason}" );
		}
	}

	//Writes back whatever is already compiled beside the definition, for the ported models whose geometry has no source art to rebuild it from.
	private bool Keep( string definition, string lodGroup )
	{
		if ( !File.Exists( definition ) ) return false;

		var compiled = CompiledPaths.Model( definition );

		if ( !File.Exists( compiled ) ) return false;

		byte[] bytes;

		try
		{
			bytes = File.ReadAllBytes( compiled );
		}
		catch ( IOException )
		{
			return false;
		}

		//Either our format straight out, or ours inside a container from the last time this ran. Anything else was not a model of ours to begin with.
		var inside = CoreMeshImport.Unwrap( bytes );
		if ( inside is null ) return false;

		Context.Data.Write( File.ReadAllText( definition ) );
		//The one thing the definition still says about geometry it cannot rebuild: which detail
		//group it keeps. Written into the header, since nothing else will write this file again.
		Context.StreamingData.Write( ModelFormat.WithLodGroup( inside, lodGroup ) );

		return true;
	}
}
