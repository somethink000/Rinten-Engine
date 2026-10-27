using System;
using System.Linq;
using Rinten.Internal;

namespace Editor;

//Hover any editor element and click to jump to its source; hit-tests the panel tree, not hover
internal static class SourceEyedropperTool
{
	static readonly Rinten.Diagnostics.Logger log = new( "Eyedropper" );

	/// <summary>Marks the panels the eyedropper puts on the overlay, so it doesn't pick itself.</summary>
	public const string FurnitureClass = "eyedropper-furniture";

	public static bool Active { get; private set; }

	static SourceEyedropperHighlight highlight;
	static SourceEyedropperCatcher catcher;
	static IPanel hoveredPanel;
	static Vector2 hoveredOffset;
	static Rect? hoveredPart;
	static TitleBarToggle toggleButton;

	//The title bar clears and rebuilds its buttons on every refresh - see TitleBar.OnHotload - so the eyedropper's
	//toggle has to be re-added here each time rather than made once in a constructor that never runs again.
	[Event( "editor.titlebar.buttons.build" )]
	static void OnBuildTitleBarButtons( TitleBarButtons titleBarButtons )
	{
		//The bar's own toggle rather than an IconButton of our own: the buttons beside it are all TitleBarToggle,
		//and one built any other way is a different size and paints its own way in a row of things that do not.
		toggleButton = titleBarButtons.AddToggleButton( "colorize", SetActive, Active );

		toggleButton.Name = "EyedropperToggle";
		toggleButton.ToolTip = "Eyedropper - hover an element and click to open its source";
	}

	static void SetActive( bool active )
	{
		if ( Active == active ) return;

		Active = active;

		if ( toggleButton.IsValid() )
			toggleButton.Value = active;

		if ( Active )
		{
			highlight = new SourceEyedropperHighlight();
			catcher = new SourceEyedropperCatcher( OnPicked );
		}
		else
		{
			catcher?.Destroy();
			catcher = null;
			highlight?.Destroy();
			highlight = null;
			hoveredPanel = null;
			hoveredPart = null;
		}
	}

	//Overlay of the window under the pointer, since each window has its own tree and coordinates
	static EditorOverlay Layer
	{
		get
		{
			if ( Window.UnderPointer is { } window )
				return EditorOverlay.For( window );

			return EditorWindow.IsValid() ? EditorOverlay.For( EditorWindow ) : null;
		}
	}

	//Every frame while picking: find what is under the cursor and move the box onto it.
	[EditorEvent.Frame]
	static void Frame()
	{
		if ( !Active ) return;

		var layer = Layer;

		catcher?.CoverEverything();

		hoveredPanel = PanelAtCursor( layer, out hoveredOffset );

		hoveredPart = PartOf( hoveredPanel );

		if ( !hoveredPanel.IsValid() )
		{
			highlight?.Hide();
			return;
		}

		highlight?.PointAt( hoveredPanel, layer, hoveredOffset, SourceLabel( hoveredPanel ), hoveredPart );
	}

	//The panel under the cursor, and where its own space starts in the window's pixels. Over a game
	//view, pick the game's own UI using the cursor in the picture's pixels
	internal static IPanel PanelAtCursor( EditorOverlay layer, out Vector2 offset )
	{
		var found = FindPanelAt( Application.CursorPixels, layer );

		offset = default;

		if ( GameViewAt( Application.CursorPosition ) is { } view && OverThePicture( found, view ) )
		{
			var corner = view.GetScreenPixelRect().Position;
			var inside = FindGamePanelAt( Application.CursorPixels - corner );

			if ( inside.IsValid() )
			{
				found = inside;
				offset = corner;
			}
		}

		return found;
	}

	//The widget part under the cursor (e.g. a row via Widget.PartAt), in window pixels
	static Rect? PartOf( IPanel panel )
	{
		if ( !panel.IsValid() ) return null;

		if ( EditorUtility.WidgetOf( panel ) is not { IsValid: true } widget )
			return null;

		if ( widget.PartAt( widget.FromScreen( Application.CursorPosition ) ) is not { } part )
			return null;

		//The widget answers in its own units, from its own corner
		var scale = widget.DpiScale;

		return new Rect( ( part.Position + widget.ScreenPosition ) * scale, part.Size * scale );
	}

	//True if the hit is the game picture, not an editor overlay (stats, gizmo, toolbar) over it
	static bool OverThePicture( IPanel panel, SceneRenderingPanel view )
	{
		//Both the picture and the sheet the overlays hang on are panels under the viewport, and the panel asked about came out of the editor's
		//own tree - so the question is whether the walk up from it reaches either of them before it reaches anything else the editor put in front.
		for ( var at = panel; at is not null; at = at.Parent )
		{
			if ( ReferenceEquals( at, view ) || at is SceneOverlayPanel ) return true;
		}

		return false;
	}

	//The running game view under this point, recognised by its renderer having engine overlays on
	internal static SceneRenderingPanel GameViewAt( Vector2 pos )
	{
		foreach ( var view in SceneViewPanel.All )
		{
			if ( !view.IsValid() ) continue;

			foreach ( var viewport in view.Viewports )
			{
				var renderer = viewport?.Renderer;

				if ( !renderer.IsValid() || !renderer.IsVisible ) continue;
				if ( !renderer.EnableEngineOverlays ) continue;

				if ( renderer.ScreenRect.IsInside( pos ) ) return renderer;
			}
		}

		return null;
	}

	//The game's topmost panel at a point in its picture, searching every root no editor window drives
	static IPanel FindGamePanelAt( Vector2 local )
	{
		foreach ( var root in EditorUtility.GetGameRootPanels() )
		{
			var hit = root.PanelUnder( local, IsOurs, CanName );

			//The root on its own means the game draws nothing at this point: its root covers the whole viewport whether or
			//not there is any interface in it, and a box round the whole picture says less than the view it is drawn into.
			if ( hit.IsValid() && !ReferenceEquals( hit, root ) ) return hit;
		}

		return null;
	}

	//Topmost panel at a point in this window's tree, excluding the eyedropper's own overlay
	static IPanel FindPanelAt( Vector2 pos, EditorOverlay layer )
	{
		//As IPanel throughout: the panel's own methods are the engine's.
		if ( layer?.Parent is not IPanel root ) return null;

		return root.PanelUnder( pos, IsOurs, CanName );
	}

	static bool IsOurs( IPanel panel ) => panel?.Classes?.Contains( FurnitureClass ) == true;

	//Anything drawn can be named, ignoring pointer-events: none, except the overlay layer itself
	static bool CanName( IPanel panel ) => panel is not EditorOverlay;

	static void OnPicked()
	{
		//The eyedropper's own button, clicked to put it away again - that is what it means here, not a thing to go and look at the source of.
		if ( toggleButton.IsValid() && toggleButton.ScreenRect.IsInside( Application.CursorPosition ) )
		{
			SetActive( false );
			return;
		}

		if ( hoveredPanel.IsValid() )
		{
			if ( TryGetSource( hoveredPanel, out var file, out var line ) )
			{
				CodeEditor.OpenFile( file, line );
			}
			else
			{
				log.Warning( $"nothing to open for [{hoveredPanel.Classes}]" );
			}
		}

		SetActive( false );
	}

	//Source location of this panel, or of the nearest ancestor that has one
	static bool TryGetSource( IPanel panel, out string file, out int line )
	{
		file = null;
		line = 0;

		string named = null;
		var namedLine = 0;

		for ( var at = panel; at.IsValid(); at = at.Parent )
		{
			if ( !string.IsNullOrWhiteSpace( at.SourceFile ) )
			{
				if ( CanOpen( at.SourceFile ) )
				{
					file = at.SourceFile;
					line = at.SourceLine;
					return true;
				}

				//A razor panel that says where it was written and names a file nothing here can find. Kept in case nothing better
				//turns up, rather than returned over the type behind it - that type's debug file is a path that does open.
				named ??= at.SourceFile;
				if ( namedLine == 0 ) namedLine = at.SourceLine;
			}

			if ( TryGetSourceFromClasses( at.Classes, out file, out line ) )
				return true;
		}

		if ( named is not null )
		{
			file = named;
			line = namedLine;
			return true;
		}

		return false;
	}

	//What the box says about what it is over: the file a click would open, and where in it.
	static string SourceLabel( IPanel panel )
	{
		if ( !panel.IsValid() )
			return null;

		if ( !TryGetSource( panel, out var file, out var line ) )
			return "no source";

		var name = FileName( file );

		return line > 0 ? $"{name}:{line}" : name;
	}

	//The last part of a path, whichever slash it is written with - a razor file's is recorded as the compiler saw it.
	static string FileName( string path )
	{
		var cut = path.LastIndexOfAny( new[] { '/', '\\' } );

		return cut < 0 ? path : path[(cut + 1)..];
	}

	static readonly Dictionary<string, bool> openable = new();

	//Whether a recorded path names a file something can be opened at. Remembered because this is asked of every
	//panel between the cursor and the root, every frame the eyedropper is pointing, and answering it reads the disk.
	static bool CanOpen( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return false;

		if ( !openable.TryGetValue( path, out var can ) )
		{
			can = CodeEditor.CanOpenFile( path );
			openable[path] = can;
		}

		return can;
	}

	static readonly Dictionary<string, (string File, int Line)> byClasses = new();

	//The file behind a panel's classes, remembered - this is asked every frame while pointing.
	static bool TryGetSourceFromClasses( string classes, out string file, out int line )
	{
		file = null;
		line = 0;

		if ( string.IsNullOrWhiteSpace( classes ) )
			return false;

		if ( !byClasses.TryGetValue( classes, out var found ) )
		{
			found = FindSourceForClasses( classes );
			byClasses[classes] = found;
		}

		file = found.File;
		line = found.Line;

		return file is not null;
	}

	//A panel carries no C# type, but a widget's panel wears its own class name and every type it descends from, most derived first
	//- see WidgetSurface.ClassesFor. Walking them in that order finds the nearest one that was actually written down somewhere.
	static (string File, int Line) FindSourceForClasses( string classes )
	{
		foreach ( var name in classes.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var type = EditorTypeLibrary.GetType( name );

			if ( type is null )
				continue;

			//The compiler writes a path relative to the project it compiled, so what is on the type only names a file if the editor knows where that project's
			//folder is. It knows a game's; an engine type's came out as 'Systems\UI\Controls\Label.cs' and opened nowhere, which is most of the editor.
			if ( !string.IsNullOrWhiteSpace( type.SourceFile ) && CanOpen( type.SourceFile ) )
				return (type.SourceFile, type.SourceLine);

			//Built rather than compiled here, or written down somewhere this cannot reach: the debug file beside the assembly has the whole path.
			if ( SourceFromPdb.TryGet( type.TargetType, out var file, out var line ) )
				return (file, line);

			//Nothing better than what the type says, so say it and let whoever tries to open it report that it could not be found.
			if ( !string.IsNullOrWhiteSpace( type.SourceFile ) )
				return (type.SourceFile, type.SourceLine);
		}

		return default;
	}

	[Shortcut( "eyedropper.cancel", "ESCAPE", ShortcutType.Window )]
	static void Cancel()
	{
		if ( Active ) SetActive( false );
	}
}
