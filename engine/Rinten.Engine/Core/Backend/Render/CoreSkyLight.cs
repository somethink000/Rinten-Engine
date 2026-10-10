namespace Rinten;

//The sky's light on the card, for the full frame: the sky draws itself into an octahedral capture (its shader's
//SkyCapture stages), which is filtered into GGX reflection levels and an irradiance picture - see sky_light.slang.
//Redrawn when the sky changed, at most every RedrawEvery, a stage a frame
internal sealed class CoreSkyLight
{
	//The capture's side: about a degree and a half a texel, as fine at the zenith as at the horizon
	private const int CaptureSide = 128;

	//sky_capture.slang's triangles, one a face of the octahedron
	private const int CaptureVertices = 8 * 3;

	//Reflection levels: the capture's side down to sixteen, roughness level / ( count - 1 ); eight shows its texels
	private const int ReflectionLevels = 4;

	//The irradiance picture; irradiance holds nothing finer
	private const int IrradianceSide = 16;

	//The capture level the irradiance sums, sixteen a side; sky_light.slang's IrradianceTexels must agree
	private const int IrradianceSource = 3;

	//Seconds of real time between redraws of a sky that keeps changing: a turning sun, drifting cloud
	private const float RedrawEvery = 0.25f;

	private ulong capture, reflections, irradiance;
	private int captureIndex, reflectionsIndex, irradianceIndex;

	//Whether the pictures hold a sky yet; until then the frame reads the sky table
	private bool ready;

	//The device generation the pictures belong to; a device gone takes them with it
	private int generation;

	//What the pictures were drawn from
	private readonly float[] drawnFrom = new float[Watched.Length * 4];
	private CoreMaterial drawnCore;
	private int drawnRevision = -1, drawnValues = -1;
	private float drawnClock = float.NaN;
	private float drawnAt = float.NegativeInfinity;

	//The frame block's sky lanes a redraw follows: zenith, horizon, ground, sun, its light, the panorama
	private static readonly int[] Watched =
	[
		CoreSceneRenderer.SkyAt, CoreSceneRenderer.SkyAt + 4, CoreSceneRenderer.SkyAt + 8,
		CoreSceneRenderer.SunAt, CoreSceneRenderer.SunAt + 4, CoreSceneRenderer.SunLightAt,
		CoreSceneRenderer.PanoramaAt, CoreSceneRenderer.PanoramaLightAt,
	];

	//Capture pipelines by sky shader, and the two filter pipelines by target format - kept with the device
	private static readonly Dictionary<(string Shader, int Specialize, int Format), ulong> capturePipelines = new();
	private static readonly Dictionary<(string Entry, int Format), ulong> filterPipelines = new();

	private readonly byte[] push = new byte[128];

	/// <summary>Whether this device draws the sky's light on the card: the full frame only.</summary>
	internal static bool Available => CoreNative.Available && !CoreRenderDevice.PortableFrame;

	/// <summary>Writes where the frame reads the sky's light from, nought for the sky table, keeping the ambient factor.</summary>
	internal void Lay( float[] frame )
	{
		var at = CoreSceneRenderer.SkyLightAt;
		var on = ready && generation == CoreResources.Generation && Available && CoreSceneRenderer.HasSky( frame );

		frame[at + 0] = on ? reflectionsIndex : 0.0f;
		frame[at + 1] = on ? ReflectionLevels : 0.0f;
		frame[at + 2] = on ? irradianceIndex : 0.0f;
	}

	/// <summary>Redraws the sky's light where it changed. Between targets, before anything shades; frame is this frame's block.</summary>
	internal void Draw( float[] frame, string skyShader, ulong skyDevice, CoreMaterial skyCore )
	{
		if ( !Available || skyDevice == 0 || string.IsNullOrWhiteSpace( skyShader ) || !CoreSceneRenderer.HasSky( frame ) ) return;

		//A redraw already started carries on whatever the sky does now; a new one starts only when it changed
		if ( step == Step.Capture && !Stale( frame, skyShader, skyCore ) ) return;

		if ( !EnsurePictures() )
		{
			Once( "the sky light has no pictures to draw into - the frame lights by the sky table" );
			return;
		}

		var capturePipeline = CapturePipeline( skyShader, CoreSceneRenderer.SkySpecialize( skyCore ) );
		var prefilter = FilterPipeline( "MainPrefilterPs", reflections );
		var gather = FilterPipeline( "MainIrradiancePs", irradiance );

		if ( capturePipeline == 0 || prefilter == 0 || gather == 0 )
		{
			Once( $"the sky light is not drawn for {skyShader} (capture {capturePipeline != 0}, filters {prefilter != 0 && gather != 0})" );
			return;
		}

		CoreRenderDevice.ZoneBegin( "sky light" );

		//The first redraw is whole, so the frame has a sky light at once; after that a stage a frame, so no frame pays for all three
		if ( !ready )
		{
			DrawCapture( capturePipeline, skyDevice );
			DrawReflections( prefilter );
			DrawIrradiance( gather );

			Remember( frame, skyCore );
			ready = true;
		}
		else if ( step == Step.Capture )
		{
			DrawCapture( capturePipeline, skyDevice );
			Remember( frame, skyCore );
			step = Step.Reflections;
		}
		else if ( step == Step.Reflections )
		{
			DrawReflections( prefilter );
			step = Step.Irradiance;
		}
		else
		{
			DrawIrradiance( gather );
			step = Step.Capture;
		}

		CoreRenderDevice.ZoneEnd();
	}

	//Which stage of a redraw the next frame draws
	private enum Step { Capture, Reflections, Irradiance }

	private Step step;

	//The sky drawn by its own shader with its material's numbers, then the chain the filters read
	private void DrawCapture( ulong pipeline, ulong skyDevice )
	{
		CoreRenderDevice.ZoneBegin( "sky capture" );

		if ( CoreRenderDevice.BeginPicture( capture, 0 ) )
		{
			CoreRenderDevice.Draw( pipeline, CaptureVertices, System.Array.Empty<byte>(), skyDevice );
			CoreRenderDevice.EndTarget();
		}

		CoreRenderDevice.PictureChain( capture );
		CoreRenderDevice.ZoneEnd();
	}

	private void DrawReflections( ulong pipeline )
	{
		CoreRenderDevice.ZoneBegin( "sky reflections" );

		for ( var level = 0; level < ReflectionLevels; level++ )
		{
			Push( level / (float)( ReflectionLevels - 1 ), CaptureSide >> level, 0 );

			if ( !CoreRenderDevice.BeginPicture( reflections, level ) ) continue;

			CoreRenderDevice.Draw( pipeline, 3, push );
			CoreRenderDevice.EndTarget();
		}

		CoreRenderDevice.ZoneEnd();
	}

	private void DrawIrradiance( ulong pipeline )
	{
		CoreRenderDevice.ZoneBegin( "sky irradiance" );
		Push( 0.0f, IrradianceSide, IrradianceSource );

		if ( CoreRenderDevice.BeginPicture( irradiance, 0 ) )
		{
			CoreRenderDevice.Draw( pipeline, 3, push );
			CoreRenderDevice.EndTarget();
		}

		CoreRenderDevice.ZoneEnd();
	}

	private static readonly HashSet<string> said = new();

	//A reason the sky light was not drawn, said once
	private static void Once( string why )
	{
		lock ( said ) if ( said.Add( why ) ) Log.Warning( why );
	}

	//Whether the pictures no longer show the sky the frame describes: another sky at once, any other change
	//no sooner than RedrawEvery after the last, so a sky that changes every frame costs as one that changes at that rate
	private bool Stale( float[] frame, string skyShader, CoreMaterial skyCore )
	{
		if ( !ready || !ReferenceEquals( skyCore, drawnCore ) ) return true;

		var now = frame[CoreSceneRenderer.TimeAt + 0];
		if ( now >= drawnAt && now - drawnAt < RedrawEvery ) return false;

		if ( skyCore is not null && ( skyCore.Revision != drawnRevision || skyCore.ValuesRevision != drawnValues ) ) return true;

		for ( var i = 0; i < Watched.Length; i++ )
		{
			for ( var c = 0; c < 4; c++ )
			{
				if ( MathF.Abs( frame[Watched[i] + c] - drawnFrom[( i * 4 ) + c] ) > 1e-4f ) return true;
			}
		}

		return frame[CoreSceneRenderer.TimeAt + 2] != drawnClock && Drifts( skyShader, skyCore );
	}

	//Whether the sky changes with the scene's clock alone: the engine's sky only under cloud with a wind, which is
	//all of it that moves in the capture; a panorama never; any other sky, not known, is taken to
	private static bool Drifts( string skyShader, CoreMaterial skyCore )
	{
		if ( CoreResources.IsPanoramaShader( skyShader ) ) return false;
		if ( !string.Equals( skyShader, CoreSceneRenderer.SkyShader, StringComparison.OrdinalIgnoreCase ) ) return true;

		var wind = CoreResources.Vector( skyCore, "g_vCloudWind", default );
		return CoreResources.Number( skyCore, "g_flCloudCover", 0.0f ) > 0.0f && ( wind.x != 0.0f || wind.y != 0.0f );
	}

	private void Remember( float[] frame, CoreMaterial skyCore )
	{
		for ( var i = 0; i < Watched.Length; i++ )
		{
			for ( var c = 0; c < 4; c++ ) drawnFrom[( i * 4 ) + c] = frame[Watched[i] + c];
		}

		drawnCore = skyCore;
		drawnRevision = skyCore?.Revision ?? -1;
		drawnValues = skyCore?.ValuesRevision ?? -1;
		drawnClock = frame[CoreSceneRenderer.TimeAt + 2];
		drawnAt = frame[CoreSceneRenderer.TimeAt + 0];
	}

	//The prefilter and gather numbers: the capture's index and side, the roughness, the target's side and source level
	private void Push( float roughness, int side, int sourceLevel )
	{
		System.Array.Clear( push );

		var numbers = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( push.AsSpan() );

		numbers[0] = captureIndex;
		numbers[1] = CaptureSide;
		numbers[3] = roughness;
		numbers[4] = side;
		numbers[6] = sourceLevel;
	}

	private bool EnsurePictures()
	{
		//The old device's pictures went with it; nothing to give back
		if ( generation != CoreResources.Generation )
		{
			capture = reflections = irradiance = 0;
			ready = false;
			step = Step.Capture;
			generation = CoreResources.Generation;
		}

		if ( capture != 0 && reflections != 0 && irradiance != 0 ) return true;

		Release();

		capture = CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.Hdr, CaptureSide, CaptureSide, levels: 32 );
		//What every surface reads, so in half the bytes; the capture they are filtered from keeps half floats
		reflections = CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.HdrCompact, CaptureSide, CaptureSide, levels: ReflectionLevels );
		irradiance = CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.HdrCompact, IrradianceSide, IrradianceSide );

		if ( capture == 0 || reflections == 0 || irradiance == 0 )
		{
			Release();
			return false;
		}

		captureIndex = CoreSceneDrawer.Indexed( CoreRenderDevice.PictureTexture( capture ) );
		reflectionsIndex = CoreSceneDrawer.Indexed( CoreRenderDevice.PictureTexture( reflections ) );
		irradianceIndex = CoreSceneDrawer.Indexed( CoreRenderDevice.PictureTexture( irradiance ) );

		return captureIndex != 0 && reflectionsIndex != 0 && irradianceIndex != 0;
	}

	/// <summary>Gives the pictures back; the next draw makes them again.</summary>
	internal void Release()
	{
		if ( capture != 0 ) CoreRenderDevice.ReleasePicture( capture );
		if ( reflections != 0 ) CoreRenderDevice.ReleasePicture( reflections );
		if ( irradiance != 0 ) CoreRenderDevice.ReleasePicture( irradiance );

		capture = reflections = irradiance = 0;
		captureIndex = reflectionsIndex = irradianceIndex = 0;
		ready = false;
		step = Step.Capture;
	}

	private static int FormatOf( ulong picture )
		=> CoreRenderDevice.PictureShape( picture, out _, out _, out _, out var format ) ? format : -1;

	//A sky shader's capture stages, against the capture's format and the frame's sky features
	private ulong CapturePipeline( string shader, int specialize )
	{
		var key = (shader, specialize, FormatOf( capture ));
		if ( capturePipelines.TryGetValue( key, out var made ) ) return made;

		var stages = CoreResources.Shader( shader );
		var vertex = stages?[(int)CoreShaderFile.Stage.SkyCaptureVertex];
		var pixel = stages?[(int)CoreShaderFile.Stage.SkyCapturePixel];

		//Not kept: a shader still loading answers this way too
		if ( vertex is null || pixel is null ) return 0;

		made = CoreRenderDevice.CreatePipeline( vertex, pixel, CoreRender.VertexLayoutKind.None,
			target: CoreRender.TargetKind.Picture, pictureFormat: key.Item3, specialize: specialize );

		if ( made == 0 ) Log.Warning( $"the sky's capture did not compile for {shader}: {CoreRenderDevice.ShaderError}" );

		capturePipelines[key] = made;
		return made;
	}

	private static ulong FilterPipeline( string entry, ulong target )
	{
		var key = (entry, FormatOf( target ));
		if ( filterPipelines.TryGetValue( key, out var made ) ) return made;

		var vertex = CoreShaderCache.Pass( EngineShaders.SkyLight, "MainVs", CoreShaderCompiler.Stage.Vertex );
		var pixel = CoreShaderCache.Pass( EngineShaders.SkyLight, entry, CoreShaderCompiler.Stage.Pixel );

		made = vertex is null || pixel is null ? 0 : CoreRenderDevice.CreatePipeline( vertex, pixel, CoreRender.VertexLayoutKind.None,
			target: CoreRender.TargetKind.Picture, pictureFormat: key.Item2 );

		if ( made == 0 ) Log.Warning( $"the sky light's {entry} did not compile: {CoreRenderDevice.ShaderError}" );

		filterPipelines[key] = made;
		return made;
	}

	/// <summary>Lets go of every pipeline, with the device's or when shaders change.</summary>
	internal static void ForgetPipelines()
	{
		foreach ( var pipeline in capturePipelines.Values ) if ( pipeline != 0 ) CoreRenderDevice.DestroyPipeline( pipeline );
		foreach ( var pipeline in filterPipelines.Values ) if ( pipeline != 0 ) CoreRenderDevice.DestroyPipeline( pipeline );

		capturePipelines.Clear();
		filterPipelines.Clear();
	}
}
