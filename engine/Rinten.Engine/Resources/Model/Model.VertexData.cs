using NativeEngine;

namespace Rinten;

public partial class Model
{
	/// <summary>Every vertex of the model, copied out of the core - the positions an effect samples to be born on the surface. Kept once read.</summary>
	public unsafe Vertex[] GetVertices()
	{
		if ( _vertices is not null ) return _vertices;
		if ( core is not { IsValid: true } ) return null;

		var count = core.VertexCount;
		if ( count <= 0 ) return null;

		var vertices = new Vertex[count];

		fixed ( Vertex* v = vertices )
		{
			if ( core.CopyVertices( v, count ) <= 0 ) return null;
		}

		return _vertices = vertices;
	}

	/// <summary>Every index of the model, mesh after mesh - see <see cref="MeshDrawRange"/> for where each mesh's are.</summary>
	public unsafe uint[] GetIndices()
	{
		if ( _indices is not null ) return _indices;
		if ( core is not { IsValid: true } ) return null;

		var count = core.IndexCount;
		if ( count <= 0 ) return null;

		var indices = new uint[count];

		fixed ( uint* i = indices )
		{
			if ( core.CopyIndices( i, count ) <= 0 ) return null;
		}

		return _indices = indices;
	}

	/// <summary>Bind-pose triangles with absolute indices (default body group, nearest LOD); a fresh copy per call</summary>
	public bool GetTriangles( out Vector3[] positions, out int[] indices )
	{
		positions = null;
		indices = null;

		return core is { IsValid: true } && core.CopyTriangles( out positions, out indices );
	}

	Vertex[] _vertices;
	uint[] _indices;
	BBox[] _meshBounds;

	//Forget everything read back off the core, for when the model behind it changed - a copy of the old geometry is a model that is not there.
	private void ForgetGeometry()
	{
		_vertices = null;
		_indices = null;
		_meshBounds = null;

		traceMesh = null;
		traceMeshBuilt = false;

		boneBoxes = null;
		boneBoxesBuilt = false;
	}

	/// <summary>The box round one mesh of the model, in model space - the middle of which is what a piece of a broken model turns about.</summary>
	public BBox MeshBounds( int mesh )
	{
		var bounds = MeshBoundsAll();

		return bounds is not null && mesh >= 0 && mesh < bounds.Length ? bounds[mesh] : Bounds;
	}

	BBox[] MeshBoundsAll()
	{
		if ( _meshBounds is not null ) return _meshBounds;

		var vertices = GetVertices();
		var indices = GetIndices();
		var count = MeshCount;

		if ( vertices is null || indices is null || count <= 0 ) return null;

		var bounds = new BBox[count];

		for ( var mesh = 0; mesh < count; mesh++ )
		{
			MeshDrawRange( mesh, out var first, out var length, out var baseVertex );

			var box = default( BBox );
			var any = false;

			for ( var i = first; i < first + length && i < indices.Length; i++ )
			{
				var at = baseVertex + (int)indices[i];
				if ( (uint)at >= (uint)vertices.Length ) continue;

				var p = vertices[at].Position;

				box = any ? box.AddPoint( p ) : new BBox( p, p );
				any = true;
			}

			bounds[mesh] = any ? box : Bounds;
		}

		return _meshBounds = bounds;
	}

	/// <summary>How many indices one mesh of the model draws.</summary>
	public int GetIndexCount( int drawcall )
	{
		MeshDrawRange( drawcall, out _, out var count, out _ );
		return count;
	}

	/// <summary>Where one mesh's indices start in <see cref="GetIndices"/>.</summary>
	public int GetIndexStart( int drawcall )
	{
		MeshDrawRange( drawcall, out var start, out _, out _ );
		return start;
	}

	/// <summary>Where one mesh's vertices start in <see cref="GetVertices"/> - what its indices, which count from its own first vertex, are added to.</summary>
	public int GetBaseVertex( int drawcall )
	{
		MeshDrawRange( drawcall, out _, out _, out var baseVertex );
		return baseVertex;
	}

	//Vertices its meshes own, without those of LOD levels written after them
	internal int OwnVertexCount()
	{
		var count = 0;

		for ( var mesh = 0; mesh < MeshCount; mesh++ )
		{
			var range = core.MeshRange( mesh );
			count = Math.Max( count, range.FirstVertex + range.VertexCount );
		}

		return count;
	}

	//Where one mesh of this model sits in its index and vertex buffers.
	internal void MeshDrawRange( int mesh, out int startIndex, out int indexCount, out int baseVertex )
	{
		startIndex = 0;
		indexCount = 0;
		baseVertex = 0;

		if ( mesh < 0 || mesh >= MeshCount ) return;

		var range = core.MeshRange( mesh );

		startIndex = range.FirstIndex;
		indexCount = range.IndexCount;
		baseVertex = range.FirstVertex;
	}
}
