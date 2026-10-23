using Rinten.Rendering;
using Rinten.VR;

namespace Rinten;

/// <summary>Represents a camera and holds render hooks. This camera can be used to draw tool windows and scene panels.</summary>
[Expose]
public sealed partial class SceneCamera : IDisposable, IManagedCamera
{
	//Our own projection, with no native renderer involved. Same three answers - the matrices, where
	//a point lands, how big it looks - worked out from the camera rather than asked of a native frustum.
	CoreCameraFrustum _coreFrustum;

	private const bool OnCore = true;

	//The projection the camera draws with: reverse depth, one at the near plane and nought at the far - see CoreCameraFrustum.
	internal Matrix ProjectionMatrix => CoreFrustum.GetProj();

	public RenderAttributes Attributes { get; }

	/// <summary>The name of this camera.. for debugging purposes.</summary>
	public string Name { get; set; }

	/// <summary>Scene objects with any of these tags won't be rendered by this camera.</summary>
	public ITagSet ExcludeTags { get; private set; } = new TokenBasedTagSet();

	/// <summary>Only scene objects with one of these tags will be rendered by this camera.</summary>
	public ITagSet RenderTags { get; private set; } = new TokenBasedTagSet();

	//Keep hidden! CommandBuffers only!!
	internal Action<Rendering.Stage, SceneCamera> OnRenderStageHook;

	/// <summary>Called when rendering the post process pass</summary>
	[Obsolete]
	public Action OnRenderPostProcess { get; set; }

	/// <summary>Called when rendering the transparent pass</summary>
	[Obsolete]
	public Action OnRenderOpaque { get; set; }

	/// <summary>Called when rendering the transparent pass</summary>
	[Obsolete]
	public Action OnRenderTransparent { get; set; }

	public Action OnRenderOverlay { get; set; }

	public Action OnRenderUI { get; set; }

	/// <summary>The size of the screen. Allows us to work out aspect ratio. For now will get updated automatically on render.</summary>
	public Vector2 Size
	{
		get => _size;
		set
		{
			if ( _size == value )
				return;

			_size = value;
			FrustumDirty = true;
		}
	}

	Vector2 _size = new Vector2( 512, 512 );

	/// <summary>Control volumetric fog parameters, expect this to take 1-2ms of your GPU frame time.</summary>
	public VolumetricFogParameters VolumetricFog { get; init; } = new();

	//The rotation for each of the 6 cube faces, in the order the face index runs: +X, -X, +Y, -Y, +Z, -Z. Cube space is world space,
	//so a face looks straight along the world axis it is named for - see CoreEnvmap.Way, which is the same table for the live path.
	internal static readonly Rotation[] CubeRotations =
	{
		Rotation.LookAt(Vector3.Right, Vector3.Up),        //+X
		Rotation.LookAt(Vector3.Left, Vector3.Up),         //-X
		Rotation.LookAt(Vector3.Up, Vector3.Forward),      //+Y
		Rotation.LookAt(Vector3.Down, Vector3.Forward),    //-Y
		Rotation.LookAt(Vector3.Backward, Vector3.Up),     //+Z
		Rotation.LookAt(Vector3.Forward, Vector3.Up)       //-Z
	};

	public SceneCamera( string name = "Unnamed" )
	{
		Attributes = new RenderAttributes();
		Name = name;
		InitCommon();
	}

	~SceneCamera()
	{
		Dispose( disposing: false );
	}

	private bool disposedValue;

	private void Dispose( bool disposing )
	{
		if ( !disposedValue )
		{
			//Release native CStrongHandle copies held by texture tracking in our RenderAttributes. Without this, the handles
			//survive until the RenderAttributes finalizer runs which may be after the resource system has already reported leaks.
			Attributes?.Clear();

			disposedValue = true;
		}
	}

	public void Dispose()
	{
		//The camera standing in for this one, if it was ever drawn - a scene object, so let go of here on the main thread rather than by the finalizer.
		CoreSceneCameras.Release( this );

		Dispose( disposing: true );
		GC.SuppressFinalize( this );
	}


	internal int _cameraId;

	internal void InitCommon()
	{
		_cameraId = (this as IManagedCamera).AllocateCameraId();

		//TODO: lets expose these as properties
		Attributes.Set( "renderOpaque", true );
		Attributes.Set( "renderTranslucent", true );
		Attributes.Set( "directLighting", true );
		Attributes.Set( "indirectLighting", true );
		Attributes.Set( "renderSun", true );
		Attributes.Set( "drawShadows", true );

		FieldOfView = 70.0f;
	}

	//The managed one, kept up to date the same way as theirs.
	ref CoreCameraFrustum CoreFrustum
	{
		get
		{
			if ( FrustumDirty )
			{
				if ( Size.y <= 0.0f )
					Size = new Vector2( 1, 1 );

				var aspect = Size.x / Size.y;

				if ( Ortho )
				{
					var orthoWidth = Size.x * (OrthoHeight / Size.y);

					_coreFrustum.InitOrthoCamera( Position, Angles, ZNear, ZFar, orthoWidth, OrthoHeight );
				}
				else
				{
					_coreFrustum.InitCamera( Position, Angles, ZNear, ZFar, FieldOfView, aspect );
				}

				_coreFrustum.SetCameraWidthHeight( Size.x, Size.y );

				FrustumDirty = false;
			}

			return ref _coreFrustum;
		}
	}

	bool FrustumDirty { get; set; } = true;



	public override string ToString() => $"SceneCamera:{Name}";

	HashSet<SceneWorld> _worlds = new HashSet<SceneWorld>();
	SceneWorld _world;

	/// <summary>The world we're going to render.</summary>
	public SceneWorld World //PaintDay: MainWorld?
	{
		get => _world;
		set
		{
			if ( _world == value ) return;

			_worlds?.Remove( _world );

			_world = value;

			if ( _world.IsValid() )
			{
				_worlds?.Add( _world );
			}
		}
	}

	/// <summary>Your camera can render multiple worlds.</summary>
	public HashSet<SceneWorld> Worlds => _worlds;

	Transform _transform = new Transform();

	/// <summary>The position of the scene's camera.</summary>
	public Vector3 Position
	{
		get => _transform.Position;
		set
		{
			_transform.Position = value;
			FrustumDirty = true;
		}
	}

	/// <summary>The rotation of the scene's camera.</summary>
	public Rotation Rotation
	{
		get => _transform.Rotation;
		set
		{
			_transform.Rotation = value;
			FrustumDirty = true;
		}
	}

	/// <summary>The rotation of the scene's camera.</summary>
	public Angles Angles
	{
		get => _transform.Rotation;

		set
		{
			_transform.Rotation = value;
			FrustumDirty = true;
		}
	}

	float _fov = 90;

	/// <summary>The horizontal field of view of the Camera in degrees.</summary>
	public float FieldOfView
	{
		get => _fov;
		set
		{
			_fov = value;
			FrustumDirty = true;
		}
	}

	float _zfar = 100000;

	/// <summary>The camera's zFar distance. Keep as short as the game allows, roughly 1000-30000</summary>
	public float ZFar
	{
		get => _zfar;
		set
		{
			_zfar = value;
			FrustumDirty = true;
		}
	}

	float _znear = 1;

	/// <summary>The camera's zNear distance. This is the closest distance this camera will be able to render. A good value for this is about 5. Below 5 and particularly below 1 you're going to start to see a lot of artifacts like z-fighting.</summary>
	public float ZNear
	{
		get => _znear;
		set
		{
			_znear = value;
			FrustumDirty = true;
		}
	}

	bool _ortho;

	/// <summary>Whether to use orthographic projection.</summary>
	public bool Ortho
	{
		get => _ortho;
		set
		{
			_ortho = value;
			FrustumDirty = true;
		}
	}

	/// <summary>Height of the ortho when <see cref="Ortho"/> is enabled.</summary>
	public float OrthoHeight
	{
		get => Attributes.GetFloat( "cam.orthoHeight" );
		set
		{
			Attributes.Set( "cam.orthoHeight", value );
			FrustumDirty = true;
		}
	}

	/// <summary>Render this camera using a different render mode</summary>
	public SceneCameraDebugMode DebugMode
	{
		get => (SceneCameraDebugMode)Attributes.GetInt( "ToolsVisMode" );
		set => Attributes.Set( "ToolsVisMode", (int)value );
	}

	/// <summary>Render this camera using a wireframe view.</summary>
	public bool WireframeMode
	{
		get => Attributes.GetInt( "Wireframe" ) >= 1;
		set => Attributes.Set( "Wireframe", value ? 1 : 0 );
	}

	/// <summary>What kind of clearing should we do before we begin?</summary>
	public ClearFlags ClearFlags { get; set; } = ClearFlags.All;

	private Rect rect = new Rect( Vector2.Zero, Vector2.One );

	/// <summary>The rect of the screen to render to. This is normalized, between 0 and 1.</summary>
	public Rect Rect
	{
		get => rect;
		set
		{
			var newRect = value;
			newRect.Left = newRect.Left.Clamp( 0, 1 );
			newRect.Top = newRect.Top.Clamp( 0, 1 );

			rect = newRect;
		}
	}

	/// <summary>Color the scene camera clears the render target to.</summary>
	public Color BackgroundColor { get; set; }

	/// <summary>The color of the ambient light. Set it to black for no ambient light, alpha is used for lerping between IBL and constant color.</summary>
	public Color AmbientLightColor
	{
		get;
		set;
	} = Color.Transparent;

	/// <summary>Enable or disable anti-aliasing for this render.</summary>
	public bool AntiAliasing
	{
		get => Attributes.GetInt( "msaa", 4 ) == 4;
		set => Attributes.Set( "msaa", value ? 4 : 0 ); //4 = RENDER_MULTISAMPLE_8X
	}

	/// <summary>Toggle all post processing effects for this camera. The default is on.</summary>
	public bool EnablePostProcessing { get; set; } = true;

	//Should this camera render engine overlays, you'd only want this on the main camera.
	internal bool EnableEngineOverlays { get; set; } = false;

	//Whether the UI stage layer should be created for this camera render. When false, the native pipeline will skip the UI layer entirely.
	internal bool RenderUI { get; set; } = true;

	//When true, rendering from this camera won't request higher mip levels from the texture streaming system. Used
	//by cubemap rendering to prevent envmap probes from pulling in full-resolution textures across the entire map.
	internal bool ExcludeFromTextureStreaming { get; set; }

	private static WeakReference<SceneCamera> _recordingCamera;

	//The camera currently used for video/screenshot recording.
	internal static SceneCamera RecordingCamera
	{
		get => _recordingCamera?.TryGetTarget( out var cam ) == true ? cam : null;
		set => _recordingCamera = value is not null ? new WeakReference<SceneCamera>( value ) : null;
	}

	internal bool IsRecordingCamera => RecordingCamera == this;

	/// <summary>The HMD eye that this camera is targeting. Use <see cref="StereoTargetEye.None"/> for the user's monitor (i.e. the companion window).</summary>
	public StereoTargetEye TargetEye { get; set; } = StereoTargetEye.None;

	/// <summary>Set this to false if you don't want the stereo renderer to submit this camera's texture to the compositor. This option isn't considered if <see cref="TargetEye"/> is <see cref="StereoTargetEye.None"/>.</summary>
	public bool WantsStereoSubmit { get; set; } = false;

	/// <summary>Enable or disable direct lighting</summary>
	public bool EnableDirectLighting
	{
		get => Attributes.GetBool( "directLighting" );
		set => Attributes.Set( "directLighting", value );
	}

	/// <summary>Enable or disable indirect lighting</summary>
	public bool EnableIndirectLighting
	{
		get => Attributes.GetBool( "indirectLighting" );
		set => Attributes.Set( "indirectLighting", value );
	}

	//Should be called before a render
	internal void OnPreRender( Vector2 size )
	{
		Size = size;

		ConfigureView( default );
	}

	//Configure the view immediately before rendering. This will set the camera position etc on
	//the local camera renderer. This should be called immediately before adding the views etc.
	void ConfigureView( in ViewSetup config )
	{
		if ( _world.IsValid() && !Graphics.IsActive )
		{
			_world.UpdateObjectsForRendering( Position, ZFar );
		}
	}

	void IManagedCamera.OnRenderStage( Rendering.Stage renderStage )
	{
		switch ( renderStage )
		{
			case Rendering.Stage.AfterDepthPrepass:
				{
					//Light shadow-mask command lists (screen-space contact shadows) only need the full-res depth buffer, and this stage already runs per view with a valid.
					break;
				}

			case Rendering.Stage.AfterPostProcess:
				{
					OnRenderOverlay?.Invoke();
					break;
				}

			case Rendering.Stage.AfterUI:
				{
					OnRenderUI?.Invoke();
					break;
				}
		}

		//new stuff is commandlist based, so is total thread safe
		OnRenderStageHook?.InvokeWithWarning( renderStage, this );

		//Editor viewports don't use the game UI render hook, so capture the selected camera after its UI stage.
		if ( renderStage == Rendering.Stage.AfterUI && IsRecordingCamera && Application.IsEditor && !Game.IsPlaying )
		{
			ScreenCaptureUtility.CaptureFrame();
			ScreenCaptureUtility.DrawRecordingBorder();
		}
	}

	/// <summary>Given a pixel rect return a frustum on the current camera.</summary>
	public Frustum GetFrustum( Rect pixelRect ) => GetFrustum( pixelRect, Size );

	/// <summary>Given a pixel rect return a frustum on the current camera. Pass in 1 to ScreenSize to use normalized screen coords.</summary>
	public Frustum GetFrustum( Rect pixelRect, Vector3 screenSize )
	{
		if ( Ortho )
		{
			var min = Vector2.Min( pixelRect.TopLeft, pixelRect.BottomRight );
			var max = Vector2.Max( pixelRect.TopLeft, pixelRect.BottomRight );

			return Rinten.Frustum.FromOrtho( min, max, screenSize, Position, Rotation, OrthoHeight, ZNear, ZFar );
		}
		else
		{
			var c1 = pixelRect.TopLeft;
			var c2 = pixelRect.BottomRight;

			var tl = GetRay( new Vector2( MathF.Min( c1.x, c2.x ), MathF.Min( c1.y, c2.y ) ), screenSize );
			var tr = GetRay( new Vector2( MathF.Max( c1.x, c2.x ), MathF.Min( c1.y, c2.y ) ), screenSize );
			var bl = GetRay( new Vector2( MathF.Min( c1.x, c2.x ), MathF.Max( c1.y, c2.y ) ), screenSize );
			var br = GetRay( new Vector2( MathF.Max( c1.x, c2.x ), MathF.Max( c1.y, c2.y ) ), screenSize );

			//As far as the drawn view reaches: its far plane is pushed out to meet ZFar at the corners
			var aspect = Size.y > 0 ? Size.x / Size.y : 1.0f;
			return Rinten.Frustum.FromCorners( tl, tr, br, bl, ZNear, ZFar * CoreSceneDrawer.CornerStretch( FieldOfView, aspect ) );
		}
	}

	/// <summary>Given a cursor position get a scene aiming ray.</summary>
	public Ray GetRay( Vector3 cursorPosition ) => GetRay( cursorPosition, Size );

	/// <summary>Given a cursor position get a scene aiming ray.</summary>
	public Ray GetRay( Vector2 cursorPosition, Vector3 screenSize )
	{
		if ( screenSize.x <= 0.0f || screenSize.y <= 0.0f )
			return new Ray( Position, Rotation.Forward );

		if ( !Ortho )
		{
			var aspect = screenSize.x / screenSize.y;

			//Screen to camera space: x grows to the right, y grows down a screen and up in the world,
			//and the ray leaves along -Z. The same two expressions the orthographic branch below uses.
			var ndcX = (2.0f * cursorPosition.x / screenSize.x) - 1.0f;
			var ndcY = 1.0f - (2.0f * cursorPosition.y / screenSize.y);

			float halfWidth = MathF.Tan( FieldOfView * MathF.PI / 360.0f );
			float halfHeight = halfWidth / aspect;

			var ray = new Vector3( ndcX * halfWidth, ndcY * halfHeight, -1.0f ) * Rotation;
			return new Ray( Position, ray.Normal );
		}
		else
		{
			var screenX = cursorPosition.x;
			var screenY = cursorPosition.y;
			var halfScreenHeight = OrthoHeight / 2f;
			var halfScreenWidth = halfScreenHeight * screenSize.x / screenSize.y;
			var orthoX = (2f * screenX / screenSize.x - 1f) * halfScreenWidth;
			var orthoY = (1f - 2f * screenY / screenSize.y) * halfScreenHeight;
			var forward = Rotation.Forward;

			return new Ray
			{
				Position = Position + Rotation.Right * orthoX + Rotation.Up * orthoY + forward * ZNear,
				Forward = forward
			};
		}
	}

	/// <summary>Convert from world coords to screen coords. The results for x and y will be from 0 to <see cref="Size"/>.</summary>
	public Vector2 ToScreen( Vector3 world )
	{
		var v = ToScreenWithDirection( world );

		return new Vector2( v.x, v.y ) * Size;
	}

	/// <summary>Convert from world coords to screen coords. The results for x and y will be from 0 to <see cref="Size"/>.</summary>
	public bool ToScreen( Vector3 world, out Vector2 screen )
	{
		var v = ToScreenWithDirection( world );
		screen = new Vector2( v.x, v.y ) * Size;
		return v.z > 0.0f;
	}

	/// <summary>Convert from world coords to normal screen corrds. The results will be between 0 and 1</summary>
	public Vector2 ToScreenNormal( Vector3 world )
	{
		var v = ToScreenWithDirection( world );

		return new Vector2( v.x, v.y );
	}

	//Convert from world coords to screen coords but the Z component stores whether this vector is
	//behind the screen (<0) or in front of it (>0). The X and Y components are normalized from 0 to 1.
	internal Vector3 ToScreenWithDirection( Vector3 world )
	{
		CoreFrustum.WorldToView( world, out var view );

		//The frustum answers from minus one at one edge to one at the other, with y up. A screen is nought to one with y down.
		var screen = new Vector2( view.x * 0.5f + 0.5f, 0.5f - view.y * 0.5f );

		//Which side of the camera it is on: what everything reading this asks before it draws anything at that point.
		var forward = Vector3.Dot( world - Position, Rotation.Forward );

		return new Vector3( screen.x, screen.y, forward );
	}

	/// <summary>Convert from screen coords to world coords on the near frustum plane.</summary>
	public Vector3 ToWorld( Vector2 screen )
	{
		screen.x = screen.x.Remap( 0, Size.x, -1, 1 );
		screen.y = screen.y.Remap( Size.y, 0, -1, 1 );

		//Back out through the same projection the frustum builds, onto the near plane - which is what a point on the screen is, in the world.
		var inverse = ( CoreFrustum.GetView() * CoreFrustum.GetProj() ).Inverted;

		var near = inverse.Transform( new Vector4( screen.x, screen.y, 1.0f, 1.0f ) );

		if ( MathF.Abs( near.w ) < 1e-6f ) return Position;

		return new Vector3( near.x, near.y, near.z ) / near.w;
	}

	internal void AddToRenderList( Vector2? size )
	{
		ViewSetup setup = default;
		setup.ViewHash = HashCode.Combine( this );

		if ( WantsToRenderInStereo() )
		{
			RenderStereo( setup );
		}
		else
		{
			Render( size, setup );
		}
	}

	private bool WantsToRenderInStereo()
	{
		return VRSystem.IsActive && TargetEye > StereoTargetEye.None;
	}

	private void Render( Vector2? size, in ViewSetup config = default )
	{
		var renderSize = size ?? Size;

		if ( renderSize.x <= 0 ) return;
		if ( renderSize.y <= 0 ) return;

		//Our renderer draws the window itself - see CoreGameRenderer - so all a camera does here is let anything watching know a frame is coming.
		OnPreRender( renderSize );
	}

	//Draws the scene from this camera into a texture via a stand-in camera component; returns if it drew
	internal bool RenderToTexture( Texture texture, Vector2? size, in ViewSetup config )
	{
		if ( texture is null || !texture.IsValid ) return false;

		var renderSize = size ?? texture.Size;

		if ( renderSize.x <= 1 || renderSize.y <= 1 ) return false;

		OnPreRender( renderSize );

		return CoreSceneCameras.RenderToTexture( this, texture, size );
	}


	//Draws the scene from this camera into a bitmap.
	internal void RenderToBitmap( Bitmap bitmap, in ViewSetup config = default )
	{
		if ( !bitmap.IsValid() )
			return;

		var renderSize = bitmap.Size;

		if ( renderSize.x <= 1 ) return;
		if ( renderSize.y <= 1 ) return;

		OnPreRender( renderSize );

		//Through a camera component standing in for this one - see CoreSceneCameras.
		CoreSceneCameras.RenderToBitmap( this, bitmap );
	}



	//Renders the six cube faces into mip 0 of the cube texture; waits for each face
	internal void RenderToCubeTexture( Texture texture, in ViewSetup config = default )
	{
		if ( texture is null || !texture.IsValid ) return;

		OnPreRender( new Vector2( texture.Width, texture.Height ) );

		CoreSceneCameras.RenderToCube( this, texture );
	}

	private void RenderStereo( in ViewSetup config = default )
	{
		//Stereo was their renderer submitting an eye at a time to the headset. There is no headset - see VRSystem - so there is nothing to submit.
	}


	/// <summary>Allows specifying a custom projection matrix for this camera</summary>
	public Matrix? CustomProjectionMatrix { get; set; }
}

/// <summary>Flags for clearing a RT before rendering a scene using a SceneCamera</summary>
[Flags, Expose]
public enum ClearFlags
{
	None = 0x00,

	[Icon( "palette" )]
	[Description( "The color buffer (the stuff you can see)" )]
	Color = 0xFF,

	[Icon( "table_rows" )]
	[Description( "The depth buffer" )]
	Depth = 0x100,

	[Icon( "interests" )]
	[Description( "The stencil" )]
	Stencil = 0x200,

	All = Color | Depth | Stencil
}

[Expose]
public enum SceneCameraDebugMode
{
	[Title( "Lit" ), Icon( "image" )]
	Normal = 0,

	[Title( "Full Bright" ), Icon( "lightbulb" )]
	FullBright = 1,

	[Title( "World-Space Normals" ), Icon( "shuffle" )]
	NormalMap = 21,

	[Title( "Albedo" ), Icon( "palette" )]
	Albedo = 10,

	[Title( "Roughness" ), Icon( "texture" )]
	Roughness = 12,

	[Title( "Diffuse" ), Icon( "cloud" )]
	Diffuse = 2,

	[Title( "Reflect" ), Icon( "flare" )]
	Reflect = 3,

	[Title( "Transmission" ), Icon( "deblur" )]
	Transmission = 4,

	[Title( "UV Maps" ), Icon( "gradient" )]
	ShowUV = 6,

	[Title( "Shader IDs" ), Icon( "sell" )]
	ShaderIDColor = 16,
	[Title( "Clustered Light Culling" ), Icon( "scatter_plot" )]
	ClusteredLightCulling = 50,

	[Title( "Quad Overdraw" ), Icon( "signal_cellular_null" )]
	QuadOverdraw = 100,
	[Title( "Overdraw" ), Icon( "layers" )]
	Overdraw = 101,
	[Title( "Ambient Occlusion" ), Icon( "radio_button_checked" )]
	AmbientOcclusion = 14,
	[Title( "Motion Vectors" ), Icon( "animation" )]
	MotionVectors = 102,
	[Title( "Reactive Mask" ), Icon( "shield" )]
	ReactiveMask = 103,

	/// <summary>What the cull pass sees: every instance it decided in its own colour, and magenta for a surface drawn with no instance - one the picture of IDs cannot see and the occlusion cannot hide. See CoreSceneDrawer.</summary>
	[Title( "Culling IDs" ), Icon( "grid_view" )]
	CullingIds = 60,

	/// <summary>Colours each surface by the sun shadow page level it reads, with a texel grid overlay</summary>
	[Title( "Sun Shadow Levels" ), Icon( "wb_sunny" )]
	SunShadowLevels = 70,
}
