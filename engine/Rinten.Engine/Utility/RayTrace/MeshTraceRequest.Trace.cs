using System.Collections.Generic;

namespace Rinten.Engine.Utility.RayTrace;

//Ray trace against drawn geometry for editor picking: mapping meshes per triangle, models by bounds
partial struct MeshTraceRequest
{
	//Nearest hit, or nothing.
	private readonly Result First()
	{
		Result best = default;
		var bestFraction = float.MaxValue;

		if ( targetModel.IsValid() ) return TargetModelHit();

		if ( targetWorld is null ) return best;

		var candidates = TakeCandidates();

		try
		{
			foreach ( var found in candidates )
			{
				if ( !MayMeet( found ) || !Passes( found ) ) continue;
				if ( !TryHit( found, out var hit ) ) continue;
				if ( hit.Fraction >= bestFraction ) continue;

				best = hit;
				bestFraction = hit.Fraction;
			}
		}
		finally
		{
			GiveCandidates( candidates );
		}

		return best;
	}

	//Every hit, nearest first.
	private readonly Result[] Every()
	{
		List<Result> hits = null;

		if ( targetModel.IsValid() )
		{
			var single = TargetModelHit();

			return single.Hit ? new[] { single } : System.Array.Empty<Result>();
		}

		if ( targetWorld is null ) return System.Array.Empty<Result>();

		var candidates = TakeCandidates();

		try
		{
			foreach ( var found in candidates )
			{
				if ( !MayMeet( found ) || !Passes( found ) ) continue;
				if ( !TryHit( found, out var hit ) ) continue;

				hits ??= new List<Result>();
				hits.Add( hit );
			}
		}
		finally
		{
			GiveCandidates( candidates );
		}

		if ( hits is null ) return System.Array.Empty<Result>();

		hits.Sort( static ( a, b ) => a.Fraction.CompareTo( b.Fraction ) );

		return hits.ToArray();
	}

	//The one model this request was made against, traced in its own space - no world, no transform, no tags to pass.
	private readonly Result TargetModelHit()
	{
		var hit = default( Result );

		if ( targetModel.TraceMesh is not { TriangleCount: > 0 } mesh ) return hit;

		var start = request.start;
		var delta = request.end - start;

		if ( delta.IsNearZeroLength ) return hit;

		if ( !mesh.Trace( start, delta, request.cullMode, out var fraction, out var normal, out var triangle ) )
			return hit;

		var point = start + ( delta * fraction );

		hit.Hit = true;
		hit.Fraction = fraction;
		hit.StartPosition = start;
		hit.EndPosition = point;
		hit.HitPosition = point;
		hit.Distance = ( point - start ).Length;
		hit.Normal = normal;
		hit.HitTriangle = triangle;
		hit.Transform = Transform.Zero;

		return hit;
	}

	//One spare per thread, taken while in use: a filter may run a trace of its own
	[System.ThreadStatic] private static List<SceneObject> spareCandidates;

	//What the world's index says the segment may meet; the caller hands the list back
	private readonly List<SceneObject> TakeCandidates()
	{
		var candidates = spareCandidates ?? new List<SceneObject>();
		spareCandidates = null;

		targetWorld.RayCandidates( request.start, request.end - request.start, candidates );

		return candidates;
	}

	private static void GiveCandidates( List<SceneObject> candidates )
	{
		candidates.Clear();
		spareCandidates = candidates;
	}

	private readonly bool MayMeet( SceneObject found )
	{
		if ( !IsDrawnAndTagged( found ) ) return false;

		//Nothing to go by: the shape decides - see TryHit
		var bounds = found.Bounds;
		if ( bounds.Size.AlmostEqual( 0.0f ) ) return true;

		return TraceBounds( bounds, request.start, request.end - request.start, out _, out _ );
	}

	//And what the caller's filter lets through, on this thread.
	private readonly bool Passes( SceneObject found ) => filterCallback is null || filterCallback( found );

	//What is worth tracing against: drawn, and let through by the tags.
	private readonly bool IsDrawnAndTagged( SceneObject found )
	{
		if ( !found.IsValid() ) return false;

		//What is not drawn is not under the cursor. This is the whole of what "hidden" means to somebody clicking in a viewport.
		if ( !found.RenderingEnabled ) return false;

		//Lights and probes have reach bounds, not surfaces; they're picked by their handles instead
		if ( found is SceneLight or SceneLightProbe ) return false;

		return PassesTags( found );
	}

	private readonly unsafe bool PassesTags( SceneObject found )
	{
		//Read once: the set is built fresh on every ask.
		IReadOnlySet<uint> tags = null;

		var request = this.request;

		bool Has( uint token )
		{
			tags ??= found.Tags?.GetTokens();

			return tags is not null && tags.Contains( token );
		}

		for ( var i = 0; i < 8; i++ )
		{
			var token = request.tagExclude[i];
			if ( token == 0 ) break;
			if ( Has( token ) ) return false;
		}

		for ( var i = 0; i < 8; i++ )
		{
			var token = request.tagRequire[i];
			if ( token == 0 ) break;
			if ( !Has( token ) ) return false;
		}

		var wantsAny = false;

		for ( var i = 0; i < 8; i++ )
		{
			var token = request.tagAny[i];
			if ( token == 0 ) break;

			wantsAny = true;

			if ( Has( token ) ) return true;
		}

		return !wantsAny;
	}

	//Traces one object, in its own space: the segment goes local, so a hit is a fraction along it and scale - even an uneven one - takes care of itself.
	private readonly bool TryHit( SceneObject found, out Result hit )
	{
		hit = default;

		var transform = found.Transform;

		var start = request.start;
		var end = request.end;

		var delta = end - start;

		if ( delta.IsNearZeroLength ) return false;

		float fraction;
		Vector3 normal;
		var triangle = -1;

		if ( Triangles( found ) is { Count: >= 3 } indices && Positions( found ) is { Count: > 0 } positions )
		{
			//Triangles are the component's own, so the segment goes into its space - which is also what
			//makes an uneven scale come out right, the fraction along the segment being the same either side.
			var localStart = transform.PointToLocal( start );
			var localDelta = transform.PointToLocal( end ) - localStart;

			if ( localDelta.IsNearZeroLength ) return false;

			if ( !TraceTriangles( positions, indices, localStart, localDelta, out fraction, out var localNormal, out triangle ) )
				return false;

			normal = transform.NormalToWorld( localNormal ).Normal;
		}
		else
		{
			//Bounds are already in the world - what a scene object reports is its model's put through its transform - so this stays there.
			var bounds = found.Bounds;

			if ( bounds.Size.AlmostEqual( 0.0f ) ) return false;

			if ( !TraceBounds( bounds, start, delta, out fraction, out normal ) )
				return false;

			//The box says the ray came near; the shape says whether it touched. Asked for in this order on purpose - reading a model's geometry back
			//off the card is what builds the tree, and a scene full of models would build every one of them the first time anybody moved the mouse.
			if ( ModelTriangles( found ) is { TriangleCount: > 0 } mesh )
			{
				var localStart = transform.PointToLocal( start );
				var localDelta = transform.PointToLocal( end ) - localStart;

				if ( localDelta.IsNearZeroLength ) return false;

				if ( !mesh.Trace( localStart, localDelta, request.cullMode, out fraction, out var localNormal, out triangle ) )
					return false;

				normal = transform.NormalToWorld( localNormal ).Normal;
			}
			else if ( PosedBoxes( found ) is { Any: true } posed && found is SceneModel posedModel )
			{
				var bones = BoneScratch( posed.Count );

				posedModel.GetBoneWorldTransforms( bones );

				if ( !posed.Trace( bones, start, delta, out fraction, out normal, out _ ) )
					return false;
			}
			else if ( bounds.Contains( start ) )
			{
				//Camera inside a bounds-only box: ignore it, or that object would take every click
				return false;
			}
		}

		var worldHit = start + delta * fraction;

		hit = new Result
		{
			Hit = true,
			Fraction = fraction,
			StartPosition = start,
			EndPosition = worldHit,
			HitPosition = worldHit,
			Distance = ( worldHit - start ).Length,
			Normal = normal,
			HitTriangle = triangle,
			Transform = transform,
			SceneObject = found,
		};

		return true;
	}

	//The triangles of a mapping mesh, or nothing for anything else.
	private static IReadOnlyList<int> Triangles( SceneObject found )
		=> found.Component is MeshComponent component ? component.Mesh?.TraceIndices : null;

	//Model triangles for static models; animated ones fall back to bounds since bones move vertices
	private static ModelTraceMesh ModelTriangles( SceneObject found )
	{
		if ( found is SceneModel ) return null;

		var model = found.Model;

		return model.IsValid() ? model.TraceMesh : null;
	}

	//The bone boxes of an animated model, which is what stands in for its triangles once the bones have moved them.
	private static ModelBoneBoxes PosedBoxes( SceneObject found )
	{
		if ( found is not SceneModel ) return null;

		var model = found.Model;

		return model.IsValid() ? model.BoneBoxes : null;
	}

	//Somewhere to read a skeleton into, one per thread.
	[ThreadStatic] private static Transform[] boneScratch;

	private static Span<Transform> BoneScratch( int count )
	{
		if ( boneScratch is null || boneScratch.Length < count )
		{
			boneScratch = new Transform[System.Math.Max( count, 64 )];
		}

		return boneScratch.AsSpan( 0, count );
	}

	private static IReadOnlyList<Vector3> Positions( SceneObject found )
		=> found.Component is MeshComponent component ? component.Mesh?.TracePositions : null;

	private readonly bool TraceTriangles( IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices,
		Vector3 start, Vector3 delta, out float fraction, out Vector3 normal, out int triangle )
	{
		fraction = float.MaxValue;
		normal = default;
		triangle = -1;

		var count = indices.Count / 3;

		for ( var at = 0; at < count; ++at )
		{
			var i0 = indices[at * 3];
			var i1 = indices[at * 3 + 1];
			var i2 = indices[at * 3 + 2];

			if ( i0 < 0 || i1 < 0 || i2 < 0 ) continue;
			if ( i0 >= positions.Count || i1 >= positions.Count || i2 >= positions.Count ) continue;

			if ( !TraceTriangle( positions[i0], positions[i1], positions[i2], start, delta,
				out var t, out var face ) ) continue;

			if ( t >= fraction ) continue;

			fraction = t;
			normal = face;
			triangle = at;
		}

		return triangle >= 0;
	}

	//Möller-Trumbore, with the segment left unnormalised so what comes out is a fraction along it. Which side counts is the cull mode the trace was made
	//with: nothing culled, backs only, or fronts only - the last being what picking wants, so that the far wall of a room is not what a click finds.
	private readonly bool TraceTriangle( Vector3 a, Vector3 b, Vector3 c, Vector3 start, Vector3 delta,
		out float fraction, out Vector3 normal )
		=> TraceTriangle( a, b, c, start, delta, request.cullMode, out fraction, out normal );

	internal static bool TraceTriangle( Vector3 a, Vector3 b, Vector3 c, Vector3 start, Vector3 delta,
		int cullMode, out float fraction, out Vector3 normal )
	{
		fraction = 0;
		normal = default;

		var ab = b - a;
		var ac = c - a;

		var p = Vector3.Cross( delta, ac );
		var determinant = Vector3.Dot( ab, p );

		//Facing away, edge on, or degenerate. Cull mode two keeps fronts, one keeps backs, zero keeps both.
		switch ( cullMode )
		{
			case 2 when determinant <= float.Epsilon:
			case 1 when determinant >= -float.Epsilon:
				return false;

			default:
				if ( determinant > -float.Epsilon && determinant < float.Epsilon ) return false;
				break;
		}

		var inverse = 1.0f / determinant;
		var toStart = start - a;

		var u = Vector3.Dot( toStart, p ) * inverse;
		if ( u < 0.0f || u > 1.0f ) return false;

		var q = Vector3.Cross( toStart, ab );

		var v = Vector3.Dot( delta, q ) * inverse;
		if ( v < 0.0f || u + v > 1.0f ) return false;

		var t = Vector3.Dot( ac, q ) * inverse;

		//Behind the start, or past the end of the segment.
		if ( t < 0.0f || t > 1.0f ) return false;

		fraction = t;
		normal = Vector3.Cross( ab, ac ).Normal;

		return true;
	}

	//The slab test, against the same segment, for what has no triangles to offer.
	internal static bool TraceBounds( BBox bounds, Vector3 start, Vector3 delta,
		out float fraction, out Vector3 normal )
	{
		fraction = 0;
		normal = default;

		var near = 0.0f;
		var far = 1.0f;

		var axis = -1;
		var sign = 0.0f;

		for ( var i = 0; i < 3; ++i )
		{
			var origin = start[i];
			var direction = delta[i];
			var low = bounds.Mins[i];
			var high = bounds.Maxs[i];

			if ( MathF.Abs( direction ) < 1e-8f )
			{
				//Parallel to this pair of planes and outside them: no crossing anywhere along the segment.
				if ( origin < low || origin > high ) return false;

				continue;
			}

			var inverse = 1.0f / direction;

			var first = ( low - origin ) * inverse;
			var second = ( high - origin ) * inverse;

			var enterSign = -1.0f;

			if ( first > second )
			{
				(first, second) = (second, first);
				enterSign = 1.0f;
			}

			if ( first > near )
			{
				near = first;
				axis = i;
				sign = enterSign;
			}

			if ( second < far ) far = second;

			if ( near > far ) return false;
		}

		fraction = near;

		if ( axis >= 0 )
		{
			var face = Vector3.Zero;
			face[axis] = sign;
			normal = face;
		}
		else
		{
			//Started inside it: call the face the one the segment came from.
			normal = -delta.Normal;
		}

		return true;
	}
}
