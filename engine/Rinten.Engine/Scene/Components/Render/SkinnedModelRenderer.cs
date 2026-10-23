using System.Collections.Concurrent;

namespace Rinten;

/// <summary>Renders a skinned model in the world. A skinned model is any model with bones/animations.</summary>
[Title( "Model Renderer (skinned)" )]
[Category( "Rendering" )]
[Icon( "sports_martial_arts" )]
[Alias( "AnimatedModelComponent" )]
public sealed partial class SkinnedModelRenderer : ModelRenderer, Component.ExecuteInEditor
{
	bool _createBones = false;

	[Property, Group( "Bones", StartFolded = true )]
	public bool CreateBoneObjects
	{
		get => _createBones;
		set
		{
			if ( _createBones == value ) return;
			_createBones = value;

			UpdateObject();
		}
	}

	SkinnedModelRenderer _boneMergeTarget;

	[Property, Group( "Bones" )]
	public SkinnedModelRenderer BoneMergeTarget
	{
		get => _boneMergeTarget;
		set
		{
			if ( value == this ) return;
			if ( _boneMergeTarget == value ) return;

			_boneMergeTarget?.RemoveBoneMergeChild( this );

			_boneMergeTarget = value;

			_boneMergeTarget?.AddBoneMergeChild( this );
		}
	}

	bool _useAnimGraph = true;

	/// <summary>Usually used for turning off animation on ragdolls.</summary>
	[Property, Group( "Animation" ), Title( "Use Animation Graph" )]
	public bool UseAnimGraph
	{
		get => _useAnimGraph;
		set
		{
			if ( _useAnimGraph == value ) return;

			_useAnimGraph = value;

			if ( SceneModel.IsValid() )
			{
				SceneModel.UseAnimGraph = value;
			}
		}
	}

	AnimGraph _animGraph;

	//Override: clearing it falls back to the model's own graph, not the bind pose
	/// <summary>The animation graph driving this, in our own format. Empty means the one the model itself names.</summary>
	[Property, Group( "Animation" ), Title( "Animation Graph" ), ShowIf( nameof( UseAnimGraph ), true )]
	public AnimGraph AnimGraph
	{
		get => _animGraph;
		set
		{
			if ( _animGraph == value ) return;

			_animGraph = value;

			ApplyAnimationGraph();
		}
	}

	/// <summary>The effective graph: the override or the model's own AnimGraph. Not a separate property</summary>
	public AnimGraph AnimationGraph
	{
		get => AnimGraph;
		set => AnimGraph = value;
	}

	public AnimGraph EffectiveAnimationGraph => _animGraph ?? Model?.DefaultAnimationGraph;

	//Applies the effective graph to the scene model; called when the override or model changes
	void ApplyAnimationGraph()
	{
		if ( !SceneModel.IsValid() ) return;

		var graph = EffectiveAnimationGraph;

		//Setting a graph starts it from its first frame, so saying the one already playing is not
		//free - it is a character snapping back to idle every time an unrelated property is touched.
		if ( SceneModel.AnimGraph == graph ) return;

		SceneModel.AnimGraph = graph;
	}

	//The AnimGraph.Generation the graph was last asked for at - see RefreshAnimationGraph.
	int _graphAskedAt = -1;

	//Re-applies the graph when any graph was recompiled, so models don't stay in bind pose
	internal void RefreshAnimationGraph()
	{
		var generation = AnimGraph.Generation;

		if ( _graphAskedAt == generation ) return;

		_graphAskedAt = generation;

		ApplyAnimationGraph();
	}

	/// <summary>Allows playback of sequences directly, rather than using an animation graph. Requires <see cref="UseAnimGraph"/> disabled if the scene model has one.</summary>
	[Property, Group( "Animation" ), ShowIf( nameof( ShouldShowSequenceEditor ), true ), InlineEditor( Label = false )]
	public SequenceAccessor Sequence
	{
		get
		{
			_sequence ??= new SequenceAccessor( this );
			return _sequence;
		}
	}

	float _playbackRate = 1.0f;

	/// <summary>Control playback rate of animgraph or current sequence.</summary>
	[Property, Range( 0.0f, 4.0f ), Group( "Animation" )]
	public float PlaybackRate
	{
		get => _playbackRate;
		set
		{
			if ( _playbackRate == value ) return;

			_playbackRate = value;

			if ( SceneModel.IsValid() )
			{
				SceneModel.PlaybackRate = _playbackRate;
			}
		}
	}

	public SceneModel SceneModel => _sceneObject as SceneModel;

	public Transform RootMotion => SceneModel.IsValid() ? SceneModel.RootMotion : default;

	readonly HashSet<SkinnedModelRenderer> _mergeChildren = new();

	internal bool HasBoneMergeChildren => _mergeChildren.Count > 0;

	private void AddBoneMergeChild( SkinnedModelRenderer newChild )
	{
		ArgumentNullException.ThrowIfNull( newChild );

		_mergeChildren.Add( newChild );

		//Merge immediately if we can. This prevents a problem where components are added after
		//the animation has been worked out, so you get a one frame flicker of the default pose.
		if ( SceneModel is not null && newChild.SceneModel is not null )
		{
			newChild.SceneModel.Transform = SceneModel.Transform;
			newChild.SceneModel.MergeBones( SceneModel );

			//Updated bones, transform is no longer dirty.
			newChild._transformDirty = false;

			//Create bone physics on child if they exist.
			newChild.RebuildBonePhysics( this );

			if ( !newChild.UpdateGameObjectsFromBones() )
				return;

			if ( ThreadSafe.IsMainThread )
			{
				newChild.Transform.TransformChanged();
			}
		}
	}

	private void RemoveBoneMergeChild( SkinnedModelRenderer oldChild )
	{
		ArgumentNullException.ThrowIfNull( oldChild );

		_mergeChildren.Remove( oldChild );

		oldChild.DestroyBonePhysics();
	}

	private HashSet<SkinnedModelRenderer> _skinnedChildren = new();
	private SkinnedModelRenderer _skinnedParent = null;

	internal bool IsRootRenderer => _skinnedParent == null;

	internal IEnumerable<SkinnedModelRenderer> SkinnedChildren => _skinnedChildren;

	private void UpdateSkinnedRendererParent()
	{
		//Get the first ancestor skinned model renderer
		var potentialNewParent = GameObject.Parent?.Components.GetInAncestors<SkinnedModelRenderer>( true );

		//Check if there are any other skinned renderers in the parent This is an edge case, generally you should only have one skinned model renderer per GO
		var otherPotentialParents = potentialNewParent?.GetComponents<SkinnedModelRenderer>( true );
		if ( otherPotentialParents != null && otherPotentialParents.Count() > 1 )
		{
			//Make sure only one of the skinned renderers contains children
			foreach ( var parentSibling in otherPotentialParents.Where( x => x != potentialNewParent ) )
			{
				potentialNewParent._skinnedChildren.UnionWith( parentSibling._skinnedChildren );
				parentSibling._skinnedChildren.Clear();
			}
		}

		_skinnedParent?._skinnedChildren.Remove( this );
		_skinnedParent = potentialNewParent;

		if ( potentialNewParent != null )
		{
			_skinnedParent._skinnedChildren.Add( this );
		}
	}

	protected override void OnParentChanged( GameObject oldParent, GameObject newParent )
	{
		UpdateSkinnedRendererParent();
	}

	protected override void OnEnabled()
	{
		Assert.True( _sceneObject == null, "_sceneObject should be null - disable wasn't called" );
		Assert.NotNull( Scene, "Scene should not be null" );

		UpdateSkinnedRendererParent();
		Scene.GetSystem<SceneAnimationSystem>().AddRenderer( this );

		var model = ModelOrError;

		var so = new SceneModel( Scene.SceneWorld, model, WorldTransform );
		_sceneObject = so;

		//What was set here, or what the model came with - see EffectiveAnimationGraph. Off the
		//model actually being spawned, which is the cube where the renderer was given none.
		var graph = _animGraph ?? model.DefaultAnimationGraph;

		if ( graph is not null )
		{
			so.AnimGraph = graph;
		}

		if ( so.UseAnimGraph != UseAnimGraph )
		{
			so.UseAnimGraph = UseAnimGraph;
		}

		so.PlaybackRate = PlaybackRate;

		OnSceneObjectCreated( _sceneObject );

		Transform.OnTransformChanged += OnTransformChanged;
	}

	protected override void OnDisabled()
	{
		_boneMergeTarget?.RemoveBoneMergeChild( this );

		_skinnedParent?._skinnedChildren.Remove( this ); //no need to run full update
		_skinnedParent = null;

		Scene.GetSystem<SceneAnimationSystem>().RemoveRenderer( this );
	}

	internal override void OnSceneObjectCreated( SceneObject o )
	{
		base.OnSceneObjectCreated( o );

		ApplyStoredAnimParameters();

		Morphs.Apply();
		Sequence.Apply();

		//Merge our bones immediately if we have a parent. This avoids frame 0 flicker. Added
		//March 2026 to fix avatar bind pose flicker when hovering clothing icons for preview
		var skinParent = _boneMergeTarget ?? _skinnedParent;
		if ( skinParent.IsValid() && skinParent.SceneModel.IsValid() && o is SceneModel sm )
		{
			sm.Transform = skinParent.SceneModel.Transform;
			sm.MergeBones( skinParent.SceneModel );
		}
	}

	protected override void UpdateObject()
	{
		BuildBoneHierarchy();

		//If the model is changing, let go of any voice morphs now, while the scene object still has the old model our morph indices belong to
		ReleaseVoiceMorphs();

		base.UpdateObject();

		if ( !SceneModel.IsValid() )
			return;

		//The model may have just changed under us, and which graph belongs to this renderer depends on which model it is wearing.
		ApplyAnimationGraph();

		SceneModel.OnFootstepEvent = InternalOnFootstep;
		SceneModel.OnSoundEvent = InternalOnSoundEvent;
		SceneModel.OnGenericEvent = InternalOnGenericEvent;
		SceneModel.OnAnimTagEvent = InternalOnAnimTagEvent;

		//If we have a bone merge target then just set up the bone merge which will read the bones and set the game object
		//positions. If we're not bone merge, then do a first frame update to set the bone positions before anything else happens.
		if ( _boneMergeTarget is not null )
		{
			_boneMergeTarget.AddBoneMergeChild( this );
		}
		else
		{
			if ( Scene.IsEditor && !CanUpdateInEditor() )
			{
				SceneModel.UpdateToBindPose( readBones ??= ReadBonesFromGameObjects );
			}
			else
			{
				UpdateTransform( WorldTransform );
			}

			//Updated bones, transform is no longer dirty.
			_transformDirty = false;

			UpdateGameObjectsFromBones();
		}
	}

	internal override void OnDisabledInternal()
	{
		try
		{
			ClearBoneProxies();
		}
		finally
		{
			Transform.OnTransformChanged -= OnTransformChanged;

			base.OnDisabledInternal();
		}

		Physics?.Destroy();
	}

	[Obsolete]
	public void PostAnimationUpdate()
	{
		ThreadSafe.AssertIsMainThread();

		if ( !SceneModel.IsValid() )
			return;

		SceneModel.RunPendingEvents();
		SceneModel.DispatchTagEvents();

		//Skip if we're bone merged, the target will handle the merge.
		if ( _boneMergeTarget.IsValid() )
			return;

		//Bone merge all children in hierarchy in order.
		MergeDescendants();
	}

	internal void DispatchEvents()
	{
		ThreadSafe.AssertIsMainThread();

		if ( !SceneModel.IsValid() )
			return;
		SceneModel.RunPendingEvents();
		SceneModel.DispatchTagEvents();
	}

	/// <summary>If true then animations will play while in an editor scene.</summary>
	public bool PlayAnimationsInEditorScene { get; set; }

	internal bool CanUpdateInEditor()
	{
		if ( PlayAnimationsInEditorScene ) return true;

		//A model that names its own graph is meant to be doing something. A citizen standing still breathes, shifts its weight and blinks, and
		//holding it at the bind pose in the editor is how a graph that works looks like one that does not - which is exactly how it looked.
		if ( UseAnimGraph && SceneModel.IsValid() && SceneModel.AnimGraph is not null )
			return true;

		//Do we have any modified animgraph parameters?
		if ( StoredParameterCount > 0 )
			return true;

		//If we're not using animgraph, do we have a sequence selected?
		if ( !UseAnimGraph && !string.IsNullOrWhiteSpace( Sequence.Name ) )
			return true;

		//Have we procedurally moved any bones?
		return SceneModel.IsValid() && SceneModel.HasBoneOverrides();
	}

	internal bool AnimationUpdate()
	{
		if ( !SceneModel.IsValid() )
			return false;

		SceneModel.Transform = WorldTransform;

		lock ( this )
		{
			//Update physics bones if they exist.
			Physics?.Update();

			if ( Scene.IsEditor && !CanUpdateInEditor() )
			{
				SceneModel.UpdateToBindPose( readBones ??= ReadBonesFromGameObjects );
			}
			else
			{
				//A scene being edited is not playing, so its clock does not move - and a graph stepped by nothing holds its first
				//frame for ever, which looks like a character that will not breathe. The editor's own clock is the one that is running.
				var step = Scene.IsEditor ? RealTime.Delta : Time.Delta;

				SceneModel.Update( step, readBones ??= ReadBonesFromGameObjects );
			}
		}

		//Updated bones, transform is no longer dirty.
		_transformDirty = false;

		//Skip if we're bone merged, the target will handle the merge.
		return !_boneMergeTarget.IsValid() && UpdateGameObjectsFromBones();
	}

	bool _transformDirty;

	//ReadBonesFromGameObjects as the delegate the model asks for, made once rather than every update
	Action readBones;

	//Whether a merge onto this one that matched no bones has been said.
	bool _mergeReported;

	void OnTransformChanged()
	{
		//Tell the drawer we moved; OnEnabled here doesn't chain to the base, which normally does this
		ForgetCullSphere();

		//Check transform because we could get a false positive.
		_transformDirty = SceneModel.IsValid() && SceneModel.Transform != WorldTransform;
	}

	internal void FinishUpdate()
	{
		//Debug draw physics world if it exists.
		Physics?.DebugDraw();

		if ( !_transformDirty )
			return;

		//Skip if we're bone merged, the target will handle the merge.
		if ( _boneMergeTarget.IsValid() )
			return;

		//Transform changed, make sure bones are updated.
		UpdateTransform( WorldTransform );

		//Updated bones, transform is no longer dirty.
		_transformDirty = false;

		//Update all bone merge children to new transform.
		MergeDescendants();
	}

	void UpdateTransform( Transform transform )
	{
		if ( !SceneModel.IsValid() )
			return;

		SceneModel.Transform = transform;
		ReadBonesFromGameObjects();
		SceneModel.FinishBoneUpdate();
	}

	//For Procedural Bones, copy the current value to the animation bone
	void ReadBonesFromGameObjects()
	{
		//Bone map can outlive the model it was built from, so indices aren't guaranteed to still be in range.
		var boneCount = Model.IsValid() ? Model.BoneCount : 0;

		foreach ( var entry in boneToGameObject )
		{
			if ( !entry.Value.Flags.Contains( GameObjectFlags.ProceduralBone ) )
				continue;

			// Ignore absolute bones, they're probably physics bones
			if ( entry.Value.Flags.Contains( GameObjectFlags.Absolute ) )
				continue;

			var boneIndex = entry.Key.Index;
			if ( boneIndex < 0 || boneIndex >= boneCount )
				continue;

			var localTransform = entry.Value.LocalTransform;
			if ( localTransform.IsValid )
			{
				SceneModel.SetParentSpaceBone( boneIndex, localTransform );
			}
		}
	}

	private SkinnedModelRenderer RootBoneMergeTarget => BoneMergeTarget.IsValid() ? BoneMergeTarget.RootBoneMergeTarget : this;

	//For non procedural bones, copy the "parent space" bone from to the GameObject transform. Will return true if any transforms have changed.
	Transform[] _parentSpaceScratch;

	bool UpdateGameObjectsFromBones()
	{
		bool transformsChanged = false;

		var mergeTarget = RootBoneMergeTarget;

		//The offset between our transform and root target.
		Transform? mergeOffset = mergeTarget.IsValid() ? WorldTransform.ToLocal( mergeTarget.WorldTransform ) : default;

		//Pull every parent-space bone in one interop call rather than one per bone object.
		var boneCount = Model.IsValid() ? Model.BoneCount : 0;
		if ( _parentSpaceScratch is null || _parentSpaceScratch.Length < boneCount )
			_parentSpaceScratch = new Transform[boneCount];
		SceneModel.GetParentSpaceBones( _parentSpaceScratch.AsSpan( 0, boneCount ) );

		foreach ( var entry in boneToGameObject )
		{
			// Ignore procedural bones, local transform is set manually.
			if ( entry.Value.Flags.Contains( GameObjectFlags.ProceduralBone ) )
				continue;

			// Ignore absolute bones, they're probably physics bones.
			if ( entry.Value.Flags.Contains( GameObjectFlags.Absolute ) )
				continue;

			var boneIndex = entry.Key.Index;
			if ( boneIndex < 0 || boneIndex >= boneCount )
				continue;

			var transform = _parentSpaceScratch[boneIndex];
			if ( !transform.IsValid )
				continue;

			//Offset root bones to move us to the root target.
			if ( mergeOffset.HasValue && entry.Key.Parent is null )
			{
				transform = mergeOffset.Value.ToWorld( transform );
			}

			transformsChanged |= entry.Value.Transform.SetLocalTransformFast( transform );
		}

		foreach ( var entry in attachmentToGameObject )
		{
			var transform = SceneModel.GetAttachment( entry.Key.Name, false );
			if ( !transform.HasValue )
				continue;

			//Offset root attachments to move us to the root target.
			if ( mergeOffset.HasValue && entry.Key.Bone is null )
			{
				transform = mergeOffset.Value.ToWorld( transform.Value );
			}

			transformsChanged |= entry.Value.Transform.SetLocalTransformFast( transform.Value );
		}

		return transformsChanged;
	}

	internal void MergeDescendants( ConcurrentQueue<GameTransform> changedTransforms = null )
	{
		foreach ( var child in _mergeChildren )
		{
			if ( !child.IsValid() )
				continue;

			var so = child.SceneModel;
			if ( !so.IsValid() )
				continue;

			var target = child.BoneMergeTarget;
			if ( !target.IsValid() )
				continue;

			var parent = target.SceneModel;
			if ( !parent.IsValid() )
				continue;

			so.Transform = parent.Transform;

			var answered = so.MergeBones( parent );

			//A merge that matched nothing is not a merge - the child keeps its bind pose and is drawn at the target's root, which reads as a model stuck to
			//somebody's feet. Said once per pairing, because it is otherwise indistinguishable from a merge that worked and because it happens every frame.
			if ( answered == 0 && !child._mergeReported )
			{
				child._mergeReported = true;

				Log.Warning( $"{child.GameObject?.Name}: none of {child.Model?.ResourcePath}'s bones are named " +
					$"in {target.Model?.ResourcePath}, so merging onto it moves nothing" );
			}
			else if ( answered > 0 )
			{
				child._mergeReported = false;
			}

			//Updated bones, transform is no longer dirty.
			child._transformDirty = false;

			if ( child.UpdateGameObjectsFromBones() )
			{
				if ( changedTransforms is not null )
				{
					changedTransforms.Enqueue( child.Transform );
				}
				else if ( ThreadSafe.IsMainThread )
				{
					child.Transform.TransformChanged();
				}
			}

			child.MergeDescendants( changedTransforms );
		}
	}

	public Transform? GetAttachment( string name, bool worldSpace = true )
	{
		return SceneModel?.GetAttachment( name, worldSpace );
	}
}
