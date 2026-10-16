using Rinten.Utility;
using System.Collections.Concurrent;

namespace Rinten;

[Expose]
public sealed class SceneAnimationSystem : GameObjectSystem<SceneAnimationSystem>
{
	private HashSetEx<SkinnedModelRenderer> SkinnedRenderers { get; } = new();

	/// <summary>Incremented once per frame when bones are updated. Lets per-frame bone caches - like <see cref="SkinnedModelRenderer.BoneWorldTransforms"/> - know when they're stale without re-reading the skeleton more than once per frame.</summary>
	public int BoneFrame { get; private set; }

	internal void AddRenderer( SkinnedModelRenderer renderer )
	{
		SkinnedRenderers.Add( renderer );
	}

	internal void RemoveRenderer( SkinnedModelRenderer renderer )
	{
		SkinnedRenderers.Remove( renderer );
	}

	private ConcurrentQueue<GameTransform> ChangedTransforms { get; } = new();

	//Reusable lists to avoid per-frame allocations from Parallel.ForEach with IEnumerable<T>. Parallel.ForEach on IEnumerable uses a dynamic
	//partitioner that allocates KeyValuePair<long, T>[] chunks internally; passing an IList<T> uses the static range partitioner instead.
	private readonly List<SkinnedModelRenderer> _rootRenderers = new();
	private readonly List<SkinnedModelRenderer> _boneMergeRoots = new();
	private readonly List<SkinnedModelRenderer> _physRenderers = new();

	private static int _animThreadCount = Math.Max( 1, Environment.ProcessorCount - 1 );

	private static ParallelOptions _animParallelOptions = new()
	{
		MaxDegreeOfParallelism = _animThreadCount
	};

	//The two bodies handed to the parallel walks, made once. As lambdas in place they captured `this` and
	//were allocated on every frame, which for a walk that is usually over nothing was the only thing it did.
	private readonly Action<SkinnedModelRenderer> mergeDescendants;
	private readonly Action<SkinnedModelRenderer> processRenderer;
	private static readonly Action<SkinnedModelRenderer> stepPhysics = static renderer => renderer.Physics.Step();

	//Avoids Parallel.ForEach overhead: nothing for empty lists, inline for a single renderer
	private static void ForEachRenderer( List<SkinnedModelRenderer> renderers, Action<SkinnedModelRenderer> body )
	{
		if ( renderers.Count == 0 ) return;

		//And on a page, whatever the count: there is no other thread to give them to.
		if ( renderers.Count == 1 || Rinten.Utility.Parallel.InOrder )
		{
			for ( var i = 0; i < renderers.Count; i++ ) body( renderers[i] );
			return;
		}

		//A load-balanced partitioner: work per root is highly uneven (clothed characters have many
		//bone-merged children), so static range partitioning would cause severe thread idle time.
		Rinten.Utility.Parallel.ForEach( renderers, _animParallelOptions, body );
	}

	public SceneAnimationSystem( Scene scene ) : base( scene )
	{
		mergeDescendants = renderer => renderer.MergeDescendants( ChangedTransforms );
		processRenderer = ProcessRenderer;

		Listen( Stage.UpdateBones, 0, UpdateAnimation, "UpdateAnimation" );
		Listen( Stage.FinishUpdate, 0, FinishUpdate, "FinishUpdate" );
		Listen( Stage.PhysicsStep, 0, PhysicsStep, "PhysicsStep" );
	}

	void UpdateAnimation()
	{
		//Bump once per frame, before any FinishUpdate-stage consumer reads bone snapshots.
		BoneFrame++;

		using ( PerformanceStats.Timings.Animation.Scope() )
		{
			_rootRenderers.Clear();
			_boneMergeRoots.Clear();

			//Nothing animating - no bones to update, no decode caches worth maintaining
			if ( SkinnedRenderers.Count == 0 )
				return;

			foreach ( var renderer in SkinnedRenderers.EnumerateLocked() )
			{
				//A graph compiled since the last frame - a model's own, written after the model or edited on its own - put on whatever already
				//stands in that model. Before the parameters: a graph handed over starts again at its defaults, and they are what says them again.
				renderer.RefreshAnimationGraph();

				//A model that changed since the last frame has a graph built again and sitting at its defaults.
				//Whoever set something on the old one is not going to say it twice, so it is said for them.
				renderer.RefreshAnimParameters();

				//Drive viseme morphs from any speaking voice before bones/morphs update
				if ( renderer.VoiceActive )
					renderer.UpdateVoice();

				if ( renderer.IsRootRenderer )
					_rootRenderers.Add( renderer );

				if ( !renderer.BoneMergeTarget.IsValid() && renderer.HasBoneMergeChildren )
					_boneMergeRoots.Add( renderer );
			}

			//Skip out if we have a parent that is a skinned model, because we need to move relative to that
			//and their bones haven't been worked out yet. They will get worked out after our parent is.
			ForEachRenderer( _rootRenderers, processRenderer );

			//No decode cache to maintain here: our animation system decodes its own clips and caches nothing

			//Now merge any descendants without allocating per-merge delegates
			ForEachRenderer( _boneMergeRoots, mergeDescendants );

			while ( ChangedTransforms.TryDequeue( out var tx ) )
			{
				tx.TransformChanged( true );
			}

			//Run events in the main thread
			foreach ( var x in SkinnedRenderers.EnumerateLocked() )
			{
				x.DispatchEvents();
			}
		}
	}

	void ProcessRenderer( SkinnedModelRenderer renderer )
	{
		if ( !renderer.IsValid() || !renderer.Enabled )
			return;

		if ( renderer.AnimationUpdate() )
		{
			ChangedTransforms.Enqueue( renderer.Transform );
		}

		foreach ( var child in renderer.SkinnedChildren )
		{
			ProcessRenderer( child );
		}
	}

	void FinishUpdate()
	{
		using var _ = PerformanceStats.Timings.Animation.Scope();

		foreach ( var renderer in SkinnedRenderers.EnumerateLocked() )
		{
			renderer.FinishUpdate();
		}
	}

	void PhysicsStep()
	{
		using var _ = PerformanceStats.Timings.Animation.Scope();

		_physRenderers.Clear();

		foreach ( var renderer in SkinnedRenderers.EnumerateLocked() )
		{
			if ( renderer.Physics != null )
				_physRenderers.Add( renderer );
		}

		ForEachRenderer( _physRenderers, stepPhysics );
	}
}
