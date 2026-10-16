using System.Runtime.InteropServices;

namespace Rinten;

//One of our windows carried as a Wayland drag (xdg-toplevel-drag), the way Chromium drags tabs out: the window follows the pointer, and
//SDL's data device reports where it is over and put down as drop events. A compositor serves one data device per client, so it is SDL's
internal static unsafe partial class CoreWaylandDrag
{
	private const string Lib = "libwayland-client.so.0";

	/// <summary>What the drag offers; our SDL build takes it as a carried window and reports its drop as text saying this.</summary>
	internal const string Mime = "application/x-sdl3-window";

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
	[LibraryImport( Lib )] private static partial int wl_display_flush( IntPtr display );

	[LibraryImport( "libc", EntryPoint = "close" )] private static partial int Close( int fd );

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

	private static bool tried;
	private static IntPtr library, display, queue, device;
	private static IntPtr seat, pointer, devices, dragManager;
	private static IntPtr pointerInterface, sourceInterface, dragInterface;
	private static uint seatName, seatVersion, devicesName, devicesVersion, dragManagerName;
	private static IntPtr mime;

	//The serial of the last button pressed in one of our windows, which a drag has to be started with
	private static uint pressSerial;

	//The drag under way, and whether it has finished since the last Ended
	private static IntPtr source, toplevelDrag;
	private static bool finished;

	/// <summary>Whether this desktop carries windows this way: Wayland, with xdg-toplevel-drag, SDL's data device and a pointer.</summary>
	internal static bool Available => display != IntPtr.Zero && dragManager != IntPtr.Zero && pointer != IntPtr.Zero;

	/// <summary>Binds what a drag needs on SDL's own connection, once; nothing happens where the desktop lacks any of it.</summary>
	internal static void Start( IntPtr sdlDisplay, IntPtr sdlDevice )
	{
		if ( tried || sdlDisplay == IntPtr.Zero || sdlDevice == IntPtr.Zero ) return;
		tried = true;

		try
		{
			Bind( sdlDisplay, sdlDevice );
		}
		catch ( System.Exception e )
		{
			display = IntPtr.Zero;
			new Rinten.Diagnostics.Logger( "Ui" ).Warning( $"window drags: {e.Message}" );
		}
	}

	private static void Bind( IntPtr sdlDisplay, IntPtr sdlDevice )
	{
		if ( !NativeLibrary.TryLoad( Lib, out library ) ) return;

		var seatInterface = Export( "wl_seat_interface" );
		var devicesInterface = Export( "wl_data_device_manager_interface" );
		var registryInterface = Export( "wl_registry_interface" );

		pointerInterface = Export( "wl_pointer_interface" );
		sourceInterface = Export( "wl_data_source_interface" );

		var dragManagerInterface = MakeDragInterfaces();

		mime = Marshal.StringToCoTaskMemUTF8( Mime );

		//Our own queue, read by whoever reads the connection and dispatched on the main thread by Ended
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

		display = sdlDisplay;
		device = sdlDevice;

		wl_display_roundtrip_queue( sdlDisplay, queue );
	}

	/// <summary>Carries a window: it follows the pointer, held where it was grabbed, until it is put down. False where it cannot be.</summary>
	internal static bool Carry( IntPtr surface, IntPtr toplevel, int grabX, int grabY )
	{
		if ( !Available || source != IntPtr.Zero || surface == IntPtr.Zero || toplevel == IntPtr.Zero ) return false;

		//The press this carry follows may still be waiting in our queue
		wl_display_dispatch_queue_pending( display, queue );

		if ( pressSerial == 0 ) return false;

		source = Request( devices, 0, sourceInterface, devicesVersion, 0, 0 );

		wl_proxy_add_listener( source, Listener( Nothing, (nint)(delegate* unmanaged<IntPtr, IntPtr, byte*, int, void>)&Send,
			(nint)(delegate* unmanaged<IntPtr, IntPtr, void>)&Finished, Nothing, (nint)(delegate* unmanaged<IntPtr, IntPtr, void>)&Finished, Nothing ), IntPtr.Zero );

		Request( source, 0, IntPtr.Zero, devicesVersion, 0, mime );

		if ( devicesVersion >= 3 ) Request( source, 2, IntPtr.Zero, devicesVersion, 0, (nint)ActionMove );

		toplevelDrag = Request( dragManager, 1, dragInterface, 1, 0, 0, source );

		//The window is attached once the drag is under way: GNOME only moves a window whose drag it already knows
		Request( device, 0, IntPtr.Zero, wl_proxy_get_version( device ), 0, source, surface, 0, (nint)pressSerial );
		Request( toplevelDrag, 1, IntPtr.Zero, 1, 0, toplevel, grabX, grabY );

		wl_display_flush( display );

		finished = false;
		return true;
	}

	/// <summary>Whether the drag under way finished since the last call; its objects are let go then. On the main thread, each frame.</summary>
	internal static bool Ended()
	{
		if ( display == IntPtr.Zero ) return false;

		wl_display_dispatch_queue_pending( display, queue );

		if ( !finished || source == IntPtr.Zero ) return false;

		Request( toplevelDrag, 0, IntPtr.Zero, 1, FlagDestroy );
		Request( source, 1, IntPtr.Zero, devicesVersion, FlagDestroy );

		source = toplevelDrag = IntPtr.Zero;
		finished = false;

		return true;
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

	//Events, all on the main thread: in the roundtrips above or in Ended

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

	//The pointer is ours only to learn the serial a press was given; SDL's own does everything else. It comes and goes with the mouse
	[UnmanagedCallersOnly]
	private static void Capabilities( IntPtr data, IntPtr seat, uint capabilities )
	{
		var has = (capabilities & 1) != 0;

		if ( has && pointer == IntPtr.Zero )
		{
			pointer = Request( seat, 0, pointerInterface, seatVersion, 0, 0 );

			wl_proxy_add_listener( pointer, Listener( Nothing, Nothing, Nothing, (nint)(delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, void>)&Button,
				Nothing, Nothing, Nothing, Nothing, Nothing ), IntPtr.Zero );
		}
		else if ( !has && pointer != IntPtr.Zero )
		{
			if ( seatVersion >= 3 ) Request( pointer, 1, IntPtr.Zero, seatVersion, FlagDestroy );

			pointer = IntPtr.Zero;
			pressSerial = 0;
		}
	}

	[UnmanagedCallersOnly]
	private static void Button( IntPtr data, IntPtr pointer, uint serial, uint time, uint button, uint state )
	{
		if ( state == 1 ) pressSerial = serial;
	}

	//Nothing is read from a carried window; the descriptor is only closed
	[UnmanagedCallersOnly]
	private static void Send( IntPtr data, IntPtr source, byte* type, int fd ) => Close( fd );

	[UnmanagedCallersOnly]
	private static void Finished( IntPtr data, IntPtr source ) => finished = true;
}
