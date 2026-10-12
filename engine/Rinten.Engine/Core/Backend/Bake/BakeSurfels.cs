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
	public const int Links = 64;

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

	/// <summary>Each surfel's cluster: neighbours facing its way with no wall between; their light is the mean.</summary>
	public ushort[] Cluster = [];

	public int Clusters;

	/// <summary>Links a surfel: the clusters it sees, each with the share of its view it fills.</summary>
	public ushort[] Link = [];
	public Half[] Weight = [];

	/// <summary>The clusters each volume point sees, over its whole sphere.</summary>
	public ushort[] ProbeLink = [];
	public Half[] ProbeWeight = [];

	/// <summary>Most links a block of the bounce sheet keeps - see Texels.</summary>
	public const int TexelLinks = Links;

	//Only blocks a surface owns have slots, and a ring round them so a filtered read at an island's edge reads no black
	/// <summary>Each block of the bounce sheet's slot in the links, -1 for none.</summary>
	public int[] TexelSlot = [];

	//The live bounce at the lightmap's own detail, as Enlighten gathers its output pixels (CoreLiveBounce)
	/// <summary>The clusters each slot's block sees over its face, by the cosine.</summary>
	public ushort[] TexelLink = [];
	public Half[] TexelWeight = [];

	/// <summary>Each slot's surface and facing bin, which the bounce is smoothed within.</summary>
	public int[] TexelKey = [];

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

		Gather( scene );
	}

	//Clusters as Enlighten's: a cell three spacings wide by normal axis, split where a wall stands between a surfel
	//and the cluster's first - so a link reaches a patch of wall rather than a point, and none reaches through
	private void Gather( BakeScene scene )
	{
		Cluster = new ushort[Count];

		var seeds = new List<int>();
		var groups = new Dictionary<(int, int, int, int), List<int>>();
		var wide = spacing * 3.0f;

		for ( var s = 0; s < Count; ++s )
		{
			var at = position[s];
			var face = normal[s];
			var axis = Axis( face );
			var key = ((int)MathF.Floor( at.X / wide ), (int)MathF.Floor( at.Y / wide ), (int)MathF.Floor( at.Z / wide ), axis);

			if ( !groups.TryGetValue( key, out var found ) ) groups[key] = found = new List<int>();

			var joined = -1;

			foreach ( var cluster in found )
			{
				var seed = seeds[cluster];
				var from = BakeScene.Offset( position[seed], normal[seed], face );
				var to = BakeScene.Offset( at, face, face );
				var apart = to - from;
				var length = apart.Length();

				if ( length > 1e-4f && scene.Bvh.Blocked( from, apart / length, length ) ) continue;

				joined = cluster;
				break;
			}

			if ( joined < 0 && seeds.Count < Most )
			{
				joined = seeds.Count;
				seeds.Add( s );
				found.Add( joined );
			}

			Cluster[s] = (ushort)Math.Max( joined, 0 );
		}

		Clusters = seeds.Count;
	}

	//Which way a normal mostly points: an axis and its sign, nought to five
	private static int Axis( Vec3 face )
	{
		var a = Vec3.Abs( face );

		if ( a.X >= a.Y && a.X >= a.Z ) return face.X >= 0.0f ? 0 : 1;
		if ( a.Y >= a.Z ) return face.Y >= 0.0f ? 2 : 3;
		return face.Z >= 0.0f ? 4 : 5;
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

			const int Across = 16;
			Span<int> found = stackalloc int[Across * Across];
			var hits = 0;
			var open = Vec3.Zero;
			var escaped = 0;
			var struck = 0;

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

				if ( Vec3.Dot( direction, scene.Facing[hit.Triangle] ) <= 0.0f ) struck++;

				var landed = Landed( scene, from, direction, hit );
				if ( landed >= 0 ) found[hits++] = landed;
			}

			Keep( found[..hits], Across * Across, struck, Link, Weight, s * Links );

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

			const int Across = 32;
			Span<int> found = stackalloc int[Across * Across];
			var hits = 0;
			var inside = 0;
			var missed = 0;

			for ( var k = 0; k < Across * Across; ++k )
			{
				var direction = BakeMath.AnyDirection( ( ( k % Across ) + random.Float() ) / Across, ( ( k / Across ) + random.Float() ) / Across );

				if ( !scene.Bvh.Nearest( at, direction, 1e6f, out var hit ) )
				{
					missed++;
					continue;
				}

				if ( Vec3.Dot( direction, scene.Facing[hit.Triangle] ) > 0.0f )
				{
					inside++;
					continue;
				}

				var landed = Landed( scene, at, direction, hit );
				if ( landed >= 0 ) found[hits++] = landed;
			}

			Keep( found[..hits], Across * Across, Across * Across - inside - missed, ProbeLink, ProbeWeight, index * Links );

			//As BakeVolume's: a point that saw mostly insides is in a wall, and takes a neighbour's
			valid[index] = inside < Across * Across / 4;

			progress.Step();
		} );

		FillProbes( volume, valid );
	}

	/// <summary>Links each owned block to the clusters it sees, and the blocks round them to a neighbour's.</summary>
	public void Texels( BakeScene scene, Vec3[] at, Vec3[] facing, int[] key, int width, int height, BakeProgress progress, CancellationToken cancel )
	{
		var count = width * height;

		TexelSlot = new int[count];
		Array.Fill( TexelSlot, -1 );

		var owned = 0;
		for ( var b = 0; b < count; ++b )
			if ( facing[b] != Vec3.Zero ) TexelSlot[b] = owned++;

		TexelLink = new ushort[owned * TexelLinks];
		TexelWeight = new Half[owned * TexelLinks];
		TexelKey = new int[owned];

		for ( var b = 0; b < count; ++b )
			if ( TexelSlot[b] >= 0 ) TexelKey[TexelSlot[b]] = key[b];

		progress.Begin( "Linking the lightmap to the surfels", count );

		Parallel.For( 0, count, new ParallelOptions { CancellationToken = cancel }, index =>
		{
			var slot = TexelSlot[index];

			if ( slot < 0 )
			{
				progress.Step();
				return;
			}

			var random = new BakeRandom( ( (ulong)index * 0x9E6C63D0676A9A99ul ) + 31, (ulong)index + 5 );

			const int Across = 32;
			var found = new int[Across * Across];
			var hits = 0;
			var struck = 0;

			for ( var k = 0; k < Across * Across; ++k )
			{
				var direction = BakeMath.CosineAround( facing[index], ( ( k % Across ) + random.Float() ) / Across, ( ( k / Across ) + random.Float() ) / Across );

				if ( !scene.Bvh.Nearest( at[index], direction, 1e6f, out var hit ) ) continue;
				if ( Vec3.Dot( direction, scene.Facing[hit.Triangle] ) <= 0.0f ) struck++;

				var landed = Landed( scene, at[index], direction, hit );
				if ( landed >= 0 ) found[hits++] = landed;
			}

			Keep( found.AsSpan( 0, hits ), Across * Across, struck, TexelLink, TexelWeight, slot * TexelLinks, TexelLinks );

			progress.Step();
		} );

		//Two rings round the owned blocks, each taking a neighbour's slot
		for ( var ring = 0; ring < 2; ++ring )
		{
			var grown = (int[])TexelSlot.Clone();

			for ( var y = 0; y < height; ++y )
			{
				for ( var x = 0; x < width; ++x )
				{
					var index = ( y * width ) + x;
					if ( TexelSlot[index] >= 0 ) continue;

					for ( var dy = -1; dy <= 1 && grown[index] < 0; ++dy )
					for ( var dx = -1; dx <= 1 && grown[index] < 0; ++dx )
					{
						var xx = x + dx;
						var yy = y + dy;
						if ( xx < 0 || yy < 0 || xx >= width || yy >= height ) continue;

						grown[index] = TexelSlot[( yy * width ) + xx];
					}
				}
			}

			TexelSlot = grown;
		}
	}

	//The cluster of the surfel by where a ray landed, facing as the surface it landed on; -1 for a back or none near
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

		return best < 0 ? -1 : Cluster[best];
	}

	//The most seen surfels of a view, each by its share of the rays; the share of those left out goes to the kept ones,
	//as does that of rays that struck a front face with no surfel near to read - a small thing's light is its room's
	private static void Keep( Span<int> found, int taken, int struck, ushort[] links, Half[] weights, int at, int most = Links )
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

		var keep = Math.Min( count, most );
		var rays = 0;

		for ( var i = 0; i < keep; ++i ) rays += kept[i].Rays;

		var scale = Math.Max( struck, found.Length ) / (float)rays / taken;

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
