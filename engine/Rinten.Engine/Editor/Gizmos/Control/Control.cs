namespace Rinten;

public static partial class Gizmo
{
	static GizmoControls _gizmo = new();

	/// <summary>Holds fully realized controls to manipulate some value</summary>
	public static GizmoControls Control => _gizmo;

	//The handles are scaled by how far the camera is from them, so "they look too big" is a question about a distance and a lens rather than about a length. This is the four numbers that answer it.
	/// <summary>How big the transform handles came out and what made them that size.</summary>
	[ConCmd( "editor_gizmo_scale" )]
	public static void GizmoScaleStatus()
	{
		var (cam, handle, fov, viewport, scale) = GizmoControls.LastFixedScale;

		if ( scale <= 0.0f )
		{
			Log.Info( "no handle has been drawn - select something with the move tool first" );
			return;
		}

		var dist = cam.Distance( handle );
		var aspect = viewport.y > 0.0f ? viewport.x / viewport.y : 1.0f;
		var vertical = 2.0f * MathF.Atan( MathF.Tan( fov.DegreeToRadian() * 0.5f ) / aspect );

		//One arrow is 24 of the handle's own units long.
		var arrow = 24.0f * scale;

		Log.Info( $"camera {cam} -> handle {handle}, {dist:n2}m apart" );
		Log.Info( $"fov {fov:n1} across / {vertical.RadianToDegree():n1} down, viewport {viewport}" );
		Log.Info( $"scale {scale:n4}, so one arrow is {arrow:n2}m long" );
		Log.Info( $"which is {arrow / (2.0f * dist * MathF.Tan( vertical * 0.5f )) * 100.0f:n1}% of the view's height" );
	}

	/// <summary>A part of the Position control: an axis arrow, a plane square, or the middle handle.</summary>
	public enum PositionPart { None, Arrow, Plane, Center }

	/// <summary>Extendable helper to create common gizmos</summary>
	public sealed partial class GizmoControls
	{
		internal GizmoControls()
		{

		}

		/// <summary>A front left up position movement widget. If widget was moved then will return true and out will return the new position.</summary>
		public bool Position( string name, Vector3 position, out Vector3 newPos, Rotation? axisRotation = null, float squareSize = 3.0f, Func<Vector3?> centerRaycast = null )
		{
			if ( position.IsNaN )
			{
				newPos = Vector3.Zero;
				return true;
			}

			var localRotation = Transform.Rotation;
			var localScale = Transform.UniformScale;
			var axis = axisRotation ?? Rotation.Identity;

			using var x = Rinten.Gizmo.Scope( name, Vector3.Zero, axis );

			using var scaler = PushFixedScale();

			//I don't know if we should even be doing this here or leave it to the implementation
			if ( Settings.GlobalSpace )
				Transform = Transform.WithRotation( Rotation.Identity );

			Rinten.Gizmo.Draw.IgnoreDepth = true;

			var rot = Transform.Rotation;
			var forward = rot.Forward;
			var left = rot.Left;
			var up = rot.Up;

			Vector3 movement = Vector3.Zero;

			//Each arrow along the positive axis it is named after (x = Right), so scale handles grow outward
			Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Colors.Red;
			if ( Arrow( "x", Vector3.Right, out var xdist ) )
			{
				movement += Vector3.Right * xdist;
			}

			Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Colors.Green;
			if ( Arrow( "y", Vector3.Up, out var ydist ) )
			{
				movement += Vector3.Up * ydist;
			}

			Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Colors.Blue;
			if ( Arrow( "z", Vector3.Backward, out var zdist ) )
			{
				movement += Vector3.Backward * zdist;
			}

			if ( squareSize > 0 )
			{
				float squareOffset = squareSize * 3.0f;
				using var scope = Rinten.Gizmo.Scope( "squares" );
				Hitbox.DepthBias *= 0.8f;

				if ( centerRaycast != null )
				{
					Rinten.Gizmo.Draw.Color = Color.White.WithAlpha( 1.1f );
					if ( DragCenterRaycast( "drag-camera", 2.0f, centerRaycast, out var moved ) )
					{
						movement += moved;
					}
				}
				else
				{
					var camRot = Transform.RotationToLocal( Camera.Rotation );

					Rinten.Gizmo.Draw.Color = Color.White.WithAlpha( 1.1f );
					if ( DragSquare( "drag-camera", squareSize, camRot, out var moved, () => DrawPositionCenter( squareSize ) ) )
					{
						//movement is in camera space
						movement += moved;
					}
				}

				using ( Rinten.Gizmo.Scope() )
				{
					Rinten.Gizmo.Transform = Rinten.Gizmo.Transform.ToWorld( new Transform( Vector3.Up * squareOffset + Vector3.Right * squareOffset ) );
					Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Colors.Blue;
					if ( DragSquare( "xy", squareSize, Rotation.LookAt( Vector3.Backward, Vector3.Up ), out var moved ) )
					{
						movement += moved;
					}
				}

				using ( Rinten.Gizmo.Scope() )
				{
					Rinten.Gizmo.Transform = Rinten.Gizmo.Transform.ToWorld( new Transform( Vector3.Backward * squareOffset + Vector3.Right * squareOffset ) );
					Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Colors.Green;
					if ( DragSquare( "xz", squareSize, Rotation.LookAt( Vector3.Up, Vector3.Backward ), out var moved ) )
					{
						movement += moved;
					}
				}

				using ( Rinten.Gizmo.Scope() )
				{
					Rinten.Gizmo.Transform = Rinten.Gizmo.Transform.ToWorld( new Transform( Vector3.Backward * squareOffset + Vector3.Up * squareOffset ) );
					Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Colors.Red;
					if ( DragSquare( "yz", squareSize, Rotation.LookAt( Vector3.Right, Vector3.Down ), out var moved ) )
					{
						movement += moved;
					}
				}
			}

			if ( movement.IsNearlyZero() )
			{
				newPos = position;
				return false;
			}

			movement = (movement * Transform.Rotation) * localRotation.Inverse;

			if ( localScale != 0.0f )
			{
				movement *= (1.0f / localScale);
			}

			if ( movement.IsNearlyZero() )
			{
				newPos = position;
				return false;
			}

			newPos = position + movement;

			return true;
		}

		/// <summary>Which part of the Position control named so in this scope is pressed, and its axis (a plane's is its normal).</summary>
		//Axes in the control's space: 0 x (Right), 1 y (Up), 2 z (Backward)
		public PositionPart PressedPosition( string name, out int axis )
		{
			axis = -1;

			var pressed = Active?.current.PressedPath;
			if ( !GizmoHitbox.IsUnder( pressed, Join( Path, name ) ) ) return PositionPart.None;

			switch ( pressed[(pressed.LastIndexOf( '/' ) + 1)..] )
			{
				case "x": axis = 0; return PositionPart.Arrow;
				case "y": axis = 1; return PositionPart.Arrow;
				case "z": axis = 2; return PositionPart.Arrow;
				case "yz": axis = 0; return PositionPart.Plane;
				case "xz": axis = 1; return PositionPart.Plane;
				case "xy": axis = 2; return PositionPart.Plane;
				case "drag-camera": return PositionPart.Center;
				default: return PositionPart.None;
			}
		}

		//The middle handle, drawn the size it actually is. It used to be a half-unit circle over a three-unit hitbox - a few pixels of
		//ring that took the pointer from six times its own width around, and, being biased nearer than the arrows, took it off them.
		static void DrawPositionCenter( float radius )
		{
			Rinten.Gizmo.Draw.LineThickness = 3.0f;
			Rinten.Gizmo.Draw.LineCircle( 0, Rinten.Gizmo.IsHovered ? radius * 1.15f : radius, sections: 24 );
		}

		/// <summary>Draw an arrow - return move delta if interacted with</summary>
		public bool Arrow( string name, Vector3 axis, out float distance, float length = 24.0f, float girth = 3.0f, float axisOffset = 2.0f, float cullAngle = 10.0f, float snapSize = 0.0f, string head = "cone" )
		{
			distance = 0;

			var angle = Vector3.GetAngle( axis, Transform.NormalToLocal( Camera.Rotation.Forward ) );

			if ( angle < cullAngle || angle > 180.0f - cullAngle )
				return false;

			var localCam = Transform.RotationToLocal( Camera.Rotation );

			//Use the camera to provide a plane that'll work for us
			var rot = Rotation.LookAt( axis, Vector3.Up );

			using var x = Rinten.Gizmo.Scope( name, axis * axisOffset, rot );

			girth *= 0.5f;

			//Grab box down the shaft along Forward, matching how the arrow is drawn
			var shaft = Vector3.Forward * length;
			var across = ( Vector3.Left * girth ).Abs() + ( Vector3.Up * girth ).Abs();

			Rinten.Gizmo.Hitbox.BBox( new BBox(
				Vector3.Min( Vector3.Zero, shaft ) - across,
				Vector3.Max( Vector3.Zero, shaft ) + across ) );

			if ( !Rinten.Gizmo.IsHovered ) Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Draw.Color.Darken( 0.33f );

			//Not the girth. A line's thickness is pixels wherever the line is, and the girth is the gizmo's own units - setting one from
			//the other tied how thick the shaft looks to how far off it you could be and still grab it, which are not the same question.
			Rinten.Gizmo.Draw.LineThickness = 4.5f;

			var lineLength = length;
			var headLength = 4.0f;

			if ( snapSize > 0 )
			{
				lineLength = lineLength.SnapToGrid( snapSize );
			}

			//not pressed, no movement
			Rinten.Gizmo.Draw.Line( 0, Vector3.Forward * (lineLength - headLength) );

			if ( head == "cone" )
			{
				Rinten.Gizmo.Draw.SolidCone( Vector3.Forward * (lineLength - headLength), Vector3.Forward * headLength, headLength * 0.33f );
			}

			if ( head == "box" )
			{
				Rinten.Gizmo.Draw.SolidBox( BBox.FromPositionAndSize( Vector3.Forward * (lineLength - headLength), headLength * 0.5f ) );
			}

			if ( !Pressed.This )
				return false;

			//use a plane that follows the axis but that uses the camera's plane
			Gizmo.Transform = Gizmo.Transform.WithRotation( Rotation.LookAt( Transform.Rotation.Forward, Camera.Rotation.Forward ) );


			//Get the delta between trace hits against a plane
			var delta = Rinten.Gizmo.GetMouseDelta( Vector3.Zero, Vector3.Up );

			distance = Vector3.Forward.Dot( delta );

			//restrict movement to the axis direction
			return distance != 0.0f;

		}

		public bool DragBox( string name, Vector3 size, Rotation rotation, out Vector3 movement )
		{
			movement = default;
			var box = new BBox( -size * 0.5f, size * 0.5f );

			using var x = Rinten.Gizmo.Scope( name, new Transform( 0, rotation ) );

			//Hitbox
			Rinten.Gizmo.Hitbox.BBox( box );

			//Draw
			if ( !Rinten.Gizmo.IsHovered )
			{
				Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Draw.Color.Darken( 0.33f );
			}

			if ( !Rinten.Gizmo.IsHovered ) Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Draw.Color.Darken( 0.03f );

			Rinten.Gizmo.Draw.SolidBox( box );

			if ( !Pressed.This )
				return false;

			//action

			var localCameraRot = Transform.RotationToLocal( Camera.Rotation );

			var delta = Rinten.Gizmo.GetMouseDelta( 0, localCameraRot.Forward );
			//Dragging right or up grows uniformly, independent of the view and gizmo rotation.
			movement = (localCameraRot.Right + localCameraRot.Up).Dot( delta );

			//Optional: Debug drawing
			if ( Hitbox.Debug && Rinten.Gizmo.IsHovered )
			{
				using var scope = Scope();

				var os = Transform;
				Transform = Transform.WithScale( 1.0f );
				Rinten.Gizmo.Draw.LineThickness = 1.0f;
				Rinten.Gizmo.Draw.Color = Color.White;
				Rinten.Gizmo.Draw.Plane( 0, Vector3.Forward );
				Transform = os;
			}

			return movement != Vector3.Zero;
		}

		/// <summary>Manipulate a 2d value by moving on 2 axis A center handle that moves the object to the position of a raycast hit, cast from mouse cursor</summary>
		public bool DragCenterRaycast( string name, float size, Func<Vector3?> raycast, out Vector3 movement )
		{
			movement = default;

			using var x = Rinten.Gizmo.Scope( name );

			var bbox = new BBox( new Vector3( -size ), new Vector3( size ) );

			Rinten.Gizmo.Hitbox.BBox( bbox );

			if ( !Rinten.Gizmo.IsHovered ) Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Draw.Color.Darken( 0.33f );

			var drawSize = Rinten.Gizmo.IsHovered ? size : size * 0.85f;
			Rinten.Gizmo.Draw.SolidBox( new BBox( new Vector3( -drawSize ), new Vector3( drawSize ) ) );

			if ( !Pressed.This )
				return false;

			if ( raycast() is not Vector3 hit )
				return false;

			var anchor = Transform.Position;
			var rot = Transform.Rotation;

			movement = (hit - anchor) * rot.Inverse;

			return true;
		}

		public bool DragSquare( string name, Vector2 size, Rotation rotation, out Vector3 movement, Action drawHandle = null )
		{
			movement = default;
			//Thin across the plane, broad in it, using named axes so the box matches the drag plane
			var extents = ( Vector3.Forward * 0.01f ).Abs()
				+ ( Vector3.Left * size.x ).Abs()
				+ ( Vector3.Up * size.y ).Abs();

			var bbox = new BBox( -extents, extents );

			using var x = Rinten.Gizmo.Scope( name, new Transform( 0, rotation ) );


			//hitbox

			Rinten.Gizmo.Hitbox.BBox( bbox );

			//drawing

			if ( !Rinten.Gizmo.IsHovered ) Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Draw.Color.Darken( 0.33f );

			if ( drawHandle == null )
			{
				Rinten.Gizmo.Draw.LineThickness = 2.0f;
				Rinten.Gizmo.Draw.LineBBox( bbox );
				//Scene.Draw.LineCircle( 0, size.x );
			}

			drawHandle?.Invoke();

			if ( !Pressed.This )
				return false;

			//action

			movement = Rinten.Gizmo.GetMouseDelta( 0, Vector3.Forward );

			if ( Hitbox.Debug && Rinten.Gizmo.IsHovered )
			{
				using var scope = Scope();

				var os = Transform;
				Transform = Transform.WithScale( 1.0f );
				Rinten.Gizmo.Draw.LineThickness = 1.0f;
				Rinten.Gizmo.Draw.Color = Color.White;
				Rinten.Gizmo.Draw.Plane( 0, Vector3.Forward );
				Transform = os;
			}

			movement *= rotation;

			return movement != 0;

		}

		/// <summary>Scope this before drawing a control to obey Settings.GizmoScale</summary>
		public static IDisposable PushFixedScale( float? scale = null )
		{
			if ( !scale.HasValue )
				scale = Settings.GizmoScale;

			if ( Camera.Ortho )
			{
				scale *= Camera.OrthoHeight * 0.006f;
			}
			else
			{
				//Handle scale from distance and vertical FOV only, so handles keep their share of any viewport
				var dist = Camera.Position.Distance( Transform.Position );

				scale *= dist * VerticalFieldOfView() * 0.006f;
			}

			LastFixedScale = (Camera.Position, Transform.Position, Camera.FieldOfView, Camera.Size, scale.Value);

			var s = Scope( "WorldScale" );
			Transform = Transform.WithScale( scale.Value );
			Active.scope.IsHandle = true;
			return s;
		}

		//What the last handle was sized by, so editor_gizmo_scale can say it out loud rather than it being guessed at from the grid.
		internal static (Vector3 Camera, Vector3 Handle, float FieldOfView, Vector2 Viewport, float Scale) LastFixedScale;

		//How far the camera opens up and down, in radians. SceneCamera.FieldOfView is the one across, which is what the projection converts from.
		private static float VerticalFieldOfView()
		{
			var size = Camera.Size;
			var aspect = size.y > 0.0f ? size.x / size.y : 1.0f;

			return 2.0f * MathF.Atan( MathF.Tan( Camera.FieldOfView.DegreeToRadian() * 0.5f ) / aspect );
		}
	}
}
