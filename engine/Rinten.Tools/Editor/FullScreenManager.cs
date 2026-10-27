namespace Editor;

//A class that handles the fullscreen behavior for the main editor window.
internal partial class FullScreenManager
{
	public FullScreenManager()
	{
		EditorEvent.Register( this );
	}

	public bool IsActive => Widget.IsValid();

	/// <summary>The current fullscreen widget</summary>
	public Widget Widget { get; private set; }

	//A reference to the previous parent, when killing fullscreen we want to restore the widget
	private Widget PreviousParent { get; set; }

	//The layout the widget was taken out of, and where in it - so that coming back out of
	//fullscreen puts it exactly where it was rather than at the end of whatever holds it.
	private Layout PreviousLayout { get; set; }
	private int PreviousIndex { get; set; } = -1;
	private Layout.Cell PreviousCell;

	//Whether the window already had the screen to itself before this, so that coming
	//out of fullscreen puts it back the way it was found rather than always windowed.
	private bool WindowWasFullscreen;

	//The scene whose view was fullscreen when a game was started from it - the view that goes back to fullscreen when the game stops.
	private SceneEditorSession ReturnTo;

	/// <summary>Removes the current widget as the full screen widget</summary>
	public void Clear()
	{
		ReturnTo = null;

		if ( !Widget.IsValid() )
		{
			Abandon();
			return;
		}

		PutBack();
		RestoreWindow();
	}

	//The editor around the view, and the window, as they were before fullscreen.
	private void RestoreWindow()
	{
		if ( EditorWindow.Console.IsValid() )
		{
			EditorWindow.Console.SetInputEnabled( true );
		}

		ShowChrome( true );

		//And the window back off the screen, if this is what put it there.
		if ( !WindowWasFullscreen && EditorWindow.IsValid() && EditorWindow.Shell is { } shell )
			shell.SetFullscreen( false );
	}

	//The fullscreen widget back in the place it was taken from.
	private void PutBack()
	{
		if ( Widget.IsValid() )
		{
			Widget.Parent = PreviousParent;

			if ( PreviousLayout is not null && PreviousIndex >= 0 )
			{
				PreviousLayout.PutBack( PreviousIndex, PreviousCell );
			}
			else if ( PreviousParent is StackedHost host )
			{
				//No layout there to size it, so it would keep the whole window and cover the docks beside it
				host.Fit( Widget );
			}
			else if ( PreviousParent.IsValid() )
			{
				PreviousParent.Layout?.Add( Widget );
			}
		}

		Forget();
	}

	private void Forget()
	{
		Widget = null;
		PreviousParent = null;
		PreviousLayout = null;
		PreviousIndex = -1;
		PreviousCell = default;
	}

	//The fullscreen widget went away under us - a game's view, destroyed when the game stopped. There is nothing
	//to put back; the editor around it is shown again rather than left hidden with nothing in front of it.
	private void Abandon()
	{
		var had = Widget is not null;

		Forget();

		if ( had ) RestoreWindow();
	}

	/// <summary>Play from a fullscreen view: the game's view takes the screen, since its tab is hidden</summary>
	[Event( "scene.play" )]
	public void OnScenePlay()
	{
		if ( !Widget.IsValid() ) return;

		var session = SceneEditorSession.All.FirstOrDefault( x => x.SceneDock == Widget );

		if ( session?.GameSession?.SceneDock is not { } game || !game.IsValid() ) return;

		ReturnTo = session;
		Swap( game );
	}

	/// <summary>The game stopped: the scene it was started from has the screen back.</summary>
	[Event( "scene.stop" )]
	public void OnSceneStop()
	{
		var back = ReturnTo?.SceneDock;
		ReturnTo = null;

		if ( back.IsValid() )
		{
			Swap( back );
			return;
		}

		if ( Widget is not null && !Widget.IsValid() ) Abandon();
	}

	//Another widget has the screen in place of this one - the window stays fullscreen and the editor stays hidden, so nothing flickers in between.
	private void Swap( Widget next )
	{
		if ( !next.IsValid() ) return;

		if ( Widget.IsValid() ) PutBack();
		else Forget();

		Take( next );
		SetWidgetLayout();
	}

	[EditorEvent.Frame]
	public void OnFrame()
	{
		if ( Widget is not null && !Widget.IsValid() )
		{
			Abandon();
			return;
		}

		if ( !Widget.IsValid() )
			return;

		if ( Widget.Size != GetTargetSize() || Widget.Position != GetTargetPosition() )
		{
			SetWidgetLayout();
		}
	}

	//Fullscreen view covers the whole window at origin, so it matches the size the game would run at
	private Vector2 GetTargetPosition() => Vector2.Zero;

	private Vector2 GetTargetSize() => EditorWindow.Size;

	//Make the window itself fullscreen at screen resolution; restored on exit if it wasn't before
	private void TakeTheScreen()
	{
		if ( !EditorWindow.IsValid() || EditorWindow.Shell is not { } shell ) return;

		WindowWasFullscreen = shell.IsFullscreen;

		if ( !WindowWasFullscreen ) shell.SetFullscreen( true );
	}

	//Hide the rest of the editor while fullscreen; hidden panels skip tick and layout entirely
	private void ShowChrome( bool shown )
	{
		if ( EditorWindow.MenuWidget.IsValid() ) EditorWindow.MenuWidget.Visible = shown;
		if ( EditorWindow.StatusBar.IsValid() ) EditorWindow.StatusBar.Visible = shown;
		if ( EditorWindow.Workspaces.IsValid() ) EditorWindow.Workspaces.Visible = shown;
	}

	private void SetWidgetLayout()
	{
		if ( !Widget.IsValid() )
			return;

		Widget.Position = GetTargetPosition();
		Widget.Size = GetTargetSize();
	}

	/// <summary>Sets a widget as the fullscreen widget</summary>
	public void SetWidget( Widget widget )
	{
		Clear();

		if ( !widget.IsValid() )
			return;

		Take( widget );

		//Make sure we kill focus from the console
		if ( EditorWindow.Console.IsValid() )
		{
			EditorWindow.Console.SetInputEnabled( false );
		}

		//And nothing else is. After the reparent, so that what is being hidden no longer holds the widget that is going fullscreen.
		ShowChrome( false );

		//The screen, before the widget is laid out against the window - the window is about to be a different size.
		TakeTheScreen();

		//Lay the widget out
		SetWidgetLayout();
	}

	//A widget out of wherever it is and onto the whole window, with where it came from remembered so it can go back.
	private void Take( Widget widget )
	{
		//Store the widget, and the widget's parent so we can restore it later
		Widget = widget;
		PreviousParent = widget.Parent;

		//And out of the layout it was in. It is about to be positioned by hand against the whole window; left in the layout
		//as well, it is two things at once - and the cell it left behind is what came back doubled when fullscreen ended.
		PreviousLayout = null;
		PreviousIndex = -1;

		if ( PreviousParent.IsValid() && PreviousParent.Layout is { } holding &&
			holding.TakeOut( widget, out var from, out var index, out var cell ) )
		{
			PreviousLayout = from;
			PreviousIndex = index;
			PreviousCell = cell;
		}

		//Set our target widget's parent to the editor's main window, so we can size it properly
		widget.Parent = EditorWindow;

		//Make sure it's visible now
		widget.Visible = true;
	}
}
