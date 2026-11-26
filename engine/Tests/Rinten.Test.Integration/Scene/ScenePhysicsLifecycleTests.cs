namespace SceneTests;

[TestClass]
public class ScenePhysicsLifecycleTests
{
	/// <summary>
	/// Touching PhysicsWorld on a destroyed scene (e.g. from an in-flight load after Stop) must not
	/// create a native world that nothing will ever delete.
	/// </summary>
	[TestMethod]
	public void DestroyedSceneDoesntCreatePhysicsWorld()
	{
		var scene = new Scene();
		Assert.IsNotNull( scene.PhysicsWorld );

		scene.Destroy();

		var count = PhysicsWorld.All.Count;
		Assert.IsNull( scene.PhysicsWorld );
		Assert.AreEqual( count, PhysicsWorld.All.Count );
	}
}
