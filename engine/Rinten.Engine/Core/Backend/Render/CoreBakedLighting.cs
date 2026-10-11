using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Rinten.Bake;

namespace Rinten;

//Runs bakes, uploads the results for the frame and gives each renderer its atlas square
internal static class CoreBakedLighting
{
	//What one component has on the card.
	private sealed class Held
	{
		public int Revision = -1;
		public ulong Sheet, Ways, Red, Green, Blue, SheetMask, VolumeMask, SheetSky, VolumeSky, Walls;

		/// <summary>Each light the bake took something of: its mask channel, or -1 where it took the whole of it.</summary>
		public readonly Dictionary<Guid, int> Lights = new();

		/// <summary>A bake from before the masks, naming no lights - see BakedLightingData.IsOld.</summary>
		public bool Old;

		/// <summary>Each glow layer's sheet and volume, and its group - see BakedGlow.</summary>
		public readonly ulong[] GlowSheet = new ulong[BakedGlow.Groups], GlowVolume = new ulong[BakedGlow.Groups];
		public readonly int[] GlowGroup = new int[BakedGlow.Groups];

		/// <summary>The sun's and sky's bounce, relit as they change; null for a bake without surfels.</summary>
		public CoreLiveBounce Bounce;

		public void Release()
		{
			foreach ( var texture in new[] { Sheet, Ways, Red, Green, Blue, SheetMask, VolumeMask, SheetSky, VolumeSky, Walls } )
				if ( texture != 0 ) CoreRenderDevice.DestroyTexture( texture );

			for ( var i = 0; i < BakedGlow.Groups; ++i )
			{
				if ( GlowSheet[i] != 0 ) CoreRenderDevice.DestroyTexture( GlowSheet[i] );
				if ( GlowVolume[i] != 0 ) CoreRenderDevice.DestroyTexture( GlowVolume[i] );
			}

			Bounce?.Release();
			Bounce = null;

			Array.Clear( GlowSheet );
			Array.Clear( GlowVolume );
			Array.Clear( GlowGroup );

			Sheet = Ways = Red = Green = Blue = SheetMask = VolumeMask = SheetSky = VolumeSky = Walls = 0;
			Lights.Clear();
			Old = false;
			Revision = -1;
		}
	}

	//Every bake that is switched on, whichever scene it is in.
	private static readonly List<BakedLighting> enabled = new();

	private static readonly Dictionary<BakedLighting, Held> held = new();

	//The bake a scene reads: the first switched on in it that has one.
	private static BakedLighting Of( Scene scene )
	{
		if ( scene is null ) return null;

		foreach ( var bake in enabled )
			if ( bake.Scene == scene && bake.HasBake ) return bake;

		return null;
	}

	//Tells every renderer the bake has a square for where it is, and starts the bake being read.
	internal static void Place( BakedLighting bake )
	{
		if ( bake is null ) return;

		if ( !enabled.Contains( bake ) ) enabled.Add( bake );

		if ( bake.Data is not { } data || !bake.Scene.IsValid() ) return;

		foreach ( var place in data.Places )
		{
			if ( bake.Scene.Directory.FindComponentByGuid( place.Renderer ) is not ModelRenderer renderer ) continue;
			if ( !Fits( renderer, place ) ) continue;

			renderer.LightmapRect = new System.Numerics.Vector4( place.Rect.x, place.Rect.y, place.Rect.z, place.Rect.w );
		}
	}

	//Whether a renderer is still what was baked: the same model, standing still. Anything else was changed
	//since, and reads the volume until the next bake rather than a square that belongs to another shape.
	private static bool Fits( ModelRenderer renderer, in BakedLightingData.Place place )
		=> renderer.IsValid() && renderer is not SkinnedModelRenderer && renderer.GameObject.IsStatic
			&& string.Equals( renderer.Model?.ResourcePath ?? "", place.Model ?? "", StringComparison.OrdinalIgnoreCase );

	//Takes a bake off its renderers and off the card.
	internal static void Unplace( BakedLighting bake )
	{
		if ( bake is null ) return;

		enabled.Remove( bake );

		if ( held.Remove( bake, out var was ) ) was.Release();

		if ( bake.Data is not { } data || !bake.Scene.IsValid() ) return;

		foreach ( var place in data.Places )
		{
			if ( bake.Scene.Directory.FindComponentByGuid( place.Renderer ) is ModelRenderer renderer && renderer.IsValid() )
				renderer.LightmapRect = default;
		}
	}

	//Writes where a scene's bake is into a frame block - see bakedMaps in frame_data.slang. True where there
	//was one to write; the block is left at nought where there was not, which the shader reads as no bake.
	internal static bool Write( float[] frame, Scene scene )
	{
		if ( frame is null || frame.Length < CoreSceneRenderer.FrameFloats ) return false;

		var at = CoreSceneRenderer.BakedAt;
		//The sheet, the volume, the masks, the sun's channel and the glow layers - see frame_data.slang
		Array.Clear( frame, at, 40 );
		writing = null;

		//The portable frame reads neither sheet nor volume (HasBakedSheet, HasBakedVolume): nothing is uploaded
		if ( CoreRenderDevice.PortableFrame ) return false;

		var bake = Of( scene );
		if ( bake is null || bake.Data is not { } data ) return false;

		if ( !held.TryGetValue( bake, out var on ) )
		{
			on = new Held();
			held[bake] = on;
		}

		if ( on.Revision != data.Revision ) Upload( on, data );

		writing = on;
		var any = false;

		if ( on.Sheet != 0 && on.Ways != 0 )
		{
			frame[at + 0] = CoreRender.TextureIndex( on.Sheet );
			frame[at + 1] = CoreRender.TextureIndex( on.Ways );
			frame[at + 2] = on.SheetSky != 0 ? CoreRender.TextureIndex( on.SheetSky ) : 0.0f;
			frame[at + 3] = 1.0f;
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
			frame[at + 21] = on.Walls != 0 ? CoreRender.TextureIndex( on.Walls ) : 0.0f;
			any = true;

			if ( on.Bounce is not null )
			{
				on.Bounce.Tick( scene );

				if ( on.Bounce.Light != 0 && on.Bounce.Lean != 0 )
				{
					frame[at + 22] = CoreRender.TextureIndex( on.Bounce.Light );
					frame[at + 23] = CoreRender.TextureIndex( on.Bounce.Lean );
				}
			}
		}

		//The masks where shadows are baked: surfaces on the sheet read its mask, the rest the volume's,
		//so the volume's mask is what says there is one
		if ( on.VolumeMask != 0 && frame[at + 7] > 0.5f )
		{
			frame[at + 17] = CoreRender.TextureIndex( on.VolumeMask );
			frame[at + 19] = 1.0f;

			if ( on.SheetMask != 0 && frame[at + 3] > 0.5f )
			{
				frame[at + 16] = CoreRender.TextureIndex( on.SheetMask );
				frame[at + 18] = 1.0f;
			}
		}

		WriteGlows( frame, at + 24, on, scene );

		return any;
	}

	//What Write found for the frame being written, for ChannelOf.
	private static Held writing;

	//What a channel of ChannelOf says about a light the bake holds nothing of.
	internal const int NotBaked = -2;

	//And about one it holds the whole of.
	internal const int BakedWhole = -1;

	//A light's shadow mask channel in the current bake, BakedWhole, or NotBaked; valid after Write
	internal static int ChannelOf( Light light )
	{
		if ( writing is null || light is null || light.NotBaked ) return NotBaked;

		//A bake from before the masks names no lights: each stays in the frame until baked again
		if ( writing.Old ) return NotBaked;

		if ( !writing.Lights.TryGetValue( light.Id, out var channel ) ) return NotBaked;

		//A mask with no picture to read leaves its light out of the bake: drawn as any other, shadow maps and all
		if ( channel >= 0 && writing.VolumeMask == 0 ) return NotBaked;

		return channel;
	}

	private static void Upload( Held on, BakedLightingData data )
	{
		on.Release();
		on.Revision = data.Revision;
		on.Old = data.IsOld;

		foreach ( var light in data.Lights ) on.Lights[light.Light] = light.Channel;

		//Linear, as everything in a mask is a fraction and not a colour.
		if ( data.HasVolumeMask )
		{
			on.VolumeMask = CoreRenderDevice.CreateTextureRgba( data.VolumeMask, data.VolumeX * data.VolumeY, data.VolumeZ, false );

			if ( data.HasSheetMask )
				on.SheetMask = CoreRenderDevice.CreateTextureRgba( data.SheetMask, data.SheetWidth, data.SheetHeight, false );
		}

		if ( data.HasSheet )
		{
			on.Sheet = CoreRenderDevice.CreateTextureHalf( data.Sheet, data.SheetWidth, data.SheetHeight );
			on.Ways = CoreRenderDevice.CreateTextureHalf( data.Ways, data.SheetWidth, data.SheetHeight );
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

			if ( data.HasBounce ) on.Bounce = new CoreLiveBounce( data );

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
			Bounces = Math.Clamp( bake.Bounces, 1, 16 ),
			SkyStrength = MathF.Max( bake.SkyStrength, 0.0f ),
			AlbedoBoost = MathF.Max( bake.BounceStrength, 0.0f ),
			SunCosine = MathF.Cos( MathF.Max( bake.SunWidth, 0.0f ) * 0.5f * MathF.PI / 180.0f ),
		};

		world.Gather( scene );

		//The sun and sky light the bake's surfels in the frame, so their bounces are left out of it - see CoreLiveBounce
		world.LiveBounce = true;
		var liveSun = Guid.Empty;

		foreach ( var light in scene.GetAllComponents<DirectionalLight>() )
		{
			if ( !light.IsValid() || !light.Enabled || !light.Active || light.BakedWhole ) continue;
			if ( !light.Contribution.HasFlag( Light.LightContribution.Diffuse ) ) continue;

			liveSun = light.Id;
			break;
		}

		if ( world.Corners.Length == 0 )
		{
			Log.Warning( "baked lighting: nothing in the scene is marked Static, so there is nothing to bake" );
			return null;
		}

		var sheetSide = 0;
		var packed = world.Receivers.Count > 0 && BakeSheet.Pack( world, bake.TexelsPerMetre, Math.Clamp( bake.MaxResolution, 256, 8192 ), out sheetSide );

		if ( world.Receivers.Count == 0 )
			Log.Info( "baked lighting: no still model has a second set of coordinates - everything reads the volume" );
		else if ( !packed )
			Log.Warning( $"baked lighting: {world.Receivers.Count} surfaces do not fit a {bake.MaxResolution} atlas at any density - only the volume is baked" );

		var samples = Math.Clamp( bake.Samples, 1, 65536 );
		var volumeSamples = Math.Clamp( bake.VolumeSamples, 16, 65536 );
		var denoise = MathF.Max( bake.Denoise, 0.0f );
		var spacing = bake.VolumeSpacing;

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
		Half[] sheetLight = null, sheetWays = null, sheetSky = null, volumeRed = null, volumeGreen = null, volumeBlue = null, volumeSky = null;
		byte[] sheetMask = null, volumeMask = null, volumeWalls = null;

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

				( sheetLight, sheetWays, sheetMask, sheetSky ) = ( sheet.Light, sheet.Ways, sheet.Mask, sheet.Sky );
			}

			volume.Lay( world.Bounds, spacing );

			if ( !volume.TraceOnCard( world, volumeSamples, progress, stop ) )
				volume.Trace( world, volumeSamples, progress, stop );

			( volumeRed, volumeGreen, volumeBlue, volumeMask, volumeSky ) = ( volume.Red, volume.Green, volume.Blue, volume.Mask, volume.Sky );

			//The still world alone decides these, so once for all the passes
			volume.FindWalls( world, progress, stop );
			volumeWalls = volume.Walls;

			//About two surfels a volume step, which is as fine as the bounce they carry is read
			surfels.Scatter( world, Math.Clamp( volume.Step * 0.5f, 0.5f, 2.0f ) );
			surfels.Trace( world, progress, stop );
			surfels.Probes( world, volume, progress, stop );

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
			VolumeMask = volumeMask ?? [],
			VolumeSky = volumeSky ?? [],
			VolumeWalls = volumeWalls ?? [],
			LiveSun = liveSun,
			Surfels = surfels.Data,
			SurfelSun = surfels.Sun,
			SurfelDepth = surfels.Depth,
			SurfelLinks = surfels.Link,
			SurfelWeights = surfels.Weight,
			ProbeLinks = surfels.ProbeLink,
			ProbeWeights = surfels.ProbeWeight,
			Glows = glows,
		};

		foreach ( var light in world.Lights )
		{
			if ( !light.Live ) data.Bounced.Add( light.Id );

			if ( light.Whole || light.Mask >= 0 )
				data.Lights.Add( new BakedLightingData.BakedLight { Light = light.Id, Channel = light.Whole ? -1 : light.Mask } );
		}

		if ( sheet is not null )
		{
			data.SheetWidth = sheet.Width;
			data.SheetHeight = sheet.Height;
			data.Sheet = sheetLight;
			data.Ways = sheetWays;
			data.SheetMask = sheetMask ?? [];
			data.SheetSky = sheetSky ?? [];

			foreach ( var receiver in world.Receivers )
			{
				var rect = sheet.Rect( receiver );

				data.Places.Add( new BakedLightingData.Place
				{
					Renderer = receiver.Renderer.Id,
					Model = receiver.Model.ResourcePath ?? "",
					Rect = new Vector4( rect.X, rect.Y, rect.Z, rect.W ),
				} );
			}
		}

		data.Touched();

		Log.Info( $"baked lighting: {world.Corners.Length / 3} triangles, {world.Receivers.Count} surfaces on a {sheetSide}² atlas, " +
			$"{volume.CountX}x{volume.CountY}x{volume.CountZ} volume points {volume.Step:0.##} m apart, {surfels.Count} surfels, {glows.Count} glow layers, " +
			$"{( DateTime.UtcNow - started ).TotalSeconds:0.0} s" );

		return data;
	}
}
