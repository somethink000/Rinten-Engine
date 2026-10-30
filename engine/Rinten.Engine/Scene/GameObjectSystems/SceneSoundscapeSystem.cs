namespace Rinten;

/// <summary>Implements logic for the SoundScape system</summary>
[Expose]
sealed class SceneSoundscapeSystem : GameObjectSystem<SceneSoundscapeSystem>
{
	public SceneSoundscapeSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.UpdateBones, 0, Update, "TickSoundScapes" );
	}

	SoundscapeTrigger active;
	RealTimeSince timeSinceUpdate;

	//Soundscape for this point; separate from Update so its capturing lambdas don't allocate per tick
	SoundscapeTrigger Closest( Vector3 head )
	{
		return Scene.GetAllComponents<SoundscapeTrigger>()
			.Where( x => x.TestListenerPosition( head ) )
			.MinBy( x => (x.Type == SoundscapeTrigger.TriggerType.Point ? 1 : 0, x.WorldPosition.DistanceSquared( head )) );
	}

	void Update()
	{
		using var _ = PerformanceStats.Timings.Audio.Scope();

		if ( Scene.IsEditor )
			return;

		if ( timeSinceUpdate < 0.2f )
			return;

		timeSinceUpdate = 0;

		//Find the closest soundscape, sphere and box take priority over point.
		var best = Closest( Scene.GetPrimaryListenerTransform().Position );

		if ( best == active )
			return;

		if ( best == null && active.StayActiveOnExit )
			return;

		if ( active is not null )
			active.Playing = false;

		active = best;

		if ( active is not null )
			active.Playing = true;


	}
}
