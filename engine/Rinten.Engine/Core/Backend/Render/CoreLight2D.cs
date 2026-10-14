using System.Runtime.InteropServices;

namespace Rinten;

//The 2D lighting, worked out ahead of the world for an orthographic view whose scene is lit in 2D. What glows and what
//blocks is drawn (L2D.Scene: glow rgb, coverage a); a jump flood finds every texel's nearest blocker; radiance cascades
//march the global light and what glows past the blockers; and the 2D lights are added straight, each faded to its range
//and shadowed by a march to it - all into L2D.Light, which sprites and tiles lit in 2D multiply by. All the screen grown
//by the margin on every side, so what is just out of shot still lights and shadows into it. Full frame only. See
//light2d.slang, light2d_sprites.slang and light2d_cascades.slang
internal static class CoreLight2D
{
	//One view's pictures, by name. Every view has its own: two views in one frame - the editor's and the game's - sharing
	//one name had the second remake the first's pictures at its own size while the first's sprites still read them.
	//The jump flood's two and the cascades' two are each written while the other is read
	private sealed record Names( string Scene, string Light, string SeedsA, string SeedsB, string CascadeA, string CascadeB )
	{
		public static Names For( int view ) => new( $"L2D.{view}.Scene", $"L2D.{view}.Light",
			$"L2D.{view}.SeedsA", $"L2D.{view}.SeedsB", $"L2D.{view}.CascadeA", $"L2D.{view}.CascadeB" );
	}

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CameraComponent, Names> namesOf = new();
	private static int viewsNamed;

	//The first cascade's stretch, in the scene's pixels; each one past it is four times the one before
	private const float Interval0 = 1.5f;

	//How much this view's screen was shrunk inside the pictures, what it asked to be shown and its names - see DrawDebug
	private static float drawnShrink;
	private static Lighting2D.DebugView drawnDebug;
	private static Names drawnNames;

	private static readonly Dictionary<(bool Tile, int Format), ulong> spritePipelines = new();
	private static readonly Dictionary<CoreRender.TargetKind, ulong> debugPipelines = new();
	private static readonly Dictionary<string, ulong> solvePipelines = new();

	private static readonly HashSet<string> said = new();

	//Works out this view's light and lays it into the block, re-sent; false where the view has no 2D lighting, its
	//lanes nought. Before the world's picture opens - nothing else may be open as the target
	internal static bool Draw( Scene scene, CameraComponent camera, float[] frame, int width, int height )
	{
		drawnShrink = 0.0f;
		drawnDebug = Lighting2D.DebugView.None;
		drawnNames = null;

		Lay( frame, 0.0f, 0, 0 );

		if ( CoreRenderDevice.PortableFrame || camera is not { Orthographic: true } ) return false;

		var system = scene?.GetSystem<SceneLight2DSystem>();
		if ( system is not { Active: true } ) return false;

		var settings = system.Settings;
		var scale = Math.Clamp( settings.ResolutionScale, 0.25f, 1.0f );
		var grow = 1.0f + ( 2.0f * Math.Clamp( settings.Margin, 0.0f, 0.5f ) );

		var wide = Math.Max( (int)( width * scale * grow ), 1 );
		var tall = Math.Max( (int)( height * scale * grow ), 1 );

		var names = namesOf.GetValue( camera, _ => Names.For( ++viewsNamed ) );

		var glow = CorePasses.Acquire( names.Scene, CoreRender.PictureKind.Hdr, wide, tall, 1 );
		var light = CorePasses.Acquire( names.Light, CoreRender.PictureKind.Hdr, wide, tall, 1 );

		if ( glow is null || light is null ) return false;

		var shrink = 1.0f / grow;

		//Laid before the draws: the sprites' vertex stage shrinks the screen by it, the passes read the lights through
		//the view with it, and the world's sprites read the light after
		Lay( frame, shrink, glow.Index, light.Index );
		CoreRenderDevice.SetupFrame( frame );

		CorePasses.Close();

		CoreRenderDevice.ZoneBegin( "light 2d scene" );

		if ( CoreRenderDevice.BeginPicture( glow.Picture, 0, Color.Transparent, clearFirst: true ) )
		{
			DrawSprites( glow.Format );
			CoreRenderDevice.EndTarget();
		}

		glow.Cleared = true;

		CoreRenderDevice.ZoneEnd();

		CoreRenderDevice.ZoneBegin( "light 2d solve" );

		//Where the passes cannot run, the light is not laid: sprites fall back to the global light alone
		if ( !Solve( names, glow, light, system.GlobalLight, system.Glows, system.Lights.Count > 0, wide, tall ) )
		{
			if ( said.Add( "solve" ) ) Log.Warning( $"the 2D light's passes did not run: {CoreRenderDevice.ShaderError}" );

			Lay( frame, shrink, glow.Index, 0 );
			CoreRenderDevice.SetupFrame( frame );
		}

		CoreRenderDevice.ZoneEnd();

		drawnShrink = shrink;
		drawnDebug = settings.Debug;
		drawnNames = names;

		return true;
	}

	//The block's lanes for the pictures - see light2dScene in frame_data.slang; z is spare
	internal static void Lay( float[] frame, float shrink, int scene, int light )
	{
		if ( frame is null || frame.Length < CoreSceneRenderer.FrameFloats ) return;

		frame[CoreSceneRenderer.Light2DSceneAt + 0] = shrink;
		frame[CoreSceneRenderer.Light2DSceneAt + 1] = scene;
		frame[CoreSceneRenderer.Light2DSceneAt + 2] = 0.0f;
		frame[CoreSceneRenderer.Light2DSceneAt + 3] = light;
	}

	//The scene marched into the light: the jump flood, the cascades from the coarsest down where anything glows, then
	//the finest averaged with the 2D lights added
	private static bool Solve( Names names, CorePasses.Named scene, CorePasses.Named light, Color sky, bool glows, bool lights, int wide, int tall )
	{
		var seed = SolvePipeline( "SeedCs" );
		var jump = SolvePipeline( "JumpCs" );
		var cascade = SolvePipeline( "CascadeCs" );
		var integrate = SolvePipeline( "IntegrateCs" );

		if ( seed == 0 || jump == 0 || cascade == 0 || integrate == 0 ) return false;

		Span<float> push = stackalloc float[16];

		push[0] = wide;
		push[1] = tall;
		push[4] = scene.Index;
		push[8] = sky.r;
		push[9] = sky.g;
		push[10] = sky.b;
		push[11] = Interval0;

		//Only the global light: flat, nothing to march
		if ( !glows && !lights ) return Run( integrate, light, push, wide, tall );

		var seedsA = CorePasses.Acquire( names.SeedsA, CoreRender.PictureKind.Full, wide, tall, 1 );
		var seedsB = CorePasses.Acquire( names.SeedsB, CoreRender.PictureKind.Full, wide, tall, 1 );

		if ( seedsA is null || seedsB is null ) return false;

		CorePasses.Close();

		//The flood: every blocker its own seed, then strides halving down to one. From no further than FloodStride, which
		//is all a ray needs to know: a texel it gives no blocker is that far from one at least, and strides that far
		var stride = Math.Min( HighestBit( Math.Max( wide, tall ) ), FloodStride );

		push[12] = stride;

		if ( !Run( seed, seedsA, push, wide, tall ) ) return false;

		var (read, write) = ( seedsA, seedsB );

		for ( ; stride >= 1; stride /= 2 )
		{
			push[2] = stride;
			push[7] = read.Index;

			if ( !Run( jump, write, push, wide, tall ) ) return false;

			( read, write ) = ( write, read );
		}

		push[6] = read.Index;

		//The cascades carry only what glows - the lights are added straight and the global light is flat - so where
		//nothing glows they are not run at all, and are half the cost of the whole
		if ( glows )
		{
			var count = CascadeCount( wide, tall );
			var (cascadeWide, cascadeTall) = CascadeSize( wide, tall, count );

			var cascadeA = CorePasses.Acquire( names.CascadeA, CoreRender.PictureKind.Hdr, cascadeWide, cascadeTall, 1 );
			var cascadeB = CorePasses.Acquire( names.CascadeB, CoreRender.PictureKind.Hdr, cascadeWide, cascadeTall, 1 );

			if ( cascadeA is null || cascadeB is null ) return false;

			CorePasses.Close();

			//The coarsest first, each merging the one above it out of the other picture
			var (above, into) = ( cascadeA, cascadeB );

			push[3] = count;

			for ( var n = count - 1; n >= 0; n-- )
			{
				push[2] = n;
				push[7] = n == count - 1 ? 0 : above.Index;

				var probesWide = ( wide + ( 1 << n ) - 1 ) >> n;
				var probesTall = ( tall + ( 1 << n ) - 1 ) >> n;

				if ( !Run( cascade, into, push, probesWide * ( 2 << n ), probesTall * ( 2 << n ) ) ) return false;

				( above, into ) = ( into, above );
			}

			push[7] = above.Index;
			push[13] = 1.0f;
		}

		return Run( integrate, light, push, wide, tall );
	}

	//How far the flood reaches, in the scene's pixels: past it a ray strides this far at a time through empty space.
	//Further cost a pass a doubling over the whole picture for strides nothing near a blocker could take anyway
	private const int FloodStride = 64;

	//Enough cascades that the last one's stretch reaches across the whole picture
	private static int CascadeCount( int wide, int tall )
	{
		var diagonal = MathF.Sqrt( ( wide * (float)wide ) + ( tall * (float)tall ) );
		var count = 1;

		while ( count < 10 && Interval0 * ( ( 1 << ( 2 * count ) ) - 1 ) / 3.0f < diagonal ) count++;

		return count;
	}

	//A cascade's probes times the side of each one's block of rays, the widest of them all
	private static (int Wide, int Tall) CascadeSize( int wide, int tall, int count )
	{
		var cascadeWide = 0;
		var cascadeTall = 0;

		for ( var n = 0; n < count; n++ )
		{
			cascadeWide = Math.Max( cascadeWide, ( ( wide + ( 1 << n ) - 1 ) >> n ) * ( 2 << n ) );
			cascadeTall = Math.Max( cascadeTall, ( ( tall + ( 1 << n ) - 1 ) >> n ) * ( 2 << n ) );
		}

		return (cascadeWide, cascadeTall);
	}

	//The largest power of two no greater than this - the flood's first stride, at least half the picture's side
	private static int HighestBit( int value )
	{
		var bit = 1;
		while ( bit * 2 <= value ) bit *= 2;
		return bit;
	}

	//One pass storing into a picture, over enough groups of eight by eight to cover this many texels
	private static bool Run( ulong pipeline, CorePasses.Named into, ReadOnlySpan<float> push, int wide, int tall )
	{
		Span<ulong> outputs = [into.Picture];
		Span<int> levels = [0];

		return CoreRenderDevice.DispatchPass( pipeline, CoreSceneDrawer.WhiteTexture, outputs, levels,
			ReadOnlySpan<ulong>.Empty, MemoryMarshal.AsBytes( push ), ( wide + 7 ) / 8, ( tall + 7 ) / 8 );
	}

	//The frame's sprite batches again, into the scene: only what blocks or glows writes - see light2d_sprites.slang
	private static void DrawSprites( int format )
	{
		var draws = CoreImmediate.Instanced;
		int sprites = 0, tiles = 0, others = 0, failed = 0;

		for ( var i = 0; i < draws.Length; i++ )
		{
			var draw = draws[i];

			if ( !CoreResources.IsSpriteShader( draw.Kind.Shader ) )
			{
				others++;
				if ( Lighting2DLog ) notSprites.Add( draw.Kind.Shader ?? "(none)" );
				continue;
			}

			var tile = IsTiles( draw.Kind.Shader );

			var pipeline = SpritePipeline( tile, format );
			if ( pipeline == 0 ) continue;

			if ( CoreRenderDevice.DrawObjectsIndexed( pipeline, draw.IndexBuffer,
				draw.IndexCount, draw.InstanceCount, draw.WideIndices,
				MemoryMarshal.AsBytes( draw.Push.AsSpan() ),
				0, draw.Buffers.AsSpan( 0, draw.BufferCount ) ) )
			{
				if ( tile ) tiles++; else sprites++;
			}
			else failed++;
		}

		//Said once a few seconds, while the scene has 2D lights: what reached the scene picture, so a scene with no shadows
		//says whether its occluders were never drawn into it - see Lighting2D.Debug's Scene view for what was
		if ( Lighting2DLog && sinceLogged > 3.0f )
		{
			sinceLogged = 0;
			Log.Info( $"2D light scene: {sprites} sprite and {tiles} tile draws replayed, {failed} failed, {others} not sprites " +
				$"({CoreImmediate.Instanced.Length} instanced, {CoreImmediate.Blocks.Length} portable runs queued)" +
				( notSprites.Count > 0 ? $" - not sprites: {string.Join( ", ", notSprites )}" : "" ) );
		}

		notSprites.Clear();
	}

	[ConVar( "r_light2d_log", Help = "Says once a few seconds what the 2D lighting drew into its scene." )]
	internal static bool Lighting2DLog { get; set; }

	private static RealTimeSince sinceLogged;
	private static readonly HashSet<string> notSprites = new();

	//Tile maps step their animation in the pixel stage, so they are redrawn with the tile variant
	private static bool IsTiles( string shader )
		=> string.Equals( System.IO.Path.GetFileName( shader ), System.IO.Path.GetFileName( Tiles.TileSet.TileShader ),
			StringComparison.OrdinalIgnoreCase );

	//What Lighting2D.Debug asked for, over the world's picture with its margin cut off; after the world's sprites
	internal static void DrawDebug( CoreRender.TargetKind target )
	{
		if ( drawnDebug == Lighting2D.DebugView.None || drawnShrink <= 0.0f || drawnNames is null ) return;

		var picture = CorePasses.Find( drawnDebug == Lighting2D.DebugView.Light ? drawnNames.Light : drawnNames.Scene );
		if ( picture is not { Picture: not 0 } ) return;

		var pipeline = DebugPipeline( target );
		if ( pipeline == 0 ) return;

		Span<float> push = [0.0f, drawnShrink, (float)drawnDebug, 0.0f];

		//Bound as the draw's own picture rather than read out of the table, which a plain draw does not bind
		CoreRenderDevice.Draw( pipeline, 3, MemoryMarshal.AsBytes( push ), CoreRenderDevice.PictureTexture( picture.Picture ) );
	}

	private static ulong SpritePipeline( bool tiles, int format )
	{
		if ( spritePipelines.TryGetValue( (tiles, format), out var made ) ) return made;

		var defines = tiles ? EngineShaders.Light2DTiles : null;
		var vertex = CoreShaderCache.Pass( EngineShaders.Light2DSprites, "MainVs", CoreShaderCompiler.Stage.Vertex, defines );
		var pixel = CoreShaderCache.Pass( EngineShaders.Light2DSprites, "MainPs", CoreShaderCompiler.Stage.Pixel, defines );

		made = vertex is null || pixel is null ? 0 : CoreRenderDevice.CreatePipeline( vertex, pixel, CoreRender.VertexLayoutKind.None,
			blend: true, target: CoreRender.TargetKind.Picture, pictureFormat: format );

		Warn( made, $"the 2D light's {( tiles ? "tiles" : "sprites" )}" );

		spritePipelines[(tiles, format)] = made;
		return made;
	}

	private static ulong DebugPipeline( CoreRender.TargetKind target )
	{
		if ( debugPipelines.TryGetValue( target, out var made ) ) return made;

		var vertex = CoreShaderCache.Pass( EngineShaders.Light2D, "DebugVs", CoreShaderCompiler.Stage.Vertex );
		var pixel = CoreShaderCache.Pass( EngineShaders.Light2D, "DebugPs", CoreShaderCompiler.Stage.Pixel );

		//The world's normals are left as they are: only its colour is shown over
		made = vertex is null || pixel is null ? 0 : CoreRenderDevice.CreatePipeline( vertex, pixel, CoreRender.VertexLayoutKind.None,
			target: target, colourAlone: target == CoreRender.TargetKind.Scene );

		Warn( made, "the 2D light's debug view" );

		debugPipelines[target] = made;
		return made;
	}

	private static ulong SolvePipeline( string entry )
	{
		if ( solvePipelines.TryGetValue( entry, out var made ) ) return made;

		var defines = entry is "SeedCs" or "JumpCs" ? EngineShaders.Light2DSeeds : null;
		var code = CoreShaderCache.Pass( EngineShaders.Light2DCascades, entry, CoreShaderCompiler.Stage.Compute, defines );

		made = code is null ? 0 : CoreRenderDevice.CreatePassPipeline( code );

		Warn( made, $"the 2D light's {entry}" );

		solvePipelines[entry] = made;
		return made;
	}

	private static void Warn( ulong made, string what )
	{
		if ( made == 0 && said.Add( what ) ) Log.Warning( $"{what} did not compile: {CoreRenderDevice.ShaderError}" );
	}

	//Lets go of every pipeline, with the device's or when shaders change
	internal static void ForgetPipelines()
	{
		foreach ( var pipeline in spritePipelines.Values ) if ( pipeline != 0 ) CoreRenderDevice.DestroyPipeline( pipeline );
		foreach ( var pipeline in debugPipelines.Values ) if ( pipeline != 0 ) CoreRenderDevice.DestroyPipeline( pipeline );
		foreach ( var pipeline in solvePipelines.Values ) if ( pipeline != 0 ) CoreRenderDevice.DestroyPipeline( pipeline );

		spritePipelines.Clear();
		debugPipelines.Clear();
		solvePipelines.Clear();
		said.Clear();
	}
}
