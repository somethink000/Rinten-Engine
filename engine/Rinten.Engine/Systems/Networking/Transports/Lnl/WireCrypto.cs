using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Rinten.Network;

//A host's or client's key pair for one session: P-256, sent as SubjectPublicKeyInfo
internal sealed class WireKey : IDisposable
{
	readonly ECDiffieHellman pair = ECDiffieHellman.Create( ECCurve.NamedCurves.nistP256 );

	public byte[] Public { get; }

	//SHA-256 of Public, hex; what an invite can pin as host:port#fingerprint
	public string Fingerprint { get; }

	public WireKey()
	{
		Public = pair.PublicKey.ExportSubjectPublicKeyInfo();
		Fingerprint = FingerprintOf( Public );
	}

	public static string FingerprintOf( ReadOnlySpan<byte> publicKey ) => Convert.ToHexStringLower( SHA256.HashData( publicKey ) );

	//Null where the other side's key isn't a P-256 key
	public byte[] Agree( ReadOnlySpan<byte> remotePublic )
	{
		using var remote = ECDiffieHellman.Create();

		try
		{
			remote.ImportSubjectPublicKeyInfo( remotePublic, out var read );
			if ( read != remotePublic.Length || remote.KeySize != 256 ) return null;

			return pair.DeriveRawSecretAgreement( remote.PublicKey );
		}
		catch ( CryptographicException )
		{
			return null;
		}
	}

	public void Dispose() => pair.Dispose();
}

//One connection's sealing: AES-256-GCM, a key per direction and two counter streams. The ordered stream
//arrives exactly in order, so it must be the next counter; the loose one is checked against a replay window.
internal sealed class WireSession : IDisposable
{
	public const byte Plain = 0;
	public const byte Sealed = 1;

	const int CounterSize = 8;
	const int TagSize = 16;

	//Tag byte, counter, GCM tag
	public const int Overhead = 1 + CounterSize + TagSize;

	//Set on ordered counters, so the two streams never share a nonce
	const ulong OrderedBit = 1ul << 63;

	//Loose counters a late packet may still be accepted at
	const int Window = 1024;

	public enum Opened
	{
		Ok,

		//Replayed, too old or out of order: dropped, but nothing says it was forged
		Stale,

		//Fails authentication or isn't a sealed packet
		Forged,
	}

	readonly AesGcm send;
	readonly AesGcm receive;

	ulong sentOrdered;
	ulong sentLoose;
	ulong heardOrdered;
	ulong highest;
	readonly ulong[] seen = new ulong[Window / 64];

	WireSession( ReadOnlySpan<byte> sendKey, ReadOnlySpan<byte> receiveKey )
	{
		send = new AesGcm( sendKey, TagSize );
		receive = new AesGcm( receiveKey, TagSize );
	}

	//Both ends derive the same two keys: from the shared secret, salted by both public keys in client-server order
	public static WireSession Create( byte[] secret, ReadOnlySpan<byte> clientPublic, ReadOnlySpan<byte> serverPublic, bool isServer )
	{
		var salt = new byte[clientPublic.Length + serverPublic.Length];
		clientPublic.CopyTo( salt );
		serverPublic.CopyTo( salt.AsSpan( clientPublic.Length ) );

		var keys = HKDF.DeriveKey( HashAlgorithmName.SHA256, secret, 64, SHA256.HashData( salt ), "rinten wire v1"u8.ToArray() );
		CryptographicOperations.ZeroMemory( secret );

		var toServer = keys.AsSpan( 0, 32 );
		var toClient = keys.AsSpan( 32, 32 );

		var session = isServer ? new WireSession( toClient, toServer ) : new WireSession( toServer, toClient );
		CryptographicOperations.ZeroMemory( keys );

		return session;
	}

	//Into the start of a buffer at least plain.Length + Overhead long; returns the bytes written. Not thread safe:
	//ordered packets must reach the wire in the order they were sealed, so the caller holds one lock over both.
	public int Seal( ReadOnlySpan<byte> plain, Span<byte> packet, bool ordered )
	{
		var length = Overhead + plain.Length;
		var counter = ordered ? ++sentOrdered | OrderedBit : ++sentLoose;

		Span<byte> nonce = stackalloc byte[12];
		BinaryPrimitives.WriteUInt64LittleEndian( nonce, counter );

		packet[0] = Sealed;
		BinaryPrimitives.WriteUInt64LittleEndian( packet[1..], counter );
		send.Encrypt( nonce, plain, packet.Slice( 1 + CounterSize, plain.Length ), packet.Slice( length - TagSize, TagSize ), packet[..( 1 + CounterSize )] );

		return length;
	}

	//Called from the one thread that polls the connection
	public Opened Open( ReadOnlySpan<byte> packet, out byte[] plain )
	{
		plain = null;
		if ( packet.Length < Overhead || packet[0] != Sealed ) return Opened.Forged;

		var counter = BinaryPrimitives.ReadUInt64LittleEndian( packet[1..] );
		var ordered = ( counter & OrderedBit ) != 0;
		var number = counter & ~OrderedBit;

		if ( number == 0 ) return Opened.Forged;
		if ( ordered ? number != heardOrdered + 1 : !Fresh( number ) ) return Opened.Stale;

		Span<byte> nonce = stackalloc byte[12];
		BinaryPrimitives.WriteUInt64LittleEndian( nonce, counter );

		var opened = new byte[packet.Length - Overhead];

		try
		{
			receive.Decrypt( nonce, packet.Slice( 1 + CounterSize, opened.Length ), packet[^TagSize..], opened, packet[..( 1 + CounterSize )] );
		}
		catch ( AuthenticationTagMismatchException )
		{
			return Opened.Forged;
		}

		if ( ordered ) heardOrdered = number;
		else Mark( number );

		plain = opened;
		return Opened.Ok;
	}

	bool Fresh( ulong counter )
	{
		if ( counter > highest ) return true;
		if ( highest - counter >= Window ) return false;

		var bit = counter % Window;
		return ( seen[bit / 64] & ( 1ul << (int)( bit % 64 ) ) ) == 0;
	}

	void Mark( ulong counter )
	{
		if ( counter > highest )
		{
			//Bits for counters the window slid past are cleared, so they read as unseen when reused
			var step = Math.Min( counter - highest, Window );
			for ( ulong i = 1; i <= step; i++ )
			{
				var clear = ( highest + i ) % Window;
				seen[clear / 64] &= ~( 1ul << (int)( clear % 64 ) );
			}

			highest = counter;
		}

		var bit = counter % Window;
		seen[bit / 64] |= 1ul << (int)( bit % 64 );
	}

	public void Dispose()
	{
		send.Dispose();
		receive.Dispose();
	}
}
