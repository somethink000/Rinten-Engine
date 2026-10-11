using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Rinten.Bake;
using Vec3 = System.Numerics.Vector3;

namespace Rinten;

//The bounce of the sun, sky and every lamp the bake left out, off a bake's surfels, solved again on a worker whenever one
//changes, as Enlighten does: surfels lit directly, each gathering what its links send, then the volume's points the surfels
internal sealed partial class CoreLiveBounce
{
	//Milliseconds between looks at the sun, sky and lamps
	private const long Every = 100;

	//Most lamps lit into the surfels, those reaching the volume first found
	private const int MostLamps = 64;

	//A lamp as the solve reads it, off the main thread - BakeLight's terms
	private readonly record struct Lamp( int Kind, Vec3 Position, Vec3 Forward, Vec3 Colour, float Range, float Falloff, float CosInner, float CosOuter );

	//The sun's direction may turn this far, as a cosine, before the bounce follows
	private const float Turn = 0.99998f;

	private readonly BakedLightingData data;
	private readonly int surfels, points;
	private readonly Vec3[] position, normal, albedo, open;
	private readonly float[] seen;

	//The lights whose bounce the bake holds, and the volume's corners
	private readonly HashSet<Guid> bounced;
	private readonly Vec3 low, high;

	//What each surfel gives off, over pi: the last solve's, and the one being made
	private Vec3[] lit, next;

	//Each surfel's cluster, and each cluster's middle, size and mean of what its surfels give off
	private readonly int[] clusterOf, members;
	private readonly Vec3[] middle, glow;

	//Most rounds of bouncing a solve takes, each one bounce more; it stops sooner once a round changes little
	private const int MostRounds = 12;
	private bool told;

	//Which way the last update went - see Tick
	private bool traced;

	private Task<(Half[] Light, Half[] Lean)> solving;
	private long looked;

	//What the last solve was for, and a reading to fill next that no solve holds
	private BakeSky sky, spare;
	private Vec3 towards, colour;
	private List<Lamp> lamps = new();

	/// <summary>The bounce volume's two pictures - colour, then its lean - nought until the first solve lands.</summary>
	public ulong Light, Lean;

	public CoreLiveBounce( BakedLightingData data )
	{
		this.data = data;

		surfels = data.Surfels.Length / BakeSurfels.Floats;
		points = data.VolumeX * data.VolumeY * data.VolumeZ;

		position = new Vec3[surfels];
		normal = new Vec3[surfels];
		albedo = new Vec3[surfels];
		open = new Vec3[surfels];
		seen = new float[surfels];
		lit = new Vec3[surfels];
		next = new Vec3[surfels];

		for ( var s = 0; s < surfels; ++s )
		{
			var at = s * BakeSurfels.Floats;

			position[s] = Read( at );
			normal[s] = Read( at + 3 );
			albedo[s] = Read( at + 6 );
			open[s] = Read( at + 9 );
			seen[s] = data.Surfels[at + 12];
		}

		clusterOf = new int[surfels];
		var clusters = 0;

		for ( var s = 0; s < surfels; ++s )
		{
			clusterOf[s] = data.SurfelClusters[s];
			clusters = Math.Max( clusters, clusterOf[s] + 1 );
		}

		members = new int[clusters];
		middle = new Vec3[clusters];
		glow = new Vec3[clusters];

		for ( var s = 0; s < surfels; ++s )
		{
			members[clusterOf[s]]++;
			middle[clusterOf[s]] += position[s];
		}

		for ( var c = 0; c < clusters; ++c )
			if ( members[c] > 0 ) middle[c] /= members[c];

		bounced = new HashSet<Guid>( data.Bounced );
		low = new Vec3( data.VolumeOrigin.x, data.VolumeOrigin.y, data.VolumeOrigin.z );
		high = low + ( new Vec3( data.VolumeX - 1, data.VolumeY - 1, data.VolumeZ - 1 ) * data.VolumeStep );
	}

	private Vec3 Read( int at ) => new( data.Surfels[at], data.Surfels[at + 1], data.Surfels[at + 2] );

	/// <summary>Puts a finished solve on the card, and starts another where the sun or sky moved. Main thread.</summary>
	public void Tick( Scene scene )
	{
		//Traced on the card where the player turned rays on - see Trace; only the sun, sky and lamps are read here.
		//Switched either way, the other path starts over rather than keeping what this one left.
		if ( Rays != traced )
		{
			traced = Rays;
			sky = null;
		}

		if ( traced )
		{
			LookForRays( scene );
			return;
		}

		if ( solving is { IsCompleted: true } )
		{
			if ( solving.IsCompletedSuccessfully ) Upload( solving.Result );
			else Log.Warning( $"baked lighting: relighting the bounce failed: {solving.Exception?.InnerException}" );

			solving = null;
		}

		if ( solving is not null ) return;

		var now = System.Environment.TickCount64;
		if ( now - looked < Every ) return;
		looked = now;

		var current = spare ?? new BakeSky();
		current.Gather( scene );

		SunOf( scene, out var way, out var shade );
		var found = LampsOf( scene );

		var same = sky is not null && current.Same( sky ) && Vec3.Dot( way, towards ) >= Turn
			&& Vec3.Distance( shade, colour ) <= 0.01f * MathF.Max( 1.0f, colour.Length() ) && Same( found, lamps );

		if ( same )
		{
			spare = current;
			return;
		}

		spare = sky;
		( sky, towards, colour, lamps ) = ( current, way, shade, found );

		solving = Task.Run( () =>
		{
			var clock = System.Diagnostics.Stopwatch.StartNew();
			var solved = Solve( current, way, shade, found );

			//Once, so the cost is in the log without a line every time the sun moves
			if ( !told )
			{
				told = true;
				Log.Info( $"baked lighting: bounce relit in {clock.Elapsed.TotalMilliseconds:0.0} ms on a worker - {surfels} surfels, {glow.Length} clusters, {found.Count} lamps" );
			}

			return solved;
		} );
	}

	//Every lamp lit now whose bounce the bake does not hold and whose reach meets the volume
	private List<Lamp> LampsOf( Scene scene )
	{
		var found = new List<Lamp>();

		foreach ( var light in scene.GetAllComponents<Light>() )
		{
			if ( found.Count >= MostLamps ) break;
			if ( light is DirectionalLight || !light.IsValid() || !light.Enabled || !light.Active ) continue;
			if ( !light.Contribution.HasFlag( Rinten.Light.LightContribution.Diffuse ) || bounced.Contains( light.Id ) ) continue;

			var emitted = light.EmittedColor;
			if ( emitted.r <= 0.0f && emitted.g <= 0.0f && emitted.b <= 0.0f ) continue;

			var at = light.WorldPosition;
			var forward = light.WorldRotation.Forward.Normal;
			var position = new Vec3( at.x, at.y, at.z );

			Lamp lamp;

			switch ( light )
			{
				case SpotLight spot:
					lamp = new Lamp( 2, position, new Vec3( forward.x, forward.y, forward.z ), new Vec3( emitted.r, emitted.g, emitted.b ),
						CoreSceneRenderer.EffectiveRange( spot.Radius, emitted, spot.Falloff ), spot.Falloff,
						MathF.Cos( spot.ConeInner.DegreeToRadian() ), MathF.Cos( spot.ConeOuter.DegreeToRadian() ) );
					break;

				case PointLight point:
					lamp = new Lamp( 1, position, Vec3.Zero, new Vec3( emitted.r, emitted.g, emitted.b ),
						CoreSceneRenderer.EffectiveRange( point.Radius, emitted, point.Falloff ), point.Falloff, 0.0f, 0.0f );
					break;

				default:
					continue;
			}

			var nearest = Vec3.Clamp( position, low, high );
			if ( Vec3.DistanceSquared( nearest, position ) > lamp.Range * lamp.Range ) continue;

			found.Add( lamp );
		}

		return found;
	}

	//Whether two lamp lists light the same: a centimetre, a hundredth of colour or reach, a fraction of a degree
	private static bool Same( List<Lamp> a, List<Lamp> b )
	{
		if ( a.Count != b.Count ) return false;

		for ( var i = 0; i < a.Count; ++i )
		{
			var (x, y) = (a[i], b[i]);

			if ( x.Kind != y.Kind || Vec3.Distance( x.Position, y.Position ) > 0.01f ) return false;
			if ( Vec3.Distance( x.Colour, y.Colour ) > 0.01f * MathF.Max( 1.0f, y.Colour.Length() ) ) return false;
			if ( MathF.Abs( x.Range - y.Range ) > 0.01f * y.Range || x.Falloff != y.Falloff ) return false;
			if ( x.Kind == 2 && ( Vec3.Dot( x.Forward, y.Forward ) < Turn || x.CosInner != y.CosInner || x.CosOuter != y.CosOuter ) ) return false;
		}

		return true;
	}

	//The baked sun as it is now: towards it, and its colour; none where it is off or gone
	private void SunOf( Scene scene, out Vec3 way, out Vec3 shade )
	{
		way = Vec3.UnitZ;
		shade = Vec3.Zero;

		if ( data.LiveSun == Guid.Empty ) return;
		if ( scene.Directory.FindComponentByGuid( data.LiveSun ) is not DirectionalLight sun ) return;
		if ( !sun.IsValid() || !sun.Enabled || !sun.Active ) return;
		if ( !sun.Contribution.HasFlag( Rinten.Light.LightContribution.Diffuse ) ) return;

		var forward = sun.WorldRotation.Forward.Normal;
		var emitted = sun.EmittedColor;

		way = -new Vec3( forward.x, forward.y, forward.z );
		shade = new Vec3( emitted.r, emitted.g, emitted.b );
	}

	private (Half[] Light, Half[] Lean) Solve( BakeSky with, Vec3 way, Vec3 shade, List<Lamp> shining )
	{
		//Over pi, as BakeScene.Direct: the sun square on, the sky where the surfel sees it, and the lamps
		var direct = new Vec3[surfels];

		Parallel.For( 0, surfels, s =>
		{
			var arriving = Vec3.Zero;
			var facing = Vec3.Dot( normal[s], way );

			if ( facing > 0.0f && shade != Vec3.Zero ) arriving += shade * ( facing * BakeMath.InvPi * SunSeen( s, way ) );
			if ( seen[s] > 0.0f ) arriving += with.Radiance( open[s] ) * seen[s];

			foreach ( var lamp in shining ) arriving += LampAt( s, lamp );

			direct[s] = arriving;
		} );

		//Until a round moves the light by under a hundredth, so what an old light left behind is gone too
		for ( var round = 0; round < MostRounds; ++round )
		{
			Mean();

			Parallel.For( 0, surfels, s =>
			{
				var gathered = direct[s];
				var at = s * BakeSurfels.Links;

				for ( var k = 0; k < BakeSurfels.Links; ++k )
				{
					var weight = (float)data.SurfelWeights[at + k];
					if ( weight <= 0.0f ) break;

					gathered += glow[data.SurfelLinks[at + k]] * weight;
				}

				next[s] = albedo[s] * gathered;
			} );

			( lit, next ) = ( next, lit );

			if ( Settled() ) break;
		}

		Mean();

		var light = new Half[points * 4];
		var lean = new Half[points * 4];
		var origin = new Vec3( data.VolumeOrigin.x, data.VolumeOrigin.y, data.VolumeOrigin.z );

		//Each point's light by the first two bands, as BakeVolume's: the mean, and twice the mean along its way
		Parallel.For( 0, points, p =>
		{
			var x = p % data.VolumeX;
			var y = ( p / data.VolumeX ) % data.VolumeY;
			var z = p / ( data.VolumeX * data.VolumeY );

			var at = origin + ( new Vec3( x, y, z ) * data.VolumeStep );
			var links = p * BakeSurfels.Links;

			var mean = Vec3.Zero;
			var along = Vec3.Zero;

			for ( var k = 0; k < BakeSurfels.Links; ++k )
			{
				var weight = (float)data.ProbeWeights[links + k];
				if ( weight <= 0.0f ) break;

				var c = data.ProbeLinks[links + k];
				var given = glow[c] * weight;
				var apart = middle[c] - at;
				var length = apart.Length();

				mean += given;
				if ( length > 1e-5f ) along += apart * ( BakeMath.Luma( given ) * 2.0f / length );
			}

			//One lean for all three colours, the brightness's, over the constant - see BakedBounce
			var bright = BakeMath.Luma( mean );
			var tilt = bright > 1e-6f ? along / bright : Vec3.Zero;
			var tilted = tilt.Length();
			if ( tilted > 1.0f ) tilt /= tilted;

			light[( p * 4 ) + 0] = (Half)MathF.Min( mean.X, 60000.0f );
			light[( p * 4 ) + 1] = (Half)MathF.Min( mean.Y, 60000.0f );
			light[( p * 4 ) + 2] = (Half)MathF.Min( mean.Z, 60000.0f );
			light[( p * 4 ) + 3] = (Half)1.0f;

			lean[( p * 4 ) + 0] = (Half)tilt.X;
			lean[( p * 4 ) + 1] = (Half)tilt.Y;
			lean[( p * 4 ) + 2] = (Half)tilt.Z;
		} );

		return (light, lean);
	}

	//One lamp's light on a surfel over pi, as BakeScene.DirectOne without its shape, shadowed by the free distances
	private Vec3 LampAt( int s, in Lamp lamp )
	{
		var toLamp = lamp.Position - position[s];
		var distance = toLamp.Length();
		if ( distance > lamp.Range || distance < 1e-4f ) return Vec3.Zero;

		var l = toLamp / distance;
		var facing = Vec3.Dot( normal[s], l );
		if ( facing <= 0.0f ) return Vec3.Zero;

		var reach = distance / MathF.Max( lamp.Range, 1e-4f );
		var window = BakeMath.Saturate( 1.0f - ( reach * reach * reach * reach ) );
		var square = MathF.Max( distance * distance, 1e-4f );
		var spread = lamp.Falloff == 1.0f ? square : MathF.Pow( square, lamp.Falloff );
		var attenuation = window * window / MathF.Max( spread, 1e-4f );

		if ( lamp.Kind == 2 )
			attenuation *= BakeMath.Saturate( ( Vec3.Dot( -l, lamp.Forward ) - lamp.CosOuter ) / MathF.Max( lamp.CosInner - lamp.CosOuter, 1e-4f ) );

		if ( attenuation <= 0.0f ) return Vec3.Zero;

		return lamp.Colour * ( facing * attenuation * BakeMath.InvPi * Reaches( s, l, distance ) );
	}

	//How much of a point a direction and distance away a surfel sees: each near cell's free distance against it,
	//a tenth of the way past counted half, bilinear across the octahedron as SunSeen
	private float Reaches( int s, Vec3 l, float distance )
	{
		var (ox, oy) = BakeMath.Octahedron( l );

		var fx = Math.Clamp( ( ( ox + 1.0f ) * 0.5f * BakeSurfels.DepthSide ) - 0.5f, 0.0f, BakeSurfels.DepthSide - 1.0f );
		var fy = Math.Clamp( ( ( oy + 1.0f ) * 0.5f * BakeSurfels.DepthSide ) - 0.5f, 0.0f, BakeSurfels.DepthSide - 1.0f );

		var x0 = (int)fx;
		var y0 = (int)fy;
		var x1 = Math.Min( x0 + 1, BakeSurfels.DepthSide - 1 );
		var y1 = Math.Min( y0 + 1, BakeSurfels.DepthSide - 1 );
		var tx = fx - x0;
		var ty = fy - y0;

		var first = s * BakeSurfels.DepthBytes;
		var band = MathF.Max( distance * 0.2f, 0.1f );

		float Clear( int x, int y ) => BakeMath.Saturate( ( ( BakeSurfels.DepthOf( data.SurfelDepth[first + ( y * BakeSurfels.DepthSide ) + x] ) - distance ) / band ) + 0.5f );

		var near = ( Clear( x0, y0 ) * ( 1.0f - tx ) ) + ( Clear( x1, y0 ) * tx );
		var far = ( Clear( x0, y1 ) * ( 1.0f - tx ) ) + ( Clear( x1, y1 ) * tx );

		return ( near * ( 1.0f - ty ) ) + ( far * ty );
	}

	//Whether the last round changed the light by under a hundredth of the brightest, lit against next
	private bool Settled()
	{
		var most = 0.0f;
		var moved = 0.0f;

		for ( var s = 0; s < surfels; ++s )
		{
			most = MathF.Max( most, BakeMath.Luma( lit[s] ) );
			moved = MathF.Max( moved, MathF.Abs( BakeMath.Luma( lit[s] ) - BakeMath.Luma( next[s] ) ) );
		}

		return moved <= most * 0.01f;
	}

	//Each cluster's light as the mean of its surfels'
	private void Mean()
	{
		Array.Clear( glow );

		for ( var s = 0; s < surfels; ++s ) glow[clusterOf[s]] += lit[s];

		for ( var c = 0; c < glow.Length; ++c )
			if ( members[c] > 0 ) glow[c] /= members[c];
	}

	//How much of the sun from a direction reaches a surfel: its bits, bilinear across the octahedron's cells
	private float SunSeen( int s, Vec3 way )
	{
		var (ox, oy) = BakeMath.Octahedron( way );

		var fx = Math.Clamp( ( ( ox + 1.0f ) * 0.5f * BakeSurfels.SunSide ) - 0.5f, 0.0f, BakeSurfels.SunSide - 1.0f );
		var fy = Math.Clamp( ( ( oy + 1.0f ) * 0.5f * BakeSurfels.SunSide ) - 0.5f, 0.0f, BakeSurfels.SunSide - 1.0f );

		var x0 = (int)fx;
		var y0 = (int)fy;
		var x1 = Math.Min( x0 + 1, BakeSurfels.SunSide - 1 );
		var y1 = Math.Min( y0 + 1, BakeSurfels.SunSide - 1 );
		var tx = fx - x0;
		var ty = fy - y0;

		var first = s * BakeSurfels.SunBytes;

		var low = ( Bit( first, x0, y0 ) * ( 1.0f - tx ) ) + ( Bit( first, x1, y0 ) * tx );
		var high = ( Bit( first, x0, y1 ) * ( 1.0f - tx ) ) + ( Bit( first, x1, y1 ) * tx );

		return ( low * ( 1.0f - ty ) ) + ( high * ty );
	}

	private float Bit( int first, int x, int y )
	{
		var cell = ( y * BakeSurfels.SunSide ) + x;
		return ( data.SurfelSun[first + ( cell >> 3 )] >> ( cell & 7 ) ) & 1;
	}

	/// <summary>The last solve's colour a point, for the portable frame, which puts it in its own picture.</summary>
	public Half[] Solved { get; private set; }

	/// <summary>Counts solves landed, so the portable frame knows to write it again.</summary>
	public int Landed { get; private set; }

	//Into the two pictures in place, made the first time
	private void Upload( (Half[] Light, Half[] Lean) solved )
	{
		Solved = solved.Light;
		Landed++;

		if ( CoreRenderDevice.PortableFrame ) return;

		var across = data.VolumeX * data.VolumeY;
		var down = data.VolumeZ;

		if ( Light == 0 || Lean == 0 )
		{
			Release();
			Light = CoreRenderDevice.CreateTextureHalf( solved.Light, across, down );
			Lean = CoreRenderDevice.CreateTextureHalf( solved.Lean, across, down );
			return;
		}

		var stride = across * 8;

		if ( !CoreRenderDevice.WriteTextureLevel( Light, 0, 0, 0, across, down, MemoryMarshal.AsBytes( solved.Light.AsSpan() ), stride )
			|| !CoreRenderDevice.WriteTextureLevel( Lean, 0, 0, 0, across, down, MemoryMarshal.AsBytes( solved.Lean.AsSpan() ), stride ) )
		{
			//Made again by the next solve, which forgetting this one's light starts
			Release();
			sky = null;
		}
	}

	/// <summary>Takes the pictures off the card; a solve still running lands nowhere.</summary>
	public void Release()
	{
		ReleaseRays();

		if ( Light != 0 ) CoreRenderDevice.DestroyTexture( Light );
		if ( Lean != 0 ) CoreRenderDevice.DestroyTexture( Lean );

		Light = Lean = 0;
	}
}
