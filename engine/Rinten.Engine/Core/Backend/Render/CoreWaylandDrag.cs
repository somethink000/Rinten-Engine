using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace Rinten;

//One of our windows carried as a Wayland drag (xdg-toplevel-drag): the window follows the pointer as in a move, and the drag reports which
//of our windows it is over and where it is put down. The one way a Wayland client learns where its window landed - as Chromium drags tabs out
internal static unsafe partial class CoreWaylandDrag
{
	private const string Lib = "libwayland-client.so.0";

	//What the drag offers: a kind only our own windows take, so nobody else's accepts it
	private const string Mime = "application/x-rinten-window";

	private const uint ActionMove = 2;
	private const uint FlagDestroy = 1;

	[LibraryImport( Lib )] private static partial IntPtr wl_display_create_queue( IntPtr display );
	[LibraryImport( Lib )] private static partial IntPtr wl_proxy_create_wrapper( IntPtr proxy );
	[LibraryImport( Lib )] private static partial void wl_proxy_wrapper_destroy( IntPtr wrapper );
	[LibraryImport( Lib )] private static partial void wl_proxy_set_queue( IntPtr proxy, IntPtr queue );
	[LibraryImport( Lib )] private static partial IntPtr wl_proxy_marshal_array_flags( IntPtr proxy, uint opcode, IntPtr iface, uint version, uint flags, long* args );
	[LibraryImport( Lib )] private static partial int wl_proxy_add_listener( IntPtr proxy, IntPtr listener, IntPtr data );
	[LibraryImport( Lib )] private static partial uint wl_proxy_get_version( IntPtr proxy );
	[LibraryImport( Lib )] private static partial int wl_display_roundtrip_queue( IntPtr display, IntPtr queue );
	[LibraryImport( Lib )] private static partial int wl_display_dispatch_queue_pending( IntPtr display, IntPtr queue );
	[LibraryImport( Lib )] private static partial int wl_display_prepare_read_queue( IntPtr display, IntPtr queue );
	[LibraryImport( Lib )] private static partial int wl_display_read_events( IntPtr display );
	[LibraryImport( Lib )] private static partial void wl_display_cancel_read( IntPtr display );
	[LibraryImport( Lib )] private static partial int wl_display_flush( IntPtr display );
	[LibraryImport( Lib )] private static partial int wl_display_get_fd( IntPtr display );

	[LibraryImport( "libc", EntryPoint = "poll" )] private static partial int Poll( PollFd* fds, ulong count, int timeout );

	[StructLayout( LayoutKind.Sequential )]
	private struct PollFd
	{
		public int Fd;
		public short Events;
		public short Returned;
	}

	//libwayland's message and interface records, which the two interfaces it does not ship are written out as
	[StructLayout( LayoutKind.Sequential )]
	private struct Message
	{
		public IntPtr Name;
		public IntPtr Signature;
		public IntPtr Types;
	}

	[StructLayout( LayoutKind.Sequential )]
	private struct Interface
	{
		public IntPtr Name;
		public int Version;
		public int MethodCount;
		public IntPtr Methods;
		public int EventCount;
		public IntPtr Events;
	}

	/// <summary>What the drag said: over a surface of ours at a point, off all of them, dropped there, or over.</summary>
	internal enum Heard { Over, Off, Dropped, Ended }

	internal readonly record struct Report( Heard What, IntPtr Surface, float X, float Y );

	private static bool tried;
	private static IntPtr library, display, queue;
	private static IntPtr seat, pointer, devices, dragManager;
	private static IntPtr pointerInterface, deviceInterface, sourceInterface, dragInterface;
	private static uint seatName, seatVersion, devicesName, devicesVersion, dragManagerName;
	private static uint seatCapabilities;
	private static IntPtr mime;

	//The serial of the last button pressed in one of our windows, which a drag has to be started with
	private static volatile uint pressSerial;

	//The drag under way. Its data device lives only as long as the drag: the compositor talks to a client's newest device, and one
	//kept for good would take the clipboard and every drop away from SDL's
	private static IntPtr device, source, toplevelDrag;
	private static IntPtr offer, overSurface;
	private static float overX, overY;

	//What the reading thread heard, for the main thread to act on
	private static readonly ConcurrentQueue<Report> heard = new();

	/// <summary>Whether this desktop carries windows this way: Wayland, with xdg-toplevel-drag and a pointer to drag with.</summary>
	internal static bool Available => display != IntPtr.Zero && dragManager != IntPtr.Zero && pointer != IntPtr.Zero;

	/// <summary>Binds what a drag needs on SDL's own connection, once; nothing happens where the desktop lacks any of it.</summary>
	internal static void Start( IntPtr sdlDisplay )
	{
		if ( tried || sdlDisplay == IntPtr.Zero ) return;
		tried = true;

		try
		{
			Bind( sdlDisplay );
		}
		catch ( System.Exception e )
		{
			display = IntPtr.Zero;
			new Rinten.Diagnostics.Logger( "Ui" ).Warning( $"window drags: {e.Message}" );
		}
	}

	private static void Bind( IntPtr sdlDisplay )
	{
		if ( !NativeLibrary.TryLoad( Lib, out library ) ) return;

		var seatInterface = Export( "wl_seat_interface" );
		var devicesInterface = Export( "wl_data_device_manager_interface" );
		var registryInterface = Export( "wl_registry_interface" );

		pointerInterface = Export( "wl_pointer_interface" );
		deviceInterface = Export( "wl_data_device_interface" );
		sourceInterface = Export( "wl_data_source_interface" );

		var dragManagerInterface = MakeDragInterfaces();

		mime = Marshal.StringToCoTaskMemUTF8( Mime );

		//Our own queue, read on our own thread, so SDL's queue and ours never wait on each other
		queue = wl_display_create_queue( sdlDisplay );

		var wrapper = wl_proxy_create_wrapper( sdlDisplay );
		wl_proxy_set_queue( wrapper, queue );

		var registry = Request( wrapper, 1, registryInterface, wl_proxy_get_version( wrapper ), 0, 0 );
		wl_proxy_wrapper_destroy( wrapper );

		wl_proxy_add_listener( registry, Listener( (nint)(delegate* unmanaged<IntPtr, IntPtr, uint, byte*, uint, void>)&Global, Nothing ), IntPtr.Zero );
		wl_display_roundtrip_queue( sdlDisplay, queue );

		if ( seatName == 0 || devicesName == 0 || dragManagerName == 0 ) return;

		seatVersion = System.Math.Min( seatVersion, 5u );
		devicesVersion = System.Math.Min( devicesVersion, 3u );

		seat = BindGlobal( registry, seatName, seatInterface, seatVersion );
		wl_proxy_add_listener( seat, Listener( (nint)(delegate* unmanaged<IntPtr, IntPtr, uint, void>)&Capabilities, Nothing ), IntPtr.Zero );

		devices = BindGlobal( registry, devicesName, devicesInterface, devicesVersion );
		dragManager = BindGlobal( registry, dragManagerName, dragManagerInterface, 1 );

		wl_display_roundtrip_queue( sdlDisplay, queue );

		//The pointer is ours only to learn the serial a press was given; SDL's own does everything else
		if ( (seatCapabilities & 1) != 0 )
		{
			pointer = Request( seat, 0, pointerInterface, seatVersion, 0, 0 );

			wl_proxy_add_listener( pointer, Listener( Nothing, Nothing, Nothing, (nint)(delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, void>)&Button,
				Nothing, Nothing, Nothing, Nothing, Nothing ), IntPtr.Zero );
		}

		display = sdlDisplay;

		new Thread( Read ) { IsBackground = true, Name = "Wayland drags" }.Start();
	}

	/// <summary>Carries a window: it follows the pointer, held where it was grabbed, until it is put down. False where it cannot be.</summary>
	internal static bool Carry( IntPtr surface, IntPtr toplevel, int grabX, int grabY )
	{
		if ( !Available || source != IntPtr.Zero || surface == IntPtr.Zero || toplevel == IntPtr.Zero || pressSerial == 0 ) return false;

		device = Request( devices, 1, deviceInterface, devicesVersion, 0, 0, seat );

		wl_proxy_add_listener( device, Listener( (nint)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&Offered,
			(nint)(delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr, int, int, IntPtr, void>)&Entered, (nint)(delegate* unmanaged<IntPtr, IntPtr, void>)&Left,
			(nint)(delegate* unmanaged<IntPtr, IntPtr, uint, int, int, void>)&Moved, (nint)(delegate* unmanaged<IntPtr, IntPtr, void>)&Dropped, Nothing ), IntPtr.Zero );

		source = Request( devices, 0, sourceInterface, devicesVersion, 0, 0 );

		wl_proxy_add_listener( source, Listener( Nothing, Nothing, (nint)(delegate* unmanaged<IntPtr, IntPtr, void>)&Finished,
			Nothing, (nint)(delegate* unmanaged<IntPtr, IntPtr, void>)&Finished, Nothing ), IntPtr.Zero );

		Request( source, 0, IntPtr.Zero, devicesVersion, 0, mime );

		if ( devicesVersion >= 3 ) Request( source, 2, IntPtr.Zero, devicesVersion, 0, (nint)ActionMove );

		toplevelDrag = Request( dragManager, 1, dragInterface, 1, 0, 0, source );
		Request( toplevelDrag, 1, IntPtr.Zero, 1, 0, toplevel, grabX, grabY );

		Request( device, 0, IntPtr.Zero, devicesVersion, 0, source, surface, 0, (nint)pressSerial );

		wl_display_flush( display );

		return true;
	}

	/// <summary>What the drag said since the last call, on the main thread; an ending also lets the drag's objects go.</summary>
	internal static bool Next( out Report report )
	{
		if ( !heard.TryDequeue( out report ) ) return false;

		if ( report.What == Heard.Ended && source != IntPtr.Zero )
		{
			Request( toplevelDrag, 0, IntPtr.Zero, 1, FlagDestroy );
			Request( source, 1, IntPtr.Zero, devicesVersion, FlagDestroy );

			//Released, so the device the compositor talks to is SDL's again
			if ( devicesVersion >= 2 ) Request( device, 1, IntPtr.Zero, devicesVersion, FlagDestroy );

			source = toplevelDrag = device = IntPtr.Zero;
		}

		return true;
	}

	//Reads the connection for our queue while SDL reads it for its own - libwayland's way for two readers to share one socket
	private static void Read()
	{
		var fd = new PollFd { Fd = wl_display_get_fd( display ), Events = 1 };

		while ( true )
		{
			while ( wl_display_prepare_read_queue( display, queue ) != 0 )
				wl_display_dispatch_queue_pending( display, queue );

			wl_display_flush( display );

			fd.Returned = 0;

			if ( Poll( &fd, 1, 100 ) > 0 ) wl_display_read_events( display );
			else wl_display_cancel_read( display );

			wl_display_dispatch_queue_pending( display, queue );
		}
	}

	private static IntPtr Export( string name ) => NativeLibrary.GetExport( library, name );

	private static IntPtr BindGlobal( IntPtr registry, uint name, IntPtr iface, uint version )
		=> Request( registry, 0, iface, version, 0, (nint)name, *(IntPtr*)iface, (nint)version, 0 );

	//One request; wl_argument is eight bytes, whatever it holds
	private static IntPtr Request( IntPtr proxy, uint opcode, IntPtr iface, uint version, uint flags, params nint[] args )
	{
		var values = stackalloc long[System.Math.Max( args.Length, 1 )];

		for ( var i = 0; i < args.Length; i++ ) values[i] = args[i];

		return wl_proxy_marshal_array_flags( proxy, opcode, iface, version, flags, values );
	}

	//A listener: the handlers in event order, kept for as long as the process
	private static IntPtr Listener( params nint[] handlers )
	{
		var table = (nint*)NativeMemory.Alloc( (nuint)( handlers.Length * sizeof( nint ) ) );

		for ( var i = 0; i < handlers.Length; i++ ) table[i] = handlers[i];

		return (IntPtr)table;
	}

	private static IntPtr Text( string text ) => Marshal.StringToCoTaskMemUTF8( text );

	//xdg_toplevel_drag_manager_v1 and xdg_toplevel_drag_v1, as wayland-scanner would write them; hands back the manager's
	private static IntPtr MakeDragInterfaces()
	{
		var drag = (Interface*)NativeMemory.AllocZeroed( (nuint)sizeof( Interface ) );
		var manager = (Interface*)NativeMemory.AllocZeroed( (nuint)sizeof( Interface ) );

		var none = (IntPtr*)NativeMemory.AllocZeroed( (nuint)( 3 * sizeof( IntPtr ) ) );

		var getTypes = (IntPtr*)NativeMemory.AllocZeroed( (nuint)( 2 * sizeof( IntPtr ) ) );
		getTypes[0] = (IntPtr)drag;
		getTypes[1] = sourceInterface;

		var dragMethods = (Message*)NativeMemory.AllocZeroed( (nuint)( 2 * sizeof( Message ) ) );
		dragMethods[0] = new Message { Name = Text( "destroy" ), Signature = Text( "" ), Types = (IntPtr)none };
		dragMethods[1] = new Message { Name = Text( "attach" ), Signature = Text( "oii" ), Types = (IntPtr)none };

		var managerMethods = (Message*)NativeMemory.AllocZeroed( (nuint)( 2 * sizeof( Message ) ) );
		managerMethods[0] = new Message { Name = Text( "destroy" ), Signature = Text( "" ), Types = (IntPtr)none };
		managerMethods[1] = new Message { Name = Text( "get_xdg_toplevel_drag" ), Signature = Text( "no" ), Types = (IntPtr)getTypes };

		*drag = new Interface { Name = Text( "xdg_toplevel_drag_v1" ), Version = 1, MethodCount = 2, Methods = (IntPtr)dragMethods };
		*manager = new Interface { Name = Text( "xdg_toplevel_drag_manager_v1" ), Version = 1, MethodCount = 2, Methods = (IntPtr)managerMethods };

		dragInterface = (IntPtr)drag;

		return (IntPtr)manager;
	}

	//-- events: on the reading thread, or the main one during the roundtrips above --------------

	private static readonly nint Nothing = (nint)(delegate* unmanaged<IntPtr, IntPtr, void>)&Ignore;

	[UnmanagedCallersOnly]
	private static void Ignore( IntPtr data, IntPtr proxy ) { }

	[UnmanagedCallersOnly]
	private static void Global( IntPtr data, IntPtr registry, uint name, byte* iface, uint version )
	{
		var called = Marshal.PtrToStringUTF8( (IntPtr)iface );

		if ( called == "wl_seat" && seatName == 0 ) { seatName = name; seatVersion = version; }
		else if ( called == "wl_data_device_manager" ) { devicesName = name; devicesVersion = version; }
		else if ( called == "xdg_toplevel_drag_manager_v1" ) dragManagerName = name;
	}

	[UnmanagedCallersOnly]
	private static void Capabilities( IntPtr data, IntPtr seat, uint capabilities ) => seatCapabilities = capabilities;

	[UnmanagedCallersOnly]
	private static void Button( IntPtr data, IntPtr pointer, uint serial, uint time, uint button, uint state )
	{
		if ( state == 1 ) pressSerial = serial;
	}

	[UnmanagedCallersOnly]
	private static void Offered( IntPtr data, IntPtr device, IntPtr id ) => offer = id;

	//Over one of our surfaces: our own drag is taken, as a move
	[UnmanagedCallersOnly]
	private static void Entered( IntPtr data, IntPtr device, uint serial, IntPtr surface, int x, int y, IntPtr id )
	{
		offer = id;
		overSurface = surface;

		if ( id != IntPtr.Zero )
		{
			Request( id, 0, IntPtr.Zero, devicesVersion, 0, (nint)serial, mime );

			if ( devicesVersion >= 3 ) Request( id, 4, IntPtr.Zero, devicesVersion, 0, (nint)ActionMove, (nint)ActionMove );
		}

		Over( x, y );
	}

	[UnmanagedCallersOnly]
	private static void Moved( IntPtr data, IntPtr device, uint time, int x, int y ) => Over( x, y );

	[UnmanagedCallersOnly]
	private static void Left( IntPtr data, IntPtr device )
	{
		Forget();
		overSurface = IntPtr.Zero;

		heard.Enqueue( new Report( Heard.Off, IntPtr.Zero, 0, 0 ) );
	}

	[UnmanagedCallersOnly]
	private static void Dropped( IntPtr data, IntPtr device )
	{
		heard.Enqueue( new Report( Heard.Dropped, overSurface, overX, overY ) );

		//Taken, which is what lets the source hear the drag is done
		if ( offer != IntPtr.Zero && devicesVersion >= 3 ) Request( offer, 3, IntPtr.Zero, devicesVersion, 0 );

		Forget();
	}

	[UnmanagedCallersOnly]
	private static void Finished( IntPtr data, IntPtr source ) => heard.Enqueue( new Report( Heard.Ended, IntPtr.Zero, 0, 0 ) );

	//Fixed point, eight bits of fraction
	private static void Over( int x, int y )
	{
		overX = x / 256.0f;
		overY = y / 256.0f;

		heard.Enqueue( new Report( Heard.Over, overSurface, overX, overY ) );
	}

	private static void Forget()
	{
		if ( offer != IntPtr.Zero ) Request( offer, 2, IntPtr.Zero, devicesVersion, FlagDestroy );

		offer = IntPtr.Zero;
	}
}
