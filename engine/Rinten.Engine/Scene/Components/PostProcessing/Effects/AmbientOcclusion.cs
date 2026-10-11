using Rinten.Rendering;

namespace Rinten;

//Draws "Occlusion" after the depth pre-pass and surfaces darken by it; fixed 35cm reach
/// <summary>Screen space ambient occlusion: darkens corners, crevices and surfaces close to each other.</summary>
[Expose]
[Title( "Ambient Occlusion (SSAO)" )]
[Category( "Post Processing" )]
[Icon( "contrast" )]
public sealed partial class AmbientOcclusion : BasePostProcess<AmbientOcclusion>
{
	public override int ComponentVersion => 1;

	[ConVar( "r_ao_quality", Min = 0, Max = 3, Help = "Ambient occlusion quality (0: off, 1: low, 2: med, 3: high)" )]
	internal static int UserQuality { get; set; } = 3;

	/// <summary>The intensity of the darkening effect. Has no impact on performance.</summary>
	[Property, Range( 0, 1 ), Category( "Properties" )]
	public float Intensity { get; set; } = 1.0f;

	/// <summary>Divides the resolution occlusion runs at; the result is read with bilinear filtering.</summary>
	[ConVar( "r_ao_resolution", Min = 1, Max = 8, Help = "Ambient occlusion resolution scale divisor (1: full, 2: half, 4: quarter, 8: eighth)" )]
	internal static int UserResolution { get; set; } = 2;

	/// <summary>How many same-frame spatial denoise passes; each doubles the step (1, 2, 4).</summary>
	[ConVar( "r_ao_denoise_passes", Min = 1, Max = 3, Help = "Number of spatial (À-Trous) denoise passes (1-3)" )]
	internal static int UserDenoisePasses { get; set; } = 2;

	private static Material Occlusion => field ??= Material.FromShader( "shaders/postprocess/ambient_occlusion.shader" );

	//The picture of the light the horizons hide, which the frame lays for surfaces - see CoreSceneDrawer
	internal const string IndirectPicture = "Indirect";

	//Last frame's lit world, per camera: what IndirectPs reads. A target of its own, as frame pictures go back each frame;
	//small, as it is light bounced off a patch rather than a picture anyone sees
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CameraComponent, RenderTarget> history = new();
	private const int HistoryWidth = 640;
	private const int HistoryHeight = 360;

	//And the indirect light as accumulated, per camera, which each frame adds a fifth of itself to - see TemporalIndirectPs
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CameraComponent, RenderTarget> accumulated = new();
	private const int AccumulatedWidth = 960;
	private const int AccumulatedHeight = 540;

	public override void Render()
	{
		if ( UserQuality <= 0 ) return;

		var intensity = Math.Clamp( GetWeighted( x => x.Intensity, 1.0f ), 0.0f, 1.0f );
		if ( intensity <= 0.0f ) return;

		var cl = NextList( "Ambient Occlusion" );
		var scale = Math.Clamp( UserResolution, 1, 8 );

		//Occlusion and bent normal from depth, packed into four 8-bit channels
		var raw = cl.GetPicture( "OcclusionRaw", PictureFormat.Colour, sizeFactor: scale );

		cl.Attributes.Set( "Radius", 0.5f );
		cl.Attributes.Set( "Slices", (float)( UserQuality switch { 1 => 1, 2 => 2, _ => 2 } ) );
		cl.Attributes.Set( "Steps", (float)( UserQuality switch { 1 => 3, 2 => 3, _ => 4 } ) );
		cl.SetRenderTarget( raw );
		cl.Blit( Occlusion );
		cl.ClearRenderTarget();

		//The first run averages the 4x4 square the turns repeat over, in four reads; each further run reaches twice as far.
		//The last writes the picture the world reads, with the intensity in it.
		var passes = Math.Clamp( UserDenoisePasses, 1, 3 );
		var soft = passes > 1 ? cl.GetPicture( "OcclusionSoft", PictureFormat.Colour, sizeFactor: scale ) : raw;
		var result = cl.GetPicture( "Occlusion", PictureFormat.Colour, sizeFactor: scale );

		var from = raw;

		for ( var pass = 0; pass < passes; pass++ )
		{
			var last = pass == passes - 1;
			var into = last ? result : ( pass & 1 ) == 0 ? soft : raw;

			cl.Attributes.Set( "Source", from.ColorTexture );
			cl.Attributes.Set( "Reach", (float)( 1 << pass ) );
			cl.Attributes.Set( "Strength", last ? intensity : 1.0f );
			cl.SetRenderTarget( into );
			cl.Blit( Occlusion, entry: "SoftenPs" );
			cl.ClearRenderTarget();

			from = into;
		}

		//And the light those horizons hide, at medium and high: from last frame's picture, once there is one
		if ( UserQuality >= 2 && Camera.IsValid() )
		{
			var known = history.TryGetValue( Camera, out var last );
			var device = known ? last.ColorTarget?.core?.Device() ?? 0 : 0;

			if ( device != 0 )
			{
				var gathered = cl.GetPicture( "IndirectRaw", PictureFormat.Hdr, sizeFactor: scale * 2 );
				var softer = cl.GetPicture( "IndirectSoft", PictureFormat.Hdr, sizeFactor: scale * 2 );
				var indirect = cl.GetPicture( IndirectPicture, PictureFormat.Hdr, sizeFactor: scale * 2 );

				cl.Attributes.Set( "Last", (float)CoreRender.TextureIndex( device ) );
				cl.Attributes.Set( "Radius", 2.0f );
				cl.Attributes.Set( "Slices", 2.0f );
				cl.Attributes.Set( "Steps", 4.0f );
				cl.SetRenderTarget( gathered );
				cl.Blit( Occlusion, entry: "IndirectPs" );
				cl.ClearRenderTarget();

				//Softened twice, a texel then two apart, which covers the turns' 4x4 square and its neighbours
				cl.Attributes.Set( "Source", gathered.ColorTexture );
				cl.Attributes.Set( "Reach", 1.0f );
				cl.SetRenderTarget( softer );
				cl.Blit( Occlusion, entry: "SoftenIndirectPs" );
				cl.ClearRenderTarget();

				cl.Attributes.Set( "Source", softer.ColorTexture );
				cl.Attributes.Set( "Reach", 2.0f );
				cl.SetRenderTarget( gathered );
				cl.Blit( Occlusion, entry: "SoftenIndirectPs" );
				cl.ClearRenderTarget();

				//Then over what earlier frames gathered, which it is kept as for the next
				var kept = Accumulated( Camera, out var keptDevice );

				cl.Attributes.Set( "Source", gathered.ColorTexture );
				cl.Attributes.Set( "Before", keptDevice != 0 ? (float)CoreRender.TextureIndex( keptDevice ) : 0.0f );
				cl.SetRenderTarget( indirect );
				cl.Blit( Occlusion, entry: "TemporalIndirectPs" );
				cl.ClearRenderTarget();

				cl.Attributes.Set( "Source", indirect.ColorTexture );
				cl.SetRenderTarget( kept );
				cl.Blit( Occlusion, entry: "KeepPs" );
				cl.ClearRenderTarget();
			}
			else if ( !known )
			{
				//Its own, not the pool's, which takes every target back at the end of a frame
				var texture = Texture.CreateRenderTarget().WithFormat( ImageFormat.RGBA16161616F ).WithSize( HistoryWidth, HistoryHeight ).Create( "last lit" );
				last = RenderTarget.From( texture );
				history.Add( Camera, last );
			}

			var copy = NextList( "Last lit" );
			copy.SetRenderTarget( last );
			copy.Blit( Occlusion, entry: "CopyPs" );
			copy.ClearRenderTarget();

			InsertCommandList( copy, Stage.AfterOpaque, 300, "Last lit" );
		}

		InsertCommandList( cl, Stage.AfterDepthPrepass, 0, "Ambient Occlusion" );
	}

	//A camera's accumulated indirect light, made the first time; its device texture is nought until first drawn into
	private static RenderTarget Accumulated( CameraComponent camera, out ulong device )
	{
		if ( !accumulated.TryGetValue( camera, out var kept ) )
		{
			var texture = Texture.CreateRenderTarget().WithFormat( ImageFormat.RGBA16161616F ).WithSize( AccumulatedWidth, AccumulatedHeight ).Create( "indirect kept" );
			kept = RenderTarget.From( texture );
			accumulated.Add( camera, kept );
			device = 0;
			return kept;
		}

		device = kept.ColorTarget?.core?.Device() ?? 0;
		return kept;
	}
}
