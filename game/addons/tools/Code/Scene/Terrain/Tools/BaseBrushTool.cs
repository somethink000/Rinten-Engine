using Rinten;

namespace Editor.TerrainEditor;

/// <summary>A brush over the terrain's mesh: the stroke, the undo, the preview - and one thing for a tool to say, what a dab of it does.</summary>
public abstract class BaseBrushTool : EditorTool
{
	protected TerrainEditorTool _parent;
	protected bool _dragging;

	Vector3 _lastHitWorldPos;
	Transform _lastHitTx;
	Vector2? _cursorLockPosition;

	//Whether Ctrl turns the brush round.
	protected bool AllowBrushInvert { get; set; }

	//Whether a dab moves the ground, and so wants the mesh made good under it afterwards.
	protected virtual bool Reshapes => true;

	//What the stroke is called in the undo list.
	protected virtual string StrokeName => DisplayInfo.For( this ).Name ?? "Terrain Stroke";

	//Where the stroke began, and which way the ground faced there - in the terrain's own space.
	protected Vector3 StrokeStart;
	protected Vector3 StrokeNormal;

	protected Plane StrokePlane;

	public virtual bool PaintMode { get; set; } = false;

	public BaseBrushTool( TerrainEditorTool terrainEditorTool )
	{
		_parent = terrainEditorTool;
	}

	//One dab of the brush: a ball of radius at centre, in the terrain's own space. amount is the
	//opacity by the time since the last dab - below nought with Ctrl held, where the tool allows it.
	protected abstract void OnDab( Terrain terrain, TerrainMesh mesh, Vector3 centre, float radius, float amount, Vector3 viewDirection );

	//Which way the ground faced where the cursor last met it, in the terrain's own space.
	protected Vector3 HitNormal = Vector3.Up;

	public virtual bool GetHitPosition( Terrain terrain, out Vector3 position )
	{
		if ( !terrain.RayIntersects( Gizmo.CurrentRay, Gizmo.RayDepth, out position, out var normal ) ) return false;

		//The side the eye is on: a ray into a cave meets the inside of its wall.
		var view = terrain.WorldTransform.NormalToLocal( Gizmo.CurrentRay.Forward );
		HitNormal = Vector3.Dot( normal, view ) > 0.0f ? -normal : normal;

		return true;
	}

	public override void OnUpdate()
	{
		var terrain = GetSelectedComponent<Terrain>() ?? Scene.Get<Terrain>();

		if ( !terrain.IsValid() || terrain.Mesh is not { } mesh )
			return;

		if ( !GetHitPosition( terrain, out var hitPosition ) )
		{
			//Off the ground with the button still down is still the end of the stroke when it comes up.
			if ( _dragging && !Gizmo.IsLeftMouseDown )
			{
				_dragging = false;

				if ( mesh.EndEdit() is { } ended )
				{
					SceneEditorSession.Active?.UndoSystem.Insert( StrokeName,
						() => { if ( terrain.IsValid() ) terrain.Mesh?.Undo( ended ); },
						() => { if ( terrain.IsValid() ) terrain.Mesh?.Redo( ended ); } );
				}
			}

			return;
		}

		var tx = terrain.WorldTransform;

		if ( Application.MouseButtons.HasFlag( MouseButtons.Middle ) )
		{
			_cursorLockPosition ??= Application.UnscaledCursorPosition;
			var d = Application.UnscaledCursorPosition - _cursorLockPosition.Value;

			if ( Gizmo.IsShiftPressed )
			{
				//A centimetre of radius per pixel dragged.
				_parent.BrushSettings.Size = (_parent.BrushSettings.Size + d.x * 0.01f).Clamp( 0.25f, 64.0f );
				_parent.BrushSettings.Opacity = (_parent.BrushSettings.Opacity - d.y * 0.002f).Clamp( 0f, 1f );
			}
			else if ( Gizmo.IsCtrlPressed && !_parent.BrushSettings.RandomRotation )
			{
				_parent.BrushSettings.Rotation = ((_parent.BrushSettings.Rotation + d.x * 0.5f) % 360f + 360f) % 360f;
			}

			Application.UnscaledCursorPosition = _cursorLockPosition.Value;
			SceneOverlay.Cursor = CursorShape.Blank;

			DrawBrushAdjustText();
			DrawBrushPreviewAt( _lastHitWorldPos, _lastHitTx, terrain );
			return;
		}
		else
		{
			if ( _cursorLockPosition.HasValue )
				SceneOverlay.Cursor = CursorShape.None;
			_cursorLockPosition = null;
		}

		_lastHitWorldPos = tx.PointToWorld( hitPosition );

		//The brush lies on the surface under it - a cliff, a cave's roof - and not on the terrain's plane: its up is the way the ground faces there.
		_lastHitTx = tx.WithRotation( Rotation.FromToRotation( Vector3.Up, tx.NormalToWorld( HitNormal ) ) );

		if ( Gizmo.IsLeftMouseDown )
		{
			if ( !_dragging )
			{
				if ( _parent.BrushSettings.RandomRotation )
					_parent.BrushSettings.Rotation = Random.Shared.NextSingle() * 360f;

				StrokePlane = new Plane( _lastHitWorldPos, tx.Rotation.Up );

				_dragging = true;

				StrokeStart = hitPosition;
				StrokeNormal = mesh.AreaNormal( hitPosition, _parent.BrushSettings.Size );

				mesh.BeginEdit();
			}

			//Every frame the button is down, by the time that frame took: a brush held still goes on raising, and one dragged fast does not skip.
			var amount = _parent.BrushSettings.Opacity * Math.Clamp( RealTime.Delta, 0.0f, 0.1f );

			if ( AllowBrushInvert && Gizmo.IsCtrlPressed ) amount = -amount;

			var view = tx.NormalToLocal( Gizmo.CurrentRay.Forward ).Normal;

			OnDab( terrain, mesh, hitPosition, _parent.BrushSettings.Size, amount, view );

			if ( Reshapes ) mesh.Remesh( hitPosition, _parent.BrushSettings.Size, MathF.Max( terrain.Storage.EdgeLength, 0.05f ) );
		}
		else if ( _dragging )
		{
			_dragging = false;

			//The stroke as one change, which the mesh kept a journal of.
			if ( mesh.EndEdit() is { } edit )
			{
				SceneEditorSession.Active?.UndoSystem.Insert( StrokeName,
					() => { if ( terrain.IsValid() ) terrain.Mesh?.Undo( edit ); },
					() => { if ( terrain.IsValid() ) terrain.Mesh?.Redo( edit ); } );
			}
		}

		DrawBrushPreviewAt( _lastHitWorldPos, _lastHitTx, terrain );
	}

	void DrawBrushPreviewAt( Vector3 worldPos, Transform tx, Terrain terrain = null )
	{
		if ( _parent.SimpleBrushMode )
			_parent.DrawSimpleBrushPreview( worldPos, tx, terrain );
		else
			_parent.DrawBrushPreview( new Transform( worldPos, tx.Rotation ), terrain );
	}

	void DrawBrushAdjustText()
	{
		var textScope = new TextRendering.Scope
		{
			TextColor = Color.White,
			FontSize = 16 * Gizmo.Settings.GizmoScale,
			FontName = "Roboto Mono",
			FontWeight = 600,
			LineHeight = 1,
			Outline = new TextRendering.Outline() { Color = Color.Black, Enabled = true, Size = 3 }
		};

		var offset = Vector2.ScreenUp * 24;

		if ( Gizmo.IsShiftPressed )
		{
			textScope.Text = $"Size: {_parent.BrushSettings.Size:0.##}m";
			Gizmo.Draw.ScreenText( textScope, _lastHitWorldPos, offset );

			textScope.Text = $"Opacity: {_parent.BrushSettings.Opacity:0.##}";
			Gizmo.Draw.ScreenText( textScope, _lastHitWorldPos, offset * 2 );
		}
		else if ( Gizmo.IsCtrlPressed && !_parent.BrushSettings.RandomRotation )
		{
			textScope.Text = $"Rotation: {_parent.BrushSettings.Rotation:0.#}°";
			Gizmo.Draw.ScreenText( textScope, _lastHitWorldPos, offset );
		}
	}
}
