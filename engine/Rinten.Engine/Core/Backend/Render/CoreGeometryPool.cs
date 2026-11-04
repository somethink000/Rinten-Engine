using System;
using System.Collections.Generic;

namespace Rinten;

//Shared vertex and index buffers for static meshes; grows by half again and retires old buffers
internal static class CoreGeometryPool
{
	//A pool of fixed-size elements with a free list, and side buffers at the same slots.
	internal sealed class Pool
	{
		public ulong Buffer;
		public readonly int Stride;
		public long Capacity;
		public long Used;

		//The side buffers - what goes beside every element, one offset naming all of it.
		private readonly int[] sideStrides;
		private readonly ulong[] sides;

		//Ranges given back, in order, neighbours merged.
		private readonly List<(long At, long Count)> holes = new();

		public Pool( int stride, long first, params int[] sideStrides )
		{
			Stride = stride;
			Capacity = first;
			this.sideStrides = sideStrides ?? Array.Empty<int>();
			sides = new ulong[this.sideStrides.Length];
		}

		public ulong Side( int i ) => i >= 0 && i < sides.Length ? sides[i] : 0;

		/// <summary>Bytes an element takes here, its sides included.</summary>
		public int ElementBytes
		{
			get
			{
				var bytes = Stride;
				foreach ( var one in sideStrides ) bytes += one;
				return bytes;
			}
		}

		public int SideStride( int i ) => i >= 0 && i < sideStrides.Length ? sideStrides[i] : 0;

		/// <summary>Room for that many elements, or -1 if the device would not give it.</summary>
		public long Take( long count )
		{
			if ( count <= 0 ) return -1;

			for ( var i = 0; i < holes.Count; i++ )
			{
				var (at, room) = holes[i];
				if ( room < count ) continue;

				if ( room == count ) holes.RemoveAt( i );
				else holes[i] = ( at + count, room - count );

				return at;
			}

			if ( !Reach( Used + count ) ) return -1;

			var start = Used;
			Used += count;
			return start;
		}

		public void Give( long at, long count )
		{
			if ( at < 0 || count <= 0 ) return;

			var i = 0;
			while ( i < holes.Count && holes[i].At < at ) i++;

			holes.Insert( i, ( at, count ) );

			if ( i + 1 < holes.Count && holes[i].At + holes[i].Count == holes[i + 1].At )
			{
				holes[i] = ( holes[i].At, holes[i].Count + holes[i + 1].Count );
				holes.RemoveAt( i + 1 );
			}

			if ( i > 0 && holes[i - 1].At + holes[i - 1].Count == holes[i].At )
			{
				holes[i - 1] = ( holes[i - 1].At, holes[i - 1].Count + holes[i].Count );
				holes.RemoveAt( i );
			}

			if ( holes.Count > 0 && holes[^1].At + holes[^1].Count == Used )
			{
				Used = holes[^1].At;
				holes.RemoveAt( holes.Count - 1 );
			}
		}

		/// <summary>Writes elements at a slot, into the main buffer or a side.</summary>
		public bool Write( long at, ReadOnlySpan<byte> bytes, int side = -1 )
		{
			if ( bytes.IsEmpty ) return true;

			var buffer = side < 0 ? Buffer : Side( side );
			var stride = side < 0 ? Stride : SideStride( side );

			return buffer != 0 && CoreRenderDevice.WriteBuffer( buffer, at * stride, bytes );
		}

		/// <summary>The buffer holds at least that many elements afterwards; true if it grew.</summary>
		public bool Reach( long elements )
		{
			if ( Buffer != 0 && elements <= Capacity ) return true;

			//Half again, not double: a doubled pool idled 530 MB of 1.2 GB on a 9.5M-vertex scene
			var wanted = Math.Max( Capacity, 2 );
			while ( wanted < elements ) wanted += wanted / 2;
			wanted = ( wanted + 255 ) & ~255L;

			var made = CoreRenderDevice.ReserveBuffer( wanted * Stride );
			if ( made == 0 ) return false;

			var madeSides = new ulong[sides.Length];

			for ( var i = 0; i < sides.Length; i++ )
			{
				madeSides[i] = CoreRenderDevice.ReserveBuffer( wanted * sideStrides[i] );

				if ( madeSides[i] == 0 )
				{
					CoreRenderDevice.DestroyBuffer( made );
					foreach ( var one in madeSides ) if ( one != 0 ) CoreRenderDevice.DestroyBuffer( one );
					return false;
				}
			}

			if ( Buffer != 0 && Used > 0 )
			{
				var copied = CoreRenderDevice.CopyBuffer( Buffer, 0, made, 0, Used * Stride );

				for ( var i = 0; copied && i < sides.Length; i++ )
					copied = CoreRenderDevice.CopyBuffer( sides[i], 0, madeSides[i], 0, Used * sideStrides[i] );

				if ( !copied )
				{
					CoreRenderDevice.DestroyBuffer( made );
					foreach ( var one in madeSides ) CoreRenderDevice.DestroyBuffer( one );
					return false;
				}
			}

			if ( Buffer != 0 ) CoreRenderDevice.DestroyBuffer( Buffer );
			foreach ( var one in sides ) if ( one != 0 ) CoreRenderDevice.DestroyBuffer( one );

			Buffer = made;
			Array.Copy( madeSides, sides, sides.Length );
			Capacity = wanted;
			Generation++;
			return true;
		}

		/// <summary>Bumped whenever the buffers were remade, so a holder of the handles knows to ask again.</summary>
		public int Generation;

		public void Drop()
		{
			if ( Buffer != 0 ) CoreRenderDevice.DestroyBuffer( Buffer );
			foreach ( var one in sides ) if ( one != 0 ) CoreRenderDevice.DestroyBuffer( one );
			Buffer = 0;
			Array.Clear( sides );
			Used = 0;
			holes.Clear();
			Generation++;
		}
	}

	//Which side of the vertex pool holds what.
	internal const int SkinSide = 0;
	internal const int PositionSide = 1;
	internal const int FrameSide = 2;

	//Packed vertices plus separate skin, position and frame streams, which the pose pass reads and writes
	internal static readonly Pool Vertices = new( System.Runtime.CompilerServices.Unsafe.SizeOf<PackedVertex>(), 1 << 17, 8, 12, 4 );
	internal static readonly Pool Indices = new( sizeof( uint ), 1 << 19 );

	//What one vertex costs in the pool, with everything beside it.
	internal static int VertexBytes => Vertices.Stride + Vertices.SideStride( SkinSide ) +
		Vertices.SideStride( PositionSide ) + Vertices.SideStride( FrameSide );

	//Meshes holding a range, told when the buffer moves.
	private static readonly List<CoreSceneRenderer.SceneMesh> tenants = new();

	//Puts the mesh's vertices, positions and indices in the pools and points the mesh at them. False, with the mesh untouched, if there was no room.
	internal static bool Place( CoreSceneRenderer.SceneMesh mesh, ReadOnlySpan<byte> vertices, ReadOnlySpan<byte> indices,
		ReadOnlySpan<byte> skin = default, ReadOnlySpan<byte> positions = default, ReadOnlySpan<byte> frames = default )
	{
		if ( !CoreNative.Available || CoreRenderDevice.PortableFrame ) return false;

		var vertexCount = vertices.Length / Vertices.Stride;
		var indexCount = indices.Length / Indices.Stride;
		if ( vertexCount <= 0 || indexCount <= 0 ) return false;

		var vertexAt = Vertices.Take( vertexCount );
		if ( vertexAt < 0 ) return false;

		var indexAt = Indices.Take( indexCount );
		if ( indexAt < 0 )
		{
			Vertices.Give( vertexAt, vertexCount );
			return false;
		}

		if ( !Vertices.Write( vertexAt, vertices ) || !Indices.Write( indexAt, indices ) ||
			!Vertices.Write( vertexAt, skin, SkinSide ) || !Vertices.Write( vertexAt, positions, PositionSide ) ||
			!Vertices.Write( vertexAt, frames, FrameSide ) )
		{
			Vertices.Give( vertexAt, vertexCount );
			Indices.Give( indexAt, indexCount );
			return false;
		}

		//A growth above retired the buffer every earlier tenant names.
		foreach ( var tenant in tenants )
		{
			tenant.Vertices = Vertices.Buffer;
			tenant.Indices = Indices.Buffer;
			if ( tenant.Skin != 0 ) tenant.Skin = Vertices.Side( SkinSide );
		}

		mesh.Pooled = true;
		mesh.Vertices = Vertices.Buffer;
		mesh.Indices = Indices.Buffer;
		mesh.Skin = skin.IsEmpty ? 0 : Vertices.Side( SkinSide );
		mesh.PoolVertexAt = vertexAt;
		mesh.PoolVertexCount = vertexCount;
		mesh.PoolIndexAt = indexAt;
		mesh.PoolIndexCount = indexCount;
		tenants.Add( mesh );

		return true;
	}

	internal static void Give( CoreSceneRenderer.SceneMesh mesh )
	{
		if ( mesh is null || !mesh.Pooled ) return;

		Vertices.Give( mesh.PoolVertexAt, mesh.PoolVertexCount );
		Indices.Give( mesh.PoolIndexAt, mesh.PoolIndexCount );
		tenants.Remove( mesh );

		mesh.Pooled = false;
		mesh.Vertices = 0;
		mesh.Indices = 0;
		mesh.Skin = 0;
	}

	internal static void Drop()
	{
		Vertices.Drop();
		Indices.Drop();
		tenants.Clear();
	}
}
