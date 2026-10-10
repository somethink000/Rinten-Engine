using System.Threading;
using System.Threading.Tasks;

namespace Rinten;

//Holds a scene's bake settings and result; the scene view's build panel bakes it
/// <summary>The scene's baked lighting: everything a frame cannot work out about light, worked out once and kept.</summary>
[Expose]
[Title( "Baked Lighting" )]
[Category( "Light" )]
[Icon( "wb_iridescent" )]
public sealed partial class BakedLighting : Component, Component.ExecuteInEditor
{
	/// <summary>How many texels of the atlas a metre of surface gets.</summary>
	[Property, Group( "Surfaces" ), Range( 1, 64 )]
	public float TexelsPerMetre { get; set; } = 16.0f;

	/// <summary>The widest the atlas may be. A scene that needs more at the density above gets less density, evenly.</summary>
	[Property, Group( "Surfaces" ), Range( 256, 8192 ), Step( 256 )]
	public int MaxResolution { get; set; } = 4096;

	/// <summary>Paths traced a texel. Noise goes as one over its square root; the filter takes what is left.</summary>
	[Property, Group( "Surfaces" ), Range( 16, 4096 )]
	public int Samples { get; set; } = 512;

	/// <summary>How hard the filter smooths what the rays left noisy. Nought is the raw trace. It follows each texel's own noise, so a sharp shadow stays sharp.</summary>
	[Property, Group( "Surfaces" ), Range( 0, 8 )]
	public float Denoise { get; set; } = 3.0f;

	/// <summary>How far apart the points of the volume stand, in metres.</summary>
	[Property, Group( "Volume" ), Range( 0.25f, 8.0f )]
	public float VolumeSpacing { get; set; } = 1.0f;

	/// <summary>Rays a point of the volume sends, spread evenly over every direction.</summary>
	[Property, Group( "Volume" ), Range( 64, 4096 )]
	public int VolumeSamples { get; set; } = 1024;

	/// <summary>How many times light may bounce before a path stops. Past the second, a path goes on only as often as it still carries light.</summary>
	[Property, Group( "Light" ), Range( 1, 16 )]
	public int Bounces { get; set; } = 6;

	/// <summary>The sky's light, against what the frame gives an open surface. One is the same: a bake of an open field is the picture the frame already drew, and what changes is the corners and the bounce.</summary>
	[Property, Group( "Light" ), Range( 0, 4 )]
	public float SkyStrength { get; set; } = 1.0f;

	/// <summary>How much of the light surfaces throw back, against what their colour says. Above one is a cheat, and looks like one.</summary>
	[Property, Group( "Light" ), Range( 0, 2 )]
	public float BounceStrength { get; set; } = 1.0f;

	/// <summary>How wide the sun is across, in degrees - the real one is half of one. What decides how soft a baked sun's shadow is.</summary>
	[Property, Group( "Light" ), Range( 0, 10 )]
	public float SunWidth { get; set; } = 0.53f;

	/// <summary>The last bake. Empty until somebody bakes.</summary>
	[Property, Hide]
	public BakedLightingData Data
	{
		get;
		set
		{
			if ( ReferenceEquals( field, value ) ) return;

			if ( Active ) CoreBakedLighting.Unplace( this );
			field = value;
			if ( Active ) CoreBakedLighting.Place( this );
		}
	}

	/// <summary>Whether there is a bake to read, as against a wish for one.</summary>
	public bool HasBake => Data is { IsEmpty: false };

	protected override void OnEnabled() => CoreBakedLighting.Place( this );

	protected override void OnDisabled() => CoreBakedLighting.Unplace( this );

	/// <summary>Traces the scene's light and keeps it, off the main thread, reporting progress.</summary>
	public async Task Bake( CancellationToken cancel = default )
	{
		if ( !Scene.IsValid() ) return;

		var made = await CoreBakedLighting.Bake( this, cancel );

		if ( made is null ) return;

		Data = made;

		if ( Scene.Editor is { } editor ) editor.HasUnsavedChanges = true;
	}

	/// <summary>Throws the bake away: every light back in the frame, every surface back on the frame's sky.</summary>
	public void Clear()
	{
		Data = null;

		if ( Scene.Editor is { } editor ) editor.HasUnsavedChanges = true;
	}
}
