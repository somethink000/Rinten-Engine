namespace Rinten.Tiles;

/// <summary>A 2D tile map: layers of tile grids in the object's XY plane, drawn and collided per chunk.</summary>
[Title( "Tile Map" )]
[Category( "2D" )]
[Icon( "grid_on" )]
public sealed partial class TileMap : Collider, Component.ExecuteInEditor
{
	/// <summary>How big one cell is, in the object's units.</summary>
	[Property]
	public Vector2 CellSize
	{
		get;
		set
		{
			value = new Vector2( MathF.Max( value.x, 0.001f ), MathF.Max( value.y, 0.001f ) );
			if ( field == value ) return;

			field = value;
			RebuildAll();
		}
	} = Vector2.One;

	/// <summary>The layers, back to front within one <see cref="TileMapLayer.ZIndex"/>.</summary>
	[Property, WideMode]
	public List<TileMapLayer> Layers { get; set; } = new();

	/// <summary>How far apart the layers stand towards the viewer, so each is drawn over the one before.</summary>
	public const float LayerSpacing = 0.01f;

	/// <summary>How thick the collision is, through the plane.</summary>
	[Property]
	public float Thickness
	{
		get;
		set
		{
			if ( field == value ) return;

			field = MathF.Max( value, 0.01f );
			collisionDirty = true;
		}
	} = 1.0f;

	/// <summary>The layer at this place, or null.</summary>
	public TileMapLayer Layer( int index ) => Layers is not null && index >= 0 && index < Layers.Count ? Layers[index] : null;

	/// <summary>The layer with this name, or null.</summary>
	public TileMapLayer Layer( string name )
		=> Layers?.FirstOrDefault( l => l is not null && string.Equals( l.Name, name, StringComparison.OrdinalIgnoreCase ) );

	/// <summary>Add a layer in front of the others.</summary>
	public TileMapLayer AddLayer( string name, TileSet tileSet )
	{
		var layer = new TileMapLayer { Name = name, TileSet = tileSet };

		( Layers ??= new() ).Add( layer );
		Attach();

		return layer;
	}

	/// <summary>Which cell a point in the world is in.</summary>
	public Vector2Int WorldToCell( Vector3 world )
	{
		var local = WorldTransform.PointToLocal( world );

		return new Vector2Int( (int)MathF.Floor( local.x / CellSize.x ), (int)MathF.Floor( local.y / CellSize.y ) );
	}

	/// <summary>The middle of a cell, in the world.</summary>
	public Vector3 CellToWorld( Vector2Int cell )
		=> WorldTransform.PointToWorld( new Vector3( ( cell.x + 0.5f ) * CellSize.x, ( cell.y + 0.5f ) * CellSize.y, 0.0f ) );

	/// <summary>A point in the world, in the map's own plane: x right, y up, in the object's units.</summary>
	public Vector2 WorldToPlane( Vector3 world )
	{
		var local = WorldTransform.PointToLocal( world );
		return new Vector2( local.x, local.y );
	}

	/// <summary>A point in the map's plane, in the world.</summary>
	public Vector3 PlaneToWorld( Vector2 plane ) => WorldTransform.PointToWorld( new Vector3( plane.x, plane.y, 0.0f ) );

	/// <summary>The smallest rectangle of cells holding every tile of every layer.</summary>
	public RectInt CellBounds
	{
		get
		{
			RectInt? all = null;

			foreach ( var layer in Layers ?? [] )
			{
				if ( layer is null || layer.Grid.ChunkCount == 0 ) continue;

				var b = layer.Grid.Bounds;

				if ( all is not { } a )
				{
					all = b;
					continue;
				}

				var minX = Math.Min( a.Left, b.Left );
				var minY = Math.Min( a.Top, b.Top );
				var maxX = Math.Max( a.Left + a.Width, b.Left + b.Width );
				var maxY = Math.Max( a.Top + a.Height, b.Top + b.Height );

				all = new RectInt( minX, minY, maxX - minX, maxY - minY );
			}

			return all ?? default;
		}
	}

	//Keeping up with the layers

	private List<TileMapLayer> attachedList;
	private readonly List<(TileMapLayer Layer, Action<Vector2Int> Handler)> attached = new();

	protected override void OnEnabled()
	{
		Transform.OnTransformChanged += Moved;
		Attach();
	}

	protected override void OnDisabled()
	{
		Transform.OnTransformChanged -= Moved;
		Detach();
		ReleaseChunks();
	}

	protected override void OnDestroy()
	{
		Transform.OnTransformChanged -= Moved;
		Detach();
		ReleaseChunks();
	}

	protected override void OnUpdate() => Refresh();

	protected override void OnPreRender() => Refresh();

	/// <summary>Bring the drawing and the collision up to date with the layers - done by the map itself every update, and only for what changed.</summary>
	public void Refresh()
	{
		if ( NeedsAttach() ) Attach();

		RefreshTileSets();
		BuildDirtyChunks();

		if ( collisionDirty )
		{
			collisionDirty = false;
			Rebuild();
		}
	}

	private bool NeedsAttach()
	{
		if ( !ReferenceEquals( attachedList, Layers ) ) return true;
		if ( attached.Count != ( Layers?.Count ?? 0 ) ) return true;

		for ( var i = 0; i < attached.Count; i++ )
		{
			if ( !ReferenceEquals( attached[i].Layer, Layers[i] ) ) return true;
		}

		return false;
	}

	//Take hold of the layers as they are now, and draw all of them again.
	private void Attach()
	{
		Detach();

		attachedList = Layers;

		foreach ( var layer in Layers ?? [] )
		{
			if ( layer is null )
			{
				attached.Add( (null, null) );
				continue;
			}

			layer.Map = this;

			Action<Vector2Int> handler = chunk => ChunkChanged( layer, chunk );
			layer.Grid.ChunkChanged += handler;

			attached.Add( (layer, handler) );
		}

		RebuildAll();
	}

	private void Detach()
	{
		foreach ( var (layer, handler) in attached )
		{
			if ( layer is null ) continue;

			layer.Grid.ChunkChanged -= handler;
			if ( ReferenceEquals( layer.Map, this ) ) layer.Map = null;
		}

		attached.Clear();
		attachedList = null;
	}

	private void ChunkChanged( TileMapLayer layer, Vector2Int chunk )
	{
		dirty.Add( (layer, chunk) );

		if ( layer.Collides ) collisionDirty = true;
	}

	//A layer said something about itself changed - its set, its tint, whether it shows.
	internal void LayerChanged( TileMapLayer layer )
	{
		RebuildLayer( layer );
		collisionDirty = true;
	}

	//A layer's z-index changed: every layer may now stand at another depth, so all of them are drawn again.
	internal void LayerOrderChanged()
	{
		foreach ( var layer in Layers ?? [] )
		{
			if ( layer is not null ) RebuildLayer( layer );
		}
	}

	private void RebuildAll()
	{
		ReleaseChunks();

		foreach ( var layer in Layers ?? [] )
		{
			if ( layer is not null ) RebuildLayer( layer );
		}

		collisionDirty = true;
	}

	private void RebuildLayer( TileMapLayer layer )
	{
		ReleaseLayer( layer );

		foreach ( var chunk in layer.Grid.Chunks ) dirty.Add( (layer, chunk.Coord) );
	}

	private bool collisionDirty = true;
}
