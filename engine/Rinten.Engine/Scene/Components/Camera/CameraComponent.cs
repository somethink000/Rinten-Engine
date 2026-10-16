
using Rinten.Rendering;
using Rinten.UI;
using System.Drawing;
using System.Text.Json.Serialization;

namespace Rinten;

/// <summary>Every scene should have at least one Camera.</summary>
[Expose]
[Title( "Camera" )]
[Category( "Rendering" )]
[Icon( "videocam" )]
[EditorHandle( "materials/gizmo/camera.png" )]
public sealed partial class CameraComponent : Component, Component.ExecuteInEditor, Component.ISceneStage
{
	SceneCamera sceneCamera;

	/// <summary>The clear flags for this camera.</summary>
	[Property]
	public ClearFlags ClearFlags { get; set; } = ClearFlags.Color | ClearFlags.Stencil | ClearFlags.Depth;

	/// <summary>The background color of this camera's view if there's no 2D Sky in the scene.</summary>
	[Property]
	public Color BackgroundColor { get; set; } = "#557685";

	bool _isMainCamera = true;
	int _priority = 1;

	/// <summary>Returns true if this is the main game camera.</summary>
	[Property]
	public bool IsMainCamera
	{
		get => _isMainCamera;
		set
		{
			if ( _isMainCamera == value )
				return;

			_isMainCamera = value;
			Scene.UpdateMainCamera();
		}
	}

	/// <summary>The axis to use for the field of view.</summary>
	[Property, Title( "FOV Axis" ), HideIf( nameof( Orthographic ), true )]
	public Axis FovAxis { get; set; } = Axis.Horizontal;

	/// <summary>The field of view of this camera.</summary>
	[Property, Range( 1, 179 ), HideIf( nameof( Orthographic ), true )]
	public float FieldOfView { get; set; } = 60;

	/// <summary>Near clip plane distance in metres. Below about 3cm expect z-fighting.</summary>
	[Property, Range( 0.01f, 10 ), Step( 0.01f )]
	public float ZNear { get; set; } = 0.1f;

	/// <summary>Far clip plane distance in metres. Shorter is better; raise ZNear if you push it out.</summary>
	[Property, Range( 0.1f, 2500 ), Step( 1 )]
	public float ZFar { get; set; } = 250;

	/// <summary>The priority of this camera. Dictates which camera gets rendered on top of another. Higher means it'll be rendered on top.</summary>
	[Property, Range( 1, 16 )]
	public int Priority
	{
		get => _priority;
		set
		{
			if ( _priority == value ) return;

			_priority = value;
			Scene.UpdateMainCamera();
		}
	}

	/// <summary>Whether or not to use orthographic projection instead of perspective.</summary>
	[Property]
	public bool Orthographic { get; set; }

	//Ten metres - a room and the corridor outside it. Thirty was the straight conversion of what this was in the units the engine used to measure in, which is three storeys of building and not a view anybody starts from.
	/// <summary>The orthographic size for this camera while <see cref="Orthographic"/> is set to true. How many metres of the world fit top to bottom of the view.</summary>
	[Property]
	public float OrthographicHeight { get; set; } = 10.0f;

	/// <summary>Skip lit meshes, sky and post; gizmos and lines only.</summary>
	[Hide, JsonIgnore]
	public bool OverlayOnly { get; set; }

	/// <summary>The HMD eye that this camera is targeting. Use <see cref="StereoTargetEye.None"/> for the user's monitor (i.e. the companion window).</summary>
	[Property]
	public StereoTargetEye TargetEye { get; set; } = StereoTargetEye.None;

	/// <summary>A list of tags that will be checked to include specific game objects when rendering this camera. If none are set, it will include everything.</summary>
	[Property]
	public TagSet RenderTags { get; set; } = new();

	/// <summary>A list of tags that will be checked to exclude specific game objects when rendering this camera.</summary>
	[Property]
	public TagSet RenderExcludeTags { get; set; } = new();

	/// <summary>The size of the camera represented on the screen. Normalized between 0 and 1.</summary>
	[Property, Range( 0, 1, slider: false )]
	public Vector4 Viewport { get; set; } = new( 0.0f, 0.0f, 1.0f, 1.0f ); //todo this should use a rect

	/// <summary>If specified, this camera will render to this RenderTexture instead of the screen. This can then be used in other stuff like materials.</summary>
	[Property]
	public RenderTextureAsset RenderTexture;

	//Since we are rendering to a texture, better to generate mipmaps for it.
	private CommandList _renderTextureMipGenCommandList
	{
		get
		{
			var cmd = new CommandList( "Generate MipMaps For RenderTexture" );

			if ( RenderTexture != null && RenderTexture.Texture != null && RenderTexture.Texture.Mips > 1 )
				cmd.GenerateMipMaps( RenderTexture.Texture );

			return cmd;
		}
	}

	private Texture _renderTarget;

	/// <summary>The texture to draw this camera to. Requires <see cref="Texture.CreateRenderTarget()"/></summary>
	[JsonIgnore, Hide]
	public Texture RenderTarget
	{
		get => RenderTexture?.Texture ?? _renderTarget;
		set
		{
			if ( _renderTarget == value )
				return;

			if ( value is not null )
			{
				if ( !( ( value.Desc.m_nFlags & NativeEngine.RuntimeTextureSpecificationFlags.TSPEC_RENDER_TARGET ) == NativeEngine.RuntimeTextureSpecificationFlags.TSPEC_RENDER_TARGET ) )
					throw new Exception( $"{nameof( RenderTarget )} texture needs to be a render target" );
			}

			_renderTarget = value;
			Scene?.UpdateMainCamera();
		}
	}

	/// <summary>Render this camera using a different render mode</summary>
	[JsonIgnore, Hide]
	public SceneCameraDebugMode DebugMode { get; set; }

	/// <summary>Render this camera using a wireframe view.</summary>
	[JsonIgnore, Hide]
	public bool WireframeMode { get; set; }

	//Accessor for getting this Camera Component's SceneCamera
	internal SceneCamera SceneCamera
	{
		get => sceneCamera;
	}

	public override void Reset()
	{
		base.Reset();

		RenderTags = new();
		RenderExcludeTags = new();
		Viewport = new( 0.0f, 0.0f, 1.0f, 1.0f );
		ClearFlags = ClearFlags.Color | ClearFlags.Stencil | ClearFlags.Depth;
	}

	private void EnsureSceneCameraCreated()
	{
		if ( sceneCamera is not null ) return;

		//scene should be scoped in!
		Assert.NotNull( Scene );
		if ( Game.IsPlaying )
		{
			//Only assert active scene in play mode, since we want to create cameras anywhere in the editor
			Assert.AreEqual( Game.ActiveScene, Scene, "Camera scene is not active scene" );
		}

		//Use the GameObject name so we can identify the camera when debugging
		sceneCamera = new( GameObject.Name );

		sceneCamera.OnRenderStageHook = ExecuteCommandLists;
	}

	protected override void OnAwake()
	{
		EnsureSceneCameraCreated();
		Scene.Cameras.Add( this );
	}

	protected override void OnDestroy()
	{
		Scene.Cameras.Remove( this );
		sceneCamera?.Dispose();
		sceneCamera = null;
	}

	protected override void DrawGizmos()
	{
		if ( sceneCamera is null )
			return;

		using var scope = Gizmo.Scope( gizmoPath ??= $"{GetHashCode()}" );

		Gizmo.Transform = global::Transform.Zero;

		UpdateSceneCameraTransform( sceneCamera );

		var frustum = sceneCamera.GetFrustum( new Rect( 0f, 0f, 1920f, 1080f ), new Vector2( 1920, 1080 ) );

		Gizmo.Draw.Color = Color.White.WithAlpha( 0.4f );
		Gizmo.Draw.LineFrustum( frustum );
	}

	//The name this component's gizmo scope goes by, made once - it was a new string every frame.
	string gizmoPath;

	/// <summary>This frame's composed view (base plus modifiers). Read during Update and you get the last frame's.</summary>
	public CameraView View { get; private set; }

	bool _composed;
	CameraView _composedBase;
	CameraView _renderView;

	/// <summary>Composes the view from modifiers and effects; the scene does this each tick</summary>
	public void ComposeView()
	{
		ComposeView( Scene.IsEditor ? null : GatherModifiers( Scene ) );
	}

	//Scene-wide camera modifiers sorted by order, shared by all cameras this tick; null in editor
	internal static ICameraModifier[] GatherModifiers( Scene scene )
	{
		var count = 0;
		foreach ( var _ in scene.GetAll<ICameraModifier>() ) count++;

		if ( count == 0 ) return System.Array.Empty<ICameraModifier>();

		var sorted = new ICameraModifier[count];
		var at = 0;

		foreach ( var modifier in scene.GetAll<ICameraModifier>() )
		{
			if ( at == count ) break;

			var i = at++;
			while ( i > 0 && sorted[i - 1].CameraOrder > modifier.CameraOrder ) { sorted[i] = sorted[i - 1]; i--; }
			sorted[i] = modifier;
		}

		return at == count ? sorted : sorted[..at];
	}

	internal void ComposeView( ICameraModifier[] modifiers )
	{
		var view = RawView;

		//The modifier chain reshapes the view in order (scope zoom, vehicle roll), then gets the final view to place things against (view models).
		if ( modifiers is not null )
		{
			foreach ( var modifier in modifiers )
			{
				modifier.ModifyCamera( this, ref view );
			}

			View = view;

			foreach ( var modifier in modifiers )
			{
				modifier.PostCameraSetup( this, view );
			}
		}
		else
		{
			View = view;
		}

		_renderView = view;

		//Transient camera effects (screen shake, punches) bake into the render view only - the composed
		//view and component transform are never touched, so they can't accumulate or move the player's aim.
		if ( CameraEffectSystem.Get( Scene ) is { } effects )
		{
			effects.QueryOffsets( this, out var shakeOffset, out var shakeAngles, out var shakeFov );

			_renderView.Position += _renderView.Rotation * shakeOffset;
			_renderView.Rotation *= shakeAngles;
			_renderView.FieldOfView = (_renderView.FieldOfView + shakeFov).Clamp( 1f, 179f );
		}

		//What the component looked like when composition finished. A camera changed after this stomps the composition (see UpdateSceneCameraTransform).
		_composedBase = RawView;

		_composed = true;
	}

	//The camera's own values, untouched by modifiers or effects.
	CameraView RawView => new()
	{
		Position = WorldPosition,
		Rotation = WorldRotation,
		FieldOfView = FieldOfView,
		ZNear = ZNear,
		ZFar = ZFar
	};

	//The view the renderer uses: composed view, or raw values if never composed or changed since
	internal CameraView RenderView
	{
		get
		{
			var raw = RawView;

			if ( !_composed || raw != _composedBase ) _renderView = raw;

			return _renderView;
		}
	}

	//The render view's field of view as the projection takes it - across the picture - for a picture
	//this wide over this tall. A camera whose FovAxis is vertical measures its own the other way.
	internal float RenderFieldOfView( float aspect )
	{
		var fov = RenderView.FieldOfView;

		return FovAxis == Axis.Vertical && aspect > 0.0f ? Screen.CreateVerticalFieldOfView( fov, aspect ) : fov;
	}

	internal void UpdateSceneCameraTransform( SceneCamera camera )
	{
		var view = RenderView;

		camera.Position = view.Position;
		camera.Rotation = view.Rotation;
		camera.ZNear = view.ZNear;
		camera.ZFar = view.ZFar;
		camera.Rect = new Rect( Viewport.x, Viewport.y, Viewport.z, Viewport.w );
		camera.Size = ScreenRect.Size;
		camera.Ortho = Orthographic;
		camera.OrthoHeight = OrthographicHeight;
		camera.FieldOfView = RenderFieldOfView( ScreenRect.Height > 0 ? ScreenRect.Width / ScreenRect.Height : 0.0f );
	}

	internal void UpdateSceneCameraStereo( SceneCamera camera )
	{
		camera.TargetEye = TargetEye;
		camera.WantsStereoSubmit = !Scene.IsEditor;
	}

	/// <summary>Update a SceneCamera with the settings from this component</summary>
	public void UpdateSceneCamera( SceneCamera camera, bool includeTags = true )
	{
		if ( Scene is null )
		{
			Log.Warning( $"Trying to update camera from {this} but has no scene" );
			return;
		}

		UpdateSceneCameraTransform( camera );

		camera.World = Scene.SceneWorld;
		camera.ClearFlags = ClearFlags;
		camera.Worlds.Clear();
		camera.Worlds.Add( Scene.DebugSceneWorld );

		//Also render any active gizmos
		if ( Gizmo.Active is not null )
			camera.Worlds.Add( Gizmo.Active.World );

		CopyPostProcessing( camera );

		//A camera (such as the editor viewport) might not want to take render tags into account from an existing camera source
		if ( includeTags )
		{
			camera.RenderTags.SetFrom( RenderTags );
			camera.ExcludeTags.SetFrom( RenderExcludeTags );
		}

		//Merge our global Scene attributes into the camera
		Scene.RenderAttributes.MergeTo( camera.Attributes );

		camera.DebugMode = DebugMode;
		camera.WireframeMode = WireframeMode;
		camera.EnablePostProcessing = EnablePostProcessing;
		camera.BackgroundColor = ClearFlags.Contains( ClearFlags.Color ) ? BackgroundColor : Color.Transparent;
	}

	internal void CopyPostProcessing( SceneCamera camera )
	{
		if ( Scene is null )
		{
			Log.Warning( $"Trying to update camera from {this} but has no scene" );
			return;
		}

		//Don't set the background colour if we don't clear color Also don't hook into render overlays, nor volumetric fog stuff.
		if ( ClearFlags.Contains( ClearFlags.Color ) )
		{
			camera.VolumetricFog.Enabled = Scene.Get<VolumetricFogVolume>() is not null;
			camera.VolumetricFog.DrawDistance = 104.0f;
			camera.VolumetricFog.FadeInStart = 1.6f;
			camera.VolumetricFog.FadeInEnd = 6.5f;
			camera.VolumetricFog.IndirectStrength = 1.0f;
			camera.VolumetricFog.Anisotropy = 1;
			camera.VolumetricFog.Scattering = 1.0f;
			camera.VolumetricFog.BakedIndirectTexture = Scene.Get<VolumetricFogController>()?.BakedFogTexture;
		}

		//Child camera executes command lists from this camera
		camera.OnRenderStageHook = ExecuteCommandLists;
	}

	//Update the SceneCamera UI with the settings from this component
	private void UpdateSceneCameraUI( SceneCamera camera )
	{
		if ( Scene is null )
			return;

		camera.OnRenderUI = () => OnCameraRenderUI( camera );
	}

	private void OnCameraRenderUI( SceneCamera camera )
	{
		if ( Scene is null )
			return;

		foreach ( var c in Scene.GetAll<ScreenPanel>().OrderBy( x => x.ZIndex ) )
		{
			if ( !c.Active ) continue;
			var target = c.TargetCamera ?? (IsMainCamera ? this : null);
			if ( target != this ) continue;
			if ( RenderExcludeTags.HasAny( c.GameObject.Tags ) ) continue;

			c.Render();
		}
	}

	internal bool IsSceneEditorCamera;

	internal void InitializeRendering( bool renderUI = true )
	{
		using ( Scene.Push() )
		{
			EnsureSceneCameraCreated();

			if ( IsSceneEditorCamera )
			{
				UpdateSceneCamera( sceneCamera );
				Scene.Camera?.CopyPostProcessing( sceneCamera );
			}
			else
			{
				UpdateSceneCamera( sceneCamera );
			}

			UpdateSceneCameraUI( sceneCamera );
			sceneCamera.RenderUI = renderUI;
			UpdateSceneCameraStereo( sceneCamera );
		}
	}

	//This should only be called when creating render lists!!
	internal void AddToRenderList( Vector2? size )
	{
		if ( !Active )
			return;

		//if width and height are too small, skip it
		if ( Viewport.z <= 0 ) return;
		if ( Viewport.w <= 0 ) return;

		using ( Scene.Push() )
		{
			InitializeRendering();
		//No-op: nothing to do here.
		}
	}

	public Vector2 PointToScreenNormal( in Vector3 worldPosition )
	{
		EnsureSceneCameraCreated();
		UpdateSceneCameraTransform( sceneCamera );

		return sceneCamera.ToScreenNormal( worldPosition );
	}

	public Vector2 PointToScreenPixels( in Vector3 worldPosition )
	{
		var sr = ScreenRect;
		var v = PointToScreenNormal( worldPosition );
		return new Vector2( v.x, v.y ) * sr.Size;
	}

	/// <summary>The size of the viewport, in screen coordinates</summary>
	public Rect ScreenRect
	{
		get
		{
			var ss = CustomSize ?? Screen.Size;
			return new Rect( ss.x * Viewport.x, ss.y * Viewport.y, ss.x * Viewport.z, ss.y * Viewport.w );
		}
	}

	/// <summary>Given a BBox in world space, will return the screen space rect that totally contains the box.</summary>
	public Rect BBoxToScreenPixels( BBox bounds, out bool isBehind )
	{
		Vector2[] corners = new Vector2[8];
		Vector3 min = bounds.Mins;
		Vector3 max = bounds.Maxs;

		isBehind = true;

		corners[0] = PointToScreenPixels( new Vector3( min.x, min.y, min.z ) );
		corners[1] = PointToScreenPixels( new Vector3( max.x, min.y, min.z ) );
		corners[2] = PointToScreenPixels( new Vector3( min.x, max.y, min.z ) );
		corners[3] = PointToScreenPixels( new Vector3( min.x, min.y, max.z ) );
		corners[4] = PointToScreenPixels( new Vector3( max.x, max.y, min.z ) );
		corners[5] = PointToScreenPixels( new Vector3( min.x, max.y, max.z ) );
		corners[6] = PointToScreenPixels( new Vector3( max.x, min.y, max.z ) );
		corners[7] = PointToScreenPixels( new Vector3( max.x, max.y, max.z ) );

		Vector2 minScreen = corners[0];
		Vector2 maxScreen = corners[0];

		for ( int i = 1; i < 8; i++ )
		{
			minScreen = Vector2.Min( minScreen, corners[i] );
			maxScreen = Vector2.Max( maxScreen, corners[i] );
		}

		var ss = CustomSize ?? Screen.Size;

		//off screen
		if ( maxScreen.x < 0 ) isBehind = true;
		if ( maxScreen.y < 0 ) isBehind = true;
		if ( minScreen.x > ss.x ) isBehind = true;
		if ( minScreen.y > ss.y ) isBehind = true;

		return Rect.FromPoints( minScreen, maxScreen );
	}

	public Vector2 PointToScreenPixels( Vector3 worldPosition, out bool isBehind )
	{
		EnsureSceneCameraCreated();
		UpdateSceneCameraTransform( sceneCamera );

		var sr = ScreenRect;
		var v = sceneCamera.ToScreenWithDirection( worldPosition );
		isBehind = v.z <= 0.0f;
		return new Vector2( v.x, v.y ) * sr.Size;
	}

	public Vector2 PointToScreenNormal( Vector3 worldPosition, out bool isBehind )
	{
		EnsureSceneCameraCreated();
		UpdateSceneCameraTransform( sceneCamera );

		var v = sceneCamera.ToScreenWithDirection( worldPosition );
		isBehind = v.z <= 0.0f;
		return new Vector2( v.x, v.y );
	}

	public Ray ScreenPixelToRay( Vector2 pixelPosition )
	{
		EnsureSceneCameraCreated();
		UpdateSceneCameraTransform( sceneCamera );

		return sceneCamera.GetRay( pixelPosition, ScreenRect.Size );
	}

	public Ray ScreenNormalToRay( Vector3 normalPosition )
	{
		var pixelPosition = new Vector3(
			normalPosition.x * ScreenRect.Size.x,
			normalPosition.y * ScreenRect.Size.y,
			normalPosition.z );

		return ScreenPixelToRay( pixelPosition );
	}

	/// <summary>Convert from screen coords to world coords on the near frustum plane.</summary>
	public Vector3 ScreenToWorld( Vector2 screen )
	{
		EnsureSceneCameraCreated();
		UpdateSceneCameraTransform( sceneCamera );

		return sceneCamera.ToWorld( screen );
	}

	protected override void OnEnabled()
	{
		Scene?.UpdateMainCamera();

		AddCommandList( _hudCommandList, Rendering.Stage.AfterPostProcess, 5000 );
		AddCommandList( _overlayCommandList, Rendering.Stage.AfterUI, 5000 );

		if ( RenderTexture is not null )
			AddCommandList( _renderTextureMipGenCommandList, Rendering.Stage.AfterPostProcess, 10000 );
	}

	protected override void OnDisabled()
	{
		Scene?.UpdateMainCamera();

		RemoveCommandList( _hudCommandList );
		RemoveCommandList( _overlayCommandList );
		RemoveCommandList( _renderTextureMipGenCommandList );
	}

	/// <summary>Returns the view frustum of the current screen rect.</summary>
	public Frustum GetFrustum()
	{
		UpdateSceneCameraTransform( sceneCamera );
		return sceneCamera.GetFrustum( ScreenRect );
	}

	/// <summary>Given a pixel rect return a frustum on the current camera.</summary>
	public Frustum GetFrustum( Rect screenRect )
	{
		//Note: Updating every time might cause issues if too much pressure is put on this function. Keep an eye on it.
		UpdateSceneCameraTransform( sceneCamera );
		return sceneCamera.GetFrustum( screenRect );
	}

	/// <summary>Given a pixel rect return a frustum on the current camera. Pass in 1 to ScreenSize to use normalized screen coords.</summary>
	public Frustum GetFrustum( Rect screenRect, Vector3 screenSize )
	{
		//Same as above
		UpdateSceneCameraTransform( sceneCamera );
		return sceneCamera.GetFrustum( screenRect, screenSize );
	}

	//Our renderer draws into a picture rather than into a texture of the game's, so this draws the scene and then writes the pixels across.
	/// <summary>Render scene to a texture from this camera's point of view</summary>
	public bool RenderToTexture( Texture target, in ViewSetup config = default )
	{
		if ( target is null || !target.IsValid )
			return false;

		if ( !Graphics.IsActive )
		{
			Scene.PreCameraRender();
			InitializeRendering();
		}

		if ( !CoreScenePreview.Available )
			return false;

		using var picture = new Bitmap( target.Width, target.Height );

		if ( !CoreScenePreview.Shared.Render( Scene, this, picture ) )
			return false;

		target.Update( (ReadOnlySpan<Color32>)picture.GetPixels32(), 0, 0, picture.Width, picture.Height );

		return true;
	}


	/// <summary>Allows specifying a custom projection matrix for this camera</summary>
	public Matrix? CustomProjectionMatrix
	{
		get => SceneCamera.CustomProjectionMatrix;
		set => SceneCamera.CustomProjectionMatrix = value;
	}

	/// <summary>Allows specifying a custom aspect ratio for this camera. By default (or when null) the camera size is screen size or render target size.</summary>
	[JsonIgnore, Hide]
	public Vector2? CustomSize { get; set; }

	/// <summary>Get frustum projection matrix.</summary>
	public Matrix ProjectionMatrix => sceneCamera.ProjectionMatrix;

	/// <summary>Calculates a projection matrix with an oblique clip-plane defined in world space.</summary>
	public Matrix CalculateObliqueMatrix( Plane clipPlane )
	{
		var tx = WorldTransform;
		var normal = tx.Rotation.Inverse * clipPlane.Normal;

		//Camera space is the engine's own now - x right, y up, -z forward - and view space is that with the
		//distance in front on +z, so only z turns over. The swizzle this used to carry was reading a Z-up camera.
		normal = new Vector3( normal.x, normal.y, -normal.z ).Normal;

		System.Numerics.Matrix4x4 m = ProjectionMatrix;

		Vector4 q = default;

		q.x = (MathF.Sign( normal.x ) - m.M13) / m.M11;
		q.y = (MathF.Sign( normal.y ) - m.M23) / m.M22;
		q.z = 1f;
		q.w = (1f - m.M33) / m.M34;

		var plane = new Vector4( normal, Vector3.Dot( tx.Position - clipPlane.Position, clipPlane.Normal ) );
		var c = plane * (1.0f / System.Numerics.Vector4.Dot( plane, q ));

		m.M31 = -c.x;
		m.M32 = -c.y;
		m.M33 = -c.z;
		m.M34 = c.w;

		return m;
	}

	public enum Axis
	{
		/// <summary>Fits the view within the x-axis.</summary>
		[Icon( "panorama_horizontal" )]
		Horizontal,

		/// <summary>Fits the view within the y-axis.</summary>
		[Icon( "panorama_vertical" )]
		Vertical
	}

	/// <summary>Render this camera to the target bitmap.</summary>
	public void RenderToBitmap( Bitmap targetBitmap ) => RenderToBitmap( targetBitmap, true );

	/// <summary>Render this camera to the target bitmap, with the option to include or exclude the drawn UI.</summary>
	public void RenderToBitmap( Bitmap targetBitmap, bool renderUI )
	{
		if ( targetBitmap == null || targetBitmap.Width <= 1 || targetBitmap.Height <= 1 )
			return;

		if ( !this.IsValid() )
			return;

		using ( Scene.Push() )
		{
			Scene.PreCameraRender();
			InitializeRendering( renderUI );

			//Resize the UI to the target bitmap size
			if ( renderUI )
				ResizeUI( targetBitmap.Size );

			//Our renderer draws this the way it draws a window: a viewport with no window to present to.
			if ( CoreScenePreview.Available )
			{
				CoreScenePreview.Shared.Render( Scene, this, targetBitmap );
			}

			//Resize the UI back to the screen size
			if ( renderUI )
				ResizeUI( new Vector2( Screen.Width, Screen.Height ) );
		}
	}

	private void ResizeUI( Vector2 size )
	{
		if ( Scene is null )
			return;

		foreach ( var panel in Scene.GetAll<ScreenPanel>() )
		{
			if ( !panel.IsValid() || !panel.Active )
				continue;

			var target = panel.TargetCamera ?? (IsMainCamera ? this : null);
			if ( target != this )
				continue;

			if ( RenderExcludeTags.HasAny( panel.GameObject.Tags ) )
				continue;

			if ( panel.GetPanel() is not RootPanel rootPanel || !rootPanel.IsValid() )
				continue;

			var screenRect = new Rect( 0, 0, size.x, size.y );

			rootPanel.PreLayout( screenRect );
			rootPanel.CalculateLayout();
			rootPanel.PostLayout();

			rootPanel.BuildCommandList();
		}
	}
}
