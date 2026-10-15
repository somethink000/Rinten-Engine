namespace Rinten;

//Bloom for the portable frame, which has no world picture and no compute: the world is drawn aside into a picture of the
//screen's format, kept past white, then halved and carried back up in pixel passes and composed - see engine/glow.slang
internal sealed class CorePortableGlow
{
	/// <summary>What the camera's Bloom asks for, weighed across its volumes.</summary>
	internal readonly record struct Settings( float Threshold, Vector3 Scale, float Falloff, float Mode );

	//The curve FinishToScreen is told to keep light past white with - see frame.slang
	internal const float KeepCurve = -1.0f;

	//Levels under the half-size one, until the widest is about fifteen texels across, as bloom.shader's are
	private const int MostLevels = 7;

	private static ulong brightPipeline, downPipeline, upPipeline, composePipeline;
	private static int pipelineFormat;
	private static bool failed;

	//The halvings, and what each is carried back up into; up[0] is the glow
	private readonly ulong[] down = new ulong[MostLevels];
	private readonly ulong[] up = new ulong[MostLevels];
	private readonly (int Width, int Height)[] sizes = new (int, int)[MostLevels];
	private int levels;
	private int width, height;

	//Each up pass's two pictures, and the compose's: the world and the glow
	private readonly ulong[] upInputs = new ulong[MostLevels];
	private ulong composeInputs;
	private ulong composedFrom;

	private ulong world;
	private Settings settings;
	private float exposure;

	private readonly byte[] push = new byte[64];

	/// <summary>Sends the world aside for this frame's glow; false where it cannot, and the frame goes as ever.</summary>
	internal bool Open( Settings wanted, float frameExposure )
	{
		if ( failed ) return false;

		//The picture is the screen's size, which the levels follow
		world = CoreRenderDevice.WorldAside( true, out var screenWidth, out var screenHeight );
		if ( world == 0 ) return false;

		if ( screenWidth < 2 || screenHeight < 2 || !Fit( screenWidth, screenHeight ) || !EnsurePipelines() )
		{
			CoreRenderDevice.WorldAside( false, out _, out _ );
			return false;
		}

		if ( composedFrom != world )
		{
			if ( composeInputs != 0 ) CoreRenderDevice.DestroyTexture( composeInputs );

			var glow = Texture( levels > 1 ? up[0] : down[0] );
			composeInputs = CoreRenderDevice.CreateMaterial( world, glow, world, world, 1, 0, 0, 0 );
			composedFrom = composeInputs != 0 ? world : 0;
		}

		if ( composeInputs == 0 )
		{
			CoreRenderDevice.WorldAside( false, out _, out _ );
			return false;
		}

		settings = wanted;
		exposure = frameExposure;
		return true;
	}

	/// <summary>The world's pass back on the screen, the glow made from what it drew, and the two composed.</summary>
	internal void Finish( float curve )
	{
		CoreRenderDevice.WorldAside( false, out _, out _ );
		CoreRenderDevice.ZoneBegin( "glow" );

		var (fullWidth, fullHeight) = ( width, height );

		Pass( brightPipeline, down[0], world, sizes[0], fullWidth, fullHeight );

		for ( var level = 1; level < levels; level++ )
			Pass( downPipeline, down[level], Texture( down[level - 1] ), sizes[level], sizes[level - 1].Width, sizes[level - 1].Height );

		for ( var level = levels - 2; level >= 0; level-- )
			Pass( upPipeline, up[level], upInputs[level], sizes[level], sizes[level + 1].Width, sizes[level + 1].Height );

		//Onto the screen, whose pass the next draw opens again; the glow's texels are what its reads are held inside
		var glow = sizes[0];
		var total = 0.0f;

		for ( var level = 0; level < levels; level++ ) total += MathF.Pow( settings.Falloff, level );

		Write( 1.0f / fullWidth, 1.0f / fullHeight, 1.0f / glow.Width, 1.0f / glow.Height,
			settings.Threshold, settings.Falloff, settings.Mode, 1.0f / Math.Max( total, 1e-4f ), curve );

		CoreRenderDevice.Draw( composePipeline, 3, push, composeInputs );
		CoreRenderDevice.ZoneEnd();
	}

	//One level drawn from what it reads
	private void Pass( ulong pipeline, ulong into, ulong reads, (int Width, int Height) size, int readWidth, int readHeight )
	{
		if ( !CoreRenderDevice.BeginPicture( into, 0, default, discard: true ) ) return;

		Write( 1.0f / size.Width, 1.0f / size.Height, 1.0f / readWidth, 1.0f / readHeight,
			settings.Threshold, settings.Falloff, settings.Mode, 0.0f, 0.0f );

		CoreRenderDevice.Draw( pipeline, 3, push, reads );
		CoreRenderDevice.EndTarget();
	}

	private void Write( float x, float y, float readX, float readY, float threshold, float falloff, float mode, float share, float curve )
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
		floats[14] = 0.0f;
		floats[15] = 0.0f;
	}

	private static ulong Texture( ulong picture ) => CoreRenderDevice.PictureTexture( picture );

	//The levels for a screen of this size, made again only when it changes
	private bool Fit( int screenWidth, int screenHeight )
	{
		if ( screenWidth == width && screenHeight == height && levels > 0 ) return true;

		Release();

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
				Release();
				return false;
			}
		}

		//What the device made them in, which their passes' pipelines are baked for
		if ( CoreRenderDevice.PictureShape( down[0], out _, out _, out _, out var format ) && format != pipelineFormat )
		{
			brightPipeline = downPipeline = upPipeline = composePipeline = 0;
			pipelineFormat = format;
		}

		levels = wanted;

		for ( var level = 0; level < levels - 1; level++ )
		{
			upInputs[level] = CoreRenderDevice.CreateMaterial( Texture( down[level] ), Texture( up[level + 1] ),
				Texture( down[level] ), Texture( down[level] ), 1, 0, 0, 0 );

			if ( upInputs[level] == 0 )
			{
				Release();
				return false;
			}
		}

		width = screenWidth;
		height = screenHeight;
		return true;
	}

	private static bool EnsurePipelines()
	{
		if ( composePipeline != 0 ) return true;

		var vs = CoreShaderCache.Pass( EngineShaders.Glow, "MainVs", CoreShaderCompiler.Stage.Vertex );
		var bright = CoreShaderCache.Pass( EngineShaders.Glow, "BrightPs", CoreShaderCompiler.Stage.Pixel );
		var halve = CoreShaderCache.Pass( EngineShaders.Glow, "DownPs", CoreShaderCompiler.Stage.Pixel );
		var carry = CoreShaderCache.Pass( EngineShaders.Glow, "UpPs", CoreShaderCompiler.Stage.Pixel );
		var compose = CoreShaderCache.Pass( EngineShaders.Glow, "ComposePs", CoreShaderCompiler.Stage.Pixel );

		if ( vs is null || bright is null || halve is null || carry is null || compose is null )
		{
			failed = true;
			Log.Warning( $"the portable glow did not compile: {CoreRenderDevice.ShaderError}" );
			return false;
		}

		brightPipeline = Picture( vs, bright );
		downPipeline = Picture( vs, halve );
		upPipeline = Picture( vs, carry );

		composePipeline = CoreRenderDevice.CreatePipeline( vs, compose, CoreRender.VertexLayoutKind.None,
			depthTest: false, CoreRender.Topology.Triangles, blend: false, depthWrite: false,
			target: CoreRender.TargetKind.Screen );

		if ( brightPipeline == 0 || downPipeline == 0 || upPipeline == 0 || composePipeline == 0 )
		{
			failed = true;
			Log.Warning( $"the portable glow's passes would not be made: {CoreRenderDevice.Description}" );
			return false;
		}

		return true;
	}

	//For the levels' format, which Fit has already made one of
	private static ulong Picture( byte[] vs, byte[] ps )
		=> CoreRenderDevice.CreatePipeline( vs, ps, CoreRender.VertexLayoutKind.None,
			depthTest: false, CoreRender.Topology.Triangles, blend: false, depthWrite: false,
			target: CoreRender.TargetKind.Picture, pictureFormat: pipelineFormat );

	/// <summary>Drops the pipelines with the device that made them.</summary>
	internal static void ForgetPipelines()
	{
		brightPipeline = downPipeline = upPipeline = composePipeline = 0;
		pipelineFormat = 0;
		failed = false;
	}

	/// <summary>Gives the levels back and drops the materials naming them.</summary>
	internal void Release()
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
}
