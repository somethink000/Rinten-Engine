using System.Collections.Concurrent;
using System.Net;

namespace Rinten.Network;

//One end of a connection: numbered packets with acks, reliable resends and message fragmentation
internal sealed class UdpPeer
{
	/// <summary>How long without hearing anything before it is gone.</summary>
	public const float Timeout = 10.0f;

	//And how often to say nothing, so the other end knows we are here.
	private const float PingEvery = 1.0f;

	//How long to wait for an acknowledgement before saying it again.
	private const float ResendAfter = 0.1f;

	public UdpPeer( IPEndPoint address, uint connection )
	{
		Address = address;
		Connection = connection;

		lastHeard = RealTime.Now;
		counted = lastHeard;
	}

	public IPEndPoint Address { get; }

	/// <summary>What this connection is called, as far as both ends agree.</summary>
	public uint Connection { get; set; }

	/// <summary>Whether the far end has stopped answering.</summary>
	public bool TimedOut => RealTime.Now - lastHeard > Timeout;

	/// <summary>How long a round trip is taking, in seconds.</summary>
	public float Latency { get; private set; }

	/// <summary>What this connection is doing, as the overlays and the server browser want it.</summary>
	public ConnectionStats Stats { get; private set; }

	private int sentPackets;
	private int sentBytes;
	private int heardPackets;
	private int heardBytes;

	private double counted;

	/// <summary>A packet went out.</summary>
	public void Sent( int bytes )
	{
		sentPackets++;
		sentBytes += bytes;
	}

	//And one came in.
	private void Heard( int bytes )
	{
		heardPackets++;
		heardBytes += bytes;
	}

	private void Count()
	{
		var now = RealTime.Now;
		var over = (float)( now - counted );

		if ( over < 1.0f ) return;

		//Inbound delivery ratio, counted against packets actually sent so far, not a full 32
		var span = Math.Min( (int)(ushort)( incoming - first ), 32 );
		var seen = span >= 32 ? incomingBits : incomingBits & ( ( 1u << span ) - 1 );

		var arrived = System.Numerics.BitOperations.PopCount( seen ) + ( heardAnything ? 1 : 0 );
		var could = span + 1;

		Stats = new ConnectionStats
		{
			Ping = (int)( Latency * 1000.0f ),
			OutPacketsPerSecond = sentPackets / over,
			OutBytesPerSecond = sentBytes / over,
			InPacketsPerSecond = heardPackets / over,
			InBytesPerSecond = heardBytes / over,
			SendRateBytesPerSecond = (int)( sentBytes / over ),
			ConnectionQuality = arrived / (float)could,
		};

		sentPackets = 0;
		sentBytes = 0;
		heardPackets = 0;
		heardBytes = 0;

		counted = now;
	}

	private double lastHeard;
	private double lastSent;

	private ushort outgoing;
	private ushort incoming;
	private uint incomingBits;

	//Whether anything has arrived at all.
	private bool heardAnything;

	//The first number heard, so a young connection can be told from a lossy one.
	private ushort first;

	private ushort nextMessage;

	//Reliable messages sent and not yet acknowledged.
	private readonly Dictionary<ushort, Pending> pending = new();

	//Which packet carried which reliable piece, so an ack can clear it.
	private readonly Dictionary<ushort, (ushort Message, ushort Fragment)> carried = new();

	private sealed class Pending
	{
		public byte[][] Fragments;
		public bool[] Acked;
		public double[] SentAt;
	}

	//Reliable messages that have arrived and are waiting their turn.
	private readonly Dictionary<ushort, byte[]> ordered = new();

	private ushort nextExpected;

	//Pieces of messages still missing pieces.
	private readonly Dictionary<ushort, Assembly> assembling = new();

	private sealed class Assembly
	{
		public byte[][] Fragments;
		public int Have;
		public bool Reliable;
		public double Started;
	}

	//Drop half-assembled messages after this; only unreliable ones can stall with a lost piece
	private const float GiveUpAfter = 5.0f;

	/// <summary>Whole messages, in the order they should be handed over.</summary>
	public readonly ConcurrentQueue<byte[]> Received = new();

	/// <summary>What wants sending, filled by whoever is holding the socket.</summary>
	public readonly ConcurrentQueue<byte[]> Outgoing = new();

	/// <summary>Put a message into packets and hand them over to be sent.</summary>
	public void Send( byte[] message, bool reliable )
	{
		if ( message is null || message.Length == 0 ) return;

		var payload = UdpProtocol.MaxPayload;
		var fragments = Math.Max( ( message.Length + payload - 1 ) / payload, 1 );

		var id = nextMessage++;

		var pieces = new byte[fragments][];

		for ( var fragment = 0; fragment < fragments; fragment++ )
		{
			var from = fragment * payload;
			var size = Math.Min( payload, message.Length - from );

			var packet = new byte[UdpProtocol.HeaderSize + size];

			var header = new UdpProtocol.Header
			{
				Kind = UdpProtocol.Kind.Payload,
				Connection = Connection,
				Flags = ( reliable ? UdpProtocol.Flags.Reliable : UdpProtocol.Flags.None )
					| ( fragments > 1 ? UdpProtocol.Flags.Fragment : UdpProtocol.Flags.None ),
				Message = id,
				Fragment = (ushort)fragment,
				Fragments = (ushort)fragments,
			};

			UdpProtocol.Write( packet, header );
			message.AsSpan( from, size ).CopyTo( packet.AsSpan( UdpProtocol.HeaderSize ) );

			pieces[fragment] = packet;
		}

		if ( reliable )
		{
			//Kept until it is acknowledged. What goes out now is the first attempt; the rest are made by Tick when nothing comes back.
			pending[id] = new Pending
			{
				Fragments = pieces,
				Acked = new bool[fragments],
				SentAt = new double[fragments],
			};
		}

		for ( var fragment = 0; fragment < fragments; fragment++ )
		{
			Stamp( pieces[fragment], id, (ushort)fragment, reliable );
			Outgoing.Enqueue( pieces[fragment] );
		}
	}

	//Number a packet and put this end's acknowledgements on it, just before it goes out.
	private void Stamp( byte[] packet, ushort message, ushort fragment, bool reliable )
	{
		UdpProtocol.Read( packet, out var header );

		header.Connection = Connection;
		header.Sequence = outgoing++;
		header.Ack = incoming;
		header.AckBits = incomingBits;

		UdpProtocol.Write( packet, header );

		if ( !reliable ) return;

		carried[header.Sequence] = (message, fragment);

		if ( pending.TryGetValue( message, out var waiting ) && fragment < waiting.SentAt.Length )
		{
			waiting.SentAt[fragment] = RealTime.Now;
		}
	}

	/// <summary>A packet with nothing in it, to say we are still here.</summary>
	public byte[] MakePing()
	{
		var packet = new byte[UdpProtocol.HeaderSize];

		UdpProtocol.Write( packet, new UdpProtocol.Header
		{
			Kind = UdpProtocol.Kind.Ping,
			Connection = Connection,
			Sequence = outgoing++,
			Ack = incoming,
			AckBits = incomingBits,
		} );

		lastSent = RealTime.Now;

		return packet;
	}

	/// <summary>What just arrived from the far end.</summary>
	public void Receive( in UdpProtocol.Header header, ReadOnlySpan<byte> payload )
	{
		lastHeard = RealTime.Now;

		Heard( UdpProtocol.HeaderSize + payload.Length );

		Remember( header.Sequence );
		Acknowledge( header.Ack, header.AckBits );

		if ( header.Kind != UdpProtocol.Kind.Payload ) return;

		var reliable = ( ( header.Flags & UdpProtocol.Flags.Reliable ) == UdpProtocol.Flags.Reliable );

		//Acknowledgements ride on whatever goes back the other way, and an end that has nothing to say would hold the other one up
		//until it did - which is every resend timer's worth of packets, for nothing. So a reliable packet is a reason to say something.
		if ( reliable ) ackDue = true;

		//One packet, one message: the common case, and worth not putting through the reassembly.
		if ( header.Fragments <= 1 )
		{
			Deliver( header.Message, payload.ToArray(), reliable );
			return;
		}

		if ( !assembling.TryGetValue( header.Message, out var assembly ) )
		{
			assembly = new Assembly
			{
				Fragments = new byte[header.Fragments][],
				Reliable = reliable,
				Started = RealTime.Now,
			};

			assembling[header.Message] = assembly;
		}

		if ( header.Fragment >= assembly.Fragments.Length ) return;
		if ( assembly.Fragments[header.Fragment] is not null ) return;

		assembly.Fragments[header.Fragment] = payload.ToArray();
		assembly.Have++;

		if ( assembly.Have < assembly.Fragments.Length ) return;

		var size = 0;
		foreach ( var fragment in assembly.Fragments ) size += fragment.Length;

		var message = new byte[size];
		var at = 0;

		foreach ( var fragment in assembly.Fragments )
		{
			fragment.CopyTo( message.AsSpan( at ) );
			at += fragment.Length;
		}

		assembling.Remove( header.Message );

		Deliver( header.Message, message, assembly.Reliable );
	}

	//Hand a whole message over, in order if it was sent in order. Unreliable messages go straight through: something
	//sent unreliably is wanted now or not at all, and holding one back to put it in order is the worst of both.
	private void Deliver( ushort message, byte[] data, bool reliable )
	{
		if ( !reliable )
		{
			Received.Enqueue( data );
			return;
		}

		//Already had, and said again because our acknowledgement went missing.
		if ( UdpProtocol.Newer( nextExpected, message ) || nextExpected == (ushort)( message + 1 ) ) return;

		ordered[message] = data;

		while ( ordered.Remove( nextExpected, out var next ) )
		{
			Received.Enqueue( next );
			nextExpected++;
		}
	}

	//Note that a packet arrived, for the acknowledgements we send.
	private void Remember( ushort sequence )
	{
		//Nothing has been seen yet, so there is nothing behind this to have missed. Without this the first packet shifts a bit in that
		//says the packet before it arrived - and the far end stops waiting for the one thing it should have been told to send again.
		if ( !heardAnything )
		{
			heardAnything = true;

			incoming = sequence;
			incomingBits = 0;
			first = sequence;

			return;
		}

		if ( UdpProtocol.Newer( sequence, incoming ) )
		{
			var moved = (ushort)( sequence - incoming );

			incomingBits = moved >= 32 ? 0 : ( incomingBits << moved ) | ( 1u << ( moved - 1 ) );
			incoming = sequence;

			return;
		}

		var behind = (ushort)( incoming - sequence );
		if ( behind is > 0 and <= 32 ) incomingBits |= 1u << ( behind - 1 );
	}

	//What the far end says it has seen.
	private void Acknowledge( ushort ack, uint bits )
	{
		Clear( ack );

		for ( var bit = 0; bit < 32; bit++ )
		{
			if ( ( bits & ( 1u << bit ) ) == 0 ) continue;

			Clear( (ushort)( ack - bit - 1 ) );
		}
	}

	private void Clear( ushort sequence )
	{
		if ( !carried.Remove( sequence, out var piece ) ) return;

		if ( !pending.TryGetValue( piece.Message, out var waiting ) ) return;
		if ( piece.Fragment >= waiting.Acked.Length ) return;

		waiting.Acked[piece.Fragment] = true;

		//The round trip, measured off whatever came back last. Smoothed, because one late packet is not a slower connection.
		var trip = (float)( RealTime.Now - waiting.SentAt[piece.Fragment] );
		if ( trip is > 0.0f and < 5.0f ) Latency = Latency <= 0.0f ? trip : MathX.Lerp( Latency, trip, 0.1f );

		foreach ( var acked in waiting.Acked )
		{
			if ( !acked ) return;
		}

		pending.Remove( piece.Message );
	}

	//Say again whatever has not been acknowledged, and say something if nothing has been said for a while.
	private bool ackDue;

	public void Tick()
	{
		var now = RealTime.Now;

		Count();

		if ( ackDue )
		{
			ackDue = false;
			Outgoing.Enqueue( MakePing() );
		}

		foreach ( var (message, waiting) in pending )
		{
			for ( var fragment = 0; fragment < waiting.Fragments.Length; fragment++ )
			{
				if ( waiting.Acked[fragment] ) continue;
				if ( now - waiting.SentAt[fragment] < ResendAfter ) continue;

				Stamp( waiting.Fragments[fragment], message, (ushort)fragment, reliable: true );
				Outgoing.Enqueue( waiting.Fragments[fragment] );

				lastSent = now;
			}
		}

		Forget( now );

		if ( now - lastSent < PingEvery ) return;

		Outgoing.Enqueue( MakePing() );
	}

	//Give up on messages that are never going to be whole.
	private void Forget( double now )
	{
		if ( assembling.Count == 0 ) return;

		foreach ( var (message, assembly) in assembling )
		{
			if ( assembly.Reliable ) continue;
			if ( now - assembly.Started < GiveUpAfter ) continue;

			assembling.Remove( message );
		}
	}

	/// <summary>How much is still waiting to be acknowledged.</summary>
	public int Unacknowledged => pending.Count;
}
