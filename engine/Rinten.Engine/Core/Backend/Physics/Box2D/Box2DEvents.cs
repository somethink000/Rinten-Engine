using Rinten.Native.Box2D;
using static Rinten.Native.Box2D.Box2D;

namespace Rinten;

//Turns Box2D contact and sensor events into touch/hit/trigger callbacks, counted per managed shape
internal sealed unsafe class Box2DEvents
{
	private readonly record struct Pair( long A, long B )
	{
		public static Pair Of( long a, long b ) => a < b ? new Pair( a, b ) : new Pair( b, a );
	}

	private sealed class Touch
	{
		public PhysicsShape A;
		public PhysicsShape B;
		public int Count;
		public bool Trigger;
	}

	private readonly Dictionary<Pair, Touch> touching = new();

	//The copy DispatchPersists walks, kept between steps
	private readonly List<KeyValuePair<Pair, Touch>> walking = new();
	private readonly HashSet<Pair> began = new();

	public bool AreTouching( long a, long b ) => touching.ContainsKey( Pair.Of( a, b ) );

	private static long SerialOf( PhysicsShape shape ) => shape?.Backend is Box2DShapeBackend s ? s.Serial : 0;

	//Count one more piece touching; true when the pair has just started.
	private bool Begin( PhysicsShape a, PhysicsShape b, bool trigger )
	{
		var pair = Pair.Of( SerialOf( a ), SerialOf( b ) );

		if ( touching.TryGetValue( pair, out var touch ) )
		{
			touch.Count++;
			return false;
		}

		touching[pair] = new Touch { A = a, B = b, Count = 1, Trigger = trigger };
		began.Add( pair );
		return true;
	}

	//Count one fewer; true when the pair has just stopped.
	private bool End( PhysicsShape a, PhysicsShape b )
	{
		var pair = Pair.Of( SerialOf( a ), SerialOf( b ) );
		if ( !touching.TryGetValue( pair, out var touch ) ) return false;

		if ( --touch.Count > 0 ) return false;

		touching.Remove( pair );
		return true;
	}

	private static bool Live( PhysicsShape shape ) => shape is not null && shape.IsValid() && shape.Body.IsValid();

	public void Dispatch( PhysicsWorld world, Box2DWorldBackend backend )
	{
		if ( world is null || backend is null || !backend.IsValid ) return;

		began.Clear();

		var contacts = b2World_GetContactEvents( backend.Id );
		var sensors = b2World_GetSensorEvents( backend.Id );

		//Copied before anything is dispatched: a handler that destroys a shape or a body changes what Box2D is holding these arrays for.
		var beginContacts = Copy( contacts.beginEvents, contacts.beginCount );
		var endContacts = Copy( contacts.endEvents, contacts.endCount );
		var hitContacts = Copy( contacts.hitEvents, contacts.hitCount );
		var beginSensors = Copy( sensors.beginEvents, sensors.beginCount );
		var endSensors = Copy( sensors.endEvents, sensors.endCount );

		//The contact data for a begin is read now too, for the same reason.
		var beginData = new PhysicsContact[beginContacts.Length];
		for ( var i = 0; i < beginContacts.Length; i++ )
			beginData[i] = ContactOf( beginContacts[i].contactId );

		for ( var i = 0; i < beginContacts.Length; i++ )
		{
			var a = Box2DRegistry.Shape( beginContacts[i].shapeIdA );
			var b = Box2DRegistry.Shape( beginContacts[i].shapeIdB );
			if ( !Live( a ) || !Live( b ) ) continue;
			if ( !Begin( a, b, false ) ) continue;

			Guard( () =>
			{
				var left = new PhysicsContact.Target( a );
				var right = new PhysicsContact.Target( b );

				world.OnIntersectionStart?.InvokeWithWarning( new PhysicsIntersection( left, right, beginData[i] ) );
				a.Body.DispatchIntersectionStart( new PhysicsIntersection( left, right, beginData[i] ) );
				b.Body.DispatchIntersectionStart( new PhysicsIntersection( right, left, beginData[i] ) );
			} );
		}

		foreach ( var e in endContacts )
		{
			//A shape destroyed this step has no managed side left to tell.
			var a = Box2DRegistry.Shape( e.shapeIdA );
			var b = Box2DRegistry.Shape( e.shapeIdB );
			if ( !Live( a ) || !Live( b ) ) continue;
			if ( !End( a, b ) ) continue;

			Guard( () =>
			{
				var left = new PhysicsContact.Target( a );
				var right = new PhysicsContact.Target( b );

				world.OnIntersectionEnd?.InvokeWithWarning( new PhysicsIntersectionEnd( left, right ) );
				a.Body.DispatchIntersectionEnd( new PhysicsIntersectionEnd( left, right ) );
				b.Body.DispatchIntersectionEnd( new PhysicsIntersectionEnd( right, left ) );
			} );
		}

		foreach ( var e in hitContacts )
		{
			var a = Box2DRegistry.Shape( e.shapeIdA );
			var b = Box2DRegistry.Shape( e.shapeIdB );
			if ( !Live( a ) || !Live( b ) ) continue;

			var contact = new PhysicsContact( Box2DPlane.Point( e.point, a.Body.Position.z ),
				new Vector3( e.normal.x, e.normal.y, 0 ), e.approachSpeed );

			Guard( () => world.OnIntersectionHit?.InvokeWithWarning(
				new PhysicsIntersection( new PhysicsContact.Target( a ), new PhysicsContact.Target( b ), contact ) ) );
		}

		foreach ( var e in beginSensors )
		{
			var sensor = Box2DRegistry.Shape( e.sensorShapeId );
			var visitor = Box2DRegistry.Shape( e.visitorShapeId );
			if ( !Live( sensor ) || !Live( visitor ) ) continue;
			if ( !Begin( sensor, visitor, true ) ) continue;

			Guard( () =>
			{
				var left = new PhysicsContact.Target( sensor );
				var right = new PhysicsContact.Target( visitor );

				sensor.Body.DispatchTriggerBegin( new PhysicsIntersection( left, right, default ) );
				visitor.Body.DispatchTriggerBegin( new PhysicsIntersection( right, left, default ) );
			} );
		}

		foreach ( var e in endSensors )
		{
			var sensor = Box2DRegistry.Shape( e.sensorShapeId );
			var visitor = Box2DRegistry.Shape( e.visitorShapeId );
			if ( !Live( sensor ) || !Live( visitor ) ) continue;
			if ( !End( sensor, visitor ) ) continue;

			Guard( () =>
			{
				var left = new PhysicsContact.Target( sensor );
				var right = new PhysicsContact.Target( visitor );

				sensor.Body.DispatchTriggerEnd( new PhysicsIntersectionEnd( left, right ) );
				visitor.Body.DispatchTriggerEnd( new PhysicsIntersectionEnd( right, left ) );
			} );
		}

		DispatchPersists( world );
	}

	//Every touch still going that did not start this step.
	private void DispatchPersists( PhysicsWorld world )
	{
		if ( touching.Count == 0 ) return;

		walking.Clear();
		walking.AddRange( touching );

		foreach ( var (pair, touch) in walking )
		{
			if ( !Live( touch.A ) || !Live( touch.B ) )
			{
				//Box2D reports no end for a shape that no longer exists.
				touching.Remove( pair );
				continue;
			}

			if ( touch.Trigger || began.Contains( pair ) ) continue;

			Guard( () =>
			{
				var left = new PhysicsContact.Target( touch.A );
				var right = new PhysicsContact.Target( touch.B );

				world.OnIntersectionUpdate?.InvokeWithWarning( new PhysicsIntersection( left, right, default ) );
				touch.A.Body.DispatchIntersectionUpdate( new PhysicsIntersection( left, right, default ) );
				touch.B.Body.DispatchIntersectionUpdate( new PhysicsIntersection( right, left, default ) );
			} );
		}
	}

	private static void Guard( Action action )
	{
		try { action(); }
		catch ( Exception ex ) { Log.Error( ex ); }
	}

	private static T[] Copy<T>( T* events, int count ) where T : unmanaged
	{
		if ( events == null || count <= 0 ) return Array.Empty<T>();
		return new ReadOnlySpan<T>( events, count ).ToArray();
	}

	//The deepest point of a contact, where it is in the world, and how hard it closed.
	private static PhysicsContact ContactOf( b2ContactId id )
	{
		if ( !b2Contact_IsValid( id ) ) return default;

		var data = b2Contact_GetData( id );
		ref var m = ref data.manifold;
		if ( m.pointCount <= 0 ) return default;

		var bodyA = b2Shape_GetBody( data.shapeIdA );
		var center = b2Body_GetWorldCenter( bodyA );
		var z = Box2DRegistry.Body( bodyA )?.Position.z ?? 0.0f;

		var deepest = 0;
		for ( var i = 1; i < m.pointCount; i++ )
		{
			if ( m.points[i].separation < m.points[deepest].separation ) deepest = i;
		}

		var point = m.points[deepest];
		var world = new Vector3( center.x + point.anchorA.x, center.y + point.anchorA.y, z );

		return new PhysicsContact( world, new Vector3( m.normal.x, m.normal.y, 0 ), MathF.Max( 0.0f, -point.normalVelocity ) );
	}
}
