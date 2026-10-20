using System;
using System.IO;
using System.Threading;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Rinten.Engine;

namespace Rinten.Android;

//Android host: passes surfaces and input to the engine, runs it off the main thread
//Activity settings live in AndroidManifest.xml; only the name is set here so manifests can name it
[Activity( Name = "rinten.android.MainActivity" )]
public sealed class MainActivity : Activity, ISurfaceHolderCallback
{
	private const string Tag = "rinten";

	//The engine's thread - one per process. The system may make a new activity while the process lives, and the game carries on in it.
	private static Thread engine;

	//The activity on the screen now, which the engine's thread asks for the keyboard and reports through.
	private static MainActivity current;

	//Whether the player closed the game, which is a game being ended rather than one falling over.
	private static volatile bool closing;

	//Whether this run has already left a report on the screen.
	private static bool reported;

	//What this run said, written to the phone as it says it.
	private static RunLog log;

	private GameView view;

	//Told what covers the screen's edges - see Insets.
	private Insets insets;

	//The window behind the surface, while there is one.
	private IntPtr native;

	//Whether native has been given to the engine.
	private bool given;

	private static bool EngineRunning => engine is { IsAlive: true };

	protected override void OnCreate( Bundle state )
	{
		base.OnCreate( state );

		current = this;

		//Nothing else is on the screen and nothing turns it off while a game is being played.
		RequestWindowFeature( WindowFeatures.NoTitle );
		Window?.AddFlags( WindowManagerFlags.KeepScreenOn );
		Window?.AddFlags( WindowManagerFlags.Fullscreen );

		DrawUnderCutout();

		//Once a process: an activity made again by the system is the same run.
		if ( log is null )
		{
			//Before anything else: what a thread nobody is catching for throws is the one kind of failure that leaves no trace
			//at all, and a report written here is on the phone whether or not the process survives long enough to show it.
			global::Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += ( _, e ) =>
				Report( "The game stopped unexpectedly", e.Exception );

			AppDomain.CurrentDomain.UnhandledException += ( _, e ) =>
				Report( "The game stopped unexpectedly", e.ExceptionObject as Exception );

			//Opened first, so that a run which dies before the engine says anything still leaves which phone and which build it was - see RunLog.
			log = RunLog.Open( this, CrashReport.About( this ) );
		}

		view = new GameView( this );
		view.Holder?.AddCallback( this );
		insets = new Insets();
		view.SetOnApplyWindowInsetsListener( insets );

		SetContentView( view );

		//What the engine asks of the phone, from its own thread: the keyboard is the activity's to show, and the motor is anybody's to run.
		CoreInput.HostKeyboard = shown => current?.RunOnUiThread( () => current?.view?.ShowKeyboard( shown ) );
		CoreInput.HostVibrate ??= DeviceVibrator.For( ApplicationContext ?? this );
		VoiceManager.HostMicrophone ??= Microphone.For( this, () => current );

		Immersive();
	}

	protected override void OnResume()
	{
		base.OnResume();

		current = this;

		Immersive();
		CoreLifecycle.Resume();
	}

	protected override void OnPause()
	{
		//Before the system can decide to end the process: the game is told it is going away, and what it saves there is written, before this returns.
		if ( EngineRunning && !reported ) CoreLifecycle.Pause( TimeSpan.FromSeconds( 1.5 ) );

		base.OnPause();
	}

	protected override void OnDestroy()
	{
		//Finishing is the player closing the game; anything else is the system making the activity again, and the game carries on in the next one.
		if ( IsFinishing && EngineRunning )
		{
			closing = true;
			Rinten.Application.WantsExit = true;
			CoreLifecycle.Resume();
		}

		if ( current == this ) current = null;

		base.OnDestroy();
	}

	public override void OnRequestPermissionsResult( int requestCode, string[] permissions, Permission[] grantResults )
	{
		base.OnRequestPermissionsResult( requestCode, permissions, grantResults );

		if ( requestCode == Microphone.RequestCode )
			Microphone.Answered( grantResults is { Length: > 0 } && grantResults[0] == Permission.Granted );
	}

	public override void OnWindowFocusChanged( bool hasFocus )
	{
		base.OnWindowFocusChanged( hasFocus );

		//The bars come back on a swipe from the edge, or when a dialog of the system's was up; they go again when the game has the screen back.
		if ( hasFocus ) Immersive();
	}

	public override bool DispatchTouchEvent( MotionEvent e )
	{
		//The report screen is ordinary views, and they want their touches.
		if ( e is null || reported || view is null ) return base.DispatchTouchEvent( e );

		TouchInput.Forward( e, view );
		return true;
	}

	public override bool DispatchKeyEvent( KeyEvent e )
	{
		if ( e is null || reported || !EngineRunning ) return base.DispatchKeyEvent( e );

		return KeyInput.Forward( e, view?.Typing ?? false ) || base.DispatchKeyEvent( e );
	}

	public override void OnBackPressed()
	{
		//Back reaches here only where it did not come as a key - see DispatchKeyEvent, which hands it on
		//as escape. Either way it never ends the game; once there is a report on the screen it closes it.
		if ( reported || !EngineRunning )
		{
#pragma warning disable CA1422
			base.OnBackPressed();
#pragma warning restore CA1422
			return;
		}

		CoreInput.HostKey( NativeEngine.ButtonCode.KEY_ESCAPE, true );
		CoreInput.HostKey( NativeEngine.ButtonCode.KEY_ESCAPE, false );
	}

	//The game drawn over the whole display, under the camera's cut-out as well - what is under it is said to the game as Screen.SafeInsets.
	private void DrawUnderCutout()
	{
		if ( Window?.Attributes is not { } attributes ) return;

		attributes.LayoutInDisplayCutoutMode = OperatingSystem.IsAndroidVersionAtLeast( 30 )
			? LayoutInDisplayCutoutMode.Always
			: LayoutInDisplayCutoutMode.ShortEdges;

		Window.Attributes = attributes;
	}

	//The status bar and the navigation bar off the screen, coming back over the game for a moment on a swipe from the edge - the way a game is played.
	private void Immersive()
	{
		if ( Window is null ) return;

		if ( OperatingSystem.IsAndroidVersionAtLeast( 30 ) )
		{
			//What Android 15 does by itself for a package that targets it, said for the ones before it.
#pragma warning disable CA1422
			Window.SetDecorFitsSystemWindows( false );
#pragma warning restore CA1422

			if ( Window.InsetsController is { } bars )
			{
				bars.Hide( WindowInsets.Type.SystemBars() );
				bars.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
			}

			return;
		}

#pragma warning disable CA1422 //the way before Android 11
		Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
			SystemUiFlags.ImmersiveSticky | SystemUiFlags.Fullscreen | SystemUiFlags.HideNavigation |
			SystemUiFlags.LayoutFullscreen | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutStable );
#pragma warning restore CA1422
	}

	//What covers the screen's edges, said to the engine whenever it changes - the phone turning, for one.
	private sealed class Insets : Java.Lang.Object, View.IOnApplyWindowInsetsListener
	{
		public WindowInsets OnApplyWindowInsets( View v, WindowInsets insets )
		{
			var cutout = insets?.DisplayCutout;

			CoreLifecycle.SetInsets(
				cutout?.SafeInsetLeft ?? 0, cutout?.SafeInsetTop ?? 0,
				cutout?.SafeInsetRight ?? 0, cutout?.SafeInsetBottom ?? 0 );

			return insets;
		}
	}

	public void SurfaceCreated( ISurfaceHolder holder )
	{
		native = NativeSurface.Take( holder?.Surface );
		given = false;

		if ( native == IntPtr.Zero )
			global::Android.Util.Log.Error( Tag, "the surface has no native window behind it" );
	}

	public void SurfaceChanged( ISurfaceHolder holder, global::Android.Graphics.Format format, int w, int h )
	{
		if ( native == IntPtr.Zero ) return;

		//The size arrives after the surface does, and the swapchain wants both - so this, and not SurfaceCreated, is where there is enough to hand over.
		if ( given )
		{
			CoreLifecycle.SurfaceResized( native, w, h );
			return;
		}

		given = true;
		CoreLifecycle.SurfaceGiven( native, w, h );

		Start( w, h );
	}

	public void SurfaceDestroyed( ISurfaceHolder holder )
	{
		if ( native == IntPtr.Zero ) return;

		//The window is going away, maybe under a frame being drawn into it. The engine lets its swapchain go and waits for
		//the next surface; this waits for it to have done so, because nothing may draw into the window once this returns.
		if ( given ) CoreLifecycle.SurfaceGone( native, EngineRunning ? TimeSpan.FromSeconds( 3 ) : TimeSpan.Zero );

		NativeSurface.Let( native );
		native = IntPtr.Zero;
		given = false;
	}

	private void Start( int width, int height )
	{
		if ( engine is not null ) return;

		engine = new Thread( () => Run( width, height ) ) { Name = "rinten", IsBackground = true };
		engine.Start();
	}

	private void Run( int width, int height )
	{
		try
		{
			var into = FilesDir?.AbsolutePath ?? Path.GetTempPath();

			//What the player makes - saves, settings - is kept beside the build rather than in it: the
			//build is thrown away whole when a new version is installed. See EngineFileSystem.DataFolder.
			var data = Path.Combine( into, "data" );

			var root = Unpack( into, data );

			if ( root is null )
			{
				Report( "This package has no game in it", null );
				return;
			}

			//Qualified: Android.OS has an Environment of its own, and both are in scope here.
			System.Environment.CurrentDirectory = root;

			EngineFileSystem.DataFolder = data;

			//The one platform-specific thing about starting a game here: the window is not ours to open. It is the surface handed over last.
			CoreAppSystem.WindowSource = CoreLifecycle.Adopt;

			//For a headset, whose loader and runtime want the VM and the activity before they will look for anything - see CoreXr.
			CoreAppSystem.AndroidXr = (Java.Interop.JniEnvironment.Runtime.InvocationPointer, Handle);

			log?.Say( $"a {width}x{height} surface, the game in {root}, its data in {data}" );

			new CoreAppSystem().Run();

			if ( closing ) log?.Close( "the game was closed" );

			//The loop returned, and nobody closed the game - so it stopped on its own. Whatever it could not do it said in the log before giving up,
			//which is what the report carries: OpenWindow, for one, answers a device it could not open with an error and a return rather than by throwing.
			if ( !closing ) Report( "The game stopped on its own", null );
		}
		catch ( Exception e )
		{
			Report( "The game could not start", e );
		}
		finally
		{
			//Only where there is nothing to read. A screen with the report on it is the
			//one thing a player can do something with, so it stays up until they leave it.
			if ( !reported ) ( current ?? this ).RunOnUiThread( () => ( current ?? this ).Finish() );
		}
	}

	//Writes what went wrong where the player can find it, and puts it on the screen in place of the game.
	private static void Report( string what, Exception e )
	{
		//Written whether or not there is an activity to show it in - the system may have taken it away and not yet made the next one.
		var activity = current;
		var context = (global::Android.Content.Context)activity ?? global::Android.App.Application.Context;

		var text = CrashReport.Text( context, what, e );

		global::Android.Util.Log.Error( Tag, text );

		//The run is over, one way or another. Said so before the report is kept, or the next start takes this run for one that died and keeps it twice.
		log?.Close( what );

		var file = Downloads.Write( context, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt", text );

		if ( reported || activity is null ) return;

		reported = true;

		activity.RunOnUiThread( () => CrashScreen.Show( activity, text, file ) );
	}

	//The build out of the package, or null where there is none.
	private string Unpack( string into, string data )
	{
		if ( Assets is null || !Payload.In( Assets ) ) return null;

		//Which build this is, and when it was put on the phone. The version alone is the same number for every export of a project until somebody
		//changes it, so a game installed over itself kept the content it unpacked the first time - a new engine over last week's shaders and scenes.
		var info = PackageManager?.GetPackageInfo( PackageName, 0 );
		var version = info is null ? "1" : $"{info.LongVersionCode}:{info.LastUpdateTime}";

		return Payload.Open( Assets, into, version, data );
	}
}
