using Rinten.UI;
using Rinten.VR;

namespace Rinten;

//In VR it becomes a sheet floating in front of the head instead
/// <summary>Renders any attached PanelComponents to the screen. Acts as the root for all your UI components.</summary>
[Title( "Screen Panel" )]
[Category( "UI" )]
[Icon( "desktop_windows" )]
[EditorHandle( "materials/gizmo/ui.png" )]
[Alias( "PanelRoot" )]
public sealed class ScreenPanel : Component, IRootPanelComponent
{
	[Property, Range( 0, 1 )] public float Opacity { get; set; } = 1.0f;
	[Property, Range( 0, 5 )] public float Scale { get; set; } = 1.0f;
	[Property] public bool AutoScreenScale { get; set; } = true;
	[Property, ShowIf( "AutoScreenScale", true )] public AutoScale ScaleStrategy { get; set; }
	[Property] public int ZIndex { get; set; } = 100;
	[Property] public CameraComponent TargetCamera { get; set; }

	/// <summary>How the sheet the panel becomes in a headset keeps up with the head.</summary>
	public enum HeadsetFollow
	{
		/// <summary>Stays where it was put until the head has turned well away from it, then swings round after it - level, at eye height. What a menu or a HUD wants: it can be looked around, and it is never lost.</summary>
		Lazy,

		/// <summary>Fixed to the head: always straight ahead, whichever way it turns.</summary>
		Head,

		/// <summary>Put in front of the head when the headset is put on, and left there.</summary>
		World,
	}

	/// <summary>How the panel keeps up with the head in a headset.</summary>
	[Property, Category( "VR" )] public HeadsetFollow VrFollow { get; set; } = HeadsetFollow.Lazy;

	/// <summary>How far in front of the eyes the sheet hangs, in metres.</summary>
	[Property, Category( "VR" ), Range( 0.3f, 5.0f )] public float VrDistance { get; set; } = 1.6f;

	/// <summary>How wide the sheet is, in metres. Its height follows from <see cref="VrCanvas"/>.</summary>
	[Property, Category( "VR" ), Range( 0.2f, 5.0f )] public float VrWidth { get; set; } = 1.6f;

	/// <summary>How many pixels the panel is laid out on in a headset, which stands in for the screen's size: what a style's pixels, percentages and the auto scale are measured against. 1920 by 1080 lays a panel out as it is on a 1080p screen.</summary>
	[Property, Category( "VR" )] public Vector2 VrCanvas { get; set; } = new( 1920, 1080 );

	/// <summary>How far below eye height the middle of the sheet is, in metres. Nought is straight ahead.</summary>
	[Property, Category( "VR" ), Range( -1.0f, 1.0f )] public float VrDrop { get; set; } = 0.1f;

	/// <summary>For <see cref="HeadsetFollow.Lazy"/>: how far, in degrees, the head may turn from the sheet before it follows.</summary>
	[Property, Category( "VR" ), Range( 0.0f, 90.0f )] public float VrFollowAngle { get; set; } = 35.0f;

	private GameRootPanel rootPanel;

	//Which way the sheet faces now, about the vertical, and whether it has been placed yet.
	private float sheetYaw;
	private bool sheetPlaced;
	private Transform sheetWorld;

	public enum AutoScale
	{
		/// <summary>The height is scaled on the assumptiuon that we're always 1080p</summary>
		ConsistentHeight,

		/// <summary>We use the same scaling as the desktop</summary>
		FollowDesktopScaling,
	}

	protected override void OnValidate()
	{
		if ( Scale < 0.001f ) Scale = 0.001f;
	}

	protected override void OnAwake()
	{
		rootPanel = new GameRootPanel();
		rootPanel.GameObject = GameObject;
		rootPanel.RenderedManually = true;
		rootPanel.Style.Display = DisplayMode.None;

		OnUpdate();
	}

	protected override void OnEnabled()
	{
		if ( !rootPanel.IsValid() )
			return;

		//todo RootPanel enable
		rootPanel.Style.Display = DisplayMode.Flex;

		EnsurePanelParents();
	}

	void EnsurePanelParents()
	{
		Components.ExecuteEnabledInSelfAndDescendants<PanelComponent>( c => c.EnsureParentPanel() );
	}

	protected override void OnDisabled()
	{
		//todo disable rootpanel
		if ( !rootPanel.IsValid() )
			return;

		rootPanel.Style.Display = DisplayMode.None;
	}

	protected override void OnDestroy()
	{
		rootPanel?.Delete();
		rootPanel = null;
	}

	public Panel GetPanel()
	{
		return rootPanel;
	}

	protected override void OnUpdate()
	{
		if ( !rootPanel.IsValid() )
			return;

		rootPanel.Style.ZIndex = ZIndex;
		rootPanel.Style.Opacity = Opacity;
		rootPanel.AutoScale = AutoScreenScale;
		rootPanel.ManualScale = Scale;
		rootPanel.ScaleStrategy = ScaleStrategy;
		rootPanel.Canvas = new Vector2( MathF.Max( VrCanvas.x, 64 ), MathF.Max( VrCanvas.y, 64 ) );
	}

	//Where the sheet is this frame, from the head the frame is drawn from - so a sheet fixed to the head is fixed to it exactly, not a frame behind it.
	protected override void OnPreRender()
	{
		if ( !rootPanel.IsValid() ) return;

		var inHeadset = Game.IsRunningInVR && Input.VR is not null;
		rootPanel.InHeadset = inHeadset;
		rootPanel.IsVR = inHeadset;

		if ( !inHeadset )
		{
			//Put in front of the head afresh the next time the headset is put on.
			sheetPlaced = false;
			return;
		}

		var head = Input.VR.Head;
		var forward = head.Rotation.Forward;

		//Level: the sheet turns about the vertical and never rolls or tips with the head - a sheet that tilts with every nod cannot be read.
		var flat = forward.WithY( 0 );
		var yaw = flat.Length > 0.01f ? MathF.Atan2( flat.x, -flat.z ) : sheetYaw;

		Vector3 middle;
		Rotation facing;

		if ( VrFollow == HeadsetFollow.Head )
		{
			middle = head.Position + forward * VrDistance - head.Rotation.Up * VrDrop;
			facing = head.Rotation;
		}
		else
		{
			if ( !sheetPlaced )
			{
				sheetYaw = yaw;
				sheetPlaced = true;
				sheetWorld = default;
			}

			if ( VrFollow == HeadsetFollow.Lazy )
			{
				//Behind by more than the angle: swing round after the head, quickly at first and slowing as it arrives, until it is back in front.
				var off = MathF.IEEERemainder( yaw - sheetYaw, MathF.Tau );

				if ( MathF.Abs( off ) > VrFollowAngle.DegreeToRadian() || following )
				{
					following = MathF.Abs( off ) > 0.02f;
					sheetYaw += off * MathF.Min( 1.0f, RealTime.Delta * 4.0f );
				}
			}

			var along = new Vector3( MathF.Sin( sheetYaw ), 0, -MathF.Cos( sheetYaw ) );

			if ( VrFollow == HeadsetFollow.World && sheetWorld != default )
			{
				middle = sheetWorld.Position;
				facing = sheetWorld.Rotation;
			}
			else
			{
				middle = head.Position + along * VrDistance - Vector3.Up * VrDrop;
				facing = Rotation.LookAt( along, Vector3.Up );

				if ( VrFollow == HeadsetFollow.World ) sheetWorld = new Transform( middle, facing );
			}
		}

		//A canvas pixel is ScreenToWorldScale metres at scale one - see ScenePanelObject - so the scale is what makes the canvas VrWidth across.
		var canvas = rootPanel.Canvas;
		var scale = VrWidth / ( canvas.x * ScenePanelObject.ScreenToWorldScale );

		//Facing away from the eye, which is the side a panel's face is on - see WorldPanel.OnPreRender.
		rootPanel.Sheet = new Transform( middle, facing, scale );
	}

	//Whether a lazy sheet is still on its way round after the head.
	private bool following;
}

class GameRootPanel : RootPanel
{
	public bool AutoScale = true;
	public float ManualScale = 1.0f;
	public ScreenPanel.AutoScale ScaleStrategy;

	override public bool IsWorldPanel => false;

	//The size the panel is laid out at in a headset - see ScreenPanel.VrCanvas.
	internal Vector2 Canvas = new( 1920, 1080 );

	//Where the sheet is in the world: its middle, facing away from the eyes, scaled from canvas pixels.
	internal Transform Sheet = Transform.Zero;

	//In a headset the canvas stands in for the screen: laid out around the sheet's middle, the way a world panel is around its object.
	protected override void UpdateBounds( Rect rect )
	{
		if ( InHeadset )
		{
			PanelBounds = new Rect( -Canvas.x * 0.5f, -Canvas.y * 0.5f, Canvas.x, Canvas.y );
			return;
		}

		base.UpdateBounds( rect );
	}

	/// <summary>Where a controller's ray meets the sheet, in canvas pixels - the same arithmetic a world panel's is, through the sheet's transform. Nothing outside a headset: there the mouse is the pointer.</summary>
	public override bool RayToLocalPosition( Ray ray, out Vector2 position, out float distance )
	{
		position = default;
		distance = 0;

		if ( !InHeadset ) return false;

		var plane = new Plane( Sheet.Position, Sheet.Rotation.Backward );
		var hit = plane.Trace( ray, false, 100.0f );

		if ( !hit.HasValue ) return false;

		distance = Vector3.DistanceBetween( hit.Value, ray.Position );

		var local3 = Sheet.PointToLocal( hit.Value );
		var local = new Vector2( local3.x, -local3.y ) * ( 1.0f / ScenePanelObject.ScreenToWorldScale );

		if ( !IsInside( local ) ) return false;

		//Panels on the one sheet are the same distance away: the one drawn on top is the one a ray finds first - see ZIndex.
		distance -= ( ComputedStyle?.ZIndex ?? 0 ) * 0.0001f;

		position = local;
		return true;
	}

	protected override void UpdateScale( Rect screenSize )
	{
		if ( AutoScale )
		{
			if ( ScaleStrategy == ScreenPanel.AutoScale.ConsistentHeight )
			{
				Scale = screenSize.Height / 1080.0f;
			}
			else if ( ScaleStrategy == ScreenPanel.AutoScale.FollowDesktopScaling && !InHeadset )
			{
				Scale = Screen.DesktopScale;

				var minimumHeight = 1080.0f * Screen.DesktopScale;

				//If the screen height is less than 1080, it's less than supported so scale the screen size down.
				if ( screenSize.Height < minimumHeight )
				{
					Scale *= screenSize.Height / minimumHeight;
				}
			}

			//A headset's canvas has no desktop scale: laid out as a 1080p screen, whichever way the screen would have been.
			if ( InHeadset && ScaleStrategy == ScreenPanel.AutoScale.FollowDesktopScaling )
			{
				Scale = screenSize.Height / 1080.0f;
			}

			Scale *= ManualScale;
		}
		else
		{
			Scale = ManualScale;
		}
	}
}
