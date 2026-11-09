using System;

namespace Editor;

//The camera one viewport looks through. Where it is, how wide it sees, and the blend between a flat view and a perspective
//one. It knows nothing about the mouse or about tools: the viewport hands it the decisions and this works out the transform.
internal sealed class ViewportCamera
{
	//todo: replace with proper scene bounds calcs? The diagonal of a large fixed-size
	//world, in metres, so an ortho view never clips what is behind it.
	private static readonly float MassiveZFar = 1.7321f * 416.05f;

	private static float TransitionSpeed => 40;

	private readonly ViewportState state;
	private readonly Func<Vector2> surfaceSize;

	private float currentFov;
	private float targetFov = 80;
	private float transitionBlend;
	private bool was2d;

	private bool orthoSnap;
	private Gizmo.GridAxis? lastGridAxis;

	private Vector3 velocity;

	public ViewportCamera( ViewportState state, Func<Vector2> surfaceSize )
	{
		this.state = state;
		this.surfaceSize = surfaceSize;
	}

	/// <summary>The camera being driven, which the viewport swaps when the scene does.</summary>
	public CameraComponent Active { get; set; }

	/// <summary>If assigned, copy FoV / ZNear / ZFar etc from this camera.</summary>
	public CameraComponent SourceCamera { get; set; }

	/// <summary>Where the camera is flying to, or nothing when it is being flown by hand.</summary>
	public Vector3? TargetPosition { get; set; }

	/// <summary>How far an ALT+drag orbits around what is in front of the camera.</summary>
	public float OrbitDistance { get; set; } = 5.08f;

	/// <summary>The ortho height being drawn, which lags the one asked for while it blends.</summary>
	public float CurrentOrthoHeight { get; private set; }

	/// <summary>Whether snapping to an axis put the camera in ortho, without it being a 2D view.</summary>
	public bool GizmoOrthoActive { get; private set; }

	/// <summary>Whether the picture is drawn flat, however it got that way.</summary>
	public bool WantOrtho => state.Is2D || GizmoOrthoActive;

	public void ClearTarget()
	{
		TargetPosition = null;
		velocity = Vector3.Zero;
	}

	/// <summary>An ortho view read back from a cookie, already where it belongs.</summary>
	public void RestoreOrtho()
	{
		GizmoOrthoActive = true;
		orthoSnap = true;
	}

	/// <summary>Whatever the wheel did to the ortho height, taken as read rather than blended to.</summary>
	public void TakeOrthoHeight( float height )
	{
		state.CameraOrthoHeight = height;
		CurrentOrthoHeight = height;
	}

	//Gets a camera size value based on a distance and field of view.
	private static float SizeFromDistanceAndFieldOfView( float distance, float fov )
		=> 2.0f * distance * MathF.Tan( 0.5f * MathX.DegreeToRadian( fov ) );

	public void EnterPerspective()
	{
		var wasGizmoOrtho = GizmoOrthoActive && !state.Is2D;

		GizmoOrthoActive = false;

		if ( wasGizmoOrtho && lastGridAxis is { } axis )
		{
			state.GridAxis = axis;
			lastGridAxis = null;
		}

		if ( state.Is2D )
			state.View = ViewportViewMode.Perspective;

		TargetPosition = null;
	}

	public void EnterOrtho( Vector3 axisDir, Vector3 up, Vector3 pivot, float distance )
	{
		if ( !state.Is2D && !GizmoOrthoActive )
		{
			lastGridAxis = state.GridAxis;
		}

		state.CameraRotation = Rotation.LookAt( -axisDir, up );
		state.CameraPosition = pivot + axisDir * distance;
		state.CameraOrthoHeight = SizeFromDistanceAndFieldOfView( distance, EditorPreferences.CameraFieldOfView );
		state.GridAxis = ViewportState.GridAxisForDirection( axisDir );

		Active.WorldRotation = state.CameraRotation;
		Active.WorldPosition = state.CameraPosition;

		GizmoOrthoActive = true;
		orthoSnap = true;
		TargetPosition = null;
	}

	/// <summary>Returns the distance the camera needs to be from the target to fit the ortho height in the view at the given FOV.</summary>
	public float GetDollyDistance( float fovDegrees, float orthoHeight )
	{
		float fovRad = MathX.DegreeToRadian( fovDegrees );
		float halfAngle = MathF.Max( fovRad / 2, 0.0001f );
		var aspect = Active.ScreenRect.Width / Active.ScreenRect.Height;
		return (orthoHeight * aspect) / (2f * MathF.Tan( halfAngle ));
	}

	/// <summary>Writes everything this viewport has decided onto the camera, and moves it a frame's worth towards where it is going.</summary>
	public void Update()
	{
		if ( !Active.IsValid() ) return;

		var source = SourceCamera.IsValid() ? SourceCamera : null;

		Active.BackgroundColor = source?.BackgroundColor ?? EditorPreferences.CameraBackgroundColor;
		Active.WorldPosition = state.CameraPosition;
		Active.WorldRotation = state.CameraRotation;

		bool wantOrtho = source?.Orthographic ?? WantOrtho;

		//Snapping to an axis view enters ortho instantly
		if ( orthoSnap )
		{
			transitionBlend = 1f;
			currentFov = 1f;
			targetFov = 1f;
			CurrentOrthoHeight = state.CameraOrthoHeight;
			was2d = true;
			orthoSnap = false;
		}

		//Smooth transition between ortho and perspective
		if ( wantOrtho )
		{
			//todo: fog?
			if ( !was2d )
			{
				float distance = (Active.WorldRotation.Forward * state.CameraPosition).Length;
				CurrentOrthoHeight = SizeFromDistanceAndFieldOfView( distance, currentFov );
				targetFov = 1f; //Going lower than that makes it more error prone depth precision wise
			}
		}
		else
		{
			targetFov = EditorPreferences.CameraFieldOfView;
		}

		//Small value to make the jump to ortho less jarring in low precision
		if ( transitionBlend.AlmostEqual( 0f, 0.000001f ) ) transitionBlend = 0;
		if ( transitionBlend.AlmostEqual( 1f, 0.000001f ) ) transitionBlend = 1;

		//Transition is [ 0, 1 ] or [ 1, 0 ] depending on going to or from 2D
		transitionBlend = wantOrtho ?
			MathX.Lerp( transitionBlend, 1f, RealTime.Delta * TransitionSpeed ) :
			MathX.Lerp( transitionBlend, 0f, RealTime.Delta * TransitionSpeed );

		Active.OrthographicHeight = CurrentOrthoHeight;

		currentFov = MathX.Lerp( currentFov, targetFov, RealTime.Delta * TransitionSpeed );
		CurrentOrthoHeight = MathX.Lerp( CurrentOrthoHeight, state.CameraOrthoHeight, RealTime.Delta * TransitionSpeed );
		was2d = wantOrtho;

		Active.DebugMode = state.RenderMode;
		Active.WireframeMode = state.WireframeMode;
		Active.CustomSize = surfaceSize();

		//Looking through a scene camera - piloting it, see CameraEditorTool - so the
		//view is what that camera sees rather than what the editor's preferences say.
		if ( source is not null )
		{
			Active.ClearFlags = source.ClearFlags;
			Active.ZNear = source.ZNear;
			Active.ZFar = source.ZFar;
			Active.FieldOfView = source.FieldOfView;
			Active.FovAxis = source.FovAxis;
			Active.EnablePostProcessing = source.EnablePostProcessing;
			Active.Orthographic = source.Orthographic;
			Active.OrthographicHeight = source.OrthographicHeight;
			Active.OverlayOnly = false;
			Active.RenderExcludeTags.Set( "skybox", false );

			MoveTowardsTarget();
			return;
		}

		Active.ClearFlags = ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil;
		Active.ZNear = EditorPreferences.CameraZNear;
		Active.ZFar = wantOrtho ? MassiveZFar : EditorPreferences.CameraZFar;
		Active.FieldOfView = currentFov;
		Active.FovAxis = CameraComponent.Axis.Horizontal;
		Active.EnablePostProcessing = !state.Is2D && state.EnablePostProcessing;
		Active.Orthographic = wantOrtho;
		Active.OrthographicHeight = CurrentOrthoHeight;
		Active.OverlayOnly = state.Is2D;

		if ( state.Is2D )
		{
			transitionBlend = 1f;
			currentFov = 1f;
			targetFov = 1f;
			Active.RenderExcludeTags.Set( "skybox", true );
		}
		else
		{
			Active.RenderExcludeTags.Set( "skybox", false );
		}

		MoveTowardsTarget();
	}

	private void MoveTowardsTarget()
	{
		if ( TargetPosition is null ) return;

		var currentPos = Active.WorldPosition;
		var targetPos = TargetPosition.Value;

		//If camera position is fucked, just jump to target
		if ( currentPos.IsNaN || currentPos.IsInfinity )
		{
			Active.WorldPosition = targetPos;
			ClearTarget();
			return;
		}

		//If target is fucked, just ignore it
		if ( targetPos.IsNaN || targetPos.IsInfinity )
		{
			ClearTarget();
			return;
		}

		//If we're at a crazy distance, jump a bit closer first before smoothing
		var distance = targetPos.Distance( currentPos );
		if ( distance > 1e5f )
		{
			var direction = (targetPos - currentPos).Normal;
			Active.WorldPosition = targetPos - direction * 1e5f;
			currentPos = Active.WorldPosition;
			distance = targetPos.Distance( currentPos );
		}

		Active.WorldPosition = Vector3.SmoothDamp( currentPos, targetPos, ref velocity, 0.3f, RealTime.Delta );

		if ( distance < 0.1f )
		{
			TargetPosition = null;
		}
	}

	/// <summary>Pulls the camera back far enough that a flat view has everything in front of it, and part of the way there while the two are blending.</summary>
	public void ApplyViewOffset()
	{
		if ( !Active.IsValid() ) return;

		if ( WantOrtho )
		{
			Vector3 viewOffset = Active.WorldRotation.Forward;
			if ( transitionBlend > 0f && transitionBlend < 1f )
			{
				//Dolly out when transitioning to ortho
				float dolly = GetDollyDistance( currentFov, Active.OrthographicHeight );
				viewOffset *= MathF.Min( dolly, EditorPreferences.CameraZFar ) * transitionBlend;
			}
			else
			{
				//Keep ortho clipping planes predictable
				viewOffset *= Active.ZFar / 2;
			}

			Active.WorldPosition -= viewOffset;
		}
		else if ( transitionBlend > 0f && transitionBlend < 1f )
		{
			//Dolly in when transitioning to perspective
			float dolly = GetDollyDistance( currentFov, Active.OrthographicHeight );
			Active.WorldPosition -= Active.WorldRotation.Forward * MathF.Min( dolly, EditorPreferences.CameraZFar ) * transitionBlend;
		}
	}

	//Puts the camera where the whole of a box is in front of it. The closest framing puts the camera to what it frames, in metres.
	private const float MinFrameDistance = 0.5f;

	//The furthest, in metres.
	private const float MaxFrameDistance = 100.0f;

	public void FrameOn( BBox target )
	{
		if ( !Active.IsValid() ) return;

		//Make sure the camera transform is up to date
		Active.WorldPosition = state.CameraPosition;
		Active.WorldRotation = state.CameraRotation;

		if ( state.Is2D )
		{
			FrameFlat( target );
			return;
		}

		//Radius is half the box diagonal, fitted to the narrower FOV, then tightened to 3/4
		var aspect = Active.ScreenRect.Height > 0f
			? Active.ScreenRect.Width / Active.ScreenRect.Height
			: 1.0f;

		var verticalFov = 2.0f * MathF.Atan( MathF.Tan( MathX.DegreeToRadian( Active.FieldOfView ) * 0.5f ) / aspect );

		var distance = MathX.SphereCameraDistance( target.Size.Length * 0.5f, MathX.RadianToDegree( verticalFov ) ) * 0.75f;

		//Never closer than half a metre, whatever is small enough to want it, and never further than a
		//hundred: past that a level or a terrain is a dot on the horizon rather than something framed.
		distance = Math.Clamp( distance, MinFrameDistance, MaxFrameDistance );

		TargetPosition = target.Center + distance * Active.WorldRotation.Backward;
		OrbitDistance = target.Center.Distance( TargetPosition.Value );
	}

	private void FrameFlat( BBox target )
	{
		var viewForward = Active.WorldRotation.Forward;
		var size = target.Size;
		//Across and up the screen, in the views SetViewmode makes: from above that is X and Z, from the front X and Y, from the side Z and Y.
		var viewSize = state.View switch
		{
			ViewportViewMode.Top2d => new Vector2( size.x, size.z ),
			ViewportViewMode.Front2d => new Vector2( size.x, size.y ),
			ViewportViewMode.Side2d => new Vector2( size.z, size.y ),
			ViewportViewMode.Flat2d => new Vector2( size.x, size.y ),
			_ => new Vector2( size.x, size.y )
		};

		var renderSize = surfaceSize();
		var aspect = MathF.Max( renderSize.y > 0f ? renderSize.x / renderSize.y : 1f, 0.0001f );
		var fitHeight = MathF.Max( viewSize.y, viewSize.x / aspect );

		TakeOrthoHeight( MathF.Max( 0.5f, fitHeight * 1.2f ) );

		var depthOffset = Vector3.Dot( target.Center - state.CameraPosition, viewForward );
		TargetPosition = target.Center - viewForward * depthOffset;
		OrbitDistance = target.Center.Distance( TargetPosition.Value );
	}
}
