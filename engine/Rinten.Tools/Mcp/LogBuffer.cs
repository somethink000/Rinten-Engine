using System;

namespace Editor.Mcp;

//Ring of recent log events for read_console, with sequence numbers so readers can fetch only new ones
internal static class LogBuffer
{
	/// <summary>How many events the ring holds before the oldest go.</summary>
	public const int Capacity = 2000;

	/// <summary>One buffered event and its place in the sequence. Sequences start at 1 and never repeat within an editor session, whether or not the event is still in the ring.</summary>
	public readonly record struct Entry( long Seq, LogEvent Event );

	static readonly object sync = new();
	static readonly Queue<Entry> events = new();
	static long last;
	static bool capturing;

	public static void StartCapture()
	{
		if ( capturing )
			return;

		capturing = true;

		EditorUtility.AddLogger( OnLog );
	}

	static void OnLog( LogEvent e )
	{
		//The ring holds 2000 events from editor start: it must not own what they logged, or a logged component keeps its closed scene alive
		e = e.WithWeakArguments();
		e.Exception = null;
		lock ( sync )
		{
			events.Enqueue( new Entry( ++last, e ) );

			while ( events.Count > Capacity )
			{
				events.Dequeue();
			}
		}
	}

	/// <summary>The sequence number of the newest event, 0 before anything was logged. Take it before doing something and pass it to <see cref="Since"/> afterwards to see only what that did.</summary>
	public static long Last
	{
		get
		{
			lock ( sync )
			{
				return last;
			}
		}
	}

	/// <summary>Forgets everything buffered. Sequence numbers carry on from where they were, so a cursor taken before still means what it meant.</summary>
	public static void Clear()
	{
		lock ( sync )
		{
			events.Clear();
		}
	}

	/// <summary>Everything buffered so far, oldest first.</summary>
	public static Entry[] Snapshot()
	{
		lock ( sync )
		{
			return events.ToArray();
		}
	}

	/// <summary>The lines logged after sequence <paramref name="since"/> as text, one per line, the newest <paramref name="limit"/> of them - for a tool to show what it caused. Null when nothing was.</summary>
	public static string TextSince( long since, int limit = 50 )
	{
		var entries = Since( since, out _ );

		if ( entries.Length == 0 )
			return null;

		var builder = new System.Text.StringBuilder();

		if ( entries.Length > limit )
			builder.AppendLine( $"({entries.Length} console lines, the last {limit}:)" );

		foreach ( var (_, e) in entries.TakeLast( limit ) )
		{
			builder.AppendLine( $"[{e.Logger}] {e.Level}: {e.Message}" );
		}

		return builder.ToString().TrimEnd();
	}

	/// <summary>Every event logged after sequence <paramref name="after"/>, oldest first. <paramref name="dropped"/> is how many of those have already fallen out of the ring - a reader that waited too long is told what it missed rather than shown a gap it can't see.</summary>
	public static Entry[] Since( long after, out long dropped )
	{
		lock ( sync )
		{
			var oldest = events.Count > 0 ? events.Peek().Seq : last + 1;

			dropped = Math.Max( 0, oldest - 1 - Math.Max( after, 0 ) );

			return events.Where( x => x.Seq > after ).ToArray();
		}
	}
}
