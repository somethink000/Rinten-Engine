namespace Rinten;

partial class ModelRenderer
{
	/// <summary>The deformers whose target this is.</summary>
	internal List<ModelDeformer> ModelDeformers { get; } = new();

	//Whose deformers reach this one too - what it is bone merged to
	internal virtual ModelRenderer DeformationSource => null;

	/// <summary>The stack packed for the skin pass, ModelDeformer.Words float4s a volume.</summary>
	internal System.Numerics.Vector4[] DeformationWords = [];

	/// <summary>How many volumes DeformationWords holds this frame.</summary>
	internal int DeformationVolumes;

	private readonly List<ModelDeformer> deformedBy = new();
	private readonly HashSet<ModelRenderer> deformationSources = new();

	//Gathers this frame's stack: its own deformers and those of what it is merged to that ask, in priority order
	internal void UpdateDeformations()
	{
		deformedBy.Clear();
		deformationSources.Clear();

		for ( var source = this; source.IsValid() && source.Active && deformationSources.Add( source ); source = source.DeformationSource )
			foreach ( var deformer in source.ModelDeformers )
				if ( deformer.Active && deformer.IsActive && ( source == this || deformer.ApplyToBoneMergedChildren ) )
					deformedBy.Add( deformer );

		if ( !Active ) deformedBy.Clear();

		deformedBy.Sort( static ( a, b ) => a.Priority.CompareTo( b.Priority ) );

		var words = deformedBy.Count * ModelDeformer.Words;
		if ( DeformationWords.Length < words ) DeformationWords = new System.Numerics.Vector4[words];

		var packed = 0;
		for ( var i = 0; i < deformedBy.Count; i++ )
			if ( deformedBy[i].Pack( PlacementOf( deformedBy[i] ), DeformationWords.AsSpan( packed * ModelDeformer.Words, ModelDeformer.Words ) ) ) packed++;

		var was = DeformationVolumes;
		DeformationVolumes = packed;

		//On or off is a different instance to the table: posed or not
		if ( ( was > 0 ) != ( packed > 0 ) ) DrawChanged();
		if ( was > 0 || packed > 0 ) ForgetCullSphere();
	}

	//Model-space bounds grown by what the stack can move them by
	internal BBox Deformed( BBox bounds )
	{
		if ( DeformationVolumes == 0 ) return bounds;

		var grown = bounds;
		foreach ( var deformer in deformedBy ) grown = deformer.Reach( grown );
		return grown;
	}

	//A deformer's placement in this model's space. One on the model this is merged to is in that model's, which is this one's
	//too unless this was exported turned or moved from it - then it is carried across through the first bone they share
	private Transform PlacementOf( ModelDeformer deformer )
	{
		var owner = deformer.Owner;
		if ( owner == this || !owner.IsValid() || owner.Model is null || Model is null ) return deformer.Placement;

		if ( restOffsetModels != (Model, owner.Model) )
		{
			restOffset = RestOffset( Model, owner.Model );
			restOffsetModels = (Model, owner.Model);
		}

		return restOffset is { } offset ? offset.ToLocal( deformer.Placement ) : deformer.Placement;
	}

	private Transform? restOffset;
	private (Model Model, Model Target) restOffsetModels;

	//Where a model's rest pose sits in another's through the first bone they share, or null when it is the same
	private static Transform? RestOffset( Model model, Model target )
	{
		foreach ( var bone in model.Bones.AllBones )
		{
			if ( target.Bones.GetBone( bone.Name ) is not { } shared ) continue;

			var offset = shared.LocalTransform.ToWorld( bone.LocalTransform.ToLocal( global::Transform.Zero ) );
			return offset.Position.Length < 0.00025f && offset.Rotation.Distance( Rotation.Identity ) < 0.01f ? null : offset;
		}

		return null;
	}
}

partial class SkinnedModelRenderer
{
	internal override ModelRenderer DeformationSource => _boneMergeTarget;

	/// <summary>Move with model deformers without being reshaped by them, as for earrings or a sword.</summary>
	[Property, Group( "Deformation" )]
	public bool RigidDeformation { get; set; }

	private System.Numerics.Vector4[] anchors = [];
	private (CoreSceneRenderer.BoneRest[] Bones, Model Model, Model Target) anchorsFor;

	//Each mesh bone's anchor in model space: the rest position of the bone it hangs from - its own, or the nearest up its
	//chain the model it is merged to has, so jiggle bones move as one piece with it. Empty where it deforms as usual
	internal System.Numerics.Vector4[] DeformationAnchors( CoreSceneRenderer.BoneRest[] meshBones )
	{
		if ( !RigidDeformation || Model is null ) return [];

		var target = _boneMergeTarget?.Model;
		if ( anchorsFor != (meshBones, Model, target) )
		{
			anchors = new System.Numerics.Vector4[meshBones.Length];

			for ( var i = 0; i < anchors.Length; i++ )
			{
				var bone = Model.Bones.GetBone( meshBones[i].Name );
				var at = bone is null ? Vector3.Zero : HangsFrom( bone, target ).LocalTransform.Position;
				anchors[i] = new( at.x, at.y, at.z, 0 );
			}

			anchorsFor = (meshBones, Model, target);
		}

		return anchors;
	}

	private static BoneCollection.Bone HangsFrom( BoneCollection.Bone bone, Model target )
	{
		for ( var b = bone; b is not null && target is not null; b = b.Parent )
			if ( target.Bones.HasBone( b.Name ) ) return b;

		return bone;
	}
}
