using Rinten.Network;
using System.Buffers.Binary;
using System.IO;

namespace Rinten;

public abstract partial class Connection
{
	//Per-connection chunk reassembly buffer, grown on demand to the high-water mark. _chunkBufferLength == -1 signals "no assembly in progress".
	byte[] _chunkBuffer;
	int _chunkBufferLength = -1;
	uint _chunkExpectedIndex;
	uint _chunkTotal;

	//Entry point for all incoming transport packets. Handles chunk reassembly transparently;
	//fully assembled (or non-chunked) payloads are decoded and dispatched to handler.
	internal void OnRawPacketReceived( ReadOnlySpan<byte> rawPacket, NetworkSystem.MessageHandler handler )
	{
		try
		{
			if ( rawPacket.Length < 1 ) return;

			if ( rawPacket[0] == FlagChunk )
			{
				AssembleChunk( rawPacket, handler );
				return;
			}

			DeliverDecoded( rawPacket, handler );
		}
		catch ( Exception e )
		{
			//Drop the bad packet and keep draining. A throw here would abort the whole drain loop
			//and back up the incoming channel, starving every client until the server restarts.
			Log.Warning( e, $"Dropping malformed packet ({rawPacket.Length}b) from {this}" );
		}
	}

	private void AssembleChunk( ReadOnlySpan<byte> rawPacket, NetworkSystem.MessageHandler handler )
	{
		try
		{
			AssembleChunkInternal( rawPacket, handler );
		}
		catch
		{
			//Abandon any partial assembly so the next chunk can't extend a buffer with a gap in it. Reassembly only restarts on the next index==0 packet.
			_chunkBufferLength = -1;
			throw;
		}
	}

	private void AssembleChunkInternal( ReadOnlySpan<byte> rawPacket, NetworkSystem.MessageHandler handler )
	{
		if ( rawPacket.Length < 9 ) throw new InvalidDataException( "Chunk packet too short" );

		var index = BinaryPrimitives.ReadUInt32LittleEndian( rawPacket.Slice( 1 ) );
		var total = BinaryPrimitives.ReadUInt32LittleEndian( rawPacket.Slice( 5 ) );
		var chunkData = rawPacket.Slice( 9 );

		if ( index + 1 > total ) throw new InvalidDataException( $"chunkIndex {index} >= total {total}" );
		if ( total <= 1 ) throw new InvalidDataException( "Chunk total must be > 1" );
		if ( total > 1024 ) throw new InvalidDataException( $"Chunk total {total} exceeds 1024 limit" );
		if ( !IsHost && (long)total * MaxChunkSize > MostFromClient ) throw new InvalidDataException( $"Chunk total {total} is more than a client may send" );

		if ( index == 0 )
		{
			//Room for the first few chunks; it grows as they arrive, so a claimed total costs nothing until sent
			var start = (int)Math.Min( total, 4 ) * MaxChunkSize;
			if ( _chunkBuffer == null || _chunkBuffer.Length < start )
				_chunkBuffer = GC.AllocateUninitializedArray<byte>( start );
			_chunkBufferLength = 0;
			_chunkExpectedIndex = 0;
			_chunkTotal = total;
		}

		//This can happen when leaving a lobby (usually during connect) and then rejoining:
		//packets from the previous connection arrive after the new one has started.
		if ( _chunkBufferLength < 0 )
			throw new InvalidDataException( $"Received chunk {index + 1} of {total} with no assembly in progress for {this}" );

		//Chunks must arrive in strict sequential order. Out-of-order or duplicate chunks would silently corrupt the reassembled payload.
		if ( index != _chunkExpectedIndex )
			throw new InvalidDataException( $"Expected chunk {_chunkExpectedIndex} but received {index} of {total} from {this}" );

		_chunkExpectedIndex++;

		if ( chunkData.Length > MaxChunkSize )
			throw new InvalidDataException( $"Chunk payload {chunkData.Length}b exceeds MaxChunkSize ({MaxChunkSize}b) from {this}" );

		if ( total != _chunkTotal )
			throw new InvalidDataException( $"Chunk total changed from {_chunkTotal} to {total} from {this}" );

		if ( _chunkBufferLength + chunkData.Length > _chunkBuffer.Length )
			Array.Resize( ref _chunkBuffer, (int)Math.Min( (long)_chunkBuffer.Length * 2, (long)total * MaxChunkSize ) );

		chunkData.CopyTo( _chunkBuffer.AsSpan( _chunkBufferLength ) );
		_chunkBufferLength += chunkData.Length;

		if ( index + 1 < total ) return; //Not the final chunk yet.

		var assembledLength = _chunkBufferLength;
		_chunkBufferLength = -1;
		//_chunkBuffer is intentionally kept, it will be reused for the next chunked message.

		DeliverDecoded( _chunkBuffer.AsSpan( 0, assembledLength ), handler );
	}

	private void DeliverDecoded( ReadOnlySpan<byte> encoded, NetworkSystem.MessageHandler handler )
	{
		var payload = Decode( encoded, IsHost ? MaxDecompressedByteSize : MostFromClient );
		using var stream = ByteStream.CreateReader( payload );
		handler( new NetworkSystem.NetworkMessage { Source = this, Data = stream } );
		MessagesRecieved++;
	}
}
