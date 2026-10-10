using System.Runtime.CompilerServices;
using Vec3 = System.Numerics.Vector3;

namespace Rinten;

//A panorama sky (ENGINE_SKY_PANORAMA): its light and turn for the frame, and its sky table and reflection levels,
//made from the picture rather than drawn so every device and the bake agree
internal static class CorePanoramaSky
{
	//What a panorama shader names its picture - see sky_hdri.shader
	internal const string PictureName = "g_tSkyTexture";

	//What it names the multiplier on its light
	internal const string BrightnessName = "g_flBrightness";

	//What it names its turn about up, in degrees
	internal const string RotationName = "g_flRotation";

	internal sealed class Sky
	{
		/// <summary>The picture on the CPU; null where its compiled file could not be read.</summary>
		public CorePanorama Panorama;

		/// <summary>The material's picture on the device, by which a recompile is seen.</summary>
		public ulong Picture;

		/// <summary>Brightness by the skybox's tint.</summary>
		public Vec3 Light;

		/// <summary>Radians about up.</summary>
		public float Turn;

		/// <summary>The frame's sky table, in the world's turn and at Light; null without a Panorama.</summary>
		public Half[] Table;

		//What the device table was made from, so it is made again only when the table is
		internal Half[] TableMade;
		internal ulong TableTexture;

		//And the reflection levels, made again only for another picture
		internal CorePanorama ReflectedFrom;
		internal ulong ReflectionTexture;

		//The material picture the Panorama was read for
		internal string ReadFrom;
		internal ulong ReadPicture;
	}

	private static readonly ConditionalWeakTable<Material, Sky> skies = new();

	//Every sky made, so a table on the device goes when its material does
	private static readonly List<(WeakReference<Material> Material, Sky Sky)> made = new();

	//The sky a panorama material draws, brought up to date; null for any other material
	internal static Sky Of( Material material, Color tint )
	{
		var core = material?.core;
		if ( core is null || !CoreResources.IsPanoramaShader( core.Shader ) ) return null;

		Sky sky;

		lock ( made )
		{
			if ( !skies.TryGetValue( material, out sky ) )
			{
				sky = new Sky();
				skies.Add( material, sky );
				made.Add( (new WeakReference<Material>( material ), sky) );
			}
		}

		lock ( sky )
		{
			var picture = core.Texture( PictureName );
			var path = picture?.core?.Name;
			sky.Picture = picture?.core?.Device() ?? 0;

			//A recompiled picture comes back as another handle on the device
			if ( path != sky.ReadFrom || sky.Picture != sky.ReadPicture )
			{
				sky.ReadFrom = path;
				sky.ReadPicture = sky.Picture;
				sky.Panorama = Read( path );
				sky.Table = null;
			}

			var light = new Vec3( tint.r, tint.g, tint.b ) * MathF.Max( CoreResources.Number( core, BrightnessName, 1.0f ), 0.0f );
			var turn = CoreResources.Number( core, RotationName, 0.0f ).DegreeToRadian();

			if ( sky.Table is null || light != sky.Light || turn != sky.Turn )
			{
				sky.Light = light;
				sky.Turn = turn;
				sky.Table = sky.Panorama?.Table( light, turn );
			}

			return sky;
		}
	}

	//The sky a material was last brought up to date as by Of, or null
	internal static Sky Find( Material material )
	{
		if ( material is null ) return null;

		lock ( made ) return skies.TryGetValue( material, out var sky ) ? sky : null;
	}

	//The compiled picture a material reads by this path, on the CPU
	private static CorePanorama Read( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return null;

		var blocks = CoreSceneRenderer.MapBlocks( path, srgb: false );
		var panorama = blocks is null ? null : CorePanorama.Load( blocks );

		if ( panorama is null )
			Log.Warning( $"{path}: a panorama sky needs an .exr or .hdr - without one it lights nothing" );

		return panorama;
	}

	//The table on the device, remade when the table changes; nought without one. Drawing thread
	internal static ulong TableTexture( Sky sky )
	{
		Sweep();

		if ( sky?.Table is null ) return 0;

		if ( ReferenceEquals( sky.Table, sky.TableMade ) && sky.TableTexture != 0 ) return sky.TableTexture;

		CoreRenderDevice.DestroyTexture( sky.TableTexture );

		sky.TableTexture = CoreRenderDevice.CreateTextureHalf( sky.Table, CoreSkyTable.Side, CoreSkyTable.Side );
		sky.TableMade = sky.Table;

		return sky.TableTexture;
	}

	//The reflection levels on the device, made once a picture; nought without them
	internal static ulong ReflectionTexture( Sky sky )
	{
		if ( sky?.Panorama?.Reflections is not { } levels ) return 0;

		lock ( sky )
		{
			if ( ReferenceEquals( sky.ReflectedFrom, sky.Panorama ) && sky.ReflectionTexture != 0 ) return sky.ReflectionTexture;

			CoreRenderDevice.DestroyTexture( sky.ReflectionTexture );

			sky.ReflectionTexture = CoreRenderDevice.CreateTexture( levels );
			sky.ReflectedFrom = sky.Panorama;

			return sky.ReflectionTexture;
		}
	}

	//Lets go of the device tables of skies whose material is gone
	private static void Sweep()
	{
		lock ( made )
		{
			for ( var i = made.Count - 1; i >= 0; i-- )
			{
				if ( made[i].Material.TryGetTarget( out _ ) ) continue;

				CoreRenderDevice.DestroyTexture( made[i].Sky.TableTexture );
				CoreRenderDevice.DestroyTexture( made[i].Sky.ReflectionTexture );
				made.RemoveAt( i );
			}
		}
	}
}
