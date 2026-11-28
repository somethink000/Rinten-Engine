using Rinten.UI;
using System;

namespace Rinten;

//Our own window title bar (name, drag strip, three buttons), drawn the same on every platform
internal sealed class WindowCaption : Panel
{
	//How tall the bar is, in the window's own pixels.
	internal const int BarHeight = 30;

	//The window behind the bar: $window-ground, the same grey the editor sits on.
	internal static Color Ground => EditorTokens.Colour( "window-ground", new Color( 42 / 255f, 42 / 255f, 42 / 255f ) );

	//The same ground where the desktop blurs behind the window: thin enough to see the blur through, dark enough to read on.
	internal static Color GlassGround => EditorTokens.Colour( "window-glass", Ground.WithAlpha( 0.72f ) );

	private readonly CoreUiWindow window;
	private readonly Panel drag;
	private readonly Label title;
	private readonly Label maximizeIcon;

	private WindowCaption( CoreUiWindow window )
	{
		this.window = window;

		AddClass( "window-caption" );

		Style.Height = BarHeight;
		Style.Width = Length.Percent( 100 );
		Style.FlexShrink = 0;
		Style.FlexDirection = FlexDirection.Row;
		Style.PointerEvents = PointerEvents.All;

		drag = AddChild<Panel>( "drag" );
		drag.Style.FlexGrow = 1;
		drag.Style.Height = Length.Percent( 100 );

		title = drag.AddChild<Label>( "what" );
		title.Tokenize = false;

		var controls = AddChild<Panel>( "controls" );

		AddControl( controls, "remove", Minimize );
		maximizeIcon = AddControl( controls, "crop_square", ToggleMaximized );
		AddControl( controls, "close", Close, shut: true );
	}

	//Builds the bar into a window's root, dressed so it looks like itself before any content filesystem is mounted.
	internal static WindowCaption Attach( CoreUiWindow window, Panel root, string heading )
	{
		var bar = new WindowCaption( window );

		bar.title.Text = heading ?? string.Empty;

		//On the root, not the bar: .captioned is what makes the window a column (bar, then body), and a sheet only on the bar never reaches it.
		EditorTheme.Apply( root );
		root.AddClass( "captioned" );
		root.AddChild( bar );

		return bar;
	}

	//What the bar says, kept in step with the window's own name.
	internal string Title
	{
		get => title.Text;
		set => title.Text = value ?? string.Empty;
	}

	//Hands the window system the empty middle of the bar, and keeps the maximize glyph matching the window.
	internal void Sync()
	{
		if ( window is null ) return;

		if ( !drag.IsValid() )
		{
			window.DragStrip = default;
			return;
		}

		var rect = drag.Box.Rect;

		window.DragStrip = ( (int)rect.Left, (int)rect.Top, (int)rect.Width, (int)rect.Height );

		if ( maximizeIcon is null ) return;

		var glyph = window.IsMaximized ? "filter_none" : "crop_square";

		if ( maximizeIcon.Text != glyph ) maximizeIcon.Text = glyph;
	}

	private static Label AddControl( Panel into, string glyph, Action pressed, bool shut = false )
	{
		var control = into.AddChild<Panel>( shut ? "control shut" : "control" );

		var icon = control.AddChild<Label>();
		icon.ElementName = "i";
		icon.Text = glyph;
		icon.Tokenize = false;

		control.AddEventListener( "onclick", pressed );

		return icon;
	}

	private void Minimize() => window?.Minimize();

	private void ToggleMaximized()
	{
		if ( window is null ) return;

		if ( window.IsMaximized ) window.Restore();
		else window.Maximize();
	}

	private void Close()
	{
		if ( window is null ) return;

		//The window is asked before the window system is told. It may have somewhere else to put the press
		//- the editor closes the workspace that is up before it closes itself - or it may want a second one.
		if ( window.AskClose is { } ask && !ask() ) return;

		window.RequestClose();
	}
}
