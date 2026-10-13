namespace Rinten;

partial class Gizmo
{
	[System.Obsolete( "Use Gizmo.Pressed.This" )]
	public static bool IsPressed => Pressed.This;

	[System.Obsolete( "Use Gizmo.Pressed.Any" )]
	public static bool HasPressed => Pressed.Any;

	[System.Obsolete( "Use Gizmo.Pressed.Ray" )]
	public static Ray PressRay => Pressed.Ray;

	/// <summary>Access to the currently pressed path information</summary>
	public static class Pressed
	{
		/// <summary>The ray representing the cursor direction</summary>
		public static Ray Ray => Active.pressed.Input.CursorRay;

		/// <summary>True if the current gizmo scope is pressed</summary>
		public static bool This => Active is not null && Active.current.PressedPath == Path;

		/// <summary>True if any object is currently pressed</summary>
		public static bool Any => Active is not null && !string.IsNullOrEmpty( Active.current.PressedPath );

		/// <summary>True if this scope is pressed, or a scope opened directly inside it is.</summary>
		public static bool ThisOrChild
		{
			get
			{
				if ( Active is null ) return false;

				var pressed = Active.current.PressedPath;
				var path = Path;

				if ( string.IsNullOrEmpty( pressed ) || string.IsNullOrEmpty( path ) ) return false;
				if ( pressed == path ) return true;

				//The same question the hover arbitration asks, asked once - see GizmoHitbox.IsUnder.
				//Written out twice, it was got right here and left wrong there for a while.
				if ( !GizmoHitbox.IsUnder( pressed, path ) ) return false;

				return pressed.IndexOf( '/', path.Length + 1 ) < 0;
			}
		}

		/// <summary>The distance the cursor has travelled since press started</summary>
		public static Vector2 CursorDelta => (CursorPosition - Gizmo.CursorPosition);

		/// <summary>The cursor position at the start of the press</summary>
		public static Vector2 CursorPosition => Active.pressed.Input.CursorPosition;

		/// <summary>True if press is active. This generally means that the left mouse button is down</summary>
		public static bool IsActive => Gizmo.IsLeftMouseDown;

		public static void ClearPath()
		{
			Active.builder.PressedPath = default;
		}
	}
}
