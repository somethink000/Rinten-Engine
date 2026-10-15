using Rinten.Rendering;
namespace Rinten;

using System.Text.Json.Nodes;

/// <summary>Applies a bloom effect to the camera</summary>
[Title( "Bloom" )]
[Category( "Post Processing" )]
[Icon( "exposure" )]
public class Bloom : BasePostProcess<Bloom>
{
	[ConVar( "r_bloom", ConVarFlags.Saved, Help = "Enable or disable bloom effect." )]
	internal static bool UserEnabled { get; set; } = true;

	/// <summary>How the glow is laid over the frame.</summary>
	public enum BloomMode
	{
		/// <summary>Added to it.</summary>
		Additive,

		/// <summary>Screened over it: what is already bright cannot be taken past white.</summary>
		Screen,

		/// <summary>The glow alone, for looking at what the passes are doing.</summary>
		Blur
	}

	[Property] public BloomMode Mode { get; set; }

	[Range( 0, 10 )]
	[Property] public float Strength { get; set; } = 1.0f;

	[Range( 0, 2 )]
	[Property] public float Threshold { get; set; } = 1.0f;

	//0.5 is an even share across blur levels (inverse-square halo); higher hazes, lower stays tight
	/// <summary>How far out the light carries, as a share between the near levels of the blur and the wide ones.</summary>
	[Range( 0, 1 )]
	[Property] public float Spread { get; set; } = 0.5f;

	[Property] public Color Tint { get; set; } = Color.White;

	private static Material Shader => field ??= Material.FromShader( "shaders/postprocess/bloom.shader" );
	private static ComputeShader Gather => field ??= new ComputeShader( "shaders/postprocess/bloom.shader", "GatherCs" );
	private static ComputeShader Finish => field ??= new ComputeShader( "shaders/postprocess/bloom.shader", "FinishCs" );

	/// <summary>Threshold at full size, downsample and blur by compute, then add back via bloom.shader's Apply</summary>
	public override void Render()
	{
		if ( Strength <= 0.0f || !UserEnabled )
			return;

		//The portable frame has no compute or world picture for the passes below, and draws its own
		if ( CoreRenderDevice.PortableFrame )
		{
			var scale = GetWeighted( x => x.Strength, 0 );
			var colour = GetWeighted( x => x.Tint, Color.White );
			var falloff = 0.70f + ( 0.60f * Math.Clamp( GetWeighted( x => x.Spread, 0.5f, onlyLerpBetweenVolumes: true ), 0.0f, 1.0f ) );

			Camera.PostProcess.Glow = new CorePortableGlow.Settings( GetWeighted( x => x.Threshold, 0 ),
				new Vector3( scale * colour.r, scale * colour.g, scale * colour.b ), falloff,
				Mode switch { BloomMode.Additive => 1.0f, BloomMode.Screen => 2.0f, _ => 3.0f } );

			return;
		}

		var cl = NextList( "Bloom" );

		//What is over the line, at half the frame's size, with a chain of halvings under it. The
		//line is in the light the world was shaded in, which is why this belongs before the tonemap.
		var kept = cl.GetPicture( "BloomKept", PictureFormat.HdrCompact, sizeFactor: 2, levels: 8 );

		cl.Attributes.Set( "Threshold", GetWeighted( x => x.Threshold, 0 ) );
		cl.SetRenderTarget( kept );
		cl.Blit( Shader, entry: "BrightPs" );
		cl.ClearRenderTarget();
		cl.GenerateMipMaps( kept );

		//The glow, out of the halvings: the wide levels at a quarter of the frame, then the near ones and those at half.
		var coarse = cl.GetPicture( "BloomCoarse", PictureFormat.HdrCompact, sizeFactor: 4 );
		var glow = cl.GetPicture( "Glow", PictureFormat.HdrCompact, sizeFactor: 2 );

		var strength = GetWeighted( x => x.Strength, 0 );
		var tint = GetWeighted( x => x.Tint, Color.White );
		var spread = GetWeighted( x => x.Spread, 0.5f, onlyLerpBetweenVolumes: true );

		cl.Attributes.Set( "Kept", kept.ColorTexture );
		cl.Attributes.Set( "KeptSize", kept.Size );
		cl.Attributes.Set( "FrameSize", cl.ViewportSize );
		cl.Attributes.Set( "Falloff", 0.70f + ( 0.60f * Math.Clamp( spread, 0.0f, 1.0f ) ) );

		cl.Attributes.Set( "Out", coarse.ColorTexture );
		cl.DispatchCompute( Gather, coarse.Size );

		cl.Attributes.Set( "Coarse", coarse.ColorTexture );
		cl.Attributes.Set( "Scale", new Vector3( strength * tint.r, strength * tint.g, strength * tint.b ) );
		cl.Attributes.Set( "Out", glow.ColorTexture );
		cl.DispatchCompute( Finish, glow.Size );

		InsertCommandList( cl, Stage.BeforePostProcess, 100, "Bloom" );

		//Lay the glow over the frame as a point effect: additive, screen, or glow only for debugging
		var lay = NextList( "Bloom lay" );

		lay.Attributes.Set( "Glow", glow.ColorTexture );
		lay.Attributes.Set( "Mode", Mode switch
		{
			BloomMode.Additive => 1.0f,
			BloomMode.Screen => 2.0f,
			_ => 3.0f,
		} );
		lay.Apply( Shader );

		InsertCommandList( lay, Stage.BeforePostProcess, 102, "Bloom lay" );
	}

	public override int ComponentVersion => 1;
	[Expose, JsonUpgrader( typeof( Bloom ), 1 )]
	static void Upgrader_v1( JsonObject obj )
	{
		//Our bloom has a wider range, remap existing ones to an acceptable range
		if ( obj.TryGetPropertyValue( "Threshold", out var frequency ) )
		{
			obj["Threshold"] = ((float)frequency / 2.0f) + 1.0f;
		}
	}
}
