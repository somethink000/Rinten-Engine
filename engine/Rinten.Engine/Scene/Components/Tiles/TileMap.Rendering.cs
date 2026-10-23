using Rinten.Rendering;
using System.Runtime.InteropServices;

namespace Rinten.Tiles;

public sealed partial class TileMap
{
	//One chunk of one layer as it is drawn: a sprite record a tile, and - outside the portable frame
	//- the buffer they were uploaded into, which is written when the chunk changes and not otherwise.
	private sealed class DrawnChunk
	{
		public SpriteBatchSceneObject.SpriteData[] Tiles;
		public int Count;
		public GpuBuffer<SpriteBatchSceneObject.SpriteData> Buffer;
		public bool Opaque;
		public int Order;
		public int ZIndex;
		public BBox Bounds;

		public void Release()
		{
			Buffer?.Dispose();
			Buffer = null;
		}
	}

	private readonly Dictionary<(TileMapLayer Layer, Vector2Int Chunk), DrawnChunk> drawn = new();

	//Chunks whose cells changed since they were last built.
	private readonly HashSet<(TileMapLayer Layer, Vector2Int Chunk)> dirty = new();

	//The tile set revision each layer was last built with, to notice a set being edited.
	private readonly Dictionary<TileMapLayer, (TileSet Set, int Revision)> builtWith = new();

	//What draws the chunks, one a z-index the map's layers use, so each is drawn in its place among the sprites - see TileMapSceneObject.
	private readonly Dictionary<int, TileMapSceneObject> sceneObjects = new();

	//Where each layer stands towards the viewer: its place among the layers in z-index order, the list's order within one z-index.
	private readonly Dictionary<TileMapLayer, int> ranks = new();

	//The chunks in the order they are drawn: lowest z-index first, and within one the back layer first and the ones that blend last.
	private readonly List<DrawnChunk> drawOrder = new();
	private bool drawOrderDirty;

	//The quad every tile is: four corners, six indices - see sprite/sprite.slang's Corner.
	private static GpuBuffer<int> QuadIndices
	{
		get
		{
			if ( field.IsValid() ) return field;

			field = new GpuBuffer<int>( 6, GpuBuffer.UsageFlags.Index );
			field.SetData( [0, 1, 2, 0, 2, 3] );

			return field;
		}
	}

	//What every tile is drawn with - the picture is the record's, so one material serves every set.
	private static Material TileMaterial => field ??= Material.FromShader( TileSet.TileShader );

	private void RefreshTileSets()
	{
		foreach ( var layer in Layers ?? [] )
		{
			if ( layer is null ) continue;

			var now = (layer.TileSet, layer.TileSet?.Revision ?? -1);

			if ( builtWith.TryGetValue( layer, out var was ) && was == now ) continue;

			builtWith[layer] = now;

			//The first sight of a layer is not a change: Attach has already asked for all of it.
			if ( was.Set is not null || was.Revision != 0 ) RebuildLayer( layer );

			collisionDirty = true;
		}
	}

	//Build every chunk that changed. A frame in which nothing did costs a look at an empty set.
	private void BuildDirtyChunks()
	{
		if ( dirty.Count == 0 ) return;

		var world = Scene?.SceneWorld;
		if ( world is null || Application.IsHeadless ) return;

		RankLayers();

		var transform = WorldTransform;

		foreach ( var (layer, coord) in dirty )
		{
			if ( !ranks.TryGetValue( layer, out var rank ) ) continue;

			BuildChunk( layer, rank, coord, transform );
		}

		dirty.Clear();
		drawOrderDirty = true;

		UpdateSceneObjects( world );
	}

	private void RankLayers()
	{
		ranks.Clear();

		var layers = Layers;
		if ( layers is null ) return;

		//OrderBy keeps the list's order among layers of one z-index.
		var rank = 0;

		foreach ( var layer in layers.Where( l => l is not null ).OrderBy( l => l.ZIndex ) )
		{
			if ( ranks.TryAdd( layer, rank ) ) rank++;
		}
	}

	private void BuildChunk( TileMapLayer layer, int rank, Vector2Int coord, Transform transform )
	{
		var key = (layer, coord);

		drawn.TryGetValue( key, out var chunkDrawn );

		var set = layer.TileSet;
		var chunk = layer.Grid.ChunkAt( coord, false );

		if ( !layer.Visible || set?.Sheet is null || chunk is null || chunk.Filled == 0 )
		{
			if ( chunkDrawn is not null )
			{
				chunkDrawn.Release();
				drawn.Remove( key );
			}

			return;
		}

		chunkDrawn ??= drawn[key] = new DrawnChunk();

		BuildTiles( chunkDrawn, set, layer, chunk, rank * LayerSpacing, transform );

		chunkDrawn.Opaque = layer.Tint.a >= 1.0f;
		chunkDrawn.Order = rank;
		chunkDrawn.ZIndex = layer.ZIndex;

		if ( chunkDrawn.Count == 0 )
		{
			chunkDrawn.Release();
			drawn.Remove( key );
			return;
		}

		//The portable frame hands the records over with each draw, a run at a time - it has no buffer a
		//shader may read. Everywhere else they live on the card, written now and read every frame after.
		if ( CoreRenderDevice.PortableFrame ) return;

		if ( chunkDrawn.Buffer is null || chunkDrawn.Buffer.ElementCount < chunkDrawn.Count )
		{
			chunkDrawn.Buffer?.Dispose();
			chunkDrawn.Buffer = new GpuBuffer<SpriteBatchSceneObject.SpriteData>( chunkDrawn.Count );
		}

		chunkDrawn.Buffer.SetData( chunkDrawn.Tiles.AsSpan( 0, chunkDrawn.Count ) );
	}

	//A chunk's cells as sprite records, standing where the map puts them in the world: each the picture of its tile, turned and flipped
	//as its cell says, tinted by its tile and its layer, and - where it is animated - with its frames for tile_ps.shader to step through.
	private void BuildTiles( DrawnChunk into, TileSet set, TileMapLayer layer, TileGrid.Chunk chunk, float depth, Transform transform )
	{
		var sheet = set.Sheet;
		var width = (float)Math.Max( sheet.Width, 1 );
		var height = (float)Math.Max( sheet.Height, 1 );
		var picture = sheet.SpriteIndex;

		if ( into.Tiles is null || into.Tiles.Length < chunk.Filled )
			into.Tiles = new SpriteBatchSceneObject.SpriteData[Math.Max( chunk.Filled, 16 )];

		var origin = TileGrid.OriginOf( chunk.Coord );
		var size = CellSize;
		var half = new Vector2( size.x * 0.5f * transform.Scale.x, size.y * 0.5f * transform.Scale.y );
		var step = ( set.TileSize.x + set.Spacing.x ) / width;
		var cutout = SpriteBatchSceneObject.SpriteData.PackFogAndAlphaCutout( 0.0f, AlphaCutoff );

		//The four ways a tile can be turned, as the angles the record carries - worked out once a chunk rather than once a tile.
		Span<Vector3> turned = stackalloc Vector3[4];

		for ( var t = 0; t < 4; t++ )
			turned[t] = ( transform.Rotation * Rotation.FromAxis( Vector3.Backward, 90.0f * t ) ).Angles().AsVector3();

		var count = 0;

		for ( var i = 0; i < chunk.Cells.Length; i++ )
		{
			var cell = chunk.Cells[i];
			if ( cell.IsEmpty || !set.Has( cell.Tile ) ) continue;

			var info = set.Info( cell.Tile );
			var region = set.Region( cell.Tile );

			var flags = SpriteBatchSceneObject.SpriteFlags.Pixelated;
			if ( ( ( cell.Flip & TileFlip.X ) == TileFlip.X ) ) flags |= SpriteBatchSceneObject.SpriteFlags.FlipX;
			if ( ( ( cell.Flip & TileFlip.Y ) == TileFlip.Y ) ) flags |= SpriteBatchSceneObject.SpriteFlags.FlipY;

			var tint = ( info?.Tint ?? Color.White ) * layer.Tint;

			//Past white, the tint goes as a sprite's does: RGBE, its exponent in the top half of Lighting - so a layer
			//or a tile can be bright enough for the bloom to pick it out. Within white it stays four plain bytes.
			var bright = tint.r > 1.0f || tint.g > 1.0f || tint.b > 1.0f;
			var alpha = Math.Clamp( tint.a, 0.0f, 1.0f );
			var rgbe = bright ? tint.ToRgbe() : default;
			var frames = Math.Max( info?.Frames ?? 1, 1 );

			var x = ( origin.x + ( i % TileGrid.ChunkSize ) + 0.5f ) * size.x;
			var y = ( origin.y + ( i / TileGrid.ChunkSize ) + 0.5f ) * size.y;

			into.Tiles[count++] = new SpriteBatchSceneObject.SpriteData
			{
				Position = transform.PointToWorld( new Vector3( x, y, depth ) ),
				Rotation = turned[cell.Turns & 3],
				Scale = half,
				TextureHandle = picture,
				Region = new Vector4( region.Left / width, region.Top / height, region.Width / width, region.Height / height ),
				TintColor = bright
					? new Color32( rgbe.r, rgbe.g, rgbe.b, (byte)( alpha * 255.0f ) ).RawInt
					: SpriteBatchSceneObject.SpriteData.PackColor( new Color( Math.Clamp( tint.r, 0.0f, 1.0f ), Math.Clamp( tint.g, 0.0f, 1.0f ), Math.Clamp( tint.b, 0.0f, 1.0f ), alpha ) ),
				Lighting = ( bright ? (uint)rgbe.a << 16 : 0 ) | ( layer.Lit2D ? SpriteBatchSceneObject.SpriteData.Lit2D : 0 )
					| ( layer.Occluder2D ? SpriteBatchSceneObject.SpriteData.Occluder2D : 0 ),
				RenderFlags = (int)flags,
				BillboardMode = (uint)SpriteRenderer.BillboardMode.None,
				FogStrengthCutout = cutout,
				Offset = new Vector2( 0.5f, 0.5f ),
				BlendSheetUV = frames > 1 ? new Vector4( frames, info.FramesPerSecond, step, 0.0f ) : default,
			};
		}

		into.Count = count;

		var min = new Vector3( float.MaxValue );
		var max = new Vector3( float.MinValue );
		var reach = new Vector3( MathF.Max( half.x, half.y ) );

		for ( var i = 0; i < count; i++ )
		{
			min = Vector3.Min( min, into.Tiles[i].Position - reach );
			max = Vector3.Max( max, into.Tiles[i].Position + reach );
		}

		into.Bounds = count > 0 ? new BBox( min, max ) : default;
	}

	//Where a tile's alpha is cut: under it the pixel is not there at all.
	private const float AlphaCutoff = 0.5f;

	//An object for every z-index something is drawn at, and none for the rest, each with the box round its chunks so it is culled as one.
	private void UpdateSceneObjects( SceneWorld world )
	{
		var bounds = new Dictionary<int, BBox>();

		foreach ( var chunk in drawn.Values )
		{
			bounds[chunk.ZIndex] = bounds.TryGetValue( chunk.ZIndex, out var b ) ? b.AddBBox( chunk.Bounds ) : chunk.Bounds;
		}

		foreach ( var (zIndex, sceneObject) in sceneObjects.ToArray() )
		{
			if ( bounds.ContainsKey( zIndex ) && sceneObject.IsValid() ) continue;

			if ( sceneObject.IsValid() ) sceneObject.Delete();
			sceneObjects.Remove( zIndex );
		}

		foreach ( var (zIndex, box) in bounds )
		{
			if ( !sceneObjects.TryGetValue( zIndex, out var sceneObject ) )
			{
				sceneObject = sceneObjects[zIndex] = new TileMapSceneObject( world, this, zIndex );
				sceneObject.Tags.SetFrom( Tags );
			}

			sceneObject.Bounds = box;
		}
	}

	//Draw the chunks of one z-index, for the view being gathered - called by its scene object while the frame collects what is drawn by hand. Solid chunks
	//write their depth and need no order among themselves; a layer tinted see-through blends, and is drawn after the solid ones, back layer first.
	internal void DrawChunks( int zIndex )
	{
		if ( drawn.Count == 0 ) return;

		if ( drawOrderDirty )
		{
			drawOrder.Clear();
			drawOrder.AddRange( drawn.Values );
			drawOrder.Sort( static ( a, b ) => a.ZIndex != b.ZIndex ? a.ZIndex.CompareTo( b.ZIndex )
				: a.Opaque != b.Opaque ? ( a.Opaque ? -1 : 1 ) : a.Order.CompareTo( b.Order ) );
			drawOrderDirty = false;
		}

		var material = TileMaterial;
		var quad = QuadIndices;

		if ( material is null || !quad.IsValid() ) return;

		foreach ( var layer in Layers ?? [] )
		{
			if ( layer?.ZIndex == zIndex ) layer.TileSet?.Sheet?.MarkUsed( ushort.MaxValue );
		}

		var attributes = RenderAttributes.Pool.Get();

		try
		{
			attributes.SetCombo( "D_BLEND", 0 );

			var portable = CoreRenderDevice.PortableFrame;
			var perRun = portable ? Graphics.InstancesPerRun( Marshal.SizeOf<SpriteBatchSceneObject.SpriteData>() ) : 0;

			foreach ( var chunk in drawOrder )
			{
				if ( chunk.ZIndex < zIndex ) continue;
				if ( chunk.ZIndex > zIndex ) break;
				if ( chunk.Count == 0 ) continue;

				attributes.SetCombo( "D_OPAQUE", chunk.Opaque ? 1 : 0 );

				if ( portable )
				{
					//A chunk is one picture, so a run is cut only where the slot fills.
					var texture = CoreRender.PortableTexture( chunk.Tiles[0].TextureHandle );
					var bytes = MemoryMarshal.AsBytes( chunk.Tiles.AsSpan( 0, chunk.Count ) );
					var size = bytes.Length / chunk.Count;

					for ( var start = 0; start < chunk.Count; start += perRun )
					{
						var run = Math.Min( perRun, chunk.Count - start );
						Graphics.DrawIndexedInstancedRun( (GpuBuffer)quad, material, run, bytes.Slice( start * size, run * size ), texture, attributes );
					}

					continue;
				}

				if ( !chunk.Buffer.IsValid() ) continue;

				attributes.Set( "Sprites", (GpuBuffer)chunk.Buffer );
				Graphics.DrawIndexedInstanced( (GpuBuffer)quad, material, chunk.Count, attributes );
			}
		}
		finally
		{
			RenderAttributes.Pool.Return( attributes );
		}
	}

	//The object moved: the records stand in the world, so every chunk is laid out again where it now is.
	private void Moved()
	{
		foreach ( var key in drawn.Keys ) dirty.Add( key );
	}

	protected override void OnTagsChanged()
	{
		base.OnTagsChanged();

		foreach ( var sceneObject in sceneObjects.Values )
		{
			if ( sceneObject.IsValid() ) sceneObject.Tags.SetFrom( Tags );
		}
	}

	private void ReleaseLayer( TileMapLayer layer )
	{
		foreach ( var key in drawn.Keys.Where( k => ReferenceEquals( k.Layer, layer ) ).ToArray() )
		{
			if ( drawn.Remove( key, out var chunk ) ) chunk.Release();
		}

		drawOrderDirty = true;
	}

	private void ReleaseChunks()
	{
		foreach ( var chunk in drawn.Values ) chunk.Release();

		drawn.Clear();
		drawOrder.Clear();
		dirty.Clear();
		builtWith.Clear();

		foreach ( var sceneObject in sceneObjects.Values )
		{
			if ( sceneObject.IsValid() ) sceneObject.Delete();
		}

		sceneObjects.Clear();
	}
}

//What draws a TileMap: one object in the world for each z-index its layers use, which draws their chunks the way sprites are drawn, in
//its place among them - see TileMap.DrawChunks.
internal sealed class TileMapSceneObject : SceneCustomObject
{
	private readonly TileMap map;
	private readonly int zIndex;

	public TileMapSceneObject( SceneWorld world, TileMap map, int zIndex ) : base( world )
	{
		this.map = map;
		this.zIndex = zIndex;
		DrawOrder = zIndex;
		Flags.CastShadows = false;
	}

	public override void RenderSceneObject() => map?.DrawChunks( zIndex );
}
