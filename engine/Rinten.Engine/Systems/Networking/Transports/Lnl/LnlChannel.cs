using LiteNetLib;
using LiteNetLib.Utils;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace Rinten.Network;

//A sealed connection over LiteNetLib, the same class at both ends; it reaches the other end directly or through the relay
internal sealed class LnlChannel : Connection
{
	static readonly Logger log = new( "Udp" );

	//Messages a client may queue before the host's key arrives, and forged packets tolerated before hanging up
	const int MostQueued = 4096;
	const int MostForged = 64;

	//Messages waiting for the main thread before the sender is taken to be flooding
	const int MostReceived = 16384;

	//Op byte and client id in front of a relayed packet
	internal const int RelayHeader = 5;

	readonly LnlTransport transport;
	readonly bool isHost;
	readonly bool owned;

	readonly ConcurrentQueue<byte[]> received = new();
	readonly ConcurrentQueue<(byte[] Data, NetFlags Flags)> waiting = new();

	//Null while a client is still choosing between a punched path and the relay
	NetPeer peer;
	bool relayed;
	uint relayId;
	string relayedAddress;

	WireSession session;
	int forged;
	string endReason;
	bool told;

	//Fingerprint the host's key must have, from a host:port#fingerprint target
	public string Pin { get; init; }

	internal LnlChannel( LnlTransport transport, NetPeer peer, bool isHost, bool owned )
	{
		this.transport = transport;
		this.peer = peer;
		this.isHost = isHost;
		this.owned = owned;
	}

	public override string Address => relayed ? relayedAddress ?? "relay" : peer?.ToString() ?? "connecting";

	public override bool IsHost => isHost;

	//Through the relay this is only the hop to it
	public override float Latency => ( peer?.RoundTripTime ?? 0 ) / 1000.0f;

	internal bool IsGone => endReason is not null;

	internal bool HasPath => peer is not null;

	internal NetPeer Peer => peer;

	internal void Begin( WireSession wire ) => session = wire;

	internal void Gone( string reason ) => endReason ??= reason ?? "disconnected";

	internal void UseDirect( NetPeer direct ) => peer = direct;

	internal void UseRelay( NetPeer relay, uint id = 0, string address = null )
	{
		peer = relay;
		relayed = true;
		relayId = id;
		relayedAddress = address;
	}

	//From LnlTransport.Poll: the host's key once, sealed messages after it
	internal void Arrived( ReadOnlySpan<byte> packet )
	{
		if ( packet.IsEmpty || IsGone ) return;

		if ( packet[0] == WireSession.Plain )
		{
			if ( isHost && session is null ) Agree( packet[1..] );
			else HangUp( 0, "Unexpected plain message" );

			return;
		}

		//Unreliable messages can beat the key; they are dropped
		if ( session is null ) return;

		if ( session.Open( packet ) is { } message )
		{
			if ( received.Count >= MostReceived ) HangUp( 0, "Too many messages" );
			else received.Enqueue( message );
			return;
		}

		if ( ++forged > MostForged ) HangUp( 0, "Too many damaged packets" );
	}

	void Agree( ReadOnlySpan<byte> hostKey )
	{
		if ( Pin is not null && !WireKey.FingerprintOf( hostKey ).StartsWith( Pin, StringComparison.Ordinal ) )
		{
			log.Warning( $"{Address}: the host's key doesn't match the one asked for" );
			HangUp( 0, "Host key mismatch" );
			return;
		}

		var secret = transport.Key.Agree( hostKey );
		if ( secret is null )
		{
			HangUp( 0, "Bad host key" );
			return;
		}

		session = WireSession.Create( secret, transport.Key.Public, hostKey, isServer: false );

		while ( waiting.TryDequeue( out var queued ) ) SendSealed( queued.Data, queued.Flags );
	}

	internal override void InternalSend( byte[] data, NetFlags flags )
	{
		if ( data is null || data.Length == 0 || IsGone ) return;

		if ( session is null || peer is null )
		{
			if ( waiting.Count < MostQueued ) waiting.Enqueue( (data, flags) );
			return;
		}

		SendSealed( data, flags );
	}

	//Per sending thread; LiteNetLib copies what it is given, so one buffer serves every send
	[ThreadStatic] static byte[] scratch;

	void SendSealed( byte[] data, NetFlags flags )
	{
		var needed = RelayHeader + WireSession.Overhead + data.Length;
		if ( scratch is null || scratch.Length < needed ) scratch = new byte[Math.Max( needed, 2048 )];

		//Room for the relay header in front, so a relayed packet needs no second copy
		int length;

		try
		{
			length = session.Seal( data, scratch.AsSpan( RelayHeader ) );
		}
		catch ( ObjectDisposedException )
		{
			return;
		}

		var method = ( flags & NetFlags.Unreliable ) != 0 ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered;

		//Only reliable messages are split into fragments; an unreliable one too big for a packet goes reliably instead
		if ( method == DeliveryMethod.Unreliable && length + RelayHeader > peer.GetMaxSinglePacketSize( DeliveryMethod.Unreliable ) )
			method = DeliveryMethod.ReliableUnordered;

		Transmit( scratch, RelayHeader, length, method );
	}

	//The packet sits at offset in buffer with RelayHeader free bytes before it. The host's end of a relayed channel
	//names the client; the client's end has only the one host.
	internal void Transmit( byte[] buffer, int offset, int length, DeliveryMethod method )
	{
		if ( !relayed )
		{
			peer.Send( buffer.AsSpan( offset, length ), method );
			return;
		}

		var header = isHost ? 1 : RelayHeader;
		var start = offset - header;

		buffer[start] = (byte)RelayProtocol.Op.Data;
		if ( !isHost ) BinaryPrimitives.WriteUInt32LittleEndian( buffer.AsSpan( start + 1 ), relayId );

		peer.Send( buffer.AsSpan( start, length + header ), method );
	}

	internal override void InternalRecv( NetworkSystem.MessageHandler handler )
	{
		//A client's transport is its own; the host's socket polls its shared one
		if ( owned ) transport.Poll();

		while ( received.TryDequeue( out var message ) )
		{
			OnRawPacketReceived( message, handler );
		}

		if ( isHost && IsGone && !told )
		{
			told = true;
			System?.OnServerDisconnection( 0, endReason );
		}
	}

	internal override void InternalClose( int closeCode, string closeReason )
	{
		told = true;
		HangUp( closeCode, closeReason );
	}

	void HangUp( int code, string reason )
	{
		if ( IsGone && session is null ) return;

		Gone( reason );

		if ( relayed && !isHost ) transport.KickRelayed( relayId, reason );
		else if ( peer is { ConnectionState: ConnectionState.Connected or ConnectionState.Outgoing } ) peer.Disconnect( Encoding.UTF8.GetBytes( reason ?? "" ) );

		if ( owned ) transport.Dispose();

		session?.Dispose();
		session = null;
	}

	ConnectionStats stats;
	double statsAt;
	long lastSentBytes, lastSentPackets, lastHeardBytes, lastHeardPackets;

	//A relayed channel at the host reports the whole relay link
	public override ConnectionStats Stats
	{
		get
		{
			var now = RealTime.NowDouble;
			var over = (float)( now - statsAt );
			if ( over < 1.0f || peer is null ) return stats;

			var s = peer.Statistics;
			var sentBytes = s.BytesSent - lastSentBytes;

			stats = new ConnectionStats
			{
				Ping = peer.RoundTripTime,
				OutPacketsPerSecond = ( s.PacketsSent - lastSentPackets ) / over,
				OutBytesPerSecond = sentBytes / over,
				InPacketsPerSecond = ( s.PacketsReceived - lastHeardPackets ) / over,
				InBytesPerSecond = ( s.BytesReceived - lastHeardBytes ) / over,
				SendRateBytesPerSecond = (int)( sentBytes / over ),
				ConnectionQuality = 1.0f - Math.Clamp( s.PacketLossPercent / 100.0f, 0, 1 ),
			};

			lastSentBytes = s.BytesSent;
			lastSentPackets = s.PacketsSent;
			lastHeardBytes = s.BytesReceived;
			lastHeardPackets = s.PacketsReceived;
			statsAt = now;

			return stats;
		}
	}
}
