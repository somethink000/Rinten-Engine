using System.Net;

namespace Rinten.Network;

//Client-side UDP connection; owns its socket, unlike the server side
internal sealed class UdpConnection : Connection
{
	private static readonly Logger log = new( "Udp" );

	private readonly UdpTransport transport;
	private readonly UdpPeer peer;

	/// <summary>Connect to "host:port", or to "host" on the default port.</summary>
	public UdpConnection( string target )
	{
		transport = new UdpTransport( 0 );

		var address = Parse( target );

		log.Info( $"connecting to {address}" );

		peer = transport.Connect( address );
	}

	/// <summary>Whatever the game was told to connect to, as an address.</summary>
	public static IPEndPoint Parse( string target )
	{
		var wanted = ( target ?? "" ).Trim();

		if ( wanted.StartsWith( "udp://", StringComparison.OrdinalIgnoreCase ) ) wanted = wanted[6..];

		wanted = wanted.Replace( "localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase );

		var port = Networking.Port;
		var host = wanted;

		var colon = wanted.LastIndexOf( ':' );

		if ( colon > 0 && int.TryParse( wanted[( colon + 1 )..], out var parsed ) )
		{
			host = wanted[..colon];
			port = parsed;
		}

		return IPAddress.TryParse( host, out var ip )
			? new IPEndPoint( ip, port )
			: new IPEndPoint( Dns.GetHostAddresses( host ).FirstOrDefault() ?? IPAddress.Loopback, port );
	}

	public override string Address => peer.Address?.ToString() ?? "udp";

	/// <summary>The far end is the server, which is the host.</summary>
	public override bool IsHost => true;

	public override float Latency => peer.Latency;

	public override ConnectionStats Stats => peer.Stats;

	internal override void InternalSend( byte[] data, NetFlags flags )
	{
		if ( data is null || data.Length == 0 ) return;

		peer.Send( data, !( ( flags & NetFlags.Unreliable ) == NetFlags.Unreliable ) );

		transport.Flush( peer );
	}

	internal override void InternalRecv( NetworkSystem.MessageHandler handler )
	{
		//Whatever has to be said again, and whatever has to be said at all, before reading: this is the only place a client's connection is moved along.
		transport.Tick();

		while ( peer.Received.TryDequeue( out var message ) )
		{
			OnRawPacketReceived( message, handler );
		}
	}

	internal override void InternalClose( int closeCode, string closeReason )
	{
		transport.Close( peer );
		transport.Dispose();
	}
}
