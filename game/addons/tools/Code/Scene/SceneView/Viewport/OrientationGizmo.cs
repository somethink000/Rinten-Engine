namespace Editor;

//What the orientation gizmo needs from the viewport it sits in the corner of.
internal interface IOrientationGizmoHost
{
	CameraComponent Camera { get; }

	Gizmo.Instance GizmoInstance { get; }

	//How big the picture is, in its own pixels.
	Vector2 SurfaceSize { get; }

	//Where the pointer is in that picture.
	Vector2 MousePosition { get; }

	//Whether the window this is drawn in is the one the desktop is pointing at.
	bool IsActiveWindow { get; }

	void SetCursor( CursorShape shape );

	void FocusSurface();
}

//The ball of axes in the top right of a viewport. Clicking a handle looks down that axis; dragging the body
//orbits. It takes the mouse before the scene does, so a click meant for it never also picks something behind it.
internal sealed class OrientationGizmo
{
	//World directions for the six handles, ordered X+, X-, Y+, Y-, Z+, Z-; +X right, +Y up, +Z back
	private static readonly Vector3[] Axes =
	{
		new( 1, 0, 0 ), new( -1, 0, 0 ), //X+, X-
		new( 0, 1, 0 ), new( 0, -1, 0 ), //Y+, Y-
		new( 0, 0, 1 ), new( 0, 0, -1 ), //Z+, Z-
	};

	private static readonly string[] AxisLabels = { "X", "X", "Y", "Y", "Z", "Z" };

	private static Color AxisColor( int axis ) => (axis / 2) switch
	{
		0 => Color.FromBytes( 226, 84, 84 ),  //X - red
		1 => Color.FromBytes( 130, 200, 80 ), //Y - green
		_ => Color.FromBytes( 74, 140, 226 ), //Z - blue
	};

	private readonly IOrientationGizmoHost host;
	private readonly ViewportCamera camera;
	private readonly ViewportState state;

	private bool hovered;
	private int hoveredAxis = -1;
	private bool dragging;
	private bool mouseDown;
	private bool leftWasDown;
	private bool cameraDragWasActive;
	private Vector2 pressPos;
	private Vector2 pressCursor;

	public OrientationGizmo( IOrientationGizmoHost host, ViewportCamera camera, ViewportState state )
	{
		this.host = host;
		this.camera = camera;
		this.state = state;
	}

	//A viewport's pixels are its own, so nothing is scaled by anything.
	private const float Dpi = 1.0f;

	private float Radius => 30f * Dpi;
	private float BallRadius => 7f * Dpi;

	private Vector2 Center
	{
		get
		{
			var inset = Radius + BallRadius + 20f * Dpi;
			return new Vector2( host.SurfaceSize.x - inset, inset );
		}
	}

	//Screen direction and depth of a world axis; camera axes are +X right, +Y up, +Z back
	private Vector2 AxisScreenDir( Vector3 worldDir, out float depth )
	{
		var local = host.Camera.WorldRotation.Inverse * worldDir;
		depth = -local.z;
		return new Vector2( local.x, -local.y );
	}

	private Vector3 ComputeOrbitPivot()
	{
		var pos = host.Camera.WorldPosition;
		var fwd = host.Camera.WorldRotation.Forward;

		var plane = new Plane( Vector3.Up, 0f );
		if ( plane.TryTrace( new Ray( pos, fwd ), out var hit, twosided: true, maxDistance: 12.7f ) )
			return hit;

		return (pos + fwd * (12.7f)).WithY( 0f );
	}

	/// <summary>Whether the gizmo took the mouse this frame, and so the scene must not.</summary>
	public bool Update( bool hasMouseFocus )
	{
		if ( !host.Camera.IsValid() )
			return false;

		var center = Center;
		var radius = Radius;
		var ballRadius = BallRadius;
		var mouse = host.MousePosition;

		//Pick the front-most handle under the cursor, but not while dragging
		hoveredAxis = -1;
		var pickDist = ballRadius * 1.6f;
		var bestDepth = float.MaxValue;
		if ( !dragging )
		{
			for ( int i = 0; i < Axes.Length; i++ )
			{
				var pos = center + AxisScreenDir( Axes[i], out var depth ) * radius;
				var d = Vector2.DistanceBetween( mouse, pos );
				if ( d <= pickDist && depth < bestDepth )
				{
					bestDepth = depth;
					hoveredAxis = i;
				}
			}
		}

		var overBody = Vector2.DistanceBetween( mouse, center ) <= radius + ballRadius;
		var interacting = mouseDown || dragging;
		var cameraDragActive = SceneEditorExtensions.IsControllingCamera;
		var suppressHover = cameraDragActive || cameraDragWasActive;
		cameraDragWasActive = cameraDragActive;

		hovered = host.IsActiveWindow && !suppressHover && (interacting || overBody || hoveredAxis >= 0);

		var leftDown = ( Application.MouseButtons & MouseButtons.Left ) != 0;

		//Begin interaction only on a direct click on the gizmo, so a scene drag that happens to pass over doesn't take over
		if ( leftDown && !leftWasDown && !mouseDown && hovered )
		{
			mouseDown = true;
			dragging = false;
			pressPos = mouse;
			pressCursor = Application.UnscaledCursorPosition;
		}

		leftWasDown = leftDown;

		if ( mouseDown )
		{
			if ( leftDown )
			{
				if ( !dragging && Vector2.DistanceBetween( mouse, pressPos ) > 4f * Dpi )
					dragging = true;

				//Hidden but not held in place: a warp every frame turns into relative mode on Wayland, and the delta goes to zero
				if ( dragging )
				{
					OrbitAroundPivot();
					host.SetCursor( CursorShape.Blank );
				}
			}
			else
			{
				//Released without dragging over a handle -> snap to that axis view.
				if ( !dragging && hoveredAxis >= 0 )
					SnapToAxis( hoveredAxis );

				//Shown again where the drag began, over the gizmo, so hover resumes
				if ( dragging )
				{
					host.SetCursor( CursorShape.None );
					Application.UnscaledCursorPosition = pressCursor;
				}

				mouseDown = false;
				dragging = false;
			}
		}

		return hovered || mouseDown || dragging;
	}

	private void OrbitAroundPivot()
	{
		camera.EnterPerspective();

		var delta = Application.CursorDelta * 0.1f;
		var distance = camera.OrbitDistance;

		host.GizmoInstance.OrbitCameraAroundPivot( host.Camera, delta, ref distance );

		camera.OrbitDistance = distance;

		state.CameraRotation = host.Camera.WorldRotation;
		state.CameraPosition = host.Camera.WorldPosition;

		host.FocusSurface();
	}

	private void SnapToAxis( int axis )
	{
		var axisDir = Axes[axis];
		var gizmoPivot = ComputeOrbitPivot();

		//If we're already looking down this axis, toggle between ortho/perspective
		var alreadyAligned = Vector3.Dot( state.CameraRotation.Forward, -axisDir ) > 0.999f;
		var currentlyOrtho = camera.GizmoOrthoActive || state.Is2D;
		if ( alreadyAligned && currentlyOrtho )
		{
			camera.EnterPerspective();
			return;
		}

		var distance = (state.CameraPosition - gizmoPivot).Length;
		//Metres
		if ( distance < 1f )
			distance = 10f;

		//Pick an up vector for vertical views so that world +X is always on the right
		Vector3 up;
		if ( axisDir.y > 0.5f ) up = Vector3.Forward;
		else if ( axisDir.y < -0.5f ) up = Vector3.Backward;
		else up = Vector3.Up;

		camera.EnterOrtho( axisDir, up, gizmoPivot, distance );
	}

	public void Draw()
	{
		if ( !host.Camera.IsValid() )
			return;

		var center = Center;
		var radius = Radius;
		var ballRadius = BallRadius;

		//Screen shapes draw in call order through the gizmo scope; nothing is kept between frames
		using var scope = Gizmo.Scope( "orientation-gizmo" );

		var draw = Gizmo.Draw;

		if ( hovered )
		{
			var bg = radius + ballRadius + 4f * Dpi;
			draw.ScreenCircle( center, bg, Color.Black.WithAlpha( 0.1f ) );
		}

		Span<int> order = stackalloc int[Axes.Length];
		Span<float> depths = stackalloc float[Axes.Length];
		Span<Vector2> positions = stackalloc Vector2[Axes.Length];

		for ( int i = 0; i < Axes.Length; i++ )
		{
			positions[i] = center + AxisScreenDir( Axes[i], out var depth ) * radius;
			depths[i] = depth;
			order[i] = i;
		}

		//Back-to-front sort so closer handles draw on top.
		for ( int i = 1; i < order.Length; i++ )
		{
			var key = order[i];
			int j = i - 1;
			while ( j >= 0 && depths[order[j]] < depths[key] )
			{
				order[j + 1] = order[j];
				j--;
			}
			order[j + 1] = key;
		}

		foreach ( var i in order )
		{
			var positive = (i % 2) == 0;
			var color = AxisColor( i );
			var pos = positions[i];

			//Fade handles that are further back
			var facing = depths[i].Remap( 1f, -1f, 0.5f, 1f, true );
			var isHovered = hoveredAxis == i;

			//Positive axes get a solid line, negative axes a thinner dashed line. Hovered axes are highlighted.
			var lineColor = (isHovered ? Color.Lerp( color, Color.White, 0.5f ) : color)
				.WithAlpha( facing * (positive ? (isHovered ? 1f : 0.8f) : (isHovered ? 0.9f : 0.45f)) );
			var lineThickness = (positive ? (isHovered ? 2.5f : 2f) : (isHovered ? 2f : 1.5f)) * Dpi;

			if ( positive )
			{
				draw.ScreenLine( center, pos, lineThickness, lineColor );

				draw.ScreenCircle( pos, ballRadius, color.WithAlpha( facing ), isHovered ? Color.White : default, isHovered ? 1.5f * Dpi : 0f );

				draw.Color = Color.Black.WithAlpha( facing );
				draw.ScreenText( AxisLabels[i], pos, "Roboto", 10f * Dpi, TextFlag.Center );
			}
			else
			{
				DrawDashedLine( draw, center, pos, lineThickness, 4f * Dpi, 3f * Dpi, lineColor );
				draw.ScreenCircle( pos, ballRadius, Color.Black.WithAlpha( facing * 0.5f ), (isHovered ? Color.White : color).WithAlpha( facing ), 1.5f * Dpi );
			}
		}
	}

	private static void DrawDashedLine( Gizmo.GizmoDraw draw, Vector2 a, Vector2 b, float thickness, float dash, float gap, Color color )
	{
		var delta = b - a;
		var length = delta.Length;
		if ( length <= 0f )
			return;

		var dir = delta / length;
		for ( var pos = 0f; pos < length; pos += dash + gap )
		{
			var segStart = a + dir * pos;
			var segEnd = a + dir * MathF.Min( pos + dash, length );
			draw.ScreenLine( segStart, segEnd, thickness, color );
		}
	}
}
