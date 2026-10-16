using Rinten.Utility;
using static Rinten.Component;

namespace Rinten;

public partial class Scene : GameObject
{
	FixedUpdate fixedUpdate = new FixedUpdate();
	public bool IsFixedUpdate { get; private set; }

	public float FixedDelta => (float)fixedUpdate.Delta;

	[Obsolete( "Moved to Rinten.ProjectSettings.PhysicsSettings" )] public float FixedUpdateFrequency { get; set; } = 50.0f;
	[Obsolete( "Moved to Rinten.ProjectSettings.PhysicsSettings" )] public int MaxFixedUpdates { get; set; } = 5;
	[Obsolete( "Moved to Rinten.ProjectSettings.PhysicsSettings" )] public int PhysicsSubSteps { get; set; }
	[Obsolete( "Unused. Animation is always threaded." )] public bool ThreadedAnimation { get; set; } = true;
	[Obsolete( "Moved to Rinten.ProjectSettings.PhysicsSettings" )] public bool UseFixedUpdate { get; set; }

	[Property, Range( 0, 1 )] public float TimeScale { get; set; } = 1.0f;

	//Set to 0 to freeze the clock for comparable perf captures (e.g. -perfconvar "host_timescale 0")
	/// <summary>A multiplier over every scene's own <see cref="TimeScale"/>.</summary>
	[ConVar( "host_timescale", Min = 0.0f, Max = 16.0f,
		Help = "Scales the game clock of every scene. Nought stops it, which is how two shots of a moving scene are made comparable." )]
	internal static float HostTimeScale { get; set; } = 1.0f;

	/// <summary>Where a stopped game clock stands, in seconds, so runs freeze at the same moment; 0 leaves it.</summary>
	[ConVar( "host_time", Min = 0.0f, Max = 1000000.0f,
		Help = "With host_timescale 0, sets the stopped game clock of every scene to this many seconds, so two runs stop at the same moment. Nought leaves it where it stopped." )]
	internal static float HostTime { get; set; }

	//The update loop will turn certain settings on Here we turn them to their defaults.
	void PreTickReset()
	{
		//Forward our preference to the Scene's PhysicsWorld Access the backing fields - ticking shouldn't force these worlds to exist
		if ( _physicsWorld.IsValid() )
		{
			_physicsWorld.SubSteps = Rinten.ProjectSettings.Physics.SubSteps;
		}

		if ( _sceneWorld is not null )
		{
			_sceneWorld.GradientFog.Enabled = false;
		}
	}

	double estimatedServerTime;

	//Update the current time from the host
	internal void UpdateTimeFromHost( double time )
	{
		estimatedServerTime = time;

		if ( TimeNow == 0f )
		{
			TimeNow = time;
		}
	}

	internal double TimeNow { get; private set; }
	internal double TimeDelta { get; private set; } = 0.1;

	//Tick counts per editor scene since last read, to tell if background tabs are being ticked
	internal static readonly Dictionary<string, int> EditorTicks = new();

	public void EditorTick( float timeNow, float timeDelta )
	{
		//Only tick here if we're an editor scene The game will tick a game scene!
		if ( !IsEditor || !IsValid )
			return;

		//Counted only while a report is being written. It is a lock, a string picked out of two nullable properties
		//and a dictionary write, on the editor's own tick, once a frame per open scene - for one line of one report.
		if ( Diagnostics.PerfReport.Measuring )
		{
			lock ( EditorTicks )
			{
				var name = Source?.ResourcePath ?? Name ?? "untitled";

				EditorTicks.TryGetValue( name, out var seen );
				EditorTicks[name] = seen + 1;
			}
		}

		TimeNow = timeNow;
		TimeDelta = timeDelta;

		using var timeScope = Time.ScopeInternal( TimeNow, TimeDelta );
		using var gizmoScope = gizmoInstance.PushInternal();

		SharedTick();

		using ( PerformanceStats.Timings.NavMesh.Scope() )
		{
			Nav_Update();
		}
	}

	public void EditorDraw()
	{
		//The two halves named apart. Together they are most of the editor's frame in a workspace with several viewports - twelve
		//milliseconds of a twenty millisecond frame in Mapping - and "the gizmo frame is expensive" is not a sentence anybody can act on.
		using ( Diagnostics.Performance.Scope( "Gizmo Debug Draw" ) )
		{
			DebugDraw();
		}

		using ( Diagnostics.Performance.Scope( "Gizmo Walk" ) )
		{
			DrawGizmos();
		}
	}

	//Run OnStart on all components that haven't had OnStart called yet
	internal void RunPendingStarts()
	{
		foreach ( var c in pendingStartComponents.EnumerateLocked() )
		{
			if ( !c.IsValid() )
				continue;

			c.InternalOnStart();
		}
	}

	internal void InternalUpdate()
	{
		RunPendingStarts();

		Signal( GameObjectSystem.Stage.Interpolation );

		foreach ( var c in updateComponents.EnumerateLocked( true ) ) c.InternalUpdate();
	}

	List<CameraComponent> _cameraViewScratch = new();

	//Composes every enabled camera's view - the one point in the frame where the camera moves. Runs after Update and bone merging, before PreRender.
	void UpdateCameraViews()
	{
		//Snapshot - a modifier could add or remove cameras while we iterate.
		_cameraViewScratch.Clear();
		_cameraViewScratch.AddRange( Cameras );

		//One sorted modifier set serves every camera this tick.
		var modifiers = IsEditor ? null : CameraComponent.GatherModifiers( this );

		foreach ( var camera in _cameraViewScratch )
		{
			if ( !camera.IsValid() || !camera.Active )
				continue;

			camera.ComposeView( modifiers );
		}
	}

	internal void PreRender()
	{
		foreach ( var c in preRenderComponents.EnumerateLocked() ) c.OnPreRenderInternal();
	}

	static Superluminal _updateTimer = new Superluminal( "Scene.Update", Color.Cyan );
	static Superluminal _preRenderTimer = new Superluminal( "Scene.PreRender", Color.Cyan );
	static Superluminal _signalUpdateBones = new Superluminal( "Signal.UpdateBones", Color.Cyan );
	static Superluminal _signalStarthUpdate = new Superluminal( "Signal.StartUpdate", Color.Cyan );
	static Superluminal _signalFinishUpdate = new Superluminal( "Signal.FinishUpdate", Color.Cyan );

	private void FixedUpdate()
	{
		if ( !ProjectSettings.Physics.UseFixedUpdate )
		{
			InternalFixedUpdate();
		}
		else
		{
			fixedUpdate.Frequency = ProjectSettings.Physics.FixedUpdateFrequency;

			IsFixedUpdate = true;
			fixedUpdate.Run( InternalFixedUpdate, Time.NowDouble, ProjectSettings.Physics.MaxFixedUpdates );
			IsFixedUpdate = false;
		}
	}

	//Cached stage delegates, so SharedTick doesn't allocate new ones every frame per scene
	static readonly System.Action<ISceneStage> stageStart = x => x.Start();
	static readonly System.Action<ISceneStage> stageEnd = x => x.End();

	void SharedTick()
	{
		using ( Diagnostics.Performance.Scope( "scene stage start" ) )
		{
			Scene.RunEvent( stageStart );
		}

		if ( !IsEditor )
		{
			using ( PerformanceStats.Timings.Network.Scope() )
			{
				SceneNetworkUpdate();
			}
		}

		using ( Diagnostics.Performance.Scope( "scene fixed" ) )
		{
			FixedUpdate();
		}

		{
			using ( Diagnostics.Performance.Scope( "scene deletes" ) )
			{
				ProcessDeletes();
			}

			using ( _signalStarthUpdate.Start() )
			{
				//What was drawn for one frame goes, and what was drawn for longer is a frame older - before anything draws this frame's.
				gizmoInstance?.Draws.Age( Time.Delta );

				Signal( GameObjectSystem.Stage.StartUpdate );
			}

			using ( _updateTimer.Start() )
			using ( PerformanceStats.Timings.Update.Scope() )
			{
				PreTickReset();
				InternalUpdate();
			}

			using ( _signalUpdateBones.Start() )
			{
				Signal( GameObjectSystem.Stage.UpdateBones );
			}

			//The cameras' views become final here - PreRender and rendering read a settled camera.
			using ( Diagnostics.Performance.Scope( "scene camera views" ) )
			{
				UpdateCameraViews();
			}

			if ( !Application.IsHeadless )
			{
				using ( _preRenderTimer.Start() )
				using ( PerformanceStats.Timings.Render.Scope() )
				{
					PreRender();
				}
			}

			using ( Diagnostics.Performance.Scope( "scene deletes" ) )
			{
				ProcessDeletes();
			}

			using ( _signalFinishUpdate.Start() )
			{
				Signal( GameObjectSystem.Stage.FinishUpdate );
			}
		}

		using ( Diagnostics.Performance.Scope( "scene stage end" ) )
		{
			Scene.RunEvent( stageEnd );
		}

		if ( !IsEditor )
		{
			using ( PerformanceStats.Timings.Async.Scope() )
			{
				SyncContext.FrameStage.PreRender.Trigger();
			}
		}

	}

	internal void SyncServerTime()
	{
		if ( Networking.IsHost ) return;

		//Estimate what the server time is now
		estimatedServerTime += TimeDelta;

		//How far off are we?
		var timeDifference = Math.Abs( TimeNow - estimatedServerTime );

		//If the time difference is large, snap to it
		if ( timeDifference > 0.25f )
		{
			TimeNow = estimatedServerTime;
			return;
		}

		//Smoothly lerp to the correct time The larger the difference, the faster we lerp
		TimeNow = MathX.Lerp( TimeNow, estimatedServerTime, RealTime.Delta + timeDifference );
	}

	internal void UpdateTime( double delta )
	{
		if ( delta <= 0.0 ) return;

		TimeDelta = delta * TimeScale * HostTimeScale;
		TimeNow += TimeDelta;

		//A stopped clock put where the measurement asked - see HostTime.
		if ( HostTimeScale <= 0.0f && HostTime > 0.0f ) TimeNow = HostTime;
	}

	public void GameTick( double timeDelta = 0.1 )
	{
		UpdateTime( timeDelta );

		if ( Camera is not null )
		{
			gizmoInstance.Input.Camera = Camera.SceneCamera;

			UpdateDefaultListener();
		}

		//Where the eye is this frame, once, for everything that does less of something because it is far away - see CoreDetail, which
		//is the one place that says what far means. Before anything ticks, because the systems that ask are the ones about to run.
		LookForDetail();

		using var timeScope = Time.ScopeInternal( TimeNow, TimeDelta );
		using var gizmoScope = gizmoInstance?.PushInternal();

		using ( PerformanceStats.Timings.Async.Scope() )
		{
			SyncContext.FrameStage.Update.Trigger();
		}

		if ( IsLoading )
			return;

		if ( Game.IsPaused && !Game.Stepping() )
			return;

		SharedTick();

		//If we started loading, then try to run a full tick again - because we might be able
		//to immediately finish the load and be ready to render propertly on the next render!
		if ( IsLoading )
		{
			GameTick();
		}

	}

	//This scene's view of itself this tick - what every system in it asks how big
	//something is on the screen. See CoreDetail, whose policies it is given to.
	internal CoreDetail.Look Detail;

	//Computes the detail view once per tick; with no camera, policies act as if right in front of the eye
	private void LookForDetail()
	{
		var camera = Camera;

		//Editor scenes are viewed through the editor camera, which Camera excludes
		if ( !camera.IsValid() ) camera = EditorCamera;

		if ( !camera.IsValid() )
		{
			Detail = CoreDetail.Blind();
			return;
		}

		//In a headset the view is the head's, not the camera's - the camera keeps only its yaw there, so what the head turned towards was "off
		//screen" and stepped at a quarter rate. A field as wide as a headset's, about a hundred and ten degrees, and an eye's height of pixels.
		if ( Game.IsRunningInVR && Input.VR is not null )
		{
			var head = Input.VR.Head;
			var side = MathF.Tan( MathX.DegreeToRadian( 55.0f ) );

			var forward = head.Rotation.Forward;
			var right = head.Rotation.Right * side;
			var up = head.Rotation.Up * side;

			var field = Frustum.FromCorners(
				new Ray( head.Position, ( forward + up - right ).Normal ),
				new Ray( head.Position, ( forward + up + right ).Normal ),
				new Ray( head.Position, ( forward - up + right ).Normal ),
				new Ray( head.Position, ( forward - up - right ).Normal ),
				MathF.Max( camera.ZNear * 0.5f, 0.01f ), camera.ZFar );

			var eyeTall = MathF.Max( VR.VRSystem.EyeRenderTargetSize.y, 1.0f );

			Detail = CoreDetail.Frame( head.Position, field, eyeTall * 0.5f / side );
			return;
		}

		//Half the view's height in pixels over the tangent of half its vertical field of view - the same number the cull
		//carries as a view's eye.w, so that the card and the processor mean one thing by "how big is this on the screen".
		var tall = MathF.Max( camera.ScreenRect.Height, 1.0f );
		var half = MathF.Tan( MathX.DegreeToRadian( MathF.Max( camera.FieldOfView, 1.0f ) * 0.5f ) );

		Detail = CoreDetail.Frame( camera.WorldPosition, camera.GetFrustum(), tall * 0.5f / MathF.Max( half, 0.001f ) );
	}

	Input.Context FixedUpdateInputContext { get; set; } = Input.Context.Create( "Scene.FixedUpdate" );

	static Superluminal _fixedUpdateTimer = new Superluminal( "Scene.FixedUpdate", Color.Cyan );
	static Superluminal _processDeletesTimer = new Superluminal( "ProcessDeletes", Color.Orange );

	internal void InternalFixedUpdate()
	{
		FixedUpdateInputContext.Flip();
		using var _ = FixedUpdateInputContext.PushScope();

		using ( _fixedUpdateTimer.Start() )
		{
			//What was drawn for one fixed update goes, and what is drawn in this one lives until the next.
			gizmoInstance?.Draws.AgeFixed();
			if ( gizmoInstance is not null ) gizmoInstance.InFixedUpdate = true;

			Signal( GameObjectSystem.Stage.StartFixedUpdate );

			//All components that have not had OnStart() called yet ?: Is there a chance this gets called before Update()? Should this even be here

			RunPendingStarts();

			using ( PerformanceStats.Timings.Update.Scope() )
			{
				foreach ( var c in fixedUpdateComponents.EnumerateLocked() )
				{
					if ( !c.IsValid() )
						continue;

					c.InternalFixedUpdate();
				}
			}

			Signal( GameObjectSystem.Stage.PhysicsStep );

			if ( !IsEditor )
			{
				using ( PerformanceStats.Timings.NavMesh.Scope() )
				{
					Nav_Update();
				}
			}

			using ( _processDeletesTimer.Start() )
			{
				ProcessDeletes();
			}

			using ( PerformanceStats.Timings.Async.Scope() )
			{
				SyncContext.FrameStage.FixedUpdate.Trigger();
			}

			Signal( GameObjectSystem.Stage.FinishFixedUpdate );

			if ( gizmoInstance is not null ) gizmoInstance.InFixedUpdate = false;
		}

		Connection.ClearFixedUpdateContextInput();
	}
}
