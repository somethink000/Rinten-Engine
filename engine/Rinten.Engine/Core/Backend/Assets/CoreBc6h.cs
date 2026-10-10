using System.Buffers.Binary;

namespace Rinten;

//BC6H, unsigned, decoded on the CPU - basisu's unpack_bc6h (basisu_gpu_texture.cpp) in C#, with its tables.
//For what reads an HDR picture back - a panorama's light, a device without BC - in every build, the page's included
internal static class CoreBc6h
{
	/// <summary>Bytes a block of four by four texels takes.</summary>
	public const int BlockBytes = 16;

	//The mode by a block's low five bits, -1 for the four that are reserved
	private static readonly sbyte[] ModeOf = [0, 1, 2, 10, 0, 1, 3, 11, 0, 1, 4, 12, 0, 1, 5, 13, 0, 1, 6, -1, 0, 1, 7, -1, 0, 1, 8, -1, 0, 1, 9, -1];

	private static readonly byte[] Weight3 = [0, 9, 18, 27, 37, 46, 55, 64];
	private static readonly byte[] Weight4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

	//Per mode, fields in block order: component (3 = partition), subset * 2 + end, last bit, first bit (-1 = one bit)
	private static readonly sbyte[][] Layouts =
	[
		[ 1, 2, 4, -1, 2, 2, 4, -1, 2, 3, 4, -1, 0, 0, 9, 0, 1, 0, 9, 0, 2, 0, 9, 0, 0, 1, 4, 0, 1, 3, 4, -1, 1, 2, 3, 0, 1, 1, 4, 0, 2, 3, 0, -1, 1, 3, 3, 0, 2, 1, 4, 0, 2, 3, 1, -1, 2, 2, 3, 0, 0, 2, 4, 0, 2, 3, 2, -1, 0, 3, 4, 0, 2, 3, 3, -1, 3, -1, 4, 0 ],
		[ 1, 2, 5, -1, 1, 3, 4, -1, 1, 3, 5, -1, 0, 0, 6, 0, 2, 3, 0, -1, 2, 3, 1, -1, 2, 2, 4, -1, 1, 0, 6, 0, 2, 2, 5, -1, 2, 3, 2, -1, 1, 2, 4, -1, 2, 0, 6, 0, 2, 3, 3, -1, 2, 3, 5, -1, 2, 3, 4, -1, 0, 1, 5, 0, 1, 2, 3, 0, 1, 1, 5, 0, 1, 3, 3, 0, 2, 1, 5, 0, 2, 2, 3, 0, 0, 2, 5, 0, 0, 3, 5, 0, 3, -1, 4, 0 ],
		[ 0, 0, 9, 0, 1, 0, 9, 0, 2, 0, 9, 0, 0, 1, 4, 0, 0, 0, 10, -1, 1, 2, 3, 0, 1, 1, 3, 0, 1, 0, 10, -1, 2, 3, 0, -1, 1, 3, 3, 0, 2, 1, 3, 0, 2, 0, 10, -1, 2, 3, 1, -1, 2, 2, 3, 0, 0, 2, 4, 0, 2, 3, 2, -1, 0, 3, 4, 0, 2, 3, 3, -1, 3, -1, 4, 0 ],
		[ 0, 0, 9, 0, 1, 0, 9, 0, 2, 0, 9, 0, 0, 1, 3, 0, 0, 0, 10, -1, 1, 3, 4, -1, 1, 2, 3, 0, 1, 1, 4, 0, 1, 0, 10, -1, 1, 3, 3, 0, 2, 1, 3, 0, 2, 0, 10, -1, 2, 3, 1, -1, 2, 2, 3, 0, 0, 2, 3, 0, 2, 3, 0, -1, 2, 3, 2, -1, 0, 3, 3, 0, 1, 2, 4, -1, 2, 3, 3, -1, 3, -1, 4, 0 ],
		[ 0, 0, 9, 0, 1, 0, 9, 0, 2, 0, 9, 0, 0, 1, 3, 0, 0, 0, 10, -1, 2, 2, 4, -1, 1, 2, 3, 0, 1, 1, 3, 0, 1, 0, 10, -1, 2, 3, 0, -1, 1, 3, 3, 0, 2, 1, 4, 0, 2, 0, 10, -1, 2, 2, 3, 0, 0, 2, 3, 0, 2, 3, 1, -1, 2, 3, 2, -1, 0, 3, 3, 0, 2, 3, 4, -1, 2, 3, 3, -1, 3, -1, 4, 0 ],
		[ 0, 0, 8, 0, 2, 2, 4, -1, 1, 0, 8, 0, 1, 2, 4, -1, 2, 0, 8, 0, 2, 3, 4, -1, 0, 1, 4, 0, 1, 3, 4, -1, 1, 2, 3, 0, 1, 1, 4, 0, 2, 3, 0, -1, 1, 3, 3, 0, 2, 1, 4, 0, 2, 3, 1, -1, 2, 2, 3, 0, 0, 2, 4, 0, 2, 3, 2, -1, 0, 3, 4, 0, 2, 3, 3, -1, 3, -1, 4, 0 ],
		[ 0, 0, 7, 0, 1, 3, 4, -1, 2, 2, 4, -1, 1, 0, 7, 0, 2, 3, 2, -1, 1, 2, 4, -1, 2, 0, 7, 0, 2, 3, 3, -1, 2, 3, 4, -1, 0, 1, 5, 0, 1, 2, 3, 0, 1, 1, 4, 0, 2, 3, 0, -1, 1, 3, 3, 0, 2, 1, 4, 0, 2, 3, 1, -1, 2, 2, 3, 0, 0, 2, 5, 0, 0, 3, 5, 0, 3, -1, 4, 0 ],
		[ 0, 0, 7, 0, 2, 3, 0, -1, 2, 2, 4, -1, 1, 0, 7, 0, 1, 2, 5, -1, 1, 2, 4, -1, 2, 0, 7, 0, 1, 3, 5, -1, 2, 3, 4, -1, 0, 1, 4, 0, 1, 3, 4, -1, 1, 2, 3, 0, 1, 1, 5, 0, 1, 3, 3, 0, 2, 1, 4, 0, 2, 3, 1, -1, 2, 2, 3, 0, 0, 2, 4, 0, 2, 3, 2, -1, 0, 3, 4, 0, 2, 3, 3, -1, 3, -1, 4, 0 ],
		[ 0, 0, 7, 0, 2, 3, 1, -1, 2, 2, 4, -1, 1, 0, 7, 0, 2, 2, 5, -1, 1, 2, 4, -1, 2, 0, 7, 0, 2, 3, 5, -1, 2, 3, 4, -1, 0, 1, 4, 0, 1, 3, 4, -1, 1, 2, 3, 0, 1, 1, 4, 0, 2, 3, 0, -1, 1, 3, 3, 0, 2, 1, 5, 0, 2, 2, 3, 0, 0, 2, 4, 0, 2, 3, 2, -1, 0, 3, 4, 0, 2, 3, 3, -1, 3, -1, 4, 0 ],
		[ 0, 0, 5, 0, 1, 3, 4, -1, 2, 3, 0, -1, 2, 3, 1, -1, 2, 2, 4, -1, 1, 0, 5, 0, 1, 2, 5, -1, 2, 2, 5, -1, 2, 3, 2, -1, 1, 2, 4, -1, 2, 0, 5, 0, 1, 3, 5, -1, 2, 3, 3, -1, 2, 3, 5, -1, 2, 3, 4, -1, 0, 1, 5, 0, 1, 2, 3, 0, 1, 1, 5, 0, 1, 3, 3, 0, 2, 1, 5, 0, 2, 2, 3, 0, 0, 2, 5, 0, 0, 3, 5, 0, 3, -1, 4, 0 ],
		[ 0, 0, 9, 0, 1, 0, 9, 0, 2, 0, 9, 0, 0, 1, 9, 0, 1, 1, 9, 0, 2, 1, 9, 0 ],
		[ 0, 0, 9, 0, 1, 0, 9, 0, 2, 0, 9, 0, 0, 1, 8, 0, 0, 0, 10, -1, 1, 1, 8, 0, 1, 0, 10, -1, 2, 1, 8, 0, 2, 0, 10, -1 ],
		[ 0, 0, 9, 0, 1, 0, 9, 0, 2, 0, 9, 0, 0, 1, 7, 0, 0, 0, 10, 11, 1, 1, 7, 0, 1, 0, 10, 11, 2, 1, 7, 0, 2, 0, 10, 11 ],
		[ 0, 0, 9, 0, 1, 0, 9, 0, 2, 0, 9, 0, 0, 1, 3, 0, 0, 0, 10, 15, 1, 1, 3, 0, 1, 0, 10, 15, 2, 1, 3, 0, 2, 0, 10, 15 ],
	];

	//Two-subset patterns, [pattern * 16 + y * 4 + x]; bit 7 marks an anchor, whose weight is a bit shorter
	private static readonly byte[] Patterns =
	[
		0x80, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x81,
		0x80, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x81,
		0x80, 0x01, 0x01, 0x01, 0x00, 0x01, 0x01, 0x01, 0x00, 0x01, 0x01, 0x01, 0x00, 0x01, 0x01, 0x81,
		0x80, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x01, 0x01, 0x81,
		0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 0x81,
		0x80, 0x00, 0x01, 0x01, 0x00, 0x01, 0x01, 0x01, 0x00, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x81,
		0x80, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x81,
		0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x01, 0x01, 0x81,
		0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 0x81,
		0x80, 0x00, 0x01, 0x01, 0x00, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x81,
		0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x81,
		0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x01, 0x81,
		0x80, 0x00, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x81,
		0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x81,
		0x80, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x81,
		0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01, 0x81,
		0x80, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01, 0x00, 0x01, 0x01, 0x01, 0x81,
		0x80, 0x01, 0x81, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x81, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01, 0x00,
		0x80, 0x01, 0x81, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
		0x80, 0x00, 0x81, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x80, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x81, 0x01, 0x00, 0x00, 0x01, 0x01, 0x01, 0x00,
		0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x81, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00,
		0x80, 0x01, 0x01, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x81,
		0x80, 0x00, 0x81, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
		0x80, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x81, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00,
		0x80, 0x01, 0x81, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00,
		0x80, 0x00, 0x81, 0x01, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x01, 0x01, 0x00, 0x00,
		0x80, 0x00, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x81, 0x01, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00,
		0x80, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01, 0x01, 0x81, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00,
		0x80, 0x01, 0x81, 0x01, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01, 0x00,
		0x80, 0x00, 0x81, 0x01, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x01, 0x01, 0x00, 0x00,
	];

	//Per mode: base bits, then red, green and blue delta (or second endpoint) bits
	private static readonly byte[] SignificantBits =
	[
		10, 5, 5, 5,
		7, 6, 6, 6,
		11, 5, 4, 4,
		11, 4, 5, 4,
		11, 4, 4, 5,
		9, 5, 5, 5,
		8, 6, 5, 5,
		8, 5, 6, 5,
		8, 5, 5, 6,
		6, 6, 6, 6,
		10, 10, 10, 10,
		11, 9, 9, 9,
		12, 8, 8, 8,
		16, 4, 4, 4,
	];

	/// <summary>One block into sixteen linear RGB texels in raster order; a reserved mode is black.</summary>
	public static void Decode( ReadOnlySpan<byte> block, Span<float> rgb )
	{
		var low = BinaryPrimitives.ReadUInt64LittleEndian( block );
		var high = BinaryPrimitives.ReadUInt64LittleEndian( block[8..] );

		var mode = ModeOf[(int)( low & 31 )];

		if ( mode < 0 )
		{
			rgb[..48].Clear();
			return;
		}

		Bits( mode < 2 ? 2 : 5, ref low, ref high );

		var subsets = mode >= 10 ? 1 : 2;
		var absolute = mode == 9 || mode == 10;

		//[subset][component][low/high]
		Span<int> ends = stackalloc int[12];
		var pattern = 0;

		var layout = Layouts[mode];

		for ( var i = 0; i < layout.Length; i += 4 )
		{
			int component = layout[i], index = layout[i + 1], last = layout[i + 2], first = layout[i + 3];

			int value;

			if ( first < 0 )
			{
				value = (int)Bits( 1, ref low, ref high ) << last;
			}
			else
			{
				var count = Math.Abs( last - first ) + 1;
				var read = (int)Bits( count, ref low, ref high );

				if ( last < first ) read = Reversed( read, count );

				value = read << Math.Min( first, last );
			}

			if ( component == 3 ) pattern |= value;
			else ends[( ( index >> 1 ) * 6 ) + ( component * 2 ) + ( index & 1 )] |= value;
		}

		var baseBits = SignificantBits[mode * 4];

		//Every endpoint after the first is a signed delta from it, where the mode says so
		if ( !absolute )
		{
			for ( var subset = 0; subset < subsets; subset++ )
				for ( var component = 0; component < 3; component++ )
					for ( var end = subset == 0 ? 1 : 0; end < 2; end++ )
					{
						var at = ( subset * 6 ) + ( component * 2 ) + end;
						var delta = SignExtend( ends[at], SignificantBits[( mode * 4 ) + 1 + component] );

						ends[at] = ( ends[component * 2] + delta ) & ( ( 1 << baseBits ) - 1 );
					}
		}

		for ( var i = 0; i < subsets * 6; i++ ) ends[i] = Dequantize( ends[i], baseBits );

		var weightBits = mode >= 10 ? 4 : 3;
		var weights = mode >= 10 ? Weight4 : Weight3;

		for ( var texel = 0; texel < 16; texel++ )
		{
			int subset = subsets == 1 ? ( texel == 0 ? 0x80 : 0 ) : Patterns[( pattern * 16 ) + texel];

			//An anchor texel's weight leaves out its top bit, which is always nought
			var weight = weights[(int)Bits( weightBits - ( ( subset & 0x80 ) != 0 ? 1 : 0 ), ref low, ref high )];

			subset &= 1;

			for ( var component = 0; component < 3; component++ )
			{
				var a = ends[( subset * 6 ) + ( component * 2 )];
				var b = ends[( subset * 6 ) + ( component * 2 ) + 1];

				//Interpolated in the quantized space, then scaled by 31/64 into a half's bits
				var mixed = ( ( a * ( 64 - weight ) ) + ( b * weight ) + 32 ) >> 6;

				rgb[( texel * 3 ) + component] = (float)BitConverter.UInt16BitsToHalf( (ushort)( ( mixed * 31 ) >> 6 ) );
			}
		}
	}

	//The next bits off the bottom of the block
	private static uint Bits( int count, ref ulong low, ref ulong high )
	{
		var value = (uint)( low & ( ( 1ul << count ) - 1 ) );

		low = ( low >> count ) | ( high << ( 64 - count ) );
		high >>= count;

		return value;
	}

	private static int Reversed( int value, int count )
	{
		var result = 0;

		for ( var i = 0; i < count; i++ )
			if ( ( value & ( 1 << i ) ) != 0 ) result |= 1 << ( count - 1 - i );

		return result;
	}

	private static int SignExtend( int value, int bits ) => ( value << ( 32 - bits ) ) >> ( 32 - bits );

	//An endpoint's bits widened to sixteen, the unsigned way
	private static int Dequantize( int value, int bits )
	{
		if ( bits >= 15 || value == 0 ) return value;
		if ( value == ( 1 << bits ) - 1 ) return 0xFFFF;

		return ( ( value << 16 ) + 0x8000 ) >> bits;
	}
}
