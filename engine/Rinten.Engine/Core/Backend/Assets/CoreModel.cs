using System.Collections.Generic;
using System.IO;

namespace Rinten;

//A model loaded by our core; only covers name, bounds and collision, not the full model backend
internal sealed partial class CoreModel : IDisposable
{
	private static readonly Logger log = new( "Core" );

	private CoreAssets.ModelFile handle;

	private CoreModel( CoreAssets.ModelFile handle, string name )
	{
		this.handle = handle;
		Name = name;
		if ( handle is not null ) handle.Tag = name;
	}

	public string Name { get; }

	public bool IsValid => handle is { Valid: true };

	//Whether this core is shared by several holders (built-ins, error model), so none may dispose it
	internal bool Shared { get; set; }

	//The model the core loaded, for the parts of the engine that read it directly rather than through
	//this class - the animation runtime, which builds a skeleton out of the bones this model carries.
	internal CoreAssets.ModelFile Loaded => handle;

	//The level-of-detail group the model's definition named, or empty. Read out of the file once, when first asked.
	internal string LodGroup => lodGroup ??= handle is { Valid: true } ? handle.LodGroup : "";

	private string lodGroup;

	/// <summary>Load the compiled form of a model resource, if there is one.</summary>
	public static CoreModel Load( string resourcePath )
	{
		if ( string.IsNullOrWhiteSpace( resourcePath ) ) return null;
		if ( !CoreNative.Available ) return null;

		//The four the engine reaches for are built rather than read. Asked for first, because there is
		//no file to find - and looking for one is how they used to end up on disk, and then out of date.
		if ( CoreDevModels.IsBuiltIn( resourcePath ) ) return CoreDevModels.Get( resourcePath );

		var compiled = CompiledPaths.Model( resourcePath ).Replace( '\\', '/' );

		var full = ResolveOnDisk( compiled );

		//Nothing of ours on disk under that name is nothing to load. Their compiled models used to be read here as a fallback; a port turns
		//those into ours, and reading them at run time meant a project could run for months on content nobody had actually brought across.
		if ( full is null ) return null;

		var handle = Read( full );

		if ( handle is null )
		{
			log.Warning( $"{compiled} is not a model this core understands" );
			return null;
		}

		return new CoreModel( handle, resourcePath );
	}

	/// <summary>A model from bytes rather than a path - what the built-ins are, since they are described in code and never written down.</summary>
	public static unsafe CoreModel FromBytes( byte[] bytes, string name )
	{
		if ( !CoreNative.Available || bytes is not { Length: > 0 } ) return null;

		fixed ( byte* data = bytes )
		{
			var handle = CoreAssets.ModelFile.LoadMemory( data, bytes.Length );

			return handle is null ? null : new CoreModel( handle, name );
		}
	}

	/// <summary>A model from bytes a builder already wrote, e.g. on a worker thread</summary>
	public static CoreModel FromMemory( byte[] bytes, string name )
	{
		if ( bytes is not { Length: > 0 } ) return null;

		var handle = CoreAssets.ModelFile.LoadMemory( bytes );

		return handle is null ? null : new CoreModel( handle, name );
	}

	//The model keeps the array and may rewrite it, so the caller must not hold on to it
	internal static CoreModel FromOwned( byte[] bytes, string name )
	{
		var handle = CoreAssets.ModelFile.LoadOwned( bytes );

		return handle is null ? null : new CoreModel( handle, name );
	}

	//What a model built while the game is running is: a tool's brush, a mesh a component rebuilt, one of the built-in shapes. The builder is the caller's to destroy afterwards - the model keeps its own copy.
	/// <summary>A model straight out of a builder, with no file in between.</summary>
	public static CoreModel FromBuilder( CoreAssets.ModelBuilder builder, string name )
	{
		if ( builder is null ) return null;

		var handle = builder.Build();

		return handle is null ? null : new CoreModel( handle, name );
	}

	//Reads a compiled model written either by our compiler or wrapped in a legacy resource container
	internal static unsafe CoreAssets.ModelFile Read( string path )
	{
		//The common way, and the only way once the editor's compiler is ours.
		var handle = CoreAssets.ModelFile.Load( path );
		if ( handle is not null ) return handle;

		byte[] bytes;

		try
		{
			bytes = SealedFile.ReadAllBytes( path );
		}
		catch ( IOException )
		{
			return null;
		}

		var inside = CoreMeshImport.Unwrap( bytes ) ?? FromDefinition( path );
		if ( inside is null ) return null;

		fixed ( byte* data = inside )
		{
			return CoreAssets.ModelFile.LoadMemory( data, inside.Length );
		}
	}

	//Builds a model from its .mdl art when the compiled file is not ours; a stopgap, so it warns
	private static byte[] FromDefinition( string compiled )
	{
		var definition = Path.ChangeExtension( compiled, ".mdl" );
		if ( !File.Exists( definition ) ) return null;

		List<Piece> meshes;

		try
		{
			using var document = System.Text.Json.JsonDocument.Parse( CompiledFile.TextOf( definition ) );
			var root = document.RootElement;

			var scale = 1.0f;

			if ( root.TryGetProperty( nameof( ModelDefinition.Scale ), out var scaleValue ) &&
				scaleValue.TryGetSingle( out var read ) && read > 0.0f )
			{
				scale = read;
			}

			meshes = MeshPieces( root, scale );
		}
		catch ( System.Text.Json.JsonException )
		{
			return null;
		}

		if ( meshes.Count == 0 ) return null;

		if ( !CoreNative.Available ) return null;

		var builder = new CoreAssets.ModelBuilder();

		try
		{
			var added = 0;

			//Anything hanging off a bone after everything that is not, since the bone it is placed from arrives with the mesh that was rigged to it.
			foreach ( var mesh in meshes.OrderBy( piece => string.IsNullOrWhiteSpace( piece.Parent ) ? 0 : 1 ) )
			{
				var source = FindBeside( definition, mesh.File );
				if ( source is null ) continue;
				if ( !CoreMeshImport.CanRead( source ) ) continue;

				//Each piece at its own size and in its own place: a model is several exports assembled, and
				//one of them being a sight authored in millimetres says nothing about the body it goes on.
				var (offset, turn) = CoreMeshImport.Placement( builder, mesh.Parent, mesh.Position, mesh.Rotation );

				var count = CoreMeshImport.Import( builder, source, mesh.Scale, offset, turn, mesh.Parent );
				if ( count > 0 ) added += count;
			}

			if ( added <= 0 ) return null;

			//The bytes rather than a file: the builder is ours now and hands them straight back, where the core could only write them to a path.
			var bytes = builder.ToBytes();

			log.Warning( $"{Path.GetFileName( definition )} was compiled by something else - " +
				"built from its mesh instead. Recompile it to stop paying for this every load." );

			return bytes;
		}
		finally
		{
			builder.Dispose();
		}
	}

	//One export a definition is built from, and where it goes.
	private readonly record struct Piece( string File, float Scale, Vector3 Position, Angles Rotation, string Parent );

	private static List<Piece> MeshPieces( System.Text.Json.JsonElement root, float scale )
	{
		var found = new List<Piece>();

		if ( root.TryGetProperty( nameof( ModelDefinition.Meshes ), out var listed ) &&
			listed.ValueKind == System.Text.Json.JsonValueKind.Array )
		{
			foreach ( var entry in listed.EnumerateArray() )
			{
				if ( entry.ValueKind == System.Text.Json.JsonValueKind.String )
				{
					var path = entry.GetString();
					if ( !string.IsNullOrWhiteSpace( path ) ) found.Add( new Piece( path, scale, default, default, "" ) );
					continue;
				}

				if ( entry.ValueKind != System.Text.Json.JsonValueKind.Object ) continue;

				if ( !entry.TryGetProperty( nameof( ModelDefinition.MeshPart.File ), out var file ) ) continue;

				var pathNamed = file.GetString();
				if ( string.IsNullOrWhiteSpace( pathNamed ) ) continue;

				var own = scale;

				if ( entry.TryGetProperty( nameof( ModelDefinition.MeshPart.Scale ), out var partScale ) &&
					partScale.TryGetSingle( out var read ) && read > 0.0f )
				{
					own = read;
				}

				var turn = Vector( entry, nameof( ModelDefinition.MeshPart.Rotation ) );

				var parent = entry.TryGetProperty( nameof( ModelDefinition.MeshPart.Parent ), out var named )
					? named.GetString() ?? ""
					: "";

				found.Add( new Piece( pathNamed, own,
					Vector( entry, nameof( ModelDefinition.MeshPart.Position ) ),
					new Angles( turn.x, turn.y, turn.z ), parent ) );
			}
		}

		return found;
	}

	//A vector field of a part, however it was written.
	private static Vector3 Vector( System.Text.Json.JsonElement entry, string name )
	{
		if ( !entry.TryGetProperty( name, out var value ) ) return default;

		if ( value.ValueKind == System.Text.Json.JsonValueKind.String )
			return Vector3.Parse( value.GetString() ?? "" );

		if ( value.ValueKind != System.Text.Json.JsonValueKind.Object ) return default;

		float Component( string of )
			=> value.TryGetProperty( of, out var found ) && found.TryGetSingle( out var number ) ? number : 0.0f;

		return new Vector3( Component( "x" ), Component( "y" ), Component( "z" ) );
	}

	//A file named relative to a definition: beside it, or from any directory above it - which is where an assets root ends up being.
	private static string FindBeside( string definition, string relative )
	{
		var name = relative.Replace( '/', Path.DirectorySeparatorChar ).TrimStart( Path.DirectorySeparatorChar );
		var directory = Path.GetDirectoryName( definition );

		var beside = Path.Combine( directory, name );
		if ( File.Exists( beside ) ) return beside;

		while ( !string.IsNullOrEmpty( directory ) )
		{
			var candidate = Path.Combine( directory, name );
			if ( File.Exists( candidate ) ) return candidate;

			directory = Path.GetDirectoryName( directory );
		}

		return null;
	}

	//Where a resource path lands on disk. The core opens files by name, so a mounted path is not enough.
	internal static string ResolveOnDisk( string path )
	{
		//Already a real path. The compiler writes absolute ones, and so does anything working outside a mounted project.
		if ( Path.IsPathRooted( path ) )
			return File.Exists( path ) ? path : null;

		foreach ( var system in new[] { EngineFileSystem.Mounted, EngineFileSystem.Assets, EngineFileSystem.CoreContent } )
		{
			if ( system is null ) continue;
			if ( !system.FileExists( path ) ) continue;

			var full = system.GetFullPath( path );
			if ( !string.IsNullOrEmpty( full ) && File.Exists( full ) ) return full;
		}

		return Shipped( path );
	}

	//Where a text resource lands on disk: its own name or its packed compiled file, for packed kinds only
	internal static string ResolveTextOnDisk( string path )
		=> ResolveOnDisk( path ) ?? ( path.EndsWith( "_c", StringComparison.OrdinalIgnoreCase )
			? null
			: ResolveOnDisk( path + "_c" ) );

	//The path in the engine's shipped assets, for when no project is mounted
	private static string Shipped( string path )
	{
		var relative = Path.Combine( "addons", "base", "Assets",
			path.Replace( '/', Path.DirectorySeparatorChar ) );

		foreach ( var root in new[] { EngineFileSystem.Root?.GetFullPath( "/" ), Directory.GetCurrentDirectory() } )
		{
			if ( string.IsNullOrEmpty( root ) ) continue;

			var full = Path.Combine( root, relative );
			if ( File.Exists( full ) ) return full;
		}

		return null;
	}

	public unsafe BBox RenderBounds
	{
		get
		{
			var v = stackalloc float[6];
			handle.GetRenderBounds( v );
			return new BBox( new Vector3( v[0], v[1], v[2] ), new Vector3( v[3], v[4], v[5] ) );
		}
	}

	public unsafe BBox PhysicsBounds
	{
		get
		{
			var v = stackalloc float[6];
			handle.GetPhysicsBounds( v );
			return new BBox( new Vector3( v[0], v[1], v[2] ), new Vector3( v[3], v[4], v[5] ) );
		}
	}

	/// <summary>Whether this model brought any collision with it.</summary>
	public bool HasCollision => IsValid && handle.BodyCount > 0;

	/// <summary>How many pieces of geometry it draws as.</summary>
	public int MeshCount => IsValid ? handle.MeshCount : 0;

	//-- body groups: a mesh draws if it has any chosen bit; a mesh with no bits always draws --

	/// <summary>What is drawn when nobody has chosen anything.</summary>
	public ulong DefaultMask => IsValid ? handle.DefaultMask : 0;

	/// <summary>Which choices claim a mesh; zero is always drawn.</summary>
	public ulong MeshGroup( int mesh ) => IsValid ? handle.MeshGroup( mesh ) : 0;

	public int PartCount => IsValid ? handle.PartCount : 0;

	public string PartName( int part )
		=> IsValid ? handle.PartName( part ) : "";

	public ulong PartMask( int part ) => IsValid ? handle.PartMask( part ) : 0;

	public int PartChoiceCount( int part )
		=> IsValid ? handle.PartChoiceCount( part ) : 0;

	public string PartChoiceName( int part, int choice )
		=> IsValid ? handle.PartChoiceName( part, choice ) : "";

	public ulong PartChoiceMask( int part, int choice )
		=> IsValid ? handle.PartChoiceMask( part, choice ) : 0;

	//-- levels of detail: one bit per level like body groups, chosen by camera distance --




	//-- material groups -------------------------------------------------------

	public int MaterialGroupCount => IsValid ? handle.MaterialGroupCount : 0;

	public string MaterialGroupName( int group )
		=> IsValid ? handle.MaterialGroupName( group ) : "";

	/// <summary>What a group puts on a mesh - the mesh's own material where the group says nothing, so a caller never has to check whether it did.</summary>
	public string GroupMaterial( int group, int mesh )
		=> IsValid ? handle.GroupMaterial( group, mesh ) : "";

	/// <summary>What a mesh is called, as the model was built.</summary>
	public string MeshName( int mesh )
		=> IsValid ? handle.MeshName( mesh ) : "";

	/// <summary>Where a mesh's geometry sits in the shared buffers.</summary>
	public int VertexCount => IsValid ? handle.VertexCount : 0;
	public int IndexCount => IsValid ? handle.IndexCount : 0;
	public unsafe int CopyVertices( Vertex* outVertices, int capacity ) => IsValid ? handle.CopyVertices( outVertices, capacity ) : 0;
	public unsafe int CopyIndices( uint* outIndices, int capacity ) => IsValid ? handle.CopyIndices( outIndices, capacity ) : 0;

	public unsafe (int FirstIndex, int IndexCount, int FirstVertex, int VertexCount) MeshRange( int mesh )
	{
		if ( !IsValid ) return default;

		var range = stackalloc int[4];
		handle.MeshRange( mesh, range );

		return (range[0], range[1], range[2], range[3]);
	}

	/// <summary>How many blend shapes the model carries.</summary>
	public int MorphCount => IsValid ? handle.MorphCount : 0;

	/// <summary>The name of one, in the order a delta's morph index means.</summary>
	public string MorphName( int morph )
		=> IsValid ? handle.MorphName( morph ) : "";

	//Copied out whole rather than read a delta at a time - it goes straight into a buffer, once, when the model is first drawn.
	/// <summary>The blend shape data as the renderer wants it: every delta sorted by the vertex it moves, and the run of them each vertex owns.</summary>
	public unsafe (byte[] Deltas, byte[] Runs) MorphData()
	{
		if ( !IsValid ) return (null, null);

		var deltaCount = handle.MorphDeltaCount;
		if ( deltaCount <= 0 ) return (null, null);

		var vertexCount = handle.VertexCount;

		var deltas = new byte[deltaCount * MorphDeltaStride];
		var runs = new byte[vertexCount * MorphRunStride];

		fixed ( byte* d = deltas )
		fixed ( byte* r = runs )
		{
			handle.CopyMorphDeltas( d, deltaCount );
			handle.CopyMorphRuns( r, vertexCount );
		}

		return (deltas, runs);
	}

	/// <summary>A morph index and two float3s - what the on-disk MorphDelta is.</summary>
	public const int MorphDeltaStride = 28;

	//Held still the same way, against ModelFormat.MorphRun.
	/// <summary>A first and a count, one per vertex.</summary>
	public const int MorphRunStride = 8;

	/// <summary>Build this model's collision onto a body, in the model's own space.</summary>
	public int AddCollision( PhysicsBody body ) => CoreModelPhysics.AddShapes( body, handle );

	public void Dispose()
	{
		if ( handle is null ) return;

		//Not ours to close - see Shared. The holder lets go of it either way.
		if ( Shared ) return;

		handle.Dispose();
		handle = null;
	}
}
