using System.Threading;
using Rinten.Engine.Utility.RayTrace;

namespace Rinten;

//Objects gathered into small spatial chunks for picking traces: a ray asks the chunks' boxes, not every object's
public sealed partial class SceneWorld
{
	private const int RayChunkSize = 32;

	//A model's box can change with no word to its objects, after a recompile; all boxes are remade at this rate for it
	private const float RayRefitAllSeconds = 1.0f;

	private readonly Lock rayLock = new();

	//Boxed objects in space-filling order, a chunk being RayChunkSize of them; loose ones have no box to go by
	private SceneObject[] rayObjects = [];
	private SceneObject[] rayLoose = [];
	private BBox[] rayBoxes = [];
	private bool[] rayDirty = [];
	private bool[] rayAlways = [];
	private bool rayAnyDirty;
	private int rayBuiltAt = -1;
	private RealTimeSince rayRefitAll;

	//A chunk's object moved. Any thread; a mark lost to a rebuild is caught by that rebuild
	internal void RayMoved( int chunk )
	{
		var dirty = rayDirty;
		if ( (uint)chunk >= (uint)dirty.Length ) return;

		dirty[chunk] = true;
		Volatile.Write( ref rayAnyDirty, true );
	}

	//Adds what the segment may meet: every object of each chunk it crosses, and all the loose ones
	internal void RayCandidates( Vector3 start, Vector3 delta, List<SceneObject> into )
	{
		lock ( rayLock )
		{
			if ( rayBuiltAt != Membership ) RayBuild();
			else RayRefit();

			into.AddRange( rayLoose );

			for ( var chunk = 0; chunk < rayBoxes.Length; chunk++ )
			{
				if ( !rayAlways[chunk] && !MeshTraceRequest.TraceBounds( rayBoxes[chunk], start, delta, out _, out _ ) ) continue;

				var from = chunk * RayChunkSize;
				var count = Math.Min( RayChunkSize, rayObjects.Length - from );

				for ( var i = 0; i < count; i++ ) into.Add( rayObjects[from + i] );
			}
		}
	}

	private void RayBuild()
	{
		rayBuiltAt = Membership;
		rayRefitAll = 0;

		var objects = ObjectsNow();
		var boxed = new List<SceneObject>( objects.Length );
		var loose = new List<SceneObject>();
		var extent = default( BBox? );

		foreach ( var found in objects )
		{
			found.RayChunk = -1;

			var bounds = found.Bounds;

			if ( bounds.Size.AlmostEqual( 0.0f ) )
			{
				loose.Add( found );
				continue;
			}

			boxed.Add( found );
			extent = extent?.AddPoint( bounds.Center ) ?? BBox.FromPositionAndSize( bounds.Center );
		}

		rayLoose = loose.ToArray();
		rayObjects = boxed.ToArray();

		var keys = new uint[rayObjects.Length];

		for ( var i = 0; i < keys.Length; i++ ) keys[i] = RayKey( rayObjects[i].Bounds.Center, extent.Value );

		Array.Sort( keys, rayObjects );

		var chunks = ( rayObjects.Length + RayChunkSize - 1 ) / RayChunkSize;

		rayBoxes = new BBox[chunks];
		rayAlways = new bool[chunks];

		//Published last: RayMoved reads it without the lock
		rayDirty = new bool[chunks];

		for ( var chunk = 0; chunk < chunks; chunk++ )
		{
			RayFit( chunk );

			var from = chunk * RayChunkSize;
			var count = Math.Min( RayChunkSize, rayObjects.Length - from );

			for ( var i = 0; i < count; i++ ) rayObjects[from + i].RayChunk = chunk;
		}

		rayAnyDirty = false;
	}

	private void RayRefit()
	{
		var all = rayRefitAll >= RayRefitAllSeconds;
		if ( !all && !Volatile.Read( ref rayAnyDirty ) ) return;

		rayAnyDirty = false;

		if ( all ) rayRefitAll = 0;

		for ( var chunk = 0; chunk < rayDirty.Length; chunk++ )
		{
			if ( !all && !rayDirty[chunk] ) continue;

			rayDirty[chunk] = false;
			RayFit( chunk );
		}
	}

	private void RayFit( int chunk )
	{
		var from = chunk * RayChunkSize;
		var count = Math.Min( RayChunkSize, rayObjects.Length - from );
		var box = rayObjects[from].Bounds;
		var always = box.Size.AlmostEqual( 0.0f );

		for ( var i = 1; i < count; i++ )
		{
			var bounds = rayObjects[from + i].Bounds;

			//The trace asks the shape of what has no box, wherever it stands
			always |= bounds.Size.AlmostEqual( 0.0f );
			box = box.AddBBox( bounds );
		}

		rayBoxes[chunk] = box;
		rayAlways[chunk] = always;
	}

	//Where a point falls along a Z-order curve through the extent, ten bits an axis
	private static uint RayKey( Vector3 point, BBox extent )
	{
		var size = extent.Size;

		return Spread( ( point.x - extent.Mins.x ) / Math.Max( size.x, 1e-3f ) )
			| ( Spread( ( point.y - extent.Mins.y ) / Math.Max( size.y, 1e-3f ) ) << 1 )
			| ( Spread( ( point.z - extent.Mins.z ) / Math.Max( size.z, 1e-3f ) ) << 2 );

		static uint Spread( float share )
		{
			var bits = (uint)Math.Clamp( share * 1023.0f, 0.0f, 1023.0f );

			bits = ( bits | ( bits << 16 ) ) & 0x030000FF;
			bits = ( bits | ( bits << 8 ) ) & 0x0300F00F;
			bits = ( bits | ( bits << 4 ) ) & 0x030C30C3;
			bits = ( bits | ( bits << 2 ) ) & 0x09249249;

			return bits;
		}
	}
}
