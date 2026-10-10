namespace Editor;

partial class ViewportToolsPanel
{
	Item PlayButton { get; set; }
	Item PauseButton { get; set; }
	Group PlayToolbar { get; set; }
	Group BuildToolbar { get; set; }

	enum PlayControlState
	{
		Ready,
		Playing,
		Hidden
	}

	PlayControlState _playState;

	PlayControlState CurrentPlayState => sceneView.Session switch
	{
		{ IsPrefabSession: true } => PlayControlState.Hidden,
		{ IsPlaying: true } => PlayControlState.Playing,
		_ when Game.IsPlaying => PlayControlState.Hidden,
		_ => PlayControlState.Ready
	};

	private void BuildPlayToolbar( List<Group> section )
	{
		PlayToolbar = AddGroup( section );

		PlayButton = AddButton( PlayToolbar, "Play", "play_arrow", PlayStop );
		PauseButton = AddButton( PlayToolbar, "Pause", "pause", Pause );

		//To the right of play, apart from it: what gets built for the scene
		BuildToolbar = AddGroup( section );
		BuildToolbar.Items.Add( new Item { BuildMenu = true } );

		UpdateState();
	}

	[Event( "keybinds.update" )]
	private void OnKeybindsUpdated()
	{
		UpdateState();
	}

	private static string WithKeys( string text, string shortcut )
	{
		var keys = EditorShortcuts.GetDisplayKeys( shortcut );
		return string.IsNullOrEmpty( keys ) ? text : $"{text} [{keys}]";
	}

	//When the state of game changes, e.g we're playing, stopping, ejecting, pausing, this gets called.
	private void UpdateState()
	{
		//The keybinds can change before the bar has been built, and after it has been taken apart to be built again
		if ( PlayToolbar is null ) return;

		_playState = CurrentPlayState;
		PlayToolbar.Visible = _playState != PlayControlState.Hidden;

		//A prefab has no lighting of its own to build
		if ( BuildToolbar is not null ) BuildToolbar.Visible = !sceneView.Session.IsPrefabSession;

		if ( _playState == PlayControlState.Hidden )
		{
			Refresh();
			return;
		}

		var isPlaying = _playState == PlayControlState.Playing;
		PlayButton.Enabled = true;

		if ( isPlaying )
		{
			PlayButton.ToolTip = WithKeys( "Stop", "editor.toggle-play" );
			PlayButton.Icon = () => "stop";
			PlayButton.Color = Theme.Red;
		}
		else
		{
			PlayButton.ToolTip = WithKeys( "Play", "editor.toggle-play" );
			PlayButton.Icon = () => "play_arrow";
			PlayButton.Color = Theme.Green;
		}

		PauseButton.Enabled = isPlaying;
		PauseButton.ToolTip = WithKeys( "Pause", "editor.pause" );

		Refresh();
	}


	private void PlayStop()
	{
		if ( !Game.IsPlaying )
		{
			EditorScene.Play( sceneView.Session );
		}
		else
		{
			EditorScene.Stop();
		}
	}

	[EditorEvent.Frame]
	private void UpdatePauseState()
	{
		if ( PauseButton is null )
			return;

		if ( _playState != CurrentPlayState )
			UpdateState();

		PauseButton.Color = _playState == PlayControlState.Playing && Game.IsPaused ? Theme.Text : Theme.TextLight;
	}

	[Shortcut( "editor.pause", "F7", ShortcutType.Window )]
	private void Pause()
	{
		if ( !sceneView.Session.IsPlaying )
			return;

		//What the fuck, why isnt this a method
		Game.IsPaused = !Game.IsPaused;
	}

}
