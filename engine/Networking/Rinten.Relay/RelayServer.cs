using LiteNetLib;
using LiteNetLib.Utils;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Rinten.Network;
using static Rinten.Network.RelayProtocol;

namespace Rinten.Relay;

//Introduces hosts and clients for NAT punch-through, relays those it can't introduce, and lists public lobbies.
//It never sees game traffic in the clear: the game seals it end to end before it gets here.
sealed class RelayServer : INetEventListener, INatPunchListener
{
	public sealed class Options
	{
		public int Port = DefaultPort;
		public int MostHosts = 2000;

		//Connections one address may hold, and wrong codes it may try a minute
		public int MostPerAddress = 16;
		public int MostMisses = 20;

		//Bytes a second a relayed client may send, and a host may send to all its relayed clients
		public int ClientRate = 256 * 1024;
		public int HostRate = 4 * 1024 * 1024;
	}

	const string CodeLetters = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
	const int CodeLength = 6;
	const double IntroduceFor = 10.0;

	readonly Options options;
	readonly NetManager manager;
	readonly Dictionary<string, Host> hosts = new( StringComparer.Ordinal );
	readonly Dictionary<string, Introduction> introductions = new( StringComparer.Ordinal );
	readonly Dictionary<IPAddress, (int Misses, double Since)> misses = new();
	readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
	uint nextId;

	double Now => clock.Elapsed.TotalSeconds;

	sealed class Bucket
	{
		double tokens;
		double at;

		//False once spent; deep in debt means it should be dropped
		public bool Spend( int bytes, int rate, double now, out bool abusive )
		{
			tokens = Math.Min( tokens + ( now - at ) * rate, rate * 2.0 );
			at = now;
			tokens -= bytes;
			abusive = tokens < -rate * 4.0;
			return tokens >= 0;
		}
	}

	sealed class Host
	{
		public NetPeer Peer;
		public string Game;
		public Lobby Lobby;
		public readonly Dictionary<uint, Client> Clients = new();
		public readonly Bucket Sent = new();
	}

	sealed class Client
	{
		public NetPeer Peer;
		public Host Host;
		public uint Id;
		public string Game;
		public bool Relayed;
		public readonly Bucket Sent = new();
	}

	sealed class Introduction
	{
		public Host Host;
		public Client Client;
		public IPEndPoint HostLocal, HostRemote, ClientLocal, ClientRemote;
		public double Until;
	}

	public RelayServer( Options options )
	{
		this.options = options;

		manager = new NetManager( this )
		{
			IPv6Enabled = true,
			AutoRecycle = true,
			DisconnectTimeout = 10000,
			NatPunchEnabled = true,
			ChannelsCount = 1,
		};

		manager.NatPunchModule.Init( this );
	}

	public void Run( CancellationToken token )
	{
		if ( !manager.Start( options.Port ) ) throw new InvalidOperationException( $"Couldn't listen on udp {options.Port}" );

		Log( $"listening on udp {manager.LocalPort}" );

		var nextSweep = 0.0;
		var nextReport = 60.0;

		while ( !token.IsCancellationRequested )
		{
			manager.PollEvents();
			manager.NatPunchModule.PollEvents();

			var now = Now;

			if ( now >= nextSweep )
			{
				Sweep( now );
				nextSweep = now + 1.0;
			}

			if ( now >= nextReport )
			{
				var relayed = hosts.Values.Sum( h => h.Clients.Values.Count( c => c.Relayed ) );
				Log( $"{hosts.Count} hosts, {manager.ConnectedPeersCount} connections, {relayed} relayed" );
				nextReport = now + 60.0;
			}

			Thread.Sleep( 2 );
		}

		manager.Stop( true );
	}

	void Sweep( double now )
	{
		foreach ( var (token, introduction) in introductions.Where( x => x.Value.Until < now ).ToList() )
			introductions.Remove( token );

		foreach ( var (address, entry) in misses.Where( x => now - x.Value.Since > 60.0 ).ToList() )
			misses.Remove( address );
	}

	static void Log( string text ) => Console.WriteLine( $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {text}" );

	void INetEventListener.OnConnectionRequest( ConnectionRequest request )
	{
		var data = request.Data;
		var address = request.RemoteEndPoint.Address;

		if ( !data.TryGetString( out var magic ) || magic != RelayProtocol.Magic || !data.TryGetByte( out var version ) || !data.TryGetByte( out var role ) )
		{
			request.RejectForce();
			return;
		}

		if ( version != RelayProtocol.Version )
		{
			Reject( request, "Different relay version" );
			return;
		}

		if ( !data.TryGetString( out var game ) || game.Length > MostText )
		{
			request.RejectForce();
			return;
		}

		if ( misses.TryGetValue( address, out var missed ) && missed.Misses >= options.MostMisses )
		{
			request.RejectForce();
			return;
		}

		if ( manager.ConnectedPeerList.Count( p => p.Address.Equals( address ) ) >= options.MostPerAddress )
		{
			Reject( request, "Too many connections" );
			return;
		}

		switch ( (Role)role )
		{
			case Role.Host:
				AcceptHost( request, game );
				break;

			case Role.Client:
				AcceptClient( request, game );
				break;

			case Role.Query:
				AcceptQuery( request, game );
				break;

			default:
				request.RejectForce();
				break;
		}
	}

	static void Reject( ConnectionRequest request, string reason ) => request.Reject( Encoding.UTF8.GetBytes( reason ) );

	void AcceptHost( ConnectionRequest request, string game )
	{
		if ( !RelayProtocol.TryRead( request.Data, out var lobby ) )
		{
			request.RejectForce();
			return;
		}

		if ( hosts.Count >= options.MostHosts )
		{
			Reject( request, "Relay is full" );
			return;
		}

		var code = NewCode();
		lobby.Code = code;

		var host = new Host { Peer = request.Accept(), Game = game, Lobby = lobby };
		host.Peer.Tag = host;
		hosts[code] = host;

		var w = new NetDataWriter();
		w.Put( (byte)Op.Registered );
		w.Put( code );
		host.Peer.Send( w, DeliveryMethod.ReliableOrdered );
	}

	void AcceptClient( ConnectionRequest request, string game )
	{
		if ( !request.Data.TryGetString( out var code ) || code.Length > 16
			|| !hosts.TryGetValue( code.Trim().ToUpperInvariant(), out var host )
			|| !string.Equals( host.Game, game, StringComparison.OrdinalIgnoreCase ) )
		{
			var address = request.RemoteEndPoint.Address;
			misses[address] = misses.TryGetValue( address, out var m ) ? (m.Misses + 1, m.Since) : (1, Now);

			Reject( request, "No game with that code" );
			return;
		}

		if ( host.Clients.Count >= Math.Max( host.Lobby.MaxMembers, 1 ) * 2 + 8 )
		{
			Reject( request, "Game is full" );
			return;
		}

		var client = new Client { Peer = request.Accept(), Host = host, Id = ++nextId, Game = game };
		client.Peer.Tag = client;
		host.Clients[client.Id] = client;

		//Both sides ask for an introduction with this; the relay pairs them and each punches toward the other
		var token = RandomNumberGenerator.GetHexString( 24 );
		introductions[token] = new Introduction { Host = host, Client = client, Until = Now + IntroduceFor };

		var w = new NetDataWriter();
		w.Put( (byte)Op.Punch );
		w.Put( token );
		host.Peer.Send( w, DeliveryMethod.ReliableOrdered );
		client.Peer.Send( w, DeliveryMethod.ReliableOrdered );
	}

	void AcceptQuery( ConnectionRequest request, string game )
	{
		var peer = request.Accept();

		var listed = hosts.Values.Where( h => h.Lobby.Public && string.Equals( h.Game, game, StringComparison.OrdinalIgnoreCase ) ).Take( 200 ).ToList();

		var w = new NetDataWriter();
		w.Put( (byte)Op.Lobbies );
		w.Put( (ushort)listed.Count );
		foreach ( var host in listed ) RelayProtocol.Write( w, host.Lobby );

		peer.Send( w, DeliveryMethod.ReliableOrdered );
	}

	string NewCode()
	{
		Span<char> code = stackalloc char[CodeLength];

		while ( true )
		{
			for ( var i = 0; i < CodeLength; i++ ) code[i] = CodeLetters[RandomNumberGenerator.GetInt32( CodeLetters.Length )];

			var text = new string( code );
			if ( !hosts.ContainsKey( text ) ) return text;
		}
	}

	void INetEventListener.OnNetworkReceive( NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod )
	{
		if ( !reader.TryGetByte( out var op ) ) return;

		if ( peer.Tag is Host host ) FromHost( host, (Op)op, reader, deliveryMethod );
		else if ( peer.Tag is Client client ) FromClient( client, (Op)op, reader, deliveryMethod );
	}

	void FromHost( Host host, Op op, NetPacketReader r, DeliveryMethod method )
	{
		switch ( op )
		{
			case Op.Update when RelayProtocol.TryRead( r, out var lobby ):
				lobby.Code = host.Lobby.Code;
				host.Lobby = lobby;
				break;

			case Op.Data when r.TryGetUInt( out var id ) && host.Clients.TryGetValue( id, out var client ) && client.Relayed:
			{
				var payload = r.GetRemainingBytesSpan();
				var allowed = host.Sent.Spend( payload.Length, options.HostRate, Now, out _ );
				if ( !allowed && method == DeliveryMethod.Unreliable ) return;

				var w = new NetDataWriter( false, payload.Length + 1 );
				w.Put( (byte)Op.Data );
				w.Put( payload.ToArray() );
				client.Peer.Send( w, method );
				break;
			}

			case Op.Kick when r.TryGetUInt( out var id ) && host.Clients.TryGetValue( id, out var kicked ):
				r.TryGetString( out var reason );
				kicked.Peer.Disconnect( Encoding.UTF8.GetBytes( Clip( reason ) ) );
				break;
		}
	}

	void FromClient( Client client, Op op, NetPacketReader r, DeliveryMethod method )
	{
		var host = client.Host;

		switch ( op )
		{
			case Op.Join when !client.Relayed && r.TryGetBytesWithLength( out var key ) && key.Length <= 256:
			{
				client.Relayed = true;

				var w = new NetDataWriter();
				w.Put( (byte)Op.Join );
				w.Put( client.Id );
				w.Put( client.Peer.Address.ToString() );
				w.Put( client.Game );
				w.PutBytesWithLength( key );
				host.Peer.Send( w, DeliveryMethod.ReliableOrdered );
				break;
			}

			case Op.Data when client.Relayed:
			{
				var payload = r.GetRemainingBytesSpan();

				if ( !client.Sent.Spend( payload.Length, options.ClientRate, Now, out var abusive ) )
				{
					if ( abusive )
					{
						client.Peer.Disconnect( Encoding.UTF8.GetBytes( "Too much traffic" ) );
						return;
					}

					if ( method == DeliveryMethod.Unreliable ) return;
				}

				var w = new NetDataWriter( false, payload.Length + 5 );
				w.Put( (byte)Op.Data );
				w.Put( client.Id );
				w.Put( payload.ToArray() );
				host.Peer.Send( w, method );
				break;
			}
		}
	}

	void INetEventListener.OnPeerDisconnected( NetPeer peer, DisconnectInfo disconnectInfo )
	{
		if ( peer.Tag is Host host )
		{
			hosts.Remove( host.Lobby.Code );

			foreach ( var client in host.Clients.Values.ToList() )
				client.Peer.Disconnect( Encoding.UTF8.GetBytes( "The host left" ) );

			foreach ( var token in introductions.Where( x => x.Value.Host == host ).Select( x => x.Key ).ToList() )
				introductions.Remove( token );

			return;
		}

		if ( peer.Tag is Client gone )
		{
			gone.Host.Clients.Remove( gone.Id );

			if ( gone.Relayed && gone.Host.Peer.ConnectionState == ConnectionState.Connected )
			{
				var w = new NetDataWriter();
				w.Put( (byte)Op.Leave );
				w.Put( gone.Id );
				gone.Host.Peer.Send( w, DeliveryMethod.ReliableOrdered );
			}
		}
	}

	void INatPunchListener.OnNatIntroductionRequest( IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token )
	{
		if ( token is null || !introductions.TryGetValue( token, out var introduction ) ) return;

		if ( Same( remoteEndPoint, introduction.Host.Peer ) )
		{
			introduction.HostLocal = localEndPoint;
			introduction.HostRemote = remoteEndPoint;
		}
		else if ( Same( remoteEndPoint, introduction.Client.Peer ) )
		{
			introduction.ClientLocal = localEndPoint;
			introduction.ClientRemote = remoteEndPoint;
		}
		else return;

		if ( introduction.HostRemote is null || introduction.ClientRemote is null ) return;

		manager.NatPunchModule.NatIntroduce( introduction.HostLocal, introduction.HostRemote, introduction.ClientLocal, introduction.ClientRemote, token );
		introductions.Remove( token );
	}

	static bool Same( IPEndPoint a, IPEndPoint b ) => a.Port == b.Port && a.Address.Equals( b.Address );

	void INatPunchListener.OnNatIntroductionSuccess( IPEndPoint targetEndPoint, NatAddressType type, string token ) { }

	void INetEventListener.OnPeerConnected( NetPeer peer ) { }

	void INetEventListener.OnNetworkError( IPEndPoint endPoint, System.Net.Sockets.SocketError socketError ) { }

	void INetEventListener.OnNetworkReceiveUnconnected( IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType ) { }

	void INetEventListener.OnNetworkLatencyUpdate( NetPeer peer, int latency ) { }
}
