namespace Editor;

//Tracks whether this viewport owns the pointer (held through a press) and if the camera may act
internal sealed class ViewportPointer
{
	private bool mouseWasPressed;
	private int framesAfterRelease;

	/// <summary>Whether this viewport is the one the mouse is talking to.</summary>
	public bool HasMouseInput { get; private set; }

	/// <summary>Whether the last press was the second of a pair, until something reads it.</summary>
	public bool DoubleClick { get; set; }

	/// <summary>Whether a modifier is being held for a tool rather than for the camera.</summary>
	public bool BlockCamera { get; private set; }

	/// <summary>Whether ALT+drag on a gizmo has taken the camera off the mouse.</summary>
	public bool BlockForToolInput { get; private set; }

	/// <summary>Whether the held left button went down on a handle with ALT held, so it drags the handle.</summary>
	public bool HandleHasPress { get; private set; }

	private bool leftWasDown;

	/// <summary>Where the right button went down, which is where a paste lands.</summary>
	public Vector2 PressPosition { get; set; }

	/// <summary>Where the camera was when it did, so a drag is told from a click.</summary>
	public Vector3 PressCameraPosition { get; set; }

	private Vector2 blockPosition;

	/// <summary>Keep input state stable while pressed: own presses until release, ignore external presses</summary>
	public void Update( bool modalBlocksInput, bool activeWindow, bool underMouse )
	{
		if ( modalBlocksInput )
		{
			HasMouseInput = false;
			mouseWasPressed = false;
			framesAfterRelease = 0;
			return;
		}

		//If mouse buttons are pressed, maintain current state until released
		if ( Application.MouseButtons != MouseButtons.None )
		{
			mouseWasPressed = true;
			framesAfterRelease = 0;
			return;
		}

		//If mouse was just released, keep the state stable for a few frames to ensure click processing
		if ( mouseWasPressed )
		{
			mouseWasPressed = false;
			framesAfterRelease = 3;
			return;
		}

		if ( framesAfterRelease > 0 )
		{
			framesAfterRelease--;
			return;
		}

		HasMouseInput = activeWindow && underMouse;
	}

	/// <summary>A drop has just happened, so the button coming up has already been seen.</summary>
	public void Dropped( bool activeWindow, bool underMouse )
	{
		mouseWasPressed = false;
		framesAfterRelease = 0;
		HasMouseInput = activeWindow && underMouse;
	}

	/// <summary>ALT and a double click is a tool being used, not the camera being flown.</summary>
	public void NoteDoubleClick( Vector2 localPosition )
	{
		DoubleClick = true;

		if ( !Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Alt ) ) return;

		BlockForToolInput = true;
		blockPosition = localPosition;
	}

	/// <summary>Works out whether the camera may answer the mouse this frame.</summary>
	public void UpdateCameraBlocks( bool gizmoHovered, bool handleHovered, Vector2 cursorInSurface )
	{
		//Block camera input when shift or ctrl was down first and right mouse pressed.
		var rightDown = SceneEditorExtensions.IsPilotingCamera;
		var modifiers = Application.KeyboardModifiers;

		//Decided at the press only, so an orbit that sweeps over a handle keeps orbiting
		var leftDown = Application.MouseButtons.HasFlag( MouseButtons.Left );
		if ( leftDown && !leftWasDown ) HandleHasPress = gizmoHovered && handleHovered && modifiers.HasFlag( KeyboardModifiers.Alt );
		if ( !leftDown ) HandleHasPress = false;
		leftWasDown = leftDown;
		var modifiersDown = modifiers.Contains( KeyboardModifiers.Shift ) || modifiers.HasFlag( KeyboardModifiers.Ctrl );

		BlockCamera = !BlockCamera ? modifiersDown && !rightDown : modifiersDown;

		if ( modifiers.HasFlag( KeyboardModifiers.Alt ) && rightDown && gizmoHovered && !BlockForToolInput )
		{
			BlockForToolInput = true;
			blockPosition = cursorInSurface;
		}

		if ( !BlockForToolInput ) return;

		if ( Application.MouseButtons == MouseButtons.None )
		{
			BlockForToolInput = false;
			return;
		}

		if ( Vector2.DistanceBetween( blockPosition, cursorInSurface ) > 3f )
		{
			BlockForToolInput = false;
		}
	}
}
