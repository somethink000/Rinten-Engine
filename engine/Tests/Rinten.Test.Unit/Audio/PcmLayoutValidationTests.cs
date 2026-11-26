using System;

namespace AudioTests;

[TestClass]
public class PcmLayoutValidationTests
{
	static (int Start, int End) Validate( int dataLength, int channels, int bits, int loopStart = -1, int loopEnd = 0 )
	{
		SoundFile.ValidatePcmLayout( dataLength, channels, bits, ref loopStart, ref loopEnd );
		return (loopStart, loopEnd);
	}

	[TestMethod]
	public void ValidLayoutKeepsItsLoop()
	{
		//100 bytes of mono 16-bit is 50 sample frames.
		Assert.AreEqual( (10, 50), Validate( 100, 1, 16, 10, 50 ) );
	}

	[TestMethod]
	public void LoopEndPastTheDataIsCleared()
	{
		Assert.AreEqual( (10, 0), Validate( 100, 1, 16, 10, 500 ) );
	}

	[TestMethod]
	public void LoopStartPastTheLoopIsCleared()
	{
		Assert.AreEqual( (-1, 50), Validate( 100, 1, 16, 60, 50 ) );
	}

	[TestMethod]
	public void ImpossibleLayoutsAreRejected()
	{
		Assert.ThrowsException<ArgumentException>( () => Validate( 100, 0, 16 ) );
		Assert.ThrowsException<ArgumentException>( () => Validate( 100, 256, 16 ) );
		Assert.ThrowsException<ArgumentException>( () => Validate( 100, 1, 7 ) );
		Assert.ThrowsException<ArgumentException>( () => Validate( 101, 2, 16 ) );
		Assert.ThrowsException<ArgumentException>( () => Validate( 0, 1, 16 ) );
	}
}
