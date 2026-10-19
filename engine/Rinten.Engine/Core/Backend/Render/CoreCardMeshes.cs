namespace Rinten;

//Geometry the engine makes on the card - a vertex and an index buffer written by a compute pass - drawn the classic way with a material
//in the world it stands in: the camera's passes and the maps of what moves, so it casts as it is drawn. See TerrainRelief
internal static class CoreCardMeshes
{
	/// <summary>One such mesh: where it is drawn, with what, and whether its buffers hold anything yet.</summary>
	internal sealed class Shape
	{
		public SceneWorld World;
		public readonly CoreSceneRenderer.SceneMesh Mesh = new();
		public Material Wears;
		public RenderAttributes Attributes;
		public Func<bool> Ready;
		public Transform Transform = Transform.Zero;

		/// <summary>Points the mesh at its buffers, the first <paramref name="indices"/> indices drawn, inside <paramref name="bounds"/>.</summary>
		public void Hold( GpuBuffer vertices, GpuBuffer indices, int count, BBox bounds )
		{
			Mesh.Vertices = vertices?.Handle ?? 0;
			Mesh.Indices = indices?.Handle ?? 0;
			Mesh.Bounds = bounds;
			Mesh.BoundCentre = bounds.Center;
			Mesh.BoundRadius = bounds.Size.Length * 0.5f;

			Mesh.Meshes.Clear();
			if ( count > 0 ) Mesh.Meshes.Add( new CoreSceneRenderer.MeshDraw( 0, count, 0, 0, Color.White ) );
		}
	}

	private static readonly List<Shape> live = new();

	internal static void Add( Shape shape )
	{
		lock ( live ) if ( !live.Contains( shape ) ) live.Add( shape );
	}

	internal static void Remove( Shape shape )
	{
		lock ( live ) live.Remove( shape );
	}

	//The ones standing in a world that are ready to draw, into a list the caller keeps.
	internal static void In( SceneWorld world, List<Shape> into )
	{
		into.Clear();

		lock ( live )
		{
			foreach ( var shape in live )
			{
				if ( !ReferenceEquals( shape.World, world ) || shape.Wears is null || shape.Mesh.Vertices == 0 ) continue;
				if ( shape.Mesh.Meshes.Count == 0 || shape.Ready?.Invoke() == false ) continue;

				into.Add( shape );
			}
		}
	}
}
