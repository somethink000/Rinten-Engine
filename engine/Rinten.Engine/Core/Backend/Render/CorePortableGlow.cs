namespace Rinten;

//Bloom and passes for the portable frame, which has no world picture and no compute: the world is drawn aside into a picture
//of the screen's format, kept past white; passes over it alternate between two pictures of light (see CorePostBlit), the
//glow is halved and carried back up in pixel passes, and the result is finished onto the screen - see engine/glow.slang
internal sealed class CorePortableGlow
{
	/// <summary>What the camera's Bloom asks for, weighed across its volumes.</summary>
	internal readonly record struct Settings( float Threshold, Vector3 Scale, float Falloff, float Mode );

	//The curve FinishToScreen is told to keep light past white with - see frame.slang
	internal const float KeepCurve = -1.0f;

	//Levels under the half-size one, until the widest is about fifteen texels across, as bloom.shader's are
	private const int MostLevels = 7;

	private const string ComposeEntry = "ComposePs";
	private const string FinishEntry = "FinishPs";

	private static ulong brightPipeline, downPipeline, upPipeline, composePipeline, finishPipeline;
	private static int pipelineFormat;
	private static bool failed;

	//The compose or the finish carrying a run of point effects, by entry and the run's key - see ChainPipeline
	private static readonly Dictionary<(string Entry, string Key), ulong> chainPipelines = new();
	private static byte[] composeVertex;

	//The halvings, and what each is carried back up into; up[0] is the glow
	private readonly ulong[] down = new ulong[MostLevels];
	private readonly ulong[] up = new ulong[MostLevels];
	private readonly (int Width, int Height)[] sizes = new (int, int)[MostLevels];
	private int levels;
	private int width, height;

	//Each up pass's two pictures, and the compose's: what it finishes and the glow
	private readonly ulong[] upInputs = new ulong[MostLevels];
	private ulong composeInputs;
	private ulong composedFrom;

	//The two pictures of light the passes over the world alternate between, at the screen's size
	private readonly ulong[] passPictures = new ulong[2];
	private int passWidth, passHeight;

	private ulong world;
	private int worldWidth, worldHeight;
	private bool glowing;
	private Settings settings;
	private float exposure;

	private readonly byte[] push = new byte[64];

	/// <summary>The world as drawn aside this frame, kept past white - what the first pass and the finish read.</summary>
	internal ulong World => world;

	/// <summary>Whether this frame's world drawn aside has a glow made from it.</summary>
	internal bool Glows => glowing;

	/// <summary>The two pictures of light passes alternate between this frame; nought where none were asked for.</summary>
	internal (ulong First, ulong Second) Passes => ( passPictures[0], passPictures[1] );

	/// <summary>Their size, which is the screen's and the world's drawn aside.</summary>
	internal (int Width, int Height) PassSize => ( passWidth, passHeight );

	/// <summary>Sends the world aside for glow, passes, a finish-only run or soft edges; false where it cannot.</summary>
	internal bool Open( Settings? wanted, bool passes, bool carry, bool soften, float frameExposure )
	{
		softening = soften;

		if ( failed || wanted is null && !passes && !carry && !soften ) return false;

		//The picture is the screen's size, which the levels follow
		world = CoreRenderDevice.WorldAside( true, out var screenWidth, out var screenHeight );
		if ( world == 0 ) return false;

		if ( screenWidth < 2 || screenHeight < 2 || !EnsurePipelines() ||
			wanted is not null && !Fit( screenWidth, screenHeight ) ||
			passes && !FitPasses( screenWidth, screenHeight ) )
		{
			CoreRenderDevice.WorldAside( false, out _, out _ );
			world = 0;
			return false;
		}

		glowing = wanted is not null;
		settings = wanted ?? default;
		( worldWidth, worldHeight ) = ( screenWidth, screenHeight );
		exposure = frameExposure;
		return true;
	}

	//The world's pass closed, then what reads holds - the world or a pass's picture - finished onto the screen with its glow
	internal void Finish( float curve, string chain, ulong reads = 0, bool light = false, bool finished = false )
	{
		CoreRenderDevice.WorldAside( false, out _, out _ );

		if ( reads == 0 )
		{
			reads = world;
			light = false;
		}

		//A world drawn finished is read as it is and written out unchanged: the linear curve at an exposure of one
		if ( finished )
		{
			exposure = 1.0f;
			curve = (float)(int)Tonemapping.TonemappingMode.Linear;
			light = true;
		}

		var lit = light ? 1.0f : 0.0f;

		if ( !glowing )
		{
			CoreRenderDevice.ZoneBegin( "finish" );

			Write( 1.0f / Math.Max( worldWidth, 1 ), 1.0f / Math.Max( worldHeight, 1 ), 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, curve, lit );

			//A texture is a material of itself in every slot
			var finish = ChainPipeline( FinishEntry, chain );
			CoreRenderDevice.Draw( finish != 0 ? finish : finishPipeline, 3, push, reads );
			CoreRenderDevice.ZoneEnd();
			return;
		}

		CoreRenderDevice.ZoneBegin( "glow" );

		var (fullWidth, fullHeight) = ( width, height );

		Pass( brightPipeline, down[0], reads, sizes[0], fullWidth, fullHeight, lit );

		for ( var level = 1; level < levels; level++ )
			Pass( downPipeline, down[level], Texture( down[level - 1] ), sizes[level], sizes[level - 1].Width, sizes[level - 1].Height, 0.0f );

		for ( var level = levels - 2; level >= 0; level-- )
			Pass( upPipeline, up[level], upInputs[level], sizes[level], sizes[level + 1].Width, sizes[level + 1].Height, 0.0f );

		if ( composedFrom != reads )
		{
			if ( composeInputs != 0 ) CoreRenderDevice.DestroyTexture( composeInputs );

			composeInputs = CoreRenderDevice.CreateMaterial( reads, Texture( up[0] ), reads, reads, 1, 0, 0, 0 );
			composedFrom = composeInputs != 0 ? reads : 0;
		}

		//Onto the screen, whose pass the next draw opens again; the glow's texels are what its reads are held inside
		var glow = sizes[0];
		var total = 0.0f;

		for ( var level = 0; level < levels; level++ ) total += MathF.Pow( settings.Falloff, level );

		Write( 1.0f / fullWidth, 1.0f / fullHeight, 1.0f / glow.Width, 1.0f / glow.Height,
			settings.Threshold, settings.Falloff, settings.Mode, 1.0f / Math.Max( total, 1e-4f ), curve, lit );

		//The curve alone while the run's compose compiles, and for good where it would not
		var compose = ChainPipeline( ComposeEntry, chain );
		if ( composeInputs != 0 ) CoreRenderDevice.Draw( compose != 0 ? compose : composePipeline, 3, push, composeInputs );

		CoreRenderDevice.ZoneEnd();
	}

	//The compose or the finish with a run of point effects, the curve among them; nought while it compiles
	private static ulong ChainPipeline( string entry, string key )
	{
		if ( key is null || composeVertex is null ) return 0;

		if ( chainPipelines.TryGetValue( (entry, key), out var made ) ) return made;

		var pixel = CoreShaderJobs.Words( CoreShaderCache.Revision + "|glow#" + entry + "#" + key, () => ChainRequest( key, entry ), out var waiting );
		if ( pixel is null && waiting ) return 0;

		made = pixel is null ? 0 : CoreRenderDevice.CreatePipeline( composeVertex, pixel, CoreRender.VertexLayoutKind.None,
			depthTest: false, CoreRender.Topology.Triangles, blend: false, depthWrite: false,
			target: CoreRender.TargetKind.Screen );

		chainPipelines[(entry, key)] = made;
		return made;
	}

	/// <summary>The compose's or finish's pixel stage carrying the run keyed so - see CorePointChain.ScreenText.</summary>
	internal static CoreShaderJobs.Request? ChainRequest( string key, string entry )
		=> CorePointChain.ScreenText( key ) is { } chain &&
			CoreShaderCache.PassText( EngineShaders.Glow, "#define ENGINE_SCREEN_CHAIN 1\n", chain ) is { } made
			? new CoreShaderJobs.Request( made.Text, made.Directory, entry, CoreShaderCompiler.Stage.Pixel,
				CoreShaderCache.Target, EngineShaders.Glow )
			: null;

	/// <summary>Both entries that finish a frame, each carrying the run known by key - what an export bakes.</summary>
	internal static IEnumerable<CoreShaderJobs.Request> ChainRequests( string key )
	{
		foreach ( var entry in new[] { ComposeEntry, FinishEntry } )
		{
			if ( ChainRequest( key, entry ) is { } request ) yield return request;
		}
	}

	//One level drawn from what it reads
	private void Pass( ulong pipeline, ulong into, ulong reads, (int Width, int Height) size, int readWidth, int readHeight, float lit )
	{
		if ( !CoreRenderDevice.BeginPicture( into, 0, default, discard: true ) ) return;

		Write( 1.0f / size.Width, 1.0f / size.Height, 1.0f / readWidth, 1.0f / readHeight,
			settings.Threshold, settings.Falloff, settings.Mode, 0.0f, 0.0f, lit );

		CoreRenderDevice.Draw( pipeline, 3, push, reads );
		CoreRenderDevice.EndTarget();
	}

	//Whether the finish or compose runs FXAA over the world - see World in glow.slang
	private bool softening;

	private void Write( float x, float y, float readX, float readY, float threshold, float falloff, float mode, float share,
		float curve, float lit )
	{
		var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( push.AsSpan() );

		floats[0] = x;
		floats[1] = y;
		floats[2] = readX;
		floats[3] = readY;
		floats[4] = threshold;
		floats[5] = falloff;
		floats[6] = mode;
		floats[7] = share;
		floats[8] = settings.Scale.x;
		floats[9] = settings.Scale.y;
		floats[10] = settings.Scale.z;
		floats[11] = 0.0f;
		floats[12] = exposure;
		floats[13] = curve;
		floats[14] = lit;
		floats[15] = softening ? 1.0f : 0.0f;
	}

	private static ulong Texture( ulong picture ) => CoreRenderDevice.PictureTexture( picture );

	//The levels for a screen of this size, made again only when it changes
	private bool Fit( int screenWidth, int screenHeight )
	{
		if ( screenWidth == width && screenHeight == height && levels > 0 ) return true;

		ReleaseLevels();

		var half = Math.Max( screenWidth / 2, 1 );
		var wanted = Math.Clamp( (int)MathF.Floor( MathF.Log2( half / 15.0f ) ) + 1, 1, MostLevels );

		for ( var level = 0; level < wanted; level++ )
		{
			sizes[level] = ( Math.Max( half >> level, 1 ), Math.Max( Math.Max( screenHeight / 2, 1 ) >> level, 1 ) );

			down[level] = CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.HdrCompact, sizes[level].Width, sizes[level].Height );

			//The coarsest is carried up as it is
			up[level] = level < wanted - 1
				? CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.HdrCompact, sizes[level].Width, sizes[level].Height )
				: down[level];

			if ( down[level] == 0 || up[level] == 0 )
			{
				ReleaseLevels();
				return false;
			}
		}

		//What the device made them in, which their passes' pipelines are baked for
		if ( CoreRenderDevice.PictureShape( down[0], out _, out _, out _, out var format ) && format != pipelineFormat )
		{
			brightPipeline = downPipeline = upPipeline = 0;
			pipelineFormat = format;
		}

		levels = wanted;

		for ( var level = 0; level < levels - 1; level++ )
		{
			upInputs[level] = CoreRenderDevice.CreateMaterial( Texture( down[level] ), Texture( up[level + 1] ),
				Texture( down[level] ), Texture( down[level] ), 1, 0, 0, 0 );

			if ( upInputs[level] == 0 )
			{
				ReleaseLevels();
				return false;
			}
		}

		width = screenWidth;
		height = screenHeight;

		return EnsureLevelPipelines();
	}

	//The passes' two pictures at the screen's size, made again only when it changes
	private bool FitPasses( int screenWidth, int screenHeight )
	{
		if ( screenWidth == passWidth && screenHeight == passHeight && passPictures[0] != 0 ) return true;

		ReleasePasses();

		for ( var i = 0; i < passPictures.Length; i++ )
		{
			passPictures[i] = CoreRenderDevice.AcquirePicture( CoreRender.PictureKind.Hdr, screenWidth, screenHeight );

			if ( passPictures[i] == 0 )
			{
				ReleasePasses();
				return false;
			}
		}

		passWidth = screenWidth;
		passHeight = screenHeight;
		return true;
	}

	//The two that draw onto the screen; made once
	private static bool EnsurePipelines()
	{
		if ( composePipeline != 0 ) return true;

		var vs = CoreShaderCache.Pass( EngineShaders.Glow, "MainVs", CoreShaderCompiler.Stage.Vertex );
		var compose = CoreShaderCache.Pass( EngineShaders.Glow, ComposeEntry, CoreShaderCompiler.Stage.Pixel );
		var finish = CoreShaderCache.Pass( EngineShaders.Glow, FinishEntry, CoreShaderCompiler.Stage.Pixel );

		if ( vs is null || compose is null || finish is null )
		{
			failed = true;
			Log.Warning( $"the portable glow did not compile: {CoreRenderDevice.ShaderError}" );
			return false;
		}

		composePipeline = Screen( vs, compose );
		finishPipeline = Screen( vs, finish );
		composeVertex = vs;

		if ( composePipeline == 0 || finishPipeline == 0 )
		{
			failed = true;
			Log.Warning( $"the portable glow's passes would not be made: {CoreRenderDevice.Description}" );
			return false;
		}

		return true;
	}

	//The three that draw the levels, for their format; made again where it changes
	private static bool EnsureLevelPipelines()
	{
		if ( brightPipeline != 0 ) return true;

		var bright = CoreShaderCache.Pass( EngineShaders.Glow, "BrightPs", CoreShaderCompiler.Stage.Pixel );
		var halve = CoreShaderCache.Pass( EngineShaders.Glow, "DownPs", CoreShaderCompiler.Stage.Pixel );
		var carry = CoreShaderCache.Pass( EngineShaders.Glow, "UpPs", CoreShaderCompiler.Stage.Pixel );

		if ( bright is null || halve is null || carry is null )
		{
			failed = true;
			Log.Warning( $"the portable glow did not compile: {CoreRenderDevice.ShaderError}" );
			return false;
		}

		brightPipeline = Picture( composeVertex, bright );
		downPipeline = Picture( composeVertex, halve );
		upPipeline = Picture( composeVertex, carry );

		if ( brightPipeline == 0 || downPipeline == 0 || upPipeline == 0 )
		{
			failed = true;
			Log.Warning( $"the portable glow's passes would not be made: {CoreRenderDevice.Description}" );
			return false;
		}

		return true;
	}

	private static ulong Screen( byte[] vs, byte[] ps )
		=> CoreRenderDevice.CreatePipeline( vs, ps, CoreRender.VertexLayoutKind.None,
			depthTest: false, CoreRender.Topology.Triangles, blend: false, depthWrite: false,
			target: CoreRender.TargetKind.Screen );

	//For the levels' format, which Fit has already made one of
	private static ulong Picture( byte[] vs, byte[] ps )
		=> CoreRenderDevice.CreatePipeline( vs, ps, CoreRender.VertexLayoutKind.None,
			depthTest: false, CoreRender.Topology.Triangles, blend: false, depthWrite: false,
			target: CoreRender.TargetKind.Picture, pictureFormat: pipelineFormat );

	/// <summary>Drops the pipelines with the device that made them.</summary>
	internal static void ForgetPipelines()
	{
		brightPipeline = downPipeline = upPipeline = composePipeline = finishPipeline = 0;
		pipelineFormat = 0;
		failed = false;
		composeVertex = null;
		chainPipelines.Clear();
	}

	/// <summary>Gives the levels and the passes' pictures back and drops the materials naming them.</summary>
	internal void Release()
	{
		ReleaseLevels();
		ReleasePasses();
	}

	private void ReleaseLevels()
	{
		for ( var level = 0; level < MostLevels; level++ )
		{
			if ( upInputs[level] != 0 ) CoreRenderDevice.DestroyTexture( upInputs[level] );
			if ( up[level] != 0 && up[level] != down[level] ) CoreRenderDevice.ReleasePicture( up[level] );
			if ( down[level] != 0 ) CoreRenderDevice.ReleasePicture( down[level] );

			upInputs[level] = up[level] = down[level] = 0;
		}

		if ( composeInputs != 0 ) CoreRenderDevice.DestroyTexture( composeInputs );

		composeInputs = 0;
		composedFrom = 0;
		levels = 0;
		width = height = 0;
	}

	private void ReleasePasses()
	{
		//The passes' materials name these pictures and the world drawn aside, which goes with them
		if ( passPictures[0] != 0 ) CoreResources.ForgetReading();

		for ( var i = 0; i < passPictures.Length; i++ )
		{
			if ( passPictures[i] != 0 ) CoreRenderDevice.ReleasePicture( passPictures[i] );
			passPictures[i] = 0;
		}

		passWidth = passHeight = 0;
	}
}
