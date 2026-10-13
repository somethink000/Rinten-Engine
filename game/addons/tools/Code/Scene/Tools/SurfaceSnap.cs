namespace Editor;

/// <summary>Puts a selection's bounds against surfaces along a handle's axes.</summary>
public sealed class SurfaceSnap
{
	//World units looked along each direction
	const float Reach = 10000.0f;

	//Rays across the bounds per direction, and how far in from the bounds' edges the outer ones start
	const int Rays = 5;
	const float Inset = 0.02f;

	const int AllDirections = 0b111111;

	/// <summary>The handle axis that is up: 0 x (Right), 1 y (Up), 2 z (Backward).</summary>
	public const int UpAxis = 1;

	readonly Scene scene;
	readonly GameObject[] selection;
	readonly Rotation rotation;

	//The selection's bounds when the snap was taken, in the handle's axes
	readonly BBox bounds;

	//How long each ray is: the reach, plus room for the half of the bounds it starts inside
	readonly float length;

	//Per direction (axis * 2, +1 for negative) for the shift they were cast at; NaN until cast
	readonly float[] distances = new float[6];
	Vector3 castShift;

	//The side of its normal a plane drag sits on, chosen at its first frame so a wall walked into is climbed, not swapped for
	int planeSide = -1;

	/// <summary>Takes the selection's bounds where it stands now.</summary>
	public SurfaceSnap( Scene scene, IEnumerable<GameObject> selection, Rotation rotation )
	{
		this.scene = scene;
		this.selection = selection.Where( x => x.IsValid() ).ToArray();
		this.rotation = rotation;

		bounds = BBox.FromPoints( this.selection.SelectMany( x => x.GetBounds().Corners ).Select( x => x * rotation.Inverse ) );
		length = Reach + bounds.Size.Length;
		Array.Fill( distances, float.NaN );
	}

	/// <summary>Where a drag on the given part of a Position control lands, the selection snapped onto surfaces.</summary>
	//An arrow slides from where the snap was taken into a surface; a plane moves freely and sits on one along its normal;
	//the center moves freely onto the nearest
	public Vector3 Offset( Gizmo.PositionPart part, int axis, Vector3 taken, Vector3 free, Vector3 dragSinceTaken )
	{
		switch ( part )
		{
			case Gizmo.PositionPart.Arrow:
				var along = (dragSinceTaken * rotation.Inverse)[axis];
				if ( along.AlmostEqual( 0.0f ) ) return taken;

				return taken + (Toward( axis, along < 0 ) ?? Vector3.Zero);

			case Gizmo.PositionPart.Plane:
				if ( planeSide < 0 )
				{
					Nearest( free - taken, 0b11 << (axis * 2), out planeSide );
					if ( planeSide < 0 ) return free;
				}

				return free + (Nearest( free - taken, 1 << planeSide ) ?? Vector3.Zero);

			default:
				return free + (Nearest( free - taken, AllDirections ) ?? Vector3.Zero);
		}
	}

	/// <summary>The move along one handle axis that puts the bounds on a surface, or null.</summary>
	public Vector3? Toward( int axis, bool negative ) => Nearest( Vector3.Zero, 1 << (axis * 2 + (negative ? 1 : 0)) );

	Vector3? Nearest( Vector3 shift, int mask ) => Nearest( shift, mask, out _ );

	//The shortest move onto a surface among the directions in the mask, the bounds moved by shift; negative pushes out of one
	Vector3? Nearest( Vector3 shift, int mask, out int side )
	{
		side = -1;

		if ( selection.Length == 0 ) return null;

		if ( shift != castShift )
		{
			Array.Fill( distances, float.NaN );
			castShift = shift;
		}

		var best = -1;

		for ( var i = 0; i < 6; i++ )
		{
			if ( (mask & (1 << i)) == 0 ) continue;
			if ( best < 0 || MathF.Abs( Distance( i ) ) < MathF.Abs( Distance( best ) ) ) best = i;
		}

		if ( best < 0 || Distance( best ) == float.MaxValue ) return null;

		side = best;

		return Direction( best ) * rotation * Distance( best );
	}

	static Vector3 Direction( int index )
	{
		var direction = Vector3.Zero;
		direction[index / 2] = index % 2 == 0 ? 1.0f : -1.0f;
		return direction;
	}

	//Rays from the bounds' middle plane; the nearest hit less the half size is how far its face is from the surface.
	//A ray that starts inside something comes out of it first, so the face lands on top of what it went into.
	float Distance( int index )
	{
		if ( !float.IsNaN( distances[index] ) ) return distances[index];

		var box = bounds.Translate( castShift * rotation.Inverse );
		var axis = index / 2;
		var u = (axis + 1) % 3;
		var v = (axis + 2) % 3;
		var direction = Direction( index ) * rotation;
		var half = box.Size[axis] * 0.5f;
		var best = float.MaxValue;

		for ( var i = 0; i < Rays; i++ )
		{
			for ( var j = 0; j < Rays; j++ )
			{
				var point = Vector3.Zero;
				point[axis] = box.Center[axis];
				point[u] = box.Mins[u] + box.Size[u] * Inset.LerpTo( 1.0f - Inset, i / (Rays - 1.0f) );
				point[v] = box.Mins[v] + box.Size[v] * Inset.LerpTo( 1.0f - Inset, j / (Rays - 1.0f) );

				var from = point * rotation;

				//Leaving through a face's back is having started inside what it belongs to
				var exit = Cast( from, -direction, true );

				if ( exit.Hit && exit.Normal.Dot( -direction ) > 0.0f )
				{
					best = MathF.Min( best, -exit.Fraction * length - half );
					continue;
				}

				var result = Cast( from, direction, false );

				if ( result.Hit && !result.StartedSolid )
					best = MathF.Min( best, result.Fraction * length - half );
			}
		}

		distances[index] = best;
		return best;
	}

	SceneTraceResult Cast( Vector3 from, Vector3 direction, bool backs )
	{
		var trace = scene.Trace.Ray( from, from + direction * length )
			.WithoutTags( "trigger", "hidden" )
			.UseRenderMeshes( true, backs );

		foreach ( var go in selection )
		{
			trace = trace.IgnoreGameObjectHierarchy( go );
		}

		return trace.Run();
	}
}
