using System.Runtime.InteropServices;

namespace Rinten
{
	public sealed partial class ModelBuilder
	{
		private readonly List<Vector3> vertices = new();
		private readonly List<int> indices = new();
		private readonly List<byte> triangleMaterials = new();
		private readonly List<BoxDesc> boxes = new();
		private readonly List<SphereDesc> spheres = new();
		private readonly List<CapsuleDesc> capsules = new();
		private readonly List<HullDesc> hulls = new();
		private readonly List<MeshDesc> meshShapes = new();
		private readonly List<Vector3> traceVertices = [];
		private readonly List<int> traceIndices = [];

		private struct BoxDesc
		{
			public Transform transform;
			public Vector3 extents;
		}

		private struct SphereDesc
		{
			public Vector3 center;
			public float radius;
		}

		private struct CapsuleDesc
		{
			public Vector3 center0;
			public Vector3 center1;
			public float radius;
		}

		private struct HullDesc
		{
			public Transform transform;
			public int startVertex;
			public int numVertex;
		}

		private struct MeshDesc
		{
			public int startVertex;
			public int numVertex;
			public int startIndex;
			public int numIndex;
			public int startMaterial;
		}

		/// <summary>Adds a box collision shape.</summary>
		public ModelBuilder AddCollisionBox( Vector3 extents, Vector3? center = default, Rotation? rotation = default )
		{
			boxes.Add( new()
			{
				extents = extents,
				transform = new Transform( center ?? Vector3.Zero, rotation ?? Rotation.Identity )
			} );

			return this;
		}

		/// <summary>Adds a sphere collision shape.</summary>
		public ModelBuilder AddCollisionSphere( float radius, Vector3 center = default )
		{
			spheres.Add( new()
			{
				center = center,
				radius = radius
			} );

			return this;
		}

		/// <summary>Adds a capsule collision shape.</summary>
		public ModelBuilder AddCollisionCapsule( Vector3 center0, Vector3 center1, float radius )
		{
			capsules.Add( new()
			{
				center0 = center0,
				center1 = center1,
				radius = radius
			} );

			return this;
		}

		/// <summary>Adds a convex hull collision shape.</summary>
		public ModelBuilder AddCollisionHull( List<Vector3> vertices, Vector3? center = default, Rotation? rotation = default )
		{
			return AddCollisionHull( CollectionsMarshal.AsSpan( vertices ), center, rotation );
		}

		/// <summary>Adds a convex hull collision shape.</summary>
		public ModelBuilder AddCollisionHull( Span<Vector3> vertices, Vector3? center = default, Rotation? rotation = default )
		{
			if ( vertices.IsEmpty ) return this;

			var startVertex = this.vertices.Count;

			hulls.Add( new()
			{
				startVertex = startVertex,
				numVertex = vertices.Length,
				transform = new Transform( center ?? Vector3.Zero, rotation ?? Rotation.Identity )
			} );

			this.vertices.AddRange( vertices );

			return this;
		}

		/// <summary>Adds a concave triangle mesh collision shape. Mesh collision cannot be physically simulated.</summary>
		public ModelBuilder AddCollisionMesh( List<Vector3> vertices, List<int> indices )
		{
			return AddCollisionMesh( CollectionsMarshal.AsSpan( vertices ), CollectionsMarshal.AsSpan( indices ) );
		}

		/// <summary>Adds a concave triangle mesh collision shape. Mesh collision cannot be physically simulated.</summary>
		public ModelBuilder AddCollisionMesh( List<Vector3> vertices, List<int> indices, List<byte> materials )
		{
			return AddCollisionMesh( CollectionsMarshal.AsSpan( vertices ), CollectionsMarshal.AsSpan( indices ), CollectionsMarshal.AsSpan( materials ) );
		}

		/// <summary>Adds a concave triangle mesh collision shape. Mesh collision cannot be physically simulated.</summary>
		public ModelBuilder AddCollisionMesh( Span<Vector3> vertices, Span<int> indices )
		{
			return AddCollisionMesh( vertices, indices, null );
		}

		/// <summary>Adds a concave triangle mesh collision shape. Mesh collision cannot be physically simulated.</summary>
		public ModelBuilder AddCollisionMesh( Span<Vector3> vertices, Span<int> indices, Span<byte> materials )
		{
			if ( vertices.Length < 3 )
				return this;

			if ( indices.Length < 3 )
				return this;

			if ( (indices.Length % 3) != 0 )
				throw new ArgumentException( "Indices length must be a multiple of 3", nameof( indices ) );

			var triangleCount = indices.Length / 3;

			//Materials are optional, but if provided must match triangle count
			if ( materials.Length != 0 && materials.Length != triangleCount )
			{
				throw new ArgumentException(
					$"Materials length ({materials.Length}) must match triangle count ({triangleCount}) when provided",
					nameof( materials )
				);
			}

			//Validate indices are in range, creating collision mesh is expensive anyway so no harm in being safe.
			int numVertices = vertices.Length;
			foreach ( var i in indices )
			{
				if ( i < 0 || i >= numVertices )
					throw new ArgumentOutOfRangeException( nameof( indices ), $"Tried to access out of range vertex {i}, range is 0-{numVertices - 1}" );
			}

			var startVertex = this.vertices.Count;
			var startIndex = this.indices.Count;
			var startMaterial = this.triangleMaterials.Count;

			meshShapes.Add( new()
			{
				startVertex = startVertex,
				numVertex = vertices.Length,
				startIndex = startIndex,
				numIndex = indices.Length,
				startMaterial = materials.Length != 0 ? startMaterial : -1,
			} );

			this.vertices.AddRange( vertices );
			this.indices.AddRange( indices );

			if ( materials.Length != 0 )
				triangleMaterials.AddRange( materials );

			return this;
		}

		/// <summary>Adds geometry used by traces and ray queries. Multiple calls append additional trace geometry.</summary>
		public ModelBuilder AddTraceMesh( List<Vector3> vertices, List<int> indices )
		{
			return AddTraceMesh( CollectionsMarshal.AsSpan( vertices ), CollectionsMarshal.AsSpan( indices ) );
		}

		/// <summary>Adds geometry used by traces and ray queries. Multiple calls append additional trace geometry.</summary>
		public ModelBuilder AddTraceMesh( Span<Vector3> vertices, Span<int> indices )
		{
			if ( vertices.Length < 3 )
				return this;

			if ( indices.Length < 3 )
				return this;

			//Validate indices are in range
			int numVertices = vertices.Length;
			foreach ( var i in indices )
			{
				if ( i < 0 || i >= numVertices )
					throw new ArgumentOutOfRangeException( nameof( indices ), $"Tried to access out of range vertex {i}, range is 0-{numVertices - 1}" );
			}

			var vertexOffset = traceVertices.Count;
			traceVertices.AddRange( vertices );

			foreach ( var index in indices )
			{
				traceIndices.Add( vertexOffset + index );
			}

			return this;
		}

		private bool HasNamedCollision => boxes.Count + spheres.Count + capsules.Count + hulls.Count + meshShapes.Count > 0;

		//The shapes added by name, as one body of the core model
		private unsafe void AddNamedCollision( CoreAssets.ModelBuilder builder, string surface )
		{
			var identity = stackalloc float[7] { 0, 0, 0, 0, 0, 0, 1 };
			var body = builder.AddBody( "collision", "", identity, mass, 0.0f, 0.0f, 1.0f );
			if ( body < 0 ) return;

			var a = stackalloc float[3];
			var b = stackalloc float[3];

			foreach ( var sphere in spheres )
			{
				Put( a, sphere.center );
				builder.AddSphere( body, surface, a, sphere.radius );
			}

			foreach ( var capsule in capsules )
			{
				Put( a, capsule.center0 );
				Put( b, capsule.center1 );
				builder.AddCapsule( body, surface, a, b, capsule.radius );
			}

			var points = new List<float>();

			foreach ( var box in boxes )
			{
				points.Clear();

				for ( var corner = 0; corner < 8; corner++ )
				{
					var local = new Vector3(
						( corner & 1 ) == 0 ? -box.extents.x : box.extents.x,
						( corner & 2 ) == 0 ? -box.extents.y : box.extents.y,
						( corner & 4 ) == 0 ? -box.extents.z : box.extents.z );

					Add( points, box.transform.PointToWorld( local ) );
				}

				AddHull( builder, body, surface, points );
			}

			foreach ( var hull in hulls )
			{
				points.Clear();

				for ( var i = 0; i < hull.numVertex; i++ )
					Add( points, hull.transform.PointToWorld( vertices[hull.startVertex + i] ) );

				AddHull( builder, body, surface, points );
			}

			foreach ( var mesh in meshShapes )
			{
				points.Clear();

				for ( var i = 0; i < mesh.numVertex; i++ )
					Add( points, vertices[mesh.startVertex + i] );

				var meshIndices = new uint[mesh.numIndex];
				for ( var i = 0; i < mesh.numIndex; i++ )
					meshIndices[i] = (uint)indices[mesh.startIndex + i];

				var span = CollectionsMarshal.AsSpan( points );

				fixed ( float* p = span )
				fixed ( uint* ix = meshIndices )
					builder.AddCollisionMesh( body, surface, p, mesh.numVertex, ix, meshIndices.Length );
			}

			static void Put( float* to, Vector3 v ) { to[0] = v.x; to[1] = v.y; to[2] = v.z; }
			static void Add( List<float> to, Vector3 v ) { to.Add( v.x ); to.Add( v.y ); to.Add( v.z ); }

			static void AddHull( CoreAssets.ModelBuilder builder, int body, string surface, List<float> points )
			{
				var span = CollectionsMarshal.AsSpan( points );

				fixed ( float* p = span )
					builder.AddHull( body, surface, p, points.Count / 3 );
			}
		}

		private readonly List<int> _surfaces = [];

		/// <summary>Adds a surface that can be referenced by collision mesh material indices.</summary>
		public ModelBuilder AddSurface( Surface surface )
		{
			surface ??= Surface.FindByName( "default" );
			_surfaces.Add( surface?.Index ?? 0 );
			return this;
		}

	}
}
