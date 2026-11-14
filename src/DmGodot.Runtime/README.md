# DM runtime bootstrap

This project executes a deliberately small subset of real OpenDream bytecode.
It is independent of Godot and has no NuGet dependencies. The opcode wire-format
sources are linked from the pinned MIT-licensed OpenDream checkout; the compiler
revision and opcode hash are checked separately by the build tooling and loader.

## Current behavior

- Loads the pinned compiler's JSON, inherited constant type variables, globals,
  static DMM turf grids, and mapped entity coordinates.
- Retains each static map turf and mapped movable as a DM object; unoverridden area
  prototypes of the same type share an area object. `MapCells` exposes these identities.
  DM reads of `loc` and coordinates use retained metadata, and instance methods can
  retain ordinary user-variable state. Diagnostic records remain separate snapshots.
- Executes `world.New()` and global/instance procedure calls using explicit call
  frames. Numeric values use `float`, matching the currently targeted DM model.
- Allocates ordinary datums with inherited type initialization and `New()` on explicit
  construction continuations. Initializers run once through their inherited super chain;
  constructor results are discarded and creation returns the allocated object. Normal
  sleeping constructors suspend their caller; `waitfor=0` constructors detach at yield.
  Direct and type-reference creation support positional arguments.
- Dispatches positional instance-method calls against the receiver\'s actual type,
  preserving receiver `src`, inherited `usr`, overrides/super calls and cooperative
  suspension. Missing/null/non-object/native calls fail explicitly. Dynamic `call()`,
  named arguments, `arglist` and safe-navigation instructions remain outside this slice.
- Implements nested `sleep()`, `spawn()` with copied local bindings and shared
  list/object references, and `set waitfor = 0` detachment on actual suspension.
- Provides deterministic virtual decisecond time; equal-time continuations are
  FIFO. `AdvanceBy(0)` drains currently runnable work. A per-call instruction
  budget detects non-yielding/infinite zero-delay loops. Call-frame and immediate
  spawn depth guards prevent recursive programs from exhausting the host stack.
- Supports basic arithmetic, comparisons, local/global assignment, ordinary
  list construction/indexing, simple value interpolation, and world output.
- Validates opcode-table version, known opcodes, operand lengths, type cycles,
  jump boundaries, and required nested JSON collections, including explicit nulls. Unsupported executed operations include original DM
  file/line and byte offset in `CompatibilityException`.

Use `CompiledProject.Load(path)`, `new DmRuntime(project)`, `Boot()`, and
`AdvanceBy(deciseconds)`. `Logs`, `Tiles`, and `MapEntities` expose immutable
record snapshots through read-only list interfaces. `InvokeGlobal()` is a
fixture/testing entry point, not a complete BYOND verb dispatch implementation.

## Explicit boundaries

This is **not a complete BYOND world runtime**. Static map objects now retain identity;
the host's initial avatar/movement protocol does not execute DM Login, Move,
Enter/Exit, or collision callbacks. Static map New() overrides, map/world type
initializers, sparse/overlapping maps, and map entity/area overrides are rejected
rather than silently ignored. Areas are only checked for unsupported lifecycle
code. Static loc links are readable (movable to turf, turf to area, area to null).
Area coordinates use the lowest turf by z, y, then x. Native atom/appearance, movable and mob fields beyond loc/coordinates/type metadata
remain explicit errors, including contents, color, bounds and connection/vision fields.
This avoids exposing unnormalized compiler defaults as runtime behavior. Mutation of
those fields remains rejected so retained objects cannot silently diverge from snapshots.
Direct mapped-object output also fails until native name/stringification is implemented.
Ordinary user-declared variables remain readable and writable.
Full containment/movement, native managed object creation,
GC/deletion, associative-list construction, dynamic calls, native interfaces,
files, database APIs, persistence, browser UI, resources/rendering, and most
built-ins still need implementation. Constant tagged resources retain paths;
this runtime does not load their image bytes. Unimplemented runtime-managed world
fields, including `tick_usage`, `contents`, and `realtime`, raise source-located
errors instead of returning compiler placeholder values. Reads of implemented
`time`/map dimensions and a narrow list of static world settings remain supported.
Integer modulo checks its divisor after coercion; zero and out-of-range cases
produce DM diagnostics rather than host arithmetic exceptions.

Scheduling regressions encode the intended compatibility contract; they are
not differential BYOND conformance results. Tick-lag quantization, real-time
budgets, background/instant flags, and event-specific sleeping rules need
comparison with a pinned BYOND reference. Numeric/string edge cases and list
operations beyond the tested subset remain compatibility work. Unsupported
optimized opcodes fail explicitly; the initial build disables upstream compiler
optimizations by default.

## Regression tests

From the repository root:

```sh
python3 scripts/compile.py fixtures/smoke/smoke.dme --output build/smoke.json
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/semantics.dme --output build/semantics.json
python3 scripts/compile.py tests/DmGodot.Runtime.Tests/Fixtures/semantics.dme --output build/semantics-optimized.json --optimize
dotnet run --project tests/DmGodot.Runtime.Tests -- build/smoke.json build/semantics.json build/semantics-optimized.json
```

Tests exercise code emitted by the real compiler, scheduler interactions, map
coordinates, source diagnostics, malformed artifacts, and bounded execution.

Empty methods owned by ordinary user datum types return null. The pinned compiler
JSON has no native flag, so empty built-in-owner and global procedures remain
explicit compatibility errors (apart from the existing New/Del defaults). This
uses the pinned native registry's absence of custom ordinary-datum native owners.
