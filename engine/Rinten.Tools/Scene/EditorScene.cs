using Rinten.Engine;
using Rinten.Modals;
using System;
using System.Text.Json.Nodes;

namespace Editor;

public static class EditorScene
{
	/// <summary>Should the game start in play mode when hitting play, instead of playing the active scene.</summary>
	public static bool PlayMode
	{
		get => ProjectCookie?.Get( "editor.playmode", false ) ?? false;
		set => ProjectCookie?.Set( "editor.playmode", value );
	}

	public static Gizmo.SceneSettings GizmoSettings { get; private set; } = new Gizmo.SceneSettings();

	//Cookie name includes the unit so old inch-based gizmo settings are not read as metres
	internal const string GizmoSettingsCookie = "gizmo.settings.metric";

	//The shown session's, not the tab's: while a game is playing, what is clicked in the viewport is
	//one of its objects, and the panels that show a selection have to be looking at the same list.
	public static SelectionSystem Selection => SceneEditorSession.Current?.Selection;

	public static void RestoreState()
	{
		if ( ProjectCookie?.Get<Gizmo.SceneSettings>( GizmoSettingsCookie, null ) is Gizmo.SceneSettings savedSettings )
		{
			GizmoSettings = savedSettings;
		}
	}

	[Shortcut( "editor.new", "CTRL+N" )]
	public static void NewScene()
	{
		var newSession = SceneEditorSession.CreateDefault();
		newSession.MakeActive();
	}

	/// <summary>File > Open on the tab in front: a scene, or what that editor works on.</summary>
	[Shortcut( "editor.open", "CTRL+O" )]
	public static void Open() => EditorMainWindow.Current?.OpenInCurrentWorkspace();

	/// <summary>Opens the given scene file for editing, if it's not already open.</summary>
	public static void OpenScene( SceneFile resource )
	{
		if ( SceneEditorSession.Resolve( resource ) is { } session )
		{
			session.MakeActive();
			return;
		}

		if ( string.IsNullOrWhiteSpace( resource.ResourcePath ) )
		{
			//Play mode has a copy of the scene file that shouldn't be edited.
			return;
		}

		session = SceneEditorSession.CreateFromPath( resource.ResourcePath );
		session.MakeActive();
	}

	/// <summary>Opens the given prefab file for editing, if it's not already open.</summary>
	public static void OpenPrefab( PrefabFile resource )
	{
		if ( SceneEditorSession.Resolve( resource ) is PrefabEditorSession session )
		{
			session.MakeActive();
			return;
		}

		var prefabScene = PrefabScene.CreateForEditing();
		using ( prefabScene.Push() )
		{
			prefabScene.Load( resource );

			session = new PrefabEditorSession( prefabScene );
			session.MakeActive();
		}
	}

	[Shortcut( "editor.save", "CTRL+S" )]
	public static void SaveSession()
	{
		SceneEditorSession.Active?.Save( false );
	}

	[Shortcut( "editor.save-as", "CTRL+SHIFT+S" )]
	public static void SaveSessionAs()
	{
		SceneEditorSession.Active?.Save( true );
	}

	[Shortcut( "editor.save-all", "CTRL+ALT+SHIFT+S" )]
	public static void SaveAllSessions()
	{
		foreach ( var session in SceneEditorSession.All )
		{
			session.Save( false );
		}
	}

	public static void Discard()
	{
		if ( !SceneEditorSession.Active?.HasUnsavedChanges ?? false )
			return;

		EditorModal.Confirm( null, "Discard Changes",
			"The scene is loaded again from disk, and everything changed since it was last saved is lost.",
			() =>
			{
				//Load from source
				var scene = SceneEditorSession.Active?.Scene;
				if ( scene is not null )
					scene.Load( scene.Source );
			},
			"Discard", "Cancel", destructive: true );
	}

	static SceneEditorSession recentlyActivePlayableScene;

	static SceneEditorSession FindPlayableSession()
	{
		//Current scene is good
		if ( SceneEditorSession.Active?.Scene is not PrefabScene )
			return SceneEditorSession.Active;

		//Last viewed scene is good
		if ( SceneEditorSession.All.Contains( recentlyActivePlayableScene ) )
			return recentlyActivePlayableScene;

		//We don't want to play prefab scenes
		var sessions = SceneEditorSession.All.Where( x => x.Scene is not PrefabScene ).ToList();
		var idx = sessions.IndexOf( SceneEditorSession.Active );

		//If the active session is not in the filtered list, we can't play
		if ( idx < 0 )
			return null;

		//Find closest scene (prefer left, then right)
		return sessions.Take( idx ).LastOrDefault() ?? sessions.Skip( idx + 1 ).FirstOrDefault();
	}

	/// <summary>Toggles play mode.</summary>
	[Shortcut( "editor.toggle-play", "F5", ShortcutType.Window )]
	public static void TogglePlay()
	{
		EditorShortcuts.ReleaseAll();

		if ( !Game.IsPlaying )
			Play();
		else
			Stop();
	}

	static JsonObject _mixerStore;

	//Store stuff before playing
	static void OnPlayStore()
	{
		_mixerStore = Rinten.Audio.Mixer.Master.Serialize();
	}

	//Restore stuff before playing
	static void OnPlayRestore()
	{
		if ( _mixerStore is not null )
		{
			Rinten.Audio.Mixer.ResetToDefault();
			Rinten.Audio.Mixer.Master.Deserialize( _mixerStore, TypeLibrary );
		}

	}

	public static void Play( SceneEditorSession session = null ) => Play( PlayMode, session );

	public static void Play( bool playMode, SceneEditorSession playableSession = null )
	{
		playableSession ??= FindPlayableSession();
		if ( playableSession is null ) return;

		OnPlayStore();

		Game.IsPlaying = true;

		IModalSystem.Current?.CloseAll( true );

		EditorEvent.Run( "scene.startplay" );

		if ( playMode )
		{
			if ( IGameInstance.Current is not null )
			{
				LoadingScreen.IsVisible = true;
				LoadingScreen.Title = "Loading Game..";
				IGameInstanceDll.Current.EditorPlay();
			}
		}
		else
		{
			LoadingScreen.IsVisible = true;
			LoadingScreen.Title = "Loading Scene..";

			if ( Game.ActiveScene is not null && !Game.ActiveScene.IsEditor )
			{
				Game.ActiveScene?.Destroy();
				Game.ActiveScene = null;
			}

			var current = playableSession.Scene.CreateSceneFile();
			var name = playableSession.Scene.Name;

			Game.ActiveScene = new Scene();
			Game.ActiveScene.Name = name;
			Game.ActiveScene.StartLoading();

			var options = new SceneLoadOptions();
			options.SetScene( current );

			Game.ActiveScene.RunEvent<ISceneStartup>( x => x.OnHostPreInitialize( options.GetSceneFile() ) );

			Game.ActiveScene.Load( options );

			Game.ActiveScene.RunEvent<ISceneStartup>( x => x.OnHostInitialize() );
			Game.ActiveScene.RunEvent<ISceneStartup>( x => x.OnClientInitialize() );
		}

		if ( Game.ActiveScene is null )
		{
			Log.Warning( "Tried to play, but there was no scene." );
			return;
		}

		SceneEditorSession.Active.SetPlaying( Game.ActiveScene );
		EditorEvent.Run( "scene.play" );
	}

	public static void Stop()
	{
		//Close any open overlay modals so they don't persist in the next play session
		IModalSystem.Current?.CloseAll( true );

		Game.IsClosing = true;

		SceneEditorSession.Playing?.StopPlaying();

		Game.IsPlaying = false;
		Game.IsPaused = false;

		//Immediately stop active recordings so we don't get any black frames
		ScreenRecorder.StopRecording();

		//Let's use a disconnect scope here to prevent network destroy messages being sent. This lets clients already
		//connected to our editor session keep playing. Orphaned actions will take care of any objects owned by us.
		using ( Networking.DisconnectScope() )
		{
			Game.ActiveScene?.Destroy();
			Game.ActiveScene = null;
		}

		Game.IsClosing = false;

		Sound.StopAll( 0.5f );

		SceneEditorTick();

		EditorEvent.Run( "scene.stop" );

		Mouse.Visibility = MouseVisibility.Auto;

		OnPlayRestore();
	}

	/// <summary>Called once a frame to keep the game camera in sync with the main camera in the editor scene</summary>
	[EditorEvent.Frame]
	public static void SceneEditorTick()
	{
		if ( SceneEditorSession.Active is null ) return;

		if ( SceneEditorSession.Active.Scene is not PrefabScene )
			recentlyActivePlayableScene = SceneEditorSession.Active;

		SceneEditorSession.ProcessSceneEdits();
	}

	[EditorForAssetType( "scene" )]
	[EditorForAssetType( "prefab" )]
	public static void LoadFromResource( GameResource resource )
	{
		Assert.NotNull( resource, "resource should not be null" );

		var session = SceneEditorSession.CreateFromPath( resource.ResourcePath );
		session.MakeActive();
	}

	//Whether any instance in this scene was actually built again.
	internal static bool UpdatePrefabInstancesInScene( Scene scene, PrefabFile prefab )
	{
		var changedPath = prefab.ResourcePath;
		var updated = 0;

		var started = System.Diagnostics.Stopwatch.GetTimestamp();

		using ( scene.Push() )
		{
			//Copied: updating a prefab can spawn objects or components into this collection
			var prefabInstancesRequiringUpdate = scene.GetAllObjects( false )
				.Where( x => x.IsPrefabInstanceRoot && x.PrefabInstanceSource == changedPath )
				.Select( x => x.OutermostPrefabInstanceRoot ) //We always need to update the outermostprefab instance
				.ToHashSet();

			var found = System.Diagnostics.Stopwatch.GetTimestamp();

			foreach ( var obj in prefabInstancesRequiringUpdate )
			{
				if ( obj.IsValid() )
				{
					obj.UpdateFromPrefab();
					scene.Editor.HasUnsavedChanges = true;
					updated++;
				}
			}

			var whole = System.Diagnostics.Stopwatch.GetElapsedTime( started ).TotalMilliseconds;

			//How many of them there were, because the two ways this is slow look the same from outside: one instance
			//that is expensive to build, and a hundred that each pay for the whole prefab to be serialised again.
			if ( whole > 20.0 )
			{
				var looking = System.Diagnostics.Stopwatch.GetElapsedTime( started, found ).TotalMilliseconds;

				Log.Info( $"'{scene.Name}': {updated} instance(s) of '{changedPath}' rebuilt in {whole:0.#}ms " +
					$"(finding them {looking:0.#}ms, {(updated > 0 ? whole - looking : 0) / Math.Max( updated, 1 ):0.#}ms each)" );
			}
		}

		return updated > 0;
	}

	/// <summary>Update any/all instances of a prefab in any open sessions.</summary>
	public static void UpdatePrefabInstances( PrefabFile prefab )
	{
		UpdatePrefabInstances( prefab, new HashSet<PrefabFile>() );
	}

	//What each prefab's instances were last built from.
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PrefabFile, JsonObject> handedOut = new();

	//Whether instances need rebuilding: false if they were built from this same RootObject already
	internal static bool NeedsHandingOut( PrefabFile prefab )
	{
		if ( prefab?.RootObject is not { } root ) return false;

		return !handedOut.TryGetValue( prefab, out var last ) || !ReferenceEquals( last, root );
	}

	//Says that this prefab's instances are built from what is in it now.
	private static void Remember( PrefabFile prefab )
	{
		if ( prefab?.RootObject is not { } root ) return;

		handedOut.Remove( prefab );
		handedOut.Add( prefab, root );
	}

	//One prefab into every open session, and on into whatever that changed.
	private static void UpdatePrefabInstances( PrefabFile prefab, HashSet<PrefabFile> done )
	{
		ArgumentNullException.ThrowIfNull( prefab );

		if ( !done.Add( prefab ) ) return;

		Remember( prefab );

		//The open prefabs this changed, kept for after the walk: writing one out is serialising the whole of
		//it, and doing that for every open prefab on every edit of any other one was most of what an edit cost.
		var changed = new List<PrefabEditorSession>();

		foreach ( var session in SceneEditorSession.All.ToArray() )
		{
			//The source prefab already has the latest changes via OnEdited
			if ( session is PrefabEditorSession source && source.Scene.Source == prefab ) continue;

			if ( !UpdatePrefabInstancesInScene( session.Scene, prefab ) ) continue;

			if ( session is PrefabEditorSession holder ) changed.Add( holder );
		}

		foreach ( var session in changed )
		{
			session.Scene.ToPrefabFile();

			if ( session.Scene.Source is PrefabFile file )
				UpdatePrefabInstances( file, done );
		}
	}

	[Shortcut( "editor.cut", "CTRL+X" )]
	public static void Cut()
	{
		var selection = EditorScene.Selection.OfType<GameObject>().ToArray();
		if ( selection.Count() < 1 ) return;

		var session = SceneEditorSession.Resolve( selection.FirstOrDefault() );
		using var scene = session.Scene.Push();

		var options = new GameObject.SerializeOptions();
		var serializedObjects = selection.Select( x => x.Serialize( options ) ).ToArray();

		EditorUtility.Clipboard.Copy( Json.Serialize( serializedObjects ) );

		using ( session.UndoScope( "Cut" ).WithGameObjectDestructions( selection ).Push() )
		{
			session.Selection.Clear();
			//Delete all objects in selection
			foreach ( var go in selection )
			{
				go.Destroy();
			}
		}
	}

	[Shortcut( "editor.select-all", "CTRL+A" )]
	public static void SelectAll()
	{
		using ( SceneEditorSession.Current.UndoScope( "Select All" ).Push() )
		{
			Selection.Clear();
			foreach ( var child in SceneEditorSession.Current.Scene.Children.Where( x => x.ShouldShowInHierarchy() ) )
			{
				Selection.Add( child );
			}
		}
	}

	[Shortcut( "editor.copy", "CTRL+C" )]
	public static void Copy()
	{
		var options = new GameObject.SerializeOptions();

		var selection = EditorScene.Selection.OfType<GameObject>().ToArray();
		if ( !selection.Any() ) return;

		var serialized = selection.Select( x =>
		{
			using var blobs = BlobDataSerializer.Capture();

			var s = x.Serialize( options );
			//When we copy we keep the world transform.
			s["Position"] = JsonValue.Create( x.WorldPosition );
			s["Rotation"] = JsonValue.Create( x.WorldRotation );
			s["Scale"] = JsonValue.Create( x.WorldScale );

			blobs.SaveTo( s );
			return s;
		} );

		EditorUtility.Clipboard.Copy( Json.Serialize( serialized ) );
	}

	[Shortcut( "editor.paste", "CTRL+V" )]
	public static void Paste()
	{
		PasteInternal();
	}

	public static void PasteAt( SceneTraceResult tr )
	{
		PasteInternal( tr );
	}

	static void PasteInternal( SceneTraceResult? tr = default )
	{
		var selected = Selection.FirstOrDefault() as GameObject;

		var session = SceneEditorSession.Resolve( selected ) ?? SceneEditorSession.Current;
		using var scene = session.Scene.Push();

		//Paste to scene root if nobody is selected
		selected ??= SceneEditorSession.Current.Scene;

		if ( selected is Scene )
		{
			PasteAsChildAt( tr );
			return;
		}

		ExecutableUndoablePaste( selected, false, tr );
	}

	static void PasteAsChildAt( SceneTraceResult? tr = default )
	{
		var selected = Selection.OfType<GameObject>().ToArray();
		var first = selected.FirstOrDefault();

		var session = SceneEditorSession.Resolve( first ) ?? SceneEditorSession.Current;
		using var scene = session.Scene.Push();

		//Paste to scene root if nobody is selected
		if ( !first.IsValid() )
		{
			selected = [SceneEditorSession.Current.Scene];
		}

		ExecutableUndoablePaste( selected, true, tr );
	}

	[Shortcut( "editor.paste-as-child", "CTRL+SHIFT+V" )]
	public static void PasteAsChild()
	{
		PasteAsChildAt();
	}

	[Shortcut( "editor.paste-special", "CTRL+ALT+V" )]
	public static void PasteSpecial()
	{
		var dialog = new ScenePasteSpecialDialog( ScenePasteSpecialDialog.Execute );
		dialog.Show();
	}

	private static void ExecutableUndoablePaste( GameObject target, bool asChild, SceneTraceResult? tr )
	{
		ExecutableUndoablePaste( [target], asChild, tr );
	}

	private static void ExecutableUndoablePaste( IEnumerable<GameObject> targets, bool asChild, SceneTraceResult? tr )
	{
		var text = EditorUtility.Clipboard.Paste();

		//Deserialize can fail if the clipboards contents area ambigous / partally invalid
		try
		{
			if ( Json.Deserialize<IEnumerable<JsonObject>>( text ) is IEnumerable<JsonObject> serializedObjects )
			{
				var objCount = serializedObjects.Count();
				if ( objCount == 0 ) return;

				var session = SceneEditorSession.Resolve( targets.FirstOrDefault() );
				using var scene = session.Scene.Push();
				using ( session.UndoScope( $"Paste {objCount} Objects" ).WithGameObjectCreations().Push() )
				{
					Selection.Clear();

					foreach ( var target in targets )
					{
						foreach ( var jso in serializedObjects )
						{
							var go = SceneEditorSession.Current.Scene.CreateObject();
							//avoids some warnings
							SceneUtility.MakeIdGuidsUnique( jso );

							using var blobs = BlobDataSerializer.LoadFrom( jso );
							go.Deserialize( jso );

							if ( target.IsValid() )
							{
								if ( asChild )
								{
									go.SetParent( target );
								}
								else
								{
									target.AddSibling( go, false );
								}
							}

							go.MakeNameUnique();

							Selection.Add( go );
						}
					}

					if ( tr is { } trace )
					{
						PlaceBoundsOnSurface( Selection.OfType<GameObject>(), trace.HitPosition, trace.Normal );
					}
				}
			}
		}
		catch
		{
			Log.Warning( "Failed to paste, invalid JSON." );
		}
	}

	/// <summary>Helper function to offset gameobjects to surface bounds.</summary>
	public static void PlaceBoundsOnSurface( IEnumerable<GameObject> gos, Vector3 position, Vector3 normal )
	{
		var selectionBounds = BBox.FromBoxes( gos.Select( x => x.GetBounds() ) );
		var selectionCenter = selectionBounds.Center;
		var maxDot = float.NegativeInfinity;

		foreach ( var corner in selectionBounds.Corners )
		{
			var d = Vector3.Dot( corner - selectionCenter, normal );
			if ( d > maxDot ) maxDot = d;
		}

		var pastePos = position + normal * maxDot;
		var offset = pastePos - selectionCenter;

		foreach ( var o in gos )
		{
			o.WorldPosition += offset;
		}
	}

}
