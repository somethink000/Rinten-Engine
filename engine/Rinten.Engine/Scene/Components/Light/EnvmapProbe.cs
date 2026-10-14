namespace Rinten;

/// <summary>A cubemap probe: what a place reflects, and how far its reflection reaches. Baked ones are taken by the
/// scene's bake and kept in its envmaps asset; runtime ones draw themselves as they play.</summary>
[Expose]
[Title( "Envmap Probe" )]
[Category( "Light" )]
[Icon( "radio_button_unchecked" )]
[Alias( "EnvmapComponent" )]
public sealed partial class EnvmapProbe : Component, Component.ExecuteInEditor, Component.DontExecuteOnServer, ISceneLoadingEvents
{
	[Expose]
	public enum EnvmapProbeMode
	{
		/// <summary>Taken by the scene's bake and read as it was kept - see BakedLighting.Envmaps.</summary>
		[Icon( "🥖" )]
		Baked,

		/// <summary>Drawn while the game runs, as often as its update says.</summary>
		[Icon( "📹" )]
		Runtime,
	}

	[Expose]
	public enum ProjectionMode
	{
		/// <summary>Reflected as though everything were far away.</summary>
		[Icon( "panorama_photosphere" )]
		Sphere = 0,

		/// <summary>Reflected off the box's walls, so a room's reflection lines up with the room.</summary>
		[Icon( "check_box_outline_blank" )]
		Box = 1,
	}

	[Expose]
	public enum CubemapResolution
	{
		[Title( "128x128" )]
		Small = 128,

		[Title( "256x256" )]
		Medium = 256,

		[Title( "512x512" )]
		Large = 512,
	}

	[Expose]
	public enum CubemapDynamicUpdate
	{
		/// <summary>Once, when it is switched on.</summary>
		OnEnabled,

		/// <summary>Every frame, as far as LeastBetweenCaptures lets it.</summary>
		EveryFrame,

		/// <summary>Every so many frames.</summary>
		FrameInterval,

		/// <summary>Every so many seconds.</summary>
		TimeInterval,
	}

	//Seconds between a runtime probe's captures at least; each is six draws of the world
	private const float LeastBetweenCaptures = 0.25f;

	/// <summary>Whether the probe is taken by the scene's bake or drawn while the game runs.</summary>
	[Property, EnumButtonGroup, WideMode( HasLabel = false )]
	public EnvmapProbeMode Mode
	{
		get;
		set
		{
			if ( field == value ) return;

			field = value;
			CoreEnvmap.Forget( this );
			owed = value == EnvmapProbeMode.Runtime;
		}
	}

	/// <summary>How a reflection is read: as from far away, or off the box's walls.</summary>
	[Property, Space]
	public ProjectionMode Projection { get; set; }

	/// <summary>What the reflection is multiplied by, alpha its strength.</summary>
	[Property]
	public Color TintColor { get; set; } = Color.White;

	/// <summary>Where it reflects, about the probe: whole inside, fading over Feathering past it.</summary>
	[Property]
	public BBox Bounds { get; set; } = BBox.FromPositionAndSize( 0, 26.0f );

	/// <summary>How far past the box its reflection fades out, in metres.</summary>
	[Property, Range( 0.0f, 32.0f )]
	public float Feathering { get; set; } = 0.2f;

	/// <summary>Which of the probes over a place wins: higher first, then the nearer.</summary>
	[Property, Range( 0, 100 )]
	public int Priority { get; set; }

	/// <summary>How big each of the six faces is drawn.</summary>
	[Header( "Rendering" )]
	[Property, EnumButtonGroup]
	public CubemapResolution Resolution { get; set; } = CubemapResolution.Small;

	/// <summary>The nearest a face draws, in metres.</summary>
	[Property]
	public float ZNear { get; set; } = 0.4f;

	/// <summary>The furthest a face draws, in metres.</summary>
	[Property]
	public float ZFar { get; set; } = 104.0f;

	/// <summary>When a runtime probe draws itself again.</summary>
	[Header( "Runtime Updates" )]
	[ShowIf( nameof( Mode ), EnvmapProbeMode.Runtime )]
	[Property]
	public CubemapDynamicUpdate UpdateStrategy { get; set; }

	/// <summary>Seconds between captures, for TimeInterval.</summary>
	[ShowIf( nameof( Mode ), EnvmapProbeMode.Runtime )]
	[ShowIf( nameof( UpdateStrategy ), CubemapDynamicUpdate.TimeInterval )]
	[Property, Range( 0, 10 )]
	public float DelayBetweenUpdates { get; set; } = 0.1f;

	/// <summary>Frames between captures, for FrameInterval.</summary>
	[ShowIf( nameof( Mode ), EnvmapProbeMode.Runtime )]
	[ShowIf( nameof( UpdateStrategy ), CubemapDynamicUpdate.FrameInterval )]
	[Property, Range( 0, 16 )]
	public int FrameInterval { get; set; } = 5;

	/// <summary>Captures again a few times once switched on, so it shows itself in what it reflects.</summary>
	[ShowIf( nameof( Mode ), EnvmapProbeMode.Runtime )]
	[ShowIf( nameof( UpdateStrategy ), CubemapDynamicUpdate.OnEnabled )]
	[Property]
	public bool MultiBounce { get; set; }

	//A runtime probe's capture that is due
	private bool owed;

	private float capturedAt;
	private int bouncesLeft;
	private int queuedFrames;
	private float queuedTime;

	protected override void OnEnabled()
	{
		if ( Mode != EnvmapProbeMode.Runtime ) return;

		bouncesLeft = MultiBounce ? 4 : 0;
		owed = true;
	}

	protected override void OnDisabled()
	{
		CoreEnvmap.Forget( this );
		owed = false;
	}

	//A runtime probe's first capture once loading is done, so it sees the whole world and not part of it
	void ISceneLoadingEvents.AfterLoad( Scene scene )
	{
		if ( Application.IsHeadless || Mode != EnvmapProbeMode.Runtime || !owed ) return;

		Capture( wait: true );
	}

	//Draws the six faces now or a face a tick; not callable from inside a frame
	private bool Capture( bool wait = false )
	{
		owed = false;
		queuedFrames = 0;
		queuedTime = 0;
		capturedAt = RealTime.Now;

		if ( bouncesLeft > 0 ) bouncesLeft--;

		return Scene.IsValid() && CoreEnvmap.Capture( Scene, this, wait );
	}

	protected override void OnUpdate()
	{
		if ( Mode != EnvmapProbeMode.Runtime || !CoreNative.Available ) return;

		//A capture under way draws a face a tick
		if ( CoreEnvmap.Capturing( this ) )
		{
			CoreEnvmap.Step( Scene, this );
			return;
		}

		queuedFrames++;
		queuedTime += Time.Delta;

		if ( !owed ) owed = Due();
		if ( !owed ) return;

		//Each capture is tens of milliseconds, and probes take turns - see CoreEnvmap.MayCapture
		if ( RealTime.Now - capturedAt < LeastBetweenCaptures || !CoreEnvmap.MayCapture ) return;

		Capture();
	}

	private bool Due() => UpdateStrategy switch
	{
		CubemapDynamicUpdate.EveryFrame => true,
		CubemapDynamicUpdate.FrameInterval => queuedFrames > FrameInterval,
		CubemapDynamicUpdate.TimeInterval => queuedTime > DelayBetweenUpdates,
		_ => bouncesLeft > 0,
	};
}
