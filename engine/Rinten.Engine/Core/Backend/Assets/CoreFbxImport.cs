using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

using Rinten.CoreAnimation;
using Rinten.Native;

namespace Rinten;

//FBX import via ufbx, the main path for ported skeletons and skins; ufbx struct access stays in C
internal static unsafe class CoreFbxImport
{
	private static readonly Logger log = new( "Core" );

	//ufbx converts to right-handed Y-up metres itself, so no axis swizzle may be added here
	private static IntPtr Load( string path )
	{
		ImportReads.File( path );

		var message = stackalloc byte[512];

		var scene = Ufbx.rn_fbx_load( path, message, 512 );

		if ( scene == IntPtr.Zero )
			log.Warning( Text( message, 512 ) );

		return scene;
	}

	private static string Text( byte* buffer, int size )
	{
		var run = new ReadOnlySpan<byte>( buffer, size );
		var end = run.IndexOf( (byte)0 );

		return Encoding.UTF8.GetString( end < 0 ? run : run[..end] );
	}

	private static string NodeName( IntPtr scene, int node )
	{
		var buffer = stackalloc byte[256];

		var length = Ufbx.rn_fbx_node_name( scene, node, buffer, 256 );
		if ( length <= 0 ) return "";
		if ( length < 256 ) return Text( buffer, 256 );

		var wide = new byte[length + 1];

		fixed ( byte* p = wide ) Ufbx.rn_fbx_node_name( scene, node, p, wide.Length );

		return Encoding.UTF8.GetString( wide, 0, length );
	}

	private static string StackName( IntPtr scene, int stack )
	{
		var buffer = stackalloc byte[256];

		var length = Ufbx.rn_fbx_stack_name( scene, stack, buffer, 256 );
		if ( length <= 0 ) return "";
		if ( length < 256 ) return Text( buffer, 256 );

		var wide = new byte[length + 1];

		fixed ( byte* p = wide ) Ufbx.rn_fbx_stack_name( scene, stack, p, wide.Length );

		return Encoding.UTF8.GetString( wide, 0, length );
	}

	private static string ShapeName( IntPtr scene, int node, int shape )
	{
		var buffer = stackalloc byte[256];

		var length = Ufbx.rn_fbx_shape_name( scene, node, shape, buffer, 256 );
		if ( length <= 0 ) return "";
		if ( length < 256 ) return Text( buffer, 256 );

		var wide = new byte[length + 1];

		fixed ( byte* p = wide ) Ufbx.rn_fbx_shape_name( scene, node, shape, p, wide.Length );

		return Encoding.UTF8.GetString( wide, 0, length );
	}

	private static string PartMaterial( IntPtr scene, int node, int part, out int length )
	{
		var buffer = stackalloc byte[256];

		length = Ufbx.rn_fbx_part_material( scene, node, part, buffer, 256 );
		if ( length <= 0 ) return "";
		if ( length < 256 ) return Text( buffer, 256 );

		var wide = new byte[length + 1];

		fixed ( byte* p = wide ) Ufbx.rn_fbx_part_material( scene, node, part, p, wide.Length );

		return Encoding.UTF8.GetString( wide, 0, length );
	}

	//Material numbers from ufbx's PBR set; skips values with a texture, trusts opacity only in (0, 1)
	private static ModelFormat.Surface PartSurface( IntPtr scene, int node, int part )
	{
		var surface = default( ModelFormat.Surface );
		var read = stackalloc float[12];

		int said;

		try
		{
			said = Ufbx.rn_fbx_part_surface( scene, node, part, read );
		}
		catch ( EntryPointNotFoundException )
		{
			return surface;
		}

		bool Said( int slot ) => ( said & ( 1 << slot ) ) != 0 && ( said & ( 1 << ( slot + 8 ) ) ) == 0;

		if ( Said( 0 ) )
		{
			//A factor nobody wrote is one by the time it arrives here, so this is the colour itself wherever only the colour was said.
			var factor = ( said & ( 1 << 9 ) ) == 0 ? read[3] : 1.0f;
			var opacity = Said( 6 ) && read[10] > 0.0f && read[10] < 1.0f ? read[10] : 1.0f;

			surface.SetColour( read[0] * factor, read[1] * factor, read[2] * factor, opacity );
		}

		if ( Said( 2 ) ) surface.SetRoughness( read[4] );
		if ( Said( 3 ) ) surface.SetMetalness( read[5] );
		if ( Said( 4 ) && ( said & ( 1 << 13 ) ) == 0 ) surface.SetEmission( read[6], read[7], read[8], read[9] );

		return surface;
	}

	//The same direction at unit length, or zero where there was none.
	private static Vector3 Unit( Vector3 v )
	{
		var length = MathF.Sqrt( ( v.x * v.x ) + ( v.y * v.y ) + ( v.z * v.z ) );

		return length > 1e-8f ? new Vector3( v.x / length, v.y / length, v.z / length ) : Vector3.Zero;
	}

	private static byte ToByte( float value )
	{
		var clamped = value < 0.0f ? 0.0f : ( value > 1.0f ? 1.0f : value );

		return (byte)( ( clamped * 255.0f ) + 0.5f );
	}

	//-- which nodes are the skeleton ---------------------------------------

	//Marked nodes plus all their ancestors, ordered parents first so a pose resolves in one pass
	private static List<int> OrderedByDepth( IntPtr scene, int count, bool[] keep )
	{
		for ( var i = 0; i < count; i++ )
		{
			if ( !keep[i] ) continue;

			for ( var up = Ufbx.rn_fbx_node_parent( scene, i ); up >= 0; up = Ufbx.rn_fbx_node_parent( scene, up ) )
				keep[up] = true;
		}

		var ordered = new List<int>( count );

		for ( var depth = 0; ; ++depth )
		{
			var added = 0;

			for ( var i = 0; i < count; i++ )
			{
				if ( !keep[i] ) continue;

				var here = 0;
				for ( var up = Ufbx.rn_fbx_node_parent( scene, i ); up >= 0; up = Ufbx.rn_fbx_node_parent( scene, up ) ) ++here;

				if ( here != depth ) continue;

				ordered.Add( i );
				++added;
			}

			if ( added == 0 ) break;
		}

		return ordered;
	}

	//Unskinned meshes a take animates as objects, turned into bones so their clips play
	private static List<int> MovingMeshNodes( IntPtr scene, int nodeCount )
	{
		var moving = new List<int>();

		var stacks = Ufbx.rn_fbx_stack_count( scene );
		if ( stacks <= 0 ) return moving;

		//Each candidate followed by its parent, so its sample is against the parent rather than
		//the world: a mesh carried by something else moving does not move of its own accord.
		var nodes = new List<int>();
		var parents = new List<int>();
		var asked = new List<bool>();

		for ( var node = 0; node < nodeCount; node++ )
		{
			var flags = (Ufbx.NodeFlags)Ufbx.rn_fbx_node_flags( scene, node );
			if ( !( ( flags & Ufbx.NodeFlags.Mesh ) == Ufbx.NodeFlags.Mesh ) ) continue;
			if ( Ufbx.rn_fbx_mesh_deformer( scene, node ) >= 0 ) continue;

			var above = Ufbx.rn_fbx_node_parent( scene, node );

			nodes.Add( node );
			parents.Add( above >= 0 ? nodes.Count : -1 );
			asked.Add( true );

			if ( above >= 0 )
			{
				nodes.Add( above );
				parents.Add( -1 );
				asked.Add( false );
			}
		}

		if ( nodes.Count == 0 ) return moving;

		var rate = Ufbx.rn_fbx_frame_rate( scene );
		var scratch = new byte[Ufbx.rn_fbx_scratch_size( scene )];
		var found = new bool[nodes.Count];

		var nodeArray = nodes.ToArray();
		var parentArray = parents.ToArray();

		for ( var stack = 0; stack < stacks; stack++ )
		{
			var counted = Math.Floor( ( Ufbx.rn_fbx_stack_length( scene, stack ) * rate ) + 0.5 ) + 1.0;
			var frames = (int)Math.Clamp( counted, 1.0, MaxFrames );
			if ( frames < 2 ) continue;

			var trs = new float[nodeArray.Length * frames * AnimClipBuilder.FloatsPerFrame];

			fixed ( int* n = nodeArray, p = parentArray )
			fixed ( byte* work = scratch )
			fixed ( float* out_ = trs )
			{
				if ( Ufbx.rn_fbx_sample( scene, stack, n, p, nodeArray.Length, frames, work, out_ ) == 0 ) continue;
			}

			for ( var b = 0; b < nodeArray.Length; b++ )
			{
				//Only the candidates, not the parents sampled beside them.
				if ( found[b] || !asked[b] ) continue;

				var track = trs.AsSpan( b * frames * AnimClipBuilder.FloatsPerFrame, frames * AnimClipBuilder.FloatsPerFrame );

				for ( var f = 1; f < frames && !found[b]; f++ )
				{
					for ( var k = 0; k < AnimClipBuilder.FloatsPerFrame; k++ )
					{
						var first = track[k];
						var here = track[( f * AnimClipBuilder.FloatsPerFrame ) + k];

						if ( MathF.Abs( here - first ) <= 1e-4f * MathF.Max( 1.0f, MathF.Abs( first ) ) ) continue;

						found[b] = true;
						break;
					}
				}
			}
		}

		for ( var b = 0; b < nodeArray.Length; b++ )
		{
			if ( found[b] ) moving.Add( nodeArray[b] );
		}

		return moving;
	}

	//Blender hangs the rig under a node named Armature. It is not a bone.
	private static bool IsArmatureWrapper( IntPtr scene, int node )
	{
		var flags = (Ufbx.NodeFlags)Ufbx.rn_fbx_node_flags( scene, node );

		if ( ( ( flags & Ufbx.NodeFlags.Root ) == Ufbx.NodeFlags.Root ) || ( ( flags & Ufbx.NodeFlags.Mesh ) == Ufbx.NodeFlags.Mesh ) ) return false;
		if ( ( ( flags & Ufbx.NodeFlags.Bone ) == Ufbx.NodeFlags.Bone ) ) return false;

		var name = NodeName( scene, node );
		if ( name.Length == 0 ) return false;

		if ( string.Equals( name, "Armature", StringComparison.OrdinalIgnoreCase ) ) return true;

		//Blender duplicate suffix: Armature.001
		if ( name.Length <= 9 ) return false;

		var dot = name.LastIndexOf( '.' );
		if ( dot <= 0 ) return false;

		for ( var p = dot + 1; p < name.Length; p++ )
		{
			if ( name[p] < '0' || name[p] > '9' ) return false;
		}

		return string.Equals( name[..dot], "Armature", StringComparison.OrdinalIgnoreCase );
	}

	private static void DropArmatureWrappers( IntPtr scene, List<int> nodes )
		=> nodes.RemoveAll( node => IsArmatureWrapper( scene, node ) );

	//The bones of every skin in the file, gathered once. One skeleton for the whole model even when the file has several
	//skin deformers: they are usually the same rig seen by different meshes, and a model has one skeleton by definition.
	private static List<int> GatherSkeleton( IntPtr scene, int nodeCount )
	{
		var keep = new bool[nodeCount];
		var any = false;

		var deformers = Ufbx.rn_fbx_deformer_count( scene );

		for ( var d = 0; d < deformers; d++ )
		{
			var clusters = Ufbx.rn_fbx_cluster_count( scene, d );

			for ( var c = 0; c < clusters; c++ )
			{
				var node = Ufbx.rn_fbx_cluster_node( scene, d, c );
				if ( node < 0 ) continue;

				keep[node] = true;
				any = true;
			}
		}

		//A rig parented onto the mesh, with no skin clusters - Blender books and props - still has bone attributes. Those are the skeleton.
		if ( !any )
		{
			for ( var i = 0; i < nodeCount; i++ )
			{
				var flags = (Ufbx.NodeFlags)Ufbx.rn_fbx_node_flags( scene, i );
				if ( ( ( flags & Ufbx.NodeFlags.Root ) == Ufbx.NodeFlags.Root ) || !( ( flags & Ufbx.NodeFlags.Bone ) == Ufbx.NodeFlags.Bone ) ) continue;

				keep[i] = true;
				any = true;
			}
		}

		//Some exporters write the rig as plain nodes, no bone attribute.
		if ( !any )
		{
			for ( var i = 0; i < nodeCount; i++ )
			{
				var flags = (Ufbx.NodeFlags)Ufbx.rn_fbx_node_flags( scene, i );
				if ( ( ( flags & Ufbx.NodeFlags.Root ) == Ufbx.NodeFlags.Root ) || ( ( flags & Ufbx.NodeFlags.Attribute ) == Ufbx.NodeFlags.Attribute ) ) continue;

				keep[i] = true;
			}
		}

		//And every mesh the file animates as an object rather than through a rig - see MovingMeshNodes.
		foreach ( var node in MovingMeshNodes( scene, nodeCount ) ) keep[node] = true;

		var ordered = OrderedByDepth( scene, nodeCount, keep );
		DropArmatureWrappers( scene, ordered );

		return ordered;
	}

	//Skeleton nodes parents first: bone attributes, or empty nodes if a file has no bone attributes
	private static List<int> GatherAnimationNodes( IntPtr scene, int nodeCount )
	{
		var keep = new bool[nodeCount];
		var anyBone = false;

		for ( var i = 0; i < nodeCount; i++ )
		{
			var flags = (Ufbx.NodeFlags)Ufbx.rn_fbx_node_flags( scene, i );
			if ( ( ( flags & Ufbx.NodeFlags.Root ) == Ufbx.NodeFlags.Root ) || !( ( flags & Ufbx.NodeFlags.Bone ) == Ufbx.NodeFlags.Bone ) ) continue;

			keep[i] = true;
			anyBone = true;
		}

		if ( !anyBone )
		{
			for ( var i = 0; i < nodeCount; i++ )
			{
				var flags = (Ufbx.NodeFlags)Ufbx.rn_fbx_node_flags( scene, i );
				if ( ( ( flags & Ufbx.NodeFlags.Root ) == Ufbx.NodeFlags.Root ) || ( ( flags & Ufbx.NodeFlags.Attribute ) == Ufbx.NodeFlags.Attribute ) ) continue;

				keep[i] = true;
			}
		}

		//The same meshes the model made bones of, so their tracks land on them.
		foreach ( var node in MovingMeshNodes( scene, nodeCount ) ) keep[node] = true;

		var ordered = OrderedByDepth( scene, nodeCount, keep );
		DropArmatureWrappers( scene, ordered );

		return ordered;
	}

	//-- the model ----------------------------------------------------------

	/// <summary>Reads the meshes, skeleton, skin and blend shapes of an FBX into a builder, and says how many meshes came out. Negative means the file could not be read at all.</summary>
	public static int ImportModel( CoreAssets.ModelBuilder builder, string path, float scale )
	{
		if ( builder is null || path is null ) return -1;

		//Trace, both: the model's own "Compiling" line already says which file is being read, and at info every model
		//compiled was two more lines of it. Flushed all the same, so a crash in the reader with trace on names the file.
		log.Trace( $"FBX {path}" );
		Logging.Flush();

		var scene = Load( path );
		if ( scene == IntPtr.Zero ) return -1;

		try
		{
			var meshes = ReadModel( builder, scene, scale > 0.0f ? scale : 1.0f );
			log.Trace( $"FBX {path}: {meshes} meshes" );
			Logging.Flush();
			return meshes;
		}
		catch ( Exception e )
		{
			log.Warning( e, $"FBX import of {path} threw" );
			return -1;
		}
		finally
		{
			Ufbx.rn_fbx_free( scene );
		}
	}

	//A vertex bound wholly to bone, or - where that is NoBone - to no bone at all: all-zero weights,
	//which the builder binds to a bone that never moves. See ModelBuilder.BindStillVertices.
	private static void Rigid( byte* bones, byte* weights, int bone )
	{
		for ( var k = 0; k < 4; k++ ) { bones[k] = 0; weights[k] = 0; }

		if ( bone < 0 ) return;

		bones[0] = (byte)( bone & 0xFF );
		weights[0] = 255;
	}

	private static int ReadModel( CoreAssets.ModelBuilder builder, IntPtr scene, float unitScale )
	{
		var nodeCount = Ufbx.rn_fbx_node_count( scene );

		var skeleton = GatherSkeleton( scene, nodeCount );

		//Where a node sits in the skeleton, or -1. The C++ walked the list looking for
		//a pointer; a map from the node index says the same thing without the walk.
		var slotOf = new Dictionary<int, int>( skeleton.Count );

		for ( var i = 0; i < skeleton.Count; i++ ) slotOf[skeleton[i]] = i;

		int SlotOf( int node ) => node >= 0 && slotOf.TryGetValue( node, out var found ) ? found : -1;

		//What takes a vertex into each bone's space, from the cluster that binds it. Found before any
		//bone is added, because the builder's bone list is append-only and each bone must go in complete.
		var binding = new (int Deformer, int Cluster)[skeleton.Count];
		Array.Fill( binding, ( -1, -1 ) );

		var deformers = Ufbx.rn_fbx_deformer_count( scene );

		for ( var d = 0; d < deformers; d++ )
		{
			var clusters = Ufbx.rn_fbx_cluster_count( scene, d );

			for ( var c = 0; c < clusters; c++ )
			{
				var bone = SlotOf( Ufbx.rn_fbx_cluster_node( scene, d, c ) );
				if ( bone >= 0 && binding[bone].Deformer < 0 ) binding[bone] = ( d, c );
			}
		}

		//Maps the file's bones onto same-named bones already in the builder, to avoid a duplicate skeleton
		var boneIndex = new int[skeleton.Count];
		Array.Fill( boneIndex, -1 );

		var inverseBind = stackalloc float[16];

		//Up here: a stackalloc in a loop holds until the method returns, megabytes on a big skin
		var bones = stackalloc byte[4];
		var vertexWeights = stackalloc byte[4];

		for ( var b = 0; b < skeleton.Count; b++ )
		{
			var node = skeleton[b];

			var local = SlotOf( Ufbx.rn_fbx_node_parent( scene, node ) );
			var parent = local >= 0 && local < boneIndex.Length ? boneIndex[local] : -1;

			var name = NodeName( scene, node );

			var held = builder.FindBone( name );

			if ( held >= 0 )
			{
				boneIndex[b] = held;
				continue;
			}

			if ( binding[b].Deformer >= 0 )
			{
				//Inverse bind as recorded at bind time, world to bone, so model, skeleton and clips share one space
				Ufbx.rn_fbx_cluster_inverse_bind( scene, binding[b].Deformer, binding[b].Cluster, inverseBind );
			}
			else
			{
				//A bone no cluster mentions - an ancestor whose skin hangs off its children.
				//Nothing recorded where it stood at bind time, so where it rests now stands in.
				Ufbx.rn_fbx_node_world_inverse( scene, node, inverseBind );
			}

			//The matrix comes across in the file's own metres; only its translation is a length.
			inverseBind[12] *= unitScale;
			inverseBind[13] *= unitScale;
			inverseBind[14] *= unitScale;

			boneIndex[b] = builder.AddBone( name, parent, inverseBind );
		}

		//Which bone of the builder one of this file's skeleton slots turned into.
		int BuilderBone( int slot ) => slot >= 0 && slot < boneIndex.Length ? boneIndex[slot] : -1;

		var meshes = 0;

		var vertices = new List<Vertex>();
		var indices = new List<uint>();
		var skinBones = new List<byte>();
		var skinWeights = new List<byte>();

		var morphVertices = new List<int>();
		var morphPositions = new List<float>();
		var morphNormals = new List<float>();

		for ( var node = 0; node < nodeCount; node++ )
		{
			var flags = (Ufbx.NodeFlags)Ufbx.rn_fbx_node_flags( scene, node );
			if ( !( ( flags & Ufbx.NodeFlags.Mesh ) == Ufbx.NodeFlags.Mesh ) ) continue;

			var attributes = (Ufbx.MeshAttributes)Ufbx.rn_fbx_mesh_attributes( scene, node );

			//The skin that deforms this mesh, if any. A mesh with several is unusual and the first is the one that matters.
			var deformer = Ufbx.rn_fbx_mesh_deformer( scene, node );

			//The bone the mesh hangs off, for whatever of it no skin weights: the nearest bone above it - a door
			//parented to its hinge in Blender is a child of the hinge's node here. None is none: it stays still.
			var carrier = CoreAssets.ModelBuilder.NoBone;

			for ( var up = node; up >= 0 && carrier < 0; up = Ufbx.rn_fbx_node_parent( scene, up ) )
				carrier = BuilderBone( SlotOf( up ) );

			var partCount = Ufbx.rn_fbx_part_count( scene, node );

			for ( var part = 0; part < partCount; part++ )
			{
				var triangles = Ufbx.rn_fbx_part_triangles( scene, node, part );
				if ( triangles <= 0 ) continue;

				var corners = triangles * 3;

				var positions = new float[corners * 3];
				var normals = new float[corners * 3];
				var tangents = new float[corners * 3];
				var uv0 = new float[corners * 2];
				var uv1 = new float[corners * 2];
				var colours = new float[corners * 4];
				var sourceVertices = new uint[corners];
				var skinClusters = new int[corners * 4];
				var weights = new float[corners * 4];

				int written;

				fixed ( float* p = positions, n = normals, t = tangents, u0 = uv0, u1 = uv1, c = colours, w = weights )
				fixed ( uint* source = sourceVertices )
				fixed ( int* cluster = skinClusters )
				{
					written = Ufbx.rn_fbx_part_corners( scene, node, part, corners,
						p, n, t, u0, u1, c, source, cluster, w );
				}

				if ( written <= 0 ) continue;
				if ( written > corners ) written = corners;

				vertices.Clear();
				indices.Clear();
				skinBones.Clear();
				skinWeights.Clear();

				for ( var at = 0; at < written; at++ )
				{
					var vertex = default( Vertex );

					vertex.Position = new Vector3( positions[at * 3] * unitScale,
						positions[( at * 3 ) + 1] * unitScale,
						positions[( at * 3 ) + 2] * unitScale );

					vertex.Normal = new Vector3( normals[at * 3], normals[( at * 3 ) + 1], normals[( at * 3 ) + 2] );

					if ( ( ( attributes & Ufbx.MeshAttributes.Tangents ) == Ufbx.MeshAttributes.Tangents ) )
					{
						vertex.Tangent = new Vector4( tangents[at * 3],
							tangents[( at * 3 ) + 1], tangents[( at * 3 ) + 2], -1.0f );
					}

					if ( ( ( attributes & Ufbx.MeshAttributes.TexCoord0 ) == Ufbx.MeshAttributes.TexCoord0 ) )
					{
						//FBX counts V from the bottom, every renderer here from the top.
						vertex.TexCoord0 = new Vector4( uv0[at * 2], 1.0f - uv0[( at * 2 ) + 1], 0.0f, 0.0f );
					}

					//Second UV set, for lightmap unwraps; anything beyond two is ignored
					if ( ( ( attributes & Ufbx.MeshAttributes.TexCoord1 ) == Ufbx.MeshAttributes.TexCoord1 ) )
						vertex.TexCoord1 = new Vector4( uv1[at * 2], 1.0f - uv1[( at * 2 ) + 1], 0.0f, 0.0f );

					//White where the mesh has no colours, which is what the shim hands over for one.
					vertex.Color = new Color32( ToByte( colours[at * 4] ),
						ToByte( colours[( at * 4 ) + 1] ),
						ToByte( colours[( at * 4 ) + 2] ),
						ToByte( colours[( at * 4 ) + 3] ) );

					indices.Add( (uint)vertices.Count );
					vertices.Add( vertex );

					if ( skeleton.Count == 0 ) continue;

					//The four heaviest bones of this vertex, which is what the vertex format holds. ufbx has already sorted each vertex's weights heaviest first.
					for ( var k = 0; k < 4; k++ ) { bones[k] = 0; vertexWeights[k] = 0; }

					if ( deformer >= 0 )
					{
						var taken = 0;
						while ( taken < 4 && skinClusters[( at * 4 ) + taken] >= 0 ) taken++;

						var total = 0.0f;

						for ( var k = 0; k < taken; k++ )
						{
							var bone = BuilderBone( SlotOf( Ufbx.rn_fbx_cluster_node( scene, deformer,
								skinClusters[( at * 4 ) + k] ) ) );

							if ( bone < 0 ) continue;

							bones[k] = (byte)( bone & 0xFF );
							total += weights[( at * 4 ) + k];
						}

						if ( total > 0.0f )
						{
							var sum = 0;
							var largest = 0;

							for ( var k = 0; k < taken; k++ )
							{
								//Renormalised over the four kept: dropping the fifth and beyond would otherwise shrink the vertex towards the origin.
								vertexWeights[k] = ToByte( weights[( at * 4 ) + k] / total );

								sum += vertexWeights[k];
								if ( vertexWeights[k] > vertexWeights[largest] ) largest = k;
							}

							var corrected = vertexWeights[largest] + ( 255 - sum );
							vertexWeights[largest] = (byte)( corrected < 0 ? 0 : ( corrected > 255 ? 255 : corrected ) );
						}
						else
						{
							//Weighted to nothing: it stays where it was modelled, the way it does in the tool - see Rigid below.
							Rigid( bones, vertexWeights, carrier );
						}
					}
					else
					{
						//An unskinned mesh in a rigged file is bound rigidly to the bone that carries it
						Rigid( bones, vertexWeights, carrier );
					}

					for ( var k = 0; k < 4; k++ ) skinBones.Add( bones[k] );
					for ( var k = 0; k < 4; k++ ) skinWeights.Add( vertexWeights[k] );
				}

				if ( vertices.Count == 0 ) continue;

				int firstVertex;

				fixed ( Vertex* v = CollectionsMarshal.AsSpan( vertices ) )
					firstVertex = builder.AddVertices( v, vertices.Count );

				//After the vertices, not before: the builder catches the skin up against the vertex count as runs
				//go in, so a run that adds its skin first would have it counted against the run before this one.
				if ( skeleton.Count > 0 )
				{
					fixed ( byte* b = CollectionsMarshal.AsSpan( skinBones ) )
					fixed ( byte* w = CollectionsMarshal.AsSpan( skinWeights ) )
						builder.AddSkin( b, w, vertices.Count );
				}

				int firstIndex;

				fixed ( uint* i = CollectionsMarshal.AsSpan( indices ) )
					firstIndex = builder.AddIndices( i, indices.Count );

				if ( firstVertex < 0 || firstIndex < 0 ) continue;

				ReadShapes( builder, scene, node, firstVertex, vertices, sourceVertices, written, unitScale,
					morphVertices, morphPositions, morphNormals );

				var material = PartMaterial( scene, node, part, out var materialLength );

				var nodeName = NodeName( scene, node );
				var meshName = nodeName.Length > 0 ? nodeName : "mesh";

				if ( partCount > 1 && materialLength > 0 ) meshName = $"{meshName}.{material}";

				var added = builder.AddMesh( meshName, material, firstIndex, indices.Count, firstVertex, vertices.Count );

				if ( materialLength > 0 ) builder.SetMeshSurface( added, PartSurface( scene, node, part ) );

				++meshes;
			}
		}

		return meshes;
	}

	//The blend shapes a piece of a mesh carries. Walked from the shape's own sparse offsets rather than by asking each vertex what every shape does to
	//it: a face of fifty shapes moves a few hundred vertices between them, and the other way round is fifty binary searches per vertex to be told nothing.
	private static void ReadShapes( CoreAssets.ModelBuilder builder, IntPtr scene, int node, int firstVertex,
		List<Vertex> vertices, uint[] sourceVertices, int written, float unitScale,
		List<int> morphVertices, List<float> morphPositions, List<float> morphNormals )
	{
		var shapes = Ufbx.rn_fbx_shape_count( scene, node );
		if ( shapes == 0 ) return;

		//The mesh vertex each emitted vertex came from. Triangulating splits one mesh vertex into a corner per face that touches it,
		//and a blend shape is written against the vertex - so this is how a shape's sparse offsets find every copy of what they move.
		var emitted = new Dictionary<uint, List<uint>>();

		for ( var v = 0; v < written; v++ )
		{
			if ( !emitted.TryGetValue( sourceVertices[v], out var copies ) )
			{
				copies = new List<uint>();
				emitted[sourceVertices[v]] = copies;
			}

			copies.Add( (uint)v );
		}

		for ( var shape = 0; shape < shapes; shape++ )
		{
			var offsets = Ufbx.rn_fbx_shape_offsets( scene, node, shape );
			if ( offsets == 0 ) continue;

			var name = ShapeName( scene, node, shape );
			if ( name.Length == 0 ) continue;

			morphVertices.Clear();
			morphPositions.Clear();
			morphNormals.Clear();

			var hasNormals = Ufbx.rn_fbx_shape_has_normals( scene, node, shape ) != 0;

			var moved = new uint[offsets];
			var positionOffsets = new float[offsets * 3];
			var normalOffsets = new float[offsets * 3];

			fixed ( uint* m = moved )
			fixed ( float* p = positionOffsets, n = normalOffsets )
				Ufbx.rn_fbx_shape_read( scene, node, shape, offsets, m, p, n );

			for ( var k = 0; k < offsets; k++ )
			{
				if ( !emitted.TryGetValue( moved[k], out var copies ) ) continue;

				//A delta is a displacement, so it goes through the same matrix the positions did but as a direction: where the mesh sits does not move it.
				var offset = new Vector3( positionOffsets[k * 3],
					positionOffsets[( k * 3 ) + 1], positionOffsets[( k * 3 ) + 2] );

				//Normal delta as the difference of two unit normals, not the file's unnormalised offset
				var raw = hasNormals
					? new Vector3( normalOffsets[k * 3], normalOffsets[( k * 3 ) + 1], normalOffsets[( k * 3 ) + 2] )
					: Vector3.Zero;

				foreach ( var local in copies )
				{
					morphVertices.Add( firstVertex + (int)local );

					morphPositions.Add( offset.x * unitScale );
					morphPositions.Add( offset.y * unitScale );
					morphPositions.Add( offset.z * unitScale );

					if ( !hasNormals )
					{
						morphNormals.Add( 0.0f );
						morphNormals.Add( 0.0f );
						morphNormals.Add( 0.0f );

						continue;
					}

					var baseNormal = vertices[(int)local].Normal;

					var turned = Unit( baseNormal + raw );
					var from = Unit( baseNormal );

					morphNormals.Add( turned.x - from.x );
					morphNormals.Add( turned.y - from.y );
					morphNormals.Add( turned.z - from.z );
				}
			}

			if ( morphVertices.Count == 0 ) continue;

			fixed ( int* v = CollectionsMarshal.AsSpan( morphVertices ) )
			fixed ( float* p = CollectionsMarshal.AsSpan( morphPositions ) )
			fixed ( float* n = CollectionsMarshal.AsSpan( morphNormals ) )
				builder.AddMorph( name, v, p, n, morphVertices.Count );
		}
	}

	//-- animation ----------------------------------------------------------

	/// <summary>The anim stacks an FBX carries, in the order the file holds them.</summary>
	public static List<string> ListTakes( string path )
	{
		var names = new List<string>();
		if ( path is null ) return names;

		var scene = Ufbx.rn_fbx_load( path, null, 0 );
		if ( scene == IntPtr.Zero ) return names;

		try
		{
			var count = Ufbx.rn_fbx_stack_count( scene );

			for ( var i = 0; i < count; i++ ) names.Add( StackName( scene, i ) );
		}
		finally
		{
			Ufbx.rn_fbx_free( scene );
		}

		return names;
	}

	/// <summary>Samples one take of an FBX into a clip builder, and says how many frames came out. Negative means the file could not be read at all.</summary>
	public static int ImportClip( AnimClipBuilder builder, string path, float scale, int take )
	{
		if ( builder is null || path is null ) return -1;

		var scene = Load( path );
		if ( scene == IntPtr.Zero ) return -1;

		try
		{
			return ReadClip( builder, scene, scale > 0.0f ? scale : 1.0f, take );
		}
		finally
		{
			Ufbx.rn_fbx_free( scene );
		}
	}

	//Case-insensitively, because a clip list written by hand is not an export's spelling. An empty name is the first take, which is what a definition that says nothing means.
	/// <summary>The same, naming the take rather than numbering it.</summary>
	public static int ImportClipNamed( AnimClipBuilder builder, string path, float scale, string takeName )
	{
		if ( path is null ) return -1;
		if ( string.IsNullOrEmpty( takeName ) ) return ImportClip( builder, path, scale, 0 );

		var index = -1;

		var names = ListTakes( path );

		for ( var i = 0; i < names.Count; i++ )
		{
			if ( !string.Equals( names[i], takeName, StringComparison.OrdinalIgnoreCase ) ) continue;

			index = i;
			break;
		}

		if ( index < 0 ) return 0;

		return ImportClip( builder, path, scale, index );
	}

	//A clip of one frame is a pose, which is a legitimate thing to export. The ceiling is against a
	//file claiming a length nothing could have authored - the allocation below is frames times bones.
	private const double MaxFrames = 100000.0;

	private static int ReadClip( AnimClipBuilder builder, IntPtr scene, float unitScale, int take )
	{
		var stacks = Ufbx.rn_fbx_stack_count( scene );
		if ( stacks == 0 ) return 0;

		var index = take > 0 ? take : 0;
		if ( index >= stacks ) return 0;

		var nodeCount = Ufbx.rn_fbx_node_count( scene );

		var bones = GatherAnimationNodes( scene, nodeCount );
		if ( bones.Count == 0 ) return 0;

		//The file's own timeline. An export at 30 frames a second is resampled to nothing - the frames land
		//where they already are - and one at 24 or 60 keeps its own rate rather than being forced onto ours.
		var rate = Ufbx.rn_fbx_frame_rate( scene );

		var length = Ufbx.rn_fbx_stack_length( scene, index );

		var counted = Math.Floor( ( length * rate ) + 0.5 ) + 1.0;
		if ( counted < 1.0 ) counted = 1.0;
		if ( counted > MaxFrames ) counted = MaxFrames;

		var frameCount = (int)counted;

		//Where each bone's parent sits in this list, or -1. Found by walking up past anything that was not
		//kept, so an intermediate node's motion ends up inside the bone below it rather than being lost.
		var parents = new int[bones.Count];
		Array.Fill( parents, -1 );

		for ( var b = 0; b < bones.Count; b++ )
		{
			for ( var up = Ufbx.rn_fbx_node_parent( scene, bones[b] ); up >= 0;
				up = Ufbx.rn_fbx_node_parent( scene, up ) )
			{
				var found = bones.IndexOf( up );
				if ( found < 0 ) continue;

				parents[b] = found;
				break;
			}
		}

		for ( var b = 0; b < bones.Count; b++ )
		{
			if ( builder.AddBone( NodeName( scene, bones[b] ), parents[b] ) < 0 ) return -1;
		}

		builder.SetFrameRate( (float)rate );

		//Ten floats a frame per bone: position, rotation, scale - the shape the builder takes and the shape a sample comes back in.
		var tracks = new float[bones.Count * frameCount * AnimClipBuilder.FloatsPerFrame];

		var nodes = bones.ToArray();
		var scratch = new byte[Ufbx.rn_fbx_scratch_size( scene )];

		fixed ( int* n = nodes, p = parents )
		fixed ( byte* work = scratch )
		fixed ( float* out_ = tracks )
		{
			if ( Ufbx.rn_fbx_sample( scene, index, n, p, bones.Count, frameCount, work, out_ ) == 0 ) return 0;
		}

		//Positions come across in the file's own metres.
		if ( unitScale != 1.0f )
		{
			for ( var at = 0; at < tracks.Length; at += AnimClipBuilder.FloatsPerFrame )
			{
				tracks[at] *= unitScale;
				tracks[at + 1] *= unitScale;
				tracks[at + 2] *= unitScale;
			}
		}

		for ( var b = 0; b < bones.Count; b++ )
		{
			builder.SetFrames( b, tracks.AsSpan( b * frameCount * AnimClipBuilder.FloatsPerFrame,
				frameCount * AnimClipBuilder.FloatsPerFrame ), frameCount );
		}

		return frameCount;
	}
}
