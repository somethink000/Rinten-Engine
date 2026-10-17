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
	private sealed record Names( string Scene, string Light, string SeedsA, string SeedsB, string CascadeA, string CascadeB, string Shadows )
	{
		//How big a texel of this view's pictures is in the world - see TexelOf
		public float Texel { get; set; }

		public static Names For( int view ) => new( $"L2D.{view}.Scene", $"L2D.{view}.Light",
			$"L2D.{view}.SeedsA", $"L2D.{view}.SeedsB", $"L2D.{view}.CascadeA", $"L2D.{view}.CascadeB", $"L2D.{view}.Shadows" );
	}

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CameraComponent, Names> namesOf = new();
	private static int viewsNamed;

	//The first cascade's stretch in its probes' spacings; each one past it is four times the one before
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

		Lay( frame, default, 0, 0, default );

		if ( CoreRenderDevice.PortableFrame || camera is not { Orthographic: true } ) return false;

		var system = scene?.GetSystem<SceneLight2DSystem>();
		if ( system is not { Active: true } ) return false;

		var settings = system.Settings;
		var scale = Math.Clamp( settings.ResolutionScale, 0.25f, 1.0f );
		var grow = 1.0f + ( 2.0f * Math.Clamp( settings.Margin, 0.0f, 0.5f ) );

		var names = namesOf.GetValue( camera, _ => Names.For( ++viewsNamed ) );

		//Screen pixels a texel of the pictures is, out of the world's size of one - or, where the view says nothing of
		//the world's scale, the screen's own pixels scaled, as before
		var across = TexelOf( names, frame, width, height, scale );
		if ( across <= 0.0f ) across = 1.0f / scale;

		var wide = Math.Clamp( (int)MathF.Ceiling( width * grow / across ) + 1, 1, MostTexels );
		var tall = Math.Clamp( (int)MathF.Ceiling( height * grow / across ) + 1, 1, MostTexels );

		//The screen inside the pictures, each way: what of them it is, so the margin is what is left
		var shrink = new System.Numerics.Vector2( width / ( wide * across ), height / ( tall * across ) );

		var glow = CorePasses.Acquire( names.Scene, CoreRender.PictureKind.Hdr, wide, tall, 1 );
		var light = CorePasses.Acquire( names.Light, CoreRender.PictureKind.Hdr, wide, tall, 1 );

		if ( glow is null || light is null ) return false;

		var snap = SnapOf( frame, shrink, wide, tall );

		//Laid before the draws: the sprites' vertex stage shrinks the screen by it, the passes read the lights through
		//the view with it, and the world's sprites read the light after
		Lay( frame, shrink, glow.Index, light.Index, snap );
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
		if ( !Solve( names, glow, light, system.GlobalLight, system.Glows, system.Lights.Count, wide, tall ) )
		{
			if ( said.Add( "solve" ) ) Log.Warning( $"the 2D light's passes did not run: {CoreRenderDevice.ShaderError}" );

			Lay( frame, shrink, glow.Index, 0, snap );
			CoreRenderDevice.SetupFrame( frame );
		}

		CoreRenderDevice.ZoneEnd();

		drawnShrink = shrink.X;
		drawnDebug = settings.Debug;
		drawnNames = names;

		return true;
	}

	//The block's lanes for the pictures - see light2dScene and light2dSnap in frame_data.slang
	internal static void Lay( float[] frame, System.Numerics.Vector2 shrink, int scene, int light, System.Numerics.Vector2 snap )
	{
		if ( frame is null || frame.Length < CoreSceneRenderer.FrameFloats ) return;

		frame[CoreSceneRenderer.Light2DSceneAt + 0] = shrink.X;
		frame[CoreSceneRenderer.Light2DSceneAt + 1] = scene;
		frame[CoreSceneRenderer.Light2DSceneAt + 2] = shrink.Y;
		frame[CoreSceneRenderer.Light2DSceneAt + 3] = light;

		frame[CoreSceneRenderer.Light2DSnapAt + 0] = snap.X;
		frame[CoreSceneRenderer.Light2DSnapAt + 1] = snap.Y;
	}

	//How far to move the pictures, in clip space, so the world's origin falls on a texel's corner of them - and so every
	//point of the world on the same part of its texel whatever the camera does, until the zoom changes. Without it a
	//camera moving by less than a texel moved the
	//world against the pictures' grid: an occluder's edge fell in the next texel or did not, and every shadow it threw
	//shook with it, the further from the light the more. The camera's jitter, where an upscaler adds one, shook them
	//the same standing still. The same move, applied wherever the world is put into the pictures, cancels both
	private static System.Numerics.Vector2 SnapOf( float[] frame, System.Numerics.Vector2 shrink, int wide, int tall )
	{
		if ( frame is null || frame.Length < 16 ) return default;

		var worldToClip = MemoryMarshal.Read<System.Numerics.Matrix4x4>( MemoryMarshal.AsBytes( frame.AsSpan( 0, 16 ) ) );
		var clip = System.Numerics.Vector4.Transform( new System.Numerics.Vector4( 0.0f, 0.0f, 0.0f, 1.0f ), worldToClip );

		if ( MathF.Abs( clip.W ) < 1e-6f ) return default;

		var size = new System.Numerics.Vector2( wide, tall );
		var texel = ( ( new System.Numerics.Vector2( clip.X, clip.Y ) / clip.W * shrink ) * 0.5f + new System.Numerics.Vector2( 0.5f ) ) * size;
		var whole = new System.Numerics.Vector2( MathF.Round( texel.X ), MathF.Round( texel.Y ) );

		return ( whole - texel ) * 2.0f / size;
	}

	//The widest a picture is let be, whatever the texel size asks for
	private const int MostTexels = 4096;

	//How many screen pixels a texel of this view's pictures is. Its size in the world is held, not its size on the
	//screen: a camera that zooms changed the world's size of a texel every frame, every occluder fell on the pictures'
	//grid differently, and every shadow shook as it zoomed. So a texel is a power of two of world units - nearest the
	//size Resolution Scale asks for - and kept until the zoom takes it past half again or two thirds of it, when it
	//steps once to the next. Between, the world sits on the same texels however the camera zooms and moves; the
	//pictures are as many texels as the screen takes. Nought where the view says nothing of the world's scale
	private static float TexelOf( Names names, float[] frame, int width, int height, float scale )
	{
		if ( frame is null || frame.Length < 16 || width <= 0 || height <= 0 ) return 0.0f;

		var worldToClip = MemoryMarshal.Read<System.Numerics.Matrix4x4>( MemoryMarshal.AsBytes( frame.AsSpan( 0, 16 ) ) );

		//Screen pixels a world unit along the plane's x is: the view keeps them square
		var perUnit = MathF.Sqrt( ( worldToClip.M11 * width * 0.5f ) * ( worldToClip.M11 * width * 0.5f )
			+ ( worldToClip.M12 * height * 0.5f ) * ( worldToClip.M12 * height * 0.5f ) );

		if ( !float.IsFinite( perUnit ) || perUnit <= 1e-6f ) return 0.0f;

		var wanted = 1.0f / ( scale * perUnit );

		if ( names.Texel <= 0.0f || wanted > names.Texel * 1.5f || wanted < names.Texel * 0.66f )
			names.Texel = MathF.Pow( 2.0f, MathF.Round( MathF.Log2( wanted ) ) );

		return names.Texel * perUnit;
	}

	//The scene marched into the light: the jump flood, the cascades from the coarsest down where anything glows, then
	//the finest gathered with the 2D lights added
	private static bool Solve( Names names, CorePasses.Named scene, CorePasses.Named light, Color sky, bool glows, int lights, int wide, int tall )
	{
		var jump = SolvePipeline( "JumpCs" );
		var cascade = SolvePipeline( "CascadeCs" );
		var shadows = SolvePipeline( "ShadowsCs" );
		var integrate = SolvePipeline( "IntegrateCs" );

		if ( jump == 0 || cascade == 0 || shadows == 0 || integrate == 0 ) return false;

		Span<float> push = stackalloc float[16];

		push[0] = wide;
		push[1] = tall;
		push[4] = scene.Index;
		push[5] = ProbeSpacing;
		push[8] = sky.r;
		push[9] = sky.g;
		push[10] = sky.b;
		push[11] = Interval0 * ProbeSpacing;

		//Only the global light: flat, nothing to march
		if ( !glows && lights == 0 ) return Run( integrate, light, push, wide, tall );

		CoreRenderDevice.ZoneBegin( "light 2d flood" );
		var flooded = Flood( names, jump, push, wide, tall );
		CoreRenderDevice.ZoneEnd();

		if ( !flooded ) return false;

		//The cascades carry only what glows - the lights are added straight and the global light is flat - so where
		//nothing glows they are not run at all
		if ( glows )
		{
			CoreRenderDevice.ZoneBegin( "light 2d cascades" );
			var cascaded = Cascades( names, cascade, push, wide, tall );
			CoreRenderDevice.ZoneEnd();

			if ( !cascaded ) return false;
		}

		//Each light's shadow map: a row of angles marched once, which every pixel it reaches reads rather than marching
		if ( lights > 0 )
		{
			var map = CorePasses.Acquire( names.Shadows, CoreRender.PictureKind.SingleFull, ShadowAngles, SceneLight2DSystem.MostLights, 1 );
			if ( map is null ) return false;

			CorePasses.Close();

			push[14] = map.Index;

			CoreRenderDevice.ZoneBegin( "light 2d shadows" );
			var mapped = Run( shadows, map, push, ShadowAngles, lights );
			CoreRenderDevice.ZoneEnd();

			if ( !mapped ) return false;
		}

		CoreRenderDevice.ZoneBegin( "light 2d lights" );
		var done = Run( integrate, light, push, wide, tall );
		CoreRenderDevice.ZoneEnd();

		return done;
	}

	//Every two by two block's nearest blocking block, as the way to it: strides halving down to one, the first taking
	//each blocker as its own seed. From no further than FloodStride pixels, all a ray needs: a texel given no blocker
	//is that far from one at least, and strides that far. A ray only strides by it, so blocks are near enough, at a
	//quarter of the work. Leaves the result's index in push[6]
	private static bool Flood( Names names, ulong jump, Span<float> push, int wide, int tall )
	{
		var (blocksWide, blocksTall) = (( wide + 1 ) / 2, ( tall + 1 ) / 2);

		var seedsA = CorePasses.Acquire( names.SeedsA, CoreRender.PictureKind.Pair, blocksWide, blocksTall, 1 );
		var seedsB = CorePasses.Acquire( names.SeedsB, CoreRender.PictureKind.Pair, blocksWide, blocksTall, 1 );

		if ( seedsA is null || seedsB is null ) return false;

		CorePasses.Close();

		var first = Math.Min( HighestBit( Math.Max( blocksWide, blocksTall ) ), FloodStride / 2 );
		push[12] = first * 2;

		var (read, write) = ( seedsA, seedsB );

		for ( var stride = first; stride >= 1; stride /= 2 )
		{
			push[2] = stride;
			push[7] = read.Index;
			push[15] = stride == first ? 1.0f : 0.0f;

			if ( !Run( jump, write, push, blocksWide, blocksTall ) ) return false;

			( read, write ) = ( write, read );
		}

		push[6] = read.Index;
		push[15] = 0.0f;
		return true;
	}

	//The cascades, the coarsest first, each merging the one above it out of the other picture. Leaves the finest's index
	//in push[7] and says in push[13] that they ran
	private static bool Cascades( Names names, ulong cascade, Span<float> push, int wide, int tall )
	{
		var count = CascadeCount( wide, tall );
		var (cascadeWide, cascadeTall) = CascadeSize( wide, tall, count );

		var cascadeA = CorePasses.Acquire( names.CascadeA, CoreRender.PictureKind.Hdr, cascadeWide, cascadeTall, 1 );
		var cascadeB = CorePasses.Acquire( names.CascadeB, CoreRender.PictureKind.Hdr, cascadeWide, cascadeTall, 1 );

		if ( cascadeA is null || cascadeB is null ) return false;

		CorePasses.Close();

		var (above, into) = ( cascadeA, cascadeB );

		push[3] = count;

		for ( var n = count - 1; n >= 0; n-- )
		{
			push[2] = n;
			push[7] = n == count - 1 ? 0 : above.Index;

			var (probesWide, probesTall) = Probes( wide, tall, n );

			if ( !Run( cascade, into, push, probesWide << n, probesTall << n ) ) return false;

			( above, into ) = ( into, above );
		}

		push[7] = above.Index;
		push[13] = 1.0f;
		return true;
	}

	//Angles a light's shadow map has about it - see ShadowsCs in light2d_cascades.slang
	private const int ShadowAngles = 4096;

	//How far apart the first cascade's probes stand, in the scene's pixels; each cascade past it twice the one before.
	//Every cascade sends four times as many rays as the picture has pixels over this squared, which is what they cost
	//less than at one probe a pixel - the glow they carry is soft light, and the lights' own shadows are mapped apart
	private const int ProbeSpacing = 4;

	//A cascade's probes along each side
	private static (int Wide, int Tall) Probes( int wide, int tall, int cascade )
	{
		var spacing = ProbeSpacing << cascade;
		return (( wide + spacing - 1 ) / spacing, ( tall + spacing - 1 ) / spacing);
	}

	//How far the flood reaches, in the scene's pixels: past it a ray strides this far at a time through empty space.
	//Further cost a pass a doubling over the whole picture for strides nothing near a blocker could take anyway
	private const int FloodStride = 64;

	//Enough cascades that the last one's stretch reaches across the whole picture
	private static int CascadeCount( int wide, int tall )
	{
		var diagonal = MathF.Sqrt( ( wide * (float)wide ) + ( tall * (float)tall ) );
		var count = 1;

		while ( count < 10 && Interval0 * ProbeSpacing * ( ( 1 << ( 2 * count ) ) - 1 ) / 3.0f < diagonal ) count++;

		return count;
	}

	//A cascade's probes times the side of each one's block of averages - a quarter of its rays - the widest of them all
	private static (int Wide, int Tall) CascadeSize( int wide, int tall, int count )
	{
		var cascadeWide = 0;
		var cascadeTall = 0;

		for ( var n = 0; n < count; n++ )
		{
			var (probesWide, probesTall) = Probes( wide, tall, n );

			cascadeWide = Math.Max( cascadeWide, probesWide << n );
			cascadeTall = Math.Max( cascadeTall, probesTall << n );
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

		var defines = entry switch
		{
			"JumpCs" => EngineShaders.Light2DSeeds,
			"ShadowsCs" => EngineShaders.Light2DShadows,
			_ => null,
		};
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
