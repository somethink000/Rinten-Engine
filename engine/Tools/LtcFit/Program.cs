using System;
using System.IO;
using System.Threading.Tasks;

namespace Rinten.LtcFit;

//Fits the area lights' LTC tables to GGX (Heitz et al. 2016): dotnet run -- <out.bin>
//Writes two 64x64 RGBA half tables, inverse matrix then magnitude and Fresnel - see CoreAreaLights
internal static class Program
{
	//Texels along each side: roughness across, sqrt( 1 - cos theta ) down
	private const int Size = 64;

	//Samples along each side of the square the error and the averages are taken over
	private const int Samples = 32;

	private const double MinAlpha = 0.00001;

	private static int Main( string[] args )
	{
		if ( args.Length < 1 )
		{
			Console.Error.WriteLine( "usage: LtcFit <out.bin> | LtcFit --check <table.bin>" );
			return 1;
		}

		if ( args[0] == "--check" ) return Check.Run( args[1] );

		var fits = new Fit[Size * Size];

		//The theta = 0 column first, each roughness starting from the rougher one; then each row on its own, theta by theta
		var column = new Fit { M11 = 1.0, M22 = 1.0 };

		for ( var a = Size - 1; a >= 0; --a )
		{
			column = FitOne( a, 0, column );
			fits[a] = column;
		}

		Parallel.For( 0, Size, a =>
		{
			var previous = fits[a];

			for ( var t = 1; t < Size; ++t )
			{
				previous = FitOne( a, t, previous );
				fits[a + ( t * Size )] = previous;
			}
		} );

		var table = new Half[Size * Size * 8];

		for ( var i = 0; i < Size * Size; ++i )
		{
			var inverse = fits[i].Ltc.Inverse;
			var scale = 1.0 / inverse.M11;

			table[( i * 4 ) + 0] = (Half)( inverse.M00 * scale );
			table[( i * 4 ) + 1] = (Half)( inverse.M02 * scale );
			table[( i * 4 ) + 2] = (Half)( inverse.M20 * scale );
			table[( i * 4 ) + 3] = (Half)( inverse.M22 * scale );

			var second = ( Size * Size * 4 ) + ( i * 4 );
			table[second + 0] = (Half)fits[i].Ltc.Magnitude;
			table[second + 1] = (Half)fits[i].Ltc.Fresnel;
		}

		using var file = File.Create( args[0] );
		using var writer = new BinaryWriter( file );

		foreach ( var value in table ) writer.Write( value );

		Console.WriteLine( $"wrote {args[0]}: {Size}x{Size}, two tables" );
		return 0;
	}

	private sealed class Fit
	{
		public double M11, M22, M13;
		public Ltc Ltc;
	}

	private static Fit FitOne( int a, int t, Fit previous )
	{
		var x = t / (double)( Size - 1 );
		var theta = Math.Min( 1.57, Math.Acos( 1.0 - ( x * x ) ) );
		var v = new Vec3( Math.Sin( theta ), 0.0, Math.Cos( theta ) );

		var roughness = a / (double)( Size - 1 );
		var alpha = Math.Max( roughness * roughness, MinAlpha );

		Averages( v, alpha, out var norm, out var fresnel, out var average );

		var ltc = new Ltc { Magnitude = norm, Fresnel = fresnel };
		var isotropic = t == 0;

		if ( isotropic )
		{
			ltc.X = new Vec3( 1, 0, 0 );
			ltc.Y = new Vec3( 0, 1, 0 );
			ltc.Z = new Vec3( 0, 0, 1 );
		}
		else
		{
			ltc.X = new Vec3( average.Z, 0, -average.X );
			ltc.Y = new Vec3( 0, 1, 0 );
			ltc.Z = average;
		}

		double[] start = isotropic
			? [Math.Max( previous.M11, MinAlpha ), 0.0, 0.0]
			: [previous.M11, previous.M22, previous.M13];

		double Error( double[] p )
		{
			Set( ltc, p, isotropic );
			return ErrorOf( ltc, v, alpha );
		}

		var best = NelderMead( start, 0.05, 1e-5, 100, Error );
		Set( ltc, best, isotropic );

		return new Fit { M11 = ltc.M11, M22 = ltc.M22, M13 = ltc.M13, Ltc = ltc };
	}

	private static void Set( Ltc ltc, double[] p, bool isotropic )
	{
		if ( isotropic )
		{
			ltc.M11 = Math.Max( p[0], 1e-7 );
			ltc.M22 = ltc.M11;
			ltc.M13 = 0.0;
		}
		else
		{
			ltc.M11 = Math.Max( p[0], 1e-7 );
			ltc.M22 = Math.Max( p[1], 1e-7 );
			ltc.M13 = p[2];
		}

		ltc.Update();
	}

	//The lobe's integral, its Schlick part and the direction it leans, by sampling GGX
	private static void Averages( Vec3 v, double alpha, out double norm, out double fresnel, out Vec3 average )
	{
		norm = 0.0;
		fresnel = 0.0;
		average = default;

		for ( var j = 0; j < Samples; ++j )
		{
			for ( var i = 0; i < Samples; ++i )
			{
				var l = Ggx.Sample( v, alpha, ( i + 0.5 ) / Samples, ( j + 0.5 ) / Samples );
				var value = Ggx.Eval( v, l, alpha, out var pdf );
				if ( pdf <= 0.0 ) continue;

				var weight = value / pdf;
				var h = ( v + l ).Normalized();

				norm += weight;
				fresnel += weight * Math.Pow( 1.0 - Math.Max( Vec3.Dot( v, h ), 0.0 ), 5.0 );
				average += l * weight;
			}
		}

		norm /= Samples * Samples;
		fresnel /= Samples * Samples;

		average = new Vec3( average.X, 0.0, average.Z ).Normalized();
	}

	//Cubed difference over both sampling strategies, MIS-weighted
	private static double ErrorOf( Ltc ltc, Vec3 v, double alpha )
	{
		var error = 0.0;

		for ( var j = 0; j < Samples; ++j )
		{
			for ( var i = 0; i < Samples; ++i )
			{
				var u1 = ( i + 0.5 ) / Samples;
				var u2 = ( j + 0.5 ) / Samples;

				error += One( ltc, v, alpha, ltc.Sample( u1, u2 ) );
				error += One( ltc, v, alpha, Ggx.Sample( v, alpha, u1, u2 ) );
			}
		}

		return error / ( Samples * Samples );
	}

	private static double One( Ltc ltc, Vec3 v, double alpha, Vec3 l )
	{
		var brdf = Ggx.Eval( v, l, alpha, out var pdfBrdf );
		var fitted = ltc.Eval( l );
		var pdfLtc = fitted / ltc.Magnitude;

		var difference = Math.Abs( brdf - fitted );
		var cubed = difference * difference * difference;

		return pdfLtc + pdfBrdf > 0.0 ? cubed / ( pdfLtc + pdfBrdf ) : 0.0;
	}

	private static double[] NelderMead( double[] start, double delta, double tolerance, int iterations, Func<double[], double> f )
	{
		var n = start.Length;
		var points = new double[n + 1][];
		var values = new double[n + 1];

		for ( var i = 0; i <= n; ++i )
		{
			points[i] = (double[])start.Clone();
			if ( i > 0 ) points[i][i - 1] += delta;
			values[i] = f( points[i] );
		}

		var centre = new double[n];
		var trial = new double[n];
		var further = new double[n];

		for ( var iteration = 0; iteration < iterations; ++iteration )
		{
			Array.Sort( values, points );

			if ( Math.Abs( values[n] - values[0] ) <= tolerance * ( Math.Abs( values[0] ) + Math.Abs( values[n] ) + 1e-30 ) ) break;

			Array.Clear( centre );
			for ( var i = 0; i < n; ++i )
				for ( var k = 0; k < n; ++k ) centre[k] += points[i][k] / n;

			for ( var k = 0; k < n; ++k ) trial[k] = centre[k] + ( centre[k] - points[n][k] );
			var reflected = f( trial );

			if ( reflected < values[0] )
			{
				for ( var k = 0; k < n; ++k ) further[k] = centre[k] + ( 2.0 * ( centre[k] - points[n][k] ) );
				var expanded = f( further );

				if ( expanded < reflected ) Take( points, values, n, further, expanded );
				else Take( points, values, n, trial, reflected );

				continue;
			}

			if ( reflected < values[n - 1] )
			{
				Take( points, values, n, trial, reflected );
				continue;
			}

			//Contract towards the better of the worst and its reflection
			var outside = reflected < values[n];
			for ( var k = 0; k < n; ++k )
				further[k] = centre[k] + ( 0.5 * ( ( outside ? trial[k] : points[n][k] ) - centre[k] ) );

			var contracted = f( further );

			if ( contracted < Math.Min( reflected, values[n] ) )
			{
				Take( points, values, n, further, contracted );
				continue;
			}

			//Shrink everything towards the best
			for ( var i = 1; i <= n; ++i )
			{
				for ( var k = 0; k < n; ++k ) points[i][k] = points[0][k] + ( 0.5 * ( points[i][k] - points[0][k] ) );
				values[i] = f( points[i] );
			}
		}

		Array.Sort( values, points );
		return points[0];
	}

	private static void Take( double[][] points, double[] values, int at, double[] point, double value )
	{
		Array.Copy( point, points[at], point.Length );
		values[at] = value;
	}
}

internal readonly record struct Vec3( double X, double Y, double Z )
{
	public static Vec3 operator +( Vec3 a, Vec3 b ) => new( a.X + b.X, a.Y + b.Y, a.Z + b.Z );
	public static Vec3 operator -( Vec3 a, Vec3 b ) => new( a.X - b.X, a.Y - b.Y, a.Z - b.Z );
	public static Vec3 operator -( Vec3 a ) => new( -a.X, -a.Y, -a.Z );
	public static Vec3 operator *( Vec3 a, double s ) => new( a.X * s, a.Y * s, a.Z * s );

	public static double Dot( Vec3 a, Vec3 b ) => ( a.X * b.X ) + ( a.Y * b.Y ) + ( a.Z * b.Z );

	public double Length => Math.Sqrt( Dot( this, this ) );

	public Vec3 Normalized() => this * ( 1.0 / Length );
}

//Row-major: Mrc is row r, column c
internal readonly record struct Mat3( double M00, double M01, double M02, double M10, double M11, double M12, double M20, double M21, double M22 )
{
	public static Vec3 operator *( Mat3 m, Vec3 v ) => new(
		( m.M00 * v.X ) + ( m.M01 * v.Y ) + ( m.M02 * v.Z ),
		( m.M10 * v.X ) + ( m.M11 * v.Y ) + ( m.M12 * v.Z ),
		( m.M20 * v.X ) + ( m.M21 * v.Y ) + ( m.M22 * v.Z ) );

	public double Determinant =>
		( M00 * ( ( M11 * M22 ) - ( M12 * M21 ) ) ) - ( M01 * ( ( M10 * M22 ) - ( M12 * M20 ) ) ) + ( M02 * ( ( M10 * M21 ) - ( M11 * M20 ) ) );

	public Mat3 Inverse()
	{
		var d = 1.0 / Determinant;

		return new Mat3(
			( ( M11 * M22 ) - ( M12 * M21 ) ) * d, ( ( M02 * M21 ) - ( M01 * M22 ) ) * d, ( ( M01 * M12 ) - ( M02 * M11 ) ) * d,
			( ( M12 * M20 ) - ( M10 * M22 ) ) * d, ( ( M00 * M22 ) - ( M02 * M20 ) ) * d, ( ( M02 * M10 ) - ( M00 * M12 ) ) * d,
			( ( M10 * M21 ) - ( M11 * M20 ) ) * d, ( ( M01 * M20 ) - ( M00 * M21 ) ) * d, ( ( M00 * M11 ) - ( M01 * M10 ) ) * d );
	}
}

//A clamped cosine stretched by M: its X, Y, Z frame times [[m11, 0, m13], [0, m22, 0], [0, 0, 1]]
internal sealed class Ltc
{
	public double Magnitude = 1.0, Fresnel = 1.0;
	public double M11 = 1.0, M22 = 1.0, M13;
	public Vec3 X = new( 1, 0, 0 ), Y = new( 0, 1, 0 ), Z = new( 0, 0, 1 );

	public Mat3 M, Inverse;
	private double determinant;

	public void Update()
	{
		//Columns: X * m11, Y * m22, X * m13 + Z
		M = new Mat3(
			X.X * M11, Y.X * M22, ( X.X * M13 ) + Z.X,
			X.Y * M11, Y.Y * M22, ( X.Y * M13 ) + Z.Y,
			X.Z * M11, Y.Z * M22, ( X.Z * M13 ) + Z.Z );

		Inverse = M.Inverse();
		determinant = Math.Abs( M.Determinant );
	}

	public double Eval( Vec3 l )
	{
		var original = ( Inverse * l ).Normalized();
		var stretched = M * original;
		var length = stretched.Length;

		var jacobian = determinant / ( length * length * length );
		var d = Math.Max( 0.0, original.Z ) / Math.PI;

		return Magnitude * d / jacobian;
	}

	public Vec3 Sample( double u1, double u2 )
	{
		var theta = Math.Acos( Math.Sqrt( u1 ) );
		var phi = 2.0 * Math.PI * u2;

		return ( M * new Vec3( Math.Sin( theta ) * Math.Cos( phi ), Math.Sin( theta ) * Math.Sin( phi ), Math.Cos( theta ) ) ).Normalized();
	}
}

//GGX with height-correlated Smith masking, cosine included, Fresnel left at one
internal static class Ggx
{
	public static double Eval( Vec3 v, Vec3 l, double alpha, out double pdf )
	{
		if ( v.Z <= 0.0 )
		{
			pdf = 0.0;
			return 0.0;
		}

		var lambdaV = Lambda( alpha, v.Z );
		var g2 = l.Z <= 0.0 ? 0.0 : 1.0 / ( 1.0 + lambdaV + Lambda( alpha, l.Z ) );

		var h = ( v + l ).Normalized();
		var slopeX = h.X / h.Z;
		var slopeY = h.Y / h.Z;

		var d = 1.0 / ( 1.0 + ( ( ( slopeX * slopeX ) + ( slopeY * slopeY ) ) / alpha / alpha ) );
		d = d * d / ( Math.PI * alpha * alpha * h.Z * h.Z * h.Z * h.Z );

		pdf = Math.Abs( d * h.Z / 4.0 / Vec3.Dot( v, h ) );
		return d * g2 / 4.0 / v.Z;
	}

	public static Vec3 Sample( Vec3 v, double alpha, double u1, double u2 )
	{
		var phi = 2.0 * Math.PI * u1;
		var r = alpha * Math.Sqrt( u2 / ( 1.0 - u2 ) );
		var n = new Vec3( r * Math.Cos( phi ), r * Math.Sin( phi ), 1.0 ).Normalized();

		return -v + ( n * ( 2.0 * Vec3.Dot( n, v ) ) );
	}

	private static double Lambda( double alpha, double cosTheta )
	{
		if ( cosTheta >= 1.0 ) return 0.0;

		var a = 1.0 / alpha / Math.Tan( Math.Acos( cosTheta ) );
		return 0.5 * ( -1.0 + Math.Sqrt( 1.0 + ( 1.0 / a / a ) ) );
	}
}
