using System.Text.RegularExpressions;

namespace Editor.Tiles;

//How a picture is cut into tiles: a size in its name, blank lines between tiles, or the seams of a packed sheet
internal static class TileSheetGuess
{
	internal readonly record struct Grid( Vector2Int Size, Vector2Int Margin, Vector2Int Spacing, string How );

	private readonly record struct Cut( int Size, int Margin, int Spacing, string How );

	//Tried in this order when the picture shows no grid of its own
	private static readonly int[] CommonSizes = { 16, 32, 8, 24, 48, 64, 18, 12, 20, 128 };

	private const int MinSize = 4;
	private const int MaxSize = 512;
	private const int MaxSpacing = 4;
	private const int MaxMargin = 8;

	//Per line of one axis. Blank: every pixel see-through, or the corner's colour on a sheet with none see-through.
	//Seam: how much a line differs from the one before. Clear: how see-through it and the one before are
	private sealed record Lines( bool[] Blank, double[] Seam, double[] Clear );

	public static Grid Guess( Bitmap bitmap, string name )
	{
		var w = bitmap.Width;
		var h = bitmap.Height;
		var (columns, rows) = Measure( bitmap.GetPixels32(), w, h );

		Cut? x, y;

		if ( SizeFromName( name ) is { } hint && hint.x <= w && hint.y <= h )
		{
			x = Hinted( columns.Blank, hint.x );
			y = Hinted( rows.Blank, hint.y );
		}
		else
		{
			x = Spaced( columns.Blank ) ?? Packed( columns.Seam, columns.Clear );
			y = Spaced( rows.Blank ) ?? Packed( rows.Seam, rows.Clear );
		}

		//An axis that showed nothing borrows from the other - tiles are mostly square
		if ( x is null && y is { } fy ) (y, x) = Borrow( w, fy );
		if ( y is null && x is { } fx ) (x, y) = Borrow( h, fx );

		if ( x is null || y is null )
		{
			var size = CommonSizes.FirstOrDefault( s => w % s == 0 && h % s == 0 );
			if ( size == 0 ) size = Math.Clamp( Gcd( w, h ), 1, Math.Min( 16, Math.Min( w, h ) ) );

			x ??= new Cut( size, 0, 0, "common size" );
			y ??= new Cut( size, 0, 0, "common size" );
		}

		var cx = x.Value;
		var cy = y.Value;
		var how = cx.How == cy.How ? cx.How : $"{cx.How} across, {cy.How} down";

		return new Grid( new( cx.Size, cy.Size ), new( cx.Margin, cy.Margin ), new( cx.Spacing, cy.Spacing ), how );
	}

	private static (Lines Columns, Lines Rows) Measure( Color32[] pixels, int w, int h )
	{
		var columns = new Lines( new bool[w], new double[w], new double[w] );
		var rows = new Lines( new bool[h], new double[h], new double[h] );

		var hasAlpha = pixels.Any( p => p.a < 250 );
		var corner = pixels[0];

		bool Empty( Color32 p ) => hasAlpha
			? p.a < 16
			: Math.Abs( p.r - corner.r ) <= 6 && Math.Abs( p.g - corner.g ) <= 6 && Math.Abs( p.b - corner.b ) <= 6;

		static int Difference( Color32 a, Color32 b ) =>
			Math.Abs( a.r * a.a - b.r * b.a ) / 255 + Math.Abs( a.g * a.a - b.g * b.a ) / 255
			+ Math.Abs( a.b * a.a - b.b * b.a ) / 255 + Math.Abs( a.a - b.a );

		var emptyColumns = new int[w];
		var emptyRows = new int[h];

		for ( var py = 0; py < h; py++ )
		{
			var row = py * w;

			for ( var px = 0; px < w; px++ )
			{
				var p = pixels[row + px];

				if ( Empty( p ) )
				{
					emptyColumns[px]++;
					emptyRows[py]++;
				}

				if ( px > 0 ) columns.Seam[px] += Difference( p, pixels[row + px - 1] );
				if ( py > 0 ) rows.Seam[py] += Difference( p, pixels[row - w + px] );
			}
		}

		Finish( columns, emptyColumns, h );
		Finish( rows, emptyRows, w );

		return (columns, rows);

		static void Finish( Lines lines, int[] empty, int along )
		{
			for ( var i = 0; i < lines.Blank.Length; i++ )
			{
				lines.Blank[i] = empty[i] == along;
				lines.Seam[i] /= along;
				if ( i > 0 ) lines.Clear[i] = ( empty[i] + empty[i - 1] ) * 0.5 / along;
			}
		}
	}

	//"tiles_16x16", "tiles_16x24", "props_32px"
	private static Vector2Int? SizeFromName( string name )
	{
		if ( string.IsNullOrEmpty( name ) ) return null;

		var pair = Regex.Match( name, @"(?<!\d)(\d{1,3})\s*[xX×]\s*(\d{1,3})(?!\d)" );
		if ( pair.Success && Sensible( int.Parse( pair.Groups[1].Value ) ) && Sensible( int.Parse( pair.Groups[2].Value ) ) )
			return new Vector2Int( int.Parse( pair.Groups[1].Value ), int.Parse( pair.Groups[2].Value ) );

		var square = Regex.Match( name, @"(?<!\d)(\d{1,3})\s*px", RegexOptions.IgnoreCase );
		if ( square.Success && Sensible( int.Parse( square.Groups[1].Value ) ) )
			return new Vector2Int( int.Parse( square.Groups[1].Value ) );

		return null;

		//Below eight it is more likely a count of tiles than their size
		static bool Sensible( int size ) => size >= 8 && size <= MaxSize;
	}

	private static Cut Hinted( bool[] blank, int size )
	{
		if ( blank.Length % size == 0 ) return new Cut( size, 0, 0, "size from the name" );

		return Spaced( blank, size ) is { } spaced
			? spaced with { How = "size from the name" }
			: new Cut( size, 0, 0, "size from the name" );
	}

	//The shortest period whose gap lines are all blank. Where the period divides the sheet the tiles are packed and
	//the gaps are their own see-through edges, so the whole period is the tile
	private static Cut? Spaced( bool[] blank, int fixedSize = 0 )
	{
		var len = blank.Length;

		for ( var period = MinSize + 1; period <= Math.Min( MaxSize + MaxSpacing, len / 2 ); period++ )
		{
			for ( var spacing = 1; spacing <= MaxSpacing; spacing++ )
			{
				var size = period - spacing;
				if ( size < MinSize || ( fixedSize > 0 && size != fixedSize ) ) continue;

				for ( var margin = 0; margin <= MaxMargin; margin++ )
				{
					if ( !Gapped( blank, size, margin, spacing ) ) continue;

					if ( fixedSize == 0 && len % period == 0 ) return new Cut( period, 0, 0, "blank edges" );

					return new Cut( size, margin, spacing, "blank lines between tiles" );
				}
			}
		}

		return null;
	}

	//Whether tiles of this size, after this margin and with this spacing, fill the line with only blank lines between
	private static bool Gapped( bool[] blank, int size, int margin, int spacing )
	{
		var len = blank.Length;
		var period = size + spacing;
		var count = ( len - margin + spacing ) / period;
		if ( count < 2 ) return false;

		//What is left after the last tile is no margin or the same margin again
		var left = len - margin - ( count * period - spacing );
		if ( left != 0 && left != margin ) return false;

		for ( var i = 0; i < margin; i++ )
			if ( !blank[i] || ( left > 0 && !blank[len - 1 - i] ) ) return false;

		var filled = 0;

		for ( var t = 0; t < count; t++ )
		{
			var start = margin + t * period;

			if ( t < count - 1 )
			{
				for ( var g = 0; g < spacing; g++ )
					if ( !blank[start + size + g] ) return false;
			}

			for ( var i = 0; i < size; i++ )
			{
				if ( blank[start + i] ) continue;

				filled++;
				break;
			}
		}

		//A sheet mostly empty along this line proves nothing
		return filled * 2 >= count;
	}

	//Packed tiles: the size whose boundaries stand out, by colour seams (tiles drawn edge to edge) or by
	//see-through lines either side (sprites on a clear sheet) - whichever stands out more
	private static Cut? Packed( double[] seam, double[] clear )
	{
		var bySeam = Packed( seam, "seams" );
		var byClear = Packed( clear, "clear edges" );

		if ( bySeam is null ) return byClear?.Cut;
		if ( byClear is null ) return bySeam?.Cut;

		return bySeam.Value.Score >= byClear.Value.Score ? bySeam.Value.Cut : byClear.Value.Cut;
	}

	//The size whose boundaries score highest, then down through its divisors while the boundaries each adds stand out
	//too: art two or four tiles wide makes a multiple of the tile score highest
	private static (Cut Cut, double Score)? Packed( double[] signal, string how )
	{
		var len = signal.Length;
		if ( len < 2 * MinSize ) return null;

		var mean = 0.0;
		for ( var i = 1; i < len; i++ ) mean += signal[i];
		mean /= len - 1;

		var variance = 0.0;
		for ( var i = 1; i < len; i++ ) variance += ( signal[i] - mean ) * ( signal[i] - mean );
		var deviation = Math.Sqrt( variance / ( len - 1 ) );
		if ( deviation < 1e-6 ) return null;

		//A t-score of the lines at multiples of size, leaving out multiples of skip
		double Score( int size, int skip, out double ratio )
		{
			var sum = 0.0;
			var count = 0;

			for ( var b = size; b < len; b += size )
			{
				if ( skip > 0 && b % skip == 0 ) continue;

				sum += signal[b];
				count++;
			}

			ratio = count == 0 ? 0 : sum / count / Math.Max( mean, 1e-9 );

			return count == 0 ? 0 : ( sum / count - mean ) * Math.Sqrt( count ) / deviation;
		}

		var best = 0;
		var bestScore = 0.0;

		for ( var size = MinSize; size <= Math.Min( MaxSize, len / 2 ); size++ )
		{
			if ( len % size != 0 ) continue;

			var score = Score( size, 0, out _ );
			if ( score > bestScore )
			{
				bestScore = score;
				best = size;
			}
		}

		if ( bestScore < 3.0 ) return null;

		for ( var refined = true; refined; )
		{
			refined = false;

			for ( var divisor = best - 1; divisor >= MinSize; divisor-- )
			{
				if ( best % divisor != 0 ) continue;
				if ( Score( divisor, best, out var ratio ) < 2.5 || ratio < 1.3 ) continue;

				best = divisor;
				refined = true;
				break;
			}
		}

		return (new Cut( best, 0, 0, how ), bestScore);
	}

	//The other axis's size where it fits this one, else the largest size that divides both - and then the other axis
	//takes it too, since its size was a whole number of these tiles. Returns the other axis, then this one
	private static (Cut? Other, Cut? This) Borrow( int len, Cut other )
	{
		if ( len % other.Size == 0 ) return (other, new Cut( other.Size, 0, 0, other.How ));

		if ( other.Spacing > 0 || other.Margin > 0 )
		{
			var period = other.Size + other.Spacing;
			var left = len - other.Margin - ( ( len - other.Margin + other.Spacing ) / period * period - other.Spacing );

			return left == 0 || left == other.Margin ? (other, other) : (other, null);
		}

		var common = Gcd( len, other.Size );
		if ( common < MinSize ) return (other, null);

		var cut = new Cut( common, 0, 0, other.How );
		return (cut, cut);
	}

	private static int Gcd( int a, int b ) => b == 0 ? a : Gcd( b, a % b );
}
