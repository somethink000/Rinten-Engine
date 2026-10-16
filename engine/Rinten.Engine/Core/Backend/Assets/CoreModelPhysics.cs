using System.Collections.Generic;
using System.IO;

using Rinten.CoreAnimation;

namespace Rinten;

//Builds collision out of a model in our own format. The other end of ModelCompiler, and the point of the whole exercise:
//geometry that came out of a file this engine wrote, simulated by physics this engine wrote, with no legacy code in the chain.
internal static unsafe class CoreModelPhysics
{
	/// <summary>Add every shape of every part of <paramref name="model"/> to a single body, in the model's own space. One body rather than one per part, because that is what a static prop or a collider is. A ragdoll wants a part each and goes through the group path instead.</summary>
	public static int AddShapes( PhysicsBody body, CoreAssets.ModelFile model )
	{
		if ( !body.IsValid() ) return 0;
		if ( !model.Valid ) return 0;

		var added = 0;
		var parts = model.BodyCount;

		for ( var part = 0; part < parts; ++part )
		{
			var transform = ReadTransform( model, part );

			var shapes = model.ShapeCount( part );

			for ( var index = 0; index < shapes; ++index )
			{
				var shape = AddShape( body, model, part, index, transform );
				if ( !shape.IsValid() ) continue;

				var surface = model.ShapeSurface( part, index );
				if ( !string.IsNullOrEmpty( surface ) )
					shape.SurfaceMaterial = surface;

				++added;
			}
		}

		return added;
	}

	private static Transform ReadTransform( CoreAssets.ModelFile model, int part )
	{
		var t = stackalloc float[7];
		model.BodyTransform( part, t );

		return new Transform(
			new Vector3( t[0], t[1], t[2] ),
			new Rotation( t[3], t[4], t[5], t[6] ) );
	}

	private static PhysicsShape AddShape( PhysicsBody body, CoreAssets.ModelFile model, int part, int index, Transform transform )
	{
		var primitive = stackalloc float[7];
		model.ShapePrimitive( part, index, primitive );

		switch ( (ModelFormat.ShapeKind)model.ShapeKind( part, index ) )
		{
			case ModelFormat.ShapeKind.Sphere:
			{
				var center = transform.PointToWorld( new Vector3( primitive[0], primitive[1], primitive[2] ) );
				return body.AddSphereShape( center, primitive[3], rebuildMass: false );
			}

			case ModelFormat.ShapeKind.Capsule:
			{
				var a = transform.PointToWorld( new Vector3( primitive[0], primitive[1], primitive[2] ) );
				var b = transform.PointToWorld( new Vector3( primitive[3], primitive[4], primitive[5] ) );
				return body.AddCapsuleShape( a, b, primitive[6], rebuildMass: false );
			}

			case ModelFormat.ShapeKind.Hull:
			{
				var points = ReadPoints( model, part, index );
				if ( points.Count < 4 ) return null;

				//The points are in the part's space; the body expects the model's.
				for ( var i = 0; i < points.Count; ++i )
					points[i] = transform.PointToWorld( points[i] );

				return body.AddHullShape( Vector3.Zero, Rotation.Identity, points, rebuildMass: false );
			}

			case ModelFormat.ShapeKind.Mesh:
			{
				var points = ReadPoints( model, part, index );
				var indices = ReadIndices( model, part, index );

				if ( points.Count < 3 || indices.Count < 3 ) return null;

				for ( var i = 0; i < points.Count; ++i )
					points[i] = transform.PointToWorld( points[i] );

				return body.AddMeshShape( points, indices );
			}

			default:
				return null;
		}
	}

	private static List<Vector3> ReadPoints( CoreAssets.ModelFile model, int part, int index )
	{
		var count = model.ShapePointCount( part, index );
		if ( count <= 0 ) return new List<Vector3>();

		var flat = new float[count * 3];

		fixed ( float* p = flat )
		{
			model.ShapeCopyPoints( part, index, p, flat.Length );
		}

		var points = new List<Vector3>( count );
		for ( var i = 0; i < count; ++i )
			points.Add( new Vector3( flat[i * 3], flat[i * 3 + 1], flat[i * 3 + 2] ) );

		return points;
	}

	private static List<int> ReadIndices( CoreAssets.ModelFile model, int part, int index )
	{
		var count = model.ShapeIndexCount( part, index );
		if ( count <= 0 ) return new List<int>();

		var raw = new uint[count];

		fixed ( uint* i = raw )
		{
			model.ShapeCopyIndices( part, index, i, raw.Length );
		}

		var indices = new List<int>( count );
		foreach ( var value in raw )
			indices.Add( (int)value );

		return indices;
	}

	/// <summary>Native bodies as a physics description, plus joints from the sibling .mdl.</summary>
	public static PhysicsGroupDescription Describe( CoreAssets.ModelFile model, string resourcePath = "" )
	{
		if ( !model.Valid ) return null;

		var parts = model.BodyCount;
		if ( parts <= 0 ) return null;

		var desc = new PhysicsGroupDescription( resourcePath ?? "" );

		for ( var part = 0; part < parts; ++part )
		{
			var body = new PhysicsGroupDescription.BodyPart
			{
				Transform = ReadTransform( model, part ),
				BoneName = model.BodyBone( part )
			};

			var props = stackalloc float[4];
			model.BodyProperties( part, props );
			body.Mass = props[0] > 0f ? props[0] : 1f;
			body.LinearDamping = props[1];
			body.AngularDamping = props[2];
			body.GravityScale = props[3] > 0f ? props[3] : 1f;

			var shapes = model.ShapeCount( part );
			for ( var index = 0; index < shapes; ++index )
			{
				var surfaceName = model.ShapeSurface( part, index );
				var surface = string.IsNullOrWhiteSpace( surfaceName ) ? null : Surface.FindByName( surfaceName );
				var primitive = stackalloc float[7];
				model.ShapePrimitive( part, index, primitive );

				switch ( (ModelFormat.ShapeKind)model.ShapeKind( part, index ) )
				{
					case ModelFormat.ShapeKind.Sphere:
						body.Add( new PhysicsGroupDescription.BodyPart.SpherePart(
							new Sphere( new Vector3( primitive[0], primitive[1], primitive[2] ), primitive[3] ), surface ) );
						break;
					case ModelFormat.ShapeKind.Capsule:
						body.Add( new PhysicsGroupDescription.BodyPart.CapsulePart(
							new Capsule(
								new Vector3( primitive[0], primitive[1], primitive[2] ),
								new Vector3( primitive[3], primitive[4], primitive[5] ),
								primitive[6] ), surface ) );
						break;
					case ModelFormat.ShapeKind.Hull:
					{
						var points = ReadPoints( model, part, index );
						body.Add( new PhysicsGroupDescription.BodyPart.HullPart( points.ToArray(), System.Array.Empty<int>(), surface ) );
						break;
					}
					case ModelFormat.ShapeKind.Mesh:
					{
						var points = ReadPoints( model, part, index );
						var indices = ReadIndices( model, part, index );
						body.Add( new PhysicsGroupDescription.BodyPart.MeshPart( points.ToArray(), indices.ToArray(), surface, null ) );
						break;
					}
				}
			}

			desc.AddPart( body );
		}

		AddJoints( desc, resourcePath, model );
		return desc.Parts.Count > 0 ? desc : null;
	}

	static void AddJoints( PhysicsGroupDescription desc, string resourcePath, CoreAssets.ModelFile model )
	{
		if ( string.IsNullOrWhiteSpace( resourcePath ) ) return;

		var definition = Path.ChangeExtension( resourcePath, ".mdl" ).Replace( '\\', '/' );
		var full = CoreModel.ResolveOnDisk( definition );
		if ( full is null || !File.Exists( full ) ) return;

		try
		{
			//Deserialised, not parsed by hand, so new joint fields always reach the solver
			var parsed = System.Text.Json.JsonSerializer.Deserialize<ModelDefinition>(
				CompiledFile.TextOf( full ), DefinitionOptions );

			if ( parsed?.Joints is not { Count: > 0 } joints ) return;

			int BodyOf( string bone )
			{
				if ( string.IsNullOrWhiteSpace( bone ) ) return -1;
				for ( var i = 0; i < desc.Parts.Count; i++ )
				{
					if ( string.Equals( desc.Parts[i].BoneName, bone, StringComparison.OrdinalIgnoreCase ) )
						return i;
				}
				return -1;
			}

			var rests = BoneRests( model );

			foreach ( var joint in joints )
			{
				if ( joint is null ) continue;

				var a = BodyOf( joint.BoneA );
				var b = BodyOf( joint.BoneB );
				if ( a < 0 || b < 0 || a == b ) continue;

				var kind = joint.Type;

				var position = joint.Position;
				var rotation = joint.Rotation;
				//Anchors are in unscaled bone space; the runtime reapplies the body scale on the finished frame
				var boneA = RestOf( rests, joint.BoneA ).WithScale( 1f );
				var boneB = RestOf( rests, joint.BoneB ).WithScale( 1f );

				if ( joint.ModelSpace )
				{
					position = boneA.PointToLocal( position );
					rotation = ( boneA.Rotation.Inverse * rotation.ToRotation() ).Angles();
				}

				var world = boneA.PointToWorld( position );
				var turn = rotation.ToRotation();

				var ball = kind == ModelDefinition.JointKind.Ball;

				//Swing offset turns only the parent frame, centring the cone there without moving the twist axis
				var swingOffset = ball ? joint.SwingOffset.ToRotation() : Rotation.Identity;

				var frame1 = new Transform( position, turn * swingOffset );
				var frame2 = new Transform( boneB.PointToLocal( world ),
					( boneB.Rotation.Inverse * ( boneA.Rotation * turn ) ).Normal );

				desc.AddJoint( new PhysicsGroupDescription.Joint
				{
					Type = kind switch
					{
						ModelDefinition.JointKind.Ball => PhysicsGroupDescription.JointType.Ball,
						ModelDefinition.JointKind.Slider => PhysicsGroupDescription.JointType.Slider,
						ModelDefinition.JointKind.Fixed => PhysicsGroupDescription.JointType.Fixed,
						_ => PhysicsGroupDescription.JointType.Hinge,
					},

					//A joint turned off is kept rather than dropped - see PhysicsGroupDescription.Joint.Enabled for why one still matters switched off.
					Enabled = joint.Enabled,
					Body1 = a,
					Body2 = b,
					Frame1 = frame1,
					Frame2 = frame2,
					EnableCollision = joint.EnableCollision,

					EnableTwistLimit = kind == ModelDefinition.JointKind.Hinge
						|| ( ball && joint.EnableTwistLimit ),
					TwistMin = ball ? joint.MinTwist : joint.MinAngle,
					TwistMax = ball ? joint.MaxTwist : joint.MaxAngle,

					EnableSwingLimit = ball && joint.EnableSwingLimit,
					SwingMin = 0,
					SwingMax = joint.SwingLimit,

					Friction = joint.Friction,
					LinearStrength = joint.LinearStrength,
					AngularStrength = joint.AngularStrength,
					AngularFrequency = ball ? joint.Stiffness : 0f,
					AngularDampingRatio = ball ? joint.Damping : 0f,

					EnableLinearLimit = kind == ModelDefinition.JointKind.Slider,
					LinearMin = joint.MinLength,
					LinearMax = joint.MaxLength,
				} );
			}
		}
		catch ( System.Text.Json.JsonException )
		{
		}
	}

	//How a model definition reads here. Forgiving in the same ways the tools' compiler is - a hand-written
	//.mdl uses names for its enums and may carry comments, one the serialiser wrote uses numbers.
	static readonly System.Text.Json.JsonSerializerOptions DefinitionOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		IncludeFields = true,

		//The model's own graph, stepped over - see ModelDefinition.ModelGraph.Skim.
		MaxDepth = 512,
		Converters = { new EnumConvert(), new ModelDefinition.ModelGraph.Skim() },
	};

	static Dictionary<string, Transform> BoneRests( CoreAssets.ModelFile model )
	{
		var map = new Dictionary<string, Transform>( StringComparer.OrdinalIgnoreCase );
		if ( model is not { Valid: true } ) return map;

		var skeleton = CoreAnimRuntime.CreateSkeleton( model );
		if ( skeleton is null ) return map;

		{
			var count = skeleton.BoneCount;
			if ( count <= 0 ) return map;

			var locals = new Transform[count];
			var parents = new int[count];

			for ( var i = 0; i < count; i++ )
			{
				locals[i] = skeleton.Rest( i ).ToTransform();
				parents[i] = skeleton.BoneParent( i );
			}

			//Compose bone rests into model space, since joint anchors are in model space, not parent space
			var composed = new Transform[count];
			var done = new bool[count];

			Transform Composed( int bone )
			{
				if ( done[bone] ) return composed[bone];

				done[bone] = true;

				var parent = parents[bone];
				composed[bone] = parent >= 0 && parent != bone
					? Composed( parent ).ToWorld( locals[bone] )
					: locals[bone];

				return composed[bone];
			}

			for ( var i = 0; i < count; i++ )
			{
				var name = skeleton.BoneName( i );
				if ( string.IsNullOrWhiteSpace( name ) ) continue;

				map[name] = Composed( i );
			}
		}

		return map;
	}

	static Transform RestOf( Dictionary<string, Transform> rests, string bone )
		=> !string.IsNullOrWhiteSpace( bone ) && rests.TryGetValue( bone, out var rest ) ? rest : Transform.Zero;

	static Vector3 Vec( System.Text.Json.JsonElement entry, string name )
		=> entry.TryGetProperty( name, out var value ) ? Triple( value ) : default;

	static Angles Ang( System.Text.Json.JsonElement entry, string name )
	{
		if ( !entry.TryGetProperty( name, out var value ) ) return default;
		if ( value.ValueKind == System.Text.Json.JsonValueKind.String )
			return Angles.Parse( value.GetString() );

		var v = Triple( value );
		return new Angles( v.x, v.y, v.z );
	}

	static Vector3 Triple( System.Text.Json.JsonElement value )
	{
		if ( value.ValueKind == System.Text.Json.JsonValueKind.String )
			return Vector3.Parse( value.GetString() );

		if ( value.ValueKind == System.Text.Json.JsonValueKind.Array )
		{
			var i = 0;
			var v = Vector3.Zero;
			foreach ( var n in value.EnumerateArray() )
			{
				if ( !n.TryGetSingle( out var f ) ) { i++; continue; }
				if ( i == 0 ) v.x = f;
				else if ( i == 1 ) v.y = f;
				else if ( i == 2 ) v.z = f;
				i++;
			}

			return v;
		}

		if ( value.ValueKind != System.Text.Json.JsonValueKind.Object ) return default;

		return new Vector3(
			Num( value, "x" ) != 0 ? Num( value, "x" ) : Num( value, "pitch" ),
			Num( value, "y" ) != 0 ? Num( value, "y" ) : Num( value, "yaw" ),
			Num( value, "z" ) != 0 ? Num( value, "z" ) : Num( value, "roll" ) );
	}

	static float Num( System.Text.Json.JsonElement obj, string name )
		=> obj.TryGetProperty( name, out var value ) && value.TryGetSingle( out var n ) ? n : 0;
}
