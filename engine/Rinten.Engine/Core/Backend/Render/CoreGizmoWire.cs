using System.Runtime.InteropServices;

namespace Rinten;

//A collision mesh's edges kept on the card, made on first draw; each frame after is one indexed draw
internal static class CoreGizmoWire
{
	internal sealed class Geometry : IDisposable
	{
		public ulong Vertices;
		public ulong Indices;
		public int IndexCount;

		//Which device clear the handles belong to - see CoreResources.Cleared
		public int Cleared;

		public bool IsValid => Vertices != 0 && Indices != 0 && IndexCount >= 2;

		public void Dispose()
		{
			//After a clear the handles may name somebody else's buffers
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

	//Null without a device; an invalid one where the card would not take it, kept so it is not built again each frame
	internal static Geometry Get( PhysicsGroupDescription.BodyPart.MeshPart part )
	{
		if ( part.Wire is { } kept && kept.Cleared == CoreResources.Cleared ) return kept;

		if ( !CoreNative.Available || CoreRender.Ready() == 0 ) return null;

		var (vertices, indices) = LinesOf( part.GetVertices(), part.GetIndices() );

		var made = new Geometry { Cleared = CoreResources.Cleared };

		if ( indices.Length >= 2 )
		{
			made.Vertices = CoreRenderDevice.CreateBuffer( MemoryMarshal.AsBytes( vertices.AsSpan() ) );
			made.Indices = CoreRenderDevice.CreateBuffer( MemoryMarshal.AsBytes( indices.AsSpan() ) );
			made.IndexCount = indices.Length;
		}

		if ( !made.IsValid ) made.Dispose();

		return part.Wire = made;
	}

	//Six indices a triangle into its points; white, so the draw's colour tints it
	internal static (PackedVertex[] Vertices, int[] Indices) LinesOf( Vector3[] points, int[] indices )
	{
		if ( points is not { Length: > 0 } || indices is not { Length: >= 3 } )
			return (Array.Empty<PackedVertex>(), Array.Empty<int>());

		var vertices = new PackedVertex[points.Length];
		var white = Color32.White;

		for ( var i = 0; i < points.Length; i++ )
			vertices[i] = new PackedVertex { Position = points[i], Color = white };

		//A triangle with a corner out of range is left out
		var lines = new int[indices.Length / 3 * 6];

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
