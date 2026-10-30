namespace Rinten;

/// <summary>Defines a box collider.</summary>
[Expose]
[Title( "Collider - Box" )]
[Category( "Physics" )]
[Icon( "check_box_outline_blank" )]
[Alias( "ColliderBoxComponent" )]
public sealed class BoxCollider : Collider
{
	private Vector3 _center = 0;
	//A metre cube. It was fifty, which was fifty inches where this came from and is fifty metres
	//here - a box the size of an office block, dropped on the scene by clicking Add Component.
	private Vector3 _scale = 1.0f;

	/// <summary>The size of the box, from corner to corner.</summary>
	[Property, Title( "Size" ), Group( "Box" )]
	public Vector3 Scale
	{
		get => _scale;
		set
		{
			if ( _scale == value ) return;

			_scale = value;
			UpdateShape();
		}
	}

	/// <summary>The center of the box relative to this GameObject</summary>
	[Property, Group( "Box" )]
	public Vector3 Center
	{
		get => _center;
		set
		{
			if ( _center == value ) return;

			_center = value;
			UpdateShape();
		}
	}

	/// <summary>Sets the box to the bounds of this GameObject</summary>
	[Button( "Set to GameObject Bounds", "fullscreen" ), Group( "Box" )]
	public void SetToGameObjectBounds()
	{
		BBox? bounds = null;

		foreach ( var component in GameObject.GetComponentsInChildren<Component.IHasBounds>() )
		{
			if ( component is Component source && ReferenceEquals( source, this ) )
				continue;

			var sourceTransform = component is Component sourceComponent ? WorldTransform.ToLocal( sourceComponent.WorldTransform ) : global::Transform.Zero;
			var localBounds = component.LocalBounds.Transform( sourceTransform );
			bounds = bounds.HasValue ? bounds.Value.AddBBox( localBounds ) : localBounds;
		}

		if ( !bounds.HasValue )
			return;

		_center = bounds.Value.Center;
		_scale = bounds.Value.Size;
		UpdateShape();
	}

	private PhysicsShape Shape;

	internal override void UpdateShape()
	{
		if ( !Shape.IsValid() )
			return;

		var body = Rigidbody;
		var world = Transform.TargetWorld;
		var local = body.IsValid() ? body.Transform.TargetWorld.WithScale( 1.0f ).ToLocal( world ) : global::Transform.Zero;
		var box = BBox.FromPositionAndSize( Center, Scale );
		box.Mins *= world.Scale;
		box.Maxs *= world.Scale;

		//The shape rotates about the box's center, so the center's offset from our origin needs rotating too
		var offset = local.Position + local.Rotation * box.Center - box.Center;
		box.Mins += offset;
		box.Maxs += offset;

		Shape.UpdateBoxShape( box.Center, local.Rotation, box.Size * 0.5f );

		CalculateLocalBounds();
	}

	protected override void DrawGizmos()
	{
		if ( !Gizmo.IsSelected && !Gizmo.IsHovered )
			return;

		var box = BBox.FromPositionAndSize( Center, Scale );

		//The edges coincide with the model's faces, so with the depth test on they are hidden by the very box they outline.
		Gizmo.Hitbox.DepthBias = 0.01f;
		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 1;
		Gizmo.Draw.Color = Gizmo.Colors.Green.WithAlpha( Gizmo.IsSelected ? 1.0f : 0.2f );

		if ( Scene.Is2D )
		{
			//In the plane the box is its outline seen side-on.
			var z = box.Center.z;
			var a = new Vector3( box.Mins.x, box.Mins.y, z );
			var b = new Vector3( box.Maxs.x, box.Mins.y, z );
			var c = new Vector3( box.Maxs.x, box.Maxs.y, z );
			var d = new Vector3( box.Mins.x, box.Maxs.y, z );
			Gizmo.Draw.Line( a, b );
			Gizmo.Draw.Line( b, c );
			Gizmo.Draw.Line( c, d );
			Gizmo.Draw.Line( d, a );
			return;
		}

		Gizmo.Draw.LineBBox( box );
	}

	protected override IEnumerable<PhysicsShape> CreatePhysicsShapes( PhysicsBody targetBody, Transform local )
	{
		var box = BBox.FromPositionAndSize( Center, Scale );

		var scale = WorldScale;

		//scale by our scale!
		box.Mins *= scale;
		box.Maxs *= scale;

		//Move! The shape rotates about the box's center, so the center's offset from our origin needs rotating too
		var offset = local.Position + local.Rotation * box.Center - box.Center;
		box.Mins += offset;
		box.Maxs += offset;

		var shape = targetBody.AddBoxShape( box, local.Rotation );

		Shape = shape;

		yield return shape;
	}
}
