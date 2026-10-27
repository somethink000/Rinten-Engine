using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using Rinten.Meshlets;

namespace Rinten.CoreAssets;

//Everything a builder holds, as the bytes of a model file. The same bytes whether they are going to a file or straight back in as a model:
//a model built at runtime is the same model as one compiled and loaded, so it is worth its being literally the same code rather than nearly.
internal sealed unsafe partial class ModelBuilder
{
	//Collects strings into one blob and hands out offsets, sharing repeats. Surface names
	//especially: a model with two hundred shapes usually has three distinct materials between them.
	private sealed class TextBlock
	{
		//Starts with a zero byte, which is what makes offset nought the empty string.
		private readonly List<byte> bytes = new() { 0 };

		private readonly Dictionary<string, uint> seen = new( StringComparer.Ordinal );

		public ReadOnlySpan<byte> Bytes => CollectionsMarshal.AsSpan( bytes );

		public uint Add( string value )
		{
			if ( string.IsNullOrEmpty( value ) ) return 0;

			if ( seen.TryGetValue( value, out var found ) ) return found;

			var at = (uint)bytes.Count;

			bytes.AddRange( Encoding.UTF8.GetBytes( value ) );
			bytes.Add( 0 );

			seen[value] = at;
			return at;
		}
	}

	//Appends a block to the file, aligned, and says where it went.
	private static ModelFormat.Block Append<T>( List<byte> file, ReadOnlySpan<T> data ) where T : unmanaged
	{
		if ( data.Length == 0 ) return default;

		//Eight is enough for everything written here and keeps the reader's casts aligned on every platform we target.
		while ( file.Count % 8 != 0 ) file.Add( 0 );

		var block = new ModelFormat.Block { offset = (uint)file.Count, count = (uint)data.Length };

		file.AddRange( MemoryMarshal.AsBytes( data ) );

		return block;
	}

	//The same for a block already flat in bytes, whose count is measured in something wider - the skin, which is eight bytes a vertex.
	private static ModelFormat.Block AppendRaw( List<byte> file, ReadOnlySpan<byte> data, int count )
	{
		if ( count == 0 ) return default;

		while ( file.Count % 8 != 0 ) file.Add( 0 );

		var block = new ModelFormat.Block { offset = (uint)file.Count, count = (uint)count };

		file.AddRange( data );

		return block;
	}

	private static void BoundsAround( ReadOnlySpan<Vertex> all, Span<float> outBounds )
	{
		if ( all.Length == 0 )
		{
			for ( var i = 0; i < 6; ++i ) outBounds[i] = 0.0f;
			return;
		}

		for ( var axis = 0; axis < 3; ++axis )
		{
			outBounds[axis] = all[0].Position[axis];
			outBounds[axis + 3] = all[0].Position[axis];
		}

		foreach ( ref readonly var vertex in all )
		{
			for ( var axis = 0; axis < 3; ++axis )
			{
				if ( vertex.Position[axis] < outBounds[axis] ) outBounds[axis] = vertex.Position[axis];
				if ( vertex.Position[axis] > outBounds[axis + 3] ) outBounds[axis + 3] = vertex.Position[axis];
			}
		}
	}

	private byte[] BuildBytes()
	{
		//First: everything below reads the skin and the bones, the levels of detail among them.
		BindStillVertices();

		var text = new TextBlock();

		//The header goes in first as zeroes and is written over at the end, once every block below it knows where it landed.
		var file = new List<byte>( ModelFormat.HeaderSize );
		for ( var i = 0; i < ModelFormat.HeaderSize; i++ ) file.Add( 0 );

		var header = default( ModelFormat.Header );
		header.magic = ModelFormat.Magic;
		header.version = ModelFormat.Version;
		header.generation = ModelFormat.ImportGeneration;

		//Strings are resolved before the text block is written, since resolving is what fills it.
		var named = text.Add( name );
		header.lodGroup = text.Add( lodGroup );

		var writtenMeshes = new ModelFormat.Mesh[meshes.Count];
		var writtenSurfaces = new ModelFormat.Surface[meshes.Count];
		var anySurface = false;

		for ( var i = 0; i < meshes.Count; i++ )
		{
			var entry = meshes[i];

			var mesh = default( ModelFormat.Mesh );
			mesh.name = text.Add( entry.Name );
			mesh.material = text.Add( entry.Material );
			mesh.firstIndex = entry.FirstIndex;
			mesh.indexCount = entry.IndexCount;
			mesh.firstVertex = entry.FirstVertex;
			mesh.vertexCount = entry.VertexCount;
			mesh.group = entry.Group;
			mesh.reserved = (uint)entry.Flags;

			writtenMeshes[i] = mesh;
			writtenSurfaces[i] = entry.Surface;

			anySurface |= ( entry.Surface.says & ~ModelFormat.SurfaceSays.Worn ) != 0;
		}

		//Body groups, flattened the same way the collision is: every part points at its run of choices in one shared array.
		var writtenParts = new List<ModelFormat.RenderPart>( parts.Count );
		var writtenChoices = new List<ModelFormat.PartChoice>();

		var mask = defaultMask;
		var workOutDefault = mask == 0;

		foreach ( var entry in parts )
		{
			var part = default( ModelFormat.RenderPart );
			part.name = text.Add( entry.Name );
			part.choices.offset = (uint)writtenChoices.Count;
			part.choices.count = (uint)entry.Choices.Count;

			for ( var i = 0; i < entry.Choices.Count; i++ )
			{
				var choice = default( ModelFormat.PartChoice );
				choice.name = text.Add( entry.Choices[i].Name );
				choice.mask = entry.Choices[i].Mask;

				part.mask |= choice.mask;

				//Nothing chosen is the first option of every group, which is what an artist means by putting it first.
				if ( workOutDefault && i == 0 ) mask |= choice.mask;

				writtenChoices.Add( choice );
			}

			writtenParts.Add( part );
		}

		var writtenGroups = new List<ModelFormat.MaterialGroup>( materialGroups.Count );
		var groupMaterials = new List<uint>();

		foreach ( var entry in materialGroups )
		{
			var group = default( ModelFormat.MaterialGroup );
			group.name = text.Add( entry.Name );
			group.materials.offset = (uint)groupMaterials.Count;
			group.materials.count = (uint)entry.Materials.Count;

			foreach ( var material in entry.Materials ) groupMaterials.Add( text.Add( material ) );

			writtenGroups.Add( group );
		}

		header.defaultMask = mask;

		//Shapes are flattened into one array and each body points at its run, so the reader indexes rather than walks.
		var writtenBodies = new List<ModelFormat.BodyPart>( bodies.Count );
		var writtenShapes = new List<ModelFormat.Shape>();
		var points = new List<float>();
		var collisionIndices = new List<uint>();

		foreach ( var entry in bodies )
		{
			var part = default( ModelFormat.BodyPart );
			part.name = text.Add( entry.Name );
			part.boneName = text.Add( entry.BoneName );

			for ( var i = 0; i < 7; i++ ) part.transform[i] = entry.Transform[i];

			part.mass = entry.Mass;
			part.linearDamping = entry.LinearDamping;
			part.angularDamping = entry.AngularDamping;
			part.gravityScale = entry.GravityScale;
			part.overrideMassCenter = entry.OverrideMassCenter ? 1u : 0u;

			for ( var i = 0; i < 3; i++ ) part.massCenter[i] = entry.MassCenter[i];

			part.shapes.offset = (uint)writtenShapes.Count;
			part.shapes.count = (uint)entry.Shapes.Count;

			foreach ( var source in entry.Shapes )
			{
				var shape = default( ModelFormat.Shape );
				shape.kind = source.Kind;
				shape.surface = text.Add( source.Surface );

				for ( var i = 0; i < 7; i++ ) shape.primitive[i] = source.Primitive[i];

				shape.points.offset = (uint)points.Count;
				shape.points.count = (uint)source.Points.Count;
				points.AddRange( source.Points );

				shape.indices.offset = (uint)collisionIndices.Count;
				shape.indices.count = (uint)source.Indices.Count;
				collisionIndices.AddRange( source.Indices );

				writtenShapes.Add( shape );
			}

			writtenBodies.Add( part );
		}

		header.name = named;

		//Cut clusters before writing vertices, since LOD levels add vertices; morphed models too
		var built = new LevelChain.Build();

		if ( vertices.Count > 0 )
		{
			try
			{
				var cutouts = new LevelChain.Cutout[meshes.Count];
				for ( var i = 0; i < meshes.Count; i++ ) cutouts[i] = meshes[i].Cutout;

				var options = new LevelChain.ChainOptions( meshes.Count ) { Step = chainStep, LockBorder = lockBorder };
				for ( var i = 0; i < meshes.Count; i++ )
				{
					options.GoneAt[i] = meshes[i].GoneAt;
					options.LevelOf[i] = meshes[i].LevelOf;
					options.LevelAt[i] = meshes[i].LevelAt;
				}

				LevelChain.CutClusters( CollectionsMarshal.AsSpan( vertices ), CollectionsMarshal.AsSpan( indices ), writtenMeshes, built, clusterLevels, cutouts, options );

				//A mesh that is another's level is drawn only as that level: its own range of indices
				//goes, so nothing that draws meshes by their ranges draws it at level nought as well.
				for ( var i = 0; i < meshes.Count; i++ )
					if ( meshes[i].LevelOf >= 0 ) writtenMeshes[i].indexCount = 0;

				//And the impostor on top of it all, where the model has one - see ModelBuilder.Impostor.
				LinkImpostor( built );
			}
			catch ( Exception e )
			{
				log.Warning( $"clusters threw - {e.Message}" );
				built = new LevelChain.Build();
			}
		}

		//A copy, not the builder's own list: a builder may be written twice, and the second cut must not find the first one's vertices among its own.
		var ownCount = vertices.Count;
		var written = new Vertex[ownCount + built.Extra.Count];
		CollectionsMarshal.AsSpan( vertices ).CopyTo( written );
		CollectionsMarshal.AsSpan( built.Extra ).CopyTo( written.AsSpan( ownCount ) );

		var all = written.AsSpan();

		if ( hasRenderBounds )
		{
			for ( var i = 0; i < 6; i++ ) header.renderBounds[i] = renderBounds[i];
		}
		else
		{
			Span<float> around = stackalloc float[6];
			BoundsAround( all, around );

			for ( var i = 0; i < 6; i++ ) header.renderBounds[i] = around[i];
		}

		//A model with collision but no render mesh is a normal thing - a physics prop - so its physics bounds are not the render ones by default.
		for ( var i = 0; i < 6; i++ )
			header.physicsBounds[i] = hasPhysicsBounds ? physicsBounds[i] : header.renderBounds[i];

		header.text = Append( file, text.Bytes );
		//Packed as they go in: the normal and the tangent become one word - see PackedVertex. Everything
		//before this, the weld and the chain's simplifier among them, worked on the full floats.
		var packed = new PackedVertex[all.Length];
		PackedVertex.Pack( all, packed );
		header.vertices = Append( file, (ReadOnlySpan<PackedVertex>)packed );
		header.indices = Append( file, (ReadOnlySpan<uint>)CollectionsMarshal.AsSpan( indices ) );
		header.meshes = Append( file, (ReadOnlySpan<ModelFormat.Mesh>)writtenMeshes );

		//One per mesh or none at all: most models are painted, their art says nothing in numbers, and they should not carry a block of nothing.
		if ( anySurface ) header.surfaces = Append( file, (ReadOnlySpan<ModelFormat.Surface>)writtenSurfaces );
		header.bodyParts = Append( file, (ReadOnlySpan<ModelFormat.BodyPart>)CollectionsMarshal.AsSpan( writtenBodies ) );
		header.shapes = Append( file, (ReadOnlySpan<ModelFormat.Shape>)CollectionsMarshal.AsSpan( writtenShapes ) );
		header.collisionPoints = Append( file, (ReadOnlySpan<float>)CollectionsMarshal.AsSpan( points ) );
		header.collisionIndices = Append( file, (ReadOnlySpan<uint>)CollectionsMarshal.AsSpan( collisionIndices ) );

		header.renderParts = Append( file, (ReadOnlySpan<ModelFormat.RenderPart>)CollectionsMarshal.AsSpan( writtenParts ) );
		header.partChoices = Append( file, (ReadOnlySpan<ModelFormat.PartChoice>)CollectionsMarshal.AsSpan( writtenChoices ) );
		header.materialGroups = Append( file, (ReadOnlySpan<ModelFormat.MaterialGroup>)CollectionsMarshal.AsSpan( writtenGroups ) );
		header.groupMaterials = Append( file, (ReadOnlySpan<uint>)CollectionsMarshal.AsSpan( groupMaterials ) );

		//Write the skeleton whenever there is one; unskinned runs are rigid, unweighted art has no skin
		if ( weighted )
		{
			PadSkin();

			if ( skin.Count > vertices.Count * SkinStride ) skin.RemoveRange( vertices.Count * SkinStride,
				skin.Count - ( vertices.Count * SkinStride ) );
		}
		else
		{
			skin.Clear();
		}

		var writtenBones = new ModelFormat.Bone[bones.Count];

		for ( var i = 0; i < bones.Count; i++ )
		{
			var bone = default( ModelFormat.Bone );
			bone.name = text.Add( bones[i].Name );
			bone.parent = bones[i].Parent;

			for ( var k = 0; k < 16; k++ ) bone.inverseBind[k] = bones[i].InverseBind[k];

			writtenBones[i] = bone;
		}

		//Blend shapes, turned round from a list per morph into a run per vertex, which is the order the vertex shader wants them in.
		var writtenMorphs = Array.Empty<ModelFormat.Morph>();
		var deltas = Array.Empty<ModelFormat.MorphDelta>();
		var runs = Array.Empty<ModelFormat.MorphRun>();

		if ( morphs.Count > 0 && vertices.Count > 0 )
		{
			writtenMorphs = new ModelFormat.Morph[morphs.Count];

			for ( var i = 0; i < morphs.Count; i++ ) writtenMorphs[i].name = text.Add( morphs[i].Name );

			//The vertices the levels made, by the vertex each came from: a shape that moves the one moves them the same way.
			var made = new Dictionary<uint, List<uint>>();

			for ( var e = 0; e < built.ExtraFrom.Count; e++ )
			{
				if ( !made.TryGetValue( built.ExtraFrom[e], out var list ) ) made[built.ExtraFrom[e]] = list = new List<uint>();

				list.Add( (uint)( ownCount + e ) );
			}

			//Counted first so every run can be given its place before anything is written into one, which is what keeps a vertex's deltas together.
			runs = new ModelFormat.MorphRun[all.Length];

			foreach ( var source in morphs )
			{
				foreach ( var vertex in source.Vertices )
				{
					if ( vertex >= ownCount ) continue;

					runs[vertex].count++;

					if ( made.TryGetValue( vertex, out var also ) )
						foreach ( var extra in also ) runs[extra].count++;
				}
			}

			var at = 0u;

			for ( var i = 0; i < runs.Length; i++ )
			{
				runs[i].first = at;
				at += runs[i].count;
			}

			deltas = new ModelFormat.MorphDelta[at];

			var filled = new uint[runs.Length];

			for ( var m = 0; m < morphs.Count; m++ )
			{
				var source = morphs[m];

				for ( var i = 0; i < source.Vertices.Count; i++ )
				{
					var vertex = source.Vertices[i];
					if ( vertex >= ownCount ) continue;

					var delta = default( ModelFormat.MorphDelta );

					delta.morph = (uint)m;

					for ( var k = 0; k < 3; k++ )
					{
						delta.position[k] = source.Positions[( i * 3 ) + k];
						delta.normal[k] = source.Normals[( i * 3 ) + k];
					}

					deltas[runs[vertex].first + filled[vertex]++] = delta;

					if ( made.TryGetValue( vertex, out var also ) )
						foreach ( var extra in also ) deltas[runs[extra].first + filled[extra]++] = delta;
				}
			}
		}

		//The text block was appended before these names were added to it, so it goes down again with them included. The first copy is left
		//where it is - a few bytes of a file written once, against the alternative of ordering the whole write around one optional block.
		if ( writtenBones.Length > 0 || writtenMorphs.Length > 0 )
			header.text = Append( file, text.Bytes );

		if ( writtenBones.Length > 0 )
		{
			if ( skin.Count > 0 )
			{
				//The levels' vertices follow the bones of the vertices they came from.
				var writtenSkin = new byte[all.Length * SkinStride];
				var flat = CollectionsMarshal.AsSpan( skin );

				flat.CopyTo( writtenSkin );

				for ( var e = 0; e < built.ExtraFrom.Count; e++ )
					flat.Slice( (int)built.ExtraFrom[e] * SkinStride, SkinStride ).CopyTo( writtenSkin.AsSpan( ( ownCount + e ) * SkinStride ) );

				header.skin = AppendRaw( file, writtenSkin, all.Length );
			}
			header.bones = Append( file, (ReadOnlySpan<ModelFormat.Bone>)writtenBones );
		}

		if ( writtenMorphs.Length > 0 )
		{
			header.morphs = Append( file, (ReadOnlySpan<ModelFormat.Morph>)writtenMorphs );
			header.morphDeltas = Append( file, (ReadOnlySpan<ModelFormat.MorphDelta>)deltas );
			header.morphRuns = Append( file, (ReadOnlySpan<ModelFormat.MorphRun>)runs );
		}

		//The clusters, cut above, and the positions on their own.
		if ( all.Length > 0 )
		{
			var positions = new float[all.Length * 3];

			for ( var v = 0; v < all.Length; v++ )
			{
				positions[( v * 3 ) + 0] = all[v].Position.x;
				positions[( v * 3 ) + 1] = all[v].Position.y;
				positions[( v * 3 ) + 2] = all[v].Position.z;
			}

			header.clusters = Append( file, (ReadOnlySpan<ModelFormat.Cluster>)CollectionsMarshal.AsSpan( built.Clusters ) );
			header.clusterVertices = Append( file, (ReadOnlySpan<uint>)CollectionsMarshal.AsSpan( built.Vertices ) );
			header.clusterTriangles = Append( file, (ReadOnlySpan<byte>)CollectionsMarshal.AsSpan( built.Triangles ) );
			header.positions = Append( file, (ReadOnlySpan<float>)positions );
			header.clusterLevels = built.Levels;
		}

		//The chain's bits are this build's to say, not something a copied model brings.
		header.flags = (uint)Flags & ~( (uint)ModelFormat.ModelFlags.Chain | ( 3u << ModelFormat.ShadowOffsetShift ) );

		//A chain, and how many levels coarser its shadows are - see ModelFormat.ModelFlags.
		if ( built.Clusters.Count > 0 )
			header.flags |= (uint)ModelFormat.ModelFlags.Chain | ( (uint)Math.Clamp( shadowLevelOffset, 0, 3 ) << ModelFormat.ShadowOffsetShift );

		//The impostor, where one was made: what it is, and its two pictures as the texture files the engine reads them from - see ModelBuilder.Impostor.
		if ( impostorColour is not null && impostorShape is not null )
		{
			var info = impostorInfo;

			header.impostor = Append( file, new ReadOnlySpan<ModelFormat.Impostor>( ref info ) );
			header.impostorColour = Append( file, (ReadOnlySpan<byte>)impostorColour );
			header.impostorShape = Append( file, (ReadOnlySpan<byte>)impostorShape );
		}

		header.byteCount = (uint)file.Count;

		var bytes = new byte[file.Count];
		CollectionsMarshal.AsSpan( file ).CopyTo( bytes );

		MemoryMarshal.Write( bytes, in header );

		return bytes;
	}

	//Welds identical corners per mesh; morphed vertices also need identical deltas in every shape
	private void WeldVertices()
	{
		var total = vertices.Count;
		if ( total == 0 || meshes.Count == 0 ) return;

		var skinned = skin.Count == total * SkinStride;

		foreach ( var mesh in meshes )
		{
			if ( (long)mesh.FirstVertex + mesh.VertexCount > total ) return;
			if ( (long)mesh.FirstIndex + mesh.IndexCount > indices.Count ) return;
		}

		//Which shapes move each vertex and by how much, vertex by vertex and shape by shape - what two corners must agree on to be welded.
		var shaped = MorphsByVertex( total );

		//Vertex runs the meshes use; a shared run is welded once, overlapping runs are left alone
		var runs = new List<Run>();

		for ( var m = 0; m < meshes.Count; m++ )
		{
			var mesh = meshes[m];

			Run run = null;
			foreach ( var r in runs ) if ( r.First == mesh.FirstVertex && r.Count == mesh.VertexCount ) run = r;

			if ( run is null )
			{
				run = new Run { First = mesh.FirstVertex, Count = mesh.VertexCount };
				runs.Add( run );
			}

			run.Meshes.Add( m );
		}

		runs.Sort( ( x, y ) => x.First.CompareTo( y.First ) );

		for ( var r = 0; r + 1 < runs.Count; ++r )
		{
			if ( (long)runs[r].First + runs[r].Count > runs[r + 1].First )
			{
				runs[r].Whole = true;
				runs[r + 1].Whole = true;
			}
		}

		var toNew = new uint[total];
		Array.Fill( toNew, uint.MaxValue );

		var made = new List<Vertex>( total );
		var madeSkin = new List<byte>( skinned ? total * SkinStride : 0 );

		var keySize = sizeof( Vertex ) + ( skinned ? SkinStride : 0 );

		var keys = Array.Empty<byte>();
		var remap = Array.Empty<uint>();
		var gathered = new List<uint>();

		var held = CollectionsMarshal.AsSpan( vertices );
		var flatIndices = CollectionsMarshal.AsSpan( indices );
		var flatSkin = CollectionsMarshal.AsSpan( skin );

		foreach ( var run in runs )
		{
			var first = (int)run.First;
			var count = (int)run.Count;
			var newFirst = (uint)made.Count;

			var whole = run.Whole || count == 0;

			var anyShaped = false;
			for ( var k = 0; k < count && !anyShaped; ++k ) anyShaped = shaped.Start[first + k + 1] != shaped.Start[first + k];

			gathered.Clear();

			foreach ( var m in run.Meshes )
			{
				var mesh = meshes[m];

				for ( var i = 0u; i < mesh.IndexCount && !whole; ++i )
				{
					var index = flatIndices[(int)( mesh.FirstIndex + i )];
					if ( index >= count ) whole = true;

					gathered.Add( index );
				}
			}

			if ( whole )
			{
				for ( var k = 0; k < count; ++k )
				{
					toNew[first + k] = newFirst + (uint)k;
					made.Add( held[first + k] );

					if ( skinned )
						madeSkin.AddRange( flatSkin.Slice( ( first + k ) * SkinStride, SkinStride ) );
				}

				foreach ( var m in run.Meshes ) meshes[m].FirstVertex = newFirst;
				continue;
			}

			if ( keys.Length < count * keySize ) keys = new byte[count * keySize];

			for ( var k = 0; k < count; ++k )
			{
				MemoryMarshal.Write( keys.AsSpan( k * keySize ), in held[first + k] );

				if ( skinned )
					flatSkin.Slice( ( first + k ) * SkinStride, SkinStride )
						.CopyTo( keys.AsSpan( ( k * keySize ) + sizeof( Vertex ) ) );
			}

			if ( remap.Length < count ) remap = new uint[count];

			var uniqueCount = anyShaped
				? MeshWeld.Remap( remap, CollectionsMarshal.AsSpan( gathered ), count,
					new ShapedHasher { Keys = keys, Size = keySize, First = first, Shapes = shaped } )
				: MeshWeld.Remap( remap, CollectionsMarshal.AsSpan( gathered ), keys, count, keySize );

			made.EnsureCapacity( (int)newFirst + uniqueCount );
			while ( made.Count < (int)newFirst + uniqueCount ) made.Add( default );

			if ( skinned )
			{
				while ( madeSkin.Count < ( (int)newFirst + uniqueCount ) * SkinStride ) madeSkin.Add( 0 );
			}

			var target = CollectionsMarshal.AsSpan( made );
			var targetSkin = CollectionsMarshal.AsSpan( madeSkin );

			for ( var k = 0; k < count; ++k )
			{
				var to = remap[k];
				if ( to == uint.MaxValue ) continue;

				toNew[first + k] = newFirst + to;
				target[(int)( newFirst + to )] = held[first + k];

				if ( skinned )
					flatSkin.Slice( ( first + k ) * SkinStride, SkinStride )
						.CopyTo( targetSkin.Slice( (int)( newFirst + to ) * SkinStride, SkinStride ) );
			}

			foreach ( var m in run.Meshes )
			{
				var mesh = meshes[m];

				for ( var i = 0u; i < mesh.IndexCount; ++i )
					flatIndices[(int)( mesh.FirstIndex + i )] = remap[flatIndices[(int)( mesh.FirstIndex + i )]];

				mesh.FirstVertex = newFirst;
				mesh.VertexCount = (uint)uniqueCount;
			}
		}

		//Welded copies carried the same delta: the shape keeps one of them.
		var lastShape = new int[made.Count];
		Array.Fill( lastShape, -1 );

		for ( var m = 0; m < morphs.Count; ++m )
		{
			var morph = morphs[m];
			var kept = 0;

			for ( var n = 0; n < morph.Vertices.Count; ++n )
			{
				var v = morph.Vertices[n];
				var to = v < total ? toNew[v] : uint.MaxValue;
				if ( to == uint.MaxValue || lastShape[(int)to] == m ) continue;

				lastShape[(int)to] = m;

				morph.Vertices[kept] = to;

				for ( var k = 0; k < 3; ++k )
				{
					morph.Positions[( kept * 3 ) + k] = morph.Positions[( n * 3 ) + k];
					morph.Normals[( kept * 3 ) + k] = morph.Normals[( n * 3 ) + k];
				}

				++kept;
			}

			morph.Vertices.RemoveRange( kept, morph.Vertices.Count - kept );
			morph.Positions.RemoveRange( kept * 3, morph.Positions.Count - ( kept * 3 ) );
			morph.Normals.RemoveRange( kept * 3, morph.Normals.Count - ( kept * 3 ) );
		}

		vertices.Clear();
		vertices.AddRange( made );

		if ( !skinned ) return;

		skin.Clear();
		skin.AddRange( madeSkin );
	}

	//Every vertex's shapes, in shape order: which shape, and its six numbers (position, then normal).
	//Start is one longer than the vertices, so a vertex's entries are from its start to the next one's.
	private sealed class ShapesByVertex
	{
		public int[] Start;
		public int[] Shape;
		public float[] Delta;
	}

	private ShapesByVertex MorphsByVertex( int total )
	{
		var start = new int[total + 1];

		foreach ( var morph in morphs )
			foreach ( var v in morph.Vertices )
				if ( v < total ) start[v + 1]++;

		for ( var v = 0; v < total; ++v ) start[v + 1] += start[v];

		var fill = (int[])start.Clone();
		var shape = new int[start[total]];
		var delta = new float[start[total] * 6];

		for ( var m = 0; m < morphs.Count; ++m )
		{
			var morph = morphs[m];

			for ( var n = 0; n < morph.Vertices.Count; ++n )
			{
				var v = morph.Vertices[n];
				if ( v >= total ) continue;

				var e = fill[v]++;
				shape[e] = m;

				for ( var k = 0; k < 3; ++k )
				{
					delta[( e * 6 ) + k] = morph.Positions[( n * 3 ) + k];
					delta[( e * 6 ) + 3 + k] = morph.Normals[( n * 3 ) + k];
				}
			}
		}

		return new ShapesByVertex { Start = start, Shape = shape, Delta = delta };
	}

	//Two vertices of a run are one when every byte of them is and every shape moves them alike - see WeldVertices.
	private struct ShapedHasher : MeshHash.IIndexHasher
	{
		public byte[] Keys;
		public int Size;
		public int First;
		public ShapesByVertex Shapes;

		public uint Hash( uint index )
		{
			var hash = MeshHash.Words( new ReadOnlySpan<byte>( Keys, (int)index * Size, Size ) );
			var v = First + (int)index;

			for ( var e = Shapes.Start[v]; e < Shapes.Start[v + 1]; ++e )
			{
				hash = ( hash * 0x9E3779B1u ) ^ (uint)Shapes.Shape[e];
				hash = ( hash * 0x9E3779B1u ) ^ BitConverter.SingleToUInt32Bits( Shapes.Delta[e * 6] );
			}

			return hash;
		}

		public bool Equal( uint a, uint b )
		{
			if ( !new ReadOnlySpan<byte>( Keys, (int)a * Size, Size ).SequenceEqual( new ReadOnlySpan<byte>( Keys, (int)b * Size, Size ) ) )
				return false;

			var va = First + (int)a;
			var vb = First + (int)b;
			var count = Shapes.Start[va + 1] - Shapes.Start[va];

			if ( count != Shapes.Start[vb + 1] - Shapes.Start[vb] ) return false;

			var ea = Shapes.Start[va];
			var eb = Shapes.Start[vb];

			for ( var i = 0; i < count; ++i )
			{
				if ( Shapes.Shape[ea + i] != Shapes.Shape[eb + i] ) return false;

				if ( !MemoryMarshal.AsBytes( Shapes.Delta.AsSpan( ( ea + i ) * 6, 6 ) )
					.SequenceEqual( MemoryMarshal.AsBytes( Shapes.Delta.AsSpan( ( eb + i ) * 6, 6 ) ) ) )
					return false;
			}

			return true;
		}
	}

	//One run of vertices the meshes stand on - see WeldVertices.
	private sealed class Run
	{
		public uint First, Count;
		public readonly List<int> Meshes = new();
		public bool Whole;
	}

	/// <summary>Writes the model to a path. False if nothing could be written.</summary>
	public int Save( string path )
	{
		if ( path is null ) return 0;

		WeldVertices();

		var file = BuildBytes();

		try
		{
			File.WriteAllBytes( path, file );
		}
		catch ( IOException )
		{
			return 0;
		}
		catch ( UnauthorizedAccessException )
		{
			return 0;
		}

		return 1;
	}

	/// <summary>The model this builder describes, with no file in between.</summary>
	public ModelFile Build()
	{
		WeldVertices();

		return ModelFile.LoadOwned( BuildBytes() );
	}

	//What the editor's compiler wants: it writes through the asset system rather than to a path, and used to get here through a temporary file.
	/// <summary>The bytes a <see cref="Save"/> would write, handed back instead.</summary>
	public byte[] ToBytes()
	{
		WeldVertices();

		return BuildBytes();
	}
}
