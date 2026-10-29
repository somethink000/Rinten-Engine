namespace Rinten;

public sealed partial class ModelBuilder
{
	//Applies a body group choice to every core mesh built from index `from` onwards
	private static void Mark( CoreAssets.ModelBuilder builder, int from, ulong group )
	{
		//"Every choice" is what a mesh nobody marked already means, and the format says that with nought.
		if ( group == ulong.MaxValue ) group = 0;

		if ( group == 0 ) return;

		var to = builder.MeshCount;

		for ( var i = from; i < to; i++ )
		{
			if ( group != 0 ) builder.SetMeshGroup( i, group );
		}
	}

	//Writes body groups and LOD switches; group bits come from the same choice list as mesh marks
	private void WriteGroups( CoreAssets.ModelBuilder builder )
	{
		if ( meshGroups.Count > 0 )
		{
			var byName = new Dictionary<string, int>( StringComparer.Ordinal );

			for ( var i = 0; i < meshGroups.Count; i++ )
			{
				var entry = meshGroups[i];

				var at = entry.LastIndexOf( "_@", StringComparison.Ordinal );
				if ( at <= 0 ) continue;

				var group = entry[..at];

				if ( !byName.TryGetValue( group, out var part ) )
				{
					part = builder.AddPart( group );
					if ( part < 0 ) continue;

					byName[group] = part;
				}

				builder.AddPartChoice( part, entry[( at + 2 )..], 1UL << i );
			}

			builder.SetDefaultMask( DefaultMeshGroupMask );
		}

	}

	//What the model is called, where the caller never said.
	private string Named => string.IsNullOrWhiteSpace( modelName ) ? "runtime_model" : modelName;

	//Core builder with all added meshes, or null if empty; pure, so it can run on a worker thread
	private unsafe CoreAssets.ModelBuilder Filled( out List<CoreMesh> foreign )
	{
		var builder = new CoreAssets.ModelBuilder();
		var kept = false;

		foreign = new List<CoreMesh>();

		try
		{
			var name = Named;
			builder.SetName( name );

			if ( clusterLevels > 0 ) builder.SetClusterLevels( clusterLevels );
			builder.SetLockBorder( lockedBorder );

			var bounds = new BBox();
			var any = false;

			//Meshes with no position attribute go on the model raw for their own shader; others are converted
			for ( var m = 0; m < meshes.Count; m++ )
			{
				var mesh = meshes[m];

				if ( mesh?.core is not { } source ) continue;

				//What the caller said about this one, if anything. Applied to whatever it turns into rather than to
				//itself: a mesh split by material is several draw calls here, and they all belong to the same choice.
				var groupMask = m < bodyGroups.Count ? bodyGroups[m] : 0ul;

				var before = builder.MeshCount;

				if ( !source.IsEngineVertex && !source.HasPositions )
				{
					foreign.Add( source );

					//Its own bounds, which the caller set, because there are no positions here to work them out from.
					bounds = any ? bounds.AddBBox( source.Bounds ) : source.Bounds;
					any = true;

					continue;
				}

				var vertices = source.AsEngineVertices();
				if ( vertices.Length == 0 ) continue;

				//Nothing is scaled: a mesh handed to the builder is already in world units, and the core counts in the same
				//ones. See <see cref="Units"/> - the only conversions left are in the importers that read somebody else's file.
				for ( var i = 0; i < vertices.Length; i++ )
				{
					bounds = any || i > 0
						? bounds.AddPoint( vertices[i].Position )
						: BBox.FromPositionAndSize( vertices[i].Position, 0 );
				}

				any = true;

				int firstVertex;
				fixed ( Vertex* v = vertices )
					firstVertex = builder.AddVertices( v, vertices.Length );

				AddMorphs( builder, source, firstVertex );

				//A mesh with no indices draws its vertices in order, which is the same thing as the indices nought upwards.
				var indices = source.Indices ?? Sequence( vertices.Length );

				int firstIndex;
				fixed ( int* i = indices )
					firstIndex = builder.AddIndices( (uint*)i, indices.Length );

				if ( source.SubMeshes.Count == 0 )
				{
					builder.AddMesh( source.Name ?? name, MaterialName( source.Material ), firstIndex, indices.Length, firstVertex, vertices.Length );
				}
				else
				{
					//A mesh split into ranges with materials of their own is that many meshes to us: the model
					//format has one material per mesh, which is the same picture drawn the same number of times.
					foreach ( var part in source.SubMeshes )
					{
						var count = part.IndexCount > 0 ? part.IndexCount : indices.Length;

						builder.AddMesh( source.Name ?? name, MaterialName( part.Material ?? source.Material ), firstIndex + part.StartIndex, count, firstVertex + part.StartVertex, part.VertexCount > 0 ? part.VertexCount : vertices.Length );
					}
				}

				Mark( builder, before, groupMask );
			}

			WriteGroups( builder );

			if ( !any ) return null;

			//All-foreign model still needs one degenerate triangle to be valid; it is never drawn
			if ( foreign.Count > 0 && builder.MeshCount == 0 )
			{
				var placeholder = new Vertex[3];

				for ( var i = 0; i < placeholder.Length; i++ )
				{
					placeholder[i].Position = bounds.Center;
					placeholder[i].Color = Color32.White;
					placeholder[i].Normal = Vector3.Up;
				}

				int placeholderVertex;
				fixed ( Vertex* v = placeholder )
					placeholderVertex = builder.AddVertices( v, placeholder.Length );

				var placeholderIndices = Sequence( placeholder.Length );

				int placeholderIndex;
				fixed ( int* i = placeholderIndices )
					placeholderIndex = builder.AddIndices( (uint*)i, placeholderIndices.Length );

				builder.AddMesh( name, "", placeholderIndex, placeholderIndices.Length, placeholderVertex, placeholder.Length );
			}

			var minMax = stackalloc float[6]
			{
				bounds.Mins.x, bounds.Mins.y, bounds.Mins.z,
				bounds.Maxs.x, bounds.Maxs.y, bounds.Maxs.z,
			};

			builder.SetRenderBounds( minMax );
			builder.SetPhysicsBounds( minMax );

			//Named shapes are the collision; without any, the drawn triangles are, so a brush can be stood on
			var surface = string.IsNullOrWhiteSpace( surfaceProperty ) ? "default" : surfaceProperty;

			if ( HasNamedCollision )
				AddNamedCollision( builder, surface );
			else if ( collides )
				builder.CollisionFromMesh( 1, surface, mass );

			kept = true;
			return builder;
		}
		finally
		{
			if ( !kept ) builder.Dispose();
		}
	}

	private unsafe Model CreateOnCore()
	{
		var builder = Filled( out var foreign );
		if ( builder is null ) return Model.Error;

		try
		{
			var made = Model.FromCore( CoreModel.FromBuilder( builder, Named ) ) ?? Model.Error;

			//And the ones the format could not hold, carried on the model beside what it could.
			if ( foreign.Count > 0 && made != Model.Error ) made.pulled = foreign.ToArray();

			return made;
		}
		finally
		{
			builder.Dispose();
		}
	}

	//Nought upwards, for a mesh that has no indices of its own.
	private static int[] Sequence( int count )
	{
		var indices = new int[count];
		for ( var i = 0; i < count; i++ ) indices[i] = i;

		return indices;
	}

	//What a mesh's material is called, as the model format records it - a path, which is looked up when the model is drawn.
	private static string MaterialName( Material material )
		=> material?.ResourcePath ?? material?.Name ?? "";

}
