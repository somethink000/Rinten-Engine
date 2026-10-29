namespace Rinten;

//Gathers every renderer's ModelDeformer stack before the bones are worked out, for the table to hand the skin pass
internal sealed class SceneDeformationSystem : GameObjectSystem<SceneDeformationSystem>
{
	private readonly HashSet<ModelDeformer> deformers = new();
	private readonly HashSet<ModelRenderer> touched = new();

	//Deformed last frame: asked again so one whose deformers went is turned back off
	private readonly HashSet<ModelRenderer> deformed = new();

	public SceneDeformationSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.UpdateBones, -100, Update, "UpdateDeformations" );
	}

	internal void Add( ModelDeformer deformer ) => deformers.Add( deformer );
	internal void Remove( ModelDeformer deformer ) => deformers.Remove( deformer );

	private void Update()
	{
		if ( deformers.Count == 0 && deformed.Count == 0 ) return;

		touched.UnionWith( deformed );
		var merged = false;

		foreach ( var deformer in deformers )
		{
			deformer.UpdateVolume();
			if ( deformer.Owner.IsValid() ) touched.Add( deformer.Owner );
			merged |= deformer.ApplyToBoneMergedChildren;
		}

		if ( merged )
			foreach ( var renderer in Scene.GetAll<SkinnedModelRenderer>() )
				if ( renderer.BoneMergeTarget.IsValid() ) touched.Add( renderer );

		deformed.Clear();

		foreach ( var renderer in touched )
		{
			if ( !renderer.IsValid() ) continue;

			renderer.UpdateDeformations();
			if ( renderer.DeformationVolumes > 0 ) deformed.Add( renderer );
		}

		touched.Clear();
	}
}
