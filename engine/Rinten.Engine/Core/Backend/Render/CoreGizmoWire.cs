using System.Runtime.InteropServices;

namespace Rinten;

//A collision mesh as a line mesh kept on the card: the vertices and the line indices are made the first time a
//selected collider is drawn, and every frame after that is one indexed draw. Walking the triangles and building
//six gizmo vertices for each one - what Gizmo.Draw.LineTriangles does - is what a high-poly collider used to cost
//the editor. The geometry is the mesh's own, so it is kept with the mesh part and goes when that does.
internal static class CoreGizmoWire
{
	//A built wire mesh. The buffers are the card's; nothing here owns the CPU arrays a MeshPart already has.
	internal sealed class Geometry : IDisposable
	{
		public ulong Vertices;
		public ulong Indices;
		public int IndexCount;

		//Which clearing of the device the handles belong to - see CoreResources.Cleared.
		public int Cleared;

		public bool IsValid => Vertices != 0 && Indices != 0 && IndexCount >= 2;

		public void Dispose()
		{
			//After a clear the handles name buffers that no longer exist, and may belong to somebody else's.
			if ( Cleared == CoreResources.Cleared )
			{
				CoreRenderDevice.DestroyBuffer( Vertices );
				CoreRenderDevice.DestroyBuffer( Indices );
			}

			Vertices = 0;
			Indices = 0;
			IndexCount = 0;
		}
	}

	//The line copy of a mesh part, built once. Null where the device would not make it.
	internal static Geometry Get( PhysicsGroupDescription.BodyPart.MeshPart part )
	{
		if ( part.Wire is { } kept )
		{
			//A device clear took every buffer with it; this one has to be made again.
			if ( kept.Cleared == CoreResources.Cleared && kept.IsValid ) return kept;

			part.Wire = null;
			part.WireTried = false;
		}

		//No device yet: the caller draws the lines the old way and asks again next frame.
		if ( !CoreNative.Available || CoreRender.Ready() == 0 ) return null;

		//Asked once already and the card would not have it: do not build the CPU side again every frame.
		if ( part.WireTried ) return null;

		part.WireTried = true;

		var (vertices, indices) = LinesOf( part.GetVertices(), part.GetIndices() );
		if ( indices.Length < 2 || vertices.Length == 0 ) return null;

		var made = new Geometry
		{
			Vertices = CoreRenderDevice.CreateBuffer( MemoryMarshal.AsBytes( vertices.AsSpan() ) ),
			Indices = CoreRenderDevice.CreateBuffer( MemoryMarshal.AsBytes( indices.AsSpan() ) ),
			IndexCount = indices.Length,
			Cleared = CoreResources.Cleared,
		};

		//An invalid one is handed back rather than null, so the caller skips the mesh instead of walking its
		//triangles on the CPU every frame to draw what the card would not keep.
		if ( made.IsValid ) part.Wire = made;
		else made.Dispose();

		return made;
	}

	//Every triangle as the three lines round it: six indices into its points. The colour is white, so the draw's
	//own color can tint it without the buffer being rebuilt when that changes.
	internal static (PackedVertex[] Vertices, int[] Indices) LinesOf( Vector3[] points, int[] indices )
	{
		if ( points is not { Length: > 0 } || indices is not { Length: >= 3 } )
			return (Array.Empty<PackedVertex>(), Array.Empty<int>());

		var vertices = new PackedVertex[points.Length];
		var white = Color32.White;

		for ( var i = 0; i < points.Length; i++ )
			vertices[i] = new PackedVertex { Position = points[i], Color = white };

		//Six a triangle: a, b, b, c, c, a. A triangle with a corner out of range is left out rather than drawn
		//as a line to nowhere, and the count that survives is what is uploaded.
		var lines = new int[(long)( indices.Length / 3 ) * 6 > int.MaxValue ? 0 : ( indices.Length / 3 ) * 6];
		if ( lines.Length == 0 ) return (vertices, Array.Empty<int>());

		var at = 0;

		for ( var i = 0; i + 2 < indices.Length; i += 3 )
		{
			var a = indices[i];
			var b = indices[i + 1];
			var c = indices[i + 2];

			if ( (uint)a >= (uint)points.Length || (uint)b >= (uint)points.Length || (uint)c >= (uint)points.Length )
				continue;

			lines[at++] = a;
			lines[at++] = b;
			lines[at++] = b;
			lines[at++] = c;
			lines[at++] = c;
			lines[at++] = a;
		}

		if ( at == 0 ) return (vertices, Array.Empty<int>());
		if ( at < lines.Length ) Array.Resize( ref lines, at );

		return (vertices, lines);
	}
}
