namespace Editor.MeshEditor;

/// <summary>Draw a primitive by dragging its bounds in the scene.</summary>
[Title( "Block" ), Icon( "check_box_outline_blank" )]
public sealed class BlockEditor( PrimitiveTool tool ) : PrimitiveEditor( tool )
{
	PrimitiveBuilder _primitive = EditorTypeLibrary.Create<PrimitiveBuilder>( nameof( BlockPrimitive ) );

	//Set from the strip of buttons beside the modes rather than from a picker in this editor's own sheet - see PrimitiveTool.CreateOptionGroup . The sheet follows it; see BlockEditorPanel.Tick .
	/// <summary>Which shape a drag pulls out: a box, a cylinder, a flight of stairs.</summary>
	public PrimitiveBuilder Shape
	{
		get => _primitive;
		set
		{
			if ( value is null ) return;

			_primitive = value;
		}
	}

	BBox? _box;

	//The frame the block is drawn in: the world's, or one stood on the surface the drag started from when shift was held. The box and the drag are in it.
	Rotation _rotation = Rotation.Identity;

	Vector3 _dragStartPos;
	Vector3 _dragEndPos;
	bool _dragStarted;

	static float TextSize => 22 * Gizmo.Settings.GizmoScale * Application.DpiScale;

	//A storey. It was a hundred and twenty eight - that storey in inches - so the first
	//block dragged out of the tool came up a hundred and twenty eight metres tall.
	private const float DefaultHeight = 2.56f;

	public override bool CanBuild => _primitive is not null && _box.HasValue;
	public override bool InProgress => _dragStarted;

	public override Rotation BuildRotation => _rotation;

	public override PolygonMesh Build()
	{
		if ( !CanBuild ) return null;

		var box = _box.Value;
		if ( _primitive.Is2D )
		{
			//The tool drags on the ground and extrudes upwards, so a flat primitive is the one with no height.
			box.Maxs.y = box.Mins.y;
		}

		//The colour with it: a block drawn while the palette is on a tint comes out wearing that tint, the same as it comes out wearing the material.
		var paint = Tool.ActivePaint;
		var material = paint.Material;
		var primitive = new PrimitiveBuilder.PolygonMesh();
		_primitive.Material = material;
		_primitive.SetFromBox( box );
		_primitive.Build( primitive );

		var mesh = new PolygonMesh();
		var vertices = mesh.AddVertices( [.. primitive.Vertices] );

		foreach ( var face in primitive.Faces )
		{
			var index = mesh.AddFace( [.. face.Indices.Select( x => vertices[x] )] );
			paint.Apply( mesh, index );
		}

		mesh.TextureAlignToGrid( Transform.Zero );
		mesh.SetSmoothingAngle( 40.0f );

		return mesh;
	}

	/// <summary>Hand the finished block to the resize gizmo, which is what was being dragged a moment ago - so letting go of the mouse leaves something to keep pulling on.</summary>
	public override void OnCreated( MeshComponent component )
	{
		var selection = SceneEditorSession.Active.Selection;
		selection.Set( component.GameObject );

		if ( Tool.MeshTool is { } mapping )
		{
			EditorToolManager.SetSubTool( nameof( ObjectSelection ) );
			mapping.SetMoveMode<ResizeMode>();
		}

		_box = null;
		_dragStarted = false;
		_rotation = Rotation.Identity;
	}

	void StartStage( SceneTrace trace )
	{
		var tr = trace.Run();

		if ( !tr.Hit )
		{
			var plane = new Plane( Vector3.Up, 0.0f );
			if ( plane.TryTrace( Gizmo.CurrentRay, out var point, true ) )
			{
				tr.Hit = true;
				tr.Normal = plane.Normal;
				tr.EndPosition = point;
			}
		}
		else if ( tr.Component is MeshComponent mesh && mesh.Mesh is not null )
		{
			var face = mesh.Mesh.TriangleToFace( tr.Triangle );
			if ( face.IsValid )
			{
				mesh.Mesh.ComputeFaceNormal( face, out var localNormal );
				var center = mesh.WorldTransform.PointToWorld( mesh.Mesh.GetFaceCenter( face ) );
				tr.Normal = mesh.WorldTransform.NormalToWorld( localNormal );
				tr.EndPosition = new Plane( center, tr.Normal ).SnapToPlane( tr.EndPosition );
			}
		}

		if ( !tr.Hit ) return;

		//Shift stands the block on the surface under the pointer. The snap is done in that frame, and still leaves alone the axis the surface faces along.
		var rotation = Gizmo.IsShiftPressed ? RotationFromNormal( tr.Normal ) : Rotation.Identity;

		tr.EndPosition = rotation * SnapToSurface( rotation.Inverse * tr.EndPosition, rotation.Inverse * tr.Normal );

		if ( Gizmo.WasLeftMousePressed )
		{
			_rotation = rotation;
			_dragStartPos = rotation.Inverse * tr.EndPosition;
			_dragEndPos = _dragStartPos;
			_dragStarted = true;
			_box = null;
		}
		else
		{
			var size = 3.0f * Gizmo.Camera.Position.Distance( tr.EndPosition ) / 1000.0f;
			Gizmo.Draw.Color = Color.White;
			Gizmo.Draw.SolidSphere( tr.EndPosition, size );

			if ( !rotation.Equals( Rotation.Identity ) )
			{
				Gizmo.Draw.Color = Gizmo.Colors.Up;
				Gizmo.Draw.Line( tr.EndPosition, tr.EndPosition + rotation.Up * size * 8.0f );
			}
		}
	}

	//Box from a drag: footprint plus a storey of height, capped at the footprint's narrower side
	BBox Dragged( Vector3 point )
	{
		var box = new BBox( _dragStartPos, point );

		box.Maxs.y = box.Mins.y + MathF.Min( DefaultHeight, MathF.Min( box.Size.x, box.Size.z ) );

		return box;
	}

	void DraggingStage()
	{
		var plane = new Plane( _rotation * _dragStartPos, _rotation * Vector3.Up );

		//Where the pointer is on the ground, or where it last was. A ray along the horizon meets the ground nowhere, and giving up on that used
		//to skip the release with it: letting go while looking at the sky left the tool dragging a box that nothing could finish or cancel.
		if ( plane.TryTrace( Gizmo.CurrentRay, out var point, true ) )
			_dragEndPos = GridSnap( _rotation.Inverse * point, Vector3.Up );

		var box = Dragged( _dragEndPos );

		if ( Gizmo.IsLeftMouseDown )
		{
			DrawDragged( box );
			return;
		}

		_dragStarted = false;

		//A drag with no width or no depth is a line, and a line has no box in it. The two the drag has are the ground's - the plane traced here
		//is level, so y is the same at both ends of every drag and asking whether it moved was asking whether anything had happened at all.
		if ( box.Size.x.AlmostEqual( 0.0f ) || box.Size.z.AlmostEqual( 0.0f ) )
		{
			_box = null;

			return;
		}

		//Letting go builds it. It used to leave a preview sitting in the view that a second click somewhere else
		//turned into an object, so the thing somebody had just drawn was not in the scene and could not be resized.
		_box = box;

		Tool.Create();
	}

	//Draws the full box that releasing would create, not just its flat footprint
	void DrawDragged( BBox box )
	{
		//The box is in the block's own frame, the same as the object it becomes.
		using var scope = Gizmo.Scope( "drag" );
		Gizmo.Transform = new Transform( Vector3.Zero, _rotation );

		Gizmo.Draw.IgnoreDepth = true;

		Gizmo.Draw.Color = Gizmo.Colors.Active.WithAlpha( 0.15f );
		Gizmo.Draw.SolidBox( box );

		Gizmo.Draw.LineThickness = 2;
		Gizmo.Draw.Color = Gizmo.Colors.Active.WithAlpha( 0.8f );
		Gizmo.Draw.LineBBox( box );

		var textScope = new TextRendering.Scope
		{
			Text = null,
			TextColor = Color.White,
			FontSize = TextSize,
			FontName = "Roboto Mono",
			FontWeight = 400,
			LineHeight = 1,
			Outline = new TextRendering.Outline() { Color = Color.Black, Enabled = true, Size = 3 }
		};

		//The three sides of it, by their letters: x across, z along, y up.
		textScope.Text = $"W: {Measurement.Length( box.Size.x )}";
		textScope.TextColor = Gizmo.Colors.Red;
		Gizmo.Draw.ScreenText( textScope, _rotation * box.Mins.WithX( box.Center.x ), Vector2.ScreenUp * 32 );

		textScope.Text = $"L: {Measurement.Length( box.Size.z )}";
		textScope.TextColor = Gizmo.Colors.Blue;
		Gizmo.Draw.ScreenText( textScope, _rotation * box.Mins.WithZ( box.Center.z ), Vector2.ScreenUp * 32 );

		textScope.Text = $"H: {Measurement.Length( box.Size.y )}";
		textScope.TextColor = Gizmo.Colors.Green;
		Gizmo.Draw.ScreenText( textScope, _rotation * box.Mins.WithY( box.Center.y ), Vector2.ScreenUp * 32 );
	}

	public override void OnUpdate( SceneTrace trace )
	{
		//Another viewport of the same view: the button is never down there, and the drag used to end in the
		//first one of those to run. It only shows the box being pulled out - see EditorToolManager.IsInputView.
		if ( !Tool.IsInputView )
		{
			if ( _dragStarted ) DrawDragged( Dragged( _dragEndPos ) );
			return;
		}

		if ( Application.IsKeyDown( KeyCode.Escape ) ||
			 Application.IsKeyDown( KeyCode.Delete ) )
		{
			Cancel();
		}

		if ( Gizmo.Pressed.Any ) return;

		if ( _dragStarted )
			DraggingStage();
		else
			StartStage( trace );
	}

	void Cancel()
	{
		_box = null;
		_dragStarted = false;
		_rotation = Rotation.Identity;
	}

	public override void OnCancel()
	{
		Cancel();
	}

	static Vector3 GridSnap( Vector3 point, Vector3 normal )
	{
		var n = normal.Normal.Abs();
		var x = n.x >= n.y && n.x >= n.z;
		var y = !x && n.y >= n.z;
		return Gizmo.Snap( point, new Vector3( x ? 0 : 1, y ? 0 : 1, x || y ? 1 : 0 ) );
	}

	//Grid snap across the surface, then slid along the dominant axis back onto the surface's plane
	static Vector3 SnapToSurface( Vector3 point, Vector3 normal )
	{
		normal = normal.Normal;
		var snapped = GridSnap( point, normal );

		var n = normal.Abs();
		var axis = n.x >= n.y && n.x >= n.z ? new Vector3( 1, 0, 0 ) : n.y >= n.z ? new Vector3( 0, 1, 0 ) : new Vector3( 0, 0, 1 );
		var denom = normal.Dot( axis );
		if ( denom.AlmostEqual( 0.0f ) ) return snapped;

		var t = normal.Dot( point - snapped ) / denom;
		return snapped + axis * t;
	}

	//Build a frame whose up axis is the given surface normal, keeping the other two axes as close to the world axes as possible.
	static Rotation RotationFromNormal( Vector3 normal )
	{
		normal = normal.Normal;

		var dot = normal.Dot( Vector3.Up );
		if ( dot >= 0.9999f ) return Rotation.Identity;
		if ( dot <= -0.9999f ) return Rotation.From( 0.0f, 0.0f, 180.0f );

		var right = Vector3.Cross( Vector3.Up, normal ).Normal;
		var forward = Vector3.Cross( normal, right ).Normal;

		return Rotation.LookAt( forward, normal );
	}

	public override Panel CreatePanel() => new BlockEditorPanel( this );

	//What the shape being pulled out is made of - which sides of a box, how many steps. Which shape is
	//not here: it is the strip of buttons beside the modes, and this follows it rather than setting it.
	class BlockEditorPanel : ToolSettingsPanel
	{
		readonly BlockEditor _editor;

		PrimitiveBuilder _primitive;

		public BlockEditorPanel( BlockEditor editor )
		{
			_editor = editor;
			_primitive = _editor._primitive;

			AddClass( "nested" );

			BuildControlSheet();
		}

		void BuildControlSheet()
		{
			DeleteChildren( true );

			if ( _primitive is null ) return;

			var title = EditorTypeLibrary.GetType( _primitive.GetType() ).Title;
			var group = AddGroup( $"{title} Properties" );

			var sheet = AddSheet( group.Content );
			sheet.Target = _primitive.GetSerialized();
			sheet.Rebuild();

			AddShortcuts(
				("Align to Surface", "Shift + Drag")
			);
		}

		//The strip beside the modes is what changes the shape, and it does not know this panel is open - so what is in it is checked rather than pushed.
		public override void Tick()
		{
			base.Tick();

			if ( _primitive == _editor._primitive ) return;

			_primitive = _editor._primitive;
			BuildControlSheet();
		}
	}
}
