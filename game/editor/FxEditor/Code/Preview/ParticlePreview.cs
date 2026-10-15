using Rinten.UI;

using UILabel = Rinten.UI.Label;

namespace Editor.FxEditor;

/// <summary>Preview scene with one ParticlePlayer, running the same code as the game; rebuilds on Revision</summary>
public sealed class ParticlePreview : PanelDockHost
{
	readonly ParticlePreviewPanel _panel;

	public ParticlePreview( ParticleEditorWindow window ) : base( null )
	{
		Name = "Preview";
		WindowTitle = "Preview";
		SetWindowIcon( "visibility" );

		_panel = new ParticlePreviewPanel( window );

		SetContent( _panel );
	}

	/// <summary>What is being played.</summary>
	public void SetDefinition( ParticleDefinition definition ) => _panel.SetDefinition( definition );

	/// <summary>Play it from the beginning.</summary>
	public void Restart() => _panel.Restart();

	/// <summary>The timeline's rows, built again from the effect.</summary>
	public void RebuildTimeline() => _panel.RebuildTimeline();

	/// <summary>A parameter's default moved: the effect reads it again where it stands.</summary>
	public void RefreshParameters() => _panel.RefreshParameters();
}

//The toolbar, the picture, and the count in the corner.
[StyleSheet( "ParticlePreview.cs.scss" )]
internal sealed class ParticlePreviewPanel : EditorPanel
{
	readonly ParticleEditorWindow _window;

	readonly PreviewView _view;
	readonly UILabel _stats;
	readonly PanelToolbarButton _pause;

	Scene _scene;
	CameraComponent _camera;
	GameObject _playerObject;
	ParticlePlayer _player;

	ParticleDefinition _definition;

	Vector2 _angles = new( 135.0f, 20.0f );
	Vector3 _origin;
	float _distance = 3.0f;
	float _reach = 3.0f;

	bool _orbit;
	bool _zoom;
	bool _pan;
	bool _paused;

	float _speed = 1.0f;

	public ParticlePreviewPanel( ParticleEditorWindow window )
	{
		_window = window;

		AddClass( "particle-preview" );

		BuildScene();

		var bar = AddChild<PanelToolbar>();

		bar.AddIcon( "replay", "Restart", Restart );

		_pause = bar.AddToggle( "pause", "Pause", false, paused =>
		{
			_paused = paused;
		} );

		bar.AddSeparator();

		bar.AddText( "0.25x", "Quarter speed", () => SetSpeed( 0.25f ) );
		bar.AddText( "1x", "Normal speed", () => SetSpeed( 1.0f ) );
		bar.AddText( "2x", "Double speed", () => SetSpeed( 2.0f ) );

		bar.AddIcon( "skip_next", "Step a frame", () =>
		{
			if ( !_paused ) { _paused = true; _pause.IsActive = true; }
			using ( _scene.Push() ) _player?.Advance( 1.0f / 60.0f );
		} );

		bar.AddSeparator();

		bar.AddIcon( "center_focus_strong", "Frame", Frame );

		bar.AddSeparator();

		//Once through, and the loop region: what a one-shot looks like in a game, and a second of it over and over while a curve is tuned.
		_onceToggle = bar.AddToggle( "looks_one", "Play once", false, once =>
		{
			_playOnce = once;
			if ( _player.IsValid() ) { _player.PlayOnce = once; Restart(); }
		} );

		_loopToggle = bar.AddToggle( "repeat", "Loop region", false, on =>
		{
			if ( _timeline is not null ) _timeline.LoopOn = on;
		} );

		bar.AddSeparator();

		//A floor to hit. Off by default so a preview is the effect alone, on for anything
		//that collides - debris, rain, a meteor - which has nothing to land on otherwise.
		_groundToggle = bar.AddToggle( "horizontal_rule", "Ground", EditorCookie.Get( GroundCookie, false ), SetGround );

		bar.AddStretch();

		var frame = AddChild<Panel>( "viewport" );

		_view = frame.AddChild( new PreviewView( this ) { Scene = _scene } );
		_view.AddClass( "view" );

		//The engine's own grid, which is how the scene view draws one too.
		_view.ShowEditorOverlays = true;
		_view.OnPreFrame += PreFrame;

		_stats = frame.AddChild<UILabel>( "stats" );
		_stats.Tokenize = false;

		_timeline = AddChild( new TimelinePanel( this, window ) );
	}

	PanelToolbarButton _onceToggle;
	PanelToolbarButton _loopToggle;
	TimelinePanel _timeline;
	bool _playOnce;

	/// <summary>Seconds the effect has run, for the playhead.</summary>
	public float Elapsed => _player.IsValid() ? _player.Elapsed : 0.0f;

	/// <summary>The strip's rows, built again - after an edit, and when the effect changes.</summary>
	public void RebuildTimeline() => _timeline?.Rebuild();

	/// <summary>Put the effect at a second, paused - what dragging the ruler does.</summary>
	public void Scrub( float seconds )
	{
		if ( !_player.IsValid() ) return;

		if ( !_paused ) { _paused = true; _pause.IsActive = true; }

		using ( _scene.Push() )
			_player.Seek( seconds );
	}

	const string GroundCookie = "fxeditor.preview.ground";

	PanelToolbarButton _groundToggle;
	GameObject _ground;

	//Put a floor under the effect, or take it away. A real box with a collider in the
	//preview scene, so a collision module hits it the way it hits a floor in a game.
	void SetGround( bool on )
	{
		EditorCookie.Set( GroundCookie, on );

		if ( !_scene.IsValid() ) return;

		using ( _scene.Push() )
		{
			if ( !on )
			{
				if ( _ground.IsValid() ) _ground.Destroy();
				_ground = null;
				return;
			}

			if ( _ground.IsValid() ) return;

			_ground = new GameObject( true, "ground" );
			_ground.WorldPosition = Vector3.Down * 0.05f;
			_ground.WorldScale = new Vector3( 40.0f, 0.1f, 40.0f );
			_ground.Tags.Add( "world" );

			var renderer = _ground.AddComponent<ModelRenderer>();
			renderer.Model = Model.Cube;
			renderer.Tint = new Color( 0.32f, 0.34f, 0.38f );
			renderer.MaterialOverride = Material.Load( "materials/dev/grid.mat" );

			var collider = _ground.AddComponent<BoxCollider>();
			collider.Static = true;
		}
	}

	void BuildScene()
	{
		//A real scene, so a particle behaves here exactly as it will in a game - collision, prefabs on impact, lights and all.
		_scene = new Scene { Name = "Particle Preview" };

		using ( _scene.Push() )
		{
			var cameraObject = new GameObject( true, "camera" );

			_camera = cameraObject.AddComponent<CameraComponent>();
			_camera.BackgroundColor = Theme.ControlBackground;
			_camera.FieldOfView = 60.0f;
			_camera.ZNear = 0.02f;
			_camera.ZFar = 5000.0f;

			PreviewLighting.HideSky( _camera );
			PreviewLighting.Light( _scene, new Angles( _angles.y, -_angles.x, 0.0f ) );
		}

		if ( EditorCookie.Get( GroundCookie, false ) )
			SetGround( true );

		UpdateCamera();
	}

	/// <summary>What is being played here.</summary>
	public void SetDefinition( ParticleDefinition definition )
	{
		_definition = definition;

		using ( _scene.Push() )
		{
			if ( !_playerObject.IsValid() )
			{
				_playerObject = new GameObject( true, "effect" );
				_player = _playerObject.AddComponent<ParticlePlayer>();
			}

			_player.Definition = definition;
			_player.PlayOnce = _playOnce;

			//Seeded, so a scrub to the same second shows the same picture.
			_player.Seed = 1;
			_player.Rebuild();
		}

		_timeline?.Rebuild();

		Frame();
	}

	/// <summary>Play it from the beginning.</summary>
	public void RefreshParameters()
	{
		if ( _player.IsValid() )
			_player.RefreshParameters();
	}

	public void Restart()
	{
		if ( !_player.IsValid() ) return;

		using ( _scene.Push() )
		{
			_player.Rebuild();
		}
	}

	void SetSpeed( float speed ) => _speed = speed;

	//Stand back far enough to see the whole of it. From the longest lifetime and the fastest thing the modules are throwing,
	//not from a bounding box: an effect that has just been made has no particles in it yet, so there is nothing to measure.
	void Frame()
	{
		_origin = Vector3.Zero;
		_distance = Reach();
		_reach = _distance;
	}

	float Reach()
	{
		if ( _definition is null ) return 3.0f;

		var reach = 1.0f;

		foreach ( var emitter in _definition.Emitters )
		{
			foreach ( var module in emitter.AllModules() )
			{
				reach = module switch
				{
					InitializePositionModule position => MathF.Max( reach, position.Radius + position.BoxSize.Length ),
					InitializeVelocityModule velocity => MathF.Max( reach, velocity.Velocity.Evaluate( null ).Length ),
					_ => reach
				};
			}
		}

		return reach.Clamp( 0.5f, 200.0f ) * 1.5f;
	}

	void PreFrame()
	{
		if ( !_scene.IsValid() ) return;

		using ( _scene.Push() )
		{
			_scene.TimeScale = _paused ? 0.0f : _speed;
			_scene.GameTick( RealTime.Delta );

			//The loop region: past its end, back to its start.
			if ( _timeline is { LoopOn: true } && !_paused && _player.IsValid() )
			{
				var span = _timeline.Span;
				var end = _timeline.LoopEnd * span;

				if ( _player.Elapsed > end )
					_player.Seek( _timeline.LoopStart * span );
			}
		}

		UpdateCamera();
		UpdateStats();
		ApplySolo();
		DrawShapes();
	}

	//The soloed emitter alone, every frame: the player builds its emitter objects again on
	//every edit, so what was switched off has to be switched off again rather than remembered.
	void ApplySolo()
	{
		if ( !_player.IsValid() ) return;

		var solo = _window.SoloEmitter;

		foreach ( var emitter in _player.EmitterObjects )
		{
			if ( !emitter.IsValid() ) continue;
			if ( emitter.GetComponent<ParticleModuleRunner>() is not { } runner ) continue;

			var wanted = solo is null || runner.Data?.Identifier == solo;

			if ( emitter.Enabled != wanted )
				emitter.Enabled = wanted;
		}
	}

	//Draws the selected module's shape in the preview, since nothing there is ever selected
	void DrawShapes()
	{
		if ( !_player.IsValid() ) return;

		var selection = _window.Selection;
		var emitter = _window.EmitterOf( selection );
		var tx = _player.WorldTransform;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 1.5f;

		//The effect's own shapes: the selected one bright, the rest faint, and the one a selected module names lit beside it.
		if ( _definition is not null )
		{
			//Anchors as small frames, the selected one and the one a selected thing names lit; an emitter with a transform of its own as a frame where it sits.
			var anchorNamed = ( selection as GoalModule )?.AnchorName ?? ( selection as ParticleShape )?.Anchor ?? ( selection as ParticleDefinition.Emitter )?.Anchor;

			foreach ( var anchor in _definition.Anchors )
			{
				var lit = ReferenceEquals( anchor, selection )
					|| ( anchorNamed is not null && string.Equals( anchorNamed, anchor.Name, StringComparison.OrdinalIgnoreCase ) );

				DrawFrame( _player.Anchor( anchor.Name ), lit ? 0.35f : 0.2f, lit ? 1.0f : 0.45f );
			}

			if ( emitter is not null && ( emitter.Offset != Vector3.Zero || emitter.Rotation != Angles.Zero || !string.IsNullOrEmpty( emitter.Anchor ) ) )
				DrawFrame( _player.Anchor( emitter.Anchor ).ToWorld( new Transform( emitter.Offset, emitter.Rotation.ToRotation(), emitter.Scale ) ), 0.25f, 0.8f );

			var named = ( selection as InitializePositionModule )?.ShapeName ?? ( selection as FollowShapeModule )?.ShapeName ?? ( selection as GoalModule )?.ShapeName;

			foreach ( var shape in _definition.Shapes )
			{
				var lit = ReferenceEquals( shape, selection )
					|| ( named is not null && string.Equals( named, shape.Name, StringComparison.OrdinalIgnoreCase ) );

				Gizmo.Draw.Color = new Color( 0.95f, 0.75f, 0.35f, lit ? 0.95f : 0.25f );
				Gizmo.Draw.LineThickness = lit ? 2.0f : 1.0f;

				DrawEffectShape( shape, tx );
			}

			Gizmo.Draw.LineThickness = 1.5f;
		}

		if ( emitter is null ) return;

		if ( selection is ParticleModule module )
		{
			DrawShape( module, tx );
			return;
		}

		foreach ( var each in emitter.Modules( ParticleModuleStage.Initialize ) )
		{
			if ( each is InitializePositionModule ) DrawShape( each, tx );
		}
	}

	//Three short axes and a dot: a pose.
	void DrawFrame( Transform tx, float size, float alpha )
	{
		Gizmo.Draw.Color = new Color( 1.0f, 0.3f, 0.3f, alpha );
		Gizmo.Draw.Line( tx.Position, tx.Position + tx.Rotation.Right * size );
		Gizmo.Draw.Color = new Color( 0.3f, 1.0f, 0.3f, alpha );
		Gizmo.Draw.Line( tx.Position, tx.Position + tx.Rotation.Up * size );
		Gizmo.Draw.Color = new Color( 0.3f, 0.5f, 1.0f, alpha );
		Gizmo.Draw.Line( tx.Position, tx.Position + tx.Rotation.Forward * size );
		Gizmo.Draw.Color = new Color( 0.4f, 0.9f, 0.9f, alpha );
		Gizmo.Draw.LineSphere( tx.Position, size * 0.12f, 8 );
	}

	//One of the effect's shapes, drawn as its own outline where the emitter would put it.
	void DrawEffectShape( ParticleShape shape, Transform tx )
	{
		//From its anchor where it hangs from one - what SampleWorld does.
		if ( !string.IsNullOrEmpty( shape.Anchor ) && _player.IsValid() )
			tx = _player.Anchor( shape.Anchor );

		//A line between anchors is wherever the anchors are, whatever frame the rest of the shape has.
		if ( shape.Kind == ParticleShape.ShapeKind.Line && _player.IsValid() && ( !string.IsNullOrEmpty( shape.LineStartAnchor ) || !string.IsNullOrEmpty( shape.LineEndAnchor ) ) )
		{
			var a = shape.SampleWorld( _player, ParticleShapeSample.ByTime, 0, 1, 0.0f ).Position;
			var b = shape.SampleWorld( _player, ParticleShapeSample.ByTime, 0, 1, 1.0f ).Position;

			Gizmo.Draw.Line( a, b );
			Gizmo.Draw.LineSphere( a, 0.03f );
			Gizmo.Draw.LineSphere( b, 0.03f );
			return;
		}

		var rotation = tx.Rotation * shape.Rotation.ToRotation();
		var at = tx.Position + ( tx.Rotation * ( shape.Offset * tx.Scale ) );

		using var scope = Gizmo.Scope( $"shape-{shape.Identifier}", new Transform( at, rotation, tx.Scale ) );

		switch ( shape.Kind )
		{
			case ParticleShape.ShapeKind.Sphere:
				Gizmo.Draw.LineSphere( Vector3.Zero, shape.Radius );
				break;

			case ParticleShape.ShapeKind.Box:
				Gizmo.Draw.LineBBox( BBox.FromPositionAndSize( Vector3.Zero, shape.BoxSize ) );
				break;

			case ParticleShape.ShapeKind.Circle:
				Gizmo.Draw.LineCircle( Vector3.Zero, Vector3.Up, shape.Radius, sections: 32 );
				break;

			case ParticleShape.ShapeKind.Cone:
			{
				var mouth = MathF.Tan( shape.ConeAngle.DegreeToRadian() ) * shape.Radius;

				Gizmo.Draw.LineCircle( Vector3.Up * shape.Radius, Vector3.Up, mouth, sections: 32 );

				for ( var i = 0; i < 4; i++ )
				{
					var angle = i * MathF.PI * 0.5f;
					Gizmo.Draw.Line( Vector3.Zero, new Vector3( MathF.Cos( angle ) * mouth, shape.Radius, MathF.Sin( angle ) * mouth ) );
				}

				break;
			}

			case ParticleShape.ShapeKind.Line:
				Gizmo.Draw.Line( shape.LineStart, shape.LineEnd );
				Gizmo.Draw.LineSphere( shape.LineStart, 0.03f );
				Gizmo.Draw.LineSphere( shape.LineEnd, 0.03f );
				break;

			case ParticleShape.ShapeKind.Spline:
			{
				var steps = Math.Max( 8, shape.Points.Count * 12 );
				var last = shape.Spline( 0.0f ).Position;

				for ( var i = 1; i <= steps; i++ )
				{
					var next = shape.Spline( i / (float)steps ).Position;
					Gizmo.Draw.Line( last, next );
					last = next;
				}

				foreach ( var point in shape.Points )
					Gizmo.Draw.LineSphere( point, 0.04f, 8 );

				break;
			}

			case ParticleShape.ShapeKind.Grid:
			{
				var cx = Math.Max( 1, (int)shape.GridCount.x );
				var cy = Math.Max( 1, (int)shape.GridCount.y );
				var cz = Math.Max( 1, (int)shape.GridCount.z );
				var shown = 0;

				for ( var iz = 0; iz < cz && shown < 512; iz++ )
				for ( var iy = 0; iy < cy && shown < 512; iy++ )
				for ( var ix = 0; ix < cx && shown < 512; ix++, shown++ )
				{
					var p = new Vector3(
						( ix - ( cx - 1 ) * 0.5f ) * shape.GridSpacing.x,
						( iy - ( cy - 1 ) * 0.5f ) * shape.GridSpacing.y,
						( iz - ( cz - 1 ) * 0.5f ) * shape.GridSpacing.z );

					Gizmo.Draw.Line( p + Vector3.Left * 0.02f, p + Vector3.Right * 0.02f );
					Gizmo.Draw.Line( p + Vector3.Down * 0.02f, p + Vector3.Up * 0.02f );
				}

				break;
			}

			case ParticleShape.ShapeKind.Model when shape.Model is { } model:
			{
				var scale = shape.ModelScale;

				Gizmo.Draw.LineBBox( new BBox( model.Bounds.Mins * scale, model.Bounds.Maxs * scale ) );

				//Each piece's middle - where a particle born by Parts is, and where a Goal back home takes it.
				if ( shape.ModelSampling == ParticleShape.ModelSample.Parts )
				{
					for ( var i = 0; i < model.MeshCount; i++ )
					{
						var c = model.MeshBounds( i ).Center * scale;

						Gizmo.Draw.Line( c + Vector3.Left * 0.03f, c + Vector3.Right * 0.03f );
						Gizmo.Draw.Line( c + Vector3.Down * 0.03f, c + Vector3.Up * 0.03f );
						Gizmo.Draw.Line( c + Vector3.Backward * 0.03f, c + Vector3.Forward * 0.03f );
					}
				}

				break;
			}

			case ParticleShape.ShapeKind.Helix:
			{
				var steps = (int)( shape.Turns * 24 ).Clamp( 16, 400 );
				Vector3 last = default;

				for ( var i = 0; i <= steps; i++ )
				{
					var t = i / (float)steps;
					var angle = t * shape.Turns * MathF.PI * 2.0f;
					var next = new Vector3( MathF.Cos( angle ) * shape.Radius, t * shape.Height, MathF.Sin( angle ) * shape.Radius );

					if ( i > 0 ) Gizmo.Draw.Line( last, next );
					last = next;
				}

				break;
			}

			default:
				Gizmo.Draw.Line( Vector3.Left * 0.1f, Vector3.Right * 0.1f );
				Gizmo.Draw.Line( Vector3.Down * 0.1f, Vector3.Up * 0.1f );
				Gizmo.Draw.Line( Vector3.Backward * 0.1f, Vector3.Forward * 0.1f );
				break;
		}
	}

	void DrawShape( ParticleModule module, Transform tx )
	{
		switch ( module )
		{
			case InitializePositionModule position when position.UsesEffectShape:
				break;

			case InitializePositionModule position:
			{
				Gizmo.Draw.Color = new Color( 0.35f, 0.8f, 0.4f, 0.9f );

				var follows = position.Follows;
				var rotation = follows.HasFlag( InitializePositionModule.ShapeTransform.Rotation ) ? tx.Rotation : Rotation.Identity;
				var scale = follows.HasFlag( InitializePositionModule.ShapeTransform.Scale ) ? tx.Scale : Vector3.One;
				var at = tx.Position + ( rotation * ( position.Offset * scale ) );

				using var scope = Gizmo.Scope( "spawn", new Transform( at, rotation, scale ) );

				switch ( position.Shape )
				{
					case InitializePositionModule.SpawnShape.Sphere:
						Gizmo.Draw.LineSphere( Vector3.Zero, position.Radius );
						break;

					case InitializePositionModule.SpawnShape.Box:
						Gizmo.Draw.LineBBox( BBox.FromPositionAndSize( Vector3.Zero, position.BoxSize ) );
						break;

					case InitializePositionModule.SpawnShape.Circle:
						Gizmo.Draw.LineCircle( Vector3.Zero, Vector3.Up, position.Radius, sections: 32 );
						break;

					case InitializePositionModule.SpawnShape.Cone:
					{
						//A cone standing on its point, opening upward - see InCone.
						var mouth = MathF.Tan( position.ConeAngle.DegreeToRadian() ) * position.Radius;

						Gizmo.Draw.LineCircle( Vector3.Up * position.Radius, Vector3.Up, mouth, sections: 32 );

						for ( var i = 0; i < 4; i++ )
						{
							var angle = i * MathF.PI * 0.5f;
							Gizmo.Draw.Line( Vector3.Zero, new Vector3( MathF.Cos( angle ) * mouth, position.Radius, MathF.Sin( angle ) * mouth ) );
						}

						break;
					}

					case InitializePositionModule.SpawnShape.Line:
						Gizmo.Draw.Line( position.LineStart, position.LineEnd );
						Gizmo.Draw.LineSphere( position.LineStart, 0.03f );
						Gizmo.Draw.LineSphere( position.LineEnd, 0.03f );
						break;

					default:
						Gizmo.Draw.Line( Vector3.Left * 0.1f, Vector3.Right * 0.1f );
						Gizmo.Draw.Line( Vector3.Down * 0.1f, Vector3.Up * 0.1f );
						Gizmo.Draw.Line( Vector3.Backward * 0.1f, Vector3.Forward * 0.1f );
						break;
				}

				break;
			}

			case OrbitModule orbit:
			{
				Gizmo.Draw.Color = new Color( 0.35f, 0.62f, 1.0f, 0.9f );

				var (center, axis) = Frame( orbit.Space, orbit.Center, orbit.Axis.Evaluate( null, _player ), tx );

				if ( axis.IsNearlyZero() ) break;

				Gizmo.Draw.Line( center - axis.Normal * 0.5f, center + axis.Normal * 0.5f );

				for ( var radius = 0.5f; radius <= 2.0f; radius += 0.5f )
					Gizmo.Draw.LineCircle( center, axis.Normal, radius, sections: 32 );

				break;
			}

			case VortexModule vortex:
			{
				Gizmo.Draw.Color = new Color( 0.35f, 0.62f, 1.0f, 0.9f );

				var (center, axis) = Frame( vortex.Space, vortex.Center, vortex.Axis.Evaluate( null, _player ), tx );

				if ( axis.IsNearlyZero() ) break;

				Gizmo.Draw.Line( center - axis.Normal, center + axis.Normal );
				Gizmo.Draw.LineCircle( center, axis.Normal, 1.0f, sections: 32 );

				break;
			}

			case AttractorModule attractor:
			{
				Gizmo.Draw.Color = attractor.Invert ? new Color( 1.0f, 0.5f, 0.3f, 0.9f ) : new Color( 1.0f, 0.85f, 0.3f, 0.9f );

				var (center, _) = Frame( attractor.Space, attractor.Position.Evaluate( null, _player ), Vector3.Up, tx );

				Gizmo.Draw.LineSphere( center, attractor.Size, 12 );
				Gizmo.Draw.Line( center + Vector3.Left * 0.1f, center + Vector3.Right * 0.1f );
				Gizmo.Draw.Line( center + Vector3.Down * 0.1f, center + Vector3.Up * 0.1f );

				break;
			}
		}
	}

	//A local-space point and axis put into the world, the way the modules do it.
	static (Vector3 Center, Vector3 Axis) Frame( ParticleSpace space, Vector3 center, Vector3 axis, Transform tx )
	{
		if ( space != ParticleSpace.Local ) return (center, axis);

		return (tx.Position + ( tx.Rotation * center ), tx.Rotation * axis);
	}

	void UpdateStats()
	{
		if ( _definition is null )
		{
			PanelText.Set( _stats, "No effect open" );
			return;
		}

		var alive = 0;
		var most = 0;

		foreach ( var emitter in _player.IsValid() ? _player.EmitterObjects : [] )
		{
			if ( emitter?.GetComponent<ParticleEffect>() is not { } effect ) continue;

			alive += effect.Particles.Count;
			most += effect.MaxParticles;
		}

		PanelText.Set( _stats, $"{_definition.Emitters.Count} emitters · {alive} / {most} particles" );
	}

	//The camera

	void UpdateCamera()
	{
		if ( !_camera.IsValid() ) return;

		//The pointer's own movement since the last frame, rather than the distance from where the button went down: a Wayland client
		//cannot put the pointer back, so the second reading keeps adding the whole offset again and the camera turns on its own.
		var delta = Dragging ? Application.CursorDelta : Vector2.Zero;

		if ( Dragging && _view.IsValid() && SceneEditorExtensions.LockCursorToCanvas( _view ) )
			delta = Vector2.Zero;

		if ( _orbit )
		{
			_angles.x = ( _angles.x + delta.x * 0.3f ).NormalizeDegrees();
			_angles.y = ( _angles.y + delta.y * 0.3f ).Clamp( -89.0f, 89.0f );
		}
		else if ( _zoom )
		{
			Zoom( delta.y * 0.02f );
		}
		else if ( _pan )
		{
			//What a pixel is worth out where the effect is: a fixed number of units per pixel drags a small effect off the screen and barely moves a large one.
			var perPixel = _distance * 0.002f;
			var pan = _camera.WorldRotation.Right * delta.x * perPixel
				+ _camera.WorldRotation.Down * delta.y * perPixel;

			if ( !EditorPreferences.CameraInvertPan ) pan = -pan;

			_origin += pan;
		}

		//Eased rather than jumped, so a notch of the wheel reads as moving closer rather than as a cut.
		_reach = _reach.LerpTo( _distance, RealTime.Delta * 15.0f );

		_camera.WorldRotation = new Angles( _angles.y, -_angles.x, 0.0f );
		_camera.WorldPosition = _origin + _camera.WorldRotation.Backward * _reach;
	}

	bool Dragging => _orbit || _zoom || _pan;

	void Zoom( float amount ) => _distance = ( _distance * ( 1.0f + amount ) ).Clamp( 0.1f, 500.0f );

	void ViewMouseDown( MousePanelEvent e )
	{
		switch ( e.MouseButton )
		{
			case MouseButtons.Left: _orbit = true; break;
			case MouseButtons.Right: _zoom = true; break;
			case MouseButtons.Middle: _pan = true; break;
		}
	}

	void ViewMouseUp( MousePanelEvent e )
	{
		switch ( e.MouseButton )
		{
			case MouseButtons.Left: _orbit = false; break;
			case MouseButtons.Right: _zoom = false; break;
			case MouseButtons.Middle: _pan = false; break;
		}
	}

	void ViewMouseWheel( Vector2 value ) => Zoom( -value.y * 0.1f );

	public override void OnDeleted()
	{
		base.OnDeleted();

		_scene?.Destroy();
		_scene = null;
	}

	//The picture owns the camera input, so a click on the toolbar above it never starts an orbit.
	sealed class PreviewView : SceneRenderingPanel
	{
		readonly ParticlePreviewPanel _owner;

		public PreviewView( ParticlePreviewPanel owner ) => _owner = owner;

		protected override void OnMouseDown( MousePanelEvent e )
		{
			base.OnMouseDown( e );

			_owner.ViewMouseDown( e );
		}

		protected override void OnMouseUp( MousePanelEvent e )
		{
			base.OnMouseUp( e );

			_owner.ViewMouseUp( e );
		}

		/// <summary>The wheel dollies the camera. Deliberately not the base behaviour: that hands the notch to the game, and there is no game in a preview.</summary>
		public override void OnMouseWheel( Vector2 value ) => _owner.ViewMouseWheel( value );
	}
}
