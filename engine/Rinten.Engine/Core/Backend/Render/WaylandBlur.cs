using System.Runtime.InteropServices;

namespace Rinten;

//The desktop's own blur behind a window, asked for with ext_background_effect_v1 - the protocol KWin speaks for
//frosted glass on Wayland. A window with no effect is left solid by its owner; one that has it may draw with alpha
//and see the blurred desktop through its own ground.
internal sealed unsafe partial class WaylandBlur : IDisposable
{
	private const string Lib = "libwayland-client.so.0";

	private static readonly Diagnostics.Logger log = new( "Ui" );

	private readonly nint display;
	private readonly Globals globals;
	private nint effect;

	private WaylandBlur( nint display, Globals globals, nint effect )
	{
		this.display = display;
		this.globals = globals;
		this.effect = effect;
	}

	/// <summary>Asks the compositor to blur what is behind this surface, or null where it cannot.</summary>
	public static WaylandBlur Create( nint display, nint surface, int width, int height )
	{
		if ( display == 0 || surface == 0 || !OperatingSystem.IsLinux() ) return null;

		try
		{
			if ( !Globals.Bind( display, out var globals ) ) return null;

			var effect = Request( globals.Manager, GetBackgroundEffect, Interfaces.Effect, [default, Object( surface )] );

			if ( effect == 0 ) return null;

			var made = new WaylandBlur( display, globals, effect );
			made.SetRegion( width, height );

			wl_display_flush( display );

			return made;
		}
		catch ( Exception e ) when ( e is DllNotFoundException or EntryPointNotFoundException )
		{
			log.Info( $"no blur: {e.Message}" );
			return null;
		}
	}

	/// <summary>How much of the surface has the desktop blurred behind it, in surface units. A window that changes size says so again.</summary>
	public void SetRegion( int width, int height )
	{
		if ( effect == 0 || width <= 0 || height <= 0 ) return;

		var region = Request( globals.Compositor, CompositorCreateRegion, Interfaces.Region, [default] );

		if ( region == 0 ) return;

		Request( region, RegionAdd, 0, [Int( 0 ), Int( 0 ), Int( width ), Int( height )] );
		Request( effect, SetBlurRegion, 0, [Object( region )] );

		Destroy( region, RegionDestroy );

		wl_display_flush( display );
	}

	/// <summary>Takes the effect off the surface. The surface itself outlives this, so it has to be let go of before the window goes.</summary>
	public void Dispose()
	{
		if ( effect != 0 ) Destroy( effect, EffectDestroy );
		effect = 0;

		wl_display_flush( display );
	}

	//-- the protocol, as much of it as this needs ---------------------------

	const uint DisplayGetRegistry = 1;
	const uint RegistryBind = 0;
	const uint CompositorCreateRegion = 1;
	const uint RegionAdd = 1;
	const uint RegionDestroy = 0;
	const uint GetBackgroundEffect = 1;
	const uint SetBlurRegion = 1;
	const uint EffectDestroy = 0;

	const uint MarshalDestroy = 1;

	//One argument of a request, as libwayland takes them: eight bytes, whatever it is.
	[StructLayout( LayoutKind.Explicit, Size = 8 )]
	struct Argument
	{
		[FieldOffset( 0 )] public int I;
		[FieldOffset( 0 )] public uint U;
		[FieldOffset( 0 )] public nint O;
	}

	static Argument Int( int value ) => new() { I = value };
	static Argument Uint( uint value ) => new() { U = value };
	static Argument Object( nint value ) => new() { O = value };

	//Sends a request on a proxy. One with a new object in it hands that object back; its slot in the arguments is filled in by libwayland.
	static nint Request( nint proxy, uint opcode, nint made, ReadOnlySpan<Argument> arguments, uint flags = 0, uint version = 0 )
	{
		if ( version == 0 ) version = wl_proxy_get_version( proxy );

		var copy = stackalloc Argument[Math.Max( 1, arguments.Length )];
		arguments.CopyTo( new Span<Argument>( copy, arguments.Length ) );

		return wl_proxy_marshal_array_flags( proxy, opcode, made, version, flags, copy );
	}

	static void Destroy( nint proxy, uint opcode ) => Request( proxy, opcode, 0, [], MarshalDestroy );

	//libwayland's interface descriptions, loaded lazily so missing libwayland does not crash startup
	static class Interfaces
	{
		static readonly nint library = NativeLibrary.TryLoad( Lib, out var found ) ? found : 0;

		static nint Export( string name )
			=> library != 0 && NativeLibrary.TryGetExport( library, name, out var at ) ? at : 0;

		public static readonly nint Registry = Export( "wl_registry_interface" );
		public static readonly nint Compositor = Export( "wl_compositor_interface" );
		public static readonly nint Surface = Export( "wl_surface_interface" );
		public static readonly nint Region = Export( "wl_region_interface" );

		/// <summary>The name an interface description carries first, which binding says out loud.</summary>
		public static nint NameOf( nint description ) => *(nint*)description;

		//ext_background_effect_manager_v1 and ext_background_effect_surface_v1 are not libwayland's to export - they are a
		//protocol of their own, and the descriptions its scanner would generate are written out here instead.
		public static readonly nint Manager;
		public static readonly nint Effect;

		static Interfaces()
		{
			var manager = (Interface*)NativeMemory.AllocZeroed( (nuint)sizeof( Interface ) );
			var effect = (Interface*)NativeMemory.AllocZeroed( (nuint)sizeof( Interface ) );

			var none = (nint*)NativeMemory.AllocZeroed( (nuint)( 4 * sizeof( nint ) ) );

			var getTypes = (nint*)NativeMemory.AllocZeroed( (nuint)( 2 * sizeof( nint ) ) );
			getTypes[0] = (nint)effect;
			getTypes[1] = Surface;

			var managerRequests = (Message*)NativeMemory.Alloc( (nuint)( 2 * sizeof( Message ) ) );
			managerRequests[0] = new Message { Name = Utf8( "destroy" ), Signature = Utf8( "" ), Types = (nint)none };
			managerRequests[1] = new Message { Name = Utf8( "get_background_effect" ), Signature = Utf8( "no" ), Types = (nint)getTypes };

			var managerEvents = (Message*)NativeMemory.Alloc( (nuint)sizeof( Message ) );
			managerEvents[0] = new Message { Name = Utf8( "capabilities" ), Signature = Utf8( "u" ) };

			var regionTypes = (nint*)NativeMemory.AllocZeroed( (nuint)sizeof( nint ) );
			regionTypes[0] = Region;

			var effectRequests = (Message*)NativeMemory.Alloc( (nuint)( 2 * sizeof( Message ) ) );
			effectRequests[0] = new Message { Name = Utf8( "destroy" ), Signature = Utf8( "" ), Types = (nint)none };
			effectRequests[1] = new Message { Name = Utf8( "set_blur_region" ), Signature = Utf8( "o" ), Types = (nint)regionTypes };

			*manager = new Interface
			{
				Name = Utf8( "ext_background_effect_manager_v1" ),
				Version = 1,
				MethodCount = 2,
				Methods = (nint)managerRequests,
				EventCount = 1,
				Events = (nint)managerEvents,
			};

			*effect = new Interface
			{
				Name = Utf8( "ext_background_effect_surface_v1" ),
				Version = 1,
				MethodCount = 2,
				Methods = (nint)effectRequests,
			};

			Manager = (nint)manager;
			Effect = (nint)effect;
		}

		//A string for the life of the process, which is how long the descriptions are wanted.
		static nint Utf8( string text ) => Marshal.StringToCoTaskMemUTF8( text );
	}

	//The globals the effects are made from, bound once, on a queue of our own. The editor has one display; another one binds again.
	sealed class Globals
	{
		public nint Display;
		public nint Queue;
		public nint Compositor;
		public nint Manager;
		public bool Blur;

		uint compositorName, compositorVersion, managerName;

		static Globals bound;

		public static bool Bind( nint display, out Globals globals )
		{
			globals = bound;

			if ( globals is not null && globals.Display == display )
			{
				//Capabilities are double-buffered state of their own: a compositor that had its blur effect turned
				//off when a window opened may have turned it on since, and the next window should be told so.
				if ( globals.Queue != 0 ) wl_display_dispatch_queue_pending( display, globals.Queue );

				return globals.Manager != 0 && globals.Blur;
			}

			globals = bound = new Globals { Display = display };

			globals.Queue = wl_display_create_queue( display );
			if ( globals.Queue == 0 ) return false;

			//The display's registry, asked for through a wrapper that is on our queue, so what it says is dispatched by our roundtrip and never by SDL's.
			var wrapper = wl_proxy_create_wrapper( display );
			if ( wrapper == 0 ) return false;

			wl_proxy_set_queue( wrapper, globals.Queue );

			var registry = Request( wrapper, DisplayGetRegistry, Interfaces.Registry, [default], 0, 1 );
			wl_proxy_wrapper_destroy( wrapper );

			if ( registry == 0 ) return false;

			try
			{
				wl_proxy_add_listener( registry, (nint)RegistryListener, 0 );

				if ( wl_display_roundtrip_queue( display, globals.Queue ) < 0 ) return false;

				if ( globals.compositorName != 0 )
					globals.Compositor = BindGlobal( registry, globals.compositorName, Interfaces.Compositor, Math.Min( globals.compositorVersion, 4u ) );

				if ( globals.managerName != 0 )
				{
					globals.Manager = BindGlobal( registry, globals.managerName, Interfaces.Manager, 1 );

					if ( globals.Manager != 0 )
					{
						wl_proxy_add_listener( globals.Manager, (nint)ManagerListener, 0 );

						//The blur capability comes when the manager is bound; this is what waits for it.
						wl_display_roundtrip_queue( display, globals.Queue );
					}
				}
			}
			finally
			{
				//Nothing more is wanted of the registry: what was bound outlives it, and a registry nobody dispatches would keep the
				//compositor's later news on our queue for ever. What it says after this goes to a proxy that is gone, which libwayland drops.
				wl_proxy_destroy( registry );
			}

			wl_display_flush( display );

			if ( globals.Manager == 0 )
				log.Info( "no blur: the compositor has no ext_background_effect_manager_v1" );
			else if ( !globals.Blur )
				log.Info( "no blur: the compositor has no blur effect to ask for" );
			else
				log.Info( "blur behind windows: taken (ext_background_effect_v1)" );

			return globals.Manager != 0 && globals.Blur;
		}

		static nint BindGlobal( nint registry, uint name, nint description, uint version )
			=> Request( registry, RegistryBind, description,
				[Uint( name ), Object( Interfaces.NameOf( description ) ), Uint( version ), default], 0, version );

		void Seen( string name, uint id, uint version )
		{
			switch ( name )
			{
				case "wl_compositor": (compositorName, compositorVersion) = (id, version); break;
				case "ext_background_effect_manager_v1": managerName = id; break;
			}
		}

		[UnmanagedCallersOnly]
		static void Global( nint data, nint registry, uint name, byte* named, uint version )
		{
			if ( bound is Globals globals )
				globals.Seen( Marshal.PtrToStringUTF8( (nint)named ), name, version );
		}

		[UnmanagedCallersOnly]
		static void GlobalRemove( nint data, nint registry, uint name ) { }

		[UnmanagedCallersOnly]
		static void Capabilities( nint data, nint manager, uint flags )
		{
			if ( bound is Globals globals )
				globals.Blur = (flags & 1) != 0;
		}

		//The registry's two events and the manager's one, in their order - for the life of the process, which is how long a listener has to outlive what it listens to.
		static readonly nint* RegistryListener = MakeRegistryListener();
		static readonly nint* ManagerListener = MakeManagerListener();

		static nint* MakeRegistryListener()
		{
			var table = (nint*)NativeMemory.Alloc( (nuint)( 2 * sizeof( nint ) ) );
			table[0] = (nint)(delegate* unmanaged<nint, nint, uint, byte*, uint, void>)&Global;
			table[1] = (nint)(delegate* unmanaged<nint, nint, uint, void>)&GlobalRemove;
			return table;
		}

		static nint* MakeManagerListener()
		{
			var table = (nint*)NativeMemory.Alloc( (nuint)sizeof( nint ) );
			table[0] = (nint)(delegate* unmanaged<nint, nint, uint, void>)&Capabilities;
			return table;
		}
	}

	[StructLayout( LayoutKind.Sequential )]
	struct Message
	{
		public nint Name, Signature, Types;
	}

	[StructLayout( LayoutKind.Sequential )]
	struct Interface
	{
		public nint Name;
		public int Version, MethodCount;
		public nint Methods;
		public int EventCount;
		public nint Events;
	}

	[LibraryImport( Lib )] private static partial nint wl_display_create_queue( nint display );
	[LibraryImport( Lib )] private static partial int wl_display_roundtrip_queue( nint display, nint queue );
	[LibraryImport( Lib )] private static partial int wl_display_dispatch_queue_pending( nint display, nint queue );
	[LibraryImport( Lib )] private static partial int wl_display_flush( nint display );
	[LibraryImport( Lib )] private static partial nint wl_proxy_create_wrapper( nint proxy );
	[LibraryImport( Lib )] private static partial void wl_proxy_wrapper_destroy( nint wrapper );
	[LibraryImport( Lib )] private static partial void wl_proxy_set_queue( nint proxy, nint queue );
	[LibraryImport( Lib )] private static partial int wl_proxy_add_listener( nint proxy, nint implementation, nint data );
	[LibraryImport( Lib )] private static partial uint wl_proxy_get_version( nint proxy );
	[LibraryImport( Lib )] private static partial void wl_proxy_destroy( nint proxy );
	[LibraryImport( Lib )] private static partial nint wl_proxy_marshal_array_flags( nint proxy, uint opcode, nint made, uint version, uint flags, Argument* arguments );
}
