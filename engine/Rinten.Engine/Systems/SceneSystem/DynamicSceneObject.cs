using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rinten;

/// <summary>Scene object whose vertices are supplied every frame it changes: debug lines, gizmos and the like</summary>
public class SceneDynamicObject : SceneObject
{
	/// <summary>What it is drawn with. Nothing is the engine's own line and triangle drawing, which is what nearly all of these want.</summary>
	public Material Material { get; set; }

	/// <summary>Whether these vertices are lines, triangles, or a strip.</summary>
	public Graphics.PrimitiveType Primitive { get; private set; } = Graphics.PrimitiveType.Triangles;

	//What to draw. Written between Init and the end of the frame, read by the renderer. Guarded by geometryLock:
	//game worker threads add to it while the frame is being gathered, and a List being resized under a reader is not.
	internal List<Vertex> Geometry { get; } = new();

	private readonly object geometryLock = new();

	//Where a draw copies the vertices from, under the lock. Reused, so reading a scene's dynamic geometry allocates nothing.
	private Vertex[] snapshot;

	//Drawn procedurally by its shader (the editor grid); skipped where that shader can't run
	internal bool ShaderDriven { get; set; }

	public SceneDynamicObject( SceneWorld sceneWorld )
	{
		Assert.IsValid( sceneWorld );

		//Infinite until something says otherwise, so an object whose bounds nobody set is drawn rather than culled.
		Bounds = new BBox( new Vector3( float.MinValue * 0.5f ), new Vector3( float.MaxValue * 0.5f ) );

		JoinWorld( sceneWorld );
	}

	public void Clear()
	{
		lock ( geometryLock ) Geometry.Clear();
	}

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public void AddVertex( in Vertex v )
	{
		lock ( geometryLock ) Geometry.Add( v );
	}

	public void AddVertex( in Span<Vertex> v )
	{
		lock ( geometryLock )
		{
			foreach ( var vertex in v ) Geometry.Add( vertex );
		}
	}

	internal unsafe void AddVertex( in Vertex* ptr, int count )
	{
		lock ( geometryLock )
		{
			for ( var i = 0; i < count; i++ ) Geometry.Add( ptr[i] );
		}
	}

	//A copy of the vertices as they are now, for the frame to draw. The renderer is the only writer of the copy,
	//so the span stays good for its own use after the lock is released.
	internal ReadOnlySpan<Vertex> GeometryForDraw()
	{
		lock ( geometryLock )
		{
			if ( Geometry.Count == 0 ) return default;

			if ( snapshot is null || snapshot.Length < Geometry.Count )
				snapshot = new Vertex[Geometry.Count];

			CollectionsMarshal.AsSpan( Geometry ).CopyTo( snapshot );

			return snapshot.AsSpan( 0, Geometry.Count );
		}
	}

	public IDisposable Write( Graphics.PrimitiveType type, int vertices, int indices )
	{
		ThrowIfUndefined( type );

		lock ( geometryLock )
		{
			Primitive = type;
			Geometry.Clear();

			if ( vertices > 0 ) Geometry.Capacity = Math.Max( Geometry.Capacity, vertices );
		}

		return null;
	}

	public void Init( Graphics.PrimitiveType type )
	{
		ThrowIfUndefined( type );

		lock ( geometryLock )
		{
			Primitive = type;
			Geometry.Clear();
		}
	}

	internal static void ThrowIfUndefined( Graphics.PrimitiveType type )
	{
		if ( !Enum.IsDefined( type ) )
			throw new ArgumentOutOfRangeException( nameof( type ), $"Unknown primitive type {(int)type}" );
	}
}
