using Rinten.Audio;
using Rinten.Diagnostics;
using Rinten.Engine;
using Rinten.Internal;
using Rinten.Modals;
using Rinten.UI;
using Rinten.Utility;
using Rinten.VR;
using System;
using System.IO;

namespace Rinten;

internal partial class GameInstanceDll : Engine.IGameInstanceDll
{
	public static GameInstanceDll Current { get; private set; }

	public static GameInstance gameInstance;

	public static PackageLoader PackageLoader { get; set; }
	PackageLoader.Enroller AssemblyEnroller { get; set; }

	private bool _isAssemblyLoadingPaused;
	private CancellationTokenSource _loadGameCts;
	private Task<bool> _loadGameTask;

	public void Bootstrap()
	{
		Current = this;

		GlobalContext.Current.Reset();
		GlobalContext.Current.LocalAssembly = GetType().Assembly;

		using ( var _ = Rinten.Engine.Bootstrap.StartupTiming?.ScopeTimer( "Game PreInit: Type Library" ) )
			Game.InitHost();

		if ( Application.OnWeb )
		{
			//Input first: the context is what turns a key into the action a game asks Input.Down about, and without one an
			//exported game runs and cannot be played. Everything below here wants a project on a disk, which a page does not have.
			SetupInputContext();

			GlobalContext.Current.FileMount = new AggregateFileSystem();
			return;
		}

		using ( var _ = Rinten.Engine.Bootstrap.StartupTiming?.ScopeTimer( "Game PreInit: Input" ) )
			SetupInputContext();

		if ( !OperatingSystem.IsBrowser() )
		{
			using var _ = Rinten.Engine.Bootstrap.StartupTiming?.ScopeTimer( "Game PreInit: Json Upgraders" );
			JsonUpgrader.UpdateUpgraders( TypeLibrary );
		}

		PackageManager.OnPackageInstalledToContext += OnPackageInstalled;

		//Files accessible by game instance
		GlobalContext.Current.FileMount = new AggregateFileSystem();
		{
			if ( Application.IsEditor )
			{
				//If we're in the editor, also mount the cloud folder, which is where we download resources and assets from the backend to.
				FileSystem.Mounted.Mount( EngineFileSystem.LibraryContent );
			}

			if ( Application.IsStandalone )
			{
				//In standalone, we don't ship code - only assets
				FileSystem.Mounted.CreateAndMountIfExists( EngineFileSystem.Addons, $"/base/Assets" );
				FileSystem.Mounted.CreateAndMountIfExists( EngineFileSystem.Root, "/core/" );
			}
			else
			{
				FileSystem.Mounted.CreateAndMountIfExists( EngineFileSystem.Addons, "/base/Assets/" );
				FileSystem.Mounted.CreateAndMountIfExists( EngineFileSystem.Addons, "/base/code/" );
				FileSystem.Mounted.CreateAndMountIfExists( EngineFileSystem.Root, "/core/" );
			}
		}

		PackageLoader?.Dispose();
		if ( !OperatingSystem.IsBrowser() )
		{
			PackageLoader = new PackageLoader( "GameMenu", typeof( GameInstanceDll ).Assembly );
			PackageLoader.HotloadWatch( Game.GameAssembly );
			PackageLoader.OnAfterHotload = OnAfterHotload;
			ConVarSystem.AddAssembly( Game.GameAssembly, "game" );
		}
	}

	public async Task Initialize()
	{
		//Not the type library: Bootstrap built it a moment ago and nothing of a game has been put in it since. A game loaded later resets it whole.
		using ( var _ = Rinten.Engine.Bootstrap.StartupTiming?.ScopeTimer( "Game Init: Reset Environment" ) )
			ResetEnvironment( rebuildTypes: false );

		Networking.StartThread();

		if ( !Application.IsStandalone )
		{
			using var _ = Rinten.Engine.Bootstrap.StartupTiming?.ScopeTimer( "Game Init: Mounts" );
			await Mounting.MountConfig.Mount();
		}
	}

	public void Exiting()
	{
		Networking.Disconnect();
		Networking.StopThread();

		Event.Run( "app.exit" );
		Game.Cookies?.Save();

		//Release InputContext references so the UISystem/PanelRenderer chain (and any RenderAttributes it holds) can be collected.
		if ( InputContext is not null )
		{
			InputContext.KeyboardFocusPanel = null;
			InputContext.MouseFocusPanel = null;
			InputContext = null;
		}
	}

	static int Counter;

	void OnAfterHotload()
	{
		GlobalContext.Current.OnHotload();
		Game.ActiveScene?.OnHotload();
		Event.Run( "hotloaded" );
	}

	/// <summary>This should reset our environment to a clean state.</summary>
	public void ResetEnvironment() => ResetEnvironment( rebuildTypes: true );

	void ResetEnvironment( bool rebuildTypes )
	{
		using var scope = GlobalContext.GameScope();

		Log.Trace( "Game Menu - ResetEnvironment" );


		//Use a new package loader for every game if we're not in editor The editor is only going to load 1 game and ToolsDll has a reference to it
		if ( PackageLoader is not null && !PackageLoader.ToolsMode )
		{
			PackageLoader?.Dispose();
			PackageLoader = null;

			if ( !OperatingSystem.IsBrowser() )
			{
				PackageLoader = new PackageLoader( "GameMenu", typeof( GameInstanceDll ).Assembly );
				PackageLoader.HotloadWatch( Game.GameAssembly );
				PackageLoader.OnAfterHotload = OnAfterHotload;
			}
		}

		ResourceLoader.Clear();

		if ( DidMountNetworkedFiles )
		{
			EngineFileSystem.Mounted.UnMount( NetworkedLargeFiles.Files );
			EngineFileSystem.Mounted.UnMount( NetworkedSmallFiles.Files );
			EngineFileSystem.ProjectSettings.UnMount( NetworkedConfigFiles.Files );
			DidMountNetworkedFiles = false;
		}

		FontManager.Instance.Clear( false );
		FontManager.Instance.LoadAll( FileSystem.Mounted );

		AssemblyEnroller?.Dispose();
		AssemblyEnroller = null;

		CodeArchiveTable.Reset();
		NetworkedSmallFiles.Reset();
		NetworkedConfigFiles.Reset();
		NetworkedLangFiles.Reset();
		NetworkedLargeFiles.Reset();
		ReplicatedConvars.Reset();
		ServerPackages.Clear();

		FileWatchers.ForEach( w => w.Dispose() );
		FileWatchers.Clear();

		NetworkedFileSystem?.Dispose();
		NetworkedFileSystem = null;

		Screen.UpdateFromEngine();

		if ( rebuildTypes ) Game.InitTypeLibrary();

		UserPermission.Load();

		//Use the project's input binds if there is a project, else the engine defaults
		Input.ReadConfig( ProjectSettings.Input );
		StyleSheet.ResetStyleSheets();
		Networking.Reset();
		Connection.Reset();
		GlobalContext.Current.Reset();
		NativeResourceCache.Clear();
		//Windows only - there is no SAPI to reset anywhere else, and nothing has been started that would need it.
		if ( OperatingSystem.IsWindows() ) Speech.Recognition.Reset();
		Json.Initialize();
		VRSystem.Reset();

		if ( !Application.IsEditor )
		{
			Mixer.ResetToDefault();
		}

		Sound.Clear();
		Application.ClearGame();

		ReflectionQueryCache.ClearTypeCache();

		EngineFileSystem.ProjectSettings?.Dispose();
		EngineFileSystem.ProjectSettings = null;

		ProjectSettings.ClearCache();

		AssemblyEnroller = PackageLoader.CreateEnroller( $"gamedll{Counter++}" );

		AssemblyEnroller.OnAssemblyAdded += ( a ) =>
		{
			Assert.NotNull( a.Assembly );

			if ( a.IsEditorAssembly )
				return;

			Game.TypeLibrary.AddAssembly( a.Assembly, true );
			Game.NodeLibrary.AddAssembly( a.Assembly );
			ConVarSystem.AddAssembly( a.Assembly, "game" );
			Cloud.UpdateTypes( a.Assembly );
			JsonUpgrader.UpdateUpgraders( TypeLibrary );

			if ( !a.IsEditorAssembly && a.CodeArchiveBytes is not null )
			{
				AddArchiveToCodeArchiveTable( a );
			}

			ReplicatedConvars.OnAssembliesLoaded();
		};

		AssemblyEnroller.OnAssemblyRemoved += ( a ) =>
		{
			if ( a.IsEditorAssembly )
				return;

			Assert.NotNull( a.Assembly );

			Game.NodeLibrary.RemoveAssembly( a.Assembly );
			Game.TypeLibrary.RemoveAssembly( a.Assembly );
			ConVarSystem.RemoveAssembly( a.Assembly );
			JsonUpgrader.UpdateUpgraders( TypeLibrary );

			CodeArchiveTable.Remove( a.Name );
		};

		AssemblyEnroller.OnAssemblyFastHotload += ( a ) =>
		{
			if ( !a.IsEditorAssembly && a.CodeArchiveBytes is not null )
			{
				AddArchiveToCodeArchiveTable( a );
			}
		};

		IMenuDll.Current?.Reset();

		//Run GC and finalizers to clear any native resources held
		GC.Collect();
		GC.WaitForPendingFinalizers();

		//Run the queue one more time, since some finalizers queue tasks
		MainThread.RunQueues();
	}

	//This method grabs the currently loaded assembly's code archive, strips serverside code and spits it back out into the CodeArchiveTable.
	private void AddArchiveToCodeArchiveTable( LoadedAssembly a )
	{
		if ( a?.CodeArchiveBytes is null )
			return;

		//Only send code archives if we're a developer host (editor) or a dedicated server
		//started with a local project — clients can download remote packages from the backend.
		if ( !Application.IsEditor && !(Application.IsDedicatedServer && Application.GamePackage is LocalPackage) )
			return;

		//If we're not a dedicated server OR this isn't a game assembly, just use the original archive bytes
		if ( !Application.IsDedicatedServer || !a.IsGame )
		{
			CodeArchiveTable.Set( a.Name, a.CodeArchiveBytes );
			return;
		}

		CodeArchiveTable.Set( a.Name, ProjectCompiling.Current?.WithoutServerCode( a.Name ) ?? a.CodeArchiveBytes );
	}


	//Don't attempt to load new assemblies during Tick inside this scope.
	private DisposeAction PauseLoadingAssemblies()
	{
		if ( _isAssemblyLoadingPaused )
		{
			//Don't unpause after this scope if we're already paused

			return default;
		}

		_isAssemblyLoadingPaused = true;

		return new DisposeAction( () =>
		{
			_isAssemblyLoadingPaused = false;
		} );
	}

	public void FinishLoadingAssemblies()
	{
		//None in a page, where a game's code is loaded once and never again.
		PackageLoader?.Tick();

		//we send table updates right after packageloader has run so that any further messages will be read with the same assemblies
		Networking.System?.SendTableUpdates();
	}

	public void CloseGame()
	{
		CancelLoad();

		if ( gameInstance is null ) return;

		using var scope = GlobalContext.GameScope();

		ConVarSystem.SaveAll();

		//Scope disconnect so we can shutdown game before disconnect and stop game objects from sending network destroy, orphaned action should take care of it.
		using ( Networking.DisconnectScope() )
		{
			gameInstance.Shutdown();
			gameInstance = null;
			IGameInstance.Current = null;
		}

		Application.ClearGame();

		LoadingScreen.IsVisible = false;
		LoadingScreen.Media = null;

		Sound.StopAll( 0.2f );

		ResetEnvironment();

		Mounting.MountUtility.TickPreviewRenders();
	}

	internal Input.Context _perFrameInput = Input.Context.Create( "ClientPerFrame" );

	public void Tick()
	{
		var scene = Game.ActiveScene;

		using var sceneScope = scene?.Push();

		if ( scene is not null )
		{
			//Update the time now that we're in the scene scope
			scene.UpdateTime( RealTime.Delta );

			//If we're a client then advance server time
			scene.SyncServerTime();

			//push this time scope now that the scene has updated time
			Time.Update( scene.TimeNow, scene.TimeDelta );
		}

		if ( gameInstance?.WantsToQuit ?? false )
		{
			CloseGame();
		}
		else
		{
			//Update input
			{
				_perFrameInput.Flip();
				_perFrameInput.MakeCurrent();
				Input.Process();
			}

			//Recieve incoming network messages, send heartbeat and other outgoing messages
			Networking.PreFrameTick();

			//We may have got new assemblies in the network update, so finish loading them now before running updates
			if ( !_isAssemblyLoadingPaused )
			{
				FinishLoadingAssemblies();
			}

			//Run the actual game scene tick
			using ( Performance.Scope( "GameFrame" ) )
			{
				//The old scene could be invalid here as a network message may end up destroying it (such as changing a scene)
				if ( scene.IsValid() )
				{
					RunGameFrame( scene );
				}
			}

			Networking.PostFrameTick();

			Connection.ClearUpdateContextInput();
		}

		if ( !Application.IsDedicatedServer )
		{
			Services.Achievements.Tick();
		}

		//Advance per frame scene metrics
		TickSceneStats( scene );

		Analytics.Tick();

		//Run any pending queue'd mainthread tasks here so they're in the same scene scope
		MainThread.RunQueues();
	}

	private void RunGameFrame( Scene activeScene )
	{
		if ( !Game.IsPlaying ) return;
		if ( activeScene is null ) return;
		if ( Networking.IsConnecting ) return;

		activeScene.GameTick( 0 ); //we already advanced time

		//Run any pending queue'd mainthread tasks here so they're in the same scene scope
		MainThread.RunQueues();
	}

	public void SimulateUI()
	{
		bool mouseIsAllowed = true;
		if ( IMenuDll.Current is not null )
		{
			mouseIsAllowed = !IMenuDll.Current.HasOverlayMouseInput();
		}

		using ( Game.ActiveScene?.Push() )
		{
			Game.Language?.Tick();
			GlobalContext.Current.UISystem.Simulate( mouseIsAllowed );

			Game.ActiveScene?.ProcessDeletes();
		}
	}

	public void ClosePopups( object panelClickedOn )
	{
		BasePopup.CloseAll( panelClickedOn as Panel );
	}

	public void Disconnect( string message = null )
	{
		if ( !string.IsNullOrEmpty( message ) )
		{
			if ( Networking.System is { } system ) system.FailureReason ??= message;
			Log.Warning( $"Disconnected: {message.Replace( "\n", "" )}" );
		}

		if ( Networking.IsMatchmaking )
		{
			//don't want any disconnection popups, or to close the game or loading ui - matchmaking should handle all that
			return;
		}

		//cancel any in-progress load right now instead of waiting for tick
		CancelLoad();
		Game.Close();

		if ( !string.IsNullOrEmpty( message ) )
		{
			using var scope = GlobalContext.MenuScope();
			IModalSystem.Current.Notice( "Disconnected", message, "wifi_off" );
		}

		LoadingScreen.IsVisible = false;
	}

	private void CancelLoad()
	{
		_loadGameCts?.Cancel();
		_loadGameCts?.Dispose();
		_loadGameCts = null;
	}

	/// <summary>Loads the game asynchronously. Only one game loads at a time: any load still in flight is cancelled, and this one waits for it to finish tearing down before it starts.</summary>
	public async Task<bool> LoadGamePackageAsync( string ident, GameLoadingFlags flags, CancellationToken ct )
	{
		ThreadSafe.AssertIsMainThread();

		var previous = _loadGameTask;

		CancelLoad();
		_loadGameCts = CancellationTokenSource.CreateLinkedTokenSource( ct );

		_loadGameTask = LoadGamePackageInternalAsync( previous, ident, flags, _loadGameCts.Token );
		return await _loadGameTask;
	}

	//Waits for any previous load to finish tearing down, then runs the load and handles its failure.
	private async Task<bool> LoadGamePackageInternalAsync( Task previous, string ident, GameLoadingFlags flags, CancellationToken token )
	{
		if ( previous is { IsCompleted: false } )
		{
			try
			{
				await previous;
			}
			catch ( System.Exception )
			{
				//The previous load reports its own failures
			}
		}

		//We may have been superseded ourselves while waiting
		if ( token.IsCancellationRequested )
			return false;

		//The previous load hid the loading screen on its way out, it's ours now
		LoadingScreen.IsVisible = true;

		try
		{
			await DoLoadGamePackageAsync( ident, flags, token );

			return !token.IsCancellationRequested;
		}
		catch ( System.Exception e )
		{
			ResetEnvironment();

			if ( e is not OperationCanceledException )
			{
				using ( IMenuDll.Current?.PushScope() )
				{
					IModalSystem.Current?.Notice( "Loading Error", $"An error occurred when loading this game.\n\n{e.Message}", "error" );
				}

				Log.Warning( e, e.Message );

				if ( Application.IsEditor )
				{
					//raise in editor, load has failed and we should alert the user
					throw;
				}
			}

			LoadingScreen.IsVisible = false;
			LoadingScreen.Media = null;
		}

		return false;
	}

	private async Task DoLoadGamePackageAsync( string ident, GameLoadingFlags flags, CancellationToken ct )
	{
		//We might not need to reload if this is the same package. Bit of extra dancing here because we want #local to be treated as released
		if ( gameInstance is not null && !flags.Contains( GameLoadingFlags.Reload ) )
		{
			Package.TryParseIdent( ident, out var iparts );
			Package.TryParseIdent( gameInstance?.Ident, out var oparts );

			//No need to recreate - this is the same package (and the same version)
			if ( iparts.package == oparts.package && iparts.org == oparts.org && iparts.version == oparts.version )
				return;
		}

		//Tear down the current game in its own scope. We may have been called from the menu, and the shutdown clears per-context state.
		using ( GlobalContext.GameScope() )
		{
			gameInstance?.Shutdown();

			//If this isn't part of a remote connection, leave any active network session
			if ( !flags.Contains( GameLoadingFlags.Remote ) )
			{
				Networking.Disconnect();
			}

			Application.ClearGame();
		}

		if ( !Application.IsDedicatedServer && !Application.IsStandalone )
		{
			//Get the stats ready
			var s = Rinten.Services.Stats.GetGlobalStats( ident );
			if ( !s.IsRefreshing )
			{
				_ = s.Refresh();
			}

			//get the local stats too
			var playerStats = Rinten.Services.Stats.GetLocalPlayerStats( ident );
			if ( !playerStats.IsRefreshing )
			{
				_ = playerStats.Refresh();
			}

			await Task.Yield();
			LoadingScreen.Title ??= "Loading..";
		}

		GameInstance newInstance = default;

		try
		{
			if ( Application.IsStandalone )
			{
				newInstance = new StandaloneGameInstance( ident, flags );
			}
			else
			{
				newInstance = new GameInstance( ident, flags );
			}

			using var _ = GlobalContext.GameScope();

			ResetEnvironment();

			if ( !Application.IsDedicatedServer && !Application.IsStandalone )
			{
				await Task.Yield();
			}

			if ( !await newInstance.LoadAsync( AssemblyEnroller, ct ) )
			{
				if ( ct.IsCancellationRequested )
					return;

				throw new Exception( "GameInstance load failed" );
			}

			Json.PopulateReflectionCache( Game.TypeLibrary );

			if ( ct.IsCancellationRequested )
				return;

			await Task.Yield();
			GC.Collect( GC.MaxGeneration, GCCollectionMode.Optimized, false, false );
			await Task.Yield();

			if ( Package.TryParseIdent( ident, out var parsed ) )
			{
				EngineFileSystem.Data.CreateDirectory( parsed.org );

				var package = parsed.local ? $"{parsed.package}#local" : parsed.package;

				GlobalContext.Current.FileOrg = EngineFileSystem.Data.CreateSubSystem( parsed.org );
				GlobalContext.Current.FileOrg.CreateDirectory( package );

				GlobalContext.Current.FileData = FileSystem.OrganizationData.CreateSubSystem( package );
			}
			else
			{
				EngineFileSystem.Data.CreateDirectory( ".local" );

				GlobalContext.Current.FileOrg = EngineFileSystem.Data.CreateSubSystem( ".local" );
				GlobalContext.Current.FileOrg.CreateDirectory( ident );

				GlobalContext.Current.FileData = FileSystem.OrganizationData.CreateSubSystem( ident );
			}

			Game.Cookies = new CookieContainer( "cookies", false, GlobalContext.Current.FileData );

			IGameInstance.Current = newInstance;
			gameInstance = newInstance;
			newInstance = default;

			//Boot up
			if ( flags.Contains( GameLoadingFlags.Host ) )
			{
				//The scene loads synchronously, so the title shown until it returns is whatever the last frame drew
				LoadingScreen.Title = "Loading Scene";
				await Task.Yield();
				if ( !gameInstance.OpenStartupScene() )
				{
					throw new Exception( "Failed to load startup scene" );
				}
			}

			if ( Application.IsEditor )
			{
				Game.CheatsEnabled = true; //auto-enable cheats in the editor
			}

			//If we're the game, start it straight away. In editor we're going to start in editor mode
			if ( !Application.IsEditor )
			{
				Game.IsPlaying = true;

				LoadingScreen.Title = flags.Contains( GameLoadingFlags.Host ) ? "Starting Game" : "Joining Game..";
				await Task.Yield();

				IMenuDll.Current?.OnGameEntered();
			}
		}
		finally
		{
			//Loading failed
			if ( newInstance is not null )
			{
				using var _ = GlobalContext.GameScope();

				newInstance.Close();
				newInstance.Shutdown();
				newInstance = default;
			}
		}
	}

	private void OnPackageInstalled( PackageManager.ActivePackage package, string context )
	{
		if ( context != "game" ) return;
		Log.Trace( $"OnPackageInstalled: {package.Package.FullIdent} {context}" );

		//Register assemblies from this package so types are available immediately.
		AssemblyEnroller?.LoadPackage( package.Package.FullIdent, true );
	}

	/// <summary>Called when the game menu is closed</summary>
	public void Shutdown( IGameInstance instance )
	{
		if ( gameInstance == instance ) gameInstance = null;
		if ( IGameInstance.Current == instance ) IGameInstance.Current = null;
	}

	public void OnRender()
	{
		Game.Render();
	}

	/// <summary>The play button was pressed in the editor</summary>
	public void EditorPlay()
	{
		if ( gameInstance is null )
		{
			Log.Warning( "Tried to editor play but we don't have a game instance" );
			return;
		}

		Game.IsPlaying = true;

		if ( !gameInstance.OpenStartupScene() )
		{
			Log.Warning( "There was a problem opening the StartupScene" );
			return;
		}
	}

	public TypeLibrary TypeLibrary => Rinten.Internal.GlobalGameNamespace.TypeLibrary;

	/// <summary>Pushes the game scope. This will push the active scene and the right time.</summary>
	public ScenePushScope PushScope()
	{
		return Game.ActiveScene?.Push() ?? default;
	}

	//How many of these the scene holds, counted by a walk - Count() boxed the walk, every frame.
	static int CountOf<T>( Scene scene )
	{
		var count = 0;
		foreach ( var _ in scene.GetAllComponents<T>() ) count++;
		return count;
	}

	//Every particle alive in the scene, for the stats - see CountOf .
	static int ParticlesIn( Scene scene )
	{
		var count = 0;
		foreach ( var effect in scene.GetAllComponents<ParticleEffect>() ) count += effect.Particles.Count;
		return count;
	}

	//Called per frame to add scene's stats to our analytics
	void TickSceneStats( Scene scene )
	{
		var sceneValid = scene.IsValid();
		Api.Performance.CollectStat( "GameObjectCount", sceneValid ? scene.Directory.GameObjectCount : 0 );
		Api.Performance.CollectStat( "ComponentCount", sceneValid ? scene.Directory.ComponentCount : 0 );
		Api.Performance.CollectStat( "RootGameObjects", sceneValid ? scene.Children.Count : 0 );
		Api.Performance.CollectStat( "CameraCount", sceneValid ? CountOf<CameraComponent>( scene ) : 0 );
		Api.Performance.CollectStat( "ColliderCount", sceneValid ? scene.PhysicsWorld.BodyCount : 0 );
		Api.Performance.CollectStat( "Particles", sceneValid ? ParticlesIn( scene ) : 0 );

		Api.Performance.CollectStat( "GameObjectsDestroyed", SceneMetrics.GameObjectsDestroyed );
		Api.Performance.CollectStat( "ParticlesCreated", SceneMetrics.ParticlesCreated );
		Api.Performance.CollectStat( "ParticlesDestroyed", SceneMetrics.ParticlesDestroyed );
		Api.Performance.CollectStat( "GameObjectsCreated", SceneMetrics.GameObjectsCreated );
		Api.Performance.CollectStat( "GameObjectsDestroyed", SceneMetrics.GameObjectsDestroyed );
		Api.Performance.CollectStat( "ComponentsCreated", SceneMetrics.ComponentsCreated );
		Api.Performance.CollectStat( "ComponentsDestroyed", SceneMetrics.ComponentsDestroyed );
		Api.Performance.CollectStat( "RayTrace", SceneMetrics.RayTrace );
		Api.Performance.CollectStat( "RayTraceAll", SceneMetrics.RayTraceAll );

		SceneMetrics.Flip();
	}

	void IGameInstanceDll.ResetSceneListenerMetrics()
	{
		Game.ActiveScene.ResetListenerMetrics();
	}

	object IGameInstanceDll.GetSceneListenerMetrics()
	{
		return Game.ActiveScene.GetListenerMetrics();
	}

	[ConCmd( "game", ConVarFlags.Protected, Help = "Play a game" )]
	public static async Task StartGame( string gameIdent, string mapIdent = null )
	{
		//We don't want to open games in the editor
		if ( Application.IsEditor )
			return;

		//We can load and run projects if we're a Dedicated Server.
		if ( Application.IsDedicatedServer && gameIdent.ToLower().Contains( Project.Extension ) )
		{
			await Project.InitializeBuiltIn( false );

			var project = Project.AddFromFile( gameIdent );

			//Spelt as it is on the disk: this is a real directory being handed to the engine, and lowercasing one on Linux names a folder nobody has.
			EngineFiles.AddProjectPath( gameIdent, project.GetAssetsPath() );

			var libraries = Path.Combine( project.RootDirectory.FullName, "Libraries" );

			//We should iterate all available libraries and add their projects
			foreach ( var folder in Directory.EnumerateDirectories( libraries ) )
			{
				var configs = Directory.EnumerateFiles( folder, Project.ExtensionSearch ).ToArray();
				if ( configs.Length != 1 ) continue;
				Project.AddFromFile( configs[0] );
			}

			//We need to reload the project since we may have had a bunch of libraries added
			project.Load();

			await Project.CompileAsync();

			if ( !project.Active )
			{
				Log.Error( $"Unable to load {gameIdent}" );
				return;
			}

			gameIdent = project.Package.FullIdent;
		}

		Log.Info( $"Loading game '{gameIdent}'" );

		LaunchArguments.Map = mapIdent;

		if ( LaunchArguments.Map is not null )
		{
			Log.Info( $" with map: '{LaunchArguments.Map}'" );
		}

		LoadingScreen.IsVisible = true;
		LoadingScreen.Media = null;
		LoadingScreen.Title = null;

		await Current.LoadGamePackageAsync( gameIdent, GameLoadingFlags.Host, default );
	}

	public static void Create()
	{
		IGameInstanceDll.Current = new GameInstanceDll();

		if ( Application.OnWeb ) return;

		//PreJIT the methods in these dlls on background threads to avoid stalls during gameplay. At the start in the editor too,
		//not after its window: measured both ways, the window came up sooner with these running beside the start than without them.
		_ = Rinten.ReflectionUtility.PreJITAsync( typeof( GameInstanceDll ).Assembly );
		_ = Rinten.ReflectionUtility.PreJITAsync( typeof( Vector3 ).Assembly );
		_ = Rinten.ReflectionUtility.PreJITAsync( typeof( Bootstrap ).Assembly );
	}

	/// <summary>Try to get the replicated var value from the host</summary>
	public bool TryGetReplicatedVarValue( string name, out string value )
	{
		value = default;

		if ( !Networking.IsActive ) return false;
		if ( Networking.IsHost ) return false;

		return ReplicatedConvars.TryGetValue( name, out value );
	}

	public InputContext InputContext { get; private set; }

	internal void SetupInputContext()
	{
		var uiSystem = new UISystem();

		var input = new InputContext();
		input.Name = GetType().Name;
		input.TargetUISystem = uiSystem;
		input.OnGameMouseWheel += Rinten.Input.AddMouseWheel;
		input.OnMouseMotion += Rinten.Input.AddMouseMovement;
		input.OnGameButton += Input.OnButton;

		InputContext = input;

		GlobalContext.Current.UISystem = uiSystem;
		GlobalContext.Current.InputContext = input;
	}

	/// <summary>Load the assemblies from this package into the current game instance</summary>
	public Task LoadPackageAssembliesAsync( Package package )
	{
		AssemblyEnroller.LoadPackage( package.FullIdent, true );
		return Task.CompletedTask;
	}
}
