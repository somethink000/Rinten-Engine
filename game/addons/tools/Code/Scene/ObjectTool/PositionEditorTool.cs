
namespace Editor;

/// <summary>Move selected Gameobjects.<br/> <br/> <b>Ctrl</b> - toggle snap to grid<br/> <b>Shift</b> - duplicate selection<br/> <b>Alt</b> - an arrow or plane snaps the selection's edge to the first surface the drag heads at, the center handle to the nearest</summary>
[Title( "Move/Position" )]
[Icon( "control_camera" )]
[Alias( "tools.position-tool" )]
[Group( "1" )]
[Order( 0 )]
public class PositionEditorTool : EditorTool
{
	readonly Dictionary<GameObject, Transform> startPoints = [];
	readonly HashSet<Rigidbody> bodies = [];

	Vector3 moveDelta;
	Vector3 handlePosition;

	//Per handle direction (axis * 2, +1 for negative): how far the selection goes before its bounds meet a surface
	readonly Dictionary<int, float> surfaceDistances = [];
	//The selection's bounds at the start of the drag, in the handle's axes
	BBox surfaceBounds;

	//World units the surface snap looks along each direction
	const float SurfaceReach = 10000.0f;

	//Rays across each face of the bounds, and how far in from the face's edges the outer ones start
	const int SurfaceRays = 5;
	const float SurfaceInset = 0.02f;

	IDisposable undoScope;

	public override void OnDisabled()
	{
		base.OnDisabled();

		ClearBodies();
	}

	private void ClearBodies()
	{
		foreach ( var body in bodies )
		{
			if ( !body.IsValid() )
				continue;

			body.SetTargetTransform( null );
		}

		bodies.Clear();
	}

	public override void OnUpdate()
	{
		var nonSceneGos = Selection.OfType<GameObject>().Where( go => go.GetType() != typeof( Rinten.Scene ) );
		if ( nonSceneGos.Count() == 0 ) return;

		var positions = nonSceneGos.Select( x => x.WorldPosition ).ToArray();
		var centroid = positions.Aggregate( Vector3.Zero, ( sum, p ) => sum + p ) / positions.Length;
		var handleRotation = Gizmo.Settings.GlobalSpace ? Rotation.Identity : nonSceneGos.FirstOrDefault().WorldRotation;

		if ( !Gizmo.Pressed.Any && Gizmo.HasMouseFocus )
		{
			ClearBodies();

			startPoints.Clear();
			moveDelta = default;
			handlePosition = centroid;
			surfaceDistances.Clear();
			undoScope?.Dispose();
			undoScope = null;
		}

		using ( Gizmo.Scope( "Tool", new Transform( centroid ) ) )
		{
			Gizmo.Hitbox.DepthBias = 0.01f;

			Vector3? surfaceHit = null;
			var centerPressed = false;

			var moved = Gizmo.Control.Position( "position", Vector3.Zero, out var delta, handleRotation, centerRaycast: () =>
			{
				centerPressed = true;
				surfaceHit = RaycastToSurface( nonSceneGos )?.HitPosition;
				return surfaceHit;
			} );

			//Alt snaps even on frames the pointer stays still, so pressing it mid-drag shows at once
			var toSurface = Gizmo.IsAltPressed && (centerPressed || startPoints.Count != 0);

			if ( moved || toSurface )
			{
				StartDrag( nonSceneGos, handleRotation );

				if ( moved )
					moveDelta = surfaceHit.HasValue ? surfaceHit.Value - handlePosition : moveDelta + delta;

				var offset = Gizmo.Snap( handlePosition, moveDelta, handleRotation ) - handlePosition;

				if ( toSurface && SurfaceOffset( centerPressed, handleRotation ) is Vector3 snapped )
					offset = snapped;

				foreach ( var entry in startPoints )
				{
					OnMoveObject( entry.Key, entry.Value.Add( offset, true ) );
				}
			}
		}
	}

	//The move that puts the selection's bounds against a surface: the nearest one from the center handle,
	//else the one the drag heads at along its strongest handle axis. Null when there is none in reach.
	private Vector3? SurfaceOffset( bool nearest, Rotation rotation )
	{
		var best = 0;

		if ( nearest )
		{
			for ( var i = 1; i < 6; i++ )
			{
				if ( SurfaceDistance( i, rotation ) < SurfaceDistance( best, rotation ) )
					best = i;
			}
		}
		else
		{
			var local = moveDelta * rotation.Inverse;
			var axis = 0;

			for ( var i = 1; i < 3; i++ )
			{
				if ( MathF.Abs( local[i] ) > MathF.Abs( local[axis] ) ) axis = i;
			}

			if ( local[axis].AlmostEqual( 0.0f ) ) return null;

			best = axis * 2 + (local[axis] > 0 ? 0 : 1);
		}

		var distance = SurfaceDistance( best, rotation );
		if ( distance == float.MaxValue ) return null;

		return Direction( best ) * rotation * distance;
	}

	private static Vector3 Direction( int index )
	{
		var direction = Vector3.Zero;
		direction[index / 2] = index % 2 == 0 ? 1.0f : -1.0f;
		return direction;
	}

	//Rays off the leading face of the selection's bounds, taken in the handle's axes; MaxValue when none hits
	private float SurfaceDistance( int index, Rotation rotation )
	{
		if ( surfaceDistances.TryGetValue( index, out var found ) )
			return found;

		var bounds = surfaceBounds;
		var axis = index / 2;
		var u = (axis + 1) % 3;
		var v = (axis + 2) % 3;
		var direction = Direction( index ) * rotation;
		var best = float.MaxValue;

		//Rays start a little inside the face, so a surface the face already rests on is still met
		var back = MathF.Max( bounds.Size[axis] * 0.01f, 0.001f );

		for ( var i = 0; i < SurfaceRays; i++ )
		{
			for ( var j = 0; j < SurfaceRays; j++ )
			{
				var point = Vector3.Zero;
				point[axis] = index % 2 == 0 ? bounds.Maxs[axis] : bounds.Mins[axis];
				point[u] = bounds.Mins[u] + bounds.Size[u] * SurfaceInset.LerpTo( 1.0f - SurfaceInset, i / (SurfaceRays - 1.0f) );
				point[v] = bounds.Mins[v] + bounds.Size[v] * SurfaceInset.LerpTo( 1.0f - SurfaceInset, j / (SurfaceRays - 1.0f) );

				var from = point * rotation - direction * back;
				var trace = Scene.Trace.Ray( from, from + direction * SurfaceReach )
					.WithoutTags( "trigger", "hidden" )
					.UseRenderMeshes( true, false );

				foreach ( var go in startPoints.Keys )
				{
					trace = trace.IgnoreGameObjectHierarchy( go );
				}

				var result = trace.Run();

				if ( result.Hit && !result.StartedSolid )
					best = MathF.Min( best, MathF.Max( result.Fraction * SurfaceReach - back, 0.0f ) );
			}
		}

		surfaceDistances[index] = best;
		return best;
	}

	private SceneTraceResult? RaycastToSurface( IEnumerable<GameObject> targets )
	{
		var trace = Trace;

		foreach ( var go in targets )
		{
			trace = trace.IgnoreGameObjectHierarchy( go );
		}

		var result = trace.Run();

		return result.Hit ? result : null;
	}

	private void StartDrag( IEnumerable<GameObject> selectedGos, Rotation rotation )
	{
		if ( startPoints.Count != 0 )
			return;

		if ( Gizmo.IsShiftPressed )
		{
			undoScope ??= SceneEditorSession.Active.UndoScope( "Duplicate Object(s)" ).WithGameObjectCreations().Push();

			DuplicateSelection();
		}
		else
		{
			undoScope ??= SceneEditorSession.Active.UndoScope( "Transform Object(s)" ).WithGameObjectChanges( selectedGos, GameObjectUndoFlags.Properties ).Push();

			selectedGos.DispatchPreEdited( nameof( GameObject.LocalPosition ) );
		}

		foreach ( var entry in selectedGos )
		{
			startPoints[entry] = entry.WorldTransform;
		}

		surfaceBounds = BBox.FromPoints( startPoints.Keys.SelectMany( x => x.GetBounds().Corners ).Select( x => x * rotation.Inverse ) );
	}

	private void OnMoveObject( GameObject gameObject, Transform transform )
	{
		if ( !gameObject.IsValid() )
			return;

		if ( !Scene.IsEditor )
		{
			var rb = gameObject.GetComponent<Rigidbody>();

			if ( rb.IsValid() && rb.MotionEnabled )
			{
				bodies.Add( rb );
				rb.SetTargetTransform( transform );

				return;
			}
		}

		gameObject.BreakProceduralBone();
		gameObject.WorldTransform = transform;

		gameObject.DispatchEdited( nameof( GameObject.LocalPosition ) );
	}

	//The key is on the scene view's tool strip - see ToolSidebarPanel.Shortcuts
}

