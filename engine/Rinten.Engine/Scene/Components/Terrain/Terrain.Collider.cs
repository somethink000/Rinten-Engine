namespace Rinten;

public partial class Terrain
{
	private bool _enableCollision = true;

	[Property, Category( "Physics" )]
	public bool EnableCollision
	{
		get => _enableCollision;
		set
		{
			_enableCollision = value;
			Rebuild();
		}
	}

	//One mesh collision shape per chunk from its baked triangles, so caves and overhangs collide
	protected override IEnumerable<PhysicsShape> CreatePhysicsShapes( PhysicsBody targetBody, Transform local )
	{
		foreach ( var chunk in chunks ) chunk.Shape = null;

		if ( !Enabled || !EnableCollision || Storage is null )
			yield break;

		foreach ( var chunk in chunks )
		{
			if ( !chunk.Solid ) continue;

			var shape = ShapeOf( chunk, targetBody );
			if ( !shape.IsValid() ) continue;

			chunk.Shape = shape;
			yield return shape;
		}
	}
}
