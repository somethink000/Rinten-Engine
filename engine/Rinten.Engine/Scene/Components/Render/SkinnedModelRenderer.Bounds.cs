namespace Rinten;

partial class SkinnedModelRenderer
{
	//Rejects NaN or inverted boxes; a NaN cull sphere would make the renderer invisible forever
	static bool Usable( in BBox box )
		=> !box.Mins.IsNaN && !box.Maxs.IsNaN
			&& box.Maxs.x >= box.Mins.x && box.Maxs.y >= box.Mins.y && box.Maxs.z >= box.Mins.z;

	//Said once per renderer - a bad pose is bad every frame.
	bool _warnedAboutBounds;

	void WarnAboutBounds( string which, in BBox box )
	{
		if ( _warnedAboutBounds ) return;
		_warnedAboutBounds = true;

		var overrides = SceneModel.IsValid() && SceneModel.HasBoneOverrides();

		Log.Warning( $"{GameObject?.Name}: {Model?.ResourcePath ?? "<no model>"} has unusable {which} bounds {box} - " +
			$"falling back to the model's own so it stays visible. " +
			$"boneOverrides={overrides}, physics={GetComponent<ModelPhysics>().IsValid()}, " +
			$"worldTransform={WorldTransform}" );
	}

	internal override BBox GetWorldBoundsInternal()
	{
		//Whoever is animating keeps its own box, or says it has nothing better than the model's - which is what the base measures.
		if ( SceneModel.IsValid() && SceneModel.Animation.WorldBounds is { } bounds )
		{
			if ( Usable( bounds ) )
				return bounds;

			WarnAboutBounds( "world", bounds );
		}

		return base.GetWorldBoundsInternal();
	}

	//Cull sphere holds unless the animator provides pose bounds - see CoreAnimatableObject.LocalBounds
	internal override bool CullSphereHolds
		=> !SceneModel.IsValid() || SceneModel.Animation is not { } animation
			|| ( animation.LocalBounds is null && animation.WorldBounds is null );

	internal override BBox GetLocalBoundsInternal()
	{
		if ( SceneModel.IsValid() && SceneModel.Animation.LocalBounds is { } bounds )
		{
			if ( Usable( bounds ) )
				return bounds;

			WarnAboutBounds( "local", bounds );
		}

		return base.GetLocalBoundsInternal();
	}
}
