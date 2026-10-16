namespace Rinten;

//Effects one effect plays - where a particle died, or on an event of one. An effect is a tree of objects and components, so one that
//has played through is kept, standing idle, and played again for the next rather than built again. Main thread only.
internal sealed class ParticleChildEffects
{
	//A playing effect, the particle it follows, and the frame it began - it is not over before it has stepped once.
	record struct Child( GameObject Object, ParticlePlayer Player, Particle Follows, long Began );

	readonly List<Child> playing = new();
	readonly Stack<ParticlePlayer> idle = new();

	/// <summary>How many are playing.</summary>
	public int Playing => playing.Count;

	/// <summary>One for this effect, idle or new, not yet started: placed and given its parameters by the caller, then <see cref="Start"/>.</summary>
	public ParticlePlayer Take( ParticleDefinition effect, string name )
	{
		while ( idle.TryPop( out var kept ) )
		{
			if ( kept.IsValid() && kept.GameObject.IsValid() && kept.Definition == effect ) return kept;
			if ( kept.IsValid() && kept.GameObject.IsValid() ) kept.GameObject.Destroy();
		}

		var go = new GameObject( false, name );

		go.Flags = go.Flags
			.WithFlag( GameObjectFlags.Hidden, true )
			.WithFlag( GameObjectFlags.NotSaved, true )
			.WithFlag( GameObjectFlags.NotNetworked, true );

		var player = go.AddComponent<ParticlePlayer>();

		player.Definition = effect;
		player.PlayOnce = true;

		return player;
	}

	/// <summary>Plays one taken with <see cref="Take"/>, following a particle where one is given.</summary>
	public void Start( ParticlePlayer player, Particle follows = null )
	{
		var go = player.GameObject;

		if ( go.Enabled ) player.Restart();
		else go.Enabled = true;

		playing.Add( new Child( go, player, follows, EngineLoop.LoopFrames ) );
	}

	/// <summary>Once a frame: the played-through go idle, and the ones that follow are put where their particle is.</summary>
	public void Update()
	{
		for ( var i = playing.Count - 1; i >= 0; i-- )
		{
			var child = playing[i];

			if ( !child.Object.IsValid() || !child.Player.IsValid() )
			{
				playing.RemoveAt( i );
				continue;
			}

			if ( child.Began < EngineLoop.LoopFrames && !child.Player.IsActive )
			{
				playing.RemoveAt( i );
				idle.Push( child.Player );
				continue;
			}

			if ( child.Follows is not null ) child.Object.WorldPosition = child.Follows.Position;
		}
	}

	/// <summary>A particle has died: what followed it stays where it is - the particle goes back to the pool and would be another's next.</summary>
	public void LetGo( Particle p )
	{
		for ( var i = 0; i < playing.Count; i++ )
		{
			if ( ReferenceEquals( playing[i].Follows, p ) ) playing[i] = playing[i] with { Follows = null };
		}
	}

	/// <summary>Every one destroyed, playing or idle.</summary>
	public void Clear()
	{
		foreach ( var child in playing )
		{
			if ( child.Object.IsValid() ) child.Object.Destroy();
		}

		foreach ( var player in idle )
		{
			if ( player.IsValid() && player.GameObject.IsValid() ) player.GameObject.Destroy();
		}

		playing.Clear();
		idle.Clear();
	}
}
