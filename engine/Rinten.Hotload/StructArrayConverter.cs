using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rinten
{
	internal abstract class StructArrayConverter : IDisposable
	{
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

			Unsafe.CopyBlockUnaligned( ref dstDataRef, ref srcDataRef, checked( (uint)byteCount ) );
		}
	}
}
