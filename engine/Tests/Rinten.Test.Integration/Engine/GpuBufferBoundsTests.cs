using System;

namespace EngineTests;

[TestClass]
public class GpuBufferBoundsTests
{
	//A subclass whose constructor shrinks the element size after the base has sized the buffer.
	class ReinitializedBuffer : GpuBuffer<Matrix>
	{
		public ReinitializedBuffer() : base( 1 ) => Initialize( 1024, 4 );
	}

	[TestMethod]
	public void InitializeTwiceThrows()
	{
		Assert.ThrowsException<InvalidOperationException>( () => new ReinitializedBuffer() );
	}
}
