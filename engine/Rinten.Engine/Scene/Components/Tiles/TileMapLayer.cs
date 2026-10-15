using System.Text.Json.Serialization;

namespace Rinten.Tiles;

/// <summary>One layer of a <see cref="TileMap"/>: a grid of tiles from one tile set, drawn at its own depth - ground, then decoration over it, then foreground.</summary>
public sealed class TileMapLayer
{
	[Property]
	public string Name { get; set; } = "Layer";

	/// <summary>What the numbers in this layer mean and look like.</summary>
	[Property]
	public TileSet TileSet
	{
		get;
		set
		{
			if ( ReferenceEquals( field, value ) ) return;

			field = value;
			Map?.LayerChanged( this );
		}
	}

	[Property]
	public bool Visible
	{
		get;
		set
		{
			if ( field == value ) return;

			field = value;
			Map?.LayerChanged( this );
		}
	} = true;

	/// <summary>Whether its solid tiles are part of the map's collision.</summary>
	[Property]
	public bool Collides
	{
		get;
		set
		{
			if ( field == value ) return;

			field = value;
			Map?.LayerChanged( this );
		}
	} = true;

	/// <summary>
	/// Whether the layer's tiles are lit by the scene's 2D lights - see <see cref="Light2D"/>.
	/// Drawn as they are in a scene with no 2D lights, so on by default.
	/// </summary>
	[Property, Title( "Lit 2D" )]
	public bool Lit2D
	{
		get;
		set
		{
			if ( field == value ) return;

			field = value;
			Map?.LayerChanged( this );
		}
	} = true;

	/// <summary>Whether the layer's tiles block 2D light, by their pictures' shapes, and so cast 2D shadows.</summary>
	[Property, Title( "Occluder 2D" )]
	public bool Occluder2D
	{
		get;
		set
		{
			if ( field == value ) return;

			field = value;
			Map?.LayerChanged( this );
		}
	}

	/// <summary>Multiplied into every tile of the layer.</summary>
	[Property]
	public Color Tint
	{
		get;
		set
		{
			if ( field == value ) return;

			field = value;
			Map?.LayerChanged( this );
		}
	} = Color.White;

	/// <summary>The cells, as they are kept in a scene. Written every time the scene is, and read back into <see cref="Grid"/> when it is loaded.</summary>
	[Property, Hide]
	public string Data
	{
		get
		{
			if ( dataRevision != Grid.Revision )
			{
				data = Grid.Serialize();
				dataRevision = Grid.Revision;
			}

			return data;
		}
		set
		{
			if ( value == data && dataRevision == Grid.Revision ) return;

			Grid.Deserialize( value );
			data = value ?? "";
			dataRevision = Grid.Revision;
		}
	}

	private string data = "";
	private int dataRevision = -1;

	/// <summary>The cells.</summary>
	[JsonIgnore, Hide]
	public TileGrid Grid { get; } = new();

	/// <summary>The map this layer belongs to, once it has been added to one.</summary>
	[JsonIgnore, Hide]
	public TileMap Map { get; internal set; }

	/// <summary>What is in a cell.</summary>
	public TileCell Get( Vector2Int cell ) => Grid.Get( cell );

	/// <summary>Which tile is in a cell, or nought.</summary>
	public int GetTile( Vector2Int cell ) => Grid.Get( cell ).Tile;

	/// <summary>Put a tile in a cell. Nought empties it.</summary>
	public bool Set( Vector2Int cell, int tile, byte turns = 0, TileFlip flip = TileFlip.None )
		=> Grid.Set( cell, new TileCell( tile, turns, flip ) );

	/// <summary>Put the tile with this name in a cell.</summary>
	public bool Set( Vector2Int cell, string name ) => Set( cell, TileSet?.Find( name ) ?? 0 );

	/// <summary>Empty a cell.</summary>
	public bool Erase( Vector2Int cell ) => Grid.Set( cell, TileCell.Empty );

	/// <summary>Fill a rectangle of cells with one tile.</summary>
	public void Fill( RectInt area, int tile, byte turns = 0, TileFlip flip = TileFlip.None )
	{
		var value = new TileCell( tile, turns, flip );

		for ( var y = area.Top; y < area.Top + area.Height; y++ )
		{
			for ( var x = area.Left; x < area.Left + area.Width; x++ )
			{
				Grid.Set( new Vector2Int( x, y ), value );
			}
		}
	}

	/// <summary>Empty every cell.</summary>
	public void Clear() => Grid.Clear();

	/// <summary>How a cell's tile stops things that move.</summary>
	public TileCollision CollisionAt( Vector2Int cell )
	{
		var tile = GetTile( cell );
		return tile > 0 && TileSet is not null ? TileSet.CollisionOf( tile ) : TileCollision.None;
	}

	public bool IsSolid( Vector2Int cell ) => CollisionAt( cell ) == TileCollision.Solid;

	public bool HasTag( Vector2Int cell, string tag )
	{
		var tile = GetTile( cell );
		return tile > 0 && ( TileSet?.HasTag( tile, tag ) ?? false );
	}

	/// <summary>What the tile set says about a cell's tile, or null.</summary>
	public TileInfo InfoAt( Vector2Int cell ) => TileSet?.Info( GetTile( cell ) );

	public override string ToString() => Name;
}
