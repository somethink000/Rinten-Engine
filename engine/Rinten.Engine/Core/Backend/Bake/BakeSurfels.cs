using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vec3 = System.Numerics.Vector3;

namespace Rinten.Bake;

//Sparse points on the still world, what each sees - the sun by direction, the sky, each other - and what each volume
//point sees of them: the transfer the frame relights the sun's and sky's bounce through, as Enlighten does (CoreLiveBounce)
internal sealed class BakeSurfels
{
	/// <summary>Most links a surfel or a volume point keeps to the surfels it sees.</summary>
	public const int Links = 16;

	/// <summary>Most surfels, so a link fits two bytes.</summary>
	public const int Most = ushort.MaxValue;

	/// <summary>Directions along each side of the octahedron a surfel's sun visibility is kept over, a bit each.</summary>
	public const int SunSide = 16;

	/// <summary>Bytes of sun visibility a surfel.</summary>
	public const int SunBytes = SunSide * SunSide / 8;

	/// <summary>Directions along each octahedron side a surfel's free distances are kept over, a byte each.</summary>
	public const int DepthSide = 8;

	/// <summary>Bytes of free distance a surfel.</summary>
	public const int DepthBytes = DepthSide * DepthSide;

	/// <summary>Metres the furthest free distance byte stands for; nearer is finer, by the square root.</summary>
	public const float DepthFar = 256.0f;

	/// <summary>Floats a surfel: position, normal, albedo, its sky's open way, its seen share times strength.</summary>
	public const int Floats = 13;

	public int Count;

	/// <summary>Floats a surfel each - see Floats.</summary>
	public float[] Data = [];

	/// <summary>SunBytes a surfel: a bit per octahedron direction, set where the sun from there reaches it.</summary>
	public byte[] Sun = [];

	/// <summary>DepthBytes a surfel: how far it sees by octahedron direction, for lamps lit live - see DepthOf.</summary>
	public byte[] Depth = [];

	/// <summary>Links a surfel: the surfels it sees, each with the share of its view it fills.</summary>
	public ushort[] Link = [];
	public Half[] Weight = [];

	/// <summary>The same for each volume point, over its whole sphere.</summary>
	public ushort[] ProbeLink = [];
	public Half[] ProbeWeight = [];

	private Vec3[] position = [], normal = [];
	private float spacing;

	//Surfels by grid cell a spacing wide, for finding the one a ray landed by
	private Dictionary<(int, int, int), List<int>> cells = new();

	/// <summary>Lays surfels over what stops light, one about every spacing square, fewer where that is too many.</summary>
	public void Scatter( BakeScene scene, float wanted )
	{
		var triangles = scene.Corners.Length / 3;
		var area = 0.0f;

		for ( var t = 0; t < triangles; ++t )
			if ( scene.ThroughMode( t ) != 1 ) area += scene.Area( t );

		spacing = MathF.Max( wanted, MathF.Sqrt( area / Most ) * 1.05f );

		var perArea = 1.0f / ( spacing * spacing );
		var random = new BakeRandom( 0x5EEDul, 1 );
		var made = new List<(Vec3 At, Vec3 Face, Vec3 Albedo)>();

		for ( var t = 0; t < triangles && made.Count < Most; ++t )
		{
			if ( scene.ThroughMode( t ) == 1 ) continue;

			//Whole surfels by area, the rest by chance, so small triangles get theirs between them
			var expected = scene.Area( t ) * perArea;
			var count = (int)expected + ( random.Float() < expected - (int)expected ? 1 : 0 );
			var face = scene.Facing[t];

			for ( var i = 0; i < count && made.Count < Most; ++i )
			{
				var r = MathF.Sqrt( random.Float() );
				var turn = random.Float();
				var u = r * ( 1.0f - turn );
				var v = r * turn;

				var at = scene.Interpolate( scene.Positions, t, u, v );
				var hit = new BakeHit { Triangle = t, U = u, V = v, Distance = 1.0f };
				var surface = scene.Surface( hit, at + face, -face );

				made.Add( (at, face, surface.Albedo) );
			}
		}

		Count = made.Count;
		position = new Vec3[Count];
		normal = new Vec3[Count];
		Data = new float[Count * Floats];
		cells = new Dictionary<(int, int, int), List<int>>();

		for ( var s = 0; s < Count; ++s )
		{
			var (at, face, albedo) = made[s];

			position[s] = at;
			normal[s] = face;
			Put( s * Floats, at );
			Put( ( s * Floats ) + 3, face );
			Put( ( s * Floats ) + 6, albedo );

			var key = Cell( at );
			if ( !cells.TryGetValue( key, out var list ) ) cells[key] = list = new List<int>();
			list.Add( s );
		}
	}

	/// <summary>Traces each surfel's sun by direction, its sky and the surfels it sees. Any thread.</summary>
	public void Trace( BakeScene scene, BakeProgress progress, CancellationToken cancel )
	{
		Sun = new byte[Count * SunBytes];
		Depth = new byte[Count * DepthBytes];
		Link = new ushort[Count * Links];
		Weight = new Half[Count * Links];

		progress.Begin( "Tracing the surfels", Count );

		Parallel.For( 0, Count, new ParallelOptions { CancellationToken = cancel }, s =>
		{
			var random = new BakeRandom( ( (ulong)s * 0xA24BAED4963EE407ul ) + 11, (ulong)s + 5 );
			var at = position[s];
			var face = normal[s];

			const int Across = 11;
			Span<int> found = stackalloc int[Across * Across];
			var hits = 0;
			var open = Vec3.Zero;
			var escaped = 0;

			//By the cosine, so each ray is worth the same share of what arrives
			for ( var k = 0; k < Across * Across; ++k )
			{
				var direction = BakeMath.CosineAround( face, ( ( k % Across ) + random.Float() ) / Across, ( ( k / Across ) + random.Float() ) / Across );
				var from = BakeScene.Offset( at, face, direction );

				if ( !scene.Bvh.Nearest( from, direction, 1e6f, out var hit ) )
				{
					open += direction;
					escaped++;
					continue;
				}

				var landed = Landed( scene, from, direction, hit );
				if ( landed >= 0 ) found[hits++] = landed;
			}

			Keep( found[..hits], Across * Across, Link, Weight, s * Links );

			var way = open.LengthSquared() > 1e-8f ? Vec3.Normalize( open ) : face;
			Put( ( s * Floats ) + 9, way );
			Data[( s * Floats ) + 12] = escaped / (float)( Across * Across ) * scene.SkyStrength;

			//The sun from each direction the octahedron holds, where it is over the surfel's plane
			for ( var cell = 0; cell < SunSide * SunSide; ++cell )
			{
				var ox = ( ( ( ( cell % SunSide ) + 0.5f ) / SunSide ) * 2.0f ) - 1.0f;
				var oy = ( ( ( ( cell / SunSide ) + 0.5f ) / SunSide ) * 2.0f ) - 1.0f;
				var direction = BakeMath.FromOctahedron( ox, oy );

				if ( Vec3.Dot( direction, face ) <= 0.0f ) continue;
				if ( scene.Bvh.Blocked( BakeScene.Offset( at, face, direction ), direction, 1e5f ) ) continue;

				Sun[( s * SunBytes ) + ( cell >> 3 )] |= (byte)( 1 << ( cell & 7 ) );
			}

			//How far it sees towards each cell's middle, nothing under its plane
			for ( var cell = 0; cell < DepthBytes; ++cell )
			{
				var ox = ( ( ( ( cell % DepthSide ) + 0.5f ) / DepthSide ) * 2.0f ) - 1.0f;
				var oy = ( ( ( ( cell / DepthSide ) + 0.5f ) / DepthSide ) * 2.0f ) - 1.0f;
				var direction = BakeMath.FromOctahedron( ox, oy );

				if ( Vec3.Dot( direction, face ) <= 0.0f ) continue;

				var from = BakeScene.Offset( at, face, direction );
				var free = scene.Bvh.Nearest( from, direction, DepthFar, out var hit ) ? hit.Distance : DepthFar;

				Depth[( s * DepthBytes ) + cell] = ByteOf( free );
			}

			progress.Step();
		} );
	}

	/// <summary>Finds the surfels each volume point sees, over its whole sphere. Any thread, and all of them.</summary>
	public void Probes( BakeScene scene, BakeVolume volume, BakeProgress progress, CancellationToken cancel )
	{
		var count = volume.CountX * volume.CountY * volume.CountZ;

		ProbeLink = new ushort[count * Links];
		ProbeWeight = new Half[count * Links];

		var valid = new bool[count];

		progress.Begin( "Linking the volume to the surfels", count );

		Parallel.For( 0, count, new ParallelOptions { CancellationToken = cancel }, index =>
		{
			var x = index % volume.CountX;
			var y = ( index / volume.CountX ) % volume.CountY;
			var z = index / ( volume.CountX * volume.CountY );

			var at = volume.Origin + ( new Vec3( x, y, z ) * volume.Step );
			var random = new BakeRandom( ( (ulong)index * 0xC2B2AE3D27D4EB4Ful ) + 23, (ulong)index + 9 );

			const int Across = 8;
			Span<int> found = stackalloc int[Across * Across];
			var hits = 0;
			var inside = 0;

			for ( var k = 0; k < Across * Across; ++k )
			{
				var direction = BakeMath.AnyDirection( ( ( k % Across ) + random.Float() ) / Across, ( ( k / Across ) + random.Float() ) / Across );

				if ( !scene.Bvh.Nearest( at, direction, 1e6f, out var hit ) ) continue;

				if ( Vec3.Dot( direction, scene.Facing[hit.Triangle] ) > 0.0f )
				{
					inside++;
					continue;
				}

				var landed = Landed( scene, at, direction, hit );
				if ( landed >= 0 ) found[hits++] = landed;
			}

			Keep( found[..hits], Across * Across, ProbeLink, ProbeWeight, index * Links );

			//As BakeVolume's: a point that saw mostly insides is in a wall, and takes a neighbour's
			valid[index] = inside < Across * Across / 4;

			progress.Step();
		} );

		FillProbes( volume, valid );
	}

	//The surfel by where a ray landed, facing as the surface it landed on; -1 for the back of something or none near
	private int Landed( BakeScene scene, Vec3 from, Vec3 direction, in BakeHit hit )
	{
		var face = scene.Facing[hit.Triangle];
		if ( Vec3.Dot( direction, face ) > 0.0f ) return -1;

		var point = from + ( direction * hit.Distance );
		var (cx, cy, cz) = Cell( point );

		var best = -1;
		var nearest = 4.0f * spacing * spacing;

		for ( var dz = -1; dz <= 1; ++dz )
		for ( var dy = -1; dy <= 1; ++dy )
		for ( var dx = -1; dx <= 1; ++dx )
		{
			if ( !cells.TryGetValue( (cx + dx, cy + dy, cz + dz), out var list ) ) continue;

			foreach ( var s in list )
			{
				if ( Vec3.Dot( normal[s], face ) < 0.5f ) continue;

				var apart = Vec3.DistanceSquared( position[s], point );
				if ( apart >= nearest ) continue;

				nearest = apart;
				best = s;
			}
		}

		return best;
	}

	//The most seen surfels of a view, each by its share of the rays; the share of those left out goes to the kept ones
	private static void Keep( Span<int> found, int taken, ushort[] links, Half[] weights, int at )
	{
		if ( found.Length == 0 ) return;

		found.Sort();

		Span<(int Surfel, int Rays)> runs = stackalloc (int, int)[found.Length];
		var count = 0;

		for ( var i = 0; i < found.Length; ++i )
		{
			if ( count > 0 && runs[count - 1].Surfel == found[i] ) runs[count - 1].Rays++;
			else runs[count++] = (found[i], 1);
		}

		var kept = runs[..count];
		kept.Sort( static ( a, b ) => b.Rays.CompareTo( a.Rays ) );

		var keep = Math.Min( count, Links );
		var rays = 0;

		for ( var i = 0; i < keep; ++i ) rays += kept[i].Rays;

		var scale = found.Length / (float)rays / taken;

		for ( var i = 0; i < keep; ++i )
		{
			links[at + i] = (ushort)kept[i].Surfel;
			weights[at + i] = (Half)( kept[i].Rays * scale );
		}
	}

	//Points inside a wall take a seeing neighbour's links, a ring at a time
	private void FillProbes( BakeVolume volume, bool[] valid )
	{
		for ( var ring = 0; ring < 8; ++ring )
		{
			var grown = (bool[])valid.Clone();
			var any = false;

			for ( var z = 0; z < volume.CountZ; ++z )
			for ( var y = 0; y < volume.CountY; ++y )
			for ( var x = 0; x < volume.CountX; ++x )
			{
				var index = x + ( volume.CountX * ( y + ( volume.CountY * z ) ) );
				if ( valid[index] ) continue;

				var from = Neighbour( volume, valid, x, y, z );
				if ( from < 0 ) continue;

				Array.Copy( ProbeLink, from * Links, ProbeLink, index * Links, Links );
				Array.Copy( ProbeWeight, from * Links, ProbeWeight, index * Links, Links );

				grown[index] = true;
				any = true;
			}

			Array.Copy( grown, valid, valid.Length );
			if ( !any ) break;
		}
	}

	private static int Neighbour( BakeVolume volume, bool[] valid, int x, int y, int z )
	{
		for ( var dz = -1; dz <= 1; ++dz )
		for ( var dy = -1; dy <= 1; ++dy )
		for ( var dx = -1; dx <= 1; ++dx )
		{
			var xx = x + dx;
			var yy = y + dy;
			var zz = z + dz;

			if ( xx < 0 || yy < 0 || zz < 0 || xx >= volume.CountX || yy >= volume.CountY || zz >= volume.CountZ ) continue;

			var near = xx + ( volume.CountX * ( yy + ( volume.CountY * zz ) ) );
			if ( valid[near] ) return near;
		}

		return -1;
	}

	/// <summary>A free distance in metres as its byte: the square root of its share of DepthFar.</summary>
	public static byte ByteOf( float metres ) => (byte)Math.Clamp( (int)MathF.Ceiling( MathF.Sqrt( Math.Clamp( metres / DepthFar, 0.0f, 1.0f ) ) * 255.0f ), 0, 255 );

	/// <summary>A byte of free distance as metres - ByteOf's inverse.</summary>
	public static float DepthOf( byte value )
	{
		var share = value / 255.0f;
		return share * share * DepthFar;
	}

	private (int, int, int) Cell( Vec3 at )
		=> ((int)MathF.Floor( at.X / spacing ), (int)MathF.Floor( at.Y / spacing ), (int)MathF.Floor( at.Z / spacing ));

	private void Put( int at, Vec3 value )
	{
		Data[at] = value.X;
		Data[at + 1] = value.Y;
		Data[at + 2] = value.Z;
	}
}
