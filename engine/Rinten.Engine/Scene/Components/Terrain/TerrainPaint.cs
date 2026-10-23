using System;

namespace Rinten;

//A byte apiece, the weights coming to one.
/// <summary>What a ground point is painted with: up to four layers by number, and how much of each.</summary>
public struct TerrainPaint : IEquatable<TerrainPaint>
{
	/// <summary>How many layers one point carries.</summary>
	public const int Most = 4;

	/// <summary>The layers' numbers, a byte each, the first in the low byte.</summary>
	public uint Layers;

	/// <summary>How much of each, a byte each, in the same order. A weight of nought is a place with no layer in it.</summary>
	public uint Weights;

	/// <summary>Which layer the slot holds.</summary>
	public readonly int LayerAt( int slot ) => (int)( ( Layers >> ( slot * 8 ) ) & 0xFFu );

	/// <summary>How much of it, nought to one.</summary>
	public readonly float WeightAt( int slot ) => ( ( Weights >> ( slot * 8 ) ) & 0xFFu ) / 255.0f;

	/// <summary>Whether anything is painted here at all.</summary>
	public readonly bool Painted => Weights != 0u;

	/// <summary>All of one layer.</summary>
	public static TerrainPaint Only( int layer ) => new() { Layers = (uint)Math.Clamp( layer, 0, 255 ), Weights = 255u };

	/// <summary>How much of a layer is here, nought to one.</summary>
	public readonly float Of( int layer )
	{
		var sum = 0.0f;

		for ( var slot = 0; slot < Most; slot++ )
			if ( LayerAt( slot ) == layer ) sum += WeightAt( slot );

		return sum;
	}

	/// <summary>Adds its weights, times a share, into one number a layer.</summary>
	public readonly void AddTo( Span<float> layers, float share )
	{
		for ( var slot = 0; slot < Most; slot++ )
		{
			var weight = WeightAt( slot );
			var layer = LayerAt( slot );

			if ( weight > 0.0f && layer < layers.Length ) layers[layer] += weight * share;
		}
	}

	/// <summary>The four heaviest of one number a layer, made to come to one. Nothing where they come to nothing.</summary>
	public static TerrainPaint Strongest( ReadOnlySpan<float> layers )
	{
		Span<int> picked = stackalloc int[Most];
		Span<float> held = stackalloc float[Most];

		var count = 0;

		for ( var layer = 0; layer < layers.Length && layer < 256; layer++ )
		{
			var weight = layers[layer];
			if ( !( weight > 0.0f ) ) continue;

			var at = count < Most ? count++ : -1;

			if ( at < 0 )
			{
				//Full: it takes the lightest one's place, if it is heavier.
				var lightest = 0;
				for ( var s = 1; s < Most; s++ ) if ( held[s] < held[lightest] ) lightest = s;

				if ( held[lightest] >= weight ) continue;

				at = lightest;
			}

			picked[at] = layer;
			held[at] = weight;
		}

		var total = 0.0f;
		for ( var s = 0; s < count; s++ ) total += held[s];

		if ( total <= 1e-6f ) return default;

		//Rounded, and what rounding lost or gained given to the heaviest, so the bytes come to exactly one.
		Span<int> bytes = stackalloc int[Most];
		var sum = 0;
		var heaviest = 0;

		for ( var s = 0; s < count; s++ )
		{
			bytes[s] = (int)MathF.Round( held[s] / total * 255.0f );
			sum += bytes[s];

			if ( held[s] > held[heaviest] ) heaviest = s;
		}

		bytes[heaviest] = Math.Clamp( bytes[heaviest] + 255 - sum, 0, 255 );

		var paint = new TerrainPaint();

		for ( var s = 0; s < count; s++ )
		{
			if ( bytes[s] == 0 ) continue;

			paint.Layers |= (uint)picked[s] << ( s * 8 );
			paint.Weights |= (uint)bytes[s] << ( s * 8 );
		}

		return paint;
	}

	/// <summary>More of one layer by an amount, nought to one, the rest scaled back to keep a sum of one.</summary>
	public readonly TerrainPaint With( int layer, float amount )
	{
		if ( amount <= 0.0f || layer < 0 || layer >= Terrain.MostLayers ) return this;

		Span<float> layers = stackalloc float[Terrain.MostLayers];
		layers.Clear();

		AddTo( layers, 1.0f );

		var had = layers[layer];
		var others = 0.0f;

		for ( var i = 0; i < layers.Length; i++ ) if ( i != layer ) others += layers[i];

		var now = Math.Clamp( had + amount, 0.0f, 1.0f );

		//Nothing else painted and this one short of whole: it is whole.
		if ( others <= 1e-6f ) return Only( layer );

		var scale = ( 1.0f - now ) / others;
		for ( var i = 0; i < layers.Length; i++ ) layers[i] = i == layer ? now : layers[i] * scale;

		return Strongest( layers );
	}

	/// <summary>Three paints weighed together, as a point inside a triangle between them is.</summary>
	public static TerrainPaint Mix( in TerrainPaint a, in TerrainPaint b, in TerrainPaint c, float wa, float wb, float wc )
	{
		Span<float> layers = stackalloc float[Terrain.MostLayers];
		layers.Clear();

		a.AddTo( layers, wa );
		b.AddTo( layers, wb );
		c.AddTo( layers, wc );

		return Strongest( layers );
	}

	/// <summary>Halfway between two.</summary>
	public static TerrainPaint Between( in TerrainPaint a, in TerrainPaint b ) => Mix( a, b, default, 0.5f, 0.5f, 0.0f );

	/// <summary>Out of the eight weights a vertex once carried, one per layer of the first eight.</summary>
	public static TerrainPaint FromEight( Color32 first, Vector4 last )
	{
		Span<float> layers = stackalloc float[Terrain.MostLayers];
		layers.Clear();

		layers[0] = first.r / 255.0f;
		layers[1] = first.g / 255.0f;
		layers[2] = first.b / 255.0f;
		layers[3] = first.a / 255.0f;
		layers[4] = last.x;
		layers[5] = last.y;
		layers[6] = last.z;
		layers[7] = last.w;

		return Strongest( layers );
	}

	public readonly bool Equals( TerrainPaint other ) => Layers == other.Layers && Weights == other.Weights;
	public override readonly bool Equals( object other ) => other is TerrainPaint paint && Equals( paint );
	public override readonly int GetHashCode() => HashCode.Combine( Layers, Weights );

	public static bool operator ==( TerrainPaint a, TerrainPaint b ) => a.Equals( b );
	public static bool operator !=( TerrainPaint a, TerrainPaint b ) => !a.Equals( b );
}
