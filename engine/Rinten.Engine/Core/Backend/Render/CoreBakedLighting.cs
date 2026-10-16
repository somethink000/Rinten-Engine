using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Rinten.Bake;

namespace Rinten;

//Runs bakes, uploads the results for the frame and gives each renderer its atlas square
internal static partial class CoreBakedLighting
{
	//What one scene's bake has on the card.
	private sealed class Held
	{
		public int Revision = -1;
		public ulong Sheet, Band, Red, Green, Blue, SheetSky, VolumeSky, Walls;

		/// <summary>The lights in the bake, whose light a lightmapped surface already has - see InBake.</summary>
		public readonly HashSet<Guid> Baked = new();

		/// <summary>Each glow layer's sheet and volume, and its group - see BakedGlow.</summary>
		public readonly ulong[] GlowSheet = new ulong[BakedGlow.Groups], GlowVolume = new ulong[BakedGlow.Groups];
		public readonly int[] GlowGroup = new int[BakedGlow.Groups];

		/// <summary>The sun's and sky's bounce, relit as they change; null for a bake without surfels.</summary>
		public CoreLiveBounce Bounce;

		/// <summary>The portable frame's two pictures and what they were made with - see Portable.</summary>
		public PortablePictures Portable;

		public void Release()
		{
			foreach ( var texture in new[] { Sheet, Band, Red, Green, Blue, SheetSky, VolumeSky, Walls } )
				if ( texture != 0 ) CoreRenderDevice.DestroyTexture( texture );

			for ( var i = 0; i < BakedGlow.Groups; ++i )
			{
				if ( GlowSheet[i] != 0 ) CoreRenderDevice.DestroyTexture( GlowSheet[i] );
				if ( GlowVolume[i] != 0 ) CoreRenderDevice.DestroyTexture( GlowVolume[i] );
			}

			Bounce?.Release();
			Bounce = null;

			Portable?.Release();
			Portable = null;

			Array.Clear( GlowSheet );
			Array.Clear( GlowVolume );
			Array.Clear( GlowGroup );

			Sheet = Band = Red = Green = Blue = SheetSky = VolumeSky = Walls = 0;
			Baked.Clear();
			Revision = -1;
		}
	}

	private static readonly Dictionary<BakedLighting, Held> held = new();

	//Lightmap texels a side of a block of the live bounce's sheet - see BakeSheet.BounceBlocks
	internal const int BounceBlock = 4;

	//The bake a scene reads, where it has one switched on.
	private static BakedLighting Of( Scene scene )
		=> scene?.LightingIfAny is { Enabled: true, HasBake: true } bake ? bake : null;

	//Tells every renderer the bake has a square for where it is. On the first frame that reads the bake rather than
	//when it is loaded, as a mesh has no model until it has started
	private static void Place( BakedLighting bake, BakedLightingData data )
	{
		bake.Placed = data;

		if ( !bake.Scene.IsValid() ) return;

		foreach ( var place in data.Places )
		{
			var rect = new System.Numerics.Vector4( place.Rect.x, place.Rect.y, place.Rect.z, place.Rect.w );

			switch ( bake.Scene.Directory.FindComponentByGuid( place.Renderer ) )
			{
				case ModelRenderer renderer when Fits( renderer, place ):
					renderer.LightmapRect = rect;
					break;

				//A mesh edited since has a sheet of another shape: none until the next bake, as with a changed model
				case MeshComponent built when built.IsValid() && built.GameObject.IsStatic && built.Model is { } model
					&& model.GetVertices()?.Length == place.Vertices:
					built.LightmapRect = rect;
					break;
			}
		}
	}

	//Whether a renderer is still what was baked: the same model, standing still. Anything else was changed
	//since, and reads the volume until the next bake rather than a square that belongs to another shape.
	private static bool Fits( ModelRenderer renderer, in BakedLightingData.Place place )
		=> renderer.IsValid() && renderer is not SkinnedModelRenderer && renderer.GameObject.IsStatic
			&& string.Equals( renderer.Model?.ResourcePath ?? "", place.Model ?? "", StringComparison.OrdinalIgnoreCase );

	//Takes a bake off its renderers and off the card; the next frame that reads it places it again.
	internal static void Unplace( BakedLighting bake )
	{
		if ( bake is null ) return;

		if ( held.Remove( bake, out var was ) ) was.Release();

		if ( bake.Placed is not { } data ) return;
		bake.Placed = null;

		if ( !bake.Scene.IsValid() ) return;

		foreach ( var place in data.Places )
		{
			switch ( bake.Scene.Directory.FindComponentByGuid( place.Renderer ) )
			{
				case ModelRenderer renderer when renderer.IsValid():
					renderer.LightmapRect = default;
					break;

				case MeshComponent built when built.IsValid():
					built.LightmapRect = default;
					break;
			}
		}
	}

	//Writes where a scene's bake is into a frame block - see bakedMaps in frame_data.slang. True where there
	//was one to write; the block is left at nought where there was not, which the shader reads as no bake.
	internal static bool Write( float[] frame, Scene scene )
	{
		if ( frame is null || frame.Length < CoreSceneRenderer.FrameFloats ) return false;

		var at = CoreSceneRenderer.BakedAt;
		//The sheet, the volume, its walls and bounce, and the glow layers - see frame_data.slang
		Array.Clear( frame, at, 36 );
		writing = null;

		Array.Clear( frame, CoreSceneRenderer.PortableBakeAt, 4 );
		PortableSheet = PortableVolume = 0;

		var bake = Of( scene );
		if ( bake is null || bake.Data is not { } data ) return false;

		//A new bake into the lightmap, or another lightmap: off the old squares and onto the new
		if ( !ReferenceEquals( bake.Placed, data ) )
		{
			Unplace( bake );
			Place( bake, data );
		}

		if ( !held.TryGetValue( bake, out var on ) )
		{
			on = new Held();
			held[bake] = on;
		}

		if ( on.Revision != data.Revision ) Upload( on, data );

		writing = on;

		//Its own two pictures in the portable frame, which has no table to read the full one's out of
		if ( CoreRenderDevice.PortableFrame ) return WritePortable( frame, on, data, scene );

		var any = false;

		if ( on.Sheet != 0 )
		{
			frame[at + 0] = CoreRender.TextureIndex( on.Sheet );
			frame[at + 2] = on.SheetSky != 0 ? CoreRender.TextureIndex( on.SheetSky ) : 0.0f;
			frame[at + 3] = 1.0f;
			frame[at + 19] = on.Band != 0 ? CoreRender.TextureIndex( on.Band ) : 0.0f;
			any = true;
		}

		if ( on.Red != 0 && on.Green != 0 && on.Blue != 0 )
		{
			frame[at + 4] = CoreRender.TextureIndex( on.Red );
			frame[at + 5] = CoreRender.TextureIndex( on.Green );
			frame[at + 6] = CoreRender.TextureIndex( on.Blue );
			frame[at + 7] = 1.0f;

			frame[at + 8] = data.VolumeOrigin.x;
			frame[at + 9] = data.VolumeOrigin.y;
			frame[at + 10] = data.VolumeOrigin.z;
			frame[at + 11] = 1.0f / data.VolumeStep;

			frame[at + 12] = data.VolumeX;
			frame[at + 13] = data.VolumeY;
			frame[at + 14] = data.VolumeZ;
			frame[at + 15] = on.VolumeSky != 0 ? CoreRender.TextureIndex( on.VolumeSky ) : 0.0f;
			frame[at + 16] = on.Walls != 0 ? CoreRender.TextureIndex( on.Walls ) : 0.0f;
			any = true;

			if ( on.Bounce is not null )
			{
				on.Bounce.Tick( scene );

				if ( on.Bounce.Light != 0 && on.Bounce.Lean != 0 )
				{
					frame[at + 17] = CoreRender.TextureIndex( on.Bounce.Light );
					frame[at + 18] = CoreRender.TextureIndex( on.Bounce.Lean );

					//The sheet the worker lays; traced on the card, the bounce is in the volume alone
					if ( on.Bounce.Sheet != 0 && !CoreLiveBounce.Rays ) frame[CoreSceneRenderer.BakedTexelsAt] = CoreRender.TextureIndex( on.Bounce.Sheet );
				}
			}
		}

		WriteGlows( frame, at + 20, on, scene );

		return any;
	}

	/// <summary>Whether a scene has a bake it reads.</summary>
	internal static bool Baked( Scene scene ) => Of( scene ) is not null;

	/// <summary>Traces a scene's bounce with hardware rays, where the card has them; in the frame's recording.</summary>
	internal static void TraceBounce( Scene scene )
	{
		if ( !CoreLiveBounce.Rays || Of( scene ) is not { } bake ) return;
		if ( held.TryGetValue( bake, out var on ) ) on.Bounce?.Trace( scene );
	}

	//What Write found for the frame being written, for InBake.
	private static Held writing;

	//Whether a light is in the bake the frame being written reads: a lightmapped surface has its light already and
	//takes only what movers shade of it, as UE's stationary lights; valid after Write
	internal static bool InBake( Light light )
		=> writing is not null && light is { NotBaked: false } && writing.Baked.Contains( light.Id );

	private static void Upload( Held on, BakedLightingData data )
	{
		on.Release();
		on.Revision = data.Revision;

		foreach ( var light in data.Baked ) on.Baked.Add( light );

		if ( data.HasBounce ) on.Bounce = new CoreLiveBounce( data );

		//The portable frame makes pictures of its own, out of the data, as it writes the frame
		if ( CoreRenderDevice.PortableFrame ) return;

		if ( data.HasSheet )
		{
			on.Sheet = CoreRenderDevice.CreateTextureHalf( data.Sheet, data.SheetWidth, data.SheetHeight );
		}

		if ( data.HasVolume )
		{
			var across = data.VolumeX * data.VolumeY;

			on.Red = CoreRenderDevice.CreateTextureHalf( data.VolumeRed, across, data.VolumeZ );
			on.Green = CoreRenderDevice.CreateTextureHalf( data.VolumeGreen, across, data.VolumeZ );
			on.Blue = CoreRenderDevice.CreateTextureHalf( data.VolumeBlue, across, data.VolumeZ );

			//Bytes of nibbles, read whole: linear, and only ever at a texel's middle
			if ( data.HasWalls )
				on.Walls = CoreRenderDevice.CreateTextureRgba( data.VolumeWalls, across, data.VolumeZ * 3, false );

			//Three half sheets wide, a colour each, linear - see BakeSheet.Band
			if ( data.HasBand )
				on.Band = CoreRenderDevice.CreateTextureRgba( data.SheetBand, Math.Max( data.SheetWidth / 2, 1 ) * 3, Math.Max( data.SheetHeight / 2, 1 ), false );

			if ( data.HasSky )
			{
				on.VolumeSky = CoreRenderDevice.CreateTextureHalf( data.VolumeSky, across, data.VolumeZ );

				if ( data.HasSheet && data.SheetSky.Length == data.Sheet.Length )
					on.SheetSky = CoreRenderDevice.CreateTextureHalf( data.SheetSky, data.SheetWidth, data.SheetHeight );
			}
		}

		for ( var i = 0; i < Math.Min( data.Glows.Count, BakedGlow.Groups ); ++i )
		{
			var glow = data.Glows[i];
			on.GlowGroup[i] = Math.Clamp( glow.Group, 1, BakedGlow.Groups );

			if ( data.HasSheet && glow.Sheet?.Length == data.Sheet.Length )
				on.GlowSheet[i] = CoreRenderDevice.CreateTextureHalf( glow.Sheet, data.SheetWidth, data.SheetHeight );

			if ( data.HasVolume && glow.Volume?.Length == data.VolumeRed.Length )
				on.GlowVolume[i] = CoreRenderDevice.CreateTextureHalf( glow.Volume, data.VolumeX * data.VolumeY, data.VolumeZ );
		}
	}

	//Each glow layer's pictures and its group's brightness now - see bakedGlow in frame_data.slang
	private static void WriteGlows( float[] frame, int at, Held on, Scene scene )
	{
		Span<float> brightness = stackalloc float[BakedGlow.Groups];
		var any = false;

		for ( var i = 0; i < BakedGlow.Groups; ++i ) any |= on.GlowGroup[i] != 0;
		if ( !any ) return;

		//The brightest switched-on glow of a group decides it; a group with none on is dark
		foreach ( var glow in scene.GetAllComponents<BakedGlow>() )
		{
			if ( !glow.IsValid() || !glow.Active ) continue;
			brightness[glow.Layer] = MathF.Max( brightness[glow.Layer], MathF.Max( glow.Brightness, 0.0f ) );
		}

		for ( var i = 0; i < BakedGlow.Groups; ++i )
		{
			if ( on.GlowGroup[i] == 0 ) continue;

			var slot = at + ( i * 4 );
			frame[slot + 0] = on.GlowSheet[i] != 0 && frame[CoreSceneRenderer.BakedAt + 3] > 0.5f ? CoreRender.TextureIndex( on.GlowSheet[i] ) : 0.0f;
			frame[slot + 1] = on.GlowVolume[i] != 0 && frame[CoreSceneRenderer.BakedAt + 7] > 0.5f ? CoreRender.TextureIndex( on.GlowVolume[i] ) : 0.0f;
			frame[slot + 2] = brightness[on.GlowGroup[i] - 1];
		}
	}

	//A volume's colour constants, one point a texel - what a glow layer keeps of it, its lean left out
	private static Half[] Constants( BakeVolume volume )
	{
		var points = volume.CountX * volume.CountY * volume.CountZ;
		var made = new Half[points * 4];

		for ( var i = 0; i < points; ++i )
		{
			made[( i * 4 ) + 0] = volume.Red[i * 4];
			made[( i * 4 ) + 1] = volume.Green[i * 4];
			made[( i * 4 ) + 2] = volume.Blue[i * 4];
			made[( i * 4 ) + 3] = (Half)1.0f;
		}

		return made;
	}

	//Everything off the card, on device teardown.
	internal static void Release()
	{
		foreach ( var on in held.Values ) on.Release();
		held.Clear();
	}

	//Bakes the component's scene with its settings: read on the main thread, traced on every other core
	//Null where it was cancelled or there was nothing to bake
	internal static async Task<BakedLightingData> Bake( BakedLighting bake, CancellationToken cancel )
	{
		ThreadSafe.AssertIsMainThread();

		var scene = bake.Scene;
		var started = DateTime.UtcNow;

		var world = new BakeScene
		{
			Bounces = Math.Clamp( bake.Lightmaps.Bounces, 0, 16 ),
			SkyStrength = MathF.Max( bake.Lightmaps.SkyStrength, 0.0f ),
			AlbedoBoost = MathF.Max( bake.Lightmaps.BounceStrength, 0.0f ),
			SunCosine = MathF.Cos( MathF.Max( bake.Lightmaps.SunWidth, 0.0f ) * 0.5f * MathF.PI / 180.0f ),
		};

		world.Gather( scene );

		//A sun left out of the bake has its bounce lit in the frame, through the surfels - see CoreLiveBounce
		var liveSun = Guid.Empty;

		foreach ( var light in scene.GetAllComponents<DirectionalLight>() )
		{
			if ( !light.IsValid() || !light.Enabled || !light.Active || !light.NotBaked ) continue;
			if ( !( ( light.Contribution & Light.LightContribution.Diffuse ) == Light.LightContribution.Diffuse ) ) continue;

			liveSun = light.Id;
			break;
		}

		if ( world.Corners.Length == 0 )
		{
			Log.Warning( "baked lighting: nothing in the scene is marked Static, so there is nothing to bake" );
			return null;
		}

		var sheetSide = 0;
		var packed = world.Receivers.Count > 0 && BakeSheet.Pack( world, bake.Lightmaps.TexelsPerMetre, Math.Clamp( (int)bake.Lightmaps.Resolution, 256, 8192 ), out sheetSide );

		if ( world.Receivers.Count == 0 )
			Log.Info( "baked lighting: no still model has a second set of coordinates - everything reads the volume" );
		else if ( !packed )
			Log.Warning( $"baked lighting: {world.Receivers.Count} surfaces do not fit a {(int)bake.Lightmaps.Resolution} atlas at any density - only the volume is baked" );

		var samples = BakedLighting.LightmapSettings.TexelSamples;
		var volumeSamples = BakedLighting.LightmapSettings.VolumeSamples;
		var denoise = MathF.Max( bake.Lightmaps.Denoise, 0.0f );
		var spacing = bake.Lightmaps.VolumeSpacing;

		var progress = new BakeProgress();
		using var section = Application.Editor?.ProgressSection();

		if ( section is not null )
		{
			section.Icon = "wb_iridescent";
			section.Title = "Baking lighting";
		}

		using var both = section is not null
			? CancellationTokenSource.CreateLinkedTokenSource( cancel, section.GetCancel() )
			: CancellationTokenSource.CreateLinkedTokenSource( cancel );

		var stop = both.Token;

		BakeSheet sheet = null;
		var volume = new BakeVolume();
		var surfels = new BakeSurfels();

		//The main bake's results, kept as the glow groups' passes reuse the same sheet and lattice
		Half[] sheetLight = null, sheetSky = null, volumeRed = null, volumeGreen = null, volumeBlue = null, volumeSky = null;
		byte[] sheetBand = null;
		byte[] volumeWalls = null;
		int bounceWidth = 0, bounceHeight = 0;

		var groups = world.GlowGroups();
		var glows = new List<BakedLightingData.Glow>();

		var work = Task.Run( () =>
		{
			progress.Begin( "Building the world's tree", 1 );
			world.Build();

			if ( packed )
			{
				sheet = new BakeSheet();
				sheet.Rasterise( world, sheetSide );

				//On the card where it can - the same bake, see BakeGpu - and on every core where it cannot.
				if ( !sheet.TraceOnCard( world, samples, progress, stop ) )
					sheet.Trace( world, samples, progress, stop );

				progress.Begin( "Filtering", 1 );
				sheet.Denoise( world, denoise );
				sheet.Dilate();
				sheet.Encode();
				( sheetLight, sheetSky, sheetBand ) = ( sheet.Light, sheet.Sky, sheet.Band );
			}

			volume.Lay( world.Bounds, spacing );

			if ( !volume.TraceOnCard( world, volumeSamples, progress, stop ) )
				volume.Trace( world, volumeSamples, progress, stop );

			( volumeRed, volumeGreen, volumeBlue, volumeSky ) = ( volume.Red, volume.Green, volume.Blue, volume.Sky );

			//The still world alone decides these, so once for all the passes
			volume.FindWalls( world, progress, stop );
			volumeWalls = volume.Walls;

			//About two surfels a volume step, which is as fine as the bounce they carry is read
			surfels.Scatter( world, Math.Clamp( volume.Step * 0.5f, 0.5f, 2.0f ) );
			surfels.Trace( world, progress, stop );
			surfels.Probes( world, volume, progress, stop );

			//And each block of the lightmap, so the live bounce on a still surface is as fine as its lightmap's half
			if ( sheet is not null )
			{
				sheet.BounceBlocks( BounceBlock, out bounceWidth, out bounceHeight, out var blockAt, out var blockFacing, out var blockKey );
				surfels.Texels( world, blockAt, blockFacing, blockKey, bounceWidth, bounceHeight, progress, stop );
			}

			//Each glow group's light alone, on the same sheet and lattice - see BakedGlow
			foreach ( var group in groups )
			{
				world.UsePass( group );

				Half[] glowSheet = null;

				if ( sheet is not null )
				{
					if ( !sheet.TraceOnCard( world, samples, progress, stop ) )
						sheet.Trace( world, samples, progress, stop );

					progress.Begin( "Filtering", 1 );
					sheet.Denoise( world, denoise );
					sheet.Dilate();
					sheet.Encode();
					glowSheet = sheet.Light;
				}

				if ( !volume.TraceOnCard( world, volumeSamples, progress, stop ) )
					volume.Trace( world, volumeSamples, progress, stop );

				glows.Add( new BakedLightingData.Glow { Group = group, Sheet = glowSheet ?? [], Volume = Constants( volume ) } );
			}

			world.UsePass( 0 );
		}, stop );

		while ( !work.IsCompleted )
		{
			if ( section is not null )
			{
				section.Subtitle = progress.Stage;
				section.TotalCount = Math.Max( progress.Total, 1 );
				section.Current = progress.Done;
			}

			await GameTask.DelayRealtime( 200 );
		}

		try
		{
			await work;
		}
		catch ( OperationCanceledException )
		{
			Log.Info( "baked lighting: cancelled" );
			return null;
		}
		catch ( Exception e )
		{
			Log.Warning( $"baked lighting failed: {e}" );
			return null;
		}

		await GameTask.MainThread();

		var data = new BakedLightingData
		{
			VolumeX = volume.CountX,
			VolumeY = volume.CountY,
			VolumeZ = volume.CountZ,
			VolumeOrigin = new Vector3( volume.Origin.X, volume.Origin.Y, volume.Origin.Z ),
			VolumeStep = volume.Step,
			VolumeRed = volumeRed,
			VolumeGreen = volumeGreen,
			VolumeBlue = volumeBlue,
			VolumeSky = volumeSky ?? [],
			VolumeWalls = volumeWalls ?? [],
			LiveSun = liveSun,
			Surfels = surfels.Data,
			SurfelSun = surfels.Sun,
			SurfelDepth = surfels.Depth,
			SurfelClusters = surfels.Cluster,
			SurfelLinks = surfels.Link,
			SurfelWeights = surfels.Weight,
			ProbeLinks = surfels.ProbeLink,
			ProbeWeights = surfels.ProbeWeight,
			BounceWidth = bounceWidth,
			BounceHeight = bounceHeight,
			TexelSlots = surfels.TexelSlot,
			TexelKeys = surfels.TexelKey,
			TexelLinks = surfels.TexelLink,
			TexelWeights = surfels.TexelWeight,
			Glows = glows,
		};

		foreach ( var light in world.Lights ) data.Baked.Add( light.Id );

		if ( sheet is not null )
		{
			data.SheetWidth = sheet.Width;
			data.SheetHeight = sheet.Height;
			data.Sheet = sheetLight;
			data.SheetSky = sheetSky ?? [];
			data.SheetBand = sheetBand ?? [];

			foreach ( var receiver in world.Receivers )
			{
				var rect = sheet.Rect( receiver );

				data.Places.Add( new BakedLightingData.Place
				{
					Renderer = receiver.Renderer.Id,
					Model = receiver.Model.ResourcePath ?? "",
					Vertices = receiver.Model.GetVertices()?.Length ?? 0,
					Rect = new Vector4( rect.X, rect.Y, rect.Z, rect.W ),
				} );
			}
		}

		data.Touched();

		Log.Info( $"baked lighting: {world.Corners.Length / 3} triangles, {world.Receivers.Count} surfaces on a {sheetSide}² atlas, " +
			$"{volume.CountX}x{volume.CountY}x{volume.CountZ} volume points {volume.Step:0.##} m apart, {surfels.Count} surfels in {surfels.Clusters} clusters, {glows.Count} glow layers, " +
			$"{( DateTime.UtcNow - started ).TotalSeconds:0.0} s" );

		return data;
	}
}
