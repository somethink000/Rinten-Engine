using System;
using System.Linq;
using Rinten.Internal;
using Rinten.UI;

namespace Editor;

//Pick a panel in the editor and print its matched CSS rules to the console.
internal static class StylePickerTool
{
	public const string FurnitureClass = "style-picker-furniture";

	public static bool Active { get; private set; }

	static StylePickerHighlight highlight;
	static StylePickerCatcher catcher;
	static IPanel hoveredPanel;
	static Vector2 hoveredOffset;
	static TitleBarToggle toggleButton;

	static readonly Func<Panel, bool> PreviousDismissal = Panel.SuppressPopupDismissal;

	static StylePickerTool()
	{
		Panel.SuppressPopupDismissal = SuppressPopupDismissal;
	}

	static bool SuppressPopupDismissal( Panel panel )
	{
		if ( PreviousDismissal?.Invoke( panel ) == true )
			return true;

		if ( Active ) return true;

		if ( HasFurniture( panel ) ) return true;

		//IconButton has no public Surface in tools code; the title bar is widgets.
		if ( toggleButton.IsValid() && toggleButton.ScreenRect.IsInside( Application.CursorPosition ) )
			return true;

		return false;
	}

	static bool HasFurniture( Panel panel )
	{
		for ( var p = panel; p is not null; p = p.Parent )
		{
			if ( p.Classes?.Contains( FurnitureClass ) == true )
				return true;
		}

		return false;
	}

	[Event( "editor.titlebar.buttons.build", Priority = 100 )]
	static void OnBuildTitleBarButtons( TitleBarButtons titleBarButtons )
	{
		//The bar's own toggle, so it is sized and centred like the buttons beside it
		toggleButton = titleBarButtons.AddToggleButton( "style", SetActive, Active );
		toggleButton.Name = "StylePickerToggle";
		toggleButton.ToolTip = "Style picker - hover an element and click to open its CSS";
	}

	static void SetActive( bool active )
	{
		if ( Active == active ) return;

		Active = active;

		if ( toggleButton.IsValid() )
			toggleButton.Value = active;

		if ( Active )
		{
			highlight = new StylePickerHighlight();
			catcher = new StylePickerCatcher( OnPicked );
		}
		else
		{
			catcher?.Destroy();
			catcher = null;
			highlight?.Destroy();
			highlight = null;
			hoveredPanel = null;
		}
	}

	[EditorEvent.Frame]
	static void Frame()
	{
		if ( !Active ) return;

		var window = Window.GetPointed();
		if ( window is not { IsValid: true } )
		{
			hoveredPanel = null;
			highlight?.Hide();
			return;
		}

		var layer = EditorOverlay.For( window );

		catcher?.CoverFor( layer );
		catcher?.KeepOnTop();

		hoveredPanel = FindPanelAt( Application.CursorPixels, layer );
		hoveredOffset = default;

		if ( GameViewAt( Application.CursorPosition ) is { } view )
		{
			var corner = view.GetScreenPixelRect().Position;
			var inside = FindGamePanelAt( Application.CursorPixels - corner, layer?.Parent );

			if ( inside.IsValid() )
			{
				hoveredPanel = inside;
				hoveredOffset = corner;
			}
		}

		if ( !hoveredPanel.IsValid() )
		{
			highlight?.Hide();
			return;
		}

		highlight?.PointAt( hoveredPanel, layer, hoveredOffset );
	}

	static SceneRenderingPanel GameViewAt( Vector2 pos )
	{
		foreach ( var view in SceneViewPanel.All )
		{
			if ( !view.IsValid() ) continue;
			if ( view.CurrentView != SceneViewMode.Game ) continue;

			foreach ( var viewport in view.Viewports )
			{
				var renderer = viewport?.Renderer;

				if ( !renderer.IsValid() || !renderer.IsVisible ) continue;
				if ( renderer.ScreenRect.IsInside( pos ) ) return renderer;
			}
		}

		return null;
	}

	static IPanel FindGamePanelAt( Vector2 local, IPanel window )
	{
		foreach ( var root in EditorUtility.GetRootPanels() )
		{
			if ( ReferenceEquals( root, window ) ) continue;

			var hit = root.GetPanelAt( local, true, true );
			if ( hit.IsValid() ) return hit;
		}

		return null;
	}

	static IPanel FindPanelAt( Vector2 pos, EditorOverlay layer )
	{
		var root = (IPanel)layer?.Parent;

		if ( root is null ) return null;

		if ( Window.ModalBlocksInput )
		{
			foreach ( var child in root.Children.Reverse() )
			{
				if ( child?.Classes?.Contains( "inside" ) != true )
					continue;

				var hit = child.GetPanelAt( pos, true, true );
				if ( hit.IsValid() )
					return hit;
			}
		}

		foreach ( var child in root.Children.Reverse() )
		{
			if ( IsOurs( child ) ) continue;

			if ( child is EditorOverlay )
			{
				foreach ( var above in child.Children.Reverse() )
				{
					if ( IsOurs( above ) ) continue;

					var found = above.GetPanelAt( pos, true, true );
					if ( found.IsValid() ) return found;
				}

				continue;
			}

			var hit = child.GetPanelAt( pos, true, true );
			if ( hit.IsValid() ) return hit;
		}

		return null;
	}

	static bool IsOurs( IPanel panel ) => panel?.Classes?.Contains( FurnitureClass ) == true;

	static void OnPicked()
	{
		if ( toggleButton.IsValid() && toggleButton.ScreenRect.IsInside( Application.CursorPosition ) )
		{
			SetActive( false );
			return;
		}

		var panel = hoveredPanel as Panel;

		SetActive( false );

		if ( !panel.IsValid() )
			return;

		//Straight to the rule, in the code editor. The full dump still goes to the console for when the rule that won is not the one you were after.
		PanelStyleDumper.Log( panel );

		if ( PanelStyleDumper.FindSource( panel ) is { } source )
			CodeEditor.OpenFile( source.Path, source.Line );
	}

	[Shortcut( "stylepicker.cancel", "ESCAPE", ShortcutType.Window )]
	static void Cancel()
	{
		if ( Active ) SetActive( false );
	}
}
