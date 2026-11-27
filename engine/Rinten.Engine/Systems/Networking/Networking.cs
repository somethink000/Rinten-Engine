using Rinten.Engine;
using Rinten.Network;
using Rinten.Utility;
using System.Text;
using System.Threading;

namespace Rinten;

/// <summary>Global manager to hold and tick the singleton instance of NetworkSystem.</summary>
public static partial class Networking
{
	internal const int ReceiveBatchSize = 32;
	internal static NetworkSystem System;

	[ConVar( "net_max_outgoing", ConVarFlags.Protected, Help = "Maximum outgoing messages to send per tick. 0 = unlimited." )]
	internal static int MaxOutgoingMessagesPerTick { get; set; } = 1024;

	[ConVar( "net_max_incoming", ConVarFlags.Protected, Help = "Maximum incoming messages to receive per tick. 0 = unlimited." )]
	internal static int ReceiveBatchSizePerTick { get; set; } = 1024;

	[ConVar( "net_allow_local", ConVarFlags.Protected, Help = "Allow loopback connections for multi-instance testing on one machine (P2P-like)." )]
	internal static bool AllowLocal { get; set; } = false;

	internal static Dictionary<string, string> ServerData { get; set; } = new();

	/// <summary>Set data about the current server or lobby. Other players can query this when searching for a game.</summary>
	public static void SetData( string key, string value )
	{
		ServerData[key] = value;

		if ( !IsHost || System is null )
			return;

		foreach ( var s in System.Sockets )
		{
			s.SetData( key, value );
		}

		var msg = new ServerDataMsg { Name = key, Value = value };
		System.Broadcast( msg, Connection.ChannelState.Welcome );
	}

	/// <summary>Get data about the current server or lobby. This data can be used for filtering when querying lobbies.</summary>
	public static string GetData( string key, string defaultValue = "" )
	{
		return ServerData.GetValueOrDefault( key, defaultValue );
	}

	private static string _serverName;
	private static string _mapName;

	/// <summary>The name of the server you are currently connected to.</summary>
	public static string ServerName
	{
		get => _serverName;
		set
		{
			if ( _serverName == value )
				return;

			_serverName = value;

			if ( !IsHost || System is null )
				return;

			foreach ( var s in System.Sockets )
			{
				s.SetServerName( value );
			}

			var msg = new ServerNameMsg { Name = value };
			System.Broadcast( msg, Connection.ChannelState.Welcome );
		}
	}

	/// <summary>The name of the map being used on the server you're connected to.</summary>
	public static string MapName
	{
		get => _mapName;
		internal set
		{
			if ( _mapName == value )
				return;

			_mapName = value;

			if ( !IsHost || System is null )
				return;

			foreach ( var s in System.Sockets )
			{
				s.SetMapName( value );
			}

			var msg = new MapNameMsg { Name = value };
			System.Broadcast( msg, Connection.ChannelState.Welcome );
		}
	}

	/// <summary>The join code the project's relay gave this host; Connect( "relay://" + code ) joins it. Null without a relay.</summary>
	public static string RelayCode => System?.Sockets.OfType<LnlSocket>().FirstOrDefault()?.RelayCode;

	/// <summary>The maximum number of players allowed on the server you're connected to.</summary>
	public static int MaxPlayers { get; internal set; }

	//The last connection string used to connect to a server.
	internal static string LastConnectionString { get; set; }

	[ConVar( "net_debug", ConVarFlags.Protected )]
	internal static bool Debug { get; set; }

	[ConVar( "net_hide_address", ConVarFlags.Protected )]
	internal static bool HideAddress { get; set; } = true;

	[ConVar( "net_game_server_token", ConVarFlags.Protected )]
	internal static string GameServerToken { get; set; } = string.Empty;

	[ConVar( "net_interp_time", ConVarFlags.Protected, Help = "Interpolation time in seconds" )]
	internal static float InterpolationTime { get; set; } = 0.1f;

	[ConVar( "net_fakepacketloss", ConVarFlags.Protected | ConVarFlags.Cheat, Help = "Simulate packet loss in %" )]
	internal static float FakePacketLoss { get; set; } = 0;

	[ConVar( "net_fakelag", ConVarFlags.Protected | ConVarFlags.Cheat, Help = "Simulate latency in ms" )]
	internal static int FakeLag { get; set; } = 0;

	[ConVar( "net_query_port", ConVarFlags.Protected )]
	internal static int QueryPort { get; set; } = 27016;

	[ConCmd( "hostname", ConVarFlags.Protected | ConVarFlags.Admin )]
	private static void SetHostname( string name )
	{
		ServerName = name;
	}

	[ConVar( "port", ConVarFlags.Protected )]
	internal static int Port { get; set; } = 27015;

	[ConCmd( "kick", ConVarFlags.Protected )]
	private static void Kick( string id, string reason = "" )
	{
		if ( !IsHost )
		{
			Log.Warning( "You need to be the host to kick other players!" );
			return;
		}

		var connection = Connection.All.FirstOrDefault( c => c.SteamId.ToString() == id || c.DisplayName.Contains( id ) );

		if ( connection is null )
		{
			Log.Warning( "Unable to find a matching connection with that id or name!" );
			return;
		}

		connection.Kick( reason );
	}

	/// <summary>Get the latest host stats such as bandwidth used and the current frame rate.</summary>
	public static HostStats HostStats => System?.HostStats ?? default;

	//Wire-level network stats for this machine, aggregated across all non-local
	//connections. Post-compression, post-framing bytes as reported by the transport layer.
	internal static ConnectionStats LocalStats { get; private set; }

	/// <summary>True if we can be considered the host of this session. Either we're not connected to a server, or we are host of a server.</summary>
	public static bool IsHost => System is null || System.IsHost;

	/// <summary>True if we're currently connected to a server, and we are not the host</summary>
	public static bool IsClient => System is not null && System.IsClient;

	/// <summary>True if we're currently connecting to the server</summary>
	public static bool IsConnecting => System?.IsConnecting ?? false;

	/// <summary>True if we're currently connecting to the server</summary>
	public static bool IsActive => System is not null;

	//True if we're currently disconnecting from the server
	internal static bool IsDisconnecting => System is not null && System.IsDisconnecting;

	/// <summary>The connection of the current network host.</summary>
	[Obsolete( "Moved to Connection.Host" )]
	public static Connection HostConnection => Connection.Host;

	//Whether the host is busy right now. This can be used to determine if the host can be changed.
	internal static bool IsHostBusy
	{
		get
		{
			return System?.IsHostBusy ?? true;
		}
	}

	/// <summary>A list of connections that are currently on this server. If you're not on a server this will return only one connection (Connection.Local). Some games restrict the connection list - in which case you will get an empty list.</summary>
	[Obsolete( "Moved to Connection.All" )]
	public static IReadOnlyList<Connection> Connections => Connection.All;

	//Our UDP is the way in from another machine. Off, a game can still be hosted and played here, but nobody can join it by its address.
	/// <summary>Whether a server also listens on our own UDP.</summary>
	[ConVar( "net_udp" )]
	public static bool UdpEnabled { get; set; } = true;

	//Open our own listen socket, if we are meant to and if we can. A port that is already taken is an ordinary thing here - a second copy of the game
	//on the same machine is how anybody tests one - and it is not a reason to refuse to host. What is lost is the ability to be joined by address.
	private static LnlSocket OpenUdpSocket( LobbyConfig config )
	{
		if ( !UdpEnabled || Application.OnWeb ) return null;

		try
		{
			return new LnlSocket( Port, config );
		}
		catch ( Exception e )
		{
			Log.Warning( $"Couldn't listen on udp {Port}: {e.Message}" );
			return null;
		}
	}

	//Open a listen socket through every provider that is there to be used, for a lobby that is being hosted. A provider that has nothing
	//to offer here - a platform that is not running - is not asked, and one that fails is a warning: the game is still hosted without it.
	private static void AddProviderSockets( NetworkSystem net, LobbyConfig config )
	{
		foreach ( var provider in NetworkProviders.All )
		{
			if ( !provider.IsAvailable || !provider.HostsByDefault ) continue;

			try
			{
				net.AddSocket( new ProviderSocket( provider, provider.Listen( config ) ) );
			}
			catch ( Exception e )
			{
				Log.Warning( $"Couldn't host through {provider.Title}: {e.Message}" );
			}
		}
	}

	/// <summary>Say what the player is doing where their friends can see it: a status, the map, whatever a game wants shown. On Steam it is rich presence; with no platform it does nothing, so a game can call it everywhere. Pass null to remove a key. <c>status</c> is the line a friends list shows.</summary>
	public static void SetRichPresence( string key, string value ) => NetworkProviders.SetRichPresence( key, value );

	/// <summary>Remove everything set with <see cref="SetRichPresence"/>.</summary>
	public static void ClearRichPresence() => NetworkProviders.ClearRichPresence();

	/// <summary>The lobbies the providers can see, from every one that is available or from the one named. Empty in a build with none - a game for the web or itch.io asks and gets nothing, and joins by address or code.</summary>
	public static async Task<List<LobbyInformation>> QueryLobbies( string provider = null, IReadOnlyDictionary<string, string> filters = null )
	{
		var found = new List<LobbyInformation>();
		filters ??= new Dictionary<string, string>();

		var relay = ProjectSettings.Networking?.RelayAddress;
		if ( !Application.OnWeb && !string.IsNullOrWhiteSpace( relay ) && ( provider is null || provider.Equals( "relay", StringComparison.OrdinalIgnoreCase ) ) )
		{
			try
			{
				found.AddRange( ( await LnlTransport.QueryRelay( relay ) )
					.Where( x => filters.All( f => x.Data is not null && x.Data.TryGetValue( f.Key, out var v ) && v == f.Value ) ) );
			}
			catch ( Exception e )
			{
				Log.Warning( $"Couldn't list lobbies from the relay: {e.Message}" );
			}
		}

		foreach ( var p in NetworkProviders.All )
		{
			if ( !p.IsAvailable ) continue;
			if ( provider is not null && !string.Equals( p.Scheme, provider, StringComparison.OrdinalIgnoreCase ) ) continue;

			try
			{
				found.AddRange( await p.QueryLobbies( filters ) );
			}
			catch ( Exception e )
			{
				Log.Warning( $"Couldn't list lobbies from {p.Title}: {e.Message}" );
			}
		}

		return found;
	}

	internal static void Bootstrap()
	{
		//Nothing to set up ahead of a session: the sockets are opened by whoever hosts or connects, when they do.
	}

	//Internally update the server name without propagating to sockets.
	internal static void UpdateServerName( string name )
	{
		_serverName = name;
	}

	//Reset any static members to their defaults or clear them.
	internal static void Reset()
	{
		MaxPlayers = 0;
		ServerData.Clear();
		LocalStats = default;
	}

	private static int? OldFakePacketLoss { get; set; }
	private static int? OldFakeLag { get; set; }
	private static void UpdateFakeLag()
	{
		//Our UDP has no fake lag or packet loss yet, so there is nothing to slow down.
		return;
	}

	//Aggregate wire stats across all active non-local connections, expose them via LocalStats, and feed them into
	//the performance telemetry pipeline so they appear in activity updates alongside frametime and render stats.
	private static void UpdateLocalStats()
	{
		if ( System is null )
		{
			LocalStats = default;
			return;
		}

		var totalIn = 0f;
		var totalOut = 0f;
		var totalPing = 0;
		var connectionCount = 0;

		//Iterate System.Connections (real wire connections in the NetworkSystem) rather than
		//Connection.All, which allocates and includes mock ConnectionInfo entries with zero stats.
		foreach ( var c in System.Connections )
		{
			//A connection that has not been welcomed yet has nothing worth counting
			if ( c.State < Connection.ChannelState.Welcome )
				continue;

			var s = c.Stats;
			totalIn += s.InBytesPerSecond;
			totalOut += s.OutBytesPerSecond;
			totalPing += s.Ping;
			connectionCount++;
		}

		LocalStats = new ConnectionStats( "local" )
		{
			InBytesPerSecond = totalIn,
			OutBytesPerSecond = totalOut,
			//Average ping across real wire connections; on a client this is just the host ping
			Ping = connectionCount > 0 ? totalPing / connectionCount : 0,
		};
	}

	internal static void PreFrameTick()
	{
		UpdateFakeLag();

		NetworkProviders.Tick();

		try
		{
			System?.Tick();
			System?.SendTableUpdates();
			System?.SendHeartbeat();
			System?.SendHostStats();
			UpdateLocalStats();
		}
		catch ( Exception e )
		{
			Log.Error( e );
		}
	}

	internal static void PostFrameTick()
	{
		try
		{
			System?.SendTableUpdates();
		}
		catch ( Exception e )
		{
			Log.Error( e );
		}
	}

	[Obsolete( "Moved to Connection.Find" )]
	public static Connection FindConnection( Guid id ) => Connection.Find( id );

	//When creating a lobby from the editor, we'll use this override for the lobby privacy.
	internal static LobbyPrivacy EditorLobbyPrivacy { get; set; } = LobbyPrivacy.Private;

	private static CancellationTokenSource lobbyCts;

	/// <summary>Will create a new lobby with the specified <see cref="LobbyConfig"/> to customize the lobby further.</summary>
	public static void CreateLobby( LobbyConfig config )
	{
		//Let's not make a lobby if we're in the editor, and we're not playing a game. Editor tools could call this, and we don't want a lingering lobby.
		if ( Application.IsEditor && !Game.IsPlaying )
			throw new UnauthorizedAccessException( "Unable to create a lobby outside of a game" );

		//If this game can only be hosted on a Dedicated Server, ensure that we are one. We'll
		//allow the Editor to host a lobby as well, though, for testing and development purposes.
		var launchMode = Application.GamePackage?.GetCachedMeta( "LaunchMode", "default" ).ToLower();

		if ( launchMode == "dedicatedserveronly" && !Application.IsDedicatedServer && !Application.IsEditor )
			throw new UnauthorizedAccessException( "This game can only be hosted on a Dedicated Server" );

		if ( IsActive )
			return;

		lobbyCts?.Cancel();
		lobbyCts = new();

		//Did the menu want to override the lobby's max players?
		if ( LaunchArguments.MaxPlayers > 0 )
		{
			config.MaxPlayers = LaunchArguments.MaxPlayers;
		}

		//Did the menu want to override the lobby's name?
		if ( !string.IsNullOrEmpty( LaunchArguments.ServerName ) )
		{
			config.Name = LaunchArguments.ServerName;
		}

		//Did the menu want to override the lobby's privacy mode?
		if ( LaunchArguments.PrivacyOverride is { } privacy )
		{
			config.Privacy = privacy;
		}

		_ = CreateLobbyAsync( config, lobbyCts.Token );
	}

	/// <summary>Will create a new lobby.</summary>
	[Obsolete( "Use CreateLobby( LobbyConfig )" )]
	public static void CreateLobby()
	{
		var config = new LobbyConfig
		{
			MaxPlayers = Application.GamePackage?.GetCachedMeta( "MaxPlayers", 32 ) ?? 32
		};

		CreateLobby( config );
	}

	static bool CreateDedicatedServer( LobbyConfig config, CancellationToken token = default )
	{
		lock ( NetworkThreadLock )
		{
			var net = new NetworkSystem( "server", Engine.IGameInstanceDll.Current.TypeLibrary )
			{
				Config = config
			};

			System = net;
			System.InitializeHost();

			//Our UDP, on the server's port: the way in from other machines.
			var udp = OpenUdpSocket( config );
			if ( udp is not null ) System.AddSocket( udp );

			AddProviderSockets( System, config );

			if ( AllowLocal )
			{
				System.AddSocket( new TcpSocket( "127.0.0.1", 55333 ) );
			}

			return !token.IsCancellationRequested;
		}
	}

	static async Task<bool> CreateLobbyAsync( LobbyConfig config, CancellationToken token = default )
	{
		if ( IsActive )
			return false;

		if ( Application.IsEditor )
		{
			config.Privacy = EditorLobbyPrivacy;
		}

		if ( Application.IsDedicatedServer )
		{
			return CreateDedicatedServer( config, token );
		}

		var net = new NetworkSystem( "lobbyhost", Engine.IGameInstanceDll.Current.TypeLibrary )
		{
			Config = config
		};

		lock ( NetworkThreadLock )
		{
			System = net;
			net.InitializeHost();
		}

		if ( Engine.IToolsDll.Current is not null )
		{
			await Engine.IToolsDll.Current.OnInitializeHost();
		}

		if ( token.IsCancellationRequested )
			return false;

		//Our UDP, the same way a dedicated server opens it. Nothing lists this game anywhere: it is joined by its address.
		var udp = OpenUdpSocket( config );
		if ( udp is not null ) net.AddSocket( udp );

		//Any platform there is to host through, with the privacy the lobby asked for.
		AddProviderSockets( net, config );

		//If runnning in editor, we create a named socket that we can join locally
		if ( Application.IsEditor || Application.IsStandalone )
		{
			net.AddSocket( new TcpSocket( "127.0.0.1", 55333 ) );
		}

		if ( !net.Sockets.Any() )
		{
			Log.Warning( "Could not host: there is nothing to listen on." );

			Disconnect();
			return false;
		}

		return true;
	}

	/// <summary>Disconnect from current multiplayer session.</summary>
	public static void Disconnect()
	{
		lobbyCts?.Cancel();
		lobbyCts = null;

		if ( System is null ) return;

		lock ( NetworkThreadLock )
		{
			//Send any remaining messages
			System.ProcessMessagesInThread();

			Log.Info( $"Disconnected from {System}" );

			System.Disconnect();
			System = null;
		}
	}

	internal static IDisposable DisconnectScope()
	{
		if ( System is null ) return default;

		System.IsDisconnecting = true;

		return new DisposeAction( () =>
		{
			System?.IsDisconnecting = false;
			Disconnect();
		} );
	}

	/// <summary>Will try to determine the right method for connection, and then try to connect.</summary>
	public static void Connect( string target )
	{
		_ = TryConnect( target );
	}

	internal static async Task<bool> TryConnect( string target, int retries = 30, CancellationToken token = default, Action<string> onFailure = null )
	{
		token.ThrowIfCancellationRequested();
		Disconnect();

		if ( string.IsNullOrWhiteSpace( target ) )
		{
			Log.Warning( "Couldn't connect - target is null!" );
			return false;
		}

		Log.Info( $"Connect to '{target}'" );
		Assert.IsNull( System );

		LoadingScreen.IsVisible = true;
		LoadingScreen.Media = null;
		LoadingScreen.Title = "Connecting";

		token.ThrowIfCancellationRequested();

		var count = 0;
		while ( count < retries )
		{
			lock ( NetworkThreadLock )
			{
				if ( target == "local" )
				{
					Log.Info( $"Connecting to local client.." );

					System = new( "localclient", IGameInstanceDll.Current.TypeLibrary );
					System.Connect( new TcpChannel( "127.0.0.1", 55333 ) );
				}
				else if ( target.StartsWith( "relay://", StringComparison.OrdinalIgnoreCase ) )
				{
					var relay = ProjectSettings.Networking?.RelayAddress;
					if ( string.IsNullOrWhiteSpace( relay ) )
					{
						Log.Warning( "Couldn't connect - this project names no relay (Networking settings, Relay Address)" );
						return false;
					}

					System = new( "client", IGameInstanceDll.Current.TypeLibrary );
					System.Connect( LnlTransport.ConnectViaRelay( target[8..], relay ) );
				}
				else if ( NetworkProviders.TryResolve( target, out var provider, out var rest ) )
				{
					Log.Info( $"Connecting through {provider.Title} to {rest}.." );

					System = new( "client", IGameInstanceDll.Current.TypeLibrary );
					System.Connect( new ProviderChannel( provider.Connect( rest ), isHost: true ) );
				}
				else
				{
					//host[:port][#fingerprint] over our UDP; udp:// in front says the same
					Log.Info( $"Connecting to {target}.." );
					System = new( "client", IGameInstanceDll.Current.TypeLibrary );
					System.Connect( LnlTransport.Connect( target ) );
				}

				LastConnectionString = target;
			}

			var attemptSystem = System;
			var success = await AwaitSuccessfulConnection( token );
			if ( success ) return true;

			if ( attemptSystem?.FailureReason is { } reason )
			{
				onFailure?.Invoke( reason );
				return false;
			}

			//An older attempt must not tear down a newer one.
			if ( !ReferenceEquals( System, attemptSystem ) )
				return false;

			Log.Info( $"Couldn't connect, retrying ({count}/{retries})" );
			count++;

			Disconnect();
		}

		ReportConnectionFailure( $"Could not establish a connection to {target} after {retries} attempts. No initial game handshake was received.", onFailure );
		return false;
	}

	internal const double InitialConnectionTimeout = 30;

	static async Task<bool> AwaitSuccessfulConnection( CancellationToken token = default, double timeout = 3 )
	{
		var system = System;
		var timer = global::System.Diagnostics.Stopwatch.StartNew();
		while ( timer.Elapsed.TotalSeconds < timeout )
		{
			try
			{
				await Task.Delay( 100, token );
			}
			catch ( OperationCanceledException ) when ( system?.FailureReason is not null )
			{
				//Reporting a real failure also tears down/cancels the connection. Preserve that
				//reason instead of mistaking the resulting cancellation for a user cancel.
				return false;
			}

			if ( system is null || !ReferenceEquals( System, system ) || system.IsDisconnected || system.FailureReason is not null )
				return false;

			if ( Connection.Local?.State > Connection.ChannelState.Unconnected )
				return true;
		}

		return false;
	}

	static void ReportConnectionFailure( string message, Action<string> onFailure )
	{
		onFailure?.Invoke( message );
		//A caller handling the error owns its UI. Still cancel loading and tear down the game.
		IGameInstanceDll.Current.Disconnect( onFailure is null ? message : null );
	}

	//The client has been told to reconnect to the server. Pause while the server restarts, then attempt to reconnect.
	internal static async Task<bool> ClientReconnect( ReconnectMsg data )
	{
		IGameInstanceDll.Current?.CloseGame();

		string address = LastConnectionString;
		if ( string.IsNullOrWhiteSpace( address ) )
		{
			IGameInstanceDll.Current.Disconnect( "Reconnect failed, missing target address." );
			return false;
		}

		Disconnect();

		Log.Info( $"Reconnecting to {address}" );

		LoadingScreen.IsVisible = true;
		LoadingScreen.Media = null;
		LoadingScreen.Title = "Server Restarting";
		await Task.Delay( 4000 ); //pause to allow server to restart

		return await TryConnect( address );
	}

	//Are we currently matchmaking? We want to suppress user-facing join errors in this case, and silently keep trying lobbies until we find one that works.
	internal static bool IsMatchmaking { get; private set; }

	internal static IDisposable MatchmakingScope()
	{
		IsMatchmaking = true;

		return new DisposeAction( () =>
		{
			IsMatchmaking = false;
		} );
	}
}
