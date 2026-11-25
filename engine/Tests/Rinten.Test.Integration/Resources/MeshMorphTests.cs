using System;

namespace ResourceTests;

[TestClass]
public class MeshMorphTests
{
	[TestMethod]
	public void AddMorphRejectsTooManyTargets()
	{
		var mesh = new Mesh();
		var deltas = new[] { new MorphDelta( 0, Vector3.Up, Vector3.Zero ) };

		for ( var i = 0; i < Mesh.MaxMorphTargets; i++ )
			mesh.AddMorph( $"morph{i}", deltas );

		Assert.ThrowsException<InvalidOperationException>( () => mesh.AddMorph( "one_too_many", deltas ) );

		//Replacing one that is already there is allowed at the cap.
		mesh.AddMorph( "morph0", deltas );
		Assert.AreEqual( Mesh.MaxMorphTargets, mesh.MorphTargets.Count );
	}

	[TestMethod]
	public void AddMorphRejectsDeltaOutsideTheMesh()
	{
		var mesh = new Mesh();
		mesh.CreateVertexBuffer( 3, new Vertex[3].AsSpan() );

		var deltas = new[] { new MorphDelta( 3, Vector3.Up, Vector3.Zero ) };

		Assert.ThrowsException<ArgumentOutOfRangeException>( () => mesh.AddMorph( "bad", deltas ) );
	}
}
