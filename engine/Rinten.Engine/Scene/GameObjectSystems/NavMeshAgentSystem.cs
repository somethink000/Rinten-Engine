using System.Collections.Concurrent;

namespace Rinten;

/// <summary>Updates NavMeshAgent ground positions in parallel during PrePhysicsStep.</summary>
internal sealed class NavMeshGameSystem : GameObjectSystem
{
	private readonly List<NavMeshAgent> _agents = new();

	//Below this many agents the traces run on this thread. An agent is two sphere traces every few frames - a few microseconds - and a Parallel.ForEach
	//costs microseconds before its first item and allocates its machinery every call, so a handful of agents would pay more for the fork than for the work.
	private const int ParallelAgents = 32;

	private readonly Action<NavMeshAgent> findGround;

	public NavMeshGameSystem( Scene scene ) : base( scene )
	{
		//Listen to StartFixedUpdate to run before physics
		Listen( Stage.StartFixedUpdate, -100, UpdateAgentGoundZ, "UpdateAgentGroundZ" );
		findGround = FindPhysicsGroundHeight;
	}

	void UpdateAgentGoundZ()
	{
		using var _ = PerformanceStats.Timings.NavMesh.Scope();

		_agents.Clear();
		Scene.GetAll<NavMeshAgent>( _agents );
		if ( _agents.Count == 0 ) return;

		//A browser has one thread; forking there only allocates.
		if ( _agents.Count < ParallelAgents || OperatingSystem.IsBrowser() )
		{
			foreach ( var agent in _agents ) FindPhysicsGroundHeight( agent );
			return;
		}

		Rinten.Utility.Parallel.ForEach( _agents, null, findGround );
	}

	//Trace down from above the capsule centre; with several hits prefer the one closest to the centre
	private void FindPhysicsGroundHeight( NavMeshAgent agent )
	{
		var simulationAgent = agent.agentInternal;
		if ( simulationAgent is null ) return;

		if ( agent.timeUntilNextGroundTrace > 0f )
		{
			return;
		}

		//Introduce some random jitter so not all agents trace on the same frame
		agent.timeUntilNextGroundTrace = Random.Shared.Int( 2, 4 ) * Time.Delta;

		var footRadius = agent.Radius * 0.1f;
		var traceStartOffset = MathF.Max( 1.6f, Scene.NavMesh.AgentHeight ) * 8f;

		var navMeshPos = simulationAgent.State.Position;

		var traceStart = navMeshPos + Vector3.Up * Scene.NavMesh.AgentHeight * 0.3f;

		var baseTrace =
			Scene.Trace
				.IgnoreDynamic()
				.IgnoreGameObjectHierarchy( agent.GameObject )
				.WithAnyTags( Scene.NavMesh.IncludedBodies )
				.WithCollisionRules( agent.Tags )
				.WithoutTags( Scene.NavMesh.ExcludedBodies );

		if ( !Scene.NavMesh.IncludeStaticBodies )
		{
			baseTrace = baseTrace.IgnoreStatic();
		}
		if ( !Scene.NavMesh.IncludeKeyframedBodies )
		{
			baseTrace = baseTrace.IgnoreKeyframed();
		}

		var downTrace = baseTrace.Sphere( footRadius, traceStart, traceStart + Vector3.Down * traceStartOffset );
		var upTrace = baseTrace.Sphere( footRadius, traceStart, traceStart + Vector3.Up * traceStartOffset );

		var downResult = downTrace.Run();
		var upResult = upTrace.Run();

		var bestHeight = navMeshPos.y;
		var closestDistanceToTraceStart = float.MaxValue;

		//Process downTrace result
		if ( downResult.Hit )
		{
			var distance = MathF.Abs( downResult.HitPosition.y - traceStart.y );
			if ( distance < closestDistanceToTraceStart )
			{
				bestHeight = downResult.HitPosition.y;
				closestDistanceToTraceStart = distance;
			}
		}

		//Process upTrace result
		if ( upResult.Hit )
		{
			var distance = MathF.Abs( upResult.HitPosition.y - traceStart.y );
			if ( distance < closestDistanceToTraceStart )
			{
				bestHeight = upResult.HitPosition.y;
			}
		}

		agent.groundTraceHeight = bestHeight;
		if ( downResult.Hit || upResult.Hit )
		{
			lock ( simulationAgent.Owner.Gate )
			{
				if ( simulationAgent.Path.Count == 0 && simulationAgent.Link is null )
					simulationAgent.Position.y = bestHeight;
			}
		}
	}
}
