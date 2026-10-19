using System;
using System.Collections.Generic;
using System.IO;

namespace Rinten;

//Never changed once made - a change makes another - so a worker can read one while the mesh is painted.
/// <summary>One square of the paint mask: what each of its points is painted with, and the top of the ground there.</summary>
public sealed class TerrainMaskTile
{
	/// <summary>Points a side, the far edge's shared with the next tile over.</summary>
	public const int Side = TerrainMesh.MaskCells + 1;

	public readonly TerrainPaint[] Paint;

	/// <summary>The highest ground over each point, in the terrain's metres; <see cref="TerrainMesh.NoGround"/> where there is none.</summary>
	public readonly float[] Top;

	internal TerrainMaskTile( TerrainPaint[] paint, float[] top )
	{
		Paint = paint;
		Top = top;
	}
}

//The paint mask: over the top of the ground, paint at a quarter of a metre whatever the edges are, in tiles only where a brush has been. Below the
//top - a cave's floor, the foot of a cliff - the vertices' paint is what there is. Where a tile is, it is the whole of the paint on the top surface.
public sealed partial class TerrainMesh
{
	/// <summary>Metres between two points of the mask.</summary>
	public const float MaskStep = 0.25f;

	/// <summary>Steps along a tile's side.</summary>
	public const int MaskCells = 32;

	/// <summary>Metres along a tile's side.</summary>
	public const float MaskSpan = MaskStep * MaskCells;

	/// <summary>How far under the top of the ground the mask still paints; below it is a cave's floor, painted by its vertices.</summary>
	public const float MaskUnder = 1.0f;

	/// <summary>What a point of the mask with no ground over it says the top is.</summary>
	public const float NoGround = -1e30f;

	private readonly Dictionary<long, TerrainMaskTile> mask = new();

	//Tiles whose ground moved, so their tops are to be worked out again when next asked for.
	private readonly HashSet<long> maskStale = new();

	//What changed since the terrain last mirrored it to the card.
	private readonly HashSet<long> maskChanged = new();

	//Tiles being written in one go, published when the call is done.
	private readonly Dictionary<long, TerrainPaint[]> maskWriting = new();

	private readonly List<int> maskTriangles = new();

	/// <summary>How many tiles there are.</summary>
	public int MaskTileCount => mask.Count;

	/// <summary>Every tile there is, by key.</summary>
	public IEnumerable<long> MaskKeys => mask.Keys;

	public static long MaskKey( int x, int z ) => ( (long)x << 32 ) | (uint)z;

	public static int MaskKeyX( long key ) => (int)( key >> 32 );

	public static int MaskKeyZ( long key ) => (int)( key & 0xFFFFFFFFL );

	/// <summary>The tile at a key, its tops worked out again first where its ground moved - or null.</summary>
	public TerrainMaskTile MaskTile( long key )
	{
		if ( !mask.TryGetValue( key, out var tile ) ) return null;

		if ( maskStale.Remove( key ) || tile.Top is null )
		{
			tile = new TerrainMaskTile( tile.Paint, Tops( key, null ) );
			mask[key] = tile;
		}

		return tile;
	}

	/// <summary>Hands over which tiles changed since last asked - painted, made, gone, or their ground moved - and forgets them.</summary>
	public void TakeMaskChanged( List<long> into )
	{
		into.AddRange( maskChanged );
		maskChanged.Clear();
	}

	/// <summary>The tiles over a rectangle of the ground plane, their tops up to date, for a worker to read - or null where there are none.</summary>
	public Dictionary<long, TerrainMaskTile> MaskAround( Vector2 min, Vector2 max )
	{
		if ( mask.Count == 0 ) return null;

		Dictionary<long, TerrainMaskTile> found = null;

		TileRange( min.x, min.y, max.x, max.y, out var x0, out var x1, out var z0, out var z1 );

		for ( var z = z0; z <= z1; z++ )
			for ( var x = x0; x <= x1; x++ )
				if ( MaskTile( MaskKey( x, z ) ) is { } tile ) ( found ??= new() )[MaskKey( x, z )] = tile;

		return found;
	}

	//facingUp is how far the ground there faces up; what the mask does not cover is the vertices'.
	/// <summary>Adds the mask's paint at a point into one number a layer; returns how much of the point it covers.</summary>
	public float MaskAt( Vector3 point, float facingUp, Span<float> layers )
		=> mask.Count == 0 ? 0.0f : MaskAt( MaskTile, point, facingUp, layers );

	/// <summary>The same, out of tiles handed out by <see cref="MaskAround"/>.</summary>
	public static float MaskAt( IReadOnlyDictionary<long, TerrainMaskTile> tiles, Vector3 point, float facingUp, Span<float> layers )
		=> tiles is null || tiles.Count == 0 ? 0.0f : MaskAt( key => tiles.GetValueOrDefault( key ), point, facingUp, layers );

	//Bilinear over the four points round it, where the nearest one's top says the point is on top - as terrain_layers.slang reads it.
	private static float MaskAt( Func<long, TerrainMaskTile> tileAt, Vector3 point, float facingUp, Span<float> layers )
	{
		var ax = point.x / MaskStep;
		var az = point.z / MaskStep;

		var tx = (int)MathF.Floor( ax / MaskCells );
		var tz = (int)MathF.Floor( az / MaskCells );

		if ( tileAt( MaskKey( tx, tz ) ) is not { Top: not null } tile ) return 0.0f;

		var ix = ax - ( tx * MaskCells );
		var iz = az - ( tz * MaskCells );

		var bx = Math.Clamp( (int)MathF.Floor( ix ), 0, MaskCells - 1 );
		var bz = Math.Clamp( (int)MathF.Floor( iz ), 0, MaskCells - 1 );

		var fx = Math.Clamp( ix - bx, 0.0f, 1.0f );
		var fz = Math.Clamp( iz - bz, 0.0f, 1.0f );

		//Under the top - a cave's floor - the vertices paint.
		var nearest = ( ( bz + ( fz >= 0.5f ? 1 : 0 ) ) * TerrainMaskTile.Side ) + bx + ( fx >= 0.5f ? 1 : 0 );
		if ( point.y < tile.Top[nearest] - MaskUnder ) return 0.0f;

		//Under a cave's roof the ground faces down, and there too.
		var upright = Math.Clamp( ( facingUp * 4.0f ) + 1.0f, 0.0f, 1.0f );
		var cover = 0.0f;

		for ( var k = 0; k < 4; k++ )
		{
			var dx = k & 1;
			var dz = k >> 1;
			var at = ( ( bz + dz ) * TerrainMaskTile.Side ) + bx + dx;

			var share = ( dx == 1 ? fx : 1.0f - fx ) * ( dz == 1 ? fz : 1.0f - fz ) * upright;

			if ( share <= 0.0f || !tile.Paint[at].Painted ) continue;

			tile.Paint[at].AddTo( layers, share );
			cover += share;
		}

		return cover;
	}

	//Which tiles a rectangle of the ground plane touches.
	private static void TileRange( float minX, float minZ, float maxX, float maxZ, out int x0, out int x1, out int z0, out int z1 )
	{
		x0 = (int)MathF.Floor( Math.Clamp( minX / MaskSpan, -1e8f, 1e8f ) );
		x1 = (int)MathF.Floor( Math.Clamp( maxX / MaskSpan, -1e8f, 1e8f ) );
		z0 = (int)MathF.Floor( Math.Clamp( minZ / MaskSpan, -1e8f, 1e8f ) );
		z1 = (int)MathF.Floor( Math.Clamp( maxZ / MaskSpan, -1e8f, 1e8f ) );
	}

	//The top of the ground over each point of a tile, out of the triangles over it; and, where asked, what the vertices paint there.
	private float[] Tops( long key, TerrainPaint[] paintInto )
	{
		var top = new float[TerrainMaskTile.Side * TerrainMaskTile.Side];
		Array.Fill( top, NoGround );

		var x0 = MaskKeyX( key ) * MaskSpan;
		var z0 = MaskKeyZ( key ) * MaskSpan;

		//A triangle over the tile has its middle within an edge of it.
		var reach = longestEdge + MaskStep;

		maskTriangles.Clear();
		TrianglesInRect( new Vector2( x0 - reach, z0 - reach ), new Vector2( x0 + MaskSpan + reach, z0 + MaskSpan + reach ), maskTriangles );

		foreach ( var t in maskTriangles )
		{
			var ia = tVertex[t * 3];
			var ib = tVertex[( t * 3 ) + 1];
			var ic = tVertex[( t * 3 ) + 2];

			var a = vPosition[ia];
			var b = vPosition[ib];
			var c = vPosition[ic];

			var area = ( ( b.x - a.x ) * ( c.z - a.z ) ) - ( ( c.x - a.x ) * ( b.z - a.z ) );
			if ( MathF.Abs( area ) < 1e-8f ) continue;

			var i0 = Math.Max( (int)MathF.Ceiling( ( MathF.Min( a.x, MathF.Min( b.x, c.x ) ) - x0 ) / MaskStep ), 0 );
			var i1 = Math.Min( (int)MathF.Floor( ( MathF.Max( a.x, MathF.Max( b.x, c.x ) ) - x0 ) / MaskStep ), MaskCells );
			var j0 = Math.Max( (int)MathF.Ceiling( ( MathF.Min( a.z, MathF.Min( b.z, c.z ) ) - z0 ) / MaskStep ), 0 );
			var j1 = Math.Min( (int)MathF.Floor( ( MathF.Max( a.z, MathF.Max( b.z, c.z ) ) - z0 ) / MaskStep ), MaskCells );

			for ( var j = j0; j <= j1; j++ )
			{
				var pz = z0 + ( j * MaskStep );

				for ( var i = i0; i <= i1; i++ )
				{
					var px = x0 + ( i * MaskStep );

					var wb = ( ( ( px - a.x ) * ( c.z - a.z ) ) - ( ( c.x - a.x ) * ( pz - a.z ) ) ) / area;
					var wc = ( ( ( b.x - a.x ) * ( pz - a.z ) ) - ( ( px - a.x ) * ( b.z - a.z ) ) ) / area;
					var wa = 1.0f - wb - wc;

					if ( wa < -1e-5f || wb < -1e-5f || wc < -1e-5f ) continue;

					var y = ( a.y * wa ) + ( b.y * wb ) + ( c.y * wc );
					var at = ( j * TerrainMaskTile.Side ) + i;

					if ( y <= top[at] ) continue;

					top[at] = y;

					if ( paintInto is not null ) paintInto[at] = TerrainPaint.Mix( vPaint[ia], vPaint[ib], vPaint[ic], wa, wb, wc );
				}
			}
		}

		return top;
	}

	//The mask's half of a dab. A tile is made wherever the brush comes near the top of the ground - out to an edge past the brush, so the vertices'
	//paint blurring past it lands inside tiles too - filled from what the vertices paint, and then the brush paints its points as it does the vertices.
	private void PaintMask( Vector3 centre, float radius, int layer, float strength )
	{
		if ( strength <= 0.0f || radius <= 0.0f ) return;

		var margin = MathF.Min( longestEdge, MaskSpan ) + MaskStep;
		var outer = radius + margin;

		TileRange( centre.x - outer, centre.z - outer, centre.x + outer, centre.z + outer, out var x0, out var x1, out var z0, out var z1 );

		for ( var tz = z0; tz <= z1; tz++ )
		{
			for ( var tx = x0; tx <= x1; tx++ )
			{
				var key = MaskKey( tx, tz );
				var tile = MaskTile( key );

				if ( tile is null )
				{
					var paint = new TerrainPaint[TerrainMaskTile.Side * TerrainMaskTile.Side];
					var top = Tops( key, paint );

					//Only where the brush is near the top: a dab on a cave's floor makes no tile over the hill above it.
					if ( !Near( key, top, centre, outer ) ) continue;

					tile = new TerrainMaskTile( paint, top );
					mask[key] = tile;
					maskChanged.Add( key );

					open?.AddedTile( key, paint );
				}

				PaintTile( key, tile, centre, radius, layer, strength );
			}
		}

		PublishMask();
	}

	private static bool Near( long key, float[] top, Vector3 centre, float reach )
	{
		var x0 = MaskKeyX( key ) * MaskSpan;
		var z0 = MaskKeyZ( key ) * MaskSpan;
		var most = reach * reach;

		for ( var j = 0; j < TerrainMaskTile.Side; j++ )
		{
			for ( var i = 0; i < TerrainMaskTile.Side; i++ )
			{
				var y = top[( j * TerrainMaskTile.Side ) + i];
				if ( y == NoGround ) continue;

				if ( ( new Vector3( x0 + ( i * MaskStep ), y, z0 + ( j * MaskStep ) ) - centre ).LengthSquared <= most ) return true;
			}
		}

		return false;
	}

	private void PaintTile( long key, TerrainMaskTile tile, Vector3 centre, float radius, int layer, float strength )
	{
		var x0 = MaskKeyX( key ) * MaskSpan;
		var z0 = MaskKeyZ( key ) * MaskSpan;

		var i0 = Math.Max( (int)MathF.Ceiling( ( centre.x - radius - x0 ) / MaskStep ), 0 );
		var i1 = Math.Min( (int)MathF.Floor( ( centre.x + radius - x0 ) / MaskStep ), MaskCells );
		var j0 = Math.Max( (int)MathF.Ceiling( ( centre.z - radius - z0 ) / MaskStep ), 0 );
		var j1 = Math.Min( (int)MathF.Floor( ( centre.z + radius - z0 ) / MaskStep ), MaskCells );

		for ( var j = j0; j <= j1; j++ )
		{
			for ( var i = i0; i <= i1; i++ )
			{
				var at = ( j * TerrainMaskTile.Side ) + i;
				var y = tile.Top[at];

				if ( y == NoGround ) continue;

				var point = new Vector3( x0 + ( i * MaskStep ), y, z0 + ( j * MaskStep ) );
				var add = strength * Falloff( ( point - centre ).Length / radius );

				if ( add <= 0.0f ) continue;

				var before = MaskWriting( key )[at];
				var after = before.With( layer, add );

				if ( after == before ) continue;

				open?.SetTexel( key, at, before, after );
				RawSetTexel( key, at, after );

				Touch( point );
			}
		}
	}

	//A tile's paint as it is being written in this call, copied from the published one the first time it is asked for.
	private TerrainPaint[] MaskWriting( long key )
	{
		if ( maskWriting.TryGetValue( key, out var writing ) ) return writing;

		writing = mask.TryGetValue( key, out var tile )
			? (TerrainPaint[])tile.Paint.Clone()
			: new TerrainPaint[TerrainMaskTile.Side * TerrainMaskTile.Side];
		maskWriting[key] = writing;

		return writing;
	}

	private void RawSetTexel( long key, int at, TerrainPaint value )
	{
		if ( !mask.ContainsKey( key ) ) return;

		MaskWriting( key )[at] = value;
	}

	private void RawAddTile( long key, TerrainPaint[] paint )
	{
		maskWriting.Remove( key );
		mask[key] = new TerrainMaskTile( paint, null );
		maskChanged.Add( key );
	}

	private void RawRemoveTile( long key )
	{
		maskWriting.Remove( key );
		mask.Remove( key );
		maskStale.Remove( key );
		maskChanged.Add( key );
	}

	//What was written in this call into tiles of their own, which is when anybody else sees it.
	private void PublishMask()
	{
		foreach ( var (key, paint) in maskWriting )
		{
			if ( !mask.TryGetValue( key, out var tile ) ) continue;

			mask[key] = new TerrainMaskTile( paint, tile.Top );
			maskChanged.Add( key );
		}

		maskWriting.Clear();
	}

	//The ground moved inside a box: the tops of the tiles over it, out to an edge further, are worked out again.
	private void StaleMask( BBox box )
	{
		if ( mask.Count == 0 ) return;

		var reach = longestEdge + MaskStep;

		TileRange( box.Mins.x - reach, box.Mins.z - reach, box.Maxs.x + reach, box.Maxs.z + reach, out var x0, out var x1, out var z0, out var z1 );

		if ( (long)( x1 - x0 + 1 ) * ( z1 - z0 + 1 ) > mask.Count )
		{
			foreach ( var key in mask.Keys )
			{
				var x = MaskKeyX( key );
				var z = MaskKeyZ( key );

				if ( x < x0 || x > x1 || z < z0 || z > z1 ) continue;

				maskStale.Add( key );
				maskChanged.Add( key );
			}

			return;
		}

		for ( var z = z0; z <= z1; z++ )
		{
			for ( var x = x0; x <= x1; x++ )
			{
				var key = MaskKey( x, z );
				if ( !mask.ContainsKey( key ) ) continue;

				maskStale.Add( key );
				maskChanged.Add( key );
			}
		}
	}

	//What a splat map imported over the ground wants. Part of the open edit.
	/// <summary>Takes the whole mask away, so the vertices paint everything.</summary>
	public void ClearMask()
	{
		foreach ( var key in new List<long>( mask.Keys ) )
		{
			var tile = mask[key];

			foreach ( var t in Corners( key ) ) Touch( t );

			open?.RemovedTile( key, tile.Paint );
			RawRemoveTile( key );
		}

		Report();
	}

	private static IEnumerable<Vector3> Corners( long key )
	{
		var x0 = MaskKeyX( key ) * MaskSpan;
		var z0 = MaskKeyZ( key ) * MaskSpan;

		yield return new Vector3( x0, 0.0f, z0 );
		yield return new Vector3( x0 + MaskSpan, 0.0f, z0 + MaskSpan );
	}

	//The mask after the ground was stretched across: each point reads the nearest of where it was.
	private void StretchMask( float across )
	{
		if ( mask.Count == 0 ) return;

		var old = new Dictionary<long, TerrainMaskTile>( mask );

		foreach ( var key in old.Keys ) RawRemoveTile( key );

		foreach ( var key in old.Keys )
		{
			TileRange( MaskKeyX( key ) * MaskSpan * across, MaskKeyZ( key ) * MaskSpan * across,
				( MaskKeyX( key ) + 1 ) * MaskSpan * across, ( MaskKeyZ( key ) + 1 ) * MaskSpan * across,
				out var x0, out var x1, out var z0, out var z1 );

			for ( var z = z0; z <= z1; z++ )
			{
				for ( var x = x0; x <= x1; x++ )
				{
					var made = MaskKey( x, z );
					if ( mask.ContainsKey( made ) ) continue;

					var paint = new TerrainPaint[TerrainMaskTile.Side * TerrainMaskTile.Side];
					var any = false;

					for ( var j = 0; j < TerrainMaskTile.Side; j++ )
					{
						for ( var i = 0; i < TerrainMaskTile.Side; i++ )
						{
							var sx = ( ( x * MaskSpan ) + ( i * MaskStep ) ) / across / MaskStep;
							var sz = ( ( z * MaskSpan ) + ( j * MaskStep ) ) / across / MaskStep;

							var gx = (int)MathF.Round( sx );
							var gz = (int)MathF.Round( sz );

							var from = MaskKey( (int)MathF.Floor( gx / (float)MaskCells ), (int)MathF.Floor( gz / (float)MaskCells ) );
							if ( !old.TryGetValue( from, out var was ) ) continue;

							var at = ( ( gz - ( MaskKeyZ( from ) * MaskCells ) ) * TerrainMaskTile.Side ) + gx - ( MaskKeyX( from ) * MaskCells );

							paint[( j * TerrainMaskTile.Side ) + i] = was.Paint[at];
							any = true;
						}
					}

					if ( any ) RawAddTile( made, paint );
				}
			}
		}
	}

	private void WriteMask( BinaryWriter into )
	{
		into.Write( mask.Count );

		foreach ( var (key, tile) in mask )
		{
			into.Write( MaskKeyX( key ) );
			into.Write( MaskKeyZ( key ) );

			foreach ( var paint in tile.Paint )
			{
				into.Write( paint.Layers );
				into.Write( paint.Weights );
			}
		}
	}

	private bool ReadMask( BinaryReader from )
	{
		var count = from.ReadInt32();
		if ( count < 0 ) return false;

		for ( var n = 0; n < count; n++ )
		{
			var key = MaskKey( from.ReadInt32(), from.ReadInt32() );
			var paint = new TerrainPaint[TerrainMaskTile.Side * TerrainMaskTile.Side];

			for ( var at = 0; at < paint.Length; at++ )
				paint[at] = new TerrainPaint { Layers = from.ReadUInt32(), Weights = from.ReadUInt32() };

			RawAddTile( key, paint );
		}

		return true;
	}
}
