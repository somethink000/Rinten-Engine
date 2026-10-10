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

	/// <summary>Four bytes a point, null where nothing is masked: each masked light's share, for movers.</summary>
	public byte[] Mask;

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
		var mask = scene.AnyMasked() ? new Vec4[count] : null;

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

			//Every direction once, stratified, so sky on one side and a wall on the other are both seen
			var across = Math.Max( (int)MathF.Sqrt( samples ), 2 );

			for ( var s = 0; s < across * across; ++s )
			{
				var u = ( ( s % across ) + random.Float() ) / across;
				var v = ( ( s / across ) + random.Float() ) / across;

				var direction = BakeMath.AnyDirection( u, v );
				var arriving = scene.Arriving( at, direction, glowing, ref random, out var behind );

				if ( behind ) inside++;

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

			//And the lamps whose light is baked, straight to the point. One of those is a direction, not a spread,
			//and projects onto the bands as a quarter of its light in the constant and half of it in the lean.
			foreach ( var light in scene.Lights )
			{
				if ( !light.Whole ) continue;

				var direct = LampAt( scene, light, at, ref random, out var towards );

				if ( direct == Vec3.Zero ) continue;

				mean += direct * 0.25f;
				towardsR += towards * ( direct.X * 0.5f );
				towardsG += towards * ( direct.Y * 0.5f );
				towardsB += towards * ( direct.Z * 0.5f );
			}

			constant[index] = mean;
			leanR[index] = towardsR;
			leanG[index] = towardsG;
			leanB[index] = towardsB;

			//A point inside a wall saw its inside and would darken everything near it: it takes its neighbours'
			valid[index] = inside < taken * 0.25f;

			if ( mask is not null ) mask[index] = MaskAt( scene, at, ref random );

			progress.Step();
		} );

		Fill( constant, leanR, leanG, leanB, mask, valid );
		Encode( constant, leanR, leanG, leanB, mask );
	}

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
		var mask = scene.AnyMasked() ? new Vec4[count] : null;

		var taken = across * across;

		progress.Begin( "Lighting the volume from its lamps", count );

		Parallel.For( 0, count, new ParallelOptions { CancellationToken = cancel }, index =>
		{
			var o = index * 16;
			var random = new BakeRandom( ( (ulong)index * 0xD1B54A32D192ED03ul ) + 17, (ulong)index + 1 );

			var mean = new Vec3( sums[o], sums[o + 1], sums[o + 2] ) / taken;
			var towardsR = new Vec3( sums[o + 4], sums[o + 5], sums[o + 6] ) * ( 2.0f / taken );
			var towardsG = new Vec3( sums[o + 8], sums[o + 9], sums[o + 10] ) * ( 2.0f / taken );
			var towardsB = new Vec3( sums[o + 12], sums[o + 13], sums[o + 14] ) * ( 2.0f / taken );
			var inside = sums[o + 7];

			foreach ( var light in scene.Lights )
			{
				if ( !light.Whole ) continue;

				var direct = LampAt( scene, light, points[index], ref random, out var towards );

				if ( direct == Vec3.Zero ) continue;

				mean += direct * 0.25f;
				towardsR += towards * ( direct.X * 0.5f );
				towardsG += towards * ( direct.Y * 0.5f );
				towardsB += towards * ( direct.Z * 0.5f );
			}

			constant[index] = mean;
			leanR[index] = towardsR;
			leanG[index] = towardsG;
			leanB[index] = towardsB;
			valid[index] = inside < taken * 0.25f;

			if ( mask is not null ) mask[index] = MaskAt( scene, points[index], ref random );

			progress.Step();
		} );

		Fill( constant, leanR, leanG, leanB, mask, valid );
		Encode( constant, leanR, leanG, leanB, mask );

		return true;
	}

	//One baked lamp's light at a point over pi, as a surface facing it has it, shadowed across the lamp's width
	private static Vec3 LampAt( BakeScene scene, BakeLight light, Vec3 at, ref BakeRandom random, out Vec3 towards )
	{
		//Asked with the normal at the lamp: its light square on, with a texel's falloff, cone and shadow
		towards = light.Kind == 0 ? -light.Forward : Vec3.Normalize( light.Position - at );

		var sum = Vec3.Zero;
		const int Tries = 16;

		for ( var i = 0; i < Tries; ++i )
			sum += scene.DirectOne( light, at, towards, towards, ref random, out _ );

		return sum / Tries;
	}

	//Points that were inside something take the mean of their answered neighbours, a ring at a time.
	private void Fill( Vec3[] constant, Vec3[] leanR, Vec3[] leanG, Vec3[] leanB, Vec4[] mask, bool[] valid )
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
				var m = Vec4.Zero;
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
					if ( mask is not null ) m += mask[near];
					n++;
				}

				if ( n == 0 ) continue;

				constant[index] = c / n;
				leanR[index] = r / n;
				leanG[index] = g / n;
				leanB[index] = b / n;
				if ( mask is not null ) mask[index] = m / n;

				grown[index] = true;
				any = true;
			}

			Array.Copy( grown, valid, valid.Length );
			if ( !any ) break;
		}
	}

	//Into three pictures, one a colour, the lattice's layers side by side - see BakedVolume in frame.slang
	private void Encode( Vec3[] constant, Vec3[] leanR, Vec3[] leanG, Vec3[] leanB, Vec4[] mask )
	{
		var count = CountX * CountY * CountZ;

		Red = new Half[count * 4];
		Green = new Half[count * 4];
		Blue = new Half[count * 4];
		Mask = mask is null ? null : new byte[count * 4];

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

			if ( Mask is null ) continue;

			var through = Vec4.Clamp( mask[index], Vec4.Zero, Vec4.One );

			Mask[texel + 0] = (byte)MathF.Round( through.X * 255.0f );
			Mask[texel + 1] = (byte)MathF.Round( through.Y * 255.0f );
			Mask[texel + 2] = (byte)MathF.Round( through.Z * 255.0f );
			Mask[texel + 3] = (byte)MathF.Round( through.W * 255.0f );
		}
	}

	//How much of each masked light reaches a point in the air through the still world: a handful of shadow
	//rays at each, across the light's width. A channel no light reaches here lets everything through.
	private static Vec4 MaskAt( BakeScene scene, Vec3 at, ref BakeRandom random )
	{
		const int Tries = 16;
		Span<float> through = stackalloc float[BakeScene.MaskChannels];

		for ( var c = 0; c < BakeScene.MaskChannels; ++c )
		{
			if ( scene.MaskedAt( c, at ) is not { } light )
			{
				through[c] = 1.0f;
				continue;
			}

			//Facing the light, so the point does not shadow itself: it is in the air, and every side of it is open.
			var towards = light.Kind == 0 ? -light.Forward : Vec3.Normalize( light.Position - at );
			var sum = 0.0f;

			for ( var i = 0; i < Tries; ++i ) sum += scene.Visible( light, at, towards, ref random );

			through[c] = sum / Tries;
		}

		return new Vec4( through[0], through[1], through[2], through[3] );
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
