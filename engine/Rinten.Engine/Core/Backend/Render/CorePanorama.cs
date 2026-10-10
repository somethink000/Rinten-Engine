using System.Threading.Tasks;
using Vec3 = System.Numerics.Vector3;

namespace Rinten;

//A panorama read back from its compiled picture, for what cannot read it on the card: the bake's sky, and the portable
//frame's table. Directions map onto it as PanoramaUv in frame.slang: u round from -X, v down from the zenith
internal sealed class CorePanorama
{
	//The widest level read back; finer than any bake's sky or reflection resolves
	private const int ReadWidth = 512;

	//The widest level convolved into the table; irradiance holds nothing finer
	private const int SourceWidth = 64;

	//The levels read back, widest first, each half the one before
	private readonly List<(float[] Rgb, int Width, int Height)> chain = new();

	//The table in the panorama's own turn, unscaled: rgb a texel, CoreSkyTable.Side square; made on first asking
	private float[] table;

	private CorePanorama( CoreTextureFile file, int first )
	{
		for ( var mip = first; mip < file.MipCount; mip++ )
		{
			var light = file.LightLevel( mip );
			if ( light is null ) break;

			var size = file.MipInfo( mip );
			chain.Add( (light, size.Width, size.Height) );
		}
	}

	/// <summary>Reads a compiled HDR picture, or null where it is not one.</summary>
	public static CorePanorama Load( string compiled )
	{
		var file = CoreTextureFile.Load( compiled );
		if ( file?.Format is not ( CoreTextureFile.TextureFormat.Rgb9e5 or CoreTextureFile.TextureFormat.Bc6h ) ) return null;

		var first = 0;
		while ( first < file.MipCount - 1 && file.MipInfo( first ).Width > ReadWidth ) first++;

		var panorama = new CorePanorama( file, first );
		return panorama.chain.Count > 0 ? panorama : null;
	}

	/// <summary>Where a direction lands on the picture, turned <paramref name="turn"/> radians about up.</summary>
	public static (float U, float V) Uv( Vec3 direction, float turn )
	{
		var (s, c) = MathF.SinCos( turn );

		var x = ( direction.X * c ) - ( direction.Z * s );
		var z = ( direction.X * s ) + ( direction.Z * c );

		var u = ( MathF.Atan2( -z, -x ) * ( 0.5f / MathF.PI ) ) + 0.5f;
		var v = 0.5f - ( MathF.Asin( Math.Clamp( direction.Y, -1.0f, 1.0f ) ) / MathF.PI );

		return (u, v);
	}

	//The direction at a point on the picture, unturned - Uv's inverse
	private static Vec3 Direction( float u, float v )
	{
		var (sinAround, cosAround) = MathF.SinCos( ( u - 0.5f ) * 2.0f * MathF.PI );
		var (sinUp, cosUp) = MathF.SinCos( ( 0.5f - v ) * MathF.PI );

		return new Vec3( -cosAround * cosUp, sinUp, -sinAround * cosUp );
	}

	/// <summary>The light arriving from a direction, before brightness - bilinear on the widest level read.</summary>
	public Vec3 Radiance( Vec3 direction, float turn )
	{
		var (u, v) = Uv( direction, turn );
		return Bilinear( 0, u, v );
	}

	//Bilinear on one level, round the horizon wrapping and down the picture stopping at the poles
	private Vec3 Bilinear( int level, float u, float v )
	{
		var (rgb, width, height) = chain[level];

		var x = ( u * width ) - 0.5f;
		var y = Math.Clamp( ( v * height ) - 0.5f, 0.0f, height - 1 );

		var x0 = (int)MathF.Floor( x );
		var y0 = (int)y;
		var fx = x - x0;
		var fy = y - y0;
		var y1 = Math.Min( y0 + 1, height - 1 );

		x0 = ( ( x0 % width ) + width ) % width;
		var x1 = ( x0 + 1 ) % width;

		Vec3 Texel( int tx, int ty )
		{
			var at = ( ( ty * width ) + tx ) * 3;
			return new Vec3( rgb[at], rgb[at + 1], rgb[at + 2] );
		}

		var top = Vec3.Lerp( Texel( x0, y0 ), Texel( x1, y0 ), fx );
		var bottom = Vec3.Lerp( Texel( x0, y1 ), Texel( x1, y1 ), fx );

		return Vec3.Lerp( top, bottom, fy );
	}

	/// <summary>The frame's sky table for this panorama, four halves a texel, in the world's turn.</summary>
	public Half[] Table( Vec3 light, float turn )
	{
		const int side = CoreSkyTable.Side;

		if ( table is null )
		{
			var source = chain.Find( level => level.Width <= SourceWidth );
			table = Convolve( source.Rgb, source.Width, source.Height );
		}

		var texels = new Half[side * side * 4];

		//Turning about up only slides the picture sideways, by the turn's share of a full circle
		var shift = turn / ( 2.0f * MathF.PI ) * side;

		for ( var row = 0; row < side; row++ )
		{
			for ( var column = 0; column < side; column++ )
			{
				var from = column + shift;
				var left = (int)MathF.Floor( from );
				var t = from - left;

				left = ( ( left % side ) + side ) % side;
				var right = ( left + 1 ) % side;

				var a = ( ( row * side ) + left ) * 3;
				var b = ( ( row * side ) + right ) * 3;
				var at = ( ( row * side ) + column ) * 4;

				texels[at] = (Half)( float.Lerp( table[a], table[b], t ) * light.X );
				texels[at + 1] = (Half)( float.Lerp( table[a + 1], table[b + 1], t ) * light.Y );
				texels[at + 2] = (Half)( float.Lerp( table[a + 2], table[b + 2], t ) * light.Z );

				//One says a table is there - see SkyTableAt in frame.slang
				texels[at + 3] = (Half)1.0f;
			}
		}

		return texels;
	}

	//Cosine-weighted mean radiance about each table texel's direction: irradiance over pi
	private static float[] Convolve( float[] source, int width, int height )
	{
		const int side = CoreSkyTable.Side;

		var table = new float[side * side * 3];
		if ( source is null || width <= 0 || height <= 0 ) return table;

		//Each texel's direction, and its share of the sphere - a row nearer a pole covers less of it
		var count = width * height;
		var directions = new Vec3[count];
		var areas = new float[count];

		for ( var y = 0; y < height; y++ )
		{
			for ( var x = 0; x < width; x++ )
			{
				var i = ( y * width ) + x;
				var direction = Direction( ( x + 0.5f ) / width, ( y + 0.5f ) / height );

				directions[i] = direction;
				areas[i] = MathF.Sqrt( MathF.Max( 0.0f, 1.0f - ( direction.Y * direction.Y ) ) );
			}
		}

		Parallel.For( 0, side, row =>
		{
			for ( var column = 0; column < side; column++ )
			{
				var normal = Direction( ( column + 0.5f ) / side, ( row + 0.5f ) / side );

				var sum = Vec3.Zero;
				var weight = 0.0f;

				for ( var i = 0; i < count; i++ )
				{
					var facing = Vec3.Dot( normal, directions[i] );
					if ( facing <= 0.0f ) continue;

					var w = facing * areas[i];

					sum += new Vec3( source[i * 3], source[( i * 3 ) + 1], source[( i * 3 ) + 2] ) * w;
					weight += w;
				}

				if ( weight > 0.0f ) sum /= weight;

				var at = ( ( row * side ) + column ) * 3;

				table[at] = sum.X;
				table[at + 1] = sum.Y;
				table[at + 2] = sum.Z;
			}
		} );

		return table;
	}
}
