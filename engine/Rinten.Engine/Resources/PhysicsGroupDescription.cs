namespace Rinten;

//Plain managed data now, filled by the model importer
/// <summary>Describes the physics shapes of a model: the bodies, the shapes on each one, and the joints between them.</summary>
public sealed class PhysicsGroupDescription : Resource
{
	public override bool IsValid => true;

	public string Name { get; internal set; }

	//How many collision attribute sets the description carries.
	internal int CollisionAttributeCount => _tags.Count;

	internal PhysicsGroupDescription( string name )
	{
		Name = name;

		if ( !string.IsNullOrEmpty( name ) )
			RegisterWeakResourceId( name );
	}

	internal override void Destroy()
	{
		Dispose();

		base.Destroy();
	}

	internal override void OnReloaded()
	{
		foreach ( var scene in Scene.Loaded )
		{
			using var scope = scene.Push();

			foreach ( var c in scene.GetAllComponents<IHasPhysicsDescription>() )
			{
				if ( c.Physics != this ) continue;

				c.OnPhysicsReloaded();
			}
		}
	}

	//Nothing writes one to disk yet - collision comes off the model as it is imported - so this finds nothing, the same as a file that is not there.
	/// <summary>Load a <see cref="PhysicsGroupDescription"/> from a physics resource path.</summary>
	public static PhysicsGroupDescription Load( string path )
	{
		return null;
	}

	internal void Dispose()
	{
		foreach ( var part in _parts ) part.Dispose();

		_parts.Clear();
		_joints.Clear();
		_surfaces.Clear();
		_tags.Clear();
	}

	readonly List<BodyPart> _parts = new();
	readonly List<Joint> _joints = new();
	readonly List<string> _surfaces = new();
	readonly List<List<StringToken>> _tags = new();

	public IReadOnlyList<BodyPart> Parts => _parts;
	public IReadOnlyList<Joint> Joints => _joints;

	/// <summary>Enumerate every <see cref="Surface"/> in this physics description.</summary>
	public IEnumerable<Surface> Surfaces => Enumerable.Range( 0, _surfaces.Count )
		.Select( x => GetSurface( (uint)x ) );

	//Adds a surface, returning the index shapes refer to it by.
	internal int AddSurface( string name )
	{
		_surfaces.Add( string.IsNullOrEmpty( name ) ? "default" : name );

		return _surfaces.Count - 1;
	}

	//Adds a set of collision tags, returning its index.
	internal int AddTags( IEnumerable<StringToken> tags )
	{
		_tags.Add( tags?.ToList() ?? new List<StringToken>() );

		return _tags.Count - 1;
	}

	internal void AddPart( BodyPart part ) => _parts.Add( part );
	internal void AddJoint( Joint joint ) => _joints.Add( joint );

	internal Surface GetSurface( uint index )
	{
		if ( index >= _surfaces.Count )
			return null;

		var surfaceName = _surfaces[(int)index];
		return Surface.FindByName( surfaceName );
	}

	internal IReadOnlyList<StringToken> GetTags( int index )
	{
		if ( index >= _tags.Count )
			return null;

		return _tags[index];
	}

	/// <summary>How many bones the parts are attached to.</summary>
	public int BoneCount { get; internal set; }

	public enum JointType
	{
		Ball,
		Hinge,
		Slider,
		Fixed,
	}

	/// <summary>One joint between two bodies, and everything it is allowed to do.</summary>
	public sealed class Joint
	{
		public JointType Type { get; internal set; }
		public bool Fixed { get; internal set; }

		/// <summary>Disabled joints are kept, so adjacent shapes still don't collide with each other</summary>
		public bool Enabled { get; internal set; } = true;

		public int Body1 { get; internal set; }
		public int Body2 { get; internal set; }

		public Transform Frame1 { get; internal set; } = Transform.Zero;
		public Transform Frame2 { get; internal set; } = Transform.Zero;

		public bool EnableCollision { get; internal set; }

		public bool EnableLinearLimit { get; internal set; }
		public bool EnableLinearMotor { get; internal set; }
		public Vector3 LinearTargetVelocity { get; internal set; }
		public float MaxForce { get; internal set; }
		public float LinearFrequency { get; internal set; }
		public float LinearDampingRatio { get; internal set; }
		public float LinearStrength { get; internal set; }

		public bool EnableSwingLimit { get; internal set; }
		public bool EnableTwistLimit { get; internal set; }
		public bool EnableAngularMotor { get; internal set; }
		public Vector3 AngularTargetVelocity { get; internal set; }
		public float MaxTorque { get; internal set; }
		public float AngularFrequency { get; internal set; }
		public float AngularDampingRatio { get; internal set; }
		public float AngularStrength { get; internal set; }

		public float LinearMin { get; internal set; }
		public float LinearMax { get; internal set; }

		/// <summary>Swing limits, in degrees.</summary>
		public float SwingMin { get; internal set; }

		public float SwingMax { get; internal set; }

		/// <summary>How much the joint resists being moved at all.</summary>
		public float Friction { get; internal set; }

		/// <summary>Twist limits, in degrees.</summary>
		public float TwistMin { get; internal set; }

		public float TwistMax { get; internal set; }
	}

	/// <summary>One rigid body of the description: a bone, what it weighs, and the shapes that make up its collision.</summary>
	public sealed class BodyPart
	{
		public Transform Transform { get; init; } = Transform.Zero;

		public string BoneName { get; init; }

		private List<Part> All { get; } = new();

		public float Mass { get; internal set; } = 1.0f;
		public float LinearDamping { get; internal set; }
		public float AngularDamping { get; internal set; }
		public bool OverrideMassCenter { get; internal set; }
		public Vector3 MassCenterOverride { get; internal set; }
		public float GravityScale { get; internal set; } = 1.0f;

		internal void Add( Part part ) => All.Add( part );

		internal void Dispose()
		{
			foreach ( var part in All ) part.Dispose();

			All.Clear();
		}

		public IReadOnlyList<SpherePart> Spheres => All.OfType<SpherePart>().ToList();
		public IReadOnlyList<CapsulePart> Capsules => All.OfType<CapsulePart>().ToList();
		public IReadOnlyList<HullPart> Hulls => All.OfType<HullPart>().ToList();
		public IReadOnlyList<MeshPart> Meshes => All.OfType<MeshPart>().ToList();
		public IReadOnlyList<Part> Parts => All;

		public abstract class Part
		{
			public Surface Surface { get; protected internal set; }

			internal virtual void Dispose()
			{

			}
		}

		public class SpherePart : Part
		{
			public Sphere Sphere { get; init; }

			internal SpherePart( Sphere sphere, Surface surface )
			{
				Sphere = sphere;
				Surface = surface;
			}

			/// <summary>Line outline vertices and edge index pairs for debug rendering: three rings around the sphere, one per axis.</summary>
			public void GetOutline( out Vector3[] vertices, out int[] indices )
			{
				Outline.Rings( Sphere.Center, Sphere.Radius, out vertices, out indices );
			}
		}

		public class CapsulePart : Part
		{
			public Capsule Capsule { get; init; }

			internal CapsulePart( Capsule capsule, Surface surface )
			{
				Capsule = capsule;
				Surface = surface;
			}

			public void GetOutline( out Vector3[] vertices, out int[] indices )
			{
				//A ring at each end, and the lines between them: enough to read a capsule at a glance, which is all an outline is for.
				Outline.Rings( Capsule.CenterA, Capsule.Radius, out var a, out var ai );
				Outline.Rings( Capsule.CenterB, Capsule.Radius, out var b, out var bi );

				vertices = a.Concat( b ).ToArray();

				var indexList = new List<int>( ai );
				indexList.AddRange( bi.Select( x => x + a.Length ) );

				for ( var i = 0; i < a.Length; i += a.Length / 4 )
				{
					indexList.Add( i );
					indexList.Add( i + a.Length );
				}

				indices = indexList.ToArray();
			}
		}

		public class HullPart : Part
		{
			//The points the hull is the convex volume of.
			internal Vector3[] Points { get; init; } = Array.Empty<Vector3>();

			//Pairs of point indices, one per edge of the hull.
			internal int[] Edges { get; init; } = Array.Empty<int>();

			//Edges worked out from Points, for a hull that carried none.
			private int[] outline;

			public BBox Bounds { get; init; }

			internal HullPart( Vector3[] points, int[] edges, Surface surface )
			{
				Points = points ?? Array.Empty<Vector3>();
				Edges = edges ?? Array.Empty<int>();
				Surface = surface;
				Bounds = Points.Length > 0 ? BBox.FromPoints( Points ) : default;
			}

			/// <summary>For debug rendering</summary>
			public IEnumerable<Line> GetLines()
			{
				//A compiled model stores a hull as points alone, so its edges are worked out here.
				var edges = Edges is { Length: > 0 } ? Edges : outline ??= Outline.HullEdges( Points );

				for ( var i = 0; i + 1 < edges.Length; i += 2 )
				{
					var a = edges[i];
					var b = edges[i + 1];

					if ( a < 0 || b < 0 || a >= Points.Length || b >= Points.Length ) continue;

					yield return new Line( Points[a], Points[b] );
				}
			}

			public IEnumerable<Vector3> GetPoints() => Points;
		}

		public class MeshPart : Part
		{
			private readonly Vector3[] vertices = Array.Empty<Vector3>();
			private readonly int[] indices = Array.Empty<int>();

			public BBox Bounds { get; init; }

			public Surface[] Surfaces { get; protected set; }

			internal MeshPart( Vector3[] vertices, int[] indices, Surface surface, Surface[] surfaces )
			{
				this.vertices = vertices ?? Array.Empty<Vector3>();
				this.indices = indices ?? Array.Empty<int>();

				Surface = surface;
				Surfaces = surfaces;
				Bounds = this.vertices.Length > 0 ? BBox.FromPoints( this.vertices ) : default;
			}

			//The line copy kept on the card for a selected collider's gizmo, made on first use - see CoreGizmoWire.
			internal CoreGizmoWire.Geometry Wire;

			//Whether the card was asked for one already, so a mesh it would not take is not built again every frame.
			internal bool WireTried;

			internal override void Dispose()
			{
				Wire?.Dispose();
				Wire = null;
				WireTried = false;
			}

			/// <summary>Get all vertices in this mesh.</summary>
			public Vector3[] GetVertices() => vertices;

			/// <summary>Get all triangle indices in this mesh. Every 3 consecutive values form a triangle.</summary>
			public int[] GetIndices() => indices;

			/// <summary>For debug rendering</summary>
			public IEnumerable<Triangle> GetTriangles()
			{
				for ( var i = 0; i + 2 < indices.Length; i += 3 )
				{
					yield return new Triangle( vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]] );
				}
			}
		}

		//Outlines for the shapes that have no vertices of their own.
		private static class Outline
		{
			private const int Segments = 16;

			//The most corners a hull outline is worked out from; the cost is cubic in this.
			private const int MostCorners = 96;

			/// <summary>The edges of the convex hull of a set of points, as pairs of indices into it.</summary>
			public static int[] HullEdges( Vector3[] points )
			{
				if ( points is not { Length: >= 4 } || points.Length > MostCorners ) return Array.Empty<int>();

				var count = points.Length;

				//Tolerances go by the size of the shape, so the same hull scaled down is the same hull.
				var reach = BBox.FromPoints( points ).Size.Length;
				if ( reach <= 0.0f ) return Array.Empty<int>();

				var offPlane = reach * 1e-4f;
				var inLine = reach * reach * 1e-8f;

				//Each edge a face turned out to have, and which way the first face along it points.
				var found = new Dictionary<long, (Vector3 Facing, bool Turns)>();

				for ( var a = 0; a < count; a++ )
				{
					for ( var b = a + 1; b < count; b++ )
					{
						for ( var c = b + 1; c < count; c++ )
						{
							var normal = Vector3.Cross( points[b] - points[a], points[c] - points[a] );

							//Three points in a line have no plane to take a side of.
							if ( normal.LengthSquared <= inLine ) continue;

							normal = normal.Normal;
							var plane = Vector3.Dot( normal, points[a] );

							var above = false;
							var below = false;

							for ( var p = 0; p < count && !( above && below ); p++ )
							{
								var side = Vector3.Dot( normal, points[p] ) - plane;

								if ( side > offPlane ) above = true;
								else if ( side < -offPlane ) below = true;
							}

							//Points either side means the plane cuts the shape, so these three are inside it.
							if ( above && below ) continue;

							//Turned outward, so two faces are told apart by where they look, not their winding.
							var facing = above ? -normal : normal;

							//A set with no volume has no outward at all; settle it so it yields no edges.
							if ( !above && !below ) facing = Settle( normal );

							Note( found, a, b, facing );
							Note( found, b, c, facing );
							Note( found, a, c, facing );
						}
					}
				}

				var edges = new List<int>( found.Count * 2 );

				foreach ( var (key, edge) in found )
				{
					//A flat face comes out as several triangles; the seams between them are not corners.
					if ( !edge.Turns ) continue;

					edges.Add( (int)( key >> 32 ) );
					edges.Add( (int)( key & 0xFFFFFFFF ) );
				}

				return edges.ToArray();
			}

			//Remembers an edge, and whether the shape turns along it.
			private static void Note( Dictionary<long, (Vector3 Facing, bool Turns)> found, int a, int b, Vector3 facing )
			{
				var key = ( (long)a << 32 ) | (uint)b;

				if ( !found.TryGetValue( key, out var edge ) )
				{
					found[key] = (facing, false);
					return;
				}

				if ( edge.Turns ) return;

				//About a degree: under any real corner, over what rounding does to a normal.
				if ( Vector3.Dot( edge.Facing, facing ) < 0.9998f )
					found[key] = (edge.Facing, true);
			}

			//A normal and its opposite, told apart the same way every time.
			private static Vector3 Settle( Vector3 normal )
			{
				var x = MathF.Abs( normal.x );
				var y = MathF.Abs( normal.y );
				var z = MathF.Abs( normal.z );

				var major = x >= y && x >= z ? normal.x : y >= z ? normal.y : normal.z;

				return major < 0.0f ? -normal : normal;
			}

			/// <summary>Three rings around a point, one per axis.</summary>
			public static void Rings( Vector3 centre, float radius, out Vector3[] vertices, out int[] indices )
			{
				var points = new List<Vector3>( Segments * 3 );
				var lines = new List<int>( Segments * 6 );

				for ( var axis = 0; axis < 3; axis++ )
				{
					var first = points.Count;

					for ( var i = 0; i < Segments; i++ )
					{
						var angle = i / (float)Segments * MathF.PI * 2.0f;
						var x = MathF.Cos( angle ) * radius;
						var y = MathF.Sin( angle ) * radius;

						points.Add( centre + axis switch
						{
							0 => new Vector3( 0, x, y ),
							1 => new Vector3( x, 0, y ),
							_ => new Vector3( x, y, 0 )
						} );

						lines.Add( first + i );
						lines.Add( first + ( ( i + 1 ) % Segments ) );
					}
				}

				vertices = points.ToArray();
				indices = lines.ToArray();
			}
		}
	}
}

internal interface IHasPhysicsDescription
{
	//The PhysicsGroupDescription associated with this object.
	PhysicsGroupDescription Physics { get; }

	//Called by the engine when the associated PhysicsGroupDescription is reloaded. Implementing classes should rebuild any physics created from it.
	void OnPhysicsReloaded();
}
