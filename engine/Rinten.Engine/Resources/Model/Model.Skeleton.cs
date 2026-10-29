namespace Rinten;

internal class ModelBones : BoneCollection
{
	public Model Model { get; }

	Bone _root;

	public override Bone Root => _root;

	List<Bone> _allBones;

	public override IReadOnlyList<Bone> AllBones => _allBones;

	public ModelBones( Model model )
	{
		Model = model;

		_allBones = new();

		for ( int i = 0; i < Model.BoneCount; i++ )
		{
			var b = new ModelBone( Model, i );
			_allBones.Add( b );
		}

		for ( int i = 0; i < Model.BoneCount; i++ )
		{
			var parent = Model.GetBoneParent( i );
			if ( parent < 0 ) continue;
			if ( parent >= _allBones.Count() ) continue;
			_allBones[i].SetParent( _allBones[parent] );
		}

		_root = _allBones.Where( x => x.Parent == null ).FirstOrDefault();
	}

	class ModelBone : Bone
	{
		public ModelBone( Model model, int i )
		{
			Index = i;
			Name = model.GetBoneName( i );
			LocalTransform = model.GetBoneTransform( i );
		}

		public override int Index { get; }
		public override string Name { get; }
		public override Transform LocalTransform { get; }
	}

	internal void Dispose()
	{
		_allBones.Clear();
	}
}

public partial class Model
{
	BoneCollection _bones;

	/// <summary>Access to bones of this model.</summary>
	public BoneCollection Bones
	{
		get
		{
			_bones ??= new ModelBones( this );
			return _bones;
		}
	}

	//A model in our own format has no native peer to ask - its bones live in the skeleton our core built out of it, which is also what animates them. The same goes for the three below.
	/// <summary>Number of bones this model has.</summary>
	public int BoneCount => CoreSkeletons.BoneCount( this );

	/// <summary>Returns name of a bone at given bone index.</summary>
	public string GetBoneName( int boneIndex )
	{
		OOBChecks.ThrowIfBoneOutOfBounds( boneIndex, BoneCount, nameof( boneIndex ) );

		return CoreSkeletons.BoneName( this, boneIndex );
	}

	/// <summary>Returns the id of given bone's parent bone.</summary>
	public int GetBoneParent( int boneIndex )
	{
		OOBChecks.ThrowIfBoneOutOfBounds( boneIndex, BoneCount, nameof( boneIndex ) );

		return CoreSkeletons.BoneParent( this, boneIndex );
	}

	/// <summary>Returns transform of given bone at bind position.</summary>
	public Transform GetBoneTransform( int boneIndex )
	{
		OOBChecks.ThrowIfBoneOutOfBounds( boneIndex, BoneCount, nameof( boneIndex ) );

		return CoreSkeletons.BoneRest( this, boneIndex );
	}

	/// <summary>Returns transform of given bone at bind position.</summary>
	public Transform GetBoneTransform( string bone )
	{
		var found = Bones?.AllBones?.FirstOrDefault( x => x.IsNamed( bone ) );

		return found is null ? Transform.Zero : GetBoneTransform( found.Index );
	}

	/// <summary>Creates a GameObject per bone; flagAsBone false for physics-only use, so no skeleton is drawn</summary>
	public Dictionary<BoneCollection.Bone, GameObject> CreateBoneObjects( GameObject root, bool flagAsBone = true )
	{
		Dictionary<BoneCollection.Bone, GameObject> boneObjects = new();
		if ( !root.IsValid() ) return boneObjects;

		foreach ( var b in Bones.AllBones.Where( x => x is { Parent: null } ) )
		{
			CreateBoneObjects( root, b, boneObjects, flagAsBone );
		}

		return boneObjects;
	}

	//Recursively creates game objects for each bone in the hierarchy, starting from the given root bone.
	private void CreateBoneObjects( GameObject root, BoneCollection.Bone thisBone,
		Dictionary<BoneCollection.Bone, GameObject> boneObjects, bool flagAsBone )
	{
		var scene = root.Scene;
		if ( !scene.IsValid() ) return;

		var go = root.Children.FirstOrDefault( x => x.Name == thisBone.Name && !x.Flags.Contains( GameObjectFlags.Attachment ) );

		go ??= new GameObject( root, true, thisBone.Name );

		//Only ever added, never taken away: where a renderer has already made these
		//and marked them, something else reusing them must not unmark them behind it.
		if ( flagAsBone )
			go.Flags |= GameObjectFlags.Bone;

		boneObjects[thisBone] = go;

		foreach ( var childBone in thisBone.Children )
		{
			CreateBoneObjects( go, childBone, boneObjects, flagAsBone );
		}
	}

	//A model is its own skeleton here - there is no separate skeleton asset, and a clip records the model it was authored on. Kept as a name because that is what it is compared against.
	/// <summary>What clips made against this model say they were made against.</summary>
	public string SkeletonName => Name ?? "";

	//A mask is a fact about a skeleton rather than about a graph, so it is written on the model and asked for here by name: the editor offers these beside the bones, and a graph that names one gets the weights the model it is animating keeps under that name.
	/// <summary>The bone masks the model carries - the weight lists its definition names.</summary>
	public IReadOnlyList<string> BoneMaskNames => CoreSkeletons.WeightListNames( this );
}
