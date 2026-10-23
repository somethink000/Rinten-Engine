using System.Collections.Concurrent;

namespace Rinten;

/// <summary>Simulates VerletRope components in parallel during PrePhysicsStep</summary>
internal sealed class VerletRopeGameSystem : GameObjectSystem
{
	private readonly List<VerletRope> _ropes = new();

	public VerletRopeGameSystem( Scene scene ) : base( scene )
	{
		//Listen to StartFixedUpdate to run before physics
		Listen( Stage.StartFixedUpdate, -100, UpdateRopes, "UpdateRopes" );
	}

	void UpdateRopes()
	{
		using var _ = PerformanceStats.Timings.Physics.Scope();

		_ropes.Clear();
		Scene.GetAll<VerletRope>( _ropes );
		if ( _ropes.Count == 0 ) return;

		//The step as a field and the body made once: a lambda over a local was a closure made every frame, whichever way it ran
		stepDelta = Time.Delta;
		Rinten.Utility.Parallel.ForEach( _ropes, null, simulate ??= rope => rope.Simulate( stepDelta ) );
	}

	float stepDelta;
	Action<VerletRope> simulate;
}
