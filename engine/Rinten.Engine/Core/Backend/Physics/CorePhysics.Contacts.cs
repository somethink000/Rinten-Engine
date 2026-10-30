using System.Runtime.InteropServices;
using Rinten.Native.Box3D;
using static Rinten.Native.Box3D.Box3D;

namespace Rinten;

//Where a body is touching things right now and how hard it rubs them - what scrape sounds are made from
internal static unsafe partial class CorePhysics
{
	/// <summary>One touching manifold as seen from the body asked about.</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct ScrapeContact
	{
		public ulong SelfShape;
		public ulong OtherShape;
		public Vector3 Point;
		public Vector3 Normal;

		/// <summary>The friction the solver applied over the step, as a force: newtons.</summary>
		public float FrictionForce;
	}

	//The body's touching manifolds with friction in them, up to the buffer's length
	public static int BodyScrapeContacts( ulong body, float stepTime, Span<ScrapeContact> into )
	{
		if ( into.IsEmpty || stepTime <= 0 || !ResolveBody( body, out var id ) ) return 0;

		var capacity = b3Body_GetContactCapacity( id );
		if ( capacity <= 0 ) return 0;

		var data = stackalloc b3ContactData[Math.Min( capacity, 32 )];
		var found = b3Body_GetContactData( id, data, Math.Min( capacity, 32 ) );
		var count = 0;

		for ( var c = 0; c < found && count < into.Length; c++ )
		{
			ref var contact = ref data[c];
			var selfIsA = Same( b3Shape_GetBody( contact.shapeIdA ), id );
			var bodyA = b3Shape_GetBody( contact.shapeIdA );
			var origin = b3Body_GetPosition( bodyA );

			for ( var m = 0; m < contact.manifoldCount && count < into.Length; m++ )
			{
				ref var manifold = ref contact.manifolds[m];
				if ( manifold.pointCount <= 0 ) continue;

				var friction = new Vector3( manifold.frictionImpulse.x, manifold.frictionImpulse.y, manifold.frictionImpulse.z ).Length / stepTime;
				if ( friction <= 0 ) continue;

				//Anchors are from body A's origin in world orientation; the contact is their middle
				var sum = Vector3.Zero;
				for ( var p = 0; p < manifold.pointCount; p++ )
				{
					var anchor = manifold.points[p].anchorA;
					sum += new Vector3( anchor.x, anchor.y, anchor.z );
				}

				ref var hit = ref into[count++];
				hit.SelfShape = Pack( selfIsA ? contact.shapeIdA : contact.shapeIdB );
				hit.OtherShape = Pack( selfIsA ? contact.shapeIdB : contact.shapeIdA );
				hit.Point = new Vector3( origin.x, origin.y, origin.z ) + sum / manifold.pointCount;
				hit.Normal = new Vector3( manifold.normal.x, manifold.normal.y, manifold.normal.z );
				hit.FrictionForce = friction;
			}
		}

		return count;
	}

	private static bool Same( b3BodyId a, b3BodyId b ) => a.index1 == b.index1 && a.world0 == b.world0 && a.generation == b.generation;
}
