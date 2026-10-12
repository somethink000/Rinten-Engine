using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rinten;

//On-disk compiled model: flat, offset-based structs read in place; sizes are pinned by Verify
internal static unsafe class ModelFormat
{
	/// <summary>"ESSM", little-endian.</summary>
	public const uint Magic = 0x4D535345;

	/// <summary>Bumped whenever the layout below changes in a way an older reader would misread. The reader refuses anything it does not recognise rather than reading garbage into the right-shaped structs.</summary>
	public const uint Version = 9;

	/// <summary>The first version that carries clusters; an older file is read and its clusters are cut on load until the editor rebuilds it.</summary>
	public const uint VersionClusters = 6;

	/// <summary>The first version whose vertices are <see cref="PackedVertex"/> - fifty-two bytes, the normal and the tangent one word - rather than the seventy-six of <see cref="Vertex"/>. An older file is read as it was written and packed on its way to the card, until the editor rebuilds it.</summary>
	public const uint VersionPackedVertices = 7;

	/// <summary>The first version that can carry an impostor - see <see cref="Impostor"/>. An older file's header stops before it and is read as having none.</summary>
	public const uint VersionImpostor = 8;

	/// <summary>The first version whose clusters are a chain's - five float4s, without the two spheres a DAG cut each cluster by. An older file's clusters are not read: it is cut on load, level nought alone, until the editor rebuilds it.</summary>
	public const uint VersionChains = 9;

	/// <summary>The oldest version the reader still takes.</summary>
	public const uint VersionOldest = 5;

	//Bumped when importers change their output, so the editor can rebuild models built by older ones
	public const uint ImportGeneration = 67;   //67: islands within a cone, so a rounded model does not fold

	/// <summary>The first generation whose header carries <see cref="Header.surfaces"/>.</summary>
	public const uint GenerationSurfaces = 31;

	/// <summary>A run of bytes inside the file.</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct Block
	{
		public uint offset;
		public uint count;
	}

	/// <summary>Up to four bones per vertex, byte weights summing to 255; parallel to the vertex array</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct SkinVertex
	{
		public fixed byte bones[4];
		public fixed byte weights[4];
	}

	/// <summary>One blend shape the mesh can be moved towards, by name.</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct Morph
	{
		public uint name;
		public uint padding;
	}

	/// <summary>What one morph does to one vertex, in the space the vertices are in.</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct MorphDelta
	{
		public uint morph;
		public fixed float position[3];
		public fixed float normal[3];
	}

	/// <summary>Which deltas belong to a vertex: where its run starts and how long it is. Parallel to the vertex array for the same reason the skin is.</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct MorphRun
	{
		public uint first;
		public uint count;
	}

	/// <summary>One cluster of a level of a model's chain, as the card reads it: five float4s.</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct Cluster
	{
		public fixed float centre[3];
		public float radius;
		public fixed float coneAxis[3];
		public float coneCutoff;
		public fixed float boxMin[3];
		public float error;
		public fixed float boxMax[3];
		public float parentError;

		/// <summary>Into the cluster vertex block, in vertices.</summary>
		public uint vertexOffset;

		/// <summary>Into the cluster triangle block, in triangles (three bytes each).</summary>
		public uint triangleOffset;

		/// <summary>Vertex count in the low sixteen bits, triangle count above.</summary>
		public uint counts;

		/// <summary>Which mesh the cluster belongs to in the low sixteen, its level above.</summary>
		public uint partLevel;
	}

	/// <summary>One joint of the skeleton.</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct Bone
	{
		public uint name;

		/// <summary>Index into this same array, or -1 for a root.</summary>
		public int parent;

		/// <summary>Model-to-bone at rest, row-major, row-vector convention (v * M), translation in the last row</summary>
		public fixed float inverseBind[16];
	}

	/// <summary>One draw call: a slice of the shared index buffer and what to draw it with. Vertices are indexed from <c>firstVertex</c>, so an index fits in 16 or 32 bits regardless of how large the whole model is.</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct Mesh
	{
		public uint name;
		public uint material;

		public uint firstIndex;
		public uint indexCount;
		public uint firstVertex;
		public uint vertexCount;

		/// <summary>min xyz, max xyz</summary>
		public fixed float bounds[6];

		/// <summary>Which body-group choices this mesh belongs to, one bit each. Zero means it is not part of any of them and is always drawn.</summary>
		public ulong group;

		/// <summary>What kind of mesh it is - <see cref="MeshFlags"/>. Once the authored levels of detail, nought in every file from before, which is a mesh drawn with the model like any other.</summary>
		public uint reserved;
		public uint reservedPadding;
	}

	/// <summary>What a mesh is, in <see cref="Mesh.reserved"/>.</summary>
	[Flags]
	public enum MeshFlags : uint
	{
		None = 0,

		/// <summary>The model's impostor: drawn only where the card picks it as the model's level, in place of the rest - never beside it. A draw that takes every mesh of a model whole, with no clusters to choose by, leaves it out.</summary>
		StandsIn = 1,
	}

	/// <summary>Model seen from the six box sides, packed into colour and normal/roughness sheets; drawn as quads</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct Impostor
	{
		/// <summary>Texels across the model's sphere - a texel is its diameter over this.</summary>
		public uint texels;

		/// <summary>The middle of the model's box, in the model's space.</summary>
		public fixed float centre[3];

		/// <summary>The radius of the sphere round <see cref="centre"/> the model is inside - its cluster's bounds.</summary>
		public float radius;

		/// <summary>Which mesh is the quads it is drawn on.</summary>
		public uint mesh;

		/// <summary>The model's box: half its size along each axis, about <see cref="centre"/>.</summary>
		public fixed float extent[3];

		/// <summary>How far off its quads the model is, in the model's units: of every face, the depth nine in ten of the samples it saw are within of its quad - see ModelBuilder.PlaneDepth.</summary>
		public float spread;
	}


	/// <summary>Which of a <see cref="Surface"/>'s numbers the art actually said.</summary>
	[Flags]
	public enum SurfaceSays : uint
	{
		Nothing = 0,
		Colour = 1,
		Roughness = 2,
		Metalness = 4,
		Emission = 8,

		/// <summary>Whether the mesh uses these values; false once someone picks a material for it</summary>
		Worn = 0x100,
	}

	//Applied as the draw's tint over the mesh's material; linear, straight alpha
	/// <summary>What the art said one mesh's surface is, where it said so in numbers rather than pictures: a colour, how rough, how much a metal, what it emits. One per mesh, beside <see cref="Mesh"/>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct Surface
	{
		public SurfaceSays says;
		public float roughness;
		public float metalness;
		public float emissionStrength;

		public float r, g, b, a;
		public float emissionR, emissionG, emissionB;
		public uint padding;

		public readonly bool Says( SurfaceSays what ) => ( says & what ) != 0;

		/// <summary>Whether the mesh wears any of it - see <see cref="SurfaceSays.Worn"/>.</summary>
		public readonly bool Worn => Says( SurfaceSays.Worn ) && ( says & ~SurfaceSays.Worn ) != 0;

		/// <summary>The colour it said, or white.</summary>
		public readonly Color Colour => Says( SurfaceSays.Colour ) ? new Color( r, g, b, a ) : Color.White;

		public void SetColour( float red, float green, float blue, float alpha )
		{
			r = MathF.Max( red, 0.0f );
			g = MathF.Max( green, 0.0f );
			b = MathF.Max( blue, 0.0f );
			a = Math.Clamp( alpha, 0.0f, 1.0f );
			says |= SurfaceSays.Colour;
		}

		public void SetRoughness( float value )
		{
			roughness = Math.Clamp( value, 0.0f, 1.0f );
			says |= SurfaceSays.Roughness;
		}

		public void SetMetalness( float value )
		{
			metalness = Math.Clamp( value, 0.0f, 1.0f );
			says |= SurfaceSays.Metalness;
		}

		/// <summary>What it emits. Nothing at all is not said, so a surface that glows by nought costs no build that glows.</summary>
		public void SetEmission( float red, float green, float blue, float strength )
		{
			if ( strength <= 0.0f || ( red <= 0.0f && green <= 0.0f && blue <= 0.0f ) ) return;

			emissionR = red;
			emissionG = green;
			emissionB = blue;
			emissionStrength = strength;
			says |= SurfaceSays.Emission;
		}
	}

	/// <summary>One option within a body group - a head, a hat, nothing at all.</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct PartChoice
	{
		public uint name;
		public uint padding;

		/// <summary>The bits this choice turns on. Every choice owns bits of its own, so choosing is setting them and clearing whatever the group owns besides.</summary>
		public ulong mask;
	}

	//Not to be confused with BodyPart, which is a rigid piece of the collision. The names come from two different systems that both got here first; this one is what an artist means by a body group.
	/// <summary>A body group: a set of choices, of which one is picked at a time.</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct RenderPart
	{
		public uint name;
		public uint padding;

		/// <summary>Every bit any of its choices owns, so a chooser can clear the group first.</summary>
		public ulong mask;

		/// <summary>PartChoice</summary>
		public Block choices;
	}

	/// <summary>A named alternative set of materials: one entry per mesh, in mesh order.</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct MaterialGroup
	{
		public uint name;
		public uint padding;

		/// <summary>StringRef, one per mesh</summary>
		public Block materials;
	}

	/// <summary>What a collision shape is.</summary>
	public enum ShapeKind : uint
	{
		Sphere = 0,
		Capsule = 1,
		Hull = 2,
		Mesh = 3,
	}

	/// <summary>One collision shape belonging to a body part. The geometry lives in the shared point and index blocks, so a hull and a mesh are described the same way and only differ in whether the indices are used.</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct Shape
	{
		public ShapeKind kind;
		public uint surface;

		/// <summary>Sphere: centre and radius in [0..3]. Capsule: the two ends in [0..5] and the radius in [6]. Unused by hulls and meshes.</summary>
		public fixed float primitive[7];
		public uint padding;

		/// <summary>Into the collision point block, and the collision index block.</summary>
		public Block points;
		public Block indices;
	}

	/// <summary>One rigid part of the model's collision - a bone's worth of shapes.</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct BodyPart
	{
		public uint name;
		public uint boneName;

		/// <summary>position xyz, rotation xyzw</summary>
		public fixed float transform[7];

		public float mass;
		public float linearDamping;
		public float angularDamping;
		public float gravityScale;

		public uint overrideMassCenter;
		public fixed float massCenter[3];

		/// <summary>Into the shape block.</summary>
		public Block shapes;
	}

	/// <summary>The head of the file. Every block below is an offset and a count, and a count of zero means the block is absent - so a model with no collision costs nothing but the zero.</summary>
	[StructLayout( LayoutKind.Sequential )]
	public struct Header
	{
		public uint magic;
		public uint version;

		/// <summary>Total file size, checked against what was actually read. A truncated file otherwise reads as a valid one with garbage at the end.</summary>
		public uint byteCount;

		/// <summary>Which build of the importers filled this - see <see cref="ImportGeneration"/>. Zero on anything written before the field meant something, which is its own answer: rebuild it.</summary>
		public uint generation;

		public fixed float renderBounds[6];
		public fixed float physicsBounds[6];

		public uint name;

		/// <summary>The model's LOD group as a StringRef, 0 for none; old files read as no group</summary>
		public uint lodGroup;

		public Block text;     //bytes
		public Block vertices; //PackedVertex; Vertex before VersionPackedVertices
		public Block indices;  //uint
		public Block meshes;   //Mesh

		public Block bodyParts;        //BodyPart
		public Block shapes;           //Shape
		public Block collisionPoints;  //float[3]
		public Block collisionIndices; //uint

		//Empty on an unskinned model. When present, skin.count equals vertices.count -
		//a vertex either belongs to the skeleton or the model has no skeleton at all.
		public Block skin;  //SkinVertex
		public Block bones; //Bone

		//Body groups and their options, and the alternative material sets. All three are empty on a model that has none, which is most of them.
		public Block renderParts;    //RenderPart
		public Block partChoices;    //PartChoice
		public Block materialGroups; //MaterialGroup
		public Block groupMaterials; //StringRef

		/// <summary>What is drawn when nobody has chosen anything: the first choice of every group, together.</summary>
		public ulong defaultMask;

		//Per-mesh art surfaces, or empty; trusted only from GenerationSurfaces on
		public Block surfaces; //Surface

		/// <summary>Once where the authored levels of detail switched; kept as bytes so an older file still reads. See clusters.</summary>
		public fixed float reservedDistances[8];

		//The blend shapes, what they move, and which of those moves belong to each vertex. All three are
		//empty together on a model with none, and when they are present morphRuns.count equals vertices.count.
		public Block morphs;      //Morph
		public Block morphDeltas; //MorphDelta
		public Block morphRuns;   //MorphRun

		//The clusters every mesh is cut into, all levels, level nought first within each mesh;
		//the vertices each names (into the vertex block) and its triangles as bytes into those.
		public Block clusters;         //Cluster
		public Block clusterVertices;  //uint
		public Block clusterTriangles; //byte, three a triangle

		//The vertices' positions alone, one float[3] each, in vertex order: what the pass that draws the shape of the world reads.
		public Block positions; //float, three a vertex

		/// <summary>How many levels the model's chain has.</summary>
		public uint clusterLevels;

		/// <summary>What the model says about itself that is one bit - see <see cref="ModelFlags"/>. Nought in every file from before it was kept: four bytes of padding until then.</summary>
		public uint flags;

		//The impostor, where the model has one: one Impostor, and its two pictures as compiled texture
		//files - colour and cover, normal and depth. Nothing in a file from before VersionImpostor.
		public Block impostor;       //Impostor, one
		public Block impostorColour; //bytes, a texture file
		public Block impostorShape;  //bytes, a texture file
	}

	/// <summary>One bit each of what a model says about itself - <see cref="Header.flags"/>.</summary>
	[Flags]
	public enum ModelFlags : uint
	{
		None = 0,

		/// <summary>Its skin follows its bones as dual quaternions - see Rinten.SkinningMethod.</summary>
		DualQuaternion = 1,

		/// <summary>Its levels are a chain, one level of the whole model at a time - see LevelChain.BuildChain - rather than a DAG cut a cluster at a time. Two bits from <see cref="ShadowOffsetShift"/> say how many levels coarser its shadows are.</summary>
		Chain = 2,
	}

	/// <summary>Where in <see cref="Header.flags"/> a chain's shadow level offset is, two bits.</summary>
	public const int ShadowOffsetShift = 8;

	/// <summary>How long the header was before the clusters, which is what a file of that version has.</summary>
	public static readonly int HeaderSizeBeforeClusters = (int)Marshal.OffsetOf<Header>( nameof( Header.clusters ) );

	/// <summary>How long the header was before the impostor.</summary>
	public static readonly int HeaderSizeBeforeImpostor = (int)Marshal.OffsetOf<Header>( nameof( Header.impostor ) );

	public static readonly int HeaderSize = sizeof( Header );
	public static readonly int VertexSize = sizeof( PackedVertex );

	/// <summary>How many bytes a vertex is in a file of this version.</summary>
	public static int VertexSizeOf( uint version ) => version >= VersionPackedVertices ? sizeof( PackedVertex ) : sizeof( Vertex );

	//The static_asserts of model_format.h. The sizes are written to disk, so a struct that came out a different size on this runtime is
	//a file that reads garbage into the right-shaped fields - and better refused here, once, than found in a model a metre to the left.
	static ModelFormat()
	{
		Verify();
	}

	private static void Verify()
	{
		Size( sizeof( Mesh ), 64, nameof( Mesh ), "mesh layout is written to disk" );
		Size( sizeof( Surface ), 48, nameof( Surface ), "surface layout is written to disk" );
		Size( sizeof( PartChoice ), 16, nameof( PartChoice ), "choice layout is written to disk" );
		Size( sizeof( RenderPart ), 24, nameof( RenderPart ), "part layout is written to disk" );
		Size( sizeof( MaterialGroup ), 16, nameof( MaterialGroup ), "material group layout is written to disk" );
		Size( sizeof( Vertex ), 76, nameof( Vertex ), "vertex layout is written to disk before version 7" );
		Size( sizeof( PackedVertex ), PackedVertex.Size, nameof( PackedVertex ), "vertex layout is written to disk and read by the card" );
		Size( (int)Marshal.OffsetOf<PackedVertex>( nameof( PackedVertex.Frame ) ), PackedVertex.FrameOffset, nameof( PackedVertex ) + "." + nameof( PackedVertex.Frame ), "the pipelines and vertex_frame.slang read it there" );
		Size( (int)Marshal.OffsetOf<PackedVertex>( nameof( PackedVertex.TexCoord0 ) ), PackedVertex.TexCoord0Offset, nameof( PackedVertex ) + "." + nameof( PackedVertex.TexCoord0 ), "the pipelines and vertex_frame.slang read it there" );
		Size( (int)Marshal.OffsetOf<PackedVertex>( nameof( PackedVertex.TexCoord1 ) ), PackedVertex.TexCoord1Offset, nameof( PackedVertex ) + "." + nameof( PackedVertex.TexCoord1 ), "the pipelines and vertex_frame.slang read it there" );
		Size( sizeof( SkinVertex ), 8, nameof( SkinVertex ), "skin layout is written to disk" );
		Size( sizeof( Morph ), 8, nameof( Morph ), "morph layout is written to disk" );
		Size( sizeof( MorphDelta ), 28, nameof( MorphDelta ), "morph delta layout is written to disk" );
		Size( sizeof( MorphRun ), 8, nameof( MorphRun ), "morph run layout is written to disk" );
		Size( sizeof( Bone ), 72, nameof( Bone ), "bone layout is written to disk" );
		Size( sizeof( Cluster ), 80, nameof( Cluster ), "cluster layout is written to disk and read as float4s" );
		Size( sizeof( Shape ), 56, nameof( Shape ), "shape layout is written to disk" );
		Size( sizeof( BodyPart ), 76, nameof( BodyPart ), "body layout is written to disk" );
		Check( sizeof( Header ) % 8 == 0, "header must not need padding after it" );

		//And where each field sits, because the size alone does not pin it down. Two readers unpack this blob by hand
		//from bytes - the managed side and LoadMorphDelta in frame.slang - and both are written as these three numbers.
		Size( (int)Marshal.OffsetOf<MorphDelta>( nameof( MorphDelta.morph ) ), 0, nameof( MorphDelta ) + "." + nameof( MorphDelta.morph ), "morph delta layout is written to disk" );
		Size( (int)Marshal.OffsetOf<MorphDelta>( nameof( MorphDelta.position ) ), 4, nameof( MorphDelta ) + "." + nameof( MorphDelta.position ), "morph delta layout is written to disk" );
		Size( (int)Marshal.OffsetOf<MorphDelta>( nameof( MorphDelta.normal ) ), 16, nameof( MorphDelta ) + "." + nameof( MorphDelta.normal ), "morph delta layout is written to disk" );
	}

	private static void Check( bool condition, string message )
	{
		if ( !condition ) throw new InvalidOperationException( $"model format: {message}" );
	}

	//Like above, for an exact value, and reports what it actually was to debug other runtimes' layouts
	private static void Size( int actual, int expected, string what, string message )
	{
		if ( actual != expected )
			throw new InvalidOperationException( $"model format: {what} is {actual} on this runtime and {expected} on disk - {message}" );
	}

	/// <summary>Rewrites an existing model's LOD group in place, touching only fields every header version has</summary>
	public static byte[] WithLodGroup( byte[] model, string group )
	{
		group = group?.Trim() ?? "";

		if ( model is null || model.Length < HeaderSizeBeforeClusters ) return model;
		if ( Word( model, nameof( Header.magic ) ) != Magic ) return model;

		var textAt = (int)Marshal.OffsetOf<Header>( nameof( Header.text ) );
		var textOffset = Read( model, textAt );
		var textCount = Read( model, textAt + 4 );
		if ( (long)textOffset + textCount > model.Length ) return model;

		var text = model.AsSpan( (int)textOffset, (int)textCount );
		var wanted = System.Text.Encoding.UTF8.GetBytes( group );

		//Already this group, or none and asked for none.
		var current = Word( model, nameof( Header.lodGroup ) );
		if ( current < textCount )
		{
			var run = text[(int)current..];
			var end = run.IndexOf( (byte)0 );
			if ( ( end < 0 ? run : run[..end] ).SequenceEqual( wanted ) ) return model;
		}

		if ( wanted.Length == 0 )
		{
			SetWord( model, nameof( Header.lodGroup ), 0 );
			return model;
		}

		//The name may be in the text already - as a whole string, not the end of a longer one.
		Span<byte> terminated = stackalloc byte[wanted.Length + 1];
		wanted.CopyTo( terminated );
		terminated[^1] = 0;

		for ( var from = 0; from < text.Length; )
		{
			var found = text[from..].IndexOf( terminated );
			if ( found < 0 ) break;

			var at = from + found;
			if ( at == 0 || text[at - 1] == 0 )
			{
				SetWord( model, nameof( Header.lodGroup ), (uint)at );
				return model;
			}

			from = at + 1;
		}

		var grown = new byte[model.Length + text.Length + terminated.Length];
		model.CopyTo( grown, 0 );
		text.CopyTo( grown.AsSpan( model.Length ) );
		terminated.CopyTo( grown.AsSpan( model.Length + text.Length ) );

		Write( grown, textAt, (uint)model.Length );
		Write( grown, textAt + 4, (uint)( text.Length + terminated.Length ) );
		SetWord( grown, nameof( Header.lodGroup ), (uint)text.Length );
		SetWord( grown, nameof( Header.byteCount ), (uint)grown.Length );

		return grown;
	}

	private static uint Word( byte[] bytes, string field ) => Read( bytes, (int)Marshal.OffsetOf<Header>( field ) );

	private static void SetWord( byte[] bytes, string field, uint value ) => Write( bytes, (int)Marshal.OffsetOf<Header>( field ), value );

	private static uint Read( byte[] bytes, int at ) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( bytes.AsSpan( at ) );

	private static void Write( byte[] bytes, int at, uint value ) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian( bytes.AsSpan( at ), value );

	/// <summary>Reads a struct out of a byte span at an offset, unaligned.</summary>
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public static ref readonly T At<T>( ReadOnlySpan<byte> bytes, uint offset, int index ) where T : unmanaged
		=> ref MemoryMarshal.AsRef<T>( bytes.Slice( (int)offset + ( index * sizeof( T ) ), sizeof( T ) ) );

	/// <summary>A block of the file as a typed span.</summary>
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public static ReadOnlySpan<T> Span<T>( ReadOnlySpan<byte> bytes, in Block block ) where T : unmanaged
		=> block.count == 0
			? ReadOnlySpan<T>.Empty
			: MemoryMarshal.Cast<byte, T>( bytes.Slice( (int)block.offset, (int)block.count * sizeof( T ) ) );
}
