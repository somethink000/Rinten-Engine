using System.Runtime.CompilerServices;
using Rinten.Rendering;

namespace Rinten;

//The light near surfaces by hardware rays (ray_light.shader), hung on every camera while rays are on: traced at half size,
//softened and kept over frames as AmbientOcclusion keeps its gather, which steps aside for it - see CoreSceneDrawer
internal static class CoreRayLight
{
	/// <summary>The picture the frame lays as its traced screen light - see LayScreenLight.</summary>
	internal const string Picture = "RayLight";

	private static Material Tracer => field ??= Material.FromShader( "shaders/postprocess/ray_light.shader" );

	//Last frame's lit world, which a hit the screen shows is read from, and the light as kept over frames
	private const int LastWidth = 960;
	private const int LastHeight = 540;
	private const int KeptWidth = 1280;
	private const int KeptHeight = 720;

	//Two of the kept light, written in turn: each frame reads one and writes the other at the same texels, so a still
	//view takes no blur from frame to frame
	private sealed class Kept
	{
		public readonly CommandList Trace = new( "Ray traced light" );
		public readonly CommandList Copy = new( "Last lit for rays" );
		public RenderTarget Last;
		public readonly RenderTarget[] Light = new RenderTarget[2];
		public int Into;
	}

	private static readonly ConditionalWeakTable<CameraComponent, Kept> kept = new();

	//Which frame this is, so each traces new directions
	private static int turn;

	/// <summary>Whether the rays draw the near light this frame, in place of AmbientOcclusion's gather.</summary>
	internal static bool On => CoreRays.On;

	/// <summary>Hangs the trace on a camera's stages; nothing where rays are off.</summary>
	internal static void Build( CameraComponent camera )
	{
		//A scene with no bake takes its light from the probe grid, which keeps nothing of the screen
		if ( !On || !camera.IsValid() || CoreProbeGrid.Takes( camera.Scene ) ) return;

		var mine = kept.GetValue( camera, static _ => new Kept() );

		mine.Last ??= Target( "last lit for rays", LastWidth, LastHeight );
		mine.Light[0] ??= Target( "ray light kept", KeptWidth, KeptHeight );
		mine.Light[1] ??= Target( "ray light kept", KeptWidth, KeptHeight );

		var into = mine.Light[mine.Into];
		var from = mine.Light[1 - mine.Into];
		mine.Into = 1 - mine.Into;

		var last = IndexOf( mine.Last );
		var before = IndexOf( from );
		var held = IndexOf( into );

		var cl = mine.Trace;
		cl.Reset();

		var raw = cl.GetPicture( "RayLightRaw", PictureFormat.Hdr, sizeFactor: 2 );
		var soft = cl.GetPicture( "RayLightSoft", PictureFormat.Hdr, sizeFactor: 2 );
		var light = cl.GetPicture( Picture, PictureFormat.Hdr, sizeFactor: 2 );

		cl.Attributes.Set( "Last", last );
		cl.Attributes.Set( "Length", 4.0f );
		cl.Attributes.Set( "Count", 2.0f );
		cl.Attributes.Set( "Turn", (float)( turn++ % 65536 ) );
		cl.SetRenderTarget( raw );
		cl.Blit( Tracer );
		cl.ClearRenderTarget();

		//Over what earlier frames traced, into the kept light; then softened from it three times, one, two and four texels
		//apart, by depth - the kept stays unsoftened, so the frames do not blur into each other
		cl.Attributes.Set( "Source", raw.ColorTexture );
		cl.Attributes.Set( "Before", before );
		cl.SetRenderTarget( into );
		cl.Blit( Tracer, entry: "TemporalPs" );
		cl.ClearRenderTarget();

		//A kept target has no place in the table until first drawn: that frame softens what it traced
		if ( held != 0 ) cl.Attributes.Set( "Source", held );
		cl.Attributes.Set( "Reach", 1.0f );
		cl.SetRenderTarget( soft );
		cl.Blit( Tracer, entry: "SoftenPs" );
		cl.ClearRenderTarget();

		cl.Attributes.Set( "Source", soft.ColorTexture );
		cl.Attributes.Set( "Reach", 2.0f );
		cl.SetRenderTarget( raw );
		cl.Blit( Tracer, entry: "SoftenPs" );
		cl.ClearRenderTarget();

		cl.Attributes.Set( "Source", raw.ColorTexture );
		cl.Attributes.Set( "Reach", 4.0f );
		cl.SetRenderTarget( light );
		cl.Blit( Tracer, entry: "SoftenPs" );
		cl.ClearRenderTarget();

		var copy = mine.Copy;
		copy.Reset();
		copy.SetRenderTarget( mine.Last );
		copy.Blit( Tracer, entry: "CopyPs" );
		copy.ClearRenderTarget();

		Hang( camera, cl, Stage.AfterDepthPrepass, -100, "Ray traced light" );
		Hang( camera, copy, Stage.AfterOpaque, 300, "Last lit for rays" );
	}

	private static RenderTarget Target( string name, int width, int height )
		=> RenderTarget.From( Texture.CreateRenderTarget().WithFormat( ImageFormat.RGBA16161616F ).WithSize( width, height ).Create( name ) );

	//A kept target's place in the table, nought until it has been drawn into once
	private static float IndexOf( RenderTarget target )
		=> target.ColorTarget?.core?.Device() is { } device and not 0 ? CoreRender.TextureIndex( device ) : 0.0f;

	private static void Hang( CameraComponent camera, CommandList list, Stage stage, int order, string name )
	{
		var layer = camera.PostProcess.CreateLayer( stage );
		layer.Order = order;
		layer.CommandList = list;
		layer.Name = name;
	}
}
