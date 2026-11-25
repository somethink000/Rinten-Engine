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

	//Soundscape for this point; a loop rather than LINQ, so asking it every fifth of a second allocates nothing
	SoundscapeTrigger Closest( Vector3 head )
	{
		SoundscapeTrigger best = null;
		(int, float) bestKey = default;

		foreach ( var trigger in Scene.GetAllComponents<SoundscapeTrigger>() )
		{
			if ( !trigger.TestListenerPosition( head ) ) continue;

			var key = (trigger.Type == SoundscapeTrigger.TriggerType.Point ? 1 : 0, trigger.WorldPosition.DistanceSquared( head ));
			if ( best is not null && key.CompareTo( bestKey ) >= 0 ) continue;

			best = trigger;
			bestKey = key;
		}

		return best;
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
