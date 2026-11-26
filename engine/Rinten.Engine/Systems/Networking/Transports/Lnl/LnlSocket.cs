namespace Rinten.Network;

//A host's listen socket over LiteNetLib, registered with the project's relay where it names one; connects and
//disconnects arrive while polling on the main thread
internal sealed class LnlSocket : NetworkSocket
{
	readonly LnlTransport transport;
	readonly List<LnlChannel> channels = new();
	readonly LobbyConfig config;

	public LnlSocket( int port, LobbyConfig config = default )
	{
		this.config = config;
		transport = LnlTransport.Listen( port );

		transport.OnConnected = channel =>
		{
			channels.Add( channel );
			OnClientConnect?.Invoke( channel );
		};

		transport.OnDisconnected = channel =>
		{
			if ( !channels.Remove( channel ) ) return;
			OnClientDisconnect?.Invoke( channel );
		};
	}

	public int Port => transport.Port;

	//What an invite can pin: host:port#Fingerprint
	public string Fingerprint => transport.Key.Fingerprint;

	public string RelayCode => transport.RelayCode;

	internal override void Initialize( NetworkSystem networkSystem )
	{
		var relay = ProjectSettings.Networking?.RelayAddress;
		if ( string.IsNullOrWhiteSpace( relay ) ) return;

		try
		{
			transport.Register( relay, new RelayProtocol.Lobby
			{
				Name = Networking.ServerName ?? config.Name ?? "",
				Map = Networking.MapName ?? "",
				MaxMembers = config.MaxPlayers,
				Public = config.Privacy == LobbyPrivacy.Public && !config.Hidden,
				Data = new( Networking.ServerData ),
			} );
		}
		catch ( Exception e )
		{
			Log.Warning( $"Couldn't reach the relay at {relay}: {e.Message}" );
		}
	}

	internal override void SetData( string key, string value ) => transport.UpdateLobby( x =>
	{
		x.Data[key] = value;
		return true;
	} );

	internal override void SetServerName( string name ) => transport.UpdateLobby( x =>
	{
		x.Name = name ?? "";
		return true;
	} );

	internal override void SetMapName( string name ) => transport.UpdateLobby( x =>
	{
		x.Map = name ?? "";
		return true;
	} );

	//Once a second
	internal override void Tick( NetworkSystem networkSystem )
	{
		var members = Connection.All.Count;
		transport.UpdateLobby( x =>
		{
			if ( x.Members == members ) return false;
			x.Members = members;
			return true;
		} );
	}

	internal override void Dispose()
	{
		channels.Clear();
		transport.Dispose();
	}

	internal override void ProcessMessagesInThread() { }

	internal override void GetIncomingMessages( NetworkSystem.MessageHandler handler )
	{
		transport.Poll();

		for ( var i = channels.Count - 1; i >= 0; i-- )
		{
			var channel = channels[i];
			channel.InternalRecv( handler );

			//Closed from our side (kicked, too many forged packets): no disconnect event follows
			if ( channel.IsGone )
			{
				channels.RemoveAt( i );
				OnClientDisconnect?.Invoke( channel );
			}
		}
	}
}
