using Rinten;
using Rinten.Engine;
using System;

namespace Editor;

public static class Application
{
	/// <summary>Called when any widget is clicked. Can set MouseEvent.Accepted to true to prevent the Widget's OnMouseClick from firing.</summary>
	public static Action<Widget, MouseEvent> OnWidgetClicked { get; set; }

	public static void SetStyles( string style )
	{
		return;
	}

	internal static void ReloadStyles()
	{
		EditorSheetPreamble.ReloadAll();
	}

	/// <summary>Will process all of the UI events - allowing the UI to stay responsive during a blocking call.</summary>
	public static void Spin()
	{
		Rinten.Engine.IToolsDll.Current?.Spin();
	}

	/// <summary>Pixels per interface unit of the window the pointer is in - the desktop's scale setting.</summary>
	public static float DpiScale => CoreUiWindow.PointerScale;

	/// <summary>Pointer position in its window's interface units, same space as Widget.ScreenPosition</summary>
	public static Vector2 CursorPosition
	{
		get => CoreUiWindow.Pointer / CoreUiWindow.PointerScale;
		set
		{
			CoreUiWindow.PutPointer( value * CoreUiWindow.PointerScale );

			lastCursorPos = UnscaledCursorPosition;
		}
	}

	/// <summary>Pointer position in its window's pixels, the space panels are laid out and hit-tested in</summary>
	public static Vector2 CursorPixels => CoreUiWindow.Pointer;

	/// <summary>The cursor position, not scaled for DPI</summary>
	public static Vector2 UnscaledCursorPosition
	{
		get => CoreCursor.Position;
		set
		{
			CoreCursor.Position = value;

			lastCursorPos = UnscaledCursorPosition;
		}
	}

	/// <summary>The cursor delta between this and previous frame.</summary>
	public static Vector2 CursorDelta
	{
		get; private set;
	}

	/// <summary>The mouse wheel delta between this and previous frame</summary>
	public static Vector2 MouseWheelDelta { get; private set; }

	/// <summary>Take this frame's wheel movement, so that whatever acted on it is the only thing that does.</summary>
	public static void ConsumeMouseWheel() => MouseWheelDelta = Vector2.Zero;

	static Vector2 lastCursorPos;
	internal static Vector2 accumulatedWheelDelta = Vector2.Zero;

	//Feeds wheel notches to non-widget readers of MouseWheelDelta such as the scene camera
	internal static void TakeWheel( float notches )
	{
		accumulatedWheelDelta += new Vector2( 0, notches );
	}

	internal static void StartFrame()
	{
		//Desktop units to interface units, with the fractions CursorPosition does not have
		CursorDelta = (UnscaledCursorPosition - lastCursorPos) * CoreUiWindow.PointerDensity / DpiScale;

		//record this for next time around
		lastCursorPos = UnscaledCursorPosition;

		MouseWheelDelta = accumulatedWheelDelta;
		accumulatedWheelDelta = Vector2.Zero;
	}


	/// <summary>Returns which keyboard modified keys are held down right at this point.</summary>
	public static KeyboardModifiers KeyboardModifiers
	{
		get
		{
			var held = CoreCursor.Modifiers;
			var modifiers = global::Rinten.KeyboardModifiers.None;

			if ( held.Shift ) modifiers |= global::Rinten.KeyboardModifiers.Shift;
			if ( held.Ctrl ) modifiers |= global::Rinten.KeyboardModifiers.Ctrl;
			if ( held.Alt ) modifiers |= global::Rinten.KeyboardModifiers.Alt;

			return modifiers;
		}
	}

	/// <summary>Returns the current state of the mouse buttons.</summary>
	public static MouseButtons MouseButtons
	{
		get => CoreCursor.Buttons;
	}


	/// <summary>Returns whether or not a key is currently being held down.</summary>
	public static bool IsKeyDown( Editor.KeyCode code )
	{
		return CoreCursor.IsScancodeDown( EditorKeys.ToScancode( code ) );
	}

	/// <summary>The game's name for an editor key.</summary>
	public static string KeyCodeToString( Editor.KeyCode code )
	{
		return EditorKeys.ToName( code );
	}

	/// <summary>The <see cref="Widget"/> that has the keyboard input focus, or <c>null</c>if no widget in this application has the focus.</summary>
	public static Widget FocusWidget => WidgetSurface.Focused;


	/// <summary>The Widget that is currently hovered</summary>
	public static Widget HoveredWidget => WidgetSurface.Hovered;

	/// <summary>Get the current editor if any. Will return null if we're not in the editor, or there is no active editor session.</summary>
	public static EditorSystem Editor => IToolsDll.Current?.ActiveEditor;
}
