using System;
using Vec3 = System.Numerics.Vector3;
using Vec4 = System.Numerics.Vector4;

namespace Rinten.Bake;

//The sky as the bake and the live bounce read it on the CPU: the built-in sky or a panorama, under flat ambient
internal sealed class BakeSky
{
	//The sky, out of the same block the frame is written into.
	private readonly float[] block = new float[CoreSceneRenderer.FrameFloats];
	private bool hasSky;
	private bool hasSun;
	private Vec3 zenith, horizon, ground, sunDirection, sunLight;
	private float night;

	//A panorama sky's picture, light and turn, where the sky is one - see CorePanoramaSky
	private CorePanorama panorama;
	private Vec3 panoramaLight;
	private float panoramaTurn;

	private Vec4 ambient;

	/// <summary>Reads the scene's sky as it is now. Main thread.</summary>
	public void Gather( Scene scene )
	{
		CoreSceneRenderer.SetSky( block, scene );
		CoreSceneRenderer.SetAmbient( block, scene );

		hasSky = block[CoreSceneRenderer.SkyAt + 3] > 0.5f;
		hasSun = block[CoreSceneRenderer.SunAt + 3] > 0.0f;

		zenith = Read3( CoreSceneRenderer.SkyAt + 0 );
		horizon = Read3( CoreSceneRenderer.SkyAt + 4 );
		ground = Read3( CoreSceneRenderer.SkyAt + 8 );
		sunDirection = Read3( CoreSceneRenderer.SunAt );
		sunLight = Read3( CoreSceneRenderer.SunLightAt );
		night = block[CoreSceneRenderer.SunLightAt + 3];

		var drawn = CoreSceneRenderer.HasPanorama( block ) ? CorePanoramaSky.Find( CoreSceneRenderer.SkyMaterialOf( scene ) ) : null;

		panorama = CorePanoramaSky.PanoramaOf( drawn );
		panoramaLight = drawn?.Light ?? Vec3.Zero;
		panoramaTurn = drawn?.Turn ?? 0.0f;

		var at = CoreSceneRenderer.AmbientAt;
		ambient = new Vec4( block[at], block[at + 1], block[at + 2], block[at + 3] );
	}

	private Vec3 Read3( int at ) => new( block[at], block[at + 1], block[at + 2] );

	/// <summary>Sky plus flat ambient from one direction, scaled as frame.slang's SkyAmbientScale. Any thread.</summary>
	public Vec3 Radiance( Vec3 direction )
	{
		var around = !hasSky ? Vec3.Zero
			: panorama is not null ? panorama.Radiance( direction, panoramaTurn ) * panoramaLight
			: CoreSkyTable.ColourOf( new CoreSkyTable.Air( zenith, horizon, ground, sunDirection, sunLight, night, hasSun ),
				direction.Y, BakeMath.Saturate( Vec3.Dot( direction, sunDirection ) ) ) * 0.25f;

		return BakeMath.Lerp( around, new Vec3( ambient.X, ambient.Y, ambient.Z ), ambient.W );
	}

	/// <summary>Whether two readings light the same, near enough that the bounce off them would not change.</summary>
	public bool Same( BakeSky other )
	{
		if ( other is null || hasSky != other.hasSky || hasSun != other.hasSun || panorama != other.panorama ) return false;

		return Near( zenith, other.zenith ) && Near( horizon, other.horizon ) && Near( ground, other.ground )
			&& Near( sunDirection, other.sunDirection ) && Near( sunLight, other.sunLight )
			&& Near( new Vec3( night, panoramaTurn, 0.0f ), new Vec3( other.night, other.panoramaTurn, 0.0f ) )
			&& Near( panoramaLight, other.panoramaLight ) && Vec4.Distance( ambient, other.ambient ) <= 0.01f;
	}

	//A hundredth apart, or a hundredth of the larger
	private static bool Near( Vec3 a, Vec3 b ) => Vec3.Distance( a, b ) <= 0.01f * MathF.Max( 1.0f, MathF.Max( a.Length(), b.Length() ) );
}
