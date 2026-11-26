using System;

namespace SceneTests;

[TestClass]
public class DynamicSceneObjectTests
{
	[TestMethod]
	public void UndefinedPrimitiveIsRejected()
	{
		var world = new SceneWorld();
		var obj = new SceneDynamicObject( world );

		Assert.ThrowsException<ArgumentOutOfRangeException>( () => obj.Init( (Graphics.PrimitiveType)5000 ) );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => obj.Write( (Graphics.PrimitiveType)5000, 0, 0 ) );
	}

	[TestMethod]
	public void GeometryForDrawCopiesVertices()
	{
		var world = new SceneWorld();
		var obj = new SceneDynamicObject( world );

		obj.AddVertex( new Vertex( new Vector3( 1, 2, 3 ) ) );

		var drawn = obj.GeometryForDraw();
		Assert.AreEqual( 1, drawn.Length );
		Assert.AreEqual( new Vector3( 1, 2, 3 ), drawn[0].Position );

		//A later write does not change what a snapshot already handed out.
		obj.AddVertex( new Vertex( new Vector3( 4, 5, 6 ) ) );
		Assert.AreEqual( new Vector3( 1, 2, 3 ), drawn[0].Position );
	}
}
