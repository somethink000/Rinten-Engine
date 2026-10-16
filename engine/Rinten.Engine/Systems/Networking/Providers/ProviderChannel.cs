namespace Rinten.Network;

//A connection carried by a NetworkProvider. The same class at both ends, as the UDP one is.
internal sealed class ProviderChannel : Connection
{
	private readonly NetworkPeer peer;
	private readonly bool isHost;

	private bool told;

	internal ProviderChannel( NetworkPeer peer, bool isHost )
	{
		this.peer = peer;
		this.isHost = isHost;
	}

	internal NetworkPeer Peer => peer;

	public override string Address => peer.Address ?? "provider";

	public override bool IsHost => isHost;

	public override float Latency => peer.Latency;

	public override ConnectionStats Stats => peer.Stats;

	internal override void InternalSend( byte[] data, NetFlags flags )
	{
		if ( data is null || data.Length == 0 ) return;

		peer.Send( data, !( ( flags & NetFlags.Unreliable ) == NetFlags.Unreliable ) );
	}

	internal override void InternalRecv( NetworkSystem.MessageHandler handler )
	{
		while ( peer.TryReceive( out var message ) )
		{
			OnRawPacketReceived( message, handler );
		}

		//A client has nobody above it to tell it the server went: the socket's own check is for somebody who connected to us. Said once.
		if ( isHost && !peer.IsConnected && !told )
		{
			told = true;
			System?.OnServerDisconnection( 0, peer.EndReason ?? "connection lost" );
		}
	}

	internal override void InternalClose( int closeCode, string closeReason )
	{
		told = true;
		peer.Close( closeCode, closeReason );
	}
}
