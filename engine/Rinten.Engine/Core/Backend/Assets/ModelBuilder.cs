using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Rinten.CoreAssets;

//Compiled model writer; ModelFile is the reader and both share ModelFormat
internal sealed unsafe partial class ModelBuilder : IDisposable
{
	//What the model says about itself that is one bit - see ModelFormat.Header.flags.
	internal ModelFormat.ModelFlags Flags { get; set; }

	private static readonly Logger log = new( "Core" );

	//Skin as eight bytes per vertex (four bone indices, four weights), the ModelFormat.SkinVertex layout
	private const int SkinStride = 8;

	private string name = "";

	private readonly float[] renderBounds = new float[6];
	private readonly float[] physicsBounds = new float[6];
	private bool hasRenderBounds;
	private bool hasPhysicsBounds;

	private readonly List<Vertex> vertices = new();
	private readonly List<uint> indices = new();

	//Placement (rotation, offset) for the next vertex run, applied centrally in AddVertices
	private readonly float[] placementRotation = { 0.0f, 0.0f, 0.0f, 1.0f };
	private readonly float[] placementOffset = new float[3];
	private bool hasPlacement;

	//Parallel to the vertices when the model is skinned, empty otherwise.
	private readonly List<byte> skin = new();

	//Whether any source art had skin weights; if none, added bones lie over a still mesh
	private bool weighted;

	//A blend shape being gathered by name, since one shape arrives across several vertex runs
	private sealed class MorphEntry
	{
		public string Name = "";
		public readonly List<uint> Vertices = new();

		/// <summary>Three per vertex.</summary>
		public readonly List<float> Positions = new();

		/// <summary>Three per vertex.</summary>
		public readonly List<float> Normals = new();
	}

	private readonly List<MorphEntry> morphs = new();

	private sealed class BoneEntry
	{
		public string Name = "";
		public int Parent;
		public readonly float[] InverseBind = new float[16];
	}

	private readonly List<BoneEntry> bones = new();

	private sealed class MeshEntry
	{
		public string Name = "";
		public string Material = "";
		public uint FirstIndex, IndexCount, FirstVertex, VertexCount;

		/// <summary>Which body-group choices claim this mesh. Zero is always drawn.</summary>
		public ulong Group;

		/// <summary>What the art said its surface is - see <see cref="ModelFormat.Surface"/>.</summary>
		public ModelFormat.Surface Surface;

		/// <summary>Where its material cuts holes in it, or null - see <see cref="SetMeshCutout"/>.</summary>
		public LevelChain.Cutout Cutout;

		/// <summary>What kind of mesh it is - the impostor's plane says so. See ModelFormat.MeshFlags.</summary>
		public ModelFormat.MeshFlags Flags;

		/// <summary>The first level of its chain it is no longer in, or -1 for all of them - see <see cref="SetMeshLastLevel"/>.</summary>
		public int GoneAt = -1;

		/// <summary>The mesh whose chain this one is a level of, or -1 - see <see cref="SetMeshAsLevel"/>.</summary>
		public int LevelOf = -1;
		public int LevelAt;
	}

	private sealed class ChoiceEntry
	{
		public string Name = "";
		public ulong Mask;
	}

	private sealed class PartEntry
	{
		public string Name = "";
		public readonly List<ChoiceEntry> Choices = new();
	}

	private sealed class MaterialGroupEntry
	{
		public string Name = "";

		/// <summary>One per mesh, in mesh order. Short entries mean the meshes past the end keep the material they were built with.</summary>
		public readonly List<string> Materials = new();
	}

	private sealed class ShapeEntry
	{
		public ModelFormat.ShapeKind Kind;
		public string Surface = "";
		public readonly float[] Primitive = new float[7];
		public readonly List<float> Points = new();
		public readonly List<uint> Indices = new();
	}

	private sealed class BodyEntry
	{
		public string Name = "";
		public string BoneName = "";
		public readonly float[] Transform = { 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f };
		public float Mass;
		public float LinearDamping;
		public float AngularDamping;
		public float GravityScale = 1.0f;
		public bool OverrideMassCenter;
		public readonly float[] MassCenter = new float[3];
		public readonly List<ShapeEntry> Shapes = new();
	}

	private readonly List<MeshEntry> meshes = new();
	private readonly List<BodyEntry> bodies = new();
	private readonly List<PartEntry> parts = new();
	private readonly List<MaterialGroupEntry> materialGroups = new();

	//Zero means "work it out from the first choice of every group", which is what a caller who never says otherwise wants.
	private ulong defaultMask;

	//-- what it is called and where it is ---------------------------------

	public void SetName( string value ) => name = value ?? "";

	/// <summary>The level-of-detail group the model keeps its detail by - see ModelDefinition.LodGroup.</summary>
	public void SetLodGroup( string value ) => lodGroup = value?.Trim() ?? "";

	private string lodGroup = "";

	/// <summary>How many levels of detail the clusters are built to, or nought for as many as the geometry allows - see <see cref="LevelChain.BuildChain"/>. One is level nought alone.</summary>
	public void SetClusterLevels( int value ) => clusterLevels = value > 0 ? value : 0;

	private int clusterLevels;

	/// <summary>Writes no clusters nor positions: the model is drawn only if ever asked, and is cut then - see ModelFormat.ModelFlags.NoClusters.</summary>
	public void SetNoClusters( bool value ) => noClusters = value;

	private bool noClusters;

	/// <summary>What share of a level's triangles the next level of the chain keeps - a half, unless the model says otherwise. See LevelChain.BuildChain.</summary>
	public void SetChainStep( float value ) => chainStep = value is >= 0.1f and <= 0.9f ? value : 0.5f;

	private float chainStep = 0.5f;

	/// <summary>Whether the model is a piece of something larger - a terrain chunk - whose open edges are where its neighbours meet it: the chain keeps those edges at every level, so two pieces at any two levels still meet. See LevelChain.BuildChain.</summary>
	public void SetLockBorder( bool value ) => lockBorder = value;

	private bool lockBorder;

	/// <summary>How many levels coarser than the camera's a chain's shadows are drawn with - see ModelDefinition.ShadowLevelOffset.</summary>
	public void SetShadowLevelOffset( int value ) => shadowLevelOffset = Math.Clamp( value, 0, 3 );

	private int shadowLevelOffset;

	/// <summary>Leaves a mesh out of its chain from <paramref name="level"/> on: a bolt, a strap, an eyelash that is nothing from that far away. Below 1 is ignored - level nought is the model as authored.</summary>
	public int SetMeshLastLevel( int mesh, int level )
	{
		if ( mesh < 0 || mesh >= meshes.Count || level < 1 ) return 0;

		meshes[mesh].GoneAt = level;
		return 1;
	}

	/// <summary>Makes a mesh of the model a level of another one's chain rather than a mesh of its own: the artist's own coarse copy, standing in for the level the simplifier would have made there. It is drawn only as that level, and the levels after it are made from it. Its bones and blend shapes are its own.</summary>
	public int SetMeshAsLevel( int mesh, int of, int level )
	{
		if ( mesh < 0 || mesh >= meshes.Count || of < 0 || of >= meshes.Count || of == mesh || level < 1 ) return 0;

		meshes[mesh].LevelOf = of;
		meshes[mesh].LevelAt = level;
		return 1;
	}

	public void SetPlacement( float* rotation, float* offset )
	{
		if ( rotation is null && offset is null )
		{
			hasPlacement = false;
			return;
		}

		placementRotation[0] = rotation is not null ? rotation[0] : 0.0f;
		placementRotation[1] = rotation is not null ? rotation[1] : 0.0f;
		placementRotation[2] = rotation is not null ? rotation[2] : 0.0f;
		placementRotation[3] = rotation is not null ? rotation[3] : 1.0f;

		placementOffset[0] = offset is not null ? offset[0] : 0.0f;
		placementOffset[1] = offset is not null ? offset[1] : 0.0f;
		placementOffset[2] = offset is not null ? offset[2] : 0.0f;

		hasPlacement = true;
	}

	public void SetRenderBounds( float* minMax )
	{
		if ( minMax is null ) return;

		for ( var i = 0; i < 6; i++ ) renderBounds[i] = minMax[i];
		hasRenderBounds = true;
	}

	public void SetPhysicsBounds( float* minMax )
	{
		if ( minMax is null ) return;

		for ( var i = 0; i < 6; i++ ) physicsBounds[i] = minMax[i];
		hasPhysicsBounds = true;
	}

	//-- geometry -----------------------------------------------------------

	public int AddVertices( Vertex* added, int count )
	{
		if ( added is null || count <= 0 ) return -1;

		//Whatever was added before this run either brought its own skin or gets a rigid one now, so the two arrays stay index for index.
		PadSkin();

		var at = vertices.Count;

		//The vertex the caller holds and the one on disk are the same layout, deliberately - so this is a copy rather than a conversion.
		for ( var i = 0; i < count; i++ ) vertices.Add( added[i] );

		if ( hasPlacement ) Place( at, count );

		return at;
	}

	public int AddIndices( uint* added, int count )
	{
		if ( added is null || count <= 0 ) return -1;

		var at = indices.Count;
		for ( var i = 0; i < count; i++ ) indices.Add( added[i] );

		return at;
	}

	public int AddMesh( string meshName, string material,
		int firstIndex, int indexCount, int firstVertex, int vertexCount )
	{
		meshes.Add( new MeshEntry
		{
			Name = meshName ?? "",
			Material = material ?? "",
			FirstIndex = (uint)( firstIndex < 0 ? 0 : firstIndex ),
			IndexCount = (uint)( indexCount < 0 ? 0 : indexCount ),
			FirstVertex = (uint)( firstVertex < 0 ? 0 : firstVertex ),
			VertexCount = (uint)( vertexCount < 0 ? 0 : vertexCount ),
		} );

		return meshes.Count - 1;
	}

	public int SetMeshMaterial( int mesh, string material )
	{
		if ( mesh < 0 || mesh >= meshes.Count ) return 0;

		meshes[mesh].Material = material ?? "";
		return 1;
	}

	/// <summary>A mesh's cutout alpha and cutoff, used only by LOD to find cards</summary>
	public int SetMeshCutout( int mesh, byte[] alpha, int width, int height, float cutoff )
	{
		if ( mesh < 0 || mesh >= meshes.Count ) return 0;

		meshes[mesh].Cutout = alpha is null || width <= 0 || height <= 0 || alpha.Length < width * height
			? null
			: new LevelChain.Cutout( alpha, width, height, (byte)Math.Clamp( (int)MathF.Round( cutoff * 255.0f ), 1, 255 ) );

		return 1;
	}

	/// <summary>What the art said this mesh's surface is, where it said it in numbers - see <see cref="ModelFormat.Surface"/>. Every importer says it the same way, here, so what is done with it is decided once.</summary>
	public int SetMeshSurface( int mesh, in ModelFormat.Surface surface )
	{
		if ( mesh < 0 || mesh >= meshes.Count ) return 0;

		meshes[mesh].Surface = surface;
		return 1;
	}

	public ModelFormat.Surface MeshSurface( int mesh )
		=> mesh >= 0 && mesh < meshes.Count ? meshes[mesh].Surface : default;

	/// <summary>Whether a mesh wears what its art said, or the material somebody chose for it and nothing else - see <see cref="ModelFormat.SurfaceSays.Worn"/>.</summary>
	public int WearMeshSurface( int mesh, bool worn )
	{
		if ( mesh < 0 || mesh >= meshes.Count ) return 0;

		if ( worn ) meshes[mesh].Surface.says |= ModelFormat.SurfaceSays.Worn;
		else meshes[mesh].Surface.says &= ~ModelFormat.SurfaceSays.Worn;

		return 1;
	}

	public int SetMeshName( int mesh, string meshName )
	{
		if ( mesh < 0 || mesh >= meshes.Count ) return 0;

		meshes[mesh].Name = meshName ?? "";
		return 1;
	}

	public int DropMesh( int mesh )
	{
		if ( mesh < 0 || mesh >= meshes.Count ) return 0;

		meshes.RemoveAt( mesh );
		return 1;
	}

	public string MeshName( int mesh )
		=> mesh >= 0 && mesh < meshes.Count ? meshes[mesh].Name : "";

	public string MeshMaterial( int mesh )
		=> mesh >= 0 && mesh < meshes.Count ? meshes[mesh].Material : "";

	public int SetMeshGroup( int mesh, ulong group )
	{
		if ( mesh < 0 || mesh >= meshes.Count ) return 0;

		meshes[mesh].Group = group;
		return 1;
	}

	public int MeshCount => meshes.Count;

	/// <summary>How many indices a mesh has.</summary>
	public long MeshIndexCount( int mesh ) => mesh >= 0 && mesh < meshes.Count ? meshes[mesh].IndexCount : 0;

	//-- body groups and material groups ------------------------------------

	public int AddPart( string partName )
	{
		parts.Add( new PartEntry { Name = partName ?? "" } );
		return parts.Count - 1;
	}

	public int AddPartChoice( int part, string choiceName, ulong mask )
	{
		if ( part < 0 || part >= parts.Count ) return -1;

		var choices = parts[part].Choices;
		choices.Add( new ChoiceEntry { Name = choiceName ?? "", Mask = mask } );

		return choices.Count - 1;
	}

	public int AddMaterialGroup( string groupName )
	{
		materialGroups.Add( new MaterialGroupEntry { Name = groupName ?? "" } );
		return materialGroups.Count - 1;
	}

	public int SetGroupMaterial( int group, int mesh, string material )
	{
		if ( group < 0 || group >= materialGroups.Count ) return 0;
		if ( mesh < 0 ) return 0;

		var materials = materialGroups[group].Materials;

		//Grown to fit rather than refused: a group that only overrides the third mesh should not have to say something about the first two.
		while ( materials.Count <= mesh ) materials.Add( "" );

		materials[mesh] = material ?? "";
		return 1;
	}

	public void SetDefaultMask( ulong mask ) => defaultMask = mask;

	//-- collision ----------------------------------------------------------

	public int AddBody( string bodyName, string boneName, float* transform,
		float mass, float linearDamping, float angularDamping, float gravityScale )
	{
		var body = new BodyEntry
		{
			Name = bodyName ?? "",
			BoneName = boneName ?? "",
			Mass = mass,
			LinearDamping = linearDamping,
			AngularDamping = angularDamping,
			GravityScale = gravityScale,
		};

		if ( transform is not null )
		{
			for ( var i = 0; i < 7; i++ ) body.Transform[i] = transform[i];
		}

		bodies.Add( body );
		return bodies.Count - 1;
	}

	public void SetMassCenter( int body, float* center )
	{
		if ( center is null ) return;
		if ( body < 0 || body >= bodies.Count ) return;

		for ( var i = 0; i < 3; i++ ) bodies[body].MassCenter[i] = center[i];
		bodies[body].OverrideMassCenter = true;
	}

	//Appends a shape to a body and hands it back to be filled in. Every Add* below is this plus its own geometry.
	private ShapeEntry AddShape( int body, ModelFormat.ShapeKind kind, string surface )
	{
		if ( body < 0 || body >= bodies.Count ) return null;

		var shape = new ShapeEntry { Kind = kind, Surface = surface ?? "" };

		bodies[body].Shapes.Add( shape );
		return shape;
	}

	public void AddSphere( int body, string surface, float* center, float radius )
	{
		var shape = AddShape( body, ModelFormat.ShapeKind.Sphere, surface );
		if ( shape is null ) return;

		if ( center is not null )
		{
			for ( var i = 0; i < 3; i++ ) shape.Primitive[i] = center[i];
		}

		shape.Primitive[3] = radius;
	}

	public void AddCapsule( int body, string surface, float* centerA, float* centerB, float radius )
	{
		var shape = AddShape( body, ModelFormat.ShapeKind.Capsule, surface );
		if ( shape is null ) return;

		if ( centerA is not null ) for ( var i = 0; i < 3; i++ ) shape.Primitive[i] = centerA[i];
		if ( centerB is not null ) for ( var i = 0; i < 3; i++ ) shape.Primitive[3 + i] = centerB[i];

		shape.Primitive[6] = radius;
	}

	public void AddHull( int body, string surface, float* points, int pointCount )
	{
		if ( points is null || pointCount < 4 ) return;

		var shape = AddShape( body, ModelFormat.ShapeKind.Hull, surface );
		if ( shape is null ) return;

		for ( var i = 0; i < pointCount * 3; i++ ) shape.Points.Add( points[i] );
	}

	public void AddCollisionMesh( int body, string surface,
		float* points, int pointCount, uint* collisionIndices, int indexCount )
	{
		if ( points is null || pointCount < 3 ) return;
		if ( collisionIndices is null || indexCount < 3 ) return;

		var shape = AddShape( body, ModelFormat.ShapeKind.Mesh, surface );
		if ( shape is null ) return;

		for ( var i = 0; i < pointCount * 3; i++ ) shape.Points.Add( points[i] );
		for ( var i = 0; i < indexCount; i++ ) shape.Indices.Add( collisionIndices[i] );
	}

	//Kind is 1 for the mesh itself and 0 for a hull around it, which is what SBC_MESH_COLLISION_MESH and _HULL were.
	/// <summary>Collision built out of the render geometry the builder holds.</summary>
	public int CollisionFromMesh( int kind, string surface, float mass )
	{
		if ( vertices.Count == 0 ) return 0;
		if ( kind == MeshCollisionMesh && indices.Count == 0 ) return 0;

		//The positions, without the rest of the vertex. Collision has no use for normals or texture coordinates and carrying them would triple the file.
		var points = new float[vertices.Count * 3];

		for ( var i = 0; i < vertices.Count; i++ )
		{
			var position = vertices[i].Position;

			points[( i * 3 ) + 0] = position.x;
			points[( i * 3 ) + 1] = position.y;
			points[( i * 3 ) + 2] = position.z;
		}

		var identity = stackalloc float[7] { 0, 0, 0, 0, 0, 0, 1 };
		var body = AddBody( "collision", "", identity, mass, 0.0f, 0.0f, 1.0f );
		if ( body < 0 ) return 0;

		fixed ( float* p = points )
		{
			if ( kind == MeshCollisionMesh )
			{
				var flat = CollectionsMarshal.AsSpan( indices );

				fixed ( uint* i = flat )
					AddCollisionMesh( body, surface, p, points.Length / 3, i, flat.Length );
			}
			else
			{
				//Every point of the model, which Box3D simplifies down to a hull it can use when the shape is
				//made. Choosing which points to keep is its job and it does it better than picking some here would.
				AddHull( body, surface, p, points.Length / 3 );
			}
		}

		return 1;
	}

	//What SBC_MESH_COLLISION_MESH was: the triangles rather than a hull.
	private const int MeshCollisionMesh = 1;

	//-- the skeleton and what hangs off it ---------------------------------

	//Bone for unweighted vertices; skin padding uses it so arrays stay one entry per vertex
	private int rigidBone = NoBone;

	/// <summary>What <see cref="SetRigidBone"/> is told for vertices that hang off no bone at all, and stay where they are whatever the skeleton does.</summary>
	public const int NoBone = -1;

	/// <summary>The bone a model's still vertices are bound to, where it has any - see <see cref="BindStillVertices"/>.</summary>
	public const string StillBoneName = "static";

	/// <summary>Bone that subsequently added unweighted vertices follow rigidly, e.g. bolt-on parts</summary>
	public void SetRigidBone( int bone ) => rigidBone = bone >= 0 ? bone : NoBone;

	/// <summary>Gives every vertex that has no weights yet the bone <see cref="SetRigidBone"/> named, so the next part cannot inherit them.</summary>
	public void FillSkin() => PadSkin();

	private void PadSkin() => PadSkinTo( vertices.Count );

	private void PadSkinTo( int count )
	{
		var still = rigidBone < 0 || rigidBone > 255;
		var bone = (byte)( still ? 0 : rigidBone );
		var weight = (byte)( still ? 0 : 255 );

		for ( var have = skin.Count / SkinStride; have < count; have++ )
		{
			skin.Add( bone ); skin.Add( 0 ); skin.Add( 0 ); skin.Add( 0 );
			skin.Add( weight ); skin.Add( 0 ); skin.Add( 0 ); skin.Add( 0 );
		}
	}

	//Binds all-zero-weight vertices to an added static bone so they stay put while the rig moves
	private void BindStillVertices()
	{
		if ( !weighted ) return;

		PadSkin();

		var still = -1;

		for ( var at = 0; at + SkinStride <= skin.Count; at += SkinStride )
		{
			if ( ( skin[at + 4] | skin[at + 5] | skin[at + 6] | skin[at + 7] ) != 0 ) continue;

			if ( still < 0 ) still = StillBone();

			skin[at] = (byte)still; skin[at + 1] = 0; skin[at + 2] = 0; skin[at + 3] = 0;
			skin[at + 4] = 255;
		}
	}

	//The bone nothing poses, added if the model has none yet.
	private int StillBone()
	{
		var name = StillBoneName;

		//The art's own bone of that name is the art's, and may well move.
		for ( var n = 2; FindBone( name ) >= 0; n++ ) name = $"{StillBoneName}{n}";

		var bone = AddBone( name, -1, null );

		if ( bone > 255 )
		{
			log.Warning( $"model has {bones.Count} bones - more than a vertex can name - so its still vertices follow bone 0" );
			return 0;
		}

		return bone;
	}

	public int AddSkin( byte* boneIndices, byte* weights, int count )
	{
		if ( boneIndices is null || weights is null || count <= 0 ) return -1;

		weighted = true;

		//A run's vertices go in before its skin, so everything before this run is already accounted for and only what came before that can be short.
		var before = vertices.Count > count ? vertices.Count - count : 0;

		PadSkinTo( before );

		var at = skin.Count / SkinStride;

		for ( var i = 0; i < count; i++ )
		{
			for ( var k = 0; k < 4; k++ ) skin.Add( boneIndices[( i * 4 ) + k] );
			for ( var k = 0; k < 4; k++ ) skin.Add( weights[( i * 4 ) + k] );
		}

		return at;
	}

	public int AddMorph( string morphName, int* movedVertices, float* positions, float* normals, int count )
	{
		if ( morphName is null || movedVertices is null || positions is null ) return -1;
		if ( count <= 0 ) return -1;

		//One shape, however many runs of vertices it arrives in.
		var at = -1;

		for ( var i = 0; i < morphs.Count; i++ )
		{
			if ( morphs[i].Name != morphName ) continue;

			at = i;
			break;
		}

		if ( at < 0 )
		{
			at = morphs.Count;
			morphs.Add( new MorphEntry { Name = morphName } );
		}

		var morph = morphs[at];

		//Out of the loop: a stackalloc holds until the method returns
		var position = stackalloc float[3];
		var normal = stackalloc float[3];

		for ( var i = 0; i < count; i++ )
		{
			var vertex = movedVertices[i];
			if ( vertex < 0 || vertex >= vertices.Count ) continue;

			morph.Vertices.Add( (uint)vertex );

			position[0] = positions[i * 3];
			position[1] = positions[( i * 3 ) + 1];
			position[2] = positions[( i * 3 ) + 2];
			normal[0] = normal[1] = normal[2] = 0;

			if ( normals is not null )
			{
				normal[0] = normals[i * 3];
				normal[1] = normals[( i * 3 ) + 1];
				normal[2] = normals[( i * 3 ) + 2];
			}

			//Turned with the run it belongs to, and not moved with it: a delta is a direction, so the
			//placement's rotation applies and its offset does not - the same rule the vertex normals go by.
			if ( hasPlacement )
			{
				Turn( placementRotation, position );
				Turn( placementRotation, normal );
			}

			for ( var axis = 0; axis < 3; axis++ )
			{
				morph.Positions.Add( position[axis] );
				morph.Normals.Add( normal[axis] );
			}
		}

		return at;
	}

	public int RemoveMorph( string morphName )
	{
		if ( morphName is null ) return 0;

		for ( var i = 0; i < morphs.Count; i++ )
		{
			if ( morphs[i].Name != morphName ) continue;

			//Erased rather than flagged: the morph index a delta carries is the place in this list, and it is worked out when the file is written.
			morphs.RemoveAt( i );
			return 1;
		}

		return 0;
	}

	public int MorphCount => morphs.Count;

	public string MorphName( int morph )
		=> morph >= 0 && morph < morphs.Count ? morphs[morph].Name : "";

	public int AddBone( string boneName, int parent, float* inverseBind )
	{
		var entry = new BoneEntry { Name = boneName ?? "", Parent = parent };

		if ( inverseBind is not null )
		{
			for ( var i = 0; i < 16; i++ ) entry.InverseBind[i] = inverseBind[i];
		}
		else
		{
			//Identity, so a bone with no bind pose given moves its vertices by its world transform alone rather than by nonsense.
			for ( var i = 0; i < 16; i++ ) entry.InverseBind[i] = ( i % 5 ) == 0 ? 1.0f : 0.0f;
		}

		bones.Add( entry );
		return bones.Count - 1;
	}

	public int FindBone( string boneName )
	{
		if ( boneName is null ) return -1;

		//Case-insensitively, because a rig is matched by name across files nobody exported together and a capital letter is not a different bone.
		for ( var i = 0; i < bones.Count; i++ )
		{
			if ( string.Equals( bones[i].Name, boneName, StringComparison.OrdinalIgnoreCase ) ) return i;
		}

		return -1;
	}

	//What sbc_model_builder_destroy was. Nothing has to call it - the collector will take a builder nobody holds - but a compile gathers millions of vertices and the callers all knew where they were finished with one, so the point is kept.
	/// <summary>Lets go of everything the builder was gathering.</summary>
	public void Dispose()
	{
		vertices.Clear();
		indices.Clear();
		skin.Clear();
		weighted = false;
		morphs.Clear();
		bones.Clear();
		meshes.Clear();
		bodies.Clear();
		parts.Clear();
		materialGroups.Clear();
	}

	/// <summary>How many bones the builder is holding.</summary>
	public int BoneCount => bones.Count;

	/// <summary>One bone's name, its parent, and its inverse bind - what a caller placing a part from a bone's rest needs.</summary>
	public string BoneName( int bone ) => bone >= 0 && bone < bones.Count ? bones[bone].Name : "";

	public int BoneParent( int bone ) => bone >= 0 && bone < bones.Count ? bones[bone].Parent : -1;

	public ReadOnlySpan<float> BoneInverseBind( int bone )
		=> bone >= 0 && bone < bones.Count ? bones[bone].InverseBind : ReadOnlySpan<float>.Empty;
}
