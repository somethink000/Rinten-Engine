using System.Text.Json.Serialization;

namespace Editor;

public class EditorTool : IDisposable
{
	[JsonIgnore]
	public EditorToolManager Manager { get; internal set; }

	/// <summary>Whether this frame is the viewport's input goes to - see <see cref="EditorToolManager.IsInputView"/>. A tool that holds a press or a drag across frames changes it only where this is true, and draws everywhere.</summary>
	[JsonIgnore]
	public bool IsInputView => Manager?.IsInputView ?? true;
	[JsonIgnore]
	public SelectionSystem Selection => Manager.CurrentSession.Selection;
	[JsonIgnore]
	public Scene Scene => Manager.CurrentSession.Scene;
	[JsonIgnore]
	public Widget SceneOverlay => SceneDock.Active;
	[JsonIgnore]
	public CameraComponent Camera { get; private set; }

	List<Widget> overlayWidgets = new();
	List<EditorTool> _tools = new();

	public IEnumerable<EditorTool> Tools => _tools;

	/// <summary>If true well recreate the sidebar when the selection changes. This is really useful if your tool's sidebar depends on the selection. But also it can be a waste of energy if your tool's sidebar is static.</summary>
	public bool RebuildSidebarOnSelectionChange { get; set; } = true;

	private EditorTool _currentTool;
	[JsonIgnore]
	public EditorTool CurrentTool
	{
		get => _currentTool;
		set
		{
			if ( _currentTool == value )
				return;

			_currentTool?.OnDisabled();
			_currentTool = value;
			_currentTool?.OnEnabled();

			EditorToolManager.SetSubTool( _currentTool?.GetType().Name );
		}
	}

	/// <summary>Create a scene trace against the current scene, using the current mouse cursor</summary>
	[JsonIgnore]
	public SceneTrace Trace => Scene.Trace.Ray( Gizmo.CurrentRay, Gizmo.RayDepth );

	//The tag the editor's own traces pass over, as the array WithoutTags takes - made once, not at every trace.
	static readonly string[] HiddenTag = { "hidden" };

	/// <summary>Trace against render meshes (not physics) at the cursor; front faces only</summary>
	[JsonIgnore]
	public SceneTrace MeshTrace => Trace.UseRenderMeshes( true, false )
										.WithoutTags( HiddenTag )
										.UsePhysicsWorld( false );


	//Return the selected component of type
	protected T GetSelectedComponent<T>() where T : Component
	{
		return Selection.OfType<GameObject>().Select( x => x.Components.Get<T>() ).FirstOrDefault();
	}

	/// <summary>if true then regular scene object selection will apply</summary>
	public bool AllowGameObjectSelection { get; set; } = true;

	/// <summary>allow context menu or not, some tools don't need it.</summary>
	public bool AllowContextMenu { get; set; } = true;

	internal void InitializeInternal( EditorToolManager manager )
	{
		Manager = manager;
		OnEnabled();

		CreateTools();
	}

	private void CreateTools()
	{
		_tools.Clear();

		var tools = GetSubtools();
		if ( tools == null )
			return;

		foreach ( var tool in tools )
		{
			if ( tool is null )
				continue;

			tool.Manager = Manager;
			_tools.Add( tool );
		}

		CurrentTool = _tools.FirstOrDefault();
	}

	internal void Frame( CameraComponent camera )
	{
		Camera = camera;

		try
		{
			if ( Manager.IsCurrentViewFocused )
			{
				if ( HasLassoSelectionMode() && (IsLassoSelecting || (Gizmo.IsAltPressed && (Gizmo.IsShiftPressed || Gizmo.IsCtrlPressed) && Gizmo.IsLeftMouseDown && Gizmo.Pressed.CursorDelta.Length > 0.1f)) )
				{
					UpdateLassoSelection();
				}
				else if ( HasBoxSelectionMode() )
				{
					UpdateBoxSelection();
				}
			}

			OnUpdate();
			CurrentTool?.Frame( camera );
		}
		catch ( System.Exception e )
		{
			Log.Warning( e, $"{this}.OnUpdate exception: {e.Message}" );
		}
	}

	public virtual void OnUpdate()
	{

	}

	public virtual void OnEnabled()
	{

	}

	public virtual void OnDisabled()
	{

	}

	public virtual void OnSelectionChanged()
	{

	}

	/// <summary>Allows tools to contribute custom items to the scene viewport context menu.</summary>
	public virtual void BuildSceneContextMenu( Menu menu, Ray ray, SceneTraceResult? trace )
	{
	}

	protected static void AddMenuOption( Menu m, string name, string icon, string shortcut, bool condition )
	{
		if ( !condition ) return;
		m.AddOption( name, icon, () => InvokeShortcut( shortcut ), shortcut );
	}

	protected static void AddMenuOption( Menu m, string name, string icon, Action action, string shortcut, bool condition )
	{
		if ( !condition ) return;
		m.AddOption( name, icon, action, shortcut );
	}

	internal static bool InvokeShortcut( string identifier )
	{
		foreach ( var shortcut in EditorShortcuts.Entries )
		{
			if ( shortcut.Identifier != identifier ) continue;
			if ( shortcut.Invoke( true ) ) return true;
		}

		return false;
	}

	/// <summary>Return true here to keep the tool active even if the component is no longer in the selection.</summary>
	public virtual bool ShouldKeepActive()
	{
		return false;
	}

	public virtual void Dispose()
	{
		OnDisabled();

		foreach ( var w in overlayWidgets )
		{
			if ( w.IsValid() ) w.Destroy();
		}

		overlayWidgets.Clear();

		foreach ( var tool in _tools )
		{
			tool?.Dispose();
		}

		_tools.Clear();
	}

	/// <summary>Return any tools that this tool wants to use</summary>
	public virtual IEnumerable<EditorTool> GetSubtools()
	{
		return default;
	}

	[Obsolete]
	protected void EditLog( string v, IEnumerable<object> targets )
	{
		SceneEditorSession.Active.Scene.EditLog( v, this );
	}

	//Duplicates the selected objects and selects the duplicated set
	protected void DuplicateSelection()
	{
		SceneEditorMenus.DuplicateInternal();
	}

	public void AddOverlay( Widget widget, TextFlag align = TextFlag.RightTop, Vector2 offset = default )
	{
		widget.Parent = SceneOverlay;

		overlayWidgets.Add( widget );

		widget.AdjustSize();
		widget.AlignToParent( align, offset );
		widget.Show();
	}

	//It shows nothing. A [Shortcut] is live while the thing that declares it is, so a tool whose keys should only work while it is picked up hangs them on a panel that only exists then.
	/// <summary>Create a panel that carries the shortcut keys for this tool (and its parents).</summary>
	public virtual Panel CreateShortcutsPanel()
	{
		return null;
	}

	/// <summary>Create a panel for this tool to be added next to the left toolbar.</summary>
	public virtual Panel CreateToolSidebarPanel()
	{
		return null;
	}

	//The last of the old path. A tool answers one of these two, never both, and the sidebar asks for the panel first - see ToolSidebarPanel . When the tools still on this one are markup, it goes.
	/// <summary>The same, for the tools whose settings are still widgets.</summary>
	public virtual Widget CreateToolSidebar()
	{
		return null;
	}

	/// <summary>Create a panel to be added to the viewport toolbar when this tool is active. This allows tools to add custom controls, options, or information to the main toolbar.</summary>
	public virtual Panel CreateToolbarPanel()
	{
		return null;
	}

	/// <summary>Create a panel for this tool to be added at the bottom of the tools</summary>
	public virtual Panel CreateToolFooterPanel()
	{
		return null;
	}

	/// <summary>The same, for a footer that is still a widget. See <see cref="CreateToolSidebar"/>.</summary>
	public virtual Widget CreateToolFooter()
	{
		return null;
	}

	/// <summary>Mode column for this tool, or null; default is one button per sub-tool, asked once per change</summary>
	public virtual ToolGroup CreateModeGroup()
	{
		var group = new ToolGroup( "Modes", ToolButtonSize.Mode );

		group.AddRange( SubToolCommand.ForSubToolsOf( this ) );

		return ToolGroup.OrNull( group );
	}

	/// <summary>Option column beside modes, or null; asked of the sub-tool first, then the tool</summary>
	public virtual ToolGroup CreateOptionGroup()
	{
		return null;
	}

	/// <summary>Action column (bevel, merge...) for the sub-tool, or null; same commands as its settings panel</summary>
	public virtual ToolGroup CreateActionGroup()
	{
		return null;
	}

	/// <summary>Get the current selection as a SerializedObject</summary>
	public SerializedObject GetSerializedSelection()
	{
		var o = Selection.ToArray();
		SerializedObject so;

		if ( o.Length == 0 )
		{
			so = o.GetSerialized();
		}
		else if ( o.Length == 1 )
		{
			so = o.GetValue( 0 )?.GetSerialized();
		}
		else
		{
			var mo = new MultiSerializedObject();

			for ( int i = 0; i < o.Length; i++ )
			{
				var val = o?.GetValue( i );
				if ( val is null ) continue;
				mo.Add( val.GetSerialized() );
			}

			mo.Rebuild();
			so = mo;
		}

		return so;
	}

	/// <summary>If true then this mode uses box selection</summary>
	public virtual bool HasBoxSelectionMode() => false;

	/// <summary>If true then this mode uses lasso selection (Alt+Shift+Drag to add, Alt+Ctrl+Drag to remove)</summary>
	public virtual bool HasLassoSelectionMode() => false;

	Ray _ray1;
	Ray _ray2;
	List<Vector2> _lassoPoints = new();
	Dictionary<GameObject, BBox> _dragBounds = new();

	protected bool IsBoxSelecting { get; private set; }
	protected bool IsLassoSelecting { get; private set; }

	/// <summary>A box or lasso is out: the selection changes every frame until it is let go, and what is costly to build from it waits.</summary>
	public bool IsDragSelecting => IsBoxSelecting || IsLassoSelecting;

	private void UpdateBoxSelection()
	{
		if ( !Gizmo.IsLeftMouseDown && !Gizmo.WasLeftMouseReleased )
		{
			IsBoxSelecting = false;
			return;
		}

		//Something else holds the press (gizmo handle, vertex), so this is not a box select
		if ( Gizmo.Pressed.Any )
		{
			IsBoxSelecting = false;
			return;
		}

		if ( Gizmo.WasLeftMousePressed )
		{
			_ray1 = Gizmo.CurrentRay;
			_dragBounds.Clear();
		}

		if ( Gizmo.IsLeftMouseDown )
		{
			_ray2 = Gizmo.CurrentRay;
		}

		//Use direct screen-space positions from Gizmo inputs instead of projecting rays 100 units out
		Vector2 c1 = Gizmo.Pressed.CursorPosition;
		Vector2 c2 = Gizmo.CursorPosition;

		if ( Vector2.Distance( c1, c2 ) < 5 )
		{
			IsBoxSelecting = false;

			if ( !Gizmo.IsLeftMouseDown )
			{
				_ray1 = _ray2 = default;
			}

			return;
		}

		//Clear the bounds cache where the box select actually begins, not on press
		if ( !IsBoxSelecting ) _dragBounds.Clear();

		IsBoxSelecting = true;

		Rect rect = Rect.FromPoints( c1, c2 );
		var frustum = Gizmo.Camera.GetFrustum( rect );

		OnBoxSelect( frustum, rect, Gizmo.WasLeftMouseReleased );

		//Crystalize the box select
		if ( Gizmo.WasLeftMouseReleased )
		{
			_ray1 = _ray2 = default;
			return;
		}

		//Paint the selection rectangle
		{
			Gizmo.Draw.ScreenRect( rect, Theme.Primary.WithAlpha( 0.1f ), new Vector4( 1.0f ), Theme.Primary, new Vector4( 1.0f ) );
		}
	}

	private void UpdateLassoSelection()
	{
		if ( Gizmo.IsLeftMouseDown && !IsLassoSelecting )
		{
			_lassoPoints.Clear();
			IsLassoSelecting = true;
		}

		if ( Gizmo.IsLeftMouseDown && IsLassoSelecting )
		{
			var currentPoint = Gizmo.Camera.ToScreen( Gizmo.CurrentRay.Project( 100 ) );

			if ( _lassoPoints.Count == 0 || Vector2.Distance( _lassoPoints[^1], currentPoint ) > 5 )
			{
				_lassoPoints.Add( currentPoint );
			}
		}

		if ( Gizmo.WasLeftMouseReleased && IsLassoSelecting )
		{
			if ( _lassoPoints.Count > 2 )
			{
				OnLassoSelect( _lassoPoints, true );
			}

			_lassoPoints.Clear();
			IsLassoSelecting = false;
			return;
		}

		if ( IsLassoSelecting && _lassoPoints.Count > 1 )
		{
			Color lassoColor;
			if ( Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Ctrl ) )
			{
				lassoColor = Theme.Red;
			}
			else if ( Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Shift ) )
			{
				lassoColor = Theme.Green;
			}
			else
			{
				lassoColor = Theme.Primary;
			}

			DrawLasso( _lassoPoints, true, lassoColor );
		}
	}

	private void DrawLasso( List<Vector2> points, bool isClosed, Color color )
	{
		if ( points.Count < 2 )
			return;

		using ( Gizmo.Scope( "Lasso" ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.Color = color;
			Gizmo.Draw.LineThickness = 2;

			var worldPoints = new List<Vector3>( points.Count );
			var planeDistance = 100f;

			foreach ( var screenPoint in points )
			{
				var viewportX = screenPoint.x / Gizmo.Camera.Rect.Width;
				var viewportY = screenPoint.y / Gizmo.Camera.Rect.Height;

				var ray = Gizmo.Camera.GetRay( new Vector2( viewportX, viewportY ) );
				var worldPos = ray.Project( planeDistance );
				worldPoints.Add( worldPos );
			}

			//Maybe this should be it's own gizmo draw function?
			for ( int i = 0; i < worldPoints.Count - 1; i++ )
			{
				Gizmo.Draw.Line( worldPoints[i], worldPoints[i + 1] );
			}

			if ( isClosed && worldPoints.Count > 2 )
			{
				Gizmo.Draw.Line( worldPoints[^1], worldPoints[0] );
			}
		}
	}

	//Called when the lasso selection changed or completed
	protected virtual void OnLassoSelect( List<Vector2> lassoPoints, bool isFinal )
	{

	}

	//Called when the box selection changed
	protected virtual void OnBoxSelect( Frustum frustum, Rect screenRect, bool isFinal )
	{

	}

	//Bounds of an object's own components (not children), cached for the duration of a drag
	protected BBox GetDragBounds( GameObject go )
	{
		if ( _dragBounds.TryGetValue( go, out var bounds ) )
			return bounds;

		return _dragBounds[go] = OwnBounds( go );
	}

	//The bounds of an object's own enabled components, ignoring its children. An object with nothing of its
	//own to draw comes back as a point at its position, which is what a drag has to cross to pick it up.
	private static BBox OwnBounds( GameObject go )
	{
		BBox? result = null;

		foreach ( var has in go.Components.GetAll<Component.IHasBounds>( FindMode.EnabledInSelf ) )
		{
			var bounds = has is Component component
				? has.LocalBounds.Transform( component.WorldTransform )
				: has.LocalBounds;

			result = result.HasValue ? result.Value.AddBBox( bounds ) : bounds;
		}

		return result ?? BBox.FromPositionAndSize( go.WorldPosition );
	}

	//Box-selectable only if neither it nor any parent is hidden, matching click selection
	protected static bool CanDragSelect( GameObject go )
	{
		for ( var at = go; at.IsValid() && at is not Rinten.Scene; at = at.Parent )
		{
			if ( !at.Active ) return false;
			if ( at.Flags.Contains( GameObjectFlags.Hidden ) ) return false;
			if ( at.Tags.Has( "hidden" ) ) return false;
		}

		return true;
	}

	//Apply the result of a drag selection. Ctrl deselects whatever the drag covered,
	//shift keeps what was already selected, otherwise the drag replaces the selection.
	protected void ApplyDragSelection<T>( IEnumerable<T> inside, IEnumerable<T> outside )
	{
		if ( Gizmo.IsCtrlPressed )
		{
			foreach ( var element in inside )
			{
				if ( Selection.Contains( element ) )
					Selection.Remove( element );
			}

			return;
		}

		foreach ( var element in inside )
		{
			if ( !Selection.Contains( element ) )
				Selection.Add( element );
		}

		if ( Gizmo.IsShiftPressed ) return;

		foreach ( var element in outside )
		{
			if ( Selection.Contains( element ) )
				Selection.Remove( element );
		}
	}

	//Helper method to check if a screen point is inside the lasso polygon Uses the ray casting algorithm
	protected static bool IsPointInLasso( Vector2 point, List<Vector2> lassoPoints )
	{
		if ( lassoPoints.Count < 3 )
			return false;

		int intersections = 0;
		for ( int i = 0; i < lassoPoints.Count; i++ )
		{
			var p1 = lassoPoints[i];
			var p2 = lassoPoints[(i + 1) % lassoPoints.Count];

			if ( (p1.y > point.y) != (p2.y > point.y) )
			{
				float intersectX = (p2.x - p1.x) * (point.y - p1.y) / (p2.y - p1.y) + p1.x;
				if ( point.x < intersectX )
					intersections++;
			}
		}

		return (intersections % 2) == 1;
	}
}

