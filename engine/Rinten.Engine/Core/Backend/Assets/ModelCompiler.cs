using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Rinten;

//Turns a ModelDefinition into the compiled model the engine loads. The geometry comes
//from an export the artist made, read by our own core.
internal static class ModelCompiler
{
	private static readonly Logger log = new( "Core" );

	/// <summary>What a compile did, or why it did nothing.</summary>
	public readonly record struct Result( bool Success, string Reason, int Meshes )
	{
		public static Result Failed( string reason ) => new( false, reason, 0 );
		public static Result Ok( int meshes ) => new( true, "", meshes );
	}

	/// <summary>Compile <paramref name="definition"/> to the file beside it.</summary>
	public static Result Compile( ModelDefinition definition, string sourceDirectory, string outputPath )
	{
		if ( definition is null ) return Result.Failed( "no definition" );
		if ( !CoreNative.Available ) return Result.Failed( $"rintencore is not loaded: {CoreNative.BuildInfo}" );

		var parts = definition.AllMeshes();

		//A definition with no mesh is not broken: a port bakes the geometry into the compiled file and leaves
		//this empty, because what it read was a model in the legacy compiled format. Recompiling would throw that away.
		if ( parts.Count == 0 )
			return Result.Failed( "no mesh - geometry is already baked, nothing to rebuild from" );

		var builder = new CoreAssets.ModelBuilder();

		try
		{
			builder.SetName( definition.ResourcePath ?? "" );
			builder.SetLodGroup( definition.LodGroup );

			//This model's body group bits, worked out before import so each mesh gets its own as it arrives -
			//see ModelDefinition.BodyGroupLayout: the numbering is the file's own order
			var layout = definition.BodyGroupBits();

			var meshes = 0;

			foreach ( var part in parts )
			{
				//A part their document named and this project has no art for. The definition keeps it
				//so it can be reimported; importing it is a compile that fails on a file nobody has.
				if ( !part.Enabled ) continue;

				var mesh = AssetSources.Resolve( part.File, sourceDirectory );
				if ( mesh is null )
					return Result.Failed( $"cannot find {part.File}" );

				var scale = part.Scale > 0.0f ? part.Scale : 1.0f;

				var first = builder.MeshCount;

				var added = CoreMeshImport.Import( builder, mesh, scale, part.Position, part.Rotation );

				if ( added < 0 ) return Result.Failed( $"{part.File} is not a mesh this core can read" );

				meshes += KeepIncluded( builder, part, first );

				Claim( builder, layout, part, first );
			}

			if ( meshes == 0 ) return Result.Failed( "the named meshes have nothing to draw" );

			WriteParts( builder, layout );

			KeepListedMorphs( builder, definition );

			//Whatever the project asked to happen to this model, after the art is in and before the
			//whole-model transform, so anything a step adds is moved by that transform with everything else.
			RunSteps( builder, definition );

			if ( definition.OverrideBounds )
				SetBounds( builder, definition.Bounds );

			ModelCollision.Write( builder, definition, sourceDirectory );

			//The whole-model TRS, once, after mesh, skeleton and hulls are in - the ModelTransformOp every compile
			//path calls, so mesh, skeleton and every bone-relative hull move together
			ModelTransformOp.Apply( builder, definition );

			//A lightmap sheet with its islands apart, as engines make one on import; an artist's own is kept
			builder.GenerateLightmapUv( 0 );

			Directory.CreateDirectory( Path.GetDirectoryName( outputPath ) );

			return builder.Save( outputPath ) != 0
				? Result.Ok( meshes )
				: Result.Failed( $"could not write {outputPath}" );
		}
		catch ( Exception e )
		{
			log.Warning( $"compiling {definition.ResourcePath} failed: {e.Message}" );
			return Result.Failed( e.Message );
		}
		finally
		{
			builder.Dispose();
		}
	}

	//Removes meshes a part did not ask for; returns how many remain
	private static int KeepIncluded( CoreAssets.ModelBuilder builder, ModelDefinition.MeshPart part, int first )
	{
		var count = builder.MeshCount;

		if ( part.Include is not { Count: > 0 } ) return count - first;

		var wanted = new List<int>();

		for ( var i = first; i < count; i++ )
		{
			if ( part.Includes( builder.MeshName( i ) ) )
				wanted.Add( i );
		}

		//A filter matching nothing keeps everything: it names a node in the export, and a format with none (an smd) comes
		//back with names their document never mentions - dropping on that draws nothing
		if ( wanted.Count == 0 ) return count - first;

		//Backwards: dropping one moves everything after it down.
		for ( var i = count - 1; i >= first; i-- )
		{
			if ( !wanted.Contains( i ) ) builder.DropMesh( i );
		}

		return wanted.Count;
	}

	//Sets each part mesh's body group bits from the choices that claim the part or the mesh
	private static void Claim( CoreAssets.ModelBuilder builder, ModelDefinition.BodyGroupLayout layout,
		ModelDefinition.MeshPart part, int first )
	{
		if ( !layout.Any ) return;

		var count = builder.MeshCount;

		var whole = layout.ClaimOf( part.Name );

		for ( var i = first; i < count; i++ )
		{
			var claim = whole;

			if ( claim == 0 )
			{
				claim = layout.ClaimOf( builder.MeshName( i ) );
			}

			if ( claim != 0 ) builder.SetMeshGroup( i, claim );
		}
	}

	//Writes the groups themselves, and what the model shows before anybody chooses.
	private static void WriteParts( CoreAssets.ModelBuilder builder, ModelDefinition.BodyGroupLayout layout )
	{
		if ( !layout.Any ) return;

		foreach ( var group in layout.Parts )
		{
			var index = builder.AddPart( group.Name ?? "" );

			if ( index < 0 ) continue;

			foreach ( var choice in group.Choices )
				builder.AddPartChoice( index, choice.Name ?? "", choice.Mask );
		}

		builder.SetDefaultMask( layout.DefaultMask );
	}

	//Removes blend shapes the definition does not list
	private static void KeepListedMorphs( CoreAssets.ModelBuilder builder, ModelDefinition definition )
	{
		var count = builder.MorphCount;
		if ( count <= 0 ) return;

		var listed = definition.Morphs;

		//Backwards: removing one moves everything after it down.
		for ( var i = count - 1; i >= 0; i-- )
		{
			var name = builder.MorphName( i );
			if ( string.IsNullOrWhiteSpace( name ) ) continue;

			var wanted = listed?.Any( morph => morph is { Enabled: true }
				&& string.Equals( morph.Name, name, StringComparison.OrdinalIgnoreCase ) ) ?? false;

			if ( !wanted ) builder.RemoveMorph( name );
		}
	}

	//Runs definition build steps, each in its own try so one failure does not stop the others
	private static void RunSteps( CoreAssets.ModelBuilder builder, ModelDefinition definition )
	{
		if ( definition?.Steps is not { Count: > 0 } steps ) return;

		var build = new ModelBuild( builder, definition.ResourcePath ?? "model" );

		foreach ( var step in steps )
		{
			if ( step is not { Enabled: true } ) continue;

			try
			{
				step.Run( build );
			}
			catch ( Exception e )
			{
				log.Warning( e, $"build step {step.GetType().Name} " +
					$"'{step.Name}' failed on {definition.ResourcePath}: {e.Message}" );
			}
		}
	}

	private static unsafe void SetBounds( CoreAssets.ModelBuilder builder, BBox bounds )
	{
		var box = stackalloc float[6]
		{
			bounds.Mins.x, bounds.Mins.y, bounds.Mins.z,
			bounds.Maxs.x, bounds.Maxs.y, bounds.Maxs.z,
		};

		builder.SetRenderBounds( box );
	}
}
