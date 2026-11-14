# Architecture

## Source and compiler boundary

The first frontend is the pinned upstream OpenDream DMCompiler. It compiles DM
and maps into OpenDream JSON containing bytecode, types, procs and map records.
Our runtime consumes that format directly for the implemented subset. This keeps
the first implementation grounded in a real DM compiler and permits later
transpilation without changing developer source files.

OpenDream's compiler output is an internal versioned dependency, not a permanent
public interchange standard. Pin both the compiler revision and opcode metadata;
reject incompatible artifacts. Errors must identify the offending proc/opcode
and source position whenever the compiler provides it.

## Simulation boundary

`DmGodot.Runtime` contains the artifact reader, type/value model, execution frames,
cooperative scheduler, and world data. It has no Godot or network dependency.
Unsupported semantics raise compatibility errors. A method's presence in the
compiler artifact does not establish that the runtime implements it.

`DmGodot.Host` drives the runtime from a monotonic clock. Network readers enqueue
commands; game state changes are applied on the host's single logical loop. The
first host exposes a deliberately named diagnostic-grid mode. Its player markers
are transport test entities, not fully implemented DM mobs. It must not be used
to claim that DM Login/Move/Entered/Exited hooks already work.

## Presentation boundary

The Godot client displays snapshots and sends input intent. The server owns
positions. Diagnostic colored tiles are temporary presentation for imported DMM
data; they are not DMI, appearance, or SS13 HUD compatibility. The next renderer
will consume a separate appearance model and use batching/low-level rendering
where appropriate instead of mirroring every datum into the scene tree.

## Protocol v1 (diagnostic only)

TCP carries one UTF-8 JSON object per line. The host binds loopback by default.
Coordinates are integer, one-based, and DM-oriented (north increases y).

| Direction | Type | Fields |
| --- | --- | --- |
| Client to server | `hello` | `name` |
| Server to client | `welcome` | `id` (string), `protocol` (1) |
| Server to client | `snapshot` | `width`, `height`, `revision`, `tiles`, `players` |
| Client to server | `move` | `dx`, `dy` (one cardinal step) |
| Server to client | `log` | `text` |
| Server to client | `error` | `text` |

Tile fields: `x`, `y`, `z`, `density`, `type`. Player fields: `id`, `name`, `x`,
`y`, `z`. The diagnostic host displays z=1. Movement must remain on an existing
non-dense tile, and may not overlap another connected player. Unsupported
commands are errors, never DM proc calls. This protocol is not exposed as a
production service or authenticated identity system.

## Compatibility strategy

Use three kinds of evidence: compiler results on immutable targets, small
language/engine conformance fixtures, and actual connected gameplay/save tests.
Maintain separate statuses. Preserve existing code semantics by default, and
make new behavior explicit and opt-in. Existing DMB binaries, native DLL ABIs,
BYOND service identity and old save formats must not be conflated with DM source
compatibility.
