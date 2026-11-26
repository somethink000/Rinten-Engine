using System;

namespace ResourceTests;

[TestClass]
public class GraphicsDrawCountTests
{
	[TestMethod]
	public void CountsPastTheSpansAreRejected()
	{
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => Graphics.ValidateDrawCounts( 4, 3 ) );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => Graphics.ValidateDrawCounts( -1, 3 ) );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => Graphics.ValidateDrawCounts( 3, 3, 4, 3 ) );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => Graphics.ValidateDrawCounts( 3, 3, -1, 3 ) );
	}

	[TestMethod]
	public void CountsWithinTheSpansAreAccepted()
	{
		Graphics.ValidateDrawCounts( 3, 3 );
		Graphics.ValidateDrawCounts( 2, 3 );
		Graphics.ValidateDrawCounts( 2, 3, 3, 3 );
	}
}
