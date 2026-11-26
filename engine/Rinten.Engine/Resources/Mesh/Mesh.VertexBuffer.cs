using NativeEngine;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace NativeEngine
{
	[StructLayout( LayoutKind.Sequential )]
	internal unsafe struct VertexField
	{
		public ColorFormat Format;
		public int Offset;
		public int NameSize;
		public int NameOffset;
		public int SemanticIndex;
	}
}

namespace Rinten
{
	public enum VertexAttributeType
	{
		Position,
		Normal,
		Tangent,
		TexCoord,
		Color,
		BlendIndices,
		BlendWeights,
	}

	public enum VertexAttributeFormat : uint
	{
		Float32,
		Float16,
		SInt32,
		UInt32,
		SInt16,
		UInt16,
		SInt8,
		UInt8,
	}

	public struct VertexAttribute
	{
		public VertexAttribute( VertexAttributeType type, VertexAttributeFormat format, int components = 3, int semanticIndex = 0 )
		{
			Type = type;
			Format = format;
			Components = components;
			SemanticIndex = semanticIndex;
		}

		public VertexAttributeType Type;
		public VertexAttributeFormat Format;
		public int Components;
		public int SemanticIndex;

		internal static readonly ColorFormat[] Float32FormatTable =
		{
			ColorFormat.COLOR_FORMAT_UNKNOWN,
			ColorFormat.COLOR_FORMAT_R32_FLOAT,
			ColorFormat.COLOR_FORMAT_R32G32_FLOAT,
			ColorFormat.COLOR_FORMAT_R32G32B32_FLOAT,
			ColorFormat.COLOR_FORMAT_R32G32B32A32_FLOAT
		};

		internal static readonly ColorFormat[] UInt32FormatTable =
		{
			ColorFormat.COLOR_FORMAT_UNKNOWN,
			ColorFormat.COLOR_FORMAT_R32_UINT,
			ColorFormat.COLOR_FORMAT_R32G32_UINT,
			ColorFormat.COLOR_FORMAT_R32G32B32_UINT,
			ColorFormat.COLOR_FORMAT_R32G32B32A32_UINT
		};

		internal static readonly ColorFormat[] SInt32FormatTable =
		{
			ColorFormat.COLOR_FORMAT_UNKNOWN,
			ColorFormat.COLOR_FORMAT_R32_SINT,
			ColorFormat.COLOR_FORMAT_R32G32_SINT,
			ColorFormat.COLOR_FORMAT_R32G32B32_SINT,
			ColorFormat.COLOR_FORMAT_R32G32B32A32_SINT
		};

		internal static readonly ColorFormat[] UInt8FormatTable =
		{
			ColorFormat.COLOR_FORMAT_UNKNOWN,
			ColorFormat.COLOR_FORMAT_R8_UINT,
			ColorFormat.COLOR_FORMAT_R8G8_UINT,
			ColorFormat.COLOR_FORMAT_UNKNOWN,
			ColorFormat.COLOR_FORMAT_R8G8B8A8_UINT
		};

		internal ColorFormat GetColorFormat()
		{
			if ( Components > 4 || Components == 0 )
				return ColorFormat.COLOR_FORMAT_UNKNOWN;

			if ( (Type == VertexAttributeType.Color || Type == VertexAttributeType.BlendWeights) && Format == VertexAttributeFormat.UInt8 )
			{
				return Components switch
				{
					1 => ColorFormat.COLOR_FORMAT_R8_UNORM,
					2 => ColorFormat.COLOR_FORMAT_R8G8_UNORM,
					3 => ColorFormat.COLOR_FORMAT_UNKNOWN,
					4 => ColorFormat.COLOR_FORMAT_R8G8B8A8_UNORM,
					_ => ColorFormat.COLOR_FORMAT_UNKNOWN,
				};
			}

			switch ( Format )
			{
				case VertexAttributeFormat.Float32: return Float32FormatTable[Components];
				case VertexAttributeFormat.UInt32: return UInt32FormatTable[Components];
				case VertexAttributeFormat.SInt32: return SInt32FormatTable[Components];
				case VertexAttributeFormat.UInt8: return UInt8FormatTable[Components];
				case VertexAttributeFormat.Float16:
					break;
				case VertexAttributeFormat.SInt16:
					break;
				case VertexAttributeFormat.UInt16:
					break;
				case VertexAttributeFormat.SInt8:
					break;
				default: return ColorFormat.COLOR_FORMAT_UNKNOWN;
			}

			return ColorFormat.COLOR_FORMAT_UNKNOWN;
		}
	}

	public partial class Mesh
	{
		/// <summary>Whether this mesh has a vertex buffer.</summary>
		public bool HasVertexBuffer => core.VertexCount > 0;

		/// <summary>Number of vertices this mesh has.</summary>
		public int VertexCount => core.VertexCount;

		private VertexAttribute[] vertexLayout;
		private Type vertexType;

		/// <summary>Create a vertex buffer with a number of vertices</summary>
		public unsafe void CreateVertexBuffer<T>( int vertexCount, Span<T> data = default ) where T : unmanaged
		{
			if ( HasVertexBuffer )
				throw new Exception( "Vertex buffer has already been created" );

			if ( !SandboxedUnsafe.IsAcceptablePod<T>() )
				throw new ArgumentException( $"{nameof( T )} must be a POD type" );

			vertexType = typeof( T );

			if ( vertexCount <= 0 )
				return;

			//Layout from the struct's attributes; without one the model builder can't read the mesh back
			vertexLayout = VertexLayout.Attributes( typeof( T ) );

			Keep( vertexCount, data, vertexLayout );
		}

		/// <summary>Create a vertex buffer with a number of vertices</summary>
		public void CreateVertexBuffer<T>( int vertexCount, List<T> data ) where T : unmanaged
		{
			CreateVertexBuffer( vertexCount, CollectionsMarshal.AsSpan( data ) );
		}

		/// <summary>Create an empty vertex buffer, it can be resized later</summary>
		[Obsolete( $"Use CreateVertexBuffer without {nameof( layout )}, use {nameof( VertexLayout )} attributes on your vertex struct instead" )]
		public void CreateVertexBuffer<T>( VertexAttribute[] layout ) where T : unmanaged
		{
			CreateVertexBuffer( 0, layout, Span<T>.Empty );
		}

		/// <summary>Create a vertex buffer with a number of vertices</summary>
		[Obsolete( $"Use CreateVertexBuffer without {nameof( layout )}, use {nameof( VertexLayout )} attributes on your vertex struct instead" )]
		public void CreateVertexBuffer<T>( int vertexCount, VertexAttribute[] layout, List<T> data ) where T : unmanaged
		{
			CreateVertexBuffer( vertexCount, layout, CollectionsMarshal.AsSpan( data ) );
		}

		/// <summary>Create a vertex buffer with a number of vertices</summary>
		[Obsolete( $"Use CreateVertexBuffer without {nameof( layout )}, use {nameof( VertexLayout )} attributes on your vertex struct instead" )]
		public unsafe void CreateVertexBuffer<T>( int vertexCount, VertexAttribute[] layout, Span<T> data = default ) where T : unmanaged
		{
			if ( HasVertexBuffer )
				throw new Exception( "Vertex buffer has already been created" );

			if ( !SandboxedUnsafe.IsAcceptablePod<T>() )
				throw new ArgumentException( $"{nameof( T )} must be a POD type" );

			vertexLayout = layout;
			vertexType = typeof( T );

			if ( vertexCount <= 0 )
				return;

			Keep( vertexCount, data, layout );
		}

		//Keeps the vertices where the mesh is one of ours, which is where they live until a model is built out of them. A layout of null means the vertices
		//are laid out the way the engine's own Vertex is - which is what the attributes on a vertex struct describe, and what the model format stores.
		private unsafe void Keep<T>( int vertexCount, Span<T> data, VertexAttribute[] layout ) where T : unmanaged
		{

			core.VertexStride = sizeof( T );
			core.VertexCount = vertexCount;
			core.Layout = layout;

			var elementMax = int.MaxValue / core.VertexStride;
			if ( vertexCount > elementMax )
				throw new ArgumentException( $"Too many vertices for a {core.VertexStride}-byte vertex. Maximum is {elementMax}" );

			core.Vertices = new byte[vertexCount * core.VertexStride];

			if ( data.IsEmpty ) return;

			var bytes = MemoryMarshal.AsBytes( data );
			bytes[..Math.Min( bytes.Length, core.Vertices.Length )].CopyTo( core.Vertices );
		}

		/// <summary>Set data of this buffer</summary>
		public void SetVertexBufferData<T>( List<T> data, int elementOffset = 0 ) where T : unmanaged
		{
			SetVertexBufferData( CollectionsMarshal.AsSpan( data ), elementOffset );
		}

		/// <summary>Set data of this buffer</summary>
		public unsafe void SetVertexBufferData<T>( Span<T> data, int elementOffset = 0 ) where T : unmanaged
		{
			if ( core.Vertices is null )
				throw new InvalidOperationException( "Vertex buffer has not been created" );

			var bytes = MemoryMarshal.AsBytes( data );
			var at = (long)elementOffset * sizeof( T );

			if ( at < 0 || at + bytes.Length > core.Vertices.Length )
				throw new ArgumentException( "Setting vertex buffer data out of bounds" );

			bytes.CopyTo( core.Vertices.AsSpan( (int)at ) );
		}

		/// <summary>Resize the vertex buffer</summary>
		public void SetVertexBufferSize( int elementCount )
		{
			if ( elementCount <= 0 )
				throw new ArgumentException( "Vertex buffer size can't be zero" );

			if ( vertexType is null ) return;
			if ( core.VertexStride <= 0 ) return;

			var elementMax = int.MaxValue / core.VertexStride;
			if ( elementCount > elementMax )
				throw new ArgumentException( $"Too many vertices for a {core.VertexStride}-byte vertex. Maximum is {elementMax}" );

			var resized = new byte[elementCount * core.VertexStride];

			core.Vertices?.AsSpan( 0, Math.Min( core.Vertices.Length, resized.Length ) ).CopyTo( resized );

			core.Vertices = resized;
			core.VertexCount = elementCount;
		}

		public delegate void VertexBufferLockHandler<T>( Span<T> data );

		/// <summary>Lock all the memory in this buffer so you can write to it</summary>
		public void LockVertexBuffer<T>( VertexBufferLockHandler<T> handler ) where T : unmanaged
		{
			LockVertexBuffer( 0, VertexCount, handler );
		}

		/// <summary>Lock a specific amount of the memory in this buffer so you can write to it</summary>
		public void LockVertexBuffer<T>( int elementCount, VertexBufferLockHandler<T> handler ) where T : unmanaged
		{
			LockVertexBuffer( 0, elementCount, handler );
		}

		//Nothing has to be locked: the vertices are ours, in memory, so what the handler is given is the bytes themselves, read as its own type.
		/// <summary>Lock a region of memory in this buffer so you can write to it</summary>
		public unsafe void LockVertexBuffer<T>( int elementOffset, int elementCount, VertexBufferLockHandler<T> handler ) where T : unmanaged
		{
			if ( handler is null || core.Vertices is null ) return;

			var at = (long)elementOffset * sizeof( T );
			var length = (long)elementCount * sizeof( T );

			if ( at < 0 || length < 0 || at + length > core.Vertices.Length ) return;

			handler( MemoryMarshal.Cast<byte, T>( core.Vertices.AsSpan( (int)at, (int)length ) ) );
		}
	}
}
