using System.Threading.Tasks;
using Vec3 = System.Numerics.Vector3;

namespace Rinten;

//A panorama read back from its compiled picture: the bake's sky, the ambient table and the reflection levels.
//Directions map onto it as PanoramaUv in frame.slang: u round from -X, v down from the zenith
internal sealed class CorePanorama
{
	//The widest level read back; finer than any bake's sky or reflection resolves
	private const int ReadWidth = 512;

	//The widest level convolved into the table; irradiance holds nothing finer
	private const int SourceWidth = 64;

	//GGX samples a reflection texel takes; filtered importance sampling keeps so few free of noise
	private const int ReflectionSamples = 64;

	//The levels read back, widest first, each half the one before
	private readonly List<(float[] Rgb, int Width, int Height)> chain = new();

	//The table in the panorama's own turn, unscaled: rgb a texel, CoreSkyTable.Side square
	private readonly float[] table;

	/// <summary>Reflection levels, unscaled and unturned: level i is GGX at roughness i / ( levels - 1 ).</summary>
	public CoreTextureFile Reflections { get; }

	private CorePanorama( CoreTextureFile file, int first )
	{
		for ( var mip = first; mip < file.MipCount; mip++ )
		{
			var light = file.LightLevel( mip );
			if ( light is null ) break;

			var size = file.MipInfo( mip );
			chain.Add( (light, size.Width, size.Height) );
		}

		var source = chain.Find( level => level.Width <= SourceWidth );
		table = Convolve( source.Rgb, source.Width, source.Height );

		Reflections = Prefilter();
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

	//Trilinear across the levels read
	private Vec3 Trilinear( float level, Vec3 direction )
	{
		var (u, v) = Uv( direction, 0.0f );

		level = Math.Clamp( level, 0.0f, chain.Count - 1 );

		var finer = (int)level;
		var coarser = Math.Min( finer + 1, chain.Count - 1 );

		return Vec3.Lerp( Bilinear( finer, u, v ), Bilinear( coarser, u, v ), level - finer );
	}

	/// <summary>The frame's sky table for this panorama, four halves a texel, in the world's turn.</summary>
	public Half[] Table( Vec3 light, float turn )
	{
		const int side = CoreSkyTable.Side;

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

	//Karis's split-sum prefilter, view along the normal; samples read the level matching their share of the lobe
	//(Krivanek's filtered importance sampling). Level 0 is the picture itself
	private CoreTextureFile Prefilter()
	{
		//Down to eight across: a lobe at roughness one is wider than that holds
		var count = 1;
		while ( count < chain.Count && chain[0].Width >> count >= 8 ) count++;

		var levels = new (float[] Rgb, int Width, int Height)[count];
		levels[0] = chain[0];

		//What one texel of the widest level covers, on average over the sphere
		var texelAngle = 4.0f * MathF.PI / ( chain[0].Width * chain[0].Height );

		for ( var level = 1; level < count; level++ )
		{
			var width = Math.Max( 1, chain[0].Width >> level );
			var height = Math.Max( 1, chain[0].Height >> level );
			var roughness = level / (float)( count - 1 );
			var alpha = roughness * roughness;
			var rgb = new float[width * height * 3];

			Parallel.For( 0, height, y =>
			{
				for ( var x = 0; x < width; x++ )
				{
					var normal = Direction( ( x + 0.5f ) / width, ( y + 0.5f ) / height );
					var sum = Filtered( normal, alpha, texelAngle );

					var at = ( ( y * width ) + x ) * 3;

					rgb[at] = sum.X;
					rgb[at + 1] = sum.Y;
					rgb[at + 2] = sum.Z;
				}
			} );

			levels[level] = (rgb, width, height);
		}

		return CoreTextureFile.FromLight( levels );
	}

	//The GGX lobe about one direction, the NdotL-weighted mean of its samples
	private Vec3 Filtered( Vec3 normal, float alpha, float texelAngle )
	{
		var up = MathF.Abs( normal.Y ) < 0.999f ? Vec3.UnitY : Vec3.UnitX;
		var tangent = Vec3.Normalize( Vec3.Cross( up, normal ) );
		var bitangent = Vec3.Cross( normal, tangent );

		var alpha2 = alpha * alpha;
		var sum = Vec3.Zero;
		var weight = 0.0f;

		for ( uint i = 0; i < ReflectionSamples; i++ )
		{
			//Hammersley: an even spread with the radical inverse of the index
			var e1 = i / (float)ReflectionSamples;
			var e2 = ReverseBits( i ) * 2.3283064365386963e-10f;

			var cosTheta = MathF.Sqrt( ( 1.0f - e2 ) / ( 1.0f + ( ( alpha2 - 1.0f ) * e2 ) ) );
			var sinTheta = MathF.Sqrt( 1.0f - ( cosTheta * cosTheta ) );
			var (sinPhi, cosPhi) = MathF.SinCos( 2.0f * MathF.PI * e1 );

			var half = ( tangent * ( sinTheta * cosPhi ) ) + ( bitangent * ( sinTheta * sinPhi ) ) + ( normal * cosTheta );
			var toward = ( half * ( 2.0f * Vec3.Dot( normal, half ) ) ) - normal;

			var lit = Vec3.Dot( normal, toward );
			if ( lit <= 0.0f ) continue;

			//View along the normal makes the pdf D / 4; its solid angle against a texel's picks the level
			var d = cosTheta * cosTheta * ( alpha2 - 1.0f ) + 1.0f;
			var pdf = alpha2 / ( MathF.PI * d * d ) * 0.25f;
			var sampleAngle = 1.0f / ( ReflectionSamples * MathF.Max( pdf, 1e-6f ) );
			var level = alpha == 0.0f ? 0.0f : MathF.Max( 0.0f, ( 0.5f * MathF.Log2( sampleAngle / texelAngle ) ) + 1.0f );

			sum += Trilinear( level, toward ) * lit;
			weight += lit;
		}

		return weight > 0.0f ? sum / weight : Vec3.Zero;
	}

	private static uint ReverseBits( uint value )
	{
		value = ( value << 16 ) | ( value >> 16 );
		value = ( ( value & 0x00FF00FF ) << 8 ) | ( ( value & 0xFF00FF00 ) >> 8 );
		value = ( ( value & 0x0F0F0F0F ) << 4 ) | ( ( value & 0xF0F0F0F0 ) >> 4 );
		value = ( ( value & 0x33333333 ) << 2 ) | ( ( value & 0xCCCCCCCC ) >> 2 );
		return ( ( value & 0x55555555 ) << 1 ) | ( ( value & 0xAAAAAAAA ) >> 1 );
	}
}
