using Rinten;
using System.Collections.Concurrent;
using System.Threading;

//Weak lookup of one C# Resource per native handle; recently used ones held strongly for a few seconds
internal static partial class NativeResourceCache
{
	const int ExpirationSeconds = 5;

	//Recently touched resources, stamped with the generation they were last touched in. Each tick drops
	//those untouched for a whole generation, so retention is one to two ExpirationSeconds - enough that
	//something fetched every frame isn't collected and rebuilt in between. Entries are updated in place,
	//so only a newly touched resource allocates.
	static readonly ConcurrentDictionary<long, Retained> retained = new();
	static int generation;

	sealed class Retained
	{
		public object Value;
		public int Generation;
	}

	//We still want a WeakReference cache because we might have a strong reference somewhere to a resource
	//that has been expired from the cache. And we absolutely only want 1 instance of the resource.
	static readonly ConcurrentDictionary<long, WeakReference> WeakTable = new();

	//When LeakTracking is enabled, stores the callstack captured at the time each resource was added
	//to the cache. This prevents WeakTable pruning so every allocation remains visible at shutdown.
	static readonly ConcurrentDictionary<long, string> CallstackTable = new();

	/// <summary>When enabled, disables WeakTable entry pruning and captures allocation callstacks so that resource leaks can be diagnosed at shutdown with full context.</summary>
	[ConVar( "resource_leak_tracking" )]
	public static bool LeakTracking
	{
		get => field;
		set
		{
			if ( value && !field )
			{
				Log.Warning( "resource_leak_tracking enabled — resources allocated before this point will not have callstacks and may not appear in the shutdown leak report." );
			}
			field = value;
		}
	} = false;

	internal static void Add( long key, object value )
	{
		KeepAlive( key, value );

		WeakTable[key] = new WeakReference( value );

		if ( LeakTracking )
		{
			CallstackTable[key] = new System.Diagnostics.StackTrace( skipFrames: 1, fNeedFileInfo: false ).ToString();
		}
	}

	//Hold a strong reference until at least the next swap.
	static void KeepAlive( long key, object value )
	{
		var current = Volatile.Read( ref generation );

		if ( retained.TryGetValue( key, out var entry ) )
		{
			entry.Value = value;
			Volatile.Write( ref entry.Generation, current );

			//Tick may have removed it in between, put it back (no-op when still there)
			retained.TryAdd( key, entry );
			return;
		}

		retained[key] = new Retained { Value = value, Generation = current };
	}

	//Remove a key. Used when a resource is explicitly disposed so that a new instance can be created for the same native pointer.
	internal static void Remove( long key )
	{
		//Explicitly disposed, so drop the strong reference now rather than at the next swap.
		retained.TryRemove( key, out _ );

		WeakTable.TryRemove( key, out _ );
		CallstackTable.TryRemove( key, out _ );
	}

	//Look up without renewing retention, for anything that walks every resource - a plain TryGetValue would touch the whole set and keep all of it alive.
	internal static bool TryPeek<T>( long key, out T value ) where T : class
	{
		value = null;

		if ( WeakTable.TryGetValue( key, out var weakValue ) && weakValue.Target is T target )
		{
			value = target;
			return true;
		}

		return false;
	}

	internal static bool TryGetValue<T>( long key, out T value ) where T : class
	{
		value = null;

		//Read Target once to avoid TOCTOU race with GC.
		if ( WeakTable.TryGetValue( key, out var weakValue ) && weakValue.Target is T target )
		{
			value = target;

			//Touching it renews retention, so anything in active use stays put.
			KeepAlive( key, target );

			return true;
		}

		return false;
	}

	//Expiry has to stop when renewals do. Only a paused game qualifies: nothing ticks, so nothing renews. Everywhere else - playing, editor
	//stopped, menu - still fetches every frame. Was a TimeSince, which froze whenever the editor wasn't playing, so nothing expired.
	static double expiryClock;

	//Expires resources we haven't touched recently, and prunes dead weak entries.
	internal static void Tick()
	{
		expiryClock += Game.IsPaused ? 0 : RealTime.Delta;

		if ( expiryClock < ExpirationSeconds )
			return;

		expiryClock = 0;

		//Anything not touched since the last tick loses its strong reference here.
		var current = Interlocked.Increment( ref generation );
		foreach ( var kvp in retained )
		{
			if ( Volatile.Read( ref kvp.Value.Generation ) < current - 1 && retained.TryRemove( kvp ) )
			{
				//Touched again while we removed it, keep it
				if ( Volatile.Read( ref kvp.Value.Generation ) >= current - 1 )
					retained.TryAdd( kvp.Key, kvp.Value );
			}
		}

		//Prune dead WeakTable entries to prevent unbounded growth from procedural resources.
		//Skipped when leak tracking is enabled so every allocation stays visible until shutdown.
		if ( !LeakTracking )
		{
			foreach ( var kvp in WeakTable )
			{
				if ( kvp.Value.Target is null )
				{
					WeakTable.TryRemove( kvp.Key, out _ );
				}
			}
		}

	}

	//Returns stats about the NativeResourceCache for debug overlays.
	internal static NativeCacheStats GetStats()
	{
		var stats = new NativeCacheStats();

		foreach ( var kvp in WeakTable )
		{
			var target = kvp.Value.Target;
			var alive = target is not null;
			var typeName = alive ? target.GetType().Name : "(dead)";
			stats.Entries.TryGetValue( typeName, out var count );
			stats.Entries[typeName] = count + 1;
		}

		stats.WeakTableTotal = WeakTable.Count;
		stats.MemoryCacheCount = retained.Count;

		return stats;
	}

	internal struct NativeCacheStats
	{
		public Dictionary<string, int> Entries;
		public int WeakTableTotal;
		public int MemoryCacheCount;

		public NativeCacheStats()
		{
			Entries = new();
		}
	}

	//Clear the cache when games are closed etc. ready for a GC.Collect
	internal static void Clear()
	{
		ClearCache();

		//When leak tracking is enabled, preserve the WeakTable and CallstackTable across game resets so
		//that resources allocated before the reset remain visible to HandleShutdownLeaks() at shutdown.
		if ( !LeakTracking ) ClearWeakTable();
	}

	internal static void ClearCache()
	{
		retained.Clear();
	}

	private static void ClearWeakTable()
	{
		WeakTable.Clear();
		CallstackTable.Clear();
	}

	internal static void HandleShutdownLeaks()
	{

		foreach ( var kvp in WeakTable )
		{
			if ( kvp.Value.Target is Resource resource && resource.IsValid() )
			{
				var resourceName = resource.ResourceName;
				ulong resourceId = resource.ResourceIdLong;

				if ( resource is Texture tex )
				{
					resourceName = "RenderTarget";
				}
			}
		}
	}
}
