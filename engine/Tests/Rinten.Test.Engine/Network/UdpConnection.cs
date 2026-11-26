using Rinten.Diagnostics;
using Rinten.Engine;
using Rinten.Internal;
using Rinten.Network;
using System;

namespace NetworkTests;

//Full game protocol (handshake, 1.5MB string table) over the LiteNetLib transport, mirroring the TCP test
[TestClass]
public class UdpConnectionTest
{
	//Somewhere unlikely to be somebody else's.
	private const int Port = 55344;

	TypeLibrary tl;

	[TestInitialize]
	public void TestInitialize()
	{
		Logging.Enabled = true;
		Project.Clear();

		var dir = $"{Environment.CurrentDirectory}/.rinten/test_download_cache/udp";
		AssetDownloadCache.Initialize( dir );

		tl = new TypeLibrary();
		tl.AddAssembly( typeof( Bootstrap ).Assembly, false );
		tl.AddAssembly( GetType().Assembly, true );
	}

	[TestCleanup]
	public void TestCleanup()
	{
		Project.Clear();
	}

	private static Rinten.Network.StringTable InstallDataTables( NetworkSystem system )
	{
		Rinten.Network.StringTable table = new( "Assembly", true );

		if ( system.IsHost )
		{
			table.Set( "TestAssembly", new byte[883712] );
			table.Set( "AnotherAssembly", new byte[345600] );
			table.Set( "MoreAssembly", new byte[376346] );
			table.Set( "WhatNotAnotherOne", new byte[153019] );
		}

		system.InstallTable( table );
		return table;
	}

	[TestMethod]
	[DoNotParallelize]
	public async Task Udp_ServerClient()
	{
		var server = new NetworkSystem( "server", tl );
		server.InitializeHost();
		server.AddSocket( new LnlSocket( Port ) );

		var serverTable = InstallDataTables( server );
		Assert.AreEqual( 4, serverTable.Entries.Count );

		var client = new NetworkSystem( "client", tl );
		client.Connect( LnlTransport.Connect( $"udp://127.0.0.1:{Port}" ) );

		var clientTable = InstallDataTables( client );
		Assert.AreEqual( 0, clientTable.Entries.Count );

		Assert.IsTrue( client.IsClient );

		Connection.Local.State = Connection.ChannelState.Unconnected;

		for ( int i = 0; i < 500; i++ )
		{
			server.Tick();
			client.Tick();
			await Task.Delay( 20 );

			if ( client.Connection.State == Connection.ChannelState.Connected )
				break;
		}

		//As with TCP: the handshake parks at LoadingServerInformation, because finishing it wants a game
		//loop this harness does not have. What is being tested is that it got that far over our own wire.
		Assert.AreNotEqual( Connection.ChannelState.Unconnected, client.Connection.State, "Client handshake never progressed" );
		var stateAfterHandshake = client.Connection.State;

		//A megabyte and a half, in packets of twelve hundred bytes: every one of them numbered, acknowledged and put back in order.
		Assert.AreEqual( 4, clientTable.Entries.Count );

		//And it stays up while nothing is being said, which is what the pings and the timeout are for.
		for ( int i = 0; i < 100; i++ )
		{
			server.Tick();
			client.Tick();
			await Task.Delay( 20 );
		}

		Assert.AreEqual( stateAfterHandshake, client.Connection.State, "Client connection state changed while idling on heartbeats" );

		client.Disconnect();
		server.Disconnect();
	}
}
