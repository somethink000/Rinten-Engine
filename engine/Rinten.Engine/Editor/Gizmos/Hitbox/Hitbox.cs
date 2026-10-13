
using System.ComponentModel;

namespace Rinten;

public static partial class Gizmo
{
	static GizmoHitbox _hitbox = new();

	/// <summary>Allows creating a gizmo hitbox which will be interactable using the mouse (or vr deck2 super controller)</summary>
	public static GizmoHitbox Hitbox => _hitbox;

	/// <summary>Contains functions to add objects to the immediate mode Scene. This is an instantiable class so it's possible to add extensions.</summary>
	public sealed partial class GizmoHitbox
	{
		internal GizmoHitbox()
		{

		}

		/// <summary>Whether or not drawn gizmos can be interacted with. Only affects gizmos in the current scope.</summary>
		public bool CanInteract
		{
			get => Active.scope.CanInteract;
			set => Active.scope.CanInteract = value;
		}

		public bool Debug { get; internal set; }

		public float DepthBias
		{
			get => Active.scope.HitDepthBias;
			set => Active.scope.HitDepthBias = value;
		}

		/// <summary>If this distance is closer than our previous best, this path will become the hovered path</summary>
		public void TrySetHovered( float distance )
		{
			if ( !CanInteract )
				return;

			distance *= DepthBias;

			var path = Active.lineScope.Enabled ? Active.lineScope.LinePath : Path;

			//we have a closer object
			if ( distance > Active.builder.HitDistance )
			{
				//it's us anyway, forget it
				if ( path == Active.builder.HoveredPath )
					return;

				//A nearer hit keeps hover unless this path is inside it; IsUnder checks whole names, not prefixes
				if ( !IsUnder( path, Active.builder.HoveredPath ) )
					return;
			}

			Active.builder.HoveredPath = path;
			Active.builder.HoveredHandle = Active.scope.IsHandle;
			Active.builder.HitDistance = distance;
		}

		//Whether one gizmo path is inside another by whole slash-separated names, compared ordinally
		internal static bool IsUnder( string path, string ancestor )
		{
			if ( string.IsNullOrEmpty( ancestor ) ) return true;
			if ( string.IsNullOrEmpty( path ) ) return false;

			if ( path.Length <= ancestor.Length ) return false;
			if ( !path.StartsWith( ancestor, System.StringComparison.Ordinal ) ) return false;

			return path[ancestor.Length] == '/';
		}

		/// <summary>If this distance is closer than our previous best, this path will become the hovered path</summary>
		public void TrySetHovered( Vector3 position )
		{
			TrySetHovered( Camera.Position.Distance( position ) );
		}

		//Converts a scope-local distance to world units, since handle scopes are scaled to screen size
		private static float ToWorldDistance( float localDistance )
		{
			var scale = MathF.Abs( Transform.UniformScale );

			return scale > 0.0f ? localDistance * scale : localDistance;
		}

		/// <summary>A sphere hitbox</summary>
		public void Sphere( Sphere sphere )
		{
			if ( Debug )
			{
				using var scope = Scope();
				Rinten.Gizmo.Draw.LineThickness = 1;
				Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Colors.Selected.WithAlpha( 0.33f );
				Rinten.Gizmo.Draw.LineSphere( sphere );
			}

			//convert ray to local to the sphere
			var ray = CurrentRay.ToLocal( Transform );

			if ( !sphere.Trace( ray, float.MaxValue, out float hitDistance ) )
				return;

			hitDistance = ToWorldDistance( hitDistance );

			//Too close to be pointing at rather than standing inside. Five centimetres: it was two, which was two
			//inches and is now two metres, and nothing within arm's reach of the camera could be picked at all.
			if ( hitDistance < 0.05f )
				return;

			TrySetHovered( hitDistance );

			if ( Debug )
			{
				using var scope = Scope();
				Rinten.Gizmo.Draw.LineThickness = 1;
				Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Colors.Selected.WithAlpha( 0.8f );
				Rinten.Gizmo.Draw.LineSphere( sphere );
			}
		}

		/// <summary>A bounding box hitbox</summary>
		public void BBox( BBox bounds )
		{
			if ( Debug )
			{
				using var scope = Scope();
				Rinten.Gizmo.Draw.LineThickness = 2;
				Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Colors.Selected.WithAlpha( 0.33f );
				Rinten.Gizmo.Draw.LineBBox( bounds );
			}

			//convert ray to local to the box
			var ray = CurrentRay.ToLocal( Transform );

			if ( !bounds.Trace( ray, float.MaxValue, out float hitDistance ) )
				return;

			TrySetHovered( ToWorldDistance( hitDistance ) );

			if ( Debug )
			{
				using var scope = Scope();

				Rinten.Gizmo.Draw.LineThickness = 2;
				Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Colors.Selected.WithAlpha( 0.8f );
				Rinten.Gizmo.Draw.LineBBox( bounds );
			}
		}

		/// <summary>A 2d circle hitbox, on a plane</summary>
		public void Circle( Vector3 center, Vector3 forward, float outerRadius, float innerRadius = 0.0f )
		{
			//convert ray to local to the box
			var ray = CurrentRay.ToLocal( Transform );

			var plane = new Plane( center, forward );
			if ( !plane.TryTrace( ray, out var hitPoint, true ) ) return;

			var dist = Vector3.DistanceBetween( center, hitPoint );
			if ( dist > outerRadius ) return;
			if ( dist < innerRadius ) return;

			//The hit is in the scope's space; the overload below measures from the camera, which is in the world's.
			TrySetHovered( Transform.PointToWorld( hitPoint ) );
		}

		/// <summary>A model hitbox</summary>
		public void Model( Model model )
		{
			if ( model is null )
				return;

			var modelBounds = model.Bounds;

			if ( Debug )
			{
				using var scope = Scope();
				Rinten.Gizmo.Draw.LineThickness = 2;
				Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Colors.Selected.WithAlpha( 0.33f );
				Rinten.Gizmo.Draw.LineBBox( modelBounds );
			}

			//convert ray to local to the box
			var ray = CurrentRay.ToLocal( Transform );

			if ( !modelBounds.Trace( ray, RayDepth, out _ ) )
				return;

			//Front faces only, as every tool's trace is. Both sides let the back of a mesh wall
			//standing in front of a prop take the click that was meant for the prop behind it.
			var hitResult = model.Trace.Ray( ray, RayDepth ).FrontFacesOnly().Run();
			if ( !hitResult.Hit )
				return;

			TrySetHovered( Transform.PointToWorld( hitResult.HitPosition ) );

			if ( Debug )
			{
				using var scope = Scope();

				Rinten.Gizmo.Draw.LineThickness = 2;
				Rinten.Gizmo.Draw.Color = Rinten.Gizmo.Colors.Selected.WithAlpha( 0.8f );
				Rinten.Gizmo.Draw.LineBBox( modelBounds );
			}
		}

		/// <summary>A 2d sprite hitbox</summary>
		public void Sprite( Vector3 center, float size, bool worldspace = true )
		{
			var position = Transform.Position + center;
			var halfSize = size * 0.5f;
			var plane = new Plane( position, Camera.Rotation.Forward );
			var hit = plane.TryTrace( CurrentRay, out var hitPoint, true );

			if ( hit )
			{
				if ( worldspace )
				{
					var toHitPoint = hitPoint - position;
					hit = Math.Abs( Vector3.Dot( toHitPoint, Camera.Rotation.Right ) ) <= halfSize &&
						  Math.Abs( Vector3.Dot( toHitPoint, Camera.Rotation.Up ) ) <= halfSize;
				}
				else
				{
					var hitScreenPos = Camera.ToScreen( hitPoint );
					var centerScreenPos = Camera.ToScreen( position );
					hit = Math.Abs( hitScreenPos.x - centerScreenPos.x ) <= halfSize &&
						  Math.Abs( hitScreenPos.y - centerScreenPos.y ) <= halfSize;
				}
			}

			if ( Debug )
			{
				using var scope = Scope();

				Draw.Color = Colors.Selected.WithAlpha( hit ? 0.8f : 0.33f );
				Draw.Sprite( center, size, Texture.White, false );
			}

			if ( !hit )
				return;

			TrySetHovered( hitPoint );
		}

		//PAINDAY
		[EditorBrowsable( EditorBrowsableState.Never ), Obsolete( "maxDistance is obsolete and unused" )]
		public void Model( Model model, float maxDistance = 10000 )
		{
			Model( model );
		}
	}
}
