using Rinten.Rendering;

namespace Rinten;

//One pass writes "Defocused" at half size with blur amount in alpha; the compose mixes it in
/// <summary>Applies a depth of field effect to the camera</summary>
[Title( "Depth Of Field" )]
[Category( "Post Processing" )]
[Icon( "center_focus_strong" )]
public sealed class DepthOfField : BasePostProcess<DepthOfField>
{
	/// <summary>Not in a headset: focus that is not where the eye looks makes people sick. See <see cref="BasePostProcess.AllowedInHeadset"/>.</summary>
	public override bool AllowedInHeadset => false;

	//same problem, this isn't granular enough
	[ConVar( "r_dof_quality", ConVarFlags.Saved, Min = 0, Max = 3, Help = "Depth of field quality (0: off, 1: low, 2: med, 3: high)" )]
	internal static int Quality { get; set; } = 3;

	//In pixels of the finished picture, so it means the same thing at every resolution. Eight is a soft background rather than an unreadable one.
	/// <summary>How blurry to make stuff that isn't in focus.</summary>
	[Range( 0, 100 )]
	[Property, Group( "Focus" ), Icon( "blur_circular" )]
	public float BlurSize { get; set; } = 8.0f;

	//Two hundred was the number this arrived with, from an engine that measured in inches - five metres, and nothing at all in one that measures in metres, where two hundred is further than most rooms are deep and nothing would ever be out of focus.
	/// <summary>How far away from the camera to focus, in metres.</summary>
	[Range( 0.1f, 1000 ), FocusPicker]
	[Property, Group( "Focus" ), Icon( "horizontal_distribute" )]
	public float FocalDistance { get; set; } = 5.0f;

	//In metres, and for the same reason as the distance above: five hundred of them is the whole of any scene.
	/// <summary>This modulates how far is the blur to the image.</summary>
	[Property, Range( 0.01f, 1000.0f ), Group( "Focus" ), Icon( "blur_linear" )]
	public float FocusRange { get; set; } = 12.0f;

	private static Material Defocus => field ??= Material.FromShader( "shaders/postprocess/depth_of_field.shader" );

	public override void Render()
	{
		var quality = Math.Clamp( Quality, 0, 3 );
		if ( quality <= 0 ) return;

		var focal = GetWeighted( x => x.FocalDistance, 5.0f );
		var blur = GetWeighted( x => x.BlurSize, 0.0f );
		if ( focal <= 0.0f || blur <= 0.0f ) return;

		var cl = NextList( "Depth of field" );
		var focus = cl.GetPicture( "FocusSource", PictureFormat.Hdr, sizeFactor: 2 );
		var defocused = cl.GetPicture( "Defocused", PictureFormat.Hdr, sizeFactor: 2 );

		cl.Attributes.Set( "FocalDistance", focal );
		cl.Attributes.Set( "FocusRange", MathF.Max( GetWeighted( x => x.FocusRange, 12.0f ), 0.01f ) );
		cl.Attributes.Set( "BlurSize", blur );
		cl.Attributes.Set( "Taps", (float)( quality switch { 1 => 4, 2 => 8, _ => 16 } ) );
		cl.Attributes.Set( "FrameSize", cl.ViewportSize );

		//The frame at half the size with how far out of focus each pixel is beside it, worked out once a pixel - then the blur, which reads nothing else.
		cl.SetRenderTarget( focus );
		cl.Blit( Defocus, entry: "PrefilterPs" );
		cl.ClearRenderTarget();

		cl.Attributes.Set( "Source", focus.ColorTexture );
		cl.SetRenderTarget( defocused );
		cl.Blit( Defocus );
		cl.ClearRenderTarget();

		//Ahead of the other effects that read the frame, and its lay-over after them, so it is still held for the
		//compose - see CorePointChain. Laid before the glow and the reflections, which go on top of what it blurred.
		InsertCommandList( cl, Stage.BeforePostProcess, 60, "Depth of field" );

		var lay = NextList( "Depth of field lay" );

		lay.Attributes.Set( "Defocused", defocused.ColorTexture );
		lay.Apply( Defocus );

		InsertCommandList( lay, Stage.BeforePostProcess, 101, "Depth of field lay" );
	}
}
