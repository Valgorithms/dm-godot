# Godot presentation client

This prototype consumes authoritative world snapshots over a versioned JSON-lines
TCP protocol. It does not connect to DreamDaemon, load DM code itself, or implement
BYOND appearance rendering. Tiles and players currently use procedural markers.

Requires Godot 4.6 or newer, standard build; no .NET Godot build is needed.

```sh
godot --path client -- --host 127.0.0.1 --port 25566 --name Explorer
godot --headless --path client -- --port 25566 --name Smoke --smoke-test
godot --headless --path client --script res://tests/protocol_test.gd
python3 scripts/check_end_to_end.py
```

Arrow keys or WASD request cardinal movement. BYOND coordinates increase upward
on the y axis. The client does not predict or apply movement locally: it redraws
only the server's returned state. The smoke test chooses an adjacent passable
tile, requests movement, and requires a returned snapshot confirming the target.
It fails within 15 seconds if connection, handshake, world loading, or movement
does not complete.

The host explicitly labels its movement mode `diagnostic-grid`: player markers
are transport fixtures, not DM mob instances, and do not execute DM movement
callbacks. `--smoke-min-players 2` waits until another participant is visible
before requesting a move. The end-to-end script starts a real host and two real
Godot clients and requires both success records to identify both participants.

Protocol version 1 requires a `welcome` before snapshots. Frames are UTF-8 JSON
terminated by a newline and limited to 4 MiB. Coordinates are positive, one-based
integers. Maps larger than 8192 × 8192 are rejected by this prototype client.

The stream is intentionally unencrypted and unauthenticated for local fixtures.
Do not expose the prototype server publicly.
