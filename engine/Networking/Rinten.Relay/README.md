# Rinten Relay

A small server that lets players behind home routers play each other without forwarding ports.

- **Join codes.** A hosting game registers and gets a six-letter code. Players join with `Networking.Connect( "relay://CODE" )`.
- **NAT punch-through.** The relay introduces host and player, and they connect directly when their routers allow it.
- **Relaying.** If they can't connect directly within 3 seconds, traffic goes through the relay. The game encrypts it end to end, so the relay never sees it in the clear.
- **Lobby list.** `Networking.QueryLobbies()` lists public games of the same game ident.

Without a relay, games are still joined by address (`ip:port`). The relay is only needed for codes, players behind NAT and the lobby list.

## Run it

```bash
dotnet publish engine/Networking/Rinten.Relay -c Release -o relay
dotnet relay/rinten-relay.dll --port 27020
```

Open UDP port 27020 on the machine. A small VPS is enough: the relay only carries the games that couldn't connect directly.

Options:

| Option | Default | Meaning |
|---|---|---|
| `--port` | 27020 | UDP port |
| `--max-hosts` | 2000 | Games registered at once |
| `--client-rate` | 262144 | Bytes per second a relayed player may send |
| `--host-rate` | 4194304 | Bytes per second a host may send to its relayed players |

## Point a game at it

In the project's Networking settings, set **Relay Address** to `your.server:27020`.

## Testing on one machine

Punch-through always succeeds on one machine. Start the joining game with `+net_relay_only 1` to test the relayed path.
