# DM-Godot

An experimental DM-compatible runtime and Godot client. The goal is to compile
and play unchanged Civ13 and SS13 projects through a new engine. **That goal is
not implemented yet.** The first milestone executes a small real-DM fixture and
connects Godot clients to a diagnostic map.

The accepted [plan](docs/PLAN.md), [architecture](docs/ARCHITECTURE.md), and
[tracking issue draft](docs/TRACKING_ISSUE.md) define the work. The
[compatibility baseline](docs/compatibility-baseline.md) pins Civ13 and /tg
targets and records observed dependencies without claiming gameplay support.
The complete pinned Civ13 project compiles through the frontend without source
edits, with 137 warnings. Its gameplay is not supported by the new runtime yet.

## Quick start

Install .NET SDK 10.0.404, Python 3.11+, Git, and a standard Godot 4.6+ build.
This implementation was checked with Godot 4.6.3 and 4.7.2. Godot .NET is not
required: the C# runtime runs in its own server process.

```sh
python3 scripts/bootstrap_opendream.py
python3 scripts/compile.py fixtures/smoke/smoke.dme --output build/smoke.json
dotnet build DmGodot.slnx -c Release -m:1 -p:UseSharedCompilation=false
dotnet src/DmGodot.Host/bin/Release/net10.0/DmGodot.Host.dll \
  --project build/smoke.json --diagnostic-grid --port 25566
```

In another terminal:

```sh
godot --path client -- --port 25566 --name Explorer
```

Use WASD or arrows. Launch another client with a different name to inspect
replicated player markers. The host binds loopback. `--port 0` selects an
available port and prints it; `--duration N` exits after N seconds for tests.

On Windows, use `python`/`py -3` instead of `python3` and put Godot's console
executable on PATH or set the `GODOT` environment variable for the test scripts.
Set `DM_GODOT_DOTNET` to an absolute dotnet path if it is not on PATH.

## What works in this foundation

- The pinned **real OpenDream compiler**, with unmodified DM inputs, source
  revision checks, generated-artifact provenance, DMM and DMI resource retention.
- A deliberately limited bytecode runtime, actual `world.New()`, procedure call
  frames, `sleep`, `spawn`, `waitfor=0`, and source-mapped compatibility errors.
- Static map descriptors, a server-authoritative diagnostic grid, and a Godot
  presentation client using versioned JSON-lines TCP.
- Explicitly bounded execution and validation of malformed compiler artifacts.

## What is still missing

Full DM built-ins and semantics; mob lifecycle and movement callbacks;
atoms/loc/contents; real DMI/appearance rendering; browser UI; native libraries;
authentication; persistence and existing save migration. Diagnostic player
markers are not DM mobs. The UI's colored rectangles are not BYOND appearance
compatibility. See the [runtime limits](src/DmGodot.Runtime/README.md).

## Validation

```sh
python3 scripts/verify.py
```

The verification script builds the pinned compiler, compiles real fixtures,
builds the runtime and host, and runs compiler/runtime/client/host integration
checks. It needs a Godot executable and fails if a required check cannot run.
Results do not represent differential BYOND conformance or full-game acceptance.

The expected smoke trace is `boot`, `sleep-start`, `spawn`, `sleep-end`, `ready`.
Full target acceptance is tracked separately; a clean fixture build does not
mean Civ13 or any SS13 fork is playable.

## Source layout

| Directory | Responsibility |
| --- | --- |
| `src/DmGodot.Runtime` | Artifact reader, VM, scheduler and static map data |
| `src/DmGodot.Host` | Diagnostic server and authoritative transport test grid |
| `client` | Godot presentation client |
| `scripts` | Pinned compiler bootstrap, compilation and verification |
| `fixtures`, `tests` | Valid DM fixtures and regression checks |
| `compatibility` | Immutable full-game targets and dependency baseline |

MIT licensed; see [third-party notices](THIRD_PARTY_NOTICES.md). Upstream source
is fetched into ignored `vendor/`; compiler binaries and generated artifacts are
not committed.
