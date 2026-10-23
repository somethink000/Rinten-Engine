using System;

namespace SceneTests.Components;

[TestClass]
public class TerrainComponentTest
{
	//Builds a small TerrainStorage for component tests: 64x64 heightmap, 6400 world units across (100 units per
	//cell) and 1000 units of height range, so a raw height of ushort.MaxValue maps to exactly 1000 world units.
	static TerrainStorage CreateSmallStorage( bool plateau = false )
	{
		var storage = new TerrainStorage();
		storage.TerrainSize = 6400.0f;
		storage.TerrainHeight = 1000.0f;
		storage.MakeFromHeights( Heights( plateau ), 64 );
		return storage;
	}

	//A 64x64 height map, flat or with the square vertex region [16..47] on both axes raised to the maximum raw
	//height. The region is symmetric so the result is identical regardless of row/column transposition.
	static ushort[] Heights( bool plateau )
	{
		var heights = new ushort[64 * 64];
		if ( !plateau ) return heights;

		for ( int row = 16; row < 48; row++ )
		{
			for ( int col = 16; col < 48; col++ )
			{
				heights[row * 64 + col] = ushort.MaxValue;
			}
		}

		return heights;
	}

	//CompactTerrainMaterial packs base/overlay ids, blend and hole bits exactly and clips per field
	[TestMethod]
	public void CompactMaterialBitPacking()
	{
		var material = new CompactTerrainMaterial( baseTextureId: 3, overlayTextureId: 5, blendFactor: 200, isHole: true );

		Assert.AreEqual( (byte)3, material.BaseTextureId );
		Assert.AreEqual( (byte)5, material.OverlayTextureId );
		Assert.AreEqual( (byte)200, material.BlendFactor );
		Assert.IsTrue( material.IsHole );
		Assert.AreEqual( 3u | (5u << 5) | (200u << 10) | (1u << 18), material.Packed, "Packed layout is base|overlay<<5|blend<<10|hole<<18" );

		var unpacked = new CompactTerrainMaterial( material.Packed );
		Assert.AreEqual( (byte)3, unpacked.BaseTextureId, "Packed value should round trip" );
		Assert.AreEqual( (byte)5, unpacked.OverlayTextureId );
		Assert.AreEqual( (byte)200, unpacked.BlendFactor );
		Assert.IsTrue( unpacked.IsHole );

		unpacked.BaseTextureId = 255;
		Assert.AreEqual( (byte)31, unpacked.BaseTextureId, "Base texture id is 5 bits, writes clip to 31" );
		Assert.AreEqual( (byte)5, unpacked.OverlayTextureId, "Writing one field should not disturb the others" );
		Assert.IsTrue( unpacked.IsHole );

		Assert.AreEqual( 0u, new CompactTerrainMaterial( 0 ).Packed, "Material 0 packs to zero" );
	}

	//A programmatically created TerrainStorage is half a kilometre across and a quarter of one tall, flat, with no materials
	[TestMethod]
	public void StorageDefaults()
	{
		var storage = new TerrainStorage();

		Assert.AreEqual( 500.0f, storage.TerrainSize );
		Assert.AreEqual( 250.0f, storage.TerrainHeight );
		Assert.AreEqual( 0.0f, storage.Mesh.Bounds.Maxs.y, 1e-3f, "The ground starts flat" );
		Assert.AreEqual( 0, storage.Materials.Count, "No materials by default" );
		Assert.IsNotNull( storage.MaterialSettings );
		Assert.IsTrue( storage.MaterialSettings.HeightBlendEnabled, "Height blending defaults on" );
		Assert.AreEqual( 0.87f, storage.MaterialSettings.HeightBlendSharpness, 0.001f );
	}

	//TerrainMaterialSettings raises OnChanged only when a property actually changes value -
	//writing the current value back is silent for the bool, the float and the sampler state.
	[TestMethod]
	public void StorageMaterialSettingsChangeEvents()
	{
		var storage = new TerrainStorage();
		var changes = 0;
		storage.MaterialSettings.OnChanged += () => changes++;

		storage.MaterialSettings.HeightBlendEnabled = true;
		Assert.AreEqual( 0, changes, "Writing the same bool value should not notify" );

		storage.MaterialSettings.HeightBlendEnabled = false;
		Assert.AreEqual( 1, changes, "Changing the bool should notify" );

		storage.MaterialSettings.HeightBlendSharpness = 0.87f;
		Assert.AreEqual( 1, changes, "Writing the same float value should not notify" );

		storage.MaterialSettings.HeightBlendSharpness = 0.25f;
		Assert.AreEqual( 2, changes, "Changing the float should notify" );
	}

	//A terrain's layers are ordinary materials - there is no resource type of their own any more - and the list on the storage is plain in-memory state.
	[TestMethod]
	public void StorageMaterialsListState()
	{
		var storage = new TerrainStorage();

		Assert.AreEqual( 0, storage.Materials.Count, "A new terrain is painted with nothing" );

		var layer = Material.FromShader( "shaders/terrain_layer.shader" );

		storage.Materials.Add( layer );
		Assert.AreEqual( 1, storage.Materials.Count );
		Assert.AreSame( layer, storage.Materials[0] );
	}

	//A terrain whose mesh exists saves the mesh and nothing else: what was sculpted into it
	//- here a dab that lifts the middle - comes back from the blob, triangle for triangle.
	[TestMethod]
	public void StorageMeshSerializationRoundTrip()
	{
		var storage = new TerrainStorage();
		storage.TerrainSize = 320.0f;
		storage.TerrainHeight = 100.0f;
		storage.MakeFromHeights( new ushort[32 * 32], 32 );

		var mesh = storage.Mesh;
		Assert.IsNotNull( mesh );

		mesh.BeginEdit();
		mesh.Displace( new Vector3( 160, 0, 160 ), 60.0f, Vector3.Up, 25.0f );
		mesh.Remesh( new Vector3( 160, 0, 160 ), 60.0f, storage.EdgeLength );
		mesh.EndEdit();

		var json = storage.Serialize().ToJsonString();

		var loaded = new TerrainStorage();
		loaded.BinaryData = storage.BinaryData;
		loaded.LoadFromJson( json );

		Assert.AreEqual( mesh.TriangleCount, loaded.Mesh.TriangleCount, "Every triangle comes back" );
		Assert.AreEqual( mesh.VertexCount, loaded.Mesh.VertexCount );
		Assert.AreEqual( mesh.Bounds.Maxs.y, loaded.Mesh.Bounds.Maxs.y, 1e-3f, "And the sculpted rise with them" );
		Assert.IsTrue( loaded.Mesh.Bounds.Maxs.y > 20.0f, "The rise is there" );
	}

	//Disabled Terrain defaults and clamps; size properties only work once a storage is assigned
	[TestMethod]
	public void ComponentDefaultsAndClampsWhileDisabled()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var terrain = go.Components.Create<Terrain>( false );

		Assert.IsTrue( terrain.IsConcave, "Terrain is always concave" );
		Assert.IsTrue( terrain.Static, "Concave colliders are forced static" );
		terrain.Static = false;
		Assert.IsTrue( terrain.Static, "The static flag cannot be turned off on a concave collider" );

		Assert.IsNull( terrain.Storage );
		Assert.IsTrue( terrain.EnableCollision );
		Assert.AreEqual( ModelRenderer.ShadowRenderType.Off, terrain.RenderType, "Terrain defaults to not casting shadows" );
		Assert.AreEqual( 32, terrain.ChunkSize );

		Assert.AreEqual( 0.0f, terrain.TerrainSize, "Size reads zero with no storage" );
		Assert.AreEqual( 0.0f, terrain.TerrainHeight );
		terrain.TerrainSize = 5000.0f;
		Assert.AreEqual( 0.0f, terrain.TerrainSize, "Size writes are ignored with no storage" );

		terrain.ChunkSize = 4;
		Assert.AreEqual( 16, terrain.ChunkSize, "Chunk size clamps to a minimum of 16" );
		terrain.ChunkSize = 4096;
		Assert.AreEqual( 128, terrain.ChunkSize, "Chunk size clamps to a maximum of 128" );


		var storage = CreateSmallStorage();
		terrain.Storage = storage;

		Assert.AreEqual( 6400.0f, terrain.TerrainSize, "Size reads through to the storage" );
		Assert.AreEqual( 1000.0f, terrain.TerrainHeight );
		terrain.TerrainSize = 1234.0f;
		terrain.TerrainHeight = 777.0f;
		Assert.AreEqual( 1234.0f, storage.TerrainSize, "Size writes through to the storage" );
		Assert.AreEqual( 777.0f, storage.TerrainHeight );

		go.Destroy();
		scene.ProcessDeletes();
	}

	//Terrain settings round-trip; storage made in code has no path and comes back as null
	[TestMethod]
	public void ComponentSerializationRoundTrip()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var mat = Material.Load( "materials/default/white.vmat" );

		var go = scene.CreateObject();
		var terrain = go.Components.Create<Terrain>( false );
		terrain.Storage = CreateSmallStorage();
		terrain.EnableCollision = false;
		terrain.ChunkSize = 64;
		terrain.RenderType = ModelRenderer.ShadowRenderType.ShadowsOnly;
		terrain.MaterialOverride = mat;
		terrain.Friction = 0.25f;

		var clone = RenderTestUtility.SerializeRoundTrip( scene, go );
		var loaded = clone.Components.Get<Terrain>( true );

		Assert.IsNotNull( loaded, "Deserialized GameObject should have a Terrain" );
		Assert.IsFalse( loaded.Enabled, "The disabled state should round trip" );
		Assert.IsNull( loaded.Storage, "A pathless programmatic storage serializes as null and does not survive" );
		Assert.IsFalse( loaded.EnableCollision );
		Assert.AreEqual( 64, loaded.ChunkSize );
		Assert.AreEqual( ModelRenderer.ShadowRenderType.ShadowsOnly, loaded.RenderType );
		Assert.AreEqual( mat.Name, loaded.MaterialOverride?.Name, "Material override should round trip by path" );
		Assert.AreEqual( 0.25f, loaded.Friction );

		clone.Destroy();
		scene.ProcessDeletes();
	}

	//Enabling bakes chunk meshes into static mesh colliders, inset half a cell; disabling removes them
	[TestMethod]
	public void EnableWithStorageBuildsMeshCollider()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var storage = CreateSmallStorage( plateau: true );

		var go = scene.CreateObject();
		var terrain = go.Components.Create<Terrain>( false );
		terrain.Storage = storage;

		Assert.AreEqual( 0, terrain.Shapes.Count, "No shapes while disabled" );

		terrain.Enabled = true;

		Assert.IsTrue( terrain.Active );
		Assert.IsTrue( terrain.ChunkCount > 1, "The ground is baked in chunks" );
		Assert.AreEqual( 0, terrain.ChunksWaiting, "The first bake is done before enabling returns - a level's floor has to be there on its first frame" );
		Assert.IsTrue( terrain.Shapes.Count > 1, "A mesh shape a chunk that has ground" );

		foreach ( var shape in terrain.Shapes )
		{
			Assert.IsTrue( shape.IsValid() );
			Assert.AreSame( terrain, shape.Collider, "Every shape links back to the terrain collider" );
			Assert.IsFalse( shape.IsTrigger );
		}

		Assert.IsTrue( terrain.KeyBody.IsValid(), "Terrain collides through a keyframe body" );
		Assert.AreEqual( PhysicsBodyType.Static, terrain.KeyBody.BodyType, "A concave collider is a static body" );

		//64 cells of 100 units, each vertex in the middle of its cell.
		Assert.AreEqual( 50.0f, terrain.LocalBounds.Mins.x, 1.0f );
		Assert.AreEqual( 0.0f, terrain.LocalBounds.Mins.y, 1.0f );
		Assert.AreEqual( 50.0f, terrain.LocalBounds.Mins.z, 1.0f );
		Assert.AreEqual( 6350.0f, terrain.LocalBounds.Maxs.x, 1.0f );
		Assert.AreEqual( 1000.0f, terrain.LocalBounds.Maxs.y, 1.0f, "The plateau reaches the full terrain height" );
		Assert.AreEqual( 6350.0f, terrain.LocalBounds.Maxs.z, 1.0f );

		scene.GameTick();

		Assert.IsTrue( terrain.Shapes.All( x => x.IsValid() ), "The shapes survive ticking" );

		terrain.Enabled = false;

		Assert.AreEqual( 0, terrain.Shapes.Count, "Disabling should destroy the shapes" );
		Assert.IsNull( terrain.KeyBody, "Disabling should destroy the keyframe body" );
		Assert.AreEqual( 0, terrain.ChunkCount, "And the chunks" );

		go.Destroy();
		scene.ProcessDeletes();
	}

	//A dab of a brush reaches what the terrain collides with: the chunks under it are baked again on the workers, and as each comes back its shape is
	//swapped for the new one. Turning collision off and on builds the shapes from the mesh as it is, and undoing the dab lays the ground as it was.
	[TestMethod]
	public void SculptReachesColliderWhenChunksRebake()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var storage = CreateSmallStorage();

		var go = scene.CreateObject();
		var terrain = go.Components.Create<Terrain>( false );
		terrain.Storage = storage;
		terrain.Enabled = true;

		Assert.AreEqual( 0.0f, terrain.LocalBounds.Maxs.y, 1.0f, "A flat terrain has no height extent" );

		var mesh = terrain.Mesh;
		mesh.BeginEdit();
		mesh.Displace( new Vector3( 3200, 0, 3200 ), 800.0f, Vector3.Up, 500.0f );
		var edit = mesh.EndEdit();

		Assert.IsNotNull( edit, "The dab moved the ground" );
		Assert.IsTrue( terrain.ChunksWaiting > 0, "The chunks under it are waiting to be baked again" );

		var clock = System.Diagnostics.Stopwatch.StartNew();
		while ( terrain.ChunksWaiting > 0 && clock.Elapsed.TotalSeconds < 30 )
		{
			scene.GameTick();
			System.Threading.Thread.Sleep( 5 );
		}

		Assert.AreEqual( 0, terrain.ChunksWaiting, "They were" );
		Assert.AreEqual( 500.0f, terrain.LocalBounds.Maxs.y, 5.0f, "What is collided with rose with the ground" );

		terrain.EnableCollision = false;
		Assert.AreEqual( 0, terrain.Shapes.Count, "Turning collision off destroys the shapes" );

		terrain.EnableCollision = true;
		Assert.IsTrue( terrain.Shapes.Count > 1, "Turning it back on builds them from the mesh as it is" );
		Assert.AreEqual( 500.0f, terrain.LocalBounds.Maxs.y, 5.0f );

		mesh.Undo( edit );
		Assert.AreEqual( 0.0f, mesh.Bounds.Maxs.y, 1e-3f, "Undoing the dab lays the ground flat again" );

		go.Destroy();
		scene.ProcessDeletes();
	}

	//RayIntersects hits the terrain mesh in local space (height on Y) without enabling the component
	[TestMethod]
	public void RayIntersectsReportsLocalMeshHits()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var storage = CreateSmallStorage( plateau: true );

		var go = scene.CreateObject();
		var terrain = go.Components.Create<Terrain>( false );
		terrain.Storage = storage;

		//Straight down onto the middle of the plateau.
		var hitPlateau = terrain.RayIntersects( new Ray( new Vector3( 3200, 1500, 3200 ), Vector3.Down ), 4000.0f, out var plateauPosition );

		Assert.IsTrue( hitPlateau, "A downward ray over the plateau should hit" );
		Assert.AreEqual( 3200.0f, plateauPosition.x, 1.0f, "A vertical ray keeps its horizontal position" );
		Assert.AreEqual( 3200.0f, plateauPosition.z, 1.0f );
		Assert.AreEqual( 1000.0f, plateauPosition.y, 1.0f, "The plateau surface sits at the full terrain height" );

		//Straight down onto the flat base region away from the plateau.
		var hitBase = terrain.RayIntersects( new Ray( new Vector3( 800, 1500, 800 ), Vector3.Down ), 4000.0f, out var basePosition );

		Assert.IsTrue( hitBase, "A downward ray over the base should hit" );
		Assert.AreEqual( 800.0f, basePosition.x, 1.0f );
		Assert.AreEqual( 800.0f, basePosition.z, 1.0f );
		Assert.AreEqual( 0.0f, basePosition.y, 1.0f, "The untouched base sits at height zero" );

		//Moving the GameObject up moves the surface in world space, but the reported position stays local to the terrain.
		go.WorldPosition = new Vector3( 0, 500, 0 );

		var hitElevated = terrain.RayIntersects( new Ray( new Vector3( 3200, 2500, 3200 ), Vector3.Down ), 4000.0f, out var elevatedPosition );

		Assert.IsTrue( hitElevated, "The ray accounts for the GameObject transform" );
		Assert.AreEqual( 1000.0f, elevatedPosition.y, 1.0f, "The hit position is local to the terrain, not world space" );

		go.Destroy();
		scene.ProcessDeletes();
	}

	//RayIntersects guard rails: no storage means a quiet miss, and a ray cast away from the terrain misses
	[TestMethod]
	public void RayIntersectsGuardsAgainstBadStorage()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var terrain = go.Components.Create<Terrain>( false );

		Assert.IsFalse( terrain.RayIntersects( new Ray( new Vector3( 100, 100, 1000 ), Vector3.Down ), 2000.0f, out _ ), "No storage should be a quiet miss" );

		var storage = CreateSmallStorage();
		terrain.Storage = storage;

		Assert.IsFalse( terrain.RayIntersects( new Ray( new Vector3( 3200, 3200, 1500 ), Vector3.Up ), 4000.0f, out _ ), "A ray cast away from the terrain should miss" );

		go.Destroy();
		scene.ProcessDeletes();
	}

	//FindClosestPoint snaps down onto the mesh surface instead of returning the body origin
	[TestMethod]
	public void FindClosestPointSnapsToMeshSurface()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var storage = CreateSmallStorage( plateau: true );

		var go = scene.CreateObject();
		var terrain = go.Components.Create<Terrain>( false );
		terrain.Storage = storage;
		terrain.Enabled = true;

		Assert.IsTrue( terrain.KeyBody.IsValid(), "The terrain needs a keyframe body to query against" );

		var overPlateau = terrain.FindClosestPoint( new Vector3( 3150, 5000, 3150 ) );

		Assert.AreEqual( 3150.0f, overPlateau.x, 75.0f, "The closest point keeps the query's ground position" );
		Assert.AreEqual( 1000.0f, overPlateau.y, 25.0f, "The closest point sits on the plateau surface, not the body origin" );
		Assert.AreEqual( 3150.0f, overPlateau.z, 75.0f );

		var overBase = terrain.FindClosestPoint( new Vector3( 800, 300, 800 ) );

		Assert.AreEqual( 800.0f, overBase.x, 75.0f );
		Assert.AreEqual( 0.0f, overBase.y, 25.0f, "The flat base sits at height zero" );
		Assert.AreEqual( 800.0f, overBase.z, 75.0f );

		Assert.AreNotEqual( overPlateau, overBase, "Distinct queries resolve to distinct surface points, not one constant" );

		go.Destroy();
		scene.ProcessDeletes();
	}
}
