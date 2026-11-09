using Rinten.Audio;
using Rinten.Engine;
using Rinten.UI;

namespace Rinten.UI;

//The engine's answers to what the toolkit asks: its type library, its design tokens, its mouse, gamepads and mixers
internal static partial class UiPlatform
{
	//The game's library knows what a game registered; there is none in a process that never loaded one, and the editor's own panels are up long before
	internal static partial Panel CreatePanel( string elementName ) => Game.TypeLibrary?.Create<Panel>( elementName, false );

	internal static partial void SetProperty( object target, string name, string value ) => Game.TypeLibrary?.SetProperty( target, name, value );

	internal static partial bool Describes( Type type ) => Game.TypeLibrary?.TryGetType( type, out _ ) ?? false;

	internal static partial string PrepareSheet( string text, string file ) => EditorTokens.PrepareSheetText( text, file );

	internal static partial Vector2 PointerPosition { get => Mouse.Position; set => Mouse.Position = value; }
	internal static partial Vector2 PointerDelta { get => Mouse.Delta; }
	internal static partial Vector2 PointerVelocity { get => Mouse.Velocity; }
	internal static partial bool PointerVisible { get => InputRouter.MouseCursorVisible; }
	internal static partial void SetCursor( string name ) => Mouse.CursorType = name;

	internal static partial void VirtualCursor( ref bool leftDown, ref Vector2 wheel )
	{
		if ( !Input.EnableVirtualCursor || Input.CurrentController is not { } controller ) return;

		var moveX = controller.GetAxis( NativeEngine.GameControllerAxis.LeftX );
		var moveY = controller.GetAxis( NativeEngine.GameControllerAxis.LeftY );

		if ( MathF.Abs( moveX ) > 0 || MathF.Abs( moveY ) > 0 )
		{
			var screen = global::Rinten.Screen.Size;
			var min = MathF.Min( screen.x, screen.y );
			Mouse.Position += new Vector2( moveX * min, moveY * min ) * Preferences.ControllerAnalogSpeed * RealTime.Delta;
		}

		leftDown |= InputRouter.IsButtonDown( GamepadCode.A );

		const float scrollScale = 0.5f;

		var wheelY = controller.GetAxis( NativeEngine.GameControllerAxis.RightY, 0 ) * scrollScale;
		var wheelX = controller.GetAxis( NativeEngine.GameControllerAxis.RightX, 0 ) * scrollScale;

		if ( MathF.Abs( wheelX ) > 0f ) wheel.x = wheelX;
		if ( MathF.Abs( wheelY ) > 0f ) wheel.y = wheelY;
	}

	internal static partial string Phrase( string token ) => Language.GetPhrase( token );

	internal static partial void ClosePopups( Panel pressed )
	{
		IMenuDll.Current?.ClosePopups( pressed );
		IGameInstanceDll.Current?.ClosePopups( pressed );
	}

	internal static partial bool Fetch( string path, Action arrived ) => WebContent.Request( path, arrived );

	internal static partial void For<TState>( int count, TState state, Action<TState, int> body ) => Rinten.Jobs.JobSystem.For( count, state, body );

	internal static partial bool InOrder { get => Rinten.Utility.Parallel.InOrder; }

	internal static partial void PlaySound( Panel panel, string sound )
	{
		var h = Sound.Play( sound );
		if ( !h.IsValid() ) return;

		if ( panel.FindRootPanel() is WorldPanel worldPanel )
		{
			//The element's own place in the world, not just its root's
			var worldPosition = worldPanel.Position;
			var panelPosition = panel.Box.Rect.Position;
			var worldRotation = worldPanel.Rotation * Rotation.FromYaw( 180 );
			var worldOffset = new Vector3( panelPosition.x, panelPosition.y, 0 );
			worldOffset = worldRotation * (worldOffset * ScenePanelObject.ScreenToWorldScale);
			h.TargetMixer = Mixer.FindMixerByName( "Game" );
			h.Position = worldPosition + worldOffset;
		}
		else
		{
			var normalizedScreenPosition = panel.Box.Rect.Center / global::Rinten.Screen.Size;
			normalizedScreenPosition -= 0.5f;
			h.TargetMixer = Mixer.FindMixerByName( "UI" );

			//In front of the listener, which is -z, with the screen's x across and its y - which grows downwards - up
			h.Position = new Vector3( normalizedScreenPosition.x.Clamp( -1, 1 ) * 256.0f,
				-normalizedScreenPosition.y.Clamp( -1, 1 ) * 64.0f, -64.0f );
			h.ListenLocal = true;
		}
	}

	internal static partial ITextureHost Textures { get => EngineTextures.Instance; }
	internal static partial IUiDevice Device { get => EngineDevice.Instance; }

	internal static partial long Frame() => EngineLoop.LoopFrames;
	internal static partial double Now() => Time.NowDouble;
	internal static partial float TimeScale() => ( Game.ActiveScene?.TimeScale ?? 1.0f ) * Scene.HostTimeScale;

	internal static partial Rect Screen()
	{
		Rinten.Screen.UpdateFromEngine();
		return new Rect( 0, 0, Rinten.Screen.Width, Rinten.Screen.Height );
	}

	internal static partial void PumpAsync() => EngineLoop.RunAsyncTasks();
	internal static partial void Spin() => IToolsDll.Current?.Spin();

	internal static partial void InputTicked( UISystem ui ) => UiGameInput.Route( ui );
	internal static partial void TickWorldInput( UISystem ui ) => UiGameInput.TickWorld( ui );

	internal static partial bool? PointerForced() => Mouse.Visibility switch
	{
		MouseVisibility.Visible => true,
		MouseVisibility.Hidden when !Game.IsMenu => false,
		_ => null,
	};

	internal static partial bool HidesPointer( RootPanel root ) => Game.IsMenu && root.RenderedManually && !Game.IsMainMenuVisible;

	internal static partial UiScope Scope( string name ) => new() { Inner = Performance.Scope( name ) };

	internal static partial bool IsHeadless { get => Application.IsHeadless; }
	internal static partial bool OnWeb { get => Application.OnWeb; }

	internal static partial void SetClipboard( string text ) => EnginePlatform.SetClipboardText( text );

	//The engine's own textures, made and loaded the way the rest of the engine makes and loads them
	private sealed class EngineTextures : ITextureHost
	{
		public static readonly EngineTextures Instance = new();

		public unsafe ITexture Create( in TextureRequest request )
		{
			var builder = Texture.Create( request.Width, request.Height, request.Format == TexturePixels.Bgra8 ? ImageFormat.BGRA8888 : ImageFormat.RGBA8888 );

			if ( request.Name is not null ) builder = builder.WithName( request.Name );
			if ( request.Mips == 0 ) builder = builder.WithMips();
			else if ( request.Mips > 1 ) builder = builder.WithMips( request.Mips );
			if ( request.Dynamic ) builder = builder.WithDynamicUsage();

			Texture texture;

			fixed ( byte* data = request.Data )
			{
				if ( !request.Data.IsEmpty ) builder = builder.WithData( (IntPtr)data, request.Data.Length );
				texture = builder.Finish();
			}

			//Taken as written: the colours are already multiplied by their alpha, and a chain's levels are plain averages - see CoreTexture.BuildLevels
			if ( request.Premultiplied && texture.core is { } core ) core.RawPixels = true;

			return texture;
		}

		public ITexture Load( string path ) => Texture.Load( path );

		public async Task<ITexture> LoadAsync( string path ) => await Texture.LoadAsync( path );

		public ITexture Invalid => Texture.Invalid;

		public ITexture Transparent => Texture.Transparent;
	}

	private sealed class EngineDevice : IUiDevice
	{
		public static readonly EngineDevice Instance = new();

		public int BindlessSlots => CoreRenderDevice.BindlessSlots;
		public int ResourceGeneration => CoreResources.Generation;
		public uint TextureIndex( ulong handle ) => CoreRender.TextureIndex( handle );
	}
}

//The engine's profiler section
internal ref partial struct UiScope
{
	internal Performance.ScopeSection Inner;

	public void Dispose() => Inner.Dispose();
}
