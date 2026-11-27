using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Rinten.Network;


//A listen socket over TCP. For testing locally.
internal class TcpSocket : NetworkSocket, IValid
{
	//Accepted on the listen task, handed over on the main thread
	readonly System.Collections.Concurrent.ConcurrentQueue<TcpChannel> accepted = new();
	readonly List<TcpChannel> connections = new();

	async Task SocketThread( string address, int port, CancellationToken token )
	{
		using var listener = new TcpListener( new IPEndPoint( IPAddress.Parse( address ), port ) );

		try
		{
			listener.Start();
		}
		catch ( System.Exception e )
		{
			Log.Warning( e, "Couldnt start TcpSocket" );
			return;
		}

		while ( !token.IsCancellationRequested )
		{
			try
			{
				var client = await listener.AcceptTcpClientAsync( token );
				accepted.Enqueue( new TcpChannel( client ) );
			}
			catch ( OperationCanceledException )
			{
				//Dispose() was called
			}
			catch ( Exception e )
			{
				Log.Warning( e, $"TcpSocket exception: {e.Message}" );
			}
		}

		listener.Stop();
	}

	CancellationTokenSource tokenSource;

	public bool IsValid => true;

	public TcpSocket( string address, int port )
	{
		tokenSource = new();
		_ = SocketThread( address, port, tokenSource.Token );
	}

	~TcpSocket()
	{
		Dispose();
	}

	internal override void Dispose()
	{
		tokenSource.Cancel();
		tokenSource.Dispose();

		//Their sockets would otherwise stay open, and the clients would never hear the host went
		foreach ( var c in connections ) c.Close( 0, "Host closed" );
		while ( accepted.TryDequeue( out var c ) ) c.Close( 0, "Host closed" );
		connections.Clear();

		GC.SuppressFinalize( this );
	}

	internal override void ProcessMessagesInThread()
	{

	}

	internal override void GetIncomingMessages( NetworkSystem.MessageHandler handler )
	{
		while ( accepted.TryDequeue( out var c ) )
		{
			connections.Add( c );
			OnClientConnect?.Invoke( c );
		}

		for ( var i = connections.Count - 1; i >= 0; i-- )
		{
			var c = connections[i];

			if ( !c.IsConnected )
			{
				connections.RemoveAt( i );
				OnClientDisconnect?.Invoke( c );
				c.Close( 0, "Disconnect" );
				continue;
			}

			c.GetIncomingMessages( handler );
		}
	}
}
