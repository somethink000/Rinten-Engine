using System.Runtime.InteropServices;

namespace Rinten;

public static partial class Gizmo
{
	public sealed partial class GizmoDraw
	{
		//Every shape that game code draws takes the same three at the end: its own
		//colour, how many seconds it stays (only a scene's instance keeps anything;
		//an editor view's frame is one frame), and whether it is drawn over everything. Where one is left out, the brush's is used.

		/// <summary>Draw a line from a to b</summary>
		public void Line( in Vector3 a, in Vector3 b, Color? color = null, float duration = 0, bool? overlay = null )
		{
			var sink = LineSink( color, duration, overlay );
			sink.Line( a, b );
		}

		/// <summary>Draw a line</summary>
		public void Line( in Line line, Color? color = null, float duration = 0, bool? overlay = null )
		{
			var sink = LineSink( color, duration, overlay );
			sink.Line( line.Start, line.End );
		}

		/// <summary>Draw lines</summary>
		public void Lines( in IEnumerable<Line> lines, Color? color = null, float duration = 0, bool? overlay = null )
		{
			var sink = LineSink( color, duration, overlay );

			foreach ( var line in lines )
			{
				sink.Line( line.Start, line.End );
			}
		}

		/// <summary>Draw many separate lines as one: the points are read in pairs, a line each. An odd point at the end has nothing to be a line with.</summary>
		public void Lines( ReadOnlySpan<Vector3> pairs, Color? color = null, float duration = 0, bool? overlay = null )
		{
			var sink = LineSink( color, duration, overlay );

			for ( var i = 0; i + 1 < pairs.Length; i += 2 ) sink.Line( pairs[i], pairs[i + 1] );
		}

		/// <summary>The same, where each end carries its own colour - a graph whose line changes colour along it.</summary>
		public void Lines( ReadOnlySpan<Vertex> pairs, float duration = 0, bool? overlay = null )
		{
			LineSink( null, duration, overlay );
			Target?.Add( pairs[..( pairs.Length & ~1 )] );
		}

		/// <summary>Draw a line through every point in turn</summary>
		public void LineStrip( IEnumerable<Vector3> points, Color? color = null, float duration = 0, bool? overlay = null )
		{
			var sink = LineSink( color, duration, overlay );

			var first = true;
			var last = Vector3.Zero;

			foreach ( var point in points )
			{
				if ( !first ) sink.Line( last, point );

				last = point;
				first = false;
			}
		}

		/// <summary>Draw a bounding box</summary>
		public void LineBBox( in BBox box, Color? color = null, float duration = 0, bool? overlay = null )
		{
			var sink = LineSink( color, duration, overlay );
			DebugShapes.LineBox( ref sink, box );
		}

		/// <summary>Draws a frustum.</summary>
		public void LineFrustum( in Frustum frustum, Color? color = null, float duration = 0, bool? overlay = null )
		{
			var sink = LineSink( color, duration, overlay );
			DebugShapes.LineFrustum( ref sink, frustum );
		}

		/// <summary>Draw a sphere made out of lines</summary>
		public void LineSphere( in Vector3 point, in float radius, in int rings = 8 ) => LineSphere( new Sphere( point, radius ), rings );

		/// <summary>Draw a sphere made out of lines: its three rings and its outline from wherever it is seen, which the drawer builds for each view.</summary>
		public void LineSphere( in Sphere sphere, int rings = 8, Color? color = null, float duration = 0, bool? overlay = null )
		{
			Target?.Add( new DebugDrawList.Ring
			{
				Center = sphere.Center,
				Radius = sphere.Radius,
				Transform = Transform,
				Color = color ?? Color,
				IgnoreDepth = overlay ?? IgnoreDepth,
				Life = LifeFor( duration ),
			} );
		}

		/// <summary>Draw a circle made out of lines, facing along the scope's forward</summary>
		public void LineCircle( in Vector3 center, float radius, float startAngle = 0.0f, float totalDegrees = 360.0f, int sections = 16 )
		{
			var sink = LineSink();
			DebugShapes.Arc( ref sink, center, Vector3.Left * radius, Vector3.Up * radius, startAngle, totalDegrees, sections );
		}

		public void LineCircle( in Vector3 center, Vector3 forward, float radius, float startAngle = 0.0f, float totalDegrees = 360.0f, int sections = 16 )
		{
			Vector3 up = Vector3.Up;
			if ( MathF.Abs( Vector3.Dot( forward, up ) ) > 0.99 )
			{
				up = Vector3.Forward;
			}

			LineCircle( center, forward, up, radius, startAngle, totalDegrees, sections );
		}

		public void LineCircle( in Vector3 center, Vector3 forward, Vector3 up, float radius, float startAngle = 0.0f, float totalDegrees = 360.0f, int sections = 16 )
		{
			var sink = LineSink();
			DebugShapes.Circle( ref sink, center, forward, up, radius, startAngle, totalDegrees, sections );
		}

		/// <summary>A cylinder, or a cone where one radius is nought</summary>
		public void LineCylinder( Vector3 vPointA, Vector3 vPointB, float flRadiusA, float flRadiusB, int nNumSegments = 12, Color? color = null, float duration = 0, bool? overlay = null )
		{
			var sink = LineSink( color, duration, overlay );
			DebugShapes.LineCylinder( ref sink, vPointA, vPointB, flRadiusA, flRadiusB, nNumSegments );
		}

		/// <summary>A cylinder with a half sphere on each end</summary>
		public void LineCapsule( Capsule capsule, int rings = 12, Color? color = null, float duration = 0, bool? overlay = null )
		{
			if ( ( capsule.CenterB - capsule.CenterA ).IsNearZeroLength )
			{
				LineSphere( new Sphere( capsule.CenterA, capsule.Radius ), rings, color, duration, overlay );
				return;
			}

			var sink = LineSink( color, duration, overlay );
			DebugShapes.LineCapsule( ref sink, capsule, rings );
		}

		/// <summary>A triangle</summary>
		public void LineTriangle( in Triangle triangle )
		{
			var sink = LineSink();
			DebugShapes.LineTriangle( ref sink, triangle.A, triangle.B, triangle.C );
		}

		/// <summary>Multiple triangles</summary>
		public void LineTriangles( in IEnumerable<Triangle> triangles, Color? color = null, float duration = 0, bool? overlay = null )
		{
			var sink = LineSink( color, duration, overlay );

			foreach ( var triangle in triangles )
			{
				DebugShapes.LineTriangle( ref sink, triangle.A, triangle.B, triangle.C );
			}
		}

		/// <summary>
		/// Draws every edge of a collision mesh from geometry kept on the card: one indexed draw a frame, however
		/// many triangles it has, rather than three lines built on the CPU for each of them. Falls back to
		/// <see cref="LineTriangles"/> where there is no device to keep it on.
		/// </summary>
		public void LineMesh( PhysicsGroupDescription.BodyPart.MeshPart mesh, Color? color = null, float duration = 0, bool? overlay = null )
		{
			if ( mesh is null ) return;

			var wire = CoreGizmoWire.Get( mesh );

			//No device to keep it on: the lines are built the old way, which is what a test host and a
			//viewport before the device starts get.
			if ( wire is null )
			{
				LineTriangles( mesh.GetTriangles(), color, duration, overlay );
				return;
			}

			//The card would not keep it. Falling back would put the whole mesh back on every frame, which is the
			//cost this is here to avoid, so the mesh is left out rather than drawn at that price.
			if ( !wire.IsValid ) return;

			Target?.Add( new DebugDrawList.Wire
			{
				Vertices = wire.Vertices,
				Indices = wire.Indices,
				IndexCount = wire.IndexCount,
				Transform = Transform,
				Color = color.HasValue ? (Color32)color.Value : Color32,
				IgnoreDepth = overlay ?? IgnoreDepth,
				Life = LifeFor( duration ),
			} );
		}

		/// <summary>Draws the result of a physics trace: where it started and ended, where it hit and which way the surface faced, and the traced shape at both ends.</summary>
		public void Trace( SceneTraceResult trace, float duration = 0, bool? overlay = null )
		{
			const float endSize = 0.025f;
			const float hitSize = 0.05f;
			const float normalLength = 0.25f;

			var white = global::Color.White;
			var red = global::Color.Red;

			Line( trace.StartPosition, trace.EndPosition, white, duration, overlay );
			Point( trace.StartPosition, endSize, white, duration, overlay );
			Point( trace.EndPosition, endSize, white, duration, overlay );

			if ( trace.Hit )
			{
				Point( trace.HitPosition, hitSize, red, duration, overlay );
				Line( trace.HitPosition, trace.HitPosition + trace.Normal * normalLength, red, duration, overlay );
			}

			//The swept shape at both ends. In a 2D scene these are seen side-on by an orthographic camera, which is the outline the trace swept.
			TraceShape( trace.StartShape, trace.StartPosition, red, duration, overlay );
			TraceShape( trace.StartShape, trace.EndPosition, trace.Hit ? global::Color.Green : red, duration, overlay );
		}

		//A square this many metres across, facing the eye.
		void Point( Vector3 position, float size, Color color, float duration, bool? overlay )
			=> Sprite( position, new Vector2( size, size ), null, true, 0.0f, color, duration, overlay );

		//The shape a trace swept, standing at one of its ends.
		void TraceShape( in PhysicsTrace.Request.Shape shape, Vector3 position, Color color, float duration, bool? overlay )
		{
			var space = Transform.ToWorld( new Transform( position, shape.StartRot ) );

			switch ( shape.Type )
			{
				case PhysicsTrace.Request.ShapeType.Sphere:
					LineSphere( new Sphere( position, shape.Radius.x ), 8, color, duration, overlay );
					break;

				case PhysicsTrace.Request.ShapeType.Box:
				{
					var sink = LineSink( space, color, duration, overlay );
					DebugShapes.LineBox( ref sink, new BBox( shape.Mins, shape.Maxs ) );
					break;
				}

				case PhysicsTrace.Request.ShapeType.Capsule:
				{
					var sink = LineSink( space, color, duration, overlay );
					DebugShapes.LineCapsule( ref sink, new Capsule( shape.Mins, shape.Maxs, shape.Radius.x ) );
					break;
				}

				case PhysicsTrace.Request.ShapeType.Cylinder:
				{
					var sink = LineSink( space, color, duration, overlay );
					DebugShapes.LineCylinder( ref sink, shape.Mins, shape.Maxs, shape.Radius.x, shape.Radius.y, 16 );
					break;
				}
			}
		}

		/// <summary>Draw a solid cone shape</summary>
		public void SolidCone( Vector3 @base, Vector3 extent, float flRadius, int? segments = null )
		{
			var sink = SolidSink();
			DebugShapes.SolidCone( ref sink, @base, extent, flRadius, segments is int s && s >= 3 ? s : 10 );
		}

		/// <summary>Draw a solid box shape</summary>
		public void SolidBox( BBox box )
		{
			var sink = SolidSink();
			DebugShapes.SolidBox( ref sink, box );
		}

		/// <summary>Draw a solid triangle shape</summary>
		public void SolidTriangle( in Triangle triangle )
		{
			var sink = SolidSink();
			sink.Triangle( triangle.A, triangle.B, triangle.C );
		}

		/// <summary>Draw a solid triangle shape</summary>
		public void SolidTriangle( in Vector3 a, in Vector3 b, in Vector3 c )
		{
			var sink = SolidSink();
			sink.Triangle( a, b, c );
		}

		/// <summary>Multiple solid triangles</summary>
		public void SolidTriangles( in IEnumerable<Triangle> triangles )
		{
			if ( !triangles.Any() ) return;

			var sink = SolidSink();

			foreach ( var triangle in triangles )
			{
				sink.Triangle( triangle.A, triangle.B, triangle.C );
			}
		}

		/// <summary>Draw a filled circle</summary>
		public void SolidCircle( Vector3 center, float radius, float startAngle = 0, float totalDegrees = 360, int sections = 8 )
		{
			var sink = SolidSink();
			DebugShapes.SolidCircle( ref sink, center, radius, startAngle, totalDegrees, sections );
		}

		/// <summary>Draw a filled ring</summary>
		public void SolidRing( Vector3 center, float innerRadius, float outerRadius, float startAngle = 0, float totalDegrees = 360.0f, int sections = 8 )
		{
			var sink = SolidSink();
			DebugShapes.SolidRing( ref sink, center, innerRadius, outerRadius, startAngle, totalDegrees, sections );
		}

		/// <summary>Draw a solid sphere shape</summary>
		public void SolidSphere( Vector3 center, float radius, int hSegments = 8, int vSegments = 8 )
		{
			var sink = SolidSink();
			DebugShapes.SolidSphere( ref sink, center, radius, hSegments, vSegments );
		}

		/// <summary>Draw a solid cylinder shape</summary>
		public void SolidCylinder( Vector3 start, Vector3 end, float radius, int hSegments = 32 )
		{
			var sink = SolidSink();
			DebugShapes.SolidCylinder( ref sink, start, end, radius, hSegments );
		}

		/// <summary>Draw a solid capsule shape</summary>
		public void SolidCapsule( Vector3 start, Vector3 end, float radius, int hSegments, int vSegments )
		{
			var sink = SolidSink();
			DebugShapes.SolidCapsule( ref sink, start, end, radius, hSegments, vSegments );
		}

		/// <summary>Draws a half circle that tries its best to point towards the camera. This is used by the rotation widgets that bias towards the camera.</summary>
		public void ScreenBiasedHalfCircle( Vector3 center, float radius )
		{
			var vecViewPoint = Transform.PointToLocal( Camera.Position );
			var vecForward = (center - vecViewPoint).Normal;

			Vector3 vecAxis = Vector3.Forward;
			float flDot = MathF.Abs( vecForward.Dot( vecAxis ) );

			//Facing the camera, just draw the whole circle
			if ( flDot > 0.95f )
			{
				Draw.LineCircle( 0, radius, sections: 64 );
				return;
			}

			//Draw a half-circle to reduce visual clutter. Find the vector within the rotation
			//plane toward the camera position. This will be our "right" direction for our circle.
			float flPlaneDist = Vector3.Dot( vecViewPoint, vecAxis ) - Vector3.Dot( center, vecAxis );

			Vector3 vecRight = vecViewPoint - flPlaneDist * vecAxis;
			vecRight -= center;
			vecRight = vecRight.Normal;

			//Generate the "up" vector within the rotation plane for the circle.
			Vector3 vecUp = Vector3.Cross( vecAxis, vecRight ).Normal;

			using ( Gizmo.Scope() )
			{
				Transform = Transform.WithRotation( Transform.Rotation * Rotation.LookAt( vecAxis, vecUp ) );
				Draw.LineCircle( 0, radius, 0, 180.0f, sections: 32 );
			}
		}

		/// <summary>Draw a sprite.</summary>
		public void Sprite( Vector3 center, float size, string texture )
		{
			//Textures will get destroyed automatically if they get garbage collected, and we don't want to immediately
			//dispose after use here otherwise the same texture will get loaded from scratch over and over again.

#pragma warning disable CA2000 //Dispose objects before losing scope
			Sprite( center, size, Texture.Load( texture ) );
#pragma warning restore CA2000
		}

		/// <summary>Draw a sprite.</summary>
		public void Sprite( Vector3 center, float size, Texture texture )
		{
			Sprite( center, size, texture, true );
		}

		/// <summary>Draw a sprite.</summary>
		public void Sprite( Vector3 center, Vector2 size, Texture texture, bool worldspace )
		{
			Sprite( center, size, texture, worldspace, 0.0f );
		}

		//Handed over as what it means - a picture, this big, standing here - and built into a quad by the drawer, which knows where the eye is.
		/// <summary>Draw a sprite.</summary>
		public void Sprite( Vector3 center, Vector2 size, Texture texture, bool worldspace, float angle, Color? color = null, float duration = 0, bool? overlay = null )
		{
			Target?.Add( new DebugDrawList.Sprite
			{
				Position = Transform.PointToWorld( center ),
				Size = size,
				WorldSpace = worldspace,
				Angle = angle,
				Tint = color ?? Color,
				Picture = texture,
				IgnoreDepth = overlay ?? IgnoreDepth,
				Life = LifeFor( duration ),
			} );
		}

		/// <summary>Draw a batch of identical sprites. Much faster than calling <see cref="Sprite( Vector3, Vector2, Texture, bool, float, Color?, float, bool? )"/> in a loop, which repeats the per sprite setup for every one.</summary>
		public void Sprites( ReadOnlySpan<Vector3> centers, Vector2 size, Texture texture = null, bool worldspace = true, float angle = 0.0f )
		{
			var list = Target;
			if ( list is null || centers.IsEmpty ) return;

			var transform = Transform;
			var identity = transform == global::Transform.Zero;

			var sprite = new DebugDrawList.Sprite
			{
				Size = size,
				WorldSpace = worldspace,
				Angle = angle,
				Tint = Color,
				Picture = texture,
				IgnoreDepth = IgnoreDepth,
				Life = LifeFor( 0.0f ),
			};

			foreach ( var center in centers )
			{
				sprite.Position = identity ? center : transform.PointToWorld( center );
				list.Add( sprite );
			}
		}

		/// <summary>Draw a batch of identical sprites. Much faster than calling <see cref="Sprite( Vector3, Vector2, Texture, bool, float, Color?, float, bool? )"/> in a loop, which repeats the per sprite setup for every one.</summary>
		public void Sprites( IEnumerable<Vector3> centers, Vector2 size, Texture texture = null, bool worldspace = true, float angle = 0.0f )
		{
			if ( centers is null ) return;

			//Take the span path when the source is already contiguous.
			if ( centers is List<Vector3> list )
			{
				Sprites( CollectionsMarshal.AsSpan( list ), size, texture, worldspace, angle );
				return;
			}

			if ( centers is Vector3[] array )
			{
				Sprites( array.AsSpan(), size, texture, worldspace, angle );
				return;
			}

			foreach ( var center in centers )
			{
				Sprite( center, size, texture, worldspace, angle );
			}
		}
	}
}
