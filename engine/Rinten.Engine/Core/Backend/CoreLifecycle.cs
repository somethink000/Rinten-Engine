using System.Threading;
using Rinten.Audio;

namespace Rinten;

//Mobile app lifecycle: the host reports pause/resume and surface changes, Hold reacts each frame
internal static class CoreLifecycle
{
	private static readonly Logger log = new( "Lifecycle" );

	private static readonly object gate = new();

	//Whether a host is driving this at all - set by the first surface it gives.
	internal static bool Hosted { get; private set; }

	//What the host says, under the gate.

	//Whether the activity is out of the player's sight.
	static bool paused;

	//The surface the host says is current, or zero while it has none.
	static IntPtr surface;

	//Which surface that is, counting from one. Counted rather than compared by address: a window that was let go
	//and one made after it can be given the same address, and a swapchain on the first is no good on the second.
	static int generation;

	static int width;
	static int height;

	//The surface the engine is drawing into, as far as the host is to know - see SurfaceGone.
	static int drawing;

	//Whether the engine has told the game it is away - see Pause.
	static bool away;

	static Rinten.UI.Margin insets;
	static bool insetsChanged;

	//The engine's own, on its thread.

	//The window the engine has made on the surface it is drawing into.
	static CoreWindow window;

	//Which surface window is on, or zero.
	static int held;

	//A surface to draw into, from the host's thread. The first one is the one the game
	//starts on - see Adopt; every one after it replaces whatever was there before.
	internal static void SurfaceGiven( IntPtr native, int w, int h )
	{
		if ( native == IntPtr.Zero ) return;

		lock ( gate )
		{
			Hosted = true;

			surface = native;
			width = Math.Max( w, 1 );
			height = Math.Max( h, 1 );
			generation++;

			Monitor.PulseAll( gate );
		}
	}

	//The same surface at another size - the device turned. From the host's thread.
	internal static void SurfaceResized( IntPtr native, int w, int h )
	{
		lock ( gate )
		{
			if ( native != surface ) return;

			width = Math.Max( w, 1 );
			height = Math.Max( h, 1 );

			Monitor.PulseAll( gate );
		}
	}

	//The host is taking its surface back, from the host's thread. Waits for the engine to
	//have stopped drawing into it - the host may not let it go before - for as long as wait.
	internal static bool SurfaceGone( IntPtr native, TimeSpan wait )
	{
		lock ( gate )
		{
			if ( native != surface ) return true;

			var gone = generation;
			surface = IntPtr.Zero;

			Monitor.PulseAll( gate );

			var until = DateTime.UtcNow + wait;

			while ( drawing == gone )
			{
				var left = until - DateTime.UtcNow;
				if ( left <= TimeSpan.Zero ) break;

				Monitor.Wait( gate, left );
			}

			if ( drawing == gone )
			{
				log.Warning( "the engine did not let the window go in time - it was taken anyway" );
				return false;
			}

			return true;
		}
	}

	//The activity left the player's sight, from the host's thread. Waits - for as long as wait - for the game
	//to have been told, so that what it saves is written before the system can decide to end the process.
	internal static void Pause( TimeSpan wait )
	{
		lock ( gate )
		{
			paused = true;
			Monitor.PulseAll( gate );

			if ( !Hosted ) return;

			var until = DateTime.UtcNow + wait;

			while ( !away )
			{
				var left = until - DateTime.UtcNow;
				if ( left <= TimeSpan.Zero ) break;

				Monitor.Wait( gate, left );
			}
		}
	}

	//The activity is back in front of the player, from the host's thread.
	internal static void Resume()
	{
		lock ( gate )
		{
			paused = false;
			Monitor.PulseAll( gate );
		}
	}

	//What covers the edges of the screen - see Screen.SafeInsets. From the host's thread.
	internal static void SetInsets( float left, float top, float right, float bottom )
	{
		lock ( gate )
		{
			insets = new Rinten.UI.Margin( left, top, right, bottom );
			insetsChanged = true;
		}
	}

	//The window the game starts in, on the surface the host gave first. What the host hands the app system as its window source.
	internal static CoreWindow Adopt()
	{
		lock ( gate )
		{
			if ( surface == IntPtr.Zero ) return null;

			window = CoreWindow.Adopt( CoreWindow.Kind.Android, (ulong)surface, 0, width, height );
			held = generation;
			drawing = held;

			return window;
		}
	}

	//At the top of the engine's frame: answers whatever the host has said since the last
	//one, and waits here while there is nothing to draw into or nobody to draw for.
	internal static CoreWindow Hold()
	{
		if ( !Hosted ) return null;

		CoreWindow changed = null;
		var wentAway = false;

		while ( true )
		{
			IntPtr given;
			int givenGeneration, w, h;
			bool isPaused;

			lock ( gate )
			{
				given = surface;
				givenGeneration = generation;
				w = width;
				h = height;
				isPaused = paused;

				if ( insetsChanged )
				{
					insetsChanged = false;
					Screen.SafeInsets = insets;
				}
			}

			//The surface the swapchain is on has been taken back. Nothing more is drawn into it, from now.
			if ( held != 0 && ( given == IntPtr.Zero || givenGeneration != held ) )
			{
				LetGo();
			}

			var drawable = given != IntPtr.Zero && !isPaused;

			if ( drawable )
			{
				if ( held == 0 )
				{
					changed = Take( given, givenGeneration, w, h );
				}
				else if ( window is not null && window.SizeInPixels != (w, h) )
				{
					window.SetSizeInPixels( w, h );
				}

				break;
			}

			if ( !wentAway )
			{
				wentAway = true;
				GoAway();
			}

			if ( Application.WantsExit ) break;

			lock ( gate )
			{
				//Asleep until the host says something - and looked at again every so often, so an exit asked for meanwhile is not waited on for ever.
				if ( surface == given && generation == givenGeneration && paused == isPaused )
					Monitor.Wait( gate, 1000 );
			}
		}

		if ( wentAway && !Application.WantsExit ) ComeBack();

		return changed;
	}

	//Lets go of the swapchain and the window it was on, and says so to the host.
	static void LetGo()
	{
		CoreGameRenderer.LetGoOfWindow();

		CoreInput.Window = IntPtr.Zero;

		window?.Dispose();
		window = null;
		held = 0;

		lock ( gate )
		{
			drawing = 0;
			Monitor.PulseAll( gate );
		}
	}

	//A window on the surface the host has given, for the renderer to make a swapchain on.
	static CoreWindow Take( IntPtr given, int givenGeneration, int w, int h )
	{
		window = CoreWindow.Adopt( CoreWindow.Kind.Android, (ulong)given, 0, w, h );
		held = givenGeneration;

		CoreGameRenderer.Window = window;
		CoreInput.Window = window.Handle;

		lock ( gate )
		{
			drawing = held;
		}

		log.Info( $"a new {w}x{h} surface to draw into" );

		return window;
	}

	//Tells the game it is going into the background, and stops what runs on its own.
	static void GoAway()
	{
		log.Info( "into the background" );

		//Nothing held stays held while away: a finger on the screen when the call came in would be a button down for the rest of the game.
		CoreInput.CancelTouches();
		Engine.InputRouter.OnWindowActive( false );

		Post( static x => x.OnApplicationPause() );

		EngineAudio.Suspend( true );

		lock ( gate )
		{
			away = true;
			Monitor.PulseAll( gate );
		}
	}

	//Tells the game it is back, and starts again what was stopped.
	static void ComeBack()
	{
		lock ( gate )
		{
			away = false;
		}

		//The time away is not a frame: a delta of however long the phone was in a pocket would throw everything in the game that moves by it.
		RealTime.SkipGap();

		EngineAudio.Suspend( false );

		Engine.InputRouter.OnWindowActive( true );

		Post( static x => x.OnApplicationResume() );

		log.Info( "back in front" );
	}

	static void Post( Action<IApplicationEvents> action )
	{
		try
		{
			using ( Rinten.Engine.GlobalContext.GameScope() )
			{
				IApplicationEvents.Post( action );
			}
		}
		catch ( Exception e )
		{
			log.Warning( e, $"the game failed answering the application going away or coming back: {e.Message}" );
		}
	}
}
