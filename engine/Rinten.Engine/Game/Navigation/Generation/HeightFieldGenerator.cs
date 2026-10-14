using NativeEngine;
using System.Buffers;
using System.Runtime.InteropServices;

namespace Rinten.Navigation.Generation;

[SkipHotload]
class HeightFieldGenerator : IDisposable
{
	internal Vector3[] inputGeoVertices = null;
	internal int[] inputGeoIndices = null;
	internal int inputGeoVerticesCount = 0;
	internal int inputGeoIndicesCount = 0;

	float inputGeometryMaxY;
	float inputGeometryMinY;

	Config cfg;

	Heightfield cachedHeightField;

	//Reused for every shape of every tile: the geometry is copied straight out into
	//the pooled arrays below, so these only ever have to be big enough for one shape.
	readonly List<Vector3> triangulationVertCache = new( 512 );
	readonly List<int> triangulationIndexCache = new( 1024 );

	//Every shape in the tile, from one query to the next.
	readonly List<PhysicsShape> queryResults = new( 64 );

	public bool IsEmpty => inputGeoVerticesCount < 3 || inputGeoIndicesCount < 3;

	public void Dispose()
	{
		cachedHeightField?.Dispose();
		cachedHeightField = null;

		if ( inputGeoVertices != null )
		{
			ArrayPool<Vector3>.Shared.Return( inputGeoVertices );
			inputGeoVertices = null;
		}

		if ( inputGeoIndices != null )
		{
			ArrayPool<int>.Shared.Return( inputGeoIndices );
			inputGeoIndices = null;
		}
	}

	public void Init( Config config )
	{
		inputGeoVertices ??= ArrayPool<Vector3>.Shared.Rent( 2048 );
		inputGeoIndices ??= ArrayPool<int>.Shared.Rent( 4096 );
		inputGeoVerticesCount = 0;
		inputGeoIndicesCount = 0;
		inputGeometryMaxY = float.MinValue;
		inputGeometryMinY = float.MaxValue;
		cfg = config;
	}

	public void CollectGeometry( NavMesh navMesh, PhysicsWorld world, BBox tileBoundsWorld )
	{
		ThreadSafe.AssertIsMainThread();

		if ( !world.IsValid() )
			return;

		queryResults.Clear();
		world.Backend.OverlapBox( tileBoundsWorld, queryResults );

		//clear arrays
		inputGeoVerticesCount = 0;
		inputGeoIndicesCount = 0;

		foreach ( var shape in queryResults )
		{
			if ( !shape.IsValid() )
				continue;

			var body = shape.Body;
			if ( !body.IsValid() )
				continue;

			if ( !navMesh.IsBodyRelevantForNavmesh( body ) )
				continue;

			AddGeometryFromPhysicsShape( shape );
		}
	}

	internal void AddGeometryFromPhysicsShape( PhysicsShape shape )
	{
		triangulationVertCache.Clear();
		triangulationIndexCache.Clear();

		shape.Backend.AppendTriangulation( cfg.Bounds, triangulationVertCache, triangulationIndexCache );

		if ( inputGeoVerticesCount + triangulationVertCache.Count > inputGeoVertices.Length )
		{
			var newVerts = ArrayPool<Vector3>.Shared.Rent( (inputGeoVerticesCount + triangulationVertCache.Count) * 2 );
			Array.Copy( inputGeoVertices, newVerts, inputGeoVerticesCount );
			ArrayPool<Vector3>.Shared.Return( inputGeoVertices );
			inputGeoVertices = newVerts;
		}
		if ( inputGeoIndicesCount + triangulationIndexCache.Count > inputGeoIndices.Length )
		{
			var newIndices = ArrayPool<int>.Shared.Rent( (inputGeoIndicesCount + triangulationIndexCache.Count) * 2 );
			Array.Copy( inputGeoIndices, newIndices, inputGeoIndicesCount );
			ArrayPool<int>.Shared.Return( inputGeoIndices );
			inputGeoIndices = newIndices;
		}

		int startV = inputGeoVerticesCount;

		//As the shape winds them: the world is Y-up already, so a floor's normal points up as InputFilter asks
		for ( int i = 0; i < triangulationIndexCache.Count; i++ )
			inputGeoIndices[inputGeoIndicesCount++] = triangulationIndexCache[i] + startV;

		//Transform positions added to the vertex data
		var bodyTransform = shape.Body.Transform;
		foreach ( var localPos in triangulationVertCache )
		{
			var vertexWorldPos = bodyTransform.PointToWorld( localPos );

			if ( vertexWorldPos.y > inputGeometryMaxY ) inputGeometryMaxY = vertexWorldPos.y;
			if ( vertexWorldPos.y < inputGeometryMinY ) inputGeometryMinY = vertexWorldPos.y;

			inputGeoVertices[inputGeoVerticesCount++] = vertexWorldPos;
		}
	}

	public CompactHeightfield Generate()
	{
		var nverts = inputGeoVerticesCount;
		var ntris = inputGeoIndicesCount / 3;

		//Nothing todo bail
		if ( nverts < 3 || ntris == 0 )
		{
			return null;
		}

		//Ideally we should also never arrive here
		if ( cfg.TileSizeXY == 0 )
		{
			Log.Warning( "buildNavigation: Empty tile provided.\n" );
			//Bail nothing more todo
			return null;
		}

		//The geometry may not fully cover the vertical space of the tile. By shrinking the bounds we can avoid
		//precissions issues due to large tile height. Make sure we are at least couple voxels high though
		cfg.Bounds.Maxs.y = inputGeometryMaxY + cfg.CellHeight * 2; //add alittle bit extra to avoid precission issues
		cfg.Bounds.Mins.y = inputGeometryMinY - cfg.CellHeight * 2; //add alittle bit extra to avoid precission issues

		//Set the area where the navigation will be build.
		var minBounds = cfg.Bounds.Mins;
		var maxBounds = cfg.Bounds.Maxs;

		//Step 2. Rasterize input polygon soup.

		//Allocate voxel heightfield where we rasterize our input data to.
		if ( cachedHeightField == null )
		{
			cachedHeightField = new( cfg.TileSizeXY, cfg.TileSizeXY, minBounds, maxBounds, cfg.CellSize, cfg.CellHeight );
		}
		else
		{
			//Reuse memory
			cachedHeightField.Init( cfg.TileSizeXY, cfg.TileSizeXY, minBounds, maxBounds, cfg.CellSize, cfg.CellHeight );
		}


		using var pooledTriAreas = new PooledSpan<int>( ntris );
		var triAreas = pooledTriAreas.Span;
		InputFilter.MarkWalkableTriangles( cfg.WalkableSlopeAngle, inputGeoVertices.AsSpan( 0, inputGeoVerticesCount ), inputGeoIndices.AsSpan( 0, inputGeoIndicesCount ), triAreas );

		Rasterization.RasterizeTriangles( inputGeoVertices.AsSpan( 0, inputGeoVerticesCount ), inputGeoIndices.AsSpan( 0, inputGeoIndicesCount ), triAreas, cachedHeightField, cfg.WalkableClimb );

		cachedHeightField.EnsureCompressed();

		if ( cachedHeightField.TotalSpanCount == 0 )
		{
			return null;
		}

		//Step 3. Filter walkables surfaces.

		//Once all geometry is rasterized, we do initial pass of filtering to remove unwanted overhangs caused
		//by the conservative rasterization as well as filter spans where the character cannot possibly stand.
		SpanFilter.Filter( cfg.WalkableHeight, cfg.WalkableClimb, cachedHeightField );

		//Step 4. Partition walkable surface to simple regions.

		//Compact the heightfield so that it is faster to handle from now on. This will result more
		//cache coherent data as well as the neighbours between walkable cells will be calculated.
#pragma warning disable CA2000 //Dispose objects before losing scope
		//chf is returned by this function, caller takes ownership
		var chf = cachedHeightField.BuildCompactHeightfield( cfg.WalkableHeight, cfg.WalkableClimb );
#pragma warning restore CA2000 //Dispose objects before losing scope

		//Erode the walkable area by agent radius.
		AreaFilter.ErodeWalkableArea( cfg.WalkableRadius, chf );

		return chf;
	}
}
