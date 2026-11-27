using LiteNetLib;
using LiteNetLib.Utils;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Rinten.Network;

//One LiteNetLib manager: a host's listen socket, or a client's one connection. Events run on whoever calls Poll.
internal sealed partial class LnlTransport : INetEventListener, IDisposable
{
	static readonly Logger log = new( "Udp" );

	const string Magic = "RNTN";
	const byte Version = 2;

	//Peers one address may hold at once, so a single machine can't fill the server
	const int MostPerAddress = 8;
	const int MostPeers = 512;

	readonly NetManager manager;
	readonly bool isServer;
	readonly object pollLock = new();

	public WireKey Key { get; } = new();

	public Action<LnlChannel> OnConnected;
	public Action<LnlChannel> OnDisconnected;

	public int Port => manager.LocalPort;

	LnlTransport( bool isServer )
	{
		this.isServer = isServer;

		manager = new NetManager( this )
		{
			IPv6Enabled = true,
			AutoRecycle = true,
			EnableStatistics = true,
			DisconnectTimeout = 10000,
			ChannelsCount = 1,
			NatPunchEnabled = true,
		};

		manager.NatPunchModule.Init( this );
	}

	//Throws where the port can't be had
	public static LnlTransport Listen( int port )
	{
		var transport = new LnlTransport( true );

		if ( !transport.manager.Start( port ) )
		{
			transport.Dispose();
			throw new SocketException( (int)SocketError.AddressAlreadyInUse );
		}

		log.Info( $"listening on udp {transport.Port}, host key {transport.Key.Fingerprint[..16]}" );
		return transport;
	}

	//A connection to host:port, optionally pinned to the host key fingerprint after a '#'
	public static LnlChannel Connect( string target )
	{
		var (address, pin) = Parse( target, Networking.Port );

		var transport = StartClient();

		log.Info( $"connecting to {address}" );

		var peer = transport.manager.Connect( address, transport.ConnectRequest() );
		var channel = new LnlChannel( transport, peer, isHost: true, owned: true ) { Pin = pin };
		peer.Tag = channel;

		return channel;
	}

	static LnlTransport StartClient()
	{
		var transport = new LnlTransport( false );
		if ( !transport.manager.Start() ) throw new SocketException( (int)SocketError.AddressNotAvailable );
		return transport;
	}

	NetDataWriter ConnectRequest()
	{
		var request = new NetDataWriter();
		request.Put( Magic );
		request.Put( Version );
		request.Put( Application.GameIdent ?? "" );
		request.PutBytesWithLength( Key.Public );
		return request;
	}

	//host[:port][#pin], [v6]:port or a name; throws where a name doesn't resolve
	public static (IPEndPoint Address, string Pin) Parse( string target, int defaultPort )
	{
		var wanted = ( target ?? "" ).Trim();
		if ( wanted.StartsWith( "udp://", StringComparison.OrdinalIgnoreCase ) ) wanted = wanted[6..];

		string pin = null;
		var hash = wanted.IndexOf( '#' );
		if ( hash >= 0 )
		{
			pin = wanted[( hash + 1 )..].Trim().ToLowerInvariant();
			wanted = wanted[..hash];
		}

		var port = defaultPort;
		var host = wanted;

		//A bare v6 address has more than one colon and no port
		if ( wanted.StartsWith( '[' ) && wanted.IndexOf( ']' ) is var close and > 0 )
		{
			host = wanted[1..close];
			if ( close + 1 < wanted.Length && wanted[close + 1] == ':' ) int.TryParse( wanted[( close + 2 )..], out port );
		}
		else if ( wanted.Count( c => c == ':' ) == 1 )
		{
			var colon = wanted.IndexOf( ':' );
			host = wanted[..colon];
			if ( int.TryParse( wanted[( colon + 1 )..], out var parsed ) ) port = parsed;
		}

		if ( host.Equals( "localhost", StringComparison.OrdinalIgnoreCase ) ) host = "127.0.0.1";

		var ip = IPAddress.TryParse( host, out var parsedIp ) ? parsedIp : Dns.GetHostAddresses( host ).FirstOrDefault();
		if ( ip is null ) throw new ArgumentException( $"Couldn't find '{host}'" );

		return (new IPEndPoint( ip, port ), string.IsNullOrEmpty( pin ) ? null : pin);
	}

	public void Poll()
	{
		lock ( pollLock )
		{
			manager.PollEvents();
			manager.NatPunchModule.PollEvents();
			TickRelay();
		}
	}

	public void Dispose()
	{
		manager.Stop( true );
		Key.Dispose();
	}

	void INetEventListener.OnConnectionRequest( ConnectionRequest request )
	{
		if ( !isServer )
		{
			request.RejectForce();
			return;
		}

		var data = request.Data;

		if ( !data.TryGetString( out var magic ) || magic != Magic || !data.TryGetByte( out var version ) )
		{
			request.RejectForce();
			return;
		}

		if ( Admit( data, version, request.RemoteEndPoint.Address, out var clientKey, out var secret ) is { } refused )
		{
			if ( refused.Length == 0 ) request.RejectForce();
			else request.Reject( Encoding.UTF8.GetBytes( refused ) );
			return;
		}

		var peer = request.Accept();
		var channel = new LnlChannel( this, peer, isHost: false, owned: false );
		peer.Tag = channel;

		Welcome( channel, secret, clientKey );
	}

	//Null to let them in; otherwise why not, empty for a request too broken to answer
	string Admit( NetDataReader data, byte version, IPAddress from, out byte[] clientKey, out byte[] secret )
	{
		clientKey = null;
		secret = null;

		if ( version != Version ) return "Different engine version";

		if ( !data.TryGetString( out var game ) || !string.Equals( game, Application.GameIdent ?? "", StringComparison.OrdinalIgnoreCase ) )
			return "Different game";

		if ( !data.TryGetBytesWithLength( out clientKey ) || clientKey.Length > 256 ) return "";

		var peers = manager.ConnectedPeerList;
		var total = peers.Count + relayedChannels.Count;
		if ( total >= MostPeers || ( from is not null && peers.Count( p => p.Address.Equals( from ) ) >= MostPerAddress ) )
			return "Server is full";

		secret = Key.Agree( clientKey );
		return secret is null ? "" : null;
	}

	//The one plain message: the host's key, first on the ordered channel so everything sealed comes after it
	void Welcome( LnlChannel channel, byte[] secret, byte[] clientKey )
	{
		channel.Begin( WireSession.Create( secret, clientKey, Key.Public, isServer: true ) );

		const int at = LnlChannel.RelayHeader;
		var hello = new byte[at + 1 + Key.Public.Length];
		hello[at] = WireSession.Plain;
		Key.Public.CopyTo( hello, at + 1 );
		channel.Transmit( hello, at, hello.Length - at, DeliveryMethod.ReliableOrdered );

		OnConnected?.Invoke( channel );
	}

	void INetEventListener.OnPeerConnected( NetPeer peer )
	{
		//A punched path came up before the relay was chosen
		if ( peer.Tag is LnlChannel channel && !isServer ) Punched( channel, peer );
	}

	void INetEventListener.OnPeerDisconnected( NetPeer peer, DisconnectInfo info )
	{
		var reason = info.AdditionalData is { AvailableBytes: > 0 and <= 512 } extra
			? Encoding.UTF8.GetString( extra.GetRemainingBytesSpan() )
			: info.Reason.ToString();

		if ( peer == relay )
		{
			RelayLost( peer, reason );
			return;
		}

		if ( peer.Tag is not LnlChannel channel || channel.Peer != peer ) return;

		channel.Gone( reason );
		OnDisconnected?.Invoke( channel );
	}

	void INetEventListener.OnNetworkError( IPEndPoint endPoint, SocketError socketError )
	{
		log.Trace( $"udp error from {endPoint}: {socketError}" );
	}

	void INetEventListener.OnNetworkReceive( NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod )
	{
		if ( peer == relay )
		{
			FromRelay( reader );
			return;
		}

		if ( peer.Tag is LnlChannel channel && channel.Peer == peer )
			channel.Arrived( reader.GetRemainingBytesSpan() );
	}

	void INetEventListener.OnNetworkReceiveUnconnected( IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType ) { }

	void INetEventListener.OnNetworkLatencyUpdate( NetPeer peer, int latency ) { }
}
