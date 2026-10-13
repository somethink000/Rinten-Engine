
using HalfEdgeMesh;

namespace Editor.MeshEditor;

/// <summary>Select and edit objects.</summary>
[Title( "Object Selection" )]
[Icon( "view_in_ar" )]
[Alias( "tools.object-selection" )]
[Group( "5" )]
public sealed partial class ObjectSelection( MeshTool tool ) : SelectionTool( tool )
{
	readonly Dictionary<GameObject, Transform> _startPoints = [];
	readonly Dictionary<MeshVertex, Vector3> _transformVertices = [];
	IDisposable _undoScope;

	MeshComponent[] _meshes = [];
	GameObject[] _objects = [];

	protected override bool ShowSelectionBoundsDefault => true;

	readonly Dictionary<MeshComponent, FaceTextureParameters[]> _startFaceParameters = [];

	readonly record struct FaceTextureParameters( FaceHandle Face, Vector4 AxisU, Vector4 AxisV, Vector2 Scale );
	readonly record struct MeshTopology( int VertexCount, int EdgeCount, int FaceCount );

	public override void BuildSceneContextMenu( Menu menu, Ray ray, SceneTraceResult? trace )
	{
		menu.AddSeparator();

		bool hasMeshes = _meshes.Length > 0;
		bool manyMeshes = _meshes.Length > 1;
		bool hasObjects = _objects.Length > 0;

		if ( hasObjects )
		{
			var selection = menu.AddMenu( "Selection", "select_all" );
			AddMenuOption( selection, "Select Similar", "filter_center_focus", SelectSimilar, "mesh.select-similar", true );
		}

		bool convertible = _objects
			.Select( x => x.GetComponent<ModelRenderer>() )
			.Any( x => x.IsValid() && x.Model.IsValid() && x.Model.HasRenderMeshes() );

		if ( manyMeshes || convertible || hasMeshes )
		{
			var ops = menu.AddMenu( "Object Operations", "build" );
			AddMenuOption( ops, "Merge Meshes", "merge", "mesh.merge-meshes", manyMeshes );
			AddMenuOption( ops, "Boolean Tool", "difference", "mesh.boolean-tool", manyMeshes );
			AddMenuOption( ops, "Convert To Mesh", "category", "mesh.convert-model-to-mesh", convertible );
			AddMenuOption( ops, "Flip Faces", "flip", "mesh.flip-all-mesh-faces", hasMeshes );
		}

		if ( hasMeshes )
		{
			var transform = menu.AddMenu( "Transform", "straighten" );
			AddMenuOption( transform, "Bake Scale", "fit_screen", "mesh.bake-scale", true );
			AddMenuOption( transform, "Set Origin To Pivot", "gps_fixed", "mesh.set-origin-to-pivot", hasObjects );
			AddMenuOption( transform, "Center Origin", "filter_center_focus", "mesh.center-origin", true );
			AddMenuOption( transform, "Align To View", "visibility", "gameObject.align-to-view", true );
			transform.AddSeparator();
			AddMenuOption( transform, "Align Down Local", "vertical_align_bottom", () => AlignDown( useLocalDown: true ), null, true );
			AddMenuOption( transform, "Align Down World", "vertical_align_bottom", () => AlignDown( useLocalDown: false ), null, true );
			AddMenuOption( transform, "Align To Closest Normal", "swap_vert", AlignToClosestNormal, null, true );
			transform.AddSeparator();
			AddMenuOption( transform, "Flip Horizontal", "swap_horiz", "mesh.flip-horizontal", true );
			AddMenuOption( transform, "Flip Vertical", "swap_vert", "mesh.flip-vertical", true );
		}

		if ( hasObjects )
		{
			var pivot = menu.AddMenu( "Pivot", "my_location" );
			AddMenuOption( pivot, "Previous", "navigate_before", "mesh.previous-pivot", true );
			AddMenuOption( pivot, "Next", "navigate_next", "mesh.next-pivot", true );
			AddMenuOption( pivot, "Clear", "clear", "mesh.clear-pivot", true );
			AddMenuOption( pivot, "Center", "filter_center_focus", "mesh.center-pivot", true );
			AddMenuOption( pivot, "World Origin", "public", "mesh.zero-pivot", true );
		}
	}

	protected override void OnStartDrag()
	{
		if ( _startPoints.Count > 0 ) return;

		if ( Gizmo.IsShiftPressed )
		{
			_undoScope ??= SceneEditorSession.Active.UndoScope( "Duplicate Object(s)" )
				.WithGameObjectCreations()
				.WithComponentChanges( _meshes )
				.Push();

			DuplicateSelectionKeepingPivot();
		}
		else
		{
			_undoScope ??= SceneEditorSession.Active.UndoScope( "Transform Object(s)" )
				.WithGameObjectChanges( _objects, GameObjectUndoFlags.Properties )
				.WithComponentChanges( _meshes )
				.Push();
		}

		foreach ( var go in _objects )
		{
			_startPoints[go] = go.WorldTransform;
		}

		_startFaceParameters.Clear();

		foreach ( var mesh in _meshes )
		{
			foreach ( var vertex in mesh.Mesh.VertexHandles )
			{
				var v = new MeshVertex( mesh, vertex );
				_transformVertices[v] = mesh.WorldTransform.PointToWorld( mesh.Mesh.GetVertexPosition( vertex ) );
			}

			var parameters = new List<FaceTextureParameters>();

			foreach ( var face in mesh.Mesh.FaceHandles )
			{
				mesh.Mesh.GetFaceTextureParameters( face, out var axisU, out var axisV, out var scale );
				parameters.Add( new FaceTextureParameters( face, axisU, axisV, scale ) );
			}

			_startFaceParameters[mesh] = parameters.ToArray();
		}
	}

	protected override void OnEndDrag()
	{
		_startPoints.Clear();
		_startFaceParameters.Clear();
		_transformKind = TextureLockTransform.Move;

		_undoScope?.Dispose();
		_undoScope = null;
	}

	public override void Translate( Vector3 delta )
	{
		_transformKind = TextureLockTransform.Move;

		foreach ( var entry in _startPoints )
		{
			entry.Key.WorldPosition = entry.Value.Position + delta;
		}

		Pivot.Drag( delta );
	}

	public override void Rotate( Vector3 origin, Rotation basis, Rotation delta )
	{
		_transformKind = TextureLockTransform.Rotate;

		foreach ( var entry in _startPoints )
		{
			var rot = basis * delta * basis.Inverse;
			var position = entry.Value.Position - origin;
			position *= rot;
			position += origin;
			rot *= entry.Value.Rotation;
			var scale = entry.Value.Scale;
			entry.Key.WorldTransform = new Transform( position, rot, scale );
		}
	}

	public override void Scale( Vector3 origin, Rotation basis, Vector3 deltaScale )
	{
		_transformKind = TextureLockTransform.Scale;

		var scaleFromIndividualOrigins = !GlobalSpace && _startPoints.Count > 1;

		foreach ( var entry in _startPoints )
		{
			var position = entry.Value.Position;

			if ( !scaleFromIndividualOrigins )
			{
				position -= origin;
				position *= basis.Inverse;
				position *= deltaScale;
				position *= basis;
				position += origin;
			}

			entry.Key.WorldTransform = new Transform(
				position,
				entry.Value.Rotation,
				entry.Value.Scale * deltaScale
			);
		}
	}

	public override void Resize( Vector3 origin, Rotation basis, Vector3 scale )
	{
		_transformKind = TextureLockTransform.Scale;

		var invBasis = basis.Inverse;

		foreach ( var entry in _startPoints )
		{
			var start = entry.Value;
			var local = invBasis * (start.Position - origin);
			local *= scale;
			var position = origin + (basis * local);

			if ( entry.Key.GetComponent<MeshComponent>() is { } mc && mc.IsValid() )
			{
				mc.Mesh.SetTransform( mc.WorldTransform.WithPosition( position ) );
			}
			else
			{
				entry.Key.WorldTransform = new Transform( position, start.Rotation, start.Scale * scale );
			}
		}

		foreach ( var entry in _transformVertices )
		{
			var local = invBasis * (entry.Value - origin);
			local *= scale;
			var worldPos = origin + (basis * local);
			var mesh = entry.Key.Component.Mesh;
			mesh.SetVertexPosition( entry.Key.Handle, mesh.Transform.PointToLocal( worldPos ) );
		}

		foreach ( var start in _startPoints )
		{
			if ( start.Key.GetComponent<MeshComponent>() is not { } mc || !mc.IsValid() ) continue;

			mc.WorldTransform = mc.Mesh.Transform;
			mc.RebuildMesh();
		}
	}

	protected override void OnUpdateDrag()
	{
		if ( ShouldLockTexture() )
			return;

		foreach ( var (mesh, parameters) in _startFaceParameters )
		{
			if ( !mesh.IsValid() )
				continue;

			foreach ( var p in parameters )
			{
				if ( !p.Face.IsValid )
					continue;

				mesh.Mesh.SetFaceTextureParameters( p.Face, p.AxisU, p.AxisV, p.Scale );
			}

			mesh.RebuildMesh();
		}
	}

	public override void Nudge( Vector2 direction )
	{
		if ( _objects.Length == 0 ) return;

		var viewport = SceneViewPanel.Current?.LastSelectedViewport;
		if ( !viewport.IsValid() ) return;

		var gizmo = viewport.GizmoInstance;
		if ( gizmo is null ) return;

		using var gizmoScope = gizmo.Push();
		if ( Gizmo.Pressed.Any ) return;

		using var scope = SceneEditorSession.Scope();
		var duplicate = Gizmo.IsShiftPressed;

		var rotation = CalculateSelectionBasis();
		var delta = Gizmo.Nudge( rotation, direction );

		Pivot.BeginDrag();

		using ( duplicate
			? SceneEditorSession.Active.UndoScope( "Duplicate Object(s)" )
				.WithGameObjectCreations()
				.WithComponentChanges( _meshes )
				.Push()
			: SceneEditorSession.Active.UndoScope( "Nudge Mesh(s)" )
				.WithGameObjectChanges( _objects, GameObjectUndoFlags.Properties )
				.Push() )
		{
			if ( duplicate )
			{
				DuplicateSelectionKeepingPivot();
			}

			foreach ( var go in _objects )
			{
				go.WorldPosition -= delta;
			}

			Pivot.Translate( -delta );
		}

		Pivot.EndDrag();

		Tool?.MoveMode?.OnBegin( this );
	}

	public override void NudgeRotation( Vector2 direction )
	{
		if ( !Selection.Any() ) return;

		var viewport = SceneViewPanel.Current?.LastSelectedViewport;
		if ( !viewport.IsValid() ) return;

		var gizmo = viewport.GizmoInstance;
		if ( gizmo is null ) return;

		using var gizmoScope = gizmo.Push();
		if ( Gizmo.Pressed.Any ) return;

		var basis = CalculateSelectionBasis();
		var screenLeft = Gizmo.Nudge( basis, Vector2.Left ).Normal;
		var screenUp = Gizmo.Nudge( basis, Vector2.Up ).Normal;
		var faceNormal = screenLeft.Cross( screenUp ).Normal;

		var axis = direction.x != 0.0f
			? faceNormal
			: screenLeft;

		var angle = direction.x != 0.0f
			? direction.x * Gizmo.Settings.AngleSpacing
			: direction.y * Gizmo.Settings.AngleSpacing;

		var delta = Rotation.FromAxis( axis, angle );

		StartDrag();

		try
		{
			Rotate( Pivot.Position, Rotation.Identity, delta );
			UpdateDrag();
		}
		finally
		{
			EndDrag();
		}

		Tool?.MoveMode?.OnBegin( this );
	}

	public override BBox CalculateLocalBounds()
	{
		var invBasis = CalculateSelectionBasis().Inverse;

		var points = _objects
			.Where( x => x.IsValid() )
			.SelectMany( go =>
			{
				if ( go.GetComponent<MeshComponent>() is { } mc && mc.IsValid() )
					return mc.Mesh.VertexHandles.Select( v => invBasis * mc.WorldTransform.PointToWorld( mc.Mesh.GetVertexPosition( v ) ) );

				return go.GetBounds().Corners.Select( c => invBasis * c );
			} );

		return BBox.FromPoints( points );
	}

	public override Rotation CalculateSelectionBasis()
	{
		if ( GlobalSpace ) return Rotation.Identity;

		var mesh = _objects.FirstOrDefault();
		return mesh.IsValid() ? mesh.WorldRotation : Rotation.Identity;
	}

	public override void OnEnabled()
	{
		var objects = Selection.OfType<GameObject>()
			.ToArray();

		var connectedObjects = Selection.OfType<IMeshElement>()
			.Select( x => x.Component.GameObject )
			.ToArray();

		Selection.Clear();

		foreach ( var go in objects ) Selection.Add( go );
		foreach ( var go in connectedObjects ) Selection.Add( go );

		//Only restore previous selection if we don't have any selected objects ready to go.
		if ( !Selection.OfType<GameObject>().Any() )
		{
			RestorePreviousSelection<GameObject>();
		}

		OnSelectionChanged();

		SubscribeUndo();
	}

	public override void OnDisabled()
	{
		EndGrab();
		ResetGrabCursor();

		UnsubscribeUndo();

		SaveCurrentSelection<GameObject>();
	}

	protected override void OnAfterUndoRedo() => RebuildSelectionCache();

	public override void OnUpdate()
	{
		GlobalSpace = Gizmo.Settings.GlobalSpace;

		Pivot.Update();
		UpdateMoveMode();
		UpdateHovered();
		UpdateGrab();
		UpdateSelectionMode();
		UpdateCursor();

		if ( ShowSelectionBounds )
			DrawBounds();
	}

	void UpdateMoveMode()
	{
		if ( Tool is null ) return;
		if ( Tool.MoveMode is null ) return;
		if ( _objects.Length == 0 ) return;

		Tool.MoveMode.Update( this );
	}

	public override Vector3 CalculateSelectionOrigin()
	{
		var mesh = _objects.FirstOrDefault();
		return mesh.IsValid() ? mesh.WorldPosition : default;
	}

	//Uses mesh vertices for bounds, since physics-based GetBounds is larger than the mesh
	public override BBox CalculateSelectionBounds()
	{
		var points = _objects
			.Where( x => x.IsValid() )
			.SelectMany( go =>
			{
				if ( go.GetComponent<MeshComponent>() is { } mc && mc.IsValid() && mc.Mesh is not null )
					return mc.Mesh.VertexHandles.Select( v => mc.WorldTransform.PointToWorld( mc.Mesh.GetVertexPosition( v ) ) );

				return go.GetBounds().Corners;
			} );

		return BBox.FromPoints( points );
	}

	public override void OnSelectionChanged()
	{
		//Undo restores the selection, reselecting the same objects. That isn't a change, so
		//the pivot stays where it is. Ids are used because a restore rebuilds the objects.
		var previous = _objects.Select( x => x?.Id ).ToHashSet();

		RebuildSelectionCache();

		if ( !previous.SetEquals( _objects.Select( x => x?.Id ) ) )
			Pivot.Reset();
	}

	//Duplicating swaps the selection for copies sat in the same place. That isn't a selection
	//change as far as the pivot is concerned, so refresh the cache without resetting it.
	void DuplicateSelectionKeepingPivot()
	{
		DuplicateSelection();
		RebuildSelectionCache();
	}

	void RebuildSelectionCache()
	{
		_objects = Selection.OfType<GameObject>().ToArray();
		_meshes = Selection.OfType<GameObject>()
			.Select( x => x.GetComponent<MeshComponent>() )
			.Where( x => x.IsValid() )
			.ToArray();

		_transformVertices.Clear();

		foreach ( var mesh in _meshes )
		{
			foreach ( var vertex in mesh.Mesh.VertexHandles )
			{
				var v = new MeshVertex( mesh, vertex );
				_transformVertices[v] = mesh.WorldTransform.PointToWorld( mesh.Mesh.GetVertexPosition( vertex ) );
			}
		}
	}

	public void SelectSimilar()
	{
		var meshTopologies = _meshes
			.Where( x => x.IsValid() && x.Mesh is not null )
			.Select( GetTopology )
			.ToHashSet();

		var models = _objects
			.Select( x => x.GetComponent<ModelRenderer>() )
			.Where( x => x.IsValid() && x.Model.IsValid() )
			.Select( x => x.Model )
			.ToHashSet();

		if ( meshTopologies.Count == 0 && models.Count == 0 )
			return;

		using var scope = SceneEditorSession.Scope();
		using var undoScope = SceneEditorSession.Active
			.UndoScope( "Select Similar Objects" )
			.Push();

		foreach ( var go in Scene.GetAllObjects( true ) )
		{
			if ( go == Scene || go.Tags.Has( "hidden" ) )
				continue;

			bool matchingMesh = go.GetComponent<MeshComponent>() is { } meshComponent
				&& meshComponent.IsValid()
				&& meshComponent.Mesh is not null
				&& meshTopologies.Contains( GetTopology( meshComponent ) );

			bool matchingModel = go.GetComponent<ModelRenderer>() is { } modelRenderer
				&& modelRenderer.IsValid()
				&& modelRenderer.Model.IsValid()
				&& models.Contains( modelRenderer.Model );

			if ( matchingMesh || matchingModel )
				Selection.Add( go );
		}
	}

	private static MeshTopology GetTopology( MeshComponent component )
	{
		var mesh = component.Mesh;

		return new MeshTopology(
			mesh.VertexHandles.Count(),
			mesh.HalfEdgeHandles.Count() / 2,
			mesh.FaceHandles.Count()
		);
	}

	void UpdateSelectionMode()
	{
		if ( !Gizmo.HasMouseFocus ) return;
		if ( !IsAllowedToSelect ) return;

		//Not at the end of a drag that carried something - see UpdateGrab.
		if ( Gizmo.WasLeftMouseReleased && !Gizmo.Pressed.Any && !IsBoxSelecting && !_grabbed )
		{
			using ( Scene.Editor?.UndoScope( "Deselect all" ).Push() )
			{
				EditorScene.Selection.Clear();
			}
		}
	}

	void UpdateHovered()
	{
		_hovered = default;

		if ( IsBoxSelecting ) return;

		if ( !IsAllowedToSelect ) return;

		//The nearest hit that belongs to an object, rather than the nearest hit of any kind: a scene draws
		//plenty that nobody owns, and stopping at one of those left whatever was behind it unhoverable.
		var tr = default( SceneTraceResult );

		foreach ( var hit in MeshTrace.RunAll() )
		{
			if ( !hit.Hit ) continue;
			if ( !hit.GameObject.IsValid() ) continue;

			tr = hit;
			break;
		}

		if ( !tr.Hit ) return;

		//Kept for taking hold of it - see UpdateGrab.
		_hovered = tr;

		if ( tr.Component is not MeshComponent component ) return;

		using ( Gizmo.ObjectScope( tr.GameObject, tr.GameObject.WorldTransform ) )
		{
			Gizmo.Hitbox.DepthBias = 1;
			Gizmo.Hitbox.TrySetHovered( tr.Distance );

			//What a hovered mesh looks like is the mesh's own now - see MeshComponent.DrawGizmos, which lays the target a click
			//lands on and draws the box around it. This drew a second one over the top of that, pulsing, in this one mode.
		}
	}

	protected override void OnBoxSelect( Frustum frustum, Rect screenRect, bool isFinal )
	{
		var selection = new HashSet<GameObject>();
		var previous = new HashSet<GameObject>();

		foreach ( var go in Scene.GetAllObjects( true ) )
		{
			//GetAllObjects starts with the scene itself, which encloses everything
			if ( go == Scene ) continue;

			//What a click cannot reach, a drag over it must not pick up either.
			if ( !CanDragSelect( go ) )
			{
				previous.Add( go );
				continue;
			}

			//Its own bounds, not its subtree's - see GetDragBounds.
			var bounds = GetDragBounds( go );

			//Nothing of its own for the box to catch, and no handle either. The objects under it are
			//in this walk too and stand for themselves; taking the parent as well took all of them.
			if ( bounds.Size.AlmostEqual( 0.0f ) && !go.HasGizmoHandle )
			{
				previous.Add( go );
				continue;
			}

			//Partial, otherwise you'd have to fit a whole block in the box to select it
			if ( frustum.IsInside( bounds, true ) )
				selection.Add( go );
			else
				previous.Add( go );
		}

		ApplyDragSelection( selection, previous );
	}

	private void DrawBounds()
	{
		using ( Gizmo.Scope( "Bounds" ) )
		{
			var box = CalculateSelectionBounds();
			DimensionDisplay.DrawBounds( box );
		}
	}

	//Not while a press is on something selected: dragging it moves it - see UpdateGrab.
	public override bool HasBoxSelectionMode() => _grabFrom is null;

	public override void AlignDown( bool useLocalDown )
	{
		if ( useLocalDown )
			SceneEditorMenus.AlignToGroundLocal();
		else
			SceneEditorMenus.AlignToGround();
	}

	public override SurfaceSnap CreateSurfaceSnap( Rotation basis )
	{
		return new SurfaceSnap( Scene, _startPoints.Count > 0 ? _startPoints.Keys : _objects, basis );
	}

	public override void AlignToClosestNormal()
	{
		SceneEditorMenus.AlignToClosestNormal();
	}
}
