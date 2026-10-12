using System;
using System.Threading;
using System.Threading.Tasks;
using Vec3 = System.Numerics.Vector3;
using Vec4 = System.Numerics.Vector4;

namespace Rinten.Bake;

//Light probe lattice storing L1 spherical harmonics per colour, for moving things and unwrapped ones
internal sealed class BakeVolume
{
	public int CountX, CountY, CountZ;
	public Vec3 Origin;
	public float Step;

	/// <summary>A colour channel each: four halves a point - constant, then the lean along x, y, z.</summary>
	public Half[] Red, Green, Blue;


	/// <summary>Four halves a point: how much sky is seen, then its lean along x, y, z - see SkyLit.</summary>
	public Half[] Sky;

	/// <summary>Each cell's corners' clear reach into it, a nibble an axis and corner - see BakedVolumeSpot.</summary>
	public byte[] Walls;

	//Most points along one side.
	private const int MostAlong = 128;

	//The layers lie side by side across one picture, which cannot be wider than this.
	private const int MostAcross = 8192;

	//Most points in all - past this the step grows.
	private const int MostPoints = 1 << 19;

	/// <summary>Lays the lattice over the still world a step apart, or further where that is too many points.</summary>
	public void Lay( BBox bounds, float step )
	{
		step = MathF.Max( step, 0.05f );

		var mins = new Vec3( bounds.Mins.x, bounds.Mins.y, bounds.Mins.z ) - new Vec3( step * 0.5f );
		var size = new Vec3( bounds.Size.x, bounds.Size.y, bounds.Size.z ) + new Vec3( step );

		while ( true )
		{
			CountX = Math.Max( (int)MathF.Ceiling( size.X / step ) + 1, 2 );
			CountY = Math.Max( (int)MathF.Ceiling( size.Y / step ) + 1, 2 );
			CountZ = Math.Max( (int)MathF.Ceiling( size.Z / step ) + 1, 2 );

			var fits = CountX <= MostAlong && CountY <= MostAlong && CountZ <= MostAlong
				&& CountX * CountY <= MostAcross
				&& (long)CountX * CountY * CountZ <= MostPoints;

			if ( fits ) break;

			step *= 1.15f;
		}

		Step = step;

		//Centred on the world, so the slack is shared between both ends.
		var span = new Vec3( CountX - 1, CountY - 1, CountZ - 1 ) * step;
		Origin = mins + ( ( size - span ) * 0.5f );
	}

	/// <summary>Traces every point. Any thread, and all of them.</summary>
	public void Trace( BakeScene scene, int samples, BakeProgress progress, CancellationToken cancel )
	{
		var count = CountX * CountY * CountZ;

		var constant = new Vec3[count];
		var leanR = new Vec3[count];
		var leanG = new Vec3[count];
		var leanB = new Vec3[count];
		var valid = new bool[count];
		var sky = new Vec4[count];

		progress.Begin( "Tracing the volume", count );

		//The sphere's chance per steradian of a direction, where glows get rays of their own - see Arriving
		var glowing = scene.Emitters.Length > 0 ? BakeMath.InvPi * 0.25f : 0.0f;

		Parallel.For( 0, count, new ParallelOptions { CancellationToken = cancel }, index =>
		{
			var x = index % CountX;
			var y = ( index / CountX ) % CountY;
			var z = index / ( CountX * CountY );

			var at = Origin + ( new Vec3( x, y, z ) * Step );
			var random = new BakeRandom( ( (ulong)index * 0xD1B54A32D192ED03ul ) + 17, (ulong)index + 1 );

			var sum = Vec3.Zero;
			Vec3 r = Vec3.Zero, g = Vec3.Zero, b = Vec3.Zero;
			var inside = 0;
			var open = Vec4.Zero;

			//Every direction once, stratified, so sky on one side and a wall on the other are both seen
			var across = Math.Max( (int)MathF.Sqrt( samples ), 2 );

			for ( var s = 0; s < across * across; ++s )
			{
				var u = ( ( s % across ) + random.Float() ) / across;
				var v = ( ( s / across ) + random.Float() ) / across;

				var direction = BakeMath.AnyDirection( u, v );
				var arriving = scene.Arriving( at, direction, glowing, ref random, out var behind, out var escaped );

				if ( behind ) inside++;
				if ( escaped ) open += new Vec4( direction, 1.0f );

				//And a glowing triangle straight from the point, as radiance over the sphere's chance of its direction
				if ( glowing > 0.0f )
				{
					var glow = scene.TowardsGlow( at, Vec3.Zero, Vec3.Zero, false, ref random, out var aim ) * glowing;

					sum += glow;
					r += aim * glow.X;
					g += aim * glow.Y;
					b += aim * glow.Z;
				}

				sum += arriving;
				r += direction * arriving.X;
				g += direction * arriving.Y;
				b += direction * arriving.Z;
			}

			var taken = across * across;

			//What a surface facing n receives, over pi: the mean of what arrives, plus twice the mean of what
			//arrives times its lean onto n - the first two bands, convolved with the cosine, in the fewest words.
			var mean = sum / taken;
			var towardsR = r * ( 2.0f / taken );
			var towardsG = g * ( 2.0f / taken );
			var towardsB = b * ( 2.0f / taken );

			//No lamp's light straight to the point: the frame draws that, shadowed, on what reads the volume
			constant[index] = mean;
			leanR[index] = towardsR;
			leanG[index] = towardsG;
			leanB[index] = towardsB;

			//A point inside a wall saw its inside and would darken everything near it: it takes its neighbours'
			valid[index] = inside < taken * 0.25f;

			sky[index] = SkyOf( open, taken, scene.SkyStrength );

			progress.Step();
		} );

		Fill( constant, leanR, leanG, leanB, sky, valid );
		Encode( constant, leanR, leanG, leanB, sky );
	}

	//Escaping rays' direction sum and count into the sky's visibility as its first two bands, as the light's are
	private static Vec4 SkyOf( Vec4 open, int taken, float strength )
		=> new Vec4( open.X, open.Y, open.Z, 0.0f ) * ( 2.0f / taken * strength ) + new Vec4( 0.0f, 0.0f, 0.0f, open.W / taken * strength );

	/// <summary>Traces every point on the GPU (BakeGpu); false means the CPU. Baked lamps added on the CPU.</summary>
	public bool TraceOnCard( BakeScene scene, int samples, BakeProgress progress, CancellationToken cancel )
	{
		var count = CountX * CountY * CountZ;
		var across = Math.Max( (int)MathF.Sqrt( samples ), 2 );

		var points = new Vec3[count];

		for ( var index = 0; index < count; ++index )
		{
			var x = index % CountX;
			var y = ( index / CountX ) % CountY;
			var z = index / ( CountX * CountY );

			points[index] = Origin + ( new Vec3( x, y, z ) * Step );
		}

		if ( !BakeGpu.TraceProbes( scene, points, across, progress, cancel, out var sums ) ) return false;

		var constant = new Vec3[count];
		var leanR = new Vec3[count];
		var leanG = new Vec3[count];
		var leanB = new Vec3[count];
		var valid = new bool[count];
		var sky = new Vec4[count];

		var taken = across * across;

		progress.Begin( "Reading the volume back", count );

		Parallel.For( 0, count, new ParallelOptions { CancellationToken = cancel }, index =>
		{
			var o = index * BakeGpu.ProbeFloats;

			var mean = new Vec3( sums[o], sums[o + 1], sums[o + 2] ) / taken;
			var towardsR = new Vec3( sums[o + 4], sums[o + 5], sums[o + 6] ) * ( 2.0f / taken );
			var towardsG = new Vec3( sums[o + 8], sums[o + 9], sums[o + 10] ) * ( 2.0f / taken );
			var towardsB = new Vec3( sums[o + 12], sums[o + 13], sums[o + 14] ) * ( 2.0f / taken );
			var inside = sums[o + 7];

			sky[index] = SkyOf( new Vec4( sums[o + 16], sums[o + 17], sums[o + 18], sums[o + 19] ), taken, scene.SkyStrength );

			constant[index] = mean;
			leanR[index] = towardsR;
			leanG[index] = towardsG;
			leanB[index] = towardsB;
			valid[index] = inside < taken * 0.25f;

			progress.Step();
		} );

		Fill( constant, leanR, leanG, leanB, sky, valid );
		Encode( constant, leanR, leanG, leanB, sky );

		return true;
	}

	/// <summary>Finds how far each point sees into the cells it is a corner of. Any thread.</summary>
	public void FindWalls( BakeScene scene, BakeProgress progress, CancellationToken cancel )
	{
		var count = CountX * CountY * CountZ;

		//Per point, octant and axis: how far its box reaches before a wall, in fifteenths of a step
		var reach = new byte[count * 24];

		progress.Begin( "Finding the volume's walls", count );

		Parallel.For( 0, count, new ParallelOptions { CancellationToken = cancel }, index =>
		{
			var x = index % CountX;
			var y = ( index / CountX ) % CountY;
			var z = index / ( CountX * CountY );

			var at = Origin + ( new Vec3( x, y, z ) * Step );
			var random = new BakeRandom( ( (ulong)index * 0x9E3779B97F4A7C15ul ) + 3, (ulong)index + 7 );

			for ( var octant = 0; octant < 8; ++octant )
			{
				var signs = new Vec3( ( octant & 1 ) != 0 ? -1.0f : 1.0f, ( octant & 2 ) != 0 ? -1.0f : 1.0f, ( octant & 4 ) != 0 ? -1.0f : 1.0f );
				var box = Box( scene, at, signs, ref random );

				reach[( index * 24 ) + ( octant * 3 ) + 0] = Nibble( box.X );
				reach[( index * 24 ) + ( octant * 3 ) + 1] = Nibble( box.Y );
				reach[( index * 24 ) + ( octant * 3 ) + 2] = Nibble( box.Z );
			}

			progress.Step();
		} );

		PackWalls( reach );
	}

	//The box from a point into one octant, in steps, that no wall crosses: a wall hit within a step
	//stops the box along its own normal's axis, so a floor bounds its height and a wall its depth
	private Vec3 Box( BakeScene scene, Vec3 at, Vec3 signs, ref BakeRandom random )
	{
		const int Across = 4;
		var box = Vec3.One;

		//A step's cube corner to corner, the furthest a point of the cell lies
		var furthest = Step * 1.7321f;

		for ( var s = 0; s < Across * Across; ++s )
		{
			//Even over the octant: height uniform, angle uniform around
			var up = ( ( s % Across ) + random.Float() ) / Across;
			var turn = ( ( s / Across ) + random.Float() ) * ( BakeMath.Pi * 0.5f / Across );
			var flat = MathF.Sqrt( MathF.Max( 1.0f - ( up * up ), 0.0f ) );
			var direction = new Vec3( flat * MathF.Cos( turn ), flat * MathF.Sin( turn ), up ) * signs;

			if ( !scene.Bvh.Nearest( at, direction, furthest, out var hit ) ) continue;

			var r = Vec3.Abs( direction ) * ( hit.Distance / Step );
			if ( r.X > 1.0f || r.Y > 1.0f || r.Z > 1.0f ) continue;

			var face = Vec3.Abs( scene.Facing[hit.Triangle] );

			if ( face.X >= face.Y && face.X >= face.Z ) box.X = MathF.Min( box.X, r.X );
			else if ( face.Y >= face.Z ) box.Y = MathF.Min( box.Y, r.Y );
			else box.Z = MathF.Min( box.Z, r.Z );
		}

		return box;
	}

	private static byte Nibble( float reach ) => (byte)Math.Clamp( (int)MathF.Round( reach * 15.0f ), 0, 15 );

	//Into one picture as wide as the volume's, three times as tall: a row of cells an axis, each texel that
	//axis's reach of all eight corners - its channels along y then z, the near x corner low, the far one high
	private void PackWalls( byte[] reach )
	{
		var across = CountX * CountY;
		Walls = new byte[across * CountZ * 3 * 4];

		for ( var z = 0; z < CountZ - 1; ++z )
		for ( var y = 0; y < CountY - 1; ++y )
		for ( var x = 0; x < CountX - 1; ++x )
		{
			for ( var corner = 0; corner < 8; ++corner )
			{
				var cx = corner & 1;
				var cy = ( corner >> 1 ) & 1;
				var cz = ( corner >> 2 ) & 1;

				var point = x + cx + ( CountX * ( y + cy + ( CountY * ( z + cz ) ) ) );

				//The octant from that corner into the cell: away from its own side
				var octant = cx | ( cy << 1 ) | ( cz << 2 );
				var channel = cy | ( cz << 1 );

				for ( var axis = 0; axis < 3; ++axis )
				{
					var value = reach[( point * 24 ) + ( octant * 3 ) + axis];
					var texel = ( ( ( ( axis * CountZ ) + z ) * across ) + ( y * CountX ) + x ) * 4;

					Walls[texel + channel] |= (byte)( cx == 0 ? value : value << 4 );
				}
			}
		}
	}

	//Points that were inside something take the mean of their answered neighbours, a ring at a time.
	private void Fill( Vec3[] constant, Vec3[] leanR, Vec3[] leanG, Vec3[] leanB, Vec4[] sky, bool[] valid )
	{
		for ( var ring = 0; ring < 8; ++ring )
		{
			var grown = (bool[])valid.Clone();
			var any = false;

			for ( var z = 0; z < CountZ; ++z )
			for ( var y = 0; y < CountY; ++y )
			for ( var x = 0; x < CountX; ++x )
			{
				var index = x + ( CountX * ( y + ( CountY * z ) ) );
				if ( valid[index] ) continue;

				var c = Vec3.Zero;
				Vec3 r = Vec3.Zero, g = Vec3.Zero, b = Vec3.Zero;
				var open = Vec4.Zero;
				var n = 0;

				for ( var dz = -1; dz <= 1; ++dz )
				for ( var dy = -1; dy <= 1; ++dy )
				for ( var dx = -1; dx <= 1; ++dx )
				{
					var xx = x + dx;
					var yy = y + dy;
					var zz = z + dz;

					if ( xx < 0 || yy < 0 || zz < 0 || xx >= CountX || yy >= CountY || zz >= CountZ ) continue;

					var near = xx + ( CountX * ( yy + ( CountY * zz ) ) );
					if ( !valid[near] ) continue;

					c += constant[near];
					r += leanR[near];
					g += leanG[near];
					b += leanB[near];
					open += sky[near];
					n++;
				}

				if ( n == 0 ) continue;

				constant[index] = c / n;
				leanR[index] = r / n;
				leanG[index] = g / n;
				leanB[index] = b / n;
				sky[index] = open / n;

				grown[index] = true;
				any = true;
			}

			Array.Copy( grown, valid, valid.Length );
			if ( !any ) break;
		}
	}

	//Into three pictures, one a colour, the lattice's layers side by side - see BakedVolume in frame.slang
	private void Encode( Vec3[] constant, Vec3[] leanR, Vec3[] leanG, Vec3[] leanB, Vec4[] sky )
	{
		var count = CountX * CountY * CountZ;

		Red = new Half[count * 4];
		Green = new Half[count * 4];
		Blue = new Half[count * 4];
		Sky = new Half[count * 4];

		for ( var z = 0; z < CountZ; ++z )
		for ( var y = 0; y < CountY; ++y )
		for ( var x = 0; x < CountX; ++x )
		{
			var index = x + ( CountX * ( y + ( CountY * z ) ) );

			//Across is layer then x; down is z.
			var texel = ( ( z * CountX * CountY ) + ( y * CountX ) + x ) * 4;

			Put( Red, texel, constant[index].X, leanR[index] );
			Put( Green, texel, constant[index].Y, leanG[index] );
			Put( Blue, texel, constant[index].Z, leanB[index] );
			Put( Sky, texel, sky[index].W, new Vec3( sky[index].X, sky[index].Y, sky[index].Z ) );
		}
	}

	private static void Put( Half[] into, int at, float constant, Vec3 lean )
	{
		//A lean longer than the constant turns the far side negative; the shader stops that at
		//nought, but a ring is still a ring, so it is kept inside what the constant can carry.
		var length = lean.Length();
		var most = constant * 1.5f;
		if ( length > most && length > 0.0f ) lean *= most / length;

		into[at + 0] = (Half)Math.Min( constant, 60000.0f );
		into[at + 1] = (Half)lean.X;
		into[at + 2] = (Half)lean.Y;
		into[at + 3] = (Half)lean.Z;
	}
}
