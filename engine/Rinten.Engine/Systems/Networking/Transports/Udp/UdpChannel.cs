using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Rinten.Network;

//One connection over our own UDP. The same class on both ends: a client's connection to a server and a
//server's connection to a client differ in who opened the socket, and in nothing else that matters here.
internal sealed class UdpChannel : Connection
{
	private readonly UdpPeer peer;
	private readonly UdpTransport transport;

	private readonly bool isHost;

	internal UdpChannel( UdpTransport transport, UdpPeer peer, bool isHost )
	{
		this.transport = transport;
		this.peer = peer;
		this.isHost = isHost;
	}

	internal UdpPeer Peer => peer;

	public override string Address => peer.Address?.ToString() ?? "udp";

	public override bool IsHost => isHost;

	public override float Latency => peer.Latency;

	public override ConnectionStats Stats => peer.Stats;

	internal override void InternalSend( byte[] data, NetFlags flags )
	{
		if ( data is null || data.Length == 0 ) return;

		//Anything not asked for unreliably is sent reliably: a message that went missing
		//is worse than one that arrived late, unless whoever sent it said otherwise.
		peer.Send( data, !( ( flags & NetFlags.Unreliable ) == NetFlags.Unreliable ) );

		transport.Flush( peer );
	}

	internal override void InternalRecv( NetworkSystem.MessageHandler handler )
	{
		while ( peer.Received.TryDequeue( out var message ) )
		{
			//Through the same door every transport uses: what a packet means - chunks, decoding,
			//who it is from - belongs to the connection and not to whichever wire carried it.
			OnRawPacketReceived( message, handler );
		}
	}

	internal override void InternalClose( int closeCode, string closeReason )
	{
		transport.Close( peer );
	}
}

//One UDP socket for all connections; a receive task routes packets to their peers
internal sealed class UdpTransport : IDisposable
{
	private static readonly Logger log = new( "Udp" );

	private readonly UdpClient socket;
	private readonly CancellationTokenSource cancel = new();

	private readonly Dictionary<IPEndPoint, UdpPeer> peers = new();
	private readonly object padlock = new();

	//Pending join requests and when last sent; resent until answered, since loss can't be recovered
	private readonly Dictionary<UdpPeer, double> asking = new();

	//How long to wait for an answer before asking again.
	private const float AskAgain = 0.25f;

	private uint nextConnection = 1;

	/// <summary>Told when somebody new arrives, on a socket that is listening.</summary>
	public Action<UdpPeer> OnConnected;

	public Action<UdpPeer> OnDisconnected;

	public UdpTransport( int port )
	{
		socket = new UdpClient( port );

		//Windows raises an exception on the next receive when an earlier send was refused,
		//which for a connectionless socket is not an error we can do anything with.
		try
		{
			socket.Client.IOControl( unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null );
		}
		catch ( Exception )
		{
			//Not Windows, or not allowed. Neither matters.
		}

		Port = ( (IPEndPoint)socket.Client.LocalEndPoint ).Port;

		_ = Task.Run( () => ReceiveLoop( cancel.Token ) );
	}

	public int Port { get; }

	/// <summary>Start talking to somebody, from this end.</summary>
	public UdpPeer Connect( IPEndPoint address )
	{
		var peer = Peer( address, 0 );

		lock ( padlock ) asking[peer] = RealTime.Now;

		Ask( peer );

		return peer;
	}

	private void Ask( UdpPeer peer )
	{
		var packet = new byte[UdpProtocol.HeaderSize];

		UdpProtocol.Write( packet, new UdpProtocol.Header { Kind = UdpProtocol.Kind.Connect } );

		Send( peer, packet );
	}

	private UdpPeer Peer( IPEndPoint address, uint connection )
	{
		lock ( padlock )
		{
			if ( peers.TryGetValue( address, out var found ) ) return found;

			var peer = new UdpPeer( address, connection );
			peers[address] = peer;

			return peer;
		}
	}

	private async Task ReceiveLoop( CancellationToken token )
	{
		while ( !token.IsCancellationRequested )
		{
			try
			{
				var result = await socket.ReceiveAsync( token );

				if ( !UdpProtocol.Read( result.Buffer, out var header ) ) continue;

				Handle( result.RemoteEndPoint, header, result.Buffer.AsSpan( UdpProtocol.HeaderSize ) );
			}
			catch ( OperationCanceledException )
			{
				return;
			}
			catch ( ObjectDisposedException )
			{
				return;
			}
			catch ( Exception e )
			{
				log.Warning( e, $"udp: {e.Message}" );
			}
		}
	}

	private void Handle( IPEndPoint from, in UdpProtocol.Header header, ReadOnlySpan<byte> payload )
	{
		switch ( header.Kind )
		{
			case UdpProtocol.Kind.Connect:
			{
				var known = false;

				lock ( padlock ) known = peers.ContainsKey( from );

				var peer = Peer( from, known ? 0u : nextConnection++ );

				//Answered every time, not only the first: a lost acceptance looks to the other end exactly like a lost request.
				var packet = new byte[UdpProtocol.HeaderSize];

				UdpProtocol.Write( packet, new UdpProtocol.Header
				{
					Kind = UdpProtocol.Kind.Accept,
					Connection = peer.Connection,
				} );

				Send( peer, packet );

				if ( !known ) OnConnected?.Invoke( peer );

				return;
			}

			case UdpProtocol.Kind.Accept:
			{
				var peer = Peer( from, header.Connection );

				if ( peer.Connection == 0 )
				{
					peer.Connection = header.Connection;
					OnConnected?.Invoke( peer );
				}

				peer.Receive( header, payload );

				return;
			}

			case UdpProtocol.Kind.Disconnect:
			{
				UdpPeer going;

				lock ( padlock )
				{
					if ( !peers.Remove( from, out going ) ) return;
				}

				OnDisconnected?.Invoke( going );

				return;
			}

			default:
			{
				UdpPeer peer;

				lock ( padlock )
				{
					//Anything from somebody we have never heard of is dropped: a connection begins with a request and not with traffic.
					if ( !peers.TryGetValue( from, out peer ) ) return;
				}

				peer.Receive( header, payload );

				return;
			}
		}
	}

	/// <summary>Send whatever a peer has waiting.</summary>
	public void Flush( UdpPeer peer )
	{
		while ( peer.Outgoing.TryDequeue( out var packet ) )
		{
			Send( peer, packet );
		}
	}

	private void Send( UdpPeer peer, byte[] packet )
	{
		try
		{
			socket.Send( packet, packet.Length, peer.Address );

			peer.Sent( packet.Length );
		}
		catch ( Exception e )
		{
			log.Warning( e, $"udp: could not send to {peer.Address}: {e.Message}" );
		}
	}

	/// <summary>Move every connection along: say again what was not heard, and drop whoever has stopped answering.</summary>
	public void Tick()
	{
		UdpPeer[] all;

		lock ( padlock ) all = peers.Values.ToArray();

		var now = RealTime.Now;

		foreach ( var peer in all )
		{
			peer.Tick();
			Flush( peer );

			var ask = false;

			lock ( padlock )
			{
				if ( asking.TryGetValue( peer, out var asked ) )
				{
					if ( peer.Connection != 0 )
					{
						asking.Remove( peer );
					}
					else if ( now - asked > AskAgain )
					{
						asking[peer] = now;
						ask = true;
					}
				}
			}

			//Outside the lock: the receive loop wants it back, and a send can take as long as the operating system feels like taking.
			if ( ask ) Ask( peer );

			if ( !peer.TimedOut ) continue;

			lock ( padlock )
			{
				peers.Remove( peer.Address );
				asking.Remove( peer );
			}

			OnDisconnected?.Invoke( peer );
		}
	}

	/// <summary>Say goodbye, once, and forget them.</summary>
	public void Close( UdpPeer peer )
	{
		var packet = new byte[UdpProtocol.HeaderSize];

		UdpProtocol.Write( packet, new UdpProtocol.Header
		{
			Kind = UdpProtocol.Kind.Disconnect,
			Connection = peer.Connection,
		} );

		Send( peer, packet );

		bool had;

		lock ( padlock )
		{
			had = peers.Remove( peer.Address );
			asking.Remove( peer );
		}

		//Told about at this end too, not only at the other. A server that kicks somebody has to forget them
		//the same way it forgets somebody who left on their own, or the connection stays on its books for good.
		if ( had ) OnDisconnected?.Invoke( peer );
	}

	public void Dispose()
	{
		cancel.Cancel();

		socket.Close();
		socket.Dispose();

		cancel.Dispose();
	}
}
