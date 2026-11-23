using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rinten
{
	internal abstract class StructArrayConverter : IDisposable
	{
		private delegate void MemmoveDelegate( ref byte dest, ref byte src, nuint len );

		private static MemmoveDelegate MemmoveImpl { get; }

		static StructArrayConverter()
		{
			//cpblk under the hood, so this is the same unaligned block copy the runtime's own memmove does.
			//Reflecting into a private Buffer helper used to work, but .NET 11 turned it into a generic
			//Buffer.Memmove<T> taking an element count, so the old lookup silently found nothing and left
			//MemmoveImpl null - which then blew up as a null reference on the first block copy.
			MemmoveImpl = ( ref byte dest, ref byte src, nuint len ) =>
				Unsafe.CopyBlockUnaligned( ref dest, ref src, (uint)len );
		}

		public static bool CanCopyHigherRankArrays => MemmoveImpl is not null;

		protected static void Memmove( ref byte dst, ref byte src, nuint len )
		{
			MemmoveImpl( ref dst, ref src, len );
		}

		protected int ElementSize { get; }

		public static StructArrayConverter Create( Type srcType, Type dstType )
		{
			var genType = typeof( StructArrayConverter<,> );
			var conType = genType.MakeGenericType( srcType, dstType );
			return (StructArrayConverter)Activator.CreateInstance( conType );
		}

		protected StructArrayConverter( Type srcType, int srcSize, Type dstType, int dstSize )
		{
			if ( srcSize != dstSize )
			{
				throw new Exception( $"Cannot construct a {nameof( StructArrayConverter )} with type arguments of " +
					$"{srcType} and {dstType} - structures are different sizes ({srcSize} vs {dstSize})." );
			}

			ElementSize = srcSize;
		}

		public void BlockCopy( Array src, Array dst, int count )
		{
			OnBlockCopy( src, dst, count );
		}

		protected abstract void OnBlockCopy( Array src, Array dst, int count );

		public void Dispose()
		{
		}
	}

	internal class StructArrayConverter<TSrc, TDst> : StructArrayConverter
		where TSrc : struct
		where TDst : struct
	{
		public StructArrayConverter() : base( typeof( TSrc ), Unsafe.SizeOf<TSrc>(), typeof( TDst ), Unsafe.SizeOf<TDst>() ) { }

		protected override void OnBlockCopy( Array src, Array dst, int count )
		{
			if ( src.Rank != dst.Rank )
			{
				throw new NotImplementedException( "Arrays must have same rank." );
			}

			if ( src.Length < count )
			{
				throw new ArgumentException( "Source array is too small.", nameof( src ) );
			}

			if ( dst.Length < count )
			{
				throw new ArgumentException( "Destination array is too small.", nameof( dst ) );
			}

			var byteCount = (uint)count * (nuint)ElementSize;

			ref var srcDataRef = ref MemoryMarshal.GetArrayDataReference( src );
			ref var dstDataRef = ref MemoryMarshal.GetArrayDataReference( dst );

			Memmove( ref dstDataRef, ref srcDataRef, byteCount );
		}
	}
}
