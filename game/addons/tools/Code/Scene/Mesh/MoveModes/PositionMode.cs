namespace Editor.MeshEditor;

/// <summary>Move selected Mesh Elements.<br/> <br/> <b>Ctrl</b> - toggle snap to grid<br/> <b>Shift</b> - extrude selection<br/> <b>Alt</b> - snap objects' edge to surfaces: an arrow slides into one, a plane sits on one along its normal, the center on the nearest</summary>
[Title( "Move/Position" )]
[Icon( "open_with" )]
[Alias( "mesh.position.mode" )]
[Order( 0 )]
public sealed class PositionMode : MoveMode
{
	private Vector3 _moveDelta;
	private Vector3 _origin;
	private Rotation _basis;
	private Vector3 _startPosition;

	//Set while Alt is held: the snap, and the drag and offset from when Alt went down
	private SurfaceSnap _surface;
	private Vector3 _altMoveDelta;
	private Vector3 _altOffset;
	private Vector3 _lastOffset;

	public override void OnBegin( SelectionTool tool )
	{
		_basis = tool.CalculateSelectionBasis();
		_origin = tool.Pivot.Position;
		_moveDelta = default;
		_startPosition = tool.Pivot.Position;
		_surface = null;
		_lastOffset = default;
	}

	protected override void OnUpdate( SelectionTool tool )
	{
		var origin = tool.Pivot.Position;

		var meshTool = tool.Manager?.CurrentTool as MeshTool;
		Vector3? snapTarget = null;
		bool isActivelySnapping = false;

		if ( meshTool?.VertexSnappingEnabled == true && Gizmo.IsLeftMouseDown )
		{
			var gizmoSize = 0.5f * Gizmo.Settings.GizmoScale * Application.DpiScale;

			var closestVertex = tool.MeshTrace.GetClosestVertex( 8 );
			bool hasSnapTarget = closestVertex.IsValid();

			if ( hasSnapTarget )
			{
				var cameraDistance = Gizmo.Camera.Position.Distance( closestVertex.PositionWorld );
				var scaledGizmo = gizmoSize * (cameraDistance / 50.0f).Clamp( 0.1f, 4.0f );

				snapTarget = closestVertex.PositionWorld;
				isActivelySnapping = Gizmo.IsLeftMouseDown;

				using ( Gizmo.Scope( "VertexSnapTarget" ) )
				{
					Gizmo.Draw.IgnoreDepth = true;
					Gizmo.Draw.Color = Color.Green;
					Gizmo.Draw.Sprite( snapTarget.Value, 8, null, false );

					Gizmo.Transform = new Transform( snapTarget.Value, Rotation.LookAt( Gizmo.LocalCameraTransform.Rotation.Backward ) );

					Gizmo.Draw.LineThickness = 2;
					Gizmo.Draw.LineCircle( 0, Vector3.Forward, scaledGizmo );
				}
			}
			else
			{
				var nearbyVertex = tool.MeshTrace.GetClosestVertex( 50 );
				if ( nearbyVertex.IsValid() )
				{
					var cameraDistance = Gizmo.Camera.Position.Distance( nearbyVertex.PositionWorld );
					var scaledGizmo = gizmoSize * (cameraDistance / 50.0f).Clamp( 0.1f, 4.0f );

					var distance = Vector3.DistanceBetween( nearbyVertex.PositionWorld, origin );

					if ( distance > 5f )
					{
						using ( Gizmo.Scope( "VertexNearby" ) )
						{
							Gizmo.Draw.IgnoreDepth = true;
							Gizmo.Draw.Color = Color.Red;

							Gizmo.Transform = new Transform( nearbyVertex.PositionWorld, Rotation.LookAt( Gizmo.LocalCameraTransform.Rotation.Backward ) );
							Gizmo.Draw.LineThickness = 2;
							Gizmo.Draw.LineCircle( 0, Vector3.Forward, scaledGizmo );
						}
					}
				}
			}
		}

		using ( Gizmo.Scope( "Tool", new Transform( origin ) ) )
		{
			Gizmo.Hitbox.DepthBias = 0.01f;
			Gizmo.Hitbox.CanInteract = CanUseGizmo;

			var dragBefore = _moveDelta;
			Vector3? offset = null;

			if ( Gizmo.Control.Position( "position", Vector3.Zero, out var delta, _basis ) )
			{
				if ( _moveDelta == Vector3.Zero )
				{
					_startPosition = _origin;
				}

				_moveDelta += delta;

				var moveDelta = Gizmo.Snap( _origin, _moveDelta, _basis );

				if ( snapTarget.HasValue )
				{
					var localDelta = delta * _basis.Inverse;
					var absX = Math.Abs( localDelta.x );
					var absY = Math.Abs( localDelta.y );
					var absZ = Math.Abs( localDelta.z );

					const float threshold = 0.001f;

					bool xActive = absX > threshold;
					bool yActive = absY > threshold;
					bool zActive = absZ > threshold;
					int activeCount = (xActive ? 1 : 0) + (yActive ? 1 : 0) + (zActive ? 1 : 0);

					Vector3 snappedPosition = moveDelta;

					if ( activeCount == 1 )
					{
						Vector3 axisVector;
						if ( xActive )
							axisVector = _basis.Forward;
						else if ( yActive )
							axisVector = _basis.Right;
						else
							axisVector = _basis.Up;

						var toTarget = snapTarget.Value - _origin;
						var projectedDistance = Vector3.Dot( toTarget, axisVector );
						snappedPosition = _origin + axisVector * projectedDistance;
					}
					else if ( activeCount == 2 )
					{
						Vector3 planeNormal;
						if ( !xActive )
							planeNormal = _basis.Forward;
						else if ( !yActive )
							planeNormal = _basis.Right;
						else
							planeNormal = _basis.Up;

						var toTarget = snapTarget.Value - _origin;
						var distanceFromPlane = Vector3.Dot( toTarget, planeNormal );
						snappedPosition = snapTarget.Value - planeNormal * distanceFromPlane;
					}
					else if ( activeCount == 3 )
					{
						snappedPosition = snapTarget.Value;
					}

					moveDelta = snappedPosition;
				}

				offset = moveDelta - _origin;
			}

			var moved = offset.HasValue;
			var pressed = Gizmo.Pressed.FullPath;
			var alt = Gizmo.IsAltPressed && pressed is not null && pressed.StartsWith( $"{Gizmo.Path}/position/" );

			if ( alt && _surface is null && tool.CreateSurfaceSnap( _basis ) is { } surface )
			{
				_surface = surface;
				_altMoveDelta = dragBefore;
				_altOffset = _lastOffset;
			}

			//Released, the selection stays where the snap put it and the drag goes on from there
			if ( !alt && _surface is not null )
			{
				_surface = null;
				_moveDelta = _lastOffset;
			}

			if ( _surface is not null )
				offset = _surface.Offset( pressed, _altOffset, offset ?? _lastOffset, _moveDelta - _altMoveDelta );

			//A press with Alt that has not moved anything yet is not an edit
			if ( offset is Vector3 move && (moved || move != _lastOffset) )
			{
				tool.StartDrag();
				tool.Translate( move );
				tool.UpdateDrag();
				_lastOffset = move;
			}
		}

		if ( Gizmo.Pressed.Any && _moveDelta != Vector3.Zero )
		{
			DrawMovementLine( _startPosition, tool.Pivot.Position, _basis );
		}
	}

	private void DrawMovementLine( Vector3 start, Vector3 end, Rotation basis )
	{
		var distance = start.Distance( end );

		if ( distance < 0.01f )
			return;

		using ( Gizmo.Scope( "MovementLine" ) )
		{
			Gizmo.Draw.IgnoreDepth = true;

			Gizmo.Draw.Color = Color.Blue.WithAlpha( 0.7f );
			Gizmo.Draw.LineThickness = 2;
			Gizmo.Draw.Line( start, end );

			Gizmo.Draw.Color = Color.White.WithAlpha( 0.7f );
			Gizmo.Draw.Sprite( start, 6, null, false );

			Gizmo.Draw.Color = Color.Cyan;
			Gizmo.Draw.Sprite( end, 8, null, false );

			var midPoint = (start + end) * 0.5f;
			var textSize = 22 * Gizmo.Settings.GizmoScale * Application.DpiScale;

			var cameraDistance = Gizmo.Camera.Position.Distance( midPoint );
			var scaledTextSize = textSize * (cameraDistance / 50.0f).Clamp( 0.5f, 1.0f );

			string distanceText;
			//Local distance
			if ( !Gizmo.Settings.GlobalSpace )
			{
				var localDelta = (end - start) * basis.Inverse;
				var localDistance = localDelta.Length;
				distanceText = Measurement.Length( localDistance );
			}
			else
			{
				distanceText = Measurement.Length( distance );
			}

			var textScope = new TextRendering.Scope
			{
				Text = distanceText,
				TextColor = Color.White,
				FontSize = scaledTextSize,
				FontName = "Roboto Mono",
				FontWeight = 600,
				LineHeight = 1,
				Outline = new TextRendering.Outline() { Color = Color.Black, Enabled = true, Size = 3 }
			};

			Gizmo.Draw.ScreenText( textScope, midPoint, new Vector2( 0, -scaledTextSize ) );

			var direction = (end - start).Normal;
			var arrowLength = Math.Min( distance * 0.2f, 5.0f );
		}
	}
}
