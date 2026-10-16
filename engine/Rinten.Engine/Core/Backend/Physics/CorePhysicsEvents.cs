using System.Runtime.InteropServices;

namespace Rinten;

//Turns contacts into touch/hit/trigger callbacks and derives "still touching"; one per world
internal sealed unsafe partial class CorePhysicsEvents
{
	//Events one step may report before the rest are dropped. The buffer is a field rather than a
	//stackalloc because it is tens of kilobytes and this runs on whatever thread the world steps on.
	private const int MaxEvents = 1024;

	private enum Kind
	{
		TouchBegin = 0,
		TouchEnd = 1,
		Hit = 2,
		TriggerBegin = 3,
		TriggerEnd = 4,
	}

	//A pair of shapes in contact, ordered so it hashes the same either way round.
	private readonly record struct Pair( ulong A, ulong B )
	{
		public static Pair Of( ulong a, ulong b ) => a < b ? new Pair( a, b ) : new Pair( b, a );
	}

	private readonly HashSet<Pair> touching = new();

	//The copy DispatchPersists walks, kept between steps
	private readonly List<Pair> walking = new();
	private readonly List<Pair> began = new();
	private readonly CorePhysics.ContactEvent[] events = new CorePhysics.ContactEvent[MaxEvents];

	/// <summary>Drain the last step's contacts and dispatch them.</summary>
	public void Dispatch( PhysicsWorld world, CorePhysicsWorldBackend backend )
	{
		if ( world is null || backend is null || !backend.IsValid ) return;

		int count = CorePhysics.WorldDrainContacts( backend.Handle, events );

		began.Clear();

		for ( var i = 0; i < count; i++ )
		{
			ref var e = ref events[i];

			var a = CoreShapeRegistry.Get( e.ShapeA );
			var b = CoreShapeRegistry.Get( e.ShapeB );

			//A shape destroyed by a handler earlier in this batch, or by the step itself - Box3D reports end events for those.
			if ( a is null || b is null ) continue;
			if ( !a.Body.IsValid() || !b.Body.IsValid() ) continue;

			var left = new PhysicsContact.Target( a );
			var right = new PhysicsContact.Target( b );

			var contact = new PhysicsContact(
				new Vector3( e.PointX, e.PointY, e.PointZ ),
				new Vector3( e.NormalX, e.NormalY, e.NormalZ ),
				e.Speed );

			try
			{
				switch ( (Kind)e.Kind )
				{
					case Kind.TouchBegin:
					{
						var pair = Pair.Of( e.ShapeA, e.ShapeB );
						touching.Add( pair );
						began.Add( pair );

						world.OnIntersectionStart?.InvokeWithWarning( new PhysicsIntersection( left, right, contact ) );
						left.Body.DispatchIntersectionStart( new PhysicsIntersection( left, right, contact ) );
						right.Body.DispatchIntersectionStart( new PhysicsIntersection( right, left, contact ) );
						break;
					}

					case Kind.TouchEnd:
					{
						touching.Remove( Pair.Of( e.ShapeA, e.ShapeB ) );

						world.OnIntersectionEnd?.InvokeWithWarning( new PhysicsIntersectionEnd( left, right ) );
						left.Body.DispatchIntersectionEnd( new PhysicsIntersectionEnd( left, right ) );
						right.Body.DispatchIntersectionEnd( new PhysicsIntersectionEnd( right, left ) );
						break;
					}

					case Kind.Hit:
						world.OnIntersectionHit?.InvokeWithWarning( new PhysicsIntersection( left, right, contact ) );
						break;

					case Kind.TriggerBegin:
						touching.Add( Pair.Of( e.ShapeA, e.ShapeB ) );

						left.Body.DispatchTriggerBegin( new PhysicsIntersection( left, right, contact ) );
						right.Body.DispatchTriggerBegin( new PhysicsIntersection( right, left, contact ) );
						break;

					case Kind.TriggerEnd:
						touching.Remove( Pair.Of( e.ShapeA, e.ShapeB ) );

						left.Body.DispatchTriggerEnd( new PhysicsIntersectionEnd( left, right ) );
						right.Body.DispatchTriggerEnd( new PhysicsIntersectionEnd( right, left ) );
						break;
				}
			}
			catch ( Exception ex )
			{
				Log.Error( ex );
			}
		}

		DispatchPersists( world );
	}

	//Everything still touching that did not start touching this step. A pair that
	//only just began already had its start event; a persist as well would double up.
	private void DispatchPersists( PhysicsWorld world )
	{
		if ( touching.Count == 0 ) return;

		//Handlers can destroy shapes, so walk a copy and prune as we go.
		walking.Clear();
		walking.AddRange( touching );

		foreach ( var pair in walking )
		{
			if ( began.Contains( pair ) ) continue;

			var a = CoreShapeRegistry.Get( pair.A );
			var b = CoreShapeRegistry.Get( pair.B );

			if ( a is null || b is null || !a.Body.IsValid() || !b.Body.IsValid() )
			{
				//The pair is gone and Box3D will not report an end for a shape that no longer exists, so drop it here or it accumulates.
				touching.Remove( pair );
				continue;
			}

			var left = new PhysicsContact.Target( a );
			var right = new PhysicsContact.Target( b );
			var contact = default( PhysicsContact );

			try
			{
				world.OnIntersectionUpdate?.InvokeWithWarning( new PhysicsIntersection( left, right, contact ) );
				left.Body.DispatchIntersectionUpdate( new PhysicsIntersection( left, right, contact ) );
				right.Body.DispatchIntersectionUpdate( new PhysicsIntersection( right, left, contact ) );
			}
			catch ( Exception ex )
			{
				Log.Error( ex );
			}
		}
	}

	//How many pairs are being tracked as touching. Diagnostics.
	internal int TouchingCount => touching.Count;

	/// <summary>Whether two shapes are in contact right now.</summary>
	public bool AreTouching( ulong shapeA, ulong shapeB ) => touching.Contains( Pair.Of( shapeA, shapeB ) );

	/// <summary>Whether any of these shapes is in contact with a given shape.</summary>
	public bool IsTouchingAny( IReadOnlyList<PhysicsShape> shapes, ulong other )
	{
		if ( touching.Count == 0 ) return false;

		for ( var i = 0; i < shapes.Count; i++ )
		{
			if ( shapes[i]?.Backend is not CorePhysicsShapeBackend core ) continue;
			if ( touching.Contains( Pair.Of( core.Handle, other ) ) ) return true;
		}

		return false;
	}
}
