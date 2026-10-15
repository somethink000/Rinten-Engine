using Rinten.UI;

namespace Editor.MdlEditor;

/// <summary>Base for 3D model previews: lit scene, treadmill ground, orbit/zoom/pan camera controls</summary>
[StyleSheet( "PreviewScenePanel.cs.scss" )]
public class PreviewScenePanel : EditorPanel
{
	//Panel already answers "which scene does this panel belong to"; this is the scene the panel is looking at, which is a different question.
	protected new Scene Scene { get; private set; }
	protected CameraComponent Camera { get; private set; }
	private protected TreadmillGround Ground { get; private set; }
	protected GameObject ModelObject { get; private set; }
	protected SkinnedModelRenderer Renderer { get; private set; }
	protected Model Model { get; private set; }
	private readonly PreviewThrobber _throbber;

	//Scroll the ground against root motion. Turn off when the model shouldn't treadmill, e.g. ragdolled.
	protected bool TreadmillEnabled { get; set; } = true;

	//When set, the orbit camera sticks to this bone instead of the framed center.
	protected string FocusBone { get; set; }

	private int _stepFrames;

	//Advance the scene a single frame, ignoring time scale.
	protected void StepFrame() => _stepFrames++;

	private readonly RenderView _renderView;
	private readonly Panel _viewportFrame;

	internal Panel ViewportOverlayParent => _viewportFrame;

	//The viewport in device pixels - what a gizmo drawn over the picture is measured in, and where the throbber sits.
	internal Vector2 ViewportPixelSize => _renderView.IsValid() ? _renderView.Box.Rect.Size : Vector2.Zero;

	//Height of the transport at the viewport bottom, in stylesheet units, so overlays sit above it
	internal float ViewportBottomInset
		=> _transportShown && TransportHost.IsValid()
			? TransportHost.Box.Rect.Height * TransportHost.ScaleFromScreen
			: 0.0f;

	private protected Vector2 _angles = new( 135f, 20f );
	private protected Vector3 _origin;
	private protected Vector3 _center;
	private protected float _distance = 5.08f;
	private bool _orbit;
	private bool _zoom;
	private bool _pan;
	private float _dragDistance;

	//True while a mouse button is dragging the camera (orbit, zoom or pan).
	protected bool CameraDragging => _orbit || _zoom || _pan;

	//Last frame's gizmo hover or drag, for the press that happens between frames.
	protected bool GizmoWantsMouse { get; private set; }

	//True while a handle under the pointer must win the click over a physics body behind it
	protected virtual bool HandleBeatsGrab => false;

	//True while the cursor is over the viewport.
	protected bool ViewIsHovered => _renderView.IsUnderMouse;

	private PhysicsBody _mouseBody;
	private Rinten.Physics.ControlJoint _mouseJoint;
	private float _grabDistance;

	//Toolbar strip above the viewport for preview controls.
	protected Panel Toolbar { get; }

	//The engine's own grid (and its light/camera icons) - the scene view turns this on the
	//same way. Nothing routed through Gizmo.Draw.Grid draws here; see the constructor for why.
	protected bool ShowGrid
	{
		get => _renderView.ShowEditorOverlays;
		set => _renderView.ShowEditorOverlays = value;
	}

	//Strip along the bottom of the 3D view, for playback.
	protected Panel TransportHost { get; }

	private bool _transportShown;

	//Whether the transport strip is up. Explicit rather than measured: a panel is laid out by the sheet and
	//nothing writes a height back onto it, so "is it there" cannot be read off the box the frame before it exists.
	protected bool ShowTransport
	{
		get => _transportShown;
		set
		{
			_transportShown = value;
			TransportHost.SetClass( "hidden", !value );
		}
	}

	public PreviewScenePanel( string sceneName )
	{
		AddClass( "preview-scene" );

		//A real game scene - components, physics and ragdolls behave exactly like in game
		Scene = new Scene();
		Scene.Name = sceneName;

		using ( Scene.Push() )
		{
			var camGo = new GameObject( true, "camera" );
			Camera = camGo.AddComponent<CameraComponent>();
			Camera.BackgroundColor = Theme.ControlBackground;
			Camera.FieldOfView = 60f;
			Camera.ZFar = 15000f;
			//Worked out again from how far the camera is - see UpdateCamera. A tenth of a metre cut the model open as soon as it came close.
			Camera.ZNear = 0.001f;

			PreviewLighting.HideSky( Camera );
			PreviewLighting.Light( Scene, new Angles( _angles.y, -_angles.x, 0 ) );
		}

		//Treadmills against root motion so the model stays put. Drawn as a gizmo rather than put in the scene, so it needs nothing from the scope above.
		Ground = new TreadmillGround();

		Toolbar = AddChild<Panel>( "toolbar" );

		_viewportFrame = AddChild<Panel>( "viewport" );

		_renderView = _viewportFrame.AddChild( new RenderView( this ) { Scene = Scene } );
		_renderView.AddClass( "view" );

		//The engine's grid is drawn by the renderer, gated on this - the scene view turns it on
		//the same way. See TreadmillGround for why the preview does not draw a grid of its own.
		_renderView.ShowEditorOverlays = true;

		//What the handles snap to, how big they are drawn, and what that grid is spaced at - all of it comes off the instance's settings,
		//and a fresh instance's are the defaults a level is laid out with. See PreviewGizmoSettings for what that did to a model.
		_renderView.GizmoInstance.Settings = PreviewGizmoSettings.Current;

		_renderView.OnPreFrame += PreFrame;
		_throbber = new PreviewThrobber( Scene.SceneWorld, _renderView );

		TransportHost = _viewportFrame.AddChild<Panel>( "transport hidden" );
	}

	protected void SetCompiling( bool compiling ) => _throbber.Enabled = compiling;

	//The viewport owns camera input so clicks on surrounding UI never start an orbit.
	private sealed class RenderView : SceneRenderingPanel
	{
		private readonly PreviewScenePanel _owner;

		public RenderView( PreviewScenePanel owner )
		{
			_owner = owner;
		}

		protected override void OnMouseDown( MousePanelEvent e )
		{
			base.OnMouseDown( e );

			_owner.ViewMouseDown( e );
		}

		protected override void OnMouseUp( MousePanelEvent e )
		{
			base.OnMouseUp( e );

			_owner.ViewMouseUp( e );
		}

		/// <summary>The wheel dollies the camera. Deliberately not the base behaviour: that hands the notch to the game, and there is no game in a preview.</summary>
		public override void OnMouseWheel( Vector2 value ) => _owner.ViewMouseWheel( value );

	}

	//Swap the preview model and frame the camera around it. Returns false if the model is unchanged and the renderer is still live.
	protected bool SetModel( Model model, bool force = false )
	{
		if ( model is not null && !model.IsValid() )
			model = Model.Load( model.Name );

		if ( !model.IsValid() )
			model = null;

		var rendererValid = Renderer.IsValid() && Renderer.SceneModel.IsValid();
		if ( !force && model == Model && rendererValid == (model is not null) )
			return false;

		var sameIdentity = Model is not null && model is not null
			&& string.Equals( Model.Name, model.Name, StringComparison.OrdinalIgnoreCase );

		Model = model;

		using ( Scene.Push() )
		{
			if ( ModelObject.IsValid() )
			{
				ModelObject.Destroy();
				ModelObject = null;
				Renderer = null;
			}

			if ( model is null )
				return true;

			ModelObject = new GameObject( true, "preview model" );
			Renderer = ModelObject.AddComponent<SkinnedModelRenderer>();
			Renderer.Model = model;
			Renderer.PlayAnimationsInEditorScene = true;

			//Nothing here plays the model's legacy animgraph - until an AG2 graph is bound the model stays in its reference pose
			Renderer.UseAnimGraph = false;

			if ( !sameIdentity )
				FrameCamera();
		}

		return true;
	}

	//Convert a world position to pixel coordinates in the preview viewport.
	protected Vector2 ToViewportPixels( Vector3 worldPosition, out bool isBehind )
	{
		return Camera.PointToScreenNormal( worldPosition, out isBehind ) * ViewportPixelSize;
	}

	//Frame the camera around the model's bounds.
	protected void FrameCamera()
	{
		if ( Model is null )
			return;

		_center = Model.Bounds.Center;
		_distance = MathX.SphereCameraDistance( Model.Bounds.Size.Length * 0.5f, Camera.FieldOfView ) * 1.2f;
		_origin = Vector3.Zero;

		UpdateCamera();
	}

	private void PreFrame()
	{
		if ( !Scene.IsValid() || Camera is null )
			return;

		GizmoWantsMouse = _renderView.GizmoHasPointer;
		var hasMouse = ( ViewIsHovered && !CameraDragging ) || Gizmo.Pressed.Any;
		_renderView.GizmoInstance.Input.IsHovered = hasMouse;
		_renderView.UpdateGizmoInputs( hasMouse );

		using ( Scene.Push() )
		{
			OnScenePrepareFrame();

			if ( _stepFrames > 0 )
			{
				var timeScale = Scene.TimeScale;
				Scene.TimeScale = 1f;
				Scene.GameTick( 1f / 60f );
				Scene.TimeScale = timeScale;
				_stepFrames--;
			}
			else
			{
				Scene.GameTick( RealTime.Delta );
			}

			if ( TreadmillEnabled )
				UpdateTreadmill();

			UpdateGrab();
			UpdateCamera();

			OnScenePreFrame();
		}
	}

	//Push preview state before the scene ticks, so the graph reads this frame's parameters.
	protected virtual void OnScenePrepareFrame() { }

	//Per-frame hook, called with the preview scene pushed.
	protected virtual void OnScenePreFrame() { }

	//Move the ground under the model. Integrates the root motion the model reports each
	//frame, which previews that scrub through a clip replace with an absolute placement.
	protected virtual void UpdateTreadmill() => Ground.Update( Renderer );

	protected virtual void UpdateCamera()
	{
		//Per-frame cursor delta, not offset from press: pointer warping is a no-op on Wayland
		var delta = CameraDragging ? Application.CursorDelta : Vector2.Zero;

		//Wrapped round the edge of the picture so a long drag does not run out of screen, which is what the scene view
		//does with the same call. The frame it wraps on is a jump rather than a movement, so it is not turned into one.
		if ( CameraDragging && _renderView.IsValid() && SceneEditorExtensions.LockCursorToCanvas( _renderView ) )
			delta = Vector2.Zero;

		if ( _orbit )
		{
			_angles.x = (_angles.x + delta.x * OrbitPerPixel).NormalizeDegrees();
			_angles.y = (_angles.y + delta.y * OrbitPerPixel).Clamp( -89, 89 );
		}
		else if ( _zoom )
		{
			Zoom( delta.y );
		}
		else if ( _pan )
		{
			//Pan speed scales with camera distance so it feels the same close up and far away
			var perPixel = _distance * PanPerUnit;

			var pan = Camera.WorldRotation.Right * delta.x * perPixel
				+ Camera.WorldRotation.Down * delta.y * perPixel;

			//Inverted to match the scene view's panning, respecting the CameraInvertPan preference
			if ( !EditorPreferences.CameraInvertPan ) pan = -pan;

			_origin += pan;
		}

		if ( CameraDragging )
		{
			_dragDistance += delta.Length;
			SetViewCursor( CursorShape.Blank );
		}
		else
		{
			SetViewCursor( _mouseJoint.IsValid() ? CursorShape.ClosedHand
				: GizmoWantsMouse ? CursorShape.Finger
				: IsHoveringDynamicBody() ? CursorShape.OpenHand : CursorShape.None );
		}

		Camera.WorldRotation = new Angles( _angles.y, -_angles.x, 0 );

		var center = _center;
		if ( FocusBone is not null && Renderer.IsValid() && Renderer.TryGetBoneTransform( FocusBone, out var boneTx ) )
			center = boneTx.Position;

		Camera.WorldPosition = center + _origin + Camera.WorldRotation.Backward * _distance;

		//The near plane with the camera: close to the model it is a fraction of a millimetre, so nothing
		//is cut open, and from across the room it is far enough out that the depth keeps its precision.
		var near = Math.Clamp( _distance * 0.005f, 0.0005f, 0.1f );

		if ( Camera.ZNear != near )
			Camera.ZNear = near;
	}

	private CursorShape _cursor = CursorShape.None;

	//The pointer's shape while it is over the picture. A panel has no cursor of its own - this ends up on the widget the whole window is drawn in -
	//so it is written when it changes and never per frame, or the preview would be deciding the shape of the cursor over every other dock as well.
	private void SetViewCursor( CursorShape shape )
	{
		if ( _cursor == shape )
			return;

		_cursor = shape;
		_renderView.Cursor = shape;
	}

	private void ViewMouseDown( MousePanelEvent e )
	{
		if ( e.MouseButton == MouseButtons.Left )
		{
			//A handle that is being offered takes the click before anything else looks at it - see HandleBeatsGrab.
			if ( _renderView.GizmoHasPointer && HandleBeatsGrab )
			{
				e.StopPropagation();
				return;
			}

			//Ragdoll bodies win over gizmo hits: joint EditorHandle sprites sit at body origins
			if ( TryStartGrab() )
			{
				//handled below
			}
			else if ( _renderView.GizmoHasPointer )
			{
				e.StopPropagation();
				return;
			}
			else
			{
				_orbit = true;
			}
		}
		else if ( e.MouseButton == MouseButtons.Right ) _zoom = true;
		else if ( e.MouseButton == MouseButtons.Middle ) _pan = true;
		else return;

		_dragDistance = 0f;
		e.StopPropagation();
	}

	private void ViewMouseUp( MousePanelEvent e ) => EndDrag( e.MouseButton );

	//Finish whatever that button was doing. Split out because the button can also stop being held without the panel being told - see Tick.
	private void EndDrag( MouseButtons button )
	{
		if ( button == MouseButtons.Left )
		{
			//A press that went nowhere is a click on the picture, not a turn of the camera - the same test the right button's menu uses.
			var clicked = _orbit && _dragDistance < 5f;

			EndGrab();
			_orbit = false;

			if ( clicked )
				OnViewLeftClick();
		}
		else if ( button == MouseButtons.Right )
		{
			//A panel hears a release wherever the pointer happens to be, not only where the press
			//landed - so a right click is only this view's if this view is the one it started on.
			if ( !_zoom )
				return;

			_zoom = false;

			//A right click without dragging is a context click, not a zoom
			if ( _dragDistance < 5f )
				OnViewRightClick();
		}
		else if ( button == MouseButtons.Middle ) _pan = false;
	}

	public override void Tick()
	{
		base.Tick();

		//There is no implicit grab on a panel, and a camera drag pins the pointer where it started rather than letting it travel - so the
		//release is read from the buttons themselves as well, or a drag that ended anywhere but squarely on the picture would run for ever.
		if ( !CameraDragging && _mouseBody is null )
			return;

		var held = Application.MouseButtons;

		if ( (_orbit || _mouseBody is not null) && !held.HasFlag( MouseButtons.Left ) ) EndDrag( MouseButtons.Left );
		if ( _zoom && !held.HasFlag( MouseButtons.Right ) ) EndDrag( MouseButtons.Right );
		if ( _pan && !held.HasFlag( MouseButtons.Middle ) ) EndDrag( MouseButtons.Middle );
	}

	//Called when the viewport is right clicked without dragging.
	protected virtual void OnViewRightClick() { }

	//Called when the viewport is left clicked without dragging, on nothing a gizmo or a grab took.
	protected virtual void OnViewLeftClick() { }

	//A key typed while the picture has the keyboard. Fires on the way down and on the way up, so anything that acts once asks e.Pressed first.
	protected virtual void ViewButtonTyped( ButtonEvent e ) { }

	/// <summary>The preview's own shortcuts, offered wherever in it the keyboard is.</summary>
	public override void OnButtonTyped( ButtonEvent e )
	{
		ViewButtonTyped( e );

		if ( e.StopPropagation ) return;

		base.OnButtonTyped( e );
	}

	private void ViewMouseWheel( Vector2 value )
	{
		//A panel hears -1 for the notch a widget heard as +120, so the twelve notches-worth a widget zoomed by is this, sign and all: 0.3048m a notch.
		Zoom( value.y * 12f );
	}

	private void Zoom( float delta ) => _distance = (_distance + delta * PerNotch).Clamp( 0.254f, 2000f );

	//How far a notch of the wheel moves the camera, in metres.
	private const float PerNotch = 0.0254f;

	//Degrees turned per pixel of drag. Twice the scene view's, and left that way on purpose: this drag is read off the
	//unscaled cursor and turns a model an arm's length away, where the scene view's is a person walking around a level.
	private const float OrbitPerPixel = 0.2f;

	//Pan metres per pixel per metre of distance; tuned for the default 5.08 view distance
	private const float PanPerUnit = 0.001f;

	private Ray CursorRay => _renderView.CursorRay;

	private bool IsHoveringDynamicBody()
	{
		if ( !_renderView.IsUnderMouse )
			return false;

		var tr = Scene.Trace.Ray( CursorRay, 254f ).Run();
		return tr.Hit && tr.Body.IsValid() && tr.Body.BodyType == PhysicsBodyType.Dynamic;
	}

	private bool TryStartGrab()
	{
		var tr = Scene.Trace.Ray( CursorRay, 254f ).Run();
		if ( !tr.Hit || !tr.Body.IsValid() || tr.Body.BodyType != PhysicsBodyType.Dynamic )
			return false;

		_grabDistance = tr.HitPosition.Dot( Camera.WorldRotation.Forward );

		_mouseBody = new PhysicsBody( Scene.PhysicsWorld ) { BodyType = PhysicsBodyType.Keyframed };
		_mouseBody.Position = tr.HitPosition;

		//Motor joint pulling the grab point to the mouse body
		_mouseJoint = Rinten.Physics.PhysicsJoint.CreateControl(
			new Rinten.Physics.PhysicsPoint( _mouseBody ),
			Rinten.Physics.PhysicsPoint.World( tr.Body, tr.HitPosition ) );

		var maxForce = 100.0f * tr.Body.Mass * Scene.PhysicsWorld.Gravity.Length;
		_mouseJoint.LinearSpring = new Rinten.Physics.PhysicsSpring( 15, 1, maxForce );
		_mouseJoint.AngularSpring = new Rinten.Physics.PhysicsSpring( 0, 0, 0 );

		return true;
	}

	private void UpdateGrab()
	{
		if ( _mouseBody is null )
			return;

		//Grabbed body got destroyed under us - ragdoll toggled off, model swapped
		if ( !_mouseJoint.IsValid() )
		{
			EndGrab();
			return;
		}

		//Drag along the camera-facing plane at the grab distance
		var plane = new Plane( Camera.WorldRotation.Forward, _grabDistance );
		if ( plane.TryTrace( CursorRay, out var hitPoint, true ) )
			_mouseBody.Position = hitPoint;
	}

	private void EndGrab()
	{
		if ( _mouseJoint.IsValid() )
			_mouseJoint.Remove();

		if ( _mouseBody.IsValid() )
			_mouseBody.Remove();

		_mouseJoint = null;
		_mouseBody = null;
	}

	private int _attireHash;

	//Drop graph bone-merges so the Model tab only shows this model.
	protected void ClearPreviewAttachments()
	{
		_attireHash = 0;
		if ( !ModelObject.IsValid() )
			return;

		using ( Scene.Push() )
		{
			foreach ( var child in ModelObject.Children.Where( c => c.Name == "bonemerge" ).ToArray() )
				child.Destroy();
		}
	}

	//Apply the graph's saved preview settings - body groups, skin and bone merged models.
	protected void ApplyPreviewSettings( Data graph )
	{
		if ( graph is null || !Renderer.IsValid() )
			return;

		Renderer.BodyGroups = graph.PreviewBodyGroups;
		Renderer.MaterialGroup = graph.PreviewMaterialGroup;

		var hash = new HashCode();
		hash.Add( Renderer );

		foreach ( var a in graph.PreviewAttachments )
		{
			hash.Add( a.Model );
			hash.Add( a.BodyGroups );
			hash.Add( a.MaterialGroup );
		}

		var attireHash = hash.ToHashCode();
		if ( attireHash == _attireHash )
			return;

		_attireHash = attireHash;

		using ( Scene.Push() )
		{
			foreach ( var child in ModelObject.Children.Where( c => c.Name == "bonemerge" ).ToArray() )
				child.Destroy();

			foreach ( var a in graph.PreviewAttachments )
			{
				if ( a.Model is null )
					continue;

				var go = new GameObject( true, "bonemerge" ) { Parent = ModelObject };
				var renderer = go.AddComponent<SkinnedModelRenderer>();
				renderer.Model = a.Model;
				renderer.BodyGroups = a.BodyGroups;
				renderer.MaterialGroup = a.MaterialGroup;
				renderer.BoneMergeTarget = Renderer;
				renderer.UseAnimGraph = false;
			}
		}
	}

	protected void DestroyScene()
	{
		Scene?.Destroy();
		Scene = null;
	}

	public override void OnDeleted()
	{
		//The cursor a panel sets is the whole window's, so it has to be handed back before the panel goes:
		//a preview deleted mid-drag was leaving the pointer hidden over everything else in the window.
		SetViewCursor( CursorShape.None );

		base.OnDeleted();

		DestroyScene();
	}
}
