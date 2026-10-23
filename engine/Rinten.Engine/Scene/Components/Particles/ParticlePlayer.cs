using System.Text.Json.Serialization;

namespace Rinten;

//One hidden, unsaved child per emitter rebuilt from the definition; the engine does the simulating
/// <summary>Plays a <see cref="ParticleDefinition"/>: builds the engine's own particle components from the recipe in the file, and lets whoever spawned it set the parameters the recipe left named.</summary>
[Expose]
[Title( "Particle Player" )]
[Category( "Effects" )]
[Icon( "auto_awesome" )]
[EditorHandle( "materials/gizmo/particles.png" )]
public sealed class ParticlePlayer : Component, Component.ExecuteInEditor, Component.ITemporaryEffect
{
	/// <summary>The effect this plays.</summary>
	[Property]
	public ParticleDefinition Definition
	{
		get;
		set
		{
			if ( field == value ) return;

			field = value;
			_builtRevision = -1;
		}
	}

	/// <summary>Whether the object goes away once every emitter has finished.</summary>
	[Property]
	public bool DestroyOnEnd { get; set; }

	//What an effect spawned by another effect wants - see ParticleEffectSpawner. A preview ordinarily loops everything so the window is never empty, and that is exactly wrong for a burst that is meant to happen where a rocket died and then stop.
	/// <summary>Played through once, whatever the file says about looping - and then, with <see cref="DestroyOnEnd"/>, gone, in the editor as much as in a game.</summary>
	[Property]
	public bool PlayOnce { get; set; }

	/// <summary>Where the effect's anchors are in this scene: each named one bound to an object, or a bone of one. An anchor not listed here sits where the file says, from this player.</summary>
	[Property, Group( "Anchors" ), Title( "Bindings" )]
	public List<ParticleAnchorBinding> AnchorBindings { get; set; } = new();

	/// <summary>One anchor's override pose, and how many times it has been written.</summary>
	public sealed class AnchorOverride
	{
		//Double buffer: the writer fills the unpublished pose then flips, readers never see a partial write
		private Transform first;
		private Transform second;
		private int published;
		private int busy;

		public long Writes;

		/// <summary>The pose, without a lock - see the note on the class.</summary>
		public Transform Value => System.Threading.Volatile.Read( ref published ) == 0 ? first : second;

		/// <summary>A new pose, published whole.</summary>
		public void Write( in Transform value )
		{
			var spin = new System.Threading.SpinWait();

			while ( System.Threading.Interlocked.CompareExchange( ref busy, 1, 0 ) != 0 ) spin.SpinOnce();

			if ( published == 0 )
			{
				second = value;
				System.Threading.Volatile.Write( ref published, 1 );
			}
			else
			{
				first = value;
				System.Threading.Volatile.Write( ref published, 0 );
			}

			Writes++;

			System.Threading.Volatile.Write( ref busy, 0 );
		}
	}

	/// <summary>Anchors moved from code, in the player's space, over what the file says.</summary>
	[Hide]
	public System.Collections.Concurrent.ConcurrentDictionary<string, AnchorOverride> AnchorOverrides { get; set; } = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Bind an anchor to an object, or a bone of one, from code.</summary>
	public void Bind( string name, GameObject target, string bone = null )
	{
		AnchorBindings.RemoveAll( b => string.Equals( b.Name, name, StringComparison.OrdinalIgnoreCase ) );

		if ( target.IsValid() )
			AnchorBindings.Add( new ParticleAnchorBinding { Name = name, Target = target, Bone = bone } );
	}

	/// <summary>Set an anchor's pose from code, in the player's space.</summary>
	public void SetAnchor( string name, Transform local )
	{
		if ( string.IsNullOrEmpty( name ) ) return;

		var slot = AnchorOverrides.GetOrAdd( name, static _ => new AnchorOverride() );

		//Counted on the slot rather than in one number for the whole process: a static counter bumped by every worker
		//thread is one cache line every one of them has to own in turn, which is the thing this is here to measure.
		slot.Write( local );
	}

	//How many anchor poses this player has written, and to how many names.
	internal (long Writes, int Names) AnchorTraffic()
	{
		var writes = 0L;

		foreach ( var slot in AnchorOverrides.Values ) writes += System.Threading.Interlocked.Read( ref slot.Writes );

		return (writes, AnchorOverrides.Count);
	}

	/// <summary>Where an anchor is now, in the world: the bound object or bone, else the override, else the file's own offset from this player. The player itself where the name is unknown, so a reader always has a pose.</summary>
	public Transform Anchor( string name )
	{
		if ( string.IsNullOrEmpty( name ) ) return WorldTransform;

		foreach ( var binding in AnchorBindings )
		{
			if ( !string.Equals( binding.Name, name, StringComparison.OrdinalIgnoreCase ) ) continue;
			if ( !binding.Target.IsValid() ) continue;

			var tx = binding.Target.WorldTransform;

			if ( !string.IsNullOrEmpty( binding.Bone )
				&& binding.Target.GetComponent<SkinnedModelRenderer>() is { } skinned
				&& skinned.GetBoneObject( skinned.Model?.Bones?.GetBone( binding.Bone )?.Index ?? -1 ) is { } boneObject )
			{
				tx = boneObject.WorldTransform;
			}

			return tx.WithPosition( tx.PointToWorld( binding.Offset ) );
		}

		//Read without a lock: an anchor is read by every particle that follows a shape, every step, and written about once a frame - see AnchorOverride.
		if ( AnchorOverrides.TryGetValue( name, out var slot ) )
			return WorldTransform.ToWorld( slot.Value );

		if ( Definition?.Anchor( name ) is { } anchor )
			return WorldTransform.ToWorld( anchor.Local );

		return WorldTransform;
	}

	/// <summary>Where the effect's random numbers start. Nought is different every time it is built; any other number plays the same every time, which is what a seek on a timeline needs and what a replay wants.</summary>
	[Property]
	public int Seed { get; set; }

	/// <summary>Named floats set for this instance, over whatever the definition says.</summary>
	[Property, Group( "Parameters" ), Title( "Floats" )]
	public Dictionary<string, float> FloatOverrides { get; set; } = new();

	/// <summary>Named vectors set for this instance, over whatever the definition says.</summary>
	[Property, Group( "Parameters" ), Title( "Vectors" )]
	public Dictionary<string, Vector3> VectorOverrides { get; set; } = new();

	/// <summary>Named colours set for this instance, over whatever the definition says.</summary>
	[Property, Group( "Parameters" ), Title( "Colors" )]
	public Dictionary<string, ParticleGradient> ColorOverrides { get; set; } = new();

	/// <summary>How fast this object is moving, in units per second, for a module that wants to hand the emitter's own movement to the particles it makes.</summary>
	[JsonIgnore, Hide]
	public Vector3 Velocity { get; private set; }

	readonly List<GameObject> _emitters = new();

	//Each built emitter's runner, kept with it rather than looked up: the runners are walked several times a frame
	readonly List<ParticleModuleRunner> _runners = new();

	Vector3 _lastPosition;
	int _builtRevision = -1;

	/// <summary>The objects this built, for anything that has to reach into them.</summary>
	[JsonIgnore, Hide]
	public IReadOnlyList<GameObject> EmitterObjects => _emitters;

	//Parameters

	/// <summary>Set a named float for this instance.</summary>
	public void Set( string name, float value )
	{
		FloatOverrides[name] = value;

		PushParameters();
	}

	/// <summary>Set a named vector for this instance.</summary>
	public void Set( string name, Vector3 value )
	{
		VectorOverrides[name] = value;

		PushParameters();
	}

	/// <summary>Set a named colour for this instance.</summary>
	public void Set( string name, Color value ) => SetGradient( name, value );

	//Named apart from the rest rather than another Set : a Color is implicitly a one-colour ParticleGradient, so the two overloads would be ambiguous at every call that passes a colour.
	/// <summary>Set a named colour for this instance, as a gradient over a particle's life.</summary>
	public void SetGradient( string name, ParticleGradient value )
	{
		ColorOverrides[name] = value;

		PushParameters();
	}

	/// <summary>What a named float is worth here - the override, or the definition's default.</summary>
	public float GetFloat( string name )
		=> FloatOverrides.TryGetValue( name, out var value ) ? value : Definition?.FloatDefault( name ) ?? 0.0f;

	/// <summary>What a named vector is worth here - the override, or the definition's default.</summary>
	public Vector3 GetVector( string name )
	{
		if ( VectorOverrides.TryGetValue( name, out var value ) ) return value;

		var definition = Definition;

		if ( definition is not null && definition.HasVector( name ) ) return definition.VectorDefault( name );

		//An anchor's position reads as a vector parameter of the same name - so a shader row, a
		//force, an offset can be told where a bound object or a published pose is, in the world.
		if ( AnchorOverrides.ContainsKey( name ) || definition?.Anchor( name ) is not null || BoundAnchor( name ) )
			return Anchor( name ).Position;

		return Vector3.Zero;
	}

	//Whether this player binds an anchor of that name - see GetVector, which may not allocate.
	private bool BoundAnchor( string name )
	{
		var bindings = AnchorBindings;

		for ( var i = 0; i < bindings.Count; i++ )
		{
			if ( string.Equals( bindings[i].Name, name, StringComparison.OrdinalIgnoreCase ) ) return true;
		}

		return false;
	}

	/// <summary>What a named colour is worth here - the override, or the definition's default.</summary>
	public ParticleGradient GetColor( string name )
		=> ColorOverrides.TryGetValue( name, out var value ) ? value : Definition?.ColorDefault( name ) ?? Color.White;

	/// <summary>Forget one override, so the definition's default is read again.</summary>
	public void Clear( string name )
	{
		FloatOverrides.Remove( name );
		VectorOverrides.Remove( name );
		ColorOverrides.Remove( name );

		PushParameters();
	}

	/// <summary>Forget every override.</summary>
	[Button( "Reset Parameters" ), Group( "Parameters" )]
	public void ClearParameters()
	{
		FloatOverrides.Clear();
		VectorOverrides.Clear();
		ColorOverrides.Clear();

		PushParameters();
	}

	/// <summary>Write every one of the definition's parameters in as an override.</summary>
	public void FillParameters()
	{
		if ( Definition is null ) return;

		foreach ( var parameter in Definition.FloatParameters )
			FloatOverrides[parameter.Name] = parameter.DefaultValue;

		foreach ( var parameter in Definition.VectorParameters )
			VectorOverrides[parameter.Name] = parameter.DefaultValue;

		foreach ( var parameter in Definition.ColorParameters )
			ColorOverrides[parameter.Name] = parameter.DefaultValue;

		PushParameters();
	}

	//Playing

	/// <summary>Start every emitter again from the beginning.</summary>
	public void Restart()
	{
		foreach ( var runner in Runners() )
		{
			runner.Restart();

			foreach ( var emitter in runner.GameObject.GetComponentsInChildren<ParticleEmitter>() )
				emitter.ResetEmitter();
		}
	}

	/// <summary>Fire every emitter's burst again, wherever the effect is standing.</summary>
	public void Burst()
	{
		foreach ( var runner in Runners() )
		{
			foreach ( var emitter in runner.GameObject.GetComponentsInChildren<ParticleEmitter>() )
				emitter.ResetEmitter();
		}
	}

	/// <summary>Build the emitters again from the definition, whatever state they were in.</summary>
	public void Rebuild()
	{
		Teardown();

		if ( Definition?.Emitters is null )
			return;

		_builtRevision = Definition.Revision;

		foreach ( var data in Definition.Emitters )
		{
			if ( !data.Enabled ) continue;

			_emitters.Add( BuildEmitter( data ) );
		}
	}

	GameObject BuildEmitter( ParticleDefinition.Emitter data )
	{
		var target = new GameObject( GameObject );

		target.Name = data.Name;
		target.LocalTransform = new Transform( data.Offset, data.Rotation.ToRotation(), data.Scale );
		//Hidden, not saved (rebuilt from the file) and not networked (built on each machine)
		target.Flags = target.Flags
			.WithFlag( GameObjectFlags.Hidden, true )
			.WithFlag( GameObjectFlags.NotSaved, true )
			.WithFlag( GameObjectFlags.NotNetworked, true );

		var effect = target.GetOrAddComponent<ParticleEffect>();

		effect.MaxParticles = data.MaxParticles;
		effect.StartDelay = data.Delay;
		effect.PreWarm = data.PreWarm;
		effect.TimeScale = data.TimeScale;
		effect.Timing = data.Timing;

		//The components first, so a module that writes onto an emitter in Apply has one to write onto.
		foreach ( var module in data.AllModules() )
		{
			if ( module is IParticleModuleComponents creator && module.Enabled )
				creator.CreateComponents( target );
		}

		//Nothing asked for an emitter, so the effect would never make a particle. A point emitter is the least surprising thing to give it.
		if ( !target.GetComponent<ParticleEmitter>().IsValid() )
			target.AddComponent<ParticleSphereEmitter>().Radius = 0.0f;

		var duration = data.Duration > 0.0f ? data.Duration : Definition.Duration;

		foreach ( var emitter in target.GetComponentsInChildren<ParticleEmitter>() )
		{
			emitter.Duration = duration;
			emitter.Loop = Definition.Looping && !PlayOnce;
			emitter.DestroyOnEnd = false;
		}

		var runner = target.GetOrAddComponent<ParticleModuleRunner>();
		_runners.Add( runner );

		runner.Data = data;
		runner.Player = this;
		runner.ParticleEffect = effect;
		effect.Paused = _paused;

		Apply( target, data );

		//Reset emitters now that modules have configured them; they evaluated burst/delay when enabled
		foreach ( var emitter in target.GetComponentsInChildren<ParticleEmitter>() )
			emitter.ResetEmitter();

		return target;
	}

	/// <summary>Re-applies parameter values without rebuilding emitters; caller ensures structure is unchanged</summary>
	public void RefreshParameters()
	{
		if ( Definition is not null )
			_builtRevision = Definition.Revision;

		PushParameters();
	}

	//Hand every module the effect again, for when a parameter has moved.
	void PushParameters()
	{
		foreach ( var runner in Runners() )
		{
			if ( runner.Data is not null )
				Apply( runner.GameObject, runner.Data );
		}
	}

	void Apply( GameObject target, ParticleDefinition.Emitter data )
	{
		var runner = target.GetComponent<ParticleModuleRunner>();
		var context = runner.IsValid() ? runner.Context( null ) : new ParticleModuleContext
		{
			Effect = target.GetComponent<ParticleEffect>(),
			Emitter = target.GetComponent<ParticleEmitter>(),
			Player = this
		};

		foreach ( var module in data.AllModules() )
		{
			if ( !module.Enabled ) continue;

			try
			{
				module.Apply( context );
			}
			catch ( Exception e )
			{
				Log.Warning( e, $"Particle: '{module.Name ?? module.GetType().Name}' could not be applied" );
			}
		}
	}

	void Teardown()
	{
		foreach ( var emitter in _emitters )
			emitter?.DestroyImmediate();

		_emitters.Clear();
		_runners.Clear();
	}

	//The live runners; one destroyed with its object since is dropped here
	List<ParticleModuleRunner> Runners()
	{
		_runners.RemoveAll( static x => !x.IsValid() );
		return _runners;
	}

	//Component

	protected override void OnEnabled()
	{
		_lastPosition = WorldPosition;

		Rebuild();
	}

	protected override void OnStart()
	{
		//Destroy once every emitter is done; editor previews loop instead of destroying
		if ( DestroyOnEnd && !Scene.IsEditor )
		{
			var temporary = GetOrAddComponent<TemporaryEffect>();

			temporary.DestroyAfterSeconds = 0.0f;
			temporary.WaitForChildEffects = true;
		}
	}

	protected override void OnDisabled() => Teardown();

	/// <summary>Whether the effect stands still. Every emitter's own pause, set from here.</summary>
	[Hide, JsonIgnore]
	public bool Paused
	{
		get => _paused;
		set
		{
			_paused = value;

			foreach ( var runner in Runners() )
			{
				if ( runner.ParticleEffect.IsValid() ) runner.ParticleEffect.Paused = value;
			}
		}
	}

	bool _paused;

	/// <summary>Seconds the effect has run since it was last built - the furthest of its emitters.</summary>
	[Hide, JsonIgnore]
	public float Elapsed
	{
		get
		{
			var most = 0.0f;

			foreach ( var runner in Runners() )
			{
				if ( runner.ParticleEffect.IsValid() ) most = MathF.Max( most, runner.ParticleEffect.Elapsed );
			}

			return most;
		}
	}

	/// <summary>Move the effect on by so many seconds, now, whatever the scene is doing - a frame step while paused, the steps of a seek.</summary>
	public void Advance( float seconds )
	{
		if ( seconds <= 0.0f ) return;

		RideAnchors();

		foreach ( var runner in Runners() )
		{
			var effect = runner.ParticleEffect;
			if ( !effect.IsValid() ) continue;

			var was = effect.Paused;
			effect.Paused = false;
			effect.Step( seconds );
			effect.Paused = was;
		}
	}

	/// <summary>Put the effect at a moment: built again from nothing and stepped there at a fixed rate, so the same second looks the same whichever way the scrub came from - up to the randomness the modules draw.</summary>
	public void Seek( float seconds, float step = 1.0f / 60.0f )
	{
		Rebuild();

		var paused = _paused;
		Paused = false;

		var steps = (int)MathF.Ceiling( MathF.Max( seconds, 0.0f ) / step );

		//Ten thousand steps is nearly three minutes at sixty a second, which is past anything a timeline shows; further than that is a wait.
		for ( var i = 0; i < Math.Min( steps, 10000 ); i++ )
			Advance( step );

		Paused = paused;
	}

	//An emitter that rides an anchor is put where the anchor is, every frame, before the particles step.
	void RideAnchors()
	{
		if ( Definition is null ) return;

		foreach ( var runner in Runners() )
		{
			var data = runner.Data;

			if ( data is null || string.IsNullOrEmpty( data.Anchor ) || !runner.GameObject.IsValid() ) continue;

			//Where the anchor is and which way it faces - not how big the object it is bound to
			//happens to be: a marker ball scaled to a fifth would shrink every particle born on it.
			var at = Anchor( data.Anchor ).WithScale( 1.0f );

			runner.GameObject.WorldTransform = at.ToWorld( new Transform( data.Offset, data.Rotation.ToRotation(), data.Scale ) );
		}
	}

	protected override void OnFixedUpdate()
	{
		RideAnchors();

		Velocity = WorldPosition - _lastPosition;
		_lastPosition = WorldPosition;

		//The definition was edited - by the particle editor, or by a recompile from disk. Nothing tells
		//us; the revision is just different from the one the emitters standing here were built from.
		if ( Definition is not null && Definition.Revision != _builtRevision )
		{
			Rebuild();
			return;
		}

		//Previews always loop, restarting all emitters together so they stay in step
		if ( IsActive ) return;

		//Once through and gone: a spawned effect does not come round again, and it takes its object with it even where a preview would loop.
		if ( PlayOnce )
		{
			if ( DestroyOnEnd && GameObject.IsValid() )
				GameObject.Destroy();

			return;
		}

		if ( Scene.IsEditor )
			Restart();
	}

	//Worked out from the emitters rather than kept as a flag somebody has to remember to lower, so it cannot go stale against what is actually alive.
	/// <summary><see cref="Component.ITemporaryEffect"/>: whether anything is still going.</summary>
	public bool IsActive
	{
		get
		{
			foreach ( var runner in Runners() )
			{
				if ( !runner.IsFinished )
					return true;
			}

			return false;
		}
	}

	void Component.ITemporaryEffect.DisableLooping()
	{
		foreach ( var runner in Runners() )
		{
			foreach ( var emitter in runner.GameObject.GetComponentsInChildren<ParticleEmitter>() )
				emitter.Loop = false;
		}
	}
}
