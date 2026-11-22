namespace UnitTests;

//A collision mesh is drawn from a line list kept on the card: six indices a triangle, into the mesh's own points.
[TestClass]
public class GizmoWireTest
{
	static Rinten.PhysicsGroupDescription.BodyPart.MeshPart Mesh( Vector3[] points, int[] indices )
		=> new( points, indices, null, null );

	[TestMethod]
	public void ATriangleIsItsThreeEdges()
	{
		var mesh = Mesh(
			[new Vector3( 0, 0, 0 ), new Vector3( 1, 0, 0 ), new Vector3( 0, 1, 0 )],
			[0, 1, 2] );

		var (vertices, indices) = Rinten.CoreGizmoWire.LinesOf( mesh.GetVertices(), mesh.GetIndices() );

		Assert.AreEqual( 3, vertices.Length, "one vertex on the card for each point of the mesh" );
		CollectionAssert.AreEqual( new[] { 0, 1, 1, 2, 2, 0 }, indices, "a, b, b, c, c, a" );
	}

	[TestMethod]
	public void EveryTriangleAddsSixIndices()
	{
		var mesh = Mesh(
			[new Vector3( 0, 0, 0 ), new Vector3( 1, 0, 0 ), new Vector3( 0, 1, 0 ), new Vector3( 1, 1, 0 )],
			[0, 1, 2, 2, 1, 3] );

		var (_, indices) = Rinten.CoreGizmoWire.LinesOf( mesh.GetVertices(), mesh.GetIndices() );

		Assert.AreEqual( 12, indices.Length );
	}

	[TestMethod]
	public void ATriangleWithACornerThatIsNotThereIsLeftOut()
	{
		var mesh = Mesh( [new Vector3( 0, 0, 0 ), new Vector3( 1, 0, 0 )], [0, 1, 7] );

		var (_, indices) = Rinten.CoreGizmoWire.LinesOf( mesh.GetVertices(), mesh.GetIndices() );

		Assert.AreEqual( 0, indices.Length, "a line to nowhere is worse than no line" );
	}

	[TestMethod]
	public void VerticesAreWhiteSoTheDrawColoursThem()
	{
		var mesh = Mesh( [new Vector3( 0, 0, 0 )], [0, 0, 0] );

		var (vertices, _) = Rinten.CoreGizmoWire.LinesOf( mesh.GetVertices(), mesh.GetIndices() );

		Assert.AreEqual( Color32.White, vertices[0].Color, "the gizmo's colour is a tint over the buffer, not baked into it" );
	}

	[TestMethod]
	public void AMeshWithNoTrianglesGivesNothing()
	{
		var mesh = Mesh( [new Vector3( 0, 0, 0 )], [] );

		var (vertices, indices) = Rinten.CoreGizmoWire.LinesOf( mesh.GetVertices(), mesh.GetIndices() );

		Assert.AreEqual( 0, vertices.Length );
		Assert.AreEqual( 0, indices.Length );
	}
}
