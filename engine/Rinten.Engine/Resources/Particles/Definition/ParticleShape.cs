using System.Text.Json.Serialization;
namespace Rinten;

/// <summary>Which of the effect's shapes a property names.</summary>
[AttributeUsage( AttributeTargets.Property )]
public sealed class ParticleShapeNameAttribute : Attribute { }

/// <summary>Which of the effect's slots a property names.</summary>
[AttributeUsage( AttributeTargets.Property )]
public sealed class ParticleSlotNameAttribute : Attribute { }

/// <summary>How a shape is walked when a pose is asked of it.</summary>
[Expose]
public enum ParticleShapeSample
{
	/// <summary>Anywhere on it, each time.</summary>
	Random,

	/// <summary>The k-th of N places for the k-th particle - a formation.</summary>
	ByIndex,

	/// <summary>Where the shape is at a moment nought to one - a path walked over time.</summary>
	ByTime,
}

/// <summary>A pose a shape gave: where, which way, and which of its places it was.</summary>
public readonly record struct ShapePose( Vector3 Position, Vector3 Normal, float Along );

/// <summary>Geometry the effect owns and modules sample: a point, a sphere, a box, a circle, a cone, a line, a spline, a grid or a helix. Sampled at random, by index, or by time - the same sphere is a spawn volume, a formation and a path, depending on who asks and how.</summary>
[Expose]
public sealed class ParticleShape
{
	[Expose]
	public enum ShapeKind
	{
		Point,
		Sphere,
		Box,
		Circle,
		Cone,
		Line,
		Spline,
		Grid,
		Helix,

		/// <summary>A model: its pieces, its surface, its vertices, its attachments or its bones.</summary>
		Model,
	}

	/// <summary>What of a model a pose is taken from.</summary>
	[Expose]
	public enum ModelSample
	{
		/// <summary>The middle of each mesh of the model - a piece of something broken. The particle is told which piece.</summary>
		Parts,

		/// <summary>Anywhere on its triangles, facing out.</summary>
		Surface,

		/// <summary>Its vertices, one each.</summary>
		Vertices,

		/// <summary>Anywhere in its box.</summary>
		Bounds,

		/// <summary>Its attachment points.</summary>
		Attachments,

		/// <summary>Its bones, at rest.</summary>
		Bones,
	}

	[Property]
	public string Name { get; set; } = "Shape";

	[Hide]
	public string Identifier { get; set; } = Guid.NewGuid().ToString();

	[Property]
	public ShapeKind Kind { get; set; } = ShapeKind.Sphere;

	/// <summary>The anchor this shape hangs from, where it hangs from one instead of the emitter: a ring around a hand, a cone out of a muzzle.</summary>
	[Property, ParticleAnchorName]
	public string Anchor { get; set; }

	/// <summary>Where the shape sits, from the emitter.</summary>
	[Property]
	public Vector3 Offset { get; set; } = Vector3.Zero;

	/// <summary>Which way it faces. A circle lies flat until it is turned.</summary>
	[Property]
	public Angles Rotation { get; set; } = Angles.Zero;

	[Property, ShowIf( nameof( HasRadius ), true ), Range( 0.0f, 20.0f )]
	public float Radius { get; set; } = 0.5f;

	[Property, ShowIf( nameof( Kind ), ShapeKind.Model )]
	public Model Model { get; set; }

	[Property, ShowIf( nameof( Kind ), ShapeKind.Model ), Title( "Sample" )]
	public ModelSample ModelSampling { get; set; } = ModelSample.Parts;

	/// <summary>How big the model is taken to be, over its own size.</summary>
	[Property, ShowIf( nameof( Kind ), ShapeKind.Model ), Range( 0.01f, 10.0f )]
	public float ModelScale { get; set; } = 1.0f;

	[Property, ShowIf( nameof( Kind ), ShapeKind.Box )]
	public Vector3 BoxSize { get; set; } = Vector3.One;

	[Property, ShowIf( nameof( Kind ), ShapeKind.Cone ), Range( 0.0f, 180.0f )]
	public float ConeAngle { get; set; } = 45.0f;

	/// <summary>Only the surface of it, or anywhere inside as well.</summary>
	[Property, ShowIf( nameof( HasShell ), true )]
	public bool OnShell { get; set; }

	[Property, ShowIf( nameof( Kind ), ShapeKind.Line )]
	public Vector3 LineStart { get; set; } = Vector3.Zero;

	[Property, ShowIf( nameof( Kind ), ShapeKind.Line )]
	public Vector3 LineEnd { get; set; } = Vector3.Up;

	/// <summary>A line from one anchor to another - a blade from hilt to tip. Either empty is the point written above.</summary>
	[Property, ShowIf( nameof( Kind ), ShapeKind.Line ), ParticleAnchorName]
	public string LineStartAnchor { get; set; }

	[Property, ShowIf( nameof( Kind ), ShapeKind.Line ), ParticleAnchorName]
	public string LineEndAnchor { get; set; }

	/// <summary>The points a spline runs through, in order.</summary>
	[Property, ShowIf( nameof( Kind ), ShapeKind.Spline )]
	public List<Vector3> Points { get; set; } = new();

	/// <summary>Whether a spline joins its end back to its start.</summary>
	[Property, ShowIf( nameof( Kind ), ShapeKind.Spline )]
	public bool Closed { get; set; }

	/// <summary>How many cells a grid has along each axis.</summary>
	[Property, ShowIf( nameof( Kind ), ShapeKind.Grid )]
	public Vector3 GridCount { get; set; } = new( 5, 1, 5 );

	/// <summary>How far apart a grid's cells are.</summary>
	[Property, ShowIf( nameof( Kind ), ShapeKind.Grid )]
	public Vector3 GridSpacing { get; set; } = new( 0.3f, 0.3f, 0.3f );

	[Property, ShowIf( nameof( Kind ), ShapeKind.Helix ), Range( 0.0f, 20.0f )]
	public float Height { get; set; } = 2.0f;

	[Property, ShowIf( nameof( Kind ), ShapeKind.Helix ), Range( 0.1f, 20.0f )]
	public float Turns { get; set; } = 3.0f;

	/// <summary>How many places the shape has when walked by index. A grid is its cells; anything else is this. Nought means "as many as the emitter bursts".</summary>
	[Property, Range( 0, 1000 )]
	public int Count { get; set; }

	//The random a sample is drawing from - per thread, since particles step in parallel.
	[ThreadStatic]
	static Random _random;

	static Random Rng => _random ?? Random.Shared;

	//The triangles of the model this shape samples, which it is asked for once a particle.
	Model _trianglesFor;
	Vector3[] _trianglePoints;
	int[] _triangleIndices;

	//The model's triangles, kept between particles. Not its vertices and indices: those indices are relative to each mesh of
	//the model, and read against the whole vertex list they land on the wrong corners of anything with more than one mesh.
	bool Triangles( Model model, out Vector3[] points, out int[] indices )
	{
		if ( _trianglesFor != model )
		{
			_trianglesFor = model;

			if ( !model.GetTriangles( out _trianglePoints, out _triangleIndices ) )
			{
				_trianglePoints = null;
				_triangleIndices = null;
			}
		}

		points = _trianglePoints;
		indices = _triangleIndices;

		return indices is { Length: >= 3 };
	}

	[Hide, JsonIgnore] public bool HasRadius => Kind is ShapeKind.Sphere or ShapeKind.Circle or ShapeKind.Cone or ShapeKind.Helix;
	[Hide, JsonIgnore] public bool HasShell => Kind is ShapeKind.Sphere or ShapeKind.Box or ShapeKind.Circle or ShapeKind.Cone;

	/// <summary>How many places this has for By Index, given what the emitter would do otherwise.</summary>
	public int Places( int fallback )
	{
		if ( Kind == ShapeKind.Grid )
			return Math.Max( 1, (int)GridCount.x ) * Math.Max( 1, (int)GridCount.y ) * Math.Max( 1, (int)GridCount.z );

		if ( Kind == ShapeKind.Model && Model is { } model )
		{
			var own = ModelSampling switch
			{
				ModelSample.Parts => model.MeshCount,
				ModelSample.Vertices => model.GetVertices()?.Length ?? 0,
				ModelSample.Attachments => model.AttachmentCount,
				ModelSample.Bones => model.Bones.AllBones.Count,
				_ => 0,
			};

			if ( own > 0 ) return Count > 0 ? Math.Min( Count, own ) : own;
		}

		return Count > 0 ? Count : Math.Max( 1, fallback );
	}

	/// <summary>The frame the shape is measured in, in the world: the anchor it hangs from, else the player, with only what <paramref name="follows"/> says of the player's rotation and scale.</summary>
	public Transform Frame( ParticlePlayer player, InitializePositionModule.ShapeTransform follows = InitializePositionModule.ShapeTransform.Rotation | InitializePositionModule.ShapeTransform.Scale )
	{
		if ( !player.IsValid() ) return Transform.Zero;

		if ( !string.IsNullOrEmpty( Anchor ) )
			return player.Anchor( Anchor );

		var tx = player.WorldTransform;

		if ( ( follows & InitializePositionModule.ShapeTransform.Rotation ) == 0 ) tx.Rotation = global::Rotation.Identity;
		if ( ( follows & InitializePositionModule.ShapeTransform.Scale ) == 0 ) tx.Scale = Vector3.One;

		return tx;
	}

	/// <summary>A pose from the shape in the world, for this player: the frame applied, and a line between anchors read off where the anchors are now.</summary>
	public ShapePose SampleWorld( ParticlePlayer player, ParticleShapeSample sample, int index, int places, float time,
		InitializePositionModule.ShapeTransform follows = InitializePositionModule.ShapeTransform.Rotation | InitializePositionModule.ShapeTransform.Scale,
		Random random = null )
		=> SampleIn( Frame( player, follows ), player, sample, index, places, time, random );

	//The frame is the same for every particle of an effect and working it out reaches an anchor by name through two case-insensitive maps - so a module that samples per particle works it out once a frame instead and hands it in. See IParticleModuleFrame.
	/// <summary>The same, with the frame already in hand.</summary>
	public ShapePose SampleIn( in Transform frame, ParticlePlayer player, ParticleShapeSample sample, int index, int places, float time,
		Random random = null )
	{
		_random = random ?? Random.Shared;

		if ( Kind == ShapeKind.Line && player.IsValid() && ( !string.IsNullOrEmpty( LineStartAnchor ) || !string.IsNullOrEmpty( LineEndAnchor ) ) )
		{
			var start = string.IsNullOrEmpty( LineStartAnchor ) ? frame.PointToWorld( Offset + LineStart ) : player.Anchor( LineStartAnchor ).Position;
			var end = string.IsNullOrEmpty( LineEndAnchor ) ? frame.PointToWorld( Offset + LineEnd ) : player.Anchor( LineEndAnchor ).Position;
			var t = sample switch
			{
				ParticleShapeSample.ByIndex => places <= 1 ? 0.0f : ( index % places ) / (float)( places - 1 ),
				ParticleShapeSample.ByTime => time.Clamp( 0.0f, 1.0f ),
				_ => Rng.Float( 0.0f, 1.0f ),
			};
			var along = end - start;

			return new ShapePose( Vector3.Lerp( start, end, t ), along.IsNearlyZero() ? frame.Rotation.Up : along.Normal, t );
		}

		var pose = Sample( sample, index, places, time, _random );

		return new ShapePose( frame.PointToWorld( pose.Position ), frame.NormalToWorld( pose.Normal ), pose.Along );
	}

	/// <summary>A pose from the shape, in the shape's own frame (the emitter's, plus <see cref="Offset"/> and <see cref="Rotation"/>).</summary>
	public ShapePose Sample( ParticleShapeSample sample, int index, int places, float time, Random random = null )
	{
		_random = random ?? Random.Shared;

		var u = sample switch
		{
			ParticleShapeSample.ByIndex => places <= 1 ? 0.0f : ( index % places ) / (float)( places - 1 ),
			ParticleShapeSample.ByTime => time.Clamp( 0.0f, 1.0f ),
			_ => Rng.Float( 0.0f, 1.0f ),
		};

		var anywhere = sample == ParticleShapeSample.Random;
		var pose = Local( u, anywhere, index, places );
		var turn = Rotation.ToRotation();

		return new ShapePose( Offset + ( turn * pose.Position ), turn * pose.Normal, pose.Along );
	}

	ShapePose Local( float u, bool random, int index, int places )
	{
		switch ( Kind )
		{
			case ShapeKind.Sphere:
			{
				var direction = random ? Rng.VectorInSphere().Normal : Fibonacci( u, index, places );
				var radius = OnShell || !random ? Radius : Rng.Float( 0.0f, Radius );

				return new ShapePose( direction * radius, direction, u );
			}

			case ShapeKind.Box:
			{
				var half = BoxSize * 0.5f;

				if ( random )
				{
					var inside = new Vector3(
						Rng.Float( -half.x, half.x ),
						Rng.Float( -half.y, half.y ),
						Rng.Float( -half.z, half.z ) );

					if ( !OnShell ) return new ShapePose( inside, Vector3.Up, u );

					var axis = Rng.Int( 0, 2 );
					var sign = Rng.Float( 0.0f, 1.0f ) < 0.5f ? -1.0f : 1.0f;

					inside = axis switch
					{
						0 => inside.WithX( half.x * sign ),
						1 => inside.WithY( half.y * sign ),
						_ => inside.WithZ( half.z * sign ),
					};

					return new ShapePose( inside, inside.Normal, u );
				}

				//Walked as a line around the box's floor: corners in order.
				var edge = u * 4.0f;
				var side = (int)MathF.Floor( edge ) % 4;
				var t = edge - MathF.Floor( edge );

				var corners = new[]
				{
					new Vector3( -half.x, 0, -half.z ), new Vector3( half.x, 0, -half.z ),
					new Vector3( half.x, 0, half.z ), new Vector3( -half.x, 0, half.z ),
				};

				return new ShapePose( Vector3.Lerp( corners[side], corners[( side + 1 ) % 4], t ), Vector3.Up, u );
			}

			case ShapeKind.Circle:
			{
				var angle = ( random ? Rng.Float( 0.0f, 1.0f ) : u ) * MathF.PI * 2.0f;
				var radius = OnShell || !random ? Radius : MathF.Sqrt( Rng.Float( 0.0f, 1.0f ) ) * Radius;
				var flat = new Vector3( MathF.Cos( angle ), 0.0f, MathF.Sin( angle ) );

				return new ShapePose( flat * radius, Vector3.Up, u );
			}

			case ShapeKind.Cone:
			{
				var angle = ( random ? Rng.Float( 0.0f, 1.0f ) : u ) * MathF.PI * 2.0f;
				var distance = random ? Rng.Float( 0.0f, Radius ) : Radius;
				var mouth = MathF.Tan( ConeAngle.DegreeToRadian() ) * distance;
				var radius = OnShell || !random ? mouth : Rng.Float( 0.0f, mouth );
				var at = new Vector3( MathF.Cos( angle ) * radius, distance, MathF.Sin( angle ) * radius );

				return new ShapePose( at, at.Normal, u );
			}

			case ShapeKind.Line:
			{
				var t = random ? Rng.Float( 0.0f, 1.0f ) : u;
				var along = LineEnd - LineStart;

				return new ShapePose( Vector3.Lerp( LineStart, LineEnd, t ), along.IsNearlyZero() ? Vector3.Up : along.Normal, u );
			}

			case ShapeKind.Spline:
			{
				var t = random ? Rng.Float( 0.0f, 1.0f ) : u;

				return Spline( t );
			}

			case ShapeKind.Grid:
			{
				var cx = Math.Max( 1, (int)GridCount.x );
				var cy = Math.Max( 1, (int)GridCount.y );
				var cz = Math.Max( 1, (int)GridCount.z );
				var total = cx * cy * cz;

				var cell = random ? Rng.Int( 0, total - 1 ) : (int)MathF.Round( u * ( total - 1 ) );

				var ix = cell % cx;
				var iy = ( cell / cx ) % cy;
				var iz = cell / ( cx * cy );

				var at = new Vector3(
					( ix - ( cx - 1 ) * 0.5f ) * GridSpacing.x,
					( iy - ( cy - 1 ) * 0.5f ) * GridSpacing.y,
					( iz - ( cz - 1 ) * 0.5f ) * GridSpacing.z );

				return new ShapePose( at, Vector3.Up, total <= 1 ? 0.0f : cell / (float)( total - 1 ) );
			}

			case ShapeKind.Helix:
			{
				var t = random ? Rng.Float( 0.0f, 1.0f ) : u;
				var angle = t * Turns * MathF.PI * 2.0f;
				var at = new Vector3( MathF.Cos( angle ) * Radius, t * Height, MathF.Sin( angle ) * Radius );

				return new ShapePose( at, new Vector3( at.x, 0.0f, at.z ).Normal, u );
			}

			case ShapeKind.Model:
				return FromModel( u, random, index, places );

			default:
				return new ShapePose( Vector3.Zero, Vector3.Up, u );
		}
	}

	//A pose off the model, in the model's own space scaled by ModelScale.
	ShapePose FromModel( float u, bool random, int index, int places )
	{
		var model = Model;

		if ( model is null ) return new ShapePose( Vector3.Zero, Vector3.Up, u );

		var scale = ModelScale;

		switch ( ModelSampling )
		{
			case ModelSample.Parts:
			{
				var count = model.MeshCount;
				if ( count <= 0 ) break;

				var part = PartOf( u, random, index, count );
				var centre = model.MeshBounds( part ).Center;

				return new ShapePose( centre * scale, Vector3.Up, count <= 1 ? 0.0f : part / (float)( count - 1 ) );
			}

			case ModelSample.Vertices:
			{
				var vertices = model.GetVertices();
				if ( vertices is null || vertices.Length == 0 ) break;

				var at = random ? Rng.Int( 0, vertices.Length - 1 ) : (int)MathF.Round( u * ( vertices.Length - 1 ) );
				var vertex = vertices[Math.Clamp( at, 0, vertices.Length - 1 )];

				return new ShapePose( vertex.Position * scale, vertex.Normal.IsNearlyZero() ? Vector3.Up : vertex.Normal.Normal, u );
			}

			case ModelSample.Surface:
			{
				if ( !Triangles( model, out var positions, out var indices ) ) break;

				//A triangle picked by index, a point picked in it: not weighted by area, which is the bargain that costs nothing per particle.
				var triangles = indices.Length / 3;
				var tri = random ? Rng.Int( 0, triangles - 1 ) : (int)MathF.Round( u * ( triangles - 1 ) );
				tri = Math.Clamp( tri, 0, triangles - 1 );

				var a = positions[indices[tri * 3]];
				var b = positions[indices[tri * 3 + 1]];
				var c = positions[indices[tri * 3 + 2]];

				var r1 = MathF.Sqrt( Rng.Float( 0.0f, 1.0f ) );
				var r2 = Rng.Float( 0.0f, 1.0f );
				var point = a * ( 1.0f - r1 ) + b * ( r1 * ( 1.0f - r2 ) ) + c * ( r1 * r2 );
				var normal = Vector3.Cross( b - a, c - a );

				return new ShapePose( point * scale, normal.IsNearlyZero() ? Vector3.Up : normal.Normal, u );
			}

			case ModelSample.Bounds:
			{
				var box = model.Bounds;
				var point = new Vector3(
					Rng.Float( box.Mins.x, box.Maxs.x ),
					Rng.Float( box.Mins.y, box.Maxs.y ),
					Rng.Float( box.Mins.z, box.Maxs.z ) );

				return new ShapePose( point * scale, Vector3.Up, u );
			}

			case ModelSample.Attachments:
			{
				var count = model.AttachmentCount;
				if ( count <= 0 ) break;

				var at = PartOf( u, random, index, count );

				if ( model.GetAttachment( at ) is { } tx )
					return new ShapePose( tx.Position * scale, tx.Rotation.Forward, count <= 1 ? 0.0f : at / (float)( count - 1 ) );

				break;
			}

			case ModelSample.Bones:
			{
				var bones = model.Bones.AllBones;
				if ( bones.Count <= 0 ) break;

				var at = PartOf( u, random, index, bones.Count );
				var bone = bones[at];
				var tx = bone.LocalTransform;

				for ( var parent = bone.Parent; parent is not null; parent = parent.Parent )
					tx = parent.LocalTransform.ToWorld( tx );

				return new ShapePose( tx.Position * scale, tx.Rotation.Forward, bones.Count <= 1 ? 0.0f : at / (float)( bones.Count - 1 ) );
			}
		}

		return new ShapePose( Vector3.Zero, Vector3.Up, u );
	}

	//Which of N things this is: the particle's own by index, a random one, or the one at u.
	static int PartOf( float u, bool random, int index, int count )
	{
		if ( count <= 1 ) return 0;
		if ( random ) return Rng.Int( 0, count - 1 );

		return Math.Clamp( (int)MathF.Round( u * ( count - 1 ) ), 0, count - 1 );
	}

	/// <summary>The piece of the model a particle at this index stands for - what the mesh module draws for it.</summary>
	public int PartFor( ParticleShapeSample sample, int index, int places, float time )
	{
		if ( Kind != ShapeKind.Model || Model is not { } model || ModelSampling != ModelSample.Parts ) return -1;

		var count = model.MeshCount;
		if ( count <= 0 ) return -1;

		var u = sample switch
		{
			ParticleShapeSample.ByIndex => places <= 1 ? 0.0f : ( index % places ) / (float)( places - 1 ),
			ParticleShapeSample.ByTime => time.Clamp( 0.0f, 1.0f ),
			_ => 0.0f,
		};

		return PartOf( u, sample == ParticleShapeSample.Random, index, count );
	}

	//Points spread evenly over a sphere, the k-th of N - the sunflower.
	static Vector3 Fibonacci( float u, int index, int places )
	{
		var n = Math.Max( places, 1 );
		var i = ( index % n ) + 0.5f;
		var y = 1.0f - ( i / n ) * 2.0f;
		var r = MathF.Sqrt( MathF.Max( 0.0f, 1.0f - y * y ) );
		var angle = i * 2.399963f;

		return new Vector3( MathF.Cos( angle ) * r, y, MathF.Sin( angle ) * r );
	}

	/// <summary>Where the spline is at t, Catmull-Rom between its points.</summary>
	public ShapePose Spline( float t )
	{
		var points = Points;

		if ( points is null || points.Count == 0 ) return new ShapePose( Vector3.Zero, Vector3.Up, t );
		if ( points.Count == 1 ) return new ShapePose( points[0], Vector3.Up, t );

		var n = points.Count;
		var segments = Closed ? n : n - 1;
		var scaled = t.Clamp( 0.0f, 1.0f ) * segments;
		var segment = Math.Min( (int)MathF.Floor( scaled ), segments - 1 );
		var f = scaled - segment;

		Vector3 At( int i ) => Closed ? points[( ( i % n ) + n ) % n] : points[Math.Clamp( i, 0, n - 1 )];

		var p0 = At( segment - 1 );
		var p1 = At( segment );
		var p2 = At( segment + 1 );
		var p3 = At( segment + 2 );

		var f2 = f * f;
		var f3 = f2 * f;

		var position = 0.5f * ( ( 2.0f * p1 ) + ( -p0 + p2 ) * f + ( 2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3 ) * f2 + ( -p0 + 3.0f * p1 - 3.0f * p2 + p3 ) * f3 );
		var tangent = 0.5f * ( ( -p0 + p2 ) + 2.0f * ( 2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3 ) * f + 3.0f * ( -p0 + 3.0f * p1 - 3.0f * p2 + p3 ) * f2 );

		return new ShapePose( position, tangent.IsNearlyZero() ? Vector3.Up : tangent.Normal, t );
	}
}
