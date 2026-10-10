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
		public ulong Sheet, Ways, Red, Green, Blue, SheetMask, VolumeMask;

		/// <summary>Each light the bake took something of: its mask channel, or -1 where it took the whole of it.</summary>
		public readonly Dictionary<Guid, int> Lights = new();

		/// <summary>A bake from before the masks, which took every Baked light whole - see BakedLightingData.IsOld.</summary>
		public bool Old;

		public void Release()
		{
			foreach ( var texture in new[] { Sheet, Ways, Red, Green, Blue, SheetMask, VolumeMask } )
				if ( texture != 0 ) CoreRenderDevice.DestroyTexture( texture );

			Sheet = Ways = Red = Green = Blue = SheetMask = VolumeMask = 0;
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
		//The sheet, the volume, the masks and the sun's channel - see frame_data.slang.
		Array.Clear( frame, at, 24 );
		writing = null;

		//The portable frame reads neither the sheet nor the volume - see HasBakedSheet and HasBakedVolume in frame.slang, both of which answer
		//false there - so the block is left at nought and, more to the point, the atlas and the volume are not uploaded to the card at all.
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
			any = true;
		}

		//The masks, where any light's shadows are baked - see bakedMask in frame_data.slang. A surface with a place on the sheet reads the sheet's, everything
		//else the volume's - so the volume's is what says there is a mask at all, and a bake of a scene nothing of which was unwrapped has that one alone.
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

		//A mask is no use without a picture to read it from, and its light is not in the bake: drawn as any other light, shadow maps and all.
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
		}
	}

	//Everything off the card, on device teardown.
	internal static void Release()
	{
		foreach ( var on in held.Values ) on.Release();
		held.Clear();
	}

	//Runs a bake of the component's scene with its settings. The scene is read on the main thread; everything after is
	//traced on every other core, and what it made comes back here. Null where it was cancelled or there was nothing to bake.
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
			}

			volume.Lay( world.Bounds, spacing );

			if ( !volume.TraceOnCard( world, volumeSamples, progress, stop ) )
				volume.Trace( world, volumeSamples, progress, stop );
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
			VolumeRed = volume.Red,
			VolumeGreen = volume.Green,
			VolumeBlue = volume.Blue,
			VolumeMask = volume.Mask ?? [],
		};

		foreach ( var light in world.Lights )
		{
			if ( light.Whole || light.Mask >= 0 )
				data.Lights.Add( new BakedLightingData.BakedLight { Light = light.Id, Channel = light.Whole ? -1 : light.Mask } );
		}

		if ( sheet is not null )
		{
			data.SheetWidth = sheet.Width;
			data.SheetHeight = sheet.Height;
			data.Sheet = sheet.Light;
			data.Ways = sheet.Ways;
			data.SheetMask = sheet.Mask ?? [];

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
			$"{volume.CountX}x{volume.CountY}x{volume.CountZ} volume points {volume.Step:0.##} m apart, " +
			$"{( DateTime.UtcNow - started ).TotalSeconds:0.0} s" );

		return data;
	}
}
