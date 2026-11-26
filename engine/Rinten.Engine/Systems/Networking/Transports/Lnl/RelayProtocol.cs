using LiteNetLib.Utils;
using System.Collections.Generic;

namespace Rinten.Network;

//What a game and the relay server say to each other; compiled into both, so it uses LiteNetLib and nothing of the engine
internal static class RelayProtocol
{
	public const string Magic = "RNRL";
	public const byte Version = 1;
	public const int DefaultPort = 27020;

	//Longest name, map, key or value the relay keeps, and most data pairs per lobby
	public const int MostText = 128;
	public const int MostData = 32;

	public enum Role : byte
	{
		Host = 1,
		Client = 2,
		Query = 3,
	}

	public enum Op : byte
	{
		//Relay to host: the join code
		Registered = 1,

		//Host to relay: the lobby as it is now
		Update = 2,

		//Relay to host and client: a token to ask for a NAT introduction with
		Punch = 3,

		//Client to relay: give up on a direct path; relay to host: a relayed client arrived (id, key, address)
		Join = 4,

		//Payload; between relay and host it starts with the client's id
		Data = 5,

		//Relay to host: a relayed client went (id)
		Leave = 6,

		//Host to relay: drop a relayed client (id, reason)
		Kick = 7,

		//Relay to a query: the public lobbies of its game
		Lobbies = 8,
	}

	public sealed class Lobby
	{
		public string Code = "";
		public string Name = "";
		public string Map = "";
		public int Members;
		public int MaxMembers;
		public bool Public;
		public Dictionary<string, string> Data = new();
	}

	public static void Write( NetDataWriter w, Lobby lobby )
	{
		w.Put( lobby.Code ?? "", MostText );
		w.Put( lobby.Name ?? "", MostText );
		w.Put( lobby.Map ?? "", MostText );
		w.Put( lobby.Members );
		w.Put( lobby.MaxMembers );
		w.Put( lobby.Public );

		var count = System.Math.Min( lobby.Data.Count, MostData );
		w.Put( (byte)count );

		foreach ( var (key, value) in lobby.Data )
		{
			if ( count-- == 0 ) break;
			w.Put( key ?? "", MostText );
			w.Put( value ?? "", MostText );
		}
	}

	//False for anything short or malformed
	public static bool TryRead( NetDataReader r, out Lobby lobby )
	{
		lobby = new Lobby();

		if ( !r.TryGetString( out lobby.Code ) || !r.TryGetString( out lobby.Name ) || !r.TryGetString( out lobby.Map ) ) return false;
		if ( !r.TryGetInt( out lobby.Members ) || !r.TryGetInt( out lobby.MaxMembers ) || !r.TryGetBool( out lobby.Public ) ) return false;
		if ( !r.TryGetByte( out var count ) || count > MostData ) return false;

		for ( var i = 0; i < count; i++ )
		{
			if ( !r.TryGetString( out var key ) || !r.TryGetString( out var value ) ) return false;
			lobby.Data[Clip( key )] = Clip( value );
		}

		lobby.Code = Clip( lobby.Code );
		lobby.Name = Clip( lobby.Name );
		lobby.Map = Clip( lobby.Map );

		return true;
	}

	public static string Clip( string text ) => text is null ? "" : text.Length > MostText ? text[..MostText] : text;
}
