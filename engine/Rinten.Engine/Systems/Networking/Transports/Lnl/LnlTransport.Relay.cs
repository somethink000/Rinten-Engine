using LiteNetLib;
using LiteNetLib.Utils;
using System.Net;
using static Rinten.Network.RelayProtocol;

namespace Rinten.Network;

//The relay link on the game's own socket: a host registers for a join code and a lobby listing; a client joins by
//code, over a punched direct path where the routers allow one and through the relay where they don't
internal sealed partial class LnlTransport : INatPunchListener
{
	//Seconds a client tries for a punched path before taking the relay, and between introduction requests
	const double PunchFor = 3.0;
	const double PunchEvery = 0.5;

	//Seconds between a host's lobby updates, and before it registers again after losing the relay
	const double UpdateEvery = 1.0;
	const double RetryAfter = 10.0;

	NetPeer relay;
	IPEndPoint relayAddress;
	readonly Dictionary<uint, LnlChannel> relayedChannels = new();
	readonly List<(string Token, double Until, double Next)> punching = new();

	Lobby lobby;
	bool lobbyDirty;
	double nextLobbyUpdate;
	double retryAt;
	bool relayWarned;

	LnlChannel joining;
	NetPeer punchedPeer;
	double joinStarted;

	List<Lobby> queried;
	bool queryOver;

	//Skips the punched path, to try the relayed one on a single machine where punching always works
	[ConVar( "net_relay_only", ConVarFlags.Protected, Help = "Join relay:// games through the relay without trying a direct path" )]
	internal static bool RelayOnly { get; set; }

	//What the relay calls this host; relay://code joins it
	public string RelayCode { get; private set; }

	public void Register( string relayTarget, Lobby info )
	{
		relayAddress = Parse( relayTarget, DefaultPort ).Address;
		lobby = info;
		Register();
	}

	void Register()
	{
		var w = new NetDataWriter();
		w.Put( RelayProtocol.Magic );
		w.Put( RelayProtocol.Version );
		w.Put( (byte)Role.Host );
		w.Put( Application.GameIdent ?? "" );
		RelayProtocol.Write( w, lobby );

		relay = manager.Connect( relayAddress, w );
		log.Info( $"registering with the relay at {relayAddress}" );
	}

	//The change says whether it changed anything
	public void UpdateLobby( Func<Lobby, bool> change )
	{
		if ( lobby is not null && change( lobby ) ) lobbyDirty = true;
	}

	//code[#pin] through the relay at relayTarget
	public static LnlChannel ConnectViaRelay( string target, string relayTarget )
	{
		var code = target.Trim();
		string pin = null;

		if ( code.IndexOf( '#' ) is var hash and >= 0 )
		{
			pin = code[( hash + 1 )..].Trim().ToLowerInvariant();
			code = code[..hash];
		}

		var transport = StartClient();
		transport.relayAddress = Parse( relayTarget, DefaultPort ).Address;

		var w = new NetDataWriter();
		w.Put( RelayProtocol.Magic );
		w.Put( RelayProtocol.Version );
		w.Put( (byte)Role.Client );
		w.Put( Application.GameIdent ?? "" );
		w.Put( code.ToUpperInvariant(), 32 );

		transport.relay = transport.manager.Connect( transport.relayAddress, w );

		var channel = new LnlChannel( transport, null, isHost: true, owned: true ) { Pin = string.IsNullOrEmpty( pin ) ? null : pin };
		transport.joining = channel;
		transport.joinStarted = RealTime.NowDouble;

		log.Info( $"joining {code} through the relay at {transport.relayAddress}" );
		return channel;
	}

	//The public lobbies of this game the relay knows; empty where it can't be reached
	public static async Task<List<LobbyInformation>> QueryRelay( string relayTarget )
	{
		var transport = StartClient();

		try
		{
			var w = new NetDataWriter();
			w.Put( RelayProtocol.Magic );
			w.Put( RelayProtocol.Version );
			w.Put( (byte)Role.Query );
			w.Put( Application.GameIdent ?? "" );

			transport.relay = transport.manager.Connect( Parse( relayTarget, DefaultPort ).Address, w );

			var until = RealTime.NowDouble + 4.0;
			while ( !transport.queryOver && RealTime.NowDouble < until )
			{
				transport.Poll();
				await Task.Delay( 20 );
			}

			return ( transport.queried ?? [] ).Select( x => new LobbyInformation
			{
				Provider = "relay",
				Target = $"relay://{x.Code}",
				Name = x.Name,
				Map = x.Map,
				Members = x.Members,
				MaxMembers = x.MaxMembers,
				Data = x.Data,
			} ).ToList();
		}
		finally
		{
			transport.Dispose();
		}
	}

	void TickRelay()
	{
		if ( relayAddress is null ) return;

		var now = RealTime.NowDouble;

		for ( var i = punching.Count - 1; i >= 0; i-- )
		{
			var (token, until, next) = punching[i];

			if ( now > until ) punching.RemoveAt( i );
			else if ( now >= next )
			{
				manager.NatPunchModule.SendNatIntroduceRequest( relayAddress, token );
				punching[i] = (token, until, now + PunchEvery);
			}
		}

		if ( joining is not null && !joining.HasPath && !joining.IsGone && relay is { ConnectionState: ConnectionState.Connected } && ( RelayOnly || now - joinStarted > PunchFor ) )
		{
			punchedPeer?.Disconnect();
			punchedPeer = null;

			var w = new NetDataWriter();
			w.Put( (byte)Op.Join );
			w.PutBytesWithLength( Key.Public );
			relay.Send( w, DeliveryMethod.ReliableOrdered );

			joining.UseRelay( relay );
			log.Info( "no direct path to the host, joining through the relay" );
		}

		if ( !isServer || lobby is null ) return;

		if ( relay is null && now >= retryAt )
		{
			Register();
			return;
		}

		if ( lobbyDirty && relay is { ConnectionState: ConnectionState.Connected } && now >= nextLobbyUpdate )
		{
			var w = new NetDataWriter();
			w.Put( (byte)Op.Update );
			RelayProtocol.Write( w, lobby );
			relay.Send( w, DeliveryMethod.ReliableOrdered );

			lobbyDirty = false;
			nextLobbyUpdate = now + UpdateEvery;
		}
	}

	void FromRelay( NetPacketReader r )
	{
		if ( !r.TryGetByte( out var op ) ) return;

		switch ( (Op)op )
		{
			case Op.Registered when isServer && r.TryGetString( out var code ):
				RelayCode = Clip( code );
				log.Info( $"relay join code {RelayCode}" );
				break;

			case Op.Punch when r.TryGetString( out var token ) && token.Length <= 64 && punching.Count < 64 && !( RelayOnly && !isServer ):
				punching.Add( (token, RealTime.NowDouble + PunchFor + 1.0, 0) );
				break;

			case Op.Join when isServer:
				RelayedJoin( r );
				break;

			case Op.Data when isServer:
				if ( r.TryGetUInt( out var id ) && relayedChannels.TryGetValue( id, out var channel ) )
					channel.Arrived( r.GetRemainingBytesSpan() );
				break;

			case Op.Data when joining is not null && joining.Peer == relay:
				joining.Arrived( r.GetRemainingBytesSpan() );
				break;

			case Op.Leave when isServer:
				if ( r.TryGetUInt( out var gone ) && relayedChannels.Remove( gone, out var left ) )
				{
					left.Gone( "left" );
					OnDisconnected?.Invoke( left );
				}
				break;

			case Op.Lobbies when !isServer:
				ReadLobbies( r );
				break;
		}
	}

	//The relay passes on what a direct client would have sent after its magic and version: game, then key
	void RelayedJoin( NetPacketReader r )
	{
		if ( !r.TryGetUInt( out var id ) || !r.TryGetString( out var address ) || relayedChannels.ContainsKey( id ) ) return;

		if ( Admit( r, Version, null, out var clientKey, out var secret ) is { } refused )
		{
			SendKick( id, refused.Length == 0 ? "Refused" : refused );
			return;
		}

		var channel = new LnlChannel( this, relay, isHost: false, owned: false );
		channel.UseRelay( relay, id, $"relay:{Clip( address )}" );
		relayedChannels[id] = channel;

		Welcome( channel, secret, clientKey );
	}

	public void KickRelayed( uint id, string reason )
	{
		if ( relayedChannels.Remove( id ) ) SendKick( id, reason );
	}

	void SendKick( uint id, string reason )
	{
		if ( relay is not { ConnectionState: ConnectionState.Connected } ) return;

		var w = new NetDataWriter();
		w.Put( (byte)Op.Kick );
		w.Put( id );
		w.Put( reason ?? "", MostText );
		relay.Send( w, DeliveryMethod.ReliableOrdered );
	}

	void ReadLobbies( NetPacketReader r )
	{
		queried = new();

		if ( r.TryGetUShort( out var count ) )
		{
			for ( var i = 0; i < count && RelayProtocol.TryRead( r, out var found ); i++ )
				queried.Add( found );
		}

		queryOver = true;
	}

	void RelayLost( NetPeer lost, string reason )
	{
		relay = null;
		queryOver = true;

		if ( isServer )
		{
			//Said once: a relay that stays down would otherwise be a warning every few seconds
			if ( RelayCode is not null || !relayWarned ) log.Warning( $"lost the relay ({reason}), trying again every {RetryAfter:0}s" );
			relayWarned = true;

			RelayCode = null;
			retryAt = RealTime.NowDouble + RetryAfter;

			foreach ( var channel in relayedChannels.Values )
			{
				channel.Gone( "relay lost" );
				OnDisconnected?.Invoke( channel );
			}

			relayedChannels.Clear();
			return;
		}

		//Refused by the relay (no such code, full), or the relayed path went; a punched path doesn't need it
		if ( joining is not null && ( joining.Peer is null || joining.Peer == lost ) )
			joining.Gone( reason );
	}

	void Punched( LnlChannel channel, NetPeer peer )
	{
		if ( peer == relay || channel.Peer == peer ) return;

		//The relay was taken first
		if ( channel.HasPath )
		{
			peer.Disconnect();
			return;
		}

		channel.UseDirect( peer );
		punchedPeer = null;

		//Not needed any more; cleared first so its disconnect isn't read as losing the host
		var link = relay;
		relay = null;
		link?.Disconnect();

		log.Info( $"direct path to the host at {peer}" );
	}

	void INatPunchListener.OnNatIntroductionRequest( IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token ) { }

	void INatPunchListener.OnNatIntroductionSuccess( IPEndPoint targetEndPoint, NatAddressType type, string token )
	{
		if ( isServer || joining is null || joining.HasPath || punchedPeer is not null ) return;

		punchedPeer = manager.Connect( targetEndPoint, ConnectRequest() );
		if ( punchedPeer is not null ) punchedPeer.Tag = joining;
	}
}
