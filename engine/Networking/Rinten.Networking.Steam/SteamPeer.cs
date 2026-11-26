using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Rinten.Diagnostics;
using Rinten.Network;

namespace Rinten.NetworkModules.Steam;

//A Steam P2P peer; sends queue until the lobby connection to the host exists
internal sealed class SteamPeer : NetworkPeer
{
	//Sends held before the connection exists, and arrivals waiting for the main thread before the sender is taken to be flooding
	private const int MostWaiting = 4096;
	private const int MostReceived = 16384;

	private readonly SteamProvider provider;
	private readonly ConcurrentQueue<byte[]> received = new();
	private readonly List<(byte[] Data, bool Reliable)> waiting = new();
	private readonly object padlock = new();

	private volatile bool ended;
	private string reason;
	private ConnectionStats stats;
	private float latency;

	public uint Connection { get; private set; }

	public ulong Remote { get; private set; }

	/// <summary>The lobby this client entered to find the host, left again with the connection.</summary>
	public ulong Lobby { get; set; }

	/// <summary>The lobby socket this arrived on, if it is a player who joined us.</summary>
	public SteamListener Listener { get; }

	public bool Announced { get; set; }

	public SteamPeer( SteamProvider provider )
	{
		this.provider = provider;
	}

	public SteamPeer( SteamProvider provider, uint connection, ulong remote, SteamListener listener )
	{
		this.provider = provider;
		Connection = connection;
		Remote = remote;
		Listener = listener;
	}

	public override string Address => Remote != 0 ? $"steam:{Remote}" : "steam";

	public override float Latency => latency;

	public override ConnectionStats Stats => stats;

	public override bool IsConnected => !ended || !received.IsEmpty;

	public override string EndReason => reason;

	public override SteamId? RemoteId => Remote != 0 ? new SteamId( Remote ) : null;

	/// <summary>Give a peer that was waiting on a lobby the connection it was waiting for.</summary>
	public void Attach( uint connection, ulong remote )
	{
		List<(byte[] Data, bool Reliable)> flush;

		lock ( padlock )
		{
			Connection = connection;
			Remote = remote;

			flush = new List<(byte[], bool)>( waiting );
			waiting.Clear();
		}

		foreach ( var (data, reliable) in flush )
			SteamApi.Send( connection, data, reliable );
	}

	public override void Send( byte[] data, bool reliable )
	{
		if ( ended || data is null || data.Length == 0 ) return;

		lock ( padlock )
		{
			if ( Connection == 0 )
			{
				if ( waiting.Count < MostWaiting ) waiting.Add( (data, reliable) );
				return;
			}
		}

		SteamApi.Send( Connection, data, reliable );
	}

	public override bool TryReceive( out byte[] data ) => received.TryDequeue( out data );

	/// <summary>A message that arrived by some way other than <see cref="Pump"/>.</summary>
	public void Deliver( byte[] data )
	{
		if ( received.Count >= MostReceived ) Close( 0, "Too many messages" );
		else received.Enqueue( data );
	}

	/// <summary>Collect what Steam has for this connection. Main thread.</summary>
	public void Pump()
	{
		if ( Connection == 0 || ended ) return;

		SteamApi.Receive( Connection, Deliver );
	}

	public void Measure()
	{
		if ( Connection == 0 || ended ) return;

		if ( SteamApi.GetStatus( Connection, out var measured ) )
		{
			stats = measured;
			latency = measured.Ping / 1000.0f;
		}
	}

	/// <summary>It is over, from the other end or the network. Whatever already came is still readable.</summary>
	public void End( string why )
	{
		reason ??= why;
		ended = true;
	}

	public override void Close( int code, string why )
	{
		if ( ended && Connection == 0 ) return;

		End( why );

		provider.Forget( this, code, why );
	}
}
