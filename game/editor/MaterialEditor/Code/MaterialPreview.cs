using Rinten.UI;
using System.Collections.Generic;

namespace Editor.Materials;

//The dock is the only widget here; everything in it is panels. The dock manager's central slot is arranged as a widget - see PanelDockHost - and nothing below it needs to be.
/// <summary>The middle of the material editor: the shape wearing the material.</summary>
public sealed class MaterialStage : PanelDockHost
{
	public MaterialPreviewPanel Preview { get; }

	public MaterialStage( IMaterialDocument document ) : base( null )
	{
		Name = "MaterialPreview";
		WindowTitle = "Preview";
		SetWindowIcon( "visibility" );

		Preview = new MaterialPreviewPanel( document );

		SetContent( Preview );
	}
}

/// <summary>Material preview: the shape picker strip on top and a SceneRenderingPanel following the document</summary>
[StyleSheet( "MaterialPreview.cs.scss" )]
public sealed class MaterialPreviewPanel : EditorPanel
{
	const string ShapeCookie = "MaterialEditor.Preview.Shape";

	readonly IMaterialDocument _document;
	readonly MaterialPreviewScene _scene;
	readonly Dictionary<PreviewShape, PanelToolbarButton> _shapes = new();

	public MaterialPreviewPanel( IMaterialDocument document )
	{
		_document = document;

		AddClass( "material-preview" );

		_scene = new MaterialPreviewScene( EditorCookie.Get( ShapeCookie, PreviewShape.Cube ) );

		var bar = AddChild<PanelToolbar>();
		AddShape( bar, PreviewShape.Cube, "Cube", "view_in_ar" );
		AddShape( bar, PreviewShape.Sphere, "Sphere", "circle" );
		AddShape( bar, PreviewShape.Plane, "Plane", "crop_square" );
		bar.AddSeparator();
		bar.AddIcon( "center_focus_strong", "Reset View", _scene.ResetView );
		bar.AddStretch();

		//The frame around the picture is the two pixels the sheet paints behind the view - it reads as a
		//bezel, and the camera's own clear colour is the same, so an empty scene is one flat rectangle.
		var frame = AddChild<Panel>( "viewport" );
		frame.AddChild( new MaterialPreviewView( _scene ) );

		_document.Opened += Apply;
		_document.Changed += OnChanged;

		Apply();
	}

	public override void OnDeleted()
	{
		_document.Opened -= Apply;
		_document.Changed -= OnChanged;

		//The view is deleted with the children above; the scene it was looking at goes after it.
		base.OnDeleted();

		_scene.Dispose();
	}

	void OnChanged( MaterialEditKind kind ) => Apply();

	void Apply() => _scene.Apply( _document.Definition );

	//One of the shapes, lit while it is the one worn - a radio rather than three switches.
	void AddShape( PanelToolbar bar, PreviewShape shape, string title, string icon )
	{
		_shapes[shape] = bar.AddToggle( icon, title, _scene.Worn == shape, _ => Wear( shape ) );
	}

	void Wear( PreviewShape shape )
	{
		_scene.Wear( shape );
		EditorCookie.Set( ShapeCookie, shape );

		foreach ( var (worn, button) in _shapes )
			button.IsActive = worn == shape;
	}
}

//Orbit with left drag, zoom with wheel; release also read from button state, as panels lack grab
sealed class MaterialPreviewView : SceneRenderingPanel
{
	readonly MaterialPreviewScene _scene;

	bool _orbiting;
	Vector2 _lastCursor;
	CursorShape _cursor = CursorShape.None;

	public MaterialPreviewView( MaterialPreviewScene scene )
	{
		_scene = scene;

		AddClass( "view" );

		Scene = scene.Scene;
		Camera = scene.Camera;
		ShowEditorOverlays = false;

		OnPreFrame += () => _scene.Frame( RealTime.Delta );
	}

	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );

		if ( e.MouseButton != MouseButtons.Left ) return;

		_orbiting = true;
		_lastCursor = Application.CursorPosition;
		e.StopPropagation();
	}

	protected override void OnMouseUp( MousePanelEvent e )
	{
		base.OnMouseUp( e );

		if ( e.MouseButton == MouseButtons.Left )
			_orbiting = false;
	}

	/// <summary>The wheel dollies the camera. Deliberately not the base behaviour: that hands the notch to the game, and there is no game in a preview.</summary>
	public override void OnMouseWheel( Vector2 value ) => _scene.Zoom( value.y );

	public override void Tick()
	{
		base.Tick();

		if ( _orbiting )
		{
			if ( !Application.MouseButtons.HasFlag( MouseButtons.Left ) )
			{
				_orbiting = false;
			}
			else
			{
				var cursor = Application.CursorPosition;
				_scene.Orbit( cursor - _lastCursor );
				_lastCursor = cursor;
			}
		}

		SetViewCursor( _orbiting ? CursorShape.ClosedHand : IsUnderMouse ? CursorShape.SizeAll : CursorShape.None );
	}

	//The pointer's shape over the picture. A panel has no cursor of its own - this ends up on
	//the widget the dock is drawn in - so it is written when it changes and never per frame.
	void SetViewCursor( CursorShape shape )
	{
		if ( _cursor == shape ) return;

		_cursor = shape;
		Cursor = shape;
	}

	public override void OnDeleted()
	{
		//The cursor a panel sets is the whole dock's, so it is handed back before the panel goes.
		SetViewCursor( CursorShape.None );

		base.OnDeleted();
	}
}
