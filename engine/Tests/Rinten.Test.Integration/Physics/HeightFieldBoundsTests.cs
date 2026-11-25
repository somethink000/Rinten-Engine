using System;

namespace PhysicsTests;

[TestClass]
public class HeightFieldBoundsTests
{
	[TestMethod]
	public void InvalidSizesAreRejected()
	{
		var body = new PhysicsBody( new PhysicsWorld() );

		//65537 * 65537 wraps to 131073 in 32-bit math, so the lengths used to line up by accident.
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => body.AddHeightFieldShape( new ushort[131073], null, 65537, 65537, 1, 1 ) );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => body.AddHeightFieldShape( new ushort[4], null, -2, -2, 1, 1 ) );
	}
}
