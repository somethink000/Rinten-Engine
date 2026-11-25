using Rinten.Navigation.Generation;

namespace NavigationTests;

[TestClass]
public class HeightfieldTest
{
	static Heightfield Make( int width, int height ) => new( width, height, new Vector3( 0, 0, 0 ), new Vector3( 10, 10, 10 ), 1f, 1f );

	static void Fill( Heightfield heightfield, int x, int z, int count )
	{
		for ( var i = 0; i < count; i++ )
			heightfield.AddOrMergeSpan( x, z, (ushort)( i * 4 ), (ushort)( i * 4 + 2 ), Constants.WALKABLE_AREA, 0 );
	}

	[TestMethod]
	public void InitResetsColumnCapacityAfterACrowdedTile()
	{
		using var heightfield = Make( 1, 1 );

		Fill( heightfield, 0, 0, 5 );
		Assert.IsTrue( heightfield.ColumnCapacity > 4, "five spans should have grown the columns" );

		heightfield.Init( 1, 1, new Vector3( 0, 0, 0 ), new Vector3( 10, 10, 10 ), 1f, 1f );
		Assert.AreEqual( 4, heightfield.ColumnCapacity, "a reused heightfield starts with the small column stride again" );

		Fill( heightfield, 0, 0, 4 );
		Assert.AreEqual( 4, heightfield.ColumnCapacity, "four spans fit without growing" );

		heightfield.EnsureCompressed();
		Assert.AreEqual( 4, heightfield.GetColumn( 0 ).Length );
	}

	[TestMethod]
	public void GrowingKeepsEveryColumn()
	{
		using var heightfield = Make( 3, 1 );

		for ( var x = 0; x < 3; x++ )
			Fill( heightfield, x, 0, 5 );

		heightfield.GrowColumns();
		heightfield.EnsureCompressed();

		for ( var x = 0; x < 3; x++ )
			Assert.AreEqual( 5, heightfield.GetColumn( x ).Length, $"column {x} kept its spans" );
	}
}
