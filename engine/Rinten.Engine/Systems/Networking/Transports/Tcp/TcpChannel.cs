using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;

namespace Rinten.Network;


//A listen socket, one socket to many. We should really use this just dedicated servers imo.
internal class TcpChannel : Connection
{
	internal readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>();

	string _address = "Tcp";

	public override string Address => _address;

	public bool IsConnected => client?.Connected ?? false;

	//Set once the socket had connected and its loop ended; the client end tells the network system once
	volatile bool ended;
	bool told;

	async Task SocketLoop( CancellationToken token )
	{
		try
		{
			while ( client?.Connected != true )
			{
				await Task.Delay( 10, token );
				token.ThrowIfCancellationRequested();
			}

			_address = client?.Client?.RemoteEndPoint?.ToString() ?? client?.Client?.LocalEndPoint?.ToString() ?? "Tcp";

			var stream = client.GetStream();
			started = true;

			_ = Task.Run( async () => await SendThread( token ), token );

			while ( !token.IsCancellationRequested )
			{
				var header = new byte[sizeof( int )];
				await stream.ReadExactlyAsync( header.AsMemory( 0, header.Length ), token );

				var messageLength = BitConverter.ToInt32( header );
				if ( messageLength <= 0 )
				{
					Log.Warning( $"TcpChannel: Invalid message length {messageLength} from {Address}" );
					break;
				}

				var packet = new byte[messageLength];
				await stream.ReadExactlyAsync( packet.AsMemory( 0, packet.Length ), token );
				await incoming.Writer.WriteAsync( packet, token );
			}
		}
		catch ( Exception e ) when ( e is OperationCanceledException or global::System.IO.EndOfStreamException )
		{
			//Closed by us, or by the other end
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"TcpChannel: {e.Message}" );
		}
		finally
		{
			client?.Close();
			client?.Dispose();
			client = null;
			ended = started;
		}
	}

	bool started;

	readonly CancellationTokenSource tokenSource;

	public bool IsValid => true;

	bool isHost;
	public override bool IsHost => isHost;

	TcpClient client;

	public TcpChannel( TcpClient client )
	{
		this.client = client;
		client.ReceiveBufferSize = 1024 * 1024;
		client.SendBufferSize = 1024 * 1024;
		client.NoDelay = true;

		tokenSource = new();
		isHost = false;

		_ = Task.Run( () => SocketLoop( tokenSource.Token ), tokenSource.Token );
	}

	public TcpChannel( string host, int port )
	{
		client = new();
		client.ReceiveBufferSize = 1024 * 1024;
		client.SendBufferSize = 1024 * 1024;
		client.LingerState = new( true, 15 ); //15 seconds is a long time, but we want reliability

		tokenSource = new();
		isHost = true;

		_ = Task.Run( () => ConnectAndRunAsync( host, port, tokenSource.Token ) );
	}

	~TcpChannel()
	{
		tokenSource?.Cancel();
	}

	Channel<byte[]> sendChannel = Channel.CreateUnbounded<byte[]>();


	private async Task ConnectAndRunAsync( string host, int port, CancellationToken token )
	{
		try
		{
			await client.ConnectAsync( host, port );
			await SocketLoop( token );
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"TcpChannel connect failed: {e.Message}" );
			client?.Close();
			client?.Dispose();
			client = null;
		}
	}

	internal override void InternalSend( byte[] output, NetFlags flags )
	{
		if ( client?.Connected != true )
			return;

		try
		{
			sendChannel.Writer.TryWrite( BitConverter.GetBytes( output.Length ) );
			sendChannel.Writer.TryWrite( output );
		}
		catch ( Exception )
		{
			//Probably disconnected, who cares
		}
	}

	//Send the network data in a thread. This prevents the client from freezing up when running a client and
	//server in the same process. In reality this only really happens in unit tests, but better safe than sorry.
	private async Task SendThread( CancellationToken token )
	{
		try
		{
			while ( !token.IsCancellationRequested )
			{
				await sendChannel.Reader.WaitToReadAsync( token );

				if ( client?.Connected != true || !sendChannel.Reader.TryRead( out byte[] data ) )
					continue;

				var writeStream = client.GetStream();
				try
				{
					await writeStream.WriteAsync( data, token );
					MessagesSent++;
				}
				catch ( System.IO.IOException )
				{
					continue;
				}
			}
		}
		catch ( OperationCanceledException )
		{
			//normal
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"TcpChannel: {e.Message}" );
		}
	}

	bool closed;

	//Both the network system and the socket close a channel; the second does nothing
	internal override void InternalClose( int closeCode, string closeReason )
	{
		if ( closed ) return;
		closed = true;

		told = true;
		tokenSource.Cancel();
		tokenSource.Dispose();
		GC.SuppressFinalize( this );
	}

	internal override void InternalRecv( NetworkSystem.MessageHandler handler )
	{
		//A message handler can synchronously close this channel (for example, reconnect).
		while ( !tokenSource.IsCancellationRequested && incoming.Reader.TryRead( out byte[] data ) )
		{
			OnRawPacketReceived( data, handler );
		}

		if ( isHost && ended && !told )
		{
			told = true;
			System?.OnServerDisconnection( 0, "connection lost" );
		}
	}
}
