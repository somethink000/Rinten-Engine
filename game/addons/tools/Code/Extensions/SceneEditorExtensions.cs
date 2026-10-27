namespace Editor;

public static class SceneEditorExtensions
{
	/// <summary>Helper to easily set up all of the inputs for this camera and widget. This is assuming that the passed in widget is the render panel.</summary>
	public static void UpdateInputs( this Gizmo.Instance self, SceneCamera camera, ISceneCanvas canvas = null, bool hasMouseFocus = true )
	{
		ArgumentNullException.ThrowIfNull( camera );

		self.Input.Camera = camera;
		self.Input.Modifiers = Application.KeyboardModifiers;

		if ( !hasMouseFocus )
		{
			self.Input.CursorRay = new Ray();
			return;
		}

		self.Input.CursorPosition = Application.CursorPosition;
		self.Input.LeftMouse = Application.MouseButtons.HasFlag( MouseButtons.Left );
		self.Input.RightMouse = Application.MouseButtons.HasFlag( MouseButtons.Right );

		if ( canvas is { IsCanvasValid: true } )
		{
			self.Input.CursorPosition -= canvas.CanvasScreenPosition;
			self.Input.CursorRay = camera.GetRay( self.Input.CursorPosition, canvas.CanvasSize );

			if ( !self.Input.IsHovered )
			{
				self.Input.LeftMouse = false;
				self.Input.RightMouse = false;
			}
		}
	}

	/// <summary>Locks the cursor to the scene picture. If we go outside it, this function will wrap the cursor around nicely.</summary>
	public static bool LockCursorToCanvas( ISceneCanvas canvas, int margin = 16 )
	{
		var rect = new Rect( 0, 0, canvas.CanvasSize.x, canvas.CanvasSize.y ).Shrink( margin );
		var pos = Application.CursorPosition - canvas.CanvasScreenPosition;

		if ( rect.IsInside( pos ) )
			return false;

		var newPos = new Vector2(
			pos.x < rect.Left ? rect.Right : pos.x > rect.Right ? rect.Left : pos.x,
			pos.y < rect.Top ? rect.Bottom : pos.y > rect.Bottom ? rect.Top : pos.y );

		//Within the window rather than across the desktop: the move is in the window's own units, and Wayland has no desktop to warp across
		Application.CursorPosition += newPos - pos;

		return true;
	}


	/// <summary>Wheel with right mouse held sets camera fly speed; checked first so modifiers don't block it</summary>
	public static bool AdjustCameraSpeed( this Gizmo.Instance self, CameraComponent camera )
	{
		ArgumentNullException.ThrowIfNull( camera );

		if ( !self.Input.IsHovered ) return false;
		if ( camera.Orthographic ) return false;

		var wheel = Application.MouseWheelDelta.y;
		if ( wheel == 0.0f ) return false;

		//Only while flying (right button held), so speed can change without letting go
		if ( !Application.MouseButtons.HasFlag( MouseButtons.Right ) ) return false;

		var speed = EditorPreferences.CameraSpeed;

		//Multiplicative step, so each wheel notch feels the same across the whole speed range
		speed *= MathF.Pow( 1.15f, Math.Sign( wheel ) );

		//Rounded to something somebody would say out loud, so the number beside the bar reads as a speed rather than as a measurement.
		speed = speed < 10.0f ? MathF.Round( speed, 2 ) : MathF.Round( speed, 1 );

		EditorPreferences.CameraSpeed = Math.Clamp( speed, 0.25f, 100.0f );

		//One turn of the wheel is one thing happening, so the dolly zoom below does not also get to act on it.
		Application.ConsumeMouseWheel();

		return true;
	}

	public static bool IsDraggingCamera => Application.MouseButtons.HasFlag( MouseButtons.Middle );

	public static bool IsPilotingCamera => Application.MouseButtons.HasFlag( MouseButtons.Right )
		|| EditorToolManager.CurrentModeName == CameraEditorTool.PilotModeName;

	public static bool IsControllingCamera => IsDraggingCamera || IsPilotingCamera;

	/// <summary>Helper to easily set up all of the inputs for this camera and widget. This is assuming that the passed in widget is the render panel.</summary>
	public static bool FirstPersonCamera( this Gizmo.Instance self, CameraComponent camera, ISceneCanvas canvas, bool lockCursor = false )
	{
		if ( Window.ModalBlocksInput )
			return false;

		ArgumentNullException.ThrowIfNull( camera );
		ArgumentNullException.ThrowIfNull( canvas );

		bool moved = false;
		var rightMouse = IsPilotingCamera;
		var middleMouse = IsDraggingCamera;

		//A flat view has nothing to look around with - there is no forward to turn - so the right button there does what the middle one does and pans. Held to
		//the rule everywhere else in the editor, it did nothing at all, and a view where dragging does nothing reads as one the mouse has stopped working in.
		var panning = middleMouse || (rightMouse && camera.Orthographic);

		if ( (rightMouse || middleMouse) && self.Input.IsHovered )
		{
			EditorShortcuts.AllowShortcuts = false;
			canvas.FocusCanvas();

			var delta = Application.CursorDelta * 0.1f;

			if ( lockCursor && LockCursorToCanvas( canvas ) )
				delta = Vector2.Zero;

			if ( self.ControlMode != "firstperson" )
			{
				EditorShortcuts.ReleaseAll();

				delta = 0;
				self.ControlMode = "firstperson";
				self.StompCursorPosition( Application.CursorPosition );
			}

			var moveSpeed = EditorPreferences.CameraSpeed;

			if ( EditorShortcuts.IsDown( "scene.movement-quick" ) ) moveSpeed *= 8.0f;
			if ( EditorShortcuts.IsDown( "scene.movement-slow" ) ) moveSpeed /= 8.0f;

			if ( rightMouse && !camera.Orthographic )
			{
				var sens = EditorPreferences.CameraSensitivity;
				var angles = camera.WorldRotation.Angles();

				angles.roll = 0;
				angles.yaw -= delta.x * sens;
				angles.pitch += delta.y * sens;
				angles.pitch = angles.pitch.Clamp( -89, 89 );

				//Updating camera angles is lossy on the backing quat, so don't shake the camera non stop
				if ( !delta.IsNearZeroLength )
					camera.WorldRotation = angles;

				if ( EditorPreferences.HideRotateCursor )
					canvas.SetCanvasCursor( CursorShape.Blank );
				else
					canvas.SetCanvasCursor( CursorShape.Cross );
			}
			else if ( panning )
			{
				var positionChange = new Vector3();

				//Perspective pans a fixed distance per pixel dragged; a unit is a metre, so five centimetres of it.
				float zoomModifierY = camera.Orthographic ? camera.OrthographicHeight / canvas.CanvasSize.y : 0.05f;
				float zoomModifierX = camera.Orthographic ? (camera.OrthographicHeight * (canvas.CanvasSize.x / canvas.CanvasSize.y)) / canvas.CanvasSize.x : 0.05f;

				positionChange += camera.WorldRotation.Right * Application.CursorDelta.x * zoomModifierX;
				positionChange += camera.WorldRotation.Down * Application.CursorDelta.y * zoomModifierY;

				if ( !EditorPreferences.CameraInvertPan )
					positionChange = -positionChange;

				camera.WorldPosition += positionChange;

				if ( EditorPreferences.HidePanCursor )
					canvas.SetCanvasCursor( CursorShape.Blank );
				else
					canvas.SetCanvasCursor( CursorShape.ClosedHand );
			}

			var move = Vector3.Zero;

			if ( EditorShortcuts.IsDown( "scene.move-forward" ) ) move += camera.WorldRotation.Forward;
			if ( EditorShortcuts.IsDown( "scene.move-backward" ) ) move += camera.WorldRotation.Backward;
			if ( EditorShortcuts.IsDown( "scene.move-left" ) ) move += camera.WorldRotation.Left;
			if ( EditorShortcuts.IsDown( "scene.move-right" ) ) move += camera.WorldRotation.Right;
			if ( EditorShortcuts.IsDown( "scene.move-down" ) ) move += Vector3.Down;
			if ( EditorShortcuts.IsDown( "scene.move-up" ) ) move += Vector3.Up;

			if ( !move.IsNearZeroLength )
			{
				//Moves directly with no smoothing, so the camera stops when the key is released
				camera.WorldPosition += move.Normal * RealTime.Delta * moveSpeed;
			}

			moved = true;
		}
		else
		{
			canvas.SetCanvasCursor( CursorShape.None );

			if ( self.ControlMode != "mouse" )
			{
				self.ControlMode = "mouse";
			}

			//if ( Scene.Settings.CursorMode != "mouse" )
			//{
			//	//Scene.Settings.CursorMode = "mouse";
			//}
		}

		//While the right button is down as well: flying and moving closer are the same thing to whoever
		//is doing them, and this used to be the one place in the view where the wheel did nothing at all.
		if ( self.Input.IsHovered && Math.Abs( Application.MouseWheelDelta.y ) > 0.001f )
		{
			if ( camera.Orthographic )
			{
				if ( ZoomOrthographic( camera, canvas ) )
					moved = true;
			}
			else
			{
				//Metres the camera walks forward per notch of the wheel.
				const float dollySpeed = 0.5f;

				camera.WorldPosition += camera.WorldRotation.Forward * Application.MouseWheelDelta.y * dollySpeed;
				moved = true;

				//One notch is one thing happening, whoever asks next.
				Application.ConsumeMouseWheel();
			}
		}

		return moved;
	}

	/// <summary>Wheel zoom for an orthographic camera, toward the cursor.</summary>
	public static bool ZoomOrthographic( CameraComponent camera, ISceneCanvas canvas )
	{
		if ( !camera.IsValid() || canvas is not { IsCanvasValid: true } ) return false;
		if ( Math.Abs( Application.MouseWheelDelta.y ) <= 0.001f ) return false;

		//Zooms by a share of the visible view per notch, so it feels the same at any scale
		const float perNotch = 0.12f;
		const float minHeight = 0.05f;
		const float maxHeight = 4096.0f;

		var canvasCursor = Application.CursorPosition - canvas.CanvasScreenPosition;
		Vector3 worldBefore = camera.ScreenToWorld( canvasCursor );

		camera.OrthographicHeight *= MathF.Pow( 1.0f - perNotch, Application.MouseWheelDelta.y );
		camera.OrthographicHeight = camera.OrthographicHeight.Clamp( minHeight, maxHeight );

		Vector3 worldAfter = camera.ScreenToWorld( canvasCursor );
		camera.WorldPosition -= worldAfter - worldBefore;

		Application.ConsumeMouseWheel();
		return true;
	}

	/// <summary>Orbit the camera around a point into the distance.</summary>
	public static bool OrbitCamera( this Gizmo.Instance self, CameraComponent camera, ISceneCanvas canvas, ref float distance )
	{
		if ( Window.ModalBlocksInput )
			return false;

		var leftMouse = Application.MouseButtons.HasFlag( MouseButtons.Left );
		var rightMouse = Application.MouseButtons.HasFlag( MouseButtons.Right );

		if ( !self.Input.IsHovered )
			return false;

		if ( !Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Alt ) )
			return false;

		if ( !leftMouse && !rightMouse )
			return false;

		canvas.FocusCanvas();

		var delta = Application.CursorDelta * 0.1f;

		if ( LockCursorToCanvas( canvas ) )
			delta = Vector2.Zero;

		if ( rightMouse )
		{
			var orbitPosition = camera.WorldPosition + camera.WorldRotation.Forward * distance;

			float zoomDelta = (delta.x + delta.y) * EditorPreferences.OrbitZoomSpeed;

			if ( EditorPreferences.InvertOrbitZoom )
				zoomDelta = -zoomDelta;

			if ( camera.Orthographic )
			{
				camera.OrthographicHeight += zoomDelta * (camera.OrthographicHeight / canvas.CanvasSize.y);
			}
			else
			{
				distance += zoomDelta;
			}

			if ( EditorPreferences.HideOrbitCursor )
				canvas.SetCanvasCursor( CursorShape.Blank );
			else
				canvas.SetCanvasCursor( CursorShape.SizeV );

			distance = distance.Clamp( 0, 250 );
			camera.WorldPosition = orbitPosition + camera.WorldRotation.Backward * distance;

			return true;
		}

		if ( camera.Orthographic )
			return false;

		//Shared with the scene orientation gizmo drag so both orbit identically.
		self.OrbitCameraAroundPivot( camera, delta, ref distance );

		if ( EditorPreferences.HideOrbitCursor )
			canvas.SetCanvasCursor( CursorShape.Blank );
		else
			canvas.SetCanvasCursor( CursorShape.ClosedHand );

		return true;
	}

	/// <summary>Orbits a camera around a pivot point in a set amount of units in front of it. Shared by ALT+drag orbit and the scene orientation gizmo</summary>
	public static void OrbitCameraAroundPivot( this Gizmo.Instance self, CameraComponent camera, Vector2 delta, ref float distance )
	{
		var orbitPosition = camera.WorldPosition + camera.WorldRotation.Forward * distance;

		var angles = camera.WorldRotation.Angles();
		angles.roll = 0;
		angles.yaw -= delta.x;
		angles.pitch += delta.y;
		angles = angles.Normal;
		angles.pitch = angles.pitch.Clamp( -89, 89 );

		camera.WorldRotation = angles;

		distance = distance.Clamp( 0, 250 );
		camera.WorldPosition = orbitPosition + camera.WorldRotation.Backward * distance;
	}

	/// <summary>Stop this bone being procedural</summary>
	public static void BreakProceduralBone( this GameObject go )
	{
		GameObjectFlags flags = go.Flags;

		if ( !flags.Contains( GameObjectFlags.Bone ) )
			return;

		if ( flags.Contains( GameObjectFlags.ProceduralBone ) )
			return;

		flags |= GameObjectFlags.ProceduralBone;
		go.Flags = flags;
	}

	#region Dispatch Edited

	/// <summary>Run the <see cref="EditorEvent.ISceneEdited.GameObjectPreEdited"/> or <see cref="EditorEvent.ISceneEdited.ComponentPreEdited"/> event for the given property.</summary>
	public static void DispatchPreEdited( this SerializedProperty property )
	{
		if ( property.FindPathInScene() is not { } path ) return;

		foreach ( var target in path.Targets )
		{
			switch ( target )
			{
				case GameObject go:
					DispatchPreEdited( go, path.FullName );
					break;
				case Component cmp:
					DispatchPreEdited( cmp, path.FullName );
					break;
			}
		}
	}

	/// <summary>Run the <see cref="EditorEvent.ISceneEdited.GameObjectEdited"/> or <see cref="EditorEvent.ISceneEdited.ComponentEdited"/> event for the given property.</summary>
	public static void DispatchEdited( this SerializedProperty property )
	{
		if ( property.FindPathInScene() is not { } path ) return;

		foreach ( var target in path.Targets )
		{
			switch ( target )
			{
				case GameObject go:
					DispatchEdited( go, path.FullName );
					break;
				case Component cmp:
					DispatchEdited( cmp, path.FullName );
					break;
			}
		}
	}

	/// <summary>Run the <see cref="EditorEvent.ISceneEdited.GameObjectPreEdited"/> event for the given property.</summary>
	public static void DispatchPreEdited( this GameObject go, string propertyName ) =>
		EditorEvent.RunInterface<EditorEvent.ISceneEdited>( i => i.GameObjectPreEdited( go, propertyName ) );

	/// <summary>Run the <see cref="EditorEvent.ISceneEdited.ComponentPreEdited"/> event for the given property.</summary>
	public static void DispatchPreEdited( this Component cmp, string propertyName ) =>
		EditorEvent.RunInterface<EditorEvent.ISceneEdited>( i => i.ComponentPreEdited( cmp, propertyName ) );

	/// <summary>Run the <see cref="EditorEvent.ISceneEdited.GameObjectEdited"/> event for the given property.</summary>
	public static void DispatchEdited( this GameObject go, string propertyName ) =>
		EditorEvent.RunInterface<EditorEvent.ISceneEdited>( i => i.GameObjectEdited( go, propertyName ) );

	/// <summary>Run the <see cref="EditorEvent.ISceneEdited.ComponentEdited"/> event for the given property.</summary>
	public static void DispatchEdited( this Component cmp, string propertyName ) =>
		EditorEvent.RunInterface<EditorEvent.ISceneEdited>( i => i.ComponentEdited( cmp, propertyName ) );

	public static void DispatchPreEdited( this IEnumerable<GameObject> gos, string propertyName )
	{
		foreach ( var go in gos ) go.DispatchPreEdited( propertyName );
	}

	public static void DispatchPreEdited( this IEnumerable<Component> cmps, string propertyName )
	{
		foreach ( var cmp in cmps ) cmp.DispatchPreEdited( propertyName );
	}

	public static void DispatchEdited( this IEnumerable<GameObject> gos, string propertyName )
	{
		foreach ( var go in gos ) go.DispatchEdited( propertyName );
	}

	public static void DispatchEdited( this IEnumerable<Component> cmps, string propertyName )
	{
		foreach ( var cmp in cmps ) cmp.DispatchEdited( propertyName );
	}

	#endregion
}
